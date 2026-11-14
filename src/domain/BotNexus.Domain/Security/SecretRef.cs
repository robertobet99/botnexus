namespace BotNexus.Domain.Security;

/// <summary>
/// A pointer to a credential held in a secret store, in the form <c>scheme:identifier</c> —
/// for example <c>env:PROXMOX_TOKEN</c> or <c>file:~/.botnexus/secrets/proxmox</c>.
/// </summary>
/// <remarks>
/// <para>
/// This type exists so that a configuration field which should hold a <i>reference</i> cannot
/// quietly hold the <i>credential</i> instead. A bare password has no scheme, so it fails to
/// parse, and the failure surfaces at config validation naming the offending key rather than at
/// the first call that tries to use it. Making the wrong state unrepresentable is the same move
/// <see cref="Secret"/> makes for disclosure, and the same one <c>SkillPath</c> makes for
/// containment.
/// </para>
/// <para>
/// Unlike <see cref="Secret"/>, <see cref="ToString"/> here returns the real value — and that
/// asymmetry is the point. A reference is not sensitive; it names a location, and being able to
/// log it is exactly what makes a resolution failure diagnosable. The credential it points at is
/// the thing that must never be printed.
/// </para>
/// <para>
/// Which schemes actually exist is not this type's business. It validates shape only; the
/// resolver owns the set of registered schemes and reports an unknown one. That split keeps the
/// type usable from configuration validation, which has no access to the resolver.
/// </para>
/// </remarks>
public readonly record struct SecretRef
{
    /// <summary>
    /// Bound on the whole reference. A reference is a short pointer; anything longer is far more
    /// likely to be a credential that was pasted into the wrong field.
    /// </summary>
    public const int MaxLength = 512;

    private readonly string? _scheme;
    private readonly string? _identifier;

    private SecretRef(string scheme, string identifier)
    {
        _scheme = scheme;
        _identifier = identifier;
    }

    /// <summary>True when this instance came from a validating factory.</summary>
    public bool HasValue => _scheme is not null;

    /// <summary>
    /// The store to resolve against, lower-cased — <c>env</c>, <c>file</c>, and later
    /// <c>sqlite</c> and <c>keyring</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance is <c>default</c>.</exception>
    public string Scheme => _scheme
        ?? throw new InvalidOperationException("SecretRef has no value; it was never created through a validating factory.");

    /// <summary>
    /// What to look up within that store: a variable name, a file path, a key. Case is preserved,
    /// because environment variables and file paths are case-sensitive on the platforms that
    /// matter here.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance is <c>default</c>.</exception>
    public string Identifier => _identifier
        ?? throw new InvalidOperationException("SecretRef has no value; it was never created through a validating factory.");

    /// <summary>
    /// Attempts to parse a reference, reporting why it failed in terms an operator editing
    /// <c>config.json</c> can act on.
    /// </summary>
    /// <param name="value">Raw configured value; may be null.</param>
    /// <param name="reference">The parsed reference on success; <c>default</c> on failure.</param>
    /// <param name="error">Why it failed, or null on success. Never contains <paramref name="value"/>.</param>
    public static bool TryParse(string? value, out SecretRef reference, out string? error)
    {
        reference = default;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A credential reference is required, in the form scheme:identifier (for example env:MY_TOKEN).";
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            // Deliberately does not echo the value: an over-long value in this field is most
            // likely a credential someone pasted in by mistake, and this message may be logged.
            error = $"A credential reference must be at most {MaxLength} characters. This looks like a secret value rather than a reference to one.";
            return false;
        }

        var separator = trimmed.IndexOf(':');
        if (separator <= 0)
        {
            error = "A credential reference must be scheme:identifier (for example env:MY_TOKEN or file:~/.botnexus/secrets/my-token). "
                  + "Put the credential itself in one of those stores - this field holds a reference to it, never the value.";
            return false;
        }

        var scheme = trimmed[..separator];
        var identifier = trimmed[(separator + 1)..];

        if (!IsWellFormedScheme(scheme))
        {
            error = "The scheme of a credential reference must start with a letter and contain only letters, digits, '+', '.' or '-'. "
                  + "A value with a colon in it is not automatically a reference.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(identifier))
        {
            error = $"The '{scheme}' credential reference names nothing to look up. Expected {scheme}:<identifier>.";
            return false;
        }

        reference = new SecretRef(scheme.ToLowerInvariant(), identifier.Trim());
        return true;
    }

    /// <summary>
    /// Parses a reference, throwing when it is not well formed.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a well-formed reference.</exception>
    public static SecretRef Parse(string? value)
        => TryParse(value, out var reference, out var error)
            ? reference
            : throw new ArgumentException(error, nameof(value));

    /// <summary>
    /// Returns <c>scheme:identifier</c>. Safe to log — see the remarks on this type for why that
    /// is the opposite of <see cref="Secret.ToString"/> and deliberately so.
    /// </summary>
    public override string ToString() => _scheme is null ? "SecretRef(none)" : $"{_scheme}:{_identifier}";

    private static bool IsWellFormedScheme(string scheme)
    {
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]))
            return false;

        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '.' && c != '-')
                return false;
        }

        return true;
    }
}
