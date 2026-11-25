using System.Text;
using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudServices.Pages.Appointment;

/// <summary>
/// Staffelleitung view of all Termine of a Spielplan. Only here can booked
/// Termine be freed again or deleted, and the result exported for myTT.
/// </summary>
public class TermineAdminModel : PageModel
{
    private readonly TermineStore _store;
    private readonly StaffelleitungAccess _access;

    public TermineAdminModel(TermineStore store, StaffelleitungAccess access)
    {
        _store = store;
        _access = access;
    }

    public string Key => RouteData.Values["key"] as string ?? "";

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    [BindProperty]
    public string? SlotKey { get; set; }

    public TermineTenantDocument? Tenant { get; private set; }

    public TermineTenantSummary? Summary { get; private set; }

    public TermineMatrixView? Matrix { get; private set; }

    public string? StatusMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        if (!_access.IsValid(Key))
        {
            context.Result = NotFound();
            return;
        }

        await next().ConfigureAwait(false);
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        StatusMessage = TempData[TerminGeneratorModel.StatusKey] as string;
        ErrorMessage = TempData[TerminGeneratorModel.ErrorKey] as string;
        Tenant = await _store.GetByIdAsync(Id, cancellationToken).ConfigureAwait(false);
        if (Tenant is null)
        {
            TempData[TerminGeneratorModel.ErrorKey] = "Spielplan wurde nicht gefunden.";
            return RedirectToPage("/Appointment/TerminGenerator", new { key = Key, id = (string?)null });
        }

        Summary = TermineTenantSummary.From(Tenant);
        Matrix = new TermineMatrixView(Tenant, selectedClubId: null, isAdmin: true);
        return Page();
    }

    public Task<IActionResult> OnPostReleaseAsync(CancellationToken cancellationToken) =>
        ClearAsync(deleteSlot: false, cancellationToken);

    public Task<IActionResult> OnPostDeleteSlotAsync(CancellationToken cancellationToken) =>
        ClearAsync(deleteSlot: true, cancellationToken);

    /// <summary>
    /// CSV for the myTT page import: Datum;Uhrzeit;Nr;Heim;Gast, one row per booked Begegnung.
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellationToken)
    {
        var tenant = await _store.GetByIdAsync(Id, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        var rows = tenant.Matches
            .Select(match => (Match: match, Slot: TermineRules.FindSlotForMatch(tenant, match.Id)))
            .Where(item => item.Slot is not null)
            .OrderBy(item => item.Slot!.Date, StringComparer.Ordinal)
            .ThenBy(item => item.Slot!.Time, StringComparer.Ordinal)
            .ThenBy(item => TermineRules.ClubName(tenant, item.Match.HomeClubId), StringComparer.CurrentCultureIgnoreCase);

        var csv = new StringBuilder();
        csv.Append("Datum;Uhrzeit;Nr;Heim;Gast\r\n");
        foreach (var (match, slot) in rows)
        {
            csv.Append(Cell(TermineDates.FormatDisplay(slot!.Date))).Append(';')
                .Append(Cell(slot.Time)).Append(';')
                .Append(Cell(match.Location)).Append(';')
                .Append(Cell(TermineRules.ClubName(tenant, match.HomeClubId))).Append(';')
                .Append(Cell(TermineRules.ClubName(tenant, match.GuestClubId))).Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", FileName(tenant.Name) + " - myTT.csv");
    }

    private async Task<IActionResult> ClearAsync(bool deleteSlot, CancellationToken cancellationToken)
    {
        var parts = (SlotKey ?? "").Split('|', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            TempData[TerminGeneratorModel.ErrorKey] = "Ungültiger Termin.";
            return RedirectToSelf();
        }

        var result = await _store.AdminClearSlotAsync(Id, parts[0], parts[1], deleteSlot, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            TempData[TerminGeneratorModel.StatusKey] = deleteSlot
                ? "Termin am " + TermineDates.FormatWithWeekday(parts[1]) + " wurde gelöscht."
                : "Buchung am " + TermineDates.FormatWithWeekday(parts[1]) + " wurde aufgehoben. Der Terminvorschlag ist wieder frei.";
        }
        else
        {
            TempData[TerminGeneratorModel.ErrorKey] = result.Error;
        }

        return RedirectToSelf();
    }

    private RedirectToPageResult RedirectToSelf() =>
        RedirectToPage("/Appointment/TermineAdmin", new { key = Key, id = Id });

    private static string Cell(string? value)
    {
        var text = value ?? "";
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? "Spielplan" : cleaned;
    }
}
