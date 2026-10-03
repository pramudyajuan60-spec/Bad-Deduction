using BadDeduction.AI;

namespace BadDeduction.Social;

/// <summary>
/// Phase 14: the order taxonomy — how each <see cref="OrderKind"/> maps to an
/// <see cref="ActionRequest"/> for <see cref="ComplianceEvaluator"/>. Risk and moral
/// cost are integers 0-100; rationale per kind is documented on each constant.
/// <para/>
/// The table is deliberately conservative: even a beloved (Trust 100) Gullible NPC
/// cannot be talked into murder — the evaluator's factor weights (ADR-014) cap the
/// relationship's pull at 98 + 15 (tier bonus) = 113, while Attack pushes back
/// 85·(risk, softened only by courage) + 90·(morality, felt more by the honest) —
/// always above 113. That is the user's "jangan semua yang dikatakan player" rule,
/// encoded as arithmetic and pinned by test.
/// </summary>
public static class ComplianceRules
{
    /// <summary>
    /// How dangerous each order kind is for the person who would carry it out (0-100).
    /// GoTo 20 (walking somewhere is mildly risky at night); Buy 10; Follow 20;
    /// Wait 5; Lie 25 (getting caught lying burns); Steal 45 (guards, punishment);
    /// Attack 85 (lethal violence endangers the attacker).
    /// </summary>
    public static int RiskOf(OrderKind kind) => kind switch
    {
        OrderKind.GoTo => 20,
        OrderKind.Buy => 10,
        OrderKind.Steal => 45,
        OrderKind.Lie => 25,
        OrderKind.Attack => 85,
        OrderKind.Follow => 20,
        OrderKind.Wait => 5,
        _ => 0,
    };

    /// <summary>
    /// How wrong each order kind feels (0-100); honest characters feel this more
    /// (ADR-014). Wait 0; GoTo 5; Buy 5; Follow 15; Lie 40; Steal 55; Attack 90.
    /// </summary>
    public static int MoralCostOf(OrderKind kind) => kind switch
    {
        OrderKind.GoTo => 5,
        OrderKind.Buy => 5,
        OrderKind.Steal => 55,
        OrderKind.Lie => 40,
        OrderKind.Attack => 90,
        OrderKind.Follow => 15,
        OrderKind.Wait => 0,
        _ => 0,
    };

    /// <summary>Human-readable label for prompts and event data.</summary>
    public static string LabelOf(DetectedOrder order) => order.Kind switch
    {
        OrderKind.GoTo => $"go to {order.TargetText ?? "somewhere"}" +
            (order.TimeMinute.HasValue ? $" ({FmtTime(order.TimeMinute.Value)})" : ""),
        OrderKind.Buy => $"buy {order.TargetText ?? "something"}",
        OrderKind.Steal => $"steal {order.TargetText ?? "something"}",
        OrderKind.Lie => $"lie about {order.TargetText ?? "something"}",
        OrderKind.Attack => $"attack {order.TargetText ?? "someone"}",
        OrderKind.Follow => "follow someone",
        OrderKind.Wait => "wait here",
        _ => "do something",
    };

    /// <summary>
    /// Builds the <see cref="ActionRequest"/> the evaluator scores. HarmsPeople carries
    /// the attack victim's character id when the target resolved to a person; goal
    /// conflicts are a documented extension point (Phase 15+: e.g. stealing vs an
    /// honest living).
    /// </summary>
    public static ActionRequest ToActionRequest(DetectedOrder order, string? targetCharacterId) =>
        new()
        {
            Label = LabelOf(order),
            Risk = RiskOf(order.Kind),
            MoralCost = MoralCostOf(order.Kind),
            HarmsPeople = targetCharacterId is null
                ? Array.Empty<string>()
                : new[] { targetCharacterId },
        };

    private static string FmtTime(long totalMinutes)
    {
        var h = (int)((totalMinutes % 1440) / 60);
        var m = (int)((totalMinutes % 1440) % 60);
        return $"{h:00}:{m:00}";
    }
}
