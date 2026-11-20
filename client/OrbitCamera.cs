using System;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>
/// A camera that navigates as Blender's viewport does: middle-drag orbits (turntable, 0.4 degrees per pixel, about the puppet), Shift+middle-drag
/// pans, Ctrl+middle-drag and the wheel zoom. With <see cref="Emulate3ButtonMouse"/> (on by default) Alt+left-drag stands for the middle button
/// in each combination. Plain left, right and every other combination pass through unhandled. The camera's own transform is the state: the orientation is
/// its basis, the view centre lies <see cref="Distance"/> ahead of it, and every gesture writes the transform once, on input.
/// </summary>
public partial class OrbitCamera : Camera3D
{
    private enum Gesture { Orbit, Pan, Zoom }

    private readonly record struct Binding(MouseButton Button, bool Alt, bool Shift, bool Ctrl, Gesture Gesture, bool Emulated);

    /// <summary>The gesture the drag in progress runs, fixed at the press: the button that holds it, where an orbit turns and which way up the view started.</summary>
    private readonly record struct Drag(Gesture Gesture, MouseButton Button, Vector3 Pivot, float Reverse);

    private static readonly Binding[] Bindings =
    {
        new(MouseButton.Middle, false, false, false, Gesture.Orbit, false),
        new(MouseButton.Middle, false, true, false, Gesture.Pan, false),
        new(MouseButton.Middle, false, false, true, Gesture.Zoom, false),
        new(MouseButton.Left, true, false, false, Gesture.Orbit, true),
        new(MouseButton.Left, true, true, false, Gesture.Pan, true),
        new(MouseButton.Left, true, false, true, Gesture.Zoom, true),
    };

    /// <summary>Blender's default turntable sensitivity.</summary>
    private const float DegreesPerPixel = 0.4f;
    /// <summary>One wheel notch, or 16 pixels of zoom drag, scales the distance by this.</summary>
    private const float ZoomStep = 1.2f;
    private const float ZoomDragPixels = 16f;
    private const float MinDistance = 0.6f;
    private const float MaxDistance = 12f;

    private float _distance = 2.4f;
    private Drag? _drag;

    /// <summary>Alt+left-drag stands for the middle button (Blender's Emulate 3 Button Mouse).</summary>
    public bool Emulate3ButtonMouse { get; set; } = true;

    /// <summary>Where an orbit turns, read once at each orbit press; null, or a null result, means the view centre.</summary>
    public Func<Vector3?>? Pivot { get; set; }

    /// <summary>How far the view centre lies ahead of the camera, in metres. Setting it zooms about the view centre, within the clamp.</summary>
    public float Distance
    {
        get => _distance;
        set => Commit(Basis, ViewCentre, Math.Clamp(value, MinDistance, MaxDistance));
    }

    /// <summary>The point the camera looks at and the view pans and zooms about.</summary>
    public Vector3 ViewCentre => Position - Basis.Z * _distance;

    public override void _Ready()
    {
        Fov = 55f;
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off; // moved on input, not on the physics tick
        Current = true;
        // The startup view: yaw 0.35, pitch 0.2, 2.4 m from the hold point.
        var centre = new Vector3(BodyPainBox.HoldPoint.X, BodyPainBox.HoldPoint.Y, BodyPainBox.HoldPoint.Z);
        const float yaw = 0.35f, pitch = 0.2f;
        var offset = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        Commit(Basis.LookingAt(-offset, Vector3.Up), centre, _distance);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                if (wheel.AltPressed || wheel.ShiftPressed || wheel.CtrlPressed) return;
                Distance = wheel.ButtonIndex == MouseButton.WheelUp ? _distance / MathF.Pow(ZoomStep, wheel.Factor) : _distance * MathF.Pow(ZoomStep, wheel.Factor);
                Handle();
                break;
            case InputEventMouseButton { Pressed: true } press:
                foreach (var row in Bindings)
                {
                    if (row.Button != press.ButtonIndex || row.Alt != press.AltPressed || row.Shift != press.ShiftPressed || row.Ctrl != press.CtrlPressed) continue;
                    if (row.Emulated && !Emulate3ButtonMouse) continue;
                    var pivot = row.Gesture == Gesture.Orbit ? Pivot?.Invoke() ?? ViewCentre : ViewCentre;
                    _drag = new Drag(row.Gesture, row.Button, pivot, Basis.Y.Dot(Vector3.Up) < 0 ? -1f : 1f);
                    Handle();
                    break;
                }
                break;
            case InputEventMouseButton { Pressed: false } release when _drag is { } drag && drag.Button == release.ButtonIndex:
                _drag = null;
                Handle();
                break;
            case InputEventMouseMotion motion when _drag is { } drag:
                var held = drag.Button == MouseButton.Left ? MouseButtonMask.Left : MouseButtonMask.Middle;
                if ((motion.ButtonMask & held) == 0) { _drag = null; return; } // the release was lost, to a focus change say
                Apply(drag, motion.Relative);
                Handle();
                break;
        }
    }

    private void Handle() => GetViewport().SetInputAsHandled();

    private void Apply(Drag drag, Vector2 move)
    {
        switch (drag.Gesture)
        {
            case Gesture.Orbit:
                // Turntable: yaw about the world's up, pitch about the view's right, both about the pivot, unclamped.
                float step = Mathf.DegToRad(DegreesPerPixel);
                var turn = new Basis(Vector3.Up, -move.X * step * drag.Reverse) * new Basis(Basis.X, -move.Y * step);
                Commit(turn * Basis, drag.Pivot + turn * (ViewCentre - drag.Pivot), _distance);
                break;
            case Gesture.Pan:
                // The view-centre plane follows the cursor.
                float perPixel = 2f * _distance * MathF.Tan(Mathf.DegToRad(Fov) / 2f) / GetViewport().GetVisibleRect().Size.Y;
                Commit(Basis, ViewCentre + (-move.X * Basis.X + move.Y * Basis.Y) * perPixel, _distance);
                break;
            case Gesture.Zoom:
                Distance = _distance * MathF.Pow(ZoomStep, move.Y / ZoomDragPixels);
                break;
        }
    }

    /// <summary>The one writer of the camera's transform: <paramref name="basis"/> looking at <paramref name="centre"/> from <paramref name="distance"/> away.</summary>
    private void Commit(Basis basis, Vector3 centre, float distance)
    {
        _distance = distance;
        basis = basis.Orthonormalized();
        Transform = new Transform3D(basis, centre + basis.Z * distance);
    }
}
