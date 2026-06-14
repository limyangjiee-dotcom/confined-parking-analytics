namespace ParkingApiPg.Models;

// Parking tariff used by the gate fee calculation. Values are configurable
// from the platform's Settings (SettingsService pushes them in at startup
// and whenever settings are saved) — defaults below match the calibration:
// free first 15 min; weekday RM2 for the first 3 hours, +RM1 for hour 3-4,
// +RM2.50/hour after; flat RM2 on Friday / weekend.
public static class Tariff
{
    public static int FreeMinutes = 15;
    public static int PaymentGraceMinutes = 15;      // time to reach the exit after paying
    public static decimal WeekdayBase = 2m;          // first 3 hours, weekday
    public static decimal Hour4Add = 1m;             // hour 3-4
    public static decimal AfterHourRate = 2.5m;      // per hour after the 4th
    public static decimal WeekendFlat = 2m;          // Fri / Sat / Sun flat

    public static decimal FeeFor(DateTime entry, DateTime exit)
    {
        var minutes = (exit - entry).TotalMinutes;
        if (minutes <= FreeMinutes) return 0m;

        if (entry.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday)
            return WeekendFlat;

        var hours = (int)Math.Ceiling((exit - entry).TotalHours);
        if (hours <= 3) return WeekdayBase;
        if (hours <= 4) return WeekdayBase + Hour4Add;
        return WeekdayBase + Hour4Add + AfterHourRate * (hours - 4);
    }
}
