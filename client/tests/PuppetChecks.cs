using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CultMath;
using Ymir.Box3D;

namespace BadDay.Client.Tests;

/// <summary>
/// The puppet's rules that need no scene, run by the smoke before its scenario: when the model rebuilds the shape, and what the ball-jointed doll's
/// pieces are (separate, closed, within the error, clear of their balls, clean across the hinges' range). Each check reports what broke through <c>fail</c>.
/// </summary>
public static class PuppetChecks
{
    private static PuppetBody Body(ulong id, Box3DSoaKind kind, float s0, float s1, float y) =>
        new(id, kind, new float3(s0, s1, 0), Rigid.FromQuaternion(0, 0, 0, 1, new float3(0, y, 0)));

    private static (List<PuppetBody> Bodies, List<PuppetJointLane> Joints) Chain() => (
        new List<PuppetBody>
        {
            Body(10, Box3DSoaKind.Capsule, 0.03f, 0.05f, 0.1f),
            Body(11, Box3DSoaKind.Capsule, 0.025f, 0.04f, 0.25f),
            Body(12, Box3DSoaKind.Sphere, 0.03f, 0f, 0.38f),
        },
        new List<PuppetJointLane>
        {
            new(Box3DSoaJointKind.Spherical, 10, 11, new float3(0, 0.17f, 0)),
            new(Box3DSoaJointKind.Revolute, 11, 12, new float3(0, 0.32f, 0)),
        });

    /// <summary>
    /// The doll's measured counts (the vacuity floors are half of them): shared edges per pose and edges turning more than a hundredth of a radian per pose,
    /// as printed by the first run on the shipped doll. A check that sees fewer proves nothing about the doll.
    /// </summary>
    private const int DollSharedEdges = 1110, DollCreasedEdges = 1106;

    public static void Run(Action<string> fail, in Box3DSoaView creationView)
    {
        var total = Stopwatch.StartNew();
        void Check(bool ok, string what) { if (!ok) fail(what); }
        PuppetModel Fresh() => new();

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
        Changes("a body added", (b, j) => b.Add(Body(13, Box3DSoaKind.Sphere, 0.03f, 0f, 0.5f)));
        Changes("a body removed", (b, j) => { b.RemoveAt(2); j.RemoveAt(1); });
        Changes("a size changed", (b, j) => b[0] = b[0] with { Size = new float3(0.035f, 0.05f, 0f) });
        Changes("a shape kind changed", (b, j) => b[2] = b[2] with { Kind = Box3DSoaKind.Capsule, Size = new float3(0.03f, 0.02f, 0f) });
        Changes("a joint's kind changed", (b, j) => j[0] = j[0] with { Kind = Box3DSoaJointKind.Weld });
        Changes("a body's identity changed", (b, j) => { b[1] = b[1] with { Id = 21 }; j[0] = j[0] with { B = 21 }; j[1] = j[1] with { A = 21 }; });

        // A body with no doll entry is drawn as its physics shape, and any number of bodies is drawn: a crowd of 40 spheres builds a shape whose every body
        // owns a closed piece, and skins to a surface with a facet for every triangle.
        var many = Fresh();
        var crowd = Enumerable.Range(0, 40).Select(i => Body((ulong)(100 + i), Box3DSoaKind.Sphere, 0.03f, 0f, i * 0.2f)).ToList();
        Check(many.Update(crowd, new List<PuppetJointLane>()) && many.Shape is { Corners.Length: > 0 }, "a puppet of 40 bodies was not built");
        var crowdShape = many.Shape!;
        Check(crowdShape.Ids.Length == 40, $"the crowd's shape carries {crowdShape.Ids.Length} bodies, not 40");
        Check(!many.Update(crowd, new List<PuppetJointLane>()), "a crowd reported a change with none");
        var crowdSkin = new PuppetSkin(crowdShape);
        crowdSkin.Update(Enumerable.Repeat(Identity, crowdShape.Ids.Length).ToArray());
        Check(crowdSkin.Facet.Length * 3 == crowdShape.Corners.Length && crowdSkin.Facet.All(f => math.length(f) > 0.99f), "the crowd's skin has no unit facet for every triangle");
        Check(Pieces(crowdShape) == 40 && crowdShape.Bone.Distinct().Count() == 40, $"the crowd is {Pieces(crowdShape)} pieces over {crowdShape.Bone.Distinct().Count()} bones, not one closed piece per body");
        Check(crowdShape.Vertices.All(v => MathF.Abs(math.length(v - new float3(0, 0.2f * ((int)MathF.Round(v.y / 0.2f)), 0)) - 0.03f) < Doll.FacetError + 1e-4f), "a fallback sphere is not drawn as its physics shape");

        Console.WriteLine($"PUPPET the model's rebuild rule and the crowd of 40: {total.ElapsedMilliseconds} ms");

        // The real puppet at its creation pose, built as the client builds it: the doll's pieces.
        var real = new List<PuppetBody>(); var realLanes = new List<PuppetJointLane>();
        PuppetModel.Lanes(creationView, real, realLanes);
        var index = real.Select((b, k) => (b.Id, k)).ToDictionary(x => x.Id, x => x.k);
        var inputs = realLanes.Select(j => new PuppetJoint(j.Kind, index[j.A], index[j.B], real[index[j.A]].Bind.Inverse().Apply(j.Origin))).ToList();
        var clock = Stopwatch.StartNew();
        var puppet = PuppetField.Build(real, inputs);
        long built = clock.ElapsedMilliseconds;
        var fields = PuppetField.Dress(real, inputs);
        var triangles = PieceTriangles(puppet);
        Console.WriteLine($"PUPPET {puppet.Triangles} triangles ({triangles.Take(real.Count).Sum(t => t.Length)} in {real.Count} bodies, {triangles.Skip(real.Count).Sum(t => t.Length)} in {inputs.Count} balls), {puppet.Vertices.Length} vertices, built in {built} ms; per piece {string.Join(" ", triangles.Select(t => t.Length))}");

        PiecesAreSeparate(puppet, real.Count, inputs.Count, triangles, Check);
        Clearance(puppet, fields, triangles, Check);
        WithinTheError(puppet, fields, triangles, Check);
        LimbsKept(puppet, fields, triangles, Check);
        HingesClean(puppet, fields, real, triangles, Check);
        ChildCones(puppet, real, triangles, Check);
        Mesher(puppet, triangles, Check);
        AcrossIsPositionWeld(puppet, Check);
        SkinnedSurface(puppet, Check);
        FoldHasNoCrease(Check);
    }

    /// <summary>The corner slot's vertex, in the bind.</summary>
    private static float3 At(PuppetShape shape, int slot) => shape.Vertices[shape.Corners[slot]];

    /// <summary>The unit normal of a triangle seen from outside (the winding is clockwise from outside), from its corners.</summary>
    private static float3 Facet(PuppetShape shape, int triangle)
    {
        var cross = math.cross(At(shape, 3 * triangle + 1) - At(shape, 3 * triangle), At(shape, 3 * triangle + 2) - At(shape, 3 * triangle));
        return -cross / math.length(cross);
    }

    /// <summary>The triangles of each piece (indices into the shape's triangles), by piece.</summary>
    private static int[][] PieceTriangles(PuppetShape shape)
    {
        var lists = Enumerable.Range(0, shape.PieceCount).Select(_ => new List<int>()).ToArray();
        for (int t = 0; t < shape.Triangles; t++) lists[shape.Piece[shape.Corners[3 * t]]].Add(t);
        return lists.Select(l => l.ToArray()).ToArray();
    }

    /// <summary>The points a piece is judged at: its vertices, its triangle centroids and its edge midpoints (the decimator's own).</summary>
    private static IEnumerable<float3> Samples(PuppetShape shape, int[] piece)
    {
        foreach (int t in piece)
        {
            var a = At(shape, 3 * t); var b = At(shape, 3 * t + 1); var c = At(shape, 3 * t + 2);
            yield return a; yield return b; yield return c;
            yield return (a + b + c) / 3f; yield return (a + b) / 2f; yield return (b + c) / 2f; yield return (c + a) / 2f;
        }
    }

    /// <summary>One closed piece per dressed body and per joint ball, and no vertex shared across pieces.</summary>
    private static void PiecesAreSeparate(PuppetShape shape, int bodies, int joints, int[][] triangles, Action<bool, string> check)
    {
        check(shape.PieceCount == bodies + joints && triangles.All(t => t.Length >= 4), $"the doll has {shape.PieceCount} pieces, {triangles.Count(t => t.Length >= 4)} of them solids, not one per body and per joint ({bodies} + {joints})");
        int shared = 0, loose = 0;
        for (int t = 0; t < shape.Triangles; t++)
            if (shape.Piece[shape.Corners[3 * t]] != shape.Piece[shape.Corners[3 * t + 1]] || shape.Piece[shape.Corners[3 * t]] != shape.Piece[shape.Corners[3 * t + 2]]) shared++;
        for (int s = 0; s < shape.Across.Length; s++) if (shape.Across[s] < 0) loose++;
        check(shared == 0, $"{shared} triangles join vertices of more than one piece: the doll's joints blend");
        check(loose == 0, $"{loose} corner slots have no partner: a piece is not closed");
        check(Pieces(shape) == shape.PieceCount, $"the doll's mesh is {Pieces(shape)} connected pieces, not {shape.PieceCount}");
    }

    /// <summary>
    /// Clearance by construction: no body vertex, centroid or edge midpoint within the radius of a ball it takes part in (the decimator's hard rule is
    /// the ball's radius plus 0.2 mm), and no ball point outside it.
    /// </summary>
    private static void Clearance(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        int inside = 0, outside = 0, probed = 0;
        foreach (var f in fields)
            foreach (var p in Samples(shape, triangles[f.Index]))
            {
                probed++;
                if (f.IsBall) { if (math.length(p - f.BallCentre) > f.BallRadius + 0.0002f + 1e-5f) outside++; }
                else foreach (var c in f.Cuts) if (math.length(p - c.Origin) < c.BallRadius + 0.0002f - 1e-5f) inside++;
            }
        check(probed > 1000, $"the clearance check is vacuous: {probed} points");
        check(inside == 0, $"{inside} body points lie inside a joint ball's radius");
        check(outside == 0, $"{outside} ball points lie outside the ball's radius");
    }

    /// <summary>Within the error: every piece's vertices, centroids and edge midpoints lie within FacetError (BallError for a ball) of its own field's surface, the cuts included.</summary>
    private static void WithinTheError(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        int over = 0; float worstBody = 0, worstBall = 0;
        foreach (var f in fields)
            foreach (var p in Samples(shape, triangles[f.Index]))
            {
                float e = MathF.Abs(f.Value(p));
                if (f.IsBall) worstBall = MathF.Max(worstBall, e); else worstBody = MathF.Max(worstBody, e);
                if (e > (f.IsBall ? Doll.BallError : Doll.FacetError) + 1e-4f) over++;
            }
        Console.WriteLine($"PUPPET worst error: bodies {worstBody * 1000:F1} mm of {Doll.FacetError * 1000:F0}, balls {worstBall * 1000:F1} mm of {Doll.BallError * 1000:F0}");
        check(over == 0, $"{over} points stray further from their piece's surface than the error allows (bodies {worstBody * 1000:F1} mm, balls {worstBall * 1000:F1} mm)");
    }

    /// <summary>Limbs kept: every guard point of a piece (its primitives' axis) is inside it with its clearance, half its depth, to the surface: no limb is pinched away.</summary>
    private static void LimbsKept(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        int bad = 0, points = 0; float worst = float.MaxValue;
        foreach (var f in fields)
            foreach (var (point, clear) in f.Guard())
            {
                points++;
                float d = triangles[f.Index].Min(t => PuppetDecimate.PointTriangle(point, At(shape, 3 * t), At(shape, 3 * t + 1), At(shape, 3 * t + 2)));
                worst = MathF.Min(worst, d / clear);
                if (f.Value(point) >= 0 || d < clear - 1e-4f) bad++;
            }
        check(points > 30, $"the limb check is vacuous: {points} guard points");
        check(bad == 0, $"{bad} of {points} guard points are outside their piece or nearer its surface than half their depth (worst {worst:F2} of the clearance)");
    }

    /// <summary>
    /// Hinges clean across their range: each hinge joint (a Revolute whose child the doll gives a range) is swept from -flex to +extension in 5 degree
    /// steps, the child turned about the hinge axis through the joint; no point of the parent piece within 2.5 ball radii of the joint lies more than a
    /// millimetre inside the child piece's field, nor the child's inside the parent's (clean to the limit: lodur:ruling:doll-hinges-clean-to-limit).
    /// </summary>
    private static void HingesClean(PuppetShape shape, PieceField[] fields, List<PuppetBody> bodies, int[][] triangles, Action<bool, string> check)
    {
        int hinges = 0, bad = 0; float worst = 0;
        for (int j = 0; j < shape.Joints.Length; j++)
        {
            var (kind, a, b, o) = shape.Joints[j];
            if (kind != Box3DSoaJointKind.Revolute || Doll.Find(bodies[b].Kind, bodies[b].Size) is not { IsHinge: true } entry) continue;
            hinges++;
            var axis = math.normalize(math.mul(bodies[b].Bind.R, Doll.HingeAxis));
            float near = 2.5f * shape.BallRadius[j];
            var parentPoints = Samples(shape, triangles[a]).Where(p => math.length(p - o) < near).ToArray();
            var childPoints = Samples(shape, triangles[b]).Where(p => math.length(p - o) < near).ToArray();
            for (float angle = -entry.Flex; ; angle += 5f * MathF.PI / 180f)
            {
                angle = MathF.Min(angle, entry.Extension);
                var turn = Rigid.About(o, axis, angle); var back = turn.Inverse();
                float depth = 0;
                foreach (var p in parentPoints) depth = MathF.Max(depth, -fields[b].Value(back.Apply(p)));
                foreach (var c in childPoints) depth = MathF.Max(depth, -fields[a].Value(turn.Apply(c)));
                worst = MathF.Max(worst, depth);
                if (depth > 0.001f) bad++;
                if (angle >= entry.Extension) break;
            }
        }
        Console.WriteLine($"PUPPET hinges {hinges}: worst penetration over the range {worst * 1000:F2} mm");
        check(hinges == 6, $"the hinge sweep is vacuous: {hinges} hinges, not an elbow, a knee and an ankle on each side");
        check(bad == 0, $"{bad} poses over the hinges' ranges sink a limb into another (worst {worst * 1000:F1} mm, over 1 mm)");
    }

    /// <summary>
    /// A Spherical joint's child keeps the cone of half angle (180 degrees - the entry's swing cone) / 2 about its rest direction from the joint (the child's
    /// centre seen from it): no point of the child piece lies further than the error outside that cone, measured on the cone's own surface (the apex is the
    /// joint, inside the ball). The parent is not cut: its points reach every way round the joint.
    /// </summary>
    private static void ChildCones(PuppetShape shape, List<PuppetBody> bodies, int[][] triangles, Action<bool, string> check)
    {
        int cones = 0, outside = 0; float worst = 0;
        for (int j = 0; j < shape.Joints.Length; j++)
        {
            var (kind, a, b, o) = shape.Joints[j];
            if (kind != Box3DSoaJointKind.Spherical || Doll.Find(bodies[b].Kind, bodies[b].Size) is not { SwingCone: > 0 } entry) continue;
            cones++;
            var rest = math.normalize(bodies[b].Bind.T - o);
            float half = MathF.Min(MathF.PI / 2, (MathF.PI - entry.SwingCone) / 2), cs = MathF.Cos(half), sn = MathF.Sin(half);
            foreach (var p in Samples(shape, triangles[b]))
            {
                var r = p - o; float along = math.dot(r, rest), perp = math.length(r - rest * along);
                float distance = along * cs + perp * sn < 0 ? math.length(r) : perp * cs - along * sn;
                worst = MathF.Max(worst, distance);
                if (distance > Doll.FacetError + 1e-3f) outside++;
            }
        }
        Console.WriteLine($"PUPPET child cones {cones}: worst {worst * 1000:F1} mm outside");
        check(cones == 6, $"the cone check is vacuous: {cones} spherical joints with a swing cone, not the spine, the neck and a shoulder and a hip on each side");
        check(outside == 0, $"{outside} points of a spherical joint's child lie outside its swing cone's half space (worst {worst * 1000:F1} mm)");
    }

    /// <summary>
    /// Each piece's mesh: no edge folds (its two triangles disagree on convexity), no triangle faces into its piece (seen from a point a millimetre
    /// off its face along its normal, the piece's surface is crossed an odd number of times in two directions), none is tiny, and the vertices the
    /// untangle pulled back are few and stay near the isosurface.
    /// </summary>
    private static void Mesher(PuppetShape shape, int[][] pieces, Action<bool, string> check)
    {
        int folds = 0, edges = 0, inward = 0, count = shape.Triangles; float smallest = float.MaxValue;
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
        check(edges >= DollSharedEdges / 2, $"the mesher check is vacuous: {edges} shared edges");
        check(folds == 0, $"{folds} of {edges} shared edges are folds: their two triangles disagree on convexity");

        var directions = new[] { math.normalize(new float3(0.5773f, 0.3011f, 0.7571f)), math.normalize(new float3(-0.2113f, 0.8317f, -0.5129f)) };
        foreach (var piece in pieces)
            foreach (int t in piece)
            {
                var a = At(shape, 3 * t); var b = At(shape, 3 * t + 1); var c = At(shape, 3 * t + 2);
                smallest = MathF.Min(smallest, 0.5f * math.length(math.cross(b - a, c - a)));
                var origin = (a + b + c) / 3f + Facet(shape, t) * 1e-3f;
                bool both = true;
                foreach (var d in directions)
                {
                    int crossings = 0;
                    foreach (int u in piece) if (Crosses(origin, d, At(shape, 3 * u), At(shape, 3 * u + 1), At(shape, 3 * u + 2))) crossings++;
                    both &= crossings % 2 == 1;
                }
                if (both) inward++;
            }
        check(inward == 0, $"{inward} of {count} triangles face into their piece");
        check(smallest >= 1e-6f, $"the smallest triangle is {smallest * 1e6:F2} mm2, under 1 mm2");
        Console.WriteLine($"PUPPET mesher: {edges} shared edges, smallest triangle {smallest * 1e6:F1} mm2, {shape.Pulled} vertices pulled back, worst {shape.OffSurface * 1000:F1} mm off");
        check(shape.Pulled <= 800, $"the untangle pulled back {shape.Pulled} vertices, over 800");
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
    /// the corners opposite it in the two triangles that have it, or nothing where it has other than two. (The pieces' seams are 2 mm apart,
    /// so a weld by position at 10 micrometres does not join two pieces.)
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
        check(shared >= DollSharedEdges / 2, $"the Across check is vacuous: {shared} shared edges and {loose} not");
        check(wrong == 0, $"{wrong} corner slots' Across differs from the edge match a weld by position makes");
    }

    /// <summary>
    /// <see cref="PuppetSkin"/> against the geometry it should describe, measured from its own skinned corners and the bones alone, at
    /// bends of 0, 45 and 90 degrees (seeds 0 to 19): every vertex where its one bone puts it; per edge the same crease on both sides, half
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
                    at[v] = bones[shape.Bone[v]].Apply(shape.Vertices[v]);
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
        Console.WriteLine($"PUPPET skin check: {shared / 60} shared edges and {creased / 60} turning ones per pose");
        check(shared > 60 * DollSharedEdges / 2 && creased > 60 * DollCreasedEdges / 2, $"the skin check is vacuous: {shared} shared edges, {creased} turning more than a hundredth of a radian, over 60 poses");
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

    /// <summary>
    /// A skinned fold, two triangles lying over each other so that their sides disagree on which way their shared edge turns, has no edge to
    /// bevel: both sides carry crease 0. No pose of the real puppet reaches one but a single joint swung 180 degrees, so the rule is held to
    /// two small shapes of four vertices, both with one bone and no joint: the same four points wound so that the second triangle is
    /// inconsistent with the first (a fold), and wound consistently with a right angle between the two (a turn, which keeps its crease).
    /// </summary>
    private static void FoldHasNoCrease(Action<bool, string> check)
    {
        PuppetShape Pair(float3[] vertices, int[] corners, int slotA, int slotB)
        {
            var across = Enumerable.Repeat(-1, corners.Length).ToArray();
            across[slotA] = slotB; across[slotB] = slotA;
            return new PuppetShape
            {
                Ids = new ulong[] { 1 }, Bind = new[] { Identity }, Joints = Array.Empty<(Box3DSoaJointKind, int, int, float3)>(), BallRadius = Array.Empty<float>(),
                Vertices = vertices, Bone = new int[vertices.Length], Piece = new int[vertices.Length],
                Corners = corners, Across = across, Pulled = 0, OffSurface = 0,
            };
        }
        // Triangle A is (0, 1, 2); its edge 1-2 is opposite its corner 0 (slot 0).
        var fold = Pair(new[] { new float3(0, 0, 0), new float3(1, 0, 0), new float3(0, 1, 0), new float3(1, 1, 0.1f) }, new[] { 0, 1, 2, 1, 2, 3 }, 0, 5);
        var turn = Pair(new[] { new float3(0, 0, 0), new float3(1, 0, 0), new float3(0, 1, 0), new float3(0.5f, 0.5f, 0.7f) }, new[] { 0, 1, 2, 1, 3, 2 }, 0, 4);
        foreach (var (name, shape, folds) in new[] { ("fold", fold, true), ("turn", turn, false) })
        {
            var skin = new PuppetSkin(shape);
            skin.Update(new[] { Identity });
            float3 P(int slot) => shape.Vertices[shape.Corners[slot]];
            float3 na = skin.Facet[0], nb = skin.Facet[1];
            bool convexA = math.dot(na, P(shape.Across[0]) - P(1)) < 0, convexB = math.dot(nb, P(0) - P(1)) < 0;
            check((convexA != convexB) == folds, $"the {name} fixture is {(folds ? "not" : "")} a fold: the two sides {(convexA == convexB ? "agree" : "disagree")} on convexity");
            int other = shape.Across[0];
            check(skin.Crease[0] == skin.Crease[other], $"the {name} fixture's two sides carry different creases, {skin.Crease[0]} and {skin.Crease[other]}");
            check(folds ? skin.Crease[0] == 0 : MathF.Abs(skin.Crease[0]) > 0.1f, $"the {name} fixture's crease is {skin.Crease[0]}: a fold has none, a turn keeps its own");
        }
    }

    /// <summary>The mesh's connected pieces.</summary>
    public static int Pieces(PuppetShape shape)
    {
        var parent = Enumerable.Range(0, shape.Vertices.Length).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int t = 0; t < shape.Corners.Length; t += 3) { parent[Find(shape.Corners[t + 1])] = Find(shape.Corners[t]); parent[Find(shape.Corners[t + 2])] = Find(shape.Corners[t]); }
        return shape.Corners.Select(Find).Distinct().Count();
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
            if (up[joint.B] != joint.A) continue; // only the joint that decides the body's parent
            var axis = new float3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            axis = math.length(axis) < 1e-3f ? new float3(1, 0, 0) : math.normalize(axis);
            var turn = Rigid.About(bones[joint.A].Apply(joint.Origin), axis, ((float)rng.NextDouble() * 2 - 1) * maxRadians);
            for (int i = 0; i < bones.Length; i++) if (Under(shape, i, joint.B)) bones[i] = turn.After(bones[i]);
        }
        return bones;
    }
}
