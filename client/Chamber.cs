using System;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>The Synaptic Induction Chamber: the stimulus volume drawn as a translucent box, its glow following the box's intensity, and the light it casts: the shader globals ps1_glow_*, which every surface reads per vertex.</summary>
public partial class Chamber : Node3D
{
    private readonly MeshInstance3D _volume = new() { Mesh = new BoxMesh { Size = Vector3.One } };
    private readonly ShaderMaterial _material = Look.Ps1(null, Look.Glow, 0.06f, emissive: true);

    public override void _Ready()
    {
        _volume.MaterialOverride = _material;
        AddChild(_volume);
    }

    /// <summary>The translucent box.</summary>
    public MeshInstance3D Volume => _volume;

    /// <summary>The material of the volume.</summary>
    public ShaderMaterial Material => _material;

    /// <summary>The glow last drawn, 0 (dark) to 1.</summary>
    public float Glow { get; private set; }

    public void Sync(BodyPainBoxSession session)
    {
        double intensity = session.BoxIntensity; // 0 once the box is off
        var box = session.Chamber;
        Position = new Vector3(box.CenterX, box.CenterY, box.CenterZ);
        _volume.Scale = new Vector3(2f * box.HalfX, 2f * box.HalfY, 2f * box.HalfZ);
        float glow = (float)Math.Min(1.0, intensity / 1.5);
        Glow = glow;
        _material.SetShaderParameter("alpha", 0.06f + 0.3f * glow);
        Look.Set("ps1_glow_pos", new Vector3(box.CenterX, box.CenterY, box.CenterZ));
        Look.Set("ps1_glow_color", Look.Rgb(Look.Glow));
        Look.Set("ps1_glow_energy", Look.GlowLightEnergy * glow);
    }
}
