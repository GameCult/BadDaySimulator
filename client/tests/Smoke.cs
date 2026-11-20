using System;
using System.Collections.Generic;
using BadDay.Core;
using BadDay.Client;
using BadDay.Ymir;
using Godot;
using Ymir.Box3D;

namespace BadDay.Client.Tests;

/// <summary>
/// The headless smoke: runs the real Main scene and checks what the client promises. Exit code 0 passes, 1 fails.
/// <c>godot --headless --path client res://tests/Smoke.tscn</c>
/// </summary>
public partial class Smoke : Node
{
    private const ulong Seed = 1;
    private const long ParityTick = 240;
    private const long BoxOffTick = 600;
    private const long Watchdog = 2000;

    private readonly List<string> _failures = new();
    private Main _main = null!;
    private HandSample _reference;
    private long _frames;
    private bool _parityChecked, _boxOffIssued;

    public override void _Ready()
    {
        try { Arrange(); }
        catch (Exception e) { Check(false, e.ToString()); }
    }

    private void Arrange()
    {
        Check(Engine.PhysicsTicksPerSecond == 120, $"physics ticks per second is {Engine.PhysicsTicksPerSecond}, not 120");
        Check((string)ProjectSettings.GetSetting("physics/3d/physics_engine") == "Dummy", "Godot's 3D physics engine is not Dummy");

        // The reference: the same run, headless, in this process.
        var reference = BodyPainBox.Run(Main.ParametersFor(new PainBoxParameters().BoxIntensity), Seed);
        _reference = reference.Hand[(int)ParityTick - 1];

        _main = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
        AddChild(_main);
        Check(!_main.GetWorld3D().Space.IsValid, "Godot has a physics space");
        Check(_main.Seed == Seed, "Main did not start from the smoke's seed");
        Check(Count(_main, n => n is CollisionObject3D || n.GetClass().StartsWith("Collision")) == 0, "Godot physics nodes are in the scene");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_main is null) { Finish(); return; }
        _frames++;
        var s = _main.Session;
        if (!_boxOffIssued && s.TickIndex != _frames - 1 && !_failures.Exists(f => f.StartsWith("ticks"))) // this node runs before Main: frame n sees n-1 steps
            Check(false, $"ticks: {s.TickIndex} steps after {_frames - 1} physics frames, not one per frame");
        if (!_parityChecked && s.TickIndex == ParityTick) CheckParityAndRender(s);
        if (_parityChecked && !_boxOffIssued && s.TickIndex == BoxOffTick)
        {
            Check(!s.Finished, $"the run was over before tick {BoxOffTick}; the box-off probe needs the will intact");
            _boxOffIssued = true;
            _main.SwitchOff(); // the HUD button's handler
            return;
        }
        if (_boxOffIssued)
        {
            Check(s.Finished && s.Result().Outcome == Outcome.BoxDisabled, $"box off did not end the run as BoxDisabled within one tick (finished {s.Finished})");
            Finish();
            return;
        }
        if (_frames > Watchdog) { Check(false, $"no result after {Watchdog} frames (tick {s.TickIndex})"); Finish(); }
    }

    private void CheckParityAndRender(BodyPainBoxSession s)
    {
        _parityChecked = true;
        var h = s.Hand;
        Check(Bits(h.X) == Bits(_reference.X) && Bits(h.Y) == Bits(_reference.Y) && Bits(h.Z) == Bits(_reference.Z),
            $"hand at tick {ParityTick} ({h.X}, {h.Y}, {h.Z}) differs from the headless run ({_reference.X}, {_reference.Y}, {_reference.Z})");

        var view = s.View;
        Check(_main.Bodies.NodeCount == view.Count && Count(_main.Bodies, n => n is MeshInstance3D) == view.Count,
            $"{_main.Bodies.NodeCount} nodes for {view.Count} bodies");
        for (int i = 0; i < view.Count; i++)
        {
            var node = _main.Bodies.NodeFor(view.StableId[i]);
            if (node is null) { Check(false, $"no node for stable id {view.StableId[i]}"); continue; }
            var want = new Vector3(view.PositionX[i], view.PositionY[i], view.PositionZ[i]);
            Check(node.Transform.Origin == want, $"body {view.StableId[i]} at {node.Transform.Origin}, lanes say {want}");
            var q = new Quaternion(view.RotationX[i], view.RotationY[i], view.RotationZ[i], view.RotationW[i]);
            Check(Math.Abs(node.Transform.Basis.GetRotationQuaternion().Dot(q)) > 0.99999, $"body {view.StableId[i]} is rotated differently from its lane");

            float r = view.Size0[i], a = view.Size1[i], b = view.Size2[i];
            var size = (Box3DSoaKind)view.Kind[i] switch
            {
                Box3DSoaKind.Sphere => new Vector3(2 * r, 2 * r, 2 * r),
                Box3DSoaKind.Capsule => new Vector3(2 * r, 2 * (a + r), 2 * r),
                _ => new Vector3(2 * r, 2 * a, 2 * b),
            };
            Check(node.Mesh.GetAabb().Size.IsEqualApprox(size), $"body {view.StableId[i]} mesh is {node.Mesh.GetAabb().Size}, the lanes say {size}");
        }
    }

    private void Finish()
    {
        foreach (var f in _failures) GD.PrintErr("SMOKE FAIL: " + f);
        GD.Print(_failures.Count == 0 ? "SMOKE PASS" : $"SMOKE FAILED ({_failures.Count})");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    private void Check(bool ok, string failure)
    {
        if (!ok) _failures.Add(failure);
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static int Count(Node root, Func<Node, bool> match)
    {
        int n = match(root) ? 1 : 0;
        foreach (var child in root.GetChildren()) n += Count(child, match);
        return n;
    }
}
