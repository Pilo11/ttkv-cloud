namespace CloudServices.Services.Appointment;

public static class TermineRules
{
    public static TermineSlot? FindSlot(TermineTenantDocument tenant, string homeClubId, string date) =>
        tenant.Slots.FirstOrDefault(slot =>
            string.Equals(slot.HomeClubId, homeClubId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(slot.Date, date, StringComparison.OrdinalIgnoreCase));

    public static bool HasBookedMatch(TermineTenantDocument tenant, string clubId, string date)
    {
        return tenant.Slots.Any(slot =>
            string.Equals(slot.Date, date, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(slot.GuestClubId)
            && (string.Equals(slot.HomeClubId, clubId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(slot.GuestClubId, clubId, StringComparison.OrdinalIgnoreCase)));
    }

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

        if (!TermineDates.TryParseTime(time, out _))
        {
            return TermineResult.Fail("Bitte eine Uhrzeit im Format HH:mm angeben.");
        }

        var slot = FindSlot(tenant, homeClubId, date);
        if (slot is not null && !string.IsNullOrEmpty(slot.GuestClubId))
        {
            return TermineResult.Fail("Dieser Termin ist bereits gebucht.");
        }

        return TermineResult.Success();
    }

    public static TermineResult CanWithdraw(TermineTenantDocument tenant, string homeClubId, string date)
    {
        var slot = FindSlot(tenant, homeClubId, date);
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

    public static TermineResult CanBook(TermineTenantDocument tenant, string homeClubId, string date, string guestClubId)
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

        var slot = FindSlot(tenant, homeClubId, date);
        if (slot is null || string.IsNullOrWhiteSpace(slot.Time))
        {
            return TermineResult.Fail("Die Heimmannschaft hat an diesem Tag keinen Termin angeboten.");
        }

        if (!string.IsNullOrEmpty(slot.GuestClubId))
        {
            return TermineResult.Fail("Dieser Heimtermin ist bereits vergeben.");
        }

        if (HasBookedMatch(tenant, guestClubId, date))
        {
            return TermineResult.Fail("Ihre Mannschaft hat an diesem Tag bereits ein Spiel.");
        }

        if (HasBookedMatch(tenant, homeClubId, date))
        {
            return TermineResult.Fail("Die Heimmannschaft hat an diesem Tag bereits ein Spiel.");
        }

        return TermineResult.Success();
    }
}
