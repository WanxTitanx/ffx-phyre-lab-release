// PoseJournal — pose-edit operation journal with undo/redo cursor and
// JSON save/load (P15). Scene-level analogue of phyre.ops.v1 semantics:
// append-only ops, cursor for undo/redo, reopen = reapply ops[0..cursor).
// This is SESSION pose data, not a phyre writer — it never claims to
// serialize a .phyre or glTF animation clip.

using System.Numerics;
using System.Text.Json;

namespace PhyreInspector;

public sealed class PoseJournal
{
    public sealed class Op
    {
        public int Node { get; set; }
        public float[] T { get; set; } = new float[3];
        public float[] R { get; set; } = new float[4];
        public float[] S { get; set; } = new float[3];
    }

    public List<Op> Ops = new();
    public int Cursor;                       // ops[0..Cursor) are applied

    public void Record(int node, Vector3 t, Quaternion r, Vector3 s)
    {
        Ops.RemoveRange(Cursor, Ops.Count - Cursor);  // truncate redo tail
        Ops.Add(new Op
        {
            Node = node,
            T = new[] { t.X, t.Y, t.Z },
            R = new[] { r.X, r.Y, r.Z, r.W },
            S = new[] { s.X, s.Y, s.Z },
        });
        Cursor = Ops.Count;
    }

    public bool CanUndo => Cursor > 0;
    public bool CanRedo => Cursor < Ops.Count;
    public void Undo() { if (CanUndo) Cursor--; }
    public void Redo() { if (CanRedo) Cursor++; }

    // Reapply ops[0..Cursor) onto a fresh bind pose.
    public void ApplyTo(GltfScene scene)
    {
        scene.ResetPose();
        for (int i = 0; i < Cursor; i++)
        {
            var o = Ops[i];
            if (o.Node >= scene.PoseT.Length) continue;
            scene.SetNodeTransform(o.Node,
                new Vector3(o.T[0], o.T[1], o.T[2]),
                new Quaternion(o.R[0], o.R[1], o.R[2], o.R[3]),
                new Vector3(o.S[0], o.S[1], o.S[2]));
        }
    }

    public void Save(string path)
    {
        var doc = new { schema = "phyre-inspector-pose.v1", cursor = Cursor, ops = Ops };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc));
        File.Move(tmp, path, overwrite: true);   // atomic-ish save
    }

    public static (PoseJournal?, string? Error) Load(string path)
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            if (doc.GetProperty("schema").GetString() != "phyre-inspector-pose.v1")
                return (null, "not a pose session file (schema mismatch)");
            var j = new PoseJournal();
            foreach (var e in doc.GetProperty("ops").EnumerateArray())
                j.Ops.Add(new Op
                {
                    Node = e.GetProperty("Node").GetInt32(),
                    T = e.GetProperty("T").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(),
                    R = e.GetProperty("R").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(),
                    S = e.GetProperty("S").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(),
                });
            j.Cursor = Math.Min(doc.GetProperty("cursor").GetInt32(), j.Ops.Count);
            return (j, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
