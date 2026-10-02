namespace BadDeduction.Content;

/// <summary>One piece of evidence a crime definition can spawn. Authenticity is drawn from the
/// three weights (they must sum to more than zero) and is immutable once drawn.</summary>
public sealed class EvidenceTemplate
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Summary { get; set; } = "";
    public int AuthenticWeight { get; set; }
    public int FalseWeight { get; set; }
    public int MisleadingWeight { get; set; }
}

/// <summary>
/// Immutable, data-driven description of an incident type (loaded from data/crimes.json).
/// Murder is the first-class type; arson is a variant that can produce victims and evidence.
/// The slice incident is data, not code (audit §C-2).
/// </summary>
public sealed class CrimeDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Which CharacterKind values may be picked as victim (empty = any kind).
    /// The player character is never eligible, whatever this list says.</summary>
    public List<string> VictimKinds { get; set; } = new();

    /// <summary>Whether the victim dies in the incident. A surviving victim is a prime witness.</summary>
    public bool Fatal { get; set; }

    /// <summary>Victims are preferred at locations carrying one of these tags.</summary>
    public List<string> LocationTags { get; set; } = new();

    /// <summary>Nobody can discover the scene before this many minutes have passed.</summary>
    public int MinDiscoveryDelayMinutes { get; set; }

    public List<EvidenceTemplate> EvidenceTemplates { get; set; } = new();
}

public sealed class CrimesFile
{
    public List<CrimeDefinition> Crimes { get; set; } = new();
}
