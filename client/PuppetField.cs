using System;
using System.Collections.Generic;
using CultMath;
using System.Linq;
using System.Threading.Tasks;
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
/// What the puppet is made of, read-only: the bodies' creation pose (bone i is body i, and the bind), the joints, and the pieces' meshes welded
/// per piece: each vertex once, in the bind, with its one bone and its piece, and the triangles as indices into the vertices. Pieces are the dressed
/// bodies (piece i is body i) and then the joint balls (piece n + j is joint j's ball); no vertex belongs to two.
/// </summary>
public sealed class PuppetShape
{
    public required ulong[] Ids { get; init; }

    /// <summary>The creation pose of every body: the pose its vertices are kept in.</summary>
    public required Rigid[] Bind { get; init; }
    public required (Box3DSoaJointKind Kind, int A, int B, float3 Origin)[] Joints { get; init; }

    /// <summary>The radius (m) of each joint's ball, as <see cref="Joints"/>.</summary>
    public required float[] BallRadius { get; init; }

    /// <summary>The vertices in the bind.</summary>
    public required float3[] Vertices { get; init; }

    /// <summary>Per vertex, the one body it follows (a ball follows its joint's parent).</summary>
    public required int[] Bone { get; init; }

    /// <summary>Per vertex, the piece it belongs to.</summary>
    public required int[] Piece { get; init; }

    /// <summary>The triangles, three vertex indices each, wound clockwise seen from outside (Godot's front face).</summary>
    public required int[] Corners { get; init; }

    /// <summary>
    /// One entry per corner slot: for slot 3t+k, which is edge k of triangle t (the edge opposite corner k), the slot of the neighbour's corner
    /// opposite that same edge, or -1 unless exactly two triangles share the edge. Whoever pairs the two sides of an edge reads it here.
    /// </summary>
    public required int[] Across { get; init; }

    /// <summary>The mesher's own account of its untangle: how many vertices it pulled back toward their net position, and the worst distance (m) of one of them from the isosurface.</summary>
    public required int Pulled { get; init; }
    public required float OffSurface { get; init; }

    public int Triangles => Corners.Length / 3;

    /// <summary>One piece per body and one per joint.</summary>
    public int PieceCount => Ids.Length + Joints.Length;
}

/// <summary>A cut on a body piece at a joint: the socket the ball fills, and for a hinge the wedge of angles about the hinge axis the piece keeps, or for a Spherical joint's child the cone it keeps.</summary>
public sealed record PieceCut(float3 Origin, float BallRadius, bool Wedge, float3 E1, float3 E2, float PsiMin, float PsiMax, float3 ConeAxis = default, float ConeHalf = 0)
{
    public float SocketRadius => BallRadius + Doll.Seam;

    /// <summary>The wedge's two bounding planes' normals (a plane through the hinge axis per bound under 90 degrees) and the cone's cosine and sine, once.</summary>
    internal float3 NMax { get; } = E1 * -MathF.Sin(PsiMax) + E2 * MathF.Cos(PsiMax);
    internal float3 NMin { get; } = E1 * MathF.Sin(PsiMin) - E2 * MathF.Cos(PsiMin);
    internal float ConeCos { get; } = MathF.Cos(ConeHalf);
    internal float ConeSin { get; } = MathF.Sin(ConeHalf);
}

/// <summary>
/// One rigid piece's field, engine-free, in the bind: a dressed body's primitives smooth-min'd by <see cref="Doll.BodyBlend"/> (within the body only),
/// minus its sockets and clipped to its hinge wedges and child cone; or a ball, the sphere of the joint's radius about its origin. The field is a distance
/// bound, negative inside.
/// </summary>
public sealed class PieceField
{
    private readonly DollPrim[] _prims = Array.Empty<DollPrim>();
    private readonly Rigid _inverse;
    private readonly Rigid _bind;
    private readonly float3 _centre;
    private readonly float _radius;
    private readonly List<PieceCut> _cuts = new();

    /// <summary>Which piece this is (as <see cref="PuppetShape.Piece"/>) and which body it follows.</summary>
    public int Index { get; }
    public int Bone { get; }
    public bool IsBall { get; }

    /// <summary>The corners of a box around the piece.</summary>
    public float3 Lo { get; }
    public float3 Hi { get; }
    public IReadOnlyList<PieceCut> Cuts => _cuts;

    /// <summary>The grid (m) the piece is extracted on before decimation.</summary>
    public float Cell { get; }

    /// <summary>The ball's centre and radius (for a ball).</summary>
    public float3 BallCentre => _centre;
    public float BallRadius => _radius;

    internal PieceField(int index, int bone, DollPrim[] prims, Rigid bind)
    {
        Index = index; Bone = bone; _prims = prims; _bind = bind; _inverse = bind.Inverse();
        var lo = new float3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
        float thinnest = float.MaxValue;
        foreach (var p in prims)
        {
            var (c, r) = p.Bound(); var w = bind.Apply(c);
            lo = math.min(lo, w - r); hi = math.max(hi, w + r);
            thinnest = MathF.Min(thinnest, p.Thickness);
        }
        Lo = lo; Hi = hi;
        Cell = MathF.Min(Doll.BodyCell, thinnest / 2);
    }

    internal PieceField(int index, int bone, float3 centre, float radius)
    {
        Index = index; Bone = bone; IsBall = true; _centre = centre; _radius = radius;
        Lo = centre - radius; Hi = centre + radius;
        Cell = MathF.Min(Doll.BallCell, radius / 4);
    }

    internal void Cut(PieceCut cut) => _cuts.Add(cut);

    private static float SmoothMin(float a, float b, float k)
    {
        float h = math.saturate(0.5f + 0.5f * (b - a) / k);
        return b + (a - b) * h - k * h * (1 - h);
    }

    /// <summary>The field at world point <paramref name="p"/>.</summary>
    public float Value(float3 p)
    {
        if (IsBall) return math.length(p - _centre) - _radius;
        var q = _inverse.Apply(p);
        float d = _prims[0].Dist(q);
        for (int i = 1; i < _prims.Length; i++) d = SmoothMin(d, _prims[i].Dist(q), Doll.BodyBlend);
        foreach (var c in _cuts)
        {
            var r = p - c.Origin;
            d = MathF.Max(d, c.SocketRadius - math.length(r));
            if (c.ConeHalf > 0)
            {
                // Keep the cone of half-angle ConeHalf about ConeAxis, its apex at the joint.
                float along = math.dot(r, c.ConeAxis), perp = math.length(r - c.ConeAxis * along);
                float cs = c.ConeCos, sn = c.ConeSin;
                d = MathF.Max(d, along * cs + perp * sn < 0 ? math.length(r) : perp * cs - along * sn);
            }
            if (!c.Wedge) continue;
            // Keep the angles psi in [PsiMin, PsiMax] about the hinge axis: a half-space through the axis per bound under 90 degrees.
            if (c.PsiMax < MathF.PI / 2 - 1e-4f) d = MathF.Max(d, math.dot(r, c.NMax));
            if (c.PsiMin > -MathF.PI / 2 + 1e-4f) d = MathF.Max(d, math.dot(r, c.NMin));
        }
        return d;
    }

    /// <summary>The field's gradient by central differences (the cuts have no closed form worth keeping).</summary>
    public float3 Gradient(float3 p)
    {
        const float e = 0.0005f;
        return new float3(Value(p + new float3(e, 0, 0)) - Value(p - new float3(e, 0, 0)), Value(p + new float3(0, e, 0)) - Value(p - new float3(0, e, 0)), Value(p + new float3(0, 0, e)) - Value(p - new float3(0, 0, e))) / (2 * e);
    }

    /// <summary>
    /// The limb guard's skeleton: the primitives' axis points (a round cone's at five stations, the others' centre) that lie inside the piece, each with
    /// half its depth as clearance; a ball's is its centre with half its radius.
    /// </summary>
    public (float3 P, float Clear)[] Guard()
    {
        if (IsBall) return new[] { (_centre, 0.5f * _radius) };
        var list = new List<(float3, float)>();
        foreach (var pr in _prims)
        {
            var stations = pr.Shape == DollShape.RoundCone ? new[] { 0f, .25f, .5f, .75f, 1f } : new[] { 0f };
            foreach (float s in stations)
            {
                var w = _bind.Apply(pr.A + (pr.B - pr.A) * s);
                float v = Value(w);
                if (v < -0.01f) list.Add((w, 0.5f * -v));
            }
        }
        return list.ToArray();
    }

    /// <summary>The hard clearance: a body piece may not reach into one of its balls, and a ball may not reach outside its sphere.</summary>
    public bool Forbidden(float3 p)
    {
        const float slack = 0.0002f;
        if (IsBall) return math.length(p - _centre) > _radius + slack;
        foreach (var c in _cuts) if (math.length(p - c.Origin) < c.BallRadius + slack) return true;
        return false;
    }
}

/// <summary>
/// The puppet's surface, engine-free: the ball-jointed doll as rigid pieces (lodur:ruling:puppet-joints-ball-jointed-doll). Every shape body is dressed
/// from <see cref="Doll"/> (or drawn as its physics shape) into its own closed piece, every joint gets a ball piece, and each piece is meshed fine
/// (surface nets) and decimated to <see cref="Doll.FacetError"/> or <see cref="Doll.BallError"/> by <see cref="PuppetDecimate"/>. Each vertex has one
/// bone; nothing blends across a joint.
/// </summary>
public static class PuppetField
{
    private const int Newton = 2;

    /// <summary>A body's radius, or for a box its smallest half extent: the thickness of the limb it draws.</summary>
    public static float Thickness(Box3DSoaKind kind, float3 size) => kind == Box3DSoaKind.Box ? math.min(size.x, math.min(size.y, size.z)) : size.x;

    /// <summary>The bodies of the lanes that are drawn: a shape, not an Anchor or a StaticBox.</summary>
    public static bool HasShape(Box3DSoaKind kind) => kind is Box3DSoaKind.Sphere or Box3DSoaKind.Capsule or Box3DSoaKind.Box;

    /// <summary>A joint that gets a ball and sockets: Revolute, Spherical or Weld (never Motor), both bodies with shapes.</summary>
    public static bool Qualifies(Box3DSoaJointKind joint, Box3DSoaKind a, Box3DSoaKind b) =>
        joint is Box3DSoaJointKind.Revolute or Box3DSoaJointKind.Spherical or Box3DSoaJointKind.Weld && HasShape(a) && HasShape(b);

    /// <summary>
    /// The pieces' fields, bodies first (piece i is body i) then one ball per joint (piece n + j): a body from its <see cref="Doll"/> entry or its physics
    /// shape; a ball of the child's entry radius (or the child's thickness) at the joint origin, kept by the parent; at each joint both bodies lose the
    /// socket sphere (ball radius + <see cref="Doll.Seam"/>); a hinge's two bodies keep the wedge of angles about the hinge axis that the entry's range
    /// leaves (each bound (180 degrees - range) / 2 less <see cref="Doll.HingeMargin"/>, none past 90), and a Spherical joint's child the cone of half
    /// angle (180 degrees - swing cone) / 2 about its rest direction from the joint. The parent of a Spherical joint is not cut: a cone cut there removes the trunk.
    /// </summary>
    public static PieceField[] Dress(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints)
    {
        int n = bodies.Count;
        var pieces = new PieceField[n + joints.Count];
        for (int i = 0; i < n; i++)
            pieces[i] = new PieceField(i, i, Doll.Find(bodies[i].Kind, bodies[i].Size)?.Prims ?? Doll.Fallback(bodies[i].Kind, bodies[i].Size), bodies[i].Bind);
        for (int j = 0; j < joints.Count; j++)
        {
            var joint = joints[j];
            var o = bodies[joint.A].Bind.Apply(joint.LocalOrigin);
            var child = Doll.Find(bodies[joint.B].Kind, bodies[joint.B].Size);
            float radius = child is { BallRadius: > 0 } ? child.BallRadius : Thickness(bodies[joint.B].Kind, bodies[joint.B].Size);
            pieces[n + j] = new PieceField(n + j, joint.A, o, radius);
            var axis = math.normalize(math.mul(bodies[joint.B].Bind.R, Doll.HingeAxis));
            foreach (bool isParent in new[] { true, false })
            {
                int body = isParent ? joint.A : joint.B;
                var rest = bodies[body].Bind.T - o;
                if (joint.Kind == Box3DSoaJointKind.Spherical && !isParent && child is { SwingCone: > 0 } && math.length(rest) > 1e-6f)
                {
                    pieces[body].Cut(new PieceCut(o, radius, false, default, default, 0, 0, math.normalize(rest), MathF.Min(MathF.PI / 2, (MathF.PI - child.SwingCone) / 2)));
                    continue;
                }
                var square = rest - axis * math.dot(rest, axis);
                if (joint.Kind != Box3DSoaJointKind.Revolute || child is not { IsHinge: true } || math.length(square) < 1e-6f)
                {
                    pieces[body].Cut(new PieceCut(o, radius, false, default, default, 0, 0));
                    continue;
                }
                var e1 = math.normalize(square); var e2 = math.cross(axis, e1);
                float half = MathF.PI / 2;
                float up = MathF.Min(half, (MathF.PI - MathF.Min(child.Flex, Doll.HingeClean)) / 2 - Doll.HingeMargin);
                float down = MathF.Min(half, (MathF.PI - MathF.Min(child.Extension, Doll.HingeClean)) / 2 - Doll.HingeMargin);
                pieces[body].Cut(isParent ? new PieceCut(o, radius, true, e1, e2, -down, up) : new PieceCut(o, radius, true, e1, e2, -up, down));
            }
        }
        return pieces;
    }

    /// <summary>Builds the shape: dresses the pieces, extracts each fine, decimates it, and welds the pieces' meshes into one (no vertex is shared across pieces).</summary>
    public static PuppetShape Build(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints)
    {
        int n = bodies.Count;
        var pieces = Dress(bodies, joints);
        // Each piece is meshed and decimated on its own (nothing is shared), so they run side by side; the shape is welded in piece order.
        var meshes = new (float3[] V, int[] Tris)[pieces.Length]; var pulledBy = new int[pieces.Length]; var offBy = new float[pieces.Length];
        Parallel.For(0, pieces.Length, i =>
        {
            var f = pieces[i];
            var (v, tris, p, off) = Extract(f);
            pulledBy[i] = p; offBy[i] = off;
            meshes[i] = (v, tris);
        });
        var vertices = new List<float3>(); var bone = new List<int>(); var piece = new List<int>();
        var corners = new List<int>();
        for (int i = 0; i < pieces.Length; i++)
        {
            var (v, tris) = meshes[i];
            // The nets wind every quad counter-clockwise seen from the field's outside; Godot's front face is clockwise, so each triangle is emitted reversed.
            int first = vertices.Count;
            vertices.AddRange(v);
            for (int k = 0; k < v.Length; k++) { bone.Add(pieces[i].Bone); piece.Add(i); }
            for (int k = 0; k < tris.Length; k += 3) { corners.Add(first + tris[k]); corners.Add(first + tris[k + 2]); corners.Add(first + tris[k + 1]); }
        }
        int pulled = pulledBy.Sum(); float offSurface = offBy.Max();
        var cornerArray = corners.ToArray();

        var ids = new ulong[n]; var bind = new Rigid[n];
        for (int i = 0; i < n; i++) { ids[i] = bodies[i].Id; bind[i] = bodies[i].Bind; }
        var js = new (Box3DSoaJointKind, int, int, float3)[joints.Count]; var radii = new float[joints.Count];
        for (int j = 0; j < js.Length; j++)
        {
            js[j] = (joints[j].Kind, joints[j].A, joints[j].B, pieces[n + j].BallCentre);
            radii[j] = pieces[n + j].BallRadius;
        }
        return new PuppetShape
        {
            Ids = ids, Bind = bind, Joints = js, BallRadius = radii,
            Vertices = vertices.ToArray(), Bone = bone.ToArray(), Piece = piece.ToArray(), Corners = cornerArray, Across = Across(cornerArray),
            Pulled = pulled, OffSurface = offSurface,
        };
    }

    /// <summary>One piece on its fine grid: surface nets, two Newton steps onto the isosurface, the untangle, and the split of non-manifold vertices. Triangles wound as the nets wind them.</summary>
    private static (float3[] V, int[] Tris, int Pulled, float OffSurface) Extract(PieceField f)
    {
        float cell = f.Cell, pad = 2 * cell + 0.04f;
        var lo = f.Lo - pad; var hi = f.Hi + pad;
        int nx = (int)MathF.Ceiling((hi.x - lo.x) / cell) + 1, ny = (int)MathF.Ceiling((hi.y - lo.y) / cell) + 1, nz = (int)MathF.Ceiling((hi.z - lo.z) / cell) + 1;
        var samples = new float[nx, ny, nz];
        Parallel.For(0, nx, x =>
        {
            for (int y = 0; y < ny; y++) for (int z = 0; z < nz; z++) samples[x, y, z] = f.Value(lo + new float3(x, y, z) * cell);
        });
        var nets = CultGeometrySurfaceNets.Extract(samples, 0f, lo, cell);

        var net = new float3[nets.VertexCount]; var projected = new float3[net.Length];
        for (int i = 0; i < net.Length; i++)
        {
            net[i] = new float3(nets.Positions[3 * i], nets.Positions[3 * i + 1], nets.Positions[3 * i + 2]);
            var p = net[i];
            for (int it = 0; it < Newton; it++) { var g = f.Gradient(p); p -= f.Value(p) * g / MathF.Max(math.lengthsq(g), 1e-6f); }
            projected[i] = p;
        }
        var (v, tris, pulled, off) = Untangle(f, nets.Quads, net, projected);
        v = SplitNonManifold(v, tris);
        return (v, LargestSolid(tris, v.Length), pulled, off);
    }

    /// <summary>
    /// Duplicates every vertex whose incident triangles fall into more than one fan (fans joined only across edges exactly two triangles share): two
    /// sheets of the surface that touch at a point or along an edge become two sheets that meet without sharing a vertex. Rewrites <paramref name="tris"/> in place.
    /// </summary>
    private static int[] LargestSolid(int[] tris, int vertexCount)
    {
        // A piece is one solid: what a cut leaves of a primitive below the grid's resolution (a film around a socket) is dropped, not drawn as a sliver.
        var parent = Enumerable.Range(0, vertexCount).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int k = 0; k < tris.Length; k += 3) { parent[Find(tris[k + 1])] = Find(tris[k]); parent[Find(tris[k + 2])] = Find(tris[k]); }
        var count = new Dictionary<int, int>();
        for (int k = 0; k < tris.Length; k += 3) { int r = Find(tris[k]); count[r] = count.GetValueOrDefault(r) + 1; }
        int largest = count.MaxBy(c => c.Value).Key;
        var kept = new List<int>();
        for (int k = 0; k < tris.Length; k += 3) if (Find(tris[k]) == largest) { kept.Add(tris[k]); kept.Add(tris[k + 1]); kept.Add(tris[k + 2]); }
        return kept.ToArray();
    }

    private static float3[] SplitNonManifold(float3[] v, int[] tris)
    {
        var orig = (int[])tris.Clone();
        int count = orig.Length / 3;
        // Directed half-edges per undirected edge.
        var sides = new Dictionary<(int, int), List<(int Tri, bool Forward)>>();
        for (int t = 0; t < count; t++)
            for (int e = 0; e < 3; e++)
            {
                int a = orig[3 * t + e], b = orig[3 * t + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!sides.TryGetValue(key, out var l)) sides[key] = l = new List<(int, bool)>(2);
                l.Add((t, a < b));
            }
        float3 N(int t) => Unit(math.cross(v[orig[3 * t + 1]] - v[orig[3 * t]], v[orig[3 * t + 2]] - v[orig[3 * t]]));
        // Which triangles an edge joins: the two of a manifold edge; on a non-manifold edge each forward triangle pairs with the backward one
        // whose normal is nearest its own (the same sheet turns a little across the edge; a touching sheet faces the other way).
        var joins = new Dictionary<(int, int), List<(int, int)>>();
        foreach (var (key, l) in sides)
        {
            var pairs = new List<(int, int)>();
            if (l.Count == 2) { if (l[0].Forward != l[1].Forward) pairs.Add((l[0].Tri, l[1].Tri)); }
            else
            {
                var fw = l.Where(x => x.Forward).Select(x => x.Tri).ToList(); var bw = l.Where(x => !x.Forward).Select(x => x.Tri).ToList();
                foreach (int t in fw)
                {
                    int best = -1; float bd = float.MinValue;
                    foreach (int b in bw) { float d = math.dot(N(t), N(b)); if (d > bd) { bd = d; best = b; } }
                    if (best >= 0) { pairs.Add((t, best)); bw.Remove(best); }
                }
            }
            joins[key] = pairs;
        }
        var incident = new List<int>[v.Length];
        for (int i = 0; i < v.Length; i++) incident[i] = new List<int>();
        for (int t = 0; t < count; t++) for (int e = 0; e < 3; e++) incident[orig[3 * t + e]].Add(t);
        var nv = new List<float3>(v);
        for (int i = 0; i < v.Length; i++)
        {
            var ts = incident[i]; if (ts.Count == 0) continue;
            var local = new Dictionary<int, int>(); for (int a = 0; a < ts.Count; a++) local[ts[a]] = a;
            var parent = new int[ts.Count]; for (int a = 0; a < parent.Length; a++) parent[a] = a;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (int t in ts)
                for (int e = 0; e < 3; e++)
                {
                    int w = orig[3 * t + e]; if (w == i) continue;
                    foreach (var (p, q) in joins[i < w ? (i, w) : (w, i)])
                        if (local.TryGetValue(p, out int lp) && local.TryGetValue(q, out int lq)) parent[Find(lp)] = Find(lq);
                }
            var copyOf = new Dictionary<int, int>();
            int first = Find(0);
            for (int a = 0; a < ts.Count; a++)
            {
                int r = Find(a); if (r == first) continue;
                if (!copyOf.TryGetValue(r, out int c)) { copyOf[r] = c = nv.Count; nv.Add(v[i]); }
                for (int e = 0; e < 3; e++) if (orig[3 * ts[a] + e] == i) tris[3 * ts[a] + e] = c;
            }
        }
        return nv.ToArray();
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
    private static (float3[] V, int[] Tris, int Pulled, float OffSurface) Untangle(PieceField f, uint[] quads, float3[] net, float3[] projected)
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
                var g = f.Gradient((v[a] + v[b] + v[c]) / 3f);
                // The nets wind every quad counter-clockwise seen from the field's outside, so a triangle's cross points along the gradient.
                bool against = math.dot(cross, g) <= 0;
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
            if (pull[i] < 1) { pulled++; off = MathF.Max(off, MathF.Abs(f.Value(v[i]))); }
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

    /// <summary>Each triangle edge is matched to its neighbour by the two vertex indices it joins; <see cref="PuppetShape.Across"/>.</summary>
    private static int[] Across(int[] corners)
    {
        var sides = new Dictionary<(int, int), List<int>>();
        for (int s = 0; s < corners.Length; s++)
        {
            int t = s / 3, k = s % 3;
            int p = corners[3 * t + (k + 1) % 3], q = corners[3 * t + (k + 2) % 3];
            var key = p < q ? (p, q) : (q, p);
            if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<int>(2);
            list.Add(s);
        }
        var across = new int[corners.Length];
        Array.Fill(across, -1);
        foreach (var list in sides.Values)
            if (list.Count == 2) { across[list[0]] = list[1]; across[list[1]] = list[0]; }
        return across;
    }

    private static float3 Unit(float3 v)
    {
        float l = math.length(v);
        return l > 1e-12f ? v / l : new float3(0, 0, 0);
    }
}
