using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudServices.Pages.Appointment;

/// <summary>
/// Public Termineingabe: the same Spielplan as the Terminplanung, as one list of all
/// Begegnungen. The selected Mannschaft types the Termin of each of its home matches; every
/// change is saved right away and shows in the Terminplanung as a booked Termin.
/// </summary>
public class TermineingabeModel : PageModel
{
    private readonly TermineStore _store;

    public TermineingabeModel(TermineStore store)
    {
        _store = store;
    }

    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public string? ActingClubId { get; set; }

    public IReadOnlyList<TermineTenantSummary> Plans { get; private set; } = [];

    public TermineTenantDocument? Tenant { get; private set; }

    public TermineTenantSummary? Summary { get; private set; }

    public TermineingabeView? View { get; private set; }

    public IReadOnlyList<TermineClub> ActingClubOptions { get; private set; } = [];

    public string? SelectedClubId { get; private set; }

    public int MyTotal { get; private set; }

    public int MyDone { get; private set; }

    public string? StatusMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        StatusMessage = TempData[TerminGeneratorModel.StatusKey] as string;
        ErrorMessage = TempData[TerminGeneratorModel.ErrorKey] as string;
        if (string.IsNullOrWhiteSpace(Id))
        {
            Plans = await TermineTenantSummary.LoadAllAsync(_store, cancellationToken).ConfigureAwait(false);
            return Page();
        }

        Tenant = await _store.GetByIdAsync(Id, cancellationToken).ConfigureAwait(false);
        if (Tenant is null)
        {
            TempData[TerminGeneratorModel.ErrorKey] = "Spielplan wurde nicht gefunden.";
            // Clear the ambient {id}; otherwise this redirects to itself forever.
            return RedirectToPage("/Appointment/Termineingabe", new { id = (string?)null });
        }

        ActingClubOptions = TermineRules.ActingClubs(Tenant);
        var cookieClub = TermineClubCookie.Read(Request);
        SelectedClubId = ActingClubOptions.Any(club =>
            string.Equals(club.Id, cookieClub, StringComparison.OrdinalIgnoreCase))
            ? cookieClub
            : null;
        Summary = TermineTenantSummary.From(Tenant);
        View = new TermineingabeView(
            Tenant,
            SelectedClubId,
            isAdmin: false,
            Url.Page("/Appointment/Termineingabe", "Termin", new { id = Tenant.Id }) ?? "");
        MyTotal = View.Rows.Count(row => row.OwnHome);
        MyDone = View.Rows.Count(row => row.OwnHome && row.Slot is not null);
        return Page();
    }

    public IActionResult OnPostSetClub()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Appointment/Termineingabe");
        }

        TermineClubCookie.Write(Response, ActingClubId);
        return RedirectToPage("/Appointment/Termineingabe", new { id = Id });
    }

    /// <summary>Auto-save of one Termin (fetch from termineingabe.js); answers JSON.</summary>
    public async Task<IActionResult> OnPostTerminAsync(
        string? matchId,
        string? value,
        string? expected,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(matchId))
        {
            return BadRequest();
        }

        var clubId = TermineClubCookie.Read(Request);
        if (string.IsNullOrWhiteSpace(clubId))
        {
            return new JsonResult(TermineingabeSaveResult.Fail("Bitte zuerst oben Ihre Mannschaft wählen."));
        }

        var result = await _store.SetMatchTerminAsync(Id, matchId, clubId, value, expected, cancellationToken)
            .ConfigureAwait(false);
        return new JsonResult(result);
    }
}
