using System;
using System.Collections.Generic;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>A part of the body met by a ray: its plan ordinal, the world point and the outward surface normal there.</summary>
public readonly record struct PartHit(int Part, BodyPoint Point, BodyPoint Normal);

/// <summary>What the Actor reported on one tick of a body run; only ticks that reported something are queued.</summary>
public readonly record struct BodyEvent(long Tick, TickEvents Events);

/// <summary>
/// The one owner of a BDS body world: the <see cref="Box3DSoaWorld"/> sized by <see cref="SeatedPuppet.Needs"/>, the
/// ragdoll <see cref="SeatedPuppet.Build"/> puts in it, the body's blood (<see cref="Blood"/>, the one writer of fluid particles and wet patches in the world), the sense-point, strength and target lanes, and the queue of
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
    private readonly Blood _blood;
    private readonly float[] _airLive, _airPooled, _airDrop;
    private readonly Queue<BodyEvent> _events = new();
    private bool _disposed;

    public SeatedBody(BloodParameters? blood = null)
    {
        blood ??= new BloodParameters();
        var needs = SeatedPuppet.Needs;
        _world = new Box3DSoaWorld(capacity: needs.Bodies, jointCapacity: needs.Joints,
            particles: new Box3DSoaParticleCapacities(Particles: blood.LiveCap, 0, 0, 0, 0, Materials: blood.Material + 1,
                Fluid: blood.LiveCap, Wets: blood.WetLanes, Sensors: needs.Sensors));
        try
        {
            _world.SetMaterial(blood.Material, blood.Friction, viscosity: blood.Viscosity, wetFriction: blood.WetFriction);
            _ragdoll = SeatedPuppet.Build(_world);
            var plan = _ragdoll.Plan;
            _blood = new Blood(_world, _ragdoll, blood);
            _airLive = new float[plan.AirwayOpenings.Count];
            _airPooled = new float[plan.AirwayOpenings.Count];
            _airDrop = new float[plan.AirwayOpenings.Count];
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
    public void DriveAndStep(double dt, ReadOnlySpan<double> bleedPerPart = default)
    {
        Live();
        _blood.BeforeStep(bleedPerPart, dt);
        _ragdoll.Drive(_strengths, _targets);
        _world.Step((float)dt, SeatedPuppet.Substeps);
        _blood.AfterStep();
    }

    /// <summary>What the world holds at the airway openings now: Lodur's sample of the liquid sensors and the body's pose, down being the world's gravity.</summary>
    public AirwaySample ReadAirway()
    {
        Live();
        _ragdoll.ReadAirway(_airLive, _airPooled, _airDrop, _world.Gravity);
        return AirwaySample.Of(_airLive, _airPooled, _airDrop);
    }

    /// <summary>The part of the body a ray from <paramref name="origin"/> along <paramref name="translation"/> meets first, with the point and surface normal; null for a miss or when the first thing met is not a part of the body.</summary>
    public PartHit? Pick(BodyPoint origin, BodyPoint translation)
    {
        Live();
        var hit = _world.CastRayClosest((origin.X, origin.Y, origin.Z), (translation.X, translation.Y, translation.Z));
        if (!hit.Hit || hit.StableId < SeatedPuppet.FirstStableId) return null;
        ulong ordinal = hit.StableId - SeatedPuppet.FirstStableId;
        if (ordinal >= (ulong)Plan.Parts.Count || _ragdoll.BodyLane((int)ordinal) != hit.Lane) return null;
        return new PartHit((int)ordinal, new BodyPoint(hit.Point.X, hit.Point.Y, hit.Point.Z), new BodyPoint(hit.Normal.X, hit.Normal.Y, hit.Normal.Z));
    }

    /// <summary>Opens a wound at a world point and normal on <paramref name="part"/>: from now on it sheds its share of the part's bleed, carried by the part.</summary>
    public void OpenWound(int part, BodyPoint point, BodyPoint normal)
    {
        Live();
        int lane = _ragdoll.BodyLane(part);
        var v = _world.View;
        var inverse = new Quat(v.RotationX[lane], v.RotationY[lane], v.RotationZ[lane], v.RotationW[lane]).Conjugate();
        inverse.Rotate(point.X - v.PositionX[lane], point.Y - v.PositionY[lane], point.Z - v.PositionZ[lane], out var px, out var py, out var pz);
        inverse.Rotate(normal.X, normal.Y, normal.Z, out var nx, out var ny, out var nz);
        _blood.Open(part, px, py, pz, nx, ny, nz);
    }

    /// <summary>Visible blood volume (m^3) shed so far and still gathered at the wounds, and the drops not yet settled.</summary>
    public double BloodEmitted { get { Live(); return _blood.Emitted; } }
    public double BloodBacklog { get { Live(); return _blood.Backlog; } }
    public int LiveDrops { get { Live(); return _blood.Live; } }

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
