using System.IO;
using System.Threading;
using System.Windows;

namespace LoafEar;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 诊断模式放在单实例检查之前，这样主程序运行时也能随时探测
        if (Array.IndexOf(e.Args, "--probe") >= 0)
        {
            RunProbe();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--selftest") >= 0)
        {
            RunSelfTest();
            Shutdown();
            return;
        }

        var iconArg = Array.FindIndex(e.Args, a => a.Equals("--makeicon", StringComparison.OrdinalIgnoreCase));
        if (iconArg >= 0)
        {
            var target = iconArg + 1 < e.Args.Length
                ? e.Args[iconArg + 1]
                : Path.Combine(AppContext.BaseDirectory, "brand.ico");
            RunMakeIcon(target);
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--dirsim") >= 0)
        {
            RunDirSim();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--cardprobe") >= 0)
        {
            RunCardProbe();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--widthsim") >= 0)
        {
            RunWidthSim();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--floatsim") >= 0)
        {
            RunFloatSim();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--speedshot") >= 0)
        {
            RunSpeedShot();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--shot") >= 0)
        {
            RunShot();
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--menushot") >= 0)
        {
            RunMenuShot();
            Shutdown();

            // 菜单关掉之后它那个 Popup 还会拖住消息循环，Shutdown 收不干净，
            // 表现为「图出完了、进程赖着不退」。诊断进程本来就不需要慢慢收尾，直接走。
            Environment.Exit(0);
            return;
        }

        if (Array.IndexOf(e.Args, "--barshot") >= 0)
        {
            RunBarShot();
            Shutdown();
            return;
        }

        // 回归测试：故意按住界面线程，看任务栏会不会跟着卡。
        // 字幕条只要还是 explorer 的子窗口，这个测试就一定会把任务栏拖死。
        var freezeArg = Array.FindIndex(
            e.Args, a => a.Equals("--freezetest", StringComparison.OrdinalIgnoreCase));
        if (freezeArg >= 0)
        {
            var blockMs = freezeArg + 1 < e.Args.Length && int.TryParse(e.Args[freezeArg + 1], out var ms)
                ? ms
                : 3000;
            RunFreezeTest(blockMs);
            return;
        }

        if (Array.IndexOf(e.Args, "--barstate") >= 0)
        {
            RunBarState();
            return;
        }

        if (Array.IndexOf(e.Args, "--jumpsim") >= 0)
        {
            var jumpArg = Array.FindIndex(e.Args, a => a.Equals("--jumpsim", StringComparison.OrdinalIgnoreCase));
            var audio = jumpArg + 1 < e.Args.Length
                ? e.Args[jumpArg + 1]
                : Path.Combine(AppContext.BaseDirectory, "E01-1.MP3");
            RunJumpSim(audio);
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--favsim") >= 0)
        {
            var favArg = Array.FindIndex(e.Args, a => a.Equals("--favsim", StringComparison.OrdinalIgnoreCase));
            var audio = favArg + 1 < e.Args.Length
                ? e.Args[favArg + 1]
                : Path.Combine(AppContext.BaseDirectory, "E01-1.MP3");
            RunFavSim(audio);
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--loopsim") >= 0)
        {
            var simArg = Array.FindIndex(e.Args, a => a.Equals("--loopsim", StringComparison.OrdinalIgnoreCase));
            var audio = simArg + 1 < e.Args.Length
                ? e.Args[simArg + 1]
                : Path.Combine(AppContext.BaseDirectory, "E01-1.MP3");
            RunLoopSim(audio);
            Shutdown();
            return;
        }

        var audioArg = Array.FindIndex(e.Args, a => a.Equals("--audiotest", StringComparison.OrdinalIgnoreCase));
        if (audioArg >= 0)
        {
            var target = audioArg + 1 < e.Args.Length
                ? e.Args[audioArg + 1]
                : Path.Combine(AppContext.BaseDirectory, "E01-1.MP3");
            RunAudioTest(target);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show(
                $"出错了：\n{args.Exception.Message}\n\n详情见 {LogPath}",
                "LoafEar", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _mutex = new Mutex(true, @"Local\LoafEar.SingleInstance", out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show("LoafEar 已经在运行了。", "LoafEar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        LoafEar.MainWindow.Trace("main window constructed");

        // 常驻卡顿记录器：界面线程一旦超过 300ms 不处理消息就记一条到 hang.log，
        // 省得下次再靠猜。平时只在内存里跑，不写盘。
        Diag.StallWatch.Start(window.Dispatcher);

        // 启动形态定版：开机就把主界面显示出来，字幕条同时吸附到任务栏。
        // 「静默启动」（开机只出字幕条、主界面靠右键唤起）试过，冷启动阶段右键会卡，
        // 已彻底撤掉，不再保留命令行开关 —— 一条入口到底，省得两种形态各自有坑。
        window.ShowMainWindow();
    }

    private static void DumpTree(IntPtr parent, int depth, System.Text.StringBuilder sb)
    {
        if (depth > 4)
            return;

        var child = Interop.NativeMethods.GetWindow(parent, Interop.NativeMethods.GW_CHILD);

        while (child != IntPtr.Zero)
        {
            var name = new System.Text.StringBuilder(256);
            Interop.NativeMethods.GetClassName(child, name, name.Capacity);

            Interop.NativeMethods.GetWindowRect(child, out var rect);
            var visible = Interop.NativeMethods.IsWindowVisible(child) ? "vis" : "hid";

            sb.AppendLine($"{new string(' ', depth * 2)}[{name}] {visible} {rect}");
            DumpTree(child, depth + 1, sb);

            child = Interop.NativeMethods.GetWindow(child, Interop.NativeMethods.GW_HWNDNEXT);
        }
    }

    /// <summary>诊断模式：把任务栏实测数据写到 probe.txt，便于排查吸附位置问题。</summary>
    /// <summary>把品牌矢量渲染成多尺寸 ico，顺带导出预览图（256 看细节，32 看托盘实际观感）。</summary>
    private static void RunMakeIcon(string path)
    {
        Brand.BrandIcon.WriteIco(path, new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });

        var folder = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;

        foreach (var size in new[] { 256, 32 })
        {
            File.WriteAllBytes(
                Path.Combine(folder, $"brand-preview-{size}.png"),
                Brand.BrandIcon.EncodePng(Brand.BrandIcon.Render(size)));
        }

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "makeicon.txt"),
            $"ok {path} ({new FileInfo(path).Length} bytes)");
    }

    private static void RunProbe()
    {
        var sb = new System.Text.StringBuilder();

        try
        {
            var tb = Interop.TaskbarService.Locate();

            if (tb is null)
            {
                sb.AppendLine("taskbar: NOT FOUND");
            }
            else
            {
                sb.AppendLine($"taskbar.hwnd   = 0x{tb.Handle.ToInt64():X}");
                sb.AppendLine($"taskbar.rect   = {tb.Rect}");
                sb.AppendLine($"taskbar.scale  = {tb.Scale:0.###}");
                sb.AppendLine($"taskbar.autohide = {tb.IsAutoHide}");
                sb.AppendLine($"taskbar.horizontal = {tb.IsHorizontal}");

                var tray = Interop.NativeMethods.FindWindowEx(
                    tb.Handle, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray != IntPtr.Zero && Interop.NativeMethods.GetWindowRect(tray, out var trayRect))
                    sb.AppendLine($"tray.rect      = {trayRect}");

                foreach (var cls in new[] { "ReBarWindow32", "MSTaskSwWClass", "MSTaskListWClass", "Start" })
                {
                    var h = Interop.NativeMethods.FindWindowEx(tb.Handle, IntPtr.Zero, cls, null);
                    if (h == IntPtr.Zero)
                        continue;
                    if (Interop.NativeMethods.GetWindowRect(h, out var r))
                        sb.AppendLine($"buttonarea[{cls}] = {r}");
                }

                foreach (var width in new double[] { 300, 420, 600, 900 })
                {
                    var s = Interop.TaskbarService.ComputeStrip(tb, width, 2);
                    sb.AppendLine($"strip({width,4} dip) = x={s.X,5} y={s.Y,3} w={s.Width,4} h={s.Height,3}");
                }

                sb.AppendLine($"screenbusy     = {Interop.TaskbarService.IsScreenBusy()}");

                // 检查字幕条是否真的成了任务栏的子窗口
                var overlay = Interop.NativeMethods.FindWindowEx(
                    tb.Handle, IntPtr.Zero, null, "LoafEar-Overlay");

                if (overlay == IntPtr.Zero)
                {
                    sb.AppendLine("overlay        = NOT ATTACHED");
                }
                else
                {
                    Interop.NativeMethods.GetWindowRect(overlay, out var overlayRect);
                    sb.AppendLine($"overlay.hwnd   = 0x{overlay.ToInt64():X}");
                    sb.AppendLine($"overlay.parent = 0x{Interop.NativeMethods.GetParent(overlay).ToInt64():X} (taskbar=0x{tb.Handle.ToInt64():X})");
                    sb.AppendLine($"overlay.rect   = {overlayRect}");
                    sb.AppendLine($"overlay.visible= {Interop.NativeMethods.IsWindowVisible(overlay)}");
                }

                sb.AppendLine();
                sb.AppendLine("--- 任务栏窗口树 ---");
                DumpTree(tb.Handle, 0, sb);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"probe failed: {ex}");
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    // ------------------------------------------------------------------ 自检

    /// <summary>
    /// 离线自检：不依赖声卡和界面，直接验证变速链路的时间伸缩比例与 ASS 解析结果。
    /// </summary>
    private static void RunSelfTest()
    {
        var sb = new System.Text.StringBuilder();

        try
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "selftest.wav");
            WriteSineWav(wav, seconds: 3, frequency: 440);

            sb.AppendLine("--- 变速时间伸缩 ---");
            foreach (var tempo in new[] { 1.0f, 0.8f, 0.6f, 0.4f, 1.5f, 0.5f, 2.0f })
            {
                using var reader = new NAudio.Wave.AudioFileReader(wav);
                var provider = new Media.TempoSampleProvider(reader) { Tempo = tempo };

                long frames = 0;
                var buffer = new float[8192];
                int read;

                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                    frames += read / reader.WaveFormat.Channels;

                var outSeconds = frames / (double)reader.WaveFormat.SampleRate;
                var expect = 3.0 / tempo;

                sb.AppendLine(
                    $"tempo={tempo:0.0}  输出={outSeconds:0.000}s  期望={expect:0.000}s  " +
                    $"位置={provider.Position.TotalSeconds:0.000}s  " +
                    $"误差={Math.Abs(outSeconds - expect) / expect * 100:0.0}%");
            }

            sb.AppendLine();
            sb.AppendLine("--- ASS 解析 ---");
            var assPath = Path.Combine(AppContext.BaseDirectory, "selftest.ass");
            File.WriteAllText(assPath, SampleAss, new System.Text.UTF8Encoding(false));

            var track = Subs.SubtitleParser.Load(assPath);
            sb.AppendLine($"format={track.Format}  count={track.Count}");

            for (var i = 0; i < track.Count; i++)
            {
                var cue = track.Cues[i];
                sb.AppendLine(
                    $"[{i}] {cue.Start:mm\\:ss\\.fff}→{cue.End:mm\\:ss\\.fff} " +
                    $"an={cue.Alignment} color=#{cue.Color:X6} karaoke=#{cue.KaraokeColor:X6} " +
                    $"bold={cue.Bold} italic={cue.Italic} karaoke={cue.HasKaraoke} " +
                    $"fad=({cue.FadeInMs:0},{cue.FadeOutMs:0})");
                sb.AppendLine($"    text = \"{cue.Text}\"");

                if (cue.Spans is null)
                {
                    sb.AppendLine("    spans = null");
                    continue;
                }

                foreach (var span in cue.Spans)
                {
                    sb.AppendLine(
                        $"    span at={span.KaraokeAtMs,6:0}ms bold={span.Bold} italic={span.Italic} " +
                        $"color={(span.Color is null ? "-" : $"#{span.Color:X6}")} \"{span.Text}\"");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"selftest failed: {ex}");
        }

        sb.AppendLine();
        sb.AppendLine("--- 字幕改写（保存按钮走的就是这条路径）---");

        try
        {
            var srtPath = Path.Combine(AppContext.BaseDirectory, "selftest-edit.srt");
            File.WriteAllText(
                srtPath,
                "1\r\n00:00:01,000 --> 00:00:03,000\r\nHello there.\r\n你好。\r\n\r\n" +
                "2\r\n00:00:04,000 --> 00:00:06,000\r\nSecond line.\r\n第二句。\r\n",
                new System.Text.UTF8Encoding(false));

            var before = Subs.SubtitleParser.Load(srtPath);
            var ok = Subs.SubtitleEditor.ReplaceCueText(
                srtPath, before.Cues[0].Start, before.Cues[0].Text, "Hi there, friend.", out var msg);

            var after = Subs.SubtitleParser.Load(srtPath);
            sb.AppendLine($"srt  changed={ok} ({msg})  count={after.Count}");
            sb.AppendLine($"     cue0 = \"{after.Cues[0].Text}\" / \"{after.Cues[0].Translation}\"");
            sb.AppendLine($"     cue1 = \"{after.Cues[1].Text}\" / \"{after.Cues[1].Translation}\"");
            sb.AppendLine($"     bak  = {File.Exists(srtPath + ".bak")}");

            var assEdit = Path.Combine(AppContext.BaseDirectory, "selftest-edit.ass");
            File.WriteAllText(assEdit, SampleAss, new System.Text.UTF8Encoding(false));

            var assBefore = Subs.SubtitleParser.Load(assEdit);
            var okAss = Subs.SubtitleEditor.ReplaceCueText(
                assEdit, assBefore.Cues[0].Start, assBefore.Cues[0].Text, "Edited ASS line.", out var msgAss);

            var assAfter = Subs.SubtitleParser.Load(assEdit);
            sb.AppendLine($"ass  changed={okAss} ({msgAss})  count={assAfter.Count}");
            sb.AppendLine($"     cue0 = \"{assAfter.Cues[0].Text}\"  karaoke={assAfter.Cues[0].HasKaraoke}");
            sb.AppendLine($"     cue1 = \"{assAfter.Cues[1].Text}\"  an={assAfter.Cues[1].Alignment}");

            // 折行台词：一句在文件里占两行（中文字幕几乎都这样）。
            // 解析会拼成一句，写回必须能定位回那两行，否则界面显示得出来、却永远存不进去。
            var wrapPath = Path.Combine(AppContext.BaseDirectory, "selftest-wrap.srt");
            File.WriteAllText(
                wrapPath,
                "1\r\n00:00:01,000 --> 00:00:05,000\r\n这是一句很长的中文字幕，\r\n在文件里被折成了两行。\r\n\r\n" +
                "2\r\n00:00:06,000 --> 00:00:08,000\r\nShort one.\r\n",
                new System.Text.UTF8Encoding(false));

            var wrapBefore = Subs.SubtitleParser.Load(wrapPath);
            var okWrap = Subs.SubtitleEditor.ReplaceCueText(
                wrapPath,
                wrapBefore.Cues[0].Start,
                wrapBefore.Cues[0].Text,
                "这是一句很长的中文字幕，在文件里被折成了两行（已改）。",
                out var msgWrap);

            var wrapAfter = Subs.SubtitleParser.Load(wrapPath);
            var wrapRaw = File.ReadAllText(wrapPath);

            sb.AppendLine($"wrap changed={okWrap} ({msgWrap})");
            sb.AppendLine($"     cue0 = \"{wrapAfter.Cues[0].Text}\"");
            sb.AppendLine($"     折行已合并成一行 = {!wrapRaw.Contains("在文件里被折成了两行。\r\n", StringComparison.Ordinal)}");

            // 双语 + 折行：英文两行、中文两行，译文也要能单独改
            var biPath = Path.Combine(AppContext.BaseDirectory, "selftest-bi.srt");
            File.WriteAllText(
                biPath,
                "1\r\n00:00:01,000 --> 00:00:05,000\r\nThis is a long English line\r\nsplit across two rows.\r\n" +
                "这是一句很长的话，\r\n也折成了两行。\r\n",
                new System.Text.UTF8Encoding(false));

            var biBefore = Subs.SubtitleParser.Load(biPath);
            var biText = biBefore.Cues[0].Text;
            var biSub = biBefore.Cues[0].Translation;

            var okBiMain = Subs.SubtitleEditor.ReplaceCueText(
                biPath, biBefore.Cues[0].Start, biText, "Short English.", out var msgBiMain);
            var okBiSub = Subs.SubtitleEditor.ReplaceCueText(
                biPath, biBefore.Cues[0].Start, biSub, "改过的译文。", out var msgBiSub);

            var biAfter = Subs.SubtitleParser.Load(biPath);
            sb.AppendLine($"bi   main={okBiMain} ({msgBiMain})  sub={okBiSub} ({msgBiSub})");
            sb.AppendLine($"     cue0 = \"{biAfter.Cues[0].Text}\" / \"{biAfter.Cues[0].Translation}\"");

            File.Delete(srtPath);
            File.Delete(srtPath + ".bak");
            File.Delete(assEdit);
            File.Delete(assEdit + ".bak");
            File.Delete(wrapPath);
            File.Delete(wrapPath + ".bak");
            File.Delete(biPath);
            File.Delete(biPath + ".bak");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"edit failed: {ex}");
        }

        sb.AppendLine();
        sb.AppendLine("--- 目录扫描与字幕配对 ---");

        try
        {
            var libDir = Path.Combine(AppContext.BaseDirectory, "libtest");
            if (Directory.Exists(libDir))
                Directory.Delete(libDir, true);
            Directory.CreateDirectory(libDir);

            // 造一组典型命名：同名字幕、带后缀字幕、无字幕、数字乱序
            void Touch(string name) => File.WriteAllText(Path.Combine(libDir, name), string.Empty);

            Touch("E01.mp3");
            Touch("E01.srt");
            Touch("E02.mp3");
            Touch("E02.中英.srt");
            Touch("E10.mp3");
            Touch("E3.mp3");
            Touch("E3.lrc");
            Touch("没有字幕的一集.mp3");

            foreach (var item in Media.MediaLibrary.Scan(libDir))
            {
                var sub = item.SubtitlePath is null
                    ? "(无)"
                    : Path.GetFileName(item.SubtitlePath);
                sb.AppendLine($"  {item.Title,-18} → {sub}");
            }

            Directory.Delete(libDir, true);

            // 顺便扫一遍程序目录（用户实际放素材的地方），确认真实环境也能识别
            sb.AppendLine($"  实际目录扫描：{AppContext.BaseDirectory}");
            var real = Media.MediaLibrary.Scan(AppContext.BaseDirectory);
            sb.AppendLine($"  识别到 {real.Count} 个音频");
            foreach (var item in real.Take(5))
                sb.AppendLine($"    {item.Title} → {item.SubtitlePath ?? "(无)"}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"library failed: {ex}");
        }

        sb.AppendLine();
        sb.AppendLine("--- 自定义快捷键 ---");

        try
        {
            var spec = new Config.HotkeySpec
            {
                Modifiers = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT,
                VirtualKey = 0x48,
            };

            var same = new Config.HotkeySpec
            {
                Modifiers = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT,
                VirtualKey = 0x48,
            };

            var other = new Config.HotkeySpec
            {
                Modifiers = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT,
                VirtualKey = 0x20,
            };

            sb.AppendLine($"  display={spec.Display}  valid={spec.IsValid}");
            sb.AppendLine($"  相同组合重复检测 = {spec.SameAs(same)}（应为 True）");
            sb.AppendLine($"  不同组合重复检测 = {spec.SameAs(other)}（应为 False）");

            // 八个默认热键都得有值、且互不重复，否则后注册的那个必然失败
            var defaults = new Config.AppConfig();
            var all = new (string Name, Config.HotkeySpec Spec)[]
            {
                ("老板键", defaults.HotkeyToggle),
                ("播放", defaults.HotkeyPlay),
                ("上一句", defaults.HotkeyPrev),
                ("下一句", defaults.HotkeyNext),
                ("单句循环", defaults.HotkeyLoop),
                ("收藏本句", defaults.HotkeyCollect),
                ("偏移−", defaults.HotkeyOffsetDown),
                ("偏移+", defaults.HotkeyOffsetUp),
            };

            var clashes = all
                .SelectMany((a, i) => all.Skip(i + 1)
                    .Where(b => a.Spec.SameAs(b.Spec))
                    .Select(b => $"{a.Name}×{b.Name}"))
                .ToList();

            var empty = all.Where(a => !a.Spec.IsValid).Select(a => a.Name).ToList();

            sb.AppendLine("  默认八键：" + string.Join(
                " / ", all.Select(a => $"{a.Name}={a.Spec.Display}")));
            sb.AppendLine($"  默认组合都有效 = {empty.Count == 0}（应为 True）"
                + (empty.Count > 0 ? "  缺值：" + string.Join("、", empty) : string.Empty));
            sb.AppendLine($"  默认组合互不重复 = {clashes.Count == 0}（应为 True）"
                + (clashes.Count > 0 ? "  冲突：" + string.Join("、", clashes) : string.Empty));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"hotkey failed: {ex}");
        }

        sb.AppendLine();
        sb.AppendLine("--- 右键快捷菜单 ---");

        try
        {
            var model = new UI.QuickMenuModel
            {
                IsLoaded = true,
                IsPlaying = false,
                HasTrack = true,
                Speed = 1.2,
                ShowTranslation = true,
                Items = new List<Media.LibraryItem>
                {
                    new() { AudioPath = @"C:\demo\E01.mp3", SubtitlePath = @"C:\demo\E01.srt" },
                    new() { AudioPath = @"C:\demo\E02.mp3" },
                },
                CurrentAudioPath = @"C:\demo\E01.mp3",
            };

            var menu = UI.QuickMenu.Build(model);
            sb.AppendLine($"style={(menu.Style is null ? "MISSING" : "ok")} items={menu.Items.Count}");

            foreach (var item in menu.Items)
            {
                switch (item)
                {
                    case System.Windows.Controls.MenuItem mi:
                        var label = mi.Header switch
                        {
                            string s => s,
                            System.Windows.Controls.TextBlock tb => tb.Text,
                            System.Windows.Controls.Panel p => $"<{p.GetType().Name} {p.Children.Count}项>",
                            _ => $"<{mi.Header?.GetType().Name}>",
                        };

                        var sub = mi.Items.Count > 0 ? $"  子菜单={mi.Items.Count}" : string.Empty;
                        sb.AppendLine($"  \"{label}\"{sub}  enabled={mi.IsEnabled}");
                        break;

                    case System.Windows.Controls.Separator:
                        sb.AppendLine("  ---");
                        break;

                    default:
                        sb.AppendLine($"  ? {item.GetType().Name}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"menu failed: {ex}");
        }

        sb.AppendLine();
        sb.AppendLine("--- 字幕条渲染 ---");

        try
        {
            var bar = new Overlay.SubtitleBar();
            bar.Show();
            bar.ApplyAppearance(new Config.AppConfig { BackgroundTransparent = true, OpacityIdle = 0.7 });

            var renderTrack = Subs.SubtitleParser.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "selftest.ass"));
            var frames = 0;

            foreach (var cue in renderTrack.Cues)
            {
                var total = cue.Duration.TotalMilliseconds;
                for (var ms = 0.0; ms <= total + 100; ms += 100)
                {
                    bar.SetContent(
                        cue,
                        string.Empty,
                        cue.Translation,
                        $"{ms / 1000:0.0}s",
                        cue.Start + TimeSpan.FromMilliseconds(ms),
                        true);
                    frames++;
                }
            }

            bar.SetContent(null, "未载入字幕", string.Empty, string.Empty, TimeSpan.Zero, true);
            Pump();

            sb.AppendLine($"ok  cue={renderTrack.Count} 渲染帧={frames} 无异常");

            // 超长句子应当分页显示，每页静止，末页右对齐
            var longCue = new Subs.Cue
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(4),
                Text = "The quick brown fox jumps over the lazy dog and then keeps running for quite a while",
            };

            bar.Width = 300;
            bar.SetContent(longCue, string.Empty, string.Empty, string.Empty, TimeSpan.Zero, true);
            Pump();

            var first = bar.ScrollState;
            sb.AppendLine(
                $"超长字幕（条宽 300，句长 4s，文本 {first.TextWidth:0}px，可视 {first.Viewport:0}px）：");
            sb.AppendLine($"  对照：FormattedText 量出 {bar.MeasureProbe(longCue.Text):0}px，字数 {longCue.Text.Length}");

            foreach (var page in bar.Pages)
            {
                sb.AppendLine(
                    $"  页 起={page.StartX,6:0} 宽={page.Width,6:0} 时间={page.StartMs,5:0}~{page.EndMs,5:0}ms");
            }

            foreach (var ms in new[] { 0, 900, 1900, 2100, 3000, 3990, 4000 })
            {
                bar.SetContent(
                    longCue,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    longCue.Start + TimeSpan.FromMilliseconds(ms),
                    true);
                Pump();

                sb.AppendLine($"  t={ms,4}ms 位移={bar.ScrollState.Shift,7:0.0}");
            }

            sb.AppendLine($"  状态：{bar.MainTextDump()}");

            // 多页 + 末页太短时的合并
            var longerCue = new Subs.Cue
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(7),
                Text = "I have been learning English for quite a few years but I still find it hard to follow "
                       + "native speakers when they talk fast, so I keep practising every single day.",
            };

            bar.SetContent(longerCue, string.Empty, string.Empty, string.Empty, TimeSpan.Zero, true);
            Pump();
            sb.AppendLine($"三段文本（可视 {bar.ScrollState.Viewport:0}px，共 {longerCue.Text.Length} 字）：");

            foreach (var page in bar.Pages)
            {
                sb.AppendLine(
                    $"  页 起={page.StartX,6:0} 宽={page.Width,6:0} 时间={page.StartMs,5:0}~{page.EndMs,5:0}ms");
            }

            // 把两屏都渲染出来，方便肉眼确认裁切和右对齐是否正确
            var shotDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(shotDir);

            foreach (var (label, at) in new[] { ("bar-page1", 0.0), ("bar-page2", 3500.0) })
            {
                bar.SetContent(
                    longCue,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    longCue.Start + TimeSpan.FromMilliseconds(at),
                    true);
                Pump();
                Shoot(bar, Path.Combine(shotDir, $"{label}.png"));
            }

            // 正常长度的句子不该被裁切容器影响，仍要上下居中、译文在下面
            bar.Width = 400;
            var shortCue = new Subs.Cue
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(3),
                Text = "Hello world, again.",
                Translation = "你好，世界，再来一次。",
            };

            bar.SetContent(shortCue, string.Empty, shortCue.Translation, string.Empty, TimeSpan.Zero, true);
            Pump();
            Shoot(bar, Path.Combine(shotDir, "bar-short.png"));

            bar.Close();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"render failed: {ex}");
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 「单句循环 + 上一句」的真实路径回归：开一个真窗口（隐藏）、挂真字幕条、用真声卡放，
    /// 然后按界面的节拍走，中途在几个不同的时间点各按一次「上一句」，逐拍记下
    /// 位置 / 当前句 / 循环句 / 字幕条上显示的字。
    /// </summary>
    private static void RunJumpSim(string audioPath)
    {
        var sb = new System.Text.StringBuilder();
        var subtitlePath = Path.ChangeExtension(audioPath, ".srt");

        try
        {
            var window = new MainWindow();
            Current.MainWindow = window;
            window.StartHidden();

            var ready = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < ready)
            {
                Pump();
                if (window.OverlayReady)
                    break;
                Thread.Sleep(25);
            }

            Settle(400);

            var track = Subs.SubtitleParser.Load(subtitlePath);
            sb.AppendLine($"audio = {audioPath}");
            sb.AppendLine($"cues  = {track.Count}");

            if (track.Count == 0)
                throw new InvalidOperationException("没解析到字幕");

            var startCue = Math.Min(4, track.Count - 1);
            sb.AppendLine($"start = 第 {startCue + 1} 句，{track.Cues[startCue].Start.TotalSeconds:0.000}s");

            window.SetLoopForDiag(true);

            if (!window.LoadForDiag(audioPath, subtitlePath, track.Cues[startCue].Start))
                throw new InvalidOperationException("加载失败");

            sb.AppendLine();
            sb.AppendLine(window.TrackInfoForDiag(startCue - 2, 5));

            // 音量压到 5%：会真的出声，但别吵
            window.SetVolumeForDiag(0.05);
            window.PlayForDiag();

            // 四次按键分别落在：句中间、刚跳完（<1.5s）、刚跳完（<1.5s）、本句已播过 1.5 秒之后
            var pressTicks = new HashSet<int> { 60, 66, 76, 110 };

            sb.AppendLine();
            sb.AppendLine("tick   时刻      位置      当前句 循环句  动作");

            for (var tick = 0; tick < 200; tick++)
            {
                Pump();
                Thread.Sleep(50);

                var note = "";

                if (pressTicks.Contains(tick))
                {
                    sb.AppendLine($"{tick,4} {tick * 50,6}ms {window.CueStateForDiag()}  << 按「上一句」之前");
                    window.JumpForDiag(-1);
                    Pump();
                    note = "  <<< 按「上一句」";
                }

                sb.AppendLine($"{tick,4} {tick * 50,6}ms {window.CueStateForDiag()}{note}");
            }

            // 两套跳句规则本身：顺序模式「播过 1.5 秒先回本句开头」，单句循环模式一律退一句
            sb.AppendLine();
            sb.AppendLine("--- ResolveJump ---");

            var cases = new (string Name, int Got, int Want)[]
            {
                ("顺序·已播 2s 按上一句 → 本句", Subs.CueLoop.ResolveJump(10, TimeSpan.FromSeconds(2), -1, 74, true), 10),
                ("顺序·已播 0.5s 按上一句 → 上一句", Subs.CueLoop.ResolveJump(10, TimeSpan.FromSeconds(0.5), -1, 74, true), 9),
                ("循环·已播 2s 按上一句 → 上一句", Subs.CueLoop.ResolveJump(10, TimeSpan.FromSeconds(2), -1, 74, false), 9),
                ("循环·第一句按上一句 → 停在第一句", Subs.CueLoop.ResolveJump(0, TimeSpan.FromSeconds(0.5), -1, 74, false), 0),
                ("循环·按下一句 → 下一句", Subs.CueLoop.ResolveJump(10, TimeSpan.FromSeconds(2), 1, 74, false), 11),
            };

            foreach (var (name, got, want) in cases)
                sb.AppendLine($"  {(got == want ? "[通过]" : "[失败]")} {name}（得到 {got}）");

            window.RequestExit();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"jumpsim failed: {ex}");
            Current.Shutdown();
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "jumpsim.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 收藏 / 老板键 / 偏移的离屏回归。真窗口 + 真声卡 + 真字幕，跑的就是界面用的那套代码：
    ///
    /// 1. 收藏开关：收一句 → 再按一次应当取消 → 再收回（Ctrl+Alt+S 是开关）；
    /// 2. 「本节目」筛选：收三句就该出三行；
    /// 3. 老板键：一次隐藏并暂停，再一次显示并接着放；
    /// 4. 偏移 ±100ms：数值、字幕轨、输入框三处必须同步；
    /// 5. 「全部播放」：连播三句，看 pos 是否 0→1→2 推进、最后一句放完是否停下。
    ///
    /// 会真的出声（音量压到 2%）。跑之前备份 favorites.json，跑完原样放回，
    /// 不能把用户真实收藏洗掉。
    /// </summary>
    private static void RunFavSim(string audioPath)
    {
        var sb = new System.Text.StringBuilder();
        var subtitlePath = Path.ChangeExtension(audioPath, ".srt");
        var favPath = Path.Combine(Config.AppConfig.ConfigDirectory, "favorites.json");

        string? backup = null;
        try
        {
            if (File.Exists(favPath))
                backup = File.ReadAllText(favPath);
        }
        catch
        {
            // 备份不了就不备份，下面照样跑
        }

        try
        {
            sb.AppendLine($"audio = {audioPath}");
            sb.AppendLine($"sub   = {subtitlePath}");
            sb.AppendLine($"fav   = {favPath}");

            var window = new MainWindow();
            Current.MainWindow = window;
            window.StartHidden();

            var ready = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < ready)
            {
                Pump();
                if (window.OverlayReady)
                    break;
                Thread.Sleep(25);
            }

            Settle(400);

            var track = Subs.SubtitleParser.Load(subtitlePath);
            sb.AppendLine($"cues  = {track.Count}");

            if (track.Count < 4)
                throw new InvalidOperationException("字幕句数太少，跑不了这项回归");

            window.SetVolumeForDiag(0.02);

            if (!window.LoadForDiag(audioPath, subtitlePath, track.Cues[0].Start))
                throw new InvalidOperationException("加载失败");

            sb.AppendLine();
            sb.AppendLine("--- 收藏开关 ---");
            sb.AppendLine($"  初始       {window.FavoriteStateForDiag()}");
            window.ToggleFavoriteForDiag();
            sb.AppendLine($"  收第 1 句  {window.FavoriteStateForDiag()}");
            window.ToggleFavoriteForDiag();
            sb.AppendLine($"  再按一次   {window.FavoriteStateForDiag()}   （应为空）");
            window.ToggleFavoriteForDiag();
            sb.AppendLine($"  收回来     {window.FavoriteStateForDiag()}");

            sb.AppendLine();
            sb.AppendLine("--- 再收两句，「本节目」应当三行 ---");
            window.JumpForDiag(1);
            Settle(150);
            window.ToggleFavoriteForDiag();
            window.JumpForDiag(1);
            Settle(150);
            window.ToggleFavoriteForDiag();
            sb.AppendLine($"  收藏       {window.FavoriteStateForDiag()}");
            sb.AppendLine($"  行数       {window.FavoriteRowCountForDiag()}   （应为 3）");

            sb.AppendLine();
            sb.AppendLine("--- 老板键 ---");
            window.PlayForDiag();
            Settle(250);
            sb.AppendLine($"  按之前     {window.BossKeyStateForDiag()}");
            window.ToggleBossKeyForDiag();
            Settle(200);
            sb.AppendLine($"  按一下     {window.BossKeyStateForDiag()}   （应 hidden=True playing=False）");
            window.ToggleBossKeyForDiag();
            Settle(200);
            sb.AppendLine($"  再按一次   {window.BossKeyStateForDiag()}   （应 hidden=False playing=True）");

            sb.AppendLine();
            sb.AppendLine("--- 偏移 ±100ms ---");
            sb.AppendLine($"  初始       {window.OffsetStateForDiag()}");
            window.NudgeOffsetForDiag(100);
            sb.AppendLine($"  +100       {window.OffsetStateForDiag()}");
            window.NudgeOffsetForDiag(-100);
            window.NudgeOffsetForDiag(-100);
            sb.AppendLine($"  -200       {window.OffsetStateForDiag()}");
            window.NudgeOffsetForDiag(100);
            sb.AppendLine($"  归零       {window.OffsetStateForDiag()}");

            sb.AppendLine();
            sb.AppendLine("--- 全部播放（连播三句）---");
            window.StartFavoriteQueueForDiag();
            Settle(320);
            sb.AppendLine($"  连播中     {window.FavPlayStateForDiag()}   （应为 playing=True glyph=pause）");
            window.ClickFavPlayForDiag();
            Settle(320);
            sb.AppendLine($"  点一下     {window.FavPlayStateForDiag()}   （应为 playing=False glyph=play）");
            window.ClickFavPlayForDiag();
            Settle(320);
            sb.AppendLine($"  再点一下   {window.FavPlayStateForDiag()}   （应为 playing=True glyph=pause）");
            Settle(120);

            var seen = new List<int>();
            for (var tick = 0; tick < 160; tick++)
            {
                var state = window.QueueStateForDiag();
                sb.AppendLine($"  {tick,4} {tick * 100,6}ms {state}");

                var pos = state.Split(' ')[0].Replace("pos=", string.Empty);
                if (int.TryParse(pos.Split('/')[0], out var p) && p >= 0 && (seen.Count == 0 || seen[^1] != p))
                    seen.Add(p);

                Pump();
                Thread.Sleep(100);

                if (seen.Count > 0 && state.StartsWith("pos=-1"))
                    break;
            }

            sb.AppendLine($"  走过的位置 {string.Join(" → ", seen)}   （应为 0 → 1 → 2）");
            sb.AppendLine($"  结束状态   {window.QueueStateForDiag()}");

            window.RequestExit();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"favsim failed: {ex}");
            Current.Shutdown();
        }
        finally
        {
            try
            {
                if (backup is not null)
                    File.WriteAllText(favPath, backup);
                else if (File.Exists(favPath))
                    File.Delete(favPath);
            }
            catch
            {
                // 还原不了就算了，别把诊断结果藏起来
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "favsim.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 单句循环 + 「上一句」的离屏回归。真实声卡放真音频，按界面的 50ms 节拍跑同一套
    /// <see cref="Subs.CueLoop"/>，中途模拟按一下「上一句」，逐拍记录位置 / 当前句 / 循环句。
    ///
    /// 判据三条：
    /// 1. 循环开着的时候，循环句不许自己往后挪（以前会一拍挪一句）；
    /// 2. 按「上一句」之后的落点必须正好是目标句；
    /// 3. 落点之后必须停得住，不能又被循环逻辑带跑。
    ///
    /// 声音会真的响十几秒（音量压到 5%）。
    /// </summary>
    private static void RunLoopSim(string audioPath)
    {
        var sb = new System.Text.StringBuilder();
        var subtitlePath = Path.ChangeExtension(audioPath, ".srt");

        try
        {
            sb.AppendLine($"audio = {audioPath}");
            sb.AppendLine($"sub   = {subtitlePath}");

            var track = Subs.SubtitleParser.Load(subtitlePath);
            sb.AppendLine($"cues  = {track.Count}");

            if (track.Count == 0)
                throw new InvalidOperationException("没解析到字幕，检查 .srt 路径");

            // 从第 5 句开头起播，保证循环句前后都有邻居
            var startCue = Math.Min(4, track.Count - 1);
            var start = track.Cues[startCue].Start;

            using var player = new Media.AudioPlayer();
            player.Load(audioPath, 0.05);
            player.Position = start;
            player.Play();

            var loop = new Subs.CueLoop { Enabled = true };

            const int TickMs = 50;
            const int TotalTicks = 240;   // 12 秒
            const int PressTick = 100;    // 第 5 秒按一下「上一句」

            var before = new List<int>();
            var after = new List<int>();
            var rewinds = 0;
            var jumpTarget = -1;
            var landed = -1;

            sb.AppendLine($"start = {start.TotalSeconds:0.000}s（第 {startCue + 1} 句）");
            sb.AppendLine();
            sb.AppendLine("tick   时刻      位置     当前句  循环句  动作");

            for (var tick = 0; tick < TotalTicks; tick++)
            {
                Thread.Sleep(TickMs);

                var position = player.Position;
                var index = track.IndexAt(position);
                var note = "";

                if (tick == PressTick)
                {
                    var played = index >= 0 ? position - track.Cues[index].Start : TimeSpan.Zero;
                    jumpTarget = Subs.CueLoop.ResolveJump(index, played, -1, track.Count, restartFirst: false);

                    if (jumpTarget >= 0)
                    {
                        player.Position = track.Cues[jumpTarget].Start;
                        loop.Jump(jumpTarget);
                    }

                    sb.AppendLine(
                        $"{tick,4} {tick * TickMs,6}ms {position.TotalSeconds,8:0.000}s {index,6} {loop.Index,6}" +
                        $"  <<< 按「上一句」：本句已播 {played.TotalSeconds:0.000}s，目标 {jumpTarget}");

                    continue;
                }

                if (tick == PressTick + 1)
                    landed = index;

                var step = loop.OnTick(index);

                if (step == Subs.CueLoopStep.Rewind)
                {
                    player.Position = track.Cues[loop.Index].Start;
                    rewinds++;
                    note = "  <<< 回到句首";
                }

                if (tick < PressTick)
                    before.Add(loop.Index);
                else
                    after.Add(loop.Index);

                sb.AppendLine(
                    $"{tick,4} {tick * TickMs,6}ms {position.TotalSeconds,8:0.000}s {index,6} {loop.Index,6}{note}");
            }

            var loopStableBefore = before.Count > 0 && before.TrueForAll(i => i == before[0]);
            var holdsAfterJump = after.Count > 0 && jumpTarget >= 0 && after.TrueForAll(i => i == jumpTarget);

            sb.AppendLine();
            sb.AppendLine("--- 结论 ---");
            sb.AppendLine($"按「上一句」前，循环句    = {string.Join(",", before.Distinct().Order())}（应当只有一个）");
            sb.AppendLine($"按「上一句」目标 / 落点  = {jumpTarget} / {landed}（应当相同）");
            sb.AppendLine($"按「上一句」之后循环句    = {string.Join(",", after.Distinct().Order())}（应当只有目标那一个）");
            sb.AppendLine($"回到句首次数              = {rewinds}");
            sb.AppendLine();
            sb.AppendLine(loopStableBefore
                ? "[通过] 循环句没有自己往后挪"
                : "[失败] 循环句自己挪了：循环中位置被算到了上一句");
            sb.AppendLine(loopStableBefore && landed == jumpTarget
                ? "[通过] 「上一句」落点正确"
                : "[失败] 「上一句」没落在目标句上");
            sb.AppendLine(holdsAfterJump
                ? "[通过] 落点之后停得住"
                : "[失败] 落点之后又被循环逻辑带跑了");
            sb.AppendLine(rewinds >= 2
                ? $"[通过] 确实在循环（回到句首 {rewinds} 次）"
                : "[失败] 没观察到循环");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"loopsim failed: {ex}");
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "loopsim.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 真实设备播放自检：加载音频、初始化 WasapiOut、播放 1.5 秒，
    /// 记录播放状态与位置推进，用来定位「加载成功但没声音」这类问题。
    /// 声音会真的响一下。
    /// </summary>
    private static void RunAudioTest(string path)
    {
        var sb = new System.Text.StringBuilder();

        try
        {
            sb.AppendLine($"file = {path}");
            sb.AppendLine($"exists = {File.Exists(path)}");

            // 第一段：绕开 WasapiOut，直接读 provider，看时间伸缩层本身会不会卡住
            sb.AppendLine();
            sb.AppendLine("--- provider 直读（不经声卡） ---");

            using (var raw = new NAudio.Wave.AudioFileReader(path))
            {
                sb.AppendLine(
                    $"source fmt = {raw.WaveFormat.SampleRate}Hz {raw.WaveFormat.Channels}ch " +
                    $"{raw.WaveFormat.BitsPerSample}bit enc={raw.WaveFormat.Encoding}");

                var probe = new Media.TempoSampleProvider(raw) { Tempo = 1.0f };
                var buf = new float[8192];
                long totalFrames = 0;

                for (var i = 0; i < 12; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var read = probe.Read(buf, 0, buf.Length);
                    sw.Stop();
                    totalFrames += read / raw.WaveFormat.Channels;
                    sb.AppendLine(
                        $"  read#{i} 返回={read,6} 帧  用时={sw.ElapsedMilliseconds,4}ms  " +
                        $"累计={totalFrames / (double)raw.WaveFormat.SampleRate:0.000}s");
                }
            }

            sb.AppendLine();
            sb.AppendLine("--- 真实声卡播放 ---");

            using var player = new Media.AudioPlayer();

            player.StateChanged += (_, _) => sb.AppendLine("  [event] StateChanged");
            player.PlaybackEnded += (_, _) => sb.AppendLine("  [event] PlaybackEnded");

            player.Load(path, 0.5);
            sb.AppendLine($"loaded   isLoaded={player.IsLoaded} duration={player.Duration.TotalSeconds:0.000}s");
            sb.AppendLine($"speed    {player.Speed:0.0}");

            player.Play();
            sb.AppendLine($"afterPlay isPlaying={player.IsPlaying}");

            for (var i = 0; i < 15; i++)
            {
                Thread.Sleep(100);
                sb.AppendLine(
                    $"  t={i * 100,4}ms  state={player.OutputState,-9} playing={player.IsPlaying,-5}  " +
                    $"pos={player.Position.TotalSeconds:0.000}s  reads={player.ReadCalls}");
            }

            player.Pause();
            sb.AppendLine($"paused   isPlaying={player.IsPlaying} pos={player.Position.TotalSeconds:0.000}s");

            // 变速后再放一次，确认 tempo 路径也能出声
            player.Speed = 0.8;
            player.Position = TimeSpan.Zero;
            player.Play();
            sb.AppendLine($"speedTest isPlaying={player.IsPlaying} speed={player.Speed:0.0}");

            for (var i = 0; i < 10; i++)
            {
                Thread.Sleep(100);
                sb.AppendLine($"  t={i * 100,4}ms  playing={player.IsPlaying,-5}  pos={player.Position.TotalSeconds:0.000}s");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"audiotest failed: {ex}");
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "audiotest.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 把右键菜单渲成图片，专门用来验证二级菜单（自定义模板下的 PART_Popup 绑定）。
    /// 菜单本身在离屏窗口上打开，不会闪到用户眼前。
    /// </summary>
    private static void RunMenuShot()
    {
        var sb = new System.Text.StringBuilder();

        Window? host = null;

        try
        {
            host = new Window
            {
                Width = 1,
                Height = 1,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            host.Show();

            var model = new UI.QuickMenuModel
            {
                IsLoaded = true,
                IsPlaying = false,
                HasTrack = true,
                Loop = true,
                Speed = 1.2,
                ShowTranslation = true,
                Items = Enumerable.Range(1, 20)
                    .Select(i => new Media.LibraryItem
                    {
                        AudioPath = $@"C:\demo\E{i:00}.mp3",
                        SubtitlePath = i % 4 == 0 ? null : $@"C:\demo\E{i:00}.srt",
                    })
                    .ToList(),
                CurrentAudioPath = @"C:\demo\E02.mp3",
                ToggleGesture = "Ctrl+Alt+H",
                FavoriteGesture = "Ctrl+Alt+S",
                CanFavorite = true,
                IsFavorited = false,
            };

            var menu = UI.QuickMenu.Build(model);
            menu.PlacementTarget = host;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            menu.IsOpen = true;

            Pump();
            menu.UpdateLayout();

            var outDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            Shoot(menu, Path.Combine(outDir, "menu.png"));
            sb.AppendLine($"menu = {menu.ActualWidth:0}x{menu.ActualHeight:0} items={menu.Items.Count}");

            // 图标行点一下播放键，验证图标会当场翻面（菜单不重建也要变）
            var iconRow = menu.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .FirstOrDefault()?.Header as System.Windows.Controls.StackPanel;

            if (iconRow is not null)
            {
                var playButton = iconRow.Children
                    .OfType<System.Windows.Controls.Button>()
                    .Skip(1)
                    .FirstOrDefault();

                playButton?.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Pump();
                menu.UpdateLayout();
                Shoot(menu, Path.Combine(outDir, "menu-after-click.png"));
                sb.AppendLine("icon-row = 已模拟点击播放键");
            }

            // 优先展开「节目」那个滚动子菜单，没有的话退回子项最多的那个（变速）
            var submenu = menu.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .FirstOrDefault(m => m.Items.Count == 1
                                     && m.Items[0] is System.Windows.Controls.MenuItem host
                                     && host.Header is System.Windows.Controls.ScrollViewer)
                ?? menu.Items
                    .OfType<System.Windows.Controls.MenuItem>()
                    .FirstOrDefault(m => m.Items.Count > 5);

            if (submenu is not null)
            {
                submenu.IsSubmenuOpen = true;
                Pump();

                var popup = submenu.Template?.FindName("PART_Popup", submenu)
                    as System.Windows.Controls.Primitives.Popup;

                if (popup?.Child is FrameworkElement child)
                {
                    child.Measure(new Size(400, 700));
                    child.Arrange(new Rect(0, 0, child.DesiredSize.Width, child.DesiredSize.Height));
                    child.UpdateLayout();

                    Shoot(child, Path.Combine(outDir, "submenu.png"));
                    sb.AppendLine($"submenu = {child.ActualWidth:0}x{child.ActualHeight:0} 子项={submenu.Items.Count}");
                }
                else
                {
                    sb.AppendLine("submenu = PART_Popup 未找到或没有内容");
                }

                submenu.IsSubmenuOpen = false;
            }

            menu.IsOpen = false;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"menushot failed: {ex}");
        }
        finally
        {
            // 先落盘再关窗口：Popup 在无真实输入的离屏环境下关闭时，
            // WPF 内部可能抛 stylus 捕获异常，别让它把结果吞掉
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "menushot.txt"), sb.ToString());
            }
            catch
            {
                // 忽略
            }

            try
            {
                host?.Close();
            }
            catch
            {
                // 忽略关闭期异常
            }
        }
    }

    /// <summary>
    /// 离屏渲染字幕条，检查左右留白、长句分页的裁切是否正确，
    /// 顺便验证双击暂停在原生消息层能不能稳定触发。
    /// </summary>
    private static void RunBarShot()
    {
        var sb = new System.Text.StringBuilder();

        try
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            var config = Config.AppConfig.Load();
            config.Theme = Config.BarTheme.Dark;
            config.ShowTime = false;

            // 基线图固定拍「有底板」的样子：透明底现在是默认值，若跟着默认走，
            // 这张图会从「有底板」变成「只有字」，以后就分不清是默认值变了还是
            // 字幕条代码被改坏了。透明底的样子另有 --widthsim/--barstate 覆盖。
            config.BackgroundTransparent = false;

            var cue = new Subs.Cue
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(4),
                Text = "Double-click the bar to pause, right-click for settings.",
                Translation = "双击字幕条暂停，右键打开设置。",
            };

            var bar = new Overlay.SubtitleBar();
            bar.Show();
            bar.Width = 420;
            bar.Height = 40;
            bar.ApplyAppearance(config);
            bar.SetContent(cue, "", cue.Translation, "", TimeSpan.Zero, true);

            Pump();

            var root = (FrameworkElement)bar.Content;
            root.Measure(new Size(420, 40));
            root.Arrange(new Rect(0, 0, 420, 40));
            root.UpdateLayout();

            Shoot(root, Path.Combine(outDir, "bar.png"));

            // 调宽度时的临时样子：100% 不透明 + 实底 + 1px 灰描边（只出图，不改上面那张）。
            // 用浅色主题出图 —— 描边是灰的，摆在近黑的条上看不出来
            var light = Config.AppConfig.Load();
            light.Theme = Config.BarTheme.Light;
            light.ShowTime = false;

            bar.ApplyAppearance(light);
            bar.SetMeasureMode(true);
            // ApplyAppearance 会清掉富文本，换完主题得重新喂一遍内容
            bar.SetContent(cue, "", cue.Translation, "", TimeSpan.Zero, true);
            Pump();
            root.Measure(new Size(420, 40));
            root.Arrange(new Rect(0, 0, 420, 40));
            root.UpdateLayout();
            Shoot(root, Path.Combine(outDir, "bar-measure.png"));
            bar.SetMeasureMode(false);
            bar.ApplyAppearance(config);
            Pump();

            var texts = bar.FindText();
            sb.AppendLine($"bar={root.ActualWidth:0}x{root.ActualHeight:0} " +
                          $"text={texts.MainWidth:0} viewport={texts.Viewport:0} pages={texts.Pages}");
            sb.AppendLine($"  main=\"{texts.Main}\" sub=\"{texts.Sub}\"");

            // 长句分页：末页只剩一两个词的话，屏幕上会闪一下几乎空白的一页
            var longCue = new Subs.Cue
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(4.53),
                // 拿真实语料当样本：逗号、引号、省略号都有，能看出标点停顿算在哪一页
                Text = "It's like, all of my life, everyone has always told me, "
                       + "'You're a shoe! You're a shoe, you're a shoe, you're a shoe!'",
                Translation = "就好像，我这辈子所有人都一直跟我说，你是一只鞋……",
            };

            bar.SetContent(longCue, "", longCue.Translation, "", TimeSpan.Zero, true);
            Pump();

            root.Measure(new Size(420, 40));
            root.Arrange(new Rect(0, 0, 420, 40));
            root.UpdateLayout();

            // 再喂一次播放位置，让分页按当前宽度重算并落到某一页
            bar.SetContent(longCue, "", longCue.Translation, "", TimeSpan.FromSeconds(2), true);
            Pump();

            var pages = bar.Pages;
            sb.AppendLine($"paging pages={pages.Count} viewport={texts.Viewport:0}");

            for (var i = 0; i < pages.Count; i++)
            {
                var ratio = pages[i].Width / Math.Max(1, texts.Viewport) * 100;
                sb.AppendLine($"  page{i + 1} x={pages[i].StartX:0} w={pages[i].Width:0} " +
                              $"占宽={ratio:0}% t={pages[i].StartMs:0}~{pages[i].EndMs:0}ms " +
                              $"时长={pages[i].EndMs - pages[i].StartMs:0}ms\n    \"{pages[i].Text}\"");
            }

            // 原生左键路径：投两次按下/抬起模拟双击，应只算一次「暂停 / 继续」
            var (mx, my) = bar.ClientPoint();
            bar.BeginToggleProbe();
            bar.PostClick(mx, my);
            Settle(200);
            bar.PostClick(mx, my);
            Settle(300);
            sb.AppendLine($"  dblclick at ({mx},{my}) toggles={bar.ToggleProbeCount} " +
                          $"[{bar.PostResult}] [{bar.MsgStats}]");
            sb.AppendLine($"    last down: {bar.ToggleDebug}");

            bar.Close();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"barshot failed: {ex}");
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "barshot.txt"), sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 界面线程阻塞回归测试：等字幕条真正显示出来，然后把界面线程按住 blockMs 毫秒。
    /// 期间外部探针会把鼠标移到字幕条上并给任务栏发同步消息：
    /// 字幕条若是 explorer 的子窗口，任务栏会一起卡住（探针超时）；
    /// 修好之后任务栏应该照样秒回。
    /// </summary>
    private static void RunFreezeTest(int blockMs)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "freezetest.txt");
        var sb = new System.Text.StringBuilder();

        try
        {
            var window = new MainWindow();
            Current.MainWindow = window;
            window.StartHidden();

            var deadline = Environment.TickCount64 + 8000;
            while (!window.OverlayReady && Environment.TickCount64 < deadline)
            {
                Pump();
                Thread.Sleep(15);
            }

            var overlay = window.OverlayHandle;
            var rect = default(Interop.RECT);

            if (overlay != IntPtr.Zero)
                Interop.NativeMethods.GetWindowRect(overlay, out rect);

            sb.AppendLine($"ready={window.OverlayReady}");
            sb.AppendLine($"overlay=0x{overlay.ToInt64():X} rect={rect}");
            sb.AppendLine($"parent=0x{Interop.NativeMethods.GetParent(overlay).ToInt64():X}");
            sb.AppendLine($"visible={Interop.NativeMethods.IsWindowVisible(overlay)}");
            sb.AppendLine($"state=blocking at={DateTime.Now:HH:mm:ss.fff}");

            File.WriteAllText(path, sb.ToString());

            // 关键动作：按住界面线程。这里什么都不做，就是卡着不退。
            Thread.Sleep(blockMs);

            File.AppendAllText(path, $"state=released at={DateTime.Now:HH:mm:ss.fff}{Environment.NewLine}");

            window.RequestExit();
        }
        catch (Exception ex)
        {
            File.AppendAllText(path, $"freezetest failed: {ex}{Environment.NewLine}");
            Current.Shutdown();
        }
    }

    /// <summary>
    /// 诊断：启动到就绪后，把字幕条的窗口状态和离屏渲染结果落盘。
    /// 专门用来分辨「窗口确实在，但画出来是空的」这种问题。
    /// </summary>
    private static void RunBarState()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "barstate.txt");
        var sb = new System.Text.StringBuilder();

        try
        {
            var window = new MainWindow();
            Current.MainWindow = window;
            window.StartHidden();

            var deadline = Environment.TickCount64 + 6000;

            while (Environment.TickCount64 < deadline)
            {
                Pump();

                if (window.OverlayReady)
                    break;

                Thread.Sleep(25);
            }

            // 多给几帧，让透明度和首次渲染都落定
            Settle(600);

            sb.AppendLine(window.OverlayState());

            var overlay = window.OverlayHandle;
            var rect = default(Interop.RECT);

            if (overlay != IntPtr.Zero)
                Interop.NativeMethods.GetWindowRect(overlay, out rect);

            sb.AppendLine($"win32.rect={rect}");
            sb.AppendLine($"win32.parent=0x{Interop.NativeMethods.GetParent(overlay).ToInt64():X}");
            sb.AppendLine($"win32.visible={Interop.NativeMethods.IsWindowVisible(overlay)}");

            if (window.OverlayRoot is { } root)
            {
                sb.AppendLine($"root={root.ActualWidth:0}x{root.ActualHeight:0} opacity={root.Opacity:0.00}");

                var shotDir = Path.Combine(AppContext.BaseDirectory, "shots");
                Directory.CreateDirectory(shotDir);
                Shoot(root, Path.Combine(shotDir, "bar-live.png"));
                sb.AppendLine("shot=shots/bar-live.png");
            }

            sb.AppendLine("state=done");
            window.RequestExit();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"barstate failed: {ex}");
            Current.Shutdown();
        }

        try
        {
            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>跑一轮消息循环，让菜单的布局生效。</summary>
    private static void Pump()    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 反复 Pump 一小段时间。投递的 WM_* 消息和 Dispatcher 队列不在同一个队列里，
    /// 只 Pump 一次可能还没轮到，等一会儿更稳。
    /// </summary>
    private static void Settle(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        do
        {
            Pump();
            System.Threading.Thread.Sleep(15);
        }
        while (Environment.TickCount64 < deadline);
    }

    private static void Shoot(FrameworkElement element, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 播放 / 暂停四枚图标排成一张图（46 与 28 各一对），只给 --shot 核对几何用。
    /// 圆盘是按钮模板里画的，这里补一个同样颜色的椭圆，其他地方跟真按钮一致。
    /// </summary>
    private static FrameworkElement BuildIconSheet()
    {
        var disc = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x59, 0x57, 0x57));

        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Background = System.Windows.Media.Brushes.White,
            Margin = new Thickness(10),
        };

        FrameworkElement Cell(double size, bool pause)
        {
            var grid = new System.Windows.Controls.Grid
            {
                Width = size,
                Height = size,
                Margin = new Thickness(10),
            };

            grid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = disc });
            grid.Children.Add(pause
                ? UI.Icons.MakePauseGlyph(size)
                : UI.Icons.MakePlayGlyph(size));

            return grid;
        }

        row.Children.Add(Cell(46, false));
        row.Children.Add(Cell(46, true));
        row.Children.Add(Cell(28, false));
        row.Children.Add(Cell(28, true));

        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, row.DesiredSize.Width, row.DesiredSize.Height));
        row.UpdateLayout();

        return row;
    }

    /// <summary>
    /// 离屏渲染主界面各标签页，用来检查布局有没有溢出或错位。
    /// 不 Show 窗口，所以不会触发加载音频、吸附任务栏等副作用。
    /// </summary>
    private static void RunShot()
    {
        try
        {
            var win = new MainWindow();
            var root = (FrameworkElement)win.Content;

            // 摆一句双语字幕，「当前句」区（含可编辑的译文）才有东西可看
            win.ShowPreviewForShot(new Subs.Cue
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(4),
                Text = "Click the bar to pause, right-click for settings.",
                Translation = "点一下字幕条暂停，右键打开设置。",
            });

            // 节目下拉也塞几条样例：一条长的（要截断）、两条短的
            win.ShowAudioBoxForShot(
                "E01-1 The One Where Monica Gets a Roommate",
                "E02-1 The One with the Sonogram",
                "E03-1");

            // 收藏页摆几行假收藏，核对星标、两行文案、右侧「E01-1 · 03」和底栏
            win.FillFavoritesForShot();

            // 窗口有系统标题栏了，Width/Height 是含边框的外框尺寸；
            // 截图要的是客户区，把非客户区那圈扣掉，否则图会比真实观感高一条标题栏。
            var frame = SystemParameters.WindowNonClientFrameThickness;
            var w = win.Width - frame.Left - frame.Right;
            var h = win.Height - frame.Top - frame.Bottom;

            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();

            var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));

            var outDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            for (var i = 0; i < 5; i++)
            {
                win.SelectPage(i);

                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

                var file = Path.Combine(outDir, $"tab{i}.png");
                using var stream = File.Create(file);
                encoder.Save(stream);
            }

            // 轻提示单独出一张：它压在最底下，得确认不跟控制行打架
            win.SelectPage(0);
            win.ShowStatusForShot("已保存 · E01-5.srt");
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            Shoot(root, Path.Combine(outDir, "toast.png"));

            // 「目录」浮层单独出一张：节目清单 + 当前项浅灰底，离屏也要能验排版
            var dir = (FrameworkElement)win.ProgramPopup.Child;
            dir.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            dir.Arrange(new Rect(0, 0, dir.DesiredSize.Width, dir.DesiredSize.Height));
            dir.UpdateLayout();
            Shoot(dir, Path.Combine(outDir, "dir.png"));

            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "shot.txt"),
                $"size={width}x{height} tabs=5 dir={dir.ActualWidth:0}x{dir.ActualHeight:0}");

            // 播放/暂停四枚图标单独出一张：主截图上播放键是「未装载音频」的淡灰态，
            // 看不出白图形的几何对不对，这里按 46 / 28 两个直径各画一遍核形状。
            Shoot(BuildIconSheet(), Path.Combine(outDir, "icons.png"));
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "shot.txt"),
                    $"shot failed: {ex}");
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 「目录」浮层的离线自检：打开的节目清单对不对、当前项有没有浅灰底、
    /// 点一行能不能把选中项换过去并且把浮层收起来。
    /// </summary>
    private static void RunDirSim()
    {
        var lines = new List<string>();

        try
        {
            // Popup 只有在窗口真正显示之后才肯开，所以这里把窗口摆到屏幕外面跑
            // Popup 只有在窗口真正显示之后才肯开；量「右对齐 / 居中」还得让它在屏幕内，
            // 所以这次把窗口摆在屏幕里正常位置量（诊断进程，闪一下无所谓）
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 300,
                Top = 220,
                ShowInTaskbar = false,
            };

            win.Show();

            win.OpenDirForTest();
            lines.Add($"打开浮层={win.ProgramPopup.IsOpen}（应为 True）");
            Settle(150);
            lines.Add($"  位置 {win.ProgramPopupAlignForTest()}");
            win.CloseDirForTest();
            lines.Add($"收起浮层={!win.ProgramPopup.IsOpen}（应为 True）");

            win.OpenSpeedPopupForTest();
            Settle(200);
            lines.Add($"倍速下拉 {win.SpeedPopupAlignForTest()}");
            win.CloseSpeedPopupForTest();

            win.ShowAudioBoxForShot("E01-1 The One Where Monica Gets a Roommate", "E02-1", "E03-1");

            lines.Add($"清单行数={win.ProgramList.Children.Count}（应为 3）");
            lines.Add($"第 1 行是当前项={win.IsProgramHighlighted(0)}（应为 True）");
            lines.Add($"第 2 行是当前项={win.IsProgramHighlighted(1)}（应为 False）");

            win.OpenDirForTest();
            win.ClickProgramForTest(1);
            lines.Add($"点完浮层已收起={!win.ProgramPopup.IsOpen}（应为 True）");
            lines.Add($"行点击换到了={win.SelectedProgramForTest}（应为 E02-1）");
            lines.Add($"标题文字=\"{win.AudioTitleText.Text}\"");

            win.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"失败：{ex}");
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dirsim.txt"), string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 「常规」页第一张卡片的自检：量卡片边框、标题、两颗按钮相对窗口的位置，
    /// 专门核对「标题下移几像素、外框上下各收几像素」这种肉眼比不出来的调整。
    /// </summary>
    private static void RunCardProbe()
    {
        var lines = new List<string>();

        try
        {
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
            };

            win.Show();
            win.SelectPage(2);
            Settle(120);
            win.UpdateLayout();

            lines.Add(win.LibraryCardProbeForTest());
            win.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"失败：{ex}");
        }

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "cardprobe.txt"),
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 调宽度的自检：推一下滑条 → 字幕条应立刻变实（100% + 底板 + 1px 灰描边），
    /// 停手 1.4 秒后自己恢复成原来的半透明。
    /// </summary>
    private static void RunWidthSim()
    {
        var lines = new List<string>();

        try
        {
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
            };

            win.Show();
            Settle(200);
            lines.Add($"拖动前  {win.BarLookForTest()}");

            win.NudgeBarWidthForTest(60);
            Settle(120);
            lines.Add($"拖动后  {win.BarLookForTest()}");

            Settle(1800);
            lines.Add($"1.9 秒后 {win.BarLookForTest()}");

            win.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"失败：{ex}");
        }

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "widthsim.txt"),
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 抓真屏：主窗口摆到屏幕上、拉开倍速下拉，按按钮与浮层的真实屏幕坐标截一块图。
    /// Popup 是独立 HWND，RenderTargetBitmap 拍不到它压在窗口上的样子 ——
    /// 「浮层的框和按钮对不对齐」只能靠真屏截图看，光量窗口矩形会漏掉框里的内边距。
    /// </summary>
    private static void RunSpeedShot()
    {
        var lines = new List<string>();
        MainWindow? win = null;

        try
        {
            var outDir = Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 160,
                Top = 120,
                ShowInTaskbar = false,
            };

            win.Show();
            Settle(400);

            win.OpenSpeedPopupForTest();
            Settle(600);

            var (button, popup) = win.SpeedPopupRectsForTest();

            lines.Add($"文字 {win.SpeedTextMetricsForTest()}");
            lines.Add($"摆放 {win.LastSpeedPlacement}");
            lines.Add($"按钮 {button.Left:0},{button.Top:0}–{button.Right:0},{button.Bottom:0} " +
                      $"w={button.Width:0} h={button.Height:0}");
            lines.Add($"浮层 {popup.Left:0},{popup.Top:0}–{popup.Right:0},{popup.Bottom:0} " +
                      $"w={popup.Width:0} h={popup.Height:0}");
            lines.Add($"左差={button.Left - popup.Left:0} 右差={button.Right - popup.Right:0} " +
                      $"上距={popup.Top - button.Bottom:0}");

            if (popup.IsEmpty)
            {
                lines.Add("浮层没打开，没图可截");
            }
            else
            {
                // 按钮上方多留一点，能看清浮层压在窗口上的相对位置
                var left = (int)Math.Floor(Math.Min(button.Left, popup.Left)) - 14;
                var top = (int)Math.Floor(button.Top) - 18;
                var right = (int)Math.Ceiling(Math.Max(button.Right, popup.Right)) + 14;
                var bottom = (int)Math.Ceiling(Math.Max(button.Bottom, popup.Bottom)) + 14;
                var w = Math.Max(1, right - left);
                var h = Math.Max(1, bottom - top);

                var screen = Interop.NativeMethods.GetDC(IntPtr.Zero);
                var mem = Interop.NativeMethods.CreateCompatibleDC(screen);
                var bmp = Interop.NativeMethods.CreateCompatibleBitmap(screen, w, h);
                var old = Interop.NativeMethods.SelectObject(mem, bmp);

                Interop.NativeMethods.BitBlt(mem, 0, 0, w, h, screen, left, top, Interop.NativeMethods.SRCCOPY);

                Interop.NativeMethods.SelectObject(mem, old);
                Interop.NativeMethods.DeleteDC(mem);
                Interop.NativeMethods.ReleaseDC(IntPtr.Zero, screen);

                var shot = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bmp, IntPtr.Zero, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));

                using (var stream = File.Create(Path.Combine(outDir, "speed.png")))
                    encoder.Save(stream);

                Interop.NativeMethods.DeleteObject(bmp);
                lines.Add($"截图 {w}×{h} 从屏幕 {left},{top}");
            }

            win.CloseSpeedPopupForTest();
        }
        catch (Exception ex)
        {
            lines.Add($"speedshot failed: {ex}");
        }
        finally
        {
            win?.Close();
        }

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "speedshot.txt"),
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 悬浮字幕自检：勾上 → 脱离任务栏浮起来（多一行锁）、拖动记位置、点锁记状态、
    /// 取消 → 贴回任务栏；顺带比一下靠左 / 靠右两种吸附算出来的 x。
    /// </summary>
    private static void RunFloatSim()
    {
        var lines = new List<string>();

        try
        {
            // 字幕条要真的建出窗口句柄，所以主窗口还是得 Show 一次（摆到屏幕外）
            var win = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
            };

            win.Show();

            win.SetFloatForTest(true);
            lines.Add($"勾上悬浮     {win.FloatStateForTest()}");

            // 锁图标「悬停才露」：把字幕条挪到光标下 → 露出；挪远 → 收回。
            // 先悬停再截图，图里才看得到锁。
            lines.Add($"鼠标压在字幕上 锁图标={win.HoverLockForTest()}（应 True）");

            // 悬浮窗单独出一张：核对上面那行锁按钮、字幕条本体没被挤变形
            if (win.BarRootForTest is { } barRoot)
            {
                win.PushBarTextForTest(
                    "Click the bar to pause, right-click for settings.",
                    "点一下字幕条暂停，右键打开设置。",
                    "01:23");

                barRoot.Measure(new Size(barRoot.ActualWidth, barRoot.ActualHeight));
                barRoot.Arrange(new Rect(0, 0, barRoot.ActualWidth, barRoot.ActualHeight));
                barRoot.UpdateLayout();

                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "shots"));
                Shoot(barRoot, Path.Combine(AppContext.BaseDirectory, "shots", "float.png"));
            }

            lines.Add($"鼠标移开       锁图标={win.UnhoverLockForTest()}（应 False）");

            win.DragBarForTest(240, 180);
            var afterDrag = Config.AppConfig.Load();
            lines.Add($"拖动后配置   left/top={afterDrag.FloatLeftDip:0},{afterDrag.FloatTopDip:0}（应为 240,180）");

            // 拖动过程中的一次位移：窗口要跟着走（30/20 像素 ÷ 当前缩放）
            win.NudgeBarForTest(30, 20);
            lines.Add($"拖动位移 30,20 后 {win.FloatStateForTest()}");

            // 一路往屏幕最底下拖：按 WorkArea 夹的话任务栏那块是禁区，拖不过去。
            // 用 Nudge（走拖动那条 DragBy，带夹取）而不是 DragBar（直接赋值，不夹）。
            win.NudgeBarForTest(0, 2000);
            var bottomState = win.FloatStateForTest();
            var workBottom = SystemParameters.WorkArea.Bottom;
            var barTop = double.Parse(
                bottomState.Split("left/top=")[1].Split(' ')[0].Split(',')[1],
                System.Globalization.CultureInfo.InvariantCulture);

            lines.Add($"拖到最底部   top={barTop:0} / 工作区底={workBottom:0} " +
                      $"（top 应大于工作区底：{barTop > workBottom}）");

            win.DragBarForTest(240, 180);
            Settle(120);

            var locked = win.ToggleLockForTest();
            var afterLock = Config.AppConfig.Load();
            lines.Add($"点锁后       locked={locked} / 配置 FloatLocked={afterLock.FloatLocked}（都应 True）");

            var unlocked = win.ToggleLockForTest();
            lines.Add($"再点一次     locked={unlocked}（应为 False，解锁后才能再拖）");

            // 回到任务栏：任务栏轮询可能还没跑完，补几次守护时钟
            win.SetFloatForTest(false);

            for (var i = 0; i < 6; i++)
            {
                Thread.Sleep(80);
                win.GuardTickForTest();
            }

            lines.Add($"取消悬浮     {win.FloatStateForTest()}");

            var taskbar = Interop.TaskbarService.Locate();
            if (taskbar is null)
            {
                lines.Add("任务栏       未找到（跳过左右对比）");
            }
            else
            {
                var left = Interop.TaskbarService.ComputeStrip(taskbar, 420, 2, 160, Config.DockSide.Left);
                var right = Interop.TaskbarService.ComputeStrip(taskbar, 420, 2, 160, Config.DockSide.Right);
                lines.Add($"靠左/靠右    x={left.X} / {right.X}（靠左应更小：{left.X < right.X}）" +
                          $" 同一行 y={left.Y}/{right.Y} 宽={left.Width}/{right.Width}");
            }

            win.DockForTest(Config.DockSide.Left);
            var dockedLeft = Config.AppConfig.Load();
            lines.Add($"点靠左按钮   配置 DockSide={dockedLeft.DockSide}（应为 Left）");

            for (var i = 0; i < 6; i++)
            {
                Thread.Sleep(80);
                win.GuardTickForTest();
            }

            lines.Add($"  靠左后窗口 {win.BarWindowRectForTest()} / 任务栏 {win.TaskbarRectForTest()}");

            win.DockForTest(Config.DockSide.Right);
            var dockedRight = Config.AppConfig.Load();
            lines.Add($"点靠右按钮   配置 DockSide={dockedRight.DockSide}（应为 Right）");

            for (var i = 0; i < 6; i++)
            {
                Thread.Sleep(80);
                win.GuardTickForTest();
            }

            lines.Add($"  靠右后窗口 {win.BarWindowRectForTest()}");

            // 锁按钮：走真实的 WM_LBUTTONDOWN/UP（窗口过程里那条原生分支），
            // 光调 ToggleLock 是测不到「点了没反应」这个毛病的。
            // 锁图标是悬停才露的，所以得先把字幕条挪到光标底下。
            win.SetFloatForTest(true);
            Settle(200);
            win.HoverLockForTest();

            var (lx, ly) = win.BarLockCenterForTest();
            var before = win.FloatStateForTest();
            win.PostBarClickForTest(lx, ly);
            Settle(300);
            var after = win.FloatStateForTest();

            lines.Add($"原生点锁     at({lx},{ly}) locked: {before.Split("locked=")[1][..5]} → " +
                      $"{after.Split("locked=")[1][..5]}（应 False→True）");

            win.PostBarClickForTest(lx, ly);
            Settle(300);
            lines.Add($"再点一次     {win.FloatStateForTest()}");

            win.Close();
        }
        catch (Exception ex)
        {
            lines.Add($"floatsim failed: {ex}");
        }

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "floatsim.txt"),
            string.Join(Environment.NewLine, lines));
    }

    private const string SampleAss = """
        [Script Info]
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,20,&H00FFFFFF,&H000088FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:01.00,0:00:04.00,Default,,0,0,0,,{\k20}Hello {\k30}world, {\i1}again{\i0}
        Dialogue: 0,0:00:05.00,0:00:08.00,Default,,0,0,0,,{\an8}{\c&H00FF00&}TOP LINE
        Dialogue: 0,0:00:09.00,0:00:12.00,Default,,0,0,0,,{\fad(200,300)}{\b1}Bold fade{\b0} text
        """;

    /// <summary>生成一个 16bit 单声道正弦波 WAV，用于离线验证变速。</summary>
    private static void WriteSineWav(string path, int seconds, double frequency)
    {
        const int sampleRate = 44100;
        var samples = sampleRate * seconds;
        var dataSize = samples * 2;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var i = 0; i < samples; i++)
        {
            var value = Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            writer.Write((short)(value * 12000));
        }
    }

    private static string LogPath =>
        Path.Combine(Config.AppConfig.ConfigDirectory, "error.log");    private static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去也不影响主流程
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 未持有互斥体时忽略
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
