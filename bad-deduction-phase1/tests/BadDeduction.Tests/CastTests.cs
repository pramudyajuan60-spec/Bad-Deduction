using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;
using BadDeduction.World;

namespace BadDeduction.Tests;

public sealed class CastTests
{
    [Fact]
    public void Default_cast_matches_the_vertical_slice_size()
    {
        var s = CastSupport.NewCastSession();
        var all = s.State.World.Characters.Values.ToList();
        Assert.Equal(22, all.Count);
        Assert.Equal(17, all.Count(c => c.Kind == CharacterKind.Civilian));
        Assert.Equal(5, all.Count(c => c.Kind == CharacterKind.Police));
        Assert.Equal(1, all.Count(c => c.OccupationId == "guard_captain"));
        Assert.Equal(0, GameStateValidator.Validate(s.State).Count);
        Assert.Equal(0, GameStateValidator.ValidateAgainstContent(s.State, s.Content).Count());
    }

    [Fact]
    public void Same_seed_gives_the_same_cast_and_different_seeds_give_different_social_worlds()
    {
        var a = CastSupport.NewCastSession(7).StateHash();
        Assert.Equal(a, CastSupport.NewCastSession(7).StateHash());

        var names = new HashSet<string>();
        var structures = new HashSet<string>();
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            names.Add(string.Join("|", s.State.World.Characters.Values.Select(c => c.DisplayName)));
            structures.Add(string.Join("|", s.State.World.Relationships.Select(e => $"{e.From}>{e.To}:{e.Kind}")));
        }
        Assert.Equal(30, names.Count, "every seed should produce a different cast");
        Assert.Equal(30, structures.Count, "every seed should produce a different relationship graph");
    }

    [Fact]
    public void Generating_the_cast_does_not_consume_the_simulation_rng()
    {
        var before = GameSession.NewRun(5, Campaign.Malvr, Difficulty.Easy, TestSupport.LoadContent()).State.SimRng;
        var s = CastSupport.NewCastSession(5);
        Assert.Equal(before.S0, s.State.SimRng.S0);
        Assert.Equal(before.S3, s.State.SimRng.S3);
    }

    [Fact]
    public void Ids_carry_no_information_about_occupation()
    {
        // The captain must not always be c_01, or a player could learn "c_01 is the captain".
        var captainIds = new HashSet<string>();
        for (ulong seed = 1; seed <= 40; seed++)
            captainIds.Add(CastSupport.NewCastSession(seed).State.World.Characters.Values.Single(c => c.OccupationId == "guard_captain").Id);
        Assert.True(captainIds.Count >= 10, $"captain id took only {captainIds.Count} distinct values");
    }

    [Fact]
    public void Names_are_unique_and_households_share_home_and_surname()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            var chars = s.State.World.Characters.Values.ToList();
            Assert.Equal(chars.Count, chars.Select(c => c.DisplayName).Distinct().Count(), $"seed {seed}: duplicate full names");

            foreach (var group in chars.GroupBy(c => s.State.World.Profiles[c.Id].HouseholdId))
            {
                Assert.Equal(1, group.Select(c => c.DisplayName.Split(' ')[1]).Distinct().Count(), $"seed {seed}: household surnames differ");
                Assert.Equal(1, group.Select(c => c.HomeLocationId).Distinct().Count());
            }
        }
    }

    [Fact]
    public void Ages_and_jobs_respect_occupation_definitions_and_family_ages_make_sense()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            var counts = new Dictionary<string, int>();
            foreach (var c in s.State.World.Characters.Values)
            {
                var occ = s.Content.GetOccupation(c.OccupationId);
                Assert.True(c.Age >= occ.MinAge && c.Age <= occ.MaxAge, $"seed {seed}: {c.Id} age {c.Age} outside {occ.Id} range");
                Assert.Equal(occ.Kind, c.Kind);
                Assert.Equal(occ.WorkLocationId, c.WorkLocationId);
                counts[occ.Id] = counts.GetValueOrDefault(occ.Id) + 1;
            }
            foreach (var (id, n) in counts) Assert.True(n <= s.Content.GetOccupation(id).MaxCount, $"seed {seed}: too many {id}");
        }
    }

    [Fact]
    public void Slice_locations_only_and_no_hidden_roles_are_assigned()
    {
        var s = CastSupport.NewCastSession(11);
        Assert.Equal(0, s.State.Truth.HiddenRoles.Count);
        foreach (var c in s.State.World.Characters.Values)
        {
            Assert.NotEqual("loc_harbor", c.WorkLocationId);
            foreach (var b in s.State.World.Schedules[c.Id].Workday.Concat(s.State.World.Schedules[c.Id].DayOff))
            {
                Assert.NotEqual("loc_harbor", b.LocationId);
                Assert.NotEqual("loc_underground", b.LocationId);
            }
        }
    }

    [Fact]
    public void Personality_is_in_range_and_varies()
    {
        var s = CastSupport.NewCastSession(3);
        foreach (var p in s.State.World.Profiles.Values)
            foreach (var t in p.Personality.All()) Assert.True(t is >= 0 and <= 100);
        Assert.True(s.State.World.Profiles.Values.Select(p => p.Personality.Honesty).Distinct().Count() > 8);
    }

    [Fact]
    public void Goals_match_the_character_and_can_target_real_relatives_and_friends()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var c in s.State.World.Characters.Values)
            {
                var profile = s.State.World.Profiles[c.Id];
                Assert.Equal(GoalSlot.Primary, profile.Goals[0].Slot);
                Assert.Equal(1, profile.Goals.Count(g => g.Slot == GoalSlot.Primary));
                Assert.True(profile.Goals.Any(g => g.Slot == GoalSlot.Secondary));
                Assert.Equal(profile.Goals.Count, profile.Goals.Select(g => g.DefinitionId).Distinct().Count(), "duplicate goal");

                foreach (var g in profile.Goals)
                {
                    var def = s.Content.Goals.Single(d => d.Id == g.DefinitionId);
                    if (def.Requires.Contains("police")) Assert.Equal(CharacterKind.Police, c.Kind);
                    if (def.Requires.Contains("civilian")) Assert.Equal(CharacterKind.Civilian, c.Kind);
                    if (def.Requires.Contains("secret")) Assert.True(profile.SecretIds.Count > 0, $"{def.Id} without a secret");
                    if (def.TargetFrom == GoalTargetSource.Family)
                        Assert.True(s.Relationships.Get(c.Id, g.TargetId!)?.Kind == RelationshipKind.Family, "family goal must target family");
                    if (def.TargetFrom == GoalTargetSource.Friend)
                        Assert.True(s.Relationships.Get(c.Id, g.TargetId!)?.Kind == RelationshipKind.Friend, "friend goal must target a friend");
                }
            }
        }
    }

    [Fact]
    public void Secrets_exist_but_are_not_universal_and_dishonest_people_have_more()
    {
        int withSecret = 0, total = 0, lowHonesty = 0, lowWith = 0, highHonesty = 0, highWith = 0;
        for (ulong seed = 1; seed <= 60; seed++)
            foreach (var p in CastSupport.NewCastSession(seed).State.World.Profiles.Values)
            {
                total++;
                var has = p.SecretIds.Count > 0;
                if (has) withSecret++;
                if (p.Personality.Honesty < 35) { lowHonesty++; if (has) lowWith++; }
                if (p.Personality.Honesty > 65) { highHonesty++; if (has) highWith++; }
            }
        Assert.True(withSecret > total / 10 && withSecret < total / 2, $"{withSecret}/{total} characters have secrets");
        Assert.True((double)lowWith / lowHonesty > (double)highWith / highHonesty, "low-honesty characters should hide more");
    }

    [Fact]
    public void Private_profile_data_never_reaches_the_public_view()
    {
        var s = CastSupport.NewCastSession(9);
        var id = s.State.World.Characters.Keys.First();
        var info = s.View.PublicProfile(id)!;
        var fields = info.GetType().GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var forbidden in new[] { "Personality", "Goals", "SecretIds", "Secrets", "HouseholdId", "Profile" })
            Assert.False(fields.Contains(forbidden), $"PublicCharacterInfo exposes {forbidden}");
    }

    [Fact]
    public void Cast_can_only_be_generated_into_an_empty_world_and_bad_specs_fail_loudly()
    {
        var s = CastSupport.NewCastSession(1);
        Assert.Throws<InvalidOperationException>(() => s.Cast.Generate(new CastSpec()));

        var fresh = GameSession.NewRun(1, Campaign.Lumiel, Difficulty.Easy, TestSupport.LoadContent());
        Assert.Throws<ArgumentException>(() => fresh.Cast.Generate(new CastSpec { HomeLocationId = "loc_nowhere" }));
        Assert.Throws<InvalidOperationException>(() => fresh.Cast.Generate(new CastSpec { Police = 0, Civilians = 5 }));
    }

    [Fact]
    public void Other_cast_sizes_work()
    {
        var s = CastSupport.NewCastSession(4, new CastSpec { Civilians = 8, Police = 2 });
        Assert.Equal(10, s.State.World.Characters.Count);
        Assert.True(s.Relationships.IsConnected(s.State.World.Characters.Keys));
    }
}
