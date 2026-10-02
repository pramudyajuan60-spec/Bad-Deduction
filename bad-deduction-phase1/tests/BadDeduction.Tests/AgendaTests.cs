using BadDeduction.Agenda;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Investigation;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;
using BadDeduction.World;

namespace BadDeduction.Tests;

/// <summary>Phase 10: seeded hidden identities, hidden objectives, the strategic tick and deception.</summary>
public sealed class AgendaTests
{
    private static string NpcGenius(GameSession s) =>
        s.State.Truth.HiddenRoles.First(kv => kv.Key != s.State.Player.CharacterId).Key;

    private static HiddenRole NpcGeniusRole(GameSession s) =>
        s.State.Truth.HiddenRoles[NpcGenius(s)];

    private static HiddenObjective InjectObjective(GameSession s, string holderId, ObjectiveKind kind,
        string? targetId = null, string? targetId2 = null, string? crimeId = null)
    {
        var agenda = s.State.Agenda;
        var obj = new HiddenObjective
        {
            Id = $"obj_{agenda.NextObjectiveId++}",
            HolderId = holderId,
            Kind = kind,
            TargetId = targetId,
            TargetId2 = targetId2,
            CrimeId = crimeId,
            CreatedAt = s.State.TotalMinutes,
        };
        agenda.Objectives.Add(obj.Id, obj);
        return obj;
    }

    // ------------------------------------------------------------------ role assignment

    [Fact]
    public void Player_always_holds_their_campaign_role()
    {
        var lumiel = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        Assert.Equal(HiddenRole.Lumiel, lumiel.State.Truth.HiddenRoles["c_player"]);
        var malvr = TestSupport.NewAgendaSession(42, Campaign.Malvr);
        Assert.Equal(HiddenRole.Malvr, malvr.State.Truth.HiddenRoles["c_player"]);
    }

    [Fact]
    public void Opposing_genius_is_a_seeded_npc_never_the_player()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var s = TestSupport.NewAgendaSession(seed, Campaign.Lumiel);
            var malvr = s.State.Truth.CharacterWithRole(HiddenRole.Malvr)!;
            Assert.NotEqual("c_player", malvr);
            Assert.True(s.State.World.Characters[malvr].IsAlive);
        }
    }

    [Fact]
    public void Role_assignment_is_deterministic_per_seed()
    {
        var a = TestSupport.NewAgendaSession(1234, Campaign.Lumiel);
        var b = TestSupport.NewAgendaSession(1234, Campaign.Lumiel);
        Assert.Equal(
            a.State.Truth.CharacterWithRole(HiddenRole.Malvr),
            b.State.Truth.CharacterWithRole(HiddenRole.Malvr));
    }

    [Fact]
    public void No_eligible_npc_gets_malvr_more_than_twice_its_uniform_share()
    {
        // Exit criterion: 240 seeds over the 4 eligible NPCs (uniform share 60 each).
        var content = TestSupport.LoadContent();
        var counts = new Dictionary<string, int>();
        for (ulong seed = 0; seed < 240; seed++)
        {
            var s = TestSupport.NewAgendaSession(seed, Campaign.Lumiel, content);
            var malvr = s.State.Truth.CharacterWithRole(HiddenRole.Malvr)!;
            counts[malvr] = counts.GetValueOrDefault(malvr) + 1;
        }
        Assert.Equal(4, counts.Count);
        foreach (var (id, n) in counts)
        {
            Assert.True(n <= 120, $"NPC '{id}' was Malvr {n} times — more than twice the uniform share of 60.");
            Assert.True(n >= 1, $"NPC '{id}' was never Malvr in 240 seeds.");
        }
        Assert.Equal(240, counts.Values.Sum());
    }

    [Fact]
    public void Dead_characters_are_never_picked()
    {
        var content = TestSupport.LoadContent();
        for (ulong seed = 0; seed < 120; seed++)
        {
            var s = TestSupport.NewAgendaSession(seed, Campaign.Lumiel, content);
            s.State.World.Characters["c_rival"].IsAlive = false;
            s.State.Truth.HiddenRoles.Clear();
            s.State.Agenda.RolesSeeded = false;
            s.Identity.AssignHiddenRoles();
            Assert.NotEqual("c_rival", s.State.Truth.CharacterWithRole(HiddenRole.Malvr));
        }
    }

    [Fact]
    public void AssignHiddenRoles_is_idempotent()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var first = s.State.Truth.CharacterWithRole(HiddenRole.Malvr);
        s.Identity.AssignHiddenRoles();
        Assert.Equal(first, s.State.Truth.CharacterWithRole(HiddenRole.Malvr));
        Assert.Equal(2, s.State.Truth.HiddenRoles.Count);
    }

    [Fact]
    public void AssignHiddenRoles_throws_without_a_player_character()
    {
        var s = GameSession.NewRun(42, Campaign.Lumiel, Difficulty.Medium, TestSupport.LoadContent());
        Assert.Throws<InvalidOperationException>(() => s.Identity.AssignHiddenRoles());
    }

    // ------------------------------------------------------------------ objectives

    [Fact]
    public void EnsureObjectives_deals_two_matching_objectives_per_holder()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        s.Agenda.EnsureObjectives(malvr);
        var objs = s.Agenda.ObjectivesOf(malvr);
        Assert.Equal(2, objs.Count);
        foreach (var o in objs)
            Assert.True(o.Kind is ObjectiveKind.EliminateObstacle or ObjectiveKind.SowDistrust or ObjectiveKind.EvadeSuspicion);
        // Idempotent.
        s.Agenda.EnsureObjectives(malvr);
        Assert.Equal(2, s.Agenda.ObjectivesOf(malvr).Count);
    }

    [Fact]
    public void Lumiel_objectives_come_from_the_lumiel_pool()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Malvr); // NPC is Lumiel
        var lumiel = NpcGenius(s);
        Assert.Equal(HiddenRole.Lumiel, NpcGeniusRole(s));
        s.Agenda.EnsureObjectives(lumiel);
        foreach (var o in s.Agenda.ObjectivesOf(lumiel))
            Assert.True(o.Kind is ObjectiveKind.ProtectTarget or ObjectiveKind.GatherAlly or ObjectiveKind.PursueLead);
    }

    [Fact]
    public void NpcGeniusHolders_excludes_the_player()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var holders = s.Agenda.NpcGeniusHolders();
        Assert.Equal(1, holders.Count);
        Assert.NotEqual("c_player", holders[0]);
    }

    // ------------------------------------------------------------------ Malvr actions

    [Fact]
    public void OrchestrateIncident_kills_the_target_and_hides_the_link()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        Assert.Equal(HiddenRole.Malvr, NpcGeniusRole(s));
        InjectObjective(s, malvr, ObjectiveKind.EliminateObstacle, targetId: "c_merchant");

        var before = s.State.EventLog.Events.Count;
        s.Agenda.StrategicTick();

        var incident = s.State.EventLog.Events.Skip(before)
            .FirstOrDefault(e => e.Type == WorldEventTypes.CrimeIncident);
        Assert.True(incident is not null, "expected an orchestrated incident");
        Assert.Equal("c_merchant", incident!.Data["victim"]);
        Assert.False(incident.Participants.Contains(malvr), "holder must not appear in the incident");
        Assert.Equal(malvr, s.State.Agenda.IncidentAttribution[incident.Data["crime"]]);
        Assert.False(s.State.World.Characters["c_merchant"].IsAlive);
        // No event data names the culprit.
        foreach (var e in s.State.EventLog.Events.Skip(before))
            foreach (var v in e.Data.Values)
                Assert.False(v.Contains("Malvr", StringComparison.OrdinalIgnoreCase),
                    $"event {e.Id} leaks the role: {v}");
    }

    [Fact]
    public void Orchestrated_crime_completes_the_objective_next_tick()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        var obj = InjectObjective(s, malvr, ObjectiveKind.EliminateObstacle, targetId: "c_merchant");
        s.Agenda.StrategicTick();
        Assert.Equal(ObjectiveStatus.Active, s.State.Agenda.Objectives[obj.Id].Status);

        s.Time.Advance(1440); // next day: status update runs in the auto-tick
        Assert.Equal(ObjectiveStatus.Completed, s.State.Agenda.Objectives[obj.Id].Status);
        Assert.True(s.State.EventLog.Events.Any(e =>
            e.Type == WorldEventTypes.AgendaObjectiveCompleted && e.Data["objective"] == obj.Id));
    }

    [Fact]
    public void SowDistrust_lowers_trust_and_spreads_the_rumor()
    {
        var s = TestSupport.NewAgendaSession(9, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        var crime = s.Crime.GenerateIncident("murder");
        s.Cognition.Perceive(malvr, crime.IncidentEventId, MemorySource.Witnessed);
        // Pick two living non-genius characters and give them solid trust to erode
        // (the action is correctly skipped when trust is already below the threshold).
        var living = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != malvr && c.Id != "c_player")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .ToList();
        Assert.True(living.Count >= 2);
        var (a, b) = (living[0], living[1]);
        s.Social.Adjust(a, b, new SocialDelta(Trust: 50 - s.Social.View(a, b).Trust), "test setup");
        Assert.True(s.Social.View(a, b).Trust >= AgendaRules.SowCompleteTrust + 5);
        // Contact: move the holder to the mark's location.
        s.World.MoveCharacter(malvr, s.State.World.Characters[a].CurrentLocationId);
        InjectObjective(s, malvr, ObjectiveKind.SowDistrust, targetId: a, targetId2: b);

        var trustBefore = s.Social.View(a, b).Trust;
        var suspBefore = s.Social.View(a, b).Suspicion;
        s.Agenda.StrategicTick();

        Assert.Equal(Math.Max(0, trustBefore + AgendaRules.SowTrustDelta), s.Social.View(a, b).Trust);
        Assert.Equal(Math.Min(100, suspBefore + AgendaRules.SowSuspicionDelta), s.Social.View(a, b).Suspicion);
        // The mark heard the crime through the rumor (knowledge gate respected).
        Assert.True(s.Cognition.Knows(a, crime.IncidentEventId));
    }

    [Fact]
    public void SowDistrust_completes_when_trust_collapses()
    {
        var s = TestSupport.NewAgendaSession(9, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        var obj = InjectObjective(s, malvr, ObjectiveKind.SowDistrust, targetId: "c_merchant", targetId2: "c_priest");
        s.Social.Adjust("c_merchant", "c_priest", new SocialDelta(Trust: -100), "test setup");
        s.Agenda.StrategicTick();
        Assert.Equal(ObjectiveStatus.Completed, s.State.Agenda.Objectives[obj.Id].Status);
    }

    [Fact]
    public void DeflectAttention_fires_once_the_holders_crime_is_discovered()
    {
        var s = TestSupport.NewAgendaSession(11, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        var obj = InjectObjective(s, malvr, ObjectiveKind.EliminateObstacle, targetId: "c_merchant");
        s.Agenda.StrategicTick(); // day 1: orchestrate
        var crimeId = s.State.Agenda.IncidentAttribution.First(kv => kv.Value == malvr).Key;
        var crime = s.Crime.GetCrime(crimeId);

        // Mark the scene discovered directly (discovery mechanics are tested elsewhere);
        // nobody else learns about it, so Malvr has someone to deflect toward.
        s.State.Crime.Scenes[crime.SceneId].DiscoveredAt = s.State.TotalMinutes;

        InjectObjective(s, malvr, ObjectiveKind.EvadeSuspicion);
        // A potential listener in contact with Malvr who hasn't heard about the crime.
        var listener = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != malvr && c.Id != "c_player"
                && !s.Cognition.Knows(c.Id, crime.IncidentEventId))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .First().Id;
        s.World.MoveCharacter(listener, s.State.World.Characters[malvr].CurrentLocationId);
        var rumorsBefore = s.State.EventLog.Events.Count(e =>
            e.Type == WorldEventTypes.RumorSpread && e.Participants.Contains(malvr));
        s.Time.Advance(1440); // next day: the auto-tick deflects
        var rumorsAfter = s.State.EventLog.Events.Count(e =>
            e.Type == WorldEventTypes.RumorSpread && e.Participants.Contains(malvr));
        Assert.True(rumorsAfter > rumorsBefore, "expected Malvr to spread a deflecting rumor");
        Assert.Equal(ObjectiveStatus.Completed, s.State.Agenda.Objectives[obj.Id].Status);
    }

    // ------------------------------------------------------------------ Lumiel actions

    [Fact]
    public void ProtectTarget_raises_police_trust_and_warns_the_target()
    {
        var s = TestSupport.NewAgendaSession(21, Campaign.Malvr); // NPC is Lumiel
        var lumiel = NpcGenius(s);
        Assert.Equal(HiddenRole.Lumiel, NpcGeniusRole(s));
        InjectObjective(s, lumiel, ObjectiveKind.ProtectTarget, targetId: "c_priest");

        var trustBefore = s.Social.View("c_guard", "c_priest").Trust;
        s.Agenda.StrategicTick();

        Assert.Equal(Math.Min(100, trustBefore + AgendaRules.ProtectTrustBoost),
            s.Social.View("c_guard", "c_priest").Trust);
        Assert.True(s.State.EventLog.Events.Any(e => e.Type == WorldEventTypes.AgendaWarning));
        Assert.True(s.Cognition.GetMemories("c_priest").Any(m => m.Summary.Contains("warned me")));
    }

    [Fact]
    public void ProtectTarget_fails_when_the_target_dies()
    {
        var s = TestSupport.NewAgendaSession(21, Campaign.Malvr);
        var lumiel = NpcGenius(s);
        var obj = InjectObjective(s, lumiel, ObjectiveKind.ProtectTarget, targetId: "c_priest");
        s.State.World.Characters["c_priest"].IsAlive = false;
        s.Agenda.StrategicTick();
        Assert.Equal(ObjectiveStatus.Failed, s.State.Agenda.Objectives[obj.Id].Status);
        Assert.True(s.State.EventLog.Events.Any(e =>
            e.Type == WorldEventTypes.AgendaObjectiveFailed && e.Data["objective"] == obj.Id));
    }

    [Fact]
    public void GatherAlly_builds_mutual_trust_and_completes()
    {
        var s = TestSupport.NewAgendaSession(22, Campaign.Malvr);
        var lumiel = NpcGenius(s);
        var obj = InjectObjective(s, lumiel, ObjectiveKind.GatherAlly, targetId: "c_merchant");
        // The return direction gains trust more slowly (+8/day vs +10/day), so allow
        // enough days for both directions to reach the completion threshold.
        for (var day = 0; day < 12; day++)
        {
            s.Agenda.StrategicTick();
            if (s.State.Agenda.Objectives[obj.Id].Status == ObjectiveStatus.Completed) break;
            s.Time.Advance(1440);
        }
        Assert.Equal(ObjectiveStatus.Completed, s.State.Agenda.Objectives[obj.Id].Status);
        Assert.True(s.Social.View(lumiel, "c_merchant").Trust >= AgendaRules.GatherCompleteTrust);
        Assert.True(s.Social.View("c_merchant", lumiel).Trust >= AgendaRules.GatherCompleteTrust);
    }

    [Fact]
    public void PursueLead_interviews_witnesses_and_completes_on_knowledge()
    {
        var s = TestSupport.NewAgendaSession(33, Campaign.Malvr);
        var lumiel = NpcGenius(s);
        var crime = s.Crime.GenerateIncident("murder");
        s.Cognition.Perceive(lumiel, crime.IncidentEventId, MemorySource.Witnessed);
        s.Cognition.Perceive("c_merchant", crime.IncidentEventId, MemorySource.Witnessed);
        var obj = InjectObjective(s, lumiel, ObjectiveKind.PursueLead, crimeId: crime.Id);

        s.Agenda.StrategicTick();

        var interviews = s.State.EventLog.Events
            .Where(e => e.Type == WorldEventTypes.Interviewed && e.Participants.Contains(lumiel))
            .ToList();
        Assert.True(interviews.Count > 0, "expected Lumiel to interview a witness");
        // The holder remembers the exchange: knowledge grows toward completion.
        Assert.True(s.Cognition.Knows(lumiel, interviews[0].Id));
    }

    // ------------------------------------------------------------------ tick mechanics

    [Fact]
    public void Tick_drives_only_npc_geniuses_and_once_per_day()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        s.Agenda.StrategicTick();
        var eventsAfterFirst = s.State.EventLog.Events.Count;
        s.Agenda.StrategicTick(); // same day: nothing new may happen
        Assert.Equal(eventsAfterFirst, s.State.EventLog.Events.Count);
        Assert.False(s.State.Agenda.LastActionDay.ContainsKey("c_player"), "the player is never tick-driven");
    }

    [Fact]
    public void Tick_runs_automatically_on_day_change_for_seeded_runs()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        Assert.Equal(0, s.State.Agenda.Objectives.Count);
        s.Time.Advance(1440);
        Assert.True(s.State.Agenda.Objectives.Count > 0);
    }

    [Fact]
    public void Tick_is_a_noop_for_manual_role_setups()
    {
        var s = TestSupport.NewPopulatedSession(42, Campaign.Lumiel); // manual roles
        s.Time.Advance(3 * 1440);
        Assert.Equal(0, s.State.Agenda.Objectives.Count);
        Assert.False(s.State.Agenda.LastActionDay.Count > 0);
    }

    [Fact]
    public void Both_campaigns_tick_without_errors()
    {
        foreach (var campaign in new[] { Campaign.Malvr, Campaign.Lumiel })
        {
            var s = TestSupport.NewAgendaSession(7, campaign);
            for (var day = 0; day < 3; day++) s.Time.Advance(1440);
            Assert.True(s.Agenda.ObjectivesOf(NpcGenius(s)).Count == 2);
        }
    }

    // ------------------------------------------------------------------ knowledge discipline

    [Fact]
    public void Tick_never_uses_unknown_information_and_never_leaks_roles()
    {
        for (ulong seed = 0; seed < 20; seed++)
        {
            var s = TestSupport.NewAgendaSession(seed, Campaign.Lumiel);
            var malvr = NpcGenius(s);
            Assert.Equal(0, s.Cognition.GetMemories(malvr).Count);
            var before = s.State.EventLog.Events.Count;
            s.Agenda.StrategicTick();
            s.Time.Advance(1440);
            s.Agenda.StrategicTick();

            foreach (var e in s.State.EventLog.Events.Skip(before))
            {
                if (e.Type == WorldEventTypes.RumorSpread && e.Participants.Contains(malvr))
                {
                    // TellRumor itself enforces the gate; the tick must never trip it.
                    var rumored = long.Parse(e.Data["event"]);
                    Assert.True(s.Cognition.Knows(malvr, rumored),
                        $"seed {seed}: rumor of unknown event {rumored}");
                }
                foreach (var v in e.Data.Values)
                {
                    Assert.False(v.Contains("malvr", StringComparison.OrdinalIgnoreCase)
                        && (v.Contains(" is ", StringComparison.OrdinalIgnoreCase)
                            || v.Contains("am ", StringComparison.OrdinalIgnoreCase)),
                        $"seed {seed}: event {e.Id} leaks a role: {v}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ deception

    private static GameSession DeceptionSetup(ulong seed, out string malvr, out CrimeRecord crime)
    {
        var s = TestSupport.NewAgendaSession(seed, Campaign.Lumiel);
        var holder = NpcGenius(s);
        malvr = holder;
        // Explicit victim: never the holder (a dead genius cannot be interviewed),
        // and never c_guard (the interviewer in the interview test).
        var victim = s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != holder && c.Id != "c_player" && c.Id != "c_guard")
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .First().Id;
        crime = s.Crime.GenerateIncident("murder", victim);
        s.Cognition.Perceive(holder, crime.IncidentEventId, MemorySource.Witnessed);
        return s;
    }

    private static string? FindLyingTopic(GameSession s, string malvr, long crimeAt)
    {
        for (long m = crimeAt - 170; m <= crimeAt + 170; m += 10)
        {
            var topic = InvestigationRules.WhereaboutsTopic(m);
            if (s.Agenda.MaybeDeceive(malvr, topic, $"at:loc_residential@{m}") is not null)
                return topic;
        }
        return null;
    }

    [Fact]
    public void Genius_may_lie_about_whereabouts_near_a_known_crime()
    {
        var s = DeceptionSetup(42, out var malvr, out var crime);
        var topic = FindLyingTopic(s, malvr, crime.OccurredAt);
        Assert.True(topic is not null, "expected at least one lying topic near the crime");

        // Deterministic: the same question always gets the same answer.
        var lie1 = s.Agenda.MaybeDeceive(malvr, topic!, "at:loc_residential@0");
        var lie2 = s.Agenda.MaybeDeceive(malvr, topic!, "at:loc_residential@0");
        Assert.Equal(lie1, lie2);
        Assert.True(lie1 is not null);
        Assert.True(InvestigationRules.TryParseWhereabouts(lie1!, out var parsed) && parsed is not null);
    }

    [Fact]
    public void Deception_requires_role_crime_proximity_and_nonplayer()
    {
        var s = DeceptionSetup(42, out var malvr, out var crime);
        var nearTopic = InvestigationRules.WhereaboutsTopic(crime.OccurredAt);

        // A character with no role never lies.
        Assert.True(s.Agenda.MaybeDeceive("c_merchant", nearTopic, "at:loc_residential@0") is null);
        // Far from any known crime: no lie.
        Assert.True(s.Agenda.MaybeDeceive(malvr, InvestigationRules.WhereaboutsTopic(crime.OccurredAt + 1000), "at:loc_x@0") is null);
        // The player decides their own lies.
        s.Cognition.Perceive("c_player", crime.IncidentEventId, MemorySource.Witnessed);
        Assert.True(s.Agenda.MaybeDeceive("c_player", nearTopic, "at:loc_residential@0") is null);
        // Non-whereabouts topics are untouched.
        Assert.True(s.Agenda.MaybeDeceive(malvr, InvestigationRules.EventTopic(crime.IncidentEventId), "summary") is null);
    }

    [Fact]
    public void Interview_records_the_lie_and_genius_can_catch_it()
    {
        var s = DeceptionSetup(42, out var malvr, out var crime);
        var topic = FindLyingTopic(s, malvr, crime.OccurredAt);
        Assert.True(topic is not null);
        var minute = long.Parse(topic!.Substring(InvestigationRules.WhereaboutsTopicPrefix.Length));

        var result = s.Investigate.Interview("c_guard", malvr, topic);
        var stmt = s.Investigate.GetStatement(result.StatementId);

        var (actual, _, _) = PresenceTracker.PresenceAt(s.State, malvr, minute);
        var truthful = actual is null ? null : InvestigationRules.FormatWhereabouts(actual, minute);
        Assert.NotEqual(truthful, stmt.Claim);

        // A sharp investigator cross-checks against surveillance and catches the lie.
        var contradiction = s.Investigate.CheckAgainstSurveillance(result.StatementId);
        Assert.True(contradiction is not null, "expected the false alibi to clash with surveillance");
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void Save_load_roundtrip_is_hash_identical_with_agenda_state()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var malvr = NpcGenius(s);
        InjectObjective(s, malvr, ObjectiveKind.SowDistrust, targetId: "c_merchant", targetId2: "c_priest");
        s.Agenda.StrategicTick();
        s.Time.Advance(1440);

        var path = Path.Combine(Path.GetTempPath(), $"agenda_{Guid.NewGuid():N}.json");
        try
        {
            s.Save(path);
            var loaded = GameSession.Load(path, TestSupport.LoadContent());
            Assert.Equal(s.StateHash(), loaded.StateHash());
            loaded.Agenda.StrategicTick();
            s.Agenda.StrategicTick();
            loaded.Time.Advance(1440);
            s.Time.Advance(1440);
            Assert.Equal(s.StateHash(), loaded.StateHash());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Migration_v8_to_v9_installs_empty_agenda_state()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        var json = SaveSystem.Serialize(s.State);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["State"]!.AsObject().Remove("Agenda");
        node["FormatVersion"] = 8;
        var v8 = node.ToJsonString();
        Assert.False(v8.Contains("\"Agenda\""), "test setup: v8 JSON should not have Agenda");
        var migrated = SaveSystem.Deserialize(v8);
        Assert.Equal(0, migrated.Agenda.Objectives.Count);
        Assert.False(migrated.Agenda.AgendaRng.IsZero);
        Assert.Equal(0, GameStateValidator.Validate(migrated).Count);
    }

    [Fact]
    public void Validator_rejects_broken_agenda_state()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        s.State.Agenda.Objectives["obj_bad"] = new HiddenObjective
        {
            Id = "obj_other", // key mismatch
            HolderId = "c_nobody",
            Kind = ObjectiveKind.EliminateObstacle,
        };
        var errors = GameStateValidator.Validate(s.State);
        Assert.True(errors.Count > 0);
    }

    [Fact]
    public void GenerateIncident_rejects_bad_explicit_victims()
    {
        var s = TestSupport.NewAgendaSession(42, Campaign.Lumiel);
        Assert.Throws<ArgumentException>(() => s.Crime.GenerateIncident("murder", "c_nobody"));
        Assert.Throws<InvalidOperationException>(() => s.Crime.GenerateIncident("murder", "c_player"));
        s.State.World.Characters["c_merchant"].IsAlive = false;
        Assert.Throws<InvalidOperationException>(() => s.Crime.GenerateIncident("murder", "c_merchant"));
    }
}
