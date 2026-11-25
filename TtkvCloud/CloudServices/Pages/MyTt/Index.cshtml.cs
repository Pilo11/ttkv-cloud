using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CloudServices.Pages.MyTt;

/// <summary>
/// myTT-Verwaltung (click-TT admin in the iframe), for the Staffelleitung only: it answers under the
/// secret path /staffelleitung/{key}/mytt; any other key, and the old /mytt, is a plain 404.
/// </summary>
public class IndexModel : PageModel
{
    private readonly StaffelleitungAccess _access;

    public IndexModel(StaffelleitungAccess access)
    {
        _access = access;
    }

    public string Key => RouteData.Values["key"] as string ?? "";

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

        // Secret area: the layout adds noindex and no-referrer, so the click-TT iframe never gets
        // the key as its Referer or document.referrer.
        ViewData["Staffelleitung"] = true;
        await next().ConfigureAwait(false);
    }

    public void OnGet()
    {
    }
}
