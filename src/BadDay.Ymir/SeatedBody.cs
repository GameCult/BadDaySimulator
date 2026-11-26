using System;
using System.Collections.Generic;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>What the Actor reported on one tick of a body run; only ticks that reported something are queued.</summary>
public readonly record struct BodyEvent(long Tick, TickEvents Events);

/// <summary>
/// The one owner of a BDS body world: the <see cref="Box3DSoaWorld"/> sized by <see cref="SeatedPuppet.Needs"/>, the
/// ragdoll <see cref="SeatedPuppet.Build"/> puts in it, the sense-point, strength and target lanes, and the queue of
/// the Actor's events. Every body run (the pain box, the office) holds one and decides nothing here: per tick the run
/// calls <see cref="ReadSenses"/>, lets its scenario write <see cref="Strengths"/> and <see cref="Targets"/> from the
/// points, then <see cref="DriveAndStep"/>. No other code in BDS creates or steps a world. After <see cref="Dispose"/>
/// every member throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class SeatedBody : IDisposable
{
    private readonly Box3DSoaWorld _world;
    private readonly Ragdoll _ragdoll;
    private readonly float[] _x, _y, _z;
    private readonly float[] _strengths;
    private readonly Quat[] _targets;
    private readonly Queue<BodyEvent> _events = new();
    private bool _disposed;

    public SeatedBody()
    {
        var needs = SeatedPuppet.Needs;
        _world = new Box3DSoaWorld(capacity: needs.Bodies, jointCapacity: needs.Joints);
        try
        {
            _ragdoll = SeatedPuppet.Build(_world);
            var plan = _ragdoll.Plan;
            _x = new float[plan.SensePoints.Count];
            _y = new float[plan.SensePoints.Count];
            _z = new float[plan.SensePoints.Count];
            _strengths = new float[plan.Joints.Count];
            _targets = new Quat[plan.Joints.Count];
        }
        catch
        {
            _world.Dispose();
            throw;
        }
    }

    public BodyPlan Plan => _ragdoll.Plan;

    /// <summary>The lane of a part's body in <see cref="View"/>. The ragdoll itself is not handed out: it is driven only through <see cref="DriveAndStep"/>.</summary>
    public int BodyLane(int part)
    {
        Live();
        return _ragdoll.BodyLane(part);
    }

    /// <summary>The live lanes of the world, for rendering. Read-only: nothing outside this body writes them.</summary>
    public Box3DSoaView View { get { Live(); return _world.View; } }

    /// <summary>The sense points as of the latest <see cref="ReadSenses"/>.</summary>
    public ReadOnlySpan<float> X { get { Live(); return _x; } }
    public ReadOnlySpan<float> Y { get { Live(); return _y; } }
    public ReadOnlySpan<float> Z { get { Live(); return _z; } }

    /// <summary>The muscle strengths and targets the scenario writes and <see cref="DriveAndStep"/> applies; they persist between ticks.</summary>
    public Span<float> Strengths { get { Live(); return _strengths; } }
    public Span<Quat> Targets { get { Live(); return _targets; } }

    /// <summary>Reads the body's sense points from the world.</summary>
    public void ReadSenses()
    {
        Live();
        _ragdoll.ReadSensePoints(_x, _y, _z);
    }

    /// <summary>Drives the ragdoll with the current strengths and targets, then steps the world one <paramref name="dt"/> seconds.</summary>
    public void DriveAndStep(double dt)
    {
        Live();
        _ragdoll.Drive(_strengths, _targets);
        _world.Step((float)dt, SeatedPuppet.Substeps);
    }

    public void Enqueue(long tick, TickEvents events)
    {
        Live();
        _events.Enqueue(new BodyEvent(tick, events));
    }

    /// <summary>
    /// Moves the queued events (oldest first) into <paramref name="buffer"/> and returns how many it wrote; the rest stay
    /// queued for the next call. Allocation-free once the queue has grown to the client's drain interval.
    /// </summary>
    public int DrainEvents(Span<BodyEvent> buffer)
    {
        Live();
        int n = 0;
        while (n < buffer.Length && _events.Count > 0) buffer[n++] = _events.Dequeue();
        return n;
    }

    /// <summary>Frees the world; a second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.Dispose();
    }

    private void Live() => ObjectDisposedException.ThrowIf(_disposed, this);
}
