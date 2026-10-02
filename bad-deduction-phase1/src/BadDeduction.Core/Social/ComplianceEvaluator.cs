using BadDeduction.Characters;
using BadDeduction.Core;

namespace BadDeduction.Social;

/// <summary>What is being asked of someone. Describes the request, not the requester.</summary>
public sealed class ActionRequest
{
    public string Label { get; init; } = "";
    /// <summary>0-100: how dangerous the action is for the person who would do it.</summary>
    public int Risk { get; init; }
    /// <summary>0-100: how wrong the action feels (honest people feel this more).</summary>
    public int MoralCost { get; init; }
    /// <summary>People whose interests the action damages. Loyalty to or fear of them weighs against doing it.</summary>
    public IReadOnlyList<string> HarmsPeople { get; init; } = Array.Empty<string>();
    /// <summary>Goal definition ids (see data/goals.json) the action would undermine for the decider.</summary>
    public IReadOnlyList<string> UndermineGoals { get; init; } = Array.Empty<string>();
}

/// <summary>One signed contribution to a decision: positive pushes toward doing it, negative against.</summary>
public sealed record ComplianceFactor(string Name, int Value);

public sealed record ComplianceDecision(bool Complies, int Score, IReadOnlyList<ComplianceFactor> Factors)
{
    /// <summary>The factor that mattered most in the direction of the outcome (null when there were no factors).</summary>
    public ComplianceFactor? Decisive => Factors.Count == 0 ? null
        : Complies ? Factors.MaxBy(f => f.Value) : Factors.MinBy(f => f.Value);
}

/// <summary>
/// Design §9: "Trust must NOT be treated as the only factor." The decision is the sum of named, signed factors
/// (so it can be explained), and the weights guarantee that trust at 100 can still be outweighed:
/// the strongest possible pull from the relationship is 98, while a maximally risky and immoral request
/// pushes back by more than that. Pure and deterministic: evaluating never changes state.
/// </summary>
public sealed class ComplianceEvaluator
{
    private readonly GameState _state;
    private readonly SocialService _social;

    public ComplianceEvaluator(GameState state, SocialService social)
    {
        _state = state;
        _social = social;
    }

    /// <param name="extraFactors">
    /// Hook for systems that arrive later (Phase 4 beliefs, evidence and emotional state): positive values pull toward
    /// compliance, negative values push against it. They are reported alongside the built-in factors.
    /// </param>
    public ComplianceDecision Evaluate(string deciderId, string requesterId, ActionRequest request, IEnumerable<ComplianceFactor>? extraFactors = null)
    {
        var rel = _social.View(deciderId, requesterId);
        var profile = _state.World.Profiles.GetValueOrDefault(deciderId);
        int courage = profile?.Personality.Courage ?? 50;
        int honesty = profile?.Personality.Honesty ?? 50;
        var factors = new List<ComplianceFactor>();

        void Add(string name, int value) { if (value != 0) factors.Add(new ComplianceFactor(name, value)); }

        // Pull from the relationship (weights sum to 110, so each term is axis * weight / 110; max total 98 after rounding).
        Add("trust", rel.Trust * 30 / 110);
        Add("loyalty", rel.Loyalty * 25 / 110);
        Add("influence", rel.Influence * 15 / 110);
        Add("fear_of_requester", rel.Fear * 20 / 110);   // coercion: people obey those they fear
        Add("affection", rel.Affection * 10 / 110);
        Add("respect", rel.Respect * 10 / 110);
        Add("suspicion", -(rel.Suspicion * 20 / 100));
        Add("resentment", -(rel.Resentment * 10 / 100));

        // Resistance from the request itself and from the decider's own character.
        Add("risk", -(Clamp(request.Risk) * (120 - courage) / 120));
        Add("morality", -(Clamp(request.MoralCost) * (30 + honesty * 70 / 100) / 100));

        // Loyalty to, affection for, or fear of someone the action would hurt: the strongest single pull counts.
        string? protectedId = null;
        var strongest = 0;
        foreach (var id in request.HarmsPeople.OrderBy(i => i, StringComparer.Ordinal))
        {
            if (id == deciderId) continue;
            var other = _social.View(deciderId, id);
            var pressure = (other.Loyalty * 40 + other.Affection * 20 + other.Fear * 40) / 100;
            if (pressure > strongest) { strongest = pressure; protectedId = id; }
        }
        if (protectedId is not null) Add($"protects:{protectedId}", -strongest);

        // The decider's own goals the action would undermine: the highest-priority conflict counts.
        if (profile is not null)
        {
            Goal? worst = null;
            foreach (var g in profile.Goals)
                if (request.UndermineGoals.Contains(g.DefinitionId) && (worst is null || g.Priority > worst.Priority)) worst = g;
            if (worst is not null) Add($"goal:{worst.DefinitionId}", -(worst.Priority * 80 / 100));
        }

        if (extraFactors is not null)
            foreach (var f in extraFactors) Add(f.Name, f.Value);

        var score = factors.Sum(f => f.Value);
        return new ComplianceDecision(score > 0, score, factors);
    }

    private static int Clamp(int v) => SocialRules.Clamp(v);
}
