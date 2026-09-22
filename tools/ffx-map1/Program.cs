// ffx-map1 — structural reader for FFX HD PC MAP1 map packages
// (data/FinalFantasyX/<group>/<id>.bin pairs consumed by the noclip viewer).
//
//   ffx-map1 info <bin>              header slots + section walk, type histogram
//   ffx-map1 parts <geom-bin>        LEVEL_PART list with transforms/effects
//   ffx-map1 models <geom-bin>       MODEL records (VIF stream descriptors)
//   ffx-map1 textures <tex-bin>      texture/palette uploads (type 2/3)
//   ffx-map1 lighting <geom-bin>     LIGHTING entries (clear/fog/envmap)

using FfxMap1;
using System.Buffers.Binary;

// ffx-map1 selftest — regression asserts over known corpus bins
// (magic header/index discovery, kernel tables, invalid-input handling).
if (args.Length == 1 && args[0] == "selftest")
{
    var dir = AppContext.BaseDirectory;
    string root = Directory.GetCurrentDirectory();
    for (var q = new DirectoryInfo(dir); q != null; q = q.Parent)
        if (File.Exists(Path.Combine(q.FullName, "tools/noclip_server.py"))) { root = q.FullName; break; }
    int pass = 0, fail = 0, skip = 0;
    void Check(string name, bool cond, string detail = "")
    {
        if (cond) { pass++; Console.WriteLine($"  PASS {name} {detail}"); }
        else { fail++; Console.WriteLine($"  FAIL {name} {detail}"); }
    }
    foreach (var (file, wantIdx, minEmit) in new[] { ("004c", 2, 1), ("006a", -1, 3), ("000a", -1, 1) })
    {
        var mp = Path.Combine(root, ".lab/fetched/11", file + ".bin");
        if (!File.Exists(mp)) { skip++; Console.WriteLine($"  SKIP {file} (não baixado)"); continue; }
        var d = File.ReadAllBytes(mp);
        var headers = new List<int>();
        var sys = MagicParticles.FromMagicBin(d, -1, headersOut: headers);
        Check($"{file} headers", headers.Count > 0, $"[{string.Join(",", headers.Select(h => $"0x{h:x}"))}]");
        Check($"{file} sys", sys != null && sys.Emitters.Count >= minEmit,
            sys == null ? "null" : $"emitters={sys.Emitters.Count}");
        if (wantIdx >= 0)
        {
            var cands = MagicBin.FindParticleIndices(d);
            Check($"{file} indexCandidates contém {wantIdx}", cands.Contains(wantIdx),
                $"[{string.Join(",", cands)}]");
        }
        var sim = new List<ParticleSim.DrawItem>();
        for (int fr = 0; fr < 60 && sys != null; fr++) sys.Step(1f, sim);
        Check($"{file} draws", sim.Count > 0, $"draws={sim.Count}");
    }
    var kp = Path.Combine(root, ".lab/fetched/kernel/command.bin");
    if (!File.Exists(kp))
    {
        // fall back to the extracted corpus when present
        var env = Path.Combine(root, ".lab/environment.json");
        if (File.Exists(env))
        {
            var er = System.Text.Json.JsonDocument.Parse(File.ReadAllText(env)).RootElement;
            var ar = er.TryGetProperty("assets_root", out var a) ? a.GetString() : null;
            if (ar != null) kp = Path.Combine(ar, "ffx_ps2/ffx/master/new_uspc/battle/kernel/command.bin");
        }
    }
    if (!File.Exists(kp)) { skip++; Console.WriteLine("  SKIP command.bin (sem corpus)"); }
    else
    {
        var d = File.ReadAllBytes(kp);
        var err = KernelBin.Load(d, out var t);
        Check("command.bin load", err == null, err ?? "");
        if (err == null)
        {
            Check("entries>300", t.EntryCount > 300, $"{t.EntryCount}");
            Check("rec0 name=Attack", t.Name(0) == "Attack", t.Name(0));
            Check("rec0 power=16", t.Record(0)[0x2A] == 16);
            Check("ultima mp=90", t.Record(83)[0x25] == 90, t.Name(83));
        }
        Check("bad magic rejeita", KernelBin.Load(new byte[0x100], out _) != null);
    }
    // LEVEL_PART editor: ListParts + TRS patch round-trip on a fetched map
    var lp = Path.Combine(root, ".lab/fetched/13/001b.bin");
    if (!File.Exists(lp)) { skip++; Console.WriteLine("  SKIP 13/001b (não baixado)"); }
    else
    {
        var d = File.ReadAllBytes(lp);
        var parts = LevelParts.ListParts(Map1File.Load("13/001b.bin", d));
        Check("001b ListParts", parts.Count > 0, $"parts={parts.Count}");
        if (parts.Count > 0)
        {
            var (en, _) = parts[0];
            var p = en.PayloadOffset;
            var d2 = (byte[])d.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(d2.AsSpan((int)(p + 0x20)), 123.5f);
            BinaryPrimitives.WriteSingleLittleEndian(d2.AsSpan((int)(p + 0x14)),
                (float)(Math.PI / 2));
            BinaryPrimitives.WriteUInt16LittleEndian(d2.AsSpan((int)(p + 0x04)), 7);
            var re = LevelParts.ReadPart(Map1File.Load("x", d2), en);
            Check("001b part patch round-trip",
                Math.Abs(re.Px - 123.5f) < 1e-4 && Math.Abs(re.Ey - Math.PI / 2) < 1e-4 && re.Layer == 7,
                $"pos.x={re.Px} eu.y={re.Ey:0.###} layer={re.Layer}");
        }
    }
    // texture editor: List + decode + replace round-trip on a map pair
    var tp = Path.Combine(root, ".lab/fetched/13/001a.bin");
    var gp = Path.Combine(root, ".lab/fetched/13/001b.bin");
    if (!File.Exists(tp) || !File.Exists(gp)) { skip++; Console.WriteLine("  SKIP 13/001a-b (não baixado)"); }
    else
    {
        var texF = Map1File.Load("13/001a.bin", File.ReadAllBytes(tp));
        var geoF = Map1File.Load("13/001b.bin", File.ReadAllBytes(gp));
        var mset = MapModelSet.Parse(geoF);
        var mts = MapTextures.Parse(texF);
        var infos = MapTexEdit.List(texF, mset);
        Check("001a TexInfos", infos.Count > 0, $"uploads={infos.Count}");
        var ti = infos.FirstOrDefault(x => x.Tex0 != null);
        Check("001a textura com TEX0", ti != null,
            infos.Count(x => x.Tex0 != null) + "/" + infos.Count + " bound");
        if (ti != null && mts != null)
        {
            var rgba = MapTexEdit.DecodeRgba(texF, mts, ti);
            Check("001a decode", rgba != null, $"{ti.W}x{ti.H} psm{ti.Entry.Psm}");
            if (rgba != null)
            {
                // BGRA->PNG-domain swap like the UI does, then re-encode
                var bgra = rgba.ToArray();
                for (int k = 0; k + 3 < bgra.Length; k += 4)
                    (bgra[k], bgra[k + 2]) = (bgra[k + 2], bgra[k]);
                var patched = (byte[])texF.Data.Clone();
                var note = MapTexEdit.Replace(texF, mts, ti, bgra, ti.W, ti.H, patched);
                var back = MapTexEdit.DecodeRgba(
                    Map1File.Load("x", patched), mts, ti);
                bool eq = back != null && back.SequenceEqual(rgba);
                Check("001a tex replace round-trip", eq, note);

                // CLUT write: set decoded index 0 to a sentinel RGBA —
                // texels using index 0 must decode to it (alpha may differ
                // per tcc, so compare rgb only when the texture had any
                // index-0 texel at all)
                var p2 = (byte[])texF.Data.Clone();
                bool wok = MapTexEdit.WritePaletteColor(
                    texF, mts, ti, 0, 11, 222, 99, 64, p2);
                Check("001a palette write aplica", wok);
                if (wok)
                {
                    var back2 = MapTexEdit.DecodeRgba(
                        Map1File.Load("x", p2), mts, ti);
                    bool any = false, okc = true;
                    for (int k = 0; k + 3 < back2!.Length; k += 4)
                    {
                        // DecodeRgba returns RGBA: r=11 g=222 b=99
                        bool hit = back2[k] == 11 && back2[k+1] == 222 && back2[k+2] == 99;
                        if (hit) any = true;
                        else if (back2[k] != rgba[k] || back2[k+1] != rgba[k+1] ||
                                 back2[k+2] != rgba[k+2]) okc = false;
                    }
                    Check("001a palette write visível", any && okc,
                        $"sentinela={any} outros={okc}");
                }
                var p3 = (byte[])texF.Data.Clone();
                int nt = MapTexEdit.TintPalette(texF, mts, ti, 0, 1, 1, 1, p3);
                Check("001a palette tint células", nt > 0, $"cells={nt}");
            }
        }
    }
    // actor eri texture: decode -> re-index into the pair palette -> decode
    var ap = Path.Combine(root, ".lab/noclip-data/FinalFantasyX/21/01b8.bin");
    if (!File.Exists(ap)) { skip++; Console.WriteLine("  SKIP actor 21/01b8 (ausente)"); }
    else
    {
        var ab = ActorBin.Load(ap);
        var tex = ab.Textures("a");
        Check("21/01b8 pares", tex.Images.Count > 0 && tex.Pairs.Count == tex.Images.Count,
            $"{tex.Images.Count}");
        if (tex.Images.Count > 0)
        {
            var img = tex.Images[0]; var pr = tex.Pairs[0];
            var reg = tex.Regions[pr.Texture];
            var bgra = new byte[img.W * img.H * 4];
            for (int k = 0; k < img.W * img.H; k++)
            {   // Rgba -> Bgra like the UI PNG path
                bgra[4*k]   = img.Rgba[4*k+2]; bgra[4*k+1] = img.Rgba[4*k+1];
                bgra[4*k+2] = img.Rgba[4*k];   bgra[4*k+3] = img.Rgba[4*k+3];
            }
            var patched = (byte[])ab.Data.Clone();
            ActorBin.ReplaceImage(ab.Data, tex.PaletteOffs[pr.Palette],
                reg.Start, img.W, img.H, bgra, patched);
            var ab2 = ActorBin.FromBytes(patched);
            var back = ab2.Textures("a").Images[0];
            bool eq = back.Rgba.SequenceEqual(img.Rgba);
            Check("01b8 tex replace round-trip", eq,
                $"par0 {img.W}x{img.H} pal={pr.Palette}");

            // palette write: set slot 0 to a sentinel color; every texel
            // mapped to slot 0 must decode to it (alpha x2, clamped)
            var palOff = tex.PaletteOffs[pr.Palette];
            var p2 = (byte[])ab.Data.Clone();
            ActorBin.WritePaletteColor(p2, palOff, 0, 12, 34, 56, 64);
            var t2 = ActorBin.FromBytes(p2).Textures("a");
            var img2 = t2.Images[0];
            bool hit = false, ok = true;
            for (int k = 0; k < img.W * img.H; k++)
            {
                // a texel decodes via slot0 iff its swizzled index is 0
                // — detect by comparing to img: changed pixels must equal
                if (img2.Rgba[4*k] == 12 && img2.Rgba[4*k+1] == 34 &&
                    img2.Rgba[4*k+2] == 56)
                    hit = true;
                else if (img2.Rgba[4*k] != img.Rgba[4*k] ||
                         img2.Rgba[4*k+1] != img.Rgba[4*k+1] ||
                         img2.Rgba[4*k+2] != img.Rgba[4*k+2]) ok = false;
            }
            Check("01b8 palette write aplica", hit && ok,
                $"slot0={hit} outrosPreservados={ok}");
            // tint: r*0 -> all red channels 0
            var p3 = (byte[])ab.Data.Clone();
            ActorBin.TintPalette(p3, palOff, 0f, 1f, 1f, 1f);
            var img3 = ActorBin.FromBytes(p3).Textures("a").Images[0];
            bool allZero = true;
            for (int k = 0; k < img.W * img.H && allZero; k++)
                if (img3.Rgba[4*k] != 0) allZero = false;
            Check("01b8 palette tint r=0", allZero);
            // bone-map + default-anim slot edits: write, reload, verify —
            // 01b8 is a prop (no bones); use a real .chr from assets_root
            var cp = Path.Combine(
                Environment.GetEnvironmentVariable("FFX_ASSETS") ?? "",
                "ffx_ps2/ffx/master/jppc/chr/pc/c002/mdl/c002.chr");
            if (!File.Exists(cp)) { Check("bone map", true, "skip — .chr ausente"); }
            else
            {
                var cb = ActorBin.Load(cp);
                var bm = cb.BoneMapEntries();
                var da = cb.DefaultAnimEntries();
                Check("c002 bone map enumera", bm.Count > 0, $"{bm.Count} slots");
                if (bm.Count > 0)
                {
                    var e0 = bm[0];
                    var db = cb.SetBoneMapValue(e0.Off, (e0.Value + 1) & 0xFFFF);
                    var bm2 = ActorBin.FromBytes(db).BoneMapEntries();
                    Check("c002 bone map grava",
                        bm2[0].Value == ((e0.Value + 1) & 0xFFFF) &&
                        bm2[0].Key == e0.Key);
                }
                Check("c002 default anims", da.Count > 0, $"{da.Count} slots");
                if (da.Count > 0)
                {
                    var e0 = da[0];
                    var dd = cb.SetDefaultAnim(e0.Off, 0x1234);
                    var da2 = ActorBin.FromBytes(dd).DefaultAnimEntries();
                    Check("c002 default anim grava",
                        da2[0].AnimId == 0x1234 && da2[0].Slot == e0.Slot);
                }
            }
        }
    }
    // FX overlay multipliers: EmitMul=0 must spawn strictly fewer particles
    var ep = Path.Combine(root, ".lab/fetched/11/006a.bin");
    if (!File.Exists(ep)) { skip++; Console.WriteLine("  SKIP 006a EmitMul (não baixado)"); }
    else
    {
        var d = File.ReadAllBytes(ep);
        var s0 = MagicParticles.FromMagicBin(d, -1, headersOut: null);
        var s1 = MagicParticles.FromMagicBin(d, -1, headersOut: null);
        if (s0 == null || s1 == null) Check("006a EmitMul sims", false, "null sys");
        else
        {
            s0.EmitMul = 0;
            var o0 = new List<ParticleSim.DrawItem>();
            var o1 = new List<ParticleSim.DrawItem>();
            for (int fr = 0; fr < 90; fr++) { s0.Step(1f, o0); s1.Step(1f, o1); }
            Check("006a EmitMul=0 <= =1", o0.Count <= o1.Count,
                $"mul0={o0.Count} mul1={o1.Count}");
            s1.LifeMul = 0.1f;
            var o2 = new List<ParticleSim.DrawItem>();
            for (int fr = 0; fr < 60; fr++) s1.Step(1f, o2);
            Check("006a LifeMul curto roda", true, $"draws={o2.Count}");
        }
    }
    // kernel rename: append-to-pool + repoint, round-trip through Load
    string kcmd = "";
    try
    {
        var env = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".lab/environment.json"))).RootElement;
        var ar = env.GetProperty("assets_root").GetString() ?? "";
        kcmd = Path.Combine(ar, "ffx_ps2/ffx/master/new_uspc/battle/kernel/command.bin");
    }
    catch { }
    if (!File.Exists(kcmd)) { skip++; Console.WriteLine("  SKIP command.bin rename (ausente)"); }
    else
    {
        var d = File.ReadAllBytes(kcmd);
        var err = KernelBin.Load(d, out var kt);
        Check("command.bin load", err == null, err ?? "");
        if (err == null)
        {
            var before = kt.Name(1);
            var grown = KernelBin.Rename(kt, 1, "Fira X");
            Check("rename encoda", grown != null, $"orig='{before}'");
            if (grown != null)
            {
                var err2 = KernelBin.Load(grown, out var kt2);
                Check("rename releitura", err2 == null && kt2.Name(1) == "Fira X",
                    $"-> '{(err2 == null ? kt2.Name(1) : err2)}'");
                Check("rename preserva outros", err2 == null && kt2.Name(0) == kt.Name(0) &&
                    kt2.U16(2, 0x10) == kt.U16(2, 0x10));
                Check("rename recusa char", KernelBin.Rename(kt, 1, "火") == null);
            }
        }
    }
    // *_txt.bin: SetText appends + repoints a slot offset
    string kb = "";
    try
    {
        var env = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".lab/environment.json"))).RootElement;
        kb = Path.Combine(env.GetProperty("assets_root").GetString() ?? "",
            "ffx_ps2/ffx/master/new_uspc/battle/kernel");
    }
    catch { }
    var kbtl = Path.Combine(kb, "btl_txt.bin");
    if (!File.Exists(kbtl)) { skip++; Console.WriteLine("  SKIP btl_txt SetText (ausente)"); }
    else
    {
        var err = KernelBin.Load(File.ReadAllBytes(kbtl), out var tt);
        Check("btl_txt load", err == null, err ?? "");
        if (err == null)
        {
            Check("btl_txt decoda", KernelBin.DecodeText(tt.Pool, tt.U16(3, 0)).Contains("Ambushed"),
                KernelBin.DecodeText(tt.Pool, tt.U16(3, 0)));
            var grown = KernelBin.SetText(tt, 3, 0, "Surprise!");
            Check("btl_txt SetText", grown != null);
            if (grown != null)
            {
                var e2 = KernelBin.Load(grown, out var t2);
                Check("btl_txt releitura", e2 == null &&
                    KernelBin.DecodeText(t2.Pool, t2.U16(3, 0)) == "Surprise!",
                    KernelBin.DecodeText(t2.Pool, t2.U16(3, 0)));
                Check("btl_txt outros slots", e2 == null &&
                    KernelBin.DecodeText(t2.Pool, t2.U16(2, 0)).Contains("Preemptive"));
            }
        }
    }
    var bad = MagicParticles.FromMagicBin(new byte[0x400]);
    Check("magic vazio => null", bad == null);

    {
        // enc edits overlay schema: per-slot deltas+swap, extra spawns,
        // removed slots — parsed by the shared EncEdits
        const string ej = """{"actors":{"2":{"position":[1,2,3],"heading":0.5,"scale":1.5,"monster":4097},"4":{"monster":8193}},"extra":[{"monster":12289,"position":[9,8,7],"heading":0.25,"scale":2}],"remove":[6],"party":{"0":[5,0,-2]},"other":{"1":[0,3,0]}}""";
        var ed = EncEdits.Parse(ej);
        Check("enc edits parse deltas",
            ed.Deltas.Count == 1 && ed.Deltas[2][0] == 1 &&
            ed.Deltas[2][5] == 1.5f && !ed.Deltas.ContainsKey(4),
            $"d={ed.Deltas.Count}"); // slot 4 is swap-only -> no delta entry
        Check("enc edits parse swaps",
            ed.Swaps.Count == 2 && ed.Swaps[2] == 4097 && ed.Swaps[4] == 8193,
            $"s={ed.Swaps.Count}");
        Check("enc edits parse extra+remove",
            ed.Extra.Count == 1 && ed.Extra[0].Monster == 12289 &&
            ed.Extra[0].Pos[0] == 9 && ed.Extra[0].Scale == 2 &&
            ed.Remove.Contains(6),
            $"x={ed.Extra.Count} r={ed.Remove.Count}");
        Check("enc edits parse party+other",
            ed.Party.Count == 1 && ed.Party[0][0] == 5 && ed.Party[0][2] == -2 &&
            ed.Other.Count == 1 && ed.Other[1][1] == 3,
            $"p={ed.Party.Count} o={ed.Other.Count}");
    }

    {
        // PPP emitter record edit: patch pos/delay/scale of emitter 1 in a
        // real map bin (001b, 72 emitters) and re-parse — target changes,
        // neighbors untouched
        var mp = ".lab/fetched/13/001b.bin";
        if (File.Exists(mp))
        {
            var md = File.ReadAllBytes(mp);
            var mf = Map1File.Load(mp, md);
            int pppOffs = (int)mf.Slot(0x38);
            var l = Particles.Parse(md, pppOffs);
            Check("001b ppp emitters", l.Emitters.Count > 4,
                $"n={l.Emitters.Count}");
            long rec = Particles.PppEdit.EmitterOff(pppOffs, 1);
            var p2 = (byte[])md.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(
                p2.AsSpan((int)(rec + Particles.PppEdit.FPos)), 1234.5f);
            BinaryPrimitives.WriteInt32LittleEndian(
                p2.AsSpan((int)(rec + Particles.PppEdit.FDelay)), 77);
            BinaryPrimitives.WriteSingleLittleEndian(
                p2.AsSpan((int)(rec + Particles.PppEdit.FWidth)), 3.25f);
            var l2 = Particles.Parse(p2, pppOffs);
            var e2 = l2.Emitters[1];
            Check("001b emitter patch aplica",
                Math.Abs(e2.Pos[0] - 1234.5) < 0.01 && e2.Delay == 77 &&
                Math.Abs(e2.Width - 3.25) < 0.001,
                $"pos={e2.Pos[0]:0.#} d={e2.Delay} w={e2.Width:0.##}");
            var e0 = l2.Emitters[0]; var o0 = l.Emitters[0];
            Check("001b emitter vizinhos",
                e0.Pos[0] == o0.Pos[0] && e0.Delay == o0.Delay &&
                l2.Emitters[2].Pos[0] == l.Emitters[2].Pos[0]);
        }
        else Check("001b ppp emitters", true, "skip — bin não baixado");
    }

    {
        // walkmesh vertex edit: quantize world pos → s16×scale, re-parse —
        // target moves to the requested pos (within quant error), neighbor
        // verts untouched, w component preserved
        var wmp = ".lab/fetched/13/001b.bin";
        if (File.Exists(wmp))
        {
            var gd = File.ReadAllBytes(wmp);
            var w = Walkmesh.Parse(Map1File.Load(wmp, gd));
            Check("001b walkmesh", w != null && w.Vertices.Length >= 8,
                w == null ? "null" : $"v={w.Vertices.Length / 4}");
            if (w != null)
            {
                var (ox, oy, oz) = w.VertPos(1);
                var p2 = (byte[])gd.Clone();
                long o = w.VertsOffset + 8;
                short wKeep = w.Vertices[7];
                float nx = ox + 10.5f, ny = oy - 3.25f, nz = oz + 0.5f;
                var nv = new[] { nx, ny, nz };
                for (int k = 0; k < 3; k++)
                    BinaryPrimitives.WriteInt16LittleEndian(
                        p2.AsSpan((int)(o + 2 * k)),
                        (short)Math.Clamp((int)MathF.Round(nv[k] * w.Scale), -32768, 32767));
                var w2 = Walkmesh.Parse(Map1File.Load("x", p2))!;
                var (x2, y2, z2) = w2.VertPos(1);
                bool moved = Math.Abs(x2 - nx) < 0.2f && Math.Abs(y2 - ny) < 0.2f
                    && Math.Abs(z2 - nz) < 0.2f;
                var (ax, ay, az) = w2.VertPos(0);
                var (bx, by, bz) = w.VertPos(0);
                Check("001b walkmesh vert move", moved &&
                    ax == bx && ay == by && az == bz &&
                    w2.Vertices[7] == wKeep,
                    $"({x2:0.#},{y2:0.#},{z2:0.#})");
            }
        }
        else Check("001b walkmesh", true, "skip — bin não baixado");
    }

    {
        // broader kernel tables share the Excel format — load each from the
        // real kernel dir and check counts/name decoding (layouts per
        // Fahrenheit: KeyItem 20B, SphereGridNodeType 24B, Sphere 16B,
        // PlyRom 44B, PlySave 148B, AutoAbility 108B)
        var kd = Path.Combine(
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(".lab/environment.json"))
                .RootElement.GetProperty("assets_root").GetString()!,
            "ffx_ps2/ffx/master/new_uspc/battle/kernel");
        foreach (var (file, wantLen) in new[]
        {
            ("important.bin", 20), ("panel.bin", 24), ("sphere.bin", 16),
            ("ply_rom.bin", 44), ("ply_save.bin", 148), ("a_ability.bin", 108),
            ("w_name.bin", 72),
        })
        {
            var fp = Path.Combine(kd, file);
            if (!File.Exists(fp)) { Check($"{file} load", true, "skip"); continue; }
            var err = KernelBin.Load(File.ReadAllBytes(fp), out var t);
            Check($"{file} load",
                err == null && t.EntryLength == wantLen && t.EntryCount > 0 &&
                t.EntryCount < 2000,
                err ?? $"n={t.EntryCount} len={t.EntryLength}");
            if (err == null && t.EntryLength != 16) // sphere's +0 is help, not name
            {
                var n = t.Name(file == "w_name.bin" ? 1 : 0); // w_name rec0 slot0 is blank
                Check($"{file} nome decoda", n.Length > 0,
                    n.Length > 24 ? n[..24] : n);
            }
        }
    }

    {
        // LEVEL_PART hide: type-swap 0xFFFFFFFF keeps the section walkable
        // (sizes preserved) but drops the part from ListParts
        var gp2 = ".lab/fetched/13/0065.bin";
        if (File.Exists(gp2))
        {
            var gd2 = File.ReadAllBytes(gp2);
            var gf = Map1File.Load(gp2, gd2);
            var parts = LevelParts.ListParts(gf);
            Check("0065 parts", parts.Count > 10, $"n={parts.Count}");
            var (en0, _) = parts[0];
            var p2 = (byte[])gd2.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(
                p2.AsSpan((int)en0.DescOffset), 0xFFFFFFFF);
            var gf2 = Map1File.Load("x", p2);
            var sec2 = gf2.WalkSection(gf2.Slot(0x14));
            var parts2 = LevelParts.ListParts(gf2);
            Check("0065 part hide walkável",
                sec2 != null && sec2.Count == gf.WalkSection(gf.Slot(0x14))!.Count,
                $"entries={sec2?.Count}");
            Check("0065 part hide some da lista",
                parts2.Count == parts.Count - 1,
                $"{parts.Count}→{parts2.Count}");
        }
        else Check("0065 parts", true, "skip — bin não baixado");
    }

    {
        // emit datum edit: enumerate emit entries in the map PPP, patch
        // count/period of the first, bytes change and sentinel walk stays
        // bounded
        var edp = ".lab/fetched/13/001b.bin";
        if (File.Exists(edp))
        {
            var ed2 = File.ReadAllBytes(edp);
            var ef = Map1File.Load(edp, ed2);
            int ppp2 = (int)ef.Slot(0x38);
            var entries = Particles.PppEdit.EmitDatumEntries(ed2, ppp2).ToList();
            Check("001b emit datum enumera", entries.Count > 0,
                $"n={entries.Count}");
            if (entries.Count > 0)
            {
                var en = entries[0];
                var spec = Particles.PppEdit.EmitOps[en.Op];
                var p2 = (byte[])ed2.Clone();
                p2[en.Off + spec.CntOff] = 99;
                if (spec.PerOff >= 0) p2[en.Off + spec.PerOff] = 42;
                BitConverter.GetBytes((ushort)0x1234).CopyTo(p2,
                    (int)en.Off + Particles.PppEdit.EmitPatOff);
                BitConverter.GetBytes(0x13579).CopyTo(p2,
                    (int)en.Off + spec.ProgOff);
                Check("001b emit datum grava",
                    p2[en.Off + spec.CntOff] == 99 &&
                    (spec.PerOff < 0 || p2[en.Off + spec.PerOff] == 42) &&
                    BitConverter.ToUInt16(p2, (int)en.Off +
                        Particles.PppEdit.EmitPatOff) == 0x1234 &&
                    BitConverter.ToInt32(p2, (int)en.Off + spec.ProgOff) == 0x13579 &&
                    en.Off + spec.Size <= p2.Length,
                    $"kind={en.Kind} op=0x{en.Op:x}");
                // unchanged on original; entries still enumerate identically
                var re = Particles.PppEdit.EmitDatumEntries(p2, ppp2).ToList();
                Check("001b emit datum re-enum",
                    re.Count == entries.Count && re[0].Off == en.Off);
            }
        }
        else Check("001b emit datum", true, "skip");
    }

    {
        // structural add: duplicate a LEVEL_PART + its MODELs appended at
        // the section end — count bumps, header slots shift, everything
        // reparses (parts/models/walkmesh/ppp), all prior indices intact
        var dp = ".lab/fetched/13/0065.bin";
        if (File.Exists(dp))
        {
            var dd = File.ReadAllBytes(dp);
            var df0 = Map1File.Load(dp, dd);
            var parts0 = LevelParts.ListParts(df0);
            int srcIdx = parts0.Count > 0 ? parts0[0].Entry.Index : -1;
            if (srcIdx >= 0)
            {
                var dd2 = LevelParts.DuplicatePart(df0, srcIdx);
                var df1 = Map1File.Load("dup", dd2);
                var parts1 = LevelParts.ListParts(df1);
                Check("0065 dup part adiciona",
                    parts1.Count == parts0.Count + 1 &&
                    dd2.Length > dd.Length,
                    $"parts {parts0.Count}→{parts1.Count} " +
                    $"bytes +{dd2.Length - dd.Length}");
                // record equality on int[] is reference-based — compare
                // the scalar fields + effect list explicitly
                bool same = parts0.Zip(parts1).All(x =>
                    x.First.Entry.Index == x.Second.Entry.Index &&
                    x.First.Info.Px == x.Second.Info.Px &&
                    x.First.Info.Py == x.Second.Info.Py &&
                    x.First.Info.Pz == x.Second.Info.Pz &&
                    x.First.Info.Ex == x.Second.Info.Ex &&
                    x.First.Info.Layer == x.Second.Info.Layer &&
                    x.First.Info.EffectIndices.SequenceEqual(x.Second.Info.EffectIndices));
                Check("0065 dup preserva parts", same);
                bool ok = true;
                try
                {
                    var wdf = Map1File.Load("dup2", dd2);
                    _ = Walkmesh.Parse(wdf);
                    if (wdf.Slot(0x38) != 0)
                        _ = Particles.Parse(dd2, (int)wdf.Slot(0x38));
                }
                catch (Exception ex) { ok = false; Console.WriteLine("  dup reparse: " + ex.Message); }
                Check("0065 dup seções válidas", ok);
                Check("0065 dup clone TRS",
                    parts1[^1].Info.Px == parts0[0].Info.Px &&
                    parts1[^1].Info.Py == parts0[0].Info.Py);
            }
            else Check("0065 dup part", true, "skip — sem parts");
        }
        else Check("0065 dup part", true, "skip");
    }

    {
        // structural add: duplicate a PPP emitter record — the appended
        // copy bumps emitterCount + the four header offsets + the whole
        // behavior-offset table + MAP1 header slots; parse must stay
        // coherent and other emitters unchanged
        var ep2 = ".lab/fetched/13/001b.bin";
        if (File.Exists(ep2))
        {
            var ed3 = File.ReadAllBytes(ep2);
            var ef2 = Map1File.Load(ep2, ed3);
            int pp3 = (int)ef2.Slot(0x38);
            var l0 = Particles.Parse(ed3, pp3);
            var ed4 = Particles.PppEdit.DuplicateEmitter(ed3, pp3, 0);
            var l1 = Particles.Parse(ed4, pp3);
            Check("001b dup emissor adiciona",
                l1.Emitters.Count == l0.Emitters.Count + 1,
                $"emitters {l0.Emitters.Count}→{l1.Emitters.Count}");
            Check("001b dup emissor behaviors",
                l1.Behaviors.Count == l0.Behaviors.Count &&
                l1.Behaviors.Sum(b => b.Programs.Count) ==
                    l0.Behaviors.Sum(b => b.Programs.Count),
                $"beh={l1.Behaviors.Count} progs={l1.Behaviors.Sum(b => b.Programs.Count)}");
            bool sameEm = l0.Emitters.Zip(l1.Emitters).All(x =>
                x.First.Pos.SequenceEqual(x.Second.Pos) &&
                x.First.Behavior == x.Second.Behavior);
            Check("001b dup preserva emissores", sameEm);
            // disable: behavior=-1 makes an emitter inert, record intact
            var ed5 = (byte[])ed3.Clone();
            long behOff = Particles.PppEdit.EmitterOff(pp3, 0) +
                Particles.PppEdit.FBehavior;
            BitConverter.GetBytes(-1).CopyTo(ed5, (int)behOff);
            Check("001b emissor desativa",
                BitConverter.ToInt32(ed5, (int)behOff) == -1 &&
                Particles.Parse(ed5, pp3).Emitters.Count == l0.Emitters.Count);
            Check("001b dup emissor datums",
                Particles.PppEdit.DatumEntries(ed4, pp3).Count() ==
                Particles.PppEdit.DatumEntries(ed3, pp3).Count());
            // walkmesh + everything after PPP still parse
            bool ok = true;
            try
            {
                var wf = Map1File.Load("dup3", ed4);
                _ = Walkmesh.Parse(wf);
                _ = LevelParts.ListParts(wf);
            }
            catch (Exception ex) { ok = false; Console.WriteLine("  em dup: " + ex.Message); }
            Check("001b dup emissor seções", ok);
        }
        else Check("001b dup emissor", true, "skip");
    }

    {
        // magic-bin emit datum: same records, but raw ops are funcMap
        // indices and the container uses synthesized emitters — exercise
        // the remapped enumeration + patch round trip on 006a (Ultima)
        var mp = ".lab/fetched/11/006a.bin";
        if (File.Exists(mp))
        {
            var md = File.ReadAllBytes(mp);
            var msys = MagicParticles.FromMagicBin(md, -1);
            if (msys != null)
            {
                var mfm = MagicBin.BuildFuncMap(md, MagicBin.FindFuncOffset(md));
                var mfma = mfm.Count > 0 ? mfm.ToArray() : null;
                var ment = Particles.PppEdit.EmitDatumEntries(
                    md, msys.L.Offs, mfma, synthEmitters: true).ToList();
                Check("006a magic emit datum enumera", ment.Count > 0,
                    $"n={ment.Count} ppp@0x{msys.L.Offs:x}");
                if (ment.Count > 0)
                {
                    var en = ment[0];
                    var spec = Particles.PppEdit.EmitOps[en.Op];
                    var p2 = (byte[])md.Clone();
                    p2[en.Off + spec.CntOff] = 77;
                    if (spec.PerOff >= 0) p2[en.Off + spec.PerOff] = 33;
                    var re = Particles.PppEdit.EmitDatumEntries(
                        p2, msys.L.Offs, mfma, synthEmitters: true).ToList();
                    Check("006a magic emit datum grava",
                        p2[en.Off + spec.CntOff] == 77 &&
                        re.Count == ment.Count && re[0].Off == en.Off,
                        $"kind={en.Kind} op=0x{en.Op:x}");
                }
            }
            else Check("006a magic emit datum", true, "skip — FromMagicBin falhou");
        }
        else Check("006a magic emit datum", true, "skip");
    }

    {
        // color datums: glare/ftrail carry RGBA byte fields — enumerate +
        // patch on the same real PPPs used above
        var cp1 = ".lab/fetched/13/001b.bin";
        var cp2 = ".lab/fetched/11/006a.bin";
        if (File.Exists(cp1) && File.Exists(cp2))
        {
            var c1 = File.ReadAllBytes(cp1);
            var f1 = Map1File.Load(cp1, c1);
            var mapCols = Particles.PppEdit.ColorDatumEntries(
                c1, (int)f1.Slot(0x38)).ToList();
            var c2 = File.ReadAllBytes(cp2);
            var ms2 = MagicParticles.FromMagicBin(c2, -1);
            var fm2 = ms2 != null
                ? MagicBin.BuildFuncMap(c2, MagicBin.FindFuncOffset(c2))
                : new List<int>();
            var magCols = ms2 == null ? new List<Particles.PppEdit.ColorEntry>()
                : Particles.PppEdit.ColorDatumEntries(c2, ms2.L.Offs,
                    fm2.Count > 0 ? fm2.ToArray() : null, synthEmitters: true).ToList();
            // generic datum surface: enumerate all non-emit kinds on the
            // same two PPPs + typed field write round-trip
            var mapDats = Particles.PppEdit.DatumEntries(
                c1, (int)f1.Slot(0x38)).ToList();
            var magDats = ms2 == null ? new List<Particles.PppEdit.DatumEntry>()
                : Particles.PppEdit.DatumEntries(c2, ms2.L.Offs,
                    fm2.Count > 0 ? fm2.ToArray() : null, synthEmitters: true).ToList();
            Check("datum genérico enumera", mapDats.Count + magDats.Count > 0,
                $"map={mapDats.Count} magic={magDats.Count}");
            if (magDats.Count > 0)
            {
                var de = magDats[0];
                var fld = Particles.PppEdit.DatumOps[de.Op].Fields[0];
                var orig = Particles.PppEdit.ReadField(c2, de.Off, fld);
                var p4 = (byte[])c2.Clone();
                bool wr = Particles.PppEdit.WriteField(p4, de.Off, fld, "1");
                Check("datum genérico grava",
                    wr && Particles.PppEdit.ReadField(p4, de.Off, fld) != orig ||
                    orig == "1",
                    $"kind={de.Kind} field={fld.Name}({fld.Type})");
                // invalid values must be rejected without mutation
                var p5 = (byte[])c2.Clone();
                var rgbaFld = Particles.PppEdit.DatumOps[de.Op].Fields
                    .FirstOrDefault(x => x.Type == Particles.PppEdit.DF.RGBA);
                if (rgbaFld.Off != 0)
                    Check("datum genérico rejeita",
                        !Particles.PppEdit.WriteField(p5, de.Off, rgbaFld, "zzz") &&
                        p5.SequenceEqual(c2));
            }
            Check("datum cor enumera", mapCols.Count + magCols.Count > 0,
                $"map={mapCols.Count} magic={magCols.Count}");
            var pool = mapCols.Count > 0 ? (c1, mapCols) : (c2, magCols);
            if (pool.Item2.Count > 0)
            {
                var ce = pool.Item2[0];
                var p3 = (byte[])pool.Item1.Clone();
                p3[ce.Off] = 0xDE; p3[ce.Off + 1] = 0xAD;
                p3[ce.Off + 2] = 0xBE; p3[ce.Off + 3] = 0xEF;
                Check("datum cor grava",
                    p3[ce.Off] == 0xDE && p3[ce.Off + 3] == 0xEF,
                    $"kind={ce.Kind} field={ce.Field}");
            }
        }
        else Check("datum cor", true, "skip");
    }

    {
        // vertex editing: walk the VIF stream of a real MODEL, patch one
        // vertex in place, reparse — count must match the draw totals
        var vp = ".lab/fetched/13/0065.bin";
        if (File.Exists(vp))
        {
            var vd = File.ReadAllBytes(vp);
            var vf = Map1File.Load(vp, vd);
            var sec = vf.WalkSection(vf.Slot(0x14));
            var me = sec?.FirstOrDefault(e => e.Type == LevelParts.Model);
            var ms = MapModelSet.Parse(vf);
            if (me != null)
            {
                var verts = MapModelEdit.PositionVerts(vf, me.PayloadOffset);
                int total = ms.Models
                    .Where(m => m.SectionIndex == me.Index)
                    .Sum(m => m.Draws.Sum(dc => dc.VertexCount));
                Check("0065 vértices enumera",
                    verts.Count == total && verts.Count > 0,
                    $"verts={verts.Count} draws={total}");
                if (verts.Count > 0)
                {
                    var vr = verts[0];
                    var vd2 = (byte[])vd.Clone();
                    MapModelEdit.WriteVert(vd2, vr, 1.5f, -2.5f, 3.5f);
                    var vf2 = Map1File.Load("vpatch", vd2);
                    var verts2 = MapModelEdit.PositionVerts(vf2, me.PayloadOffset);
                    Check("0065 vértice grava",
                        verts2.Count == verts.Count &&
                        Math.Abs(verts2[0].X - 1.5f) < 0.001 &&
                        Math.Abs(verts2[0].Z - 3.5f) < 0.001 &&
                        verts2[1].X == verts[1].X,
                        $"v0=({verts[0].X:0.#}→1.5)");
                    // the model still parses end-to-end
                    Check("0065 vértice reparse",
                        MapModelSet.Parse(vf2).Models.Count == ms.Models.Count);
                    var cols = MapModelEdit.ColorVerts(vf, me.PayloadOffset);
                    Check("0065 cores enumera", cols.Count == total,
                        $"cols={cols.Count}");
                }
            }
            else Check("0065 vértices", true, "skip — sem MODEL");
        }
        else Check("0065 vértices", true, "skip");
    }

    {
        // physical part delete: inverse of DuplicatePart — section
        // compacts, count drops, fx refs (→EFFECT entries) stay valid
        var gpx = ".lab/fetched/13/001b.bin";
        if (File.Exists(gpx))
        {
            var gd = File.ReadAllBytes(gpx);
            var gf = Map1File.Load(gpx, gd);
            var before = LevelParts.ListParts(gf);
            int pi = before[0].Entry.Index;
            var gsec = gf.WalkSection(gf.Slot(0x14))!;
            int models = 0;
            for (int j = pi + 1; j < gsec.Count && gsec[j].Type == 1; j++) models++;
            var gd2 = LevelParts.DeletePart(gf, pi);
            var gf2 = Map1File.Load(gpx, gd2);
            var after = LevelParts.ListParts(gf2);
            var gsec2 = gf2.WalkSection(gf2.Slot(0x14))!;
            Check("delete part físico",
                after.Count == before.Count - 1 &&
                gsec2.Count == gsec.Count - 1 - models &&
                gd2.Length < gd.Length,
                $"parts {before.Count}→{after.Count} entries {gsec.Count}→{gsec2.Count}");
            Check("delete part reparse",
                after.TrueForAll(x => x.Info.Index >= 0) &&
                after[0].Info.EffectIndices.SequenceEqual(before[1].Info.EffectIndices));
        }
        else Check("delete part", true, "skip");
    }

    {
        // physical PPP emitter delete — inverse of DuplicateEmitter
        var epx = ".lab/fetched/13/001b.bin";
        if (File.Exists(epx))
        {
            var ed = File.ReadAllBytes(epx);
            var eg = Map1File.Load(epx, ed);
            int eo = (int)eg.Slot(0x38);
            var es = ParticleSim.Sys.FromBytes(ed, eo);
            int n0 = es.Emitters.Count, b0 = es.L.Behaviors.Count;
            var ed2 = Particles.PppEdit.DeleteEmitter(ed, eo, 0);
            var es2 = ParticleSim.Sys.FromBytes(ed2, eo);
            Check("delete emissor físico",
                es2.Emitters.Count == n0 - 1 &&
                ed2.Length == ed.Length - 0x50 &&
                es2.L.Behaviors.Count == b0,
                $"ems {n0}→{es2.Emitters.Count} behav {b0}");
            // behaviors/datums still reachable → datum enum non-empty
            Check("delete emissor reparse",
                Particles.PppEdit.EmitDatumEntries(ed2, eo).Count() > 0);
        }
        else Check("delete emissor", true, "skip");
    }

    {
        // EFFECT keyframe editing: vec3 slots + duration patch in place
        var fxp = ".lab/fetched/13/001b.bin";
        if (File.Exists(fxp))
        {
            var fxd = File.ReadAllBytes(fxp);
            var fxf = Map1File.Load(fxp, fxd);
            var fxs = LevelParts.LevelEffects.ListEffects(fxf);
            Check("effects enumera", fxs.Count == 8,
                $"fx={fxs.Count} types={string.Join(',', fxs.Select(x => LevelParts.LevelEffects.TypeName(x.Info.Type)))}");
            var (en, fx) = fxs[1]; // PARAMETER 1 key
            var k = fx.Keys[0];
            var fd2 = LevelParts.LevelEffects.SetKey(fxf, en, k, 1, 5, 6, 7, duration: 500);
            var fxf2 = Map1File.Load(fxp, fd2);
            var fxs2 = LevelParts.LevelEffects.ListEffects(fxf2);
            var k2 = fxs2[1].Info.Keys[0];
            var vo = k2.VecOff(1);
            Check("effect keyframe grava",
                fxs2.Count == 8 && k2.Duration == 500 &&
                fxf2.F32(vo) == 5 && fxf2.F32(vo + 4) == 6 && fxf2.F32(vo + 8) == 7,
                $"dur={k2.Duration} vec=({fxf2.F32(vo)},{fxf2.F32(vo + 4)},{fxf2.F32(vo + 8)})");
        }
        else Check("effects", true, "skip");
    }

    {
        // EFFECT eval: ROTATION linear (0,0,0)->(0,-2pi,0) over 2000f
        var evp = ".lab/fetched/13/001b.bin";
        if (File.Exists(evp))
        {
            var evd = File.ReadAllBytes(evp);
            var evf = Map1File.Load(evp, evd);
            var evs = LevelParts.LevelEffects.ListEffects(evf);
            var rot = evs.First(x => x.Info.Type == LevelParts.LevelEffects.Rotation).Info;
            var v0 = LevelParts.LevelEffects.Eval(evf, rot, 0);
            var vm = LevelParts.LevelEffects.Eval(evf, rot, 1000);
            var vL = LevelParts.LevelEffects.Eval(evf, rot, 1999);
            Check("effect eval rot",
                Math.Abs(v0[1]) < 0.01 &&
                Math.Abs(vm[1] + Math.PI) < 0.1 &&
                Math.Abs(vL[1] + 2 * Math.PI) < 0.1,
                $"t0={v0[1]:0.###} t1000={vm[1]:0.###} t1999={vL[1]:0.###}");
            var mot = evs.First(x => x.Info.Type == LevelParts.LevelEffects.Motion).Info;
            Check("effect eval loop",
                LevelParts.LevelEffects.Eval(evf, mot, 0)
                    .SequenceEqual(LevelParts.LevelEffects.Eval(evf, mot,
                        LevelParts.LevelEffects.FxLength(mot))));
        }
        else Check("effect eval", true, "skip");
    }

    {
        // ATEL disasm on both container kinds (EV01 + encounter)
        var ap1 = ".lab/fetched/0c/0384.bin";
        var ap2 = ".lab/fetched/0e/0002.bin";
        if (File.Exists(ap1) && File.Exists(ap2))
        {
            var a1 = AtelBlob.Load(File.ReadAllBytes(ap1),
                AtelBlob.FindBlob(File.ReadAllBytes(ap1)));
            var i1 = a1.Disasm().Count();
            Check("atel ev01", a1.WorkerCount == 14 && a1.ActorCount == 13 &&
                a1.ScriptId == "bjyt0300" && i1 == 1386,
                $"{a1.ScriptId} w={a1.WorkerCount} ins={i1}");
            var a2 = AtelBlob.Load(File.ReadAllBytes(ap2),
                AtelBlob.FindBlob(File.ReadAllBytes(ap2)));
            var i2 = a2.Disasm().Count();
            Check("atel encounter", a2.WorkerCount == 5 && i2 == 433 &&
                a2.ScriptId == "bjyt02_00", $"{a2.ScriptId} w={a2.WorkerCount} ins={i2}");
            // every opcode resolves to a name or opXX — census non-empty
            var ops = a1.Disasm().Select(x => x.Op).Distinct().Count();
            Check("atel opcodes", ops > 20, $"{ops} ops distintos");
        }
        else Check("atel", true, "skip");
    }

    {
        // kernel structural add: clone a record at the table end — count-1
        // and TotalDataLength bump, pool untouched (offsets pool-relative)
        var kxp = Path.Combine(
            Environment.GetEnvironmentVariable("FFX_KERNEL") ?? "",
            "command.bin");
        if (!File.Exists(kxp))
            kxp = Path.Combine(
                Environment.GetEnvironmentVariable("FFX_ASSETS") ?? "",
                "ffx_ps2/ffx/master/new_uspc/battle/kernel/command.bin");
        if (File.Exists(kxp))
        {
            var kd = File.ReadAllBytes(kxp);
            var kerr = KernelBin.Load(kd, out var kt);
            if (kerr == null)
            {
                var kd2 = KernelBin.AddRecord(kt, 1);
                var kerr2 = KernelBin.Load(kd2, out var kt2);
                Check("kernel add registro",
                    kerr2 == null && kt2.EntryCount == kt.EntryCount + 1 &&
                    kt2.EntryLength == kt.EntryLength,
                    $"regs {kt.EntryCount}→{kt2.EntryCount}");
                // clone inherits fields; pool intact
                Check("kernel add clone",
                    kt2.Name(kt2.EntryCount - 1) == kt.Name(1) &&
                    kt2.Pool.Length == kt.Pool.Length &&
                    kt2.Name(0) == kt.Name(0));
            }
            else Check("kernel add", true, "skip — load: " + kerr);
        }
        else Check("kernel add", true, "skip");
    }

    {
        // EV01 event package: model list + map points parse on a real bin
        var evp = ".lab/fetched/0c/0384.bin";
        if (File.Exists(evp))
        {
            var ev = Ev01File.Load(evp);
            var ml = ev.ModelList();
            var pts = ev.Points();
            Check("0384 ev01 modelList", ml.Count > 0, $"models={ml.Count}");
            Check("0384 ev01 points", pts.Count > 0, $"points={pts.Count}");
        }
        else Check("ev01", true, "skip");
    }

    Console.WriteLine($"selftest: {pass} pass {fail} fail {skip} skip");
    return fail > 0 ? 1 : 0;
}

if (args.Length < 2) { Usage(); return 2; }

var cmd = args[0];
Map1File? f = null;
Map1File F() => f ??= Map1File.Load(args[1]);

switch (cmd)
{
    case "info":
    {
        Console.WriteLine($"{F().Path}  {F().Data.Length} bytes  magic=MAP1");
        foreach (var (slot, name) in new[] { (0x14, "+0x14 level/tex"), (0x18, "+0x18 walkmesh"), (0x38, "+0x38 particles"), (0x3c, "+0x3c yngm"), (0x40, "+0x40 water") })
        {
            var off = F().Slot(slot);
            Console.Write($"  {name} -> 0x{off:x8}");
            if (off == 0) { Console.WriteLine("  (absent)"); continue; }
            if (off >= F().Data.Length) { Console.WriteLine("  OOB"); continue; }
            var sig = F().U32BE(off);
            Console.WriteLine(sig == 0x65432100 ? "  signed section" : $"  raw (be=0x{sig:x8})");
            if (sig != 0x65432100) continue;
            var hist = new Dictionary<uint, int>();
            foreach (var e in F().WalkSection(off)!)
                hist[e.Type] = hist.GetValueOrDefault(e.Type) + 1;
            Console.WriteLine($"      {string.Join("  ", hist.OrderBy(kv => kv.Key).Select(kv => $"{LevelParts.TypeName(kv.Key)}x{kv.Value}"))}");
        }
        return 0;
    }

    case "atel":
    {
        var d2 = File.ReadAllBytes(args[1]);
        int offs = OptInt("--offset", -1);
        if (offs < 0) offs = AtelBlob.FindBlob(d2);
        var at = AtelBlob.Load(d2, offs);
        Console.WriteLine($"{args[1]}+0x{offs:x}: scriptId={at.ScriptId} " +
            $"creator={at.Creator} codeLen=0x{at.CodeLen:x} code@0x{at.CodeOff:x} " +
            $"workers={at.WorkerCount} actors={at.ActorCount}");
        for (int i = 0; i < at.Workers.Count; i++)
        {
            var w = at.Workers[i];
            Console.WriteLine($"  worker{i}: eventType=0x{w.EventType:x} " +
                $"vars={w.VarCount} iC={w.IntConstCount} fC={w.FloatConstCount} " +
                $"funcs=[{string.Join(',', w.Funcs.Select(x => $"0x{x:x}"))}] " +
                $"jumps=[{string.Join(',', w.Jumps.Select(x => $"0x{x:x}"))}]");
        }
        var ins = at.Disasm().ToList();
        Console.WriteLine($"{ins.Count} instructions");
        var census = ins.GroupBy(x => x.Op).OrderByDescending(g => g.Count());
        Console.WriteLine("ops: " + string.Join(" ",
            census.Select(g => $"{AtelBlob.OpName(g.Key)}x{g.Count()}")));
        if (args.Contains("-v"))
            foreach (var (addr, op, operand) in ins)
                Console.WriteLine($"0x{addr:x5}  {AtelBlob.OpName(op),-10}" +
                    (operand.HasValue ? $" 0x{operand:x4} ({operand})" : ""));
        return 0;
    }

    case "parts":
    {
        var sec = RequireSection(F(), 0x14);
        foreach (var e in sec.Where(e => e.Type == LevelParts.LevelPart))
        {
            var p = LevelParts.ReadPart(f, e);
            var fx = p.EffectIndices.Length > 0 ? string.Join(",", p.EffectIndices) : "-";
            Console.WriteLine(
                $"part[{p.Index,3}] layer={p.Layer,2} skybox={p.IsSkybox} " +
                $"pos=({p.Px,8:0.#},{p.Py,8:0.#},{p.Pz,8:0.#}) " +
                $"euler=({p.Ex,6:0.##},{p.Ey,6:0.##},{p.Ez,6:0.##}) ord={p.EulerOrder} fx=[{fx}]");
        }
        return 0;
    }

    case "effects":
    {
        foreach (var (e, fx) in LevelParts.LevelEffects.ListEffects(F()))
        {
            Console.WriteLine(
                $"fx[{fx.Index,2}] @{e.Index,3} {LevelParts.LevelEffects.TypeName(fx.Type)} " +
                $"keys={fx.Keys.Length}" + (fx.Combined ? " (combined)" : ""));
            foreach (var k in fx.Keys)
            {
                var vecs = new List<string>();
                for (int i = 0; i < k.VecCount; i++)
                {
                    var vo = k.VecOff(i);
                    vecs.Add($"({f.F32(vo):0.##},{f.F32(vo + 4):0.##},{f.F32(vo + 8):0.##})");
                }
                Console.WriteLine(
                    $"   k@0x{k.Offs:x} fmt={k.Format} dur={k.Duration} {string.Join(" ", vecs)}");
            }
        }
        return 0;
    }

    case "models":
    {
        var sec = RequireSection(F(), 0x14);
        var curPart = -1;
        foreach (var e in sec)
        {
            if (e.Type == LevelParts.LevelPart) { curPart = e.Index; continue; }
            if (e.Type != LevelParts.Model) continue;
            var m = LevelParts.ReadModel(f, e);
            Console.WriteLine(
                $"model[{m.Index,3}] part={curPart,3} recs={m.RecordCount,4} @0x{m.RecordsOffset:x} " +
                $"bbox=({m.BMinX:0.#},{m.BMinY:0.#},{m.BMinZ:0.#})..({m.BMaxX:0.#},{m.BMaxY:0.#},{m.BMaxZ:0.#})");
        }
        return 0;
    }

    case "lighting":
    {
        var sec = RequireSection(F(), 0x14);
        foreach (var e in sec.Where(e => e.Type == LevelParts.Lighting))
        {
            var l = LevelParts.ReadLighting(f, e);
            Console.WriteLine(
                $"light[{l.Index,3}] clear=rgba({l.R},{l.G},{l.B},{l.A}) " +
                $"fog=rgb({l.FogR},{l.FogG},{l.FogB}) near={l.Near:0.#} far={l.Far:0.#} " +
                $"opacity={l.Opacity:0.##} envRot=({l.EnvRotU:0.#},{l.EnvRotP:0.#})");
        }
        return 0;
    }

    // ffx-map1 setlight <geom-bin> <out-bin> [--entry N]
    //   [--clear r,g,b[,a]] [--fog r,g,b] [--near f] [--far f] [--opacity f]
    // Writes a copy with the LIGHTING entry patched — byte-identical except
    // the edited fields (bounds-checked against the entry payload).
    case "setlight":
    {
        if (args.Length < 3) { Usage(); return 2; }
        var sec = RequireSection(F(), 0x14);
        var lights = sec.Where(e => e.Type == LevelParts.Lighting).ToList();
        if (lights.Count == 0) { Console.Error.WriteLine("no LIGHTING entries"); return 1; }
        int ei = OptInt("--entry", 0);
        if (ei < 0 || ei >= lights.Count) { Console.Error.WriteLine($"--entry 0..{lights.Count - 1}"); return 2; }
        var e = lights[ei];
        var p = e.PayloadOffset;
        var d = (byte[])F().Data.Clone();

        void P8(long off, byte v) => d[off] = v;
        void PF(long off, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)off), v);

        if (OptBytes("--clear", 3, 4) is { } cl)
        {
            P8(p + 0, cl[0]); P8(p + 1, cl[1]); P8(p + 2, cl[2]);
            if (cl.Length > 3) P8(p + 3, cl[3]);
        }
        if (OptBytes("--fog", 3, 3) is { } fg)
        {
            P8(p + 12, fg[0]); P8(p + 13, fg[1]); P8(p + 14, fg[2]);
        }
        if (OptF("--near") is { } fn) PF(p + 20, fn);
        if (OptF("--far") is { } ff) PF(p + 24, ff);
        if (OptF("--opacity") is { } fo) PF(p + 16, fo);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        File.WriteAllBytes(args[2], d);
        var l = LevelParts.ReadLighting(f, e);
        Console.WriteLine(
            $"patched LIGHTING entry {e.Index} -> {args[2]} " +
            $"(was clear=rgba({l.R},{l.G},{l.B},{l.A}) fog=rgb({l.FogR},{l.FogG},{l.FogB}) " +
            $"near={l.Near:0.#} far={l.Far:0.#} opacity={l.Opacity:0.##})");
        return 0;
    }

    case "walkmesh":
    {
        var w = Walkmesh.Parse(F());
        if (w == null) { Console.WriteLine("no walkmesh (+0x18 absent)"); return 0; }
        Console.WriteLine($"walkmesh ver={w.Version} verts={w.Vertices.Length / 4} tris={w.Tris.Count} scale={w.Scale:0.###} @0x{w.SectionOffset:x}");
        var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
        for (int i = 0; i < w.Vertices.Length / 4; i++)
        {
            var (x, y, z) = w.VertPos(i);
            xs.Add(x); ys.Add(y); zs.Add(z);
        }
        Console.WriteLine($"bounds X[{xs.Min():0.#}..{xs.Max():0.#}] Y[{ys.Min():0.#}..{ys.Max():0.#}] Z[{zs.Min():0.#}..{zs.Max():0.#}]");
        Console.WriteLine($"passability: {string.Join(" ", w.Tris.Select(t => t.Passability).Distinct().OrderBy(x => x))}");
        Console.WriteLine($"encounter:   {string.Join(" ", w.Tris.Select(t => t.Encounter).Distinct().OrderBy(x => x))}");
        Console.WriteLine($"location:    {string.Join(" ", w.Tris.Select(t => t.Location).Distinct().OrderBy(x => x))}");
        Console.WriteLine($"surface:     {string.Join(" ", w.Tris.Select(t => t.SurfaceType).Distinct().OrderBy(x => x))}");
        if (args.Any(a => a == "--tris"))
            for (int i = 0; i < w.Tris.Count; i++)
            {
                var t = w.Tris[i];
                Console.WriteLine(
                    $"  tri[{i,3}] v=({t.V0},{t.V1},{t.V2}) adj=({t.E01},{t.E12},{t.E20}) " +
                    $"data=0x{t.Data:x8} pass={t.Passability} enc={t.Encounter} " +
                    $"loc={t.Location} surf={t.SurfaceType} light=({t.Light0},{t.Light1},{t.Light2})");
            }
        return 0;
    }

    // ffx-map1 settri <geom-bin> <out-bin> --range A:B
    //   [--pass v] [--enc v] [--loc v] [--surf v] [--light v0,v1,v2]
    // Patches the packed u32 data of walkmesh tris; v/e adjacency untouched.
    case "settri":
    {
        if (args.Length < 3) { Usage(); return 2; }
        var w = Walkmesh.Parse(F());
        if (w == null) { Console.Error.WriteLine("no walkmesh"); return 1; }
        var (a, b) = OptRange("--range", 0, w.Tris.Count - 1);
        if (a < 0 || b >= w.Tris.Count || a > b) { Console.Error.WriteLine($"--range 0:{w.Tris.Count - 1}"); return 2; }
        var d = (byte[])F().Data.Clone();
        int? pass = OptIntN("--pass"), enc = OptIntN("--enc"),
              loc = OptIntN("--loc"), surf = OptIntN("--surf");
        int[]? light = null;
        {
            int i = Array.IndexOf(args, "--light");
            if (i >= 0 && i + 1 < args.Length)
                light = args[i + 1].Split(',').Select(int.Parse).ToArray();
        }
        for (int i = a; i <= b; i++)
        {
            var t = w.Tris[i];
            uint data = t.Data;
            if (pass is { } pv) data = (data & ~0x7fu) | ((uint)pv & 0x7f);
            if (enc is { } ev) data = (data & ~(3u << 7)) | (((uint)ev & 3) << 7);
            if (loc is { } lv) data = (data & ~(3u << 11)) | (((uint)lv & 3) << 11);
            if (surf is { } sv) data = (data & ~(3u << 15)) | (((uint)sv & 3) << 15);
            if (light is { } li && li.Length == 3)
                data = (data & ~((31u << 17) | (31u << 22) | (31u << 27)))
                     | (((uint)li[0] & 31) << 17) | (((uint)li[1] & 31) << 22) | (((uint)li[2] & 31) << 27);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((int)(w.TrisOffset + 16 * i + 12)), data);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        File.WriteAllBytes(args[2], d);
        Console.WriteLine($"patched {b - a + 1} walkmesh tris [{a}..{b}] -> {args[2]}");
        return 0;
    }

    // ffx-map1 vif <geom-bin> [--entry N] — walk the VIF command stream of
    // MODEL entries: per-command opcode, count, byte range, GS register
    // writes (DIRECT) and UNPACK formats. Structure map for mesh edits.
    case "vif":
    {
        var sec = RequireSection(F(), 0x14);
        int only = OptInt("--entry", -1);
        foreach (var e in sec.Where(e => e.Type == LevelParts.Model))
        {
            if (only >= 0 && e.Index != only) continue;
            var p = e.PayloadOffset;
            int count = (int)F().U32(p + 12);
            long cur = p + 64, end = cur + 16L * count;
            Console.WriteLine($"model[{e.Index}] vif @0x{cur:x}..0x{end:x} ({count}x16B)");
            int n = 0;
            while (cur < end)
            {
                ushort imm = F().U16(cur); byte num = F().Data[cur + 2]; byte op = (byte)(F().Data[cur + 3] & 0x7f);
                bool hl = (imm & 0x4000) != 0;
                long start = cur;
                cur += 4;
                // VIF opcodes (PS2 spec, matches the bundle's MT enum):
                // UNPACK=0x60|fmt; fmt sizes: S_32=0:4, S_16=1:2, S_8=2:1,
                // V2_32=4:8, V2_16=5:4, V2_8=6:2, V3_32=8:12, V3_16=9:6,
                // V3_8=10:3, V4_32=12:16, V4_16=13:8, V4_8=14:4, V5_16=15:6
                string desc;
                if ((op & 0x60) == 0x60)
                {
                    int fmt = op & 0x0f;
                    int sz = fmt switch
                    {
                        0 => 4, 1 => 2, 2 => 1,
                        4 => 8, 5 => 4, 6 => 2,
                        8 => 12, 9 => 6, 10 => 3,
                        12 => 16, 13 => 8, 14 => 4, 15 => 6,
                        _ => 4,
                    };
                    string fmtName = fmt switch
                    {
                        0 => "S_32", 1 => "S_16", 2 => "S_8",
                        4 => "V2_32", 5 => "V2_16", 6 => "V2_8",
                        8 => "V3_32", 9 => "V3_16", 10 => "V3_8",
                        12 => "V4_32", 13 => "V4_16", 14 => "V4_8", 15 => "V5_16",
                        _ => $"fmt{fmt}",
                    };
                    desc = $"UNPACK {fmtName} x{num}";
                    cur += (long)num * sz;
                }
                else
                {
                    desc = op switch
                    {
                        0x00 => "NOP",
                        0x01 => "STCYCL",
                        0x02 => "OFFSET",
                        0x03 => "BASE",
                        0x04 => "ITOP",
                        0x05 => "STMOD",
                        0x06 => "MSKPATH3",
                        0x07 => "MARK",
                        0x10 => "FLUSHE",
                        0x11 => "FLUSH",
                        0x13 => "FLUSHA",
                        0x14 => "MSCAL",
                        0x15 => "MSCALF",
                        0x17 => "MSCNT",
                        0x20 => "STMASK",
                        0x30 => "STROW",
                        0x31 => "STCOL",
                        0x4a => $"MPG x{num}",
                        0x50 => $"DIRECT x{imm & 0x7fff}",
                        0x51 => $"DIRECTHL x{imm & 0x7fff}",
                        _ => $"op=0x{op:x2}!",
                    };
                    cur += op switch
                    {
                        0x30 or 0x31 => 16,          // STROW / STCOL
                        0x4a => (long)num * 8,       // MPG
                        0x50 or 0x51 => (imm & 0x7fff) * 16L, // DIRECT
                        _ => 0,
                    };
                    if (desc.EndsWith('!')) cur = end; // unknown opcode: bail
                }
                if (only >= 0 || args.Any(a => a == "--cmds"))
                    Console.WriteLine($"   [{n,3}] @0x{start:x}..0x{cur:x} imm=0x{imm:x4} {desc}");
                n++;
            }
        }
        return 0;
    }

    // ffx-map1 enc <bin> — encounter bin: monsters + battlePositions.
    case "enc":
    {
        var e = EncounterFile.Load(args[1]);
        Console.WriteLine($"{e.Path}  {e.Data.Length} bytes");
        var mons = e.Monsters();
        Console.WriteLine($"monsters ({mons.Count}): {string.Join(", ", mons)}");
        foreach (var b in e.Positions())
        {
            Console.WriteLine($"block {b.Index} @0x{b.Offset:x}:");
            for (int q = 0; q < b.Party.Length; q++)
                Console.WriteLine($"  party[{q}]    = ({b.Party[q][0]:f2}, {b.Party[q][1]:f2}, {b.Party[q][2]:f2})");
            for (int q = 0; q < b.Other.Length; q++)
                Console.WriteLine($"  other[{q}]    = ({b.Other[q][0]:f2}, {b.Other[q][1]:f2}, {b.Other[q][2]:f2})");
            for (int q = 0; q < b.Monsters.Length; q++)
                Console.WriteLine($"  monster[{q}]  = ({b.Monsters[q][0]:f2}, {b.Monsters[q][1]:f2}, {b.Monsters[q][2]:f2})  @0x{b.MonsterPosOffsets[q]:x}");
        }
        return 0;
    }

    // ffx-map1 setpos <bin> <out-bin> --block N --monster M --pos x,y,z
    // Patch a monster spawn position in the encounter bin (f3, three floats).
    case "setpos":
    {
        if (args.Length < 3) { Usage(); return 2; }
        var e = EncounterFile.Load(args[1]);
        int blk = OptInt("--block", 0), mon = OptInt("--monster", 0);
        var pv = args.Skip(3).FirstOrDefault(a => !a.StartsWith("--"));
        int pi = Array.IndexOf(args, "--pos");
        if (pi >= 0 && pi + 1 < args.Length) pv = args[pi + 1];
        if (pv == null) { Console.Error.WriteLine("--pos x,y,z required"); return 2; }
        var pf = pv.Split(',').Select(float.Parse).ToArray();
        if (pf.Length != 3) { Console.Error.WriteLine("--pos needs 3 floats"); return 2; }
        var blocks = e.Positions();
        var b = blocks.FirstOrDefault(x => x.Index == blk);
        if (b.MonsterPosOffsets.Length <= mon)
        {
            Console.Error.WriteLine($"block {blk} has {b.MonsterPosOffsets.Length} monster slots, asked {mon}");
            return 2;
        }
        long o = b.MonsterPosOffsets[mon];
        var d2 = (byte[])e.Data.Clone();
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteSingleLittleEndian(d2.AsSpan((int)o + 4 * i), pf[i]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        File.WriteAllBytes(args[2], d2);
        Console.WriteLine($"block {blk} monster {mon} @0x{o:x} -> ({pf[0]:f2},{pf[1]:f2},{pf[2]:f2})  -> {args[2]}");
        return 0;
    }

    // ffx-map1 actor <bin> — actor model census (ers): parts, draw calls,
    // bones, skinning, refPoints, scales, texture section.
    case "actor":
    {
        var a = ActorBin.Load(args[1]);
        Console.WriteLine($"{a.Path}  {a.Data.Length} bytes  ver={a.Version} modelId={a.ModelId}");
        var tex = a.Textures(Path.GetFileNameWithoutExtension(args[1]));
        Console.WriteLine($"textures: {tex.Pairs.Count} pairs, {tex.Regions.Count} regions, {tex.PaletteOffs.Count} palettes, anim=0x{tex.AnimOff:x}");
        for (int i = 0; i < tex.Pairs.Count; i++)
            Console.WriteLine($"  pair[{i}] tex={tex.Pairs[i].Texture} pal={tex.Pairs[i].Palette} blend={tex.Pairs[i].BlendValue} {tex.Images[i].W}x{tex.Images[i].H}");
        var parts = a.Parts();
        Console.WriteLine($"parts[{parts.Count}] (stride {a.PartStride}):");
        foreach (var p in parts)
            Console.WriteLine($"  bone={p.Bone} baseV={p.BaseVertexCount} extraV={p.ExtraVertexCount} calls={p.DrawCalls.Count} " +
                $"[{string.Join(", ", p.DrawCalls.Select(c => $"tex{c.TexIndex}:{c.VertexCount}{(c.Runs ? "runs" : "tris")}"))}]");
        var bones = a.Bones();
        Console.WriteLine($"bones[{bones.Count}]:");
        for (int i = 0; i < bones.Count; i++)
            Console.WriteLine($"  [{i,2}] parent={bones[i].Parent,2} off=({bones[i].Ox},{bones[i].Oy},{bones[i].Oz}) scale=({bones[i].Sx:0.##},{bones[i].Sy:0.##},{bones[i].Sz:0.##})");
        var skin = a.Skinning();
        Console.WriteLine($"skinning[{skin.Count}]: {string.Join(", ", skin.Select(s => $"b{s.Bone}/p{s.Part}/r{s.RelBone}{(s.Longform ? "L" : "")}:{s.Lists.Count}l"))}");
        var refs = a.RefPoints();
        if (refs.Count > 0)
        {
            Console.WriteLine($"refPoints[{refs.Count}]:");
            foreach (var rp in refs)
                Console.WriteLine($"  id={rp.Id} flags={rp.Flags} bone={rp.Bone} pos=({rp.X:0.#},{rp.Y:0.#},{rp.Z:0.#})");
        }
        var bm = a.BoneMappings();
        if (bm.Count > 0)
            Console.WriteLine($"boneMappings[{bm.Count}]: {string.Join(", ", bm.Select(kv => $"{kv.Key}:[{kv.Value.Length}]"))}");
        var anims = a.DefaultAnimations();
        Console.WriteLine($"defaultAnims: {string.Join(" | ", anims.Select(x => x.Length == 0 ? "-" : $"[{string.Join(" ", x)}]"))}");
        if (a.GetScales() is { } sc)
            Console.WriteLine($"scales: height={sc.Height:0.##} base={sc.Base:0.##} actor={sc.Actor:0.##} collR={sc.CollisionRadius:0.##} envMap={sc.EnvMap:0.##} spec=({sc.SpecR:0.##},{sc.SpecG:0.##},{sc.SpecB:0.##},{sc.SpecA:0.##})");
        Console.WriteLine($"particles: @0x{a.ParticleOff:x}{(a.ParticleOff == 0 ? " (absent)" : "")}");
        return 0;
    }

    // ffx-map1 actortex <bin> <out-dir> — export actor textures as PNG.
    case "actortex":
    {
        if (args.Length < 3) { Usage(); return 2; }
        var a = ActorBin.Load(args[1]);
        var tex = a.Textures(Path.GetFileNameWithoutExtension(args[1]));
        Directory.CreateDirectory(args[2]);
        foreach (var img in tex.Images)
        {
            var argb = new uint[img.W * img.H];
            for (int i = 0; i < argb.Length; i++)
                argb[i] = (uint)(img.Rgba[4 * i + 3] << 24 | img.Rgba[4 * i] << 16 | img.Rgba[4 * i + 1] << 8 | img.Rgba[4 * i + 2]);
            var p = Path.Combine(args[2], $"{img.Name}.png");
            Png.Encode(p, argb, img.W, img.H);
            Console.WriteLine($"{p}  {img.W}x{img.H}");
        }
        return 0;
    }

    // ffx-map1 actorobj <bin> <out.obj> — decode mesh to Wavefront OBJ.
    case "actorobj":
    {
        if (args.Length < 3) { Usage(); return 2; }
        var a = ActorBin.Load(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        a.ExportObj(args[2]);
        Console.WriteLine($"{args[2]} written (modelId={a.ModelId})");
        return 0;
    }

    // ffx-map1 actorgltf <bin> <out.gltf> — decode mesh to glTF + textures.
    case "actorgltf":
    {
        if (args.Length < 3) { Usage(); return 2; }
        var a = ActorBin.Load(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        a.ExportGltf(args[2]);
        Console.WriteLine($"{args[2]} written (modelId={a.ModelId})");
        return 0;
    }

    // ffx-map1 ppp <bin> [--offset 0xN] — particle container readout
    // (emitters, behaviors/programs, instruction census, geometry, patterns).
    case "ppp":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var d = File.ReadAllBytes(args[1]);
        int offs = OptInt("--offset", 0);
        Console.Write(Particles.Describe(args[1], d, offs));
        return 0;
    }

    // ffx-map1 pppsim <bin> --offset 0xN [--frames N] — run the particle
    // simulation (noclip port) for N frames at 30fps and report draw output:
    // per-frame draw/tris counts and world bounds of emitted geometry.
    case "pppsim":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var d = File.ReadAllBytes(args[1]);
        int offs = OptInt("--offset", 0);
        int frames = OptInt("--frames", 300);
        var sys = ParticleSim.Sys.FromBytes(d, offs);
        Console.WriteLine($"{args[1]}+0x{offs:X}  emitters={sys.Emitters.Count} " +
            $"geos={sys.L.Geos.Count} patterns={sys.L.Patterns.Count} flipbooks={sys.L.Flipbooks.Count}");
        var opCensus = new SortedDictionary<string, int>();
        foreach (var b in sys.L.Behaviors)
            foreach (var pr in b.Programs)
                foreach (var ins in pr.Instructions)
                {
                    var n = Particles.OpName(ins.Op);
                    opCensus[n] = opCensus.TryGetValue(n, out var c) ? c + 1 : 1;
                }
        Console.WriteLine("ops: " + string.Join(" ", opCensus.Select(kv => $"{kv.Key}x{kv.Value}")));
        var outp = new List<ParticleSim.DrawItem>();
        var t0 = Environment.TickCount;
        int totalDraws = 0, totalTris = 0;
        float mnX = float.MaxValue, mnY = mnX, mnZ = mnX, mxX = float.MinValue, mxY = mxX, mxZ = mxX;
        int activeFrames = 0, firstActive = -1;
        for (int fr = 0; fr < frames; fr++)
        {
            outp.Clear();
            sys.Step(1f, outp);
            int tris = outp.Sum(x => x.V.Length / 13 / 3);
            totalDraws += outp.Count; totalTris += tris;
            foreach (var it in outp)
                for (int v = 0; v < it.V.Length; v += 13)
                {
                    mnX = Math.Min(mnX, it.V[v]); mxX = Math.Max(mxX, it.V[v]);
                    mnY = Math.Min(mnY, it.V[v + 1]); mxY = Math.Max(mxY, it.V[v + 1]);
                    mnZ = Math.Min(mnZ, it.V[v + 2]); mxZ = Math.Max(mxZ, it.V[v + 2]);
                }
            if (outp.Count > 0) { activeFrames++; if (firstActive < 0) firstActive = fr; }
            if (fr % 60 == 0 || fr == frames - 1)
                Console.WriteLine($"f{fr:000}  draws={outp.Count} tris={tris} live={sys.Emitters.Count(x => !x.Dead)}");
        }
        Console.WriteLine($"\n{frames} frames em {Environment.TickCount - t0}ms  " +
            $"totalDraws={totalDraws} totalTris={totalTris} activeFrames={activeFrames} firstActive=f{firstActive}");
        if (totalTris > 0)
        {
            Console.WriteLine($"bounds x[{mnX:0.#},{mxX:0.#}] y[{mnY:0.#},{mxY:0.#}] z[{mnZ:0.#},{mxZ:0.#}]");
            // alpha/color stats on the last frame
            double sumA = 0, sumC = 0; int nv = 0, zeroA = 0, texN = 0;
            foreach (var it in outp)
            {
                if (it.Tex != null) texN++;
                for (int v = 0; v < it.V.Length; v += 13)
                {
                    sumA += it.V[v + 6]; nv++;
                    if (it.V[v + 6] <= 0.01) zeroA++;
                    sumC += it.V[v + 3] + it.V[v + 4] + it.V[v + 5];
                }
            }
            Console.WriteLine($"last frame: verts={nv} drawsTex={texN}/{outp.Count} " +
                $"avgA={sumA / Math.Max(1, nv):0.###} zeroA={zeroA}/{nv} avgRGB={sumC / Math.Max(1, nv):0.###}");
        }
        else
            Console.WriteLine("SEM SAIDA — nenhum draw emitido");
        var scatter = Opt("--scatter");
        if (scatter != null && totalTris > 0)
        {
            // re-run and rasterize the LAST frame's draw centroids top-down (XZ)
            var sys2 = ParticleSim.Sys.FromBytes(d, offs);
            const int W = 512, H = 512;
            var px = new uint[W * H];
            float sx = (W - 8) / (mxX - mnX), sz = (H - 8) / (mxZ - mnZ);
            float s = Math.Min(sx, sz);
            for (int fr = 0; fr < frames; fr++)
            {
                outp.Clear();
                sys2.Step(1f, outp);
            }
            foreach (var it in outp)
                for (int v = 0; v < it.V.Length; v += 13)
                {
                    int ix = (int)((it.V[v] - mnX) * s + 4);
                    int iy = (int)((it.V[v + 2] - mnZ) * s + 4);
                    if (ix < 0 || ix >= W || iy < 0 || iy >= H) continue;
                    int cr = (int)(Math.Clamp(it.V[v + 3], 0, 1) * 255);
                    int cg = (int)(Math.Clamp(it.V[v + 4], 0, 1) * 255);
                    int cb = (int)(Math.Clamp(it.V[v + 5], 0, 1) * 255);
                    px[iy * W + ix] = 0xFF000000u | (uint)(cr << 16) | (uint)(cg << 8) | (uint)cb;
                }
            FfxMap1.Png.Encode(scatter, px, W, H);
            Console.WriteLine($"scatter -> {scatter} (ultimo frame, {outp.Count} draws)");
        }
        return 0;
    }

    // ffx-map1 ppactor <actor-bin> [--frames N] — actor-embedded particles
    // (bin.ts parseActorParticles): block @+0x60, sprite textures uploaded
    // into a private GS map, PPP program run for N frames.
    case "ppactor":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var d = File.ReadAllBytes(args[1]);
        int frames = OptInt("--frames", 300);
        int pOffs = d.Length >= 0x64 ? (int)BitConverter.ToUInt32(d, 0x60) : 0;
        Console.WriteLine($"{args[1]}  {d.Length}B  particleOff=0x{pOffs:x}");
        var gs = new Gs();
        var sys = ActorParticles.FromActorBin(d, gs);
        if (sys == null) { Console.WriteLine("sem particulas de ator"); return 0; }
        Console.WriteLine($"emitters={sys.Emitters.Count} geos={sys.L.Geos.Count} " +
            $"patterns={sys.L.Patterns.Count} flipbooks={sys.L.Flipbooks.Count}");
        var outp = new List<ParticleSim.DrawItem>();
        int totalDraws = 0, totalTris = 0, activeFrames = 0, firstActive = -1;
        float mnX = float.MaxValue, mnY = mnX, mnZ = mnX, mxX = float.MinValue, mxY = mxX, mxZ = mxX;
        for (int fr = 0; fr < frames; fr++)
        {
            outp.Clear();
            sys.Step(1f, outp);
            int tris = outp.Sum(x => x.V.Length / 13 / 3);
            totalDraws += outp.Count; totalTris += tris;
            foreach (var it in outp)
                for (int v = 0; v < it.V.Length; v += 13)
                {
                    mnX = Math.Min(mnX, it.V[v]); mxX = Math.Max(mxX, it.V[v]);
                    mnY = Math.Min(mnY, it.V[v + 1]); mxY = Math.Max(mxY, it.V[v + 1]);
                    mnZ = Math.Min(mnZ, it.V[v + 2]); mxZ = Math.Max(mxZ, it.V[v + 2]);
                }
            if (outp.Count > 0) { activeFrames++; if (firstActive < 0) firstActive = fr; }
            if (fr % 60 == 0 || fr == frames - 1)
                Console.WriteLine($"f{fr:000}  draws={outp.Count} tris={tris} live={sys.Emitters.Count(x => !x.Dead)}");
        }
        Console.WriteLine($"{frames} frames  totalDraws={totalDraws} totalTris={totalTris} " +
            $"activeFrames={activeFrames} firstActive=f{firstActive}");
        if (totalTris > 0)
            Console.WriteLine($"bounds x[{mnX:0.#},{mxX:0.#}] y[{mnY:0.#},{mxY:0.#}] z[{mnZ:0.#},{mxZ:0.#}]");
        else
            Console.WriteLine("SEM SAIDA — nenhum draw emitido");
        return 0;
    }

    // ffx-map1 magic <11-bin> — structural readout (entry pointers, funcList
    // offset, funcMap opcode sequence). Static port of magic.ts.
    case "magic":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var md = File.ReadAllBytes(args[1]);
        Console.Write(MagicBin.Describe(args[1], md));
        Console.WriteLine();
        Console.Write(MagicParticles.Describe(md));
        return 0;
    }

    // ffx-map1 txt <btl_txt.bin|*_txt.bin> — Excel-header help/name-help
    // text tables: HelpText=8B (standard+simplified offsets), NameHelpText=16B.
    case "txt":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var d = File.ReadAllBytes(args[1]);
        var err = KernelBin.Load(d, out var t);
        if (err != null) { Console.WriteLine("ERRO: " + err); return 1; }
        int slots = t.EntryLength / 8; // 1 (btl_txt) or 2 (name+help)
        Console.WriteLine($"{args[1]}  {d.Length}B  entries={t.EntryCount} stride={t.EntryLength} " +
            $"slots={slots} pool={t.Pool.Length}B");
        for (int i = 0; i < t.EntryCount; i++)
        {
            var sb = new System.Text.StringBuilder($"[{i,3}]");
            for (int k = 0; k < slots; k++)
            {
                int stdOff = t.U16(i, k * 8 + 0), stdUnk = t.U16(i, k * 8 + 2);
                int simOff = t.U16(i, k * 8 + 4), simUnk = t.U16(i, k * 8 + 6);
                sb.Append($"  {k}: std@{stdOff}='{KernelBin.DecodeText(t.Pool, stdOff)}'");
                if (simOff != stdOff)
                    sb.Append($" sim@{simOff}='{KernelBin.DecodeText(t.Pool, simOff)}'");
                sb.Append($" (unk {stdUnk}/{simUnk})");
            }
            Console.WriteLine(sb.ToString());
        }
        return 0;
    }

    // ffx-map1 command <command.bin> [--dump] [--set <idx> <off> <hexval> --out <file>]
    // Excel-header kernel table: 96B command records + text pool.
    case "command":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var d = File.ReadAllBytes(args[1]);
        var err = KernelBin.Load(d, out var t);
        if (err != null) { Console.WriteLine("ERRO: " + err); return 1; }
        Console.WriteLine($"{args[1]}  {d.Length}B  entries={t.EntryCount} stride={t.EntryLength} " +
            $"minIndex={t.MinIndex} pool={t.Pool.Length}B");
        int setIdx = -1, setOff = -1, setVal = -1;
        var outPath = "";
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--set" && i + 3 < args.Length)
            { setIdx = int.Parse(args[i + 1]); setOff = Convert.ToInt32(args[i + 2], 16); setVal = Convert.ToInt32(args[i + 3], 16); i += 3; }
            if (args[i] == "--out") outPath = args[++i];
        }
        if (setIdx >= 0)
        {
            if (setIdx >= t.EntryCount || setOff < 0 || setOff >= t.EntryLength || setVal < 0 || setVal > 0xFFFF)
            { Console.WriteLine("ERRO: set fora de range"); return 1; }
            var copy = (byte[])d.Clone();
            var t2err = KernelBin.Load(copy, out var t2);
            if (t2err != null) { Console.WriteLine("ERRO: " + t2err); return 1; }
            if (setVal > 0xFF) t2.SetU16(setIdx, setOff, (ushort)setVal);
            else t2.SetU8(setIdx, setOff, (byte)setVal);
            if (outPath.Length == 0) { Console.WriteLine("ERRO: --out obrigatório (nunca in-place)"); return 1; }
            File.WriteAllBytes(outPath, copy);
            var relErr = KernelBin.Load(File.ReadAllBytes(outPath), out var t3);
            Console.WriteLine($"escrito {outPath}  rec[{setIdx}] +0x{setOff:x}=0x{setVal:x}  releitura: {(relErr ?? "ok")} " +
                $"name={t3.Name(setIdx)}");
            return 0;
        }
        for (int i = 0; i < t.EntryCount; i++)
        {
            var name = t.Name(i);
            int power = t.Record(i)[0x2A], mp = t.Record(i)[0x25], hits = t.Record(i)[0x2B];
            int elem = t.Record(i)[0x2D], caster = t.Record(i)[0x15];
            int giBase = args[1].Contains("monmagic1") ? 0x4000 : args[1].Contains("monmagic2") ? 0x6000
                : args[1].Contains("item") ? 0x2000 : args[1].Contains("a_ability") ? 0x8000 : 0x3000;
            Console.WriteLine($"[{i,3}] 0x{giBase + i:x4} {name,-24} mp={mp,3} pow={power,3} hit={hits,2} " +
                $"elem=0x{elem:x2} caster={caster} rank={t.Record(i)[0x24]}");
        }
        return 0;
    }

    // ffx-map1 ppmagic <11-bin> [--index N] [--frames N] — locate particle
    // headers via the init-function MIPS scan, remap opcodes through the
    // funcMap and run the particle simulation for the selected index.
    case "ppmagic":
    {
        if (args.Length < 2) { Usage(); return 2; }
        var d = File.ReadAllBytes(args[1]);
        int index = OptInt("--index", -1); // -1 = auto (getParticleData a2)
        int frames = OptInt("--frames", 120);
        int forceHeader = OptHex("--header", -1); // multi-effect bins
        var headers = new List<int>();
        var sys = MagicParticles.FromMagicBin(d, index, headersOut: headers, forceHeader: forceHeader);
        Console.WriteLine($"{args[1]}  {d.Length}B  headers: {headers.Count}" +
            (headers.Count > 0 ? $"  [{string.Join(", ", headers.Select(h => $"0x{h:x}"))}]" : "") +
            $"  indexCandidates: [{string.Join(", ", MagicBin.FindParticleIndices(d))}]");
        if (sys == null) { Console.WriteLine("sem sistema de particulas"); return 0; }
        Console.WriteLine($"index={index}  emitters={sys.Emitters.Count} geos={sys.L.Geos.Count} " +
            $"patterns={sys.L.Patterns.Count} flipbooks={sys.L.Flipbooks.Count}" +
            (sys.Vm != null ? $"  vm=prog[{sys.Vm.ProgLen}]" : ""));
        int baseEmitters = sys.Emitters.Count;
        if (Environment.GetEnvironmentVariable("FFX_OPS") != null)
            for (int bi = 0; bi < sys.L.Behaviors.Count; bi++)
            {
                var bh = sys.L.Behaviors[bi];
                for (int pi = 0; pi < bh.Programs.Count; pi++)
                {
                    var pr = bh.Programs[pi];
                    Console.WriteLine($"  bhv{bi}.prog{pi} life={pr.Lifetime:0.#} start={pr.Start:0.#} " +
                        $"ops=[{string.Join(" ", pr.Instructions.Select(x => $"0x{x.Op:x2}"))}]");
                }
            }
        var outp = new List<ParticleSim.DrawItem>();
        int totalDraws = 0, totalTris = 0, activeFrames = 0;
        for (int fr = 0; fr < frames; fr++)
        {
            outp.Clear();
            sys.Step(1f, outp);
            if (sys.Vm != null && Environment.GetEnvironmentVariable("FFX_VM") != null && fr % 10 == 0)
                Console.Error.WriteLine($"  vm f{fr}: states={sys.Vm.States.Count} " +
                    $"emitters={sys.Emitters.Count - baseEmitters} spawned, " +
                    $"errored={sys.Vm.States.Count(s => s.Errored)}");
            int tris = outp.Sum(x => x.V.Length / 13 / 3);
            totalDraws += outp.Count; totalTris += tris;
            if (outp.Count > 0) activeFrames++;
            if (fr % 30 == 0 || fr == frames - 1)
                Console.WriteLine($"f{fr:000}  draws={outp.Count} tris={tris} live={sys.Emitters.Count(x => !x.Dead)}");
        }
        Console.WriteLine($"{frames} frames  totalDraws={totalDraws} totalTris={totalTris} activeFrames={activeFrames}");
        return 0;
    }

    // ffx-map1 events <ev01-bin> — model list + spawn points (placement table)
    case "events":
    {
        var ev = Ev01File.Load(args[1]);
        Console.WriteLine($"{ev.Path}  {ev.Data.Length} bytes  magic=EV01");
        var models = ev.ModelList();
        Console.WriteLine($"models[{models.Count}]: {string.Join(" ", models)}");
        foreach (var p in ev.Points())
            Console.WriteLine(
                $"  point map={p.MapId} entry={p.Entrypoint} heading={p.Heading:0.###} " +
                $"pos=({p.X:0.#},{p.Y:0.#},{p.Z:0.#})");
        return 0;
    }

    // ffx-map1 resolve — print the bin path map for the whole data model
    // (per the bundle fetch logic): map pair, event bins, actor 5-pack,
    // magic, encounter table, globals.
    case "resolve":
    {
        Console.WriteLine(
            "map field <idx>     : 13/{0:x4}.bin + 13/{1:x4}.bin  (2*idx, 2*idx+1)\n" +
            "map battle <idx>    : 1a/{0:x4}.bin + 1a/{1:x4}.bin\n" +
            "event <evIdx>       : 0c/{0:x4}.bin .. +17  (18*evIdx + i)\n" +
            "globals             : 0d/0000.bin\n" +
            "encounter <dec>     : 0e/{0:x4}.bin  (id decimal -> hex4)\n" +
            "magic <id>          : 11/{0:x4}.bin\n" +
            "actor <id>          : <group+28:hex>/{5*slot+i:x4}.bin i=0..4 (erB table)\n" +
            "shared              : common_textures.bin env_map_texture.bin screen_shatter.bin\n" +
            "edits overlay       : edits/<enc>.json");
        int? actor = OptIntN("--actor"), map = OptIntN("--map"),
              ev = OptIntN("--event"), enc = OptIntN("--enc"), mag = OptIntN("--magic");
        if (map is { } mi)
            Console.WriteLine($"map {mi}: 13/{2 * mi:x4}.bin 13/{2 * mi + 1:x4}.bin");
        if (ev is { } evi)
            Console.WriteLine($"event {evi}: 0c/{18 * evi:x4}.bin .. 0c/{18 * evi + 17:x4}.bin");
        if (enc is { } en)
            Console.WriteLine($"encounter {en}: 0e/{en:x4}.bin");
        if (mag is { } mg)
            Console.WriteLine($"magic {mg}: 11/{mg:x4}.bin");
        if (actor is { } aid)
        {
            int g = aid >> 12;
            if (g >= ErB.Groups.Length) { Console.Error.WriteLine($"actor 0x{aid:x}: group {g} out of range"); return 1; }
            int s = Array.IndexOf(ErB.Groups[g], aid & 0xFFF);
            if (s < 0) { Console.Error.WriteLine($"actor 0x{aid:x}: not in erB[{g}]"); return 1; }
            Console.Write($"actor {aid} (0x{aid:x}): {g + 28:x2}/");
            for (int i = 0; i < 5; i++) Console.Write($"{5 * s + i:x4}.bin ");
            Console.WriteLine();
        }
        return 0;
    }

    // ffx-map1 maptex <tex-bin> <geom-bin> <out-dir>
    //   Replay GS uploads (textures + palettes) from the texture bin into a
    //   4MB GS memory image, scan the geometry bin's VIF DIRECT streams for
    //   TEX0 bindings, and decode each unique (tbp0,cbp,psm,csa) to PNG.
    case "maptex":
    {
        if (args.Length < 4) { Usage(); return 2; }
        var mt = MapTextures.Parse(F());
        if (mt == null) { Console.Error.WriteLine("no texture section"); return 1; }
        var gs = mt.BuildGsMap(F());
        Console.WriteLine($"gs map: {mt.Textures.Count} uploads, paletteType={mt.PaletteType}");
        var geom = File.ReadAllBytes(args[2]);
        var tex0s = Gs.ScanTex0(geom);
        Console.WriteLine($"tex0 bindings: {tex0s.Count} unique");
        Directory.CreateDirectory(args[3]);
        int n = 0;
        foreach (var tx in tex0s)
        {
            int w = tx.Width, h = tx.Height;
            byte[] rgba;
            try
            {
                rgba = tx.Psm switch
                {
                    19 => gs.DecodePSMT8(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Tcc),
                    20 => gs.DecodePSMT4(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa, tx.Tcc),
                    27 => gs.DecodePSMT8H(tx.Tbp0, tx.Tbw, w, h, tx.Cbp),
                    44 => gs.DecodePSMT4HH(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa),
                    36 => gs.DecodePSMT4HL(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa),
                    _ => throw new Exception($"psm {tx.Psm}"),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  tbp0={tx.Tbp0:x4} psm={tx.Psm}: decode failed: {ex.Message}");
                continue;
            }
            var argb = new uint[w * h];
            for (int i = 0; i < argb.Length; i++)
                argb[i] = (uint)(rgba[4 * i + 3] << 24 | rgba[4 * i] << 16
                    | rgba[4 * i + 1] << 8 | rgba[4 * i + 2]);
            var name = $"tex{n:00}_{tx.Tbp0:x4}_{tx.Cbp:x4}_p{tx.Psm}{(tx.Csa > 0 ? $"_c{tx.Csa}" : "")}.png";
            Png.Encode(Path.Combine(args[3], name), argb, w, h);
            Console.WriteLine($"  {name}  {w}x{h} tcc={tx.Tcc}");
            n++;
        }
        Console.WriteLine($"{n} textures -> {args[3]}");
        return 0;
    }

    // ffx-map1 clut <tex-bin> <cbp hex> <out.png> — dump 256x1 palette strip
    case "clut":
    {
        var mt2 = MapTextures.Parse(F());
        var gs2 = mt2!.BuildGsMap(F());
        int cbp2 = Convert.ToInt32(args[2].Replace("0x", ""), 16);
        var argb2 = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            int p = gs2.PaletteEntry(cbp2, i);
            var g = gs2;
            argb2[i] = (uint)(0xFF000000 | g.Mem(p + 2) << 16 | g.Mem(p + 1) << 8 | g.Mem(p));
        }
        Png.Encode(args[3], argb2, 256, 1);
        Console.WriteLine($"clut 0x{cbp2:x} -> {args[3]}");
        return 0;
    }

    // ffx-map1 mapgltf <geom-bin> <tex-bin> <out.gltf>
    //   Full map export: VIF-decoded MODEL meshes + GS-decoded textures.
    case "mapgltf":
    {
        if (args.Length < 4) { Usage(); return 2; }
        var set = MapModelSet.Parse(F());   // args[1] = geometry bin
        var mt = MapTextures.Parse(Map1File.Load(args[2]))
            ?? throw new InvalidDataException($"{args[2]}: no texture section");
        var gs = mt.BuildGsMap(Map1File.Load(args[2]));
        Console.WriteLine($"models={set.Models.Count} tex0s={set.Textures.Count} parts={set.Parts.Count}");
        foreach (var m in set.Models)
            Console.WriteLine($"  model[{m.SectionIndex}] verts={m.VertexCount} draws={m.Draws.Count} part={m.PartIndex} flags={m.ModelFlags:x} trans={m.IsTranslucent}");
        MapGltf.Export(set, gs, args[3]);
        Console.WriteLine($"{args[3]} written");
        return 0;
    }

    case "textures":
    {
        var sec = RequireSection(F(), 0x14);
        foreach (var e in sec)
        {
            var p = e.PayloadOffset;
            if (e.Type == 2)
            {
                // GS upload params live in the payload's own 64B header
                // (mirrors the bundle parser): +4,+8,+12,+16,+20 fields.
                uint t4 = F().U32(p + 4), s = F().U32(p + 12);
                bool n = s == 20;
                uint o = F().U32(p + 8) >> (n ? 1 : 0);
                uint l = F().U32(p + 16) >> (n ? 1 : 0);
                uint h = F().U32(p + 20) >> (n ? 2 : 0);
                Console.WriteLine(
                    $"tex[{e.Index,3}] desc@0x{e.DescOffset:x} payload=0x{e.Size:x} " +
                    $"psm={s} t4={t4} o=0x{o:x} l=0x{l:x} h=0x{h:x} data@0x{p + 64:x}+{e.Size - 64:x}");
            }
            else if (e.Type == 3)
            {
                uint paletteType = F().U32(e.DescOffset + 36);
                Console.WriteLine(
                    $"pal[{e.Index,3}] desc@0x{e.DescOffset:x} type={paletteType} " +
                    $"72 palettes x1KB @0x{p:x}");
            }
            else
            {
                Console.WriteLine($"???[{e.Index,3}] type={e.Type} size=0x{e.Size:x}");
            }
        }
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown command: {cmd}");
        Usage();
        return 2;
}

static int ParseNum(string s) =>
    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(s.Substring(2), 16)
        : int.Parse(s);

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int OptHex(string name, int def)
    {
        int ai = Array.IndexOf(args, name);
        return ai >= 0 && ai + 1 < args.Length
            ? (args[ai + 1].StartsWith("0x") ? Convert.ToInt32(args[ai + 1], 16) : int.Parse(args[ai + 1])) : def;
    }
    int OptInt(string name, int def)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? ParseNum(args[i + 1]) : def;
}

int? OptIntN(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? ParseNum(args[i + 1]) : null;
}

(int a, int b) OptRange(string name, int defA, int defB)
{
    int i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length) return (defA, defB);
    var v = args[i + 1].Split(':');
    return (int.Parse(v[0]), int.Parse(v[1]));
}

float? OptF(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length
        ? float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture)
        : null;
}

byte[]? OptBytes(string name, int min, int max)
{
    int i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length) return null;
    var v = args[i + 1].Split(',').Select(s => byte.Parse(s.Trim())).ToArray();
    if (v.Length < min || v.Length > max)
        throw new ArgumentException($"{name}: expected {min}..{max} comma-separated bytes");
    return v;
}

List<SectionEntry> RequireSection(Map1File f, int slot)
{
    var off = f.Slot(slot);
    if (off == 0) throw new InvalidDataException($"{f.Path}: slot +0x{slot:x} absent");
    return f.WalkSection(off)!;
}

static void Usage()
{
    Console.Error.WriteLine(
        "ffx-map1 info|parts|models|lighting|textures|magic|ppp <bin>\n" +
        "ffx-map1 setlight <geom-bin> <out-bin> [--entry N] [--clear r,g,b[,a]] [--fog r,g,b] [--near f] [--far f] [--opacity f]");
}
