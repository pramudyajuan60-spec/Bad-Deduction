using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Tests.Harness;
using BadDeduction.World;

namespace BadDeduction.Tests;

/// <summary>Phase 5: the world tick — tiers, schedule-driven routines, timed travel, witnessing.</summary>
public sealed class WorldSimulationTests
{
    // ------------------------------------------------------------------ helpers

    private static GameSession NewSim(ulong seed = 7) => CastSupport.NewCastSession(seed);

    private static void AdvanceTo(GameSession s, int day, int hour, int minute = 0)
    {
        var delta = GameTime.At(day, hour, minute).TotalMinutes - s.State.TotalMinutes;
        Assert.True(delta >= 0, "test bug: tried to go back in time");
        s.Simulate.Advance((int)delta);
    }

    private static void GiveSchedule(GameSession s, string charId, params (int from, int to, string loc, Activity act)[] defs)
    {
        s.State.World.Schedules[charId] = new Schedule
        {
            Workday = defs.Select(d => new ScheduleBlock
            {
                StartMinute = d.from, EndMinute = d.to, LocationId = d.loc, Activity = d.act,
            }).ToList(),
            DayOffIndex = -1,
        };
    }

    /// <summary>c_merchant: home 00:00-08:00, market 08:00-23:00 (8 min away), home 23:00-24:00.</summary>
    private static void GiveCommuterSchedule(GameSession s)
    {
        GiveSchedule(s, "c_merchant",
            (0, 470, "loc_residential", Activity.Sleeping),
            (470, 480, "loc_residential", Activity.Idle),
            (480, 1380, "loc_central_market", Activity.Working),
            (1380, 1440, "loc_residential", Activity.Idle));
    }

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void Session_exposes_the_simulation()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.True(s.Simulate is not null);
    }

    [Fact]
    public void Advance_rejects_negative_minutes()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Simulate.Advance(-1));
    }

    [Fact]
    public void Simulate_advance_moves_time_and_fires_day_boundaries()
    {
        var s = TestSupport.NewPopulatedSession();
        s.Simulate.Advance(90);
        Assert.Equal(GameTime.At(1, 7, 30).TotalMinutes, s.State.TotalMinutes);
        AdvanceTo(s, 2, 0, 1);
        Assert.True(s.Events.Query(typePrefix: "time.day_started").Any(e => e.Data["day"] == "2"));
    }

    [Fact]
    public void Raw_time_advance_does_not_run_the_simulation()
    {
        // Time.Advance stays sim-free on purpose (ADR-023): existing systems keep working on raw time.
        var s = TestSupport.NewPopulatedSession();
        GiveCommuterSchedule(s);
        s.Time.Advance(112); // to 07:52, when the sim would depart for the market
        Assert.False(s.State.World.ActiveTravels.ContainsKey("c_merchant"));
        Assert.Equal("loc_residential", s.State.World.Characters["c_merchant"].CurrentLocationId);
    }

    // ------------------------------------------------------------------ tiers

    [Fact]
    public void TierOf_is_spotlight_at_the_player_location_and_near_elsewhere()
    {
        var s = TestSupport.NewPopulatedSession(); // player c_player lives at loc_residential
        Assert.Equal(SimulationTier.Spotlight, s.Simulate.TierOf("c_merchant")); // same place
        s.State.World.Characters["c_merchant"].CurrentLocationId = "loc_church";
        Assert.Equal(SimulationTier.Near, s.Simulate.TierOf("c_merchant"));
    }

    [Fact]
    public void TierOf_is_near_for_everyone_when_no_player_is_set()
    {
        var s = NewSim();
        foreach (var id in s.State.World.Characters.Keys)
            Assert.Equal(SimulationTier.Near, s.Simulate.TierOf(id));
    }

    [Fact]
    public void TierOf_throws_for_unknown_characters()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.Throws<KeyNotFoundException>(() => s.Simulate.TierOf("nobody"));
    }

    [Fact]
    public void Background_tier_is_a_reserved_seam_with_daily_cadence()
    {
        Assert.Equal(1, SimulationTiers.CadenceMinutes(SimulationTier.Spotlight));
        Assert.Equal(15, SimulationTiers.CadenceMinutes(SimulationTier.Near));
        Assert.Equal(1440, SimulationTiers.CadenceMinutes(SimulationTier.Background));
        var s = NewSim();
        Assert.False(s.State.World.Characters.Keys.Any(id => s.Simulate.TierOf(id) == SimulationTier.Background));
    }

    [Fact]
    public void Spotlight_characters_react_within_a_minute()
    {
        var s = TestSupport.NewPopulatedSession();
        GiveSchedule(s, "c_merchant", // at the player's location -> Spotlight
            (0, 547, "loc_residential", Activity.Idle),
            (547, 1440, "loc_residential", Activity.Eating));
        AdvanceTo(s, 1, 9, 7);
        Assert.Equal(Activity.Eating, s.State.World.Characters["c_merchant"].Activity);
    }

    [Fact]
    public void Near_characters_react_within_fifteen_minutes()
    {
        var s = TestSupport.NewPopulatedSession();
        s.State.World.Characters["c_rival"].HomeLocationId = "loc_church";
        s.State.World.Characters["c_rival"].CurrentLocationId = "loc_church";
        GiveSchedule(s, "c_rival", // away from the player -> Near
            (0, 547, "loc_church", Activity.Idle),
            (547, 1440, "loc_church", Activity.Eating));
        Assert.Equal(SimulationTier.Near, s.Simulate.TierOf("c_rival"));
        AdvanceTo(s, 1, 9, 7); // 547 is not a 15-minute tick: no reaction yet
        Assert.Equal(Activity.Idle, s.State.World.Characters["c_rival"].Activity);
        s.Simulate.Advance(8); // 555 is a tick: the routine catches up
        Assert.Equal(Activity.Eating, s.State.World.Characters["c_rival"].Activity);
    }

    // ------------------------------------------------------------------ travel

    [Fact]
    public void Travel_takes_exactly_the_edge_minutes()
    {
        var s = TestSupport.NewPopulatedSession();
        GiveCommuterSchedule(s);
        var edge = s.Content.TravelMinutes("loc_residential", "loc_central_market");
        Assert.Equal(8, edge);

        AdvanceTo(s, 1, 7, 52); // departure is due at 08:00 minus 8 minutes
        Assert.True(s.State.World.ActiveTravels.TryGetValue("c_merchant", out var tripRaw));
        var trip = tripRaw ?? throw new Harness.AssertionException("expected c_merchant to be traveling");
        Assert.Equal(480, trip.ArrivalMinute);
        Assert.Equal(Activity.Traveling, s.State.World.Characters["c_merchant"].Activity);

        s.Simulate.Advance(8); // arrive at 08:00 sharp
        Assert.False(s.State.World.ActiveTravels.ContainsKey("c_merchant"));
        Assert.Equal("loc_central_market", s.State.World.Characters["c_merchant"].CurrentLocationId);
        Assert.Equal(Activity.Working, s.State.World.Characters["c_merchant"].Activity);
        var moved = s.Events.Query(typePrefix: WorldEventTypes.CharacterMoved, participantId: "c_merchant").Last();
        Assert.Equal(480, moved.Timestamp);
        Assert.Equal("loc_residential", moved.Data["from"]);
        Assert.Equal("loc_central_market", moved.Data["to"]);
    }

    [Fact]
    public void Late_characters_head_straight_for_the_current_block()
    {
        var s = TestSupport.NewPopulatedSession();
        GiveCommuterSchedule(s);
        AdvanceTo(s, 1, 9, 0); // commute done: merchant is at the market, Working
        Assert.Equal("loc_central_market", s.State.World.Characters["c_merchant"].CurrentLocationId);
        // Displace them mid-block: the sim recovers by heading for the current block.
        s.State.World.Characters["c_merchant"].CurrentLocationId = "loc_church";
        s.Simulate.Advance(15); // next Near tick corrects course
        Assert.True(s.State.World.ActiveTravels.TryGetValue("c_merchant", out var tripRaw2));
        var trip2 = tripRaw2 ?? throw new Harness.AssertionException("expected c_merchant to be traveling");
        Assert.Equal("loc_central_market", trip2.ToLocationId);
    }

    // ------------------------------------------------------------------ witnessing

    private static GameSession NewWitnessScene()
    {
        var s = TestSupport.NewPopulatedSession();
        GiveCommuterSchedule(s);
        // B waits at the market, C stays across town; both keep Check-valid single-block routines.
        s.State.World.Characters["c_priest"].HomeLocationId = "loc_central_market";
        s.State.World.Characters["c_priest"].CurrentLocationId = "loc_central_market";
        GiveSchedule(s, "c_priest", (0, 1440, "loc_central_market", Activity.Idle));
        s.State.World.Characters["c_guard"].HomeLocationId = "loc_church";
        s.State.World.Characters["c_guard"].CurrentLocationId = "loc_church";
        GiveSchedule(s, "c_guard", (0, 1440, "loc_church", Activity.Idle));
        return s;
    }

    [Fact]
    public void Arrival_is_witnessed_at_the_destination_but_not_across_town()
    {
        var s = NewWitnessScene();
        AdvanceTo(s, 1, 8, 5); // merchant arrives at the market at 08:00
        var moved = s.Events.Query(typePrefix: WorldEventTypes.CharacterMoved, participantId: "c_merchant").Last();

        Assert.True(s.Cognition.Knows("c_priest", moved.Id), "present at the destination: must know");
        var memory = s.Cognition.GetMemories("c_priest").First(m => m.EventId == moved.Id);
        Assert.Equal(MemorySource.Witnessed, memory.Source);

        Assert.False(s.Cognition.Knows("c_guard", moved.Id), "across town: must not know");
    }

    [Fact]
    public void Departure_is_witnessed_at_the_origin()
    {
        var s = NewWitnessScene();
        AdvanceTo(s, 1, 7, 55); // departed at 07:52, still en route until 08:00
        var departed = s.Events.Query(typePrefix: WorldEventTypes.CharacterDeparted, participantId: "c_merchant").ToList();
        Assert.Equal(1, departed.Count);
        Assert.Equal(472, departed[0].Timestamp);
        Assert.Equal("loc_residential", departed[0].Data["from"]);
        Assert.Equal("loc_central_market", departed[0].Data["to"]);

        Assert.True(s.Cognition.Knows("c_rival", departed[0].Id), "at the origin when he left: must know");
        var memory = s.Cognition.GetMemories("c_rival").First(m => m.EventId == departed[0].Id);
        Assert.Equal(MemorySource.Witnessed, memory.Source);
        Assert.False(s.Cognition.Knows("c_priest", departed[0].Id), "at the destination: only learns of the arrival");
    }

    [Fact]
    public void Arrival_links_back_to_the_departure_in_the_causal_chain()
    {
        var s = NewWitnessScene();
        AdvanceTo(s, 1, 8, 5);
        var departed = s.Events.Query(typePrefix: WorldEventTypes.CharacterDeparted, participantId: "c_merchant").ToList();
        var moved = s.Events.Query(typePrefix: WorldEventTypes.CharacterMoved, participantId: "c_merchant").ToList();
        Assert.Equal(1, departed.Count);
        Assert.Equal(1, moved.Count);
        Assert.Equal(departed[0].Id, moved[0].CausedBy);
        var chain = s.Events.CausalChain(moved[0].Id);
        Assert.True(chain.Any(e => e.Id == departed[0].Id));
    }

    [Fact]
    public void The_traveler_remembers_their_own_trip()
    {
        var s = NewWitnessScene();
        AdvanceTo(s, 1, 8, 5);
        var moved = s.Events.Query(typePrefix: WorldEventTypes.CharacterMoved, participantId: "c_merchant").Last();
        Assert.True(s.Cognition.Knows("c_merchant", moved.Id));
    }

    [Fact]
    public void Dead_characters_perceive_nothing()
    {
        var s = NewWitnessScene();
        s.State.World.Characters["c_priest"].IsAlive = false;
        AdvanceTo(s, 1, 8, 5);
        var moved = s.Events.Query(typePrefix: WorldEventTypes.CharacterMoved, participantId: "c_merchant").Last();
        Assert.False(s.Cognition.Knows("c_priest", moved.Id));
        Assert.False(s.Cognition.GetMemories("c_priest").Any(m => m.EventId == moved.Id));
    }

    // ------------------------------------------------------------------ routines

    [Fact]
    public void Player_character_is_never_simulated()
    {
        var s = TestSupport.NewPopulatedSession();
        s.Simulate.Advance(3 * 1440);
        var p = s.State.World.Characters["c_player"];
        Assert.Equal("loc_residential", p.CurrentLocationId);
        Assert.Equal(Activity.Idle, p.Activity);
    }

    [Fact]
    public void Dead_characters_are_never_simulated()
    {
        var s = TestSupport.NewPopulatedSession();
        s.State.World.Characters["c_rival"].IsAlive = false;
        s.Simulate.Advance(1440);
        var c = s.State.World.Characters["c_rival"];
        Assert.Equal("loc_residential", c.CurrentLocationId);
        Assert.Equal(Activity.Idle, c.Activity);
        Assert.False(s.State.World.ActiveTravels.ContainsKey("c_rival"));
    }

    [Fact]
    public void Activity_transitions_are_logged_once_each()
    {
        var s = TestSupport.NewPopulatedSession();
        GiveCommuterSchedule(s);
        AdvanceTo(s, 1, 7, 50); // crosses the 07:50 block boundary (Sleeping -> Idle)
        var changes = s.Events.Query(typePrefix: WorldEventTypes.ActivityChanged, participantId: "c_merchant").ToList();
        var wake = changes.FirstOrDefault(e => e.Data["from"] == Activity.Sleeping.ToString());
        Assert.True(wake is not null);
        Assert.Equal(Activity.Idle.ToString(), wake!.Data["to"]);
    }

    [Fact]
    public void No_activity_events_when_nothing_changes()
    {
        var s = TestSupport.NewPopulatedSession(); // default routines: Idle at home 06:00-12:00
        s.Simulate.Advance(120); // 06:00 -> 08:00, no block boundaries
        Assert.False(s.Events.Query(typePrefix: WorldEventTypes.ActivityChanged).Any());
        Assert.False(s.Events.Query(typePrefix: WorldEventTypes.CharacterMoved).Any());
    }

    [Fact]
    public void Seven_day_run_everyone_sleeps_at_home_at_3am()
    {
        var s = NewSim();
        AdvanceTo(s, 2, 3, 0);
        foreach (var (id, c) in s.State.World.Characters)
        {
            Assert.Equal(Activity.Sleeping, c.Activity, $"{id} should be asleep at 03:00");
            Assert.Equal(c.HomeLocationId, c.CurrentLocationId, $"{id} should be home at 03:00");
        }
    }

    [Fact]
    public void Seven_day_run_workers_are_at_work_mid_morning()
    {
        var s = NewSim();
        AdvanceTo(s, 2, 10, 0);
        var checkedCount = 0;
        foreach (var (id, c) in s.State.World.Characters)
        {
            if (c.Kind != CharacterKind.Civilian || c.WorkLocationId is null) continue;
            if (s.Schedules.GetSchedule(id).IsDayOff(2)) continue;
            var expected = s.Schedules.BlockAt(id, GameTime.At(2, 10, 0));
            Assert.Equal(expected.LocationId, c.CurrentLocationId, $"{id} should be where the schedule says at 10:00");
            Assert.Equal(expected.Activity, c.Activity, $"{id} should be doing what the schedule says at 10:00");
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "the cast should contain workers");
        Assert.True(s.State.World.Characters.Values.Any(c => c.Activity == Activity.Working));
    }

    [Fact]
    public void Seven_day_run_evenings_have_social_life()
    {
        var s = NewSim();
        AdvanceTo(s, 2, 20, 0);
        var socializing = s.State.World.Characters.Values.Count(c => c.IsAlive && c.Activity == Activity.Socializing);
        Assert.True(socializing > 0, "someone should be out socializing at 20:00");
    }

    [Fact]
    public void Seven_day_run_day_off_differs_from_workday()
    {
        var s = NewSim();
        var worker = s.State.World.Characters.Values
            .First(c => c.Kind == CharacterKind.Civilian && c.WorkLocationId is not null
                        && s.Schedules.GetSchedule(c.Id).DayOffIndex >= 0);
        var offDay = s.Schedules.GetSchedule(worker.Id).DayOffIndex + 1;
        var workDay = offDay == 2 ? 3 : 2;
        var offBlock = s.Schedules.BlockAt(worker.Id, GameTime.At(offDay, 10, 0));
        var workBlock = s.Schedules.BlockAt(worker.Id, GameTime.At(workDay, 10, 0));
        Assert.False(offBlock.LocationId == workBlock.LocationId && offBlock.Activity == workBlock.Activity,
            "day-off and workday schedules should differ at 10:00");

        // And the simulation honors the day-off schedule.
        var fresh = NewSim();
        AdvanceTo(fresh, offDay, 10, 0);
        var c = fresh.State.World.Characters[worker.Id];
        Assert.Equal(offBlock.LocationId, c.CurrentLocationId);
        Assert.Equal(offBlock.Activity, c.Activity);
    }

    // ------------------------------------------------------------------ determinism

    [Fact]
    public void Same_seed_same_advance_gives_identical_state()
    {
        var a = NewSim(42);
        var b = NewSim(42);
        a.Simulate.Advance(3 * 1440);
        b.Simulate.Advance(3 * 1440);
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void Different_seeds_give_different_routines()
    {
        var a = NewSim(42);
        var b = NewSim(43);
        a.Simulate.Advance(3 * 1440);
        b.Simulate.Advance(3 * 1440);
        Assert.NotEqual(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void Save_load_continue_is_hash_identical()
    {
        var path = Path.GetTempFileName();
        try
        {
            var a = NewSim(11);
            a.Simulate.Advance(2 * 1440);
            a.Save(path);
            var b = GameSession.Load(path, TestSupport.LoadContent());
            a.Simulate.Advance(1440);
            b.Simulate.Advance(1440);
            Assert.Equal(a.StateHash(), b.StateHash());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_load_mid_travel_continues_the_trip_identically()
    {
        var path = Path.GetTempFileName();
        try
        {
            var a = TestSupport.NewPopulatedSession(3);
            GiveCommuterSchedule(a);
            AdvanceTo(a, 1, 7, 55); // departed 07:52, arrives 08:00: mid-travel
            Assert.True(a.State.World.ActiveTravels.ContainsKey("c_merchant"));
            a.Save(path);

            var b = GameSession.Load(path, TestSupport.LoadContent());
            Assert.True(b.State.World.ActiveTravels.TryGetValue("c_merchant", out var tripRaw3));
            var trip3 = tripRaw3 ?? throw new Harness.AssertionException("expected c_merchant to be traveling after load");
            Assert.Equal(480, trip3.ArrivalMinute);

            a.Simulate.Advance(30);
            b.Simulate.Advance(30);
            Assert.Equal(a.StateHash(), b.StateHash());
            var moved = b.Events.Query(typePrefix: WorldEventTypes.CharacterMoved, participantId: "c_merchant").Last();
            Assert.Equal(480, moved.Timestamp, "arrival still lands on the exact minute after load");
            Assert.Equal("loc_central_market", b.State.World.Characters["c_merchant"].CurrentLocationId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_format_v4_migrates_to_v5_with_empty_travels()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var json = SaveSystem.Serialize(s.State)
            .Replace($"\"FormatVersion\":{SaveSystem.CurrentFormatVersion}", "\"FormatVersion\":4");
        var restored = SaveSystem.Deserialize(json);
        Assert.True(restored.World.ActiveTravels.Count == 0);
        Assert.True(GameStateValidator.Validate(restored).Count == 0);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Validator_rejects_broken_travel_state()
    {
        var s = TestSupport.NewPopulatedSession();
        s.State.World.ActiveTravels["ghost"] = new TravelState
        {
            CharacterId = "ghost", FromLocationId = "loc_residential",
            ToLocationId = "loc_church", DepartureMinute = 10, ArrivalMinute = 20,
        };
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("ghost")));

        var t = TestSupport.NewPopulatedSession();
        t.State.World.ActiveTravels["c_merchant"] = new TravelState
        {
            CharacterId = "c_merchant", FromLocationId = "loc_residential",
            ToLocationId = "loc_nowhere", DepartureMinute = 10, ArrivalMinute = 20,
        };
        Assert.True(GameStateValidator.Validate(t.State).Any(e => e.Contains("loc_nowhere")));

        var u = TestSupport.NewPopulatedSession();
        u.State.World.ActiveTravels["c_merchant"] = new TravelState
        {
            CharacterId = "c_merchant", FromLocationId = "loc_residential",
            ToLocationId = "loc_church", DepartureMinute = 30, ArrivalMinute = 20,
        };
        Assert.True(GameStateValidator.Validate(u.State).Any(e => e.Contains("arrives before it departs")));

        var v = TestSupport.NewPopulatedSession();
        v.State.World.Characters["c_merchant"].Activity = Activity.Traveling; // no trip recorded
        Assert.True(GameStateValidator.Validate(v.State).Any(e => e.Contains("no active trip")));
    }

    [Fact]
    public void Validator_accepts_a_healthy_mid_travel_state()
    {
        var s = TestSupport.NewPopulatedSession();
        GiveCommuterSchedule(s);
        AdvanceTo(s, 1, 7, 55);
        Assert.True(GameStateValidator.Validate(s.State).Count == 0);
    }
}
