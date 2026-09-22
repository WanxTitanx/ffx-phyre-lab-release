// phyre-reader — CLI mínima que varre .phyre do corpus e emite resumo JSON.
// Uso: phyre-reader <dir|file.phyre> <out.json>
using System.Text.Json;
using PhyreReader;

var input = args.Length > 0 ? args[0] : throw new ArgumentException("input path required");
var outPath = args.Length > 1 ? args[1] : null;

var files = File.Exists(input)
    ? new[] { input }
    : Directory.GetFiles(input, "*.phyre", SearchOption.AllDirectories);

var results = new List<object>();
foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
{
    var id = Path.GetFileName(f);
    try
    {
        var r = PhyreDescriptorParser.Parse(id, f, portable: true);
        results.Add(new
        {
            file = f,
            decision = r.DecisionBand,
            promotion = r.PromotionStatus,
            objectBlocks = r.ObjectBlocks.Length,
            meshes = r.MeshCount,
            segments = r.MeshSegmentCount,
            dataBlocks = r.DataBlockCount,
            vertexStreams = r.VertexStreamCount,
            submeshes = r.DescriptorSubmeshCount,
            totalIndices = r.TotalIndexCount,
            totalVertices = r.TotalVertexCount,
            warnings = r.Warnings,
        });
    }
    catch (Exception ex)
    {
        results.Add(new { file = f, decision = "unhandled_exception", error = ex.Message });
    }
}

var json = JsonSerializer.Serialize(new { input, count = results.Count, results },
    new JsonSerializerOptions { WriteIndented = true });
if (outPath is null) Console.WriteLine(json);
else File.WriteAllText(outPath, json + "\n");
Console.Error.WriteLine($"parsed {results.Count} files");
