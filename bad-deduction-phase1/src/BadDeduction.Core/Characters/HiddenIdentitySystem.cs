using BadDeduction.Core;

namespace BadDeduction.Characters;

/// <summary>Assigns the hidden roles. Phase 10 adds seeded randomization and campaign logic on top.</summary>
public sealed class HiddenIdentitySystem
{
    private readonly GameState _state;

    public HiddenIdentitySystem(GameState state) => _state = state;

    public void AssignRole(string characterId, HiddenRole role)
    {
        if (!_state.World.Characters.ContainsKey(characterId))
            throw new ArgumentException($"Unknown character '{characterId}'.", nameof(characterId));

        var existing = _state.Truth.CharacterWithRole(role);
        if (existing is not null && existing != characterId)
            throw new InvalidOperationException($"Role {role} is already held by '{existing}'.");

        _state.Truth.HiddenRoles[characterId] = role;
    }
}
