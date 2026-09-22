// GS memory model for FFX MAP1 textures.
// Ported from the FFX noclip bundle (dist-ffxstudio) GS upload/decode path:
// Ak() uploads linear payloads into swizzled 4MB GS memory via the address
// functions AC/AA/AD/AM; AP/AI/AR read texels back through the CLUT.
// See docs/MAP.md + THIRD_PARTY.md for provenance.
using System;
using System.Collections.Generic;

public class Gs
{
    static readonly int[] AS = {0,1,4,5,16,17,20,21,2,3,6,7,18,19,22,23,8,9,12,13,24,25,28,29,10,11,14,15,26,27,30,31};
    static readonly int[] Ax = {0,1,4,5,8,9,12,13,2,3,6,7,10,11,14,15};
    static readonly int[] Av = {0,1,4,5,16,17,20,21,2,3,6,7,18,19,22,23,8,9,12,13,24,25,28,29,10,11,14,15,26,27,30,31};
    static readonly int[] Aw = {
        0,4,16,20,32,36,48,52,2,6,18,22,34,38,50,54,
        8,12,24,28,40,44,56,60,10,14,26,30,42,46,58,62,
        33,37,49,53,1,5,17,21,35,39,51,55,3,7,19,23,
        41,45,57,61,9,13,25,29,43,47,59,63,11,15,27,31,
        96,100,112,116,64,68,80,84,98,102,114,118,66,70,82,86,
        104,108,120,124,72,76,88,92,106,110,122,126,74,78,90,94,
        65,69,81,85,97,101,113,117,67,71,83,87,99,103,115,119,
        73,77,89,93,105,109,121,125,75,79,91,95,107,111,123,127,
        128,132,144,148,160,164,176,180,130,134,146,150,162,166,178,182,
        136,140,152,156,168,172,184,188,138,142,154,158,170,174,186,190,
        161,165,177,181,129,133,145,149,163,167,179,183,131,135,147,151,
        169,173,185,189,137,141,153,157,171,175,187,191,139,143,155,159,
        224,228,240,244,192,196,208,212,226,230,242,246,194,198,210,214,
        232,236,248,252,200,204,216,220,234,238,250,254,202,206,218,222,
        193,197,209,213,225,229,241,245,195,199,211,215,227,231,243,247,
        201,205,217,221,233,237,249,253,203,207,219,223,235,239,251,255};
    static readonly int[] AT = {
        0,8,32,40,64,72,96,104,2,10,34,42,66,74,98,106,
        4,12,36,44,68,76,100,108,6,14,38,46,70,78,102,110,
        16,24,48,56,80,88,112,120,18,26,50,58,82,90,114,122,
        20,28,52,60,84,92,116,124,22,30,54,62,86,94,118,126,
        65,73,97,105,1,9,33,41,67,75,99,107,3,11,35,43,
        69,77,101,109,5,13,37,45,71,79,103,111,7,15,39,47,
        81,89,113,121,17,25,49,57,83,91,115,123,19,27,51,59,
        85,93,117,125,21,29,53,61,87,95,119,127,23,31,55,63,
        192,200,224,232,128,136,160,168,194,202,226,234,130,138,162,170,
        196,204,228,236,132,140,164,172,198,206,230,238,134,142,166,174,
        208,216,240,248,144,152,176,184,210,218,242,250,146,154,178,186,
        212,220,244,252,148,156,180,188,214,222,246,254,150,158,182,190,
        129,137,161,169,193,201,225,233,131,139,163,171,195,203,227,235,
        133,141,165,173,197,205,229,237,135,143,167,175,199,207,231,239,
        145,153,177,185,209,217,241,249,147,155,179,187,211,219,243,251,
        149,157,181,189,213,221,245,253,151,159,183,191,215,223,247,255,
        256,264,288,296,320,328,352,360,258,266,290,298,322,330,354,362,
        260,268,292,300,324,332,356,364,262,270,294,302,326,334,358,366,
        272,280,304,312,336,344,368,376,274,282,306,314,338,346,370,378,
        276,284,308,316,340,348,372,380,278,286,310,318,342,350,374,382,
        321,329,353,361,257,265,289,297,323,331,355,363,259,267,291,299,
        325,333,357,365,261,269,293,301,327,335,359,367,263,271,295,303,
        337,345,369,377,273,281,305,313,339,347,371,379,275,283,307,315,
        341,349,373,381,277,285,309,317,343,351,375,383,279,287,311,319,
        448,456,480,488,384,392,416,424,450,458,482,490,386,394,418,426,
        452,460,484,492,388,396,420,428,454,462,486,494,390,398,422,430,
        464,472,496,504,400,408,432,440,466,474,498,506,402,410,434,442,
        468,476,500,508,404,412,436,444,470,478,502,510,406,414,438,446,
        385,393,417,425,449,457,481,489,387,395,419,427,451,459,483,491,
        389,397,421,429,453,461,485,493,391,399,423,431,455,463,487,495,
        401,409,433,441,465,473,497,505,403,411,435,443,467,475,499,507,
        405,413,437,445,469,477,501,509,407,415,439,447,471,479,503,511};
    static readonly int[] Ay = {0,2,8,10,1,3,9,11,4,6,12,14,5,7,13,15};

    public readonly byte[] Data = new byte[4194304];

    // PSMCT32 address (returns byte offset)
    static int Ac(int e, int t, int a, int i)
    {
        int s = ((i >> 1 & 3) << 4) + Ax[((1 & i) << 3) | (7 & a)];
        int r = 63 & a;
        int inner = ((e >> 5) + (i >> 5) * t + (a >> 6)) << 11;
        int mid = (31 & e) + ((r >> 1) & ~31) + (AS[(((31 & i) >> 3) & 3) << 3 | ((r >> 3) & 7)] << 6);
        return ((inner + mid + s) << 2) & 4194300;
    }

    // PSMCT16 address
    static int AA(int e, int t, int a, int i)
    {
        int s = ((i >> 1 & 3) << 4) + Ax[((1 & i) << 3) | (7 & a)];
        int r = 63 & a;
        int inner = ((e >> 5) + (i >> 6) * t + (a >> 6)) << 11;
        int mid = (31 & e) + ((r >> 1) & ~31) + (AS[((r >> 4) & 3) << 3 | ((63 & i) >> 3) & 7] << 6);
        return (((inner + mid + s) << 2) & 4194300) + ((8 & a) >> 2);
    }

    // PSMT8 address (returns byte offset); GS VRAM is circular — wraps at 4MB
    static int AD(int e, int t, int a, int i)
    {
        int s = Aw[((15 & i) << 4) | (15 & a)];
        int r = 127 & a;
        int base_ = ((e >> 5) + (i >> 6) * (t >> 1) + (a >> 7)) << 13;
        int mid = (31 & e) + ((r >> 2) & ~31) + (Av[(((63 & i) >> 4) & 3) << 3 | ((r >> 4) & 7)] << 8);
        return (base_ + mid + s) & 4194303;
    }

    // Address wrappers for MapTexEdit's packed-32 (file psm 20) mapping:
    // the upload walks CT32 words while PSMT4 decodes nibbles — the two
    // swizzles differ, so file texel bytes aren't row-major like other psms.
    internal static int AddrCt32(int tbp, int tbw, int x, int y) => Ac(tbp, tbw, x, y);
    internal static int AddrPsmt4(int tbp, int tbw, int x, int y) => AM(tbp, tbw, x, y);

    // PSMT4 address (returns nibble offset — /2 byte, &1 picks nibble)
    static int AM(int e, int t, int a, int i)
    {
        int n = AT[((15 & i) << 5) | (31 & a)];
        int r = 127 & a, s = 127 & i;
        int base_ = ((e >> 5) + (i >> 7) * (t >> 1) + (a >> 7)) << 14;
        int mid = (31 & e) + ((r >> 2) & ~31) + (((s >> 6) & 1) << 4) + (Ay[((s >> 4) & 3) << 2 | (r >> 5) & 3] << 9);
        return base_ + mid + n;
    }

    // Ak: upload a linear row-major payload into GS memory.
    public void Upload(int psm, int tbp, int tbw, int x, int y, int w, int h, byte[] data, int dataOff = 0)
    {
        int pos = dataOff;
        for (int yy = y; yy < y + h; yy++)
        for (int xx = x; xx < x + w; xx++)
        {
            if (psm == 0)
            {
                int i = Ac(tbp, tbw, xx, yy);
                Data[i] = data[pos]; Data[i + 1] = data[pos + 1];
                Data[i + 2] = data[pos + 2]; Data[i + 3] = data[pos + 3];
                pos += 4;
            }
            else if (psm == 2)
            {
                int i = AA(tbp, tbw, xx, yy);
                Data[i] = data[pos]; Data[i + 1] = data[pos + 1];
                pos += 2;
            }
            else if (psm == 19)
            {
                Data[AD(tbp, tbw, xx, yy)] = data[pos++];
            }
            else if (psm == 20)
            {
                int i = AM(tbp, tbw, xx, yy);
                int s = (data[pos >> 1] >> ((1 & pos) << 2)) & 15;
                Data[i >> 1] = (byte)((s << ((1 & i) << 2)) | (Data[i >> 1] & (240 >> ((1 & i) << 2))));
                pos++;
            }
            else if (psm == 27)
            {
                Data[Ac(tbp, tbw, xx, yy) + 3] = data[pos++];
            }
            else if (psm == 44)
            {
                int i = Ac(tbp, tbw, xx, yy);
                int keep = Data[i + 3] & 15;
                int n = (1 & pos) != 0 ? 0 : 4;
                Data[i + 3] = (byte)(((data[pos >> 1] << n) & 240) | keep);
                pos++;
            }
            else if (psm == 36)
            {
                // PSMT4HL: index in low nibble of byte+3; source byte packs
                // high nibble first (opposite order of PSMT4HH)
                int i = Ac(tbp, tbw, xx, yy);
                int keep = Data[i + 3] & 240;
                int n = (1 & pos) != 0 ? 0 : 4;
                Data[i + 3] = (byte)(((data[pos >> 1] >> n) & 15) | keep);
                pos++;
            }
            else throw new Exception($"unsupported upload psm {psm}");
        }
    }

    static int Clut(int cbp, int c, Gs gs)
    {
        int dd = (224 & c) >> 4;
        if ((8 & c) != 0) dd++;
        int uu = 7 & c;
        if ((16 & c) != 0) uu += 8;
        return Ac(cbp, 1, uu, dd);
    }

    /// <summary>CLUT word address in GS memory (x,y in the 16x16 palette
    /// grid) — palette blocks overlap in memory, so readers must query the
    /// post-upload GS state, not the file palette bytes.</summary>
    public int ClutAddr(int cbp, int x, int y) => Ac(cbp, 1, x, y);

    /// <summary>Exposed palette lookup for CLUT dumps — same path as Clut().</summary>
    public int PaletteEntry(int cbp, int c)
    {
        int dd = (224 & c) >> 4;
        if ((8 & c) != 0) dd++;
        int uu = 7 & c;
        if ((16 & c) != 0) uu += 8;
        return Ac(cbp, 1, uu, dd);
    }

    public byte Mem(int i) => Data[i];

    // PSMT8 decode (AP): index byte -> CLUT RGBA
    public byte[] DecodePSMT8(int tbp, int tbw, int w, int h, int cbp, int tcc)
    {
        var outp = new byte[w * h * 4];
        int o = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int c = Data[AD(tbp, tbw, x, y)];
            int p = Clut(cbp, c, this);
            int a = tcc == 1 ? Data[p + 3] : 128;
            outp[o] = Data[p]; outp[o + 1] = Data[p + 1]; outp[o + 2] = Data[p + 2];
            outp[o + 3] = (byte)Math.Min(255, 2 * a);
            o += 4;
        }
        return outp;
    }

    // PSMCT32 decode (no palette): direct 4-byte RGBA per texel
    public byte[] DecodePSMT32(int tbp, int tbw, int w, int h)
    {
        var outp = new byte[w * h * 4];
        int o = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = Ac(tbp, tbw, x, y);
            outp[o] = Data[p]; outp[o + 1] = Data[p + 1]; outp[o + 2] = Data[p + 2];
            outp[o + 3] = Data[p + 3];
            o += 4;
        }
        return outp;
    }

    // PSMT8H decode (AR): index in high byte of PSMCT32 word
    public byte[] DecodePSMT8H(int tbp, int tbw, int w, int h, int cbp)
    {
        var outp = new byte[w * h * 4];
        int o = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int c = Data[Ac(tbp, tbw, x, y) + 3];
            int p = Clut(cbp, c, this);
            outp[o] = Data[p]; outp[o + 1] = Data[p + 1]; outp[o + 2] = Data[p + 2];
            // psm27 previews opaque: doubling palette alpha here punched
            // holes in skies (yellow wall showing through) — translucency
            // comes from the draw's ALPHA config, not the CLUT
            outp[o + 3] = 255;
            o += 4;
        }
        return outp;
    }

    // PSMT4 decode (AI): nibble index -> CLUT, csa selects 16-color column
    public byte[] DecodePSMT4(int tbp, int tbw, int w, int h, int cbp, int csa, int tcc)
    {
        var outp = new byte[w * h * 4];
        int o = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int r = AM(tbp, tbw, x, y);
            int dd = (Data[r >> 1] >> ((1 & r) << 2)) & 15;
            int u = ((dd >> 3) & 1) + (14 & csa);
            int p = Ac(cbp, 1, (7 & dd) + ((1 & csa) << 3), u);
            int a = tcc == 1 ? Data[p + 3] : 128;
            outp[o] = Data[p]; outp[o + 1] = Data[p + 1]; outp[o + 2] = Data[p + 2];
            outp[o + 3] = (byte)Math.Min(255, 2 * a);
            o += 4;
        }
        return outp;
    }

    // PSMT4HH decode (AF): index in high nibble of byte+3 of the PSMCT32 word
    public byte[] DecodePSMT4HH(int tbp, int tbw, int w, int h, int cbp, int csa)
    {
        var outp = new byte[w * h * 4];
        int o = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int r = Ac(tbp, tbw, x, y) + 3;
            int dd = (Data[r] >> 4) & 15;
            int u = ((dd >> 3) & 1) + (14 & csa);
            int p = Ac(cbp, 1, (7 & dd) + ((1 & csa) << 3), u);
            outp[o] = Data[p]; outp[o + 1] = Data[p + 1]; outp[o + 2] = Data[p + 2];
            outp[o + 3] = (byte)Math.Min(255, 2 * Data[p + 3]);
            o += 4;
        }
        return outp;
    }

    // PSMT4HL decode (AB): index in low nibble of byte+3 of the PSMCT32 word
    public byte[] DecodePSMT4HL(int tbp, int tbw, int w, int h, int cbp, int csa)
    {
        var outp = new byte[w * h * 4];
        int o = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int r = Ac(tbp, tbw, x, y) + 3;
            int dd = Data[r] & 15;
            int u = ((dd >> 3) & 1) + (14 & csa);
            int p = Ac(cbp, 1, (7 & dd) + ((1 & csa) << 3), u);
            outp[o] = Data[p]; outp[o + 1] = Data[p + 1]; outp[o + 2] = Data[p + 2];
            outp[o + 3] = (byte)Math.Min(255, 2 * Data[p + 3]);
            o += 4;
        }
        return outp;
    }

    // ei5: palette slot -> GS CLUT base address (cbp) by paletteType.
    public static int PaletteCbp(int paletteType, int a)
    {
        int base_ = 0, i2 = 0, r = 4, s = 8;
        switch (paletteType)
        {
            case 2: base_ = 32; i2 = 11520; r = 16; s = 4; break;
            case 3: base_ = 32; i2 = 1536; break;
            case 4: base_ = 16; i2 = 8320; break;
            default: throw new Exception($"bad palette type {paletteType}");
        }
        return a >= base_ ? 11776 + (a - base_) * 4 : i2 + (a >> 2) * 32 + r + (a % 4) * s;
    }

    // TEX0 register decode (Ag)
    public record Tex0(int Tbp0, int Tbw, int Psm, int Tw, int Th, int Tcc,
                       int Tfx, int Cbp, int Cpsm, int Csm, int Csa, int Cld)
    {
        public int Width => 1 << Tw;
        public int Height => 1 << Th;
    }

    public static Tex0 DecodeTex0(uint lo, uint hi) => new(
        (int)(lo & 0x3fff), (int)((lo >> 14) & 0x3f), (int)((lo >> 20) & 0x3f),
        (int)((lo >> 26) & 0xf), (int)(((lo >> 30) & 3) | ((hi & 3) << 2)),
        (int)((hi >> 2) & 1), (int)((hi >> 3) & 3), (int)((hi >> 5) & 0x3fff),
        (int)((hi >> 19) & 0xf), (int)((hi >> 23) & 1), (int)((hi >> 24) & 0x1f),
        (int)((hi >> 29) & 7));

    // Scan a byte range (e.g. a MAP1 pair file) for A+D TEX0 register writes.
    // Records are 16B {lo u32, hi u32, reg u32@+8 (u64 addr field)}; reg
    // 6/7 = TEX0_1/TEX0_2. Reading only the low byte also matched unrelated
    // 16-byte records whose low byte happened to be 6/7 (bad palette keys).
    public static List<Tex0> ScanTex0(byte[] data)
    {
        var seen = new HashSet<(int, int, int, int)>();
        var outp = new List<Tex0>();
        for (int o = 0; o + 16 <= data.Length; o += 16)
        {
            int reg = (int)BitConverter.ToUInt32(data, o + 8);
            if (reg != 6 && reg != 7) continue;
            var tx = DecodeTex0(BitConverter.ToUInt32(data, o), BitConverter.ToUInt32(data, o + 4));
            if (tx.Psm is not (19 or 20 or 27 or 44)) continue;
            if (tx.Tbp0 <= 0 || tx.Tbp0 >= 16384 || tx.Tw is < 3 or > 10 || tx.Th is < 3 or > 10 || tx.Cbp >= 16384)
                continue;
            if (seen.Add((tx.Tbp0, tx.Cbp, tx.Psm, tx.Csa))) outp.Add(tx);
        }
        return outp;
    }
}
