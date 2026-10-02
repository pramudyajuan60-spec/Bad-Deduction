namespace BadDeduction.Cognition;

/// <summary>
/// All Phase 4 tuning constants and pure formulas in one place. Everything is integer math
/// (0-100 scales) so results are identical on every platform (see ADR-002/ADR-014 conventions).
/// Move these to data/ when balancing starts.
/// </summary>
public static class CognitionRules
{
    /// <summary>Confidence at or above this counts as a belief.</summary>
    public const int BeliefThreshold = 60;

    /// <summary>Confidence at or above this (but below belief) counts as a suspicion; below it is dismissed.</summary>
    public const int SuspectThreshold = 40;

    /// <summary>Memories below this confidence are foggy (hard to recall).</summary>
    public const int RecallFloor = 20;

    /// <summary>The first time confidence sinks below this, the memory may distort (one RNG roll, once).</summary>
    public const int DistortionThreshold = 40;

    /// <summary>Confidence never decays or distorts below this: a trace always remains.</summary>
    public const int MinConfidence = 5;

    public const int DailyDecayWitnessed = 2;
    public const int DailyDecayHeard = 4; // Told and Inferred fade faster than first-hand memory.

    /// <summary>Maximum confidence lost in a single rumor hop (telephone game). The actual loss is 0..15.</summary>
    public const int RumorMaxConfidenceLoss = 15;

    /// <summary>Chance a rumor hop garbles the retelling (prepends "[retold]").</summary>
    public const double RumorGarbleChance = 0.5;

    /// <summary>Chance a memory distorts when it first sinks below the distortion threshold.</summary>
    public const double DistortionChance = 0.5;

    public static int InitialConfidence(MemorySource source) => source switch
    {
        MemorySource.Witnessed => 90,
        MemorySource.Told => 70,
        MemorySource.Inferred => 55,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static int DailyDecay(MemorySource source) => source switch
    {
        MemorySource.Witnessed => DailyDecayWitnessed,
        MemorySource.Told => DailyDecayHeard,
        MemorySource.Inferred => DailyDecayHeard,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>
    /// Belief confidence from integer evidence counts: neutral 50, moved 8 points per net piece of
    /// evidence, clamped to [5, 95] so no belief is ever absolute or impossible. One supporting item
    /// (58) is a suspicion; two (66) make a belief; two refuting items (34) dismiss it.
    /// </summary>
    public static int BeliefConfidence(int evidenceFor, int evidenceAgainst) =>
        Clamp(50 + 8 * (evidenceFor - evidenceAgainst), MinConfidence, 95);

    public static BeliefBand BandOf(int confidence) =>
        confidence >= BeliefThreshold ? BeliefBand.Believes :
        confidence >= SuspectThreshold ? BeliefBand.Suspect :
        BeliefBand.Dismissed;

    public static int Clamp(int value, int min = 0, int max = 100) => Math.Clamp(value, min, max);
}
