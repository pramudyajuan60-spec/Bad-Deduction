using BadDeduction.Core;

namespace BadDeduction.Cognition;

/// <summary>How a memory was acquired. Determines its starting confidence and daily decay rate.</summary>
public enum MemorySource
{
    /// <summary>Seen first-hand. Starts sure, fades slowly.</summary>
    Witnessed,
    /// <summary>Heard from someone else (conversation, rumor). Starts weaker, fades faster.</summary>
    Told,
    /// <summary>Worked out by the character itself. Starts weakest, fades faster.</summary>
    Inferred,
}

/// <summary>What a confidence value means. Thresholds live in <see cref="CognitionRules"/>.</summary>
public enum BeliefBand
{
    /// <summary>No belief record exists for this proposition.</summary>
    None,
    /// <summary>Confidence below the suspicion threshold: leaning against.</summary>
    Dismissed,
    /// <summary>Confidence between suspicion and belief thresholds: a hunch, not a conviction.</summary>
    Suspect,
    /// <summary>Confidence at or above the belief threshold.</summary>
    Believes,
}

/// <summary>
/// One thing a character remembers. Plain data; the <see cref="CognitionService"/> is the only
/// sanctioned writer. A memory is never deleted — it fades (confidence decays) and may distort,
/// which is what lets old testimony contradict fresh evidence in Phase 7+.
/// </summary>
public sealed class MemoryEntry
{
    public long Id { get; set; }
    public string CharacterId { get; set; } = "";

    /// <summary>The world event this memory is about, if any (always set for Perceive/TellRumor).</summary>
    public long? EventId { get; set; }

    public string Summary { get; set; } = "";
    public long RecordedAt { get; set; }

    /// <summary>How crisp the memory is, 0-100. Decays daily; never below <see cref="CognitionRules.MinConfidence"/>.</summary>
    public int Confidence { get; set; }

    public MemorySource Source { get; set; }

    /// <summary>Set at most once, when confidence first sinks below the distortion threshold.</summary>
    public bool IsDistorted { get; set; }

    /// <summary>Derived, never stored: below the recall floor the memory is hard to bring to mind.</summary>
    public bool IsFoggy => Confidence < CognitionRules.RecallFloor;
}

/// <summary>
/// What a character thinks about one proposition (e.g. "the fire was arson"). Confidence is a pure,
/// documented function of the integer evidence counts (<see cref="CognitionRules.BeliefConfidence"/>),
/// so a belief is always explainable as "N for, M against".
/// </summary>
public sealed class Belief
{
    public string PropositionId { get; set; } = "";
    public int Confidence { get; set; } = 50;
    public int EvidenceFor { get; set; }
    public int EvidenceAgainst { get; set; }
    public long UpdatedAt { get; set; }

    public BeliefBand Band => CognitionRules.BandOf(Confidence);
}

/// <summary>
/// Everything characters know, remember and believe. Hung off <see cref="GameState"/>; plain data only.
/// The five knowledge layers (audit §B) map onto this as: truth lives in <see cref="WorldTruth"/>,
/// "knows" is <see cref="KnownEvents"/>, "believes"/"suspects" are <see cref="Beliefs"/> bands,
/// and player knowledge is <see cref="PlayerJournal"/> (read through <see cref="PlayerView"/>).
/// </summary>
public sealed class CognitionState
{
    public long NextMemoryId { get; set; } = 1;

    /// <summary>
    /// Dedicated persisted RNG stream ("cognition.memory", derived from the run seed): rumor
    /// distortion and memory-distortion draws never shift the main simulation stream and survive
    /// save/load, so a reloaded run continues the exact same random sequence.
    /// </summary>
    public RngState CognitionRng { get; set; } = new();

    public Dictionary<string, List<MemoryEntry>> Memories { get; set; } = new();
    public Dictionary<string, List<Belief>> Beliefs { get; set; } = new();

    /// <summary>
    /// The "knows" layer: which world-event ids each character has actually received.
    /// The ONLY writer is <see cref="CognitionService.Perceive"/> (and rumor, which routes through
    /// the same gate), so "an NPC cannot know what it never received" holds by construction.
    /// </summary>
    public Dictionary<string, HashSet<long>> KnownEvents { get; set; } = new();

    /// <summary>Ids of world events the current player character has perceived, oldest first.</summary>
    public List<long> PlayerJournal { get; set; } = new();
}
