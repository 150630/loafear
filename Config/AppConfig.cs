using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoafEar.Config;

public enum BarTheme
{
    Auto,
    Light,
    Dark,
}

/// <summary>字幕条贴在任务栏空位的哪一端。</summary>
public enum DockSide
{
    /// <summary>贴任务按钮区右边、靠任务栏左侧。</summary>
    Left,

    /// <summary>贴系统托盘左边、靠任务栏右侧（一直是这个行为）。</summary>
    Right,
}

/// <summary>
/// 可选倍速。只留三档慢速 + 原速：精听时真正用得上的就这几个，
/// 原来那套 0.5~2.0 的十档列表按起来太碎。
/// </summary>
public static class PlaybackSpeeds
{
    public static readonly double[] All = { 1.0, 0.8, 0.6, 0.4 };

    /// <summary>把任意值吸附到最接近的一档，手改配置也不会落到菜单外。</summary>
    public static double Nearest(double value)
    {
        var best = All[0];
        var distance = Math.Abs(value - best);

        foreach (var candidate in All)
        {
            var current = Math.Abs(value - candidate);
            if (current < distance)
            {
                best = candidate;
                distance = current;
            }
        }

        return best;
    }

    /// <summary>菜单与下拉框统一用这个文本，不带任何后缀说明。</summary>
    public static string Label(double value) => $"{value:0.0#}×";
}

public sealed class AppConfig
{
    /// <summary>
    /// 配置结构版本，用来把老配置一次性迁到新默认值。
    /// 默认必须是 0：属性初始值在反序列化时也会生效，写成 CurrentVersion 的话
    /// 老文件里没有这个字段，读出来就已经是「最新版」，迁移永远不触发。
    /// </summary>
    public int ConfigVersion { get; set; }

    private const int CurrentVersion = 3;

    // ---- 字幕条外观 ----
    public double BarWidthDip { get; set; } = 420;
    public double BarInsetDip { get; set; } = 2;
    public double MainFontSize { get; set; } = 13;
    public double SubFontSize { get; set; } = 11;
    public BarTheme Theme { get; set; } = BarTheme.Auto;
    public bool ShowTranslation { get; set; } = true;

    /// <summary>字幕条右侧的播放时间。默认关掉，需要时在主界面「外观」里打开。</summary>
    public bool ShowTime { get; set; }

    /// <summary>吸附在任务栏空位的哪一端。</summary>
    public DockSide DockSide { get; set; } = DockSide.Right;

    // ---- 悬浮字幕 ----
    /// <summary>
    /// 悬浮字幕：勾上就不贴任务栏了，改成可以在屏幕上随便拖的一条浮窗。
    /// 取消勾选会回到任务栏原来的位置。
    /// </summary>
    public bool FloatBar { get; set; }

    /// <summary>悬浮时的位置（DIP）。负数表示还没拖过，用默认位置。</summary>
    public double FloatLeftDip { get; set; } = -1;
    public double FloatTopDip { get; set; } = -1;

    /// <summary>悬浮字幕是否已「上锁」：上锁后鼠标点字幕是暂停 / 继续，不能再拖。</summary>
    public bool FloatLocked { get; set; }

    /// <summary>鼠标不在字幕条上时的整体不透明度。</summary>
    public double OpacityIdle { get; set; } = 0.72;

    /// <summary>鼠标悬停在字幕条上时的整体不透明度。</summary>
    public double OpacityHover { get; set; } = 1.0;

    /// <summary>
    /// 长句分页时，每次翻页往后让出多少毫秒（读的余量）。
    /// 0 = 翻页落在「这一页念完」的那一刻；往大调则上一屏多留一会儿。
    /// </summary>
    public double PageFlipLagMs { get; set; } = 380;

    /// <summary>不画底板，只留文字。默认打开：压在任务栏上时不挡图标、也更像原生字幕。</summary>
    public bool BackgroundTransparent { get; set; } = true;

    /// <summary>字幕整体时间偏移（毫秒，正数=字幕推后）。</summary>
    public double SubtitleOffsetMs { get; set; }

    // ---- 播放 ----
    public double Speed { get; set; } = 1.0;
    public bool LoopSingleCue { get; set; }
    public double Volume { get; set; } = 0.8;

    // ---- 行为 ----
    public bool ClickThrough { get; set; }
    public bool CaptureHide { get; set; } = true;
    public bool HideOnScreenBusy { get; set; } = true;

    /// <summary>关闭主窗口时收进托盘而不是退出。</summary>
    public bool MinimizeToTray { get; set; } = true;

    // ---- 听力目录 ----
    /// <summary>听力目录。设定后自动扫描里面的音频与字幕，不需要逐个添加。</summary>
    public string? LibraryDirectory { get; set; }

    /// <summary>当前播放的音频（听力目录里的绝对路径，或目录外的单文件）。</summary>
    public string? LastAudioPath { get; set; }

    public string? LastSubtitlePath { get; set; }

    /// <summary>上次停下的播放位置（毫秒）。启动时从这里接着显示，但保持暂停。</summary>
    public double LastPositionMs { get; set; }

    // ---- 全局快捷键 ----
    public HotkeySpec HotkeyToggle { get; set; } = new() { VirtualKey = 0x48 };      // Ctrl+Alt+H（老板键）
    public HotkeySpec HotkeyPlay { get; set; } = new() { VirtualKey = 0x20 };        // Ctrl+Alt+Space
    public HotkeySpec HotkeyPrev { get; set; } =
        new() { Modifiers = Interop.NativeMethods.MOD_CONTROL, VirtualKey = 0x25 };   // Ctrl+←
    public HotkeySpec HotkeyNext { get; set; } =
        new() { Modifiers = Interop.NativeMethods.MOD_CONTROL, VirtualKey = 0x27 };   // Ctrl+→
    public HotkeySpec HotkeyLoop { get; set; } = new() { VirtualKey = 0x4C };       // Ctrl+Alt+L
    public HotkeySpec HotkeyCollect { get; set; } = new() { VirtualKey = 0x53 };    // Ctrl+Alt+S
    public HotkeySpec HotkeyOffsetDown { get; set; } = new() { VirtualKey = 0xDB }; // Ctrl+Alt+[
    public HotkeySpec HotkeyOffsetUp { get; set; } = new() { VirtualKey = 0xDD };   // Ctrl+Alt+]

    [JsonIgnore]
    public static string ConfigDirectory { get; } = ResolveConfigDirectory();

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        var config = new AppConfig();

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);

                // 必须传和 Save 相同的 JsonOptions：
                // Save 用 JsonStringEnumConverter 把枚举写成 "Auto"，
                // 读的时候不带同一个 converter 就会因为 "Auto" 转不成枚举而整份配置报废。
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null)
                    config = loaded;
            }
        }
        catch (Exception ex)
        {
            // 配置损坏时回落到默认值，不阻断启动。
            // 但要把原因留下来，否则「设置全丢了」这种情况完全无从排查。
            try
            {
                File.WriteAllText(
                    Path.Combine(ConfigDirectory, "config-error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}");
            }
            catch
            {
                // 连日志都写不了就算了
            }
        }

        config.Normalize();
        config.Migrate();
        return config;
    }

    /// <summary>把可能越界的数值夹回合法区间，防止手改配置后界面炸掉。</summary>
    private void Normalize()
    {
        BarWidthDip = Math.Clamp(BarWidthDip, 220, 1200);
        MainFontSize = Math.Clamp(MainFontSize, 9, 24);
        SubFontSize = Math.Clamp(SubFontSize, 8, 20);
        OpacityIdle = Math.Clamp(OpacityIdle, 0.05, 1.0);
        OpacityHover = Math.Clamp(OpacityHover, 0.05, 1.0);
        PageFlipLagMs = Math.Clamp(PageFlipLagMs, 0, 1200);
        Speed = PlaybackSpeeds.Nearest(Speed);
        Volume = Math.Clamp(Volume, 0, 1);
        SubtitleOffsetMs = Math.Clamp(SubtitleOffsetMs, -10000, 10000);
        LastPositionMs = Math.Clamp(LastPositionMs, 0, 24 * 3600 * 1000);

        // 热键字段手改坏了（null / Key=0）时补回默认值，否则注册必然失败
        HotkeyToggle ??= new HotkeySpec { VirtualKey = 0x48 };
        HotkeyPlay ??= new HotkeySpec { VirtualKey = 0x20 };
        HotkeyPrev ??= PrevDefault();
        HotkeyNext ??= NextDefault();
        HotkeyLoop ??= new HotkeySpec { VirtualKey = 0x4C };
        HotkeyCollect ??= new HotkeySpec { VirtualKey = 0x53 };
        HotkeyOffsetDown ??= new HotkeySpec { VirtualKey = 0xDB };
        HotkeyOffsetUp ??= new HotkeySpec { VirtualKey = 0xDD };

        if (!HotkeyToggle.IsValid) HotkeyToggle = new HotkeySpec { VirtualKey = 0x48 };
        if (!HotkeyPlay.IsValid) HotkeyPlay = new HotkeySpec { VirtualKey = 0x20 };
        if (!HotkeyPrev.IsValid) HotkeyPrev = PrevDefault();
        if (!HotkeyNext.IsValid) HotkeyNext = NextDefault();
        if (!HotkeyLoop.IsValid) HotkeyLoop = new HotkeySpec { VirtualKey = 0x4C };
        if (!HotkeyCollect.IsValid) HotkeyCollect = new HotkeySpec { VirtualKey = 0x53 };
        if (!HotkeyOffsetDown.IsValid) HotkeyOffsetDown = new HotkeySpec { VirtualKey = 0xDB };
        if (!HotkeyOffsetUp.IsValid) HotkeyOffsetUp = new HotkeySpec { VirtualKey = 0xDD };
    }

    /// <summary>上一句的默认组合：Ctrl+←。</summary>
    private static HotkeySpec PrevDefault() =>
        new() { Modifiers = Interop.NativeMethods.MOD_CONTROL, VirtualKey = 0x25 };

    /// <summary>下一句的默认组合：Ctrl+→。</summary>
    private static HotkeySpec NextDefault() =>
        new() { Modifiers = Interop.NativeMethods.MOD_CONTROL, VirtualKey = 0x27 };

    /// <summary>
    /// 老配置一次性迁移。存在的原因是改了默认值：只改属性初始值对已经存过盘的
    /// config.json 不起作用，用户会以为改动没生效。
    /// </summary>
    private void Migrate()
    {
        if (ConfigVersion >= CurrentVersion)
            return;

        if (ConfigVersion < 2)
        {
            // v2：时间默认不显示，开关挪到主界面「外观」
            ShowTime = false;
        }

        if (ConfigVersion < 3)
        {
            // v3：透明背景改为默认打开
            BackgroundTransparent = true;

            // 上一句 / 下一句的默认组合从 Ctrl+Alt+, . 换成 Ctrl+← →。
            // 只在还留着老默认值时才换 —— 用户自己改过的不动。
            if (IsOldPrevDefault(HotkeyPrev))
                HotkeyPrev = PrevDefault();
            if (IsOldNextDefault(HotkeyNext))
                HotkeyNext = NextDefault();
        }

        ConfigVersion = CurrentVersion;
    }

    private static bool IsOldPrevDefault(HotkeySpec? spec) =>
        spec is not null && spec.VirtualKey == 0xBC &&
        spec.Modifiers == (Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT);

    private static bool IsOldNextDefault(HotkeySpec? spec) =>
        spec is not null && spec.VirtualKey == 0xBE &&
        spec.Modifiers == (Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 无写入权限时静默失败
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ResolveConfigDirectory()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var probe = Path.Combine(exeDir, ".write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return exeDir;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LoafEar");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
