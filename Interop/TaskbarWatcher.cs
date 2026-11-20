using System.Diagnostics;

namespace LoafEar.Interop;

/// <summary>
/// 后台轮询任务栏状态。
///
/// 这里做的每一件事都是跨进程调 shell：FindWindowEx、GetWindowRect、GetDpiForWindow、
/// SHAppBarMessage(ABM_GETSTATE)、SHQueryUserNotificationState。
/// 它们平时是几十微秒，但 shell 一忙（启动初期、explorer 重启、弹 UAC、开全屏应用）
/// 就可能阻塞几百毫秒甚至几秒。
///
/// 以前这些调用全在界面线程上、每 800ms 跑一次，于是表现成
/// 「点字幕条没反应，过一会儿自己好了」—— 界面线程正等 shell 回话，什么消息都处理不了。
/// 现在只在这个后台线程上问，界面线程只读最近一次结果。
/// </summary>
internal static class TaskbarWatcher
{
    private static Thread? _thread;
    private static Snapshot _current = Snapshot.Empty;

    /// <summary>一轮查询的结果。Stamp=0 表示还没跑过第一轮。</summary>
    public sealed record Snapshot(
        TaskbarInfo? Taskbar,
        bool ScreenBusy,
        bool Collapsed,
        double LocateMs,
        double BusyMs,
        long Stamp)
    {
        public static readonly Snapshot Empty = new(null, false, false, 0, 0, 0);

        public bool Ready => Stamp != 0;
    }

    public static Snapshot Current => Volatile.Read(ref _current);

    public static void Start()
    {
        if (_thread is not null)
            return;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LoafEar.TaskbarWatcher",
        };

        _thread.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            var locate = Stopwatch.StartNew();
            TaskbarInfo? taskbar;

            try
            {
                taskbar = TaskbarService.Locate();
            }
            catch
            {
                taskbar = null;
            }

            locate.Stop();

            var busy = Stopwatch.StartNew();
            bool screenBusy;

            try
            {
                screenBusy = TaskbarService.IsScreenBusy();
            }
            catch
            {
                screenBusy = false;
            }

            busy.Stop();

            // 任务栏是否已经缩回屏幕外（自动隐藏）：字幕条是独立浮窗，不会跟着滑走，
            // 这种时候得自己躲起来。这个判断要读显示器信息，一并放后台做。
            bool collapsed;

            try
            {
                collapsed = taskbar is not null && TaskbarService.IsCollapsed(taskbar);
            }
            catch
            {
                collapsed = false;
            }

            Volatile.Write(
                ref _current,
                new Snapshot(
                    taskbar,
                    screenBusy,
                    collapsed,
                    locate.Elapsed.TotalMilliseconds,
                    busy.Elapsed.TotalMilliseconds,
                    Environment.TickCount64));

            Thread.Sleep(500);
        }
    }
}
