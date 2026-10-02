using System.Reflection;
using BadDeduction.AI;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

/// <summary>Phase 6: AI dialogue pipeline — context engine, provider abstraction, mock, validator.</summary>
public sealed class AIDialogueTests
{
    private static ContextEngine BuildEngine(GameSession s) =>
        new(s.View, s.Cognition, s.Social, s.Relationships, s.Content, s.State.World.Profiles,
            id => s.State.World.Characters[id].CurrentLocationId);

    private static DialogueOrchestrator BuildOrchestrator(
        GameSession s, IAIProvider provider, IReadOnlyList<string>? forbidden = null) =>
        new(BuildEngine(s), provider, new DialogueValidator(), s.Cognition, s.Social, s.Events,
            id => s.State.World.Characters[id].CurrentLocationId,
            () => new GameTime(s.State.TotalMinutes),
            forbidden ?? new List<string>(), Difficulty.Medium, 99);

    private sealed class ScriptedProvider : IAIProvider
    {
        private readonly Func<AIRequest, AIResponse> _fn;
        public ScriptedProvider(Func<AIRequest, AIResponse> fn) => _fn = fn;
        public AIResponse Complete(AIRequest request) => _fn(request);
    }

    private static AIResponse AcceptAll(AIRequest request) => AIResponse.Accept(new DialogueOutput
    {
        ReplyText = "Very interesting.",
        TrustDelta = 3,
        SuspicionDelta = -1,
        NewFacts = new List<string> { $"{request.ListenerName} asked a question" },
        NewMemorySummary = $"spoke with {request.ListenerName}",
    });

    /// <summary>Test-only derivation of role-linking phrases from truth. Production code never does this.</summary>
    private static List<string> ForbiddenFromTruth(GameSession s)
    {
        s.Debug.Enabled = true;
        try
        {
            var phrases = new List<string>();
            foreach (var (id, role) in s.Debug.GetTruth().HiddenRoles)
                phrases.Add($"{s.State.World.Characters[id].DisplayName} is {role}");
            return phrases;
        }
        finally
        {
            s.Debug.Enabled = false;
        }
    }

    private static long RecordIncident(GameSession s, string who = "c_merchant", string loc = "loc_residential") =>
        s.Events.Record("test.incident", loc, new[] { who }).Id;

    private static int CountOccurrences(string text, string substring)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += substring.Length;
        }
        return count;
    }

    // ------------------------------------------------------- truth-leak enforcement

    [Fact]
    public void Ai_namespace_never_touches_truth_types()
    {
        var forbidden = new[] { typeof(GameState), typeof(WorldTruth), typeof(DebugAccess) };
        var aiTypes = typeof(MockAIProvider).Assembly.GetTypes()
            .Where(t => t.Namespace == "BadDeduction.AI")
            .ToList();
        Assert.True(aiTypes.Count > 0, "expected AI types to exist");

        foreach (var type in aiTypes)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(flags))
                Assert.False(forbidden.Contains(field.FieldType),
                    $"{type.Name}.{field.Name} touches a truth type");
            foreach (var prop in type.GetProperties(flags))
                Assert.False(forbidden.Contains(prop.PropertyType),
                    $"{type.Name}.{prop.Name} touches a truth type");
            foreach (var ctor in type.GetConstructors())
                foreach (var param in ctor.GetParameters())
                    Assert.False(forbidden.Contains(param.ParameterType),
                        $"{type.Name} ctor param '{param.Name}' touches a truth type");
        }
    }

    [Fact]
    public void No_prompt_contains_role_words_or_holder_linkage()
    {
        var s = TestSupport.NewPopulatedSession(11);
        var engine = BuildEngine(s);
        var forbidden = ForbiddenFromTruth(s);
        Assert.True(forbidden.Count > 0, "test setup: roles must be assigned");

        foreach (var speaker in s.State.World.Characters.Keys)
        {
            foreach (var listener in s.State.World.Characters.Keys)
            {
                if (speaker == listener) continue;
                var prompt = engine.BuildPrompt(speaker, listener, "What did you see last night?",
                    Difficulty.Genius, new GameTime(s.State.TotalMinutes));
                Assert.False(prompt.Contains("malvr", StringComparison.OrdinalIgnoreCase),
                    $"prompt for {speaker} leaks a role word");
                Assert.False(prompt.Contains("lumiel", StringComparison.OrdinalIgnoreCase),
                    $"prompt for {speaker} leaks a role word");
                foreach (var phrase in forbidden)
                    Assert.False(prompt.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                        $"prompt for {speaker} links a holder to a role: '{phrase}'");
            }
        }
    }

    [Fact]
    public void Validator_rejects_output_naming_hidden_role_holder()
    {
        var s = TestSupport.NewPopulatedSession(11);
        var forbidden = ForbiddenFromTruth(s);
        var validator = new DialogueValidator();
        var known = new List<string> { "The Rival", "A Merchant", "tavern" };

        var naming = new DialogueOutput
        {
            ReplyText = "I have my suspicions.",
            NewFacts = new List<string> { forbidden[0] },
        };
        var result = validator.Validate(naming, known, forbidden);
        Assert.False(result.Accepted, "naming a hidden role holder must be rejected");
        Assert.True(result.RejectionReason is not null && result.RejectionReason.Contains("hidden role"),
            $"unexpected reason: {result.RejectionReason}");

        var selfClaim = new DialogueOutput { ReplyText = "I am Lumiel, trust me." };
        Assert.False(validator.Validate(selfClaim, known, forbidden).Accepted,
            "self-identifying role claim must be rejected");
    }

    // ------------------------------------------------------- context engine

    [Fact]
    public void Prompt_reflects_speaker_memories_beliefs_and_personality()
    {
        var s = TestSupport.NewPopulatedSession(12);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the chapel fire");
        s.Cognition.AddEvidence("c_merchant", "the rival started the fire", supports: true);
        s.Cognition.AddEvidence("c_merchant", "the rival started the fire", supports: true);

        var engine = BuildEngine(s);
        var prompt = engine.BuildPrompt("c_merchant", "c_rival", "Tell me everything.",
            Difficulty.Medium, new GameTime(s.State.TotalMinutes));

        Assert.Contains("saw the chapel fire", prompt);
        Assert.Contains("the rival started the fire", prompt);
        Assert.Contains("Believes", prompt);
        Assert.Contains("A Merchant", prompt);
        Assert.Contains("The Rival", prompt);
        Assert.Contains("Tell me everything.", prompt);
    }

    [Fact]
    public void Different_speakers_get_different_prompts()
    {
        var s = TestSupport.NewPopulatedSession(12);
        var eid = RecordIncident(s);
        s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, "saw the secret meeting");

        var engine = BuildEngine(s);
        var now = new GameTime(s.State.TotalMinutes);
        var merchantPrompt = engine.BuildPrompt("c_merchant", "c_rival", "Hello.", Difficulty.Medium, now);
        var rivalPrompt = engine.BuildPrompt("c_rival", "c_merchant", "Hello.", Difficulty.Medium, now);

        Assert.Contains("saw the secret meeting", merchantPrompt);
        Assert.False(rivalPrompt.Contains("saw the secret meeting"),
            "one NPC's private memory must never appear in another's prompt");
    }

    [Fact]
    public void Prompt_memory_cap_follows_difficulty()
    {
        var s = TestSupport.NewPopulatedSession(13);
        for (var i = 0; i < 12; i++)
        {
            var eid = RecordIncident(s);
            s.Cognition.Perceive("c_merchant", eid, MemorySource.Witnessed, $"memory-{i}");
        }

        var engine = BuildEngine(s);
        var now = new GameTime(s.State.TotalMinutes);
        var easy = engine.BuildPrompt("c_merchant", "c_rival", "Hi.", Difficulty.Easy, now);
        var genius = engine.BuildPrompt("c_merchant", "c_rival", "Hi.", Difficulty.Genius, now);

        Assert.Equal(3, CountOccurrences(easy, "memory-"));
        Assert.Equal(12, CountOccurrences(genius, "memory-"));
    }

    [Fact]
    public void Prompt_includes_relationship_toward_listener()
    {
        var s = TestSupport.NewPopulatedSession(14);
        s.Social.Adjust("c_merchant", "c_rival",
            new Social.SocialDelta(Trust: -15, Suspicion: 25), "test setup");

        var engine = BuildEngine(s);
        var prompt = engine.BuildPrompt("c_merchant", "c_rival", "Hi.", Difficulty.Medium,
            new GameTime(s.State.TotalMinutes));

        Assert.Contains("How you feel about The Rival", prompt);
        Assert.Contains("suspicion", prompt);
    }

    [Fact]
    public void ContextEngine_rejects_bad_input()
    {
        var s = TestSupport.NewPopulatedSession(14);
        var engine = BuildEngine(s);
        var now = new GameTime(s.State.TotalMinutes);
        Assert.Throws<ArgumentException>(() => engine.BuildPrompt("nobody", "c_rival", "Hi.", Difficulty.Medium, now));
        Assert.Throws<ArgumentException>(() => engine.BuildPrompt("c_merchant", "c_merchant", "Hi.", Difficulty.Medium, now));
        Assert.Throws<ArgumentException>(() => engine.BuildPrompt("c_merchant", "c_rival", "  ", Difficulty.Medium, now));
    }

    // ------------------------------------------------------- validator

    [Fact]
    public void Validator_accepts_good_output()
    {
        var validator = new DialogueValidator();
        var output = new DialogueOutput
        {
            ReplyText = "I saw nothing, I swear.",
            TrustDelta = 5,
            SuspicionDelta = -3,
            NewFacts = new List<string> { "the tavern was quiet" },
            NewMemorySummary = "talked with the rival",
        };
        var result = validator.Validate(output,
            new List<string> { "The Rival", "tavern", "talked with the rival" },
            new List<string> { "The Rival is Malvr" });

        Assert.True(result.Accepted, result.RejectionReason);
        Assert.Equal(5, result.Output!.TrustDelta);
        Assert.False(result.DeltasClamped);
    }

    [Fact]
    public void Validator_rejects_malformed_output()
    {
        var validator = new DialogueValidator();
        var known = new List<string> { "The Rival" };
        var none = new List<string>();

        Assert.False(validator.Validate(null, known, none).Accepted);
        Assert.False(validator.Validate(new DialogueOutput { ReplyText = "   " }, known, none).Accepted);
        Assert.False(validator.Validate(
            new DialogueOutput { ReplyText = new string('x', AIRules.MaxReplyLength + 1) }, known, none).Accepted);

        var tooMany = new DialogueOutput { ReplyText = "ok" };
        for (var i = 0; i < AIRules.MaxNewFacts + 1; i++) tooMany.NewFacts.Add($"fact {i}");
        Assert.False(validator.Validate(tooMany, known, none).Accepted);

        var tooLong = new DialogueOutput
            { ReplyText = "ok", NewFacts = new List<string> { new string('x', AIRules.MaxFactLength + 1) } };
        Assert.False(validator.Validate(tooLong, known, none).Accepted);
    }

    [Fact]
    public void Validator_rejects_unknown_entity_but_accepts_known_ones()
    {
        var validator = new DialogueValidator();
        var known = new List<string> { "The Rival", "tavern", "What did you see?" };
        var none = new List<string>();

        var unknown = new DialogueOutput
            { ReplyText = "ok", NewFacts = new List<string> { "Joren told me the vault code" } };
        var rejected = validator.Validate(unknown, known, none);
        Assert.False(rejected.Accepted);
        Assert.Contains("Joren", rejected.RejectionReason ?? "");

        var knownFact = new DialogueOutput
            { ReplyText = "ok", NewFacts = new List<string> { "The Rival asked about the tavern" } };
        Assert.True(validator.Validate(knownFact, known, none).Accepted);
    }

    [Fact]
    public void Validator_clamps_delta_overflow_instead_of_rejecting()
    {
        var validator = new DialogueValidator();
        var result = validator.Validate(
            new DialogueOutput { ReplyText = "Whoa.", TrustDelta = 100, SuspicionDelta = -100 },
            new List<string>(), new List<string>());

        Assert.True(result.Accepted);
        Assert.True(result.DeltasClamped);
        Assert.Equal(AIRules.MaxDeltaMagnitude, result.Output!.TrustDelta);
        Assert.Equal(-AIRules.MaxDeltaMagnitude, result.Output.SuspicionDelta);
    }

    // ------------------------------------------------------- mock provider

    [Fact]
    public void Mock_provider_is_deterministic()
    {
        var provider = new MockAIProvider(1234);
        var request = new AIRequest
        {
            SpeakerId = "c_merchant", SpeakerName = "A Merchant",
            ListenerId = "c_rival", ListenerName = "The Rival",
            ConversationId = "c1", Utterance = "What did you see?",
        };
        var first = provider.Complete(request);
        var second = provider.Complete(request);

        Assert.False(first.Refused);
        Assert.Equal(first.Output!.ReplyText, second.Output!.ReplyText);
        Assert.Equal(first.Output.TrustDelta, second.Output.TrustDelta);
        Assert.Equal(first.Output.SuspicionDelta, second.Output.SuspicionDelta);
        Assert.SequenceEqual(first.Output.NewFacts, second.Output.NewFacts);
    }

    [Fact]
    public void Mock_provider_varies_across_utterances()
    {
        var provider = new MockAIProvider(1234);
        var replies = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var response = provider.Complete(new AIRequest
            {
                SpeakerId = "c_merchant", SpeakerName = "A Merchant",
                ListenerId = "c_rival", ListenerName = "The Rival",
                ConversationId = "c1", Utterance = $"utterance number {i}",
            });
            replies.Add(response.Output!.ReplyText);
        }
        Assert.True(replies.Count > 1, "mock should vary its replies across utterances");
    }

    [Fact]
    public void Mock_output_passes_the_validator_in_a_real_session()
    {
        var s = TestSupport.NewPopulatedSession(15);
        var provider = new MockAIProvider(s.State.Meta.RunSeed);
        var engine = BuildEngine(s);
        var validator = new DialogueValidator();
        var utterance = "What did you see last night?";

        var request = new AIRequest
        {
            SpeakerId = "c_merchant", SpeakerName = "A Merchant",
            ListenerId = "c_rival", ListenerName = "The Rival",
            ConversationId = "c1", Utterance = utterance,
            ContextPrompt = engine.BuildPrompt("c_merchant", "c_rival", utterance, Difficulty.Medium,
                new GameTime(s.State.TotalMinutes)),
        };
        var response = provider.Complete(request);
        var result = validator.Validate(response.Output,
            engine.KnownTexts("c_merchant", "c_rival", utterance), ForbiddenFromTruth(s));
        Assert.True(result.Accepted, result.RejectionReason);
    }

    // ------------------------------------------------------- orchestrator

    [Fact]
    public void Session_dialogue_exchange_applies_deltas_and_logs()
    {
        var s = TestSupport.NewPopulatedSession(16);
        var before = s.Social.View("c_merchant", "c_rival").Trust;

        var result = s.Dialogue.Exchange("c_merchant", "c_rival", "What did you see last night?");

        Assert.True(result.Accepted, result.FallbackReason);
        Assert.False(string.IsNullOrWhiteSpace(result.ReplyText));
        Assert.Equal(before + result.TrustDelta, s.Social.View("c_merchant", "c_rival").Trust);

        var logged = s.State.EventLog.Events.FirstOrDefault(e => e.Id == result.ExchangeEventId);
        Assert.True(logged is not null);
        Assert.Equal("dialogue.exchanged", logged!.Type);
        Assert.True(logged.Participants.Contains("c_merchant") && logged.Participants.Contains("c_rival"));

        Assert.True(s.Cognition.Knows("c_merchant", result.ExchangeEventId), "speaker remembers");
        Assert.True(s.Cognition.Knows("c_rival", result.ExchangeEventId), "listener remembers");
    }

    [Fact]
    public void Exchange_turns_heard_facts_into_listener_beliefs()
    {
        var s = TestSupport.NewPopulatedSession(17);
        var orch = BuildOrchestrator(s, new ScriptedProvider(AcceptAll));

        var result = orch.Exchange("c_merchant", "c_rival", "Tell me.");
        Assert.True(result.Accepted, result.FallbackReason);

        var belief = s.Cognition.GetBelief("c_rival", "The Rival asked a question");
        Assert.True(belief is not null, "listener should hold the heard fact as a belief");
        Assert.Equal(1, belief!.EvidenceFor);
    }

    [Fact]
    public void Refusal_falls_back_with_zero_deltas_and_still_logs()
    {
        var s = TestSupport.NewPopulatedSession(18);
        var orch = BuildOrchestrator(s,
            new ScriptedProvider(_ => AIResponse.Refuse("policy")));
        var before = s.Social.View("c_merchant", "c_rival").Trust;

        var result = orch.Exchange("c_merchant", "c_rival", "Tell me everything.");

        Assert.False(result.Accepted);
        Assert.Contains("refused", result.FallbackReason ?? "");
        Assert.Equal(0, result.TrustDelta);
        Assert.Equal(0, result.SuspicionDelta);
        Assert.False(string.IsNullOrWhiteSpace(result.ReplyText), "fallback must still say something");
        Assert.Equal(before, s.Social.View("c_merchant", "c_rival").Trust);
        Assert.True(s.Cognition.Knows("c_rival", result.ExchangeEventId), "fallback is still logged and perceived");
    }

    [Fact]
    public void Rejected_output_falls_back()
    {
        var s = TestSupport.NewPopulatedSession(19);
        var orch = BuildOrchestrator(s,
            new ScriptedProvider(_ => AIResponse.Accept(new DialogueOutput
                { ReplyText = "I serve Malvr in secret." })),
            ForbiddenFromTruth(s));

        var result = orch.Exchange("c_merchant", "c_rival", "Who do you serve?");
        Assert.False(result.Accepted);
        Assert.Contains("rejected", result.FallbackReason ?? "");
    }

    [Fact]
    public void Budget_exhausts_after_ten_exchanges()
    {
        var s = TestSupport.NewPopulatedSession(20);
        var orch = BuildOrchestrator(s, new ScriptedProvider(AcceptAll));

        DialogueResult? last = null;
        for (var i = 0; i < AIRules.MaxExchangesPerConversation + 1; i++)
            last = orch.Exchange("c_merchant", "c_rival", $"Question {i}.");

        Assert.False(last!.Accepted);
        Assert.Contains("budget exhausted", last.FallbackReason ?? "");
    }

    [Fact]
    public void Exchange_validates_participants()
    {
        var s = TestSupport.NewPopulatedSession(21);
        Assert.Throws<ArgumentException>(() => s.Dialogue.Exchange("nobody", "c_rival", "Hi."));
        Assert.Throws<ArgumentException>(() => s.Dialogue.Exchange("c_merchant", "nobody", "Hi."));
        Assert.Throws<ArgumentException>(() => s.Dialogue.Exchange("c_merchant", "c_merchant", "Hi."));
        Assert.Throws<ArgumentException>(() => s.Dialogue.Exchange("c_merchant", "c_rival", "   "));
    }

    [Fact]
    public void Dialogue_is_save_load_stable()
    {
        var s = TestSupport.NewPopulatedSession(22);
        s.Dialogue.Exchange("c_merchant", "c_rival", "First question.");
        s.Dialogue.Exchange("c_rival", "c_merchant", "Second question.");
        var hashBefore = s.StateHash();

        var path = Path.Combine(Path.GetTempPath(), $"dialogue-test-{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var loaded = GameSession.Load(path, TestSupport.LoadContent());
            Assert.Equal(hashBefore, loaded.StateHash());

            var result = loaded.Dialogue.Exchange("c_merchant", "c_priest", "Third question.");
            Assert.True(result.Accepted, result.FallbackReason);
            Assert.True(loaded.Cognition.Knows("c_priest", result.ExchangeEventId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Fallback_is_deterministic_for_same_conversation()
    {
        var s1 = TestSupport.NewPopulatedSession(23);
        var s2 = TestSupport.NewPopulatedSession(23);
        var orch1 = BuildOrchestrator(s1, new ScriptedProvider(_ => AIResponse.Refuse("x")));
        var orch2 = BuildOrchestrator(s2, new ScriptedProvider(_ => AIResponse.Refuse("x")));

        var r1 = orch1.Exchange("c_merchant", "c_rival", "Hi.", conversationId: "conv-1");
        var r2 = orch2.Exchange("c_merchant", "c_rival", "Hi.", conversationId: "conv-1");
        Assert.Equal(r1.ReplyText, r2.ReplyText);
    }

    // ------------------------------------------------------- difficulty

    [Fact]
    public void Difficulty_params_scale_cognition_not_omniscience()
    {
        Assert.Equal(3, DifficultySystem.ContextMemoryCap(Difficulty.Easy));
        Assert.Equal(5, DifficultySystem.ContextMemoryCap(Difficulty.Medium));
        Assert.Equal(8, DifficultySystem.ContextMemoryCap(Difficulty.Hard));
        Assert.Equal(12, DifficultySystem.ContextMemoryCap(Difficulty.Genius));

        Assert.True(DifficultySystem.ContextBeliefCap(Difficulty.Genius) >
                    DifficultySystem.ContextBeliefCap(Difficulty.Easy));
        Assert.True(DifficultySystem.ContradictionSensitivity(Difficulty.Genius) >
                    DifficultySystem.ContradictionSensitivity(Difficulty.Easy));
    }

    [Fact]
    public void Session_dialogue_uses_session_difficulty()
    {
        var easy = GameSession.NewRun(24, Campaign.Lumiel, Difficulty.Easy, TestSupport.LoadContent());
        var genius = GameSession.NewRun(24, Campaign.Lumiel, Difficulty.Genius, TestSupport.LoadContent());
        Assert.Equal(Difficulty.Easy, easy.State.Meta.Difficulty);
        Assert.Equal(Difficulty.Genius, genius.State.Meta.Difficulty);
        Assert.True(easy.Dialogue is not null && genius.Dialogue is not null);
    }
}
