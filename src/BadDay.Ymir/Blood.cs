using System;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>
/// Content for the blood of a seated body: every number is a balance knob (ruling economy-numbers-are-sim-tuned-knobs).
/// <see cref="Gain"/> is how many times the real volume is drawn and spilled (ruling bds-blood-gain-simulated); the
/// drops are fluid particles of one size, <see cref="DropVolume"/> each.
/// </summary>
public sealed record BloodParameters
{
    /// <summary>Visible volume per real volume bled; the physiology's own blood volume is never scaled.</summary>
    public double Gain { get; init; } = 10.0;
    /// <summary>Drop radius in m; a drop's volume is (2 r)^3, as Ymir's Settle reads it (0.0125 m: 15.625 mL).</summary>
    public float DropRadius { get; init; } = 0.0125f;
    /// <summary>kg/m^3; a drop's mass is this times <see cref="DropVolume"/>.</summary>
    public double Density { get; init; } = 1060.0;
    /// <summary>m/s along the wound's normal, over the part's own velocity.</summary>
    public float ExitSpeed { get; init; } = 0.5f;
    /// <summary>m/s: a supported drop slower than this is calm.</summary>
    public float SettleSpeed { get; init; } = 0.05f;
    /// <summary>Consecutive calm ticks before a drop settles into a wet patch.</summary>
    public int SettleTicks { get; init; } = 30;
    /// <summary>Fluid particle lanes: the most drops that are live at once (ruling ymir-fluid-live-cap-512).</summary>
    public int LiveCap { get; init; } = 512;
    /// <summary>Drops emitted over all wounds in one tick; the rest of the volume waits as backlog.</summary>
    public int MaxDropsPerTick { get; init; } = 8;
    /// <summary>Wet patch lanes.</summary>
    public int WetLanes { get; init; } = 256;
    /// <summary>The blood material's table index and its friction, viscosity and wet friction.</summary>
    public int Material { get; init; } = 1;
    public float Friction { get; init; } = 0.3f;
    public float Viscosity { get; init; } = 0.01f;
    public float WetFriction { get; init; } = 0.1f;

    /// <summary>Sets the blood material in <paramref name="world"/>: the one place the table entry is written, used by the seated body.</summary>
    public void ApplyTo(Box3DSoaWorld world) => world.SetMaterial(Material, Friction, viscosity: Viscosity, wetFriction: WetFriction);

    /// <summary>One drop's volume in m^3.</summary>
    public double DropVolume => Math.Pow(2.0 * DropRadius, 3.0);
}

/// <summary>
/// The blood of a <see cref="SeatedBody"/>, and the one writer of fluid particles and wet patches in BDS. Lodur's
/// physiology alone decides how much bleeds; this decides how that volume is drawn: each wound point gathers
/// <see cref="BloodParameters.Gain"/> times its part's bleed (shared by the part's points) as visible volume and sheds
/// it as drops of <see cref="BloodParameters.DropVolume"/> from where the wound is, carried by the part's pose, at most
/// <see cref="BloodParameters.MaxDropsPerTick"/> a tick; volume that cannot be shed yet waits as backlog and none is
/// dropped. After every step a drop that has rested on something for <see cref="BloodParameters.SettleTicks"/> ticks
/// settles into a wet patch, and the oldest supported drops settle until the live drops are at most LiveCap - MaxDropsPerTick, so
/// they stay under <see cref="BloodParameters.LiveCap"/>. A lane Settle frees cannot be taken before the next Step, so a cap-bound run sheds fewer drops
/// than it could (measured 50% of 8 a tick at saturation, 19268 of 38400 drops over 4800 ticks at SettleTicks 30; the rest waits as backlog, none is lost); a lethal bleed (2.5 drops a tick) never reaches it. Ymir never settles by itself; this is the policy.
/// Allocation-free once the wound points are open.
/// </summary>
public sealed class Blood
{
    private struct WoundPoint
    {
        public int Part;
        public float Px, Py, Pz, Nx, Ny, Nz; // the part's frame
        public double Volume;                 // visible m^3 gathered and not yet shed
    }

    private const int SpiralSpots = 40;
    private readonly Box3DSoaWorld _world;
    private readonly Ragdoll _ragdoll;
    private readonly BloodParameters _p;
    private readonly double _dropVolume;
    private readonly float _mass;
    private readonly uint _phase;
    private readonly int[] _pointsOnPart;
    private WoundPoint[] _points = new WoundPoint[8];
    private int _pointCount, _cursor;
    // The live drops, oldest first.
    private readonly Box3DSoaParticleHandle[] _live;
    private readonly int[] _calm;
    private readonly bool[] _supported, _chosen;
    private int _liveCount;
    private readonly Box3DSoaParticleDesc[] _descs;
    private readonly Box3DSoaParticleHandle[] _created;
    private readonly Box3DSoaParticleHandle[] _settling;

    internal Blood(Box3DSoaWorld world, Ragdoll ragdoll, BloodParameters parameters)
    {
        _world = world;
        _ragdoll = ragdoll;
        _p = parameters;
        _dropVolume = parameters.DropVolume;
        _mass = (float)(parameters.Density * _dropVolume);
        _phase = Box3DSoaPhase.Of(1, fluid: true);
        _pointsOnPart = new int[ragdoll.Plan.Parts.Count];
        _live = new Box3DSoaParticleHandle[parameters.LiveCap];
        _calm = new int[parameters.LiveCap];
        _supported = new bool[parameters.LiveCap];
        _chosen = new bool[parameters.LiveCap];
        _descs = new Box3DSoaParticleDesc[parameters.MaxDropsPerTick];
        _created = new Box3DSoaParticleHandle[parameters.MaxDropsPerTick];
        _settling = new Box3DSoaParticleHandle[parameters.LiveCap];
    }

    /// <summary>Visible volume (m^3) shed as drops so far, settled or live.</summary>
    public double Emitted { get; private set; }

    /// <summary>Visible volume (m^3) gathered at the wounds and not yet shed.</summary>
    public double Backlog { get { double b = 0.0; for (int i = 0; i < _pointCount; i++) b += _points[i].Volume; return b; } }

    /// <summary>Drops in the air or resting, not yet settled.</summary>
    public int Live => _liveCount;

    /// <summary>Opens a wound point on <paramref name="part"/>, in the part's frame; from now on it gathers its share of the part's bleed.</summary>
    internal void Open(int part, float px, float py, float pz, float nx, float ny, float nz)
    {
        if ((uint)part >= (uint)_pointsOnPart.Length) throw new ArgumentOutOfRangeException(nameof(part));
        if (_pointCount == _points.Length) Array.Resize(ref _points, _points.Length * 2);
        _points[_pointCount++] = new WoundPoint { Part = part, Px = px, Py = py, Pz = pz, Nx = nx, Ny = ny, Nz = nz };
        _pointsOnPart[part]++;
    }

    /// <summary>Gathers this tick's volume from <paramref name="bleedPerPart"/> (real m^3/s per part, empty for none) and sheds what whole drops it makes, before the step.</summary>
    internal void BeforeStep(ReadOnlySpan<double> bleedPerPart, double dt)
    {
        if (_pointCount == 0) return;
        for (int i = 0; i < _pointCount; i++)
        {
            ref var w = ref _points[i];
            if ((uint)w.Part < (uint)bleedPerPart.Length) w.Volume += _p.Gain * bleedPerPart[w.Part] / _pointsOnPart[w.Part] * dt;
        }

        // A lane Settle freed is reusable only after the next Step, so the room is what the world says, not the cap alone.
        int budget = Math.Min(_p.MaxDropsPerTick, Math.Min(_world.FreeParticleSlots, _p.LiveCap - _liveCount));
        int n = 0;
        var v = _world.View;
        float r = _p.DropRadius;
        for (bool any = true; any && n < budget;)
        {
            any = false;
            for (int k = 0; k < _pointCount && n < budget; k++)
            {
                ref var w = ref _points[(_cursor + k) % _pointCount];
                if (w.Volume < _dropVolume) continue;

                int lane = _ragdoll.BodyLane(w.Part);
                var q = new Quat(v.RotationX[lane], v.RotationY[lane], v.RotationZ[lane], v.RotationW[lane]);
                q.Rotate(w.Px, w.Py, w.Pz, out var px, out var py, out var pz);
                q.Rotate(w.Nx, w.Ny, w.Nz, out var nx, out var ny, out var nz);
                // Outside the surface at the picked point; when a live drop or one shed this tick is in the way (coincident cuts, a wound that sheds faster
                // than a drop leaves), at a free spot on the cut around the point (a spiral over the tangent plane, so a cut spreads), then outward. Overlapped
                // fluid blows up. A point with no free spot at all waits; its volume stays gathered.
                float lift = r + 0.002f;
                float bx = v.PositionX[lane] + px + nx * lift, by = v.PositionY[lane] + py + ny * lift, bz = v.PositionZ[lane] + pz + nz * lift;
                (float X, float Y, float Z) at = (bx, by, bz);
                bool clear = Clear(at, n, v);
                if (!clear)
                {
                    // The tangent basis: a deterministic pair across the normal.
                    float ax = MathF.Abs(ny) < 0.9f ? 0f : 1f, ay = MathF.Abs(ny) < 0.9f ? 1f : 0f;
                    float tx = -nz * ay, ty = nz * ax, tz = nx * ay - ny * ax; // n x axis
                    float tl = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                    tx /= tl; ty /= tl; tz /= tl;
                    float ux = ny * tz - nz * ty, uy = nz * tx - nx * tz, uz = nx * ty - ny * tx; // n x t
                    for (int spot = 1; spot <= SpiralSpots && !clear; spot++)
                    {
                        float angle = spot * 2.39996323f; // the golden angle
                        float radius = 1.8f * r * MathF.Sqrt(spot);
                        float c = MathF.Cos(angle) * radius, d = MathF.Sin(angle) * radius;
                        at = (bx + tx * c + ux * d, by + ty * c + uy * d, bz + tz * c + uz * d);
                        clear = Clear(at, n, v);
                    }
                    for (int step = 1; step <= 12 && !clear; step++)
                    {
                        at = (bx + nx * step * r, by + ny * step * r, bz + nz * step * r);
                        clear = Clear(at, n, v);
                    }
                }
                if (!clear) continue;
                w.Volume -= _dropVolume;
                any = true;
                var velocity = (v.VelocityX[lane] + nx * _p.ExitSpeed, v.VelocityY[lane] + ny * _p.ExitSpeed, v.VelocityZ[lane] + nz * _p.ExitSpeed);
                _descs[n++] = new Box3DSoaParticleDesc(at, _mass, r, _phase, (uint)_p.Material, velocity: velocity);
            }
        }
        _cursor = (_cursor + 1) % _pointCount;
        if (n == 0) return;

        _world.CreateParticles(_descs.AsSpan(0, n), _created.AsSpan(0, n));
        for (int i = 0; i < n; i++)
        {
            _live[_liveCount] = _created[i];
            _calm[_liveCount] = 0;
            _liveCount++;
        }
        Emitted += n * _dropVolume;
    }

    /// <summary>True when a drop at <paramref name="at"/> touches none of the live drops nor the <paramref name="placed"/> drops of this tick.</summary>
    private bool Clear((float X, float Y, float Z) at, int placed, Box3DSoaView v)
    {
        float min = 2f * _p.DropRadius * 0.999f;
        float min2 = min * min;
        for (int i = 0; i < placed; i++)
        {
            float dx = _descs[i].X - at.X, dy = _descs[i].Y - at.Y, dz = _descs[i].Z - at.Z;
            if (dx * dx + dy * dy + dz * dz < min2) return false;
        }
        var px = v.ParticleX; var py = v.ParticleY; var pz = v.ParticleZ;
        for (int i = 0; i < _liveCount; i++)
        {
            int lane = _live[i].Lane;
            float dx = px[lane] - at.X, dy = py[lane] - at.Y, dz = pz[lane] - at.Z;
            if (dx * dx + dy * dy + dz * dz < min2) return false;
        }
        return true;
    }

    /// <summary>Settles the drops that have come to rest, then the oldest supported ones until at most LiveCap - MaxDropsPerTick are live, after the step.</summary>
    internal void AfterStep()
    {
        if (_liveCount == 0) return;
        var v = _world.View;
        var support = v.SupportBody;
        var onParticle = v.SupportParticle;
        var vx = v.ParticleVx; var vy = v.ParticleVy; var vz = v.ParticleVz;
        float slow2 = _p.SettleSpeed * _p.SettleSpeed;
        int settling = 0;
        for (int i = 0; i < _liveCount; i++)
        {
            int lane = _live[i].Lane;
            bool supported = support[lane] >= 0 || onParticle[lane] >= 0;
            bool still = vx[lane] * vx[lane] + vy[lane] * vy[lane] + vz[lane] * vz[lane] < slow2;
            _calm[i] = supported && still ? _calm[i] + 1 : 0;
            _supported[i] = supported;
            _chosen[i] = supported && _calm[i] >= _p.SettleTicks;
            if (_chosen[i]) settling++;
        }
        int room = _p.LiveCap - _p.MaxDropsPerTick;
        for (int i = 0; i < _liveCount && _liveCount - settling > room; i++)
        {
            if (_chosen[i] || !_supported[i]) continue;
            _chosen[i] = true;
            settling++;
        }
        if (settling == 0) return;

        int s = 0, kept = 0;
        for (int i = 0; i < _liveCount; i++) if (_chosen[i]) _settling[s++] = _live[i];
        _world.Settle(_settling.AsSpan(0, s));
        for (int i = 0; i < _liveCount; i++)
        {
            if (_chosen[i]) continue;
            _live[kept] = _live[i];
            _calm[kept] = _calm[i];
            kept++;
        }
        _liveCount = kept;
    }
}
