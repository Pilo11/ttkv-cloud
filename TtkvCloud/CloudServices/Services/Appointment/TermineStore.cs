using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CloudServices.Services.Appointment;

public sealed class TermineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly ILogger<TermineStore> _logger;

    public TermineStore(
        IHostEnvironment env,
        IConfiguration configuration,
        IOptions<TermineOptions> options,
        ILogger<TermineStore> logger)
    {
        _logger = logger;
        var configured = options.Value.DataDirectory?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _directory = ResolvePath(env, configured);
        }
        else
        {
            var cacheRoot = configuration["CACHE_ROOT"]?.Trim();
            _directory = !string.IsNullOrWhiteSpace(cacheRoot)
                ? Path.Combine(ResolvePath(env, cacheRoot), "termine")
                : Path.Combine(env.ContentRootPath, "Data", "termine");
        }

        Directory.CreateDirectory(_directory);
        _indexPath = Path.Combine(_directory, "tenants-index.json");
        _logger.LogInformation("Termine store directory: {Path}", _directory);
    }

    public string DataDirectory => _directory;

    public async Task<IReadOnlyList<TermineTenantIndexEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return index.Tenants
                .OrderByDescending(entry => entry.CreatedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TermineTenantDocument?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadTenantUnlockedAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TermineTenantDocument> CreateAsync(TermineTenantDocument tenant, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NormalizeTenant(tenant);
            if (string.IsNullOrWhiteSpace(tenant.Id) || !IsSafeId(tenant.Id))
            {
                tenant.Id = Guid.NewGuid().ToString("N");
            }

            var index = await LoadIndexUnlockedAsync(cancellationToken).ConfigureAwait(false);
            index.Tenants.RemoveAll(entry => string.Equals(entry.Id, tenant.Id, StringComparison.OrdinalIgnoreCase));
            index.Tenants.Add(new TermineTenantIndexEntry
            {
                Id = tenant.Id,
                Name = tenant.Name,
                CreatedAt = DateTimeOffset.Now
            });

            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);
            await SaveIndexUnlockedAsync(index, cancellationToken).ConfigureAwait(false);
            return tenant;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TermineResult> UpdateAsync(TermineTenantDocument tenant, CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(tenant.Id))
        {
            return TermineResult.Fail("Ungültiger Spielplan.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadTenantUnlockedAsync(tenant.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return TermineResult.Fail("Spielplan wurde nicht gefunden.");
            }

            NormalizeTenant(tenant);
            PruneSlots(tenant);
            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);

            var index = await LoadIndexUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var entry = index.Tenants.FirstOrDefault(item =>
                string.Equals(item.Id, tenant.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.Name = tenant.Name;
                await SaveIndexUnlockedAsync(index, cancellationToken).ConfigureAwait(false);
            }

            return TermineResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsSafeId(id))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var removed = index.Tenants.RemoveAll(entry =>
                string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            await SaveIndexUnlockedAsync(index, cancellationToken).ConfigureAwait(false);
            var path = TenantPath(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TermineResult> OfferSlotsAsync(
        string tenantId,
        string homeClubId,
        IReadOnlyCollection<string> dates,
        string time,
        CancellationToken cancellationToken = default)
    {
        if (!TermineDates.TryParseTime(time, out var normalizedTime))
        {
            return TermineResult.Fail("Bitte eine Uhrzeit im Format HH:mm angeben.");
        }

        if (dates.Count == 0)
        {
            return TermineResult.Fail("Bitte mindestens ein Datum auswählen.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenant = await LoadTenantUnlockedAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                return TermineResult.Fail("Spielplan wurde nicht gefunden.");
            }

            var offered = 0;
            TermineResult? lastError = null;
            foreach (var rawDate in dates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!TermineDates.TryParse(rawDate, out var parsed))
                {
                    continue;
                }

                var date = TermineDates.ToIso(parsed);
                var check = TermineRules.CanOffer(tenant, homeClubId, date, normalizedTime);
                if (!check.Ok)
                {
                    lastError = check;
                    continue;
                }

                // Another slot that day (at least 4 hours from the others, CanOffer); the same open
                // slot again changes nothing.
                if (TermineRules.FindSlot(tenant, homeClubId, date, normalizedTime) is null)
                {
                    tenant.Slots.Add(new TermineSlot
                    {
                        HomeClubId = homeClubId,
                        Date = date,
                        Time = normalizedTime
                    });
                }

                offered += 1;
            }

            if (offered == 0)
            {
                return lastError ?? TermineResult.Fail("Es konnte kein Heimtermin gesetzt werden.");
            }

            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);
            return TermineResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="time">The offered slot; without it the first one of the day.</param>
    public async Task<TermineResult> WithdrawOfferAsync(
        string tenantId,
        string homeClubId,
        string date,
        string? time = null,
        CancellationToken cancellationToken = default)
    {
        if (!TermineDates.TryParse(date, out var parsed))
        {
            return TermineResult.Fail("Ungültiges Datum.");
        }

        var iso = TermineDates.ToIso(parsed);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenant = await LoadTenantUnlockedAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                return TermineResult.Fail("Spielplan wurde nicht gefunden.");
            }

            var check = TermineRules.CanWithdraw(tenant, homeClubId, iso, time);
            if (!check.Ok)
            {
                return check;
            }

            tenant.Slots.Remove(TermineRules.FindSlot(tenant, homeClubId, iso, time)!);
            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);
            return TermineResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Staffelleitung only: frees a booked Termin (keeps the home offer) or deletes the
    /// Termin entirely. Mannschaftsführer cannot undo a booking themselves.
    /// </summary>
    /// <param name="time">The slot; without it the first one of the day.</param>
    public async Task<TermineResult> AdminClearSlotAsync(
        string tenantId,
        string homeClubId,
        string date,
        bool deleteSlot,
        string? time = null,
        CancellationToken cancellationToken = default)
    {
        if (!TermineDates.TryParse(date, out var parsed))
        {
            return TermineResult.Fail("Ungültiges Datum.");
        }

        var iso = TermineDates.ToIso(parsed);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenant = await LoadTenantUnlockedAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                return TermineResult.Fail("Spielplan wurde nicht gefunden.");
            }

            var slot = TermineRules.FindSlot(tenant, homeClubId, iso, time);
            if (slot is null)
            {
                return TermineResult.Fail("An diesem Tag gibt es keinen Termin.");
            }

            if (deleteSlot)
            {
                tenant.Slots.Remove(slot);
            }
            else
            {
                if (string.IsNullOrEmpty(slot.GuestClubId))
                {
                    return TermineResult.Fail("Dieser Termin ist nicht gebucht.");
                }

                slot.GuestClubId = null;
                slot.MatchId = null;
            }

            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);
            return TermineResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="time">The offered slot; without it the first one of the day.</param>
    public async Task<TermineResult> BookGuestAsync(
        string tenantId,
        string homeClubId,
        string date,
        string? time,
        string guestClubId,
        CancellationToken cancellationToken = default)
    {
        if (!TermineDates.TryParse(date, out var parsed))
        {
            return TermineResult.Fail("Ungültiges Datum.");
        }

        var iso = TermineDates.ToIso(parsed);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenant = await LoadTenantUnlockedAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                return TermineResult.Fail("Spielplan wurde nicht gefunden.");
            }

            var check = TermineRules.CanBook(tenant, homeClubId, iso, time, guestClubId);
            if (!check.Ok)
            {
                return check;
            }

            var slot = TermineRules.FindSlot(tenant, homeClubId, iso, time);
            if (slot is null)
            {
                return TermineResult.Fail("Die Heimmannschaft hat an diesem Tag keinen Termin angeboten.");
            }

            var match = TermineRules.FindUnscheduledMatch(tenant, homeClubId, guestClubId);
            if (match is null)
            {
                return TermineResult.Fail("Sie haben kein offenes Auswärtsspiel bei dieser Mannschaft.");
            }

            slot.GuestClubId = guestClubId;
            slot.MatchId = match.Id;
            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);
            return TermineResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Termineingabe: sets, moves or clears the Termin of one Begegnung directly. Rules: the day is
    /// a Spieltag of the Spielplan (inside the Zeitraum and not gesperrt), and two matches of a
    /// Mannschaft on the same day start at least <see cref="TermineRules.MinHoursBetweenMatches"/>
    /// hours apart; offers, bookings and "one match per day" of the Terminplanung do not apply. The
    /// Termin is stored as the Begegnung's booked slot, so the Terminplanung shows it as gebucht and
    /// the myTT export contains it.
    /// </summary>
    /// <param name="actingClubId">The selected Mannschaft, which may only change its home matches; null for the Staffelleitung.</param>
    /// <param name="expectedStamp">The Termin the page showed (yyyy-MM-ddTHH:mm or empty); a different stored Termin is a conflict.</param>
    public async Task<TermineingabeSaveResult> SetMatchTerminAsync(
        string tenantId,
        string matchId,
        string? actingClubId,
        string? text,
        string? expectedStamp,
        CancellationToken cancellationToken = default)
    {
        string? iso = null;
        string? time = null;
        if (!TermineingabeInput.IsEmpty(text))
        {
            if (!TermineingabeInput.TryParse(text, out var parsedDate, out var parsedTime, out var error))
            {
                return TermineingabeSaveResult.Fail(error);
            }

            iso = TermineDates.ToIso(parsedDate);
            time = parsedTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenant = await LoadTenantUnlockedAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null)
            {
                return TermineingabeSaveResult.Fail("Spielplan wurde nicht gefunden.");
            }

            var match = TermineRules.FindMatch(tenant, matchId);
            if (match is null)
            {
                return TermineingabeSaveResult.Fail("Diese Begegnung gibt es nicht mehr. Bitte die Seite neu laden.");
            }

            // A Mannschaft enters the Termine of its home matches; the away team's are the other home team's.
            if (actingClubId is not null
                && !string.Equals(match.HomeClubId, actingClubId, StringComparison.OrdinalIgnoreCase))
            {
                return TermineingabeSaveResult.Fail("Sie können nur die Termine der Heimspiele Ihrer Mannschaft eintragen.");
            }

            var current = TermineRules.FindSlotForMatch(tenant, match.Id);
            var currentStamp = TermineingabeInput.Stamp(current);
            var newStamp = iso is null ? "" : iso + "T" + time;
            if (string.Equals(currentStamp, newStamp, StringComparison.Ordinal))
            {
                return TermineingabeSaveResult.Current(current);
            }

            if (expectedStamp is not null && !string.Equals(currentStamp, expectedStamp.Trim(), StringComparison.Ordinal))
            {
                return TermineingabeSaveResult.Current(
                    current,
                    ok: false,
                    conflict: true,
                    error: current is null
                        ? "Der Termin wurde inzwischen entfernt. Ihre Eingabe wurde nicht gespeichert – bitte erneut eintragen."
                        : "Der Termin wurde inzwischen auf " + TermineDates.FormatWithWeekday(current.Date) + ", " + current.Time
                            + " Uhr geändert. Ihre Eingabe wurde nicht gespeichert.");
            }

            if (iso is null)
            {
                tenant.Slots.Remove(current!);
            }
            else
            {
                if (!tenant.Dates.Contains(iso, StringComparer.OrdinalIgnoreCase))
                {
                    var outside = TermineDates.TryGetRange(tenant, out var from, out var to)
                        && (string.CompareOrdinal(iso, TermineDates.ToIso(from)) < 0 || string.CompareOrdinal(iso, TermineDates.ToIso(to)) > 0);
                    return TermineingabeSaveResult.Fail(outside
                        ? "Der Termin muss zwischen " + TermineDates.FormatDisplay(TermineDates.ToIso(from)) + " und "
                            + TermineDates.FormatDisplay(TermineDates.ToIso(to)) + " liegen."
                        : "Am " + TermineDates.FormatWithWeekday(iso) + " darf nicht gespielt werden (gesperrter Tag).");
                }

                var tooClose = TermineRules.FindTooCloseMatch(tenant, match, iso, time!);
                if (tooClose is not null)
                {
                    var shared = TermineRules.Involves(tooClose, match.HomeClubId) ? match.HomeClubId : match.GuestClubId;
                    return TermineingabeSaveResult.Fail(TermineRules.GapMessage(
                        TermineRules.ClubName(tenant, shared) + " spielt", iso, tooClose.Time)
                        .Replace(" Uhr. ", " Uhr (" + TermineRules.ClubName(tenant, tooClose.HomeClubId) + " – "
                            + TermineRules.ClubName(tenant, tooClose.GuestClubId) + "). "));
                }

                // An open Terminvorschlag of the home team less than 4 hours from the new time
                // becomes this Termin; offers further away stay open.
                var offer = TermineRules.FindTooClose(
                    TermineRules.SlotsAt(tenant, match.HomeClubId, iso).Where(slot => string.IsNullOrEmpty(slot.GuestClubId)),
                    time!);
                if (offer is not null)
                {
                    if (current is not null)
                    {
                        tenant.Slots.Remove(current);
                    }

                    offer.Time = time!;
                    offer.GuestClubId = match.GuestClubId;
                    offer.MatchId = match.Id;
                }
                else if (current is not null)
                {
                    current.Date = iso;
                    current.Time = time!;
                }
                else
                {
                    tenant.Slots.Add(new TermineSlot
                    {
                        HomeClubId = match.HomeClubId,
                        Date = iso,
                        Time = time!,
                        GuestClubId = match.GuestClubId,
                        MatchId = match.Id
                    });
                }
            }

            await SaveTenantUnlockedAsync(tenant, cancellationToken).ConfigureAwait(false);
            return TermineingabeSaveResult.Current(TermineRules.FindSlotForMatch(tenant, match.Id));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ResolvePath(IHostEnvironment env, string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, path));
    }

    private static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private string TenantPath(string id) => Path.Combine(_directory, id + ".json");

    private async Task<TermineIndexDocument> LoadIndexUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
        {
            return new TermineIndexDocument();
        }

        try
        {
            await using var fs = File.OpenRead(_indexPath);
            var doc = await JsonSerializer.DeserializeAsync<TermineIndexDocument>(fs, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            doc ??= new TermineIndexDocument();
            doc.Tenants ??= [];
            return doc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read termine index; starting empty.");
            return new TermineIndexDocument();
        }
    }

    private async Task<TermineTenantDocument?> LoadTenantUnlockedAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        var path = TenantPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var fs = File.OpenRead(path);
            var doc = await JsonSerializer.DeserializeAsync<TermineTenantDocument>(fs, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            NormalizeTenant(doc);
            return doc;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read termine tenant {Id}.", id);
            return null;
        }
    }

    private async Task SaveIndexUnlockedAsync(TermineIndexDocument doc, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(_indexPath, doc, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveTenantUnlockedAsync(TermineTenantDocument tenant, CancellationToken cancellationToken)
    {
        NormalizeTenant(tenant);
        await WriteJsonAsync(TenantPath(tenant.Id), tenant, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = path + ".tmp";
        await using (var fs = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(fs, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void NormalizeTenant(TermineTenantDocument tenant)
    {
        tenant.Clubs ??= [];
        tenant.Dates ??= [];
        tenant.Matches ??= [];
        tenant.Slots ??= [];
        tenant.Name = (tenant.Name ?? "").Trim();
        if (tenant.Clubs.Count == 0)
        {
            tenant.Clubs = DistinctClubs([.. tenant.HomeClubs ?? [], .. tenant.GuestClubs ?? []]);
        }
        else
        {
            tenant.Clubs = DistinctClubs(tenant.Clubs);
        }

        tenant.HomeClubs = null;
        tenant.GuestClubs = null;
        tenant.Dates = tenant.Dates
            .Select(value => TermineDates.TryParse(value, out var date) ? TermineDates.ToIso(date) : null)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        PruneMatches(tenant);
        PruneSlots(tenant);
    }

    private static List<TermineClub> DistinctClubs(List<TermineClub> clubs)
    {
        var result = new List<TermineClub>();
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var club in clubs)
        {
            var name = (club.Name ?? "").Trim();
            if (name.Length == 0 || !names.Add(name))
            {
                continue;
            }

            result.Add(new TermineClub
            {
                Id = string.IsNullOrWhiteSpace(club.Id) ? Guid.NewGuid().ToString("N") : club.Id.Trim(),
                Name = name
            });
        }

        return result;
    }

    private static void PruneMatches(TermineTenantDocument tenant)
    {
        var clubIds = tenant.Clubs.Select(club => club.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<TermineMatch>();
        foreach (var match in tenant.Matches)
        {
            if (string.IsNullOrWhiteSpace(match.Id))
            {
                match.Id = Guid.NewGuid().ToString("N");
            }

            if (!clubIds.Contains(match.HomeClubId)
                || !clubIds.Contains(match.GuestClubId)
                || string.Equals(match.HomeClubId, match.GuestClubId, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(match.Id))
            {
                continue;
            }

            match.Location = (match.Location ?? "").Trim();
            kept.Add(match);
        }

        tenant.Matches = kept;
    }

    private static void PruneSlots(TermineTenantDocument tenant)
    {
        var clubIds = tenant.Clubs.Select(club => club.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchIds = tenant.Matches.Select(match => match.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dates = tenant.Dates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new List<TermineSlot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in tenant.Slots)
        {
            if (!clubIds.Contains(slot.HomeClubId) || !dates.Contains(slot.Date) || string.IsNullOrWhiteSpace(slot.Time))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(slot.MatchId) && !matchIds.Contains(slot.MatchId))
            {
                slot.MatchId = null;
                slot.GuestClubId = null;
            }

            if (!string.IsNullOrEmpty(slot.GuestClubId) && !clubIds.Contains(slot.GuestClubId))
            {
                slot.GuestClubId = null;
                slot.MatchId = null;
            }

            if (!string.IsNullOrEmpty(slot.GuestClubId)
                && !string.IsNullOrEmpty(slot.MatchId)
                && TermineRules.FindMatch(tenant, slot.MatchId) is { } match
                && (!string.Equals(match.HomeClubId, slot.HomeClubId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(match.GuestClubId, slot.GuestClubId, StringComparison.OrdinalIgnoreCase)))
            {
                slot.GuestClubId = null;
                slot.MatchId = null;
            }

            // A home team can have several slots a day (at least 4 hours apart): one per time; and
            // each Begegnung is booked at most once.
            var key = slot.HomeClubId + "|" + slot.Date + "|" + slot.Time;
            if (!seen.Add(key)
                || (!string.IsNullOrEmpty(slot.GuestClubId) && !string.IsNullOrEmpty(slot.MatchId) && !seen.Add("match|" + slot.MatchId)))
            {
                continue;
            }

            kept.Add(slot);
        }

        tenant.Slots = kept;
    }
}
