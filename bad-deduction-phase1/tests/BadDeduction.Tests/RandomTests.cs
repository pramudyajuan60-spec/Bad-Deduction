using BadDeduction.Core;
using BadDeduction.Tests.Harness;

namespace BadDeduction.Tests;

public sealed class RandomTests
{
    [Fact]
    public void Same_seed_produces_same_sequence()
    {
        var a = new DeterministicRandom(123);
        var b = new DeterministicRandom(123);
        for (var i = 0; i < 100; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());
    }

    [Fact]
    public void Different_seeds_produce_different_sequences()
    {
        var a = new DeterministicRandom(1);
        var b = new DeterministicRandom(2);
        Assert.NotEqual(a.NextUInt64(), b.NextUInt64());
    }

    [Fact]
    public void Sequence_matches_independent_reference_values()
    {
        // Golden values produced by a separate Python implementation of SplitMix64 + xoshiro256**.
        // If these ever change, every existing save and replay silently diverges.
        var r = new DeterministicRandom(42);
        Assert.Equal(1546998764402558742UL, r.NextUInt64());
        Assert.Equal(6990951692964543102UL, r.NextUInt64());
        Assert.Equal(12544586762248559009UL, r.NextUInt64());
        Assert.Equal(518670270849972523UL, DeterministicRandom.Derive(7, "worldgen").NextUInt64());
    }

    [Fact]
    public void Derived_streams_are_reproducible_and_independent()
    {
        var a1 = DeterministicRandom.Derive(7, "worldgen").NextUInt64();
        var a2 = DeterministicRandom.Derive(7, "worldgen").NextUInt64();
        var b = DeterministicRandom.Derive(7, "dialogue").NextUInt64();
        var c = DeterministicRandom.Derive(8, "worldgen").NextUInt64();
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.NotEqual(a1, c);
    }

    [Fact]
    public void NextInt_respects_bounds_and_covers_the_range()
    {
        var r = new DeterministicRandom(99);
        var seen = new HashSet<int>();
        for (var i = 0; i < 2000; i++)
        {
            var v = r.NextInt(3, 9);
            Assert.True(v >= 3 && v < 9, $"{v} out of [3,9)");
            seen.Add(v);
        }
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void NextDouble_is_in_unit_interval()
    {
        var r = new DeterministicRandom(5);
        for (var i = 0; i < 1000; i++)
        {
            var d = r.NextDouble();
            Assert.True(d >= 0.0 && d < 1.0);
        }
    }

    [Fact]
    public void Snapshot_and_restore_continue_the_same_sequence()
    {
        var r = new DeterministicRandom(11);
        r.NextUInt64();
        var snapshot = r.Snapshot();
        var expected = new[] { r.NextUInt64(), r.NextUInt64() };

        var restored = new DeterministicRandom(snapshot);
        Assert.SequenceEqual(expected, new[] { restored.NextUInt64(), restored.NextUInt64() });
    }

    [Fact]
    public void Shuffle_is_deterministic_and_keeps_all_items()
    {
        var a = Enumerable.Range(0, 20).ToList();
        var b = Enumerable.Range(0, 20).ToList();
        new DeterministicRandom(3).Shuffle(a);
        new DeterministicRandom(3).Shuffle(b);
        Assert.SequenceEqual(a, b);
        Assert.SequenceEqual(Enumerable.Range(0, 20), a.OrderBy(x => x));
    }
}
