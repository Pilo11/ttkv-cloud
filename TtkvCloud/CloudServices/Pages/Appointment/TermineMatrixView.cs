using CloudServices.Services.Appointment;

namespace CloudServices.Pages.Appointment;

public enum TermineMatrixRowKind
{
    Month,
    Day,
    Blocked
}

public sealed class TermineMatrixRow
{
    public TermineMatrixRowKind Kind { get; init; }

    /// <summary>The day (Day) or first gesperrter day (Blocked).</summary>
    public string Iso { get; init; } = "";

    /// <summary>Last day of a gesperrt block.</summary>
    public string ToIso { get; init; } = "";

    public int Count { get; init; } = 1;

    public string Title { get; init; } = "";
}

/// <summary>
/// Date × home team matrix. Public mode acts for the selected Mannschaft;
/// admin mode (Staffelleitung) can free or delete any Termin.
/// </summary>
public sealed class TermineMatrixView
{
    public TermineMatrixView(TermineTenantDocument tenant, string? selectedClubId, bool isAdmin)
    {
        Tenant = tenant;
        IsAdmin = isAdmin;
        SelectedClubId = isAdmin ? null : selectedClubId;
        HomeColumns = TermineRules.HomeColumnClubs(tenant);
        CanOffer = SelectedClubId is not null && TermineRules.IsHomeClub(tenant, SelectedClubId);
        CanBookAsGuest = SelectedClubId is not null && TermineRules.IsGuestClub(tenant, SelectedClubId);
        Rows = BuildRows(tenant);
    }

    public TermineTenantDocument Tenant { get; }

    public bool IsAdmin { get; }

    public string? SelectedClubId { get; }

    public bool CanOffer { get; }

    public bool CanBookAsGuest { get; }

    public IReadOnlyList<TermineClub> HomeColumns { get; }

    public IReadOnlyList<TermineMatrixRow> Rows { get; }

    public bool IsOwnColumn(string homeClubId) => Same(homeClubId, SelectedClubId);

    /// <summary>The selected Mannschaft still has to book an away match at this home team.</summary>
    public bool HasOpenAwayMatch(string homeClubId) =>
        SelectedClubId is not null
        && !Same(homeClubId, SelectedClubId)
        && TermineRules.FindUnscheduledMatch(Tenant, homeClubId, SelectedClubId) is not null;

    public TermineSlot? Slot(string homeClubId, string date) => TermineRules.FindSlot(Tenant, homeClubId, date);

    public bool IsMine(TermineSlot slot) =>
        SelectedClubId is not null && (Same(slot.HomeClubId, SelectedClubId) || Same(slot.GuestClubId, SelectedClubId));

    public bool CanBookCell(string homeClubId, string date)
    {
        if (SelectedClubId is null || !CanBookAsGuest)
        {
            return false;
        }

        return TermineRules.CanBook(Tenant, homeClubId, date, SelectedClubId).Ok;
    }

    public string? BookBlockReason(string homeClubId, string date)
    {
        if (SelectedClubId is null || !CanBookAsGuest)
        {
            return null;
        }

        var slot = TermineRules.FindSlot(Tenant, homeClubId, date);
        if (slot is null || string.IsNullOrWhiteSpace(slot.Time) || !string.IsNullOrEmpty(slot.GuestClubId))
        {
            return null;
        }

        if (TermineRules.FindUnscheduledMatch(Tenant, homeClubId, SelectedClubId) is not null)
        {
            var result = TermineRules.CanBook(Tenant, homeClubId, date, SelectedClubId);
            return result.Ok ? null : result.Error;
        }

        return null;
    }

    public string ClubName(string? clubId) => TermineRules.ClubName(Tenant, clubId) ?? "";

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<TermineMatrixRow> BuildRows(TermineTenantDocument tenant)
    {
        var rows = new List<TermineMatrixRow>();
        if (!TermineDates.TryGetRange(tenant, out var from, out var to))
        {
            return rows;
        }

        var allowed = tenant.Dates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var month = 0;
        string? blockStart = null;
        string? blockEnd = null;
        var blockCount = 0;

        void FlushBlock()
        {
            if (blockStart is null)
            {
                return;
            }

            rows.Add(new TermineMatrixRow
            {
                Kind = TermineMatrixRowKind.Blocked,
                Iso = blockStart,
                ToIso = blockEnd ?? blockStart,
                Count = blockCount
            });
            blockStart = null;
            blockEnd = null;
            blockCount = 0;
        }

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var iso = TermineDates.ToIso(day);
            var monthKey = day.Year * 100 + day.Month;
            if (monthKey != month)
            {
                FlushBlock();
                month = monthKey;
                rows.Add(new TermineMatrixRow
                {
                    Kind = TermineMatrixRowKind.Month,
                    Iso = iso,
                    Title = TermineDates.FormatMonthYear(iso)
                });
            }

            if (allowed.Contains(iso))
            {
                FlushBlock();
                rows.Add(new TermineMatrixRow { Kind = TermineMatrixRowKind.Day, Iso = iso });
            }
            else
            {
                blockStart ??= iso;
                blockEnd = iso;
                blockCount++;
            }
        }

        FlushBlock();
        return rows;
    }
}
