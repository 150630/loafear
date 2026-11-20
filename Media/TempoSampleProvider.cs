using NAudio.Wave;
using SoundTouch;

namespace LoafEar.Media;

/// <summary>
/// 变速不变调。用 SoundTouch 做时间伸缩（tempo），音调保持不变。
///
/// 关键点：<see cref="Position"/> 不用底层 reader 的读取位置，而是按“已输出的帧数 × tempo”累计
/// 反推源时间。因为 SoundTouch 会预读一大段输入，reader 的位置会明显超前于耳朵听到的位置，
/// 直接拿来当字幕时钟会整段提前。
/// </summary>
public sealed class TempoSampleProvider : ISampleProvider
{
    private const int FeedFrames = 2048;

    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _st = new();
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly float[] _feed;

    private float[] _ready = new float[16384];
    private int _readyPos;
    private int _readyLen;

    // 相对 _baseSeconds 的源进度（帧），只在 tempo 恒定段内按 tempo 累加
    private double _sourceFramesOut;
    private double _baseSeconds;
    private bool _sourceEnded;
    private float _tempo = 1f;

    public TempoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = Math.Max(8000, source.WaveFormat.SampleRate);
        _feed = new float[FeedFrames * _channels];

        _st.Channels = _channels;
        _st.SampleRate = _sampleRate;
        _st.SetSetting(SettingId.UseQuickSeek, 1);
        _st.Tempo = 1f;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>1.0 = 原速；0.5 = 放慢一半；2.0 = 加快一倍。</summary>
    public float Tempo
    {
        get => _tempo;
        set
        {
            // 下限放到 0.35：0.4 倍是精听常用的「超慢速」，卡在 0.5 会让这一档跟 0.5 完全一样
            var target = Math.Clamp(value, 0.35f, 2.0f);
            if (Math.Abs(target - _tempo) < 0.001f)
                return;

            _tempo = target;
            _st.Tempo = target;
        }
    }

    /// <summary>当前实际播放到的源时间。</summary>
    public TimeSpan Position => TimeSpan.FromSeconds(_baseSeconds + _sourceFramesOut / _sampleRate);

    /// <summary>跳转后必须调用，重置内部进度与缓冲。</summary>
    public void Reset(TimeSpan position)
    {
        _st.Clear();
        _baseSeconds = Math.Max(0, position.TotalSeconds);
        _sourceFramesOut = 0;
        _readyPos = 0;
        _readyLen = 0;
        _sourceEnded = false;
    }

    /// <summary>Read 被调用的次数，用于诊断播放线程是否真的在跑。</summary>
    public long ReadCalls { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0)
            return 0;

        ReadCalls++;

        var wantFrames = count / _channels;
        var written = 0;

        while (written < wantFrames)
        {
            // 先把上一次解码出来的余量搬走
            if (_readyLen > 0)
            {
                var take = Math.Min(_readyLen, (wantFrames - written) * _channels);
                Array.Copy(_ready, _readyPos, buffer, offset + written * _channels, take);
                _readyPos += take;
                _readyLen -= take;
                written += take / _channels;
                continue;
            }

            // SoundTouch 是块处理：前 ~140ms 的输入只会被吞进缓冲区而不产出。
            // 所以这里不能“没产出就返回 0”，否则上层会当成流结束。继续喂，直到真结束。
            var produced = Pump();
            if (!produced && _sourceEnded)
                break;
        }

        return written * _channels;
    }

    /// <summary>读一段源、过一遍 SoundTouch、把结果放进 _ready。返回是否取出样本。</summary>
    private bool Pump()
    {
        if (!_sourceEnded)
        {
            var frames = _source.Read(_feed, 0, _feed.Length) / _channels;

            if (frames > 0)
            {
                _st.PutSamples(_feed.AsSpan(0, frames * _channels), frames);
            }
            else
            {
                _sourceEnded = true;
                _st.Flush();
            }
        }

        var maxFrames = _ready.Length / _channels;
        var gotFrames = _st.ReceiveSamples(_ready.AsSpan(0, maxFrames * _channels), maxFrames);

        _readyPos = 0;
        _readyLen = gotFrames * _channels;

        // 输出帧数换算回源时间：tempo=0.5 时输出 2 帧才走过 1 帧源
        _sourceFramesOut += gotFrames * (double)_tempo;

        return gotFrames > 0;
    }
}
