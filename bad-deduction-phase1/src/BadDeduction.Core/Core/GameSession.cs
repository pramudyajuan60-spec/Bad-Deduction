using BadDeduction.AI;
using BadDeduction.Agenda;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Crime;
using BadDeduction.Initiative;
using BadDeduction.Investigation;
using BadDeduction.Manipulation;
using BadDeduction.Police;
using BadDeduction.Social;
using BadDeduction.World;

namespace BadDeduction.Core;

/// <summary>
/// Composition root for one run: authoritative state plus the systems that operate on it.
/// UI, AI and tests talk to a GameSession; only the Truth-aware engine code touches GameState directly.
/// </summary>
public sealed class GameSession
{
    public GameState State { get; }
    public ContentDatabase Content { get; }
    public EventSystem Events { get; }
    public TimeSystem Time { get; }
    public WorldService World { get; }
    public HiddenIdentitySystem Identity { get; }
    public ScheduleSystem Schedules { get; }
    public RelationshipGraph Relationships { get; }
    public SocialService Social { get; }
    public ComplianceEvaluator Compliance { get; }
    public CognitionService Cognition { get; }
    public CastGenerator Cast { get; }

    /// <summary>Phase 7: incidents, scenes, evidence and discovery.</summary>
    public CrimeService Crime { get; }
    /// <summary>
    /// Phase 8: surveillance, interviews, interrogations, statements, contradictions and
    /// hypotheses — the investigator's toolkit.
    /// </summary>
    public InvestigationService Investigate { get; }

    /// <summary>
    /// Phase 9: independent officers, trust-ladder dynamics, the alert ladder, cordons
    /// and duty rosters.
    /// </summary>
    public PoliceService Police { get; }

    /// <summary>
    /// Phase 13: threats as game events (social fallout, witnesses, police disturbance
    /// reports). Wired into <see cref="Dialogue"/>; also callable directly.
    /// </summary>
    public ThreatService Threat { get; }

    /// <summary>
    /// Phase 14: manipulation — order evaluation/execution, despair and suicide,
    /// lure destinations, knowledge-gated routine observation.
    /// </summary>
    public ManipulationService Manipulation { get; }

    /// <summary>
    /// Phase 13: NPC-initiated dialogue. Call <see cref="Initiative.NpcInitiativeService.Evaluate"/>
    /// to find out which NPCs want to talk to the player, then feed an accepted initiative
    /// to <see cref="DialogueOrchestrator.OpeningLine"/>.
    /// </summary>
    public NpcInitiativeService Initiative { get; }

    /// <summary>
    /// Phase 10: the hidden-genius layer (Malvr / Lumiel) — seeded identities, hidden
    /// objectives and the daily strategic tick for NPC-held geniuses. Also feeds the
    /// interview deception seam (<see cref="Investigation.InvestigationService.Deception"/>).
    /// </summary>
    public HiddenAgendaService Agenda { get; }

    /// <summary>
    /// Phase 5: the world tick. Advance time through here (not <see cref="Time"/>) when the
    /// world should live: <c>Simulate.Advance(n)</c> wraps <see cref="TimeSystem.Advance"/>
    /// minute by minute. <see cref="Time"/> stays sim-free for raw time jumps.
    /// </summary>
    public WorldSimulation Simulate { get; }
    public PlayerView View { get; }
    public DebugAccess Debug { get; }

    /// <summary>
    /// Phase 6: the dialogue pipeline (Orchestrator → ContextEngine → provider → validator).
    /// The provider defaults to the deterministic mock; callers (e.g. the Godot UI) may pass
    /// an <see cref="OllamaDialogueProvider"/> instead — the orchestrator and
    /// validator treat every provider identically. The forbidden phrases are derived from
    /// hidden roles HERE, outside the AI namespace, so no truth type ever crosses into it.
    /// </summary>
    public DialogueOrchestrator Dialogue { get; }

    /// <summary>The main simulation RNG. Wraps State.SimRng, so it is always in sync with saves.</summary>
    public DeterministicRandom Rng { get; }

    private GameSession(GameState state, ContentDatabase content, IAIProvider? dialogueProvider = null)
    {
        State = state;
        Content = content;
        Events = new EventSystem(state);
        Time = new TimeSystem(state, Events);
        World = new WorldService(state, Events, content);
        Identity = new HiddenIdentitySystem(state);
        Schedules = new ScheduleSystem(state, content);
        Relationships = new RelationshipGraph(state);
        Social = new SocialService(state, Events, Relationships);
        Compliance = new ComplianceEvaluator(state, Social);
        Cognition = new CognitionService(state, Events, Relationships);
        Cast = new CastGenerator(state, content, World, Relationships);
        Crime = new CrimeService(state, Events, Cognition, content);
        Investigate = new InvestigationService(state, Events, Cognition, Crime, Social, content);
        Police = new PoliceService(state, Events, Social, Investigate, Crime, Cognition, content);
        World.AccessCheck = Police.CanEnter;
        Threat = new ThreatService(state, Events, Social, Cognition, Police,
            locId => content.HasLocation(locId)
                     && content.GetLocation(locId).Visibility == LocationVisibility.Public);
        Agenda = new HiddenAgendaService(state, Events, Cognition, Crime, Investigate, Social, content);
        Investigate.Deception = Agenda;
        Simulate = new WorldSimulation(state, Time, Events, World, Cognition, content);
        Simulate.CanEnter = Police.CanEnter;
        Simulate.DutyLocationFor = Police.DutyLocationFor;
        // Phase 14: old saves predate the manipulation state; property initializers
        // supply the defaults (no migration needed — same pattern as Phase 13).
        state.Manipulation ??= new ManipulationState();
        Manipulation = new ManipulationService(state, Events, Social, Compliance, Cognition,
            Police, content, Schedules);
        Simulate.CommandedDestinationFor = Manipulation.CommandedDestinationFor;
        Simulate.StrategicDestinationFor = Agenda.StrategicDestinationFor;
        View = new PlayerView(state);
        Debug = new DebugAccess(state);
        // Phase 13: old saves predate the initiative stream, so their InitiativeRng is
        // all-zero (which DeterministicRandom rejects). Derive the same stream a fresh
        // run would have gotten, so migrated saves behave deterministically.
        state.Initiative ??= new NpcInitiativeState();
        if (state.Initiative.InitiativeRng.IsZero)
            state.Initiative.InitiativeRng =
                DeterministicRandom.Derive(state.Meta.RunSeed, "npc.initiative").Snapshot();
        Initiative = new NpcInitiativeService(state, Events, Social, Cognition, View);
        var context = new ContextEngine(
            View, Cognition, Social, Relationships, content, state.World.Profiles,
            id => state.World.Characters[id].CurrentLocationId);
        Dialogue = new DialogueOrchestrator(
            context,
            dialogueProvider ?? new MockAIProvider(state.Meta.RunSeed),
            new DialogueValidator(),
            Cognition, Social, Events,
            id => state.World.Characters[id].CurrentLocationId,
            () => new GameTime(state.TotalMinutes),
            BuildForbiddenPhrases(state),
            state.Meta.Difficulty,
            state.Meta.RunSeed,
            threatService: Threat,
            manipulationService: Manipulation,
            content: content);
        Rng = new DeterministicRandom(state.SimRng);
    }

    /// <summary>
    /// Derives the validator's forbidden phrases from hidden roles. This runs OUTSIDE the AI
    /// namespace (which never sees truth types): the validator only ever receives strings.
    /// The validator additionally rejects the bare role words "malvr"/"lumiel" on its own.
    /// </summary>
    private static IReadOnlyList<string> BuildForbiddenPhrases(GameState state)
    {
        var phrases = new List<string>();
        foreach (var (characterId, role) in state.Truth.HiddenRoles)
        {
            if (!state.World.Characters.TryGetValue(characterId, out var c)) continue;
            var roleName = role.ToString();
            phrases.Add($"{c.DisplayName} is {roleName}");
            phrases.Add($"{c.DisplayName} is the {roleName}");
            phrases.Add($"i am {roleName}");
            phrases.Add($"you are {roleName}");
        }
        return phrases;
    }

    public static GameSession NewRun(
        ulong runSeed,
        Campaign campaign,
        Difficulty difficulty,
        ContentDatabase content,
        GameTime? start = null,
        IAIProvider? dialogueProvider = null)
    {
        var state = new GameState
        {
            Meta = new GameMeta { RunSeed = runSeed, Campaign = campaign, Difficulty = difficulty },
            TotalMinutes = (start ?? GameTime.At(1, 6)).TotalMinutes,
            SimRng = DeterministicRandom.Derive(runSeed, "sim").Snapshot(),
        };
        // Cognition gets its own persisted RNG stream so rumor/distortion draws never shift the
        // main simulation stream (ADR-002) and continue identically after save/load.
        state.Cognition.CognitionRng = DeterministicRandom.Derive(runSeed, "cognition.memory").Snapshot();
        // Crime gets its own persisted RNG stream for the same reason: incident generation must
        // never shift another system's draws (ADR-002).
        state.Crime.CrimeRng = DeterministicRandom.Derive(runSeed, "crime.incident").Snapshot();
        // The hidden-agenda layer gets its own stream too: role assignment, objective picks
        // and strategic draws (ADR-002).
        state.Agenda.AgendaRng = DeterministicRandom.Derive(runSeed, "agenda.roles").Snapshot();
        // Phase 13: NPC-initiative draws get their own persisted stream for the same reason.
        state.Initiative.InitiativeRng = DeterministicRandom.Derive(runSeed, "npc.initiative").Snapshot();
        foreach (var def in content.Locations)
            state.World.Locations[def.Id] = new LocationState { Id = def.Id };

        var session = new GameSession(state, content, dialogueProvider);
        session.Events.Record(
            WorldEventTypes.RunStarted,
            data: new Dictionary<string, string>
            {
                ["seed"] = runSeed.ToString(),
                ["campaign"] = campaign.ToString(),
                ["difficulty"] = difficulty.ToString(),
            });
        return session;
    }

    public static GameSession FromState(GameState state, ContentDatabase content, IAIProvider? dialogueProvider = null)
    {
        var errors = GameStateValidator.Validate(state).ToList();
        foreach (var id in state.World.Locations.Keys)
            if (!content.HasLocation(id)) errors.Add($"Save references location '{id}' which is not in the loaded content.");
        foreach (var def in content.Locations)
            if (!state.World.Locations.ContainsKey(def.Id)) errors.Add($"Loaded content has location '{def.Id}' missing from the save.");
        errors.AddRange(GameStateValidator.ValidateAgainstContent(state, content));
        if (errors.Count > 0)
            throw new SaveFormatException("State failed validation:\n - " + string.Join("\n - ", errors));
        return new GameSession(state, content, dialogueProvider);
    }

    public static GameSession Load(string path, ContentDatabase content, IAIProvider? dialogueProvider = null) =>
        FromState(SaveSystem.ReadFile(path), content, dialogueProvider);

    public void Save(string path) => SaveSystem.WriteFile(path, State);

    public string StateHash() => SaveSystem.ComputeStateHash(State);
}
