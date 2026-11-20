using System.Globalization;
using System.Text;

namespace LoafEar.Subs;

/// <summary>
/// ASS / SSA 解析器。只实现字幕条这个尺寸下真正看得见的东西：
/// 样式（字体色、粗体、斜体）、内联标签、卡拉OK逐字时间轴、\an 对齐、\fad 淡入淡出。
/// 绘图指令、旋转、缩放、定位等一律忽略。
/// </summary>
internal static class AssParser
{
    private sealed class AssStyle
    {
        public uint Primary = 0xFFFFFF;
        public uint Secondary = 0xFFFFFF;
        public bool Bold;
        public bool Italic;
        public int Alignment = 2;
    }

    private sealed class LexerState
    {
        public bool Bold;
        public bool Italic;
        public uint? Color;
        public int Alignment = 2;
        public double FadeIn;
        public double FadeOut;

        /// <summary>下一个文本片段的卡拉OK高亮时刻（相对句首毫秒），-1 表示整句同时显示。</summary>
        public double NextKaraokeMs = -1;
        public double KaraokeCursorMs;
        public bool KaraokePending;
        public bool InDrawing;
    }

    public static SubtitleTrack Parse(string text, string path)
    {
        var styles = new Dictionary<string, AssStyle>(StringComparer.OrdinalIgnoreCase);
        var cues = new List<Cue>();

        string section = string.Empty;
        string[] eventFormat = Array.Empty<string>();
        string[] styleFormat = Array.Empty<string>();

        foreach (var rawLine in SplitLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (section.StartsWith("v4") || section == "script info")
            {
                if (key == "format" && section.StartsWith("v4"))
                    styleFormat = SplitFields(value);
                else if (key == "style")
                    ParseStyle(value, styleFormat, styles);
                continue;
            }

            if (section != "events")
                continue;

            if (key == "format")
            {
                eventFormat = SplitFields(value);
                continue;
            }

            if (key is not ("dialogue" or "comment"))
                continue;

            // Comment 行不参与显示，但保留解析结构以防将来需要
            if (key == "comment")
                continue;

            var cue = ParseEvent(value, eventFormat, styles);
            if (cue is not null)
                cues.Add(cue);
        }

        return SubtitleTrack.Create(cues, path, "ASS");
    }

    private static void ParseStyle(string value, string[] format, Dictionary<string, AssStyle> styles)
    {
        if (format.Length == 0)
            format = new[]
            {
                "name", "fontname", "fontsize", "primarycolour", "secondarycolour",
                "outlinecolour", "backcolour", "bold", "italic",
            };

        var fields = SplitLimited(value, format.Length);
        var style = new AssStyle();

        for (var i = 0; i < format.Length && i < fields.Length; i++)
        {
            switch (format[i])
            {
                case "primarycolour":
                    style.Primary = ParseColour(fields[i]) ?? style.Primary;
                    break;
                case "secondarycolour":
                    style.Secondary = ParseColour(fields[i]) ?? style.Secondary;
                    break;
                case "bold":
                    style.Bold = IsTruthy(fields[i]);
                    break;
                case "italic":
                    style.Italic = IsTruthy(fields[i]);
                    break;
                case "alignment":
                    if (int.TryParse(fields[i], out var al) && al is >= 1 and <= 9)
                        style.Alignment = al;
                    break;
            }
        }

        var nameIndex = Array.IndexOf(format, "name");
        var name = nameIndex >= 0 && nameIndex < fields.Length ? fields[nameIndex] : "default";
        styles[name] = style;
    }

    private static Cue? ParseEvent(string value, string[] format, Dictionary<string, AssStyle> styles)
    {
        if (format.Length == 0)
            format = new[] { "layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text" };

        var fields = SplitLimited(value, format.Length);

        string Get(string name)
        {
            var index = Array.IndexOf(format, name);
            return index >= 0 && index < fields.Length ? fields[index] : string.Empty;
        }

        if (!TryParseAssTime(Get("start"), out var start) || !TryParseAssTime(Get("end"), out var end))
            return null;

        if (end <= start)
            end = start.Add(TimeSpan.FromSeconds(2));

        styles.TryGetValue(Get("style").Trim(), out var style);
        style ??= new AssStyle();

        var state = new LexerState
        {
            Bold = style.Bold,
            Italic = style.Italic,
            Alignment = style.Alignment,
        };

        var spans = new List<CueSpan>();
        var plain = new StringBuilder();

        ScanText(Get("text"), state, spans, plain);

        var body = plain.ToString().Trim();
        if (body.Length == 0)
            return null;

        var normalised = NormaliseSpans(spans);
        var (main, translation) = SplitBilingual(body);

        return new Cue
        {
            Start = start,
            End = end,
            Text = main,
            Translation = translation,
            Spans = normalised,
            Color = style.Primary,
            KaraokeColor = style.Secondary,
            Bold = style.Bold,
            Italic = style.Italic,
            Alignment = state.Alignment,
            FadeInMs = state.FadeIn,
            FadeOutMs = state.FadeOut,
        };
    }

    /// <summary>逐字符扫描覆盖标签，产出带样式的片段。</summary>
    private static void ScanText(string text, LexerState state, List<CueSpan> spans, StringBuilder plain)
    {
        var buffer = new StringBuilder();
        var bufferKaraoke = -1d;
        var bufferBold = state.Bold;
        var bufferItalic = state.Italic;
        uint? bufferColor = null;
        var activeKaraoke = -1d;

        void Flush()
        {
            if (buffer.Length == 0)
                return;

            spans.Add(new CueSpan
            {
                Text = buffer.ToString(),
                KaraokeAtMs = bufferKaraoke,
                Bold = bufferBold,
                Italic = bufferItalic,
                Color = bufferColor,
            });

            buffer.Clear();

            // 这一段唱完了，后面没有新 \k 的文本就算「已唱到当前累计位置」
            if (activeKaraoke >= 0)
                activeKaraoke = state.KaraokeCursorMs;
        }

        void Sync()
        {
            if (state.KaraokePending)
            {
                if (buffer.Length > 0)
                    Flush();

                activeKaraoke = state.NextKaraokeMs;
                state.KaraokePending = false;
            }

            if (buffer.Length > 0 &&
                (state.Bold != bufferBold || state.Italic != bufferItalic || state.Color != bufferColor))
            {
                Flush();
            }

            if (buffer.Length == 0)
            {
                bufferKaraoke = activeKaraoke;
                bufferBold = state.Bold;
                bufferItalic = state.Italic;
                bufferColor = state.Color;
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close < 0)
                    break;

                ApplyTags(text.Substring(i + 1, close - i - 1), state);
                i = close;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next is 'N' or 'n' or 'h')
                {
                    if (!state.InDrawing)
                    {
                        Sync();
                        buffer.Append(' ');
                        plain.Append(' ');
                    }

                    i++;
                    continue;
                }
            }

            if (state.InDrawing)
                continue;

            Sync();
            buffer.Append(c);
            plain.Append(c);
        }

        Flush();
    }

    private static void ApplyTags(string block, LexerState state)
    {
        var parts = block.Split('\\');

        foreach (var raw in parts)
        {
            var tag = raw.Trim();
            if (tag.Length == 0)
                continue;

            var name = tag;
            var arg = string.Empty;

            var split = tag.IndexOfAny(new[] { '(', '&' });
            if (split > 0)
            {
                name = tag[..split];
                arg = tag[split..];
            }
            else
            {
                // 形如 i1 / b0 / an5 / k30 这类「字母+数字」
                var digit = tag.IndexOfAny("0123456789".ToCharArray());
                if (digit > 0)
                {
                    name = tag[..digit];
                    arg = tag[digit..];
                }
            }

            name = name.ToLowerInvariant();

            switch (name)
            {
                case "i":
                    state.Italic = ParseFlag(arg, state.Italic);
                    break;

                case "b":
                    state.Bold = ParseFlag(arg, state.Bold);
                    break;

                case "c":
                case "1c":
                {
                    var colour = ParseColour(arg);
                    if (colour is not null)
                        state.Color = colour;
                    break;
                }

                case "k":
                case "kf":
                case "ko":
                case "kt":
                {
                    // \k 的单位是「厘秒」
                    if (double.TryParse(arg.Trim('(', ')'), NumberStyles.Any, CultureInfo.InvariantCulture, out var cs))
                    {
                        state.NextKaraokeMs = state.KaraokeCursorMs;
                        state.KaraokeCursorMs += cs * 10;
                        state.KaraokePending = true;
                    }

                    break;
                }

                case "an":
                    if (int.TryParse(arg, out var al) && al is >= 1 and <= 9)
                        state.Alignment = al;
                    break;

                case "a":
                    // 旧式 \a 对齐，映射到 \an
                    if (int.TryParse(arg, out var legacy))
                        state.Alignment = legacy switch
                        {
                            1 => 1, 2 => 2, 3 => 3,
                            5 => 7, 6 => 8, 7 => 9,
                            9 => 4, 10 => 5, 11 => 6,
                            _ => state.Alignment,
                        };
                    break;

                case "fad":
                    ParseFad(arg, state);
                    break;

                case "fade":
                    ParseFade(arg, state);
                    break;

                case "p":
                    state.InDrawing = int.TryParse(arg, out var mode) && mode > 0;
                    break;
            }
        }
    }

    private static void ParseFad(string arg, LexerState state)
    {
        var inner = arg.Trim('(', ')');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return;

        if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var fadeIn))
            state.FadeIn = Math.Clamp(fadeIn, 0, 5000);

        if (double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var fadeOut))
            state.FadeOut = Math.Clamp(fadeOut, 0, 5000);
    }

    private static void ParseFade(string arg, LexerState state)
    {
        // \fade(a1,a2,a3,t1,t2,t3,t4)
        var inner = arg.Trim('(', ')');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 7)
            return;

        if (double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var t1) &&
            double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var t2))
            state.FadeIn = Math.Clamp(t2 - t1, 0, 5000);

        if (double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var t3) &&
            double.TryParse(parts[6], NumberStyles.Any, CultureInfo.InvariantCulture, out var t4))
            state.FadeOut = Math.Clamp(t4 - t3, 0, 5000);
    }

    private static List<CueSpan>? NormaliseSpans(List<CueSpan> spans)
    {
        if (spans.Count == 0)
            return null;

        // 把相邻同属性片段合并，减少渲染时的 Run 数量
        var merged = new List<CueSpan>(spans.Count);

        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
                continue;

            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.KaraokeAtMs == span.KaraokeAtMs &&
                    last.Bold == span.Bold && last.Italic == span.Italic &&
                    last.Color == span.Color)
                {
                    merged[^1] = new CueSpan
                    {
                        Text = last.Text + span.Text,
                        KaraokeAtMs = last.KaraokeAtMs,
                        Bold = last.Bold,
                        Italic = last.Italic,
                        Color = last.Color,
                    };
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged.Count == 0 ? null : merged;
    }

    // ------------------------------------------------------------------ 工具

    private static (string Main, string Translation) SplitBilingual(string value)
    {
        var parts = value
            .Replace("\r", " ")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length <= 1)
            return (value.Trim(), "");

        var cjk = new List<string>();
        var latin = new List<string>();

        foreach (var line in parts)
        {
            if (HasCjk(line))
                cjk.Add(line);
            else
                latin.Add(line);
        }

        if (cjk.Count > 0 && latin.Count > 0)
            return (string.Join(" ", latin).Trim(), string.Join(" ", cjk).Trim());

        return (string.Join(" ", parts).Trim(), "");
    }

    private static bool HasCjk(string value)
    {
        foreach (var c in value)
        {
            if (c is >= '\u4E00' and <= '\u9FFF')
                return true;
        }

        return false;
    }

    private static bool ParseFlag(string arg, bool fallback)
    {
        var text = arg.Trim('(', ')');
        if (text.Length == 0)
            return true;

        return int.TryParse(text, out var n) ? n != 0 : fallback;
    }

    /// <summary>解析 &HBBGGRR&amp; / &HAABBGGRR&amp; / &HBBGGRR，返回 0xRRGGBB。</summary>
    private static uint? ParseColour(string arg)
    {
        var text = arg.Trim().Trim('(', ')').Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        text = text.TrimEnd('&').Trim();

        // 带 alpha 的 8 位形式，去掉高两位
        if (text.Length > 6)
            text = text[^6..];

        if (text.Length == 0 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bgr))
            return null;

        var b = (bgr >> 16) & 0xFF;
        var g = (bgr >> 8) & 0xFF;
        var r = bgr & 0xFF;

        return (r << 16) | (g << 8) | b;
    }

    private static bool IsTruthy(string value) => int.TryParse(value, out var n) && n != 0;

    private static bool TryParseAssTime(string value, out TimeSpan result)
    {
        result = default;
        var text = value.Trim();
        if (text.Length == 0)
            return false;

        var parts = text.Split(':');
        if (parts.Length != 3)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var hours) ||
            !double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2].Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            return false;

        result = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
        return result >= TimeSpan.Zero;
    }

    private static string[] SplitFields(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries)
             .Select(f => f.ToLowerInvariant())
             .ToArray();

    /// <summary>按字段数切分，最后一个字段保留其中的逗号（Text 字段常有逗号）。</summary>
    private static string[] SplitLimited(string value, int count)
    {
        if (count <= 1)
            return new[] { value };

        var parts = new string[count];
        var index = 0;
        var start = 0;

        for (var i = 0; i < value.Length && index < count - 1; i++)
        {
            if (value[i] != ',')
                continue;

            parts[index++] = value[start..i].Trim();
            start = i + 1;
        }

        parts[index] = start < value.Length ? value[start..] : string.Empty;
        return parts;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
