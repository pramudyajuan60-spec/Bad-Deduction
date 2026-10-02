using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

/// <summary>Roadmap exit criterion for Phase 3: "Trust alone never forces an action."</summary>
public sealed class ComplianceTests
{
    private static Personality Average() => new()
    {
        Extraversion = 50, Agreeableness = 50, Conscientiousness = 50, Neuroticism = 50, Honesty = 50, Courage = 50,
    };

    /// <summary>The merchant (average personality) decides what to do when the player asks, at the given trust.</summary>
    private static GameSession Setup(int trust, params Goal[] goals)
    {
        var s = TestSupport.NewPopulatedSession(7);
        foreach (var id in new[] { "c_merchant", "c_priest", "c_player" })
            s.State.World.Profiles[id] = new CharacterProfile { Personality = Average() };
        s.State.World.Profiles["c_merchant"].Goals.AddRange(goals);
        s.Relationships.Connect("c_merchant", "c_player", RelationshipKind.Friend);
        var current = s.Social.View("c_merchant", "c_player").Trust;
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Trust: trust - current), "test setup");
        return s;
    }

    private static ComplianceDecision Ask(GameSession s, ActionRequest r, IEnumerable<ComplianceFactor>? extra = null) =>
        s.Compliance.Evaluate("c_merchant", "c_player", r, extra);

    [Fact]
    public void A_harmless_request_from_someone_trusted_is_granted()
    {
        var d = Ask(Setup(90), new ActionRequest { Label = "pass a message" });
        Assert.True(d.Complies, $"score {d.Score}");
        Assert.Equal("trust", d.Decisive!.Name);
    }

    [Fact]
    public void Trust_90_is_still_refused_when_any_single_strong_reason_stands_against_it()
    {
        // Each case: the same decider who would happily do a harmless favour at trust 90 refuses once ONE of the
        // design's counter-forces is strong enough: risk, morality, loyalty to someone else, fear, personal goals,
        // or (via the Phase 4 hook) emotional state.
        var s = Setup(90, new Goal { DefinitionId = "protect_family", Slot = GoalSlot.Primary, Priority = 90 });
        s.Relationships.Connect("c_merchant", "c_priest", RelationshipKind.Friend);
        s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Loyalty: 40, Fear: 17), "setup: loyal to and wary of the priest");

        var cases = new (string Why, ActionRequest Request, IEnumerable<ComplianceFactor>? Extra)[]
        {
            ("risk", new ActionRequest { Risk = 100 }, null),
            ("morality", new ActionRequest { MoralCost = 100 }, null),
            ("loyalty to someone else / fear of them", new ActionRequest { HarmsPeople = new[] { "c_priest" } }, null),
            ("personal goal", new ActionRequest { UndermineGoals = new[] { "protect_family" } }, null),
            ("emotional state (Phase 4 hook)", new ActionRequest(), new[] { new ComplianceFactor("panic", -60) }),
        };
        foreach (var (why, request, extra) in cases)
        {
            var d = Ask(s, request, extra);
            Assert.False(d.Complies, $"trust 90 should not force compliance against {why} (score {d.Score})");
        }
        Assert.True(Ask(s, new ActionRequest()).Complies, "control: the same decider grants a harmless favour");
    }

    [Fact]
    public void Even_maximum_trust_and_every_other_pull_maxed_cannot_force_an_extreme_request()
    {
        var s = Setup(100);
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Loyalty: 100, Influence: 100, Fear: 100, Affection: 100, Respect: 100, Suspicion: -100, Resentment: -100), "maxed");
        var edge = s.Relationships.Get("c_merchant", "c_player")!;
        foreach (var axis in new[] { RelationshipAxis.Trust, RelationshipAxis.Loyalty, RelationshipAxis.Influence, RelationshipAxis.Fear, RelationshipAxis.Affection, RelationshipAxis.Respect })
            Assert.Equal(100, edge.GetAxis(axis), $"setup: {axis}");
        var d = Ask(s, new ActionRequest { Risk = 100, MoralCost = 100 });
        Assert.False(d.Complies, $"score {d.Score}");
    }

    [Fact]
    public void More_trust_never_makes_someone_less_willing()
    {
        var last = int.MinValue;
        for (var trust = 0; trust <= 100; trust++)
        {
            var score = Ask(Setup(trust), new ActionRequest { Risk = 40, MoralCost = 30 }).Score;
            Assert.True(score >= last, $"trust {trust}: score fell from {last} to {score}");
            last = score;
        }
    }

    [Fact]
    public void Trust_still_matters_it_tips_a_borderline_request()
    {
        // Resistance here is 36 (risk 23 + morality 13); the relationship pulls 23 at trust 5 and 47 at trust 95.
        var borderline = new ActionRequest { Risk = 40, MoralCost = 20 };
        var low = Ask(Setup(5), borderline);
        var high = Ask(Setup(95), borderline);
        Assert.False(low.Complies, $"low trust refuses (score {low.Score})");
        Assert.True(high.Complies, $"high trust grants the same request (score {high.Score})");
    }

    [Fact]
    public void Fear_can_compel_without_any_trust_but_not_against_serious_danger()
    {
        var s = Setup(0);
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 90, Suspicion: -100), "intimidation");
        Assert.True(Ask(s, new ActionRequest()).Complies, "a frightened person obeys a trivial order");
        Assert.False(Ask(s, new ActionRequest { Risk = 100 }).Complies, "but not one that endangers them");
    }

    [Fact]
    public void Decisions_are_explainable_pure_and_deterministic()
    {
        var s = Setup(60, new Goal { DefinitionId = "keep_job", Slot = GoalSlot.Primary, Priority = 70 });
        var request = new ActionRequest { Risk = 50, MoralCost = 20, UndermineGoals = new[] { "keep_job" } };
        var hash = s.StateHash();
        var a = Ask(s, request);
        var b = Ask(s, request);
        Assert.Equal(hash, s.StateHash(), "evaluating must not change the world");
        Assert.Equal(a.Score, b.Score);
        Assert.SequenceEqual(a.Factors.Select(f => $"{f.Name}={f.Value}"), b.Factors.Select(f => $"{f.Name}={f.Value}"));
        Assert.Equal(a.Factors.Sum(f => f.Value), a.Score, "score is exactly the sum of the reported factors");
        if (!a.Complies) Assert.True(a.Decisive!.Value < 0, "a refusal is explained by a negative factor");
    }
}
