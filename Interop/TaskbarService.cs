namespace LoafEar.Interop;

public sealed class TaskbarInfo
{
    public IntPtr Handle { get; init; }
    public RECT Rect { get; init; }
    public double Scale { get; init; } = 1.0;
    public bool IsAutoHide { get; init; }

    /// <summary>任务栏横放（屏幕上下）时为 true；竖放在左右时为 false。</summary>
    public bool IsHorizontal => Rect.Width > Rect.Height;
}

public readonly record struct StripRect(int X, int Y, int Width, int Height);

/// <summary>
/// 任务栏定位与空位计算。
///
/// 位置策略：**右对齐到系统托盘左边界**。
/// 这样天然不会左右横跳（避开了 UIA 单次测量漏报导致的抖动），
/// 也不需要每帧重算任务按钮区。
/// </summary>
public static class TaskbarService
{
    private const string ClassPrimaryTaskbar = "Shell_TrayWnd";
    private const string ClassTrayNotify = "TrayNotifyWnd";

    /// <summary>
    /// 任务按钮区候选类名。
    /// Win11 25H2 实测结构：Shell_TrayWnd → ReBarWindow32 → MSTaskSwWClass → MSTaskListWClass，
    /// 且 MSTaskListWClass 是隐藏的、还是孙窗口，FindWindowEx 只找直接子窗口，所以必须从 ReBarWindow32 起找。
    /// </summary>
    private static readonly string[] ButtonAreaClasses =
    {
        "ReBarWindow32",
        "MSTaskSwWClass",
        "MSTaskListWClass",
        "Start",
    };

    /// <summary>找主屏任务栏。找不到返回 null（调用方应降级为悬浮窗）。</summary>
    public static TaskbarInfo? Locate()
    {
        var hwnd = NativeMethods.FindWindow(ClassPrimaryTaskbar, null);
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return null;

        if (rect.Width <= 0 || rect.Height <= 0)
            return null;

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        return new TaskbarInfo
        {
            Handle = hwnd,
            Rect = rect,
            Scale = scale,
            IsAutoHide = QueryAutoHide(),
        };
    }

    private static bool QueryAutoHide()
    {
        try
        {
            var abd = new NativeMethods.APPBARDATA
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            };
            var state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd).ToInt64();
            return (state & NativeMethods.ABS_AUTOHIDE) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算字幕条在任务栏客户区中的位置（坐标已转换为父窗口客户坐标）。
    /// </summary>
    /// <param name="tb">任务栏信息</param>
    /// <param name="widthDip">期望宽度（DIP）</param>
    /// <param name="insetDip">上下左右内缩（DIP）</param>
    /// <param name="minWidthDip">最小宽度（DIP）</param>
    /// <param name="side">贴在空位的左端（靠任务按钮）还是右端（靠托盘）</param>
    public static StripRect ComputeStrip(
        TaskbarInfo tb,
        double widthDip,
        double insetDip,
        double minWidthDip = 160,
        Config.DockSide side = Config.DockSide.Right)
    {
        var scale = tb.Scale <= 0 ? 1.0 : tb.Scale;
        var inset = Math.Max(0, (int)Math.Round(insetDip * scale));
        var desired = Math.Max(1, (int)Math.Round(widthDip * scale));
        var minWidth = Math.Max(1, (int)Math.Round(minWidthDip * scale));

        // 竖排任务栏（左右停靠）暂不支持横向字幕条，直接退化为整条覆盖。
        if (!tb.IsHorizontal)
        {
            return new StripRect(0, 0, tb.Rect.Width, tb.Rect.Height);
        }

        var leftLimit = tb.Rect.Left + inset;
        var rightLimit = tb.Rect.Right - inset;

        // 1) 系统托盘左边界 —— 最主要的硬约束
        var tray = NativeMethods.FindWindowEx(tb.Handle, IntPtr.Zero, ClassTrayNotify, null);
        if (tray != IntPtr.Zero &&
            NativeMethods.GetWindowRect(tray, out var trayRect) &&
            trayRect.Width > 0)
        {
            rightLimit = Math.Min(rightLimit, trayRect.Left - inset);
        }

        // 2) 任务按钮区右边界 —— 取所有候选里最靠右的那个
        var iconRight = leftLimit;
        foreach (var className in ButtonAreaClasses)
        {
            var handle = NativeMethods.FindWindowEx(tb.Handle, IntPtr.Zero, className, null);
            if (handle == IntPtr.Zero)
                continue;

            if (!NativeMethods.GetWindowRect(handle, out var areaRect))
                continue;

            if (areaRect.Width <= 0 || areaRect.Right >= rightLimit)
                continue;

            if (areaRect.Right > iconRight)
                iconRight = areaRect.Right;
        }

        if (iconRight > leftLimit)
            leftLimit = iconRight + inset;

        // 靠左贴的是任务栏**最左端**，不是任务按钮的右边：
        // Win11 把任务按钮居中摆放，任务按钮右边那块「空白」其实在屏幕中间，
        // 贴着它看起来就是浮在任务栏中间。左侧 0 → 第一个任务按钮之间本来就是空着的。
        var anchor = side == Config.DockSide.Left ? tb.Rect.Left + inset : leftLimit;

        var available = rightLimit - anchor;
        if (available <= 0)
        {
            // 空间被吃光（例如任务栏挤满），退化为「托盘左侧整块」
            available = Math.Max(minWidth, tb.Rect.Width / 3);
            rightLimit = tb.Rect.Right - inset;
            anchor = rightLimit - available;
        }

        var width = Math.Min(desired, available);
        if (width < minWidth)
            width = Math.Min(minWidth, available);
        if (width <= 0)
            width = Math.Max(60, available);

        var height = tb.Rect.Height - inset * 2;
        if (height <= 0)
            height = tb.Rect.Height;

        // 靠右：贴到 rightLimit（托盘左侧）；靠左：贴到任务栏最左端
        var screenX = side == Config.DockSide.Left ? anchor : rightLimit - width;
        var screenY = tb.Rect.Top + inset;

        // 屏幕坐标 → 任务栏客户坐标（任务栏无边框，原点是窗口左上角）
        return new StripRect(
            screenX - tb.Rect.Left,
            screenY - tb.Rect.Top,
            width,
            height);
    }

    /// <summary>
    /// 任务栏是不是「自动隐藏并且已经缩回去了」。
    /// 字幕条现在是独立浮窗，不会跟着任务栏一起滑走，所以这种时候要自己躲起来。
    /// </summary>
    public static bool IsCollapsed(TaskbarInfo tb)
    {
        if (!tb.IsAutoHide)
            return false;

        try
        {
            var monitor = NativeMethods.MonitorFromWindow(
                tb.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (monitor == IntPtr.Zero)
                return false;

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                return false;

            var screen = info.rcMonitor;
            var visibleWidth = Math.Max(
                0, Math.Min(tb.Rect.Right, screen.Right) - Math.Max(tb.Rect.Left, screen.Left));
            var visibleHeight = Math.Max(
                0, Math.Min(tb.Rect.Bottom, screen.Bottom) - Math.Max(tb.Rect.Top, screen.Top));

            // 缩回屏幕外以后只剩一两条像素
            return tb.IsHorizontal ? visibleHeight < 12 : visibleWidth < 12;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前是否处于投屏 / 全屏 / 演示状态。</summary>
    public static bool IsScreenBusy()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0)
                return false;

            // QUNS_BUSY=2, QUNS_RUNNING_D3D_FULL_SCREEN=3, QUNS_PRESENTATION_MODE=4
            return state is 2 or 3 or 4;
        }
        catch
        {
            return false;
        }
    }
}
