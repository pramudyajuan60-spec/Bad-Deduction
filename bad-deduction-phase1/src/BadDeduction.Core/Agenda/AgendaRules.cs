namespace BadDeduction.Agenda;

/// <summary>Phase 10 tuning constants. All integer scales; see ADR-002/ADR-014 conventions.</summary>
public static class AgendaRules
{
    /// <summary>Deception only applies near a crime the holder knows: |crime time − topic minute|.</summary>
    public const int DeceptionWindowMinutes = 180;

    /// <summary>Trust below which a SowDistrust objective counts as completed.</summary>
    public const int SowCompleteTrust = 25;

    /// <summary>SowDistrust action: the trust/suspicion swing.</summary>
    public const int SowTrustDelta = -10;
    public const int SowSuspicionDelta = 8;

    /// <summary>Trust both ways at or above which a GatherAlly objective counts as completed.</summary>
    public const int GatherCompleteTrust = 70;

    /// <summary>GatherAlly action: the trust/affection swing.</summary>
    public const int GatherTrustDelta = 10;
    public const int GatherAffectionDelta = 5;
    public const int GatherReturnTrustDelta = 8;

    /// <summary>ProtectTarget action: police trust boost per officer…</summary>
    public const int ProtectTrustBoost = 5;

    /// <summary>…skipped when every officer already trusts the target this much.</summary>
    public const int ProtectTrustCap = 80;

    /// <summary>DeflectAttention action: suspicion nudged toward the patsy.</summary>
    public const int DeflectSuspicionDelta = 8;

    /// <summary>PursueLead objective: crime-linked events the holder must know to complete it.</summary>
    public const int PursueLeadKnownEventsNeeded = 3;

    /// <summary>Don't re-interview the same witness about the same topic within this window.</summary>
    public const int InterviewCooldownMinutes = 1440;
}
