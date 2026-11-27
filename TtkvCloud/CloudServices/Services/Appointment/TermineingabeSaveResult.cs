namespace CloudServices.Services.Appointment;

/// <summary>Answer to one auto-saved Termin of the Termineingabe; the page gets it as JSON.</summary>
public sealed class TermineingabeSaveResult
{
    public bool Ok { get; init; }

    /// <summary>Someone else changed this Termin after the page loaded; Value/Stamp are theirs, the input was not saved.</summary>
    public bool Conflict { get; init; }

    public string? Error { get; init; }

    /// <summary>The Termin now stored, as the input shows it ("14.01.2027 19:30"); empty when there is none.</summary>
    public string Value { get; init; } = "";

    /// <summary>The Termin now stored as yyyy-MM-ddTHH:mm; empty when there is none.</summary>
    public string Stamp { get; init; } = "";

    public string Weekday { get; init; } = "";

    public static TermineingabeSaveResult Fail(string error) => new() { Error = error };

    public static TermineingabeSaveResult Current(TermineSlot? slot, bool ok = true, bool conflict = false, string? error = null) => new()
    {
        Ok = ok,
        Conflict = conflict,
        Error = error,
        Value = TermineingabeInput.Display(slot),
        Stamp = TermineingabeInput.Stamp(slot),
        Weekday = TermineingabeInput.Weekday(slot)
    };
}
