using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskVault.Storage;

namespace DiskVault.Jobs;

public sealed class IpcRequest
{
    /// <summary>status / run / cancel / reload / ping</summary>
    public string Cmd { get; set; } = "";
    public string? JobId { get; set; }
    public PointKind? Kind { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<JobState>? Jobs { get; set; }
    public string? Version { get; set; }
}

/// <summary>サービスと GUI の間の名前付きパイプ（1 行 JSON の要求 → 1 行 JSON の応答）。Administrators と SYSTEM だけが接続できる。</summary>
public static class Ipc
{
    public static string PipeName => Environment.GetEnvironmentVariable("DISKVAULT_PIPE") ?? "DiskVault.Control";
    static JsonSerializerOptions J => Repository.Json;
    static readonly JsonSerializerOptions Line = new(Repository.Json) { WriteIndented = false };

    public static void Serve(Scheduler s, CancellationToken ct, string version)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // テスト時（非管理者でコンソール実行）は自分自身も許可
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!ct.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
            try { pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            _ = Task.Run(() => Handle(pipe, s, version));
        }
    }

    static void Handle(NamedPipeServerStream pipe, Scheduler s, string version)
    {
        using (pipe)
        {
            try
            {
                var r = new StreamReader(pipe, Encoding.UTF8);
                var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                var line = r.ReadLine();
                if (line == null) return;
                IpcResponse resp;
                try
                {
                    var req = JsonSerializer.Deserialize<IpcRequest>(line, J) ?? new IpcRequest();
                    resp = Dispatch(req, s, version);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                {
                    resp = new IpcResponse { Ok = false, Error = e.Message };
                }
                w.WriteLine(JsonSerializer.Serialize(resp, Line));
            }
            catch (IOException) { }
        }
    }

    static IpcResponse Dispatch(IpcRequest req, Scheduler s, string version)
    {
        switch (req.Cmd)
        {
            case "ping": return new IpcResponse { Ok = true, Version = version };
            case "status": return new IpcResponse { Ok = true, Jobs = s.Status(), Version = version };
            case "run":
                s.Enqueue(req.JobId ?? throw new ArgumentException("JobId がありません。"), req.Kind ?? PointKind.Incremental);
                return new IpcResponse { Ok = true };
            case "cancel":
                return new IpcResponse { Ok = s.Cancel(req.JobId ?? "") , Error = null };
            case "reload":
                s.ReloadConfig();
                return new IpcResponse { Ok = true };
            default: throw new ArgumentException($"不明なコマンド {req.Cmd}");
        }
    }

    /// <summary>サービスに要求を送る。サービスが動いていなければ null。</summary>
    public static IpcResponse? Call(IpcRequest req, int timeoutMs = 3000)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            c.Connect(timeoutMs);
            var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(c, Encoding.UTF8);
            w.WriteLine(JsonSerializer.Serialize(req, Line));
            var line = r.ReadLine();
            return line == null ? null : JsonSerializer.Deserialize<IpcResponse>(line, J);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
