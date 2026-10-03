using BadDeduction.Agenda;
using BadDeduction.AI;
using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Manipulation;
using BadDeduction.Police;
using BadDeduction.Social;
using BadDeduction.World;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests.Manipulation;

public sealed class ManipulationTests
{
    private static string? TestResolveLocation(string fragment)
    {
        var f = fragment.Trim().ToLowerInvariant();
        if (f.Contains("warehouse") || f.Contains("gudang")) return "loc_warehouse";
        if (f.Contains("market")) return "loc_central_market";
        return null;
    }

    // ------------------------------------------------------- trust starts at 20

    [Fact]
    public void Trust_starts_at_exactly_20_toward_player()
    {
        var s = TestSupport.NewPopulatedSession(42);
        // Stranger view (no edge yet): exactly 20, no personality nudge.
        Assert.Equal(20, s.Social.View("c_merchant", "c_player").Trust);
        // Edge creation: pinned at exactly 20 before the delta applies.
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Trust: 5), "test");
        Assert.Equal(25, s.Social.View("c_merchant", "c_player").Trust);
    }

    [Fact]
    public void Trust_can_move_after_start()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Trust: 30), "test");
        Assert.Equal(50, s.Social.View("c_merchant", "c_player").Trust);
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Trust: -45), "test");
        Assert.Equal(5, s.Social.View("c_merchant", "c_player").Trust);
    }

    [Fact]
    public void Police_trust_seeding_still_reaches_90()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.Social.SeedPoliceTrust("c_player", 90);
        Assert.Equal(90, s.Social.View("c_guard", "c_player").Trust);
    }

    [Fact]
    public void Npc_to_npc_baselines_unchanged()
    {
        var s = TestSupport.NewPopulatedSession(42);
        // NPC↔NPC edges keep kind-based baselines: a fresh Friend edge starts near
        // 60, never forced to the player rule's 20.
        s.Relationships.Connect("c_merchant", "c_rival", RelationshipKind.Friend);
        var trust = s.Social.View("c_merchant", "c_rival").Trust;
        Assert.True(trust >= 50 && trust <= 70, $"friend baseline should be ~60, was {trust}");
    }

    // ------------------------------------------------------- manipulability tiers

    [Fact]
    public void Tier_assignment_is_deterministic()
    {
        var s1 = TestSupport.NewPopulatedSession(42);
        var s2 = TestSupport.NewPopulatedSession(42);
        foreach (var id in new[] { "c_merchant", "c_priest", "c_rival", "c_guard" })
            Assert.Equal(s1.Manipulation.TierOf(id), s2.Manipulation.TierOf(id));
        var s3 = TestSupport.NewPopulatedSession(43);
        // Different seed: at least the pure function is stable per (seed, id).
        Assert.Equal(ManipulabilityRules.TierFor(43, "c_merchant"), s3.Manipulation.TierOf("c_merchant"));
    }

    [Fact]
    public void Tier_rules_have_documented_values()
    {
        Assert.Equal(15, ManipulabilityRules.ComplianceBonus(ManipulabilityTier.Gullible));
        Assert.Equal(0, ManipulabilityRules.ComplianceBonus(ManipulabilityTier.Standard));
        Assert.Equal(-20, ManipulabilityRules.ComplianceBonus(ManipulabilityTier.Wary));
        Assert.Equal(6, ManipulabilityRules.ScaleTrustShift(ManipulabilityTier.Gullible, 4));
        Assert.Equal(2, ManipulabilityRules.ScaleTrustShift(ManipulabilityTier.Wary, 4));
        Assert.Equal(4, ManipulabilityRules.ScaleTrustShift(ManipulabilityTier.Standard, 4));
    }

    // ------------------------------------------------------- order detection

    [Fact]
    public void Detect_goto_english_with_time()
    {
        // Day 1, 10:00 → next midnight is day 2, 00:00 = minute 1440.
        var order = OrderDetector.Detect("meet me at the warehouse at midnight",
            TestResolveLocation, GameTime.At(1, 10).TotalMinutes);
        Assert.Equal(OrderKind.GoTo, order.Kind);
        Assert.Equal("loc_warehouse", order.LocationId);
        Assert.Equal(1440, order.TimeMinute);
    }

    [Fact]
    public void Detect_goto_indonesian()
    {
        var order = OrderDetector.Detect("temui aku di gudang tengah malam",
            TestResolveLocation, GameTime.At(1, 10).TotalMinutes);
        Assert.Equal(OrderKind.GoTo, order.Kind);
        Assert.Equal("loc_warehouse", order.LocationId);
        Assert.Equal(1440, order.TimeMinute);
    }

    [Fact]
    public void Detect_attack_indonesian_third_person()
    {
        var order = OrderDetector.Detect("bunuh dia", TestResolveLocation, 0);
        Assert.Equal(OrderKind.Attack, order.Kind);
        Assert.Equal("dia", order.TargetText);
    }

    [Fact]
    public void Detect_ignores_second_person_violence()
    {
        // "kill you" belongs to the threat pipeline, not orders.
        Assert.Equal(OrderKind.None, OrderDetector.Detect("kubunuh", TestResolveLocation, 0).Kind);
        Assert.Equal(OrderKind.None, OrderDetector.Detect("I will kill you", TestResolveLocation, 0).Kind);
    }

    [Fact]
    public void Detect_wait_and_buy()
    {
        Assert.Equal(OrderKind.Wait, OrderDetector.Detect("tunggu di sini", TestResolveLocation, 0).Kind);
        var buy = OrderDetector.Detect("belikan aku pisau", TestResolveLocation, 0);
        Assert.Equal(OrderKind.Buy, buy.Kind);
        Assert.True(buy.TargetText!.Contains("pisau"));
    }

    [Fact]
    public void Detect_nothing_for_small_talk()
    {
        Assert.Equal(OrderKind.None, OrderDetector.Detect("what did you see last night?", TestResolveLocation, 0).Kind);
        Assert.Equal(OrderKind.None, OrderDetector.Detect("", TestResolveLocation, 0).Kind);
    }

    // ------------------------------------------------------- compliance: the core rule

    [Fact]
    public void Compliance_refuses_murder_even_at_max_trust()
    {
        var s = TestSupport.NewPopulatedSession(42);
        // Max out every pull factor toward the player: trust, loyalty, influence,
        // fear, affection, respect at 100; suspicion and resentment at 0.
        s.Social.Adjust("c_merchant", "c_player",
            new SocialDelta(Trust: 100, Loyalty: 100, Influence: 100, Fear: 100,
                Affection: 100, Respect: 100, Suspicion: -100, Resentment: -100),
            "maxed");
        var order = new DetectedOrder(OrderKind.Attack, "The Rival", null, null);
        var decision = s.Manipulation.EvaluateOrder("c_merchant", "c_player", order);
        // The user's rule, pinned: no trust value forces murder.
        Assert.False(decision.Complies);
        Assert.True(decision.Score < 0);
    }

    [Fact]
    public void Compliance_accepts_benign_order_at_high_trust()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Trust: 60), "test"); // 20 + 60 = 80
        var order = new DetectedOrder(OrderKind.Wait, null, null, null);
        var decision = s.Manipulation.EvaluateOrder("c_merchant", "c_player", order);
        Assert.True(decision.Complies);
    }

    [Fact]
    public void Compliance_refuses_attack_at_default_trust()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var order = new DetectedOrder(OrderKind.Attack, "The Rival", null, null);
        var decision = s.Manipulation.EvaluateOrder("c_merchant", "c_player", order);
        Assert.False(decision.Complies);
    }

    // ------------------------------------------------------- orders through dialogue

    [Fact]
    public void Exchange_accepted_lure_creates_visible_travel()
    {
        var s = TestSupport.NewPopulatedSession(42);
        // Trust + fear maxed so the lure is accepted in every tier.
        s.Social.Adjust("c_merchant", "c_player",
            new SocialDelta(Trust: 100, Fear: 100, Suspicion: -100), "test");
        var before = s.World.GetCharacter("c_merchant").CurrentLocationId;

        var result = s.Dialogue.Exchange("c_merchant", "c_player", "go to the warehouse");

        Assert.Equal("accepted:GoTo", result.OrderOutcome);
        Assert.Equal("loc_warehouse", s.Manipulation.CommandedDestinationFor("c_merchant"));
        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.OrderAccepted));

        // The simulation picks the command up: the NPC visibly walks there.
        s.Simulate.Advance(120);
        var after = s.World.GetCharacter("c_merchant").CurrentLocationId;
        Assert.True(after == "loc_warehouse" || s.State.World.ActiveTravels.ContainsKey("c_merchant"),
            $"merchant should be heading to the warehouse (was {before}, now {after})");
    }

    [Fact]
    public void Exchange_refused_order_logs_event_and_drops_trust()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var result = s.Dialogue.Exchange("c_merchant", "c_player", "kill him");

        Assert.Equal("refused:Attack", result.OrderOutcome);
        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.OrderRefused));
        // Trust fell below the starting 20 (refusal fallout + mock's -1, tier-scaled).
        Assert.True(s.Social.View("c_merchant", "c_player").Trust < 20);
    }

    [Fact]
    public void Commanded_destination_expires()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.State.Manipulation.CommandedDestinations["c_merchant"] = new CommandedDestination
        {
            LocationId = "loc_warehouse",
            UntilMinute = s.State.TotalMinutes + 10,
            OrderedBy = "c_player",
            OrderLabel = "test",
        };
        Assert.Equal("loc_warehouse", s.Manipulation.CommandedDestinationFor("c_merchant"));
        s.Time.Advance(20);
        Assert.True(s.Manipulation.CommandedDestinationFor("c_merchant") is null);
    }

    // ------------------------------------------------------- despair & suicide

    [Fact]
    public void Despair_accumulates_and_triggers_suicide()
    {
        var s = TestSupport.NewPopulatedSession(42);
        Assert.Equal(0, s.Manipulation.DespairOf("c_merchant"));

        // Drive to 100 in tier-proof steps (Gullible sinks 2x, Wary 0.5x).
        for (var i = 0; i < 10 && s.World.GetCharacter("c_merchant").IsAlive; i++)
            s.Manipulation.AdjustDespair("c_merchant", 30, causedBy: 1);

        Assert.False(s.World.GetCharacter("c_merchant").IsAlive);
        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.ManipulationSuicide));
        Assert.True(s.State.Police.Disturbances.Count > 0);
    }

    [Fact]
    public void Despair_validator_clamps()
    {
        var v = new DialogueValidator();
        var result = v.Validate(
            new DialogueOutput { ReplyText = "ok", DespairDelta = 100 },
            new List<string>(), new List<string>());
        Assert.True(result.Accepted);
        Assert.Equal(AIRules.MaxDespairDelta, result.Output!.DespairDelta);
    }

    [Fact]
    public void Despair_stays_at_zero_without_pressure()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var result = s.Dialogue.Exchange("c_merchant", "c_player", "lovely weather today");
        Assert.Equal(0, result.DespairDelta);
        Assert.Equal(0, s.Manipulation.DespairOf("c_merchant"));
    }

    // ------------------------------------------------------- observable routines

    [Fact]
    public void Routine_observation_is_knowledge_gated()
    {
        var s = TestSupport.NewPopulatedSession(42);
        // Give the merchant a real schedule for today.
        s.State.World.Schedules["c_merchant"] = new Schedule
        {
            Workday = new List<ScheduleBlock>
            {
                new() { StartMinute = 0, EndMinute = 480, LocationId = "loc_residential", Activity = Activity.Sleeping },
                new() { StartMinute = 480, EndMinute = 1080, LocationId = "loc_central_market", Activity = Activity.Working },
                new() { StartMinute = 1080, EndMinute = 1440, LocationId = "loc_residential", Activity = Activity.Idle },
            },
        };
        // Nothing learned before observing.
        Assert.Equal(0, s.Manipulation.GetLearnedSchedule("c_merchant").Count);

        // 06:00 start; advance to 09:00 (540) and stand with the merchant at the market.
        s.Time.Advance(180);
        s.World.MoveCharacter("c_player", "loc_central_market");
        s.World.MoveCharacter("c_merchant", "loc_central_market");
        s.Manipulation.ObserveRoutines();

        var learned = s.Manipulation.GetLearnedSchedule("c_merchant");
        Assert.Equal(1, learned.Count);
        Assert.Equal("loc_central_market", learned[0].LocationId);
        Assert.Equal("seen", learned[0].Source);
        // The unobserved blocks stay unknown.
        Assert.False(learned.Any(b => b.LocationId == "loc_residential"));
    }

    // ------------------------------------------------------- visible enemy agency

    [Fact]
    public void Malvr_lurks_near_target_at_night()
    {
        var s = TestSupport.NewPopulatedSession(42);
        // c_rival secretly holds Malvr (manual assignment in NewPopulatedSession).
        s.State.Agenda.RolesSeeded = true;
        s.State.Agenda.Objectives["obj_test"] = new HiddenObjective
        {
            Id = "obj_test",
            HolderId = "c_rival",
            Kind = ObjectiveKind.EliminateObstacle,
            Status = ObjectiveStatus.Active,
            TargetId = "c_merchant",
            CreatedAt = 0,
        };
        // Night: 06:00 + 17h = 23:00. Move the target away first: everyone starts
        // co-located, and no travel is needed when the lurker is already there.
        s.World.MoveCharacter("c_merchant", "loc_central_market");
        s.Time.Advance(17 * 60);
        var dest = s.Agenda.StrategicDestinationFor("c_rival");
        Assert.Equal("loc_central_market", dest);
    }

    [Fact]
    public void Malvr_does_not_lurk_by_day()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.State.Agenda.RolesSeeded = true;
        s.State.Agenda.Objectives["obj_test"] = new HiddenObjective
        {
            Id = "obj_test",
            HolderId = "c_rival",
            Kind = ObjectiveKind.EliminateObstacle,
            Status = ObjectiveStatus.Active,
            TargetId = "c_merchant",
            CreatedAt = 0,
        };
        // 06:00 start is daytime.
        Assert.True(s.Agenda.StrategicDestinationFor("c_rival") is null);
    }

    [Fact]
    public void Strategic_destination_inert_without_seeded_roles()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.State.Agenda.Objectives["obj_test"] = new HiddenObjective
        {
            Id = "obj_test",
            HolderId = "c_rival",
            Kind = ObjectiveKind.EliminateObstacle,
            Status = ObjectiveStatus.Active,
            TargetId = "c_merchant",
            CreatedAt = 0,
        };
        s.Time.Advance(17 * 60);
        Assert.True(s.Agenda.StrategicDestinationFor("c_rival") is null);
    }
}
