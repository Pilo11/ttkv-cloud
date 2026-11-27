using CloudServices.Services.Appointment;

namespace CloudServices.Pages.Appointment;

public sealed record TermineingabeRow(
    TermineMatch Match,
    string Home,
    string Guest,
    TermineSlot? Slot,
    bool Mine,
    bool OwnHome,
    bool Editable);

/// <summary>
/// All Begegnungen of a Spielplan in one list, the Termin in the first column. The selected
/// Mannschaft enters the Termine of its home matches (its away matches are the other home
/// team's); the Staffelleitung (isAdmin) edits every one.
/// </summary>
public sealed class TermineingabeView
{
    public TermineingabeView(TermineTenantDocument tenant, string? selectedClubId, bool isAdmin, string saveUrl)
    {
        Tenant = tenant;
        IsAdmin = isAdmin;
        SelectedClubId = isAdmin ? null : selectedClubId;
        SaveUrl = saveUrl;
        Rows = tenant.Matches
            .Select(match =>
            {
                var ownHome = SelectedClubId is not null && Same(match.HomeClubId, SelectedClubId);
                var mine = ownHome || (SelectedClubId is not null && Same(match.GuestClubId, SelectedClubId));
                return new TermineingabeRow(
                    match,
                    TermineRules.ClubName(tenant, match.HomeClubId) ?? "",
                    TermineRules.ClubName(tenant, match.GuestClubId) ?? "",
                    TermineRules.FindSlotForMatch(tenant, match.Id),
                    mine,
                    ownHome,
                    isAdmin || ownHome);
            })
            .ToList();

        if (TermineDates.TryGetRange(tenant, out var from, out var to))
        {
            RangeFrom = TermineDates.ToIso(from);
            RangeTo = TermineDates.ToIso(to);
            BlockedCount = Math.Max(0, to.DayNumber - from.DayNumber + 1 - tenant.Dates.Count);
        }
    }

    public TermineTenantDocument Tenant { get; }

    public bool IsAdmin { get; }

    public string? SelectedClubId { get; }

    public string SelectedClubName => TermineRules.ClubName(Tenant, SelectedClubId) ?? "";

    /// <summary>Handler URL the page posts every changed Termin to.</summary>
    public string SaveUrl { get; }

    public IReadOnlyList<TermineingabeRow> Rows { get; }

    public string? RangeFrom { get; }

    public string? RangeTo { get; }

    public bool ShowNumbers => Tenant.Matches.Any(match => !string.IsNullOrEmpty(match.Location));

    public int BlockedCount { get; }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
