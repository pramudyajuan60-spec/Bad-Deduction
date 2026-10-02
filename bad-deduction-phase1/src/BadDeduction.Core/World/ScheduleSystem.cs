using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;

namespace BadDeduction.World;

/// <summary>Reads schedules. Movement along the schedule (and interruptions) is Phase 5.</summary>
public sealed class ScheduleSystem
{
    private readonly GameState _state;
    private readonly ContentDatabase _content;

    public ScheduleSystem(GameState state, ContentDatabase content)
    {
        _state = state;
        _content = content;
    }

    public Schedule GetSchedule(string characterId) =>
        _state.World.Schedules.TryGetValue(characterId, out var s)
            ? s
            : throw new KeyNotFoundException($"Character '{characterId}' has no schedule.");

    /// <summary>The block that applies at a game time (blocks tile the whole day, so one always exists).</summary>
    public ScheduleBlock BlockAt(string characterId, GameTime time)
    {
        var blocks = GetSchedule(characterId).ForDay(time.Day);
        var minute = time.MinuteOfDay;
        foreach (var b in blocks)
            if (minute >= b.StartMinute && minute < b.EndMinute) return b;
        throw new InvalidOperationException($"Schedule of '{characterId}' does not cover minute {minute}.");
    }

    /// <summary>Puts every scheduled character at the place and activity their schedule says for the current time.</summary>
    public void PlaceAtScheduledPositions()
    {
        var now = new GameTime(_state.TotalMinutes);
        foreach (var (id, c) in _state.World.Characters)
        {
            if (!c.IsAlive || !_state.World.Schedules.ContainsKey(id)) continue;
            var block = BlockAt(id, now);
            c.CurrentLocationId = block.LocationId;
            c.Activity = block.Activity;
        }
    }

    /// <summary>Returns every problem with a schedule (empty when valid). Used by the validator and tests.</summary>
    public static IReadOnlyList<string> Check(string characterId, string homeId, Schedule schedule, ContentDatabase content)
    {
        var errors = new List<string>();
        CheckBlocks($"{characterId}/workday", homeId, schedule.Workday, content, errors);
        if (schedule.DayOffIndex is < -1 or > 6) errors.Add($"{characterId}: DayOffIndex {schedule.DayOffIndex} out of range.");
        if (schedule.DayOffIndex >= 0) CheckBlocks($"{characterId}/dayoff", homeId, schedule.DayOff, content, errors);
        return errors;
    }

    private static void CheckBlocks(string label, string homeId, List<ScheduleBlock> blocks, ContentDatabase content, List<string> errors)
    {
        if (blocks.Count == 0) { errors.Add($"{label}: no blocks."); return; }
        if (blocks[0].StartMinute != 0) errors.Add($"{label}: first block must start at 0.");
        if (blocks[^1].EndMinute != GameTime.MinutesPerDay) errors.Add($"{label}: last block must end at midnight.");

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.EndMinute <= b.StartMinute) errors.Add($"{label}: block {i} is empty or reversed.");
            if (!content.HasLocation(b.LocationId)) { errors.Add($"{label}: block {i} has unknown location '{b.LocationId}'."); continue; }
            if (i > 0 && blocks[i - 1].EndMinute != b.StartMinute) errors.Add($"{label}: gap or overlap before block {i}.");

            // Travel to the next block happens inside this one, so it must fit.
            var nextLoc = i + 1 < blocks.Count ? blocks[i + 1].LocationId : blocks[0].LocationId;
            if (content.HasLocation(nextLoc) && b.LocationId != nextLoc
                && b.EndMinute - b.StartMinute < content.TravelMinutes(b.LocationId, nextLoc))
                errors.Add($"{label}: block {i} at {b.LocationId} is too short to travel to {nextLoc}.");
        }

        // The day must loop: the character ends the day where the next day begins (home, asleep).
        if (blocks[0].LocationId != homeId) errors.Add($"{label}: the day must start at home.");
        if (blocks[^1].LocationId != blocks[0].LocationId) errors.Add($"{label}: the day must end where it starts.");
    }
}
