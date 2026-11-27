namespace CloudServices.Services.Appointment;

public static class TermineRules
{
    /// <summary>
    /// Several Termine of one Mannschaft on the same day (offered home slots, booked home or away
    /// matches) start at least this many hours apart; in the Terminplanung and the Termineingabe.
    /// </summary>
    public const int MinHoursBetweenMatches = 4;

    /// <summary>
    /// The home team's slot on a day: the one at <paramref name="time"/>, or without a time the
    /// first of the day (a home team can have several slots per day, at least 4 hours apart).
    /// </summary>
    public static TermineSlot? FindSlot(TermineTenantDocument tenant, string homeClubId, string date, string? time = null) =>
        SlotsAt(tenant, homeClubId, date).FirstOrDefault(slot =>
            string.IsNullOrEmpty(time) || string.Equals(slot.Time, time, StringComparison.Ordinal));

    /// <summary>All slots (open Terminvorschläge and booked matches) of a home team on a day, by time.</summary>
    public static IReadOnlyList<TermineSlot> SlotsAt(TermineTenantDocument tenant, string homeClubId, string date) =>
        tenant.Slots
            .Where(slot =>
                string.Equals(slot.HomeClubId, homeClubId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.Date, date, StringComparison.OrdinalIgnoreCase))
            .OrderBy(slot => slot.Time, StringComparer.Ordinal)
            .ToList();

    public static bool Involves(TermineSlot slot, string clubId) =>
        string.Equals(slot.HomeClubId, clubId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(slot.GuestClubId, clubId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The booked matches of <paramref name="clubId"/> (home or away) on that day.</summary>
    public static IEnumerable<TermineSlot> BookedMatchesOf(TermineTenantDocument tenant, string clubId, string date) =>
        tenant.Slots.Where(slot =>
            !string.IsNullOrEmpty(slot.GuestClubId)
            && string.Equals(slot.Date, date, StringComparison.OrdinalIgnoreCase)
            && Involves(slot, clubId));

    /// <summary>The first of <paramref name="slots"/> that starts less than 4 hours before or after <paramref name="time"/>.</summary>
    public static TermineSlot? FindTooClose(IEnumerable<TermineSlot> slots, string time)
    {
        if (MinuteOfDay(time) is not { } start)
        {
            return null;
        }

        return slots.FirstOrDefault(slot =>
            MinuteOfDay(slot.Time) is { } other
            && Math.Abs(other - start) < MinHoursBetweenMatches * 60);
    }

    /// <summary>"Ihre Mannschaft spielt am Di, 19.01.2027 schon um 19:30 Uhr. Zwischen zwei Terminen …"</summary>
    public static string GapMessage(string subject, string date, string otherTime) =>
        subject + " am " + TermineDates.FormatWithWeekday(date) + " schon um " + otherTime
        + " Uhr. Zwischen zwei Terminen am selben Tag müssen mindestens " + MinHoursBetweenMatches + " Stunden liegen.";

    /// <summary>
    /// Termineingabe: another booked match of the home or guest team of <paramref name="match"/>
    /// on that day less than 4 hours from <paramref name="time"/>.
    /// </summary>
    public static TermineSlot? FindTooCloseMatch(TermineTenantDocument tenant, TermineMatch match, string date, string time) =>
        FindTooClose(
            tenant.Slots.Where(slot =>
                !string.IsNullOrEmpty(slot.GuestClubId)
                && !string.Equals(slot.MatchId, match.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.Date, date, StringComparison.OrdinalIgnoreCase)
                && (Involves(slot, match.HomeClubId) || Involves(slot, match.GuestClubId))),
            time);

    private static int? MinuteOfDay(string? time) =>
        TermineDates.TryParseTime(time, out var normalized)
            ? (int)TimeOnly.ParseExact(normalized, "HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToTimeSpan().TotalMinutes
            : null;

    public static bool IsHomeClub(TermineTenantDocument tenant, string clubId) =>
        tenant.Matches.Any(match => string.Equals(match.HomeClubId, clubId, StringComparison.OrdinalIgnoreCase));

    public static bool IsGuestClub(TermineTenantDocument tenant, string clubId) =>
        tenant.Matches.Any(match => string.Equals(match.GuestClubId, clubId, StringComparison.OrdinalIgnoreCase));

    public static string? ClubName(TermineTenantDocument tenant, string? clubId)
    {
        if (string.IsNullOrEmpty(clubId))
        {
            return null;
        }

        return tenant.Clubs.FirstOrDefault(club =>
            string.Equals(club.Id, clubId, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    public static IReadOnlyList<TermineClub> ActingClubs(TermineTenantDocument tenant)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        return tenant.Clubs
            .Where(club => !string.IsNullOrWhiteSpace(club.Id))
            .OrderBy(club => club.Name, StringComparer.Create(culture, ignoreCase: true))
            .ToList();
    }

    public static IReadOnlyList<TermineClub> HomeColumnClubs(TermineTenantDocument tenant)
    {
        var result = new List<TermineClub>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in tenant.Matches)
        {
            if (!seen.Add(match.HomeClubId))
            {
                continue;
            }

            var club = tenant.Clubs.FirstOrDefault(item =>
                string.Equals(item.Id, match.HomeClubId, StringComparison.OrdinalIgnoreCase));
            if (club is not null)
            {
                result.Add(club);
            }
        }

        return result;
    }

    /// <summary>
    /// A home Termin at <paramref name="time"/>: on an allowed day, and at least 4 hours from every
    /// other Termin of the team that day (its offered or booked home slots and its away matches).
    /// Offering an existing open slot again is fine.
    /// </summary>
    public static TermineResult CanOffer(TermineTenantDocument tenant, string homeClubId, string date, string time)
    {
        if (!IsHomeClub(tenant, homeClubId))
        {
            return TermineResult.Fail("Diese Mannschaft hat in diesem Spielplan kein Heimspiel.");
        }

        if (!tenant.Dates.Contains(date, StringComparer.OrdinalIgnoreCase))
        {
            return TermineResult.Fail("An diesem Tag darf nicht gespielt werden (gesperrt oder außerhalb des Zeitraums).");
        }

        if (!TermineDates.TryParseTime(time, out var normalized))
        {
            return TermineResult.Fail("Bitte eine Uhrzeit im Format HH:mm angeben.");
        }

        var same = FindSlot(tenant, homeClubId, date, normalized);
        if (same is not null)
        {
            return string.IsNullOrEmpty(same.GuestClubId)
                ? TermineResult.Success()
                : TermineResult.Fail("Dieser Termin ist bereits gebucht.");
        }

        var teamTermine = SlotsAt(tenant, homeClubId, date).Concat(BookedMatchesOf(tenant, homeClubId, date)).Distinct();
        if (FindTooClose(teamTermine, normalized) is { } close)
        {
            return TermineResult.Fail(GapMessage("Ihre Mannschaft hat", date, close.Time).Replace(" schon um ", " schon einen Termin um "));
        }

        return TermineResult.Success();
    }

    public static TermineResult CanWithdraw(TermineTenantDocument tenant, string homeClubId, string date, string? time = null)
    {
        var slot = FindSlot(tenant, homeClubId, date, time);
        if (slot is null)
        {
            return TermineResult.Fail("Es gibt kein Angebot an diesem Tag.");
        }

        if (!string.IsNullOrEmpty(slot.GuestClubId))
        {
            return TermineResult.Fail("Der Termin ist bereits gebucht. Nur die Staffelleitung kann ihn wieder freigeben.");
        }

        return TermineResult.Success();
    }

    public static TermineMatch? FindMatch(TermineTenantDocument tenant, string? matchId)
    {
        if (string.IsNullOrEmpty(matchId))
        {
            return null;
        }

        return tenant.Matches.FirstOrDefault(match =>
            string.Equals(match.Id, matchId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsMatchScheduled(TermineTenantDocument tenant, string matchId) =>
        tenant.Slots.Any(slot =>
            string.Equals(slot.MatchId, matchId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(slot.GuestClubId));

    public static TermineMatch? FindUnscheduledMatch(
        TermineTenantDocument tenant,
        string homeClubId,
        string guestClubId)
    {
        return tenant.Matches.FirstOrDefault(match =>
            string.Equals(match.HomeClubId, homeClubId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(match.GuestClubId, guestClubId, StringComparison.OrdinalIgnoreCase)
            && !IsMatchScheduled(tenant, match.Id));
    }

    public static bool HasFixedHomeGuestMatch(
        TermineTenantDocument tenant,
        string homeClubId,
        string guestClubId) =>
        tenant.Matches.Any(match =>
            string.Equals(match.HomeClubId, homeClubId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(match.GuestClubId, guestClubId, StringComparison.OrdinalIgnoreCase));

    public static TermineSlot? FindSlotForMatch(TermineTenantDocument tenant, string matchId) =>
        tenant.Slots.FirstOrDefault(slot =>
            string.Equals(slot.MatchId, matchId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(slot.GuestClubId));

    /// <summary>
    /// The guest books the home team's open slot at <paramref name="time"/> (without a time: the
    /// first slot of the day). Both teams' other matches that day must be at least 4 hours away.
    /// </summary>
    public static TermineResult CanBook(
        TermineTenantDocument tenant,
        string homeClubId,
        string date,
        string? time,
        string guestClubId)
    {
        if (string.Equals(homeClubId, guestClubId, StringComparison.OrdinalIgnoreCase))
        {
            return TermineResult.Fail("Eine Mannschaft kann nicht bei sich selbst Gast sein.");
        }

        if (tenant.Matches.Count == 0)
        {
            return TermineResult.Fail("Bitte zuerst Begegnungen importieren.");
        }

        var match = FindUnscheduledMatch(tenant, homeClubId, guestClubId);
        if (match is null)
        {
            if (HasFixedHomeGuestMatch(tenant, guestClubId, homeClubId))
            {
                return TermineResult.Fail("Heim und Gast dieser Begegnung dürfen nicht getauscht werden.");
            }

            return TermineResult.Fail("Sie haben kein offenes Auswärtsspiel bei dieser Mannschaft.");
        }

        if (!IsHomeClub(tenant, homeClubId))
        {
            return TermineResult.Fail("Diese Mannschaft hat in diesem Spielplan kein Heimspiel.");
        }

        var slot = FindSlot(tenant, homeClubId, date, time);
        if (slot is null || string.IsNullOrWhiteSpace(slot.Time))
        {
            return TermineResult.Fail("Die Heimmannschaft hat an diesem Tag keinen Termin angeboten.");
        }

        if (!string.IsNullOrEmpty(slot.GuestClubId))
        {
            return TermineResult.Fail("Dieser Heimtermin ist bereits vergeben.");
        }

        if (FindTooClose(BookedMatchesOf(tenant, guestClubId, date), slot.Time) is { } own)
        {
            return TermineResult.Fail(GapMessage("Ihre Mannschaft spielt", date, own.Time));
        }

        if (FindTooClose(BookedMatchesOf(tenant, homeClubId, date), slot.Time) is { } home)
        {
            return TermineResult.Fail(GapMessage("Die Heimmannschaft spielt", date, home.Time));
        }

        return TermineResult.Success();
    }
}
