using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudServices.Pages.Appointment;

/// <summary>
/// Staffelleitung (secret URL): list, create and edit Spielpläne – name, fixed
/// date scope with gesperrte Tage, and the imported Begegnungen.
/// </summary>
public class TerminGeneratorModel : PageModel
{
    public const string StatusKey = "TermineStatus";
    public const string ErrorKey = "TermineError";
    public const string NewId = "neu";
    private const int MaxRangeDays = 366;

    private readonly TermineStore _store;
    private readonly StaffelleitungAccess _access;

    public TerminGeneratorModel(TermineStore store, StaffelleitungAccess access)
    {
        _store = store;
        _access = access;
    }

    public string Key => RouteData.Values["key"] as string ?? "";

    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public DateOnly? DateFrom { get; set; }

    [BindProperty]
    public DateOnly? DateTo { get; set; }

    [BindProperty]
    public List<string> BlockedDates { get; set; } = [];

    [BindProperty]
    public List<TermineClub> Clubs { get; set; } = [];

    [BindProperty]
    public List<TermineMatch> Matches { get; set; } = [];

    [BindProperty]
    public IFormFile? PlanFile { get; set; }

    public IReadOnlyList<TermineTenantSummary> Plans { get; private set; } = [];

    public bool IsCreate => string.Equals(Id, NewId, StringComparison.OrdinalIgnoreCase);

    public bool IsEdit => !string.IsNullOrWhiteSpace(Id) && !IsCreate;

    public bool IsList => !IsCreate && !IsEdit;

    public string SavedName { get; private set; } = "";

    public IReadOnlySet<string> DatesWithTermine { get; private set; } = new HashSet<string>();

    public int BookedCount { get; private set; }

    public string? StatusMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<TermineCalendarMonth> Calendar =>
        DateFrom is { } from && DateTo is { } to && Math.Abs(to.DayNumber - from.DayNumber) < MaxRangeDays
            ? TermineCalendar.Build(from, to, BlockedDates.ToHashSet(StringComparer.OrdinalIgnoreCase), DatesWithTermine)
            : [];

    public string PublicUrl(string id) =>
        Url.PageLink("/Appointment/Termine", values: new { id }) ?? "";

    public override async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        // Defense in depth; the route constraint already rejects wrong keys.
        if (!_access.IsValid(Key))
        {
            context.Result = NotFound();
            return;
        }

        await next().ConfigureAwait(false);
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ReadFlash();
        if (IsList)
        {
            Plans = await TermineTenantSummary.LoadAllAsync(_store, cancellationToken).ConfigureAwait(false);
            return Page();
        }

        if (IsCreate)
        {
            return Page();
        }

        var tenant = await _store.GetByIdAsync(Id!, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            TempData[ErrorKey] = "Spielplan wurde nicht gefunden.";
            return RedirectToOverview();
        }

        LoadFrom(tenant);
        LoadEditContext(tenant);
        return Page();
    }

    public async Task<IActionResult> OnPostApplyRangeAsync(CancellationToken cancellationToken)
    {
        Normalize();
        if (!TryGetRange(out var from, out var to, out var error))
        {
            ErrorMessage = error;
        }
        else
        {
            DateFrom = from;
            DateTo = to;
            BlockedDates = BlockedDates.Where(iso => InRange(iso, from, to)).ToList();
        }

        return await RedisplayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        if (IsEdit)
        {
            return await OnPostSaveAsync(cancellationToken).ConfigureAwait(false);
        }

        Normalize();
        if (PlanFile is null || PlanFile.Length == 0)
        {
            ErrorMessage = "Bitte eine CSV-Datei mit den Begegnungen wählen.";
            return await RedisplayAsync(cancellationToken).ConfigureAwait(false);
        }

        var draft = new TermineTenantDocument { Id = "draft", Clubs = Clubs, Matches = Matches };
        var parsed = await TryApplyUploadAsync(draft).ConfigureAwait(false);
        if (!parsed.Ok)
        {
            ErrorMessage = parsed.Error;
            return await RedisplayAsync(cancellationToken).ConfigureAwait(false);
        }

        StatusMessage = $"{Matches.Count} Begegnungen mit {Clubs.Count} Mannschaften eingelesen. Prüfen Sie die Liste und legen Sie den Spielplan an.";
        return await RedisplayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!IsCreate)
        {
            return RedirectToOverview();
        }

        Normalize();
        if (!Validate(out var from, out var to))
        {
            return Page();
        }

        var tenant = new TermineTenantDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name,
            RangeFrom = TermineDates.ToIso(from),
            RangeTo = TermineDates.ToIso(to),
            Dates = AllowedDates(from, to),
            Clubs = Clubs,
            Matches = Matches
        };

        var import = await TryApplyUploadAsync(tenant).ConfigureAwait(false);
        if (!import.Ok)
        {
            ErrorMessage = import.Error;
            return Page();
        }

        await _store.CreateAsync(tenant, cancellationToken).ConfigureAwait(false);
        TempData[StatusKey] = "Spielplan wurde angelegt. Schicken Sie den öffentlichen Link an die Mannschaftsführer.";
        return RedirectToEdit(tenant.Id);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (!IsEdit)
        {
            return RedirectToOverview();
        }

        Normalize();
        var existing = await _store.GetByIdAsync(Id!, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            TempData[ErrorKey] = "Spielplan wurde nicht gefunden.";
            return RedirectToOverview();
        }

        LoadEditContext(existing);
        if (!Validate(out var from, out var to))
        {
            return Page();
        }

        var dates = AllowedDates(from, to);
        var allowed = dates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = existing.Slots
            .Select(slot => slot.Date)
            .Where(date => !allowed.Contains(date))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (conflicts.Count > 0)
        {
            ErrorMessage = "An diesen Tagen gibt es bereits Termine: "
                + string.Join(", ", conflicts.Select(TermineDates.FormatWithWeekday))
                + ". Löschen Sie diese Termine zuerst unter „Termine“ oder lassen Sie die Tage frei.";
            return Page();
        }

        var tenant = new TermineTenantDocument
        {
            Id = existing.Id,
            Name = Name,
            RangeFrom = TermineDates.ToIso(from),
            RangeTo = TermineDates.ToIso(to),
            Dates = dates,
            Clubs = Clubs,
            Matches = Matches,
            Slots = existing.Slots
        };

        var hadFile = PlanFile is { Length: > 0 };
        var import = await TryApplyUploadAsync(tenant).ConfigureAwait(false);
        if (!import.Ok)
        {
            ErrorMessage = import.Error;
            return Page();
        }

        var result = await _store.UpdateAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        TempData[StatusKey] = hadFile
            ? $"{tenant.Matches.Count} Begegnungen importiert und Spielplan gespeichert."
            : "Spielplan wurde gespeichert.";
        return RedirectToEdit(tenant.Id);
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        if (!IsEdit)
        {
            return RedirectToOverview();
        }

        var deleted = await _store.DeleteAsync(Id!, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            TempData[StatusKey] = "Spielplan wurde gelöscht.";
        }
        else
        {
            TempData[ErrorKey] = "Spielplan wurde nicht gefunden.";
        }

        return RedirectToOverview();
    }

    public string ClubName(string clubId) =>
        Clubs.FirstOrDefault(club => string.Equals(club.Id, clubId, StringComparison.OrdinalIgnoreCase))
            ?.Name
        ?? clubId;

    // Clear the ambient {id}; otherwise the redirect keeps the current id and an
    // unknown/deleted id redirects to itself forever.
    private RedirectToPageResult RedirectToOverview() =>
        RedirectToPage("/Appointment/TerminGenerator", new { key = Key, id = (string?)null });

    private RedirectToPageResult RedirectToEdit(string id) =>
        RedirectToPage("/Appointment/TerminGenerator", new { key = Key, id });

    private async Task<IActionResult> RedisplayAsync(CancellationToken cancellationToken)
    {
        if (IsEdit)
        {
            var existing = await _store.GetByIdAsync(Id!, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                TempData[ErrorKey] = "Spielplan wurde nicht gefunden.";
                return RedirectToOverview();
            }

            LoadEditContext(existing);
        }

        return Page();
    }

    private bool Validate(out DateOnly from, out DateOnly to)
    {
        if (!TryGetRange(out from, out to, out var rangeError))
        {
            ErrorMessage = rangeError;
            return false;
        }

        DateFrom = from;
        DateTo = to;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Bitte einen Namen für den Spielplan angeben.";
            return false;
        }

        if (AllowedDates(from, to).Count == 0)
        {
            ErrorMessage = "Alle Tage im Zeitraum sind gesperrt. Bitte mindestens einen Spieltag freigeben.";
            return false;
        }

        return true;
    }

    private bool TryGetRange(out DateOnly from, out DateOnly to, out string error)
    {
        from = default;
        to = default;
        error = "";
        if (DateFrom is null || DateTo is null)
        {
            error = "Bitte den Zeitraum (von und bis) angeben.";
            return false;
        }

        from = DateFrom.Value;
        to = DateTo.Value;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        if (to.DayNumber - from.DayNumber >= MaxRangeDays)
        {
            error = "Der Zeitraum darf höchstens ein Jahr umfassen.";
            return false;
        }

        return true;
    }

    private List<string> AllowedDates(DateOnly from, DateOnly to)
    {
        var blocked = BlockedDates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TermineDates.Generate(from, to).Where(iso => !blocked.Contains(iso)).ToList();
    }

    private static bool InRange(string iso, DateOnly from, DateOnly to) =>
        TermineDates.TryParse(iso, out var date) && date >= from && date <= to;

    private void ReadFlash()
    {
        StatusMessage = TempData[StatusKey] as string;
        ErrorMessage = TempData[ErrorKey] as string;
    }

    private void LoadFrom(TermineTenantDocument tenant)
    {
        Name = tenant.Name;
        Clubs = tenant.Clubs
            .Select(club => new TermineClub { Id = club.Id, Name = club.Name })
            .ToList();
        Matches = tenant.Matches
            .Select(match => new TermineMatch
            {
                Id = match.Id,
                HomeClubId = match.HomeClubId,
                GuestClubId = match.GuestClubId,
                Location = match.Location
            })
            .ToList();
        if (TermineDates.TryGetRange(tenant, out var from, out var to))
        {
            DateFrom = from;
            DateTo = to;
            var allowed = tenant.Dates.ToHashSet(StringComparer.OrdinalIgnoreCase);
            BlockedDates = TermineDates.Generate(from, to).Where(iso => !allowed.Contains(iso)).ToList();
        }
    }

    private void LoadEditContext(TermineTenantDocument tenant)
    {
        SavedName = tenant.Name;
        DatesWithTermine = tenant.Slots.Select(slot => slot.Date).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BookedCount = tenant.Slots.Count(slot => !string.IsNullOrEmpty(slot.GuestClubId));
    }

    private async Task<TermineResult> TryApplyUploadAsync(TermineTenantDocument tenant)
    {
        if (PlanFile is null || PlanFile.Length == 0)
        {
            return TermineResult.Success();
        }

        await using var stream = PlanFile.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory).ConfigureAwait(false);
        var parsed = BegegnungenCsv.TryParse(memory.ToArray(), out var rows);
        if (!parsed.Ok)
        {
            return parsed;
        }

        BegegnungenCsv.ApplyToTenant(tenant, rows);
        Clubs = tenant.Clubs;
        Matches = tenant.Matches;
        return TermineResult.Success();
    }

    private void Normalize()
    {
        Clubs ??= [];
        Matches ??= [];
        BlockedDates ??= [];
        Name = Name?.Trim() ?? "";
        BlockedDates = BlockedDates
            .Select(value => TermineDates.TryParse(value, out var date) ? TermineDates.ToIso(date) : null)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
