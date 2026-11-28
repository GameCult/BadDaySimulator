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
/// The headless smoke: runs the real Main scene, drives it through the HUD's own controls and checks what the client
/// promises against a headless BDS run in the same process. Exit code 0 passes, 1 fails.
/// <c>godot --headless --path client res://tests/Smoke.tscn</c>
/// </summary>
public partial class Smoke : Node
{
    private const ulong Seed = 1;
    private const double WatchdogSeconds = 150;
    // The joint rings' bound, in micrometres (lodur:ruling:deterministic-bound-margin and deterministic-bound-zero-reading): shipped worst 0 (counted as half a micrometre),
    // the least claimed mutant (split rings: each copy weighted to its own part only) 75768 at tick 240; sqrt(0.5 x 75768) = 195, separation 1.5e5.
    private const double RingGapMicrometres = 195;

    private readonly List<string> _failures = new();
    private Main _main = null!;
    private IBodyRun? _lastSession;
    private long _lastTick;
    private bool _done;

    private Hud Hud => _main.Hud;
    private BodyPainBoxSession Session => _main.PainBox;
    private OfficeBodySession Office => _main.Office;

    public override void _Ready() => _ = RunAsync();

    public override void _Process(double delta)
    {
        if (!_done && Time.GetTicksMsec() > WatchdogSeconds * 1000)
        {
            Check(false, $"no result after {WatchdogSeconds} s");
            Finish();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            _main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
            AddChild(_main);
            if (_main.Failure is { } failure) Check(false, "Main could not start a session: " + failure);
            else await ChamberScenario();
        }
        catch (Exception e)
        {
            Check(false, e.ToString());
        }
        Finish();
    }

    private async Task ChamberScenario()
    {
        CheckEngineAndScene();
        if (_failures.Count > 0) return;

        // Slow motion, by wall clock: a quarter of the session steps per real second, the same run.
        await Frames(10);
        double normal = await StepsPerSecond(1000);
        Hud.SlowToggle.ButtonPressed = true; // the real control
        double slow = await StepsPerSecond(2000);
        Check(slow > 0.15 * normal && slow < 0.4 * normal, $"slow motion runs {slow:F0} steps per second against {normal:F0}; wanted about a quarter");
        Hud.SlowToggle.ButtonPressed = false;
        Check(Engine.PhysicsTicksPerSecond == 120, $"slow motion off leaves {Engine.PhysicsTicksPerSecond} physics ticks per second");

        // The rest runs the physics clock fast; the session still steps 1/120 s per frame.
        Engine.PhysicsTicksPerSecond = 1200;

        // A Reset is not a new set of bodies: the carriers and meshes made at startup stay. Checked after three resets so a rebuild per
        // Reset fails here, not after hundreds.
        for (ulong seed = 1; seed <= 3; seed++) { PressReset(seed); await Frame(); }
        CheckMeshBuilds("by the third Reset");
        CameraChecks.Run(failure => Check(false, "camera: " + failure), _main);
        // The camera writes its transform on input only: one written between frames stays through them.
        var orbit = _main.GetNode<OrbitCamera>("Camera");
        var home = orbit.Transform;
        var moved = new Transform3D(home.Basis.Rotated(Vector3.Up, 1f), home.Origin + new Vector3(0.1f, 0, 0));
        orbit.Transform = moved;
        for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(orbit.Transform == moved, $"the camera moved itself from {moved.Origin} to {orbit.Transform.Origin} between frames");
        orbit.Transform = home;
        if (_failures.Count > 0) return;

        // Seed 1 at the default intensity against the headless run: events, and the hand at ticks 240 and 1500.
        PressReset(1);
        var reference = Run(new PainBoxParameters().BoxIntensity, 1, 1500);
        await UntilTick(240);
        CheckHand(reference.Hand[239], "tick 240");
        CheckPoses("tick 240");
        CheckSkin("tick 240");
        await UntilTick(1500);
        CheckEvents(reference, "seed 1, default intensity");
        CheckHand(reference.Hand[^1], "tick 1500");

        // The slider sets the next run's intensity; a high one breaks the will, and the run ends.
        Hud.IntensitySlider.Value = 6;
        Check(_main.Intensity == 6, $"the slider set intensity {_main.Intensity}, not 6");
        PressReset(3);
        reference = Run(6, 3, 1500);
        await UntilTick(1500);
        CheckEvents(reference, "seed 3, intensity 6");
        Check(Session.Finished && _main.Events.Any(e => (e.Events & Lodur.TickEvents.WillBroke) != 0), "intensity 6 did not break the will");

        // A reset puts every body at its pose now, not on the way from where the last run left it. A reset from the UI
        // lands between physics frames, and on a slow physics clock the next render frame comes before the next tick.
        Engine.PhysicsTicksPerSecond = 10;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        PressReset(4);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        foreach (var id in Session.View.StableId.ToArray())
        {
            var shown = _main.Bodies.InterpolatedPoseOf(id);
            var pose = _main.Bodies.PoseOf(id);
            Check(shown is { } a && pose is { } b && a.Origin.DistanceTo(b.Origin) < 1e-2f, $"after Reset body {id} is rendered at {shown?.Origin}, not at {pose?.Origin}");
        }
        Engine.PhysicsTicksPerSecond = 1200;

        // Every reset honours its seed, whatever it replaces.
        Hud.IntensitySlider.Value = new PainBoxParameters().BoxIntensity;
        foreach (ulong seed in new ulong[] { 2, 7, 123456789 })
        {
            PressReset(seed);
            reference = Run(_main.Intensity, seed, 150);
            await UntilTick(150);
            Check(Session.Pain == reference.Pain && Session.Strain == reference.Strain, $"seed {seed}: pain {Session.Pain} differs from the headless run's {reference.Pain}");
            CheckHand(reference.Hand[^1], $"seed {seed} tick 150");
        }

        // Many resets without leaking native worlds (the native library caps them near 128).
        for (int i = 0; i < 400; i++)
        {
            PressReset(1000 + (ulong)i);
            await Frame();
        }
        Check(_main.Seed == 1399, $"after 400 resets the seed is {_main.Seed}");
        CheckMeshBuilds("after 400 Resets");

        // Box off, through the HUD button, at tick 600: the run ends with the will intact and the screen shows the box off.
        PressReset(Seed);
        await UntilTick(600);
        Check(!Session.Finished, "the run was over before tick 600; the box-off probe needs the will intact");
        Hud.BoxOffButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        Check(Session.Finished && Session.Result().Outcome == Outcome.BoxDisabled, "Box off did not end the run as BoxDisabled within one tick");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); // the HUD draws in _Process, after this frame's signal
        Check(_main.Chamber.Glow == 0f, $"the chamber still glows ({_main.Chamber.Glow}) with the box off");
        Check(Hud.Readout.Contains("box intensity 0.00"), "the readout does not show the box off: " + Hud.Readout.Split('\n')[1]);

        // Box off after the will has broken: the run is over, and the box the player switches off still goes dark.
        PressReset(Seed);
        while (!Session.Finished) await Frame();
        Check(Session.Result().Outcome == Outcome.WillBroke && Session.BoxOn, $"the run ended {Session.Result().Outcome} with the box on: expected WillBroke");
        await Frame();
        Check(_main.Chamber.Glow > 0.1f, $"the chamber is dark ({_main.Chamber.Glow}) after the will broke, so the probe proves nothing");
        Hud.BoxOffButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(!Session.BoxOn && _main.Chamber.Glow == 0f && Hud.Readout.Contains("box intensity 0.00 off"), $"after Box off on a finished run: on {Session.BoxOn}, glow {_main.Chamber.Glow}");

        await OfficeScenario();
    }

    /// <summary>The office, chosen through the HUD's picker: the bars, Silence, and the way back to the chamber.</summary>
    private async Task OfficeScenario()
    {
        Hud.SeedBox.Value = Seed;
        Hud.ScenarioPicker.Select((int)Scenario.Office);
        Hud.ScenarioPicker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)Scenario.Office);
        Check(_main.Scenario == Scenario.Office && _main.Seed == Seed, $"the picker left Main in {_main.Scenario}, seed {_main.Seed}");
        var office = Office;
        Check(!_main.Chamber.Visible && _main.Chamber.Glow == 0f, "the chamber is drawn in the office");
        await UntilTick(120);

        // The knife: where the player clicks. An Alt+left press is the camera's emulated middle button and cuts nothing; a plain left press on
        // LowerArm L's drawn centre opens one cut there, through the same CutAt a direct call takes.
        int arm = SeatedPuppet.Plan.PartIndex("LowerArm", Lodur.Side.Left);
        var camera = _main.GetNode<OrbitCamera>("Camera");
        Vector2 at = camera.UnprojectPosition(_main.Bodies.PoseOf(SeatedPuppet.FirstStableId + (ulong)arm)!.Value.Origin);
        Click(at, alt: true);
        Check(_main.Cuts.Count == 0, "an Alt+left press cut");
        Click(at, alt: false);
        Check(_main.Cuts.Count == 1 && _main.Cuts[0].Part == "LowerArm L", $"a left press on LowerArm L's centre left cuts [{string.Join(", ", _main.Cuts)}]");
        Check(office.Result().Timeline.Count > 0, "no timeline after a cut");
        Check(Hud.Readout.Contains("airway "), "the readout has no airway line");
        await CheckAltGuard(at);
        await UntilTick(600);

        // The blood drawn is the blood the world holds: a drop per live fluid lane, a disc per live wet lane, counted from the lanes here.
        var view = office.View;
        int liveDrops = 0, livePatches = 0;
        for (int i = 0; i < view.ParticleCount; i++) if (view.ParticleLive[i] != 0 && (view.ParticlePhase[i] & Box3DSoaPhase.Fluid) != 0) liveDrops++;
        for (int i = 0; i < view.WetCount; i++) if (view.WetLive[i] != 0) livePatches++;
        Check(liveDrops + livePatches > 0, "no blood in the world 480 ticks after the cut");
        Check(_main.Blood.DropCount == liveDrops && _main.Blood.PatchCount == livePatches,
            $"blood draws {_main.Blood.DropCount} drops and {_main.Blood.PatchCount} patches; the lanes hold {liveDrops} and {livePatches}");

        CheckBloodPlaced(view);

        // The bars are the session's will and arousal, to the bit, once a frame has drawn them (stepping paused so the session holds still).
        // Run on until the cut has cost his will, so a bar bound to resolve instead shows a different number.
        // The knife is severity 20 (the arm bleeds for 191 s), so the will falls behind resolve later than tick 1800: run to the first tick it has, within the break.
        while (office.Will >= office.Resolve - 0.1 && office.TickIndex < 12000) await Frame();
        Check(office.Will < office.Resolve - 0.1, $"will {office.Will} and resolve {office.Resolve} are too close at tick {office.TickIndex} for the bars to tell them apart");
        _main.SetPhysicsProcess(false);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(Hud.WillBar.Value == office.Will && Hud.StressBar.Value == office.Arousal,
            $"bars show will {Hud.WillBar.Value} and stress {Hud.StressBar.Value}; the session has {office.Will} and {office.Arousal}");
        _main.SetPhysicsProcess(true);
        _lastSession = null;

        // Silence is the HUD's button for the stressor, and the session logs it.
        Check(Hud.SilenceButtons.Count == Main.OfficeParameters.Cause.Stressors.Count, $"{Hud.SilenceButtons.Count} Silence buttons for {Main.OfficeParameters.Cause.Stressors.Count} stressors");
        int silenced = office.Result().Timeline.Count(e => e.Kind == StressEntryKind.Silenced);
        Hud.SilenceButtons["printer"].EmitSignal(BaseButton.SignalName.Pressed);
        Check(office.Result().Timeline.Count(e => e.Kind == StressEntryKind.Silenced) == silenced + 1, "Silence did not put a Silenced row in the result");
        await Frame();

        // Back to the chamber: the office run is gone and the chamber is drawn.
        Hud.ScenarioPicker.Select((int)Scenario.Chamber);
        Hud.ScenarioPicker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)Scenario.Chamber);
        await Frame();
        Check(_main.Scenario == Scenario.Chamber && _main.Run is BodyPainBoxSession, "switching back did not make the pain box the live run");
        Check(_main.Chamber.Visible, "the chamber is hidden after switching back");
        Check(Throws(() => _ = office.TickIndex), "the office run was not disposed by the switch");
    }

    /// <summary>Each drop is drawn at its lane's point and 2 x its radius wide; each disc at its body's pose applied to its point, lifted, and as wide as its radius (both in lane order).</summary>
    private void CheckBloodPlaced(Box3DSoaView view)
    {
        int d = 0;
        for (int i = 0; i < view.ParticleCount; i++)
        {
            if (view.ParticleLive[i] == 0 || (view.ParticlePhase[i] & Box3DSoaPhase.Fluid) == 0) continue;
            var t = _main.Blood.Drops[d++];
            Check(t.Origin == new Vector3(view.ParticleX[i], view.ParticleY[i], view.ParticleZ[i]) && Math.Abs(t.Basis.Scale.X - 2f * view.ParticleRadius[i]) < 1e-6f,
                $"drop {d - 1} is drawn at {t.Origin} size {t.Basis.Scale.X}; lane {i} is at ({view.ParticleX[i]}, {view.ParticleY[i]}, {view.ParticleZ[i]}), radius {view.ParticleRadius[i]}");
        }
        int p = 0;
        for (int i = 0; i < view.WetCount; i++)
        {
            if (view.WetLive[i] == 0) continue;
            int b = view.WetBody[i];
            if (b < 0) { p++; continue; }
            var q = new Quaternion(view.RotationX[b], view.RotationY[b], view.RotationZ[b], view.RotationW[b]);
            var want = new Vector3(view.PositionX[b], view.PositionY[b], view.PositionZ[b]) + q * new Vector3(view.WetX[i], view.WetY[i], view.WetZ[i]) + Vector3.Up * BloodRenderer.PatchLift;
            var t = _main.Blood.Patches[p++];
            Check(t.Origin.DistanceTo(want) < 1e-5f && Math.Abs(t.Basis.Scale.X - view.WetRadius[i]) < 1e-6f,
                $"patch {p - 1} is drawn at {t.Origin} radius {t.Basis.Scale.X}; lane {i} puts it at {want}, radius {view.WetRadius[i]}");
        }
    }

    /// <summary>The knife leaves the camera's presses alone in every mode: with emulation off, and while a middle drag is under way, an Alt+left press cuts nothing.</summary>
    private async Task CheckAltGuard(Vector2 at)
    {
        var camera = _main.GetNode<OrbitCamera>("Camera");
        int cuts = _main.Cuts.Count;
        camera.Emulate3ButtonMouse = false;
        Click(at, alt: true);
        camera.Emulate3ButtonMouse = true;
        Check(_main.Cuts.Count == cuts, "an Alt+left press cut with camera emulation off");
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true, Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Middle }, true);
        Click(at, alt: true);
        Check(_main.Cuts.Count == cuts, "an Alt+left press cut during a middle drag");
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false, Position = at, GlobalPosition = at }, true);
        await Task.CompletedTask;
    }

    /// <summary>A left press and release at <paramref name="at"/> (viewport coordinates), pushed through the viewport like the mouse's.</summary>
    private void Click(Vector2 at, bool alt)
    {
        foreach (bool pressed in new[] { true, false })
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at, AltPressed = alt, ButtonMask = pressed ? MouseButtonMask.Left : 0 }, inLocalCoords: true);
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private void CheckEngineAndScene()
    {
        Check(Engine.PhysicsTicksPerSecond == 120, $"physics ticks per second is {Engine.PhysicsTicksPerSecond}, not 120");
        Check((string)ProjectSettings.GetSetting("physics/3d/physics_engine") == "Dummy", "Godot's 3D physics engine is not Dummy");
        Check(!_main.GetWorld3D().Space.IsValid, "Godot has a physics space");
        Check(Count(_main, n => n is CollisionObject3D || n.GetClass().StartsWith("Collision")) == 0, "Godot physics nodes are in the scene");
        Check(_main.IsPhysicsInterpolatedAndEnabled() && (bool)ProjectSettings.GetSetting("physics/common/physics_interpolation"), "physics interpolation is off");
        Check(_main.Seed == Seed, "Main did not start from the smoke's seed");

        // The look: PS1. Every surface is the PS1 shader and the shader lights it (no Light3D), the quantising pass sits below the HUD, the
        // 3D has no MSAA, and ps1_res is the window over its block size.
        var ps1 = GD.Load<Shader>("res://ps1.gdshader");
        var meshes = _main.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>().ToArray();
        Check(meshes.Length > 0 && meshes.All(m => m.MaterialOverride is ShaderMaterial sm && (sm.Shader == ps1 || sm.Shader.Code.EndsWith(ps1.Code))), "a mesh is not drawn with res://ps1.gdshader");
        Check(Count(_main, n => n is Light3D) == 0, "the scene has a Light3D; the shader lights it");
        Check(_main.GetNode<CanvasLayer>("Post").Layer < Hud.Layer, "the post pass does not sit below the HUD");
        Check((int)ProjectSettings.GetSetting("rendering/anti_aliasing/quality/msaa_3d", 0) == 0, "msaa_3d is on");
        int height = GetWindow().Size.Y, block = Math.Max(1, (int)Math.Round(height / (double)Look.Lines, MidpointRounding.AwayFromZero));
        Check(Math.Abs(_main.Ps1Res.Y - height / (float)block) < 1e-4f, $"ps1_res.y is {_main.Ps1Res.Y}, not {height} / {block}");
        Check(Look.Scenery.Luminance < 0.3f, "the floor is not dark");
        var glow = (Vector3)_main.Chamber.Material.GetShaderParameter("tint");
        Check(Math.Max(glow.X, Math.Max(glow.Y, glow.Z)) > 0.7f, $"the chamber is {glow}, not light");
        var environment = _main.GetChildren().OfType<WorldEnvironment>().Single().Environment;
        Check(environment.BackgroundColor.Luminance < 0.2f, "the background is not dark grey");
        Check(environment.Sky is null && environment.TonemapMode == Godot.Environment.ToneMapper.Linear && !environment.GlowEnabled, "the environment is not a linear colour environment with no sky and no glow");
    }

    /// <summary>Every body with a shape that Fella's skin does not draw has one primitive mesh, made once (the anchor has none); the skin is one surface, made once.</summary>
    private void CheckMeshBuilds(string when)
    {
        var view = Session.View;
        int shaped = 0;
        for (int i = 0; i < view.Count; i++) if ((Box3DSoaKind)view.Kind[i] != Box3DSoaKind.Anchor && SeatedPuppet.PartOf(view.StableId[i]) is null) shaped++;
        Check(_main.Bodies.MeshBuilds == shaped, $"{when}: {_main.Bodies.MeshBuilds} body meshes were made for {shaped} bodies with a shape the skin does not draw; a Reset makes none");
        Check(_main.Bodies.Skin.MeshBuilds == 1, $"{when}: the skin was built {_main.Bodies.Skin.MeshBuilds} times, not once");
    }

    /// <summary>
    /// Fella's skin: every part of the plan owns vertices, and the copies of every joint-ring vertex are drawn together while the parts are joined.
    /// The ring-gap bound is in micrometres (lodur:ruling:deterministic-bound-zero-reading: a shipped zero counts as half a micrometre).
    /// </summary>
    private void CheckSkin(string when)
    {
        var skin = _main.Bodies.Skin;
        for (int part = 0; part < SeatedPuppet.Plan.Parts.Count; part++)
            Check(skin.OwnedVertices(part) > 0, $"{when}: part {part} ({SeatedPuppet.Plan.Parts[part].Label}) owns no skin vertices");
        double gap = skin.RingGap() * 1e6;
        Check(gap < RingGapMicrometres, $"{when}: joint-ring copies are drawn {gap:F1} micrometres apart (bound {RingGapMicrometres})");
        GD.Print($"SKIN {skin.VertexCount} vertices, ring gap {gap:F1} um, deform {skin.MedianDeformMicroseconds(1000):F1} us per frame (median of 1000, headless)");
    }

    private void CheckPoses(string when)
    {
        var view = Session.View;
        Check(_main.Bodies.BodyCount == view.Count, $"{when}: {_main.Bodies.BodyCount} bodies drawn for {view.Count} lanes");
        for (int i = 0; i < view.Count; i++)
        {
            var id = view.StableId[i];
            if (_main.Bodies.PoseOf(id) is not { } pose) { Check(false, $"{when}: no body drawn for stable id {id}"); continue; }
            var want = new Vector3(view.PositionX[i], view.PositionY[i], view.PositionZ[i]);
            Check(pose.Origin == want, $"{when}: body {id} at {pose.Origin}, lanes say {want}");
            var q = new Quaternion(view.RotationX[i], view.RotationY[i], view.RotationZ[i], view.RotationW[i]);
            Check(Math.Abs(pose.Basis.GetRotationQuaternion().Dot(q)) > 0.99999, $"{when}: body {id} is rotated differently from its lane");
        }
    }

    private void CheckEvents(HeadlessRun reference, string what)
    {
        var got = _main.Events.Select(e => (e.Tick, e.Events)).ToList();
        var want = reference.Events.Select(e => (e.Tick, e.Events)).ToList();
        Check(want.Count > 0, $"{what}: the headless run reported no events");
        Check(got.SequenceEqual(want), $"{what}: events {Format(got)} differ from the headless run's {Format(want)}");
    }

    private void CheckHand(HandSample want, string when)
    {
        var h = Session.Hand;
        Check(Bits(h.X) == Bits(want.X) && Bits(h.Y) == Bits(want.Y) && Bits(h.Z) == Bits(want.Z),
            $"hand at {when} ({h.X}, {h.Y}, {h.Z}) differs from the headless run ({want.X}, {want.Y}, {want.Z})");
    }

    /// <summary>Presses the HUD's Reset with the seed typed in its seed box.</summary>
    private void PressReset(ulong seed)
    {
        Hud.SeedBox.Value = seed;
        Hud.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
        Check(_main.Seed == seed, $"Reset started seed {_main.Seed}, not {seed}");
        _lastSession = null;
    }

    /// <summary>Waits one physics frame, and checks the session took exactly one step in the frame before it.</summary>
    private async Task Frame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (_main.Failure is { } failure) throw new InvalidOperationException(failure);
        var s = _main.Run;
        if (ReferenceEquals(s, _lastSession) && !s.Finished) Check(s.TickIndex == _lastTick + 1, $"{s.TickIndex - _lastTick} steps in one physics frame");
        _lastSession = s;
        _lastTick = s.TickIndex;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await Frame();
    }

    private async Task UntilTick(long tick)
    {
        while (_main.Run.TickIndex < tick && !_main.Run.Finished) await Frame();
    }

    /// <summary>Session steps per real second over the next <paramref name="milliseconds"/>.</summary>
    private async Task<double> StepsPerSecond(ulong milliseconds)
    {
        ulong t0 = Time.GetTicksMsec();
        long tick0 = Session.TickIndex;
        while (Time.GetTicksMsec() - t0 < milliseconds) await Frame();
        return (Session.TickIndex - tick0) * 1000.0 / (Time.GetTicksMsec() - t0);
    }

    private readonly record struct HeadlessRun(List<BodyEvent> Events, List<HandSample> Hand, double Pain, double Strain);

    /// <summary>The same run without Godot: stepped until <paramref name="ticks"/> or the end, events drained as the client does.</summary>
    private static HeadlessRun Run(double intensity, ulong seed, long ticks)
    {
        using var s = new BodyPainBoxSession(Main.ParametersFor(intensity), seed);
        var events = new List<BodyEvent>();
        var hand = new List<HandSample>();
        var buffer = new BodyEvent[8];
        while (s.TickIndex < ticks && !s.Finished)
        {
            s.Step();
            hand.Add(s.Hand);
            int n;
            while ((n = s.DrainEvents(buffer)) > 0) events.AddRange(buffer.Take(n));
        }
        return new HeadlessRun(events, hand, s.Pain, s.Strain);
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        foreach (var f in _failures) GD.PrintErr("SMOKE FAIL: " + f);
        GD.Print(_failures.Count == 0 ? "SMOKE PASS" : $"SMOKE FAILED ({_failures.Count})");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    private void Check(bool ok, string failure)
    {
        if (!ok) _failures.Add(failure);
    }

    private static string Format(List<(long Tick, Lodur.TickEvents Events)> events) =>
        string.Join(",", events.Select(e => $"{e.Tick}:{e.Events}"));

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static int Count(Node root, Func<Node, bool> match)
    {
        int n = match(root) ? 1 : 0;
        foreach (var child in root.GetChildren()) n += Count(child, match);
        return n;
    }
}
