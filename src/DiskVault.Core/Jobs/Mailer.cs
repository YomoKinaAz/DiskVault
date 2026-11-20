using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DiskVault.Jobs;

/// <summary>SMTP でメール通知を送る（MailKit）。</summary>
public static class Mailer
{
    public static void Send(MailConfig c, string subject, string text, string? html = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.Host)) throw new InvalidOperationException(T("SMTP サーバーが設定されていません。"));
        if (c.To.Count == 0) throw new InvalidOperationException(T("宛先が設定されていません。"));
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(c.From) ? c.User ?? "diskvault@localhost" : c.From));
        foreach (var t in c.To) msg.To.Add(MailboxAddress.Parse(t));
        msg.Subject = $"{c.SubjectPrefix} {subject}".Trim();
        var body = new BodyBuilder { TextBody = text };
        if (html != null) body.HtmlBody = html;
        msg.Body = body.ToMessageBody();
        foreach (var part in msg.BodyParts.OfType<TextPart>())
            part.ContentTransferEncoding = ContentEncoding.Base64;

        using var smtp = new SmtpClient { Timeout = 60_000 };
        var sec = c.Security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        smtp.Connect(c.Host, c.Port, sec, ct);
        if (!string.IsNullOrEmpty(c.User))
            smtp.Authenticate(new NetworkCredential(c.User, Secret.Unprotect(c.PasswordEnc) ?? ""), ct);
        smtp.Send(FormatOptions.Default, new MailKit.SendRequest(msg), ct);
        smtp.Disconnect(true, ct);
    }

    /// <summary>ジョブ結果の通知（設定の条件を満たすときだけ送る）。送らなかった・送れたら null、失敗したらエラーメッセージ。</summary>
    public static string? NotifyJob(MailConfig c, JobResult r)
    {
        if (!c.Enabled) return null;
        if (c.When == "problems" && r.Status == JobStatus.Success) return null;
        try
        {
            Send(c, T("{0}: {1}（{2}）", r.StatusText, r.JobName, Environment.MachineName), Text(r), Html(r));
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.Net.Sockets.SocketException
                                       or MailKit.ProtocolException or MailKit.Security.AuthenticationException or MailKit.CommandException
                                       or FormatException or OperationCanceledException or TimeoutException)
        {
            return e.Message;
        }
    }

    static string Text(JobResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(T("ジョブ: {0}", r.JobName));
        sb.AppendLine($"PC: {Environment.MachineName}");
        sb.AppendLine(T("結果: {0}", r.StatusText));
        sb.AppendLine(T("種別: {0}", r.Kind == Storage.PointKind.Full ? T("フル") : T("増分")));
        sb.AppendLine(T("開始: {0:yyyy-MM-dd HH:mm:ss}", r.Start));
        sb.AppendLine(T("終了: {0:yyyy-MM-dd HH:mm:ss}（{1}）", r.End, Fmt.Duration(r.End - r.Start)));
        if (r.PointId != null) sb.AppendLine(T("復元ポイント: {0}", r.PointId));
        if (r.ReadBytes > 0) sb.AppendLine(T("読み取り {0} / 新規 {1} / 保存 {2}", Fmt.Size(r.ReadBytes), Fmt.Size(r.NewBytes), Fmt.Size(r.StoredBytes)));
        if (r.Points > 0) sb.AppendLine(T("保持中の復元ポイント: {0}", r.Points));
        if (r.Error != null) sb.AppendLine(T("エラー: {0}", r.Error));
        foreach (var w in r.Warnings) sb.AppendLine(T("警告: {0}", w));
        sb.AppendLine();
        sb.AppendLine(T("--- ログ ---"));
        foreach (var l in r.LogTail) sb.AppendLine(l);
        return sb.ToString();
    }

    static string Html(JobResult r)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        string color = r.Status switch { JobStatus.Success => "#1a7f37", JobStatus.Warning => "#9a6700", _ => "#cf222e" };
        var rows = new List<(string, string)>
        {
            (T("ジョブ"), r.JobName), ("PC", Environment.MachineName), (T("種別"), r.Kind == Storage.PointKind.Full ? T("フル") : T("増分")),
            (T("開始"), $"{r.Start:yyyy-MM-dd HH:mm:ss}"), (T("所要時間"), Fmt.Duration(r.End - r.Start)),
        };
        if (r.PointId != null) rows.Add((T("復元ポイント"), r.PointId));
        if (r.ReadBytes > 0) rows.Add((T("データ量"), T("読み取り {0} / 新規 {1} / 保存 {2}", Fmt.Size(r.ReadBytes), Fmt.Size(r.NewBytes), Fmt.Size(r.StoredBytes))));
        if (r.Points > 0) rows.Add((T("保持中のポイント"), r.Points.ToString()));
        if (r.Error != null) rows.Add((T("エラー"), r.Error));
        foreach (var w in r.Warnings) rows.Add((T("警告"), w));
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:'Yu Gothic UI','Meiryo',sans-serif;font-size:14px\">");
        sb.Append($"<div style=\"font-size:18px;font-weight:bold;color:{color};margin-bottom:8px\">{E(r.StatusText)}</div>");
        sb.Append("<table style=\"border-collapse:collapse\">");
        foreach (var (k, v) in rows)
            sb.Append($"<tr><td style=\"padding:3px 12px 3px 0;color:#57606a;white-space:nowrap\">{E(k)}</td><td style=\"padding:3px 0\">{E(v)}</td></tr>");
        sb.Append("</table>");
        sb.Append("<pre style=\"margin-top:12px;padding:8px;background:#f6f8fa;font-size:12px\">");
        sb.Append(E(string.Join("\n", r.LogTail)));
        sb.Append("</pre></div>");
        return sb.ToString();
    }
}

