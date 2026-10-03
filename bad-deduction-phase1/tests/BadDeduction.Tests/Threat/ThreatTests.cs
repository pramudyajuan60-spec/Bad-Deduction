using BadDeduction.AI;
using BadDeduction.Core;
using BadDeduction.Police;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests.Threat;

public sealed class ThreatTests
{
    // ------------------------------------------------------- ThreatDetector

    [Fact]
    public void Detect_death_threat_english()
    {
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("I want to kill you"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("I'll kill you"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("I will kill you"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("I'm going to murder you"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("You are dead."));
    }

    [Fact]
    public void Detect_explicit_threat_english()
    {
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("I'll hurt you"));
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("watch your back"));
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("This is your last warning"));
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("I'm warning you"));
    }

    [Fact]
    public void Detect_menacing_english()
    {
        Assert.Equal(ThreatLevel.Menacing, ThreatDetector.Detect("don't test me"));
        Assert.Equal(ThreatLevel.Menacing, ThreatDetector.Detect("don't push me"));
    }

    [Fact]
    public void Detect_death_threat_indonesian()
    {
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("akan kubunuh"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("kubunuh kau"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("Aku akan membunuhmu"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("mati kau"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("kau mati"));
    }

    [Fact]
    public void Detect_explicit_threat_indonesian()
    {
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("awas kau"));
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("ini peringatan terakhir"));
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("akan kuhajar kau"));
    }

    [Fact]
    public void Detect_is_case_insensitive_and_punctuated()
    {
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("I WILL KILL YOU!"));
        Assert.Equal(ThreatLevel.DeathThreat, ThreatDetector.Detect("AKAN KUBUNUH!!!"));
        Assert.Equal(ThreatLevel.ExplicitThreat, ThreatDetector.Detect("Watch. Your. Back."));
    }

    [Fact]
    public void Detect_negatives()
    {
        Assert.Equal(ThreatLevel.None, ThreatDetector.Detect("skill issue"));
        Assert.Equal(ThreatLevel.None, ThreatDetector.Detect("killing time"));
        Assert.Equal(ThreatLevel.None, ThreatDetector.Detect("Aku hampir dibunuh kemarin"));
        Assert.Equal(ThreatLevel.None, ThreatDetector.Detect("hello there"));
        Assert.Equal(ThreatLevel.None, ThreatDetector.Detect(""));
        Assert.Equal(ThreatLevel.None, ThreatDetector.Detect("   "));
    }

    // ------------------------------------------------------- threat as game event

    [Fact]
    public void Exchange_with_death_threat_records_threat_event_and_fallout()
    {
        var s = TestSupport.NewPopulatedSession(7);
        // c_player, c_rival, c_merchant and c_guard (police) all start at loc_residential.
        var fearBefore = s.Social.View("c_merchant", "c_player").Fear;
        var trustBefore = s.Social.View("c_merchant", "c_player").Trust;
        var rivalSuspicionBefore = s.Social.View("c_rival", "c_player").Suspicion;

        var result = s.Dialogue.Exchange("c_merchant", "c_player", "I will kill you");

        Assert.True(result.Accepted, "mock threat output must pass the validator");
        var threat = s.State.EventLog.Events.FirstOrDefault(e => e.Type == WorldEventTypes.DialogueThreat);
        Assert.True(threat is not null, "a dialogue.threat event must be recorded");
        Assert.Equal(result.ExchangeEventId, threat!.CausedBy);
        Assert.Equal("DeathThreat", threat.Data["level"]);
        Assert.Equal("3", threat.Data["severity"]);
        Assert.True(threat.Participants[0] == "c_player" && threat.Participants[1] == "c_merchant",
            "participants are [threatener, target]");

        // Target's view of the threatener shifts hard (plus the mock's own -3 dialogue delta).
        Assert.Equal(fearBefore + 40, s.Social.View("c_merchant", "c_player").Fear);
        Assert.Equal(trustBefore - 15 + result.TrustDelta, s.Social.View("c_merchant", "c_player").Trust);

        // A witness (c_rival) grows wary of the threatener.
        Assert.Equal(rivalSuspicionBefore + 10, s.Social.View("c_rival", "c_player").Suspicion);

        // Both the target and witnesses perceived the threat through the knowledge gate.
        Assert.True(s.Cognition.Knows("c_merchant", threat.Id), "target must know about the threat");
        Assert.True(s.Cognition.Knows("c_rival", threat.Id), "witness must know about the threat");
    }

    [Fact]
    public void Death_threat_in_public_without_police_reports_disturbance()
    {
        var s = TestSupport.NewPopulatedSession(7);
        // Public location, no police present: c_guard stays at loc_residential.
        s.World.MoveCharacter("c_player", "loc_central_market");
        s.World.MoveCharacter("c_merchant", "loc_central_market");

        s.Dialogue.Exchange("c_merchant", "c_player", "akan kubunuh");

        var disturbance = s.State.EventLog.Events
            .FirstOrDefault(e => e.Type == WorldEventTypes.PoliceDisturbance);
        Assert.True(disturbance is not null, "public death threat must file a disturbance report");
        Assert.Equal("c_player", disturbance!.Data["subject"]);
        Assert.Equal(1, s.State.Police.Disturbances.Count);
        Assert.Equal("loc_central_market", s.State.Police.Disturbances[0].LocationId);
    }

    [Fact]
    public void Death_threat_with_police_witness_reports_disturbance_even_when_private()
    {
        var s = TestSupport.NewPopulatedSession(7);
        // loc_residential is Private, but c_guard (police) is there to witness it.
        Assert.True(s.State.World.Characters["c_guard"].CurrentLocationId == "loc_residential");

        s.Dialogue.Exchange("c_merchant", "c_player", "I will kill you");

        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.PoliceDisturbance),
            "death threat witnessed by police must file a disturbance report");
    }

    [Fact]
    public void Non_death_threat_never_reports_disturbance()
    {
        var s = TestSupport.NewPopulatedSession(7);
        s.World.MoveCharacter("c_player", "loc_central_market");
        s.World.MoveCharacter("c_merchant", "loc_central_market");

        s.Dialogue.Exchange("c_merchant", "c_player", "watch your back");

        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.DialogueThreat),
            "explicit threats are still threat events");
        Assert.False(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.PoliceDisturbance),
            "explicit threats must not file disturbance reports");
        Assert.Equal(0, s.State.Police.Disturbances.Count);
    }

    [Fact]
    public void Two_disturbances_raise_alert_to_alert_but_never_manhunt()
    {
        var s = TestSupport.NewPopulatedSession(7);
        Assert.Equal(AlertLevel.Calm, s.State.Police.Alert);

        s.Police.ReportDisturbance("c_rival", "loc_central_market", 3, null);
        Assert.Equal(AlertLevel.Calm, s.State.Police.Alert);

        s.Police.ReportDisturbance("c_rival", "loc_central_market", 3, null);
        Assert.Equal(AlertLevel.Alert, s.State.Police.Alert);

        // Threats alone never reach Manhunt (ADR-048): the ladder steps on facts, never jumps.
        s.Police.ReportDisturbance("c_rival", "loc_central_market", 3, null);
        s.Police.ReportDisturbance("c_rival", "loc_central_market", 3, null);
        s.Police.ReportDisturbance("c_rival", "loc_central_market", 3, null);
        Assert.Equal(AlertLevel.Alert, s.State.Police.Alert);
    }

    // ------------------------------------------------------- prompt + mock

    [Fact]
    public void Prompt_with_threat_has_threat_section_and_no_leaks()
    {
        var s = TestSupport.NewPopulatedSession(11);
        var engine = new ContextEngine(s.View, s.Cognition, s.Social, s.Relationships,
            s.Content, s.State.World.Profiles,
            id => s.State.World.Characters[id].CurrentLocationId);

        var prompt = engine.BuildPrompt("c_merchant", "c_player", "I will kill you",
            Difficulty.Medium, new GameTime(s.State.TotalMinutes), ThreatLevel.DeathThreat);

        Assert.Contains("THREAT", prompt);
        Assert.Contains("FIRST and DIRECTLY", prompt);
        Assert.Contains("death threat", prompt);
        Assert.Contains("I will kill you", prompt);
        Assert.False(prompt.Contains("malvr", StringComparison.OrdinalIgnoreCase), "threat section must not leak role words");
        Assert.False(prompt.Contains("lumiel", StringComparison.OrdinalIgnoreCase), "threat section must not leak role words");
    }

    [Fact]
    public void Mock_provider_answers_threats_from_threat_templates()
    {
        var threatReplies = new[]
        {
            "W-wait. Take that back — I don't want trouble.",
            "Threaten me again and I'll scream for the guard!",
            "You don't frighten me. Say it again and we'll see.",
            "Please. There's no need for threats — let's talk.",
        };

        var mock = new MockAIProvider(99);
        var response = mock.Complete(new AIRequest
        {
            SpeakerId = "c_merchant",
            SpeakerName = "A Merchant",
            ListenerId = "c_player",
            ListenerName = "The Investigator",
            ConversationId = "test",
            Utterance = "I will kill you",
        });
        Assert.False(response.Refused);
        Assert.True(threatReplies.Contains(response.Output!.ReplyText),
            $"threat reply must come from the threat pool, got: '{response.Output.ReplyText}'");
        Assert.Equal(-3, response.Output.TrustDelta);
        Assert.Equal(4, response.Output.SuspicionDelta);

        // Through the full exchange path the NPC also reacts to the threat, not the generic pool.
        var s = TestSupport.NewPopulatedSession(7);
        var result = s.Dialogue.Exchange("c_merchant", "c_player", "kubunuh kau");
        Assert.True(result.Accepted);
        Assert.True(threatReplies.Contains(result.ReplyText),
            $"exchange reply must come from the threat pool, got: '{result.ReplyText}'");
    }

    [Fact]
    public void HandleThreat_is_safe_on_self_threat()
    {
        var s = TestSupport.NewPopulatedSession(7);
        // Must no-op, never throw.
        s.Threat.HandleThreat("c_player", "c_player", ThreatLevel.DeathThreat, "loc_residential", 1);
        s.Threat.HandleThreat("c_player", "c_merchant", ThreatLevel.None, "loc_residential", 1);
        Assert.False(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.DialogueThreat));
    }
}
