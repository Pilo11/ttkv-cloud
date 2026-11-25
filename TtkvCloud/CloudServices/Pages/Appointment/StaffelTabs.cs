namespace CloudServices.Pages.Appointment;

/// <summary>Header tabs of a Spielplan in the Staffelleitung.</summary>
public sealed record StaffelTabs(string Key, string Id, string Active)
{
    public const string Settings = "settings";
    public const string Termine = "termine";
}
