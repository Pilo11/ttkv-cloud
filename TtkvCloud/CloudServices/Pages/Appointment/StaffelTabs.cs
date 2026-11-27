namespace CloudServices.Pages.Appointment;

/// <summary>Header tabs of a Spielplan in the Staffelleitung.</summary>
public sealed record StaffelTabs(string Key, string Id, string Active)
{
    public const string Settings = "settings";
    public const string Termine = "termine";
    public const string Eingabe = "eingabe";
}

/// <summary>Public header tabs of a Spielplan: the same Termine as matrix or as list.</summary>
public sealed record PlanViewTabs(string Id, string Active)
{
    public const string Terminplanung = "terminplanung";
    public const string Termineingabe = "termineingabe";
}
