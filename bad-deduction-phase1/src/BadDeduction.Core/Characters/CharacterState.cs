namespace BadDeduction.Characters;

public enum CharacterKind { Civilian, Police }

public enum Activity { Idle, Sleeping, Working, Eating, Socializing, Traveling, Investigating }

/// <summary>
/// Phase 1 "basic NPC data": identity and position only.
/// Personality, goals, needs, memory, beliefs, emotions etc. arrive in Phases 2-4 and will
/// hang off this record by character id so that this class stays small.
/// The player-controlled character is also a CharacterState (NPCs need relationships toward "You").
/// </summary>
public sealed class CharacterState
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Age { get; set; }
    public string OccupationId { get; set; } = "";
    public CharacterKind Kind { get; set; }

    public string HomeLocationId { get; set; } = "";
    public string? WorkLocationId { get; set; }
    public string CurrentLocationId { get; set; } = "";
    public Activity Activity { get; set; } = Activity.Idle;

    /// <summary>Dead characters remain in the world (bodies, memories of them, investigations).</summary>
    public bool IsAlive { get; set; } = true;
}

/// <summary>
/// The only character projection UI code should use. It intentionally has no role/identity-truth
/// fields. Location/alive state are also omitted: what the player may see there depends on the
/// player's knowledge, which is gated in Phase 4.
/// </summary>
public sealed record PublicCharacterInfo(
    string Id,
    string DisplayName,
    int Age,
    string OccupationId,
    CharacterKind Kind);
