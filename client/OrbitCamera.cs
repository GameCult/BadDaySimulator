using System;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>A camera orbiting the chamber: right-drag orbits, the wheel zooms.</summary>
public partial class OrbitCamera : Camera3D
{
    private Vector3 _target = new(BodyPainBox.HoldPoint.X, BodyPainBox.HoldPoint.Y, BodyPainBox.HoldPoint.Z);
    private float _yaw = 0.35f;
    private float _pitch = 0.2f;
    private float _distance = 2.4f;

    public override void _Ready()
    {
        Fov = 55f;
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off; // moved in _Process, not on the physics tick
        Current = true;
        Place();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion m when (m.ButtonMask & MouseButtonMask.Right) != 0:
                _yaw -= m.Relative.X * 0.006f;
                _pitch = Math.Clamp(_pitch + m.Relative.Y * 0.006f, -0.2f, 1.4f);
                Place();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _distance = Math.Max(0.6f, _distance * 0.9f);
                Place();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _distance = Math.Min(12f, _distance * 1.1f);
                Place();
                break;
        }
    }

    private void Place()
    {
        var offset = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch)) * _distance;
        Position = _target + offset;
        LookAt(_target, Vector3.Up);
    }
}
