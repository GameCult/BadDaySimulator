using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BadDay.Core;
using BadDay.Ymir;
using Godot;

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

    /// <summary>The puppet mesh's triangle range: 0.6x to 1.6x the doll's measured 1332, which FacetError, SeatBand, SeatError and BallError decide.</summary>
    private const int MinTriangles = 800, MaxTriangles = 2130;

    private readonly List<string> _failures = new();
    private Main _main = null!;
    private BodyPainBoxSession? _lastSession;
    private long _lastTick;
    private bool _done;

    private Hud Hud => _main.Hud;
    private BodyPainBoxSession Session => _main.Session;

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
            else await Scenario();
        }
        catch (Exception e)
        {
            Check(false, e.ToString());
        }
        Finish();
    }

    private async Task Scenario()
    {
        CheckEngineAndScene();
        PuppetChecks.Run(failure => Check(false, "puppet: " + failure), Session.View);
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

        // A Reset is not a topology change: the mesh stays the one built at startup. Checked after three resets so a rebuild per
        // Reset fails here, not after hundreds.
        for (ulong seed = 1; seed <= 3; seed++) { PressReset(seed); await Frame(); }
        Check(_main.Bodies.PuppetMeshBuilds == 1, $"the puppet mesh was built {_main.Bodies.PuppetMeshBuilds} times by the third Reset; the signature never changed, so once");
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
        CheckPuppetMesh("intensity 6, tick 1500");

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
        Check(_main.Bodies.PuppetMeshBuilds == 1, $"the puppet mesh was built {_main.Bodies.PuppetMeshBuilds} times; the signature never changed, so once");

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
    }

    private void CheckEngineAndScene()
    {
        Check(Engine.PhysicsTicksPerSecond == 120, $"physics ticks per second is {Engine.PhysicsTicksPerSecond}, not 120");
        Check((string)ProjectSettings.GetSetting("physics/3d/physics_engine") == "Dummy", "Godot's 3D physics engine is not Dummy");
        Check(!_main.GetWorld3D().Space.IsValid, "Godot has a physics space");
        Check(Count(_main, n => n is CollisionObject3D || n.GetClass().StartsWith("Collision")) == 0, "Godot physics nodes are in the scene");
        Check(_main.IsPhysicsInterpolatedAndEnabled() && (bool)ProjectSettings.GetSetting("physics/common/physics_interpolation"), "physics interpolation is off");
        Check(_main.Seed == Seed, "Main did not start from the smoke's seed");

        // The look: white on dark grey, flat.
        var puppet = _main.Bodies.PuppetColor;
        Check(puppet.Luminance > 0.7f && puppet.S < 0.1f, $"the puppet is {puppet}, not white");
        Check(_main.Bodies.SceneryMaterial.AlbedoColor.Luminance < 0.3f, "the floor is not dark");
        var glow = _main.Chamber.Material.AlbedoColor;
        Check(glow.V > 0.7f && glow.S < 0.3f, $"the chamber is {glow}, not light");
        var environment = _main.GetChildren().OfType<WorldEnvironment>().Single().Environment;
        Check(environment.BackgroundColor.Luminance < 0.2f, "the background is not dark grey");
        // The studio: a shadowing key and two rims. Where the camera turns the key's light turns with it, and it keeps coming from the studio's direction in the camera's space.
        var rig = _main.FindChildren("*", "DirectionalLight3D", true, false).Cast<DirectionalLight3D>().ToArray();
        Check(rig.Length == 3 && rig.Count(l => l.ShadowEnabled) == 1, $"the scene has {rig.Length} directional lights, {rig.Count(l => l.ShadowEnabled)} with a shadow, not a key and two rims");
        var camera = _main.GetNode<Camera3D>("Camera");
        var home = camera.GlobalTransform;
        foreach (float turn in new[] { 0f, 2.1f })
        {
            camera.GlobalTransform = new Transform3D(home.Basis.Rotated(Vector3.Up, turn), home.Origin);
            foreach (var key in rig.Where(l => l.ShadowEnabled))
            {
                var shinesAlong = camera.GlobalBasis.Inverse() * -key.GlobalBasis.Z; // the key's direction of travel in the camera's space
                Check((shinesAlong + Look.KeyDirection).Length() < 1e-3f, $"the key shines along {shinesAlong} in the camera's space after the camera turned {turn} rad; the studio does not follow the camera");
            }
        }
        camera.GlobalTransform = home;
        Check(environment.Sky is null && environment.TonemapMode == Godot.Environment.ToneMapper.Linear && !environment.GlowEnabled, "the environment is not a linear colour environment with no sky and no glow");
    }

    /// <summary>
    /// The puppet's mesh as it is drawn: the ball-jointed doll's pieces, at about the triangle count FacetError and BallError leave (the doll measured
    /// 1332 at the probe; PuppetChecks prints the count per piece), one connected piece for every body and every joint ball.
    /// </summary>
    private void CheckPuppetMesh(string when)
    {
        if (_main.Bodies.PuppetShape is not { } shape) { Check(false, $"{when}: the puppet has no mesh"); return; }
        Check(shape.Triangles is >= MinTriangles and <= MaxTriangles, $"{when}: the puppet mesh has {shape.Triangles} triangles, not {MinTriangles} to {MaxTriangles}");
        int pieces = PuppetChecks.Pieces(shape);
        Check(pieces == shape.PieceCount, $"{when}: the puppet mesh is {pieces} pieces, not one for each of its {shape.Ids.Length} bodies and {shape.Joints.Length} joints");
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
        var s = Session;
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
        while (Session.TickIndex < tick && !Session.Finished) await Frame();
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
