using BadDeduction.Characters;

namespace BadDeduction.World;

/// <summary>
/// Where a character is supposed to be during [StartMinute, EndMinute) of a day.
/// StartMinute is the *arrival deadline*: the character leaves the previous block early
/// enough to be here on time (travel happens at the tail of the previous block).
/// </summary>
public sealed class ScheduleBlock
{
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public string LocationId { get; set; } = "";
    public Activity Activity { get; set; }
}

public sealed class Schedule
{
    public List<ScheduleBlock> Workday { get; set; } = new();
    public List<ScheduleBlock> DayOff { get; set; } = new();

    /// <summary>Days with (day - 1) % 7 == DayOffIndex use the DayOff blocks. -1 means no day off.</summary>
    public int DayOffIndex { get; set; } = -1;

    public bool IsDayOff(int day) => DayOffIndex >= 0 && (day - 1) % 7 == DayOffIndex;

    public IReadOnlyList<ScheduleBlock> ForDay(int day) => IsDayOff(day) ? DayOff : Workday;
}
