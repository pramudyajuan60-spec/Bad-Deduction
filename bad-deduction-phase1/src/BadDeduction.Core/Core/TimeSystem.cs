namespace BadDeduction.Core;

/// <summary>
/// Advances game time one minute at a time so that every consumer (schedules, rumors, police
/// patrols, background simulation in Phase 5) sees each boundary exactly once and in order.
/// Per step the order is: DayChanged, TimeOfDayChanged, HourChanged, MinuteElapsed.
/// Do not call <see cref="Advance"/> from inside an event handler.
/// </summary>
public sealed class TimeSystem
{
    private readonly GameState _state;
    private readonly EventSystem _events;

    public TimeSystem(GameState state, EventSystem events)
    {
        _state = state;
        _events = events;
    }

    public GameTime Now => new(_state.TotalMinutes);

    public void Advance(int minutes)
    {
        if (minutes < 0) throw new ArgumentOutOfRangeException(nameof(minutes), "Time only moves forward.");
        for (var i = 0; i < minutes; i++) Step();
    }

    public void AdvanceTo(GameTime target)
    {
        var delta = target.TotalMinutes - _state.TotalMinutes;
        if (delta < 0) throw new ArgumentOutOfRangeException(nameof(target), "Cannot move time backwards.");
        Advance(checked((int)delta));
    }

    private void Step()
    {
        var before = new GameTime(_state.TotalMinutes);
        _state.TotalMinutes++;
        var now = new GameTime(_state.TotalMinutes);

        if (now.Day != before.Day)
        {
            _events.Record(WorldEventTypes.DayStarted,
                data: new Dictionary<string, string> { ["day"] = now.Day.ToString() });
            _events.Publish(new DayChanged(now, now.Day));
        }
        if (now.Phase != before.Phase)
            _events.Publish(new TimeOfDayChanged(now, before.Phase, now.Phase));
        if (now.Hour != before.Hour)
            _events.Publish(new HourChanged(now));
        _events.Publish(new MinuteElapsed(now));
    }
}
