using System.IO;

namespace LoafEar.Media;

/// <summary>听力目录里的一个节目：一段音频，外加能自动配对上的字幕。</summary>
public sealed class LibraryItem
{
    public required string AudioPath { get; init; }
    public string? SubtitlePath { get; init; }

    /// <summary>显示名：文件名去掉扩展名。</summary>
    public string Title => Path.GetFileNameWithoutExtension(AudioPath);

    public bool HasSubtitle => !string.IsNullOrEmpty(SubtitlePath);
}

/// <summary>
/// 扫描听力目录，把音频和字幕自动配对。
///
/// 配对规则（从强到弱）：
///   1. 同目录、同文件名、不同扩展名  → E01.mp3 + E01.srt
///   2. 同目录、字幕名以音频名开头    → E01.mp3 + E01.中英.srt
///   3. 同目录只有唯一一个字幕         → 兜底，避免整集没字幕
/// </summary>
public static class MediaLibrary
{
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".wav", ".flac", ".wma", ".ogg", ".opus",
    };

    private static readonly string[] SubtitleExtensions =
    {
        ".srt", ".ass", ".ssa", ".lrc", ".vtt",
    };

    /// <summary>扫目录（含子目录）。返回按文件名自然排序的节目列表。</summary>
    public static List<LibraryItem> Scan(string? directory)
    {
        var result = new List<LibraryItem>();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return result;

        string[] audioFiles;
        string[] subtitleFiles;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 3,
            };

            audioFiles = Directory.EnumerateFiles(directory, "*", options)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToArray();

            subtitleFiles = Directory.EnumerateFiles(directory, "*", options)
                .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToArray();
        }
        catch
        {
            return result;
        }

        // 字幕按目录分组，配对时只在本目录里找
        var subsByDir = subtitleFiles
            .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var audio in audioFiles)
        {
            var dir = Path.GetDirectoryName(audio) ?? string.Empty;
            subsByDir.TryGetValue(dir, out var candidates);

            result.Add(new LibraryItem
            {
                AudioPath = audio,
                SubtitlePath = MatchSubtitle(audio, candidates),
            });
        }

        return result
            .OrderBy(i => NaturalKey(i.AudioPath), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? MatchSubtitle(string audioPath, List<string>? candidates)
    {
        if (candidates is null || candidates.Count == 0)
            return null;

        var stem = Path.GetFileNameWithoutExtension(audioPath);
        var stemLength = stem.Length;

        // 规则 1：同名不同扩展
        var exact = candidates.FirstOrDefault(
            f => string.Equals(
                Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
            return exact;

        // 规则 2：字幕名以音频名开头（E01.mp3 → E01.中英.srt）
        var prefixed = candidates
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.Length > stemLength
                       && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f.Length)
            .FirstOrDefault();

        if (prefixed is not null)
            return prefixed;

        // 规则 3：目录里只有一个字幕时兜底
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// 自然排序键：把连续数字统一补零成定宽，让 E01 &lt; E02 &lt; E3 &lt; E10。
    ///
    /// 注意不能按「数字位数」优先，否则 E3（1 位）会排到 E01（2 位）前面。
    /// 定宽补零后逐字符比较就等价于按数值比较。
    /// </summary>
    private static string NaturalKey(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var sb = new System.Text.StringBuilder(name.Length + 8);
        var i = 0;

        while (i < name.Length)
        {
            if (!char.IsDigit(name[i]))
            {
                sb.Append(char.ToUpperInvariant(name[i]));
                i++;
                continue;
            }

            var start = i;
            while (i < name.Length && char.IsDigit(name[i]))
                i++;

            var digits = name.AsSpan(start, i - start);

            // 超长数字串不解析（避免溢出），原样保留
            if (digits.Length <= 9 && long.TryParse(digits, out var value))
                sb.Append(value.ToString("D9"));
            else
                sb.Append(digits);
        }

        return sb.ToString();
    }
}
