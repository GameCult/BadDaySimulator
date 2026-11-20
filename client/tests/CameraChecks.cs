using System;
using System.Collections.Generic;
using System.Linq;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client.Tests;

/// <summary>Records the unhandled input that reaches it: what the camera let through.</summary>
public partial class UnhandledListener : Node
{
    public readonly List<InputEvent> Seen = new();
    public override void _UnhandledInput(InputEvent e) => Seen.Add(e);
}

/// <summary>
/// The orbit camera's navigation, run by the smoke on the real Main: events pushed through the viewport reach the camera as the player's mouse would, and a listener
/// behind it sees what the camera did not consume. Synchronous; the camera's transform and distance are restored at the end. Each check reports what broke through <c>fail</c>.
/// </summary>
public static class CameraChecks
{
    private const float Degrees = 0.4f;

    public static void Run(Action<string> fail, Main main)
    {
        void Check(bool ok, string what) { if (!ok) fail(what); }
        var camera = main.GetNode<OrbitCamera>("Camera");
        var viewport = main.GetViewport();
        // Events go in local (canvas) coordinates: the headless window is 64 px wide under a stretch to 1280, which would scale a window-space drag by 20.
        var cursor = viewport.GetVisibleRect().Size * new Vector2(0.1f, 0.9f); // clear of the HUD's controls, which sit at the top right
        var listener = new UnhandledListener();
        main.AddChild(listener);
        main.MoveChild(listener, 0); // the first child gets unhandled input last
        var home = camera.Transform;
        float homeDistance = camera.Distance;

        // The startup view: the hold point from yaw 0.35, pitch 0.2, 2.4 m, FOV 55, as the camera was placed before it navigated like Blender's.
        var centre = new Vector3(BodyPainBox.HoldPoint.X, BodyPainBox.HoldPoint.Y, BodyPainBox.HoldPoint.Z);
        var offset = new Vector3(MathF.Sin(0.35f) * MathF.Cos(0.2f), MathF.Sin(0.2f), MathF.Cos(0.35f) * MathF.Cos(0.2f)) * 2.4f;
        var startup = new Transform3D(Basis.Identity, centre + offset).LookingAt(centre, Vector3.Up);
        Check(Near(camera.Transform, startup, 1e-5f) && camera.Fov == 55f && camera.Distance == 2.4f, $"the startup view is {camera.Transform} at {camera.Distance} m, FOV {camera.Fov}, not {startup} at 2.4 m, FOV 55");

        void Button(MouseButton button, bool pressed, bool alt = false, bool shift = false, bool ctrl = false, double factor = 1) =>
            viewport.PushInput(new InputEventMouseButton
            {
                ButtonIndex = button, Pressed = pressed, Position = cursor, GlobalPosition = cursor, AltPressed = alt, ShiftPressed = shift, CtrlPressed = ctrl, Factor = (float)factor,
                ButtonMask = pressed && button is MouseButton.Left or MouseButton.Middle or MouseButton.Right ? Mask(button) : 0,
            }, true);
        void Motion(Vector2 move, MouseButtonMask held, bool alt = false, bool shift = false, bool ctrl = false) =>
            viewport.PushInput(new InputEventMouseMotion { Position = cursor + move, GlobalPosition = cursor + move, Relative = move, ButtonMask = held, AltPressed = alt, ShiftPressed = shift, CtrlPressed = ctrl }, true);
        void Drag(MouseButton button, Vector2 move, bool alt = false, bool shift = false, bool ctrl = false)
        {
            Button(button, true, alt, shift, ctrl);
            Motion(move, Mask(button), alt, shift, ctrl);
            Button(button, false, alt, shift, ctrl);
        }

        // Pan first, so the puppet's centre is off the view centre and an orbit about the wrong point shows.
        Drag(MouseButton.Middle, new Vector2(60, -30), shift: true);
        if (main.Bodies.PuppetCentre is not { } puppet) { fail("the puppet has no centre to orbit"); main.RemoveChild(listener); listener.Free(); return; }
        var afterPan = camera.Transform;
        float afterPanDistance = camera.Distance;
        void Back() { camera.Distance = afterPanDistance; camera.Transform = afterPan; listener.Seen.Clear(); }
        Check(camera.UnprojectPosition(puppet).DistanceTo(camera.UnprojectPosition(camera.ViewCentre)) > 5, "the pan left the puppet at the view centre; the orbit check would not tell the pivots apart");
        listener.Seen.Clear();

        // Orbit: the turntable turns by 0.4 degrees a pixel about the world's up, and the puppet stays where it is on the screen.
        var shown = camera.UnprojectPosition(puppet);
        Drag(MouseButton.Middle, new Vector2(50, 0));
        var orbited = camera.Transform;
        float turned = Floor(-afterPan.Basis.Z).SignedAngleTo(Floor(-orbited.Basis.Z), Vector3.Up);
        Check(MathF.Abs(turned - Mathf.DegToRad(-50 * Degrees)) < Mathf.DegToRad(0.1f), $"a 50 px drag turned the camera {Mathf.RadToDeg(turned):F2} degrees about up, not {-50 * Degrees}");
        Check(camera.UnprojectPosition(puppet).DistanceTo(shown) < 0.5f, $"the orbit moved the puppet from {shown} to {camera.UnprojectPosition(puppet)} on the screen; it turned about something else");
        Check(listener.Seen.Count == 0, "the orbit's events were not consumed");
        Check(MathF.Abs(camera.Distance - afterPanDistance) < 1e-6f, "an orbit changed the distance");
        Back();
        Drag(MouseButton.Left, new Vector2(50, 0), alt: true);
        Check(Near(camera.Transform, orbited, 1e-5f), $"Alt+left-drag gave {camera.Transform}, not the middle-drag's {orbited}");
        Back();

        // Over the top: a drag down of 400 px in 40 motions pitches 160 degrees, past the pole, and the view turns upside down rather than stopping.
        Button(MouseButton.Middle, true);
        for (int i = 0; i < 40; i++) Motion(new Vector2(0, 10), MouseButtonMask.Middle); // many small steps: a clamp near the pole would stop here
        Button(MouseButton.Middle, false);
        Check(camera.Basis.Y.Dot(Vector3.Up) < 0, "an orbit of 160 degrees over the top did not carry the view over");
        // Upside down, a drag to the right still turns the scene with the cursor: the yaw reverses (Blender's turntable does).
        var flipped = camera.Transform;
        Drag(MouseButton.Middle, new Vector2(50, 0));
        float flippedTurn = Floor(-flipped.Basis.Z).SignedAngleTo(Floor(-camera.Basis.Z), Vector3.Up);
        Check(MathF.Abs(flippedTurn - Mathf.DegToRad(50 * Degrees)) < Mathf.DegToRad(0.1f), $"a 50 px drag with the view upside down turned the camera {Mathf.RadToDeg(flippedTurn):F2} degrees about up, not {50 * Degrees}");
        Back();

        // Emulation off: Alt+left-drag is no gesture, and the press reaches whoever is behind.
        camera.Emulate3ButtonMouse = false;
        Drag(MouseButton.Left, new Vector2(50, 20), alt: true);
        camera.Emulate3ButtonMouse = true;
        Check(Near(camera.Transform, afterPan, 1e-6f), "Alt+left-drag moved the camera with the emulation off");
        Check(listener.Seen.OfType<InputEventMouseButton>().Any(b => b.Pressed && b.ButtonIndex == MouseButton.Left), "Alt+left press did not reach the listener with the emulation off");
        listener.Seen.Clear();

        // Plain left and right drags are free: nothing moves and everything reaches the listener.
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right })
        {
            Drag(button, new Vector2(50, 20));
            Check(Near(camera.Transform, afterPan, 1e-6f), $"a plain {button} drag moved the camera");
            Check(listener.Seen.OfType<InputEventMouseButton>().Count(b => b.ButtonIndex == button) == 2, $"the plain {button} press and release did not both reach the listener");
            listener.Seen.Clear();
        }

        // Pan: the basis holds and the view centre follows the cursor, 1 px per px.
        var centreShown = camera.UnprojectPosition(camera.ViewCentre);
        var centreWas = camera.ViewCentre;
        Drag(MouseButton.Middle, new Vector2(40, -25), shift: true);
        Check(SameBasis(camera.Basis, afterPan.Basis, 1e-6f), "a pan changed the view's orientation");
        Check(camera.UnprojectPosition(centreWas).DistanceTo(centreShown + new Vector2(40, -25)) < 0.5f, $"a pan of (40, -25) moved the view centre on the screen from {centreShown} to {camera.UnprojectPosition(centreWas)}");
        var panned = camera.Transform;
        Back();
        Drag(MouseButton.Left, new Vector2(40, -25), alt: true, shift: true);
        Check(Near(camera.Transform, panned, 1e-5f), "Alt+Shift+left-drag did not pan as Shift+middle-drag does");
        Back();

        // Zoom: a notch is a factor 1.2, a drag of 16 px up is a notch in, the clamp holds and the view direction and centre stay.
        var zoomCentre = camera.ViewCentre;
        Button(MouseButton.WheelUp, true);
        Check(MathF.Abs(camera.Distance - afterPanDistance / 1.2f) < 1e-5f, $"a notch in made the distance {camera.Distance}, not {afterPanDistance / 1.2f}");
        Button(MouseButton.WheelDown, true);
        Check(MathF.Abs(camera.Distance - afterPanDistance) < 1e-5f, $"a notch in and a notch out left the distance {camera.Distance}, not {afterPanDistance}");
        Button(MouseButton.WheelUp, true, factor: 2);
        Check(MathF.Abs(camera.Distance - afterPanDistance / 1.44f) < 1e-5f, $"a notch in of factor 2 made the distance {camera.Distance}, not {afterPanDistance / 1.44f}");
        camera.Distance = afterPanDistance;
        Button(MouseButton.WheelUp, true, ctrl: true);
        Check(MathF.Abs(camera.Distance - afterPanDistance) < 1e-6f && listener.Seen.Count == 1, "a wheel notch with Ctrl held was consumed or zoomed");
        listener.Seen.Clear();
        Drag(MouseButton.Middle, new Vector2(0, -32), ctrl: true);
        Check(MathF.Abs(camera.Distance - afterPanDistance / 1.44f) < 1e-5f, $"Ctrl+middle-drag up by 32 px made the distance {camera.Distance}, not {afterPanDistance / 1.44f}");
        Drag(MouseButton.Left, new Vector2(0, 32), alt: true, ctrl: true);
        Check(MathF.Abs(camera.Distance - afterPanDistance) < 1e-4f, $"Ctrl+Alt+left-drag down by 32 px made the distance {camera.Distance}, not back to {afterPanDistance}");
        for (int i = 0; i < 100; i++) Button(MouseButton.WheelUp, true);
        Check(MathF.Abs(camera.Distance - 0.6f) < 1e-5f, $"100 notches in left the distance {camera.Distance}, not 0.6");
        for (int i = 0; i < 100; i++) Button(MouseButton.WheelDown, true);
        Check(MathF.Abs(camera.Distance - 12f) < 1e-4f, $"100 notches out left the distance {camera.Distance}, not 12");
        Check(SameBasis(camera.Basis, afterPan.Basis, 1e-6f) && camera.ViewCentre.DistanceTo(zoomCentre) < 1e-4f, "zooming changed the view direction or moved the view centre");
        Back();

        // The gesture is fixed at the press: Alt released mid-drag still orbits, and a release the viewport never delivered ends the drag.
        Button(MouseButton.Left, true, alt: true);
        Motion(new Vector2(10, 0), MouseButtonMask.Left);
        var midDrag = camera.Transform;
        Check(!Near(midDrag, afterPan, 1e-4f), "a drag that began with Alt stopped orbiting when Alt was released");
        Motion(new Vector2(10, 0), 0);
        Motion(new Vector2(10, 0), MouseButtonMask.Left);
        Check(Near(camera.Transform, midDrag, 1e-6f), "a motion with no button held, or one after it, still moved the camera; the drag was not ended");
        Button(MouseButton.Left, false);

        main.RemoveChild(listener);
        listener.Free();
        camera.Distance = homeDistance;
        camera.Transform = home;
    }

    private static MouseButtonMask Mask(MouseButton button) => button switch
    {
        MouseButton.Left => MouseButtonMask.Left,
        MouseButton.Right => MouseButtonMask.Right,
        _ => MouseButtonMask.Middle,
    };

    private static Vector3 Floor(Vector3 v) => new Vector3(v.X, 0, v.Z).Normalized();

    private static bool Near(Transform3D a, Transform3D b, float tolerance) => SameBasis(a.Basis, b.Basis, tolerance) && a.Origin.DistanceTo(b.Origin) < tolerance;

    private static bool SameBasis(Basis a, Basis b, float tolerance) => (a.X - b.X).Length() < tolerance && (a.Y - b.Y).Length() < tolerance && (a.Z - b.Z).Length() < tolerance;
}
