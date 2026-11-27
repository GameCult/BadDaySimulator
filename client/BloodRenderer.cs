using System;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client;

/// <summary>
/// Draws the body's blood from Ymir's lanes and writes nothing back: one MultiMesh of the live fluid drops (a small box of side 2 x the
/// particle's radius) and one of the wet patches (a flat disc of the patch's radius, lying horizontal at the patch's point carried by the body it
/// is on, in that body's pose). The drawing is not scaled (lodur:ruling:bds-blood-gain-simulated): the volume the physiology loses is
/// scaled where the blood is emitted, and what is shown is what the world holds. Splat decals, painted textures and billboards are a later cut.
/// </summary>
public partial class BloodRenderer : Node3D
{
    /// <summary>A patch is a disc this thick (m), lifted this far (m) so it does not z-fight the surface it lies on.</summary>
    private const float PatchThickness = 0.004f, PatchLift = 0.003f;

    private readonly MultiMeshInstance3D _drops = Instances(new BoxMesh { Size = Vector3.One }, "Drops");
    private readonly MultiMeshInstance3D _patches = Instances(new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 1f, RadialSegments = 8, Rings = 0 }, "Patches");

    /// <summary>Drops and wet patches drawn by the latest <see cref="Sync"/>: the live fluid lanes and the live wet lanes.</summary>
    public int DropCount { get; private set; }
    public int PatchCount { get; private set; }

    public override void _Ready()
    {
        var material = Look.Ps1(null, Look.Blood);
        foreach (var node in new[] { _drops, _patches })
        {
            node.MaterialOverride = material;
            node.Multimesh.CustomAabb = new Aabb(new Vector3(-50, -5, -50), new Vector3(100, 20, 100)); // instances move every frame: never culled on a stale box
        }
        AddChild(_drops);
        AddChild(_patches);
    }

    public void Sync(in Box3DSoaView view)
    {
        var live = view.ParticleLive;
        var phase = view.ParticlePhase;
        var radius = view.ParticleRadius;
        int drops = 0;
        for (int i = 0; i < live.Length; i++) if (live[i] != 0 && (phase[i] & Box3DSoaPhase.Fluid) != 0) drops++;
        Reserve(_drops.Multimesh, drops);
        int d = 0;
        for (int i = 0; i < live.Length && d < drops; i++)
        {
            if (live[i] == 0 || (phase[i] & Box3DSoaPhase.Fluid) == 0) continue;
            _drops.Multimesh.SetInstanceTransform(d++, new Transform3D(Basis.FromScale(Vector3.One * 2f * radius[i]), new Vector3(view.ParticleX[i], view.ParticleY[i], view.ParticleZ[i])));
        }
        DropCount = drops;
        _drops.Multimesh.VisibleInstanceCount = drops;

        var wet = view.WetLive;
        int patches = 0;
        for (int i = 0; i < wet.Length; i++) if (wet[i] != 0) patches++;
        Reserve(_patches.Multimesh, patches);
        int p = 0;
        for (int i = 0; i < wet.Length && p < patches; i++)
        {
            if (wet[i] == 0) continue;
            var local = new Vector3(view.WetX[i], view.WetY[i], view.WetZ[i]);
            Vector3 at;
            int body = view.WetBody[i], particle = view.WetParticle[i];
            if (body >= 0)
            {
                var q = new Quaternion(view.RotationX[body], view.RotationY[body], view.RotationZ[body], view.RotationW[body]);
                at = new Vector3(view.PositionX[body], view.PositionY[body], view.PositionZ[body]) + q * local;
            }
            else if (particle >= 0) at = new Vector3(view.ParticleX[particle], view.ParticleY[particle], view.ParticleZ[particle]);
            else at = local;
            float r = view.WetRadius[i];
            _patches.Multimesh.SetInstanceTransform(p++, new Transform3D(Basis.FromScale(new Vector3(r, PatchThickness, r)), at + Vector3.Up * PatchLift));
        }
        PatchCount = patches;
        _patches.Multimesh.VisibleInstanceCount = patches;
    }

    /// <summary>Grows the multimesh's capacity (doubling) so <paramref name="count"/> instances fit; it never shrinks, and only the visible ones are drawn.</summary>
    private static void Reserve(MultiMesh mm, int count)
    {
        if (count <= mm.InstanceCount) return;
        mm.InstanceCount = Math.Max(count, Math.Max(16, mm.InstanceCount * 2));
    }

    private static MultiMeshInstance3D Instances(Mesh mesh, string name) => new()
    {
        Name = name,
        Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh },
    };
}
