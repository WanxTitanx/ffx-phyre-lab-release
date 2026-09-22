using System.Text;

// Phyre parser core — extracted VERBATIM from the proven animdump/Program.cs link decoder.
namespace PhyreLib;

public sealed class Phyre
{
    public byte[] Bytes = Array.Empty<byte>();
    public PhyreHeader Header = null!;
    public PhyreObjectBlock[] Blocks = Array.Empty<PhyreObjectBlock>();
    public PhyreSharedData[] SharedData = Array.Empty<PhyreSharedData>();
    public long ExternalIndexBase;
    public long VertexBase;

    public PhyreObjectBlock? Block(string name) => Blocks.FirstOrDefault(b => b.Name == name);

    public float F(long o) => BitConverter.ToSingle(Bytes, (int)o);
    public int I(long o) => BitConverter.ToInt32(Bytes, (int)o);
    public uint U(long o) => BitConverter.ToUInt32(Bytes, (int)o);

    public static Phyre Load(string path) => LoadBytes(File.ReadAllBytes(path));

    // Lab extension (P10): parse from an in-memory buffer.
    public static Phyre LoadBytes(byte[] bytes)
    {
        var header = ReadHeader(bytes);
        var (blocks, sharedDataOffset, _, _) = LoadObjectBlocks(bytes, header);
        var sharedData = LoadSharedData(bytes, header, blocks, sharedDataOffset);
        long extBase = ExternalIndexBaseCalc(header, blocks);
        long vBase = extBase + header.IndicesSize;
        return new Phyre { Bytes = bytes, Header = header, Blocks = blocks, SharedData = sharedData, ExternalIndexBase = extBase, VertexBase = vBase };
    }

    static long ExternalIndexBaseCalc(PhyreHeader header, PhyreObjectBlock[] objectBlocks)
    {
        var last = objectBlocks[^1];
        return last.DataOffset + last.DataSize
            + header.ArrayLinkSize
            + header.ObjectLinkSize
            + header.ObjectArrayLinkSize
            + header.HeaderClassObjectBlockCount * 4L
            + header.HeaderClassChildCount * 16L
            + header.SharedDataSize
            + header.SharedDataCount * 12L;
    }

    static PhyreHeader ReadHeader(byte[] bytes)
    {
        var c = new Cur(bytes);
        return new PhyreHeader(
            c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(),
            c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32(), c.U32());
    }

    static (PhyreObjectBlock[] blocks, long sharedDataOffset, long objectLinkOffset, long arrayLinkOffset) LoadObjectBlocks(byte[] bytes, PhyreHeader header)
    {
        var cursor = new Cur(bytes);
        cursor.Seek((int)header.HeaderSize + 8);
        var namespaceTypeCount = cursor.I32();
        var namespaceClassCount = cursor.I32();
        var namespaceClassDataMemberCount = cursor.I32();
        cursor.Skip(12 + namespaceTypeCount * 4);

        var nameOffsetsOffset = cursor.Position;
        var nameOffsets = new int[namespaceClassCount];
        for (var index = 0; index < namespaceClassCount; index++)
        {
            cursor.Seek(nameOffsetsOffset + index * 36 + 8);
            nameOffsets[index] = cursor.I32();
        }

        var namesBase = nameOffsetsOffset + 36 * namespaceClassCount + namespaceClassDataMemberCount * 24;
        var names = new string[namespaceClassCount];
        for (var index = 0; index < namespaceClassCount; index++)
            names[index] = cursor.Ascii(namesBase + nameOffsets[index]);

        cursor.Seek((int)(header.HeaderSize + header.NamespaceSize));
        var objectBlocksDataOffset = cursor.Position + 36L * header.ObjectBlockCount;
        var blocks = new List<PhyreObjectBlock>((int)header.ObjectBlockCount);
        for (var index = 0; index < header.ObjectBlockCount; index++)
        {
            var block = new PhyreObjectBlock
            {
                Id = index,
                NameId = cursor.I32(),
                ElemCount = cursor.I32(),
                DataSize = cursor.I32(),
                ObjectsSize = cursor.I32(),
                ArraysSize = cursor.I32(),
            };
            cursor.Skip(4);
            var arrayLinkCount = cursor.I32();
            var objectLinkCount = cursor.I32();
            block.DataOffset = objectBlocksDataOffset;
            block.ElemSize = block.ElemCount == 0 ? 0 : block.ObjectsSize / block.ElemCount;
            block.Name = block.NameId > 0 && block.NameId <= names.Length ? names[block.NameId - 1] : $"name_id_{block.NameId}";
            block.ArrayLinksExpected = arrayLinkCount;
            block.ObjectLinksExpected = objectLinkCount;
            objectBlocksDataOffset += block.DataSize;
            blocks.Add(block);
            cursor.Skip(4);
        }

        var sharedDataOffset = blocks[^1].DataOffset + blocks[^1].DataSize;
        var objectLinkOffset = objectBlocksDataOffset
            + header.SharedDataCount * 12L
            + header.SharedDataSize
            + header.HeaderClassObjectBlockCount * 4L
            + header.HeaderClassChildCount * 16L
            + header.ObjectArrayLinkSize;
        var arrayLinkOffset = objectLinkOffset + header.ObjectLinkSize;

        cursor.Seek((int)objectLinkOffset);
        foreach (var block in blocks)
            LoadObjectLinks(cursor, block.ObjectLinks, block.ObjectLinksExpected, (uint)Math.Max(0, block.ElemCount));

        cursor.Seek((int)arrayLinkOffset);
        foreach (var block in blocks)
            LoadArrayLinks(cursor, block.ArrayLinks, block.ArrayLinksExpected, (uint)Math.Max(0, block.ElemCount));

        return (blocks.ToArray(), sharedDataOffset, objectLinkOffset, arrayLinkOffset);
    }

    static PhyreSharedData[] LoadSharedData(byte[] bytes, PhyreHeader header, PhyreObjectBlock[] objectBlocks, long sharedDataOffset)
    {
        var cursor = new Cur(bytes);
        var values = new PhyreSharedData[header.SharedDataCount];
        for (var index = 0; index < values.Length; index++)
        {
            cursor.Seek((int)(sharedDataOffset + header.SharedDataSize + index * 12L));
            var rawType = cursor.U32();
            var size = cursor.U32();
            var offset = cursor.U32();
            var data = new byte[size];
            Buffer.BlockCopy(bytes, (int)(sharedDataOffset + offset), data, 0, (int)size);
            values[index] = new PhyreSharedData(rawType, data);
        }
        return values;
    }

    const uint SkipArrayCount = 1 << 3, SkipSharedDataId = 1 << 4, SkipObjectBlockId = 1 << 5, SkipObjectOffset = 1 << 6;

    static void LoadObjectLinks(Cur cursor, List<PhyreObjectLink> links, int expectedCount, uint elemCount)
    {
        while (links.Count < expectedCount)
        {
            var loadTypeAndMask = cursor.Byte();
            var loadType = loadTypeAndMask & 7;
            var mask = (uint)loadTypeAndMask & ~7u;
            var baseLink = new PhyreObjectLink();
            LoadParentObjOffset(cursor, baseLink);
            if ((mask & SkipObjectBlockId) != 0) baseLink.ObjBlockId = cursor.Var();
            RunObjectLinkLoader(cursor, links, loadType, elemCount, mask, baseLink, false);
        }
    }

    static void LoadArrayLinks(Cur cursor, List<PhyreArrayLink> links, int expectedCount, uint elemCount)
    {
        while (links.Count < expectedCount)
        {
            var loadTypeAndMask = cursor.Byte();
            var loadType = loadTypeAndMask & 7;
            var mask = (uint)loadTypeAndMask & ~7u;
            var baseLink = new PhyreArrayLink();
            LoadParentObjOffset(cursor, baseLink);
            RunArrayLinkLoader(cursor, links, loadType, elemCount, mask, baseLink, false);
        }
    }

    static void RunObjectLinkLoader(Cur cursor, List<PhyreObjectLink> links, int loadType, uint elemCount, uint mask, PhyreObjectLink baseLink, bool parentOnly)
    {
        switch (loadType)
        {
            case 0:
                for (uint i = 0; i < elemCount; i++) links.Add(ReadObjectLink(cursor, baseLink, i, mask, parentOnly));
                break;
            case 1:
                var t = links.Count + (int)elemCount;
                while (links.Count < t) { var g = cursor.Byte(); LoadObjectDst(cursor, baseLink, mask); RunObjectLinkLoader(cursor, links, g, elemCount, mask, baseLink, true); }
                break;
            case 2:
                foreach (var id in LoadIdList(cursor, elemCount)) links.Add(ReadObjectLink(cursor, baseLink, id, mask, parentOnly));
                break;
            case 3:
                var ex = LoadIdList(cursor, elemCount).ToHashSet();
                for (uint i = 0; i < elemCount; i++) if (!ex.Contains(i)) links.Add(ReadObjectLink(cursor, baseLink, i, mask, parentOnly));
                break;
            case 4:
                var bm = cursor.Bytes((int)((elemCount >> 3) + ((elemCount & 7) == 0 ? 0 : 1)));
                for (uint i = 0; i < elemCount; i++) if ((bm[i / 8] & (1 << (int)(i % 8))) != 0) links.Add(ReadObjectLink(cursor, baseLink, i, mask, parentOnly));
                break;
            case 5:
                var lc = cursor.Var();
                for (uint i = 0; i < lc; i++) { var p = elemCount > 1 ? cursor.Var() : baseLink.ParentObjId; links.Add(ReadObjectLink(cursor, baseLink, p, mask, parentOnly)); }
                break;
            case 6:
                var pid = cursor.Var(); var stride = cursor.Var(); var cnt = cursor.Var();
                for (uint i = 0; i < cnt; i++, pid += stride) links.Add(ReadObjectLink(cursor, baseLink, pid, mask, parentOnly));
                break;
            default: throw new InvalidOperationException($"obj loadType {loadType}");
        }
    }

    static void RunArrayLinkLoader(Cur cursor, List<PhyreArrayLink> links, int loadType, uint elemCount, uint mask, PhyreArrayLink baseLink, bool parentOnly)
    {
        switch (loadType)
        {
            case 0:
                for (uint i = 0; i < elemCount; i++) links.Add(ReadArrayLink(cursor, baseLink, i, mask, parentOnly));
                break;
            case 1:
                var t = links.Count + (int)elemCount;
                while (links.Count < t) { var g = cursor.Byte(); LoadArrayDst(cursor, baseLink, mask); RunArrayLinkLoader(cursor, links, g, elemCount, mask, baseLink, true); }
                break;
            case 2:
                foreach (var id in LoadIdList(cursor, elemCount)) links.Add(ReadArrayLink(cursor, baseLink, id, mask, parentOnly));
                break;
            case 3:
                var ex = LoadIdList(cursor, elemCount).ToHashSet();
                for (uint i = 0; i < elemCount; i++) if (!ex.Contains(i)) links.Add(ReadArrayLink(cursor, baseLink, i, mask, parentOnly));
                break;
            case 4:
                var bm = cursor.Bytes((int)((elemCount >> 3) + ((elemCount & 7) == 0 ? 0 : 1)));
                for (uint i = 0; i < elemCount; i++) if ((bm[i / 8] & (1 << (int)(i % 8))) != 0) links.Add(ReadArrayLink(cursor, baseLink, i, mask, parentOnly));
                break;
            case 5:
                var lc = cursor.Var();
                for (uint i = 0; i < lc; i++) { var p = elemCount > 1 ? cursor.Var() : baseLink.ParentObjId; links.Add(ReadArrayLink(cursor, baseLink, p, mask, parentOnly)); }
                break;
            case 6:
                var pid = cursor.Var(); var stride = cursor.Var(); var cnt = cursor.Var();
                for (uint i = 0; i < cnt; i++, pid += stride) links.Add(ReadArrayLink(cursor, baseLink, pid, mask, parentOnly));
                break;
            default: throw new InvalidOperationException($"arr loadType {loadType}");
        }
    }

    static PhyreObjectLink ReadObjectLink(Cur cursor, PhyreObjectLink baseLink, uint index, uint mask, bool parentOnly)
    {
        var link = baseLink.Clone();
        link.ParentObjId = index;
        if (!parentOnly) LoadObjectDst(cursor, link, mask);
        return link;
    }

    static PhyreArrayLink ReadArrayLink(Cur cursor, PhyreArrayLink baseLink, uint index, uint mask, bool parentOnly)
    {
        var link = baseLink.Clone();
        link.ParentObjId = index;
        if (!parentOnly) LoadArrayDst(cursor, link, mask);
        return link;
    }

    static void LoadParentObjOffset(Cur cursor, PhyreFileLink link)
    {
        var value = cursor.Var();
        link.ParentOffsetFlag = value & 1;
        if (link.ParentOffsetFlag != 0) link.ParentFieldOffset = value >> 1;
        else link.ParentObjOffset = value >> 1;
    }

    static void LoadObjectDst(Cur cursor, PhyreObjectLink link, uint mask)
    {
        link.SharedDataId = (mask & SkipSharedDataId) != 0 ? 0 : cursor.Var();
        if (link.SharedDataId == 0)
        {
            link.ObjId = cursor.Var();
            if ((mask & SkipObjectBlockId) == 0) link.ObjBlockId = cursor.Var();
            if ((mask & SkipObjectOffset) == 0) link.ObjOffset = cursor.Var();
        }
        link.SharedDataId -= 1;
        if ((mask & SkipArrayCount) == 0) link.ObjArrayCount = cursor.Var();
    }

    static void LoadArrayDst(Cur cursor, PhyreArrayLink link, uint mask)
    {
        if ((mask & SkipArrayCount) == 0) link.Count = cursor.Var();
        link.Offset = cursor.Var();
    }

    static uint[] LoadIdList(Cur cursor, uint elemCount)
    {
        var count = cursor.Var();
        var values = new uint[count];
        for (var i = 0; i < values.Length; i++) values[i] = (elemCount >> 8) != 0 ? cursor.Var() : cursor.Byte();
        return values;
    }
}

public sealed class Cur
{
    private readonly byte[] b;
    public Cur(byte[] b, int pos = 0) { this.b = b; Position = pos; }
    public int Position { get; private set; }
    public void Seek(int p) => Position = p;
    public void Skip(int n) => Position += n;
    public byte Byte() => b[Position++];
    public byte[] Bytes(int n) { var d = new byte[n]; Buffer.BlockCopy(b, Position, d, 0, n); Position += n; return d; }
    public int I32() { var v = BitConverter.ToInt32(b, Position); Position += 4; return v; }
    public uint U32() { var v = BitConverter.ToUInt32(b, Position); Position += 4; return v; }
    public uint Var() { uint v = 0; int s = 0; byte r; do { r = Byte(); v |= (uint)(r & 127) << s; s += 7; } while ((r & 128) != 0); return v; }
    public string Ascii(int off) { var e = off; while (e < b.Length && b[e] != 0) e++; return Encoding.ASCII.GetString(b, off, e - off); }
}

public record PhyreHeader(uint MagicBytes, uint HeaderSize, uint NamespaceSize, uint PlatformId, uint ObjectBlockCount,
    uint ArrayLinkSize, uint ArrayLinkCount, uint ObjectLinkSize, uint ObjectLinkCount, uint ObjectArrayLinkSize,
    uint ObjectArrayLinkCount, uint ObjectsInArraysCount, uint SharedDataCount, uint SharedDataSize, uint BlockDataSize,
    uint HeaderClassObjectBlockCount, uint HeaderClassChildCount, uint PhysicsEngineId, uint IndicesSize, uint VerticesSize, uint MaxTextureSize);

public class PhyreFileLink
{
    public uint ParentObjId, ParentObjOffset, ParentFieldOffset, ParentOffsetFlag;
}

public sealed class PhyreArrayLink : PhyreFileLink
{
    public uint Offset, Count;
    public PhyreArrayLink Clone() => new() { ParentObjId = ParentObjId, ParentObjOffset = ParentObjOffset, ParentFieldOffset = ParentFieldOffset, ParentOffsetFlag = ParentOffsetFlag, Offset = Offset, Count = Count };
}

public sealed class PhyreObjectLink : PhyreFileLink
{
    public uint ObjId, ObjOffset, ObjBlockId, ObjArrayCount, SharedDataId;
    public PhyreObjectLink Clone() => new() { ParentObjId = ParentObjId, ParentObjOffset = ParentObjOffset, ParentFieldOffset = ParentFieldOffset, ParentOffsetFlag = ParentOffsetFlag, ObjId = ObjId, ObjOffset = ObjOffset, ObjBlockId = ObjBlockId, ObjArrayCount = ObjArrayCount, SharedDataId = SharedDataId };
}

public sealed class PhyreObjectBlock
{
    public int Id, NameId, ElemCount, DataSize, ObjectsSize, ArraysSize, ElemSize, ObjectLinksExpected, ArrayLinksExpected;
    public long DataOffset;
    public string Name = "";
    public List<PhyreObjectLink> ObjectLinks = new();
    public List<PhyreArrayLink> ArrayLinks = new();
}

public sealed record PhyreSharedData(uint RawType, byte[] Data)
{
    public string AsString()
    {
        var len = Array.IndexOf(Data, (byte)0);
        if (len < 0) len = Data.Length;
        return Encoding.ASCII.GetString(Data, 0, len);
    }
}
