using BadDeduction.Core;

namespace BadDeduction.Social;

/// <summary>Read/write access to the relationship edges stored in the world state.</summary>
public sealed class RelationshipGraph
{
    private readonly GameState _state;

    public RelationshipGraph(GameState state) => _state = state;

    public IReadOnlyList<RelationshipEdge> Edges => _state.World.Relationships;

    public RelationshipEdge? Get(string from, string to) =>
        _state.World.Relationships.FirstOrDefault(e => e.From == from && e.To == to);

    public IEnumerable<RelationshipEdge> From(string id) => _state.World.Relationships.Where(e => e.From == id);

    public IEnumerable<string> Of(string id, RelationshipKind kind) =>
        From(id).Where(e => e.Kind == kind).Select(e => e.To);

    /// <summary>Adds both directions. Returns false (and adds nothing) if the pair is already related or invalid.</summary>
    public bool Connect(string a, string b, RelationshipKind kind)
    {
        if (a == b || !_state.World.Characters.ContainsKey(a) || !_state.World.Characters.ContainsKey(b)) return false;
        if (Get(a, b) is not null || Get(b, a) is not null) return false;
        // Each direction starts at its resting values, which depend on the kind and on both personalities.
        var pa = _state.World.Profiles.GetValueOrDefault(a)?.Personality;
        var pb = _state.World.Profiles.GetValueOrDefault(b)?.Personality;
        var ab = new RelationshipEdge { From = a, To = b, Kind = kind };
        var ba = new RelationshipEdge { From = b, To = a, Kind = kind };
        SocialBaselines.Apply(ab, SocialBaselines.Resting(kind, pa, pb));
        SocialBaselines.Apply(ba, SocialBaselines.Resting(kind, pb, pa));
        _state.World.Relationships.Add(ab);
        _state.World.Relationships.Add(ba);
        return true;
    }

    /// <summary>Connected components over the given character ids, each sorted ordinally, ordered by first member.</summary>
    public List<List<string>> Components(IEnumerable<string> ids)
    {
        var all = ids.OrderBy(i => i, StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<string>>();
        foreach (var start in all)
        {
            if (!seen.Add(start)) continue;
            var comp = new List<string> { start };
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
                foreach (var e in From(stack.Pop()))
                    if (seen.Add(e.To)) { comp.Add(e.To); stack.Push(e.To); }
            comp.Sort(StringComparer.Ordinal);
            result.Add(comp);
        }
        return result;
    }

    public bool IsConnected(IEnumerable<string> ids) => Components(ids).Count <= 1;
}
