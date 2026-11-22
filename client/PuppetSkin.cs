using System;
using CultMath;

namespace BadDay.Client;

/// <summary>
/// The puppet's drawn surface, engine-free, owned for every frame: <see cref="Update"/> skins the vertices of a <see cref="PuppetShape"/> by the bodies' transforms, one bone each, and poses the normals and
/// vertex normals the shape stores (derived once from each triangle's source: <see cref="PuppetShade"/>) by each corner's bone. A piece is rigid, so a normal, a crease, a line's distance and a cap never change with the pose
/// but a normal's direction, and nothing here is derived from the skinned corners: a fragment's own corners are not its source. Every array is allocated once, here, and refilled.
/// </summary>
public sealed class PuppetSkin
{
    private readonly PuppetShape _shape;
    private readonly float[] _bones;

    public PuppetSkin(PuppetShape shape)
    {
        _shape = shape;
        _bones = new float[12 * shape.Ids.Length];
        Skinned = new float3[shape.Vertices.Length];
        Normal = new float3[shape.Triangles];
        VertexNormal = new float3[shape.Corners.Length];
    }

    /// <summary>Every vertex skinned, in the space the bodies' transforms take the bind to.</summary>
    public float3[] Skinned { get; }

    /// <summary>Per triangle: the unit normal of its source seen from outside, posed.</summary>
    public float3[] Normal { get; }

    /// <summary>Per corner slot: the unit vertex normal the corner draws, posed.</summary>
    public float3[] VertexNormal { get; }

    /// <summary>The corners of the box around the skinned vertices.</summary>
    public float3 Lo { get; private set; }
    public float3 Hi { get; private set; }

    /// <summary><paramref name="bones"/> take each body from the bind to where it is now, one per body of the shape.</summary>
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
            var p = s.Vertices[v];
            int a = 12 * s.Bone[v];
            var q = new float3(
                _bones[a] * p.x + _bones[a + 1] * p.y + _bones[a + 2] * p.z + _bones[a + 9],
                _bones[a + 3] * p.x + _bones[a + 4] * p.y + _bones[a + 5] * p.z + _bones[a + 10],
                _bones[a + 6] * p.x + _bones[a + 7] * p.y + _bones[a + 8] * p.z + _bones[a + 11]);
            Skinned[v] = q;
            lo = math.min(lo, q); hi = math.max(hi, q);
        }
        Lo = lo; Hi = hi;

        var corners = s.Corners;
        for (int t = 0; t < Normal.Length; t++)
        {
            int a = 12 * s.Bone[corners[3 * t]]; // a triangle is one piece's: its three corners follow one bone
            Normal[t] = Turn(a, s.Normal[t]);
            for (int k = 0; k < 3; k++) VertexNormal[3 * t + k] = Turn(a, s.VertexNormal[3 * t + k]);
        }
    }

    private float3 Turn(int a, float3 n) => new(
        _bones[a] * n.x + _bones[a + 1] * n.y + _bones[a + 2] * n.z,
        _bones[a + 3] * n.x + _bones[a + 4] * n.y + _bones[a + 5] * n.z,
        _bones[a + 6] * n.x + _bones[a + 7] * n.y + _bones[a + 8] * n.z);
}
