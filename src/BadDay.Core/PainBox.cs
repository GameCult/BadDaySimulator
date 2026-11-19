using System;
using Lodur;

namespace BadDay.Core;

/// <summary>
/// The game's box: its schedule. A noxious intensity ramped in with seeded jitter, switched off at the disable
/// time. It writes the intensity of a Lodur <see cref="BoxStimulus"/> and nothing else; what the intensity does
/// to a character is Lodur's.
/// </summary>
public sealed class PainBox
{
    private readonly PainBoxParameters _p;
    private readonly SplitMix64 _rng;

    public PainBox(PainBoxParameters parameters, SplitMix64 rng)
    {
        _p = parameters;
        _rng = rng;
        Level = parameters.BoxIntensity;
        DisableTick = long.MaxValue;
        if (parameters.BoxDisableAtSeconds is { } d) SwitchOff((long)Math.Round(d / parameters.TickSeconds));
    }

    /// <summary>The first tick the box is off; <see cref="long.MaxValue"/> when it is never disabled.</summary>
    public long DisableTick { get; private set; }

    /// <summary>The intensity the ramp scales: the parameters' <c>BoxIntensity</c> until a caller sets it.</summary>
    public double Level { get; set; }

    /// <summary>Switches the box off from <paramref name="tick"/>; an earlier switch-off stands.</summary>
    public void SwitchOff(long tick) => DisableTick = Math.Min(DisableTick, tick);

    /// <summary>Writes this tick's intensity. Always draws the jitter, so the stream does not depend on state.</summary>
    public void Write(long tick, ref BoxStimulus box)
    {
        double jitter = _rng.NextSigned();
        double ramp = Math.Min(1.0, tick * _p.TickSeconds / _p.BoxRampSeconds);
        box.Intensity = tick < DisableTick
            ? Math.Max(0.0, Level * ramp * (1.0 + _p.BoxNoise * jitter))
            : 0.0;
    }
}
