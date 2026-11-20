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
    private static readonly float3 Zero = new(0, 0, 0);

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

    public static void Run(Action<string> fail)
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
    }
}
