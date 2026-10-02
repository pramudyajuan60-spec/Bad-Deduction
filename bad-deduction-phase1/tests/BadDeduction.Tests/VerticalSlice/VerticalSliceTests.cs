using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Cognition;
using BadDeduction.Slice;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests.VerticalSlice;

/// <summary>
/// Phase 12: the vertical slice — "The Burning at Saint Velmont". Assembly, beats,
/// balance metrics and the acceptance criteria, all headless and deterministic.
/// </summary>
public sealed class VerticalSliceTests
{
    private static ContentDatabase Content() => TestSupport.LoadContent();

    // ------------------------------------------------------------------ assembly

    [Fact]
    public void Slice_assembles_22_characters_and_a_cathedral_arson()
    {
        var run = SliceScenario.Build(7, Campaign.Lumiel, Difficulty.Medium, Content());
        var chars = run.Session.State.World.Characters;
        Assert.Equal(22, chars.Count);
        Assert.Equal(17, chars.Values.Count(c => c.Kind == CharacterKind.Civilian));
        Assert.Equal(5, chars.Values.Count(c => c.Kind == CharacterKind.Police));

        var crime = run.Session.Crime.GetCrime(run.CrimeId);
        Assert.Equal("arson_fatal", crime.DefinitionId);
        Assert.Equal("loc_cathedral", crime.LocationId);
        Assert.True(crime.Fatal);
        Assert.Equal(new GameTime(crime.OccurredAt).Day, 1);

        var victim = chars[run.VictimId];
        Assert.False(victim.IsAlive);
        Assert.NotEqual(run.Session.State.Player.CharacterId, run.VictimId);
    }

    [Fact]
    public void Slice_locations_form_a_connected_subgraph()
    {
        var db = Content();
        var ids = SliceScenario.SliceLocationIds;
        Assert.Equal(8, ids.Count);
        // BFS from the cathedral over the content graph, restricted to the slice set.
        var seen = new HashSet<string>(StringComparer.Ordinal) { SliceScenario.CathedralId };
        var queue = new Queue<string>(seen);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var (to, _) in db.Neighbors(cur))
            {
                if (ids.Contains(to, StringComparer.Ordinal) && seen.Add(to))
                    queue.Enqueue(to);
            }
        }
        Assert.Equal(ids.Count, seen.Count);
    }

    [Fact]
    public void Player_knows_their_own_side_and_the_rival_is_seeded()
    {
        var run = SliceScenario.Build(7, Campaign.Lumiel, Difficulty.Medium, Content());
        Assert.Equal(Campaign.Lumiel, run.Session.State.Meta.Campaign);
        var holder = SliceMetrics.MalvrHolderOf(run);
        Assert.NotEqual(run.Session.State.Player.CharacterId, holder);
        Assert.True(run.Session.State.World.Characters[holder].IsAlive);
    }

    // ------------------------------------------------------------------ bot & beats

    [Fact]
    public void Bot_run_is_deterministic()
    {
        var a = SliceMetrics.RunSeed(11, Content());
        var b = SliceMetrics.RunSeed(11, Content());
        Assert.Equal(a.Metric.FinalHash, b.Metric.FinalHash);
    }

    [Fact]
    public void Bot_hits_all_seven_beats()
    {
        // Seeds vetted to exercise the deception path (see VERTICAL_SLICE.md).
        foreach (var seed in new ulong[] { 4, 11, 20 })
        {
            var (run, _) = SliceMetrics.RunSeed(seed, Content());
            var beats = SliceBeats.Check(run);
            Assert.True(SliceBeats.AllHit(beats),
                $"seed {seed} missed: {string.Join(", ", beats.Where(b => !b.Hit).Select(b => $"{b.Name}({b.Detail})"))}");
        }
    }

    [Fact]
    public void Solvable_definition_marks_the_slice_solvable()
    {
        var (run, _) = SliceMetrics.RunSeed(11, Content());
        Assert.True(SliceMetrics.IsSolvable(run));
    }

    // ------------------------------------------------------------------ difficulty gap

    [Fact]
    public void Genius_flags_more_contradictions_than_easy_on_a_fixed_seed()
    {
        const ulong seed = 13; // vetted: the seeded deceiver lies at T but not T-90
        // (see VERTICAL_SLICE.md): the lie clashes with the truthful T-90 alibi inside
        // Genius's 120-minute window but outside Easy's 60-minute window.
        var easyCount = CountFlaggedAfterInterrogations(seed, Difficulty.Easy);
        var geniusCount = CountFlaggedAfterInterrogations(seed, Difficulty.Genius);
        Assert.True(geniusCount > easyCount,
            $"expected a Genius/Easy gap: easy={easyCount} genius={geniusCount}");
    }

    private static int CountFlaggedAfterInterrogations(ulong seed, Difficulty difficulty)
    {
        var run = SliceScenario.Build(seed, Campaign.Lumiel, difficulty, Content());
        var s = run.Session;
        var holder = SliceMetrics.MalvrHolderOf(run);
        var player = s.State.Player.CharacterId;
        // Two alibi probes 90 minutes apart: inside Genius's 120-minute window,
        // outside Easy's 60-minute window.
        s.Investigate.Interrogate(player, holder,
            BadDeduction.Investigation.InvestigationRules.WhereaboutsTopic(run.IncidentMinute), run.CrimeId);
        s.Investigate.Interrogate(player, holder,
            BadDeduction.Investigation.InvestigationRules.WhereaboutsTopic(run.IncidentMinute - 90), run.CrimeId);
        return s.State.Investigation.Contradictions.Values.Count(c => c.Flagged);
    }

    // ------------------------------------------------------------------ metrics

    [Fact]
    public void Metrics_over_200_seeds_meet_the_acceptance_bars()
    {
        // ~4-6 minutes on this machine; the bar is < 10 minutes (see VERTICAL_SLICE.md).
        var seeds = Enumerable.Range(1, 200).Select(i => (ulong)i).ToList();
        var (summary, _) = SliceMetrics.Run(seeds, Content());

        Assert.True(summary.SolvableRate >= 0.95,
            $"solvable rate {summary.SolvableRate:P1} below the 95% bar");
        Assert.True(summary.MaxShareVsUniform(eligibleCount: 21) <= 2.0,
            $"identity dominance {summary.MaxShareVsUniform(21):F2}x above the 2x bar");
        Assert.Equal(0, summary.TotalKnowledgeViolations);
        Assert.True(summary.TotalMs < 10 * 60 * 1000,
            $"200-seed run took {summary.TotalMs}ms, over the 10-minute CI budget");

        TestContext.MetricsSummary = summary;
    }

    [Fact]
    public void Perf_single_seed_seven_days_stays_fast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SliceMetrics.RunSeed(99, Content());
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 30_000,
            $"single-seed run took {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Carries the measured numbers into the docs/report step.</summary>
    internal static class TestContext
    {
        public static SliceMetricSummary? MetricsSummary;
    }
}
