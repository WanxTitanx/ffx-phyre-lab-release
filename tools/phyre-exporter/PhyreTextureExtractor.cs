using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PhyreExporter;

public static class PhyreTextureExtractor
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static PhyreTextureFileExtractionReport ExtractTextureFile(
        string sourcePath,
        string pngPath,
        string assetId,
        bool portable,
        string decisionBand = "texture_png_extracted_candidate",
        string nextStrike = "texture decoded to PNG mip0; material binding still needs PMaterial/PParameterBuffer confirmation")
    {
        if (!File.Exists(sourcePath))
        {
            return PhyreTextureFileExtractionReport.Blocked(
                assetId,
                FileSnapshot.FromPath(sourcePath, portable),
                "blocked_missing_texture_phyre",
                "source .dds.phyre file is missing");
        }

        var bytes = File.ReadAllBytes(sourcePath);
        var texture = Decode(bytes);
        WritePngRgba(pngPath, texture.Width, texture.Height, texture.Rgba);

        return new PhyreTextureFileExtractionReport(
            DateTimeOffset.UtcNow,
            assetId,
            FileSnapshot.FromPath(sourcePath, portable),
            portable ? PortablePath.Invoke(pngPath) : pngPath,
            texture.Format,
            texture.Width,
            texture.Height,
            texture.BufferStart,
            texture.Mip0Bytes,
            texture.Mipped,
            decisionBand,
            nextStrike);
    }

    public static PhyreTextureExtractionReport ExtractPrimaryTexture(string ps3Root, string monsterId, string outputRoot, bool portable)
    {
        var sourcePath = Path.Combine(ps3Root, "chr", "mon", monsterId, "tex", "d3d11", $"{monsterId}.dds.phyre");
        if (!File.Exists(sourcePath))
        {
            return PhyreTextureExtractionReport.Blocked(monsterId, FileSnapshot.FromPath(sourcePath, portable), "blocked_missing_texture_phyre", "official monster texture file is missing");
        }

        var bytes = File.ReadAllBytes(sourcePath);
        var texture = Decode(bytes);
        Directory.CreateDirectory(outputRoot);

        var pngPath = Path.Combine(outputRoot, $"{monsterId}.official-texture.png");
        WritePngRgba(pngPath, texture.Width, texture.Height, texture.Rgba);

        return new PhyreTextureExtractionReport(
            DateTimeOffset.UtcNow,
            monsterId,
            FileSnapshot.FromPath(sourcePath, portable),
            portable ? PortablePath.Invoke(pngPath) : pngPath,
            texture.Format,
            texture.Width,
            texture.Height,
            texture.BufferStart,
            texture.Mip0Bytes,
            texture.Mipped,
            "texture_png_extracted_candidate",
            "primary .dds.phyre decoded to PNG mip0; material binding remains coarse until PMaterial/PParameterBuffer texture slots are decoded");
    }

    private static DecodedTexture Decode(byte[] bytes)
    {
        if (bytes.Length < 128 || Encoding.ASCII.GetString(bytes, 0, 5) != "RYHPT")
        {
            throw new InvalidOperationException("Not a RYHPT/.phyre texture file.");
        }

        var pidx = -1;
        var format = "";
        var scanLimit = Math.Min(bytes.Length, 16384);
        for (var offset = 0; offset < scanLimit;)
        {
            var hit = FindAscii(bytes, "PTexture2D", offset, scanLimit);
            if (hit < 0)
            {
                break;
            }

            var token = ReadPrintableToken(bytes, hit + 10);
            if (token is "ARGB8" or "DXT1" or "DXT3" or "DXT5" or "L8")
            {
                pidx = hit;
                format = token;
                break;
            }

            offset = hit + 1;
        }

        if (pidx < 0)
        {
            throw new InvalidOperationException("No PTexture2D instance with a supported texture format token.");
        }

        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pidx - 88, 4)));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pidx - 84, 4)));
        if (width is < 1 or > 8192 || height is < 1 or > 8192)
        {
            throw new InvalidOperationException($"Texture dimensions are implausible: {width}x{height}.");
        }

        var bufferStart = pidx + 11 + format.Length + 38;
        var mip0Bytes = GetMip0Size(width, height, format);
        if (bufferStart < 0 || bufferStart + mip0Bytes > bytes.Length)
        {
            throw new InvalidOperationException($"Computed mip0 range is out of file bounds: start={bufferStart}, bytes={mip0Bytes}, len={bytes.Length}.");
        }

        var rgba = format switch
        {
            "ARGB8" => DecodeArgb8(bytes, bufferStart, width, height),
            "DXT1" => DecodeDxt1(bytes, bufferStart, width, height),
            "DXT3" => DecodeDxt3(bytes, bufferStart, width, height),
            "DXT5" => DecodeDxt5(bytes, bufferStart, width, height),
            "L8" => DecodeL8(bytes, bufferStart, width, height),
            _ => throw new InvalidOperationException($"Unsupported texture format: {format}"),
        };

        return new DecodedTexture(format, width, height, bufferStart, mip0Bytes, bytes.Length - bufferStart > mip0Bytes + 16, rgba);
    }

    private static int FindAscii(byte[] bytes, string needle, int start, int limit)
    {
        var needleBytes = Encoding.ASCII.GetBytes(needle);
        var end = Math.Min(bytes.Length - needleBytes.Length, limit);
        for (var offset = start; offset <= end; offset++)
        {
            var ok = true;
            for (var index = 0; index < needleBytes.Length; index++)
            {
                if (bytes[offset + index] != needleBytes[index])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return offset;
            }
        }

        return -1;
    }

    private static string ReadPrintableToken(byte[] bytes, int start)
    {
        var offset = start;
        while (offset < bytes.Length && bytes[offset] == 0)
        {
            offset++;
        }

        var builder = new StringBuilder();
        while (offset < bytes.Length && bytes[offset] >= 32 && bytes[offset] < 127)
        {
            builder.Append((char)bytes[offset]);
            offset++;
        }

        return builder.ToString();
    }

    private static int GetMip0Size(int width, int height, string format) => format switch
    {
        "ARGB8" => checked(width * height * 4),
        "L8" => checked(width * height),
        "DXT1" => checked(((width + 3) / 4) * ((height + 3) / 4) * 8),
        "DXT3" or "DXT5" => checked(((width + 3) / 4) * ((height + 3) / 4) * 16),
        _ => throw new InvalidOperationException($"Unsupported texture format: {format}"),
    };

    private static byte[] DecodeArgb8(byte[] bytes, int offset, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var source = offset + pixel * 4;
            var target = pixel * 4;
            rgba[target + 0] = bytes[source + 2];
            rgba[target + 1] = bytes[source + 1];
            rgba[target + 2] = bytes[source + 0];
            rgba[target + 3] = bytes[source + 3];
        }

        return rgba;
    }

    private static byte[] DecodeL8(byte[] bytes, int offset, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var value = bytes[offset + pixel];
            var target = pixel * 4;
            rgba[target + 0] = value;
            rgba[target + 1] = value;
            rgba[target + 2] = value;
            rgba[target + 3] = 255;
        }

        return rgba;
    }

    private static byte[] DecodeDxt1(byte[] bytes, int offset, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        var blockOffset = offset;
        for (var blockY = 0; blockY < (height + 3) / 4; blockY++)
        {
            for (var blockX = 0; blockX < (width + 3) / 4; blockX++)
            {
                DecodeColorBlock(bytes, blockOffset, width, height, blockX, blockY, rgba, dxt1: true, alphaBlock: null);
                blockOffset += 8;
            }
        }

        return rgba;
    }

    private static byte[] DecodeDxt3(byte[] bytes, int offset, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        var blockOffset = offset;
        for (var blockY = 0; blockY < (height + 3) / 4; blockY++)
        {
            for (var blockX = 0; blockX < (width + 3) / 4; blockX++)
            {
                var alpha = new byte[16];
                ulong alphaBits = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(blockOffset, 8));
                for (var i = 0; i < 16; i++)
                {
                    alpha[i] = (byte)(((alphaBits >> (i * 4)) & 0xF) * 17);
                }

                DecodeColorBlock(bytes, blockOffset + 8, width, height, blockX, blockY, rgba, dxt1: false, alpha);
                blockOffset += 16;
            }
        }

        return rgba;
    }

    private static byte[] DecodeDxt5(byte[] bytes, int offset, int width, int height)
    {
        var rgba = new byte[checked(width * height * 4)];
        var blockOffset = offset;
        for (var blockY = 0; blockY < (height + 3) / 4; blockY++)
        {
            for (var blockX = 0; blockX < (width + 3) / 4; blockX++)
            {
                var alpha = BuildDxt5Alpha(bytes, blockOffset);
                DecodeColorBlock(bytes, blockOffset + 8, width, height, blockX, blockY, rgba, dxt1: false, alpha);
                blockOffset += 16;
            }
        }

        return rgba;
    }

    private static byte[] BuildDxt5Alpha(byte[] bytes, int offset)
    {
        var a0 = bytes[offset];
        var a1 = bytes[offset + 1];
        Span<byte> table = stackalloc byte[8];
        table[0] = a0;
        table[1] = a1;
        if (a0 > a1)
        {
            for (var i = 1; i < 7; i++)
            {
                table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
        }
        else
        {
            for (var i = 1; i < 5; i++)
            {
                table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            }

            table[6] = 0;
            table[7] = 255;
        }

        ulong bits = 0;
        for (var i = 0; i < 6; i++)
        {
            bits |= (ulong)bytes[offset + 2 + i] << (8 * i);
        }

        var alpha = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            alpha[i] = table[(int)((bits >> (3 * i)) & 7)];
        }

        return alpha;
    }

    private static void DecodeColorBlock(byte[] bytes, int offset, int width, int height, int blockX, int blockY, byte[] rgba, bool dxt1, byte[]? alphaBlock)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2, 2));
        var colors = BuildColorTable(c0, c1, dxt1);
        var colorBits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));

        for (var py = 0; py < 4; py++)
        {
            for (var px = 0; px < 4; px++)
            {
                var x = blockX * 4 + px;
                var y = blockY * 4 + py;
                if (x >= width || y >= height)
                {
                    continue;
                }

                var colorIndex = (int)((colorBits >> (2 * (py * 4 + px))) & 3);
                var target = (y * width + x) * 4;
                rgba[target + 0] = colors[colorIndex, 0];
                rgba[target + 1] = colors[colorIndex, 1];
                rgba[target + 2] = colors[colorIndex, 2];
                rgba[target + 3] = alphaBlock?[py * 4 + px] ?? (byte)(dxt1 && c0 <= c1 && colorIndex == 3 ? 0 : 255);
            }
        }
    }

    private static byte[,] BuildColorTable(ushort c0, ushort c1, bool dxt1)
    {
        var colors = new byte[4, 3];
        Unpack565(c0, colors, 0);
        Unpack565(c1, colors, 1);
        if (!dxt1 || c0 > c1)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                colors[2, channel] = (byte)((2 * colors[0, channel] + colors[1, channel]) / 3);
                colors[3, channel] = (byte)((colors[0, channel] + 2 * colors[1, channel]) / 3);
            }
        }
        else
        {
            for (var channel = 0; channel < 3; channel++)
            {
                colors[2, channel] = (byte)((colors[0, channel] + colors[1, channel]) / 2);
                colors[3, channel] = 0;
            }
        }

        return colors;
    }

    private static void Unpack565(ushort value, byte[,] colors, int row)
    {
        colors[row, 0] = (byte)(((value >> 11) & 31) * 255 / 31);
        colors[row, 1] = (byte)(((value >> 5) & 63) * 255 / 63);
        colors[row, 2] = (byte)((value & 31) * 255 / 31);
    }

    public static void WritePngRgba(string path, int width, int height, byte[] rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = File.Create(path);
        stream.Write(PngSignature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(stream, "IHDR", ihdr);

        using var raw = new MemoryStream();
        var rowBytes = checked(width * 4);
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * rowBytes, rowBytes);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] typeBytes, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }

    private sealed record DecodedTexture(string Format, int Width, int Height, int BufferStart, int Mip0Bytes, bool Mipped, byte[] Rgba);
}

public sealed record PhyreTextureExtractionReport(
    DateTimeOffset GeneratedAtUtc,
    string MonsterId,
    FileSnapshot Source,
    string? PngPath,
    string? Format,
    int Width,
    int Height,
    int BufferStart,
    int Mip0Bytes,
    bool Mipped,
    string DecisionBand,
    string NextStrike)
{
    public static PhyreTextureExtractionReport Blocked(string monsterId, FileSnapshot source, string decisionBand, string nextStrike) => new(
        DateTimeOffset.UtcNow,
        monsterId,
        source,
        null,
        null,
        0,
        0,
        0,
        0,
        false,
        decisionBand,
        nextStrike);
}

public sealed record PhyreTextureFileExtractionReport(
    DateTimeOffset GeneratedAtUtc,
    string AssetId,
    FileSnapshot Source,
    string? PngPath,
    string? Format,
    int Width,
    int Height,
    int BufferStart,
    int Mip0Bytes,
    bool Mipped,
    string DecisionBand,
    string NextStrike)
{
    public static PhyreTextureFileExtractionReport Blocked(string assetId, FileSnapshot source, string decisionBand, string nextStrike) => new(
        DateTimeOffset.UtcNow,
        assetId,
        source,
        null,
        null,
        0,
        0,
        0,
        0,
        false,
        decisionBand,
        nextStrike);
}
