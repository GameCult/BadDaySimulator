using System;
using System.Collections.Generic;
using BadDay.Ymir;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// Draws Ymir's world from its lanes and writes nothing back: one carrier Node3D per stable id, posed each frame from the lanes so
/// physics interpolation has something to interpolate. Fella's parts (the stable ids <see cref="SeatedPuppet.PartOf"/> claims) are drawn
/// by <see cref="Skin"/> from their carriers' poses; every other body holds one MeshInstance3D of its own primitive (a capsule, box or sphere
/// of the lane's size), and the anchor, which has no shape, draws nothing.
/// </summary>
public partial class BodyRenderer : Node3D
{
    private readonly Dictionary<ulong, Node3D> _carriers = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly ShaderMaterial _figure = Look.Ps1(Look.SkinTexture, Colors.White);
    private readonly ShaderMaterial _scenery = Look.Ps1(Look.TileTexture, Colors.White, tile: 1f);

    /// <summary>Bodies drawn, static and dynamic.</summary>
    public int BodyCount => _carriers.Count;

    /// <summary>How many primitive MeshInstance3D this renderer has made (Fella's skin is counted by <see cref="FellaSkin.MeshBuilds"/>): a Reset with the same bodies makes none.</summary>
    public int MeshBuilds { get; private set; }

    public ShaderMaterial SceneryMaterial => _scenery;

    /// <summary>Fella's drawn surface: the one owner of how the parts of the seated puppet look.</summary>
    public FellaSkin Skin { get; } = new() { Name = "Skin" };

    /// <summary>The world centre of Fella's drawn bounds as of the last frame, or null before the skin has posed once: where the camera orbits.</summary>
    public Vector3? PuppetCentre => Skin.Centre;

    public override void _Ready() => AddChild(Skin);

    /// <summary>Where body <paramref name="stableId"/> is drawn now, or null if it is not drawn.</summary>
    public Transform3D? PoseOf(ulong stableId) => _carriers.TryGetValue(stableId, out var c) ? c.Transform : null;

    /// <summary>The same pose as rendered this frame, between the last two ticks (a static body does not move).</summary>
    public Transform3D? InterpolatedPoseOf(ulong stableId) => _carriers.TryGetValue(stableId, out var c) ? c.GetGlobalTransformInterpolated() : null;

    /// <summary>Makes the carriers match the lanes (creates the missing, frees the gone, poses every one). <paramref name="teleport"/> skips the interpolation from the previous pose.</summary>
    public void Sync(in Box3DSoaView view, bool teleport = false)
    {
        var ids = view.StableId;
        if (ids.Length != _carriers.Count || !AllPresent(ids))
        {
            _seen.Clear();
            for (int i = 0; i < ids.Length; i++) _seen.Add(ids[i]);
            foreach (var id in new List<ulong>(_carriers.Keys))
            {
                if (_seen.Contains(id)) continue;
                _carriers[id].QueueFree();
                _carriers.Remove(id);
            }
            for (int i = 0; i < ids.Length; i++)
            {
                if (_carriers.ContainsKey(ids[i])) continue;
                var node = new Node3D { Name = $"Body{ids[i]}" };
                var kind = (Box3DSoaKind)view.Kind[i];
                if (SeatedPuppet.PartOf(ids[i]) is null && PrimitiveOf(kind, view.Size0[i], view.Size1[i], view.Size2[i]) is { } primitive)
                {
                    var material = kind != Box3DSoaKind.StaticBox ? _figure : _scenery;
                    node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = primitive, MaterialOverride = material });
                    MeshBuilds++;
                }
                AddChild(node);
                _carriers[ids[i]] = node;
                teleport = true;
            }
        }

        var px = view.PositionX; var py = view.PositionY; var pz = view.PositionZ;
        var rx = view.RotationX; var ry = view.RotationY; var rz = view.RotationZ; var rw = view.RotationW;
        for (int i = 0; i < ids.Length; i++)
        {
            var node = _carriers[ids[i]];
            node.Transform = new Transform3D(new Basis(new Quaternion(rx[i], ry[i], rz[i], rw[i])), new Vector3(px[i], py[i], pz[i]));
            if (teleport) node.ResetPhysicsInterpolation();
        }
    }

    private bool AllPresent(ReadOnlySpan<ulong> ids)
    {
        for (int i = 0; i < ids.Length; i++) if (!_carriers.ContainsKey(ids[i])) return false;
        return true;
    }

    /// <summary>The extra cuts along a box edge of half length <paramref name="half"/>: a cell is at most a metre, so affine texturing on the big floor stays small per cell, as the console's own floors were tiled.</summary>
    private static int Cells(float half) => Math.Max(0, (int)(2f * half) - 1);

    /// <summary>The body's own primitive, or null for a body with no shape. A capsule's lane is its radius and half-length along Y; a box's its half extents.</summary>
    private static Mesh? PrimitiveOf(Box3DSoaKind kind, float size0, float size1, float size2) => kind switch
    {
        Box3DSoaKind.Capsule => new CapsuleMesh { Radius = size0, Height = 2f * (size1 + size0), RadialSegments = 6, Rings = 1 },
        Box3DSoaKind.Box or Box3DSoaKind.StaticBox => new BoxMesh
        {
            Size = new Vector3(2f * size0, 2f * size1, 2f * size2),
            SubdivideWidth = Cells(size0), SubdivideHeight = Cells(size1), SubdivideDepth = Cells(size2),
        },
        Box3DSoaKind.Sphere => new SphereMesh { Radius = size0, Height = 2f * size0, RadialSegments = 6, Rings = 4 },
        _ => null,
    };
}
