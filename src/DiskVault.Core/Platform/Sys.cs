using System.Runtime.InteropServices;

namespace DiskVault.Platform;

public static class Sys
{
    public static bool IsAdmin => Native.IsAdmin();
    public static bool IsWinPE => Engine.Targets.IsWinPE;

    /// <summary>MSIX パッケージ（Microsoft Store 版）として動いているか。サービスの登録はパッケージが行う。</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    static bool DetectPackage()
    {
        try
        {
            int len = 0;
            return GetCurrentPackageFullName(ref len, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    /// <summary>
    /// COM のセキュリティを初期化する（VSS ライターからのコールバックを受けるため）。プロセスの最初に 1 回呼ぶ。
    /// </summary>
    public static void InitComSecurity()
    {
        try { CoInitializeSecurity(0, -1, 0, 0, 6 /* PKT_PRIVACY */, 2 /* IMP_IDENTIFY */, 0, 0x40 /* DYNAMIC_CLOAKING */, 0); }
        catch (DllNotFoundException) { }
    }

    [DllImport("ole32.dll")]
    static extern int CoInitializeSecurity(nint sd, int cAuth, nint authSvc, nint reserved1, int authnLevel, int impLevel, nint authList, int capabilities, nint reserved3);
}
