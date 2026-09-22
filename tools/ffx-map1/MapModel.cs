// MAP1 MODEL decode — port of the bundle's map-model VIF walker (the second
// variant, with run headers + embedded TEX0_2 — NOT the MM() container).
//
// MODEL entry payload:
//   +0  u16 modelFlags (bit4 = MSCNT split hint)
//   +4  u16 ==1 -> isTranslucent
//   +12 u32 streamLen16 (stream byte bound = 16 * streamLen16)
//   +16 f3 (center-ish), +32 f3 bboxMin/10, +48 f3 bboxMax/10, +60 f32
//   +64 VIF command stream (variable-length commands within the bound)
//
// VIF stream: STCYCL/MSCAL/STMOD/STROW/STMASK/FLUSH(E)/NOP scaffolding;
// DIRECT A+D carries reg 79 (depthWrite). UNPACK V4_32 @addr0 opens a vertex
// run whose first record is a 144B header:
//   +0  u32 triCount -> m = 3*tris verts (sequential triangle soup)
//   +4  u32 base b -> channel addrs: S=b+m, x=b+2m, v=b+3m
//   +16 u32 vertex format selector (w, 0..5)
//   +32 u32 nregs (0..4) -> embedded GS regs @+48 stride16 {lo,hi,reg}:
//       reg 7 = TEX0_2, 9 = CLAMP_2, 67 = ALPHA_2
//   +128..140 tail {0x8000|triCount, primBits, 0x12412412, 4};
//   prim = tail_lo >> 15 & 2047 (GS PRIM value)
// Vertex stride 13 floats: pos[0..2], color[3..6], st[7..8], extra[9..11].
// Channels: V4_8@b -> color[3], V2_16@S -> st[7] (s16/4096),
//           V3_32@x -> pos[0]; addr v -> slot 9 (normals / alt UV by w).
// MSCNT merges the run into a draw call keyed by
// (textureIndex, effectType, gsConfig, isTranslucent, modelFlags).
using System.Buffers.Binary;

namespace FfxMap1;

public sealed class MapModel
{
    public required List<DrawCall> Draws;
    public required float BMinX, BMinY, BMinZ, BMaxX, BMaxY, BMaxZ;
    public required int ModelFlags;
    public required bool IsTranslucent;
    public required bool CullBackface;
    public required int SectionIndex;
    public int PartIndex = -1;
    public int VertexCount => Draws.Sum(d => d.VertexCount);

    public sealed class DrawCall
    {
        public required int VertexCount;
        public required int TextureIndex;      // into MapModelSet.Textures (-1 none)
        public required int EffectType;
        public required bool IsTranslucent;
        public required bool CullBackface;     // GS culling flag from the VIF stream
        public required int ModelFlags;
        public required GsConfig Gs;
        public List<float[]> VertexRuns = new();
    }

    public sealed class GsConfig
    {
        public Gs.Tex0 Tex0 = Gs.DecodeTex0(0, 0);
        public (uint Lo, uint Hi) Clamp;
        public uint Tex1Lo = 96, Tex1Hi;
        public uint AlphaLo = 68, AlphaHi = unchecked((uint)-1);
        public uint TestLo = 327693, TestHi = unchecked((uint)-1);
        public bool DepthWrite = true;
        public bool CullingEnabled = true;
        public int Prim;
        public bool SameAs(GsConfig o) =>
            Tex0 == o.Tex0 && Clamp == o.Clamp &&
            Tex1Lo == o.Tex1Lo && Tex1Hi == o.Tex1Hi &&
            AlphaLo == o.AlphaLo && AlphaHi == o.AlphaHi &&
            TestLo == o.TestLo && TestHi == o.TestHi &&
            DepthWrite == o.DepthWrite && CullingEnabled == o.CullingEnabled &&
            Prim == o.Prim;
    }
}

public sealed class MapModelSet
{
    public required List<MapModel> Models { get; init; }
    // unique (tex0,clamp) pairs -> decode index, first-seen order
    public required List<(Gs.Tex0 Tex0, (uint Lo, uint Hi) Clamp)> Textures { get; init; }
    public List<LevelParts.PartInfo> Parts = new();

    const int UnpackMask = 0x60;
    const int Direct = 0x50, DirectHl = 0x51, Mscnt = 0x17, Mscal = 0x14,
              Stcycl = 0x01, Stmod = 0x05, Strow = 0x30, Stmask = 0x20,
              Nop = 0x00, Flushe = 0x10, Flush = 0x11;

    static int MA(int t)
    {
        int a = t & 3, i = (t >> 2 & 3) + 1;
        return a switch { 2 => i, 1 => 2 * i, 0 => 4 * i, _ => 2 };
    }

    public static MapModelSet Parse(Map1File f)
    {
        var models = new List<MapModel>();
        var texList = new List<(Gs.Tex0, (uint, uint))>();
        var models_out = new MapModelSet { Models = models, Textures = texList };

        var sec = f.WalkSection(f.Slot(0x14));
        if (sec == null) return models_out;

        int curPart = -1;
        var partList = new List<LevelParts.PartInfo>();
        foreach (var e in sec)
        {
            if (e.Type == LevelParts.LevelPart)
            {
                curPart = e.Index;
                partList.Add(LevelParts.ReadPart(f, e));
                continue;
            }
            if (e.Type != LevelParts.Model) continue;
            var m = ParseModel(f, e.PayloadOffset, e.Index, curPart, texList);
            if (m != null) models.Add(m);
        }
        models_out.Parts = partList;
        return models_out;
    }

    static MapModel? ParseModel(Map1File f, long a, int secIdx, int partIdx,
        List<(Gs.Tex0, (uint, uint))> texList)
    {
        int n = f.U16(a);
        bool isTrans = f.U16(a + 4) == 1;
        int streamLen = (int)f.U32(a + 12);
        float bx = f.F32(a + 32) / 10, by = f.F32(a + 36) / 10, bz = f.F32(a + 40) / 10;
        float cx = f.F32(a + 48) / 10, cy = f.F32(a + 52) / 10, cz = f.F32(a + 56) / 10;

        long d = a + 64, u = d + 16L * streamLen;
        long w = d;
        int m = 0;
        float[]? p = null;
        int g = -1;
        var gc = new MapModel.GsConfig();
        int b = -1, S = -1, x = -1, v = -1;
        int wFx = 0;

        var draws = new List<MapModel.DrawCall>();

        float[] ReadRunHeader()
        {
            int triCount = (int)f.U32(w);
            m = 3 * triCount;
            var arr = new float[13 * m];
            b = (int)f.U32(w + 4);
            S = b + m; x = S + m; v = x + m;
            int fmt = (int)f.U32(w + 16);
            if (fmt == 2 || fmt >= 6) throw new InvalidDataException($"vtx fmt {fmt}");
            if (wFx == 6 && fmt != 0) throw new InvalidDataException("fmt!=0 under w=6");
            wFx = fmt;
            int nregs = (int)f.U32(w + 32);
            if (nregs > 0)
            {
                for (int q = 0; q < nregs; q++)
                {
                    long rp = w + 48 + 16L * q;
                    uint lo = f.U32(rp), hi = f.U32(rp + 4), reg = f.U32(rp + 8);
                    if (reg == 0) continue;   // padding record (bundle: if(0!==r))
                    if (reg == 7) gc.Tex0 = Gs.DecodeTex0(lo, hi);
                    else if (reg == 9) gc.Clamp = (lo, hi);
                    else if (reg == 67) { gc.AlphaLo = lo; gc.AlphaHi = hi; }
                    else throw new InvalidDataException($"model gs reg {reg:x2}");
                }
                var key = (gc.Tex0, gc.Clamp);
                g = texList.IndexOf(key);
                if (g < 0) { g = texList.Count; texList.Add(key); }
            }
            // tail validation per the bundle: +128 == 0x8000|triCount,
            // +136 == 0x12412412, +140 == 4; prim bits from +132.
            gc.Prim = (int)((f.U32(w + 132) >> 15) & 2047);
            return arr;
        }

        while (w < u)
        {
            int imm = f.U16(w), num = f.Data[w + 2];
            int op = f.Data[w + 3] & 0x7f;
            bool hasAddr = (imm & 32768) != 0;
            bool noMask = (imm & 16384) == 0;
            int c = imm & 16383;
            w += 4;
            if ((op & UnpackMask) == UnpackMask)
            {
                int fmt = op & 15;
                if (!hasAddr)
                {
                    // S_32 at addr 4: culling flag word
                    gc.CullingEnabled = (f.U32(w) & 32768) != 0;
                    w += (long)num * MA(fmt);
                    continue;
                }
                if (fmt == 12) // V4_32 — run header (addr must be 0)
                {
                    p = ReadRunHeader();
                    w += (long)num * 16;
                }
                else if (fmt == 5) // V2_16 — ST
                {
                    int e2 = c == S ? 7 : (c == v && wFx == 3 ? 9 : 7);
                    if (!noMask) throw new InvalidDataException("V2_16 masked");
                    for (int q = 0; q < num; q++)
                    {
                        p![13 * q + e2] = f.I16(w) / 4096f;
                        p[13 * q + e2 + 1] = f.I16(w + 2) / 4096f;
                        w += 4;
                    }
                }
                else if (fmt == 8) // V3_32 — positions (x) or extra (S/v)
                {
                    int e2 = c == x ? 0 : 9;
                    for (int q = 0; q < num; q++)
                    {
                        p![13 * q + e2] = f.F32(w);
                        p[13 * q + e2 + 1] = f.F32(w + 4);
                        p[13 * q + e2 + 2] = f.F32(w + 8);
                        w += 12;
                    }
                }
                else if (fmt == 14) // V4_8 — colors (b) or extra (v)
                {
                    int e2 = c == b ? 3 : 9;
                    for (int q = 0; q < num; q++)
                    {
                        p![13 * q + e2] = f.Data[w] / 128f;
                        p[13 * q + e2 + 1] = f.Data[w + 1] / 128f;
                        p[13 * q + e2 + 2] = f.Data[w + 2] / 128f;
                        p[13 * q + e2 + 3] = f.Data[w + 3] / 128f;
                        w += 4;
                    }
                }
                else throw new InvalidDataException($"UNPACK fmt {fmt}");
            }
            else if (op is Direct or DirectHl)
            {
                int nreg = (int)(f.U32(w) & 32767);
                w += 16;
                for (int q = 0; q < nreg; q++)
                {
                    uint lo = f.U32(w), hi = f.U32(w + 4);
                    int reg = f.Data[w + 8] & 0x7f;
                    if (reg == 79) gc.DepthWrite = (hi & 1) == 0;
                    else throw new InvalidDataException($"GS reg {reg:x2}");
                    w += 16;
                }
            }
            else if (op == Mscnt)
            {
                if (p == null) throw new InvalidDataException("MSCNT with no run");
                // bundle merges only when imm!=76 || !isTrans || (modelFlags&16)!=0
                int match = -1;
                if (imm != 76 || !isTrans || (n & 16) != 0)
                    match = draws.FindIndex(dc =>
                        dc.TextureIndex == g && dc.EffectType == wFx &&
                        dc.Gs.SameAs(gc) && dc.IsTranslucent == isTrans &&
                        dc.ModelFlags == n);
                if (match >= 0)
                {
                    draws[match].VertexRuns.Add(p);
                    draws[match].VertexCount += m;
                }
                else
                {
                    draws.Add(new MapModel.DrawCall
                    {
                        VertexCount = m,
                        TextureIndex = g, EffectType = wFx,
                        IsTranslucent = isTrans, CullBackface = gc.CullingEnabled, ModelFlags = n, Gs = gc,
                        VertexRuns = { p },
                    });
                    gc = new MapModel.GsConfig();
                }
                m = 0; p = null;
            }
            else if (op == Mscal) wFx = 6 * (imm == 2 ? 1 : 0);
            else if (op is Nop or Flushe or Flush) { }
            else if (op == Stcycl) { }
            else if (op == Stmod) { }
            else if (op == Strow) w += 16;
            else if (op == Stmask) w += 4;
            else throw new InvalidDataException($"VIF op 0x{op:x2} @0x{w:x}");
        }

        if (draws.Count == 0) return null;
        return new MapModel
        {
            Draws = draws,
            BMinX = bx, BMinY = by, BMinZ = bz,
            BMaxX = cx, BMaxY = cy, BMaxZ = cz,
            ModelFlags = n, IsTranslucent = isTrans, CullBackface = gc.CullingEnabled,
            SectionIndex = secIdx,
            PartIndex = partIdx,
        };
    }
}

/// <summary>Vertex-level editing of MODEL payloads — walks the VIF stream
/// recording the file offsets of each UNPACK payload (position runs at
/// addr x, color runs at addr b, UV runs at addr S) so the UI can patch
/// individual vertices/colors in place. The stream layout is
/// self-describing (addr fields in the V4_32 run header), so the same
/// model re-parses identically after a value write.</summary>
public static class MapModelEdit
{
    /// <summary>One editable vertex: flat index across runs, run-local
    /// index, file offset of its first byte.</summary>
    public readonly record struct VertRef(int Run, int Local, long Off,
        float X, float Y, float Z);
    public readonly record struct ColRef(int Run, int Local, long Off,
        byte R, byte G, byte B, byte A);

    const int UnpackMask = 0x60, Direct = 0x50, DirectHl = 0x51,
              Mscnt = 0x17, Mscal = 0x14, Stcycl = 0x01, Stmod = 0x05,
              Strow = 0x30, Stmask = 0x20, Nop = 0x00, Flushe = 0x10, Flush = 0x11;

    static int MA(int t)
    {
        int a = t & 3, i = (t >> 2 & 3) + 1;
        return a switch { 2 => i, 1 => 2 * i, 0 => 4 * i, _ => 2 };
    }

    /// <summary>Position vertices (V3_32 runs at channel x) — flat list
    /// across all runs of the model.</summary>
    public static List<VertRef> PositionVerts(Map1File f, long payloadOff)
    {
        var list = new List<VertRef>();
        Walk(f, payloadOff, (run, local, off, ch) =>
        {
            if (ch == 0)
                list.Add(new VertRef(run, local, off,
                    f.F32(off), f.F32(off + 4), f.F32(off + 8)));
        });
        return list;
    }

    /// <summary>Vertex colors (V4_8 runs at channel b).</summary>
    public static List<ColRef> ColorVerts(Map1File f, long payloadOff)
    {
        var list = new List<ColRef>();
        Walk(f, payloadOff, (run, local, off, ch) =>
        {
            if (ch == 3)
                list.Add(new ColRef(run, local, off,
                    f.Data[off], f.Data[off + 1], f.Data[off + 2], f.Data[off + 3]));
        });
        return list;
    }

    /// <summary>Patch one position vertex in place (f32x3 at ref.Off).</summary>
    public static void WriteVert(byte[] d, VertRef r, float x, float y, float z)
    {
        BitConverter.GetBytes(x).CopyTo(d, (int)r.Off);
        BitConverter.GetBytes(y).CopyTo(d, (int)r.Off + 4);
        BitConverter.GetBytes(z).CopyTo(d, (int)r.Off + 8);
    }

    /// <summary>Patch one vertex color in place (u8x4 at ref.Off).</summary>
    public static void WriteColor(byte[] d, ColRef r, byte[] rgba)
    {
        d[r.Off] = rgba[0]; d[r.Off + 1] = rgba[1];
        d[r.Off + 2] = rgba[2]; d[r.Off + 3] = rgba[3];
    }

    /// <summary>Shared VIF walk emitting (run, localVertex, fileOff,
    /// channel) for every position (ch=0) and color (ch=3) element —
    /// mirrors ParseModel's stream loop minus draw/gs tracking.</summary>
    static void Walk(Map1File f, long payloadOff,
        Action<int, int, long, int> emit)
    {
        int streamLen = (int)f.U32(payloadOff + 12);
        long w = payloadOff + 64, u = w + 16L * streamLen;
        int b = -1, S = -1, x = -1, v = -1, run = -1;
        while (w < u)
        {
            int imm = f.U16(w), num = f.Data[w + 2];
            int op = f.Data[w + 3] & 0x7f;
            bool hasAddr = (imm & 32768) != 0;
            int c = imm & 16383;
            w += 4;
            if ((op & UnpackMask) == UnpackMask)
            {
                int fmt = op & 15;
                if (!hasAddr) { w += (long)num * MA(fmt); continue; }
                if (fmt == 12)
                {
                    // V4_32 run header @w: triCount→m, b/S/x/v addrs
                    run++;
                    int tris = (int)f.U32(w);
                    b = (int)f.U32(w + 4); S = b + 3 * tris;
                    x = S + 3 * tris; v = x + 3 * tris;
                    w += (long)num * 16;
                }
                else if (fmt == 5)
                {
                    for (int q = 0; q < num; q++) { emit(run, q, w, 7); w += 4; }
                }
                else if (fmt == 8)
                {
                    int ch = c == x ? 0 : 9;
                    for (int q = 0; q < num; q++) { emit(run, q, w, ch); w += 12; }
                }
                else if (fmt == 14)
                {
                    int ch = c == b ? 3 : 9;
                    for (int q = 0; q < num; q++) { emit(run, q, w, ch); w += 4; }
                }
                else w += (long)num * MA(fmt);
            }
            else if (op is Direct or DirectHl)
            {
                int nreg = (int)(f.U32(w) & 32767);
                w += 16 + 16L * nreg;
            }
            else if (op is Mscnt or Mscal or Nop or Flushe or Flush
                     or Stcycl or Stmod) { }
            else if (op == Strow) w += 16;
            else if (op == Stmask) w += 4;
            else throw new InvalidDataException($"VIF op 0x{op:x2} @0x{w:x}");
        }
    }
}
