using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudServices.Pages.Appointment;

/// <summary>
/// Public Terminplanung for Mannschaftsführer: the selected Mannschaft offers home
/// Termine and books offered Termine as guest. Bookings cannot be undone here.
/// </summary>
public class TermineModel : PageModel
{
    public const string StatusKey = "TermineStatus";
    public const string ErrorKey = "TermineError";

    private readonly TermineStore _store;

    public TermineModel(TermineStore store)
    {
        _store = store;
    }

    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public string? ActingClubId { get; set; }

    [BindProperty]
    public string? Time { get; set; } = "20:00";

    [BindProperty]
    public List<string> SelectedDates { get; set; } = [];

    [BindProperty]
    public string? PlusDate { get; set; }

    [BindProperty]
    public string? BookKey { get; set; }

    [BindProperty]
    public string? WithdrawKey { get; set; }

    public IReadOnlyList<TermineTenantSummary> Plans { get; private set; } = [];

    public TermineTenantDocument? Tenant { get; private set; }

    public TermineTenantSummary? Summary { get; private set; }

    public TermineMatrixView? Matrix { get; private set; }

    public IReadOnlyList<TermineClub> ActingClubOptions { get; private set; } = [];

    public string? SelectedClubId { get; private set; }

    public string? SelectedClubName => Tenant is null ? null : TermineRules.ClubName(Tenant, SelectedClubId);

    public bool CanOffer => Matrix?.CanOffer == true;

    public int MyHomeTotal { get; private set; }

    public int MyHomeScheduled { get; private set; }

    public int MyAwayTotal { get; private set; }

    public int MyAwayScheduled { get; private set; }

    public string? StatusMessage { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        ReadFlash();
        if (string.IsNullOrWhiteSpace(Id))
        {
            Plans = await TermineTenantSummary.LoadAllAsync(_store, cancellationToken).ConfigureAwait(false);
            return Page();
        }

        if (!await LoadTenantAsync(cancellationToken).ConfigureAwait(false))
        {
            TempData[ErrorKey] = "Spielplan wurde nicht gefunden.";
            // Clear the ambient {id}; otherwise this redirects to itself forever.
            return RedirectToPage("/Appointment/Termine", new { id = (string?)null });
        }

        return Page();
    }

    public IActionResult OnPostSetClub()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Appointment/Termine");
        }

        TermineClubCookie.Write(Response, ActingClubId);
        return RedirectToPage("/Appointment/Termine", new { id = Id });
    }

    public async Task<IActionResult> OnPostOfferAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Appointment/Termine");
        }

        var clubId = TermineClubCookie.Read(Request);
        var dates = new HashSet<string>(SelectedDates ?? [], StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(PlusDate))
        {
            dates.Add(PlusDate);
        }

        if (string.IsNullOrWhiteSpace(clubId))
        {
            TempData[ErrorKey] = "Bitte zuerst Ihre Mannschaft wählen.";
            return RedirectToPage("/Appointment/Termine", new { id = Id });
        }

        var result = await _store.OfferSlotsAsync(Id, clubId, dates, Time ?? "", cancellationToken).ConfigureAwait(false);
        TempData[result.Ok ? StatusKey : ErrorKey] = result.Ok
            ? (dates.Count == 1 ? "Heimtermin wurde angeboten." : dates.Count + " Heimtermine wurden angeboten.")
            : result.Error;
        return RedirectToPage("/Appointment/Termine", new { id = Id });
    }

    public async Task<IActionResult> OnPostBookAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Appointment/Termine");
        }

        var clubId = TermineClubCookie.Read(Request);
        if (string.IsNullOrWhiteSpace(clubId) || !TrySplitKey(BookKey, out var homeClubId, out var date, out var time))
        {
            TempData[ErrorKey] = "Bitte zuerst Ihre Mannschaft wählen.";
            return RedirectToPage("/Appointment/Termine", new { id = Id });
        }

        var result = await _store.BookGuestAsync(Id, homeClubId, date, time, clubId, cancellationToken).ConfigureAwait(false);
        TempData[result.Ok ? StatusKey : ErrorKey] = result.Ok
            ? "Termin am " + TermineDates.FormatWithWeekday(date) + " wurde verbindlich gebucht."
            : result.Error;
        return RedirectToPage("/Appointment/Termine", new { id = Id });
    }

    public async Task<IActionResult> OnPostWithdrawAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return RedirectToPage("/Appointment/Termine");
        }

        var clubId = TermineClubCookie.Read(Request);
        if (string.IsNullOrWhiteSpace(clubId) || !TrySplitKey(WithdrawKey, out var homeClubId, out var date, out var time))
        {
            return RedirectToPage("/Appointment/Termine", new { id = Id });
        }

        if (!string.Equals(clubId, homeClubId, StringComparison.OrdinalIgnoreCase))
        {
            TempData[ErrorKey] = "Nur die Heimmannschaft kann ihren Terminvorschlag zurückziehen.";
            return RedirectToPage("/Appointment/Termine", new { id = Id });
        }

        var result = await _store.WithdrawOfferAsync(Id, homeClubId, date, time, cancellationToken).ConfigureAwait(false);
        TempData[result.Ok ? StatusKey : ErrorKey] = result.Ok
            ? "Terminvorschlag wurde zurückgezogen."
            : result.Error;
        return RedirectToPage("/Appointment/Termine", new { id = Id });
    }

    private async Task<bool> LoadTenantAsync(CancellationToken cancellationToken)
    {
        Tenant = await _store.GetByIdAsync(Id!, cancellationToken).ConfigureAwait(false);
        if (Tenant is null)
        {
            return false;
        }

        ActingClubOptions = TermineRules.ActingClubs(Tenant);
        var cookieClub = TermineClubCookie.Read(Request);
        SelectedClubId = ActingClubOptions.Any(club =>
            string.Equals(club.Id, cookieClub, StringComparison.OrdinalIgnoreCase))
            ? cookieClub
            : null;
        ActingClubId = SelectedClubId;
        Summary = TermineTenantSummary.From(Tenant);
        Matrix = new TermineMatrixView(Tenant, SelectedClubId, isAdmin: false);

        if (SelectedClubId is not null)
        {
            foreach (var match in Tenant.Matches)
            {
                var scheduled = TermineRules.IsMatchScheduled(Tenant, match.Id);
                if (string.Equals(match.HomeClubId, SelectedClubId, StringComparison.OrdinalIgnoreCase))
                {
                    MyHomeTotal++;
                    MyHomeScheduled += scheduled ? 1 : 0;
                }
                else if (string.Equals(match.GuestClubId, SelectedClubId, StringComparison.OrdinalIgnoreCase))
                {
                    MyAwayTotal++;
                    MyAwayScheduled += scheduled ? 1 : 0;
                }
            }
        }

        ViewData["Title"] = Tenant.Name;
        return true;
    }

    private void ReadFlash()
    {
        StatusMessage = TempData[StatusKey] as string;
        ErrorMessage = TempData[ErrorKey] as string;
    }

    /// <summary>"home|date|time" of a slot (a home team can have several per day); the time is optional.</summary>
    private static bool TrySplitKey(string? key, out string homeClubId, out string date, out string? time)
    {
        homeClubId = "";
        date = "";
        time = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split('|', 3);
        if (parts.Length < 2)
        {
            return false;
        }

        homeClubId = parts[0];
        date = parts[1];
        time = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null;
        return homeClubId.Length > 0 && date.Length > 0;
    }
}
