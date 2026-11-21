using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CultMath;
using Ymir.Box3D;

namespace BadDay.Client.Tests;

/// <summary>
/// The puppet's rules that need no scene, run by the smoke before its scenario: when the model rebuilds the shape, and what the ball-jointed doll's
/// pieces are (separate, closed, within the error, clear of their balls). Each check reports what broke through <c>fail</c>.
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
    private const int DollSharedEdges = 1998, DollCreasedEdges = 1991;

    public static void Run(Action<string> fail, in Box3DSoaView creationView)
    {
        var total = Stopwatch.StartNew();
        void Check(bool ok, string what) { if (!ok) fail(what); }
        PuppetModel Fresh() => new();

        // A shape is published when its pieces are built: the first update starts them, the wait lets them land, and the next update publishes once.
        bool Published(PuppetModel model, List<PuppetBody> b, List<PuppetJointLane> j)
        {
            bool now = model.Update(b, j);
            model.Building?.Wait();
            return now || model.Update(b, j);
        }

        // The first update builds; the same signature again, even from new lists, never does.
        var m = Fresh();
        var (bodies, joints) = Chain();
        Check(Published(m, bodies, joints) && m.Shape is not null, "the first update did not build the puppet");
        var first = m.Shape;
        var (again, againJoints) = Chain();
        Check(!m.Update(again, againJoints) && ReferenceEquals(m.Shape, first), "an unchanged signature rebuilt the puppet");

        // Poses and joint origins alone are not topology: the bind is the first sight.
        var moved = Chain();
        for (int i = 0; i < moved.Bodies.Count; i++) moved.Bodies[i] = moved.Bodies[i] with { Bind = Rigid.FromQuaternion(0, 0, 0, 1, new float3(0.5f, 2f + i, 0)) };
        moved.Joints[0] = moved.Joints[0] with { Origin = new float3(9, 9, 9) };
        Check(!m.Update(moved.Bodies, moved.Joints) && ReferenceEquals(m.Shape, first), "bodies that only moved rebuilt the puppet");

        // Each change of the bodies publishes a new shape exactly once, and then holds.
        void Changes(string what, Action<List<PuppetBody>, List<PuppetJointLane>> edit)
        {
            var model = Fresh();
            var (b, j) = Chain();
            Published(model, b, j);
            var built = model.Shape;
            edit(b, j);
            Check(Published(model, b, j) && !ReferenceEquals(model.Shape, built), $"{what}: the signature changed and the puppet was not rebuilt");
            var rebuilt = model.Shape;
            Check(!model.Update(b, j) && ReferenceEquals(model.Shape, rebuilt), $"{what}: the new signature rebuilt again");
        }
        Changes("a body added", (b, j) => b.Add(Body(13, Box3DSoaKind.Sphere, 0.03f, 0f, 0.5f)));
        Changes("a body removed", (b, j) => { b.RemoveAt(2); j.RemoveAt(1); });
        Changes("a size changed", (b, j) => b[0] = b[0] with { Size = new float3(0.035f, 0.05f, 0f) });
        Changes("a shape kind changed", (b, j) => b[2] = b[2] with { Kind = Box3DSoaKind.Capsule, Size = new float3(0.03f, 0.02f, 0f) });
        Changes("a body's identity changed", (b, j) => { b[1] = b[1] with { Id = 21 }; j[0] = j[0] with { B = 21 }; j[1] = j[1] with { A = 21 }; });

        // Sockets and balls are fixed at first sight like the bind (lodur:ruling:doll-severed-keeps-socket): severing a joint, changing its kind or adding a joint
        // later changes nothing and builds nothing; a body that leaves takes its joints' balls with it.
        {
            var model = Fresh();
            var (b, j) = Chain();
            Published(model, b, j);
            var drawn = model.Shape!; long keys = PuppetPieces.Built;
            j.RemoveAt(1);
            Check(!model.Update(b, j) && ReferenceEquals(model.Shape, drawn), "severing a joint changed the drawn shape: its socket and ball are kept from first sight");
            j[0] = j[0] with { Kind = Box3DSoaJointKind.Weld };
            j.Add(new PuppetJointLane(Box3DSoaJointKind.Weld, 10, 12, new float3(0, 0.3f, 0)));
            Check(!model.Update(b, j) && ReferenceEquals(model.Shape, drawn) && PuppetPieces.Built == keys, "a joint changed or added after first sight rebuilt the puppet: it gets no ball or socket");
            Check(drawn.Joints.Length == 2 && drawn.PieceCount == 5, $"the chain has {drawn.Joints.Length} joints and {drawn.PieceCount} pieces, not the 2 and 5 of its first sight");
            b.RemoveAt(2);
            Check(Published(model, b, j) && model.Shape!.Joints.Length == 1, "a body that left kept its joint's ball");
        }

        // A body is first seen with its joints (lodur:ruling:doll-severed-keeps-socket): one that appears later with a joint to a body already seen gets its socket and ball,
        // by its own first sight; a joint that appears an update after its bodies, or between two bodies already seen, gets none.
        {
            var model = Fresh();
            var (b, j) = Chain();
            Published(model, b, j);
            b.Add(Body(13, Box3DSoaKind.Capsule, 0.03f, 0.05f, 0.55f));
            j.Add(new PuppetJointLane(Box3DSoaJointKind.Revolute, 12, 13, new float3(0, 0.47f, 0)));
            Check(Published(model, b, j) && model.Shape!.Joints.Length == 3 && model.Shape.PieceCount == 7, $"a body that first appears with its joint has {model.Shape!.Joints.Length} joints and {model.Shape.PieceCount} pieces, not the 3 and 7 of a ball and socket at its first sight");
            var drawn = model.Shape;
            j.RemoveAt(2);
            Check(!model.Update(b, j) && ReferenceEquals(model.Shape, drawn), "severing the joint of a later body changed the drawn shape");
            var late = Fresh();
            var (lb, lj) = Chain();
            lj.RemoveAt(1);
            Published(late, lb, lj);
            lj.Add(new PuppetJointLane(Box3DSoaJointKind.Revolute, 11, 12, new float3(0, 0.32f, 0)));
            Check(!late.Update(lb, lj) && late.Shape!.Joints.Length == 1, "a joint between two bodies already seen rebuilt the puppet");
            lb[0] = lb[0] with { Size = new float3(0.035f, 0.05f, 0f) };
            Check(Published(late, lb, lj) && late.Shape!.Joints.Length == 1, "a joint between two bodies already seen got a ball and socket at the next rebuild");
        }

        // Pieces are cached by key and built off the tick: a body whose pieces are not built leaves Update false with the previous shape drawn, until they are.
        {
            var model = Fresh();
            var (b, j) = Chain();
            Published(model, b, j);
            var drawn = model.Shape;
            b[0] = b[0] with { Size = new float3(0.0371f, 0.0529f, 0f) }; // a size never built
            Check(!model.Update(b, j) && ReferenceEquals(model.Shape, drawn), "an update whose pieces were not built yet published or dropped the previous shape");
            Check(model.Building is not null, "an update with unbuilt pieces is not building them");
            model.Building?.Wait();
            Check(model.Update(b, j) && !ReferenceEquals(model.Shape, drawn), "the shape was not published once its pieces were built");
            Check(!model.Update(b, j) && model.Building is null, "the published shape was published again");
        }

        // A body with no doll entry is drawn as its physics shape, and any number of bodies is drawn: a crowd of 40 spheres builds a shape whose every body
        // owns a closed piece, and skins to a surface with a facet for every triangle.
        var many = Fresh();
        long keysBefore = PuppetPieces.Built;
        var crowd = Enumerable.Range(0, 40).Select(i => Body((ulong)(100 + i), Box3DSoaKind.Sphere, 0.03f, 0f, i * 0.2f)).ToList();
        Check(Published(many, crowd, new List<PuppetJointLane>()) && many.Shape is { Corners.Length: > 0 }, "a puppet of 40 bodies was not built");
        Check(PuppetPieces.Built - keysBefore <= 1, $"a crowd of 40 equal spheres built {PuppetPieces.Built - keysBefore} piece meshes, not one: the pieces are not cached by key");
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
        long built = clock.ElapsedMilliseconds; // waits on the pieces the scene's puppet already asked for
        var set = PuppetField.Pieces(real, inputs); var fields = set.Fields;
        Console.WriteLine($"PUPPET {PuppetPieces.Built} piece meshes built so far, {PuppetPieces.BuildMilliseconds:F0} ms summed over the thread pool");
        long builtKeys = PuppetPieces.Built; var again2 = Stopwatch.StartNew();
        PuppetField.Build(real, inputs);
        Console.WriteLine($"PUPPET the same doll again, from the cache: {again2.ElapsedMilliseconds} ms, {PuppetPieces.Built - builtKeys} piece meshes built");
        Check(PuppetPieces.Built == builtKeys, $"building the same doll twice built {PuppetPieces.Built - builtKeys} piece meshes the second time");
        var triangles = PieceTriangles(puppet);
        Console.WriteLine($"PUPPET {puppet.Triangles} triangles ({triangles.Take(real.Count).Sum(t => t.Length)} in {real.Count} bodies, {triangles.Skip(real.Count).Sum(t => t.Length)} in {inputs.Count} balls), {puppet.Vertices.Length} vertices, built in {built} ms; per piece {string.Join(" ", triangles.Select(t => t.Length))}");

        PiecesAreSeparate(puppet, real.Count, inputs.Count, triangles, Check);
        Clearance(puppet, fields, triangles, Check);
        BallsFollowTheirParent(puppet, real.Count, Check);
        Seated(puppet, fields, triangles, Check);
        HandsArePieces(puppet, real, fields, Check);
        BallsArePrimitives(puppet, set, triangles, Check);
        ThinPiecesKeepTheirSilhouette(puppet, real, fields, triangles, Check);
        ExistingPiecesUnchanged(fields, Check);
        HandsHug(puppet, set.Balls, triangles, Check);
        NoSlits(puppet, real, triangles, Check);
        NoSeeInside(puppet, real, triangles, Check);
        WithinTheError(puppet, fields, triangles, Check);
        LimbsKept(puppet, fields, triangles, Check);
        Mesher(puppet, triangles, Check);
        AcrossIsPositionWeld(puppet, Check);
        SkinnedSurface(puppet, Check);
        FoldHasNoCrease(Check);
    }

    /// <summary>The doll's body balls are 34 mm or more; a ball under this radius belongs to a hand (the wrist ball is 26 mm) and is judged by <see cref="HandsHug"/>, not by the seat's mm-scale bounds.</summary>
    private const float HandBall = 0.03f;

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
    /// Clearance by construction: no body vertex, centroid or edge midpoint within the radius of a ball it takes part in, of its core (the joint origin, or a roll's
    /// segment: the knuckle roll's clearance is its radius from the segment, not from its centre); the decimator's hard rule is the ball's radius plus 0.2 mm.
    /// </summary>
    private static void Clearance(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        int inside = 0, probed = 0;
        foreach (var f in fields)
        {
            foreach (var p in Samples(shape, triangles[f.Index]))
            {
                probed++;
                foreach (var c in f.Cuts) if (c.Off(f.Local(p)) < c.BallRadius + 0.0002f - 1e-5f) inside++;
            }
        }
        check(probed > 1000, $"the clearance check is vacuous: {probed} points");
        check(inside == 0, $"{inside} body points lie inside a joint ball's radius");
    }

    /// <summary>A ball is drawn by its joint's parent: every vertex of ball piece n + j has the bone of joint j's body A.</summary>
    private static void BallsFollowTheirParent(PuppetShape shape, int bodies, Action<bool, string> check)
    {
        int wrong = 0, vertices = 0;
        for (int v = 0; v < shape.Vertices.Length; v++)
        {
            if (shape.Piece[v] < bodies) continue;
            vertices++;
            if (shape.Bone[v] != shape.Joints[shape.Piece[v] - bodies].A) wrong++;
        }
        check(vertices > 0, "the ball bone check is vacuous: no ball vertices");
        check(wrong == 0, $"{wrong} of {vertices} ball vertices do not follow their joint's parent body");
    }

    /// <summary>The hand's three bodies as the doll keys them (ruling puppet-hands-thumb-palm-fingers): by physics shape, never by name, and each one's bend (rad) at its joint to its parent in the bent pose (wrist 0.6, thumb 0.7, fingers 1.2, as HandFrames bends them).</summary>
    private static readonly (string Name, Box3DSoaKind Kind, float3 Size, float Bend)[] HandBodies =
    {
        ("palm", Box3DSoaKind.Box, new float3(.014f, .045f, .04f), 0.6f),
        ("thumb", Box3DSoaKind.Capsule, new float3(.011f, .03f, 0f), 0.7f),
        ("fingers", Box3DSoaKind.Box, new float3(.011f, .0425f, .038f), 1.2f),
    };

    /// <summary>The puppet is 37 pieces (19 bodies, 18 balls; PiecesAreSeparate holds each closed), and every palm, thumb and fingers body is dressed by its doll entry, not drawn as its physics shape.</summary>
    private static void HandsArePieces(PuppetShape shape, List<PuppetBody> bodies, PieceField[] fields, Action<bool, string> check)
    {
        check(shape.PieceCount == 37, $"the puppet is {shape.PieceCount} pieces, not the 37 of 19 bodies and 18 balls");
        foreach (var (name, kind, size, _) in HandBodies)
        {
            var mine = Enumerable.Range(0, bodies.Count).Where(i => bodies[i].Kind == kind && math.length(bodies[i].Size - size) <= 1e-4f).ToArray();
            check(mine.Length == 2, $"{mine.Length} {name} bodies, not two");
            int fallback = mine.Count(i => !fields[i].Key.Id.StartsWith("entry "));
            check(fallback == 0, $"{fallback} of the {name} bodies are drawn as their fallback physics shape, not by a doll entry");
        }
    }

    /// <summary>
    /// Balls are built primitives (ruling doll-joints-built-primitives): every ball piece is the straddled lattice of <see cref="Doll.BallFrequency"/> and nothing else. A sphere has 38 vertices and 72 triangles, a roll
    /// 50 and 96; every vertex of a ball lies the same distance from its core (the joint origin, or the roll's segment) to 1e-5, outside the ball's radius by at most 4%, and every face centroid at least 4% inside it
    /// (the facets straddle the sphere); the balls total 1344 triangles. Vacuous under 18 balls.
    /// </summary>
    private static void BallsArePrimitives(PuppetShape shape, PieceSet set, int[][] triangles, Action<bool, string> check)
    {
        const float straddle = 0.04f;
        int wrongCount = 0, uneven = 0, outside = 0, inside = 0, total = 0;
        foreach (var ball in set.Balls)
        {
            bool roll = math.lengthsq(ball.Roll) > 0;
            var vertices = Enumerable.Range(0, shape.Vertices.Length).Where(v => shape.Piece[v] == ball.Index).Select(v => shape.Vertices[v]).ToArray();
            int tris = triangles[ball.Index].Length; total += tris;
            if (vertices.Length != (roll ? 50 : 38) || tris != (roll ? 96 : 72)) wrongCount++;
            float Core(float3 p) => PieceCut.Core(p - ball.Origin, ball.Roll);
            float r = Core(vertices[0]);
            if (vertices.Any(v => MathF.Abs(Core(v) - r) > 1e-5f)) uneven++;
            if (r > ball.Radius * (1 + straddle)) outside++;
            foreach (int t in triangles[ball.Index])
                if (Core((At(shape, 3 * t) + At(shape, 3 * t + 1) + At(shape, 3 * t + 2)) / 3f) < ball.Radius * (1 - straddle)) inside++;
        }
        Console.WriteLine($"PUPPET balls: {set.Balls.Length} primitives, {total} triangles");
        check(set.Balls.Length >= 18, $"the ball check is vacuous: {set.Balls.Length} balls, under 18");
        check(wrongCount == 0, $"{wrongCount} balls are not 38 vertices and 72 triangles (a roll 50 and 96): a ball is not built at BallFrequency {Doll.BallFrequency}");
        check(total == 1344, $"the balls total {total} triangles, not 1344");
        check(uneven == 0, $"{uneven} balls have vertices at different distances from their core: a ball is not a scaled lattice");
        check(outside == 0, $"{outside} balls reach more than {straddle * 100:F0}% past their radius: the facets do not straddle the sphere");
        check(inside == 0, $"{inside} ball faces lie more than {straddle * 100:F0}% inside their radius: the ball does not fill its socket");
    }

    /// <summary>
    /// The silhouette share a thin body's mesh may miss its field's by: the area where the mesh's silhouette and the field's differ, over the field's silhouette, worst over the three axes of the body's
    /// frame (along its thinnest axis and across it), at 2 px/mm. Derived as lodur:ruling:deterministic-bound-margin derives a bound: THINDERIVE
    /// </summary>
    private const float MaxSilhouetteMiss = 0.50f;

    /// <summary>
    /// Thin pieces keep their silhouette: every body under 40 mm thick has a mesh whose silhouette along each axis of its frame, rasterised at 2 px/mm, differs from its field's silhouette (the cuts included) by at most
    /// <see cref="MaxSilhouetteMiss"/> of the field's. The error ThinError sets is a tolerance, not a shape: a tolerance a prism satisfies (the thumb at 8 triangles) is what this reads. Vacuous under six thin bodies.
    /// </summary>
    private static void ThinPiecesKeepTheirSilhouette(PuppetShape shape, List<PuppetBody> bodies, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        const float px = 0.0005f; int thin = 0; float worst = 0; string where = "";
        for (int i = 0; i < bodies.Count; i++)
        {
            if (PuppetField.Thickness(bodies[i].Kind, bodies[i].Size) >= 0.04f) continue;
            thin++;
            var f = fields[i];
            var tris = triangles[i].Select(t => (a: f.Local(At(shape, 3 * t)), b: f.Local(At(shape, 3 * t + 1)), c: f.Local(At(shape, 3 * t + 2)))).ToArray();
            var lo = f.Lo - 0.01f; var hi = f.Hi + 0.01f; var misses = new float[3];
            for (int axis = 0; axis < 3; axis++)
            {
                int iu = (axis + 1) % 3, iv = (axis + 2) % 3;
                int nu = (int)((hi[iu] - lo[iu]) / px) + 1, nv = (int)((hi[iv] - lo[iv]) / px) + 1, nd = (int)((hi[axis] - lo[axis]) / px) + 1;
                var inField = new bool[nu * nv]; var inMesh = new bool[nu * nv];
                System.Threading.Tasks.Parallel.For(0, nu, x =>
                {
                    for (int y = 0; y < nv; y++)
                    {
                        float3 q = default; q[iu] = lo[iu] + (x + 0.5f) * px; q[iv] = lo[iv] + (y + 0.5f) * px;
                        for (int d = 0; d < nd && !inField[x * nv + y]; d++) { q[axis] = lo[axis] + d * px; if (f.Value(q) < 0) inField[x * nv + y] = true; }
                        float qu = q[iu], qv = q[iv];
                        foreach (var (a, b, c) in tris)
                        {
                            float d0 = (b[iu] - a[iu]) * (qv - a[iv]) - (b[iv] - a[iv]) * (qu - a[iu]), d1 = (c[iu] - b[iu]) * (qv - b[iv]) - (c[iv] - b[iv]) * (qu - b[iu]), d2 = (a[iu] - c[iu]) * (qv - c[iv]) - (a[iv] - c[iv]) * (qu - c[iu]);
                            if ((d0 >= 0 && d1 >= 0 && d2 >= 0) || (d0 <= 0 && d1 <= 0 && d2 <= 0)) { inMesh[x * nv + y] = true; break; }
                        }
                    }
                });
                int field = 0, differ = 0;
                for (int k = 0; k < inField.Length; k++) { if (inField[k]) field++; if (inField[k] != inMesh[k]) differ++; }
                float miss = misses[axis] = differ / (float)Math.Max(field, 1);
                if (miss > worst) { worst = miss; where = $"body {i} axis {axis}"; }
            }
            Console.WriteLine($"PUPPET thin silhouette: body {i} ({bodies[i].Kind}, {triangles[i].Length} triangles): miss along x {misses[0] * 100:F1}%, y {misses[1] * 100:F1}%, z {misses[2] * 100:F1}%");
        }
        Console.WriteLine($"PUPPET thin silhouettes: {thin} bodies, worst miss {worst * 100:F1}% of {MaxSilhouetteMiss * 100:F0}% ({where})");
        check(thin >= 6, $"the thin silhouette check is vacuous: {thin} bodies under 40 mm, not six");
        check(worst <= MaxSilhouetteMiss, $"a thin body's mesh silhouette differs from its field's by {worst * 100:F1}% ({where}), over {MaxSilhouetteMiss * 100:F0}%: its shape is lost");
    }

    /// <summary>
    /// The meshes of the body pieces the hands did not touch, as the doll made them before the hands (every body but the forearms and the hands; the balls are primitives, pinned by <see cref="BallsArePrimitives"/>):
    /// per piece key, the vertex and triangle counts and a sum of the vertices to 0.1 mm. A piece of those keys that differs was changed by the hands; the pieces are a pure function of their key.
    /// </summary>
    private static readonly (string Key, int Vertices, int Triangles, long Sum)[] BeforeTheHands =
    {
        ("entry 0|-900,-700,0,700;0,800,0,720;900,-700,0,700", 87, 170, 8281621657712494562),
        ("entry 1|-2100,1400,0,500;0,-2200,0,720;0,2200,0,360;2100,1400,0,500", 72, 140, -6458902082010238553),
        ("entry 2|0,-1100,0,360", 21, 38, -4950703998283759519), ("entry 3|0,-1550,0,380;0,1550,0,500", 17, 30, -7670264571177566577),
        ("entry 5|0,-2500,0,500;0,2500,0,700", 43, 82, 2598061049774360997), ("entry 6|0,-2300,0,340;0,2300,0,500", 25, 46, 3412638719505470267),
        ("entry 7|0,400,-500,340", 22, 40, 4538328027949402287),
    };

    private static long MeshSum(float3[] v)
    {
        long h = 0;
        foreach (var q in v) h = h * 31 + (long)MathF.Round(q.x * 1e4f) * 7 + (long)MathF.Round(q.y * 1e4f) * 13 + (long)MathF.Round(q.z * 1e4f);
        return h;
    }

    /// <summary>Every body but the hands and the forearms is the mesh it was before the hands (<see cref="BeforeTheHands"/>), unchanged by SeatShare (every body ball is 34 mm or more); vacuous under 10 pieces compared (the doll has 11).</summary>
    private static void ExistingPiecesUnchanged(PieceField[] fields, Action<bool, string> check)
    {
        int compared = 0; var changed = new List<string>();
        foreach (var f in fields)
            foreach (var (key, vertices, triangles, sum) in BeforeTheHands)
            {
                if (key != f.Key.Id) continue;
                compared++;
                var (v, t) = PuppetPieces.Get(f.Key, f).Result;
                if (v.Length != vertices || t.Length / 3 != triangles || MeshSum(v) != sum) changed.Add(key);
            }
        check(compared >= 10, $"the unchanged-pieces check is vacuous: {compared} pieces compared, under 10");
        check(changed.Count == 0, $"pieces the hands must not change changed: {string.Join("; ", changed.Distinct())}");
    }

    /// <summary>
    /// Hands hug their balls (ruling doll-pieces-hug-balls, doll-knuckle-roll): at rest every limb at a hand ball seats against it. The ball's true surface (the sphere, or the roll: its two end
    /// caps and its side) is sampled 4000 times; the share of it within 3 mm (2 mm seam + 1 mm) of a neighbouring piece's mesh is that piece's seat, at least HugShare. A limb that stops short of
    /// its ball has no seat, and the share is what a cup holds, not a single touching point. Vacuous under 12 sides of hand joints.
    /// </summary>
    private static void HandsHug(PuppetShape shape, JointBall[] balls, int[][] triangles, Action<bool, string> check)
    {
        const int count = 2000; const float near = 0.003f; // the ruling's 2 mm seam and 1 mm, fixed: the check is not to follow Doll.Seam
        var dirs = new float3[count];
        for (int i = 0; i < count; i++) { float z = 1 - 2f * (i + 0.5f) / count, r = MathF.Sqrt(1 - z * z), ph = i * 2.399963f; dirs[i] = new float3(r * MathF.Cos(ph), r * MathF.Sin(ph), z); }
        int bodies = shape.Ids.Length, sides = 0, bare = 0; float least = float.MaxValue; string leastAt = "";
        for (int j = 0; j < shape.Joints.Length; j++)
        {
            float radius = shape.BallRadius[j];
            if (radius >= HandBall) continue;
            var (_, a, b, o) = shape.Joints[j];
            var roll = balls[j].Roll;
            var surface = new List<float3>();
            if (math.lengthsq(roll) == 0) foreach (var u in dirs) surface.Add(o + u * radius);
            else
            {
                // the capsule about the segment o +- roll: two end caps, and the side as rings about the roll's axis
                foreach (var u in dirs) surface.Add(o + (math.dot(u, roll) >= 0 ? roll : -roll) + u * radius);
                var along = math.normalize(roll); var e1 = math.normalize(math.cross(along, math.abs(along.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0))); var e2 = math.cross(along, e1);
                for (int k = 0; k <= 20; k++) for (int m = 0; m < 100; m++)
                    { float ph = m * 6.2831853f / 100; surface.Add(o + roll * (2f * k / 20 - 1) + (e1 * MathF.Cos(ph) + e2 * MathF.Sin(ph)) * radius); }
            }
            foreach (int body in new[] { a, b })
            {
                int hug = 0;
                foreach (var p in surface)
                {
                    float gap = float.MaxValue;
                    foreach (int t in triangles[body]) gap = MathF.Min(gap, PuppetDecimate.PointTriangle(p, At(shape, 3 * t), At(shape, 3 * t + 1), At(shape, 3 * t + 2)));
                    if (gap <= near) hug++;
                }
                float share = hug / (float)surface.Count;
                sides++; if (share < least) { least = share; leastAt = $"joint {j} body {body}"; } Console.WriteLine($"PUPPET hug: joint {j} body {body}: {share * 100:F1}%");
                if (share < HugShare) bare++;
            }
        }
        Console.WriteLine($"PUPPET hands hug: {sides} hand sides, least seat {least * 100:F1}% of {HugShare * 100:F0}% ({leastAt})");
        check(sides >= 12, $"the hands' hug check is vacuous: {sides} sides of hand joints, under 12");
        check(bare == 0, $"{bare} limbs at hand balls seat on under {HugShare * 100:F0}% of their ball within Seam + 1 mm (least {least * 100:F1}%): they stop short of it");
    }


    /// <summary>A ball joint's readings' poses: the creation pose (the ball and its two neighbours' triangles, in the bind) and, at a hand's joint, the bent pose: the child turned about the joint by its bend (<see cref="HandBodies"/>) about z, as the hand bends about its joints.</summary>
    private static List<(string Pose, List<(float3, float3, float3)> Tris)> JointPoses(PuppetShape shape, List<PuppetBody> bodies, int[][] triangles, int j)
    {
        var (_, a, b, o) = shape.Joints[j];
        List<(float3, float3, float3)> Of(int piece, Rigid? turn = null) =>
            triangles[piece].Select(t => { var p = new[] { At(shape, 3 * t), At(shape, 3 * t + 1), At(shape, 3 * t + 2) }; if (turn is { } r) for (int k = 0; k < 3; k++) p[k] = r.Apply(p[k]); return (p[0], p[1], p[2]); }).ToList();
        int ball = bodies.Count + j;
        var poses = new List<(string, List<(float3, float3, float3)>)> { ("creation", Of(ball).Concat(Of(a)).Concat(Of(b)).ToList()) };
        foreach (var (_, kind, size, bend) in HandBodies)
        {
            if (bodies[b].Kind != kind || math.length(bodies[b].Size - size) > 1e-4f) continue;
            var turn = Rigid.About(o, new float3(0, 0, 1), bend * (o.x < 0 ? 1f : -1f)); // the hand at negative x bends one way, the other hand the other, as HandFrames does
            poses.Add(("bent", Of(ball).Concat(Of(a)).Concat(Of(b, turn)).ToList()));
        }
        return poses;
    }

    /// <summary>
    /// No slits at a ball (ruling doll-pieces-hug-balls: intersection beats a gap): at every ball joint of the doll, at the creation pose and, at a hand's joint, at the bent pose, the mean over <see cref="Slits.Views"/>
    /// silhouettes of the background a 3 mm disc closes between the ball and its two neighbours (<see cref="Slits"/>) is at most <see cref="MaxSlit"/> mm2: one standard for every ball. Prints each joint's reading
    /// and its worst single view. Vacuous under 18 joints.
    /// </summary>
    private static void NoSlits(PuppetShape shape, List<PuppetBody> bodies, int[][] triangles, Action<bool, string> check)
    {
        int joints = shape.Joints.Length;
        var rows = new (string Pose, float Mean, float Max)[joints][];
        var clock = Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.For(0, joints, j =>
            rows[j] = JointPoses(shape, bodies, triangles, j).Select(p => { var (mean, max) = Slits.Per(p.Tris, shape.Joints[j].Origin, shape.BallRadius[j]); return (p.Pose, mean, max); }).ToArray());
        float worst = 0;
        for (int j = 0; j < joints; j++)
            foreach (var (pose, mean, max) in rows[j])
            {
                Console.WriteLine($"PUPPET slits: joint {j} (bodies {shape.Joints[j].A}-{shape.Joints[j].B}, ball {shape.BallRadius[j] * 1000:F1} mm, {pose}): mean {mean:F2} mm2, worst view {max:F1}");
                worst = MathF.Max(worst, mean);
            }
        Console.WriteLine($"PUPPET slits: worst joint {worst:F2} mm2 of {MaxSlit} ({clock.ElapsedMilliseconds} ms)");
        check(joints >= 18, $"the slit check is vacuous: {joints} joints, under 18");
        check(worst <= MaxSlit, $"a ball joint shows slits: mean {worst:F2} mm2 over {Slits.Views} views, over {MaxSlit}");
    }

    /// <summary>
    /// The most slit area (mm2, mean over the views) any ball joint may show, one standard for the hands and the body, derived by lodur:ruling:deterministic-bound-margin as the geometric midpoint of the shipped
    /// worst and the least claimed mutant. SLITDERIVE
    /// </summary>
    private const float MaxSlit = 100f;

    /// <summary>
    /// No see-inside (ruling doll-pieces-hug-balls: a limb is not a window into itself): at every ball joint, at both poses, the mean over <see cref="SeeInside.Directions"/> directions of the area where a ray first meets the
    /// back of a triangle of the ball or its two neighbours (<see cref="SeeInside"/>) is at most <see cref="MaxSeeInside"/> mm2. The silhouette reads cannot see an opening in the skin: there is a surface behind it.
    /// </summary>
    private static void NoSeeInside(PuppetShape shape, List<PuppetBody> bodies, int[][] triangles, Action<bool, string> check)
    {
        int joints = shape.Joints.Length;
        var rows = new (string Pose, float Mean, float Max)[joints][];
        var clock = Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.For(0, joints, j =>
            rows[j] = JointPoses(shape, bodies, triangles, j).Select(p => { var (mean, max) = SeeInside.Per(p.Tris, shape.Joints[j].Origin, shape.BallRadius[j]); return (p.Pose, mean, max); }).ToArray());
        float worst = 0;
        for (int j = 0; j < joints; j++)
            foreach (var (pose, mean, max) in rows[j])
            {
                if (mean > 0.005f) Console.WriteLine($"PUPPET see-inside: joint {j} (ball {shape.BallRadius[j] * 1000:F1} mm, {pose}): mean {mean:F2} mm2, worst direction {max:F1}");
                worst = MathF.Max(worst, mean);
            }
        Console.WriteLine($"PUPPET see-inside: worst joint {worst:F2} mm2 of {MaxSeeInside} ({clock.ElapsedMilliseconds} ms)");
        check(worst <= MaxSeeInside, $"a ball joint shows the inside of a piece: mean {worst:F2} mm2 over {SeeInside.Directions} directions, over {MaxSeeInside}");
    }

    /// <summary>The most see-inside area (mm2, mean over the directions) any ball joint may show, derived by lodur:ruling:deterministic-bound-margin. SEEDERIVE</summary>
    private const float MaxSeeInside = 100f;

    /// <summary>The share of a hand ball's surface a limb's mesh must lie within Seam + 1 mm of, per side: derived from the shipped hands (the least side seats on 20.2% with the cups, 9.3% before them) and the seam mutant (sockets 10 mm off their balls seat on none).</summary>
    private const float HugShare = 0.05f;

    /// <summary>The distance along the ray from <paramref name="o"/> in direction <paramref name="d"/> to the first of the piece's triangles it crosses, or infinity.</summary>
    private static float FirstHit(PuppetShape shape, int[] piece, float3 o, float3 d)
    {
        float best = float.PositiveInfinity;
        foreach (int t in piece)
        {
            var a = At(shape, 3 * t); var e1 = At(shape, 3 * t + 1) - a; var e2 = At(shape, 3 * t + 2) - a; var pv = math.cross(d, e2);
            float det = math.dot(e1, pv);
            if (MathF.Abs(det) < 1e-12f) continue;
            var tv = o - a; float u = math.dot(tv, pv) / det;
            if (u < 0 || u > 1) continue;
            var qv = math.cross(tv, e1); float w = math.dot(d, qv) / det;
            if (w < 0 || u + w > 1) continue;
            float distance = math.dot(e2, qv) / det;
            if (distance > 1e-6f && distance < best) best = distance;
        }
        return best;
    }

    /// <summary>
    /// The seat (ruling doll-pieces-hug-balls): at the creation pose, per joint and side, 3000 Fibonacci rays leave the joint origin; the cup is the rays whose
    /// point at the socket radius + 1 mm is inside the piece's field, and the rays kept are those within 0.6 of the cup's angular radius about its mean direction.
    /// The gap of a ray is where it first meets the limb's mesh less where it first meets the ball's (a miss counts 60 mm). The median gap is at most 7 mm at every
    /// side, and the largest at most 15 mm at every side whose cup lies within a hemisphere (the pelvis's spine socket spans more). FacetError does not own the seat: the
    /// band does. Vacuous unless 22 sides have 50 rays.
    /// </summary>
    private static void Seated(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        const int rays = 3000; const float miss = 0.06f;
        var dirs = new float3[rays];
        for (int i = 0; i < rays; i++) { float z = 1 - 2f * (i + 0.5f) / rays, r = MathF.Sqrt(1 - z * z), ph = i * 2.399963f; dirs[i] = new float3(r * MathF.Cos(ph), r * MathF.Sin(ph), z); }
        int sides = 0, loose = 0, tooFar = 0; float worstMedian = 0, worstMax = 0;
        for (int j = 0; j < shape.Joints.Length; j++)
        {
            if (shape.BallRadius[j] < HandBall) continue;
            var (_, a, b, o) = shape.Joints[j];
            int ball = shape.Ids.Length + j; float socket = shape.BallRadius[j] + Doll.Seam;
            foreach (int body in new[] { a, b })
            {
                var cup = dirs.Where(d => fields[body].Value(fields[body].Local(o + d * (socket + 0.001f))) < 0).ToArray();
                if (cup.Length == 0) continue;
                var axis = math.normalize(cup.Aggregate(new float3(0, 0, 0), (x, d) => x + d));
                float cosMax = cup.Min(d => math.dot(d, axis)), thetaMax = MathF.Acos(MathF.Max(-1, cosMax));
                var gaps = new List<float>();
                foreach (var d in dirs)
                {
                    if (MathF.Acos(MathF.Min(1, math.dot(d, axis))) > 0.6f * thetaMax) continue;
                    float limb = FirstHit(shape, triangles[body], o, d);
                    gaps.Add(float.IsInfinity(limb) || limb > shape.BallRadius[j] + miss ? miss * 1000 : (limb - FirstHit(shape, triangles[ball], o, d)) * 1000);
                }
                if (gaps.Count < 50) continue;
                sides++;
                gaps.Sort();
                float median = gaps[gaps.Count / 2], max = gaps[^1];
                worstMedian = MathF.Max(worstMedian, median);
                if (median > 7) loose++;
                if (cosMax >= 0) { worstMax = MathF.Max(worstMax, max); if (max > 15) tooFar++; }
            }
        }
        Console.WriteLine($"PUPPET seat: {sides} sides, worst median gap {worstMedian:F1} mm, worst largest {worstMax:F1} mm (hemispherical cups)");
        check(sides >= 22, $"the seat check is vacuous: {sides} sides with 50 rays, under 22");
        check(loose == 0, $"{loose} sides of joints have a limb whose median distance from its ball exceeds 7 mm (worst {worstMedian:F1}): the piece floats off its ball");
        check(tooFar == 0, $"{tooFar} sides have a ray that meets the limb more than 15 mm beyond the ball (worst {worstMax:F1})");
    }

    /// <summary>
    /// Within the error: every body piece's vertices, centroids and edge midpoints lie within its error (FacetError, or ThinError of its thickness where that is less) of its field's surface, the cuts included; a
    /// triangle with all three vertices in the seat band has every vertex, centroid and edge midpoint within its seat limit (<see cref="PieceField.SeatLimit"/>: SeatError, or SeatShare of its socket's ball where less, at that point) + 1 mm of it (the
    /// fine mesh's own error at the rims). The balls are primitives: <see cref="BallsArePrimitives"/>.
    /// </summary>
    private static void WithinTheError(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        int over = 0, overBand = 0, bandTriangles = 0; float worstBody = 0, worstBand = 0; var overIn = new SortedSet<int>(); var bandIn = new SortedSet<int>();
        foreach (var f in fields)
        {
            foreach (int t in triangles[f.Index])
            {
                var a = At(shape, 3 * t); var b = At(shape, 3 * t + 1); var c = At(shape, 3 * t + 2);
                var points = new[] { a, b, c, (a + b + c) / 3f, (a + b) / 2f, (b + c) / 2f, (c + a) / 2f };
                float worst = points.Max(p => MathF.Abs(f.Value(f.Local(p))));
                worstBody = MathF.Max(worstBody, worst);
                if (worst > f.Error + 1e-4f) { over++; overIn.Add(f.Index); }
                if (!(f.Tight(f.Local(a)) && f.Tight(f.Local(b)) && f.Tight(f.Local(c)))) continue;
                bandTriangles++;
                float excess = points.Max(p => MathF.Abs(f.Value(f.Local(p))) - MathF.Min(f.Error, f.SeatLimit(f.Local(p)))); // the decimator's own rule, point by point
                worstBand = MathF.Max(worstBand, excess);
                if (excess > 0.001f + 1e-4f) { overBand++; bandIn.Add(f.Index); }
            }
        }
        Console.WriteLine($"PUPPET worst error: bodies {worstBody * 1000:F1} mm of {Doll.FacetError * 1000:F0} (less for thin pieces), {bandTriangles} seat-band triangles {worstBand * 1000:F1} mm past their points' seat limits (SeatError {Doll.SeatError * 1000:F0}, SeatShare {Doll.SeatShare} of the ball where less) + 1");
        check(bandTriangles > 100, $"the seat band check is vacuous: {bandTriangles} triangles");
        check(over == 0, $"{over} body triangles stray further from their piece's surface than its error allows (worst {worstBody * 1000:F1} mm; pieces {string.Join(",", overIn)})");
        check(overBand == 0, $"{overBand} of {bandTriangles} triangles in the seat band stray more than their points' seat limit + 1 mm from their piece's surface (worst {worstBand * 1000:F1} mm past it; pieces {string.Join(",", bandIn)})");
    }

    /// <summary>Limbs kept: every guard point of a piece (its primitives' axis) is inside it with its clearance, half its depth, to the surface: no limb is pinched away.</summary>
    private static void LimbsKept(PuppetShape shape, PieceField[] fields, int[][] triangles, Action<bool, string> check)
    {
        int bad = 0, points = 0; float worst = float.MaxValue;
        foreach (var f in fields)
            foreach (var (local, clear) in f.Guard())
            {
                points++;
                var point = f.Placement.Apply(local);
                float d = triangles[f.Index].Min(t => PuppetDecimate.PointTriangle(point, At(shape, 3 * t), At(shape, 3 * t + 1), At(shape, 3 * t + 2)));
                worst = MathF.Min(worst, d / clear);
                if (f.Value(local) >= 0 || d < clear - 1e-4f) bad++;
            }
        check(points > 30, $"the limb check is vacuous: {points} guard points");
        check(bad == 0, $"{bad} of {points} guard points are outside their piece or nearer its surface than half their depth (worst {worst:F2} of the clearance)");
    }

    /// <summary>
    /// Each piece's mesh: no edge folds (its two triangles disagree on convexity), no triangle faces into its piece (seen from a point a millimetre
    /// off its face along its normal, the piece's surface is crossed an odd number of times in two directions), and none is tiny.
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
        Console.WriteLine($"PUPPET mesher: {edges} shared edges, smallest triangle {smallest * 1e6:F1} mm2");
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
                Corners = corners, Across = across,
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
