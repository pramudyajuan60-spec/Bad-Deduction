using BadDeduction.Characters;

namespace BadDeduction.Core;

/// <summary>
/// Gameplay-safe read access for UI. Nothing here can reveal hidden roles or world truth.
/// Phase 4 makes this knowledge-gated (only what the player has actually perceived).
/// </summary>
public sealed class PlayerView
{
    private readonly GameState _state;

    public PlayerView(GameState state) => _state = state;

    public PublicCharacterInfo? PublicProfile(string characterId) =>
        _state.World.Characters.TryGetValue(characterId, out var c)
            ? new PublicCharacterInfo(c.Id, c.DisplayName, c.Age, c.OccupationId, c.Kind)
            : null;
}

/// <summary>Developer-only window into hidden truth. Disabled unless debug mode is explicitly on.</summary>
public sealed class DebugAccess
{
    private readonly GameState _state;

    public DebugAccess(GameState state) => _state = state;

    public bool Enabled { get; set; }

    public WorldTruth GetTruth() =>
        Enabled
            ? _state.Truth
            : throw new InvalidOperationException("Debug mode is disabled; hidden truth is not available.");
}
