using Godot;

namespace BadDay.Client;

/// <summary>The scene's colours and its colour environment: dark grey, a colour ambient, linear tone mapping, no sky and no glow.</summary>
public static class Look
{
    public static readonly Color Background = new(0.10f, 0.10f, 0.11f);
    public static readonly Color Scenery = new(0.15f, 0.15f, 0.16f);
    public static readonly Color Figure = new(0.9f, 0.9f, 0.88f);
    public static readonly Color Glow = new(0.85f, 0.95f, 1f);

    /// <summary>The chamber light at full glow. At 3 it washed the whole dummy out to white (56% of its pixels clipped at near full glow); at 0.1 under 1% clip.</summary>
    public const float GlowLightEnergy = 0.1f;

    public const float AmbientEnergy = 0.06f;
    public static readonly Color AmbientColor = new(0.9f, 0.93f, 1f);

    /// <summary>The scene's environment: a colour background, a colour ambient, linear, no glow.</summary>
    public static WorldEnvironment World() => new()
    {
        Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = AmbientColor,
            AmbientLightEnergy = AmbientEnergy,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        },
    };

    public static StandardMaterial3D Matte(Color color) => new() { AlbedoColor = color, Roughness = 1f, MetallicSpecular = 0f };
}
