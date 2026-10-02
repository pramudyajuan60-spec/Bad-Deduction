using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Investigation;

namespace BadDeduction.Slice;

/// <summary>
/// Phase 12: a deterministic, deliberately MEDIOCRE headless player for the vertical slice.
/// It exercises the full loop (move → discover → interview → interrogate → dialogue →
/// hypothesize → advance time) using ONLY player-legal services — the same calls the Godot
/// UI makes. It never reads hidden truth and never sees undisplayed state.
///
/// Fixed policy (documented so replays are exact):
/// - Day 1 (evening): go to the cathedral, look around until the scene is discovered,
///   examine every evidence item found there.
/// - Day 2: interview up to 3 witnesses about the incident; survey the cathedral;
///   propose the "it was arson" hypothesis and attach all discovered evidence as supporting.
/// - Days 3–4: interrogate every living NPC about their whereabouts at the incident minute
///   and cross-check each statement against surveillance; two short dialogue exchanges.
/// - Days 5–6: re-interview the witnesses; keep the city living.
/// - Day 7: final review, no new actions.
/// </summary>
public sealed class SliceBot
{
    private readonly SliceRun _run;
    private readonly GameSession _s;
    private readonly string _player;
    private string? _hypothesisId;

    public SliceBot(SliceRun run)
    {
        _run = run;
        _s = run.Session;
        _player = run.Session.State.Player.CharacterId;
    }

    public void PlayAll()
    {
        for (var day = 1; day <= 7; day++)
            PlayDay(day);
        // End exactly at the start of day 8: the full seven-day arc.
        AdvanceTo(8, 0);
    }

    public void PlayDay(int day)
    {
        switch (day)
        {
            case 1: PlayDayOne(); break;
            case 2: PlayDayTwo(); break;
            case 3:
            case 4: PlayInterrogationSweep(); break;
            case 5:
            case 6: PlayFollowUp(); break;
            case 7: break; // resolution day: review only
            default: throw new ArgumentOutOfRangeException(nameof(day));
        }
        AdvanceTo(day + 1, 0);
    }

    // ------------------------------------------------------------ day 1: the fire

    private void PlayDayOne()
    {
        MovePlayer(SliceScenario.CathedralId);
        _s.Crime.CheckDiscovery(_player);
        // The scene needs its discovery delay; look around again afterwards.
        AdvanceTo(1, 23);
        _s.Crime.CheckDiscovery(_player);

        var scene = _s.Crime.GetScene(_s.Crime.GetCrime(_run.CrimeId).SceneId);
        if (scene.IsDiscovered)
        {
            foreach (var ev in _s.Crime.EvidenceAtScene(scene.Id).OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                if (!ev.Discovered)
                    _s.Crime.DiscoverEvidence(_player, ev.Id);
            }
        }
    }

    // ------------------------------------------------------------ day 2: investigation

    private void PlayDayTwo()
    {
        var crime = _s.Crime.GetCrime(_run.CrimeId);
        var witnesses = _s.Crime.GetWitnesses(_run.CrimeId)
            .Where(w => w != _player)
            .OrderBy(w => w, StringComparer.Ordinal)
            .Take(3).ToList();
        foreach (var w in witnesses)
            _s.Investigate.Interview(_player, w, InvestigationRules.EventTopic(crime.IncidentEventId), _run.CrimeId);

        _s.Investigate.SurveyLocation(SliceScenario.CathedralId, _run.IncidentMinute - 60, _s.State.TotalMinutes, _run.CrimeId);

        var hyp = _s.Investigate.ProposeHypothesis(
            $"slice:{_run.CrimeId}:arson",
            "The cathedral fire was deliberately set.",
            _run.CrimeId);
        _hypothesisId = hyp.Id;
        foreach (var ev in _s.Crime.EvidenceAtScene(crime.SceneId).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (ev.Discovered)
                _s.Investigate.AttachEvidence(hyp.Id, ev.Id, supports: true);
        }
    }

    // ------------------------------------------------------------ days 3-4: pressure

    private void PlayInterrogationSweep()
    {
        var living = _s.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != _player)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id).ToList();
        foreach (var npc in living)
        {
            var result = _s.Investigate.Interrogate(
                _player, npc,
                InvestigationRules.WhereaboutsTopic(_run.IncidentMinute),
                _run.CrimeId);
            _s.Investigate.CheckAgainstSurveillance(result.StatementId);
        }

        // Two short dialogue exchanges with the first witnesses (mock provider).
        var witnesses = _s.Crime.GetWitnesses(_run.CrimeId)
            .Where(w => w != _player)
            .OrderBy(w => w, StringComparer.Ordinal)
            .Take(2).ToList();
        foreach (var w in witnesses)
            _s.Dialogue.Exchange(w, _player, "What did you see at the cathedral that night?");
    }

    // ------------------------------------------------------------ days 5-6: follow-up

    private void PlayFollowUp()
    {
        var crime = _s.Crime.GetCrime(_run.CrimeId);
        var witnesses = _s.Crime.GetWitnesses(_run.CrimeId)
            .Where(w => w != _player)
            .OrderBy(w => w, StringComparer.Ordinal)
            .Take(3).ToList();
        foreach (var w in witnesses)
        {
            var result = _s.Investigate.Interview(
                _player, w, InvestigationRules.EventTopic(crime.IncidentEventId), _run.CrimeId);
            _s.Investigate.CheckAgainstSurveillance(result.StatementId);
        }
    }

    // ------------------------------------------------------------ helpers

    private void MovePlayer(string locationId)
    {
        // The player has a consultant's pass: cordons never block them (ADR-050).
        _s.World.MoveCharacter(_player, locationId);
    }

    private void AdvanceTo(int day, int hour)
    {
        var target = GameTime.At(day, hour).TotalMinutes;
        var now = _s.State.TotalMinutes;
        if (target > now)
            _s.Simulate.Advance((int)(target - now));
    }
}
