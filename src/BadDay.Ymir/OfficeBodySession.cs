using System;
using BadDay.Core;
using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace BadDay.Ymir;

/// <summary>
/// The office on the seated body, stepped by its caller like <see cref="BodyPainBoxSession"/>: a <see cref="SeatedBody"/>
/// (the one owner of the world), a Lodur <see cref="Physiology"/> on the humanoid plan with the session's slow scale,
/// and the <see cref="StressSession"/> the headless run drives, with the desk's emitters moved to where the head sits and
/// sensed there. Per <see cref="Step"/>: the body reports its points and what its airway openings hold, the session decides (Lodur's Actor, which steps
/// the physiology first), the ragdoll is driven, the world steps. Harm enters only through <see cref="Wound"/>; the
/// session never decides death, blood or consciousness: the read-outs are Lodur's. A dead body is limp (the Actor writes
/// no strength). The office has no end (ruling bds-office-is-a-sandbox-preset): <see cref="Finished"/> is never true, the will's break is an event, and the caller stops stepping.
/// </summary>
public sealed class OfficeBodySession : IBodyRun
{
    private readonly SeatedBody _body;
    private readonly StressSession _session;
    private readonly Physiology _physiology;
    private readonly double _tickSeconds;
    private readonly double[] _bleeds;
    private readonly int _handL, _handR, _pelvisLane;
    private bool _disposed;

    public OfficeBodySession(StressParameters p, ulong seed, BloodParameters? blood = null)
    {
        if (Math.Abs(p.TickSeconds - OfficeBody.TickSeconds) > 1e-12)
        {
            throw new ArgumentException($"The body run steps at 1/120 s; got TickSeconds {p.TickSeconds}.");
        }

        _tickSeconds = p.TickSeconds;
        blood ??= new BloodParameters();
        _body = new SeatedBody(blood);
        try
        {
            var plan = _body.Plan;
            _bleeds = new double[plan.Parts.Count];
            _body.ReadSenses();
            int head = plan.SenseIndex("Head");
            _handL = plan.SenseIndex("Hand", Side.Left);
            _handR = plan.SenseIndex("Hand", Side.Right);
            _pelvisLane = _body.BodyLane(plan.PartIndex("Pelvis"));
            var physiologyPlan = PhysiologyPlans.Humanoid();
            _physiology = new Physiology(plan, physiologyPlan, p.SlowScale, blood.Gain);
            // The desk's emitters sit where the head does and are sensed there.
            _session = new StressSession(p with { Setting = p.Setting.Shifted(_body.X[head], _body.Y[head], _body.Z[head]) }, seed,
                plan.SensePoints.Count, OfficeBody.Poses, head, _physiology, plan, physiologyPlan);
        }
        catch
        {
            _body.Dispose();
            throw;
        }
    }

    /// <summary>The live lanes of the world, for rendering. Read-only: nothing outside the body writes them.</summary>
    public Box3DSoaView View { get { Live(); return _body.View; } }

    /// <summary>Ticks run so far; the tick the next <see cref="Step"/> runs.</summary>
    public long TickIndex { get { Live(); return _session.TickIndex; } }

    /// <summary>Always false: the office is a sandbox preset that lasts until the player ends it. Here for <see cref="IBodyRun"/>, which the chamber ends.</summary>
    public bool Finished { get { Live(); return false; } }

    /// <summary>The Actor's state at the end of the latest tick, and the body's (Lodur's numbers, never BDS's).</summary>
    public double Will { get { Live(); return _session.Actor.Drive.Will; } }
    public double Arousal { get { Live(); return _session.Actor.Drive.Arousal; } }
    public double Resolve { get { Live(); return _session.Actor.Drive.Resolve; } }
    public double AirHunger { get { Live(); return _session.Actor.Drive.AirHunger; } }
    public double Consciousness { get { Live(); return _physiology.Consciousness; } }
    public double BloodLoss { get { Live(); return _physiology.BloodLoss; } }
    public bool Dead { get { Live(); return _physiology.Dead; } }
    /// <summary>The liquid in the airway (m^3), the fraction of it filled, and whether the last sample found liquid at an opening: Lodur's numbers.</summary>
    public double AirwayLiquid { get { Live(); return _physiology.AirwayLiquid; } }
    public double Occlusion { get { Live(); return _physiology.Occlusion; } }
    public bool LiquidAtAirway { get { Live(); return _physiology.LiquidAtAirway; } }
    /// <summary>The blood the body has shed as drops, the volume still waiting to be shed (visible m^3, the gain applied) and the drops not yet settled: the body's, display only.</summary>
    public double BloodEmitted { get { Live(); return _body.BloodEmitted; } }
    public double BloodBacklog { get { Live(); return _body.BloodBacklog; } }
    public int LiveDrops { get { Live(); return _body.LiveDrops; } }
    /// <summary>The blood bled into the airway (real m^3/s, entering the airway at the body's blood gain; the HUD reads it) and the live liquid drawn from it (world m^3) in the latest step, then what the airway owes the world (m^3, signed), the part of <see cref="BloodEmitted"/> that was cough and drain, and the cough and drain still waiting to be shed: the body's, display only.</summary>
    public double AirwayBleed { get { Live(); return _physiology.AirwayBleed; } }
    public double AirwayDrawnLive { get { Live(); return _physiology.DrawnLive; } }
    public double AirwayOwed { get { Live(); return _body.Owed; } }
    public double BloodExpelled { get { Live(); return _body.BloodExpelled; } }
    public double ExpelBacklog { get { Live(); return _body.ExpelBacklog; } }
    public DeathCause DeathCause { get { Live(); return _physiology.Cause; } }
    public OfficeGoal Goal { get { Live(); return OfficeBrain.GoalAt(_session.Actor.GoalIndex); } }

    /// <summary>The hands and the pelvis as of the latest step (read from the world each time).</summary>
    public BodyPoint HandL { get { Live(); return Sense(_handL); } }
    public BodyPoint HandR { get { Live(); return Sense(_handR); } }
    public BodyPoint Pelvis
    {
        get
        {
            Live();
            var v = _body.View;
            return new BodyPoint(v.PositionX[_pelvisLane], v.PositionY[_pelvisLane], v.PositionZ[_pelvisLane]);
        }
    }

    /// <summary>One step of 1/120 s: the body reports its points and what its airway openings hold, the session decides, the ragdoll is driven, the world steps.</summary>
    public void Step()
    {
        Live();
        long tick = _session.TickIndex;
        _body.ReadSenses();
        var airway = _body.ReadAirway();
        var events = _session.Tick(_body.X, _body.Y, _body.Z, _body.Strengths, _body.Targets, airway);
        if (events != TickEvents.None) _body.Enqueue(tick, events);
        _body.Exchange(_physiology.DrawnLive, _physiology.DrawnPooled, _physiology.Coughed, _physiology.Drained); // the dying step's flows are real; a dead physiology's read 0 (Lodur steps it)
        for (int part = 0; part < _bleeds.Length; part++) _bleeds[part] = _physiology.Bleed(part);
        _body.DriveAndStep(_tickSeconds, _bleeds);
    }

    /// <summary>Moves the queued events (oldest first) into <paramref name="buffer"/>; see <see cref="SeatedBody.DrainEvents"/>.</summary>
    public int DrainEvents(Span<BodyEvent> buffer)
    {
        Live();
        return _body.DrainEvents(buffer);
    }

    /// <summary>The player silences a stressor from the next tick; see <see cref="StressSession.Silence"/>.</summary>
    public void Silence(string stressorId)
    {
        Live();
        _session.Silence(stressorId);
    }

    /// <summary>The part of the body a ray meets first; see <see cref="SeatedBody.Pick"/>.</summary>
    public PartHit? Pick(BodyPoint origin, BodyPoint translation)
    {
        Live();
        return _body.Pick(origin, translation);
    }

    /// <summary>
    /// Wounds the part <paramref name="hit"/> names from the next tick, and opens the wound at the point it hit so the blood leaves from there;
    /// the point goes into the part's frame (<see cref="SeatedBody.ToPart"/>) for the layers the tool's depth reaches there.
    /// False when refused (a part outside the plan, or see <see cref="StressSession.Wound"/>), and then nothing is opened.
    /// </summary>
    public bool Wound(in PartHit hit, WoundTool tool)
    {
        Live();
        if ((uint)hit.Part >= (uint)_body.Plan.Parts.Count) return false;
        if (!_session.Wound(hit.Part, _body.ToPart(hit.Part, hit.Point), tool)) return false;
        _body.OpenWound(hit.Part, hit.Point, hit.Normal);
        return true;
    }

    /// <summary>The nervous result so far, with the death if there was one.</summary>
    public StressResult Result()
    {
        Live();
        return _session.Result();
    }

    /// <summary>Frees the world; a second call does nothing.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _body.Dispose();
    }

    private BodyPoint Sense(int i)
    {
        _body.ReadSenses();
        return new BodyPoint(_body.X[i], _body.Y[i], _body.Z[i]);
    }

    private void Live() => ObjectDisposedException.ThrowIf(_disposed, this);
}
