namespace BadDeduction.World;

/// <summary>
/// Phase 5: a character caught between two locations. Travel takes real minutes (the location
/// graph has timed edges), so a departure and its arrival are separate moments and the
/// character is <see cref="BadDeduction.Characters.Activity.Traveling"/> in between.
/// Plain data on <see cref="WorldState.ActiveTravels"/>: saving mid-travel and loading later
/// continues the trip identically, which the determinism tests pin down.
/// </summary>
public sealed class TravelState
{
    public string CharacterId { get; set; } = "";
    public string FromLocationId { get; set; } = "";
    public string ToLocationId { get; set; } = "";

    /// <summary>Game time (TotalMinutes) the trip started.</summary>
    public long DepartureMinute { get; set; }

    /// <summary>Game time (TotalMinutes) the trip ends: DepartureMinute + edge minutes.</summary>
    public long ArrivalMinute { get; set; }

    /// <summary>
    /// The character.departed event recorded when the trip started; the arrival's
    /// character.moved event links back to it via CausedBy.
    /// </summary>
    public long DepartureEventId { get; set; }
}
