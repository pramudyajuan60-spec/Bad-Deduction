using BadDeduction.Core;

namespace BadDeduction.Investigation;

/// <summary>One maximal interval a character spent at a location: [EnteredAt, ExitedAt).</summary>
public sealed class PresenceEntry
{
    public string CharacterId { get; set; } = "";
    public long EnteredAt { get; set; }
    public long ExitedAt { get; set; }
}

/// <summary>
/// A timestamped observation of who was where — the setting-agnostic surveillance record.
/// Whether the fiction skins this as CCTV footage, a watchman's log or gate ledgers (audit
/// §C-1, still undecided) changes nothing about the mechanic: presence reconstructed from
/// the authoritative movement log.
/// </summary>
public sealed class SurveillanceRecord
{
    public string LocationId { get; set; } = "";
    public long WindowStart { get; set; }
    public long WindowEnd { get; set; }
    public List<PresenceEntry> Entries { get; set; } = new();
}

/// <summary>
/// Reconstructs where anyone was, purely from the movement log — no new state, no RNG,
/// fully deterministic. Two event kinds move characters: <c>character.moved</c> (instant,
/// hand-driven or sim arrival; data from/to) and <c>character.departed</c> (sim travel;
/// data departure/arrival/from/to). Departure does NOT change CurrentLocationId (only
/// arrival does, via WorldService.MoveCharacter), so the initial position is recovered by
/// walking the moved-events backward from the character's current position.
/// </summary>
public static class PresenceTracker
{
    /// <summary>
    /// Where a character was at a given minute: the location id, or null while traveling.
    /// When traveling, <paramref name="travelFrom"/>/<paramref name="travelTo"/> say between where.
    /// </summary>
    public static (string? Location, string? TravelFrom, string? TravelTo) PresenceAt(
        GameState state, string characterId, long minute)
    {
        if (minute < 0) throw new ArgumentException("Minute cannot be negative.", nameof(minute));
        var points = BuildTimeline(state, characterId);
        var location = points[0].Location;
        string? travelFrom = null, travelTo = null;
        foreach (var p in points)
        {
            if (p.Minute > minute) break;
            location = p.Location;
            travelFrom = p.TravelFrom;
            travelTo = p.TravelTo;
        }
        return (location, travelFrom, travelTo);
    }

    /// <summary>
    /// Maximal presence intervals of every character at a location inside [fromMinute, toMinute).
    /// Dead characters are included: a body is still present where it fell.
    /// </summary>
    public static IReadOnlyList<PresenceEntry> IntervalsAt(
        GameState state, string locationId, long fromMinute, long toMinute)
    {
        if (fromMinute < 0) throw new ArgumentException("Window start cannot be negative.", nameof(fromMinute));
        if (toMinute < fromMinute) throw new ArgumentException("Window end is before its start.", nameof(toMinute));

        var entries = new List<PresenceEntry>();
        foreach (var id in state.World.Characters.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var points = BuildTimeline(state, id);
            string? current = null;
            // Location just before the window opens.
            foreach (var p in points)
            {
                if (p.Minute > fromMinute) break;
                current = p.Location;
            }
            long? openStart = current == locationId ? fromMinute : null;
            foreach (var p in points)
            {
                if (p.Minute <= fromMinute || p.Minute >= toMinute) continue;
                if (p.Location == current) continue; // no change: keep the interval maximal
                if (openStart.HasValue)
                {
                    entries.Add(new PresenceEntry { CharacterId = id, EnteredAt = openStart.Value, ExitedAt = p.Minute });
                    openStart = null;
                }
                current = p.Location;
                if (current == locationId) openStart = p.Minute;
            }
            if (openStart.HasValue)
                entries.Add(new PresenceEntry { CharacterId = id, EnteredAt = openStart.Value, ExitedAt = toMinute });
        }
        return entries;
    }

    private sealed record Waypoint(long Minute, string? Location, string? TravelFrom, string? TravelTo);

    /// <summary>
    /// The character's location timeline from minute 0: (minute, location-after-minute).
    /// A null location means traveling between TravelFrom and TravelTo.
    /// </summary>
    private static List<Waypoint> BuildTimeline(GameState state, string characterId)
    {
        if (!state.World.Characters.TryGetValue(characterId, out var character))
            throw new ArgumentException($"Unknown character '{characterId}'.", nameof(characterId));

        var moved = new List<WorldEvent>();
        var departed = new List<WorldEvent>();
        foreach (var e in state.EventLog.Events)
        {
            if (e.Participants.Count == 0 || e.Participants[0] != characterId) continue;
            if (e.Type == WorldEventTypes.CharacterMoved) moved.Add(e);
            else if (e.Type == WorldEventTypes.CharacterDeparted) departed.Add(e);
        }

        // Initial position: walk the moved-events backward from the current position.
        // Departures never change CurrentLocationId (only arrivals do), so they are ignored here.
        var initial = character.CurrentLocationId;
        foreach (var e in moved.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id))
        {
            if (e.Data.TryGetValue("to", out var to) && to == initial &&
                e.Data.TryGetValue("from", out var from))
                initial = from;
        }

        var points = new List<Waypoint> { new(0, initial, null, null) };
        var ordered = moved.Cast<WorldEvent>()
            .Concat(departed)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToList();

        foreach (var e in ordered)
        {
            if (e.Type == WorldEventTypes.CharacterDeparted)
            {
                if (!e.Data.TryGetValue("departure", out var depText) || !long.TryParse(depText, out var departure)) continue;
                if (!e.Data.TryGetValue("arrival", out var arrText) || !long.TryParse(arrText, out var arrival)) continue;
                if (!e.Data.TryGetValue("from", out var from) || !e.Data.TryGetValue("to", out var to)) continue;
                TruncateAfter(points, departure);
                points.Add(new Waypoint(departure, null, from, to));
                points.Add(new Waypoint(arrival, to, null, null));
            }
            else
            {
                if (!e.Data.TryGetValue("to", out var to)) continue;
                TruncateAfter(points, e.Timestamp);
                points.Add(new Waypoint(e.Timestamp, to, null, null));
            }
        }
        return points;
    }

    /// <summary>Drops timeline points after a minute (pathological overlaps: hand-driven moves mid-travel).</summary>
    private static void TruncateAfter(List<Waypoint> points, long minute)
    {
        while (points.Count > 1 && points[^1].Minute > minute)
            points.RemoveAt(points.Count - 1);
    }
}
