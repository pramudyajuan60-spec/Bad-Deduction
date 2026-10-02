using System.Diagnostics;
using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Crime;

namespace BadDeduction.Slice;

public sealed record SeedMetric(
    ulong Seed,
    bool Solvable,
    string MalvrHolder,
    string FinalHash,
    int KnowledgeViolations,
    int FlaggedContradictions,
    long ElapsedMs,
    int EventCount);

public sealed record SliceMetricSummary(
    int Seeds,
    int SolvableCount,
    IReadOnlyDictionary<string, int> MalvrCounts,
    int TotalKnowledgeViolations,
    double AvgMsPerSeed,
    long TotalMs)
{
    public double SolvableRate => Seeds == 0 ? 0 : (double)SolvableCount / Seeds;

    /// <summary>
    /// Highest holder frequency relative to the uniform share (1/eligible).
    /// The acceptance bar is ≤ 2.0 (no dominant NPC).
    /// </summary>
    public double MaxShareVsUniform(int eligibleCount)
    {
        if (Seeds == 0 || MalvrCounts.Count == 0) return 0;
        var uniform = (double)Seeds / eligibleCount;
        return MalvrCounts.Values.Max() / uniform;
    }
};

/// <summary>
/// Phase 12: headless balance metrics for the vertical slice. Runs the scenario + bot
/// over many seeds and scores the acceptance criteria. The scenario runs at Medium
/// difficulty unless a metric says otherwise.
/// </summary>
public static class SliceMetrics
{
    public static (SliceRun Run, SeedMetric Metric) RunSeed(ulong seed, ContentDatabase content, Campaign campaign = Campaign.Lumiel)
    {
        var sw = Stopwatch.StartNew();
        var run = SliceScenario.Build(seed, campaign, Difficulty.Medium, content);
        new SliceBot(run).PlayAll();
        sw.Stop();
        var metric = new SeedMetric(
            Seed: seed,
            Solvable: IsSolvable(run),
            MalvrHolder: MalvrHolderOf(run),
            FinalHash: run.Session.StateHash(),
            KnowledgeViolations: CountKnowledgeViolations(run.Session),
            FlaggedContradictions: run.Session.State.Investigation.Contradictions.Values.Count(c => c.Flagged),
            ElapsedMs: sw.ElapsedMilliseconds,
            EventCount: run.Session.State.EventLog.Events.Count);
        return (run, metric);
    }

    public static (SliceMetricSummary Summary, IReadOnlyList<SeedMetric> PerSeed) Run(
        IReadOnlyList<ulong> seeds, ContentDatabase content)
    {
        var sw = Stopwatch.StartNew();
        var perSeed = new List<SeedMetric>();
        var malvrCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var solvable = 0;
        var violations = 0;
        foreach (var seed in seeds)
        {
            var (_, metric) = RunSeed(seed, content);
            perSeed.Add(metric);
            if (metric.Solvable) solvable++;
            violations += metric.KnowledgeViolations;
            malvrCounts[metric.MalvrHolder] = malvrCounts.GetValueOrDefault(metric.MalvrHolder) + 1;
        }
        sw.Stop();
        var summary = new SliceMetricSummary(
            Seeds: seeds.Count,
            SolvableCount: solvable,
            MalvrCounts: malvrCounts,
            TotalKnowledgeViolations: violations,
            AvgMsPerSeed: seeds.Count == 0 ? 0 : (double)sw.ElapsedMilliseconds / seeds.Count,
            TotalMs: sw.ElapsedMilliseconds);
        return (summary, perSeed);
    }

    /// <summary>
    /// OMNISCIENT GRADER (test-harness only — never player-facing): scores whether a fair
    /// path to solving exists using ground truth. Solvable = scene discovered by end of
    /// Day 2 AND at least 2 authentic evidence items discovered AND at least one living
    /// witness at the end. (Two authentic items attached as supporting reach the Believes
    /// band at 50+8·2=66, so the theory CAN harden.)
    /// </summary>
    public static bool IsSolvable(SliceRun run)
    {
        var s = run.Session;
        var crime = s.Crime.GetCrime(run.CrimeId);
        var scene = s.Crime.GetScene(crime.SceneId);
        if (!scene.IsDiscovered || scene.DiscoveredAt > GameTime.At(3, 0).TotalMinutes)
            return false;
        var authenticFound = s.State.Crime.Evidence.Values.Count(e =>
            e.SceneId == scene.Id && e.Discovered && e.Authenticity == Authenticity.Authentic);
        if (authenticFound < 2)
            return false;
        return s.Crime.GetWitnesses(run.CrimeId)
            .Any(w => s.State.World.Characters.TryGetValue(w, out var c) && c.IsAlive);
    }

    /// <summary>Grader-only truth read, clearly marked as such.</summary>
    public static string MalvrHolderOf(SliceRun run)
    {
        run.Session.Debug.Enabled = true;
        try
        {
            return run.Session.Debug.GetTruth().CharacterWithRole(HiddenRole.Malvr)
                ?? throw new InvalidOperationException("No Malvr holder assigned.");
        }
        finally
        {
            run.Session.Debug.Enabled = false;
        }
    }

    /// <summary>
    /// Knowledge-boundary audit: every KnownEvents entry must have a matching memory
    /// recorded through the sanctioned gate (Perceive/TellRumor). Returns the violation
    /// count — the acceptance bar is zero.
    /// </summary>
    public static int CountKnowledgeViolations(GameSession s)
    {
        var cog = s.State.Cognition;
        var violations = 0;
        foreach (var (charId, known) in cog.KnownEvents)
        {
            cog.Memories.TryGetValue(charId, out var list);
            foreach (var eventId in known)
            {
                if (list is null || !list.Any(m => m.EventId == eventId))
                    violations++;
            }
        }
        return violations;
    }
}
