using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Police;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class PoliceTests
{
    // ------------------------------------------------------------ helpers

    private static GameSession NewPoliceSession(ulong seed = 42)
    {
        var s = TestSupport.NewPopulatedSession(seed);
        AddOfficer(s, "c_guard2", "Second Guard", 31);
        AddOfficer(s, "c_guard3", "Third Guard", 44);
        return s;
    }

    private static void AddOfficer(GameSession s, string id, string name, int age) =>
        s.World.AddCharacter(new CharacterState
        {
            Id = id, DisplayName = name, Age = age, OccupationId = "guard",
            Kind = CharacterKind.Police, HomeLocationId = "loc_residential",
            WorkLocationId = "loc_guard_station", CurrentLocationId = "loc_residential",
        });

    /// <summary>A murder, discovered by a civilian after the delay. Returns session, crime, discoverer.</summary>
    private static (GameSession S, CrimeRecord Crime, string Discoverer) SetupDiscoveredCrime(ulong seed = 42)
    {
        var s = NewPoliceSession(seed);
        var crime = s.Crime.GenerateIncident("murder");
        s.Time.Advance(200); // past the 120-minute discovery delay
        var discoverer = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId
                        && c.CurrentLocationId != crime.LocationId)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .First();
        var moved = s.World.MoveCharacter(discoverer, crime.LocationId);
        Assert.True(moved is not null, "pre-discovery arrival must be allowed");
        return (s, crime, discoverer);
    }

    /// <summary>
    /// A fresh (undiscovered) murder. Officers are brought up to speed identically:
    /// the first arrival discovers, the rest learn by rumor on the spot, then the first
    /// discovers every evidence item while all are present — so all listed officers know
    /// the incident and every evidence discovery event.
    /// </summary>
    private static (GameSession S, CrimeRecord Crime) SetupFreshCrime(ulong seed = 42)
    {
        var s = NewPoliceSession(seed);
        return (s, s.Crime.GenerateIncident("murder"));
    }

    private static void BringOfficersUpToSpeed(GameSession s, CrimeRecord crime, params string[] officers)
    {
        s.Time.Advance(200); // past the 120-minute discovery delay
        foreach (var o in officers)
            Assert.True(s.World.MoveCharacter(o, crime.LocationId) is not null, $"{o} must reach the scene");
        var first = officers[0];
        foreach (var o in officers.Skip(1))
            s.Cognition.TellRumor(first, o, crime.IncidentEventId);
        foreach (var e in s.Crime.EvidenceAtScene(crime.SceneId))
            s.Crime.DiscoverEvidence(first, e.Id);
    }

    // ------------------------------------------------------------ cordon

    [Fact]
    public void Civilian_denied_entry_to_discovered_sealed_scene()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        var civilian = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Kind != CharacterKind.Police
                        && c.Id != s.State.Player.CharacterId
                        && c.CurrentLocationId != crime.LocationId)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).First();

        var before = s.State.World.Characters[civilian].CurrentLocationId;
        var result = s.World.MoveCharacter(civilian, crime.LocationId);

        Assert.True(result is null, "denied move returns null");
        Assert.Equal(before, s.World.GetCharacter(civilian).CurrentLocationId);
        Assert.Equal(1, s.Events.Query(WorldEventTypes.PoliceAccessDenied).Count());
    }

    [Fact]
    public void Police_officer_may_enter_cordoned_scene()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        var moved = s.World.MoveCharacter("c_guard2", crime.LocationId);
        Assert.True(moved is not null, "police pass the cordon");
        Assert.Equal(crime.LocationId, s.World.GetCharacter("c_guard2").CurrentLocationId);
    }

    [Fact]
    public void Player_may_enter_cordoned_scene_with_consultant_pass()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        var player = s.State.Player.CharacterId;
        if (s.World.GetCharacter(player).CurrentLocationId == crime.LocationId)
            return; // already there: nothing to prove
        var moved = s.World.MoveCharacter(player, crime.LocationId);
        Assert.True(moved is not null, "the player holds a consultant's pass");
    }

    [Fact]
    public void Undiscovered_scene_has_no_cordon()
    {
        var s = NewPoliceSession();
        var crime = s.Crime.GenerateIncident("murder");
        s.Time.Advance(200); // past the discovery delay, so arrival would discover
        var civilian = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Kind != CharacterKind.Police && c.CurrentLocationId != crime.LocationId)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).First();
        var moved = s.World.MoveCharacter(civilian, crime.LocationId);
        Assert.True(moved is not null, "pre-discovery arrival must be allowed so discovery can happen");
        Assert.Equal(1, s.Events.Query(WorldEventTypes.CrimeDiscovered).Count());
    }

    // ------------------------------------------------------------ duties

    [Fact]
    public void AssignDuties_guards_sealed_scenes()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        s.Police.AssignDuties();

        var guards = s.State.Police.Duties
            .Where(kv => kv.Value.Any(d => d.Kind == DutyKind.Guard))
            .ToList();
        Assert.True(guards.Count >= 1, "at least one officer guards the sealed scene");
        Assert.True(guards.All(kv => kv.Value[0].LocationId == crime.LocationId));
        Assert.True(guards.All(kv => kv.Value[0].SceneId == crime.SceneId));
        Assert.Equal(s.Police.Officers().Count, s.Events.Query(WorldEventTypes.PoliceDutyAssigned).Count());
    }

    [Fact]
    public void AssignDuties_is_idempotent_per_day()
    {
        var (s, _, _) = SetupDiscoveredCrime();
        s.Police.AssignDuties();
        var events = s.Events.Query(WorldEventTypes.PoliceDutyAssigned).Count();
        s.Police.AssignDuties();
        Assert.Equal(events, s.Events.Query(WorldEventTypes.PoliceDutyAssigned).Count());
    }

    [Fact]
    public void DutyLocationFor_feeds_the_sim_seam()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        Assert.True(s.Police.DutyLocationFor("c_guard", 1) is null, "no roster yet");
        s.Police.AssignDuties();
        var day = new GameTime(s.State.TotalMinutes).Day;
        var guard = s.State.Police.Duties.First(kv => kv.Value[0].Kind == DutyKind.Guard).Key;
        Assert.Equal(crime.LocationId, s.Police.DutyLocationFor(guard, day));
        Assert.True(s.Police.DutyLocationFor(guard, day + 1) is null, "roster is per-day");
    }

    [Fact]
    public void Patrol_slots_scale_with_alert()
    {
        var (s, _, _) = SetupDiscoveredCrime();
        s.State.Police.Alert = AlertLevel.Manhunt;
        s.Police.AssignDuties();
        var patrols = s.State.Police.Duties.Count(kv => kv.Value[0].Kind == DutyKind.Patrol);
        var officers = s.Police.Officers().Count;
        Assert.True(patrols >= 2, $"manhunt should flood the streets, got {patrols} patrols of {officers} officers");
    }

    // ------------------------------------------------------------ divergence

    [Fact]
    public void ReadingOf_is_deterministic_and_stream_safe()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId)[0].Id;
        var subject = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Kind != CharacterKind.Police).Id;

        var hashBefore = s.StateHash();
        var first = s.Police.ReadingOf("c_guard", evidence, subject);
        var second = s.Police.ReadingOf("c_guard", evidence, subject);
        Assert.Equal(first, second);
        Assert.Equal(hashBefore, s.StateHash());
    }

    [Fact]
    public void Officers_diverge_on_identical_evidence()
    {
        var (s, crime) = SetupFreshCrime();
        // Both officers learn everything: identical inputs.
        BringOfficersUpToSpeed(s, crime, "c_guard", "c_guard2");
        s.Police.OpenCase("c_guard", crime.Id);
        s.Police.OpenCase("c_guard2", crime.Id);
        s.Police.AssessEvidence("c_guard", crime.Id);
        s.Police.AssessEvidence("c_guard2", crime.Id);

        var confA = s.Police.OfficerHypotheses("c_guard").Select(h => h.Confidence).ToList();
        var confB = s.Police.OfficerHypotheses("c_guard2").Select(h => h.Confidence).ToList();
        Assert.Equal(confA.Count, confB.Count);
        Assert.False(confA.SequenceEqual(confB), "independent officers must diverge from identical inputs");
    }

    [Fact]
    public void Divergence_is_seed_deterministic()
    {
        static List<int> Run(ulong seed)
        {
            var (s, crime) = SetupFreshCrime(seed);
            BringOfficersUpToSpeed(s, crime, "c_guard");
            s.Police.OpenCase("c_guard", crime.Id);
            s.Police.AssessEvidence("c_guard", crime.Id);
            return s.Police.OfficerHypotheses("c_guard").Select(h => h.Confidence).ToList();
        }
        Assert.True(Run(42).SequenceEqual(Run(42)), "same seed, same case file");
    }

    [Fact]
    public void Officer_without_knowledge_cannot_open_case()
    {
        var (s, crime, _) = SetupDiscoveredCrime();
        // c_guard3 never perceived the incident and was never told.
        Assert.Throws<InvalidOperationException>(() => s.Police.OpenCase("c_guard3", crime.Id));
    }

    [Fact]
    public void Knowledge_gap_means_thinner_case_file()
    {
        var (s, crime) = SetupFreshCrime();
        BringOfficersUpToSpeed(s, crime, "c_guard");
        s.Police.OpenCase("c_guard", crime.Id);
        s.Police.AssessEvidence("c_guard", crime.Id);

        // c_guard2 learns of the incident by rumor at the scene but never sees the evidence.
        Assert.True(s.World.MoveCharacter("c_guard2", crime.LocationId) is not null, "police pass the cordon");
        s.Cognition.TellRumor("c_guard", "c_guard2", crime.IncidentEventId);
        s.Police.OpenCase("c_guard2", crime.Id);
        s.Police.AssessEvidence("c_guard2", crime.Id);

        var withEvidence = s.Police.OfficerHypotheses("c_guard").Sum(h => h.SupportingEvidenceIds.Count + h.RefutingEvidenceIds.Count);
        var withoutEvidence = s.Police.OfficerHypotheses("c_guard2").Sum(h => h.SupportingEvidenceIds.Count + h.RefutingEvidenceIds.Count);
        Assert.True(withEvidence > 0, "the knowing officer attaches evidence");
        Assert.Equal(0, withoutEvidence);
    }

    // ------------------------------------------------------------ trust ladder

    [Fact]
    public void EvaluateSubject_escalates_stance_with_mounting_evidence()
    {
        var (s, crime) = SetupFreshCrime();
        var subject = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId).Id;
        s.Social.SeedPoliceTrust(subject, 30); // IndependentVerification
        Assert.Equal(PoliceStance.IndependentVerification, s.Social.StanceToward("c_guard", subject));

        BringOfficersUpToSpeed(s, crime, "c_guard");
        s.Police.OpenCase("c_guard", crime.Id);
        var hyp = s.Police.OfficerHypotheses("c_guard")
            .First(h => h.PropositionId == PoliceService.PropositionFor("c_guard", subject, crime.Id));
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId).Take(2).ToList();
        foreach (var e in evidence)
            s.Investigate.AttachEvidence(hyp.Id, e.Id, supports: true); // 2 for -> Believes (66)

        var stance = s.Police.EvaluateSubject("c_guard", subject);
        Assert.Equal(22, s.Social.View("c_guard", subject).Trust);
        Assert.Equal(PoliceStance.SuspicionOfSubject, stance);
    }

    [Fact]
    public void EvaluateSubject_exonerates_on_refuting_evidence()
    {
        var (s, crime) = SetupFreshCrime();
        var subject = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId).Id;
        s.Social.SeedPoliceTrust(subject, 40);
        BringOfficersUpToSpeed(s, crime, "c_guard");
        s.Police.OpenCase("c_guard", crime.Id);
        var hyp = s.Police.OfficerHypotheses("c_guard")
            .First(h => h.PropositionId == PoliceService.PropositionFor("c_guard", subject, crime.Id));
        foreach (var e in s.Crime.EvidenceAtScene(crime.SceneId).Take(2))
            s.Investigate.AttachEvidence(hyp.Id, e.Id, supports: false); // 2 against -> Dismissed (34)

        s.Police.EvaluateSubject("c_guard", subject);
        Assert.Equal(45, s.Social.View("c_guard", subject).Trust);
    }

    [Fact]
    public void Manhunt_doubles_evaluation_nudges()
    {
        var (s, crime) = SetupFreshCrime();
        var subject = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId).Id;
        s.Social.SeedPoliceTrust(subject, 90);
        BringOfficersUpToSpeed(s, crime, "c_guard");
        s.Police.OpenCase("c_guard", crime.Id);
        var hyp = s.Police.OfficerHypotheses("c_guard")
            .First(h => h.PropositionId == PoliceService.PropositionFor("c_guard", subject, crime.Id));
        foreach (var e in s.Crime.EvidenceAtScene(crime.SceneId).Take(2))
            s.Investigate.AttachEvidence(hyp.Id, e.Id, supports: true);

        s.State.Police.Alert = AlertLevel.Manhunt;
        s.Police.EvaluateSubject("c_guard", subject);
        Assert.Equal(90 - 16, s.Social.View("c_guard", subject).Trust);
    }

    // ------------------------------------------------------------ alert ladder

    [Fact]
    public void Stale_fatal_incident_raises_alert()
    {
        var s = NewPoliceSession();
        s.Crime.GenerateIncident("murder");
        Assert.Equal(AlertLevel.Calm, s.State.Police.Alert);
        s.Time.Advance(1200); // past midnight: DayChanged re-evaluates; 1200 > 720 undiscovered
        Assert.Equal(AlertLevel.Alert, s.State.Police.Alert);
        Assert.Equal(1, s.Events.Query(WorldEventTypes.PoliceAlertChanged).Count());
    }

    [Fact]
    public void Second_open_crime_escalates_to_alert()
    {
        var s = NewPoliceSession();
        s.Crime.GenerateIncident("murder");
        s.Crime.GenerateIncident("arson");
        Assert.Equal(AlertLevel.Alert, s.State.Police.Alert);
    }

    [Fact]
    public void Piling_crimes_reach_manhunt_stepwise()
    {
        var s = NewPoliceSession();
        s.Crime.GenerateIncident("murder");
        s.Crime.GenerateIncident("murder");
        Assert.Equal(AlertLevel.Alert, s.State.Police.Alert);
        s.Crime.GenerateIncident("murder");
        Assert.Equal(AlertLevel.Manhunt, s.State.Police.Alert);
    }

    [Fact]
    public void Closing_all_cases_steps_alert_down()
    {
        var s = NewPoliceSession();
        s.Crime.GenerateIncident("murder");
        s.Crime.GenerateIncident("murder");
        Assert.Equal(AlertLevel.Alert, s.State.Police.Alert);
        foreach (var crime in s.Crime.AllCrimes())
            s.State.Police.Cases[crime.Id] = new CaseStatus { State = CaseState.InCustody, SubjectId = "c_rival" };
        Assert.Equal(AlertLevel.Calm, s.Police.EvaluateAlert());
    }

    // ------------------------------------------------------------ arrest

    private static (GameSession S, CrimeRecord Crime, string Officer, string Subject) SetupArrestable(ulong seed = 42)
    {
        var (s, crime) = SetupFreshCrime(seed);
        BringOfficersUpToSpeed(s, crime, "c_guard");
        var officer = "c_guard";
        var subject = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId).Id;
        s.Social.SeedPoliceTrust(subject, 10); // SuspicionOfSubject
        s.Police.OpenCase(officer, crime.Id);
        var hyp = s.Police.OfficerHypotheses(officer)
            .First(h => h.PropositionId == PoliceService.PropositionFor(officer, subject, crime.Id));
        var evidence = s.Crime.EvidenceAtScene(crime.SceneId).Take(2).ToList();
        Assert.True(evidence.Count >= 2, "test needs at least 2 evidence items");
        foreach (var e in evidence)
            s.Investigate.AttachEvidence(hyp.Id, e.Id, supports: true); // Believes (66)
        return (s, crime, officer, subject);
    }

    [Fact]
    public void Arrest_succeeds_with_stance_belief_and_evidence()
    {
        var (s, crime, officer, subject) = SetupArrestable();
        var logged = s.Police.Arrest(officer, subject, crime.Id);

        Assert.Equal(WorldEventTypes.PoliceArrest, logged.Type);
        Assert.Equal(CaseState.InCustody, s.Police.GetCaseStatus(crime.Id).State);
        Assert.Equal(subject, s.Police.GetCaseStatus(crime.Id).SubjectId);
        Assert.True(s.Police.IsInCustody(subject));
    }

    [Fact]
    public void Arrest_fails_without_suspect_stance()
    {
        var (s, crime, officer, subject) = SetupArrestable();
        s.Social.SeedPoliceTrust(subject, 90); // back to Cooperation
        Assert.Throws<InvalidOperationException>(() => s.Police.Arrest(officer, subject, crime.Id));
        Assert.False(s.Police.IsInCustody(subject));
    }

    [Fact]
    public void Arrest_fails_without_belief()
    {
        var (s, crime) = SetupFreshCrime();
        BringOfficersUpToSpeed(s, crime, "c_guard");
        var subject = s.State.World.Characters.Values
            .First(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId).Id;
        s.Social.SeedPoliceTrust(subject, 10);
        s.Police.OpenCase("c_guard", crime.Id); // hypotheses at neutral 50, no evidence
        Assert.Throws<InvalidOperationException>(() => s.Police.Arrest("c_guard", subject, crime.Id));
    }

    [Fact]
    public void Arrest_fails_when_already_in_custody()
    {
        var (s, crime, officer, subject) = SetupArrestable();
        s.Police.Arrest(officer, subject, crime.Id);
        Assert.Throws<InvalidOperationException>(() => s.Police.Arrest(officer, subject, crime.Id));
    }

    [Fact]
    public void Detainee_is_skipped_by_the_sim()
    {
        var (s, crime, officer, subject) = SetupArrestable();
        var before = s.World.GetCharacter(subject).CurrentLocationId;
        s.Police.Arrest(officer, subject, crime.Id);
        s.Simulate.Advance(1440);
        Assert.Equal(before, s.World.GetCharacter(subject).CurrentLocationId);
    }

    // ------------------------------------------------------------ persistence

    [Fact]
    public void Save_load_roundtrip_is_hash_identical_with_police_state()
    {
        var (s, crime, officer, subject) = SetupArrestable();
        s.Police.AssignDuties();
        s.Police.EvaluateSubject(officer, subject);

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bd-police-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            s.Save(path);
            var loaded = GameSession.Load(path, TestSupport.LoadContent());
            Assert.Equal(s.StateHash(), loaded.StateHash());

            s.Simulate.Advance(500);
            loaded.Simulate.Advance(500);
            Assert.Equal(s.StateHash(), loaded.StateHash());
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void Migration_v7_to_v8_initializes_police_state()
    {
        var s = NewPoliceSession();
        var json = SaveSystem.Serialize(s.State);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["State"]!.AsObject().Remove("Police");
        node["FormatVersion"] = 7;
        Assert.False(node["State"]!.AsObject().ContainsKey("Police"), "test setup: v7 JSON should not have Police");
        var v7 = node.ToJsonString();

        var migrated = SaveSystem.Deserialize(v7);
        Assert.Equal(AlertLevel.Calm, migrated.Police!.Alert);
        Assert.Equal(0, migrated.Police!.DutiesDay);
        Assert.Equal(0, GameStateValidator.Validate(migrated).Count);
    }

    [Fact]
    public void Validator_rejects_broken_police_state()
    {
        var s = NewPoliceSession();
        s.State.Police.Duties["c_merchant"] = new System.Collections.Generic.List<DutyAssignment>
        {
            new() { OfficerId = "c_merchant", Day = 1, Kind = DutyKind.Patrol, LocationId = "loc_tavern" },
        };
        s.State.Police.Cases["crime_nope"] = new CaseStatus { State = CaseState.Open };
        var errors = GameStateValidator.Validate(s.State);
        Assert.True(errors.Count >= 2, "expected duty-owner and unknown-crime errors");
    }

    // ------------------------------------------------------------ hygiene

    [Fact]
    public void Police_namespace_never_touches_truth_types()
    {
        var asm = typeof(PoliceService).Assembly;
        var banned = new[] { "WorldTruth", "DebugAccess" };
        foreach (var type in asm.GetTypes().Where(t => t.Namespace == "BadDeduction.Police"))
        {
            foreach (var member in type.GetMembers(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                         System.Reflection.BindingFlags.DeclaredOnly))
            {
                var memberTypes = member switch
                {
                    System.Reflection.FieldInfo f => new[] { f.FieldType },
                    System.Reflection.PropertyInfo p => new[] { p.PropertyType },
                    System.Reflection.MethodInfo m => m.GetParameters().Select(pp => pp.ParameterType)
                        .Append(m.ReturnType),
                    System.Reflection.ConstructorInfo c => c.GetParameters().Select(pp => pp.ParameterType),
                    _ => Enumerable.Empty<Type>(),
                };
                foreach (var t in memberTypes)
                    Assert.False(banned.Contains(t.Name),
                        $"{type.Name}.{member.Name} touches truth type {t.Name}");
            }
        }
    }
}
