using Godot;

namespace BadDay.Client;

/// <summary>The scene's stylised look: white on dark grey, flat and high-contrast, one hard light.</summary>
public static class Look
{
    public static readonly Color Background = new(0.10f, 0.10f, 0.11f);
    public static readonly Color Scenery = new(0.15f, 0.15f, 0.16f);
    public static readonly Color Figure = new(0.9f, 0.9f, 0.88f);
    public static readonly Color Glow = new(0.85f, 0.95f, 1f);

    public static Godot.Environment Environment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = Background,
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(1f, 1f, 1f),
        AmbientLightEnergy = 0.2f,
    };

    public static DirectionalLight3D Sun() => new()
    {
        RotationDegrees = new Vector3(-50f, 35f, 0f),
        LightEnergy = 0.5f,
        ShadowEnabled = true,
    };

    public static StandardMaterial3D Matte(Color color) => new() { AlbedoColor = color, Roughness = 1f, MetallicSpecular = 0f };
}
