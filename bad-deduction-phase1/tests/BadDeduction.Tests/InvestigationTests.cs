using System.Text.Json.Nodes;
using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Investigation;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class InvestigationTests
{
    // ------------------------------------------------------------ helpers

    private static (GameSession Session, CrimeRecord Crime, string Discoverer) SetupCrime(ulong seed = 42)
    {
        var s = TestSupport.NewPopulatedSession(seed);
        var crime = s.Crime.GenerateIncident("murder");
        s.Time.Advance(200); // past the 120-minute discovery delay
        var mover = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != crime.VictimId && c.CurrentLocationId != crime.LocationId)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .First().Id;
        s.World.MoveCharacter(mover, crime.LocationId); // arrival bus -> discovery
        return (s, crime, mover);
    }

    private static (GameSession Session, CrimeRecord Crime, string EvidenceId) SetupEvidence(ulong seed = 42)
    {
        var (s, crime, discoverer) = SetupCrime(seed);
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId).First(e => !e.Discovered);
        s.Crime.DiscoverEvidence(discoverer, evidence.Id);
        return (s, crime, evidence.Id);
    }

    // ------------------------------------------------------------ surveillance

    [Fact]
    public void Survey_reconstructs_presence_windows_from_moves()
    {
        var s = TestSupport.NewPopulatedSession(42); // t=360
        s.World.MoveCharacter("c_merchant", "loc_tavern"); // ts=360
        s.Time.Advance(60); // t=420
        s.World.MoveCharacter("c_merchant", "loc_residential"); // ts=420

        var rec = s.Investigate.SurveyLocation("loc_tavern", 300, 500);
        Assert.Equal("loc_tavern", rec.LocationId);
        Assert.Equal(300, rec.WindowStart);
        Assert.Equal(500, rec.WindowEnd);
        var entry = rec.Entries.Single(e => e.CharacterId == "c_merchant");
        Assert.Equal(360, entry.EnteredAt);
        Assert.Equal(420, entry.ExitedAt);
    }

    [Fact]
    public void Survey_empty_window_has_no_sightings()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var rec = s.Investigate.SurveyLocation("loc_tavern", 300, 500);
        Assert.Equal(0, rec.Entries.Count);
    }

    [Fact]
    public void Survey_is_deterministic()
    {
        var a = TestSupport.NewPopulatedSession(42);
        var b = TestSupport.NewPopulatedSession(42);
        TestSupport.Simulate(a, 20);
        TestSupport.Simulate(b, 20);
        var ra = a.Investigate.SurveyLocation("loc_tavern", 0, 2000);
        var rb = b.Investigate.SurveyLocation("loc_tavern", 0, 2000);
        Assert.Equal(ra.Entries.Count, rb.Entries.Count);
        for (var i = 0; i < ra.Entries.Count; i++)
        {
            Assert.Equal(ra.Entries[i].CharacterId, rb.Entries[i].CharacterId);
            Assert.Equal(ra.Entries[i].EnteredAt, rb.Entries[i].EnteredAt);
            Assert.Equal(ra.Entries[i].ExitedAt, rb.Entries[i].ExitedAt);
        }
    }

    [Fact]
    public void Survey_rejects_bad_input()
    {
        var s = TestSupport.NewPopulatedSession(42);
        Assert.Throws<KeyNotFoundException>(() => s.Investigate.SurveyLocation("loc_nope", 0, 100));
        Assert.Throws<ArgumentException>(() => s.Investigate.SurveyLocation("loc_tavern", 200, 100));
        Assert.Throws<ArgumentException>(() => s.Investigate.SurveyLocation("loc_tavern", -1, 100));
    }

    [Fact]
    public void Survey_logs_an_event()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        var before = s.State.EventLog.Events.Count;
        s.Investigate.SurveyLocation("loc_tavern", 300, 500, crime.Id);
        var evt = s.State.EventLog.Events[before]; // the survey event
        Assert.Equal(WorldEventTypes.Surveyed, evt.Type);
        Assert.Equal("loc_tavern", evt.Data["location"]);
        Assert.Equal(crime.Id, evt.Data["crime"]);
    }

    [Fact]
    public void Survey_marks_travel_as_absence()
    {
        var s = CastSupport.NewCastSession(42);
        s.Simulate.Advance(2 * 1440);
        var departed = s.State.EventLog.Events
            .FirstOrDefault(e => e.Type == WorldEventTypes.CharacterDeparted);
        Assert.True(departed is not null, "a day of simulated routine should include travel");
        var charId = departed!.Participants[0];
        var dep = long.Parse(departed.Data["departure"]);
        var arr = long.Parse(departed.Data["arrival"]);
        var from = departed.Data["from"];
        var to = departed.Data["to"];

        var (beforeLoc, _, _) = PresenceTracker.PresenceAt(s.State, charId, dep - 1);
        Assert.Equal(from, beforeLoc);
        var (midLoc, midFrom, midTo) = PresenceTracker.PresenceAt(s.State, charId, dep);
        Assert.True(midLoc is null, "traveling characters are not present anywhere");
        Assert.Equal(from, midFrom);
        Assert.Equal(to, midTo);
        var (afterLoc, _, _) = PresenceTracker.PresenceAt(s.State, charId, arr);
        Assert.Equal(to, afterLoc);

        // The origin's surveillance shows the traveler leaving at departure, not lingering.
        var rec = s.Investigate.SurveyLocation(from, dep - 10, arr + 10);
        var entry = rec.Entries.FirstOrDefault(e => e.CharacterId == charId);
        Assert.True(entry is null || entry.ExitedAt <= dep,
            "surveillance must not show the traveler present after departure");
    }

    // ------------------------------------------------------------ interviews

    [Fact]
    public void Interview_witness_reports_their_memory()
    {
        var (s, crime, witness) = SetupCrime();
        var result = s.Investigate.Interview("c_player", witness, InvestigationRules.EventTopic(crime.IncidentEventId));
        Assert.True(result.Answer.Length > 0);
        Assert.NotEqual(InvestigationRules.DontKnowClaim, result.Answer);
        Assert.Contains("crime.incident", result.Answer); // their memory summary of the incident
        var stmt = s.Investigate.GetStatement(result.StatementId);
        Assert.Equal(InvestigationRules.EventTopic(crime.IncidentEventId), stmt.Topic);
        Assert.Equal(witness, stmt.SpeakerId);
        // The crime resolved automatically from the incident event.
        var evt = s.State.EventLog.Events.First(e => e.Id == result.InterviewEventId);
        Assert.Equal(WorldEventTypes.Interviewed, evt.Type);
        Assert.Equal(crime.Id, evt.Data["crime"]);
    }

    [Fact]
    public void Interview_non_witness_says_dont_know()
    {
        var (s, crime, _) = SetupCrime();
        var outsider = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Id != "c_player" && !s.Cognition.Knows(c.Id, crime.IncidentEventId)).Id;
        var result = s.Investigate.Interview("c_player", outsider, InvestigationRules.EventTopic(crime.IncidentEventId));
        Assert.Equal(InvestigationRules.DontKnowClaim, result.Answer);
        var stmt = s.Investigate.GetStatement(result.StatementId);
        Assert.Equal(InvestigationRules.DontKnowClaim, stmt.Claim);
    }

    [Fact]
    public void Interview_person_reports_the_relationship()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.Relationships.Connect("c_merchant", "c_priest", RelationshipKind.Friend);
        var result = s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.PersonTopic("c_priest"));
        Assert.Contains("Friend", result.Answer);

        var stranger = s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.PersonTopic("c_guard"));
        Assert.Contains("don't really know", stranger.Answer);
    }

    [Fact]
    public void Interview_whereabouts_reports_a_structured_claim()
    {
        var s = TestSupport.NewPopulatedSession(42); // t=360, nobody has moved
        var result = s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.WhereaboutsTopic(360));
        Assert.Equal(InvestigationRules.FormatWhereabouts("loc_residential", 360),
            s.Investigate.GetStatement(result.StatementId).Claim);
        Assert.Contains("Residential", result.Answer);
    }

    [Fact]
    public void Interview_rejects_bad_input()
    {
        var s = TestSupport.NewPopulatedSession(42);
        Assert.Throws<ArgumentException>(() => s.Investigate.Interview("c_player", "c_nobody", InvestigationRules.WhereaboutsTopic(10)));
        Assert.Throws<ArgumentException>(() => s.Investigate.Interview("c_player", "c_player", InvestigationRules.WhereaboutsTopic(10)));
        Assert.Throws<ArgumentException>(() => s.Investigate.Interview("c_player", "c_merchant", "nonsense"));
        Assert.Throws<ArgumentException>(() => s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.EventTopic(99999)));
        Assert.Throws<ArgumentException>(() => s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.WhereaboutsTopic(s.State.TotalMinutes + 1)));
    }

    [Fact]
    public void Interrogate_marks_pressure_and_nudges_suspicion()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var before = s.Social.View("c_merchant", "c_player").Suspicion;
        var result = s.Investigate.Interrogate("c_player", "c_merchant", InvestigationRules.WhereaboutsTopic(360));
        Assert.True(result.Pressured);
        var evt = s.State.EventLog.Events.First(e => e.Id == result.InterviewEventId);
        Assert.Equal(WorldEventTypes.Interrogated, evt.Type);
        Assert.Equal("true", evt.Data["pressure"]);
        var after = s.Social.View("c_merchant", "c_player").Suspicion;
        Assert.Equal(Math.Min(100, before + 5), after);
    }

    // ------------------------------------------------------------ statements & contradictions

    [Fact]
    public void Blatant_contradiction_is_flagged_at_easy()
    {
        var s = TestSupport.NewPopulatedSession(42, difficulty: Difficulty.Easy);
        s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(300),
            InvestigationRules.FormatWhereabouts("loc_tavern", 300));
        var r2 = s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(320),
            InvestigationRules.FormatWhereabouts("loc_church", 320));
        Assert.Equal(1, r2.FlaggedCount);
        var contra = s.Investigate.GetContradiction(r2.ContradictionIds[0]);
        Assert.Equal(ContradictionSeverity.Blatant, contra.Severity);
        Assert.True(contra.Flagged);
        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.ContradictionFound));
    }

    [Fact]
    public void Subtle_detail_mismatch_is_ignored_at_easy_but_flagged_at_hard()
    {
        string[] speakers = { "c_merchant" };
        foreach (var difficulty in new[] { Difficulty.Easy, Difficulty.Hard })
        {
            var s = TestSupport.NewPopulatedSession(42, difficulty: difficulty);
            s.Investigate.RecordStatement(speakers[0], InvestigationRules.WhereaboutsTopic(300),
                InvestigationRules.FormatWhereabouts("loc_tavern", 300, "drinking alone"));
            var r2 = s.Investigate.RecordStatement(speakers[0], InvestigationRules.WhereaboutsTopic(310),
                InvestigationRules.FormatWhereabouts("loc_tavern", 310, "playing cards"));
            Assert.Equal(1, r2.ContradictionIds.Count);
            var contra = s.Investigate.GetContradiction(r2.ContradictionIds[0]);
            Assert.Equal(ContradictionSeverity.Subtle, contra.Severity);
            if (difficulty == Difficulty.Easy)
            {
                Assert.Equal(0, r2.FlaggedCount);
                Assert.False(contra.Flagged);
                Assert.False(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.ContradictionFound),
                    "sub-threshold clashes are recorded, not logged");
            }
            else
            {
                Assert.Equal(1, r2.FlaggedCount);
                Assert.True(contra.Flagged);
            }
        }
    }

    [Fact]
    public void Denial_then_claim_is_flagged_at_medium_but_not_easy()
    {
        foreach (var difficulty in new[] { Difficulty.Easy, Difficulty.Medium })
        {
            var s = TestSupport.NewPopulatedSession(42, difficulty: difficulty);
            var crime = s.Crime.GenerateIncident("murder");
            var incidentTopic = InvestigationRules.EventTopic(crime.IncidentEventId);
            s.Investigate.RecordStatement("c_merchant", incidentTopic, InvestigationRules.DontKnowClaim);
            var r2 = s.Investigate.RecordStatement("c_merchant", incidentTopic, "I saw the whole thing.");
            Assert.Equal(1, r2.ContradictionIds.Count);
            Assert.Equal(difficulty == Difficulty.Medium ? 1 : 0, r2.FlaggedCount);
        }
    }

    [Fact]
    public void Consistent_statements_produce_no_contradictions()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(300),
            InvestigationRules.FormatWhereabouts("loc_tavern", 300));
        var r2 = s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(300),
            InvestigationRules.FormatWhereabouts("loc_tavern", 300));
        Assert.Equal(0, r2.ContradictionIds.Count);
        Assert.Equal(0, s.State.Investigation.Contradictions.Count);
    }

    [Fact]
    public void Genius_holds_alibis_to_a_wider_window()
    {
        // 90 minutes apart at different places: inside Genius's 120-minute window, outside Easy's 60.
        foreach (var difficulty in new[] { Difficulty.Easy, Difficulty.Genius })
        {
            var s = TestSupport.NewPopulatedSession(42, difficulty: difficulty);
            s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(240),
                InvestigationRules.FormatWhereabouts("loc_tavern", 240));
            var r2 = s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(330),
                InvestigationRules.FormatWhereabouts("loc_church", 330));
            if (difficulty == Difficulty.Genius)
            {
                Assert.Equal(1, r2.FlaggedCount);
            }
            else
            {
                Assert.Equal(0, r2.ContradictionIds.Count);
            }
        }
    }

    [Fact]
    public void Surveillance_check_catches_a_false_alibi()
    {
        var s = TestSupport.NewPopulatedSession(42); // nobody has moved; merchant is home
        var r = s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(300),
            InvestigationRules.FormatWhereabouts("loc_tavern", 300));
        var contra = s.Investigate.CheckAgainstSurveillance(r.StatementId);
        Assert.True(contra is not null);
        Assert.Equal(ContradictionSeverity.Blatant, contra!.Severity);
        Assert.True(contra!.Flagged, "investigator-initiated checks are always raised");
        Assert.Contains("surveillance", contra!.Reason);
    }

    [Fact]
    public void Surveillance_check_passes_a_truthful_claim()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var r = s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(300),
            InvestigationRules.FormatWhereabouts("loc_residential", 300));
        Assert.True(s.Investigate.CheckAgainstSurveillance(r.StatementId) is null);
    }

    [Fact]
    public void RecordStatement_validates_input()
    {
        var s = TestSupport.NewPopulatedSession(42);
        Assert.Throws<ArgumentException>(() => s.Investigate.RecordStatement("c_nobody", InvestigationRules.WhereaboutsTopic(10), "x"));
        Assert.Throws<ArgumentException>(() => s.Investigate.RecordStatement("c_merchant", "bogus", "x"));
        Assert.Throws<ArgumentException>(() => s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(10), ""));
        Assert.Throws<ArgumentException>(() => s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(10),
            new string('x', InvestigationRules.MaxClaimLength + 1)));
    }

    // ------------------------------------------------------------ hypotheses

    [Fact]
    public void Propose_hypothesis_logs_an_event_and_rejects_duplicates()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        var hyp = s.Investigate.ProposeHypothesis("rival-did-it", "The rival did it.", crime.Id);
        Assert.Equal("hyp_1", hyp.Id);
        Assert.Equal(50, hyp.Confidence); // neutral until evidence lands
        var evt = s.State.EventLog.Events.First(e => e.Type == WorldEventTypes.HypothesisProposed);
        Assert.Equal(crime.Id, evt.Data["crime"]);
        Assert.Throws<InvalidOperationException>(() => s.Investigate.ProposeHypothesis("rival-did-it", "Again."));
        Assert.Throws<ArgumentException>(() => s.Investigate.ProposeHypothesis("", "No id."));
    }

    [Fact]
    public void Attach_evidence_moves_confidence_through_bands()
    {
        var (s, crime, firstEvidence) = SetupEvidence();
        var second = s.Crime.EvidenceAtScene(crime.SceneId).First(e => e.Id != firstEvidence && !e.Discovered);
        // The discoverer is still at the scene and knows the incident.
        var discoverer = s.Crime.GetScene(crime.SceneId).DiscoveredBy!;
        s.Crime.DiscoverEvidence(discoverer, second.Id);

        var hyp = s.Investigate.ProposeHypothesis("rival-did-it", "The rival did it.", crime.Id);
        s.Investigate.AttachEvidence(hyp.Id, firstEvidence, supports: true);
        Assert.Equal(58, s.Investigate.GetHypothesis(hyp.Id).Confidence);
        Assert.Equal(Cognition.BeliefBand.Suspect, s.Investigate.GetHypothesis(hyp.Id).Band);
        Assert.False(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.HypothesisUpdated),
            "Suspect -> Suspect is not a band transition");

        var updated = s.Investigate.AttachEvidence(hyp.Id, second.Id, supports: true);
        Assert.True(updated is not null);
        Assert.Equal(66, s.Investigate.GetHypothesis(hyp.Id).Confidence);
        Assert.Equal(Cognition.BeliefBand.Believes, s.Investigate.GetHypothesis(hyp.Id).Band);
        Assert.Equal("Suspect", updated!.Data["from"]);
        Assert.Equal("Believes", updated!.Data["to"]);
        Assert.Equal(crime.Id, updated!.Data["crime"]);
    }

    [Fact]
    public void Attach_refuting_evidence_lowers_confidence()
    {
        var (s, crime, firstEvidence) = SetupEvidence();
        var hyp = s.Investigate.ProposeHypothesis("rival-did-it", "The rival did it.");
        s.Investigate.AttachEvidence(hyp.Id, firstEvidence, supports: true);
        s.Investigate.AttachEvidence(hyp.Id, firstEvidence, supports: false); // moves sides
        var after = s.Investigate.GetHypothesis(hyp.Id);
        Assert.Equal(42, after.Confidence);
        Assert.Equal(Cognition.BeliefBand.Suspect, after.Band);
        Assert.True(after.RefutingEvidenceIds.Contains(firstEvidence));
        Assert.False(after.SupportingEvidenceIds.Contains(firstEvidence));
    }

    [Fact]
    public void Attach_evidence_validates()
    {
        var (s, _, evidenceId) = SetupEvidence();
        var hyp = s.Investigate.ProposeHypothesis("p1", "d1");
        Assert.Throws<KeyNotFoundException>(() => s.Investigate.AttachEvidence(hyp.Id, "ev_nope", true));
        Assert.Throws<ArgumentException>(() => s.Investigate.AttachEvidence("hyp_nope", evidenceId, true));

        // Undiscovered evidence cannot theorize.
        var s2 = TestSupport.NewPopulatedSession(7);
        var crime2 = s2.Crime.GenerateIncident("murder");
        var hidden = s2.Crime.EvidenceAtScene(crime2.SceneId).First(e => !e.Discovered);
        var hyp2 = s2.Investigate.ProposeHypothesis("p2", "d2");
        Assert.Throws<InvalidOperationException>(() => s2.Investigate.AttachEvidence(hyp2.Id, hidden.Id, true));

        // Re-attaching to the same side is a quiet no-op.
        Assert.True(s.Investigate.AttachEvidence(hyp.Id, evidenceId, supports: true) is null);
    }

    [Fact]
    public void False_evidence_can_support_a_hypothesis_and_stays_false()
    {
        for (ulong seed = 1; seed < 50; seed++)
        {
            var (s, crime, discoverer) = SetupCrime(seed);
            var falseItem = s.Crime.EvidenceAtScene(crime.SceneId)
                .FirstOrDefault(e => e.Authenticity == Authenticity.False);
            if (falseItem is null) continue;
            s.Crime.DiscoverEvidence(discoverer, falseItem.Id);
            var hyp = s.Investigate.ProposeHypothesis("frame-job", "Someone is being framed.");
            s.Investigate.AttachEvidence(hyp.Id, falseItem.Id, supports: true);
            Assert.Equal(Authenticity.False, s.Crime.GetEvidence(falseItem.Id).Authenticity);
            Assert.True(s.Investigate.GetHypothesis(hyp.Id).SupportingEvidenceIds.Contains(falseItem.Id));
            return;
        }
        throw new Exception("no seed in 1..49 produced false evidence");
    }

    // ------------------------------------------------------------ timeline

    [Fact]
    public void Case_timeline_merges_crime_and_investigation_events_in_order()
    {
        var (s, crime, discoverer) = SetupCrime();
        var interview = s.Investigate.Interview("c_player", discoverer,
            InvestigationRules.EventTopic(crime.IncidentEventId)); // crime auto-linked
        s.Investigate.SurveyLocation(crime.LocationId, 300, 700, crime.Id);
        var hyp = s.Investigate.ProposeHypothesis("p1", "d1", crime.Id);

        var timeline = s.Investigate.GetCaseTimeline(crime.Id);
        var types = timeline.Select(e => e.Type).ToList();
        Assert.True(types.IndexOf(WorldEventTypes.CrimeIncident) < types.IndexOf(WorldEventTypes.CrimeDiscovered));
        Assert.True(types.IndexOf(WorldEventTypes.CrimeDiscovered) < types.IndexOf(WorldEventTypes.Interviewed));
        Assert.True(types.IndexOf(WorldEventTypes.Interviewed) < types.IndexOf(WorldEventTypes.StatementRecorded));
        Assert.True(types.IndexOf(WorldEventTypes.StatementRecorded) < types.IndexOf(WorldEventTypes.Surveyed));
        Assert.True(types.IndexOf(WorldEventTypes.Surveyed) < types.IndexOf(WorldEventTypes.HypothesisProposed));
        // Chronological order holds across the whole merge.
        for (var i = 1; i < timeline.Count; i++)
            Assert.True(timeline[i].Timestamp >= timeline[i - 1].Timestamp);

        Assert.Throws<KeyNotFoundException>(() => s.Investigate.GetCaseTimeline("crime_nope"));
    }

    // ------------------------------------------------------------ save / determinism / validation

    [Fact]
    public void Save_load_roundtrip_is_hash_identical()
    {
        var (s, crime, _) = SetupCrime();
        s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.WhereaboutsTopic(400), crime.Id);
        s.Investigate.RecordStatement("c_merchant", InvestigationRules.WhereaboutsTopic(400),
            InvestigationRules.FormatWhereabouts("loc_tavern", 400), crime.Id);
        s.Investigate.ProposeHypothesis("p1", "d1", crime.Id);

        var path = Path.Combine(Path.GetTempPath(), "bd-inv-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            s.Save(path);
            var loaded = GameSession.Load(path, TestSupport.LoadContent());
            Assert.Equal(s.StateHash(), loaded.StateHash());
            Assert.Equal(s.Investigate.AllHypotheses().Count, loaded.Investigate.AllHypotheses().Count);
            Assert.Equal(s.Investigate.StatementsBy("c_merchant").Count,
                loaded.Investigate.StatementsBy("c_merchant").Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Same_seed_same_interviews_same_hash()
    {
        GameSession Scripted(ulong seed)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            s.Investigate.Interview("c_player", "c_merchant", InvestigationRules.WhereaboutsTopic(300));
            s.Investigate.RecordStatement("c_priest", InvestigationRules.WhereaboutsTopic(300),
                InvestigationRules.FormatWhereabouts("loc_church", 300));
            s.Investigate.ProposeHypothesis("p1", "d1");
            return s;
        }
        Assert.Equal(Scripted(42).StateHash(), Scripted(42).StateHash());
    }

    [Fact]
    public void Version_6_saves_migrate_to_the_current_format()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var json = SaveSystem.Serialize(s.State);
        var node = JsonNode.Parse(json)!.AsObject();
        node["State"]!.AsObject().Remove("Investigation");
        node["FormatVersion"] = 6;
        var v6 = node.ToJsonString();
        Assert.False(v6.Contains("\"Investigation\""), "test setup: v6 JSON should not have Investigation");
        var migrated = SaveSystem.Deserialize(v6);
        Assert.True(migrated.Investigation is not null);
        Assert.Equal(0, migrated.Investigation!.Statements.Count);
        Assert.Equal(1, migrated.Investigation.NextStatementId);
        Assert.Equal(0, GameStateValidator.Validate(migrated).Count);
    }

    [Fact]
    public void Validator_rejects_broken_investigation_state()
    {
        var s = TestSupport.NewPopulatedSession(42);
        s.State.Investigation.Statements["stmt_x"] = new Statement
        {
            Id = "stmt_y", // key mismatch
            SpeakerId = "c_nobody",
            Topic = "bogus",
            Claim = "",
            Timestamp = -5,
        };
        s.State.Investigation.Hypotheses["hyp_x"] = new Hypothesis
        {
            Id = "hyp_x", PropositionId = "p", Description = "d",
            SupportingEvidenceIds = new List<string> { "ev_nobody" },
        };
        var errors = GameStateValidator.Validate(s.State);
        Assert.True(errors.Count >= 5, $"expected several errors, got: {string.Join("; ", errors)}");
    }
}
