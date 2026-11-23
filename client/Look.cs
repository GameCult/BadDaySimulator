using System;
using Godot;

namespace BadDay.Client;

/// <summary>
/// The scene's look: PlayStation-1-era 3D (lodur:ruling:bds-look-ps1-anatomy). Every number the look has is here. ps1.gdshader draws every
/// surface (vertices snapped to the line grid, affine texture, unfiltered texels, Gouraud light and fog, all per vertex) and ps1_post.gdshader
/// the framebuffer (15-bit colour, ordered dither, one colour per block of the grid). Colours are raw sRGB values: the console's arithmetic
/// had no colour management and neither does the shader.
/// </summary>
public static class Look
{
    /// <summary>The framebuffer's height in lines (lodur:question:bds-render-lines): the window is divided into blocks of round(H / Lines) pixels.</summary>
    public const int Lines = 240;

    public static readonly Color Background = new(0.10f, 0.10f, 0.11f);
    /// <summary>The floor's tile: noise on this colour, grouted with <see cref="Grout"/>.</summary>
    public static readonly Color Scenery = Color.Color8(0x1b, 0x26, 0x32);
    public static readonly Color Grout = Color.Color8(0x0d, 0x15, 0x1d);
    public static readonly Color Skin = Color.Color8(196, 146, 120);
    public static readonly Color Glow = new(0.85f, 0.95f, 1f);

    /// <summary>The key light's direction (where it comes from, in the world), its colour and the ambient: ps1.js's.</summary>
    public static readonly Vector3 KeyDirection = new Vector3(-0.4f, 0.8f, 0.45f).Normalized();
    public static readonly Color KeyColor = new(0.95f, 0.90f, 0.82f);
    public static readonly Color AmbientColor = new(0.30f, 0.34f, 0.42f);

    /// <summary>Fog toward <see cref="Background"/>, by view depth in metres.</summary>
    public const float FogNear = 4f, FogFar = 14f;

    /// <summary>The chamber's light at full glow (it reaches 0.9 m): added per vertex to the key and the ambient.</summary>
    public const float GlowLightEnergy = 4f;

    /// <summary>Pixels per block of the line grid for a window <paramref name="height"/> pixels high: never less than 1.</summary>
    public static int BlockSize(int height) => Math.Max(1, (int)Math.Round(height / (double)Lines, MidpointRounding.AwayFromZero));

    private static ImageTexture? _skinTexture, _tileTexture;

    /// <summary>The skin: 8 x 8 texels of <see cref="Skin"/> with a little noise.</summary>
    public static ImageTexture SkinTexture => _skinTexture ??= Noise(8, 8, Skin, 0.035f, 7);

    /// <summary>The floor's tile: 32 x 32 texels of <see cref="Scenery"/> with noise and a texel of grout on two edges.</summary>
    public static ImageTexture TileTexture => _tileTexture ??= Noise(32, 32, Scenery, 0.02f, 11, (x, y) => x == 0 || y == 0, Grout);

    /// <summary>The scene's environment: a colour background, linear, no sky and no glow. Every surface is unshaded, so it lights nothing.</summary>
    public static WorldEnvironment World() => new()
    {
        Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Background,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        },
    };

    /// <summary>Sets the look's constant shader globals, once. Main sets ps1_res, Chamber the glow.</summary>
    public static void Apply()
    {
        Set("ps1_key_dir", KeyDirection);
        Set("ps1_key_color", Rgb(KeyColor));
        Set("ps1_ambient", Rgb(AmbientColor));
        Set("ps1_fog_color", Rgb(Background));
        Set("ps1_fog_near", FogNear);
        Set("ps1_fog_far", FogFar);
    }

    public static void Set(string name, Variant value) => RenderingServer.GlobalShaderParameterSet(name, value);

    public static Vector3 Rgb(Color c) => new(c.R, c.G, c.B);

    /// <summary>
    /// The PS1 material: <paramref name="texture"/> (white if null) times <paramref name="tint"/>, lit and fogged per vertex. Below alpha 1 it is
    /// the translucent variant of the same shader. <paramref name="tile"/> is the world's metres per texture repeat (0: the mesh's UV).
    /// </summary>
    public static ShaderMaterial Ps1(Texture2D? texture, Color tint, float alpha = 1f, bool emissive = false, float tile = 0f)
    {
        var source = GD.Load<Shader>("res://ps1.gdshader");
        var shader = alpha < 1f ? new Shader { Code = "#define TRANSLUCENT\n" + source.Code } : source;
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("albedo_tex", texture);
        material.SetShaderParameter("tint", Rgb(tint));
        material.SetShaderParameter("alpha", alpha);
        material.SetShaderParameter("emissive", emissive);
        material.SetShaderParameter("tile", tile);
        return material;
    }

    private static ImageTexture Noise(int w, int h, Color baseColour, float spread, ulong seed, Func<int, int, bool>? grouted = null, Color? groutColour = null)
    {
        var rng = new RandomNumberGenerator { Seed = seed };
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = grouted?.Invoke(x, y) == true ? groutColour ?? baseColour : baseColour;
                float k = rng.RandfRange(-spread, spread);
                image.SetPixel(x, y, new Color(Math.Clamp(c.R + k, 0, 1), Math.Clamp(c.G + k, 0, 1), Math.Clamp(c.B + k, 0, 1)));
            }
        return ImageTexture.CreateFromImage(image);
    }
}
