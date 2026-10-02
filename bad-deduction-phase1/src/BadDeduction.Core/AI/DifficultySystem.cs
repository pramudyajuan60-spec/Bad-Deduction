using BadDeduction.Core;

namespace BadDeduction.AI;

/// <summary>
/// Difficulty scales AI *cognition* (how much an NPC remembers and notices), never hidden
/// omniscience: a Genius NPC still only knows what it perceived (ADR-003/ADR-016).
/// </summary>
public static class DifficultySystem
{
    /// <summary>How many of the speaker's most recent memories enter the context prompt.</summary>
    public static int ContextMemoryCap(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 3,
        Difficulty.Medium => 5,
        Difficulty.Hard => 8,
        Difficulty.Genius => 12,
        _ => 5,
    };

    /// <summary>How many of the speaker's beliefs enter the context prompt.</summary>
    public static int ContextBeliefCap(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 2,
        Difficulty.Medium => 4,
        Difficulty.Hard => 6,
        Difficulty.Genius => 10,
        _ => 4,
    };

    /// <summary>
    /// Phase 8 hook: how eagerly an NPC spots contradictions in what it hears (0-100).
    /// Exposed now so difficulty has a single home; unused until Phase 8.
    /// </summary>
    public static int ContradictionSensitivity(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 25,
        Difficulty.Medium => 50,
        Difficulty.Hard => 75,
        Difficulty.Genius => 100,
        _ => 50,
    };
}
