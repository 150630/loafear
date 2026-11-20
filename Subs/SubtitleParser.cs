using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LoafEar.Subs;

public static class SubtitleParser
{
    private static readonly Regex AngleTagRegex = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex BraceTagRegex = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex LrcTimeRegex = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex LrcMetaRegex = new(@"^\[(ti|ar|al|by|offset|length|re|ve|kana):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SubtitleExtensions = { ".srt", ".ass", ".ssa", ".lrc", ".vtt" };

    public static SubtitleTrack Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return SubtitleTrack.Empty(path ?? "");

        string text;
        try
        {
            text = ReadTextSmart(path);
        }
        catch (Exception ex)
        {
            throw new IOException($"读取字幕失败：{ex.Message}", ex);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".lrc" => ParseLrc(text, path),
            ".ass" or ".ssa" => AssParser.Parse(text, path),
            ".srt" => ParseSrt(text, path, "SRT"),
            ".vtt" => ParseSrt(text, path, "VTT"),
            _ => ParseAuto(text, path),
        };
    }

    /// <summary>在音频同目录下找同名 / 同前缀的字幕文件。</summary>
    public static string? FindSibling(string audioPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var stem = Path.GetFileNameWithoutExtension(audioPath);

            foreach (var ext in SubtitleExtensions)
            {
                var candidate = Path.Combine(dir, stem + ext);
                if (File.Exists(candidate))
                    return candidate;
            }

            foreach (var file in Directory.EnumerateFiles(dir, stem + "*"))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (SubtitleExtensions.Contains(ext))
                    return file;
            }
        }
        catch
        {
            // 目录不可读等情况直接放弃自动匹配
        }

        return null;
    }

    private static SubtitleTrack ParseAuto(string text, string path)
    {
        var head = text.Length > 4000 ? text[..4000] : text;

        if (head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("[V4 Styles]", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            return AssParser.Parse(text, path);
        }

        if (head.Contains('[') && LrcTimeRegex.IsMatch(head))
            return ParseLrc(text, path);

        return ParseSrt(text, path, "SRT");
    }

    private static SubtitleTrack ParseSrt(string text, string path, string format)
    {
        var cues = new List<Cue>();
        var lines = SplitLines(text);
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                i++;
                continue;
            }

            if (line.Contains("-->", StringComparison.Ordinal))
            {
                var halves = line.Split("-->", 2, StringSplitOptions.TrimEntries);
                if (halves.Length == 2 &&
                    TryParseClock(halves[0], out var start) &&
                    TryParseClock(FirstToken(halves[1]), out var end))
                {
                    i++;
                    var buffer = new List<string>();
                    while (i < lines.Length && lines[i].Trim().Length > 0)
                    {
                        buffer.Add(lines[i].Trim());
                        i++;
                    }

                    var (main, sub) = SplitBilingual(buffer);
                    if (main.Length > 0 || sub.Length > 0)
                    {
                        if (end <= start)
                            end = start.Add(TimeSpan.FromSeconds(2));

                        cues.Add(new Cue { Start = start, End = end, Text = main, Translation = sub });
                    }

                    continue;
                }
            }

            i++;
        }

        return SubtitleTrack.Create(cues, path, format);
    }

    private static SubtitleTrack ParseLrc(string text, string path)
    {
        var stamped = new List<(TimeSpan Time, string Content)>();
        var lines = SplitLines(text);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (LrcMetaRegex.IsMatch(line))
                continue;

            var matches = LrcTimeRegex.Matches(line);
            if (matches.Count == 0)
                continue;

            var content = LrcTimeRegex.Replace(line, string.Empty);
            content = CleanInline(content);
            if (content.Length == 0)
                continue;

            foreach (Match match in matches)
            {
                if (TryParseLrcStamp(match, out var stamp))
                    stamped.Add((stamp, content));
            }
        }

        // 同一时间戳的多行合并（译文常常是独立一行、与原文同时间戳）
        var groups = stamped
            .GroupBy(x => x.Time)
            .OrderBy(g => g.Key)
            .Select(g => (Time: g.Key, Lines: g.Select(x => x.Content).ToList()))
            .ToList();

        var cues = new List<Cue>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var current = groups[i];
            var end = i + 1 < groups.Count
                ? groups[i + 1].Time
                : current.Time.Add(TimeSpan.FromSeconds(6));

            if (end <= current.Time)
                end = current.Time.Add(TimeSpan.FromSeconds(1));

            var (main, sub) = SplitBilingual(current.Lines);
            if (main.Length == 0 && sub.Length == 0)
                continue;

            cues.Add(new Cue { Start = current.Time, End = end, Text = main, Translation = sub });
        }

        return SubtitleTrack.Create(cues, path, "LRC");
    }

    private static (string Main, string Translation) SplitBilingual(IEnumerable<string> rawLines)
    {
        var cleaned = rawLines
            .Select(CleanInline)
            .Where(l => l.Length > 0)
            .ToList();

        if (cleaned.Count == 0)
            return ("", "");

        if (cleaned.Count == 1)
            return (cleaned[0], "");

        var cjk = new List<string>();
        var latin = new List<string>();

        foreach (var line in cleaned)
        {
            if (HasCjk(line))
                cjk.Add(line);
            else
                latin.Add(line);
        }

        if (cjk.Count > 0 && latin.Count > 0)
            return (string.Join(" ", latin).Trim(), string.Join(" ", cjk).Trim());

        return (string.Join(" ", cleaned).Trim(), "");
    }

    private static string CleanInline(string value)
    {
        var result = AngleTagRegex.Replace(value, string.Empty);
        result = BraceTagRegex.Replace(result, string.Empty);
        result = result.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);
        return result.Trim();
    }

    private static bool HasCjk(string value)
    {
        foreach (var c in value)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
                return true;
        }

        return false;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string FirstToken(string value)
    {
        var trimmed = value.Trim();
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static bool TryParseClock(string value, out TimeSpan result)
    {
        result = default;
        var text = value.Trim();
        if (text.Length == 0)
            return false;

        var parts = text.Split(':');
        if (parts.Length is not (2 or 3))
            return false;

        var digits = parts.Select(p => p.Trim().Replace(',', '.')).ToArray();
        if (!double.TryParse(digits[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            return false;

        double minutes = 0;
        double hours = 0;

        if (!double.TryParse(digits[^2], NumberStyles.Any, CultureInfo.InvariantCulture, out minutes))
            return false;

        if (parts.Length == 3 &&
            !double.TryParse(digits[0], NumberStyles.Any, CultureInfo.InvariantCulture, out hours))
            return false;

        var total = hours * 3600 + minutes * 60 + seconds;
        if (total < 0 || total > 86400 * 24)
            return false;

        result = TimeSpan.FromSeconds(total);
        return true;
    }

    private static bool TryParseLrcStamp(Match match, out TimeSpan result)
    {
        result = default;
        if (!int.TryParse(match.Groups[1].Value, out var minutes))
            return false;
        if (!int.TryParse(match.Groups[2].Value, out var seconds))
            return false;

        var fractionText = match.Groups[3].Success ? match.Groups[3].Value : "0";
        if (!double.TryParse("0." + fractionText, NumberStyles.Any, CultureInfo.InvariantCulture, out var fraction))
            fraction = 0;

        result = TimeSpan.FromSeconds(minutes * 60 + seconds + fraction);
        return true;
    }

    private static string ReadTextSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
            return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            var strict = new UTF8Encoding(false, true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 非 UTF-8（常见为 GBK）时用 Latin1 兜底，至少不抛异常
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
