namespace BadDeduction.Police;

/// <summary>
/// All Phase 9 tuning constants in one place. Integer scales only, per the conventions.
/// Move to data/ when balancing starts.
/// </summary>
public static class PoliceRules
{
    /// <summary>Minutes a fatal incident may lie undiscovered before the city goes to Alert.</summary>
    public const int UndiscoveredFatalAlertMinutes = 720;

    /// <summary>
    /// Phase 13: how long a disturbance report stays "recent" for the alert ladder (one day).
    /// </summary>
    public const int DisturbanceAlertWindowMinutes = 1440;

    /// <summary>Recent severe disturbances that step the alert ladder toward Alert (never Manhunt).</summary>
    public const int AlertDisturbanceCount = 2;

    /// <summary>Open (non-custody) crimes that trigger Alert.</summary>
    public const int AlertOpenCrimeCount = 2;

    /// <summary>Open fatal crimes that trigger Manhunt.</summary>
    public const int ManhuntFatalCrimeCount = 2;

    /// <summary>Open crimes (any) that trigger Manhunt.</summary>
    public const int ManhuntOpenCrimeCount = 3;

    /// <summary>Patrol slots by alert level; Manhunt puts every free officer on the street.</summary>
    public static int PatrolSlots(AlertLevel alert) => alert switch
    {
        AlertLevel.Calm => 1,
        AlertLevel.Alert => 2,
        AlertLevel.Manhunt => int.MaxValue,
        _ => 1,
    };

    /// <summary>Trust/suspicion nudge per owned hypothesis at Believes band (EvaluateSubject).</summary>
    public const int BelievesTrustDelta = -8;
    public const int BelievesSuspicionDelta = 10;

    /// <summary>Nudge per owned hypothesis at Suspect band.</summary>
    public const int SuspectTrustDelta = -3;
    public const int SuspectSuspicionDelta = 4;

    /// <summary>Partial exoneration: dismissed band with enough refuting evidence.</summary>
    public const int DismissedTrustDelta = 5;
    public const int DismissedSuspicionDelta = -3;
    public const int DismissedRefutingNeeded = 2;

    /// <summary>Nudges are doubled during a Manhunt.</summary>
    public const int ManhuntNudgeMultiplier = 2;

    /// <summary>Supporting evidence required on the hypothesis before an arrest.</summary>
    public const int ArrestEvidenceNeeded = 2;

    /// <summary>
    /// Per-officer reading of one evidence item for one suspect hypothesis.
    /// A pure function of (runSeed, officer, evidence, subject): two officers can read the
    /// same evidence differently — the "independent officers" exit criterion — deterministically.
    /// </summary>
    public static EvidenceReading Reading(int roll0To99) =>
        roll0To99 < 70 ? EvidenceReading.Supports :
        roll0To99 < 90 ? EvidenceReading.Inconclusive :
        EvidenceReading.Refutes;
}

/// <summary>How one officer reads one evidence item against one suspect. Never touches ground truth.</summary>
public enum EvidenceReading
{
    Supports,
    Inconclusive,
    Refutes,
}
