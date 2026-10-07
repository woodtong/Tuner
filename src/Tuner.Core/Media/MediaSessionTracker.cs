using Windows.Media.Control;

namespace Tuner.Core.Media;

/// <summary>
/// 轮询系统媒体会话（SMTC），提供"进程名 → 是否正在播放"查询。
/// PlaybackStatus 是应用自报的播放事实（暂停不是注销会话，而是状态翻转），
/// 是区分"歌间静音"与"真正停止/暂停"的权威信号。
/// AUMID 与进程名的对应用"互相包含 + 内置别名表"启发式匹配，未命中时调用方回退到峰值判定。
/// </summary>
public sealed class MediaSessionTracker : IDisposable
{
    private readonly object _gate = new();
    private Dictionary<string, string> _statusByAumid = new(StringComparer.OrdinalIgnoreCase);
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    // 进程名 → SMTC AUMID 的已知别名（两侧小写比较），覆盖 AUMID 与进程名互不包含的常见应用
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["哔哩哔哩"] = new[] { "bilibili" },
        ["bilibili"] = new[] { "哔哩哔哩", "bilibili" },
        ["网易云音乐"] = new[] { "cloudmusic" },
        ["cloudmusic"] = new[] { "网易云音乐", "cloudmusic" },
        ["msedge"] = new[] { "microsoftedge" },
    };

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
                return;
            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Loop(_cts.Token)) { IsBackground = true, Name = "Tuner.MediaSessionTracker" };
            _thread.Start();
        }
    }

    private void Loop(CancellationToken ct)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                manager ??= GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().AsTask().GetAwaiter().GetResult();
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in manager.GetSessions())
                {
                    var aumid = s.SourceAppUserModelId ?? "";
                    if (aumid.Length == 0)
                        continue;
                    dict[aumid] = s.GetPlaybackInfo().PlaybackStatus.ToString();
                }
                lock (_gate)
                    _statusByAumid = dict;
            }
            catch
            {
                // WinRT 未就绪等瞬时问题：下个周期重试
            }
            ct.WaitHandle.WaitOne(500);
        }
    }

    /// <summary>
    /// 查询某进程名对应的媒体会话是否报告"播放中"。
    /// 返回 null 表示没有任何匹配的 SMTC 会话（调用方应回退到峰值/宽限判定）。
    /// </summary>
    public bool? IsPlaying(string processName)
    {
        lock (_gate)
        {
            var anyMatched = false;
            foreach (var (aumid, status) in _statusByAumid)
            {
                if (!Matches(processName, aumid))
                    continue;
                anyMatched = true;
                if (status == "Playing")
                    return true;
            }
            return anyMatched ? false : null;
        }
    }

    private static bool Matches(string processName, string aumid)
    {
        var p = processName.ToLowerInvariant();
        var a = aumid.ToLowerInvariant();
        if (a.Contains(p) || p.Contains(a))
            return true;
        if (Aliases.TryGetValue(p, out var aliases))
            foreach (var alias in aliases)
                if (a.Contains(alias.ToLowerInvariant()))
                    return true;
        return false;
    }

    /// <summary>诊断视图：全部会话的 AUMID → 播放状态。</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
            return new Dictionary<string, string>(_statusByAumid, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _cts?.Cancel();
        }
        thread?.Join(1000);
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _thread = null;
        }
    }
}
