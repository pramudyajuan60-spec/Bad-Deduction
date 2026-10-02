using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
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

    /// <summary>
    /// Phase 5: the world tick. Advance time through here (not <see cref="Time"/>) when the
    /// world should live: <c>Simulate.Advance(n)</c> wraps <see cref="TimeSystem.Advance"/>
    /// minute by minute. <see cref="Time"/> stays sim-free for raw time jumps.
    /// </summary>
    public WorldSimulation Simulate { get; }
    public PlayerView View { get; }
    public DebugAccess Debug { get; }

    /// <summary>The main simulation RNG. Wraps State.SimRng, so it is always in sync with saves.</summary>
    public DeterministicRandom Rng { get; }

    private GameSession(GameState state, ContentDatabase content)
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
        Simulate = new WorldSimulation(state, Time, Events, World, Cognition, content);
        View = new PlayerView(state);
        Debug = new DebugAccess(state);
        Rng = new DeterministicRandom(state.SimRng);
    }

    public static GameSession NewRun(
        ulong runSeed,
        Campaign campaign,
        Difficulty difficulty,
        ContentDatabase content,
        GameTime? start = null)
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
        foreach (var def in content.Locations)
            state.World.Locations[def.Id] = new LocationState { Id = def.Id };

        var session = new GameSession(state, content);
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

    public static GameSession FromState(GameState state, ContentDatabase content)
    {
        var errors = GameStateValidator.Validate(state).ToList();
        foreach (var id in state.World.Locations.Keys)
            if (!content.HasLocation(id)) errors.Add($"Save references location '{id}' which is not in the loaded content.");
        foreach (var def in content.Locations)
            if (!state.World.Locations.ContainsKey(def.Id)) errors.Add($"Loaded content has location '{def.Id}' missing from the save.");
        errors.AddRange(GameStateValidator.ValidateAgainstContent(state, content));
        if (errors.Count > 0)
            throw new SaveFormatException("State failed validation:\n - " + string.Join("\n - ", errors));
        return new GameSession(state, content);
    }

    public static GameSession Load(string path, ContentDatabase content) =>
        FromState(SaveSystem.ReadFile(path), content);

    public void Save(string path) => SaveSystem.WriteFile(path, State);

    public string StateHash() => SaveSystem.ComputeStateHash(State);
}
