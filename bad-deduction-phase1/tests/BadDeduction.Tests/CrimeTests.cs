using System.Text.Json.Nodes;
using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class CrimeTests
{
    // ------------------------------------------------------------ content

    [Fact]
    public void Crimes_json_loads_murder_and_arson()
    {
        var content = TestSupport.LoadContent();
        Assert.Equal(2, content.Crimes.Count);
        var murder = content.GetCrime("murder");
        Assert.Equal("Murder", murder.Label);
        Assert.True(murder.Fatal);
        Assert.Equal(3, murder.EvidenceTemplates.Count);
        var arson = content.GetCrime("arson");
        Assert.False(arson.Fatal);
        Assert.Equal(3, arson.EvidenceTemplates.Count);
    }

    [Fact]
    public void Unknown_crime_definition_throws()
    {
        var s = TestSupport.NewPopulatedSession(42);
        Assert.Throws<KeyNotFoundException>(() => s.Crime.GenerateIncident("no_such_crime"));
        Assert.Throws<KeyNotFoundException>(() => s.Crime.GetCrime("no_such_crime"));
        Assert.Throws<KeyNotFoundException>(() => s.Crime.GetScene("no_such_scene"));
        Assert.Throws<KeyNotFoundException>(() => s.Crime.GetEvidence("no_such_evidence"));
    }

    // ------------------------------------------------------------ generation

    [Fact]
    public void Generation_is_deterministic_for_the_same_seed()
    {
        var a = TestSupport.NewPopulatedSession(42);
        var b = TestSupport.NewPopulatedSession(42);
        var ca = a.Crime.GenerateIncident("murder");
        var cb = b.Crime.GenerateIncident("murder");
        Assert.Equal(ca.VictimId, cb.VictimId);
        Assert.Equal(ca.LocationId, cb.LocationId);
        Assert.SequenceEqual(
            a.Crime.EvidenceAtScene(ca.SceneId).Select(e => e.Authenticity),
            b.Crime.EvidenceAtScene(cb.SceneId).Select(e => e.Authenticity));
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void Different_seeds_produce_variation()
    {
        var hashes = new HashSet<string>();
        for (ulong seed = 42; seed < 47; seed++)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            s.Crime.GenerateIncident("murder");
            hashes.Add(s.StateHash());
        }
        Assert.True(hashes.Count > 1, "expected incident variation across seeds");
    }

    [Fact]
    public void Victim_is_never_the_player_character()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            var crime = s.Crime.GenerateIncident("murder");
            Assert.NotEqual(s.State.Player.CharacterId, crime.VictimId);
            var victim = s.State.World.Characters[crime.VictimId];
            Assert.Equal(CharacterKind.Civilian, victim.Kind);
        }
    }

    [Fact]
    public void Murder_kills_the_victim_and_seals_the_location()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        Assert.True(crime.Fatal);
        Assert.False(s.State.World.Characters[crime.VictimId].IsAlive);
        Assert.True(s.State.World.Locations[crime.LocationId].IsSealed);
    }

    [Fact]
    public void Arson_victim_survives_and_stays_in_play()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("arson");
        Assert.False(crime.Fatal);
        Assert.True(s.State.World.Characters[crime.VictimId].IsAlive);
        Assert.True(s.State.World.Locations[crime.LocationId].IsSealed);
    }

    [Fact]
    public void Incident_is_logged_with_causal_data()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        var incidents = s.Events.Query("crime.incident").ToList();
        Assert.Equal(1, incidents.Count);
        var evt = incidents[0];
        Assert.Equal(crime.IncidentEventId, evt.Id);
        Assert.True(evt.Participants.Contains(crime.VictimId));
        Assert.Equal("murder", evt.Data["definition"]);
        Assert.Equal(crime.Id, evt.Data["crime"]);
        Assert.Equal(crime.LocationId, evt.LocationId);
    }

    [Fact]
    public void Evidence_spawns_from_the_definition_templates()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId);
        var templates = s.Content.GetCrime("murder").EvidenceTemplates;
        Assert.Equal(templates.Count, evidence.Count);
        Assert.SequenceEqual(templates.Select(t => t.Label), evidence.Select(e => e.Label));
        Assert.True(evidence.All(e => !e.Discovered));
    }

    [Fact]
    public void Crime_draws_do_not_shift_the_sim_rng()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var s0 = s.State.SimRng.S0;
        var s1 = s.State.SimRng.S1;
        var s2 = s.State.SimRng.S2;
        var s3 = s.State.SimRng.S3;
        s.Crime.GenerateIncident("murder");
        s.Crime.GenerateIncident("arson");
        Assert.Equal(s0, s.State.SimRng.S0);
        Assert.Equal(s1, s.State.SimRng.S1);
        Assert.Equal(s2, s.State.SimRng.S2);
        Assert.Equal(s3, s.State.SimRng.S3);
    }

    [Fact]
    public void Hidden_roles_do_not_change_the_incident()
    {
        // Same seed, different secret role assignments: the incident must be identical,
        // because victim selection never reads WorldTruth.
        var a = TestSupport.NewPopulatedSession(42, Campaign.Lumiel); // player Lumiel, rival Malvr
        var b = TestSupport.NewPopulatedSession(42, Campaign.Malvr);  // player Malvr, rival Lumiel
        var ca = a.Crime.GenerateIncident("murder");
        var cb = b.Crime.GenerateIncident("murder");
        Assert.Equal(ca.VictimId, cb.VictimId);
        Assert.Equal(ca.LocationId, cb.LocationId);
        Assert.SequenceEqual(
            a.Crime.EvidenceAtScene(ca.SceneId).Select(e => e.Authenticity),
            b.Crime.EvidenceAtScene(cb.SceneId).Select(e => e.Authenticity));
    }

    [Fact]
    public void GenerateIncident_throws_when_no_eligible_victim_exists()
    {
        var s = GameSession.NewRun(9, Campaign.Lumiel, Difficulty.Medium, TestSupport.LoadContent());
        s.World.AddCharacter(new CharacterState
        {
            Id = "c_player", DisplayName = "The Investigator", Age = 34, OccupationId = "consultant",
            Kind = CharacterKind.Civilian, HomeLocationId = "loc_residential", CurrentLocationId = "loc_residential",
        });
        s.World.SetPlayerCharacter("c_player");
        Assert.Throws<InvalidOperationException>(() => s.Crime.GenerateIncident("murder"));
    }

    // ------------------------------------------------------------ discovery

    private static (GameSession Session, CrimeRecord Crime, string MoverId, string OutsiderId) SetupDiscovery(ulong seed = 42)
    {
        var s = TestSupport.NewPopulatedSession(seed);
        var crime = s.Crime.GenerateIncident("murder");
        var loc = crime.LocationId;
        var alive = s.State.World.Characters.Values
            .Where(c => c.IsAlive)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var away = alive.Where(c => c.CurrentLocationId != loc).Select(c => c.Id).ToList();
        // The mover walks into the scene; the outsider stays away from it the whole time.
        // When only one character is away, the mover is already at the scene (a same-location
        // move still fires the arrival bus) and the lone outsider stays put.
        string mover, outsider;
        if (away.Count >= 2) { mover = away[0]; outsider = away[1]; }
        else { mover = alive.First(c => c.CurrentLocationId == loc).Id; outsider = away[0]; }
        return (s, crime, mover, outsider);
    }

    [Fact]
    public void Scene_starts_undiscovered()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        var scene = s.Crime.GetScene(crime.SceneId);
        Assert.False(scene.IsDiscovered);
        Assert.True(scene.DiscoveredBy is null);
    }

    [Fact]
    public void Discovery_requires_the_definition_delay()
    {
        var (s, crime, mover, _) = SetupDiscovery();
        s.World.MoveCharacter(mover, crime.LocationId); // t=360, delay is 120 -> too early
        Assert.False(s.Crime.GetScene(crime.SceneId).IsDiscovered);
        Assert.Equal(0, s.Crime.CheckDiscovery(mover).Count);

        s.Time.Advance(200); // t=560, past the delay
        var found = s.Crime.CheckDiscovery(mover);
        Assert.Equal(1, found.Count);
        Assert.True(s.Crime.GetScene(crime.SceneId).IsDiscovered);
    }

    [Fact]
    public void Arrival_discovers_the_scene_and_witnesses_it()
    {
        var (s, crime, mover, _) = SetupDiscovery();
        s.Time.Advance(200); // past the 120-minute murder delay
        s.World.MoveCharacter(mover, crime.LocationId); // bus -> CheckDiscovery

        var scene = s.Crime.GetScene(crime.SceneId);
        Assert.True(scene.IsDiscovered);
        Assert.Equal(mover, scene.DiscoveredBy);

        var discoveries = s.Events.Query("crime.discovered").ToList();
        Assert.Equal(1, discoveries.Count);
        Assert.Equal(crime.IncidentEventId, discoveries[0].CausedBy);
        Assert.Equal(mover, discoveries[0].Data["discoverer"]);
        Assert.Equal(scene.DiscoveryEventId, discoveries[0].Id);

        // The discoverer now knows the incident through the Phase 4 gate.
        Assert.True(s.Cognition.Knows(mover, crime.IncidentEventId));
    }

    [Fact]
    public void Second_arrival_does_not_rediscover()
    {
        var (s, crime, mover, outsider) = SetupDiscovery();
        s.Time.Advance(200);
        s.World.MoveCharacter(mover, crime.LocationId);
        s.World.MoveCharacter(outsider, crime.LocationId);
        Assert.Equal(1, s.Events.Query("crime.discovered").Count());
    }

    [Fact]
    public void Dead_characters_cannot_discover()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        Assert.Throws<InvalidOperationException>(() => s.Crime.CheckDiscovery(crime.VictimId));
    }

    [Fact]
    public void Witnesses_are_exactly_the_perceivers()
    {
        var (s, crime, mover, outsider) = SetupDiscovery();
        s.Time.Advance(200);
        s.World.MoveCharacter(mover, crime.LocationId);

        var witnesses = s.Crime.GetWitnesses(crime.Id);
        Assert.True(witnesses.Contains(mover), "the discoverer is a witness");
        Assert.False(witnesses.Contains(crime.VictimId), "the dead victim is not a witness");
        Assert.False(witnesses.Contains(outsider), "a character across town is not a witness");
        Assert.True(witnesses.All(id => s.State.World.Characters[id].IsAlive));
    }

    // ------------------------------------------------------------ evidence discovery

    [Fact]
    public void Evidence_discovery_requires_knowledge_of_the_incident()
    {
        var (s, crime, mover, _) = SetupDiscovery();
        // Phase 9 cordon: the late arrival must be someone the cordon admits —
        // a police officer, or the player with the consultant's pass.
        var lateId = new[] { "c_guard", "c_player" }.First(id => id != mover);
        if (s.World.GetCharacter(lateId).CurrentLocationId == crime.LocationId)
        {
            var elsewhere = s.State.World.Locations.Keys
                .Where(l => l != crime.LocationId).OrderBy(l => l, StringComparer.Ordinal).First();
            s.World.MoveCharacter(lateId, elsewhere); // pre-discovery: no cordon yet
        }
        s.Time.Advance(200);
        s.World.MoveCharacter(mover, crime.LocationId); // discovers the scene
        var arrived = s.World.MoveCharacter(lateId, crime.LocationId); // cordon admits police / player
        Assert.True(arrived is not null, "the cordon should admit police and the player");

        var evidence = s.Crime.EvidenceAtScene(crime.SceneId)[0];
        // The late arrival never perceived the incident, so investigation is refused.
        Assert.Throws<InvalidOperationException>(() => s.Crime.DiscoverEvidence(lateId, evidence.Id));

        // Once told about it through the rumor gate, they can investigate.
        s.Cognition.TellRumor(mover, lateId, crime.IncidentEventId);
        var logged = s.Crime.DiscoverEvidence(lateId, evidence.Id);
        Assert.Equal("crime.evidence_discovered", logged.Type);
    }

    [Fact]
    public void Discovering_evidence_marks_it_and_links_the_chain()
    {
        var (s, crime, mover, _) = SetupDiscovery();
        s.Time.Advance(200);
        s.World.MoveCharacter(mover, crime.LocationId);
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId)[0];

        var logged = s.Crime.DiscoverEvidence(mover, evidence.Id);

        var updated = s.Crime.GetEvidence(evidence.Id);
        Assert.True(updated.Discovered);
        Assert.Equal(mover, updated.DiscoveredBy);
        var scene = s.Crime.GetScene(crime.SceneId);
        Assert.Equal(scene.DiscoveryEventId, logged.CausedBy);
        Assert.Equal(evidence.Authenticity, updated.Authenticity);
        Assert.True(s.Cognition.Knows(mover, logged.Id));
    }

    [Fact]
    public void Discovering_unknown_or_already_found_evidence_throws()
    {
        var (s, crime, mover, _) = SetupDiscovery();
        s.Time.Advance(200);
        s.World.MoveCharacter(mover, crime.LocationId);
        Assert.Throws<KeyNotFoundException>(() => s.Crime.DiscoverEvidence(mover, "ev_999"));
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId)[0];
        s.Crime.DiscoverEvidence(mover, evidence.Id);
        Assert.Throws<InvalidOperationException>(() => s.Crime.DiscoverEvidence(mover, evidence.Id));
    }

    // ------------------------------------------------------------ authenticity immutability

    [Fact]
    public void Authenticity_has_no_setter()
    {
        var setter = typeof(Evidence).GetProperty(nameof(Evidence.Authenticity))?.SetMethod;
        Assert.True(setter is null, "Authenticity must have no setter");
    }

    [Fact]
    public void False_evidence_never_becomes_true()
    {
        // Find a seed that actually draws a false evidence item (deterministic loop).
        GameSession? found = null;
        CrimeRecord? foundCrime = null;
        string? falseId = null;
        for (ulong seed = 1; seed <= 200 && found is null; seed++)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            var crime = s.Crime.GenerateIncident("murder");
            var item = s.Crime.EvidenceAtScene(crime.SceneId)
                .FirstOrDefault(e => e.Authenticity == Authenticity.False);
            if (item is not null) { found = s; foundCrime = crime; falseId = item.Id; }
        }
        Assert.True(found is not null, "test setup: expected a false evidence item within 200 seeds");

        var session = found!;
        var before = session.Crime.EvidenceAtScene(foundCrime!.SceneId)
            .ToDictionary(e => e.Id, e => e.Authenticity);

        // Run every public mutator over the evidence: discovery, rumors, time passing.
        session.Time.Advance(200);
        var mover = session.State.World.Characters.Values
            .Where(c => c.IsAlive && c.CurrentLocationId != foundCrime.LocationId)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).First();
        session.World.MoveCharacter(mover, foundCrime.LocationId);
        foreach (var e in session.Crime.EvidenceAtScene(foundCrime.SceneId))
            session.Crime.DiscoverEvidence(mover, e.Id);
        session.Time.Advance(20000); // memory decay must not touch authenticity either

        foreach (var e in session.Crime.EvidenceAtScene(foundCrime.SceneId))
            Assert.Equal(before[e.Id], e.Authenticity);
        Assert.Equal(Authenticity.False, session.Crime.GetEvidence(falseId!).Authenticity);
    }

    [Fact]
    public void Validator_rejects_tampered_authenticity()
    {
        // A hand-edited save claiming an unknown authenticity value must fail loudly.
        GameSession? session = null;
        for (ulong seed = 1; seed <= 200 && session is null; seed++)
        {
            var s = TestSupport.NewPopulatedSession(seed);
            s.Crime.GenerateIncident("murder");
            if (s.Crime.EvidenceAtScene(s.Crime.AllCrimes()[0].SceneId).Any(e => e.Authenticity == Authenticity.False))
                session = s;
        }
        Assert.True(session is not null, "test setup");
        var json = SaveSystem.Serialize(session!.State);
        Assert.Contains("\"Authenticity\":\"False\"", json);
        var tampered = json.Replace("\"Authenticity\":\"False\"", "\"Authenticity\":\"Bogus\"");
        Assert.NotEqual(json, tampered);
        Assert.Throws<SaveFormatException>(() => SaveSystem.Deserialize(tampered));
    }

    [Fact]
    public void Validator_rejects_crime_with_unknown_victim()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        crime.VictimId = "c_nobody";
        var errors = GameStateValidator.Validate(s.State);
        Assert.True(errors.Any(e => e.Contains("unknown victim")), "expected an unknown-victim error, got: " + string.Join("; ", errors));
    }

    // ------------------------------------------------------------ timeline

    [Fact]
    public void Timeline_orders_the_incident_chain()
    {
        var (s, crime, mover, _) = SetupDiscovery();
        s.Time.Advance(200);
        s.World.MoveCharacter(mover, crime.LocationId);
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId)[0];
        var evidenceLogged = s.Crime.DiscoverEvidence(mover, evidence.Id);

        var timeline = s.Crime.GetTimeline(crime.Id);
        Assert.SequenceEqual(
            new[] { "crime.incident", "crime.discovered", "crime.evidence_discovered" },
            timeline.Select(e => e.Type));

        // The causal chain of the last event reproduces the timeline root-first.
        var chain = s.Events.CausalChain(evidenceLogged.Id);
        Assert.SequenceEqual(timeline.Select(e => e.Id), chain.Select(e => e.Id));
    }

    // ------------------------------------------------------------ persistence

    [Fact]
    public void Save_load_is_hash_identical_mid_investigation()
    {
        var s = TestSupport.NewPopulatedSession(42);
        var crime = s.Crime.GenerateIncident("murder");
        s.Time.Advance(200);
        var mover = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.CurrentLocationId != crime.LocationId)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).First();
        s.World.MoveCharacter(mover, crime.LocationId);
        s.Crime.DiscoverEvidence(mover, s.Crime.EvidenceAtScene(crime.SceneId)[0].Id);

        var path = Path.Combine(Path.GetTempPath(), "bd-crime-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            s.Save(path);
            var loaded = GameSession.Load(path, TestSupport.LoadContent());
            Assert.Equal(s.StateHash(), loaded.StateHash());

            s.Simulate.Advance(120);
            loaded.Simulate.Advance(120);
            Assert.Equal(s.StateHash(), loaded.StateHash());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void V5_saves_migrate_to_v6()
    {
        var s = TestSupport.NewPopulatedSession(5);
        var json = SaveSystem.Serialize(s.State);
        var root = JsonNode.Parse(json)!.AsObject();
        root["State"]!.AsObject().Remove("Crime");
        root["FormatVersion"] = 5;
        var migrated = SaveSystem.Deserialize(root.ToJsonString());
        Assert.Equal(0, migrated.Crime.Crimes.Count);
        Assert.Equal(0, migrated.Crime.Scenes.Count);
        Assert.Equal(0, migrated.Crime.Evidence.Count);
        Assert.False(migrated.Crime.CrimeRng.IsZero);
        Assert.Equal(0, GameStateValidator.Validate(migrated).Count);
    }
}
