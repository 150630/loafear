namespace LoafEar.Subs;

/// <summary>收藏页顶部的三个筛选。</summary>
public enum FavoriteFilter
{
    /// <summary>所有节目，按节目名 + 句序排。</summary>
    All,

    /// <summary>最近收的排前面。</summary>
    Recent,

    /// <summary>只看当前正在播的这一集（默认）。</summary>
    Episode,
}

/// <summary>
/// 收藏的一句。存的是「哪个音频文件 + 第几句」，外加文本快照 ——
/// 字幕文件被改过、甚至被删掉，列表里也还能认出这是哪一句。
/// </summary>
public sealed class FavoriteCue
{
    /// <summary>音频绝对路径。换目录、改名之后这条就对不上了，界面上要能看见。</summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>节目名（音频文件名去扩展名），列表右侧显示用。</summary>
    public string Episode { get; set; } = string.Empty;

    /// <summary>在字幕轨里的序号（0 起）。</summary>
    public int CueIndex { get; set; }

    /// <summary>句子的起止时间，带偏移之后的实际播放时间。</summary>
    public double StartMs { get; set; }

    public double EndMs { get; set; }

    public string Text { get; set; } = string.Empty;

    public string Translation { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>同一句的唯一标识：同一个音频的第几句。改字幕文件不影响它。</summary>
    public string Key => KeyOf(AudioPath, CueIndex);

    /// <summary>列表右侧那行「E01-1 · 03」。</summary>
    public string Meta => $"{Episode} · {CueIndex + 1:00}";

    /// <summary>路径比较一律忽略大小写，Windows 上同一个文件换个大小写不该算两条。</summary>
    public static string Normalize(string? path) =>
        (path ?? string.Empty).Replace('/', '\\').Trim().ToLowerInvariant();

    public static string KeyOf(string audioPath, int cueIndex) =>
        $"{Normalize(audioPath)}|{cueIndex}";
}
