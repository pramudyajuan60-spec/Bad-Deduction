namespace BadDeduction.Core;

// ---- Transient bus messages (never saved; consumers rebuild from state) ----
public sealed record MinuteElapsed(GameTime Time);
public sealed record HourChanged(GameTime Time);
public sealed record TimeOfDayChanged(GameTime Time, TimeOfDay Previous, TimeOfDay Current);
public sealed record DayChanged(GameTime Time, int Day);
public sealed record WorldEventRecorded(WorldEvent Event);

/// <summary>
/// Two complementary facilities:
/// 1) a synchronous, deterministic pub/sub bus for transient in-process messages, and
/// 2) a persistent world-event log (<see cref="Record"/>) that forms the causal history of the run.
/// Dispatch is by exact runtime message type. Handlers run in subscription order.
/// Messages published from inside a handler are queued (FIFO) and delivered after the
/// current message has reached all of its handlers, which keeps ordering predictable.
/// </summary>
public sealed class EventSystem
{
    private sealed class Subscription : IDisposable
    {
        private readonly EventSystem _owner;
        public Type MessageType { get; }
        public Action<object> Handler { get; }
        public bool Active { get; private set; } = true;

        public Subscription(EventSystem owner, Type type, Action<object> handler)
        {
            _owner = owner;
            MessageType = type;
            Handler = handler;
        }

        public void Dispose()
        {
            if (!Active) return;
            Active = false;
            _owner.Remove(this);
        }
    }

    private readonly GameState _state;
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = new();
    private readonly Queue<object> _queue = new();
    private bool _dispatching;

    public EventSystem(GameState state) => _state = state;

    public IDisposable Subscribe<T>(Action<T> handler) where T : notnull
    {
        var sub = new Subscription(this, typeof(T), o => handler((T)o));
        if (!_subscriptions.TryGetValue(typeof(T), out var list))
            _subscriptions[typeof(T)] = list = new List<Subscription>();
        list.Add(sub);
        return sub;
    }

    private void Remove(Subscription sub)
    {
        if (_subscriptions.TryGetValue(sub.MessageType, out var list))
            list.Remove(sub);
    }

    public void Publish<T>(T message) where T : notnull
    {
        _queue.Enqueue(message);
        if (_dispatching) return;

        _dispatching = true;
        try
        {
            while (_queue.Count > 0)
            {
                var msg = _queue.Dequeue();
                if (!_subscriptions.TryGetValue(msg.GetType(), out var list)) continue;
                foreach (var sub in list.ToArray())
                    if (sub.Active) sub.Handler(msg);
            }
        }
        finally
        {
            _dispatching = false;
            _queue.Clear(); // if a handler threw, drop undelivered transient messages
        }
    }

    /// <summary>Appends an authoritative event to the persistent log and announces it on the bus.</summary>
    public WorldEvent Record(
        string type,
        string? locationId = null,
        IEnumerable<string>? participants = null,
        IReadOnlyDictionary<string, string>? data = null,
        long? causedBy = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Event type is required.", nameof(type));

        var log = _state.EventLog;
        if (causedBy is { } cause && !log.TryGet(cause, out _))
            throw new ArgumentException($"Unknown causing event id {cause}.", nameof(causedBy));

        var evt = new WorldEvent
        {
            Id = log.NextId++,
            Timestamp = _state.TotalMinutes,
            Type = type,
            LocationId = locationId,
            Participants = participants?.ToList() ?? new List<string>(),
            Data = data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data),
            CausedBy = causedBy,
        };
        log.Events.Add(evt);
        Publish(new WorldEventRecorded(evt));
        return evt;
    }

    /// <summary>Returns the causal chain ending at <paramref name="eventId"/>, ordered root-first.</summary>
    public IReadOnlyList<WorldEvent> CausalChain(long eventId)
    {
        var chain = new List<WorldEvent>();
        long? current = eventId;
        while (current is { } id)
        {
            if (!_state.EventLog.TryGet(id, out var evt))
                throw new ArgumentException($"Unknown event id {id}.", nameof(eventId));
            chain.Add(evt);
            current = evt.CausedBy;
        }
        chain.Reverse();
        return chain;
    }

    public IEnumerable<WorldEvent> Query(
        string? typePrefix = null,
        string? participantId = null,
        GameTime? from = null,
        GameTime? to = null)
    {
        foreach (var e in _state.EventLog.Events)
        {
            if (typePrefix is not null && !e.Type.StartsWith(typePrefix, StringComparison.Ordinal)) continue;
            if (participantId is not null && !e.Participants.Contains(participantId)) continue;
            if (from is { } f && e.Timestamp < f.TotalMinutes) continue;
            if (to is { } t && e.Timestamp > t.TotalMinutes) continue;
            yield return e;
        }
    }
}
