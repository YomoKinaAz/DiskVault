using System.Text.Json;
using DiskVault.Engine;
using DiskVault.Storage;

namespace DiskVault.Jobs;

public enum JobStatus { Success, Warning, Failed, Canceled }

/// <summary>ジョブ 1 回分の結果（history.jsonl に 1 行ずつ保存）。</summary>
public sealed class JobResult
{
    public string JobId { get; set; } = "";
    public string JobName { get; set; } = "";
    public PointKind Kind { get; set; }
    public string Trigger { get; set; } = "manual";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public JobStatus Status { get; set; }
    public string? PointId { get; set; }
    public long ReadBytes { get; set; }
    public long NewBytes { get; set; }
    public long StoredBytes { get; set; }
    public int Points { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? LogFile { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> LogTail { get; set; } = [];

    public string StatusText => Status switch
    {
        JobStatus.Success => T("成功"),
        JobStatus.Warning => T("警告あり"),
        JobStatus.Failed => T("失敗"),
        _ => IsEnglish ? "Canceled" : "中止", // 「中止」は GUI のボタン（Cancel）と同じキーになるため T() を使わない
    };
}

/// <summary>実行中ジョブの進捗（GUI に見せる）。</summary>
public sealed class JobProgress
{
    public string Phase { get; set; } = "";
    public int Volume { get; set; }
    public int VolumeCount { get; set; }
    public long Done { get; set; }
    public long Total { get; set; }
    public long NewBytes { get; set; }
    public double Rate { get; set; }
    public DateTime Started { get; set; }
    public PointKind Kind { get; set; }
    public string? LastLog { get; set; }
}

public static class History
{
    public static string FilePath => Path.Combine(AppConfig.Dir, "history.jsonl");
    static readonly object Lock = new();

    public static void Append(JobResult r)
    {
        var line = JsonSerializer.Serialize(r, new JsonSerializerOptions(Repository.Json) { WriteIndented = false });
        lock (Lock) File.AppendAllText(FilePath, line + "\n");
    }

    public static List<JobResult> Load(string? jobId = null, int max = 500)
    {
        if (!File.Exists(FilePath)) return [];
        var list = new List<JobResult>();
        string[] lines;
        lock (Lock)
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
        {
            try
            {
                var r = JsonSerializer.Deserialize<JobResult>(lines[i], Repository.Json);
                if (r != null && (jobId == null || r.JobId == jobId)) list.Add(r);
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>古い履歴とログを消す。</summary>
    public static void Prune(int keepDays)
    {
        var cut = DateTime.Now.AddDays(-keepDays);
        var logs = Path.Combine(AppConfig.Dir, "logs");
        if (Directory.Exists(logs))
            foreach (var f in Directory.EnumerateFiles(logs, "*.log", SearchOption.AllDirectories))
                if (File.GetLastWriteTime(f) < cut) try { File.Delete(f); } catch (IOException) { }
        if (!File.Exists(FilePath)) return;
        lock (Lock)
        {
            var keep = File.ReadAllLines(FilePath).Where(l =>
            {
                try { return JsonSerializer.Deserialize<JobResult>(l, Repository.Json)?.End >= cut; } catch (JsonException) { return false; }
            }).ToList();
            File.WriteAllLines(FilePath, keep);
        }
    }
}

/// <summary>ジョブを 1 回実行する：共有フォルダ接続 → スナップショット → バックアップ → 世代整理 →（検証）→ 履歴・メール。</summary>
public static class JobRunner
{
    /// shadows: 既存のシャドウコピーの ID（そのボリュームはシャドウコピーの時点の内容でバックアップする）。
    public static JobResult Run(AppConfig cfg, JobConfig job, PointKind kind, string trigger, Action<JobProgress>? onProgress = null,
        CancellationToken ct = default, IReadOnlyList<string>? shadows = null)
    {
        var r = new JobResult { JobId = job.Id, JobName = job.Name, Kind = kind, Trigger = trigger, Start = DateTime.Now };
        var logDir = Path.Combine(AppConfig.Dir, "logs", job.Id);
        Directory.CreateDirectory(logDir);
        r.LogFile = Path.Combine(logDir, $"{r.Start:yyyyMMdd-HHmmss}.log");
        var prog = new JobProgress { Started = r.Start, Kind = kind, Phase = T("準備中") };
        using var logw = new StreamWriter(r.LogFile, append: true) { AutoFlush = true };
        var tail = new List<string>();
        void Log(string m)
        {
            var line = $"{DateTime.Now:HH:mm:ss} {m}";
            logw.WriteLine(line);
            lock (tail) { tail.Add(line); if (tail.Count > 60) tail.RemoveAt(0); }
            prog.LastLog = m;
            onProgress?.Invoke(prog);
        }
        Log(T("ジョブ「{0}」開始（{1}、{2}）", job.Name, kind == PointKind.Full ? T("フル") : T("増分"), trigger == "schedule" ? T("スケジュール") : T("手動")));
        try
        {
            if (string.IsNullOrWhiteSpace(job.Destination.Path)) throw new InvalidOperationException(T("バックアップ先が設定されていません。"));
            using var share = NetShare.Connect(job.Destination);
            var repoPath = job.RepoPath;
            Log(T("バックアップ先: {0}", repoPath));
            var pw = Secret.Unprotect(job.EncryptionPasswordEnc);
            var repo = Repository.OpenOrCreate(repoPath, pw, level: job.CompressionLevel);
            if (repo.IsEncrypted != (pw != null))
                Log(repo.IsEncrypted ? T("注意: バックアップ先は暗号化されています。") : T("注意: バックアップ先は既存の暗号化なしのリポジトリです（暗号化設定は新しい保存先から有効になります）。"));
            using (var plan = SourcePlanner.Build(job.Sources, Log, job.VssWriters, job.VssFullBackup, shadows: shadows))
            {
                prog.Phase = T("バックアップ中");
                var point = BackupEngine.Run(repo, plan.Volumes, new BackupOptions
                {
                    Job = job.Name,
                    Kind = kind,
                    Disks = plan.Disks,
                    ExcludeSystemFiles = job.ExcludeSystemFiles,
                    Warnings = [.. plan.Warnings],
                    Consistency = plan.Consistency,
                    SourceTime = plan.SourceTime,
                    Comment = plan.SourceTime is { } st ? T("シャドウコピー（{0:yyyy/MM/dd HH:mm}）の内容", st) : null,
                }, new Progress(prog, onProgress), Log, ct);
                plan.Complete();
                if (plan.Writers.Count > 0)
                    Log(T("VSS ライター: {0}", string.Join(T("、"), plan.Writers.Select(w => w.Error == 0 && !Platform.VssSet.IsFailedState(w.State) ? w.Writer : T("{0}（{1}）", w.Writer, w.State)))));
                foreach (var w in plan.Warnings.Except(point.Warnings)) { r.Warnings.Add(w); Log(T("警告: {0}", w)); }
                r.Kind = point.Kind;
                r.PointId = point.Id;
                r.ReadBytes = point.Stats.ReadBytes;
                r.NewBytes = point.Stats.NewBytes;
                r.StoredBytes = point.Stats.StoredBytes;
                r.Warnings.AddRange(point.Warnings);
            }
            ct.ThrowIfCancellationRequested();
            prog.Phase = T("世代整理中");
            onProgress?.Invoke(prog);
            var ret = Maintenance.ApplyRetention(repo, job.Keep, Log);
            if (ret.DeletedPoints.Count > 0) Log(T("世代整理: {0} ポイント削除、{1} 解放", ret.DeletedPoints.Count, Fmt.Size(ret.FreedBytes)));
            if (job.VerifyAfterBackup && r.PointId != null)
            {
                prog.Phase = T("検証中");
                var v = Maintenance.Verify(repo, [repo.GetPoint(r.PointId)], new Progress<(long Done, long Total)>(x =>
                {
                    prog.Done = x.Done;
                    prog.Total = x.Total;
                    onProgress?.Invoke(prog);
                }), ct);
                if (v.Bad > 0)
                {
                    r.Warnings.Add(T("検証で {0} 件のエラー: {1}", v.Bad, string.Join(" / ", v.Errors.Take(3))));
                    Log(T("検証 NG: {0} 件", v.Bad));
                }
                else Log(T("検証 OK（{0} ブロック）", v.Blocks));
            }
            r.Points = repo.ListPoints().Count;
            r.Status = r.Warnings.Count > 0 ? JobStatus.Warning : JobStatus.Success;
        }
        catch (OperationCanceledException)
        {
            r.Status = JobStatus.Canceled;
            r.Error = T("ユーザーが中止しました。");
        }
        catch (Exception e)
        {
            r.Status = JobStatus.Failed;
            r.Error = e.Message;
            logw.WriteLine(e.ToString());
        }
        r.End = DateTime.Now;
        Log(T("終了: {0}{1}（{2}）", r.StatusText, r.Error != null ? " - " + r.Error : "", Fmt.Duration(r.End - r.Start)));
        lock (tail) r.LogTail = [.. tail];
        var mailErr = Mailer.NotifyJob(cfg.Mail, r);
        if (mailErr != null) logw.WriteLine($"{DateTime.Now:HH:mm:ss} " + T("メール送信に失敗: {0}", mailErr));
        History.Append(r);
        return r;
    }

    sealed class Progress(JobProgress p, Action<JobProgress>? cb) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress v)
        {
            p.Phase = v.Phase;
            p.Volume = v.Volume;
            p.VolumeCount = v.VolumeCount;
            p.Done = v.Done;
            p.Total = v.Total;
            p.NewBytes = v.NewBytes;
            p.Rate = v.BytesPerSecond;
            cb?.Invoke(p);
        }
    }
}



