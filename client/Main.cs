using System;
using System.Collections.Generic;
using BadDay.Core;
using Lodur;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>The runs the client hosts: the Synaptic Induction Chamber's pain box, or the office with its knife.</summary>
public enum Scenario { Chamber, Office }

/// <summary>
/// The client's root. Godot's physics frame decides when a tick happens (physics interpolation on, Godot's own
/// physics engine Dummy); the live <see cref="IBodyRun"/> (the pain box or the office, one at a time) decides everything a tick does.
/// This node only steps whichever is live, draws it and forwards the player's commands to it. A session that cannot start (the native library is missing, say)
/// is reported once in <see cref="Failure"/> and the log, and nothing ticks until a Reset succeeds.
/// </summary>
public partial class Main : Node3D
{
    /// <summary>Slow motion runs this fraction of the session steps per real second.</summary>
    private const double SlowFactor = 0.25;

    private readonly List<BodyEvent> _events = new();
    private BodyRenderer _bodies = null!;
    private Chamber _chamber = null!;
    private OrbitCamera _camera = null!;
    private Hud _hud = null!;
    private IBodyRun? _run;
    private double _intensity = new PainBoxParameters().BoxIntensity;

    public Scenario Scenario { get; private set; } = Scenario.Chamber;

    /// <summary>The live run, whichever scenario.</summary>
    public IBodyRun Run => _run ?? throw new InvalidOperationException($"Main has no session ({Failure ?? "before _Ready"}).");

    /// <summary>The live run as the pain box; throws in the office.</summary>
    public BodyPainBoxSession PainBox => Run as BodyPainBoxSession ?? throw new InvalidOperationException("The live run is not the pain box.");

    /// <summary>The live run as the office; throws in the chamber.</summary>
    public OfficeBodySession Office => Run as OfficeBodySession ?? throw new InvalidOperationException("The live run is not the office.");
    public BodyRenderer Bodies => _bodies;
    public Chamber Chamber => _chamber;
    public Hud Hud => _hud;
    public ulong Seed { get; private set; } = 1;

    /// <summary>Why the session could not start, or null. Cleared by the next successful Reset.</summary>
    public string? Failure { get; private set; }

    /// <summary>The intensity the box is set to; the next Reset starts with it.</summary>
    public double Intensity => _intensity;

    /// <summary>The parameters of an office run: every default except the 1/120 s tick the body needs.</summary>
    public static StressParameters OfficeParameters { get; } = new() { TickSeconds = OfficeBody.TickSeconds };

    /// <summary>A cut the knife opened: the part's label and the tick.</summary>
    public readonly record struct Cut(string Part, long Tick);

    private readonly List<Cut> _cuts = new();

    /// <summary>Every cut opened since the last Reset, oldest first: a log for the HUD.</summary>
    public IReadOnlyList<Cut> Cuts => _cuts;

    public bool SlowMotion { get; private set; }

    /// <summary>Every event the session has reported since the last Reset, oldest first.</summary>
    public IReadOnlyList<BodyEvent> Events => _events;

    /// <summary>The parameters of a run at the given box intensity: every default except the 1/120 s tick the body needs.</summary>
    public static PainBoxParameters ParametersFor(double intensity) =>
        new() { TickSeconds = BodyPainBox.TickSeconds, BoxIntensity = intensity };

    private static int StepsPerSecond(double speed) => (int)Math.Round(speed / BodyPainBox.TickSeconds);

    public override void _Ready()
    {
        Look.Apply();
        _bodies = new BodyRenderer { Name = "Bodies" };
        AddChild(_bodies);
        _chamber = new Chamber { Name = "Chamber" };
        AddChild(_chamber);
        _camera = new OrbitCamera { Name = "Camera", Pivot = () => _bodies.PuppetCentre };
        AddChild(_camera);
        AddChild(Look.World());
        _hud = new Hud(this) { Name = "Hud" };
        AddChild(_hud);
        AddChild(Post());
        SetLines();
        GetWindow().SizeChanged += SetLines;
        Reset(Seed);
    }

    /// <summary>The line grid: the window's size over its block size, the one value ps1_res ever holds.</summary>
    public Vector2 Ps1Res { get; private set; }

    private void SetLines()
    {
        var size = GetWindow().Size;
        float block = Look.BlockSize(size.Y);
        Ps1Res = new Vector2(size.X / block, size.Y / block);
        Look.Set("ps1_res", Ps1Res);
    }

    /// <summary>The framebuffer pass: a full-screen rect under the HUD (its layer is above this one) that quantises what the 3D drew.</summary>
    private static CanvasLayer Post()
    {
        var layer = new CanvasLayer { Name = "Post", Layer = -1 };
        layer.AddChild(new ColorRect
        {
            Name = "Quantise",
            Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://ps1_post.gdshader") },
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1,
            AnchorBottom = 1,
        });
        return layer;
    }

    public override void _ExitTree()
    {
        if (IsInstanceValid(GetWindow())) GetWindow().SizeChanged -= SetLines;
        Engine.PhysicsTicksPerSecond = StepsPerSecond(1.0);
        Input.SetCustomMouseCursor(null);
        _run?.Dispose();
        _run = null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_run is null) return;
        _run.Step(); // one step of 1/120 s per physics frame; never Godot's delta, whatever the frame rate
        Present(teleport: false);
    }

    /// <summary>Switches to <paramref name="scenario"/> and starts it at the current seed.</summary>
    public void SetScenario(Scenario scenario)
    {
        Scenario = scenario;
        if (scenario == Scenario.Office) Input.SetCustomMouseCursor(ImageTexture.CreateFromImage(Look.KnifeCursor()), Input.CursorShape.Arrow, Vector2.Zero);
        else Input.SetCustomMouseCursor(null);
        Reset(Seed);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // The knife takes plain left presses; Alt+left is the camera's emulated middle button, and the camera never binds a plain left.
        if (Scenario == Scenario.Office && e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, AltPressed: false } press)
        {
            CutAt(press.Position);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// The knife at a screen point: the camera's ray through it (20 m), the office's pick, and a cut where it hit. True when a cut opened;
    /// then the part and the tick are appended to <see cref="Cuts"/>. A miss, a refusal and any other scenario do nothing.
    /// </summary>
    public bool CutAt(Vector2 screenPoint)
    {
        if (Scenario != Scenario.Office || _run is not OfficeBodySession office) return false;
        var origin = _camera.ProjectRayOrigin(screenPoint);
        var translation = _camera.ProjectRayNormal(screenPoint) * 20f;
        if (office.Pick(new BodyPoint(origin.X, origin.Y, origin.Z), new BodyPoint(translation.X, translation.Y, translation.Z)) is not { } hit) return false;
        if (!office.Wound(hit, WoundTool.Knife)) return false;
        var part = SeatedPuppet.Plan.Parts[hit.Part];
        _cuts.Add(new Cut(part.Side switch { Lodur.Side.Left => part.Label + " L", Lodur.Side.Right => part.Label + " R", _ => part.Label }, office.TickIndex));
        return true;
    }

    /// <summary>Starts a new run of the current scenario from <paramref name="seed"/> (the box at the current intensity), replacing the live run.</summary>
    public void Reset(ulong seed)
    {
        _run?.Dispose();
        _run = null;
        Failure = null;
        _events.Clear();
        _cuts.Clear();
        Seed = seed;
        try
        {
            _run = Scenario == Scenario.Office
                ? new OfficeBodySession(OfficeParameters, seed)
                : new BodyPainBoxSession(ParametersFor(_intensity), seed);
        }
        catch (Exception e) // the session's start can fail in native code (library missing, ABI mismatch): report once, tick nothing
        {
            Failure = $"{e.GetType().Name}: {e.Message}";
            GD.PushError($"BadDay: cannot start a run: {e}");
            return;
        }
        Present(teleport: true);
    }

    /// <summary>Sets the box intensity now and for the next Reset.</summary>
    public void SetIntensity(double level)
    {
        (_run as BodyPainBoxSession)?.SetIntensity(level);
        _intensity = level;
    }

    /// <summary>The player silences a stressor of the office.</summary>
    public void Silence(string stressorId) => Office.Silence(stressorId);

    public void SwitchOff() => (_run as BodyPainBoxSession)?.SwitchOff();

    /// <summary>
    /// Slow motion is fewer session steps per real second: Godot runs its physics frames at a quarter of the rate and each
    /// frame still steps the session 1/120 s, so the run is the same run, slower, and interpolation smooths the pose
    /// between ticks. (Engine.TimeScale would only scale the delta, which the session never uses.)
    /// </summary>
    public void SetSlowMotion(bool on)
    {
        SlowMotion = on;
        Engine.PhysicsTicksPerSecond = StepsPerSecond(on ? SlowFactor : 1.0);
    }

    private void Present(bool teleport)
    {
        var s = Run;
        _bodies.Sync(s.View, teleport);
        if (s is BodyPainBoxSession box) _chamber.Sync(box); else _chamber.Hide();
        Span<BodyEvent> events = stackalloc BodyEvent[8];
        int n;
        while ((n = s.DrainEvents(events)) > 0)
        {
            for (int i = 0; i < n; i++) _events.Add(events[i]);
        }
    }
}
