namespace BadDeduction.Characters;

public enum HiddenRole { Malvr, Lumiel }

/// <summary>
/// WORLD TRUTH layer: what is actually true, independent of what anyone knows or believes.
/// Only characters holding a hidden role appear here. Which character holds which role is
/// decided per run from the RunSeed (Phase 10), so no NPC is "always Malvr".
/// </summary>
public sealed class WorldTruth
{
    public Dictionary<string, HiddenRole> HiddenRoles { get; set; } = new();

    public string? CharacterWithRole(HiddenRole role)
    {
        foreach (var (id, r) in HiddenRoles)
            if (r == role) return id;
        return null;
    }
}
