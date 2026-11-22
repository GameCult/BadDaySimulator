using System;
using CultMath;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>The four signed-distance primitives the doll is dressed in (iq's, exact except the ellipsoid, which is his bound), in a body's local frame.</summary>
public enum DollShape { Sphere, Ellipsoid, RoundCone, RoundBox }

/// <summary>One primitive of a dressed body: a sphere (A, Ra), an ellipsoid (A, Half), a round cone from A to B (Ra, Rb) or a round box (A, Half, Round).</summary>
public sealed record DollPrim(DollShape Shape, float3 A, float3 B, float Ra, float Rb, float3 Half, float Round)
{
    public static DollPrim Sphere(float3 c, float r) => new(DollShape.Sphere, c, c, r, r, default, 0);
    public static DollPrim Ellipsoid(float3 c, float3 radii) => new(DollShape.Ellipsoid, c, c, 0, 0, radii, 0);
    public static DollPrim Cone(float3 a, float ra, float3 b, float rb) => new(DollShape.RoundCone, a, b, ra, rb, default, 0);
    public static DollPrim Box(float3 c, float3 half, float round) => new(DollShape.RoundBox, c, c, 0, 0, half, round);

    /// <summary>The signed distance (an exact one but for the ellipsoid) at <paramref name="q"/>, in the body's frame.</summary>
    public float Dist(float3 q)
    {
        switch (Shape)
        {
            case DollShape.Sphere: return math.length(q - A) - Ra;
            case DollShape.Ellipsoid:
            {
                var p = q - A;
                float k0 = math.length(p / Half), k1 = math.length(p / (Half * Half));
                return k1 > 1e-9f ? k0 * (k0 - 1) / k1 : -math.min(Half.x, math.min(Half.y, Half.z));
            }
            case DollShape.RoundCone: return RoundCone(q, A, B, Ra, Rb);
            default:
            {
                var p = math.abs(q - A) - (Half - Round);
                return math.length(math.max(p, 0f)) + math.min(math.max(p.x, math.max(p.y, p.z)), 0f) - Round;
            }
        }
    }

    private static float RoundCone(float3 p, float3 a, float3 b, float r1, float r2)
    {
        var ba = b - a; float l2 = math.dot(ba, ba), rr = r1 - r2, a2 = l2 - rr * rr, il2 = 1f / l2;
        var pa = p - a; float y = math.dot(pa, ba), z = y - l2;
        var xv = pa * l2 - ba * y; float x2 = math.dot(xv, xv), y2 = y * y * l2, z2 = z * z * l2;
        float k = MathF.Sign(rr) * rr * rr * x2;
        if (MathF.Sign(z) * a2 * z2 > k) return MathF.Sqrt(x2 + z2) * il2 - r2;
        if (MathF.Sign(y) * a2 * y2 < k) return MathF.Sqrt(x2 + y2) * il2 - r1;
        return (MathF.Sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
    }

    /// <summary>A bounding sphere, in the body's frame.</summary>
    public (float3 Centre, float Radius) Bound() => Shape switch
    {
        DollShape.Sphere => (A, Ra),
        DollShape.Ellipsoid => (A, math.max(Half.x, math.max(Half.y, Half.z))),
        DollShape.RoundCone => ((A + B) * 0.5f, 0.5f * math.length(B - A) + MathF.Max(Ra, Rb)),
        _ => (A, math.length(Half)),
    };

    /// <summary>The primitive's smallest radius: how thin a part it draws.</summary>
    public float Thickness => Shape switch
    {
        DollShape.Sphere => Ra,
        DollShape.Ellipsoid => math.min(Half.x, math.min(Half.y, Half.z)),
        DollShape.RoundCone => MathF.Min(Ra, Rb),
        _ => math.min(Half.x, math.min(Half.y, Half.z)),
    };
}

/// <summary>
/// One body of the doll: the physics shape it dresses (the key), the primitives it is made of, and the ball at its joint to its parent. The limb's
/// half-width at its socket end is under its ball's radius, as the look and not as a guard: a limb end of any width is cut by its socket's countersunk cutter (thigh at 78 mm over its 70 mm ball and a palm 34 mm across over the 26 mm wrist
/// ball: slits within the slit check's bound, no knife rim added). A lip, where a limb end is nearly as wide as its socket, is blunted by the countersink (<see cref="Doll.SinkAngle"/>), not by the entry. Joint ranges are not doll data: the lanes carry no joint limits (follow-up lodur-joint-ranges-anatomical).
/// </summary>
/// <param name="Kind">The physics shape's kind.</param>
/// <param name="Size">The physics shape's Size0..2 (matched within 1e-4).</param>
/// <param name="Prims">The body's primitives in its own frame, smooth-min'd by <see cref="Doll.BodyBlend"/>.</param>
/// <param name="BallRadius">The radius (m) of the ball at the joint to the parent, or 0 for a body with no joint to a parent of its own.</param>
/// <param name="RollAxis">A hinge ball stretched along the hinge axis into a roll (ruling doll-knuckle-roll), as a unit vector in this body's own frame, or zero for a sphere.</param>
/// <param name="RollHalf">Half the roll's length (m) between its two end centres; the ball is the segment of this half-length about the joint origin, swept by <paramref name="BallRadius"/>.</param>
/// <param name="Line">The line out of this body through its socket at its parent joint, in its own frame (<see cref="Doll.Away"/>); zero: the line from the body's centre through the joint origin.</param>
/// <param name="ParentLine">The parent's line out through this body's socket, in the parent's frame, for a socket at x &gt;= 0 (x is negated for a socket at x &lt; 0); zero: the line from the parent's centre through the joint origin.</param>
public sealed record DollEntry(Box3DSoaKind Kind, float3 Size, DollPrim[] Prims, float BallRadius = 0, float3 RollAxis = default, float RollHalf = 0, float3 Line = default, float3 ParentLine = default)
{
    public bool Matches(Box3DSoaKind kind, float3 size) => kind == Kind && math.length(size - Size) <= 1e-4f;
}

/// <summary>
/// The ball-jointed art doll (lodur:ruling:puppet-joints-ball-jointed-doll, puppet-look-artdoll-alpha-half) as data, engine-free: the constants that
/// decide the pieces and the entries that dress the humanoid ragdoll's bodies, keyed by physics shape and not by limb name. The limb primitives are
/// x-symmetric, so one entry dresses both sides.
/// </summary>
public static class Doll
{
    /// <summary>The smooth-min radius (m) between the primitives of one body.</summary>
    public const float BodyBlend = 0.012f;

    /// <summary>The gap (m) a socket leaves around its ball: the visible seam.</summary>
    public const float Seam = 0.002f;

    /// <summary>The decimator's alpha: 0 is plane distance alone, 1 normal deviation alone (ruling puppet-look-artdoll-alpha-half: 0.5).</summary>
    public const float FacetAlpha = 0.5f;

    /// <summary>How far (m) a body piece's surface may stray from its own field when the decimator collapses an edge, away from the sockets.</summary>
    public const float FacetError = 0.02f;

    /// <summary>
    /// A thin body piece's facet error is at most this fraction of its thickness (<see cref="PuppetField.Thickness"/>): below <see cref="ThinBelow"/> a part cannot afford <see cref="FacetError"/> (at 20 mm
    /// the probe's fingers, 11 mm thick, decimated to 2 triangles and the palm to 8). Every body of 40 mm or more keeps <see cref="FacetError"/>, so its mesh does not change. Outside the seat bands
    /// it decides the thin bodies' free ends; off, the fingers' tip departs 5.83 mm from its silhouette against 2.06 shipped (ThinPiecesKeepTheirSilhouette).
    /// </summary>
    public const float ThinError = 0.25f;

    /// <summary>The thickness (m) under which a body is thin: only a body thinner than this has its error cut to <see cref="ThinError"/> of its thickness (<see cref="BodyError"/>); a body of 40 mm or more keeps <see cref="FacetError"/> and its mesh (ruling puppet-look-artdoll-alpha-half).</summary>
    public const float ThinBelow = 0.04f;

    /// <summary>A body piece's facet error (m) away from the sockets: <see cref="FacetError"/>, or for a thin body (under <see cref="ThinBelow"/>) <see cref="ThinError"/> of its thickness where that is less.</summary>
    public static float BodyError(float thickness) => thickness < ThinBelow ? MathF.Min(FacetError, ThinError * thickness) : FacetError;

    /// <summary>
    /// The countersink of a socket's cutter (ruling doll-socket-lip-countersink-35): its ring across the away direction stands (BallRadius + Seam) / cos(SinkAngle) from the core, so a limb end wider than
    /// cos(SinkAngle) of the socket meets the cutter at no less than about this angle. Measured on the doll (prototype, rim length under 30 degrees at facet level): 0 degrees (a plain ball plus shadow) 1570 mm of
    /// 9238, 30 degrees 486, 35 degrees 111 (202 at the checks' level), at the cost of about 1.8 x the visible facets (the 12-sided chamfer ring at wide joints); the operator took 35.
    /// </summary>
    public const float SinkAngle = 35f * (MathF.PI / 180f);

    /// <summary>
    /// A socket's away line (unit, in the piece's own frame; ruling doll-socket-away-authored): the line out of the piece through its socket, from the doll's authored joint geometry and never from the decimated mesh. It is
    /// <paramref name="authored"/> (an entry's <see cref="DollEntry.Line"/> or <see cref="DollEntry.ParentLine"/>, x negated for a socket at x &lt; 0), else the line from the piece's centre through the joint
    /// <paramref name="origin"/>; for a roll, with its component along the roll removed. A zero line throws: author one.
    /// </summary>
    public static float3 Away(float3 authored, float3 origin, float3 roll)
    {
        if (authored.x != 0 && MathF.Abs(origin.x) < 0.001f) throw new InvalidOperationException("an authored line with an x component needs a socket off the midline to say its side");
        var a = math.lengthsq(authored) == 0 ? origin : origin.x < 0 ? new float3(-authored.x, authored.y, authored.z) : authored;
        if (math.lengthsq(roll) > 0) a -= math.normalize(roll) * math.dot(a, math.normalize(roll)); // across the roll
        if (math.lengthsq(a) < 1e-10f) throw new InvalidOperationException("a socket has no away line: author one on its entry");
        return math.normalize(a);
    }

    /// <summary>The edge (m) under which the boolean's tidy collapses an edge (a curve's near-coincident points), and the edge under which it collapses the shortest edge of a sliver (a triangle under 0.01 mm2).</summary>
    public const float TidyEdge = 0.0005f, TidySliverEdge = 0.002f;

    /// <summary>
    /// The seat band (m) beyond a socket's radius: a collapse that touches a vertex within it runs under <see cref="SeatError"/>. A field-value error is blind to a
    /// socket's lip, which is thinner than any tolerance, so the band judges every check point of every collapse near a socket (ruling doll-pieces-hug-balls).
    /// </summary>
    public const float SeatBand = 0.015f;

    /// <summary>How far (m) a body piece's surface may stray from its own field, two-sided, inside the seat band: the limb's end sits on its ball with only the seam showing.</summary>
    public const float SeatError = 0.005f;

    /// <summary>
    /// The share of a ball's radius a seat may stray by, where that is under <see cref="SeatError"/> (<see cref="PieceField.SeatLimit"/>): the wrist's 26 mm ball seats within 3.9 mm, the knuckle roll's 15 mm within 2.25
    /// and the CMC's 14 mm within 2.1; every body ball is 34 mm or more, where 0.15 x 34 = 5.1 is over <see cref="SeatError"/>, so no body piece's mesh changes. Since the sockets are cut after decimation it sets the hands' density near
    /// their sockets, not their seat: off reads the hands 680 triangles against 728 and the wrist slit 7.06 against 7.35; 0.08 changes every body (0.08 x 34 is under SeatError), 5006 triangles.
    /// </summary>
    public const float SeatShare = 0.15f;

    /// <summary>
    /// The octahedral lattice's frequency every joint ball and roll is built at (<see cref="PuppetField.Primitive"/>): 4 x 3 = 12 sides round the axis, 72 triangles and 38 vertices a ball, 96 and 50 a roll, the facets
    /// straddling the sphere by +-3.9% of its radius (2.0 mm at 50, 0.6 mm at 15). Frequency 2 (8 sides, 32 triangles, +-10%) reads as a cut gem at the shoulder; 4 (128) shows nothing more at play distance. One value, so
    /// every ball has the same angular look at every scale.
    /// </summary>
    public const int BallFrequency = 3;

    /// <summary>The radius (m) a fallback box is rounded by (a body with no entry is drawn as its physics shape).</summary>
    public const float FallbackRound = 0.03f;

    /// <summary>The fine grid (m) a body is extracted on before decimation: at the probe's 12 mm the decimator reaches the error with the doll's thinnest parts (30 mm) resolved.</summary>
    public const float BodyCell = 0.012f;

    /// <summary>The decimator's normal-deviation weight k (m^2): the plane distance is in m^4 and the deviation in m^2, so k = (16 mm)^2 sets them on one scale.</summary>
    public const float FacetScale = 0.016f * 0.016f;

    private static float3 V(float x, float y, float z) => new(x, y, z);

    /// <summary>The art doll's bodies. Torso: the shoulder bar runs along the shoulder joints (y 0.14) to inside the shoulder balls (x = +-0.205: its end then lies within the socket, where at y 0.15 and x 0.20 it left a crescent a few millimetres thick outside it), the neck cone stops at the neck ball (y 0.17 to 0.20) and the waist cone at the spine ball (to y -0.15, not -0.19: there it left a lip 1 to 4 mm thick outside the socket, below the grid, that no mesher keeps manifold).</summary>
    public static readonly DollEntry[] Entries =
    {
        new(Box3DSoaKind.Box, V(.15f, .08f, .09f), new[] { DollPrim.Ellipsoid(V(0, -.005f, 0), V(.14f, .085f, .095f)), DollPrim.Cone(V(0, .02f, 0), .085f, V(0, .09f, 0), .07f) }),
        new(Box3DSoaKind.Box, V(.15f, .22f, .09f), new[]
        {
            DollPrim.Ellipsoid(V(0, .07f, .005f), V(.145f, .15f, .095f)), DollPrim.Cone(V(0, -.06f, 0), .08f, V(0, -.15f, 0), .072f),
            DollPrim.Cone(V(-.205f, .14f, 0), .045f, V(.205f, .14f, 0), .045f), DollPrim.Cone(V(0, .17f, 0), .042f, V(0, .20f, 0), .038f),
        }, 0.072f),
        new(Box3DSoaKind.Sphere, V(.11f, 0, 0), new[] { DollPrim.Ellipsoid(V(0, .015f, -.005f), V(.085f, .108f, .095f)), DollPrim.Cone(V(0, -.02f, .015f), .06f, V(0, -.075f, .045f), .033f) }, 0.036f),
        // The shoulder opens along the shoulder bar (the torso's line out through the socket), not along the bone from the torso's centre (33.7 degrees up it): the torso's knife rim 0.0 mm against 85.4 from its centre, and 137.9 along the arm's bind line.
        new(Box3DSoaKind.Capsule, V(.045f, .11f, 0), new[] { DollPrim.Cone(V(0, .125f, 0), .05f, V(0, -.12f, 0), .037f) }, 0.05f, ParentLine: V(1, 0, 0)),
        new(Box3DSoaKind.Capsule, V(.04f, .10f, 0), new[] { DollPrim.Cone(V(0, .115f, 0), .036f, V(0, -.105f, 0), .024f) }, 0.038f),
        // The hip opens straight down the thigh, not along the line from the pelvis's centre (37.9 degrees off it): the pelvis's knife rim 0.0 mm.
        new(Box3DSoaKind.Capsule, V(.07f, .18f, 0), new[] { DollPrim.Cone(V(0, .2f, 0), .07f, V(0, -.22f, 0), .05f) }, 0.07f, ParentLine: V(0, -1, 0)),
        new(Box3DSoaKind.Capsule, V(.06f, .17f, 0), new[] { DollPrim.Cone(V(0, .2f, 0), .05f, V(0, -.2f, 0), .035f), DollPrim.Ellipsoid(V(0, .08f, -.012f), V(.05f, .1f, .055f)) }, 0.05f),
        // The ankle opens up the shin, not along the line from the foot's centre (51 degrees back): each foot's knife rim 0.0 mm against 10.8.
        new(Box3DSoaKind.Box, V(.05f, .04f, .12f), new[] { DollPrim.Ellipsoid(V(0, 0, -.005f), V(.045f, .038f, .105f)), DollPrim.Cone(V(0, .01f, -.08f), .036f, V(0, -.015f, .1f), .02f) }, 0.034f, Line: V(0, 1, 0)),
        // The hands (ruling puppet-hands-thumb-palm-fingers): the forearm ends at the wrist ball; the palm runs from the wrist (y +.045) to the knuckles (y -.045); the thumb from its
        // CMC ball (y +.05) down its metacarpal; the fingers from the knuckle (y +.0425) down. Sockets only on the hand joints (doll-pieces-hug-balls), and the knuckle ball is a roll
        // across the hand (doll-knuckle-roll) that palm and fingers hug. Each limb ends inside its ball (see DollEntry): the forearm's 24 mm end under the wrist ball's 26, the thumb's 12 mm root under the CMC ball's 14, the palm's
        // 14 mm half-thickness under the knuckle roll's 15, so the ball shows round where a flat shoulder would leave a wedge; the roll shows a 12 mm band between the palm (ending 6 mm short of the knuckle axis) and the fingers (starting 6 mm past it).
        new(Box3DSoaKind.Box, V(.014f, .045f, .04f), new[] { DollPrim.Box(V(0, -.0125f, 0), V(.014f, .0265f, .04f), .011f), DollPrim.Ellipsoid(V(0, .028f, 0), V(.0135f, .022f, .027f)) }, 0.026f),
        new(Box3DSoaKind.Capsule, V(.011f, .03f, 0), new[] { DollPrim.Cone(V(0, .044f, 0), .012f, V(0, -.032f, 0), .0105f) }, 0.014f),
        new(Box3DSoaKind.Box, V(.011f, .0425f, .038f), new[] { DollPrim.Box(V(0, -.00575f, 0), V(.0115f, .03625f, .037f), .0095f) }, 0.015f, V(0, 0, 1), 0.028f),
    };

    /// <summary>The entry that dresses a body of this physics shape, or null.</summary>
    public static DollEntry? Find(Box3DSoaKind kind, float3 size)
    {
        foreach (var e in Entries) if (e.Matches(kind, size)) return e;
        return null;
    }

    /// <summary>A body with no entry, as its physics shape: a sphere, a capsule (a round cone of one radius) or a box rounded by <see cref="FallbackRound"/>.</summary>
    public static DollPrim[] Fallback(Box3DSoaKind kind, float3 size) => kind switch
    {
        Box3DSoaKind.Sphere => new[] { DollPrim.Sphere(default, size.x) },
        Box3DSoaKind.Capsule => new[] { DollPrim.Cone(V(0, size.y, 0), size.x, V(0, -size.y, 0), size.x) },
        _ => new[] { DollPrim.Box(default, size, MathF.Min(FallbackRound, math.min(size.x, math.min(size.y, size.z)))) },
    };
}
