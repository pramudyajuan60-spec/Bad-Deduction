using BadDeduction.Characters;

namespace BadDeduction.Social;

/// <summary>
/// Where a relationship starts and where its volatile axes settle back to. Pure function of the
/// relationship kind and the two people's personalities (no RNG), so the same cast always gets the same
/// relationships and drift targets can be recomputed any time without storing them.
/// </summary>
public static class SocialBaselines
{
    //                                    Trust Fear Resp Loyal Susp Infl Affec Resent
    private static readonly int[] Family   = { 70,   5,  60,  70,   5,  30,  70,   5 };
    private static readonly int[] Friend   = { 60,   3,  50,  50,   5,  25,  60,   3 };
    private static readonly int[] Colleague= { 35,   8,  40,  20,  12,  20,  25,   8 };
    // Design §9: an ordinary NPC starts at Trust 20. Strangers (no edge) look exactly like acquaintances,
    // so a relationship created on first contact never "jumps" from what View() reported before.
    private static readonly int[] Acquaint = { 20,   5,  30,   5,  15,  10,  15,   5 };
    private static readonly int[] Rival    = { 10,  15,  30,   0,  45,  15,   5,  40 };

    /// <summary>Resting values indexed by <c>(int)RelationshipAxis</c>. <paramref name="kind"/> null means "no edge yet".</summary>
    public static int[] Resting(RelationshipKind? kind, Personality? from, Personality? to)
    {
        var v = (int[])(kind switch
        {
            RelationshipKind.Family => Family,
            RelationshipKind.Friend => Friend,
            RelationshipKind.Colleague => Colleague,
            RelationshipKind.Rival => Rival,
            RelationshipKind.Acquaintance or null => Acquaint,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }).Clone();

        // Missing personalities (hand-built test worlds, migrated saves) count as perfectly average (50).
        int fAgree = from?.Agreeableness ?? 50, fNeuro = from?.Neuroticism ?? 50;
        int tHonest = to?.Honesty ?? 50, tConsc = to?.Conscientiousness ?? 50;
        int tExtra = to?.Extraversion ?? 50, tAgree = to?.Agreeableness ?? 50;

        v[(int)RelationshipAxis.Trust] += (fAgree - 50) / 10 + (tHonest - 50) / 10;      // trusting people, honest-seeming targets
        v[(int)RelationshipAxis.Suspicion] += (fNeuro - 50) / 10;                         // anxious people suspect more
        v[(int)RelationshipAxis.Respect] += (tConsc - 50) / 10;                           // diligent people are respected
        v[(int)RelationshipAxis.Influence] += (tExtra - 50) / 5 + (fAgree - 50) / 10;     // forceful targets, pliable deciders
        v[(int)RelationshipAxis.Affection] += (tAgree - 50) / 10;                         // agreeable people are liked
        v[(int)RelationshipAxis.Resentment] += (50 - fAgree) / 10;                        // disagreeable people hold grudges

        for (var i = 0; i < v.Length; i++) v[i] = SocialRules.Clamp(v[i]);
        return v;
    }

    public static void Apply(RelationshipEdge edge, int[] values)
    {
        foreach (var axis in SocialRules.AllAxes) edge.SetAxis(axis, values[(int)axis]);
    }
}
