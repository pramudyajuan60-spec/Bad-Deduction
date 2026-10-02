using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Social;

namespace BadDeduction.AI;

/// <summary>The orchestrator's verdict for one exchange.</summary>
public sealed class DialogueResult
{
    /// <summary>True when a provider output passed validation and was applied.</summary>
    public bool Accepted { get; set; }
    public string ReplyText { get; set; } = "";
    public long ExchangeEventId { get; set; }
    /// <summary>Null when accepted; otherwise why the deterministic fallback was used.</summary>
    public string? FallbackReason { get; set; }
    /// <summary>Deltas carried by the accepted output (validator-clamped; the social layer may clamp further at axis bounds).</summary>
    public int TrustDelta { get; set; }
    public int SuspicionDelta { get; set; }
}

/// <summary>
/// Phase 6: Orchestrator → ContextEngine → IAIProvider → Validator → engine (audit §F).
/// <para/>
/// One <see cref="Exchange"/> builds the speaker's context, asks the provider, validates the
/// proposal, and — only on acceptance — applies it through the sanctioned services:
/// trust/suspicion via <c>SocialService.Adjust</c>, knowledge via the Phase 4 gate
/// (<c>CognitionService.Perceive</c>), heard facts as evidence via <c>AddEvidence</c>.
/// The provider never touches state directly.
/// <para/>
/// Every exchange (accepted or fallback) is stored as a <c>dialogue.exchanged</c> world event,
/// so the sim never re-calls the provider on load (risk 1). Refusals (risk 5), validation
/// failures and budget exhaustion (risk 4) all funnel into the same deterministic fallback:
/// a canned line picked by <see cref="StableHash"/>, zero deltas, still logged — the game
/// never breaks immersion with an exception.
/// <para/>
/// The per-conversation budget is transient (kept on this instance, never saved): conversations
/// are UI-session scoped, and a fresh budget after load is the documented behavior.
/// </summary>
public sealed class DialogueOrchestrator
{
    private static readonly string[] FallbackLines =
    {
        "I... I'm not sure what to say to that.",
        "Let's talk about something else.",
        "Hmm. I need to think about that.",
        "You'd have to ask someone else.",
        "I don't have anything to add right now.",
    };

    private readonly ContextEngine _context;
    private readonly IAIProvider _provider;
    private readonly DialogueValidator _validator;
    private readonly CognitionService _cognition;
    private readonly SocialService _social;
    private readonly EventSystem _events;
    private readonly Func<string, string> _currentLocationOf;
    private readonly Func<GameTime> _clock;
    private readonly IReadOnlyList<string> _forbiddenPhrases;
    private readonly Difficulty _difficulty;
    private readonly ulong _seed;
    private readonly Dictionary<string, int> _exchangesPerConversation = new();

    public DialogueOrchestrator(
        ContextEngine context,
        IAIProvider provider,
        DialogueValidator validator,
        CognitionService cognition,
        SocialService social,
        EventSystem events,
        Func<string, string> currentLocationOf,
        Func<GameTime> clock,
        IReadOnlyList<string> forbiddenPhrases,
        Difficulty difficulty,
        ulong seed)
    {
        _context = context;
        _provider = provider;
        _validator = validator;
        _cognition = cognition;
        _social = social;
        _events = events;
        _currentLocationOf = currentLocationOf;
        _clock = clock;
        _forbiddenPhrases = forbiddenPhrases;
        _difficulty = difficulty;
        _seed = seed;
    }

    /// <summary>
    /// One line of dialogue: the listener says <paramref name="utterance"/>, the speaker
    /// (NPC) replies. Returns what was said and whether a real provider output was applied.
    /// </summary>
    public DialogueResult Exchange(
        string speakerId, string listenerId, string utterance,
        string? conversationId = null, long? causedBy = null)
    {
        if (speakerId == listenerId)
            throw new ArgumentException("A character cannot hold a dialogue with itself.");
        if (string.IsNullOrWhiteSpace(utterance))
            throw new ArgumentException("An utterance is required.", nameof(utterance));

        conversationId ??= $"{speakerId}>{listenerId}";
        _exchangesPerConversation.TryGetValue(conversationId, out var used);
        var budgetLeft = AIRules.MaxExchangesPerConversation - used;

        string replyText;
        DialogueOutput? applied = null;
        string? fallbackReason = null;

        if (budgetLeft <= 0)
        {
            fallbackReason = "conversation budget exhausted";
            replyText = FallbackLine(conversationId, used);
        }
        else
        {
            var cleanUtterance = utterance.Length > AIRules.MaxUtteranceLength
                ? utterance.Substring(0, AIRules.MaxUtteranceLength)
                : utterance;
            var request = new AIRequest
            {
                SpeakerId = speakerId,
                SpeakerName = _context.DisplayNameOf(speakerId),
                ListenerId = listenerId,
                ListenerName = _context.DisplayNameOf(listenerId),
                ConversationId = conversationId,
                Utterance = cleanUtterance,
                ContextPrompt = _context.BuildPrompt(speakerId, listenerId, cleanUtterance, _difficulty, _clock()),
                Difficulty = _difficulty,
                BudgetLeft = budgetLeft,
            };

            var response = _provider.Complete(request);
            if (response.Refused)
            {
                fallbackReason = $"provider refused: {response.RefusalReason ?? "no reason given"}";
                replyText = FallbackLine(conversationId, used);
            }
            else
            {
                var validation = _validator.Validate(
                    response.Output,
                    _context.KnownTexts(speakerId, listenerId, cleanUtterance),
                    _forbiddenPhrases);
                if (!validation.Accepted)
                {
                    fallbackReason = $"output rejected: {validation.RejectionReason}";
                    replyText = FallbackLine(conversationId, used);
                }
                else
                {
                    applied = validation.Output!;
                    replyText = applied.ReplyText;
                }
            }
        }

        // Stored as a world event either way: the sim never re-calls the provider on load.
        var exchange = _events.Record(WorldEventTypes.DialogueExchanged,
            locationId: _currentLocationOf(speakerId),
            participants: new[] { speakerId, listenerId },
            data: new Dictionary<string, string>
            {
                ["utterance"] = utterance.Length > AIRules.MaxUtteranceLength
                    ? utterance.Substring(0, AIRules.MaxUtteranceLength)
                    : utterance,
                ["reply"] = replyText,
                ["fallback"] = fallbackReason is null ? "false" : "true",
            },
            causedBy: causedBy);

        // Both participants perceive the exchange through the Phase 4 knowledge gate.
        var memorySummary = applied?.NewMemorySummary;
        _cognition.Perceive(speakerId, exchange.Id, MemorySource.Witnessed, memorySummary, causedBy: exchange.Id);
        _cognition.Perceive(listenerId, exchange.Id, MemorySource.Witnessed, memorySummary, causedBy: exchange.Id);

        if (applied is not null)
        {
            _social.Adjust(speakerId, listenerId,
                new SocialDelta(Trust: applied.TrustDelta, Suspicion: applied.SuspicionDelta),
                "dialogue", causedBy: exchange.Id);
            foreach (var fact in applied.NewFacts)
                _cognition.AddEvidence(listenerId, fact, supports: true, causedBy: exchange.Id);
        }

        _exchangesPerConversation[conversationId] = used + 1;

        return new DialogueResult
        {
            Accepted = applied is not null,
            ReplyText = replyText,
            ExchangeEventId = exchange.Id,
            FallbackReason = fallbackReason,
            TrustDelta = applied?.TrustDelta ?? 0,
            SuspicionDelta = applied?.SuspicionDelta ?? 0,
        };
    }

    private string FallbackLine(string conversationId, int index)
    {
        var h = StableHash.Compute(_seed, conversationId, index.ToString());
        return FallbackLines[h % (ulong)FallbackLines.Length];
    }
}
