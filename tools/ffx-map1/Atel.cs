// ATEL script blob parser + disassembler.
// Blob layout (ffx-editor research_tools/Atel/atel_disasm.py + noclip
// bin.ts parseScript): i32 codeLen@0, i32 creatorOff@8, i32 scriptIdOff@0xC,
// i32 totalLen@0x10, i32 codeOff@0x30, u16 workerCount@0x34,
// u16 actorCount@0x36, then workerCount i32 offsets @0x38.
// Worker header: u16 eventType@0, varCount@2, intConstCount@4,
// floatConstCount@6, funcCount@8, jumpCount@0xA, i32 privDataLen@0x10,
// intConstOff@0x18, floatConstOff@0x1C, funcTableOff@0x20,
// jumpTableOff@0x24, privDataOff@0x2C, sharedDataOff@0x30.
// Instructions: opcode < 0x80 → 1 byte; opcode >= 0x80 → 3 bytes (u16 LE).
// Containers: EV01 → blob at u32@4; encounter 0e bins → blob at u32@4.
using System.Buffers.Binary;

namespace FfxMap1;

public sealed class AtelBlob
{
    public required byte[] Data { get; init; }
    public required int Offs { get; init; }
    public int CodeLen, CodeOff, WorkerCount, ActorCount;
    public string Creator = "", ScriptId = "";
    public List<Worker> Workers = new();

    public sealed class Worker
    {
        public int Offs, EventType, VarCount, IntConstCount, FloatConstCount,
                    FuncCount, JumpCount, PrivDataLen;
        public int[] Funcs = Array.Empty<int>(), Jumps = Array.Empty<int>();
        public float[] Floats = Array.Empty<float>();
        public int[] Ints = Array.Empty<int>();
    }

    int I32(long o) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan((int)(Offs + o)));
    ushort U16(long o) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)(Offs + o)));

    static string CStr(byte[] d, int o)
    {
        if (o <= 0 || o >= d.Length) return "";
        int e = Array.IndexOf(d, (byte)0, o);
        return System.Text.Encoding.ASCII.GetString(d, o, (e < 0 ? d.Length : e) - o);
    }

    /// <summary>Parse a blob at byte offset `offs` inside `d`.</summary>
    public static AtelBlob Load(byte[] d, int offs)
    {
        if (offs < 0 || offs + 0x38 > d.Length)
            throw new InvalidDataException($"ATEL blob @0x{offs:x} fora do arquivo");
        var b = new AtelBlob { Data = d, Offs = offs };
        b.CodeLen = b.I32(0);
        int creatorOff = b.I32(8), scriptIdOff = b.I32(0x0C);
        b.CodeOff = offs + b.I32(0x30);
        b.WorkerCount = b.U16(0x34);
        b.ActorCount = b.U16(0x36);
        b.Creator = CStr(d, offs + creatorOff);
        b.ScriptId = CStr(d, offs + scriptIdOff);
        if (b.CodeLen <= 0 || b.CodeOff < offs || b.CodeOff + b.CodeLen > d.Length)
            throw new InvalidDataException(
                $"ATEL @0x{offs:x}: codeLen 0x{b.CodeLen:x}/codeOff 0x{b.CodeOff:x} inválidos");
        for (int i = 0; i < b.WorkerCount; i++)
        {
            int wo = b.I32(0x38 + 4 * i);
            if (wo <= 0 || offs + wo + 0x34 > d.Length) continue;
            var w = new Worker
            {
                Offs = offs + wo,
                EventType = ReadU16(d, offs + wo),
                VarCount = ReadU16(d, offs + wo + 2),
                IntConstCount = ReadU16(d, offs + wo + 4),
                FloatConstCount = ReadU16(d, offs + wo + 6),
                FuncCount = ReadU16(d, offs + wo + 8),
                JumpCount = ReadU16(d, offs + wo + 0x0A),
                PrivDataLen = ReadI32(d, offs + wo + 0x10),
            };
            int fto = ReadI32(d, offs + wo + 0x20),
                jto = ReadI32(d, offs + wo + 0x24),
                ico = ReadI32(d, offs + wo + 0x18),
                fco = ReadI32(d, offs + wo + 0x1C);
            w.Funcs = Table(d, offs + fto, w.FuncCount, ReadI32);
            w.Jumps = Table(d, offs + jto, w.JumpCount, ReadI32);
            w.Ints = Table(d, offs + ico, w.IntConstCount, ReadI32);
            w.Floats = Table(d, offs + fco, w.FloatConstCount, ReadF32);
            b.Workers.Add(w);
        }
        return b;
    }

    static ushort ReadU16(byte[] d, int o)
        => o + 2 <= d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) : (ushort)0;
    static int ReadI32(byte[] d, int o)
        => o + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o)) : 0;
    static float ReadF32(byte[] d, int o)
        => o + 4 <= d.Length ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)) : 0;
    static T[] Table<T>(byte[] d, int off, int n, Func<byte[], int, T> rd)
    {
        var a = new T[Math.Max(0, n)];
        for (int i = 0; i < a.Length; i++) a[i] = rd(d, off + 4 * i);
        return a;
    }

    /// <summary>Linear sweep over the code region: (code-rel addr, op, operand).</summary>
    public IEnumerable<(int Addr, byte Op, int? Operand)> Disasm()
    {
        int pos = CodeOff, end = Math.Min(Data.Length, CodeOff + CodeLen);
        while (pos < end)
        {
            byte op = Data[pos];
            if ((op & 0x80) != 0)
            {
                if (pos + 3 > end) { yield return (pos - CodeOff, op, null); yield break; }
                yield return (pos - CodeOff, op,
                    BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(pos + 1)));
                pos += 3;
            }
            else { yield return (pos - CodeOff, op, null); pos += 1; }
        }
    }

    /// <summary>Locate the ATEL blob inside a container bin: EV01 and
    /// encounter files both store the blob offset at +0x04.</summary>
    public static int FindBlob(byte[] d)
        => d.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)) : 0;

    /// <summary>Opcode names (ffx-editor atel_disasm.py table —
    /// same-repo provenance; unknown ops render opXX).</summary>
    public static string OpName(byte op) => Ops.TryGetValue(op, out var n) ? n : $"op{op:x2}";

    static readonly Dictionary<byte, string> Ops = new()
    {
        [0x00] = "NOP", [0x01] = "LOR", [0x02] = "LAND", [0x03] = "OR",
        [0x04] = "EOR", [0x05] = "AND", [0x06] = "EQ", [0x07] = "NE",
        [0x08] = "GTU", [0x09] = "LSU", [0x0A] = "GT", [0x0B] = "LS",
        [0x0C] = "GTEU", [0x0D] = "LSEU", [0x0E] = "GTE", [0x0F] = "LSE",
        [0x10] = "BON", [0x11] = "BOFF", [0x12] = "SLL", [0x13] = "SRL",
        [0x14] = "ADD", [0x15] = "SUB", [0x16] = "MUL", [0x17] = "DIV",
        [0x18] = "MOD", [0x19] = "NOT", [0x1A] = "UMINUS",
        [0x1B] = "FIXADRS", [0x1C] = "BNOT",
        [0x25] = "POPA", [0x26] = "PUSHA", [0x28] = "PUSHX", [0x29] = "PUSHY",
        [0x2A] = "POPX", [0x2B] = "REPUSH", [0x2C] = "POPY",
        [0x34] = "RTS", [0x36] = "REQ", [0x37] = "REQSW", [0x38] = "REQEW",
        [0x39] = "PREQ", [0x3A] = "PREQSW", [0x3B] = "PREQEW",
        [0x3C] = "RET", [0x3D] = "RETN", [0x3E] = "RETT", [0x3F] = "RETTN",
        [0x40] = "HALT",
        [0x45] = "FREQ", [0x46] = "TREQ", [0x47] = "BREQ", [0x48] = "BFREQ",
        [0x49] = "BTREQ", [0x4A] = "FREQSW", [0x4B] = "TREQSW",
        [0x4C] = "BREQSW", [0x4D] = "BFREQSW", [0x4E] = "BTREQSW",
        [0x4F] = "FREQEW", [0x50] = "TREQEW", [0x51] = "BREQEW",
        [0x52] = "BFREQEW", [0x53] = "BTREQEW", [0x54] = "DRET",
        [0x59] = "POPI0", [0x5A] = "POPI1", [0x5B] = "POPI2", [0x5C] = "POPI3",
        [0x5D] = "POPF0", [0x5E] = "POPF1", [0x5F] = "POPF2", [0x60] = "POPF3",
        [0x61] = "POPF4", [0x62] = "POPF5", [0x63] = "POPF6", [0x64] = "POPF7",
        [0x65] = "POPF8", [0x66] = "POPF9",
        [0x67] = "PUSHI0", [0x68] = "PUSHI1", [0x69] = "PUSHI2", [0x6A] = "PUSHI3",
        [0x6B] = "PUSHF0", [0x6C] = "PUSHF1", [0x6D] = "PUSHF2", [0x6E] = "PUSHF3",
        [0x6F] = "PUSHF4", [0x70] = "PUSHF5", [0x71] = "PUSHF6", [0x72] = "PUSHF7",
        [0x73] = "PUSHF8", [0x74] = "PUSHF9",
        [0x77] = "REQWAIT", [0x78] = "PREQWAIT", [0x79] = "REQCHG",
        [0x7A] = "ACTREQ", [0x9D] = "LABEL", [0x9E] = "TAG",
        [0x9F] = "PUSHV", [0xA0] = "POPV", [0xA1] = "POPVL",
        [0xA2] = "PUSHAR", [0xA3] = "POPAR", [0xA4] = "POPARL",
        [0xA7] = "PUSHARP", [0xAD] = "PUSHI", [0xAE] = "PUSHII",
        [0xAF] = "PUSHF", [0xB0] = "JMP", [0xB1] = "CJMP", [0xB2] = "NCJMP",
        [0xB3] = "JSR", [0xB5] = "CALL", [0xC1] = "PUSHN", [0xC2] = "PUSHT",
        [0xC3] = "PUSHVP", [0xC4] = "PUSHFIX",
        [0xD5] = "POPXJMP", [0xD6] = "POPXCJMP", [0xD7] = "POPXNCJMP",
        [0xD8] = "CALLPOPA", [0xF5] = "PUSHAINTER", [0xF6] = "SYSTEM",
    };
}
