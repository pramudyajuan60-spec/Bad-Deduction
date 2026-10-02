using System.Diagnostics.CodeAnalysis;

namespace BadDeduction.Core;

/// <summary>
/// A persistent, authoritative record of something that happened in the world (ground truth).
/// What any character *knows* or *believes* about it is a separate concern (Phase 4).
/// <see cref="CausedBy"/> links events into causal chains, e.g. murder → discovery → rumor → arrest.
/// </summary>
public sealed class WorldEvent
{
    public long Id { get; set; }
    public long Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string? LocationId { get; set; }
    public List<string> Participants { get; set; } = new();
    public Dictionary<string, string> Data { get; set; } = new();
    public long? CausedBy { get; set; }
}

/// <summary>Append-only event log. Ids are sequential from 1, so lookup is O(1).</summary>
public sealed class EventLog
{
    public long NextId { get; set; } = 1;
    public List<WorldEvent> Events { get; set; } = new();

    public bool TryGet(long id, [NotNullWhen(true)] out WorldEvent? evt)
    {
        if (id >= 1 && id <= Events.Count && Events[(int)(id - 1)].Id == id)
        {
            evt = Events[(int)(id - 1)];
            return true;
        }
        evt = null;
        return false;
    }
}

/// <summary>Well-known event type ids. Later phases add crime.*, evidence.*, police.* etc.</summary>
public static class WorldEventTypes
{
    public const string RunStarted = "run.started";
    public const string DayStarted = "time.day_started";
    public const string CharacterMoved = "character.moved";
    public const string CharacterDeparted = "character.departed";
    public const string ActivityChanged = "character.activity_changed";
    public const string RelationshipChanged = "social.relationship_changed";
    public const string MemoryRecorded = "cognition.memory_recorded";
    public const string RumorSpread = "cognition.rumor_spread";
    public const string BeliefChanged = "cognition.belief_changed";
    public const string DialogueExchanged = "dialogue.exchanged";
}
