using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Initiative;
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
    /// <summary>True when the provider fell back internally (e.g. Ollama unreachable → mock). Drives the UI's "offline dialogue" indicator.</summary>
    public bool UsedFallback { get; set; }
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
    private readonly ThreatService? _threatService;
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
        ulong seed,
        ThreatService? threatService = null)
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
        _threatService = threatService;
    }

    /// <summary>
    /// One line of dialogue: the listener says <paramref name="utterance"/>, the speaker
    /// (NPC) replies. Returns what was said and whether a real provider output was applied.
    /// <para/>
    /// Phase 13: the utterance is classified by <see cref="ThreatDetector"/>; the level
    /// steers the prompt (THREAT section) and, when a <see cref="ThreatService"/> is wired,
    /// the threat is applied as a game event afterwards. The listener — the one who spoke —
    /// is the threatener; the speaker (NPC) is the target.
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
        var cleanUtterance = utterance.Length > AIRules.MaxUtteranceLength
            ? utterance.Substring(0, AIRules.MaxUtteranceLength)
            : utterance;
        var threat = ThreatDetector.Detect(cleanUtterance);

        var result = RunConversation(speakerId, listenerId, cleanUtterance, conversationId,
            causedBy, threat, extraData: null);

        if (threat != ThreatLevel.None && _threatService is not null)
            _threatService.HandleThreat(listenerId, speakerId, threat,
                _currentLocationOf(speakerId), result.ExchangeEventId);

        return result;
    }

    /// <summary>
    /// Phase 13: the NPC speaks FIRST, acting on an accepted <see cref="NpcInitiative"/>
    /// (see <see cref="Initiative.NpcInitiativeService"/>). The provider is asked for an
    /// opening line through the same provider → validator → apply pipeline as
    /// <see cref="Exchange"/>, and the exchange is recorded as <c>dialogue.exchanged</c>
    /// with <c>initiative=true</c> and the motive in its data.
    /// <para/>
    /// The threat pipeline is deliberately NOT run here: the motive already encodes the
    /// NPC's intent (e.g. ThreatenBack is roleplay directed by motive), and a
    /// <c>dialogue.threat</c> event describes a threat made by the player, not an NPC's
    /// scripted opening.
    /// </summary>
    public DialogueResult OpeningLine(string npcId, string playerId, NpcInitiative initiative)
    {
        if (initiative is null) throw new ArgumentNullException(nameof(initiative));
        if (npcId == playerId)
            throw new ArgumentException("A character cannot hold a dialogue with itself.");
        if (initiative.NpcId != npcId)
            throw new ArgumentException("Initiative does not belong to this NPC.", nameof(initiative));

        var conversationId = $"{npcId}>{playerId}:initiative";
        var stageDirection =
            $"[You decide to approach {_context.DisplayNameOf(playerId)} and speak first. " +
            $"Your motive: {MotiveLabel(initiative.Motive)}. {initiative.MotiveDetails} " +
            "Say your opening line now — one or two sentences, in character.]";
        var extraData = new Dictionary<string, string>
        {
            ["initiative"] = "true",
            ["motive"] = initiative.Motive.ToString(),
        };
        return RunConversation(npcId, playerId, stageDirection, conversationId,
            causedBy: null, ThreatLevel.None, extraData);
    }

    private static string MotiveLabel(NpcMotive motive) => motive switch
    {
        NpcMotive.Warn => "warn them about a danger you know of",
        NpcMotive.Plead => "plead with them for mercy",
        NpcMotive.ThreatenBack => "threaten them back for threatening you",
        NpcMotive.ShareRumor => "share a rumor you heard",
        NpcMotive.Confront => "confront them about your suspicions",
        _ => "speak with them",
    };

    /// <summary>
    /// The shared provider → validator → apply pipeline behind <see cref="Exchange"/> and
    /// <see cref="OpeningLine"/>. Every exchange — accepted or fallback — is stored as a
    /// world event, both participants perceive it through the Phase 4 gate, and accepted
    /// output moves trust/suspicion and adds heard facts as evidence.
    /// </summary>
    private DialogueResult RunConversation(
        string speakerId, string listenerId, string cleanUtterance, string conversationId,
        long? causedBy, ThreatLevel threat, IReadOnlyDictionary<string, string>? extraData)
    {
        _exchangesPerConversation.TryGetValue(conversationId, out var used);
        var budgetLeft = AIRules.MaxExchangesPerConversation - used;

        string replyText;
        DialogueOutput? applied = null;
        string? fallbackReason = null;
        var usedFallback = false;

        if (budgetLeft <= 0)
        {
            fallbackReason = "conversation budget exhausted";
            replyText = FallbackLine(conversationId, used);
        }
        else
        {
            var request = new AIRequest
            {
                SpeakerId = speakerId,
                SpeakerName = _context.DisplayNameOf(speakerId),
                ListenerId = listenerId,
                ListenerName = _context.DisplayNameOf(listenerId),
                ConversationId = conversationId,
                Utterance = cleanUtterance,
                ContextPrompt = _context.BuildPrompt(speakerId, listenerId, cleanUtterance, _difficulty, _clock(), threat),
                Difficulty = _difficulty,
                BudgetLeft = budgetLeft,
            };

            var response = _provider.Complete(request);
            usedFallback = !response.Refused && response.UsedFallback;
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
        var data = new Dictionary<string, string>
        {
            ["utterance"] = cleanUtterance,
            ["reply"] = replyText,
            ["fallback"] = fallbackReason is null ? "false" : "true",
        };
        if (extraData is not null)
            foreach (var kv in extraData)
                data[kv.Key] = kv.Value;

        var exchange = _events.Record(WorldEventTypes.DialogueExchanged,
            locationId: _currentLocationOf(speakerId),
            participants: new[] { speakerId, listenerId },
            data: data,
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
            UsedFallback = usedFallback,
        };
    }

    private string FallbackLine(string conversationId, int index)
    {
        var h = StableHash.Compute(_seed, conversationId, index.ToString());
        return FallbackLines[h % (ulong)FallbackLines.Length];
    }
}
