using System;
using System.Collections.Generic;
using CultMath;

namespace BadDay.Client.Tests;

/// <summary>
/// Slits at a ball joint (ruling doll-pieces-hug-balls: intersection is acceptable, a gap is not). Where a round ball meets a limb's flat cut edge, the ball's rim and the
/// limb's lip leave a wedge so narrow that the background shows through it as a hairline slit. A slit is measured in silhouette: the ball and its two neighbouring pieces are drawn
/// orthographically from <see cref="Views"/> directions at <see cref="PixelsPerMetre"/>, and the slit is the background a disc of <see cref="Disc"/> radius closes (morphological
/// closing minus the silhouette), within <see cref="Reach"/> of the ball's rim. The reading of a joint is the mean of that area over the views, in mm2.
/// </summary>
public static class Slits
{
    /// <summary>The views: Fibonacci directions over a hemisphere (an orthographic silhouette from -d is the mirror of d's).</summary>
    public const int Views = 72;

    /// <summary>The resolution: 2 px per mm resolves the 2 mm seam in four pixels.</summary>
    public const float PixelsPerMetre = 2000f;

    /// <summary>The disc (m) a slit is closed by: a background wedge narrower than twice this closes. Background that a 3 mm disc cannot enter is what the hairline is.</summary>
    public const float Disc = 0.003f;

    /// <summary>How far (m) beyond the ball's radius a slit is counted: the wedge sits at the rim, and a V between two limbs further out is the pose, not a slit.</summary>
    public const float Reach = 0.004f;

    private static readonly float3[] Dirs = MakeDirs();

    private static float3[] MakeDirs()
    {
        var d = new float3[Views];
        for (int i = 0; i < Views; i++) { float z = 1 - (i + 0.5f) / Views, r = MathF.Sqrt(1 - z * z), ph = i * 2.399963f; d[i] = new float3(r * MathF.Cos(ph), r * MathF.Sin(ph), z); }
        return d;
    }

    /// <summary>
    /// The mean over the views, and the largest single view, of the slit area (mm2) at a ball at <paramref name="origin"/> with radius <paramref name="radius"/>, of the triangles <paramref name="tris"/> (three points each: the ball's and its two neighbours').
    /// A roll (<paramref name="roll"/>, the half-segment from the origin to one end, zero for a sphere) is read along its whole length: the region is what lies within <see cref="Reach"/> of the roll's rim, the ends and the neighbours' ends included.
    /// </summary>
    public static (float Mean, float Max) Per(IReadOnlyList<(float3 A, float3 B, float3 C)> tris, float3 origin, float radius, float3 roll = default)
    {
        float roi = radius + Reach;
        int half = (int)MathF.Ceiling((roi + math.length(roll) + 2 * Disc) * PixelsPerMetre), n = 2 * half + 1, rad = (int)MathF.Round(Disc * PixelsPerMetre);
        var runs = new int[rad + 1];
        for (int dy = 0; dy <= rad; dy++) runs[dy] = (int)MathF.Floor(MathF.Sqrt(rad * rad - dy * dy) + 1e-3f);
        float total = 0, worst = 0;
        var mask = new bool[n * n]; var dil = new bool[n * n]; var closed = new bool[n * n];
        foreach (var d in Dirs)
        {
            var r = math.normalize(math.cross(d, MathF.Abs(d.y) < 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0))); var u = math.cross(d, r);
            Array.Clear(mask);
            foreach (var (a, b, c) in tris)
                Fill(mask, n, half, Project(a - origin, r, u), Project(b - origin, r, u), Project(c - origin, r, u));
            Dilate(mask, dil, n, rad, runs, true);
            Dilate(dil, closed, n, rad, runs, false);
            int count = 0;
            float roiPx = roi * PixelsPerMetre; var seg = Project(roll, r, u); float sl = math.lengthsq(seg);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - half, dyy = y - half;
                    if (sl > 0) { float k = math.clamp((dx * seg.x + dyy * seg.y) / sl, -1f, 1f); dx -= k * seg.x; dyy -= k * seg.y; }
                    if (closed[y * n + x] && !mask[y * n + x] && dx * dx + dyy * dyy <= roiPx * roiPx) count++;
                }
            float mm2 = count / (PixelsPerMetre / 1000f * PixelsPerMetre / 1000f);
            total += mm2; worst = MathF.Max(worst, mm2);
        }
        return (total / Views, worst);
    }

    private static float2 Project(float3 p, float3 r, float3 u) => new(math.dot(p, r) * PixelsPerMetre, math.dot(p, u) * PixelsPerMetre);

    /// <summary>The pixels (centres) inside the triangle, either winding.</summary>
    private static void Fill(bool[] mask, int n, int half, float2 a, float2 b, float2 c)
    {
        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (MathF.Abs(area) < 1e-9f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.x, MathF.Min(b.x, c.x)) + half)), x1 = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(a.x, MathF.Max(b.x, c.x)) + half));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.y, MathF.Min(b.y, c.y)) + half)), y1 = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(a.y, MathF.Max(b.y, c.y)) + half));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float qx = x - half, qy = y - half;
                float w0 = ((b.x - qx) * (c.y - qy) - (b.y - qy) * (c.x - qx)) / area, w1 = ((c.x - qx) * (a.y - qy) - (c.y - qy) * (a.x - qx)) / area, w2 = 1 - w0 - w1;
                if (w0 >= 0 && w1 >= 0 && w2 >= 0) mask[y * n + x] = true;
            }
    }

    /// <summary>The Minkowski sum (<paramref name="any"/>) or difference (all) of <paramref name="src"/> with the disc of <paramref name="rad"/> pixels, rows by prefix sums. Outside the image counts as empty.</summary>
    private static void Dilate(bool[] src, bool[] dst, int n, int rad, int[] runs, bool any)
    {
        var prefix = new int[(n + 1) * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) prefix[y * (n + 1) + x + 1] = prefix[y * (n + 1) + x] + (src[y * n + x] ? 1 : 0);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool hit = !any;
                for (int dy = -rad; dy <= rad && (any ? !hit : hit); dy++)
                {
                    int yy = y + dy; int w = runs[Math.Abs(dy)];
                    int lo = x - w, hi = x + w;
                    int count = yy < 0 || yy >= n ? 0 : prefix[yy * (n + 1) + Math.Min(n - 1, hi) + 1] - prefix[yy * (n + 1) + Math.Max(0, lo)];
                    if (any) { if (count > 0) hit = true; }
                    else if (yy < 0 || yy >= n || lo < 0 || hi >= n || count != hi - lo + 1) hit = false;
                }
                dst[y * n + x] = hit;
            }
    }
}
