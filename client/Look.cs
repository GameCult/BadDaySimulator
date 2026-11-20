using Godot;

namespace BadDay.Client;

/// <summary>The scene's stylised look: white on dark grey, flat and high-contrast, one hard light.</summary>
public static class Look
{
    public static readonly Color Background = new(0.10f, 0.10f, 0.11f);
    public static readonly Color Scenery = new(0.15f, 0.15f, 0.16f);
    public static readonly Color Figure = new(0.9f, 0.9f, 0.88f);
    public static readonly Color Glow = new(0.85f, 0.95f, 1f);

    /// <summary>The chamber light at full glow. At 3 it washed the whole dummy out to white (56% of its pixels clipped at near full glow); at 0.1 under 1% clip. RenderCheck bounds the clipped share and the facet contrast.</summary>
    public const float GlowLightEnergy = 0.1f;

    /// <summary>The puppet's blend radius (m): how far limbs flow into the body they hang from.</summary>
    public const float SminRadius = 0.025f;

    /// <summary>The joint ball's radius beyond what touches both limbs, in units of the thinner limb's thickness.</summary>
    public const float JointBallScale = 0.5f;

    /// <summary>The radius (m) the puppet's boxes are rounded by, so the isosurface has no sawtooth bands on their edges.</summary>
    public const float BoxRound = 0.03f;

    /// <summary>
    /// The width (m) each side of a puppet edge over which the facet normal turns to the edge's bisector: 4 mm, the recommended
    /// option of the open question lodur:question:bds-puppet-bevel-width (0 keeps hard facets; 10 mm makes curved limbs read as smooth).
    /// An answer changes this constant and nothing else.
    /// </summary>
    public const float BevelWidth = 0.004f;

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
        LightEnergy = 0.25f, // the sun on a white-ish puppet with the ambient must stay under white, or the facets wash out: RenderCheck bounds the clipped share
        ShadowEnabled = true,
    };

    public static StandardMaterial3D Matte(Color color) => new() { AlbedoColor = color, Roughness = 1f, MetallicSpecular = 0f };
}
