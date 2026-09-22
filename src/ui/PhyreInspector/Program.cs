using Avalonia;
using Avalonia.Media.Imaging;

namespace PhyreInspector;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless render mode (tests + evidence capture, no window):
        //   PhyreInspector --render <asset.dae.phyre|asset.gltf> --out <png>
        //       [--wire] [--joint N] [--yaw f] [--pitch f] [--dist f]
        //       [--set-node "i,tx,ty,tz,rxDeg,ryDeg,rzDeg,sx,sy,sz"]...
        //       [--clip N --time f]
        //       [--session-out <file>] [--session-in <file>] [--undo N] [--redo N]
        if (args.Length >= 1 && args[0] == "--render")
            return HeadlessRender(args[1..]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static int HeadlessRender(string[] args)
    {
        string? input = null, output = null;
        var r = new SoftRenderer();
        var setNodes = new List<float[]>();
        int clipIdx = -1; float time = 0;
        string? sesOut = null, sesIn = null; int undoN = 0, redoN = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 < args.Length)
            {
                switch (args[i])
                {
                    case "--out": output = args[i + 1]; i++; continue;
                    case "--joint": r.HighlightJoint = int.Parse(args[i + 1]); i++; continue;
                    case "--yaw": r.Yaw = float.Parse(args[i + 1]); i++; continue;
                    case "--pitch": r.Pitch = float.Parse(args[i + 1]); i++; continue;
                    case "--dist": r.Dist = float.Parse(args[i + 1]); i++; continue;
                    case "--hide":
                        foreach (var t in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                            r.HiddenPrims.Add(int.Parse(t));
                        i++; continue;
                    case "--time": time = float.Parse(args[i + 1]); i++; continue;
                    case "--clip": clipIdx = int.Parse(args[i + 1]); i++; continue;
                    case "--set-node":
                        setNodes.Add(args[i + 1].Split(',').Select(float.Parse).ToArray());
                        i++; continue;
                    case "--session-out": sesOut = args[i + 1]; i++; continue;
                    case "--session-in": sesIn = args[i + 1]; i++; continue;
                    case "--undo": undoN = int.Parse(args[i + 1]); i++; continue;
                    case "--redo": redoN = int.Parse(args[i + 1]); i++; continue;
                }
            }
            if (!args[i].StartsWith("--")) input ??= args[i];
        }
        input ??= args.LastOrDefault(a => !a.StartsWith("--"));
        if (input == null || output == null)
        {
            Console.Error.WriteLine("usage: --render <asset> --out <png> [--wire] [--joint N] [--yaw f] [--pitch f] [--dist f]");
            return 2;
        }
        if (args.Contains("--wire")) r.Wireframe = true;

        var workDir = Path.Combine(Path.GetTempPath(), "phyre-inspector-headless");
        var (scene, err) = InspectorCore.LoadAsset(input, workDir);
        if (err != null)
        {
            Console.Error.WriteLine($"error: {err.What}: {err.Detail}");
            Console.Error.WriteLine($"hint: {err.Hint}");
            return 1;
        }
        // pose edits go through the journal (undo/redo + session persistence)
        var journal = new PoseJournal();
        if (sesIn != null)
        {
            var (j, lerr) = PoseJournal.Load(sesIn);
            if (lerr != null) { Console.Error.WriteLine($"error: session load failed: {lerr}"); return 1; }
            journal = j!;
            journal.ApplyTo(scene!);
        }
        foreach (var sn in setNodes)
        {
            if (sn.Length != 10 || sn[0] < 0 || sn[0] >= scene!.PoseT.Length)
            {
                Console.Error.WriteLine($"error: --set-node needs 10 numbers, node index in range");
                return 1;
            }
            int n = (int)sn[0];
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(
                sn[5] * MathF.PI / 180, sn[4] * MathF.PI / 180, sn[6] * MathF.PI / 180);
            journal.Record(n,
                new System.Numerics.Vector3(sn[1], sn[2], sn[3]), q,
                new System.Numerics.Vector3(sn[7], sn[8], sn[9]));
        }
        for (int i = 0; i < undoN; i++) journal.Undo();
        for (int i = 0; i < redoN; i++) journal.Redo();
        journal.ApplyTo(scene!);
        if (sesOut != null) journal.Save(sesOut);
        if (clipIdx >= 0)
        {
            if (scene!.Animations.Count == 0)
            {
                Console.Error.WriteLine("error: scene has no animation clips");
                return 1;
            }
            scene.SampleAnimation(scene.Animations[Math.Min(clipIdx, scene.Animations.Count - 1)], time);
        }
        if (scene!.TexturePng != null && File.Exists(scene.TexturePng)) r.SetTexture(scene.TexturePng);
        var fb = r.Render(scene);
        Png.Encode(output, fb, r.W, r.H);
        Console.WriteLine($"rendered {output}: {scene.Prims.Count} prims, {scene.SkinJoints.Length} joints, " +
                          $"{scene.Animations.Count} anim(s), journal {journal.Cursor}/{journal.Ops.Count} ops, " +
                          $"bounds [{scene.BoundsMin}..{scene.BoundsMax}]");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
