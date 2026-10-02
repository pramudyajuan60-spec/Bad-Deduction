using BadDeduction.Characters;
using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class WorldTests
{
    [Fact]
    public void Characters_can_be_added_and_moved_and_movement_is_logged()
    {
        var s = TestSupport.NewPopulatedSession();
        var evt = s.World.MoveCharacter("c_merchant", "loc_central_market");
        Assert.True(evt is not null, "the move should succeed");

        Assert.Equal("loc_central_market", s.World.GetCharacter("c_merchant").CurrentLocationId);
        Assert.Equal(WorldEventTypes.CharacterMoved, evt!.Type);
        Assert.Equal("loc_residential", evt.Data["from"]);
        Assert.True(s.World.CharactersAt("loc_central_market").Any(c => c.Id == "c_merchant"));
    }

    [Fact]
    public void Movement_can_be_causally_linked()
    {
        var s = TestSupport.NewPopulatedSession();
        var cause = s.Events.Record("test.alarm");
        var move = s.World.MoveCharacter("c_guard", "loc_warehouse", causedBy: cause.Id);
        Assert.True(move is not null, "the move should succeed");
        Assert.Equal(cause.Id, move!.CausedBy);
    }

    [Fact]
    public void Invalid_characters_and_moves_are_rejected()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.Throws<ArgumentException>(() => s.World.MoveCharacter("c_merchant", "loc_nowhere"));
        Assert.Throws<KeyNotFoundException>(() => s.World.MoveCharacter("c_ghost", "loc_tavern"));
        Assert.Throws<InvalidOperationException>(() => s.World.AddCharacter(new CharacterState
        {
            Id = "c_merchant", DisplayName = "Dup", HomeLocationId = "loc_tavern", CurrentLocationId = "loc_tavern",
        }));
        Assert.Throws<ArgumentException>(() => s.World.AddCharacter(new CharacterState
        {
            Id = "c_new", DisplayName = "Lost", HomeLocationId = "loc_moon", CurrentLocationId = "loc_moon",
        }));
    }

    [Fact]
    public void Dead_characters_do_not_walk_around()
    {
        var s = TestSupport.NewPopulatedSession();
        s.World.GetCharacter("c_priest").IsAlive = false;
        Assert.Throws<InvalidOperationException>(() => s.World.MoveCharacter("c_priest", "loc_tavern"));
    }
}

public sealed class HiddenIdentityTests
{
    [Fact]
    public void Roles_follow_the_campaign_and_are_unique()
    {
        var s = TestSupport.NewPopulatedSession(campaign: Campaign.Malvr);
        Assert.Equal("c_player", s.State.Truth.CharacterWithRole(HiddenRole.Malvr));
        Assert.Equal("c_rival", s.State.Truth.CharacterWithRole(HiddenRole.Lumiel));
        Assert.Throws<InvalidOperationException>(() => s.Identity.AssignRole("c_merchant", HiddenRole.Malvr));
        Assert.Throws<ArgumentException>(() => s.Identity.AssignRole("c_ghost", HiddenRole.Lumiel));
    }

    [Fact]
    public void Truth_is_inaccessible_unless_debug_mode_is_enabled()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.Throws<InvalidOperationException>(() => s.Debug.GetTruth());
        s.Debug.Enabled = true;
        Assert.Equal("c_player", s.Debug.GetTruth().CharacterWithRole(HiddenRole.Lumiel));
    }

    [Fact]
    public void Player_facing_profiles_never_carry_role_information()
    {
        var s = TestSupport.NewPopulatedSession();
        var profile = s.View.PublicProfile("c_rival");
        Assert.True(profile is not null);

        var forbidden = new[] { "Role", "Hidden", "Malvr", "Lumiel", "Truth" };
        foreach (var p in typeof(PublicCharacterInfo).GetProperties())
            Assert.False(forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)),
                $"PublicCharacterInfo.{p.Name} could leak hidden identity.");
    }

    [Fact]
    public void Validator_rejects_duplicate_roles_and_campaign_mismatch()
    {
        var s = TestSupport.NewPopulatedSession(campaign: Campaign.Lumiel);

        s.State.Truth.HiddenRoles["c_merchant"] = HiddenRole.Malvr; // second Malvr
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("at most one")));
        s.State.Truth.HiddenRoles.Remove("c_merchant");
        Assert.Equal(0, GameStateValidator.Validate(s.State).Count);

        s.State.Meta.Campaign = Campaign.Malvr; // player holds Lumiel but campaign says Malvr
        Assert.True(GameStateValidator.Validate(s.State).Any(e => e.Contains("campaign")));
    }
}
