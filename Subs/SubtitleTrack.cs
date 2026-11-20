namespace LoafEar.Subs;

public sealed class SubtitleTrack
{
    private readonly List<Cue> _cues = new();

    public IReadOnlyList<Cue> Cues => _cues;
    public int Count => _cues.Count;
    public string SourcePath { get; private init; } = "";
    public string Format { get; private init; } = "";

    public static SubtitleTrack Empty(string path = "", string format = "") =>
        new() { SourcePath = path, Format = format };

    internal static SubtitleTrack Create(IEnumerable<Cue> cues, string path, string format)
    {
        var track = new SubtitleTrack { SourcePath = path, Format = format };
        track._cues.AddRange(cues.OrderBy(c => c.Start));
        return track;
    }

    /// <summary>
    /// 二分查找：返回 Start &lt;= position 的最后一句的索引；若早于第一句则返回 -1。
    /// 注意：不校验 End，句间空隙仍停留在上一句，避免字幕闪烁消失。
    /// </summary>
    public int IndexAt(TimeSpan position)
    {
        var lo = 0;
        var hi = _cues.Count - 1;
        var result = -1;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (_cues[mid].Start <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    public Cue? At(TimeSpan position)
    {
        var index = IndexAt(position);
        return index >= 0 ? _cues[index] : null;
    }

    public TimeSpan? StartOf(int index) =>
        index >= 0 && index < _cues.Count ? _cues[index].Start : null;

    /// <summary>整体平移所有句子（用于字幕偏移校正）。</summary>
    public SubtitleTrack Shift(TimeSpan delta)
    {
        if (delta == TimeSpan.Zero || _cues.Count == 0)
            return this;

        var track = new SubtitleTrack { SourcePath = SourcePath, Format = Format };
        track._cues.AddRange(_cues.Select(c => c.Shift(delta)));
        return track;
    }
}
