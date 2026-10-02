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

    /// <summary>
    /// Seeded role assignment (Phase 10, the canonical path): the player always holds their
    /// campaign's role (already enforced by the validator), and the opposing genius is drawn
    /// deterministically from the living non-player characters. The draw comes from the
    /// shared agenda stream ("agenda.roles"), which also feeds objective generation — one
    /// logical stream mutated in place, so the draw sequence is fixed by call order and
    /// never touches the sim stream (ADR-002). Idempotent: re-running keeps existing
    /// assignments (manual test setups stay put).
    /// </summary>
    public void AssignHiddenRoles()
    {
        var playerId = _state.Player.CharacterId;
        if (string.IsNullOrEmpty(playerId))
            throw new InvalidOperationException("Set the player character before assigning hidden roles.");
        if (!_state.World.Characters.TryGetValue(playerId, out var player) || !player.IsAlive)
            throw new InvalidOperationException("The player character must be a living character.");

        var playerRole = _state.Meta.Campaign == Campaign.Malvr ? HiddenRole.Malvr : HiddenRole.Lumiel;
        AssignRole(playerId, playerRole);

        var opposing = playerRole == HiddenRole.Malvr ? HiddenRole.Lumiel : HiddenRole.Malvr;
        if (_state.Truth.CharacterWithRole(opposing) is not null) return; // already assigned manually

        var rng = new DeterministicRandom(_state.Agenda.AgendaRng);
        var eligible = _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != playerId)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .ToList();
        if (eligible.Count == 0)
            throw new InvalidOperationException("No eligible living NPC can hold the opposing genius role.");
        AssignRole(rng.Pick(eligible), opposing);
        _state.Agenda.RolesSeeded = true;
    }
}
