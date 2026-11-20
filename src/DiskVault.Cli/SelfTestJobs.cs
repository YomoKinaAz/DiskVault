using System.Net;
using System.Net.Sockets;
using System.Text;
using DiskVault.Jobs;
using DiskVault.Storage;

namespace DiskVault.Cli;

/// <summary>ジョブ・スケジュール・メール・サービス制御（パイプ）の自己テスト。</summary>
static class SelfTestJobs
{
    public static void Run(string work, Action<bool, string> check)
    {
        Schedules(check);
        var home = Path.Combine(work, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("DISKVAULT_HOME", home);
        Environment.SetEnvironmentVariable("DISKVAULT_PIPE", "DiskVault.SelfTest." + Environment.ProcessId);
        try { Jobs(work, check); }
        finally
        {
            Environment.SetEnvironmentVariable("DISKVAULT_HOME", null);
            Environment.SetEnvironmentVariable("DISKVAULT_PIPE", null);
        }
    }

    static void Schedules(Action<bool, string> check)
    {
        Console.WriteLine("== スケジュール");
        var s = new ScheduleConfig
        {
            Incremental = "daily", Days = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Sunday],
            Times = ["02:00", "14:30"], Full = "weekly", FullDay = DayOfWeek.Sunday, FullTime = "02:00",
        };
        var mon = new DateTime(2026, 11, 9); // 月曜
        var occ = s.Occurrences(mon, mon.AddDays(7)).ToList();
        check(occ.Count == 8, $"毎週 月水金日 2 回 = 8 回（{occ.Count}）");
        check(occ.Count(o => o.Kind == PointKind.Full) == 1 && occ.First(o => o.Kind == PointKind.Full).When == new DateTime(2026, 11, 15, 2, 0, 0),
            "日曜 02:00 は増分と重なってフルになる");
        check(s.Next(new DateTime(2026, 11, 9, 2, 0, 0)) == (new DateTime(2026, 11, 9, 14, 30, 0), PointKind.Incremental), "ちょうどの時刻の次は同じ日の 14:30");
        var h = new ScheduleConfig { Incremental = "hourly", IntervalHours = 6, HourlyStart = "01:00", Full = "monthly", FullDayOfMonth = 31, FullTime = "03:00" };
        var nov = h.Occurrences(new DateTime(2026, 11, 29), new DateTime(2026, 12, 1)).ToList();
        check(nov.Count(o => o.Kind == PointKind.Full) == 1 && nov.Any(o => o.When == new DateTime(2026, 11, 30, 3, 0, 0) && o.Kind == PointKind.Full),
            "毎月 31 日指定は 30 日までの月は月末に実行");
        check(nov.Count(o => o.Kind == PointKind.Incremental) == 8, "6 時間ごと（01,07,13,19 時）× 2 日");
        check(!new ScheduleConfig { Enabled = false }.Occurrences(mon, mon.AddDays(3)).Any(), "無効なら予定なし");
    }

    static void Jobs(string work, Action<bool, string> check)
    {
        Console.WriteLine("== ジョブ・メール通知");
        var vol = Path.Combine(work, "jobvol.img");
        var data = new byte[24 << 20];
        new Random(7).NextBytes(data.AsSpan(0, 8 << 20));
        File.WriteAllBytes(vol, data);

        using var smtp = new FakeSmtp();
        var cfg = new AppConfig
        {
            Mail = new MailConfig
            {
                Enabled = true, Host = "127.0.0.1", Port = smtp.Port, Security = "none", From = "dv@test.local", To = ["admin@test.local"], When = "always",
            },
        };
        var job = new JobConfig
        {
            Name = "テストジョブ", Sources = [vol], Destination = new DestinationConfig { Path = Path.Combine(work, "dest") },
            RepoFolder = "テストジョブ", EncryptionPasswordEnc = Secret.Protect("pw-123"), Keep = 2,
            VerifyAfterBackup = true,
            Schedule = new ScheduleConfig { Incremental = "hourly", IntervalHours = 1, HourlyStart = "00:00", Full = "none" },
        };
        cfg.Jobs.Add(job);
        cfg.Save();
        check(Secret.Unprotect(job.EncryptionPasswordEnc) == "pw-123", "DPAPI でパスワードを保存・復号");

        var r1 = JobRunner.Run(cfg, job, PointKind.Incremental, "manual");
        check(r1.Status == JobStatus.Success && r1.Kind == PointKind.Full, $"ジョブ 1 回目は成功・フル（{r1.StatusText} {r1.Error}）");
        check(Repository.Open(job.RepoPath, "pw-123").IsEncrypted, $"保存先 {Path.GetRelativePath(work, job.RepoPath)} は暗号化リポジトリ");
        var mail = smtp.WaitMessage(5000);
        check(mail != null && mail.Contains("admin@test.local") && mail.Contains("Subject:"), "メール通知が届く");
        if (mail != null)
        {
            var decoded = FakeSmtp.DecodeBodies(mail);
            check(decoded.Contains("テストジョブ") && decoded.Contains("成功"), "本文に日本語のジョブ名と結果");
        }

        cfg.Mail.When = "problems";
        var r2 = JobRunner.Run(cfg, job, PointKind.Incremental, "manual");
        check(r2.Status == JobStatus.Success && r2.Kind == PointKind.Incremental && r2.NewBytes == 0, "2 回目は増分・新規 0");
        check(smtp.WaitMessage(1000) == null, "「問題があるときだけ」なら成功時は送らない");

        var bad = new JobConfig { Name = "壊れたジョブ", Sources = [Path.Combine(work, "nothing.img")], Destination = job.Destination, RepoFolder = "bad" };
        var r3 = JobRunner.Run(cfg, bad, PointKind.Incremental, "manual");
        check(r3.Status == JobStatus.Failed && r3.Error != null, $"元が無いジョブは失敗（{r3.Error}）");
        var m3 = smtp.WaitMessage(5000);
        check(m3 != null && FakeSmtp.DecodeBodies(m3).Contains("失敗"), "失敗時はメールが届く");
        check(History.Load().Count == 3 && File.Exists(r1.LogFile), "履歴 3 件とログファイル");

        Console.WriteLine("== スケジューラー・サービス制御");
        cfg.Mail.Enabled = false;
        cfg.Save();
        var clock = new DateTime(2026, 11, 11, 9, 50, 0);
        using var sch = new Scheduler { Clock = () => clock, TickInterval = TimeSpan.FromMilliseconds(200) };
        sch.Start();
        Thread.Sleep(600); // 初回評価（ここから先の予定だけを見る）
        clock = new DateTime(2026, 11, 11, 10, 0, 30);
        var t0 = DateTime.Now;
        while (DateTime.Now - t0 < TimeSpan.FromSeconds(30) && History.Load(job.Id).Count < 3) Thread.Sleep(200);
        var hist = History.Load(job.Id);
        check(hist.Count == 3 && hist[0].Trigger == "schedule", "10:00 の予定で自動実行された");

        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Ipc.Serve(sch, cts.Token, "test"));
        var st = Ipc.Call(new IpcRequest { Cmd = "status" });
        check(st?.Ok == true && st.Jobs?.Count == 1 && st.Jobs[0].Next == new DateTime(2026, 11, 11, 11, 0, 0), "パイプで状態取得（次回 11:00）");
        var run = Ipc.Call(new IpcRequest { Cmd = "run", JobId = job.Id, Kind = PointKind.Full });
        check(run?.Ok == true, "パイプから手動フル実行を依頼");
        t0 = DateTime.Now;
        while (DateTime.Now - t0 < TimeSpan.FromSeconds(30) && History.Load(job.Id).Count < 4) Thread.Sleep(200);
        hist = History.Load(job.Id);
        check(hist.Count == 4 && hist[0].Kind == PointKind.Full && hist[0].Trigger == "manual", "手動フルが実行された");
        check(Repository.Open(job.RepoPath, "pw-123").ListPoints().Count == 2, "保持数 2 で世代整理された");

        // 停止中に予定を過ぎた場合（取りこぼし実行）
        clock = new DateTime(2026, 11, 11, 13, 20, 0);
        t0 = DateTime.Now;
        while (DateTime.Now - t0 < TimeSpan.FromSeconds(30) && History.Load(job.Id).Count < 5) Thread.Sleep(200);
        hist = History.Load(job.Id);
        check(hist.Count == 5, "過ぎた 3 回分の予定は 1 回だけ実行（取りこぼし実行）");
        cts.Cancel();
        sch.Stop();
        try { server.Wait(3000); } catch (AggregateException) { }
    }
}

/// <summary>テスト用の最小 SMTP サーバー（受け取ったメールを生のまま保持）。</summary>
sealed class FakeSmtp : IDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly System.Collections.Concurrent.BlockingCollection<string> _msgs = [];
    readonly CancellationTokenSource _cts = new();
    public int Port { get; }

    public FakeSmtp()
    {
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        Task.Run(Accept);
    }

    async Task Accept()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Session(c));
        }
    }

    void Session(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.ASCII);
            var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            w.WriteLine("220 fake ESMTP");
            var env = new StringBuilder();
            try
            {
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    var u = line.ToUpperInvariant();
                    if (u.StartsWith("EHLO")) { w.WriteLine("250-fake"); w.WriteLine("250 8BITMIME"); }
                    else if (u.StartsWith("HELO")) w.WriteLine("250 fake");
                    else if (u.StartsWith("MAIL") || u.StartsWith("RCPT")) { env.AppendLine(line); w.WriteLine("250 OK"); }
                    else if (u == "DATA")
                    {
                        w.WriteLine("354 go");
                        var data = new StringBuilder(env.ToString());
                        while ((line = r.ReadLine()) != null && line != ".") data.AppendLine(line);
                        _msgs.Add(data.ToString());
                        w.WriteLine("250 queued");
                    }
                    else if (u == "QUIT") { w.WriteLine("221 bye"); return; }
                    else w.WriteLine("250 OK");
                }
            }
            catch (IOException) { }
        }
    }

    public string? WaitMessage(int ms) => _msgs.TryTake(out var m, ms) ? m : null;

    /// <summary>base64 の本文を復号して連結（件名・本文の確認用）。</summary>
    public static string DecodeBodies(string raw)
    {
        var sb = new StringBuilder();
        var parts = raw.Split("\r\n\r\n");
        foreach (var p in parts)
        {
            var b64 = string.Concat(p.Split("\r\n").TakeWhile(l => l.Length > 0 && !l.StartsWith("--")));
            try { sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); } catch (FormatException) { }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _l.Stop();
    }
}
