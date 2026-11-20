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

    /// <summary>
    /// One entry per triangle corner slot, 3t+k = edge k of triangle t, the edge opposite corner k: half the turn from this facet's
    /// normal to the neighbour's across that edge, in radians, positive where the edge is convex. An edge not shared by exactly two
    /// triangles has 0, and so has a fold (two triangles lying over each other, which the Newton step can make). Both sides of an edge carry the same value, so the shader turns both facets to one bisector.
    /// </summary>
    public required float[] Crease { get; init; }

    /// <summary>How far a skinned vertex can lie from its bone's origin.</summary>
    public required float Reach { get; init; }

    /// <summary>The mesher's own account of its untangle: how many vertices it pulled back toward their net position, and the worst distance (m) of one of them from the isosurface.</summary>
    public required int Pulled { get; init; }
    public required float OffSurface { get; init; }

    public int Triangles => Positions.Length / 3;
}

/// <summary>
/// The puppet's surface, engine-free: every shape body is an SDF primitive; a body that is the child of a joint is
/// smooth-min'd with its parent and a ball at the joint; the field is meshed once (surface nets) at a spread bind
/// pose and each vertex keeps the two bones of the term that won it.
/// </summary>
public static class PuppetField
{
    /// <summary>The grid cell (m): the coarsest at which the thinnest limb, radius 40 mm, still spans two cells across (PuppetChecks measures this against the coarser cells).</summary>
    public const float Cell = 0.04f;

    /// <summary>How far a Spherical joint's child subtree swings in the bind pose, so a limb hanging beside the trunk is not webbed to it.</summary>
    public const float SpreadDegrees = 20f;

    private const int Newton = 2;

    /// <summary>A body's radius, or for a box its smallest half extent: the thickness of the limb it draws.</summary>
    public static float Thickness(Box3DSoaKind kind, float3 size) => kind == Box3DSoaKind.Box ? math.min(size.x, math.min(size.y, size.z)) : size.x;

    /// <summary>The bodies of the lanes that are drawn: a shape, not an Anchor or a StaticBox.</summary>
    public static bool HasShape(Box3DSoaKind kind) => kind is Box3DSoaKind.Sphere or Box3DSoaKind.Capsule or Box3DSoaKind.Box;

    /// <summary>A joint that blends two bodies: Revolute, Spherical or Weld (never Motor), both bodies with shapes.</summary>
    public static bool Qualifies(Box3DSoaJointKind joint, Box3DSoaKind a, Box3DSoaKind b) =>
        joint is Box3DSoaJointKind.Revolute or Box3DSoaJointKind.Spherical or Box3DSoaJointKind.Weld && HasShape(a) && HasShape(b);

    /// <summary>Builds the shape on a grid of <paramref name="cell"/> metres (the shipped <see cref="Cell"/>; the resolution check sweeps it).</summary>
    public static PuppetShape Build(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints, float smin, float ballScale, float boxRound, float cell = Cell)
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
        return Mesh(field, bodies, t, joints, smin, cell);
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

    private static PuppetShape Mesh(Field f, IReadOnlyList<PuppetBody> bodies, Rigid[] t, IReadOnlyList<PuppetJoint> joints, float smin, float cell)
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
        float pad = smin + 2 * cell;
        lo -= pad; hi += pad;
        int nx = (int)MathF.Ceiling((hi.x - lo.x) / cell) + 1, ny = (int)MathF.Ceiling((hi.y - lo.y) / cell) + 1, nz = (int)MathF.Ceiling((hi.z - lo.z) / cell) + 1;
        var samples = new float[nx, ny, nz];
        for (int x = 0; x < nx; x++)
        for (int y = 0; y < ny; y++)
        for (int z = 0; z < nz; z++)
            samples[x, y, z] = f.Eval(lo + new float3(x, y, z) * cell, out _, out _, out _).w;
        var nets = CultGeometrySurfaceNets.Extract(samples, 0f, lo, cell);

        // Two Newton steps put each net vertex on the isosurface; the untangle then pulls back the few it threw into a fold.
        var net = new float3[nets.VertexCount]; var projected = new float3[net.Length];
        for (int i = 0; i < net.Length; i++)
        {
            net[i] = new float3(nets.Positions[3 * i], nets.Positions[3 * i + 1], nets.Positions[3 * i + 2]);
            var p = net[i];
            for (int it = 0; it < Newton; it++) { var e = f.Eval(p, out _, out _, out _); p -= e.w * new float3(e.x, e.y, e.z); }
            projected[i] = p;
        }
        var (v, tris, pulled, offSurface) = Untangle(f, nets.Quads, net, projected);

        var pos = new List<float3>(); var nor = new List<float3>();
        var ba = new List<int>(); var bb = new List<int>(); var wa = new List<float>();
        var corner = new List<int>();
        // The nets wind every quad counter-clockwise seen from the field's outside; Godot's front face is clockwise, so each triangle is emitted reversed.
        for (int k = 0; k < tris.Length; k += 3)
        {
            int ia = tris[k], ib = tris[k + 2], ic = tris[k + 1];
            var cross = math.cross(v[ib] - v[ia], v[ic] - v[ia]);
            var normal = -cross / math.length(cross);
            foreach (int i in new[] { ia, ib, ic })
            {
                f.Eval(v[i], out int bone, out int other, out float w);
                pos.Add(v[i]); nor.Add(normal); ba.Add(bone); bb.Add(other); wa.Add(w);
            }
            corner.Add(ia); corner.Add(ib); corner.Add(ic);
        }
        var crease = Creases(corner, pos, nor);

        float maxBall = 0;
        for (int i = 0; i < n; i++) maxBall = MathF.Max(maxBall, f.BallR[i]);
        var ids = new ulong[n];
        for (int i = 0; i < n; i++) ids[i] = bodies[i].Id;
        var js = new (Box3DSoaJointKind, int, int, float3)[joints.Count];
        for (int j = 0; j < js.Length; j++) js[j] = (joints[j].Kind, joints[j].A, joints[j].B, t[joints[j].A].Apply(joints[j].LocalOrigin));
        return new PuppetShape
        {
            Ids = ids, SpreadBind = t, Joints = js,
            Positions = pos.ToArray(), Normals = nor.ToArray(), BoneA = ba.ToArray(), BoneB = bb.ToArray(), WeightA = wa.ToArray(), Crease = crease,
            Reach = 2 * reach + 2 * maxBall + smin, Pulled = pulled, OffSurface = offSurface,
        };
    }

    /// <summary>The untangle's pull-back rounds: each halves the Newton displacement of the vertices it marks, so twelve leave them at the net.</summary>
    private const int UntangleRounds = 12;

    /// <summary>
    /// The nets' triangles (quads split on the flatter diagonal, wound as the nets wind them) over the vertices <paramref name="projected"/>
    /// onto the isosurface, with every vertex of a bad triangle pulled back toward its net position, half as far each round, until none
    /// is bad or <see cref="UntangleRounds"/> rounds are done. A triangle is bad when it faces against the field's gradient at its centroid,
    /// is inverted or collapsed against its net triangle (its cross has a component along the net's of at most 5% of the net's squared
    /// length), or has an edge that is a fold (its two triangles disagree on convexity). Returns the vertices, the triangles (three
    /// indices each), how many vertices were pulled back and the worst distance of one from the isosurface (m).
    /// </summary>
    private static (float3[] V, int[] Tris, int Pulled, float OffSurface) Untangle(Field f, uint[] quads, float3[] net, float3[] projected)
    {
        var v = (float3[])projected.Clone();
        var pull = new float[v.Length]; Array.Fill(pull, 1f);
        int[] tris;
        for (int round = 0; ; round++)
        {
            tris = Triangulate(quads, v);
            var bad = new HashSet<int>();
            var sides = new Dictionary<(int, int), List<int>>();
            for (int k = 0; k < tris.Length; k += 3)
            {
                int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                var cross = math.cross(v[b] - v[a], v[c] - v[a]);
                var netCross = math.cross(net[b] - net[a], net[c] - net[a]);
                var g = f.Eval((v[a] + v[b] + v[c]) / 3f, out _, out _, out _);
                // The nets wind every quad counter-clockwise seen from the field's outside, so a triangle's cross points along the gradient.
                bool against = math.dot(cross, new float3(g.x, g.y, g.z)) <= 0;
                bool inverted = math.dot(cross, netCross) <= 0.05f * math.lengthsq(netCross);
                if (against || inverted) { bad.Add(a); bad.Add(b); bad.Add(c); }
                for (int e = 0; e < 3; e++)
                {
                    int p = tris[k + (e + 1) % 3], q = tris[k + (e + 2) % 3];
                    var key = p < q ? (p, q) : (q, p);
                    if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<int>(2);
                    list.Add(k + e);
                }
            }
            foreach (var list in sides.Values)
            {
                if (list.Count != 2) continue;
                if (Convex(tris, v, list[0], list[1]) != Convex(tris, v, list[1], list[0]) && Turns(tris, v, list[0], list[1]))
                    foreach (int s in list) { bad.Add(tris[s]); bad.Add(tris[s - s % 3 + (s % 3 + 1) % 3]); bad.Add(tris[s - s % 3 + (s % 3 + 2) % 3]); }
            }
            if (bad.Count == 0 || round == UntangleRounds) break;
            foreach (int i in bad) { pull[i] *= 0.5f; v[i] = net[i] + (projected[i] - net[i]) * pull[i]; }
        }
        int pulled = 0; float off = 0;
        for (int i = 0; i < v.Length; i++)
            if (pull[i] < 1) { pulled++; off = MathF.Max(off, MathF.Abs(f.Eval(v[i], out _, out _, out _).w)); }
        return (v, tris, pulled, off);
    }

    /// <summary>The quads as triangles, each split on its flatter diagonal, in the quad's winding.</summary>
    private static int[] Triangulate(uint[] quads, float3[] v)
    {
        var tris = new int[quads.Length / 4 * 6];
        for (int i = 0, o = 0; i < quads.Length; i += 4, o += 6)
        {
            int ia = (int)quads[i], ib = (int)quads[i + 1], ic = (int)quads[i + 2], id = (int)quads[i + 3];
            var a = v[ia]; var b = v[ib]; var c = v[ic]; var d = v[id];
            bool ac = math.dot(Unit(math.cross(b - a, c - a)), Unit(math.cross(c - a, d - a))) >= math.dot(Unit(math.cross(b - a, d - a)), Unit(math.cross(c - b, d - b)));
            var t = ac ? new[] { ia, ib, ic, ia, ic, id } : new[] { ia, ib, id, ib, ic, id };
            t.CopyTo(tris, o);
        }
        return tris;
    }

    /// <summary>Whether, from the triangle of slot <paramref name="s"/>, the triangle of slot <paramref name="other"/> lies behind its plane: the edge between them is convex there.</summary>
    private static bool Convex(int[] tris, float3[] v, int s, int other)
    {
        int k = s - s % 3, e = s % 3;
        var n = math.cross(v[tris[k + 1]] - v[tris[k]], v[tris[k + 2]] - v[tris[k]]);
        return math.dot(n, v[tris[other]] - v[tris[k + (e + 1) % 3]]) < 0;
    }

    /// <summary>Whether the two triangles' normals differ by more than a hundredth of a radian: a flatter edge has no convexity to speak of.</summary>
    private static bool Turns(int[] tris, float3[] v, int s, int t)
    {
        int a = s - s % 3, b = t - t % 3;
        var na = Unit(math.cross(v[tris[a + 1]] - v[tris[a]], v[tris[a + 2]] - v[tris[a]]));
        var nb = Unit(math.cross(v[tris[b + 1]] - v[tris[b]], v[tris[b + 2]] - v[tris[b]]));
        return MathF.Atan2(math.length(math.cross(na, nb)), math.dot(na, nb)) >= 1e-2f;
    }

    /// <summary>
    /// Each triangle edge is matched to its neighbour by the two net-vertex indices it joins. An edge that exactly two triangles share
    /// gets, on each side, half the turn from this facet's normal to the other's: with u the edge's in-plane unit normal pointing away
    /// from the opposite corner and b the normalized sum of the two facet normals, atan2(b.u, b.n). Any other edge gets 0, and so does a fold.
    /// </summary>
    private static float[] Creases(List<int> corner, List<float3> pos, List<float3> nor)
    {
        var sides = new Dictionary<(int, int), List<int>>();
        for (int s = 0; s < corner.Count; s++)
        {
            int t = s / 3, k = s % 3;
            int p = corner[3 * t + (k + 1) % 3], q = corner[3 * t + (k + 2) % 3];
            var key = p < q ? (p, q) : (q, p);
            if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<int>(2);
            list.Add(s);
        }
        var crease = new float[corner.Count];
        foreach (var list in sides.Values)
        {
            if (list.Count != 2) continue;
            foreach (int s in list)
            {
                int t = s / 3, k = s % 3, other = list[0] == s ? list[1] : list[0];
                var n = nor[3 * t]; var m = nor[3 * (other / 3)];
                var p = pos[3 * t + (k + 1) % 3]; var q = pos[3 * t + (k + 2) % 3]; var r = pos[3 * t + k];
                var u = Unit(math.cross(Unit(q - p), n));
                if (math.dot(u, r - p) > 0) u = -u;
                var b = Unit(n + m);
                if (math.lengthsq(b) == 0) continue;
                crease[s] = MathF.Atan2(math.dot(b, u), math.dot(b, n));
            }
            // A fold, two triangles lying over each other, has no edge to bevel: the sides disagree on which way it turns.
            if (crease[list[0]] * crease[list[1]] < 0) crease[list[0]] = crease[list[1]] = 0;
        }
        return crease;
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

        public float Thickness(int i) => PuppetField.Thickness(_kind[i], _size[i]);

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
