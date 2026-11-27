namespace CloudServices.Pages.Appointment;

/// <summary>
/// "Ihre Mannschaft": the Mannschaft a visitor acts for. Terminplanung and Termineingabe share it,
/// so switching between the two views keeps the selection.
/// </summary>
public static class TermineClubCookie
{
    public const string Name = "ttkv-club";

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static void Write(HttpResponse response, string? clubId)
    {
        var options = new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(365)
        };

        if (string.IsNullOrWhiteSpace(clubId))
        {
            response.Cookies.Delete(Name, options);
            return;
        }

        response.Cookies.Append(Name, clubId, options);
    }
}
