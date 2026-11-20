using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskVault.Jobs;

/// <summary>NAS の共有フォルダに資格情報付きで接続する（サービス＝SYSTEM で動くとき用）。</summary>
public sealed partial class NetShare : IDisposable
{
    readonly string? _remote;

    NetShare(string? remote) => _remote = remote;

    /// <summary>\\server\share\sub → \\server\share</summary>
    public static string ShareRoot(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException($"{unc} は共有フォルダのパスではありません。");
        return $@"\\{parts[0]}\{parts[1]}";
    }

    public static NetShare Connect(DestinationConfig d) =>
        d.IsUnc && !string.IsNullOrEmpty(d.User) ? Connect(d.Path, d.User, Secret.Unprotect(d.PasswordEnc)) : new NetShare(null);

    /// <summary>平文のパスワードで接続する（回復環境など、DPAPI に頼らないとき）。</summary>
    public static NetShare Connect(string path, string? user, string? password)
    {
        if (!path.StartsWith(@"\\") || string.IsNullOrEmpty(user)) return new NetShare(null);
        var root = ShareRoot(path);
        var res = new NETRESOURCE { dwType = 1, lpRemoteName = root };
        var pass = password ?? "";
        int rc = WNetAddConnection2W(ref res, pass, user, 0);
        if (rc == 1219)
        {
            // 別の資格情報で接続済み：切ってからつなぎ直す
            WNetCancelConnection2W(root, 0, true);
            rc = WNetAddConnection2W(ref res, pass, user, 0);
        }
        if (rc != 0 && rc != 85) throw new IOException($"{root} に接続できません: {new Win32Exception(rc).Message}（{rc}）");
        return new NetShare(root);
    }

    public void Dispose()
    {
        if (_remote != null) WNetCancelConnection2W(_remote, 0, true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetAddConnection2W(ref NETRESOURCE res, string? password, string? user, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetCancelConnection2W(string name, int flags, bool force);
}
