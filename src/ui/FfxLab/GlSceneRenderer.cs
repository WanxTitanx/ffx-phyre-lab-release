// GPU raster backend — draws the SAME scene state a MapRenderer rasterizes in
// software, but on the GPU via the shared Avalonia OpenGL context. Simulation,
// particle stepping and GS texture decode stay on the CPU; only rasterization
// moves to GL. Layout matches the software vertex: float[13] = pos(3) col(4)
// uv(2) pad(4).
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.OpenGL;

namespace FfxLab;

sealed class GlSceneRenderer : IDisposable
{
    GlFuncs _f = null!;
    int _prog;
    int _uView, _uProj, _uTex, _uUseTex, _uBoost;
    int _vao;
    bool _gles;

    // static map geometry: one VBO per (tex, blend-class) group, uploaded once
    sealed class Batch { public int Vbo, Tex, Count; public int Mode; public bool Cull; }
    readonly List<Batch> _opaque = new(), _trans = new();
    int _overlayVbo; int _overlayCount;
    // fx layer rebuilt every frame into a big dynamic VBO (draw ranges per item)
    int _fxVbo; readonly List<(int first, int count, int tex, int mode)> _fxRanges = new();
    readonly Dictionary<long, int> _glTex = new();  // (_tex index, clamp) -> GL texture id
    int _whiteTex;
    bool _staticDirty = true;
    int _lastGeomVersion = -1;
    MapRenderer? _src;

    const int STRIDE = 13 * 4;

    static string Vs(bool gles) => (gles ? "#version 100\nprecision mediump float;\n" : "#version 120\n") + @"
attribute vec3 aPos; attribute vec4 aCol; attribute vec2 aUv;
uniform mat4 uView, uProj;
varying vec4 vCol; varying vec2 vUv;
void main() { gl_Position = uProj * uView * vec4(aPos, 1.0); vCol = aCol; vUv = aUv; }";
    static string Fs(bool gles) => (gles ? "#version 100\nprecision mediump float;\n" : "#version 120\n") + @"
varying vec4 vCol; varying vec2 vUv;
uniform sampler2D uTex; uniform float uUseTex; uniform float uBoost;
void main() {
    vec4 c = uUseTex > 0.5 ? texture2D(uTex, vUv) : vec4(1.0);
    gl_FragColor = vec4(c.rgb * vCol.rgb * uBoost, c.a * vCol.a);
}";

    public string InitError = "";

    public bool Init(GlInterface gl)
    {
        try
        {
            _f = GlFuncs.Load(gl);
            _gles = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
            int vs = gl.CreateShader(GL.VERTEX_SHADER);
            string? e = gl.CompileShaderAndGetError(vs, Vs(_gles));
            if (e != null) { InitError = "vs: " + e; return false; }
            int fs = gl.CreateShader(GL.FRAGMENT_SHADER);
            e = gl.CompileShaderAndGetError(fs, Fs(_gles));
            if (e != null) { InitError = "fs: " + e; return false; }
            _prog = gl.CreateProgram();
            gl.AttachShader(_prog, vs); gl.AttachShader(_prog, fs);
            gl.BindAttribLocationString(_prog, 0, "aPos");
            gl.BindAttribLocationString(_prog, 1, "aCol");
            gl.BindAttribLocationString(_prog, 2, "aUv");
            e = gl.LinkProgramAndGetError(_prog);
            if (e != null) { InitError = "link: " + e; return false; }
            _uView = gl.GetUniformLocationString(_prog, "uView");
            _uProj = gl.GetUniformLocationString(_prog, "uProj");
            _uTex = gl.GetUniformLocationString(_prog, "uTex");
            _uUseTex = gl.GetUniformLocationString(_prog, "uUseTex");
            _uBoost = gl.GetUniformLocationString(_prog, "uBoost");
            _vao = gl.GenVertexArray();
            _fxVbo = gl.GenBuffer();
            _overlayVbo = gl.GenBuffer();
            // 1x1 white texture for untextured draws
            _whiteTex = gl.GenTexture();
            gl.BindTexture(GL.TEXTURE_2D, _whiteTex);
            var px = new byte[] { 255, 255, 255, 255 };
            unsafe { fixed (byte* p = px) gl.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA, 1, 1, 0, GL.RGBA, GL.UNSIGNED_BYTE, (IntPtr)p); }
            gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.NEAREST);
            gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.NEAREST);
            return true;
        }
        catch (Exception ex) { InitError = ex.Message; return false; }
    }

    /// <summary>Bind the scene source. Re-uploads static VBOs/textures.</summary>
    public void SetScene(MapRenderer r) { _src = r; _staticDirty = true; }

    // noclip: level textures wrap, flipbook FX sprites clamp — a UV outside
    // [0,1] extends the edge texel instead of tiling the sprite
    int UploadTexture(GlInterface gl, int i, bool clamp)
    {
        long key = ((long)i << 1) | (clamp ? 1L : 0L);
        if (_glTex.TryGetValue(key, out int id)) return id;
        var t = _src!.TexAt(i);
        if (t == null || t.Rgba.Length == 0) return _whiteTex;
        id = gl.GenTexture();
        gl.BindTexture(GL.TEXTURE_2D, id);
        _f.PixelStorei(GL.UNPACK_ALIGNMENT, 1);
        unsafe { fixed (byte* p = t.Rgba)
            gl.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA, t.W, t.H, 0, GL.RGBA, GL.UNSIGNED_BYTE, (IntPtr)p); }
        gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.LINEAR);
        gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.LINEAR);
        int wrap = clamp ? GL.CLAMP_TO_EDGE : GL.REPEAT;
        gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, wrap);
        gl.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, wrap);
        _glTex[key] = id;
        return id;
    }

    static int BlendClass(int blend) => blend switch
    {
        0x00 => 5, 0x42 => 3, 0x44 or 0x04 => 1,
        0x46 => 4, 0x88 => 6, _ => 2,
    };

    int NewVbo(GlInterface gl, float[] data, int usage)
    {
        int b = gl.GenBuffer();
        gl.BindBuffer(GL.ARRAY_BUFFER, b);
        unsafe { fixed (float* p = data)
            gl.BufferData(GL.ARRAY_BUFFER, (IntPtr)(data.Length * 4), (IntPtr)p, usage); }
        return b;
    }

    void UploadStatic(GlInterface gl)
    {
        foreach (var b in _opaque.Concat(_trans)) gl.DeleteBuffer(b.Vbo);
        _opaque.Clear(); _trans.Clear();
        // group by (tex, trans, cull) to minimize state changes
        var groups = _src!.DebugDraws()
            .Select((d, i) => (d, i))
            .GroupBy(x => (x.d.tex, x.d.trans, x.d.cull));
        foreach (var g in groups)
        {
            var verts = g.SelectMany(x => x.d.v).ToArray();
            if (verts.Length == 0) continue;
            int vbo = NewVbo(gl, verts, GL.STATIC_DRAW);
            var b = new Batch
            {
                Vbo = vbo, Tex = g.Key.tex, Count = verts.Length / 13,
                Mode = g.Key.trans ? 1 : 0, Cull = g.Key.cull,
            };
            (g.Key.trans ? _trans : _opaque).Add(b);
            if (g.Key.tex >= 0) UploadTexture(gl, g.Key.tex, false);
        }
        // overlay (walkmesh/markers) — one VBO, alpha blend, no tex
        var ov = _src.DebugOverlay().SelectMany(v => v).ToArray();
        if (ov.Length > 0)
        {
            gl.BindBuffer(GL.ARRAY_BUFFER, _overlayVbo);
            unsafe { fixed (float* p = ov)
                gl.BufferData(GL.ARRAY_BUFFER, (IntPtr)(ov.Length * 4), (IntPtr)p, GL.STATIC_DRAW); }
            _overlayCount = ov.Length / 13;
        }
        _staticDirty = false;
    }

    void Attribs(GlInterface gl)
    {
        gl.VertexAttribPointer(0, 3, GL.FLOAT, 0, STRIDE, (IntPtr)0);
        gl.VertexAttribPointer(1, 4, GL.FLOAT, 0, STRIDE, (IntPtr)12);
        gl.VertexAttribPointer(2, 2, GL.FLOAT, 0, STRIDE, (IntPtr)28);
        gl.EnableVertexAttribArray(0); gl.EnableVertexAttribArray(1); gl.EnableVertexAttribArray(2);
    }

    void BindTex(GlInterface gl, int tex, bool clamp = false)
    {
        gl.ActiveTexture(GL.TEXTURE0);
        gl.BindTexture(GL.TEXTURE_2D, tex >= 0 ? UploadTexture(gl, tex, clamp) : _whiteTex);
        _f.Uniform1i(_uTex, 0);
        _f.Uniform1f(_uUseTex, tex >= 0 ? 1f : 0f);
    }

    void BlendState(GlInterface gl, int mode, bool zwrite)
    {
        switch (mode)
        {
            case 0: case 5: // replace / opaque
                gl.Disable(GL.BLEND); break;
            case 1: // 0x44 alpha
                gl.Enable(GL.BLEND); _f.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
                _f.BlendEquation(GL.FUNC_ADD); break;
            case 3: // 0x42 reverse subtract: dst - src*srcA
                gl.Enable(GL.BLEND); _f.BlendFunc(GL.SRC_ALPHA, GL.ONE);
                _f.BlendEquation(GL.FUNC_REVERSE_SUBTRACT); break;
            case 4: // 0x46 darken: dst*(1-srcA)
                gl.Enable(GL.BLEND); _f.BlendFunc(GL.ZERO, GL.ONE_MINUS_SRC_ALPHA);
                _f.BlendEquation(GL.FUNC_ADD); break;
            case 6: // 0x88 src*a replace
                gl.Enable(GL.BLEND); _f.BlendFunc(GL.SRC_ALPHA, GL.ZERO);
                _f.BlendEquation(GL.FUNC_ADD); break;
            default: // 2: 0x48 additive dst += src*srcA
                gl.Enable(GL.BLEND); _f.BlendFunc(GL.SRC_ALPHA, GL.ONE);
                _f.BlendEquation(GL.FUNC_ADD); break;
        }
        gl.DepthMask(zwrite ? 1 : 0);
    }

    /// <summary>Render one frame into the provided framebuffer object.</summary>
    public unsafe void Render(GlInterface gl, int fb, int W, int H)
    {
        if (_src == null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_src.GeomVersion != _lastGeomVersion) _staticDirty = true;
        if (_staticDirty) { UploadStatic(gl); _lastGeomVersion = _src.GeomVersion; }

        gl.BindFramebuffer(GL.FRAMEBUFFER, fb);
        gl.Viewport(0, 0, W, H);
        gl.ClearColor(0x20 / 255f, 0x22 / 255f, 0x26 / 255f, 1f);
        gl.ClearDepth(1f);
        gl.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
        gl.Enable(GL.DEPTH_TEST); gl.DepthFunc(GL.LESS);
        gl.Disable(GL.CULL_FACE); // match software raster (no winding risk)

        // camera: same orbit as the software renderer
        var eye = _src.EyePos();
        var fwd = _src.FwdVec();
        var view = LookAt(eye, new[] { eye[0] + fwd[0], eye[1] + fwd[1], eye[2] + fwd[2] });
        var proj = Perspective(2.41421356f, (float)W / H, 0.05f, (float)(_src.Dist * 20 + 400));

        gl.UseProgram(_prog);
        gl.BindVertexArray(_vao);
        unsafe { fixed (float* vp = view) gl.UniformMatrix4fv(_uView, 1, false, vp);
                 fixed (float* pp = proj) gl.UniformMatrix4fv(_uProj, 1, false, pp); }
        _f.Uniform1f(_uBoost, _src.Brightness);

        // 1) opaque map geometry
        foreach (var b in _opaque)
        {
            BindTex(gl, b.Tex); BlendState(gl, 0, true);
            gl.BindBuffer(GL.ARRAY_BUFFER, b.Vbo); Attribs(gl);
            gl.DrawArrays(GL.TRIANGLES, 0, (IntPtr)b.Count);
        }
        // 2) translucent map geometry (alpha blend, no z write)
        foreach (var b in _trans)
        {
            BindTex(gl, b.Tex); BlendState(gl, 1, false);
            gl.BindBuffer(GL.ARRAY_BUFFER, b.Vbo); Attribs(gl);
            gl.DrawArrays(GL.TRIANGLES, 0, (IntPtr)b.Count);
        }
        // 3) particle FX — rebuilt each frame into the dynamic VBO
        UploadFxAndDraw(gl);
        // 4) editor overlay (walkmesh/markers) — alpha, no z write
        if (_src.ShowOverlay && _overlayCount > 0)
        {
            BindTex(gl, -1); BlendState(gl, 1, false);
            gl.BindBuffer(GL.ARRAY_BUFFER, _overlayVbo); Attribs(gl);
            gl.DrawArrays(GL.TRIANGLES, 0, (IntPtr)_overlayCount);
        }
        gl.BindVertexArray(0);
        // frame-time probe: average over 120 frames to the app log
        _ms += sw.Elapsed.TotalMilliseconds;
        if (++_frames >= 120)
        {
            Console.WriteLine($"[gl] {_ms / _frames:0.##}ms/frame avg ({W}x{H}) fxCalls={_fxCalls} fxVerts={_fxVerts}");
            _ms = 0; _frames = 0;
        }
    }
    double _ms; int _frames;

    float[] _fxBuf = new float[1 << 20];
    (float[] v, int tex, int blend, bool noDepth)[] _fxSorted = new (float[], int, int, bool)[4096];

    void UploadFxAndDraw(GlInterface gl)
    {
        var eye = _src!.EyePos();
        // noclip sorts every FX draw back-to-front (TRANSLUCENT+PARTICLES
        // layer, sortKey = view depth) — alpha/subtract modes depend on it
        var list = _src.DebugFx();
        int n = list.Count();
        if (_fxSorted.Length < n) _fxSorted = new (float[], int, int, bool)[n * 2];
        int i = 0;
        foreach (var f in list) _fxSorted[i++] = f;
        Array.Sort(_fxSorted, 0, n,
            Comparer<(float[] v, int tex, int blend, bool noDepth)>.Create(
                (a, b) => DepthOf(b.v, eye).CompareTo(DepthOf(a.v, eye))));
        int total = 0;
        for (int k = 0; k < n; k++) total += _fxSorted[k].v.Length;
        if (total == 0) return;
        if (_fxBuf.Length < total) _fxBuf = new float[total * 2];
        int pos = 0;
        var ranges = new (int first, int count, int tex, int mode, bool noDepth)[n];
        for (int k = 0; k < n; k++)
        {
            var f = _fxSorted[k];
            ranges[k] = (pos / 13, f.v.Length / 13, f.tex, BlendClass(f.blend), f.noDepth);
            Array.Copy(f.v, 0, _fxBuf, pos, f.v.Length);
            pos += f.v.Length;
        }
        gl.BindBuffer(GL.ARRAY_BUFFER, _fxVbo);
        unsafe { fixed (float* p = _fxBuf)
            gl.BufferData(GL.ARRAY_BUFFER, (IntPtr)(total * 4), (IntPtr)p, GL.DYNAMIC_DRAW); }
        Attribs(gl);
        // merge depth-adjacent draws sharing (tex, blend, depth) into one call —
        // keeps the sorted order while cutting state changes/draw calls
        int calls = 0;
        for (int k = 0; k < n;)
        {
            var r = ranges[k];
            int span = r.count, j = k + 1;
            while (j < n && ranges[j].tex == r.tex && ranges[j].mode == r.mode
                && ranges[j].noDepth == r.noDepth && ranges[j].first == r.first + span)
            { span += ranges[j].count; j++; }
            if (span == 0) { k = j; continue; }
            // noclip: isGlare draws with depthCompare=Always (renders on top)
            if (r.noDepth) gl.Disable(GL.DEPTH_TEST);
            BindTex(gl, r.tex, clamp: true); BlendState(gl, r.mode, false);
            gl.DrawArrays(GL.TRIANGLES, r.first, (IntPtr)span);
            if (r.noDepth) gl.Enable(GL.DEPTH_TEST);
            calls++;
            k = j;
        }
        _fxCalls = calls; _fxVerts = total / 13;
    }
    int _fxCalls, _fxVerts;

    /// <summary>Squared distance of the draw's first vertex from the eye —
    /// noclip sorts on the model-matrix translation, a single point; the
    /// first vertex is the same locality for particle-localized draws.</summary>
    static float DepthOf(float[] v, float[] eye)
    {
        double dx = v[0] - eye[0], dy = v[1] - eye[1], dz = v[2] - eye[2];
        return (float)(dx * dx + dy * dy + dz * dz);
    }

    // --- camera math -----------------------------------------------------
    static float[] LookAt(float[] e, float[] t)
    {
        // forward = target - eye (points INTO the scene, +Z in view space)
        var f = Norm(new[] { t[0] - e[0], t[1] - e[1], t[2] - e[2] });
        var r = Norm(Cross(f, new[] { 0f, 1f, 0f }));
        var u = Cross(r, f);
        // column-major; view maps world -> view with +Z forward
        return new float[]
        {
            r[0], u[0], -f[0], 0,
            r[1], u[1], -f[1], 0,
            r[2], u[2], -f[2], 0,
            -Dot(r, e), -Dot(u, e), Dot(f, e), 1,
        };
    }
    static float[] Perspective(float f, float aspect, float near, float far) => new float[]
    {
        f / aspect, 0, 0, 0,
        0, f, 0, 0,
        0, 0, (far + near) / (near - far), -1,
        0, 0, 2 * far * near / (near - far), 0,
    };
    static float[] Cross(float[] a, float[] b) => new[]
        { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static float[] Norm(float[] v)
    {
        float l = (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return l < 1e-9f ? new[] { 0f, 0f, 1f } : new[] { v[0] / l, v[1] / l, v[2] / l };
    }
    static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    public void Dispose()
    {
        // GL objects die with the shared context; nothing to free on CPU side.
    }
}
