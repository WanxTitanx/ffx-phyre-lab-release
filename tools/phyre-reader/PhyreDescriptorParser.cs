// Provenance: ffx-editor-main RuntimeTools/PhyreModelExportLab/PhyreDescriptorParser.cs
// snapshot 3da386821a3134d291d8bece769d3a4f5c6bde49934c8afc46429eea2d7e276a (P01); GPLv3 (THIRD_PARTY.md).
// Adaptacao: Program.ToPortablePath -> PortablePath.Invoke (helper local); demais conteudo identico.

using System.Security.Cryptography;
using System.Text;

namespace PhyreReader;

internal static class PortablePath
{
    public static string Invoke(string path) => path;
}


public static class PhyreDescriptorParser
{
    private const uint SkipArrayCount = 1 << 3;
    private const uint SkipSharedDataId = 1 << 4;
    private const uint SkipObjectBlockId = 1 << 5;
    private const uint SkipObjectOffset = 1 << 6;

    private static readonly string[] PrimNames =
    {
        "Float",
        "Half",
        "UInt32",
        "UInt16",
        "UInt8",
        "UChar",
        "UNormInt16",
        "UNormInt8",
        "Int32",
        "Int16",
        "Int8",
        "Char",
        "NormInt16",
        "NormInt8",
        "Matrix3x3",
        "Matrix3x4",
        "Matrix4x4",
    };

    public static PhyreDescriptorReport Parse(string monsterId, string daePath, bool portable)
    {
        if (!File.Exists(daePath))
        {
            return PhyreDescriptorReport.BlockedMissingFile(monsterId, FileSnapshot.FromPath(daePath, portable));
        }

        var bytes = File.ReadAllBytes(daePath);
        var warnings = new List<string>();
        try
        {
            var header = ReadHeader(bytes);
            var objectBlocks = LoadObjectBlocks(bytes, header);
            var sharedData = LoadSharedData(bytes, header, objectBlocks);

            var meshBlock = FindObjectBlock(objectBlocks, "PMesh");
            var meshSegmentBlock = FindObjectBlock(objectBlocks, "PMeshSegment");
            var dataBlock = FindObjectBlock(objectBlocks, "PDataBlock");
            var vertexStreamBlock = FindObjectBlock(objectBlocks, "PVertexStream");

            if (meshBlock is null || meshSegmentBlock is null || dataBlock is null || vertexStreamBlock is null)
            {
                return BuildReport(
                    monsterId,
                    daePath,
                    portable,
                    header,
                    objectBlocks,
                    Array.Empty<PhyreSubmeshReport>(),
                    warnings.Append("required descriptor block missing").ToArray(),
                    "blocked_descriptor_blocks_missing",
                    "not_promoted_descriptor_decode_incomplete",
                    "recover PMesh/PMeshSegment/PDataBlock/PVertexStream blocks before emitting GLB");
            }

            var submeshToMesh = BuildSubmeshToMeshMap(objectBlocks, meshBlock);
            var dataBlockLinks = FindMemberObjectLinks(objectBlocks, meshSegmentBlock, "PDataBlock");
            var dataBlocksBySubmesh = dataBlockLinks
                .GroupBy(link => link.ParentObjId)
                .ToDictionary(group => group.Key, group => group.OrderBy(link => link.ObjId).ToArray());
            var vertexStreamLinks = FindMemberObjectLinks(objectBlocks, dataBlock, "PVertexStream");
            var vertexStreamByDataBlock = vertexStreamLinks
                .GroupBy(link => link.ParentObjId)
                .ToDictionary(group => group.Key, group => group.First());

            var submeshes = new List<PhyreSubmeshReport>();
            for (var submeshId = 0; submeshId < meshSegmentBlock.ElemCount; submeshId++)
            {
                var submesh = ReadSubmesh(bytes, header, objectBlocks, meshSegmentBlock, dataBlock, vertexStreamBlock, sharedData, submeshId, submeshToMesh, dataBlocksBySubmesh, vertexStreamByDataBlock, warnings);
                submeshes.Add(submesh);
            }

            var submeshReports = submeshes.ToArray();
            var decisionBand = DetermineDecisionBand(submeshReports);
            var nextStrike = decisionBand == "mesh_descriptor_decode_candidate"
                ? "emit descriptor-derived GLTF by submesh and compare bounds/silhouette against the m020 FBX oracle"
                : "inspect descriptor warnings and avoid visual promotion until indices, vertices, and finite bounds all pass";

            return BuildReport(
                monsterId,
                daePath,
                portable,
                header,
                objectBlocks,
                submeshReports,
                warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                decisionBand,
                "not_promoted_external_visual_validation_required",
                nextStrike);
        }
        catch (Exception exception)
        {
            return new PhyreDescriptorReport(
                DateTimeOffset.UtcNow,
                monsterId,
                FileSnapshot.FromPath(daePath, portable),
                null,
                Array.Empty<ObjectBlockReport>(),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                "blocked_descriptor_parser_exception",
                "not_promoted_parser_failed",
                "fix PhyreDescriptorParser exception before any visual promotion",
                Array.Empty<PhyreSubmeshReport>(),
                warnings.Append(exception.Message).ToArray());
        }
    }

    public static PhyreLinkDumpReport DumpLinks(string assetId, string daePath, bool portable)
    {
        if (!File.Exists(daePath))
        {
            return PhyreLinkDumpReport.BlockedMissingFile(assetId, FileSnapshot.FromPath(daePath, portable));
        }

        var bytes = File.ReadAllBytes(daePath);
        var header = ReadHeader(bytes);
        var objectBlocks = LoadObjectBlocks(bytes, header);
        var sharedData = LoadSharedData(bytes, header, objectBlocks);
        var blockNames = objectBlocks.ToDictionary(static block => (uint)block.Id, static block => block.Name);

        return new PhyreLinkDumpReport(
            DateTimeOffset.UtcNow,
            assetId,
            FileSnapshot.FromPath(daePath, portable),
            objectBlocks.Select(block => new PhyreObjectLinkBlockReport(
                block.Id,
                block.Name,
                block.ElemCount,
                block.ElemSize,
                block.DataSize,
                block.DataOffset,
                block.ObjectLinks.Select(link => ToReport(link, blockNames, sharedData)).ToArray(),
                block.ArrayLinks.Select(static link => new PhyreArrayLinkReport(
                    link.ParentObjId,
                    link.ParentObjOffset,
                    link.ParentFieldOffset,
                    link.ParentOffsetFlag,
                    link.Offset,
                    link.Count)).ToArray())).ToArray(),
            sharedData.Select((item, index) => new PhyreSharedDataReport(
                index,
                item.RawType,
                item.Data.Length,
                item.AsString())).ToArray(),
            "phyre_link_dump_candidate",
            "Use PMaterial/PParameterBuffer/PAssetReferenceImport links to replace sorted material texture binding.");
    }

    public static PhyreStringIndexReport BuildStringIndex(string assetId, string daePath, bool portable)
    {
        if (!File.Exists(daePath))
        {
            return PhyreStringIndexReport.BlockedMissingFile(assetId, FileSnapshot.FromPath(daePath, portable));
        }

        var bytes = File.ReadAllBytes(daePath);
        var strings = ScanInterestingStrings(bytes)
            .Select(item =>
            {
                var role = ClassifyPhyreString(item.Value);
                return new PhyreStringIndexEntry(
                    item.Offset,
                    item.Value,
                    role,
                    ExtractTextureKey(item.Value));
            })
            .Where(static item => item.Role != "unclassified")
            .DistinctBy(static item => (item.Offset, item.Value))
            .ToArray();

        return new PhyreStringIndexReport(
            DateTimeOffset.UtcNow,
            assetId,
            FileSnapshot.FromPath(daePath, portable),
            strings,
            "phyre_string_index_candidate",
            "Cross material names, texture paths, shader parameter names, and block names against link dump.");
    }

    private static PhyreObjectLinkReport ToReport(
        PhyreObjectLink link,
        IReadOnlyDictionary<uint, string> blockNames,
        PhyreSharedData[] sharedData)
    {
        var sharedDataIndex = link.SharedDataId < sharedData.Length ? (int?)link.SharedDataId : null;
        return new PhyreObjectLinkReport(
            link.ParentObjId,
            link.ParentObjOffset,
            link.ParentFieldOffset,
            link.ParentOffsetFlag,
            link.ObjBlockId,
            blockNames.TryGetValue(link.ObjBlockId, out var targetName) ? targetName : $"block_{link.ObjBlockId}",
            link.ObjId,
            link.ObjOffset,
            link.ObjArrayCount,
            sharedDataIndex,
            sharedDataIndex is null ? null : sharedData[sharedDataIndex.Value].RawType,
            sharedDataIndex is null ? null : sharedData[sharedDataIndex.Value].AsString());
    }

    private static IEnumerable<(int Offset, string Value)> ScanInterestingStrings(byte[] bytes)
    {
        var start = -1;
        for (var index = 0; index <= bytes.Length; index++)
        {
            var isAscii = index < bytes.Length && bytes[index] is >= 0x20 and <= 0x7E;
            if (isAscii)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0 && index - start >= 4)
            {
                var value = Encoding.ASCII.GetString(bytes, start, index - start);
                if (IsInterestingPhyreString(value))
                {
                    yield return (start, value);
                }
            }

            start = -1;
        }
    }

    private static bool IsInterestingPhyreString(string value) =>
        value.Contains("azit00", StringComparison.OrdinalIgnoreCase)
        || value.Contains(".dds", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Material", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Texture", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Shader", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Sampler", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Colour", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Color", StringComparison.OrdinalIgnoreCase)
        || value.Contains("globalColorMask", StringComparison.OrdinalIgnoreCase)
        || value.Contains("PAssetReference", StringComparison.Ordinal)
        || value.Contains("PParameterBuffer", StringComparison.Ordinal)
        || value.Contains("PMaterial", StringComparison.Ordinal)
        || value.Contains("PTexture", StringComparison.Ordinal);

    private static string ClassifyPhyreString(string value)
    {
        if (value.Contains(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return "texture_path";
        }

        if (value.StartsWith("_collada_", StringComparison.OrdinalIgnoreCase)
            || (value.Contains("azit00", StringComparison.OrdinalIgnoreCase)
                && value.Contains("clamp", StringComparison.OrdinalIgnoreCase)))
        {
            return "material_name";
        }

        if (value.StartsWith('P') && value.Contains("Material", StringComparison.Ordinal))
        {
            return "object_block_or_type";
        }

        if (value.Contains("Shader", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Colour", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Color", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Sampler", StringComparison.OrdinalIgnoreCase))
        {
            return "shader_parameter_or_state";
        }

        if (value.Contains("PAssetReference", StringComparison.Ordinal)
            || value.Contains("PParameterBuffer", StringComparison.Ordinal)
            || value.Contains("PTexture", StringComparison.Ordinal))
        {
            return "object_block_or_type";
        }

        return "unclassified";
    }

    private static string? ExtractTextureKey(string value)
    {
        var marker = ".dds";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var before = value[..index].Replace('\\', '/');
        var slash = before.LastIndexOf('/');
        return slash >= 0 ? before[(slash + 1)..] : before;
    }

    private static PhyreDescriptorReport BuildReport(
        string monsterId,
        string daePath,
        bool portable,
        PhyreHeader header,
        PhyreObjectBlock[] objectBlocks,
        PhyreSubmeshReport[] submeshes,
        string[] warnings,
        string decisionBand,
        string promotionStatus,
        string nextStrike)
    {
        var meshBlock = FindObjectBlock(objectBlocks, "PMesh");
        var meshSegmentBlock = FindObjectBlock(objectBlocks, "PMeshSegment");
        var dataBlock = FindObjectBlock(objectBlocks, "PDataBlock");
        var vertexStreamBlock = FindObjectBlock(objectBlocks, "PVertexStream");
        var bounds = MergeBounds(submeshes.Select(item => item.PositionBounds).Where(item => item is not null).Cast<PositionBounds>());

        return new PhyreDescriptorReport(
            DateTimeOffset.UtcNow,
            monsterId,
            FileSnapshot.FromPath(daePath, portable),
            new PhyreHeaderReport(
                header.HeaderSize,
                header.NamespaceSize,
                header.ObjectBlockCount,
                header.ArrayLinkSize,
                header.ArrayLinkCount,
                header.ObjectLinkSize,
                header.ObjectLinkCount,
                header.ObjectArrayLinkSize,
                header.ObjectArrayLinkCount,
                header.SharedDataCount,
                header.SharedDataSize,
                header.BlockDataSize,
                header.IndicesSize,
                header.VerticesSize,
                header.MaxTextureSize),
            objectBlocks.Select(block => new ObjectBlockReport(
                block.Id,
                block.Name,
                block.ElemCount,
                block.ElemSize,
                block.DataSize,
                block.DataOffset,
                block.ObjectLinks.Count,
                block.ArrayLinks.Count)).ToArray(),
            meshBlock?.ElemCount ?? 0,
            meshSegmentBlock?.ElemCount ?? 0,
            dataBlock?.ElemCount ?? 0,
            vertexStreamBlock?.ElemCount ?? 0,
            submeshes.Length,
            submeshes.Sum(item => item.IndexCount),
            checked((int)submeshes.Sum(item => item.PositionVertexCount ?? 0)),
            bounds,
            decisionBand,
            promotionStatus,
            nextStrike,
            submeshes,
            warnings);
    }

    private static string DetermineDecisionBand(PhyreSubmeshReport[] submeshes)
    {
        if (submeshes.Length == 0)
        {
            return "blocked_no_submesh_descriptors";
        }

        if (submeshes.Any(item => item.IndexCount <= 0))
        {
            return "blocked_empty_index_descriptor";
        }

        if (submeshes.Any(item => (item.PositionVertexCount ?? 0) <= 0))
        {
            return "blocked_position_stream_missing";
        }

        if (submeshes.Any(item => item.IndexOutOfRangeCount is null or > 0))
        {
            return "blocked_indices_out_of_range";
        }

        if (submeshes.Any(item => item.PositionBounds is null || !HasFiniteBounds(item.PositionBounds)))
        {
            return "blocked_nonfinite_bounds";
        }

        return "mesh_descriptor_decode_candidate";
    }

    private static PhyreSubmeshReport ReadSubmesh(
        byte[] bytes,
        PhyreHeader header,
        PhyreObjectBlock[] objectBlocks,
        PhyreObjectBlock meshSegmentBlock,
        PhyreObjectBlock dataBlock,
        PhyreObjectBlock vertexStreamBlock,
        PhyreSharedData[] sharedData,
        int submeshId,
        IReadOnlyDictionary<uint, uint> submeshToMesh,
        IReadOnlyDictionary<uint, PhyreObjectLink[]> dataBlocksBySubmesh,
        IReadOnlyDictionary<uint, PhyreObjectLink> vertexStreamByDataBlock,
        List<string> warnings)
    {
        var indexBase = GetExternalIndexBase(header, objectBlocks);
        var cursor = new ByteCursor(bytes, checked((int)(meshSegmentBlock.DataOffset + meshSegmentBlock.ElemSize * submeshId)));
        var materialId = cursor.ReadInt32();
        long indexDataOffset;
        int indexCount;
        int indexDataSize;
        int? drawType = null;

        if (meshSegmentBlock.ElemSize == 108)
        {
            cursor.Skip(52);
            indexCount = cursor.ReadInt32();
            cursor.Skip(32);
            indexDataOffset = indexBase + cursor.ReadInt32();
            cursor.Skip(4);
            indexDataSize = cursor.ReadInt32();
        }
        else if (meshSegmentBlock.ElemSize == 61)
        {
            cursor.Skip(9);
            drawType = cursor.ReadByte();
            cursor.Skip(28);
            indexCount = cursor.ReadInt32();
            cursor.Skip(7);
            indexDataOffset = indexBase + cursor.ReadInt32();
            indexDataSize = cursor.ReadInt32();
        }
        else
        {
            throw new InvalidOperationException($"Unsupported PMeshSegment elem size: {meshSegmentBlock.ElemSize}");
        }

        if (indexDataSize != indexCount * 2)
        {
            warnings.Add($"submesh {submeshId}: index data size {indexDataSize} != indexCount*2 {indexCount * 2}");
        }

        var indices = ReadU16Array(bytes, indexDataOffset, indexCount);
        var components = new List<PhyreVertexComponentReport>();
        if (dataBlocksBySubmesh.TryGetValue((uint)submeshId, out var dataLinks))
        {
            foreach (var link in dataLinks)
            {
                var begin = checked((int)link.ObjId);
                var end = checked(begin + (int)Math.Max(1, link.ObjArrayCount));
                for (var dataBlockId = begin; dataBlockId < end; dataBlockId++)
                {
                    if (!vertexStreamByDataBlock.TryGetValue((uint)dataBlockId, out var vertexStreamLink))
                    {
                        warnings.Add($"submesh {submeshId}: PDataBlock {dataBlockId} has no PVertexStream link");
                        continue;
                    }

                    components.Add(ReadVertexComponent(bytes, header, objectBlocks, dataBlock, vertexStreamBlock, sharedData, dataBlockId, vertexStreamLink, warnings));
                }
            }
        }
        else
        {
            warnings.Add($"submesh {submeshId}: no PDataBlock link");
        }

        var position = components.FirstOrDefault(item => item.ComponentType is "Vertex" or "SkinnableVertex" && item.PositionBounds is not null);
        int? outOfRangeCount = null;
        int? maxIndex = indices.Length == 0 ? null : indices.Max(value => (int)value);
        if (position?.ElementCount > 0)
        {
            outOfRangeCount = indices.Count(value => value >= position.ElementCount);
        }

        return new PhyreSubmeshReport(
            submeshId,
            submeshToMesh.TryGetValue((uint)submeshId, out var meshId) ? (int)meshId : null,
            materialId,
            drawType,
            indexCount,
            indexDataOffset,
            indexDataSize,
            maxIndex,
            outOfRangeCount,
            position?.ElementCount,
            position?.PositionBounds,
            components.ToArray());
    }

    private static PhyreVertexComponentReport ReadVertexComponent(
        byte[] bytes,
        PhyreHeader header,
        PhyreObjectBlock[] objectBlocks,
        PhyreObjectBlock dataBlock,
        PhyreObjectBlock vertexStreamBlock,
        PhyreSharedData[] sharedData,
        int dataBlockId,
        PhyreObjectLink vertexStreamLink,
        List<string> warnings)
    {
        var vertexDataBase = GetExternalIndexBase(header, objectBlocks) + header.IndicesSize;
        var cursor = new ByteCursor(bytes, checked((int)(dataBlock.DataOffset + dataBlock.ElemSize * dataBlockId)));
        var elemSize = cursor.ReadUInt32();
        var elemCount = cursor.ReadUInt32();
        long vertexDataOffset;
        int vertexDataSize;

        if (dataBlock.ElemSize == 64)
        {
            cursor.Skip(40);
            vertexDataOffset = vertexDataBase + cursor.ReadUInt32();
            cursor.Skip(4);
            vertexDataSize = cursor.ReadInt32();
        }
        else if (dataBlock.ElemSize == 27)
        {
            cursor.Skip(11);
            vertexDataOffset = vertexDataBase + cursor.ReadUInt32();
            vertexDataSize = cursor.ReadInt32();
        }
        else
        {
            throw new InvalidOperationException($"Unsupported PDataBlock elem size: {dataBlock.ElemSize}");
        }

        if (vertexDataSize != elemCount * elemSize)
        {
            warnings.Add($"PDataBlock {dataBlockId}: vertex data size {vertexDataSize} != elemCount*elemSize {elemCount * elemSize}");
        }

        var vertexStreamId = checked((int)vertexStreamLink.ObjId);
        var componentName = ResolveVertexComponentName(vertexStreamBlock, sharedData, vertexStreamId);
        var streamCursor = new ByteCursor(bytes, checked((int)(vertexStreamBlock.DataOffset + vertexStreamBlock.ElemSize * vertexStreamId)));
        uint rawPrimType;
        if (vertexStreamBlock.ElemSize == 7)
        {
            streamCursor.Skip(5);
            rawPrimType = streamCursor.ReadByte();
        }
        else if (vertexStreamBlock.ElemSize == 12)
        {
            streamCursor.Skip(8);
            rawPrimType = streamCursor.ReadByte();
        }
        else
        {
            throw new InvalidOperationException($"Unsupported PVertexStream elem size: {vertexStreamBlock.ElemSize}");
        }

        var prim = DecodePrim(rawPrimType);
        var positionBounds = TryReadPositionBounds(bytes, vertexDataOffset, checked((int)elemCount), checked((int)elemSize), componentName, prim);

        return new PhyreVertexComponentReport(
            dataBlockId,
            vertexStreamId,
            componentName,
            rawPrimType,
            prim.PrimId,
            prim.ElementCount,
            elemSize,
            elemCount,
            vertexStreamLink.ObjArrayCount,
            vertexDataOffset,
            vertexDataSize,
            positionBounds);
    }

    private static string ResolveVertexComponentName(PhyreObjectBlock vertexStreamBlock, PhyreSharedData[] sharedData, int vertexStreamId)
    {
        var links = vertexStreamBlock.ObjectLinks
            .Where(link => link.ParentObjId == vertexStreamId)
            .ToArray();
        if (links.Length != 1)
        {
            return $"unresolved_vertex_stream_{vertexStreamId}";
        }

        var sharedId = links[0].SharedDataId;
        if (sharedId == uint.MaxValue || sharedId >= sharedData.Length)
        {
            return $"unresolved_vertex_stream_{vertexStreamId}";
        }

        return sharedData[sharedId].AsString();
    }

    private static PositionBounds? TryReadPositionBounds(byte[] bytes, long offset, int count, int stride, string componentName, PrimDecode prim)
    {
        if (componentName is not ("Vertex" or "SkinnableVertex") || prim.PrimId != "Float" || prim.ElementCount < 3 || stride < 12 || count <= 0)
        {
            return null;
        }

        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (var index = 0; index < count; index++)
        {
            var baseOffset = checked((int)(offset + index * (long)stride));
            for (var axis = 0; axis < 3; axis++)
            {
                var value = BitConverter.ToSingle(bytes, baseOffset + axis * 4);
                if (!float.IsFinite(value))
                {
                    return null;
                }

                min[axis] = Math.Min(min[axis], value);
                max[axis] = Math.Max(max[axis], value);
            }
        }

        return new PositionBounds(min, max);
    }

    private static ushort[] ReadU16Array(byte[] bytes, long offset, int count)
    {
        var values = new ushort[count];
        var start = checked((int)offset);
        for (var index = 0; index < count; index++)
        {
            values[index] = BitConverter.ToUInt16(bytes, start + index * 2);
        }

        return values;
    }

    private static long GetExternalIndexBase(PhyreHeader header, PhyreObjectBlock[] objectBlocks)
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

    private static IReadOnlyDictionary<uint, uint> BuildSubmeshToMeshMap(PhyreObjectBlock[] objectBlocks, PhyreObjectBlock meshBlock)
    {
        var links = FindMemberObjectLinks(objectBlocks, meshBlock, "PMeshSegment");
        var map = new Dictionary<uint, uint>();
        foreach (var link in links)
        {
            var count = Math.Max(1, link.ObjArrayCount);
            for (uint offset = 0; offset < count; offset++)
            {
                map[link.ObjId + offset] = link.ParentObjId;
            }
        }

        return map;
    }

    private static PhyreHeader ReadHeader(byte[] bytes)
    {
        var cursor = new ByteCursor(bytes);
        return new PhyreHeader(
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32(),
            cursor.ReadUInt32());
    }

    private static PhyreObjectBlock[] LoadObjectBlocks(byte[] bytes, PhyreHeader header)
    {
        var cursor = new ByteCursor(bytes);
        cursor.Seek(checked((int)header.HeaderSize + 8));
        var namespaceTypeCount = cursor.ReadInt32();
        var namespaceClassCount = cursor.ReadInt32();
        var namespaceClassDataMemberCount = cursor.ReadInt32();
        cursor.Skip(12 + namespaceTypeCount * 4);

        var nameOffsetsOffset = cursor.Position;
        var nameOffsets = new int[namespaceClassCount];
        for (var index = 0; index < namespaceClassCount; index++)
        {
            cursor.Seek(nameOffsetsOffset + index * 36 + 8);
            nameOffsets[index] = cursor.ReadInt32();
        }

        var namesBase = nameOffsetsOffset + 36 * namespaceClassCount + namespaceClassDataMemberCount * 24;
        var names = new string[namespaceClassCount];
        for (var index = 0; index < namespaceClassCount; index++)
        {
            names[index] = cursor.ReadNullTerminatedAscii(namesBase + nameOffsets[index]);
        }

        cursor.Seek(checked((int)(header.HeaderSize + header.NamespaceSize)));
        var objectBlocksDataOffset = cursor.Position + 36L * header.ObjectBlockCount;
        var blocks = new List<PhyreObjectBlock>(checked((int)header.ObjectBlockCount));
        for (var index = 0; index < header.ObjectBlockCount; index++)
        {
            var block = new PhyreObjectBlock
            {
                Id = index,
                NameId = cursor.ReadInt32(),
                ElemCount = cursor.ReadInt32(),
                DataSize = cursor.ReadInt32(),
                ObjectsSize = cursor.ReadInt32(),
                ArraysSize = cursor.ReadInt32(),
            };
            cursor.Skip(4);
            var arrayLinkCount = cursor.ReadInt32();
            var objectLinkCount = cursor.ReadInt32();
            block.DataOffset = objectBlocksDataOffset;
            block.ElemSize = block.ElemCount == 0 ? 0 : block.ObjectsSize / block.ElemCount;
            block.Name = block.NameId > 0 && block.NameId <= names.Length ? names[block.NameId - 1] : $"name_id_{block.NameId}";
            block.ArrayLinksExpected = arrayLinkCount;
            block.ObjectLinksExpected = objectLinkCount;
            objectBlocksDataOffset += block.DataSize;
            blocks.Add(block);
            cursor.Skip(4);
        }

        var objectLinkOffset = objectBlocksDataOffset
            + header.SharedDataCount * 12L
            + header.SharedDataSize
            + header.HeaderClassObjectBlockCount * 4L
            + header.HeaderClassChildCount * 16L
            + header.ObjectArrayLinkSize;
        var arrayLinkOffset = objectLinkOffset + header.ObjectLinkSize;

        cursor.Seek(checked((int)objectLinkOffset));
        foreach (var block in blocks)
        {
            LoadObjectLinks(cursor, block.ObjectLinks, block.ObjectLinksExpected, checked((uint)Math.Max(0, block.ElemCount)));
        }

        cursor.Seek(checked((int)arrayLinkOffset));
        foreach (var block in blocks)
        {
            LoadArrayLinks(cursor, block.ArrayLinks, block.ArrayLinksExpected, checked((uint)Math.Max(0, block.ElemCount)));
        }

        return blocks.ToArray();
    }

    private static PhyreSharedData[] LoadSharedData(byte[] bytes, PhyreHeader header, PhyreObjectBlock[] objectBlocks)
    {
        var sharedDataOffset = objectBlocks[^1].DataOffset + objectBlocks[^1].DataSize;
        var cursor = new ByteCursor(bytes);
        var values = new PhyreSharedData[header.SharedDataCount];
        for (var index = 0; index < values.Length; index++)
        {
            cursor.Seek(checked((int)(sharedDataOffset + header.SharedDataSize + index * 12L)));
            var rawType = cursor.ReadUInt32();
            var size = cursor.ReadUInt32();
            var offset = cursor.ReadUInt32();
            var data = new byte[size];
            Buffer.BlockCopy(bytes, checked((int)(sharedDataOffset + offset)), data, 0, checked((int)size));
            values[index] = new PhyreSharedData(rawType, data);
        }

        return values;
    }

    private static PhyreObjectBlock? FindObjectBlock(PhyreObjectBlock[] blocks, string name) =>
        blocks.FirstOrDefault(block => string.Equals(block.Name, name, StringComparison.Ordinal));

    private static PhyreObjectLink[] FindMemberObjectLinks(PhyreObjectBlock[] objectBlocks, PhyreObjectBlock objectBlock, string memberObjectBlockName)
    {
        var memberIds = objectBlocks
            .Where(block => string.Equals(block.Name, memberObjectBlockName, StringComparison.Ordinal))
            .Select(block => block.Id)
            .ToHashSet();
        return objectBlock.ObjectLinks
            .Where(link => memberIds.Contains(checked((int)link.ObjBlockId)))
            .ToArray();
    }

    private static void LoadObjectLinks(ByteCursor cursor, List<PhyreObjectLink> links, int expectedCount, uint elemCount)
    {
        while (links.Count < expectedCount)
        {
            var loadTypeAndMask = cursor.ReadByte();
            var loadType = loadTypeAndMask & 7;
            var mask = (uint)loadTypeAndMask & ~7u;
            var baseLink = new PhyreObjectLink();
            LoadParentObjOffset(cursor, baseLink, mask);
            if ((mask & SkipObjectBlockId) != 0)
            {
                baseLink.ObjBlockId = cursor.ReadVarUInt32();
            }

            RunObjectLinkLoader(cursor, links, loadType, elemCount, mask, baseLink, parentOnly: false);
        }

        if (links.Count != expectedCount)
        {
            throw new InvalidOperationException($"Object link group decoded {links.Count}, expected {expectedCount}.");
        }
    }

    private static void LoadArrayLinks(ByteCursor cursor, List<PhyreArrayLink> links, int expectedCount, uint elemCount)
    {
        while (links.Count < expectedCount)
        {
            var loadTypeAndMask = cursor.ReadByte();
            var loadType = loadTypeAndMask & 7;
            var mask = (uint)loadTypeAndMask & ~7u;
            var baseLink = new PhyreArrayLink();
            LoadParentObjOffset(cursor, baseLink, mask);
            RunArrayLinkLoader(cursor, links, loadType, elemCount, mask, baseLink, parentOnly: false);
        }

        if (links.Count != expectedCount)
        {
            throw new InvalidOperationException($"Array link group decoded {links.Count}, expected {expectedCount}.");
        }
    }

    private static void RunObjectLinkLoader(ByteCursor cursor, List<PhyreObjectLink> links, int loadType, uint elemCount, uint mask, PhyreObjectLink baseLink, bool parentOnly)
    {
        switch (loadType)
        {
            case 0:
                for (uint index = 0; index < elemCount; index++)
                {
                    links.Add(ReadObjectLink(cursor, baseLink, index, mask, parentOnly));
                }
                break;
            case 1:
                var target = links.Count + checked((int)elemCount);
                while (links.Count < target)
                {
                    var groupLoadType = cursor.ReadByte();
                    LoadObjectDstFields(cursor, baseLink, mask);
                    RunObjectLinkLoader(cursor, links, groupLoadType, elemCount, mask, baseLink, parentOnly: true);
                }
                break;
            case 2:
                foreach (var id in LoadIdList(cursor, elemCount))
                {
                    links.Add(ReadObjectLink(cursor, baseLink, id, mask, parentOnly));
                }
                break;
            case 3:
                var excluded = LoadIdList(cursor, elemCount).ToHashSet();
                for (uint index = 0; index < elemCount; index++)
                {
                    if (!excluded.Contains(index))
                    {
                        links.Add(ReadObjectLink(cursor, baseLink, index, mask, parentOnly));
                    }
                }
                break;
            case 4:
                var bytes = cursor.ReadBytes(checked((int)((elemCount >> 3) + ((elemCount & 7) == 0 ? 0 : 1))));
                for (uint index = 0; index < elemCount; index++)
                {
                    if ((bytes[index / 8] & (1 << (int)(index % 8))) != 0)
                    {
                        links.Add(ReadObjectLink(cursor, baseLink, index, mask, parentOnly));
                    }
                }
                break;
            case 5:
                var linkCount = cursor.ReadVarUInt32();
                for (uint i = 0; i < linkCount; i++)
                {
                    var parent = elemCount > 1 ? cursor.ReadVarUInt32() : baseLink.ParentObjId;
                    links.Add(ReadObjectLink(cursor, baseLink, parent, mask, parentOnly));
                }
                break;
            case 6:
                var parentObjId = cursor.ReadVarUInt32();
                var stride = cursor.ReadVarUInt32();
                var count = cursor.ReadVarUInt32();
                for (uint i = 0; i < count; i++, parentObjId += stride)
                {
                    links.Add(ReadObjectLink(cursor, baseLink, parentObjId, mask, parentOnly));
                }
                break;
            default:
                throw new InvalidOperationException($"Unrecognized object link loading type: {loadType}");
        }
    }

    private static void RunArrayLinkLoader(ByteCursor cursor, List<PhyreArrayLink> links, int loadType, uint elemCount, uint mask, PhyreArrayLink baseLink, bool parentOnly)
    {
        switch (loadType)
        {
            case 0:
                for (uint index = 0; index < elemCount; index++)
                {
                    links.Add(ReadArrayLink(cursor, baseLink, index, mask, parentOnly));
                }
                break;
            case 1:
                var target = links.Count + checked((int)elemCount);
                while (links.Count < target)
                {
                    var groupLoadType = cursor.ReadByte();
                    LoadArrayDstFields(cursor, baseLink, mask);
                    RunArrayLinkLoader(cursor, links, groupLoadType, elemCount, mask, baseLink, parentOnly: true);
                }
                break;
            case 2:
                foreach (var id in LoadIdList(cursor, elemCount))
                {
                    links.Add(ReadArrayLink(cursor, baseLink, id, mask, parentOnly));
                }
                break;
            case 3:
                var excluded = LoadIdList(cursor, elemCount).ToHashSet();
                for (uint index = 0; index < elemCount; index++)
                {
                    if (!excluded.Contains(index))
                    {
                        links.Add(ReadArrayLink(cursor, baseLink, index, mask, parentOnly));
                    }
                }
                break;
            case 4:
                var bytes = cursor.ReadBytes(checked((int)((elemCount >> 3) + ((elemCount & 7) == 0 ? 0 : 1))));
                for (uint index = 0; index < elemCount; index++)
                {
                    if ((bytes[index / 8] & (1 << (int)(index % 8))) != 0)
                    {
                        links.Add(ReadArrayLink(cursor, baseLink, index, mask, parentOnly));
                    }
                }
                break;
            case 5:
                var linkCount = cursor.ReadVarUInt32();
                for (uint i = 0; i < linkCount; i++)
                {
                    var parent = elemCount > 1 ? cursor.ReadVarUInt32() : baseLink.ParentObjId;
                    links.Add(ReadArrayLink(cursor, baseLink, parent, mask, parentOnly));
                }
                break;
            case 6:
                var parentObjId = cursor.ReadVarUInt32();
                var stride = cursor.ReadVarUInt32();
                var count = cursor.ReadVarUInt32();
                for (uint i = 0; i < count; i++, parentObjId += stride)
                {
                    links.Add(ReadArrayLink(cursor, baseLink, parentObjId, mask, parentOnly));
                }
                break;
            default:
                throw new InvalidOperationException($"Unrecognized array link loading type: {loadType}");
        }
    }

    private static PhyreObjectLink ReadObjectLink(ByteCursor cursor, PhyreObjectLink baseLink, uint index, uint mask, bool parentOnly)
    {
        var link = baseLink.Clone();
        link.ParentObjId = index;
        if (!parentOnly)
        {
            LoadObjectDstFields(cursor, link, mask);
        }

        return link;
    }

    private static PhyreArrayLink ReadArrayLink(ByteCursor cursor, PhyreArrayLink baseLink, uint index, uint mask, bool parentOnly)
    {
        var link = baseLink.Clone();
        link.ParentObjId = index;
        if (!parentOnly)
        {
            LoadArrayDstFields(cursor, link, mask);
        }

        return link;
    }

    private static void LoadParentObjOffset(ByteCursor cursor, PhyreFileLink link, uint mask)
    {
        var value = cursor.ReadVarUInt32();
        link.ParentOffsetFlag = value & 1;
        if (link.ParentOffsetFlag != 0)
        {
            link.ParentFieldOffset = value >> 1;
        }
        else
        {
            link.ParentObjOffset = value >> 1;
        }
    }

    private static void LoadObjectDstFields(ByteCursor cursor, PhyreObjectLink link, uint mask)
    {
        link.SharedDataId = (mask & SkipSharedDataId) != 0 ? 0 : cursor.ReadVarUInt32();
        if (link.SharedDataId == 0)
        {
            link.ObjId = cursor.ReadVarUInt32();
            if ((mask & SkipObjectBlockId) == 0)
            {
                link.ObjBlockId = cursor.ReadVarUInt32();
            }

            if ((mask & SkipObjectOffset) == 0)
            {
                link.ObjOffset = cursor.ReadVarUInt32();
            }
        }

        link.SharedDataId -= 1;
        if ((mask & SkipArrayCount) == 0)
        {
            link.ObjArrayCount = cursor.ReadVarUInt32();
        }
    }

    private static void LoadArrayDstFields(ByteCursor cursor, PhyreArrayLink link, uint mask)
    {
        if ((mask & SkipArrayCount) == 0)
        {
            link.Count = cursor.ReadVarUInt32();
        }

        link.Offset = cursor.ReadVarUInt32();
    }

    private static uint[] LoadIdList(ByteCursor cursor, uint elemCount)
    {
        var count = cursor.ReadVarUInt32();
        var values = new uint[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (elemCount >> 8) != 0 ? cursor.ReadVarUInt32() : cursor.ReadByte();
        }

        return values;
    }

    private static PrimDecode DecodePrim(uint raw)
    {
        if (raw > 49)
        {
            return new PrimDecode("INVALID", 0);
        }

        return raw switch
        {
            48 => new PrimDecode("Matrix3x4", 1),
            49 => new PrimDecode("Matrix4x4", 1),
            _ => new PrimDecode(PrimNames[raw >> 2], raw % 4 + 1),
        };
    }

    private static PositionBounds? MergeBounds(IEnumerable<PositionBounds> bounds)
    {
        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        var any = false;
        foreach (var bound in bounds)
        {
            any = true;
            for (var axis = 0; axis < 3; axis++)
            {
                min[axis] = Math.Min(min[axis], bound.Min[axis]);
                max[axis] = Math.Max(max[axis], bound.Max[axis]);
            }
        }

        return any ? new PositionBounds(min, max) : null;
    }

    private static bool HasFiniteBounds(PositionBounds bounds) =>
        bounds.Min.Concat(bounds.Max).All(double.IsFinite);

    private sealed record PrimDecode(string PrimId, uint ElementCount);

    private sealed class ByteCursor
    {
        private readonly byte[] bytes;

        public ByteCursor(byte[] bytes, int position = 0)
        {
            this.bytes = bytes;
            Position = position;
        }

        public int Position { get; private set; }

        public void Seek(int position) => Position = position;

        public void Skip(int count) => Position += count;

        public byte ReadByte() => bytes[Position++];

        public byte[] ReadBytes(int count)
        {
            var data = new byte[count];
            Buffer.BlockCopy(bytes, Position, data, 0, count);
            Position += count;
            return data;
        }

        public int ReadInt32()
        {
            var value = BitConverter.ToInt32(bytes, Position);
            Position += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            var value = BitConverter.ToUInt32(bytes, Position);
            Position += 4;
            return value;
        }

        public uint ReadVarUInt32()
        {
            uint value = 0;
            var shift = 0;
            byte raw;
            do
            {
                raw = ReadByte();
                value |= (uint)(raw & 127) << shift;
                shift += 7;
            }
            while ((raw & 128) != 0);

            return value;
        }

        public string ReadNullTerminatedAscii(int offset)
        {
            var end = offset;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }
    }

    private sealed record PhyreHeader(
        uint MagicBytes,
        uint HeaderSize,
        uint NamespaceSize,
        uint PlatformId,
        uint ObjectBlockCount,
        uint ArrayLinkSize,
        uint ArrayLinkCount,
        uint ObjectLinkSize,
        uint ObjectLinkCount,
        uint ObjectArrayLinkSize,
        uint ObjectArrayLinkCount,
        uint ObjectsInArraysCount,
        uint SharedDataCount,
        uint SharedDataSize,
        uint BlockDataSize,
        uint HeaderClassObjectBlockCount,
        uint HeaderClassChildCount,
        uint PhysicsEngineId,
        uint IndicesSize,
        uint VerticesSize,
        uint MaxTextureSize);

    private class PhyreFileLink
    {
        public uint ParentObjId { get; set; }
        public uint ParentObjOffset { get; set; }
        public uint ParentFieldOffset { get; set; }
        public uint ParentOffsetFlag { get; set; }
    }

    private sealed class PhyreArrayLink : PhyreFileLink
    {
        public uint Offset { get; set; }
        public uint Count { get; set; }

        public PhyreArrayLink Clone() => new()
        {
            ParentObjId = ParentObjId,
            ParentObjOffset = ParentObjOffset,
            ParentFieldOffset = ParentFieldOffset,
            ParentOffsetFlag = ParentOffsetFlag,
            Offset = Offset,
            Count = Count,
        };
    }

    private sealed class PhyreObjectLink : PhyreFileLink
    {
        public uint ObjId { get; set; }
        public uint ObjOffset { get; set; }
        public uint ObjBlockId { get; set; }
        public uint ObjArrayCount { get; set; }
        public uint SharedDataId { get; set; }

        public PhyreObjectLink Clone() => new()
        {
            ParentObjId = ParentObjId,
            ParentObjOffset = ParentObjOffset,
            ParentFieldOffset = ParentFieldOffset,
            ParentOffsetFlag = ParentOffsetFlag,
            ObjId = ObjId,
            ObjOffset = ObjOffset,
            ObjBlockId = ObjBlockId,
            ObjArrayCount = ObjArrayCount,
            SharedDataId = SharedDataId,
        };
    }

    public static PhyreSceneGraphReport ParseSceneGraph(string assetId, string daePath, bool portable)
    {
        if (!File.Exists(daePath))
        {
            return PhyreSceneGraphReport.BlockedMissingFile(assetId, FileSnapshot.FromPath(daePath, portable));
        }

        try
        {
            var bytes = File.ReadAllBytes(daePath);
            var header = ReadHeader(bytes);
            var objectBlocks = LoadObjectBlocks(bytes, header);
            var pnodeBlock = FindObjectBlock(objectBlocks, "PNode");
            if (pnodeBlock is null)
            {
                return new PhyreSceneGraphReport(
                    DateTimeOffset.UtcNow,
                    assetId,
                    FileSnapshot.FromPath(daePath, portable),
                    Array.Empty<PhyreSceneNodeEntry>(),
                    "blocked_pnode_block_missing",
                    "No PNode block in descriptor — scene graph export not available for this map.");
            }

            const int localMatrixOffset = 0x10;
            const int nameOffset = 0x50;
            var parentByChild = BuildPNodeParentMap(pnodeBlock);
            var localMatrices = new float[pnodeBlock.ElemCount][];
            var nodes = new List<PhyreSceneNodeEntry>(pnodeBlock.ElemCount);
            for (var index = 0; index < pnodeBlock.ElemCount; index++)
            {
                var baseOffset = checked((int)(pnodeBlock.DataOffset + (long)index * pnodeBlock.ElemSize));
                var matrix = ReadMatrix16(bytes, baseOffset + localMatrixOffset);
                localMatrices[index] = matrix;
                var localTx = matrix[12];
                var localTy = matrix[13];
                var localTz = matrix[14];
                var name = TryReadPNodeName(bytes, baseOffset + nameOffset);
                parentByChild.TryGetValue(index, out var parentIndex);
                nodes.Add(new PhyreSceneNodeEntry(
                    index,
                    name,
                    matrix,
                    localTx,
                    localTy,
                    localTz,
                    localTx,
                    localTy,
                    localTz,
                    parentIndex >= 0 ? parentIndex : null));
            }

            for (var index = 0; index < nodes.Count; index++)
            {
                var world = ComposeWorldMatrix(index, parentByChild, localMatrices);
                var entry = nodes[index];
                nodes[index] = entry with
                {
                    WorldTx = world[12],
                    WorldTy = world[13],
                    WorldTz = world[14],
                };
            }

            return new PhyreSceneGraphReport(
                DateTimeOffset.UtcNow,
                assetId,
                FileSnapshot.FromPath(daePath, portable),
                nodes,
                parentByChild.Count > 0 ? "phyre_scene_graph_offline_candidate" : "phyre_scene_graph_local_only",
                parentByChild.Count > 0
                    ? "Offline PNode local + parent-chain world compose (heuristic link group). Validate vs Field Scout scene_node_placed; PMeshInstance bridge is separate instance-map."
                    : "Offline PNode m_localMatrix@+0x10 only — no PNode parent link group found in file.");
        }
        catch (Exception exception)
        {
            return new PhyreSceneGraphReport(
                DateTimeOffset.UtcNow,
                assetId,
                FileSnapshot.FromPath(daePath, portable),
                Array.Empty<PhyreSceneNodeEntry>(),
                "blocked_scene_graph_parser_exception",
                exception.Message);
        }
    }

    public static PhyreInstanceMapReport ParseInstanceMap(string assetId, string daePath, bool portable)
    {
        if (!File.Exists(daePath))
        {
            return PhyreInstanceMapReport.BlockedMissingFile(assetId, FileSnapshot.FromPath(daePath, portable));
        }

        try
        {
            var bytes = File.ReadAllBytes(daePath);
            var header = ReadHeader(bytes);
            var objectBlocks = LoadObjectBlocks(bytes, header);
            var meshBlock = FindObjectBlock(objectBlocks, "PMesh");
            var instanceBlock = FindObjectBlock(objectBlocks, "PMeshInstance");
            if (meshBlock is null || instanceBlock is null)
            {
                return new PhyreInstanceMapReport(
                    DateTimeOffset.UtcNow,
                    assetId,
                    FileSnapshot.FromPath(daePath, portable),
                    Array.Empty<PhyreInstanceEntry>(),
                    new Dictionary<int, int>(),
                    false,
                    "blocked_pmesh_or_instance_missing",
                    "PMesh and PMeshInstance blocks required for instance map.");
            }

            const uint meshLinkGroup = 138;
            const uint segmentContextFieldOffset = 40;
            const int instanceNameOffset = 0x2C;

            var meshSpans = BuildInstanceSegmentSpans(objectBlocks, meshBlock, "PMeshSegment", parentFieldOffset: 4);
            var instanceToMesh = BuildInstanceToMeshMap(instanceBlock, meshBlock, meshLinkGroup);
            var instanceSpans = BuildInstanceSegmentSpans(objectBlocks, instanceBlock, "PMeshInstanceSegmentContext", segmentContextFieldOffset);

            var instances = new List<PhyreInstanceEntry>(instanceBlock.ElemCount);
            var submeshToInstance = new Dictionary<int, int>();
            var spansMatchMesh = true;

            for (var instanceId = 0; instanceId < instanceBlock.ElemCount; instanceId++)
            {
                if (!instanceToMesh.TryGetValue(instanceId, out var meshId)
                    || !instanceSpans.TryGetValue(instanceId, out var span))
                {
                    continue;
                }

                if (meshSpans.TryGetValue(meshId, out var meshSpan)
                    && (meshSpan.Start != span.Start || meshSpan.Count != span.Count))
                {
                    spansMatchMesh = false;
                }

                var submeshIds = new int[span.Count];
                for (var offset = 0; offset < span.Count; offset++)
                {
                    var submeshId = span.Start + offset;
                    submeshIds[offset] = submeshId;
                    submeshToInstance[submeshId] = instanceId;
                }

                var nameOffset = checked((int)(instanceBlock.DataOffset + (long)instanceId * instanceBlock.ElemSize + instanceNameOffset));
                var name = TryReadPNodeName(bytes, nameOffset);

                instances.Add(new PhyreInstanceEntry(
                    instanceId,
                    meshId,
                    span.Start,
                    span.Count,
                    submeshIds,
                    name));
            }

            return new PhyreInstanceMapReport(
                DateTimeOffset.UtcNow,
                assetId,
                FileSnapshot.FromPath(daePath, portable),
                instances,
                submeshToInstance,
                spansMatchMesh,
                instances.Count > 0 ? "phyre_instance_map_candidate" : "blocked_no_instance_spans",
                spansMatchMesh
                    ? "PMeshInstance segment spans match PMesh spans (structural). PNode pairing still runtime @ FFX_FieldMap_WireInstanceToSceneNodes (0x65B0F0)."
                    : "Instance/mesh segment span mismatch — inspect link dump before glTF grouping.");
        }
        catch (Exception exception)
        {
            return new PhyreInstanceMapReport(
                DateTimeOffset.UtcNow,
                assetId,
                FileSnapshot.FromPath(daePath, portable),
                Array.Empty<PhyreInstanceEntry>(),
                new Dictionary<int, int>(),
                false,
                "blocked_instance_map_parser_exception",
                exception.Message);
        }
    }

    private static Dictionary<int, int> BuildInstanceToMeshMap(PhyreObjectBlock instanceBlock, PhyreObjectBlock meshBlock, uint meshLinkGroup)
    {
        var map = new Dictionary<int, int>();
        foreach (var link in instanceBlock.ObjectLinks)
        {
            if (link.ParentOffsetFlag != 0 || link.ParentObjOffset != meshLinkGroup || link.ObjBlockId != meshBlock.Id)
            {
                continue;
            }

            map[(int)link.ParentObjId] = (int)link.ObjId;
        }

        return map;
    }

    private static Dictionary<int, (int Start, int Count)> BuildInstanceSegmentSpans(
        PhyreObjectBlock[] objectBlocks,
        PhyreObjectBlock parentBlock,
        string targetBlockName,
        uint parentFieldOffset)
    {
        var targetBlock = FindObjectBlock(objectBlocks, targetBlockName);
        if (targetBlock is null)
        {
            return new Dictionary<int, (int Start, int Count)>();
        }

        var spans = new Dictionary<int, (int Start, int Count)>();
        foreach (var link in parentBlock.ObjectLinks)
        {
            if (link.ParentOffsetFlag != 1 || link.ParentFieldOffset != parentFieldOffset || link.ObjBlockId != targetBlock.Id)
            {
                continue;
            }

            spans[(int)link.ParentObjId] = ((int)link.ObjId, (int)Math.Max(1, link.ObjArrayCount));
        }

        return spans;
    }

    private static Dictionary<int, int> BuildPNodeParentMap(PhyreObjectBlock pnodeBlock)
    {
        var groups = new Dictionary<uint, List<(int Child, int Parent)>>();
        foreach (var link in pnodeBlock.ObjectLinks)
        {
            if (link.ObjBlockId != pnodeBlock.Id || link.ParentOffsetFlag != 0)
            {
                continue;
            }

            if (!groups.TryGetValue(link.ParentObjOffset, out var list))
            {
                list = new List<(int, int)>();
                groups[link.ParentObjOffset] = list;
            }

            list.Add(((int)link.ParentObjId, (int)link.ObjId));
        }

        if (groups.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var best = groups.OrderByDescending(static pair => pair.Value.Count).First();
        var map = new Dictionary<int, int>(best.Value.Count);
        foreach (var (child, parent) in best.Value)
        {
            map[child] = parent;
        }

        return map;
    }

    private static float[] ComposeWorldMatrix(int nodeIndex, IReadOnlyDictionary<int, int> parentByChild, float[][] localMatrices)
    {
        var chain = new Stack<int>();
        var cursor = nodeIndex;
        while (cursor >= 0 && cursor < localMatrices.Length)
        {
            chain.Push(cursor);
            if (!parentByChild.TryGetValue(cursor, out var parent) || parent < 0 || parent >= localMatrices.Length)
            {
                break;
            }

            cursor = parent;
        }

        var world = new float[16];
        Array.Copy(localMatrices[chain.Pop()], world, 16);
        while (chain.Count > 0)
        {
            world = MultiplyMatrix4x4(world, localMatrices[chain.Pop()]);
        }

        return world;
    }

    private static float[] MultiplyMatrix4x4(float[] left, float[] right)
    {
        var result = new float[16];
        for (var column = 0; column < 4; column++)
        {
            for (var row = 0; row < 4; row++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    sum += left[k * 4 + row] * right[column * 4 + k];
                }

                result[column * 4 + row] = sum;
            }
        }

        return result;
    }

    private static float[] ReadMatrix16(byte[] bytes, int offset)
    {
        var matrix = new float[16];
        for (var index = 0; index < 16; index++)
        {
            matrix[index] = BitConverter.ToSingle(bytes, offset + index * 4);
        }

        return matrix;
    }

    private static string? TryReadPNodeName(byte[] bytes, int offset)
    {
        if (offset < 0 || offset >= bytes.Length)
        {
            return null;
        }

        var first = bytes[offset];
        if (first is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')
        {
            var end = offset;
            while (end < bytes.Length && bytes[end] != 0 && end - offset < 96)
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }

        if (offset + 4 > bytes.Length)
        {
            return null;
        }

        var rel = BitConverter.ToInt32(bytes, offset);
        var candidates = new[] { offset + rel, rel };
        foreach (var candidate in candidates)
        {
            if (candidate < 0 || candidate >= bytes.Length)
            {
                continue;
            }

            var probe = bytes[candidate];
            if (probe is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z')
            {
                var end = candidate;
                while (end < bytes.Length && bytes[end] != 0 && end - candidate < 96)
                {
                    end++;
                }

                return Encoding.ASCII.GetString(bytes, candidate, end - candidate);
            }
        }

        return null;
    }

    private sealed class PhyreObjectBlock
    {
        public int Id { get; set; }
        public int NameId { get; set; }
        public int ElemCount { get; set; }
        public int DataSize { get; set; }
        public int ObjectsSize { get; set; }
        public int ArraysSize { get; set; }
        public long DataOffset { get; set; }
        public int ElemSize { get; set; }
        public string Name { get; set; } = "";
        public int ObjectLinksExpected { get; set; }
        public int ArrayLinksExpected { get; set; }
        public List<PhyreObjectLink> ObjectLinks { get; } = new();
        public List<PhyreArrayLink> ArrayLinks { get; } = new();
    }

    private sealed record PhyreSharedData(uint RawType, byte[] Data)
    {
        public string AsString()
        {
            var length = Array.IndexOf(Data, (byte)0);
            if (length < 0)
            {
                length = Data.Length;
            }

            return Encoding.ASCII.GetString(Data, 0, length);
        }
    }

    public sealed record PhyreHeaderReport(
        uint HeaderSize,
        uint NamespaceSize,
        uint ObjectBlockCount,
        uint ArrayLinkSize,
        uint ArrayLinkCount,
        uint ObjectLinkSize,
        uint ObjectLinkCount,
        uint ObjectArrayLinkSize,
        uint ObjectArrayLinkCount,
        uint SharedDataCount,
        uint SharedDataSize,
        uint BlockDataSize,
        uint IndicesSize,
        uint VerticesSize,
        uint MaxTextureSize)
    {
    }

    public sealed record ObjectBlockReport(
        int Id,
        string Name,
        int ElemCount,
        int ElemSize,
        int DataSize,
        long DataOffset,
        int ObjectLinkCount,
        int ArrayLinkCount)
    {
    }
}

public sealed record PhyreLinkDumpReport(
    DateTimeOffset GeneratedAtUtc,
    string AssetId,
    FileSnapshot DaePhyre,
    PhyreObjectLinkBlockReport[] ObjectBlocks,
    PhyreSharedDataReport[] SharedData,
    string DecisionBand,
    string NextStrike)
{
    public static PhyreLinkDumpReport BlockedMissingFile(string assetId, FileSnapshot snapshot) => new(
        DateTimeOffset.UtcNow,
        assetId,
        snapshot,
        Array.Empty<PhyreObjectLinkBlockReport>(),
        Array.Empty<PhyreSharedDataReport>(),
        "blocked_missing_dae_phyre",
        "locate the .dae.phyre before dumping Phyre links");
}

public sealed record PhyreObjectLinkBlockReport(
    int Id,
    string Name,
    int ElemCount,
    int ElemSize,
    int DataSize,
    long DataOffset,
    PhyreObjectLinkReport[] ObjectLinks,
    PhyreArrayLinkReport[] ArrayLinks);

public sealed record PhyreObjectLinkReport(
    uint ParentObjId,
    uint ParentObjOffset,
    uint ParentFieldOffset,
    uint ParentOffsetFlag,
    uint TargetBlockId,
    string TargetBlockName,
    uint TargetObjId,
    uint TargetObjOffset,
    uint TargetArrayCount,
    int? SharedDataId,
    uint? SharedDataRawType,
    string? SharedDataText);

public sealed record PhyreArrayLinkReport(
    uint ParentObjId,
    uint ParentObjOffset,
    uint ParentFieldOffset,
    uint ParentOffsetFlag,
    uint Offset,
    uint Count);

public sealed record PhyreSharedDataReport(
    int Id,
    uint RawType,
    int Length,
    string Text);

public sealed record PhyreStringIndexReport(
    DateTimeOffset GeneratedAtUtc,
    string AssetId,
    FileSnapshot DaePhyre,
    PhyreStringIndexEntry[] Entries,
    string DecisionBand,
    string NextStrike)
{
    public static PhyreStringIndexReport BlockedMissingFile(string assetId, FileSnapshot snapshot) => new(
        DateTimeOffset.UtcNow,
        assetId,
        snapshot,
        Array.Empty<PhyreStringIndexEntry>(),
        "blocked_missing_dae_phyre",
        "locate the .dae.phyre before indexing Phyre strings");
}

public sealed record PhyreStringIndexEntry(
    int Offset,
    string Value,
    string Role,
    string? TextureKey);

public sealed record PhyreDescriptorIndex(
    DateTimeOffset GeneratedAtUtc,
    string Ps3Root,
    int ReportCount,
    int CandidateCount,
    int BlockedCount,
    PhyreDescriptorReport[] Reports);

public sealed record PhyreDescriptorReport(
    DateTimeOffset GeneratedAtUtc,
    string MonsterId,
    FileSnapshot DaePhyre,
    PhyreDescriptorParser.PhyreHeaderReport? Header,
    PhyreDescriptorParser.ObjectBlockReport[] ObjectBlocks,
    int MeshCount,
    int MeshSegmentCount,
    int DataBlockCount,
    int VertexStreamCount,
    int DescriptorSubmeshCount,
    int TotalIndexCount,
    int TotalVertexCount,
    PositionBounds? PositionBounds,
    string DecisionBand,
    string PromotionStatus,
    string NextStrike,
    PhyreSubmeshReport[] Submeshes,
    string[] Warnings)
{
    public static PhyreDescriptorReport BlockedMissingFile(string monsterId, FileSnapshot snapshot) => new(
        DateTimeOffset.UtcNow,
        monsterId,
        snapshot,
        null,
        Array.Empty<PhyreDescriptorParser.ObjectBlockReport>(),
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        "blocked_missing_dae_phyre",
        "not_promoted_missing_source_asset",
        "locate the .dae.phyre before descriptor decoding",
        Array.Empty<PhyreSubmeshReport>(),
        Array.Empty<string>());
}

public sealed record PhyreSubmeshReport(
    int SubmeshId,
    int? MeshId,
    int FileMaterialId,
    int? DrawType,
    int IndexCount,
    long IndexDataOffset,
    int IndexDataSize,
    int? MaxIndex,
    int? IndexOutOfRangeCount,
    uint? PositionVertexCount,
    PositionBounds? PositionBounds,
    PhyreVertexComponentReport[] Components);

public sealed record PhyreVertexComponentReport(
    int DataBlockId,
    int VertexStreamId,
    string ComponentType,
    uint RawPrimType,
    string PrimType,
    uint PrimElementCount,
    uint ElementSize,
    uint ElementCount,
    uint VertexStreamCount,
    long VertexDataOffset,
    int VertexDataSize,
    PositionBounds? PositionBounds);

public sealed record OracleIndexReport(
    DateTimeOffset GeneratedAtUtc,
    string[] Roots,
    int AssetCount,
    int FbxCount,
    int GltfCount,
    int DaeCount,
    OracleAssetSnapshot[] Assets);

public sealed record OracleAssetSnapshot(
    string MonsterId,
    string Extension,
    string Path,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    string SourceKind)
{
    public static OracleAssetSnapshot? FromPath(string path, bool portable)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        var monsterId = fileName[..4].ToLowerInvariant();
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var info = new FileInfo(path);
        return new OracleAssetSnapshot(
            monsterId,
            extension,
            portable ? PortablePath.Invoke(path) : path,
            info.Length,
            info.LastWriteTimeUtc,
            sha256,
            Classify(path, extension));
    }

    private static string Classify(string path, string extension)
    {
        if (extension == ".fbx")
        {
            return "fbx_oracle";
        }

        var normalized = path.Replace('/', '\\');
        if (normalized.Contains(@"\modelviewer_next\", StringComparison.OrdinalIgnoreCase))
        {
            return "local_lab_candidate";
        }

        return "external_model_candidate";
    }
}

public sealed record PhyreSceneNodeEntry(
    int Index,
    string? Name,
    float[] LocalMatrix,
    float LocalTx,
    float LocalTy,
    float LocalTz,
    float WorldTx,
    float WorldTy,
    float WorldTz,
    int? ParentIndex);

public sealed record PhyreSceneGraphReport(
    DateTimeOffset GeneratedAtUtc,
    string AssetId,
    FileSnapshot DaePhyre,
    IReadOnlyList<PhyreSceneNodeEntry> Nodes,
    string DecisionBand,
    string NextStrike)
{
    public static PhyreSceneGraphReport BlockedMissingFile(string assetId, FileSnapshot snapshot) => new(
        DateTimeOffset.UtcNow,
        assetId,
        snapshot,
        Array.Empty<PhyreSceneNodeEntry>(),
        "blocked_missing_dae_phyre",
        "locate the .dae.phyre before scene graph decode");
}

public sealed record PhyreInstanceEntry(
    int InstanceId,
    int MeshId,
    int SegmentStart,
    int SegmentCount,
    int[] SubmeshIds,
    string? Name);

public sealed record PhyreInstanceMapReport(
    DateTimeOffset GeneratedAtUtc,
    string AssetId,
    FileSnapshot DaePhyre,
    IReadOnlyList<PhyreInstanceEntry> Instances,
    IReadOnlyDictionary<int, int> SubmeshToInstance,
    bool SpansMatchMesh,
    string DecisionBand,
    string NextStrike)
{
    public static PhyreInstanceMapReport BlockedMissingFile(string assetId, FileSnapshot snapshot) => new(
        DateTimeOffset.UtcNow,
        assetId,
        snapshot,
        Array.Empty<PhyreInstanceEntry>(),
        new Dictionary<int, int>(),
        false,
        "blocked_missing_dae_phyre",
        "locate the .dae.phyre before instance map decode");
}
