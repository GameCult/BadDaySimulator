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
        Check(m.Update(bodies, joints) && m.Builds == 1 && m.Shape is not null, "the first update did not build the puppet once");
        var (again, againJoints) = Chain();
        Check(!m.Update(again, againJoints) && m.Builds == 1, "an unchanged signature rebuilt the puppet");

        // Poses and joint origins alone are not topology: the bind is the first sight.
        var moved = Chain();
        for (int i = 0; i < moved.Bodies.Count; i++) moved.Bodies[i] = moved.Bodies[i] with { Bind = Rigid.FromQuaternion(0, 0, 0, 1, new float3(0.5f, 2f + i, 0)) };
        moved.Joints[0] = moved.Joints[0] with { Origin = new float3(9, 9, 9) };
        var before = m.Shape;
        Check(!m.Update(moved.Bodies, moved.Joints) && m.Builds == 1 && ReferenceEquals(m.Shape, before), "bodies that only moved rebuilt the puppet");

        // Each change of topology rebuilds exactly once, and then holds.
        void Changes(string what, Action<List<PuppetBody>, List<PuppetJointLane>> edit)
        {
            var model = Fresh();
            var (b, j) = Chain();
            model.Update(b, j);
            edit(b, j);
            Check(model.Update(b, j) && model.Builds == 2, $"{what}: the signature changed and the puppet was not rebuilt once");
            Check(!model.Update(b, j) && model.Builds == 2, $"{what}: the new signature rebuilt again");
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
        Check(many.Update(crowd, new List<PuppetJointLane>()) && many.Shape is null && many.Builds == 0, "more shape bodies than bones was drawn");
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
            for (int v = 0; v < shape.Positions.Length; v++) owned[shape.WeightA[v] >= 0.5f ? shape.BoneA[v] : shape.BoneB[v]]++;
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
    }

    /// <summary>The mesh's connected pieces with coincident triangle corners welded.</summary>
    public static int Pieces(PuppetShape shape)
    {
        var id = new Dictionary<(long, long, long), int>();
        var corner = new int[shape.Positions.Length];
        for (int i = 0; i < corner.Length; i++)
        {
            var p = shape.Positions[i];
            var key = ((long)Math.Round(p.x * 1e5), (long)Math.Round(p.y * 1e5), (long)Math.Round(p.z * 1e5));
            if (!id.TryGetValue(key, out corner[i])) id[key] = corner[i] = id.Count;
        }
        var parent = Enumerable.Range(0, id.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int t = 0; t < corner.Length; t += 3) { parent[Find(corner[t + 1])] = Find(corner[t]); parent[Find(corner[t + 2])] = Find(corner[t]); }
        return corner.Select(Find).Distinct().Count();
    }

    /// <summary>
    /// The worst a skinned triangle edge gets under <paramref name="bones"/> (per body, applied to the spread bind): its length over
    /// its bind length, and its length in metres. The ratio overstates when the bind edge is tiny; the length does not.
    /// </summary>
    public static (float Ratio, float Length) Stretch(PuppetShape shape, Rigid[] bones)
    {
        float3 Skin(int v) { float w = shape.WeightA[v]; var p = shape.Positions[v]; return bones[shape.BoneA[v]].Apply(p) * w + bones[shape.BoneB[v]].Apply(p) * (1 - w); }
        float ratio = 0, length = 0;
        for (int t = 0; t < shape.Positions.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = t + e, b = t + (e + 1) % 3;
                float bind = math.length(shape.Positions[a] - shape.Positions[b]);
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
