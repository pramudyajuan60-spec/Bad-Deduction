using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Social;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class RelationshipTests
{
    [Fact]
    public void Every_relationship_has_a_reverse_edge_of_the_same_kind_and_no_self_edges()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var e in s.State.World.Relationships)
            {
                Assert.NotEqual(e.From, e.To);
                Assert.Equal(e.Kind, s.Relationships.Get(e.To, e.From)!.Kind);
            }
        }
    }

    [Fact]
    public void The_social_network_is_one_connected_component_for_every_seed()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            Assert.True(s.Relationships.IsConnected(s.State.World.Characters.Keys), $"seed {seed}: social network is split");
        }
    }

    [Fact]
    public void Household_members_are_family_and_colleagues_share_a_workplace()
    {
        var s = CastSupport.NewCastSession(17);
        var chars = s.State.World.Characters;
        foreach (var e in s.State.World.Relationships)
        {
            var a = chars[e.From];
            var b = chars[e.To];
            var sameHousehold = s.State.World.Profiles[a.Id].HouseholdId == s.State.World.Profiles[b.Id].HouseholdId;
            Assert.Equal(sameHousehold, e.Kind == RelationshipKind.Family, "family <=> same household");
            if (e.Kind == RelationshipKind.Colleague) Assert.Equal(a.WorkLocationId, b.WorkLocationId);
        }
    }

    [Fact]
    public void Family_ages_are_plausible()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var e in s.State.World.Relationships.Where(e => e.Kind == RelationshipKind.Family))
            {
                var gap = Math.Abs(s.State.World.Characters[e.From].Age - s.State.World.Characters[e.To].Age);
                Assert.True(gap <= 35, $"seed {seed}: family age gap {gap}");
            }
        }
    }

    [Fact]
    public void Rivals_and_friends_exist_and_no_one_has_too_many_friends()
    {
        var kinds = new HashSet<RelationshipKind>();
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var s = CastSupport.NewCastSession(seed);
            foreach (var e in s.State.World.Relationships) kinds.Add(e.Kind);
            foreach (var id in s.State.World.Characters.Keys)
                Assert.True(s.Relationships.Of(id, RelationshipKind.Friend).Count() <= 4, "friend cap");
        }
        Assert.True(kinds.Contains(RelationshipKind.Friend) && kinds.Contains(RelationshipKind.Rival) && kinds.Contains(RelationshipKind.Family) && kinds.Contains(RelationshipKind.Colleague));
    }

    [Fact]
    public void Connect_rejects_duplicates_self_links_and_unknown_characters()
    {
        var s = CastSupport.NewCastSession(1);
        Assert.False(s.Relationships.Connect("c_01", "c_01", RelationshipKind.Friend));
        Assert.False(s.Relationships.Connect("c_01", "c_99", RelationshipKind.Friend));
        var existing = s.State.World.Relationships[0];
        Assert.False(s.Relationships.Connect(existing.From, existing.To, RelationshipKind.Rival));
    }

    [Fact]
    public void Validator_rejects_one_way_and_dangling_relationships()
    {
        var s = CastSupport.NewCastSession(1);
        s.State.World.Relationships.RemoveAt(0);
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("no reverse edge")));

        var t = CastSupport.NewCastSession(1);
        t.State.World.Relationships.Add(new RelationshipEdge { From = "c_01", To = "ghost", Kind = RelationshipKind.Friend });
        Assert.True(GameStateValidator.Validate(t.State).Any(e => e.Contains("unknown character")));
    }
}
