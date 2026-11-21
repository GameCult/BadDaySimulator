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

    public int Triangles => Corners.Length / 3;

    /// <summary>One piece per body and one per joint.</summary>
    public int PieceCount => Ids.Length + Joints.Length;
}

/// <summary>A cut on a body piece at a joint: the socket the ball fills. A roll (<see cref="DollEntry.RollAxis"/>) is the segment Origin +- Roll swept by the ball's radius; no roll is a sphere.</summary>
public sealed record PieceCut(float3 Origin, float BallRadius, float3 Roll = default)
{
    public float SocketRadius => BallRadius + Doll.Seam;

    /// <summary>The distance from <paramref name="p"/> to the ball's core: the origin, or the segment Origin +- Roll. The ball is <c>Off(p) &lt;= BallRadius</c>.</summary>
    public float Off(float3 p) => Core(p - Origin, Roll);

    /// <summary>The distance from <paramref name="q"/> to the segment +- <paramref name="roll"/> about the origin (to the origin for a zero roll).</summary>
    public static float Core(float3 q, float3 roll)
    {
        float l2 = math.lengthsq(roll);
        return math.length(l2 > 0 ? q - roll * math.clamp(math.dot(q, roll) / l2, -1f, 1f) : q);
    }
}

/// <summary>
/// One rigid piece's field, engine-free, in the piece's own frame: a body's primitives smooth-min'd by <see cref="Doll.BodyBlend"/> (within the body only)
/// in the body's frame, minus its sockets at the joint origins in that frame; or a ball, the sphere of the joint's radius about the origin. The field is a
/// distance bound, negative inside. <see cref="Placement"/> takes the piece to the bind: a body's by its bind, a ball's by its joint origin.
/// </summary>
public sealed class PieceField
{
    private readonly DollPrim[] _prims = Array.Empty<DollPrim>();
    private readonly string _shape;
    private readonly float _radius;
    private readonly float3 _roll;
    private readonly List<PieceCut> _cuts = new();

    /// <summary>Which piece this is (as <see cref="PuppetShape.Piece"/>) and which body it follows.</summary>
    public int Index { get; }
    public int Bone { get; }
    public bool IsBall { get; }

    /// <summary>The piece's frame in the bind: the bind of a body, the translation to the joint origin for a ball.</summary>
    public Rigid Placement { get; }

    /// <summary>The corners of a box around the piece, in its frame.</summary>
    public float3 Lo { get; }
    public float3 Hi { get; }
    public IReadOnlyList<PieceCut> Cuts => _cuts;

    /// <summary>The grid (m) the piece is extracted on before decimation.</summary>
    public float Cell { get; }

    /// <summary>The ball's radius (for a ball).</summary>
    public float BallRadius => _radius;

    /// <summary>The ball's roll, a half-segment in the bind's axes (for a ball): zero for a sphere.</summary>
    public float3 Roll => _roll;

    /// <summary>How far (m) a body piece's surface may stray from its field away from the sockets: <see cref="Doll.FacetError"/>, less for a thin body (<see cref="Doll.ThinError"/>).</summary>
    public float Error { get; }

    internal PieceField(int index, int bone, DollPrim[] prims, string shape, Rigid bind, float error, float cell = 0)
    {
        Index = index; Bone = bone; _prims = prims; _shape = shape; Placement = bind; Error = error;
        var lo = new float3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
        float thinnest = float.MaxValue;
        foreach (var p in prims)
        {
            var (c, r) = p.Bound();
            lo = math.min(lo, c - r); hi = math.max(hi, c + r);
            thinnest = MathF.Min(thinnest, p.Thickness);
        }
        Lo = lo; Hi = hi;
        Cell = cell > 0 ? cell : MathF.Min(Doll.BodyCell, thinnest / 2);
    }

    internal PieceField(int index, int bone, float3 origin, float radius, float3 roll = default)
    {
        Index = index; Bone = bone; IsBall = true; _radius = radius; _roll = roll; _shape = "ball";
        Placement = new Rigid(new float3x3(1, 0, 0, 0, 1, 0, 0, 0, 1), origin);
        var reach = new float3(radius, radius, radius) + math.abs(roll);
        Lo = -reach; Hi = reach;
        Cell = MathF.Min(Doll.BallCell, radius / 4);
    }

    internal void Cut(PieceCut cut) => _cuts.Add(cut);

    /// <summary>The point of the bind <paramref name="p"/> in the piece's frame.</summary>
    public float3 Local(float3 p) => Placement.Inverse().Apply(p);

    /// <summary>
    /// What the piece's mesh is a pure function of: the shape (a doll entry or a fallback physics shape, or a ball's radius) and its sockets (origin in the body's frame
    /// and ball radius, to 0.1 mm, sorted). Left and right limbs share a key.
    /// </summary>
    public PieceKey Key
    {
        get
        {
            static long Q(float v) => (long)MathF.Round(v * 1e4f);
            static string Rolled(float3 r) => math.lengthsq(r) > 0 ? $",{Q(r.x)},{Q(r.y)},{Q(r.z)}" : "";
            if (IsBall) return new PieceKey($"ball {Q(_radius)}{Rolled(_roll)}");
            var sockets = _cuts.Select(c => $"{Q(c.Origin.x)},{Q(c.Origin.y)},{Q(c.Origin.z)},{Q(c.BallRadius)}{Rolled(c.Roll)}").OrderBy(x => x, StringComparer.Ordinal);
            return new PieceKey($"{_shape}|{string.Join(";", sockets)}");
        }
    }

    private static float SmoothMin(float a, float b, float k)
    {
        float h = math.saturate(0.5f + 0.5f * (b - a) / k);
        return b + (a - b) * h - k * h * (1 - h);
    }

    /// <summary>The field at <paramref name="p"/>, in the piece's frame.</summary>
    public float Value(float3 p)
    {
        if (IsBall) return PieceCut.Core(p, _roll) - _radius;
        float d = Body(p);
        foreach (var c in _cuts) d = MathF.Max(d, c.SocketRadius - c.Off(p));
        return d;
    }

    /// <summary>A body's primitives smooth-min'd, before its sockets are cut.</summary>
    public float Body(float3 p)
    {
        float d = _prims[0].Dist(p);
        for (int i = 1; i < _prims.Length; i++) d = SmoothMin(d, _prims[i].Dist(p), Doll.BodyBlend);
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
    /// half its depth as clearance; a ball's is its centre with half its radius. In the piece's frame.
    /// </summary>
    public (float3 P, float Clear)[] Guard()
    {
        if (IsBall) return new[] { (new float3(0, 0, 0), 0.5f * _radius) };
        var list = new List<(float3, float)>();
        foreach (var pr in _prims)
        {
            var stations = pr.Shape == DollShape.RoundCone ? new[] { 0f, .25f, .5f, .75f, 1f } : new[] { 0f };
            foreach (float s in stations)
            {
                var w = pr.A + (pr.B - pr.A) * s;
                float v = Value(w);
                if (v < -0.01f) list.Add((w, 0.5f * -v));
            }
        }
        return list.ToArray();
    }

    /// <summary>Whether <paramref name="p"/> lies in the seat band: within a socket's radius + <see cref="Doll.SeatBand"/> of a joint origin (a ball has none).</summary>
    public bool Tight(float3 p)
    {
        foreach (var c in _cuts) if (c.Off(p) < c.SocketRadius + Doll.SeatBand) return true;
        return false;
    }

    /// <summary>
    /// Where a decimated surface point may lie, the one owner of the error map: a ball within <see cref="Doll.BallError"/> of its sphere, either way; a body within
    /// <see cref="Doll.SeatError"/> of its field under the tight rule and <see cref="Error"/> otherwise, and never within a ball's radius + 0.2 mm
    /// of its core (the origin, or a roll's segment).
    /// </summary>
    public bool Allowed(float3 p, bool tight)
    {
        if (IsBall) return MathF.Abs(PieceCut.Core(p, _roll) - _radius) <= Doll.BallError;
        if (MathF.Abs(Value(p)) > (tight ? Doll.SeatError : Error)) return false;
        foreach (var c in _cuts) if (c.Off(p) < c.BallRadius + 0.0002f) return false;
        return true;
    }
}

/// <summary>The pieces of a puppet, bodies first then balls (see <see cref="PuppetShape"/>), with what <see cref="PuppetField.Assemble"/> needs to place them.</summary>
public sealed record PieceSet(IReadOnlyList<PuppetBody> Bodies, IReadOnlyList<PuppetJoint> Joints, PieceField[] Fields);

/// <summary>
/// The puppet's surface, engine-free: the ball-jointed doll as rigid pieces (lodur:ruling:puppet-joints-ball-jointed-doll). Every shape body is dressed
/// from <see cref="Doll"/> (or drawn as its physics shape) into its own closed piece, every joint gets a ball piece, and each piece is meshed fine
/// (surface nets) and decimated to <see cref="PieceField.Error"/> or <see cref="Doll.BallError"/> by <see cref="PuppetDecimate"/>. Each vertex has one
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
    /// The pieces' fields, bodies first (piece i is body i) then one ball per joint (piece n + j), each in its own frame: a body from its <see cref="Doll"/> entry
    /// or its physics shape, in the body's frame (placed by its bind); a ball of the child's entry radius (or the child's thickness) about the joint origin, kept by
    /// the parent; at each joint both bodies lose the socket (ball radius + <see cref="Doll.Seam"/>, about the origin or a roll's segment) in their frame, and nothing else.
    /// </summary>
    public static PieceSet Pieces(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints)
    {
        int n = bodies.Count;
        var fields = new PieceField[n + joints.Count];
        for (int i = 0; i < n; i++)
        {
            var entry = Doll.Find(bodies[i].Kind, bodies[i].Size);
            string shape = entry is null ? $"{bodies[i].Kind} {MathF.Round(bodies[i].Size.x * 1e4f)} {MathF.Round(bodies[i].Size.y * 1e4f)} {MathF.Round(bodies[i].Size.z * 1e4f)}" : $"entry {Array.IndexOf(Doll.Entries, entry)}";
            float error = MathF.Min(Doll.FacetError, Doll.ThinError * Thickness(bodies[i].Kind, bodies[i].Size));
            fields[i] = new PieceField(i, i, entry?.Prims ?? Doll.Fallback(bodies[i].Kind, bodies[i].Size), shape, bodies[i].Bind, error, entry?.Cell ?? 0);
        }
        for (int j = 0; j < joints.Count; j++)
        {
            var joint = joints[j];
            var o = bodies[joint.A].Bind.Apply(joint.LocalOrigin);
            var child = Doll.Find(bodies[joint.B].Kind, bodies[joint.B].Size);
            float radius = child is { BallRadius: > 0 } ? child.BallRadius : Thickness(bodies[joint.B].Kind, bodies[joint.B].Size);
            var inChild = child is { RollHalf: > 0 } ? child.RollAxis * child.RollHalf : default; // a roll's half-segment, in the child's frame; then in the bind's axes, then in the parent's frame
            var inBind = math.mul(bodies[joint.B].Bind.R, inChild);
            fields[n + j] = new PieceField(n + j, joint.A, o, radius, inBind);
            fields[joint.A].Cut(new PieceCut(joint.LocalOrigin, radius, math.mul(bodies[joint.A].Bind.Inverse().R, inBind)));
            fields[joint.B].Cut(new PieceCut(bodies[joint.B].Bind.Inverse().Apply(o), radius, inChild));
        }
        return new PieceSet(bodies, joints, fields);
    }

    /// <summary>One piece's mesh: extracted fine and decimated to what <see cref="PieceField.Allowed"/> says, in the piece's frame. A pure function of the piece's key; <see cref="PuppetPieces"/> caches it.</summary>
    internal static (float3[] V, int[] Tris) Mesh(PieceField f)
    {
        var (v, tris) = Extract(f);
        return PuppetDecimate.Run(v, tris, Doll.FacetAlpha, Doll.FacetScale, new DecimateRules(f.Allowed, f.Tight, f.Guard()));
    }

    /// <summary>Builds the shape and waits for it: the pieces' meshes come from <see cref="PuppetPieces"/> (built once per key), so a second build of the same doll builds nothing.</summary>
    public static PuppetShape Build(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints)
    {
        var set = Pieces(bodies, joints);
        var tasks = set.Fields.Select(f => PuppetPieces.Get(f.Key, f)).ToArray(); // all asked for before any is waited on: the keys build side by side
        Task.WaitAll(tasks);
        return Assemble(set, tasks.Select(t => t.Result).ToArray());
    }

    /// <summary>Places the pieces' meshes in the bind and welds them into one shape (no vertex is shared across pieces): a body's vertex is its bind applied, a ball's the joint origin plus it.</summary>
    public static PuppetShape Assemble(PieceSet set, IReadOnlyList<(float3[] V, int[] Tris)> meshes)
    {
        var bodies = set.Bodies; var joints = set.Joints; var pieces = set.Fields;
        int n = bodies.Count;
        var vertices = new List<float3>(); var bone = new List<int>(); var piece = new List<int>();
        var corners = new List<int>();
        for (int i = 0; i < pieces.Length; i++)
        {
            var (v, tris) = meshes[i];
            // The nets wind every quad counter-clockwise seen from the field's outside; Godot's front face is clockwise, so each triangle is emitted reversed.
            int first = vertices.Count;
            foreach (var q in v) vertices.Add(pieces[i].Placement.Apply(q));
            for (int k = 0; k < v.Length; k++) { bone.Add(pieces[i].Bone); piece.Add(i); }
            for (int k = 0; k < tris.Length; k += 3) { corners.Add(first + tris[k]); corners.Add(first + tris[k + 2]); corners.Add(first + tris[k + 1]); }
        }
        var cornerArray = corners.ToArray();

        var ids = new ulong[n]; var bind = new Rigid[n];
        for (int i = 0; i < n; i++) { ids[i] = bodies[i].Id; bind[i] = bodies[i].Bind; }
        var js = new (Box3DSoaJointKind, int, int, float3)[joints.Count]; var radii = new float[joints.Count];
        for (int j = 0; j < js.Length; j++)
        {
            js[j] = (joints[j].Kind, joints[j].A, joints[j].B, pieces[n + j].Placement.T);
            radii[j] = pieces[n + j].BallRadius;
        }
        return new PuppetShape
        {
            Ids = ids, Bind = bind, Joints = js, BallRadius = radii,
            Vertices = vertices.ToArray(), Bone = bone.ToArray(), Piece = piece.ToArray(), Corners = cornerArray, Across = Across(cornerArray),
        };
    }

    /// <summary>One piece on its fine grid: surface nets, two Newton steps onto the isosurface, the nets' quads as triangles, the split of non-manifold vertices and the largest solid. Triangles wound as the nets wind them.</summary>
    private static (float3[] V, int[] Tris) Extract(PieceField f)
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

        var projected = new float3[nets.VertexCount];
        for (int i = 0; i < projected.Length; i++)
        {
            var p = new float3(nets.Positions[3 * i], nets.Positions[3 * i + 1], nets.Positions[3 * i + 2]);
            for (int it = 0; it < Newton; it++) { var g = f.Gradient(p); p -= f.Value(p) * g / MathF.Max(math.lengthsq(g), 1e-6f); }
            projected[i] = p;
        }
        var tris = Triangulate(nets.Quads, projected);
        var v = SplitNonManifold(projected, tris);
        return (v, LargestSolid(tris, v.Length));
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
