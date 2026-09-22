// Headless map render — same pipeline as the viewport, PNG out via Png.cs.
using FfxMap1;

namespace FfxLab;

public static class RenderTest
{
    public static int RunMap(string geomPath, string texPath, string outPath, int size)
    {
        var geom = Map1File.Load(geomPath);
        var tex = Map1File.Load(texPath);
        var set = MapModelSet.Parse(geom);
        var mt = MapTextures.Parse(tex) ?? throw new InvalidDataException("no tex section");
        var gs = mt.BuildGsMap(tex);
        var r = MapRenderer.FromMap(set, gs);
        var wm = Walkmesh.Parse(geom);
        if (wm != null) r.AddWalkmesh(wm);
        if (double.TryParse(Environment.GetEnvironmentVariable("FFX_YAW"), out var yw)) r.Yaw = yw;
        if (double.TryParse(Environment.GetEnvironmentVariable("FFX_PITCH"), out var pt)) r.Pitch = pt;
        if (double.TryParse(Environment.GetEnvironmentVariable("FFX_DIST"), out var ds)) r.Dist = ds;
        if (double.TryParse(Environment.GetEnvironmentVariable("FFX_TX"), out var tx)) r.Tx = tx;
        if (double.TryParse(Environment.GetEnvironmentVariable("FFX_TY"), out var ty)) r.Ty = ty;
        if (double.TryParse(Environment.GetEnvironmentVariable("FFX_TZ"), out var tz)) r.Tz = tz;
        // map PPP: particle GS map = common_textures + tex-bin @0x18
        // sprites (noclip particleMap — separate from the level tex GS)
        int pppOffs = (int)geom.Slot(0x38);
        if (pppOffs > 0)
        {
            try
            {
                var sys = ParticleSim.Sys.FromBytes(geom.Data, pppOffs);
                var pgs = new Gs();
                string dd = Path.GetDirectoryName(texPath) ?? ".";
                string common = Path.GetFullPath(Path.Combine(dd, "..", "common_textures.bin"));
                if (File.Exists(common))
                    Sprites.Upload(File.ReadAllBytes(common), 0, pgs);
                int sp = (int)tex.Slot(0x18);
                if (sp > 0) Sprites.Upload(tex.Data, sp, pgs);
                r.FxGs = pgs;
                var fxl = new List<ParticleSim.DrawItem>();
                int warm = int.TryParse(Environment.GetEnvironmentVariable("FFX_FX_WARM"), out int w2) ? w2 : 60;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < warm; f++) { fxl.Clear(); sys.Step(1f, fxl); }
                if (Environment.GetEnvironmentVariable("FFX_BENCH") != null)
                    Console.Write($"sim={sw.ElapsedMilliseconds / (double)warm:0.#}ms/step ");
                if (Environment.GetEnvironmentVariable("FFX_FX_ONLY") is string only)
                    fxl = fxl.Where(it => it.Tag.StartsWith(only)).ToList();
                if (Environment.GetEnvironmentVariable("FFX_NO_FX") != "1")
                    r.SetFx(fxl);
                if (Environment.GetEnvironmentVariable("FFX_DUMP_FXTEX") is string fd)
                    r.DumpTextures(fd);
                if (Environment.GetEnvironmentVariable("FFX_NO_OVERLAY") == "1")
                    r.ShowOverlay = false;
                int texN = fxl.Count(it => it.Tex != null);
                Console.Write($"ppp: {sys.Emitters.Count} emitters {fxl.Count} draws tex={texN} ");
                if (Environment.GetEnvironmentVariable("FFX_FX_DBG") != null)
                    for (int ei = 0; ei < sys.Emitters.Count; ei++)
                    {
                        var esp = sys.Emitters[ei].Spec;
                        Console.WriteLine($"  em{ei}: scale=({esp.Scale[0]:0.##},{esp.Scale[1]:0.##},{esp.Scale[2]:0.##}) pos=({esp.Pos[0]:0.#},{esp.Pos[1]:0.#},{esp.Pos[2]:0.#})");
                    }
                if (Environment.GetEnvironmentVariable("FFX_FX_DBG") != null)
                {
                    var rfx = r.DebugFx().ToList();
                    for (int i = 0; i < Math.Min(fxl.Count, rfx.Count); i++)
                    {
                        var it = fxl[i]; var vv = it.V; int ti = rfx[i].tex;
                        float mnx = 1e30f, mxx = -1e30f, mny = 1e30f, mxy = -1e30f;
                        for (int q = 0; q + 2 < vv.Length; q += 13)
                        { mnx = Math.Min(mnx, vv[q]); mxx = Math.Max(mxx, vv[q]);
                          mny = Math.Min(mny, vv[q + 1]); mxy = Math.Max(mxy, vv[q + 1]); }
                        var t0 = it.Tex;
                        float mnu = 1e30f, mxu = -1e30f, mnv = 1e30f, mxv = -1e30f;
                        for (int q = 0; q + 8 < vv.Length; q += 13)
                        { mnu = Math.Min(mnu, vv[q + 7]); mxu = Math.Max(mxu, vv[q + 7]);
                          mnv = Math.Min(mnv, vv[q + 8]); mxv = Math.Max(mxv, vv[q + 8]); }
                        Console.WriteLine($"  fx{i}: {it.Tag} nv={vv.Length / 13} blend=0x{it.Blend:x2} texi={ti} " +
                            $"tex={(t0 == null ? "null" : $"psm{t0.Psm} tbp{t0.Tbp0:x} cbp{t0.Cbp:x} csa{t0.Csa} {t0.Width}x{t0.Height}")} " +
                            $"sz=({mxx - mnx:0.#}x{mxy - mny:0.#}) " +
                            $"uv=({mnu:0.##}..{mxu:0.##},{mnv:0.##}..{mxv:0.##}) " +
                            $"c=({vv[3]:0.#},{vv[4]:0.#},{vv[5]:0.#},{vv[6]:0.#})");
                    }
                }
            }
            catch (Exception ex) { Console.Write($"ppp fail: {ex.Message} "); }
        }
        Console.Write($"{set.Models.Count} models {wm?.Tris.Count ?? 0} walktris " +
            $"nulltex={r.NullTexDraws} ");
        if (Environment.GetEnvironmentVariable("FFX_MAP_DBG") != null)
        {
            var rows = new List<(float area, int idx, float[] v, int tex)>();
            int di = 0;
            foreach (var (v, txi, trans, cull) in r.DebugDraws())
            {
                float mnx = 1e30f, mxx = -1e30f, mny = 1e30f, mxy = -1e30f;
                for (int q = 0; q + 2 < v.Length; q += 13)
                { mnx = Math.Min(mnx, v[q]); mxx = Math.Max(mxx, v[q]);
                  mny = Math.Min(mny, v[q + 1]); mxy = Math.Max(mxy, v[q + 1]); }
                rows.Add(((mxx - mnx) * (mxy - mny), di++, v, txi));
            }
            foreach (var (ti, frac) in r.DebugTexGreen())
                Console.WriteLine($"  green tex#{ti}: {frac * 100:0.#}% pixels ~(0,255,0)");
            foreach (var (area, idx, v, txi) in rows.OrderByDescending(t => t.area).Take(12))
            {
                int nv = v.Length / 13;
                float rr = 0, gg = 0, bb = 0;
                for (int q = 0; q + 6 < v.Length; q += 13)
                { rr += v[q + 3]; gg += v[q + 4]; bb += v[q + 5]; }
                var tc = r.DebugTexAvg(txi);
                Console.WriteLine($"  draw#{idx}: tex={txi} nv={nv} area={area:0} " +
                    $"vcol=({rr / nv:0.##},{gg / nv:0.##},{bb / nv:0.##}) texavg=({tc[0]:0},{tc[1]:0},{tc[2]:0})");
            }
        }
        if (Environment.GetEnvironmentVariable("FFX_BENCH") != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int frames = 5;
            int W = size, H = size * 3 / 4;
            var fb = new byte[W * H * 4];
            var zb = new float[W * H];
            unsafe
            {
                fixed (byte* fp = fb) fixed (float* zp = zb)
                    for (int i = 0; i < frames; i++)
                        r.Render(fp, W, H, zp);
            }
            Console.Write($"raster={sw.ElapsedMilliseconds / (double)frames:0.#}ms/frame ");
        }
        return Blit(r, outPath, size);
    }

    public static int RunActor(string actorPath, string outPath, int size)
    {
        var a = ActorBin.Load(actorPath);
        bool bones = Environment.GetEnvironmentVariable("FFX_ACTOR_RAW") != "1";
        int only = int.TryParse(Environment.GetEnvironmentVariable("FFX_ACTOR_PART"), out int pi) ? pi : -1;
        var r = MapRenderer.FromActor(a, bones, only);
        // actor-embedded particles (block @+0x60, sprite textures in a private GS map)
        var ags = new Gs();
        var apsys = ActorParticles.FromActorBin(a.Data, ags);
        if (apsys != null)
        {
            r.FxGs = ags;
            var fxl = new List<ParticleSim.DrawItem>();
            int warm = int.TryParse(Environment.GetEnvironmentVariable("FFX_FX_WARM"), out int w) ? w : 10;
            for (int f = 0; f < warm; f++) { fxl.Clear(); apsys.Step(1f, fxl); }
            // actor FX verts are in scaled (world) units; the standalone view
            // draws the raw model, so expand FX by 1/totalScale to match
            float ts = a.GetScales() is { } sc2 ? sc2.Base * sc2.Actor / sc2.Offset / 100f : 1f;
            if (ts > 0 && Math.Abs(ts - 1) > 1e-6)
                foreach (var it in fxl)
                    for (int v = 0; v < it.V.Length; v += 13)
                    { it.V[v] /= ts; it.V[v + 1] /= ts; it.V[v + 2] /= ts; }
            r.SetFx(fxl);
            Console.WriteLine($"actorFX: {apsys.Emitters.Count} emitters, {fxl.Count} draws @f{warm}");
        }
        // diagnostic: raw vert bounds vs bone world translation per part
        var bw = a.BoneWorld();
        foreach (var p in a.Parts())
        {
            var (pos, _) = a.VertexPool(p);
            float mnx = 1e30f, mny = 1e30f, mnz = 1e30f, mxx = -1e30f, mxy = -1e30f, mxz = -1e30f;
            foreach (var q in pos)
            { mnx = Math.Min(mnx, q[0]); mny = Math.Min(mny, q[1]); mnz = Math.Min(mnz, q[2]);
              mxx = Math.Max(mxx, q[0]); mxy = Math.Max(mxy, q[1]); mxz = Math.Max(mxz, q[2]); }
            var m = p.Bone >= 0 && p.Bone < bw.Count ? bw[p.Bone] : null;
            Console.WriteLine($"part bone={p.Bone} verts=({mnx:F0}..{mxx:F0},{mny:F0}..{mxy:F0},{mnz:F0}..{mxz:F0}) boneT=({m?[12] ?? 0:F0},{m?[13] ?? 0:F0},{m?[14] ?? 0:F0})");
        }
        return Blit(r, outPath, size);
    }

    // --render-magic <11-bin> <index> <warm frames> — standalone magic PPP:
    // headers scanned, sprites uploaded to a private GS map, sim warmed N
    // frames, FX layer fit to bounds and rendered alone.
    public static int RunMagic(string binPath, int index, int warm, string outPath, int size)
    {
        var d = File.ReadAllBytes(binPath);
        var gs = new Gs();
        var headers = new List<int>();
        var sys = MagicParticles.FromMagicBin(d, index, gs, headers);
        if (sys == null) { Console.WriteLine("no magic particles"); return 1; }
        Console.WriteLine($"headers: [{string.Join(", ", headers.Select(h => $"0x{h:x}"))}] " +
            $"emitters={sys.Emitters.Count} geos={sys.L.Geos.Count} flipbooks={sys.L.Flipbooks.Count}");
        var r = MapRenderer.Empty();
        r.FxGs = gs;
        var fxl = new List<ParticleSim.DrawItem>();
        var mn = new float[] { 1e30f, 1e30f, 1e30f };
        var mx = new float[] { -1e30f, -1e30f, -1e30f };
        for (int f = 0; f < warm; f++)
        {
            fxl.Clear();
            sys.Step(1f, fxl);
            foreach (var it in fxl)
                for (int v = 0; v + 2 < it.V.Length; v += 13)
                    for (int c = 0; c < 3; c++)
                    { mn[c] = Math.Min(mn[c], it.V[v + c] * 0.1f); mx[c] = Math.Max(mx[c], it.V[v + c] * 0.1f); }
        }
        Console.WriteLine($"warm={warm}  draws={fxl.Count} " +
            $"bounds x[{mn[0]:0.#},{mx[0]:0.#}] y[{mn[1]:0.#},{mx[1]:0.#}] z[{mn[2]:0.#},{mx[2]:0.#}]");
        if (mn[0] < mx[0])
        {
            for (int c = 0; c < 3; c++) { mn[c] *= 1.2f; mx[c] *= 1.2f; }
            r.FitTo(mn, mx);
        }
        // billboards (AxialBillboardMatrix) need the camera before stepping
        var eye0 = r.EyePos();
        sys.CamPos = new[] { eye0[0] / 0.1f, eye0[1] / 0.1f, eye0[2] / 0.1f };
        sys.CamFwd = r.FwdVec();
        int texN = 0, zeroA = 0; double sumA = 0, sumC = 0; int nv = 0;
        foreach (var it in fxl)
        {
            if (it.Tex != null) texN++;
            for (int v = 0; v + 6 < it.V.Length; v += 13)
            {
                sumA += it.V[v + 6]; nv++;
                if (it.V[v + 6] <= 0.01) zeroA++;
                sumC += it.V[v + 3] + it.V[v + 4] + it.V[v + 5];
            }
        }
        Console.WriteLine($"verts={nv} drawsTex={texN}/{fxl.Count} avgA={sumA / Math.Max(1, nv):0.###} " +
            $"zeroA={zeroA}/{nv} avgRGB={sumC / Math.Max(1, nv):0.###}");
        r.SetFx(fxl);
        if (Environment.GetEnvironmentVariable("FFX_MAGIC_DEBUG") != null)
        {
            var sn = new float[] { 1e30f, 1e30f, 1e30f }; var sx = new float[] { -1e30f, -1e30f, -1e30f };
            foreach (var it in fxl)
                for (int v = 0; v + 2 < it.V.Length; v += 13)
                    for (int c = 0; c < 3; c++)
                    { sn[c] = Math.Min(sn[c], it.V[v + c]); sx[c] = Math.Max(sx[c], it.V[v + c]); }
            Console.WriteLine($"scaled bounds x[{sn[0]:0.#},{sx[0]:0.#}] y[{sn[1]:0.#},{sx[1]:0.#}] z[{sn[2]:0.#},{sx[2]:0.#}]  dist={r.Dist:0.#} t=({r.Tx:0.#},{r.Ty:0.#},{r.Tz:0.#})");
        }
        return Blit(r, outPath, size);
    }

    public static int RunActorAnim(string actorPath, string animPath, int animId, float time, string outPath, int size)
    {
        var a = ActorBin.Load(actorPath);
        var anim = ActorAnim.Parse(File.ReadAllBytes(animPath));
        var state = ActorAnim.BindState(a);
        var clip = animId >= 0 ? anim.Resolve(animId) : anim.Groups.SelectMany(g => g.Animations).FirstOrDefault();
        if (clip != null)
        {
            var bm = a.BoneMappings();
            bm.TryGetValue(animId >> 16 & 0xFFFF, out var map);
            var before = (float[])state.Clone();
            anim.EvalPose(clip, time, state, map);
            int changed = 0; float maxD = 0;
            for (int i = 0; i < state.Length; i++)
            { var dd = Math.Abs(state[i] - before[i]); if (dd > 1e-6f) { changed++; maxD = Math.Max(maxD, dd); } }
            Console.WriteLine($"anim {animId:x} segs={clip.Segments.Count} t={time} map={(map != null ? "y" : "n")} changed={changed} maxDelta={maxD:F3}");
        }
        else Console.WriteLine("anim not found; bind pose");
        var r = MapRenderer.FromActor(a, true, -1, state);
        return Blit(r, outPath, size);
    }

    /// <summary>Battle composition test: arena map + monsters at encounter
    /// positions (script units /10). Needs the actor bins already fetched.</summary>
    public static int RunEncounter(string listsPath, string encPath,
        string geomPath, string texPath, string outPath, int size)
    {
        var geom = Map1File.Load(geomPath);
        var texF = Map1File.Load(texPath);
        var set = MapModelSet.Parse(geom);
        var mt = MapTextures.Parse(texF) ?? throw new InvalidDataException("sem texturas");
        var gs = mt.BuildGsMap(texF);
        var r = MapRenderer.FromMap(set, gs);
        if (Environment.GetEnvironmentVariable("FFX_DUMP_TEX") is string dt)
            r.DumpTextures(dt);
        if (float.TryParse(Environment.GetEnvironmentVariable("FFX_BRIGHTNESS"), out var br))
            r.Brightness = br;
        if (Environment.GetEnvironmentVariable("FFX_NO_OVERLAY") == "1")
            r.ShowOverlay = false;
        // frame on the walkmesh (playable area) when present — arenas wrap
        // the battle area in a skydome that would dominate the fit
        var wm = Walkmesh.Parse(geom);
        if (wm != null)
        {
            r.AddWalkmesh(wm);
            var mn = new[] { 1e30f, 1e30f, 1e30f };
            var mx = new[] { -1e30f, -1e30f, -1e30f };
            foreach (var t in wm.Tris)
                foreach (var vi in new[] { t.V0, t.V1, t.V2 })
                {
                    var (x, y, z) = wm.VertPos(vi);
                    float[] q = { x / 10f, y / 10f, z / 10f };
                    for (int c = 0; c < 3; c++)
                    { mn[c] = Math.Min(mn[c], q[c]); mx[c] = Math.Max(mx[c], q[c]); }
                }
            r.FitBounds = (mn, mx);
            Console.WriteLine($"walkmesh bounds x[{mn[0]:0.#}..{mx[0]:0.#}] y[{mn[1]:0.#}..{mx[1]:0.#}] z[{mn[2]:0.#}..{mx[2]:0.#}]");
        }
        Console.WriteLine($"arena bounds x[{r.BoundsMin[0]:0.#}..{r.BoundsMax[0]:0.#}] y[{r.BoundsMin[1]:0.#}..{r.BoundsMax[1]:0.#}] z[{r.BoundsMin[2]:0.#}..{r.BoundsMax[2]:0.#}]");
        Console.WriteLine($"tex: {r.NullTexDraws}/{r.DrawCalls} draws sem textura decodificada");
        var enc = EncounterFile.Load(encPath);
        var mons = enc.Monsters();
        var blocks = enc.Positions();
        // overlay edits: same contract the viewer POSTs to /api/edits/<enc>
        // (actors[slot] = {position:[dx,dy,dz] added, heading added, scale mult})
        var edits = LoadEncEdits(Environment.GetEnvironmentVariable("FFX_ENC_EDITS"),
            out var extras, out var removed);
        Console.WriteLine($"enc: {mons.Count} monsters, {blocks.Count} pos blocks" +
            (extras.Count + removed.Count > 0 ? $" (+{extras.Count} extra, -{removed.Count})" : ""));
        string dataDir = Path.GetDirectoryName(Path.GetDirectoryName(encPath.TrimEnd('/'))) ?? ".";
        if (blocks.Count > 0)
        {
            var blk = blocks[0];
            const float inv = 1f; // battle positions are already file units
            for (int i = 0; i < mons.Count && i < blk.Monsters.Length; i++)
            {
                if (removed.Contains(i)) { Console.WriteLine($"  slot {i}: removed (overlay)"); continue; }
                int mid = mons[i];
                int g = mid >> 12;
                int slot = g < ErB.Groups.Length ? Array.IndexOf(ErB.Groups[g], mid & 0xFFF) : -1;
                if (slot < 0) { Console.WriteLine($"  mon {mid}: fora de erB[{g}]"); continue; }
                var mp = Path.Combine(dataDir, $"{g + 28:x2}", $"{5 * slot:x4}.bin");
                if (!File.Exists(mp)) { Console.WriteLine($"  mon {mid}: {mp} ausente"); continue; }
                var a = ActorBin.Load(mp);
                var pos = blk.Monsters[i];
                var sc = a.GetScales();
                float ts = sc.HasValue ? sc.Value.Base * sc.Value.Actor / sc.Value.Offset / 100f : 0.01f;
                // face the first party spot (viewer modelMatrix rotates -heading-π/2)
                float hx = -pos[0], hz = -pos[2];
                if (blk.Party.Length > 0) { hx = blk.Party[0][0] - pos[0]; hz = blk.Party[0][2] - pos[2]; }
                float rot = -MathF.Atan2(hx, hz) - MathF.PI / 2;
                float px = pos[0] * inv, py = pos[1] * inv, pz = pos[2] * inv;
                if (edits.TryGetValue(i, out var ed))
                {
                    px += ed[0]; py += ed[1]; pz += ed[2];
                    if (ed.Length > 4) rot += ed[4];
                    if (ed.Length > 5) ts *= ed[5];
                    Console.WriteLine($"  [edit] slot {i}: +({ed[0]:0.#},{ed[1]:0.#},{ed[2]:0.#}) heading+{(ed.Length > 4 ? ed[4] : 0):0.##} scale*{(ed.Length > 5 ? ed[5] : 1):0.##}");
                }
                r.AddActor(a, MapRenderer.PlacementM(px, py, pz, rot, ts));
                Console.WriteLine($"  mon {mid} @ ({pos[0]:0.#},{pos[1]:0.#},{pos[2]:0.#}) scale={ts:0.####}");
            }
            // overlay extras: absolute placements
            foreach (var (xmid, xpos, xh, xs) in extras)
            {
                int g = xmid >> 12;
                int slot = g < ErB.Groups.Length ? Array.IndexOf(ErB.Groups[g], xmid & 0xFFF) : -1;
                if (slot < 0) { Console.WriteLine($"  extra {xmid}: fora de erB[{g}]"); continue; }
                var mp = Path.Combine(dataDir, $"{g + 28:x2}", $"{5 * slot:x4}.bin");
                if (!File.Exists(mp)) { Console.WriteLine($"  extra {xmid}: {mp} ausente"); continue; }
                var a = ActorBin.Load(mp);
                var sc = a.GetScales();
                float ts = (sc.HasValue ? sc.Value.Base * sc.Value.Actor / sc.Value.Offset / 100f : 0.01f) * xs;
                float hx = -xpos[0], hz = -xpos[2];
                if (blk.Party.Length > 0) { hx = blk.Party[0][0] - xpos[0]; hz = blk.Party[0][2] - xpos[2]; }
                float rot = -MathF.Atan2(hx, hz) - MathF.PI / 2 + xh;
                r.AddActor(a, MapRenderer.PlacementM(xpos[0], xpos[1], xpos[2], rot, ts));
                Console.WriteLine($"  extra mon {xmid} @ ({xpos[0]:0.#},{xpos[1]:0.#},{xpos[2]:0.#}) scale={ts:0.####}");
            }
            // battle framing: eye at the party spot looking at the monsters
            if (blk.Party.Length > 0)
            {
                var p0 = blk.Party[0];
                double cx = 0, cy = 0, cz = 0; int n = 0;
                for (int i = 0; i < mons.Count && i < blk.Monsters.Length; i++)
                { cx += blk.Monsters[i][0]; cy += blk.Monsters[i][1]; cz += blk.Monsters[i][2]; n++; }
                if (n > 0)
                {
                    // step back from the centroid so the battle fills the
                    // frame without clipping through party-position geometry
                    double bx = p0[0] + (p0[0] - cx / n) * 0.45;
                    double bz = p0[2] + (p0[2] - cz / n) * 0.45;
                    r.LookFrom(bx, p0[1], bz, cx / n, cy / n, cz / n);
                    Console.WriteLine($"cam: ({bx:0.#},{p0[1]:0.#},{bz:0.#}) -> ({cx / n:0.#},{cy / n:0.#},{cz / n:0.#})");
                }
            }
        }
        int rc = Blit(r, outPath, size);
        // pick validation: each monster's projected center must pick itself
        int pk = 0;
        for (int i = 0; i < r.ActorInstances.Count; i++)
        {
            var ai = r.ActorInstances[i];
            var pr = r.ProjectWorld(ai.Center[0], ai.Center[1], ai.Center[2], size, size);
            if (pr == null) continue;
            int hit = r.PickActor((int)Math.Round(pr.Value.sx), (int)Math.Round(pr.Value.sy), size, size);
            Console.WriteLine($"  pick[{i}] @({pr.Value.sx:0},{pr.Value.sy:0}) -> {hit}{(hit == i ? " OK" : " MISS")}");
            if (hit == i) pk++;
        }
        Console.WriteLine($"pick: {pk}/{r.ActorInstances.Count} monsters hit");
        // drag validation: move inst 0 +20x and re-project — center must follow
        if (r.ActorInstances.Count > 0)
        {
            var a0 = r.ActorInstances[0];
            var p0 = r.ProjectWorld(a0.Center[0], a0.Center[1], a0.Center[2], size, size);
            if (a0.Place != null)
                r.MoveActorTo(0, a0.Place[12] + 20, a0.Place[13], a0.Place[14]);
            var p1 = r.ProjectWorld(a0.Center[0], a0.Center[1], a0.Center[2], size, size);
            Console.WriteLine($"move: center ({p0?.sx:0},{p0?.sy:0}) -> ({p1?.sx:0},{p1?.sy:0})");
            if (a0.Place != null)
                r.MoveActorTo(0, a0.Place[12] - 20, a0.Place[13], a0.Place[14]);
        }
        return rc;
    }

    /// <summary>Field scene + EV01 actors headless: 13/ pair + 0c/18*ev bin.
    /// Approximation: modelList[i] at mapPoint[i] with the record heading
    /// (the real placement is driven by ATEL at runtime).</summary>
    public static int RunField(string geomPath, string texPath, string evPath,
        string outPath, int size)
    {
        var geom = Map1File.Load(geomPath);
        var texF = Map1File.Load(texPath);
        // the 13/ pair order varies: geom = the file with MODEL sections
        bool gHasModels = geom.WalkSection(geom.Slot(0x14))?.Any(x => x.Type == LevelParts.Model) == true;
        bool tHasModels = texF.WalkSection(texF.Slot(0x14))?.Any(x => x.Type == LevelParts.Model) == true;
        if (!gHasModels && tHasModels) (geom, texF) = (texF, geom);
        var set = MapModelSet.Parse(geom);
        var mt = MapTextures.Parse(texF) ?? throw new InvalidDataException("sem texturas");
        var gs = mt.BuildGsMap(texF);
        var r = MapRenderer.FromMap(set, gs);
        if (float.TryParse(Environment.GetEnvironmentVariable("FFX_BRIGHTNESS"), out var br))
            r.Brightness = br;
        if (Environment.GetEnvironmentVariable("FFX_NO_OVERLAY") == "1")
            r.ShowOverlay = false;
        var wm = Walkmesh.Parse(geom);
        var fitMn = new[] { 1e30f, 1e30f, 1e30f };
        var fitMx = new[] { -1e30f, -1e30f, -1e30f };
        bool haveFit = false;
        if (wm != null)
        {
            r.AddWalkmesh(wm);
            // frame on walkmesh + actor points (stable A/B when actors toggle)
            foreach (var t in wm.Tris)
                foreach (var vi in new[] { t.V0, t.V1, t.V2 })
                {
                    var (x, y, z) = wm.VertPos(vi);
                    float[] q = { x / 10f, y / 10f, z / 10f };
                    for (int c = 0; c < 3; c++)
                    { fitMn[c] = Math.Min(fitMn[c], q[c]); fitMx[c] = Math.Max(fitMx[c], q[c]); }
                    haveFit = true;
                }
        }
        Console.WriteLine($"tex: {r.NullTexDraws}/{r.DrawCalls} draws sem textura (slots={r.TexCount} nulls={r.NullTexSlots}, tex<0: {r.NoTexIndexDraws})");
        Console.WriteLine($"map bounds x[{r.BoundsMin[0]:0.#}..{r.BoundsMax[0]:0.#}] y[{r.BoundsMin[1]:0.#}..{r.BoundsMax[1]:0.#}] z[{r.BoundsMin[2]:0.#}..{r.BoundsMax[2]:0.#}]");
        // evPath "none" still parses the event for framing but skips actors —
        // keeps one camera across the A/B (actors on/off)
        bool loadActors = evPath != "none";
        string evSrc = loadActors ? evPath : Environment.GetEnvironmentVariable("FFX_EV_FOR_FIT") ?? "";
        List<int> models = new();
        List<Ev01File.MapPoint> pts = new();
        if (evSrc.Length > 0 && File.Exists(evSrc))
        {
            var ev = Ev01File.Load(evSrc);
            models = ev.ModelList();
            pts = ev.Points();
            Console.WriteLine($"modelList[{models.Count}]: {string.Join(" ", models)}");
            Console.WriteLine($"mapPoints[{pts.Count}]");
            foreach (var p in pts)
                for (int c = 0; c < 3; c++)
                {
                    float v = c == 0 ? p.X : c == 1 ? p.Y : p.Z;
                    fitMn[c] = Math.Min(fitMn[c], v - 40f);
                    fitMx[c] = Math.Max(fitMx[c], v + 40f);
                }
            haveFit |= pts.Count > 0;
        }
        if (!loadActors) Console.WriteLine("(baseline: sem atores)");
        string dataDir = Path.GetDirectoryName(Path.GetDirectoryName(evPath.TrimEnd('/'))) ?? ".";
        int placed = 0;
        for (int i = 0; loadActors && i < models.Count; i++)
        {
            int pid = models[i];
            if (pid == 0) continue;
            int g = pid >> 12;
            int slot = g < ErB.Groups.Length ? Array.IndexOf(ErB.Groups[g], pid & 0xFFF) : -1;
            if (slot < 0) { Console.WriteLine($"  ator {pid}: fora de erB[{g}]"); continue; }
            var mp = Path.Combine(dataDir, $"{g + 28:x2}", $"{5 * slot:x4}.bin");
            if (!File.Exists(mp)) { Console.WriteLine($"  ator {pid}: {mp} ausente"); continue; }
            var a = ActorBin.Load(mp);
            var p = pts.Count > 0 ? pts[Math.Min(i, pts.Count - 1)] : default;
            var sc = a.GetScales();
            float ts = sc.HasValue ? sc.Value.Base * sc.Value.Actor / sc.Value.Offset / 100f : 0.01f;
            r.AddActor(a, MapRenderer.PlacementM(p.X, p.Y, p.Z, -p.Heading, ts));
            placed++;
            Console.WriteLine($"  ator {pid} @ ({p.X:0.#},{p.Y:0.#},{p.Z:0.#}) heading={p.Heading:0.##} scale={ts:0.####}");
        }
        Console.WriteLine($"atores posicionados: {placed} (aprox.: sem execucao do ATEL)");
        // field camera: eye at the first mapPoint looking along the corridor
        // (toward the next point, else the walkmesh center)
        if (pts.Count > 0)
        {
            var p0 = pts[0];
            // entrypoint heading = facing direction (viewer: Ry(-heading),
            // forward +Z => dir = (sin h, 0, cos h))
            double dx2 = Math.Sin(p0.Heading), dz2 = Math.Cos(p0.Heading);
            double tx2 = p0.X + dx2 * 150, ty2 = p0.Y - 5, tz2 = p0.Z + dz2 * 150;
            r.LookFrom(p0.X - dx2 * 60, p0.Y + 12, p0.Z - dz2 * 60, p0.X, p0.Y, p0.Z);
            Console.WriteLine($"cam: eye ({p0.X - dx2 * 60:0.#},{p0.Y + 12:0.#},{p0.Z - dz2 * 60:0.#}) -> ponto0 ({p0.X:0.#},{p0.Y:0.#},{p0.Z:0.#}) fwd=({dx2:0.##},{dz2:0.##})");
        }
        else if (haveFit) r.FitBounds = (fitMn, fitMx);
        return Blit(r, outPath, size);
    }

    /// <summary>Reads the viewer/lab edits JSON (actors[slot] = position delta
    /// + optional heading/scale) into a flat per-slot array.</summary>
    static Dictionary<int, float[]> LoadEncEdits(string? path,
        out List<(int mid, float[] pos, float heading, float scale)> extras,
        out HashSet<int> removed)
    {
        extras = new(); removed = new();
        var map = new Dictionary<int, float[]>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("extra", out var xe) && xe.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in xe.EnumerateArray())
            {
                var pos = new float[3]; int k = 0;
                if (x.TryGetProperty("position", out var pp))
                    foreach (var q in pp.EnumerateArray()) { if (k < 3) pos[k] = (float)q.GetDouble(); k++; }
                extras.Add((x.GetProperty("monster").GetInt32(), pos,
                    x.TryGetProperty("heading", out var hd) ? (float)hd.GetDouble() : 0,
                    x.TryGetProperty("scale", out var sc) ? (float)sc.GetDouble() : 1));
            }
        if (root.TryGetProperty("remove", out var rm) && rm.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in rm.EnumerateArray()) removed.Add(x.GetInt32());
        if (!root.TryGetProperty("actors", out var actors)) return map;
        foreach (var p in actors.EnumerateObject())
        {
            if (!int.TryParse(p.Name, out var slot)) continue;
            var v = p.Value;
            var e = new float[6];
            if (v.TryGetProperty("position", out var pos) && pos.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int k = 0;
                foreach (var x in pos.EnumerateArray()) { if (k < 3) e[k] = (float)x.GetDouble(); k++; }
            }
            if (v.TryGetProperty("heading", out var hd)) e[4] = (float)hd.GetDouble();
            e[5] = v.TryGetProperty("scale", out var sc) ? (float)sc.GetDouble() : 1f;
            map[slot] = e;
        }
        return map;
    }

    static unsafe int Blit(MapRenderer r, string outPath, int size)
    {
        if (!r.CameraSet) r.Fit();
        int W = size, H = size * 3 / 4;
        var frame = new byte[W * H * 4];
        var zb = new float[W * H];
        fixed (byte* fb = frame)
        fixed (float* zp = zb)
        {
            for (int i = 0; i < W * H * 4; i += 4)
            { fb[i] = 0x20; fb[i + 1] = 0x22; fb[i + 2] = 0x26; fb[i + 3] = 255; }
            r.Render(fb, W, H, zp);
        }
        var argb = new uint[W * H];
        for (int i = 0; i < W * H; i++)
            argb[i] = (uint)(frame[4 * i + 3] << 24 | frame[4 * i] << 16
                | frame[4 * i + 1] << 8 | frame[4 * i + 2]);
        Png.Encode(outPath, argb, W, H);
        Console.WriteLine($"{r.TriCount} tris {r.DrawCalls} draws -> {outPath}");
        return 0;
    }
}
