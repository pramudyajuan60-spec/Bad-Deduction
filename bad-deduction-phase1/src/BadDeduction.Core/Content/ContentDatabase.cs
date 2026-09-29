using System.Text.Json;
using System.Text.Json.Serialization;

namespace BadDeduction.Content;

/// <summary>
/// Read-only game content shared by every run. Phase 1 holds locations only; occupations,
/// personalities, evidence and crime definitions are added by later phases as JSON files.
/// </summary>
public sealed class ContentDatabase
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // typos in data files must fail loudly
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, LocationDefinition> _locations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _adjacency = new(StringComparer.Ordinal);
    private readonly List<string> _buildErrors = new();

    public IReadOnlyList<LocationDefinition> Locations { get; }

    public ContentDatabase(IEnumerable<LocationDefinition> locations)
    {
        var ordered = new List<LocationDefinition>();
        foreach (var def in locations)
        {
            if (string.IsNullOrWhiteSpace(def.Id)) { _buildErrors.Add("Location with empty id."); continue; }
            if (!_locations.TryAdd(def.Id, def)) { _buildErrors.Add($"Duplicate location id '{def.Id}'."); continue; }
            ordered.Add(def);
            _adjacency[def.Id] = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        Locations = ordered;

        // Connections are undirected; declaring both directions is allowed if the times agree.
        foreach (var def in ordered)
        {
            foreach (var link in def.Connections)
            {
                if (!_locations.ContainsKey(link.To)) { _buildErrors.Add($"'{def.Id}' connects to unknown location '{link.To}'."); continue; }
                if (link.To == def.Id) { _buildErrors.Add($"'{def.Id}' connects to itself."); continue; }
                if (link.Minutes <= 0) { _buildErrors.Add($"'{def.Id}' -> '{link.To}' has non-positive travel time."); continue; }

                if (_adjacency[def.Id].TryGetValue(link.To, out var existing) && existing != link.Minutes)
                    _buildErrors.Add($"Conflicting travel time between '{def.Id}' and '{link.To}' ({existing} vs {link.Minutes}).");
                else
                {
                    _adjacency[def.Id][link.To] = link.Minutes;
                    _adjacency[link.To][def.Id] = link.Minutes;
                }
            }
        }
    }

    public bool HasLocation(string id) => _locations.ContainsKey(id);

    public LocationDefinition GetLocation(string id) =>
        _locations.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"Unknown location '{id}'.");

    public IReadOnlyDictionary<string, int> Neighbors(string id) =>
        _adjacency.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException($"Unknown location '{id}'.");

    /// <summary>Returns all data-integrity problems (empty when the content is valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(_buildErrors);
        if (Locations.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { Locations[0].Id };
            var stack = new Stack<string>();
            stack.Push(Locations[0].Id);
            while (stack.Count > 0)
                foreach (var next in _adjacency[stack.Pop()].Keys)
                    if (seen.Add(next)) stack.Push(next);
            foreach (var def in Locations)
                if (!seen.Contains(def.Id)) errors.Add($"Location '{def.Id}' is unreachable from '{Locations[0].Id}'.");
        }
        return errors;
    }

    public void ThrowIfInvalid()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new InvalidDataException("Invalid game content:\n - " + string.Join("\n - ", errors));
    }

    public static ContentDatabase LoadFromDirectory(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "locations.json");
        var file = JsonSerializer.Deserialize<LocationsFile>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException($"'{path}' is empty.");
        var db = new ContentDatabase(file.Locations);
        db.ThrowIfInvalid();
        return db;
    }
}
