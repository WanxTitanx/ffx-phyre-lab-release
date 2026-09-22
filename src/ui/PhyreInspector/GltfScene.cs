// GltfScene — minimal glTF loader for the inspector (P14).
// Parses the exporter's glTF: base64 buffers, float/ushort accessors,
// node hierarchy with TRS, skins (joints + inverse bind matrices),
// texture image path. No external glTF dependency.

using System.Numerics;
using System.Text.Json;

namespace PhyreInspector;

public sealed class GltfScene
{
    public sealed class Prim
    {
        public Vector3[] Pos = Array.Empty<Vector3>();
        public Vector2[] Uv = Array.Empty<Vector2>();
        public ushort[,] Joints = new ushort[0, 4];
        public float[,] Weights = new float[0, 4];
        public int[] Indices = Array.Empty<int>();
        public int Material = -1;
    }

    public sealed class AnimChannel
    {
        public int Node; public string Path = "";       // translation|rotation|scale
        public float[] Times = Array.Empty<float>();
        public float[] Values = Array.Empty<float>();    // vec3 or quat packed
    }
    public sealed class AnimClip
    {
        public string Name = "";
        public List<AnimChannel> Channels = new();
        public float Duration;
    }

    public List<Prim> Prims = new();
    public List<Matrix4x4> NodeLocal = new();   // per-node local TRS (bind)
    public List<int> NodeParent = new();
    public List<string> NodeName = new();
    public int[] SkinJoints = Array.Empty<int>();
    public Matrix4x4[] InvBind = Array.Empty<Matrix4x4>();
    public string? TexturePng;                   // resolved path
    public Vector3 BoundsMin, BoundsMax;
    public List<AnimClip> Animations = new();

    // Editable pose layer: current TRS per node (starts = bind pose).
    public Vector3[] PoseT = Array.Empty<Vector3>();
    public Quaternion[] PoseR = Array.Empty<Quaternion>();
    public Vector3[] PoseS = Array.Empty<Vector3>();
    public Vector3[] BindT = Array.Empty<Vector3>();
    public Quaternion[] BindR = Array.Empty<Quaternion>();
    public Vector3[] BindS = Array.Empty<Vector3>();

    static void Decompose(Matrix4x4 m, out Vector3 t, out Quaternion r, out Vector3 s)
    {
        Matrix4x4.Decompose(m, out s, out r, out t);
    }

    public void InitPose()
    {
        int n = NodeLocal.Count;
        BindT = new Vector3[n]; BindR = new Quaternion[n]; BindS = new Vector3[n];
        PoseT = new Vector3[n]; PoseR = new Quaternion[n]; PoseS = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Decompose(NodeLocal[i], out BindT[i], out BindR[i], out BindS[i]);
            PoseT[i] = BindT[i]; PoseR[i] = BindR[i]; PoseS[i] = BindS[i];
        }
    }

    public void ResetPose()
    {
        for (int i = 0; i < PoseT.Length; i++)
        { PoseT[i] = BindT[i]; PoseR[i] = BindR[i]; PoseS[i] = BindS[i]; }
    }

    public void SetNodeTransform(int node, Vector3 t, Quaternion r, Vector3 s)
    { PoseT[node] = t; PoseR[node] = r; PoseS[node] = s; }

    // Sample clip at time into the pose layer (linear interp, nlerp for quats).
    public void SampleAnimation(AnimClip clip, float time)
    {
        foreach (var ch in clip.Channels)
        {
            var times = ch.Times;
            if (times.Length == 0 || ch.Node >= PoseT.Length) continue;
            float t = Math.Clamp(time, times[0], times[^1]);
            int i = 0;
            while (i + 1 < times.Length && times[i + 1] < t) i++;
            int j = Math.Min(i + 1, times.Length - 1);
            float f = times[j] > times[i] ? (t - times[i]) / (times[j] - times[i]) : 0;
            if (ch.Path == "rotation")
            {
                var a = new Quaternion(ch.Values[i * 4], ch.Values[i * 4 + 1], ch.Values[i * 4 + 2], ch.Values[i * 4 + 3]);
                var b = new Quaternion(ch.Values[j * 4], ch.Values[j * 4 + 1], ch.Values[j * 4 + 2], ch.Values[j * 4 + 3]);
                PoseR[ch.Node] = Quaternion.Normalize(Quaternion.Lerp(a, b, f));
            }
            else
            {
                var a = new Vector3(ch.Values[i * 3], ch.Values[i * 3 + 1], ch.Values[i * 3 + 2]);
                var b = new Vector3(ch.Values[j * 3], ch.Values[j * 3 + 1], ch.Values[j * 3 + 2]);
                var v = Vector3.Lerp(a, b, f);
                if (ch.Path == "translation") PoseT[ch.Node] = v;
                else if (ch.Path == "scale") PoseS[ch.Node] = v;
            }
        }
    }

    static byte[] GetBuffer(JsonElement buf, string gltfDir)
    {
        var uri = buf.GetProperty("uri").GetString()!;
        if (uri.StartsWith("data:"))
            return Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        var p = Path.GetFullPath(Path.Combine(gltfDir, Uri.UnescapeDataString(uri)));
        if (!File.Exists(p)) throw new FileNotFoundException($"glTF buffer not found: {p}");
        return File.ReadAllBytes(p);
    }

    static float[] ReadFloats(byte[] bin, JsonElement acc, JsonElement views, int n)
    {
        var a = acc;
        var bv = views[a.GetProperty("bufferView").GetInt32()];
        int off = bv.GetProperty("byteOffset").GetInt32() + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        int stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;
        int comps = a.GetProperty("type").GetString() switch
        { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 0 };
        int count = a.GetProperty("count").GetInt32();
        var res = new float[count * comps];
        int compBytes = a.GetProperty("componentType").GetInt32() switch { 5126 => 4, 5123 => 2, 5125 => 4, 5121 => 1, _ => 4 };
        for (int i = 0; i < count; i++)
        {
            int baseOff = off + (stride > 0 ? i * stride : i * comps * compBytes);
            for (int c = 0; c < comps; c++)
            {
                int p = baseOff + c * compBytes;
                res[i * comps + c] = a.GetProperty("componentType").GetInt32() switch
                {
                    5126 => BitConverter.ToSingle(bin, p),
                    5123 => BitConverter.ToUInt16(bin, p),
                    5125 => BitConverter.ToUInt32(bin, p),
                    5121 => bin[p],
                    _ => 0,
                };
            }
        }
        return res;
    }

    public static GltfScene Load(string gltfPath)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(gltfPath)).RootElement;
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(gltfPath))!;
        var bin = GetBuffer(doc.GetProperty("buffers")[0], gltfDir);
        var views = doc.GetProperty("bufferViews");
        var accs = doc.GetProperty("accessors");
        var s = new GltfScene();

        foreach (var n in doc.GetProperty("nodes").EnumerateArray())
        {
            s.NodeName.Add(n.TryGetProperty("name", out var nm) ? nm.GetString()! : "");
            Matrix4x4 m;
            if (n.TryGetProperty("matrix", out var me))
            {
                var v = me.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                m = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
                                  v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
            }
            else
            {
                var t = n.TryGetProperty("translation", out var te) ? te.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray() : new float[3];
                var r = n.TryGetProperty("rotation", out var re) ? re.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray() : new[] { 0f, 0, 0, 1 };
                var sc = n.TryGetProperty("scale", out var se) ? se.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray() : new[] { 1f, 1, 1 };
                m = Matrix4x4.CreateScale(sc[0], sc[1], sc[2])
                    * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3]))
                    * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
            }
            s.NodeLocal.Add(m);
            s.NodeParent.Add(-1);
        }
        // parents from children lists
        int ni = 0;
        foreach (var n in doc.GetProperty("nodes").EnumerateArray())
        {
            if (n.TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray())
                    s.NodeParent[c.GetInt32()] = ni;
            ni++;
        }

        // skin
        if (doc.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
        {
            var sk = skins[0];
            s.SkinJoints = sk.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            var ibm = ReadFloats(bin, accs[sk.GetProperty("inverseBindMatrices").GetInt32()], views, 16);
            s.InvBind = new Matrix4x4[ibm.Length / 16];
            for (int i = 0; i < s.InvBind.Length; i++)
                s.InvBind[i] = new Matrix4x4(ibm[i * 16], ibm[i * 16 + 1], ibm[i * 16 + 2], ibm[i * 16 + 3],
                    ibm[i * 16 + 4], ibm[i * 16 + 5], ibm[i * 16 + 6], ibm[i * 16 + 7],
                    ibm[i * 16 + 8], ibm[i * 16 + 9], ibm[i * 16 + 10], ibm[i * 16 + 11],
                    ibm[i * 16 + 12], ibm[i * 16 + 13], ibm[i * 16 + 14], ibm[i * 16 + 15]);
        }

        // meshes
        var mn = Vector3.Zero; var mx = Vector3.Zero; bool first = true;
        foreach (var mesh in doc.GetProperty("meshes").EnumerateArray())
            foreach (var pr in mesh.GetProperty("primitives").EnumerateArray())
            {
                var p = new Prim();
                var attrs = pr.GetProperty("attributes");
                var pos = ReadFloats(bin, accs[attrs.GetProperty("POSITION").GetInt32()], views, 3);
                p.Pos = new Vector3[pos.Length / 3];
                for (int i = 0; i < p.Pos.Length; i++)
                {
                    p.Pos[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                    if (first) { mn = mx = p.Pos[i]; first = false; }
                    else { mn = Vector3.Min(mn, p.Pos[i]); mx = Vector3.Max(mx, p.Pos[i]); }
                }
                if (attrs.TryGetProperty("TEXCOORD_0", out var uvA))
                {
                    var uv = ReadFloats(bin, accs[uvA.GetInt32()], views, 2);
                    p.Uv = new Vector2[uv.Length / 2];
                    for (int i = 0; i < p.Uv.Length; i++) p.Uv[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
                }
                if (attrs.TryGetProperty("JOINTS_0", out var jA))
                {
                    var j = ReadFloats(bin, accs[jA.GetInt32()], views, 4);
                    p.Joints = new ushort[j.Length / 4, 4];
                    for (int i = 0; i < j.Length / 4; i++)
                        for (int c = 0; c < 4; c++) p.Joints[i, c] = (ushort)j[i * 4 + c];
                }
                if (attrs.TryGetProperty("WEIGHTS_0", out var wA))
                {
                    var w = ReadFloats(bin, accs[wA.GetInt32()], views, 4);
                    p.Weights = new float[w.Length / 4, 4];
                    for (int i = 0; i < w.Length / 4; i++)
                        for (int c = 0; c < 4; c++) p.Weights[i, c] = w[i * 4 + c];
                }
                if (pr.TryGetProperty("indices", out var iA))
                {
                    var ind = ReadFloats(bin, accs[iA.GetInt32()], views, 1);
                    p.Indices = ind.Select(x => (int)x).ToArray();
                }
                if (pr.TryGetProperty("material", out var mA)) p.Material = mA.GetInt32();
                s.Prims.Add(p);
            }
        s.BoundsMin = mn; s.BoundsMax = mx;

        // texture: first image uri relative to gltf dir
        if (doc.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0)
        {
            var uri = imgs[0].GetProperty("uri").GetString()!;
            if (!uri.StartsWith("data:"))
                s.TexturePng = Path.GetFullPath(Path.Combine(gltfDir, Uri.UnescapeDataString(uri)));
        }

        // animations (may be absent — FFX .dae.phyre exports usually have none)
        if (doc.TryGetProperty("animations", out var anims))
            foreach (var an in anims.EnumerateArray())
            {
                var clip = new AnimClip { Name = an.TryGetProperty("name", out var anm) ? anm.GetString()! : "clip" };
                var samplers = an.GetProperty("samplers");
                foreach (var ch in an.GetProperty("channels").EnumerateArray())
                {
                    var sm = samplers[ch.GetProperty("sampler").GetInt32()];
                    var tgt = ch.GetProperty("target");
                    if (!tgt.TryGetProperty("node", out var nd)) continue;
                    var ac = new AnimChannel
                    {
                        Node = nd.GetInt32(),
                        Path = tgt.GetProperty("path").GetString()!,
                        Times = ReadFloats(bin, accs[sm.GetProperty("input").GetInt32()], views, 1),
                    };
                    int comps = ac.Path == "rotation" ? 4 : 3;
                    ac.Values = ReadFloats(bin, accs[sm.GetProperty("output").GetInt32()], views, comps);
                    clip.Channels.Add(ac);
                    if (ac.Times.Length > 0) clip.Duration = MathF.Max(clip.Duration, ac.Times[^1]);
                }
                s.Animations.Add(clip);
            }
        s.InitPose();
        return s;
    }

    // joint world matrices under the current pose layer (bind pose by default).
    // Parents may appear AFTER children in the node array (glTF has no ordering
    // guarantee — m211 oracle root is node 12) so resolve recursively.
    public Matrix4x4[] JointWorld()
    {
        var world = new Matrix4x4[NodeLocal.Count];
        var done = new bool[NodeLocal.Count];
        Matrix4x4 Get(int i)
        {
            if (done[i]) return world[i];
            var local = Matrix4x4.CreateScale(PoseS[i])
                        * Matrix4x4.CreateFromQuaternion(PoseR[i])
                        * Matrix4x4.CreateTranslation(PoseT[i]);
            world[i] = NodeParent[i] >= 0 && NodeParent[i] < NodeLocal.Count && NodeParent[i] != i
                ? local * Get(NodeParent[i])
                : local;
            done[i] = true;
            return world[i];
        }
        for (int i = 0; i < world.Length; i++) Get(i);
        return world;
    }
}
