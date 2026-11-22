using System;
using System.Collections.Generic;
using System.Linq;
using CultMath;

namespace BadDay.Client;

/// <summary>A source plane <c>N . x = D</c> (N a unit normal, outward from the solid it bounds), in double: the plane a triangle's fragments lie on, however the boolean cut them.</summary>
public readonly record struct SourcePlane(double Nx, double Ny, double Nz, double D)
{
    /// <summary>The signed distance of <paramref name="p"/> from the plane (positive on the outer side).</summary>
    public double Distance(float3 p) => Nx * p.x + Ny * p.y + Nz * p.z - D;
}

/// <summary>
/// A closed triangle mesh whose every triangle carries a source tag: an index into <see cref="Planes"/>, the plane the triangle lies on. A fresh solid (<see cref="PuppetBoolean.Solid"/>) is its own source,
/// each triangle tagged by its own index; <see cref="PuppetBoolean.Subtract"/> appends the cutter's triangles (reversed: they face into the cutter, out of the solid) after the solid's planes and tags every
/// output triangle with the plane of the triangle it is a fragment of; <see cref="PuppetBoolean.Tidy"/> and <see cref="PuppetBoolean.LargestSolid"/> carry the tags. Triangles wind counter-clockwise from outside.
/// </summary>
public sealed record PuppetSolid(float3[] V, int[] Tris, int[] Tag, SourcePlane[] Planes)
{
    public int Triangles => Tris.Length / 3;
}

/// <summary>
/// Mesh booleans for the doll's sockets: A minus B for a closed mesh A and a convex polyhedron B (a joint ball's cutter). Plane-based (Bernstein and Fussell 2009; Nehring-Wirxel, Trettner and Kobbelt 2021):
/// B is its face planes, and every new vertex is one of two kinds, each computed once from canonical inputs, so both sides of a seam get the same point and no T-junction can arise: an A edge crossing a B plane
/// (from the edge's two original endpoints, lower index first) or a B edge (two planes) piercing an A triangle (its source plane and the two). A's triangles are clipped by B's planes (Sutherland-Hodgman, each
/// edge labelled with the triangle edge or the plane it lies on); what lies outside B is re-triangulated by ear clipping with holes bridged; B's faces inside A are rebuilt from the curve and B's own edges,
/// inside-ness of B's vertices decided by A's generalised winding number (Jacobson, Kavan and Sorkine-Hornung 2013). A plane's side of a point is decided once, zero counting as outside.
/// </summary>
public static class PuppetBoolean
{
    private readonly record struct D3(double X, double Y, double Z)
    {
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 operator *(D3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);
        public double Dot(D3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public D3 Cross(D3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Len => Math.Sqrt(Dot(this));
        public D3 Unit => this * (1 / Math.Max(Len, 1e-300));
        public static D3 Of(float3 v) => new(v.x, v.y, v.z);
        public float3 F => new((float)X, (float)Y, (float)Z);
    }

    /// <summary>What the boolean and the tidy did, and what went wrong: each fault is a broken invariant (an open result, an unpaired piercing, a loop that did not close), never silently repaired.</summary>
    public sealed class Stats
    {
        public int Collapsed, Flipped, CutTriangles, NewVertices;
        public double MinAngleDeg = 180;
        public readonly List<string> Faults = new();
        public void Fault(string s) => Faults.Add(s);
    }

    private static SourcePlane PlaneOf(D3 a, D3 b, D3 c)
    {
        var n = (b - a).Cross(c - a).Unit;
        return new SourcePlane(n.X, n.Y, n.Z, n.Dot(a));
    }

    /// <summary>A mesh as its own source: every triangle tagged by itself, its plane through its own corners.</summary>
    public static PuppetSolid Solid(float3[] v, int[] t)
    {
        var planes = new SourcePlane[t.Length / 3];
        for (int k = 0; k < planes.Length; k++) planes[k] = PlaneOf(D3.Of(v[t[3 * k]]), D3.Of(v[t[3 * k + 1]]), D3.Of(v[t[3 * k + 2]]));
        return new PuppetSolid(v, t, Enumerable.Range(0, planes.Length).ToArray(), planes);
    }

    /// <summary>
    /// A minus B. B is a closed convex polyhedron (counter-clockwise from outside). Every output triangle is tagged: a fragment of an A triangle keeps its tag; a triangle of B's wall (B's faces inside A, facing
    /// the other way) takes the tag of the B triangle that holds its centroid, B's triangles being appended to the planes as <c>Planes.Length + k</c> (reversed). A's planes are A's sources', never recomputed from fragments.
    /// </summary>
    public static PuppetSolid Subtract(PuppetSolid a, float3[] bv, int[] bt, Stats stats)
    {
        var A = a.V.Select(D3.Of).ToArray(); var at = a.Tris;
        // B's faces: coplanar neighbouring triangles merged (a roll's quads), one plane each, their boundary loop counter-clockwise from outside.
        int nb = bt.Length / 3;
        var tn = new D3[nb];
        for (int t = 0; t < nb; t++) tn[t] = (D3.Of(bv[bt[3 * t + 1]]) - D3.Of(bv[bt[3 * t]])).Cross(D3.Of(bv[bt[3 * t + 2]]) - D3.Of(bv[bt[3 * t]])).Unit;
        var parent = Enumerable.Range(0, nb).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var edgeTri = new Dictionary<(int, int), int>();
        for (int t = 0; t < nb; t++) for (int e = 0; e < 3; e++) edgeTri[(bt[3 * t + e], bt[3 * t + (e + 1) % 3])] = t;
        for (int t = 0; t < nb; t++) for (int e = 0; e < 3; e++)
            if (edgeTri.TryGetValue((bt[3 * t + (e + 1) % 3], bt[3 * t + e]), out int o) && tn[t].Dot(tn[o]) > 1 - 1e-9) parent[Find(t)] = Find(o);
        var faceN = new List<D3>(); var faceD = new List<double>(); var faceLoop = new List<int[]>(); var faceTris = new List<List<int>>();
        var faceOf = new Dictionary<(int, int), int>();
        void AddFace(int[] loop, List<int> tris)
        {
            var n = new D3(0, 0, 0);
            for (int i = 0; i < loop.Length; i++) n += D3.Of(bv[loop[i]]).Cross(D3.Of(bv[loop[(i + 1) % loop.Length]]));
            n = n.Unit;
            double d = loop.Average(i => n.Dot(D3.Of(bv[i])));
            int j = faceN.Count; faceN.Add(n); faceD.Add(d); faceLoop.Add(loop); faceTris.Add(tris);
            for (int i = 0; i < loop.Length; i++) faceOf[(loop[i], loop[(i + 1) % loop.Length])] = j;
        }
        foreach (var g in Enumerable.Range(0, nb).GroupBy(Find).Select(g => g.ToList()))
        {
            var inGroup = new HashSet<(int, int)>();
            foreach (int t in g) for (int e = 0; e < 3; e++) inGroup.Add((bt[3 * t + e], bt[3 * t + (e + 1) % 3]));
            var next = new Dictionary<int, int>(); bool simple = true;
            foreach (var (p, q) in inGroup) if (!inGroup.Contains((q, p))) { if (next.ContainsKey(p)) simple = false; next[p] = q; }
            var loop = new List<int> { next.Keys.First() };
            while (simple) { int w = next[loop[^1]]; if (w == loop[0]) break; loop.Add(w); if (loop.Count > next.Count) simple = false; }
            if (!simple || loop.Count != next.Count)
            {
                foreach (int t in g) AddFace(new[] { bt[3 * t], bt[3 * t + 1], bt[3 * t + 2] }, new List<int> { t }); // not one simple polygon: its triangles are faces of their own
                continue;
            }
            AddFace(loop.ToArray(), g);
        }
        int planes = faceN.Count;
        double Dist(D3 p, int j) => faceN[j].Dot(p) - faceD[j];

        // The vertex registry: A's vertices first, then the new ones by key.
        var pos = new List<D3>(A);
        var keyed = new Dictionary<(int, int, int, int), int>();
        var keyOf = new Dictionary<int, (int Kind, int A, int B, int C)>();
        int Key((int, int, int, int) k, Func<D3> make) { if (!keyed.TryGetValue(k, out int i)) { keyed[k] = i = pos.Count; keyOf[i] = k; pos.Add(make()); stats.NewVertices++; } return i; }
        int EdgePlane(int p, int q, int j)
        {
            int lo = Math.Min(p, q), hi = Math.Max(p, q);
            return Key((1, lo, hi, j), () =>
            {
                double s0 = Dist(A[lo], j), s1 = Dist(A[hi], j);
                if ((s0 < 0) == (s1 < 0)) stats.Fault($"edge {lo}-{hi} does not cross plane {j}");
                return A[lo] + (A[hi] - A[lo]) * (s0 / (s0 - s1));
            });
        }
        int triCount = at.Length / 3;
        var triN = new D3[triCount]; var triD = new double[triCount];
        for (int t = 0; t < triCount; t++) { var s = a.Planes[a.Tag[t]]; triN[t] = new D3(s.Nx, s.Ny, s.Nz); triD[t] = s.D; }
        int Point3(int t, int j, int m)
        {
            int lo = Math.Min(j, m), hi = Math.Max(j, m);
            return Key((2, t, lo, hi), () =>
            {
                D3 n1 = triN[t], n2 = faceN[lo], n3 = faceN[hi]; double d1 = triD[t], d2 = faceD[lo], d3 = faceD[hi];
                double det = n1.Dot(n2.Cross(n3));
                if (Math.Abs(det) < 1e-12) stats.Fault($"triangle {t} parallel to the edge of planes {lo},{hi}");
                return (n2.Cross(n3) * d1 + n3.Cross(n1) * d2 + n1.Cross(n2) * d3) * (1 / det);
            });
        }

        var outTris = new List<int>(); var outTag = new List<int>();
        var patch = new List<(int, int)>[planes];
        for (int j = 0; j < planes; j++) patch[j] = new List<(int, int)>();
        var pierce = new Dictionary<(int, int), List<int>>();

        // Label of the edge leaving a polygon vertex: 0..2 is the triangle's edge k (corner k to corner k + 1), 3 + j lies on B's plane j.
        for (int t = 0; t < triCount; t++)
        {
            int[] c = { at[3 * t], at[3 * t + 1], at[3 * t + 2] };
            var poly = new List<(int V, int L)> { (c[0], 0), (c[1], 1), (c[2], 2) };
            bool clipped = false;
            for (int j = 0; j < planes && poly.Count > 0; j++)
            {
                var s = poly.Select(p => Dist(pos[p.V], j) < 0).ToArray();
                if (s.All(x => x)) continue;
                clipped = true;
                if (!s.Any(x => x)) { poly.Clear(); break; }
                var next = new List<(int, int)>();
                for (int i = 0; i < poly.Count; i++)
                {
                    var (pv, pl) = poly[i]; bool pin = s[i], qin = s[(i + 1) % poly.Count];
                    int Cross() => pl < 3 ? EdgePlane(c[pl], c[(pl + 1) % 3], j) : Point3(t, j, pl - 3);
                    if (pin) { next.Add((pv, pl)); if (!qin) next.Add((Cross(), 3 + j)); }
                    else if (qin) next.Add((Cross(), pl));
                }
                poly = next;
            }
            if (poly.Count == 0) { outTris.AddRange(c); outTag.Add(a.Tag[t]); continue; } // outside B
            if (!clipped) continue; // inside B
            stats.CutTriangles++;
            // T minus C: T's edges less the pieces C runs along, and C's plane edges reversed; C's plane edges go to their faces' patches as they are.
            var edges = new List<(int, int)>();
            var cAlong = new HashSet<(int, int)>();
            for (int i = 0; i < poly.Count; i++)
            {
                var (u, l) = poly[i]; int w = poly[(i + 1) % poly.Count].V;
                if (l < 3) cAlong.Add((u, w));
                else { edges.Add((w, u)); patch[l - 3].Add((u, w)); }
                if (keyOf.TryGetValue(u, out var ku) && ku.Kind == 2)
                {
                    if (!pierce.TryGetValue((ku.B, ku.C), out var list)) pierce[(ku.B, ku.C)] = list = new List<int>();
                    list.Add(u);
                }
            }
            for (int k = 0; k < 3; k++)
            {
                int p = c[k], q = c[(k + 1) % 3];
                var on = new List<int> { p, q };
                foreach (var (v, _) in poly) if (keyOf.TryGetValue(v, out var kv) && kv.Kind == 1 && kv.A == Math.Min(p, q) && kv.B == Math.Max(p, q)) on.Add(v);
                var da = pos[q] - pos[p]; double dl = da.Dot(da);
                var sorted = on.Distinct().OrderBy(v => (pos[v] - pos[p]).Dot(da) / dl).ToList();
                for (int i = 0; i + 1 < sorted.Count; i++) if (!cAlong.Contains((sorted[i], sorted[i + 1]))) edges.Add((sorted[i], sorted[i + 1]));
            }
            Triangulate(edges, triN[t], pos, outTris, stats, $"triangle {t}");
            while (outTag.Count < outTris.Count / 3) outTag.Add(a.Tag[t]);
        }

        // B's edges inside A, and B's faces' patches.
        var inside = new bool?[bv.Length];
        bool Inside(int k) => inside[k] ??= Winding(D3.Of(bv[k]), A, at) > 0.5;
        int BVert(int k) => Key((3, k, 0, 0), () => D3.Of(bv[k]));
        for (int j = 0; j < planes; j++)
        {
            var loop = faceLoop[j];
            for (int i = 0; i < loop.Length; i++)
            {
                int p = loop[i], q = loop[(i + 1) % loop.Length];
                if (!faceOf.TryGetValue((q, p), out int m)) { stats.Fault("open cutter"); continue; }
                if (j > m) continue; // each B edge once
                var pa = D3.Of(bv[p]); var dir = D3.Of(bv[q]) - pa; double dl = dir.Dot(dir);
                var pts = pierce.TryGetValue((Math.Min(j, m), Math.Max(j, m)), out var l0) ? l0.Distinct().OrderBy(v => (pos[v] - pa).Dot(dir) / dl).ToList() : new List<int>();
                bool state = Inside(p); int cur = state ? BVert(p) : -1;
                foreach (int v in pts)
                {
                    if (state) { patch[j].Add((v, cur)); patch[m].Add((cur, v)); }
                    state = !state; cur = v;
                }
                if (state != Inside(q)) stats.Fault($"B edge {p}-{q}: {pts.Count} piercings but inside {Inside(p)} -> {Inside(q)}");
                if (state) { int e = BVert(q); patch[j].Add((e, cur)); patch[m].Add((cur, e)); }
            }
        }
        int wallBase = a.Planes.Length;
        for (int j = 0; j < planes; j++)
        {
            if (patch[j].Count == 0) continue;
            Triangulate(patch[j], -faceN[j], pos, outTris, stats, $"patch {j}");
            while (outTag.Count < outTris.Count / 3)
            {
                int k = outTag.Count * 3;
                var m = (pos[outTris[k]] + pos[outTris[k + 1]] + pos[outTris[k + 2]]) * (1.0 / 3);
                outTag.Add(wallBase + HoldingTriangle(faceTris[j], bt, bv, tn, m));
            }
        }

        // Weld: keep the used vertices in order.
        var map = new Dictionary<int, int>(); var v2 = new List<float3>(); var t2 = new int[outTris.Count];
        for (int i = 0; i < outTris.Count; i++)
        {
            if (!map.TryGetValue(outTris[i], out int m)) { map[outTris[i]] = m = v2.Count; v2.Add(pos[outTris[i]].F); }
            t2[i] = m;
        }
        var directed = new Dictionary<(int, int), int>();
        for (int i = 0; i < t2.Length; i += 3) for (int e = 0; e < 3; e++) { var k = (t2[i + e], t2[i + (e + 1) % 3]); directed[k] = directed.GetValueOrDefault(k) + 1; }
        int open = directed.Count(kv => kv.Value != 1 || directed.GetValueOrDefault((kv.Key.Item2, kv.Key.Item1)) != 1);
        if (open > 0) stats.Fault($"{open} directed edges unpaired");
        var all = new SourcePlane[a.Planes.Length + nb];
        a.Planes.CopyTo(all, 0);
        for (int k = 0; k < nb; k++) all[wallBase + k] = new SourcePlane(-tn[k].X, -tn[k].Y, -tn[k].Z, -tn[k].Dot(D3.Of(bv[bt[3 * k]])));
        return new PuppetSolid(v2.ToArray(), t2, outTag.ToArray(), all);
    }

    /// <summary>The triangle of <paramref name="tris"/> (indices into B) that holds the point <paramref name="m"/> of their plane: the one it lies deepest inside.</summary>
    private static int HoldingTriangle(List<int> tris, int[] bt, float3[] bv, D3[] tn, D3 m)
    {
        int best = tris[0]; double bestDepth = double.NegativeInfinity;
        foreach (int t in tris)
        {
            var a = D3.Of(bv[bt[3 * t]]); var b = D3.Of(bv[bt[3 * t + 1]]); var c = D3.Of(bv[bt[3 * t + 2]]);
            double depth = double.MaxValue;
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) }) depth = Math.Min(depth, tn[t].Cross(q - p).Dot(m - p) / (q - p).Len);
            if (depth > bestDepth) { bestDepth = depth; best = t; }
        }
        return best;
    }

    /// <summary>
    /// The convex hull of the points (incremental; a point within 1e-9 m of a face's plane is inside), triangles counter-clockwise from outside. The cutter of a socket is one: its vertices are the ball's
    /// grown lattice, the ring pushed out and the ring carried into the shadow.
    /// </summary>
    public static (float3[] V, int[] Tris) Hull(IReadOnlyList<float3> pts)
    {
        // A point on the segment between two others is never extreme, and the incremental hull cannot take it: a collinear point makes a degenerate face. Drop them.
        var kept = Enumerable.Range(0, pts.Count).Where(k =>
        {
            var x = D3.Of(pts[k]);
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    if (i == k || j == k) continue;
                    var a = D3.Of(pts[i]); var d = D3.Of(pts[j]) - a; double s = (x - a).Dot(d) / d.Dot(d);
                    if (s > 0 && s < 1 && (x - (a + d * s)).Len < 1e-8) return false;
                }
            return true;
        }).ToList();
        pts = kept.Select(k => pts[k]).ToList();
        var p = pts.Select(D3.Of).ToList(); int n = p.Count; const double eps = 1e-9;
        int i0 = 0, i1 = Enumerable.Range(0, n).MaxBy(i => (p[i] - p[i0]).Len);
        var d01 = (p[i1] - p[i0]).Unit;
        int i2 = Enumerable.Range(0, n).MaxBy(i => ((p[i] - p[i0]) - d01 * (p[i] - p[i0]).Dot(d01)).Len);
        var nn = (p[i1] - p[i0]).Cross(p[i2] - p[i0]).Unit;
        int i3 = Enumerable.Range(0, n).MaxBy(i => Math.Abs((p[i] - p[i0]).Dot(nn)));
        var faces = new List<int[]>(); var alive = new List<bool>();
        var inner = (p[i0] + p[i1] + p[i2] + p[i3]) * 0.25;
        void Add(int a, int b, int c)
        {
            if ((p[b] - p[a]).Cross(p[c] - p[a]).Dot(p[a] - inner) < 0) (b, c) = (c, b);
            faces.Add(new[] { a, b, c }); alive.Add(true);
        }
        Add(i0, i1, i2); Add(i0, i1, i3); Add(i0, i2, i3); Add(i1, i2, i3);
        double Dist(int f, int q) { var t = faces[f]; var fn = (p[t[1]] - p[t[0]]).Cross(p[t[2]] - p[t[0]]).Unit; return fn.Dot(p[q] - p[t[0]]); }
        for (int q = 0; q < n; q++)
        {
            if (q == i0 || q == i1 || q == i2 || q == i3) continue;
            var edgeFace = new Dictionary<(int, int), int>();
            for (int f = 0; f < faces.Count; f++) if (alive[f]) for (int k = 0; k < 3; k++) edgeFace[(faces[f][k], faces[f][(k + 1) % 3])] = f;
            int seed = -1; double best = eps;
            for (int f = 0; f < faces.Count; f++) if (alive[f]) { double d = Dist(f, q); if (d > best) { best = d; seed = f; } }
            if (seed < 0) continue;
            // the visible region grown from the farthest face across edges (one connected cap), so the horizon is one loop
            var vis = new HashSet<int> { seed }; var stack = new Stack<int>(); stack.Push(seed);
            while (stack.Count > 0) { int f = stack.Pop(); for (int k = 0; k < 3; k++) { int g = edgeFace[(faces[f][(k + 1) % 3], faces[f][k])]; if (!vis.Contains(g) && Dist(g, q) > eps) { vis.Add(g); stack.Push(g); } } }
            var horizon = new List<(int, int)>();
            foreach (int f in vis) for (int k = 0; k < 3; k++) { int u = faces[f][k], w = faces[f][(k + 1) % 3]; if (!vis.Contains(edgeFace[(w, u)])) horizon.Add((u, w)); }
            foreach (int f in vis) alive[f] = false;
            foreach (var (u, w) in horizon) { faces.Add(new[] { u, w, q }); alive.Add(true); }
        }
        var map = new Dictionary<int, int>(); var ov = new List<float3>(); var ot = new List<int>();
        for (int f = 0; f < faces.Count; f++)
        {
            if (!alive[f]) continue;
            foreach (int w in faces[f]) { if (!map.TryGetValue(w, out int m)) { map[w] = m = ov.Count; ov.Add(pts[w]); } ot.Add(m); }
        }
        return (ov.ToArray(), ot.ToArray());
    }

    /// <summary>
    /// The boolean's slivers, tidied without leaving a source plane. An edge under <paramref name="shortEdge"/>, or the shortest edge of a triangle under 0.01 mm2 when it is under <paramref name="sliverEdge"/>, is
    /// collapsed onto one of its endpoints (never a midpoint: the endpoint that lies on every source plane of the other's triangles, so every fragment stays on its source plane; refused if neither does) when
    /// the link condition holds and the vote passes (no fan triangle's signed area on its own source plane is turned over or flattened, whatever its size); then every edge whose two triangles are of one source (coplanar) is flipped when that raises the pair's least
    /// angle (a planar re-triangulation: nothing moves). Tags are carried, and the planes are the solid's.
    /// </summary>
    public static PuppetSolid Tidy(PuppetSolid s, float shortEdge, float sliverEdge, Stats stats)
    {
        const double onPlane = 1e-5, sliverArea = 1e-8;
        var v = s.V.Select(D3.Of).ToList(); var t = new List<int[]>(); for (int k = 0; k < s.Tris.Length; k += 3) t.Add(new[] { s.Tris[k], s.Tris[k + 1], s.Tris[k + 2] });
        var tag = s.Tag.ToList(); var alive = t.Select(_ => true).ToList();
        D3 N(int[] f) => (v[f[1]] - v[f[0]]).Cross(v[f[2]] - v[f[0]]);
        bool On(int tri, D3 p) { var q = s.Planes[tag[tri]]; return Math.Abs(q.Nx * p.X + q.Ny * p.Y + q.Nz * p.Z - q.D) <= onPlane; }
        double MinAng(int a, int b, int c)
        {
            double Ang(D3 p, D3 q, D3 r) { var x = (q - p).Unit; var y = (r - p).Unit; return Math.Acos(Math.Clamp(x.Dot(y), -1, 1)); }
            return Math.Min(Ang(v[a], v[b], v[c]), Math.Min(Ang(v[b], v[c], v[a]), Ang(v[c], v[a], v[b])));
        }
        Dictionary<(int, int), int> Edges() { var e = new Dictionary<(int, int), int>(); for (int i = 0; i < t.Count; i++) if (alive[i]) for (int k = 0; k < 3; k++) e[(t[i][k], t[i][(k + 1) % 3])] = i; return e; }
        // 1. short edges
        for (bool again = true; again;)
        {
            again = false;
            var e = Edges(); var vt = new Dictionary<int, List<int>>();
            for (int i = 0; i < t.Count; i++) if (alive[i]) foreach (int w in t[i]) { if (!vt.TryGetValue(w, out var l)) vt[w] = l = new List<int>(); l.Add(i); }
            foreach (var ((a, b), ti) in e)
            {
                if (a > b || !alive[ti]) continue;
                double len = (v[a] - v[b]).Len;
                var tf = t[ti]; double area = 0.5 * N(tf).Len;
                bool shortest = Enumerable.Range(0, 3).All(k => (v[tf[k]] - v[tf[(k + 1) % 3]]).Len >= len - 1e-15);
                if (!(len < shortEdge || (area < sliverArea && shortest && len < sliverEdge))) continue;
                if (!e.TryGetValue((b, a), out int to) || !alive[to]) continue;
                var ra = new HashSet<int>(vt[a].Where(i => alive[i]).SelectMany(i => t[i])); var rb = new HashSet<int>(vt[b].Where(i => alive[i]).SelectMany(i => t[i]));
                ra.Remove(a); rb.Remove(b);
                if (ra.Intersect(rb).Count() != 2) continue; // link condition
                // the vertex that goes (m) onto the one that stays (x): x must lie on every source plane of m's other triangles
                bool Collapse(int m, int x)
                {
                    foreach (int i in vt[m]) if (alive[i] && i != ti && i != to && !On(i, v[x])) return false;
                    foreach (int i in vt[m].Concat(vt[x]).Distinct())
                    {
                        if (!alive[i] || i == ti || i == to) continue;
                        // the vote: every surviving fan triangle judges the collapse by its signed area on its own source plane, which is exact where its own normal is noise;
                        // it may not be turned over or flattened by the collapse (one that is already against its source may not be made worse)
                        var q = s.Planes[tag[i]]; var sn = new D3(q.Nx, q.Ny, q.Nz);
                        double before = sn.Dot(N(t[i])), after = sn.Dot(N(t[i].Select(w => w == m ? x : w).ToArray()));
                        if (after <= 0 && after < before) return false;
                    }
                    alive[ti] = false; alive[to] = false;
                    foreach (int i in vt[m]) if (alive[i]) for (int k = 0; k < 3; k++) if (t[i][k] == m) t[i][k] = x;
                    return true;
                }
                if (!Collapse(b, a) && !Collapse(a, b)) continue;
                stats.Collapsed++; again = true; break;
            }
        }
        // 2. flips between triangles of one source
        for (int pass = 0; pass < 20; pass++)
        {
            bool any = false; var e = Edges();
            foreach (var ((a, b), ti) in e.ToList())
            {
                if (a > b || !alive[ti] || !e.TryGetValue((b, a), out int to) || !alive[to] || e[(a, b)] != ti || tag[ti] != tag[to]) continue;
                int c = t[ti].First(w => w != a && w != b), d = t[to].First(w => w != a && w != b);
                if (c == d || e.ContainsKey((c, d)) || e.ContainsKey((d, c))) continue;
                var n1 = N(t[ti]); var f1 = new[] { a, d, c }; var f2 = new[] { b, c, d };
                if (N(f1).Dot(n1) <= 0 || N(f2).Dot(n1) <= 0) continue; // not a convex quad
                double before = Math.Min(MinAng(t[ti][0], t[ti][1], t[ti][2]), MinAng(t[to][0], t[to][1], t[to][2]));
                double after = Math.Min(MinAng(a, d, c), MinAng(b, c, d));
                if (after <= before + 1e-9) continue;
                t[ti] = f1; t[to] = f2; stats.Flipped++; any = true;
                e = Edges();
            }
            if (!any) break;
        }
        var map = new Dictionary<int, int>(); var ov = new List<float3>(); var ot = new List<int>(); var otag = new List<int>();
        for (int i = 0; i < t.Count; i++)
        {
            if (!alive[i]) continue;
            foreach (int w in t[i]) { if (!map.TryGetValue(w, out int m)) { map[w] = m = ov.Count; ov.Add(v[w].F); } ot.Add(m); }
            otag.Add(tag[i]);
        }
        return new PuppetSolid(ov.ToArray(), ot.ToArray(), otag.ToArray(), s.Planes);
    }

    /// <summary>The generalised winding number of the closed mesh at p (Jacobson et al. 2013, solid angles by Van Oosterom and Strackee 1983).</summary>
    private static double Winding(D3 p, D3[] v, int[] t)
    {
        double sum = 0;
        for (int i = 0; i < t.Length; i += 3)
        {
            var a = v[t[i]] - p; var b = v[t[i + 1]] - p; var c = v[t[i + 2]] - p;
            double la = a.Len, lb = b.Len, lc = c.Len;
            sum += 2 * Math.Atan2(a.Dot(b.Cross(c)), la * lb * lc + a.Dot(b) * lc + a.Dot(c) * lb + b.Dot(c) * la);
        }
        return sum / (4 * Math.PI);
    }

    /// <summary>Chains directed edges into loops and ear-clips them in the plane of normal n (outer loops counter-clockwise about n, holes bridged in).</summary>
    private static void Triangulate(List<(int, int)> edges, D3 n, List<D3> pos, List<int> outTris, Stats stats, string what)
    {
        var next = new Dictionary<int, int>();
        foreach (var (a, b) in edges) { if (next.ContainsKey(a)) { stats.Fault($"{what}: vertex {a} leaves twice"); return; } next[a] = b; }
        var u = Math.Abs(n.X) < 0.9 ? new D3(1, 0, 0).Cross(n).Unit : new D3(0, 1, 0).Cross(n).Unit; var w = n.Cross(u);
        (double X, double Y) P(int i) => (pos[i].Dot(u), pos[i].Dot(w));
        var loops = new List<List<int>>(); var seen = new HashSet<int>();
        foreach (int s in next.Keys)
        {
            if (seen.Contains(s)) continue;
            var loop = new List<int>(); int cur = s;
            while (seen.Add(cur)) { loop.Add(cur); if (!next.TryGetValue(cur, out cur)) { stats.Fault($"{what}: open loop"); return; } }
            if (cur != s) { stats.Fault($"{what}: loop does not close"); return; }
            loops.Add(loop);
        }
        double Area(List<int> l) { double a = 0; for (int i = 0; i < l.Count; i++) { var p = P(l[i]); var q = P(l[(i + 1) % l.Count]); a += p.X * q.Y - q.X * p.Y; } return a / 2; }
        var outers = loops.Where(l => Area(l) > 0).ToList(); var holes = loops.Where(l => Area(l) <= 0).ToList();
        if (outers.Count == 0) { stats.Fault($"{what}: no outer loop ({loops.Count} loops)"); return; }
        bool InPoly(List<int> l, (double X, double Y) q)
        {
            bool c = false;
            for (int i = 0, k = l.Count - 1; i < l.Count; k = i++) { var a = P(l[i]); var b = P(l[k]); if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) c = !c; }
            return c;
        }
        foreach (var outer in outers)
        {
            var poly = new List<int>(outer);
            foreach (var h in holes.Where(h => InPoly(outer, P(h[0]))).ToList())
            {
                int hi = Enumerable.Range(0, h.Count).MaxBy(i => P(h[i]).X);
                var hp = P(h[hi]);
                int best = -1;
                foreach (int oi in Enumerable.Range(0, poly.Count).OrderBy(i => { var q = P(poly[i]); return (q.X - hp.X) * (q.X - hp.X) + (q.Y - hp.Y) * (q.Y - hp.Y); }))
                {
                    var op = P(poly[oi]); bool clear = true;
                    foreach (var l in new[] { poly, h })
                        for (int i = 0; i < l.Count && clear; i++)
                        {
                            int a = l[i], b = l[(i + 1) % l.Count];
                            if (a == poly[oi] || b == poly[oi] || a == h[hi] || b == h[hi]) continue;
                            if (SegmentsCross(hp, op, P(a), P(b))) clear = false;
                        }
                    if (clear) { best = oi; break; }
                }
                if (best < 0) { stats.Fault($"{what}: no bridge"); return; }
                var merged = poly.Take(best + 1).ToList();
                for (int k = 0; k <= h.Count; k++) merged.Add(h[(hi + k) % h.Count]);
                merged.Add(poly[best]); merged.AddRange(poly.Skip(best + 1));
                poly = merged;
            }
            EarClip(poly, P, outTris, stats, what);
        }
    }

    private static bool SegmentsCross((double X, double Y) p, (double X, double Y) q, (double X, double Y) a, (double X, double Y) b)
    {
        double O((double X, double Y) s, (double X, double Y) t, (double X, double Y) r) => (t.X - s.X) * (r.Y - s.Y) - (t.Y - s.Y) * (r.X - s.X);
        double d1 = O(p, q, a), d2 = O(p, q, b), d3 = O(a, b, p), d4 = O(a, b, q);
        return (d1 > 0) != (d2 > 0) && (d3 > 0) != (d4 > 0);
    }

    /// <summary>Ear clipping, the ear with the largest least angle first (slivers last).</summary>
    private static void EarClip(List<int> poly, Func<int, (double X, double Y)> P, List<int> outTris, Stats stats, string what)
    {
        var l = new List<int>(poly);
        while (l.Count > 3)
        {
            int bestI = -1; double bestQ = -1;
            for (int i = 0; i < l.Count; i++)
            {
                int ia = l[(i + l.Count - 1) % l.Count], ib = l[i], ic = l[(i + 1) % l.Count];
                var a = P(ia); var b = P(ib); var c = P(ic);
                double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (cross <= 1e-16) continue;
                bool empty = true;
                foreach (int k in l)
                {
                    if (k == ia || k == ib || k == ic) continue;
                    var q = P(k);
                    if (q == a || q == b || q == c) continue;
                    double w0 = (b.X - q.X) * (c.Y - q.Y) - (b.Y - q.Y) * (c.X - q.X), w1 = (c.X - q.X) * (a.Y - q.Y) - (c.Y - q.Y) * (a.X - q.X), w2 = (a.X - q.X) * (b.Y - q.Y) - (a.Y - q.Y) * (b.X - q.X);
                    if (w0 >= 0 && w1 >= 0 && w2 >= 0) { empty = false; break; }
                }
                if (!empty) continue;
                double q2 = MinAngle(a, b, c);
                if (q2 > bestQ) { bestQ = q2; bestI = i; }
            }
            if (bestI < 0) { stats.Fault($"{what}: no ear among {l.Count}"); return; }
            int pa = l[(bestI + l.Count - 1) % l.Count], pb = l[bestI], pc = l[(bestI + 1) % l.Count];
            outTris.Add(pa); outTris.Add(pb); outTris.Add(pc);
            stats.MinAngleDeg = Math.Min(stats.MinAngleDeg, bestQ);
            l.RemoveAt(bestI);
        }
        {
            var a = P(l[0]); var b = P(l[1]); var c = P(l[2]);
            if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) <= 0) stats.Fault($"{what}: last ear flat");
            stats.MinAngleDeg = Math.Min(stats.MinAngleDeg, MinAngle(a, b, c));
            outTris.Add(l[0]); outTris.Add(l[1]); outTris.Add(l[2]);
        }
    }

    private static double MinAngle((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double Ang((double X, double Y) p, (double X, double Y) q, (double X, double Y) r)
        {
            double ux = q.X - p.X, uy = q.Y - p.Y, vx = r.X - p.X, vy = r.Y - p.Y;
            return Math.Abs(Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy)) * 180 / Math.PI;
        }
        return Math.Min(Ang(a, b, c), Math.Min(Ang(b, c, a), Ang(c, a, b)));
    }

    /// <summary>Keeps the connected part of largest volume, tags carried; returns what was dropped (m3) and how many parts there were.</summary>
    public static (PuppetSolid Solid, double Dropped, int Parts) LargestSolid(PuppetSolid s)
    {
        var v = s.V; var t = s.Tris;
        var parent = Enumerable.Range(0, v.Length).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int k = 0; k < t.Length; k += 3) { parent[Find(t[k + 1])] = Find(t[k]); parent[Find(t[k + 2])] = Find(t[k]); }
        var vol = new Dictionary<int, double>();
        for (int k = 0; k < t.Length; k += 3) { int r = Find(t[k]); vol[r] = vol.GetValueOrDefault(r) + math.dot(v[t[k]], math.cross(v[t[k + 1]], v[t[k + 2]])) / 6.0; }
        if (vol.Count <= 1) return (s, 0, vol.Count);
        int keep = vol.MaxBy(x => x.Value).Key;
        var map = new Dictionary<int, int>(); var nv = new List<float3>(); var nt = new List<int>(); var ntag = new List<int>();
        for (int k = 0; k < t.Length; k += 3)
        {
            if (Find(t[k]) != keep) continue;
            for (int e = 0; e < 3; e++) { if (!map.TryGetValue(t[k + e], out int m)) { map[t[k + e]] = m = nv.Count; nv.Add(v[t[k + e]]); } nt.Add(m); }
            ntag.Add(s.Tag[k / 3]);
        }
        return (new PuppetSolid(nv.ToArray(), nt.ToArray(), ntag.ToArray(), s.Planes), vol.Where(x => x.Key != keep).Sum(x => x.Value), vol.Count);
    }
}
