using BadDeduction.Characters;
using BadDeduction.World;

namespace BadDeduction.Core;

public enum Campaign { Malvr, Lumiel }

/// <summary>Difficulty scales AI cognition (later phases), never HP/damage or hidden omniscience.</summary>
public enum Difficulty { Easy, Medium, Hard, Genius }

public sealed class GameMeta
{
    public ulong RunSeed { get; set; }
    public Campaign Campaign { get; set; }
    public Difficulty Difficulty { get; set; }
}

public sealed class PlayerState
{
    /// <summary>The character the player controls. Empty until the run is populated.</summary>
    public string CharacterId { get; set; } = "";
}

/// <summary>
/// The single authoritative, serializable root of a run. Plain data only: all behaviour lives in
/// systems (TimeSystem, WorldService, ...) so that saving is just serializing this object.
/// The LLM never mutates this directly; it proposes actions that a validator applies (Phase 6).
/// </summary>
public sealed class GameState
{
    public GameMeta Meta { get; set; } = new();

    /// <summary>Minutes since Day 1, 00:00. See <see cref="GameTime"/>.</summary>
    public long TotalMinutes { get; set; }

    /// <summary>Live state of the main simulation RNG (persisted so loads continue the same sequence).</summary>
    public RngState SimRng { get; set; } = new();

    public WorldState World { get; set; } = new();

    /// <summary>Ground truth about hidden identities. Never exposed to gameplay UI (see DebugAccess).</summary>
    public WorldTruth Truth { get; set; } = new();

    public EventLog EventLog { get; set; } = new();
    public PlayerState Player { get; set; } = new();

    /// <summary>Phase 4: what characters know, remember and believe (plain data; see CognitionService).</summary>
    public Cognition.CognitionState Cognition { get; set; } = new();

    // Phases 5+ add: Rumors (network spread is Phase 4; done), Evidence, CrimeScenes,
    // Police, Hypotheses, Surveillance, Timeline. Each addition bumps SaveSystem.CurrentFormatVersion.
}
