using System.Collections.Generic;
using CultMath;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>A qualifying joint as the lanes name it: the bodies by StableId, the joint origin in the world now.</summary>
public readonly record struct PuppetJointLane(Box3DSoaJointKind Kind, ulong A, ulong B, float3 Origin);

/// <summary>
/// Owns the puppet's shape and the one rule for when it is rebuilt, engine-free so the rule can be tested without a scene:
/// the mesh is built from the first update and again only when the topology signature changes, which is the ordered
/// (StableId, Kind, Size0..2) of the shape bodies and the ordered (JointKind, StableId A, StableId B) of the qualifying joints.
/// A body's bind is its pose the first time it is seen (the creation pose); a joint's origin is kept in body A's frame from then.
/// Poses and joint origins alone never rebuild.
/// </summary>
public sealed class PuppetModel
{
    /// <summary>The skin's bone count: the shader's array size.</summary>
    public const int MaxBones = 32;

    private readonly float _smin, _ballScale, _boxRound;
    private readonly Dictionary<ulong, Rigid> _bind = new();
    private readonly Dictionary<(Box3DSoaJointKind, ulong, ulong), float3> _originLocal = new();
    private readonly List<BodySignature> _bodies = new();
    private readonly List<JointSignature> _joints = new();
    private readonly List<PuppetBody> _laneBodies = new();
    private readonly List<PuppetJointLane> _laneJoints = new();
    private bool _signed;

    private readonly record struct BodySignature(ulong Id, Box3DSoaKind Kind, float S0, float S1, float S2);
    private readonly record struct JointSignature(Box3DSoaJointKind Kind, ulong A, ulong B);

    public PuppetModel(float smin, float ballScale, float boxRound) { _smin = smin; _ballScale = ballScale; _boxRound = boxRound; }

    /// <summary>The shape built from the current signature, or null before the first update or while there are more than <see cref="MaxBones"/> shape bodies.</summary>
    public PuppetShape? Shape { get; private set; }

    /// <summary>How many meshes have been built.</summary>
    public int Builds { get; private set; }

    /// <summary>Reads the shape bodies and qualifying joints of the lanes and updates.</summary>
    public bool Update(in Box3DSoaView view)
    {
        var kind = view.Kind;
        var bodies = _laneBodies; bodies.Clear();
        for (int i = 0; i < kind.Length; i++)
        {
            if (!PuppetField.HasShape((Box3DSoaKind)kind[i])) continue;
            bodies.Add(new PuppetBody(view.StableId[i], (Box3DSoaKind)kind[i], new float3(view.Size0[i], view.Size1[i], view.Size2[i]),
                Rigid.FromQuaternion(view.RotationX[i], view.RotationY[i], view.RotationZ[i], view.RotationW[i], new float3(view.PositionX[i], view.PositionY[i], view.PositionZ[i]))));
        }
        var joints = _laneJoints; joints.Clear();
        for (int j = 0; j < view.JointCount; j++)
        {
            int a = view.JointBodyA[j], b = view.JointBodyB[j];
            if (a < 0 || b < 0 || !PuppetField.Qualifies((Box3DSoaJointKind)view.JointKind[j], (Box3DSoaKind)kind[a], (Box3DSoaKind)kind[b])) continue;
            joints.Add(new PuppetJointLane((Box3DSoaJointKind)view.JointKind[j], view.StableId[a], view.StableId[b], new float3(view.JointOriginX[j], view.JointOriginY[j], view.JointOriginZ[j])));
        }
        return Update(bodies, joints);
    }

    /// <summary>
    /// <paramref name="bodies"/> carry their pose now in <see cref="PuppetBody.Bind"/>; the model keeps the first one it saw.
    /// Returns whether the signature changed, which is whether the shape was rebuilt (or dropped, past the bone cap).
    /// </summary>
    public bool Update(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJointLane> joints)
    {
        foreach (var b in bodies) _bind.TryAdd(b.Id, b.Bind);
        if (_signed && SignatureHolds(bodies, joints)) return false;

        _bodies.Clear(); _joints.Clear();
        var index = new Dictionary<ulong, int>();
        var bound = new List<PuppetBody>();
        foreach (var b in bodies)
        {
            index[b.Id] = bound.Count;
            _bodies.Add(new BodySignature(b.Id, b.Kind, b.Size.x, b.Size.y, b.Size.z));
            bound.Add(b with { Bind = _bind[b.Id] });
        }
        var built = new List<PuppetJoint>();
        foreach (var j in joints)
        {
            var key = (j.Kind, j.A, j.B);
            _joints.Add(new JointSignature(j.Kind, j.A, j.B));
            // The origin is body A's, kept in A's frame so it follows A; the first sight is the creation pose.
            if (!_originLocal.TryGetValue(key, out var local)) _originLocal[key] = local = _bind[j.A].Inverse().Apply(j.Origin);
            built.Add(new PuppetJoint(j.Kind, index[j.A], index[j.B], local));
        }
        _signed = true;

        if (bound.Count > MaxBones) { Shape = null; return true; }
        Shape = PuppetField.Build(bound, built, _smin, _ballScale, _boxRound);
        Shape.Builds = ++Builds;
        return true;
    }

    private bool SignatureHolds(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJointLane> joints)
    {
        if (bodies.Count != _bodies.Count || joints.Count != _joints.Count) return false;
        for (int i = 0; i < bodies.Count; i++)
            if (_bodies[i] != new BodySignature(bodies[i].Id, bodies[i].Kind, bodies[i].Size.x, bodies[i].Size.y, bodies[i].Size.z)) return false;
        for (int i = 0; i < joints.Count; i++)
            if (_joints[i] != new JointSignature(joints[i].Kind, joints[i].A, joints[i].B)) return false;
        return true;
    }
}
