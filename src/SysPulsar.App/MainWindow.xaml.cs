using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SysPulsar.App.Controls;
using SysPulsar.App.Usage;
using SysPulsar.Core;
using SysPulsar.Core.Models;
using WpfColor = System.Windows.Media.Color;

namespace SysPulsar.App;

/// <summary>
/// 2x2 レイアウトの常駐モニター。
/// 表示要素は メトリクス(CPU/メモリ/GPU/ネット) / プロセス＆イベント /
/// ディスク / AI Usage の 4 つで、「表示エリア」の領域数(1/2/2/4)ぶんの
/// スロットにこの順で流し込まれる。表示要素は右クリックメニューで切替可能。
/// 計測はバックグラウンドスレッド、描画は UI スレッド。
/// ドラッグ/リサイズ中は WM_ENTERSIZEMOVE/EXITSIZEMOVE で検出して描画を止める。
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxDisks = 10;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private static readonly WpfColor CCpu = Sparkline.Hex("#4fc3f7");
    private static readonly WpfColor CMem = Sparkline.Hex("#ba68c8");
    private static readonly WpfColor CGpu = Sparkline.Hex("#ef5350");
    private static readonly WpfColor CDown = Sparkline.Hex("#81c784");
    private static readonly WpfColor CUp = Sparkline.Hex("#fdd835");
    private static readonly WpfColor CDisk = Sparkline.Hex("#90a4ae");

    private readonly SystemMonitor _monitor = new();
    private readonly AppConfig _config;
    private readonly Dictionary<string, MetricRow> _rows = new();
    private readonly Dictionary<int, DiskRow> _diskRows = new();
    private ProcessTable _procTable = null!;
    private CriticalEventPanel _eventPanel = null!;
    private Grid _tl = null!;
    private Grid _bl = null!;
    private Grid _tr = null!;
    private Grid _br = null!;
    private UsagePanel _usagePanel = null!;
    private Usage.Settings _usageSettings = null!;
    private UsagePoller? _usagePoller;
    private DispatcherTimer? _usageTick;
    private TextBlock _diskPlaceholder = null!;

    /// <summary>表示エリア(11=1x1 / 12=1x2 / 21=2x1 / 22=2x2)。スロット数は 1/2/2/4。</summary>
    private int _layoutMode = 22;
    private readonly Dictionary<int, MenuItem> _layoutItems = new();

    /// <summary>半透明オーバーレイの不透明度(0.1〜1.0。1.0=不透明)。window-state.json に保存。</summary>
    private double _overlayOpacity = 1.0;
    /// <summary>パネル背景の不透明度(0.1〜1.0。1.0=不透明)。window-state.json に保存。</summary>
    private double _panelOpacity = 1.0;
    /// <summary>クリックスルー(マウス操作を背面に通す)。window-state.json に保存。</summary>
    private bool _clickThrough;
    private readonly Dictionary<double, MenuItem> _overlayItems = new();
    private readonly Dictionary<double, MenuItem> _panelItems = new();
    private MenuItem _clickThroughItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _trayClickThrough = null!;

    /// <summary>更新適用項目をメニューに挿入済みか(二重挿入防止)。</summary>
    private bool _updateOffered;

    /// <summary>表示要素の有効/無効(メトリクス/プロセス＆イベント/ディスク/AI Usage)。
    /// 有効な要素が固定順でスロットに流し込まれる。スロット数を超えた分は出ない。</summary>
    private bool _showMetrics = true;
    private bool _showProcesses = true;
    private bool _showDisks = true;
    private bool _showUsage = true;

    private volatile bool _stop;
    private volatile bool _dragging;
    private Thread? _samplerThread;
    private Thread? _namesThread;
    private Thread? _eventsThread;

    private bool _disksBuilt;
    private List<(int num, string label, List<(string Letter, string VolLabel)> parts)> _diskOrder = new();

    private System.Windows.Forms.NotifyIcon? _tray;
    private EventWaitHandle? _showEvent;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load();
        SetupDragBar();
        SetupTrayIcon(); // BuildLayout より先に(トレイのクリックスルー項目に初期状態を反映するため)
        BuildLayout();
        Loaded += (_, _) =>
        {
            RestoreWindowState();
            ApplyOverlay(); // 復元したオーバーレイの濃さを反映
            ApplyClickThrough(); // hwnd 作成後にクリックスルーを反映
            StartThreads();
        };
        // ×ボタンは終了ではなくトレイへ退避(常駐)。完全終了はトレイメニューから。
        Closing += (sender, e) =>
        {
            if (WindowState == System.Windows.WindowState.Normal)
                SaveWindowState();
            if (!_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
            }
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == System.Windows.WindowState.Minimized)
                HideToTray();
        };
        Closed += (_, _) =>
        {
            _stop = true;
            if (_mouseHook != IntPtr.Zero)
                UnhookWindowsHookEx(_mouseHook);
            _usageTick?.Stop();
            _usagePoller?.Dispose();
            _tray?.Dispose();
            _showEvent?.Dispose();
            _monitor.Dispose();
        };
    }

    // ---- タスクトレイ ----

    private void SetupTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => RestoreFromTray());
        // クリックスルー有効中はウィンドウ上の右クリックも背面に通るため、解除はここから行う
        _trayClickThrough = new System.Windows.Forms.ToolStripMenuItem("クリックスルー")
        {
            CheckOnClick = true,
        };
        _trayClickThrough.CheckedChanged += (_, _) => SetClickThrough(_trayClickThrough.Checked);
        menu.Items.Add(_trayClickThrough);
        menu.Items.Add("終了", null, (_, _) =>
        {
            _reallyExit = true;
            Close();
        });

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "SysPulsar",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        // 2 個目のインスタンスからの復帰信号を受け取る(多重起動禁止の受け側)
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, App.ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(RestoreFromTray), null, -1, false);
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    /// <summary>前回の位置・サイズを復元。初回や画面外の場合は中央表示。</summary>
    private void RestoreWindowState()
    {
        if (SysPulsar.App.WindowState.Load() is { } s)
        {
            Left = s.Left;
            Top = s.Top;
            Width = Math.Max(MinWidth, s.Width);
            Height = Math.Max(MinHeight, s.Height);
            Topmost = s.Topmost;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
    }

    // ---- レイアウト構築 ----

    private void BuildLayout()
    {
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左上: CPU / メモリ / GPU / イーサネット
        _tl = MakeBlock(0, 0);
        AddRow(_tl, "cpu", "CPU", [CCpu], 100.0);
        AddRow(_tl, "mem", "メモリ", [CMem], 100.0,
               device: $"{SysPulsar.Core.Metrics.MemoryMonitor.TotalGb:F1} GB");
        AddRow(_tl, "gpu", "GPU", [CGpu], 100.0);
        AddRow(_tl, "net", "イーサネット", [CDown, CUp], null, subLarge: true, subColor: CUp);

        // 右上: プロセス表(残り全部) + イベント件数(下・2行ぶんの固定高)
        _tr = MakeBlock(0, 1);
        _tr.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _tr.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _procTable = new ProcessTable(rows: 12);
        Grid.SetRow(_procTable, 0);
        _tr.Children.Add(_procTable);
        _eventPanel = new CriticalEventPanel();
        Grid.SetRow(_eventPanel, 1);
        _tr.Children.Add(_eventPanel);

        // 左下: ディスク(2 列 x 5 行。行は Online 判定の結果が来てから構築)
        _bl = MakeBlock(1, 0);
        _diskPlaceholder = new TextBlock
        {
            Text = "ディスク情報を取得中…",
            Foreground = MetricRow.FreezeBrush("#8a8a8a"),
            FontSize = 10.7,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _bl.Children.Add(_diskPlaceholder);

        // 右下: AI Usage(UsageWatcher 統合)
        _br = MakeBlock(1, 1);
        _usageSettings = Usage.Settings.Load();
        _usagePanel = new UsagePanel(_usageSettings);
        _br.Children.Add(_usagePanel);

        // 右クリックメニュー(どのパネル上でも表示。子要素から親へ辿って出る)
        Root.ContextMenu = ExternalTools.BuildContextMenu();
        Root.ContextMenu.Items.Add(new Separator());

        // 表示エリア(レイアウト)の切替。領域数は 1/2/2/4(1x1/1x2/2x1/2x2)。
        // 状態は window-state.json に保存し次回起動時に復元
        var saved = SysPulsar.App.WindowState.LoadRaw();
        _layoutMode = saved?.Layout is 11 or 12 or 21 or 22 ? saved.Layout : 22;
        if (saved != null)
        {
            _showMetrics = saved.ShowMetrics;
            _showProcesses = saved.ShowProcesses;
            _showDisks = saved.ShowDisks;
            _showUsage = saved.ShowUsage;
        }
        var layoutMenu = new MenuItem { Header = "表示エリア" };
        foreach (var (mode, label) in new[]
        {
            (11, "1×1 (領域 1)"), (12, "1×2 (領域 2)"),
            (21, "2×1 (領域 2)"), (22, "2×2 (領域 4)"),
        })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _layoutMode == mode };
            int m = mode;
            item.Click += (_, _) => SetLayoutMode(m);
            layoutMenu.Items.Add(item);
            _layoutItems[mode] = item;
        }
        Root.ContextMenu.Items.Add(layoutMenu);

        // 表示要素の切替(有効な要素が メトリクス→プロセス＆イベント→ディスク→AI Usage の
        // 順にスロットへ流し込まれる。表示エリアの領域数を超えた分は表示されない)
        var elementMenu = new MenuItem { Header = "表示要素" };
        elementMenu.Items.Add(MakeElementToggle("CPU など (メトリクス)", _showMetrics, v => _showMetrics = v));
        elementMenu.Items.Add(MakeElementToggle("プロセス＆イベント", _showProcesses, v => _showProcesses = v));
        elementMenu.Items.Add(MakeElementToggle("ドライブ (ディスク)", _showDisks, v => _showDisks = v));
        elementMenu.Items.Add(MakeElementToggle("AI Usage (Claude/Kimi)", _showUsage, v => _showUsage = v));
        Root.ContextMenu.Items.Add(elementMenu);

        // 常に手前に表示(状態は window-state.json に保存し次回起動時に復元)
        bool topmost = saved?.Topmost ?? false;
        Topmost = topmost;
        var topmostToggle = new MenuItem { Header = "常に手前に表示", IsCheckable = true, IsChecked = topmost };
        topmostToggle.Click += (_, _) =>
        {
            Topmost = topmostToggle.IsChecked;
            SaveWindowState();
        };
        Root.ContextMenu.Items.Add(topmostToggle);

        // 半透明オーバーレイ。背景(ウィンドウの下地)とパネルで濃さを個別に設定する
        // (10%〜100% の 10% 刻みで 100%=不透明。背景は Root の下地ブラシ、
        // パネルは各ブロックの Opacity)
        _overlayOpacity = NormalizeOpacity(saved?.OverlayOpacity);
        _panelOpacity = NormalizeOpacity(saved?.PanelOpacity);
        var overlayMenu = new MenuItem { Header = "半透明オーバーレイ" };
        overlayMenu.Items.Add(MakeOpacitySubmenu("背景 (ウィンドウ)", _overlayOpacity, _overlayItems, SetOverlayOpacity));
        overlayMenu.Items.Add(MakeOpacitySubmenu("パネル", _panelOpacity, _panelItems, SetPanelOpacity));
        Root.ContextMenu.Items.Add(overlayMenu);

        // クリックスルー(有効中は右クリックも背面に通るため、解除はトレイメニューから。
        // 状態は window-state.json に保存し次回起動時に復元)
        _clickThrough = saved?.ClickThrough ?? false;
        _clickThroughItem = new MenuItem
        {
            Header = "クリックスルー (マウス操作を背面に通す)",
            IsCheckable = true,
            IsChecked = _clickThrough,
        };
        _clickThroughItem.Click += (_, _) => SetClickThrough(_clickThroughItem.IsChecked);
        Root.ContextMenu.Items.Add(_clickThroughItem);
        _trayClickThrough.Checked = _clickThrough; // トレイ側にも初期状態を反映
        Root.ContextMenu.Items.Add(new Separator());

        // AI Usage の Claude / Kimi 個別の表示切替(デフォルト両方表示。
        // 状態は settings.json に保存し次回起動時に復元。
        // 両方非表示なら AI Usage 要素は出ず、そのぶん他の要素が詰めて表示される)
        Root.ContextMenu.Items.Add(MakeUsageToggle("Claude", "claude", _usageSettings.ShowClaude, v => _usageSettings.ShowClaude = v));
        Root.ContextMenu.Items.Add(MakeUsageToggle("Kimi", "kimi", _usageSettings.ShowKimi, v => _usageSettings.ShowKimi = v));
        Root.ContextMenu.Items.Add(new Separator());

        // 手動の更新確認(起動時の自動チェックと同じ流れ。更新があれば DL まで済ませて
        // メニュー先頭に適用項目を挿入、最新版/失敗はメッセージで通知)
        var checkItem = new MenuItem { Header = "更新を確認" };
        checkItem.Click += async (_, _) =>
        {
            checkItem.IsEnabled = false; // 連打防止(完了/失敗後に戻す)
            try { await CheckForUpdateAsync(manual: true); }
            finally { checkItem.IsEnabled = true; }
        };
        Root.ContextMenu.Items.Add(checkItem);

        // 前回の表示エリア・表示要素の状態を復元
        ApplyLayout();
    }

    /// <summary>現在のウィンドウ状態(位置・サイズ・Topmost・表示エリア・表示要素)を
    /// window-state.json に保存する。</summary>
    private void SaveWindowState() =>
        SysPulsar.App.WindowState.Save(new SysPulsar.App.WindowState
        {
            Left = Left, Top = Top, Width = Width, Height = Height,
            Topmost = Topmost, Layout = _layoutMode,
            ShowMetrics = _showMetrics, ShowProcesses = _showProcesses,
            ShowDisks = _showDisks, ShowUsage = _showUsage,
            OverlayOpacity = _overlayOpacity, ClickThrough = _clickThrough,
            PanelOpacity = _panelOpacity,
        });

    /// <summary>表示エリアを切り替える(右クリックメニュー「表示エリア」から)。</summary>
    private void SetLayoutMode(int mode)
    {
        _layoutMode = mode;
        foreach (var (m, item) in _layoutItems)
            item.IsChecked = m == mode;
        ApplyLayout();
        SaveWindowState();
    }

    /// <summary>背景/パネルの透けサブメニュー(10%〜100% の 10% 刻み排他選択)を作る。</summary>
    private static MenuItem MakeOpacitySubmenu(string header, double current,
        Dictionary<double, MenuItem> items, Action<double> setter)
    {
        var menu = new MenuItem { Header = header };
        for (int i = 1; i <= 10; i++)
        {
            double v = i / 10.0;
            var item = new MenuItem { Header = $"{i * 10}%", IsCheckable = true, IsChecked = current == v };
            double value = v;
            item.Click += (_, _) => setter(value);
            menu.Items.Add(item);
            items[v] = item;
        }
        return menu;
    }

    /// <summary>背景(ウィンドウの下地)の不透明度を切り替える(0.1〜1.0)。</summary>
    private void SetOverlayOpacity(double value)
    {
        _overlayOpacity = value;
        foreach (var (v, item) in _overlayItems)
            item.IsChecked = v == value;
        ApplyOverlay();
        SaveWindowState();
    }

    /// <summary>パネル背景の不透明度を切り替える(0.1〜1.0)。</summary>
    private void SetPanelOpacity(double value)
    {
        _panelOpacity = value;
        foreach (var (v, item) in _panelItems)
            item.IsChecked = v == value;
        ApplyOverlay();
        SaveWindowState();
    }

    /// <summary>クリックスルーを切り替える(右クリックメニュー/トレイメニューのどちらからも来る)。
    /// ウィンドウ側・トレイ側の両メニュー項目のチェック状態を同期する。
    /// 起動時の初期同期(トレイ項目への Checked 設定)では状態が変わらないので何もしない。</summary>
    private void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled)
            return;
        _clickThrough = enabled;
        _clickThroughItem.IsChecked = enabled;
        _trayClickThrough.Checked = enabled;
        ApplyClickThrough();
        SaveWindowState();
    }

    /// <summary>半透明オーバーレイを適用する。ウィンドウは常に AllowsTransparency
    /// (枠なし・背景 Transparent)で動くため、Root の下地ブラシと各ブロック
    /// (メトリクス/プロセス＆イベント/ドライブ/AI Usage)の Opacity を変えるだけで
    /// 背面のウィンドウが見える。背景は下地とドラッグバーの地色、パネルは
    /// 背景・文字・グラフごと透ける。100% は不透明。</summary>
    private void ApplyOverlay()
    {
        Root.Background = TintBrush("#141414", _overlayOpacity);
        // ドラッグバーの地色も背景設定に連動(タイトル・ボタンは不透明のまま)
        DragBar.Background = TintBrush("#1f1f1f", _overlayOpacity);
        foreach (var g in new[] { _tl, _tr, _bl, _br })
            g.Opacity = _panelOpacity;
    }

    /// <summary>透過設定の正規化(0.1〜1.0 の 10% 刻み。
    /// 範囲外や 0(旧バージョンのオフ)は 1.0=不透明にする)。</summary>
    private static double NormalizeOpacity(double? v) =>
        v is >= 0.1 and <= 1.0 ? Math.Round(v.Value * 10) / 10 : 1.0;

    /// <summary>オーバーレイの濃さ(0.1〜1.0)に応じた下地ブラシ。</summary>
    private static System.Windows.Media.Brush TintBrush(string hex, double opacity)
    {
        var b = new SolidColorBrush(Sparkline.Hex(hex)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    /// <summary>枠なしモードのドラッグバーのイベントを接続する。
    /// バーのドラッグで移動(DragMove は SC_MOVE ベースなので既存の
    /// WM_ENTERSIZEMOVE 検出によるドラッグ中の描画停止がそのまま効く)、
    /// ダブルクリックで最大化切替。右クリックはウィンドウ共通のメニューを開く。</summary>
    private void SetupDragBar()
    {
        DragBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == System.Windows.WindowState.Maximized
                    ? System.Windows.WindowState.Normal
                    : System.Windows.WindowState.Maximized;
                return;
            }
            DragMove();
        };
        DragBar.MouseRightButtonUp += (_, e) =>
        {
            // ContextMenu インスタンスの共有を避けるため明示的に開く
            if (Root.ContextMenu is { } menu)
            {
                menu.PlacementTarget = DragBar;
                menu.IsOpen = true;
            }
            e.Handled = true;
        };
        SetupDragBarButton(DragBarMinimize, "#3a3a3a",
            () => WindowState = System.Windows.WindowState.Minimized); // StateChanged でトレイ退避
        SetupDragBarButton(DragBarClose, "#c42b1c", Close); // Closing でトレイ退避
    }

    /// <summary>ドラッグバーのボタン(最小化/閉じる)にホバー色とクリック処理を付ける。
    /// MouseLeftButtonDown を Handled にしてバーの DragMove を発火させない。</summary>
    private static void SetupDragBarButton(TextBlock button, string hoverColor, Action action)
    {
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, _) => action();
        button.MouseEnter += (_, _) => button.Background = MetricRow.FreezeBrush(hoverColor);
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
    }

    /// <summary>表示要素の切替メニュー項目を作る。</summary>
    private MenuItem MakeElementToggle(string name, bool initial, Action<bool> setter)
    {
        var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = initial };
        item.Click += (_, _) =>
        {
            setter(item.IsChecked);
            ApplyLayout();
            SaveWindowState();
        };
        return item;
    }

    /// <summary>AI Usage のプロバイダ表示切替メニュー項目を作る。</summary>
    private MenuItem MakeUsageToggle(string name, string providerId, bool initial, Action<bool> setter)
    {
        var item = new MenuItem { Header = $"{name} を表示", IsCheckable = true, IsChecked = initial };
        item.Click += (_, _) =>
        {
            setter(item.IsChecked);
            _usageSettings.Save();
            ApplyLayout();
        };
        return item;
    }

    /// <summary>表示エリア(レイアウト)と表示要素の設定をグリッドに適用する。
    /// モードの領域数(1/2/2/4)ぶんのスロットに、有効な表示要素を固定順
    /// (メトリクス→プロセス＆イベント→ディスク→AI Usage)で流し込む。
    /// スロット数を超えた要素は出ない。空いた下段スロットは真上の要素を
    /// 縦に伸ばして埋める(2x2 で AI Usage 非表示時にプロセス領域が縦いっぱいに
    /// なる従来挙動もこれで実現される)。</summary>
    private void ApplyLayout()
    {
        _usagePanel.SetProviderVisible("claude", _usageSettings.ShowClaude);
        _usagePanel.SetProviderVisible("kimi", _usageSettings.ShowKimi);

        bool hasBottom = _layoutMode is 21 or 22;
        bool hasRight = _layoutMode is 12 or 22;

        Root.RowDefinitions[1].Height = new GridLength(hasBottom ? 1 : 0, GridUnitType.Star);
        Root.ColumnDefinitions[1].Width = new GridLength(hasRight ? 1 : 0, GridUnitType.Star);

        // モードのスロット(配置順)
        (int Row, int Col)[] slots = _layoutMode switch
        {
            11 => [(0, 0)],
            12 => [(0, 0), (0, 1)],
            21 => [(0, 0), (1, 0)],
            _ => [(0, 0), (0, 1), (1, 0), (1, 1)],
        };

        // 有効な表示要素(固定順)。AI Usage は Claude/Kimi 両方非表示なら要素自体なし
        bool usageVisible = _showUsage && (_usageSettings.ShowClaude || _usageSettings.ShowKimi);
        var elements = new List<Grid>(4);
        if (_showMetrics) elements.Add(_tl);
        if (_showProcesses) elements.Add(_tr);
        if (_showDisks) elements.Add(_bl);
        if (usageVisible) elements.Add(_br);

        // いったん全要素を隠してスパンを戻してから、スロットに流し込む
        var occupied = new Dictionary<(int Row, int Col), Grid>();
        foreach (var g in new[] { _tl, _tr, _bl, _br })
        {
            g.Visibility = Visibility.Collapsed;
            Grid.SetRowSpan(g, 1);
        }
        for (int i = 0; i < elements.Count && i < slots.Length; i++)
        {
            var (row, col) = slots[i];
            var g = elements[i];
            Grid.SetRow(g, row);
            Grid.SetColumn(g, col);
            g.Margin = col == 0 ? new Thickness(6, 3, 3, 3) : new Thickness(3, 3, 6, 3);
            g.Visibility = Visibility.Visible;
            occupied[(row, col)] = g;
        }
        // 空いた下段スロットは真上の要素を縦に伸ばして埋める
        if (hasBottom)
            for (int c = 0; c <= (hasRight ? 1 : 0); c++)
                if (occupied.TryGetValue((0, c), out var above) && !occupied.ContainsKey((1, c)))
                    Grid.SetRowSpan(above, 2);

        // モードに応じて最小サイズを緩める(小さいモードでは小さく畳めるように)。
        // 2x2 に戻したとき現在サイズが最小を下回っていれば WPF が自動で広げる
        (MinWidth, MinHeight) = _layoutMode switch
        {
            11 => (300.0, 280.0), // 領域 1
            12 => (560.0, 280.0), // 領域 2(横並び)
            21 => (300.0, 500.0), // 領域 2(縦並び)
            _ => (560.0, 500.0),  // 領域 4
        };
    }

    private Grid MakeBlock(int row, int col)
    {
        var block = new Grid
        {
            Margin = col == 0 ? new Thickness(6, 3, 3, 3) : new Thickness(3, 3, 6, 3),
        };
        Grid.SetRow(block, row);
        Grid.SetColumn(block, col);
        Root.Children.Add(block);
        return block;
    }

    private void AddRow(Grid block, string key, string name, WpfColor[] colors,
        double? fixedMax, string device = "", bool subLarge = false, WpfColor? subColor = null)
    {
        int i = block.RowDefinitions.Count;
        block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var row = new MetricRow(name, colors, fixedMax, device, subLarge, subColor);
        Grid.SetRow(row, i);
        block.Children.Add(row);
        _rows[key] = row;
    }

    // ---- スレッド ----

    private void StartThreads()
    {
        _samplerThread = new Thread(SampleLoop) { IsBackground = true, Name = "SysPulsar.Sampler" };
        _samplerThread.Start();
        _namesThread = new Thread(NamesLoop) { IsBackground = true, Name = "SysPulsar.Names" };
        _namesThread.Start();
        _eventsThread = new Thread(EventsLoop) { IsBackground = true, Name = "SysPulsar.Events" };
        _eventsThread.Start();

        // AI Usage ポーリング(120秒周期 + 429 バックオフ。通信失敗時は直前値を保持)
        var providers = new List<IUsageProvider>();
        if (_usageSettings.EnableClaude) providers.Add(new ClaudeProvider());
        if (_usageSettings.EnableKimi) providers.Add(new KimiProvider(_config.KimiApiKey));
        foreach (var p in providers) _usagePanel.RegisterProvider(p.Id);
        _usagePoller = new UsagePoller(providers, _usageSettings);
        _usagePoller.SnapshotUpdated += snap =>
        {
            try { Dispatcher.BeginInvoke(() => _usagePanel.UpdateSnapshot(snap)); }
            catch (InvalidOperationException) { /* 終了中 */ }
        };
        _usagePoller.Start();

        // リセットまでのカウントダウン表示の定期更新
        _usageTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _usageTick.Tick += (_, _) => _usagePanel.Tick();
        _usageTick.Start();

        // 自動更新チェック(起動時のみ。あれば DL まで済ませてメニューに出す)
        _ = CheckForUpdateAsync();
    }

    /// <summary>更新チェック。新バージョンがあれば DL・展開まで済ませ、
    /// 右クリックメニューの先頭に適用項目を挿入する(自動・手動で共通)。
    /// manual == true(右クリックメニュー「更新を確認」)のときは、
    /// 最新版 / 確認失敗もメッセージで通知する(自動チェックは黙殺)。</summary>
    private async Task CheckForUpdateAsync(bool manual = false)
    {
        var result = await UpdateChecker.CheckAndDownloadAsync(CancellationToken.None);
        if (_stop)
            return;
        if (result.Info == null)
        {
            if (manual)
            {
                try
                {
                    _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                        result.UpToDate
                            ? $"最新バージョンです (v{UpdateChecker.CurrentVersion})"
                            : "更新を確認できませんでした。ネットワーク接続を確認してください。",
                        "SysPulsar 更新確認", MessageBoxButton.OK,
                        result.UpToDate ? MessageBoxImage.Information : MessageBoxImage.Warning));
                }
                catch (InvalidOperationException) { /* 終了中 */ }
            }
            return;
        }
        var info = result.Info;
        if (_updateOffered)
            return; // 既にメニューに出ている(手動で再確認した場合など)
        _updateOffered = true;
        try
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                var menu = Root.ContextMenu;
                var item = new MenuItem
                {
                    Header = $"v{info.Version} に更新 (ダウンロード済み・クリックで適用)",
                    FontWeight = FontWeights.Bold,
                };
                item.Click += (_, _) =>
                {
                    UpdateChecker.LaunchUpdater(info);
                    _reallyExit = true; // ×ボタンと違いトレイに退避させず完全終了させる
                    Close();
                };
                menu.Items.Insert(0, item);
                menu.Items.Insert(1, new Separator());
            });
        }
        catch (InvalidOperationException) { /* 終了中 */ }
    }

    private void SampleLoop()
    {
        int interval = Math.Max(200, _config.IntervalMs);
        while (!_stop)
        {
            var t0 = Environment.TickCount64;
            Snapshot snap = _monitor.Sample(processLimit: 12);
            if (!_dragging)
            {
                try { Dispatcher.BeginInvoke(() => ApplySnapshot(snap)); }
                catch (InvalidOperationException) { /* 終了中 */ }
            }
            long elapsed = Environment.TickCount64 - t0;
            int wait = (int)Math.Max(0, interval - elapsed);
            if (wait > 0)
                Thread.Sleep(wait);
        }
    }

    private void NamesLoop()
    {
        int refreshMs = Math.Max(5, _config.NamesRefreshSec) * 1000;
        while (!_stop)
        {
            DeviceInfo info = _monitor.GetDeviceInfo(); // 遅い(WMI)。専用スレッドなので OK
            if (!_dragging)
            {
                try { Dispatcher.BeginInvoke(() => ApplyDeviceInfo(info)); }
                catch (InvalidOperationException) { }
            }
            for (int waited = 0; waited < refreshMs && !_stop; waited += 200)
                Thread.Sleep(200);
        }
    }

    /// <summary>システムログのイベント件数(全レベル)を 60 秒周期で確認する。</summary>
    private void EventsLoop()
    {
        while (!_stop)
        {
            IReadOnlyDictionary<byte, int>? counts = null;
            string? error = null;
            try
            {
                counts = CriticalEventPanel.QueryCounts(); // ブロックする。専用スレッドなので OK
            }
            catch (Exception ex)
            {
                error = "イベントログの読み取りに失敗: " + ex.Message;
            }
            if (!_dragging)
            {
                try { Dispatcher.BeginInvoke(() => _eventPanel.SetData(counts, error)); }
                catch (InvalidOperationException) { }
            }
            for (int waited = 0; waited < 60_000 && !_stop; waited += 200)
                Thread.Sleep(200);
        }
    }

    // ---- 描画 ----

    private static string FmtPct(double? v) => v is double d ? $"{d:F0} %" : "—";

    /// <summary>ディスク実速度。500MB/s 以上は "0.9GB/s" のように GB/s 表記。</summary>
    private static string FmtRate(double mbps) =>
        mbps >= 500 ? $"{mbps / 1024.0:F1}GB/s" : $"{mbps:F1} MB/s";

    private void ApplySnapshot(Snapshot snap)
    {
        if (_rows.TryGetValue("cpu", out var cpu))
            cpu.Set(FmtPct(snap.Cpu.Load),
                    snap.Cpu.Ghz is double g ? $"{g:F2} GHz" : "",
                    snap.Cpu.Load);

        if (_rows.TryGetValue("mem", out var mem))
            mem.Set($"{snap.Mem.Percent:F0} %", $"{snap.Mem.UsedGb:F1} GB", snap.Mem.Percent);

        if (_rows.TryGetValue("gpu", out var gpu))
        {
            if (snap.Gpu is GpuSample g)
                gpu.Set(FmtPct(g.Load), g.Temp is double t ? $"{t:F0} °C" : "", g.Load);
            else
                gpu.Set("—", "", (double?)null);
        }

        if (_rows.TryGetValue("net", out var net))
        {
            double? d = snap.Net.DownMbps, u = snap.Net.UpMbps;
            net.Set(d is double dd ? $"↓ {dd:F1} Mbps" : "—",
                    u is double uu ? $"↑ {uu:F1} Mbps" : "",
                    d, u);
        }

        foreach (var (num, row) in _diskRows)
        {
            if (snap.Disks.TryGetValue(num, out var ds))
                row.Set(FmtPct(ds.Busy), ds.Mbps is double m ? FmtRate(m) : "", ds.Busy);
            else
                row.Set("—", "", (double?)null);
        }

        _procTable.SetRows(snap.Processes);
    }

    private void ApplyDeviceInfo(DeviceInfo info)
    {
        if (_rows.TryGetValue("cpu", out var cpu))
            cpu.SetDevice(info.Cpu);
        if (_rows.TryGetValue("mem", out var mem))
        {
            // WMI でモジュール情報が取れたときだけ上書き(取れなければ総容量表示のまま)
            if (info.Mem.Length > 0)
                mem.SetDevice(info.Mem);
        }
        if (_rows.TryGetValue("gpu", out var gpu))
            gpu.SetDevice(info.Gpu);
        if (_rows.TryGetValue("net", out var net))
            net.SetDevice(info.Net);

        BuildDiskRows(info.Disks, info.DiskLetters, info.DiskSpaces);
    }

    /// <summary>
    /// ディスク行の構築(最大 10 台)。左下ブロックを 2 列 x 5 行で使い、
    /// 左・右・左・右の順に詰める(セルは背景スパークライン付きの 2 行表示)。
    /// config に固定割り当てがあればそれを先頭に使い、残りは Online ディスクを
    /// ドライブレター順に自動追加。抜き差し(増減)があれば行を作り直す。
    /// 固定割り当てがなければ Online ディスクをドライブレター順に最大 10 台自動検出。
    /// 自動追加分の表示名はドライブレター+ボリュームラベル("C:システム"、
    /// 複数パーティションは "C:システム F:データ")。2 行目に空き/総量+空き率
    /// ("833/930GB 90%")を併記し、セルから溢れた分はクリップして隠す。
    /// レターが取れないディスクだけ従来の「ディスク N」表記にフォールバック。
    /// </summary>
    private void BuildDiskRows(Dictionary<int, string> models, IReadOnlyDictionary<int, string> letters,
        IReadOnlyDictionary<int, DiskSpaceInfo> spaces)
    {
        // 表示名はレター+ボリュームラベルの組のリスト("C:"+システム")。
        // 複数パーティションはレター毎に並べる。ラベルが無ければ "C:" のみ。
        // レター自体が取れなければ「ディスク N」。間隔制御のため文字列連結せず
        // パーツのまま DiskRow に渡す(label は変更検知用の文字列表現)。
        (string Label, List<(string Letter, string VolLabel)> Parts) AutoName(int n)
        {
            if (!letters.TryGetValue(n, out string? l) || l.Length == 0)
                return ($"ディスク {n}", [($"ディスク {n}", "")]);
            spaces.TryGetValue(n, out var sp);
            var parts = l.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => ($"{c}:", sp != null && sp.VolumeLabels.TryGetValue(c, out string? v) ? v : ""))
                .ToList();
            return (string.Join(" ", parts.Select(p => p.Item1 + p.Item2)), parts);
        }

        // 並びはドライブレター順(複合 "C:F:" は先頭レター C を基準)。
        // レターが取れないディスクは後ろに回し、従来どおり番号順。
        int LetterKey(int n) =>
            letters.TryGetValue(n, out string? l) && l.Length > 0 ? l[0] : 0x100 + n;

        var desired = new List<(int num, string label, List<(string Letter, string VolLabel)> parts)>();
        var fixedDisks = _config.Disks;
        if (fixedDisks.Count > 0)
        {
            var fixedNums = fixedDisks.Select(d => d.Number).ToHashSet();
            foreach (var e in fixedDisks.Take(MaxDisks))
                desired.Add((e.Number, e.Label, [(e.Label, "")]));
            // 固定以外の Online ディスクを空き枠へ自動追加(切断されれば消える)
            foreach (int n in models.Keys.Where(n => !fixedNums.Contains(n)).OrderBy(LetterKey))
            {
                if (desired.Count >= MaxDisks)
                    break;
                var a = AutoName(n);
                desired.Add((n, a.Label, a.Parts));
            }
        }
        else
        {
            foreach (int n in models.Keys.OrderBy(LetterKey).Take(MaxDisks))
            {
                var a = AutoName(n);
                desired.Add((n, a.Label, a.Parts));
            }
        }

        // 構成(台数・表示名)が変わっていなければモデル名と空き容量の更新だけ
        if (_disksBuilt && _diskOrder.Select(d => (d.num, d.label)).SequenceEqual(desired.Select(d => (d.num, d.label))))
        {
            foreach (var (num, _, _) in desired)
                if (_diskRows.TryGetValue(num, out var row))
                {
                    if (models.TryGetValue(num, out string? model) && model.Length > 0)
                        row.SetDevice(model);
                    var (text, warn) = FmtSpace(spaces, num);
                    row.SetSpace(text, warn);
                }
            return;
        }

        _disksBuilt = true;
        _diskPlaceholder.Visibility = Visibility.Collapsed;
        _bl.Children.Clear();
        _bl.RowDefinitions.Clear();
        _bl.ColumnDefinitions.Clear();
        _diskRows.Clear();
        _diskOrder = desired;

        // 2 列 x 最大 5 行(最大 10 台)。配置は左・右・左・右の順。
        // 行は常に 4 行以上確保してセル高を保つ(8 台以下なら 1/4、
        // 9 台以上で 5 行=1/5 に縮む。空き行は下に余る)
        _bl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rowCount = Math.Max(4, (desired.Count + 1) / 2);
        for (int r = 0; r < rowCount; r++)
            _bl.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < desired.Count; i++)
        {
            var (num, _, parts) = desired[i];
            var row = AddDiskRow(_bl, i / 2, i % 2, num, parts, models.GetValueOrDefault(num, ""));
            var (text, warn) = FmtSpace(spaces, num);
            row.SetSpace(text, warn);
        }
    }

    /// <summary>容量の表示("833GB/930GB 89%" / "46GB/1.82TB 2%" = 空き/総量+空き率)と
    /// 残量僅少フラグ。取れないときは空文字。1TB 以上の値は小数 2 桁の TB 表記。
    /// 空き率 10% 未満で warn=true(橙表示)にする。</summary>
    private static (string Text, bool Warn) FmtSpace(IReadOnlyDictionary<int, DiskSpaceInfo> spaces, int num)
    {
        if (!spaces.TryGetValue(num, out var s) || s.TotalGb <= 0)
            return ("", false);
        double pct = s.FreeGb / s.TotalGb * 100.0;
        return ($"{FmtCap(s.FreeGb)}/{FmtCap(s.TotalGb)} {pct:F0}%", pct < 10.0);
    }

    /// <summary>1TB 以上は TB 表記(10TB 以上は小数 1 桁、それ未満は小数 2 桁)、1TB 未満は整数 GB。</summary>
    private static string FmtCap(double gb) =>
        gb >= 10240.0 ? $"{gb / 1024.0:F1}TB" :
        gb >= 1024.0 ? $"{gb / 1024.0:F2}TB" : $"{gb:F0}GB";

    private DiskRow AddDiskRow(Grid block, int rowIndex, int column, int number,
        List<(string Letter, string VolLabel)> nameParts, string model)
    {
        var row = new DiskRow(nameParts, CDisk, model);
        Grid.SetRow(row, rowIndex);
        Grid.SetColumn(row, column);
        block.Children.Add(row);
        _diskRows[number] = row;
        return row;
    }

    // ---- ドラッグ/リサイズ検出・ヒットテスト ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
            _dragging = true;
        else if (msg == WmExitSizeMove)
            _dragging = false;
        else if (msg == WmNcHitTest)
            return HitTest(hwnd, lParam, ref handled);
        else if (msg == WmGetMinMaxInfo)
            LimitMaxSize(hwnd, lParam, ref handled);
        return IntPtr.Zero;
    }

    /// <summary>WM_NCHITTEST の処理。常時枠なしウィンドウのため、縁(6px)は
    /// リサイズ(HTLEFT 等)を自分で返す。ドラッグバーと表示エリアは通常処理。
    /// (クリックスルーはここではなく WS_EX_TRANSPARENT の付け外しで実現する。
    /// ULW ウィンドウでは HTTRANSPARENT が他プロセスのウィンドウに効かないため)</summary>
    private IntPtr HitTest(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        // lParam はスクリーン座標(符号付き 16bit x2。マルチモニタの負座標に対応)
        long lp = lParam.ToInt64();
        int sx = (short)(lp & 0xFFFF);
        int sy = (short)((lp >> 16) & 0xFFFF);
        if (!GetClientRect(hwnd, out Win32Rect rc))
            return IntPtr.Zero;
        var origin = new Win32Point();
        if (!ClientToScreen(hwnd, ref origin))
            return IntPtr.Zero;
        int left = origin.X, top = origin.Y;
        int right = origin.X + rc.Right, bottom = origin.Y + rc.Bottom;
        if (sx < left || sx >= right || sy < top || sy >= bottom)
            return IntPtr.Zero; // クライアント外は通常処理
        double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int border = Math.Max(1, (int)(6 * dpi));
        // 縁はリサイズ(最大化中は無効)
        if (WindowState != System.Windows.WindowState.Maximized)
        {
            bool l = sx - left < border, r = right - sx <= border;
            bool t = sy - top < border, b = bottom - sy <= border;
            if (l || r || t || b)
            {
                handled = true;
                if (t && l) return new IntPtr(HtTopLeft);
                if (t && r) return new IntPtr(HtTopRight);
                if (b && l) return new IntPtr(HtBottomLeft);
                if (b && r) return new IntPtr(HtBottomRight);
                if (t) return new IntPtr(HtTop);
                if (b) return new IntPtr(HtBottom);
                return new IntPtr(l ? HtLeft : HtRight);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>最大化時にタスクバーを避ける(枠なしウィンドウはそのままだと
    /// 画面全体に広がるため作業領域に収める)。最小サイズもここで伝える
    /// (このメッセージを自分で処理すると WPF の MinWidth/MinHeight が効かなくなるため)。</summary>
    private void LimitMaxSize(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref mi))
            return;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        mmi.MaxPosition = new Win32Point { X = mi.Work.Left - mi.Monitor.Left, Y = mi.Work.Top - mi.Monitor.Top };
        mmi.MaxSize = new Win32Point { X = mi.Work.Right - mi.Work.Left, Y = mi.Work.Bottom - mi.Work.Top };
        mmi.MinTrackSize = new Win32Point { X = (int)(MinWidth * dpi), Y = (int)(MinHeight * dpi) };
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
    }

    // ---- クリックスルー(WS_EX_TRANSPARENT の領域別付け外し)----

    private IntPtr _mouseHook;
    private bool _styleClickThrough; // hwnd に現在 WS_EX_TRANSPARENT が付いているか
    private LowLevelMouseProc? _mouseHookProc; // GC 回収防止のため保持

    /// <summary>クリックスルーを適用する。有効時は WH_MOUSE_LL フックを仕掛け、
    /// カーソル位置に応じて WS_EX_TRANSPARENT を付け外しする:
    /// 表示エリアでは付けて背面のウィンドウに操作を通し、ドラッグバーと
    /// リサイズ縁では外して操作可能にする。
    /// (ULW=AllowsTransparency ウィンドウは HTTRANSPARENT が他プロセスに
    /// 効かないため、WM_NCHITTEST 方式ではなくこの方式を取る)</summary>
    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return; // hwnd 未作成。Loaded で再適用される
        if (_clickThrough)
        {
            var pt = new Win32Point();
            GetCursorPos(ref pt);
            SetStyleClickThrough(hwnd, !IsInteractivePoint(hwnd, pt.X, pt.Y));
            if (_mouseHook == IntPtr.Zero)
            {
                _mouseHookProc = MouseHookProc;
                _mouseHook = SetWindowsHookEx(WhMouseLL, _mouseHookProc, GetModuleHandle(null), 0);
            }
        }
        else
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            SetStyleClickThrough(hwnd, false);
        }
    }

    /// <summary>カーソルが操作可能ゾーン(ドラッグバー or リサイズ縁)にあるか。
    /// false なら表示エリアかウィンドウ外(= 背面に通す側)。</summary>
    private bool IsInteractivePoint(IntPtr hwnd, int sx, int sy)
    {
        if (!GetClientRect(hwnd, out Win32Rect rc))
            return false;
        var origin = new Win32Point();
        if (!ClientToScreen(hwnd, ref origin))
            return false;
        int left = origin.X, top = origin.Y;
        int right = origin.X + rc.Right, bottom = origin.Y + rc.Bottom;
        if (sx < left || sx >= right || sy < top || sy >= bottom)
            return false; // ウィンドウ外
        double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int border = Math.Max(1, (int)(6 * dpi));
        if (sx - left < border || right - sx <= border || sy - top < border || bottom - sy <= border)
            return true; // リサイズ縁
        return sy - top < (int)(DragBar.ActualHeight * dpi); // ドラッグバー
    }

    /// <summary>WS_EX_TRANSPARENT の付け外し(変化があるときだけ API を呼ぶ)。</summary>
    private void SetStyleClickThrough(IntPtr hwnd, bool on)
    {
        if (_styleClickThrough == on)
            return;
        _styleClickThrough = on;
        long style = GetWindowLongPtr(hwnd, GwlpExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlpExStyle,
            new IntPtr(on ? style | WsExTransparent : style & ~WsExTransparent));
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt64() == WmMouseMove)
        {
            // MSLLHOOKSTRUCT の先頭は pt(スクリーン座標)
            var pt = Marshal.PtrToStructure<Win32Point>(lParam);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetStyleClickThrough(hwnd, !IsInteractivePoint(hwnd, pt.X, pt.Y));
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ---- クリックスルー・リサイズ・最大化用の Win32 ----

    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmMouseMove = 0x0200;
    private const int WhMouseLL = 14;
    private const int GwlpExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int MonitorDefaultToNearest = 2;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Win32Rect Monitor;
        public Win32Rect Work;
        public int Flags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Win32Point Reserved;
        public Win32Point MaxSize;
        public Win32Point MaxPosition;
        public Win32Point MinTrackSize;
        public Win32Point MaxTrackSize;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref Win32Point lpPoint);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn,
        IntPtr hMod, int dwThreadId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
