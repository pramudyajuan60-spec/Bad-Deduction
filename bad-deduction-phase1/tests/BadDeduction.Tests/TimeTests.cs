using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class TimeTests
{
    [Fact]
    public void GameTime_derives_day_hour_minute_and_phase()
    {
        var t = GameTime.At(3, 18, 42);
        Assert.Equal(3, t.Day);
        Assert.Equal(18, t.Hour);
        Assert.Equal(42, t.Minute);
        Assert.Equal(TimeOfDay.Evening, t.Phase);
        Assert.Equal("Day 3 18:42", t.ToString());
        Assert.Equal(TimeOfDay.Night, GameTime.At(1, 2).Phase);
        Assert.Equal(TimeOfDay.Morning, GameTime.At(1, 8).Phase);
        Assert.Equal(TimeOfDay.Afternoon, GameTime.At(1, 14).Phase);
    }

    [Fact]
    public void GameTime_rejects_invalid_input()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameTime.At(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameTime.At(1, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameTime.At(1, 0).AddMinutes(-1));
    }

    [Fact]
    public void Advancing_across_midnight_fires_day_change_once_and_logs_it()
    {
        var s = TestSupport.NewPopulatedSession();
        s.Time.AdvanceTo(GameTime.At(1, 23, 50));

        var dayChanges = new List<int>();
        s.Events.Subscribe<DayChanged>(e => dayChanges.Add(e.Day));

        s.Time.Advance(20); // 23:50 -> 00:10 next day
        Assert.SequenceEqual(new[] { 2 }, dayChanges);
        Assert.Equal(GameTime.At(2, 0, 10), s.Time.Now);
        Assert.Equal(1, s.Events.Query(WorldEventTypes.DayStarted).Count(e => e.Data["day"] == "2"));
    }

    [Fact]
    public void Hour_and_phase_boundaries_fire_in_order()
    {
        var s = TestSupport.NewPopulatedSession();
        s.Time.AdvanceTo(GameTime.At(1, 11, 58));
        var log = new List<string>();
        s.Events.Subscribe<TimeOfDayChanged>(e => log.Add($"phase:{e.Current}"));
        s.Events.Subscribe<HourChanged>(e => log.Add($"hour:{e.Time.Hour}"));
        s.Events.Subscribe<MinuteElapsed>(e => { if (e.Time.Minute == 0) log.Add("minute:00"); });

        s.Time.Advance(3); // 11:58 -> 12:01
        Assert.SequenceEqual(new[] { "phase:Afternoon", "hour:12", "minute:00" }, log);
    }

    [Fact]
    public void Time_cannot_move_backwards()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Time.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Time.AdvanceTo(GameTime.At(1, 1)));
    }

    [Fact]
    public void Seven_days_advance_produces_seven_day_boundaries()
    {
        var s = GameSession.NewRun(1, Campaign.Malvr, Difficulty.Easy, TestSupport.LoadContent(), GameTime.At(1, 0));
        s.Time.AdvanceTo(GameTime.At(8, 0));
        Assert.Equal(7, s.Events.Query(WorldEventTypes.DayStarted).Count());
    }
}
