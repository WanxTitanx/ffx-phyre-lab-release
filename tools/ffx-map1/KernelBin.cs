using System.Text;

namespace FfxMap1;

/// <summary>Excel header-driven kernel tables (command.bin / item.bin /
/// monmagic*.bin / a_ability.bin): 20-byte header, fixed-stride records,
/// trailing text pool. TextScriptInfo = u16 pool offset + u16 script id.
/// Layout provenance: FFXProjectEditor FfxLib (Ability_Command.cs,
/// EntryListFile.cs) + FFX_STRUCTURE_COMPLETE REV-W13-1 (96B stride,
/// disk record == runtime record).</summary>
public static class KernelBin
{
    public sealed class Table
    {
        public byte[] Data = Array.Empty<byte>();
        public int MinIndex, EntryCount, EntryLength, TotalDataLength, DataOff;
        public byte[] Pool = Array.Empty<byte>();
        public int RecordOff(int i) => DataOff + i * EntryLength;
        public ReadOnlySpan<byte> Record(int i) => Data.AsSpan(RecordOff(i), EntryLength);
        public ushort U16(int rec, int off) => BitConverter.ToUInt16(Data, RecordOff(rec) + off);
        public void SetU16(int rec, int off, ushort v) =>
            BitConverter.TryWriteBytes(Data.AsSpan(RecordOff(rec) + off), v);
        public void SetU8(int rec, int off, byte v) => Data[RecordOff(rec) + off] = v;
        public string Name(int rec) => DecodeText(Pool, U16(rec, 0x00));
        public string Desc(int rec) => DecodeText(Pool, U16(rec, 0x08));
    }

    /// <summary>Parse the shared 20-byte header and slice the record area +
    /// text pool. Returns an error string on any structural violation.</summary>
    public static string? Load(byte[] d, out Table t)
    {
        t = new Table { Data = d };
        if (d.Length < 0x14) return "arquivo menor que o header (0x14)";
        if (BitConverter.ToUInt32(d, 0) != 1) return "magic != 1";
        t.MinIndex = BitConverter.ToUInt16(d, 0x08);
        t.EntryCount = BitConverter.ToUInt16(d, 0x0A) + 1; // stored as count-1
        t.EntryLength = BitConverter.ToUInt16(d, 0x0C);
        t.TotalDataLength = BitConverter.ToUInt16(d, 0x0E);
        t.DataOff = (int)BitConverter.ToUInt32(d, 0x10);
        if (t.EntryLength < 4 || t.EntryLength > 0x400)
            return $"entryLength absurdo: {t.EntryLength}";
        if (t.DataOff < 0x14 || t.DataOff >= d.Length)
            return $"dataOff fora do arquivo: 0x{t.DataOff:x}";
        long end = (long)t.DataOff + (long)t.EntryCount * t.EntryLength;
        if (end > d.Length)
            return $"tabela excede o arquivo: {t.EntryCount}x{t.EntryLength} @0x{t.DataOff:x} > {d.Length}";
        t.Pool = d[(int)end..];
        return null;
    }

    // ---- FFX US text decoding (ported from FfxEncoding.us.cs — user repo) ----
    static readonly Dictionary<int, string> Us = new()
    {
        [48]="0", [49]="1", [50]="2", [51]="3", [52]="4", [53]="5", [54]="6", [55]="7", [56]="8", [57]="9", [58]=" ", [59]="!", [60]="”", [61]="#", [62]="$", [63]="%", [64]="&", [65]="’", [66]="(", [67]=")", [68]="*", [69]="+", [70]=",", [71]="-", [72]=".", [73]="/", [74]=":", [75]=";", [76]="<", [77]="=", [78]=">", [79]="?", [80]="A", [81]="B", [82]="C", [83]="D", [84]="E", [85]="F", [86]="G", [87]="H", [88]="I", [89]="J", [90]="K", [91]="L", [92]="M", [93]="N", [94]="O", [95]="P", [96]="Q", [97]="R", [98]="S", [99]="T", [100]="U", [101]="V", [102]="W", [103]="X", [104]="Y", [105]="Z", [106]="[", [108]="]", [109]="^", [110]="_", [111]="‘", [112]="a", [113]="b", [114]="c", [115]="d", [116]="e", [117]="f", [118]="g", [119]="h", [120]="i", [121]="j", [122]="k", [123]="l", [124]="m", [125]="n", [126]="o", [127]="p", [128]="q", [129]="r", [130]="s", [131]="t", [132]="u", [133]="v", [134]="w", [135]="x", [136]="y", [137]="z", [138]="{", [139]="|", [140]="}", [141]="~", [142]="·", [143]="【", [144]="】", [145]="♪", [146]="♥", [147]="Œ", [148]="“", [149]="”", [150]="—", [151]="œ", [152]="¡", [153]="↑", [154]="↓", [155]="←", [156]="→", [157]="¨", [158]="«", [159]="º", [160]=" ", [161]="»", [162]="¿", [163]="À", [164]="Á", [165]="Â", [166]="Ä", [167]="ç", [168]="È", [169]="É", [170]="Ê", [171]="Ë", [172]="Ì", [173]="Í", [174]="Î", [175]="Ï", [176]="Ñ", [177]="Ò", [178]="Ó", [179]="Ô", [180]="Ö", [181]="Ù", [182]="Ú", [183]="Û", [184]="Ü", [185]="ß", [186]="à", [187]="á", [188]="â", [189]="ä", [190]="ç", [191]="è", [192]="é", [193]="ê", [194]="ë", [195]="ì", [196]="í", [197]="î", [198]="ï", [199]="ñ", [200]="ò", [201]="ó", [202]="ô", [203]="ö", [204]="ù", [205]="ú", [206]="û", [207]="ü", [208]=",", [209]="ƒ", [210]="„", [211]="…", [213]="’", [214]="•", [215]="-", [216]="~", [217]="™", [218]=" ", [219]="›", [220]="§", [221]="©", [222]="ª", [223]="®", [224]="±", [225]="²", [226]="³", [227]="¼", [228]="½", [229]="¾", [230]="×", [231]="÷", [232]="‹", [233]="…", [234]=" ", [235]="ǎ", [236]="★", [237]="☆", [238]="■", [239]="∞"
    };

    static readonly Lazy<Dictionary<char, int>> UsEnc = new(() =>
    {
        var m = new Dictionary<char, int>();
        foreach (var kv in Us)
            if (kv.Value.Length == 1 && !m.ContainsKey(kv.Value[0]))
                m[kv.Value[0]] = kv.Key;
        return m;
    });

    /// <summary>Encodes a US-subset string to FFX bytes; null on an
    /// unsupported char (control codes / FONT banks are not encodable).</summary>
    public static byte[]? EncodeUs(string s)
    {
        var outp = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            if (!UsEnc.Value.TryGetValue(s[i], out int b)) return null;
            outp[i] = (byte)b;
        }
        return outp;
    }

    /// <summary>Renames a record: appends the encoded name+NUL to the text
    /// pool (file end) and repoints the record's name offset (+0x00).
    /// Never edits shared pool strings in place. Returns the grown file,
    /// or null when the name can't encode or the pool offset overflows.</summary>
    public static byte[]? Rename(Table t, int rec, string newName)
        => SetText(t, rec, 0x00, newName);

    /// <summary>Repoints a record's text-offset field (+fieldOff) to a new
    /// appended pool string — the general form behind Rename, also used by
    /// the *_txt.bin help/name-help tables (ExcelSimplifiableTextOffset).</summary>
    public static byte[]? SetText(Table t, int rec, int fieldOff, string newText)
    {
        var enc = EncodeUs(newText);
        if (enc == null) return null;
        int recEnd = t.DataOff + t.EntryCount * t.EntryLength;
        int newOff = t.Data.Length - recEnd; // appended string's pool offset
        if (newOff > 0xFFFF) return null;
        var d = new byte[t.Data.Length + enc.Length + 1];
        Array.Copy(t.Data, d, t.Data.Length);
        Array.Copy(enc, 0, d, t.Data.Length, enc.Length);
        BitConverter.TryWriteBytes(d.AsSpan(t.RecordOff(rec) + fieldOff), (ushort)newOff);
        return d;
    }

    /// <summary>Structural add: append a cloned record at the end of the
    /// record area (before the pool). Pool offsets are pool-relative so no
    /// fixups are needed — only EntryCount (+0x0A, stored count-1) and
    /// TotalDataLength (+0x0E, = count*stride) bump. The clone inherits
    /// srcRec's fields + shared text offsets (rename appends fresh).</summary>
    public static byte[] AddRecord(Table t, int srcRec)
    {
        if (srcRec < 0 || srcRec >= t.EntryCount) return t.Data;
        int end = t.DataOff + t.EntryCount * t.EntryLength;
        var d = new byte[t.Data.Length + t.EntryLength];
        Array.Copy(t.Data, 0, d, 0, end);
        Array.Copy(t.Data, t.RecordOff(srcRec), d, end, t.EntryLength);
        Array.Copy(t.Data, end, d, end + t.EntryLength, t.Data.Length - end);
        BitConverter.TryWriteBytes(d.AsSpan(0x0A), (ushort)t.EntryCount);      // count-1
        BitConverter.TryWriteBytes(d.AsSpan(0x0E),
            (ushort)((t.EntryCount + 1) * t.EntryLength));
        return d;
    }

    /// <summary>Decode a NUL-terminated script at pool offset: 1-byte US
    /// glyphs, control codes render as &lt;Cnn&gt;, 2-byte glyph leads
    /// (0x06, 0x26..0x2F) render as &lt;FONTn:idx&gt;.</summary>
    public static string DecodeText(byte[] pool, int off)
    {
        if (off < 0 || off >= pool.Length) return "";
        var sb = new StringBuilder();
        for (int i = off; i < pool.Length; i++)
        {
            byte b = pool[i];
            if (b == 0) break;
            if (b is >= 0x26 and <= 0x2F || b == 0x06)
            {
                if (i + 1 >= pool.Length) break;
                int n = pool[i + 1];
                int bank = b == 0x06 ? 5 : b <= 0x27 ? 3 : b <= 0x29 ? 2 : b <= 0x2B ? 1 : 0;
                int idx = b == 0x06 ? n - 0x30 : 208 * b + n - (bank == 0 ? 8992 : bank == 1 ? 8784 : bank == 2 ? 8368 : 7952);
                sb.Append($"<FONT{bank}:{idx}>");
                i++;
                continue;
            }
            if (b < 0x26 && !Us.ContainsKey(b))
            {
                sb.Append(b == 3 ? "\n" : $"<C{b}>");
                if (b == 10 || b == 19) i++; // 1-byte params
                continue;
            }
            sb.Append(Us.TryGetValue(b, out var c) ? c : $"<{b:x2}>");
        }
        return sb.ToString();
    }
}
