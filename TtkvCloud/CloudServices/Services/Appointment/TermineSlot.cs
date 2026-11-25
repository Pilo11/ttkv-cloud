namespace CloudServices.Services.Appointment;

public sealed class TermineSlot
{
    public string HomeClubId { get; set; } = "";
    public string Date { get; set; } = "";
    public string Time { get; set; } = "";
    public string? GuestClubId { get; set; }
    public string? MatchId { get; set; }
}
