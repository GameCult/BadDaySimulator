using System;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>The Synaptic Induction Chamber: the stimulus volume drawn as a translucent box, its glow following the box's intensity.</summary>
public partial class Chamber : Node3D
{
    private readonly MeshInstance3D _volume = new() { Mesh = new BoxMesh { Size = Vector3.One } };
    private readonly OmniLight3D _glow = new() { OmniRange = 1.6f, LightColor = new Color(1f, 0.85f, 0.15f), LightEnergy = 0f };
    private readonly StandardMaterial3D _material = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1f, 0.85f, 0.15f, 0.2f),
        EmissionEnabled = true,
        Emission = new Color(1f, 0.85f, 0.15f),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public override void _Ready()
    {
        _volume.MaterialOverride = _material;
        _volume.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_volume);
        AddChild(_glow);
    }

    public void Sync(BodyPainBoxSession session)
    {
        var box = session.Chamber;
        Position = new Vector3(box.CenterX, box.CenterY, box.CenterZ);
        _volume.Scale = new Vector3(2f * box.HalfX, 2f * box.HalfY, 2f * box.HalfZ);
        float glow = (float)Math.Min(1.0, box.Intensity / 1.5);
        _material.AlbedoColor = new Color(1f, 0.85f, 0.15f, 0.06f + 0.3f * glow);
        _material.EmissionEnergyMultiplier = 0.1f + 2.5f * glow;
        _glow.LightEnergy = 3f * glow;
    }
}
