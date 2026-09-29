namespace BadDeduction.Content;

/// <summary>How observable a place is: drives witness likelihood and hidden-evidence odds later on.</summary>
public enum LocationVisibility { Public, Private, Secret }

public sealed class LocationConnection
{
    public string To { get; set; } = "";
    public int Minutes { get; set; }
}

/// <summary>Immutable, data-driven description of a place (loaded from data/locations.json).</summary>
public sealed class LocationDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public LocationVisibility Visibility { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<LocationConnection> Connections { get; set; } = new();
}

public sealed class LocationsFile
{
    public List<LocationDefinition> Locations { get; set; } = new();
}
