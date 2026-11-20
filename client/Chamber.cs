using System;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>The Synaptic Induction Chamber: the stimulus volume drawn as a translucent box, its glow following the box's intensity.</summary>
public partial class Chamber : Node3D
{
    private readonly MeshInstance3D _volume = new() { Mesh = new BoxMesh { Size = Vector3.One } };
    private readonly OmniLight3D _glow = new() { OmniRange = 0.9f, LightColor = Look.Glow, LightEnergy = 0f };
    private readonly StandardMaterial3D _material = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(Look.Glow, 0.2f),
        EmissionEnabled = true,
        Emission = Look.Glow,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public override void _Ready()
    {
        _volume.MaterialOverride = _material;
        _volume.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_volume);
        AddChild(_glow);
    }

    /// <summary>The material of the volume.</summary>
    public StandardMaterial3D Material => _material;

    /// <summary>The glow last drawn, 0 (dark) to 1.</summary>
    public float Glow { get; private set; }

    public void Sync(BodyPainBoxSession session, double intensity)
    {
        var box = session.Chamber;
        Position = new Vector3(box.CenterX, box.CenterY, box.CenterZ);
        _volume.Scale = new Vector3(2f * box.HalfX, 2f * box.HalfY, 2f * box.HalfZ);
        float glow = (float)Math.Min(1.0, intensity / 1.5);
        Glow = glow;
        _material.AlbedoColor = new Color(Look.Glow, 0.06f + 0.3f * glow);
        _material.EmissionEnergyMultiplier = 0.1f + 2.5f * glow;
        _glow.LightEnergy = 3f * glow;
    }
}
