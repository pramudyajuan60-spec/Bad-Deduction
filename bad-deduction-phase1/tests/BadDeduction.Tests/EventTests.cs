using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed record Ping(string Name);
public sealed record Pong(string Name);

public sealed class EventTests
{
    [Fact]
    public void Handlers_run_in_subscription_order_and_unsubscribe_works()
    {
        var s = TestSupport.NewPopulatedSession();
        var log = new List<string>();
        s.Events.Subscribe<Ping>(p => log.Add("A:" + p.Name));
        var second = s.Events.Subscribe<Ping>(p => log.Add("B:" + p.Name));
        s.Events.Subscribe<Ping>(p => log.Add("C:" + p.Name));

        s.Events.Publish(new Ping("1"));
        second.Dispose();
        s.Events.Publish(new Ping("2"));

        Assert.SequenceEqual(new[] { "A:1", "B:1", "C:1", "A:2", "C:2" }, log);
    }

    [Fact]
    public void Messages_published_inside_handlers_are_queued_fifo()
    {
        var s = TestSupport.NewPopulatedSession();
        var log = new List<string>();
        s.Events.Subscribe<Ping>(p => { log.Add("ping-1"); s.Events.Publish(new Pong(p.Name)); });
        s.Events.Subscribe<Ping>(p => log.Add("ping-2"));
        s.Events.Subscribe<Pong>(p => log.Add("pong"));

        s.Events.Publish(new Ping("x"));
        Assert.SequenceEqual(new[] { "ping-1", "ping-2", "pong" }, log);
    }

    [Fact]
    public void A_throwing_handler_does_not_wedge_the_bus()
    {
        var s = TestSupport.NewPopulatedSession();
        var count = 0;
        using (s.Events.Subscribe<Ping>(_ => throw new InvalidOperationException("boom")))
            Assert.Throws<InvalidOperationException>(() => s.Events.Publish(new Ping("a")));

        s.Events.Subscribe<Ping>(_ => count++);
        s.Events.Publish(new Ping("b"));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Recorded_events_get_sequential_ids_timestamps_and_notify_subscribers()
    {
        var s = TestSupport.NewPopulatedSession();
        var seen = new List<long>();
        s.Events.Subscribe<WorldEventRecorded>(e => seen.Add(e.Event.Id));

        var before = s.State.EventLog.Events.Count;
        var e1 = s.Events.Record("test.a", "loc_tavern", new[] { "c_merchant" });
        var e2 = s.Events.Record("test.b");
        Assert.Equal(before + 1, e1.Id);
        Assert.Equal(before + 2, e2.Id);
        Assert.Equal(s.State.TotalMinutes, e1.Timestamp);
        Assert.SequenceEqual(new[] { e1.Id, e2.Id }, seen);
    }

    [Fact]
    public void Causal_chain_walks_back_to_the_root_event()
    {
        var s = TestSupport.NewPopulatedSession();
        var root = s.Events.Record("test.root");
        var mid = s.Events.Record("test.mid", causedBy: root.Id);
        var leaf = s.Events.Record("test.leaf", causedBy: mid.Id);
        s.Events.Record("test.unrelated");

        Assert.SequenceEqual(new[] { root.Id, mid.Id, leaf.Id }, s.Events.CausalChain(leaf.Id).Select(e => e.Id));
    }

    [Fact]
    public void Recording_with_an_unknown_cause_is_rejected()
    {
        var s = TestSupport.NewPopulatedSession();
        Assert.Throws<ArgumentException>(() => s.Events.Record("test.bad", causedBy: 9999));
        Assert.Throws<ArgumentException>(() => s.Events.Record(" "));
    }

    [Fact]
    public void Query_filters_by_type_participant_and_time()
    {
        var s = TestSupport.NewPopulatedSession();
        s.World.MoveCharacter("c_merchant", "loc_central_market");
        s.Time.Advance(120);
        s.World.MoveCharacter("c_priest", "loc_tavern");

        Assert.Equal(2, s.Events.Query("character.").Count());
        Assert.Equal(1, s.Events.Query(participantId: "c_priest").Count());
        Assert.Equal(1, s.Events.Query("character.", from: GameTime.At(1, 8)).Count());
    }
}
