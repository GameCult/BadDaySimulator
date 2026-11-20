using System.Collections.Generic;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// Draws Ymir's world: one MeshInstance3D per StableId, its mesh from the Kind and Size lanes, its transform from the
/// pose lanes every tick. The renderer owns no dimensions and writes nothing back; the lanes are the only source.
/// Meshes are stylised low poly with flat normals.
/// </summary>
public partial class BodyRenderer : Node3D
{
    private readonly Dictionary<ulong, MeshInstance3D> _nodes = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly StandardMaterial3D _dynamic = Flat(new Color(0.88f, 0.10f, 0.08f));
    private readonly StandardMaterial3D _fixed = Flat(new Color(0.74f, 0.74f, 0.72f));

    public int NodeCount => _nodes.Count;

    public MeshInstance3D? NodeFor(ulong stableId) => _nodes.GetValueOrDefault(stableId);

    /// <summary>Makes the nodes match the lanes: creates the missing, frees the gone, then poses every node. <paramref name="teleport"/> skips the interpolation from the previous pose.</summary>
    public void Sync(in Box3DSoaView view, bool teleport = false)
    {
        var ids = view.StableId;
        var kind = view.Kind;
        if (ids.Length != _nodes.Count || !AllPresent(ids))
        {
            _seen.Clear();
            for (int i = 0; i < ids.Length; i++) _seen.Add(ids[i]);
            foreach (var id in new List<ulong>(_nodes.Keys))
            {
                if (_seen.Contains(id)) continue;
                _nodes[id].QueueFree();
                _nodes.Remove(id);
            }
            for (int i = 0; i < ids.Length; i++)
            {
                if (_nodes.ContainsKey(ids[i])) continue;
                var node = new MeshInstance3D { Name = $"Body{ids[i]}", Mesh = MeshFor((Box3DSoaKind)kind[i], view.Size0[i], view.Size1[i], view.Size2[i]) };
                node.MaterialOverride = (Box3DSoaKind)kind[i] == Box3DSoaKind.StaticBox ? _fixed : _dynamic;
                AddChild(node);
                _nodes[ids[i]] = node;
                teleport = true;
            }
        }

        var px = view.PositionX; var py = view.PositionY; var pz = view.PositionZ;
        var rx = view.RotationX; var ry = view.RotationY; var rz = view.RotationZ; var rw = view.RotationW;
        for (int i = 0; i < ids.Length; i++)
        {
            var node = _nodes[ids[i]];
            node.Transform = new Transform3D(new Basis(new Quaternion(rx[i], ry[i], rz[i], rw[i])), new Vector3(px[i], py[i], pz[i]));
            if (teleport) node.ResetPhysicsInterpolation();
        }
    }

    private bool AllPresent(System.ReadOnlySpan<ulong> ids)
    {
        foreach (var id in ids) if (!_nodes.ContainsKey(id)) return false;
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

    private static StandardMaterial3D Flat(Color color) => new()
    {
        AlbedoColor = color,
        Roughness = 1f,
        MetallicSpecular = 0f,
    };
}
