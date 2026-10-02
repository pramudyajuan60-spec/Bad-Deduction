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

    private readonly ulong _seed;

    public MockAIProvider(ulong seed) => _seed = seed;

    public AIResponse Complete(AIRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var h = StableHash.Compute(_seed,
            request.SpeakerId, request.ListenerId, request.ConversationId, request.Utterance);

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
            NewFacts = facts,
            NewMemorySummary = $"talked with {request.ListenerName}.",
        });
    }
}
