// InspectorCore — non-UI core: asset discovery, export subprocess,
// error reporting. Reuses tools/phyre-exporter as the extraction
// backend; no hidden logic in event handlers (P14 contract).

using System.Diagnostics;

namespace PhyreInspector;

public sealed record InspectorError(string What, string Detail, string Hint);

public static class InspectorCore
{
    public static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));

    public static string ExporterDir => Path.Combine(RepoRoot, "tools", "phyre-exporter");

    public static string ExporterDll =>
        Path.Combine(ExporterDir, "bin", "Debug", "net10.0", "phyre-exporter.dll");

    public static string ExporterCsproj => Path.Combine(ExporterDir, "PhyreExporter.csproj");

    // Run `dotnet <args>` with captured output; returns (exitCode, output tail).
    private static (int Exit, string Tail) RunDotnet(string args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var pr = Process.Start(psi)!;
        string so = pr.StandardOutput.ReadToEnd(), se = pr.StandardError.ReadToEnd();
        pr.WaitForExit(timeoutMs);
        var text = (se + so).Trim();
        if (text.Length > 1500) text = "…\n" + text[^1500..];
        return (pr.ExitCode, text);
    }

    // Ensure the exporter is built; builds once only when the dll is missing.
    private static InspectorError? EnsureExporterBuilt()
    {
        if (File.Exists(ExporterDll)) return null;
        if (!File.Exists(ExporterCsproj))
            return new InspectorError("exporter missing", ExporterCsproj,
                "Clone/checkout incomplete — tools/phyre-exporter is required.");
        var (exit, tail) = RunDotnet($"build \"{ExporterCsproj}\" --nologo");
        if (exit != 0 || !File.Exists(ExporterDll))
            return new InspectorError("exporter build failed", $"exit={exit}: {tail}",
                "Fix the tools/phyre-exporter build errors, then Scan again.");
        return null;
    }

    // Discover loadable assets under a root (phyre models, gltf, projects).
    public static List<string> DiscoverAssets(string root)
    {
        var hits = new List<string>();
        if (!Directory.Exists(root)) return hits;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var n = f.ToLowerInvariant();
            if (n.EndsWith(".dae.phyre") || n.EndsWith(".gltf") || n.EndsWith(".glb"))
                hits.Add(f);
        }
        hits.Sort();
        return hits;
    }

    // Export .phyre -> gltf via the proven exporter (subprocess boundary).
    // Returns (gltfPath, error). Never fabricates success.
    public static (string? Path, InspectorError? Error) ExportToGltf(string phyrePath, string workDir)
    {
        if (!File.Exists(phyrePath))
            return (null, new InspectorError("asset missing", phyrePath,
                "Verify the corpus path exists; run scripts/build_corpus.py if it was removed."));
        var buildErr = EnsureExporterBuilt();
        if (buildErr != null) return (null, buildErr);
        Directory.CreateDirectory(workDir);
        var mon = Path.GetFileNameWithoutExtension(phyrePath);
        if (mon.EndsWith(".dae", StringComparison.OrdinalIgnoreCase)) mon = mon[..^4];
        var gltfOut = Path.Combine(workDir, mon + ".gltf");
        try
        {
            var (exit, tail) = RunDotnet($"\"{ExporterDll}\" \"{phyrePath}\" \"{gltfOut}\"");
            if (exit != 0)
                return (null, new InspectorError("exporter failed",
                    $"exit={exit}: {tail}", "Check the .phyre is a supported FFX model (RYHP magic)."));
            if (!File.Exists(gltfOut))
                return (null, new InspectorError("no output", gltfOut,
                    "Exporter produced no .gltf — check stderr log for skipped meshes."));
            return (gltfOut, null);
        }
        catch (Exception ex)
        {
            return (null, new InspectorError("exporter launch failed", ex.Message,
                "dotnet SDK must be on PATH; build tools/phyre-exporter."));
        }
    }

    // Load an asset: .gltf direct, .phyre via export. Returns scene + diagnostics.
    public static (GltfScene? Scene, InspectorError? Error) LoadAsset(string path, string workDir)
    {
        try
        {
            if (path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                return (GltfScene.Load(path), null);
            var (g, err) = ExportToGltf(path, workDir);
            if (err != null) return (null, err);
            return (GltfScene.Load(g!), null);
        }
        catch (Exception ex)
        {
            return (null, new InspectorError("parse failed", $"{path}: {ex.Message}",
                "File may be a texture .phyre or unsupported class layout — try a .dae.phyre model."));
        }
    }
}
