// MAP1 bin parser — FFX HD PC map packages as consumed by the noclip viewer
// (data/FinalFantasyX/13/<id>.bin pairs: even = textures/particles,
// odd = level geometry/scene graph). Layout recovered from the
// dist-ffxstudio bundle parser (see docs/NOCLIP_INVENTORY.md):
//
//   +0x00  "MAP1"
//   +0x14  u32 -> section A offset
//   +0x18  u32 -> section B offset
//   +0x38  u32 -> section C offset
//   +0x40  u32 -> section D offset
//
// A signed section starts with big-endian u32 0x65432100 and LE u32 count
// at +12, followed by count entries of {64B descriptor + <size> payload}:
//   descriptor +0 u32 type, +4 u32 payloadSize, +8..+63 reserved.
//
// Texture-bin entry types: 2 = GS texture upload (payload = 64B upload
// header {+4 tbp0, +8 tbw, +12 psm, +16 w, +20 h} + linear texels;
// psm==20 entries carry 32bpp data uploaded as PSMCT32 with dims
// tbw>>1, w>>1, h>>2), 3 = palette list (72 x 1024B palettes,
// paletteType at descriptor +36).
//
// Level-bin entry types: 0 = LEVEL_PART, 1 = MODEL (VIF packet stream),
// 4 = LIGHTING (clear color / fog / envmap), 5|8 = EFFECT,
// 6 = ANIMATED_TEXTURE (payload contains VIF DIRECT upload 0x11000000).

using System.Buffers.Binary;
using System.Text;

namespace FfxMap1;

public sealed class Map1File
{
    public required byte[] Data { get; init; }
    public required string Path { get; init; }

    public uint U32(long off) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off));
    public uint U32BE(long off) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)off));
    public ushort U16(long off) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)off));
    public short I16(long off) => BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan((int)off));
    public float F32(long off) => BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan((int)off));
    public ReadOnlySpan<byte> Span(long off, int len) => Data.AsSpan((int)off, len);

    public static Map1File Load(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 0x80 || Encoding.ASCII.GetString(d, 0, 4) != "MAP1")
            throw new InvalidDataException($"{path}: bad magic (expected MAP1)");
        return new Map1File { Data = d, Path = path };
    }

    /// <summary>Wrap already-loaded bytes (patched copies, overlays) without
    /// touching disk — keeps the same validation as Load.</summary>
    public static Map1File Load(string name, byte[] d)
    {
        if (d.Length < 0x80 || Encoding.ASCII.GetString(d, 0, 4) != "MAP1")
            throw new InvalidDataException($"{name}: bad magic (expected MAP1)");
        return new Map1File { Data = d, Path = name };
    }

    public uint Slot(int headerOffset) => U32(headerOffset);

    /// <summary>Walk a 0x65432100-signed section. Returns null when offset is 0.</summary>
    public List<SectionEntry>? WalkSection(uint offset)
    {
        if (offset == 0) return null;
        if (U32BE(offset) != 0x65432100)
            throw new InvalidDataException(
                $"{Path}: section @0x{offset:x} sig 0x{U32BE(offset):08x} != 0x65432100");
        int count = (int)U32(offset + 12);
        var list = new List<SectionEntry>(count);
        long a = offset + 64;
        for (int i = 0; i < count; i++)
        {
            uint type = U32(a), size = U32(a + 4);
            list.Add(new SectionEntry
            {
                Index = i,
                Type = type,
                Size = size,
                DescOffset = a,
                PayloadOffset = a + 64,
            });
            a += 64 + size;
            if (a > Data.Length)
                throw new InvalidDataException(
                    $"{Path}: entry {i} overruns file (end 0x{a:x} > 0x{Data.Length:x})");
        }
        return list;
    }
}

public sealed class SectionEntry
{
    public required int Index { get; init; }
    public required uint Type { get; init; }
    public required uint Size { get; init; }
    public required long DescOffset { get; init; }
    public required long PayloadOffset { get; init; }
    public long End => PayloadOffset + Size;
}

/// <summary>Walkmesh section (+0x18 of the geometry bin). Layout per the
/// bundle parser: header {u16@+4 version, u16@+10 vertCount, f32@+12 scale/10,
/// u32@+24 vertOffset, u32@+28 triHdrOffset}; triHdr {u16@+8 triCount,
/// u32@+12 triOffset}. Verts are s16 x4; tris are 16B
/// {u16 v0,v1,v2; s16 e01,e12,e20; u32 data} with
/// data = passability:7 | encounter:2@7 | location:2@11 | surfaceType:2@15
///      | light:3x5@17.</summary>
public sealed class Walkmesh
{
    public required int Version { get; init; }
    public required float Scale { get; init; }
    public required short[] Vertices { get; init; }  // x,y,z,w per vert
    public required List<Tri> Tris { get; init; }
    public required long TrisOffset { get; init; }
    public required long SectionOffset { get; init; }
    /// <summary>File offset of the s16x4 vertex array (writer needs it —
    /// verts are quantized world pos × Scale; w component preserved).</summary>
    public required long VertsOffset { get; init; }

    public readonly record struct Tri(
        int V0, int V1, int V2, int E01, int E12, int E20, uint Data)
    {
        public int Passability => (int)(Data & 0x7f);
        public int Encounter => (int)((Data >> 7) & 3);
        public int Location => (int)((Data >> 11) & 3);
        public int SurfaceType => (int)((Data >> 15) & 3);
        public int Light0 => (int)((Data >> 17) & 31);
        public int Light1 => (int)((Data >> 22) & 31);
        public int Light2 => (int)((Data >> 27) & 31);
    }

    public (float x, float y, float z) VertPos(int i)
        => (Vertices[4 * i] / Scale, Vertices[4 * i + 1] / Scale, Vertices[4 * i + 2] / Scale);

    public static Walkmesh? Parse(Map1File f)
    {
        uint n = f.Slot(0x18);
        if (n == 0) return null;
        int version = f.U16(n + 4);
        int vertCount = f.U16(n + 10);
        float scale = f.F32(n + 12) / 10f;
        long vertsOff = f.U32(n + 24) + n;
        long triHdr = f.U32(n + 28) + n;
        if (triHdr + 14 > f.Data.Length || vertCount <= 0
            || !(scale > 0.01f && scale < 1e6f)
            || vertsOff + 8L * vertCount > f.Data.Length)
            return null;
        int triCount = f.U16(triHdr + 8);
        long trisOff = f.U32(triHdr + 12) + n;
        // not every +0x18 section is a walkmesh (battle arenas reuse the slot):
        // reject implausible geometry instead of reading past EOF
        if (triCount <= 0 || trisOff + 16L * triCount > f.Data.Length)
            return null;

        var verts = new short[vertCount * 4];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = (short)f.U16(vertsOff + 2 * i);

        var tris = new List<Tri>(triCount);
        for (int i = 0; i < triCount; i++)
        {
            long o = trisOff + 16 * i;
            tris.Add(new Tri(f.U16(o), f.U16(o + 2), f.U16(o + 4),
                (short)f.U16(o + 6), (short)f.U16(o + 8), (short)f.U16(o + 10),
                f.U32(o + 12)));
        }
        return new Walkmesh
        {
            Version = version, Scale = scale, Vertices = verts,
            Tris = tris, TrisOffset = trisOff, SectionOffset = n,
            VertsOffset = vertsOff,
        };
    }
}

/// <summary>EV01 event package (0c/&lt;id&gt;.bin): actor spawn table,
/// model list, strings, ATEL script. Layout per the bundle parser:
/// +0x04 tableOff, +0x08 stringsOff; @tableOff: +4 pointsOff(rel),
/// +8 pointsEnd(rel), +44 modelListOff(rel); modelList at
/// tableOff+modelListOff+40: u32 relOff, then u16 count + count u16 ids.
/// Points: 32B records {u16 mapId, u16@6 entrypoint, f32@8 heading,
/// f3@12..20 pos}.</summary>
public sealed class Ev01File
{
    public required byte[] Data { get; init; }
    public required string Path { get; init; }

    public uint U32(long off) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off));
    public ushort U16(long off) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)off));
    public float F32(long off) => BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan((int)off));

    public static Ev01File Load(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 16 || Encoding.ASCII.GetString(d, 0, 4) != "EV01")
            throw new InvalidDataException($"{path}: bad magic (expected EV01)");
        return new Ev01File { Data = d, Path = path };
    }

    public readonly record struct MapPoint(
        int MapId, int Entrypoint, float Heading, float X, float Y, float Z);

    public List<int> ModelList()
    {
        long i = U32(4);
        long o = U32(i + 44);
        long l = U32(i + o + 40) + i;
        int h = U16(l);
        var list = new List<int>(h);
        for (int k = 0; k < h; k++) list.Add(U16(l + 2 + 2 * k));
        return list;
    }

    public List<MapPoint> Points()
    {
        long i = U32(4);
        long s = U32(i + 4) + i, n = U32(i + 8) + i;
        var list = new List<MapPoint>();
        for (long e = s; e < n; e += 32)
            list.Add(new MapPoint(U16(e), U16(e + 6), F32(e + 8),
                F32(e + 12), F32(e + 16), F32(e + 20)));
        return list;
    }
}

/// <summary>Encounter edits overlay (edits/&lt;enc&gt;.json) — shared parser so the
/// UI and selftests read the same schema. Per-slot entries live under
/// "actors": {position:[dx,dy,dz], heading, scale, monster} — monster is the
/// optional composition override (swap which actor renders in that slot);
/// the server merges field-level so extra keys survive viewer POSTs.
/// "extra" = absolute spawns, "remove" = hidden vanilla slots.</summary>
public sealed class EncEdits
{
    public Dictionary<int, float[]> Deltas { get; } = new();
    public List<(int Monster, float[] Pos, float Heading, float Scale)> Extra { get; } = new();
    public HashSet<int> Remove { get; } = new();
    public Dictionary<int, int> Swaps { get; } = new();
    /// <summary>Spawn-point deltas for the party/other groups of the first
    /// positions block — "party":{"0":[dx,dy,dz]}, "other":{...}.</summary>
    public Dictionary<int, float[]> Party { get; } = new();
    public Dictionary<int, float[]> Other { get; } = new();

    public static EncEdits Parse(string json)
    {
        var r = new EncEdits();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("actors", out var actors))
            foreach (var prop in actors.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var slot)) continue;
                var v = prop.Value;
                if (v.TryGetProperty("monster", out var mon) &&
                    mon.ValueKind == System.Text.Json.JsonValueKind.Number)
                    r.Swaps[slot] = mon.GetInt32();
                var e = new float[6] { 0, 0, 0, 0, 0, 1 };
                bool any = false;
                if (v.TryGetProperty("position", out var pos) &&
                    pos.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int k = 0;
                    foreach (var x in pos.EnumerateArray())
                    { if (k < 3) e[k] = (float)x.GetDouble(); k++; }
                    any = true;
                }
                if (v.TryGetProperty("heading", out var hd)) { e[4] = (float)hd.GetDouble(); any = true; }
                if (v.TryGetProperty("scale", out var sc)) { e[5] = (float)sc.GetDouble(); any = true; }
                if (any) r.Deltas[slot] = e;
            }
        if (root.TryGetProperty("extra", out var extra) &&
            extra.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in extra.EnumerateArray())
            {
                var e = (x.GetProperty("monster").GetInt32(),
                    new float[3], 0f, 1f);
                if (x.TryGetProperty("position", out var pos) &&
                    pos.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int k = 0;
                    foreach (var q in pos.EnumerateArray())
                    { if (k < 3) e.Item2[k] = (float)q.GetDouble(); k++; }
                }
                if (x.TryGetProperty("heading", out var hd)) e.Item3 = (float)hd.GetDouble();
                if (x.TryGetProperty("scale", out var sc)) e.Item4 = (float)sc.GetDouble();
                r.Extra.Add(e);
            }
        if (root.TryGetProperty("remove", out var rm) &&
            rm.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in rm.EnumerateArray()) r.Remove.Add(x.GetInt32());
        foreach (var (key, map) in new[] { ("party", r.Party), ("other", r.Other) })
            if (root.TryGetProperty(key, out var grp))
                foreach (var prop in grp.EnumerateObject())
                {
                    if (!int.TryParse(prop.Name, out var idx)) continue;
                    var d = new float[3]; int k = 0;
                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    foreach (var x in prop.Value.EnumerateArray())
                    { if (k < 3) d[k] = (float)x.GetDouble(); k++; }
                    map[idx] = d;
                }
        return r;
    }
}

/// <summary>0e encounter/battle bin: {ATEL script @+0x04, monsters s16x8
/// @+0x0c(+12), battlePositions @+0x10}. Positions blocks: first 64B then
/// 96B each; three f3 arrays {party, other, monsters} via
/// {u32 off@+16+8k, u8 count@+4+k}.</summary>
public sealed class EncounterFile
{
    public required byte[] Data { get; init; }
    public required string Path { get; init; }

    public uint U32(long off) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off));
    public short I16(long off) => BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan((int)off));
    public float F32(long off) => BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan((int)off));

    public static EncounterFile Load(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 0x20)
            throw new InvalidDataException($"{path}: too small for an encounter bin");
        return new EncounterFile { Data = d, Path = path };
    }

    public List<int> Monsters()
    {
        long i = U32(0x0c);
        var list = new List<int>();
        for (int e = 0; e < 8; e++)
        {
            short m = I16(i + 12 + 2 * e);
            if (m >= 0) list.Add(m);
        }
        return list;
    }

    public readonly record struct PosBlock(int Index, long Offset,
        float[][] Party, float[][] Other, float[][] Monsters,
        long[] MonsterPosOffsets);

    public List<PosBlock> Positions()
    {
        long r = U32(0x10), l = r;
        var blocks = new List<PosBlock>();
        for (int idx = 0; ; idx++)
        {
            if (U32(l) != U32(r)) break;
            var groups = new float[3][][];
            var monOff = new List<long>();
            for (int k = 0; k < 3; k++)
            {
                long rel = U32(l + 16 + 8 * k);
                int cnt = rel == 0 ? 0 : Data[l + 4 + k];
                var arr = new float[cnt][];
                for (int q = 0; q < cnt; q++)
                {
                    long o = rel + r + 16 * q;
                    arr[q] = new[] { F32(o), F32(o + 4), F32(o + 8) };
                    if (k == 2) monOff.Add(o);
                }
                groups[k] = arr;
            }
            blocks.Add(new PosBlock(idx, l, groups[0], groups[1], groups[2], monOff.ToArray()));
            l += idx == 0 ? 64 : 96;
            if (blocks.Count > 64 || l > Data.Length) break;
        }
        return blocks;
    }
}

/// <summary>0d/0000.bin — battle lists: per-area pools binding a battle-map
/// index to encounter file numbers (0e/&lt;file&gt;.bin) with spawn weights.
/// Header table @+4: 14B records {u16 id, u16 dataOff, u16 currFile, name[6]};
/// pool data at dataStart+dataOff+1: u8 poolCount, per pool {u8 encCount,
/// u16 map, +3 skip, then encCount×{u8 encId, u8 weight}}.</summary>
public sealed class BattleLists
{
    public required byte[] Data { get; init; }

    public uint U32(long off) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)off));
    public ushort U16(long off) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)off));

    public sealed record Encounter(int Id, int File, int Weight);
    public sealed record Pool(int Map, List<Encounter> Files);
    public sealed record Area(int Id, string Name, List<Pool> Pools);

    public static BattleLists Load(string path)
        => new BattleLists { Data = File.ReadAllBytes(path) };

    public List<Area> Areas()
    {
        var areas = new List<Area>();
        long offs = U32(4), dataStart = U32(8);
        while (offs < dataStart)
        {
            int id = U16(offs);
            int dataOff = U16(offs + 2);
            int currFile = U16(offs + 4);
            var name = Encoding.ASCII.GetString(Data, (int)offs + 6, 6).TrimEnd('\0', ' ');
            long sub = dataStart + dataOff + 1;
            int poolCount = Data[sub++];
            var pools = new List<Pool>();
            for (int i = 0; i < poolCount; i++)
            {
                int encCount = Data[sub];
                int map = U16(sub + 1);
                sub += 5;
                var files = new List<Encounter>();
                for (int j = 0; j < encCount; j++)
                {
                    int encId = Data[sub++]; int weight = Data[sub++];
                    files.Add(new Encounter(encId, currFile++, weight));
                }
                pools.Add(new Pool(map, files));
            }
            areas.Add(new Area(id, name, pools));
            offs += 0xE;
        }
        return areas;
    }

    /// <summary>Find the (area, pool) that owns an encounter file index.</summary>
    public (Area area, Pool pool)? Locate(int file)
    {
        foreach (var a in Areas())
            foreach (var p in a.Pools)
                if (p.Files.Any(x => x.File == file)) return (a, p);
        return null;
    }
}

/// <summary>Texture section (+0x14 of the texture/particle bin): GS texture
public sealed class MapTextures
{
    public required List<TexEntry> Textures { get; init; }
    public int PaletteType = -1;
    public long PaletteOffset;

    public readonly record struct TexEntry(
        int Index, int Tbp0, int Tbw, int Psm, int W, int H, long DataOffset);

    public static MapTextures? Parse(Map1File f)
    {
        var entries = f.WalkSection(f.Slot(0x14));
        if (entries == null) return null;
        var mt = new MapTextures { Textures = new List<TexEntry>() };
        foreach (var e in entries)
        {
            long p = e.PayloadOffset;
            if (e.Type == 2)
            {
                int psm = (int)f.U32(p + 12);
                bool is4 = psm == 20;
                mt.Textures.Add(new TexEntry(
                    e.Index,
                    (int)f.U32(p + 4),
                    (int)f.U32(p + 8) >> (is4 ? 1 : 0),
                    psm,
                    (int)f.U32(p + 16) >> (is4 ? 1 : 0),
                    (int)f.U32(p + 20) >> (is4 ? 2 : 0),
                    p + 64));
            }
            else if (e.Type == 3)
            {
                if (mt.PaletteType >= 0)
                    throw new InvalidDataException($"{f.Path}: multiple palette lists");
                mt.PaletteType = (int)f.U32(e.DescOffset + 36);
                mt.PaletteOffset = p;
            }
        }
        return mt;
    }

    public Gs BuildGsMap(Map1File f)
    {
        var gs = new Gs();
        foreach (var t in Textures)
            gs.Upload(t.Psm == 20 ? 0 : t.Psm, t.Tbp0, t.Tbw, 0, 0, t.W, t.H,
                f.Data, (int)t.DataOffset);
        if (PaletteType >= 0)
        {
            int pt = PaletteType == 0 ? 4 : PaletteType;
            for (int a = 0; a < 72; a++)
                gs.Upload(0, Gs.PaletteCbp(pt, a), 1, 0, 0, 16, 16,
                    f.Data, (int)PaletteOffset + 1024 * a);
        }
        return gs;
    }
}

public static class LevelParts
{
    public const uint LevelPart = 0, Model = 1, Lighting = 4, Effect = 5,
                      Effect8 = 8, AnimatedTexture = 6;

    public static string TypeName(uint t) => t switch
    {
        0 => "LEVEL_PART",
        1 => "MODEL",
        4 => "LIGHTING",
        5 => "EFFECT",
        6 => "ANIMATED_TEXTURE",
        8 => "EFFECT8",
        _ => $"type{t}",
    };

    public readonly record struct PartInfo(
        int Index, bool IsSkybox, int Layer,
        float Ex, float Ey, float Ez,
        float Px, float Py, float Pz,
        int EulerOrder, int[] EffectIndices);

    /// <summary>Enumerates LEVEL_PART entries of the geometry section (+0x14),
    /// pairing each section entry with its parsed TRS — the part editor patches
    /// fields through the entry's PayloadOffset.</summary>
    public static List<(SectionEntry Entry, PartInfo Info)> ListParts(Map1File f)
    {
        var list = new List<(SectionEntry, PartInfo)>();
        var sec = f.WalkSection(f.Slot(0x14));
        if (sec == null) return list;
        foreach (var e in sec)
            if (e.Type == LevelPart)
                list.Add((e, ReadPart(f, e)));
        return list;
    }

    public static PartInfo ReadPart(Map1File f, SectionEntry e)
    {
        var p = e.PayloadOffset;
        int n = (int)f.U32(p + 52);
        if (n > 4) throw new InvalidDataException($"part {e.Index}: {n} effects > 4");
        var fx = new int[n];
        for (int i = 0; i < n; i++) fx[i] = f.U16(p + 56 + 2 * i);
        int order = f.U16(p + 48);
        if (order != 0 && order != 5)
            throw new InvalidDataException($"part {e.Index}: euler order {order}");
        return new PartInfo(e.Index, f.U16(p + 2) == 1, f.U16(p + 4),
            f.F32(p + 16), f.F32(p + 20), f.F32(p + 24),
            f.F32(p + 32), f.F32(p + 36), f.F32(p + 40),
            order, fx);
    }

    /// <summary>Structural add: duplicate a LEVEL_PART + its following MODEL
    /// entries, appended at the END of the geometry section's entry list.
    /// Appending preserves every existing entry index (part EffectIndices
    /// stay valid) and rebinds the copied models to the new part via the
    /// sequential curPart rule. Header slots past the insertion point are
    /// shifted; the file has no internal absolute pointers (verified on
    /// 001b: single 0x65432100 section, no refs outside the header).</summary>
    public static byte[] DuplicatePart(Map1File f, int partEntryIndex)
    {
        var sec = f.WalkSection(f.Slot(0x14));
        if (sec == null || partEntryIndex < 0 || partEntryIndex >= sec.Count)
            throw new InvalidDataException("part index out of range");
        var src = sec[partEntryIndex];
        if (src.Type != LevelPart)
            throw new InvalidDataException($"entry {partEntryIndex} is {TypeName(src.Type)}, not LEVEL_PART");
        var copy = new List<SectionEntry> { src };
        for (int j = partEntryIndex + 1; j < sec.Count && sec[j].Type == Model; j++)
            copy.Add(sec[j]);
        long ins = sec[^1].End;
        int delta = copy.Sum(e => 64 + (int)e.Size);
        var n = new byte[f.Data.Length + delta];
        f.Data.AsSpan(0, (int)ins).CopyTo(n);
        long w = ins;
        foreach (var e in copy)
        {
            f.Data.AsSpan((int)e.DescOffset, 64 + (int)e.Size)
                .CopyTo(n.AsSpan((int)w));
            w += 64 + e.Size;
        }
        f.Data.AsSpan((int)ins).CopyTo(n.AsSpan((int)w));
        // section entry count at +12
        int cnt = (int)f.U32(f.Slot(0x14) + 12) + copy.Count;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            n.AsSpan((int)f.Slot(0x14) + 12), cnt);
        // header slots: every non-zero u32 pointer at/after the insertion
        // point shifts by delta (map bins have no other absolute refs)
        Particles.PppEdit.FixMapHeaderSlots(n, (int)ins, delta, f.Data.Length);
        return n;
    }

    /// <summary>Physical delete: remove a LEVEL_PART + its following MODEL
    /// entries, compacting the section in place — the inverse of
    /// DuplicatePart. Part EffectIndices point at EFFECT entries, which
    /// stay put, so no index fixup is needed; only the entry count drops
    /// and header slots past the hole shift by -delta.</summary>
    public static byte[] DeletePart(Map1File f, int partEntryIndex)
    {
        var sec = f.WalkSection(f.Slot(0x14));
        if (sec == null || partEntryIndex < 0 || partEntryIndex >= sec.Count)
            throw new InvalidDataException("part index out of range");
        var src = sec[partEntryIndex];
        if (src.Type != LevelPart)
            throw new InvalidDataException($"entry {partEntryIndex} is {TypeName(src.Type)}, not LEVEL_PART");
        int j = partEntryIndex + 1;
        while (j < sec.Count && sec[j].Type == Model) j++;
        long ins = src.DescOffset;
        long hole = sec[j - 1].End - ins;
        int delta = -(int)hole;
        var n = new byte[f.Data.Length + delta];
        f.Data.AsSpan(0, (int)ins).CopyTo(n);
        f.Data.AsSpan((int)(ins + hole)).CopyTo(n.AsSpan((int)ins));
        int cnt = (int)f.U32(f.Slot(0x14) + 12) - (j - partEntryIndex);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            n.AsSpan((int)f.Slot(0x14) + 12), cnt);
        Particles.PppEdit.FixMapHeaderSlots(n, (int)ins, delta, f.Data.Length);
        return n;
    }

    /// <summary>EFFECT/EFFECT8 entries are keyframe animation tracks for
    /// part transforms — MOTION (translation), ROTATION (euler), PARAMETER
    /// (shader vec), TEXTURE, or COMBINED (euler+translation in 0x20 recs).
    /// Script-activated at runtime (ATEL); parts reference them through
    /// EffectIndices, which index this table — stored explicitly in the
    /// entry descriptor at +0x24 when +0x20 != 0, else sequential.</summary>
    public static class LevelEffects
    {
        public const int Motion = 0, Rotation = 1, Parameter = 2,
                         Texture = 3, Combined = 5;
        // keyframe formats (noclip bin.ts): LINEAR=0 SPLINE=1 CONSTANT=2 COMBINED=3
        public static string TypeName(int t) => t switch
        {
            0 => "MOTION", 1 => "ROTATION", 2 => "PARAMETER",
            3 => "TEXTURE", 5 => "COMBINED", _ => $"t{t}",
        };

        /// <summary>One keyframe: file offset + format + duration (frames).
        /// Vec3 data slots at fixed payload offsets per format —
        /// VecOff(i) gives the i-th editable vec3's file offset.</summary>
        public readonly record struct Key(
            int Format, int Duration, long Offs, bool Combined)
        {
            /// <summary>File offset of vec3 slot i (CONSTANT:1, LINEAR:2,
            /// SPLINE:4, COMBINED:2 — euler then pos-delta).</summary>
            public long VecOff(int i)
            {
                if (Combined) return Offs + (i == 0 ? 0x08 : 0x14);
                return Offs + Format switch
                {
                    0 => 0x30 + 0x10 * i,             // LINEAR: 2 vecs
                    1 => 0x10 + 0x10 * i,             // SPLINE: 4 vecs
                    2 => 0x40,                        // CONSTANT: 1 vec
                    _ => 0x40,
                };
            }
            public int VecCount => Combined ? 2 : Format == 1 ? 4 : Format == 0 ? 2 : 1;
        }

        public readonly record struct Fx(
            int Index, int Type, bool Combined, Key[] Keys);

        /// <summary>Enumerate EFFECT (type5) + EFFECT8 (type8) entries,
        /// resolving the effects[] index from the descriptor.</summary>
        public static List<(SectionEntry Entry, Fx Info)> ListEffects(Map1File f)
        {
            var list = new List<(SectionEntry, Fx)>();
            var sec = f.WalkSection(f.Slot(0x14));
            if (sec == null) return list;
            int seq = 0;
            foreach (var e in sec)
            {
                bool comb = e.Type == Effect8;
                if (e.Type != Effect && !comb) continue;
                long d0 = e.DescOffset;
                int index;
                if (f.U32(d0 + 0x20) != 0)
                {
                    index = (int)f.U32(d0 + 0x24);
                }
                else index = seq++;
                var fx = ParseFx(f, e, comb);
                list.Add((e, fx with { Index = index }));
            }
            return list;
        }

        static Fx ParseFx(Map1File f, SectionEntry e, bool comb)
        {
            long p = e.PayloadOffset;
            int type = comb ? Combined : (int)f.U32(p + 0x08);
            int stride = comb ? 0x20 : 0x80;
            int n = (int)(e.Size / stride);
            var keys = new Key[n];
            for (int i = 0; i < n; i++)
            {
                long k = p + i * stride;
                int fmt = comb ? 3 : f.U16(k + 0x02);
                keys[i] = new Key(fmt, (int)f.U32(k + 0x04), k, comb);
            }
            return new Fx(-1, type, comb, keys);
        }

        /// <summary>Total frames in the track (sum of key durations).</summary>
        public static int FxLength(Fx fx) => fx.Keys.Sum(k => k.Duration);

        /// <summary>Evaluate the track at a frame (loops; noclip runOnce=false).
        /// Returns the vec3: translation (MOTION), euler radians (ROTATION),
        /// or shader params (PARAMETER). Hermite spline uses slots 2,3 as
        /// endpoints and 0,1 as tangents (noclip getPointHermite order).</summary>
        public static float[] Eval(Map1File f, Fx fx, int frame)
        {
            int len = Math.Max(1, FxLength(fx));
            frame = ((frame % len) + len) % len;
            var k = fx.Keys[^1];
            int acc = 0;
            foreach (var kk in fx.Keys)
            {
                if (frame < acc + kk.Duration) { k = kk; break; }
                acc += kk.Duration;
            }
            float t = (frame - acc) / (float)k.Duration;
            var v = new float[3];
            switch (k.Format)
            {
                case 0: // LINEAR — endpoints slots 0,1
                    for (int i = 0; i < 3; i++)
                        v[i] = Lerp(Rd(f, k, 0, i), Rd(f, k, 1, i), t);
                    break;
                case 1: // SPLINE — tangents 0,1; endpoints 2,3
                    float t2 = t * t, t3 = t2 * t;
                    for (int i = 0; i < 3; i++)
                    {
                        float p0 = Rd(f, k, 2, i), p1 = Rd(f, k, 3, i);
                        float s0 = Rd(f, k, 0, i), s1 = Rd(f, k, 1, i);
                        v[i] = (2 * t3 - 3 * t2 + 1) * p0 + (t3 - 2 * t2 + t) * s0
                             + (-2 * t3 + 3 * t2) * p1 + (t3 - t2) * s1;
                    }
                    break;
                default: // CONSTANT — slot 0
                    for (int i = 0; i < 3; i++) v[i] = Rd(f, k, 0, i);
                    break;
            }
            return v;
        }

        /// <summary>COMBINED key at frame: (euler radians, pos delta) —
        /// constant per key, no interpolation (noclip applies directly).</summary>
        public static (float[] Euler, float[] Pos) EvalCombined(Map1File f,
            Fx fx, int frame)
        {
            int acc = 0;
            var k = fx.Keys[^1];
            foreach (var kk in fx.Keys)
            {
                if (frame < acc + kk.Duration) { k = kk; break; }
                acc += kk.Duration;
            }
            var e = new float[3]; var p2 = new float[3];
            for (int i = 0; i < 3; i++)
            {
                e[i] = f.F32(k.Offs + 0x08 + 4 * i);
                p2[i] = f.F32(k.Offs + 0x14 + 4 * i);
            }
            return (e, p2);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Rd(Map1File f, Key k, int slot, int comp)
            => f.F32(k.VecOff(slot) + 4 * comp);

        /// <summary>Patch a keyframe vec3 slot + optional duration in
        /// place — bounds-checked against the entry payload.</summary>
        public static byte[] SetKey(Map1File f, SectionEntry e, Key k,
            int vecSlot, float x, float y, float z, int? duration = null)
        {
            if (vecSlot < 0 || vecSlot >= k.VecCount)
                throw new InvalidDataException($"vecSlot {vecSlot}");
            var d = (byte[])f.Data.Clone();
            long vo = k.VecOff(vecSlot);
            if (vo + 12 > e.End)
                throw new InvalidDataException("vec off past payload");
            BitConverter.GetBytes(x).CopyTo(d, (int)vo);
            BitConverter.GetBytes(y).CopyTo(d, (int)vo + 4);
            BitConverter.GetBytes(z).CopyTo(d, (int)vo + 8);
            if (duration.HasValue)
                BitConverter.GetBytes(duration.Value).CopyTo(d, (int)(k.Offs + 4));
            return d;
        }
    }

    public readonly record struct ModelInfo(
        int Index, int RecordCount, long RecordsOffset,
        float BMinX, float BMinY, float BMinZ,
        float BMaxX, float BMaxY, float BMaxZ);

    public static ModelInfo ReadModel(Map1File f, SectionEntry e)
    {
        var p = e.PayloadOffset;
        int count = (int)f.U32(p + 12);
        return new ModelInfo(e.Index, count, p + 64,
            f.F32(p + 32), f.F32(p + 36), f.F32(p + 40),
            f.F32(p + 48), f.F32(p + 52), f.F32(p + 56));
    }

    public readonly record struct LightingInfo(
        int Index, byte R, byte G, byte B, byte A,
        byte FogR, byte FogG, byte FogB,
        float Opacity, float Near, float Far,
        float EnvRotU, float EnvRotP);

    public static LightingInfo ReadLighting(Map1File f, SectionEntry e)
    {
        var p = e.PayloadOffset;
        return new LightingInfo(e.Index,
            f.Data[p], f.Data[p + 1], f.Data[p + 2], f.Data[p + 3],
            f.Data[p + 12], f.Data[p + 13], f.Data[p + 14],
            f.F32(p + 16), f.F32(p + 20), f.F32(p + 24),
            f.F32(p + 28), f.F32(p + 32));
    }
}

/// <summary>Map texture export/replace at the FILE layout level. Upload
/// entries store linear row-major texels (Gs.Upload tiles them into GS
/// memory), so re-encoding is a straight byte write — palettes stay
/// untouched (nearest-color into the texture's own CLUT block).</summary>
public static class MapTexEdit
{
    public sealed class TexInfo
    {
        public required MapTextures.TexEntry Entry;
        public Gs.Tex0? Tex0;          // model-referencing TEX0 (palette binding)
        public int PaletteSlot = -1;   // a in [0,72) with PaletteCbp(pt,a)==Tex0.Cbp
        public int W, H;               // pixel dims (Tex0 when present, else entry)
    }

    /// <summary>Pairs each type-2 upload entry with the TEX0 that references
    /// it (tbp0 match) and resolves the palette slot from its cbp.</summary>
    public static List<TexInfo> List(Map1File texBin, MapModelSet set)
    {
        var mt = MapTextures.Parse(texBin) ?? new MapTextures { Textures = new() };
        var list = new List<TexInfo>();
        foreach (var e in mt.Textures)
        {
            Gs.Tex0? tx = null;
            foreach (var (t0, _) in set.Textures)
                if (t0.Tbp0 == e.Tbp0) { tx = t0; break; }
            int slot = -1;
            if (tx != null && mt.PaletteType >= 0)
                for (int a = 0; a < 72; a++)
                    if (Gs.PaletteCbp(mt.PaletteType, a) == tx.Cbp) { slot = a; break; }
            list.Add(new TexInfo
            {
                Entry = e, Tex0 = tx, PaletteSlot = slot,
                W = tx?.Width ?? e.W, H = tx?.Height ?? e.H,
            });
        }
        return list;
    }

    /// <summary>Decodes an upload entry to RGBA through a scratch GS map —
    /// same path the renderer uses. Returns null when the entry needs a
    /// palette binding it doesn't have (unreferenced indexed texture).</summary>
    public static byte[]? DecodeRgba(Map1File texBin, MapTextures mt, TexInfo ti)
    {
        var gs = mt.BuildGsMap(texBin);
        var e = ti.Entry;
        try
        {
            if (ti.Tex0 is { } tx)
                return tx.Psm switch
                {
                    19 => gs.DecodePSMT8(tx.Tbp0, tx.Tbw, tx.Width, tx.Height, tx.Cbp, tx.Tcc),
                    20 => gs.DecodePSMT4(tx.Tbp0, tx.Tbw, tx.Width, tx.Height, tx.Cbp, tx.Csa, tx.Tcc),
                    27 => gs.DecodePSMT8H(tx.Tbp0, tx.Tbw, tx.Width, tx.Height, tx.Cbp),
                    0 => gs.DecodePSMT32(tx.Tbp0, tx.Tbw, tx.Width, tx.Height),
                    36 => gs.DecodePSMT4HL(tx.Tbp0, tx.Tbw, tx.Width, tx.Height, tx.Cbp, tx.Csa),
                    44 => gs.DecodePSMT4HH(tx.Tbp0, tx.Tbw, tx.Width, tx.Height, tx.Cbp, tx.Csa),
                    _ => null,
                };
            // no TEX0 binding: indexed data without a resolvable palette —
            // file psm 20 is 4bpp packed into 32bpp words, not raw RGBA.
            return null;
        }
        catch { return null; }
    }

    /// <summary>Palette colors addressable by index c as the DECODER sees
    /// them: 256 for 8bpp (swizzled uu/dd via Clut), 16 for 4bpp (nibble +
    /// csa column). Palette blocks overlap in GS memory, so the table must
    /// come from the post-upload GS state — file palette bytes would read
    /// pre-overlap values and break re-encoding.</summary>
    static byte[]? PaletteColors(Map1File texBin, MapTextures mt, TexInfo ti, int count)
    {
        if (mt.PaletteOffset <= 0 || ti.Tex0 is not { } tx) return null;
        var gs = mt.BuildGsMap(texBin);
        var outp = new byte[count * 4];
        for (int c = 0; c < count; c++)
        {
            int p;
            if (count == 256)
            {
                int dd = (224 & c) >> 4;
                if ((8 & c) != 0) dd++;
                int uu = 7 & c;
                if ((16 & c) != 0) uu += 8;
                p = gs.ClutAddr(tx.Cbp, uu, dd);
            }
            else
            {
                int u = ((c >> 3) & 1) + (14 & tx.Csa);
                int x = (7 & c) + ((1 & tx.Csa) << 3);
                p = gs.ClutAddr(tx.Cbp, x, u);
            }
            outp[4 * c] = gs.Mem(p);
            outp[4 * c + 1] = gs.Mem(p + 1);
            outp[4 * c + 2] = gs.Mem(p + 2);
            // effective decoded alpha per TEX0 (decoders double the stored
            // value; tcc==0 and the H variants force opaque) — needed so
            // nearest-color can't swap two same-RGB indices with different
            // alpha and break the round-trip.
            int a = gs.Mem(p + 3);
            outp[4 * c + 3] = tx.Psm switch
            {
                19 or 20 => tx.Tcc == 1 ? (byte)Math.Min(255, 2 * a) : (byte)255,
                27 => (byte)255,
                _ => (byte)Math.Min(255, 2 * a),
            };
        }
        return outp;
    }

    /// <summary>GS address of decoded CLUT index c (same swizzle the
    /// decoder uses: 8bpp uu/dd, 4bpp nibble+csa column).</summary>
    static int ClutAddrFor(Gs gs, Gs.Tex0 tx, int count, int c)
    {
        if (count == 256)
        {
            int dd = (224 & c) >> 4;
            if ((8 & c) != 0) dd++;
            int uu = 7 & c;
            if ((16 & c) != 0) uu += 8;
            return gs.ClutAddr(tx.Cbp, uu, dd);
        }
        int u = ((c >> 3) & 1) + (14 & tx.Csa);
        int x = (7 & c) + ((1 & tx.Csa) << 3);
        return gs.ClutAddr(tx.Cbp, x, u);
    }

    /// <summary>File offset of the texel that supplies GS address gsAddr:
    /// the LAST 16x16 palette upload covering it wins (uploads overlap).
    /// Each palette is 1024B row-major CT32 at PaletteOffset + 1024*a.</summary>
    static int PaletteCellFileOff(MapTextures mt, Gs gs, int gsAddr)
    {
        int pt = mt.PaletteType == 0 ? 4 : mt.PaletteType;
        int hit = -1;
        for (int a = 0; a < 72; a++)
        {
            int cbp = Gs.PaletteCbp(pt, a);
            for (int yy = 0; yy < 16; yy++)
            for (int xx = 0; xx < 16; xx++)
                if (gs.ClutAddr(cbp, xx, yy) == gsAddr)
                    hit = (int)mt.PaletteOffset + 1024 * a + 4 * (yy * 16 + xx);
        }
        return hit;
    }

    /// <summary>Writes one RGBA into the decoded CLUT at index c — patches
    /// the winning palette's file cell so re-decode shows the new color.
    /// Alpha is the STORED byte (decoder doubles it).</summary>
    public static bool WritePaletteColor(Map1File f, MapTextures mt, TexInfo ti,
        int c, byte r, byte g, byte b, byte a, byte[] patched)
    {
        if (mt.PaletteOffset <= 0 || ti.Tex0 is not { } tx) return false;
        var gs = mt.BuildGsMap(f);
        int count = tx.Psm == 19 ? 256 : 16; // GS: 19=PSMT8, 20=PSMT4
        if (c < 0 || c >= count) return false;
        int fo = PaletteCellFileOff(mt, gs, ClutAddrFor(gs, tx, count, c));
        if (fo < 0 || fo + 4 > patched.Length) return false;
        patched[fo] = r; patched[fo + 1] = g; patched[fo + 2] = b; patched[fo + 3] = a;
        return true;
    }

    /// <summary>Multiplies every decoded CLUT color of the texture —
    /// resolves each index to its winning file cell (deduped, since 4bpp
    /// csa columns can alias) so the tint applies exactly once per cell.
    /// Returns how many file cells were touched.</summary>
    public static int TintPalette(Map1File f, MapTextures mt, TexInfo ti,
        float r, float g, float b, float a, byte[] patched)
    {
        if (mt.PaletteOffset <= 0 || ti.Tex0 is not { } tx) return 0;
        var gs = mt.BuildGsMap(f);
        int count = tx.Psm == 19 ? 256 : 16; // GS: 19=PSMT8, 20=PSMT4
        var seen = new HashSet<int>();
        int n = 0;
        for (int c = 0; c < count; c++)
        {
            int fo = PaletteCellFileOff(mt, gs, ClutAddrFor(gs, tx, count, c));
            if (fo < 0 || fo + 4 > patched.Length || !seen.Add(fo)) continue;
            patched[fo]     = (byte)Math.Clamp((int)(patched[fo]     * r), 0, 255);
            patched[fo + 1] = (byte)Math.Clamp((int)(patched[fo + 1] * g), 0, 255);
            patched[fo + 2] = (byte)Math.Clamp((int)(patched[fo + 2] * b), 0, 255);
            patched[fo + 3] = (byte)Math.Clamp((int)(patched[fo + 3] * a), 0, 255);
            n++;
        }
        return n;
    }

    /// <summary>File psm 20 packs 4bpp texels into CT32 words: the upload
    /// walks an (e.W × e.H) CT32 grid but PSMT4 decodes over (ti.W × ti.H)
    /// — invert GS byte -> file byte so Replace writes the right texel.</summary>
    static Dictionary<int, int> Psm20FileMap(MapTextures.TexEntry e)
    {
        var inv = new Dictionary<int, int>();
        int pos = 0;
        for (int yy = 0; yy < e.H; yy++)
        for (int xx = 0; xx < e.W; xx++)
        {
            int a = Gs.AddrCt32(e.Tbp0, e.Tbw, xx, yy);
            for (int k = 0; k < 4; k++) inv[a + k] = pos + k;
            pos += 4;
        }
        return inv;
    }

    static int Nearest(byte[] pal, int n, int r, int g, int b, int a)
    {
        int best = 0, bd = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int dr = pal[4 * i] - r, dg = pal[4 * i + 1] - g, db = pal[4 * i + 2] - b;
            int da = pal[4 * i + 3] - a;
            int d = dr * dr + dg * dg + db * db + da * da;
            if (d < bd) { bd = d; best = i; if (d == 0) break; }
        }
        return best;
    }

    /// <summary>Re-encodes a BGRA image into the upload entry's linear file
    /// layout in-place on <paramref name="patched"/> (a writable copy of the
    /// texture bin). Indexed formats quantize to the texture's own palette.
    /// Returns a status note (palette/alpha limits), or throws on mismatch.</summary>
    public static string Replace(Map1File texBin, MapTextures mt, TexInfo ti,
        byte[] bgra, int w, int h, byte[] patched)
    {
        var e = ti.Entry;
        if (w != ti.W || h != ti.H)
            throw new InvalidDataException($"dimensão {w}x{h} != textura {ti.W}x{ti.H}");
        long dst = e.DataOffset;
        // file psm 20 packs 4bpp texels into 32bpp words (8 nibbles/word) —
        // the byte stream is still 2 texels/byte like the other 4bpp forms.
        bool is4 = e.Psm is 20 or 36 or 44;
        long bytes = is4 ? (long)w * h / 2 : (long)w * h;
        if (dst < 0 || dst + bytes > patched.Length)
            throw new InvalidDataException("dataOffset fora do bin");
        if (ti.Tex0 is { Psm: 0 })
        {
            // genuine PSMCT32 upload (rare — file psm would be 0 too)
            for (int i = 0, o = (int)dst; i < w * h; i++, o += 4)
            {
                patched[o] = bgra[4 * i + 2]; patched[o + 1] = bgra[4 * i + 1];
                patched[o + 2] = bgra[4 * i]; patched[o + 3] = bgra[4 * i + 3];
            }
            return "32bpp direto";
        }
        var pal = PaletteColors(texBin, mt, ti, is4 ? 16 : 256)
            ?? throw new InvalidDataException("textura indexada sem TEX0/paleta resolvida");
        if (is4)
        {
            if (e.Psm == 20 && ti.Tex0 is { } tx4)
            {
                // packed-32 upload: file byte per texel goes through the
                // CT32 grid inverse map; nibble select comes from AM.
                var inv = Psm20FileMap(e);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int idx = Nearest(pal, 16, bgra[4 * i + 2], bgra[4 * i + 1], bgra[4 * i], bgra[4 * i + 3]);
                    int r = Gs.AddrPsmt4(tx4.Tbp0, tx4.Tbw, x, y);
                    long o = dst + inv[r >> 1];
                    patched[o] = (byte)((r & 1) != 0
                        ? (patched[o] & 0x0F) | (idx << 4)
                        : (patched[o] & 0xF0) | idx);
                }
                return "4bpp/32-packed — 16 cores da CLUT (alpha preservado)";
            }
            // file nibble order (Gs.Upload): psm 44 even texel = low
            // nibble, odd = high; psm 36 is the opposite order.
            for (int i = 0; i < w * h; i++)
            {
                int idx = Nearest(pal, 16, bgra[4 * i + 2], bgra[4 * i + 1], bgra[4 * i], bgra[4 * i + 3]);
                long o = dst + (i >> 1);
                bool high = (i & 1) != 0;
                patched[o] = (byte)(high
                    ? (patched[o] & 0x0F) | (idx << 4)
                    : (patched[o] & 0xF0) | idx);
            }
            return "4bpp — 16 cores da CLUT existente (alpha da paleta preservado)";
        }
        for (int i = 0; i < w * h; i++)
            patched[dst + i] = (byte)Nearest(pal, 256,
                bgra[4 * i + 2], bgra[4 * i + 1], bgra[4 * i], bgra[4 * i + 3]);
        return "8bpp — 256 cores da CLUT existente (alpha da paleta preservado)";
    }
}
