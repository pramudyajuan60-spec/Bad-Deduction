using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.World;

namespace BadDeduction.Crime;

/// <summary>
/// Phase 7: the only sanctioned way to create crimes, seal scenes, spawn evidence, and discover
/// them. All notable mutations are logged as causally-linked WorldEvents.
/// <para/>
/// Discovery seam: the service subscribes to <see cref="WorldEventRecorded"/> and runs
/// <see cref="CheckDiscovery"/> on every <c>character.moved</c> event. Arrival-driven discovery
/// therefore works identically for the Phase 5 simulation and for hand-driven moves (tests, later
/// UI) — no WorldSimulation changes were needed, and the discovery rule lives in exactly one place.
/// <see cref="CheckDiscovery"/> is also public for manual use (e.g. a character already present).
/// <para/>
/// Nothing here reads hidden roles (ADR-003/ADR-015): victim selection uses only the character
/// roster and a seeded RNG stream, so the same seed always yields the same incident no matter who
/// secretly holds a role — pinned by a test.
/// </summary>
public sealed class CrimeService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly CognitionService _cognition;
    private readonly ContentDatabase _content;
    private readonly DeterministicRandom _rng;

    public CrimeService(GameState state, EventSystem events, CognitionService cognition, ContentDatabase content)
    {
        _state = state;
        _events = events;
        _cognition = cognition;
        _content = content;
        _rng = new DeterministicRandom(state.Crime.CrimeRng);
        _events.Subscribe<WorldEventRecorded>(OnWorldEvent);
    }

    private void OnWorldEvent(WorldEventRecorded msg)
    {
        if (msg.Event.Type == WorldEventTypes.CharacterMoved && msg.Event.Participants.Count > 0)
            CheckDiscovery(msg.Event.Participants[0]);
    }

    // ------------------------------------------------------------------ reads

    public CrimeRecord GetCrime(string crimeId) =>
        _state.Crime.Crimes.TryGetValue(crimeId, out var c)
            ? c
            : throw new KeyNotFoundException($"Unknown crime '{crimeId}'.");

    public CrimeScene GetScene(string sceneId) =>
        _state.Crime.Scenes.TryGetValue(sceneId, out var s)
            ? s
            : throw new KeyNotFoundException($"Unknown crime scene '{sceneId}'.");

    public Evidence GetEvidence(string evidenceId) =>
        _state.Crime.Evidence.TryGetValue(evidenceId, out var e)
            ? e
            : throw new KeyNotFoundException($"Unknown evidence '{evidenceId}'.");

    public IReadOnlyList<CrimeRecord> AllCrimes() =>
        _state.Crime.Crimes.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<Evidence> EvidenceAtScene(string sceneId)
    {
        GetScene(sceneId); // validates the id
        return _state.Crime.Evidence.Values
            .Where(e => e.SceneId == sceneId)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Witnesses are derived, never stored redundantly: the living characters who know the
    /// incident event (through the Phase 4 knowledge gate). Query-only, so no event spam.
    /// </summary>
    public IReadOnlyList<string> GetWitnesses(string crimeId)
    {
        var crime = GetCrime(crimeId);
        return _state.World.Characters.Values
            .Where(c => c.IsAlive && _cognition.Knows(c.Id, crime.IncidentEventId))
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The incident chain in time order — incident → discovery → evidence discoveries — as a
    /// read-only view for the future investigation board. Every crime event carries the crime id
    /// in its data, so the timeline is a simple filtered, ordered scan.
    /// </summary>
    public IReadOnlyList<WorldEvent> GetTimeline(string crimeId)
    {
        GetCrime(crimeId); // validates the id
        return _state.EventLog.Events
            .Where(e => e.Data.TryGetValue("crime", out var c) && c == crimeId)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToList();
    }

    // ------------------------------------------------------------------ generation

    /// <summary>
    /// Generates one incident from a data-driven <see cref="CrimeDefinition"/>: picks a victim
    /// (living, never the player), kills them if the definition is fatal, seals their location,
    /// creates the scene, and spawns one Evidence item per template with authenticity drawn from
    /// the template's weights. Time of the incident is now. Returns the crime record.
    /// </summary>
    public CrimeRecord GenerateIncident(string definitionId)
    {
        var def = _content.GetCrime(definitionId);
        var victim = PickVictim(def);
        var locationId = victim.CurrentLocationId;

        // The dead leave no trips behind.
        _state.World.ActiveTravels.Remove(victim.Id);
        if (def.Fatal)
            victim.IsAlive = false;

        var n = _state.Crime.NextCrimeId++;
        var crimeId = $"crime_{n}";
        var sceneId = $"scene_{n}";

        // State before event: subscribers to crime.incident (e.g. the police alert ladder,
        // Phase 9) must see the crime, scene, evidence and seal already in place, so the
        // records are built first and the incident event is published last. IncidentEventId
        // is fixed up once the event exists (single-threaded: nothing reads it in between).
        _state.World.Locations[locationId].IsSealed = true;

        var crime = new CrimeRecord
        {
            Id = crimeId,
            DefinitionId = def.Id,
            VictimId = victim.Id,
            Fatal = def.Fatal,
            LocationId = locationId,
            IncidentEventId = 0, // fixed up below
            OccurredAt = _state.TotalMinutes,
            SceneId = sceneId,
        };
        _state.Crime.Crimes.Add(crimeId, crime);
        var scene = new CrimeScene
        {
            Id = sceneId,
            CrimeId = crimeId,
            LocationId = locationId,
            IncidentEventId = 0, // fixed up below
        };
        _state.Crime.Scenes.Add(sceneId, scene);

        // Evidence comes into existence quietly: it is ground truth, but nobody knows about it
        // until it is discovered, so per the event-volume policy (ADR-026) creation is not logged.
        foreach (var template in def.EvidenceTemplates)
        {
            var evidenceId = $"ev_{_state.Crime.NextEvidenceId++}";
            _state.Crime.Evidence.Add(evidenceId,
                new Evidence(evidenceId, sceneId, template.Label, template.Summary, DrawAuthenticity(template)));
        }

        var incident = _events.Record(WorldEventTypes.CrimeIncident,
            locationId: locationId,
            participants: new[] { victim.Id },
            data: new Dictionary<string, string>
            {
                ["crime"] = crimeId,
                ["definition"] = def.Id,
                ["victim"] = victim.Id,
                ["fatal"] = def.Fatal ? "true" : "false",
            });
        crime.IncidentEventId = incident.Id;
        scene.IncidentEventId = incident.Id;

        return crime;
    }

    /// <summary>
    /// Picks the victim deterministically: living, never the player, optionally restricted to the
    /// definition's victim kinds. Victims at a tag-matching location are preferred; when none
    /// exists there, any eligible character may be picked (documented fallback).
    /// </summary>
    private CharacterState PickVictim(CrimeDefinition def)
    {
        var kinds = def.VictimKinds.Count == 0
            ? null
            : new HashSet<string>(def.VictimKinds, StringComparer.Ordinal);

        bool Eligible(CharacterState c) =>
            c.IsAlive
            && c.Id != _state.Player.CharacterId
            && (kinds is null || kinds.Contains(c.Kind.ToString()));

        bool AtTaggedLocation(CharacterState c) =>
            def.LocationTags.Count > 0
            && _content.GetLocation(c.CurrentLocationId).Tags
                .Any(t => def.LocationTags.Contains(t, StringComparer.Ordinal));

        var tagged = _state.World.Characters.Values
            .Where(c => Eligible(c) && AtTaggedLocation(c))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var pool = tagged.Count > 0
            ? tagged
            : _state.World.Characters.Values
                .Where(Eligible)
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();

        if (pool.Count == 0)
            throw new InvalidOperationException(
                $"No eligible victim for crime '{def.Id}': every candidate is dead or is the player character.");
        return _rng.Pick(pool);
    }

    private Authenticity DrawAuthenticity(EvidenceTemplate template)
    {
        var total = template.AuthenticWeight + template.FalseWeight + template.MisleadingWeight;
        var roll = _rng.NextInt(0, total);
        if (roll < template.AuthenticWeight) return Authenticity.Authentic;
        if (roll < template.AuthenticWeight + template.FalseWeight) return Authenticity.False;
        return Authenticity.Misleading;
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// A living character at a sealed scene location discovers its undiscovered scenes once the
    /// definition's discovery delay has passed. Discovers every eligible scene (normally one),
    /// records <c>crime.discovered</c> (CausedBy → the incident), and everyone present witnesses
    /// the incident through the Phase 4 knowledge gate. Returns the discovery events.
    /// </summary>
    public IReadOnlyList<WorldEvent> CheckDiscovery(string characterId)
    {
        var c = RequireAliveCharacter(characterId);
        var found = new List<WorldEvent>();
        foreach (var scene in _state.Crime.Scenes.Values
                     .Where(s => s.LocationId == c.CurrentLocationId && !s.IsDiscovered)
                     .OrderBy(s => s.Id, StringComparer.Ordinal)
                     .ToList())
        {
            var crime = _state.Crime.Crimes[scene.CrimeId];
            var def = _content.GetCrime(crime.DefinitionId);
            if (_state.TotalMinutes < crime.OccurredAt + def.MinDiscoveryDelayMinutes)
                continue;

            scene.DiscoveredBy = characterId;
            scene.DiscoveredAt = _state.TotalMinutes;
            var discovered = _events.Record(WorldEventTypes.CrimeDiscovered,
                locationId: scene.LocationId,
                participants: new[] { characterId },
                data: new Dictionary<string, string>
                {
                    ["crime"] = crime.Id,
                    ["scene"] = scene.Id,
                    ["definition"] = crime.DefinitionId,
                    ["discoverer"] = characterId,
                },
                causedBy: crime.IncidentEventId);
            scene.DiscoveryEventId = discovered.Id;

            // Everyone present — the discoverer included — witnesses the incident. The dead
            // perceive nothing.
            _cognition.Perceive(characterId, crime.IncidentEventId, MemorySource.Witnessed, causedBy: discovered.Id);
            foreach (var w in LivingIdsAt(scene.LocationId, characterId))
                _cognition.Perceive(w, crime.IncidentEventId, MemorySource.Witnessed, causedBy: discovered.Id);

            found.Add(discovered);
        }
        return found;
    }

    /// <summary>
    /// A character who knows about the incident examines one piece of evidence at its discovered
    /// scene. Knowledge-gated (ADR-016): you cannot investigate what you never heard of. Marks the
    /// evidence discovered, logs <c>crime.evidence_discovered</c> (CausedBy → the scene discovery),
    /// and everyone present perceives the find. Authenticity is never touched.
    /// </summary>
    public WorldEvent DiscoverEvidence(string characterId, string evidenceId)
    {
        var c = RequireAliveCharacter(characterId);
        var evidence = GetEvidence(evidenceId);
        if (evidence.Discovered)
            throw new InvalidOperationException($"Evidence '{evidenceId}' was already discovered.");
        var scene = GetScene(evidence.SceneId);
        if (!scene.IsDiscovered)
            throw new InvalidOperationException($"Scene '{scene.Id}' is still undiscovered.");
        if (c.CurrentLocationId != scene.LocationId)
            throw new InvalidOperationException($"'{characterId}' is not at the scene.");
        var crime = _state.Crime.Crimes[scene.CrimeId];
        if (!_cognition.Knows(characterId, crime.IncidentEventId))
            throw new InvalidOperationException(
                $"'{characterId}' does not know about the incident and cannot investigate its evidence.");

        evidence.Discovered = true;
        evidence.DiscoveredBy = characterId;
        evidence.DiscoveredAt = _state.TotalMinutes;

        var discovered = _events.Record(WorldEventTypes.EvidenceDiscovered,
            locationId: scene.LocationId,
            participants: new[] { characterId },
            data: new Dictionary<string, string>
            {
                ["crime"] = crime.Id,
                ["scene"] = scene.Id,
                ["evidence"] = evidence.Id,
                ["label"] = evidence.Label,
                ["authenticity"] = evidence.Authenticity.ToString(),
            },
            causedBy: scene.DiscoveryEventId ?? crime.IncidentEventId);

        _cognition.Perceive(characterId, discovered.Id, MemorySource.Witnessed, causedBy: discovered.Id);
        foreach (var w in LivingIdsAt(scene.LocationId, characterId))
            _cognition.Perceive(w, discovered.Id, MemorySource.Witnessed, causedBy: discovered.Id);
        return discovered;
    }

    // ------------------------------------------------------------------ helpers

    private CharacterState RequireAliveCharacter(string id)
    {
        if (!_state.World.Characters.TryGetValue(id, out var c))
            throw new ArgumentException($"Unknown character '{id}'.");
        if (!c.IsAlive)
            throw new InvalidOperationException($"'{id}' is dead and cannot discover anything.");
        return c;
    }

    private List<string> LivingIdsAt(string locationId, string excludeId) =>
        _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != excludeId && c.CurrentLocationId == locationId)
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
}
