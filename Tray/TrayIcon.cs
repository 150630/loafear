using System.Drawing;
using System.Windows.Forms;

namespace LoafEar.Tray;

/// <summary>
/// 系统托盘图标。主窗口关掉后靠它把程序留在后台。
/// 图标取自内嵌的 brand.ico，和 exe 图标是同一份矢量渲染结果。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _glyph;
    private bool _disposed;

    public TrayIcon()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _glyph = LoadGlyph();
        MainWindow.Trace($"tray: glyph {sw.ElapsedMilliseconds}ms");

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        MainWindow.Trace($"tray: menu {sw.ElapsedMilliseconds}ms");

        menu.Items.Add(new ToolStripMenuItem("显示主界面", null, (_, _) => ShowWindow?.Invoke()));
        // 这项走的是老板键：藏字幕条的同时会把音频停掉，文案得说清楚，
        // 否则用户以为只是藏一块字幕，结果声音也停了
        menu.Items.Add(new ToolStripMenuItem("隐藏并暂停 / 显示并继续", null, (_, _) => ToggleBar?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("播放 / 暂停", null, (_, _) => TogglePlay?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = _glyph,
            Text = "LoafEar",
            Visible = true,
            ContextMenuStrip = menu,
        };
        MainWindow.Trace($"tray: notifyicon {sw.ElapsedMilliseconds}ms");

        _icon.DoubleClick += (_, _) => ShowWindow?.Invoke();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow?.Invoke();
        };
    }

    public event Action? ShowWindow;
    public event Action? ToggleBar;
    public event Action? TogglePlay;
    public event Action? ExitRequested;

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 上限 63 字符，超长会抛异常
        var value = text.Length > 60 ? text[..60] : text;
        try
        {
            _icon.Text = value;
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 从内嵌资源取图标。ico 里放了 16~256 共 9 档，
    /// 按系统托盘的实际尺寸挑最接近的一帧，高 DPI 下也不会糊。
    /// </summary>
    private static Icon LoadGlyph()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/brand.ico"))
            ?? throw new InvalidOperationException("brand.ico 没有打包进程序");

        using var stream = resource.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _glyph.Dispose();
    }
}
