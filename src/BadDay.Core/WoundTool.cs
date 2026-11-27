using System;

namespace BadDay.Core;

/// <summary>
/// What a wound is made of: a <see cref="Depth"/> (m) and a <see cref="Severity"/>. Which tissue layers it cuts is not content:
/// Lodur's PartTissuesDef.Reach derives it from the depth and the hit point in the part's frame. Content, not a rule:
/// <see cref="Knife"/> is the click-cut's [P] until lodur-insult-resolution turns contacts into afflictions (question bds-first-wound-tool).
/// </summary>
public sealed record WoundTool
{
    /// <summary>A tool that cuts <paramref name="depth"/> m deep (finite and above 0) at <paramref name="severity"/>.</summary>
    public WoundTool(double depth, double severity)
    {
        if (!(double.IsFinite(depth) && depth > 0.0)) throw new ArgumentException("depth is not finite and above 0.", nameof(depth));
        Depth = depth;
        Severity = severity;
    }

    /// <summary>How deep the tool cuts, in m, through the part's skin outward-in (finite and above 0).</summary>
    public double Depth { get; }

    /// <summary>The severity of the Cut affliction on each reached layer.</summary>
    public double Severity { get; }

    /// <summary>
    /// 2 cm at severity 20 (ruling bds-bleed-by-vessels: an ordinary cut bleeds out in 2 to 4 minutes, a major vessel in about 30 s, the front of the throat drowns). At that depth: skin, fat and muscle on the limbs and trunk (bone lies past 2.26 cm there), skin and fat on the
    /// face and sides of the head, the larynx at the front of the throat, and bone as well as muscle in the hands and feet.
    /// </summary>
    public static WoundTool Knife { get; } = new(0.02, 20.0);
}
