using System;
using CultMath;

namespace BadDay.Client;

/// <summary>
/// The puppet's drawn surface, engine-free, owned for every frame: <see cref="Update"/> skins the welded vertices of a <see cref="PuppetShape"/>
/// by the bodies' transforms and derives from the skinned corners the facet normals, the creases (half the turn between neighbouring
/// facets, once per edge and the same on both sides), the area-weighted vertex normals and the altitudes that give the bevel its distances.
/// A crease is a property of the deformed surface, so none of it is kept from the bind. Every array is allocated once, here, and refilled.
/// </summary>
public sealed class PuppetSkin
{
    private readonly PuppetShape _shape;
    private readonly float[] _bones;

    public PuppetSkin(PuppetShape shape)
    {
        _shape = shape;
        _bones = new float[12 * shape.Ids.Length];
        int slots = shape.Corners.Length, triangles = slots / 3;
        Skinned = new float3[shape.Vertices.Length];
        VertexNormal = new float3[shape.Vertices.Length];
        Facet = new float3[triangles];
        Crease = new float[slots];
        Altitude = new float[slots];
        SmallestAltitude = new float[triangles];
    }

    /// <summary>Every vertex skinned, in the space the bodies' transforms take the spread bind to.</summary>
    public float3[] Skinned { get; }

    /// <summary>Per triangle: the unit normal seen from outside.</summary>
    public float3[] Facet { get; }

    /// <summary>
    /// Per corner slot (3t+k is edge k of triangle t, the edge opposite corner k): half the turn from this facet's normal to the
    /// neighbour's across that edge, in radians, positive where the edge is convex. Both sides of an edge carry the same value. An edge
    /// without exactly one neighbour has 0, and so has a fold (the two sides disagree on which way it turns).
    /// </summary>
    public float[] Crease { get; }

    /// <summary>Per vertex: the unit sum of its triangles' normals weighted by their areas.</summary>
    public float3[] VertexNormal { get; }

    /// <summary>Per corner slot: the distance (m) from corner k to the line of edge k, 2 * area / |edge k|.</summary>
    public float[] Altitude { get; }

    /// <summary>Per triangle: the smallest of its three altitudes.</summary>
    public float[] SmallestAltitude { get; }

    /// <summary>The corners of the box around the skinned vertices.</summary>
    public float3 Lo { get; private set; }
    public float3 Hi { get; private set; }

    /// <summary><paramref name="bones"/> take each body from the spread bind to where it is now, one per body of the shape.</summary>
    public void Update(ReadOnlySpan<Rigid> bones)
    {
        var s = _shape;
        for (int i = 0; i < bones.Length; i++)
        {
            var r = bones[i].R; var t = bones[i].T; int o = 12 * i;
            _bones[o] = r._m00; _bones[o + 1] = r._m01; _bones[o + 2] = r._m02;
            _bones[o + 3] = r._m10; _bones[o + 4] = r._m11; _bones[o + 5] = r._m12;
            _bones[o + 6] = r._m20; _bones[o + 7] = r._m21; _bones[o + 8] = r._m22;
            _bones[o + 9] = t.x; _bones[o + 10] = t.y; _bones[o + 11] = t.z;
        }

        float3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue, float.MinValue);
        for (int v = 0; v < Skinned.Length; v++)
        {
            var p = s.Vertices[v]; float w = s.WeightA[v];
            int a = 12 * s.BoneA[v], b = 12 * s.BoneB[v];
            var q = new float3(
                w * (_bones[a] * p.x + _bones[a + 1] * p.y + _bones[a + 2] * p.z + _bones[a + 9]) + (1 - w) * (_bones[b] * p.x + _bones[b + 1] * p.y + _bones[b + 2] * p.z + _bones[b + 9]),
                w * (_bones[a + 3] * p.x + _bones[a + 4] * p.y + _bones[a + 5] * p.z + _bones[a + 10]) + (1 - w) * (_bones[b + 3] * p.x + _bones[b + 4] * p.y + _bones[b + 5] * p.z + _bones[b + 10]),
                w * (_bones[a + 6] * p.x + _bones[a + 7] * p.y + _bones[a + 8] * p.z + _bones[a + 11]) + (1 - w) * (_bones[b + 6] * p.x + _bones[b + 7] * p.y + _bones[b + 8] * p.z + _bones[b + 11]));
            Skinned[v] = q; VertexNormal[v] = new float3(0, 0, 0);
            lo = math.min(lo, q); hi = math.max(hi, q);
        }
        Lo = lo; Hi = hi;

        var corners = s.Corners;
        for (int t = 0; t < Facet.Length; t++)
        {
            int c0 = corners[3 * t], c1 = corners[3 * t + 1], c2 = corners[3 * t + 2];
            var a = Skinned[c0]; var b = Skinned[c1]; var c = Skinned[c2];
            // The winding is clockwise from outside, so the counter-clockwise cross points in and its negation is the outward, area-weighted normal.
            var outward = -math.cross(b - a, c - a);
            float twiceArea = math.length(outward);
            Facet[t] = twiceArea > 1e-24f ? outward / twiceArea : new float3(0, 1, 0);
            VertexNormal[c0] += outward; VertexNormal[c1] += outward; VertexNormal[c2] += outward;
            float h0 = Altitude3(twiceArea, b, c), h1 = Altitude3(twiceArea, c, a), h2 = Altitude3(twiceArea, a, b);
            Altitude[3 * t] = h0; Altitude[3 * t + 1] = h1; Altitude[3 * t + 2] = h2;
            SmallestAltitude[t] = MathF.Min(h0, MathF.Min(h1, h2));
        }
        for (int v = 0; v < VertexNormal.Length; v++)
        {
            float l = math.length(VertexNormal[v]);
            VertexNormal[v] = l > 1e-24f ? VertexNormal[v] / l : new float3(0, 1, 0);
        }

        for (int slot = 0; slot < Crease.Length; slot++)
        {
            int other = s.Across[slot];
            if (other < 0) { Crease[slot] = 0; continue; }
            if (other < slot) continue; // its pair, lower in the array, set both
            var fn = Facet[slot / 3]; var fm = Facet[other / 3];
            // Convex from a side: the neighbour's far corner (the corner opposite the shared edge) lies behind this facet's plane.
            bool convexHere = math.dot(fn, Skinned[corners[other]] - Skinned[corners[3 * (slot / 3) + (slot % 3 + 1) % 3]]) < 0;
            bool convexThere = math.dot(fm, Skinned[corners[slot]] - Skinned[corners[3 * (other / 3) + (other % 3 + 1) % 3]]) < 0;
            if (convexHere != convexThere) { Crease[slot] = Crease[other] = 0; continue; }
            float half = 0.5f * MathF.Atan2(math.length(math.cross(fn, fm)), math.dot(fn, fm));
            Crease[slot] = Crease[other] = convexHere ? half : -half;
        }
    }

    private static float Altitude3(float twiceArea, float3 p, float3 q)
    {
        float length = math.length(p - q);
        return length > 1e-12f ? twiceArea / length : 0;
    }
}
