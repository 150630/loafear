using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using LoafEar.Config;
using LoafEar.Interop;
using LoafEar.Media;
using LoafEar.Overlay;
using LoafEar.Subs;
using LoafEar.Tray;
using LoafEar.UI;

namespace LoafEar;

public partial class MainWindow : Window
{
    private const int HotkeyToggle = 0x9001;
    private const int HotkeyPlay = 0x9002;
    private const int HotkeyPrev = 0x9003;
    private const int HotkeyNext = 0x9004;
    private const int HotkeyLoop = 0x9005;
    private const int HotkeyCollect = 0x9006;
    private const int HotkeyOffsetDown = 0x9007;
    private const int HotkeyOffsetUp = 0x9008;

    /// <summary>偏移微调一步多少毫秒。</summary>
    private const int OffsetStepMs = 100;

    /// <summary>偏移能调到多少（和播放页那个输入框同一个范围）。</summary>
    private const int OffsetLimitMs = 5000;

    /// <summary>关于页显示的版本号，跟 GitHub Release 的 tag 保持一致。</summary>
    private const string AppVersion = "v0.1";

    /// <summary>开源项目主页与反馈入口。</summary>
    private const string ProjectUrl = "https://github.com/150630/loafear";
    private const string IssuesUrl = "https://github.com/150630/loafear/issues";

    private static readonly Color Ink = Color.FromRgb(0x3E, 0x3A, 0x39);

    /// <summary>墨：只用在「选中 / 已播放 / 循环开着」这几处，面积都很小。</summary>
    private static readonly Color Accent = Color.FromRgb(0x3E, 0x3A, 0x39);

    /// <summary>关掉的控件退到这一级，界面里不出现第二种彩色。</summary>
    private static readonly Color Muted = Color.FromRgb(0x89, 0x89, 0x89);

    /// <summary>设计稿里收藏星 / 循环环关掉时用的浅灰（比 Muted 再退一档，但还看得见）。</summary>
    private static readonly Color Faint = Color.FromRgb(0xC9, 0xCA, 0xCA);

    /// <summary>
    /// 播放页那颗单句循环关掉时的颜色，直接取设计稿里那只箭头的 #DBDEE5。
    /// 比 Faint 还淡，所以只给循环用，星标仍走 Faint。
    /// </summary>
    private static readonly Color LoopIdle = Color.FromRgb(0xDB, 0xDE, 0xE5);

    /// <summary>开关按下时的极浅底，比描边浅一档，只在余光里能看出来。</summary>
    private static readonly Color AccentWash = Color.FromRgb(0xF7, 0xF8, 0xF8);

    private readonly AppConfig _config;
    private AudioPlayer _player = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _guardTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    /// <summary>拖滑条时每一帧都写盘太浪费，停手半秒再存一次。</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>轻提示停留多久后淡出。</summary>
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2400) };

    /// <summary>
    /// 调字幕宽度时字幕条临时变实（100% + 底板 + 灰描边），停手一会儿再恢复半透明。
    /// 时间给得比一次拖动的间隔长，慢慢拖也不会中途闪回。
    /// </summary>
    private readonly DispatcherTimer _measureTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    private SubtitleBar? _bar;

    /// <summary>当前打开的右键菜单。再次右键时先关掉旧的，免得 Popup 窗口堆叠。</summary>
    private ContextMenu? _quickMenu;

    /// <summary>未经偏移处理的字幕，改偏移时从它重新派生。</summary>
    private SubtitleTrack _rawTrack = SubtitleTrack.Empty();

    private SubtitleTrack _track = SubtitleTrack.Empty();
    private TaskbarInfo? _taskbar;
    private RECT _lastRect;
    private double _lastScale = -1;
    private int _cueIndex = -1;

    /// <summary>单句循环的决策核心（纯逻辑，离屏回归也是这一份）。</summary>
    private readonly CueLoop _loop = new();

    private List<LibraryItem> _library = new();

    /// <summary>收藏夹。文件就写在 config.json 旁边，跟着 exe 目录走。</summary>
    private readonly FavoriteStore _favorites =
        new(Path.Combine(AppConfig.ConfigDirectory, "favorites.json"));

    /// <summary>收藏页当前的筛选，默认「本节目」。</summary>
    private FavoriteFilter _favFilter = FavoriteFilter.Episode;

    /// <summary>「全部播放」的队列。空表示没在连播。</summary>
    private List<FavoriteCue> _favQueue = new();
    private int _favQueuePos = -1;

    /// <summary>连播中当前这句的句尾（毫秒）。过了就跳下一条。</summary>
    private double _favCueEndMs;

    /// <summary>离屏截图专用的假收藏；非 null 时收藏页只显示它。</summary>
    private List<FavoriteCue>? _favPreview;

    /// <summary>代码里改下拉选中项时置位，防止把「同步」当成用户选择。</summary>
    private bool _audioBoxSyncing;

    /// <summary>数值输入框的绑定表，统一处理提交与回写。</summary>
    private readonly List<NumberField> _fields = new();

    // 必须初始为 true：XAML 解析时控件属性会触发变更事件，
    // 从而在构造函数体执行前就触发回调，那时 _config 还是 null。
    private bool _suppressEvents = true;

    private bool _recordingHotkey;
    private Button? _recordingButton;
    private object? _recordOriginalContent;

    private bool _userHidden;

    /// <summary>老板键藏起来的时候，音频是不是我们暂停的 —— 只有是，回来才自动续播。</summary>
    private bool _bossPaused;

    private bool _exiting;
    private bool? _statusOk;
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _source;

    /// <summary>运行期初始化是否已经跑过。主界面可以一直不 Show，所以不能只靠 Loaded。</summary>
    private bool _runtimeReady;

    /// <summary>
    /// 会话是否已经恢复完（目录扫完、音频开好、字幕读好）。
    /// 只有它为 true 字幕条才会显示：字幕条一露头鼠标就会点上去，
    /// 而那时候界面线程必须已经空闲，否则任务栏会跟着卡。
    /// </summary>
    private bool _sessionReady;

    /// <summary>启动还没就绪时用户就右键了：先记住点在哪，就绪后立刻把菜单补开。</summary>
    private Point? _pendingMenuPoint;

    /// <summary>没有音频可放时，用一条测试字幕让字幕条先有内容。</summary>
    private bool _demoMode;

    private readonly Stopwatch _demoClock = new();
    private SubtitleTrack _demoTrack = SubtitleTrack.Empty();

    /// <summary>一个数值输入框：读写、范围、是否按百分比显示。</summary>
    private sealed class NumberField
    {
        public required TextBox Box { get; init; }
        public required Func<double> Get { get; init; }
        public required Action<double> Set { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
        public bool Percent { get; init; }
        public int Decimals { get; init; }

        public string Format(double value) =>
            Percent ? $"{value * 100:0}" : value.ToString($"F{Decimals}");

        public double Parse(string text)
        {
            var cleaned = new string(text.Where(c => char.IsDigit(c) || c is '.' or '-' or ',').ToArray())
                .Replace(',', '.');
            if (!double.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return Get();
            }

            return Percent ? value / 100.0 : value;
        }
    }

    /// <summary>诊断用：最近一次从下拉 / 目录里点开的节目名。</summary>
    private string? _lastPickedTitle;

    public MainWindow()
    {
        InitializeComponent();

        // 目录浮层：右对齐到按钮；倍速下拉：挂在按钮下面、左右居中。
        // 两个都得等模板套上以后才拿得到里面的 Popup，所以分开挂。
        ProgramPopup.CustomPopupPlacementCallback = PlaceProgramPopup;

        SpeedBox.Loaded += (_, _) =>
        {
            if (SpeedBox.Template.FindName("PART_Popup", SpeedBox) is Popup speedPopup)
            {
                speedPopup.Placement = PlacementMode.Custom;
                speedPopup.VerticalOffset = 0;
                speedPopup.CustomPopupPlacementCallback = PlaceSpeedPopup;
            }
        };

        _config = AppConfig.Load();
        _loop.Enabled = _config.LoopSingleCue;
        _favorites.Load();

        BuildFields();
        BindConfigToUi();
        BuildIcons();
        HookPlayer();

        _suppressEvents = false;

        // 设计稿里的标签是纯文字 + 下划线，用 RadioButton 模拟，页面靠切换 Visibility 显示
        SelectPage(0);
        RefreshFavorites();
        VersionText.Text = AppVersion;
        ConfigPathText.Text = AppConfig.ConfigPath;

        _player.Speed = _config.Speed;

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _config.Save();
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        _measureTimer.Tick += (_, _) =>
        {
            _measureTimer.Stop();
            _bar?.SetMeasureMode(false);
        };

        Loaded += OnLoaded;
    }

    // ------------------------------------------------------------------ 生命周期

    /// <summary>
    /// 启动与交互耗时探针。设了 LOAFEAR_TRACE=1 才写 ui-trace.log，平时完全不动磁盘。
    /// 字幕条是 explorer 的子窗口，界面线程一卡，任务栏和鼠标就会跟着卡，
    /// 所以这里的每个耗时都直接对应「卡多久」。
    /// </summary>
    private static readonly bool _traceOn =
        Environment.GetEnvironmentVariable("LOAFEAR_TRACE") == "1";

    private static readonly Stopwatch _clock = Stopwatch.StartNew();

    internal static void Trace(string message)
    {
        // 所有 Trace 调用点同时也是「卡顿记录器」的面包屑：
        // 一旦界面线程卡住，hang.log 里就能看到最后走到哪一步。
        Diag.StallWatch.Crumb(message);

        if (!_traceOn)
            return;

        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "ui-trace.log"),
                $"{_clock.ElapsedMilliseconds,6}ms {message}{Environment.NewLine}");
        }
        catch
        {
            // 记日志失败不影响主流程
        }
    }

    /// <summary>出错时留一份记录，方便事后排查为什么没加载、为什么没声音。</summary>
    private static void LogError(string context, Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "error.log"),
                $"{DateTime.Now:HH:mm:ss} [{context}] {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志本身失败就算了
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => InitializeRuntime();

    /// <summary>
    /// 启动时只把字幕条挂上去，主界面不显示 —— 它可以一直不 Show，
    /// 所以初始化不能写在 Loaded 里（不 Show 就永远等不到）。
    /// 幂等：从托盘真正打开窗口时会再调一次。
    /// </summary>
    private void InitializeRuntime()
    {
        if (_runtimeReady)
            return;

        _runtimeReady = true;
        Trace("init.begin");

        // 托盘整套（WinForms 初始化 + Shell_NotifyIcon）都放在自己的线程上，
        // 界面线程一点都不碰，否则 shell 一忙这里就要跟着卡几百毫秒。
        StartTray();

        // 窗口可以一直不 Show，但热键钩子和托盘都要用到句柄：
        // EnsureHandle() 会把 HWND 建出来而不显示窗口（直接读 Handle 在未 Show 时是零）。
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        RegisterHotkeys();

        EnsureBar();
        Trace($"bar created {_clock.ElapsedMilliseconds}ms");

        // 任务栏状态改由后台线程轮询：那一串 FindWindowEx / GetWindowRect /
        // SHQueryUserNotificationState 都是跨进程调 shell，shell 忙时能卡住几百毫秒，
        // 放在界面线程上就是「点了没反应」。
        TaskbarWatcher.Start();

        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _guardTimer.Tick += OnGuardTick;
        _guardTimer.Start();

        OnGuardTick(this, EventArgs.Empty);
        Trace($"guard done {_clock.ElapsedMilliseconds}ms");

        StartSessionRestore();
    }

    /// <summary>由 App 在启动时调用：挂好字幕条，但主界面保持隐藏。</summary>
    public void StartHidden() => InitializeRuntime();

    /// <summary>诊断用：字幕条是不是已经真的挂在任务栏上了（回归测试用它等时机）。</summary>
    internal bool OverlayReady =>
        _sessionReady && _bar is { IsAttached: true, IsOnScreen: true };

    /// <summary>诊断用：字幕条窗口句柄。</summary>
    internal IntPtr OverlayHandle => _bar?.WindowHandle ?? IntPtr.Zero;

    /// <summary>诊断用：字幕条的根元素，用于离屏渲染检查。</summary>
    internal FrameworkElement? OverlayRoot => _bar?.Content as FrameworkElement;

    /// <summary>诊断用：字幕条此刻的 WPF 状态，排查「窗口在但不显示」这类问题。</summary>
    internal string OverlayState()
    {
        if (_bar is null)
            return "bar=null";

        var source = PresentationSource.FromVisual(_bar);

        return $"visible={_bar.IsVisible} opacity={_bar.Opacity:0.00} " +
               $"size={_bar.ActualWidth:0}x{_bar.ActualHeight:0} " +
               $"left/top={_bar.Left:0},{_bar.Top:0} " +
               $"hwnd=0x{_bar.WindowHandle.ToInt64():X} " +
               $"onScreen={_bar.IsOnScreen} attached={_bar.IsAttached} " +
               $"presentationSource={(source is null ? "null" : "ok")} " +
               $"idle={_config.OpacityIdle:0.00} hover={_config.OpacityHover:0.00} " +
               $"transparentBg={_config.BackgroundTransparent} theme={_config.Theme}";
    }

    // ------------------------------------------------------------------ 诊断用（--jumpsim 单句循环回归）

    /// <summary>诊断用：直接指定音频与字幕起播，跳过目录扫描那套。</summary>
    internal bool LoadForDiag(string audio, string subtitle, TimeSpan start)
    {
        if (!LoadAudio(audio))
            return false;

        // 回归测试不许被上一次留下的偏移量干扰
        _config.SubtitleOffsetMs = 0;
        LoadSubtitle(subtitle);

        _loop.Enabled = _config.LoopSingleCue;
        _loop.Reset();
        _player.Position = start;
        _cueIndex = -1;
        RefreshBar();
        return true;
    }

    // ---- 收藏相关诊断口（--favsim 用，都能在真窗口上跑真代码） ----

    internal void ToggleFavoriteForDiag() => ToggleFavoriteCurrent();

    /// <summary>收藏夹当前内容，形如 E01-1#3,E01-1#6。</summary>
    internal string FavoriteStateForDiag() =>
        _favorites.Count == 0
            ? "(空)"
            : string.Join(",", _favorites.Items.Select(i => $"{i.Episode}#{i.CueIndex + 1}"));

    /// <summary>收藏页当前筛选下有几行。</summary>
    internal int FavoriteRowCountForDiag() => QueryFavorites().Count;

    internal void StartFavoriteQueueForDiag() => StartFavoriteQueue();

    /// <summary>诊断用：点一下收藏页那颗圆钮，走的是真正的 Click 处理链。</summary>
    internal void ClickFavPlayForDiag() => OnPlayAllFavorites(this, new RoutedEventArgs());

    /// <summary>诊断用：念出圆钮现在画的是哪一枚图 —— 直接读按钮上挂的标记，不重算一遍。</summary>
    internal string FavPlayStateForDiag() =>
        $"queue={_favQueuePos}/{_favQueue.Count} playing={_player.IsPlaying} " +
        $"glyph={FavPlayAllButton.Tag ?? "?"}";

    internal string QueueStateForDiag() =>
        $"pos={_favQueuePos}/{_favQueue.Count} cue={_cueIndex} " +
        $"pos={_player.Position.TotalSeconds:0.00}s end={_favCueEndMs / 1000:0.00}s " +
        $"playing={_player.IsPlaying} loop={_config.LoopSingleCue}";

    internal void NudgeOffsetForDiag(int deltaMs) => NudgeOffset(deltaMs);

    internal string OffsetStateForDiag() =>
        $"offset={_config.SubtitleOffsetMs:0}ms cues={_track.Count} box=\"{OffsetBox.Text}\"";

    internal string BossKeyStateForDiag() =>
        $"hidden={_userHidden} playing={_player.IsPlaying}";

    internal void ToggleBossKeyForDiag() => ToggleBossKey();

    /// <summary>诊断用：查看连续几句的起止时间，确认索引与位置对得上。</summary>
    internal string TrackInfoForDiag(int from, int count)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"offset={_config.SubtitleOffsetMs}ms cues={_track.Count}");

        for (var i = Math.Max(0, from); i < Math.Min(_track.Count, from + count); i++)
        {
            var cue = _track.Cues[i];
            sb.AppendLine($"  [{i,3}] {cue.Start.TotalSeconds,8:0.000} ~ {cue.End.TotalSeconds,8:0.000}  \"{cue.Text}\"");
        }

        return sb.ToString();
    }

    /// <summary>诊断用：开播。</summary>
    internal void PlayForDiag() => _player.Play();

    /// <summary>诊断用：压音量，回归测试会真的出声。</summary>
    internal void SetVolumeForDiag(double volume) => _player.Volume = volume;

    /// <summary>诊断用：切单句循环。</summary>
    internal void SetLoopForDiag(bool on) => SetLoop(on);

    /// <summary>诊断用：等同按一下「上一句 / 下一句」。</summary>
    internal void JumpForDiag(int delta) => JumpCue(delta);

    /// <summary>诊断用：位置 / 当前句 / 循环句 / 字幕条上真正显示的字。</summary>
    internal string CueStateForDiag()
    {
        var cue = _cueIndex >= 0 && _cueIndex < _track.Count ? _track.Cues[_cueIndex].Text : "-";
        var loopCue = _loop.Index >= 0 && _loop.Index < _track.Count ? _track.Cues[_loop.Index].Text : "-";
        var shown = _bar?.FindText().Main ?? "bar=null";

        return $"{_player.Position.TotalSeconds,8:0.000}s  cue={_cueIndex,3}  loop={_loop.Index,3}  " +
               $"句=\"{Trim(cue)}\" 循环=\"{Trim(loopCue)}\" 条=\"{Trim(shown)}\"";

        static string Trim(string value) =>
            value.Length > 26 ? value[..26] + "…" : value;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _uiTimer.Stop();
        _guardTimer.Stop();
        _saveTimer.Stop();
        UnregisterHotkeys();
        _source?.RemoveHook(WndProc);

        // 菜单是独立 Popup 窗口，不关掉会拖住进程退出
        if (_quickMenu is { IsOpen: true })
            _quickMenu.IsOpen = false;
        _quickMenu = null;

        if (_bar is not null)
        {
            _bar.RightClicked -= OnBarRightClicked;
            _bar.Detach();
            _bar.Close();
            _bar = null;
        }

        // 必须在 Dispose 之前取：Stop() 会把 reader 置空，之后就查不到位置了
        if (!_demoMode && _player.IsLoaded)
            _config.LastPositionMs = _player.Position.TotalMilliseconds;

        _player.Dispose();

        _config.Save();

        TrayHost.Stop();

        base.OnClosing(e);

        if (_exiting)
            Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
    }

    /// <summary>真正退出（托盘菜单调用）。</summary>
    public void RequestExit()
    {
        _exiting = true;
        Close();
    }

    /// <summary>从托盘唤起主界面。</summary>
    public void ShowMainWindow()
    {
        Diag.StallWatch.Crumb("ShowMainWindow: Show()");
        Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Diag.StallWatch.Crumb("ShowMainWindow: Activate()");
        Activate();

        // 托盘唤起的窗口常常被压在别的程序后面，用一次 Topmost 翻转把它提到最前
        Topmost = true;
        Topmost = false;
        Focus();
        Diag.StallWatch.Crumb("ShowMainWindow: done");
    }

    // ------------------------------------------------------------------ 数值输入

    private void BuildFields()
    {
        _fields.Clear();

        _fields.Add(new NumberField
        {
            Box = MainFontBox,
            Get = () => _config.MainFontSize,
            Set = v =>
            {
                _config.MainFontSize = Math.Round(v);
                RefreshBar();
            },
            Min = 9,
            Max = 24,
            Decimals = 0,
        });

        _fields.Add(new NumberField
        {
            Box = SubFontBox,
            Get = () => _config.SubFontSize,
            Set = v =>
            {
                _config.SubFontSize = Math.Round(v);
                RefreshBar();
            },
            Min = 8,
            Max = 20,
            Decimals = 0,
        });

        _fields.Add(new NumberField
        {
            Box = OffsetBox,
            Get = () => _config.SubtitleOffsetMs,
            Set = v =>
            {
                _config.SubtitleOffsetMs = Math.Round(v);
                RebuildTrack();
                _cueIndex = -1;
                _loop.Reset();
                RefreshBar();
            },
            Min = -5000,
            Max = 5000,
            Decimals = 0,
        });
    }

    private void RefreshFields()
    {
        foreach (var field in _fields)
            field.Box.Text = field.Format(field.Get());
    }

    // ------------------------------------------------------------------ 倍速

    /// <summary>下拉框的选项顺序和 PlaybackSpeeds.All 一一对应。</summary>
    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || SpeedBox.SelectedIndex < 0)
            return;

        var value = PlaybackSpeeds.All[Math.Min(SpeedBox.SelectedIndex, PlaybackSpeeds.All.Length - 1)];

        _config.Speed = value;
        _player.Speed = value;
        _config.Save();
    }

    private void RefreshSpeedBox()
    {
        var index = Array.IndexOf(PlaybackSpeeds.All, PlaybackSpeeds.Nearest(_config.Speed));

        var previous = _suppressEvents;
        _suppressEvents = true;
        SpeedBox.SelectedIndex = index < 0 ? 0 : index;
        _suppressEvents = previous;
    }

    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is TextBox box)
            CommitField(box);

        e.Handled = true;
    }

    private void OnNumberCommit(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents && sender is TextBox box)
            CommitField(box);
    }

    private void CommitField(TextBox box)
    {
        var field = _fields.FirstOrDefault(f => ReferenceEquals(f.Box, box));
        if (field is null)
            return;

        var value = Math.Clamp(field.Parse(box.Text), field.Min, field.Max);
        field.Set(value);

        // 回写规范化后的值，顺便修掉用户输入越界或格式乱的情况
        box.Text = field.Format(field.Get());
        _config.Save();
    }

    // ------------------------------------------------------------------ 宽度滑条

    private void RefreshWidth()
    {
        var width = Math.Clamp(_config.BarWidthDip, WidthSlider.Minimum, WidthSlider.Maximum);
        WidthSlider.Value = width;
        WidthValueText.Text = $"{width:0}";
    }

    private void OnWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析阶段 Slider 的 Min/Max 会改 Value 并触发这个事件，那时 _config 还没建好
        if (_suppressEvents)
            return;

        _config.BarWidthDip = Math.Round(e.NewValue);
        WidthValueText.Text = $"{_config.BarWidthDip:0}";
        RepositionBar();

        // 拖滑条时字幕条临时变实（100% + 底板 + 灰描边），停手 1.4 秒再恢复半透明：
        // 半透明的条压在任务栏上，边界看不出来，调宽度等于摸黑调
        _bar?.SetMeasureMode(true);
        _measureTimer.Stop();
        _measureTimer.Start();

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------------ 透明度滑条

    /// <summary>两条滑条按当前配置摆好，并同步右边那对数字。</summary>
    private void RefreshOpacity()
    {
        var idle = Math.Round(Math.Clamp(_config.OpacityIdle, 0.05, 1) * 100);
        var hover = Math.Round(Math.Clamp(_config.OpacityHover, 0.05, 1) * 100);

        IdleOpacitySlider.Value = idle;
        HoverOpacitySlider.Value = hover;
        IdleOpacityValueText.Text = $"{idle:0}";
        HoverOpacityValueText.Text = $"{hover:0}";
    }

    private void OnIdleOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
            return;

        _config.OpacityIdle = Math.Round(e.NewValue / 100, 2);
        IdleOpacityValueText.Text = $"{e.NewValue:0}";
        _bar?.ApplyAppearance(_config);

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnHoverOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
            return;

        _config.OpacityHover = Math.Round(e.NewValue / 100, 2);
        HoverOpacityValueText.Text = $"{e.NewValue:0}";
        _bar?.ApplyAppearance(_config);

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------------ 配置绑定

    private void BindConfigToUi()
    {
        _suppressEvents = true;

        RefreshFields();
        RefreshWidth();
        RefreshOpacity();
        RefreshSpeedBox();

        ThemeBox.SelectedIndex = _config.Theme switch
        {
            BarTheme.Light => 1,
            BarTheme.Dark => 2,
            _ => 0,
        };

        TranslationCheck.IsChecked = _config.ShowTranslation;
        ShowTimeCheck.IsChecked = _config.ShowTime;
        BgTransparentCheck.IsChecked = _config.BackgroundTransparent;
        ClickThroughCheck.IsChecked = _config.ClickThrough;
        CaptureHideCheck.IsChecked = _config.CaptureHide;
        ScreenBusyCheck.IsChecked = _config.HideOnScreenBusy;
        TrayCheck.IsChecked = _config.MinimizeToTray;
        FloatCheck.IsChecked = _config.FloatBar;

        LibraryPathText.Text = string.IsNullOrWhiteSpace(_config.LibraryDirectory)
            ? "未设置"
            : _config.LibraryDirectory;

        LibraryPathText.ToolTip = _config.LibraryDirectory;

        UpdateHotkeyButtons();

        _suppressEvents = false;
    }

    private void HookPlayer()
    {
        _player.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTransport);
        _player.PlaybackEnded += (_, _) => Dispatcher.BeginInvoke(OnPlaybackEnded);
    }

    private void OnPlaybackEnded()
    {
        // 连播中：这一集放完了就接着放收藏队列里的下一条
        if (_favQueuePos >= 0)
        {
            AdvanceFavoriteQueue();
            return;
        }

        var path = _player.FilePath;
        _cueIndex = -1;
        _loop.Reset();

        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            // 播完重新加载，回到 0 秒并保持暂停，方便立刻再听一遍
            _player.Load(path, _config.Volume);
            _player.Speed = _config.Speed;
            _player.Position = TimeSpan.Zero;
        }
        catch
        {
            // 重新加载失败时保持静默
        }

        UpdateTransport();
    }

    // ------------------------------------------------------------------ 图标

    private void BuildIcons()
    {
        RefreshPlayIcons();
    }

    private void RefreshPlayIcons()
    {
        var ink = new SolidColorBrush(Ink);
        var faint = new SolidColorBrush(Faint);

        // 设计稿这两颗是「竖条 + 三角」的跳句图标，不是菜单里那对尖括号
        PrevButton.Content = Icons.Make(Icons.SkipPrev, 16, ink, filled: true);
        NextButton.Content = Icons.Make(Icons.SkipNext, 16, ink, filled: true);

        // 播放键是深底白图标，整页唯一的实心块：一眼能找到，又不用任何彩色。
        // 没装载音频时退成浅灰底 + 灰图标，淡一点但别看着像坏掉了。
        var ready = _player.IsLoaded;
        var glyph = ready ? Brushes.White : faint;

        PlayButton.Background = ready
            ? new SolidColorBrush(Color.FromRgb(0x59, 0x57, 0x57))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

        // 图标照 2026-11-19 那两张设计稿（28×28 画板）：播放是白三角，暂停是两根白竖条，
        // 两者的圆盘都还是按钮自己的椭圆，所以按下/松开时圆的大小和颜色不会跳。
        PlayButton.Content = _player.IsPlaying
            ? Icons.MakePauseGlyph(46, glyph)
            : Icons.MakePlayGlyph(46, glyph);

        // 收藏页那颗「全部播放」：同一套图标，按 34 的圆缩 —— 连播真跑起来了就变暂停，
        // 不然点完一点反馈都没有，看着像没反应。
        var favPlaying = _favQueuePos >= 0 && _player.IsPlaying;

        FavPlayAllButton.Content = favPlaying
            ? Icons.MakePauseGlyph(34)
            : Icons.MakePlayGlyph(34);

        // 顺手记一笔画的是哪枚，诊断模式（--favsim）直接读它，不用反推
        FavPlayAllButton.Tag = favPlaying ? "pause" : "play";

        // 循环：开着是墨色图标 + 极浅底，关掉就剩设计稿那个浅浅的箭头（#DBDEE5）
        LoopButton.Content = Icons.MakeLoopArrows(
            18,
            _config.LoopSingleCue ? ink : new SolidColorBrush(LoopIdle));
        LoopButton.Background = _config.LoopSingleCue
            ? new SolidColorBrush(AccentWash)
            : Brushes.Transparent;

        PrevButton.IsEnabled = _player.IsLoaded && _track.Count > 0;
        NextButton.IsEnabled = _player.IsLoaded && _track.Count > 0;
        PlayButton.IsEnabled = _player.IsLoaded;

        RefreshStarButton();
    }

    /// <summary>播放页那颗星：当前句已收藏就填墨色，没收过留一枚浅灰实心星。</summary>
    private void RefreshStarButton()
    {
        var on = IsCurrentCueFavorited();
        FavStarButton.Content = Icons.MakeStar(17, new SolidColorBrush(on ? Ink : Faint));
        FavStarButton.ToolTip = on ? "取消收藏（Ctrl+Alt+S）" : "收藏本句（Ctrl+Alt+S）";
    }

    // ------------------------------------------------------------------ 任务栏吸附

    private void EnsureBar()
    {
        if (_bar is not null)
            return;

        _bar = new SubtitleBar();
        _bar.RightClicked += OnBarRightClicked;

        // 单击字幕条暂停 / 继续（双击同样只算一次）
        _bar.ToggleRequested += (_, _) => TogglePlayback();

        // 悬浮模式：拖动结束记位置，锁按钮记状态
        _bar.PositionChanged += OnBarPositionChanged;
        _bar.LockChanged += OnBarLockChanged;

        _bar.Show();
        _bar.ApplyAppearance(_config);

        // 配置里开着悬浮就直接飘起来，不用等任务栏那边的判断
        if (_config.FloatBar)
            _bar.SetFloating(true, _config);

        // 建好但先藏着：等目录、音频、字幕都就绪再显示，
        // 免得用户看到一条点不动的字幕条（背后界面线程还在启动）。
        if (!_sessionReady)
            _bar.SetVisible(false);
    }

    private void OnBarPositionChanged(object? sender, (double Left, double Top) e)
    {
        _config.FloatLeftDip = e.Left;
        _config.FloatTopDip = e.Top;
        _config.Save();
    }

    private void OnBarLockChanged(object? sender, bool locked)
    {
        _config.FloatLocked = locked;
        _config.Save();

        SetStatus(locked ? "已锁定位置 · 点字幕暂停 / 继续" : "已解锁 · 按住字幕拖到任意位置", true);
    }

    /// <summary>播放 / 暂停。演示模式下没有音频，直接提示去设目录。</summary>
    private void TogglePlayback()
    {
        if (_demoMode || !_player.IsLoaded)
        {
            SetStatus("先设置一个听力目录", false);
            ShowMainWindow();
            SelectPage(1);
            return;
        }

        _player.TogglePlay();
        UpdateTransport();
    }

    /// <summary>单句循环开关（右键菜单、主界面按钮共用）。</summary>
    private void ToggleLoop()
    {
        SetLoop(!_config.LoopSingleCue);
    }

    private void SetLoop(bool on)
    {
        _config.LoopSingleCue = on;
        _loop.Enabled = on;
        _loop.Reset();
        RefreshPlayIcons();
        _config.Save();
    }

    /// <summary>
    /// 托盘图标交给独立线程：它的每一次 Shell_NotifyIcon 都是跨进程调 shell，
    /// 压在界面线程上就是「点了没反应」。
    /// </summary>
    private void StartTray()
    {
        TrayHost.Start(
            () => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            () => Dispatcher.BeginInvoke(new Action(ToggleBossKey)),
            () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_player.IsLoaded)
                    return;
                _player.TogglePlay();
                UpdateTransport();
            })),
            () => Dispatcher.BeginInvoke(new Action(RequestExit)));
    }

    private void OnGuardTick(object? sender, EventArgs e)
    {
        // 顺手记一下听到哪儿了，下次启动可以接着显示（不写盘，退出时统一存）
        if (!_demoMode && _player.IsLoaded)
            _config.LastPositionMs = _player.Position.TotalMilliseconds;

        // 任务栏信息来自后台轮询，这里一次 shell 都不问。
        // 第一轮还没跑完就先不动，免得把字幕条误判成「找不到任务栏」而降级。
        var snapshot = TaskbarWatcher.Current;

        if (!snapshot.Ready)
            return;

        var taskbar = snapshot.Taskbar;

        // 悬浮字幕：完全不碰任务栏，位置由用户拖、由配置记
        if (_config.FloatBar)
        {
            EnsureBar();

            if (_bar is null)
                return;

            if (_bar.IsAttached)
                _bar.Detach();

            _bar.SetFloating(true, _config);

            var floatingHidden = _userHidden || (_config.HideOnScreenBusy && snapshot.ScreenBusy);
            _bar.SetVisible(_sessionReady && !floatingHidden);

            SetStatus(
                _bar.IsLocked ? "悬浮字幕 · 已锁定（点字幕暂停 / 继续）" : "悬浮字幕 · 可拖动",
                true,
                quiet: true);

            return;
        }

        // 从悬浮切回任务栏：先把浮窗那一行收掉，下面按正常流程重新吸附
        if (_bar is not null && _bar.IsFloating)
        {
            _bar.SetFloating(false, _config);
            _lastScale = -1;
        }

        if (taskbar is null)
        {
            SetStatus("未找到任务栏，已降级为悬浮窗", false);
            ApplyFallbackLayout();
            return;
        }

        _taskbar = taskbar;
        EnsureBar();

        if (_bar is null)
            return;

        if (!_bar.IsAttached || _bar.TaskbarHandle != taskbar.Handle)
        {
            Trace($"attach.begin {_clock.ElapsedMilliseconds}ms");

            if (!_bar.Attach(taskbar.Handle))
            {
                SetStatus("无法吸附任务栏（请勿以管理员身份运行），已降级为悬浮窗", false);
                ApplyFallbackLayout();
                return;
            }

            Trace($"attach.done {_clock.ElapsedMilliseconds}ms");

            _bar.SetClickThrough(_config.ClickThrough);
            _bar.SetCaptureHide(_config.CaptureHide);
            _bar.Reposition(taskbar, _config);
            Trace($"reposition.done {_clock.ElapsedMilliseconds}ms");
            _lastRect = taskbar.Rect;
            _lastScale = taskbar.Scale;
        }
        else if (!RectEquals(taskbar.Rect, _lastRect) || Math.Abs(taskbar.Scale - _lastScale) > 0.001)
        {
            _bar.Reposition(taskbar, _config);
            _lastRect = taskbar.Rect;
            _lastScale = taskbar.Scale;
        }

        var hidden = _userHidden
                     || snapshot.Collapsed
                     || (_config.HideOnScreenBusy && snapshot.ScreenBusy);

        // 会话没恢复完就别露头：这时候界面线程还要干活，
        // 字幕条一出现，用户一点就会觉得「卡死」。
        _bar.SetVisible(_sessionReady && !hidden);

        // 这条每轮吸附都会刷，属于「背景信息」：只更新小圆点，不弹提示
        SetStatus(
            $"已吸附 · {taskbar.Rect.Width}×{taskbar.Rect.Height} · {taskbar.Scale * 100:0}%",
            true,
            quiet: true);
    }

    private void ApplyFallbackLayout()
    {
        EnsureBar();

        if (_bar is null)
            return;

        if (_bar.IsAttached)
            _bar.Detach();

        _bar.RevertToPopup();
        _bar.Topmost = true;

        var area = SystemParameters.WorkArea;
        var width = Math.Min(_config.BarWidthDip, Math.Max(240, area.Width - 80));

        _bar.Width = width;
        _bar.Height = 40;
        _bar.Left = area.Right - width - 20;
        _bar.Top = area.Bottom - 64;

        // 降级成悬浮窗也一样：没就绪就先别显示
        _bar.SetVisible(_sessionReady && !_userHidden);
    }

    /// <summary>靠右 / 靠左吸附：记住贴哪一端，然后重建字幕条、重新挂回任务栏。</summary>
    private void OnDockRight(object sender, RoutedEventArgs e) => DockTo(DockSide.Right);

    private void OnDockLeft(object sender, RoutedEventArgs e) => DockTo(DockSide.Left);

    private void DockTo(DockSide side)
    {
        _config.DockSide = side;

        // 点「吸附」就是要贴回任务栏，悬浮开关跟着关掉
        _config.FloatBar = false;

        var previous = _suppressEvents;
        _suppressEvents = true;
        FloatCheck.IsChecked = false;
        _suppressEvents = previous;

        _config.Save();

        if (_bar is not null)
        {
            _bar.RightClicked -= OnBarRightClicked;
            _bar.SetFloating(false, _config);
            _bar.Detach();
            _bar.Close();
            _bar = null;
        }

        _lastScale = -1;
        OnGuardTick(this, EventArgs.Empty);

        SetStatus(side == DockSide.Left ? "已靠左吸附任务栏" : "已靠右吸附任务栏", true);
    }

    /// <summary>勾 / 取消「悬浮字幕」后立刻生效：勾上飘起来，取消就贴回任务栏。</summary>
    private void ApplyFloatMode()
    {
        EnsureBar();

        if (_bar is null)
            return;

        if (_config.FloatBar)
        {
            _bar.SetFloating(true, _config);
            _bar.SetVisible(_sessionReady && !_userHidden);

            // 这里不再弹左下角提示：开没开看开关本身，用法说明放在开关的悬停提示里
            return;
        }

        _bar.SetFloating(false, _config);
        _lastScale = -1;
        OnGuardTick(this, EventArgs.Empty);
    }

    private void RepositionBar()
    {
        if (_bar is null)
            return;

        // 悬浮时没有任务栏位置可言，只要跟着宽度滑条调窗口宽度
        if (_bar.IsFloating)
        {
            _bar.SetFloatWidth(_config.BarWidthDip);
            return;
        }

        if (!_bar.IsAttached)
            return;

        // 同样走后台轮询的结果，不在界面线程上问 shell
        var taskbar = _taskbar ?? TaskbarWatcher.Current.Taskbar;

        if (taskbar is null)
            return;

        _bar.Reposition(taskbar, _config);
    }

    // ------------------------------------------------------------------ 播放驱动

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (_demoMode)
        {
            TickDemo();
            return;
        }

        if (!_player.IsLoaded)
            return;

        var position = _player.Position;
        var duration = _player.Duration;

        if (!PositionSlider.IsMouseCaptureWithin)
        {
            _suppressEvents = true;
            PositionSlider.Maximum = Math.Max(0.001, duration.TotalSeconds);
            PositionSlider.Value = Math.Clamp(position.TotalSeconds, 0, PositionSlider.Maximum);
            _suppressEvents = false;
        }

        UpdateTimeLabels(position, duration);

        // 收藏连播：这一句念完就换下一条（换文件也走这里）
        if (_favQueuePos >= 0 && position.TotalMilliseconds >= _favCueEndMs)
        {
            AdvanceFavoriteQueue();
            return;
        }

        var index = _track.Count > 0 ? _track.IndexAt(position) : -1;

        if (_loop.OnTick(index) == CueLoopStep.Rewind)
        {
            // 这一句念完了，拉回句首。这一拍不渲染，下一拍自然接着画。
            _player.Position = _track.Cues[_loop.Index].Start;
            _cueIndex = -1;
            return;
        }

        if (index != _cueIndex)
        {
            _cueIndex = index;
            ShowPreview(index >= 0 && index < _track.Count ? _track.Cues[index] : null, index);
        }

        PushToBar(position);
    }

    /// <summary>把某一句填到「当前句」里。用户正在框里打字时不要抢。</summary>
    private void ShowPreview(Cue? cue, int index = -1)
    {
        RefreshStarButton();

        // 页码在「抢不抢内容」之前更新：正在打字时也该知道自己改的是第几句
        CueIndexText.Text = index >= 0 && _track.Count > 0
            ? $"第 {index + 1} / {_track.Count} 句"
            : "—";

        // 主句和译文现在都能改，两边任何一个在输入都不能被覆盖
        if (PreviewMain.IsKeyboardFocusWithin || PreviewSub.IsKeyboardFocusWithin)
            return;

        PreviewMain.Text = cue?.Text ?? "—";
        PreviewSub.Text = cue?.Translation ?? string.Empty;
    }

    /// <summary>诊断用：把一句双语字幕直接摆到「当前句」上，供离屏截图核对排版。</summary>
    internal void ShowPreviewForShot(Cue cue)
    {
        ShowPreview(cue);
    }

    /// <summary>离屏截图用：摆几行假收藏，核对列表排版（不写收藏文件）。</summary>
    internal void FillFavoritesForShot()
    {
        // 指向一个真实存在的文件，否则这些行会被渲染成「文件不见了」的灰态，验不到正常配色
        var audio = Path.Combine(AppContext.BaseDirectory, "LoafEar.exe");

        _favPreview = new List<FavoriteCue>
        {
            new()
            {
                AudioPath = audio, Episode = "E01-1", CueIndex = 2,
                Text = "Could you say that again a little slower?",
                Translation = "你能再慢一点说一遍吗？",
                StartMs = 3000, EndMs = 5000, AddedAt = DateTime.Now.AddMinutes(-3),
            },
            new()
            {
                AudioPath = audio, Episode = "E01-1", CueIndex = 4,
                Text = "I said, I don't have a spoon.",
                Translation = "我说了，我没有勺子。",
                StartMs = 12000, EndMs = 14000, AddedAt = DateTime.Now.AddMinutes(-30),
            },
            new()
            {
                AudioPath = audio, Episode = "E01-1", CueIndex = 5,
                Text = "We were on a break!",
                Translation = "我们当时在分手期！",
                StartMs = 6000, EndMs = 8000, AddedAt = DateTime.Now.AddMinutes(-9),
            },
            new()
            {
                AudioPath = audio, Episode = "E01-1", CueIndex = 8,
                Text = "That's not even a word.",
                Translation = "那根本不算个词。",
                StartMs = 15000, EndMs = 17000, AddedAt = DateTime.Now.AddMinutes(-44),
            },
            new()
            {
                AudioPath = audio, Episode = "E01-1", CueIndex = 11,
                Text = "Nobody told me it was this quiet.",
                Translation = "没人告诉我这里这么安静。",
                StartMs = 9000, EndMs = 11000, AddedAt = DateTime.Now.AddMinutes(-20),
            },
        };

        // 截图要照真实默认态渲染：默认就是「本节目」，不能摆成「全部」
        _favFilter = FavoriteFilter.Episode;
        RefreshFavorites();
    }

    /// <summary>离屏截图用：给播放页的节目下拉塞几个样例，检查宽度、截断和选中态。</summary>
    internal void ShowAudioBoxForShot(params string[] titles)
    {
        _library = titles
            .Select(t => new LibraryItem { AudioPath = t + ".mp3" })
            .ToList();

        RebuildAudioBox();

        _audioBoxSyncing = true;
        AudioBox.SelectedIndex = 0;
        _audioBoxSyncing = false;

        UpdateAudioTitle();
    }

    // ------------------------------------------------------------------ 目录浮层自检

    /// <summary>诊断用：按真按钮的路径打开「目录」浮层（`--dirsim` 调）。</summary>
    internal void OpenDirForTest()
    {
        DirButton.IsChecked = true;
        OnDirToggle(DirButton, new RoutedEventArgs());
    }

    /// <summary>诊断用：摆一条状态提示，供离屏截图核对轻提示的样子。</summary>
    internal void ShowStatusForShot(string text) => SetStatus(text, true);

    /// <summary>
    /// 诊断用：量「常规」页第一张卡片 —— 卡片边框、标题、两颗按钮各自相对窗口根元素的位置。
    /// 这类「标题下移 3px、外框上下各收 3px」的调整只能靠数字确认，
    /// 截图放大后靠眼睛比是看不出来的。
    /// </summary>
    internal string LibraryCardProbeForTest()
    {
        var root = (FrameworkElement)Content;

        return string.Join(
            " ",
            Box(LibraryCard),
            Box(LibraryTitle),
            $"标题基线={LibraryTitle.TranslatePoint(new Point(0, 0), root).Y + LibraryTitle.BaselineOffset:0.#}",
            Box(RescanButton),
            Box(ChangeDirButton));

        string Box(FrameworkElement element)
        {
            var origin = element.TranslatePoint(new Point(0, 0), root);
            return $"{element.Name}=({origin.X:0.#},{origin.Y:0.#}) " +
                   $"{element.ActualWidth:0.#}x{element.ActualHeight:0.#}";
        }
    }

    // ------------------------------------------------------------------ 悬浮字幕自检

    /// <summary>诊断用：按勾选框那条路切悬浮，验证它与界面开关走的是同一条链路。</summary>
    internal void SetFloatForTest(bool on)
    {
        var previous = _suppressEvents;
        _suppressEvents = true;
        FloatCheck.IsChecked = on;
        _suppressEvents = previous;

        _config.FloatBar = on;
        ApplyFloatMode();
    }

    /// <summary>诊断用：悬浮字幕此刻的状态。</summary>
    internal string FloatStateForTest() => _bar is null
        ? "bar=null"
        : $"floating={_bar.IsFloating} attached={_bar.IsAttached} locked={_bar.IsLocked} " +
          $"topmost={_bar.Topmost} lockRow={_bar.IsLockRowVisible} " +
          $"size={_bar.ActualWidth:0}x{_bar.ActualHeight:0} " +
          $"left/top={_bar.Left:0},{_bar.Top:0} onScreen={_bar.IsOnScreen}";

    /// <summary>诊断用：模拟一次拖动结束，走的是和真空手拖动完全相同的回调。</summary>
    internal void DragBarForTest(double left, double top) => _bar?.MoveToForTest(left, top);

    /// <summary>诊断用：模拟拖动过程中的一次位移。</summary>
    internal void NudgeBarForTest(int dx, int dy) => _bar?.DragByForTest(dx, dy);

    /// <summary>
    /// 诊断用：推一下宽度滑条 —— 走的是和真拖滑条完全相同的 ValueChanged 链路，
    /// 用来验「调宽度时字幕条临时变实、停手后恢复」。
    /// </summary>
    internal void NudgeBarWidthForTest(double delta)
    {
        EnsureBar();
        WidthSlider.Value = Math.Clamp(
            WidthSlider.Value + delta, WidthSlider.Minimum, WidthSlider.Maximum);
    }

    /// <summary>诊断用：字幕条此刻的透明度 / 底板 / 描边。</summary>
    internal string BarLookForTest() => _bar is null
        ? "bar=null"
        : $"measuring={_bar.IsMeasuring} opacity={_bar.Opacity:0.00} {_bar.LookForTest()}";

    /// <summary>诊断用：点锁按钮。</summary>
    internal bool ToggleLockForTest()
    {
        if (_bar is null)
            return false;

        _bar.ToggleLockForTest();
        return _bar.IsLocked;
    }

    /// <summary>诊断用：锁按钮中心的客户区坐标（物理像素），用来投真实鼠标消息。</summary>
    internal (int X, int Y) BarLockCenterForTest() => _bar?.LockCenterClient() ?? (0, 0);

    /// <summary>
    /// 诊断用：目录浮层此刻相对按钮的位置（物理像素）。
    /// 浮层是独立 HWND，RenderTargetBitmap 拍不到它和按钮的相对关系，只能量屏幕坐标。
    /// </summary>
    internal string ProgramPopupAlignForTest() =>
        DescribeAlign("目录", DirButton, ProgramPopup.Child as FrameworkElement);

    /// <summary>诊断用：打开倍速下拉（Popup 是异步摆的，打开后要等一拍再量）。</summary>
    internal void OpenSpeedPopupForTest() => SpeedBox.IsDropDownOpen = true;

    internal void CloseSpeedPopupForTest() => SpeedBox.IsDropDownOpen = false;

    /// <summary>诊断用：倍速下拉此刻相对按钮的位置（物理像素）。</summary>
    internal string SpeedPopupAlignForTest()
    {
        if (SpeedBox.Template.FindName("PART_Popup", SpeedBox) is not Popup popup)
            return "倍速: 找不到 PART_Popup";

        return DescribeAlign("倍速", SpeedBox, popup.Child as FrameworkElement);
    }

    /// <summary>
    /// 诊断用：倍速按钮与下拉浮层各自的屏幕矩形（物理像素）。
    /// 抓屏合成要用具体坐标，只打印差值不够。
    /// </summary>
    internal (Rect Button, Rect Popup) SpeedPopupRectsForTest()
    {
        var tl = SpeedBox.PointToScreen(new Point(0, 0));
        var br = SpeedBox.PointToScreen(new Point(SpeedBox.ActualWidth, SpeedBox.ActualHeight));
        var button = new Rect(tl, br);

        if (SpeedBox.Template.FindName("PART_Popup", SpeedBox) is Popup popup &&
            popup.Child is FrameworkElement child &&
            PresentationSource.FromVisual(child) is HwndSource src &&
            src.Handle != IntPtr.Zero &&
            NativeMethods.GetWindowRect(src.Handle, out var r))
        {
            return (button, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }

        return (button, Rect.Empty);
    }

    private static string DescribeAlign(string name, FrameworkElement target, FrameworkElement? child)
    {
        if (child is null || PresentationSource.FromVisual(child) is not HwndSource src || src.Handle == IntPtr.Zero)
            return $"{name}: 浮层没打开";

        if (!NativeMethods.GetWindowRect(src.Handle, out var popupRect))
            return $"{name}: 拿不到浮层窗口位置";

        var tl = target.PointToScreen(new Point(0, 0));
        var br = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));

        return $"{name}: 按钮 x{tl.X:0}–{br.X:0} y{tl.Y:0}–{br.Y:0} / " +
               $"浮层 x{popupRect.Left}–{popupRect.Right} y{popupRect.Top}–{popupRect.Bottom} " +
               $"右边缘差={br.X - popupRect.Right:0} 中线差={(tl.Y + br.Y) / 2 - (popupRect.Top + popupRect.Bottom) / 2:0}";
    }

    /// <summary>诊断用：往字幕条投一次真实的左键按下 + 抬起。</summary>
    internal void PostBarClickForTest(int x, int y) => _bar?.PostClick(x, y);

    /// <summary>诊断用：字幕条窗口在屏幕上的位置（物理像素），核对左右吸附是否贴边。</summary>
    internal string BarWindowRectForTest()
    {
        if (_bar is null || _bar.WindowHandle == IntPtr.Zero)
            return "bar=null";

        return NativeMethods.GetWindowRect(_bar.WindowHandle, out var r)
            ? $"{r.Left},{r.Top}–{r.Right},{r.Bottom} w={r.Right - r.Left}"
            : "rect=fail";
    }

    /// <summary>诊断用：把字幕条挪到光标下催一次悬停，锁图标应该露出来。</summary>
    internal bool HoverLockForTest()
    {
        if (_bar is null)
            return false;

        _bar.SetVisible(true);
        _bar.PlaceUnderCursorForTest();
        _bar.HoverTickForTest();

        return _bar.IsLockShown;
    }

    /// <summary>诊断用：把字幕条挪到远处再催一次，锁图标应该收回去。</summary>
    internal bool UnhoverLockForTest()
    {
        if (_bar is null)
            return false;

        _bar.MoveAwayFromCursorForTest();
        _bar.HoverTickForTest();

        return _bar.IsLockShown;
    }

    /// <summary>诊断用：任务栏在屏幕上的位置（物理像素）。</summary>
    internal string TaskbarRectForTest()
    {
        var tb = TaskbarService.Locate();

        return tb is null
            ? "taskbar=null"
            : $"{tb.Rect.Left},{tb.Rect.Top}–{tb.Rect.Right},{tb.Rect.Bottom}";
    }

    /// <summary>诊断用：点吸附加载按钮。</summary>
    internal void DockForTest(DockSide side) => DockTo(side);

    /// <summary>诊断用：当前录音的吸附端。</summary>
    internal DockSide DockSideForTest() => _config.DockSide;

    /// <summary>诊断用：手动催一次守护时钟（任务栏轮询还没跑完时用）。</summary>
    internal void GuardTickForTest() => OnGuardTick(this, EventArgs.Empty);

    /// <summary>诊断用：字幕条窗口的根元素，用来离屏截一张核对锁按钮那一行。</summary>
    internal FrameworkElement? BarRootForTest => _bar?.Content as FrameworkElement;

    /// <summary>诊断用：直接往字幕条上塞一句双语字幕。</summary>
    internal void PushBarTextForTest(string main, string sub, string time)
    {
        var cue = new Cue
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(4),
            Text = main,
            Translation = sub,
        };

        _bar?.SetContent(cue, main, sub, time, TimeSpan.Zero, true);
    }

    /// <summary>诊断用：把浮层收起来。</summary>
    internal void CloseDirForTest()
    {
        DirButton.IsChecked = false;
        OnDirToggle(DirButton, new RoutedEventArgs());
    }

    /// <summary>诊断用：最近一条状态文字（验证点击确实走进了加载流程）。</summary>
    internal string LastStatusText => StatusText?.Text ?? string.Empty;

    /// <summary>诊断用：当前选中的节目名（目录浮层点完一行靠它验证）。</summary>
    internal string SelectedProgramForTest => _lastPickedTitle ?? "(未选)";

    /// <summary>诊断用：点第 index 行节目，走的是行按钮真实的 Click 处理。</summary>
    internal void ClickProgramForTest(int index)
    {
        if (index < 0 || index >= ProgramList.Children.Count)
            return;

        if (ProgramList.Children[index] is Button row)
            OnProgramPick(row, new RoutedEventArgs());
    }

    /// <summary>诊断用：读一行是不是当前节目（浅灰底）。</summary>
    internal bool IsProgramHighlighted(int index) =>
        index >= 0
        && index < ProgramList.Children.Count
        && ProgramList.Children[index] is Button { Background: SolidColorBrush b }
        && b.Color == Color.FromRgb(0xF0, 0xF0, 0xF0);

    // ------------------------------------------------------------------ 测试字幕

    /// <summary>
    /// 没有目录、也没听过东西时显示一组测试字幕：让人一眼看出字幕条是活的，
    /// 也知道下一步该去设置目录，而不是对着一片空白猜程序是不是没启动。
    /// </summary>
    private void StartDemoSubtitles()
    {
        _demoTrack = BuildDemoTrack();
        _track = _demoTrack;
        _rawTrack = SubtitleTrack.Empty();
        _cueIndex = -1;
        _demoMode = true;
        _demoClock.Restart();

        SetStatus("未设置听力目录，正在显示测试字幕", false);
        RefreshBar();
        TickDemo();
    }

    private static SubtitleTrack BuildDemoTrack()
    {
        var lines = new (string Main, string Sub)[]
        {
            ("This is a test subtitle. Set a listening folder to begin.",
                "这是测试字幕。设置一个听力目录就能开始。"),
            ("Click the bar to pause or resume.",
                "点一下字幕条就能暂停或继续。"),
            ("Right-click for settings and the program list.",
                "右键可以打开设置和节目列表。"),
            ("Long sentences are paged by word, never cut in half.",
                "超长句按词分页，不会把半个单词切掉。"),
        };

        var cues = new List<Cue>();
        var clock = TimeSpan.Zero;

        foreach (var (main, sub) in lines)
        {
            cues.Add(new Cue
            {
                Start = clock,
                End = clock + TimeSpan.FromSeconds(4),
                Text = main,
                Translation = sub,
            });

            clock += TimeSpan.FromSeconds(4.6);
        }

        return SubtitleTrack.Create(cues, "", "DEMO");
    }

    private void TickDemo()
    {
        if (_demoTrack.Count == 0)
            return;

        var total = _demoTrack.Cues[^1].End;
        var elapsed = _demoClock.Elapsed.TotalSeconds;

        var position = TimeSpan.FromSeconds(elapsed % Math.Max(total.TotalSeconds, 1));
        var index = _demoTrack.IndexAt(position);

        _suppressEvents = true;
        PositionSlider.Maximum = Math.Max(0.001, total.TotalSeconds);
        PositionSlider.Value = Math.Clamp(position.TotalSeconds, 0, PositionSlider.Maximum);
        _suppressEvents = false;

        UpdateTimeLabels(position, total);

        if (index != _cueIndex)
        {
            _cueIndex = index;
            ShowPreview(index >= 0 && index < _track.Count ? _track.Cues[index] : null, index);
        }

        PushToBar(position);
    }

    private void StopDemo()
    {
        if (!_demoMode)
            return;

        _demoMode = false;
        _demoClock.Stop();
        _demoTrack = SubtitleTrack.Empty();
    }

    // ------------------------------------------------------------------ 当前句改写

    /// <summary>
    /// 把「当前句」里改过的台词写回原子幕文件。只替换这一句的原文，
    /// 时间码、翻译行、编号都原样留着。
    /// </summary>
    private void OnSaveCue(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _cueIndex < 0 || _cueIndex >= _rawTrack.Count)
        {
            SetStatus("这里没有可改写的原字幕", false);
            return;
        }

        var path = _rawTrack.SourcePath;
        var raw = _rawTrack.Cues[_cueIndex];

        // 单行输入框：用户粘进来的换行压成空格，否则会把 SRT 的块结构撑坏
        var text = PreviewMain.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var translation = PreviewSub.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (text.Length == 0)
        {
            SetStatus("台词不能留空", false);
            return;
        }

        var mainChanged = !string.Equals(text, raw.Text.Trim(), StringComparison.Ordinal);
        var subChanged = !string.Equals(translation, raw.Translation.Trim(), StringComparison.Ordinal);

        if (!mainChanged && !subChanged)
        {
            SetStatus("这句没有改动", true);
            return;
        }

        // 原来这句没有译文行，用户又新写了一段：没有可供替换的行，不凭空插入
        if (subChanged && raw.Translation.Length == 0)
        {
            SetStatus("原字幕这句没有译文行，无法写入新译文", false);
            return;
        }

        // 清空译文会留下一个空行，把字幕块结构撑坏
        if (subChanged && translation.Length == 0)
        {
            SetStatus("译文不能留空（删掉译文行会破坏字幕结构）", false);
            return;
        }

        if (mainChanged
            && !SubtitleEditor.ReplaceCueText(path, raw.Start, raw.Text, text, out var mainMessage))
        {
            SetStatus(mainMessage, false);
            return;
        }

        // 译文在文件里是独立一行，按同样方式再换一次
        if (subChanged
            && !SubtitleEditor.ReplaceCueText(
                path, raw.Start, raw.Translation, translation, out var subMessage))
        {
            SetStatus($"主句已写入，译文没改成：{subMessage}", false);
            return;
        }

        var position = _player.IsLoaded ? _player.Position : TimeSpan.Zero;

        try
        {
            // 重读一遍，界面上的字幕才和磁盘一致（也顺便验证写回去的文件还能解析）
            _rawTrack = SubtitleParser.Load(path);
            RebuildTrack();

            PreviewMain.Text = text;
            PreviewSub.Text = translation;

            _cueIndex = -1;
            PushToBar(position);
            SetStatus($"已保存 · {Path.GetFileName(path)}", true);
        }
        catch (Exception ex)
        {
            LogError("OnSaveCue", ex);
            SetStatus($"已写入，但重新读取失败：{ex.Message}", false);
        }
    }

    private void PushToBar(TimeSpan position)
    {
        if (_bar is null)
            return;

        Cue? cue = null;
        var placeholder = "未载入字幕";

        if (_track.Count == 0)
        {
            placeholder = _player.IsLoaded ? "未匹配字幕" : "未选择节目";
        }
        else if (_cueIndex >= 0 && _cueIndex < _track.Count)
        {
            cue = _track.Cues[_cueIndex];
        }
        else
        {
            placeholder = "···";
        }

        var sub = cue is null || !_config.ShowTranslation ? string.Empty : cue.Translation;

        _bar.SetContent(
            cue,
            placeholder,
            sub,
            _config.ShowTime ? Format(position) : string.Empty,
            position,
            _config.ShowTranslation);
    }

    private void UpdateTransport()
    {
        var loaded = _player.IsLoaded;

        RefreshPlayIcons();

        SyncAudioBox();

        // 同步调 shell（NIM_MODIFY），shell 忙的时候这里会等，所以单独记一步
        Diag.StallWatch.Crumb("UpdateTransport: tray tooltip");
        TrayHost.SetTooltip(loaded
            ? $"LoafEar · {Path.GetFileName(_player.FilePath)}"
            : "LoafEar");
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";

    /// <summary>进度条两头：左边走到哪了，右边一共多长（设计稿分的两处）。</summary>
    private void UpdateTimeLabels(TimeSpan position, TimeSpan total)
    {
        TimeLabel.Text = Format(position);
        DurationLabel.Text = Format(total);
    }

    // ------------------------------------------------------------------ 右键快捷菜单

    private void OnBarRightClicked(object? sender, Point screenPoint)
    {
        Trace($"right-click received {_clock.ElapsedMilliseconds}ms");

        // 还在恢复会话：字幕条这时候本来不该露头（万一露了），
        // 先把点的位置记住，就绪后立刻补开菜单，用户这一下不算白点。
        if (!_sessionReady)
        {
            _pendingMenuPoint = screenPoint;
            Trace("right-click deferred (session not ready)");
            return;
        }

        OpenQuickMenu(screenPoint);
    }

    private QuickMenuModel BuildMenuModel() => new()
    {
        IsLoaded = _player.IsLoaded,
        IsPlaying = _player.IsPlaying,
        HasTrack = _track.Count > 0,
        Loop = _config.LoopSingleCue,
        ShowTranslation = _config.ShowTranslation,
        IsHidden = _userHidden,
        Speed = _config.Speed,
        Items = _library,
        CurrentAudioPath = _player.FilePath,

        ToggleGesture = _config.HotkeyToggle.Display,
        PlayGesture = _config.HotkeyPlay.Display,
        PrevGesture = _config.HotkeyPrev.Display,
        NextGesture = _config.HotkeyNext.Display,
        LoopGesture = _config.HotkeyLoop.Display,

        // 菜单打开这一拍就定下「收藏 / 取消收藏」，不跟着播放位置实时变
        CanFavorite = _player.IsLoaded && _track.Count > 0,
        IsFavorited = IsCurrentCueFavorited(),

        FavoriteGesture = _config.HotkeyCollect.Display,

        OnPlayPause = () =>
        {
            if (!_player.IsLoaded)
                return;
            _player.TogglePlay();
            UpdateTransport();
        },

        OnPrevCue = () => JumpCue(-1),
        OnNextCue = () => JumpCue(1),
        OnOpenSettings = ShowMainWindow,
        OnToggleVisible = ToggleBossKey,
        OnToggleFavorite = ToggleFavoriteCurrent,
        OnExit = RequestExit,
        OnSelectItem = LoadItem,

        OnSpeed = value =>
        {
            _config.Speed = value;
            _player.Speed = value;
            RefreshSpeedBox();
            _config.Save();
        },

        OnLoop = SetLoop,

        OnTranslation = value =>
        {
            _config.ShowTranslation = value;
            _suppressEvents = true;
            TranslationCheck.IsChecked = value;
            _suppressEvents = false;
            RefreshBar();
            _config.Save();
        },
    };

    private void OpenQuickMenu(Point screenPoint)
    {
        var menu = QuickMenu.Build(BuildMenuModel());
        Trace($"menu built {_clock.ElapsedMilliseconds}ms");

        // 上一次的菜单还开着就先收掉：连续右键不该堆出好几个 Popup 窗口
        if (_quickMenu is { IsOpen: true })
            _quickMenu.IsOpen = false;

        _quickMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quickMenu, menu))
                _quickMenu = null;
        };

        // 定位用字幕条自己的 DPI（它已经有窗口句柄），坐标用屏幕绝对点：
        // MousePoint 依赖真实光标位置，脚本注入或高 DPI 缩放下容易偏。
        var dpi = VisualTreeHelper.GetDpi(_bar ?? (Visual)this);

        menu.PlacementTarget = _bar;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = screenPoint.X / dpi.DpiScaleX;
        menu.VerticalOffset = screenPoint.Y / dpi.DpiScaleY;
        menu.IsOpen = true;
        Trace($"menu opened {_clock.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 预热右键菜单：把模板展开、文字排版这些一次性开销提前到启动后的空闲时做掉，
    /// 第一次右键就不用现付。冷启动时这笔开销可能到几百毫秒，而这段时间界面线程
    /// 不走消息，字幕条（explorer 的子窗口）不回消息就会连带卡住任务栏。
    /// 只展开不弹窗，所以屏幕上不会闪任何东西。
    /// </summary>
    private void WarmUpQuickMenu()
    {
        try
        {
            var menu = QuickMenu.Build(BuildMenuModel());
            menu.ApplyTemplate();
            menu.Measure(new Size(520, 900));
            menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
            Trace($"menu warmed {_clock.ElapsedMilliseconds}ms");
        }
        catch
        {
            // 预热失败不影响真正弹菜单
        }
    }

    // ------------------------------------------------------------------ 交互事件

    // ------------------------------------------------------------------ 分类切换

    /// <summary>0 播放 / 1 收藏 / 2 常规 / 3 快捷键 / 4 关于（设计稿把「外观」并进「常规」）。</summary>
    internal void SelectPage(int index)
    {
        var pages = new FrameworkElement[]
        {
            PagePlay, PageFavorites, PageGeneral, PageHotkey, PageAbout,
        };

        var navs = new[] { NavPlay, NavFavorites, NavGeneral, NavHotkey, NavAbout };

        for (var i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        // IsChecked 不会触发 Click，所以这里不会递归
        navs[index].IsChecked = true;

        // 收藏页每次进来都重读一遍列表：别的入口（热键、右键菜单）刚收的句子要能立刻看见
        if (index == 1)
            RefreshFavorites();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, NavPlay))
            SelectPage(0);
        else if (ReferenceEquals(sender, NavFavorites))
            SelectPage(1);
        else if (ReferenceEquals(sender, NavGeneral))
            SelectPage(2);
        else if (ReferenceEquals(sender, NavHotkey))
            SelectPage(3);
        else if (ReferenceEquals(sender, NavAbout))
            SelectPage(4);
    }

    /// <summary>播放页那颗星：和右键「收藏本句」、Ctrl+Alt+S 走同一条开关。</summary>
    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        ToggleFavoriteCurrent();
    }

    /// <summary>
    /// 关于页「检查更新」。程序不联网，所以这里不假装去查服务器，
    /// 直接说清离线单文件版的升级方式，免得放一个点了没反应的假按钮。
    /// </summary>
    private void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"当前版本 {AppVersion}\n\nLoafEar 不联网、不上传，没有在线更新通道。\n换新版本时直接替换 exe，config.json 和 favorites.json 留在原目录就会继续生效。",
            "LoafEar",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppConfig.ConfigDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"打不开配置目录：{ex.Message}", false);
        }
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);

    private void OnOpenIssues(object sender, RoutedEventArgs e) => OpenUrl(IssuesUrl);

    /// <summary>用系统默认浏览器打开链接；打不开就给一句轻提示。</summary>
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"打不开链接：{ex.Message}", false);
        }
    }

    private void OnTogglePlay(object sender, RoutedEventArgs e) => TogglePlayback();

    private void OnToggleLoop(object sender, RoutedEventArgs e) => ToggleLoop();

    private void OnPrevCue(object sender, RoutedEventArgs e) => JumpCue(-1);

    private void OnNextCue(object sender, RoutedEventArgs e) => JumpCue(1);

    private void JumpCue(int delta)
    {
        if (_track.Count == 0 || !_player.IsLoaded)
            return;

        // 一旦手动跳句，收藏连播就退出，免得两句来回抢播放头
        StopFavoriteQueue();

        var position = _player.Position;
        var index = _track.IndexAt(position);
        var played = index >= 0 ? position - _track.Cues[index].Start : TimeSpan.Zero;

        var target = CueLoop.ResolveJump(
            index, played, delta, _track.Count, restartFirst: !_loop.Enabled);
        if (target < 0)
            return;

        // 定格在目标句的开头：位置一旦落到某句的第一帧，就不用再靠「猜」了。
        _player.Position = _track.Cues[target].Start;
        _cueIndex = -1;
        _loop.Jump(target);
        PushToBar(_player.Position);
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || _demoMode || !_player.IsLoaded)
            return;

        // 手动拖了进度条，收藏连播就此退出
        StopFavoriteQueue();

        _player.Position = TimeSpan.FromSeconds(e.NewValue);
        _cueIndex = -1;
        _loop.Reset();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
            return;

        _config.Theme = ThemeBox.SelectedIndex switch
        {
            1 => BarTheme.Light,
            2 => BarTheme.Dark,
            _ => BarTheme.Auto,
        };

        RefreshBar();
        _config.Save();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
            return;

        _config.ShowTranslation = TranslationCheck.IsChecked == true;
        _config.ShowTime = ShowTimeCheck.IsChecked == true;
        _config.BackgroundTransparent = BgTransparentCheck.IsChecked == true;
        _config.ClickThrough = ClickThroughCheck.IsChecked == true;
        _config.CaptureHide = CaptureHideCheck.IsChecked == true;
        _config.HideOnScreenBusy = ScreenBusyCheck.IsChecked == true;
        _config.MinimizeToTray = TrayCheck.IsChecked == true;

        if (_bar is not null)
        {
            _bar.ApplyAppearance(_config);
            _bar.SetClickThrough(_config.ClickThrough);
            _bar.SetCaptureHide(_config.CaptureHide);
        }

        // 悬浮字幕：勾上立刻飘起来，取消立刻贴回任务栏
        var wantFloat = FloatCheck.IsChecked == true;
        if (wantFloat != _config.FloatBar)
        {
            _config.FloatBar = wantFloat;
            ApplyFloatMode();
        }

        _cueIndex = -1;
        _loop.Reset();
        RefreshBar();
        _config.Save();
    }

    /// <summary>外观变了要让字幕条立刻按新样式重画，否则要等下一次换句。</summary>
    private void RefreshBar()
    {
        if (_bar is null)
            return;

        _bar.ApplyAppearance(_config);

        if (_demoMode)
        {
            TickDemo();
            return;
        }

        var position = _player.IsLoaded ? _player.Position : TimeSpan.Zero;
        _cueIndex = _track.Count > 0 ? _track.IndexAt(position) : -1;
        PushToBar(position);
    }

    // ------------------------------------------------------------------ 播放页节目目录

    /// <summary>目录里的一项：一个音频文件。</summary>
    private sealed class AudioPickItem
    {
        public required string AudioPath { get; init; }
        public required string Title { get; init; }

        public override string ToString() => Title;
    }

    /// <summary>按听力目录重建节目下拉。目录变了、重新扫描之后都要调一次。</summary>
    private void RebuildAudioBox()
    {
        _audioBoxSyncing = true;
        try
        {
            AudioBox.Items.Clear();

            foreach (var item in _library)
                AudioBox.Items.Add(new AudioPickItem { AudioPath = item.AudioPath, Title = item.Title });
        }
        finally
        {
            _audioBoxSyncing = false;
        }

        SyncAudioBox();
        RebuildProgramList();

        Trace($"audio list rebuilt items={AudioBox.Items.Count} selected={AudioBox.SelectedIndex}");
    }

    /// <summary>让下拉选中项跟上「正在播的那个文件」。目录外的音频会补一项进去。</summary>
    private void SyncAudioBox()
    {
        // XAML 还没解析完时控件字段是 null（事件可能早于构造函数体触发）
        if (AudioBox is null || AudioTitleText is null)
            return;

        var current = _player.IsLoaded ? _player.FilePath : null;

        if (string.IsNullOrEmpty(current))
        {
            if (AudioBox.SelectedIndex != -1)
            {
                _audioBoxSyncing = true;
                AudioBox.SelectedIndex = -1;
                _audioBoxSyncing = false;
            }

            UpdateAudioTitle();
            return;
        }

        var index = IndexOfAudio(current);

        if (index < 0)
        {
            // 目录外选的单文件：补一项，否则下拉显示成空，看着像没在播
            _audioBoxSyncing = true;
            AudioBox.Items.Add(new AudioPickItem
            {
                AudioPath = current,
                Title = Path.GetFileNameWithoutExtension(current),
            });
            _audioBoxSyncing = false;
            index = AudioBox.Items.Count - 1;
        }

        if (AudioBox.SelectedIndex != index)
        {
            _audioBoxSyncing = true;
            AudioBox.SelectedIndex = index;
            _audioBoxSyncing = false;
            Trace($"audio box selected={index}");
        }

        UpdateAudioTitle();
    }

    private int IndexOfAudio(string path)
    {
        for (var i = 0; i < AudioBox.Items.Count; i++)
        {
            if (AudioBox.Items[i] is AudioPickItem item
                && string.Equals(item.AudioPath, path, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>没有选中项时标题位置也得有字，不然看着像空白页。</summary>
    private void UpdateAudioTitle()
    {
        var pick = AudioBox.SelectedItem as AudioPickItem;

        AudioTitleText.Text = pick?.Title
                              ?? (_library.Count == 0 ? "未选择节目" : "选择节目");

        // 标题会截断，悬停给出完整路径
        AudioTitleText.ToolTip = pick?.AudioPath
                                 ?? "从左下角「目录」挑一个节目";

        UpdateProgramHighlight();
    }

    /// <summary>按节目清单重建「目录」浮层（loafearui-06）：一行一个节目，当前那个加浅灰底。</summary>
    private void RebuildProgramList()
    {
        if (ProgramList is null)
            return;

        ProgramList.Children.Clear();

        for (var i = 0; i < AudioBox.Items.Count; i++)
        {
            if (AudioBox.Items[i] is not AudioPickItem pick)
                continue;

            var row = new Button
            {
                Content = pick.Title,
                Tag = i,
                Style = (Style)FindResource("D10ProgItem"),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            row.Click += OnProgramPick;
            ProgramList.Children.Add(row);
        }

        UpdateProgramHighlight();
    }

    /// <summary>当前节目那一行铺浅灰底，跟设计稿一致；别的行保持透明。</summary>
    private void UpdateProgramHighlight()
    {
        if (ProgramList is null)
            return;

        var selected = AudioBox?.SelectedIndex ?? -1;

        foreach (var child in ProgramList.Children)
        {
            if (child is not Button row)
                continue;

            var on = row.Tag is int index && index == selected;
            row.Background = on
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0))
                : Brushes.Transparent;
        }
    }

    /// <summary>目录浮层里点了一个节目：交给 AudioBox 换选中项，剩下走原来的加载流程。</summary>
    private void OnProgramPick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button row || row.Tag is not int index)
            return;

        ProgramPopup.IsOpen = false;
        DirButton.IsChecked = false;

        if (index >= 0 && index < AudioBox.Items.Count)
            AudioBox.SelectedIndex = index;
    }

    /// <summary>左下角四道横线：开合节目目录。</summary>
    private void OnDirToggle(object sender, RoutedEventArgs e)
    {
        if (DirButton.IsChecked == true)
        {
            // 每次打开都对着当前清单重铺一遍：刚换过目录、刚加过目录外文件都要看得见
            RebuildProgramList();
            ProgramPopup.IsOpen = true;
        }
        else
        {
            ProgramPopup.IsOpen = false;
        }
    }

    private void OnProgramClosed(object sender, EventArgs e)
    {
        DirButton.IsChecked = false;
    }

    /// <summary>
    /// 目录浮层的位置：**右边缘对齐按钮右边缘**，往上弹。
    /// 浮层（94 宽）比按钮（32 宽）宽得多，居中的话会盖掉左边一排图标；
    /// 万一上面没地方（窗口贴着屏幕顶），第二个候选改成往下弹。
    /// </summary>
    private static CustomPopupPlacement[] PlaceProgramPopup(Size popup, Size target, Point offset)
    {
        var rightAligned = target.Width - popup.Width;

        return new[]
        {
            new CustomPopupPlacement(
                new Point(rightAligned, -popup.Height - 4), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(
                new Point(rightAligned, target.Height + 4), PopupPrimaryAxis.Horizontal),
        };
    }

    /// <summary>
    /// 倍速下拉的位置：紧贴按钮下方 1px，**并且与「1.0X」这几个字居中对齐**。
    /// 按钮本身是无边框文字，宽 52 DIP，而「1.0X」只占约 26 DIP ——
    /// 按按钮居中会把框推到字的右边，看着像没对齐，所以量的是文字那一坨的宽度。
    /// 首选在下面；屏幕底部放不下时第二个候选放上面（WPF 挑可见面积大的那个）。
    /// </summary>
    private CustomPopupPlacement[] PlaceSpeedPopup(Size popup, Size target, Point offset)
    {
        // 框的左右中心压在「1.0X」这坨字的墨迹中心上。
        // 不能用控件宽度：控件是 52 DIP 的无边框文字，字只占一半，按控件居中会偏右。
        // ⚠ 这个回调里 popup/target 的尺寸是**物理像素**（不是 DIP），
        // 而 SpeedTextCenter() 量出来的是 DIP —— 125% 缩放下差 1.25 倍，必须换算。
        var scale = VisualTreeHelper.GetDpi(SpeedBox).DpiScaleX;
        var centered = SpeedTextCenter() * scale - popup.Width / 2;

        LastSpeedPlacement =
            $"popup={popup.Width:0.00}x{popup.Height:0.00} target={target.Width:0.00}x{target.Height:0.00} " +
            $"x={centered:0.00} y={target.Height + 1:0.00}";

        return new[]
        {
            new CustomPopupPlacement(
                new Point(centered, target.Height + 1), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(
                new Point(centered, -popup.Height - 1), PopupPrimaryAxis.Horizontal),
        };
    }

    /// <summary>诊断用：倍速浮层最后一次摆放时 WPF 给的尺寸与算出的坐标。</summary>
    internal string LastSpeedPlacement = string.Empty;

    /// <summary>
    /// 倍速按钮上那行字（1.0X）在按钮坐标系里的**墨迹中心**（DIP）。
    /// 用 FormattedText 量实际字形包围盒：文字的排版宽度含两侧留白，
    /// 「1」左边就空 4px，按排版宽度算出来会偏左半个留白。
    /// </summary>
    private double SpeedTextCenter()
    {
        var formatted = SpeedFormattedText();
        if (formatted is null)
            return SpeedBox.ActualWidth / 2;

        var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        return ink.Left + ink.Width / 2;
    }

    /// <summary>把倍速按钮上那行字按控件同款字体排出来，量墨迹用。</summary>
    private FormattedText? SpeedFormattedText()
    {
        var text = (SpeedBox.SelectedItem as ComboBoxItem)?.Content as string
                   ?? SpeedBox.Text;

        if (string.IsNullOrEmpty(text))
            return null;

        return new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(SpeedBox.FontFamily, SpeedBox.FontStyle, SpeedBox.FontWeight, SpeedBox.FontStretch),
            SpeedBox.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(SpeedBox).PixelsPerDip);
    }

    /// <summary>诊断用：倍速那行字的排版宽度与墨迹包围盒。</summary>
    internal string SpeedTextMetricsForTest()
    {
        var formatted = SpeedFormattedText();
        if (formatted is null)
            return "text=(空)";

        var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;

        var site = SpeedBox.Template?.FindName("ContentSite", SpeedBox) as FrameworkElement;
        var siteX = site is null
            ? -1
            : site.TransformToAncestor(SpeedBox).Transform(new Point(0, 0)).X;
        var siteW = site?.ActualWidth ?? -1;

        return $"控件宽={SpeedBox.ActualWidth:0.00} 排版宽={formatted.Width:0.00} " +
               $"墨迹 left={ink.Left:0.00} w={ink.Width:0.00} 中心={SpeedTextCenter():0.00} " +
               $"ContentSite x={siteX:0.00} w={siteW:0.00}";
    }

    /// <summary>在下拉里点了一个节目：换音频、换字幕、开播。</summary>
    private void OnAudioPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_audioBoxSyncing)
            return;

        if (AudioBox.SelectedItem is not AudioPickItem pick)
            return;

        // 诊断用：LoadItem 之后清单会被重铺，SelectedItem 会被清掉，
        // 所以在这里先留一份「刚才点的是什么」
        _lastPickedTitle = pick.Title;

        if (string.Equals(pick.AudioPath, _player.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        // 目录里的条目带着扫描出的字幕配对，目录外的只能靠同目录同名字幕
        var item = _library.FirstOrDefault(
            i => string.Equals(i.AudioPath, pick.AudioPath, StringComparison.OrdinalIgnoreCase))
            ?? new LibraryItem { AudioPath = pick.AudioPath };

        StopFavoriteQueue();
        LoadItem(item);
        _player.Play();
        UpdateTransport();
        // 不弹「正在播放 · xxx」的角标：标题就在播放页顶上摆着，弹一次反而挡视线
    }

    // ------------------------------------------------------------------ 听力目录

    /// <summary>
    /// 播放下拉旁边的「…」：挑一个目录外的音频。配同目录同名字幕，记进「上次播放」。
    /// 日常选节目用下拉，这里只是给目录外的文件留个口子。
    /// </summary>
    private void OnPickAudio(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择音频文件",
            Filter = "音频文件|*.mp3;*.m4a;*.m4b;*.aac;*.wav;*.flac;*.wma;*.ogg;*.opus|所有文件|*.*",
            CheckFileExists = true,
        };

        var folder = !string.IsNullOrWhiteSpace(_config.LastAudioPath)
            ? Path.GetDirectoryName(_config.LastAudioPath)
            : _config.LibraryDirectory;

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) != true)
            return;

        var path = dialog.FileName;

        StopFavoriteQueue();

        if (!LoadAudio(path))
            return;

        var subtitle = SubtitleParser.FindSibling(path);

        if (subtitle is not null)
        {
            LoadSubtitle(subtitle);
        }
        else
        {
            // 没字幕就只放音频，别把上一集的字幕留在这儿对错口型
            _rawTrack = SubtitleTrack.Empty();
            _track = SubtitleTrack.Empty();
            _cueIndex = -1;
            _loop.Reset();
            RefreshBar();
            UpdateTransport();
        }

        _player.Play();
        SetStatus($"已选择 · {Path.GetFileName(path)}", true);
    }

    private void OnPickLibrary(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择听力目录",
        };

        if (Directory.Exists(_config.LibraryDirectory))
            dialog.InitialDirectory = _config.LibraryDirectory;
        else if (Directory.Exists(_config.LastAudioPath is null
                     ? null
                     : Path.GetDirectoryName(_config.LastAudioPath)))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_config.LastAudioPath)!;
        }

        if (dialog.ShowDialog(this) != true)
            return;

        _config.LibraryDirectory = dialog.FolderName;
        LibraryPathText.Text = dialog.FolderName;
        LibraryPathText.ToolTip = dialog.FolderName;
        _config.Save();

        ScanLibrary(loadCurrent: true);
    }

    private void OnRescanLibrary(object sender, RoutedEventArgs e)
        => ScanLibrary(loadCurrent: false);

    private void ScanLibrary(bool loadCurrent)
    {
        if (string.IsNullOrWhiteSpace(_config.LibraryDirectory))
        {
            SetStatus("先选择一个听力目录", false);
            return;
        }

        _library = MediaLibrary.Scan(_config.LibraryDirectory);

        var withSubs = _library.Count(i => i.HasSubtitle);
        ItemCountText.Text = _library.Count == 0
            ? "目录里没有找到音频文件"
            : $"识别到 {_library.Count} 个节目 · {withSubs} 个带字幕";

        SetStatus(
            _library.Count == 0
                ? "目录里没有音频"
                : $"已识别 {_library.Count} 个节目",
            _library.Count > 0);

        RebuildAudioBox();

        if (_library.Count == 0 || !loadCurrent)
            return;

        // 优先回到上次播放的那个文件
        var resume = _library.FirstOrDefault(
            i => string.Equals(i.AudioPath, _config.LastAudioPath, StringComparison.OrdinalIgnoreCase));

        LoadItem(resume ?? _library[0]);
    }

    /// <summary>切换到目录里的某个节目：换音频、换字幕、从头开始。</summary>
    private void LoadItem(LibraryItem item)
    {
        // 手动换节目 = 退出收藏连播
        StopFavoriteQueue();

        if (!LoadAudio(item.AudioPath))
            return;

        var subtitle = item.SubtitlePath;

        // 目录扫描没配上字幕时，再按同目录同名兜底找一次
        subtitle ??= SubtitleParser.FindSibling(item.AudioPath);

        if (subtitle is not null)
        {
            LoadSubtitle(subtitle);
        }
        else
        {
            _rawTrack = SubtitleTrack.Empty();
            _track = SubtitleTrack.Empty();
            _cueIndex = -1;
            _loop.Reset();
            RefreshBar();
            UpdateTransport();
        }
    }

    private bool LoadAudio(string path)
    {
        try
        {
            Trace("audio.load begin");
            StopDemo();

            _player.Load(path, _config.Volume);
            _player.Speed = _config.Speed;
            Trace($"audio.load device ready {_clock.ElapsedMilliseconds}ms");

            _config.LastAudioPath = path;
            if (string.IsNullOrWhiteSpace(_config.LibraryDirectory))
                _config.LibraryDirectory = Path.GetDirectoryName(path);

            _cueIndex = -1;
            _loop.Reset();
            UpdateTransport();
            _config.Save();

            // 换节目了：「本节目」这个筛选的结果跟着变
            RefreshFavorites();
            return true;
        }
        catch (Exception ex)
        {
            LogError("LoadAudio", ex);
            SetStatus($"加载失败：{ex.Message}", false);
            return false;
        }
    }

    private void LoadSubtitle(string path)
    {
        try
        {
            _rawTrack = SubtitleParser.Load(path);
            RebuildTrack();

            _config.LastSubtitlePath = path;
            _cueIndex = -1;
            _loop.Reset();
            UpdateTransport();
            RefreshBar();
            _config.Save();
        }
        catch (Exception ex)
        {
            LogError("LoadSubtitle", ex);
            SetStatus($"字幕加载失败：{ex.Message}", false);
        }
    }

    /// <summary>按当前偏移量从原始字幕派生显示用的字幕。</summary>
    private void RebuildTrack()
    {
        var delta = TimeSpan.FromMilliseconds(_config.SubtitleOffsetMs);
        _track = _rawTrack.Shift(delta);
        _cueIndex = -1;
    }

    /// <summary>后台恢复会话的产物：目录、开好的播放器、解析好的字幕。</summary>
    private sealed class RestoreResult
    {
        public List<LibraryItem>? Library { get; init; }
        public AudioPlayer? Player { get; init; }
        public SubtitleTrack? Track { get; init; }
        public string? AudioPath { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// 恢复上次的会话。扫目录、开声卡、解析字幕都放到后台线程 ——
    /// 这三件事在冷启动时可能各要几百毫秒到一两秒，一旦压在界面线程上，
    /// 字幕条（explorer 的子窗口）就不回消息，任务栏和鼠标会跟着一起假死。
    /// 之前「刚打开时右键卡死，等一会儿又好了」就是这段时间。
    /// </summary>
    private void StartSessionRestore()
    {
        var libraryDir = _config.LibraryDirectory;
        var lastAudio = _config.LastAudioPath;
        var lastSubtitle = _config.LastSubtitlePath;
        var volume = _config.Volume;
        var speed = _config.Speed;

        Trace("restore.begin (background)");

        Task.Run(() =>
        {
            try
            {
                var library = MediaLibrary.Scan(libraryDir);
                Trace($"scan done ({library.Count}) {_clock.ElapsedMilliseconds}ms");

                LibraryItem? pick = null;

                // 优先回到上次播放的那一集。播放页手选的音频可能不在听力目录里，
                // 那种情况它比「目录里的第一个」更该被恢复，否则下次启动会悄悄换掉。
                if (!string.IsNullOrWhiteSpace(lastAudio) && File.Exists(lastAudio))
                {
                    pick = library.FirstOrDefault(
                               i => string.Equals(
                                   i.AudioPath, lastAudio, StringComparison.OrdinalIgnoreCase))
                           ?? new LibraryItem { AudioPath = lastAudio };
                }

                if (pick is null && library.Count > 0)
                    pick = library[0];

                if (pick is null)
                    return new RestoreResult { Library = library };

                // 目录扫描没配上字幕时，再按同目录同名兜底找一次，最后才用上次的字幕
                var subtitle = pick.SubtitlePath ?? SubtitleParser.FindSibling(pick.AudioPath);

                if (subtitle is null
                    && !string.IsNullOrWhiteSpace(lastSubtitle)
                    && File.Exists(lastSubtitle))
                {
                    subtitle = lastSubtitle;
                }

                SubtitleTrack? track = null;

                if (subtitle is not null)
                {
                    try
                    {
                        track = SubtitleParser.Load(subtitle);
                    }
                    catch (Exception ex)
                    {
                        LogError("LoadSubtitle", ex);
                        subtitle = null;
                    }
                }

                // 播放器在后台建：WaveOut 打开声卡这一步最慢，别让界面线程等
                var player = new AudioPlayer();
                player.Load(pick.AudioPath, volume);
                player.Speed = speed;

                Trace($"audio+subs ready {_clock.ElapsedMilliseconds}ms");

                return new RestoreResult
                {
                    Library = library,
                    Player = player,
                    AudioPath = pick.AudioPath,
                    Track = track,
                };
            }
            catch (Exception ex)
            {
                LogError("StartSessionRestore", ex);
                return new RestoreResult { Error = ex.Message };
            }
        }).ContinueWith(task =>
        {
            var result = task.IsFaulted
                ? new RestoreResult { Error = task.Exception?.GetBaseException().Message }
                : task.Result;

            // 关程序时 Dispatcher 可能已经停了，这里投不进去也无所谓
            try
            {
                Dispatcher.BeginInvoke(new Action(() => FinishRestore(result)));
            }
            catch
            {
                // 忽略：进程正在退出
            }
        });
    }

    /// <summary>后台结果贴回界面：换播放器、换字幕、回到上次位置，然后才允许字幕条出现。</summary>
    private void FinishRestore(RestoreResult result)
    {
        Trace($"restore applied {_clock.ElapsedMilliseconds}ms");

        try
        {
            if (result.Library is not null)
            {
                _library = result.Library;

                var withSubs = _library.Count(i => i.HasSubtitle);
                ItemCountText.Text = _library.Count == 0
                    ? "目录里没有找到音频文件"
                    : $"识别到 {_library.Count} 个节目 · {withSubs} 个带字幕";
            }

            if (result.Player is not null && result.AudioPath is not null)
            {
                // 旧的播放器还没加载过任何东西，直接丢掉换新的；
                // 这样界面线程从头到尾没碰过「正在初始化」的半成品状态。
                _player.Dispose();
                _player = result.Player;
                HookPlayer();

                _config.LastAudioPath = result.AudioPath;

                if (string.IsNullOrWhiteSpace(_config.LibraryDirectory))
                    _config.LibraryDirectory = Path.GetDirectoryName(result.AudioPath);

                _rawTrack = result.Track ?? SubtitleTrack.Empty();
                RebuildTrack();
                _cueIndex = -1;
                _loop.Reset();

                ResumePosition();
                _config.Save();
            }
            else
            {
                // 没目录也没听过东西：摆一条测试字幕，至少能看出程序在工作
                StartDemoSubtitles();
                Trace($"demo subtitles {_clock.ElapsedMilliseconds}ms");

                if (result.Error is not null)
                    SetStatus($"恢复上次进度失败：{result.Error}", false);
            }

            // 节目下拉放在播放器就位之后再建：重建时会顺手把选中项对到当前音频，
            // 提前建的话 _player 还是空的，选中的永远是最上面那个。
            RebuildAudioBox();
        }
        catch (Exception ex)
        {
            LogError("FinishRestore", ex);
        }
        finally
        {
            // 不管上面哪一步出事，字幕条都得显示出来 ——
            // 这是用户唯一能看到程序在工作的东西，不能因为一次加载失败就再也不露面。
            MarkSessionReady();
        }
    }

    /// <summary>
    /// 启动流程走完，字幕条到这一刻才允许露头。
    /// 它一出现鼠标就可能点上来，所以必须保证界面线程此刻已经空闲。
    /// </summary>
    private void MarkSessionReady()
    {
        if (_sessionReady)
            return;

        _sessionReady = true;
        Trace("session ready");

        RefreshBar();
        UpdateTransport();
        Trace($"bar content refreshed {_clock.ElapsedMilliseconds}ms");

        // 预热放在字幕条显形之前同步做：这笔一次性开销就落在"还没露头"的阶段，
        // 用户第一次右键时菜单模板、字体排版都已经热了。
        WarmUpQuickMenu();

        OnGuardTick(this, EventArgs.Empty);
        Trace($"bar shown {_clock.ElapsedMilliseconds}ms");

        if (_pendingMenuPoint is { } point)
        {
            _pendingMenuPoint = null;
            OpenQuickMenu(point);
        }
    }

    /// <summary>回到上次停下的位置（保持暂停），字幕立刻显示那一句。</summary>
    private void ResumePosition()
    {
        var ms = _config.LastPositionMs;
        var duration = _player.Duration.TotalMilliseconds;

        if (ms > 300 && duration > 0 && ms < duration - 500)
        {
            _player.Position = TimeSpan.FromMilliseconds(ms);
            _cueIndex = -1;
        }

        RefreshBar();
        UpdateTransport();
    }

    // ------------------------------------------------------------------ 收藏

    private static string EpisodeName(string? audioPath) =>
        string.IsNullOrEmpty(audioPath) ? "未知节目" : Path.GetFileNameWithoutExtension(audioPath);

    /// <summary>
    /// 当前这一句的序号。位置优先于 <see cref="_cueIndex"/>：
    /// 暂停时 _cueIndex 可能还停在上一拍，而收藏要看的是「现在屏幕上这句话」。
    /// </summary>
    private int CurrentCueIndex()
    {
        if (_track.Count == 0 || !_player.IsLoaded)
            return -1;

        var index = _track.IndexAt(_player.Position);
        return index >= 0 ? index : _cueIndex;
    }

    /// <summary>当前这一句收过没有。右键菜单靠它决定写「收藏本句」还是「取消收藏」。</summary>
    private bool IsCurrentCueFavorited()
    {
        var index = CurrentCueIndex();
        var path = _player.FilePath;

        return index >= 0 && !string.IsNullOrEmpty(path) && _favorites.Contains(path, index);
    }

    /// <summary>
    /// 收藏 / 取消收藏当前这句。热键 Ctrl+Alt+S 和字幕条右键「收藏本句」共用。
    /// 已经收过的再按一次就取消 —— 学完一句当场划掉，比翻到收藏页找更顺。
    /// </summary>
    private void ToggleFavoriteCurrent()
    {
        var index = CurrentCueIndex();
        var path = _player.FilePath;

        if (index < 0 || index >= _track.Count || string.IsNullOrEmpty(path))
        {
            SetStatus("这一拍没有字幕可收藏", false);
            return;
        }

        var cue = _track.Cues[index];
        var meta = $"{EpisodeName(path)} · {index + 1:00}";

        if (_favorites.Contains(path, index))
        {
            _favorites.Remove(path, index);
            SetStatus($"已取消收藏 · {meta}", true);
        }
        else
        {
            _favorites.Add(new FavoriteCue
            {
                AudioPath = path,
                Episode = EpisodeName(path),
                CueIndex = index,
                StartMs = cue.Start.TotalMilliseconds,
                EndMs = cue.End.TotalMilliseconds,
                Text = cue.Text,
                Translation = cue.Translation,
                AddedAt = DateTime.Now,
            });

            SetStatus($"已收藏 · {meta}", true);
        }

        RefreshFavorites();
        RefreshStarButton();
    }

    private List<FavoriteCue> QueryFavorites()
    {
        // 离屏截图用：不碰真实收藏文件，只摆几行样例看排版
        if (_favPreview is not null)
            return _favPreview;

        var items = _favorites.Items.ToList();
        var current = FavoriteCue.Normalize(_player.FilePath);

        switch (_favFilter)
        {
            case FavoriteFilter.Episode:
                items = string.IsNullOrEmpty(current)
                    ? new List<FavoriteCue>()
                    : items.Where(i => FavoriteCue.Normalize(i.AudioPath) == current)
                        .OrderBy(i => i.CueIndex)
                        .ToList();
                break;

            case FavoriteFilter.Recent:
                items = items.OrderByDescending(i => i.AddedAt).Take(50).ToList();
                break;

            default:
                items = items
                    .OrderBy(i => i.Episode, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(i => i.CueIndex)
                    .ToList();
                break;
        }

        return items;
    }

    /// <summary>重画收藏页：筛选态、行、空态提示、计数都跟着走。</summary>
    private void RefreshFavorites()
    {
        var items = QueryFavorites();

        FavFilterAll.IsChecked = _favFilter == FavoriteFilter.All;
        FavFilterRecent.IsChecked = _favFilter == FavoriteFilter.Recent;
        FavFilterEpisode.IsChecked = _favFilter == FavoriteFilter.Episode;

        FavList.Children.Clear();
        foreach (var item in items)
            FavList.Children.Add(BuildFavoriteRow(item));

        var empty = items.Count == 0;
        FavEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (empty)
        {
            FavEmptyText.Text = _favFilter == FavoriteFilter.Episode
                ? $"这一集还没有收藏。播放时按 {_config.HotkeyCollect.Display} 收下当前这句。"
                : $"还没有收藏。播放时按 {_config.HotkeyCollect.Display}，或在字幕条上右键「收藏本句」。";
        }

        FavCountText.Text = $"共 {items.Count} 句";
        FavPlayAllButton.IsEnabled = !empty;
        FavExportButton.IsEnabled = !empty;
    }

    /// <summary>
    /// 收藏列表的一行：星标 + 主句 / 译文 + 右侧「E01-1 · 03」。
    /// 整行可点（跳到这一句并开单句循环），星标单独可点（取消收藏）。
    /// </summary>
    private FrameworkElement BuildFavoriteRow(FavoriteCue item)
    {
        var ink = new SolidColorBrush(Ink);
        var muted = new SolidColorBrush(Muted);
        var hairline = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
        var missing = FavoriteStore.IsMissing(item);

        var content = new Grid
        {
            Height = 38,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = missing
                ? "这个音频文件已经不在原来的位置了"
                : $"点击：跳到这一句并单句循环（{item.Meta}）",
        };

        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 星标：点亮的实心星 = 已收藏，悬停想取消时淡下去
        var star = new Button
        {
            Width = 22,
            Height = 22,
            Style = (Style)Application.Current.Resources["IconButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Content = Icons.MakeStar(13, missing ? muted : ink),
            ToolTip = "取消收藏",
        };

        star.Click += (_, e) =>
        {
            e.Handled = true;
            _favorites.Remove(item);
            RefreshFavorites();
            SetStatus($"已取消收藏 · {item.Meta}", true);
        };

        star.MouseEnter += (_, _) => star.Content = Icons.MakeStar(13, muted);
        star.MouseLeave += (_, _) => star.Content = Icons.MakeStar(13, missing ? muted : ink);
        Grid.SetColumn(star, 0);
        content.Children.Add(star);

        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
        };

        texts.Children.Add(new TextBlock
        {
            Text = item.Text,
            FontSize = 11.5,
            Foreground = missing ? muted : ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        if (!string.IsNullOrWhiteSpace(item.Translation))
        {
            texts.Children.Add(new TextBlock
            {
                Text = item.Translation,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        Grid.SetColumn(texts, 1);
        content.Children.Add(texts);

        var meta = new TextBlock
        {
            Text = item.Meta,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(meta, 2);
        content.Children.Add(meta);

        // 整行点击 = 播这一句（星标自己处理过点击，不会走到这里）
        content.MouseEnter += (_, _) => content.Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        content.MouseLeave += (_, _) => content.Background = Brushes.Transparent;
        content.MouseLeftButtonUp += (_, _) => PlayFavorite(item);

        // 1px 分隔线用独立 Border 画：BorderThickness 在 1.25 缩放下会被摊成半像素、颜色被冲淡
        var wrapper = new Grid();
        wrapper.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wrapper.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

        Grid.SetRow(content, 0);
        wrapper.Children.Add(content);

        var line = new Border { Height = 1, Background = hairline };
        Grid.SetRow(line, 1);
        wrapper.Children.Add(line);

        return wrapper;
    }

    /// <summary>把收藏指向的音频 / 字幕换成当前会话正在用的那一套。</summary>
    private bool LoadFavoriteSource(FavoriteCue item)
    {
        if (FavoriteStore.IsMissing(item))
        {
            SetStatus("这个音频已经不在了（可能换过目录）", false);
            return false;
        }

        var sameAudio = string.Equals(
            FavoriteCue.Normalize(item.AudioPath),
            FavoriteCue.Normalize(_player.FilePath),
            StringComparison.Ordinal);

        if (!sameAudio)
        {
            if (!LoadAudio(item.AudioPath))
                return false;

            var subtitle = _library
                               .FirstOrDefault(i => string.Equals(
                                   FavoriteCue.Normalize(i.AudioPath),
                                   FavoriteCue.Normalize(item.AudioPath),
                                   StringComparison.Ordinal))
                               ?.SubtitlePath
                           ?? SubtitleParser.FindSibling(item.AudioPath);

            if (subtitle is not null)
                LoadSubtitle(subtitle);
        }
        else if (_track.Count == 0)
        {
            // 同一个文件但当前没装字幕（换过字幕文件、或之前加载失败），再试一次
            var subtitle = SubtitleParser.FindSibling(item.AudioPath);
            if (subtitle is not null)
                LoadSubtitle(subtitle);
        }

        return _track.Count > 0;
    }

    /// <summary>收藏里记的是第几句；字幕被改过时按文本找，再不行按时间就近。</summary>
    private int ResolveFavoriteIndex(FavoriteCue item)
    {
        if (_track.Count == 0)
            return -1;

        if (item.CueIndex >= 0 && item.CueIndex < _track.Count
            && string.Equals(_track.Cues[item.CueIndex].Text, item.Text, StringComparison.Ordinal))
        {
            return item.CueIndex;
        }

        for (var i = 0; i < _track.Count; i++)
        {
            if (string.Equals(_track.Cues[i].Text, item.Text, StringComparison.Ordinal))
                return i;
        }

        return _track.IndexAt(TimeSpan.FromMilliseconds(item.StartMs));
    }

    /// <summary>点一行：跳到这一句、开单句循环、开播。列表底部那句话写的就是这个。</summary>
    private void PlayFavorite(FavoriteCue item)
    {
        StopFavoriteQueue();

        if (!LoadFavoriteSource(item))
            return;

        var index = ResolveFavoriteIndex(item);
        if (index < 0 || index >= _track.Count)
        {
            SetStatus("字幕里已经找不到这一句了", false);
            return;
        }

        // 循环开着才叫「循环这一句」；SetLoop 会同时刷图标和右键菜单里的状态
        if (!_config.LoopSingleCue)
            SetLoop(true);

        var cue = _track.Cues[index];
        _player.Position = cue.Start;
        _cueIndex = index;
        _loop.Jump(index);
        _player.Play();
        ShowPreview(cue, index);
        PushToBar(cue.Start);
        UpdateTransport();
        SetStatus($"循环 · {item.Meta}", true);
    }

    private void OnPlayAllFavorites(object sender, RoutedEventArgs e)
    {
        // 这颗钮兼「播放 / 暂停」：连播已经在跑就暂停，暂停着就接着放，
        // 只有没起过连播时才真正去开一轮新的。
        if (_favQueuePos >= 0)
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                SetStatus("已暂停", true);
            }
            else
            {
                _player.Play();
                SetStatus($"连播 {_favQueuePos + 1}/{_favQueue.Count}", true);
            }

            UpdateTransport();
            return;
        }

        StartFavoriteQueue();
    }

    private void StartFavoriteQueue()
    {
        var items = QueryFavorites();
        if (items.Count == 0)
        {
            SetStatus("没有可播的收藏", false);
            return;
        }

        // 连播时不能开单句循环，否则第一句就出不去了
        if (_config.LoopSingleCue)
            SetLoop(false);

        _favQueue = items;
        PlayFavoriteAt(0);
    }

    /// <summary>连播到队列里的第几句。换文件、换字幕都在这里统一处理。</summary>
    private void PlayFavoriteAt(int position)
    {
        if (position < 0 || position >= _favQueue.Count)
        {
            StopFavoriteQueue();
            return;
        }

        var item = _favQueue[position];

        if (!LoadFavoriteSource(item))
        {
            // 这一条放不出来（文件没了），跳过继续，别让整串卡死
            PlayFavoriteAt(position + 1);
            return;
        }

        var index = ResolveFavoriteIndex(item);
        if (index < 0 || index >= _track.Count)
        {
            PlayFavoriteAt(position + 1);
            return;
        }

        var cue = _track.Cues[index];
        _favQueuePos = position;
        _favCueEndMs = cue.End.TotalMilliseconds;

        _player.Position = cue.Start;
        _cueIndex = index;
        _loop.Jump(index);
        _player.Play();
        ShowPreview(cue, index);
        PushToBar(cue.Start);
        UpdateTransport();
        SetStatus($"连播 {position + 1}/{_favQueue.Count} · {item.Meta}", true);
    }

    private void AdvanceFavoriteQueue()
    {
        var next = _favQueuePos + 1;

        if (next >= _favQueue.Count)
        {
            // 最后一句放完就停在这儿，别让音频自己往下漂到这一集中间
            StopFavoriteQueue();
            _player.Pause();
            UpdateTransport();
            SetStatus("收藏已播完", true);
            return;
        }

        PlayFavoriteAt(next);
    }

    private void StopFavoriteQueue()
    {
        _favQueuePos = -1;
        _favQueue.Clear();
    }

    private void OnExportFavorites(object sender, RoutedEventArgs e)
    {
        var items = QueryFavorites();
        if (items.Count == 0)
            return;

        var suggested = _favFilter switch
        {
            FavoriteFilter.Episode => $"{EpisodeName(_player.FilePath)} 收藏.txt",
            FavoriteFilter.Recent => "最近收藏.txt",
            _ => "全部收藏.txt",
        };

        var dialog = new SaveFileDialog
        {
            Title = "导出收藏",
            Filter = "文本文件|*.txt",
            FileName = suggested,
            InitialDirectory = Directory.Exists(_config.LibraryDirectory)
                ? _config.LibraryDirectory
                : AppConfig.ConfigDirectory,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var sb = new StringBuilder();

            foreach (var item in items)
            {
                sb.AppendLine(item.Meta);
                sb.AppendLine(item.Text);

                if (!string.IsNullOrWhiteSpace(item.Translation))
                    sb.AppendLine(item.Translation);

                sb.AppendLine();
            }

            // 带 BOM：记事本、WPS 打开中文都不乱码
            File.WriteAllText(
                dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));

            SetStatus($"已导出 {items.Count} 句 · {Path.GetFileName(dialog.FileName)}", true);
        }
        catch (Exception ex)
        {
            LogError("OnExportFavorites", ex);
            SetStatus($"导出失败：{ex.Message}", false);
        }
    }

    private void OnFavFilterClick(object sender, RoutedEventArgs e)
    {        if (sender is not RadioButton button)
            return;

        _favFilter = (button.Tag as string) switch
        {
            "All" => FavoriteFilter.All,
            "Recent" => FavoriteFilter.Recent,
            _ => FavoriteFilter.Episode,
        };

        RefreshFavorites();
    }

    // ------------------------------------------------------------------ 偏移微调

    /// <summary>
    /// 偏移 ±100ms。正数 = 字幕往后推（字幕来得晚），负数 = 提前。
    /// </summary>
    private void NudgeOffset(int deltaMs)
    {
        var before = _config.SubtitleOffsetMs;
        var value = Math.Clamp(before + deltaMs, -OffsetLimitMs, OffsetLimitMs);

        if (Math.Abs(value - before) < 0.5)
        {
            SetStatus($"偏移已经到头（{before:+0;-0;0} ms）", false);
            return;
        }

        _config.SubtitleOffsetMs = value;
        RebuildTrack();
        _cueIndex = -1;
        _loop.Reset();
        RefreshFields();
        RefreshBar();
        _config.Save();

        SetStatus($"偏移 {value:+0;-0;0} ms", true);
    }

    // ------------------------------------------------------------------ 热键

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (ReferenceEquals(_recordingButton, button))
        {
            CancelRecord();
            return;
        }

        CancelRecord();

        _recordingButton = button;
        _recordOriginalContent = button.Content;
        _recordingHotkey = true;

        button.Content = "按下新快捷键…";
        button.Focus();
        Keyboard.Focus(button);

        SetStatus("按下新的组合键，Esc 取消", true);
    }

    private void CancelRecord()
    {
        if (_recordingButton is null)
            return;

        _recordingButton.Content = _recordOriginalContent;
        _recordingButton = null;
        _recordOriginalContent = null;
        _recordingHotkey = false;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 录制中没有按钮被聚焦时也要能捕获，所以挂在窗口级
        if (!_recordingHotkey || _recordingButton is null)
            return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelRecord();
            UpdateHotkeyButtons();
            SetStatus("已取消", true);
            return;
        }

        var spec = HotkeySpec.FromKeyEvent(e);

        // 只按了修饰键，继续等主键
        if (spec is null)
            return;

        if (spec.Modifiers == 0)
        {
            SetStatus("至少要带 Ctrl / Alt / Shift / Win 中的一个", false);
            return;
        }

        var target = (_recordingButton.Tag as string) ?? string.Empty;

        // 七个热键不能重复，否则后注册的那个必然失败
        var others = new (string Tag, HotkeySpec Spec)[]
        {
            ("Toggle", _config.HotkeyToggle),
            ("Play", _config.HotkeyPlay),
            ("Prev", _config.HotkeyPrev),
            ("Next", _config.HotkeyNext),
            ("Loop", _config.HotkeyLoop),
            ("Collect", _config.HotkeyCollect),
            ("OffsetDown", _config.HotkeyOffsetDown),
            ("OffsetUp", _config.HotkeyOffsetUp),
        };

        if (others.Any(o => o.Tag != target && spec.SameAs(o.Spec)))
        {
            SetStatus("这个组合已经被另一个功能占用了", false);
            return;
        }

        switch (target)
        {
            case "Toggle":
                _config.HotkeyToggle = spec;
                break;
            case "Play":
                _config.HotkeyPlay = spec;
                break;
            case "Prev":
                _config.HotkeyPrev = spec;
                break;
            case "Next":
                _config.HotkeyNext = spec;
                break;
            case "Loop":
                _config.HotkeyLoop = spec;
                break;
            case "Collect":
                _config.HotkeyCollect = spec;
                break;
            case "OffsetDown":
                _config.HotkeyOffsetDown = spec;
                break;
            case "OffsetUp":
                _config.HotkeyOffsetUp = spec;
                break;
        }

        CancelRecord();
        UpdateHotkeyButtons();
        ApplyHotkeys();
        _config.Save();
    }

    private void UpdateHotkeyButtons()
    {
        HotkeyToggleButton.Content = _config.HotkeyToggle.Display;
        HotkeyPlayButton.Content = _config.HotkeyPlay.Display;
        HotkeyPrevButton.Content = _config.HotkeyPrev.Display;
        HotkeyNextButton.Content = _config.HotkeyNext.Display;
        HotkeyLoopButton.Content = _config.HotkeyLoop.Display;
        HotkeyCollectButton.Content = _config.HotkeyCollect.Display;
        HotkeyOffsetDownButton.Content = $"{_config.HotkeyOffsetDown.Display} −{OffsetStepMs}";
        HotkeyOffsetUpButton.Content = $"{_config.HotkeyOffsetUp.Display} +{OffsetStepMs}";

        // 偏移输入框的悬浮提醒：键改了这里也得跟着改，不然提示是错的
        OffsetBox.ToolTip =
            $"{_config.HotkeyOffsetDown.Display} 提前 {OffsetStepMs}ms · " +
            $"{_config.HotkeyOffsetUp.Display} 推后 {OffsetStepMs}ms";

        // 状态栏右侧那枚键位块跟着显隐键走，改了键这里也要改
    }

    /// <summary>先全部注销再按当前配置注册，改键后立刻生效。</summary>
    private void ApplyHotkeys()
    {
        UnregisterHotkeys();
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var failed = new List<string>();

        if (!TryRegister(HotkeyToggle, _config.HotkeyToggle))
            failed.Add("老板键");
        if (!TryRegister(HotkeyPlay, _config.HotkeyPlay))
            failed.Add("播放");
        if (!TryRegister(HotkeyPrev, _config.HotkeyPrev))
            failed.Add("上一句");
        if (!TryRegister(HotkeyNext, _config.HotkeyNext))
            failed.Add("下一句");
        if (!TryRegister(HotkeyLoop, _config.HotkeyLoop))
            failed.Add("单句循环");
        if (!TryRegister(HotkeyCollect, _config.HotkeyCollect))
            failed.Add("收藏");
        if (!TryRegister(HotkeyOffsetDown, _config.HotkeyOffsetDown))
            failed.Add("偏移提前");
        if (!TryRegister(HotkeyOffsetUp, _config.HotkeyOffsetUp))
            failed.Add("偏移推后");

        UpdateHotkeyButtons();

        if (failed.Count > 0)
            SetStatus($"快捷键被占用：{string.Join("、", failed)}", false);
    }

    private bool TryRegister(int id, HotkeySpec spec)
    {
        if (!spec.IsValid)
            return false;

        // MOD_NOREPEAT 防止按住不放时反复触发
        return NativeMethods.RegisterHotKey(
            _hwnd, id, spec.Modifiers | NativeMethods.MOD_NOREPEAT, spec.VirtualKey);
    }

    private void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyToggle);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyPlay);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyPrev);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyNext);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyLoop);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyCollect);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyOffsetDown);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyOffsetUp);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case HotkeyToggle:
                ToggleBossKey();
                handled = true;
                break;

            case HotkeyPlay:
                if (_player.IsLoaded)
                {
                    _player.TogglePlay();
                    UpdateTransport();
                }
                handled = true;
                break;

            case HotkeyPrev:
                JumpCue(-1);
                handled = true;
                break;

            case HotkeyNext:
                JumpCue(1);
                handled = true;
                break;

            case HotkeyLoop:
                ToggleLoop();
                handled = true;
                break;

            case HotkeyCollect:
                ToggleFavoriteCurrent();
                handled = true;
                break;

            case HotkeyOffsetDown:
                NudgeOffset(-OffsetStepMs);
                handled = true;
                break;

            case HotkeyOffsetUp:
                NudgeOffset(OffsetStepMs);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 老板键：一次按下 = 字幕条消失 + 音频暂停；再按一次 = 字幕条回来 + 接着放。
    ///
    /// 续播是有条件的：只有在「藏起来的时候是我们在放」的情况下才自动继续。
    /// 本来就在暂停的时候按老板键，回来还是暂停 —— 否则按一下就自己出声了。
    /// </summary>
    private void ToggleBossKey()
    {
        _userHidden = !_userHidden;

        if (_userHidden)
        {
            _bossPaused = !_demoMode && _player.IsLoaded && _player.IsPlaying;

            if (_bossPaused)
                _player.Pause();
        }
        else
        {
            if (_bossPaused && _player.IsLoaded)
                _player.Play();

            _bossPaused = false;
        }

        // 还没就绪时按老板键只翻转状态，字幕条等恢复完再按最终状态显示
        _bar?.SetVisible(_sessionReady && !_userHidden);
        UpdateTransport();
    }

    // ------------------------------------------------------------------ 杂项

    /// <summary>
    /// 提示不再常驻（设计稿没有状态条）：写成一句「飘一下就散」的轻文字。
    /// quiet = true 的（比如每次吸附都刷的那条尺寸信息）只更新圆点颜色，不弹字。
    /// </summary>
    private void SetStatus(string text, bool ok, bool quiet = false)
    {
        // 吸附那条每 800ms 刷一次，小圆点的颜色没变就别再换画刷
        if (_statusOk != ok)
        {
            _statusOk = ok;

            if (StatusDot is not null)
            {
                StatusDot.Fill = new SolidColorBrush(
                    ok ? Color.FromRgb(0x3D, 0xA5, 0x6B) : Color.FromRgb(0xD9, 0x9A, 0x2B));
            }
        }

        if (quiet || StatusText is null)
            return;

        StatusText.Text = text;

        // 先把动画清掉再设不透明度：上一句可能正淡出到一半
        ToastBd.BeginAnimation(OpacityProperty, null);
        ToastBd.Opacity = 0.92;

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>提示淡出：0.45s 里把不透明度拉到 0。</summary>
    private void HideToast()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(450))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };

        fade.Completed += (_, _) => ToastBd.Opacity = 0;
        ToastBd.BeginAnimation(OpacityProperty, fade);
    }

    private static bool RectEquals(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
