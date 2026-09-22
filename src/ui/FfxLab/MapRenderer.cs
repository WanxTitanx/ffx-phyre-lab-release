// Software map rasterizer — renders MapModelSet draw calls into a BGRA
// buffer with z-buffer, nearest texture sampling (repeat) and GS-style
// vertex-color modulation (tex * vcol * 2). Translucent models draw last.
// Offline/editor preview — not a runtime-accurate GS emulator.
using FfxMap1;

namespace FfxLab;

public sealed class MapRenderer
{
    public sealed class Tex { public int W, H; public byte[] Rgba = Array.Empty<byte>(); }

    readonly List<Tex?> _tex = new();
    // flattened draw list: (verts13, textureIndex, translucent, cullBackface)
    readonly List<(float[] v, int tex, bool trans, bool cull)> _draws = new();
    // editor overlays (walkmesh, spawn markers) — untextured, vertex-colored,
    // rendered last without alpha test
    readonly List<float[]> _overlay = new();
    // particle FX layer — rebuilt each animation frame by ParticleSim
    // blend = GS blend byte (0x00 replace / 0x42 subtract / 0x44 alpha /
    // 0x46 darken / 0x48 additive / 0x88 src*a replace)
    readonly List<(float[] v, int tex, int blend, bool noDepth)> _fx = new();
    readonly Dictionary<Gs.Tex0, int> _fxTexMap = new();
    Gs? _gs;
    /// <summary>PPP sprite GS map (noclip particleMap) — sprite uploads
    /// from common_textures + the particle bin, separate from the level
    /// texture GS. FX textures resolve here first.</summary>
    Gs? _fxGs;
    public Gs? FxGs { set => _fxGs = value; }
    List<(Gs.Tex0 Tex0, (uint Lo, uint Hi) Clamp)>? _srcTex;

    MapRenderer() { }

    /// <summary>Empty renderer — FX layer only (magic/actor particles).</summary>
    public static MapRenderer Empty() => new();

    // --- EFFECT keyframe playback ---------------------------------------
    // Draws keep raw (model-space) verts; per frame, parts with bound
    // effects re-compose their matrix and rewrite the draw verts in place.
    // The array reference survives the translucency sort.
    readonly List<(float[] Draw, float[] Raw, int Part)> _partDraws = new();
    readonly Dictionary<int, LevelParts.PartInfo> _partInfo = new();
    Map1File? _geomFile;
    Dictionary<int, List<LevelParts.LevelEffects.Fx>>? _partFx;

    /// <summary>Bind EFFECT tracks to part entry indices — viewport calls
    /// this after FromMap; TickEffects animates bound parts each frame.</summary>
    public void SetEffects(Map1File f,
        List<(int Part, LevelParts.LevelEffects.Fx Fx)> bindings)
    {
        _geomFile = f;
        _partFx = bindings.GroupBy(x => x.Part)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Fx).ToList());
    }

    /// <summary>Advance EFFECT animation one frame — re-transforms only
    /// the draws of parts that have bound tracks (noclip applyEffect:
    /// MOTION overwrites translation, ROTATION rebuilds rotation keeping
    /// translation, COMBINED rebuilds euler + basePos+delta).</summary>
    public void TickEffects(int frame)
    {
        if (_partFx == null || _geomFile == null || _partDraws.Count == 0) return;
        foreach (var (v, raw, part) in _partDraws)
        {
            if (!_partFx.TryGetValue(part, out var fxs)) continue;
            var p = _partInfo[part];
            var m = PartMatrix(p);
            foreach (var fx in fxs)
            {
                switch (fx.Type)
                {
                    case LevelParts.LevelEffects.Motion:
                        var tr = LevelParts.LevelEffects.Eval(_geomFile, fx, frame);
                        m[12] = tr[0]; m[13] = tr[1]; m[14] = tr[2];
                        break;
                    case LevelParts.LevelEffects.Rotation:
                        var er = LevelParts.LevelEffects.Eval(_geomFile, fx, frame);
                        var rm = Mat4.FromEulerRad(er[0], er[1], er[2], p.EulerOrder);
                        for (int i = 0; i < 12; i++) m[i] = rm[i];
                        break;
                    case LevelParts.LevelEffects.Combined:
                        var (ce, cp) = LevelParts.LevelEffects
                            .EvalCombined(_geomFile, fx, frame);
                        var cm = Mat4.FromEulerRad(ce[0], ce[1], ce[2], p.EulerOrder);
                        for (int i = 0; i < 12; i++) m[i] = cm[i];
                        m[12] = p.Px + cp[0]; m[13] = p.Py + cp[1]; m[14] = p.Pz + cp[2];
                        break;
                }
            }
            for (int i = 0; i + 12 < v.Length; i += 13)
            {
                float x = raw[i], y = raw[i + 1], z = raw[i + 2];
                v[i]     = m[0] * x + m[4] * y + m[8] * z + m[12];
                v[i + 1] = m[1] * x + m[5] * y + m[9] * z + m[13];
                v[i + 2] = m[2] * x + m[6] * y + m[10] * z + m[14];
            }
        }
        GeomVersion++;
    }

    /// <summary>Bumped whenever static draw verts change (EFFECT keyframes) —
    /// the GPU backend re-uploads static VBOs when this advances.</summary>
    public int GeomVersion;

    /// <summary>PPP space = level units = file verts ×10 (noclip LEVEL_MODEL_SCALE);
    /// the renderer draws raw file units, so scale particle verts by 0.1.</summary>
    const float FxScale = 0.1f;

    /// <summary>Replace the particle FX layer (called once per anim frame).</summary>
    public void SetFx(IEnumerable<ParticleSim.DrawItem> items)
    {
        _fx.Clear();
        foreach (var it in items)
        {
            var v = it.V;
            for (int i = 0; i + 2 < v.Length; i += 13)
            {
                v[i] *= FxScale; v[i + 1] *= FxScale; v[i + 2] *= FxScale;
            }
            _fx.Add((v, ResolveFxTex(it.Tex), it.Blend, it.NoDepth));
        }
    }

    public int FxDraws => _fx.Count;

    int ResolveFxTex(Gs.Tex0? t)
    {
        if (t == null) return -1;
        if (_fxTexMap.TryGetValue(t, out var cached)) return cached;
        int idx = -1;
        // match an already-decoded map texture on (tbp0,psm,cbp,csa)
        if (_srcTex != null)
            for (int i = 0; i < _srcTex.Count; i++)
            {
                var st = _srcTex[i].Tex0;
                if (st.Tbp0 == t.Tbp0 && st.Psm == t.Psm && st.Cbp == t.Cbp && st.Csa == t.Csa)
                { idx = i; break; }
            }
        if (idx < 0)
        {
            // decode a texture the map models never reference directly —
            // PPP sprites live in the particle GS map (common_textures +
            // particle-bin uploads), but may also reference level VRAM
            foreach (var src in new[] { _fxGs, _gs })
            {
                if (src == null) continue;
                try
                {
                    int w = t.Width, h = t.Height;
                    byte[] rgba = t.Psm switch
                    {
                        19 => src.DecodePSMT8(t.Tbp0, t.Tbw, w, h, t.Cbp, t.Tcc),
                        20 => src.DecodePSMT4(t.Tbp0, t.Tbw, w, h, t.Cbp, t.Csa, t.Tcc),
                        27 => src.DecodePSMT8H(t.Tbp0, t.Tbw, w, h, t.Cbp),
                        0 => src.DecodePSMT32(t.Tbp0, t.Tbw, w, h),
                        36 => src.DecodePSMT4HL(t.Tbp0, t.Tbw, w, h, t.Cbp, t.Csa),
                        44 => src.DecodePSMT4HH(t.Tbp0, t.Tbw, w, h, t.Cbp, t.Csa),
                        _ => Array.Empty<byte>(),
                    };
                    if (rgba.Length > 0)
                    {
                        // skip all-zero decodes — the texture isn't in this GS
                        bool any = false;
                        for (int k = 0; k < rgba.Length; k += 64)
                            if (rgba[k] != 0) { any = true; break; }
                        if (any)
                        { idx = _tex.Count; _tex.Add(new Tex { W = w, H = h, Rgba = rgba }); break; }
                    }
                }
                catch (Exception ex)
                {
                    if (Environment.GetEnvironmentVariable("FFX_TEX_DBG") != null)
                        Console.WriteLine($"  tex fail tbp{t.Tbp0:x} cbp{t.Cbp:x} psm{t.Psm} {t.Width}x{t.Height}: {ex.Message}");
                }
            }
        }
        _fxTexMap[t] = idx;
        return idx;
    }

    /// <summary>Camera basis in world space (for billboard particles).</summary>
    public float[] CamRight = { 1, 0, 0 };
    public float[] CamUp = { 0, 1, 0 };

    void ComputeBounds()
    {
        var mn = new float[3] { float.MaxValue, float.MaxValue, float.MaxValue };
        var mx = new float[3] { float.MinValue, float.MinValue, float.MinValue };
        foreach (var (v, _, _, sky) in _draws)
        {
            if (sky) continue;
            for (int i = 0; i + 2 < v.Length; i += 13)
                for (int c = 0; c < 3; c++)
                {
                    if (v[i + c] < mn[c]) mn[c] = v[i + c];
                    if (v[i + c] > mx[c]) mx[c] = v[i + c];
                }
        }
        if (mn[0] > mx[0]) { mn = new float[3]; mx = new float[3] { 1, 1, 1 }; }
        BoundsMin = mn; BoundsMax = mx;
    }

    public static MapRenderer FromMap(MapModelSet set, Gs gs)
    {
        var r = new MapRenderer { _gs = gs, _srcTex = set.Textures };
        int? partOnly = int.TryParse(Environment.GetEnvironmentVariable("FFX_PART_ONLY"), out var po) ? po : null;
        foreach (var (tx, _) in set.Textures)
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
                    0 => gs.DecodePSMT32(tx.Tbp0, tx.Tbw, w, h),
                    36 => gs.DecodePSMT4HL(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa),
                    44 => gs.DecodePSMT4HH(tx.Tbp0, tx.Tbw, w, h, tx.Cbp, tx.Csa),
                    _ => Array.Empty<byte>(),
                };
            }
            catch { rgba = Array.Empty<byte>(); }
            // PSMT8 keyed by Tbp0; PSMT4 by (Tbp0,Cbp) — Cbp0 differs per
            // texture, so keying only by Tbp0 mismatches palettes on p4.
            r._tex.Add(rgba.Length == 0 ? null : new Tex { W = w, H = h, Rgba = rgba });
        }
        foreach (var m in set.Models)
        {
            if (partOnly.HasValue && m.PartIndex != partOnly.Value) continue;
            // node transform of owning part (identity on most maps)
            float[] mat = Mat4.Identity();
            var pi = m.PartIndex >= 0 ? set.Parts.FindIndex(p => p.Index == m.PartIndex) : -1;
            if (pi >= 0) mat = PartMatrix(set.Parts[pi]);
            foreach (var dc in m.Draws)
            {
                if (Environment.GetEnvironmentVariable("FFX_TEX_DBG") != null && partOnly != 122)
                    Console.Error.WriteLine($"part={m.PartIndex} tex={dc.TextureIndex} psm={dc.Gs.Tex0.Psm} tbp0={dc.Gs.Tex0.Tbp0} cbp={dc.Gs.Tex0.Cbp} csa={dc.Gs.Tex0.Csa} tcc={dc.Gs.Tex0.Tcc} tfx={dc.Gs.Tex0.Tfx}");
                if (pi >= 0)
                    r._partInfo[m.PartIndex] = set.Parts[pi];
                foreach (var run in dc.VertexRuns)
                {
                    var v = (float[])run.Clone();
                    if (pi >= 0) r._partDraws.Add((v, (float[])run.Clone(), m.PartIndex));
                    for (int i = 0; i + 12 < v.Length; i += 13)
                    {
                        if (pi >= 0)
                        {
                            float x = v[i], y = v[i + 1], z = v[i + 2];
                            v[i] = mat[0] * x + mat[4] * y + mat[8] * z + mat[12];
                            v[i + 1] = mat[1] * x + mat[5] * y + mat[9] * z + mat[13];
                            v[i + 2] = mat[2] * x + mat[6] * y + mat[10] * z + mat[14];
                        }
                        // st: s16/4096 already normalized UV (see bin.ts)
                        // — do NOT divide by tex dims again
                    }
                r._draws.Add((v, dc.TextureIndex, m.IsTranslucent || dc.IsTranslucent, dc.CullBackface));
                }
            }
        }
        r._draws.Sort((a, b) => a.trans.CompareTo(b.trans));
        r.ComputeBounds();
        return r;
    }

    /// <summary>Walkmesh overlay: green = passable, red = blocked. Walkmesh
    /// is authored in viewer units (file verts x10); divide by 10 to land on
    /// the raw file-unit map verts rendered here. Small y-bias avoids
    /// z-fighting the floor.</summary>
    public void AddWalkmesh(Walkmesh w, float yBias = 0.2f)
    {
        const float inv = 1f / 10f;
        foreach (var t in w.Tris)
        {
            bool ok = t.Passability == 0;
            var v = new float[39];
            for (int k = 0; k < 3; k++)
            {
                var (x, y, z) = w.VertPos(k == 0 ? t.V0 : k == 1 ? t.V1 : t.V2);
                v[13 * k] = x * inv; v[13 * k + 1] = y * inv + yBias; v[13 * k + 2] = z * inv;
                v[13 * k + 3] = ok ? 0.1f : 0.9f;
                v[13 * k + 4] = ok ? 0.9f : 0.1f;
                v[13 * k + 5] = 0.1f;
                v[13 * k + 6] = 0.38f; // translucent — walkable area reads over the map
            }
            _overlay.Add(v);
        }
    }

    /// <summary>Spawn markers: small octahedra (3 tris visible) at each
    /// EV01 mapPoint, cyan for entrypoints.</summary>
    public void AddMarkers(IEnumerable<(float x, float y, float z)> pts,
        float size = 0.3f, float r = 0.1f, float g = 0.9f, float b = 1f)
    {
        foreach (var (x, y, z) in pts)
        {
            // diamond: two tris facing opposite ways so visible from all sides
            var v = new float[13 * 6];
            float[][] c = {
                new[]{x, y + size, z}, new[]{x - size, y, z}, new[]{x + size, y, z},
                new[]{x, y - size, z}, new[]{x + size, y, z}, new[]{x - size, y, z},
            };
            for (int k = 0; k < 6; k++)
            {
                v[13 * k] = c[k][0]; v[13 * k + 1] = c[k][1]; v[13 * k + 2] = c[k][2];
                v[13 * k + 3] = r; v[13 * k + 4] = g; v[13 * k + 5] = b; v[13 * k + 6] = 1;
            }
            _overlay.Add(v);
        }
    }

    public int OverlayTris => _overlay.Sum(o => o.Length / 13 / 3);

    /// <summary>Build draws from an actor bin: parts -> bone world transform,
    /// MeshCall tris -> sequential 13-stride verts (white vcol, uv from the
    /// call). Textures come from the actor's own tex section.</summary>
    public static MapRenderer FromActor(ActorBin a, bool applyBones = true, int onlyPart = -1,
        float[]? boneState = null)
    {
        var r = new MapRenderer();
        r.AddActorInternal(a, applyBones ? boneState ?? ActorAnim.BindState(a) : null, null, onlyPart);
        r.Brightness = 1.1f;
        r.ComputeBounds();
        return r;
    }

    sealed class ActorDraw
    {
        public required int Inst;
        public required float[] V;
        public required int TexIndex;
        public required int Bone;
        public required float[][] PosPool;   // pristine (unskinned) pool
        public required float[][] SkinPool;  // current skinned pool
        public required int[] Resolved;
        public required int PartIndex;
        public required ActorBin.MeshCall Call;
    }
    readonly List<ActorDraw> _actorDraws = new();
    public sealed class ActorInstance
    {
        public required ActorBin A;
        public float[]? Place;
        public required float[] State;
        public bool ApplyBones = true;
        public float[] Center = new float[3]; // world-space bounds center
        public float Radius = 1;            // world-space bounds radius
    }
    readonly List<ActorInstance> _actorInst = new();
    public IReadOnlyList<ActorInstance> ActorInstances => _actorInst;

    /// <summary>Add an actor instance to the scene at an optional placement
    /// transform (column-major 4x4 applied after the bone transform).
    /// Returns the instance index for UpdateActorPose.</summary>
    public int AddActor(ActorBin a, float[]? place, float[]? state = null)
        => AddActorInternal(a, state ?? ActorAnim.BindState(a), place, -1);

    int AddActorInternal(ActorBin a, float[]? boneState, float[]? place, int onlyPart)
    {
        int inst = _actorInst.Count;
        _actorInst.Add(new ActorInstance
        { A = a, Place = place, State = boneState ?? ActorAnim.BindState(a), ApplyBones = boneState != null });
        var tex = a.Textures("a");
        int texBase = _tex.Count;
        foreach (var img in tex.Images)
            _tex.Add(new Tex { W = img.W, H = img.H, Rgba = img.Rgba });
        var bw = boneState != null ? a.BoneWorld(boneState) : a.BoneWorld();
        var allParts = a.Parts();
        bool hasSkin = a.SkinningCount > 0 && Environment.GetEnvironmentVariable("FFX_NO_SKIN") != "1";
        if (Environment.GetEnvironmentVariable("FFX_SKIN_DBG") == "1")
            Console.Error.WriteLine($"AddActorInternal: skinCount={a.SkinningCount} hasSkin={hasSkin} boneState={(boneState != null)} place={(place != null)}");
        int pidx = -1;
        foreach (var p in allParts)
        {
            pidx++;
            if (onlyPart >= 0 && pidx != onlyPart) continue;
            var (basePos, _) = a.VertexPool(p);
            // skinned parts get their own transformed pool (noclip applySkinning)
            var pos = hasSkin ? a.SkinnedPool(pidx, allParts, basePos, bw) : basePos;
            var bone = boneState != null && p.Bone >= 0 && p.Bone < bw.Count ? bw[p.Bone] : Mat4.Identity();
            var mat = place != null ? Mat4.Mul(place, bone) : bone;
            foreach (var call in a.DecodePart(p))
            {
                // resolve strip-repeat indices (0x8000|k) per call — same as ExportGltf
                var resolved = new List<int>();
                for (int i = 0; i < call.Verts.Count; i++)
                {
                    int raw = call.Verts[i].PosIdx;
                    if ((raw & 0x8000) != 0)
                    {
                        int k = raw & 0x7FFF, s = k / 3, src = resolved.Count - s;
                        resolved.Add(src >= 0 && src < resolved.Count ? resolved[src] : 0);
                    }
                    else resolved.Add(raw);
                }
                var v = new float[13 * call.Tris.Count];
                _actorDraws.Add(new ActorDraw
                {
                    Inst = inst, V = v, TexIndex = call.TexIndex + texBase, Bone = p.Bone,
                    PosPool = basePos, SkinPool = pos, Resolved = resolved.ToArray(),
                    PartIndex = pidx, Call = call,
                });
                WriteActorDraw(v, call, resolved, pos, mat);
                _draws.Add((v, call.TexIndex + texBase, false, false));
            }
        }
        // instance bounds for picking (world space, post-placement)
        {
            var mn = new float[3] { 1e30f, 1e30f, 1e30f };
            var mx = new float[3] { -1e30f, -1e30f, -1e30f };
            foreach (var d in _actorDraws)
            {
                if (d.Inst != inst) continue;
                for (int i = 0; i + 2 < d.V.Length; i += 13)
                    for (int c = 0; c < 3; c++)
                    { mn[c] = Math.Min(mn[c], d.V[i + c]); mx[c] = Math.Max(mx[c], d.V[i + c]); }
            }
            var inst2 = _actorInst[inst];
            if (mn[0] <= mx[0])
            {
                for (int c = 0; c < 3; c++) inst2.Center[c] = (mn[c] + mx[c]) / 2;
                float r = 0;
                for (int c = 0; c < 3; c++) r = Math.Max(r, (mx[c] - mn[c]) / 2);
                inst2.Radius = Math.Max(r, 0.1f);
            }
            else if (place != null)
            {
                inst2.Center = new[] { place[12], place[13], place[14] };
            }
        }
        ComputeBounds();
        return inst;
    }

    static void WriteActorDraw(float[] v, ActorBin.MeshCall call, IReadOnlyList<int> resolved,
        float[][] pos, float[] mat)
    {
        if (Environment.GetEnvironmentVariable("FFX_SKIN_DBG") == "1")
        {
            float bx = -1e30f;
            foreach (var q in pos) bx = Math.Max(bx, Math.Abs(q[0]));
            Console.Error.WriteLine($"WriteActorDraw: pool={pos.Length} maxAbsX={bx:F0}");
        }
        int o = 0;
        foreach (var vi in call.Tris)
        {
            var mv = call.Verts[vi];
            int pi = resolved[vi];
            var pp = pi >= 0 && pi < pos.Length ? pos[pi] : new float[3];
            float x = pp[0], y = pp[1], z = pp[2];
            v[o] = mat[0] * x + mat[4] * y + mat[8] * z + mat[12];
            v[o + 1] = mat[1] * x + mat[5] * y + mat[9] * z + mat[13];
            v[o + 2] = mat[2] * x + mat[6] * y + mat[10] * z + mat[14];
            // 0.5 = neutral under tex*vcol*2 modulation
            v[o + 3] = .5f; v[o + 4] = .5f; v[o + 5] = .5f; v[o + 6] = 1;
            v[o + 7] = mv.U; v[o + 8] = mv.V;
            o += 13;
        }
    }

    /// <summary>Re-pose actor draws in place from a new boneState — cheap
    /// enough for per-frame animation playback.</summary>
    public void UpdateActorPose(int inst, float[] state)
    {
        var ai = _actorInst[inst];
        ai.State = state;
        var bw = ai.A.BoneWorld(state);
        bool hasSkin = ai.A.SkinningCount > 0;
        var allParts = hasSkin ? ai.A.Parts() : null;
        // re-skin each part once (draws of the same part share the pool)
        var skinCache = new Dictionary<int, float[][]>();
        foreach (var d in _actorDraws)
        {
            if (d.Inst != inst) continue;
            var bone = d.Bone >= 0 && d.Bone < bw.Count ? bw[d.Bone] : Mat4.Identity();
            var mat = ai.Place != null ? Mat4.Mul(ai.Place, bone) : bone;
            if (hasSkin)
            {
                if (!skinCache.TryGetValue(d.PartIndex, out var sp))
                {
                    sp = ai.A.SkinnedPool(d.PartIndex, allParts!, d.PosPool, bw);
                    skinCache[d.PartIndex] = sp;
                }
                d.SkinPool = sp;
            }
            WriteActorDraw(d.V, d.Call, d.Resolved, d.SkinPool, mat);
        }
        ComputeBounds();
    }

    public void UpdateActorPose(ActorBin a, float[] state) => UpdateActorPose(0, state);

    /// <summary>T(pos)·Ry(yaw radians)·S(s) — actor placement transform.</summary>
    public static float[] PlacementM(float x, float y, float z, float yaw, float s = 1f)
        => Mat4.Placement(x, y, z, yaw, s);

    public float[] BoundsMin { get; private set; } = new float[3];
    public float[] BoundsMax { get; private set; } = new float[3];
    /// <summary>When set, Fit() frames this box instead of the draw bounds.</summary>
    public (float[] mn, float[] mx)? FitBounds;
    public int DrawCalls => _draws.Count + _fx.Count;
    public int TriCount => _draws.Sum(d => d.v.Length / 13 / 3)
        + _fx.Sum(f => f.v.Length / 13 / 3);
    /// <summary>Draws whose texture is absent (index out of range or decode
    /// failed) — they raster as flat gray boosted to white.</summary>
    public int NullTexDraws => _draws.Count(d => d.tex < 0 || d.tex >= _tex.Count || _tex[d.tex] == null);
    public int NoTexIndexDraws => _draws.Count(d => d.tex < 0);
    public int TexCount => _tex.Count;
    /// <summary>Raw map draws for headless color/bounds diagnostics.</summary>
    public IEnumerable<(float[] v, int tex, bool trans, bool cull)> DebugDraws() => _draws;
    /// <summary>Particle FX layer (pos+cor+uv verts, tex index, GS blend).</summary>
    public IEnumerable<(float[] v, int tex, int blend, bool noDepth)> DebugFx() => _fx;
    /// <summary>Editor overlay verts (walkmesh, spawn markers).</summary>
    public IEnumerable<float[]> DebugOverlay() => _overlay;
    /// <summary>Decoded texture by index (GPU backend uploads these).</summary>
    public Tex? TexAt(int i) => i >= 0 && i < _tex.Count ? _tex[i] : null;
    /// <summary>Fraction of pixels that are the PS2 CLUT pad green (0,255,0)
    /// or near it — flags textures sampling the wrong palette window.</summary>
    public (int idx, float frac)[] DebugTexGreen()
    {
        var outp = new List<(int, float)>();
        for (int i = 0; i < _tex.Count; i++)
        {
            var t = _tex[i]; if (t == null) continue;
            int n = t.Rgba.Length / 4, gn = 0;
            for (int p = 0; p < n; p++)
                if (t.Rgba[4 * p + 1] > 200 && t.Rgba[4 * p] < 80 && t.Rgba[4 * p + 2] < 80) gn++;
            if (gn > n / 20) outp.Add((i, (float)gn / n));
        }
        return outp.OrderByDescending(t => t.Item2).ToArray();
    }
    /// <summary>Average RGB of a texture for green-screen diagnostics.</summary>
    public int[] DebugTexAvg(int i)
    {
        var t = i >= 0 && i < _tex.Count ? _tex[i] : null;
        if (t == null || t.Rgba.Length == 0) return new[] { -1, -1, -1 };
        long r = 0, g = 0, b = 0; int n = t.Rgba.Length / 4;
        for (int p = 0; p < n; p++) { r += t.Rgba[4 * p]; g += t.Rgba[4 * p + 1]; b += t.Rgba[4 * p + 2]; }
        return new[] { (int)(r / n), (int)(g / n), (int)(b / n) };
    }
    public int NullTexSlots => _tex.Count(t => t == null);

    /// <summary>Dump each texture slot of the current renderer state to PNG
    /// for palette/UV debugging (uses the resolved texture list order).</summary>
    public void DumpTextures(string dir)
    {
        Directory.CreateDirectory(dir);
        var argb = new uint[1];
        for (int i = 0; i < _tex.Count; i++)
        {
            var t = _tex[i];
            if (t == null) { File.WriteAllBytes(Path.Combine(dir, $"tex{i:D3}.null"), Array.Empty<byte>()); continue; }
            var buf = new uint[t.W * t.H];
            for (int p = 0; p < buf.Length && 4 * p + 3 < t.Rgba.Length; p++)
                buf[p] = (uint)(t.Rgba[4 * p] | t.Rgba[4 * p + 1] << 8 | t.Rgba[4 * p + 2] << 16 | 0xFF000000);
            FfxMap1.Png.Encode(Path.Combine(dir, $"tex{i:D3}.png"), buf, t.W, t.H);
        }
    }

    // orbit camera state
    public double Yaw = 0.6, Pitch = 0.35, Dist = 40;
    public double Tx, Ty, Tz;   // look-at target

    /// <summary>Camera forward (unit, world space): eye -> target.</summary>
    public float[] FwdVec()
    {
        double cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        double cy = Math.Cos(Yaw), sy = Math.Sin(Yaw);
        return new[] { (float)(-cp * sy), (float)(-sp), (float)(-cp * cy) };
    }

    /// <summary>Camera eye position in world space.</summary>
    public float[] EyePos()
    {
        double cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        double cy = Math.Cos(Yaw), sy = Math.Sin(Yaw);
        return new[] { (float)(Tx + Dist * cp * sy), (float)(Ty + Dist * sp), (float)(Tz + Dist * cp * cy) };
    }
    public float Brightness = 2.4f;   // editor preview boost (map is legit dark)
    public bool ShowOverlay = true;   // walkmesh/spawn overlay layer

    /// <summary>Fit unless an explicit camera was set via LookFrom.</summary>
    public void Fit() { if (!CameraSet) FitTo(FitBounds?.mn ?? BoundsMin, FitBounds?.mx ?? BoundsMax); }

    /// <summary>When true, Blit/viewport skip Fit() and keep the explicit
    /// camera set by LookFrom.</summary>
    public bool CameraSet;

    /// <summary>Place the camera at eye looking at target (battle view:
    /// party spot -> monsters). Orbit params are derived from the vector.</summary>
    public void LookFrom(double ex, double ey, double ez, double tx, double ty, double tz)
    {
        double dx = ex - tx, dy = ey - ty, dz = ez - tz;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d < 1e-6) { Fit(); return; }
        CameraSet = true;
        Tx = tx; Ty = ty; Tz = tz;
        Dist = d;
        Pitch = Math.Asin(Math.Max(-1, Math.Min(1, dy / d)));
        Yaw = Math.Atan2(dx, dz);
    }

    /// <summary>Frame the camera on an explicit bounds box — e.g. the
    /// walkmesh extent when a skydome would dominate the full bounds.</summary>
    public void FitTo(float[] mn, float[] mx)
    {
        CameraSet = true; // explicit bounds fit — Blit must not refit over it
        Tx = (mn[0] + mx[0]) / 2;
        Ty = (mn[1] + mx[1]) / 2;
        Tz = (mn[2] + mx[2]) / 2;
        double r = 0;
        for (int c = 0; c < 3; c++) r = Math.Max(r, (mx[c] - mn[c]) / 2);
        Dist = Math.Max(r * 2.6, 1);
    }

    /// <summary>Project a world point to screen (same orbit camera as Render).
    /// Returns null when behind the camera.</summary>
    public (float sx, float sy)? ProjectWorld(float wx, float wy, float wz, int W, int H)
    {
        double cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        double cy = Math.Cos(Yaw), sy = Math.Sin(Yaw);
        double ex = Tx + Dist * cp * sy, ey = Ty + Dist * sp, ez = Tz + Dist * cp * cy;
        double fx = Tx - ex, fy = Ty - ey, fz = Tz - ez;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz); fx /= fl; fy /= fl; fz /= fl;
        double rx = fz, rz = -fx, ry = 0;
        double rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-9) return null;
        rx /= rl; rz /= rl;
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        double dx = wx - ex, dy = wy - ey, dz = wz - ez;
        double cx = dx * rx + dy * ry + dz * rz;
        double cyy = dx * ux + dy * uy + dz * uz;
        double cz = dx * fx + dy * fy + dz * fz;
        if (cz < 1e-3) return null;
        double f = 1.0 / Math.Tan(Math.PI / 8);
        return ((float)(cx * f / cz * W / 2 + W / 2), (float)(-cyy * f / cz * W / 2 + H / 2));
    }

    /// <summary>Inverse of ProjectWorld: pixel -> world-space ray (origin at
    /// the camera eye). dir is normalized.</summary>
    public (double ex, double ey, double ez, double dx, double dy, double dz)
        UnprojectRay(double sx, double sy, int W, int H)
    {
        double cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        double cy = Math.Cos(Yaw), sy2 = Math.Sin(Yaw);
        double ex = Tx + Dist * cp * sy2, ey = Ty + Dist * sp, ez = Tz + Dist * cp * cy;
        double fx = Tx - ex, fy = Ty - ey, fz = Tz - ez;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz); fx /= fl; fy /= fl; fz /= fl;
        double rx = fz, ry = 0, rz = -fx;
        double rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-9) { rx = 1; rz = 0; rl = 1; }
        rx /= rl; rz /= rl;
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        double f = 1.0 / Math.Tan(Math.PI / 8);
        double dx = (sx - W / 2) / (f * W / 2), dy = -(sy - H / 2) / (f * W / 2);
        double wx = rx * dx + ux * dy + fx, wy = ry * dx + uy * dy + fy, wz = rz * dx + uz * dy + fz;
        double wl = Math.Sqrt(wx * wx + wy * wy + wz * wz);
        return (ex, ey, ez, wx / wl, wy / wl, wz / wl);
    }

    /// <summary>Ray/plane hit on the horizontal plane y=planeY. Returns the
    /// world point or null when the ray is parallel/points away.</summary>
    public (float x, float y, float z)? RayFloor(double sx, double sy, int W, int H, float planeY)
    {
        var (ex, ey, ez, dx, dy, dz) = UnprojectRay(sx, sy, W, H);
        if (Math.Abs(dy) < 1e-6) return null;
        double t = (planeY - ey) / dy;
        if (t <= 0) return null;
        return ((float)(ex + dx * t), (float)(ey + dy * t), (float)(ez + dz * t));
    }

    /// <summary>Live-move an actor instance: translate its placement (keeps
    /// yaw/scale), rewrite draws and update the pick bounds.</summary>
    public void MoveActorTo(int inst, float wx, float wy, float wz)
    {
        var ai = _actorInst[inst];
        if (ai.Place == null) return;
        ai.Place[12] = wx; ai.Place[13] = wy; ai.Place[14] = wz;
        var bw = ai.A.BoneWorld(ai.State);
        bool hasSkin = ai.A.SkinningCount > 0;
        var allParts = hasSkin ? ai.A.Parts() : null;
        var skinCache = new Dictionary<int, float[][]>();
        var mn = new float[3] { 1e30f, 1e30f, 1e30f };
        var mx = new float[3] { -1e30f, -1e30f, -1e30f };
        foreach (var d in _actorDraws)
        {
            if (d.Inst != inst) continue;
            var bone = d.Bone >= 0 && d.Bone < bw.Count ? bw[d.Bone] : Mat4.Identity();
            var mat = Mat4.Mul(ai.Place, bone);
            if (hasSkin)
            {
                if (!skinCache.TryGetValue(d.PartIndex, out var sp))
                {
                    sp = ai.A.SkinnedPool(d.PartIndex, allParts!, d.PosPool, bw);
                    skinCache[d.PartIndex] = sp;
                }
                d.SkinPool = sp;
            }
            WriteActorDraw(d.V, d.Call, d.Resolved, d.SkinPool, mat);
            for (int i = 0; i + 2 < d.V.Length; i += 13)
                for (int c = 0; c < 3; c++)
                { mn[c] = Math.Min(mn[c], d.V[i + c]); mx[c] = Math.Max(mx[c], d.V[i + c]); }
        }
        if (mn[0] <= mx[0])
        {
            for (int c = 0; c < 3; c++) ai.Center[c] = (mn[c] + mx[c]) / 2;
            float r = 0;
            for (int c = 0; c < 3; c++) r = Math.Max(r, (mx[c] - mn[c]) / 2);
            ai.Radius = Math.Max(r, 0.1f);
        }
        ComputeBounds();
    }

    /// <summary>Actor instance nearest to a screen point (by projected
    /// placement origin). Returns instance index or -1.</summary>
    public int PickActor(double sx, double sy, int W, int H)
    {
        int best = -1; double bestD = 1e30;
        for (int i = 0; i < _actorInst.Count; i++)
        {
            var ai = _actorInst[i];
            var pr = ProjectWorld(ai.Center[0], ai.Center[1], ai.Center[2], W, H);
            if (pr == null) continue;
            // pick radius: projected world radius, clamped 24..200px
            var pr2 = ProjectWorld(ai.Center[0] + ai.Radius, ai.Center[1], ai.Center[2], W, H);
            double rad = 48;
            if (pr2 != null)
                rad = Math.Clamp(Math.Abs(pr2.Value.sx - pr.Value.sx), 24, 200);
            double d = Math.Sqrt((pr.Value.sx - sx) * (pr.Value.sx - sx) + (pr.Value.sy - sy) * (pr.Value.sy - sy));
            if (d < rad && d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // render into bgra buffer; returns tris drawn. The raster is the hot
    // path — split the frame into horizontal bands and render each on a
    // worker thread. Writes stay disjoint (band y-range), and every band
    // replays the same draw sequence, so blended results match a serial
    // pass exactly.
    public unsafe int Render(byte* bgra, int W, int H, float* zbuf)
    {
        int bands = Math.Min(Environment.ProcessorCount, Math.Max(1, H / 32));
        if (bands <= 1) return RenderBand(bgra, W, H, zbuf, 0, H);
        var counts = new int[bands];
        Parallel.For(0, bands, bi =>
        {
            int y0 = bi * H / bands, y1 = (bi + 1) * H / bands;
            counts[bi] = RenderBand(bgra, W, H, zbuf, y0, y1);
        });
        int tot = 0; foreach (var c in counts) tot += c;
        return tot;
    }

    unsafe int RenderBand(byte* bgra, int W, int H, float* zbuf, int bandY0, int bandY1)
    {
        // camera basis (Y-up orbit)
        double cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        double cy = Math.Cos(Yaw), sy = Math.Sin(Yaw);
        // eye = target + dist*(cp*sy, sp, cp*cy)
        double ex = Tx + Dist * cp * sy, ey = Ty + Dist * sp, ez = Tz + Dist * cp * cy;
        // fwd = normalize(target-eye)
        double fx = Tx - ex, fy = Ty - ey, fz = Tz - ez;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz); fx /= fl; fy /= fl; fz /= fl;
        // right = fwd x up(0,1,0)
        double rx = fz * 1 - 0, ry = 0 - fx * 0 - fz * 0 + 0, rz = 0 - fy * 0 - 0 + fx * 0;
        rx = fz; ry = 0; rz = -fx;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-9) { rx = 1; ry = 0; rz = 0; rl = 1; }
        rx /= rl; ry /= rl; rz /= rl;
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        CamRight[0] = (float)rx; CamRight[1] = (float)ry; CamRight[2] = (float)rz;
        CamUp[0] = (float)ux; CamUp[1] = (float)uy; CamUp[2] = (float)uz;
        double f = 1.0 / Math.Tan(Math.PI / 8); // 45deg fov
        int drawn = 0;
        for (int i = bandY0 * W; i < bandY1 * W; i++) zbuf[i] = float.MaxValue;

        void DrawSpan(float[] v, Tex? tex, bool cull, int blend = 0, bool clampUv = false, bool noDepth = false)
        {
            int nv = v.Length / 13;
            for (int t = 0; t + 2 < nv; t += 3)
            {
                Span<float> xs = stackalloc float[3], ys = stackalloc float[3], zs = stackalloc float[3];
                Span<float> us = stackalloc float[3], vs = stackalloc float[3];
                Span<float> cr = stackalloc float[3], cg = stackalloc float[3], cb = stackalloc float[3], ca = stackalloc float[3];
                for (int k = 0; k < 3; k++)
                {
                    int o = 13 * (t + k);
                    double dx = v[o] - ex, dy = v[o + 1] - ey, dz = v[o + 2] - ez;
                    double cx = dx * rx + dy * ry + dz * rz;
                    double cyy = dx * ux + dy * uy + dz * uz;
                    double cz = dx * fx + dy * fy + dz * fz;
                    xs[k] = (float)(cx * f / Math.Max(cz, 1e-4) * W / 2 + W / 2);
                    ys[k] = (float)(-cyy * f / Math.Max(cz, 1e-4) * W / 2 + H / 2);
                    zs[k] = (float)cz;
                    us[k] = v[o + 7]; vs[k] = v[o + 8];
                    cr[k] = v[o + 3]; cg[k] = v[o + 4]; cb[k] = v[o + 5]; ca[k] = v[o + 6];
                }
                drawn += Raster(bgra, zbuf, W, H, xs, ys, zs, us, vs, cr, cg, cb, ca, tex, Brightness, cull, blend, bandY0, bandY1, clampUv, noDepth);
            }
        }

        bool noTrans = Environment.GetEnvironmentVariable("FFX_NO_TRANS") == "1";
        bool onlyFx = Environment.GetEnvironmentVariable("FFX_ONLY_FX") == "1";
        if (!onlyFx)
        {
            // opaque first, then translucent alpha-blended (GS-style: trans
            // geometry z-tests but does not z-write)
            foreach (var (v, ti, trans, cull) in _draws)
            {
                if (trans) continue;
                DrawSpan(v, ti >= 0 && ti < _tex.Count ? _tex[ti] : null, cull);
            }
            if (!noTrans)
                foreach (var (v, ti, trans, cull) in _draws)
                {
                    if (!trans) continue;
                    DrawSpan(v, ti >= 0 && ti < _tex.Count ? _tex[ti] : null, cull, blend: 1);
                }
        }
        // FX blend semantics (render.ts translateBlendMode):
        //   0x00 replace | 0x42 dst-src*a | 0x44/0x04 alpha | 0x46 dst*(1-a)
        //   0x48 additive | 0x88 src*a replace — all z-tested, never z-written.
        //   noclip samples flipbook sprites with CLAMP wrap and submits every
        //   draw to the TRANSLUCENT+PARTICLES layer sorted back-to-front.
        var fxSorted = _fx
            .Select(fxd => (fxd, z: FxDepth(fxd.v, ex, ey, ez)))
            .OrderByDescending(fxd => fxd.z);
        foreach (var (fxd, _) in fxSorted)
        {
            int mode = fxd.blend switch
            {
                0x00 => 5, 0x42 => 3, 0x44 or 0x04 => 1,
                0x46 => 4, 0x88 => 6, _ => 2, // 0x48 and unknown -> additive
            };
            DrawSpan(fxd.v, fxd.tex >= 0 && fxd.tex < _tex.Count ? _tex[fxd.tex] : null, false, blend: mode, clampUv: true, noDepth: fxd.noDepth);
        }
        if (ShowOverlay)
            foreach (var v in _overlay)
                DrawSpan(v, null, false, blend: 1); // alpha — see map through the walkmesh
        return drawn;
    }

    /// <summary>Squared distance of a draw's first vertex from the eye —
    /// noclip sorts on the model-matrix translation (one point).</summary>
    static double FxDepth(float[] v, double ex, double ey, double ez)
    {
        double dx = v[0] - ex, dy = v[1] - ey, dz = v[2] - ez;
        return dx * dx + dy * dy + dz * dz;
    }

    static unsafe int Raster(byte* bgra, float* zbuf, int W, int H,
        Span<float> xs, Span<float> ys, Span<float> zs,
        Span<float> us, Span<float> vs,
        Span<float> cr, Span<float> cg, Span<float> cb, Span<float> ca, Tex? tex,
        float boost, bool cull, int blend = 0, int bandY0 = 0, int bandY1 = int.MaxValue,
        bool clampUv = false, bool noDepth = false)
    {
        float x0 = MathF.Min(xs[0], MathF.Min(xs[1], xs[2]));
        float x1 = MathF.Max(xs[0], MathF.Max(xs[1], xs[2]));
        float y0 = MathF.Min(ys[0], MathF.Min(ys[1], ys[2]));
        float y1 = MathF.Max(ys[0], MathF.Max(ys[1], ys[2]));
        int ix0 = Math.Max(0, (int)MathF.Floor(x0)), ix1 = Math.Min(W - 1, (int)MathF.Ceiling(x1));
        int iy0 = Math.Max(bandY0, Math.Max(0, (int)MathF.Floor(y0))),
            iy1 = Math.Min(bandY1 - 1, Math.Min(H - 1, (int)MathF.Ceiling(y1)));
        if (ix1 < 0 || iy1 < 0 || ix0 >= W || iy0 >= H) return 0;
        float d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0]);
        if (MathF.Abs(d) < 1e-9f) return 0;
        // GS backface culling: CCW tris are front-facing on screen (y down);
        // d < 0 means we are looking at the back of the triangle.
        if (cull && d < 0) return 0;
        int drawn = 0;
        for (int y = iy0; y <= iy1; y++)
        for (int x = ix0; x <= ix1; x++)
        {
            float px = x + .5f, py = y + .5f;
            float w0 = ((xs[1] - px) * (ys[2] - py) - (xs[2] - px) * (ys[1] - py)) / d;
            float w1 = ((xs[2] - px) * (ys[0] - py) - (xs[0] - px) * (ys[2] - py)) / d;
            float w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            float z = w0 * zs[0] + w1 * zs[1] + w2 * zs[2];
            if (z <= 0) continue;
            int pi = y * W + x;
            if (!noDepth && z >= zbuf[pi]) continue; // glares skip the z test
            float u = w0 * us[0] + w1 * us[1] + w2 * us[2];
            float vv = w0 * vs[0] + w1 * vs[1] + w2 * vs[2];
            int r, g, b, a;
            float ar = w0 * cr[0] + w1 * cr[1] + w2 * cr[2];
            float ag = w0 * cg[0] + w1 * cg[1] + w2 * cg[2];
            float ab = w0 * cb[0] + w1 * cb[1] + w2 * cb[2];
            float aa = w0 * ca[0] + w1 * ca[1] + w2 * ca[2];
            if (tex != null)
            {
                // bilinear sample — map textures repeat, FX sprites clamp
                // (noclip: flipbook sampler is Clamp/Clamp)
                float fx = u * tex.W - 0.5f, fy = vv * tex.H - 0.5f;
                int x0t = (int)MathF.Floor(fx); int y0t = (int)MathF.Floor(fy);
                float tx = fx - x0t, ty = fy - y0t;
                int tu0, tu1, tv0, tv1;
                if (clampUv)
                {
                    tu0 = Math.Clamp(x0t, 0, tex.W - 1); tu1 = Math.Clamp(x0t + 1, 0, tex.W - 1);
                    tv0 = Math.Clamp(y0t, 0, tex.H - 1); tv1 = Math.Clamp(y0t + 1, 0, tex.H - 1);
                }
                else
                {
                    tu0 = ((x0t % tex.W) + tex.W) % tex.W; tu1 = (tu0 + 1) % tex.W;
                    tv0 = ((y0t % tex.H) + tex.H) % tex.H; tv1 = (tv0 + 1) % tex.H;
                }
                int q00 = 4 * (tv0 * tex.W + tu0), q10 = 4 * (tv0 * tex.W + tu1);
                int q01 = 4 * (tv1 * tex.W + tu0), q11 = 4 * (tv1 * tex.W + tu1);
                int Sample(int q, int ch) => tex.Rgba[q + ch];
                r = (int)((Sample(q00,0)*(1-tx)+Sample(q10,0)*tx)*(1-ty) + (Sample(q01,0)*(1-tx)+Sample(q11,0)*tx)*ty);
                g = (int)((Sample(q00,1)*(1-tx)+Sample(q10,1)*tx)*(1-ty) + (Sample(q01,1)*(1-tx)+Sample(q11,1)*tx)*ty);
                b = (int)((Sample(q00,2)*(1-tx)+Sample(q10,2)*tx)*(1-ty) + (Sample(q01,2)*(1-tx)+Sample(q11,2)*tx)*ty);
                a = (int)((Sample(q00,3)*(1-tx)+Sample(q10,3)*tx)*(1-ty) + (Sample(q01,3)*(1-tx)+Sample(q11,3)*tx)*ty);
                // GS modulation: tex * vcol / 128 — vcol 0x80 (ar=1.0) is
                // neutral; an extra *2 saturates bright arenas to white.
                // Src-alpha modes (2 additive, 3 subtract, 4 darken, 6 src*a
                // replace) multiply tex by vertex alpha; 0/5 are opaque.
                float sa = Math.Clamp(aa, 0f, 1f);
                float aa2 = blend is 2 or 3 or 4 or 6 ? sa : 1f;
                r = Math.Min(255, r * (int)(ar * aa2 * 256) >> 8);
                g = Math.Min(255, g * (int)(ag * aa2 * 256) >> 8);
                b = Math.Min(255, b * (int)(ab * aa2 * 256) >> 8);
                a = (int)(a * aa2);
                if ((blend == 0 || blend == 5) && a < 128) continue; // opaque alpha test
                if (blend == 1 && a < 8) continue;                   // fully transparent
            }
            else
            {
                float sa3 = blend is 2 or 3 or 4 or 6 ? Math.Clamp(aa, 0f, 1f) : 1f;
                r = Math.Min(255, (int)(ar * sa3 * 255));
                g = Math.Min(255, (int)(ag * sa3 * 255));
                b = Math.Min(255, (int)(ab * sa3 * 255));
                a = 255;
                if (r + g + b == 0) { r = g = b = 90; }  // no color -> gray
            }
            if (boost != 1f)
            {
                r = Math.Min(255, (int)(r * boost));
                g = Math.Min(255, (int)(g * boost));
                b = Math.Min(255, (int)(b * boost));
            }
            // GS blend factors (render.ts translateBlendMode): src alpha
            // unit is 128 = 1.0. For src-alpha modes `a` already carries
            // the vertex-alpha modulation applied above (aa2).
            float srcA = Math.Min(1f, a / 128f);
            if (blend == 2)
            {
                // 0x48 additive: dst += src * srcA, no z write
                int sb = (int)(b * srcA), sg = (int)(g * srcA), sr = (int)(r * srcA);
                bgra[4 * pi] = (byte)Math.Min(255, bgra[4 * pi] + sb);
                bgra[4 * pi + 1] = (byte)Math.Min(255, bgra[4 * pi + 1] + sg);
                bgra[4 * pi + 2] = (byte)Math.Min(255, bgra[4 * pi + 2] + sr);
            }
            else if (blend == 3)
            {
                // 0x42 reverse-subtract: dst -= src * srcA
                int sb = (int)(b * srcA), sg = (int)(g * srcA), sr = (int)(r * srcA);
                bgra[4 * pi] = (byte)Math.Max(0, bgra[4 * pi] - sb);
                bgra[4 * pi + 1] = (byte)Math.Max(0, bgra[4 * pi + 1] - sg);
                bgra[4 * pi + 2] = (byte)Math.Max(0, bgra[4 * pi + 2] - sr);
            }
            else if (blend == 4)
            {
                // 0x46 magic-only: dst * (1 - srcA) — pure darkening pass
                float keep = 1f - srcA;
                bgra[4 * pi] = (byte)(bgra[4 * pi] * keep);
                bgra[4 * pi + 1] = (byte)(bgra[4 * pi + 1] * keep);
                bgra[4 * pi + 2] = (byte)(bgra[4 * pi + 2] * keep);
            }
            else if (blend == 5)
            {
                // 0x00 replace — no accumulation
                zbuf[pi] = z;
                bgra[4 * pi] = (byte)b; bgra[4 * pi + 1] = (byte)g;
                bgra[4 * pi + 2] = (byte)r; bgra[4 * pi + 3] = 255;
            }
            else if (blend == 6)
            {
                // 0x88 src*a replace: writes src*srcA (no accumulation)
                bgra[4 * pi] = (byte)(b * srcA); bgra[4 * pi + 1] = (byte)(g * srcA);
                bgra[4 * pi + 2] = (byte)(r * srcA); bgra[4 * pi + 3] = 255;
            }
            else if (blend == 1)
            {
                // 0x44 alpha: src*fa + dst*(1-fa), fa = srcA * vertex alpha;
                // z-tested above, no z write
                float fa = srcA * Math.Clamp(aa, 0f, 1f);
                bgra[4 * pi] = (byte)Math.Min(255, b * fa + bgra[4 * pi] * (1 - fa));
                bgra[4 * pi + 1] = (byte)Math.Min(255, g * fa + bgra[4 * pi + 1] * (1 - fa));
                bgra[4 * pi + 2] = (byte)Math.Min(255, r * fa + bgra[4 * pi + 2] * (1 - fa));
            }
            else
            {
                zbuf[pi] = z;
                bgra[4 * pi] = (byte)b; bgra[4 * pi + 1] = (byte)g;
                bgra[4 * pi + 2] = (byte)r; bgra[4 * pi + 3] = 255;
            }
            drawn++;
        }
        return drawn;
    }

    static float[] PartMatrix(LevelParts.PartInfo p)
    {
        // TRS: euler centidegrees XYZ (order0) or ZYX (order5) + translation
        var m = Mat4.FromEuler(p.Ex, p.Ey, p.Ez, p.EulerOrder);
        m[12] = p.Px; m[13] = p.Py; m[14] = p.Pz;
        return m;
    }

    static class Mat4
    {
        public static float[] Identity() => new float[16]
        { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static float[] FromEuler(float ex, float ey, float ez, int order)
        {
            float R(float a) => a * (float)Math.PI / 18000f;
            float x = R(ex), y = R(ey), z = R(ez);
            float cx = MathF.Cos(x), sx = MathF.Sin(x);
            float cy = MathF.Cos(y), sy = MathF.Sin(y);
            float cz = MathF.Cos(z), sz = MathF.Sin(z);
            // column-major m = Rz*Ry*Rx (XYZ intrinsic ~ order5 in bundle usage)
            var rx = new float[16] { 1, 0, 0, 0, 0, cx, sx, 0, 0, -sx, cx, 0, 0, 0, 0, 1 };
            var ry = new float[16] { cy, 0, -sy, 0, 0, 1, 0, 0, sy, 0, cy, 0, 0, 0, 0, 1 };
            var rz = new float[16] { cz, sz, 0, 0, -sz, cz, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            var m = order == 5 ? Mul(Mul(rz, ry), rx) : Mul(Mul(rx, ry), rz);
            return m;
        }
        /// <summary>Same as FromEuler but angles already in radians —
        /// EFFECT keyframe data is radians (ROTATION key -6.28 ≈ -2π).</summary>
        public static float[] FromEulerRad(float ex, float ey, float ez, int order)
            => FromEulerScaled(ex, ey, ez, order, 1f);

        static float[] FromEulerScaled(float ex, float ey, float ez, int order, float k)
        {
            var m = FromEulerRaw(ex * k, ey * k, ez * k, order);
            return m;
        }

        static float[] FromEulerRaw(float x, float y, float z, int order)
        {
            float cx = MathF.Cos(x), sx = MathF.Sin(x);
            float cy = MathF.Cos(y), sy = MathF.Sin(y);
            float cz = MathF.Cos(z), sz = MathF.Sin(z);
            var rx = new float[16] { 1, 0, 0, 0, 0, cx, sx, 0, 0, -sx, cx, 0, 0, 0, 0, 1 };
            var ry = new float[16] { cy, 0, -sy, 0, 0, 1, 0, 0, sy, 0, cy, 0, 0, 0, 0, 1 };
            var rz = new float[16] { cz, sz, 0, 0, -sz, cz, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            return order == 5 ? Mul(Mul(rz, ry), rx) : Mul(Mul(rx, ry), rz);
        }

        /// <summary>T(pos)·Ry(yaw radians)·S(s) — actor placement transform.
        /// Viewer modelMatrix = totalScale·Ry(-heading-π/2)+pos.</summary>
        public static float[] Placement(float x, float y, float z, float yaw, float s = 1f)
        {
            float c = MathF.Cos(yaw) * s, sn = MathF.Sin(yaw) * s;
            return new float[16] { c, 0, -sn, 0, 0, s, 0, 0, sn, 0, c, 0, x, y, z, 1 };
        }

        public static float[] Mul(float[] a, float[] b)
        {
            var o = new float[16];
            for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                o[4 * c + r] = a[r] * b[4 * c] + a[4 + r] * b[4 * c + 1]
                             + a[8 + r] * b[4 * c + 2] + a[12 + r] * b[4 * c + 3];
            return o;
        }
    }
}
