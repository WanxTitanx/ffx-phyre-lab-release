// phyre-patcher — P10 minimal preserving write route for ffx-phyre-lab.
// Region-exact writer for Phyre .phyre clusters (RYHP/* platform tags).
//
// Region map (all offsets derived from header fields, matching PhyreLib):
//   [0, headerSize+8)                    header
//   [headerSize+8, +nsSize)              class namespace (self-describing)
//   [ns end, +objectBlockCount*36)       object block table
//   [table end, +sum(block.DataSize))    per-block object/array data
//   [data end, extBase)                  sharedData + header-class tables
//                                       + object/array/object-array links
//   [extBase, +indicesSize)              external index data
//   [vertexBase, +verticesSize)          external vertex data
//   [fileEnd, file size)                 opaque trailing resource payload
//                                       (e.g. texture pixel data in .dds.phyre);
//                                       not modelled by header fields — copied
//                                       verbatim, patchable as data
//
// Commands:
//   verify  <in.phyre>                        region map + coverage report
//   rewrite <in.phyre> <out.phyre>            re-emit regions; must equal input
//   patch   <in.phyre> <out.phyre> <off> <hex>
//   patch-file <in.phyre> <out.phyre> <off> <payload.bin>
//        same-size byte patch restricted to data/index/vertex/tail regions;
//        header, namespace, block table, links and shared data are off-limits.
//
// Exit codes: 0 ok; 2 parse/structure failure; 3 unsafe patch; 4 IO/usage.

using System.Text.Json;
using PhyreLib;

static int Fail(int code, string msg)
{
    Console.Error.WriteLine(msg);
    return code;
}

// WHY: texture clusters (.dds.phyre) append a raw resource payload after the
// header-described regions that no header field sizes; treat it as an opaque
// tail so rewrite stays byte-exact instead of failing the tile check.
static long[] RegionMap(byte[] bytes)
{
    var p = Phyre.LoadBytes(bytes);
    var h = p.Header;
    long header = h.HeaderSize + 8;
    long nsEnd = h.HeaderSize + h.NamespaceSize;
    long tableEnd = nsEnd + h.ObjectBlockCount * 36L;
    long dataEnd = tableEnd;
    foreach (var b in p.Blocks) dataEnd += b.DataSize;
    long extBase = dataEnd + h.ArrayLinkSize + h.ObjectLinkSize + h.ObjectArrayLinkSize
        + h.HeaderClassObjectBlockCount * 4L + h.HeaderClassChildCount * 16L
        + h.SharedDataSize + h.SharedDataCount * 12L;
    long vertexBase = extBase + h.IndicesSize;
    long fileEnd = vertexBase + h.VerticesSize;
    if (fileEnd > bytes.Length)
        throw new InvalidDataException($"region map past EOF: end={fileEnd} size={bytes.Length}");
    return new[] { header, nsEnd, tableEnd, dataEnd, extBase, vertexBase, fileEnd, bytes.Length };
}

static int CmdVerify(string path)
{
    var bytes = File.ReadAllBytes(path);
    var r = RegionMap(bytes);
    var p = Phyre.LoadBytes(bytes);
    var covered = r[6] == bytes.Length;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        file = path,
        size = bytes.Length,
        regions = new
        {
            header = new[] { 0L, r[0] },
            classNamespace = new[] { r[0], r[1] },
            objectBlockTable = new[] { r[1], r[2] },
            blockData = new[] { r[2], r[3] },
            linksAndMeta = new[] { r[3], r[4] },
            indices = new[] { r[4], r[5] },
            vertices = new[] { r[5], r[6] },
            tail = new[] { r[6], r[7] },
        },
        fullyTiled = covered,
        objectBlocks = p.Blocks.Length,
        platformTag = $"{(char)(p.Header.PlatformId & 0xff)}{(char)(p.Header.PlatformId >> 8 & 0xff)}{(char)(p.Header.PlatformId >> 16 & 0xff)}{(char)(p.Header.PlatformId >> 24)}",
    }, new JsonSerializerOptions { WriteIndented = true }));
    return covered ? 0 : 2;
}

static int CmdRewrite(string src, string dst)
{
    var bytes = File.ReadAllBytes(src);
    var r = RegionMap(bytes);
    // re-emit each region in order from the parsed map (incl. opaque tail)
    using var w = new MemoryStream();
    long[] starts = { 0, r[0], r[1], r[2], r[3], r[4], r[5], r[6] };
    long[] ends = { r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7] };
    for (int i = 0; i < starts.Length; i++)
        w.Write(bytes, (int)starts[i], (int)(ends[i] - starts[i]));
    var outBytes = w.ToArray();
    File.WriteAllBytes(dst, outBytes);
    bool identical = outBytes.AsSpan().SequenceEqual(bytes);
    Console.WriteLine(JsonSerializer.Serialize(new { src, dst, bytes = outBytes.Length, byteExact = identical }));
    return identical ? 0 : 2;
}

static int CmdPatch(string src, string dst, long off, byte[] patch)
{
    var bytes = File.ReadAllBytes(src);
    var r = RegionMap(bytes);
    if (off < 0 || off + patch.Length > bytes.Length)
        return Fail(3, "patch out of bounds");
    // writable regions: blockData, indices, vertices and the opaque tail
    bool writable = (off >= r[2] && off + patch.Length <= r[3])
        || (off >= r[4] && off + patch.Length <= r[7]);
    if (!writable)
        return Fail(3, $"patch [{off},{off + patch.Length}) outside writable regions (data/indices/vertices)");
    var outBytes = (byte[])bytes.Clone();
    Array.Copy(patch, 0, outBytes, off, patch.Length);
    File.WriteAllBytes(dst, outBytes);
    // reopen: structural re-parse of the output must still succeed
    var reparsed = Phyre.LoadBytes(outBytes);
    var before = Phyre.LoadBytes(bytes);
    var sameShape = before.Blocks.Length == reparsed.Blocks.Length
        && before.Blocks.Select(b => (b.Name, b.ElemCount, b.DataSize))
            .SequenceEqual(reparsed.Blocks.Select(b => (b.Name, b.ElemCount, b.DataSize)));
    if (!sameShape)
        return Fail(2, "re-parse of patched file diverges structurally");
    var diffs = bytes.Zip(outBytes).Select((t, i) => (t, i)).Where(t => t.t.First != t.t.Second).Select(t => t.i).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        src, dst, patchOffset = off, patchBytes = patch.Length,
        diffRanges = new[] { new[] { (long)diffs.Min(), (long)diffs.Max() + 1 } },
        diffCount = diffs.Length, reparsedBlocks = reparsed.Blocks.Length, structurePreserved = sameShape,
    }));
    return 0;
}

try
{
    if (args.Length < 2) return Fail(4, "usage: verify|rewrite|patch ...");
    switch (args[0])
    {
        case "verify": return CmdVerify(args[1]);
        case "rewrite": return args.Length >= 3 ? CmdRewrite(args[1], args[2]) : Fail(4, "rewrite needs dst");
        case "patch":
            if (args.Length < 5) return Fail(4, "patch <in> <out> <offset> <hexbytes>");
            return CmdPatch(args[1], args[2], long.Parse(args[3]), Convert.FromHexString(args[4]));
        case "patch-file":
            if (args.Length < 5) return Fail(4, "patch-file <in> <out> <offset> <payload.bin>");
            return CmdPatch(args[1], args[2], long.Parse(args[3]), File.ReadAllBytes(args[4]));
        default: return Fail(4, $"unknown command {args[0]}");
    }
}
catch (Exception ex)
{
    return Fail(2, $"error: {ex.GetType().Name}: {ex.Message}");
}
