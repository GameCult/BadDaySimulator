namespace BadDay.Core;

/// <summary>
/// SplitMix64: the only source of randomness in a run. Pure integer arithmetic,
/// so a seed gives the same stream on every platform and runtime version.
/// </summary>
public sealed class SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1) with 53 bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Uniform in [-1, 1).</summary>
    public double NextSigned() => NextDouble() * 2.0 - 1.0;
}
