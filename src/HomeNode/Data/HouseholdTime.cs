namespace HomeNode.Data;

// Household wall clock rendering: every surface shows record times in
// the household's own zone, in the one format ("Oct 31, 5:49 PM" for a
// stamp, "5:49 PM" for a bare time). Stored instants stay UTC; only
// the rendering converts.
public static class HouseholdTime
{
    public static TimeZoneInfo ZoneFor(string? timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId ?? "America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
    }

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    public static string Stamp(DateTime utc, TimeZoneInfo zone) =>
        ToLocal(utc, zone).ToString("MMM d, h:mm tt");

    public static string Time(DateTime utc, TimeZoneInfo zone) =>
        ToLocal(utc, zone).ToString("h:mm tt");

    // A bare time for today, a dated stamp for any other day: a
    // list that mixes days never leaves the reader to guess which
    // day a bare time belongs to.
    public static string TimeOrDate(DateTime utc, TimeZoneInfo zone)
    {
        var local = ToLocal(utc, zone);
        var today = ToLocal(DateTime.UtcNow, zone);
        return local.Date == today.Date
            ? local.ToString("h:mm tt")
            : local.ToString("MMM d, h:mm tt");
    }
}
