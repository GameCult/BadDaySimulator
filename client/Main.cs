using System;
using System.Collections.Generic;
using BadDay.Core;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>
/// The client's root. Godot's physics frame decides when a tick happens (physics interpolation on, Godot's own
/// physics engine Dummy); <see cref="BodyPainBoxSession"/> decides everything a tick does. This node only steps it,
/// draws it and forwards the player's commands to it. A session that cannot start (the native library is missing, say)
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
    private BodyPainBoxSession? _session;
    private double _intensity = new PainBoxParameters().BoxIntensity;

    public BodyPainBoxSession Session => _session ?? throw new InvalidOperationException($"Main has no session ({Failure ?? "before _Ready"}).");
    public BodyRenderer Bodies => _bodies;
    public Chamber Chamber => _chamber;
    public Hud Hud => _hud;
    public ulong Seed { get; private set; } = 1;

    /// <summary>Why the session could not start, or null. Cleared by the next successful Reset.</summary>
    public string? Failure { get; private set; }

    /// <summary>The intensity the box is set to; the next Reset starts with it.</summary>
    public double Intensity => _intensity;

    public bool SlowMotion { get; private set; }

    /// <summary>Every event the session has reported since the last Reset, oldest first.</summary>
    public IReadOnlyList<BodyEvent> Events => _events;

    /// <summary>The parameters of a run at the given box intensity: every default except the 1/120 s tick the body needs.</summary>
    public static PainBoxParameters ParametersFor(double intensity) =>
        new() { TickSeconds = BodyPainBox.TickSeconds, BoxIntensity = intensity };

    private static int StepsPerSecond(double speed) => (int)Math.Round(speed / BodyPainBox.TickSeconds);

    public override void _Ready()
    {
        _bodies = new BodyRenderer { Name = "Bodies" };
        AddChild(_bodies);
        _chamber = new Chamber { Name = "Chamber" };
        AddChild(_chamber);
        _camera = new OrbitCamera { Name = "Camera", Pivot = () => _bodies.PuppetCentre };
        AddChild(_camera);
        AddChild(Look.Studio(_camera));
        _hud = new Hud(this) { Name = "Hud" };
        AddChild(_hud);
        Reset(Seed);
    }

    public override void _ExitTree()
    {
        Engine.PhysicsTicksPerSecond = StepsPerSecond(1.0);
        _session?.Dispose();
        _session = null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_session is null) return;
        _session.Step(); // one step of 1/120 s per physics frame; never Godot's delta, whatever the frame rate
        Present(teleport: false);
    }

    /// <summary>Starts a new run from <paramref name="seed"/> at the current intensity, replacing the session.</summary>
    public void Reset(ulong seed)
    {
        _session?.Dispose();
        _session = null;
        Failure = null;
        _events.Clear();
        Seed = seed;
        try
        {
            _session = new BodyPainBoxSession(ParametersFor(_intensity), seed);
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
        _session?.SetIntensity(level);
        _intensity = level;
    }

    public void SwitchOff() => _session?.SwitchOff();

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
        var s = Session;
        _bodies.Sync(s.View, teleport);
        _chamber.Sync(s);
        Span<BodyEvent> events = stackalloc BodyEvent[8];
        int n;
        while ((n = s.DrainEvents(events)) > 0)
        {
            for (int i = 0; i < n; i++) _events.Add(events[i]);
        }
    }
}
