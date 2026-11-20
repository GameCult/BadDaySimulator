using System.Collections.Generic;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// Draws Ymir's world from its lanes and writes nothing back. The static bodies (floor, stool) are plain boxes drawn
/// here; every dynamic body belongs to the <see cref="Puppet"/>, the one swappable owner of the puppet's mesh.
/// </summary>
public partial class BodyRenderer : Node3D
{
    private readonly Puppet _puppet = new() { Name = "Puppet" };
    private readonly Dictionary<ulong, MeshInstance3D> _scenery = new();
    private readonly StandardMaterial3D _material = Look.Matte(Look.Scenery);

    public int NodeCount => _puppet.NodeCount + _scenery.Count;

    public MeshInstance3D? NodeFor(ulong stableId) => _puppet.NodeFor(stableId) ?? _scenery.GetValueOrDefault(stableId);

    public override void _Ready() => AddChild(_puppet);

    public void Sync(in Box3DSoaView view, bool teleport = false)
    {
        _puppet.Sync(view, teleport);
        var ids = view.StableId;
        var kind = view.Kind;
        for (int i = 0; i < ids.Length; i++)
        {
            if ((Box3DSoaKind)kind[i] != Box3DSoaKind.StaticBox) continue;
            if (!_scenery.TryGetValue(ids[i], out var node))
            {
                node = new MeshInstance3D
                {
                    Name = $"Static{ids[i]}",
                    Mesh = new BoxMesh { Size = new Vector3(2f * view.Size0[i], 2f * view.Size1[i], 2f * view.Size2[i]) },
                    MaterialOverride = _material,
                };
                AddChild(node);
                _scenery[ids[i]] = node;
            }
            node.Transform = new Transform3D(
                new Basis(new Quaternion(view.RotationX[i], view.RotationY[i], view.RotationZ[i], view.RotationW[i])),
                new Vector3(view.PositionX[i], view.PositionY[i], view.PositionZ[i]));
        }
    }
}
