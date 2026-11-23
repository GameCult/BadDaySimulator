using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BadDay.Core;
using BadDay.Ymir;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client.Tests;

/// <summary>
/// The windowed check of the PS1 look: it renders real frames (gl_compatibility, the client's renderer) and reads them back. Every reading is
/// deterministic (seed 1, ticks pinned, physics frozen before a capture, probes drawn with the client's own materials), so a bound
/// sits at the geometric midpoint of the shipped worst and the least claimed mutant and needs at least 1.2 between them
/// (lodur:ruling:deterministic-bound-margin); tests/render-mutants.sh breaks the shipped code once per claim and wants that claim's line to fail.
/// <c>godot --path client res://tests/RenderCheck.tscn [-- --frames=dir]</c>; exit code 0 passes, 1 fails. It needs a window.
/// </summary>
public partial class RenderCheck : Node
{
    private const int Width = 1280, Height = 720;
    private const double WatchdogSeconds = 120;
    // Bounds (shipped worst, least claimed mutant, separation) are in each check's comment.
    private const int OddLevelFloor = 1245, FigureMisses = 3, DitherBlocks = 139;
    private const double FogLevels = 22, LightLevels = 11, MipPercent = 6.5;

    private readonly List<string> _failures = new();
    private Main _main = null!;
    private OrbitCamera _camera = null!;
    private string? _frames;
    private bool _done;

    private BodyPainBoxSession Session => _main.Session;
    private int Block => Look.BlockSize(Height);

    public override void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--frames=")) _frames = arg["--frames=".Length..];
        _ = RunAsync();
    }

    public override void _Process(double delta)
    {
        if (!_done && Time.GetTicksMsec() > WatchdogSeconds * 1000)
        {
            Fail("watchdog", $"no result after {WatchdogSeconds} s");
            Finish();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(Width, Height));
            _main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
            AddChild(_main);
            _camera = _main.GetNode<OrbitCamera>("Camera");
            _main.Hud.Visible = false; // the HUD draws above the post pass: it is not the 3D's
            Engine.PhysicsTicksPerSecond = 1200;
            if (_main.Failure is { } failure) Fail("start", failure);
            else await Scenario();
        }
        catch (Exception e)
        {
            Fail("exception", e.ToString());
        }
        Finish();
    }

    private async Task Scenario()
    {
        await Frames(3);
        var window = GetWindow().Size;
        if (window != new Vector2I(Width, Height)) { Fail("window", $"the window is {window}, not {Width}x{Height}"); return; }

        // The play view at tick 240 of seed 1.
        await UntilTick(240);
        _main.SetPhysicsProcess(false); // the run stops here; the carriers settle on its pose
        await Frames(3);
        var play = await Capture("play");

        CheckFifteenBit(play);
        CheckBlocks(play, Block);
        CheckLines(play);

        _main.Chamber.Volume.Visible = false; // the translucent box tints the hand bodies inside it
        CheckFigureWhereLanesAre(await Capture("play-clear"));
        _main.Chamber.Volume.Visible = true;

        var home = _camera.Transform;
        _camera.Distance = 0.9f;
        await Frames(2);
        await Capture("close");
        _camera.Transform = home;

        await CheckSnapAndAffine();
        await CheckShading();
        await CheckGlow();
    }

    /// <summary>
    /// 15-bit colour: every channel of every pixel is a multiple of 8 (within one level of the 8-bit rounding), and the odd multiples of 8 (8, 24, 40 ...)
    /// appear, which 12-bit colour (multiples of 16) never draws. Off-grid channels: bound 0; shipped 0, post off 1728141. Channels within one level of an
    /// odd multiple: floor 1245 = sqrt(1 x 1549038); the least shipped reading is 1549038 (the lines mutants read more), the 12-bit post 0 (counted as 1),
    /// so the shipped frame is over the floor by a factor of 1244 and the mutant under it by 1245.
    /// </summary>
    private void CheckFifteenBit(Frame f)
    {
        int bad = 0, odd = 0;
        for (int i = 0; i < f.Data.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                if (f.Data[i + c] % 8 is not (0 or 1 or 7)) bad++;
                if (f.Data[i + c] % 16 is 7 or 8 or 9) odd++;
            }
        Report("15-bit", bad, 0);
        Check(bad == 0, "15-bit", $"{bad} channels are not a multiple of 8");
        Report("15-bit: channels at an odd multiple of 8", odd, OddLevelFloor);
        Check(odd >= OddLevelFloor, "15-bit", $"only {odd} channels are at an odd multiple of 8 (8, 24, 40 ...), under {OddLevelFloor}: the framebuffer is not 15-bit");
    }

    /// <summary>
    /// The line grid is 240 lines, a literal and not Look.Lines: at 1280x720 the blocks are 3 px, so the greatest common divisor of the offsets from the
    /// screen centre at which any colour changes is 3 along each axis (a coarser grid has a larger divisor, a finer one a smaller). Exact. The harness
    /// asserts this at 720 only: any other window size fails the "window" check before a frame is read (the grid is round(H / 240) px, 216 lines at 1080,
    /// and Camera3D.UnprojectPosition answers in the stretch base's coordinates, so the lanes and snap probes read the wrong pixels). Shipped 3 and 3,
    /// Look.Lines 360 reads 2, Look.Lines 120 reads 6.
    /// </summary>
    private void CheckLines(Frame f)
    {
        int gx = 0, gy = 0;
        for (int y = 0; y < f.Height; y++)
            for (int x = 1; x < f.Width; x++)
                if (f.Rgb(x, y) != f.Rgb(x - 1, y)) gx = Gcd(gx, Math.Abs(x - f.Width / 2));
        for (int x = 0; x < f.Width; x++)
            for (int y = 1; y < f.Height; y++)
                if (f.Rgb(x, y) != f.Rgb(x, y - 1)) gy = Gcd(gy, Math.Abs(y - f.Height / 2));
        Report("lines: block width in px", gx, 3);
        Report("lines: block height in px", gy, 3);
        Check(gx == 3 && gy == 3, "lines", $"the blocks are {gx} x {gy} px, not 3 x 3: 240 lines at {Height} px");
    }

    /// <summary>Every block of the line grid, anchored at the screen centre, is one colour. Bound 0 pixels; shipped 0, sampling per pixel 218873.</summary>
    private void CheckBlocks(Frame f, int s)
    {
        var seen = new Dictionary<(int, int), uint>();
        int bad = 0;
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                var key = (BlockOf(x, f.Width, s), BlockOf(y, f.Height, s));
                var colour = f.Rgb(x, y);
                if (!seen.TryGetValue(key, out var first)) seen[key] = colour;
                else if (first != colour) bad++;
            }
        Report("blocks", bad, 0);
        Check(bad == 0, "blocks", $"{bad} pixels differ from the first pixel of their block");
    }

    /// <summary>
    /// Each dynamic body's lane origin, projected through the camera, lands within one block (3 px) of a figure pixel (skin is red over blue; floor,
    /// background and chamber are blue over red). A single pixel failed under unrelated edits (dither, snap, glow, post off each moved one origin onto a
    /// dithered or edge pixel), so the neighbourhood is the block. Bound 3 of 19 = sqrt(0.5 x 19): shipped 0 (counted as half a body), carriers left
    /// unposed 19, separation 38; post off, 12-bit, dither off, snap off, glow unset, fog off, per-pixel light, mipmapped and linear textures, 360 and 120
    /// lines, perspective UVs and per-block sampling each read 0, so no collateral edit turns it.
    /// </summary>
    private void CheckFigureWhereLanesAre(Frame play)
    {
        var view = Session.View;
        var camera = GetViewport().GetCamera3D();
        int missed = 0, checkedBodies = 0;
        for (int i = 0; i < view.Count; i++)
        {
            var kind = (Box3DSoaKind)view.Kind[i];
            if (kind is Box3DSoaKind.StaticBox or Box3DSoaKind.Anchor) continue;
            var p = camera.UnprojectPosition(new Vector3(view.PositionX[i], view.PositionY[i], view.PositionZ[i]));
            int x = (int)p.X, y = (int)p.Y;
            checkedBodies++;
            bool figure = false;
            for (int dy = -Block; dy <= Block && !figure; dy++)
                for (int dx = -Block; dx <= Block && !figure; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px >= 0 && py >= 0 && px < Width && py < Height && (play.Rgb(px, py) >> 16 & 0xff) >= (play.Rgb(px, py) & 0xff) + 16) figure = true;
                }
            if (!figure) missed++;
        }
        Report("figure at lanes", missed, FigureMisses);
        Check(checkedBodies > 5 && missed <= FigureMisses, "figure at lanes", $"{missed} of {checkedBodies} dynamic bodies' origins are not within a block of a figure pixel");
    }

    /// <summary>
    /// The snap and the affine probes: the figure and floor are hidden and a probe is drawn with the client's own material under the client's camera.
    /// Snap: a box on a 30-line grid (blocks of 24 px), its drawn blocks against the blocks the CPU-projected hull covers with its corners snapped to
    /// that grid and with them not snapped. Affine: one triangle with a checker texture seen from 60 degrees, one scanline, the widths of its texel runs.
    /// </summary>
    private async Task CheckSnapAndAffine()
    {
        _main.Bodies.Visible = false;
        _main.Chamber.Visible = false;
        var home = _camera.Transform;
        var res = _main.Ps1Res;
        var probes = new Node3D();
        _main.AddChild(probes);

        // Snap. Blocks of 24 px; the pose is chosen so that snapping moves enough of the silhouette to count.
        const int coarse = 24;
        Look.Set("ps1_res", new Vector2(Width / (float)coarse, Height / (float)coarse));
        _camera.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0, 0, 3f));
        var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.2f, 0.8f, 0.6f) }, MaterialOverride = Look.Ps1(null, Colors.White, emissive: true) };
        box.Transform = new Transform3D(new Basis(Vector3.Up, 0.5f) * new Basis(Vector3.Right, 0.35f), new Vector3(0.13f, -0.07f, 0f));
        probes.AddChild(box);
        await Frames(3);
        var snapFrame = await Capture("snap");
        var corners = new List<Vector2>();
        foreach (var sx in new[] { -0.6f, 0.6f }) foreach (var sy in new[] { -0.4f, 0.4f }) foreach (var sz in new[] { -0.3f, 0.3f })
            corners.Add(_camera.UnprojectPosition(box.GlobalTransform * new Vector3(sx, sy, sz)));
        var exact = Hull(corners);
        var snapped = Hull(corners.Select(p => new Vector2(Width / 2f + coarse * MathF.Floor((p.X - Width / 2f) / coarse + 0.5f), Height / 2f - coarse * MathF.Floor(-(p.Y - Height / 2f) / coarse + 0.5f))).ToList());
        int offSnapped = 0, offExact = 0;
        for (int by = BlockOf(0, Height, coarse); by <= BlockOf(Height - 1, Height, coarse); by++)
            for (int bx = BlockOf(0, Width, coarse); bx <= BlockOf(Width - 1, Width, coarse); bx++)
            {
                var centre = new Vector2(Width / 2f + (bx + 0.5f) * coarse, Height / 2f + (by + 0.5f) * coarse);
                bool drawn = snapFrame.Luma((int)centre.X, (int)centre.Y) > 128, wantSnapped = Inside(snapped, centre), wantExact = Inside(exact, centre);
                if (drawn != wantSnapped) offSnapped++;
                if (drawn != wantExact) offExact++;
            }
        // Bound 2 blocks: shipped worst 0, snap off 9 (a shipped worst of 0 counts as half a block: sqrt(0.5 x 9) = 2.1, separation 18).
        Report("snap: blocks off the snapped hull", offSnapped, 2);
        Report("snap: blocks off the unsnapped hull", offExact, 0);
        Check(offSnapped <= 2, "snap", $"{offSnapped} blocks differ from the hull of the snapped corners (the unsnapped hull differs in {offExact})");
        Check(offExact >= 4, "snap", $"the pose does not tell snapping from not: the drawn blocks differ from the unsnapped hull in {offExact}");
        box.QueueFree();

        // Affine. Full resolution, the camera at the triangle's base height so its base edge is the horizon row: a row 40 px above is a constant-height scanline.
        Look.Set("ps1_res", new Vector2(Width, Height));
        _camera.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0, -0.6f, 3f));
        var checker = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        checker.SetPixel(0, 0, Colors.White); checker.SetPixel(1, 1, Colors.White); checker.SetPixel(1, 0, Colors.Black); checker.SetPixel(0, 1, Colors.Black);
        var mesh = TriangleMesh(new[] { new Vector3(-1.6f, -0.6f, 0), new Vector3(-1.6f, 1.8f, 0), new Vector3(1.6f, -0.6f, 0) }, new[] { Vector3.Back, Vector3.Back, Vector3.Back }, new[] { new Vector2(0, 0), new Vector2(0, 8), new Vector2(8, 0) });
        var triangle = new MeshInstance3D { Mesh = mesh, MaterialOverride = Look.Ps1(ImageTexture.CreateFromImage(checker), Colors.White, emissive: true) };
        triangle.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(60)), Vector3.Zero);
        probes.AddChild(triangle);
        await Frames(3);
        var affineFrame = await Capture("affine");
        var runs = Runs(affineFrame, Height / 2 - 40);
        float spread = runs.Count > 3 ? runs.Skip(1).Take(runs.Count - 2).Max() - runs.Skip(1).Take(runs.Count - 2).Min() : float.PositiveInfinity;
        // Bound 8 px: shipped worst 1, perspective-correct UVs 67 (sqrt(1 x 67) = 8.2, separation 67).
        Report("affine: spread of texel run widths in px (" + string.Join(" ", runs) + ")", spread, 8);
        Check(runs.Count >= 6 && spread <= 8, "affine", $"texel runs along a scanline are {string.Join(" ", runs)} px wide (spread {spread}); an affine texture keeps them equal within 8");

        probes.QueueFree();
        _camera.Transform = home;
        Look.Set("ps1_res", res);
        _main.Bodies.Visible = true;
        _main.Chamber.Visible = true;
    }

    /// <summary>
    /// The shading probes, each a surface drawn with the client's own material under the client's camera (the figure and chamber hidden, the chamber's
    /// glow off): ordered dither on a flat field, fog at 9 m, per-vertex light on a triangle whose vertex normals differ, and a minified texture that
    /// carries mipmaps. Every reading is a deterministic count or mean; each bound's comment gives shipped worst, least mutant and separation.
    /// </summary>
    private async Task CheckShading()
    {
        _main.Bodies.Visible = false;
        _main.Chamber.Visible = false;
        var home = _camera.Transform;
        var res = _main.Ps1Res;
        var glow = RenderingServer.GlobalShaderParameterGet("ps1_glow_energy");
        Look.Set("ps1_glow_energy", 0f);
        var probes = new Node3D();
        _main.AddChild(probes);
        _camera.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0, 0, 3f));

        // Dither: a flat emissive field of raw (97, 106, 61), whose levels 88/96, 96/104 and 56/64 the 4x4 matrix chooses between block by block.
        // Each block's channels against ps1.js's matrix (column by block column, row by block row), within the 8-bit rounding.
        var field = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(30, 30) }, MaterialOverride = Look.Ps1(null, new Color(97 / 255f, 106 / 255f, 61 / 255f), emissive: true) };
        probes.AddChild(field);
        await Frames(3);
        var flat = await Capture("dither");
        int s = Block, offDither = 0;
        int[] raw = { 97, 106, 61 };
        for (int by = BlockOf(0, Height, s); by <= BlockOf(Height - 1, Height, s); by++)
            for (int bx = BlockOf(0, Width, s); bx <= BlockOf(Width - 1, Width, s); bx++)
            {
                int x = Width / 2 + bx * s, y = Height / 2 + by * s;
                if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                int d = Bayer[((bx % 4) + 4) % 4][((by % 4) + 4) % 4];
                bool ok = true;
                for (int c = 0; c < 3; c++)
                {
                    int level = (raw[c] + d) / 8 * 8, below = (raw[c] + d - 1) / 8 * 8, seen = flat.Data[4 * (y * Width + x) + c];
                    ok &= Math.Abs(seen - level) <= 1 || Math.Abs(seen - below) <= 1;
                }
                if (!ok) offDither++;
            }
        // Bound 139 blocks of 102480 = sqrt(0.5 x 38400): shipped 0 (counted as half a block), dither off 38400, separation 76800. (Post off and the 12-bit post
        // read 102480: they change every level the matrix picks between.)
        Report("dither: blocks off ps1.js's matrix", offDither, DitherBlocks);
        Check(offDither <= DitherBlocks, "dither", $"{offDither} blocks of a flat field differ from the 4x4 ordered dither");
        field.QueueFree();

        // Fog: a white field at 9 m (half way between FogNear and FogFar, smoothstep 0.5); the mean of a 300 px square against the fogged colour.
        var wall = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(30, 30) }, MaterialOverride = Look.Ps1(null, Colors.White, emissive: true), Position = new Vector3(0, 0, 3f - 9f) };
        probes.AddChild(wall);
        await Frames(3);
        var fogFrame = await Capture("fog");
        float t = Math.Clamp((9f - Look.FogNear) / (Look.FogFar - Look.FogNear), 0f, 1f), fogged = t * t * (3 - 2 * t);
        double worst = 0;
        float[] bg = { Look.Background.R, Look.Background.G, Look.Background.B };
        for (int c = 0; c < 3; c++)
        {
            double sum = 0; int n = 0;
            for (int y = Height / 2 - 150; y < Height / 2 + 150; y++)
                for (int x = Width / 2 - 150; x < Width / 2 + 150; x++) { sum += fogFrame.Data[4 * (y * Width + x) + c]; n++; }
            worst = Math.Max(worst, Math.Abs(sum / n - 255 * (1 - fogged * (1 - bg[c]))));
        }
        // Bound 22 levels = sqrt(4.53 x 107.75): shipped worst 4.53 (the floor to 8 and the dither's mean), fog off 107.75, separation 23.8. (The 12-bit post reads 12.25.)
        Report("fog: mean level off the fogged colour at 9 m", worst, FogLevels);
        Check(worst <= FogLevels, "fog", $"a wall at 9 m reads {worst:F1} levels from white fogged by {fogged:F2} toward the background");
        wall.QueueFree();

        // Light: one grey triangle facing the camera, vertex normals at 0 and -+120 degrees from the key (dots 1, -0.5, -0.5). Light per vertex is the
        // clamped dot at the corners interpolated across the face; per pixel it would be the dot of the interpolated normal, clamped after. One row
        // near the base, the red channel against the barycentric blend of the corners' light.
        Look.Set("ps1_res", new Vector2(Width, Height));
        var key = Look.KeyDirection;
        var u = key.Cross(Vector3.Right).Normalized();
        var normals = new[] { key, key * -0.5f + u * 0.8660254f, key * -0.5f - u * 0.8660254f };
        var corners = new[] { new Vector3(-1.2f, -0.8f, 0), new Vector3(0, 0.9f, 0), new Vector3(1.2f, -0.8f, 0) };
        var lit = new MeshInstance3D { Mesh = TriangleMesh(corners, normals, new Vector2[3]), MaterialOverride = Look.Ps1(null, new Color(0.5f, 0.5f, 0.5f)) };
        probes.AddChild(lit);
        await Frames(3);
        var litFrame = await Capture("light");
        var screen = corners.Select(c => _camera.UnprojectPosition(c)).ToArray();
        int row = (int)(screen[0].Y + (screen[1].Y - screen[0].Y) * 0.12f);
        double err = 0; int samples = 0;
        for (int x = 0; x < Width; x++)
        {
            var p = new Vector2(x + 0.5f, row + 0.5f);
            float den = (screen[1].Y - screen[2].Y) * (screen[0].X - screen[2].X) + (screen[2].X - screen[1].X) * (screen[0].Y - screen[2].Y);
            float w0 = ((screen[1].Y - screen[2].Y) * (p.X - screen[2].X) + (screen[2].X - screen[1].X) * (p.Y - screen[2].Y)) / den;
            float w1 = ((screen[2].Y - screen[0].Y) * (p.X - screen[2].X) + (screen[0].X - screen[2].X) * (p.Y - screen[2].Y)) / den;
            float w2 = 1 - w0 - w1;
            if (w0 < 0.04f || w1 < 0.04f || w2 < 0.04f) continue;
            float dots = w0 * Math.Max(normals[0].Dot(key), 0) + w1 * Math.Max(normals[1].Dot(key), 0) + w2 * Math.Max(normals[2].Dot(key), 0);
            err += Math.Abs(litFrame.Data[4 * (row * Width + x)] - Math.Min(1f, 0.5f * (Look.AmbientColor.R + Look.KeyColor.R * dots)) * 255);
            samples++;
        }
        double lightErr = samples > 100 ? err / samples : double.PositiveInfinity;
        // Bound 11 levels = sqrt(4.45 x 27.13): shipped worst 4.45, light per pixel 27.13, separation 6.1. (The 12-bit post reads 8.51.)
        Report($"light: mean red level off the Gouraud blend over {samples} px", lightErr, LightLevels);
        Check(lightErr <= LightLevels, "light", $"red along a row of a triangle is {lightErr:F1} levels from the blend of its corners' light ({samples} px): the light is not per vertex");
        lit.QueueFree();

        // Mips: a 2 x 2 checker that carries a generated mip chain, minified (about 1.7 texels a pixel). Nearest without mips draws only black and white;
        // a sampler that reads the mips or filters draws grey. The share of the central 400 x 300 px with luma between 60 and 190.
        var image = Image.CreateEmpty(2, 2, true, Image.Format.Rgb8);
        image.SetPixel(0, 0, Colors.White); image.SetPixel(1, 1, Colors.White); image.SetPixel(1, 0, Colors.Black); image.SetPixel(0, 1, Colors.Black);
        image.GenerateMipmaps();
        var sheet = new MeshInstance3D
        {
            Mesh = TriangleMesh(new[] { new Vector3(-3, 2, 0), new Vector3(3, 2, 0), new Vector3(3, -2, 0), new Vector3(-3, 2, 0), new Vector3(3, -2, 0), new Vector3(-3, -2, 0) },
                Enumerable.Repeat(Vector3.Back, 6).ToArray(),
                new[] { new Vector2(0, 0), new Vector2(2400, 0), new Vector2(2400, 1600), new Vector2(0, 0), new Vector2(2400, 1600), new Vector2(0, 1600) }),
            MaterialOverride = Look.Ps1(ImageTexture.CreateFromImage(image), Colors.White, emissive: true),
        };
        probes.AddChild(sheet);
        await Frames(3);
        var sheetFrame = await Capture("mips");
        int grey = 0, all = 0;
        for (int y = Height / 2 - 150; y < Height / 2 + 150; y++)
            for (int x = Width / 2 - 200; x < Width / 2 + 200; x++) { if (sheetFrame.Luma(x, y) is > 60 and < 190) grey++; all++; }
        double share = 100.0 * grey / all;
        // Bound 6.5 percent = sqrt(0.5 x 84.44): shipped 0 (counted as half a percent), linear filter 84.44, nearest with mipmaps 100, separation 169.
        Report("mips: percent of a minified checker that is neither black nor white", share, MipPercent);
        Check(share <= MipPercent, "mips", $"{share:F1}% of a minified checker is grey: the sampler reads mipmaps or filters");

        probes.QueueFree();
        _camera.Transform = home;
        Look.Set("ps1_res", res);
        Look.Set("ps1_glow_energy", glow);
        _main.Bodies.Visible = true;
        _main.Chamber.Visible = true;
    }

    private static ArrayMesh TriangleMesh(Vector3[] vertices, Vector3[] normals, Vector2[] uv)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>ps1.js's 4x4 ordered dither matrix, indexed [block column mod 4][block row mod 4] as ps1_post.gdshader indexes its mat4.</summary>
    private static readonly int[][] Bayer = { new[] { -4, 0, -3, 1 }, new[] { 2, -2, 3, -1 }, new[] { -3, 1, -4, 0 }, new[] { 3, -1, 2, -2 } };

    /// <summary>
    /// The chamber's light lights the figure: at tick 1 of seed 1 the chamber at full glow (box intensity 6) and dark (0), the bodies in the same
    /// pose, the chamber's own volume hidden so only the light differs; the mean luma of the 300 px square about the held hand is higher
    /// lit. Bound: half the shipped difference; a chamber whose light is not written reads 0.
    /// </summary>
    private async Task CheckGlow()
    {
        var means = new double[2];
        var poses = new List<Transform3D?[]>();
        for (int run = 0; run < 2; run++)
        {
            _main.SetPhysicsProcess(true);
            _main.SetIntensity(run == 0 ? 6 : 0);
            _main.Reset(1);
            await UntilTick(1);
            _main.SetPhysicsProcess(false);
            await Frames(3);
            _main.Chamber.Volume.Visible = false;
            var frame = await Capture(run == 0 ? "glow-full" : "glow-off");
            _main.Chamber.Volume.Visible = true;
            var hold = _camera.UnprojectPosition(new Vector3(BodyPainBox.HoldPoint.X, BodyPainBox.HoldPoint.Y, BodyPainBox.HoldPoint.Z));
            double sum = 0; int n = 0;
            for (int y = (int)hold.Y - 150; y < (int)hold.Y + 150; y++)
                for (int x = (int)hold.X - 150; x < (int)hold.X + 150; x++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                    sum += frame.Luma(x, y); n++;
                }
            means[run] = sum / n;
            poses.Add(Session.View.StableId.ToArray().Select(id => _main.Bodies.PoseOf(id)).ToArray());
        }
        bool samePose = poses[0].Zip(poses[1]).All(p => p.First is { } a && p.Second is { } b && a.Origin.DistanceTo(b.Origin) < 1e-4f);
        double lift = means[0] - means[1];
        // Bound 1.2 levels: shipped 2.4, light not written 0 (half the shipped lift: the mutant's reading has no spread to take a midpoint of).
        Report("glow: luma lift near the hand", lift, 1.2);
        Check(samePose, "glow", "the bodies are not in the same pose at tick 1 with the box lit and dark; the probe proves nothing");
        Check(lift >= 1.2, "glow", $"the figure is {lift:F2} levels brighter with the chamber at full glow than dark, under 1.2");
    }

    private readonly record struct Frame(int Width, int Height, byte[] Data)
    {
        public uint Rgb(int x, int y) { int i = 4 * (y * Width + x); return (uint)(Data[i] << 16 | Data[i + 1] << 8 | Data[i + 2]); }
        public double Luma(int x, int y) { int i = 4 * (y * Width + x); return 0.299 * Data[i] + 0.587 * Data[i + 1] + 0.114 * Data[i + 2]; }
    }

    private async Task<Frame> Capture(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgba8);
        if (_frames is not null) image.SavePng($"{_frames}/{name}.png");
        return new Frame(image.GetWidth(), image.GetHeight(), image.GetData());
    }

    /// <summary>The widths in pixels of the white and black runs along row <paramref name="y"/> inside the lit triangle, edges included.</summary>
    private static List<int> Runs(Frame f, int y)
    {
        var runs = new List<int>();
        int start = 0; bool? last = null;
        for (int x = 0; x < f.Width; x++)
        {
            if (f.Luma(x, y) is var l && l is > 60 and < 190) { last = null; continue; } // a dithered or blended pixel: not a clean texel
            bool white = f.Luma(x, y) > 128;
            if (last is null) { start = x; last = white; }
            else if (last != white) { runs.Add(x - start); start = x; last = white; }
        }
        return runs;
    }

    /// <summary>The block a pixel belongs to on a grid of <paramref name="s"/>-pixel blocks anchored at the centre of an axis of <paramref name="length"/> pixels.</summary>
    private static int BlockOf(int pixel, int length, int s) => (int)Math.Floor((pixel - length / 2.0) / s);

    private static List<Vector2> Hull(List<Vector2> points)
    {
        var p = points.OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        var h = new List<Vector2>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            int floor = h.Count;
            foreach (var q in pass)
            {
                while (h.Count >= floor + 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static bool Inside(List<Vector2> hull, Vector2 q)
    {
        for (int i = 0; i < hull.Count; i++) if (Cross(hull[i], hull[(i + 1) % hull.Count], q) < 0) return false;
        return true;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task UntilTick(long tick)
    {
        while (Session.TickIndex < tick && !Session.Finished) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (Session.TickIndex != tick) Fail("tick", $"the run stopped at tick {Session.TickIndex}, not {tick}");
        await Frames(1);
    }

    private void Report(string name, double reading, double bound) => GD.Print($"RENDER {name}: {reading:0.##} (bound {bound:0.##})");

    private void Check(bool ok, string name, string failure)
    {
        if (!ok) Fail(name, failure);
    }

    private void Fail(string name, string failure) => _failures.Add($"{name}: {failure}");

    private void Finish()
    {
        if (_done) return;
        _done = true;
        foreach (var f in _failures) GD.PrintErr("RENDER FAIL " + f);
        GD.Print(_failures.Count == 0 ? "RENDER PASS" : $"RENDER FAILED ({_failures.Count})");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
