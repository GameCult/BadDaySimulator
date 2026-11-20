using System;
using System.Collections.Generic;
using CultMath;
using GameCult.Geometry;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>A rigid transform: <c>world = R * local + T</c>.</summary>
public readonly struct Rigid
{
    public readonly float3x3 R;
    public readonly float3 T;

    public Rigid(float3x3 r, float3 t) { R = r; T = t; }

    public static Rigid FromQuaternion(float x, float y, float z, float w, float3 t) => new(new float3x3(
        1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
        2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
        2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)), t);

    /// <summary>The turn by <paramref name="radians"/> about the unit <paramref name="axis"/> through <paramref name="point"/>.</summary>
    public static Rigid About(float3 point, float3 axis, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians), u = 1 - c;
        var r = new float3x3(
            c + axis.x * axis.x * u, axis.x * axis.y * u - axis.z * s, axis.x * axis.z * u + axis.y * s,
            axis.y * axis.x * u + axis.z * s, c + axis.y * axis.y * u, axis.y * axis.z * u - axis.x * s,
            axis.z * axis.x * u - axis.y * s, axis.z * axis.y * u + axis.x * s, c + axis.z * axis.z * u);
        return new Rigid(r, point - math.mul(r, point));
    }

    public float3 Apply(float3 local) => math.mul(R, local) + T;

    public Rigid Inverse()
    {
        var rt = new float3x3(R._m00, R._m10, R._m20, R._m01, R._m11, R._m21, R._m02, R._m12, R._m22);
        return new Rigid(rt, -math.mul(rt, T));
    }

    /// <summary>The transform that applies <paramref name="inner"/> first, then this.</summary>
    public Rigid After(Rigid inner) => new(math.mul(R, inner.R), math.mul(R, inner.T) + T);
}

/// <summary>A shape body as the lanes name it: its creation pose is the bind.</summary>
public readonly record struct PuppetBody(ulong Id, Box3DSoaKind Kind, float3 Size, Rigid Bind);

/// <summary>A qualifying joint: body A and B by index into the bodies, the joint origin in body A's frame.</summary>
public readonly record struct PuppetJoint(Box3DSoaJointKind Kind, int A, int B, float3 LocalOrigin);

/// <summary>
/// What the puppet is made of, read-only: the spread bind per shape body (bone i is body i), the joints, and the
/// mesh as unwelded flat triangles (three entries per triangle) with each vertex's two bones and the first one's weight.
/// </summary>
public sealed class PuppetShape
{
    public required ulong[] Ids { get; init; }
    public required Rigid[] SpreadBind { get; init; }
    public required (Box3DSoaJointKind Kind, int A, int B, float3 Origin)[] Joints { get; init; }
    public required float3[] Positions { get; init; }
    public required float3[] Normals { get; init; }
    public required int[] BoneA { get; init; }
    public required int[] BoneB { get; init; }
    public required float[] WeightA { get; init; }

    /// <summary>How far a skinned vertex can lie from its bone's origin.</summary>
    public required float Reach { get; init; }

    /// <summary>How many times the mesh has been built; Puppet counts.</summary>
    public int Builds { get; internal set; }

    public int Triangles => Positions.Length / 3;
}

/// <summary>
/// The puppet's surface, engine-free: every shape body is an SDF primitive; a body that is the child of a joint is
/// smooth-min'd with its parent and a ball at the joint; the field is meshed once (surface nets) at a spread bind
/// pose and each vertex keeps the two bones of the term that won it.
/// </summary>
public static class PuppetField
{
    /// <summary>The grid cell, the minimum that resolves the thinnest limb (radius 40 mm).</summary>
    public const float Cell = 0.04f;

    /// <summary>How far a Spherical joint's child subtree swings in the bind pose, so a limb hanging beside the trunk is not webbed to it.</summary>
    public const float SpreadDegrees = 20f;

    private const int Newton = 2;

    /// <summary>The bodies of the lanes that are drawn: a shape, not an Anchor or a StaticBox.</summary>
    public static bool HasShape(Box3DSoaKind kind) => kind is Box3DSoaKind.Sphere or Box3DSoaKind.Capsule or Box3DSoaKind.Box;

    /// <summary>A joint that blends two bodies: Revolute, Spherical or Weld (never Motor), both bodies with shapes.</summary>
    public static bool Qualifies(Box3DSoaJointKind joint, Box3DSoaKind a, Box3DSoaKind b) =>
        joint is Box3DSoaJointKind.Revolute or Box3DSoaJointKind.Spherical or Box3DSoaJointKind.Weld && HasShape(a) && HasShape(b);

    public static PuppetShape Build(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints, float smin, float ballScale, float boxRound)
    {
        int n = bodies.Count;
        // The first qualifying joint with body b as its B decides b's blend (lane order); every other joint of b is ignored.
        var up = new int[n]; var jointOf = new int[n];
        Array.Fill(up, -1); Array.Fill(jointOf, -1);
        for (int j = 0; j < joints.Count; j++) if (jointOf[joints[j].B] < 0) { jointOf[joints[j].B] = j; up[joints[j].B] = joints[j].A; }

        // Spread, parents first: each Spherical joint swings B's subtree about its origin.
        var t = new Rigid[n];
        for (int i = 0; i < n; i++) t[i] = bodies[i].Bind;
        var order = new List<int>();
        for (int i = 0; i < n; i++) if (jointOf[i] >= 0) order.Add(i);
        order.Sort((x, y) => Depth(up, x) != Depth(up, y) ? Depth(up, x).CompareTo(Depth(up, y)) : x.CompareTo(y));
        foreach (int b in order)
        {
            var joint = joints[jointOf[b]];
            if (joint.Kind != Box3DSoaJointKind.Spherical) continue;
            var o = t[joint.A].Apply(joint.LocalOrigin);
            var axis = math.cross(t[joint.A].T - o, t[b].T - o);
            if (math.length(axis) < 1e-4f) continue;
            var swing = Rigid.About(o, math.normalize(axis), SpreadDegrees * MathF.PI / 180f);
            for (int i = 0; i < n; i++) if (IsUnder(up, i, b)) t[i] = swing.After(t[i]);
        }

        var field = new Field(bodies, t, joints, jointOf, smin, ballScale, boxRound);
        return Mesh(field, bodies, t, joints, smin);
    }

    private static int Depth(int[] up, int i)
    {
        int d = 0;
        for (; up[i] >= 0 && d <= up.Length; i = up[i]) d++;
        return d;
    }

    private static bool IsUnder(int[] up, int i, int root)
    {
        for (int d = 0; d <= up.Length; d++)
        {
            if (i == root) return true;
            if (up[i] < 0) return false;
            i = up[i];
        }
        return false;
    }

    private static PuppetShape Mesh(Field f, IReadOnlyList<PuppetBody> bodies, Rigid[] t, IReadOnlyList<PuppetJoint> joints, float smin)
    {
        int n = bodies.Count;
        var lo = new float3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = new float3(float.MinValue, float.MinValue, float.MinValue);
        float reach = 0;
        for (int i = 0; i < n; i++)
        {
            float r = f.Extent(i);
            lo = math.min(lo, t[i].T - r); hi = math.max(hi, t[i].T + r);
            reach = MathF.Max(reach, r);
            if (f.BallR[i] > 0) { lo = math.min(lo, f.Ball[i] - f.BallR[i]); hi = math.max(hi, f.Ball[i] + f.BallR[i]); }
        }
        float pad = smin + 2 * Cell;
        lo -= pad; hi += pad;
        int nx = (int)MathF.Ceiling((hi.x - lo.x) / Cell) + 1, ny = (int)MathF.Ceiling((hi.y - lo.y) / Cell) + 1, nz = (int)MathF.Ceiling((hi.z - lo.z) / Cell) + 1;
        var samples = new float[nx, ny, nz];
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        for (int z = 0; z < nz; z++)
            samples[x, y, z] = f.Eval(lo + new float3(x, y, z) * Cell, out _, out _, out _).w;
        var nets = CultGeometrySurfaceNets.Extract(samples, 0f, lo, Cell);

        // Two Newton steps put each net vertex on the isosurface.
        var v = new float3[nets.VertexCount];
        for (int i = 0; i < v.Length; i++)
        {
            var p = new float3(nets.Positions[3 * i], nets.Positions[3 * i + 1], nets.Positions[3 * i + 2]);
            for (int it = 0; it < Newton; it++) { var e = f.Eval(p, out _, out _, out _); p -= e.w * new float3(e.x, e.y, e.z); }
            v[i] = p;
        }

        var pos = new List<float3>(); var nor = new List<float3>();
        var ba = new List<int>(); var bb = new List<int>(); var wa = new List<float>();
        void Triangle(float3 a, float3 b, float3 c)
        {
            var cross = math.cross(b - a, c - a);
            float len = math.length(cross);
            if (len < 1e-12f) return;
            var g = f.Eval((a + b + c) / 3f, out _, out _, out _);
            // Godot's front face is clockwise: the counter-clockwise normal points into the body.
            if (math.dot(cross, new float3(g.x, g.y, g.z)) > 0) { (b, c) = (c, b); cross = -cross; }
            var normal = -cross / len;
            foreach (var q in new[] { a, b, c })
            {
                f.Eval(q, out int bone, out int other, out float w);
                pos.Add(q); nor.Add(normal); ba.Add(bone); bb.Add(other); wa.Add(w);
            }
        }
        for (int i = 0; i < nets.Quads.Length; i += 4)
        {
            var a = v[nets.Quads[i]]; var b = v[nets.Quads[i + 1]]; var c = v[nets.Quads[i + 2]]; var d = v[nets.Quads[i + 3]];
            // Split on the flatter diagonal.
            bool ac = math.dot(Unit(math.cross(b - a, c - a)), Unit(math.cross(c - a, d - a))) >= math.dot(Unit(math.cross(b - a, d - a)), Unit(math.cross(c - b, d - b)));
            if (ac) { Triangle(a, b, c); Triangle(a, c, d); }
            else { Triangle(a, b, d); Triangle(b, c, d); }
        }

        float maxBall = 0;
        for (int i = 0; i < n; i++) maxBall = MathF.Max(maxBall, f.BallR[i]);
        var ids = new ulong[n];
        for (int i = 0; i < n; i++) ids[i] = bodies[i].Id;
        var js = new (Box3DSoaJointKind, int, int, float3)[joints.Count];
        for (int j = 0; j < js.Length; j++) js[j] = (joints[j].Kind, joints[j].A, joints[j].B, t[joints[j].A].Apply(joints[j].LocalOrigin));
        return new PuppetShape
        {
            Ids = ids, SpreadBind = t, Joints = js,
            Positions = pos.ToArray(), Normals = nor.ToArray(), BoneA = ba.ToArray(), BoneB = bb.ToArray(), WeightA = wa.ToArray(),
            Reach = 2 * reach + 2 * maxBall + smin,
        };
    }

    private static float3 Unit(float3 v)
    {
        float l = math.length(v);
        return l > 1e-12f ? v / l : new float3(0, 0, 0);
    }

    /// <summary>The field in the spread bind: value and gradient (float4: gradient, value), and the bones of the winning term.</summary>
    private sealed class Field
    {
        private readonly int _n;
        private readonly Box3DSoaKind[] _kind;
        private readonly float3[] _size;
        private readonly Rigid[] _t;
        private readonly Rigid[] _inverse;
        private readonly int[] _up;
        private readonly float _smin, _round;
        public readonly float3[] Ball;
        public readonly float[] BallR;

        public Field(IReadOnlyList<PuppetBody> bodies, Rigid[] spread, IReadOnlyList<PuppetJoint> joints, int[] jointOf, float smin, float ballScale, float boxRound)
        {
            _n = bodies.Count; _smin = smin; _round = boxRound; _t = spread;
            _kind = new Box3DSoaKind[_n]; _size = new float3[_n]; _inverse = new Rigid[_n]; _up = new int[_n];
            Ball = new float3[_n]; BallR = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                _kind[i] = bodies[i].Kind; _size[i] = bodies[i].Size; _inverse[i] = spread[i].Inverse();
                _up[i] = jointOf[i] >= 0 ? joints[jointOf[i]].A : -1;
            }
            for (int i = 0; i < _n; i++)
            {
                if (jointOf[i] < 0) continue;
                var j = joints[jointOf[i]];
                var o = spread[j.A].Apply(j.LocalOrigin);
                Ball[i] = o;
                BallR[i] = MathF.Max(MathF.Max(Prim(j.A, o).w, Prim(i, o).w), 0f) + ballScale * MathF.Min(Thickness(j.A), Thickness(i));
            }
        }

        public float Thickness(int i) => _kind[i] == Box3DSoaKind.Box ? math.min(_size[i].x, math.min(_size[i].y, _size[i].z)) : _size[i].x;

        /// <summary>The body's reach from its origin: the farthest the primitive extends.</summary>
        public float Extent(int i) => _kind[i] == Box3DSoaKind.Box ? math.length(_size[i]) : _size[i].x + _size[i].y;

        /// <summary>One primitive's gradient and signed distance at world point <paramref name="p"/>.</summary>
        private float4 Prim(int i, float3 p)
        {
            var q = _inverse[i].Apply(p);
            var s = _size[i];
            float3 g; float d;
            switch (_kind[i])
            {
                case Box3DSoaKind.Sphere:
                {
                    float l = math.length(q);
                    d = l - s.x; g = l > 0 ? q / l : new float3(0, 1, 0);
                    break;
                }
                case Box3DSoaKind.Capsule:
                {
                    var w = q - new float3(0, math.clamp(q.y, -s.y, s.y), 0);
                    float l = math.length(w);
                    d = l - s.x; g = l > 0 ? w / l : new float3(1, 0, 0);
                    break;
                }
                default:
                {
                    float rr = math.min(_round, math.min(s.x, math.min(s.y, s.z)));
                    var a = math.abs(q) - (s - rr);
                    var o = math.max(a, 0f);
                    float ol = math.length(o);
                    d = ol + math.min(math.max(a.x, math.max(a.y, a.z)), 0f) - rr;
                    var sg = new float3(q.x < 0 ? -1 : 1, q.y < 0 ? -1 : 1, q.z < 0 ? -1 : 1);
                    if (ol > 0) g = sg * o / ol;
                    else if (a.x >= a.y && a.x >= a.z) g = new float3(sg.x, 0, 0);
                    else if (a.y >= a.z) g = new float3(0, sg.y, 0);
                    else g = new float3(0, 0, sg.z);
                    break;
                }
            }
            g = math.mul(_t[i].R, g);
            return new float4(g.x, g.y, g.z, d);
        }

        /// <summary>
        /// The field at <paramref name="p"/>: the minimum over the bodies' terms. A body that is a joint's B contributes
        /// smin(smin(itself, the joint ball), its parent), blended by that outer smin's weight; any other body is itself.
        /// The winning term names the vertex's bones: the body and its parent, with the body's weight.
        /// </summary>
        public float4 Eval(float3 p, out int bone, out int other, out float weight)
        {
            Span<float4> each = stackalloc float4[_n];
            for (int i = 0; i < _n; i++) each[i] = Prim(i, p);
            var best = new float4(0, 0, 0, float.MaxValue);
            bone = 0; other = 0; weight = 1;
            for (int i = 0; i < _n; i++)
            {
                var term = each[i]; float h = 1;
                if (_up[i] >= 0)
                {
                    var dp = p - Ball[i]; float l = math.length(dp);
                    var child = math.smin_grad(each[i], new float4(l > 0 ? dp / l : new float3(0, 1, 0), l - BallR[i]), _smin);
                    term = math.smin_grad(child, each[_up[i]], _smin);
                    h = math.saturate(0.5f + 0.5f * (each[_up[i]].w - child.w) / _smin);
                }
                if (term.w < best.w) { best = term; bone = i; other = _up[i] >= 0 ? _up[i] : i; weight = h; }
            }
            return best;
        }
    }
}
