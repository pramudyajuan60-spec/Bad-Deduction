namespace BadDeduction.Core;

/// <summary>
/// Serializable state of a <see cref="DeterministicRandom"/> (xoshiro256** words).
/// A <see cref="DeterministicRandom"/> mutates this object in place, so storing it inside
/// <see cref="GameState"/> means the RNG can never drift out of sync with a save file.
/// </summary>
public sealed class RngState
{
    public ulong S0 { get; set; }
    public ulong S1 { get; set; }
    public ulong S2 { get; set; }
    public ulong S3 { get; set; }

    public RngState Clone() => new() { S0 = S0, S1 = S1, S2 = S2, S3 = S3 };

    public bool IsZero => (S0 | S1 | S2 | S3) == 0;
}

/// <summary>
/// Portable, fully specified PRNG (xoshiro256**, seeded via SplitMix64).
/// We deliberately do not use <see cref="System.Random"/>: its seeded sequence is not
/// guaranteed stable across runtime versions, which would break save/load and replay determinism.
/// </summary>
public sealed class DeterministicRandom
{
    private readonly RngState _s;

    public DeterministicRandom(ulong seed)
    {
        var sm = seed;
        _s = new RngState
        {
            S0 = SplitMix64(ref sm),
            S1 = SplitMix64(ref sm),
            S2 = SplitMix64(ref sm),
            S3 = SplitMix64(ref sm),
        };
    }

    /// <summary>Wraps (and mutates) an existing state object.</summary>
    public DeterministicRandom(RngState backing)
    {
        if (backing.IsZero)
            throw new ArgumentException("An all-zero xoshiro state is invalid.", nameof(backing));
        _s = backing;
    }

    /// <summary>
    /// Creates an independent, reproducible stream for a named purpose (e.g. "worldgen.npcs").
    /// Adding a new consumer never shifts the sequence seen by existing consumers.
    /// </summary>
    public static DeterministicRandom Derive(ulong seed, string streamName)
    {
        ulong h = 14695981039346656037UL; // FNV-1a 64
        foreach (var c in streamName)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        var sm = seed;
        return new DeterministicRandom(SplitMix64(ref sm) ^ h);
    }

    public RngState Snapshot() => _s.Clone();

    public ulong NextUInt64()
    {
        var result = RotL(_s.S1 * 5, 7) * 9;
        var t = _s.S1 << 17;
        _s.S2 ^= _s.S0;
        _s.S3 ^= _s.S1;
        _s.S1 ^= _s.S2;
        _s.S0 ^= _s.S3;
        _s.S2 ^= t;
        _s.S3 = RotL(_s.S3, 45);
        return result;
    }

    /// <summary>Unbiased integer in [minInclusive, maxExclusive).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "max must be greater than min.");
        var range = (ulong)((long)maxExclusive - minInclusive);
        var threshold = (0UL - range) % range;
        while (true)
        {
            var x = NextUInt64();
            if (x >= threshold)
                return (int)(minInclusive + (long)(x % range));
        }
    }

    /// <summary>Double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double probability) => NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.", nameof(items));
        return items[NextInt(0, items.Count)];
    }

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = NextInt(0, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    private static ulong SplitMix64(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
