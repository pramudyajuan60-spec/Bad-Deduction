using BadDeduction.AI;
using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Police;
using BadDeduction.Social;
using BadDeduction.World;

namespace BadDeduction.Manipulation;

/// <summary>
/// Phase 14: the manipulation layer — order evaluation and execution, despair and
/// suicide, lure destinations, and knowledge-gated routine observation.
/// <para/>
/// Engine-free and deterministic: evaluation is pure (via <see cref="ComplianceEvaluator"/>),
/// execution goes through sanctioned services and logs <c>WorldEvent</c>s, and every
/// number stays integer. Tiers come from <see cref="ManipulabilityRules.TierFor"/>
/// (pure function of seed + id), so no RNG stream is consumed here.
/// </summary>
public sealed class ManipulationService
{
    private readonly GameState _state;
    private readonly EventSystem _events;
    private readonly SocialService _social;
    private readonly ComplianceEvaluator _compliance;
    private readonly CognitionService _cognition;
    private readonly PoliceService _police;
    private readonly ContentDatabase _content;
    private readonly ScheduleSystem _schedules;

    public ManipulationService(
        GameState state,
        EventSystem events,
        SocialService social,
        ComplianceEvaluator compliance,
        CognitionService cognition,
        PoliceService police,
        ContentDatabase content,
        ScheduleSystem schedules)
    {
        _state = state;
        _events = events;
        _social = social;
        _compliance = compliance;
        _cognition = cognition;
        _police = police;
        _content = content;
        _schedules = schedules;
    }

    // ------------------------------------------------------------------ tiers

    /// <summary>The NPC's manipulability tier: a pure function of (runSeed, id).</summary>
    public ManipulabilityTier TierOf(string characterId) =>
        ManipulabilityRules.TierFor(_state.Meta.RunSeed, characterId);

    // ------------------------------------------------------------------ orders

    /// <summary>
    /// Scores a detected player order. Pure: never mutates state. The tier contributes
    /// a bounded extra factor; the ADR-014 guarantee (no trust value forces a severe
    /// order) holds because tier bonuses are small next to risk/morality pushback.
    /// A target that names a living character (e.g. an Attack victim) is resolved so
    /// loyalty/fear toward them weighs into the decision.
    /// </summary>
    public ComplianceDecision EvaluateOrder(string npcId, string playerId, DetectedOrder order)
    {
        var targetId = order.Kind == OrderKind.Attack ? ResolvePerson(order.TargetText, npcId, playerId) : null;
        var request = ComplianceRules.ToActionRequest(order, targetId);
        var tier = TierOf(npcId);
        return _compliance.Evaluate(npcId, playerId, request,
            new[] { new ComplianceFactor($"tier:{tier}", ManipulabilityRules.ComplianceBonus(tier)) });
    }

    /// <summary>
    /// Case-insensitive display-name match over living characters, deterministic
    /// (lowest id wins). Never resolves to the decider or the requester.
    /// </summary>
    private string? ResolvePerson(string? fragment, string npcId, string playerId)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        var f = fragment.Trim().ToLowerInvariant();
        return _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != npcId && c.Id != playerId
                        && c.DisplayName.ToLowerInvariant().Contains(f))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Carries out an ACCEPTED order through the world. GoTo records a lure destination
    /// (the simulation picks it up next tick via the <c>CommandedDestinationFor</c>
    /// delegate, so the NPC visibly walks there on the 2D map); every other kind logs
    /// its execution as a world event — mechanical follow-through (inventory, theft
    /// resolution, combat) is Phase 15+ and documented as such.
    /// </summary>
    public void ExecuteOrder(
        string npcId, string playerId, DetectedOrder order, ComplianceDecision decision, long causedBy)
    {
        if (!decision.Complies)
            throw new ArgumentException("Only accepted orders can be executed.", nameof(decision));
        RequireLiving(npcId);

        var label = ComplianceRules.LabelOf(order);
        _events.Record(WorldEventTypes.OrderAccepted,
            participants: new[] { npcId, playerId },
            data: new Dictionary<string, string>
            {
                ["order"] = order.Kind.ToString(),
                ["label"] = label,
                ["score"] = decision.Score.ToString(),
            },
            causedBy: causedBy);

        if (order.Kind == OrderKind.GoTo && order.LocationId is not null
            && _content.HasLocation(order.LocationId))
        {
            var until = order.TimeMinute ?? _state.TotalMinutes + ManipulationRules.DefaultLureMinutes;
            _state.Manipulation.CommandedDestinations[npcId] = new CommandedDestination
            {
                LocationId = order.LocationId,
                UntilMinute = until,
                OrderedBy = playerId,
                OrderLabel = label,
            };
        }

        _events.Record(WorldEventTypes.OrderExecuted,
            locationId: _state.World.Characters[npcId].CurrentLocationId,
            participants: new[] { npcId, playerId },
            data: new Dictionary<string, string>
            {
                ["order"] = order.Kind.ToString(),
                ["label"] = label,
                ["target"] = order.TargetText ?? "",
            },
            causedBy: causedBy);
    }

    /// <summary>
    /// Applies a REFUSED order: logged, with a small deterministic fallout — the NPC's
    /// trust in the player dips (annoyance) and suspicion ticks up (why ask me that?).
    /// </summary>
    public void RefuseOrder(
        string npcId, string playerId, DetectedOrder order, ComplianceDecision decision, long causedBy)
    {
        if (decision.Complies)
            throw new ArgumentException("Only refused orders go through refusal.", nameof(decision));
        RequireLiving(npcId);

        _events.Record(WorldEventTypes.OrderRefused,
            participants: new[] { npcId, playerId },
            data: new Dictionary<string, string>
            {
                ["order"] = order.Kind.ToString(),
                ["label"] = ComplianceRules.LabelOf(order),
                ["score"] = decision.Score.ToString(),
                ["decisive"] = decision.Decisive is null
                    ? "none" : $"{decision.Decisive.Name} {decision.Decisive.Value:+0;-0}",
            },
            causedBy: causedBy);

        _social.Adjust(npcId, playerId,
            new SocialDelta(Trust: ManipulationRules.RefusalTrustDelta,
                Suspicion: ManipulationRules.RefusalSuspicionDelta),
            "refused an order", causedBy: causedBy);
    }

    /// <summary>
    /// The simulation's lure seam: returns the NPC's commanded destination while the
    /// order is still live, expiring (and removing) it afterwards. Null = no command.
    /// </summary>
    public string? CommandedDestinationFor(string npcId)
    {
        if (!_state.Manipulation.CommandedDestinations.TryGetValue(npcId, out var cmd))
            return null;
        if (_state.TotalMinutes >= cmd.UntilMinute)
        {
            _state.Manipulation.CommandedDestinations.Remove(npcId);
            return null;
        }
        var c = _state.World.Characters.GetValueOrDefault(npcId);
        if (c is null || !c.IsAlive)
        {
            _state.Manipulation.CommandedDestinations.Remove(npcId);
            return null;
        }
        return cmd.LocationId;
    }

    // ------------------------------------------------------------------ despair & suicide

    /// <summary>Current despair 0-100 (unlisted characters sit at 0).</summary>
    public int DespairOf(string characterId) =>
        _state.Manipulation.Despair.GetValueOrDefault(characterId);

    /// <summary>
    /// Moves a character's despair meter. Tier-scaled (the gullible sink twice as
    /// fast, the wary half as fast) and clamped 0-100. Reaching 100 triggers suicide
    /// — the one irreversible outcome of sustained psychological pressure.
    /// </summary>
    public void AdjustDespair(string characterId, int delta, long causedBy)
    {
        RequireLiving(characterId);
        if (delta == 0) return;
        var scaled = ManipulabilityRules.ScaleDespair(TierOf(characterId), delta);
        var before = DespairOf(characterId);
        var after = Math.Clamp(before + scaled, 0, 100);
        if (after == before) return;
        _state.Manipulation.Despair[characterId] = after;
        if (after >= 100)
            CommitSuicide(characterId, causedBy);
    }

    /// <summary>
    /// Despair hit 100: the character takes their own life. Recorded as
    /// <c>manipulation.suicide</c> (causally linked to the exchange that broke them),
    /// witnessed by everyone present through the Phase 4 gate, and reported to the
    /// police as a severity-3 disturbance so the alert ladder can react. The body is
    /// left where it fell; full death-scene investigation is Phase 15+.
    /// </summary>
    public WorldEvent CommitSuicide(string characterId, long causedBy)
    {
        var c = RequireLiving(characterId);
        var locationId = c.CurrentLocationId;

        // Record the suicide and report the disturbance while the victim is still
        // marked alive: ReportDisturbance requires a living subject, and the
        // disturbance IS the suicide. Death is marked last.
        var evt = _events.Record(WorldEventTypes.ManipulationSuicide,
            locationId: locationId,
            participants: new[] { characterId },
            data: new Dictionary<string, string> { ["despair"] = "100" },
            causedBy: causedBy);
        _police.ReportDisturbance(characterId, locationId, severity: 3, causedBy: evt.Id);

        _state.World.ActiveTravels.Remove(characterId);
        c.IsAlive = false;

        _cognition.Perceive(characterId, evt.Id, MemorySource.Witnessed, causedBy: evt.Id);
        foreach (var w in _state.World.Characters.Values
                     .Where(x => x.IsAlive && x.Id != characterId && x.CurrentLocationId == locationId)
                     .OrderBy(x => x.Id, StringComparer.Ordinal))
            _cognition.Perceive(w.Id, evt.Id, MemorySource.Witnessed, causedBy: evt.Id);

        return evt;
    }

    // ------------------------------------------------------------------ observable routines

    /// <summary>
    /// UI-driven (like <c>NpcInitiativeService.Evaluate</c>): the player learns the
    /// current schedule block of every living NPC sharing their location. Knowledge-
    /// gated by construction — the true schedule is never exposed, only blocks the
    /// player actually observed. Idempotent per (npc, block).
    /// </summary>
    public void ObserveRoutines()
    {
        var playerId = _state.Player.CharacterId;
        if (string.IsNullOrEmpty(playerId) || !_state.World.Characters.TryGetValue(playerId, out var p))
            return;
        var now = new GameTime(_state.TotalMinutes);
        foreach (var npc in _state.World.Characters.Values
                     .Where(c => c.IsAlive && c.Id != playerId && c.CurrentLocationId == p.CurrentLocationId)
                     .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (!_state.World.Schedules.TryGetValue(npc.Id, out var schedule))
                continue;
            var block = _schedules.BlockAt(npc.Id, now);
            var learned = _state.Manipulation.ObservedSchedules.GetValueOrDefault(npc.Id);
            if (learned is null)
            {
                learned = new List<ObservedBlock>();
                _state.Manipulation.ObservedSchedules[npc.Id] = learned;
            }
            if (learned.Any(b => b.StartMinute == block.StartMinute && b.LocationId == block.LocationId))
                continue;
            learned.Add(new ObservedBlock
            {
                StartMinute = block.StartMinute,
                EndMinute = block.EndMinute,
                LocationId = block.LocationId,
                Source = "seen",
            });
        }
    }

    /// <summary>
    /// The player's learned schedule for an NPC: only observed blocks, ordered by
    /// start minute. Anything unobserved is simply absent (the UI renders "???").
    /// </summary>
    public IReadOnlyList<ObservedBlock> GetLearnedSchedule(string npcId) =>
        _state.Manipulation.ObservedSchedules.GetValueOrDefault(npcId)
            ?.OrderBy(b => b.StartMinute).ToList()
        ?? (IReadOnlyList<ObservedBlock>)Array.Empty<ObservedBlock>();

    // ------------------------------------------------------------------ helpers

    private CharacterState RequireLiving(string id)
    {
        if (!_state.World.Characters.TryGetValue(id, out var c))
            throw new ArgumentException($"Unknown character '{id}'.");
        if (!c.IsAlive)
            throw new InvalidOperationException($"'{id}' is dead.");
        return c;
    }
}

/// <summary>Phase 14 tuning constants for the manipulation layer. Integers only.</summary>
public static class ManipulationRules
{
    /// <summary>Default lure duration when the order names no time: 120 minutes.</summary>
    public const int DefaultLureMinutes = 120;
    /// <summary>Trust fallout when an NPC refuses an order.</summary>
    public const int RefusalTrustDelta = -2;
    /// <summary>Suspicion fallout when an NPC refuses an order.</summary>
    public const int RefusalSuspicionDelta = 2;
}
