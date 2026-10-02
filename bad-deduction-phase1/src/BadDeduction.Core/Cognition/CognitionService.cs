using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.World;

namespace BadDeduction.Cognition;

/// <summary>
/// Phase 4: the only sanctioned way to give characters knowledge, memories and beliefs.
/// <para/>
/// Knowledge has exactly one gate — <see cref="Perceive"/> — so "an NPC cannot know what it never
/// received" holds by construction: nothing else may add to <c>KnownEvents</c>. Rumors spread only
/// between characters who have actually met (same location, or an existing relationship edge) and
/// degrade deterministically, like a game of telephone. Beliefs accumulate integer evidence and cross
/// named thresholds. Daily decay is silent, like social drift (ADR-013).
/// <para/>
/// Nothing here reads hidden roles (ADR-003/ADR-015): what a character knows flows only through
/// Perceive/TellRumor/Infer calls.
/// </summary>
public sealed class CognitionService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly RelationshipGraph _graph;
    private readonly DeterministicRandom _rng;

    public CognitionService(GameState state, EventSystem events, RelationshipGraph graph)
    {
        _state = state;
        _events = events;
        _graph = graph;
        _rng = new DeterministicRandom(state.Cognition.CognitionRng);
        _events.Subscribe<DayChanged>(_ => ApplyDailyDecay());
    }

    // ------------------------------------------------------------------ reads

    /// <summary>The "knows" layer: has this character actually received this world event?</summary>
    public bool Knows(string characterId, long eventId)
    {
        RequireCharacter(characterId);
        return _state.Cognition.KnownEvents.TryGetValue(characterId, out var known) && known.Contains(eventId);
    }

    public IReadOnlyList<MemoryEntry> GetMemories(string characterId)
    {
        RequireCharacter(characterId);
        return _state.Cognition.Memories.TryGetValue(characterId, out var list)
            ? list.AsReadOnly()
            : Array.Empty<MemoryEntry>();
    }

    public IReadOnlyList<Belief> GetBeliefs(string characterId)
    {
        RequireCharacter(characterId);
        return _state.Cognition.Beliefs.TryGetValue(characterId, out var list)
            ? list.AsReadOnly()
            : Array.Empty<Belief>();
    }

    public Belief? GetBelief(string characterId, string propositionId)
    {
        RequireProposition(propositionId);
        return GetBeliefs(characterId).FirstOrDefault(b => b.PropositionId == propositionId);
    }

    public BeliefBand BeliefBandOf(string characterId, string propositionId) =>
        GetBelief(characterId, propositionId) is { } b ? b.Band : BeliefBand.None;

    // ------------------------------------------------------------------ knowledge gate

    /// <summary>
    /// THE knowledge gate: a character directly experiences (or is told / infers) a world event.
    /// Grants knowledge, stores a memory, and journals it when the character is the player.
    /// Idempotent: perceiving an already-known event changes nothing and returns null.
    /// </summary>
    public WorldEvent? Perceive(string characterId, long eventId, MemorySource source, string? summary = null, long? causedBy = null)
    {
        RequireCharacter(characterId);
        var evt = RequireEvent(eventId);
        var cog = _state.Cognition;

        if (!cog.KnownEvents.TryGetValue(characterId, out var known))
            cog.KnownEvents[characterId] = known = new HashSet<long>();
        if (!known.Add(eventId)) return null; // already knows: nothing changes

        summary ??= DefaultSummary(evt);
        if (string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("A memory summary is required.", nameof(summary));

        var memory = new MemoryEntry
        {
            Id = cog.NextMemoryId++,
            CharacterId = characterId,
            EventId = eventId,
            Summary = summary,
            RecordedAt = _state.TotalMinutes,
            Confidence = CognitionRules.InitialConfidence(source),
            Source = source,
        };
        if (!cog.Memories.TryGetValue(characterId, out var list))
            cog.Memories[characterId] = list = new List<MemoryEntry>();
        list.Add(memory);

        if (_state.Player.CharacterId.Length > 0 && characterId == _state.Player.CharacterId)
            cog.PlayerJournal.Add(eventId);

        return _events.Record(WorldEventTypes.MemoryRecorded,
            locationId: evt.LocationId,
            participants: new[] { characterId },
            data: new Dictionary<string, string>
            {
                ["event"] = eventId.ToString(),
                ["source"] = source.ToString(),
                ["confidence"] = memory.Confidence.ToString(),
            },
            causedBy: causedBy);
    }

    /// <summary>
    /// One character tells another about an event they know. Requires real contact: the two must share
    /// a location or have a relationship edge (they have met before). Each hop deterministically degrades
    /// confidence (0-15 points) and may garble the retelling — the telephone game. Returns null when the
    /// listener already knows the event.
    /// </summary>
    public WorldEvent? TellRumor(string fromId, string toId, long eventId, long? causedBy = null)
    {
        RequireCharacter(fromId);
        RequireCharacter(toId);
        if (fromId == toId) throw new ArgumentException("A character cannot spread a rumor to itself.");
        var evt = RequireEvent(eventId);
        if (!Knows(fromId, eventId))
            throw new InvalidOperationException($"'{fromId}' cannot spread word of event {eventId}: they never received it.");
        if (Knows(toId, eventId)) return null; // already knows: nothing changes
        if (!InContact(fromId, toId))
            throw new InvalidOperationException($"'{fromId}' and '{toId}' have no contact: different locations and no relationship.");

        var sourceMemory = GetMemories(fromId).LastOrDefault(m => m.EventId == eventId);
        var baseConfidence = sourceMemory?.Confidence ?? CognitionRules.InitialConfidence(MemorySource.Told);
        var baseSummary = sourceMemory?.Summary ?? DefaultSummary(evt);

        var loss = _rng.NextInt(0, CognitionRules.RumorMaxConfidenceLoss + 1);
        var confidence = Math.Max(CognitionRules.MinConfidence, baseConfidence - loss);
        var garbled = _rng.Chance(CognitionRules.RumorGarbleChance);
        var summary = garbled ? "[retold] " + baseSummary : baseSummary;

        var cog = _state.Cognition;
        if (!cog.KnownEvents.TryGetValue(toId, out var known))
            cog.KnownEvents[toId] = known = new HashSet<long>();
        known.Add(eventId);
        var memory = new MemoryEntry
        {
            Id = cog.NextMemoryId++,
            CharacterId = toId,
            EventId = eventId,
            Summary = summary,
            RecordedAt = _state.TotalMinutes,
            Confidence = confidence,
            Source = MemorySource.Told,
        };
        if (!cog.Memories.TryGetValue(toId, out var list))
            cog.Memories[toId] = list = new List<MemoryEntry>();
        list.Add(memory);

        if (_state.Player.CharacterId.Length > 0 && toId == _state.Player.CharacterId)
            cog.PlayerJournal.Add(eventId);

        return _events.Record(WorldEventTypes.RumorSpread,
            participants: new[] { fromId, toId },
            data: new Dictionary<string, string>
            {
                ["event"] = eventId.ToString(),
                ["confidence"] = confidence.ToString(),
                ["garbled"] = garbled ? "true" : "false",
            },
            causedBy: causedBy);
    }

    /// <summary>
    /// Adds one piece of evidence for or against a proposition. Confidence is recomputed from the
    /// integer counts (see <see cref="CognitionRules.BeliefConfidence"/>); a band transition
    /// (dismissed/suspect/believes) is logged as a causally-linked event, quiet accumulation is not.
    /// Returns the logged event, or null when the band did not change.
    /// </summary>
    public WorldEvent? AddEvidence(string characterId, string propositionId, bool supports, long? causedBy = null)
    {
        RequireCharacter(characterId);
        RequireProposition(propositionId);

        var cog = _state.Cognition;
        if (!cog.Beliefs.TryGetValue(characterId, out var list))
            cog.Beliefs[characterId] = list = new List<Belief>();
        var belief = list.FirstOrDefault(b => b.PropositionId == propositionId);
        if (belief is null)
        {
            belief = new Belief { PropositionId = propositionId, UpdatedAt = _state.TotalMinutes };
            list.Add(belief);
        }

        var before = belief.Band;
        if (supports) belief.EvidenceFor++;
        else belief.EvidenceAgainst++;
        belief.Confidence = CognitionRules.BeliefConfidence(belief.EvidenceFor, belief.EvidenceAgainst);
        belief.UpdatedAt = _state.TotalMinutes;
        var after = belief.Band;
        if (after == before) return null;

        return _events.Record(WorldEventTypes.BeliefChanged,
            participants: new[] { characterId },
            data: new Dictionary<string, string>
            {
                ["proposition"] = propositionId,
                ["from"] = before.ToString(),
                ["to"] = after.ToString(),
                ["confidence"] = belief.Confidence.ToString(),
                ["for"] = belief.EvidenceFor.ToString(),
                ["against"] = belief.EvidenceAgainst.ToString(),
            },
            causedBy: causedBy);
    }

    /// <summary>
    /// Silent daily fading: memories lose confidence by source (witnessed 2/day, told/inferred 4/day),
    /// floored at 5. The first time a memory sinks below the distortion threshold it may distort once
    /// (one RNG roll; a failed roll never retries). Below the recall floor a memory is foggy but kept.
    /// </summary>
    public void ApplyDailyDecay()
    {
        foreach (var list in _state.Cognition.Memories.Values)
        {
            foreach (var memory in list)
            {
                var before = memory.Confidence;
                memory.Confidence = Math.Max(CognitionRules.MinConfidence, before - CognitionRules.DailyDecay(memory.Source));
                if (!memory.IsDistorted
                    && before >= CognitionRules.DistortionThreshold
                    && memory.Confidence < CognitionRules.DistortionThreshold
                    && _rng.Chance(CognitionRules.DistortionChance))
                {
                    memory.IsDistorted = true;
                    memory.Summary = "[hazy] " + memory.Summary;
                }
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Contact means "these two have actually met": same place right now, or a relationship edge.</summary>
    private bool InContact(string a, string b)
    {
        var chars = _state.World.Characters;
        if (chars[a].CurrentLocationId == chars[b].CurrentLocationId) return true;
        return _graph.Get(a, b) is not null || _graph.Get(b, a) is not null;
    }

    private static string DefaultSummary(WorldEvent evt) =>
        evt.LocationId is { } loc ? $"{evt.Type} at {loc}" : evt.Type;

    private void RequireCharacter(string id)
    {
        if (!_state.World.Characters.ContainsKey(id)) throw new ArgumentException($"Unknown character '{id}'.");
    }

    private WorldEvent RequireEvent(long eventId)
    {
        if (!_state.EventLog.TryGet(eventId, out var evt))
            throw new ArgumentException($"Unknown event {eventId}.", nameof(eventId));
        return evt;
    }

    private static void RequireProposition(string propositionId)
    {
        if (string.IsNullOrWhiteSpace(propositionId))
            throw new ArgumentException("A proposition id is required.", nameof(propositionId));
    }
}
