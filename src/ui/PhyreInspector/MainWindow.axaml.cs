using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.Numerics;

namespace PhyreInspector;

public partial class MainWindow : Window
{
    private readonly SoftRenderer _r = new();
    private readonly PoseJournal _journal = new();
    private GltfScene? _scene;
    private string _workDir;
    private string? _assetPath;
    private Point _last;
    private bool _orbiting, _panning;
    private int _selNode = -1;
    private bool _playing;
    private DispatcherTimer? _timer;

    public MainWindow()
    {
        InitializeComponent();
        var iconUri = new Uri("avares://PhyreInspector/Assets/app-icon.png");
        if (AssetLoader.Exists(iconUri))
            Icon = new WindowIcon(AssetLoader.Open(iconUri));
        _workDir = Path.Combine(Path.GetTempPath(), "phyre-inspector");
        RootBox.Text = Path.Combine(InspectorCore.RepoRoot, ".lab", "corpus");

        ScanBtn.Click += (_, _) => Rescan();
        AssetList.SelectionChanged += (_, _) => OnAssetSelected();
        JointBox.SelectionChanged += (_, _) => OnJointChanged();
        WireChk.IsCheckedChanged += (_, _) => { _r.Wireframe = WireChk.IsChecked == true; Redraw(); };
        Hierarchy.SelectionChanged += (_, _) => OnNodeSelected();
        ApplyBtn.Click += (_, _) => ApplyTransform();
        ResetBtn.Click += (_, _) => { _scene?.ResetPose(); _journal.Ops.Clear(); _journal.Cursor = 0; Redraw(); FillTransform(); };
        UndoBtn.Click += (_, _) => { _journal.Undo(); if (_scene != null) _journal.ApplyTo(_scene); Redraw(); FillTransform(); };
        RedoBtn.Click += (_, _) => { _journal.Redo(); if (_scene != null) _journal.ApplyTo(_scene); Redraw(); FillTransform(); };
        SaveSesBtn.Click += (_, _) => SaveSession();
        LoadSesBtn.Click += (_, _) => LoadSession();
        PlayBtn.Click += (_, _) => TogglePlay();
        ClipBox.SelectionChanged += (_, _) => { _playing = false; PlayBtn.Content = "Play"; Scrub(); };
        TimeSlider.PropertyChanged += (_, e) => { if (e.Property.Name == "Value" && !_playing) Scrub(); };

        Viewport.PointerPressed += OnPress;
        Viewport.PointerMoved += OnMove;
        Viewport.PointerReleased += (_, _) => { _orbiting = _panning = false; };
        Viewport.PointerWheelChanged += OnWheel;
        PropertyChanged += (_, e) => { if (e.Property.Name == nameof(ClientSize)) FitViewport(); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();

        Rescan();
    }

    void Rescan()
    {
        var hits = InspectorCore.DiscoverAssets(RootBox.Text ?? "");
        AssetList.ItemsSource = hits;
        Diag.Text = hits.Count == 0
            ? $"No .dae.phyre/.gltf under '{RootBox.Text}'. Point RootBox at the corpus root (e.g. .lab/corpus)."
            : $"{hits.Count} assets found.";
    }

    string SessionPath =>
        Path.Combine(_workDir, (_assetPath == null ? "scene" :
            Path.GetFileNameWithoutExtension(_assetPath).Replace(".dae", "")) + ".session.json");

    async void OnAssetSelected()
    {
        if (AssetList.SelectedItem is not string path) return;
        Diag.Text = $"Loading {Path.GetFileName(path)}…";
        var (scene, err) = await Task.Run(() => InspectorCore.LoadAsset(path, _workDir));
        if (err != null)
        {
            Diag.Text = $"{err.What}: {err.Detail}\n→ {err.Hint}";
            return;
        }
        _scene = scene!;
        _assetPath = path;
        _journal.Ops.Clear(); _journal.Cursor = 0;
        _selNode = -1; _playing = false; PlayBtn.Content = "Play";
        Diag.Text = $"{Path.GetFileName(path)} — {scene!.Prims.Count} prims, " +
                    $"{scene.Prims.Sum(p => p.Pos.Length)} verts, {scene.SkinJoints.Length} joints, " +
                    $"{scene.Animations.Count} anim(s)" +
                    (scene.TexturePng == null ? " (no texture)" : "");
        BuildHierarchy();
        BuildJointList();
        BuildClipList();
        FillTransform();
        if (_scene.TexturePng != null && File.Exists(_scene.TexturePng)) _r.SetTexture(_scene.TexturePng);
        _r.Dist = 0; // reframe
        Redraw();
    }

    void BuildHierarchy()
    {
        if (_scene == null) { Hierarchy.ItemsSource = null; return; }
        var nodes = new TreeViewItem[_scene.NodeLocal.Count];
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = new TreeViewItem { Header = $"{i}: {_scene.NodeName[i]}", Tag = i };
        var roots = new List<TreeViewItem>();
        for (int i = 0; i < nodes.Length; i++)
        {
            int par = _scene.NodeParent[i];
            if (par >= 0 && par < nodes.Length) nodes[par].Items.Add(nodes[i]);
            else roots.Add(nodes[i]);
        }
        Hierarchy.ItemsSource = roots;
    }

    void BuildJointList()
    {
        var items = new List<string> { "(none — textured)" };
        if (_scene != null)
            for (int j = 0; j < _scene.SkinJoints.Length; j++)
                items.Add($"joint {j}: {_scene.NodeName[_scene.SkinJoints[j]]}");
        JointBox.ItemsSource = items;
        JointBox.SelectedIndex = 0;
    }

    void BuildClipList()
    {
        var items = new List<string>();
        if (_scene != null)
            for (int i = 0; i < _scene.Animations.Count; i++)
                items.Add($"{i}: {_scene.Animations[i].Name} ({_scene.Animations[i].Duration:F2}s)");
        ClipBox.ItemsSource = items;
        if (items.Count > 0) { ClipBox.SelectedIndex = 0; TimeLabel.Text = "0.00s"; }
        else TimeLabel.Text = "no animation";
        TimeSlider.IsEnabled = PlayBtn.IsEnabled = ClipBox.IsEnabled = items.Count > 0;
    }

    void OnNodeSelected()
    {
        if (Hierarchy.SelectedItem is TreeViewItem item && item.Tag is int idx)
        {
            _selNode = idx;
            SelLabel.Text = $"node {idx}: {_scene?.NodeName[idx]}";
            FillTransform();
        }
    }

    static string F(float v) => v.ToString("F3");

    void FillTransform()
    {
        bool on = _scene != null && _selNode >= 0;
        foreach (var b in new[] { Tx, Ty, Tz, Rx, Ry, Rz, Sx, Sy, Sz }) b.IsEnabled = on;
        ApplyBtn.IsEnabled = on;
        if (!on) return;
        var s = _scene!;
        Tx.Text = F(s.PoseT[_selNode].X); Ty.Text = F(s.PoseT[_selNode].Y); Tz.Text = F(s.PoseT[_selNode].Z);
        var e = QuatToEuler(s.PoseR[_selNode]);
        Rx.Text = F(e.X); Ry.Text = F(e.Y); Rz.Text = F(e.Z);
        Sx.Text = F(s.PoseS[_selNode].X); Sy.Text = F(s.PoseS[_selNode].Y); Sz.Text = F(s.PoseS[_selNode].Z);
    }

    static Vector3 QuatToEuler(Quaternion q)
    {
        // ZYX euler, degrees
        float sinr = 2 * (q.W * q.X + q.Y * q.Z), cosr = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        float roll = MathF.Atan2(sinr, cosr);
        float sinp = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1);
        float pitch = MathF.Asin(sinp);
        float siny = 2 * (q.W * q.Z + q.X * q.Y), cosy = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        float yaw = MathF.Atan2(siny, cosy);
        return new Vector3(pitch * 180 / MathF.PI, yaw * 180 / MathF.PI, roll * 180 / MathF.PI);
    }

    void ApplyTransform()
    {
        if (_scene == null || _selNode < 0) return;
        try
        {
            var t = new Vector3(float.Parse(Tx.Text!), float.Parse(Ty.Text!), float.Parse(Tz.Text!));
            var e = new Vector3(float.Parse(Rx.Text!), float.Parse(Ry.Text!), float.Parse(Rz.Text!));
            var s = new Vector3(float.Parse(Sx.Text!), float.Parse(Sy.Text!), float.Parse(Sz.Text!));
            var q = Quaternion.CreateFromYawPitchRoll(e.Y * MathF.PI / 180, e.X * MathF.PI / 180, e.Z * MathF.PI / 180);
            _journal.Record(_selNode, t, q, s);
            _journal.ApplyTo(_scene);
            Redraw();
            Diag.Text = $"node {_selNode} transform applied (op #{_journal.Cursor})";
        }
        catch (FormatException)
        {
            Diag.Text = "invalid number in transform fields — use e.g. 1.5, -0.25";
        }
    }

    void SaveSession()
    {
        try { _journal.Save(SessionPath); Diag.Text = $"session saved: {SessionPath} ({_journal.Ops.Count} ops)"; }
        catch (Exception ex) { Diag.Text = $"save failed: {ex.Message}"; }
    }

    void LoadSession()
    {
        var (j, err) = PoseJournal.Load(SessionPath);
        if (err != null) { Diag.Text = $"load failed: {err}"; return; }
        if (_scene == null) { Diag.Text = "load an asset first"; return; }
        _journal.Ops.Clear(); _journal.Ops.AddRange(j!.Ops); _journal.Cursor = j.Cursor;
        _journal.ApplyTo(_scene);
        Redraw(); FillTransform();
        Diag.Text = $"session loaded: {_journal.Cursor}/{_journal.Ops.Count} ops applied";
    }

    void TogglePlay()
    {
        _playing = !_playing;
        PlayBtn.Content = _playing ? "Pause" : "Play";
    }

    void Tick()
    {
        if (!_playing || _scene == null || ClipBox.SelectedIndex < 0) return;
        var clip = _scene.Animations[ClipBox.SelectedIndex];
        var t = (float)(TimeSlider.Value + 0.033);
        if (t >= clip.Duration) t = 0;
        TimeSlider.Value = t;
        Scrub();
    }

    void Scrub()
    {
        if (_scene == null || ClipBox.SelectedIndex < 0 || _scene.Animations.Count == 0) return;
        var clip = _scene.Animations[ClipBox.SelectedIndex];
        _journal.ApplyTo(_scene);                    // pose edits first
        _scene.SampleAnimation(clip, (float)TimeSlider.Value);  // then anim on top
        TimeLabel.Text = $"{TimeSlider.Value:F2}/{clip.Duration:F2}s";
        Redraw();
    }

    void OnJointChanged()
    {
        _r.HighlightJoint = JointBox.SelectedIndex - 1; // 0 = none
        Redraw();
    }

    void OnPress(object? s, PointerPressedEventArgs e)
    {
        _last = e.GetPosition(Viewport);
        var props = e.GetCurrentPoint(Viewport).Properties;
        _orbiting = props.IsLeftButtonPressed;
        _panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed;
    }

    void OnMove(object? s, PointerEventArgs e)
    {
        var pos = e.GetPosition(Viewport);
        var d = pos - _last; _last = pos;
        if (_orbiting)
        {
            _r.Yaw += (float)d.X * 0.008f;
            _r.Pitch = Math.Clamp(_r.Pitch + (float)d.Y * 0.008f, -1.5f, 1.5f);
        }
        else if (_panning && _scene != null)
        {
            float scale = _r.Dist * 0.0016f;
            var right = new Vector3(MathF.Cos(_r.Yaw), 0, MathF.Sin(_r.Yaw));
            _r.Target += right * (float)-d.X * scale + Vector3.UnitY * (float)d.Y * scale;
        }
        else return;
        Redraw();
    }

    void OnWheel(object? s, PointerWheelEventArgs e)
    {
        _r.Dist = MathF.Max(0.05f, _r.Dist * (e.Delta.Y > 0 ? 0.88f : 1.14f));
        Redraw();
    }

    void FitViewport()
    {
        int w = (int)Math.Max(64, ClientSize.Width - 580);
        int h = (int)Math.Max(64, ClientSize.Height - 110);
        if (w != _r.W || h != _r.H) { _r.W = w; _r.H = h; Redraw(); }
    }

    void Redraw()
    {
        if (_scene == null) return;
        var fb = _r.Render(_scene);
        var wb = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(_r.W, _r.H), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var l = wb.Lock())
            System.Runtime.InteropServices.Marshal.Copy(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(fb.AsSpan()).ToArray(),
                0, l.Address, fb.Length * 4);
        Viewport.Source = wb;
        CamInfo.Text = $"yaw {_r.Yaw:F2} pitch {_r.Pitch:F2} dist {_r.Dist:F2}" +
                       (_r.Wireframe ? " [wire]" : "") +
                       (_r.HighlightJoint >= 0 ? $" [weights j{_r.HighlightJoint}]" : "");
    }
}
