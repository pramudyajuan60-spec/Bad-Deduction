using BadDeduction.Characters;

namespace BadDeduction.World;

/// <summary>Mutable per-run state of a location. Static facts (name, links) live in LocationDefinition.</summary>
public sealed class LocationState
{
    public string Id { get; set; } = "";

    /// <summary>E.g. a crime scene cordoned off by police. Access rules are applied in later phases.</summary>
    public bool IsSealed { get; set; }
}

public sealed class WorldState
{
    public Dictionary<string, LocationState> Locations { get; set; } = new();
    public Dictionary<string, CharacterState> Characters { get; set; } = new();
}
