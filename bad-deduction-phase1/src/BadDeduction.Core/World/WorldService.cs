using BadDeduction.Characters;
using BadDeduction.Content;
using BadDeduction.Core;

namespace BadDeduction.World;

/// <summary>Validated mutations of world state. Full schedule-driven movement arrives in Phase 5.</summary>
public sealed class WorldService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly ContentDatabase _content;

    public WorldService(GameState state, EventSystem events, ContentDatabase content)
    {
        _state = state;
        _events = events;
        _content = content;
    }

    public CharacterState AddCharacter(CharacterState character)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
            throw new ArgumentException("Character id is required.", nameof(character));
        if (_state.World.Characters.ContainsKey(character.Id))
            throw new InvalidOperationException($"Character '{character.Id}' already exists.");

        RequireLocation(character.HomeLocationId, "home");
        RequireLocation(character.CurrentLocationId, "current");
        if (character.WorkLocationId is not null) RequireLocation(character.WorkLocationId, "work");

        _state.World.Characters.Add(character.Id, character);
        return character;
    }

    public void SetPlayerCharacter(string characterId)
    {
        if (!_state.World.Characters.ContainsKey(characterId))
            throw new ArgumentException($"Unknown character '{characterId}'.", nameof(characterId));
        _state.Player.CharacterId = characterId;
    }

    public CharacterState GetCharacter(string id) =>
        _state.World.Characters.TryGetValue(id, out var c)
            ? c
            : throw new KeyNotFoundException($"Unknown character '{id}'.");

    /// <summary>
    /// Optional access gate (Phase 9 cordons). When set and the check fails, the move is
    /// denied: a <c>police.access_denied</c> event is logged and null is returned.
    /// Null means "everyone may pass", which preserves all pre-cordon behavior.
    /// </summary>
    public Func<string, string, bool>? AccessCheck { get; set; }

    /// <summary>
    /// Moves a character and records a persistent, optionally causally-linked event.
    /// Returns null when the move is denied by <see cref="AccessCheck"/> (the denial itself
    /// is logged); callers that must move should check the result.
    /// </summary>
    public WorldEvent? MoveCharacter(string characterId, string toLocationId, long? causedBy = null)
    {
        var c = GetCharacter(characterId);
        if (!c.IsAlive)
            throw new InvalidOperationException($"'{characterId}' is dead and cannot move on their own.");
        RequireLocation(toLocationId, "destination");

        if (AccessCheck is not null && !AccessCheck(characterId, toLocationId))
        {
            _events.Record(
                WorldEventTypes.PoliceAccessDenied,
                locationId: toLocationId,
                participants: new[] { characterId },
                data: new Dictionary<string, string> { ["to"] = toLocationId },
                causedBy: causedBy);
            return null;
        }

        var from = c.CurrentLocationId;
        c.CurrentLocationId = toLocationId;
        return _events.Record(
            WorldEventTypes.CharacterMoved,
            locationId: toLocationId,
            participants: new[] { characterId },
            data: new Dictionary<string, string> { ["from"] = from, ["to"] = toLocationId },
            causedBy: causedBy);
    }

    public IEnumerable<CharacterState> CharactersAt(string locationId) =>
        _state.World.Characters.Values.Where(c => c.CurrentLocationId == locationId);

    private void RequireLocation(string locationId, string what)
    {
        if (!_content.HasLocation(locationId) || !_state.World.Locations.ContainsKey(locationId))
            throw new ArgumentException($"Unknown {what} location '{locationId}'.");
    }
}
