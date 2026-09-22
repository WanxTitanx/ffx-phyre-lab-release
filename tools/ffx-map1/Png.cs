// Png — minimal PNG codec (8-bit RGB/RGBA, non-interlaced) for the
// inspector. Encode: ARGB uint[] framebuffer -> .png. Decode: .png ->
// BGRA bytes for texture sampling. Uses ZLibStream; no Avalonia dep.

using System.IO.Compression;

namespace FfxMap1;

public static class Png
{
    static readonly byte[] Sig = { 137, 80, 78, 71, 13, 10, 26, 10 };

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length); Array.Reverse(len);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc(t.Concat(data).ToArray());
        var cb = BitConverter.GetBytes(crc); Array.Reverse(cb);
        s.Write(cb);
    }

    public static void Encode(string path, uint[] argb, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write(Sig);
        var ihdr = new byte[13];
        BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(ihdr, 0);
        BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);
        var raw = new byte[h * (w * 4 + 1)];
        for (int y = 0; y < h; y++)
        {
            int ro = y * (w * 4 + 1); raw[ro] = 0;
            for (int x = 0; x < w; x++)
            {
                uint p = argb[y * w + x];
                raw[ro + 1 + x * 4 + 0] = (byte)(p >> 16);
                raw[ro + 1 + x * 4 + 1] = (byte)(p >> 8);
                raw[ro + 1 + x * 4 + 2] = (byte)p;
                raw[ro + 1 + x * 4 + 3] = (byte)(p >> 24);
            }
        }
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        Chunk(ms, "IDAT", idat.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, ms.ToArray());
    }

    // Decode 8-bit RGB(2)/RGBA(6) non-interlaced PNG -> BGRA bytes.
    public static (byte[] Bgra, int W, int H) Decode(string path)
    {
        var d = File.ReadAllBytes(path);
        if (!d.AsSpan(0, 8).SequenceEqual(Sig)) throw new InvalidDataException("not a PNG");
        int w = 0, h = 0, colorType = -1;
        var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= d.Length)
        {
            int len = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            if (type == "IHDR")
            {
                w = (d[pos + 8] << 24) | (d[pos + 9] << 16) | (d[pos + 10] << 8) | d[pos + 11];
                h = (d[pos + 12] << 24) | (d[pos + 13] << 16) | (d[pos + 14] << 8) | d[pos + 15];
                if (d[pos + 16] != 8) throw new InvalidDataException("only 8-bit PNG supported");
                colorType = d[pos + 17];
                if (d[pos + 20] != 0) throw new InvalidDataException("interlaced PNG not supported");
            }
            else if (type == "IDAT") idat.Write(d, pos + 8, len);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        if (colorType != 2 && colorType != 6)
            throw new InvalidDataException($"PNG colorType {colorType} not supported (need RGB/RGBA)");
        int ch = colorType == 6 ? 4 : 3;
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var rawMs = new MemoryStream();
        z.CopyTo(rawMs);
        var raw = rawMs.ToArray();
        int stride = w * ch + 1;
        var img = new byte[w * h * 4];
        var prev = new byte[w * ch];
        for (int y = 0; y < h; y++)
        {
            int ro = y * stride;
            int filt = raw[ro];
            var line = new byte[w * ch];
            Array.Copy(raw, ro + 1, line, 0, w * ch);
            for (int x = 0; x < w * ch; x++)
            {
                int a = x >= ch ? line[x - ch] : 0;
                int b = prev[x];
                int cc = x >= ch ? prev[x - ch] : 0;
                line[x] = filt switch
                {
                    0 => line[x],
                    1 => (byte)(line[x] + a),
                    2 => (byte)(line[x] + b),
                    3 => (byte)(line[x] + (a + b) / 2),
                    4 => (byte)(line[x] + Paeth(a, b, cc)),
                    _ => throw new InvalidDataException($"bad PNG filter {filt}"),
                };
            }
            for (int x = 0; x < w; x++)
            {
                int s = x * ch, o = (y * w + x) * 4;
                img[o] = line[s + 2]; img[o + 1] = line[s + 1]; img[o + 2] = line[s];
                img[o + 3] = ch == 4 ? line[s + 3] : (byte)255;
            }
            prev = line;
        }
        return (img, w, h);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
