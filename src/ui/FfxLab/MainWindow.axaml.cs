using System.Net.Http;
using System.Buffers.Binary;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FfxMap1;

namespace FfxLab;

public partial class MainWindow : Window
{
    // ---- lab root / server ----
    static string LabRoot()
    {
        var d = AppContext.BaseDirectory;
        for (var p = new DirectoryInfo(d); p != null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "tools/noclip_server.py")))
                return p.FullName;
        return Directory.GetCurrentDirectory();
    }
    static readonly string Root = LabRoot();
    const string ServerBase = "http://127.0.0.1:8777";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public sealed class SceneRow
    {
        public int Idx; public string Cat = ""; public string Name = "";
        public string Maps = ""; public string Extra = "";
        public int[] Events = Array.Empty<int>();
        public int[] Magic = Array.Empty<int>();
        public string Label => $"{Idx,3}  {Name}";
        public List<SceneRow> Kids = new();
    }
    public sealed class CatNode
    {
        public string Label { get; set; } = "";
        public List<CatNode> Kids { get; set; } = new();
        public SceneRow? Row { get; set; }
    }

    readonly List<SceneRow> _scenes = new();
    // encounter overlay edits (slot -> {dx,dy,dz,headingDelta,scaleMult})
    readonly Dictionary<int, float[]> _encEdits = new();
    // undo/redo for encounter edits: (seq, slot, before, after); null = absent.
    // seq orders ops across the slot-edit and structural stacks.
    long _opSeq;
    readonly Stack<(long seq, int slot, float[]? before, float[]? after)> _undo = new();
    readonly Stack<(long seq, int slot, float[]? before, float[]? after)> _redo = new();
    // structural encounter edits: extra monsters + removed slots
    sealed class EncExtra
    {
        public int Monster;
        public float[] Pos = new float[3];
        public float Heading;
        public float Scale = 1;
        public EncExtra Clone() => (EncExtra)MemberwiseClone();
    }
    readonly List<EncExtra> _encExtra = new();
    readonly HashSet<int> _encRemove = new();
    // composition override: slot -> monster id rendered instead of vanilla
    readonly Dictionary<int, int> _encSwap = new();
    readonly List<int> _encMons = new(); // vanilla monster ids of the loaded enc
    // spawn-point deltas for the party/other groups (positions block 0)
    readonly Dictionary<int, float[]> _encParty = new();
    readonly Dictionary<int, float[]> _encOther = new();
    sealed class EncStructSnap
    {
        public List<EncExtra> Extra = new();
        public HashSet<int> Remove = new();
        public Dictionary<int, int> Swap = new();
        public Dictionary<int, float[]> Party = new();
        public Dictionary<int, float[]> Other = new();
    }
    readonly Stack<(long seq, EncStructSnap b, EncStructSnap a)> _structUndo = new();
    readonly Stack<(long seq, EncStructSnap b, EncStructSnap a)> _structRedo = new();
    // viewport drag: actor instance -> encounter slot and its original
    // (pre-edit) position, so the drag delta becomes an overlay edit
    readonly Dictionary<int, int> _encSlotByInst = new();
    readonly Dictionary<int, float[]> _encBasePos = new();
    // field scene editing: EV01 mapPoint deltas per event bin
    bool _fieldMode;
    int _fieldEvIdx;
    readonly Dictionary<int, int> _fldPointByInst = new();
    readonly Dictionary<int, float[]> _fldBasePos = new();   // pt -> {x,y,z,heading}
    readonly Dictionary<int, float[]> _fldEdits = new();     // pt -> {dx,dy,dz,dh}
    readonly Dictionary<int, int> _fldModelSwap = new();     // modelList idx -> pid
    List<int> _fldModels = new();
    readonly Stack<(int pt, float[]? before, float[]? after)> _fldUndo = new();
    readonly Stack<(int pt, float[]? before, float[]? after)> _fldRedo = new();
    int _encEditId = -1;
    readonly List<CatNode> _cats = new();
    SceneRow? _sel;

    /// <summary>Deep-link: open the app with a scene selected and loaded.</summary>
    public int InitialScene { get; set; } = -1;
    /// <summary>Deep-link: play a magic effect (11/{id:x4}.bin) in the viewport.</summary>
    public int InitialMagic { get; set; } = -1;
    public int InitialActor { get; set; } = -1;
    /// <summary>Deep-link: open the Dados tab and load command.bin.</summary>
    public string InitialDados { get; set; } = "";
    /// <summary>Comma list: explorer,inspector — panels start collapsed.</summary>
    public string InitialCollapse { get; set; } = "";

    public MainWindow()
    {
        InitializeComponent();
        var iconUri = new Uri("avares://FfxLab/Assets/app-icon.png");
        if (AssetLoader.Exists(iconUri))
            Icon = new WindowIcon(AssetLoader.Open(iconUri));
        SceneTree.SelectionChanged += OnSel;
        RefreshViewportButtons();
        LoadScenes();
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { FilterBox.Focus(); FilterBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            { if (_fieldMode) UndoFldEdit(); else UndoEncEdit(); e.Handled = true; }
            else if ((e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Shift)))
            { if (_fieldMode) RedoFldEdit(); else RedoEncEdit(); e.Handled = true; }
        };
        // click a monster in the viewport -> select its edit slot
        Viewport.ActorPicked += inst =>
        {
            if (_fieldMode)
            {
                if (!_fldPointByInst.TryGetValue(inst, out var pt)) return;
                EditSlot.Text = pt.ToString();
                if (_fldEdits.TryGetValue(pt, out var fe))
                {
                    EditDX.Text = fe[0].ToString("0.##"); EditDY.Text = fe[1].ToString("0.##");
                    EditDZ.Text = fe[2].ToString("0.##"); EditDH.Text = fe[3].ToString("0.##");
                    EditScale.Text = "1";
                }
                else { EditDX.Text = EditDY.Text = EditDZ.Text = EditDH.Text = "0"; EditScale.Text = "1"; }
                Workspace.SelectedIndex = 3;
                EditStatus.Text = $"mapPoint {pt} selecionado no viewport";
                return;
            }
            if (_encEditId < 0 || !_encSlotByInst.TryGetValue(inst, out var slot)) return;
            SelectEncSlot(slot);
            Workspace.SelectedIndex = 3; // Batalha tab
            EditStatus.Text = $"monstro slot {slot} selecionado no viewport";
        };
        // drag a monster -> live delta fields, drop commits the overlay edit
        Viewport.ActorDragged += (inst, wp) =>
        {
            if (_fieldMode)
            {
                if (!_fldPointByInst.TryGetValue(inst, out var pt) ||
                    !_fldBasePos.TryGetValue(pt, out var fb)) return;
                EditSlot.Text = pt.ToString();
                EditDX.Text = (wp.x - fb[0]).ToString("0.##");
                EditDY.Text = (wp.y - fb[1]).ToString("0.##");
                EditDZ.Text = (wp.z - fb[2]).ToString("0.##");
                return;
            }
            if (!_encSlotByInst.TryGetValue(inst, out var slot) ||
                !_encBasePos.TryGetValue(slot, out var b)) return;
            EditSlot.Text = slot.ToString();
            EditDX.Text = (wp.x - b[0]).ToString("0.##");
            EditDY.Text = (wp.y - b[1]).ToString("0.##");
            EditDZ.Text = (wp.z - b[2]).ToString("0.##");
        };
        Viewport.ActorDropped += (inst, wp) =>
        {
            if (_fieldMode)
            {
                if (!_fldPointByInst.TryGetValue(inst, out var pt) ||
                    !_fldBasePos.TryGetValue(pt, out var fb)) return;
                var ed = _fldEdits.TryGetValue(pt, out var e0)
                    ? (float[])e0.Clone() : new float[4];
                ed[0] = wp.x - fb[0]; ed[1] = wp.y - fb[1]; ed[2] = wp.z - fb[2];
                _fldUndo.Push((pt, _fldEdits.TryGetValue(pt, out var old) ? (float[])old.Clone() : null, (float[])ed.Clone()));
                _fldRedo.Clear();
                _fldEdits[pt] = ed;
                SaveFieldEdits();
                EditSlot.Text = pt.ToString();
                EditStatus.Text = $"ponto {pt}: arrastado +({ed[0]:0.#},{ed[1]:0.#},{ed[2]:0.#}) — salvo em overlay";
                return;
            }
            if (_encEditId < 0 || !_encSlotByInst.TryGetValue(inst, out var slot) ||
                !_encBasePos.TryGetValue(slot, out var b)) return;
            if (slot >= 1000)
            {
                // extra monster: absolute position, persisted immediately
                var xe = _encExtra[slot - 1000];
                xe.Pos = new[] { wp.x, wp.y, wp.z };
                _encBasePos[slot] = xe.Pos;
                SaveEncOverlay();
                EditStatus.Text = $"extra {xe.Monster} movido para ({wp.x:0.#},{wp.y:0.#},{wp.z:0.#}) — salvo";
                return;
            }
            var ed2 = _encEdits.TryGetValue(slot, out var ex0)
                ? (float[])ex0.Clone() : new float[6] { 0, 0, 0, 0, 0, 1 };
            ed2[0] = wp.x - b[0]; ed2[1] = wp.y - b[1]; ed2[2] = wp.z - b[2];
            PushEncEdit(slot, ed2);
            _encEdits[slot] = ed2;
            SelectEncSlot(slot);
            EditStatus.Text = $"slot {slot}: arrastado +({ed2[0]:0.#},{ed2[1]:0.#},{ed2[2]:0.#}) — não salvo";
        };
        _ = EnsureServer();
        SizeChanged += OnSizeChanged;
        Opened += async (s2, e2) =>
        {
            if (InitialCollapse.Contains("explorer")) SetExplorerCollapsed(true);
            if (InitialCollapse.Contains("inspector")) SetInspectorCollapsed(true);
            if (InitialDados.Length > 0)
            {
                Workspace.SelectedIndex = 4;
                for (int i = 0; i < KernelTableBox.Items.Count; i++)
                    if (((ComboBoxItem)KernelTableBox.Items[i]).Content?.ToString() == InitialDados)
                        KernelTableBox.SelectedIndex = i;
                OnKernelLoad(null, null!);
                return;
            }
            if (InitialMagic >= 0)
            {
                MagicBox.Text = InitialMagic.ToString();
                await EnsureServer();
                OnMagicViewport(null, null!);
                return;
            }
            if (InitialActor >= 0)
            {
                ActorBox.Text = InitialActor.ToString();
                await EnsureServer();
                OnActorViewport(null, null!);
                Workspace.SelectedIndex = 2; // Atores (viewport jumps to tab 0)
                return;
            }
            if (InitialScene < 0) return;
            var row = _scenes.FirstOrDefault(x => x.Idx == InitialScene);
            if (row == null) { Status($"cena {InitialScene} não existe"); return; }
            var cat = _cats.FirstOrDefault(c => c.Label == row.Cat);
            var node = cat?.Kids.FirstOrDefault(k => k.Row == row);
            if (node != null) SceneTree.SelectedItem = node;
            SelectScene(row);
            await EnsureServer();
            OnViewport(null, null!);
        };
    }

    void LoadScenes()
    {
        var p = Path.Combine(Root, ".lab/scenes.json");
        if (!File.Exists(p)) { Status("scenes.json ausente — rode tools/extract_scenes"); return; }
        var arr = JsonDocument.Parse(File.ReadAllText(p)).RootElement;
        foreach (var e in arr.EnumerateArray())
        {
            _scenes.Add(new SceneRow
            {
                Idx = e.GetProperty("idx").GetInt32(),
                Cat = e.GetProperty("cat").GetString() ?? "",
                Name = e.GetProperty("name").GetString() ?? "",
                Maps = e.GetProperty("maps").GetString() ?? "",
                Extra = e.GetProperty("extra").GetString() ?? "",
                Events = e.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array
                    ? ev.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>(),
                Magic = e.TryGetProperty("magic", out var mg) && mg.ValueKind == JsonValueKind.Array
                    ? mg.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>(),
            });
        }
        RebuildTree("");
    }

    void RebuildTree(string filter)
    {
        _cats.Clear();
        var f = filter.Trim();
        int shown = 0;
        foreach (var s in _scenes)
        {
            if (f.Length > 0 && !s.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                && !s.Cat.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            shown++;
            var cat = _cats.FirstOrDefault(c => c.Label == s.Cat);
            if (cat == null) { cat = new CatNode { Label = s.Cat }; _cats.Add(cat); }
            cat.Kids.Add(new CatNode { Label = s.Label, Row = s });
        }
        SceneTree.ItemsSource = _cats;
        SceneCount.Text = f.Length > 0 ? $"{shown}/{_scenes.Count}" : $"{_scenes.Count}";
    }

    void OnFilter(object? s, TextChangedEventArgs e) => RebuildTree(FilterBox.Text ?? "");

    void OnSel(object? s, SelectionChangedEventArgs e)
    {
        if (SceneTree.SelectedItem is CatNode { Row: { } r })
            SelectScene(r);
    }

    void SelectScene(SceneRow r)
    {
        _sel = r;
        SceneTitle.Text = r.Name;
        Crumb.Text = $"·  {r.Cat} › {r.Name}";
        SceneInfo.Text = $"cat: {r.Cat}   mapIds: [{r.Maps}]   extra: [{r.Extra}]\n" +
                         $"bins: 13/{2 * r.Idx:x4}.bin + 13/{2 * r.Idx + 1:x4}.bin";
        PropsText.Text = $"idx       {r.Idx} (0x{r.Idx:x})\ncategoria {r.Cat}\nmapIds    {r.Maps}\nextra     {r.Extra}";
        PropsCard.IsVisible = true;
        RefreshSceneButtons();
    }

    // ---- responsive panels: collapse to rails, hysteresis auto-collapse ----

    bool _expCollapsed, _inspCollapsed;
    bool _expAuto, _inspAuto;   // collapsed by width, not by the user
    GridLength _expWidth = new(0.85, GridUnitType.Star);
    GridLength _inspWidth = new(0.9, GridUnitType.Star);

    void SetExplorerCollapsed(bool c, bool auto = false)
    {
        if (_expCollapsed == c) return;
        _expCollapsed = c; _expAuto = auto;
        if (!c) _expAuto = false;
        ExplorerPanel.IsVisible = !c;
        ExplorerRail.IsVisible = c;
        if (c) _expWidth = BodyGrid.ColumnDefinitions[0].Width;
        BodyGrid.ColumnDefinitions[0].MinWidth = c ? 0 : 220;
        BodyGrid.ColumnDefinitions[0].MaxWidth = c ? 28 : 360;
        BodyGrid.ColumnDefinitions[0].Width = c ? new GridLength(28) : _expWidth;
        BodyGrid.ColumnDefinitions[1].Width = c ? new GridLength(0) : new GridLength(4);
    }

    void SetInspectorCollapsed(bool c, bool auto = false)
    {
        if (_inspCollapsed == c) return;
        _inspCollapsed = c; _inspAuto = auto;
        if (!c) _inspAuto = false;
        InspectorPanel.IsVisible = !c;
        InspectorRail.IsVisible = c;
        if (c) _inspWidth = BodyGrid.ColumnDefinitions[4].Width;
        BodyGrid.ColumnDefinitions[4].MinWidth = c ? 0 : 250;
        BodyGrid.ColumnDefinitions[4].MaxWidth = c ? 28 : 380;
        BodyGrid.ColumnDefinitions[4].Width = c ? new GridLength(28) : _inspWidth;
        BodyGrid.ColumnDefinitions[3].Width = c ? new GridLength(0) : new GridLength(4);
    }

    void OnExplorerCollapse(object? s, RoutedEventArgs e) => SetExplorerCollapsed(true);
    void OnExplorerExpand(object? s, RoutedEventArgs e) => SetExplorerCollapsed(false);
    void OnInspectorCollapse(object? s, RoutedEventArgs e) => SetInspectorCollapsed(true);
    void OnInspectorExpand(object? s, RoutedEventArgs e) => SetInspectorCollapsed(false);

    void OnSizeChanged(object? s, SizeChangedEventArgs e)
    {
        var w = Bounds.Width;
        // auto-collapse below thresholds, auto-restore only if it was automatic (hysteresis)
        if (w < 1200 && !_inspCollapsed) SetInspectorCollapsed(true, auto: true);
        else if (w >= 1320 && _inspAuto) SetInspectorCollapsed(false);
        if (w < 1060 && !_expCollapsed) SetExplorerCollapsed(true, auto: true);
        else if (w >= 1180 && _expAuto) SetExplorerCollapsed(false);
    }

    void RefreshViewportButtons()
    {
        bool has = Viewport.Renderer != null;
        FitBtn.IsEnabled = has;
        DetachBtn.IsEnabled = has;
        OverlayChk.IsEnabled = has;
        PlayChk.IsEnabled = has;
    }

    void RefreshSceneButtons()
    {
        bool has = _sel != null;
        OpenViewerBtn.IsEnabled = has;
        ViewportBtn.IsEnabled = has;
        InspectBtn.IsEnabled = has;
        LightBtn.IsEnabled = has;
        OpenBtn.IsEnabled = has;
    }

    void Status(string t) => StatusText.Text = t;
    void Out(string t) => Output.Text = t;

    void ServerPill(bool up, string detail = "")
    {
        ServerDot.Fill = up
            ? (IBrush)Application.Current!.FindResource("LabSuccess")!
            : (IBrush)Application.Current!.FindResource("LabDanger")!;
        ServerStatus.Text = up ? "servidor on" : "servidor off";
        StatsText.Text = detail;
    }

    async Task<bool> EnsureServer()
    {
        try
        {
            using var c = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var r = await Http.GetAsync($"{ServerBase}/noclip/index.html", c.Token);
            if (r.IsSuccessStatusCode) { ServerPill(true); return true; }
        }
        catch { }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"\"{Path.Combine(Root, "tools/noclip_server.py")}\" --port 8777",
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
            await Task.Delay(1500);
            using var c2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var r2 = await Http.GetAsync($"{ServerBase}/noclip/index.html", c2.Token);
            bool ok = r2.IsSuccessStatusCode;
            ServerPill(ok);
            if (!ok) Status("servidor não respondeu — veja .lab/noclip-server.log");
            return ok;
        }
        catch (Exception ex) { ServerPill(false); Status($"server start falhou: {ex.Message}"); return false; }
    }

    async Task<string?> FetchToFile(string rel)
    {
        // overlay wins over the fetched cache (same precedence as the server)
        var ov = OverlayPath(rel);
        if (File.Exists(ov)) return ov;
        var dst = Path.Combine(Root, ".lab/fetched", rel);
        if (File.Exists(dst)) return dst;
        try
        {
            var data = await Http.GetByteArrayAsync($"{ServerBase}/data/FinalFantasyX/{rel}");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, data);
            return dst;
        }
        catch (Exception ex) { Status($"fetch {rel}: {ex.Message}"); return null; }
    }

    static void OpenBrowser(string url)
    {
        string? b = Find("firefox") ?? Find("google-chrome") ?? Find("chromium")
                    ?? Find("xdg-open");
        if (b == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = b, Arguments = url, UseShellExecute = false, CreateNoWindow = true });
    }
    static string? Find(string n)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (File.Exists(Path.Combine(d, n))) return n;
        return null;
    }

    // ---- viewport toolbar ----

    void OnVpFit(object? s, RoutedEventArgs e)
    {
        Viewport.Renderer?.Fit();
        Viewport.QueueRender();
    }

    void OnRasterSel(object? s, SelectionChangedEventArgs e)
    {
        if (Viewport == null) return; // fires during XAML init, before fields
        var mode = RasterSel.SelectedIndex switch
        {
            0 => ViewportControl.RasterBackend.Cpu,
            1 => ViewportControl.RasterBackend.Gpu,
            _ => ViewportControl.RasterBackend.Hybrid,
        };
        Viewport.SetBackend(mode);
        if (!Viewport.UsingGpu && mode != ViewportControl.RasterBackend.Cpu
            && Viewport.GlError.Length > 0)
            Status("GL indisponível (" + Viewport.GlError + ") — raster CPU");
    }

    void OnVpOverlay(object? s, RoutedEventArgs e)
    {
        if (Viewport.Renderer != null)
            Viewport.Renderer.ShowOverlay = OverlayChk.IsChecked == true;
        Viewport.QueueRender();
    }

    void OnVpPlay(object? s, RoutedEventArgs e)
    {
        if (Viewport.Playing != (PlayChk.IsChecked == true))
            Viewport.TogglePlay();
    }

    void OnVpDetach(object? s, RoutedEventArgs e)
    {
        var r = Viewport.Renderer;
        if (r == null) return;
        var w = new MapWindow(r, $"{_sel?.Name ?? "conteúdo"} — viewport");
        Viewport.Clear();
        RefreshViewportButtons();
        w.Show(this);
        Status("viewport desanexado para janela separada");
    }

    // ---- actions ----

    async void OnOpenViewer(object? s, RoutedEventArgs e)
    {
        if (_sel == null) { Status("selecione uma cena no explorer"); return; }
        if (!await EnsureServer()) { Out("servidor não subiu — veja .lab/noclip-server.log"); return; }
        OpenBrowser($"{ServerBase}/noclip/index.html#ffx/{_sel.Idx:x}/1");
        Out($"viewer -> #ffx/{_sel.Idx:x}/1  ({_sel.Name})");
    }

    string? _geomRel;      // 13/xxxx.bin of the current scene's geometry bin
    string? _texRel;       // 13/xxxx.bin of its texture/particle bin
    ParticleSim.Sys? _psys; // PPP particle system of the current scene
    byte[]? _geomBytes;    // its bytes (as loaded, before overlay edits)
    byte[]? _texBytes;     // texture bin bytes (for texture overlay edits)
    MapModelSet? _mapSet;  // parsed scene models (TEX0 list for palette binding)
    MapTextures? _mapTex;  // parsed texture-bin section

    async Task<MapRenderer?> LoadMapRenderer(int idx)
    {
        string a = $"13/{2 * idx:x4}.bin", b = $"13/{2 * idx + 1:x4}.bin";
        var pa = await FetchToFile(a);
        var pb = await FetchToFile(b);
        if (pa == null || pb == null) return null;
        var fa = Map1File.Load(pa);
        var fb = Map1File.Load(pb);
        Map1File geom = fa, tex = fb;
        string geomRel = a;
        bool aIsGeom = fa.WalkSection(fa.Slot(0x14))?.Any(x => x.Type == LevelParts.Model) == true;
        bool bIsGeom = fb.WalkSection(fb.Slot(0x14))?.Any(x => x.Type == LevelParts.Model) == true;
        if (!aIsGeom && bIsGeom) { geom = fb; tex = fa; geomRel = b; }
        _geomRel = geomRel;
        _geomBytes = (byte[])geom.Data.Clone();
        _texRel = geomRel == a ? b : a;
        _texBytes = (byte[])tex.Data.Clone();
        var set = MapModelSet.Parse(geom);
        var mt = MapTextures.Parse(tex)
            ?? throw new InvalidDataException("sem seção de texturas");
        _mapSet = set;
        _mapTex = mt;
        var gs = mt.BuildGsMap(tex);
        var r = MapRenderer.FromMap(set, gs);
        // EFFECT keyframe playback — script-activated in-game; the
        // preview loops every track bound to each part
        try
        {
            var fxs = LevelParts.LevelEffects.ListEffects(geom);
            var bind = new List<(int, LevelParts.LevelEffects.Fx)>();
            foreach (var (en, p) in LevelParts.ListParts(geom))
                foreach (var fxi in p.EffectIndices)
                    foreach (var (_, fx) in fxs)
                        if (fx.Index == fxi) bind.Add((en.Index, fx));
            if (bind.Count > 0) { r.SetEffects(geom, bind); Viewport.StartEffects(); }
        }
        catch { }
        var wm = Walkmesh.Parse(geom);
        if (wm != null) r.AddWalkmesh(wm);
        _psys = LoadPpp(geom);
        PopulatePpp(geom);
        // PPP sprite textures live in their own GS map (noclip
        // particleMap): common_textures.bin + this bin's +0x18 section
        try
        {
            var pgs = new Gs();
            var cp = await FetchToFile("common_textures.bin");
            if (cp != null)
                Sprites.Upload(File.ReadAllBytes(cp), 0, pgs);
            int sp = (int)tex.Slot(0x18);
            if (sp > 0) Sprites.Upload(tex.Data, sp, pgs);
            r.FxGs = pgs;
        }
        catch { }
        return r;
    }

    /// <summary>PPP particle system at slot +0x38 of the geometry bin
    /// (field maps and arenas both carry one).</summary>
    static ParticleSim.Sys? LoadPpp(Map1File geom)
    {
        int pppOffs = (int)geom.Slot(0x38);
        if (pppOffs <= 0 || pppOffs + 0x20 > geom.Data.Length) return null;
        try
        {
            var sys = ParticleSim.Sys.FromBytes(geom.Data, pppOffs);
            return sys.Emitters.Count > 0 ? sys : null;
        }
        catch { return null; }
    }

    async void OnViewport(object? s, RoutedEventArgs e)
    {
        if (_sel == null) { Status("selecione uma cena no explorer"); return; }
        _fieldMode = false;
        Status("carregando mapa…");
        Viewport.SetBusy("decodificando mapa…");
        try
        {
            var r = await LoadMapRenderer(_sel.Idx);
            if (r == null) { Viewport.ShowEmpty(); Out("fetch falhou — verifique o servidor (botão Servidor no header)"); return; }
            Workspace.SelectedIndex = 0;
            Viewport.SetContent(r);
            PopulateParts();
            PopulateTextures();
            if (_psys != null) { Viewport.AttachParticles(_psys); Out($"{_sel.Name}: {r.DrawCalls} draws, {r.TriCount:n0} tris + {_psys.Emitters.Count} emitters PPP — viewport"); }
            else Out($"{_sel.Name}: {r.DrawCalls} draws, {r.TriCount:n0} tris — viewport");
            StatsText.Text = $"{_sel.Name} · {r.TriCount:n0} tris";
            Status("pronto");
            RefreshViewportButtons();
        }
        catch (Exception ex) { Out($"viewport falhou: {ex.Message}"); }
    }

    // --- PPP emitter editing ----------------------------------------------
    // Map bins carry real 0x50-byte emitter records at pppOffs+0x20 (magic
    // bins only have 0x10 scale stubs — not editable here).

    List<Particles.EmitterSpec> _pppEms = new();
    readonly List<Particles.PppEdit.EmitEntry> _pppEmits = new();
    int _pppOffs;

    void PopulatePpp(Map1File geom)
    {
        _pppEms.Clear(); _pppOffs = 0;
        PppEmSel.Items.Clear();
        PppPos.Text = PppEuler.Text = PppScale.Text = "";
        PppDelay.Text = PppWidth.Text = PppHeight.Text = PppMaxDist.Text = "";
        int pppOffs = (int)geom.Slot(0x38);
        if (pppOffs <= 0 || pppOffs + 0x20 > geom.Data.Length) return;
        try
        {
            var l = Particles.Parse(geom.Data, pppOffs);
            _pppOffs = pppOffs;
            _pppEmits.Clear();
            PppEmitSel.Items.Clear();
            foreach (var en in Particles.PppEdit.EmitDatumEntries(geom.Data, pppOffs))
            {
                _pppEmits.Add(en);
                int cnt = geom.Data[en.Off + Particles.PppEdit.EmitOps[en.Op].CntOff];
                PppEmitSel.Items.Add($"b{en.Behavior}/p{en.Program}/i{en.Instr}/e{en.Entry} {en.Kind} cnt={cnt}");
            }
            _pppDats.Clear();
            PppDatSel.Items.Clear();
            foreach (var de in Particles.PppEdit.DatumEntries(geom.Data, pppOffs))
            {
                _pppDats.Add(de);
                PppDatSel.Items.Add($"b{de.Behavior}/p{de.Program}/i{de.Instr}/e{de.Entry} {de.Kind}");
            }
            _pppCols.Clear();
            PppColSel.Items.Clear();
            foreach (var ce in Particles.PppEdit.ColorDatumEntries(geom.Data, pppOffs))
            {
                _pppCols.Add(ce);
                PppColSel.Items.Add($"b{ce.Behavior}/p{ce.Program}/i{ce.Instr}/e{ce.Entry} {ce.Kind}.{ce.Field} " +
                    $"#{geom.Data[ce.Off]:x2}{geom.Data[ce.Off + 1]:x2}{geom.Data[ce.Off + 2]:x2}{geom.Data[ce.Off + 3]:x2}");
            }
            for (int i = 0; i < l.Emitters.Count; i++)
            {
                _pppEms.Add(l.Emitters[i]);
                var e = l.Emitters[i];
                PppEmSel.Items.Add($"#{i} pos({e.Pos[0]:0.#},{e.Pos[1]:0.#},{e.Pos[2]:0.#}) beh={e.Behavior} delay={e.Delay}");
            }
            if (PppEmSel.Items.Count > 0) PppEmSel.SelectedIndex = 0;
            PppStatus.Text = $"{_pppEms.Count} emissor(es) PPP";

        }
        catch { _pppEms.Clear(); PppStatus.Text = "PPP não parseia"; }
    }

    void OnPppEmSel(object? s, SelectionChangedEventArgs e)
    {
        int i = PppEmSel.SelectedIndex;
        if (i < 0 || i >= _pppEms.Count) return;
        var em = _pppEms[i];
        PppPos.Text = $"{em.Pos[0]:0.###},{em.Pos[1]:0.###},{em.Pos[2]:0.###}";
        PppEuler.Text = $"{em.Euler[0]:0},{em.Euler[1]:0},{em.Euler[2]:0}";
        PppScale.Text = $"{em.Scale[0]:0.###},{em.Scale[1]:0.###},{em.Scale[2]:0.###}";
        PppDelay.Text = em.Delay.ToString();
        PppWidth.Text = em.Width.ToString("0.###");
        PppHeight.Text = em.Height.ToString("0.###");
        PppMaxDist.Text = em.MaxDist.ToString("0.###");
    }

    async void OnPppEdit(object? s, RoutedEventArgs e)
    {
        int i = PppEmSel.SelectedIndex;
        if (_geomRel == null || _geomBytes == null || _pppOffs == 0 || i < 0 || i >= _pppEms.Count)
        { PppStatus.Text = "selecione um emissor (carregue uma cena com PPP)"; return; }
        long rec = Particles.PppEdit.EmitterOff(_pppOffs, i);
        if (rec + Particles.PppEdit.RecSize > _geomBytes.Length)
        { PppStatus.Text = "registro fora do bin"; return; }
        float[]? F3(TextBox t)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return null;
            var parts = t.Text.Split(',');
            if (parts.Length != 3) return Array.Empty<float>();
            var v = new float[3];
            for (int k = 0; k < 3; k++)
                if (!PF(parts[k], out v[k])) return Array.Empty<float>();
            return v;
        }
        int[]? I3(TextBox t)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return null;
            var parts = t.Text.Split(',');
            if (parts.Length != 3) return Array.Empty<int>();
            var v = new int[3];
            for (int k = 0; k < 3; k++)
                if (!int.TryParse(parts[k].Trim(), out v[k])) return Array.Empty<int>();
            return v;
        }
        var pos = F3(PppPos); var scl = F3(PppScale); var eul = I3(PppEuler);
        if (pos?.Length == 0 || scl?.Length == 0 || eul?.Length == 0)
        { PppStatus.Text = "vetor precisa ser x,y,z"; return; }
        float? F1(TextBox t) => string.IsNullOrWhiteSpace(t.Text)
            ? null : (PF(t.Text, out var v) ? v : float.NaN);
        var delay = string.IsNullOrWhiteSpace(PppDelay.Text)
            ? (int?)null
            : (int.TryParse(PppDelay.Text, out var dv) ? dv : -1);
        if (delay == -1) { PppStatus.Text = "delay inválido"; return; }
        var md = F1(PppMaxDist); var wd = F1(PppWidth); var hg = F1(PppHeight);
        if (float.IsNaN(md ?? 0) || float.IsNaN(wd ?? 0) || float.IsNaN(hg ?? 0))
        { PppStatus.Text = "valor numérico inválido"; return; }

        var d = (byte[])_geomBytes.Clone();
        if (pos != null) for (int k = 0; k < 3; k++)
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FPos + 4 * k)), pos[k]);
        if (eul != null) for (int k = 0; k < 3; k++)
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FEuler + 4 * k)), eul[k]);
        if (scl != null) for (int k = 0; k < 3; k++)
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FScale + 4 * k)), scl[k]);
        if (delay is { } dv2)
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FDelay)), dv2);
        if (md is { } m2) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FMaxDist)), m2);
        if (wd is { } w2) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FWidth)), w2);
        if (hg is { } h2) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(rec + Particles.PppEdit.FHeight)), h2);
        if (!SaveGeomOverlay(d, $"ppp emitter {i}")) { PppStatus.Text = "falha ao salvar"; return; }
        // re-parse the written copy to prove the record round-trips
        var l2 = Particles.Parse(d, _pppOffs);
        var e2 = l2.Emitters[i];
        PppStatus.Text = $"emissor {i} salvo — reaberto: pos({e2.Pos[0]:0.#},{e2.Pos[1]:0.#},{e2.Pos[2]:0.#}) scale({e2.Scale[0]:0.##})";
        _pppEms[i] = e2;
        await ReloadViewport();
    }

    void OnPppEmitSel(object? s, SelectionChangedEventArgs e)
    {
        int i = PppEmitSel.SelectedIndex;
        if (i < 0 || i >= _pppEmits.Count || _geomBytes == null) return;
        var en = _pppEmits[i];
        var spec = Particles.PppEdit.EmitOps[en.Op];
        int cnt = _geomBytes[en.Off + spec.CntOff];
        int per = spec.PerOff >= 0 ? _geomBytes[en.Off + spec.PerOff] : -1;
        int pat = BitConverter.ToUInt16(_geomBytes, (int)en.Off + Particles.PppEdit.EmitPatOff);
        int prog = BitConverter.ToInt32(_geomBytes, (int)en.Off + spec.ProgOff);
        PppEmitCP.Text = per >= 0
            ? $"{cnt},{per},{pat},{prog}" : $"{cnt},,{pat},{prog}";
    }

    async void OnPppEmDup(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null || _pppOffs <= 0)
        { PppStatus.Text = "carregue uma cena com PPP"; return; }
        int i = PppEmSel.SelectedIndex;
        if (i < 0 || i >= _pppEms.Count) { PppStatus.Text = "selecione um emissor"; return; }
        try
        {
            var d = Particles.PppEdit.DuplicateEmitter(_geomBytes, _pppOffs, i);
            if (!SaveGeomOverlay(d, $"duplicate ppp emitter #{i}"))
            { PppStatus.Text = "falha ao salvar"; return; }
            _geomBytes = d;
            PopulatePpp(Map1File.Load(_geomRel, _geomBytes));
            if (PppEmSel.Items.Count > 0)
                PppEmSel.SelectedIndex = PppEmSel.Items.Count - 1;
            PppStatus.Text = $"emissor #{i} duplicado no fim da tabela — ajuste o clone";
            await ReloadViewport();
        }
        catch (Exception ex) { PppStatus.Text = "dup falhou: " + ex.Message; }
    }

    async void OnPppEmHide(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null || _pppOffs <= 0)
        { PppStatus.Text = "carregue uma cena com PPP"; return; }
        int i = PppEmSel.SelectedIndex;
        if (i < 0 || i >= _pppEms.Count) { PppStatus.Text = "selecione um emissor"; return; }
        var d = (byte[])_geomBytes.Clone();
        long off = Particles.PppEdit.EmitterOff(_pppOffs, i) + Particles.PppEdit.FBehavior;
        // behavior=-1: no behavior to run — inert but structurally intact
        BitConverter.GetBytes(-1).CopyTo(d, (int)off);
        if (!SaveGeomOverlay(d, $"disable ppp emitter #{i}"))
        { PppStatus.Text = "falha ao salvar"; return; }
        _geomBytes = d;
        PppStatus.Text = $"emissor #{i} desativado (behavior=-1) — Desfazer restaura";
        await ReloadViewport();
    }

    async void OnPppEmDel(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null || _pppOffs <= 0)
        { PppStatus.Text = "carregue uma cena com PPP"; return; }
        int i = PppEmSel.SelectedIndex;
        if (i < 0 || i >= _pppEms.Count) { PppStatus.Text = "selecione um emissor"; return; }
        try
        {
            var d = Particles.PppEdit.DeleteEmitter(_geomBytes, _pppOffs, i);
            if (!SaveGeomOverlay(d, $"delete ppp emitter #{i}"))
            { PppStatus.Text = "falha ao salvar"; return; }
            _geomBytes = d;
            PopulatePpp(Map1File.Load(_geomRel, _geomBytes));
            PppStatus.Text = $"emissor #{i} removido da tabela (físico) — histórico/undo restaura";
            await ReloadViewport();
        }
        catch (Exception ex) { PppStatus.Text = "delete falhou: " + ex.Message; }
    }

    void OnPppColSel(object? s, SelectionChangedEventArgs e)
    {
        int i = PppColSel.SelectedIndex;
        if (i < 0 || i >= _pppCols.Count || _geomBytes == null) return;
        var ce = _pppCols[i];
        PppColHex.Text = $"{_geomBytes[ce.Off]:x2}{_geomBytes[ce.Off + 1]:x2}" +
            $"{_geomBytes[ce.Off + 2]:x2}{_geomBytes[ce.Off + 3]:x2}";
    }

    async void OnPppColEdit(object? s, RoutedEventArgs e)
    {
        int i = PppColSel.SelectedIndex;
        if (_geomRel == null || _geomBytes == null || i < 0 || i >= _pppCols.Count)
        { PppStatus.Text = "carregue uma cena com PPP"; return; }
        var hex = (PppColHex.Text ?? "").Trim().TrimStart('#');
        if (hex.Length != 8 || !uint.TryParse(hex,
                System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
        { PppStatus.Text = "cor inválida — use RRGGBBAA"; return; }
        var ce = _pppCols[i];
        var d = (byte[])_geomBytes.Clone();
        d[ce.Off] = (byte)(rgba >> 24); d[ce.Off + 1] = (byte)(rgba >> 16);
        d[ce.Off + 2] = (byte)(rgba >> 8); d[ce.Off + 3] = (byte)rgba;
        if (!SaveGeomOverlay(d, $"ppp color datum {ce.Kind}.{ce.Field} e{ce.Entry}"))
        { PppStatus.Text = "falha ao salvar"; return; }
        _geomBytes = d;
        PppStatus.Text = $"{ce.Kind}.{ce.Field} e{ce.Entry} = #{hex} — overlay {_geomRel}";
        PppColSel.Items[i] = $"b{ce.Behavior}/p{ce.Program}/i{ce.Instr}/e{ce.Entry} {ce.Kind}.{ce.Field} #{hex}";
        await ReloadViewport();
    }

    async void OnPppEmitEdit(object? s, RoutedEventArgs e)
    {
        int i = PppEmitSel.SelectedIndex;
        if (_geomRel == null || _geomBytes == null || i < 0 || i >= _pppEmits.Count)
        { PppStatus.Text = "selecione uma entrada emit (carregue uma cena com PPP)"; return; }
        var en = _pppEmits[i];
        var spec = Particles.PppEdit.EmitOps[en.Op];
        var parts = (PppEmitCP.Text ?? "").Split(',');
        if (parts.Length == 0 || !int.TryParse(parts[0].Trim(), out int cnt) || cnt < 0 || cnt > 255)
        { PppStatus.Text = "count inválido (0..255)"; return; }
        int per = -1;
        if (spec.PerOff >= 0 && parts.Length > 1 && parts[1].Trim().Length > 0 &&
            (!int.TryParse(parts[1].Trim(), out per) || per < 0 || per > 255))
        { PppStatus.Text = (spec.PerMask ? "mask" : "period") + " inválido (0..255)"; return; }
        int pat = -1;
        if (parts.Length > 2 && parts[2].Trim().Length > 0 &&
            (!int.TryParse(parts[2].Trim(), out pat) || pat < 0 || pat > 65535))
        { PppStatus.Text = "pattern inválido (0..65535)"; return; }
        int prog = int.MinValue;
        if (parts.Length > 3 && parts[3].Trim().Length > 0 &&
            !int.TryParse(parts[3].Trim(), out prog))
        { PppStatus.Text = "program inválido (i32)"; return; }
        var d = (byte[])_geomBytes.Clone();
        d[en.Off + spec.CntOff] = (byte)cnt;
        if (spec.PerOff >= 0 && per >= 0) d[en.Off + spec.PerOff] = (byte)per;
        if (pat >= 0) BitConverter.GetBytes((ushort)pat).CopyTo(d, en.Off + Particles.PppEdit.EmitPatOff);
        if (prog != int.MinValue) BitConverter.GetBytes(prog).CopyTo(d, en.Off + spec.ProgOff);
        if (!SaveGeomOverlay(d, $"ppp emit datum b{en.Behavior}p{en.Program}i{en.Instr}e{en.Entry}"))
        { PppStatus.Text = "falha ao salvar"; return; }
        _geomBytes = d;
        PppStatus.Text = $"emit b{en.Behavior}/p{en.Program}/i{en.Instr}/e{en.Entry}: " +
            $"count={cnt}{(per >= 0 ? $" {(spec.PerMask ? "mask" : "period")}={per}" : "")}" +
            $"{(pat >= 0 ? $" pat={pat}" : "")}{(prog != int.MinValue ? $" prog={prog}" : "")} — overlay {_geomRel}";
        PppEmitSel.Items[i] = $"b{en.Behavior}/p{en.Program}/i{en.Instr}/e{en.Entry} {en.Kind} cnt={cnt}";
        await ReloadViewport();
    }

    async void OnInspect(object? s, RoutedEventArgs e)
    {
        if (_sel == null) { Status("selecione uma cena no explorer"); return; }
        var sb = new System.Text.StringBuilder();
        foreach (var rel in new[] { $"13/{2 * _sel.Idx:x4}.bin", $"13/{2 * _sel.Idx + 1:x4}.bin" })
        {
            var p = await FetchToFile(rel);
            if (p == null) { sb.AppendLine($"{rel}: 404/falha"); continue; }
            try
            {
                var f = Map1File.Load(p);
                sb.AppendLine($"== {rel}  {f.Data.Length}B");
                foreach (var (slot, name) in new[] { (0x14, "+0x14"), (0x18, "+0x18 walk"), (0x38, "+0x38 ppp"), (0x3c, "+0x3c yngm"), (0x40, "+0x40 water") })
                {
                    var off = f.Slot(slot);
                    if (off == 0) continue;
                    var sig = f.U32BE(off);
                    sb.AppendLine($"  {name} -> 0x{off:x}  {(sig == 0x65432100 ? "signed" : "raw")}");
                    if (sig == 0x65432100)
                    {
                        var hist = new Dictionary<uint, int>();
                        foreach (var en in f.WalkSection(off)!) hist[en.Type] = hist.GetValueOrDefault(en.Type) + 1;
                        sb.AppendLine($"    {string.Join("  ", hist.OrderBy(kv => kv.Key).Select(kv => $"{LevelParts.TypeName(kv.Key)}x{kv.Value}"))}");
                        if (slot == 0x14)
                            foreach (var (en, pt) in LevelParts.ListParts(f))
                                sb.AppendLine($"    part #{en.Index}{(pt.IsSkybox ? " skybox" : "")} layer={pt.Layer} " +
                                    $"pos({pt.Px:0.#},{pt.Py:0.#},{pt.Pz:0.#}) eu(rad)({pt.Ex:0.###},{pt.Ey:0.###},{pt.Ez:0.###}) fx=[{string.Join(',', pt.EffectIndices)}]");
                    }
                }
                var w = Walkmesh.Parse(f);
                if (w != null)
                    sb.AppendLine($"  walkmesh: {w.Vertices.Length / 4}v {w.Tris.Count}t scale={w.Scale:0.#}");
            }
            catch (Exception ex) { sb.AppendLine($"{rel}: {ex.Message}"); }
        }
        SceneData.Text = sb.ToString();
        Workspace.SelectedIndex = 1;
    }

    async void OnLighting(object? s, RoutedEventArgs e)
    {
        if (_sel == null) { Status("selecione uma cena no explorer"); return; }
        var p = await FetchToFile($"13/{2 * _sel.Idx + 1:x4}.bin");
        if (p == null) return;
        var f = Map1File.Load(p);
        var sec = f.WalkSection(f.Slot(0x14));
        var sb = new System.Text.StringBuilder();
        if (sec != null)
            foreach (var en in sec.Where(x => x.Type == LevelParts.Lighting))
            {
                var l = LevelParts.ReadLighting(f, en);
                sb.AppendLine($"LIGHTING #{en.Index}: clear=rgba({l.R},{l.G},{l.B},{l.A}) fog=rgb({l.FogR},{l.FogG},{l.FogB}) near={l.Near} far={l.Far} op={l.Opacity}");
            }
        sb.AppendLine("\n(editar via: ffx-map1 setlight <geom> <out-overlay>)");
        SceneData.Text = sb.ToString();
        Workspace.SelectedIndex = 1;
    }

    async Task<ActorBin?> FetchActor(int id)
    {
        int g = id >> 12;
        if (g >= ErB.Groups.Length) { Out("grupo fora do range"); return null; }
        int slot = Array.IndexOf(ErB.Groups[g], id & 0xFFF);
        if (slot < 0) { Out($"ator 0x{id:x} nao esta em erB[{g}]"); return null; }
        var rel = $"{g + 28:x2}/{5 * slot:x4}.bin";
        var p = await FetchToFile(rel);
        if (p == null) return null;
        _actorRel = rel;
        _actorBin = ActorBin.Load(p);
        PopulateActorTex();
        return _actorBin;
    }

    async void OnActor(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(ActorBox.Text, out var id)) { Out("actor id invalido — número decimal"); return; }
        var a = await FetchActor(id);
        if (a == null) return;
        int g = id >> 12, slot = Array.IndexOf(ErB.Groups[g], id & 0xFFF);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ator {id} (0x{id:x}) -> {g + 28:x2}/{5 * slot:x4}.bin  {a.Data.Length}B ver={a.Version} modelId={a.ModelId}");
        var tex = a.Textures("a");
        sb.AppendLine($"tex: {tex.Pairs.Count} pairs {tex.Regions.Count} regions anim=0x{tex.AnimOff:x}");
        foreach (var pt in a.Parts())
            sb.AppendLine($"  part bone={pt.Bone} baseV={pt.BaseVertexCount} extraV={pt.ExtraVertexCount} calls={pt.DrawCalls.Count}");
        sb.AppendLine($"bones[{a.BoneCount}] skin[{a.SkinningCount}] refs[{a.RefPoints().Count}]");
        if (a.GetScales() is { } sc) sb.AppendLine($"scales: h={sc.Height:0.##} collR={sc.CollisionRadius:0.##}");
        sb.AppendLine($"particles @0x{a.ParticleOff:x}");
        ActorData.Text = sb.ToString();
    }

    async void OnActorGltf(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(ActorBox.Text, out var id)) { Out("actor id invalido — número decimal"); return; }
        var a = await FetchActor(id);
        if (a == null) return;
        var outDir = Path.Combine(Root, ".lab/out", $"actor{id}");
        a.ExportGltf(Path.Combine(outDir, $"{id:x}.gltf"));
        var texDir = Path.Combine(outDir, "tex");
        Directory.CreateDirectory(texDir);
        foreach (var img in a.Textures($"{id:x}").Images)
        {
            var argb = new uint[img.W * img.H];
            for (int i = 0; i < argb.Length; i++)
                argb[i] = (uint)(img.Rgba[4 * i + 3] << 24 | img.Rgba[4 * i] << 16 | img.Rgba[4 * i + 1] << 8 | img.Rgba[4 * i + 2]);
            Png.Encode(Path.Combine(texDir, img.Name + ".png"), argb, img.W, img.H);
        }
        ActorData.Text = $"exportado -> {outDir}\n  {id:x}.gltf + tex/*.png";
        Status("ator exportado");
    }

    // --- actor texture export/replace --------------------------------------
    // eri: 8bpp index stream per region + swizzled 256-color palette per
    // pair. Replace re-indexes pixels into the pair's own palette.

    ActorBin? _actorBin;
    string? _actorRel;
    ActorBin.TexSection? _actorTex;

    void PopulateActorTex()
    {
        ATexSel.Items.Clear();
        ATexStatus.Text = "";
        _actorTex = null;
        if (_actorBin == null) return;
        PopulateAnimEdits();
        try { _actorTex = _actorBin.Textures("actor"); }
        catch { ATexStatus.Text = "sem seção de texturas"; return; }
        for (int i = 0; i < _actorTex.Images.Count; i++)
        {
            var img = _actorTex.Images[i];
            var pr = _actorTex.Pairs[i];
            ATexSel.Items.Add($"par {i}: tex {pr.Texture} pal {pr.Palette} " +
                $"{img.W}x{img.H} blend={pr.BlendValue}");
        }
        ATexStatus.Text = _actorTex.Images.Count > 0
            ? $"{_actorTex.Images.Count} pares — selecione"
            : "sem texturas";
    }

    void OnATexExport(object? s, RoutedEventArgs e)
    {
        int i = ATexSel.SelectedIndex;
        if (_actorTex == null || _actorBin == null || i < 0 || i >= _actorTex.Images.Count)
        { ATexStatus.Text = "selecione um par (carregue um ator)"; return; }
        var img = _actorTex.Images[i];
        var dir = Path.Combine(Root, ".lab/out/tex");
        Directory.CreateDirectory(dir);
        var outp = Path.Combine(dir, $"actor_{Path.GetFileNameWithoutExtension(_actorRel ?? "x")}_par{i}.png");
        var argb = new uint[img.W * img.H];
        for (int k = 0; k < argb.Length; k++)
            argb[k] = (uint)(img.Rgba[4 * k + 3] << 24 | img.Rgba[4 * k] << 16 |
                             img.Rgba[4 * k + 1] << 8 | img.Rgba[4 * k + 2]);
        Png.Encode(outp, argb, img.W, img.H);
        ATexPath.Text = outp;
        ATexStatus.Text = $"exportado -> {outp} ({img.W}x{img.H})";
    }

    async void OnATexImport(object? s, RoutedEventArgs e)
    {
        int i = ATexSel.SelectedIndex;
        if (_actorTex == null || _actorBin == null || _actorRel == null ||
            i < 0 || i >= _actorTex.Images.Count)
        { ATexStatus.Text = "selecione um par (carregue um ator)"; return; }
        var path = ATexPath.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        { ATexStatus.Text = "PNG não encontrado — exporte primeiro ou indique o caminho"; return; }
        var img = _actorTex.Images[i];
        var pr = _actorTex.Pairs[i];
        var reg = _actorTex.Regions[pr.Texture];
        try
        {
            var (bgra, w, h) = Png.Decode(path);
            if (w != img.W || h != img.H)
            { ATexStatus.Text = $"dimensão {w}x{h} != textura {img.W}x{img.H}"; return; }
            var d = (byte[])_actorBin.Data.Clone();
            ActorBin.ReplaceImage(_actorBin.Data, _actorTex.PaletteOffs[pr.Palette],
                reg.Start, img.W, img.H, bgra, d);
            if (!SaveBinOverlay(_actorRel, d, $"actor tex par {i}"))
            { ATexStatus.Text = "falha ao salvar"; return; }
            ATexStatus.Text = $"par {i} salvo (256 cores da paleta do par) — overlay {_actorRel}";
            // the actor viewport path re-fetches -> reads the overlay
            OnActorViewport(null, new RoutedEventArgs());
        }
        catch (Exception ex) { ATexStatus.Text = $"import falhou: {ex.Message}"; }
        await Task.CompletedTask;
    }

    long? SelPalOff()
    {
        int i = ATexSel.SelectedIndex;
        if (_actorTex == null || i < 0 || i >= _actorTex.Pairs.Count) return null;
        return _actorTex.PaletteOffs[_actorTex.Pairs[i].Palette];
    }

    void OnAPalApply(object? s, RoutedEventArgs e)
    {
        var pal = SelPalOff();
        if (_actorBin == null || _actorRel == null || pal == null)
        { ATexStatus.Text = "selecione um par (carregue um ator)"; return; }
        if (!int.TryParse(APalIdx.Text, out int idx) || idx < 0 || idx > 255)
        { ATexStatus.Text = "índice 0..255"; return; }
        var parts = (APalColor.Text ?? "").Split(',');
        var ch = new byte[4];
        if (parts.Length != 4) { ATexStatus.Text = "cor precisa ser r,g,b,a"; return; }
        for (int k = 0; k < 4; k++)
            if (!byte.TryParse(parts[k].Trim(), out ch[k]))
            { ATexStatus.Text = "canal inválido (0..255)"; return; }
        var d = (byte[])_actorBin.Data.Clone();
        ActorBin.WritePaletteColor(d, pal.Value, idx, ch[0], ch[1], ch[2], ch[3]);
        if (!SaveBinOverlay(_actorRel, d, $"actor palette {idx}")) return;
        ATexStatus.Text = $"paleta[{idx}] = ({ch[0]},{ch[1]},{ch[2]},{ch[3]}) — overlay {_actorRel}";
        OnActorViewport(null, new RoutedEventArgs());
    }

    void OnAPalTint(object? s, RoutedEventArgs e)
    {
        var pal = SelPalOff();
        if (_actorBin == null || _actorRel == null || pal == null)
        { ATexStatus.Text = "selecione um par (carregue um ator)"; return; }
        var parts = (APalTint.Text ?? "").Split(',');
        var m = new float[4];
        if (parts.Length != 4) { ATexStatus.Text = "tint precisa ser r,g,b,a"; return; }
        for (int k = 0; k < 4; k++)
            if (!PF(parts[k], out m[k])) { ATexStatus.Text = "multiplicador inválido"; return; }
        var d = (byte[])_actorBin.Data.Clone();
        ActorBin.TintPalette(d, pal.Value, m[0], m[1], m[2], m[3]);
        if (!SaveBinOverlay(_actorRel, d, "actor palette tint")) return;
        ATexStatus.Text = $"paleta tingida ×({m[0]},{m[1]},{m[2]},{m[3]}) — overlay {_actorRel}";
        OnActorViewport(null, new RoutedEventArgs());
    }

    // anim group ids resolve through erB like actor ids:
    //   folder = (group>>12) + 0x1C, file = 5*slot + i (i = 1..4 anim files)
    async Task<ActorAnim?> FetchAnimGroup(int groupId)
    {
        int g = groupId >> 12;
        if (g >= ErB.Groups.Length) return null;
        int slot = Array.IndexOf(ErB.Groups[g], groupId & 0xFFF);
        if (slot < 0) return null;
        var groups = new List<ActorAnim.Group>();
        for (int i = 1; i <= 4; i++)
        {
            var rel = $"{g + 28:x2}/{5 * slot + i:x4}.bin";
            var p = await FetchToFile(rel);
            if (p == null) continue;
            try
            {
                foreach (var grp in ActorAnim.Parse(File.ReadAllBytes(p)).Groups)
                    groups.Add(grp);
            }
            catch { }
        }
        return groups.Count > 0 ? new ActorAnim { Data = [], Groups = groups } : null;
    }

    async void OnActorViewport(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(ActorBox.Text, out var id)) { Out("actor id invalido — número decimal"); return; }
        _fieldMode = false;
        Status("carregando ator…");
        Viewport.SetBusy("decodificando ator…");
        var a = await FetchActor(id);
        if (a == null) Viewport.ShowEmpty();
        if (a == null) return;
        var r = MapRenderer.FromActor(a);
        // actor-embedded particles (auras, breath FX) live at +0x60
        var ags = new Gs();
        var apsys = ActorParticles.FromActorBin(a.Data, ags);
        if (apsys != null) r.FxGs = ags;
        int animId = -1;
        if (!string.IsNullOrWhiteSpace(AnimBox.Text))
            int.TryParse(AnimBox.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out animId);
        var groups = a.DefaultAnimations().SelectMany(x => x).Select(x => x >> 16 & 0xFFFF).Distinct();
        var anims = new List<ActorAnim.Group>();
        foreach (var gid in groups)
        {
            var part = await FetchAnimGroup(gid);
            if (part != null) anims.AddRange(part.Groups);
        }
        Workspace.SelectedIndex = 0;
        Viewport.SetContent(r);
        if (apsys != null)
        {
            float ts = a.GetScales() is { } sc ? sc.Base * sc.Actor / sc.Offset / 100f : 1f;
            Viewport.AttachParticles(apsys, ts > 0 ? 1f / ts : 1f);
        }
        if (anims.Count > 0)
        {
            var merged = new ActorAnim { Data = [], Groups = anims };
            Viewport.AttachAnimation(a, merged, animId);
            var n = anims.SelectMany(x => x.Animations).Count();
            Out($"ator {id}: viewport + {n} anims" +
                (apsys != null ? $" + {apsys.Emitters.Count} emitters PPP" : "") +
                $" (grupos: {string.Join(", ", groups.Select(x => $"0x{x:x}"))})");
        }
        else Out($"ator {id}: viewport bind-pose (sem anims resolvidas)" +
            (apsys != null ? $" + {apsys.Emitters.Count} emitters PPP" : ""));
        StatsText.Text = $"ator {id} · {r.TriCount:n0} tris";
        Status("pronto");
        RefreshViewportButtons();
    }

    async void OnEnc(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(EncBox.Text, out var id)) { Out("enc id invalido — número decimal"); return; }
        var p = await FetchToFile($"0e/{id:x4}.bin");
        if (p == null) return;
        try
        {
            var enc = EncounterFile.Load(p);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"encounter {id} -> 0e/{id:x4}.bin  {enc.Data.Length}B");
            sb.AppendLine($"monsters: {string.Join(", ", enc.Monsters())}");
            foreach (var b in enc.Positions())
                for (int q = 0; q < b.Monsters.Length; q++)
                    sb.AppendLine($"  blk{b.Index} mon[{q}] = ({b.Monsters[q][0]:0.#},{b.Monsters[q][1]:0.#},{b.Monsters[q][2]:0.#})");
            EncData.Text = sb.ToString();
        }
        catch (Exception ex) { EncData.Text = $"enc {id}: {ex.Message}"; }
    }

    /// <summary>Field scene with EV01 actors: map pair (13/) + event bin
    /// (0c/18*events[0]) placing modelList actors at mapPoints (approximation:
    /// heading from the record; scale from the actor's own scales section).</summary>
    async void OnFieldViewport(object? s, RoutedEventArgs e)
    {
        if (_sel == null) { Status("selecione uma cena no explorer"); return; }
        if (_sel.Events.Length == 0) { Out($"cena {_sel.Idx} sem evento na tabela (grid/extra?)"); return; }
        Status("carregando cena de field…");
        Viewport.SetBusy("montando field + atores…");
        _fieldMode = true;
        _fldPointByInst.Clear(); _fldBasePos.Clear();
        try
        {
            var r = await LoadMapRenderer(_sel.Idx);
            if (r == null) { Viewport.ShowEmpty(); Out("fetch do mapa falhou"); return; }
            int evIdx = _sel.Events[0];
            _fieldEvIdx = evIdx;
            LoadFieldEdits();
            var pe = await FetchToFile($"0c/{18 * evIdx:x4}.bin");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"cena {_sel.Idx} '{_sel.Name}' — mapa 13/{2 * _sel.Idx:x4} evento 0c/{18 * evIdx:x4}");
            if (pe != null)
            {
                _fieldEvBytes = File.ReadAllBytes(pe);
                var ev = Ev01File.Load(pe);
                var models = ev.ModelList();
                _fldModels = models;
                FldAtelInfo.Text = AtelSummary(_fieldEvBytes);
                FldModelSel.Items.Clear();
                for (int mi = 0; mi < models.Count; mi++)
                    FldModelSel.Items.Add($"slot {mi}: pid {(_fldModelSwap.TryGetValue(mi, out var sp2) ? $"{sp2} (era {models[mi]})" : models[mi].ToString())}");
                var pts = ev.Points();
                sb.AppendLine($"modelList[{models.Count}]: {string.Join(" ", models)}");
                sb.AppendLine($"mapPoints[{pts.Count}]");
                // Aproximação documentada: o ATEL é quem posiciona os atores em
                // runtime; aqui distribuímos modelList[i] no mapPoint[i] (ou no
                // primeiro ponto) com o heading do record. Não é a colocação
                // exata do jogo.
                int placed = 0;
                for (int i = 0; i < models.Count; i++)
                {
                    int pid = models[i];
                    if (_fldModelSwap.TryGetValue(i, out var swp)) pid = swp;
                    if (pid == 0) continue;
                    int ptIdx = pts.Count > 0 ? Math.Min(i, pts.Count - 1) : -1;
                    var p = ptIdx >= 0 ? pts[ptIdx] : default;
                    var a = await FetchActor(pid);
                    if (a == null) { sb.AppendLine($"  ator {pid} indisponivel (erB/fetch)"); continue; }
                    var sc = a.GetScales();
                    float ts = sc.HasValue ? sc.Value.Base * sc.Value.Actor / sc.Value.Offset / 100f : 0.01f;
                    float hx = p.X, hy = p.Y, hz = p.Z, hd = p.Heading;
                    if (ptIdx >= 0 && _fldEdits.TryGetValue(ptIdx, out var fe))
                    {
                        hx += fe[0]; hy += fe[1]; hz += fe[2]; hd += fe[3];
                        sb.AppendLine($"  [edit] ponto {ptIdx}: +({fe[0]:0.#},{fe[1]:0.#},{fe[2]:0.#}) heading+{fe[3]:0.##}");
                    }
                    var place = MapRenderer.PlacementM(hx, hy, hz, -hd, ts);
                    int instIdx = r.AddActor(a, place);
                    if (ptIdx >= 0)
                    {
                        _fldPointByInst[instIdx] = ptIdx;
                        _fldBasePos[ptIdx] = new[] { p.X, p.Y, p.Z, p.Heading };
                    }
                    placed++;
                    sb.AppendLine($"  ator {pid} @ ({hx:0.#},{hy:0.#},{hz:0.#}) heading={hd:0.##} scale={ts:0.####}");
                }
                sb.AppendLine($"atores posicionados: {placed} (aprox.: sem execução do ATEL)");
            }
            else sb.AppendLine("evento indisponivel no servidor");
            Workspace.SelectedIndex = 0;
            Viewport.SetContent(r);
            Viewport.ActorDragEnabled = true;
            EncData.Text = sb.ToString();
            StatsText.Text = $"field {_sel.Idx} · {r.TriCount:n0} tris";
            Status("pronto");
            RefreshViewportButtons();
        }
        catch (Exception ex) { EncData.Text = $"field falhou: {ex.Message}"; }
    }

    async void OnEncViewport(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(EncBox.Text, out var id)) { Out("enc id invalido — número decimal"); return; }
        _fieldMode = false;
        Status("carregando batalha…");
        Viewport.SetBusy("montando batalha…");
        var pb = await FetchToFile("0d/0000.bin");
        var pe = await FetchToFile($"0e/{id:x4}.bin");
        if (pb == null || pe == null) { Out("fetch falhou — verifique o servidor"); return; }
        _encEditId = id;
        LoadEncEdits(id);
        _encSlotByInst.Clear();
        _encBasePos.Clear();
        var lists = BattleLists.Load(pb);
        var loc = lists.Locate(id);
        if (loc == null) { EncData.Text = $"enc 0e/{id:x4} nao consta nas battle lists"; return; }
        var (area, pool) = loc.Value;
        int mapIdx = pool.Map;
        var pa = await FetchToFile($"1a/{2 * mapIdx:x4}.bin");
        var px = await FetchToFile($"1a/{2 * mapIdx + 1:x4}.bin");
        if (pa == null || px == null) { EncData.Text = $"arena 1a/{2 * mapIdx:x4} indisponivel"; return; }
        try
        {
            var fa = Map1File.Load(pa);
            var fb = Map1File.Load(px);
            Map1File geom = fa, tex = fb;
            bool aIsGeom = fa.WalkSection(fa.Slot(0x14))?.Any(x => x.Type == LevelParts.Model) == true;
            bool bIsGeom = fb.WalkSection(fb.Slot(0x14))?.Any(x => x.Type == LevelParts.Model) == true;
            if (!aIsGeom && bIsGeom) { geom = fb; tex = fa; }
            var set = MapModelSet.Parse(geom);
            var mt = MapTextures.Parse(tex) ?? throw new InvalidDataException("sem seção de texturas");
            var gs = mt.BuildGsMap(tex);
            var r = MapRenderer.FromMap(set, gs);
            // arena walkmesh: FitBounds so the skydome doesn't dominate framing
            var wm = Walkmesh.Parse(geom);
            if (wm != null)
            {
                r.AddWalkmesh(wm);
                var mn = new[] { 1e30f, 1e30f, 1e30f };
                var mx = new[] { -1e30f, -1e30f, -1e30f };
                foreach (var t in wm.Tris)
                    foreach (var vi in new[] { t.V0, t.V1, t.V2 })
                    {
                        var (wx, wy, wz) = wm.VertPos(vi);
                        float[] q = { wx / 10f, wy / 10f, wz / 10f };
                        for (int c = 0; c < 3; c++)
                        { mn[c] = Math.Min(mn[c], q[c]); mx[c] = Math.Max(mx[c], q[c]); }
                    }
                if (mn[0] < mx[0]) r.FitBounds = (mn, mx);
            }
            var enc = EncounterFile.Load(pe);
            var mons = enc.Monsters();
            _encMons.Clear(); _encMons.AddRange(mons);
            var blocks = enc.Positions();
            var sb = new System.Text.StringBuilder();
            var pendingAnims = new List<(ActorBin a, ActorAnim anim, int inst)>();
            sb.AppendLine($"enc {id} -> area '{area.Name}' arena 1a/{2 * mapIdx:x4} " +
                $"({set.Models.Count} models) — {mons.Count} monstros");
            if (blocks.Count > 0)
            {
                var blk = blocks[0];
                float[][] Adj(float[][] arr, Dictionary<int, float[]> map)
                {
                    if (map.Count == 0) return arr;
                    var c = arr.Select(x => (float[])x.Clone()).ToArray();
                    foreach (var kv in map)
                        if (kv.Key >= 0 && kv.Key < c.Length)
                            for (int k = 0; k < 3; k++) c[kv.Key][k] += kv.Value[k];
                    return c;
                }
                var adjParty = Adj(blk.Party, _encParty);
                var adjOther = Adj(blk.Other, _encOther);
                for (int i = 0; i < mons.Count && i < blk.Monsters.Length; i++)
                {
                    if (_encRemove.Contains(i))
                    { sb.AppendLine($"  slot {i}: removido (overlay)"); continue; }
                    int mid = _encSwap.TryGetValue(i, out var swm) ? swm : mons[i];
                    int g = mid >> 12;
                    int slot = g < ErB.Groups.Length ? Array.IndexOf(ErB.Groups[g], mid & 0xFFF) : -1;
                    if (slot < 0) { sb.AppendLine($"  mon {mid}: fora de erB[{g}]"); continue; }
                    var mp = await FetchToFile($"{g + 28:x2}/{5 * slot:x4}.bin");
                    if (mp == null) { sb.AppendLine($"  mon {mid}: bin falhou"); continue; }
                    var a = ActorBin.Load(mp);
                    var pos = blk.Monsters[i];
                    var sc = a.GetScales();
                    float ts = sc.HasValue ? sc.Value.Base * sc.Value.Actor / sc.Value.Offset / 100f : 0.01f;
                    float hx = -pos[0], hz = -pos[2];
                    if (adjParty.Length > 0) { hx = adjParty[0][0] - pos[0]; hz = adjParty[0][2] - pos[2]; }
                    float rot = -MathF.Atan2(hx, hz) - MathF.PI / 2;
                    float mx = pos[0], my = pos[1], mz = pos[2];
                    if (_encEdits.TryGetValue(i, out var ed))
                    {
                        mx += ed[0]; my += ed[1]; mz += ed[2];
                        rot += ed[4];
                        if (ed[5] != 0) ts *= ed[5];
                        sb.AppendLine($"  [edit] slot {i}: +({ed[0]:0.#},{ed[1]:0.#},{ed[2]:0.#}) " +
                            $"heading+{ed[4]:0.##} scale×{ed[5]:0.##}");
                    }
                    var place = MapRenderer.PlacementM(mx, my, mz, rot, ts);
                    int instIdx = r.AddActor(a, place);
                    _encSlotByInst[instIdx] = i;
                    _encBasePos[i] = pos;
                    // idle animation: first resolved group clip plays per-instance
                    var gids = a.DefaultAnimations().SelectMany(x => x).Select(x => x >> 16 & 0xFFFF).Distinct();
                    var mAnims = new List<ActorAnim.Group>();
                    foreach (var gid in gids)
                    {
                        var part = await FetchAnimGroup(gid);
                        if (part != null) mAnims.AddRange(part.Groups);
                    }
                    if (mAnims.Count > 0)
                        pendingAnims.Add((a, new ActorAnim { Data = [], Groups = mAnims }, instIdx));
                    sb.AppendLine($"  mon {mid}{(_encSwap.ContainsKey(i) ? $" (troca de {mons[i]})" : "")} @ ({pos[0]:0.#},{pos[1]:0.#},{pos[2]:0.#}) scale={ts:0.####}");
                }
                // overlay extras: absolute placements at virtual slots 1000+k
                for (int k = 0; k < _encExtra.Count; k++)
                {
                    var xe = _encExtra[k];
                    int mid = xe.Monster;
                    int g = mid >> 12;
                    int slot = g < ErB.Groups.Length ? Array.IndexOf(ErB.Groups[g], mid & 0xFFF) : -1;
                    if (slot < 0) { sb.AppendLine($"  extra {mid}: fora de erB[{g}]"); continue; }
                    var mp = await FetchToFile($"{g + 28:x2}/{5 * slot:x4}.bin");
                    if (mp == null) { sb.AppendLine($"  extra {mid}: bin falhou"); continue; }
                    var a = ActorBin.Load(mp);
                    var sc = a.GetScales();
                    float ts = (sc.HasValue ? sc.Value.Base * sc.Value.Actor / sc.Value.Offset / 100f : 0.01f) * xe.Scale;
                    float hx = -xe.Pos[0], hz = -xe.Pos[2];
                    if (adjParty.Length > 0) { hx = adjParty[0][0] - xe.Pos[0]; hz = adjParty[0][2] - xe.Pos[2]; }
                    float rot = -MathF.Atan2(hx, hz) - MathF.PI / 2 + xe.Heading;
                    int instIdx = r.AddActor(a, MapRenderer.PlacementM(xe.Pos[0], xe.Pos[1], xe.Pos[2], rot, ts));
                    _encSlotByInst[instIdx] = 1000 + k;
                    _encBasePos[1000 + k] = xe.Pos;
                    var gids = a.DefaultAnimations().SelectMany(x => x).Select(x => x >> 16 & 0xFFFF).Distinct();
                    var mAnims = new List<ActorAnim.Group>();
                    foreach (var gid in gids)
                    {
                        var part = await FetchAnimGroup(gid);
                        if (part != null) mAnims.AddRange(part.Groups);
                    }
                    if (mAnims.Count > 0)
                        pendingAnims.Add((a, new ActorAnim { Data = [], Groups = mAnims }, instIdx));
                    sb.AppendLine($"  extra mon {mid} @ ({xe.Pos[0]:0.#},{xe.Pos[1]:0.#},{xe.Pos[2]:0.#}) scale={ts:0.####}");
                }
                // battle framing: eye at the party spot looking at the monster
                // centroid, stepped back so the battle fills the frame
                if (adjParty.Length > 0)
                {
                    var p0 = adjParty[0];
                    double cx = 0, cy = 0, cz = 0; int n = 0;
                    for (int i = 0; i < mons.Count && i < blk.Monsters.Length; i++)
                    { cx += blk.Monsters[i][0]; cy += blk.Monsters[i][1]; cz += blk.Monsters[i][2]; n++; }
                    if (n > 0)
                    {
                        double bx = p0[0] + (p0[0] - cx / n) * 0.45;
                        double bz = p0[2] + (p0[2] - cz / n) * 0.45;
                        r.LookFrom(bx, p0[1], bz, cx / n, cy / n, cz / n);
                    }
                }
            }
            Workspace.SelectedIndex = 0;
            Viewport.SetContent(r);
            Viewport.ActorDragEnabled = true;
            foreach (var (aa, merged, ii) in pendingAnims)
                Viewport.AttachAnimation(aa, merged, -1, ii);
            var apsys = LoadPpp(geom);
            if (apsys != null) Viewport.AttachParticles(apsys);
            EncData.Text = sb.ToString();
            StatsText.Text = $"enc {id} · {r.TriCount:n0} tris";
            Status("pronto");
            RefreshViewportButtons();
        }
        catch (Exception ex) { EncData.Text = $"batalha falhou: {ex.Message}"; }
    }

    /// <summary>Path of the overlay copy the server serves before cache/CDN.</summary>
    string OverlayPath(string rel) => Path.Combine(Root, ".lab/overlays/FinalFantasyX", rel);

    /// <summary>Append-only edit journal: one JSON line per overlay write
    /// (provenance: what was patched, by which tool, on top of which base).
    /// .lab/overlays/.history/<rel>/<n>.bin keeps the previous overlay bytes
    /// so the last edit can be reverted.</summary>
    string EditLogPath() => Path.Combine(Root, ".lab", "editlog.jsonl");
    string HistoryDir(string rel) => Path.Combine(Root, ".lab/overlays/.history", rel.Replace('/', '_'));

    /// <param name="rel">overlay-tree path (served), or null for local
    /// sidecars which journal an absolute <c>path</c> instead.</param>
    void JournalEdit(string? rel, string dst, byte[] patched, string what)
    {
        try
        {
            string? prevSha = null;
            if (File.Exists(dst))
            {
                var prev = File.ReadAllBytes(dst);
                prevSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prev))[..12].ToLowerInvariant();
                var hd = HistoryDir(rel ?? dst);
                Directory.CreateDirectory(hd);
                File.WriteAllBytes(Path.Combine(hd,
                    $"{DateTime.UtcNow:yyyyMMddHHmmss}-{prevSha}.bin"), prev);
            }
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(patched))[..12].ToLowerInvariant();
            File.AppendAllText(EditLogPath(), JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                rel, path = rel == null ? dst : null,
                what, sha, prev = prevSha ?? "base",
            }) + "\n");
        }
        catch (Exception ex) { Out($"journal falhou: {ex.Message}"); }
    }

    /// <summary>Write tmp + move-over: a crash mid-write can't leave a
    /// half-written overlay file.</summary>
    static void AtomicWriteAllBytes(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }

    static void AtomicWriteAllText(string path, string text)
        => AtomicWriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Writes a patched bin copy to the overlay dir so both the
    /// viewer and this viewport read the edit; journals provenance.</summary>
    bool SaveBinOverlay(string rel, byte[] patched, string what)
    {
        var dst = OverlayPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        JournalEdit(rel, dst, patched, what);
        AtomicWriteAllBytes(dst, patched);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(patched))[..12].ToLowerInvariant();
        Out($"{what}: overlay {rel} salvo ({patched.Length} B, sha256 {sha}…)");
        return true;
    }

    /// <summary>Writes a patched copy of the current geometry bin to the
    /// overlay dir so both the viewer and this viewport read the edit.</summary>
    bool SaveGeomOverlay(byte[] patched, string what)
    {
        if (_geomRel == null) { WmStatus.Text = "carregue uma cena no viewport primeiro"; return false; }
        return SaveBinOverlay(_geomRel, patched, what);
    }

    /// <summary>Journaled JSON/text write inside the served overlay tree
    /// (field/, edits/ sidecars the viewer re-fetches).</summary>
    bool SaveTextOverlay(string rel, string text, string what)
    {
        var dst = OverlayPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        JournalEdit(rel, dst, System.Text.Encoding.UTF8.GetBytes(text), what);
        AtomicWriteAllText(dst, text);
        Out($"{what}: overlay {rel} salvo");
        return true;
    }

    /// <summary>Journaled write outside the served tree (UI-only sidecars:
    /// magic FX overrides, kernel .edits.json provenance).</summary>
    bool SaveLocalOverlay(string path, string text, string what)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JournalEdit(null, path, System.Text.Encoding.UTF8.GetBytes(text), what);
        AtomicWriteAllText(path, text);
        return true;
    }

    /// <summary>Pops the last journal entry and restores that rel's previous
    /// overlay state — history snapshot, or delete-overlay when it was the
    /// first edit on top of the fetched base.</summary>
    async void OnUndoEdit(object? s, RoutedEventArgs e)
    {
        var log = EditLogPath();
        var lines = File.Exists(log)
            ? File.ReadAllLines(log).Where(l => l.Trim().Length > 0).ToList()
            : new List<string>();
        if (lines.Count == 0) { Out("sem edições no journal"); return; }
        var last = lines[^1];
        File.WriteAllLines(log, lines.Take(lines.Count - 1));
        try
        {
            var doc = JsonDocument.Parse(last).RootElement;
            string? rel = doc.TryGetProperty("rel", out var rp) && rp.ValueKind == JsonValueKind.String
                ? rp.GetString() : null;
            var what = doc.TryGetProperty("what", out var w) ? w.GetString() : "?";
            var prev = doc.GetProperty("prev").GetString();
            var dst = rel != null ? OverlayPath(rel) : doc.GetProperty("path").GetString()!;
            if (prev == "base")
            {
                File.Delete(dst);
                Out($"undo: {what} — overlay {rel} removido (volta ao base)");
            }
            else
            {
                var hd = HistoryDir(rel ?? dst);
                var snap = Directory.Exists(hd)
                    ? Directory.GetFiles(hd, $"*-{prev}.bin").OrderBy(f => f).LastOrDefault()
                    : null;
                if (snap == null)
                {
                    File.AppendAllText(log, last + "\n"); // don't lose the entry
                    Out($"undo: snapshot {prev} ausente em .history — journal restaurado");
                    return;
                }
                AtomicWriteAllBytes(dst, File.ReadAllBytes(snap));
                Out($"undo: {what} — overlay {rel} revertido p/ {prev}");
            }
            if (rel == _geomRel || rel == _texRel) await ReloadViewport();
        }
        catch (Exception ex) { Out($"undo falhou: {ex.Message}"); }
        await Task.CompletedTask;
    }

    async void OnWalkmeshEdit(object? s, RoutedEventArgs e)
    {
        if (_geomRel == null || _geomBytes == null) { WmStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        var w = Walkmesh.Parse(Map1File.Load(_geomRel, _geomBytes));
        if (w == null) { WmStatus.Text = "esta cena não tem walkmesh válido"; return; }
        if (!int.TryParse(WmFrom.Text, out var from) || !int.TryParse(WmTo.Text, out var to)
            || from < 0 || to >= w.Tris.Count || from > to)
        { WmStatus.Text = $"range inválido (0..{w.Tris.Count - 1})"; return; }
        int? I(TextBox t, int max)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return null;
            if (!int.TryParse(t.Text, out var v) || v < 0 || v > max) return -1;
            return v;
        }
        var pass = I(WmPass, 127); var enc = I(WmEnc, 3);
        var loc = I(WmLoc, 3); var surf = I(WmSurf, 3);
        if (pass == -1 || enc == -1 || loc == -1 || surf == -1)
        { WmStatus.Text = "valor fora do range (pass 0..127, enc/loc/surf 0..3)"; return; }
        if (pass == null && enc == null && loc == null && surf == null)
        { WmStatus.Text = "nada a aplicar — preencha ao menos um campo"; return; }

        var d = (byte[]) _geomBytes.Clone();
        for (int i = from; i <= to; i++)
        {
            var t = w.Tris[i];
            uint data = t.Data;
            if (pass is { } pv) data = (data & ~0x7fu) | ((uint)pv & 0x7f);
            if (enc is { } ev) data = (data & ~(3u << 7)) | (((uint)ev & 3) << 7);
            if (loc is { } lv) data = (data & ~(3u << 11)) | (((uint)lv & 3) << 11);
            if (surf is { } sv) data = (data & ~(3u << 15)) | (((uint)sv & 3) << 15);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((int)(w.TrisOffset + 16 * i + 12)), data);
        }
        if (!SaveGeomOverlay(d, $"walkmesh tris [{from}..{to}]"))
        { WmStatus.Text = "falha ao salvar"; return; }
        // re-parse the written copy to prove it round-trips
        var w2 = Walkmesh.Parse(Map1File.Load(_geomRel, d));
        WmStatus.Text = $"tris [{from}..{to}] salvos — reaberto: {w2?.Tris.Count} tris, " +
            $"pass[{from}]={w2?.Tris[from].Passability}";
        await ReloadViewport();
    }

    void OnWmVertShow(object? s, RoutedEventArgs e)
    {
        if (_geomRel == null || _geomBytes == null) { WmStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        var w = Walkmesh.Parse(Map1File.Load(_geomRel, _geomBytes));
        if (w == null) { WmStatus.Text = "sem walkmesh"; return; }
        if (!int.TryParse(WmVert.Text, out var vi) || vi < 0 || vi >= w.Vertices.Length / 4)
        { WmStatus.Text = $"vértice 0..{w.Vertices.Length / 4 - 1}"; return; }
        var (x, y, z) = w.VertPos(vi);
        WmVertPos.Text = $"{x:0.###},{y:0.###},{z:0.###}";
        WmStatus.Text = $"vértice {vi}: ({x:0.###},{y:0.###},{z:0.###})";
    }

    async void OnWmVertEdit(object? s, RoutedEventArgs e)
    {
        if (_geomRel == null || _geomBytes == null) { WmStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        var w = Walkmesh.Parse(Map1File.Load(_geomRel, _geomBytes));
        if (w == null) { WmStatus.Text = "sem walkmesh"; return; }
        if (!int.TryParse(WmVert.Text, out var vi) || vi < 0 || vi >= w.Vertices.Length / 4)
        { WmStatus.Text = $"vértice 0..{w.Vertices.Length / 4 - 1}"; return; }
        var parts = (WmVertPos.Text ?? "").Split(',');
        if (parts.Length != 3) { WmStatus.Text = "posição precisa ser x,y,z"; return; }
        var v = new float[3];
        for (int k = 0; k < 3; k++)
            if (!PF(parts[k], out v[k])) { WmStatus.Text = "posição inválida"; return; }
        // quantize world pos back to s16×scale (w component preserved)
        var d = (byte[])_geomBytes.Clone();
        long o = w.VertsOffset + 8L * vi;
        for (int k = 0; k < 3; k++)
            BinaryPrimitives.WriteInt16LittleEndian(
                d.AsSpan((int)(o + 2 * k)), (short)Math.Clamp((int)MathF.Round(v[k] * w.Scale), -32768, 32767));
        if (!SaveGeomOverlay(d, $"walkmesh vert {vi}")) { WmStatus.Text = "falha ao salvar"; return; }
        var w2 = Walkmesh.Parse(Map1File.Load(_geomRel, d));
        var (x2, y2, z2) = w2!.VertPos(vi);
        WmStatus.Text = $"vértice {vi} salvo — reaberto: ({x2:0.###},{y2:0.###},{z2:0.###})";
        await ReloadViewport();
    }

    async void OnLightEdit(object? s, RoutedEventArgs e)
    {
        if (_geomRel == null || _geomBytes == null) { LtStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        var f0 = Map1File.Load(_geomRel, _geomBytes);
        var sec = f0.WalkSection(f0.Slot(0x14));
        var lights = sec?.Where(x => x.Type == LevelParts.Lighting).ToList();
        if (lights == null || lights.Count == 0) { LtStatus.Text = "sem entradas LIGHTING"; return; }
        if (!int.TryParse(LtEntry.Text, out var ei) || ei < 0 || ei >= lights.Count)
        { LtStatus.Text = $"entrada 0..{lights.Count - 1}"; return; }
        int[]? Bytes(TextBox t, int n)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return null;
            var parts = t.Text.Split(',');
            if (parts.Length != n) return Array.Empty<int>();
            var v = new int[n];
            for (int i = 0; i < n; i++) if (!int.TryParse(parts[i], out v[i]) || v[i] < 0 || v[i] > 255) return Array.Empty<int>();
            return v;
        }
        float? F(TextBox t) => string.IsNullOrWhiteSpace(t.Text)
            ? null : (PF(t.Text, out var v) ? v : float.NaN);

        var cl = Bytes(LtClear, 3); var fg = Bytes(LtFog, 3);
        var op = F(LtOpacity); var nr = F(LtNear); var fr = F(LtFar);
        if (cl?.Length == 0 || fg?.Length == 0) { LtStatus.Text = "cor precisa ser r,g,b (0..255)"; return; }
        if (float.IsNaN(op ?? 0) || float.IsNaN(nr ?? 0) || float.IsNaN(fr ?? 0))
        { LtStatus.Text = "valor numérico inválido"; return; }
        if (cl == null && fg == null && op == null && nr == null && fr == null)
        { LtStatus.Text = "nada a aplicar"; return; }

        var p = lights[ei].PayloadOffset;
        var d = (byte[]) _geomBytes.Clone();
        if (cl is { Length: 3 }) { d[p] = (byte)cl[0]; d[p + 1] = (byte)cl[1]; d[p + 2] = (byte)cl[2]; }
        if (fg is { Length: 3 }) { d[p + 12] = (byte)fg[0]; d[p + 13] = (byte)fg[1]; d[p + 14] = (byte)fg[2]; }
        if (op is { } o2) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(p + 16)), o2);
        if (nr is { } n2) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(p + 20)), n2);
        if (fr is { } f2) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(p + 24)), f2);
        if (!SaveGeomOverlay(d, $"lighting entry {lights[ei].Index}"))
        { LtStatus.Text = "falha ao salvar"; return; }
        var l2 = LevelParts.ReadLighting(Map1File.Load(_geomRel, d), lights[ei]);
        LtStatus.Text = $"entrada {lights[ei].Index} salva — reaberto: clear=rgba({l2.R},{l2.G},{l2.B},{l2.A}) " +
            $"fog=rgb({l2.FogR},{l2.FogG},{l2.FogB}) near={l2.Near:0.#} far={l2.Far:0.#} op={l2.Opacity:0.##}";
        await ReloadViewport();
    }

    // --- LEVEL_PART transform editing ------------------------------------
    // Parts carry scene-object TRS: euler f3 @+0x10 (radians), position f3
    // @+0x20, isSkybox u16 @+0x02, layer u16 @+0x04 (noclip bin.ts L934+).
    // Corpus defaults are identity (baked world-space verts) — edits still
    // move the whole part subtree in the viewer/viewport.

    List<(SectionEntry Entry, LevelParts.PartInfo Info)> _parts = new();
    List<(SectionEntry Entry, LevelParts.LevelEffects.Fx Info)> _effects = new();
    readonly List<int> _fxSelMap = new(); // FxSel row → index into _effects
    List<SectionEntry> _vertModels = new();
    List<MapModelEdit.VertRef> _vertRefs = new();
    List<MapModelEdit.ColRef> _colRefs = new();

    void PopulateParts()
    {
        _parts.Clear();
        PartSel.Items.Clear();
        PartPos.Text = PartEuler.Text = PartLayer.Text = "";
        PartSky.IsChecked = false;
        if (_geomBytes == null || _geomRel == null) return;
        Map1File? pf = null;
        try { pf = Map1File.Load(_geomRel, _geomBytes);
              _parts = LevelParts.ListParts(pf);
              _effects = LevelParts.LevelEffects.ListEffects(pf); }
        catch (Exception ex) { PartCurrent.Text = $"LEVEL_PART falhou: {ex.Message}"; return; }
        PopulateVertModels();
        foreach (var (en, p) in _parts)
            PartSel.Items.Add($"#{en.Index}{(p.IsSkybox ? " skybox" : "")} layer={p.Layer} " +
                $"pos({p.Px:0.#},{p.Py:0.#},{p.Pz:0.#}) fx={p.EffectIndices.Length}");
        PartCurrent.Text = _parts.Count > 0
            ? $"{_parts.Count} objetos — selecione para editar"
            : "sem LEVEL_PART nesta cena";
    }

    void PopulateVertModels()
    {
        _vertModels.Clear(); _vertRefs.Clear(); _colRefs.Clear();
        VertModelSel.Items.Clear(); VertStatus.Text = "";
        if (_geomBytes == null || _geomRel == null) return;
        try
        {
            var f = Map1File.Load(_geomRel, _geomBytes);
            var sec = f.WalkSection(f.Slot(0x14));
            if (sec == null) return;
            foreach (var e in sec.Where(x => x.Type == LevelParts.Model))
            {
                var vr = MapModelEdit.PositionVerts(f, e.PayloadOffset);
                _vertModels.Add(e);
                VertModelSel.Items.Add($"#{e.Index} — {vr.Count} verts @0x{e.PayloadOffset:x}");
            }
            VertStatus.Text = _vertModels.Count > 0
                ? $"{_vertModels.Count} modelo(s)" : "sem MODEL nesta cena";
        }
        catch { VertStatus.Text = "MODEL falhou"; }
    }

    void OnVertModelSel(object? s, SelectionChangedEventArgs e)
    {
        _vertRefs.Clear(); _colRefs.Clear();
        int i = VertModelSel.SelectedIndex;
        if (i < 0 || i >= _vertModels.Count || _geomBytes == null || _geomRel == null) return;
        var f = Map1File.Load(_geomRel, _geomBytes);
        var en = _vertModels[i];
        _vertRefs = MapModelEdit.PositionVerts(f, en.PayloadOffset);
        _colRefs = MapModelEdit.ColorVerts(f, en.PayloadOffset);
        VertStatus.Text = $"modelo #{en.Index}: {_vertRefs.Count} vértices, {_colRefs.Count} cores";
    }

    void OnVertRead(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(VertIdx.Text, out int vi) || vi < 0 || vi >= _vertRefs.Count)
        { VertStatus.Text = $"vértice 0..{_vertRefs.Count - 1}"; return; }
        var vr = _vertRefs[vi];
        VertPos.Text = FormattableString.Invariant($"{vr.X:0.###},{vr.Y:0.###},{vr.Z:0.###}");
        if (vi < _colRefs.Count)
        {
            var cr = _colRefs[vi];
            VertCol.Text = $"{cr.R:x2}{cr.G:x2}{cr.B:x2}{cr.A:x2}";
        }
        VertStatus.Text = $"v{vi} run{vr.Run}.local{vr.Local} @0x{vr.Off:x}";
    }

    async void OnVertEdit(object? s, RoutedEventArgs e)
    {
        int mi = VertModelSel.SelectedIndex;
        if (_geomBytes == null || _geomRel == null || mi < 0 || mi >= _vertModels.Count)
        { VertStatus.Text = "selecione um modelo"; return; }
        if (!int.TryParse(VertIdx.Text, out int vi) || vi < 0 || vi >= _vertRefs.Count)
        { VertStatus.Text = $"vértice 0..{_vertRefs.Count - 1}"; return; }
        var d = (byte[])_geomBytes.Clone();
        var what = new List<string>();
        var posTxt = (VertPos.Text ?? "").Trim();
        if (posTxt.Length > 0)
        {
            var parts = posTxt.Split(',');
            if (parts.Length != 3 ||
                !PF(parts[0], out float vx) || !PF(parts[1], out float vy) || !PF(parts[2], out float vz))
            { VertStatus.Text = "pos inválida — use x,y,z"; return; }
            MapModelEdit.WriteVert(d, _vertRefs[vi], vx, vy, vz);
            what.Add($"pos=({vx:0.#},{vy:0.#},{vz:0.#})");
        }
        var colTxt = (VertCol.Text ?? "").Trim().TrimStart('#');
        if (colTxt.Length > 0)
        {
            if (colTxt.Length != 8 || !uint.TryParse(colTxt,
                    System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
            { VertStatus.Text = "cor inválida — RRGGBBAA"; return; }
            MapModelEdit.WriteColor(d, _colRefs[vi],
                new[] { (byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba });
            what.Add($"cor=#{colTxt}");
        }
        if (what.Count == 0) { VertStatus.Text = "nada a aplicar"; return; }
        if (!SaveGeomOverlay(d, $"model vert {vi} {string.Join(" ", what)}"))
        { VertStatus.Text = "falha ao salvar"; return; }
        _geomBytes = d;
        // refresh the refs so consecutive edits chain cleanly
        var f = Map1File.Load(_geomRel, _geomBytes);
        var en = _vertModels[mi];
        _vertRefs = MapModelEdit.PositionVerts(f, en.PayloadOffset);
        _colRefs = MapModelEdit.ColorVerts(f, en.PayloadOffset);
        VertStatus.Text = $"v{vi}: {string.Join(" ", what)} — overlay {_geomRel}";
        await ReloadViewport();
    }

    void OnPartSel(object? s, SelectionChangedEventArgs e)
    {
        int i = PartSel.SelectedIndex;
        if (i < 0 || i >= _parts.Count) return;
        var (en, p) = _parts[i];
        PartPos.Text = FormattableString.Invariant($"{p.Px:0.###},{p.Py:0.###},{p.Pz:0.###}");
        PartEuler.Text = FormattableString.Invariant(
            $"{p.Ex * 180 / Math.PI:0.###},{p.Ey * 180 / Math.PI:0.###},{p.Ez * 180 / Math.PI:0.###}");
        PartLayer.Text = p.Layer.ToString();
        PartSky.IsChecked = p.IsSkybox;
        PartCurrent.Text = $"entry #{en.Index} @0x{en.PayloadOffset:x} — " +
            $"eu(rad)=({p.Ex:0.####},{p.Ey:0.####},{p.Ez:0.####}) order={p.EulerOrder} " +
            $"fx=[{string.Join(',', p.EffectIndices)}]";
        // populate the effect combo from this part's fx refs
        PartFxSel.Items.Clear(); _fxSelMap.Clear();
        PartFxKey.Text = PartFxVec.Text = PartFxVec3.Text = PartFxDur.Text = "";
        foreach (int fxi in p.EffectIndices)
        {
            int li = _effects.FindIndex(x => x.Info.Index == fxi);
            if (li < 0) continue;
            var fx = _effects[li].Info;
            _fxSelMap.Add(li);
            PartFxSel.Items.Add($"fx{fxi} {LevelParts.LevelEffects.TypeName(fx.Type)} " +
                $"keys={fx.Keys.Length}");
        }
        PartFxStatus.Text = _fxSelMap.Count == 0
            ? "objeto sem efeitos" : $"{_fxSelMap.Count} efeito(s) — escolha um";
    }

    void OnPartFxSel(object? s, SelectionChangedEventArgs e)
    {
        int i = PartFxSel.SelectedIndex;
        if (i < 0 || i >= _fxSelMap.Count) return;
        var fx = _effects[_fxSelMap[i]].Info;
        PartFxStatus.Text = $"fx{fx.Index}: {LevelParts.LevelEffects.TypeName(fx.Type)} " +
            $"{fx.Keys.Length} keyframes — informe keyframe#/vec slot";
    }

    async void OnPartFxApply(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null) { PartFxStatus.Text = "carregue uma cena"; return; }
        int i = PartFxSel.SelectedIndex;
        if (i < 0 || i >= _fxSelMap.Count) { PartFxStatus.Text = "selecione um efeito"; return; }
        var (en, fx) = _effects[_fxSelMap[i]];
        if (!int.TryParse(PartFxKey.Text, out int ki) || ki < 0 || ki >= fx.Keys.Length)
        { PartFxStatus.Text = $"keyframe 0..{fx.Keys.Length - 1}"; return; }
        var k = fx.Keys[ki];
        if (!int.TryParse(PartFxVec.Text, out int vs) || vs < 0 || vs >= k.VecCount)
        { PartFxStatus.Text = $"vec slot 0..{k.VecCount - 1}"; return; }
        if (string.IsNullOrWhiteSpace(PartFxVec3.Text)) { PartFxStatus.Text = "informe x,y,z"; return; }
        var ps = PartFxVec3.Text.Split(',');
        if (ps.Length != 3) { PartFxStatus.Text = "x,y,z"; return; }
        var v = new float[3];
        for (int q = 0; q < 3; q++)
            if (!float.TryParse(ps[q], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v[q]))
            { PartFxStatus.Text = "vec inválido"; return; }
        int? dur = null;
        if (!string.IsNullOrWhiteSpace(PartFxDur.Text))
        {
            if (!int.TryParse(PartFxDur.Text, out int dv) || dv <= 0)
            { PartFxStatus.Text = "duração inválida"; return; }
            dur = dv;
        }
        try
        {
            var f = Map1File.Load(_geomRel, _geomBytes);
            var d = LevelParts.LevelEffects.SetKey(f, en, k, vs, v[0], v[1], v[2], dur);
            if (!SaveGeomOverlay(d, $"effect fx{fx.Index} key{ki} vec{vs}"))
            { PartFxStatus.Text = "falha ao salvar"; return; }
            _geomBytes = d;
            var pf = Map1File.Load(_geomRel, _geomBytes);
            _effects = LevelParts.LevelEffects.ListEffects(pf);
            PartFxStatus.Text = $"fx{fx.Index} key{ki} vec{vs}=({v[0]:0.##},{v[1]:0.##},{v[2]:0.##})" +
                (dur.HasValue ? $" dur={dur}" : "") + " — gravado";
            await ReloadViewport();
        }
        catch (Exception ex) { PartFxStatus.Text = "falhou: " + ex.Message; }
    }

    async void OnPartEdit(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null) { PartStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        int i = PartSel.SelectedIndex;
        if (i < 0 || i >= _parts.Count) { PartStatus.Text = "selecione um objeto"; return; }
        var (en, info) = _parts[i];

        float[]? V3(TextBox t)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return null;
            var ps = t.Text.Split(',');
            if (ps.Length != 3) return Array.Empty<float>();
            var v = new float[3];
            for (int k = 0; k < 3; k++)
                if (!float.TryParse(ps[k], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v[k]))
                    return Array.Empty<float>();
            return v;
        }
        var pos = V3(PartPos); var euDeg = V3(PartEuler);
        if (pos?.Length == 0 || euDeg?.Length == 0) { PartStatus.Text = "vetor precisa ser x,y,z"; return; }
        int? layer = null;
        if (!string.IsNullOrWhiteSpace(PartLayer.Text))
        {
            if (!int.TryParse(PartLayer.Text, out var lv) || lv < 0 || lv > 0xFFFF)
            { PartStatus.Text = "layer 0..65535"; return; }
            layer = lv;
        }
        bool sky = PartSky.IsChecked == true;
        if (pos == null && euDeg == null && layer == null && sky == info.IsSkybox)
        { PartStatus.Text = "nada a aplicar"; return; }

        var d = (byte[])_geomBytes.Clone();
        long p = en.PayloadOffset;
        if (pos != null)
            for (int k = 0; k < 3; k++)
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(p + 0x20 + 4 * k)), pos[k]);
        if (euDeg != null)
            for (int k = 0; k < 3; k++)
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((int)(p + 0x10 + 4 * k)),
                    euDeg[k] * (float)(Math.PI / 180));
        if (layer is { } lv2)
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan((int)(p + 0x04)), (ushort)lv2);
        if (sky != info.IsSkybox)
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan((int)(p + 0x02)), (ushort)(sky ? 1 : 0));
        if (!SaveGeomOverlay(d, $"level_part #{en.Index}"))
        { PartStatus.Text = "falha ao salvar"; return; }
        // re-parse the written copy to prove it round-trips
        var p2 = LevelParts.ReadPart(Map1File.Load(_geomRel, d), en);
        PartStatus.Text = $"objeto #{en.Index} salvo — reaberto: pos({p2.Px:0.##},{p2.Py:0.##},{p2.Pz:0.##}) " +
            $"eu°({p2.Ex * 180 / Math.PI:0.#},{p2.Ey * 180 / Math.PI:0.#},{p2.Ez * 180 / Math.PI:0.#}) " +
            $"layer={p2.Layer} sky={p2.IsSkybox}";
        _geomBytes = d;
        await ReloadViewport();
    }

    async void OnPartHide(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null) { PartStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        int i = PartSel.SelectedIndex;
        if (i < 0 || i >= _parts.Count) { PartStatus.Text = "selecione um objeto"; return; }
        var (en, _) = _parts[i];
        var d = (byte[])_geomBytes.Clone();
        // section walkers skip unknown types — 0xFFFFFFFF hides the object
        // structurally without shifting anything; undo restores the type
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan((int)en.DescOffset), 0xFFFFFFFF);
        if (!SaveGeomOverlay(d, $"hide level_part #{en.Index}"))
        { PartStatus.Text = "falha ao salvar"; return; }
        PartStatus.Text = $"objeto #{en.Index} oculto (type=0xFFFFFFFF) — Desfazer restaura";
        _geomBytes = d;
        PopulateParts();
        await ReloadViewport();
    }

    async void OnPartDup(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null) { PartStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        int i = PartSel.SelectedIndex;
        if (i < 0 || i >= _parts.Count) { PartStatus.Text = "selecione um objeto"; return; }
        var (en, _) = _parts[i];
        try
        {
            var f = Map1File.Load(_geomRel, _geomBytes);
            var d = LevelParts.DuplicatePart(f, en.Index);
            if (!SaveGeomOverlay(d, $"duplicate level_part #{en.Index}"))
            { PartStatus.Text = "falha ao salvar"; return; }
            _geomBytes = d;
            PopulateParts();
            if (PartSel.Items.Count > 0)
                PartSel.SelectedIndex = PartSel.Items.Count - 1;
            PartStatus.Text = $"objeto #{en.Index} duplicado no fim da seção — ajuste o TRS do clone";
            await ReloadViewport();
        }
        catch (Exception ex) { PartStatus.Text = "dup falhou: " + ex.Message; }
    }

    async void OnPartDel(object? s, RoutedEventArgs e)
    {
        if (_geomBytes == null || _geomRel == null) { PartStatus.Text = "carregue uma cena no viewport primeiro"; return; }
        int i = PartSel.SelectedIndex;
        if (i < 0 || i >= _parts.Count) { PartStatus.Text = "selecione um objeto"; return; }
        var (en, _) = _parts[i];
        try
        {
            var f = Map1File.Load(_geomRel, _geomBytes);
            var d = LevelParts.DeletePart(f, en.Index);
            if (!SaveGeomOverlay(d, $"delete level_part #{en.Index}"))
            { PartStatus.Text = "falha ao salvar"; return; }
            _geomBytes = d;
            PopulateParts();
            PartStatus.Text = $"objeto #{en.Index} removido da seção (físico) — histórico/undo pode restaurar o arquivo anterior";
            await ReloadViewport();
        }
        catch (Exception ex) { PartStatus.Text = "delete falhou: " + ex.Message; }
    }

    /// <summary>Writes a patched copy of the current texture bin to the
    /// overlay dir — same precedence rules as the geometry overlay.</summary>
    bool SaveTexOverlay(byte[] patched, string what)
        => _texRel != null && SaveBinOverlay(_texRel, patched, what);

    // --- texture export/replace -------------------------------------------
    // Upload entries hold linear texels; indexed formats re-encode to the
    // texture's own CLUT block (nearest RGB) so palettes are never touched.

    List<MapTexEdit.TexInfo> _texInfos = new();

    void PopulateTextures()
    {
        _texInfos.Clear();
        TexSel.Items.Clear();
        TexPath.Text = "";
        if (_texBytes == null || _texRel == null || _mapSet == null || _mapTex == null) return;
        var tf = Map1File.Load(_texRel, _texBytes);
        _texInfos = MapTexEdit.List(tf, _mapSet);
        foreach (var ti in _texInfos)
            TexSel.Items.Add($"#{ti.Entry.Index} psm{ti.Entry.Psm} {ti.W}x{ti.H}" +
                (ti.Tex0 == null ? " (sem TEX0)" : ti.PaletteSlot >= 0 ? $" pal{ti.PaletteSlot}" : " (pal?)"));
        TexStatus.Text = _texInfos.Count > 0
            ? $"{_texInfos.Count} uploads — selecione"
            : "sem uploads de textura";
    }

    void OnTexSel(object? s, SelectionChangedEventArgs e)
    {
        int i = TexSel.SelectedIndex;
        if (i < 0 || i >= _texInfos.Count) return;
        var ti = _texInfos[i];
        TexStatus.Text = $"upload #{ti.Entry.Index} @0x{ti.Entry.DataOffset:x} — " +
            $"psm{ti.Entry.Psm} fileDims {ti.Entry.W}x{ti.Entry.H} real {ti.W}x{ti.H}" +
            (ti.Tex0 is { } tx ? $" cbp={tx.Cbp} csa={tx.Csa} pal={ti.PaletteSlot}" : " sem TEX0");
    }

    void OnTexExport(object? s, RoutedEventArgs e)
    {
        int i = TexSel.SelectedIndex;
        if (_texBytes == null || _texRel == null || _mapTex == null || i < 0 || i >= _texInfos.Count)
        { TexStatus.Text = "selecione uma textura (carregue a cena no viewport)"; return; }
        var ti = _texInfos[i];
        var rgba = MapTexEdit.DecodeRgba(Map1File.Load(_texRel, _texBytes), _mapTex, ti);
        if (rgba == null)
        { TexStatus.Text = "sem TEX0 — textura indexada não referenciada por modelo"; return; }
        var dir = Path.Combine(Root, ".lab/out/tex");
        Directory.CreateDirectory(dir);
        var outp = Path.Combine(dir, $"{_sel?.Idx:x}_tex{ti.Entry.Index}.png");
        var argb = new uint[ti.W * ti.H];
        for (int k = 0; k < argb.Length; k++)
            argb[k] = (uint)(rgba[4 * k + 3] << 24 | rgba[4 * k] << 16 |
                             rgba[4 * k + 1] << 8 | rgba[4 * k + 2]);
        Png.Encode(outp, argb, ti.W, ti.H);
        TexPath.Text = outp;
        TexStatus.Text = $"exportado -> {outp} ({ti.W}x{ti.H})";
    }

    MapTexEdit.TexInfo? SelTexInfo()
    {
        int i = TexSel.SelectedIndex;
        return i >= 0 && i < _texInfos.Count ? _texInfos[i] : null;
    }

    void OnMPalApply(object? s, RoutedEventArgs e)
    {
        var ti = SelTexInfo();
        if (_texBytes == null || _texRel == null || _mapTex == null || ti == null)
        { TexStatus.Text = "selecione uma textura (carregue uma cena)"; return; }
        if (ti.Tex0 == null) { TexStatus.Text = "textura sem TEX0/paleta"; return; }
        if (!int.TryParse(MPalIdx.Text, out int idx) || idx < 0 || idx > 255)
        { TexStatus.Text = "índice 0..255"; return; }
        var parts = (MPalColor.Text ?? "").Split(',');
        var ch = new byte[4];
        if (parts.Length != 4) { TexStatus.Text = "cor precisa ser r,g,b,a"; return; }
        for (int k = 0; k < 4; k++)
            if (!byte.TryParse(parts[k].Trim(), out ch[k]))
            { TexStatus.Text = "canal inválido (0..255)"; return; }
        var tf = Map1File.Load(_texRel, _texBytes);
        var d = (byte[])_texBytes.Clone();
        if (!MapTexEdit.WritePaletteColor(tf, _mapTex, ti, idx,
                ch[0], ch[1], ch[2], ch[3], d))
        { TexStatus.Text = "índice fora da CLUT ou sem paleta"; return; }
        if (!SaveBinOverlay(_texRel, d, $"map palette {idx}")) return;
        TexStatus.Text = $"CLUT[{idx}] = ({ch[0]},{ch[1]},{ch[2]},{ch[3]}) — overlay {_texRel}";
        _ = ReloadViewport();
    }

    void OnMPalTint(object? s, RoutedEventArgs e)
    {
        var ti = SelTexInfo();
        if (_texBytes == null || _texRel == null || _mapTex == null || ti == null)
        { TexStatus.Text = "selecione uma textura (carregue uma cena)"; return; }
        if (ti.Tex0 == null) { TexStatus.Text = "textura sem TEX0/paleta"; return; }
        var parts = (MPalTint.Text ?? "").Split(',');
        var m = new float[4];
        if (parts.Length != 4) { TexStatus.Text = "tint precisa ser r,g,b,a"; return; }
        for (int k = 0; k < 4; k++)
            if (!PF(parts[k], out m[k])) { TexStatus.Text = "multiplicador inválido"; return; }
        var tf = Map1File.Load(_texRel, _texBytes);
        var d = (byte[])_texBytes.Clone();
        int n = MapTexEdit.TintPalette(tf, _mapTex, ti, m[0], m[1], m[2], m[3], d);
        if (n == 0) { TexStatus.Text = "sem células de paleta p/ tingir"; return; }
        if (!SaveBinOverlay(_texRel, d, $"map palette tint ×{n}")) return;
        TexStatus.Text = $"{n} células CLUT tingidas ×({m[0]},{m[1]},{m[2]},{m[3]}) — overlay {_texRel}";
        _ = ReloadViewport();
    }

    async void OnTexImport(object? s, RoutedEventArgs e)
    {
        int i = TexSel.SelectedIndex;
        if (_texBytes == null || _texRel == null || _mapTex == null || i < 0 || i >= _texInfos.Count)
        { TexStatus.Text = "selecione uma textura (carregue a cena no viewport)"; return; }
        var path = TexPath.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        { TexStatus.Text = "PNG não encontrado — exporte primeiro ou indique o caminho"; return; }
        var ti = _texInfos[i];
        try
        {
            var (bgra, w, h) = Png.Decode(path);
            var tf = Map1File.Load(_texRel, _texBytes);
            var d = (byte[])_texBytes.Clone();
            var note = MapTexEdit.Replace(tf, _mapTex, ti, bgra, w, h, d);
            if (!SaveTexOverlay(d, $"texture #{ti.Entry.Index}"))
            { TexStatus.Text = "falha ao salvar"; return; }
            // re-decode the written copy to prove the replace took
            var back = MapTexEdit.DecodeRgba(Map1File.Load(_texRel, d), _mapTex, ti);
            TexStatus.Text = $"textura #{ti.Entry.Index} salva ({note})" +
                (back != null ? " — re-decodificou ok" : "");
            _texBytes = d;
            await ReloadViewport();
        }
        catch (Exception ex) { TexStatus.Text = $"import falhou: {ex.Message}"; }
    }

    async Task ReloadViewport()
    {
        if (_sel == null) return;
        OnViewport(null, new RoutedEventArgs());
        await Task.CompletedTask;
    }

    byte[]? _fieldEvBytes;

    /// <summary>One-line ATEL summary for a container bin (EV01/encounter —
    /// blob offset at +0x04).</summary>
    static string AtelSummary(byte[] d)
    {
        try
        {
            var at = AtelBlob.Load(d, AtelBlob.FindBlob(d));
            int ins = at.Disasm().Count();
            return $"ATEL {at.ScriptId} ({at.Creator}): {at.WorkerCount} workers, " +
                $"{at.ActorCount} actors, {ins} instruções";
        }
        catch (Exception ex) { return "ATEL: " + ex.Message; }
    }

    void OnFldDisasm(object? s, RoutedEventArgs e)
    {
        if (_fieldEvBytes == null) { FldAtelInfo.Text = "carregue 'Field + atores'"; return; }
        try
        {
            var at = AtelBlob.Load(_fieldEvBytes, AtelBlob.FindBlob(_fieldEvBytes));
            var sb2 = new System.Text.StringBuilder();
            sb2.AppendLine($"scriptId={at.ScriptId} creator={at.Creator} " +
                $"codeLen=0x{at.CodeLen:x} workers={at.WorkerCount} actors={at.ActorCount}");
            for (int i = 0; i < at.Workers.Count; i++)
            {
                var w = at.Workers[i];
                sb2.AppendLine($"worker{i}: eventType=0x{w.EventType:x} vars={w.VarCount} " +
                    $"funcs=[{string.Join(',', w.Funcs.Select(x => $"0x{x:x}"))}] " +
                    $"jumps=[{string.Join(',', w.Jumps.Select(x => $"0x{x:x}"))}]");
            }
            foreach (var (addr, op, operand) in at.Disasm())
                sb2.AppendLine($"0x{addr:x5}  {AtelBlob.OpName(op),-10}" +
                    (operand.HasValue ? $" 0x{operand:x4} ({operand})" : ""));
            var dst = Path.Combine(Root, ".lab/out",
                $"atel_{_fieldEvIdx:x4}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            AtomicWriteAllText(dst, sb2.ToString());
            FldAtelInfo.Text = $"disasm → {dst}";
        }
        catch (Exception ex) { FldAtelInfo.Text = "disasm falhou: " + ex.Message; }
    }

    // --- actor anim/bone-map editing ------------------------------------
    List<(int Key, int Index, long Off, int Value)> _bmEntries = new();
    List<(int Slot, int Index, long Off, int AnimId)> _daEntries = new();

    void PopulateAnimEdits()
    {
        _bmEntries.Clear(); _daEntries.Clear();
        BoneMapSel.Items.Clear();
        if (_actorBin == null) return;
        _bmEntries = _actorBin.BoneMapEntries();
        _daEntries = _actorBin.DefaultAnimEntries();
        foreach (var en in _bmEntries)
            BoneMapSel.Items.Add($"key 0x{en.Key:x} slot{en.Index} → osso {en.Value}");
        AnimEditStatus.Text = $"{_bmEntries.Count} bone-map slots, " +
            $"{_daEntries.Count} default-anims";
    }

    void OnBoneMapSel(object? s, SelectionChangedEventArgs e)
    {
        int i = BoneMapSel.SelectedIndex;
        if (i < 0 || i >= _bmEntries.Count) return;
        BoneMapVal.Text = _bmEntries[i].Value.ToString();
    }

    async void OnDefAnimApply(object? s, RoutedEventArgs e)
    {
        if (_actorBin == null || _actorRel == null) { AnimEditStatus.Text = "carregue um ator"; return; }
        var ps = (DefAnimEdit.Text ?? "").Split(',');
        if (ps.Length != 3 || !int.TryParse(ps[0], out int slot) ||
            !int.TryParse(ps[1], out int idx))
        { AnimEditStatus.Text = "slot,idx,animId"; return; }
        int id;
        var idTxt = ps[2].Trim();
        if (!int.TryParse(idTxt, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out id) &&
            !int.TryParse(idTxt, out id))
        { AnimEditStatus.Text = "animId inválido (hex ou dec)"; return; }
        var en = _daEntries.Where(x => x.Slot == slot && x.Index == idx).ToList();
        if (en.Count == 0) { AnimEditStatus.Text = $"sem entrada slot{slot},idx{idx}"; return; }
        var d = _actorBin.SetDefaultAnim(en[0].Off, id);
        if (!SaveBinOverlay(_actorRel, d, $"default anim slot{slot}[{idx}]=0x{id:x}"))
        { AnimEditStatus.Text = "falha ao salvar"; return; }
        _actorBin = ActorBin.Load(OverlayPath(_actorRel));
        PopulateAnimEdits();
        AnimEditStatus.Text = $"slot{slot}[{idx}] = 0x{id:x} — gravado";
        await ReloadViewport();
    }

    async void OnBoneMapApply(object? s, RoutedEventArgs e)
    {
        if (_actorBin == null || _actorRel == null) { AnimEditStatus.Text = "carregue um ator"; return; }
        int i = BoneMapSel.SelectedIndex;
        if (i < 0 || i >= _bmEntries.Count) { AnimEditStatus.Text = "selecione uma entrada"; return; }
        if (!int.TryParse(BoneMapVal.Text, out int v) || v < 0 || v > 0xFFFF)
        { AnimEditStatus.Text = "osso u16"; return; }
        var en = _bmEntries[i];
        var d = _actorBin.SetBoneMapValue(en.Off, v);
        if (!SaveBinOverlay(_actorRel, d, $"bone map key 0x{en.Key:x}[{en.Index}]={v}"))
        { AnimEditStatus.Text = "falha ao salvar"; return; }
        _actorBin = ActorBin.Load(OverlayPath(_actorRel));
        PopulateAnimEdits();
        AnimEditStatus.Text = $"key 0x{en.Key:x}[{en.Index}] = {v} — gravado";
        await ReloadViewport();
    }

    void OnAnimSpeed(object? s, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (float.TryParse(AnimSpeed.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) &&
            v > 0.01f && v <= 8f)
            Viewport.AnimSpeed = v;
    }

    // --- field (EV01 mapPoint) overlay edits -------------------------------
    // .lab/overlays/FinalFantasyX/field/{evIdx:x4}.json:
    //   {"event": idx, "points": {"3": {"d": [dx,dy,dz], "dh": rad}}}
    string FieldEditsPath() => OverlayPath($"field/{_fieldEvIdx:x4}.json");

    void LoadFieldEdits()
    {
        _fldEdits.Clear();
        var p = FieldEditsPath();
        if (!File.Exists(p)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("models", out var mds))
                foreach (var prop in mds.EnumerateObject())
                    if (int.TryParse(prop.Name, out var mi) &&
                        prop.Value.TryGetInt32(out var np))
                        _fldModelSwap[mi] = np;
            if (!doc.RootElement.TryGetProperty("points", out var pts)) return;
            foreach (var prop in pts.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var pt)) continue;
                var e = new float[4];
                var v = prop.Value;
                if (v.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    int k = 0;
                    foreach (var x in d.EnumerateArray()) { if (k < 3) e[k] = (float)x.GetDouble(); k++; }
                }
                if (v.TryGetProperty("dh", out var dh)) e[3] = (float)dh.GetDouble();
                _fldEdits[pt] = e;
            }
            if (_fldEdits.Count > 0)
                EditStatus.Text = $"{_fldEdits.Count} ponto(s) com edição de field/{_fieldEvIdx:x4}.json";
        }
        catch (Exception ex) { EditStatus.Text = $"field/{_fieldEvIdx:x4}.json ilegível: {ex.Message}"; }
    }

    void SaveFieldEdits()
    {
        var pts = new Dictionary<string, object>();
        foreach (var kv in _fldEdits.OrderBy(k => k.Key))
            pts[kv.Key.ToString()] = new { d = new[] { kv.Value[0], kv.Value[1], kv.Value[2] }, dh = kv.Value[3] };
        var mdl = new Dictionary<string, object>();
        foreach (var kv in _fldModelSwap.OrderBy(k => k.Key))
            mdl[kv.Key.ToString()] = kv.Value;
        SaveTextOverlay($"field/{_fieldEvIdx:x4}.json", JsonSerializer.Serialize(
            new { @event = _fieldEvIdx, points = pts, models = mdl },
            new JsonSerializerOptions { WriteIndented = true }), "field edits");
    }

    void OnFldModelSel(object? s, SelectionChangedEventArgs e)
    {
        int i = FldModelSel.SelectedIndex;
        if (i < 0 || i >= _fldModels.Count) return;
        FldModelPid.Text = _fldModelSwap.TryGetValue(i, out var sw)
            ? sw.ToString() : "";
    }

    async void OnFldModelEdit(object? s, RoutedEventArgs e)
    {
        int i = FldModelSel.SelectedIndex;
        if (_fldModels.Count == 0 || i < 0 || i >= _fldModels.Count)
        { EditStatus.Text = "carregue 'Field + atores' e selecione um slot"; return; }
        var txt = (FldModelPid.Text ?? "").Trim();
        if (txt.Length == 0) _fldModelSwap.Remove(i);
        else if (!int.TryParse(txt, out int np) || np < 0 || np > 65535)
        { EditStatus.Text = "pid inválido (0..65535)"; return; }
        else _fldModelSwap[i] = np;
        SaveFieldEdits();
        FldModelSel.Items[i] = $"slot {i}: pid {(_fldModelSwap.TryGetValue(i, out var sp2) ? $"{sp2} (era {_fldModels[i]})" : _fldModels[i].ToString())}";
        EditStatus.Text = txt.Length == 0
            ? $"slot {i}: swap removido — field/{_fieldEvIdx:x4}.json"
            : $"slot {i} → pid {txt} — field/{_fieldEvIdx:x4}.json (recarregue o field)";
        await Task.CompletedTask;
    }

    void UndoFldEdit()
    {
        if (_fldUndo.Count == 0) { EditStatus.Text = "nada para desfazer"; return; }
        var it = _fldUndo.Pop();
        _fldRedo.Push(it);
        if (it.before == null) _fldEdits.Remove(it.pt); else _fldEdits[it.pt] = it.before;
        SaveFieldEdits();
        EditStatus.Text = $"desfeito: ponto {it.pt} (recarregue o field)";
    }

    void RedoFldEdit()
    {
        if (_fldRedo.Count == 0) { EditStatus.Text = "nada para refazer"; return; }
        var it = _fldRedo.Pop();
        _fldUndo.Push(it);
        if (it.after == null) _fldEdits.Remove(it.pt); else _fldEdits[it.pt] = it.after;
        SaveFieldEdits();
        EditStatus.Text = $"refeito: ponto {it.pt} (recarregue o field)";
    }

    string EncEditsPath(int id) => Path.Combine(Root, ".lab/overlays/FinalFantasyX/edits", $"{id}.json");

    void LoadEncEdits(int id)
    {
        _encEdits.Clear();
        _encExtra.Clear(); _encRemove.Clear(); _encSwap.Clear();
        _encParty.Clear(); _encOther.Clear();
        var p = EncEditsPath(id);
        if (!File.Exists(p)) return;
        try
        {
            var ed = EncEdits.Parse(File.ReadAllText(p));
            foreach (var kv in ed.Deltas) _encEdits[kv.Key] = kv.Value;
            foreach (var x in ed.Extra)
                _encExtra.Add(new EncExtra { Monster = x.Monster,
                    Pos = x.Pos, Heading = x.Heading, Scale = x.Scale });
            _encRemove.UnionWith(ed.Remove);
            foreach (var kv in ed.Swaps) _encSwap[kv.Key] = kv.Value;
            foreach (var kv in ed.Party) _encParty[kv.Key] = kv.Value;
            foreach (var kv in ed.Other) _encOther[kv.Key] = kv.Value;
            if (_encEdits.Count > 0 || _encExtra.Count > 0 || _encRemove.Count > 0 || _encSwap.Count > 0)
                EditStatus.Text = $"edits/{id}.json: {_encEdits.Count} delta(s), {_encExtra.Count} extra, {_encRemove.Count} removido(s), {_encSwap.Count} troca(s)";
        }
        catch (Exception ex) { EditStatus.Text = $"edits/{id}.json ilegível: {ex.Message}"; }
    }

    bool ReadEditFields(out int slot, out float[] e)
    {
        slot = 0; e = new float[6] { 0, 0, 0, 0, 0, 1 };
        if (!int.TryParse(EditSlot.Text, out slot) || slot < 0 || slot > 7)
        { EditStatus.Text = "slot inválido (0..7)"; return false; }
        float F(TextBox t, float dflt) => PF(t.Text, out var v) ? v : dflt;
        e[0] = F(EditDX, 0); e[1] = F(EditDY, 0); e[2] = F(EditDZ, 0);
        e[4] = F(EditDH, 0);
        e[5] = F(EditScale, 1);
        return true;
    }

    void SelectEncSlot(int slot)
    {
        EditSlot.Text = slot.ToString();
        if (_encEdits.TryGetValue(slot, out var ed))
        {
            EditDX.Text = ed[0].ToString("0.##"); EditDY.Text = ed[1].ToString("0.##");
            EditDZ.Text = ed[2].ToString("0.##"); EditDH.Text = ed[4].ToString("0.##");
            EditScale.Text = ed[5].ToString("0.##");
        }
        else
        {
            EditDX.Text = EditDY.Text = EditDZ.Text = EditDH.Text = "0";
            EditScale.Text = "1";
        }
    }

    void PushEncEdit(int slot, float[] ed)
    {
        _undo.Push((++_opSeq, slot, _encEdits.TryGetValue(slot, out var old) ? (float[])old.Clone() : null, (float[])ed.Clone()));
        _redo.Clear();
    }

    List<EncExtra> SnapExtra() => _encExtra.Select(x => { var c = x.Clone(); c.Pos = (float[])x.Pos.Clone(); return c; }).ToList();

    EncStructSnap SnapStruct() => new()
    {
        Extra = SnapExtra(),
        Remove = new HashSet<int>(_encRemove),
        Swap = new Dictionary<int, int>(_encSwap),
        Party = _encParty.ToDictionary(k => k.Key, v => (float[])v.Value.Clone()),
        Other = _encOther.ToDictionary(k => k.Key, v => (float[])v.Value.Clone()),
    };

    void ApplyStruct(EncStructSnap it)
    {
        _encExtra.Clear(); _encExtra.AddRange(it.Extra.Select(x => x.Clone()));
        _encRemove.Clear(); _encRemove.UnionWith(it.Remove);
        _encSwap.Clear(); foreach (var kv in it.Swap) _encSwap[kv.Key] = kv.Value;
        _encParty.Clear(); foreach (var kv in it.Party) _encParty[kv.Key] = kv.Value;
        _encOther.Clear(); foreach (var kv in it.Other) _encOther[kv.Key] = kv.Value;
    }

    void PushStructEdit()
    {
        _structUndo.Push((++_opSeq, SnapStruct(), null!));
        _structRedo.Clear();
    }

    async void UndoEncEdit()
    {
        // whichever stack holds the newest op wins (structural vs slot edits)
        bool structTop = _structUndo.Count > 0 &&
            (_undo.Count == 0 || _structUndo.Peek().seq > _undo.Peek().seq);
        if (structTop)
        {
            var it = _structUndo.Pop();
            // redo entry = the post-mutation state we are leaving
            _structRedo.Push((it.seq, null!, SnapStruct()));
            ApplyStruct(it.b);
            SaveEncOverlay();
            EditStatus.Text = "desfeito: mudança estrutural (add/remove)";
            await ReloadEncounter();
            return;
        }
        if (_undo.Count == 0) { EditStatus.Text = "nada para desfazer"; return; }
        var it2 = _undo.Pop();
        _redo.Push(it2);
        ApplyEncEditState(it2.slot, it2.before);
        EditStatus.Text = $"desfeito: slot {it2.slot}";
        await ReloadEncounter();
    }

    async void RedoEncEdit()
    {
        bool structTop = _structRedo.Count > 0 &&
            (_redo.Count == 0 || _structRedo.Peek().seq > _redo.Peek().seq);
        if (structTop)
        {
            var it = _structRedo.Pop();
            _structUndo.Push((it.seq, SnapStruct(), null!));
            ApplyStruct(it.a);
            SaveEncOverlay();
            EditStatus.Text = "refeito: mudança estrutural";
            await ReloadEncounter();
            return;
        }
        if (_redo.Count == 0) { EditStatus.Text = "nada para refazer"; return; }
        var it2 = _redo.Pop();
        _undo.Push(it2);
        ApplyEncEditState(it2.slot, it2.after);
        EditStatus.Text = $"refeito: slot {it2.slot}";
        await ReloadEncounter();
    }

    /// <summary>Write the full edits overlay file locally — extra/remove keys
    /// live alongside the server's per-slot `actors` merges.</summary>
    void SaveEncOverlay()
    {
        if (_encEditId < 0) return;
        var actors = new Dictionary<string, object>();
        foreach (var slot in _encEdits.Keys.Union(_encSwap.Keys).OrderBy(x => x))
        {
            float[]? ed = _encEdits.GetValueOrDefault(slot);
            int? mon = _encSwap.TryGetValue(slot, out var sw) ? sw : null;
            actors[slot.ToString()] = new
            {
                position = new[] { ed?[0] ?? 0, ed?[1] ?? 0, ed?[2] ?? 0 },
                heading = ed?[4] ?? 0,
                scale = ed?[5] ?? 1,
                monster = mon,
            };
        }
        var extra = _encExtra.Select(x => new
        { monster = x.Monster, position = x.Pos, heading = x.Heading, scale = x.Scale });
        var party = _encParty.ToDictionary(k => k.Key.ToString(), v => v.Value);
        var other = _encOther.ToDictionary(k => k.Key.ToString(), v => v.Value);
        SaveTextOverlay($"edits/{_encEditId}.json", JsonSerializer.Serialize(
            new { actors, extra, remove = _encRemove.OrderBy(x => x), party, other },
            new JsonSerializerOptions { WriteIndented = true }), "enc edits");
    }

    void ApplyEncEditState(int slot, float[]? v)
    {
        if (v == null) _encEdits.Remove(slot); else _encEdits[slot] = (float[])v.Clone();
    }

    async void OnEncEditApply(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { EditStatus.Text = "carregue um encontro no viewport primeiro"; return; }
        if (!ReadEditFields(out var slot, out var ed)) return;
        PushEncEdit(slot, ed);
        _encEdits[slot] = ed;
        EditStatus.Text = $"slot {slot} aplicado (não salvo)";
        await ReloadEncounter();
    }

    async void OnEncEditSave(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { EditStatus.Text = "carregue um encontro no viewport primeiro"; return; }
        if (!ReadEditFields(out var slot, out var ed)) return;
        PushEncEdit(slot, ed);
        _encEdits[slot] = ed;
        // mirror the viewer contract: POST the delta, server merges into the
        // overlay JSON that /data serves back
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            slot,
            position = new[] { ed[0], ed[1], ed[2] },
            heading = ed[4],
            scale = ed[5],
        });
        try
        {
            if (!await EnsureServer()) { EditStatus.Text = "servidor não subiu"; return; }
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync($"{ServerBase}/api/edits/{_encEditId}", content);
            var txt = await resp.Content.ReadAsStringAsync();
            EditStatus.Text = resp.IsSuccessStatusCode
                ? $"salvo: slot {slot} em edits/{_encEditId}.json ({txt.Trim()})"
                : $"falhou ({(int)resp.StatusCode}): {txt.Trim()}";
            if (resp.IsSuccessStatusCode) Out($"edit enc {_encEditId} slot {slot}: {body}");
        }
        catch (Exception ex) { EditStatus.Text = $"erro ao salvar: {ex.Message}"; }
        await ReloadEncounter();
    }

    async void OnEncEditReset(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { EditStatus.Text = "carregue um encontro no viewport primeiro"; return; }
        if (!int.TryParse(EditSlot.Text, out var slot)) { EditStatus.Text = "slot inválido"; return; }
        PushEncEdit(slot, new float[6] { 0, 0, 0, 0, 0, 1 });
        _encEdits.Remove(slot);
        EditDX.Text = EditDY.Text = EditDZ.Text = EditDH.Text = "0";
        EditScale.Text = "1";
        try
        {
            if (await EnsureServer())
            {
                var body = System.Text.Json.JsonSerializer.Serialize(new
                { slot, position = new[] { 0f, 0f, 0f }, heading = 0f, scale = 1f });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await Http.PostAsync($"{ServerBase}/api/edits/{_encEditId}", content);
                EditStatus.Text = resp.IsSuccessStatusCode
                    ? $"slot {slot} resetado e salvo"
                    : $"reset local; POST falhou ({(int)resp.StatusCode})";
            }
            else EditStatus.Text = $"slot {slot} resetado (servidor off)";
        }
        catch (Exception ex) { EditStatus.Text = $"reset local; erro: {ex.Message}"; }
        await ReloadEncounter();
    }

    async void OnAddMonster(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { MonStatus.Text = "carregue um encontro primeiro"; return; }
        var txt = ExtraBox.Text?.Trim() ?? "";
        int mid;
        if (txt.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        { if (!int.TryParse(txt[2..], System.Globalization.NumberStyles.HexNumber, null, out mid)) { MonStatus.Text = "id inválido"; return; } }
        else if (!int.TryParse(txt, out mid)) { MonStatus.Text = "id inválido (decimal ou 0x hex)"; return; }
        int g = mid >> 12;
        if (g >= ErB.Groups.Length || Array.IndexOf(ErB.Groups[g], mid & 0xFFF) < 0)
        { MonStatus.Text = $"mon {mid}: fora de erB[{g}]"; return; }
        PushStructEdit();
        // spawn at the party centroid — visible and draggable right away
        _encExtra.Add(new EncExtra { Monster = mid, Pos = new float[] { 0, -40, 0 } });
        SaveEncOverlay();
        MonStatus.Text = $"extra {mid} adicionado — arraste no viewport para posicionar";
        await ReloadEncounter();
    }

    async void OnRemoveMonster(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { MonStatus.Text = "carregue um encontro primeiro"; return; }
        if (!int.TryParse(EditSlot.Text, out var slot)) { MonStatus.Text = "slot inválido"; return; }
        if (slot >= 1000 && (slot - 1000 < 0 || slot - 1000 >= _encExtra.Count))
        { MonStatus.Text = "slot extra inválido"; return; }
        PushStructEdit();
        if (slot >= 1000)
        {
            int mid = _encExtra[slot - 1000].Monster;
            _encExtra.RemoveAt(slot - 1000);
            MonStatus.Text = $"extra {mid} removido";
        }
        else
        {
            _encRemove.Add(slot);
            MonStatus.Text = $"slot {slot} escondido (overlay remove) — Ctrl+Z desfaz";
        }
        SaveEncOverlay();
        await ReloadEncounter();
    }

    async void OnSwapMonster(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { MonStatus.Text = "carregue um encontro primeiro"; return; }
        if (!int.TryParse(EditSlot.Text, out var slot) || slot < 0 || slot > 7)
        { MonStatus.Text = "slot inválido (0..7)"; return; }
        if (slot >= _encMons.Count) { MonStatus.Text = "slot sem monstro vanilla — use Adicionar"; return; }
        var txt = SwapBox.Text?.Trim() ?? "";
        int mid;
        if (txt.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        { if (!int.TryParse(txt[2..], System.Globalization.NumberStyles.HexNumber, null, out mid)) { MonStatus.Text = "id inválido"; return; } }
        else if (!int.TryParse(txt, out mid)) { MonStatus.Text = "id inválido (decimal ou 0x hex)"; return; }
        int g = mid >> 12;
        if (g >= ErB.Groups.Length || Array.IndexOf(ErB.Groups[g], mid & 0xFFF) < 0)
        { MonStatus.Text = $"mon {mid}: fora de erB[{g}]"; return; }
        PushStructEdit();
        if (mid == _encMons[slot]) _encSwap.Remove(slot);
        else _encSwap[slot] = mid;
        SaveEncOverlay();
        MonStatus.Text = mid == _encMons[slot]
            ? $"slot {slot} voltou ao vanilla {_encMons[slot]}"
            : $"slot {slot}: {_encMons[slot]} → {mid} — Ctrl+Z desfaz";
        await ReloadEncounter();
    }

    async void OnPartyEdit(object? s, RoutedEventArgs e)
    {
        if (_encEditId < 0) { MonStatus.Text = "carregue um encontro primeiro"; return; }
        if (!int.TryParse(PartyIdx.Text, out var idx) || idx < 0 || idx > 15)
        { MonStatus.Text = "índice inválido (0..15)"; return; }
        var parts = (PartyDelta.Text ?? "").Split(',');
        if (parts.Length != 3) { MonStatus.Text = "delta precisa ser dx,dy,dz"; return; }
        var v = new float[3];
        for (int k = 0; k < 3; k++)
            if (!PF(parts[k], out v[k])) { MonStatus.Text = "delta inválido"; return; }
        bool isParty = PartyGroup.SelectedIndex != 1;
        var map = isParty ? _encParty : _encOther;
        PushStructEdit();
        if (v.All(x => x == 0)) map.Remove(idx); else map[idx] = v;
        SaveEncOverlay();
        MonStatus.Text = $"{(isParty ? "party" : "other")}[{idx}] " +
            (map.ContainsKey(idx) ? $"delta ({v[0]:0.#},{v[1]:0.#},{v[2]:0.#})" : "resetado") +
            " — Ctrl+Z desfaz";
        await ReloadEncounter();
    }

    /// <summary>Re-runs the encounter load with the current overlay deltas.</summary>
    async Task ReloadEncounter()
    {
        if (_encEditId < 0) return;
        EncBox.Text = _encEditId.ToString();
        OnEncViewport(null, new RoutedEventArgs());
        await Task.CompletedTask;
    }

    void FillDatFields(ComboBox fieldBox, TextBox valBox,
        Particles.PppEdit.DatumEntry de, byte[] d)
    {
        fieldBox.Items.Clear();
        foreach (var f in Particles.PppEdit.DatumOps[de.Op].Fields)
            fieldBox.Items.Add($"{f.Name} ({f.Type})");
        if (fieldBox.Items.Count > 0)
        {
            fieldBox.SelectedIndex = 0;
            var f = Particles.PppEdit.DatumOps[de.Op].Fields[0];
            valBox.Text = Particles.PppEdit.ReadField(d, de.Off, f);
        }
    }

    void ShowDatField(ComboBox fieldBox, TextBox valBox,
        List<Particles.PppEdit.DatumEntry> list, int rec, byte[]? d)
    {
        if (d == null || rec < 0 || rec >= list.Count) return;
        int fi = fieldBox.SelectedIndex;
        var de = list[rec];
        var fs = Particles.PppEdit.DatumOps[de.Op].Fields;
        if (fi < 0 || fi >= fs.Length) return;
        valBox.Text = Particles.PppEdit.ReadField(d, de.Off, fs[fi]);
    }

    bool ApplyDatField(List<Particles.PppEdit.DatumEntry> list, int rec,
        ComboBox fieldBox, TextBox valBox, byte[] d, out string desc)
    {
        desc = "";
        if (rec < 0 || rec >= list.Count) return false;
        var de = list[rec];
        var fs = Particles.PppEdit.DatumOps[de.Op].Fields;
        int fi = fieldBox.SelectedIndex;
        if (fi < 0 || fi >= fs.Length) return false;
        var f = fs[fi];
        if (!Particles.PppEdit.WriteField(d, de.Off, f, valBox.Text ?? "")) return false;
        desc = $"{de.Kind}.{f.Name} e{de.Entry}";
        return true;
    }

    void OnMagDatSel(object? s, SelectionChangedEventArgs e)
    {
        int i = MagDatSel.SelectedIndex;
        if (i < 0 || i >= _magDats.Count || _magicBytes == null) return;
        FillDatFields(MagDatField, MagDatVal, _magDats[i], _magicBytes);
    }

    void OnMagDatFieldSel(object? s, SelectionChangedEventArgs e) =>
        ShowDatField(MagDatField, MagDatVal, _magDats, MagDatSel.SelectedIndex, _magicBytes);

    void OnMagDatEdit(object? s, RoutedEventArgs e)
    {
        int i = MagDatSel.SelectedIndex;
        if (_magicBytes == null || _magicRel == null || i < 0 || i >= _magDats.Count)
        { PartFxStatus.Text = "carregue o efeito no viewport primeiro"; return; }
        var d = (byte[])_magicBytes.Clone();
        if (!ApplyDatField(_magDats, i, MagDatField, MagDatVal, d, out var desc))
        { PartFxStatus.Text = "valor inválido para o tipo do campo"; return; }
        if (!SaveBinOverlay(_magicRel, d, $"magic datum {desc}"))
        { PartFxStatus.Text = "falha ao salvar"; return; }
        _magicBytes = d;
        PartFxStatus.Text = $"{desc} = {MagDatVal.Text} — overlay {_magicRel}";
    }

    void OnPppDatSel(object? s, SelectionChangedEventArgs e)
    {
        int i = PppDatSel.SelectedIndex;
        if (i < 0 || i >= _pppDats.Count || _geomBytes == null) return;
        FillDatFields(PppDatField, PppDatVal, _pppDats[i], _geomBytes);
    }

    void OnPppDatFieldSel(object? s, SelectionChangedEventArgs e) =>
        ShowDatField(PppDatField, PppDatVal, _pppDats, PppDatSel.SelectedIndex, _geomBytes);

    async void OnPppDatEdit(object? s, RoutedEventArgs e)
    {
        int i = PppDatSel.SelectedIndex;
        if (_geomRel == null || _geomBytes == null || i < 0 || i >= _pppDats.Count)
        { PppStatus.Text = "carregue uma cena com PPP"; return; }
        var d = (byte[])_geomBytes.Clone();
        if (!ApplyDatField(_pppDats, i, PppDatField, PppDatVal, d, out var desc))
        { PppStatus.Text = "valor inválido para o tipo do campo"; return; }
        if (!SaveGeomOverlay(d, $"ppp datum {desc}"))
        { PppStatus.Text = "falha ao salvar"; return; }
        _geomBytes = d;
        PppStatus.Text = $"{desc} = {PppDatVal.Text} — overlay {_geomRel}";
        await ReloadViewport();
    }

    void OnMagColSel(object? s, SelectionChangedEventArgs e)
    {
        int i = MagColSel.SelectedIndex;
        if (i < 0 || i >= _magCols.Count || _magicBytes == null) return;
        var ce = _magCols[i];
        MagColHex.Text = $"{_magicBytes[ce.Off]:x2}{_magicBytes[ce.Off + 1]:x2}" +
            $"{_magicBytes[ce.Off + 2]:x2}{_magicBytes[ce.Off + 3]:x2}";
    }

    void OnMagColEdit(object? s, RoutedEventArgs e)
    {
        int i = MagColSel.SelectedIndex;
        if (_magicBytes == null || _magicRel == null || i < 0 || i >= _magCols.Count)
        { PartFxStatus.Text = "carregue o efeito no viewport primeiro"; return; }
        var hex = (MagColHex.Text ?? "").Trim().TrimStart('#');
        if (hex.Length != 8 || !uint.TryParse(hex,
                System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
        { PartFxStatus.Text = "cor inválida — use RRGGBBAA"; return; }
        var ce = _magCols[i];
        var d = (byte[])_magicBytes.Clone();
        d[ce.Off] = (byte)(rgba >> 24); d[ce.Off + 1] = (byte)(rgba >> 16);
        d[ce.Off + 2] = (byte)(rgba >> 8); d[ce.Off + 3] = (byte)rgba;
        if (!SaveBinOverlay(_magicRel, d, $"magic color datum {ce.Kind}.{ce.Field} e{ce.Entry}"))
        { PartFxStatus.Text = "falha ao salvar"; return; }
        _magicBytes = d;
        PartFxStatus.Text = $"{ce.Kind}.{ce.Field} e{ce.Entry} = #{hex} — overlay {_magicRel}";
        MagColSel.Items[i] = $"b{ce.Behavior}/p{ce.Program}/i{ce.Instr}/e{ce.Entry} {ce.Kind}.{ce.Field} #{hex}";
    }

    void OnMagEmitSel(object? s, SelectionChangedEventArgs e)
    {
        int i = MagEmitSel.SelectedIndex;
        if (i < 0 || i >= _magEmits.Count || _magicBytes == null) return;
        var en = _magEmits[i];
        var spec = Particles.PppEdit.EmitOps[en.Op];
        int cnt = _magicBytes[en.Off + spec.CntOff];
        int per = spec.PerOff >= 0 ? _magicBytes[en.Off + spec.PerOff] : -1;
        int pat = BitConverter.ToUInt16(_magicBytes, (int)en.Off + Particles.PppEdit.EmitPatOff);
        int prog = BitConverter.ToInt32(_magicBytes, (int)en.Off + spec.ProgOff);
        MagEmitCP.Text = per >= 0
            ? $"{cnt},{per},{pat},{prog}" : $"{cnt},,{pat},{prog}";
    }

    void OnMagEmitEdit(object? s, RoutedEventArgs e)
    {
        int i = MagEmitSel.SelectedIndex;
        if (_magicBytes == null || _magicRel == null || i < 0 || i >= _magEmits.Count)
        { PartFxStatus.Text = "carregue o efeito no viewport primeiro"; return; }
        var en = _magEmits[i];
        var spec = Particles.PppEdit.EmitOps[en.Op];
        var parts = (MagEmitCP.Text ?? "").Split(',');
        if (parts.Length == 0 || !int.TryParse(parts[0].Trim(), out int cnt) || cnt < 0 || cnt > 255)
        { PartFxStatus.Text = "count inválido (0..255)"; return; }
        int per = -1;
        if (spec.PerOff >= 0 && parts.Length > 1 && parts[1].Trim().Length > 0 &&
            (!int.TryParse(parts[1].Trim(), out per) || per < 0 || per > 255))
        { PartFxStatus.Text = (spec.PerMask ? "mask" : "period") + " inválido (0..255)"; return; }
        int pat = -1;
        if (parts.Length > 2 && parts[2].Trim().Length > 0 &&
            (!int.TryParse(parts[2].Trim(), out pat) || pat < 0 || pat > 65535))
        { PartFxStatus.Text = "pattern inválido (0..65535)"; return; }
        int prog = int.MinValue;
        if (parts.Length > 3 && parts[3].Trim().Length > 0 &&
            !int.TryParse(parts[3].Trim(), out prog))
        { PartFxStatus.Text = "program inválido (i32)"; return; }
        var d = (byte[])_magicBytes.Clone();
        d[en.Off + spec.CntOff] = (byte)cnt;
        if (spec.PerOff >= 0 && per >= 0) d[en.Off + spec.PerOff] = (byte)per;
        if (pat >= 0) BitConverter.GetBytes((ushort)pat).CopyTo(d, en.Off + Particles.PppEdit.EmitPatOff);
        if (prog != int.MinValue) BitConverter.GetBytes(prog).CopyTo(d, en.Off + spec.ProgOff);
        if (!SaveBinOverlay(_magicRel, d, $"magic emit datum e{en.Entry}"))
        { PartFxStatus.Text = "falha ao salvar"; return; }
        _magicBytes = d;
        PartFxStatus.Text = $"emit b{en.Behavior}/p{en.Program}/i{en.Instr}/e{en.Entry}: " +
            $"count={cnt}{(per >= 0 ? $" {(spec.PerMask ? "mask" : "period")}={per}" : "")}" +
            $"{(pat >= 0 ? $" pat={pat}" : "")}{(prog != int.MinValue ? $" prog={prog}" : "")} — overlay {_magicRel}";
        MagEmitSel.Items[i] = $"b{en.Behavior}/p{en.Program}/i{en.Instr}/e{en.Entry} {en.Kind} cnt={cnt}";
    }

    async void OnMagic(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(MagicBox.Text, out var id)) { Out("magic id invalido — número decimal"); return; }
        var p = await FetchToFile($"11/{id:x4}.bin");
        if (p == null) return;
        var d = File.ReadAllBytes(p);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"magic {id} -> 11/{id:x4}.bin  {d.Length}B");
        try { sb.Append(MagicBin.Describe($"11/{id:x4}.bin", d)); }
        catch (Exception ex) { sb.AppendLine($"parse falhou: {ex.Message}"); }
        sb.AppendLine();
        try { sb.Append(MagicParticles.Describe(d)); }
        catch (Exception ex) { sb.AppendLine($"particles falhou: {ex.Message}"); }
        EncData.Text = sb.ToString();
    }

    // "Efeito -> viewport": simulates the magic PPP standalone (FX-only
    // renderer, sprite textures from the bin, auto particle index) and
    // plays it looping in the viewport.
    async void OnMagicViewport(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(MagicBox.Text, out var id)) { Out("magic id invalido — número decimal"); return; }
        Status("carregando magia…");
        var p = await FetchToFile($"11/{id:x4}.bin");
        if (p == null) return;
        var d = File.ReadAllBytes(p);
        var headers = new List<int>();
        var gs = new Gs();
        var sys = MagicParticles.FromMagicBin(d, -1, gs, headers);
        _magicBytes = d; _magicRel = $"11/{id:x4}.bin";
        _magicPpp = sys?.L.Offs ?? 0;
        var fm = MagicBin.BuildFuncMap(d, MagicBin.FindFuncOffset(d));
        _magicFuncMap = fm.Count > 0 ? fm.ToArray() : null;
        _magEmits.Clear(); MagEmitSel.Items.Clear();
        if (_magicPpp > 0)
            foreach (var en in Particles.PppEdit.EmitDatumEntries(
                    d, _magicPpp, _magicFuncMap, synthEmitters: true))
            {
                _magEmits.Add(en);
                int cnt = d[en.Off + Particles.PppEdit.EmitOps[en.Op].CntOff];
                MagEmitSel.Items.Add($"b{en.Behavior}/p{en.Program}/i{en.Instr}/e{en.Entry} {en.Kind} cnt={cnt}");
            }
        _magDats.Clear(); MagDatSel.Items.Clear();
        if (_magicPpp > 0)
            foreach (var de in Particles.PppEdit.DatumEntries(
                    d, _magicPpp, _magicFuncMap, synthEmitters: true))
            {
                _magDats.Add(de);
                MagDatSel.Items.Add($"b{de.Behavior}/p{de.Program}/i{de.Instr}/e{de.Entry} {de.Kind}");
            }
        _magCols.Clear(); MagColSel.Items.Clear();
        if (_magicPpp > 0)
            foreach (var ce in Particles.PppEdit.ColorDatumEntries(
                    d, _magicPpp, _magicFuncMap, synthEmitters: true))
            {
                _magCols.Add(ce);
                MagColSel.Items.Add($"b{ce.Behavior}/p{ce.Program}/i{ce.Instr}/e{ce.Entry} {ce.Kind}.{ce.Field} " +
                    $"#{d[ce.Off]:x2}{d[ce.Off + 1]:x2}{d[ce.Off + 2]:x2}{d[ce.Off + 3]:x2}");
            }
        if (sys == null)
        {
            Out($"magia {id}: nenhum sistema de partículas (headers=[{string.Join(", ", headers.Select(h => $"0x{h:x}"))}])");
            Status("pronto");
            return;
        }
        var r = MapRenderer.Empty();
        r.FxGs = gs;
        // fit the camera on the effect: warm 45 frames, bounds in render space
        var tmp = new List<ParticleSim.DrawItem>();
        var mn = new float[] { 1e30f, 1e30f, 1e30f };
        var mx = new float[] { -1e30f, -1e30f, -1e30f };
        for (int f = 0; f < 45; f++)
        {
            tmp.Clear();
            sys.Step(1f, tmp);
            foreach (var it in tmp)
                for (int v = 0; v + 2 < it.V.Length; v += 13)
                    for (int c = 0; c < 3; c++)
                    { mn[c] = Math.Min(mn[c], it.V[v + c] * 0.1f); mx[c] = Math.Max(mx[c], it.V[v + c] * 0.1f); }
        }
        if (mn[0] < mx[0]) { for (int c = 0; c < 3; c++) { mn[c] *= 1.3f; mx[c] *= 1.3f; } r.FitTo(mn, mx); }
        else r.FitTo(new float[] { -50, -50, -50 }, new float[] { 50, 50, 50 });
        Viewport.SetContent(r);
        // fresh system for playback (the warm pass consumed this one); the
        // respawn keeps one-shot effects looping for preview
        var live = MagicParticles.FromMagicBin(d, -1, gs);
        if (live == null) { Status("pronto"); return; }
        Viewport.AttachParticles(live, 1f, () => MagicParticles.FromMagicBin(d, -1, gs));
        LoadFxOverlay(id);
        Out($"magia {id}: efeito no viewport — emitters={live.Emitters.Count} " +
            $"geos={live.L.Geos.Count} flipbooks={live.L.Flipbooks.Count} (loop)");
        Status("pronto");
        await Task.CompletedTask;
    }

    // ---- live effect editing (non-destructive overlay) ----
    int _fxMagicId = -1;
    byte[]? _magicBytes; string? _magicRel; int _magicPpp;
    int[]? _magicFuncMap;
    readonly List<Particles.PppEdit.EmitEntry> _magEmits = new();
    readonly List<Particles.PppEdit.ColorEntry> _magCols = new();
    readonly List<Particles.PppEdit.ColorEntry> _pppCols = new();
    readonly List<Particles.PppEdit.DatumEntry> _magDats = new();
    readonly List<Particles.PppEdit.DatumEntry> _pppDats = new();

    void OnFxApply(object? s, RoutedEventArgs e)
    {
        try
        {
            float PFx(TextBox t) => PF(t.Text, out var v)
                ? v : throw new FormatException($"'{t.Text}' não é número (use 1.5)");
            Viewport.FxSize = PFx(FxSize);
            Viewport.FxSpeed = PFx(FxSpeed);
            Viewport.FxEmit = PFx(FxEmit);
            Viewport.FxLife = PFx(FxLife);
            Viewport.FxTint = new[] { PFx(FxR), PFx(FxG), PFx(FxB), PFx(FxA) };
            PartFxStatus.Text = $"aplicado: ×{Viewport.FxSize} ×{Viewport.FxSpeed} emit=×{Viewport.FxEmit} vida=×{Viewport.FxLife} tint=({FxR.Text},{FxG.Text},{FxB.Text},{FxA.Text})";
        }
        catch (Exception ex) { PartFxStatus.Text = "valor inválido: " + ex.Message; }
    }

    void OnFxSave(object? s, RoutedEventArgs e)
    {
        if (_fxMagicId < 0) { PartFxStatus.Text = "carregue uma magia primeiro"; return; }
        OnFxApply(s, e);
        var dir = Path.Combine(Root, ".lab/overlays/magic");
        Directory.CreateDirectory(dir);
        var doc = new
        {
            magic = _fxMagicId, size = Viewport.FxSize, speed = Viewport.FxSpeed,
            emit = Viewport.FxEmit, life = Viewport.FxLife,
            tint = Viewport.FxTint, ts = DateTime.UtcNow.ToString("o"),
        };
        var path = Path.Combine(dir, $"{_fxMagicId:x4}.json");
        SaveLocalOverlay(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), $"fx {_fxMagicId:x4}");
        PartFxStatus.Text = $"overlay salvo: {path}";
    }

    void LoadFxOverlay(int id)
    {
        _fxMagicId = id;
        var path = Path.Combine(Root, ".lab/overlays/magic", $"{id:x4}.json");
        if (!File.Exists(path)) return;
        try
        {
            var j = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            FxSize.Text = j.GetProperty("size").GetSingle().ToString("0.###");
            FxSpeed.Text = j.GetProperty("speed").GetSingle().ToString("0.###");
            FxEmit.Text = (j.TryGetProperty("emit", out var em) ? em.GetSingle() : 1f).ToString("0.###");
            FxLife.Text = (j.TryGetProperty("life", out var li) ? li.GetSingle() : 1f).ToString("0.###");
            var t = j.GetProperty("tint");
            FxR.Text = t[0].GetSingle().ToString("0.###"); FxG.Text = t[1].GetSingle().ToString("0.###");
            FxB.Text = t[2].GetSingle().ToString("0.###"); FxA.Text = t[3].GetSingle().ToString("0.###");
            OnFxApply(null, null!);
            PartFxStatus.Text = $"overlay {path} reaplicado";
        }
        catch (Exception ex) { PartFxStatus.Text = $"overlay inválido: {ex.Message}"; }
    }

    async void OnServer(object? s, RoutedEventArgs e)
    {
        bool up = await EnsureServer();
        Out(up ? $"servidor OK em {ServerBase}\ncache: {Path.Combine(Root, ".lab/noclip-data")}\noverlays: {Path.Combine(Root, ".lab/overlays")}"
               : "servidor não subiu — veja .lab/noclip-server.log");
    }

    // ---- *_txt.bin help/name-help text tables ---------------------------
    // HelpText=8B (1 SimplifiableTextOffset) or NameHelpText=16B (2).
    // Slot s -> record field s*4 (u16 pool offset). Append-only edits.

    KernelBin.Table? _tt;
    string _ttName = "";
    readonly List<(int Idx, int Slot, string Old, string New)> _txtEdits = new();

    int TxtSlots => _tt == null ? 0 : _tt.EntryLength / 4;

    void OnTxtLoad(object? s, RoutedEventArgs e)
    {
        _ttName = ((TxtTableBox.SelectedItem as ComboBoxItem)?.Content?.ToString()) ?? "btl_txt.bin";
        var patched = Path.Combine(Root, ".lab/out/kernel", _ttName);
        var vanilla = Path.Combine(KernelDir(), _ttName);
        var src = File.Exists(patched) ? patched : vanilla;
        if (!File.Exists(src)) { TxtStatus.Text = $"não achei {_ttName}"; _tt = null; return; }
        var err = KernelBin.Load(File.ReadAllBytes(src), out var t);
        if (err != null) { TxtStatus.Text = "ERRO: " + err; _tt = null; return; }
        _tt = t; _txtEdits.Clear();
        TxtList.Items.Clear();
        for (int i = 0; i < t.EntryCount; i++)
        {
            string first = "";
            for (int k = 0; k < TxtSlots && first.Length == 0; k++)
                first = KernelBin.DecodeText(t.Pool, t.U16(i, k * 4));
            TxtList.Items.Add($"[{i,3}] {first}");
        }
        TxtStatus.Text = $"{src} — {t.EntryCount} registros ×{t.EntryLength}B, pool {t.Pool.Length}B" +
            (File.Exists(patched) ? " (PATCHED)" : "");
    }

    void OnTxtSel(object? s, SelectionChangedEventArgs e)
    {
        if (_tt == null || TxtList.SelectedIndex < 0) return;
        int i = TxtList.SelectedIndex, sl = TxtSlot.SelectedIndex;
        if (sl < 0) sl = 0;
        if (sl >= TxtSlots) { TxtEdit.Text = ""; return; }
        TxtEdit.Text = KernelBin.DecodeText(_tt.Pool, _tt.U16(i, sl * 4));
    }

    void OnTxtApply(object? s, RoutedEventArgs e)
    {
        if (_tt == null || TxtList.SelectedIndex < 0) { TxtStatus.Text = "carregue e selecione"; return; }
        int i = TxtList.SelectedIndex, sl = TxtSlot.SelectedIndex < 0 ? 0 : TxtSlot.SelectedIndex;
        if (sl >= TxtSlots) { TxtStatus.Text = $"slot inválido (0..{TxtSlots - 1})"; return; }
        var old = KernelBin.DecodeText(_tt.Pool, _tt.U16(i, sl * 4));
        if (old.Contains("<C") || old.Contains("<FONT"))
        { TxtStatus.Text = "string contém control codes — edição destruiria placeholders"; return; }
        var want = TxtEdit.Text ?? "";
        if (want == old) { TxtStatus.Text = "sem mudança"; return; }
        var grown = KernelBin.SetText(_tt, i, sl * 4, want);
        if (grown == null)
        { TxtStatus.Text = "caractere fora do alfabeto US (ou pool >64K)"; return; }
        _txtEdits.Add((i, sl, old, want));
        _tt.Data = grown;
        _tt.Pool = grown[(_tt.DataOff + _tt.EntryCount * _tt.EntryLength)..];
        TxtList.Items[i] = $"[{i,3}] {want} *";
        TxtSaveBtn.IsEnabled = true;
        TxtStatus.Text = $"{_txtEdits.Count} edições pendentes (registro {i} slot {sl})";
    }

    void OnTxtSave(object? s, RoutedEventArgs e)
    {
        if (_tt == null || _txtEdits.Count == 0) return;
        var outDir = Path.Combine(Root, ".lab/out/kernel");
        Directory.CreateDirectory(outDir);
        var dst = Path.Combine(outDir, _ttName);
        AtomicWriteAllBytes(dst, _tt.Data);
        var ovDir = Path.Combine(Root, ".lab/overlays/kernel");
        Directory.CreateDirectory(ovDir);
        var ovPath = Path.Combine(ovDir, _ttName + ".edits.json");
        var doc = new { table = _ttName, patched = dst,
            count = _txtEdits.Count,
            edits = _txtEdits.Select(x => new { idx = x.Idx, slot = x.Slot,
                old = x.Old, @new = x.New, ts = DateTime.UtcNow.ToString("o") }) };
        SaveLocalOverlay(ovPath, JsonSerializer.Serialize(doc,
            new JsonSerializerOptions { WriteIndented = true }), $"txt {_ttName}");
        var err = KernelBin.Load(File.ReadAllBytes(dst), out var t2);
        TxtStatus.Text = $"salvo {dst} ({_txtEdits.Count} edições) — releitura: {err ?? "ok"}";
        _txtEdits.Clear();
    }

    // ---- kernel tables (command.bin / monmagic*.bin / item.bin) ----
    KernelBin.Table? _kt;
    bool _ktDirty;
    string _ktName = "";
    static int KtBase(string name) => name switch
    {
        "item.bin" => 0x2000, "monmagic1.bin" => 0x4000, "monmagic2.bin" => 0x6000,
        "a_ability.bin" => 0x8000, "important.bin" => 0xA000, "monster1.bin" => 0,
        "monster2.bin" => 101, "monster3.bin" => 181, "command.bin" => 0x3000,
        _ => 0,
    };
    static bool KtHex(string name) => name is "command.bin" or "item.bin"
        or "monmagic1.bin" or "monmagic2.bin" or "a_ability.bin" or "important.bin";
    static bool KtMon(string name) => name.StartsWith("monster");
    string _ktSource = "";
    readonly List<(int Idx, int Off, int Old, int New)> _ktEdits = new();
    readonly List<(int Idx, string Old, string New)> _ktNameEdits = new();

    static string KernelDir()
    {
        try
        {
            var env = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, ".lab/environment.json"))).RootElement;
            var root = env.GetProperty("assets_root").GetString() ?? "";
            return Path.Combine(root, "ffx_ps2/ffx/master/new_uspc/battle/kernel");
        }
        catch { return ""; }
    }

    void OnKernelLoad(object? s, RoutedEventArgs e)
    {
        _ktName = ((KernelTableBox.SelectedItem as ComboBoxItem)?.Content?.ToString()) ?? "command.bin";
        var patched = Path.Combine(Root, ".lab/out/kernel", _ktName);
        var vanilla = Path.Combine(KernelDir(), _ktName);
        _ktSource = File.Exists(patched) ? patched : vanilla;
        if (!File.Exists(_ktSource))
        { KernelStatus.Text = $"não achei {_ktName} (out={patched}, vanilla={vanilla})"; _kt = null; return; }
        var d = File.ReadAllBytes(_ktSource);
        var err = KernelBin.Load(d, out var t);
        if (err != null) { KernelStatus.Text = "ERRO: " + err; _kt = null; return; }
        _kt = t; _ktEdits.Clear(); _ktNameEdits.Clear(); _ktDirty = false;
        KernelList.Items.Clear();
        for (int i = 0; i < t.EntryCount; i++)
            KernelList.Items.Add(KtHex(_ktName)
                ? $"[{i,3}] 0x{KtBase(_ktName) + i:x4}  {t.Name(i)}"
                : KtMon(_ktName)
                    ? $"[{i,3}] m{KtBase(_ktName) + i:000}  {t.Name(i)}"
                    : $"[{i,3}] #{i}  {t.Name(i)}");
        KernelSaveBtn.IsEnabled = File.Exists(patched);
        KernelStatus.Text = $"{_ktSource} — {t.EntryCount} registros ×{t.EntryLength}B, pool {t.Pool.Length}B" +
            (File.Exists(patched) ? " (PATCHED)" : "");
    }

    // per-stride editable fields: (label, record offset, size 1|2|4)
    static readonly (string L, int O, int Sz)[] CmdFields =
    {
        ("MP", 0x25, 1), ("Power", 0x2A, 1), ("Hits", 0x2B, 1), ("Element", 0x2D, 1),
        ("Accuracy", 0x29, 1), ("Formula", 0x28, 1), ("MoveRank", 0x24, 1), ("CritBonus", 0x27, 1),
    };
    static readonly (string L, int O, int Sz)[] MonFields =
    {
        ("HP", 0x14, 4), ("MP", 0x18, 4), ("Overkill", 0x1C, 4),
        ("STR", 0x20, 1), ("DEF", 0x21, 1), ("MAG", 0x22, 1), ("MDF", 0x23, 1), ("AGI", 0x24, 1),
    };
    // a_ability AutoAbility: is_sos + element u8 flags (Fahrenheit aability.cs)
    static readonly (string L, int O, int Sz)[] AbilityFields =
    {
        ("IsSOS", 0x10, 1), ("ElemStrike", 0x11, 1), ("ElemAbsorb", 0x12, 1),
        ("ElemIgnore", 0x13, 1), ("ElemResist", 0x14, 1), ("ElemWeak", 0x15, 1),
    };
    // important.bin KeyItem: item_type/value/icon/number u8 @0x10
    static readonly (string L, int O, int Sz)[] ImpFields =
    {
        ("ItemType", 0x10, 1), ("ItemValue", 0x11, 1), ("Icon", 0x12, 1), ("Number", 0x13, 1),
    };
    // panel.bin SphereGridNodeType: effect/ability/amount/icon u16 @0x10
    static readonly (string L, int O, int Sz)[] PanelFields =
    {
        ("SphereEffect", 0x10, 2), ("AbilityId", 0x12, 2), ("Amount", 0x14, 2), ("IconId", 0x16, 2),
    };
    // sphere.bin Sphere: type/activates u16 + range/special u8
    static readonly (string L, int O, int Sz)[] SphereFields =
    {
        ("Type", 0x08, 2), ("Activates", 0x0A, 2), ("Range", 0x0C, 1), ("SpecialRole", 0x0D, 1),
    };
    // ply_rom.bin PlyRom: gender + sphere-level curve + doom counter
    static readonly (string L, int O, int Sz)[] PlyRomFields =
    {
        ("Gender", 0x10, 1), ("SlvMultA", 0x11, 1), ("SlvMultB", 0x12, 1),
        ("SlvMultC", 0x13, 1), ("SlvReqMax", 0x14, 4), ("DoomCount", 0x2A, 1),
    };
    // w_name.bin WeaponName: 7 per-wielder name offsets @0x00 + hira @0x1C,
    // model_ids u16 ×7 @0x38 (Fahrenheit w_name.cs; name edit hits slot 0)
    static readonly (string L, int O, int Sz)[] WNameFields =
    {
        ("Model0", 0x38, 2), ("Model1", 0x3A, 2), ("Model2", 0x3C, 2),
        ("Model3", 0x3E, 2), ("Model4", 0x40, 2), ("Model5", 0x42, 2), ("Model6", 0x44, 2),
    };
    // ply_save.bin PlySave: base stats only (early offsets are packed-safe)
    static readonly (string L, int O, int Sz)[] PlySaveFields =
    {
        ("BaseHP", 0x04, 4), ("BaseMP", 0x08, 4), ("BaseSTR", 0x0C, 1), ("BaseDEF", 0x0D, 1),
        ("BaseMAG", 0x0E, 1), ("BaseMDF", 0x0F, 1), ("BaseAGI", 0x10, 1), ("BaseLUCK", 0x11, 1),
    };
    (string L, int O, int Sz)[] CurFields() => _ktName switch
    {
        _ when _kt?.EntryLength == 128 => MonFields,
        "a_ability.bin" => AbilityFields, "important.bin" => ImpFields,
        "panel.bin" => PanelFields, "sphere.bin" => SphereFields,
        "ply_rom.bin" => PlyRomFields, "ply_save.bin" => PlySaveFields,
        "w_name.bin" => WNameFields,
        _ => CmdFields,
    };

    TextBox[] FldBoxes() => new[] { FldMp, FldPow, FldHit, FldElem, FldAcc, FldFormula, FldRank, FldCrit };
    TextBlock[] FldLabels() => new[] { FldL0, FldL1, FldL2, FldL3, FldL4, FldL5, FldL6, FldL7 };

    void OnKernelSel(object? s, SelectionChangedEventArgs e)
    {
        if (_kt == null || KernelList.SelectedIndex < 0) return;
        int i = KernelList.SelectedIndex;
        var rr = _kt.Record(i).ToArray();
        ReadOnlySpan<byte> r = rr;
        bool mon = _kt.EntryLength == 128;
        FldName.Text = _kt.Name(i);
        // monster text: sensor @0x04, scan @0x0C; command text: desc @0x08
        string desc = mon ? KernelBin.DecodeText(_kt.Pool, _kt.U16(i, 0x0C))
            : _ktName is "sphere.bin" or "ply_save.bin" ? "" : _kt.Desc(i);
        var fields = CurFields();
        var labels = FldLabels(); var boxes = FldBoxes();
        for (int f = 0; f < fields.Length; f++)
        {
            labels[f].Text = fields[f].L;
            int off = fields[f].O;
            boxes[f].Text = fields[f].Sz == 4
                ? BitConverter.ToUInt32(r.Slice(off, 4)).ToString()
                : fields[f].Sz == 2 ? BitConverter.ToUInt16(r.Slice(off, 2)).ToString()
                : r[off].ToString();
        }
        string idStr = KtHex(_ktName) ? $"0x{KtBase(_ktName) + i:x4}"
            : KtMon(_ktName) ? $"m{KtBase(_ktName) + i:000}" : $"#{i}";
        KernelInfo.Text =
            $"[{i}] {idStr} {(_kt.Name(i))}\n" +
            $"desc: {desc}\n" +
            (mon
                ? $"hp={BitConverter.ToUInt32(r.Slice(0x14, 4))} mp={BitConverter.ToUInt32(r.Slice(0x18, 4))} " +
                  $"luk={r[0x25]} eva={r[0x26]} acc={r[0x27]} doom={r[0x77]}\n" +
                  $"propFlags=0x{BitConverter.ToUInt16(r.Slice(0x28, 2)):x4} absorb=0x{r[0x2B]:x2} " +
                  $"immune=0x{r[0x2C]:x2} resist=0x{r[0x2D]:x2} weak=0x{r[0x2E]:x2}\n" +
                  $"abilities: {string.Join(" ", Enumerable.Range(0, 16).Select(k => $"0x{BitConverter.ToUInt16(rr, 0x50 + k * 2):x4}"))}"
                : _kt.EntryLength is not (96 or 92)
                    ? $"record {_kt.EntryLength}B: {Convert.ToHexString(r[..Math.Min(24, r.Length)]).ToLowerInvariant()}"
                : $"anim1={BitConverter.ToInt16(r.Slice(0x10, 2))} anim2={BitConverter.ToInt16(r.Slice(0x12, 2))} " +
                  $"icon={r[0x14]} casterAnim={r[0x15]} menu=0x{r[0x16]:x2} char={r[0x19]}\n" +
                  $"target=0x{r[0x1A]:x2} allowed={r[0x1B]} misc={r[0x1C]:x2},{r[0x1D]:x2},{r[0x1E]:x2},{r[0x1F]:x2} " +
                  $"dmgFlgs=0x{r[0x20]:x2} stealGil={r[0x21]} preview=0x{r[0x22]:x2} dmgType=0x{r[0x23]:x2}\n" +
                  $"shatter={r[0x2C]} odCat={r[0x58]} buffVal={r[0x59]}");
    }

    /// <summary>Float text field: invariant first ("1.5"), then current
    /// culture ("1,5") — ambient parse alone reads "1.5" as 15 on pt-BR.</summary>
    static bool PF(string t, out float v) =>
        float.TryParse(t.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v) ||
        float.TryParse(t.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out v);

    static int ParseNum(string t)
    {
        t = t.Trim();
        return t.StartsWith("0x") ? Convert.ToInt32(t, 16) : int.Parse(t);
    }

    void OnKernelApply(object? s, RoutedEventArgs e)
    {
        if (_kt == null || KernelList.SelectedIndex < 0) return;
        int i = KernelList.SelectedIndex;
        var boxes = FldBoxes(); var fields = CurFields();
        try
        {
            for (int f = 0; f < fields.Length; f++)
            {
                int off = fields[f].O, sz = fields[f].Sz;
                long val = ParseNum(boxes[f].Text);
                long max = sz == 4 ? 0xFFFFFFFFL : sz == 2 ? 0xFFFF : 0xFF;
                if (val < 0 || val > max) { KernelStatus.Text = $"valor fora de u{sz * 8} em {fields[f].L}"; return; }
                int rec = _kt.RecordOff(i);
                long old = sz == 4 ? BitConverter.ToUInt32(_kt.Data, rec + off)
                    : sz == 2 ? BitConverter.ToUInt16(_kt.Data, rec + off) : _kt.Data[rec + off];
                if (old == val) continue;
                if (sz == 4) BitConverter.TryWriteBytes(_kt.Data.AsSpan(rec + off), (uint)val);
                else if (sz == 2) _kt.SetU16(i, off, (ushort)val);
                else _kt.SetU8(i, off, (byte)val);
                _ktEdits.Add((i, off, (int)old, (int)val));
            }
            var wantName = FldName.Text ?? "";
            if (wantName != _kt.Name(i))
            {
                var grown = KernelBin.Rename(_kt, i, wantName);
                if (grown == null)
                { KernelStatus.Text = "nome contém caractere fora do alfabeto US (ou pool cheio)"; return; }
                _ktNameEdits.Add((i, _kt.Name(i), wantName));
                _kt.Data = grown;
                int recEnd = _kt.DataOff + _kt.EntryCount * _kt.EntryLength;
                _kt.Pool = grown[recEnd..];
            }
            KernelSaveBtn.IsEnabled = _ktEdits.Count > 0 || _ktNameEdits.Count > 0;
            KernelStatus.Text = $"{_ktEdits.Count + _ktNameEdits.Count} edições pendentes em memória (registro {i} atualizado)";
            KernelList.Items[i] = (KtHex(_ktName)
                ? $"[{i,3}] 0x{KtBase(_ktName) + i:x4}  {_kt.Name(i)}"
                : $"[{i,3}] m{KtBase(_ktName) + i:000}  {_kt.Name(i)}") + " *";
        }
        catch (Exception ex) { KernelStatus.Text = "valor inválido: " + ex.Message; }
    }

    void OnKernelSave(object? s, RoutedEventArgs e)
    {
        if (_kt == null || (_ktEdits.Count == 0 && _ktNameEdits.Count == 0 && !_ktDirty)) return;
        var outDir = Path.Combine(Root, ".lab/out/kernel");
        Directory.CreateDirectory(outDir);
        var dst = Path.Combine(outDir, _ktName);
        AtomicWriteAllBytes(dst, _kt.Data);
        // provenance overlay: one edits.json per table, append-only
        var ovDir = Path.Combine(Root, ".lab/overlays/kernel");
        Directory.CreateDirectory(ovDir);
        var ovPath = Path.Combine(ovDir, _ktName + ".edits.json");
        var entries = _ktEdits.Select(x => new { idx = x.Idx, off = $"0x{x.Off:x2}",
            old = x.Old, @new = x.New, ts = DateTime.UtcNow.ToString("o") }).ToList();
        var names = _ktNameEdits.Select(x => new { idx = x.Idx, old = x.Old, @new = x.New,
            ts = DateTime.UtcNow.ToString("o") }).ToList();
        var doc = new { table = _ktName, source = _ktSource, patched = dst,
            count = entries.Count + names.Count, edits = entries, names };
        SaveLocalOverlay(ovPath, System.Text.Json.JsonSerializer.Serialize(doc,
            new JsonSerializerOptions { WriteIndented = true }), $"kernel {_ktName}");
        // verify: reload the written file and re-check structure
        var err = KernelBin.Load(File.ReadAllBytes(dst), out var t2);
        KernelStatus.Text = $"salvo {dst} ({_ktEdits.Count + _ktNameEdits.Count} edições) — releitura: {err ?? "ok"}";
        _ktEdits.Clear(); _ktNameEdits.Clear(); _ktDirty = false;
        KernelSaveBtn.IsEnabled = false;
    }

    void OnKernelAdd(object? s, RoutedEventArgs e)
    {
        if (_kt == null) { KernelStatus.Text = "carregue uma tabela"; return; }
        int src = KernelList.SelectedIndex >= 0 ? KernelList.SelectedIndex : _kt.EntryCount - 1;
        var d2 = KernelBin.AddRecord(_kt, src);
        var err = KernelBin.Load(d2, out var t2);
        if (err != null) { KernelStatus.Text = "ERRO pós-add: " + err; return; }
        _kt = t2; _ktDirty = true;
        KernelList.Items.Add(KtHex(_ktName)
            ? $"[{t2.EntryCount - 1,3}] 0x{KtBase(_ktName) + t2.EntryCount - 1:x4}  {t2.Name(t2.EntryCount - 1)}"
            : KtMon(_ktName)
                ? $"[{t2.EntryCount - 1,3}] m{KtBase(_ktName) + t2.EntryCount - 1:000}  {t2.Name(t2.EntryCount - 1)}"
                : $"[{t2.EntryCount - 1,3}] #{t2.EntryCount - 1}  {t2.Name(t2.EntryCount - 1)}");
        KernelList.SelectedIndex = t2.EntryCount - 1;
        KernelSaveBtn.IsEnabled = true;
        KernelStatus.Text = $"registro {src} duplicado → #{t2.EntryCount - 1} " +
            $"({t2.EntryCount} registros) — Salvar patch grava";
    }

    void OnKernelRevert(object? s, RoutedEventArgs e)
    {
        _kt = null; _ktEdits.Clear();
        KernelSaveBtn.IsEnabled = false;
        OnKernelLoad(s, e);
    }
}
