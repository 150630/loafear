using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace LoafEar.Diag;

/// <summary>
/// 界面线程卡顿记录器。
///
/// 背景：这个程序所有交互都靠界面线程（字幕条、右键菜单、设置窗口）。
/// 只要界面线程被某一次调用按住，用户看到的就是「点了没反应，过一会儿自己好了」。
/// 事后猜是哪一次调用没有意义，所以这里常驻一个探针：
///
/// 1. 后台线程每 500ms 往界面线程投一个最高优先级的空任务，
///    量它多久被处理 —— 这就是界面线程的真实响应延迟；
/// 2. 超过 <see cref="WarnMs"/> 就记一条，带上最近走过的几步（面包屑）；
/// 3. 卡住超过 1.5s 再额外记一份现场（各线程等待原因、shell 是否响应）。
///
/// 正常情况下只在内存里转，不写盘；只有出事才追加到 hang.log。
/// </summary>
internal static class StallWatch
{
    /// <summary>超过这个延迟就记一条（毫秒）。正常应该在 5ms 以内。</summary>
    private const int WarnMs = 300;

    private const int RingSize = 32;

    private static readonly string[] Ring = new string[RingSize];
    private static readonly object RingLock = new();

    private static int _head;
    private static Dispatcher? _ui;
    private static Thread? _thread;

    /// <summary>最近一次「走过某一步」的时间，用来判断界面线程是不是还在动。</summary>
    private static long _lastCrumbTick;

    private static long _slowCount;
    private static long _hangCount;
    private static long _lastWriteTick;
    private static int _maxSeenMs;

    public static void Start(Dispatcher ui)
    {
        if (_thread is not null)
            return;

        _ui = ui;
        _lastCrumbTick = Environment.TickCount64;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LoafEar.StallWatch",
        };

        _thread.Start();
    }

    /// <summary>界面线程每走过一个关键步骤调一次。只写内存，开销是几个时钟周期。</summary>
    public static void Crumb(string label)
    {
        _lastCrumbTick = Environment.TickCount64;

        var index = Interlocked.Increment(ref _head) % RingSize;
        Ring[index] = $"{DateTime.Now:HH:mm:ss.fff} {label}";
    }

    private static void Loop()
    {
        // 启动阶段本来就有几百毫秒的初始化，先让程序站稳
        Thread.Sleep(700);

        while (true)
        {
            var probe = _ui!.InvokeAsync(static () => { }, DispatcherPriority.Send);
            var sw = Stopwatch.StartNew();
            var ok = probe.Task.Wait(700);
            var elapsed = (int)sw.ElapsedMilliseconds;

            if (ok)
            {
                if (elapsed > _maxSeenMs)
                    _maxSeenMs = elapsed;

                if (elapsed >= WarnMs)
                {
                    Interlocked.Increment(ref _slowCount);
                    Write($"界面线程延迟 {elapsed}ms", null);
                }

                Thread.Sleep(450);
                continue;
            }

            // 卡住了：一直等到恢复，把等待时长和现场都留下
            Interlocked.Increment(ref _hangCount);

            var stall = Stopwatch.StartNew();
            var wrote = false;

            while (!probe.Task.Wait(200))
            {
                if (!wrote && stall.ElapsedMilliseconds >= 1200)
                {
                    wrote = true;
                    Write($"界面线程卡住（已 {(int)stall.ElapsedMilliseconds}ms 未处理消息）", Snapshot());
                }
            }

            Write($"界面线程恢复，共卡了 {(int)stall.ElapsedMilliseconds}ms", Snapshot());
            Thread.Sleep(1500);
        }
    }

    /// <summary>写一条记录。两秒内不重复写，避免刷爆磁盘。</summary>
    private static void Write(string message, string? detail)
    {
        var now = Environment.TickCount64;

        if (now - _lastWriteTick < 1500)
            return;

        _lastWriteTick = now;

        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        sb.AppendLine($"  进程运行 {Environment.TickCount64 / 1000.0:0.0}s  卡顿次数 慢={_slowCount} 卡死={_hangCount} 历史最慢={_maxSeenMs}ms");
        sb.AppendLine("  最近走过的步骤（早 → 晚）：");

        string[] steps;
        lock (RingLock)
        {
            steps = new string[RingSize];

            for (var i = 0; i < RingSize; i++)
                steps[i] = Ring[(_head + 1 + i) % RingSize] ?? string.Empty;
        }

        foreach (var step in steps)
        {
            if (step.Length > 0)
                sb.AppendLine($"    {step}");
        }

        if (detail is not null)
            sb.AppendLine(detail);

        try
        {
            File.AppendAllText(
                Path.Combine(Config.AppConfig.ConfigDirectory, "hang.log"),
                sb.ToString() + Environment.NewLine);
        }
        catch
        {
            // 记不下来也不影响程序
        }
    }

    /// <summary>卡住时的现场：各线程在等什么、shell 还通不通。</summary>
    private static string Snapshot()
    {
        var sb = new StringBuilder();

        try
        {
            sb.AppendLine("  线程状态：");

            using var process = Process.GetCurrentProcess();

            foreach (ProcessThread thread in process.Threads)
            {
                string state;

                try
                {
                    state = $"{thread.ThreadState}/{thread.WaitReason}";
                }
                catch
                {
                    continue;
                }

                sb.AppendLine(
                    $"    tid={thread.Id,-6} {state,-28} cpu={thread.TotalProcessorTime.TotalMilliseconds,8:0}ms");
            }
        }
        catch
        {
            sb.AppendLine("    线程状态读取失败");
        }

        try
        {
            var shell = Interop.NativeMethods.FindWindow("Shell_TrayWnd", null);
            var sw = Stopwatch.StartNew();
            var ok = SendMessageTimeout(
                shell, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 1500, out _);

            sb.AppendLine(
                $"  shell 任务栏响应：ok={ok == IntPtr.Zero} 耗时={sw.ElapsedMilliseconds}ms");
        }
        catch
        {
            sb.AppendLine("  shell 探测失败");
        }

        return sb.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
