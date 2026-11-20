using System.Collections.Generic;
using Lodur;

namespace BadDay.Core;

/// <summary>One of the setting's point sources: an id the cause refers to, and a Lodur <see cref="Lodur.Emitter"/> (channel, position, radius).</summary>
public sealed record NamedEmitter(string Id, Emitter Emitter);

/// <summary>
/// Where a bad day happens: the named emitters and where they sit. It holds no schedule; what the emitters do over
/// time is the <see cref="Cause"/>'s. Code defaults for now, not persisted (follow-up office-scenario-as-content).
/// Every number is a placeholder.
/// </summary>
public sealed record Setting(IReadOnlyList<NamedEmitter> Emitters)
{
    /// <summary>
    /// The office: a printer and a desk phone (audio), a ceiling light (visual) and the desktop's notifications
    /// (interrupt). Each sits at the origin, where the head is held, with a radius that reaches it in full: the
    /// placement is a placeholder until the client puts real positions in.
    /// </summary>
    public static Setting Office { get; } = new(new[]
    {
        new NamedEmitter("printer", new Emitter(Channel.Audio, 0f, 0f, 0f, 10f)),
        new NamedEmitter("ceiling-light", new Emitter(Channel.Visual, 0f, 0f, 0f, 10f)),
        new NamedEmitter("desk-phone", new Emitter(Channel.Audio, 0f, 0f, 0f, 10f)),
        new NamedEmitter("desktop", new Emitter(Channel.Interrupt, 0f, 0f, 0f, 10f)),
    });

    /// <summary>The same emitters moved by (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>): a body that senses somewhere other than the origin wants the room around it.</summary>
    public Setting Shifted(float dx, float dy, float dz)
    {
        var moved = new NamedEmitter[Emitters.Count];
        for (int i = 0; i < moved.Length; i++)
        {
            var e = Emitters[i].Emitter;
            e.X += dx; e.Y += dy; e.Z += dz;
            moved[i] = Emitters[i] with { Emitter = e };
        }
        return new Setting(moved);
    }
}
