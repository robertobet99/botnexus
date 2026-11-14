using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Domain.Security;

/// <summary>
/// A credential resolved from a secret store: a password, an API token, a connection secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>It cannot leak by accident.</b> <see cref="ToString"/> returns a redacted marker and
/// <c>PrintMembers</c> is overridden, so interpolation, a <c>{Secret}</c> structured-log
/// placeholder, an exception message and a serialiser all render the marker rather than the
/// credential. Reaching the plaintext requires calling <see cref="Reveal"/>, which is a named
/// method precisely so every unwrapping site is greppable — and so an architecture fence can
/// assert that only the intended callers do it.
/// </para>
/// <para>
/// This is deliberately a separate type from <see cref="WebhookSecret"/> rather than a reuse of
/// it. A webhook secret is a token BotNexus generates and therefore gets to constrain to
/// <c>A-Z a-z 0-9 _ -</c>; this holds a credential someone else issued, which may contain any
/// character at all. Widening <see cref="WebhookSecret"/> to accept arbitrary input would remove
/// a validation rule that is load-bearing for webhooks.
/// </para>
/// <para>
/// The value stays in managed memory and is not pinned or zeroed. That is a deliberate limit,
/// not an oversight: this type exists to stop <i>accidental disclosure through ordinary code</i>,
/// which is the failure mode that actually happens. It is not a defence against an attacker who
/// can already read the process's memory.
/// </para>
/// </remarks>
public readonly record struct Secret
{
    /// <summary>
    /// Upper bound on a credential's length. Generous, because certificates and long-lived tokens
    /// are legitimately large, but bounded so a misconfiguration pointing at the wrong file fails
    /// with a clear error instead of loading an arbitrary blob into memory.
    /// </summary>
    public const int MaxLength = 8192;

    /// <summary>Rendered in place of the credential by <see cref="ToString"/>.</summary>
    public const string RedactedMarker = "Secret(redacted)";

    private readonly string? _value;

    private Secret(string value) => _value = value;

    /// <summary>
    /// True when this instance was produced by a validating factory. A <c>default(Secret)</c> is
    /// false, holds nothing, and never matches anything.
    /// </summary>
    public bool HasValue => _value is not null;

    /// <summary>
    /// Number of characters in the credential, or zero for a <c>default</c> instance. Exposed
    /// because diagnostics often want to say a credential was present without revealing it.
    /// </summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// Returns the raw credential. A named method rather than a property so that every site which
    /// unwraps it is greppable, and so it is never picked up implicitly by a serialiser,
    /// structured logging, or string interpolation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance is <c>default</c> and holds nothing.</exception>
    public string Reveal() => _value
        ?? throw new InvalidOperationException("Secret has no value; it was never created through a validating factory.");

    /// <summary>
    /// Attempts to create a credential from raw store output.
    /// </summary>
    /// <param name="value">Candidate credential; may be null.</param>
    /// <param name="secret">The validated credential on success; <c>default</c> on failure.</param>
    /// <returns>True when the value is non-empty and at most <see cref="MaxLength"/> characters.</returns>
    public static bool TryCreate(string? value, out Secret secret)
    {
        secret = default;

        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        secret = new Secret(value);
        return true;
    }

    /// <summary>
    /// Creates a credential from raw store output, throwing when it is empty or over-long.
    /// </summary>
    /// <remarks>
    /// The exception message never contains the value — a validation failure is one of the easier
    /// ways to spill a credential into a log.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is null, empty, or longer than <see cref="MaxLength"/>.</exception>
    public static Secret Create(string? value)
        => TryCreate(value, out var secret)
            ? secret
            : throw new ArgumentException(
                $"Value is not a usable secret (must be non-empty and at most {MaxLength} characters).",
                nameof(value));

    /// <summary>
    /// Constant-time equality. Both sides are SHA-256 hashed first so the fixed-length digest
    /// comparison cannot short-circuit on a length difference — the comparison time is independent
    /// of the length and content of both operands. A <c>default</c> instance never matches.
    /// </summary>
    public bool Equals(Secret other)
    {
        if (_value is null || other._value is null)
            return false;

        Span<byte> mine = stackalloc byte[32];
        Span<byte> theirs = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(_value), mine);
        SHA256.HashData(Encoding.UTF8.GetBytes(other._value), theirs);
        return CryptographicOperations.FixedTimeEquals(mine, theirs);
    }

    /// <summary>
    /// Derived from the SHA-256 digest so it stays consistent with <see cref="Equals"/> and does
    /// not expose the plaintext through a trivially reversible hash.
    /// </summary>
    public override int GetHashCode()
    {
        if (_value is null)
            return 0;

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(_value), digest);
        return BitConverter.ToInt32(digest[..4]);
    }

    /// <summary>
    /// Returns a redacted marker, never the credential. This is what makes disclosure through
    /// interpolation, structured-log placeholders and exception messages impossible.
    /// </summary>
    public override string ToString() => RedactedMarker;

    /// <summary>
    /// Suppresses the compiler-generated record member printing, which would otherwise emit the
    /// backing field through <c>ToString</c>-adjacent paths.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("redacted");
        return true;
    }
}
