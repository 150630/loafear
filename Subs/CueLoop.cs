namespace LoafEar.Subs;

/// <summary>单句循环每拍要给界面的指示。</summary>
public enum CueLoopStep
{
    /// <summary>什么都不用做。</summary>
    None,

    /// <summary>要把播放头拉回 <see cref="CueLoop.Index"/> 那句的开头。</summary>
    Rewind,
}

/// <summary>
/// 单句循环 + 上下句跳转的决策核心。和 WPF、和播放器都没有关系，
/// 这样离屏回归（<c>--loopsim</c>）跑的就是界面用的同一套逻辑，不是另写一份。
/// </summary>
public sealed class CueLoop
{
    /// <summary>「已播过这么久，按上一句先回到本句开头」的门槛。</summary>
    public static readonly TimeSpan RewindWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>单句循环开关。</summary>
    public bool Enabled { get; set; }

    /// <summary>正在循环的那一句；-1 表示还没定下来。</summary>
    public int Index { get; private set; } = -1;

    public void Reset() => Index = -1;

    /// <summary>用户明确跳到某一句：循环目标跟着走。</summary>
    public void Jump(int target) => Index = Enabled ? target : -1;

    /// <summary>
    /// 每拍调一次。<paramref name="index"/> 是「按当前位置算出来的第几句」。
    /// </summary>
    public CueLoopStep OnTick(int index)
    {
        if (!Enabled || index < 0)
        {
            Index = -1;
            return CueLoopStep.None;
        }

        if (Index < 0)
        {
            Index = index;
            return CueLoopStep.None;
        }

        if (index == Index)
            return CueLoopStep.None;

        // 往前走了（= 这一句念完了），拉回去
        if (index > Index)
            return CueLoopStep.Rewind;

        // 往回退：用户自己拖回去的，循环目标跟着走
        Index = index;
        return CueLoopStep.None;
    }

    /// <summary>
    /// 「上一句 / 下一句」该跳到第几句。
    ///
    /// <paramref name="restartFirst"/> 为 true 时，已经播过 <see cref="RewindWindow"/> 的情况下
    /// 按「上一句」先回到本句开头 —— 顺序播放时这比直接退一句更常用。
    /// 但单句循环开着的时候不能这么干：循环本来就会一直重来，再「先回本句开头」等于按了没反应，
    /// 用户那边看到的就是「有时候退一句、有时候不动」。
    /// </summary>
    public static int ResolveJump(int index, TimeSpan playedInCue, int delta, int count, bool restartFirst)
    {
        if (count <= 0)
            return -1;

        if (restartFirst && delta < 0 && index >= 0 && playedInCue > RewindWindow)
            delta = 0;

        return Math.Clamp(index + delta, 0, count - 1);
    }
}
