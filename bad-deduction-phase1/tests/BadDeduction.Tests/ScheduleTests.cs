using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Tests.Harness;
using BadDeduction.World;

namespace BadDeduction.Tests;

public sealed class ScheduleTests
{
    [Fact]
    public void Every_generated_schedule_tiles_the_day_and_is_travel_feasible()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var (id, schedule) in s.State.World.Schedules)
            {
                var errors = ScheduleSystem.Check(id, s.State.World.Characters[id].HomeLocationId, schedule, s.Content);
                Assert.Equal(0, errors.Count, $"seed {seed}: {string.Join("; ", errors)}");
            }
        }
    }

    [Fact]
    public void Workers_are_at_work_during_their_shift_and_asleep_at_home_at_night()
    {
        var s = CastSupport.NewCastSession(21);
        foreach (var c in s.State.World.Characters.Values)
        {
            var occ = s.Content.GetOccupation(c.OccupationId);
            for (var day = 1; day <= 7; day++)
            {
                var schedule = s.State.World.Schedules[c.Id];
                var at3 = s.Schedules.BlockAt(c.Id, GameTime.At(day, 3));
                Assert.Equal(c.HomeLocationId, at3.LocationId);
                Assert.Equal(Activity.Sleeping, at3.Activity);

                var midShift = GameTime.At(day, (occ.ShiftStartHour + occ.ShiftEndHour) / 2);
                var block = s.Schedules.BlockAt(c.Id, midShift);
                if (schedule.IsDayOff(day)) Assert.True(block.Activity != Activity.Working, "no work on a day off");
                else
                {
                    Assert.Equal(c.WorkLocationId, block.LocationId);
                    Assert.Equal(Activity.Working, block.Activity);
                }
            }
        }
    }

    [Fact]
    public void Police_have_no_day_off_and_the_station_is_always_staffed_at_midday_and_evening()
    {
        for (ulong seed = 1; seed <= 25; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var c in s.State.World.Characters.Values.Where(c => c.Kind == CharacterKind.Police))
                Assert.Equal(-1, s.State.World.Schedules[c.Id].DayOffIndex);

            foreach (var hour in new[] { 10, 12, 16, 20 })
            {
                var at = GameTime.At(3, hour);
                var staffed = s.State.World.Characters.Values.Any(c =>
                    c.Kind == CharacterKind.Police && s.Schedules.BlockAt(c.Id, at).LocationId == "loc_guard_station");
                Assert.True(staffed, $"seed {seed}: nobody at the guard station at {hour}:00");
            }
        }
    }

    [Fact]
    public void Some_civilians_take_a_day_off_within_the_week_and_go_out_somewhere()
    {
        var s = CastSupport.NewCastSession(13);
        var civilians = s.State.World.Characters.Values.Where(c => c.Kind == CharacterKind.Civilian).ToList();
        Assert.True(civilians.All(c => s.State.World.Schedules[c.Id].DayOffIndex is >= 0 and <= 6));
        Assert.True(civilians.Select(c => s.State.World.Schedules[c.Id].DayOffIndex).Distinct().Count() >= 3, "days off should be spread out");
        Assert.True(civilians.Any(c => s.State.World.Schedules[c.Id].DayOff.Any(b => b.Activity == Activity.Socializing)));
    }

    [Fact]
    public void Characters_start_where_their_schedule_says_at_run_start()
    {
        var s = CastSupport.NewCastSession(2);
        var now = new GameTime(s.State.TotalMinutes);
        foreach (var c in s.State.World.Characters.Values)
        {
            var block = s.Schedules.BlockAt(c.Id, now);
            Assert.Equal(block.LocationId, c.CurrentLocationId);
            Assert.Equal(block.Activity, c.Activity);
        }
    }

    [Fact]
    public void Schedule_validation_catches_bad_schedules()
    {
        var s = CastSupport.NewCastSession(2);
        var id = s.State.World.Characters.Keys.First();
        var home = s.State.World.Characters[id].HomeLocationId;
        Schedule Clone() => System.Text.Json.JsonSerializer.Deserialize<Schedule>(System.Text.Json.JsonSerializer.Serialize(s.State.World.Schedules[id]))!;

        var gap = Clone();
        gap.Workday[1].StartMinute += 5;
        Assert.True(ScheduleSystem.Check(id, home, gap, s.Content).Any(e => e.Contains("gap")));

        var wrongEnd = Clone();
        wrongEnd.Workday[^1].LocationId = "loc_church";
        Assert.True(ScheduleSystem.Check(id, home, wrongEnd, s.Content).Any(e => e.Contains("end where it starts")));

        var ghost = Clone();
        ghost.Workday[0].LocationId = "loc_nowhere";
        Assert.True(ScheduleSystem.Check(id, home, ghost, s.Content).Any(e => e.Contains("unknown location")));

        // Teleporting: a 1-minute stop far from the next place cannot be travelled in time.
        var tooShort = Clone();
        var work = tooShort.Workday.First(b => b.Activity == Activity.Working);
        var idx = tooShort.Workday.IndexOf(work);
        work.EndMinute = work.StartMinute + 1;
        tooShort.Workday[idx + 1].StartMinute = work.EndMinute;
        Assert.True(ScheduleSystem.Check(id, home, tooShort, s.Content).Any(e => e.Contains("too short to travel")));
    }

    [Fact]
    public void Workers_only_leave_after_their_shift_has_ended()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var c in s.State.World.Characters.Values)
            {
                var occ = s.Content.GetOccupation(c.OccupationId);
                var blocks = s.State.World.Schedules[c.Id].Workday;
                var i = blocks.FindIndex(b => b.Activity == Activity.Working);
                var work = blocks[i];
                var next = blocks[i + 1];
                var departure = work.EndMinute - s.Content.TravelMinutes(work.LocationId, next.LocationId);
                Assert.True(work.StartMinute <= occ.ShiftStartHour * 60, $"seed {seed}: {c.Id} arrives late");
                Assert.True(departure >= occ.ShiftEndHour * 60, $"seed {seed}: {c.Id} leaves work at {departure} before shift end");
            }
        }
    }
}
