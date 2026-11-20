using System;
using System.Collections.Generic;
using CultMath;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// The puppet: its drawn surface is one skinned SDF isosurface mesh (<see cref="PuppetField"/>) over the dynamic bodies,
/// flat-shaded, built once and rebuilt only when the topology signature (the shape bodies and the joints that blend them)
/// changes. Per-StableId Node3D carriers hold the pose lanes so physics interpolation has something to interpolate, and
/// every frame the bones are those carriers' interpolated transforms relative to the spread bind pose.
/// </summary>
public partial class Puppet : Node3D
{
    /// <summary>The shader's bone array size.</summary>
    private const int MaxBones = 32;

    private readonly Dictionary<ulong, Node3D> _nodes = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly ShaderMaterial _material = new() { Shader = GD.Load<Shader>("res://puppet.gdshader") };
    private readonly MeshInstance3D _skin = new MeshInstance3D { Name = "Skin" };
    private readonly Godot.Collections.Array _bones = new();
    private readonly Dictionary<ulong, Rigid> _bind = new();
    private readonly Dictionary<(uint, ulong, ulong), float3> _originLocal = new();
    private readonly List<int> _shapeLanes = new();
    private readonly List<int> _jointLanes = new();
    private (ulong Id, uint Kind, float S0, float S1, float S2)[] _bodySig = Array.Empty<(ulong, uint, float, float, float)>();
    private (uint Kind, ulong A, ulong B)[] _jointSig = Array.Empty<(uint, ulong, ulong)>();
    private bool _signed;
    private int _builds;
    private ArrayMesh? _mesh;
    private Node3D[] _boneNodes = Array.Empty<Node3D>();
    private Transform3D[] _inverseBind = Array.Empty<Transform3D>();

    public Puppet()
    {
        _material.SetShaderParameter("albedo", Look.Figure);
        for (int i = 0; i < MaxBones; i++) _bones.Add(new Projection(Transform3D.Identity));
        _skin.MaterialOverride = _material;
    }

    public int BodyCount => _nodes.Count;

    /// <summary>The colour the puppet is drawn in: the shader's albedo.</summary>
    public Color Color => _material.GetShaderParameter("albedo").AsColor();

    /// <summary>What the puppet is made of now, or null before the first build or while it draws nothing (too many bodies).</summary>
    public PuppetShape? Shape { get; private set; }

    public override void _Ready() => AddChild(_skin);

    /// <summary>Where the puppet draws body <paramref name="stableId"/> now (world), or null if it draws no such body.</summary>
    public Transform3D? PoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.Transform : null;

    /// <summary>The same pose as it is rendered this frame, between the last two ticks.</summary>
    public Transform3D? InterpolatedPoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.GetGlobalTransformInterpolated() : null;

    /// <summary>The Godot transform of a <see cref="Rigid"/>.</summary>
    public static Transform3D ToTransform(Rigid r) => new(
        new Basis(new Vector3(r.R._m00, r.R._m10, r.R._m20), new Vector3(r.R._m01, r.R._m11, r.R._m21), new Vector3(r.R._m02, r.R._m12, r.R._m22)),
        new Vector3(r.T.x, r.T.y, r.T.z));

    /// <summary>Makes the carriers match the lanes (creates the missing, frees the gone, poses every one), then rebuilds the mesh if the topology changed. <paramref name="teleport"/> skips the interpolation from the previous pose.</summary>
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
            // A body's bind is its pose the first time the puppet sees it: the creation pose, every joint's zero.
            if (PuppetField.HasShape((Box3DSoaKind)kind[i]) && !_bind.ContainsKey(ids[i]))
                _bind[ids[i]] = Rigid.FromQuaternion(rx[i], ry[i], rz[i], rw[i], new float3(px[i], py[i], pz[i]));
        }

        if (!SignatureHolds(view)) Rebuild(view);
    }

    public override void _Process(double delta)
    {
        if (Shape is null) return;
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < _boneNodes.Length; i++)
        {
            var t = _boneNodes[i].GetGlobalTransformInterpolated();
            _bones[i] = new Projection(t * _inverseBind[i]);
            lo = lo.Min(t.Origin); hi = hi.Max(t.Origin);
        }
        _material.SetShaderParameter("bones", _bones);
        var pad = new Vector3(Shape.Reach, Shape.Reach, Shape.Reach);
        _mesh!.CustomAabb = new Aabb(lo - pad, hi - lo + 2 * pad);
    }

    private static bool IsPuppet(uint kind) => (Box3DSoaKind)kind != Box3DSoaKind.StaticBox;

    private static int CountDynamic(ReadOnlySpan<uint> kind)
    {
        int n = 0;
        foreach (var k in kind) if (IsPuppet(k)) n++;
        return n;
    }

    private bool AllPresent(ReadOnlySpan<ulong> ids, ReadOnlySpan<uint> kind)
    {
        for (int i = 0; i < ids.Length; i++) if (IsPuppet(kind[i]) && !_nodes.ContainsKey(ids[i])) return false;
        return true;
    }

    /// <summary>
    /// Collects the shape bodies and the qualifying joints of the lanes and says whether they are the ordered
    /// (StableId, Kind, Size0..2) and (JointKind, StableId A, StableId B) the mesh was built from.
    /// </summary>
    private bool SignatureHolds(in Box3DSoaView view)
    {
        _shapeLanes.Clear(); _jointLanes.Clear();
        var kind = view.Kind;
        for (int i = 0; i < kind.Length; i++) if (PuppetField.HasShape((Box3DSoaKind)kind[i])) _shapeLanes.Add(i);
        for (int j = 0; j < view.JointCount; j++)
        {
            int a = view.JointBodyA[j], b = view.JointBodyB[j];
            if (a >= 0 && b >= 0 && PuppetField.Qualifies((Box3DSoaJointKind)view.JointKind[j], (Box3DSoaKind)kind[a], (Box3DSoaKind)kind[b])) _jointLanes.Add(j);
        }
        if (!_signed || _shapeLanes.Count != _bodySig.Length || _jointLanes.Count != _jointSig.Length) return false;
        for (int k = 0; k < _shapeLanes.Count; k++)
        {
            int i = _shapeLanes[k];
            if (_bodySig[k] != (view.StableId[i], kind[i], view.Size0[i], view.Size1[i], view.Size2[i])) return false;
        }
        for (int k = 0; k < _jointLanes.Count; k++)
        {
            int j = _jointLanes[k];
            if (_jointSig[k] != (view.JointKind[j], view.StableId[view.JointBodyA[j]], view.StableId[view.JointBodyB[j]])) return false;
        }
        return true;
    }

    private void Rebuild(in Box3DSoaView view)
    {
        var kind = view.Kind;
        _bodySig = new (ulong, uint, float, float, float)[_shapeLanes.Count];
        var indexOf = new Dictionary<int, int>();
        var bodies = new List<PuppetBody>();
        for (int k = 0; k < _shapeLanes.Count; k++)
        {
            int i = _shapeLanes[k];
            _bodySig[k] = (view.StableId[i], kind[i], view.Size0[i], view.Size1[i], view.Size2[i]);
            indexOf[i] = k;
            bodies.Add(new PuppetBody(view.StableId[i], (Box3DSoaKind)kind[i], new float3(view.Size0[i], view.Size1[i], view.Size2[i]), _bind[view.StableId[i]]));
        }
        _jointSig = new (uint, ulong, ulong)[_jointLanes.Count];
        var joints = new List<PuppetJoint>();
        for (int k = 0; k < _jointLanes.Count; k++)
        {
            int j = _jointLanes[k], a = view.JointBodyA[j], b = view.JointBodyB[j];
            var key = (view.JointKind[j], view.StableId[a], view.StableId[b]);
            _jointSig[k] = key;
            // The joint origin is body A's, kept in A's frame so it follows A; the first sight is the creation pose.
            if (!_originLocal.TryGetValue(key, out var local))
                _originLocal[key] = local = _bind[key.Item2].Inverse().Apply(new float3(view.JointOriginX[j], view.JointOriginY[j], view.JointOriginZ[j]));
            joints.Add(new PuppetJoint((Box3DSoaJointKind)key.Item1, indexOf[a], indexOf[b], local));
        }
        _signed = true;

        if (bodies.Count > MaxBones)
        {
            GD.PushError($"BadDay: the puppet has {bodies.Count} shape bodies and the skin has {MaxBones} bones; drawing no puppet.");
            Shape = null; _mesh = null; _skin.Mesh = null;
            return;
        }
        var shape = PuppetField.Build(bodies, joints, Look.SminRadius, Look.JointBallScale, Look.BoxRound);
        shape.Builds = ++_builds;
        Shape = shape;
        _boneNodes = new Node3D[bodies.Count]; _inverseBind = new Transform3D[bodies.Count];
        for (int i = 0; i < bodies.Count; i++) { _boneNodes[i] = _nodes[shape.Ids[i]]; _inverseBind[i] = ToTransform(shape.SpreadBind[i].Inverse()); }
        _mesh = MeshOf(shape);
        _skin.Mesh = _mesh;
    }

    /// <summary>The shape as one surface: VERTEX in the spread bind, the facet NORMAL, and CUSTOM1 (bone A, bone B, weight A).</summary>
    private static ArrayMesh MeshOf(PuppetShape s)
    {
        int n = s.Positions.Length;
        var vertex = new Vector3[n]; var normal = new Vector3[n]; var skin = new float[3 * n];
        for (int i = 0; i < n; i++)
        {
            vertex[i] = new Vector3(s.Positions[i].x, s.Positions[i].y, s.Positions[i].z);
            normal[i] = new Vector3(s.Normals[i].x, s.Normals[i].y, s.Normals[i].z);
            skin[3 * i] = s.BoneA[i]; skin[3 * i + 1] = s.BoneB[i]; skin[3 * i + 2] = s.WeightA[i];
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertex;
        arrays[(int)Mesh.ArrayType.Normal] = normal;
        arrays[(int)Mesh.ArrayType.Custom1] = skin;
        var format = (Mesh.ArrayFormat)((int)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
        return mesh;
    }
}
