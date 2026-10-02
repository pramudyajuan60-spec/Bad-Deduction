using BadDeduction.Core;

namespace BadDeduction.AI;

/// <summary>
/// Phase 6: the request handed to an AI provider for one line of NPC dialogue.
/// The provider sees ONLY this: who is speaking, to whom, what was said, and a pre-built
/// context prompt. It never sees GameState, WorldTruth, hidden roles, or any other
/// character's private data (risk 2).
/// </summary>
public sealed class AIRequest
{
    public string SpeakerId { get; set; } = "";
    public string SpeakerName { get; set; } = "";
    public string ListenerId { get; set; } = "";
    public string ListenerName { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Utterance { get; set; } = "";
    public string ContextPrompt { get; set; } = "";
    public Difficulty Difficulty { get; set; }
    public int BudgetLeft { get; set; }
}

/// <summary>
/// Structured dialogue output, per the dialogue contract (audit §B): every free-text
/// exchange yields deltas — trust, suspicion, new information, new memory, relationship.
/// All numeric fields are integers; the validator clamps them to the sanctioned range.
/// The provider PROPOSES; the validator and the orchestrator dispose (ADR-004 spirit).
/// </summary>
public sealed class DialogueOutput
{
    public string ReplyText { get; set; } = "";
    public int TrustDelta { get; set; }
    public int SuspicionDelta { get; set; }
    public List<string> NewFacts { get; set; } = new();
    public string? NewMemorySummary { get; set; }
    public string? RelationshipNote { get; set; }
}

/// <summary>
/// A provider result is either an accepted-shaped output or a refusal. Refusal is a normal
/// result (risk 5), never an exception: the orchestrator falls back deterministically.
/// <para/>
/// <see cref="UsedFallback"/> marks results that came from a provider's own internal
/// fallback (e.g. the Ollama provider delegating to the mock when the local model is
/// unreachable). Additive: the mock never sets it, so it defaults to false.
/// </summary>
public sealed class AIResponse
{
    public bool Refused { get; set; }
    public DialogueOutput? Output { get; set; }
    public string? RefusalReason { get; set; }
    /// <summary>True when this result was produced by the provider's fallback path.</summary>
    public bool UsedFallback { get; set; }

    public static AIResponse Accept(DialogueOutput output) =>
        new() { Output = output ?? throw new ArgumentNullException(nameof(output)) };

    public static AIResponse Refuse(string reason) =>
        new() { Refused = true, RefusalReason = reason };
}

/// <summary>
/// Synchronous provider abstraction. Deliberately synchronous: no real network exists in
/// this codebase, and async would add nothing but ceremony (ADR-027).
/// </summary>
public interface IAIProvider
{
    AIResponse Complete(AIRequest request);
}
