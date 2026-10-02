using BadDeduction.Characters;
using BadDeduction.Social;

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

    // Phase 2. Keyed by character id; optional per character (hand-built test worlds have none).
    public Dictionary<string, CharacterProfile> Profiles { get; set; } = new();
    public Dictionary<string, Schedule> Schedules { get; set; } = new();
    public List<RelationshipEdge> Relationships { get; set; } = new();

    // Phase 5. Trips in progress, keyed by character id. Persisted so a save mid-travel
    // continues the trip identically after load (see WorldSimulation).
    public Dictionary<string, TravelState> ActiveTravels { get; set; } = new();
}
