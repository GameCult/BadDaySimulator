using System;
using System.Collections.Generic;
using CultMath;

namespace BadDay.Client;

/// <summary>What a collapse must not do, besides leave the mesh non-manifold: the piece says where a point may lie, and the decimator asks.</summary>
/// <param name="Allowed">Whether a new vertex, fan centroid or fan edge midpoint may lie at the point; the flag is whether the collapse runs under the tight rule (<paramref name="Tight"/>).</param>
/// <param name="Tight">Whether a point lies where collapses run under the tight rule (the band about a socket): a collapse of (u, v) to x does when u, v or x does.</param>
/// <param name="Stray">How far (m) the field's surface may lie from the mesh at a point (the carried surface points): <see cref="PieceField.Stray"/>; the mesh side is <paramref name="Allowed"/>'s.</param>
/// <param name="Guard">Skeleton points and their clearance: no fan triangle may come nearer a point than its clearance (the limb guard).</param>
public sealed record DecimateRules(Func<float3, bool, bool> Allowed, Func<float3, bool> Tight, Func<float3, float> Stray, (float3 P, float Clear)[] Guard);

/// <summary>
/// Edge-collapse decimation of a closed welded mesh, engine-free (edge contraction only, t = 0 in Garland-Heckbert terms, so disjoint parts never
/// aggregate). The cost of collapsing (u, v) to x blends two region errors:
///   D(x) = x^T (Q_u + Q_v) x, the area-weighted plane quadrics of the fine faces merged into the new vertex (L2, Garland-Heckbert 1997);
///   E(x) = sum over the new fan of 2 (A_t - S_t . n_t(x)), where each live triangle t carries the area A_t and area-weighted normal sum S_t of the
///          fine faces it stands for: the L2,1 normal deviation of those faces from their stand-in (Cohen-Steiner et al. 2004);
///   cost = (1 - alpha) D + alpha k E, k in m^2 (D is m^4, E m^2).
/// A collapse is refused if it breaks the link condition (manifoldness), turns a fan triangle by more than 80 degrees, makes one degenerate, or breaks
/// a <see cref="DecimateRules"/> rule at the new vertex, any fan centroid or any fan edge midpoint. The error is bounded both ways (lodur:ruling:decimator-two-sided-error): the mesh stays within the
/// error of the field (the rules above), and the field's surface stays within it of the mesh. Every vertex carries the original surface points it stands for (the fine mesh's vertices, on the field's surface), a collapse
/// merges the carried points of u and v, and it is refused unless each is within its stray limit of the new fan (a sampled, Hausdorff-style bound, as Metro measures: Cignoni, Rocchini, Scopigno 1998), so no lip or tongue
/// of the field thinner than the error is dropped.
/// </summary>
public sealed class PuppetDecimate
{
    /// <summary>cos of the largest turn (80 degrees) a collapse may give a fan triangle.</summary>
    private const float FlipCos = 0.17f;

    private readonly List<float3> _p;
    private readonly List<int[]> _t = new();
    private readonly List<bool> _alive = new();
    private readonly List<double> _area = new();
    private readonly List<(double X, double Y, double Z)> _s = new();
    private readonly List<HashSet<int>> _vt = new();
    private readonly double[][] _q;
    private readonly int[] _ver;
    private readonly bool[] _dead;
    private readonly List<(float3 P, float Limit)>[] _carry;
    private readonly double _alpha, _k;
    private readonly DecimateRules _rules;
    private int _live;

    private PuppetDecimate(float3[] vertices, int[] triangles, double alpha, double k, DecimateRules rules)
    {
        _alpha = alpha; _k = k; _rules = rules;
        _p = new List<float3>(vertices);
        int nv = vertices.Length;
        _q = new double[nv][]; _ver = new int[nv]; _dead = new bool[nv]; _carry = new List<(float3, float)>[nv];
        for (int i = 0; i < nv; i++) { _q[i] = new double[10]; _vt.Add(new HashSet<int>()); _carry[i] = new List<(float3, float)> { (_p[i], rules.Stray(_p[i])) }; }
        for (int k2 = 0; k2 < triangles.Length; k2 += 3)
        {
            var tri = new[] { triangles[k2], triangles[k2 + 1], triangles[k2 + 2] };
            int id = _t.Count; _t.Add(tri); _alive.Add(true);
            var c = math.cross(_p[tri[1]] - _p[tri[0]], _p[tri[2]] - _p[tri[0]]);
            double a = 0.5 * math.length(c); var n = Unit(c);
            _area.Add(a); _s.Add((n.x * a, n.y * a, n.z * a));
            double d = -math.dot(n, _p[tri[0]]);
            foreach (int v in tri) { AddPlane(_q[v], n, d, a); _vt[v].Add(id); }
        }
        _live = _t.Count;
    }

    /// <summary>
    /// Collapses the mesh (vertices, triangles three indices each, wound as given) until no edge can be collapsed without breaking a rule, cheapest
    /// first, and returns what is left, vertices renumbered in order of first use.
    /// </summary>
    public static (float3[] Vertices, int[] Triangles) Run(float3[] vertices, int[] triangles, double alpha, double k, DecimateRules rules)
    {
        var d = new PuppetDecimate(vertices, triangles, alpha, k, rules);
        d.Collapse();
        return d.Snapshot();
    }

    private static float3 Unit(float3 v) { float l = math.length(v); return l > 1e-12f ? v / l : new float3(0, 0, 0); }

    private static void AddPlane(double[] q, float3 n, double d, double w)
    {
        q[0] += w * n.x * n.x; q[1] += w * n.x * n.y; q[2] += w * n.x * n.z; q[3] += w * n.x * d;
        q[4] += w * n.y * n.y; q[5] += w * n.y * n.z; q[6] += w * n.y * d;
        q[7] += w * n.z * n.z; q[8] += w * n.z * d; q[9] += w * d * d;
    }

    private static double QuadricError(double[] a, double[] b, float3 x)
    {
        double X = x.x, Y = x.y, Z = x.z;
        double q0 = a[0] + b[0], q1 = a[1] + b[1], q2 = a[2] + b[2], q3 = a[3] + b[3], q4 = a[4] + b[4], q5 = a[5] + b[5], q6 = a[6] + b[6], q7 = a[7] + b[7], q8 = a[8] + b[8], q9 = a[9] + b[9];
        return q0 * X * X + 2 * q1 * X * Y + 2 * q2 * X * Z + 2 * q3 * X + q4 * Y * Y + 2 * q5 * Y * Z + 2 * q6 * Y + q7 * Z * Z + 2 * q8 * Z + q9;
    }

    private static float3 Solve3(double a00, double a01, double a02, double a11, double a12, double a22, double b0, double b1, double b2)
    {
        double c00 = a11 * a22 - a12 * a12, c01 = a02 * a12 - a01 * a22, c02 = a01 * a12 - a02 * a11;
        double det = a00 * c00 + a01 * c01 + a02 * c02;
        double c11 = a00 * a22 - a02 * a02, c12 = a01 * a02 - a00 * a12, c22 = a00 * a11 - a01 * a01;
        return new float3((float)((c00 * b0 + c01 * b1 + c02 * b2) / det), (float)((c01 * b0 + c11 * b1 + c12 * b2) / det), (float)((c02 * b0 + c12 * b1 + c22 * b2) / det));
    }

    /// <summary>The distance from <paramref name="p"/> to the triangle (a, b, c).</summary>
    public static float PointTriangle(float3 p, float3 a, float3 b, float3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return math.length(p - a);
        var bp = p - b; float d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return math.length(p - b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return math.length(p - (a + ab * (d1 / (d1 - d3))));
        var cp = p - c; float d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return math.length(p - c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return math.length(p - (a + ac * (d2 / (d2 - d6))));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return math.length(p - (b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6))));
        float denom = 1f / (va + vb + vc);
        return math.length(p - (a + ab * (vb * denom) + ac * (vc * denom)));
    }

    private float3 Normal(int t, int u, int v, float3 x)
    {
        var tri = _t[t];
        float3 P(int i) => tri[i] == u || tri[i] == v ? x : _p[tri[i]];
        return math.cross(P(1) - P(0), P(2) - P(0));
    }

    private int Third(int t, int u, int v) { foreach (int w in _t[t]) if (w != u && w != v) return w; return -1; }

    private HashSet<int> Ring(int u)
    {
        var r = new HashSet<int>();
        foreach (int t in _vt[u]) foreach (int w in _t[t]) if (w != u) r.Add(w);
        return r;
    }

    /// <summary>The collapse of edge (u, v): its best position and cost, or cost = +inf when refused.</summary>
    private (double Cost, float3 X) Evaluate(int u, int v, bool validate)
    {
        // Link condition: the common neighbours are exactly the third corners of the two triangles that share the edge.
        var shared = new List<int>(2);
        foreach (int t in _vt[u]) if (_vt[v].Contains(t)) shared.Add(t);
        if (shared.Count != 2) return (double.PositiveInfinity, default);
        var nu = Ring(u); var nv = Ring(v);
        int common = 0; foreach (int w in nu) if (nv.Contains(w)) common++;
        if (common != 2) return (double.PositiveInfinity, default);

        var fan = new List<int>();
        foreach (int t in _vt[u]) if (!shared.Contains(t)) fan.Add(t);
        foreach (int t in _vt[v]) if (!shared.Contains(t)) fan.Add(t);
        // The two dropped triangles' regions go half each to the fan triangles across their other two edges.
        var extraA = new Dictionary<int, double>(); var extraS = new Dictionary<int, (double X, double Y, double Z)>();
        foreach (int d in shared)
        {
            int w = Third(d, u, v);
            foreach (int t in fan)
                if (_t[t].AsSpan().Contains(w) && (_t[t].AsSpan().Contains(u) || _t[t].AsSpan().Contains(v)))
                {
                    extraA[t] = extraA.GetValueOrDefault(t) + 0.5 * _area[d];
                    var s0 = extraS.GetValueOrDefault(t);
                    extraS[t] = (s0.X + 0.5 * _s[d].X, s0.Y + 0.5 * _s[d].Y, s0.Z + 0.5 * _s[d].Z);
                }
        }

        var pu = _p[u]; var pv = _p[v]; float len = math.length(pu - pv);
        bool tight = _rules.Tight(pu) || _rules.Tight(pv);
        var candidates = new List<float3> { pu, pv, (pu + pv) * 0.5f };
        {
            var a = _q[u]; var b = _q[v];
            double a00 = a[0] + b[0], a01 = a[1] + b[1], a02 = a[2] + b[2], a11 = a[4] + b[4], a12 = a[5] + b[5], a22 = a[7] + b[7];
            double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            double scale = Math.Pow(a00 + a11 + a22, 3);
            if (Math.Abs(det) > 1e-6 * scale)
            {
                var x = Solve3(a00, a01, a02, a11, a12, a22, -(a[3] + b[3]), -(a[6] + b[6]), -(a[8] + b[8]));
                if (math.length(x - (pu + pv) * 0.5f) < len) candidates.Add(x);
            }
        }
        var scored = new List<(double Cost, float3 X)>(4);
        foreach (var x in candidates)
        {
            double e = 0; bool ok = true;
            foreach (int t in fan)
            {
                var c = Normal(t, u, v, x); float l = math.length(c);
                var old = math.cross(_p[_t[t][1]] - _p[_t[t][0]], _p[_t[t][2]] - _p[_t[t][0]]);
                if (l < 1e-9f || math.dot(c / l, Unit(old)) < FlipCos) { ok = false; break; }
                var n = c / l;
                var tri = _t[t];
                float3 Q(int i) => tri[i] == u || tri[i] == v ? x : _p[tri[i]];
                var q0 = Q(0); var q1 = Q(1); var q2 = Q(2); var centroid = (q0 + q1 + q2) / 3f;
                float rr = MathF.Max(math.length(q0 - centroid), MathF.Max(math.length(q1 - centroid), math.length(q2 - centroid)));
                foreach (var (gp, clear) in _rules.Guard)
                    if (math.length(gp - centroid) < rr + clear && PointTriangle(gp, q0, q1, q2) < clear) { ok = false; break; }
                if (!ok) break;
                double A = _area[t] + extraA.GetValueOrDefault(t);
                var S = _s[t]; var s1 = extraS.GetValueOrDefault(t);
                e += 2 * (A - ((S.X + s1.X) * n.x + (S.Y + s1.Y) * n.y + (S.Z + s1.Z) * n.z));
            }
            if (!ok) continue;
            double cost = (1 - _alpha) * QuadricError(_q[u], _q[v], x) + _alpha * _k * e;
            scored.Add((cost, x));
        }
        // The rules that ask the piece's field are the dear ones: the cheapest candidate that keeps them wins, and edges are only asked when they come up.
        scored.Sort((p, q) => p.Cost.CompareTo(q.Cost));
        foreach (var (cost, x) in scored)
            if (!validate || (Within(x, fan, u, v, tight) && Hugs(x, fan, u, v))) return (cost, x);
        return (double.PositiveInfinity, default);
    }

    /// <summary>Whether every original surface point u and v carry lies within its stray limit of the new fan (the triangles around x): the field's surface is held by the mesh.</summary>
    private bool Hugs(float3 x, List<int> fan, int u, int v)
    {
        var fanTris = new (float3 A, float3 B, float3 C)[fan.Count];
        for (int i = 0; i < fanTris.Length; i++)
        {
            var tri = _t[fan[i]];
            float3 R(int k) => tri[k] == u || tri[k] == v ? x : _p[tri[k]];
            fanTris[i] = (R(0), R(1), R(2));
        }
        foreach (var carried in new[] { _carry[u], _carry[v] })
            foreach (var (p, limit) in carried)
            {
                bool near = false;
                foreach (var (a, b, c) in fanTris) if (PointTriangle(p, a, b, c) <= limit) { near = true; break; }
                if (!near) return false;
            }
        return true;
    }

    /// <summary>Whether the piece allows the new vertex x, every fan centroid and every fan edge midpoint.</summary>
    private bool Within(float3 x, List<int> fan, int u, int v, bool tight)
    {
        bool under = tight || _rules.Tight(x); // a collapse lands in the band, or starts there
        bool Good(float3 p) => _rules.Allowed(p, under);
        if (!Good(x)) return false;
        foreach (int t in fan)
        {
            var tri = _t[t];
            float3 R(int i) => tri[i] == u || tri[i] == v ? x : _p[tri[i]];
            if (!Good((R(0) + R(1) + R(2)) / 3f) || !Good((R(0) + R(1)) / 2f) || !Good((R(1) + R(2)) / 2f) || !Good((R(2) + R(0)) / 2f)) return false;
        }
        return true;
    }

    private void Collapse()
    {
        var heap = new PriorityQueue<(int U, int V, int Vu, int Vv, float3 X), double>();
        void Push(int u, int v)
        {
            if (u > v) (u, v) = (v, u);
            var (c, x) = Evaluate(u, v, validate: false);
            if (!double.IsPositiveInfinity(c)) heap.Enqueue((u, v, _ver[u], _ver[v], x), c);
        }
        for (int t = 0; t < _t.Count; t++) for (int e = 0; e < 3; e++) { int a = _t[t][e], b = _t[t][(e + 1) % 3]; if (a < b) Push(a, b); }
        while (heap.TryDequeue(out var it, out double queued))
        {
            if (_dead[it.U] || _dead[it.V] || _ver[it.U] != it.Vu || _ver[it.V] != it.Vv) continue;
            // Re-evaluate, now asking the rules: the regions may have changed without a version bump at the far end, and the queue holds costs of unasked candidates.
            var (cost, x) = Evaluate(it.U, it.V, validate: true);
            if (double.IsPositiveInfinity(cost)) continue;
            if (cost > queued + 1e-9 * Math.Abs(queued) + 1e-30) { heap.Enqueue((it.U, it.V, _ver[it.U], _ver[it.V], x), cost); continue; } // dearer than queued: take its turn
            Merge(it.U, it.V, x);
            var touched = Ring(it.U); touched.Add(it.U);
            foreach (int w in touched) _ver[w]++;
            var done = new HashSet<(int, int)>();
            foreach (int w in touched) foreach (int z in Ring(w)) { var key = w < z ? (w, z) : (z, w); if (done.Add(key)) Push(key.Item1, key.Item2); }
        }
    }

    private void Merge(int u, int v, float3 x)
    {
        var shared = new List<int>();
        foreach (int t in _vt[u]) if (_vt[v].Contains(t)) shared.Add(t);
        var fan = new List<int>();
        foreach (int t in _vt[u]) if (!shared.Contains(t)) fan.Add(t);
        foreach (int t in _vt[v]) if (!shared.Contains(t)) fan.Add(t);
        foreach (int d in shared)
        {
            int w = Third(d, u, v);
            foreach (int t in fan)
                if (_t[t].AsSpan().Contains(w) && (_t[t].AsSpan().Contains(u) || _t[t].AsSpan().Contains(v)))
                {
                    _area[t] += 0.5 * _area[d];
                    var s = _s[t];
                    _s[t] = (s.X + 0.5 * _s[d].X, s.Y + 0.5 * _s[d].Y, s.Z + 0.5 * _s[d].Z);
                }
        }
        foreach (int d in shared) { _alive[d] = false; foreach (int w in _t[d]) _vt[w].Remove(d); _live--; }
        foreach (int t in new List<int>(_vt[v])) { var tri = _t[t]; for (int i = 0; i < 3; i++) if (tri[i] == v) tri[i] = u; _vt[u].Add(t); }
        _vt[v].Clear(); _dead[v] = true;
        if (_carry[v].Count > _carry[u].Count) (_carry[u], _carry[v]) = (_carry[v], _carry[u]);
        _carry[u].AddRange(_carry[v]); _carry[v] = null!;
        for (int i = 0; i < 10; i++) _q[u][i] += _q[v][i];
        _p[u] = x;
    }

    private (float3[], int[]) Snapshot()
    {
        var map = new Dictionary<int, int>(); var v = new List<float3>(); var tris = new List<int>();
        for (int t = 0; t < _t.Count; t++)
        {
            if (!_alive[t]) continue;
            foreach (int w in _t[t]) { if (!map.TryGetValue(w, out int m)) { map[w] = m = v.Count; v.Add(_p[w]); } tris.Add(m); }
        }
        return (v.ToArray(), tris.ToArray());
    }
}
