using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;

namespace BadDeduction.Tests;

internal static class TestSupport
{
    public static ContentDatabase LoadContent() =>
        ContentDatabase.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "data"));

    /// <summary>Small populated world: player, opponent, two civilians and a police officer.</summary>
    public static GameSession NewPopulatedSession(
        ulong seed = 42,
        Campaign campaign = Campaign.Lumiel,
        ContentDatabase? content = null)
    {
        var s = GameSession.NewRun(seed, campaign, Difficulty.Medium, content ?? LoadContent());

        void Add(string id, string name, int age, string job, CharacterKind kind, string home, string? work = null) =>
            s.World.AddCharacter(new CharacterState
            {
                Id = id, DisplayName = name, Age = age, OccupationId = job, Kind = kind,
                HomeLocationId = home, WorkLocationId = work, CurrentLocationId = home,
            });

        Add("c_player", "The Investigator", 34, "consultant", CharacterKind.Civilian, "loc_residential");
        Add("c_rival", "The Rival", 37, "scholar", CharacterKind.Civilian, "loc_residential", "loc_church");
        Add("c_merchant", "A Merchant", 41, "merchant", CharacterKind.Civilian, "loc_residential", "loc_central_market");
        Add("c_priest", "A Priest", 58, "priest", CharacterKind.Civilian, "loc_church", "loc_church");
        Add("c_guard", "A Guard", 29, "guard", CharacterKind.Police, "loc_residential", "loc_guard_station");

        s.World.SetPlayerCharacter("c_player");
        s.Identity.AssignRole("c_player", campaign == Campaign.Lumiel ? HiddenRole.Lumiel : HiddenRole.Malvr);
        s.Identity.AssignRole("c_rival", campaign == Campaign.Lumiel ? HiddenRole.Malvr : HiddenRole.Lumiel);
        return s;
    }

    /// <summary>
    /// A deterministic mini-simulation driven only by the session RNG: random moves and time jumps.
    /// Used to prove that same seed + same steps => same state, including across save/load.
    /// </summary>
    public static void Simulate(GameSession s, int steps)
    {
        var ids = s.State.World.Characters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var locs = s.Content.Locations.Select(l => l.Id).ToList();
        for (var i = 0; i < steps; i++)
        {
            s.World.MoveCharacter(s.Rng.Pick(ids), s.Rng.Pick(locs));
            s.Time.Advance(s.Rng.NextInt(1, 90));
        }
    }
}
