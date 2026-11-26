using System.Collections.Generic;

namespace BadDay.Core;

/// <summary>
/// What a wound is made of: one Cut affliction of <see cref="Severity"/> on each of the named tissue <see cref="Layers"/>
/// of the part (a part without a layer skips it). Content, not a rule: <see cref="Knife"/> is the click-cut's [P] until
/// lodur-insult-resolution turns contacts into afflictions (question bds-first-wound-tool).
/// </summary>
public sealed record WoundTool(IReadOnlyList<string> Layers, double Severity)
{
    /// <summary>Skin, fat and muscle cut to the same depth.</summary>
    public static WoundTool Knife { get; } = new(new[] { "Skin", "Fat", "Muscle" }, 60.0);
}
