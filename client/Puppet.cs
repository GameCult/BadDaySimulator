using System.Collections.Generic;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// The puppet: one pose carrier per dynamic body, posed from the lanes, so physics interpolation has a node to
/// interpolate. This commit draws nothing; the next gives it the one skinned SDF mesh.
/// </summary>
public partial class Puppet : Node3D
{
    private readonly Dictionary<ulong, Node3D> _nodes = new();
    private readonly HashSet<ulong> _seen = new();

    public int BodyCount => _nodes.Count;

    /// <summary>The colour the puppet is drawn in.</summary>
    public Color Color => Look.Figure;

    /// <summary>Where the puppet draws body <paramref name="stableId"/> now (world), or null if it draws no such body.</summary>
    public Transform3D? PoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.Transform : null;

    /// <summary>The same pose as it is rendered this frame, between the last two ticks.</summary>
    public Transform3D? InterpolatedPoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.GetGlobalTransformInterpolated() : null;

    /// <summary>Makes the carriers match the lanes: creates the missing, frees the gone, then poses every one. <paramref name="teleport"/> skips the interpolation from the previous pose.</summary>
    public void Sync(in Box3DSoaView view, bool teleport = false)
    {
        var ids = view.StableId;
        var kind = view.Kind;
        if (CountDynamic(kind) != _nodes.Count || !AllPresent(ids, kind))
        {
            _seen.Clear();
            for (int i = 0; i < ids.Length; i++) if (IsPuppet(kind[i])) _seen.Add(ids[i]);
            foreach (var id in new List<ulong>(_nodes.Keys))
            {
                if (_seen.Contains(id)) continue;
                _nodes[id].QueueFree();
                _nodes.Remove(id);
            }
            for (int i = 0; i < ids.Length; i++)
            {
                if (!IsPuppet(kind[i]) || _nodes.ContainsKey(ids[i])) continue;
                var node = new Node3D { Name = $"Body{ids[i]}" };
                AddChild(node);
                _nodes[ids[i]] = node;
                teleport = true;
            }
        }

        var px = view.PositionX; var py = view.PositionY; var pz = view.PositionZ;
        var rx = view.RotationX; var ry = view.RotationY; var rz = view.RotationZ; var rw = view.RotationW;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!IsPuppet(kind[i])) continue;
            var node = _nodes[ids[i]];
            node.Transform = new Transform3D(new Basis(new Quaternion(rx[i], ry[i], rz[i], rw[i])), new Vector3(px[i], py[i], pz[i]));
            if (teleport) node.ResetPhysicsInterpolation();
        }
    }

    private static bool IsPuppet(uint kind) => (Box3DSoaKind)kind != Box3DSoaKind.StaticBox;

    private static int CountDynamic(System.ReadOnlySpan<uint> kind)
    {
        int n = 0;
        foreach (var k in kind) if (IsPuppet(k)) n++;
        return n;
    }

    private bool AllPresent(System.ReadOnlySpan<ulong> ids, System.ReadOnlySpan<uint> kind)
    {
        for (int i = 0; i < ids.Length; i++) if (IsPuppet(kind[i]) && !_nodes.ContainsKey(ids[i])) return false;
        return true;
    }
}
