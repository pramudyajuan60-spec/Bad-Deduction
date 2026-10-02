using BadDeduction.Core;

namespace BadDeduction.Agenda;

public enum ObjectiveKind
{
    EliminateObstacle,
    SowDistrust,
    EvadeSuspicion,
    ProtectTarget,
    GatherAlly,
    PursueLead,
}

public enum ObjectiveStatus { Active, Completed, Failed }

/// <summary>
/// One hidden objective held by a genius. TargetId meanings depend on <see cref="Kind"/>:
/// EliminateObstacle/ProtectTarget/GatherAlly use TargetId (the person); SowDistrust uses
/// TargetId (a) and TargetId2 (b) — make a distrust b; PursueLead uses CrimeId.
/// </summary>
public sealed class HiddenObjective
{
    public string Id { get; set; } = "";
    public string HolderId { get; set; } = "";
    public ObjectiveKind Kind { get; set; }
    public string? TargetId { get; set; }
    public string? TargetId2 { get; set; }
    public string? CrimeId { get; set; }
    public ObjectiveStatus Status { get; set; } = ObjectiveStatus.Active;
    public long CreatedAt { get; set; }
}

/// <summary>
/// Truth-side store for the hidden-genius layer (Malvr / Lumiel). Like <c>WorldTruth</c>,
/// this is never exposed to gameplay UI: objectives and incident attributions would reveal
/// who the geniuses are. Engine code and tests may read it; <c>PlayerView</c> must not.
/// Objective lifecycle events (<c>agenda.objective_completed/failed</c>) deliberately carry
/// only the objective id and kind — never the holder or the role.
/// </summary>
public sealed class HiddenAgendaState
{
    /// <summary>
    /// Own persisted RNG stream ("agenda.roles"): role assignment, objective picks and
    /// strategic draws. Shared in place by HiddenIdentitySystem and HiddenAgendaService —
    /// one logical stream, so the draw sequence is fixed by call order and never touches
    /// the sim stream (ADR-002).
    /// </summary>
    public RngState AgendaRng { get; set; } = new();

    /// <summary>
    /// True once roles were assigned through the seeded canonical path
    /// (<see cref="Characters.HiddenIdentitySystem.AssignHiddenRoles"/>). The daily
    /// strategic tick only drives genius holders of seeded runs; manual test setups
    /// opt in explicitly (via this flag or <c>EnsureObjectives</c>), so no existing
    /// simulation test changes behavior.
    /// </summary>
    public bool RolesSeeded { get; set; }

    public long NextObjectiveId { get; set; } = 1;
    public Dictionary<string, HiddenObjective> Objectives { get; set; } = new();
    public Dictionary<string, long> LastActionDay { get; set; } = new();

    /// <summary>
    /// Hidden attribution: crime id → holder id of the genius who orchestrated it.
    /// Recorded ONLY here — never in event data — so the link stays invisible to
    /// investigators (it is the holder's own knowledge of their deed).
    /// </summary>
    public Dictionary<string, string> IncidentAttribution { get; set; } = new();
}
