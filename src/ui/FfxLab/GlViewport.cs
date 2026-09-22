// OpenGL-backed viewport — hosts GlSceneRenderer inside Avalonia's shared
// compositor context (OpenGlControlBase). Falls back cleanly when the platform
// cannot give us a GL surface (OnOpenGlInit never runs then; a visible-timeout
// marks GlFailed so the owner switches back to the CPU path).
using System;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace FfxLab;

public sealed class GlViewport : OpenGlControlBase
{
    readonly GlSceneRenderer _r = new();
    /// <summary>GL init failed (no shared-context interop) — owner falls back
    /// to the CPU raster path instead of showing a black box.</summary>
    public bool GlFailed;
    public string GlError = "";
    /// <summary>Raised on the UI thread when GL fails after having been visible —
    /// lets the owner re-route rendering even when no timer is ticking.</summary>
    public Action? Failed;
    bool _init;
    DispatcherTimer? _initWatch;

    /// <summary>Scene + camera source — the same MapRenderer the CPU path uses.</summary>
    public MapRenderer? Scene
    {
        get => _scene; set { _scene = value; if (value != null) _r.SetScene(value); }
    }
    MapRenderer? _scene;

    /// <summary>Frames requested but OnOpenGlInit/Render never ran — the
    /// compositor cannot give us a GL surface (software skia, remote display,
    /// missing interop). Watch starts on the FIRST frame request, not on
    /// attach: GL init is lazy and only runs once a frame is asked for.</summary>
    void ArmInitWatch()
    {
        if (_init || GlFailed) return;
        _initWatch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _initWatch.Stop();
        _initWatch.Tick -= OnWatch;
        _initWatch.Tick += OnWatch;
        _initWatch.Start();
        void OnWatch(object? s, EventArgs e)
        {
            _initWatch?.Stop();
            if (!_init && !GlFailed)
            {
                GlFailed = true; GlError = "sem contexto GL compartilhado";
                Failed?.Invoke();
            }
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _init = true;
        if (!_r.Init(gl)) { GlFailed = true; GlError = _r.InitError; Failed?.Invoke(); }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _init = false;
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (GlFailed || _scene == null) return;
        double sc = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * sc)), h = Math.Max(1, (int)(Bounds.Height * sc));
        try { _r.Render(gl, fb, w, h); }
        catch (Exception ex)
        {
            GlFailed = true; GlError = ex.Message;
            Dispatcher.UIThread.Post(() => Failed?.Invoke());
        }
    }

    /// <summary>Ask the compositor for a frame (called by the fx/anim timers
    /// and on camera drags — replaces QueueRender on the GPU path).</summary>
    public void QueueGl()
    {
        if (GlFailed) return;
        if (!_init) ArmInitWatch();   // GL is lazy: first request starts the clock
        RequestNextFrameRendering();
    }
}
