using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskVault.Storage;

namespace DiskVault.Jobs;

/// <summary>アプリ全体の設定（%ProgramData%\DiskVault\config.json）。</summary>
public sealed class AppConfig
{
    public List<JobConfig> Jobs { get; set; } = [];
    public MailConfig Mail { get; set; } = new();
    public int HistoryKeepDays { get; set; } = 400;
    /// <summary>表示言語 "auto"（Windows に合わせる）/ "ja" / "en"。GUI・CLI・サービスのログとメールに使う。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>設定の表示言語を Loc に反映する（設定が読めなければ Windows の言語）。</summary>
    public static void ApplyLanguage()
    {
        string? lang = null;
        try { lang = Load().Language; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        Loc.Set(lang);
    }

    /// <summary>データフォルダ。テスト時は環境変数 DISKVAULT_HOME で差し替える。</summary>
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DISKVAULT_HOME");
            var d = !string.IsNullOrEmpty(env) ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskVault");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string FilePath => Path.Combine(Dir, "config.json");

    /// <summary>
    /// データフォルダを Administrators と SYSTEM だけが読み書きできるようにする（管理者として実行時のみ）。
    /// 保存したパスワードは DPAPI（マシン単位）なので、フォルダを読めればどのユーザーでも復号できてしまうため。
    /// </summary>
    public static void SecureDir()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISKVAULT_HOME")) || !Native.IsAdmin()) return;
        var di = new DirectoryInfo(Dir);
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.Principal.WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
        try { di.SetAccessControl(acl); } catch (UnauthorizedAccessException) { } catch (InvalidOperationException) { }
    }

    public static AppConfig Load()
    {
        var f = FilePath;
        if (!File.Exists(f)) return new AppConfig();
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllBytes(f), Repository.Json) ?? new AppConfig();
    }

    public void Save()
    {
        var f = FilePath;
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        Repository.AtomicWrite(f, JsonSerializer.SerializeToUtf8Bytes(this, Repository.Json));
    }

    public JobConfig? Job(string id) => Jobs.FirstOrDefault(j => j.Id == id);
}

public sealed class JobConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = T("バックアップ");
    public bool Enabled { get; set; } = true;
    /// <summary>"system"（ベアメタル復元用にシステムディスク全体）、"C:"、"\\?\Volume{..}\" など。</summary>
    public List<string> Sources { get; set; } = ["system"];
    public DestinationConfig Destination { get; set; } = new();
    /// <summary>バックアップ先の中のフォルダ名（作成時に決め、ジョブ名を変えても変わらない）。</summary>
    public string RepoFolder { get; set; } = "";
    /// <summary>暗号化パスワード（DPAPI で暗号化）。null なら暗号化しない。</summary>
    public string? EncryptionPasswordEnc { get; set; }
    public ScheduleConfig Schedule { get; set; } = new();
    /// <summary>残す復元ポイントの数。</summary>
    public int Keep { get; set; } = 14;
    public bool VerifyAfterBackup { get; set; }
    public bool ExcludeSystemFiles { get; set; } = true;
    public int CompressionLevel { get; set; } = 3;
    /// <summary>VSS ライターと連携する（アプリ整合）。失敗したらクラッシュ整合に切り替える。</summary>
    public bool VssWriters { get; set; } = true;
    /// <summary>VSS 完全バックアップとして記録する（SQL Server・Exchange のトランザクションログを切り捨てる）。false ならコピー。</summary>
    public bool VssFullBackup { get; set; }

    public string RepoPath => Path.Combine(Destination.Path, Environment.MachineName, string.IsNullOrEmpty(RepoFolder) ? Id : RepoFolder);

    public static string SafeFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "job" : name;
    }
}

public sealed class DestinationConfig
{
    /// <summary>D:\Backup や \\nas\share\Backup。</summary>
    public string Path { get; set; } = "";
    /// <summary>共有フォルダの接続ユーザー（null なら現在の資格情報）。</summary>
    public string? User { get; set; }
    public string? PasswordEnc { get; set; }

    public bool IsUnc => Path.StartsWith(@"\\", StringComparison.Ordinal);
}

public sealed class MailConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>"starttls" / "ssl" / "none"</summary>
    public string Security { get; set; } = "starttls";
    public string? User { get; set; }
    public string? PasswordEnc { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
    /// <summary>"always"（毎回）/ "problems"（警告・失敗のときだけ）</summary>
    public string When { get; set; } = "always";
    public string SubjectPrefix { get; set; } = "[DiskVault]";
}

/// <summary>パスワード類を DPAPI（LocalMachine）で暗号化して保存する。サービス（SYSTEM）と GUI（管理者）の両方で読める。</summary>
public static class Secret
{
    static readonly byte[] Entropy = "DiskVault.Secret.v1"u8.ToArray();

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine));
    }

    public static string? Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), Entropy, DataProtectionScope.LocalMachine)); }
        catch (CryptographicException) { throw new UnauthorizedAccessException(T("保存されたパスワードを復号できません（別の PC で作った設定の可能性があります）。設定し直してください。")); }
    }
}

