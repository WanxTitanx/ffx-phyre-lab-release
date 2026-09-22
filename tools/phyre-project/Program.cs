// phyre-project — P13 persistent authoring container for ffx-phyre-lab.
//
// Format `phyre.ops.v1` — deliberately separate from phyre.project.v1 (the
// content/interchange document). This layer only knows bytes + operations:
//
//   <proj>/project.json     schema version, sources by sha256, op list, cursor
//   <proj>/sources/<sha256> immutable copy of each imported source
//   <proj>/journal/ops.jsonl    append-only op log (import/patch records)
//   <proj>/journal/payloads/<seq>.bin   patch payload bytes
//
// Semantics:
//   - ops[0..cursor) are applied; undo moves cursor back, redo forward.
//   - materialize replays applied ops over the stored source -> output bytes.
//   - atomic save: project.json written to .tmp then renamed (os.replace).
//   - recovery: `recover` rebuilds project.json purely from journal/ops.jsonl.
//   - missing source: verify reports the sha256/path_hint and exits 5 instead
//     of silently skipping.
//
// Exit codes: 0 ok; 2 materialization/structure failure; 3 unsafe/invalid op;
// 4 IO/usage; 5 missing source(s); 6 journal/project inconsistent.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

static int Fail(int code, string msg)
{
    Console.Error.WriteLine(msg);
    return code;
}

static string Sha256(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

// ---------- project model (type declarations live at the end of file) ----------

static string ProjPath(string dir) => Path.Combine(dir, "project.json");
static string JournalPath(string dir) => Path.Combine(dir, "journal", "ops.jsonl");
static string PayloadDir(string dir) => Path.Combine(dir, "journal", "payloads");
static string SourceDir(string dir) => Path.Combine(dir, "sources");

static void SaveAtomic(string dir, Project p)
{
    Directory.CreateDirectory(dir);
    var doc = new JsonObject
    {
        ["schema"] = "phyre.ops.v1",
        ["cursor"] = p.Cursor,
        ["result_sha256"] = p.ResultSha256,
        ["sources"] = new JsonArray(p.Sources.Select(s => (JsonNode)s.DeepClone()).ToArray()),
        ["ops"] = new JsonArray(p.Ops.Select(o => (JsonNode)new JsonObject
        {
            ["seq"] = o.Seq, ["kind"] = o.Kind, ["target"] = o.Target,
            ["offset"] = o.Offset, ["payload_sha256"] = o.PayloadSha256,
            ["payload_file"] = o.PayloadFile, ["note"] = o.Note,
        }).ToArray()),
    };
    var tmp = ProjPath(dir) + ".tmp";
    File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.Move(tmp, ProjPath(dir), overwrite: true); // atomic same-volume rename
}

static Project? LoadProject(string dir)
{
    if (!File.Exists(ProjPath(dir))) return null;
    var doc = JsonNode.Parse(File.ReadAllText(ProjPath(dir))) as JsonObject;
    if (doc?["schema"]?.GetValue<string>() != "phyre.ops.v1") return null;
    var p = new Project { Cursor = doc["cursor"]!.GetValue<int>() };
    p.ResultSha256 = doc["result_sha256"]?.GetValue<string>();
    foreach (var s in doc["sources"]!.AsArray())
        p.Sources.Add((JsonObject)s!.AsObject().DeepClone());
    foreach (var o in doc["ops"]!.AsArray())
    {
        var j = o!.AsObject();
        p.Ops.Add(new Op
        {
            Seq = j["seq"]!.GetValue<int>(), Kind = j["kind"]!.GetValue<string>(),
            Target = j["target"]!.GetValue<string>(), Offset = j["offset"]!.GetValue<long>(),
            PayloadSha256 = j["payload_sha256"]!.GetValue<string>(),
            PayloadFile = j["payload_file"]!.GetValue<string>(),
            Note = j["note"]?.GetValue<string>() ?? "",
        });
    }
    return p;
}

static void Journal(string dir, JsonObject rec)
{
    Directory.CreateDirectory(PayloadDir(dir));
    File.AppendAllText(JournalPath(dir), rec.ToJsonString() + "\n");
}

// ---------- commands ----------

static int CmdNew(string srcPath, string dir, string key)
{
    if (!File.Exists(srcPath)) return Fail(4, $"missing source: {srcPath}");
    if (File.Exists(ProjPath(dir))) return Fail(3, $"project exists: {dir}");
    Directory.CreateDirectory(dir);
    var bytes = File.ReadAllBytes(srcPath);
    var hash = Sha256(bytes);
    Directory.CreateDirectory(SourceDir(dir));
    var stored = Path.Combine("sources", hash);
    File.WriteAllBytes(Path.Combine(dir, stored), bytes);
    var p = new Project();
    p.Sources.Add(new JsonObject
    {
        ["key"] = key, ["sha256"] = hash,
        ["path_hint"] = Path.GetFullPath(srcPath), ["stored"] = stored,
    });
    Journal(dir, new JsonObject
    {
        ["seq"] = -1, ["kind"] = "import", ["key"] = key, ["sha256"] = hash,
        ["path_hint"] = Path.GetFullPath(srcPath),
    });
    SaveAtomic(dir, p);
    Console.WriteLine(JsonSerializer.Serialize(new { dir, key, sha256 = hash, bytes = bytes.Length }));
    return 0;
}

static int CmdAddPatch(string dir, string target, long off, byte[] payload, string note)
{
    var p = LoadProject(dir) ?? throw new InvalidDataException("no project");
    var src = p.Sources.FirstOrDefault(s => s["key"]!.GetValue<string>() == target)
        ?? throw new InvalidDataException($"unknown source key: {target}");
    if (off < 0 || off + payload.Length > new FileInfo(Path.Combine(dir, src["stored"]!.GetValue<string>())).Length)
        return Fail(3, $"patch [{off},{off + payload.Length}) out of bounds");
    // truncate redo tail: ops past cursor are discarded on new op (standard undo semantics)
    if (p.Cursor < p.Ops.Count) p.Ops.RemoveRange(p.Cursor, p.Ops.Count - p.Cursor);
    int seq = p.Ops.Count;
    var pf = Path.Combine("journal", "payloads", $"{seq:D4}.bin");
    File.WriteAllBytes(Path.Combine(dir, pf), payload);
    var ph = Sha256(payload);
    var op = new Op { Seq = seq, Kind = "patch", Target = target, Offset = off,
                      PayloadSha256 = ph, PayloadFile = pf, Note = note };
    p.Ops.Add(op);
    p.Cursor = p.Ops.Count;
    p.ResultSha256 = null; // materialized result invalidated by new op
    Journal(dir, new JsonObject
    {
        ["seq"] = seq, ["kind"] = "patch", ["target"] = target, ["offset"] = off,
        ["payload_sha256"] = ph, ["payload_file"] = pf, ["note"] = note,
    });
    SaveAtomic(dir, p);
    Console.WriteLine(JsonSerializer.Serialize(new { seq, cursor = p.Cursor }));
    return 0;
}

static int CmdUndoRedo(string dir, int delta)
{
    var p = LoadProject(dir) ?? throw new InvalidDataException("no project");
    int n = Math.Clamp(p.Cursor + delta, 0, p.Ops.Count);
    p.Cursor = n;
    p.ResultSha256 = null;
    SaveAtomic(dir, p);
    Console.WriteLine(JsonSerializer.Serialize(new { cursor = p.Cursor, ops = p.Ops.Count }));
    return 0;
}

static byte[] Materialize(string dir, Project p)
{
    var patches = p.Ops.Take(p.Cursor).Where(o => o.Kind == "patch").ToList();
    var groups = patches.GroupBy(o => o.Target);
    if (groups.Count() > 1) throw new InvalidDataException("multi-source materialize not supported");
    var target = patches.FirstOrDefault()?.Target ?? p.Sources[0]["key"]!.GetValue<string>();
    var src = p.Sources.First(s => s["key"]!.GetValue<string>() == target);
    var spath = Path.Combine(dir, src["stored"]!.GetValue<string>());
    if (!File.Exists(spath)) throw new FileNotFoundException($"stored source missing: {src["sha256"]}");
    var bytes = File.ReadAllBytes(spath);
    if (Sha256(bytes) != src["sha256"]!.GetValue<string>())
        throw new InvalidDataException($"stored source hash mismatch: {src["sha256"]}");
    foreach (var op in patches)
    {
        var pb = File.ReadAllBytes(Path.Combine(dir, op.PayloadFile));
        if (Sha256(pb) != op.PayloadSha256) throw new InvalidDataException($"payload hash mismatch seq {op.Seq}");
        if (op.Offset < 0 || op.Offset + pb.Length > bytes.Length)
            throw new InvalidDataException($"op {op.Seq} out of bounds");
        Array.Copy(pb, 0, bytes, op.Offset, pb.Length);
    }
    return bytes;
}

static int CmdMaterialize(string dir, string outPath)
{
    var p = LoadProject(dir) ?? throw new InvalidDataException("no project");
    var bytes = Materialize(dir, p);
    File.WriteAllBytes(outPath, bytes);
    p.ResultSha256 = Sha256(bytes);
    SaveAtomic(dir, p);
    Console.WriteLine(JsonSerializer.Serialize(new
    { outPath, bytes = bytes.Length, result_sha256 = p.ResultSha256, applied = p.Cursor }));
    return 0;
}

// reopen check: verify stored sources by hash, replay ops, compare against
// recorded result — proves semantic equality across processes.
static int CmdVerify(string dir)
{
    var p = LoadProject(dir);
    if (p is null) return Fail(6, $"no project or bad schema: {dir}");
    var missing = new List<object>();
    foreach (var s in p.Sources)
    {
        var spath = Path.Combine(dir, s["stored"]!.GetValue<string>());
        if (File.Exists(spath))
        {
            if (Sha256(File.ReadAllBytes(spath)) != s["sha256"]!.GetValue<string>())
                return Fail(6, $"corrupt stored source: {s["key"]}");
            continue;
        }
        var hint = s["path_hint"]!.GetValue<string>();
        if (File.Exists(hint) && Sha256(File.ReadAllBytes(hint)) == s["sha256"]!.GetValue<string>())
            continue; // external source still available
        missing.Add(new { key = s["key"]!.GetValue<string>(), sha256 = s["sha256"]!.GetValue<string>(), path_hint = hint });
    }
    if (missing.Count > 0)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, missing }));
        return 5;
    }
    var bytes = Materialize(dir, p);
    var current = Sha256(bytes);
    var equal = p.ResultSha256 == null || p.ResultSha256 == current;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = equal, schema = "phyre.ops.v1", cursor = p.Cursor, ops = p.Ops.Count,
        sources = p.Sources.Count, materialized_sha256 = current,
        recorded_sha256 = p.ResultSha256, semantic_equal = equal,
    }));
    return equal ? 0 : 6;
}

// rebuild project.json from journal/ops.jsonl alone (crash recovery path)
static int CmdRecover(string dir)
{
    var jl = JournalPath(dir);
    if (!File.Exists(jl)) return Fail(6, "no journal to recover from");
    var p = new Project();
    foreach (var line in File.ReadAllLines(jl))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var j = JsonNode.Parse(line)!.AsObject();
        var kind = j["kind"]!.GetValue<string>();
        if (kind == "import")
        {
            var hash = j["sha256"]!.GetValue<string>();
            p.Sources.Add(new JsonObject
            {
                ["key"] = j["key"]!.GetValue<string>(), ["sha256"] = hash,
                ["path_hint"] = j["path_hint"]!.GetValue<string>(),
                ["stored"] = Path.Combine("sources", hash),
            });
        }
        else if (kind == "patch")
        {
            p.Ops.Add(new Op
            {
                Seq = j["seq"]!.GetValue<int>(), Kind = "patch",
                Target = j["target"]!.GetValue<string>(), Offset = j["offset"]!.GetValue<long>(),
                PayloadSha256 = j["payload_sha256"]!.GetValue<string>(),
                PayloadFile = j["payload_file"]!.GetValue<string>(),
                Note = j["note"]?.GetValue<string>() ?? "",
            });
        }
    }
    p.Ops.Sort((a, b) => a.Seq.CompareTo(b.Seq));
    p.Cursor = p.Ops.Count;
    SaveAtomic(dir, p);
    Console.WriteLine(JsonSerializer.Serialize(new { recovered = true, ops = p.Ops.Count, sources = p.Sources.Count }));
    return 0;
}

try
{
    if (args.Length < 2)
        return Fail(4, "usage: new <src> <dir> [key] | add-patch <dir> <key> <off> <hex|@file> [note] | undo|redo <dir> [n] | materialize <dir> <out> | verify <dir> | recover <dir>");
    switch (args[0])
    {
        case "new": return args.Length >= 3 ? CmdNew(args[1], args[2], args.Length > 3 ? args[3] : "src0") : Fail(4, "new needs src+dir");
        case "add-patch":
        {
            if (args.Length < 5) return Fail(4, "add-patch <dir> <key> <off> <hex|@file> [note]");
            var payload = args[4].StartsWith('@') ? File.ReadAllBytes(args[4][1..]) : Convert.FromHexString(args[4]);
            return CmdAddPatch(args[1], args[2], long.Parse(args[3]), payload, args.Length > 5 ? args[5] : "");
        }
        case "undo": return CmdUndoRedo(args[1], -(args.Length > 2 ? int.Parse(args[2]) : 1));
        case "redo": return CmdUndoRedo(args[1], (args.Length > 2 ? int.Parse(args[2]) : 1));
        case "materialize": return args.Length >= 3 ? CmdMaterialize(args[1], args[2]) : Fail(4, "materialize needs out");
        case "verify": return CmdVerify(args[1]);
        case "recover": return CmdRecover(args[1]);
        default: return Fail(4, $"unknown command {args[0]}");
    }
}
catch (Exception ex)
{
    return Fail(2, $"error: {ex.GetType().Name}: {ex.Message}");
}

// ---------- type declarations (must follow all top-level statements) ----------

sealed class Op
{
    public int Seq;
    public string Kind = "";          // "patch"
    public string Target = "";        // source key
    public long Offset;
    public string PayloadSha256 = "";
    public string PayloadFile = "";   // journal/payloads/<seq>.bin
    public string Note = "";
}

sealed class Project
{
    public List<JsonObject> Sources = new();  // {key, sha256, path_hint, stored}
    public List<Op> Ops = new();
    public int Cursor;                        // ops[0..Cursor) applied
    public string? ResultSha256;              // last materialized output hash
}
