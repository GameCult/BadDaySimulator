using System;
using System.Collections.Generic;
using BadDay.Core;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>
/// The client's root. Godot's physics frame decides when a tick happens (120 Hz, physics interpolation on, Godot's own
/// physics engine Dummy); <see cref="BodyPainBoxSession"/> decides everything a tick does. This node only steps it,
/// draws it and forwards the player's commands to it.
/// </summary>
public partial class Main : Node3D
{
    private const int LogLines = 6;

    private readonly List<string> _log = new();
    private BodyRenderer _bodies = null!;
    private Chamber _chamber = null!;
    private OrbitCamera _camera = null!;
    private Hud _hud = null!;
    private BodyPainBoxSession? _session;
    private double _intensity = new PainBoxParameters().BoxIntensity;
    private bool _deltaReported;

    public BodyPainBoxSession Session => _session ?? throw new InvalidOperationException("Main has no session before _Ready.");
    public BodyRenderer Bodies => _bodies;
    public ulong Seed { get; private set; } = 1;

    /// <summary>The intensity the box is set to; the next Reset starts with it.</summary>
    public double Intensity => _intensity;

    public bool SlowMotion => Engine.TimeScale < 1.0;
    public IReadOnlyList<string> Log => _log;

    /// <summary>The parameters of a run at the given box intensity: every default except the 1/120 s tick the body needs.</summary>
    public static PainBoxParameters ParametersFor(double intensity) =>
        new() { TickSeconds = BodyPainBox.TickSeconds, BoxIntensity = intensity };

    public override void _Ready()
    {
        AddChild(new WorldEnvironment { Environment = Look.Environment() });
        AddChild(Look.Sun());
        _bodies = new BodyRenderer { Name = "Bodies" };
        AddChild(_bodies);
        _chamber = new Chamber { Name = "Chamber" };
        AddChild(_chamber);
        _camera = new OrbitCamera { Name = "Camera" };
        AddChild(_camera);
        _hud = new Hud(this) { Name = "Hud" };
        AddChild(_hud);
        Reset(Seed);
    }

    public override void _ExitTree()
    {
        Engine.TimeScale = 1.0;
        _session?.Dispose();
        _session = null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_session is null) return;
        if (Math.Abs(delta - BodyPainBox.TickSeconds) > 1e-9 && !_deltaReported)
        {
            _deltaReported = true;
            GD.PushError($"BadDay: physics delta {delta} is not 1/120; the session steps 1/120 s regardless.");
        }
        _session.Step(); // never Godot's delta: the ragdoll's gains are tuned at 1/120
        Present(teleport: false);
    }

    /// <summary>Starts a new run from <paramref name="seed"/> at the current intensity, replacing the session.</summary>
    public void Reset(ulong seed)
    {
        _session?.Dispose();
        _session = null;
        _session = new BodyPainBoxSession(ParametersFor(_intensity), seed);
        Seed = seed;
        _log.Clear();
        Present(teleport: true);
    }

    /// <summary>Sets the box intensity now and for the next Reset.</summary>
    public void SetIntensity(double level)
    {
        _session?.SetIntensity(level);
        _intensity = level;
    }

    public void SwitchOff() => _session?.SwitchOff();

    public void SetSlowMotion(bool on) => Engine.TimeScale = on ? 0.25 : 1.0;

    private void Present(bool teleport)
    {
        var s = Session;
        _bodies.Sync(s.View, teleport);
        _chamber.Sync(s);
        Span<BodyEvent> events = stackalloc BodyEvent[8];
        int n;
        while ((n = s.DrainEvents(events)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                _log.Add($"{events[i].Tick * BodyPainBox.TickSeconds,6:F2} s  {events[i].Events}");
                if (_log.Count > LogLines) _log.RemoveAt(0);
            }
        }
    }
}
