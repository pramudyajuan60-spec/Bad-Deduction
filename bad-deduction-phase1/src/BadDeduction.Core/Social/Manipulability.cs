using BadDeduction.AI;

namespace BadDeduction.Social;

/// <summary>
/// Phase 14: how easily a character is swayed by others. The user asked for explicit
/// tiers — some NPCs are easy to manipulate, some standard, some hard — and for
/// responses to scale with the tier.
/// <para/>
/// The tier is a PURE function of (runSeed, characterId) via <see cref="StableHash"/>
/// (same pattern as <c>MockAIProvider</c>'s hash determinism): no RNG stream is
/// consumed, no state is stored, the tier never shifts between sessions, and adding
/// the feature changes no existing draw sequence (ADR-002 spirit).
/// </summary>
public enum ManipulabilityTier
{
    Gullible,
    Standard,
    Wary,
}

/// <summary>Phase 14 tuning constants for manipulability. Integers only (ADR-002/ADR-014).</summary>
public static class ManipulabilityRules
{
    /// <summary>Seeded draw weights (percent): 25/50/25.</summary>
    public const int GullibleWeight = 25;
    public const int StandardWeight = 50;
    // WaryWeight is the remainder (25).

    /// <summary>
    /// Extra compliance factor for <see cref="ComplianceEvaluator.Evaluate"/>: the
    /// gullible are easier to sway, the wary resist. Bounded so the ADR-014 guarantee
    /// (no trust value forces a severe order) keeps holding.
    /// </summary>
    public static int ComplianceBonus(ManipulabilityTier tier) => tier switch
    {
        ManipulabilityTier.Gullible => 15,
        ManipulabilityTier.Standard => 0,
        ManipulabilityTier.Wary => -20,
        _ => 0,
    };

    /// <summary>
    /// Trust shifts from dialogue scale with tier: the gullible feel them 1.5x, the
    /// wary half. Integer math; truncates toward zero.
    /// </summary>
    public static int ScaleTrustShift(ManipulabilityTier tier, int delta) => tier switch
    {
        ManipulabilityTier.Gullible => delta * 3 / 2,
        ManipulabilityTier.Wary => delta / 2,
        _ => delta,
    };

    /// <summary>Despair accumulates double on the gullible, half on the wary.</summary>
    public static int ScaleDespair(ManipulabilityTier tier, int delta) => tier switch
    {
        ManipulabilityTier.Gullible => delta * 2,
        ManipulabilityTier.Wary => delta / 2,
        _ => delta,
    };

    /// <summary>Deterministic tier draw: StableHash(runSeed, "tier", characterId) % 100.</summary>
    public static ManipulabilityTier TierFor(ulong runSeed, string characterId)
    {
        var roll = (int)(StableHash.Compute(runSeed, "tier", characterId) % 100);
        if (roll < GullibleWeight) return ManipulabilityTier.Gullible;
        if (roll < GullibleWeight + StandardWeight) return ManipulabilityTier.Standard;
        return ManipulabilityTier.Wary;
    }
}
