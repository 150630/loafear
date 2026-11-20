using NAudio.Wave;

namespace LoafEar.Media;

/// <summary>
/// 本地音频播放器。字幕时钟直接读取播放位置，不做计时累加 —— 这是字幕不漂移的前提。
/// 变速走 SoundTouch 时间伸缩，音调不变，位置仍然以「源时间」返回，字幕不用做任何换算。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    /// <summary>
    /// 输出缓冲带来的固有延迟（毫秒）。已写入声卡但还没播出来的这部分声音，
    /// 在字幕时钟里必须扣掉，否则字幕会比声音早一截。
    /// </summary>
    private const double OutputLatencyMs = 120;

    private AudioFileReader? _reader;
    private TempoSampleProvider? _tempo;
    private IWavePlayer? _output;
    private bool _disposed;
    private double _speed = 1.0;
    private double _volume = 0.8;

    /// <summary>
    /// 最近一次定位到的位置。<see cref="Position"/> 永远不报得比它更早 —— 见 getter 里的注释。
    /// </summary>
    private TimeSpan _seekFloor = TimeSpan.Zero;

    public event EventHandler? PlaybackEnded;
    public event EventHandler? StateChanged;

    public string FilePath { get; private set; } = string.Empty;
    public bool IsLoaded => _reader is not null;

    public bool IsPlaying => _output is not null && _output.PlaybackState == PlaybackState.Playing;

    /// <summary>诊断用：输出设备的实际状态与 provider 被读取的次数。</summary>
    public string OutputState => _output?.PlaybackState.ToString() ?? "null";

    /// <summary>诊断用：可变速率层被上层拉取的次数。长时间不增长说明播放线程没跑。</summary>
    public long ReadCalls => _tempo?.ReadCalls ?? 0;

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <summary>播放倍速，0.4 ~ 2.0。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            var target = Math.Clamp(Math.Round(value, 2), 0.4, 2.0);
            if (Math.Abs(target - _speed) < 0.001)
                return;

            _speed = target;
            if (_tempo is not null)
                _tempo.Tempo = (float)target;
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_reader is null)
                return TimeSpan.Zero;

            try
            {
                var pos = _tempo?.Position ?? _reader.CurrentTime;

                // 扣掉输出缓冲：这段时间的声音已经交给声卡但还没放出来
                if (IsPlaying && pos > TimeSpan.Zero)
                    pos -= TimeSpan.FromMilliseconds(OutputLatencyMs);

                // 刚定位完那一小段：声卡缓冲还没重新灌满，上面这笔扣除会把位置算到定位点之前，
                // 于是「按位置算出来的第几句」会倒退一句 —— 单句循环会据此把这个上一句当成循环句，
                // 再一跳，表现就是按一下「上一句」退了两句还来回弹。
                // 定位之后真播放头只可能往后走，所以这里不许报得比定位点更早。
                if (pos < _seekFloor)
                    pos = _seekFloor;

                var total = _reader.TotalTime;
                if (total > TimeSpan.Zero && pos > total)
                    return total;
                return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
        set
        {
            if (_reader is null)
                return;

            var total = _reader.TotalTime;
            var target = value;
            if (target < TimeSpan.Zero)
                target = TimeSpan.Zero;
            if (total > TimeSpan.Zero && target > total)
                target = total;

            try
            {
                _reader.CurrentTime = target;
                _tempo?.Reset(target);
                _seekFloor = target;
            }
            catch
            {
                // 个别编码不支持定位，忽略
            }
        }
    }

    public double Volume
    {
        get => _reader?.Volume ?? _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            if (_reader is not null)
                _reader.Volume = (float)_volume;
        }
    }

    public void Load(string path, double volume = 0.8)
    {
        Stop();

        _volume = Math.Clamp(volume, 0.0, 1.0);

        _reader = new AudioFileReader(path);
        _reader.Volume = (float)_volume;

        // 统一走时间伸缩层，tempo=1 时基本是透传，切速时不用重建输出设备
        _tempo = new TempoSampleProvider(_reader) { Tempo = (float)_speed };
        _tempo.Reset(TimeSpan.Zero);
        _seekFloor = TimeSpan.Zero;

        _output = BuildOutput(_tempo);
        _output.PlaybackStopped += OnPlaybackStopped;

        FilePath = path;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 建输出设备。默认走 WinMM 的 WaveOutEvent，而不是 WASAPI。
    ///
    /// 实测（Win11 25H2 + 板载声卡）：WasapiOut 在共享/事件驱动模式下会出现
    /// 「PlaybackState 一直是 Playing，但播放线程只拉了一次数据就再没回调」的情况，
    /// 表现为进度条不动、完全没声音，也不抛异常、不发 PlaybackStopped。
    /// 这是 WASAPI 事件回调在某些驱动上不触发导致的，换 WinMM 就正常。
    ///
    /// 转 16bit PCM 而不是直接喂 float：WaveOut 对 IEEE Float 的支持依赖驱动，
    /// 16bit 是通用性最好的选择，听力练习的听感差异可以忽略。
    /// </summary>
    private static IWavePlayer BuildOutput(TempoSampleProvider tempo)
    {
        var provider = tempo.ToWaveProvider16();

        try
        {
            var wave = new WaveOutEvent
            {
                DesiredLatency = 120,
                NumberOfBuffers = 2,
            };

            wave.Init(provider);
            return wave;
        }
        catch
        {
            // WaveOut 不可用时退回 WASAPI（独占或特殊设备环境下）
            var wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);
            wasapi.Init(provider);
            return wasapi;
        }
    }

    public void Play()
    {
        if (_output is null)
            return;

        _output.Play();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_output is null)
            return;

        _output.Pause();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlay()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    /// <summary>播放到结尾后回到起点，便于反复精听。</summary>
    public void Rewind()
    {
        Position = TimeSpan.Zero;
    }

    public void Stop()
    {
        if (_output is not null)
        {
            try
            {
                _output.PlaybackStopped -= OnPlaybackStopped;
                _output.Stop();
                _output.Dispose();
            }
            catch
            {
                // 忽略释放期异常
            }

            _output = null;
        }

        if (_reader is not null)
        {
            try
            {
                _reader.Dispose();
            }
            catch
            {
                // 忽略
            }

            _reader = null;
        }

        _tempo = null;
        _seekFloor = TimeSpan.Zero;
        FilePath = string.Empty;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // 只在自然播放到结尾时通知；暂停、切歌不触发
        if (e.Exception is not null || _reader is null)
            return;

        var total = _reader.TotalTime;
        if (total <= TimeSpan.Zero)
            return;

        // 变速时 reader 的位置会超前于耳朵听到的位置，两边都看一眼更稳
        var reached = Position >= total - TimeSpan.FromMilliseconds(400)
                      || _reader.CurrentTime >= total - TimeSpan.FromMilliseconds(200);

        if (reached)
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
