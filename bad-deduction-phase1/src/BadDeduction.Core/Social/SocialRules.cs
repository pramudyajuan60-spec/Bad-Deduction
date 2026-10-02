namespace BadDeduction.Social;

/// <summary>Trust bands from design §9.</summary>
public enum TrustBand { Hostile, Suspicious, Neutral, Friendly, Trusted, ExtremelyLoyal }

/// <summary>The police escalation ladder from design §10, from high trust down to very low trust.</summary>
public enum PoliceStance { Cooperation, Questioning, IndependentVerification, SuspicionOfSubject }

/// <summary>
/// Tuning constants for the social layer, in one place. All integer. These are candidates to move into
/// data/ once balancing starts (design §61); they are constants for now because nothing else reads them.
/// </summary>
public static class SocialRules
{
    public const int Min = 0;
    public const int Max = 100;

    /// <summary>Design §10: police trust toward Lumiel at the start of a run.</summary>
    public const int PoliceStartingTrust = 90;

    // Police ladder floors (inclusive): trust >= 70 cooperates, >= 45 questions, >= 25 verifies on its own,
    // anything lower treats the subject as a suspect.
    public const int CooperationFloor = 70;
    public const int QuestioningFloor = 45;
    public const int VerificationFloor = 25;

    public static readonly RelationshipAxis[] AllAxes = Enum.GetValues<RelationshipAxis>();

    public static int Clamp(long value) => (int)Math.Clamp(value, Min, Max);

    public static TrustBand BandOf(int trust) => Clamp(trust) switch
    {
        <= 20 => TrustBand.Hostile,
        <= 40 => TrustBand.Suspicious,
        <= 60 => TrustBand.Neutral,
        <= 75 => TrustBand.Friendly,
        <= 90 => TrustBand.Trusted,
        _ => TrustBand.ExtremelyLoyal,
    };

    public static PoliceStance StanceOf(int trust)
    {
        var t = Clamp(trust);
        if (t >= CooperationFloor) return PoliceStance.Cooperation;
        if (t >= QuestioningFloor) return PoliceStance.Questioning;
        if (t >= VerificationFloor) return PoliceStance.IndependentVerification;
        return PoliceStance.SuspicionOfSubject;
    }

    /// <summary>
    /// How far an axis relaxes toward its resting value each day. Only volatile emotional axes calm down
    /// with time. Trust, respect, loyalty, affection and influence move only through events, so a
    /// betrayal is not forgotten by the calendar and police trust does not erode on its own.
    /// </summary>
    public static int DailyStep(RelationshipAxis axis) => axis switch
    {
        RelationshipAxis.Fear => 3,
        RelationshipAxis.Suspicion => 2,
        RelationshipAxis.Resentment => 1,
        _ => 0,
    };
}
