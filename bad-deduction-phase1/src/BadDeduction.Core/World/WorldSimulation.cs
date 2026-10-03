using BadDeduction.Characters;
using BadDeduction.Cognition;
using BadDeduction.Content;
using BadDeduction.Core;
using BadDeduction.Police;

namespace BadDeduction.World;

/// <summary>
/// Which fidelity a character is simulated at. Tiers differ only in how often routine
/// *decisions* are evaluated; travel itself always resolves minute-exact for every tier
/// (it is a countdown, not a decision), so arrivals never smear.
/// </summary>
public enum SimulationTier
{
    /// <summary>Shares the player's location: evaluated every sim-minute (full routine logic).</summary>
    Spotlight,
    /// <summary>Everyone else with a routine: evaluated every 15 sim-minutes (decisions lag up to 14 min).</summary>
    Near,
    /// <summary>Reserved for off-slice populations (Phase 6+): evaluated once per day. Unassigned at
    /// the current 22-character scale; the seam exists so the assignment rule has somewhere to go.</summary>
    Background,
}

public static class SimulationTiers
{
    public static int CadenceMinutes(SimulationTier tier) => tier switch
    {
        SimulationTier.Spotlight => 1,
        SimulationTier.Near => 15,
        SimulationTier.Background => 1440,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };
}

/// <summary>
/// Fallback daily rhythm for characters without an authored schedule (hand-built test worlds).
/// Home-centric on purpose: it references only the home location, so it works with any content,
/// and it never needs travel. Generated casts always have real schedules (Phase 2).
/// </summary>
public static class DefaultRoutine
{
    public static Schedule For(string homeLocationId)
    {
        var blocks = new List<ScheduleBlock>();
        void Add(int from, int to, Activity activity) => blocks.Add(new ScheduleBlock
        {
            StartMinute = from, EndMinute = to, LocationId = homeLocationId, Activity = activity,
        });
        Add(0, 360, Activity.Sleeping);
        Add(360, 720, Activity.Idle);
        Add(720, 780, Activity.Eating);
        Add(780, 1140, Activity.Idle);
        Add(1140, 1320, Activity.Socializing);
        Add(1320, GameTime.MinutesPerDay, Activity.Sleeping);
        return new Schedule { Workday = blocks, DayOffIndex = -1 };
    }
}

/// <summary>
/// Phase 5: the world tick. Turns schedules into lived routines — sleep, work, meals, evenings —
/// with travel that takes real minutes along the location graph's timed edges.
/// <para/>
/// Driven explicitly via <see cref="Advance"/>, which wraps <see cref="TimeSystem.Advance"/>
/// one minute at a time and then runs the simulation for that minute. <see cref="TimeSystem"/>
/// itself stays sim-free on purpose: existing systems and tests advance raw time without
/// characters moving under them, and the game loop (Phase 11) calls <c>session.Simulate.Advance</c>.
/// <para/>
/// Rules: the player character is never simulated (a human drives them); the dead do not move;
/// all movement goes through <see cref="WorldService.MoveCharacter"/>; knowledge of arrivals and
/// departures flows through <see cref="CognitionService.Perceive"/> for everyone present
/// (the mover included; the dead perceive nothing). Nothing here reads hidden roles.
/// </summary>
public sealed class WorldSimulation
{
    private readonly GameState _state;
    private readonly TimeSystem _time;
    private readonly EventSystem _events;
    private readonly WorldService _world;
    private readonly CognitionService _cognition;
    private readonly ContentDatabase _content;

    public WorldSimulation(
        GameState state,
        TimeSystem time,
        EventSystem events,
        WorldService world,
        CognitionService cognition,
        ContentDatabase content)
    {
        _state = state;
        _time = time;
        _events = events;
        _world = world;
        _cognition = cognition;
        _content = content;
    }

    /// <summary>
    /// Phase 9 cordon gate, wired to <see cref="PoliceService.CanEnter"/> by GameSession.
    /// Null means everyone may travel (pre-cordon behavior).
    /// </summary>
    public Func<string, string, bool>? CanEnter { get; set; }

    /// <summary>
    /// Phase 9 duty seam, wired to <see cref="PoliceService.DutyLocationFor"/> by GameSession.
    /// When an officer has a duty for the day, their work blocks run at the duty location.
    /// </summary>
    public Func<string, int, string?>? DutyLocationFor { get; set; }

    /// <summary>
    /// Phase 14 lure seam, wired to <see cref="Manipulation.ManipulationService.CommandedDestinationFor"/>
    /// by GameSession. A commanded NPC travels toward (and waits at) the ordered location
    /// instead of following their routine, until the order expires. Null = no command.
    /// </summary>
    public Func<string, string?>? CommandedDestinationFor { get; set; }

    /// <summary>
    /// Phase 14 strategic-movement seam, wired to
    /// <see cref="Agenda.HiddenAgendaService.StrategicDestinationFor"/> by GameSession.
    /// Lets the NPC-held genius visibly work their objectives on the map (Malvr lurking
    /// near their target at night, Lumiel working a crime scene) through the same
    /// travel machinery as everyone else. Director-level staging only — it moves the
    /// piece, it never grants the holder knowledge they don't have.
    /// </summary>
    public Func<string, string?>? StrategicDestinationFor { get; set; }

    /// <summary>Advances time and simulates the world minute by minute. Deterministic per seed.</summary>
    public void Advance(int minutes)
    {
        if (minutes < 0) throw new ArgumentOutOfRangeException(nameof(minutes), "Time only moves forward.");
        for (var i = 0; i < minutes; i++)
        {
            _time.Advance(1);
            TickMinute();
        }
    }

    /// <summary>
    /// The tier a character is currently simulated at: Spotlight when they share the player's
    /// location (and a player exists), Near otherwise. Background is reserved (see enum docs).
    /// A pure function of state, so tier membership survives save/load by construction.
    /// </summary>
    public SimulationTier TierOf(string characterId)
    {
        if (!_state.World.Characters.TryGetValue(characterId, out var c))
            throw new KeyNotFoundException($"Unknown character '{characterId}'.");
        var playerId = _state.Player.CharacterId;
        if (!string.IsNullOrEmpty(playerId) && characterId != playerId
            && _state.World.Characters.TryGetValue(playerId, out var p) && p.IsAlive
            && c.CurrentLocationId == p.CurrentLocationId)
            return SimulationTier.Spotlight;
        return SimulationTier.Near;
    }

    // ------------------------------------------------------------------ per-minute tick

    private void TickMinute()
    {
        var now = _state.TotalMinutes;
        CompleteArrivals(now);
        foreach (var id in _state.World.Characters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var c = _state.World.Characters[id];
            if (!c.IsAlive) continue;
            if (!string.IsNullOrEmpty(_state.Player.CharacterId) && id == _state.Player.CharacterId) continue;
            if (IsInCustody(id)) continue; // detained characters do not move (Phase 9)
            if (now % SimulationTiers.CadenceMinutes(TierOf(id)) != 0) continue;
            EvaluateRoutine(id, now);
        }
    }

    /// <summary>Phase 9: characters in custody are detained where they are.</summary>
    private bool IsInCustody(string characterId) =>
        _state.Police.Cases.Values.Any(c => c.State == CaseState.InCustody && c.SubjectId == characterId);

    /// <summary>
    /// Finishes every trip whose arrival minute has come, for all tiers alike. Runs before routine
    /// evaluation so a character who arrives this minute is seen at their destination immediately.
    /// </summary>
    private void CompleteArrivals(long now)
    {
        if (_state.World.ActiveTravels.Count == 0) return;
        foreach (var id in _state.World.ActiveTravels.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            if (!_state.World.ActiveTravels.TryGetValue(id, out var travel)) continue;
            if (now < travel.ArrivalMinute) continue;
            var c = _state.World.Characters[id];
            if (!c.IsAlive)
            {
                // Death mid-travel cannot happen yet (no death system before Phase 7);
                // cancel the trip rather than throwing on every tick forever.
                _state.World.ActiveTravels.Remove(id);
                continue;
            }
            var moved = _world.MoveCharacter(id, travel.ToLocationId, causedBy: travel.DepartureEventId);
            _state.World.ActiveTravels.Remove(id);
            if (moved is null)
            {
                // The destination became off-limits mid-travel (e.g. a cordon went up):
                // cancel the trip; the character stays where they are. The denial itself
                // is already logged by WorldService.
                SetActivity(c, Activity.Idle, "arrival denied");
                continue;
            }
            var arrival = new GameTime(travel.ArrivalMinute);
            var (block, _) = BlockAt(BlocksFor(id, arrival.Day), arrival.MinuteOfDay);
            SetActivity(c, block.Activity, $"arrived at {travel.ToLocationId}");
            // The traveler and everyone waiting at the destination witness the arrival.
            // (The origin already witnessed the departure when the trip started.)
            _cognition.Perceive(id, moved.Id, MemorySource.Witnessed, causedBy: moved.Id);
            foreach (var w in LivingIdsAt(travel.ToLocationId, id))
                _cognition.Perceive(w, moved.Id, MemorySource.Witnessed, causedBy: moved.Id);
        }
    }

    // ------------------------------------------------------------------ routine decisions

    private void EvaluateRoutine(string id, long now)
    {
        var c = _state.World.Characters[id];
        if (_state.World.ActiveTravels.ContainsKey(id)) return; // en route: arrivals resolve per-minute

        // Phase 14: directed movement wins over routine — a player's lure first, then
        // the hidden genius's strategic staging. Both travel through the same machinery
        // (witnessed departures/arrivals, cordon checks) as routine movement.
        var directed = CommandedDestinationFor?.Invoke(id) ?? StrategicDestinationFor?.Invoke(id);
        if (directed is not null)
        {
            if (c.CurrentLocationId != directed)
            {
                StartTravel(c, directed, now);
                return;
            }
            SetActivity(c, Activity.Idle, "waiting as directed");
            return;
        }

        var time = new GameTime(now);
        var blocks = BlocksFor(id, time.Day);
        var (block, index) = BlockAt(blocks, time.MinuteOfDay);

        if (c.CurrentLocationId != block.LocationId)
        {
            // Not where the routine says to be (late, displaced, or hand-placed): go there now.
            StartTravel(c, block.LocationId, now);
            return;
        }

        var (nextLoc, nextStartAbs) = NextBlockStart(id, blocks, index, time.Day);
        if (nextLoc != c.CurrentLocationId)
        {
            // ADR-008: Start is the arrival deadline, so leave travel-minutes early.
            var travelMinutes = _content.TravelMinutes(c.CurrentLocationId, nextLoc);
            if (now >= nextStartAbs - travelMinutes)
            {
                StartTravel(c, nextLoc, now);
                return;
            }
        }
        SetActivity(c, block.Activity, $"routine: {block.Activity}");
    }

    private void StartTravel(CharacterState c, string toLocationId, long now)
    {
        if (CanEnter is not null && !CanEnter(c.Id, toLocationId))
        {
            // Cordoned (Phase 9): don't start the trip, don't spam the log — just wait.
            // The denial is recorded when someone actually attempts the move by hand.
            SetActivity(c, Activity.Idle, "waiting: destination cordoned");
            return;
        }
        var minutes = _content.TravelMinutes(c.CurrentLocationId, toLocationId);
        var departed = _events.Record(WorldEventTypes.CharacterDeparted,
            locationId: c.CurrentLocationId,
            participants: new[] { c.Id },
            data: new Dictionary<string, string>
            {
                ["from"] = c.CurrentLocationId,
                ["to"] = toLocationId,
                ["departure"] = now.ToString(),
                ["arrival"] = (now + minutes).ToString(),
            });
        // Everyone at the origin — the traveler included — witnesses the departure right away.
        _cognition.Perceive(c.Id, departed.Id, MemorySource.Witnessed, causedBy: departed.Id);
        foreach (var w in LivingIdsAt(c.CurrentLocationId, c.Id))
            _cognition.Perceive(w, departed.Id, MemorySource.Witnessed, causedBy: departed.Id);
        _state.World.ActiveTravels[c.Id] = new TravelState
        {
            CharacterId = c.Id,
            FromLocationId = c.CurrentLocationId,
            ToLocationId = toLocationId,
            DepartureMinute = now,
            ArrivalMinute = now + minutes,
            DepartureEventId = departed.Id,
        };
        SetActivity(c, Activity.Traveling, $"departing for {toLocationId}");
    }

    /// <summary>Logs only actual transitions; silent when nothing changes (event-volume policy).</summary>
    private WorldEvent? SetActivity(CharacterState c, Activity activity, string reason)
    {
        if (c.Activity == activity) return null;
        var before = c.Activity;
        c.Activity = activity;
        return _events.Record(WorldEventTypes.ActivityChanged,
            participants: new[] { c.Id },
            data: new Dictionary<string, string>
            {
                ["from"] = before.ToString(),
                ["to"] = activity.ToString(),
                ["reason"] = reason,
            });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Schedule blocks for a character on a day. Police officers with an active duty roster
    /// work their duty location instead of their station (Phase 9 seam).
    /// </summary>
    private IReadOnlyList<ScheduleBlock> BlocksFor(string characterId, int day)
    {
        var blocks = _state.World.Schedules.TryGetValue(characterId, out var schedule)
            ? schedule.ForDay(day)
            : DefaultRoutine.For(_state.World.Characters[characterId].HomeLocationId).ForDay(day);
        var dutyLoc = _state.World.Characters.TryGetValue(characterId, out var c)
            && c.Kind == CharacterKind.Police
            ? DutyLocationFor?.Invoke(characterId, day)
            : null;
        if (dutyLoc is null) return blocks;
        return blocks.Select(b => b.Activity == Activity.Working
            ? new ScheduleBlock
            {
                StartMinute = b.StartMinute, EndMinute = b.EndMinute,
                LocationId = dutyLoc, Activity = b.Activity,
            }
            : b).ToList();
    }

    private static (ScheduleBlock Block, int Index) BlockAt(IReadOnlyList<ScheduleBlock> blocks, int minuteOfDay)
    {
        for (var i = 0; i < blocks.Count; i++)
            if (minuteOfDay >= blocks[i].StartMinute && minuteOfDay < blocks[i].EndMinute)
                return (blocks[i], i);
        throw new InvalidOperationException($"No schedule block covers minute {minuteOfDay}.");
    }

    private (string LocationId, long StartAbsMinute) NextBlockStart(
        string characterId, IReadOnlyList<ScheduleBlock> blocks, int index, int day)
    {
        if (index + 1 < blocks.Count)
        {
            var nb = blocks[index + 1];
            return (nb.LocationId, (long)(day - 1) * GameTime.MinutesPerDay + nb.StartMinute);
        }
        // The day loops (ADR-008): after the last block comes tomorrow's first.
        var first = BlocksFor(characterId, day + 1)[0];
        return (first.LocationId, (long)day * GameTime.MinutesPerDay + first.StartMinute);
    }

    private List<string> LivingIdsAt(string locationId, string excludeId) =>
        _state.World.Characters.Values
            .Where(c => c.IsAlive && c.Id != excludeId && c.CurrentLocationId == locationId)
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
}
