using Godot;

namespace BadDay.Client;

/// <summary>
/// The scene's stylised look: white glazed porcelain on dark grey, lit by a studio the camera carries. The bisque is lit by three
/// directional lights (a shadowing key and two rims) and the glaze reflects the same studio analytically (studio.gdshaderinc), both from
/// the constants here, so the lights and the reflections cannot drift. The clip bound RenderCheck sets is the bisque's; the glaze's
/// highlights are meant to clip, within the bound it sets for them.
/// </summary>
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
    /// The bevel's width each side of a puppet edge, as a fraction of the viewport's height: 2 px of a 720-line screen. The recommended
    /// option (screen-2px) of the open question lodur:question:bds-puppet-bevel-look-width: at the play camera a pixel is about 3.5 mm
    /// and the facets about 11 px, so a fixed 4 mm bevel is one pixel and reads as hard facets, while a width on the screen keeps the soft
    /// edge visible there and narrow close up. 0 with <see cref="BevelMin"/> as the width is a world width; an answer changes these two
    /// constants and nothing else.
    /// </summary>
    public const float BevelScreen = 2f / 720f;

    /// <summary>The narrowest bevel (m) however near the camera is: 3 mm, so a puppet filling the screen still has soft edges.</summary>
    public const float BevelMin = 0.003f;

    /// <summary>The bisque: how rough the unglazed porcelain is, its dielectric specular, and the warm light that passes through it at grazing angles.</summary>
    public const float Roughness = 0.45f, Specular = 0.5f;

    public static readonly Vector3 Backlight = 0.03f * new Vector3(1f, 0.8f, 0.65f);

    /// <summary>How far the albedo pools darker and cooler in a concave crease, and warmer over a convex bevel (0 to 1).</summary>
    public const float GlazePool = 0.8f;

    /// <summary>The glaze's strength: 0 is the bisque alone, 1 the glaze as the studio gives it.</summary>
    public const float GlazeStrength = 1f;

    /// <summary>
    /// The studio's directions, the directions its lights come from, in the camera's space (+Y up, +Z toward the viewer): the key is a soft
    /// box up and left behind the viewer, the rims strips behind the figure on either side.
    /// </summary>
    public static readonly Vector3 KeyDirection = new Vector3(-0.45f, 0.55f, 0.70f).Normalized();
    public static readonly Vector3 RimDirectionA = new Vector3(0.85f, 0.25f, -0.45f).Normalized();
    public static readonly Vector3 RimDirectionB = new Vector3(-0.85f, 0.35f, -0.40f).Normalized();

    /// <summary>What the glaze reflects: the radiance of the key soft box (its half width on the plane at unit distance) and of the rim strips, the dark room and the dim floor.</summary>
    public const float KeyRadiance = 20f, RimRadiance = 12f, KeySize = 0.45f, FloorRadiance = 0.03f;

    public static readonly Vector2 RimSize = new(0.10f, 0.9f);
    public static readonly Color RimTint = new(0.85f, 0.92f, 1f);
    public static readonly Vector3 Room = new(0.02f, 0.02f, 0.022f);

    /// <summary>The punctual lights' energies, tuned with the bisque so it stays under white where the key and a rim overlap; the second rim is dimmer.</summary>
    public const float KeyEnergy = 0.22f, RimEnergyA = 0.3f, RimEnergyB = 0.21f;

    public const float AmbientEnergy = 0.06f;
    public static readonly Color AmbientColor = new(0.9f, 0.93f, 1f);

    /// <summary>The scene's environment (a colour background, a colour ambient, linear, no glow) and the studio's lights, carried by <paramref name="camera"/>: a shadowing key and two rims, its children.</summary>
    public static WorldEnvironment Studio(Camera3D camera)
    {
        // The studio turns with the camera, as a photographer's lights do (lodur:ruling:porcelain-studio-follows-camera): form and glints on every
        // side as the player orbits, at the cost of the key's floor shadow swinging with the camera.
        camera.AddChild(Light("StudioKey", KeyDirection, KeyEnergy, true, new Color(1f, 0.98f, 0.95f)));
        camera.AddChild(Light("StudioRimA", RimDirectionA, RimEnergyA, false, RimTint));
        camera.AddChild(Light("StudioRimB", RimDirectionB, RimEnergyB, false, RimTint));
        return new WorldEnvironment
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
    }

    private static DirectionalLight3D Light(string name, Vector3 from, float energy, bool shadow, Color color) => new()
    {
        Name = name,
        Transform = Transform3D.Identity.LookingAt(-from, Vector3.Up), // shines along -from: the light comes from `from`
        LightEnergy = energy,
        LightColor = color,
        ShadowEnabled = shadow,
    };

    /// <summary>Sets the puppet shader's bisque and studio uniforms.</summary>
    public static void Glaze(ShaderMaterial material)
    {
        material.SetShaderParameter("roughness", Roughness);
        material.SetShaderParameter("specular", Specular);
        material.SetShaderParameter("backlight", Backlight);
        material.SetShaderParameter("glaze", GlazeStrength);
        material.SetShaderParameter("glaze_pool", GlazePool);
        material.SetShaderParameter("studio_room", Room);
        material.SetShaderParameter("studio_key_dir", KeyDirection);
        material.SetShaderParameter("studio_key_size", KeySize);
        material.SetShaderParameter("studio_key_radiance", KeyRadiance);
        material.SetShaderParameter("studio_rim_a", RimDirectionA);
        material.SetShaderParameter("studio_rim_b", RimDirectionB);
        material.SetShaderParameter("studio_rim_size", RimSize);
        material.SetShaderParameter("studio_rim_tint", new Vector3(RimTint.R, RimTint.G, RimTint.B));
        material.SetShaderParameter("studio_rim_radiance", RimRadiance);
        material.SetShaderParameter("studio_floor", FloorRadiance);
    }

    public static StandardMaterial3D Matte(Color color) => new() { AlbedoColor = color, Roughness = 1f, MetallicSpecular = 0f };
}
