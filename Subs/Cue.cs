namespace LoafEar.Subs;

/// <summary>ASS 里的一个文本片段（同一时刻共享同一套样式）。</summary>
public sealed class CueSpan
{
    public string Text { get; init; } = "";

    /// <summary>卡拉OK高亮时刻，相对句首的毫秒数；-1 表示整句同时显示。</summary>
    public double KaraokeAtMs { get; init; } = -1;

    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>0xRRGGBB；null 表示沿用句子的主色。</summary>
    public uint? Color { get; init; }
}

public sealed class Cue
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }

    /// <summary>主语言文本（英文）。</summary>
    public string Text { get; init; } = "";

    /// <summary>译文（中文），没有则为空串。</summary>
    public string Translation { get; init; } = "";

    // ---- ASS 特效 ----
    public IReadOnlyList<CueSpan>? Spans { get; init; }
    public uint? Color { get; init; }

    /// <summary>卡拉OK未唱到时的颜色（ASS 的 SecondaryColour）。</summary>
    public uint? KaraokeColor { get; init; }

    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>ASS \an 编号 1~9，默认 2（底部居中）。</summary>
    public int Alignment { get; init; } = 2;

    public double FadeInMs { get; init; }
    public double FadeOutMs { get; init; }

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>是否带逐字时间轴（卡拉OK）。</summary>
    public bool HasKaraoke
    {
        get
        {
            if (Spans is null || Spans.Count == 0)
                return false;

            for (var i = 0; i < Spans.Count; i++)
            {
                if (Spans[i].KaraokeAtMs >= 0)
                    return true;
            }

            return false;
        }
    }

    /// <summary>复制一份并整体平移（用于字幕偏移）。</summary>
    public Cue Shift(TimeSpan delta)
    {
        var start = Start + delta;
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;

        var end = End + delta;
        if (end < start)
            end = start;

        return new Cue
        {
            Start = start,
            End = end,
            Text = Text,
            Translation = Translation,
            Spans = Spans,
            Color = Color,
            KaraokeColor = KaraokeColor,
            Bold = Bold,
            Italic = Italic,
            Alignment = Alignment,
            FadeInMs = FadeInMs,
            FadeOutMs = FadeOutMs,
        };
    }
}
