using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Social;

namespace BadDeduction.Initiative;

/// <summary>Why an NPC wants to start a conversation with the player.</summary>
public enum NpcMotive
{
    Warn,
    Plead,
    ThreatenBack,
    ShareRumor,
    Confront,
}

/// <summary>
/// Phase 13: one NPC's pending decision to approach the player. Plain data, persisted on
/// <see cref="NpcInitiativeState"/>. <see cref="MotiveDetails"/> is speaker-known text only
/// (one of the NPC's own memory summaries, or a line built from their own relationship
/// values and the player's public name) — it is handed to the prompt builder, so it must
/// never carry hidden truth.
/// </summary>
public sealed class NpcInitiative
{
    public string NpcId { get; set; } = "";
    public NpcMotive Motive { get; set; }
    public long EnqueuedAt { get; set; }
    public string MotiveDetails { get; set; } = "";
}

/// <summary>
/// Phase 13: the NPC-initiative queue. Plain data on <see cref="GameState"/>, mutated only
/// by <see cref="NpcInitiativeService"/>. The RNG stream is dedicated and persisted, so
/// initiative draws never shift another system's draws (ADR-002).
/// </summary>
public sealed class NpcInitiativeState
{
    public List<NpcInitiative> Queue { get; set; } = new();
    public Dictionary<string, long> LastInitiativeAt { get; set; } = new();
    public RngState InitiativeRng { get; set; } = new();
}

/// <summary>
/// Phase 13: NPCs can decide to start conversations with the player. <see cref="Evaluate"/>
/// is UI-driven (the dialogue panel calls it when it needs to know who wants to talk); it
/// derives motives from real state — relationships, memories, beliefs — and rolls a seeded
/// draw per applicable motive on a dedicated persisted stream. Enqueueing records nothing in
/// the event log (a pending decision is UI-session consideration, not a world fact); the
/// resulting conversation is logged normally as <c>dialogue.exchanged</c> with initiative
/// data by <see cref="AI.DialogueOrchestrator.OpeningLine"/>.
/// </summary>
public sealed class NpcInitiativeService
{
    /// <summary>Minutes before the same NPC may initiate again.</summary>
    public const int CooldownMinutes = 1440;

    /// <summary>Maximum pending initiatives at any time.</summary>
    public const int MaxPending = 3;

    private static readonly IReadOnlyDictionary<NpcMotive, int> Weights = new Dictionary<NpcMotive, int>
    {
        [NpcMotive.ThreatenBack] = 70,
        [NpcMotive.Confront] = 50,
        [NpcMotive.Plead] = 45,
        [NpcMotive.Warn] = 40,
        [NpcMotive.ShareRumor] = 30,
    };

    private static readonly string[] ThreatWords = { "threat", "kill", "hurt", "bunuh", "hajar" };

    private readonly GameState _state;
    private readonly SocialService _social;
    private readonly CognitionService _cognition;
    private readonly PlayerView _view;
    private readonly DeterministicRandom _rng;

    /// <param name="events">
    /// Accepted for symmetry with the other services; initiative enqueue/dequeue is
    /// deliberately NOT logged (see class docs), so it is not stored.
    /// </param>
    public NpcInitiativeService(
        GameState state,
        EventSystem events,
        SocialService social,
        CognitionService cognition,
        PlayerView view)
    {
        _state = state;
        _social = social;
        _cognition = cognition;
        _view = view;
        _rng = new DeterministicRandom(state.Initiative.InitiativeRng);
    }

    /// <summary>NPCs currently waiting to talk to the player, in queue order.</summary>
    public IReadOnlyList<NpcInitiative> Pending => _state.Initiative.Queue.AsReadOnly();

    /// <summary>
    /// Recomputes who wants to talk to the player right now. For each living NPC sharing the
    /// player's location (excluding the player): skipped when already pending or when they
    /// initiated less than <see cref="CooldownMinutes"/> ago. Applicable motives are derived
    /// from real data; each gets one seeded draw (<c>NextInt(0,100) &lt; weight</c>); at most
    /// one motive per NPC is enqueued — the highest-priority passing one. The total pending
    /// is capped at <see cref="MaxPending"/>; beyond the cap the lowest-priority entries are
    /// dropped (priority = enum order, later = more severe). Deterministic: same state and
    /// same stream position always yield the same queue.
    /// </summary>
    public IReadOnlyList<NpcInitiative> Evaluate()
    {
        var st = _state.Initiative;
        var playerId = _state.Player.CharacterId;
        if (string.IsNullOrEmpty(playerId) || !_state.World.Characters.ContainsKey(playerId))
            return Pending;
        var playerLoc = _state.World.Characters[playerId].CurrentLocationId;
        var now = _state.TotalMinutes;

        var candidates = new List<NpcInitiative>();
        foreach (var npc in _state.World.Characters.Values
                     .Where(c => c.IsAlive && c.Id != playerId && c.CurrentLocationId == playerLoc)
                     .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (st.Queue.Any(q => q.NpcId == npc.Id)) continue;
            if (st.LastInitiativeAt.TryGetValue(npc.Id, out var last) && now - last < CooldownMinutes) continue;

            var pick = PickMotive(npc.Id, playerId);
            if (pick is null) continue;
            candidates.Add(new NpcInitiative
            {
                NpcId = npc.Id,
                Motive = pick.Value.Motive,
                EnqueuedAt = now,
                MotiveDetails = pick.Value.Details,
            });
        }

        var combined = st.Queue.Concat(candidates).ToList();
        while (combined.Count > MaxPending)
        {
            var drop = combined
                .OrderBy(i => (int)i.Motive)
                .ThenBy(i => i.EnqueuedAt)
                .ThenBy(i => i.NpcId, StringComparer.Ordinal)
                .First();
            combined.Remove(drop);
        }
        st.Queue.Clear();
        st.Queue.AddRange(combined);
        foreach (var added in candidates.Where(c => combined.Contains(c)))
            st.LastInitiativeAt[added.NpcId] = now;

        return Pending;
    }

    /// <summary>
    /// Accepts a pending initiative: removes it from the queue and hands it to the caller
    /// (which feeds it to <c>DialogueOrchestrator.OpeningLine</c>). False when no initiative
    /// is pending for that NPC.
    /// </summary>
    public bool TryAccept(string npcId, out NpcInitiative? initiative)
    {
        var queue = _state.Initiative.Queue;
        var index = queue.FindIndex(i => i.NpcId == npcId);
        if (index < 0)
        {
            initiative = null;
            return false;
        }
        initiative = queue[index];
        queue.RemoveAt(index);
        return true;
    }

    // ------------------------------------------------------------------ motives

    private (NpcMotive Motive, string Details)? PickMotive(string npcId, string playerId)
    {
        var rel = _social.View(npcId, playerId);
        var playerName = _view.PublicProfile(playerId)?.DisplayName ?? playerId;
        (NpcMotive Motive, string Details)? best = null;

        foreach (var motive in Enum.GetValues<NpcMotive>())
        {
            var details = MotiveDetails(npcId, playerId, playerName, rel, motive);
            if (details is null) continue; // not applicable
            if (_rng.NextInt(0, 100) >= Weights[motive]) continue; // draw failed
            if (best is null || motive > best.Value.Motive)
                best = (motive, details);
        }
        return best;
    }

    /// <summary>Returns speaker-known motive text when the motive applies, else null.</summary>
    private string? MotiveDetails(
        string npcId, string playerId, string playerName, RelationshipView rel, NpcMotive motive)
    {
        switch (motive)
        {
            case NpcMotive.ThreatenBack when rel.Fear >= 60:
            {
                var memory = LatestThreatMemory(npcId);
                return memory?.Summary ?? $"You remember {playerName} threatening you.";
            }
            case NpcMotive.Confront when rel.Suspicion >= 70:
                return $"You are deeply suspicious of {playerName} (suspicion {rel.Suspicion}/100).";
            case NpcMotive.Plead
                when _state.World.Characters[npcId].Kind == CharacterKind.Civilian
                     && rel.Trust < 30 && rel.Fear >= 40:
                return $"You are afraid of {playerName} (fear {rel.Fear}/100) and want their mercy.";
            case NpcMotive.Warn when rel.Affection >= 60:
            {
                var danger = _cognition.GetMemories(npcId)
                    .Where(m => m.Confidence >= 60)
                    .OrderByDescending(m => m.RecordedAt)
                    .FirstOrDefault();
                return danger?.Summary;
            }
            case NpcMotive.ShareRumor:
            {
                var rumor = _cognition.GetMemories(npcId)
                    .Where(m => m.Source == MemorySource.Told
                                && m.EventId.HasValue
                                && !_cognition.Knows(playerId, m.EventId.Value))
                    .OrderByDescending(m => m.RecordedAt)
                    .FirstOrDefault();
                return rumor?.Summary;
            }
            default:
                return null;
        }
    }

    private MemoryEntry? LatestThreatMemory(string npcId)
    {
        MemoryEntry? best = null;
        foreach (var memory in _cognition.GetMemories(npcId))
        {
            var summary = memory.Summary ?? "";
            if (!ThreatWords.Any(w => summary.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            if (best is null || memory.RecordedAt > best.RecordedAt) best = memory;
        }
        return best;
    }
}
