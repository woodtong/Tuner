using Windows.Media.Control;

namespace Tuner.Core.Media;

/// <summary>
/// 系统媒体会话（SMTC）跟踪器：提供"进程名 → 是否正在播放"查询。
/// 事件驱动：订阅 SessionsChanged / 每会话 PlaybackInfoChanged，状态变化即时生效
/// （另以 2s 低频轮询兜底防漏）。PlaybackStatus 是应用自报的播放事实
/// （暂停不是注销会话，而是状态翻转），是区分"歌间静音"与"真正停止"的权威信号。
/// AUMID 与进程名用"互相包含 + 内置别名表"启发式匹配，未命中时调用方回退到峰值判定。
/// </summary>
public sealed class MediaSessionTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly object _subscribeGate = new();
    private Dictionary<string, string> _statusByAumid = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _eventsAttached;

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
        // 挂接管理器与事件（失败则重试）
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var manager = GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().AsTask().GetAwaiter().GetResult();
                lock (_gate)
                    _manager = manager;
                manager.SessionsChanged += OnSessionsChanged;
                Refresh(manager);
                _eventsAttached = true;
                break;
            }
            catch
            {
                // WinRT 未就绪等瞬时问题：重试
            }
            ct.WaitHandle.WaitOne(1000);
        }

        // 低频兜底轮询（事件偶发丢失时自愈）
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_eventsAttached)
                    Refresh(GetManager());
            }
            catch
            {
                // 忽略，下个周期重试
            }
            ct.WaitHandle.WaitOne(2000);
        }
    }

    private GlobalSystemMediaTransportControlsSessionManager? GetManager()
    {
        lock (_gate)
            return _manager;
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
        => Refresh(sender);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        // 播放状态变化：立即刷新（这是"暂停/停止立即恢复"低延时的关键路径）
        var manager = GetManager();
        if (manager is not null)
            Refresh(manager);
    }

    private void Refresh(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = new List<GlobalSystemMediaTransportControlsSession>();

        try
        {
            foreach (var s in manager.GetSessions())
            {
                current.Add(s);
                var aumid = s.SourceAppUserModelId ?? "";
                if (aumid.Length > 0)
                    dict[aumid] = s.GetPlaybackInfo().PlaybackStatus.ToString();
            }
        }
        catch
        {
            return; // 枚举失败：保留旧状态
        }

        lock (_subscribeGate)
        {
            // 新会话订阅播放状态事件；消失的会话退订
            foreach (var s in current)
                if (!_subscribed.Contains(s))
                {
                    s.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _subscribed.Add(s);
                }
            foreach (var s in _subscribed.ToList())
                if (!current.Contains(s))
                {
                    s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _subscribed.Remove(s);
                }
        }

        lock (_gate)
            _statusByAumid = dict;
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
        thread?.Join(2000);

        lock (_subscribeGate)
        {
            foreach (var s in _subscribed)
                s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _subscribed.Clear();
        }
        var manager = GetManager();
        if (manager is not null)
            manager.SessionsChanged -= OnSessionsChanged;

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _thread = null;
        }
    }
}
