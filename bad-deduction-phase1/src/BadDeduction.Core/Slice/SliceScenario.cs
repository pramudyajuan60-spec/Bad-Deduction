using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Cognition;

namespace BadDeduction.Slice;

/// <summary>A fully assembled vertical-slice run: the session plus the slice's key facts.</summary>
public sealed record SliceRun(
    GameSession Session,
    string CrimeId,
    string VictimId,
    long IncidentMinute);

/// <summary>
/// Phase 12: assembles the canonical vertical slice — "The Burning at Saint Velmont".
/// 22 characters (17 civilians + 5 police via CastGenerator; the player is one genius,
/// the opposing genius is seeded), the ~8-node slice subgraph, and a Day-1 deadly-arson
/// incident at the cathedral at night. Everything is seeded and deterministic.
///
/// This is director-level scenario construction (like CastGenerator itself): it may read
/// ground truth to SET UP the slice (e.g. keeping the opposing genius alive as a candidate
/// for the hidden-conflict arc), but it never grants any character knowledge they did not
/// receive — all witness knowledge flows through the sanctioned Perceive gate.
/// </summary>
public static class SliceScenario
{
    public const string CathedralId = "loc_cathedral";
    public const string CrimeDefinitionId = "arson_fatal";

    /// <summary>The ~8 connected nodes the slice plays on (audit C-6, plus the cathedral).</summary>
    public static readonly IReadOnlyList<string> SliceLocationIds = new[]
    {
        "loc_central_market", "loc_residential", "loc_city_hall", "loc_church",
        CathedralId, "loc_tavern", "loc_warehouse", "loc_guard_station",
    };

    /// <summary>Day-1 incident hour (night): the cathedral burns while the city sleeps.</summary>
    public const int IncidentHour = 21;

    /// <summary>How many NPC eyewitnesses the scenario places at the cathedral.</summary>
    public const int PlacedWitnessCount = 2;

    public static SliceRun Build(ulong seed, Campaign campaign, Difficulty difficulty, ContentDatabase content)
    {
        if (!content.HasLocation(CathedralId))
            throw new InvalidOperationException($"Slice content is missing '{CathedralId}'.");

        var session = GameSession.NewRun(seed, campaign, difficulty, content, GameTime.At(1, 18));
        session.Cast.Generate(new CastSpec());

        // The player is the first civilian by (seed-shuffled) id order: deterministic,
        // and a different face every seed.
        var playerId = session.State.World.Characters.Values
            .Where(c => c.Kind == CharacterKind.Civilian)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .First().Id;
        session.World.SetPlayerCharacter(playerId);
        session.Identity.AssignHiddenRoles();

        // Night of day 1: let the city live a little before the fire.
        session.Simulate.Advance((int)(GameTime.At(1, IncidentHour).TotalMinutes - session.State.TotalMinutes));

        // Victim + eyewitnesses from a dedicated stream (never touches other streams).
        var vrng = DeterministicRandom.Derive(seed, "slice.victim");
        var opposingGenius = session.State.Truth.HiddenRoles
            .First(kv => kv.Value != (campaign == Campaign.Malvr ? HiddenRole.Malvr : HiddenRole.Lumiel)).Key;
        var candidates = session.State.World.Characters.Values
            .Where(c => c.IsAlive && c.Kind == CharacterKind.Civilian && c.Id != playerId && c.Id != opposingGenius)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id).ToList();
        var victimId = vrng.Pick(candidates);
        var pool = candidates.Where(id => id != victimId).ToList();
        vrng.Shuffle(pool);
        var witnesses = pool.Take(PlacedWitnessCount).ToList();

        session.World.MoveCharacter(victimId, CathedralId);
        foreach (var w in witnesses)
            session.World.MoveCharacter(w, CathedralId);

        var crime = session.Crime.GenerateIncident(CrimeDefinitionId, victimId);
        var incidentMinute = session.State.TotalMinutes;

        // The placed NPCs saw it happen: knowledge flows through the sanctioned gate.
        foreach (var w in witnesses)
            session.Cognition.Perceive(w, crime.IncidentEventId, MemorySource.Witnessed);

        // The slice's hidden truth: MALVR set the fire (the "sinister" answer to the
        // slice's accident/cover-up/sinister question). Attribution lives ONLY in the
        // hidden agenda store — never in events — and the holder knows their own deed
        // (a bland inference, like ActEliminateObstacle records). This is what makes
        // the Day-5 hidden-conflict beat and interview deception possible.
        var malvrId = campaign == Campaign.Malvr ? playerId : opposingGenius;
        session.State.Agenda.IncidentAttribution[crime.Id] = malvrId;
        session.Cognition.Perceive(malvrId, crime.IncidentEventId, MemorySource.Inferred);

        return new SliceRun(session, crime.Id, victimId, incidentMinute);
    }
}
