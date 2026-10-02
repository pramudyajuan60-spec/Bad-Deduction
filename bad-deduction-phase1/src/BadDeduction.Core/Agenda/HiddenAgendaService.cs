using BadDeduction.AI;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Investigation;
using BadDeduction.Social;

namespace BadDeduction.Agenda;

/// <summary>
/// Phase 10: the hidden-genius layer (Malvr / Lumiel), wired as <c>session.Agenda</c>.
/// <para/>
/// Knowledge discipline (the rule this whole phase stands on): the service is truth-aware
/// like any director — it must read the role roster to know WHO to drive — but each
/// genius's DECISIONS use only their own role plus their own knowledge through
/// <see cref="CognitionService"/> (memories, known events, beliefs) and their own social
/// perception through <see cref="SocialService"/>. A genius never learns another
/// character's role or secrets except through gameplay. Concretely: every rumor spreads
/// an event the holder actually knows (<see cref="CognitionService.TellRumor"/> enforces
/// it), every target comes from the public roster or the holder's contacts, and objective
/// lifecycle events never name a holder or a role.
/// <para/>
/// The daily <see cref="StrategicTick"/> drives only NPC-held geniuses: the player's own
/// side is player-driven. The tick runs automatically on <c>DayChanged</c>, but only for
/// runs whose roles were assigned through the seeded canonical path
/// (<see cref="HiddenIdentitySystem.AssignHiddenRoles"/>, which sets
/// <c>RolesSeeded</c>); manual test setups opt in explicitly, so no earlier simulation
/// test changes behavior.
/// </summary>
public sealed class HiddenAgendaService : IDeceptionHook
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly CognitionService _cognition;
    private readonly CrimeService _crime;
    private readonly InvestigationService _investigate;
    private readonly SocialService _social;
    private readonly ContentDatabase _content;
    private readonly DeterministicRandom _rng;

    public HiddenAgendaService(
        GameState state,
        EventSystem events,
        CognitionService cognition,
        CrimeService crime,
        InvestigationService investigate,
        SocialService social,
        ContentDatabase content)
    {
        _state = state;
        _events = events;
        _cognition = cognition;
        _crime = crime;
        _investigate = investigate;
        _social = social;
        _content = content;
        _rng = new DeterministicRandom(state.Agenda.AgendaRng);
        _events.Subscribe<DayChanged>(_ => StrategicTick());
    }

    // ------------------------------------------------------------------ reads

    /// <summary>NPC-held geniuses: everyone with a hidden role except the player.</summary>
    public IReadOnlyList<string> NpcGeniusHolders() =>
        _state.Truth.HiddenRoles.Keys
            .Where(id => id != _state.Player.CharacterId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<HiddenObjective> ObjectivesOf(string holderId) =>
        _state.Agenda.Objectives.Values
            .Where(o => o.HolderId == holderId)
            .OrderBy(o => o.Id, StringComparer.Ordinal)
            .ToList();

    // ------------------------------------------------------------------ objectives

    /// <summary>
    /// Deals two seeded objectives to a genius holder (idempotent). Malvr draws from
    /// {EliminateObstacle, SowDistrust, EvadeSuspicion}; Lumiel from {ProtectTarget,
    /// GatherAlly, PursueLead}. "Unmask the rival" is deliberately NOT an objective kind:
    /// the holder doesn't know who the rival is, so a fixed-target unmasking would break
    /// the knowledge discipline — Lumiel instead pursues leads on crimes they know about.
    /// </summary>
    public void EnsureObjectives(string holderId)
    {
        RequireHolder(holderId);
        if (_state.Agenda.Objectives.Values.Any(o => o.HolderId == holderId)) return;

        var role = _state.Truth.HiddenRoles[holderId];
        var kinds = (role == HiddenRole.Malvr
            ? new[] { ObjectiveKind.EliminateObstacle, ObjectiveKind.SowDistrust, ObjectiveKind.EvadeSuspicion }
            : new[] { ObjectiveKind.ProtectTarget, ObjectiveKind.GatherAlly, ObjectiveKind.PursueLead }).ToList();
        _rng.Shuffle(kinds);
        foreach (var kind in kinds.Take(2))
            CreateObjective(holderId, kind);
    }

    private void CreateObjective(string holderId, ObjectiveKind kind)
    {
        var agenda = _state.Agenda;
        var obj = new HiddenObjective
        {
            Id = $"obj_{agenda.NextObjectiveId++}",
            HolderId = holderId,
            Kind = kind,
            CreatedAt = _state.TotalMinutes,
        };
        switch (kind)
        {
            case ObjectiveKind.EliminateObstacle:
            case ObjectiveKind.ProtectTarget:
            case ObjectiveKind.GatherAlly:
                obj.TargetId = PickLiving(holderId);
                break;
            case ObjectiveKind.SowDistrust:
                obj.TargetId = PickContactCandidate(holderId) ?? PickLiving(holderId);
                obj.TargetId2 = PickLiving(holderId, obj.TargetId);
                break;
            case ObjectiveKind.EvadeSuspicion:
                break;
            case ObjectiveKind.PursueLead:
                var known = KnownCrimes(holderId).MaxBy(c => c.OccurredAt);
                if (known is null)
                {
                    // No crime on the holder's radar yet: fall back to alliance-building.
                    obj.Kind = ObjectiveKind.GatherAlly;
                    obj.TargetId = PickLiving(holderId);
                }
                else obj.CrimeId = known.Id;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        agenda.Objectives.Add(obj.Id, obj);
    }

    /// <summary>Seeded pick among living non-player characters (never the holder).</summary>
    private string PickLiving(string holderId, params string[] extraExclude)
    {
        var excluded = new HashSet<string>(extraExclude, StringComparer.Ordinal) { holderId, _state.Player.CharacterId };
        var pool = _state.World.Characters.Values
            .Where(c => c.IsAlive && !excluded.Contains(c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .ToList();
        if (pool.Count == 0)
            throw new InvalidOperationException("No living candidate for a hidden objective.");
        return _rng.Pick(pool);
    }

    /// <summary>Someone the holder has actually met (edge or shared location) — for SowDistrust.</summary>
    private string? PickContactCandidate(string holderId)
    {
        var pool = _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != holderId && c.Id != _state.Player.CharacterId && InContact(holderId, c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .ToList();
        return pool.Count == 0 ? null : _rng.Pick(pool);
    }

    // ------------------------------------------------------------------ the daily tick

    /// <summary>
    /// One strategic action per NPC-held genius per day, in deterministic priority order.
    /// Runs automatically on DayChanged for seeded runs; also callable manually.
    /// </summary>
    public void StrategicTick()
    {
        if (!_state.Agenda.RolesSeeded) return;
        var day = new GameTime(_state.TotalMinutes).Day;
        foreach (var holderId in NpcGeniusHolders())
        {
            if (!_state.World.Characters.TryGetValue(holderId, out var holder) || !holder.IsAlive)
            {
                FailAllObjectives(holderId, "holder died");
                continue;
            }
            EnsureObjectives(holderId);
            UpdateObjectiveStatuses(holderId);
            if (_state.Agenda.LastActionDay.TryGetValue(holderId, out var last) && last == day) continue;
            if (TakeAction(holderId))
                _state.Agenda.LastActionDay[holderId] = day;
        }
    }

    private bool TakeAction(string holderId)
    {
        var objectives = ObjectivesOf(holderId).Where(o => o.Status == ObjectiveStatus.Active).ToList();
        return _state.Truth.HiddenRoles[holderId] == HiddenRole.Malvr
            ? ActAsMalvr(holderId, objectives)
            : ActAsLumiel(holderId, objectives);
    }

    // ------------------------------------------------------------- Malvr

    private bool ActAsMalvr(string holderId, List<HiddenObjective> objectives)
    {
        foreach (var o in objectives.Where(o => o.Kind == ObjectiveKind.EliminateObstacle))
            if (ActEliminateObstacle(holderId, o)) return true;
        foreach (var o in objectives.Where(o => o.Kind == ObjectiveKind.EvadeSuspicion))
            if (ActDeflectAttention(holderId)) return true;
        foreach (var o in objectives.Where(o => o.Kind == ObjectiveKind.SowDistrust))
            if (ActSowDistrust(holderId, o)) return true;
        return ActSpreadRumor(holderId);
    }

    /// <summary>
    /// Malvr has a crime committed against a chosen victim. The incident is public, but the
    /// link to the holder lives ONLY in <c>IncidentAttribution</c> — never in event data —
    /// so investigators see a crime, not a culprit.
    /// </summary>
    private bool ActEliminateObstacle(string holderId, HiddenObjective objective)
    {
        var target = _state.World.Characters[objective.TargetId!];
        if (!target.IsAlive) return false; // completed in the status update
        var crime = _crime.GenerateIncident(MurderDefinitionId(), target.Id);
        _state.Agenda.IncidentAttribution[crime.Id] = holderId;
        // The orchestrator knows their own scheme succeeded — recorded as a bland inference,
        // never naming the deed, so interviews stay clean.
        _cognition.Perceive(holderId, crime.IncidentEventId, MemorySource.Inferred, causedBy: crime.IncidentEventId);
        return true;
    }

    /// <summary>
    /// When one of the holder's own crimes has been discovered, muddy the water: spread word
    /// of it to a contact and nudge their suspicion toward a patsy. Attribution is the
    /// holder's own knowledge of their deed — not omniscience.
    /// </summary>
    private bool ActDeflectAttention(string holderId)
    {
        var hot = _state.Agenda.IncidentAttribution
            .Where(kv => kv.Value == holderId
                && _state.Crime.Crimes.TryGetValue(kv.Key, out var c)
                && _state.Crime.Scenes.TryGetValue(c.SceneId, out var s)
                && s.IsDiscovered)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        if (hot is null) return false;
        var crime = _state.Crime.Crimes[hot];
        var listeners = ContactListeners(holderId, crime.IncidentEventId);
        if (listeners.Count == 0) return false;
        var patsy = PickLiving(holderId, listeners[0]);
        _cognition.TellRumor(holderId, listeners[0], crime.IncidentEventId);
        _social.Adjust(listeners[0], patsy,
            new SocialDelta(Suspicion: AgendaRules.DeflectSuspicionDelta), "deflecting whispers");
        return true;
    }

    private bool ActSowDistrust(string holderId, HiddenObjective objective)
    {
        var a = objective.TargetId!;
        var b = objective.TargetId2!;
        if (_social.View(a, b).Trust < AgendaRules.SowCompleteTrust) return false; // completes in status update
        if (!InContact(holderId, a)) return false;
        var vehicle = KnownCrimes(holderId)
            .Select(c => c.IncidentEventId)
            .FirstOrDefault(id => !_cognition.Knows(a, id));
        if (vehicle != 0)
            _cognition.TellRumor(holderId, a, vehicle);
        _social.Adjust(a, b,
            new SocialDelta(Trust: AgendaRules.SowTrustDelta, Suspicion: AgendaRules.SowSuspicionDelta),
            "unsettling talk");
        return true;
    }

    /// <summary>Fallback destabilization: spread a known crime to a contact who hasn't heard it.</summary>
    private bool ActSpreadRumor(string holderId)
    {
        foreach (var crime in KnownCrimes(holderId))
        {
            var listeners = ContactListeners(holderId, crime.IncidentEventId);
            if (listeners.Count == 0) continue;
            _cognition.TellRumor(holderId, listeners[0], crime.IncidentEventId);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------- Lumiel

    private bool ActAsLumiel(string holderId, List<HiddenObjective> objectives)
    {
        foreach (var o in objectives.Where(o => o.Kind == ObjectiveKind.ProtectTarget))
            if (ActProtectTarget(holderId, o)) return true;
        foreach (var o in objectives.Where(o => o.Kind == ObjectiveKind.PursueLead))
            if (ActPursueLead(holderId, o)) return true;
        foreach (var o in objectives.Where(o => o.Kind == ObjectiveKind.GatherAlly))
            if (ActGatherAlly(holderId, o)) return true;
        return false;
    }

    /// <summary>
    /// Lumiel's trusted public cover at work: a quiet word that lifts police trust toward
    /// the target, plus a warning the target remembers. Skipped once the target is safe.
    /// </summary>
    private bool ActProtectTarget(string holderId, HiddenObjective objective)
    {
        var target = _state.World.Characters[objective.TargetId!];
        if (!target.IsAlive) return false; // failed in the status update
        var officers = _state.World.Characters.Values
            .Where(c => c.Kind == CharacterKind.Police && c.IsAlive && c.Id != target.Id)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        if (officers.Count > 0
            && officers.All(o => _social.View(o.Id, target.Id).Trust >= AgendaRules.ProtectTrustCap))
            return false;
        foreach (var o in officers)
            _social.Adjust(o.Id, target.Id,
                new SocialDelta(Trust: AgendaRules.ProtectTrustBoost),
                $"a trusted word for {target.DisplayName}");
        var warning = _events.Record(WorldEventTypes.AgendaWarning,
            participants: new[] { holderId, target.Id },
            data: new Dictionary<string, string> { ["to"] = target.Id });
        _cognition.Perceive(target.Id, warning.Id, MemorySource.Told,
            $"{_state.World.Characters[holderId].DisplayName} warned me to be careful.", warning.Id);
        return true;
    }

    /// <summary>
    /// Lumiel works a case they know about: interview a witness, then remember the exchange.
    /// Skips witnesses already interviewed about it recently.
    /// </summary>
    private bool ActPursueLead(string holderId, HiddenObjective objective)
    {
        var crime = _crime.GetCrime(objective.CrimeId!);
        if (CountKnownCrimeEvents(holderId, crime.Id) >= AgendaRules.PursueLeadKnownEventsNeeded)
            return false; // completes in the status update
        var topic = InvestigationRules.EventTopic(crime.IncidentEventId);
        var cutoff = _state.TotalMinutes - AgendaRules.InterviewCooldownMinutes;
        var witnesses = _crime.GetWitnesses(crime.Id)
            .Where(w => w != holderId
                && _state.World.Characters[w].IsAlive
                && !_state.Investigation.Statements.Values.Any(s =>
                    s.SpeakerId == w && s.Topic == topic && s.Timestamp > cutoff))
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();
        if (witnesses.Count == 0) return false;
        var result = _investigate.Interview(holderId, witnesses[0], topic, crime.Id);
        _cognition.Perceive(holderId, result.InterviewEventId, MemorySource.Witnessed,
            causedBy: result.InterviewEventId);
        return true;
    }

    private bool ActGatherAlly(string holderId, HiddenObjective objective)
    {
        var target = _state.World.Characters[objective.TargetId!];
        if (!target.IsAlive) return false;
        if (_social.View(holderId, target.Id).Trust >= AgendaRules.GatherCompleteTrust
            && _social.View(target.Id, holderId).Trust >= AgendaRules.GatherCompleteTrust)
            return false; // completes in the status update
        _social.Adjust(holderId, target.Id,
            new SocialDelta(Trust: AgendaRules.GatherTrustDelta, Affection: AgendaRules.GatherAffectionDelta),
            "growing alliance");
        _social.Adjust(target.Id, holderId,
            new SocialDelta(Trust: AgendaRules.GatherReturnTrustDelta), "growing alliance");
        return true;
    }

    // ------------------------------------------------------------- objective lifecycle

    private void UpdateObjectiveStatuses(string holderId)
    {
        var chars = _state.World.Characters;
        foreach (var o in ObjectivesOf(holderId).Where(o => o.Status == ObjectiveStatus.Active).ToList())
        {
            if (!chars[holderId].IsAlive) { FailObjective(o, "holder died"); continue; }
            switch (o.Kind)
            {
                case ObjectiveKind.EliminateObstacle:
                    if (!chars[o.TargetId!].IsAlive) CompleteObjective(o);
                    break;
                case ObjectiveKind.SowDistrust:
                    if (!chars[o.TargetId!].IsAlive || !chars[o.TargetId2!].IsAlive) FailObjective(o, "target died");
                    else if (_social.View(o.TargetId!, o.TargetId2!).Trust < AgendaRules.SowCompleteTrust)
                        CompleteObjective(o);
                    break;
                case ObjectiveKind.EvadeSuspicion:
                    break; // an ongoing posture: fails only with the holder
                case ObjectiveKind.ProtectTarget:
                    if (!chars[o.TargetId!].IsAlive) FailObjective(o, "target died");
                    break;
                case ObjectiveKind.GatherAlly:
                    if (!chars[o.TargetId!].IsAlive) FailObjective(o, "target died");
                    else if (_social.View(holderId, o.TargetId!).Trust >= AgendaRules.GatherCompleteTrust
                        && _social.View(o.TargetId!, holderId).Trust >= AgendaRules.GatherCompleteTrust)
                        CompleteObjective(o);
                    break;
                case ObjectiveKind.PursueLead:
                    if (CountKnownCrimeEvents(holderId, o.CrimeId!) >= AgendaRules.PursueLeadKnownEventsNeeded)
                        CompleteObjective(o);
                    break;
            }
        }
    }

    private void FailAllObjectives(string holderId, string reason)
    {
        foreach (var o in ObjectivesOf(holderId).Where(o => o.Status == ObjectiveStatus.Active).ToList())
            FailObjective(o, reason);
    }

    /// <summary>
    /// Lifecycle events carry only the objective id and kind — never the holder or the role.
    /// </summary>
    private void CompleteObjective(HiddenObjective o)
    {
        o.Status = ObjectiveStatus.Completed;
        _events.Record(WorldEventTypes.AgendaObjectiveCompleted,
            data: new Dictionary<string, string>
            {
                ["objective"] = o.Id,
                ["kind"] = o.Kind.ToString(),
            });
    }

    private void FailObjective(HiddenObjective o, string reason)
    {
        o.Status = ObjectiveStatus.Failed;
        _events.Record(WorldEventTypes.AgendaObjectiveFailed,
            data: new Dictionary<string, string>
            {
                ["objective"] = o.Id,
                ["kind"] = o.Kind.ToString(),
                ["reason"] = reason,
            });
    }

    // ------------------------------------------------------------------ deception (Phase 8 seam)

    /// <summary>
    /// The Phase 8 interview seam: a secretly genius NPC (never the player — the player
    /// decides their own lies) may give a false whereabouts claim when asked about a time
    /// near a crime they know about. Deterministic per (seed, holder, topic): the same
    /// question always gets the same answer. The false claim is recorded normally, so a
    /// sharp investigator can catch it against surveillance — that is the intended loop.
    /// </summary>
    public string? MaybeDeceive(string speakerId, string topic, string truthfulClaim)
    {
        if (!topic.StartsWith(InvestigationRules.WhereaboutsTopicPrefix, StringComparison.Ordinal))
            return null;
        if (!_state.Truth.HiddenRoles.ContainsKey(speakerId)) return null;
        if (speakerId == _state.Player.CharacterId) return null;
        if (!long.TryParse(topic.Substring(InvestigationRules.WhereaboutsTopicPrefix.Length), out var minute))
            return null;

        var nearKnownCrime = _state.Crime.Crimes.Values.Any(c =>
            Math.Abs(c.OccurredAt - minute) <= AgendaRules.DeceptionWindowMinutes
            && _cognition.Knows(speakerId, c.IncidentEventId));
        if (!nearKnownCrime) return null;

        if (StableHash.Compute(_state.Meta.RunSeed, "deceive", speakerId, topic) % 2 != 0) return null;

        InvestigationRules.TryParseWhereabouts(truthfulClaim, out var parsed);
        var alibi = AlibiLocation(speakerId, parsed?.LocationId);
        return InvestigationRules.FormatWhereabouts(alibi, minute);
    }

    /// <summary>A plausible alibi: home first, then work, then the first other location.</summary>
    private string AlibiLocation(string speakerId, string? truthfulLocationId)
    {
        var c = _state.World.Characters[speakerId];
        foreach (var candidate in new[] { c.HomeLocationId, c.WorkLocationId })
        {
            if (candidate is not null && candidate != truthfulLocationId && _content.HasLocation(candidate))
                return candidate;
        }
        return _content.Locations
            .Select(l => l.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .First(id => id != truthfulLocationId);
    }

    // ------------------------------------------------------------------ helpers

    private void RequireHolder(string holderId)
    {
        if (!_state.World.Characters.ContainsKey(holderId))
            throw new ArgumentException($"Unknown character '{holderId}'.");
        if (!_state.Truth.HiddenRoles.ContainsKey(holderId))
            throw new InvalidOperationException($"'{holderId}' holds no hidden role.");
    }

    private bool InContact(string a, string b)
    {
        if (a == b) return false;
        var chars = _state.World.Characters;
        if (chars[a].CurrentLocationId == chars[b].CurrentLocationId) return true;
        return _social.View(a, b).Exists || _social.View(b, a).Exists;
    }

    private List<string> ContactListeners(string holderId, long eventId) =>
        _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != holderId
                && !_cognition.Knows(c.Id, eventId)
                && InContact(holderId, c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .ToList();

    private List<CrimeRecord> KnownCrimes(string holderId) =>
        _state.Crime.Crimes.Values
            .Where(c => _cognition.Knows(holderId, c.IncidentEventId))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

    private int CountKnownCrimeEvents(string holderId, string crimeId) =>
        _state.EventLog.Events.Count(e =>
            e.Data.TryGetValue("crime", out var c) && c == crimeId && _cognition.Knows(holderId, e.Id));

    private string MurderDefinitionId()
    {
        var defs = _content.Crimes;
        var murder = defs.FirstOrDefault(d => d.Id == "murder");
        if (murder is not null) return murder.Id;
        if (defs.Count > 0) return defs[0].Id;
        throw new InvalidOperationException("No crime definitions loaded.");
    }
}
