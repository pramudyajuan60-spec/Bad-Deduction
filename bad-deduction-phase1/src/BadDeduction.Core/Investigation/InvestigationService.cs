using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Crime;
using BadDeduction.Social;

namespace BadDeduction.Investigation;

/// <summary>The outcome of one interview or interrogation.</summary>
public sealed class InterviewResult
{
    public string StatementId { get; set; } = "";
    public string Answer { get; set; } = "";
    public long InterviewEventId { get; set; }
    public bool Pressured { get; set; }
}

/// <summary>The outcome of recording one statement, including any contradictions it triggered.</summary>
public sealed class RecordResult
{
    public string StatementId { get; set; } = "";
    public IReadOnlyList<string> ContradictionIds { get; set; } = Array.Empty<string>();
    public int FlaggedCount { get; set; }
}

/// <summary>
/// Phase 8: the only sanctioned way to investigate — survey locations, interview and
/// interrogate characters, record their statements, detect contradictions, and work
/// hypotheses. All notable mutations are logged as causally-linked WorldEvents.
/// <para/>
/// Design split (documented): interviews are STRUCTURED knowledge queries — the NPC answers
/// from what it actually knows (the Phase 4 gate), so testimony is consistent with memory.
/// Free-text conversation stays in <c>DialogueOrchestrator.Exchange</c> (Phase 6). A claim
/// recorded here is what the speaker SAID, not ground truth: lies, mistakes and vague
/// answers are exactly what contradiction detection is for.
/// <para/>
/// Nothing here reads hidden roles (ADR-003/ADR-015): contradictions are about CONSISTENCY —
/// statement vs statement, statement vs surveillance — never about truth.
/// </summary>
public sealed class InvestigationService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly CognitionService _cognition;
    private readonly CrimeService _crime;
    private readonly SocialService _social;
    private readonly ContentDatabase _content;

    public InvestigationService(
        GameState state,
        EventSystem events,
        CognitionService cognition,
        CrimeService crime,
        SocialService social,
        ContentDatabase content)
    {
        _state = state;
        _events = events;
        _cognition = cognition;
        _crime = crime;
        _social = social;
        _content = content;
    }

    // ------------------------------------------------------------------ reads

    public Statement GetStatement(string statementId) =>
        _state.Investigation.Statements.TryGetValue(statementId, out var s)
            ? s
            : throw new ArgumentException($"Unknown statement '{statementId}'.", nameof(statementId));

    public IReadOnlyList<Statement> StatementsBy(string speakerId)
    {
        RequireCharacter(speakerId);
        return _state.Investigation.Statements.Values
            .Where(s => s.SpeakerId == speakerId)
            .ToList();
    }

    public Contradiction GetContradiction(string contradictionId) =>
        _state.Investigation.Contradictions.TryGetValue(contradictionId, out var c)
            ? c
            : throw new ArgumentException($"Unknown contradiction '{contradictionId}'.", nameof(contradictionId));

    public IReadOnlyList<Contradiction> ContradictionsFor(string statementId)
    {
        GetStatement(statementId); // validates
        return _state.Investigation.Contradictions.Values
            .Where(c => c.StatementId == statementId)
            .ToList();
    }

    public Hypothesis GetHypothesis(string hypothesisId) =>
        _state.Investigation.Hypotheses.TryGetValue(hypothesisId, out var h)
            ? h
            : throw new ArgumentException($"Unknown hypothesis '{hypothesisId}'.", nameof(hypothesisId));

    public IReadOnlyList<Hypothesis> AllHypotheses() =>
        _state.Investigation.Hypotheses.Values
            .OrderBy(h => h.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The case file for the investigation board: the crime's incident chain merged with every
    /// investigation action filed under the case (interviews, statements, contradictions,
    /// surveys, hypothesis work), in timestamp order. Read-only.
    /// </summary>
    public IReadOnlyList<WorldEvent> GetCaseTimeline(string crimeId)
    {
        _crime.GetCrime(crimeId); // validates
        return _state.EventLog.Events
            .Where(e => e.Data.TryGetValue("crime", out var c) && c == crimeId)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .ToList();
    }

    // ------------------------------------------------------------------ surveillance

    /// <summary>
    /// Reconstructs who was present at a location during [fromMinute, toMinute), derived
    /// purely from the movement log — no new state, no RNG. The request itself is logged as
    /// <c>investigation.surveyed</c> so it shows up on the case timeline.
    /// </summary>
    public SurveillanceRecord SurveyLocation(
        string locationId, long fromMinute, long toMinute, string? crimeId = null)
    {
        _content.GetLocation(locationId); // validates: throws KeyNotFoundException when unknown
        if (crimeId is not null) _crime.GetCrime(crimeId); // validates
        var entries = PresenceTracker.IntervalsAt(_state, locationId, fromMinute, toMinute);

        var data = new Dictionary<string, string>
        {
            ["location"] = locationId,
            ["from"] = fromMinute.ToString(),
            ["to"] = toMinute.ToString(),
            ["sightings"] = entries.Count.ToString(),
        };
        if (crimeId is not null) data["crime"] = crimeId;
        _events.Record(WorldEventTypes.Surveyed, locationId: locationId, data: data);

        return new SurveillanceRecord
        {
            LocationId = locationId,
            WindowStart = fromMinute,
            WindowEnd = toMinute,
            Entries = entries.ToList(),
        };
    }

    // ------------------------------------------------------------------ interviews

    /// <summary>
    /// A structured interview: the speaker answers a topic from what they actually know.
    /// Topics: "event:{eventId}", "person:{characterId}", "whereabouts:{minute}".
    /// The answer is recorded as a statement and checked for contradictions.
    /// </summary>
    public InterviewResult Interview(
        string interviewerId, string speakerId, string topic,
        string? crimeId = null, long? causedBy = null) =>
        ConductInterview(interviewerId, speakerId, topic, pressured: false, crimeId, causedBy);

    /// <summary>
    /// An interrogation: an interview under pressure. The pressure is real — the speaker's
    /// suspicion of the interrogator rises — and it is recorded on the event for the record.
    /// </summary>
    public InterviewResult Interrogate(
        string interviewerId, string speakerId, string topic,
        string? crimeId = null, long? causedBy = null) =>
        ConductInterview(interviewerId, speakerId, topic, pressured: true, crimeId, causedBy);

    private InterviewResult ConductInterview(
        string interviewerId, string speakerId, string topic, bool pressured,
        string? crimeId, long? causedBy)
    {
        var interviewer = RequireAlive(interviewerId);
        var speaker = RequireAlive(speakerId);
        if (interviewerId == speakerId)
            throw new ArgumentException("A character cannot interview itself.");
        if (!InvestigationRules.IsValidTopic(topic))
            throw new ArgumentException($"Invalid interview topic '{topic}'.", nameof(topic));
        ValidateTopicTarget(topic);
        var resolvedCrime = ResolveCrime(crimeId, topic);

        var (answer, claim) = AnswerTopic(speaker, topic);

        var data = new Dictionary<string, string>
        {
            ["topic"] = topic,
            ["answer"] = answer,
        };
        if (resolvedCrime is not null) data["crime"] = resolvedCrime;
        if (pressured) data["pressure"] = "true";
        var interviewEvent = _events.Record(
            pressured ? WorldEventTypes.Interrogated : WorldEventTypes.Interviewed,
            locationId: speaker.CurrentLocationId,
            participants: new[] { interviewerId, speakerId },
            data: data,
            causedBy: causedBy);

        if (pressured)
            _social.Adjust(speakerId, interviewerId,
                new SocialDelta(Suspicion: 5), "interrogation pressure", causedBy: interviewEvent.Id);

        var recorded = RecordStatement(speakerId, topic, claim, resolvedCrime, interviewEvent.Id);
        return new InterviewResult
        {
            StatementId = recorded.StatementId,
            Answer = answer,
            InterviewEventId = interviewEvent.Id,
            Pressured = pressured,
        };
    }

    /// <summary>
    /// Builds the speaker's answer — and the structured claim recorded for it — purely from
    /// the speaker's own knowledge. Unknown events get the canonical "don't know"; whereabouts
    /// are reconstructed from the speaker's own movement record (truthful for now; deliberate
    /// lies arrive with hidden objectives in Phase 10).
    /// </summary>
    private (string Answer, string Claim) AnswerTopic(CharacterState speaker, string topic)
    {
        if (topic.StartsWith(InvestigationRules.EventTopicPrefix, StringComparison.Ordinal))
        {
            var eventId = long.Parse(topic.Substring(InvestigationRules.EventTopicPrefix.Length));
            if (!_cognition.Knows(speaker.Id, eventId))
                return (InvestigationRules.DontKnowClaim, InvestigationRules.DontKnowClaim);
            var memory = _cognition.GetMemories(speaker.Id).LastOrDefault(m => m.EventId == eventId);
            var summary = memory?.Summary ?? InvestigationRules.DontKnowClaim;
            return (summary, summary);
        }
        if (topic.StartsWith(InvestigationRules.PersonTopicPrefix, StringComparison.Ordinal))
        {
            var personId = topic.Substring(InvestigationRules.PersonTopicPrefix.Length);
            if (personId == speaker.Id) return ("That's me.", "That's me.");
            var person = _state.World.Characters[personId];
            var view = _social.View(speaker.Id, personId);
            if (!view.Exists || view.Kind is null)
            {
                var vague = $"I don't really know {person.DisplayName}.";
                return (vague, vague);
            }
            var answer = $"{person.DisplayName} and I are {view.Kind}.";
            return (answer, answer);
        }
        var minute = long.Parse(topic.Substring(InvestigationRules.WhereaboutsTopicPrefix.Length));
        var (location, travelFrom, travelTo) = PresenceTracker.PresenceAt(_state, speaker.Id, minute);
        if (location is not null)
        {
            var name = _content.GetLocation(location).Name;
            return ($"I was at {name}.", InvestigationRules.FormatWhereabouts(location, minute));
        }
        var fromName = _content.GetLocation(travelFrom!).Name;
        var toName = _content.GetLocation(travelTo!).Name;
        return ($"I was traveling from {fromName} to {toName}.",
            InvestigationRules.FormatTraveling(travelFrom!, travelTo!, minute));
    }

    // ------------------------------------------------------------------ statements

    /// <summary>
    /// Records what the speaker SAID — testimony, not ground truth. The claim is checked
    /// against the speaker's earlier statements for contradictions (difficulty-gated), and
    /// every check is persisted: flagged clashes are logged, sub-threshold ones are kept
    /// unflagged for the record.
    /// </summary>
    public RecordResult RecordStatement(
        string speakerId, string topic, string claim, string? crimeId = null, long? causedBy = null)
    {
        var speaker = RequireAlive(speakerId);
        if (!InvestigationRules.IsValidTopic(topic))
            throw new ArgumentException($"Invalid statement topic '{topic}'.", nameof(topic));
        ValidateTopicTarget(topic);
        if (string.IsNullOrWhiteSpace(claim))
            throw new ArgumentException("A claim is required.", nameof(claim));
        if (claim.Length > InvestigationRules.MaxClaimLength)
            throw new ArgumentException($"Claim exceeds {InvestigationRules.MaxClaimLength} characters.", nameof(claim));
        var resolvedCrime = ResolveCrime(crimeId, topic);

        var inv = _state.Investigation;
        var statement = new Statement
        {
            Id = $"stmt_{inv.NextStatementId++}",
            SpeakerId = speakerId,
            Topic = topic,
            Claim = claim,
            CrimeId = resolvedCrime,
            Timestamp = _state.TotalMinutes,
            SourceEventId = causedBy,
        };
        inv.Statements.Add(statement.Id, statement);

        var data = new Dictionary<string, string>
        {
            ["statement"] = statement.Id,
            ["speaker"] = speakerId,
            ["topic"] = topic,
            ["claim"] = claim,
        };
        if (resolvedCrime is not null) data["crime"] = resolvedCrime;
        _events.Record(WorldEventTypes.StatementRecorded,
            locationId: speaker.CurrentLocationId,
            participants: new[] { speakerId },
            data: data,
            causedBy: causedBy);

        var contradictionIds = CheckContradictions(statement);
        return new RecordResult
        {
            StatementId = statement.Id,
            ContradictionIds = contradictionIds,
            FlaggedCount = contradictionIds.Count(id => inv.Contradictions[id].Flagged),
        };
    }

    /// <summary>
    /// Cross-checks one whereabouts statement against surveillance: was the speaker really
    /// where they claimed? Investigator-initiated, so a found mismatch is always flagged.
    /// Returns the contradiction, or null when the claim holds (or is not a whereabouts claim).
    /// </summary>
    public Contradiction? CheckAgainstSurveillance(string statementId)
    {
        var statement = GetStatement(statementId);
        if (!InvestigationRules.TryParseWhereabouts(statement.Claim, out var claim) || claim is null)
            return null;
        var (actual, travelFrom, travelTo) = PresenceTracker.PresenceAt(_state, statement.SpeakerId, claim.Minute);
        if (claim.LocationId == actual) return null;

        var inv = _state.Investigation;
        var claimedDesc = claim.LocationId ?? $"traveling from {claim.TravelFrom} to {claim.TravelTo}";
        var actualDesc = actual ?? $"traveling from {travelFrom} to {travelTo}";
        var contra = new Contradiction
        {
            Id = $"contra_{inv.NextContradictionId++}",
            StatementId = statement.Id,
            AgainstStatementId = null,
            Severity = ContradictionSeverity.Blatant,
            Reason = $"surveillance places them at {actualDesc}, not {claimedDesc}",
            Flagged = true,
            Timestamp = _state.TotalMinutes,
        };
        inv.Contradictions.Add(contra.Id, contra);

        var data = new Dictionary<string, string>
        {
            ["contradiction"] = contra.Id,
            ["statement"] = statement.Id,
            ["severity"] = contra.Severity.ToString(),
            ["reason"] = contra.Reason,
        };
        if (statement.CrimeId is not null) data["crime"] = statement.CrimeId;
        _events.Record(WorldEventTypes.ContradictionFound,
            participants: new[] { statement.SpeakerId },
            data: data,
            causedBy: statement.SourceEventId);
        return contra;
    }

    // ------------------------------------------------------------------ contradiction detection

    private sealed record Clash(ContradictionSeverity Severity, int Score, string Reason);

    /// <summary>
    /// Compares a new statement against the speaker's earlier ones. Every incompatibility is
    /// persisted; only those reaching the difficulty bar are flagged (and logged). Statement
    /// dictionaries enumerate in insertion order, so each pair is examined exactly once —
    /// when the later statement is recorded.
    /// </summary>
    private IReadOnlyList<string> CheckContradictions(Statement current)
    {
        var inv = _state.Investigation;
        var difficulty = _state.Meta.Difficulty;
        var threshold = InvestigationRules.FlagThreshold(difficulty);
        var window = InvestigationRules.BlatantWindowMinutes(difficulty);
        var found = new List<string>();

        foreach (var prior in inv.Statements.Values)
        {
            if (prior.Id == current.Id) continue;
            if (prior.SpeakerId != current.SpeakerId) continue;
            if (!InvestigationRules.TopicsCompatible(prior.Topic, current.Topic)) continue;
            var clash = DetectClash(prior, current, window);
            if (clash is null) continue;

            var flagged = clash.Score >= threshold;
            var contra = new Contradiction
            {
                Id = $"contra_{inv.NextContradictionId++}",
                StatementId = current.Id,
                AgainstStatementId = prior.Id,
                Severity = clash.Severity,
                Reason = clash.Reason,
                Flagged = flagged,
                Timestamp = _state.TotalMinutes,
            };
            inv.Contradictions.Add(contra.Id, contra);
            found.Add(contra.Id);

            if (flagged)
            {
                var data = new Dictionary<string, string>
                {
                    ["contradiction"] = contra.Id,
                    ["statement"] = current.Id,
                    ["against"] = prior.Id,
                    ["severity"] = clash.Severity.ToString(),
                    ["reason"] = clash.Reason,
                    ["score"] = clash.Score.ToString(),
                };
                if (current.CrimeId is not null) data["crime"] = current.CrimeId;
                _events.Record(WorldEventTypes.ContradictionFound,
                    participants: new[] { current.SpeakerId },
                    data: data,
                    causedBy: current.SourceEventId);
            }
        }
        return found;
    }

    /// <summary>
    /// The incompatibility model (documented): whereabouts claims are structured, so "two
    /// places at once" is exact; same-topic free text is compared for denial-then-claim and
    /// changed accounts. Consistency only — never truth.
    /// </summary>
    private static Clash? DetectClash(Statement prior, Statement current, int blatantWindow)
    {
        if (InvestigationRules.TryParseWhereabouts(prior.Claim, out var p) && p is not null &&
            InvestigationRules.TryParseWhereabouts(current.Claim, out var c) && c is not null)
        {
            var samePlace = p.LocationId == c.LocationId; // null == null: both traveling
            if (Math.Abs(p.Minute - c.Minute) < blatantWindow && !samePlace)
                return new Clash(ContradictionSeverity.Blatant, InvestigationRules.BlatantScore,
                    $"claims to have been at {DescribePlace(p)} and at {DescribePlace(c)} within {blatantWindow} minutes");
            if (samePlace && p.LocationId is not null &&
                Math.Abs(p.Minute - c.Minute) < blatantWindow &&
                p.Detail.Length > 0 && c.Detail.Length > 0 && p.Detail != c.Detail)
                return new Clash(ContradictionSeverity.Subtle, InvestigationRules.SubtleScore,
                    $"gives conflicting details for {p.LocationId}: \"{p.Detail}\" vs \"{c.Detail}\"");
            return null;
        }
        if (prior.Topic != current.Topic) return null;

        var priorDenied = prior.Claim == InvestigationRules.DontKnowClaim;
        var currentDenied = current.Claim == InvestigationRules.DontKnowClaim;
        if (priorDenied != currentDenied)
            return new Clash(ContradictionSeverity.Subtle, InvestigationRules.DenialThenClaimedScore,
                priorDenied ? "denied knowledge, then claimed details" : "claimed details, then denied knowledge");
        if (!priorDenied && prior.Claim != current.Claim)
            return new Clash(ContradictionSeverity.Subtle, InvestigationRules.SubtleScore,
                "changed their account");
        return null;
    }

    private static string DescribePlace(WhereaboutsClaim claim) =>
        claim.LocationId ?? $"traveling from {claim.TravelFrom} to {claim.TravelTo}";

    // ------------------------------------------------------------------ hypotheses

    /// <summary>
    /// Pins a new theory to the investigation board. Proposition ids are unique: one card
    /// per theory. Confidence starts neutral (50, dismissed) until evidence is attached.
    /// An explicit case link files it under that case's timeline.
    /// </summary>
    public Hypothesis ProposeHypothesis(
        string propositionId, string description, string? crimeId = null, long? causedBy = null)
    {
        if (string.IsNullOrWhiteSpace(propositionId))
            throw new ArgumentException("A proposition id is required.", nameof(propositionId));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A description is required.", nameof(description));
        if (crimeId is not null) _crime.GetCrime(crimeId); // validates
        var inv = _state.Investigation;
        if (inv.Hypotheses.Values.Any(h => h.PropositionId == propositionId))
            throw new InvalidOperationException($"A hypothesis for '{propositionId}' already exists.");

        var hyp = new Hypothesis
        {
            Id = $"hyp_{inv.NextHypothesisId++}",
            PropositionId = propositionId,
            Description = description,
            UpdatedAt = _state.TotalMinutes,
        };
        inv.Hypotheses.Add(hyp.Id, hyp);
        var data = new Dictionary<string, string>
        {
            ["hypothesis"] = hyp.Id,
            ["proposition"] = propositionId,
            ["description"] = description,
        };
        if (crimeId is not null) data["crime"] = crimeId;
        _events.Record(WorldEventTypes.HypothesisProposed, data: data, causedBy: causedBy);
        return hyp;
    }

    /// <summary>
    /// Attaches a piece of evidence to a hypothesis, for or against. The evidence must be
    /// discovered — you cannot theorize about what you never found — but false evidence may
    /// absolutely be attached (its authenticity never changes; ADR-035). Only band
    /// transitions are logged; quiet accumulation is not. Returns the logged event, or null
    /// when nothing changed.
    /// </summary>
    public WorldEvent? AttachEvidence(string hypothesisId, string evidenceId, bool supports, long? causedBy = null)
    {
        var inv = _state.Investigation;
        if (!inv.Hypotheses.TryGetValue(hypothesisId, out var hyp))
            throw new ArgumentException($"Unknown hypothesis '{hypothesisId}'.", nameof(hypothesisId));
        var evidence = _crime.GetEvidence(evidenceId); // throws KeyNotFoundException when unknown
        if (!evidence.Discovered)
            throw new InvalidOperationException($"Evidence '{evidenceId}' is still undiscovered and cannot support a hypothesis.");

        var target = supports ? hyp.SupportingEvidenceIds : hyp.RefutingEvidenceIds;
        var other = supports ? hyp.RefutingEvidenceIds : hyp.SupportingEvidenceIds;
        if (target.Contains(evidenceId)) return null; // already attached here: no-op
        other.Remove(evidenceId);

        var before = hyp.Band;
        target.Add(evidenceId);
        hyp.UpdatedAt = _state.TotalMinutes;
        var after = hyp.Band;
        if (after == before) return null;

        var crimeId = _crime.GetScene(evidence.SceneId).CrimeId;
        return _events.Record(WorldEventTypes.HypothesisUpdated,
            data: new Dictionary<string, string>
            {
                ["hypothesis"] = hyp.Id,
                ["proposition"] = hyp.PropositionId,
                ["from"] = before.ToString(),
                ["to"] = after.ToString(),
                ["confidence"] = hyp.Confidence.ToString(),
                ["for"] = hyp.SupportingEvidenceIds.Count.ToString(),
                ["against"] = hyp.RefutingEvidenceIds.Count.ToString(),
                ["crime"] = crimeId,
            },
            causedBy: causedBy);
    }

    // ------------------------------------------------------------------ helpers

    private void ValidateTopicTarget(string topic)
    {
        if (topic.StartsWith(InvestigationRules.EventTopicPrefix, StringComparison.Ordinal))
        {
            if (!long.TryParse(topic.Substring(InvestigationRules.EventTopicPrefix.Length), out var eventId) ||
                !_state.EventLog.TryGet(eventId, out _))
                throw new ArgumentException($"Topic references unknown event '{topic}'.", nameof(topic));
        }
        else if (topic.StartsWith(InvestigationRules.PersonTopicPrefix, StringComparison.Ordinal))
        {
            var id = topic.Substring(InvestigationRules.PersonTopicPrefix.Length);
            if (!_state.World.Characters.ContainsKey(id))
                throw new ArgumentException($"Topic references unknown character '{topic}'.", nameof(topic));
        }
        else if (topic.StartsWith(InvestigationRules.WhereaboutsTopicPrefix, StringComparison.Ordinal))
        {
            if (!long.TryParse(topic.Substring(InvestigationRules.WhereaboutsTopicPrefix.Length), out var minute) || minute < 0)
                throw new ArgumentException($"Topic has an invalid minute '{topic}'.", nameof(topic));
            if (minute > _state.TotalMinutes)
                throw new ArgumentException("Cannot ask about the future.", nameof(topic));
        }
    }

    /// <summary>
    /// Links an investigation action to a case: explicit beats derived. Event topics about a
    /// crime's incident event resolve to that crime automatically.
    /// </summary>
    private string? ResolveCrime(string? crimeId, string topic)
    {
        if (crimeId is not null)
        {
            _crime.GetCrime(crimeId); // validates
            return crimeId;
        }
        if (topic.StartsWith(InvestigationRules.EventTopicPrefix, StringComparison.Ordinal) &&
            long.TryParse(topic.Substring(InvestigationRules.EventTopicPrefix.Length), out var eventId))
        {
            foreach (var c in _crime.AllCrimes())
                if (c.IncidentEventId == eventId) return c.Id;
        }
        return null;
    }

    private CharacterState RequireAlive(string id)
    {
        if (!_state.World.Characters.TryGetValue(id, out var c))
            throw new ArgumentException($"Unknown character '{id}'.");
        if (!c.IsAlive)
            throw new InvalidOperationException($"'{id}' is dead and cannot be interviewed.");
        return c;
    }

    private void RequireCharacter(string id)
    {
        if (!_state.World.Characters.ContainsKey(id))
            throw new ArgumentException($"Unknown character '{id}'.");
    }
}
