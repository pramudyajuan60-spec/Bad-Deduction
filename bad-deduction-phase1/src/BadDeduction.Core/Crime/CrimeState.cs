using System.Text.Json.Serialization;
using BadDeduction.Core;

namespace BadDeduction.Crime;

/// <summary>What an evidence item really is, independent of what anyone believes about it.</summary>
public enum Authenticity
{
    Authentic,
    False,
    Misleading,
}

/// <summary>
/// One piece of evidence at a crime scene. Plain data; all behaviour lives in
/// <see cref="CrimeService"/>.
/// </summary>
public sealed class Evidence
{
    public string Id { get; set; } = "";
    public string SceneId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Summary { get; set; } = "";

    /// <summary>
    /// Set once at creation and immutable afterwards: the property has no setter and no method
    /// on Evidence (or anywhere else in the codebase) assigns it. False evidence can therefore
    /// never become true — the Phase 7 exit criterion holds structurally, not by convention.
    /// </summary>
    public Authenticity Authenticity { get; }

    public bool Discovered { get; set; }
    public string? DiscoveredBy { get; set; }
    public long? DiscoveredAt { get; set; }

    /// <summary>Used by System.Text.Json on load; production code uses the shorter overload.</summary>
    [JsonConstructor]
    public Evidence(
        string id, string sceneId, string label, string summary, Authenticity authenticity,
        bool discovered, string? discoveredBy, long? discoveredAt)
    {
        Id = id;
        SceneId = sceneId;
        Label = label;
        Summary = summary;
        Authenticity = authenticity;
        Discovered = discovered;
        DiscoveredBy = discoveredBy;
        DiscoveredAt = discoveredAt;
    }

    public Evidence(string id, string sceneId, string label, string summary, Authenticity authenticity)
        : this(id, sceneId, label, summary, authenticity, false, null, null)
    {
    }
}

/// <summary>A sealed-off location holding one incident's evidence. Discovered once, by someone.</summary>
public sealed class CrimeScene
{
    public string Id { get; set; } = "";
    public string CrimeId { get; set; } = "";
    public string LocationId { get; set; } = "";
    public long IncidentEventId { get; set; }

    public string? DiscoveredBy { get; set; }
    public long? DiscoveredAt { get; set; }
    public long? DiscoveryEventId { get; set; }

    /// <summary>Derived, never stored: a scene is discovered exactly when DiscoveredAt is set.</summary>
    [JsonIgnore]
    public bool IsDiscovered => DiscoveredAt.HasValue;
}

/// <summary>One generated incident: what happened, to whom, where, and when.</summary>
public sealed class CrimeRecord
{
    public string Id { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string VictimId { get; set; } = "";
    public bool Fatal { get; set; }
    public string LocationId { get; set; } = "";
    public long IncidentEventId { get; set; }
    public long OccurredAt { get; set; }
    public string SceneId { get; set; } = "";
}

/// <summary>Phase 7: the crime subsystem's plain-data state. Mutated only by CrimeService.</summary>
public sealed class CrimeState
{
    /// <summary>Own persisted RNG stream so crime draws never shift the sim stream (ADR-002).</summary>
    public RngState CrimeRng { get; set; } = new();

    public long NextCrimeId { get; set; } = 1;
    public long NextEvidenceId { get; set; } = 1;

    public Dictionary<string, CrimeRecord> Crimes { get; set; } = new();
    public Dictionary<string, CrimeScene> Scenes { get; set; } = new();
    public Dictionary<string, Evidence> Evidence { get; set; } = new();
}
