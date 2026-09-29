using BadDeduction.Characters;

namespace BadDeduction.Core;

/// <summary>
/// Structural integrity checks for a GameState. Used on every load, and by tests.
/// The same "validate before trusting" principle applies to LLM output in Phase 6.
/// </summary>
public static class GameStateValidator
{
    public static IReadOnlyList<string> Validate(GameState state)
    {
        var errors = new List<string>();

        if (state.TotalMinutes < 0) errors.Add("TotalMinutes is negative.");
        if (state.SimRng.IsZero) errors.Add("SimRng state is all zero.");

        // World
        foreach (var (key, loc) in state.World.Locations)
            if (key != loc.Id) errors.Add($"Location key '{key}' does not match id '{loc.Id}'.");

        foreach (var (key, c) in state.World.Characters)
        {
            if (key != c.Id) errors.Add($"Character key '{key}' does not match id '{c.Id}'.");
            if (c.Age < 0) errors.Add($"Character '{c.Id}' has a negative age.");
            if (!state.World.Locations.ContainsKey(c.HomeLocationId)) errors.Add($"Character '{c.Id}' has unknown home '{c.HomeLocationId}'.");
            if (!state.World.Locations.ContainsKey(c.CurrentLocationId)) errors.Add($"Character '{c.Id}' has unknown current location '{c.CurrentLocationId}'.");
            if (c.WorkLocationId is not null && !state.World.Locations.ContainsKey(c.WorkLocationId))
                errors.Add($"Character '{c.Id}' has unknown work location '{c.WorkLocationId}'.");
        }

        // Event log
        var log = state.EventLog;
        if (log.NextId != log.Events.Count + 1) errors.Add("EventLog.NextId is inconsistent with the number of events.");
        long previousTime = 0;
        for (var i = 0; i < log.Events.Count; i++)
        {
            var e = log.Events[i];
            if (e.Id != i + 1) errors.Add($"Event at index {i} has id {e.Id}; expected {i + 1}.");
            if (e.Timestamp < previousTime) errors.Add($"Event {e.Id} is out of chronological order.");
            if (e.Timestamp > state.TotalMinutes) errors.Add($"Event {e.Id} is in the future.");
            if (e.CausedBy is { } cause && (cause < 1 || cause >= e.Id)) errors.Add($"Event {e.Id} has invalid CausedBy {cause}.");
            previousTime = e.Timestamp;
        }

        // Truth / identities
        var counts = new Dictionary<HiddenRole, int>();
        foreach (var (id, role) in state.Truth.HiddenRoles)
        {
            if (!state.World.Characters.ContainsKey(id)) errors.Add($"Hidden role {role} assigned to unknown character '{id}'.");
            counts[role] = counts.GetValueOrDefault(role) + 1;
        }
        foreach (var (role, n) in counts)
            if (n > 1) errors.Add($"Hidden role {role} is held by {n} characters; at most one is allowed.");

        // Player
        var pid = state.Player.CharacterId;
        if (pid.Length > 0)
        {
            if (!state.World.Characters.ContainsKey(pid)) errors.Add($"Player character '{pid}' does not exist.");
            else if (state.Truth.HiddenRoles.TryGetValue(pid, out var playerRole))
            {
                var expected = state.Meta.Campaign == Campaign.Malvr ? HiddenRole.Malvr : HiddenRole.Lumiel;
                if (playerRole != expected)
                    errors.Add($"Player is {playerRole} but the campaign is {state.Meta.Campaign}.");
            }
        }

        return errors;
    }
}
