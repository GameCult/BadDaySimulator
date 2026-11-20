using System.Collections.Generic;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// Draws Ymir's world from its lanes and writes nothing back. The static bodies (floor, stool) are plain boxes drawn
/// here; every dynamic body belongs to the <see cref="Puppet"/>, which draws them as one skinned SDF mesh.
/// </summary>
public partial class BodyRenderer : Node3D
{
    private readonly Puppet _puppet = new() { Name = "Puppet" };
    private readonly Dictionary<ulong, MeshInstance3D> _scenery = new();
    private readonly StandardMaterial3D _material = Look.Matte(Look.Scenery);

    /// <summary>Bodies drawn, static and dynamic.</summary>
    public int BodyCount => _puppet.BodyCount + _scenery.Count;

    /// <summary>The colour the puppet is drawn in.</summary>
    public Color PuppetColor => _puppet.Color;
    /// <summary>What the puppet's mesh is made of now, or null while it draws none.</summary>
    public PuppetShape? PuppetShape => _puppet.Shape;

    /// <summary>The world centre of the puppet's drawn bounds, or null before the first draw: where the camera orbits.</summary>
    public Vector3? PuppetCentre => _puppet.Centre;

    /// <summary>How many times the puppet's Godot mesh has been built.</summary>
    public int PuppetMeshBuilds => _puppet.MeshBuilds;

    public BaseMaterial3D SceneryMaterial => _material;

    /// <summary>Where body <paramref name="stableId"/> is drawn now, or null if it is not drawn.</summary>
    public Transform3D? PoseOf(ulong stableId) => _puppet.PoseOf(stableId) ?? (_scenery.TryGetValue(stableId, out var n) ? n.Transform : null);

    /// <summary>The same pose as rendered this frame, between the last two ticks (a static body does not move).</summary>
    public Transform3D? InterpolatedPoseOf(ulong stableId) => _puppet.InterpolatedPoseOf(stableId) ?? PoseOf(stableId);

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
