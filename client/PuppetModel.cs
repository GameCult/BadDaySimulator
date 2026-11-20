using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CultMath;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>A qualifying joint as the lanes name it: the bodies by StableId, the joint origin in the world now.</summary>
public readonly record struct PuppetJointLane(Box3DSoaJointKind Kind, ulong A, ulong B, float3 Origin);

/// <summary>
/// Owns the puppet's shape and the one rule for when it is rebuilt and published, engine-free so the rule can be tested without a scene. The shape is
/// rebuilt when the ordered (StableId, Kind, Size0..2) of the shape bodies changes; the joints are fixed at first sight like the bind (lodur:ruling:doll-severed-keeps-socket):
/// the qualifying joints of the first update, each with its origin in body A's frame, keep their socket in both bodies and their ball in the parent, so severing a joint
/// changes nothing drawn and a joint that appears later gets no ball or socket; a first-sight joint is drawn while both its bodies are present. A body's bind is its pose
/// the first time it is seen (the creation pose). Poses and joint origins alone never rebuild.
/// Nothing here meshes on the caller's thread: a rebuild asks <see cref="PuppetPieces"/> for its pieces (a pure function of their keys, built once) and publishes the
/// new shape when they are all built, in the same update by assembly alone if they already are; until then the previous shape stays.
/// </summary>
public sealed class PuppetModel
{
    private readonly Dictionary<ulong, Rigid> _bind = new();
    private readonly List<(Box3DSoaJointKind Kind, ulong A, ulong B, float3 Local)> _sight = new();
    private readonly List<BodySignature> _bodies = new();
    private readonly List<PuppetBody> _laneBodies = new();
    private readonly List<PuppetJointLane> _laneJoints = new();
    private PieceSet? _pendingSet;
    private Task<(float3[] V, int[] Tris)>[] _pending = System.Array.Empty<Task<(float3[] V, int[] Tris)>>();
    private bool _signed;

    private readonly record struct BodySignature(ulong Id, Box3DSoaKind Kind, float S0, float S1, float S2);

    /// <summary>The shape published so far, or null before the first one lands.</summary>
    public PuppetShape? Shape { get; private set; }

    /// <summary>The pieces the pending shape waits for (completes when they are all built), or null when nothing is pending.</summary>
    public Task? Building => _pendingSet is null ? null : Task.WhenAll(_pending);

    /// <summary>Reads the shape bodies and qualifying joints of the lanes and updates.</summary>
    public bool Update(in Box3DSoaView view)
    {
        Lanes(view, _laneBodies, _laneJoints);
        return Update(_laneBodies, _laneJoints);
    }

    /// <summary>The shape bodies (with their pose now) and the qualifying joints of the lanes: a Revolute, Spherical or Weld between two shapes.</summary>
    public static void Lanes(in Box3DSoaView view, List<PuppetBody> bodies, List<PuppetJointLane> joints)
    {
        bodies.Clear(); joints.Clear();
        var kind = view.Kind;
        for (int i = 0; i < kind.Length; i++)
        {
            if (!PuppetField.HasShape((Box3DSoaKind)kind[i])) continue;
            bodies.Add(new PuppetBody(view.StableId[i], (Box3DSoaKind)kind[i], new float3(view.Size0[i], view.Size1[i], view.Size2[i]),
                Rigid.FromQuaternion(view.RotationX[i], view.RotationY[i], view.RotationZ[i], view.RotationW[i], new float3(view.PositionX[i], view.PositionY[i], view.PositionZ[i]))));
        }
        for (int j = 0; j < view.JointCount; j++)
        {
            int a = view.JointBodyA[j], b = view.JointBodyB[j];
            if (a < 0 || b < 0 || !PuppetField.Qualifies((Box3DSoaJointKind)view.JointKind[j], (Box3DSoaKind)kind[a], (Box3DSoaKind)kind[b])) continue;
            joints.Add(new PuppetJointLane((Box3DSoaJointKind)view.JointKind[j], view.StableId[a], view.StableId[b], new float3(view.JointOriginX[j], view.JointOriginY[j], view.JointOriginZ[j])));
        }
    }

    /// <summary>
    /// <paramref name="bodies"/> carry their pose now in <see cref="PuppetBody.Bind"/>; the model keeps the first one it saw. Returns whether a new shape was published
    /// by this update: on a change of bodies, at once if the pieces are all built, else when the last of them is.
    /// </summary>
    public bool Update(IReadOnlyList<PuppetBody> bodies, IReadOnlyList<PuppetJointLane> joints)
    {
        foreach (var b in bodies) _bind.TryAdd(b.Id, b.Bind);
        if (!_signed)
        {
            // The joints are fixed here: each origin is body A's, kept in A's frame so it follows A; the first sight is the creation pose.
            foreach (var j in joints) _sight.Add((j.Kind, j.A, j.B, _bind[j.A].Inverse().Apply(j.Origin)));
        }
        if (!_signed || !SignatureHolds(bodies)) Start(bodies);
        if (_pendingSet is not { } set || !_pending.All(t => t.IsCompleted)) return false;
        Shape = PuppetField.Assemble(set, _pending.Select(t => t.Result).ToArray());
        _pendingSet = null;
        return true;
    }

    /// <summary>Asks for the pieces of the bodies and the first-sight joints between them; a newer set replaces a pending one.</summary>
    private void Start(IReadOnlyList<PuppetBody> bodies)
    {
        _bodies.Clear();
        var index = new Dictionary<ulong, int>();
        var bound = new List<PuppetBody>();
        foreach (var b in bodies)
        {
            index[b.Id] = bound.Count;
            _bodies.Add(new BodySignature(b.Id, b.Kind, b.Size.x, b.Size.y, b.Size.z));
            bound.Add(b with { Bind = _bind[b.Id] });
        }
        var built = new List<PuppetJoint>();
        foreach (var (kind, a, b, local) in _sight)
            if (index.TryGetValue(a, out int ia) && index.TryGetValue(b, out int ib)) built.Add(new PuppetJoint(kind, ia, ib, local));
        _signed = true;

        var set = PuppetField.Pieces(bound, built);
        _pending = set.Fields.Select(f => PuppetPieces.Get(f.Key, f)).ToArray();
        _pendingSet = set;
    }

    private bool SignatureHolds(IReadOnlyList<PuppetBody> bodies)
    {
        if (bodies.Count != _bodies.Count) return false;
        for (int i = 0; i < bodies.Count; i++)
            if (_bodies[i] != new BodySignature(bodies[i].Id, bodies[i].Kind, bodies[i].Size.x, bodies[i].Size.y, bodies[i].Size.z)) return false;
        return true;
    }
}
