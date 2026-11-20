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
    private readonly Dictionary<ulong, Node3D> _nodes = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly ShaderMaterial _material = new() { Shader = GD.Load<Shader>("res://puppet.gdshader") };
    private readonly MeshInstance3D _skin = new MeshInstance3D { Name = "Skin" };
    private readonly Godot.Collections.Array _bones = new();
    private readonly PuppetModel _model = new(Look.SminRadius, Look.JointBallScale, Look.BoxRound);
    private ArrayMesh? _mesh;
    private Node3D[] _boneNodes = Array.Empty<Node3D>();
    private Transform3D[] _inverseBind = Array.Empty<Transform3D>();

    public Puppet()
    {
        _material.SetShaderParameter("albedo", Look.Figure);
        _material.SetShaderParameter("bevel_width", Look.BevelWidth);
        for (int i = 0; i < PuppetModel.MaxBones; i++) _bones.Add(new Projection(Transform3D.Identity));
        _skin.MaterialOverride = _material;
    }

    public int BodyCount => _nodes.Count;

    /// <summary>How many times the Godot mesh has been made from a shape: the one build counter, which the smoke asserts.</summary>
    public int MeshBuilds { get; private set; }

    /// <summary>The colour the puppet is drawn in: the shader's albedo.</summary>
    public Color Color => _material.GetShaderParameter("albedo").AsColor();

    /// <summary>Draws the shading normal (in view space, as n * 0.5 + 0.5) instead of the lit puppet: RenderCheck's view of what the bevel did.</summary>
    public bool ShowNormals { set => _material.SetShaderParameter("show_normals", value); }

    /// <summary>What the puppet is made of now, or null before the first build or while it draws nothing (too many bodies).</summary>
    public PuppetShape? Shape => _model.Shape;

    public override void _Ready() => AddChild(_skin);

    /// <summary>Where the puppet draws body <paramref name="stableId"/> now (world), or null if it draws no such body.</summary>
    public Transform3D? PoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.Transform : null;

    /// <summary>The same pose as it is rendered this frame, between the last two ticks.</summary>
    public Transform3D? InterpolatedPoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.GetGlobalTransformInterpolated() : null;

    /// <summary>The Godot transform of a <see cref="Rigid"/>.</summary>
    public static Transform3D ToTransform(Rigid r) => new(
        new Basis(new Vector3(r.R._m00, r.R._m10, r.R._m20), new Vector3(r.R._m01, r.R._m11, r.R._m21), new Vector3(r.R._m02, r.R._m12, r.R._m22)),
        new Vector3(r.T.x, r.T.y, r.T.z));

    /// <summary>The <see cref="Rigid"/> of a Godot transform (rotation only; no scale).</summary>
    public static Rigid ToRigid(Transform3D t) => new(
        new CultMath.float3x3(new CultMath.float3(t.Basis.Row0.X, t.Basis.Row0.Y, t.Basis.Row0.Z), new CultMath.float3(t.Basis.Row1.X, t.Basis.Row1.Y, t.Basis.Row1.Z), new CultMath.float3(t.Basis.Row2.X, t.Basis.Row2.Y, t.Basis.Row2.Z)),
        new CultMath.float3(t.Origin.X, t.Origin.Y, t.Origin.Z));

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
        }

        if (_model.Update(view)) Rebuild();
    }

    public override void _Process(double delta)
    {
        if (Shape is null) return;
        // The bones take a body from the spread bind to its world pose. VERTEX is in the skin's model space, which Godot then
        // carries to the world with the skin's own transform, so the bones are taken into that space first: the puppet is right
        // under any parent, not only at the origin.
        var toModel = _skin.GetGlobalTransformInterpolated().AffineInverse();
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < _boneNodes.Length; i++)
        {
            var t = toModel * _boneNodes[i].GetGlobalTransformInterpolated();
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

    /// <summary>Takes up the model's new shape: the carriers that are its bones, their inverse bind, and the mesh.</summary>
    private void Rebuild()
    {
        if (_model.Shape is not { } shape)
        {
            GD.PushError($"BadDay: the puppet has more than {PuppetModel.MaxBones} shape bodies, which is the skin's bone count; drawing no puppet.");
            _mesh = null; _skin.Mesh = null;
            return;
        }
        _boneNodes = new Node3D[shape.Ids.Length]; _inverseBind = new Transform3D[shape.Ids.Length];
        for (int i = 0; i < shape.Ids.Length; i++) { _boneNodes[i] = _nodes[shape.Ids[i]]; _inverseBind[i] = ToTransform(shape.SpreadBind[i].Inverse()); }
        _mesh = MeshOf(shape);
        MeshBuilds++;
        _skin.Mesh = _mesh;
    }

    /// <summary>
    /// The shape as one surface: VERTEX in the spread bind, the facet NORMAL, CUSTOM0 (the edge-distance basis: vertex k of a triangle holds
    /// its altitude h_k = 2 * area / |edge k| in component k and 0 in the others, the triangle's smallest altitude in w), CUSTOM1 (bone A,
    /// bone B, weight A) and CUSTOM2 (the triangle's three creases: zero until PuppetSkin derives them, so the bevel does nothing here).
    /// </summary>
    private static ArrayMesh MeshOf(PuppetShape s)
    {
        int n = s.Corners.Length;
        var vertex = new Vector3[n]; var normal = new Vector3[n]; var edge = new float[4 * n]; var skin = new float[3 * n]; var crease = new float[3 * n];
        for (int t = 0; t < n; t += 3)
        {
            var p = new[] { s.Vertices[s.Corners[t]], s.Vertices[s.Corners[t + 1]], s.Vertices[s.Corners[t + 2]] };
            var cross = CultMath.math.cross(p[1] - p[0], p[2] - p[0]);
            float twiceArea = CultMath.math.length(cross);
            var facet = -cross / twiceArea;
            var h = new float[3];
            for (int k = 0; k < 3; k++)
            {
                float length = CultMath.math.length(p[(k + 1) % 3] - p[(k + 2) % 3]);
                h[k] = length > 0 ? twiceArea / length : 0;
            }
            float smallest = Mathf.Min(h[0], Mathf.Min(h[1], h[2]));
            for (int k = 0; k < 3; k++)
            {
                int i = t + k, c = s.Corners[i];
                vertex[i] = new Vector3(p[k].x, p[k].y, p[k].z);
                normal[i] = new Vector3(facet.x, facet.y, facet.z);
                skin[3 * i] = s.BoneA[c]; skin[3 * i + 1] = s.BoneB[c]; skin[3 * i + 2] = s.WeightA[c];
                edge[4 * i + k] = h[k]; edge[4 * i + 3] = smallest;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertex;
        arrays[(int)Mesh.ArrayType.Normal] = normal;
        arrays[(int)Mesh.ArrayType.Custom0] = edge;
        arrays[(int)Mesh.ArrayType.Custom1] = skin;
        arrays[(int)Mesh.ArrayType.Custom2] = crease;
        var format = (Mesh.ArrayFormat)(((int)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
            | ((int)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
            | ((int)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift));
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
        return mesh;
    }
}
