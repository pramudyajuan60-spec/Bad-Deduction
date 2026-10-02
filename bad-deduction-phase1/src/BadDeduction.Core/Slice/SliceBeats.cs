using BadDeduction.Core;
using BadDeduction.Cognition;

namespace BadDeduction.Slice;

public sealed record BeatResult(string Name, bool Hit, string Detail);

/// <summary>
/// Phase 12: the seven pacing beats of "The Burning at Saint Velmont" as CHECKS, not scripts.
/// Each beat names a day boundary and an observable condition; the simulation must produce
/// them emergently. If a beat systematically fails, the answer is tuning (SliceMetrics),
/// never scripting.
/// </summary>
public static class SliceBeats
{
    public static IReadOnlyList<BeatResult> Check(SliceRun run)
    {
        var s = run.Session;
        var results = new List<BeatResult>();
        var crime = s.Crime.GetCrime(run.CrimeId);
        var scene = s.Crime.GetScene(crime.SceneId);

        // Day 1 — INCIDENT: the cathedral burns.
        results.Add(new BeatResult("Incident",
            crime.DefinitionId == SliceScenario.CrimeDefinitionId
                && crime.LocationId == SliceScenario.CathedralId
                && new GameTime(crime.OccurredAt).Day == 1,
            $"crime={crime.Id} def={crime.DefinitionId} at={crime.LocationId}"));

        // Day 2 — INVESTIGATION: the scene is found and witnesses are questioned.
        var interviewedByD2 = s.Events.Query(WorldEventTypes.Interviewed, to: GameTime.At(3, 0)).Count();
        var discoveredByD2 = scene.IsDiscovered && scene.DiscoveredAt! <= GameTime.At(3, 0).TotalMinutes;
        results.Add(new BeatResult("Investigation",
            discoveredByD2 && interviewedByD2 >= 2,
            $"discovered={discoveredByD2} interviews={interviewedByD2}"));

        // Day 3 — CONTRADICTIONS: at least one flagged clash on the record.
        var contrasByD3 = s.State.Investigation.Contradictions.Values
            .Count(c => c.Flagged && c.Timestamp < GameTime.At(4, 0).TotalMinutes);
        results.Add(new BeatResult("Contradictions", contrasByD3 >= 1, $"flagged={contrasByD3}"));

        // Day 4 — SOCIAL CHAIN REACTION: the rumor mill is turning.
        var rumorsByD4 = s.Events.Query(WorldEventTypes.RumorSpread, to: GameTime.At(5, 0)).Count();
        results.Add(new BeatResult("ChainReaction", rumorsByD4 >= 3, $"rumors={rumorsByD4}"));

        // Day 5 — HIDDEN CONFLICT: a genius has acted strategically. Rumors are only
        // ever spread by hidden-agenda actions (TellRumor has no other callers), so a
        // rumor on the event log is an observable fingerprint of the hidden conflict.
        var rumorsByD5 = s.Events.Query(WorldEventTypes.RumorSpread, to: GameTime.At(6, 0)).Count();
        results.Add(new BeatResult("HiddenConflict", rumorsByD5 >= 1, $"rumors={rumorsByD5}"));

        // Day 6 — CONVERGENCE: a hypothesis hardens into belief.
        var believersByD6 = s.State.Investigation.Hypotheses.Values
            .Count(h => h.Band == BeliefBand.Believes && h.UpdatedAt < GameTime.At(7, 0).TotalMinutes);
        results.Add(new BeatResult("Convergence", believersByD6 >= 1, $"believes={believersByD6}"));

        // Day 7 — RESOLUTION: the arc completes with a believed theory on the board.
        var believersEnd = s.State.Investigation.Hypotheses.Values
            .Count(h => h.Band == BeliefBand.Believes);
        results.Add(new BeatResult("Resolution",
            s.State.TotalMinutes >= GameTime.At(8, 0).TotalMinutes && believersEnd >= 1,
            $"day={new GameTime(s.State.TotalMinutes).Day} believes={believersEnd}"));

        return results;
    }

    public static bool AllHit(IReadOnlyList<BeatResult> results) => results.All(r => r.Hit);
}
