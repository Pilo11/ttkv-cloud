namespace CloudServices.Services.Appointment;

public sealed class TermineResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public static TermineResult Success() => new() { Ok = true };

    public static TermineResult Fail(string error) => new() { Ok = false, Error = error };
}
