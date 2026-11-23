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
        CheckFigureWhereLanesAre(play);

        var home = _camera.Transform;
        _camera.Distance = 0.9f;
        await Frames(2);
        await Capture("close");
        _camera.Transform = home;

        await CheckSnapAndAffine();
        await CheckGlow();
    }

    /// <summary>Every channel of every pixel is a multiple of 8: 15-bit colour, within one level of the 8-bit rounding. Bound 0; shipped 0, post off 1729077 channels.</summary>
    private void CheckFifteenBit(Frame f)
    {
        int bad = 0;
        for (int i = 0; i < f.Data.Length; i += 4)
            for (int c = 0; c < 3; c++)
                if (f.Data[i + c] % 8 is not (0 or 1 or 7)) bad++;
        Report("15-bit", bad, 0);
        Check(bad == 0, "15-bit", $"{bad} channels are not a multiple of 8");
    }

    /// <summary>Every block of the line grid, anchored at the screen centre, is one colour. Bound 0 pixels; shipped 0, sampling per pixel 218066.</summary>
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

    /// <summary>Each dynamic body's lane origin, projected through the camera, lands on a figure pixel (skin is red over blue; floor, background and chamber are blue over red). Bound 0 misses; shipped 0, carriers left unposed 19.</summary>
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
            if (x < 0 || y < 0 || x >= Width || y >= Height || (play.Rgb(x, y) >> 16 & 0xff) < (play.Rgb(x, y) & 0xff) + 16) missed++;
        }
        Report("figure at lanes", missed, 0);
        Check(checkedBodies > 5 && missed == 0, "figure at lanes", $"{missed} of {checkedBodies} dynamic bodies' origins are not on a figure pixel");
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
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-1.6f, -0.6f, 0), new Vector3(-1.6f, 1.8f, 0), new Vector3(1.6f, -0.6f, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 0), new Vector2(0, 8), new Vector2(8, 0) };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
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
