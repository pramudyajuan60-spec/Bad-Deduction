using System.Text.Json.Serialization;
using BadDeduction.Cognition;
using BadDeduction.Core;

namespace BadDeduction.Investigation;

/// <summary>How badly a statement clashes with an earlier one (or with surveillance).</summary>
public enum ContradictionSeverity
{
    Subtle,
    Blatant,
}

/// <summary>
/// Something a character SAID — testimony, not ground truth. A claim can be wrong, vague, or a
/// lie; that is exactly what contradiction detection is for. Plain data; all behaviour lives in
/// <see cref="InvestigationService"/>.
/// </summary>
public sealed class Statement
{
    public string Id { get; set; } = "";
    public string SpeakerId { get; set; } = "";
    /// <summary>
    /// One of: "event:{eventId}", "person:{characterId}", "whereabouts:{minute}".
    /// See <see cref="InvestigationRules"/> for the topic grammar.
    /// </summary>
    public string Topic { get; set; } = "";
    /// <summary>
    /// What the speaker said. Whereabouts claims are structured
    /// ("at:{location}@{minute}[#{detail}]" or "traveling:{from}&gt;{to}@{minute}");
    /// other topics carry free text (usually the speaker's own memory summary).
    /// </summary>
    public string Claim { get; set; } = "";
    /// <summary>The case this statement was filed under, if any. Null for free-floating statements.</summary>
    public string? CrimeId { get; set; }
    public long Timestamp { get; set; }
    /// <summary>The interview / dialogue event that produced this statement, if any.</summary>
    public long? SourceEventId { get; set; }
}

/// <summary>
/// One detected incompatibility between two statements (or between a statement and
/// surveillance). <see cref="Flagged"/> is difficulty-gated: below the bar the clash is
/// recorded but not raised (no event), so a Genius run and an Easy run over the same
/// testimony produce measurably different detection rates — the Phase 8 exit criterion.
/// </summary>
public sealed class Contradiction
{
    public string Id { get; set; } = "";
    public string StatementId { get; set; } = "";
    /// <summary>
    /// The earlier statement this clashes with. Null when the clash is against surveillance
    /// rather than another statement.
    /// </summary>
    public string? AgainstStatementId { get; set; }
    public ContradictionSeverity Severity { get; set; }
    public string Reason { get; set; } = "";
    public bool Flagged { get; set; }
    public long Timestamp { get; set; }
}

/// <summary>
/// One working theory on the investigation board. Confidence is DERIVED from the attached
/// evidence counts via <see cref="CognitionRules.BeliefConfidence"/> — the same integer math
/// as character beliefs, so a hypothesis is always explainable as "N for, M against".
/// False evidence can support a hypothesis (that is the game); attaching it never changes
/// the evidence's immutable authenticity (ADR-035).
/// </summary>
public sealed class Hypothesis
{
    public string Id { get; set; } = "";
    public string PropositionId { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// Phase 9: who owns this theory. Null = the shared board (player / investigator);
    /// otherwise an officer id — each officer keeps their own case file, so two officers
    /// can hold different confidences about the same suspect.
    /// </summary>
    public string? OwnerId { get; set; }
    public List<string> SupportingEvidenceIds { get; set; } = new();
    public List<string> RefutingEvidenceIds { get; set; } = new();
    public long UpdatedAt { get; set; }

    /// <summary>Derived, never stored: neutral 50, moved 8 points per net evidence item.</summary>
    [JsonIgnore]
    public int Confidence => CognitionRules.BeliefConfidence(SupportingEvidenceIds.Count, RefutingEvidenceIds.Count);

    /// <summary>Derived, never stored.</summary>
    [JsonIgnore]
    public BeliefBand Band => CognitionRules.BandOf(Confidence);
}

/// <summary>Phase 8: the investigation subsystem's plain-data state. Mutated only by InvestigationService.</summary>
public sealed class InvestigationState
{
    public long NextStatementId { get; set; } = 1;
    public long NextHypothesisId { get; set; } = 1;
    public long NextContradictionId { get; set; } = 1;

    public Dictionary<string, Statement> Statements { get; set; } = new();
    public Dictionary<string, Hypothesis> Hypotheses { get; set; } = new();
    public Dictionary<string, Contradiction> Contradictions { get; set; } = new();
}
