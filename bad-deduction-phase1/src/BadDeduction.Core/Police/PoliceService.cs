using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Investigation;
using BadDeduction.Social;
using BadDeduction.World;

namespace BadDeduction.Police;

/// <summary>
/// Phase 9: the police subsystem. Independent officers with their own case views, a dynamic
/// trust ladder, a city-wide alert ladder, cordon enforcement and duty rosters.
/// <para/>
/// Officers are as blind as everyone else (ADR-003): they never read <c>WorldTruth</c>.
/// Their divergence comes from two legitimate sources — different knowledge (an officer who
/// missed a discovery literally has a different case file) and deterministic per-officer
/// interpretation of the same evidence (<see cref="ReadingOf"/>). Every mutation is logged
/// as a causally-linked <c>WorldEvent</c>.
/// </summary>
public sealed class PoliceService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly SocialService _social;
    private readonly InvestigationService _investigate;
    private readonly CrimeService _crime;
    private readonly CognitionService _cognition;
    private readonly ContentDatabase _content;

    public PoliceService(
        GameState state,
        EventSystem events,
        SocialService social,
        InvestigationService investigate,
        CrimeService crime,
        CognitionService cognition,
        ContentDatabase content)
    {
        _state = state;
        _events = events;
        _social = social;
        _investigate = investigate;
        _crime = crime;
        _cognition = cognition;
        _content = content;
        _events.Subscribe<DayChanged>(_ =>
        {
            if (_state.Police.DutiesDay != 0) AssignDuties();
            EvaluateAlert();
        });
        _events.Subscribe<WorldEventRecorded>(msg =>
        {
            var t = msg.Event.Type;
            if (t == WorldEventTypes.CrimeIncident
                || t == WorldEventTypes.CrimeDiscovered
                || t == WorldEventTypes.PoliceArrest)
                EvaluateAlert();
        });
    }

    // ------------------------------------------------------------------ roster

    /// <summary>Living police officers, id-ordered (deterministic).</summary>
    public IReadOnlyList<string> Officers() =>
        _state.World.Characters.Values
            .Where(c => c.Kind == CharacterKind.Police && c.IsAlive)
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private string RequireOfficer(string officerId)
    {
        if (!_state.World.Characters.TryGetValue(officerId, out var c) || c.Kind != CharacterKind.Police)
            throw new ArgumentException($"'{officerId}' is not a police officer.", nameof(officerId));
        if (!c.IsAlive) throw new InvalidOperationException($"Officer '{officerId}' is dead.");
        return officerId;
    }

    private void RequireSubject(string subjectId)
    {
        if (!_state.World.Characters.TryGetValue(subjectId, out var c))
            throw new ArgumentException($"Unknown character '{subjectId}'.", nameof(subjectId));
        if (!c.IsAlive) throw new InvalidOperationException($"'{subjectId}' is dead.");
    }

    /// <summary>
    /// Lazily-derived per-officer stream ("police.{officerId}"), persisted in PoliceState.
    /// Used only for tie-breaks and patrol choices — never for facts.
    /// </summary>
    private DeterministicRandom OfficerStream(string officerId)
    {
        var ps = _state.Police;
        if (!ps.OfficerRng.TryGetValue(officerId, out var snapshot))
        {
            snapshot = DeterministicRandom.Derive(_state.Meta.RunSeed, $"police.{officerId}").Snapshot();
            ps.OfficerRng[officerId] = snapshot;
        }
        return new DeterministicRandom(snapshot);
    }

    // ------------------------------------------------------------------ cordon

    /// <summary>
    /// The cordon rule. A location with a sealed AND discovered crime scene admits only police
    /// officers and the player character (the consultant). Undiscovered scenes are not cordoned:
    /// nobody knows to guard them yet, which keeps Phase 7 discovery semantics intact.
    /// Wired into <see cref="WorldService.AccessCheck"/> and the simulation's travel check.
    /// </summary>
    public bool CanEnter(string characterId, string locationId)
    {
        if (!_state.World.Characters.TryGetValue(characterId, out var c)) return false;
        var scene = _state.Crime.Scenes.Values
            .FirstOrDefault(s => s.LocationId == locationId
                && _state.World.Locations.TryGetValue(locationId, out var loc) && loc.IsSealed
                && s.IsDiscovered);
        if (scene is null) return true;
        if (c.Kind == CharacterKind.Police) return true;
        if (characterId == _state.Player.CharacterId) return true;
        return false;
    }

    // ------------------------------------------------------------------ duties

    /// <summary>
    /// Assigns today's duty roster. Idempotent per day. Guard covers sealed scenes first
    /// (undiscovered ones at Calm, all of them at Alert+), then patrol slots scale with the
    /// alert level, and the rest investigate from their station.
    /// </summary>
    public void AssignDuties()
    {
        var day = new GameTime(_state.TotalMinutes).Day;
        var ps = _state.Police;
        if (ps.DutiesDay == day) return;

        ps.Duties.Clear();
        var officers = Officers();
        var scenes = _state.Crime.Scenes.Values
            .Where(s => _state.World.Locations.TryGetValue(s.LocationId, out var loc) && loc.IsSealed)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var guardCount = Math.Min(scenes.Count, officers.Count);
        for (var i = 0; i < guardCount; i++)
        {
            var officer = officers[i];
            var scene = scenes[i];
            SetDuty(officer, day, new DutyAssignment
            {
                OfficerId = officer, Day = day, Kind = DutyKind.Guard,
                LocationId = scene.LocationId, SceneId = scene.Id,
            });
            assigned.Add(officer);
        }

        var free = officers.Where(o => !assigned.Contains(o)).ToList();
        var patrolSlots = Math.Min(PoliceRules.PatrolSlots(ps.Alert), free.Count);
        var candidates = _content.Locations
            .Where(l => _state.World.Locations.TryGetValue(l.Id, out var loc) && !loc.IsSealed)
            .Select(l => l.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < patrolSlots; i++)
        {
            var officer = free[i];
            var location = candidates.Count > 0
                ? candidates[OfficerStream(officer).NextInt(0, candidates.Count)]
                : _state.World.Characters[officer].HomeLocationId;
            SetDuty(officer, day, new DutyAssignment
            {
                OfficerId = officer, Day = day, Kind = DutyKind.Patrol, LocationId = location,
            });
            assigned.Add(officer);
        }

        foreach (var officer in free.Where(o => !assigned.Contains(o)))
        {
            var station = _state.World.Characters[officer].WorkLocationId
                ?? _state.World.Characters[officer].HomeLocationId;
            SetDuty(officer, day, new DutyAssignment
            {
                OfficerId = officer, Day = day, Kind = DutyKind.Investigate, LocationId = station,
            });
        }

        ps.DutiesDay = day;
    }

    private void SetDuty(string officerId, int day, DutyAssignment duty)
    {
        _state.Police.Duties[officerId] = new List<DutyAssignment> { duty };
        _events.Record(WorldEventTypes.PoliceDutyAssigned,
            participants: new[] { officerId },
            data: new Dictionary<string, string>
            {
                ["kind"] = duty.Kind.ToString(),
                ["location"] = duty.LocationId,
                ["day"] = day.ToString(),
            });
    }

    /// <summary>
    /// Seam for <see cref="WorldSimulation"/>: an officer's work blocks run at their duty
    /// location instead of their station. Null when no roster is active for that day.
    /// </summary>
    public string? DutyLocationFor(string officerId, int day)
    {
        var ps = _state.Police;
        if (ps.DutiesDay != day) return null;
        return ps.Duties.TryGetValue(officerId, out var duties) && duties.Count > 0
            ? duties[0].LocationId
            : null;
    }

    // ------------------------------------------------------------------ alert ladder

    /// <summary>
    /// Recomputes the city alert level from case facts and steps it at most one rung per call:
    /// Calm → Alert → Manhunt. Rises on undiscovered fatal violence or piling open cases;
    /// falls only when nothing is open anymore. Logs transitions.
    /// </summary>
    public AlertLevel EvaluateAlert()
    {
        var ps = _state.Police;
        var now = _state.TotalMinutes;
        var open = _state.Crime.Crimes.Values
            .Where(c => GetCaseStatus(c.Id).State != CaseState.InCustody)
            .ToList();
        var fatalOpen = open.Count(c => c.Fatal);
        var staleFatal = open.Any(c =>
            c.Fatal
            && _state.Crime.Scenes.TryGetValue(c.SceneId, out var scene) && !scene.IsDiscovered
            && now - c.OccurredAt >= PoliceRules.UndiscoveredFatalAlertMinutes);

        var current = ps.Alert;
        AlertLevel target = current;
        if (open.Count == 0)
        {
            target = current switch
            {
                AlertLevel.Manhunt => AlertLevel.Alert,
                AlertLevel.Alert => AlertLevel.Calm,
                _ => AlertLevel.Calm,
            };
        }
        else if (fatalOpen >= PoliceRules.ManhuntFatalCrimeCount || open.Count >= PoliceRules.ManhuntOpenCrimeCount)
        {
            target = current switch
            {
                AlertLevel.Calm => AlertLevel.Alert,
                AlertLevel.Alert => AlertLevel.Manhunt,
                _ => AlertLevel.Manhunt,
            };
        }
        else if (staleFatal || open.Count >= PoliceRules.AlertOpenCrimeCount)
        {
            target = current switch
            {
                AlertLevel.Calm => AlertLevel.Alert,
                _ => current,
            };
        }

        if (target != current)
        {
            ps.Alert = target;
            _events.Record(WorldEventTypes.PoliceAlertChanged,
                data: new Dictionary<string, string>
                {
                    ["from"] = current.ToString(),
                    ["to"] = target.ToString(),
                    ["open"] = open.Count.ToString(),
                });
        }
        return ps.Alert;
    }

    // ------------------------------------------------------------------ officer case work

    /// <summary>
    /// Proposition namespace for officer-owned theories: each officer keeps their own board,
    /// so two officers can hold different confidences about the same suspect.
    /// </summary>
    public static string PropositionFor(string officerId, string subjectId, string crimeId) =>
        $"{officerId}:suspect:{subjectId}:{crimeId}";

    private static bool TryParseProposition(string propositionId, out string officerId, out string subjectId, out string crimeId)
    {
        officerId = subjectId = crimeId = "";
        var parts = propositionId.Split(':');
        if (parts.Length != 4 || parts[1] != "suspect") return false;
        officerId = parts[0];
        subjectId = parts[2];
        crimeId = parts[3];
        return true;
    }

    /// <summary>
    /// An officer opens a case file: one owned hypothesis per living non-police character.
    /// Knowledge-gated — an officer who never heard of the incident cannot open the case.
    /// Returns the officer's hypotheses for this crime.
    /// </summary>
    public IReadOnlyList<Hypothesis> OpenCase(string officerId, string crimeId)
    {
        RequireOfficer(officerId);
        var crime = _crime.GetCrime(crimeId);
        if (!_cognition.Knows(officerId, crime.IncidentEventId))
            throw new InvalidOperationException(
                $"Officer '{officerId}' has no knowledge of incident {crime.IncidentEventId} and cannot open the case.");

        var label = _content.Crimes.First(c => c.Id == crime.DefinitionId).Label;
        var officerName = _state.World.Characters[officerId].DisplayName;
        foreach (var subject in _state.World.Characters.Values
                     .Where(c => c.IsAlive && c.Kind != CharacterKind.Police && c.Id != crime.VictimId)
                     .Select(c => c.Id)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var proposition = PropositionFor(officerId, subject, crimeId);
            if (_state.Investigation.Hypotheses.Values.Any(h => h.PropositionId == proposition)) continue;
            var subjectName = _state.World.Characters[subject].DisplayName;
            _investigate.ProposeHypothesis(proposition,
                $"Officer {officerName} suspects {subjectName} in the {label}.",
                crimeId, ownerId: officerId);
        }
        return OfficerHypotheses(officerId).Where(h => h.PropositionId.EndsWith($":{crimeId}", StringComparison.Ordinal)).ToList();
    }

    /// <summary>One officer's owned hypotheses, id-ordered.</summary>
    public IReadOnlyList<Hypothesis> OfficerHypotheses(string officerId) =>
        _state.Investigation.Hypotheses.Values
            .Where(h => h.OwnerId == officerId)
            .OrderBy(h => h.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// How one officer reads one evidence item against one suspect: a pure, order-independent
    /// function of (runSeed, officer, evidence, subject). Two officers may read the same
    /// evidence differently — deterministic disagreement from identical inputs.
    /// </summary>
    public EvidenceReading ReadingOf(string officerId, string evidenceId, string subjectId)
    {
        RequireOfficer(officerId);
        _crime.GetEvidence(evidenceId);
        RequireSubject(subjectId);
        var rng = DeterministicRandom.Derive(_state.Meta.RunSeed, $"police.reading.{officerId}.{evidenceId}.{subjectId}");
        return PoliceRules.Reading(rng.NextInt(0, 100));
    }

    /// <summary>
    /// The officer works their case: every owned hypothesis for this crime absorbs each
    /// discovered evidence item the OFFICER knows about, read their own way. Knowledge-gated
    /// throughout — an officer who missed a discovery works a thinner file.
    /// </summary>
    public void AssessEvidence(string officerId, string crimeId)
    {
        RequireOfficer(officerId);
        var crime = _crime.GetCrime(crimeId);
        var hypotheses = OfficerHypotheses(officerId)
            .Where(h => h.PropositionId.EndsWith($":{crimeId}", StringComparison.Ordinal))
            .ToList();

        foreach (var hyp in hypotheses)
        {
            if (!TryParseProposition(hyp.PropositionId, out _, out var subjectId, out _)) continue;
            foreach (var evidence in _crime.EvidenceAtScene(crime.SceneId).Where(e => e.Discovered))
            {
                if (!KnowsEvidence(officerId, evidence.Id)) continue;
                var reading = ReadingOf(officerId, evidence.Id, subjectId);
                if (reading == EvidenceReading.Inconclusive) continue;
                _investigate.AttachEvidence(hyp.Id, evidence.Id, reading == EvidenceReading.Supports);
            }
        }
        UpdateCaseState(crimeId);
    }

    /// <summary>An officer knows a piece of evidence when they know its discovery event.</summary>
    private bool KnowsEvidence(string officerId, string evidenceId)
    {
        foreach (var evt in _state.EventLog.Events)
        {
            if (evt.Type != WorldEventTypes.EvidenceDiscovered) continue;
            if (!evt.Data.TryGetValue("evidence", out var id) || id != evidenceId) continue;
            return _cognition.Knows(officerId, evt.Id);
        }
        return false;
    }

    /// <summary>Minimal case lifecycle: Believes-band officer theory ⇒ PersonOfInterest.</summary>
    private void UpdateCaseState(string crimeId)
    {
        var ps = _state.Police;
        if (ps.Cases.TryGetValue(crimeId, out var existing) && existing.State == CaseState.InCustody) return;
        var believer = _state.Investigation.Hypotheses.Values
            .Where(h => h.OwnerId is not null && h.PropositionId.EndsWith($":{crimeId}", StringComparison.Ordinal)
                        && h.Band == BeliefBand.Believes)
            .OrderBy(h => h.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (believer is not null && TryParseProposition(believer.PropositionId, out _, out var subjectId, out _))
            ps.Cases[crimeId] = new CaseStatus { State = CaseState.PersonOfInterest, SubjectId = subjectId };
        else if (!ps.Cases.ContainsKey(crimeId))
            ps.Cases[crimeId] = new CaseStatus { State = CaseState.Open };
    }

    public CaseStatus GetCaseStatus(string crimeId)
    {
        _crime.GetCrime(crimeId); // validates
        return _state.Police.Cases.TryGetValue(crimeId, out var status)
            ? status
            : new CaseStatus { State = CaseState.Open };
    }

    public bool IsInCustody(string characterId) =>
        _state.Police.Cases.Values.Any(c => c.State == CaseState.InCustody && c.SubjectId == characterId);

    // ------------------------------------------------------------------ trust ladder dynamics

    /// <summary>
    /// Recomputes how an officer sees a subject from the officer's OWN case file: owned
    /// hypotheses move trust/suspicion, and the Phase 3 stance ladder follows. Nudges double
    /// during a Manhunt. Returns the resulting stance.
    /// </summary>
    public PoliceStance EvaluateSubject(string officerId, string subjectId)
    {
        RequireOfficer(officerId);
        RequireSubject(subjectId);
        var mult = _state.Police.Alert == AlertLevel.Manhunt ? PoliceRules.ManhuntNudgeMultiplier : 1;

        foreach (var hyp in OfficerHypotheses(officerId)
                     .Where(h => h.PropositionId.StartsWith($"{officerId}:suspect:{subjectId}:", StringComparison.Ordinal))
                     .OrderBy(h => h.Id, StringComparer.Ordinal))
        {
            var (trust, suspicion) = hyp.Band switch
            {
                BeliefBand.Believes => (PoliceRules.BelievesTrustDelta * mult, PoliceRules.BelievesSuspicionDelta * mult),
                BeliefBand.Suspect => (PoliceRules.SuspectTrustDelta * mult, PoliceRules.SuspectSuspicionDelta * mult),
                _ => (0, 0),
            };
            if (hyp.Band == BeliefBand.Dismissed && hyp.RefutingEvidenceIds.Count >= PoliceRules.DismissedRefutingNeeded)
                (trust, suspicion) = (PoliceRules.DismissedTrustDelta * mult, PoliceRules.DismissedSuspicionDelta * mult);
            if (trust == 0 && suspicion == 0) continue;
            _social.Adjust(officerId, subjectId,
                new SocialDelta(Trust: trust, Suspicion: suspicion), "police case evaluation");
        }
        return _social.StanceToward(officerId, subjectId);
    }

    // ------------------------------------------------------------------ arrest

    /// <summary>
    /// Bounded arrest: the officer's stance must be SuspicionOfSubject, their own hypothesis
    /// about the subject must sit at the Believes band, and at least two discovered evidence
    /// items must support it. Wrong arrests are possible — the hypothesis may rest on false
    /// evidence — and are simply logged; trial and sentencing stay out of scope.
    /// </summary>
    public WorldEvent Arrest(string officerId, string subjectId, string crimeId)
    {
        RequireOfficer(officerId);
        RequireSubject(subjectId);
        _crime.GetCrime(crimeId);
        if (IsInCustody(subjectId))
            throw new InvalidOperationException($"'{subjectId}' is already in custody.");

        var stance = _social.StanceToward(officerId, subjectId);
        if (stance != PoliceStance.SuspicionOfSubject)
            throw new InvalidOperationException(
                $"Officer '{officerId}' cannot arrest '{subjectId}': stance is {stance}, not SuspicionOfSubject.");

        var hyp = OfficerHypotheses(officerId)
            .FirstOrDefault(h => h.PropositionId == PropositionFor(officerId, subjectId, crimeId))
            ?? throw new InvalidOperationException($"Officer '{officerId}' holds no hypothesis about '{subjectId}'.");
        if (hyp.Band != BeliefBand.Believes)
            throw new InvalidOperationException(
                $"Officer '{officerId}' cannot arrest '{subjectId}': hypothesis is {hyp.Band}, not Believes.");
        if (hyp.SupportingEvidenceIds.Count < PoliceRules.ArrestEvidenceNeeded)
            throw new InvalidOperationException(
                $"Officer '{officerId}' cannot arrest '{subjectId}': only {hyp.SupportingEvidenceIds.Count} supporting evidence items.");

        _state.Police.Cases[crimeId] = new CaseStatus { State = CaseState.InCustody, SubjectId = subjectId };
        var logged = _events.Record(WorldEventTypes.PoliceArrest,
            participants: new[] { officerId, subjectId },
            data: new Dictionary<string, string>
            {
                ["crime"] = crimeId,
                ["hypothesis"] = hyp.Id,
                ["confidence"] = hyp.Confidence.ToString(),
            });
        EvaluateAlert();
        return logged;
    }
}
