namespace HomeNode.Services;

// The stale-issue bound for sealed intake, as one named
// configuration value in one place.
//
//   Configuration key:  Intake:MaxPacketAgeDays
//   Shipped default:    7 (days), set in appsettings.json
//   Disabled value:     the word off, or 0 (or any non-positive
//                       number); a missing or unreadable value
//                       falls back to the shipped default, never
//                       to disabled.
//
// Why days, and why seven: the bound exists to retire packets
// issued in a previous era of the record, for example a packet
// captured before a reset and replayed long after. Legitimate
// store-and-forward traffic is never old: the relay holds a packet
// for minutes (its own window is 300 seconds) and a stand-in's
// local queue drains on its next session, so even a household
// member travelling for a weekend lands far inside seven days.
// A bound tight enough to endanger real traffic (hours) would buy
// nothing; a bound of days still closes the replay-an-era-later
// hole. Operators tune it in appsettings.json or with the
// Intake__MaxPacketAgeDays environment variable, and DEPLOY.md
// carries the same note.
public sealed class PacketAgeBound
{
    public const string ConfigKey = "Intake:MaxPacketAgeDays";
    public const int DefaultMaxAgeDays = 7;

    // Null means the bound is disabled and intake behaves exactly
    // as it did before the bound existed.
    public TimeSpan? MaxAge { get; }

    private PacketAgeBound(TimeSpan? maxAge) => MaxAge = maxAge;

    public static readonly PacketAgeBound ShippedDefault = new(TimeSpan.FromDays(DefaultMaxAgeDays));
    public static readonly PacketAgeBound Disabled = new(null);

    public static PacketAgeBound FromDays(int days) =>
        days <= 0 ? Disabled : new PacketAgeBound(TimeSpan.FromDays(days));

    public static PacketAgeBound FromTimeSpan(TimeSpan maxAge) => new(maxAge);

    public static PacketAgeBound FromConfiguration(IConfiguration config)
    {
        var raw = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw)) return ShippedDefault;
        if (raw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) return Disabled;
        return int.TryParse(raw.Trim(), out var days) ? FromDays(days) : ShippedDefault;
    }

    // True when a packet issued at the given unix time is older
    // than the bound at receipt time. A packet dated in the future
    // is never stale by this rule (clock skew is not an age).
    public bool Rejects(long issuedAtUnixSeconds, DateTime utcNow)
    {
        if (MaxAge is null) return false;
        var issued = DateTimeOffset.FromUnixTimeSeconds(issuedAtUnixSeconds).UtcDateTime;
        return utcNow - issued > MaxAge.Value;
    }

    public string Describe() => MaxAge is null
        ? "off"
        : MaxAge.Value.TotalDays >= 1
            ? $"{MaxAge.Value.TotalDays:0.###} day(s)"
            : $"{MaxAge.Value.TotalMinutes:0.###} minute(s)";
}
