using System;
using System.Collections.Generic;
using CultMath;

namespace BadDay.Client.Tests;

/// <summary>
/// Looking into a piece (ruling doll-pieces-hug-balls: the hand hugs its ball; a cup is a shell and not a window). Seen from outside, the first surface a ray meets must face the ray: a ball and its
/// two neighbouring pieces are shot from <see cref="Directions"/> orthographic directions over the whole sphere, one ray per <see cref="Cell"/> of a grid that covers the ball's radius + <see cref="Reach"/>,
/// and a ray whose first hit is the BACK of a triangle (the ray travels along the triangle's outward normal) came through an opening in the skin, or under a skin that sank below a wall behind it. The reading of a joint is
/// the mean over the directions of that area, in mm2. The silhouette (<see cref="Slits"/>) and the topological checks cannot see it: the opening has a surface behind it.
/// </summary>
public static class SeeInside
{
    /// <summary>The directions: Fibonacci over the whole sphere (a back face seen from d is not seen from -d).</summary>
    public const int Directions = 48;

    /// <summary>The ray grid (m): one ray per square millimetre.</summary>
    public const float Cell = 0.001f;

    /// <summary>How far (m) beyond the ball's radius the grid reaches: the cup's skin lies within 9 mm of it at the wrist.</summary>
    public const float Reach = 0.012f;

    public static System.Collections.Concurrent.ConcurrentBag<(float3 P, int Tri)> Debug = new();
    public static bool Dump;
    private static readonly float3[] Dirs = MakeDirs();

    private static float3[] MakeDirs()
    {
        var d = new float3[Directions];
        for (int i = 0; i < Directions; i++) { float z = 1 - 2 * (i + 0.5f) / Directions, r = MathF.Sqrt(1 - z * z), ph = i * 2.399963f; d[i] = new float3(r * MathF.Cos(ph), r * MathF.Sin(ph), z); }
        return d;
    }

    /// <summary>The mean over the directions, and the largest single direction, of the area (mm2) whose first hit is a back face, at a ball at <paramref name="origin"/> with radius <paramref name="radius"/>, of the triangles <paramref name="tris"/> (the ball's and its two neighbours', wound clockwise from outside).</summary>
    public static (float Mean, float Max) Per(IReadOnlyList<(float3 A, float3 B, float3 C)> tris, float3 origin, float radius)
    {
        float reach = radius + Reach, far = 0.5f;
        int n = (int)MathF.Ceiling(reach / Cell);
        var tri = new (float3 A, float3 E1, float3 E2, float3 N)[tris.Count];
        for (int i = 0; i < tri.Length; i++)
        {
            var (a, b, c) = tris[i]; var e1 = b - a; var e2 = c - a;
            tri[i] = (a, e1, e2, -math.cross(e1, e2));
        }
        float total = 0, worst = 0;
        foreach (var d in Dirs)
        {
            var r = math.normalize(math.cross(d, MathF.Abs(d.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0))); var u = math.cross(d, r);
            int back = 0;
            for (int iy = -n; iy < n; iy++)
                for (int ix = -n; ix < n; ix++)
                {
                    float x = (ix + 0.5f) * Cell, y = (iy + 0.5f) * Cell;
                    if (x * x + y * y > reach * reach) continue;
                    var o = origin - d * far + r * x + u * y;
                    float best = float.PositiveInfinity; bool isBack = false; int bi = -1;
                    for (int ti = 0; ti < tri.Length; ti++)
                    {
                        var t = tri[ti];
                        var pv = math.cross(d, t.E2); float det = math.dot(t.E1, pv);
                        if (MathF.Abs(det) < 1e-12f) continue;
                        var tv = o - t.A; float s = math.dot(tv, pv) / det;
                        if (s < 0 || s > 1) continue;
                        var qv = math.cross(tv, t.E1); float w = math.dot(d, qv) / det;
                        if (w < 0 || s + w > 1) continue;
                        float dist = math.dot(t.E2, qv) / det;
                        if (dist > 0 && dist < best) { best = dist; isBack = math.dot(t.N, d) > 0; bi = ti; }
                    }
                    if (isBack) { back++; if (Dump) Debug.Add((o + d * best, bi)); }
                }
            float mm2 = back * (Cell * 1000f) * (Cell * 1000f);
            total += mm2; worst = MathF.Max(worst, mm2);
        }
        return (total / Directions, worst);
    }
}
