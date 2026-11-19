using Lodur;

namespace BadDay.Core;

/// <summary>
/// The headless pain box: one virtual human with a hand fixed inside the box, run to the deliberate
/// withdrawal. The nervous model (perception, reflex, drive, behaviour) is Lodur's <see cref="Actor"/>; this
/// owns the box, the seed, the trait draw and the recorder. No body, no physics and no balance.
/// </summary>
public static class PainBoxSimulation
{
    public static PainBoxResult Run(PainBoxParameters p, ulong seed)
    {
        var pose = new[] { Quat.Identity };
        var session = new PainBoxSession(p, seed, new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f), 1, pose, pose);
        var point = new float[1];       // the hand sits at the box centre
        var strengths = new float[1];   // the behaviour's output has no body to move
        var targets = new Quat[1];
        while (!session.Finished)
        {
            if (session.Disabled()) break;
            session.Tick(point, point, point, strengths, targets);
            if (session.Withdrew) break;
        }
        return session.Result();
    }
}
