using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LoafEar.Subs;

/// <summary>
/// 把改过的台词写回原字幕文件。
///
/// 只改目标那句的原文，其余行、时间码、编号、样式全部按原样保留 ——
/// 用「重新生成整份字幕」的做法会把翻译行、ASS 样式、卡拉 OK 标签全部洗掉，
/// 那就不是「替换原字幕」而是「覆盖字幕」了。
/// </summary>
public static class SubtitleEditor
{
    /// <summary>改一句。返回是否真的写盘了，失败原因放在 message。</summary>
    public static bool ReplaceCueText(
        string path, TimeSpan start, string oldText, string newText, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            message = "找不到原字幕文件";
            return false;
        }

        if (string.IsNullOrWhiteSpace(newText))
        {
            message = "台词不能为空";
            return false;
        }

        string text;
        Encoding? encoding;

        try
        {
            (text, encoding) = ReadTextWithEncoding(path);
        }
        catch (Exception ex)
        {
            message = $"读取失败：{ex.Message}";
            return false;
        }

        if (encoding is null)
        {
            // 解不出来就别写：按 Latin1 硬读再按 Latin1 写回，中文会整片变成问号
            message = "字幕不是 UTF-8 / UTF-16 编码，改写会导致乱码，已跳过";
            return false;
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        var ext = Path.GetExtension(path).ToLowerInvariant();

        var changed = ext switch
        {
            ".ass" or ".ssa" => ReplaceAssLine(lines, start, oldText, newText),
            ".lrc" => ReplaceLrcLine(lines, oldText, newText),
            _ => ReplaceBlockLine(lines, oldText, newText),
        };

        if (!changed)
        {
            message = "没在字幕文件里定位到这一句";
            return false;
        }

        try
        {
            // 先备份一份，改坏了还能捡回来
            var backup = path + ".bak";
            if (!File.Exists(backup))
                File.WriteAllBytes(backup, File.ReadAllBytes(path));

            File.WriteAllText(path, string.Join(newline, lines), encoding);
            message = "已保存";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入失败：{ex.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------ 各格式

    /// <summary>SRT / VTT：在整份文件里找内容等于原句的那一行，换掉。</summary>
    private static bool ReplaceBlockLine(
        List<string> lines, string oldText, string newText)
    {
        var target = Normalize(oldText);
        if (target.Length == 0)
            return false;

        var replacement = newText.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (replacement.Count == 0)
            return false;

        // 第一种：整句就在某一行里
        for (var i = 0; i < lines.Count; i++)
        {
            if (Normalize(lines[i]) != target)
                continue;

            lines.RemoveAt(i);
            lines.InsertRange(i, replacement);
            return true;
        }

        // 第二种：一句话在文件里被折成了好几行（中文字幕尤其常见，英文长句也有）。
        // 解析时会把这些行用空格拼成一句，所以这里也照同样方式拼回去再比 ——
        // 不这么做的话，界面上明明显示的是一整句，写回时永远"定位不到这一句"。
        const int MaxWrappedLines = 6;

        for (var i = 0; i < lines.Count; i++)
        {
            if (IsStructureLine(lines[i]))
                continue;

            var joined = new StringBuilder();
            var count = 0;

            for (var j = i; j < lines.Count && count < MaxWrappedLines; j++)
            {
                if (IsStructureLine(lines[j]))
                    break;

                if (joined.Length > 0)
                    joined.Append(' ');

                joined.Append(lines[j].Trim());
                count++;

                if (Normalize(joined.ToString()) == target)
                {
                    lines.RemoveRange(i, count);
                    lines.InsertRange(i, replacement);
                    return true;
                }

                // 已经比原句更长了，继续往后拼只会更不像
                if (Normalize(joined.ToString()).Length > target.Length)
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// 这行是不是字幕结构（空行、SRT 序号、时间码）而不是台词。
    /// 折行合并只能在这些边界之内进行。
    /// </summary>
    private static bool IsStructureLine(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length == 0)
            return true;

        if (trimmed.Contains("-->", StringComparison.Ordinal))
            return true;

        var allDigits = true;

        foreach (var c in trimmed)
        {
            if (!char.IsDigit(c))
            {
                allDigits = false;
                break;
            }
        }

        return allDigits;
    }

    /// <summary>LRC：保留行首的时间戳，只换后面的歌词。</summary>
    private static bool ReplaceLrcLine(List<string> lines, string oldText, string newText)
    {
        var target = Normalize(oldText);
        if (target.Length == 0)
            return false;

        for (var i = 0; i < lines.Count; i++)
        {
            var stamps = Regex.Matches(lines[i], @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
            if (stamps.Count == 0)
                continue;

            // 时间戳可能一行多个（同一句挂多个时间），把它们原样留着
            var prefix = string.Concat(stamps.Select(m => m.Value));

            if (Normalize(lines[i][prefix.Length..]) != target)
                continue;

            lines[i] = prefix + newText.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return true;
        }

        return false;
    }

    /// <summary>ASS / SSA：改 Dialogue 行里第 10 个逗号之后的那段文本。</summary>
    private static bool ReplaceAssLine(
        List<string> lines, TimeSpan start, string oldText, string newText)
    {
        var target = Normalize(oldText);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                continue;

            var body = trimmed["Dialogue:".Length..];
            var fields = SplitAssFields(body, 9);
            if (fields is null)
                continue;

            // Format 行规定前九个字段是 Layer..Effect，文本在最后
            if (Normalize(StripAssTags(fields[^1])) != target)
                continue;

            lines[i] = line[..(line.Length - trimmed.Length)]
                       + "Dialogue:" + string.Join(',', fields[..^1]) + "," + newText;
            return true;
        }

        return false;
    }

    /// <summary>按前 n 个逗号切开，剩下的整体作为最后一段（ASS 文本里允许出现逗号）。</summary>
    private static string[]? SplitAssFields(string body, int count)
    {
        var fields = new List<string>(count + 1);
        var start = 0;

        for (var i = 0; i < count; i++)
        {
            var comma = body.IndexOf(',', start);
            if (comma < 0)
                return null;

            fields.Add(body[start..comma]);
            start = comma + 1;
        }

        fields.Add(body[start..]);
        return fields.ToArray();
    }

    private static string StripAssTags(string value) =>
        Regex.Replace(Regex.Replace(value, @"\{[^}]*\}", string.Empty), @"\\N|\\n", " ");

    // ------------------------------------------------------------------ 工具

    private static string Normalize(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    /// <summary>按 BOM 判断编码，避免把 GBK 文件写成 UTF-8 之后整份乱码。解不出来返回 null。</summary>
    private static (string Text, Encoding? Encoding) ReadTextWithEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
                Encoding.BigEndianUnicode);

        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            // 只有装了代码页提供程序才解得开 GBK；没装就返回 null，让调用方拒绝写入。
            foreach (var name in new[] { "GB18030", "GBK" })
            {
                try
                {
                    var gbk = Encoding.GetEncoding(name);
                    return (gbk.GetString(bytes), gbk);
                }
                catch
                {
                    // 换下一个候选
                }
            }

            return (string.Empty, null);
        }
    }
}
