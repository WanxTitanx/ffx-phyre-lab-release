// ViewportControl — the software-rendered 3D surface (orbit/pan/zoom/anim),
// embeddable in the main shell or hosted standalone by MapWindow.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FfxMap1;
using System.Linq;

namespace FfxLab;

public partial class ViewportControl : UserControl
{
    /// <summary>Raster backend: software CPU, GPU (OpenGL), or Hybrid =
    /// GPU preferred with automatic CPU fallback (inclusive — runs anywhere).</summary>
    public enum RasterBackend { Cpu, Gpu, Hybrid }

    MapRenderer? _r;
    readonly Image _img = new();
    readonly GlViewport _glView = new();
    readonly Panel _viewportPanel;
    RasterBackend _backend = RasterBackend.Hybrid;
    bool _useGl;
    readonly Border _empty;
    WriteableBitmap? _bmp;
    byte[]? _frame;
    float[]? _zbuf;
    Point _last;
    bool _drag;
    Point _pressPos;
    int _dragActor = -1;
    float _dragPlaneY, _dragOffX, _dragOffZ;
    readonly TextBlock _hud = new();
    readonly TextBlock _hint = new();

    // animation playback
    bool _playing = true;

    // particle system (PPP)
    ParticleSim.Sys? _psys;
    readonly List<ParticleSim.DrawItem> _fxOut = new();
    DispatcherTimer? _fxTimer;
    /// <summary>Live effect-edit multipliers applied per tick (non-destructive).</summary>
    public float FxSpeed = 1f, FxSize = 1f, FxEmit = 1f, FxLife = 1f;
    public float[] FxTint = { 1f, 1f, 1f, 1f };
    float _fxScale = 1f;
    Func<ParticleSim.Sys?>? _fxRespawn;

    public MapRenderer? Renderer => _r;
    public bool Playing => _playing;
    /// <summary>Raised when the user clicks an actor instance (not a drag).</summary>
    public event Action<int>? ActorPicked;
    /// <summary>Enable press-drag on actors (battle edit mode).</summary>
    public bool ActorDragEnabled { get; set; }
    /// <summary>Raised during actor drag with the live world position.</summary>
    public event Action<int, (float x, float y, float z)>? ActorDragged;
    /// <summary>Raised when an actor drag ends, with the final world position.</summary>
    public event Action<int, (float x, float y, float z)>? ActorDropped;

    public ViewportControl()
    {
        var panel = new DockPanel();

        // HUD bottom bar
        var hudBar = new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(10, 6),
        };
        var hudRow = new DockPanel();
        _hud.Foreground = (Avalonia.Media.IBrush?)Avalonia.Application.Current!
            .FindResource("LabTextSecondary") ?? Avalonia.Media.Brushes.Gray;
        _hud.FontFamily = (Avalonia.Media.FontFamily?)Avalonia.Application.Current!
            .FindResource("FontMono") ?? Avalonia.Media.FontFamily.Default;
        _hud.FontSize = 11;
        DockPanel.SetDock(_hud, Dock.Left);
        _hint.Foreground = (Avalonia.Media.IBrush?)Avalonia.Application.Current!
            .FindResource("LabTextMuted") ?? Avalonia.Media.Brushes.Gray;
        _hint.FontSize = 11;
        _hint.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        _hint.Margin = new Avalonia.Thickness(12, 0, 0, 0);
        _hint.Text = "drag=órbita  rmb=pan  wheel=zoom  WASD/QE  R=fit  Esp=play";
        // narrow windows: the hint ellipsizes instead of clipping the HUD
        _hint.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _hint.MaxWidth = 520;
        hudRow.Children.Add(_hud);
        hudRow.Children.Add(_hint);
        hudBar.Child = hudRow;
        DockPanel.SetDock(hudBar, Dock.Bottom);
        panel.Children.Add(hudBar);

        // image (CPU) + GL surface + empty state — same panel, toggled by backend
        var viewport = new Panel();
        _viewportPanel = viewport;
        _img.Stretch = Avalonia.Media.Stretch.Uniform;
        _glView.Failed += QueueRender;   // async GL failure re-routes the frame
        viewport.Children.Add(_glView);
        viewport.Children.Add(_img);
        _empty = new Border
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "nenhum conteúdo no viewport",
                Foreground = (Avalonia.Media.IBrush?)Avalonia.Application.Current!
                    .FindResource("LabTextMuted") ?? Avalonia.Media.Brushes.Gray,
                FontSize = 13,
            },
        };
        viewport.Children.Add(_empty);
        panel.Children.Add(viewport);
        Content = panel;
        _glView.IsVisible = false;

        Focusable = true;
        (int px, int py)? ToPx(Point p)
        {
            int iw = _bmp?.PixelSize.Width ?? 0, ih = _bmp?.PixelSize.Height ?? 0;
            var bb = _img.Bounds;
            if (iw <= 0 || bb.Width <= 0) return null;
            // Image is Uniform-stretched: map click into pixel space
            double sc = Math.Min(bb.Width / iw, bb.Height / ih);
            double ox = (bb.Width - iw * sc) / 2, oy = (bb.Height - ih * sc) / 2;
            return ((int)((p.X - ox) / sc), (int)((p.Y - oy) / sc));
        }
        PointerPressed += (s, e) =>
        {
            Focus();
            _drag = true; _last = e.GetPosition(this); _pressPos = _last;
            // actor drag: pressing on an actor moves it on its ground plane
            // instead of orbiting the camera (battle edit mode)
            if (ActorDragEnabled && _r != null
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var px = ToPx(_last);
                if (px != null)
                {
                    int iw = _bmp!.PixelSize.Width, ih = _bmp.PixelSize.Height;
                    int hit = _r.PickActor(px.Value.px, px.Value.py, iw, ih);
                    if (hit >= 0)
                    {
                        var pl = _r.ActorInstances[hit].Place!;
                        _dragPlaneY = pl[13];
                        var f = _r.RayFloor(px.Value.px, px.Value.py, iw, ih, _dragPlaneY);
                        _dragOffX = f.HasValue ? pl[12] - f.Value.x : 0;
                        _dragOffZ = f.HasValue ? pl[14] - f.Value.z : 0;
                        _dragActor = hit;
                    }
                }
            }
            e.Pointer.Capture(this);
        };
        PointerReleased += (s, e) =>
        {
            _drag = false; e.Pointer.Capture(null);
            if (_dragActor >= 0)
            {
                var inst = _dragActor; _dragActor = -1;
                var pl = _r!.ActorInstances[inst].Place!;
                ActorDropped?.Invoke(inst, (pl[12], pl[13], pl[14]));
                return;
            }
            // click (not drag) on an actor = pick it for editing
            var p = e.GetPosition(this);
            if (_r != null && Math.Abs(p.X - _pressPos.X) < 4 && Math.Abs(p.Y - _pressPos.Y) < 4
                && e.InitialPressMouseButton == MouseButton.Left)
            {
                var px = ToPx(p);
                if (px != null)
                {
                    int hit = _r.PickActor(px.Value.px, px.Value.py,
                        _bmp!.PixelSize.Width, _bmp.PixelSize.Height);
                    if (hit >= 0) ActorPicked?.Invoke(hit);
                }
            }
        };
        PointerMoved += (s, e) =>
        {
            if (!_drag || _r == null) return;
            var p = e.GetPosition(this);
            var dx = p.X - _last.X;
            var dy = p.Y - _last.Y;
            _last = p;
            if (_dragActor >= 0)
            {
                // slide the actor along its ground plane under the cursor
                var px = ToPx(p);
                if (px != null)
                {
                    var f = _r.RayFloor(px.Value.px, px.Value.py,
                        _bmp!.PixelSize.Width, _bmp.PixelSize.Height, _dragPlaneY);
                    if (f.HasValue)
                    {
                        _r.MoveActorTo(_dragActor, f.Value.x + _dragOffX,
                            _dragPlaneY, f.Value.z + _dragOffZ);
                        var pl = _r.ActorInstances[_dragActor].Place!;
                        ActorDragged?.Invoke(_dragActor, (pl[12], pl[13], pl[14]));
                        QueueRender();
                    }
                }
                return;
            }
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                double k = _r.Dist * 0.0016;
                double cy = Math.Cos(_r.Yaw), sy = Math.Sin(_r.Yaw);
                _r.Tx -= dx * k * cy; _r.Tz += dx * k * sy;
                _r.Ty += dy * k;
            }
            else
            {
                _r.Yaw += dx * 0.008;
                _r.Pitch = Math.Clamp(_r.Pitch + dy * 0.008, -1.45, 1.45);
            }
            QueueRender();
        };
        SizeChanged += (s, e) => QueueRender();
        KeyDown += (s, e) =>
        {
            if (_r == null) return;
            if (e.Key == Key.R) _r.Fit();
            else if (e.Key == Key.Space) { TogglePlay(); return; }
            double step = _r.Dist * 0.08;
            if (e.Key == Key.W) _r.Ty += step;
            else if (e.Key == Key.S) _r.Ty -= step;
            else if (e.Key == Key.A) { _r.Tx -= step * Math.Cos(_r.Yaw); _r.Tz += step * Math.Sin(_r.Yaw); }
            else if (e.Key == Key.D) { _r.Tx += step * Math.Cos(_r.Yaw); _r.Tz -= step * Math.Sin(_r.Yaw); }
            else if (e.Key == Key.Q) _r.Dist *= 0.9;
            else if (e.Key == Key.E) _r.Dist *= 1.1;
            else return;
            QueueRender();
        };
        AddHandler(PointerWheelChangedEvent, (s, e) =>
        {
            if (_r == null) return;
            _r.Dist = Math.Clamp(_r.Dist * (e.Delta.Y > 0 ? 0.85 : 1.18), 0.5, 1e7);
            QueueRender();
        }, handledEventsToo: true);
        AttachedToVisualTree += (s, e) => QueueRender();
    }

    /// <summary>Load a renderer and show it; clears any animation binding.</summary>
    public void SetContent(MapRenderer r)
    {
        ActorDragEnabled = false; // loaders opt in (battle edit mode)
        StopAnimation();
        _fxTimer?.Stop();
        _psys = null;
        _fxScale = 1f;
        _r = r;
        _r.Fit();
        _glView.Scene = r;
        _empty.IsVisible = false;
        UpdateHud();
        QueueRender();
    }

    public void Clear()
    {
        StopAnimation();
        _r = null;
        _glView.Scene = null;
        ShowEmpty();
        _hud.Text = "";
        _img.Source = null;
    }

    /// <summary>Show a centered status message (loading, error, empty).</summary>
    public void SetBusy(string msg = "carregando…")
    {
        ((TextBlock)_empty.Child!).Text = msg;
        _empty.IsVisible = true;
    }

    public void ShowEmpty() => SetBusy("nenhum conteúdo no viewport");

    /// <summary>Per-instance animation playback state.</summary>
    sealed class AnimBind
    {
        public required int Inst;
        public required ActorBin Actor;
        public required ActorAnim Anim;
        public required ActorAnim.Animation Clip;
        public required float[] State;
        public ushort[]? BoneMap;
        public float T;
    }
    /// <summary>Animation playback multiplier (1 = real-time).</summary>
    public float AnimSpeed = 1f;
    readonly List<AnimBind> _anims = new();
    DispatcherTimer? _animTimer;
    DispatcherTimer? _effTimer;
    int _effFrame;

    /// <summary>Start EFFECT keyframe playback — the map renderer loops
    /// every bound track at ~30fps (script-activated in-game; preview
    /// treats them as always active).</summary>
    public void StartEffects()
    {
        if (_effTimer != null) return;
        _effTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _effTimer.Tick += (s, e) =>
        {
            if (!_playing || _r == null) return;
            _r.TickEffects(_effFrame++);
            QueueRender();
        };
        _effTimer.Start();
    }

    /// <summary>Attach animation playback to an actor instance (default 0).
    /// Multiple binds run on one shared 30fps timer.</summary>
    public void AttachAnimation(ActorBin actor, ActorAnim anim, int animId, int inst = 0)
    {
        var clip = animId >= 0 ? anim.Resolve(animId)
            : anim.Groups.SelectMany(g => g.Animations).FirstOrDefault();
        if (clip == null) return;
        var bm = actor.BoneMappings();
        bm.TryGetValue(animId >> 16 & 0xFFFF, out var boneMap);
        _anims.Add(new AnimBind
        {
            Inst = inst, Actor = actor, Anim = anim, Clip = clip,
            State = ActorAnim.BindState(actor), BoneMap = boneMap,
            T = clip.Segments.FirstOrDefault()?.Start ?? 0,
        });
        _animTimer ??= StartAnimTimer();
        UpdateHud();
    }

    DispatcherTimer StartAnimTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (s, e) =>
        {
            if (!_playing || _r == null || _anims.Count == 0) return;
            foreach (var b in _anims)
            {
                var seg = b.Clip.Segments[0];
                b.T += AnimSpeed;
                if (b.T >= seg.End) b.T = seg.Start;
                b.Anim.EvalPose(b.Clip, b.T, b.State, b.BoneMap);
                _r.UpdateActorPose(b.Inst, b.State);
            }
            QueueRender();
        };
        t.Start();
        return t;
    }

    /// <summary>Attach a PPP particle system — stepped at 30fps and drawn
    /// as the renderer's FX layer (billboard quads use the camera basis).
    /// fxScale converts particle units to this renderer's world units
    /// (actor FX are authored in scaled units; the standalone actor view
    /// draws raw model space, so pass 1/totalScale there).</summary>
    /// <summary>FX-only content (magic/actor particles with no map): when all
    /// emitters die, respawn a fresh system so one-shot effects keep looping
    /// in preview. respawn is null for map-attached systems.</summary>
    public void AttachParticles(ParticleSim.Sys sys, float fxScale = 1f,
        Func<ParticleSim.Sys?>? respawn = null)
    {
        _psys = sys;
        _fxScale = fxScale;
        _fxRespawn = respawn;
        _fxTimer?.Stop();
        _fxTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _fxTimer.Tick += (s, e) =>
        {
            if (!_playing || _psys == null || _r == null) return;
            if (respawn != null && _psys.Emitters.All(em => em.Dead))
            {
                _psys = respawn();
                if (_psys == null) { _fxTimer.Stop(); return; }
            }
            _psys.CamRight = _r.CamRight;
            _psys.CamUp = _r.CamUp;
            _fxOut.Clear();
            var eye = _r.EyePos();
            float inv = _fxScale != 0 ? 1f / _fxScale : 1f;
            _psys.CamPos = new[] { eye[0] * inv, eye[1] * inv, eye[2] * inv };
            _psys.CamFwd = _r.FwdVec();
            _psys.EmitMul = FxEmit; _psys.LifeMul = FxLife;
            _psys.Step(1f * FxSpeed, _fxOut);
            float sz = _fxScale * FxSize;
            if (sz != 1f || FxTint[0] != 1f || FxTint[1] != 1f || FxTint[2] != 1f || FxTint[3] != 1f)
                foreach (var it in _fxOut)
                    for (int v = 0; v + 6 < it.V.Length; v += 13)
                    {
                        it.V[v] *= sz; it.V[v + 1] *= sz; it.V[v + 2] *= sz;
                        it.V[v + 3] *= FxTint[0]; it.V[v + 4] *= FxTint[1];
                        it.V[v + 5] *= FxTint[2]; it.V[v + 6] *= FxTint[3];
                    }
            _r.SetFx(_fxOut);
            QueueRender();
        };
        _fxTimer.Start();
        // debug: FFX_ONLY_FX=1 renders only the fx layer framed on it
        if (Environment.GetEnvironmentVariable("FFX_ONLY_FX") == "1" && _r != null)
        {
            for (int i = 0; i < 90; i++) { _fxOut.Clear(); _psys.Step(1f, _fxOut); }
            _r.SetFx(_fxOut);
            var mn = new float[3] { 1e30f, 1e30f, 1e30f };
            var mx = new float[3] { -1e30f, -1e30f, -1e30f };
            foreach (var d in _fxOut)
                for (int i = 0; i + 2 < d.V.Length; i += 13)
                    for (int c = 0; c < 3; c++)
                    {
                        mn[c] = Math.Min(mn[c], d.V[i + c]);
                        mx[c] = Math.Max(mx[c], d.V[i + c]);
                    }
            for (int c = 0; c < 3; c++) { mn[c] *= 0.1f; mx[c] *= 0.1f; }
            if (mn[0] < mx[0]) _r.FitTo(mn, mx);
        }
        UpdateHud();
    }

    public void TogglePlay()
    {
        if (_animTimer == null && _fxTimer == null) return;
        _playing = !_playing;
        UpdateHud();
    }

    public void StopAnimation()
    {
        _animTimer?.Stop();
        _animTimer = null;
        _anims.Clear();
        _fxTimer?.Stop();
        _fxTimer = null;
        _psys = null;
        _fxRespawn = null;
        _effTimer?.Stop();
        _effTimer = null;
    }

    void UpdateHud()
    {
        if (_r == null) { _hud.Text = ""; return; }
        var anim = _anims.Count > 0
            ? $"  |  {_anims.Count} anim{( _anims.Count > 1 ? "s" : "")} 0x{_anims[0].Clip.Id:x} t={_anims[0].T:0} {(_playing ? "▶" : "‖")}"
            : "";
        var fx = _psys != null ? $"  |  fx {_r.FxDraws}d" : "";
        var bk = _useGl
            ? (_backend == RasterBackend.Hybrid ? "GPU(misto)" : "GPU")
            : (_backend == RasterBackend.Hybrid ? "CPU(fallback)" : "CPU");
        _hud.Text = $"[{bk}]  tris {_r.TriCount:n0}  draws {_r.DrawCalls}  |  " +
            $"yaw {_r.Yaw:0.00}  pitch {_r.Pitch:0.00}  dist {_r.Dist:0.#}{anim}{fx}";
    }

    bool _queued;
    /// <summary>Active raster backend. Hybrid prefers GPU and degrades to
    /// CPU automatically when no shared GL context is available.</summary>
    public RasterBackend Backend => _backend;

    public void SetBackend(RasterBackend mode)
    {
        _backend = mode;
        _useGl = mode == RasterBackend.Gpu;
        if (mode == RasterBackend.Hybrid) _useGl = true; // until proven otherwise
        _img.IsVisible = !_useGl;
        _glView.IsVisible = _useGl;
        UpdateHud();
        QueueRender();
    }

    /// <summary>True when the GL surface is the active rasterizer.</summary>
    public bool UsingGpu => _useGl;
    /// <summary>GL init/render error — surfaced to the status bar.</summary>
    public string GlError => _glView.GlError;

    public void QueueRender()
    {
        if (_useGl)
        {
            _glView.QueueGl();
            if (_glView.GlFailed)
            {
                if (_backend == RasterBackend.Hybrid)
                {
                    // inclusive mode — degrade to the CPU raster once
                    _useGl = false;
                    _img.IsVisible = true;
                    _glView.IsVisible = false;
                    _empty.IsVisible = false;
                    UpdateHud();
                }
                else
                {
                    // GPU-only — fail visibly instead of a silent black box
                    SetBusy("GL falhou: " + _glView.GlError);
                    return;
                }
            }
            else return;
        }
        if (_queued || _r == null) return;
        _queued = true;
        Dispatcher.UIThread.Post(RenderNow, DispatcherPriority.Render);
    }

    unsafe void RenderNow()
    {
        _queued = false;
        if (_r == null) return;
        int W = Math.Max(64, (int)Bounds.Width - 2);
        int H = Math.Max(64, (int)Bounds.Height - 40);
        if (_bmp == null || _bmp.PixelSize.Width != W || _bmp.PixelSize.Height != H)
        {
            _bmp = new WriteableBitmap(new PixelSize(W, H), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _frame = new byte[W * H * 4];
            _zbuf = new float[W * H];
            _img.Source = _bmp;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        fixed (byte* fb = _frame)
        fixed (float* zb = _zbuf)
        {
            for (int i = 0; i < W * H * 4; i += 4)
            { fb[i] = 0x20; fb[i + 1] = 0x22; fb[i + 2] = 0x26; fb[i + 3] = 255; }
            _r.Render(fb, W, H, zb);
        }
        using (var l = _bmp.Lock())
        {
            var dst = (byte*)l.Address;
            fixed (byte* src = _frame)
                for (int y = 0; y < H; y++)
                    Buffer.MemoryCopy(src + y * W * 4, dst + y * l.RowBytes, l.RowBytes, W * 4);
        }
        sw.Stop();
        UpdateHud();
        _hud.Text += $"  |  {sw.ElapsedMilliseconds}ms";
        _img.InvalidateVisual();
    }
}
