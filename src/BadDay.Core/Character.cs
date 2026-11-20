using Lodur;

namespace BadDay.Core;

/// <summary>
/// Who has the bad day: the character's own nervous parameters, apart from the <see cref="Setting"/> (where) and the
/// <see cref="Cause"/> (why). Today that is the sense nerve he hears and sees with; his goals are <see cref="OfficeBrain"/>'s.
/// A character is code for now and becomes content with the Brains (follow-up npc-brains-as-content).
/// </summary>
public sealed record Character(string Id, NerveParameters Senses)
{
    /// <summary>
    /// Office Fella: threshold 0.05, no sensitisation, no conduction delay. The office's break near 90 s at pull 0.6
    /// holds only with these; Lodur's NerveParameters defaults break at about 100 s.
    /// </summary>
    public static Character OfficeFella { get; } = new("office-fella", new NerveParameters
    {
        Threshold = 0.05,
        SensitisationGrowth = 0.0,
        ConductionDelaySeconds = 0.0,
    });
}
