using System.Text.Json;
using System.Text.Json.Nodes;
using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class SocialTests
{
    private static GameSession Pair(RelationshipKind kind = RelationshipKind.Acquaintance)
    {
        var s = TestSupport.NewPopulatedSession(3);
        s.Relationships.Connect("c_merchant", "c_priest", kind);
        return s;
    }

    [Fact]
    public void Trust_bands_follow_the_design_table()
    {
        foreach (var (trust, band) in new[]
        {
            (0, TrustBand.Hostile), (20, TrustBand.Hostile), (21, TrustBand.Suspicious), (40, TrustBand.Suspicious),
            (41, TrustBand.Neutral), (60, TrustBand.Neutral), (61, TrustBand.Friendly), (75, TrustBand.Friendly),
            (76, TrustBand.Trusted), (90, TrustBand.Trusted), (91, TrustBand.ExtremelyLoyal), (100, TrustBand.ExtremelyLoyal),
            (-5, TrustBand.Hostile), (140, TrustBand.ExtremelyLoyal),
        })
            Assert.Equal(band, SocialRules.BandOf(trust), $"trust {trust}");
    }

    [Fact]
    public void An_ordinary_acquaintance_starts_at_trust_20_and_strangers_look_the_same()
    {
        var s = Pair(RelationshipKind.Acquaintance);
        Assert.Equal(20, s.Social.View("c_merchant", "c_priest").Trust);
        var stranger = s.Social.View("c_merchant", "c_player");
        Assert.False(stranger.Exists);
        Assert.Equal(20, stranger.Trust);
    }

    [Fact]
    public void Generated_relationships_are_in_range_and_closer_kinds_start_warmer()
    {
        var sums = new Dictionary<RelationshipKind, (int Total, int N)>();
        for (ulong seed = 1; seed <= 30; seed++)
            foreach (var e in CastSupport.NewCastSession(seed).State.World.Relationships)
            {
                foreach (var axis in SocialRules.AllAxes)
                    Assert.True(e.GetAxis(axis) is >= 0 and <= 100, $"seed {seed}: {axis}={e.GetAxis(axis)}");
                var (t, n) = sums.GetValueOrDefault(e.Kind);
                sums[e.Kind] = (t + e.Trust, n + 1);
                if (e.Kind == RelationshipKind.Rival) Assert.Equal(TrustBand.Hostile, SocialRules.BandOf(e.Trust), "rivals start in deep distrust");
            }
        double Mean(RelationshipKind k) => (double)sums[k].Total / sums[k].N;
        Assert.True(Mean(RelationshipKind.Family) > Mean(RelationshipKind.Friend), "family > friend");
        Assert.True(Mean(RelationshipKind.Friend) > Mean(RelationshipKind.Colleague), "friend > colleague");
        Assert.True(Mean(RelationshipKind.Colleague) > Mean(RelationshipKind.Acquaintance), "colleague > acquaintance");
        Assert.True(Mean(RelationshipKind.Acquaintance) > Mean(RelationshipKind.Rival), "acquaintance > rival");
    }

    [Fact]
    public void Adjust_changes_one_direction_only()
    {
        var s = Pair();
        var before = s.Social.View("c_priest", "c_merchant");
        s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: 30, Fear: 10), "test");
        Assert.Equal(50, s.Social.View("c_merchant", "c_priest").Trust);
        Assert.Equal(before, s.Social.View("c_priest", "c_merchant"));
    }

    [Fact]
    public void Axes_clamp_at_both_ends_and_the_log_records_what_was_actually_applied()
    {
        var s = Pair();
        var up = s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: 500), "windfall")!;
        Assert.Equal(100, s.Social.View("c_merchant", "c_priest").Trust);
        Assert.Equal("+80", up.Data["trust"]);
        var down = s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: -9999), "betrayal")!;
        Assert.Equal(0, s.Social.View("c_merchant", "c_priest").Trust);
        Assert.Equal("-100", down.Data["trust"]);
        Assert.True(s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: -5), "already at the floor") is null, "no-op logs nothing");
    }

    [Fact]
    public void First_contact_creates_acquaintances_both_ways_and_changes_are_causally_linked()
    {
        var s = TestSupport.NewPopulatedSession(3);
        Assert.False(s.Social.View("c_merchant", "c_player").Exists);
        var first = s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Suspicion: 25), "saw him near the stall")!;
        Assert.Equal("acquaintance", first.Data["created"]);
        Assert.True(s.Social.View("c_player", "c_merchant").Exists, "reverse edge exists");
        var second = s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Trust: -10), "his story did not add up", causedBy: first.Id)!;
        var chain = s.Events.CausalChain(second.Id);
        Assert.Equal(2, chain.Count);
        Assert.Equal(first.Id, chain[0].Id);
        Assert.Equal(WorldEventTypes.RelationshipChanged, chain[1].Type);
        Assert.Contains("c_merchant", string.Join(",", chain[1].Participants));
    }

    [Fact]
    public void Invalid_adjustments_are_rejected()
    {
        var s = Pair();
        Assert.Throws<ArgumentException>(() => s.Social.Adjust("ghost", "c_priest", new SocialDelta(Trust: 1), "x"));
        Assert.Throws<ArgumentException>(() => s.Social.Adjust("c_priest", "c_priest", new SocialDelta(Trust: 1), "x"));
        Assert.Throws<ArgumentException>(() => s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: 1), "  "));
    }

    [Fact]
    public void Volatile_axes_calm_down_daily_without_overshooting_but_trust_does_not_erode()
    {
        var s = Pair();
        s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Fear: 60, Suspicion: 60, Trust: 70), "scare");
        var start = s.Social.View("c_merchant", "c_priest");
        s.Social.ApplyDailyDrift();
        var next = s.Social.View("c_merchant", "c_priest");
        Assert.Equal(start.Fear - SocialRules.DailyStep(RelationshipAxis.Fear), next.Fear);
        Assert.Equal(start.Suspicion - SocialRules.DailyStep(RelationshipAxis.Suspicion), next.Suspicion);
        Assert.Equal(start.Trust, next.Trust, "trust only moves through events");
        for (var i = 0; i < 60; i++) s.Social.ApplyDailyDrift();
        var rested = s.Social.View("c_merchant", "c_priest");
        var resting = SocialBaselines.Resting(RelationshipKind.Acquaintance, null, null);
        Assert.Equal(resting[(int)RelationshipAxis.Fear], rested.Fear);
        Assert.Equal(resting[(int)RelationshipAxis.Suspicion], rested.Suspicion);
    }

    [Fact]
    public void Crossing_midnight_applies_exactly_one_day_of_drift()
    {
        var s = Pair();
        s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Fear: 50), "scare");
        var before = s.Social.View("c_merchant", "c_priest").Fear;
        s.Time.Advance(24 * 60);
        Assert.Equal(before - SocialRules.DailyStep(RelationshipAxis.Fear), s.Social.View("c_merchant", "c_priest").Fear);
    }

    [Fact]
    public void Social_changes_survive_save_and_load_and_the_run_stays_deterministic()
    {
        GameSession Run(bool interrupt)
        {
            var s = CastSupport.NewCastSession(11);
            var ids = s.State.World.Characters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            s.Social.Adjust(ids[0], ids[1], new SocialDelta(Suspicion: 40, Trust: -15, Fear: 30), "incident");
            s.Time.Advance(24 * 60);
            if (!interrupt) { s.Time.Advance(2 * 24 * 60); return s; }
            var path = Path.Combine(Path.GetTempPath(), "bd-social-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                s.Save(path);
                var resumed = GameSession.Load(path, TestSupport.LoadContent());
                resumed.Time.Advance(2 * 24 * 60);
                return resumed;
            }
            finally { File.Delete(path); }
        }
        Assert.Equal(Run(false).StateHash(), Run(true).StateHash());
    }

    [Fact]
    public void Police_start_with_trust_90_and_cooperate()
    {
        var s = CastSupport.NewCastSession(5);
        var subject = s.State.World.Characters.Values.First(c => c.Kind == CharacterKind.Civilian).Id;
        var officers = s.State.World.Characters.Values.Where(c => c.Kind == CharacterKind.Police).Select(c => c.Id).ToList();
        Assert.Equal(officers.Count, s.Social.SeedPoliceTrust(subject));
        foreach (var o in officers)
        {
            Assert.Equal(90, s.Social.View(o, subject).Trust);
            Assert.Equal(PoliceStance.Cooperation, s.Social.StanceToward(o, subject));
        }
    }

    [Fact]
    public void The_police_ladder_escalates_as_trust_falls()
    {
        Assert.Equal(PoliceStance.Cooperation, SocialRules.StanceOf(100));
        Assert.Equal(PoliceStance.Cooperation, SocialRules.StanceOf(70));
        Assert.Equal(PoliceStance.Questioning, SocialRules.StanceOf(69));
        Assert.Equal(PoliceStance.Questioning, SocialRules.StanceOf(45));
        Assert.Equal(PoliceStance.IndependentVerification, SocialRules.StanceOf(44));
        Assert.Equal(PoliceStance.IndependentVerification, SocialRules.StanceOf(25));
        Assert.Equal(PoliceStance.SuspicionOfSubject, SocialRules.StanceOf(24));
        Assert.Equal(PoliceStance.SuspicionOfSubject, SocialRules.StanceOf(0));

        var s = TestSupport.NewPopulatedSession(9);
        s.Social.SeedPoliceTrust("c_player");
        var playersViewOfGuard = s.Social.View("c_player", "c_guard");
        var seen = new List<PoliceStance> { s.Social.StanceToward("c_guard", "c_player") };
        foreach (var _ in new[] { 1, 2, 3 })
        {
            s.Social.Adjust("c_guard", "c_player", new SocialDelta(Trust: -25), "contradictory explanation");
            seen.Add(s.Social.StanceToward("c_guard", "c_player"));
        }
        Assert.SequenceEqual(new[]
        {
            PoliceStance.Cooperation, PoliceStance.Questioning, PoliceStance.IndependentVerification, PoliceStance.SuspicionOfSubject,
        }, seen);
        Assert.Equal(playersViewOfGuard, s.Social.View("c_player", "c_guard"), "the subject's own view of the guard is unaffected");
        Assert.Throws<InvalidOperationException>(() => s.Social.StanceToward("c_merchant", "c_player"));
    }

    [Fact]
    public void Social_state_never_depends_on_who_holds_a_hidden_role()
    {
        string Social(string malvr)
        {
            var s = CastSupport.NewCastSession(21);
            s.Identity.AssignRole(malvr, HiddenRole.Malvr);
            var ids = s.State.World.Characters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            s.Social.Adjust(ids[2], ids[3], new SocialDelta(Trust: 20, Respect: -10), "gossip");
            s.Time.Advance(3 * 24 * 60);
            return JsonSerializer.Serialize(s.State.World.Relationships);
        }
        var civilians = CastSupport.NewCastSession(21).State.World.Characters.Values.Where(c => c.Kind == CharacterKind.Civilian).Select(c => c.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal(Social(civilians[0]), Social(civilians[5]));
    }

    [Fact]
    public void The_validator_rejects_out_of_range_axes()
    {
        var s = CastSupport.NewCastSession(4);
        s.State.World.Relationships[0].Suspicion = 140;
        var errors = GameStateValidator.Validate(s.State);
        Assert.True(errors.Any(e => e.Contains("Suspicion outside 0-100")), "expected a range error");
    }

    [Fact]
    public void Version_2_saves_gain_relationship_axes_at_their_kind_baseline()
    {
        var s = CastSupport.NewCastSession(8);
        var node = JsonNode.Parse(SaveSystem.Serialize(s.State))!.AsObject();
        node["FormatVersion"] = 2;
        foreach (var edge in node["State"]!["World"]!["Relationships"]!.AsArray())
            foreach (var axis in SocialRules.AllAxes) edge!.AsObject().Remove(axis.ToString());

        var migrated = SaveSystem.Deserialize(node.ToJsonString());
        Assert.Equal(s.State.World.Relationships.Count, migrated.World.Relationships.Count);
        foreach (var e in migrated.World.Relationships)
        {
            var expected = SocialBaselines.Resting(e.Kind, null, null);
            foreach (var axis in SocialRules.AllAxes) Assert.Equal(expected[(int)axis], e.GetAxis(axis), $"{e.Kind} {axis}");
        }
    }
}
