using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BadDay.Core;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client.Tests;

/// <summary>
/// The look of the throat cut, for Deru to tune the clutch and thrash poses and <c>ThrashKeySeconds</c> on: the office at seed 1, the knife at
/// the front of the throat at tick 120 through <see cref="Main.CutAt"/>, and a PNG of the window at nine moments (the cut, the clutch, the
/// will's first break, the thrash, the sag, the limp body), with <c>frames.tsv</c> naming each one's tick, goal, will and consciousness.
/// It writes no scene state but the cut. Windowed, 1280x720, the HUD visible; the frames are of the window this scene opened.
/// <c>godot --path client res://tests/DrownCapture.tscn -- --frames=&lt;dir&gt;</c> (the directory is required); exit code 0 passes, 1 fails.
/// It passes only when the goals went Clutch then Thrash and the thrash came 0.2 s or less after a will break.
/// </summary>
public partial class DrownCapture : Node
{
    private const int Width = 1280, Height = 720, CutTick = 120, Hz = 120;
    private const double WatchdogSeconds = 300;
    private static readonly Vector3 Throat = new(0f, -0.0778f, 0.0778f); // the front of the throat, in the head's frame (the tests' HitAtPoint)

    private readonly List<string> _failures = new();
    private readonly StringBuilder _tsv = new("name\ttick\tgoal\twill\tconsciousness\n");
    private Main _main = null!;
    private string? _frames;
    private bool _done;
    private int _taken;

    private Hud Hud => _main.Hud;
    private OfficeBodySession Office => _main.Office;

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
            Fail($"no result after {WatchdogSeconds} s");
            Finish();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_frames)) Fail("--frames=<dir> is required");
            else
            {
                Directory.CreateDirectory(_frames);
                DisplayServer.WindowSetSize(new Vector2I(Width, Height));
                _main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
                AddChild(_main);
                if (_main.Failure is { } failure) Fail(failure);
                else await RunCapture();
            }
        }
        catch (Exception e)
        {
            Fail(e.ToString());
        }
        Finish();
    }

    private async Task RunCapture()
    {
        await Frames(3);
        var window = GetWindow().Size;
        if (window != new Vector2I(Width, Height)) { Fail($"the window is {window}, not {Width}x{Height}"); return; }
        Hud.SeedBox.Value = 1;
        Hud.ScenarioPicker.Select((int)Scenario.Office);
        Hud.ScenarioPicker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)Scenario.Office); // the HUD's own control, as the smoke picks it
        Engine.PhysicsTicksPerSecond = 1200; // the session still steps 1/120 s a physics frame

        while (Office.TickIndex < CutTick) await Physics();
        if (!await Cut()) return;
        long cutTick = Office.TickIndex;
        if (_main.Cuts.Count != 1 || _main.Cuts[0].Part != "Head") { Fail($"the cut landed on [{string.Join(", ", _main.Cuts)}], not the head"); return; }

        // Follow the run, noting the goal changes and the will's breaks as they come; each moment is the first tick its condition holds.
        long clutchTick = -1, thrashTick = -1, firstBreak = -1, thrashBreak = -1, bleedTick = -1, deathTick = -1;
        var moments = new List<(string Name, Func<bool> When)>
        {
            ("1-cut-0.5s", () => Office.TickIndex >= cutTick + Hz / 2),
            ("2-clutch-3s", () => clutchTick >= 0 && Office.TickIndex >= clutchTick + 3 * Hz),
            ("3-willbroke", () => firstBreak >= 0),
            ("4-thrash-0.5s", () => thrashTick >= 0 && Office.TickIndex >= thrashTick + Hz / 2),
            ("5-thrash-1.0s", () => thrashTick >= 0 && Office.TickIndex >= thrashTick + Hz),
            ("6-thrash-1.5s", () => thrashTick >= 0 && Office.TickIndex >= thrashTick + 3 * Hz / 2),
            ("7-consciousness-0.3", () => Office.Consciousness <= 0.3),
            ("8-consciousness-0.1", () => Office.Consciousness <= 0.1),
            ("9-death-2s", () => deathTick >= 0 && Office.TickIndex >= deathTick + 2 * Hz),
        };
        while (_taken < moments.Count && !_done)
        {
            await Physics();
            long t = Office.TickIndex;
            if (bleedTick < 0 && Office.AirwayBleed > 0.0) bleedTick = t;
            if (bleedTick < 0 && t > cutTick + Hz) { Fail("no AirwayBleed within 1 s of the cut"); return; }
            var goal = Office.Goal;
            if (goal == OfficeGoal.Clutch && clutchTick < 0) clutchTick = t;
            if (goal == OfficeGoal.Thrash && thrashTick < 0)
            {
                thrashTick = t;
                thrashBreak = LastBreakAtOrBefore(t);
            }
            if (firstBreak < 0) firstBreak = LastBreakAtOrBefore(t);
            if (Office.Dead && deathTick < 0) deathTick = t;
            if (moments[_taken].When()) await Capture(moments[_taken].Name);
            if (deathTick >= 0 && t > deathTick + 4 * Hz) break;
        }
        if (_taken < moments.Count) Fail($"the run reached tick {Office.TickIndex} with {moments.Count - _taken} moments not taken (dead {Office.Dead})");
        if (clutchTick < 0 || thrashTick < 0 || clutchTick >= thrashTick) Fail($"the goals did not go Clutch then Thrash (clutch tick {clutchTick}, thrash tick {thrashTick})");
        else if (thrashBreak < 0 || thrashTick - thrashBreak > Hz / 5) Fail($"the thrash at tick {thrashTick} did not follow a will break (at {thrashBreak}) within 0.2 s");
        File.WriteAllText(Path.Combine(_frames!, "frames.tsv"), _tsv.ToString());
        GD.Print($"CAPTURE cut {cutTick}, clutch {clutchTick}, will broke {firstBreak}, thrash {thrashTick}, died {deathTick}");
    }

    /// <summary>The tick of the latest WillBroke row at or before <paramref name="tick"/>, or -1.</summary>
    private long LastBreakAtOrBefore(long tick)
    {
        long found = -1;
        foreach (var e in Office.Result().Timeline)
            if (e.Kind == StressEntryKind.WillBroke && (long)Math.Round(e.TimeMs * Hz / 1000.0) <= tick) found = (long)Math.Round(e.TimeMs * Hz / 1000.0);
        return found;
    }

    /// <summary>The knife at the throat's drawn point. If the default view's ray misses, the camera is turned through its input path (a middle drag) and the cut tried again.</summary>
    private async Task<bool> Cut()
    {
        var camera = _main.GetNode<OrbitCamera>("Camera");
        int part = SeatedPuppet.Plan.PartIndex("Head", Lodur.Side.Center);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var pose = _main.Bodies.PoseOf(SeatedPuppet.FirstStableId + (ulong)part)!.Value;
            var at = camera.UnprojectPosition(pose * Throat);
            if (_main.CutAt(at)) return true;
            var centre = GetViewport().GetVisibleRect().Size / 2f;
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true, Position = centre, GlobalPosition = centre, ButtonMask = MouseButtonMask.Middle }, true);
            GetViewport().PushInput(new InputEventMouseMotion { Position = centre + new Vector2(40f, 0f), GlobalPosition = centre + new Vector2(40f, 0f), Relative = new Vector2(40f, 0f), ButtonMask = MouseButtonMask.Middle }, true);
            GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false, Position = centre, GlobalPosition = centre }, true);
            await Frames(2);
        }
        Fail("the knife was refused at the throat from every view tried");
        return false;
    }

    /// <summary>Holds the run, lets the scene catch up, saves the window, and lets the run go on.</summary>
    private async Task Capture(string name)
    {
        _main.SetPhysicsProcess(false);
        await Frames(3);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        image.Convert(Image.Format.Rgba8);
        image.SavePng(Path.Combine(_frames!, name + ".png"));
        var o = Office;
        _tsv.Append(string.Create(CultureInfo.InvariantCulture, $"{name}\t{o.TickIndex}\t{o.Goal}\t{o.Will:F3}\t{o.Consciousness:F3}\n"));
        _taken++;
        _main.SetPhysicsProcess(true);
    }

    private async Task Physics()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (_main.Failure is { } failure) throw new InvalidOperationException(failure);
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Fail(string failure) => _failures.Add(failure);

    private void Finish()
    {
        if (_done) return;
        _done = true;
        foreach (var f in _failures) GD.PrintErr("CAPTURE FAIL " + f);
        GD.Print(_failures.Count == 0 ? "CAPTURE PASS" : $"CAPTURE FAILED ({_failures.Count})");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
