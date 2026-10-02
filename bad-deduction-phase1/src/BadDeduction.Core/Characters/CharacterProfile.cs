using BadDeduction.Content;

namespace BadDeduction.Characters;

/// <summary>Six integer traits, 0-100. Integers keep the simulation deterministic across platforms.</summary>
public sealed class Personality
{
    public int Extraversion { get; set; }
    public int Agreeableness { get; set; }
    public int Conscientiousness { get; set; }
    public int Neuroticism { get; set; }
    public int Honesty { get; set; }
    public int Courage { get; set; }

    public IEnumerable<int> All()
    {
        yield return Extraversion; yield return Agreeableness; yield return Conscientiousness;
        yield return Neuroticism; yield return Honesty; yield return Courage;
    }
}

public sealed class Goal
{
    public string DefinitionId { get; set; } = "";
    public GoalSlot Slot { get; set; }
    /// <summary>Character the goal is aimed at (e.g. the family member to protect), if any.</summary>
    public string? TargetId { get; set; }
    /// <summary>0-100. Goals compete on priority; resolving conflicts is Phase 5/6.</summary>
    public int Priority { get; set; }
}

/// <summary>
/// Private, per-character data that must never reach gameplay UI directly (see PlayerView):
/// personality, goals and secrets. Memory, beliefs and emotion join in Phase 4.
/// </summary>
public sealed class CharacterProfile
{
    public Personality Personality { get; set; } = new();
    public List<Goal> Goals { get; set; } = new();
    public List<string> SecretIds { get; set; } = new();
    /// <summary>Characters sharing a household share a home and a surname.</summary>
    public string HouseholdId { get; set; } = "";
}
