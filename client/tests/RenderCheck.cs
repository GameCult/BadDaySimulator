using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace BadDay.Client.Tests;

/// <summary>
/// The puppet as the GPU draws it. The headless smoke has no renderer, so nothing there runs the shader, the attribute packing or the
/// per-frame skin upload; this check does, in a window (Godot's gl_compatibility renderer, the client's own).
/// It poses the carriers in a bent pose (every joint turned at random) under a moved and turned BodyRenderer, renders one frame of the
/// puppet alone on black, and compares the rendered silhouette with the silhouette of the same mesh skinned on the CPU from the
/// carriers and the spread bind (not from anything Puppet computed) and projected through the camera. Intersection over union must be
/// at least <see cref="MinIou"/>. It also looks at the light, with the chamber near full glow: the puppet's faces must not clip to white and must differ from each other, and the chamber's light must lift the puppet over the same frame with that light off (so the glow is bounded from below as well).
/// It also reads the shading normal the shader draws (ShowNormals: the view-space normal as emission) on the torso in the <see cref="Views"/> (close, far,
/// bent 90 degrees, and at 1920x1080 mid and far) and compares it pixel by pixel with a CPU reference of the whole formula (see <see cref="NormalCheck"/>), on all the interior pixels
/// and on the pixels of the corner blend and of sharp creases alone, and the drawn normals of adjacent pixels across triangle boundaries.
/// <c>godot --path client res://tests/RenderCheck.tscn [-- --png=path] [-- --normals-png=path] [-- --normals-far-png=path] [-- --normals-bend90-png=path] [-- --normals-mid-png=path] [-- --normals-far1080-png=path]</c>; exit code 0 passes, 1 fails. It needs a window, so it is
/// not part of the headless smoke.
/// </summary>
public partial class RenderCheck : Node
{
    /// <summary>
    /// A correct render measures 1.0000 in this pose, on every run so far, so the floor sits at the measurement's noise. Soul's
    /// gross skinning mutants (a flipped weight, no inverse bind, the creation pose as the bind, an inverted CUSTOM1 weight, an
    /// ignored parent) measure 0.10 to 0.61; the localized ones (a forearm bone 10 degrees off 0.982, a bone 2 cm off 0.973, hard
    /// weights 0.998) were under a floor of 0.95 and only fail at this one. Gap that remains: the culling box (a box of half the skinned bounds around their centre passes; a 1 cm box culls the close view and fails).
    /// </summary>
    private const float MinIou = 0.999f;

    /// <summary>The puppet's lit faces: at most this share of its pixels may be at 250 or more of 255.</summary>
    private const float MaxClipped = 0.02f;

    /// <summary>The puppet's facets must differ: its 10th to 90th percentile brightness spans at least this many of 255 levels.</summary>
    private const float MinSpread = 30f;

    /// <summary>The chamber's glow must light the puppet: with the light on, the mean level of the puppet's pixels must exceed the same frame with the light off by at least this many of 255 levels (the shipped light lifts it 9.8). A glow of 0 fails.</summary>
    private const float MinGlowLift = 5f;

    /// <summary>The 90th percentile angular error (degrees) of the drawn normal against the CPU reference, over the interior pixels.</summary>
    private const float MaxNormalP90 = 2f;

    /// <summary>The median angular error (degrees) over the bevel band: pixels within 0.6 of the bevel width of an edge that turns by more than 0.1 radian.</summary>
    private const float MaxBandMedian = 0.6f;

    /// <summary>The median angular error (degrees) over the pixels of triangles whose bevel width the smallest altitude caps.</summary>
    private const float MaxClampedMedian = 1.5f;

    /// <summary>
    /// Vacuity: the band's reference normal must lie this many degrees (median) from the facet's, or a bevel that is off would pass; each
    /// <see cref="Views"/> entry also says how many interior, band, width-capped, corner-blend and sharp-crease pixels it must reach.
    /// </summary>
    private const float MinBandTilt = 4f;

    /// <summary>
    /// The corner blend, the part that takes the bevel to the smooth vertex normal where edges meet, is held on its own pixels (those it moves
    /// past <see cref="CornerBlendFloor"/>), in a space and a radius the percentiles over all interior pixels do not see: the median angular error
    /// there is at most <see cref="MaxCornerMedian"/>, and the blend must move the normal by at least <see cref="MinCornerEffect"/> degrees (median) or
    /// the check cannot tell a blend that is off. The crease clamp is held the same way on the band pixels of edges whose half turn is
    /// between <see cref="SharpCrease"/> and <see cref="SharpCreaseTop"/> radians (115 to 137 degrees of turn: a clamp under 1 rad bites there, and
    /// the reference is still well conditioned, while at the clamp itself, 1.4 rad, the tilt is tan(c) = 5.8 and a pixel of drift in the
    /// distance moves the normal by tens of degrees), at most <see cref="MaxClampedMedian"/>.
    /// </summary>
    private const float CornerBlendFloor = 0.05f, MaxCornerMedian = 0.7f, MinCornerEffect = 0.8f, SharpCrease = 1.0f, SharpCreaseTop = 1.2f;

    /// <summary>Across triangle boundaries: the pairs of adjacent pixels the check must reach (the shares and percentiles each view allows are in <see cref="Views"/>).</summary>
    private const int MinPairs = 20000;

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

        // The puppet is drawn once as it was created before it is bent: a surface that kept anything from its first pose, creases above all,
        // is wrong in the next, and nothing here would see it if the bent pose were the first.
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        var carriers = shape.Ids.Select(id => main.Bodies.GetNode<Node3D>($"Puppet/Body{id}")).ToArray();
        var world = await Pose(shape, carriers, BendDegrees);
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
        var skinned = new Vector3[shape.Vertices.Length];
        for (int v = 0; v < skinned.Length; v++)
        {
            var p = new Vector3(shape.Vertices[v].x, shape.Vertices[v].y, shape.Vertices[v].z);
            skinned[v] = world[shape.BoneA[v]] * p * shape.WeightA[v] + world[shape.BoneB[v]] * p * (1f - shape.WeightA[v]);
        }
        var covered = new bool[w * h];
        for (int t = 0; t < shape.Corners.Length; t += 3)
        {
            var a = camera.UnprojectPosition(skinned[shape.Corners[t]]) * scale; var b = camera.UnprojectPosition(skinned[shape.Corners[t + 1]]) * scale; var c = camera.UnprojectPosition(skinned[shape.Corners[t + 2]]) * scale;
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
        ok &= await NormalCheck(main, shape, carriers, world);
        return ok;
    }

    /// <summary>The reference surface in the world: the carriers' skin of the shape, welded by position, with its own facets, altitudes, vertex normals and creases.</summary>
    private sealed record Surface(Vector3[] P, int[] Weld, Vector3[] Facet, float[] Altitude, float[] Smallest, Vector3[] VertexNormal, float[] Crease);

    /// <summary>
    /// One look at the drawn normal: where from (the offset from the torso and the field of view), in which pose and at which window size, and
    /// what it must reach to prove anything (interior, bevel-band, width-capped, corner-blend and sharply creased pixels) and, if it
    /// reads the continuity across triangles, the bar for it in that pose.
    /// </summary>
    private readonly record struct NormalViewSpec(string Label, Vector3 Offset, float Fov, int Bend, Vector2I Size, string PngArg, bool Continuity, float MaxAcross30, float MaxAcrossP90,
        int MinKept, int MinBand, int MinClamped, int MinCorner, int MinSharp);

    private static readonly Vector2I Hd = new(1280, 720), FullHd = new(1920, 1080);

    /// <summary>
    /// The views. Close (0.35, 0.2, 0.55 m off body 101, FOV 30) is at the 3 mm floor of the bevel; far (about 3.7 m) is where the screen width
    /// decides, and the same far view at 1080 lines is where a width fixed at 720 lines is wrong; mid (2.2 m) is in between, where the floor and
    /// the screen width are within a tenth of each other and the width changes across the frame. Bend 90 is a pose of sharp creases: the
    /// crease clamp is reached there, and the continuity across triangles has its own bar, since some edges fold past 120 degrees and 2 px
    /// cannot soften them.
    /// </summary>
    private static readonly NormalViewSpec[] Views =
    {
        new("close", new Vector3(0.35f, 0.2f, 0.55f), 30f, 60, Hd, "--normals-png=", true, 0.005f, 6f, 200000, 20000, 200, 5000, 1000),
        new("far", new Vector3(1.9f, 1.1f, 3.0f), 30f, 60, Hd, "--normals-far-png=", false, 0f, 0f, 10000, 100, 150, 400, 0),
        new("bend 90 close", new Vector3(0.35f, 0.2f, 0.55f), 30f, 90, Hd, "--normals-bend90-png=", true, 0.03f, 7f, 200000, 20000, 200, 5000, 100),
        new("mid 1080", new Vector3(1.13f, 0.645f, 1.77f), 30f, 60, FullHd, "--normals-mid-png=", false, 0f, 0f, 30000, 100, 100, 3000, 40),
        new("far 1080", new Vector3(1.9f, 1.1f, 3.0f), 30f, 60, FullHd, "--normals-far1080-png=", false, 0f, 0f, 20000, 1000, 150, 3000, 20),
    };

    private async Task<Transform3D[]> Pose(PuppetShape shape, Node3D[] carriers, float degrees)
    {
        var bones = PuppetChecks.Bend(shape, BendSeed, degrees * Mathf.Pi / 180f);
        for (int i = 0; i < carriers.Length; i++)
        {
            carriers[i].Transform = Puppet.ToTransform(bones[i].After(shape.SpreadBind[i]));
            carriers[i].ResetPhysicsInterpolation();
        }
        for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return carriers.Select((c, i) => c.GlobalTransform * Puppet.ToTransform(shape.SpreadBind[i].Inverse())).ToArray();
    }

    /// <summary>
    /// The drawn shading normal against the shader's formula evaluated on the CPU, from this check's own skinning of the shape's vertices
    /// by the carriers (nothing the puppet computed): its own weld of the corners by position, its own edge match, facets, creases,
    /// area-weighted vertex normals, altitudes, the screen width and the corner blend. The puppet is seen in the bent pose from the
    /// torso's front-top in each of the <see cref="Views"/>; the view-space normal is read raw from the frame (n * 0.5 + 0.5, not
    /// sRGB-decoded) and turned to world by the camera's basis.
    /// </summary>
    private async Task<bool> NormalCheck(Main main, PuppetShape shape, Node3D[] carriers, Transform3D[] world)
    {
        int torso = Array.IndexOf(shape.Ids, 101UL);
        if (torso < 0) { GD.PrintErr("RENDER FAIL: the puppet has no body 101 to look at"); return false; }
        var puppet = main.Bodies.GetNode<Puppet>("Puppet");
        var window = GetWindow(); var home = window.Size;
        int bend = (int)BendDegrees;
        var surface = BuildSurface(shape, world);

        bool ok = true;
        foreach (var spec in Views)
        {
            if (spec.Bend != bend)
            {
                bend = spec.Bend;
                surface = BuildSurface(shape, await Pose(shape, carriers, bend));
            }
            if (window.Size != spec.Size)
            {
                window.Size = spec.Size;
                for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            var camera = new Camera3D { Fov = spec.Fov, Current = true };
            AddChild(camera);
            var target = carriers[torso].GlobalPosition;
            camera.GlobalPosition = target + spec.Offset;
            camera.LookAt(target, Vector3.Up);
            puppet.ShowNormals = true;
            for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var image = GetViewport().GetTexture().GetImage();
            image.Convert(Image.Format.Rgba8);
            puppet.ShowNormals = false;
            foreach (var arg in OS.GetCmdlineUserArgs())
                if (arg.StartsWith(spec.PngArg)) image.SavePng(arg[spec.PngArg.Length..]);
            if (image.GetSize() != spec.Size) { GD.PrintErr($"RENDER FAIL: the {spec.Label} view was drawn at {image.GetSize()}, not the {spec.Size} it is for"); ok = false; }
            else ok &= NormalView(spec, image, camera, surface);
            camera.QueueFree();
        }
        window.Size = home;
        return ok;
    }

    private static Surface BuildSurface(PuppetShape shape, Transform3D[] world)
    {
        int n = shape.Corners.Length, triangles = n / 3;
        var skinned = new Vector3[shape.Vertices.Length];
        for (int v = 0; v < skinned.Length; v++)
        {
            var p = new Vector3(shape.Vertices[v].x, shape.Vertices[v].y, shape.Vertices[v].z);
            skinned[v] = world[shape.BoneA[v]] * p * shape.WeightA[v] + world[shape.BoneB[v]] * p * (1f - shape.WeightA[v]);
        }
        var P = new Vector3[n];
        for (int s = 0; s < n; s++) P[s] = skinned[shape.Corners[s]];
        var welds = new System.Collections.Generic.Dictionary<(long, long, long), int>();
        var weld = new int[n];
        for (int s = 0; s < n; s++)
        {
            var key = ((long)Math.Round(P[s].X * 1e5), (long)Math.Round(P[s].Y * 1e5), (long)Math.Round(P[s].Z * 1e5));
            if (!welds.TryGetValue(key, out weld[s])) welds[key] = weld[s] = welds.Count;
        }
        var facet = new Vector3[triangles]; var altitude = new float[n]; var smallest = new float[triangles];
        var vertexNormal = new Vector3[welds.Count];
        for (int t = 0; t < triangles; t++)
        {
            var outward = -(P[3 * t + 1] - P[3 * t]).Cross(P[3 * t + 2] - P[3 * t]);
            float twiceArea = outward.Length();
            facet[t] = outward / twiceArea;
            smallest[t] = float.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                vertexNormal[weld[3 * t + k]] += outward;
                altitude[3 * t + k] = twiceArea / (P[3 * t + (k + 1) % 3] - P[3 * t + (k + 2) % 3]).Length();
                smallest[t] = Math.Min(smallest[t], altitude[3 * t + k]);
            }
        }
        for (int v = 0; v < vertexNormal.Length; v++) vertexNormal[v] = vertexNormal[v].Normalized();
        var sides = new System.Collections.Generic.Dictionary<(int, int), System.Collections.Generic.List<int>>();
        for (int s = 0; s < n; s++)
        {
            int t = s / 3, k = s % 3, p = weld[3 * t + (k + 1) % 3], q = weld[3 * t + (k + 2) % 3];
            var key = p < q ? (p, q) : (q, p);
            if (!sides.TryGetValue(key, out var list)) sides[key] = list = new System.Collections.Generic.List<int>();
            list.Add(s);
        }
        // An edge two triangles share turns by half the angle between their facets, positive where it is convex (the neighbour's far corner
        // lies behind this facet's plane); a fold, whose sides disagree on that, and an edge with other than two sides turn by nothing.
        var crease = new float[n];
        foreach (var list in sides.Values)
        {
            if (list.Count != 2) continue;
            int a = list[0], b = list[1];
            bool convexA = facet[a / 3].Dot(P[b] - P[3 * (a / 3) + (a % 3 + 1) % 3]) < 0, convexB = facet[b / 3].Dot(P[a] - P[3 * (b / 3) + (b % 3 + 1) % 3]) < 0;
            if (convexA != convexB) continue;
            float half = 0.5f * facet[a / 3].AngleTo(facet[b / 3]);
            crease[a] = crease[b] = convexA ? half : -half;
        }
        return new Surface(P, weld, facet, altitude, smallest, vertexNormal, crease);
    }

    private bool NormalView(NormalViewSpec spec, Image image, Camera3D camera, Surface surface)
    {
        string label = spec.Label;
        var (P, weld, facet, altitude, smallest, vertexNormal, crease) = surface;
        int w = image.GetWidth(), h = image.GetHeight(), triangles = P.Length / 3;

        // Depth raster: the nearest triangle under each pixel centre, with its barycentrics.
        var origin = camera.GlobalPosition; var basis = camera.GlobalBasis;
        float tanHalf = Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2f), aspect = (float)w / h;
        Vector3 Ray(int x, int y) => (basis * new Vector3((2f * (x + 0.5f) / w - 1f) * tanHalf * aspect, -(2f * (y + 0.5f) / h - 1f) * tanHalf, -1f)).Normalized();
        var depth = new float[w * h]; var tri = new int[w * h]; var bary = new Vector3[w * h];
        Array.Fill(depth, float.MaxValue); Array.Fill(tri, -1);
        var scale = new Vector2(w, h) / GetViewport().GetVisibleRect().Size;
        for (int t = 0; t < triangles; t++)
        {
            Vector3 a = P[3 * t], b = P[3 * t + 1], c = P[3 * t + 2];
            if (camera.IsPositionBehind(a) || camera.IsPositionBehind(b) || camera.IsPositionBehind(c)) continue;
            var pa = camera.UnprojectPosition(a) * scale; var pb = camera.UnprojectPosition(b) * scale; var pc = camera.UnprojectPosition(c) * scale;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X))) - 1), x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X))) + 1);
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y))) - 1), y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y))) + 1);
            Vector3 e1 = b - a, e2 = c - a;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var d = Ray(x, y);
                    var pv = d.Cross(e2);
                    float det = e1.Dot(pv);
                    if (Mathf.Abs(det) < 1e-12f) continue;
                    var tv = origin - a;
                    float u = tv.Dot(pv) / det;
                    var qv = tv.Cross(e1);
                    float vv = d.Dot(qv) / det;
                    if (u < 0 || vv < 0 || u + vv > 1) continue;
                    float dist = e2.Dot(qv) / det;
                    if (dist <= 0 || dist >= depth[y * w + x]) continue;
                    depth[y * w + x] = dist; tri[y * w + x] = t; bary[y * w + x] = new Vector3(1 - u - vv, u, vv);
                }
        }

        // The barycentrics of a point on triangle t's plane, and where a ray meets that plane.
        Vector3 Bary(int t, Vector3 p)
        {
            Vector3 a = P[3 * t], v0 = P[3 * t + 1] - a, v1 = P[3 * t + 2] - a, v2 = p - a;
            float d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1), d20 = v2.Dot(v0), d21 = v2.Dot(v1), denom = d00 * d11 - d01 * d01;
            float vb = (d11 * d20 - d01 * d21) / denom, wb = (d00 * d21 - d01 * d20) / denom;
            return new Vector3(1 - vb - wb, vb, wb);
        }
        Vector3 OnPlane(int t, Vector3 d) => origin + d * ((P[3 * t] - origin).Dot(facet[t]) / d.Dot(facet[t]));

        float screenHeight = h;
        var errors = new System.Collections.Generic.List<float>(); var band = new System.Collections.Generic.List<float>(); var bandTilt = new System.Collections.Generic.List<float>();
        var clamped = new System.Collections.Generic.List<float>();
        var cornerErr = new System.Collections.Generic.List<float>(); var cornerEffect = new System.Collections.Generic.List<float>(); var sharp = new System.Collections.Generic.List<float>();
        var drawnNormal = new Vector3[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (tri[y * w + x] < 0) continue;
                var px = image.GetPixel(x, y);
                drawnNormal[y * w + x] = (basis * new Vector3(px.R * 2f - 1f, px.G * 2f - 1f, px.B * 2f - 1f)).Normalized();
            }
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int t = tri[y * w + x];
                if (t < 0) continue;
                bool interior = true;
                for (int dy = -1; dy <= 1 && interior; dy++) for (int dx = -1; dx <= 1; dx++) if (tri[(y + dy) * w + x + dx] != t) { interior = false; break; }
                if (!interior) continue;
                var bc = bary[y * w + x];
                // The distance to each edge here and one pixel over in x and in y on the same plane: metres per pixel across the edge.
                var bx = Bary(t, OnPlane(t, Ray(x + 1, y))); var by = Bary(t, OnPlane(t, Ray(x, y + 1)));
                var tilt = Vector3.Zero; bool inBand = false, wasClamped = false, inSharp = false;
                var g = new Vector3[3]; var width = new float[3]; var distance = new float[3];
                for (int k = 0; k < 3; k++)
                {
                    var corner = P[3 * t + k]; var edgeStart = P[3 * t + (k + 1) % 3]; var edge = P[3 * t + (k + 2) % 3] - edgeStart;
                    g[k] = facet[t].Cross(edge).Normalized();
                    if (g[k].Dot(corner - edgeStart) < 0) g[k] = -g[k];
                    float alt = altitude[3 * t + k];
                    distance[k] = bc[k] * alt;
                    float mpp = Mathf.Sqrt(Mathf.Pow(bx[k] * alt - distance[k], 2) + Mathf.Pow(by[k] * alt - distance[k], 2));
                    float wanted = Mathf.Max(Look.BevelScreen * screenHeight * mpp, Look.BevelMin), cap = 0.5f * smallest[t];
                    width[k] = Mathf.Min(wanted, cap);
                    float c = crease[3 * t + k];
                    if (c == 0f) continue;
                    if (cap < wanted) wasClamped = true;
                    tilt += (1f - Mathf.SmoothStep(0f, width[k], distance[k])) * Mathf.Tan(Mathf.Clamp(c, -1.4f, 1.4f)) * g[k];
                    if (distance[k] < 0.6f * width[k] && Mathf.Abs(c) > 0.1f) inBand = true;
                    if (distance[k] < 0.6f * width[k] && Mathf.Abs(c) > SharpCrease && Mathf.Abs(c) < SharpCreaseTop) inSharp = true;
                }
                // Near a corner the bevel gives way to the smooth vertex normal: corner k is where edges i and j meet, at the distance
                // sqrt((d_i^2 + d_j^2 - 2 d_i d_j cos) / sin^2) from the pixel.
                float cornerBlend = 0f;
                for (int k = 0; k < 3; k++)
                {
                    int i = (k + 1) % 3, j = (k + 2) % 3;
                    float cosine = g[i].Dot(g[j]);
                    float r = Mathf.Sqrt(Mathf.Max((distance[i] * distance[i] + distance[j] * distance[j] - 2f * distance[i] * distance[j] * cosine) / Mathf.Max(1f - cosine * cosine, 1e-6f), 0f));
                    float reach = 1.5f * Mathf.Max(width[i], width[j]);
                    if (reach > 0f) cornerBlend = Mathf.Max(cornerBlend, 1f - Mathf.SmoothStep(0f, reach, r));
                }
                var smooth = (vertexNormal[weld[3 * t]] * bc.X + vertexNormal[weld[3 * t + 1]] * bc.Y + vertexNormal[weld[3 * t + 2]] * bc.Z).Normalized();
                var reference = (facet[t] - (1f - cornerBlend) * tilt).Normalized().Lerp(smooth, cornerBlend).Normalized();
                float err = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(drawnNormal[y * w + x].Dot(reference), -1f, 1f)));
                errors.Add(err);
                if (cornerBlend > CornerBlendFloor) { cornerErr.Add(err); cornerEffect.Add(Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(reference.Dot((facet[t] - (1f - cornerBlend) * tilt).Normalized()), -1f, 1f)))); }
                if (inSharp) sharp.Add(err);
                if (inBand) { band.Add(err); bandTilt.Add(Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(reference.Dot(facet[t]), -1f, 1f)))); }
                if (wasClamped) clamped.Add(err);
            }
        errors.Sort(); band.Sort(); bandTilt.Sort(); clamped.Sort(); cornerErr.Sort(); cornerEffect.Sort(); sharp.Sort();
        float p90 = errors.Count == 0 ? float.MaxValue : errors[(int)(errors.Count * 0.9f)];
        float bandMedian = band.Count == 0 ? float.MaxValue : band[band.Count / 2];
        float tiltMedian = bandTilt.Count == 0 ? 0 : bandTilt[bandTilt.Count / 2];
        float clampedMedian = clamped.Count == 0 ? float.MaxValue : clamped[clamped.Count / 2];
        GD.Print($"RENDER normals {label}: p90 error {p90:F2} deg over {errors.Count} interior pixels; band median error {bandMedian:F2} deg over {band.Count} pixels, reference tilt from the facet {tiltMedian:F1} deg; width-clamped triangles' median error {clampedMedian:F2} deg over {clamped.Count} pixels");
        float cornerMedian = cornerErr.Count == 0 ? float.MaxValue : cornerErr[cornerErr.Count / 2];
        float cornerP90 = cornerErr.Count == 0 ? float.MaxValue : cornerErr[(int)(cornerErr.Count * 0.9f)];
        float effectMedian = cornerEffect.Count == 0 ? 0 : cornerEffect[cornerEffect.Count / 2];
        float sharpMedian = sharp.Count == 0 ? float.MaxValue : sharp[sharp.Count / 2];
        GD.Print($"RENDER corners {label}: median error {cornerMedian:F2} deg and p90 {cornerP90:F2} over {cornerErr.Count} pixels the corner blend reaches, which it moves by {effectMedian:F1} deg (median); sharp creases ({SharpCrease} to {SharpCreaseTop} rad): median error {sharpMedian:F2} deg over {sharp.Count} band pixels");
        bool ok = true;
        var floors = (Kept: spec.MinKept, Band: spec.MinBand, Clamped: spec.MinClamped);
        if (errors.Count < floors.Kept || band.Count < floors.Band || clamped.Count < floors.Clamped) { GD.PrintErr($"RENDER FAIL: the {label} normal check reached {errors.Count} interior, {band.Count} band and {clamped.Count} clamped pixels, under {floors.Kept}, {floors.Band} and {floors.Clamped}; it proves nothing"); ok = false; }
        if (tiltMedian < MinBandTilt) { GD.PrintErr($"RENDER FAIL: the {label} bevel band's reference normal is {tiltMedian:F1} degrees from the facet's, under {MinBandTilt}; the check cannot see a bevel"); ok = false; }
        if (p90 > MaxNormalP90) { GD.PrintErr($"RENDER FAIL: the {label} drawn shading normal is {p90:F2} degrees from the CPU reference at the 90th percentile, over {MaxNormalP90}"); ok = false; }
        if (bandMedian > MaxBandMedian) { GD.PrintErr($"RENDER FAIL: across the {label} bevel band the drawn normal is {bandMedian:F2} degrees from the CPU reference (median), over {MaxBandMedian}; the bevel is not where or as large as the creases say"); ok = false; }
        if (clampedMedian > MaxClampedMedian) { GD.PrintErr($"RENDER FAIL: on the {label} triangles whose width the smallest altitude caps the drawn normal is {clampedMedian:F2} degrees from the CPU reference (median), over {MaxClampedMedian}; the cap is not where the shader puts it"); ok = false; }
        if (cornerErr.Count < spec.MinCorner || sharp.Count < spec.MinSharp) { GD.PrintErr($"RENDER FAIL: the {label} normal check reached {cornerErr.Count} corner-blend and {sharp.Count} sharp-crease pixels, under {spec.MinCorner} and {spec.MinSharp}; it proves nothing about them"); ok = false; }
        if (spec.MinCorner > 0 && effectMedian < MinCornerEffect) { GD.PrintErr($"RENDER FAIL: the {label} corner blend moves the reference normal by {effectMedian:F1} degrees (median), under {MinCornerEffect}; the check cannot see a corner blend that is off"); ok = false; }
        if (spec.MinCorner > 0 && cornerMedian > MaxCornerMedian) { GD.PrintErr($"RENDER FAIL: where the {label} corner blend reaches, the drawn normal is {cornerMedian:F2} degrees from the CPU reference (median), over {MaxCornerMedian}; the vertex normal is in the wrong space or the blend has the wrong radius"); ok = false; }
        if (spec.MinSharp > 0 && sharpMedian > MaxClampedMedian) { GD.PrintErr($"RENDER FAIL: across the {label} band of edges turning by {SharpCrease} to {SharpCreaseTop} rad (half) the drawn normal is {sharpMedian:F2} degrees from the CPU reference (median), over {MaxClampedMedian}; the crease clamp is not where the creases say"); ok = false; }
        if (spec.Continuity) ok &= Continuity(spec, tri, depth, drawnNormal, weld, w, h);
        return ok;
    }

    /// <summary>
    /// The drawn normal across triangle boundaries: horizontally or vertically adjacent pixels on different triangles that share a vertex,
    /// at depths within 4 mm, must hardly differ, however the bevel falls on the two sides. Compares drawn pixels with drawn pixels.
    /// </summary>
    private static bool Continuity(NormalViewSpec spec, int[] tri, float[] depth, Vector3[] drawnNormal, int[] weld, int w, int h)
    {
        bool Shares(int t, int u) { for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (weld[3 * t + i] == weld[3 * u + j]) return true; return false; }
        var angles = new System.Collections.Generic.List<float>();
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                int i = y * w + x, t = tri[i];
                if (t < 0) continue;
                foreach (int j in new[] { i + 1, i + w })
                {
                    int u = tri[j];
                    if (u < 0 || u == t || Math.Abs(depth[i] - depth[j]) > 0.004f || !Shares(t, u)) continue;
                    angles.Add(Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(drawnNormal[i].Dot(drawnNormal[j]), -1f, 1f))));
                }
            }
        string label = spec.Label;
        angles.Sort();
        float over = angles.Count == 0 ? 1f : angles.Count(a => a > 30f) / (float)angles.Count;
        float p90 = angles.Count == 0 ? float.MaxValue : angles[(int)(angles.Count * 0.9f)];
        GD.Print($"RENDER continuity {label}: {angles.Count} pairs across triangle boundaries, {over:P2} over 30 deg, p90 {p90:F2} deg");
        bool ok = true;
        if (angles.Count < MinPairs) { GD.PrintErr($"RENDER FAIL: the {label} continuity check reached {angles.Count} pairs, under {MinPairs}; it proves nothing"); ok = false; }
        if (over > spec.MaxAcross30) { GD.PrintErr($"RENDER FAIL: {over:P2} of the {label} pixel pairs across triangle boundaries differ by more than 30 degrees, over {spec.MaxAcross30:P1}"); ok = false; }
        if (p90 > spec.MaxAcrossP90) { GD.PrintErr($"RENDER FAIL: the {label} pixel pairs across triangle boundaries differ by {p90:F2} degrees at the 90th percentile, over {spec.MaxAcrossP90}"); ok = false; }
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
