using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using BadDay.Ymir;
using Godot;
using Lodur;

namespace BadDay.Client;

/// <summary>
/// Fella's drawn surface: one skinned mesh, MakeHuman's base body fitted to Lodur's plan by tools/FellaMesh (client/fella/fella_skin.glb), drawn on
/// the bones. It owns which vertices exist, where each is drawn every frame, and the drawn bounds; the plan owns the parts and their creation frames
/// (<see cref="SeatedPuppet.Plan"/>), and the carriers of <see cref="BodyRenderer"/> own where each part is. Each vertex has one part, or two at a
/// joint ring (a vertex on faces of two parts, one copy per part with the same two weights, so the copies coincide while the parts are joined).
/// Every frame the vertices are rebuilt from the carriers' interpolated poses in one pass and written into one dynamic surface in place; the pass
/// allocates nothing. The file is read as bytes (no import step decides vertex data) and is never edited: rerun the build.
/// </summary>
public partial class FellaSkin : Node3D
{
    public const string AssetPath = "res://fella/fella_skin.glb";

    private const int PositionBytes = 12; // three floats
    private const int NormalBytes = 8;    // octahedral normal and tangent, four 16-bit values each vertex; the tangent is never written

    private Vector3[] _rest = Array.Empty<Vector3>();
    private Vector3[] _restNormal = Array.Empty<Vector3>();
    private Vector3[] _drawn = Array.Empty<Vector3>();
    private int[] _partA = Array.Empty<int>(), _partB = Array.Empty<int>();
    private float[] _weightA = Array.Empty<float>(), _weightB = Array.Empty<float>();
    private int[][] _rings = Array.Empty<int[]>();
    private int[] _owned = Array.Empty<int>();
    private Transform3D[] _inverseCreation = Array.Empty<Transform3D>();
    private Transform3D[] _skin = Array.Empty<Transform3D>();
    private byte[] _data = Array.Empty<byte>();
    private ArrayMesh _mesh = null!;
    private Rid _rid;
    private Aabb? _bounds;

    /// <summary>The surfaces made for the session's life: 1.</summary>
    public int MeshBuilds { get; private set; }

    /// <summary>Vertices drawn, joint-ring copies counted once each.</summary>
    public int VertexCount => _rest.Length;

    /// <summary>The world centre of the drawn bounds as of the last pose, or null before the first: where the camera orbits.</summary>
    public Vector3? Centre => _bounds is { } b ? b.Position + b.Size / 2 : null;

    /// <summary>How many vertices part <paramref name="part"/> owns (the copies of joint-ring vertices count for the part each is emitted for).</summary>
    public int OwnedVertices(int part) => _owned[part];

    /// <summary>The largest distance between the drawn copies of any joint-ring vertex, in metres, as of the last pose: 0 while the parts are joined.</summary>
    public float RingGap()
    {
        float worst = 0f;
        foreach (var ring in _rings)
            for (int i = 0; i < ring.Length; i++)
                for (int j = i + 1; j < ring.Length; j++)
                    worst = Math.Max(worst, _drawn[ring[i]].DistanceTo(_drawn[ring[j]]));
        return worst;
    }

    public override void _Ready()
    {
        var plan = SeatedPuppet.Plan;
        var state = new GltfState();
        var bytes = Godot.FileAccess.GetFileAsBytes(AssetPath);
        if (bytes.Length == 0 || new GltfDocument().AppendFromBuffer(bytes, "", state) != Error.Ok) throw new InvalidOperationException($"{AssetPath} cannot be read as glTF");
        var skin = state.GetSkins()[0];
        var nodes = state.GetNodes();
        var joints = skin.Joints;
        if (joints.Length != plan.Parts.Count) throw new InvalidOperationException($"{AssetPath} has {joints.Length} joints for the plan's {plan.Parts.Count} parts");
        var partOfJoint = new int[joints.Length];
        var seen = new HashSet<int>();
        for (int j = 0; j < joints.Length; j++)
        {
            var name = nodes[joints[j]].ResourceName; // "Label" for a Center part, else "Label.Left" or "Label.Right"
            var dot = name.IndexOf('.');
            var side = dot < 0 ? Lodur.Side.Center : Enum.Parse<Lodur.Side>(name[(dot + 1)..]);
            partOfJoint[j] = plan.PartIndex(dot < 0 ? name : name[..dot], side); // throws for a name the plan has no part for
            if (!seen.Add(partOfJoint[j])) throw new InvalidOperationException($"{AssetPath} names part {name} twice");
        }

        var surfaces = state.GetMeshes()[0].Mesh;
        if (surfaces.GetSurfaceCount() != plan.Parts.Count) throw new InvalidOperationException($"{AssetPath} has {surfaces.GetSurfaceCount()} primitives for the plan's {plan.Parts.Count} parts");
        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var indices = new List<int>();
        var partA = new List<int>(); var partB = new List<int>(); var weightA = new List<float>(); var weightB = new List<float>();
        _owned = new int[plan.Parts.Count];
        for (int s = 0; s < surfaces.GetSurfaceCount(); s++) // the build emits one primitive per part, in the plan's order
        {
            var arrays = surfaces.GetSurfaceArrays(s);
            var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var first = positions.Count;
            bool owned = false;
            for (int i = 0; i < v.Length; i++)
            {
                int a = partOfJoint[bones[4 * i]], b = partOfJoint[bones[4 * i + 1]];
                float wa = weights[4 * i], wb = weights[4 * i + 1];
                if (wb == 0f) b = a;
                owned |= a == s || b == s;
                positions.Add(v[i]); normals.Add(n[i]); uvs.Add(uv[i]);
                partA.Add(a); partB.Add(b); weightA.Add(wa); weightB.Add(wb);
            }
            if (!owned) throw new InvalidOperationException($"{AssetPath}: primitive {s} has no vertex weighted to its part");
            foreach (var index in arrays[(int)Mesh.ArrayType.Index].AsInt32Array()) indices.Add(first + index);
            _owned[s] = v.Length;
        }
        _rest = positions.ToArray(); _restNormal = normals.ToArray(); _drawn = positions.ToArray();
        _partA = partA.ToArray(); _partB = partB.ToArray(); _weightA = weightA.ToArray(); _weightB = weightB.ToArray();

        var byPosition = new Dictionary<Vector3, List<int>>(); // the copies of one ring vertex have the same rest position
        for (int i = 0; i < _rest.Length; i++)
        {
            if (!byPosition.TryGetValue(_rest[i], out var list)) byPosition[_rest[i]] = list = new List<int>();
            list.Add(i);
        }
        var rings = new List<int[]>();
        foreach (var list in byPosition.Values) if (list.Count > 1) rings.Add(list.ToArray());
        _rings = rings.ToArray();

        _inverseCreation = new Transform3D[plan.Parts.Count];
        _skin = new Transform3D[plan.Parts.Count];
        for (int p = 0; p < plan.Parts.Count; p++)
        {
            var def = plan.Parts[p];
            var frame = new Transform3D(new Basis(new Quaternion(def.Rotation.X, def.Rotation.Y, def.Rotation.Z, def.Rotation.W)), new Vector3(def.Position.X, def.Position.Y, def.Position.Z));
            _inverseCreation[p] = frame.AffineInverse();
            _skin[p] = Transform3D.Identity;
        }

        var surface = new Godot.Collections.Array();
        surface.Resize((int)Mesh.ArrayType.Max);
        surface[(int)Mesh.ArrayType.Vertex] = _rest;
        surface[(int)Mesh.ArrayType.Normal] = _restNormal;
        surface[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        surface[(int)Mesh.ArrayType.Index] = indices.ToArray();
        _mesh = new ArrayMesh();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surface, flags: Mesh.ArrayFormat.FlagUseDynamicUpdate);
        _rid = _mesh.GetRid();
        _data = RenderingServer.MeshGetSurface(_rid, 0)["vertex_data"].AsByteArray(); // positions, then normals and tangents: the layout this class rewrites in place
        if (_data.Length != _rest.Length * (PositionBytes + NormalBytes)) throw new InvalidOperationException($"the dynamic surface holds {_data.Length} vertex bytes, not {_rest.Length * (PositionBytes + NormalBytes)}");
        AddChild(new MeshInstance3D { Name = "Mesh", Mesh = _mesh, MaterialOverride = Look.Ps1(Look.SkinTexture, Colors.White) });
        MeshBuilds = 1;
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off; // the vertices are already placed from interpolated poses
    }

    public override void _Process(double delta) => Deform();

    /// <summary>Redraws every vertex from the carriers' interpolated poses: p = sum of w (pose * creation frame inverse) p_rest, normals by the same rotation.</summary>
    public void Deform()
    {
        var bodies = GetParent<BodyRenderer>();
        for (int p = 0; p < _skin.Length; p++)
            if (bodies.InterpolatedPoseOf(SeatedPuppet.FirstStableId + (ulong)p) is { } pose) _skin[p] = pose * _inverseCreation[p];
        var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var high = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        int normalBase = _rest.Length * PositionBytes;
        for (int i = 0; i < _rest.Length; i++)
        {
            var ma = _skin[_partA[i]];
            var p = ma * _rest[i];
            var n = ma.Basis * _restNormal[i];
            if (_weightB[i] > 0f)
            {
                var mb = _skin[_partB[i]];
                p = p * _weightA[i] + mb * _rest[i] * _weightB[i];
                n = n * _weightA[i] + mb.Basis * _restNormal[i] * _weightB[i];
            }
            _drawn[i] = p;
            low = new Vector3(Math.Min(low.X, p.X), Math.Min(low.Y, p.Y), Math.Min(low.Z, p.Z));
            high = new Vector3(Math.Max(high.X, p.X), Math.Max(high.Y, p.Y), Math.Max(high.Z, p.Z));
            var at = _data.AsSpan(i * PositionBytes);
            BinaryPrimitives.WriteSingleLittleEndian(at, p.X);
            BinaryPrimitives.WriteSingleLittleEndian(at[4..], p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(at[8..], p.Z);
            Octahedral(n, _data.AsSpan(normalBase + i * NormalBytes));
        }
        RenderingServer.MeshSurfaceUpdateVertexRegion(_rid, 0, 0, _data);
        _bounds = new Aabb(low, high - low);
        _mesh.CustomAabb = _bounds.Value;
    }

    /// <summary>The median time of <paramref name="frames"/> deforms, in microseconds: the cost line the smoke prints.</summary>
    public double MedianDeformMicroseconds(int frames)
    {
        var ticks = new long[frames];
        for (int f = 0; f < frames; f++)
        {
            long start = Stopwatch.GetTimestamp();
            Deform();
            ticks[f] = Stopwatch.GetTimestamp() - start;
        }
        Array.Sort(ticks);
        return ticks[frames / 2] * 1e6 / Stopwatch.Frequency;
    }

    /// <summary>Godot's surface normal encoding: the octahedral map of the unit normal, two 16-bit values.</summary>
    private static void Octahedral(Vector3 normal, Span<byte> into)
    {
        normal /= Math.Abs(normal.X) + Math.Abs(normal.Y) + Math.Abs(normal.Z);
        float x = normal.X, y = normal.Y;
        if (normal.Z < 0f)
        {
            x = (1f - Math.Abs(normal.Y)) * (normal.X >= 0f ? 1f : -1f);
            y = (1f - Math.Abs(normal.X)) * (normal.Y >= 0f ? 1f : -1f);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(into, (ushort)Math.Clamp((x * 0.5f + 0.5f) * 65535f, 0f, 65535f));
        BinaryPrimitives.WriteUInt16LittleEndian(into[2..], (ushort)Math.Clamp((y * 0.5f + 0.5f) * 65535f, 0f, 65535f));
    }
}
