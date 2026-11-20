using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace BadDay.Client.Tests;

/// <summary>
/// The puppet as the GPU draws it. The headless smoke has no renderer, so nothing there runs the shader, the CUSTOM1 packing or the
/// per-frame bone matrices; this check does, in a window (Godot's gl_compatibility renderer, the client's own).
/// It poses the carriers in a bent pose (every joint turned at random) under a moved and turned BodyRenderer, renders one frame of the
/// puppet alone on black, and compares the rendered silhouette with the silhouette of the same mesh skinned on the CPU from the
/// carriers and the spread bind (not from anything Puppet computed) and projected through the camera. Intersection over union must be
/// at least <see cref="MinIou"/>. It also looks at the light, with the chamber near full glow: the puppet's faces must not clip to white and must differ from each other, and the chamber's light must lift the puppet over the same frame with that light off (so the glow is bounded from below as well).
/// <c>godot --path client res://tests/RenderCheck.tscn [-- --png=path]</c>; exit code 0 passes, 1 fails. It needs a window, so it is
/// not part of the headless smoke.
/// </summary>
public partial class RenderCheck : Node
{
    /// <summary>
    /// A correct render measures 1.0000 in this pose, on every run so far, so the floor sits at the measurement's noise. Soul's
    /// gross skinning mutants (a flipped weight, no inverse bind, the creation pose as the bind, an inverted CUSTOM1 weight, an
    /// ignored parent) measure 0.10 to 0.61; the localized ones (a forearm bone 10 degrees off 0.982, a bone 2 cm off 0.973, hard
    /// weights 0.998) were under a floor of 0.95 and only fail at this one. Gaps that remain: the silhouette cannot see the shading
    /// normal (unskinned normals measure 1.0000) or the culling box (the puppet stays on screen with a 1 cm box).
    /// </summary>
    private const float MinIou = 0.999f;

    /// <summary>The puppet's lit faces: at most this share of its pixels may be at 250 or more of 255.</summary>
    private const float MaxClipped = 0.02f;

    /// <summary>The puppet's facets must differ: its 10th to 90th percentile brightness spans at least this many of 255 levels.</summary>
    private const float MinSpread = 30f;

    /// <summary>The chamber's glow must light the puppet: with the light on, the mean level of the puppet's pixels must exceed the same frame with the light off by at least this many of 255 levels (the shipped light lifts it 9.8). A glow of 0 fails.</summary>
    private const float MinGlowLift = 5f;

    private const int BendSeed = 7;
    private const float BendDegrees = 60f;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        bool ok = true;
        try
        {
            ok = await Check();
        }
        catch (Exception e)
        {
            GD.PrintErr("RENDER FAIL: " + e);
            ok = false;
        }
        GD.Print(ok ? "RENDER PASS" : "RENDER FAILED");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async Task<bool> Check()
    {
        var main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
        AddChild(main);
        if (main.Failure is { } failure) { GD.PrintErr("RENDER FAIL: Main could not start a session: " + failure); return false; }
        if (main.Bodies.PuppetShape is not { } shape) { GD.PrintErr("RENDER FAIL: the puppet has no mesh"); return false; }

        // The puppet alone, on black, with a parent that is neither at the origin nor unturned.
        main.SetPhysicsProcess(false);
        main.Hud.Visible = false;
        // The chamber's volume is not the puppet, but its glow lights the puppet: run the box near full glow and keep the light.
        foreach (var volume in main.Chamber.GetChildren().OfType<MeshInstance3D>()) volume.Visible = false;
        main.SetIntensity(1.5);
        for (int i = 0; i < 1200; i++) main.Session.Step();
        main.Chamber.Sync(main.Session);
        if (main.Chamber.Glow < 0.9f) { GD.PrintErr($"RENDER FAIL: the chamber glows {main.Chamber.Glow}, not near full; the light probe proves nothing"); return false; }
        foreach (var scenery in main.Bodies.GetChildren().OfType<MeshInstance3D>()) scenery.Visible = false;
        main.GetChildren().OfType<WorldEnvironment>().Single().Environment.BackgroundColor = Colors.Black;
        main.Bodies.Transform = new Transform3D(new Basis(Vector3.Up, 0.45f), new Vector3(0.15f, 0f, 0.1f));

        var bones = PuppetChecks.Bend(shape, BendSeed, BendDegrees * Mathf.Pi / 180f);
        var carriers = new Node3D[shape.Ids.Length];
        for (int i = 0; i < carriers.Length; i++)
        {
            carriers[i] = main.Bodies.GetNode<Node3D>($"Puppet/Body{shape.Ids[i]}");
            carriers[i].Transform = Puppet.ToTransform(bones[i].After(shape.SpreadBind[i]));
            carriers[i].ResetPhysicsInterpolation();
        }
        for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgba8);
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--png=")) image.SavePng(arg["--png=".Length..]);

        // What the GPU drew.
        int w = image.GetWidth(), h = image.GetHeight();
        var drawn = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = image.GetPixel(x, y);
                drawn[y * w + x] = Mathf.Max(c.R, Mathf.Max(c.G, c.B)) > 0.12f;
            }

        // What the same mesh covers skinned on the CPU: bones from the carriers' world poses and the spread bind, nothing from Puppet.
        var camera = GetViewport().GetCamera3D();
        var scale = new Vector2(w, h) / GetViewport().GetVisibleRect().Size;
        var skinned = new Vector3[shape.Positions.Length];
        var world = carriers.Select((c, i) => c.GlobalTransform * Puppet.ToTransform(shape.SpreadBind[i].Inverse())).ToArray();
        for (int v = 0; v < skinned.Length; v++)
        {
            var p = new Vector3(shape.Positions[v].x, shape.Positions[v].y, shape.Positions[v].z);
            skinned[v] = world[shape.BoneA[v]] * p * shape.WeightA[v] + world[shape.BoneB[v]] * p * (1f - shape.WeightA[v]);
        }
        var covered = new bool[w * h];
        for (int t = 0; t < skinned.Length; t += 3)
        {
            var a = camera.UnprojectPosition(skinned[t]) * scale; var b = camera.UnprojectPosition(skinned[t + 1]) * scale; var c = camera.UnprojectPosition(skinned[t + 2]) * scale;
            Fill(covered, w, h, a, b, c);
        }

        int both = 0, either = 0, clipped = 0;
        var puppetPixels = new System.Collections.Generic.List<int>();
        var levels = new System.Collections.Generic.List<float>();
        for (int i = 0; i < covered.Length; i++)
        {
            if (covered[i] || drawn[i]) either++;
            if (!covered[i] || !drawn[i]) continue;
            both++;
            puppetPixels.Add(i);
            var px = image.GetPixel(i % w, i / w);
            float level = Mathf.Max(px.R, Mathf.Max(px.G, px.B)) * 255f;
            levels.Add(level);
            if (level >= 250f) clipped++;
        }
        float meanOn = levels.Count == 0 ? 0 : levels.Average();
        levels.Sort();
        float lit = levels.Count;
        float spread = lit == 0 ? 0 : levels[(int)(lit * 0.9f)] - levels[(int)(lit * 0.1f)];
        float iou = either == 0 ? 0 : (float)both / either;
        float share = lit == 0 ? 1 : clipped / lit;

        // The control: the same frame with the chamber's light off, over the same pixels.
        var glowLight = main.Chamber.GetChildren().OfType<OmniLight3D>().Single();
        float energy = glowLight.LightEnergy;
        glowLight.LightEnergy = 0f;
        for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dark = GetViewport().GetTexture().GetImage();
        dark.Convert(Image.Format.Rgba8);
        glowLight.LightEnergy = energy;
        float meanOff = puppetPixels.Count == 0 ? 0 : puppetPixels.Average(i => { var px = dark.GetPixel(i % w, i / w); return Mathf.Max(px.R, Mathf.Max(px.G, px.B)) * 255f; });
        float lift = meanOn - meanOff;

        GD.Print($"RENDER glow lift {lift:F2} levels (mean {meanOn:F1} lit, {meanOff:F1} with the chamber light off)");
        GD.Print($"RENDER iou {iou:F4} (covered {covered.Count(x => x)}, drawn {drawn.Count(x => x)}), clipped {share:P1} of {lit} puppet pixels, 10th to 90th percentile level spread {spread:F0} of 255");
        bool ok = true;
        if (iou < MinIou) { GD.PrintErr($"RENDER FAIL: the drawn silhouette matches the CPU-skinned mesh with IoU {iou:F3}, under {MinIou}"); ok = false; }
        if (share > MaxClipped) { GD.PrintErr($"RENDER FAIL: {share:P1} of the puppet's pixels are clipped to white, over {MaxClipped:P0}; the facets cannot read"); ok = false; }
        if (spread < MinSpread) { GD.PrintErr($"RENDER FAIL: the puppet's pixels span only {spread:F0} levels between the 10th and 90th percentile, under {MinSpread}; the facets do not differ"); ok = false; }
        if (lift < MinGlowLift) { GD.PrintErr($"RENDER FAIL: the chamber's light lifts the puppet by {lift:F2} levels, under {MinGlowLift}; the box's glow does not light the body"); ok = false; }
        return ok;
    }

    /// <summary>Marks the pixels whose centres lie inside the triangle.</summary>
    private static void Fill(bool[] mask, int w, int h, Vector2 a, Vector2 b, Vector2 c)
    {
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        float Edge(Vector2 p, Vector2 q, float x, float y) => (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float e0 = Edge(a, b, px, py), e1 = Edge(b, c, px, py), e2 = Edge(c, a, px, py);
                if ((e0 >= 0 && e1 >= 0 && e2 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0)) mask[y * w + x] = true;
            }
    }
}
