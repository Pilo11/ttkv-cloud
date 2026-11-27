using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudServices.Pages.Appointment;

/// <summary>
/// Staffelleitung view of the Termineingabe: every Begegnung's Termin can be typed, changed or
/// cleared, and the result exported for myTT (same CSV as the Termine tab).
/// </summary>
public class TermineingabeAdminModel : PageModel
{
    private readonly TermineStore _store;
    private readonly StaffelleitungAccess _access;

    public TermineingabeAdminModel(TermineStore store, StaffelleitungAccess access)
    {
        _store = store;
        _access = access;
    }

    public string Key => RouteData.Values["key"] as string ?? "";

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    public TermineTenantDocument? Tenant { get; private set; }

    public TermineTenantSummary? Summary { get; private set; }

    public TermineingabeView? View { get; private set; }

    public string? StatusMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

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
        StatusMessage = TempData[TerminGeneratorModel.StatusKey] as string;
        ErrorMessage = TempData[TerminGeneratorModel.ErrorKey] as string;
        Tenant = await _store.GetByIdAsync(Id, cancellationToken).ConfigureAwait(false);
        if (Tenant is null)
        {
            TempData[TerminGeneratorModel.ErrorKey] = "Spielplan wurde nicht gefunden.";
            return RedirectToPage("/Appointment/TerminGenerator", new { key = Key, id = (string?)null });
        }

        Summary = TermineTenantSummary.From(Tenant);
        View = new TermineingabeView(
            Tenant,
            selectedClubId: null,
            isAdmin: true,
            Url.Page("/Appointment/TermineingabeAdmin", "Termin", new { key = Key, id = Id }) ?? "");
        return Page();
    }

    /// <summary>Auto-save of one Termin (fetch from termineingabe.js); the Staffelleitung may change every Begegnung.</summary>
    public async Task<IActionResult> OnPostTerminAsync(
        string? matchId,
        string? value,
        string? expected,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return BadRequest();
        }

        var result = await _store.SetMatchTerminAsync(Id, matchId, actingClubId: null, value, expected, cancellationToken)
            .ConfigureAwait(false);
        return new JsonResult(result);
    }
}
