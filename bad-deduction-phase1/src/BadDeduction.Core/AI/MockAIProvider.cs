namespace BadDeduction.AI;

/// <summary>
/// Deterministic scripted provider: the CI baseline (risk 1). Given the same seed and the
/// same request it always returns the same response — a pure function of (seed, speaker,
/// listener, conversation, utterance) via <see cref="StableHash"/>, so it never touches an
/// RNG stream and mock-driven runs are replay-identical. Deltas stay small (±4) and every
/// template is written to pass the validator (no role words, only known entities).
/// </summary>
public sealed class MockAIProvider : IAIProvider
{
    private static readonly string[] ReplyTemplates =
    {
        "Interesting. Tell me more, {L}.",
        "I've been turning that over in my mind too, {L}.",
        "You ask a lot of questions, {L}. Why do you want to know?",
        "Hmm. I hadn't thought of it that way.",
        "That matches what I heard as well.",
        "I see. And what do you make of that, {L}?",
        "Careful who you repeat that to, {L}.",
        "I don't know much about that, to be honest.",
    };

    private static readonly string[] FactTemplates =
    {
        "{L} came asking questions.",
        "I told {L} what I remembered.",
        "{L} seems worried about something.",
        "We talked for a while, {L} and I.",
    };

    /// <summary>
    /// Phase 13: when the utterance is a threat, the mock answers from a dedicated template
    /// pool instead of the generic one — a threatened NPC reacts to the threat rather than
    /// changing the subject. Still fully deterministic (same hash), validator-safe (no role
    /// words, only the known listener name), with a fixed trust −3 / suspicion +4 and
    /// zero or one fact.
    /// </summary>
    private static readonly string[] ThreatReplyTemplates =
    {
        "W-wait. Take that back — I don't want trouble.",
        "Threaten me again and I'll scream for the guard!",
        "You don't frighten me. Say it again and we'll see.",
        "Please. There's no need for threats — let's talk.",
    };

    private static readonly string[] ThreatFactTemplates =
    {
        "{L} threatened me.",
    };

    /// <summary>
    /// Phase 14: when the orchestrator reports a compliance verdict
    /// (<see cref="AIRequest.OrderDirective"/>), the mock voices it from dedicated
    /// template pools instead of the generic one — still a pure function of the hash,
    /// validator-safe, with small fixed deltas.
    /// </summary>
    private static readonly string[] OrderAcceptTemplates =
    {
        "Alright, {L}. I'll do it.",
        "Fine. Consider it done, {L}.",
        "Hm... alright, {L}. For you, I'll do it.",
    };

    private static readonly string[] OrderRefuseTemplates =
    {
        "No. I won't do that, {L}.",
        "Are you out of your mind, {L}? Absolutely not.",
        "I can't do that. Don't ask me again, {L}.",
    };

    /// <summary>
    /// Phase 14: utterances carrying these words press on the speaker's despair.
    /// English + Indonesian. The mock proposes a small deterministic despair delta
    /// (0-3) so the suicide meter moves even without an LLM.
    /// </summary>
    private static readonly string[] DespairWords =
    {
        "hopeless", "worthless", "no point", "give up", "end it",
        "putus asa", "tak berguna", "tidak berguna", "menyerah", "bunuh diri",
    };

    private readonly ulong _seed;

    public MockAIProvider(ulong seed) => _seed = seed;

    public AIResponse Complete(AIRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var h = StableHash.Compute(_seed,
            request.SpeakerId, request.ListenerId, request.ConversationId, request.Utterance);

        if (ThreatDetector.Detect(request.Utterance) != ThreatLevel.None)
            return ThreatResponse(request, h);

        if (request.OrderDirective is not null)
            return OrderResponse(request, h);

        var reply = ReplyTemplates[h % (ulong)ReplyTemplates.Length]
            .Replace("{S}", request.SpeakerName, StringComparison.Ordinal)
            .Replace("{L}", request.ListenerName, StringComparison.Ordinal);

        var trust = (int)(h % 9) - 4;                 // -4..+4
        var suspicion = (int)((h >> 16) % 9) - 4;     // -4..+4
        var factCount = (int)((h >> 32) % 3);         // 0..2 facts

        var facts = new List<string>();
        for (var i = 0; i < factCount; i++)
        {
            var template = FactTemplates[(h >> (40 + i * 8)) % (ulong)FactTemplates.Length];
            facts.Add(template
                .Replace("{S}", request.SpeakerName, StringComparison.Ordinal)
                .Replace("{L}", request.ListenerName, StringComparison.Ordinal));
        }

        return AIResponse.Accept(new DialogueOutput
        {
            ReplyText = reply,
            TrustDelta = trust,
            SuspicionDelta = suspicion,
            DespairDelta = DespairFor(request.Utterance, h),
            NewFacts = facts,
            NewMemorySummary = $"talked with {request.ListenerName}.",
        });
    }

    private static AIResponse OrderResponse(AIRequest request, ulong h)
    {
        var accepted = request.OrderDirective!.StartsWith("accept:", StringComparison.Ordinal);
        var pool = accepted ? OrderAcceptTemplates : OrderRefuseTemplates;
        var reply = pool[h % (ulong)pool.Length]
            .Replace("{L}", request.ListenerName, StringComparison.Ordinal);
        return AIResponse.Accept(new DialogueOutput
        {
            ReplyText = reply,
            TrustDelta = accepted ? 2 : -1,
            SuspicionDelta = accepted ? 0 : 2,
            DespairDelta = DespairFor(request.Utterance, h),
            NewFacts = new List<string>(),
            NewMemorySummary = $"{request.ListenerName} asked me to do something; I " +
                (accepted ? "agreed." : "refused."),
        });
    }

    /// <summary>Deterministic despair proposal: 0-3 when the utterance presses on despair, else 0.</summary>
    private static int DespairFor(string utterance, ulong h)
    {
        var lower = utterance.ToLowerInvariant();
        foreach (var word in DespairWords)
            if (lower.Contains(word, StringComparison.Ordinal))
                return (int)(h % 4);
        return 0;
    }

    private static AIResponse ThreatResponse(AIRequest request, ulong h)
    {
        var reply = ThreatReplyTemplates[h % (ulong)ThreatReplyTemplates.Length]
            .Replace("{L}", request.ListenerName, StringComparison.Ordinal);

        var factCount = (int)((h >> 32) % 2); // 0..1 facts
        var facts = new List<string>();
        for (var i = 0; i < factCount; i++)
            facts.Add(ThreatFactTemplates[(h >> 40) % (ulong)ThreatFactTemplates.Length]
                .Replace("{L}", request.ListenerName, StringComparison.Ordinal));

        return AIResponse.Accept(new DialogueOutput
        {
            ReplyText = reply,
            TrustDelta = -3,
            SuspicionDelta = 4,
            NewFacts = facts,
            NewMemorySummary = $"{request.ListenerName} threatened me.",
        });
    }
}
