// Provenance: ffx-editor-main RuntimeTools/PhyreModelExportLab/Program.cs
// snapshot 3da386821a3134d291d8bece769d3a4f5c6bde49934c8afc46429eea2d7e276a (P01); GPLv3.
// Records extraidos: FileSnapshot, PositionBounds. Adaptacao: Program.ToPortablePath -> PortablePath.Invoke.
using System.Security.Cryptography;

namespace PhyreReader;

public sealed record FileSnapshot(
    string Path,
    bool Exists,
    long? Length,
    string? Sha256)
{
    public static FileSnapshot FromPath(string path, bool portable)
    {
        if (!File.Exists(path))
        {
            return new FileSnapshot(portable ? PortablePath.Invoke(path) : path, false, null, null);
        }

        using var stream = File.OpenRead(path);
        return new FileSnapshot(
            portable ? PortablePath.Invoke(path) : path,
            true,
            stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }
}

public sealed record PositionBounds(
    double[] Min,
    double[] Max);
