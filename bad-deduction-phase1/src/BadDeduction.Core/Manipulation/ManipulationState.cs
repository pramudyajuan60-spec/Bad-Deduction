using BadDeduction.Core;
using BadDeduction.Social;

namespace BadDeduction.Manipulation;

/// <summary>
/// A lure in progress: an NPC ordered (and complying) to be somewhere until a time.
/// The world simulation honors it through the <c>CommandedDestinationFor</c> delegate;
/// expiry returns the character to their routine. Plain data, persisted.
/// </summary>
public sealed class CommandedDestination
{
    public string LocationId { get; set; } = "";
    public long UntilMinute { get; set; }
    public string OrderedBy { get; set; } = "";
    public string OrderLabel { get; set; } = "";
}

/// <summary>One learned schedule entry: the player's knowledge of an NPC's routine.</summary>
public sealed class ObservedBlock
{
    /// <summary>Minute of day the block starts (arrival deadline, ADR-008).</summary>
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public string LocationId { get; set; } = "";
    /// <summary>How it was learned: "seen" (co-located) or "surveillance".</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// Phase 14 state: despair meters, active lures, and player-learned routines.
/// Plain data on <see cref="GameState"/>, mutated only by
/// <see cref="ManipulationService"/>. Manipulability tiers are NOT stored here —
/// they are a pure function of (runSeed, characterId) via
/// <see cref="ManipulabilityRules.TierFor"/>.
/// </summary>
public sealed class ManipulationState
{
    /// <summary>Per-character despair 0-100 (suicide at 100). Unlisted = 0.</summary>
    public Dictionary<string, int> Despair { get; set; } = new();
    /// <summary>Active lure orders, keyed by NPC id.</summary>
    public Dictionary<string, CommandedDestination> CommandedDestinations { get; set; } = new();
    /// <summary>Player-learned routine entries, keyed by NPC id. The true schedule is never exposed.</summary>
    public Dictionary<string, List<ObservedBlock>> ObservedSchedules { get; set; } = new();
}
