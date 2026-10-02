namespace BadDeduction.Social;

/// <summary>Categorical tag for the graph view; the numeric axes below carry the actual attitudes.</summary>
public enum RelationshipKind { Family, Friend, Colleague, Rival, Acquaintance }

/// <summary>
/// The eight numeric axes of a directed relationship (design §16). All values are integers 0-100 so the
/// simulation stays deterministic across platforms. The declaration order is load-bearing: arrays of
/// axis values (see <see cref="SocialBaselines"/>) are indexed by <c>(int)axis</c>.
/// </summary>
public enum RelationshipAxis { Trust, Fear, Respect, Loyalty, Suspicion, Influence, Affection, Resentment }

/// <summary>
/// A directed edge. Every relationship is stored as two edges (A→B and B→A) so each side can diverge.
/// All axes describe <see cref="From"/>'s attitude toward <see cref="To"/>:
/// how much From trusts, fears, respects, is loyal to, suspects, is swayed by (Influence = To's pull over
/// From), is fond of, and resents To. Plain data: only auto-properties, because saves are strict.
/// </summary>
public sealed class RelationshipEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public RelationshipKind Kind { get; set; }

    public int Trust { get; set; }
    public int Fear { get; set; }
    public int Respect { get; set; }
    public int Loyalty { get; set; }
    public int Suspicion { get; set; }
    public int Influence { get; set; }
    public int Affection { get; set; }
    public int Resentment { get; set; }
}

public static class RelationshipEdgeExtensions
{
    public static int GetAxis(this RelationshipEdge e, RelationshipAxis axis) => axis switch
    {
        RelationshipAxis.Trust => e.Trust,
        RelationshipAxis.Fear => e.Fear,
        RelationshipAxis.Respect => e.Respect,
        RelationshipAxis.Loyalty => e.Loyalty,
        RelationshipAxis.Suspicion => e.Suspicion,
        RelationshipAxis.Influence => e.Influence,
        RelationshipAxis.Affection => e.Affection,
        RelationshipAxis.Resentment => e.Resentment,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>Sets an axis, clamping into 0-100.</summary>
    public static void SetAxis(this RelationshipEdge e, RelationshipAxis axis, long value)
    {
        var v = SocialRules.Clamp(value);
        switch (axis)
        {
            case RelationshipAxis.Trust: e.Trust = v; break;
            case RelationshipAxis.Fear: e.Fear = v; break;
            case RelationshipAxis.Respect: e.Respect = v; break;
            case RelationshipAxis.Loyalty: e.Loyalty = v; break;
            case RelationshipAxis.Suspicion: e.Suspicion = v; break;
            case RelationshipAxis.Influence: e.Influence = v; break;
            case RelationshipAxis.Affection: e.Affection = v; break;
            case RelationshipAxis.Resentment: e.Resentment = v; break;
            default: throw new ArgumentOutOfRangeException(nameof(axis));
        }
    }
}
