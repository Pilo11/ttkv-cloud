using System.Text.Json.Serialization;

namespace CloudServices.Services.Appointment;

public sealed class TermineTenantDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<TermineClub> Clubs { get; set; } = [];
    /// <summary>First day of the fixed date scope (ISO). Older documents derive it from <see cref="Dates"/>.</summary>
    public string? RangeFrom { get; set; }

    /// <summary>Last day of the fixed date scope (ISO). Days in the scope but not in <see cref="Dates"/> are gesperrt.</summary>
    public string? RangeTo { get; set; }

    /// <summary>Allowed Spieltage (ISO). Everything else in the scope is gesperrt.</summary>
    public List<string> Dates { get; set; } = [];
    public List<TermineMatch> Matches { get; set; } = [];
    public List<TermineSlot> Slots { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TermineClub>? HomeClubs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TermineClub>? GuestClubs { get; set; }
}
