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

                var slot = TermineRules.FindSlot(tenant, homeClubId, date);
                if (slot is null)
                {
                    tenant.Slots.Add(new TermineSlot
                    {
                        HomeClubId = homeClubId,
                        Date = date,
                        Time = normalizedTime
                    });
                }
                else
                {
                    slot.Time = normalizedTime;
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

    public async Task<TermineResult> WithdrawOfferAsync(
        string tenantId,
        string homeClubId,
        string date,
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

            var check = TermineRules.CanWithdraw(tenant, homeClubId, iso);
            if (!check.Ok)
            {
                return check;
            }

            tenant.Slots.RemoveAll(slot =>
                string.Equals(slot.HomeClubId, homeClubId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.Date, iso, StringComparison.OrdinalIgnoreCase));
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
    public async Task<TermineResult> AdminClearSlotAsync(
        string tenantId,
        string homeClubId,
        string date,
        bool deleteSlot,
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

            var slot = TermineRules.FindSlot(tenant, homeClubId, iso);
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

    public async Task<TermineResult> BookGuestAsync(
        string tenantId,
        string homeClubId,
        string date,
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

            var check = TermineRules.CanBook(tenant, homeClubId, iso, guestClubId);
            if (!check.Ok)
            {
                return check;
            }

            var slot = TermineRules.FindSlot(tenant, homeClubId, iso);
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

            var key = slot.HomeClubId + "|" + slot.Date;
            if (!seen.Add(key))
            {
                continue;
            }

            kept.Add(slot);
        }

        tenant.Slots = kept;
    }
}
