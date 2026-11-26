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

    /// <summary>One drop's volume in m^3.</summary>
    public double DropVolume => Math.Pow(2.0 * DropRadius, 3.0);
}
