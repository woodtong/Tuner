namespace Tuner.Core;

/// <summary>
/// 进程内诊断事件中心：环形缓冲最近事件，供界面"事件日志"自查面板展示。
/// 事件可能在任意线程产生（引擎轮询线程 / COM 回调 / UI 线程），订阅方自行调度回 UI 线程。
/// 诊断的责任主体是程序与维护人员——这里只收"值得让用户看到"的关键事件（规则生效/解除、
/// 设备切换、配置问题、暂停/恢复），不追求完整审计。
/// </summary>
public static class Diag
{
    /// <summary>一条诊断事件（内存快照，不落盘）。</summary>
    public sealed record Entry(DateTime Time, string Category, string Message);

    private const int Capacity = 600;
    private static readonly object Gate = new();
    private static readonly Queue<Entry> Entries = new();

    /// <summary>有新事件写入（可能在任意线程触发）。</summary>
    public static event Action? Changed;

    public static void Record(string category, string message)
    {
        lock (Gate)
        {
            Entries.Enqueue(new Entry(DateTime.Now, category, message));
            while (Entries.Count > Capacity)
                Entries.Dequeue();
        }
        Changed?.Invoke();
    }

    /// <summary>当前缓冲快照（时间升序，最多 Capacity 条）。</summary>
    public static List<Entry> Snapshot()
    {
        lock (Gate)
            return Entries.ToList();
    }

    public static void Clear()
    {
        lock (Gate)
            Entries.Clear();
        Changed?.Invoke();
    }
}
