using BadDeduction.AI;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Core;
using BadDeduction.Police;

namespace BadDeduction.Social;

/// <summary>
/// Phase 13: a detected threat is a game event, not just prompt flavor. When the player
/// threatens an NPC, this records <c>dialogue.threat</c> (causally linked to the exchange),
/// moves the social axes for the target and every witness, routes the happening through the
/// Phase 4 knowledge gate, and files a police disturbance report when a death threat is made
/// in public or in front of an officer.
/// </summary>
public sealed class ThreatService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly SocialService _social;
    private readonly CognitionService _cognition;
    private readonly PoliceService _police;
    private readonly Func<string, bool> _isPublicLocation;

    public ThreatService(
        GameState state,
        EventSystem events,
        SocialService social,
        CognitionService cognition,
        PoliceService police,
        Func<string, bool> isPublicLocation)
    {
        _state = state;
        _events = events;
        _social = social;
        _cognition = cognition;
        _police = police;
        _isPublicLocation = isPublicLocation;
    }

    /// <summary>Severity scale for threats: DeathThreat 3, ExplicitThreat 2, Menacing 1.</summary>
    public static int SeverityOf(ThreatLevel level) => level switch
    {
        ThreatLevel.DeathThreat => 3,
        ThreatLevel.ExplicitThreat => 2,
        ThreatLevel.Menacing => 1,
        _ => 0,
    };

    /// <summary>
    /// Applies a threat as a world event. No-ops (never throws) when the threatener and the
    /// target are the same character or the level is <see cref="ThreatLevel.None"/>.
    /// </summary>
    public void HandleThreat(
        string threatenerId, string targetId, ThreatLevel level, string locationId, long causedBy)
    {
        if (threatenerId == targetId) return;
        if (level == ThreatLevel.None) return;
        RequireCharacter(threatenerId);
        RequireCharacter(targetId);

        var severity = SeverityOf(level);
        var witnesses = _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != threatenerId && c.Id != targetId
                        && c.CurrentLocationId == locationId)
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var threatEvent = _events.Record(WorldEventTypes.DialogueThreat,
            locationId: locationId,
            participants: new[] { threatenerId, targetId },
            data: new Dictionary<string, string>
            {
                ["level"] = level.ToString(),
                ["severity"] = severity.ToString(),
                ["witnesses"] = witnesses.Count.ToString(),
            },
            causedBy: causedBy);

        // The target's view of the threatener shifts hard.
        var (fear, trust, suspicion) = level switch
        {
            ThreatLevel.DeathThreat => (40, -15, 20),
            ThreatLevel.ExplicitThreat => (25, -10, 12),
            _ => (12, -5, 6),
        };
        _social.Adjust(targetId, threatenerId,
            new SocialDelta(Trust: trust, Fear: fear, Suspicion: suspicion),
            "threatened", causedBy: threatEvent.Id);

        // Witnesses grow wary of the threatener too.
        var (witnessSuspicion, witnessFear) = level switch
        {
            ThreatLevel.DeathThreat => (10, 8),
            ThreatLevel.ExplicitThreat => (6, 5),
            _ => (3, 2),
        };
        foreach (var witnessId in witnesses)
            _social.Adjust(witnessId, threatenerId,
                new SocialDelta(Fear: witnessFear, Suspicion: witnessSuspicion),
                "witnessed threat", causedBy: threatEvent.Id);

        // Everyone present learns about it through the Phase 4 knowledge gate.
        var threatenerName = DisplayNameOf(threatenerId);
        var targetName = DisplayNameOf(targetId);
        var label = level switch
        {
            ThreatLevel.DeathThreat => "death threat",
            ThreatLevel.ExplicitThreat => "explicit threat",
            _ => "menacing remark",
        };
        _cognition.Perceive(targetId, threatEvent.Id, MemorySource.Witnessed,
            $"{threatenerName} threatened me ({label}).", causedBy: threatEvent.Id);
        foreach (var witnessId in witnesses)
            _cognition.Perceive(witnessId, threatEvent.Id, MemorySource.Witnessed,
                $"{threatenerName} threatened {targetName} ({label}).", causedBy: threatEvent.Id);

        // A death threat made in public — or in front of an officer — is a police matter.
        var policeWitness = witnesses.Any(w => _state.World.Characters[w].Kind == CharacterKind.Police);
        if (level == ThreatLevel.DeathThreat && (_isPublicLocation(locationId) || policeWitness))
            _police.ReportDisturbance(threatenerId, locationId, severity: 3, causedBy: threatEvent.Id);
    }

    private void RequireCharacter(string id)
    {
        if (!_state.World.Characters.ContainsKey(id))
            throw new ArgumentException($"Unknown character '{id}'.", nameof(id));
    }

    private string DisplayNameOf(string id) => _state.World.Characters[id].DisplayName;
}
