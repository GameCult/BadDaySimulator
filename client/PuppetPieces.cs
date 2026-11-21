using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CultMath;

namespace BadDay.Client;

/// <summary>What a piece's mesh is a pure function of (see <see cref="PieceField.Key"/>).</summary>
public readonly record struct PieceKey(string Id);

/// <summary>
/// The piece meshes, engine-free, by key: a piece's mesh (in its own frame) is built once per process on the thread pool and never edited, so the doll's
/// thirty-seven pieces cost its twenty-one distinct shapes, a crowd of forty spheres costs one, and a second build of the same doll costs none. No eviction:
/// the keys are authored shapes (<see cref="Doll"/>), not poses.
/// </summary>
public static class PuppetPieces
{
    private static readonly ConcurrentDictionary<PieceKey, Lazy<Task<(float3[] V, int[] Tris)>>> Cache = new();
    private static int _built;
    private static long _ticks;

    /// <summary>How many keys have been built: the checks' account that a second build costs none.</summary>
    public static int Built => Volatile.Read(ref _built);

    /// <summary>The time (ms) the builds took, summed over the thread pool: what the cache saves, for the report.</summary>
    public static double BuildMilliseconds => Volatile.Read(ref _ticks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The piece's mesh: started on the thread pool the first time its key is asked for, the same task after.</summary>
    public static Task<(float3[] V, int[] Tris)> Get(PieceKey key, PieceField field) =>
        Cache.GetOrAdd(key, _ => new Lazy<Task<(float3[] V, int[] Tris)>>(() => Task.Run(() =>
        {
            long t0 = Stopwatch.GetTimestamp();
            var mesh = PuppetField.Mesh(field);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref _built);
            return mesh;
        }))).Value;
}
