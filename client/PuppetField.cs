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

/// <summary>
/// A cut on a body piece at a joint: the socket the ball fills. A roll (<see cref="DollEntry.RollAxis"/>) is the segment Origin +- Roll swept by the ball's radius; no roll is a sphere. <see cref="Axis"/> and
/// <see cref="U"/> are the ball lattice's axis and first side in the piece's frame (<see cref="PuppetField.Primitive"/>), so the cutter's facets face the ball's.
/// </summary>
public sealed record PieceCut(float3 Origin, float BallRadius, float3 Roll = default, float3 Axis = default, float3 U = default)
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

    /// <summary>
    /// The countersunk cutter, in the piece's frame: the convex hull of (a) the ball grown by the seam, in the ball's own lattice orientation, so the socket's facets face the ball's and the clearance is the seam at the
    /// creation pose; (b) the ball's sides on the plane through the core across <paramref name="away"/> (the cutter's vertices within 2% of that plane, 12 even sides if fewer than 8), pushed out to
    /// (BallRadius + Seam) / cos(<see cref="Doll.SinkAngle"/>); (c) that ring carried 50 mm past the core along <paramref name="away"/> (the shadow). A limb end wider than cos(SinkAngle) of the socket then
    /// meets the cutter at no less than about SinkAngle (ruling doll-socket-lip-countersink-35), and nothing reaches past the ball.
    /// </summary>
    public Cutter Cutter(float3 away)
    {
        var (bv, _) = PuppetField.Primitive(SocketRadius, Axis, math.length(Roll), Doll.BallFrequency, U);
        for (int i = 0; i < bv.Length; i++) bv[i] += Origin;
        var w = math.normalize(away); float rho = SocketRadius, rim = rho / MathF.Cos(Doll.SinkAngle), far = rim + 0.05f;
        bool rolled = math.lengthsq(Roll) > 0;
        float3 Core0(float3 q) => rolled ? Roll * math.clamp(math.dot(q, Roll) / math.lengthsq(Roll), -1f, 1f) : default;
        var ring = bv.Where(v => { var q = v - Origin; return MathF.Abs(math.dot(q, w)) < 0.02f * rho && math.length(q - w * math.dot(q, w) - Core0(q)) > 0.5f * rho; }).ToList();
        if (ring.Count < 8)
        {
            var e1 = math.normalize(math.cross(w, MathF.Abs(w.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0))); var e2 = math.cross(w, e1);
            ring = Enumerable.Range(0, 12).Select(k => Origin + (e1 * MathF.Cos(k * MathF.PI / 6) + e2 * MathF.Sin(k * MathF.PI / 6)) * rho).ToList();
        }
        var points = new List<float3>(bv);
        foreach (var v in ring)
        {
            var q = v - Origin; var core = Core0(q);
            var lat = q - core - w * math.dot(q - core, w);
            var p0 = Origin + core + math.normalize(lat) * rim;
            points.Add(p0); points.Add(p0 + w * far);
        }
        var (hv, ht) = PuppetBoolean.Hull(points);
        return new Cutter(hv, ht);
    }
}

/// <summary>A convex cutter: its mesh (counter-clockwise from outside) and its face planes, one per triangle. <see cref="Distance"/> is the largest plane distance, a bound on the distance outside it, negative inside.</summary>
public sealed class Cutter
{
    public float3[] V { get; }
    public int[] Tris { get; }
    private readonly float3[] _n; private readonly float[] _d;

    public Cutter(float3[] v, int[] t)
    {
        V = v; Tris = t; _n = new float3[t.Length / 3]; _d = new float[t.Length / 3];
        for (int k = 0; k < _n.Length; k++) { _n[k] = math.normalize(math.cross(v[t[3 * k + 1]] - v[t[3 * k]], v[t[3 * k + 2]] - v[t[3 * k]])); _d[k] = math.dot(_n[k], v[t[3 * k]]); }
    }

    public float Distance(float3 p)
    {
        float d = float.MinValue;
        for (int k = 0; k < _n.Length; k++) d = MathF.Max(d, math.dot(_n[k], p) - _d[k]);
        return d;
    }
}

/// <summary>A body piece's mesh as <see cref="PuppetPieces"/> keeps it: the solid with its source tags, the decimated piece's centroid (the sockets' away direction), the decimated piece (<c>SourceV</c>, <c>SourceTris</c>: the sources the cut fragments shade as, before the cut, which the checks judge), and how many of the solid's planes are the decimated piece's own (a tag under it is the piece's surface, a tag from it on is a socket wall).</summary>
public sealed record PieceMesh(PuppetSolid Solid, float3 Centre, int Own, float3[] SourceV, int[] SourceTris);

/// <summary>
/// One body piece's field, engine-free, in the body's own frame: its primitives smooth-min'd by <see cref="Doll.BodyBlend"/> (within the body only), which the mesher and decimator sample (<see cref="Body"/>), and
/// the sockets <see cref="Value"/> takes out of it, which are cut from the decimated mesh by <see cref="PuppetBoolean"/>, never sampled. The field is a distance bound, negative inside. <see cref="Placement"/> takes the piece to the bind. A joint's ball is not a field: it is a
/// <see cref="JointBall"/>, built as a primitive (ruling doll-joints-built-primitives).
/// </summary>
public sealed class PieceField
{
    private readonly DollPrim[] _prims = Array.Empty<DollPrim>();
    private readonly string _shape;
    private readonly List<PieceCut> _cuts = new();

    /// <summary>Which piece this is (as <see cref="PuppetShape.Piece"/>) and which body it follows.</summary>
    public int Index { get; }
    public int Bone { get; }

    /// <summary>The piece's frame in the bind: the body's bind.</summary>
    public Rigid Placement { get; }

    /// <summary>The corners of a box around the piece, in its frame.</summary>
    public float3 Lo { get; }
    public float3 Hi { get; }
    public IReadOnlyList<PieceCut> Cuts => _cuts;

    /// <summary>The grid (m) the piece is extracted on before decimation.</summary>
    public float Cell { get; }

    /// <summary>How far (m) a body piece's surface may stray from its field away from the sockets: <see cref="Doll.FacetError"/>, less for a thin body (<see cref="Doll.ThinError"/>).</summary>
    public float Error { get; }

    internal PieceField(int index, int bone, DollPrim[] prims, string shape, Rigid bind, float error)
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
        Cell = MathF.Min(Doll.BodyCell, thinnest / 2);
    }

    internal void Socket(PieceCut cut) => _cuts.Add(cut);

    /// <summary>The point of the bind <paramref name="p"/> in the piece's frame.</summary>
    public float3 Local(float3 p) => Placement.Inverse().Apply(p);

    /// <summary>
    /// What the piece's mesh is a pure function of: the shape (a doll entry or a fallback physics shape) and its sockets (origin in the body's frame
    /// and ball radius, to 0.1 mm, and the ball lattice's axis and first side, sorted). Left and right limbs share a key unless their lattice orientation differs.
    /// </summary>
    public PieceKey Key
    {
        get
        {
            static long Q(float v) => (long)MathF.Round(v * 1e4f);
            static string Rolled(float3 r) => math.lengthsq(r) > 0 ? $",{Q(r.x)},{Q(r.y)},{Q(r.z)}" : "";
            var sockets = _cuts.Select(c => $"{Q(c.Origin.x)},{Q(c.Origin.y)},{Q(c.Origin.z)},{Q(c.BallRadius)}{Rolled(c.Roll)},{Q(c.Axis.x)},{Q(c.Axis.y)},{Q(c.Axis.z)},{Q(c.U.x)},{Q(c.U.y)},{Q(c.U.z)}").OrderBy(x => x, StringComparer.Ordinal);
            return new PieceKey($"{_shape}|{string.Join(";", sockets)}");
        }
    }

    private static float SmoothMin(float a, float b, float k)
    {
        float h = math.saturate(0.5f + 0.5f * (b - a) / k);
        return b + (a - b) * h - k * h * (1 - h);
    }

    /// <summary>
    /// The piece's cutters, in its frame: each socket's countersunk cutter, its away direction from the decimated piece's centroid to the core. Needs the piece built (the centroid is part of its
    /// <see cref="PieceMesh"/>), so it is for the checks; the mesher builds its own cutters from the centroid it has.
    /// </summary>
    public Cutter[] Cutters => _cutters ??= CuttersOf(PuppetPieces.Get(Key, this).Result);

    private Cutter[]? _cutters;

    private Cutter[] CuttersOf(PieceMesh m) => CuttersAt(m.Centre);

    internal Cutter[] CuttersAt(float3 centre) => _cuts.Select(c => c.Cutter(c.Origin - centre)).ToArray();

    /// <summary>The cut: the least over the cutters of the largest plane distance, a bound on the distance outside every cutter, negative inside one.</summary>
    public float Cut(float3 p)
    {
        float d = float.MaxValue;
        foreach (var c in Cutters) d = MathF.Min(d, c.Distance(p));
        return d;
    }

    /// <summary>The solid the checks judge at <paramref name="p"/>, in the piece's frame: <see cref="Body"/> minus the cutters.</summary>
    public float Value(float3 p) => _cuts.Count == 0 ? Body(p) : MathF.Max(Body(p), -Cut(p));

    /// <summary>A body's primitives smooth-min'd, before its sockets are cut: all the mesher and the decimator sample.</summary>
    public float Body(float3 p)
    {
        float d = _prims[0].Dist(p);
        for (int i = 1; i < _prims.Length; i++) d = SmoothMin(d, _prims[i].Dist(p), Doll.BodyBlend);
        return d;
    }

    /// <summary>The body field's gradient by central differences.</summary>
    public float3 Gradient(float3 p)
    {
        const float e = 0.0005f;
        return new float3(Body(p + new float3(e, 0, 0)) - Body(p - new float3(e, 0, 0)), Body(p + new float3(0, e, 0)) - Body(p - new float3(0, e, 0)), Body(p + new float3(0, 0, e)) - Body(p - new float3(0, 0, e))) / (2 * e);
    }

    /// <summary>
    /// The limb guard's skeleton: the primitives' axis points (a round cone's at five stations, the others' centre) that lie inside the piece, each with
    /// half its depth as clearance. In the piece's frame.
    /// </summary>
    public (float3 P, float Clear)[] Guard()
    {
        var list = new List<(float3, float)>();
        foreach (var pr in _prims)
        {
            var stations = pr.Shape == DollShape.RoundCone ? new[] { 0f, .25f, .5f, .75f, 1f } : new[] { 0f };
            foreach (float s in stations)
            {
                var w = pr.A + (pr.B - pr.A) * s;
                float v = Body(w);
                if (v < -0.01f) list.Add((w, 0.5f * -v));
            }
        }
        return list.ToArray();
    }

    /// <summary>Whether <paramref name="p"/> lies in the seat band: within a socket's radius + <see cref="Doll.SeatBand"/> of a joint origin.</summary>
    public bool Tight(float3 p)
    {
        foreach (var c in _cuts) if (c.Off(p) < c.SocketRadius + Doll.SeatBand) return true;
        return false;
    }

    /// <summary>
    /// How far (m) a surface point may stray from the field in the seat band: <see cref="Doll.SeatError"/>, and for each socket whose band holds the point at most
    /// <see cref="Doll.SeatShare"/> of that socket's ball radius, so a small ball is seated as closely as it is round.
    /// </summary>
    public float SeatLimit(float3 p)
    {
        float limit = Doll.SeatError;
        foreach (var c in _cuts) if (c.Off(p) < c.SocketRadius + Doll.SeatBand) limit = MathF.Min(limit, Doll.SeatShare * c.BallRadius);
        return limit;
    }

    /// <summary>How far (m) the field's surface may lie from the mesh at <paramref name="p"/> (the decimator's carried surface points): <see cref="Error"/>, or in the seat band at most <see cref="SeatLimit"/> too.</summary>
    public float Stray(float3 p) => Tight(p) ? MathF.Min(Error, SeatLimit(p)) : Error;

    /// <summary>
    /// Where a decimated surface point may lie, the one owner of the error map: within <see cref="Error"/> of the body field, and under the tight rule within <see cref="SeatLimit"/> too. The sockets
    /// are not here: they are cut after decimation.
    /// </summary>
    public bool Allowed(float3 p, bool tight) => MathF.Abs(Body(p)) <= (tight ? MathF.Min(Error, SeatLimit(p)) : Error);
}

/// <summary>
/// A joint's ball (ruling doll-joints-built-primitives): a sphere, or a roll (the segment Origin +- Roll swept by the radius), built directly by <see cref="PuppetField.Primitive"/> and
/// never meshed from a field or decimated. A pure function of (Radius, Roll, Axis, <see cref="Doll.BallFrequency"/>). A sphere's <see cref="Axis"/> is its child's bind y, the limb's long axis, so the
/// sides round the axis face the seam band; a roll's is the roll's direction. Kept by the joint's parent (<see cref="Bone"/>); <see cref="Placement"/> is the translation to the joint origin.
/// </summary>
public sealed record JointBall(int Index, int Bone, float3 Origin, float Radius, float3 Roll, float3 Axis)
{
    public Rigid Placement => new(new float3x3(1, 0, 0, 0, 1, 0, 0, 0, 1), Origin);

    /// <summary>The ball's mesh in its own frame, wound as the nets wind.</summary>
    public (float3[] V, int[] Tris) Mesh() => PuppetField.Primitive(Radius, Axis, math.length(Roll), Doll.BallFrequency);
}

/// <summary>The pieces of a puppet: the body fields (piece i is body i) and the joint balls (piece n + j is joint j's), with what <see cref="PuppetField.Assemble"/> needs to place them.</summary>
public sealed record PieceSet(IReadOnlyList<PuppetBody> Bodies, IReadOnlyList<PuppetJoint> Joints, PieceField[] Fields, JointBall[] Balls);

/// <summary>
/// The puppet's surface, engine-free: the ball-jointed doll as rigid pieces (lodur:ruling:puppet-joints-ball-jointed-doll). Every shape body is dressed
/// from <see cref="Doll"/> (or drawn as its physics shape) into its own closed piece, every joint gets a ball piece. A body is meshed fine (surface nets) and decimated to <see cref="PieceField.Error"/> by
/// <see cref="PuppetDecimate"/>; a ball is built as a primitive. Each vertex has one bone; nothing blends across a joint.
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
    /// The pieces: a field per body (piece i is body i) from its <see cref="Doll"/> entry or its physics shape, in the body's frame (placed by its bind), and one
    /// <see cref="JointBall"/> per joint (piece n + j) of the child's entry radius (or the child's thickness) about the joint origin, kept by the parent; at each joint both bodies get the socket (ball radius + <see cref="Doll.Seam"/>, about the origin or a roll's segment, in the ball's lattice orientation) in their frame, and nothing else.
    /// </summary>
    public static PieceSet Pieces(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints)
    {
        int n = bodies.Count;
        var fields = new PieceField[n]; var balls = new JointBall[joints.Count];
        for (int i = 0; i < n; i++)
        {
            var entry = Doll.Find(bodies[i].Kind, bodies[i].Size);
            string shape = entry is null ? $"{bodies[i].Kind} {MathF.Round(bodies[i].Size.x * 1e4f)} {MathF.Round(bodies[i].Size.y * 1e4f)} {MathF.Round(bodies[i].Size.z * 1e4f)}" : $"entry {Array.IndexOf(Doll.Entries, entry)}";
            float error = Doll.BodyError(Thickness(bodies[i].Kind, bodies[i].Size));
            fields[i] = new PieceField(i, i, entry?.Prims ?? Doll.Fallback(bodies[i].Kind, bodies[i].Size), shape, bodies[i].Bind, error);
        }
        for (int j = 0; j < joints.Count; j++)
        {
            var joint = joints[j];
            var o = bodies[joint.A].Bind.Apply(joint.LocalOrigin);
            var child = Doll.Find(bodies[joint.B].Kind, bodies[joint.B].Size);
            float radius = child is { BallRadius: > 0 } ? child.BallRadius : Thickness(bodies[joint.B].Kind, bodies[joint.B].Size);
            var inChild = child is { RollHalf: > 0 } ? child.RollAxis * child.RollHalf : default; // a roll's half-segment, in the child's frame; then in the bind's axes, then in the parent's frame
            var inBind = math.mul(bodies[joint.B].Bind.R, inChild);
            balls[j] = new JointBall(n + j, joint.A, o, radius, inBind, math.lengthsq(inBind) > 0 ? math.normalize(inBind) : math.mul(bodies[joint.B].Bind.R, new float3(0, 1, 0)));
            var axis = balls[j].Axis; var side = Unit(math.cross(axis, MathF.Abs(axis.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0))); // the ball lattice's axis and first side (Lattice picks it), in the bind's axes
            var toA = bodies[joint.A].Bind.Inverse().R; var toB = bodies[joint.B].Bind.Inverse().R;
            fields[joint.A].Socket(new PieceCut(joint.LocalOrigin, radius, math.mul(toA, inBind), math.mul(toA, axis), math.mul(toA, side)));
            fields[joint.B].Socket(new PieceCut(bodies[joint.B].Bind.Inverse().Apply(o), radius, inChild, math.mul(toB, axis), math.mul(toB, side)));
        }
        return new PieceSet(bodies, joints, fields, balls);
    }

    /// <summary>The boolean's faults (an open result, a loop that did not close), by piece key: what the checks and the smoke report; empty when every cut was clean.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Faults = new();

    /// <summary>
    /// One body piece's mesh in its frame, a pure function of its key (<see cref="PuppetPieces"/> caches it): the body field extracted fine and decimated to what <see cref="PieceField.Allowed"/> says, then each
    /// socket's cutter (<see cref="PieceCut.Cutter"/>, away from the decimated piece's centroid) subtracted from it by <see cref="PuppetBoolean.Subtract"/>, tidied and reduced to its largest solid. Every
    /// triangle carries its source (<see cref="PuppetSolid"/>).
    /// </summary>
    internal static PieceMesh Mesh(PieceField f)
    {
        var (v, tris) = Extract(f);
        var (dv, dt) = PuppetDecimate.Run(v, tris, Doll.FacetAlpha, Doll.FacetScale, new DecimateRules(f.Allowed, f.Tight, f.Stray, f.Guard()));
        var centre = Centroid(dv, dt);
        var solid = PuppetBoolean.Solid(dv, dt); var stats = new PuppetBoolean.Stats();
        foreach (var cutter in f.CuttersAt(centre)) solid = PuppetBoolean.Subtract(solid, cutter.V, cutter.Tris, stats);
        if (f.Cuts.Count > 0) solid = PuppetBoolean.Tidy(solid, Doll.TidyEdge, Doll.TidySliverEdge, stats);
        solid = PuppetBoolean.LargestSolid(solid).Solid;
        if (stats.Faults.Count > 0) Faults[f.Key.Id] = string.Join("; ", stats.Faults);
        return new PieceMesh(solid, centre, dt.Length / 3, dv, dt);
    }

    /// <summary>The centroid of a closed mesh (divergence theorem).</summary>
    private static float3 Centroid(float3[] v, int[] t)
    {
        double vol = 0, x = 0, y = 0, z = 0;
        for (int k = 0; k < t.Length; k += 3)
        {
            var a = v[t[k]]; var b = v[t[k + 1]]; var c = v[t[k + 2]];
            double d = math.dot(a, math.cross(b, c)) / 6.0; vol += d; var m = (a + b + c) / 4f; x += d * m.x; y += d * m.y; z += d * m.z;
        }
        return new float3((float)(x / vol), (float)(y / vol), (float)(z / vol));
    }

    /// <summary>Builds the shape and waits for it: the bodies' meshes come from <see cref="PuppetPieces"/> (built once per key), so a second build of the same doll builds nothing; the balls are built inline.</summary>
    public static PuppetShape Build(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJoint> joints)
    {
        var set = Pieces(bodies, joints);
        var tasks = set.Fields.Select(f => PuppetPieces.Get(f.Key, f)).ToArray(); // all asked for before any is waited on: the keys build side by side
        Task.WaitAll(tasks);
        return Assemble(set, tasks.Select(t => t.Result.Solid).ToArray());
    }

    /// <summary>
    /// Places the pieces' meshes in the bind and welds them into one shape (no vertex is shared across pieces): <paramref name="bodyMeshes"/> are the body fields' (piece i), and each
    /// <see cref="JointBall"/> builds its own (piece n + j); a body's vertex is its bind applied, a ball's the joint origin plus it.
    /// </summary>
    public static PuppetShape Assemble(PieceSet set, IReadOnlyList<PuppetSolid> bodyMeshes)
    {
        var bodies = set.Bodies; var joints = set.Joints; var balls = set.Balls;
        int n = bodies.Count;
        var vertices = new List<float3>(); var bone = new List<int>(); var piece = new List<int>();
        var corners = new List<int>();
        void Place(int index, (float3[] V, int[] Tris) mesh, Rigid placement, int bodyBone)
        {
            // The meshes wind every triangle counter-clockwise seen from outside; Godot's front face is clockwise, so each triangle is emitted reversed.
            var (v, tris) = mesh;
            int first = vertices.Count;
            foreach (var q in v) vertices.Add(placement.Apply(q));
            for (int k = 0; k < v.Length; k++) { bone.Add(bodyBone); piece.Add(index); }
            for (int k = 0; k < tris.Length; k += 3) { corners.Add(first + tris[k]); corners.Add(first + tris[k + 2]); corners.Add(first + tris[k + 1]); }
        }
        for (int i = 0; i < n; i++) Place(i, (bodyMeshes[i].V, bodyMeshes[i].Tris), set.Fields[i].Placement, set.Fields[i].Bone);
        foreach (var ball in balls) Place(ball.Index, ball.Mesh(), ball.Placement, ball.Bone);
        var cornerArray = corners.ToArray();

        var ids = new ulong[n]; var bind = new Rigid[n];
        for (int i = 0; i < n; i++) { ids[i] = bodies[i].Id; bind[i] = bodies[i].Bind; }
        var js = new (Box3DSoaJointKind, int, int, float3)[joints.Count]; var radii = new float[joints.Count];
        for (int j = 0; j < js.Length; j++)
        {
            js[j] = (joints[j].Kind, joints[j].A, joints[j].B, balls[j].Origin);
            radii[j] = balls[j].Radius;
        }
        return new PuppetShape
        {
            Ids = ids, Bind = bind, Joints = js, BallRadius = radii,
            Vertices = vertices.ToArray(), Bone = bone.ToArray(), Piece = piece.ToArray(), Corners = cornerArray, Across = Across(cornerArray),
        };
    }

    /// <summary>One piece on its fine grid: surface nets, two Newton steps onto the isosurface, the nets' quads as triangles, the split of non-manifold vertices. Of the body field alone: the sockets are cut after decimation. Triangles wound as the nets wind them.</summary>
    internal static (float3[] V, int[] Tris) Extract(PieceField f)
    {
        float cell = f.Cell, pad = 2 * cell + 0.04f;
        var lo = f.Lo - pad; var hi = f.Hi + pad;
        int nx = (int)MathF.Ceiling((hi.x - lo.x) / cell) + 1, ny = (int)MathF.Ceiling((hi.y - lo.y) / cell) + 1, nz = (int)MathF.Ceiling((hi.z - lo.z) / cell) + 1;
        var samples = new float[nx, ny, nz];
        Parallel.For(0, nx, x =>
        {
            for (int y = 0; y < ny; y++) for (int z = 0; z < nz; z++) samples[x, y, z] = f.Body(lo + new float3(x, y, z) * cell);
        });
        var nets = CultGeometrySurfaceNets.Extract(samples, 0f, lo, cell);

        var projected = new float3[nets.VertexCount];
        for (int i = 0; i < projected.Length; i++)
        {
            var p = new float3(nets.Positions[3 * i], nets.Positions[3 * i + 1], nets.Positions[3 * i + 2]);
            for (int it = 0; it < Newton; it++) { var g = f.Gradient(p); p -= f.Body(p) * g / MathF.Max(math.lengthsq(g), 1e-6f); }
            projected[i] = p;
        }
        var tris = Triangulate(nets.Quads, projected);
        var v = SplitNonManifold(projected, tris);
        return (v, tris);
    }

    /// <summary>
    /// Duplicates every vertex whose incident triangles fall into more than one fan (fans joined only across edges exactly two triangles share): two
    /// sheets of the surface that touch at a point or along an edge become two sheets that meet without sharing a vertex. Rewrites <paramref name="tris"/> in place.
    /// </summary>
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

    /// <summary>
    /// A joint ball built directly (ruling doll-joints-built-primitives): the octahedron's lattice of <paramref name="frequency"/> (|a| + |b| + |c| = frequency) normalised onto the sphere, 4 x frequency sides
    /// round <paramref name="axis"/>; with <paramref name="half"/> above zero the two hemispheres are pushed +- half apart along the axis with a ring of quads between (a roll). The vertices are scaled out by
    /// 2 / (1 + k), k the unit sphere's least face-centroid radius, so the facets straddle the sphere (the vertices as far outside it as the face centres are in), and the ball fills its socket as
    /// a sphere of that radius would. In the ball's frame about the core, its first side <paramref name="u"/> (by default the one the axis picks); wound counter-clockwise from outside, as the nets wind.
    /// </summary>
    internal static (float3[] V, int[] Tris) Primitive(float radius, float3 axis, float half, int frequency, float3 u = default)
    {
        var (uv, ut) = Lattice(1f, axis, 0f, frequency, u);
        float k = float.MaxValue;
        for (int t = 0; t < ut.Length; t += 3) k = MathF.Min(k, math.length((uv[ut[t]] + uv[ut[t + 1]] + uv[ut[t + 2]]) / 3f));
        return Lattice(radius * 2f / (1f + k), axis, half, frequency, u);
    }

    /// <summary>The lattice about <paramref name="axis"/>; <paramref name="given"/> is its first side (unit, across the axis), by default the one the axis picks: a socket cut takes the ball's, so they share one lattice.</summary>
    private static (float3[] V, int[] Tris) Lattice(float radius, float3 axis, float half, int f, float3 given = default)
    {
        var u = math.lengthsq(given) > 0 ? given : Unit(math.cross(axis, MathF.Abs(axis.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0))); var w = math.cross(axis, u);
        var index = new Dictionary<(int, int, int, int), int>(); var v = new List<float3>();
        int Vert(int a, int b, int c, int side)
        {
            var key = (a, b, c, c == 0 && half > 0 ? side : 0); // the equator is one ring on a sphere and two on a roll
            if (index.TryGetValue(key, out int i)) return i;
            var d = Unit(new float3(a, b, c));
            v.Add(u * (d.x * radius) + w * (d.y * radius) + axis * (d.z * radius + (c > 0 ? half : c < 0 ? -half : side * half)));
            index[key] = v.Count - 1; return v.Count - 1;
        }
        var tris = new List<int>();
        void Tri(int a, int b, int c)
        {
            var m = (v[a] + v[b] + v[c]) / 3f;
            var outward = m - axis * math.clamp(math.dot(m, axis), -half, half);
            bool ccw = math.dot(math.cross(v[b] - v[a], v[c] - v[a]), outward) >= 0;
            tris.Add(a); tris.Add(ccw ? b : c); tris.Add(ccw ? c : b);
        }
        foreach (int sx in new[] { 1, -1 }) foreach (int sy in new[] { 1, -1 }) foreach (int sz in new[] { 1, -1 })
        {
            int P(int a, int b) => Vert(sx * a, sy * b, sz * (f - a - b), sz);
            for (int a = 0; a < f; a++) for (int b = 0; a + b < f; b++)
            {
                Tri(P(a, b), P(a + 1, b), P(a, b + 1));
                if (a + b < f - 1) Tri(P(a + 1, b), P(a + 1, b + 1), P(a, b + 1));
            }
        }
        if (half > 0)
            for (int k = 0; k < 4 * f; k++) // the equator |a| + |b| = f, one quad per step
            {
                (int a0, int b0) = Equator(k, f); (int a1, int b1) = Equator(k + 1, f);
                int t0 = Vert(a0, b0, 0, 1), t1 = Vert(a1, b1, 0, 1), d0 = Vert(a0, b0, 0, -1), d1 = Vert(a1, b1, 0, -1);
                Tri(t0, d0, t1); Tri(t1, d0, d1);
            }
        return (v.ToArray(), tris.ToArray());
    }

    private static (int A, int B) Equator(int k, int f)
    {
        int q = k / f % 4, r = k % f;
        return q switch { 0 => (f - r, r), 1 => (-r, f - r), 2 => (-(f - r), -r), _ => (r, -(f - r)) };
    }

    private static float3 Unit(float3 v)
    {
        float l = math.length(v);
        return l > 1e-12f ? v / l : new float3(0, 0, 0);
    }
}
