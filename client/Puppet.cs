using System.Collections.Generic;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// The puppet's mesh: made from the shape lanes (Kind and Size) and the body transforms (the pose lanes) of every
/// dynamic body, one MeshInstance3D per StableId with flat-shaded low-poly limbs. This class is the only owner of
/// "the puppet's mesh from the lanes"; a different model (the SDF isosurface) replaces it and nothing else.
/// </summary>
public partial class Puppet : Node3D
{
    private readonly Dictionary<ulong, MeshInstance3D> _nodes = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly StandardMaterial3D _dynamic = Look.Matte(Look.Figure);

    public int BodyCount => _nodes.Count;

    /// <summary>The material the puppet is drawn in.</summary>
    public BaseMaterial3D Material => _dynamic;

    /// <summary>Where the puppet draws body <paramref name="stableId"/> now (world), or null if it draws no such body.</summary>
    public Transform3D? PoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.Transform : null;

    /// <summary>The same pose as it is rendered this frame, between the last two ticks.</summary>
    public Transform3D? InterpolatedPoseOf(ulong stableId) => _nodes.TryGetValue(stableId, out var n) ? n.GetGlobalTransformInterpolated() : null;

    /// <summary>Makes the nodes match the lanes: creates the missing, frees the gone, then poses every node. <paramref name="teleport"/> skips the interpolation from the previous pose.</summary>
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
                var node = new MeshInstance3D { Name = $"Body{ids[i]}", Mesh = MeshFor((Box3DSoaKind)kind[i], view.Size0[i], view.Size1[i], view.Size2[i]) };
                node.MaterialOverride = _dynamic;
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

    /// <summary>The mesh for a body: sphere radius Size0; capsule radius Size0 and half-length Size1 along Y (Godot's height counts the caps); box half-extents Size0..2.</summary>
    public static Mesh MeshFor(Box3DSoaKind kind, float size0, float size1, float size2) => kind switch
    {
        Box3DSoaKind.Sphere => FlatShaded(new SphereMesh { Radius = size0, Height = 2f * size0, RadialSegments = 8, Rings = 5 }),
        Box3DSoaKind.Capsule => FlatShaded(new CapsuleMesh { Radius = size0, Height = 2f * (size1 + size0), RadialSegments = 8, Rings = 2 }),
        _ => FlatShaded(new BoxMesh { Size = new Vector3(2f * size0, 2f * size1, 2f * size2) }),
    };

    /// <summary>The same triangles with one normal per face.</summary>
    private static ArrayMesh FlatShaded(PrimitiveMesh source)
    {
        var arrays = source.GetMeshArrays();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetSmoothGroup(uint.MaxValue);
        foreach (int i in indices) st.AddVertex(vertices[i]);
        st.GenerateNormals();
        return st.Commit();
    }

}
