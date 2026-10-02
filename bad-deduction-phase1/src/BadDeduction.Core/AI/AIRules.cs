namespace BadDeduction.AI;

/// <summary>All Phase 6 tuning constants in one place. Integers only (ADR-002/ADR-014).</summary>
public static class AIRules
{
    /// <summary>Longest reply the validator accepts; longer is rejected, not truncated.</summary>
    public const int MaxReplyLength = 500;

    /// <summary>Longest single fact the validator accepts.</summary>
    public const int MaxFactLength = 140;

    /// <summary>Longest relationship note the validator accepts.</summary>
    public const int MaxRelationshipNoteLength = 200;

    /// <summary>Most new facts per exchange; extras are rejected.</summary>
    public const int MaxNewFacts = 5;

    /// <summary>Trust/suspicion deltas are clamped to ±this, never rejected for overflow.</summary>
    public const int MaxDeltaMagnitude = 20;

    /// <summary>Utterances longer than this are truncated before reaching the provider.</summary>
    public const int MaxUtteranceLength = 1000;

    /// <summary>Exchanges allowed per conversation before the budget is exhausted (risk 4).</summary>
    public const int MaxExchangesPerConversation = 10;
}
