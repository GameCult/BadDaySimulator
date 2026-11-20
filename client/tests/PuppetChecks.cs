using System;
using System.Collections.Generic;
using System.Linq;
using CultMath;
using Ymir.Box3D;

namespace BadDay.Client.Tests;

/// <summary>
/// The puppet's rules that need no scene, run by the smoke before its scenario: when the model rebuilds the shape.
/// Each check reports what broke through <c>fail</c>.
/// </summary>
public static class PuppetChecks
{
    /// <summary>No skinned edge may be longer than this many times its bind length.</summary>
    public const float StretchLimit = 4f;

    private static PuppetBody Body(ulong id, Box3DSoaKind kind, float s0, float s1, float y) =>
        new(id, kind, new float3(s0, s1, 0), Rigid.FromQuaternion(0, 0, 0, 1, new float3(0, y, 0)));

    private static (List<PuppetBody> Bodies, List<PuppetJointLane> Joints) Chain() => (
        new List<PuppetBody>
        {
            Body(10, Box3DSoaKind.Capsule, 0.06f, 0.15f, 0.3f),
            Body(11, Box3DSoaKind.Capsule, 0.05f, 0.12f, 0.7f),
            Body(12, Box3DSoaKind.Sphere, 0.09f, 0f, 1.0f),
        },
        new List<PuppetJointLane>
        {
            new(Box3DSoaJointKind.Spherical, 10, 11, new float3(0, 0.5f, 0)),
            new(Box3DSoaJointKind.Revolute, 11, 12, new float3(0, 0.85f, 0)),
        });

    public static void Run(Action<string> fail, in Box3DSoaView creationView)
    {
        void Check(bool ok, string what) { if (!ok) fail(what); }
        PuppetModel Fresh() => new(Look.SminRadius, Look.JointBallScale, Look.BoxRound);

        // The first update builds; the same signature again, even from new lists, never does.
        var m = Fresh();
        var (bodies, joints) = Chain();
        Check(m.Update(bodies, joints) && m.Shape is not null, "the first update did not build the puppet");
        var first = m.Shape;
        var (again, againJoints) = Chain();
        Check(!m.Update(again, againJoints) && ReferenceEquals(m.Shape, first), "an unchanged signature rebuilt the puppet");

        // Poses and joint origins alone are not topology: the bind is the first sight.
        var moved = Chain();
        for (int i = 0; i < moved.Bodies.Count; i++) moved.Bodies[i] = moved.Bodies[i] with { Bind = Rigid.FromQuaternion(0, 0, 0, 1, new float3(0.5f, 2f + i, 0)) };
        moved.Joints[0] = moved.Joints[0] with { Origin = new float3(9, 9, 9) };
        Check(!m.Update(moved.Bodies, moved.Joints) && ReferenceEquals(m.Shape, first), "bodies that only moved rebuilt the puppet");

        // Each change of topology rebuilds exactly once, and then holds.
        void Changes(string what, Action<List<PuppetBody>, List<PuppetJointLane>> edit)
        {
            var model = Fresh();
            var (b, j) = Chain();
            model.Update(b, j);
            var built = model.Shape;
            edit(b, j);
            Check(model.Update(b, j) && !ReferenceEquals(model.Shape, built), $"{what}: the signature changed and the puppet was not rebuilt");
            var rebuilt = model.Shape;
            Check(!model.Update(b, j) && ReferenceEquals(model.Shape, rebuilt), $"{what}: the new signature rebuilt again");
        }
        Changes("a joint removed", (b, j) => j.RemoveAt(1));
        Changes("a body added", (b, j) => b.Add(Body(13, Box3DSoaKind.Sphere, 0.05f, 0f, 1.3f)));
        Changes("a body removed", (b, j) => { b.RemoveAt(2); j.RemoveAt(1); });
        Changes("a size changed", (b, j) => b[0] = b[0] with { Size = new float3(0.07f, 0.15f, 0f) });
        Changes("a shape kind changed", (b, j) => b[2] = b[2] with { Kind = Box3DSoaKind.Capsule, Size = new float3(0.09f, 0.05f, 0f) });
        Changes("a joint's kind changed", (b, j) => j[0] = j[0] with { Kind = Box3DSoaJointKind.Weld });
        Changes("a body's identity changed", (b, j) => { b[1] = b[1] with { Id = 21 }; j[0] = j[0] with { B = 21 }; j[1] = j[1] with { A = 21 }; });

        // Past the skin's bone count nothing is drawn, once, and the signature still decides.
        var many = Fresh();
        var crowd = Enumerable.Range(0, PuppetModel.MaxBones + 1).Select(i => Body((ulong)(100 + i), Box3DSoaKind.Sphere, 0.05f, 0f, i * 0.2f)).ToList();
        Check(many.Update(crowd, new List<PuppetJointLane>()) && many.Shape is null, "more shape bodies than bones was drawn");
        Check(!many.Update(crowd, new List<PuppetJointLane>()), "an over-full puppet reported a change with none");
        crowd.RemoveAt(0);
        Check(many.Update(crowd, new List<PuppetJointLane>()) && many.Shape is not null, "the puppet did not come back once it fit the skin");

        // The real puppet at its creation pose: the grid cell is the coarsest that resolves its limbs, and bent every which way
        // no skinned edge is a web.
        var real = new List<PuppetBody>(); var realLanes = new List<PuppetJointLane>();
        PuppetModel.Lanes(creationView, real, realLanes);
        var index = real.Select((b, k) => (b.Id, k)).ToDictionary(x => x.Id, x => x.k);
        var inputs = realLanes.Select(j => new PuppetJoint(j.Kind, index[j.A], index[j.B], real[index[j.A]].Bind.Inverse().Apply(j.Origin))).ToList();
        PuppetShape Build(float cell) => PuppetField.Build(real, inputs, Look.SminRadius, Look.JointBallScale, Look.BoxRound, cell);

        // Resolved: the cell is at most the thinnest limb's radius (Nyquist: the thinnest limb spans at least two cells across, so the
        // grid can represent it), the mesh is one piece, and every body owns some of the surface (is the most influential bone of at
        // least one triangle corner). The first condition decides which cell is the coarsest; the other two catch a mesh that
        // lost a limb, and say nothing about how fine a cell must be.
        float thinnest = real.Min(b => PuppetField.Thickness(b.Kind, b.Size));
        bool Resolved(float cell)
        {
            var shape = Build(cell);
            var owned = new int[real.Count];
            for (int v = 0; v < shape.Vertices.Length; v++) owned[shape.WeightA[v] >= 0.5f ? shape.BoneA[v] : shape.BoneB[v]]++;
            return cell <= thinnest + 1e-6f && Pieces(shape) == 1 && owned.Min() > 0;
        }
        // The look constants may be changed by the operator's joint-look question, within what the mesh can hold: a blend wider than
        // the thinnest limb swallows it, and a negative ball or rounding is no shape.
        Check(Look.SminRadius > 0 && Look.SminRadius <= thinnest, $"Look.SminRadius {Look.SminRadius} m is not within (0, the thinnest limb's radius {thinnest} m]");
        Check(Look.JointBallScale >= 0 && Look.BoxRound >= 0, "Look.JointBallScale or Look.BoxRound is negative");
        Check(Resolved(PuppetField.Cell), $"the shipped {PuppetField.Cell * 1000:F0} mm cell does not resolve the limbs");
        foreach (float coarser in new[] { thinnest * 1.05f, 0.05f, 0.06f, 0.08f, 0.10f })
            Check(coarser <= PuppetField.Cell || !Resolved(coarser), $"a {coarser * 1000:F0} mm cell resolves the limbs, so {PuppetField.Cell * 1000:F0} mm is not the coarsest");

        // Bent at random, every joint at once, no skinned edge is a web. The bounds are what the shipped look measures with a
        // margin (200 poses each: 3.24x and 0.140 m at 45 degrees, 5.0x and 0.205 m at 90); a bind edge is about a cell
        // (0.04 m), so 0.17 m is four cells and 0.25 m is six. At 90 degrees only the length is bounded, since the ratio
        // overstates when a bind edge is tiny.
        var puppet = Build(PuppetField.Cell);
        float worstRatio = 0, worstLength = 0, worstLength90 = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var (ratio, length) = Stretch(puppet, Bend(puppet, seed, 45f * MathF.PI / 180f));
            worstRatio = MathF.Max(worstRatio, ratio); worstLength = MathF.Max(worstLength, length);
            worstLength90 = MathF.Max(worstLength90, Stretch(puppet, Bend(puppet, seed, 90f * MathF.PI / 180f)).Length);
        }
        Check(worstRatio <= StretchLimit, $"every joint bent up to 45 degrees stretches an edge {worstRatio:F2}x its bind length, over {StretchLimit}x");
        Check(worstLength <= 0.17f, $"every joint bent up to 45 degrees makes a skinned edge {worstLength:F3} m long, over 0.17 m");
        Check(worstLength90 <= 0.25f, $"every joint bent up to 90 degrees makes a skinned edge {worstLength90:F3} m long, over 0.25 m");

        Mesher(puppet, Check);
        AcrossIsPositionWeld(puppet, Check);
        SkinnedSurface(puppet, Check);
    }

    /// <summary>The corner slot's vertex, in the spread bind.</summary>
    private static float3 At(PuppetShape shape, int slot) => shape.Vertices[shape.Corners[slot]];

    /// <summary>The unit normal of a triangle seen from outside (the winding is clockwise from outside), from its corners.</summary>
    private static float3 Facet(PuppetShape shape, int triangle)
    {
        var cross = math.cross(At(shape, 3 * triangle + 1) - At(shape, 3 * triangle), At(shape, 3 * triangle + 2) - At(shape, 3 * triangle));
        return -cross / math.length(cross);
    }

    /// <summary>
    /// The mesher's untangle on the shipped cell: no edge folds (its two triangles disagree on convexity), no triangle faces into the body
    /// (seen from a point a millimetre off its face along its normal, the surface is crossed an odd number of times in two directions),
    /// none is tiny, and the vertices the untangle pulled back are few and stay near the isosurface. The coarser cells of the resolution
    /// sweep are judged by Resolved alone: the untangle leaves some marked there.
    /// </summary>
    private static void Mesher(PuppetShape shape, Action<bool, string> check)
    {
        int count = shape.Triangles;
        int folds = 0, edges = 0;
        for (int s = 0; s < shape.Across.Length; s++)
        {
            int o = shape.Across[s];
            if (o < s) continue;
            edges++;
            float3 na = Facet(shape, s / 3), nb = Facet(shape, o / 3);
            if (MathF.Atan2(math.length(math.cross(na, nb)), math.dot(na, nb)) < 1e-2f) continue; // a flatter edge has no convexity to speak of
            // Convex from a side: the neighbour's far corner (the corner opposite the shared edge) lies behind this facet's plane.
            bool convexA = math.dot(na, At(shape, o) - At(shape, 3 * (s / 3) + (s % 3 + 1) % 3)) < 0;
            bool convexB = math.dot(nb, At(shape, s) - At(shape, 3 * (o / 3) + (o % 3 + 1) % 3)) < 0;
            if (convexA != convexB) folds++;
        }
        check(edges > 1000, $"the mesher check is vacuous: {edges} shared edges");
        check(folds == 0, $"{folds} of {edges} shared edges are folds: their two triangles disagree on convexity");

        // Facing into the body: an odd crossing count from just outside the face means the face looks inward.
        var directions = new[] { math.normalize(new float3(0.5773f, 0.3011f, 0.7571f)), math.normalize(new float3(-0.2113f, 0.8317f, -0.5129f)) };
        int inward = 0; float smallest = float.MaxValue;
        for (int t = 0; t < count; t++)
        {
            var a = At(shape, 3 * t); var b = At(shape, 3 * t + 1); var c = At(shape, 3 * t + 2);
            smallest = MathF.Min(smallest, 0.5f * math.length(math.cross(b - a, c - a)));
            var origin = (a + b + c) / 3f + Facet(shape, t) * 1e-3f;
            bool both = true;
            foreach (var d in directions)
            {
                int crossings = 0;
                for (int u = 0; u < count; u++) if (Crosses(origin, d, At(shape, 3 * u), At(shape, 3 * u + 1), At(shape, 3 * u + 2))) crossings++;
                both &= crossings % 2 == 1;
            }
            if (both) inward++;
        }
        check(inward == 0, $"{inward} of {count} triangles face into the body");
        check(smallest >= 1e-6f, $"the smallest triangle is {smallest * 1e6:F2} mm2, under 1 mm2");
        check(shape.Pulled <= 16, $"the untangle pulled back {shape.Pulled} vertices, over 16");
        check(shape.OffSurface <= 0.015f, $"a pulled-back vertex is {shape.OffSurface * 1000:F1} mm off the isosurface, over 15 mm");
    }

    /// <summary>Whether the ray from <paramref name="o"/> along <paramref name="d"/> crosses the triangle (Moller-Trumbore).</summary>
    private static bool Crosses(float3 o, float3 d, float3 a, float3 b, float3 c)
    {
        var e1 = b - a; var e2 = c - a; var pv = math.cross(d, e2);
        float det = math.dot(e1, pv);
        if (MathF.Abs(det) < 1e-12f) return false;
        var tv = o - a; float u = math.dot(tv, pv) / det;
        if (u < 0 || u > 1) return false;
        var qv = math.cross(tv, e1); float w = math.dot(d, qv) / det;
        return w >= 0 && u + w <= 1 && math.dot(e2, qv) / det > 0;
    }

    /// <summary>
    /// <see cref="PuppetShape.Across"/> is the edge match a weld by position makes: vertices at the same place are one, and an edge joins
    /// the corners opposite it in the two triangles that have it, or nothing where it has other than two.
    /// </summary>
    private static void AcrossIsPositionWeld(PuppetShape shape, Action<bool, string> check)
    {
        var id = new Dictionary<(long, long, long), int>();
        var weld = new int[shape.Corners.Length];
        for (int s = 0; s < weld.Length; s++)
        {
            var p = At(shape, s);
            var key = ((long)Math.Round(p.x * 1e5), (long)Math.Round(p.y * 1e5), (long)Math.Round(p.z * 1e5));
            if (!id.TryGetValue(key, out weld[s])) id[key] = weld[s] = id.Count;
        }
        var sides = new Dictionary<(int, int), List<int>>();
        for (int s = 0; s < weld.Length; s++)
        {
            int t = s / 3, k = s % 3, p = weld[3 * t + (k + 1) % 3], q = weld[3 * t + (k + 2) % 3];
            var key = p < q ? (p, q) : (q, p);
            if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<int>();
            list.Add(s);
        }
        int wrong = 0, shared = 0, loose = 0;
        foreach (var list in sides.Values)
        {
            if (list.Count == 2) { shared++; if (shape.Across[list[0]] != list[1] || shape.Across[list[1]] != list[0]) wrong++; }
            else { loose++; foreach (int s in list) if (shape.Across[s] != -1) wrong++; }
        }
        check(shape.Across.Length == shape.Corners.Length, $"the puppet has {shape.Across.Length} Across entries for {shape.Corners.Length} corner slots");
        check(shared > 1000, $"the Across check is vacuous: {shared} shared edges and {loose} not");
        check(wrong == 0, $"{wrong} corner slots' Across differs from the edge match a weld by position makes");
    }

    /// <summary>
    /// <see cref="PuppetSkin"/> against the geometry it should describe, measured from its own skinned corners and the bones alone, at
    /// bends of 0, 45 and 90 degrees (seeds 0 to 19): every vertex where the bones put it; per edge the same crease on both sides, half
    /// the angle between the skinned facets with the sign of the edge's convexity there, or 0 on a skinned fold and on an edge with
    /// no neighbour (a bind-time crease fails this at the bends: Soul counted 1368 sign flips at 90); the altitudes as 2 * area over the
    /// edge's length; the facet and area-weighted vertex normals.
    /// </summary>
    private static void SkinnedSurface(PuppetShape shape, Action<bool, string> check)
    {
        var skin = new PuppetSkin(shape);
        int moved = 0, shared = 0, creased = 0, folds = 0, badFold = 0, asymmetric = 0, notHalf = 0, badSign = 0, badLoose = 0, badAltitude = 0, badFacet = 0, badVertexNormal = 0;
        foreach (float degrees in new[] { 0f, 45f, 90f })
            for (int seed = 0; seed < 20; seed++)
            {
                var bones = Bend(shape, seed, degrees * MathF.PI / 180f);
                skin.Update(bones);
                var at = new float3[shape.Vertices.Length];
                for (int v = 0; v < at.Length; v++)
                {
                    float w = shape.WeightA[v];
                    at[v] = bones[shape.BoneA[v]].Apply(shape.Vertices[v]) * w + bones[shape.BoneB[v]].Apply(shape.Vertices[v]) * (1 - w);
                    if (math.length(skin.Skinned[v] - at[v]) > 1e-5f) moved++;
                }
                float3 P(int slot) => at[shape.Corners[slot]];
                float3 Outward(int t) => -math.cross(P(3 * t + 1) - P(3 * t), P(3 * t + 2) - P(3 * t));
                var normal = new float3[shape.Triangles];
                var vertexSum = new float3[at.Length];
                for (int t = 0; t < normal.Length; t++)
                {
                    var outward = Outward(t);
                    normal[t] = outward / math.length(outward);
                    if (math.length(skin.Facet[t] - normal[t]) > 1e-4f) badFacet++;
                    for (int k = 0; k < 3; k++) vertexSum[shape.Corners[3 * t + k]] += outward;
                    for (int k = 0; k < 3; k++)
                    {
                        float h = math.length(outward) / math.length(P(3 * t + (k + 1) % 3) - P(3 * t + (k + 2) % 3));
                        if (MathF.Abs(skin.Altitude[3 * t + k] - h) > 1e-6f) badAltitude++;
                    }
                    float smallest = MathF.Min(skin.Altitude[3 * t], MathF.Min(skin.Altitude[3 * t + 1], skin.Altitude[3 * t + 2]));
                    if (skin.SmallestAltitude[t] != smallest) badAltitude++;
                }
                for (int v = 0; v < at.Length; v++) if (math.length(skin.VertexNormal[v] - vertexSum[v] / math.length(vertexSum[v])) > 1e-4f) badVertexNormal++;
                for (int s = 0; s < shape.Across.Length; s++)
                {
                    int o = shape.Across[s];
                    if (o < 0) { if (skin.Crease[s] != 0) badLoose++; continue; }
                    if (o < s) continue;
                    shared++;
                    if (MathF.Abs(skin.Crease[s] - skin.Crease[o]) > 1e-5f) asymmetric++;
                    float3 na = normal[s / 3], nb = normal[o / 3];
                    float angle = MathF.Atan2(math.length(math.cross(na, nb)), math.dot(na, nb));
                    if (angle < 1e-2f) continue; // a flat edge has no convexity to speak of, and its crease is below the float noise of the sign
                    bool convexA = math.dot(na, P(o) - P(3 * (s / 3) + (s % 3 + 1) % 3)) < 0;
                    bool convexB = math.dot(nb, P(s) - P(3 * (o / 3) + (o % 3 + 1) % 3)) < 0;
                    if (convexA != convexB) { folds++; if (skin.Crease[s] != 0 || skin.Crease[o] != 0) badFold++; continue; }
                    creased++;
                    if (MathF.Abs(MathF.Abs(skin.Crease[s]) - 0.5f * angle) > 1e-4f) notHalf++;
                    if ((skin.Crease[s] > 0) != convexA || (skin.Crease[o] > 0) != convexB) badSign++;
                }
            }
        check(shared > 60 * 1000 && creased > 60 * 100, $"the skin check is vacuous: {shared} shared edges, {creased} turning more than a hundredth of a radian, over 60 poses");
        check(moved == 0, $"{moved} skinned vertices are not where the bones put them");
        check(badFacet == 0, $"{badFacet} facet normals differ from the skinned corners'");
        check(badVertexNormal == 0, $"{badVertexNormal} vertex normals differ from the area-weighted sum of their skinned facets");
        check(badAltitude == 0, $"{badAltitude} altitudes differ from 2 * area / edge length of the skinned triangle");
        check(asymmetric == 0, $"{asymmetric} of {shared} shared edges carry different creases on their two sides");
        check(notHalf == 0, $"{notHalf} of {creased} turning edges have a crease that is not half the angle between the skinned facets");
        check(badSign == 0, $"{badSign} of {creased} turning edges have a crease whose sign does not match the skinned convexity");
        check(badFold == 0, $"{badFold} of {folds} skinned folds carry a crease");
        check(badLoose == 0, $"{badLoose} edges without exactly one neighbour carry a crease");
    }

    /// <summary>The mesh's connected pieces.</summary>
    public static int Pieces(PuppetShape shape)
    {
        var parent = Enumerable.Range(0, shape.Vertices.Length).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int t = 0; t < shape.Corners.Length; t += 3) { parent[Find(shape.Corners[t + 1])] = Find(shape.Corners[t]); parent[Find(shape.Corners[t + 2])] = Find(shape.Corners[t]); }
        return shape.Corners.Select(Find).Distinct().Count();
    }

    /// <summary>
    /// The worst a skinned triangle edge gets under <paramref name="bones"/> (per body, applied to the spread bind): its length over
    /// its bind length, and its length in metres. The ratio overstates when the bind edge is tiny; the length does not.
    /// </summary>
    public static (float Ratio, float Length) Stretch(PuppetShape shape, Rigid[] bones)
    {
        float3 Skin(int v) { float w = shape.WeightA[v]; var p = shape.Vertices[v]; return bones[shape.BoneA[v]].Apply(p) * w + bones[shape.BoneB[v]].Apply(p) * (1 - w); }
        float ratio = 0, length = 0;
        for (int t = 0; t < shape.Corners.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = shape.Corners[t + e], b = shape.Corners[t + (e + 1) % 3];
                float bind = math.length(shape.Vertices[a] - shape.Vertices[b]);
                float now = math.length(Skin(a) - Skin(b));
                length = MathF.Max(length, now);
                if (bind > 1e-5f) ratio = MathF.Max(ratio, now / bind);
            }
        return (ratio, length);
    }

    private static int[] Parents(PuppetShape shape)
    {
        var up = Enumerable.Repeat(-1, shape.Ids.Length).ToArray();
        foreach (var j in shape.Joints) if (up[j.B] < 0) up[j.B] = j.A;
        return up;
    }

    /// <summary>Whether body <paramref name="i"/> is <paramref name="root"/> or hangs under it through the joints' first-parent tree.</summary>
    public static bool Under(PuppetShape shape, int i, int root)
    {
        var up = Parents(shape);
        for (int d = 0; d <= up.Length; d++)
        {
            if (i == root) return true;
            if (up[i] < 0) return false;
            i = up[i];
        }
        return false;
    }

    private static Rigid Identity => Rigid.FromQuaternion(0, 0, 0, 1, new float3(0, 0, 0));

    /// <summary>The bones of a pose with joint <paramref name="joint"/>'s child subtree turned by <paramref name="radians"/> about <paramref name="axis"/> through the joint origin.</summary>
    public static Rigid[] Swing(PuppetShape shape, int joint, float3 axis, float radians)
    {
        var turn = Rigid.About(shape.Joints[joint].Origin, axis, radians);
        return Enumerable.Range(0, shape.Ids.Length).Select(i => Under(shape, i, shape.Joints[joint].B) ? turn : Identity).ToArray();
    }

    /// <summary>The bones of a pose with every joint bent by a random angle up to <paramref name="maxRadians"/> about a random axis, parents first.</summary>
    public static Rigid[] Bend(PuppetShape shape, int seed, float maxRadians)
    {
        var rng = new System.Random(seed);
        var bones = Enumerable.Range(0, shape.Ids.Length).Select(_ => Identity).ToArray();
        var up = Parents(shape);
        int Depth(int i) { int d = 0; while (up[i] >= 0 && d <= up.Length) { i = up[i]; d++; } return d; }
        foreach (int j in Enumerable.Range(0, shape.Joints.Length).OrderBy(j => Depth(shape.Joints[j].B)))
        {
            var joint = shape.Joints[j];
            if (up[joint.B] != joint.A) continue; // only the joint that decides the body's blend
            var axis = new float3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            axis = math.length(axis) < 1e-3f ? new float3(1, 0, 0) : math.normalize(axis);
            var turn = Rigid.About(bones[joint.A].Apply(joint.Origin), axis, ((float)rng.NextDouble() * 2 - 1) * maxRadians);
            for (int i = 0; i < bones.Length; i++) if (Under(shape, i, joint.B)) bones[i] = turn.After(bones[i]);
        }
        return bones;
    }
}
