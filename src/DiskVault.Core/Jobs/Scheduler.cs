using System.Text.Json;
using DiskVault.Storage;

namespace DiskVault.Jobs;

public sealed class JobState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string Schedule { get; set; } = "";
    public bool Running { get; set; }
    public bool Queued { get; set; }
    public JobProgress? Progress { get; set; }
    public JobResult? Last { get; set; }
    public DateTime? Next { get; set; }
    public PointKind? NextKind { get; set; }
}

/// <summary>
/// サービスの中身：設定を監視し、スケジュールの時刻になったジョブを順番に実行する（同時に 1 つ）。
/// 各ジョブの「どこまで評価したか」を state.json に記録し、停止中に過ぎた予定は RunMissed なら起動後に 1 回だけ実行する。
/// </summary>
public sealed class Scheduler : IDisposable
{
    readonly object _lock = new();
    readonly Queue<(string Id, PointKind Kind, string Trigger)> _queue = new();
    readonly Dictionary<string, DateTime> _evaluated;
    readonly Dictionary<string, JobResult> _last = [];
    readonly CancellationTokenSource _stop = new();
    readonly AutoResetEvent _wake = new(false);
    AppConfig _cfg;
    DateTime _cfgTime;
    string? _runningId;
    JobProgress? _progress;
    CancellationTokenSource? _jobCts;
    Thread? _thread;
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(20);
    public Action<string>? Log { get; set; }

    static string StateFile => Path.Combine(AppConfig.Dir, "state.json");

    public Scheduler()
    {
        _cfg = AppConfig.Load();
        _cfgTime = File.Exists(AppConfig.FilePath) ? File.GetLastWriteTimeUtc(AppConfig.FilePath) : default;
        _evaluated = File.Exists(StateFile)
            ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(StateFile)) ?? []
            : [];
        foreach (var r in History.Load(max: 1000))
            _last.TryAdd(r.JobId, r);
    }

    public void Start()
    {
        try
        {
            int n = Platform.VssSnapshot.CleanupStale();
            if (n > 0) Log?.Invoke($"前回残ったスナップショットを {n} 個削除しました。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Management.ManagementException) { }
        _thread = new Thread(Loop) { IsBackground = true, Name = "DiskVault.Scheduler" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        lock (_lock) _jobCts?.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(30));
    }

    public void Dispose() => Stop();

    public void ReloadConfig()
    {
        lock (_lock)
        {
            _cfg = AppConfig.Load();
            _cfgTime = File.Exists(AppConfig.FilePath) ? File.GetLastWriteTimeUtc(AppConfig.FilePath) : default;
        }
        _wake.Set();
    }

    public void Enqueue(string id, PointKind kind, string trigger = "manual")
    {
        lock (_lock)
        {
            if (_cfg.Job(id) == null) throw new KeyNotFoundException($"ジョブ {id} がありません。");
            if (_runningId == id || _queue.Any(q => q.Id == id)) throw new InvalidOperationException("このジョブは既に実行中または待機中です。");
            _queue.Enqueue((id, kind, trigger));
        }
        _wake.Set();
    }

    public bool Cancel(string id)
    {
        lock (_lock)
        {
            if (_runningId == id) { _jobCts?.Cancel(); return true; }
            var rest = _queue.Where(q => q.Id != id).ToList();
            bool removed = rest.Count != _queue.Count;
            _queue.Clear();
            foreach (var q in rest) _queue.Enqueue(q);
            return removed;
        }
    }

    public List<JobState> Status()
    {
        lock (_lock)
        {
            var now = Clock();
            return _cfg.Jobs.Select(j =>
            {
                var next = j.Enabled ? j.Schedule.Next(now) : null;
                return new JobState
                {
                    Id = j.Id,
                    Name = j.Name,
                    Enabled = j.Enabled,
                    Schedule = j.Schedule.Describe(),
                    Running = _runningId == j.Id,
                    Queued = _queue.Any(q => q.Id == j.Id),
                    Progress = _runningId == j.Id ? _progress : null,
                    Last = _last.GetValueOrDefault(j.Id),
                    Next = next?.When,
                    NextKind = next?.Kind,
                };
            }).ToList();
        }
    }

    void Loop()
    {
        var lastPrune = DateTime.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                CheckConfigChanged();
                CheckSchedules();
                RunQueued();
                if (Clock() - lastPrune > TimeSpan.FromDays(1))
                {
                    lastPrune = Clock();
                    History.Prune(_cfg.HistoryKeepDays);
                }
            }
            catch (Exception e)
            {
                Log?.Invoke($"スケジューラーのエラー: {e}");
            }
            _wake.WaitOne(TickInterval);
        }
    }

    void CheckConfigChanged()
    {
        var t = File.Exists(AppConfig.FilePath) ? File.GetLastWriteTimeUtc(AppConfig.FilePath) : default;
        if (t != _cfgTime) ReloadConfig();
    }

    /// <summary>前回評価した時刻から今までに予定があれば待ち行列に入れる。</summary>
    void CheckSchedules()
    {
        var now = Clock();
        bool dirty = false;
        lock (_lock)
        {
            foreach (var j in _cfg.Jobs)
            {
                if (!_evaluated.TryGetValue(j.Id, out var from))
                {
                    _evaluated[j.Id] = now; // 新しいジョブは今から
                    dirty = true;
                    continue;
                }
                if (from > now) { _evaluated[j.Id] = now; dirty = true; continue; } // 時計が戻った
                if (!j.Enabled) { _evaluated[j.Id] = now; dirty = true; continue; }
                var occ = j.Schedule.Occurrences(from, now).ToList();
                _evaluated[j.Id] = now;
                dirty = true;
                if (occ.Count == 0) continue;
                var lastOcc = occ[^1];
                bool missed = now - lastOcc.When > TimeSpan.FromMinutes(15);
                if (missed && !j.Schedule.RunMissed)
                {
                    Log?.Invoke($"{j.Name}: 予定 {lastOcc.When:MM/dd HH:mm} を過ぎていたため実行しません（取りこぼし実行がオフ）。");
                    continue;
                }
                if (_runningId == j.Id || _queue.Any(q => q.Id == j.Id)) continue;
                var kind = occ.Any(o => o.Kind == PointKind.Full) ? PointKind.Full : PointKind.Incremental;
                _queue.Enqueue((j.Id, kind, "schedule"));
                Log?.Invoke($"{j.Name}: {(missed ? "取りこぼした予定" : "予定")} {lastOcc.When:MM/dd HH:mm} の{(kind == PointKind.Full ? "フル" : "増分")}バックアップを開始します。");
            }
        }
        if (dirty) SaveState();
    }

    void SaveState()
    {
        lock (_lock)
            Repository.AtomicWrite(StateFile, JsonSerializer.SerializeToUtf8Bytes(_evaluated));
    }

    void RunQueued()
    {
        while (!_stop.IsCancellationRequested)
        {
            (string Id, PointKind Kind, string Trigger) item;
            JobConfig? job;
            AppConfig cfg;
            lock (_lock)
            {
                if (_queue.Count == 0) return;
                item = _queue.Dequeue();
                job = _cfg.Job(item.Id);
                cfg = _cfg;
                if (job == null) continue;
                _runningId = job.Id;
                _jobCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _progress = new JobProgress { Phase = "準備中", Started = Clock(), Kind = item.Kind };
            }
            JobResult r;
            try
            {
                r = JobRunner.Run(cfg, job, item.Kind, item.Trigger, p => { lock (_lock) _progress = p; }, _jobCts.Token);
            }
            finally
            {
                lock (_lock)
                {
                    _runningId = null;
                    _progress = null;
                    _jobCts.Dispose();
                    _jobCts = null;
                }
            }
            lock (_lock) _last[job.Id] = r;
            Log?.Invoke($"{job.Name}: {r.StatusText}{(r.Error != null ? " - " + r.Error : "")}");
        }
    }
}
