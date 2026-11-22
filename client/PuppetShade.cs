using System;
using System.Collections.Generic;
using CultMath;

namespace BadDay.Client;

/// <summary>
/// The bevel a piece draws, derived once from each triangle's source and never again (the shader's bevel assumes a triangle is a facet; a fragment of the boolean is not one). A source is a triangle of the
/// decimated body, or a triangle of a cutter facing the other way (a socket wall); a ball and an uncut body are their own sources. Every fragment of a source draws exactly what the source would draw at the same
/// points: its normal, its three edges as the lines the bevel bands (a distance to a line is affine on the source's plane, so each corner of a fragment carries it exactly), the creases across them, its least altitude as
/// the cap, and its corners' vertex normals interpolated. Two fragments of one source agree on every edge between them, however the boolean cut it, and a rim, where a fragment of the body meets one of a wall, is a hard edge
/// by construction: neither side has a line there.
/// Per triangle: <see cref="Normal"/>, <see cref="Crease"/> (component j for line j, the source's edge opposite its corner j) and <see cref="Cap"/>; per corner slot (3t + e, the triangle's corner e):
/// <see cref="Distance"/> (component j the distance, positive inside the source, to line j) and <see cref="VertexNormal"/>. All in the piece's frame, outward.
/// </summary>
public sealed record PuppetShade(float3[] Normal, float3[] Crease, float[] Cap, float3[] Distance, float3[] VertexNormal)
{
    /// <summary>A source triangle, counter-clockwise from outside.</summary>
    private sealed record Source(float3[] P, float3 N, float3 Crease, float3[] VN, float Cap);

    /// <summary>A mesh (counter-clockwise from outside) as its own source: every triangle draws its own bevel.</summary>
    public static PuppetShade Of(float3[] v, int[] tris)
    {
        var sources = Sources(v, tris, false);
        var tag = new int[tris.Length / 3];
        for (int k = 0; k < tag.Length; k++) tag[k] = k;
        return Derive(sources, tag, v, tris);
    }

    /// <summary>A cut piece's fragments, each shaded as the triangle its tag names: the decimated piece's triangles, then each cutter's, reversed (the wall faces the other way), in the order <see cref="PuppetBoolean.Subtract"/> appends them.</summary>
    public static PuppetShade Of(PieceMesh m)
    {
        var sources = new List<Source>(Sources(m.SourceV, m.SourceTris, false));
        foreach (var c in m.Cutters)
        {
            var reversed = (int[])c.Tris.Clone();
            for (int k = 0; k < reversed.Length; k += 3) (reversed[k + 1], reversed[k + 2]) = (reversed[k + 2], reversed[k + 1]);
            sources.AddRange(Sources(c.V, reversed, true));
        }
        return Derive(sources, m.Solid.Tag, m.Solid.V, m.Solid.Tris);
    }

    private static PuppetShade Derive(IReadOnlyList<Source> sources, int[] tag, float3[] v, int[] tris)
    {
        int n = tris.Length / 3;
        var normal = new float3[n]; var crease = new float3[n]; var cap = new float[n]; var distance = new float3[3 * n]; var vertexNormal = new float3[3 * n];
        for (int k = 0; k < n; k++)
        {
            var s = sources[tag[k]];
            normal[k] = s.N; crease[k] = s.Crease; cap[k] = s.Cap;
            for (int e = 0; e < 3; e++)
            {
                var c = v[tris[3 * k + e]];
                distance[3 * k + e] = new float3(Line(s, 0, c), Line(s, 1, c), Line(s, 2, c));
                vertexNormal[3 * k + e] = Smooth(s, c);
            }
        }
        return new PuppetShade(normal, crease, cap, distance, vertexNormal);
    }

    /// <summary>The distance of <paramref name="c"/> from the line of the source's edge opposite corner <paramref name="j"/>, along the plane, positive toward the inside.</summary>
    private static float Line(Source s, int j, float3 c)
    {
        var p = s.P[(j + 1) % 3]; var q = s.P[(j + 2) % 3];
        double ex = (double)q.x - p.x, ey = (double)q.y - p.y, ez = (double)q.z - p.z;
        double ix = s.N.y * ez - s.N.z * ey, iy = s.N.z * ex - s.N.x * ez, iz = s.N.x * ey - s.N.y * ex; // the inward direction: N x edge
        double l = Math.Sqrt(ix * ix + iy * iy + iz * iz);
        return (float)(((double)c.x - p.x) * ix / l + ((double)c.y - p.y) * iy / l + ((double)c.z - p.z) * iz / l);
    }

    /// <summary>The source's corner vertex normals at <paramref name="c"/>, a point of its plane: barycentric, extended past the triangle when a fragment's corner lies beyond it.</summary>
    private static float3 Smooth(Source s, float3 c)
    {
        double Area(float3 x, float3 y, float3 z)
        {
            double ux = (double)y.x - x.x, uy = (double)y.y - x.y, uz = (double)y.z - x.z, wx = (double)z.x - x.x, wy = (double)z.y - x.y, wz = (double)z.z - x.z;
            return s.N.x * (uy * wz - uz * wy) + s.N.y * (uz * wx - ux * wz) + s.N.z * (ux * wy - uy * wx);
        }
        double all = Area(s.P[0], s.P[1], s.P[2]);
        double b0 = Area(c, s.P[1], s.P[2]) / all, b1 = Area(s.P[0], c, s.P[2]) / all, b2 = Area(s.P[0], s.P[1], c) / all;
        double x = s.VN[0].x * b0 + s.VN[1].x * b1 + s.VN[2].x * b2, y = s.VN[0].y * b0 + s.VN[1].y * b1 + s.VN[2].y * b2, z = s.VN[0].z * b0 + s.VN[1].z * b1 + s.VN[2].z * b2;
        double l = Math.Sqrt(x * x + y * y + z * z);
        return l > 1e-12 ? new float3((float)(x / l), (float)(y / l), (float)(z / l)) : s.N;
    }

    /// <summary>
    /// The sources of a mesh: per triangle its normal, its creases and its cap, and its corners' vertex normals (the unit sum of the triangles at the vertex, weighted by area, or by the angle at the vertex where
    /// <paramref name="byAngle"/>: a cutter's faces are of every size). A crease is half the turn from this triangle's normal to its neighbour's across the edge, positive where the edge is convex (the neighbour's far corner
    /// lies behind this triangle's plane), the same on both sides; an edge without exactly one neighbour has 0, and so has a fold (the two sides disagree on which way it turns).
    /// </summary>
    private static Source[] Sources(float3[] v, int[] t, bool byAngle)
    {
        int n = t.Length / 3;
        var normal = new float3[n]; var sum = new float3[v.Length];
        for (int k = 0; k < n; k++)
        {
            var cross = math.cross(v[t[3 * k + 1]] - v[t[3 * k]], v[t[3 * k + 2]] - v[t[3 * k]]);
            normal[k] = Unit(cross);
            for (int e = 0; e < 3; e++)
            {
                var p = v[t[3 * k + e]];
                float weight = byAngle ? MathF.Acos(Math.Clamp(math.dot(Unit(v[t[3 * k + (e + 1) % 3]] - p), Unit(v[t[3 * k + (e + 2) % 3]] - p)), -1f, 1f)) : math.length(cross);
                sum[t[3 * k + e]] += normal[k] * weight;
            }
        }
        var crease = new float[3 * n];
        var sides = new Dictionary<(int, int), List<int>>();
        for (int s = 0; s < 3 * n; s++)
        {
            int p = t[3 * (s / 3) + (s % 3 + 1) % 3], q = t[3 * (s / 3) + (s % 3 + 2) % 3];
            var key = p < q ? (p, q) : (q, p);
            if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<int>(2);
            list.Add(s);
        }
        foreach (var list in sides.Values)
        {
            if (list.Count != 2) continue;
            int a = list[0], b = list[1];
            var na = normal[a / 3]; var nb = normal[b / 3];
            bool convexA = math.dot(na, v[t[b]] - v[t[3 * (a / 3) + (a % 3 + 1) % 3]]) < 0, convexB = math.dot(nb, v[t[a]] - v[t[3 * (b / 3) + (b % 3 + 1) % 3]]) < 0;
            if (convexA != convexB) continue;
            float half = 0.5f * MathF.Atan2(math.length(math.cross(na, nb)), math.dot(na, nb));
            crease[a] = crease[b] = convexA ? half : -half;
        }
        var sources = new Source[n];
        for (int k = 0; k < n; k++)
        {
            var p = new[] { v[t[3 * k]], v[t[3 * k + 1]], v[t[3 * k + 2]] };
            float twiceArea = math.length(math.cross(p[1] - p[0], p[2] - p[0])), longest = 0;
            for (int e = 0; e < 3; e++) longest = MathF.Max(longest, math.length(p[(e + 1) % 3] - p[e]));
            sources[k] = new Source(p, normal[k], new float3(crease[3 * k], crease[3 * k + 1], crease[3 * k + 2]), new[] { Unit(sum[t[3 * k]]), Unit(sum[t[3 * k + 1]]), Unit(sum[t[3 * k + 2]]) }, twiceArea / longest);
        }
        return sources;
    }

    private static float3 Unit(float3 x)
    {
        float l = math.length(x);
        return l > 1e-24f ? x / l : new float3(0, 1, 0);
    }
}
