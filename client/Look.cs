using Godot;

namespace BadDay.Client;

/// <summary>The scene's stylised look: a bright flat world, one hard light, saturated red figures.</summary>
public static class Look
{
    public static Godot.Environment Environment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = new Color(0.94f, 0.94f, 0.92f),
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(1f, 1f, 1f),
        AmbientLightEnergy = 0.3f,
    };

    public static DirectionalLight3D Sun() => new()
    {
        RotationDegrees = new Vector3(-50f, 35f, 0f),
        LightEnergy = 1.5f,
        ShadowEnabled = true,
    };
}
