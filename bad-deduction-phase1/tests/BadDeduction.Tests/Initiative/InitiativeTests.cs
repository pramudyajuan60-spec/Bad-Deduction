using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Initiative;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests.Initiative;

public sealed class InitiativeTests
{
    /// <summary>
    /// Seed 1: the initiative stream's first draws are [12, 34, 11, 47] (verified against
    /// the real PRNG), so draws against motive weights 30..70 pass deterministically.
    /// </summary>
    private const ulong Seed = 1;

    private static GameSession CraftedSession()
    {
        var s = TestSupport.NewPopulatedSession(Seed);
        return s;
    }

    private static string Describe(NpcInitiative i) =>
        $"{i.NpcId}:{i.Motive}:{i.MotiveDetails}@{i.EnqueuedAt}";

    [Fact]
    public void Evaluate_is_deterministic()
    {
        var a = CraftedSession();
        var b = CraftedSession();
        foreach (var s in new[] { a, b })
            s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 60), "test");

        var qa = a.Initiative.Evaluate();
        var qb = b.Initiative.Evaluate();

        Assert.SequenceEqual(qa.Select(Describe), qb.Select(Describe));
        Assert.True(qa.Count > 0, "test setup: a motive must fire");
    }

    [Fact]
    public void ThreatenBack_fires_with_threat_memory_anchor()
    {
        var s = CraftedSession();
        // The player really threatens the priest first: the NPC gets a threat memory.
        var exchange = s.Dialogue.Exchange("c_priest", "c_player", "I will kill you");
        Assert.True(exchange.Accepted);
        s.Social.Adjust("c_priest", "c_player", new SocialDelta(Fear: 20), "test");
        s.World.MoveCharacter("c_priest", "loc_residential");

        var queue = s.Initiative.Evaluate();

        var init = queue.FirstOrDefault(i => i.NpcId == "c_priest");
        Assert.True(init is not null, "frightened priest must want to threaten back");
        Assert.Equal(NpcMotive.ThreatenBack, init!.Motive);
        Assert.Contains("threatened", init.MotiveDetails);
    }

    [Fact]
    public void Confront_fires_on_high_suspicion()
    {
        var s = CraftedSession();
        s.Social.Adjust("c_rival", "c_player", new SocialDelta(Suspicion: 60), "test");

        var queue = s.Initiative.Evaluate();

        var init = queue.FirstOrDefault(i => i.NpcId == "c_rival");
        Assert.True(init is not null, "suspicious rival must want to confront");
        Assert.Equal(NpcMotive.Confront, init!.Motive);
    }

    [Fact]
    public void Plead_fires_for_frightened_civilian()
    {
        var s = CraftedSession();
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 45), "test");

        var queue = s.Initiative.Evaluate();

        var init = queue.FirstOrDefault(i => i.NpcId == "c_merchant");
        Assert.True(init is not null, "frightened merchant must want to plead");
        Assert.Equal(NpcMotive.Plead, init!.Motive);
        Assert.True(init.MotiveDetails.Length > 0);
    }

    [Fact]
    public void Warn_fires_on_affection_and_danger_memory()
    {
        var s = CraftedSession();
        s.Social.Adjust("c_guard", "c_player", new SocialDelta(Affection: 50), "test");
        var danger = s.Events.Record("test.danger", locationId: "loc_residential");
        s.Cognition.Perceive("c_guard", danger.Id, MemorySource.Witnessed, "saw armed strangers near the market");

        var queue = s.Initiative.Evaluate();

        var init = queue.FirstOrDefault(i => i.NpcId == "c_guard");
        Assert.True(init is not null, "fond guard who saw danger must want to warn");
        Assert.Equal(NpcMotive.Warn, init!.Motive);
        Assert.Equal("saw armed strangers near the market", init.MotiveDetails);
    }

    [Fact]
    public void ShareRumor_fires_on_unshared_rumor()
    {
        var s = CraftedSession();
        var rumor = s.Events.Record("test.rumor", locationId: "loc_residential");
        s.Cognition.Perceive("c_merchant", rumor.Id, MemorySource.Told, "heard the guard captain takes bribes");

        var queue = s.Initiative.Evaluate();

        var init = queue.FirstOrDefault(i => i.NpcId == "c_merchant");
        Assert.True(init is not null, "merchant with an unshared rumor must want to share it");
        Assert.Equal(NpcMotive.ShareRumor, init!.Motive);
        Assert.Equal("heard the guard captain takes bribes", init.MotiveDetails);
    }

    [Fact]
    public void Evaluate_caps_pending_at_three_dropping_lowest_priority()
    {
        var s = CraftedSession();
        s.World.MoveCharacter("c_priest", "loc_residential");

        // Four NPCs, four applicable motives, all draws pass on seed 1 ([12,34,11,47]).
        s.Social.Adjust("c_guard", "c_player", new SocialDelta(Affection: 50), "test");
        var danger = s.Events.Record("test.danger", locationId: "loc_residential");
        s.Cognition.Perceive("c_guard", danger.Id, MemorySource.Witnessed, "saw armed strangers near the market");
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 45), "test");
        s.Social.Adjust("c_priest", "c_player", new SocialDelta(Fear: 60, Trust: 20), "test");
        s.Social.Adjust("c_rival", "c_player", new SocialDelta(Suspicion: 60), "test");

        var queue = s.Initiative.Evaluate();

        Assert.Equal(3, queue.Count);
        Assert.SequenceEqual(
            new[] { "c_merchant", "c_priest", "c_rival" },
            queue.Select(i => i.NpcId));
        Assert.SequenceEqual(
            new[] { NpcMotive.Plead, NpcMotive.ThreatenBack, NpcMotive.Confront },
            queue.Select(i => i.Motive));
    }

    [Fact]
    public void Evaluate_respects_pending_and_cooldown()
    {
        var s = CraftedSession();
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 45), "test");

        var first = s.Initiative.Evaluate();
        Assert.Equal(1, first.Count);
        var second = s.Initiative.Evaluate();
        Assert.Equal(1, second.Count);
        Assert.Equal(first[0].NpcId, second[0].NpcId);

        Assert.True(s.Initiative.TryAccept("c_merchant", out _));
        var third = s.Initiative.Evaluate();
        Assert.Equal(0, third.Count);
    }

    [Fact]
    public void TryAccept_returns_false_for_unknown_npc()
    {
        var s = CraftedSession();
        Assert.False(s.Initiative.TryAccept("c_merchant", out var init));
        Assert.True(init is null);
    }

    [Fact]
    public void OpeningLine_records_initiative_exchange()
    {
        var s = CraftedSession();
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 45), "test");
        s.Initiative.Evaluate();
        Assert.True(s.Initiative.TryAccept("c_merchant", out var init));
        Assert.True(init is not null);

        var result = s.Dialogue.OpeningLine("c_merchant", "c_player", init!);

        Assert.True(result.Accepted, "mock opening line must pass the validator");
        Assert.True(result.ReplyText.Length > 0);
        var evt = s.State.EventLog.Events.First(e => e.Id == result.ExchangeEventId);
        Assert.Equal(WorldEventTypes.DialogueExchanged, evt.Type);
        Assert.Equal("true", evt.Data["initiative"]);
        Assert.Equal("Plead", evt.Data["motive"]);
        Assert.False(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.DialogueThreat),
            "an NPC opening line must not run the player-threat pipeline");
    }

    [Fact]
    public void Save_load_preserves_pending_queue()
    {
        var s = CraftedSession();
        s.Social.Adjust("c_merchant", "c_player", new SocialDelta(Fear: 45), "test");
        s.Social.Adjust("c_rival", "c_player", new SocialDelta(Suspicion: 60), "test");
        var before = s.Initiative.Evaluate().Select(Describe).ToList();
        Assert.Equal(2, before.Count);

        var path = Path.Combine(Path.GetTempPath(), "bd-initiative-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            s.Save(path);
            var loaded = GameSession.Load(path, TestSupport.LoadContent());
            Assert.SequenceEqual(before, loaded.Initiative.Pending.Select(Describe));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
