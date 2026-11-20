using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CultMath;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// The puppet: its drawn surface is one SDF isosurface mesh (<see cref="PuppetField"/>) over the dynamic bodies, flat-shaded
/// with a soft bevel, built once and rebuilt only when the topology signature (the shape bodies and the joints that blend them)
/// changes. Per-StableId Node3D carriers hold the pose lanes so physics interpolation has something to interpolate, and every
/// frame <see cref="PuppetSkin"/> skins the surface on the CPU by those carriers' interpolated transforms relative to the spread
/// bind pose and derives its facets, creases and vertex normals; this class uploads the result into the mesh's two streams in place.
/// </summary>
public partial class Puppet : Node3D
{
    /// <summary>Bytes per triangle corner in the vertex stream (VERTEX, float3) and in the attribute stream (CUSTOM0 float4, CUSTOM1..3 float3 each).</summary>
    private const int VertexBytes = 12, AttributeBytes = 52;

    private readonly Dictionary<ulong, Node3D> _nodes = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly ShaderMaterial _material = new() { Shader = GD.Load<Shader>("res://puppet.gdshader") };
    private readonly MeshInstance3D _skin = new MeshInstance3D { Name = "Skin" };
    private readonly PuppetModel _model = new(Look.SminRadius, Look.JointBallScale, Look.BoxRound);
    private ArrayMesh? _mesh;
    private Node3D[] _boneNodes = Array.Empty<Node3D>();
    private Transform3D[] _inverseBind = Array.Empty<Transform3D>();
    private Rigid[] _bones = Array.Empty<Rigid>();
    private PuppetSkin? _surface;
    private byte[] _vertexStream = Array.Empty<byte>(), _attributeStream = Array.Empty<byte>();

    public Puppet()
    {
        _material.SetShaderParameter("albedo", Look.Figure);
        _material.SetShaderParameter("bevel_screen", Look.BevelScreen);
        _material.SetShaderParameter("bevel_min", Look.BevelMin);
        Look.Glaze(_material);
        _skin.MaterialOverride = _material;
    }

    public int BodyCount => _nodes.Count;

    /// <summary>How many times the Godot mesh has been made from a shape: the one build counter, which the smoke asserts.</summary>
    public int MeshBuilds { get; private set; }

    /// <summary>The colour the puppet is drawn in: the shader's albedo.</summary>
    public Color Color => _material.GetShaderParameter("albedo").AsColor();

    /// <summary>The glaze on (Look's strength) or off, the bisque alone: RenderCheck's bounds on the lit facets are the bisque's.</summary>
    public bool Glazed { set => _material.SetShaderParameter("glaze", value ? Look.GlazeStrength : 0f); }

    /// <summary>Draws the shading normal (in view space, as n * 0.5 + 0.5) instead of the lit puppet: RenderCheck's view of what the bevel did.</summary>
    public bool ShowNormals { set => _material.SetShaderParameter("show_normals", value); }

    /// <summary>What the puppet is made of now, or null before the first build or while it draws nothing (too many bodies).</summary>
    public PuppetShape? Shape => _model.Shape;

    /// <summary>The world centre of the puppet's drawn bounds (the mesh's <c>CustomAabb</c>, in the skin's space), or null before the first draw.</summary>
    public Vector3? Centre => _mesh is { CustomAabb: { } box } && box.HasSurface() ? _skin.GlobalTransform * (box.Position + box.Size / 2) : null;

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
        if (_surface is null || _mesh is null) return;
        // The bones take a body from the spread bind to its pose in the skin's model space, which Godot then carries to the world with
        // the skin's own transform: the puppet is right under any parent, not only at the origin.
        var toModel = _skin.GetGlobalTransformInterpolated().AffineInverse();
        for (int i = 0; i < _boneNodes.Length; i++) _bones[i] = ToRigid(toModel * _boneNodes[i].GetGlobalTransformInterpolated() * _inverseBind[i]);
        _surface.Update(_bones);
        Pack(_surface, Shape!.Corners, _vertexStream, _attributeStream);
        var rid = _mesh.GetRid();
        RenderingServer.MeshSurfaceUpdateVertexRegion(rid, 0, 0, _vertexStream);
        RenderingServer.MeshSurfaceUpdateAttributeRegion(rid, 0, 0, _attributeStream);
        var lo = _surface.Lo; var hi = _surface.Hi;
        _mesh.CustomAabb = new Aabb(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z));
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

    /// <summary>Takes up the model's new shape: the carriers that are its bones, their inverse bind, the skin's buffers and the mesh layout.</summary>
    private void Rebuild()
    {
        var shape = _model.Shape!;
        _boneNodes = new Node3D[shape.Ids.Length]; _inverseBind = new Transform3D[shape.Ids.Length]; _bones = new Rigid[shape.Ids.Length];
        for (int i = 0; i < shape.Ids.Length; i++) { _boneNodes[i] = _nodes[shape.Ids[i]]; _inverseBind[i] = ToTransform(shape.SpreadBind[i].Inverse()); }
        _surface = new PuppetSkin(shape);
        _vertexStream = new byte[VertexBytes * shape.Corners.Length]; _attributeStream = new byte[AttributeBytes * shape.Corners.Length];
        _mesh = LayoutOf(shape.Corners.Length);
        var surface = RenderingServer.MeshGetSurface(_mesh.GetRid(), 0);
        int vertexLength = surface["vertex_data"].AsByteArray().Length, attributeLength = surface["attribute_data"].AsByteArray().Length;
        if (vertexLength != _vertexStream.Length || attributeLength != _attributeStream.Length)
            throw new InvalidOperationException($"BadDay: the puppet mesh holds {vertexLength} and {attributeLength} bytes of vertex and attribute data, not the {_vertexStream.Length} and {_attributeStream.Length} it is filled with.");
        MeshBuilds++;
        _skin.Mesh = _mesh;
    }

    /// <summary>
    /// A surface of <paramref name="corners"/> vertices that only fixes the layout the frames fill in place (nothing is rebuilt per frame):
    /// the vertex stream is VERTEX alone, the attribute stream CUSTOM0 (RgbaFloat: component k holds the altitude h_k of corner k, w the
    /// triangle's smallest altitude), CUSTOM1 (the triangle's three creases), CUSTOM2 (the facet normal) and CUSTOM3 (the corner's vertex
    /// normal), all in the skin's model space.
    /// </summary>
    private static ArrayMesh LayoutOf(int corners)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[corners];
        arrays[(int)Mesh.ArrayType.Custom0] = new float[4 * corners];
        arrays[(int)Mesh.ArrayType.Custom1] = new float[3 * corners];
        arrays[(int)Mesh.ArrayType.Custom2] = new float[3 * corners];
        arrays[(int)Mesh.ArrayType.Custom3] = new float[3 * corners];
        var format = (Mesh.ArrayFormat)(((int)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
            | ((int)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
            | ((int)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift)
            | ((int)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom3Shift));
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
        return mesh;
    }

    /// <summary>Writes the skin's surface, one triangle corner after another, into the two streams (no allocation).</summary>
    private static void Pack(PuppetSkin skin, int[] corners, byte[] vertexStream, byte[] attributeStream)
    {
        var vertex = MemoryMarshal.Cast<byte, float>(vertexStream);
        var attribute = MemoryMarshal.Cast<byte, float>(attributeStream);
        for (int t = 0; t < corners.Length / 3; t++)
        {
            var facet = skin.Facet[t];
            for (int k = 0; k < 3; k++)
            {
                int slot = 3 * t + k, c = corners[slot];
                var p = skin.Skinned[c];
                vertex[3 * slot] = p.x; vertex[3 * slot + 1] = p.y; vertex[3 * slot + 2] = p.z;
                int o = AttributeBytes / 4 * slot;
                attribute[o] = k == 0 ? skin.Altitude[3 * t] : 0; attribute[o + 1] = k == 1 ? skin.Altitude[3 * t + 1] : 0; attribute[o + 2] = k == 2 ? skin.Altitude[3 * t + 2] : 0;
                attribute[o + 3] = skin.SmallestAltitude[t];
                attribute[o + 4] = skin.Crease[3 * t]; attribute[o + 5] = skin.Crease[3 * t + 1]; attribute[o + 6] = skin.Crease[3 * t + 2];
                attribute[o + 7] = facet.x; attribute[o + 8] = facet.y; attribute[o + 9] = facet.z;
                var n = skin.VertexNormal[c];
                attribute[o + 10] = n.x; attribute[o + 11] = n.y; attribute[o + 12] = n.z;
            }
        }
    }
}
