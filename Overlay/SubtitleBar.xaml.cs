using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LoafEar.Config;
using LoafEar.Interop;
using LoafEar.Subs;
using LoafEar.UI;

namespace LoafEar.Overlay;

/// <summary>
/// 任务栏字幕条。以 WS_CHILD 方式成为任务栏的子窗口，随任务栏移动 / 隐藏 / 自动收起。
/// </summary>
public partial class SubtitleBar : Window
{
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;

    /// <summary>
    /// 「暂停 / 继续」的去重时间窗。单击走原生消息，WPF 那条路也留着当保险，
    /// 两条路撞在同一次点击上时靠这个窗口压成一次。
    /// </summary>
    private DateTime _lastToggle = DateTime.MinValue;

    /// <summary>
    /// 自己算双击：不依赖 WM_LBUTTONDBLCLK，那个消息要求窗口类带 CS_DBLCLKS，
    /// WPF 的窗口类不一定有，所以按「两次按下间隔 + 位置」自己判定。
    /// 单击就能切之后，这个判定只剩一个用处：双击的第二下别再切一次。
    /// </summary>
    private long _lastDownTick;
    private int _lastDownX;
    private int _lastDownY;

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private readonly List<KaraokeRun> _runs = new();

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _taskbar = IntPtr.Zero;
    private HwndSource? _source;

    private Cue? _cue;
    private TimeSpan _cueStart;
    private double _textWidth;

    /// <summary>超长句的分页结果。null 表示这句放得下，不需要翻页。</summary>
    private List<TextPage>? _pages;

    private double _pagesViewport = -1;

    /// <summary>当前已经贴上去的是哪一页，避免每帧重复改宽度触发重排。</summary>
    private int _appliedPage = int.MinValue;
    private double _idleOpacity = 0.72;
    private double _hoverOpacity = 1.0;
    private bool _visible = true;
    private bool? _shown;
    private bool _dark = true;
    private bool _backgroundTransparent;

    /// <summary>
    /// 调字幕宽度时的临时状态：100% 不透明、底板强制打开、加一圈 1px 灰描边。
    /// 平常字幕条是半透明压在任务栏上的，边界跟任务栏糊在一起，
    /// 拉宽度滑条时根本看不出拉到了哪儿。
    /// </summary>
    private bool _measuring;

    /// <summary>翻页时往后让出的读的余量（毫秒），来自配置。</summary>
    private double _flipLagMs = 380;

    private sealed class KaraokeRun
    {
        public Run Run = null!;
        public Brush Lit = Brushes.White;
        public Brush Unlit = Brushes.Gray;
        public double AtMs = -1;
    }

    /// <summary>长句切成的一屏：从整句的哪个位置开始、有多宽、占哪一段时间。</summary>
    private sealed class TextPage
    {
        public double StartX;
        public double Width;
        public double StartMs;
        public double EndMs;

        /// <summary>这一页显示的文字（诊断用，也方便核对断在哪个词上）。</summary>
        public string Text = "";
    }

    public SubtitleBar()
    {
        InitializeComponent();
        Left = -12000;
        Top = -12000;

        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Start();

        SetLocked(false);
    }

    public bool IsAttached { get; private set; }

    // ------------------------------------------------------------------ 悬浮

    /// <summary>字幕条本体的高度（跟任务栏那条一样高）。</summary>
    private const double BarHeight = 40;

    /// <summary>左右内边距，跟 XAML 里 Panel 的 Padding 保持一致。</summary>
    private const double BarPaddingX = 10;

    /// <summary>悬浮时多出来的那一行：放锁按钮。</summary>
    private const double LockRowHeight = 22;

    private bool _floating;
    private bool _locked;
    private bool _dragging;
    private bool _movedWhileDragging;
    private bool _lockPressed;
    private int _dragCursorX;
    private int _dragCursorY;

    /// <summary>当前是不是「悬浮字幕」模式（脱离任务栏、可以拖）。</summary>
    public bool IsFloating => _floating;

    /// <summary>悬浮时是否已上锁：上锁后点字幕是暂停 / 继续，拖不动。</summary>
    public bool IsLocked => _locked;

    /// <summary>悬浮模式下拖动结束，参数是新的 Left / Top（DIP）。</summary>
    public event EventHandler<(double Left, double Top)>? PositionChanged;

    /// <summary>锁按钮被点，参数是新的锁定状态。</summary>
    public event EventHandler<bool>? LockChanged;

    /// <summary>
    /// 进 / 出悬浮模式。
    /// 进来时脱离任务栏、置顶、按配置里的位置摆好，并把锁按钮那一行露出来；
    /// 出去时收回锁按钮，位置交回 OnGuardTick 去重新吸附。
    /// </summary>
    public void SetFloating(bool on, AppConfig config)
    {
        if (_floating == on)
            return;

        _floating = on;
        _dragging = false;
        _movedWhileDragging = false;

        if (on)
        {
            Detach();
            RevertToPopup();

            Topmost = true;
            // 锁那一行先摆「隐形」：行占着位置（否则字幕条会被拉高），
            // 图标等鼠标悬停到字幕上再露出来（见 OnHoverTick）
            LockHost.Visibility = Visibility.Hidden;
            Width = Math.Clamp(config.BarWidthDip, 220, 1200);
            Height = BarHeight + LockRowHeight;
            SetLocked(config.FloatLocked);
            ApplyFloatPosition(config);
            BringToFront();
        }
        else
        {
            LockHost.Visibility = Visibility.Collapsed;
            Topmost = false;
        }
    }

    /// <summary>悬浮模式下改「字幕宽度」滑条：只动窗口宽度，位置不变。</summary>
    public void SetFloatWidth(double widthDip)
    {
        if (!_floating)
            return;

        Width = Math.Clamp(widthDip, 220, 1200);
    }

    /// <summary>
    /// 把悬浮窗重新顶到最上层。
    /// 任务栏自己也是 Topmost 窗口，字幕条拖到任务栏上时得确认自己压在它上面。
    /// </summary>
    private void BringToFront()
    {
        if (_hwnd == IntPtr.Zero || !_floating)
            return;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>把窗口摆到配置里记住的位置；没记过就摆在屏幕下方居中。</summary>
    private void ApplyFloatPosition(AppConfig config)
    {
        var screen = MonitorBounds();
        var work = SystemParameters.WorkArea;
        var w = Width;
        var h = Height;

        var left = config.FloatLeftDip >= 0
            ? config.FloatLeftDip
            : work.Left + (work.Width - w) / 2;

        var top = config.FloatTopDip >= 0
            ? config.FloatTopDip
            : work.Bottom - h - 90;

        Left = ClampLeft(left, w, screen);
        Top = ClampTop(top, h, screen);
    }

    /// <summary>
    /// 窗口所在显示器的整块屏幕（DIP，**含任务栏**）。
    ///
    /// 拖动 / 摆位都用它而不是 SystemParameters.WorkArea：
    /// WorkArea 把任务栏那块挖掉了，字幕条永远拖不到屏幕最下面，
    /// 悬浮字幕本来就该能压到任务栏上。
    /// </summary>
    private Rect MonitorBounds()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero)
            {
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                };

                if (NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    var m = info.rcMonitor;

                    var sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
                    var sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

                    return new Rect(
                        m.Left / sx,
                        m.Top / sy,
                        (m.Right - m.Left) / sx,
                        (m.Bottom - m.Top) / sy);
                }
            }
        }

        return SystemParameters.WorkArea;
    }

    private static double ClampLeft(double left, double width, Rect area) =>
        Math.Clamp(left, area.Left - width + 80, area.Right - 80);

    private static double ClampTop(double top, double height, Rect area) =>
        Math.Clamp(top, area.Top - height + 30, area.Bottom - 20);

    public void SetLocked(bool on)
    {
        _locked = on;

        // 不带悬停提示：这个图标就两个状态，看一眼形状就知道落在哪一边
        LockButton.Content = Icons.MakeLock(15, Brushes.White, !on);
    }

    private void OnLockClick(object sender, RoutedEventArgs e) => ToggleLock();

    /// <summary>
    /// 开锁 / 上锁。
    ///
    /// 注意：真正生效的入口是窗口过程里的 WM_LBUTTONUP（见 WndProc）——
    /// 这条字幕条所有的左键消息都在原生那层吃掉了，WPF 的 Button 收不到完整的按下 + 抬起，
    /// 光靠 Click 事件会出现「图标点了没反应」。这里留给诊断和兜底用。
    /// </summary>
    private void ToggleLock()
    {
        SetLocked(!_locked);
        LockChanged?.Invoke(this, _locked);
    }

    /// <summary>按下 / 抬起的位置是不是落在锁按钮上（客户区物理像素，带 3px 宽容）。</summary>
    private bool HitLock(int cx, int cy)
    {
        if (!_floating || LockHost.Visibility != Visibility.Visible || LockHost.ActualWidth <= 0)
            return false;

        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = LockHost.TranslatePoint(new Point(0, 0), this);

        var left = origin.X * dpi.DpiScaleX - 3;
        var top = origin.Y * dpi.DpiScaleY - 3;
        var right = left + LockHost.ActualWidth * dpi.DpiScaleX + 6;
        var bottom = top + LockHost.ActualHeight * dpi.DpiScaleY + 6;

        return cx >= left && cx <= right && cy >= top && cy <= bottom;
    }

    /// <summary>诊断用：锁按钮那一行此刻占不占位置（悬浮时才有，露不露另算）。</summary>
    internal bool IsLockRowVisible => LockHost.Visibility != Visibility.Collapsed;

    /// <summary>诊断用：锁图标此刻露没露出来（只有鼠标压在字幕上才露）。</summary>
    internal bool IsLockShown => LockHost.Visibility == Visibility.Visible;

    /// <summary>诊断用：把窗口挪到光标底下（让光标落在锁那一行），模拟一次悬停。</summary>
    internal void PlaceUnderCursorForTest()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return;

        var dpi = VisualTreeHelper.GetDpi(this);

        Left = pt.X / dpi.DpiScaleX - 20;
        Top = pt.Y / dpi.DpiScaleY - LockRowHeight / 2;
    }

    /// <summary>诊断用：手动催一次悬停时钟（不用等 90ms 的定时器）。</summary>
    internal void HoverTickForTest() => OnHoverTick(this, EventArgs.Empty);

    /// <summary>诊断用：把窗口挪到离光标最远的那个角，模拟鼠标移走。</summary>
    internal void MoveAwayFromCursorForTest()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = MonitorBounds();

        var cx = pt.X / dpi.DpiScaleX;
        var cy = pt.Y / dpi.DpiScaleY;

        Left = cx > screen.Left + screen.Width / 2 ? screen.Left : screen.Right - Width;
        Top = cy > screen.Top + screen.Height / 2 ? screen.Top : screen.Bottom - Height;
    }

    /// <summary>诊断用：等价于点一下锁按钮（事件照发）。</summary>
    internal void ToggleLockForTest()
    {
        SetLocked(!_locked);
        LockChanged?.Invoke(this, _locked);
    }

    /// <summary>诊断用：模拟一次「拖完松手」，新位置按真实路径记进配置。</summary>
    internal void MoveToForTest(double left, double top)
    {
        Left = left;
        Top = top;
        PositionChanged?.Invoke(this, (Left, Top));
    }

    /// <summary>诊断用：按像素增量挪一下，走的是拖动时那条 DragBy。</summary>
    internal void DragByForTest(int dx, int dy) => DragBy(dx, dy);

    /// <summary>诊断用：窗口此刻是否真的在屏幕上。</summary>
    internal bool IsOnScreen =>
        _hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_hwnd);

    /// <summary>诊断用：字幕条自己的窗口句柄。</summary>
    internal IntPtr WindowHandle => _hwnd;

    public IntPtr TaskbarHandle => _taskbar;

    /// <summary>诊断用：文本自然宽度、可视宽度、当前位移（负值=已向左移）。</summary>
    internal (double TextWidth, double Viewport, double Shift) ScrollState =>
        (_textWidth, MainHost.ActualWidth, MainShift.X);

    /// <summary>诊断用：用 FormattedText 单独量一段文字，和 TextBlock 的实测宽度对照。</summary>
    internal double MeasureProbe(string text)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(MainText.FontFamily, MainText.FontStyle, MainText.FontWeight, MainText.FontStretch),
            MainText.FontSize,
            Brushes.Black,
            dpi).WidthIncludingTrailingWhitespace;
    }

    /// <summary>诊断用：TextBlock 的实际状态。</summary>
    internal (string Main, string Sub, double MainWidth, double Viewport, int Pages) FindText() =>
        (MainText.Text, SubText.Text, _textWidth, MainHost.ActualWidth, _pages?.Count ?? 0);

    internal string MainTextDump()
    {
        var len = MainText.Text.Length;

        return $"chars={len} width={MainText.Width:0} actual={MainText.ActualWidth:0} " +
               $"desired={MainText.DesiredSize.Width:0} inlines={MainText.Inlines.Count} " +
               $"shift={MainShift.X:0.0} trim={MainText.TextTrimming} " +
               $"tail=\"{MainText.Text[(len > 20 ? len - 20 : 0)..]}\"";
    }

    /// <summary>诊断用：双击探针。</summary>
    private int _toggleTestCount;

    internal void BeginToggleProbe()
    {
        _toggleTestCount = 0;
        ToggleRequested -= OnToggleProbe;
        ToggleRequested += OnToggleProbe;
    }

    private void OnToggleProbe(object? sender, EventArgs e) => _toggleTestCount++;

    internal int ToggleProbeCount => _toggleTestCount;

    /// <summary>诊断用：按客户区坐标往窗口投一次左键按下 + 抬起。</summary>
    internal void PostClick(int cx, int cy)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _postResult = "hwnd=0";
            return;
        }

        var lp = new IntPtr((cy << 16) | (cx & 0xFFFF));

        var a = NativeMethods.PostMessage(_hwnd, WM_LBUTTONDOWN, new IntPtr(1), lp);
        var b = NativeMethods.PostMessage(_hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);

        _postResult = $"down={a} up={b} hwnd={_hwnd} msgsDown={_msgDown} msgsUp={_msgUp}";
    }

    private string _postResult = "(未调用)";

    internal string PostResult => _postResult;

    internal string MsgStats => $"down={_msgDown} up={_msgUp}";

    private string _dbg = "(无)";
    private long _prevTick;
    private int _prevX;
    private int _prevY;

    internal string ToggleDebug => _dbg;

    private int _msgDown;
    private int _msgUp;

    /// <summary>诊断用：字幕条正中间，换回客户区物理像素。</summary>
    internal (int X, int Y) ClientPoint()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var target = (FrameworkElement)Content;

        var local = target.TranslatePoint(
            new Point(target.ActualWidth / 2, target.ActualHeight / 2), this);

        return ((int)(local.X * dpi.DpiScaleX), (int)(local.Y * dpi.DpiScaleY));
    }

    /// <summary>诊断用：锁按钮中心，客户区物理像素。</summary>
    internal (int X, int Y) LockCenterClient()
    {
        var dpi = VisualTreeHelper.GetDpi(this);

        var local = LockHost.TranslatePoint(
            new Point(LockHost.ActualWidth / 2, LockHost.ActualHeight / 2), this);

        return ((int)(local.X * dpi.DpiScaleX), (int)(local.Y * dpi.DpiScaleY));
    }

    /// <summary>诊断用：长句分页后的每屏起点、宽度与时间区间。</summary>
    internal IReadOnlyList<(double StartX, double Width, double StartMs, double EndMs, string Text)> Pages
    {
        get
        {
            if (_pages is null)
                return Array.Empty<(double, double, double, double, string)>();

            var list = new List<(double, double, double, double, string)>(_pages.Count);

            foreach (var page in _pages)
                list.Add((page.StartX, page.Width, page.StartMs, page.EndMs, page.Text));

            return list;
        }
    }

    /// <summary>在字幕条上按下右键，参数是屏幕坐标。</summary>
    public event EventHandler<Point>? RightClicked;

    /// <summary>左键单击字幕条：暂停 / 继续。</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>
    /// 双击的第二下不要再算一次单击 —— 第一下抬起时已经切过一次了。
    /// </summary>
    private bool _skipNextUp;

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 单击 / 双击的「播放或暂停」统一在原生消息里做（见 WndProc）：
        // 字幕条是 explorer 的子窗口，WPF 的鼠标路由不一定到得了，原生这条更可靠。
        // 这里只把事件吃掉，免得同一次点击走两条路各切一次。
        //
        // 悬浮拖动同样放在原生消息里做（WM_LBUTTONDOWN + SetCapture）——
        // 窗口是 WS_EX_NOACTIVATE，WPF 那层不一定收得到拖动需要的消息。
        e.Handled = true;
    }

    /// <summary>
    /// 悬浮拖动：按光标的增量挪窗口。
    /// 光标是物理像素、Left/Top 是 DIP，所以要除掉当前显示器的缩放比。
    /// </summary>
    private void DragBy(int dx, int dy)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = MonitorBounds();

        Left = ClampLeft(Left + dx / dpi.DpiScaleX, Width, screen);
        Top = ClampTop(Top + dy / dpi.DpiScaleY, Height, screen);

        _movedWhileDragging = true;
    }

    /// <summary>去重后触发一次「暂停 / 继续」。</summary>
    private void RaiseToggle()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastToggle).TotalMilliseconds < 250)
        {
            Trace("toggle suppressed (250ms)");
            return;
        }

        _lastToggle = now;

        // 不要在窗口消息处理里直接动主界面的控件：字幕条是 explorer 的子窗口，
        // 这里同步触发布局会走跨进程 SendMessage，explorer 正忙时可能卡住整个任务栏。
        // 投到消息队列里，让当前消息先处理完。
        // 用 Send 优先级：它排在鼠标移动等输入消息之前，点下去立刻响应，
        // 不会被一串 WM_MOUSEMOVE 顶到后面去。
        Dispatcher.BeginInvoke(
            new Action(() => ToggleRequested?.Invoke(this, EventArgs.Empty)),
            DispatcherPriority.Send);
    }

    /// <summary>把消息里的 lParam 拆成客户区坐标（每轴 16 位，带符号）。</summary>
    private static (int X, int Y) Unpack(IntPtr lParam)
    {
        var v = lParam.ToInt64();

        return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    /// <summary>
    /// 交互排障开关：设了 LOAFEAR_TRACE=1 就把每次左键事件记到 bar-trace.log，
    /// 平时完全不动磁盘。
    /// </summary>
    private static readonly bool _traceOn =
        Environment.GetEnvironmentVariable("LOAFEAR_TRACE") == "1";

    private static void Trace(string message)
    {
        if (!_traceOn)
            return;

        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "bar-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 记日志失败不影响交互
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 左键：单击 = 暂停 / 继续（2026-11-19 起；以前要双击）。
        // 字幕条是 explorer 的子窗口，WPF 的鼠标路由不保证一定到位，所以这层交互在原生消息里保一份。
        if (msg == WM_LBUTTONDOWN)
        {
            _msgDown++;

            // 锁按钮优先：它自己有点击语义，按下去既不能开始拖动，也不能当暂停
            var (lx, ly) = Unpack(lParam);
            if (HitLock(lx, ly))
            {
                _lockPressed = true;
                handled = true;
                return IntPtr.Zero;
            }

            // 悬浮 + 没上锁：按下就开始拖，别去算单击 / 双击
            if (_floating && !_locked)
            {
                if (NativeMethods.GetCursorPos(out var start))
                {
                    _dragging = true;
                    _movedWhileDragging = false;
                    _dragCursorX = start.X;
                    _dragCursorY = start.Y;

                    // 捕获鼠标：拖出窗口范围也还能收到 WM_MOUSEMOVE
                    NativeMethods.SetCapture(_hwnd);
                }

                return IntPtr.Zero;
            }

            var (cx, cy) = Unpack(lParam);

            var tick = Environment.TickCount64;
            var isDouble = _lastDownTick != 0
                           && tick - _lastDownTick <= 400
                           && Math.Abs(cx - _lastDownX) <= 6
                           && Math.Abs(cy - _lastDownY) <= 6;

            // 判定成双击后立刻清零，三连击不会再多触发一次
            _lastDownTick = isDouble ? 0 : tick;
            _lastDownX = cx;
            _lastDownY = cy;

            _dbg = $"tick={tick} gap={tick - _prevTick} dbl={isDouble} " +
                   $"c=({cx},{cy}) prev=({_prevX},{_prevY})";
            _prevTick = tick;
            _prevX = cx;
            _prevY = cy;

            Trace($"down c=({cx},{cy}) dbl={isDouble}");

            // 双击的第二下：第一下抬起时已经切过一次，这次抬起不再切
            _skipNextUp = isDouble;

            return IntPtr.Zero;
        }

        if (msg == WM_MOUSEMOVE)
        {
            if (!_dragging)
                return IntPtr.Zero;

            if (NativeMethods.GetCursorPos(out var now))
            {
                var dx = now.X - _dragCursorX;
                var dy = now.Y - _dragCursorY;

                if (dx != 0 || dy != 0)
                {
                    _dragCursorX = now.X;
                    _dragCursorY = now.Y;
                    DragBy(dx, dy);
                }
            }

            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_LBUTTONUP)
        {
            _msgUp++;

            // 锁按钮的收尾：抬手还在锁上才算一次点击（拖开就取消）
            if (_lockPressed)
            {
                _lockPressed = false;
                handled = true;

                var (lx, ly) = Unpack(lParam);
                if (HitLock(lx, ly))
                {
                    Trace("=> toggle lock");
                    ToggleLock();
                }

                return IntPtr.Zero;
            }

            // 拖动的收尾：松手才记位置，中途不写盘
            if (_dragging)
            {
                _dragging = false;
                NativeMethods.ReleaseCapture();
                BringToFront();

                if (_movedWhileDragging)
                {
                    _movedWhileDragging = false;
                    PositionChanged?.Invoke(this, (Left, Top));
                }

                handled = true;
                return IntPtr.Zero;
            }

            if (_skipNextUp)
            {
                _skipNextUp = false;
                Trace("up skipped (second half of a double)");
                return IntPtr.Zero;
            }

            // 悬浮且没上锁：这一下是拖动的收尾，不能当成暂停
            if (_floating && !_locked)
            {
                Trace("up ignored (floating & unlocked: drag)");
                handled = true;
                return IntPtr.Zero;
            }

            // 在抬起时切，按下的瞬间不会误触发
            Trace("=> toggle (single click)");
            RaiseToggle();
            handled = true;

            return IntPtr.Zero;
        }

        if (msg == WM_LBUTTONDBLCLK)
        {
            // 双击的两次单击已经各走了一遍上面那条路，这里不再补一下，
            // 否则一次双击会切两次（切回去，等于没反应）。
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_RBUTTONUP)
            return IntPtr.Zero;

        // 坐标直接取光标位置，比从 lParam 解包更可靠（子窗口坐标系不统一）
        NativeMethods.GetCursorPos(out var pt);
        var screen = new Point(pt.X, pt.Y);

        // 右键菜单绝对不能在这里同步搭起来。
        // 字幕条是 explorer 的子窗口，这条消息是 explorer 同步发过来的；
        // 在过程里创建 Popup 窗口 / 设置跨进程 owner，explorer 会一直等我们返回，
        // 表现就是整个任务栏（甚至资源管理器）卡住不动。投到队列里，让消息先回去。
        // Send 优先级：排在输入消息之前，点下去不会被鼠标移动顶到后面。
        Dispatcher.BeginInvoke(
            new Action(() => RightClicked?.Invoke(this, screen)),
            DispatcherPriority.Send);

        handled = true;
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ 透明度

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero || !_visible)
            return;

        // 调整宽度期间不看悬停：不管鼠标在不在上面都按 100% 画
        var target = _measuring ? 1.0 : _idleOpacity;
        var hovering = false;

        if (!_measuring
            && NativeMethods.GetCursorPos(out var pt)
            && NativeMethods.GetWindowRect(_hwnd, out var rect)
            && pt.X >= rect.Left && pt.X < rect.Right
            && pt.Y >= rect.Top && pt.Y < rect.Bottom)
        {
            target = _hoverOpacity;
            hovering = true;
        }

        // 悬浮时锁图标只在鼠标压到字幕上时才露出来（用 Hidden 而不是 Collapsed：
        // Collapsed 那一行会塌掉，字幕条被拉高，形状就变了）
        if (_floating)
            LockHost.Visibility = hovering ? Visibility.Visible : Visibility.Hidden;

        var current = Opacity;
        var delta = target - current;

        if (Math.Abs(delta) < 0.012)
            Opacity = target;
        else
            Opacity = current + delta * 0.35;
    }

    private void ApplyOpacity()
    {
        Opacity = _measuring ? 1.0 : _idleOpacity;
    }

    /// <summary>
    /// 进 / 出「调宽度」的临时状态。进出都立刻重画底板和透明度，
    /// 不等 90ms 的悬停轮询，否则松开滑条后还会半透明地闪一下。
    /// </summary>
    public void SetMeasureMode(bool on)
    {
        if (_measuring == on)
            return;

        _measuring = on;
        ApplyPanelLook();
        ApplyOpacity();
    }

    /// <summary>诊断用：当前是不是在「调宽度」的临时状态。</summary>
    public bool IsMeasuring => _measuring;

    /// <summary>诊断用：底板与描边此刻的取值。</summary>
    public string LookForTest()
    {
        var background = Panel.Background as SolidColorBrush;
        var border = Panel.BorderBrush as SolidColorBrush;

        return $"bg={(background is null ? "null" : background.Color.ToString())} " +
               $"border={Panel.BorderThickness.Left:0.#}/" +
               $"{(border is null ? "null" : border.Color.ToString())}";
    }

    /// <summary>按主题 / 透明底 / 调宽状态画底板。</summary>
    private void ApplyPanelLook()
    {
        // 描边会吃掉 1px 的排版宽度，把左右内边距同步减 1 抵消掉：
        // 调宽度时字不会跟着左右跳，看起来就是外面套了一圈线
        var inset = _measuring ? 1.0 : 0.0;
        Panel.Padding = new Thickness(
            Math.Max(0, BarPaddingX - inset), 0, Math.Max(0, BarPaddingX - inset), 0);

        if (_measuring)
        {
            // 调整时底板必须是实的：透明底那种「alpha=1 的黑」看不出边在哪儿；
            // 描边用中灰而不是纯黑 —— 纯黑压在深色任务栏上太重，白底上又抢字
            Panel.Background = new SolidColorBrush(
                _dark ? Color.FromRgb(0x23, 0x23, 0x23) : Colors.White);
            Panel.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));
            Panel.BorderThickness = new Thickness(1);
            return;
        }

        if (_backgroundTransparent)
        {
            // 想彻底不画底板，但 alpha 真的为 0 时，分层窗口这块像素会变成鼠标穿透，
            // 右键就点不到字幕条了（这是 Windows 分层窗口的 hit-test 规则）。
            // alpha=1 是 0.4% 的黑，任何背景色下都看不出，视觉上等同全透明。
            Panel.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        else
        {
            // 白天：白条压在浅色任务栏上；夜里：近黑条，跟深色任务栏连成一片
            Panel.Background = new SolidColorBrush(
                _dark ? Color.FromArgb(0xD8, 0x23, 0x23, 0x23)
                      : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
        }

        Panel.BorderThickness = new Thickness(0);
    }

    // ------------------------------------------------------------------ 附着

    /// <summary>挂进任务栏。失败返回 false，调用方应降级为悬浮窗。</summary>
    public bool Attach(IntPtr taskbarHwnd)
    {
        if (_hwnd == IntPtr.Zero || taskbarHwnd == IntPtr.Zero)
            return false;

        if (!ApplyChildStyle())
            return false;

        NativeMethods.SetParent(_hwnd, taskbarHwnd);

        // SetParent 跨进程要求完整性级别一致；以管理员运行时这里会静默失败
        if (NativeMethods.GetParent(_hwnd) != taskbarHwnd)
            return false;

        _taskbar = taskbarHwnd;
        IsAttached = true;
        return true;
    }

    private bool ApplyChildStyle()
    {
        try
        {
            var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE);
            style &= ~(NativeMethods.WS_POPUP
                       | NativeMethods.WS_CAPTION
                       | NativeMethods.WS_THICKFRAME
                       | NativeMethods.WS_SYSMENU
                       | NativeMethods.WS_MINIMIZEBOX
                       | NativeMethods.WS_MAXIMIZEBOX);
            style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, style);

            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex &= ~NativeMethods.WS_EX_APPWINDOW;
            ex |= NativeMethods.WS_EX_NOACTIVATE
                  | NativeMethods.WS_EX_TOOLWINDOW
                  | NativeMethods.WS_EX_NOPARENTNOTIFY;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, ex);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        if (enabled)
            ex |= NativeMethods.WS_EX_TRANSPARENT;
        else
            ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    public void SetCaptureHide(bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        try
        {
            NativeMethods.SetWindowDisplayAffinity(
                _hwnd,
                enabled ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        }
        catch
        {
            // 旧驱动可能不支持，忽略
        }
    }

    public void Reposition(TaskbarInfo taskbar, AppConfig config)
    {
        if (!IsAttached || _hwnd == IntPtr.Zero)
            return;

        var rect = TaskbarService.ComputeStrip(
            taskbar, config.BarWidthDip, config.BarInsetDip, 160, config.DockSide);

        // 别在这里硬加 SWP_SHOWWINDOW：启动阶段字幕条是故意藏着的，
        // 强行显示会让 SetVisible 的状态缓存和窗口真实可见性对不上，
        // 之后叫它隐藏时会被去重逻辑直接跳过，结果一直挂在任务栏上。
        var flags = NativeMethods.SWP_NOACTIVATE;

        if (_visible)
            flags |= NativeMethods.SWP_SHOWWINDOW;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOP,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            flags);
    }

    public void SetVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _visible = visible;

        // 守护定时器每 800ms 就调一次，状态没变就别动，
        // 否则会把鼠标悬停时的透明度动画反复打回静止值，看起来在闪。
        if (_shown == visible)
            return;

        _shown = visible;

        if (visible)
            ApplyOpacity();

        NativeMethods.ShowWindow(_hwnd, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
    }

    // ------------------------------------------------------------------ 渲染

    /// <summary>更新字幕内容。cue 为 null 时显示占位文本。</summary>
    public void SetContent(Cue? cue, string placeholder, string sub, string right, TimeSpan position, bool showTranslation)
    {
        if (!string.Equals(TimeText.Text, right, StringComparison.Ordinal))
            TimeText.Text = right;

        var subText = showTranslation ? sub : string.Empty;
        if (!string.Equals(SubText.Text, subText, StringComparison.Ordinal))
            SubText.Text = subText;

        var text = cue?.Text ?? placeholder;

        if (!ReferenceEquals(cue, _cue))
        {
            _cue = cue;
            _cueStart = cue?.Start ?? TimeSpan.Zero;
            BuildText(cue, text, subText);
        }
        else if (cue is null && !string.Equals(MainText.Text, placeholder, StringComparison.Ordinal))
        {
            MainText.Inlines.Clear();
            _runs.Clear();
            MainText.Text = placeholder;
            MeasureMain();
        }

        if (cue is not null)
        {
            UpdateTimed(cue, position);
        }
        else
        {
            ContentGrid.Opacity = 1;
        }
    }

    private void BuildText(Cue? cue, string text, string sub)
    {
        MainText.Inlines.Clear();
        _runs.Clear();

        if (cue?.Spans is null || cue.Spans.Count == 0)
        {
            MainText.Text = text;
            ApplyPlainStyle(cue);
            MeasureMain();
            return;
        }

        MainText.Text = string.Empty;

        var primary = cue.Color is { } pc ? ToBrush(pc) : null;
        var karaoke = cue.KaraokeColor is { } kc ? ToBrush(kc) : null;

        foreach (var span in cue.Spans)
        {
            if (string.IsNullOrEmpty(span.Text))
                continue;

            var litColor = span.Color is { } sc ? ToBrush(sc) : primary;
            litColor ??= _dark ? Brushes.White : Brushes.Black;

            var unlitColor = karaoke ?? Dim(litColor, _dark ? 0.45 : 0.55);

            var run = new Run(span.Text)
            {
                FontWeight = span.Bold || cue.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = span.Italic || cue.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = span.KaraokeAtMs >= 0 ? unlitColor : litColor,
            };

            MainText.Inlines.Add(run);

            if (cue.HasKaraoke)
            {
                _runs.Add(new KaraokeRun
                {
                    Run = run,
                    Lit = litColor,
                    Unlit = unlitColor,
                    AtMs = span.KaraokeAtMs,
                });
            }
        }

        MeasureMain();
    }

    /// <summary>
    /// 量一遍当前文本的自然宽度，并锁成显式宽度。
    /// 解锁这一步不能省：Width 一旦设过，后面再 Measure 得到的还是旧宽度。
    /// </summary>
    private void MeasureMain()
    {
        MainShift.X = 0;
        _pages = null;

        MainText.Width = double.NaN;
        MainText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = MainText.DesiredSize.Width;

        // 锁成自然宽度，TextBlock 才会按整句排版（不然会被裁切容器的宽度截断）
        if (_textWidth > 0)
            MainText.Width = _textWidth;

        // Canvas 自己不占高度，得手动跟文本等高；多留 2px 免得把字母上下沿削掉
        var height = Math.Ceiling(MainText.DesiredSize.Height);
        if (height > 0)
            MainClip.Height = height + 2;

        // 文本变了，裁切窗口要按新文本重设
        _appliedPage = int.MinValue;
        ApplyPage(-1);
    }

    /// <summary>
    /// 超长句改成「分页」，不再滚动。
    /// 按词边界把整句切成若干屏，每屏只在自己那段时间里显示，屏内文字是死的；
    /// 滚动要眼睛一直追着字跑，分页是停下来读完再翻，精听时舒服得多。
    /// </summary>
    private void UpdatePages(Cue cue, double elapsedMs)
    {
        // 用外层 StackPanel 的宽度当可视宽度：MainClip 自己会按页收缩，拿它当基准会越缩越小
        var viewport = MainHost.ActualWidth;

        if (viewport <= 0)
            return;

        if (_textWidth <= viewport + 0.5)
        {
            if (_appliedPage != -1)
            {
                _pages = null;
                ApplyPage(-1);
            }

            return;
        }

        // 宽度变了要重新按新宽度分页；换句时 BuildText -> MeasureMain 已经清过缓存
        if (_pages is null || Math.Abs(_pagesViewport - viewport) > 0.5)
        {
            _pages = BuildPages(cue, viewport);
            _pagesViewport = viewport;
            _appliedPage = int.MinValue;
        }

        if (_pages is null || _pages.Count == 0)
        {
            ApplyPage(-1);
            return;
        }

        var index = _pages.Count - 1;

        for (var i = 0; i < _pages.Count; i++)
        {
            if (elapsedMs < _pages[i].EndMs)
            {
                index = i;
                break;
            }
        }

        ApplyPage(index);
    }

    /// <summary>
    /// 把某一页贴到条上：裁切窗口收缩到「这一页最后一个词为止」，位移把这页推到左边缘。
    /// 这样左右两边都不会出现半个单词，页内文字也完全不动。
    /// </summary>
    private void ApplyPage(int index)
    {
        if (index == _appliedPage)
            return;

        _appliedPage = index;

        if (index < 0 || _pages is null || index >= _pages.Count)
        {
            // 没分页：窗口跟着文本宽度走，尾部多留 2px 免得最后一个字的抗锯齿被削掉
            MainClip.Width = Math.Max(1, _textWidth + 2);
            MainShift.X = 0;
            return;
        }

        // 页宽已经把这个词后面的空格算进去了，多一点都会露出下一个词的头。
        // 极少数情况下单个词就比整条还宽，这时只能按条宽裁掉，避免盖到右边的内容。
        var page = _pages[index];
        MainClip.Width = Math.Max(1, Math.Min(page.Width, _pagesViewport));
        MainShift.X = -page.StartX;
    }

    /// <summary>
    /// 按词宽度把整句切成若干页，并按每页的字数把句长摊给它们（SRT 没有逐词时间，只能按字数近似）。
    /// 页边界永远落在词与词之间，所以不会出现半个单词。
    /// </summary>
    private List<TextPage>? BuildPages(Cue cue, double viewport)
    {
        var text = cue.Text;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var typeface = new Typeface(
            MainText.FontFamily, MainText.FontStyle, MainText.FontWeight, MainText.FontStretch);

        double Measure(string token) => new FormattedText(
            token,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            MainText.FontSize,
            Brushes.Black,
            dpi).WidthIncludingTrailingWhitespace;

        // 先按词量一遍，记下每个词的起点、宽度，以及「念完它大概要多久」
        var wordX = new List<double>();
        var wordWidth = new List<double>();
        var wordMs = new List<double>();
        var wordStart = new List<int>();

        double cursor = 0;
        var index = 0;

        while (index < text.Length)
        {
            var begin = index;

            while (index < text.Length && !char.IsWhiteSpace(text[index]))
                index++;

            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            var token = text[begin..index];
            var width = Measure(token);

            wordX.Add(cursor);
            wordWidth.Add(width);
            wordMs.Add(WordMs(token));
            wordStart.Add(begin);

            cursor += width;
        }

        if (wordX.Count == 0)
            return null;

        // 贪心装页：一直装到再放一个词就会超宽为止
        var ranges = new List<(int From, int To)>();
        var i = 0;

        while (i < wordX.Count)
        {
            var from = i;
            var pageX = wordX[i];
            var j = i;

            while (j < wordX.Count && wordX[j] + wordWidth[j] - pageX <= viewport)
                j++;

            // 单个词比整条还宽时只能自己占一页，否则会卡在同一页
            if (j == from)
                j = from + 1;

            ranges.Add((from, j));
            i = j;
        }

        // 末页只剩一两个词时会"闪一下几乎空白的一页"。
        // 处理办法是把上一页末尾的词让过来 —— 也就是把切分点往左挪，末页自己不额外补内容。
        // 让词必须同步收缩上一页，否则同一个词会在两页里各出现一次。
        while (ranges.Count > 1)
        {
            var last = ranges[^1];
            var lastWidth = wordX[last.To - 1] + wordWidth[last.To - 1] - wordX[last.From];
            var lastWords = last.To - last.From;

            // 词数够了、宽度也占到四成以上，就不必再动
            if (lastWords > 2 && lastWidth >= viewport * 0.45)
                break;

            var prev = ranges[^2];

            // 上一页让出这个词之后至少还得剩两个词，否则轮到它自己去闪空白
            if (prev.To - prev.From < 3)
                break;

            var grown = wordX[last.To - 1] + wordWidth[last.To - 1] - wordX[last.From - 1];
            if (grown > viewport)
                break;

            ranges[^2] = (prev.From, last.From - 1);
            ranges[^1] = (last.From - 1, last.To);
        }

        var totalMs = 0.0;
        foreach (var range in ranges)
        {
            for (var k = range.From; k < range.To; k++)
                totalMs += wordMs[k];
        }

        if (totalMs <= 0)
            return null;

        var duration = cue.Duration.TotalMilliseconds;

        // 每页该占多久：按「念完这一页所有词要多久」分，而不是按字数。
        // 字数相同的一页，可能一个全是 "extraordinarily" 这种长词，另一个全是 "a is it" 这种短词，
        // 按字数摊会让长词那页时间不够 —— 音频还没念到就翻页，就是从这里来的。
        var weights = new double[ranges.Count];
        for (var p = 0; p < ranges.Count; p++)
        {
            var sum = 0.0;
            for (var k = ranges[p].From; k < ranges[p].To; k++)
                sum += wordMs[k];
            weights[p] = sum;
        }

        var spans = AllocateSpans(weights, duration, _flipLagMs);

        var pages = new List<TextPage>(ranges.Count);
        var clock = 0.0;

        foreach (var range in ranges)
        {
            var startX = wordX[range.From];
            var endX = wordX[range.To - 1] + wordWidth[range.To - 1];

            var span = spans[pages.Count];

            var stop = range.To < wordStart.Count ? wordStart[range.To] : text.Length;

            pages.Add(new TextPage
            {
                StartX = startX,
                Width = endX - startX,
                StartMs = clock,
                EndMs = clock + span,
                Text = text[wordStart[range.From]..stop].Trim(),
            });

            clock += span;
        }

        // 末页正好收到句尾：浮点累加会留下几毫秒，句末那一下容易踩空
        if (pages.Count > 0)
            pages[^1].EndMs = Math.Max(pages[^1].StartMs + 1, duration);

        return pages;
    }

    /// <summary>
    /// 一个词念完大概要多久：词头开销 + 每音节，再加词尾标点后面的停顿。
    /// SRT 没有逐词时间，只能估。关键在标点停顿要记在「它前面那一页」头上 ——
    /// 一页以逗号结尾，后面那半秒本来就没人说话，这页自然该多停一会儿；
    /// 旧版按字数摊，这半秒被算给了下一页，于是这页还没念完就翻走了。
    /// </summary>
    private static double WordMs(string token)
    {
        var core = token.Trim();
        core = core.TrimEnd('"', '\'', '\u201D', '\u2019', ')', ']', '\u300D', '\u300F');

        if (core.Length == 0)
            return 0;

        var ms = 70 + 175 * Syllables(core);

        if (core.EndsWith("...", StringComparison.Ordinal) || core.EndsWith("\u2026", StringComparison.Ordinal))
            ms += 320;
        else if (core.EndsWith(".", StringComparison.Ordinal)
                 || core.EndsWith("!", StringComparison.Ordinal)
                 || core.EndsWith("?", StringComparison.Ordinal))
            ms += 260;
        else if (core.EndsWith(",", StringComparison.Ordinal)
                 || core.EndsWith(";", StringComparison.Ordinal)
                 || core.EndsWith(":", StringComparison.Ordinal))
            ms += 180;
        else if (core.EndsWith("-", StringComparison.Ordinal) || core.EndsWith("\u2014", StringComparison.Ordinal))
            ms += 140;

        return ms;
    }

    /// <summary>数音节：连续元音算一个，撇号连字符不打断。够用即可，不追求语言学正确。</summary>
    private static int Syllables(string word)
    {
        var count = 0;
        var prevVowel = false;

        foreach (var ch in word)
        {
            // 撇号/连字符不该把元音打断，否则 "we're" 会被数成两个音节
            if (ch is '\'' or '-' or '\u2019')
                continue;

            var vowel = "aeiouyAEIOUY".IndexOf(ch) >= 0;

            if (vowel && !prevVowel)
                count++;

            prevVowel = vowel;
        }

        if (count == 0)
            count = 1;

        // 词尾不发音的 e：make / time / love 都只有一个音节
        var lower = word.ToLowerInvariant();

        if (count > 1 && lower.Length > 2 && lower[^1] == 'e'
            && !lower.EndsWith("le", StringComparison.Ordinal)
            && !lower.EndsWith("ee", StringComparison.Ordinal))
            count--;

        return count;
    }

    /// <summary>一页在屏上至少停这么久，低于这个数看起来就是「闪了一下」。</summary>
    private const double MinPageMs = 700;

    /// <summary>
    /// 把一句的时长分给各页：先按「念完这一页要多久」的比例摊，再整体往后让出一点读的余量。
    ///
    /// 光按比例摊，翻页时刻正好落在「这一页最后一个词念完」的那一瞬 ——
    /// 也就是说音频刚一离开这页，字就没了，正在看的那半句直接被抹掉。
    /// 所以每翻一页都往后让 PageFlipLagMs（约一个词），让音频走到下一屏开头时，
    /// 上一屏的最后几个词还留在眼前。宁可晚一瞬，不要提前。
    /// </summary>
    private static double[] AllocateSpans(double[] weights, double duration, double lagMs)
    {
        var count = weights.Length;
        var spans = new double[count];

        if (count == 0)
            return spans;

        var total = 0.0;
        foreach (var weight in weights)
            total += weight;

        if (total <= 0 || duration <= 0)
        {
            for (var i = 0; i < count; i++)
                spans[i] = duration / count;

            return spans;
        }

        for (var i = 0; i < count; i++)
            spans[i] = duration * weights[i] / total;

        // 让出的时间全部由末页承担：前面的页各自多留一会儿，末页相应短一点。
        // 末页要是已经被压到保底线，就让不动了。
        if (count > 1 && lagMs > 0)
        {
            var lag = Math.Min(lagMs, Math.Max(0, spans[^1] - MinPageMs));

            spans[0] += lag;
            spans[^1] -= lag;
        }

        return EnforceMinimum(spans);
    }

    /// <summary>把短于 MinPageMs 的页补到保底线，缺口从余量最多的页里按比例扣。</summary>
    private static double[] EnforceMinimum(double[] spans)
    {
        var count = spans.Length;
        var total = 0.0;

        foreach (var span in spans)
            total += span;

        // 页数多到连保底线都分不出来时就不硬撑
        if (count == 0 || total < MinPageMs * count)
            return spans;

        var deficit = 0.0;

        for (var i = 0; i < count; i++)
        {
            if (spans[i] >= MinPageMs)
                continue;

            deficit += MinPageMs - spans[i];
            spans[i] = MinPageMs;
        }

        if (deficit <= 0)
            return spans;

        var surplus = 0.0;

        for (var i = 0; i < count; i++)
            if (spans[i] > MinPageMs)
                surplus += spans[i] - MinPageMs;

        if (surplus <= 0)
            return spans;

        var take = Math.Min(1.0, deficit / surplus);

        for (var i = 0; i < count; i++)
            if (spans[i] > MinPageMs)
                spans[i] -= (spans[i] - MinPageMs) * take;

        return spans;
    }

    private void ApplyPlainStyle(Cue? cue)
    {
        if (cue is null)
        {
            MainText.Foreground = _dark ? Brushes.White : Brushes.Black;
            MainText.FontWeight = FontWeights.Normal;
            MainText.FontStyle = FontStyles.Normal;
            return;
        }

        MainText.Foreground = cue.Color is { } c ? ToBrush(c) : _dark ? Brushes.White : Brushes.Black;
        MainText.FontWeight = cue.Bold ? FontWeights.SemiBold : FontWeights.Normal;
        MainText.FontStyle = cue.Italic ? FontStyles.Italic : FontStyles.Normal;
    }

    /// <summary>按播放位置刷新卡拉OK染色与 \fad 淡入淡出。</summary>
    private void UpdateTimed(Cue cue, TimeSpan position)
    {
        var elapsed = (position - _cueStart).TotalMilliseconds;

        if (_runs.Count > 0)
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var item = _runs[i];
                var want = item.AtMs < 0 || elapsed >= item.AtMs ? item.Lit : item.Unlit;
                if (!ReferenceEquals(item.Run.Foreground, want))
                    item.Run.Foreground = want;
            }
        }

        var opacity = 1.0;

        if (cue.FadeInMs > 0 && elapsed < cue.FadeInMs)
            opacity = Math.Max(0, elapsed / cue.FadeInMs);

        if (cue.FadeOutMs > 0)
        {
            var remaining = cue.Duration.TotalMilliseconds - elapsed;
            if (remaining < cue.FadeOutMs)
                opacity = Math.Min(opacity, Math.Max(0, remaining / cue.FadeOutMs));
        }

        if (Math.Abs(ContentGrid.Opacity - opacity) > 0.01)
            ContentGrid.Opacity = opacity;

        UpdatePages(cue, elapsed);
    }

    public void ApplyAppearance(AppConfig config)
    {
        _dark = config.Theme switch
        {
            BarTheme.Dark => true,
            BarTheme.Light => false,
            _ => ThemeHelper.IsSystemDark(),
        };

        _flipLagMs = Math.Clamp(config.PageFlipLagMs, 0, 1200);
        _idleOpacity = Math.Clamp(config.OpacityIdle, 0.05, 1);
        _hoverOpacity = Math.Clamp(config.OpacityHover, 0.05, 1);
        _backgroundTransparent = config.BackgroundTransparent;

        ApplyPanelLook();

        var main = _dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1A, 0x1A, 0x1A);
        var sub = _dark ? Color.FromRgb(0x9C, 0x9C, 0x9C) : Color.FromRgb(0x78, 0x78, 0x78);
        var time = _dark ? Color.FromRgb(0x8A, 0x8A, 0x8A) : Color.FromRgb(0xA6, 0xA6, 0xA6);

        MainText.Foreground = new SolidColorBrush(main);
        SubText.Foreground = new SolidColorBrush(sub);
        TimeText.Foreground = new SolidColorBrush(time);

        MainText.FontSize = config.MainFontSize;
        SubText.FontSize = config.SubFontSize;

        SubText.Visibility = config.ShowTranslation ? Visibility.Visible : Visibility.Collapsed;
        TimeText.Visibility = config.ShowTime ? Visibility.Visible : Visibility.Collapsed;

        // 样式变了，强制下一次重建富文本
        _cue = null;
        MainText.Inlines.Clear();
        _runs.Clear();
        MeasureMain();
        ApplyOpacity();
    }

    private static Brush ToBrush(uint rgb) =>
        new SolidColorBrush(Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF)));

    private static Brush Dim(Brush source, double factor)
    {
        if (source is not SolidColorBrush solid)
            return source;

        var c = solid.Color;
        return new SolidColorBrush(Color.FromArgb(
            c.A,
            (byte)(c.R * factor),
            (byte)(c.G * factor),
            (byte)(c.B * factor)));
    }

    // ------------------------------------------------------------------ 降级

    /// <summary>吸附失败时回滚为普通弹出窗口，用于悬浮兜底。</summary>
    public void RevertToPopup()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        try
        {
            if (NativeMethods.GetParent(_hwnd) != IntPtr.Zero)
                NativeMethods.SetParent(_hwnd, IntPtr.Zero);

            var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE);
            style &= ~NativeMethods.WS_CHILD;
            style |= NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, style);
        }
        catch
        {
            // 忽略
        }

        IsAttached = false;
        _taskbar = IntPtr.Zero;
    }

    public void Detach()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        try
        {
            if (NativeMethods.GetParent(_hwnd) != IntPtr.Zero)
                NativeMethods.SetParent(_hwnd, IntPtr.Zero);
        }
        catch
        {
            // 忽略
        }

        IsAttached = false;
        _taskbar = IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _hoverTimer.Stop();
        _source?.RemoveHook(WndProc);
        Detach();
        base.OnClosed(e);
    }
}
