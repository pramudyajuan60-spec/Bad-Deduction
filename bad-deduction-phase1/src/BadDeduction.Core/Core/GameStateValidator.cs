using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Social;
using BadDeduction.World;

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

        errors.AddRange(ValidateCast(state));
        errors.AddRange(ValidateCognition(state));

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

    private static IEnumerable<string> ValidateCast(GameState state)
    {
        var errors = new List<string>();
        var w = state.World;

        foreach (var (id, profile) in w.Profiles)
        {
            if (!w.Characters.ContainsKey(id)) errors.Add($"Profile exists for unknown character '{id}'.");
            foreach (var t in profile.Personality.All())
                if (t is < 0 or > 100) errors.Add($"Profile '{id}' has a personality trait outside 0-100.");
            foreach (var g in profile.Goals)
            {
                if (g.Priority is < 0 or > 100) errors.Add($"Profile '{id}' has a goal priority outside 0-100.");
                if (g.TargetId is not null && !w.Characters.ContainsKey(g.TargetId)) errors.Add($"Profile '{id}' has a goal aimed at unknown character '{g.TargetId}'.");
            }
        }

        foreach (var id in w.Schedules.Keys)
            if (!w.Characters.ContainsKey(id)) errors.Add($"Schedule exists for unknown character '{id}'.");

        var seen = new HashSet<(string, string)>();
        foreach (var e in w.Relationships)
        {
            if (!w.Characters.ContainsKey(e.From) || !w.Characters.ContainsKey(e.To)) errors.Add($"Relationship {e.From}->{e.To} references an unknown character.");
            else if (e.From == e.To) errors.Add($"Relationship {e.From}->{e.To} points at itself.");
            foreach (var axis in Social.SocialRules.AllAxes)
                if (e.GetAxis(axis) is < 0 or > 100) errors.Add($"Relationship {e.From}->{e.To} has {axis} outside 0-100.");
            if (!seen.Add((e.From, e.To))) errors.Add($"Duplicate relationship {e.From}->{e.To}.");
        }
        foreach (var (from, to) in seen)
            if (!seen.Contains((to, from))) errors.Add($"Relationship {from}->{to} has no reverse edge.");
        return errors;
    }

    private static IEnumerable<string> ValidateCognition(GameState state)
    {
        var errors = new List<string>();
        var cog = state.Cognition;
        if (cog.NextMemoryId < 1) errors.Add("Cognition.NextMemoryId is less than 1.");
        if (cog.CognitionRng.IsZero) errors.Add("Cognition.CognitionRng state is all zero.");

        var seenIds = new HashSet<long>();
        foreach (var (charId, list) in cog.Memories)
        {
            if (!state.World.Characters.ContainsKey(charId)) errors.Add($"Memories exist for unknown character '{charId}'.");
            foreach (var m in list)
            {
                if (m.Id < 1 || m.Id >= cog.NextMemoryId) errors.Add($"Memory {m.Id} for '{charId}' has an id outside the valid range.");
                if (!seenIds.Add(m.Id)) errors.Add($"Duplicate memory id {m.Id}.");
                if (m.CharacterId != charId) errors.Add($"Memory {m.Id} is filed under '{charId}' but belongs to '{m.CharacterId}'.");
                if (string.IsNullOrWhiteSpace(m.Summary)) errors.Add($"Memory {m.Id} has an empty summary.");
                if (m.Confidence is < 0 or > 100) errors.Add($"Memory {m.Id} has confidence outside 0-100.");
                if (m.EventId is { } eid && !state.EventLog.TryGet(eid, out _)) errors.Add($"Memory {m.Id} references unknown event {eid}.");
                if (m.RecordedAt < 0 || m.RecordedAt > state.TotalMinutes) errors.Add($"Memory {m.Id} has an impossible timestamp.");
            }
        }

        foreach (var (charId, list) in cog.Beliefs)
        {
            if (!state.World.Characters.ContainsKey(charId)) errors.Add($"Beliefs exist for unknown character '{charId}'.");
            var seenProps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in list)
            {
                if (string.IsNullOrWhiteSpace(b.PropositionId)) errors.Add($"Character '{charId}' has a belief with an empty proposition id.");
                if (!seenProps.Add(b.PropositionId)) errors.Add($"Character '{charId}' has a duplicate belief '{b.PropositionId}'.");
                if (b.Confidence is < 0 or > 100) errors.Add($"Belief '{b.PropositionId}' has confidence outside 0-100.");
                if (b.EvidenceFor < 0 || b.EvidenceAgainst < 0) errors.Add($"Belief '{b.PropositionId}' has negative evidence counts.");
                if (b.UpdatedAt < 0 || b.UpdatedAt > state.TotalMinutes) errors.Add($"Belief '{b.PropositionId}' has an impossible timestamp.");
            }
        }

        foreach (var (charId, known) in cog.KnownEvents)
        {
            if (!state.World.Characters.ContainsKey(charId)) errors.Add($"KnownEvents exist for unknown character '{charId}'.");
            foreach (var eid in known)
                if (!state.EventLog.TryGet(eid, out _)) errors.Add($"KnownEvents for '{charId}' references unknown event {eid}.");
        }

        foreach (var eid in cog.PlayerJournal)
            if (!state.EventLog.TryGet(eid, out _)) errors.Add($"PlayerJournal references unknown event {eid}.");

        return errors;
    }

    /// <summary>Checks that need the loaded content: known goal/secret ids and travel-feasible schedules.</summary>
    public static IEnumerable<string> ValidateAgainstContent(GameState state, ContentDatabase content)
    {
        var errors = new List<string>();
        foreach (var (id, profile) in state.World.Profiles)
        {
            foreach (var g in profile.Goals)
                if (!content.Goals.Any(d => d.Id == g.DefinitionId)) errors.Add($"Profile '{id}' has unknown goal '{g.DefinitionId}'.");
            foreach (var sid in profile.SecretIds)
                if (!content.Secrets.Any(d => d.Id == sid)) errors.Add($"Profile '{id}' has unknown secret '{sid}'.");
        }
        foreach (var (id, schedule) in state.World.Schedules)
            if (state.World.Characters.TryGetValue(id, out var c))
                errors.AddRange(ScheduleSystem.Check(id, c.HomeLocationId, schedule, content));
        return errors;
    }
}
