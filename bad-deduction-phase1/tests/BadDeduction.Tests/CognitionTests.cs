using System.Text.Json;
using System.Text.Json.Nodes;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

/// <summary>Phase 4: memory, the 5-layer knowledge model, beliefs and rumors.</summary>
public sealed class CognitionTests
{
    private static long RecordIncident(GameSession s, string who = "c_merchant", string loc = "loc_residential") =>
        s.Events.Record("test.incident", loc, new[] { who }).Id;

    // ------------------------------------------------------- knowledge gate (exit criterion)

    [Fact]
    public void Npc_cannot_know_what_it_never_received()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");

        Assert.True(s.Cognition.Knows("c_merchant", eid));
        Assert.False(s.Cognition.Knows("c_rival", eid), "a bystander elsewhere must not know");
        Assert.False(s.Cognition.Knows("c_priest", eid), "a bystander elsewhere must not know");
        Assert.False(s.Cognition.Knows("c_player", eid), "even the player must not know unperceived events");
    }

    [Fact]
    public void Perceive_is_idempotent()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var eid = RecordIncident(s);
        var first = s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        var second = s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");

        Assert.True(first is not null);
        Assert.True(second is null, "perceiving an already-known event changes nothing");
        Assert.Equal(1, s.Cognition.GetMemories("c_merchant").Count);
    }

    [Fact]
    public void Perceive_validates_character_and_event()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var eid = RecordIncident(s);
        Assert.Throws<ArgumentException>(() => s.Cognition.Perceive("nobody", eid, MemorySource.Witnessed));
        Assert.Throws<ArgumentException>(() => s.Cognition.Perceive("c_merchant", 9999, MemorySource.Witnessed));
    }

    [Fact]
    public void Perceive_logs_a_causally_linked_event()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var cause = RecordIncident(s);
        var logged = s.Cognition.Perceive("c_merchant", cause, MemorySource.Witnessed, "saw the fire", causedBy: cause);

        Assert.True(logged is not null);
        Assert.Equal(WorldEventTypes.MemoryRecorded, logged!.Type);
        Assert.Equal(cause, logged.CausedBy);
        var chain = s.Events.CausalChain(logged.Id);
        Assert.Equal(2, chain.Count);
    }

    [Fact]
    public void Memory_stores_source_confidence_and_timestamp()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Inferred, "worked it out");

        var mems = s.Cognition.GetMemories("c_merchant");
        Assert.Equal(2, mems.Count);
        Assert.Equal(MemorySource.Witnessed, mems[0].Source);
        Assert.Equal(90, mems[0].Confidence);
        Assert.Equal(MemorySource.Inferred, mems[1].Source);
        Assert.Equal(55, mems[1].Confidence);
        Assert.Equal(s.State.TotalMinutes, mems[0].RecordedAt);
        Assert.False(mems[0].IsFoggy);
        Assert.False(mems[0].IsDistorted);
    }

    // ------------------------------------------------------------------ rumors

    [Fact]
    public void Rumor_requires_contact()
    {
        var s = TestSupport.NewPopulatedSession(6);
        var eid = RecordIncident(s); // c_merchant at loc_residential; c_priest at loc_church
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");

        var ex = Assert.Throws<InvalidOperationException>(() => s.Cognition.TellRumor("c_merchant", "c_priest", eid));
        Assert.Contains("no contact", ex.Message);
        Assert.False(s.Cognition.Knows("c_priest", eid));
    }

    [Fact]
    public void Rumor_works_at_the_same_location()
    {
        var s = TestSupport.NewPopulatedSession(6);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        s.World.MoveCharacter("c_priest", "loc_residential");

        var logged = s.Cognition.TellRumor("c_merchant", "c_priest", eid);
        Assert.True(logged is not null);
        Assert.Equal(WorldEventTypes.RumorSpread, logged!.Type);
        Assert.True(s.Cognition.Knows("c_priest", eid));
        var mem = s.Cognition.GetMemories("c_priest")[0];
        Assert.Equal(MemorySource.Told, mem.Source);
        Assert.Equal(eid, mem.EventId);
    }

    [Fact]
    public void Rumor_works_through_a_relationship_edge()
    {
        var s = TestSupport.NewPopulatedSession(6);
        var eid = RecordIncident(s); // different locations, but they have met before
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: 10), "old friends");

        s.Cognition.TellRumor("c_merchant", "c_priest", eid);
        Assert.True(s.Cognition.Knows("c_priest", eid));
    }

    [Fact]
    public void Rumor_cannot_spread_what_the_teller_never_received()
    {
        var s = TestSupport.NewPopulatedSession(6);
        var eid = RecordIncident(s);
        s.World.MoveCharacter("c_priest", "loc_residential");

        var ex = Assert.Throws<InvalidOperationException>(() => s.Cognition.TellRumor("c_merchant", "c_priest", eid));
        Assert.Contains("never received", ex.Message);
    }

    [Fact]
    public void Rumor_to_one_who_already_knows_is_a_noop()
    {
        var s = TestSupport.NewPopulatedSession(6);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        s.Cognition.Perceive("c_priest", eid, MemorySource.Witnessed, "also saw it");

        Assert.True(s.Cognition.TellRumor("c_merchant", "c_priest", eid) is null);
        Assert.Equal(1, s.Cognition.GetMemories("c_priest").Count);
    }

    [Fact]
    public void Rumor_rejects_self_and_strangers()
    {
        var s = TestSupport.NewPopulatedSession(6);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        Assert.Throws<ArgumentException>(() => s.Cognition.TellRumor("c_merchant", "c_merchant", eid));
        Assert.Throws<ArgumentException>(() => s.Cognition.TellRumor("c_merchant", "nobody", eid));
    }

    [Fact]
    public void Telephone_distortion_is_deterministic()
    {
        static string Run(ulong seed)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            var eid = RecordIncident(s);
            s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire clearly");
            s.World.MoveCharacter("c_priest", "loc_residential");
            s.Cognition.TellRumor("c_merchant", "c_priest", eid);
            s.Cognition.TellRumor("c_priest", "c_rival", eid);
            return JsonSerializer.Serialize(s.Cognition.GetMemories("c_rival"));
        }
        Assert.Equal(Run(7), Run(7));
    }

    [Fact]
    public void Rumor_degrades_confidence_along_the_chain()
    {
        // Over many seeds the telephone game must sometimes lose confidence: the teller's
        // witnessed memory (90) cannot always arrive intact.
        var intact = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            var eid = RecordIncident(s);
            s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
            s.World.MoveCharacter("c_priest", "loc_residential");
            s.Cognition.TellRumor("c_merchant", "c_priest", eid);
            if (s.Cognition.GetMemories("c_priest")[0].Confidence == 90) intact++;
        }
        Assert.True(intact < 12, "expected the telephone game to degrade confidence on at least one seed");
    }

    // ------------------------------------------------------------------ decay

    [Fact]
    public void Memories_decay_daily_by_source()
    {
        var s = TestSupport.NewPopulatedSession(9);
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Witnessed, "w");
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Told, "t");
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Inferred, "i");
        s.Time.Advance(24 * 60); // cross exactly one midnight

        var mems = s.Cognition.GetMemories("c_merchant");
        Assert.Equal(88, mems[0].Confidence); // 90 - 2
        Assert.Equal(66, mems[1].Confidence); // 70 - 4
        Assert.Equal(51, mems[2].Confidence); // 55 - 4
    }

    [Fact]
    public void Decay_floors_at_five_and_memories_are_kept()
    {
        var s = TestSupport.NewPopulatedSession(9);
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Told, "t");
        s.Time.Advance(30 * 24 * 60);

        var mem = s.Cognition.GetMemories("c_merchant")[0];
        Assert.Equal(5, mem.Confidence);
        Assert.True(mem.IsFoggy, "below the recall floor the memory is foggy but still stored");
        Assert.Equal(1, s.Cognition.GetMemories("c_merchant").Count);
    }

    [Fact]
    public void Distortion_happens_at_most_once_and_is_deterministic()
    {
        static (bool distorted, string summary) Run(ulong seed)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Told, "plain summary");
            s.Time.Advance(30 * 24 * 60); // 70 -> 5 crosses the 40 distortion threshold exactly once
            var m = s.Cognition.GetMemories("c_merchant")[0];
            return (m.IsDistorted, m.Summary);
        }
        var a = Run(13);
        var b = Run(13);
        Assert.Equal(a, b);
        var hazyCount = a.summary.Split("[hazy]", StringSplitOptions.None).Length - 1;
        Assert.True(hazyCount <= 1, "a memory distorts at most once: the marker must never stack");
    }

    [Fact]
    public void Decay_is_silent()
    {
        var s = TestSupport.NewPopulatedSession(9);
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Witnessed, "w");
        var eventsBefore = s.State.EventLog.Events.Count;
        s.Time.Advance(3 * 24 * 60);
        // Only the three day_started ticks may appear; no per-memory decay events.
        Assert.Equal(eventsBefore + 3, s.State.EventLog.Events.Count);
    }

    // ------------------------------------------------------------------ beliefs

    [Fact]
    public void Belief_confidence_formula_is_documented_math()
    {
        Assert.Equal(50, CognitionRules.BeliefConfidence(0, 0));
        Assert.Equal(58, CognitionRules.BeliefConfidence(1, 0));
        Assert.Equal(66, CognitionRules.BeliefConfidence(2, 0));
        Assert.Equal(34, CognitionRules.BeliefConfidence(0, 2));
        Assert.Equal(95, CognitionRules.BeliefConfidence(10, 0), "clamped: never absolute");
        Assert.Equal(5, CognitionRules.BeliefConfidence(0, 10), "clamped: never impossible");
    }

    [Fact]
    public void Belief_bands_follow_the_named_thresholds()
    {
        Assert.Equal(BeliefBand.Dismissed, CognitionRules.BandOf(39));
        Assert.Equal(BeliefBand.Suspect, CognitionRules.BandOf(40));
        Assert.Equal(BeliefBand.Suspect, CognitionRules.BandOf(59));
        Assert.Equal(BeliefBand.Believes, CognitionRules.BandOf(60));
        Assert.Equal(40, CognitionRules.SuspectThreshold);
        Assert.Equal(60, CognitionRules.BeliefThreshold);
    }

    [Fact]
    public void One_piece_of_evidence_is_suspicion_not_belief()
    {
        var s = TestSupport.NewPopulatedSession(14);
        var logged = s.Cognition.AddEvidence("c_priest", "arson", supports: true);

        var b = s.Cognition.GetBelief("c_priest", "arson");
        Assert.True(b is not null);
        Assert.Equal(58, b!.Confidence);
        Assert.Equal(1, b.EvidenceFor);
        Assert.Equal(BeliefBand.Suspect, s.Cognition.BeliefBandOf("c_priest", "arson"));
        Assert.True(logged is null, "quiet accumulation below the band change is not logged");
    }

    [Fact]
    public void Two_pieces_of_evidence_make_a_belief_and_log_it()
    {
        var s = TestSupport.NewPopulatedSession(14);
        s.Cognition.AddEvidence("c_priest", "arson", supports: true);
        var logged = s.Cognition.AddEvidence("c_priest", "arson", supports: true);

        Assert.Equal(BeliefBand.Believes, s.Cognition.BeliefBandOf("c_priest", "arson"));
        Assert.True(logged is not null);
        Assert.Equal(WorldEventTypes.BeliefChanged, logged!.Type);
        Assert.Equal("Suspect", logged.Data["from"]);
        Assert.Equal("Believes", logged.Data["to"]);
        Assert.Equal("2", logged.Data["for"]);
    }

    [Fact]
    public void Refuting_evidence_lowers_confidence_through_the_bands()
    {
        var s = TestSupport.NewPopulatedSession(14);
        s.Cognition.AddEvidence("c_priest", "arson", supports: true);
        s.Cognition.AddEvidence("c_priest", "arson", supports: true); // 66: believes
        var logged = s.Cognition.AddEvidence("c_priest", "arson", supports: false); // 58: suspect

        Assert.Equal(BeliefBand.Suspect, s.Cognition.BeliefBandOf("c_priest", "arson"));
        Assert.True(logged is not null);
        Assert.Equal("Believes", logged!.Data["from"]);
        Assert.Equal("Suspect", logged.Data["to"]);

        s.Cognition.AddEvidence("c_priest", "arson", supports: false); // 50: still suspect, no log
        s.Cognition.AddEvidence("c_priest", "arson", supports: false); // 42: still suspect
        var dismissed = s.Cognition.AddEvidence("c_priest", "arson", supports: false); // 34: dismissed
        Assert.Equal(BeliefBand.Dismissed, s.Cognition.BeliefBandOf("c_priest", "arson"));
        Assert.True(dismissed is not null);
        Assert.Equal("Dismissed", dismissed!.Data["to"]);
    }

    [Fact]
    public void Unknown_proposition_has_no_band()
    {
        var s = TestSupport.NewPopulatedSession(14);
        Assert.Equal(BeliefBand.None, s.Cognition.BeliefBandOf("c_priest", "arson"));
        Assert.True(s.Cognition.GetBelief("c_priest", "arson") is null);
    }

    [Fact]
    public void AddEvidence_validates_its_inputs()
    {
        var s = TestSupport.NewPopulatedSession(14);
        Assert.Throws<ArgumentException>(() => s.Cognition.AddEvidence("nobody", "arson", true));
        Assert.Throws<ArgumentException>(() => s.Cognition.AddEvidence("c_priest", "", true));
        Assert.Throws<ArgumentException>(() => s.Cognition.AddEvidence("c_priest", "  ", true));
    }

    // ------------------------------------------------------- truth isolation

    [Fact]
    public void Cognition_never_depends_on_who_holds_a_hidden_role()
    {
        static string Run(ulong seed, string malvr)
        {
            var s = CastSupport.NewCastSession(seed);
            s.Identity.AssignRole(malvr, HiddenRole.Malvr);
            var ids = s.State.World.Characters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var a = ids[0];
            var b = ids[1];
            s.World.MoveCharacter(b, s.State.World.Characters[a].CurrentLocationId);
            var eid = s.Events.Record("test.incident", s.State.World.Characters[a].CurrentLocationId, new[] { a }).Id;
            s.Cognition.Perceive(a, eid, MemorySource.Witnessed, "saw it");
            s.Cognition.TellRumor(a, b, eid);
            s.Cognition.AddEvidence(b, "arson", supports: true);
            s.Cognition.AddEvidence(b, "arson", supports: true);
            s.Time.Advance(2 * 24 * 60);
            return JsonSerializer.Serialize(s.State.Cognition);
        }

        var probe = CastSupport.NewCastSession(21);
        var civilians = probe.State.World.Characters.Values
            .Where(c => c.Kind == CharacterKind.Civilian)
            .Select(c => c.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal(Run(21, civilians[0]), Run(21, civilians[5]));
    }

    // ------------------------------------------------------- player journal

    [Fact]
    public void Player_journal_only_contains_perceived_events()
    {
        var s = TestSupport.NewPopulatedSession(18);
        var playerEvent = RecordIncident(s, who: "c_player");
        var otherEvent = RecordIncident(s, who: "c_rival");
        s.Cognition.Perceive("c_player", playerEvent, MemorySource.Witnessed, "I was there");
        s.Cognition.Perceive("c_rival", otherEvent, MemorySource.Witnessed, "rival saw it");

        var known = s.View.KnownEventIds();
        Assert.True(known.Contains(playerEvent));
        Assert.False(known.Contains(otherEvent), "the journal is knowledge-gated");
        var entries = s.View.JournalEntries();
        Assert.Equal(1, entries.Count);
        Assert.Equal(playerEvent, entries[0].Id);
    }

    [Fact]
    public void Rumor_to_the_player_lands_in_the_journal()
    {
        var s = TestSupport.NewPopulatedSession(18);
        var eid = RecordIncident(s, who: "c_merchant");
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        s.World.MoveCharacter("c_player", "loc_residential"); // already there; contact holds
        s.Cognition.TellRumor("c_merchant", "c_player", eid);

        Assert.True(s.View.KnownEventIds().Contains(eid));
    }

    [Fact]
    public void Journal_is_empty_before_anything_is_perceived()
    {
        var s = TestSupport.NewPopulatedSession(18);
        Assert.Equal(0, s.View.KnownEventIds().Count);
        Assert.Equal(0, s.View.JournalEntries().Count);
    }

    // ------------------------------------------------------- save / load

    [Fact]
    public void Save_load_roundtrip_preserves_cognition()
    {
        var s = TestSupport.NewPopulatedSession(22);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
        s.World.MoveCharacter("c_priest", "loc_residential");
        s.Cognition.TellRumor("c_merchant", "c_priest", eid);
        s.Cognition.AddEvidence("c_priest", "arson", supports: true);
        s.Time.Advance(2 * 24 * 60);
        var before = s.StateHash();

        var loaded = GameSession.FromState(SaveSystem.Deserialize(SaveSystem.Serialize(s.State)), TestSupport.LoadContent());
        Assert.Equal(before, loaded.StateHash());
        Assert.True(loaded.Cognition.Knows("c_priest", eid));
        Assert.Equal(BeliefBand.Suspect, loaded.Cognition.BeliefBandOf("c_priest", "arson"));
    }

    [Fact]
    public void Save_continue_is_hash_identical_to_uninterrupted_run()
    {
        static void Script(GameSession s)
        {
            var eid = s.Events.Record("test.incident", "loc_residential", new[] { "c_merchant" }).Id;
            s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the fire");
            s.World.MoveCharacter("c_priest", "loc_residential");
            s.Cognition.TellRumor("c_merchant", "c_priest", eid);
            s.Cognition.AddEvidence("c_priest", "arson", supports: true);
            s.Cognition.AddEvidence("c_priest", "arson", supports: true);
            s.Time.Advance(3 * 24 * 60);
            s.Cognition.TellRumor("c_priest", "c_rival", eid);
        }

        var a = TestSupport.NewPopulatedSession(99);
        Script(a);
        var resumed = GameSession.FromState(SaveSystem.Deserialize(SaveSystem.Serialize(a.State)), TestSupport.LoadContent());
        Script(resumed);

        var b = TestSupport.NewPopulatedSession(99);
        Script(b);
        Script(b);

        Assert.Equal(b.StateHash(), resumed.StateHash());
    }

    [Fact]
    public void Migration_v3_to_v4_initializes_empty_cognition()
    {
        var s = TestSupport.NewPopulatedSession(11);
        s.Cognition.Perceive("c_player", RecordIncident(s, who: "c_player"), MemorySource.Witnessed, "saw it");

        var node = JsonNode.Parse(SaveSystem.Serialize(s.State))!.AsObject();
        node["FormatVersion"] = 3;
        node["State"]!.AsObject().Remove("Cognition");

        var migrated = SaveSystem.Deserialize(node.ToJsonString());
        Assert.Equal(1, migrated.Cognition.NextMemoryId);
        Assert.Equal(0, migrated.Cognition.Memories.Count);
        Assert.Equal(0, migrated.Cognition.Beliefs.Count);
        Assert.Equal(0, migrated.Cognition.PlayerJournal.Count);
        Assert.False(migrated.Cognition.CognitionRng.IsZero);
        Assert.Equal(0, GameStateValidator.Validate(migrated).Count);

        // And the migrated save keeps working: perceiving after migration is deterministic.
        var session = GameSession.FromState(migrated, TestSupport.LoadContent());
        var eid = session.Events.Record("test.late", "loc_church", new[] { "c_priest" }).Id;
        session.Cognition.Perceive("c_priest", eid, MemorySource.Witnessed, "late witness");
        Assert.True(session.Cognition.Knows("c_priest", eid));
    }

    // ------------------------------------------------------- validator

    [Fact]
    public void Validator_rejects_broken_cognition_state()
    {
        var s = TestSupport.NewPopulatedSession(5);
        s.Cognition.Perceive("c_merchant", RecordIncident(s), MemorySource.Witnessed, "saw the fire");

        s.State.Cognition.Memories["c_merchant"][0].Confidence = 140;
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("confidence outside 0-100")));

        s.State.Cognition.Memories["c_merchant"][0].Confidence = 90;
        s.State.Cognition.NextMemoryId = 0;
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("NextMemoryId")));

        s.State.Cognition.NextMemoryId = 2;
        s.State.Cognition.KnownEvents["c_merchant"].Add(424242);
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("unknown event")));
    }

    [Fact]
    public void Fresh_session_passes_cognition_validation()
    {
        var s = TestSupport.NewPopulatedSession(5);
        Assert.Equal(0, GameStateValidator.Validate(s.State).Count);
    }
}
