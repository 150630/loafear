using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LoafEar.Media;

namespace LoafEar.UI;

/// <summary>右键快捷菜单的数据与回调。菜单每次弹出都重建，保证勾选状态是当下的。</summary>
public sealed class QuickMenuModel
{
    public bool IsLoaded { get; init; }
    public bool IsPlaying { get; init; }
    public bool HasTrack { get; init; }
    public bool Loop { get; init; }
    public bool ShowTranslation { get; init; }
    public bool IsHidden { get; init; }
    public double Speed { get; init; }

    /// <summary>听力目录里的节目，用于二级菜单切换。</summary>
    public IReadOnlyList<LibraryItem> Items { get; init; } = Array.Empty<LibraryItem>();

    /// <summary>当前正在播的音频路径，用来给二级菜单打勾。</summary>
    public string? CurrentAudioPath { get; init; }

    public string ToggleGesture { get; init; } = string.Empty;
    public string LoopGesture { get; init; } = string.Empty;
    public string PlayGesture { get; init; } = string.Empty;
    public string PrevGesture { get; init; } = string.Empty;
    public string NextGesture { get; init; } = string.Empty;
    public string FavoriteGesture { get; init; } = string.Empty;

    /// <summary>当前这句是否已经收藏，决定菜单里写「收藏本句」还是「取消收藏」。</summary>
    public bool IsFavorited { get; init; }

    /// <summary>有没有「当前句」可收藏（没字幕、没装载时点不动）。</summary>
    public bool CanFavorite { get; init; }

    public Action? OnPlayPause { get; init; }
    public Action? OnPrevCue { get; init; }
    public Action? OnNextCue { get; init; }
    public Action? OnOpenSettings { get; init; }
    public Action? OnToggleVisible { get; init; }
    public Action? OnToggleFavorite { get; init; }
    public Action? OnExit { get; init; }

    public Action<double>? OnSpeed { get; init; }
    public Action<bool>? OnLoop { get; init; }
    public Action<bool>? OnTranslation { get; init; }

    /// <summary>切到目录里的另一个节目。</summary>
    public Action<LibraryItem>? OnSelectItem { get; init; }
}

internal static class QuickMenu
{
    /// <summary>二级菜单一屏显示几个节目，多出来的靠滚轮往下翻。</summary>
    private const int ItemsPerScreen = 8;

    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static ContextMenu Build(QuickMenuModel model)
    {
        var menu = new ContextMenu
        {
            Style = Style("QuickMenu"),
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };

        menu.Items.Add(IconRow(model));
        menu.Items.Add(Separator());

        // 收藏放在最上面：它是「对当前这句」的动作，和图标行是一组的
        menu.Items.Add(Item(
            model.IsFavorited ? "取消收藏" : "收藏本句",
            model.OnToggleFavorite,
            model.CanFavorite,
            gesture: model.FavoriteGesture));

        menu.Items.Add(Separator());

        menu.Items.Add(SpeedMenu(model));
        menu.Items.Add(ProgramMenu(model, menu));

        menu.Items.Add(Separator());

        // 时间和透明底放到主界面「外观」里了，这里只留译文一个开关
        menu.Items.Add(ToggleItem("译文", model.ShowTranslation, model.OnTranslation));

        menu.Items.Add(Separator());
        menu.Items.Add(Item("设置", model.OnOpenSettings));

        // 老板键：一次按下藏起来并暂停，再按一次回来并接着放。
        // 文案不带「老板键」三个字 —— 菜单只有 200 出头，加上去会顶到右边键位
        menu.Items.Add(Item(
            model.IsHidden ? "显示并继续" : "隐藏并暂停",
            model.OnToggleVisible,
            gesture: model.ToggleGesture));

        menu.Items.Add(Item("退出", model.OnExit));

        return menu;
    }

    // ------------------------------------------------------------------ 图标行

    /// <summary>上一句 / 播放暂停 / 下一句 / 单句循环，一行四个图标。</summary>
    private static MenuItem IconRow(QuickMenuModel model)
    {
        var idle = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var muted = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6));
        var wash = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

        // 循环那颗往右让 5px。整排加同样的左边距会被居中抵掉，
        // 只有最后一颗真的动 —— 前三颗原地不动，看着就是「循环往右挪了一点」。
        const double loopNudge = 5;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(loopNudge, 0, 0, 0),
        };

        // 这排图标跟播放页用的是同一套（跳句是「竖条 + 三角」，循环是箭头 + 1），
        // 只是菜单按钮没有深色圆盘，所以播放 / 暂停取「只有图形」的那版。
        panel.Children.Add(IconButton(
            Icons.Make(Icons.SkipPrev, 15, idle, filled: true), model.OnPrevCue,
            model.HasTrack, model.PrevGesture));

        // 播放 / 暂停：菜单开着的时候点一下图标就得当场翻面。
        // model 里那份是「打开菜单那一刻」的快照，靠它永远不会变，所以自己记状态。
        var playing = model.IsPlaying;
        var playButton = IconButton(
            PlayPauseGlyph(playing, idle), null, model.IsLoaded, model.PlayGesture);
        playButton.Click += (_, _) =>
        {
            playing = !playing;
            playButton.Content = PlayPauseGlyph(playing, idle);
            model.OnPlayPause?.Invoke();
        };
        panel.Children.Add(playButton);

        panel.Children.Add(IconButton(
            Icons.Make(Icons.SkipNext, 15, idle, filled: true), model.OnNextCue,
            model.HasTrack, model.NextGesture));

        // 循环是开关，开着就用高亮底；同样自己维护状态，点了立刻变
        var loopOn = model.Loop;
        var loopButton = IconButton(
            Icons.MakeLoopArrows(16, loopOn ? idle : muted), null, true, model.LoopGesture);
        loopButton.Margin = new Thickness(loopNudge, 0, 0, 0);
        if (loopOn)
            loopButton.Background = wash;
        loopButton.Click += (_, _) =>
        {
            loopOn = !loopOn;
            loopButton.Content = Icons.MakeLoopArrows(16, loopOn ? idle : muted);
            loopButton.Background = loopOn ? wash : Brushes.Transparent;
            model.OnLoop?.Invoke(loopOn);
        };
        panel.Children.Add(loopButton);

        var item = new MenuItem
        {
            Header = panel,
            Style = Style("QuickMenuIconRow"),
            StaysOpenOnClick = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        // 图标行自己处理点击，不要让菜单在按下时就收起
        item.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
        return item;
    }

    private static object PlayPauseGlyph(bool playing, Brush brush) =>
        playing
            ? Icons.MakePauseBare(16, brush)
            : Icons.MakePlayBare(16, brush);

    private static Button IconButton(
        object content, Action? action, bool enabled, string? gesture)
    {
        var button = new Button
        {
            Content = content,
            Style = Style("IconButton"),
            IsEnabled = enabled,
            ToolTip = gesture is { Length: > 0 } ? gesture : null,
        };

        button.Click += (_, _) => action?.Invoke();
        return button;
    }

    // ------------------------------------------------------------------ 二级菜单

    private static MenuItem SpeedMenu(QuickMenuModel model)
    {
        var root = new MenuItem
        {
            Style = Style("QuickSubMenu"),
            IsEnabled = model.IsLoaded,
        };

        // 右侧显示当前倍速，不用展开就能看到
        var header = new DockPanel { MinWidth = 74 };
        header.Children.Add(new TextBlock { Text = "变速" });
        header.Children.Add(new TextBlock
        {
            Text = $"{model.Speed:0.0}×",
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 0, 0),
        });

        root.Header = header;

        foreach (var speed in Config.PlaybackSpeeds.All)
        {
            var value = speed;
            var child = new MenuItem
            {
                Header = Config.PlaybackSpeeds.Label(value),
                IsCheckable = true,
                IsChecked = Math.Abs(model.Speed - value) < 0.01,
                Style = Style("QuickMenuItem"),
            };

            child.Click += (_, _) => model.OnSpeed?.Invoke(value);
            root.Items.Add(child);
        }

        return root;
    }

    private static MenuItem ProgramMenu(QuickMenuModel model, ContextMenu owner)
    {
        var root = new MenuItem
        {
            Style = Style("QuickSubMenu"),
            IsEnabled = model.Items.Count > 0,
        };

        var count = model.Items.Count;
        root.Header = new TextBlock
        {
            Text = count > 0 ? $"节目 ({count})" : "节目",
        };

        if (count == 0)
            return root;

        // 节目少则几集、多则上百个，全铺出来能把屏幕顶穿。
        // 一屏 8 项，剩下的滚轮往下翻：比在末尾加一句「还有 N 个…」实用得多。
        var list = new StackPanel();

        foreach (var item in model.Items)
        {
            var target = item;
            var isCurrent = !string.IsNullOrEmpty(model.CurrentAudioPath)
                            && string.Equals(
                                model.CurrentAudioPath, item.AudioPath,
                                StringComparison.OrdinalIgnoreCase);

            var child = new MenuItem
            {
                Header = item.HasSubtitle ? item.Title : $"{item.Title}  (无字幕)",
                IsCheckable = true,
                IsChecked = isCurrent,
                Style = Style("QuickMenuItem"),
                ToolTip = item.AudioPath,
            };

            if (!item.HasSubtitle)
                child.Opacity = 0.72;

            // 行不是自动生成的容器，菜单不会自己收；选完手动收起
            child.Click += (_, _) =>
            {
                model.OnSelectItem?.Invoke(target);
                owner.IsOpen = false;
            };
            list.Children.Add(child);
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = ItemsPerScreen * 26,
            Content = list,
            Focusable = false,
            CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // 行高由字号和 Padding 算出来，等第一行摆好再按实际高度校准，
        // 否则写死的 26 遇上不同的 DPI/字体就只露半个第 9 项。
        scroll.Loaded += (_, _) =>
        {
            if (list.Children.Count > 0
                && list.Children[0] is FrameworkElement row
                && row.ActualHeight > 0)
            {
                scroll.MaxHeight = row.ActualHeight * ItemsPerScreen;
            }
        };

        root.Items.Add(new MenuItem
        {
            Header = scroll,
            Style = Style("QuickMenuIconRow"),
            StaysOpenOnClick = true,
            Focusable = false,
        });

        return root;
    }

    // ------------------------------------------------------------------ 基础项

    private static MenuItem Item(
        string header, Action? action, bool enabled = true, string? gesture = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = Style("QuickMenuItem"),
            IsEnabled = enabled,
            InputGestureText = gesture ?? string.Empty,
        };

        item.Click += (_, _) => action?.Invoke();
        return item;
    }

    /// <summary>
    /// 单个开关：文字左、√ 贴右边缘。点一下立刻生效，菜单不收起，
    /// 对号当场跟着变，省得关了再开确认有没有打开。
    /// </summary>
    private static MenuItem ToggleItem(string label, bool on, Action<bool>? setter)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var check = new TextBlock
        {
            Text = "√",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = on ? Visibility.Visible : Visibility.Collapsed,
        };

        // 星号列吃掉剩余宽度，对号才会真的贴到菜单右边，
        // 而不是紧跟在「译文」两个字后面
        var panel = new Grid();
        panel.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(text, 0);
        Grid.SetColumn(check, 1);
        panel.Children.Add(text);
        panel.Children.Add(check);

        var item = new MenuItem
        {
            Header = panel,
            Style = Style("QuickMenuItem"),

            // 模板里的 ContentPresenter 跟着这个走，不设 Stretch 的话
            // header 只有内容那么宽，星号列没有剩余空间可分
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            StaysOpenOnClick = true,
        };

        var state = on;

        item.Click += (_, _) =>
        {
            state = !state;
            check.Visibility = state ? Visibility.Visible : Visibility.Collapsed;
            setter?.Invoke(state);
        };

        return item;
    }

    private static Separator Separator() => new() { Style = Style("QuickMenuSeparator") };
}
