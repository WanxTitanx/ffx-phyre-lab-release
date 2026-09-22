// Actor — faithful port of the dist-ffxstudio actor-bin parser (ers/err/eri).
// Actor resources arrive as a 5-bin pack resolved by erL; bin 0 is the model:
// parts per bone, draw calls with strip runs, skinning, skeleton, refPoints,
// scales, texture section (palettes + regions + pairs + animated textures)
// and a particle (PPP) section whose offsets we catalog.

using System.Buffers.Binary;

namespace FfxMap1;

public sealed class ActorBin
{
    public required byte[] Data { get; init; }
    public required string Path { get; init; }

    public uint U32(long o) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)o));
    public int I32(long o) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan((int)o));
    public ushort U16(long o) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)o));
    public short I16(long o) => BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan((int)o));
    public float F32(long o) => BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan((int)o));
    public sbyte I8(long o) => (sbyte)Data[o];

    // ---- header slots (absolute u32 offsets) ----
    public uint Version => U32(4);
    public uint ModelTableOff => U32(0x10);
    public uint TexSectionOff => U32(0x18);
    public uint RefPointsOff => U32(0x20);
    public uint RefPointCount => U32(0x24);
    public uint BoneMapBase => U32(0x30);
    public uint BoneMapCount => U32(0x34);
    public uint ScalesOff => U32(0x58);
    public uint ParticleOff => U32(0x60);

    public static ActorBin Load(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 0x70)
            throw new InvalidDataException($"{path}: too small for an actor bin");
        return new ActorBin { Data = d, Path = path };
    }

    public static ActorBin FromBytes(byte[] d)
    {
        if (d.Length < 0x70)
            throw new InvalidDataException("too small for an actor bin");
        return new ActorBin { Data = d, Path = "" };
    }

    // ---- model table @ModelTableOff ----
    public ushort ModelId => U16(ModelTableOff + 4);
    public ushort PartCount => U16(ModelTableOff + 6);
    public ushort SkinningCount => U16(ModelTableOff + 8);
    public ushort BoneCount => U16(ModelTableOff + 10);
    public uint PartsOff => U32(ModelTableOff + 16) + ModelTableOff;
    public uint SkinningOff => U32(ModelTableOff + 20) + ModelTableOff;
    public uint BonesOff => U32(ModelTableOff + 28) + ModelTableOff;
    public int PartStride => ModelId > 2096 && ModelId < 4884 ? 24 : 40;

    // ---- texture section err() @TexSectionOff ----
    public readonly record struct TexPair(int Texture, int Palette, int BlendValue);
    public readonly record struct TexRegion(long Start, int W, int H);
    public readonly record struct TexImage(int W, int H, byte[] Rgba, string Name);

    public sealed class TexSection
    {
        public required List<TexPair> Pairs;
        public required List<TexRegion> Regions;
        public required List<long> PaletteOffs;
        public required List<TexImage> Images;
        public long AnimOff;
    }

    /// <summary>eri() — 8bpp indexed texel -> palette RGBA (GS alpha x2).</summary>
    static byte[] DecodeImage(byte[] data, long palOff, long texStart, int w, int h)
    {
        var px = new byte[w * h * 4];
        int l = 0; long s = texStart;
        for (int i = 0; i < w * h; i++)
        {
            int t = data[s++];
            int a = (224 & t) >> 4;
            if ((8 & t) != 0) a++;
            int c = 7 & t;
            if ((16 & t) != 0) c += 8;
            long r = palOff + 4 * (c + 16 * a);
            px[l] = data[r]; px[l + 1] = data[r + 1]; px[l + 2] = data[r + 2];
            px[l + 3] = (byte)Math.Min(255, 2 * data[r + 3]);
            l += 4;
        }
        return px;
    }

    public TexSection Textures(string name)
    {
        long a = TexSectionOff;
        if (a == 0) return new TexSection { Pairs = new(), Regions = new(), PaletteOffs = new(), Images = new() };
        int n = U16(a + 4), palCnt = U16(a + 8), regCnt = U16(a + 12);
        long pairsOff = U32(a + 20) + a, palOff = U32(a + 24) + a,
             regOff = U32(a + 28) + a, anim = U32(a + 32);
        var pal = new List<long>(); var reg = new List<TexRegion>();
        for (int i = 0; i < palCnt; i++) pal.Add(U32(palOff + 8 * i + 4) + a);
        for (int i = 0; i < regCnt; i++)
            reg.Add(new TexRegion(U32(regOff + 16 * i + 12) + a, U16(regOff + 16 * i + 8), U16(regOff + 16 * i + 10)));
        var pairs = new List<TexPair>(); var imgs = new List<TexImage>();
        for (int i = 0; i < n; i++)
        {
            long f = pairsOff + 16 * i;
            int tr = Data[f], pl = Data[f + 1];
            int w = U16(f + 4), h = U16(f + 6), blend = U16(f + 8);
            pairs.Add(new TexPair(tr, pl, blend));
            imgs.Add(new TexImage(w, h, DecodeImage(Data, pal[pl], reg[tr].Start, w, h), $"{name}_{i}"));
        }
        return new TexSection { Pairs = pairs, Regions = reg, PaletteOffs = pal, Images = imgs, AnimOff = anim == 0 ? 0 : anim + a };
    }

    /// <summary>Re-encodes BGRA pixels into a texture region's 8bpp index
    /// stream, quantizing to the pair's palette (nearest RGBA, same swizzle
    /// as eri()). Indices write into <paramref name="patched"/> — an owned
    /// copy of the actor bin.</summary>
    public static void ReplaceImage(byte[] data, long palOff, long texStart,
        int w, int h, byte[] bgra, byte[] patched)
    {
        var pal = new byte[256 * 4];
        for (int t = 0; t < 256; t++)
        {
            int a = (224 & t) >> 4;
            if ((8 & t) != 0) a++;
            int c = 7 & t;
            if ((16 & t) != 0) c += 8;
            long r = palOff + 4 * (c + 16 * a);
            pal[4 * t] = data[r]; pal[4 * t + 1] = data[r + 1];
            pal[4 * t + 2] = data[r + 2];
            pal[4 * t + 3] = (byte)Math.Min(255, 2 * data[r + 3]);
        }
        for (int i = 0; i < w * h; i++)
        {
            int best = 0, bd = int.MaxValue;
            for (int t = 0; t < 256; t++)
            {
                int dr = pal[4 * t] - bgra[4 * i + 2];
                int dg = pal[4 * t + 1] - bgra[4 * i + 1];
                int db = pal[4 * t + 2] - bgra[4 * i];
                int da = pal[4 * t + 3] - bgra[4 * i + 3];
                int dd = dr * dr + dg * dg + db * db + da * da;
                if (dd < bd) { bd = dd; best = t; if (dd == 0) break; }
            }
            patched[texStart + i] = (byte)best;
        }
    }

    /// <summary>Writes one RGBA into a palette slot (linear CLUT index
    /// 0-255 — NOT the swizzled texel byte). Stored alpha is the raw GS
    /// value (render doubles it).</summary>
    public static void WritePaletteColor(byte[] patched, long palOff,
        int idx, byte r, byte g, byte b, byte a)
    {
        long o = palOff + 4 * idx;
        patched[o] = r; patched[o + 1] = g; patched[o + 2] = b; patched[o + 3] = a;
    }

    /// <summary>Multiplies every palette entry (alpha clamps at 128, the
    /// stored half-alpha ceiling).</summary>
    public static void TintPalette(byte[] patched, long palOff,
        float r, float g, float b, float a)
    {
        for (int i = 0; i < 256; i++)
        {
            long o = palOff + 4 * i;
            patched[o]     = (byte)Math.Clamp((int)(patched[o]     * r), 0, 255);
            patched[o + 1] = (byte)Math.Clamp((int)(patched[o + 1] * g), 0, 255);
            patched[o + 2] = (byte)Math.Clamp((int)(patched[o + 2] * b), 0, 255);
            patched[o + 3] = (byte)Math.Clamp((int)(patched[o + 3] * a), 0, 128);
        }
    }

    // ---- parts / draw calls ----
    public readonly record struct DrawCall(int TexIndex, int VertexCount, long VertexStart, int Effect, bool Runs);
    public readonly record struct Part(int Bone, int BaseVertexCount, int ExtraVertexCount,
        long VertexOff, long ExtraOff, List<DrawCall> DrawCalls, int VertexDataFloats);

    public List<Part> Parts()
    {
        var parts = new List<Part>();
        long m = PartsOff;
        for (int i = 0; i < PartCount; i++, m += PartStride)
        {
            int bone = U16(m), baseVc = U16(m + 4), dc = U16(m + 6);
            long vo = U32(m + 8) + ModelTableOff, eo = U32(m + 12) + ModelTableOff,
                 ho = U32(m + 16) + ModelTableOff;
            int extra = U16(m + 22);
            var calls = new List<DrawCall>();
            for (int k = 0; k < dc; k++, ho += 12)
            {
                int flags = Data[ho], tex = I8(ho + 2);
                int vc = U16(ho + 4); long vs = U32(ho + 8) + ModelTableOff;
                calls.Add(new DrawCall(tex, vc, vs, flags >> 1, (flags & 1) != 0));
            }
            parts.Add(new Part(bone, baseVc, extra, vo, eo, calls, 8 * Math.Max(baseVc, extra)));
        }
        return parts;
    }

    // ---- skinning table ----
    public readonly record struct SkinList(long DataOff, int Count, int Mode, int IndexBase);
    public readonly record struct SkinEntry(int Bone, int Part, int RelBone, bool Longform, List<SkinList> Lists);

    public List<SkinEntry> Skinning()
    {
        var list = new List<SkinEntry>();
        long m = SkinningOff;
        for (int i = 0; i < SkinningCount; i++, m += 12)
        {
            int bone = U16(m), part = U16(m + 2), rel = U16(m + 4);
            bool lf = U16(m + 6) != 0;
            long o = U32(m + 8) + ModelTableOff;
            var lists = new List<SkinList>();
            int l = (int)U32(o);
            lists.Add(new SkinList(o + 4, l, 0, 0));
            o += 4 + 8 * l;
            if (lf)
            {
                int c1 = U16(o);
                lists.Add(new SkinList(o + 2, c1, 1, 0));
                o += 2 + 10 * c1;
                int c2 = U16(o);
                lists.Add(new SkinList(o + 2, c2, 2, 0));
            }
            else
            {
                for (int c = 1; ;)
                {
                    int cnt = U16(o);
                    if (cnt == 0xFFFF)
                    {
                        if (c == 1) { c = 2; o += 4; continue; }
                        break;
                    }
                    lists.Add(new SkinList(o + 4, cnt, c, 256 * U16(o + 2)));
                    o += 4 + 8 * cnt;
                }
            }
            list.Add(new SkinEntry(bone, part, rel, lf, lists));
        }
        return list;
    }

    /// <summary>Skinned copy of a part's vertex pool (noclip actor.ts
    /// applySkinning): per skinning entry, M = invOrtho(boneWorld[rel]) ·
    /// boneWorld[bone]; each list writes (modes 0/1) or perturbs (mode 2)
    /// vertex positions. BASIC: 4×s16 stride, idx in 4th. Longform: 5×s16
    /// stride, scale = data[4]/10000. Shortform: 4×s16, 4th packs idx hi byte
    /// and scale lo byte. The vertex buffer is per part and persists between
    /// calls, so callers must keep the base pool pristine.</summary>
    public float[][] SkinnedPool(int partIndex, List<Part> parts, float[][] basePool,
        IReadOnlyList<float[]> boneWorld)
    {
        var pool = new float[basePool.Length][];
        for (int i = 0; i < basePool.Length; i++) pool[i] = (float[])basePool[i].Clone();
        foreach (var e in Skinning())
        {
            if (e.Part != partIndex) continue;
            if (e.Bone < 0 || e.Bone >= boneWorld.Count) continue;
            if (e.RelBone < 0 || e.RelBone >= boneWorld.Count) continue;
            var m = Mat4Mul(InvertOrtho(boneWorld[e.RelBone]), boneWorld[e.Bone]);
            foreach (var sc in e.Lists)
            {
                for (int k = 0; k < sc.Count; k++)
                {
                    int stride = e.Longform && sc.Mode != 0 ? 5 : 4;
                    long ro = sc.DataOff + 2L * (stride * k);
                    int idx = sc.IndexBase;
                    float scale = 1f;
                    if (sc.Mode == 0)
                        idx += I16(ro + 6);
                    else if (e.Longform)
                    {
                        idx += I16(ro + 6);
                        scale = I16(ro + 8) / 10000f;
                    }
                    else
                    {
                        int extra = U16(ro + 6);
                        idx += (extra & 0xFF00) >> 8;
                        scale = (extra & 0xFF) / 255f;
                    }
                    if (idx < 0 || idx >= pool.Length) continue;
                    var q = pool[idx];
                    float x = I16(ro), y = I16(ro + 2), z = I16(ro + 4);
                    float nx = m[0] * x + m[4] * y + m[8] * z + m[12];
                    float ny = m[1] * x + m[5] * y + m[9] * z + m[13];
                    float nz = m[2] * x + m[6] * y + m[10] * z + m[14];
                    if (sc.Mode == 2) { q[0] += nx * scale; q[1] += ny * scale; q[2] += nz * scale; }
                    else { q[0] = nx * scale; q[1] = ny * scale; q[2] = nz * scale; }
                }
            }
        }
        return pool;
    }

    static float[] InvertOrtho(float[] src)
    {
        var d = new float[16];
        d[0] = src[0]; d[1] = src[4]; d[2] = src[8]; d[3] = 0;
        d[4] = src[1]; d[5] = src[5]; d[6] = src[9]; d[7] = 0;
        d[8] = src[2]; d[9] = src[6]; d[10] = src[10]; d[11] = 0;
        float tx = src[12], ty = src[13], tz = src[14];
        d[12] = -(d[0] * tx + d[4] * ty + d[8] * tz);
        d[13] = -(d[1] * tx + d[5] * ty + d[9] * tz);
        d[14] = -(d[2] * tx + d[6] * ty + d[10] * tz);
        d[15] = 1;
        return d;
    }

    // ---- bones ----
    public readonly record struct Bone(int Parent, float Ex, float Ey, float Ez,
        float Ox, float Oy, float Oz, float Sx, float Sy, float Sz);

    const float EulerScale = 17453292519943296e-20f; // 1.7453292519943296e-6? kept verbatim
    public List<Bone> Bones()
    {
        var list = new List<Bone>();
        long m = BonesOff;
        for (int i = 0; i < BoneCount; i++, m += 20)
        {
            int par = U16(m);
            list.Add(new Bone(par == i ? -1 : par,
                I16(m + 2) * EulerScale, I16(m + 4) * EulerScale, I16(m + 6) * EulerScale,
                I16(m + 8), I16(m + 10), I16(m + 12),
                I16(m + 14) / 4096f, I16(m + 16) / 4096f, I16(m + 18) / 4096f));
        }
        return list;
    }

    // ---- ref points ----
    public readonly record struct RefPoint(int Id, int Flags, int Bone, float X, float Y, float Z);
    public List<RefPoint> RefPoints()
    {
        var list = new List<RefPoint>();
        long m = RefPointsOff;
        for (int i = 0; i < RefPointCount; i++, m += 16)
        {
            int v = U16(m);
            list.Add(new RefPoint(v & 0xFF, v >> 14, U16(m + 2), F32(m + 4), F32(m + 8), F32(m + 12)));
        }
        return list;
    }

    // ---- bone mappings (id -> u16 list) ----
    public Dictionary<int, ushort[]> BoneMappings()
    {
        var map = new Dictionary<int, ushort[]>();
        for (int i = 0; i < BoneMapCount; i++)
        {
            long a = U32(BoneMapBase + 4 * i);
            int key = U16(a), cnt = U16(a + 2);
            var vals = new ushort[cnt];
            for (int k = 0; k < cnt; k++) vals[k] = U16(a + 8 + 2 * k);
            map[key] = vals;
        }
        return map;
    }

    /// <summary>Editable bone-map entries: every u16 slot of every
    /// mapping key → file offset + current value. Bone maps remap
    /// animation channel indices to actor bone indices.</summary>
    public List<(int Key, int Index, long Off, int Value)> BoneMapEntries()
    {
        var list = new List<(int, int, long, int)>();
        for (int i = 0; i < BoneMapCount; i++)
        {
            long a = U32(BoneMapBase + 4 * i);
            int key = U16(a), cnt = U16(a + 2);
            for (int k = 0; k < cnt; k++)
                list.Add((key, k, a + 8 + 2 * k, U16(a + 8 + 2 * k)));
        }
        return list;
    }

    /// <summary>Patch one bone-map slot — bounded write into the
    /// mapping array.</summary>
    public byte[] SetBoneMapValue(long off, int value)
    {
        var d = (byte[])Data.Clone();
        U16W(d, off, (ushort)value);
        return d;
    }
    void U16W(byte[] d, long off, ushort v)
        => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan((int)off), v);
    void U32W(byte[] d, long off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((int)off), v);

    /// <summary>Editable default-anim entries: every u32 anim id of the
    /// four slots (0..3 = the locomotion tiers).</summary>
    public List<(int Slot, int Index, long Off, int AnimId)> DefaultAnimEntries()
    {
        var list = new List<(int, int, long, int)>();
        for (int s = 0; s < 4; s++)
        {
            long t = U32(56 + 8 * s); int cnt = (int)U32(56 + 8 * s + 4);
            for (int i = 0; i < cnt; i++)
                list.Add((s, i, t + 4 * i, (int)U32(t + 4 * i)));
        }
        return list;
    }

    /// <summary>Patch one default-anim slot's anim id.</summary>
    public byte[] SetDefaultAnim(long off, int animId)
    {
        var d = (byte[])Data.Clone();
        U32W(d, off, (uint)animId);
        return d;
    }

    // ---- default animations: 4 slots at 56+8e {u32 off, u32 count} ----
    public List<int[]> DefaultAnimations()
    {
        var list = new List<int[]>();
        for (int s = 0; s < 4; s++)
        {
            long t = U32(56 + 8 * s); int cnt = (int)U32(56 + 8 * s + 4);
            var arr = new int[t == 0 ? 0 : cnt];
            for (int i = 0; i < arr.Length; i++) arr[i] = (int)U32(t + 4 * i);
            list.Add(arr);
        }
        return list;
    }

    // ---- mesh decode: vertexData floats + index strips (verbatim y()/A() from ers) ----

    /// <summary>Per-part vertex pool: base verts (s16 pos) then extra verts
    /// (s16/32767 = normals). Returns (pos f3 list, normal f3 list).</summary>
    public (float[][] Pos, float[][] Norm) VertexPool(Part p)
    {
        var pos = new float[p.BaseVertexCount][];
        var norm = new float[p.ExtraVertexCount][];
        for (int i = 0; i < p.BaseVertexCount; i++)
            pos[i] = new[] { (float)I16(p.VertexOff + 6 * i), (float)I16(p.VertexOff + 6 * i + 2), (float)I16(p.VertexOff + 6 * i + 4) };
        for (int i = 0; i < p.ExtraVertexCount; i++)
            norm[i] = new[] { I16(p.ExtraOff + 6 * i) / 32767f, I16(p.ExtraOff + 6 * i + 2) / 32767f, I16(p.ExtraOff + 6 * i + 4) / 32767f };
        return (pos, norm);
    }

    public readonly record struct MeshVert(int PosIdx, int NormIdx, float U, float V);
    public readonly record struct MeshCall(int TexIndex, List<int> Tris, List<MeshVert> Verts);

    /// <summary>Decode one part's draw calls into triangle lists. Index values
    /// with the 0x8000 bit copy a previous vertex (strip continuation).</summary>
    public List<MeshCall> DecodePart(Part p)
    {
        var tex = Textures("x");
        var calls = new List<MeshCall>();
        foreach (var c in p.DrawCalls)
        {
            var verts = new List<MeshVert>();
            var tris = new List<int>();
            long o = c.VertexStart;
            float su = 16f, sv = 16f; // uv scale = 16*texW/16*texH (tex dims fold into 16 when unpaired)
            if (c.TexIndex >= 0 && c.TexIndex < tex.Pairs.Count)
            {
                var pr = tex.Pairs[c.TexIndex];
                su *= tex.Regions[pr.Texture].W; sv *= tex.Regions[pr.Texture].H;
            }
            if (c.Runs)
            {
                // runs mode: u16@+4 = run-block COUNT. Each block at chain:
                // {u8 stride*16, u8 vertStartOff, u8 runCount, u8 vertTotal,
                // u8 runs[runCount]}; records at block+vertStartOff.
                long e = c.VertexStart;
                for (int blk = 0; blk < c.VertexCount; blk++)
                {
                    int stride = 16 * Data[e];
                    int voff = Data[e + 1];
                    int runCnt = Data[e + 2];
                    int vtot = Data[e + 3];
                    var runs = new int[runCnt];
                    for (int k = 0; k < runCnt; k++) runs[k] = Data[e + 4 + k];
                    long vo = e + voff;
                    int l = 0, runIdx = 0;
                    for (int v = 0; v < vtot; v++, vo += 8)
                    {
                        if (runIdx < runCnt && l == runs[runIdx]) { runIdx++; l = 0; }
                        int a = U16(vo), b = U16(vo + 2);
                        float u = U16(vo + 4) / su, vv = U16(vo + 6) / sv;
                        verts.Add(new MeshVert(a, b, u, vv));
                        if (l >= 2)
                        {
                            // strip: tri (v-1-p, v-2+p, v) where p = l%2 alternates winding
                            tris.Add(verts.Count - 2 - (l % 2));
                            tris.Add(verts.Count - 3 + (l % 2));
                            tris.Add(verts.Count - 1);
                        }
                        l++;
                    }
                    e += stride;
                }
            }
            else
            {
                for (int v = 0; v < c.VertexCount; v++, o += 8)
                {
                    int a = U16(o), b = U16(o + 2);
                    float u = U16(o + 4) / su, vv = U16(o + 6) / sv;
                    verts.Add(new MeshVert(a, b, u, vv));
                    tris.Add(verts.Count - 1);
                }
            }
            calls.Add(new MeshCall(c.TexIndex, tris, verts));
        }
        return calls;
    }

    /// <summary>Resolve a packed index (0x8000|k = copy the index emitted s
    /// verts back). Pos: k%3==0, s=k/3. Norm: k%3==2, s=(k+1)/3.</summary>
    static int ResolveIdx(int raw, int emitted, bool isNorm, List<int> resolved)
    {
        if ((raw & 0x8000) != 0)
        {
            int k = raw & 0x7FFF;
            int s = isNorm ? (k + 1) / 3 : k / 3;
            int src = emitted - s;
            return src >= 0 && src < resolved.Count ? resolved[src] : 0;
        }
        return raw;
    }

    /// <summary>Export whole model to OBJ (v/vn/vt + f, grouped per part/call).
    /// Positions are bind-space s16 integers; normals normalized s16.</summary>
    public void ExportObj(string path)
    {
        var tex = Textures(System.IO.Path.GetFileNameWithoutExtension(Path));
        using var w = new StreamWriter(path);
        w.WriteLine($"# {System.IO.Path.GetFileName(Path)} modelId={ModelId} bones={BoneCount}");
        var parts = Parts();
        int vBase = 1, vtBase = 1, vnBase = 1;
        foreach (var p in parts)
        {
            var (pos, norm) = VertexPool(p);
            var calls = DecodePart(p);
            w.WriteLine($"g part_bone{p.Bone}");
            foreach (var v in pos) w.WriteLine($"v {v[0]} {v[1]} {v[2]}");
            foreach (var v in norm) w.WriteLine($"vn {v[0]:0.#####} {v[1]:0.#####} {v[2]:0.#####}");
            // per call: emit vt + faces; resolved indices map onto pools
            var resolvedPos = new List<int>();
            var resolvedNorm = new List<int>();
            foreach (var call in calls)
            {
                w.WriteLine($"# call tex={call.TexIndex} tris={call.Tris.Count / 3}");
                var localVt = new List<int>();
                foreach (var mv in call.Verts)
                {
                    resolvedPos.Add(ResolveIdx(mv.PosIdx, resolvedPos.Count, false, resolvedPos));
                    resolvedNorm.Add(ResolveIdx(mv.NormIdx, resolvedNorm.Count, true, resolvedNorm));
                    w.WriteLine($"vt {mv.U:0.#####} {1 - mv.V:0.#####}");
                    localVt.Add(vtBase++);
                }
                for (int t = 0; t + 2 < call.Tris.Count; t += 3)
                {
                    int i0 = call.Tris[t], i1 = call.Tris[t + 1], i2 = call.Tris[t + 2];
                    w.WriteLine($"f {vBase + resolvedPos[i0]}/{localVt[i0]}/{vnBase + resolvedNorm[i0]} " +
                                $"{vBase + resolvedPos[i1]}/{localVt[i1]}/{vnBase + resolvedNorm[i1]} " +
                                $"{vBase + resolvedPos[i2]}/{localVt[i2]}/{vnBase + resolvedNorm[i2]}");
                }
            }
            vBase += pos.Length; vnBase += norm.Length;
        }
    }

    // ---- minimal column-major mat4 for bone world transforms ----
    static float[] Mat4Identity() => new float[16] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    static float[] Mat4Mul(float[] a, float[] b)
    {
        var r = new float[16];
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                r[4 * c + row] = a[row] * b[4 * c] + a[4 + row] * b[4 * c + 1] + a[8 + row] * b[4 * c + 2] + a[12 + row] * b[4 * c + 3];
        return r;
    }
    /// <summary>TRS per rr(): scale, euler XYZ (radians), translation.</summary>
    static float[] Mat4TRS(float tx, float ty, float tz, float ex, float ey, float ez, float sx, float sy, float sz)
    {
        // viewer compose: M = S; M=M*Rz; M=M*Ry; M=M*Rx (actor.ts update())
        // R = Rz*Ry*Rx (X applied first), scale on the outermost left
        float cx = MathF.Cos(ex), sxn = MathF.Sin(ex);
        float cy = MathF.Cos(ey), syn = MathF.Sin(ey);
        float cz = MathF.Cos(ez), szn = MathF.Sin(ez);
        float r00 = cy * cz, r01 = syn * sxn * cz - cx * szn, r02 = syn * cx * cz + sxn * szn;
        float r10 = cy * szn, r11 = syn * sxn * szn + cx * cz, r12 = syn * cx * szn - sxn * cz;
        float r20 = -syn, r21 = cy * sxn, r22 = cy * cx;
        var r = Mat4Identity();
        // S*R: scale rows (row i by s_i), not columns
        r[0] = r00 * sx; r[1] = r10 * sy; r[2] = r20 * sz;
        r[4] = r01 * sx; r[5] = r11 * sy; r[6] = r21 * sz;
        r[8] = r02 * sx; r[9] = r12 * sy; r[10] = r22 * sz;
        r[12] = tx; r[13] = ty; r[14] = tz;
        return r;
    }
    static float[] Xform(float[] m, float x, float y, float z) => new[]
    {
        m[0] * x + m[4] * y + m[8] * z + m[12],
        m[1] * x + m[5] * y + m[9] * z + m[13],
        m[2] * x + m[6] * y + m[10] * z + m[14],
    };

    /// <summary>World matrix per bone: world[i] = world[parent] * TRS(bone).</summary>
    public List<float[]> BoneWorld() => BoneWorld(ActorAnim.BindState(this));

    /// <summary>World matrix per bone from a 9-float boneState
    /// {eulerXYZ rad, posXYZ (x scales.offset), scaleXYZ} — mirrors
    /// actor.ts update(): local = S.Rz.Ry.Rx, T = pos*scales.offset,
    /// world = world[parent]*local.</summary>
    public List<float[]> BoneWorld(float[] state)
    {
        float off = GetScales()?.Offset ?? 1;
        var bones = Bones();
        var world = new List<float[]>();
        for (int i = 0; i < bones.Count; i++)
        {
            var local = Mat4TRS(
                state[9 * i + 3] * off, state[9 * i + 4] * off, state[9 * i + 5] * off,
                state[9 * i + 0], state[9 * i + 1], state[9 * i + 2],
                state[9 * i + 6], state[9 * i + 7], state[9 * i + 8]);
            world.Add(bones[i].Parent >= 0 && bones[i].Parent < i ? Mat4Mul(world[bones[i].Parent], local) : local);
        }
        return world;
    }

    /// <summary>Export model to glTF (JSON + base64 buffer): per-part mesh
    /// with POSITION/NORMAL/TEXCOORD_0/indices, texture from pair images.</summary>
    public void ExportGltf(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        var tex = Textures(name);
        var boneWorld = BoneWorld();
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

        // embed each pair image as PNG
        var matForTex = new Dictionary<int, int>();
        for (int i = 0; i < tex.Images.Count; i++)
        {
            var img = tex.Images[i];
            var argb = new uint[img.W * img.H];
            for (int p = 0; p < argb.Length; p++)
                argb[p] = (uint)(img.Rgba[4 * p + 3] << 24 | img.Rgba[4 * p] << 16 | img.Rgba[4 * p + 1] << 8 | img.Rgba[4 * p + 2]);
            var pngPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{name}_{i}.png");
            Png.Encode(pngPath, argb, img.W, img.H);
            var pngB64 = Convert.ToBase64String(File.ReadAllBytes(pngPath));
            images.Add(new { mimeType = "image/png", uri = $"data:image/png;base64,{pngB64}" });
            textures.Add(new { source = i, sampler = 0 });
            materials.Add(new
            {
                name = img.Name,
                pbrMetallicRoughness = new
                {
                    baseColorTexture = new { index = i },
                    metallicFactor = 0.0,
                    roughnessFactor = 1.0,
                },
                doubleSided = true,
            });
            matForTex[i] = i;
        }

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

        var parts = Parts();
        var rootChildren = new List<int>();
        foreach (var p in parts)
        {
            var (pos0, norm0) = VertexPool(p);
            var bm = p.Bone < boneWorld.Count ? boneWorld[p.Bone] : Mat4Identity();
            // world-space positions; normals get rotation only (uniform scale ok)
            var pos = pos0.Select(v => Xform(bm, v[0], v[1], v[2])).ToArray();
            var norm = norm0.Select(v => {
                var t = Xform(bm, v[0], v[1], v[2]);
                float ox = bm[12], oy = bm[13], oz = bm[14];
                return new[] { t[0] - ox, t[1] - oy, t[2] - oz };
            }).ToArray();
            var posBytes = new byte[pos.Length * 12];
            var normBytes = new byte[norm.Length * 12];
            for (int i = 0; i < pos.Length; i++)
            {
                BitConverter.GetBytes(pos[i][0]).CopyTo(posBytes, 12 * i);
                BitConverter.GetBytes(pos[i][1]).CopyTo(posBytes, 12 * i + 4);
                BitConverter.GetBytes(pos[i][2]).CopyTo(posBytes, 12 * i + 8);
            }
            for (int i = 0; i < norm.Length; i++)
            {
                BitConverter.GetBytes(norm[i][0]).CopyTo(normBytes, 12 * i);
                BitConverter.GetBytes(norm[i][1]).CopyTo(normBytes, 12 * i + 4);
                BitConverter.GetBytes(norm[i][2]).CopyTo(normBytes, 12 * i + 8);
            }
            var mn = new[] { pos.Min(v => v[0]), pos.Min(v => v[1]), pos.Min(v => v[2]) };
            var mx = new[] { pos.Max(v => v[0]), pos.Max(v => v[1]), pos.Max(v => v[2]) };
            int posAcc = AddAcc(AddView(posBytes, 34962), 5126, pos.Length, "VEC3", mn, mx);
            int normAcc = AddAcc(AddView(normBytes, 34962), 5126, norm.Length, "VEC3");

            var calls = DecodePart(p);
            var prims = new List<object>();
            foreach (var call in calls)
            {
                var resolvedPos = new List<int>();
                var resolvedNorm = new List<int>();
                var uvBytes = new byte[call.Verts.Count * 8];
                for (int i = 0; i < call.Verts.Count; i++)
                {
                    var mv = call.Verts[i];
                    resolvedPos.Add(ResolveIdx(mv.PosIdx, i, false, resolvedPos));
                    resolvedNorm.Add(ResolveIdx(mv.NormIdx, i, true, resolvedNorm));
                    BitConverter.GetBytes(mv.U).CopyTo(uvBytes, 8 * i);
                    BitConverter.GetBytes(1f - mv.V).CopyTo(uvBytes, 8 * i + 4);
                }
                // glTF needs real attribute buffers; duplicate verts per call
                var pBytes = new byte[call.Verts.Count * 12];
                var nBytes = new byte[call.Verts.Count * 12];
                for (int i = 0; i < call.Verts.Count; i++)
                {
                    var pv = pos[resolvedPos[i]];
                    var nv = norm[Math.Min(resolvedNorm[i], norm.Length - 1)];
                    BitConverter.GetBytes(pv[0]).CopyTo(pBytes, 12 * i);
                    BitConverter.GetBytes(pv[1]).CopyTo(pBytes, 12 * i + 4);
                    BitConverter.GetBytes(pv[2]).CopyTo(pBytes, 12 * i + 8);
                    BitConverter.GetBytes(nv[0]).CopyTo(nBytes, 12 * i);
                    BitConverter.GetBytes(nv[1]).CopyTo(nBytes, 12 * i + 4);
                    BitConverter.GetBytes(nv[2]).CopyTo(nBytes, 12 * i + 8);
                }
                var mn2 = new[] { call.Verts.Select((v, i) => pos[resolvedPos[i]][0]).Min(),
                                  call.Verts.Select((v, i) => pos[resolvedPos[i]][1]).Min(),
                                  call.Verts.Select((v, i) => pos[resolvedPos[i]][2]).Min() };
                var mx2 = new[] { call.Verts.Select((v, i) => pos[resolvedPos[i]][0]).Max(),
                                  call.Verts.Select((v, i) => pos[resolvedPos[i]][1]).Max(),
                                  call.Verts.Select((v, i) => pos[resolvedPos[i]][2]).Max() };
                int pa = AddAcc(AddView(pBytes, 34962), 5126, call.Verts.Count, "VEC3", mn2, mx2);
                int na = AddAcc(AddView(nBytes, 34962), 5126, call.Verts.Count, "VEC3");
                int ta = AddAcc(AddView(uvBytes, 34962), 5126, call.Verts.Count, "VEC2");
                var idxBytes = new byte[call.Tris.Count * 2];
                for (int i = 0; i < call.Tris.Count; i++)
                    BitConverter.GetBytes((ushort)call.Tris[i]).CopyTo(idxBytes, 2 * i);
                int ia = AddAcc(AddView(idxBytes, 34963), 5123, call.Tris.Count, "SCALAR");
                prims.Add(new
                {
                    attributes = new { POSITION = pa, NORMAL = na, TEXCOORD_0 = ta },
                    indices = ia,
                    material = call.TexIndex >= 0 && matForTex.ContainsKey(call.TexIndex) ? matForTex[call.TexIndex] : 0,
                    mode = 4,
                });
            }
            meshes.Add(new { name = $"part_bone{p.Bone}", primitives = prims.ToArray() });
            nodes.Add(new { name = $"bone{p.Bone}_mesh", mesh = meshes.Count - 1 });
            rootChildren.Add(nodes.Count - 1);
        }

        nodes.Add(new { name = name, children = rootChildren.ToArray() });
        var gltf = new
        {
            asset = new { version = "2.0", generator = "ffx-map1 actorgltf" },
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
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(gltf,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
    }

    // ---- scales block (needs Version >= 9) ----
    public readonly record struct Scales(float ShadowRadius, float Base, float CollisionRadius,
        float Height, float Actor, float Offset, float EnvMap, float Deflection,
        float SpecR, float SpecG, float SpecB, float SpecA);

    public Scales? GetScales()
    {
        if (Version < 9 || ScalesOff == 0) return null;
        long u = ScalesOff;
        int e = (int)U32(u);
        return new Scales(F32(u + 4), F32(u + 12), F32(u + 16), F32(u + 20),
            F32(u + 28), F32(u + 32),
            e > 5667 ? F32(u + 56) : 1, e > 5667 ? F32(u + 60) : 2,
            e > 5667 ? Data[u + 64] / 128f : 1, e > 5667 ? Data[u + 65] / 128f : 1,
            e > 5667 ? Data[u + 66] / 128f : 1, e > 5667 ? Data[u + 67] / 127f : 1);
    }
}
