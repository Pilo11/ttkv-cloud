using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudServices.Services.Appointment;

/// <summary>
/// The Termin typed in the Termineingabe: "14.01.2027 19:30", "Do, 14.1.27 19.30 Uhr" or the
/// datetime-local value "2027-01-14T19:30". Date and time are both required; an empty text clears
/// the Termin. wwwroot/js/termineingabe.js checks the same formats with the same messages, keep
/// both in step.
/// </summary>
public static partial class TermineingabeInput
{
    public const string FormatError =
        "Bitte Datum und Uhrzeit im Format TT.MM.JJJJ HH:MM eingeben, z. B. 14.01.2027 19:30.";

    public const string MissingTimeError = "Bitte auch die Uhrzeit angeben, z. B. 14.01.2027 19:30.";

    private static readonly string[] Weekdays =
        ["Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag"];

    public static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text);

    public static bool TryParse(string? text, out DateOnly date, out TimeOnly time, out string error)
    {
        date = default;
        time = default;
        error = FormatError;
        var normalized = Whitespace().Replace(text ?? "", " ").Trim();
        var match = Pattern().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        var day = Number(match, "d");
        var month = Number(match, "m");
        var yearText = match.Groups["y"].Value;
        var year = Number(match, "y") + (yearText.Length == 2 ? 2000 : 0);
        var dateText = $"{day:00}.{month:00}.{year:0000}";
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            error = $"Den {dateText} gibt es nicht.";
            return false;
        }

        date = new DateOnly(year, month, day);
        var weekday = TermineCalendar.MondayIndex(date);
        if (match.Groups["wd"].Success)
        {
            var typed = WeekdayIndex(match.Groups["wd"].Value);
            if (typed < 0)
            {
                return false;
            }

            if (typed != weekday)
            {
                error = $"Der {dateText} ist ein {Weekdays[weekday]}, kein {Weekdays[typed]}.";
                return false;
            }
        }

        if (!match.Groups["h"].Success)
        {
            error = MissingTimeError;
            return false;
        }

        var hour = Number(match, "h");
        var minute = Number(match, "min");
        if (hour > 23 || minute > 59)
        {
            error = $"Die Uhrzeit {hour:00}:{match.Groups["min"].Value} gibt es nicht.";
            return false;
        }

        time = new TimeOnly(hour, minute);
        error = "";
        return true;
    }

    /// <summary>"14.01.2027 19:30", the text the input shows; empty while the Begegnung has no Termin.</summary>
    public static string Display(TermineSlot? slot) =>
        slot is null ? "" : TermineDates.FormatDisplay(slot.Date) + " " + slot.Time;

    /// <summary>"2027-01-14T19:30", the datetime-local value; also tells the server which Termin the page saw.</summary>
    public static string Stamp(TermineSlot? slot) =>
        slot is null ? "" : slot.Date + "T" + slot.Time;

    /// <summary>"Do".</summary>
    public static string Weekday(TermineSlot? slot) =>
        slot is null ? "" : TermineDates.FormatWeekday(slot.Date);

    private static int Number(Match match, string group) =>
        int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>"Mo", "mo.", "Montag" → 0. At least two letters.</summary>
    private static int WeekdayIndex(string typed)
    {
        if (typed.Length < 2)
        {
            return -1;
        }

        return Array.FindIndex(Weekdays, name => name.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Optional weekday, then d.m.yyyy / d.m.yy or yyyy-mm-dd, then optionally ("," | " " | "T") [um] H:mm | H.mm [Uhr].
    [GeneratedRegex(
        @"^(?:(?<wd>[a-zäöüß]+)\.?\s*,?\s*)?(?:(?<d>[0-9]{1,2})\.(?<m>[0-9]{1,2})\.(?<y>[0-9]{4}|[0-9]{2})|(?<y>[0-9]{4})-(?<m>[0-9]{1,2})-(?<d>[0-9]{1,2}))(?:(?:\s*,\s*|\s+|t)(?:um\s+)?(?<h>[0-9]{1,2})[:.](?<min>[0-9]{2})(?:\s*uhr)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
