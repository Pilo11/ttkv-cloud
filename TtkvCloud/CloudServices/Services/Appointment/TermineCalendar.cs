using System.Globalization;

namespace CloudServices.Services.Appointment;

public sealed class TermineCalendarDay
{
    public required string Iso { get; init; }

    public int Day { get; init; }

    /// <summary>0 = Monday … 6 = Sunday.</summary>
    public int WeekdayIndex { get; init; }

    public bool IsWeekend => WeekdayIndex >= 5;

    /// <summary>Inside the Spielplan's Von–Bis scope.</summary>
    public bool InRange { get; init; }

    /// <summary>Gesperrt by the Staffelleitung.</summary>
    public bool IsBlocked { get; init; }

    /// <summary>A Termin already exists on this day.</summary>
    public bool HasTermine { get; init; }

    public required string Label { get; init; }
}

public sealed class TermineCalendarMonth
{
    public required string Title { get; init; }

    /// <summary>Empty cells before day 1 in a Monday-first week.</summary>
    public int LeadingBlanks { get; init; }

    public required IReadOnlyList<TermineCalendarDay> Days { get; init; }
}

/// <summary>Month grids for the Spielplan scope, used to mark gesperrte Tage.</summary>
public static class TermineCalendar
{
    public static readonly IReadOnlyList<string> WeekdayShortNames = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static IReadOnlyList<TermineCalendarMonth> Build(
        DateOnly from,
        DateOnly to,
        IReadOnlySet<string> blockedDates,
        IReadOnlySet<string> datesWithTermine)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var months = new List<TermineCalendarMonth>();
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var days = new List<TermineCalendarDay>();
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            for (var day = 1; day <= daysInMonth; day++)
            {
                var date = new DateOnly(month.Year, month.Month, day);
                var iso = TermineDates.ToIso(date);
                var inRange = date >= from && date <= to;
                days.Add(new TermineCalendarDay
                {
                    Iso = iso,
                    Day = day,
                    WeekdayIndex = MondayIndex(date),
                    InRange = inRange,
                    IsBlocked = inRange && blockedDates.Contains(iso),
                    HasTermine = datesWithTermine.Contains(iso),
                    Label = date.ToString("dddd, dd.MM.yyyy", German)
                });
            }

            months.Add(new TermineCalendarMonth
            {
                Title = month.ToString("MMMM yyyy", German),
                LeadingBlanks = MondayIndex(month),
                Days = days
            });
        }

        return months;
    }

    public static int MondayIndex(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;
}
