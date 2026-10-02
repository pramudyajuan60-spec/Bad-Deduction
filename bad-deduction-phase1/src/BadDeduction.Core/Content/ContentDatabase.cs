using System.Text.Json;
using System.Text.Json.Serialization;

namespace BadDeduction.Content;

/// <summary>
/// Read-only game content shared by every run. Locations (Phase 1) plus occupations, goals, secrets and
/// names (Phase 2). Evidence and crime definitions are added by later phases as JSON files.
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

    private readonly Dictionary<string, Dictionary<string, int>> _travel = new(StringComparer.Ordinal);

    public IReadOnlyList<LocationDefinition> Locations { get; }
    public IReadOnlyList<OccupationDefinition> Occupations { get; }
    public IReadOnlyList<GoalDefinition> Goals { get; }
    public IReadOnlyList<SecretDefinition> Secrets { get; }
    public NameTable Names { get; }

    public ContentDatabase(
        IEnumerable<LocationDefinition> locations,
        IEnumerable<OccupationDefinition>? occupations = null,
        IEnumerable<GoalDefinition>? goals = null,
        IEnumerable<SecretDefinition>? secrets = null,
        NameTable? names = null)
    {
        Occupations = (occupations ?? Array.Empty<OccupationDefinition>()).ToList();
        Goals = (goals ?? Array.Empty<GoalDefinition>()).ToList();
        Secrets = (secrets ?? Array.Empty<SecretDefinition>()).ToList();
        Names = names ?? new NameTable();

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

    /// <summary>Shortest travel time in minutes between two locations (0 when identical).</summary>
    public int TravelMinutes(string from, string to)
    {
        if (!_locations.ContainsKey(from)) throw new KeyNotFoundException($"Unknown location '{from}'.");
        if (!_locations.ContainsKey(to)) throw new KeyNotFoundException($"Unknown location '{to}'.");
        if (!_travel.TryGetValue(from, out var dist))
            _travel[from] = dist = ShortestPaths(from);
        return dist.TryGetValue(to, out var m)
            ? m
            : throw new InvalidOperationException($"'{to}' is unreachable from '{from}'.");
    }

    private Dictionary<string, int> ShortestPaths(string source)
    {
        var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [source] = 0 };
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            string? best = null;
            foreach (var (id, d) in dist)
                if (!done.Contains(id) && (best is null || d < dist[best] || (d == dist[best] && string.CompareOrdinal(id, best) < 0)))
                    best = id;
            if (best is null) return dist;
            done.Add(best);
            foreach (var (next, w) in _adjacency[best])
                if (!dist.TryGetValue(next, out var old) || dist[best] + w < old)
                    dist[next] = dist[best] + w;
        }
    }

    public OccupationDefinition GetOccupation(string id) =>
        Occupations.FirstOrDefault(o => o.Id == id) ?? throw new KeyNotFoundException($"Unknown occupation '{id}'.");

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
        errors.AddRange(ValidateCastContent());
        return errors;
    }

    private IEnumerable<string> ValidateCastContent()
    {
        var errors = new List<string>();
        var occIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Occupations)
        {
            if (string.IsNullOrWhiteSpace(o.Id) || !occIds.Add(o.Id)) errors.Add($"Occupation id '{o.Id}' is empty or duplicated.");
            if (!_locations.ContainsKey(o.WorkLocationId)) errors.Add($"Occupation '{o.Id}' works at unknown location '{o.WorkLocationId}'.");
            if (o.ShiftStartHour < 0 || o.ShiftEndHour > 23 || o.ShiftStartHour >= o.ShiftEndHour)
                errors.Add($"Occupation '{o.Id}' has an invalid shift {o.ShiftStartHour}-{o.ShiftEndHour} (must be within 0-23).");
            if (o.MinAge < 18 || o.MinAge > o.MaxAge) errors.Add($"Occupation '{o.Id}' has an invalid age range {o.MinAge}-{o.MaxAge}.");
            if (o.Weight <= 0 || o.MaxCount <= 0) errors.Add($"Occupation '{o.Id}' needs positive weight and maxCount.");
            if (o.MinCount < 0 || o.MinCount > o.MaxCount) errors.Add($"Occupation '{o.Id}' has minCount outside 0..maxCount.");
        }

        var goalIds = new HashSet<string>(StringComparer.Ordinal);
        var knownTags = new HashSet<string> { "family", "friend", "secret", "police", "civilian" };
        foreach (var g in Goals)
        {
            if (string.IsNullOrWhiteSpace(g.Id) || !goalIds.Add(g.Id)) errors.Add($"Goal id '{g.Id}' is empty or duplicated.");
            if (g.Weight <= 0) errors.Add($"Goal '{g.Id}' needs a positive weight.");
            foreach (var tag in g.Requires)
                if (!knownTags.Contains(tag)) errors.Add($"Goal '{g.Id}' requires unknown tag '{tag}'.");
        }

        var secretIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sd in Secrets)
        {
            if (string.IsNullOrWhiteSpace(sd.Id) || !secretIds.Add(sd.Id)) errors.Add($"Secret id '{sd.Id}' is empty or duplicated.");
            if (sd.Severity is < 1 or > 3) errors.Add($"Secret '{sd.Id}' severity must be 1-3.");
            if (sd.Weight <= 0) errors.Add($"Secret '{sd.Id}' needs a positive weight.");
        }

        if (Names.Given.Distinct().Count() != Names.Given.Count || Names.Surnames.Distinct().Count() != Names.Surnames.Count)
            errors.Add("Name tables contain duplicates.");
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

        T Read<T>(string name) where T : class
        {
            var p = Path.Combine(dataDirectory, name);
            return JsonSerializer.Deserialize<T>(File.ReadAllText(p), JsonOptions)
                   ?? throw new InvalidDataException($"'{p}' is empty.");
        }

        var db = new ContentDatabase(
            file.Locations,
            Read<OccupationsFile>("occupations.json").Occupations,
            Read<GoalsFile>("goals.json").Goals,
            Read<SecretsFile>("character_secrets.json").Secrets,
            Read<NameTable>("names.json"));
        db.ThrowIfInvalid();
        return db;
    }
}
