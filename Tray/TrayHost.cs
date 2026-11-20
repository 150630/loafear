using System.Threading;
using System.Windows.Forms;

namespace LoafEar.Tray;

/// <summary>
/// 托盘图标宿主线程。
///
/// 为什么不在界面线程上建托盘图标：WinForms 的 NotifyIcon 一建出来就要调
/// Shell_NotifyIcon，这是往 explorer 发的同步消息；shell 一忙（尤其刚开机、
/// explorer 刚重启）它就能阻塞几百毫秒到两秒。以前这段开销压在界面线程上，
/// 表现就是「刚打开时点什么都没反应，过一会儿又好了」。
///
/// 现在整套 WinForms 初始化 + NotifyIcon 都活在自己的 STA 线程和消息循环里，
/// 界面线程只负责把回调转成 Dispatcher 调用。
/// </summary>
internal static class TrayHost
{
    private static Thread? _thread;
    private static TrayIcon? _icon;
    private static SynchronizationContext? _context;
    private static readonly ManualResetEventSlim Ready = new(false);

    private static Action? _showWindow;
    private static Action? _toggleBar;
    private static Action? _togglePlay;
    private static Action? _exit;

    /// <summary>托盘图标是否已经建好。只是个观测点，不影响主流程。</summary>
    public static bool IsReady => Ready.IsSet;

    public static void Start(Action showWindow, Action toggleBar, Action togglePlay, Action exit)
    {
        _showWindow = showWindow;
        _toggleBar = toggleBar;
        _togglePlay = togglePlay;
        _exit = exit;

        if (_thread is not null)
            return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LoafEar.Tray",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private static void Run()
    {
        try
        {
            // 先把同步上下文拿到手：界面线程之后要靠它把「改提示文字」这类操作
            // 送回这个线程，才不会又从界面线程调 shell。
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            _context = context;

            _icon = new TrayIcon();

            _icon.ShowWindow += () => _showWindow?.Invoke();
            _icon.ToggleBar += () => _toggleBar?.Invoke();
            _icon.TogglePlay += () => _togglePlay?.Invoke();
            _icon.ExitRequested += () => _exit?.Invoke();

            Ready.Set();

            // 这个线程从此就在自己的消息循环里跑，托盘菜单、双击都在这里响应
            Application.Run(new ApplicationContext());
        }
        catch (Exception ex)
        {
            MainWindow.Trace($"tray host failed: {ex.Message}");
        }
        finally
        {
            try
            {
                _icon?.Dispose();
            }
            catch
            {
                // 忽略退出期异常
            }

            _icon = null;
        }
    }

    /// <summary>更新托盘提示文字（内部会发 NIM_MODIFY，所以必须回到托盘线程上做）。</summary>
    public static void SetTooltip(string text)
    {
        var context = _context;

        if (context is null || !Ready.IsSet)
            return;

        // 用 Post 不用 Send：界面线程绝不等待 shell
        context.Post(_ => _icon?.SetTooltip(text), null);
    }

    /// <summary>退出时收掉托盘图标。</summary>
    public static void Stop()
    {
        var context = _context;
        var thread = _thread;

        _thread = null;
        _context = null;

        try
        {
            context?.Post(_ =>
            {
                try
                {
                    _icon?.Dispose();
                    _icon = null;
                }
                catch
                {
                    // 忽略
                }

                Application.ExitThread();
            }, null);
        }
        catch
        {
            // 忽略
        }

        // 后台线程，等不到也不影响进程退出
        thread?.Join(400);
    }
}
