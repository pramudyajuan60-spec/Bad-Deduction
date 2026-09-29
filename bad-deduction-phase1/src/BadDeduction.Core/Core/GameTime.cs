namespace BadDeduction.Core;

public enum TimeOfDay { Night, Morning, Afternoon, Evening }

/// <summary>
/// Immutable point in game time, stored as minutes since Day 1, 00:00.
/// Only the raw minute count is ever persisted; everything else is derived.
/// </summary>
public readonly record struct GameTime(long TotalMinutes) : IComparable<GameTime>
{
    public const int MinutesPerHour = 60;
    public const int MinutesPerDay = 24 * MinutesPerHour;

    /// <summary>1-based day number.</summary>
    public int Day => (int)(TotalMinutes / MinutesPerDay) + 1;
    public int MinuteOfDay => (int)(TotalMinutes % MinutesPerDay);
    public int Hour => MinuteOfDay / MinutesPerHour;
    public int Minute => MinuteOfDay % MinutesPerHour;

    public TimeOfDay Phase => Hour switch
    {
        >= 22 or < 5 => TimeOfDay.Night,
        < 12 => TimeOfDay.Morning,
        < 17 => TimeOfDay.Afternoon,
        _ => TimeOfDay.Evening,
    };

    public static GameTime At(int day, int hour, int minute = 0)
    {
        if (day < 1) throw new ArgumentOutOfRangeException(nameof(day));
        if (hour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(hour));
        if (minute is < 0 or > 59) throw new ArgumentOutOfRangeException(nameof(minute));
        return new GameTime((long)(day - 1) * MinutesPerDay + hour * MinutesPerHour + minute);
    }

    public GameTime AddMinutes(long minutes)
    {
        var total = TotalMinutes + minutes;
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(minutes), "Time cannot go before Day 1, 00:00.");
        return new GameTime(total);
    }

    public int CompareTo(GameTime other) => TotalMinutes.CompareTo(other.TotalMinutes);

    public static bool operator <(GameTime a, GameTime b) => a.TotalMinutes < b.TotalMinutes;
    public static bool operator >(GameTime a, GameTime b) => a.TotalMinutes > b.TotalMinutes;
    public static bool operator <=(GameTime a, GameTime b) => a.TotalMinutes <= b.TotalMinutes;
    public static bool operator >=(GameTime a, GameTime b) => a.TotalMinutes >= b.TotalMinutes;

    public override string ToString() => $"Day {Day} {Hour:00}:{Minute:00}";
}
