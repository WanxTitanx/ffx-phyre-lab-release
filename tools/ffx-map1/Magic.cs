// FFX magic bin (11/<id>.bin) structural readout.
// Port of the static parts of noclip_reference/magic.ts: the funcList table
// (handler address -> opcode), validFuncList, the funcMap scan and the opcode
// names. The MIPS interpreters (HeaderFinder/GraphFinder/Annotator) are NOT
// ported, so headers/particleIndex stay out of scope here.
using System;
using System.Collections.Generic;
using System.Text;

public static class MagicBin
{
    /// <summary>Base address the magic file is loaded at (magic.ts LOAD_ADDRESS).</summary>
    public const uint LoadAddress = 0x1B00000;

    /// <summary>Handler address -> opcode (magic.ts funcList).</summary>
    static readonly Dictionary<uint, int> FuncList = new()
    {
        { 0x268070, 0x0 },
        { 0x2681E8, 0x1 },
        { 0x268360, 0x2 },
        { 0x2684D8, 0x3 },
        { 0x267FE8, 0x4 },
        { 0x268160, 0x5 },
        { 0x2682D8, 0x6 },
        { 0x268450, 0x7 },
        { 0x267F80, 0x8 },
        { 0x2680F8, 0x9 },
        { 0x268270, 0xA },
        { 0x2683E8, 0xB },
        { 0x26BDD0, 0xC },
        { 0x26C1B0, 0xD },
        { 0x26CE38, 0xE },
        { 0x269A40, 0x10 },
        { 0x26A030, 0x11 },
        { 0x26A0B8, 0x12 },
        { 0x26A3E8, 0x13 },
        { 0x26A400, 0x14 },
        { 0x26A6F0, 0x15 },
        { 0x26A848, 0x16 },
        { 0x26A9B8, 0x17 },
        { 0x26AD78, 0x18 },
        { 0x285C58, 0x19 },
        { 0x2863C8, 0x1A },
        { 0x268778, 0x1B },
        { 0x268A78, 0x1C },
        { 0x26BF08, 0x1D },
        { 0x26C050, 0x1E },
        { 0x282648, 0x1F },
        { 0x2887C8, 0x20 },
        { 0x2889A0, 0x21 },
        { 0x288B50, 0x22 },
        { 0x288D00, 0x23 },
        { 0x288E90, 0x24 },
        { 0x26D128, 0x25 },
        { 0x26CC90, 0x26 },
        { 0x26CFB0, 0x27 },
        { 0x26C438, 0x28 },
        { 0x26AED0, 0x29 },
        { 0x283038, 0x2A },
        { 0x282EE0, 0x2B },
        { 0x28AAA8, 0x2C },
        { 0x288488, 0x2D },
        { 0x286B50, 0x2E },
        { 0x289C88, 0x2F },
        { 0x26A658, 0x30 },
        { 0x26E8F0, 0x31 },
        { 0x283978, 0x32 },
        { 0x26FD70, 0x33 },
        { 0x28AB18, 0x34 },
        { 0x28A0A8, 0x35 },
        { 0x28A258, 0x36 },
        { 0x28AEA0, 0x37 },
        { 0x281060, 0x38 },
        { 0x28A470, 0x39 },
        { 0x26A108, 0x3A },
        { 0x280EE8, 0x3B },
        { 0x281670, 0x3C },
        { 0x280878, 0x3D },
        { 0x281F98, 0x3E },
        { 0x281DA8, 0x3F },
        { 0x26D790, 0x40 },
        { 0x281100, 0x41 },
        { 0x26D2B8, 0x42 },
        { 0x282A38, 0x43 },
        { 0x281160, 0x44 },
        { 0x281200, 0x45 },
        { 0x281980, 0x46 },
        { 0x28B230, 0x47 },
        { 0x26DC10, 0x48 },
        { 0x2810E0, 0x49 },
        { 0x281080, 0x4A },
        { 0x281220, 0x4B },
        { 0x2811C0, 0x4C },
        { 0x28B490, 0x4D },
        { 0x28BEF8, 0x4E },
        { 0x28B7E8, 0x4F },
        { 0x28B9F8, 0x50 },
        { 0x28C750, 0x51 },
        { 0x26DCF8, 0x52 },
        { 0x269AE8, 0x53 },
        { 0x28C998, 0x54 },
        { 0x26C9C8, 0x55 },
        { 0x268EC0, 0x56 },
        { 0x26B1C8, 0x57 },
        { 0x270940, 0x58 },
        { 0x28B050, 0x59 },
        { 0x2810C0, 0x5A },
        { 0x281000, 0x5B },
        { 0x289EF8, 0x5C },
        { 0x289F60, 0x5D },
        { 0x28BC80, 0x5E },
        { 0x26B390, 0x5F },
        { 0x26DAD0, 0x60 },
        { 0x272030, 0x61 },
        { 0x268998, 0x62 },
        { 0x28D120, 0x63 },
        { 0x275520, 0x64 },
        { 0x271A88, 0x65 },
        { 0x272F68, 0x66 },
        { 0x26CB28, 0x67 },
        { 0x2767C8, 0x68 },
        { 0x2763C8, 0x69 },
        { 0x2764E8, 0x6A },
        { 0x276608, 0x6B },
        { 0x269F80, 0x6C },
        { 0x2766B0, 0x6D },
        { 0x276000, 0x6E },
        { 0x28EE88, 0x6F },
        { 0x272848, 0x70 },
        { 0x276AB8, 0x71 },
        { 0x268560, 0x72 },
        { 0x269B90, 0x73 },
        { 0x268D40, 0x74 },
        { 0x2768A0, 0x75 },
        { 0x277070, 0x76 },
        { 0x277948, 0x77 },
        { 0x279000, 0x78 },
        { 0x2793E8, 0x79 },
        { 0x27ABD0, 0x7A },
        { 0x277D18, 0x7B },
        { 0x28D8C0, 0x7C },
        { 0x28EDB8, 0x7E },
        { 0x274300, 0x7F },
        { 0x2811A0, 0x80 },
        { 0x275650, 0x81 },
        { 0x275AD0, 0x82 },
        { 0x2799F0, 0x83 },
        { 0x28A4D0, 0x84 },
        { 0x27AF88, 0x85 },
        { 0x275880, 0x86 },
        { 0x275D28, 0x87 },
        { 0x281120, 0x88 },
        { 0x269CE0, 0x89 },
        { 0x27C108, 0x8A },
        { 0x26A490, 0x8B },
        { 0x288FA0, 0x8C },
        { 0x289578, 0x8D },
        { 0x289AB8, 0x8E },
        { 0x289718, 0x8F },
        { 0x2898D8, 0x90 },
        { 0x289D30, 0x1000 },
        { 0x281240, 0x1001 },
        { 0x267F20, 0x1002 },
        { 0x28A598, 0x1003 },
        { 0x26AFD0, 0x1004 },
        { 0x28F0F0, 0x1005 },
        { 0x269C38, 0x10 },
        { 0x28C210, 0x1007 },
        { 0x2811E0, 0x1008 },
        { 0x26C2F0, 0x1009 },
        { 0x28ABD0, 0x100A },
        { 0x28AD98, 0x100B },
        { 0x2876D0, 0x100C },
        { 0x269D88, 0x100D },
        { 0x28E468, 0x100E },
        { 0x28EC68, 0x100F },
        { 0x28C440, 0x1010 },
        { 0x281260, 0x1011 },
        { 0x283498, 0x1012 },
        { 0x2836B0, 0x1013 },
        { 0x290358, 0x1014 },
    };

    /// <summary>Opcode names in magic.ts enum Opcode order (0x0..0x31).</summary>
    static readonly string[] OpNames =
    {
        "WAIT",
        "JUMP",
        "JUMP_LABEL",
        "LOOP",
        "CONTINUE",
        "CALL",
        "RETURN",
        "FUNC",
        "FUNC_YIELD",
        "TICK",
        "SET_LABEL",
        "MARK",
        "END",
        "ACTOR",
        "SELF",
        "PREP_TEX",
        "LOAD_TEX",
        "FREE_EMITTER",
        "TOGGLE_EMITTER",
        "UNK_13",
        "UNK_TARGET_14",
        "UNK_ALL_15",
        "UNK_16",
        "SWITCH_CAM",
        "CAM_ZOOM",
        "CAM_POS",
        "CAM_FOCUS",
        "CAM_ROLL",
        "CAM_SUB",
        "CAM_SUB_VEC",
        "CAM_CURVE_0",
        "CAM_CURVE_1",
        "COLOR",
        "ALPHA",
        "UNK_22",
        "SOUND",
        "SOUND_24",
        "UNK_25",
        "BLUR",
        "UNK_27",
        "UNK_CAM_28",
        "UNK_29",
        "UNK_2A",
        "UNK_2B",
        "UNK_2C",
        "UNK_2D",
        "UNK_2E",
        "UNK_2F",
        "UNK_30",
        "UNK_31",
        "UNK_RES_32",
    };

    public static string OpcodeName(int op)
    {
        if (op >= 0 && op < OpNames.Length) return OpNames[op];
        return op >= 0x1000 ? "EXT_0x" + op.ToString("X") : "OP_0x" + op.ToString("X");
    }

    /// <summary>magic.ts validFuncList: a 10-u32 window of plausible handler
    /// addresses (an all-zero window recurses +40 while before 0x100).</summary>
    public static bool ValidFuncList(byte[] d, int offs)
    {
        bool allZero = true, bad = true;
        for (int i = 0; i < 10; i++)
        {
            int p = offs + 4 * i;
            if (p + 4 > d.Length) return false;
            uint val = BitConverter.ToUInt32(d, p);
            if (val > 0)
            {
                allZero = false;
                if ((i < 4 && val < 0x280000) || (i == 7 && val > 0x280000)) bad = false;
                if (val < 0x260000 || val > 0x291000) { bad = true; break; }
            }
        }
        if (!bad) return true;
        if (allZero && offs < 0x100) return ValidFuncList(d, offs + 40);
        return false;
    }

    /// <summary>First offset in 0x40..0x130 that looks like the func list.</summary>
    public static int FindFuncOffset(byte[] d)
    {
        for (int offs = 0x40; offs <= 0x130; offs += 0x10)
            if (ValidFuncList(d, offs)) return offs;
        return -1;
    }

    /// <summary>magic.ts funcMap scan: per 40-byte block take the first non-zero
    /// u32 below LOAD_ADDRESS; unknown handler -> end; three 0xF (NOOP) in a row -> end.</summary>
    public static List<int> BuildFuncMap(byte[] d, int funcOffset)
    {
        var map = new List<int>();
        if (funcOffset < 0) return map;
        int zeros = 0; bool foundEnd = false;
        while (!foundEnd && map.Count < 4096)
        {
            int nextOp = 0xF;
            int block = funcOffset + 40 * map.Count;
            for (int i = 0; i < 40; i += 4)
            {
                int p = block + i;
                if (p + 4 > d.Length) { foundEnd = true; break; }
                uint maybe = BitConverter.ToUInt32(d, p);
                if (maybe != 0 && maybe < LoadAddress)
                {
                    if (FuncList.TryGetValue(maybe, out var op)) nextOp = op;
                    else foundEnd = true;
                    break;
                }
            }
            if (nextOp == 0xF) { zeros++; if (zeros > 2) foundEnd = true; }
            map.Add(nextOp);
        }
        return map;
    }

    public sealed record Layout(int FuncOffset, List<int> FuncMap, uint[] EntryPointers);

    // game-exe callees (magic.ts KnownFunc)
    const uint FnFixParticlePointers = 0x2b1a58;
    const uint FnAltHeader = 0x19f490;
    const uint FnGetParticleData = 0x2b1a78;
    const uint FnFixMagicPointers = 0x264c28;
    const uint FnInitParticleHeap = 0x264db0;

    /// <summary>Mini MIPS register tracker — linear scan over a byte range
    /// tracking LUI/ORI/ADDIU/ADDU/DADDU/SUBU/ANDI/LW/SW. JAL to watched
    /// targets executes the delay slot first (args are commonly set there)
    /// then invokes the callback. No branch following — a reduced port of
    /// magic.ts NaiveInterpreter sufficient for header/index discovery.</summary>
    sealed class RegTracker
    {
        readonly byte[] d;
        readonly uint[] val = new uint[32];
        readonly bool[] lw = new bool[32];
        readonly bool[] known = new bool[32];
        readonly Dictionary<uint, uint> stores = new();
        public readonly List<int> Headers = new();
        public readonly List<int> Indices = new();
        /// <summary>When set, `jal &lt;this&gt;` is treated as a magicHeader call:
        /// the header struct is read back via u32(a0 + 0x2C) (Annotator).</summary>
        public uint MagicHeaderFunc;
        /// <summary>HeaderFinder stops on J; windowed arg-tracking does not
        /// (no function-boundary knowledge — linear approximation).</summary>
        public bool StopOnJ = true;
        public RegTracker(byte[] d) { this.d = d; known[0] = true; } // r0 is always 0
        void Set(int r, uint v, bool isLw) { if (r != 0) { val[r] = v; lw[r] = isLw; known[r] = true; } }

        // 0 = normal, 1 = skip next instr (delay slot consumed), 2 = J (stop after delay)
        public int Exec(uint ins, long at)
        {
            uint op = ins >> 26;
            known[0] = true; // r0 is architecturally 0 — writes to it must
            // never mark it unknown (else branches below would clobber it)
            int rs = (int)(ins >> 21) & 31, rt = (int)(ins >> 16) & 31, rd = (int)(ins >> 11) & 31;
            uint uimm = ins & 0xFFFF;
            int simm = (short)uimm;
            switch (op)
            {
                case 0x00: // SPECIAL
                    switch (ins & 0x3F)
                    {
                        case 0x21: case 0x2C: case 0x2D: // ADDU/DADD/DADDU
                        case 0x23: // SUBU
                            if (known[rs] && known[rt])
                                Set(rd, (ins & 0x3F) == 0x23 ? val[rs] - val[rt] : val[rs] + val[rt], false);
                            else known[rd] = false;
                            break;
                        case 0x25: // OR
                            if (known[rs] && known[rt]) Set(rd, val[rs] | val[rt], false);
                            else known[rd] = false;
                            break;
                        case 0x00: case 0x02: case 0x03: break; // SLL/SRL/SRA — shift tracking skipped
                        default: known[rd] = false; break;
                    }
                    break;
                case 0x0F: Set(rt, uimm << 16, false); break; // LUI
                case 0x09: case 0x08: // ADDIU/ADDI
                    if (known[rs]) Set(rt, (uint)(val[rs] + simm), false);
                    else known[rt] = false;
                    break;
                case 0x0D: // ORI
                    if (known[rs]) Set(rt, val[rs] | uimm, false);
                    else known[rt] = false;
                    break;
                case 0x0C: // ANDI
                    if (known[rs]) Set(rt, val[rs] & uimm, false);
                    else known[rt] = false;
                    break;
                case 0x23: // LW
                    if (known[rs])
                    {
                        uint addr = (uint)(val[rs] + simm);
                        long fo = (long)addr - LoadAddress;
                        Set(rt, (fo >= 0 && fo + 4 <= d.Length) ? BitConverter.ToUInt32(d, (int)fo) : addr, true);
                        if (!(fo >= 0 && fo + 4 <= d.Length)) { val[rt] = addr; lw[rt] = true; }
                    }
                    else known[rt] = false;
                    break;
                case 0x2B: // SW — record pointer stores for LW-backtracking
                    if (known[rs] && known[rt])
                    {
                        uint addr = (uint)(val[rs] + simm);
                        if (val[rt] >= LoadAddress && val[rt] < LoadAddress + 0x300000)
                            stores[addr] = val[rt];
                    }
                    break;
                case 0x03: // JAL — args are often set in the delay slot, so
                    {      // execute it before reading a0/a2
                        uint target = (ins & 0x3FFFFFF) << 2;
                        if (target == FnFixParticlePointers || target == FnAltHeader
                            || target == FnGetParticleData || target == MagicHeaderFunc)
                        {
                            if (Environment.GetEnvironmentVariable("FFX_MAGIC_DEBUG") != null)
                                Console.Error.WriteLine($"    jal 0x{target:x} @0x{at:x} delay=0x{BitConverter.ToUInt32(d, (int)at + 4):x8} preKnown6={known[6]} v6=0x{val[6]:x}");
                            if (at + 8 <= d.Length)
                                Exec(BitConverter.ToUInt32(d, (int)at + 4), at + 4);
                            if (Environment.GetEnvironmentVariable("FFX_MAGIC_DEBUG") != null)
                                Console.Error.WriteLine($"      postKnown6={known[6]} v6=0x{val[6]:x}");
                            if (target == FnAltHeader && known[6])
                                Headers.Add((int)(val[6] + 0x10 - LoadAddress));
                            else if (target == MagicHeaderFunc && target != 0 && known[4])
                            {
                                long ho = (long)val[4] + 0x2C - LoadAddress;
                                if (ho >= 0 && ho + 4 <= d.Length)
                                    Headers.Add((int)BitConverter.ToUInt32(d, (int)ho));
                            }
                            else if (target == FnGetParticleData && known[6]
                                && known[5] && val[5] == 8) // Annotator: a1==8 gates magicIndex
                                Indices.Add((int)val[6]);
                            else if (target == FnFixParticlePointers && known[4])
                            {
                                if (lw[4])
                                {
                                    if (stores.TryGetValue(val[4], out uint st))
                                        Headers.Add((int)(st - LoadAddress));
                                    else
                                    {
                                        long fo = (long)val[4] - LoadAddress;
                                        uint actual = fo >= 0 && fo + 4 <= d.Length
                                            ? BitConverter.ToUInt32(d, (int)fo) - LoadAddress : 0;
                                        Headers.Add(actual > 0 && actual < d.Length ? (int)actual : (int)val[4]);
                                    }
                                }
                                else Headers.Add((int)(val[4] - LoadAddress));
                            }
                            return 1; // JAL's delay slot already consumed
                        }
                    }
                    break;
                case 0x02: return StopOnJ ? 2 : 0; // J
            }
            return 0;
        }

        /// <summary>Linear interpretation over [start, end) with J-stop
        /// semantics (delay slot executes, then done).</summary>
        public void Run(long start, long end, int maxSteps = 0x8000)
        {
            int delay = 0;
            for (int steps = 0; steps < maxSteps && start + 4 <= end; steps++, start += 4)
            {
                int r = Exec(BitConverter.ToUInt32(d, (int)start), start);
                if (r == 1) start += 4;
                else if (r == 2) { if (delay++ > 0) break; }
                else if (delay > 0) break;
            }
        }
    }

    /// <summary>Linear MIPS scan of the init function (entry pointer 0):
    /// records the a0 argument at every `jal fixParticlePointers` (and a2 at
    /// the alternate 0x19f490 path). Reduced port of magic.ts HeaderFinder —
    /// no branch following. Returns file offsets.</summary>
    public static List<int> FindHeaders(byte[] d)
    {
        var t = new RegTracker(d);
        if (d.Length < 4) return t.Headers;
        uint init = BitConverter.ToUInt32(d, 0);
        long pc = (long)init - LoadAddress;
        if (pc < 0 || pc + 4 > d.Length) return t.Headers;
        t.Run(pc, d.Length);
        return t.Headers;
    }

    /// <summary>Candidate particleIndex values: scans the whole bin for
    /// `jal getParticleData` sites and resolves a2 with a short windowed
    /// register track (Annotator.magicIndex in magic.ts).</summary>
    public static List<int> FindParticleIndices(byte[] d)
    {
        var found = new List<int>();
        for (int off = 0; off + 8 <= d.Length; off += 4)
        {
            uint ins = BitConverter.ToUInt32(d, off);
            if (ins >> 26 != 3 || (ins & 0x3FFFFFF) << 2 != FnGetParticleData) continue;
            var t = new RegTracker(d) { StopOnJ = false };
            t.Run(Math.Max(0, off - 0x200), off + 8, 0x400);
            if (Environment.GetEnvironmentVariable("FFX_MAGIC_DEBUG") != null)
                Console.Error.WriteLine($"  getPD site @0x{off:x}: indices=[{string.Join(",", t.Indices)}]");
            found.AddRange(t.Indices);
        }
        return found;
    }

    /// <summary>magicHeader is not a fixed address — magic.ts finds it by
    /// pattern: the function that calls both fixMagicPointers (0x264c28) and
    /// initParticleHeap (0x264db0). Locate each jal fixMagicPointers site,
    /// walk back to the enclosing function start (first insn after the
    /// previous jr ra delay slot) and confirm it also calls initParticleHeap.
    /// Returns the function start address or 0.</summary>
    public static uint FindMagicHeaderFunc(byte[] d)
    {
        for (int off = 0; off + 8 <= d.Length; off += 4)
        {
            uint ins = BitConverter.ToUInt32(d, off);
            if (ins >> 26 != 3 || (ins & 0x3FFFFFF) << 2 != FnFixMagicPointers) continue;
            // function start: byte after the delay slot of the last jr ra,
            // bounded back 0x800 bytes
            int fstart = Math.Max(0, off - 0x800);
            for (int i = off - 4; i >= fstart; i -= 4)
                if (BitConverter.ToUInt32(d, i) == 0x03e00008) { fstart = i + 8; break; }
            // confirm the same function calls initParticleHeap
            for (int i = fstart; i + 8 <= d.Length && i < fstart + 0x1000; i += 4)
            {
                uint x = BitConverter.ToUInt32(d, i);
                if (x >> 26 == 3 && (x & 0x3FFFFFF) << 2 == FnInitParticleHeap)
                    return (uint)(LoadAddress + fstart);
                if (i > fstart && x == 0x03e00008) break; // left the function
            }
        }
        return 0;
    }

    /// <summary>Headers resolved through magicHeader callers: for each
    /// `jal magicHeader` site a short windowed track resolves a0, and the
    /// header offset is read back at u32(a0 + 0x2C) — the caller association
    /// the strict file scan cannot provide.</summary>
    public static List<int> FindCallerHeaders(byte[] d)
    {
        var found = new List<int>();
        uint fn = FindMagicHeaderFunc(d);
        if (fn == 0) return found;
        for (int off = 0; off + 8 <= d.Length; off += 4)
        {
            uint ins = BitConverter.ToUInt32(d, off);
            if (ins >> 26 != 3 || (ins & 0x3FFFFFF) << 2 != fn) continue;
            var t = new RegTracker(d) { StopOnJ = false, MagicHeaderFunc = fn };
            t.Run(Math.Max(0, off - 0x200), off + 8, 0x400);
            found.AddRange(t.Headers);
        }
        return found;
    }

    /// <summary>Strict shape check for a magic header candidate: u32(+0x3C)
    /// is the dataStart rel-offset, dataStart has a particle table at +0x20
    /// and a count at +0x50, and the indexed entry validates as a PPP
    /// container (synthEmitters layout).</summary>
    public static bool ValidHeader(byte[] d, int h)
    {
        if (h < 0 || h + 0x44 > d.Length) return false;
        uint rel = BitConverter.ToUInt32(d, h + 0x3C);
        if (rel == 0 || rel == BitConverter.ToUInt32(d, h + 0x40) || rel > 0x8000) return false;
        // header signature: an ascending table of small sub-offsets
        // (sprite/clut specs, dataStart) followed by zeros — every non-zero
        // u32 in +0x00..+0x40 must stay below rel
        int nz = 0;
        for (int i = 0; i < 0x40; i += 4)
        {
            uint v = BitConverter.ToUInt32(d, h + i);
            if (v == 0) continue;
            if (v > rel) return false;
            nz++;
        }
        if (nz < 3) return false;
        long ds = h + (long)rel;
        if (ds + 0x60 > d.Length) return false;
        uint ms = BitConverter.ToUInt32(d, (int)ds + 0x20);
        int cnt = BitConverter.ToUInt16(d, (int)ds + 0x50);
        if (ms == 0 || cnt < 1 || cnt > 256 || ds + ms + 4L * cnt > d.Length) return false;
        // some entries are empty stubs (Fire: indices 0/1 empty, 2 real) —
        // the header is valid if at least one entry is a real PPP container
        for (int i = 0; i < cnt; i++)
        {
            long pi = BitConverter.ToUInt32(d, (int)(ds + ms + 4L * i)) + ds;
            if (pi > 0 && pi < d.Length
                && Particles.Validate(d, (int)pi, synthEmitters: true) == null)
                return true;
        }
        return false;
    }

    public static Layout Parse(byte[] d)
    {
        var ptrs = new uint[8];
        for (int i = 0; i < 8; i++) ptrs[i] = BitConverter.ToUInt32(d, 4 * i);
        int fo = FindFuncOffset(d);
        return new Layout(fo, BuildFuncMap(d, fo), ptrs);
    }

    public static string Describe(string path, byte[] d)
    {
        var l = Parse(d);
        var sb = new StringBuilder();
        sb.AppendLine(path + "  " + d.Length + " bytes  (magic bin)");
        sb.AppendLine("entradas +0x00:");
        for (int i = 0; i < l.EntryPointers.Length; i++)
        {
            uint v = l.EntryPointers[i];
            string note = (v >= LoadAddress && v - LoadAddress < (uint)d.Length)
                ? "-> file+0x" + (v - LoadAddress).ToString("x") : "(fora do arquivo)";
            sb.AppendLine("  [" + i + "] 0x" + v.ToString("x8") + " " + note);
        }
        sb.AppendLine(l.FuncOffset >= 0
            ? "funcList em +0x" + l.FuncOffset.ToString("x") + " (validFuncList ok)"
            : "funcList nao encontrada (0x40..0x130)");
        sb.AppendLine("funcMap[" + l.FuncMap.Count + "]:");
        var names2 = new List<string>();
        foreach (var op in l.FuncMap) names2.Add(OpcodeName(op));
        sb.AppendLine("  " + string.Join(" ", names2));
        return sb.ToString();
    }
}
