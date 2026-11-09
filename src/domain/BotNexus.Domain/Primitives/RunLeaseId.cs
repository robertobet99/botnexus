using Vogen;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// Identifies one run lease: the gateway's own record, written on a session's crash sentinel before a
/// run starts, that it admitted a run for one stored input. Only the gateway mints it, so nothing a caller
/// sends can name, create or alter a lease; recovery trusts a lease because it found it in the store.
/// </summary>
[ValueObject<string>(conversions: Conversions.SystemTextJson)]
public readonly partial struct RunLeaseId
{
    /// <summary>Creates a fresh lease identity.</summary>
    public static RunLeaseId Create() => From(Guid.NewGuid().ToString("N"));

    private static Validation Validate(string value) => string.IsNullOrWhiteSpace(value)
        ? Validation.Invalid("RunLeaseId cannot be null, empty, or whitespace.") : Validation.Ok;

    private static string NormalizeInput(string input) => input is null ? input! : input.Trim();
}
