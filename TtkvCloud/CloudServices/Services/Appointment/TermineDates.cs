using System.Globalization;

namespace CloudServices.Services.Appointment;

public static class TermineDates
{
    public static List<string> Generate(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var list = new List<string>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            list.Add(ToIso(day));
        }

        return list;
    }

    public static string ToIso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FormatDisplay(string iso)
    {
        return TryParse(iso, out var date)
            ? date.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE"))
            : iso;
    }

    /// <summary>Fixed date scope of a Spielplan; falls back to first/last Spieltag for older documents.</summary>
    public static bool TryGetRange(TermineTenantDocument tenant, out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;
        if (TryParse(tenant.RangeFrom, out from) && TryParse(tenant.RangeTo, out to))
        {
            if (to < from)
            {
                (from, to) = (to, from);
            }

            return true;
        }

        var dates = tenant.Dates
            .Select(value => TryParse(value, out var date) ? date : (DateOnly?)null)
            .Where(date => date is not null)
            .Select(date => date!.Value)
            .ToList();
        if (dates.Count == 0)
        {
            return false;
        }

        from = dates.Min();
        to = dates.Max();
        return true;
    }

    /// <summary>"Di" for a Tuesday.</summary>
    public static string FormatWeekday(string iso) =>
        TryParse(iso, out var date) ? TermineCalendar.WeekdayShortNames[TermineCalendar.MondayIndex(date)] : "";

    /// <summary>"12.01." without the year, for dense tables.</summary>
    public static string FormatDayMonth(string iso) =>
        TryParse(iso, out var date) ? date.ToString("dd.MM.", CultureInfo.InvariantCulture) : iso;

    /// <summary>"Di, 12.01.2027".</summary>
    public static string FormatWithWeekday(string iso) =>
        TryParse(iso, out var date) ? FormatWeekday(iso) + ", " + FormatDisplay(iso) : iso;

    /// <summary>"Januar 2027".</summary>
    public static string FormatMonthYear(string iso) =>
        TryParse(iso, out var date) ? date.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("de-DE")) : "";

    public static bool IsWeekend(string iso) =>
        TryParse(iso, out var date) && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static bool TryParse(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParseExact(trimmed, "dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out date)
            || DateOnly.TryParse(trimmed, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out date);
    }

    public static bool TryParseTime(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (TimeOnly.TryParseExact(trimmed, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || TimeOnly.TryParse(trimmed, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.None, out time))
        {
            normalized = time.ToString("HH:mm", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }
}
