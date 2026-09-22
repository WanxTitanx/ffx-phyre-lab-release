// Map glTF export — MODEL draw calls (13-float vertex runs, sequential
// triangles, no index buffer) + GS-decoded textures into a single .gltf.
// Nodes carry the owning LEVEL_PART's TRS so the scene is placed.
using System.Buffers.Binary;

namespace FfxMap1;

public static class MapGltf
{
    public static void Export(MapModelSet set, Gs gs, string path)
    {
        var bin = new MemoryStream();
        var bw = new BinaryWriter(bin);
        var accessors = new List<object>();
        var views = new List<object>();
        var meshes = new List<object>();
        var nodes = new List<object>();
        var images = new List<object>();
        var textures = new List<object>();
        var materials = new List<object>();
        var samplers = new object[] { new { magFilter = 9729, minFilter = 9729, wrapS = 10497, wrapT = 10497 } };

        int AddView(byte[] data, int target)
        {
            int off = (int)bin.Position;
            bw.Write(data);
            while (bin.Position % 4 != 0) bw.Write((byte)0);
            views.Add(new { buffer = 0, byteOffset = off, byteLength = data.Length, target });
            return views.Count - 1;
        }
        int AddAcc(int view, int compType, int count, string type, float[]? min = null, float[]? max = null)
        {
            object a = min != null
                ? new { bufferView = view, componentType = compType, count, type, min, max }
                : new { bufferView = view, componentType = compType, count, type };
            accessors.Add(a);
            return accessors.Count - 1;
        }

        // decode each unique (tex0,clamp) into an embedded PNG + material
        var matForTex = new Dictionary<int, int>();
        for (int i = 0; i < set.Textures.Count; i++)
        {
            var tx = set.Textures[i].Tex0;
            int w = tx.Width, h = tx.Height;
            byte[] rgba;
            try
            {
                rgba = tx.Psm switch
                {
                    19 => gs.DecodePSMT8(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Tcc),
                    20 => gs.DecodePSMT4(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa, tx.Tcc),
                    27 => gs.DecodePSMT8H(tx.Tbp0, tx.Tbw, w, h, tx.Cbp),
                    0 => gs.DecodePSMT32(tx.Tbp0, tx.Tbw, w, h),
                    44 => gs.DecodePSMT4HH(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa),
                    36 => gs.DecodePSMT4HL(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa),
                    _ => new byte[w * h * 4],
                };
            }
            catch { continue; }
            var argb = new uint[w * h];
            for (int p = 0; p < argb.Length; p++)
                argb[p] = (uint)(rgba[4 * p + 3] << 24 | rgba[4 * p] << 16
                    | rgba[4 * p + 1] << 8 | rgba[4 * p + 2]);
            var pngPath = Path.Combine(Path.GetTempPath(), $"maptex_{tx.Tbp0:x4}_{tx.Cbp:x4}_{tx.Psm}_{tx.Csa}.png");
            Png.Encode(pngPath, argb, w, h);
            images.Add(new { mimeType = "image/png", uri = $"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(pngPath))}" });
            textures.Add(new { source = images.Count - 1, sampler = 0 });
            materials.Add(new
            {
                name = $"tex_{tx.Tbp0:x4}_{tx.Cbp:x4}_p{tx.Psm}",
                pbrMetallicRoughness = new
                {
                    baseColorTexture = new { index = textures.Count - 1 },
                    metallicFactor = 0.0,
                    roughnessFactor = 1.0,
                },
                alphaMode = tx.Tcc == 1 ? "MASK" : "OPAQUE",
                alphaCutoff = 0.5,
                doubleSided = true,
            });
            matForTex[i] = materials.Count - 1;
        }
        // fallback material for untextured draws
        materials.Add(new
        {
            name = "untextured",
            pbrMetallicRoughness = new { metallicFactor = 0.0, roughnessFactor = 1.0 },
            doubleSided = true,
        });
        int defaultMat = materials.Count - 1;

        var rootChildren = new List<int>();

        foreach (var m in set.Models)
        {
            var prims = new List<object>();
            foreach (var dc in m.Draws)
            {
                int nv = dc.VertexCount;
                var posBytes = new byte[nv * 12];
                var colBytes = new byte[nv * 16];
                var uvBytes = new byte[nv * 8];
                var normBytes = new byte[nv * 12];
                bool hasNorm = false, hasUv = false;
                // st comes normalized (s16/4096) — no tex-dim scaling
                var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
                int dst = 0;
                foreach (var run in dc.VertexRuns)
                {
                    int nrv = run.Length / 13;
                    for (int i = 0; i < nrv; i++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float pv = run[13 * i + c];
                            BitConverter.GetBytes(pv).CopyTo(posBytes, 12 * dst + 4 * c);
                            mn[c] = Math.Min(mn[c], pv); mx[c] = Math.Max(mx[c], pv);
                            BitConverter.GetBytes(run[13 * i + 9 + c]).CopyTo(normBytes, 12 * dst + 4 * c);
                            if (run[13 * i + 9 + c] != 0) hasNorm = true;
                        }
                        for (int c = 0; c < 4; c++)
                            BitConverter.GetBytes(run[13 * i + 3 + c]).CopyTo(colBytes, 16 * dst + 4 * c);
                        float su = run[13 * i + 7], sv = run[13 * i + 8];
                        if (su != 0 || sv != 0) hasUv = true;
                        BitConverter.GetBytes(su).CopyTo(uvBytes, 8 * dst);
                        BitConverter.GetBytes(sv).CopyTo(uvBytes, 8 * dst + 4);
                        dst++;
                    }
                }
                int posAcc = AddAcc(AddView(posBytes, 34962), 5126, nv, "VEC3", mn, mx);
                int colAcc = AddAcc(AddView(colBytes, 34962), 5126, nv, "VEC4");
                var attrs = new Dictionary<string, int>
                {
                    ["POSITION"] = posAcc,
                    ["COLOR_0"] = colAcc,
                };
                if (hasUv)
                    attrs["TEXCOORD_0"] = AddAcc(AddView(uvBytes, 34962), 5126, nv, "VEC2");
                if (hasNorm)
                    attrs["NORMAL"] = AddAcc(AddView(normBytes, 34962), 5126, nv, "VEC3");
                prims.Add(new
                {
                    attributes = attrs,
                    material = dc.TextureIndex >= 0 && matForTex.TryGetValue(dc.TextureIndex, out int mi) ? mi : defaultMat,
                    mode = 4,
                });
            }
            if (prims.Count == 0) continue;
            string name = $"model_{m.SectionIndex}";
            meshes.Add(new { name, primitives = prims.ToArray() });

            object node;
            var pi = m.PartIndex >= 0 ? set.Parts.FindIndex(p => p.Index == m.PartIndex) : -1;
            if (pi >= 0)
            {
                var t = set.Parts[pi];
                node = new
                {
                    name,
                    mesh = meshes.Count - 1,
                    translation = new[] { t.Px, t.Py, t.Pz },
                    rotation = EulerToQuat(t.Ex, t.Ey, t.Ez, t.EulerOrder),
                };
            }
            else node = new { name, mesh = meshes.Count - 1 };
            nodes.Add(node);
            rootChildren.Add(nodes.Count - 1);
        }

        nodes.Add(new { name = "map", children = rootChildren.ToArray() });
        var gltf = new
        {
            asset = new { version = "2.0", generator = "ffx-map1 mapgltf" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { nodes.Count - 1 } } },
            nodes = nodes.ToArray(),
            meshes = meshes.ToArray(),
            materials = materials.ToArray(),
            textures = textures.ToArray(),
            images = images.ToArray(),
            samplers,
            accessors = accessors.ToArray(),
            bufferViews = views.ToArray(),
            buffers = new[] { new { byteLength = (int)bin.Length, uri = $"data:application/octet-stream;base64,{Convert.ToBase64String(bin.ToArray())}" } },
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(gltf));
    }

    // LEVEL_PART euler is centidegrees (x,y,z); order 0=XYZ, 5=ZYX per the
    // bundle's euler order table. Returns glTF quaternion [x,y,z,w].
    static float[] EulerToQuat(float ex, float ey, float ez, int order)
    {
        double R(double a) => a * Math.PI / 18000.0;
        double x = R(ex), y = R(ey), z = R(ez);
        var qx = new double[] { Math.Sin(x / 2), 0, 0, Math.Cos(x / 2) };
        var qy = new double[] { 0, Math.Sin(y / 2), 0, Math.Cos(y / 2) };
        var qz = new double[] { 0, 0, Math.Sin(z / 2), Math.Cos(z / 2) };
        double[] Mul(double[] a, double[] b) => new[]
        {
            a[3]*b[0]+a[0]*b[3]+a[1]*b[2]-a[2]*b[1],
            a[3]*b[1]-a[0]*b[2]+a[1]*b[3]+a[2]*b[0],
            a[3]*b[2]+a[0]*b[1]-a[1]*b[0]+a[2]*b[3],
            a[3]*b[3]-a[0]*b[0]-a[1]*b[1]-a[2]*b[2],
        };
        var q = order == 5 ? Mul(Mul(qx, qy), qz) : Mul(Mul(qz, qy), qx);
        return new[] { (float)q[0], (float)q[1], (float)q[2], (float)q[3] };
    }
}
