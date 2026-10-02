namespace BadDeduction.AI;

/// <summary>
/// FNV-1a 64-bit over a seed and string parts. Pure and platform-stable: the mock provider
/// and the deterministic fallback pool use it, so neither disturbs any RNG stream (ADR-002)
/// and results never shift between runs or after save/load.
/// </summary>
internal static class StableHash
{
    public static ulong Compute(ulong seed, params string?[] parts)
    {
        const ulong offsetBasis = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;
        var h = offsetBasis;
        for (var i = 0; i < 8; i++)
        {
            h ^= (byte)(seed >> (i * 8));
            h *= prime;
        }
        foreach (var part in parts)
        {
            foreach (var ch in part ?? "")
            {
                h ^= (byte)(ch & 0xFF);
                h *= prime;
                h ^= (byte)((ch >> 8) & 0xFF);
                h *= prime;
            }
            h ^= 0xFF; // part separator
            h *= prime;
        }
        return h;
    }
}
