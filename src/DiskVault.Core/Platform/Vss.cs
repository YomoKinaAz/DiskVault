using System.Management;

namespace DiskVault.Platform;

/// <summary>
/// VSS スナップショット（WMI Win32_ShadowCopy 経由、ClientAccessible コンテキスト＝クラッシュ整合）。
/// 作成した ID は %ProgramData%\DiskVault\snapshots.txt に記録し、異常終了で残ったものは次回 CleanupStale で消す。
/// </summary>
public sealed class VssSnapshot : IDisposable
{
    public string Id { get; }
    public string Volume { get; }
    /// <summary>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</summary>
    public string DeviceObject { get; }
    bool _disposed;

    VssSnapshot(string id, string volume, string device)
    {
        Id = id;
        Volume = volume;
        DeviceObject = device;
    }

    static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskVault", "snapshots.txt");

    /// <summary>volume は "C:\" または "\\?\Volume{GUID}\"。</summary>
    public static VssSnapshot Create(string volume)
    {
        if (!volume.EndsWith('\\')) volume += "\\";
        if (!Native.IsAdmin()) throw new UnauthorizedAccessException(T("スナップショットの作成には管理者権限が必要です。管理者として実行してください。"));
        using var cls = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
        using var inp = cls.GetMethodParameters("Create");
        inp["Volume"] = volume;
        inp["Context"] = "ClientAccessible";
        ManagementBaseObject outp;
        try { outp = cls.InvokeMethod("Create", inp, null); }
        catch (ManagementException e) { throw new IOException(T("{0} のスナップショットを作成できません: {1}", volume, e.Message)); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException(T("スナップショットの作成には管理者権限が必要です。")); }
        using (outp)
        {
            uint rv = Convert.ToUInt32(outp["ReturnValue"]);
            if (rv != 0) throw new IOException(T("{0} のスナップショットを作成できません: {1}（{2}）", volume, Reason(rv), rv));
            var id = (string)outp["ShadowID"];
            Record(id, add: true);
            using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{id}'");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    return new VssSnapshot(id, volume, (string)o["DeviceObject"]);
            throw new IOException(T("作成したスナップショット {0} が見つかりません。", id));
        }
    }

    static string Reason(uint rv) => rv switch
    {
        1 => T("アクセス拒否"),
        2 => T("引数が不正"),
        3 => T("ボリュームが見つからない"),
        4 => T("このボリュームは非対応"),
        5 => T("コンテキストが非対応"),
        6 => T("ディスク容量不足"),
        7 => T("ボリュームが使用中"),
        8 => T("スナップショット数の上限"),
        9 => T("別のスナップショット処理が実行中"),
        10 => T("プロバイダーが拒否"),
        11 => T("プロバイダー未登録"),
        12 => T("プロバイダーの障害"),
        _ => T("不明なエラー"),
    };

    static void Record(string id, bool add)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            lock (typeof(VssSnapshot))
            {
                var ids = File.Exists(StateFile) ? File.ReadAllLines(StateFile).Where(l => l.Length > 0).ToList() : [];
                if (add) ids.Add(id); else ids.RemoveAll(l => l.Equals(id, StringComparison.OrdinalIgnoreCase));
                File.WriteAllLines(StateFile, ids);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool Delete(string id)
    {
        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID='{id}'");
        bool found = false;
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                o.Delete();
                found = true;
            }
        return found;
    }

    /// <summary>DiskVault が作業用に作ったスナップショットの ID（既存のシャドウコピーの一覧から除くため）。</summary>
    public static HashSet<string> OwnIds()
    {
        try { return File.Exists(StateFile) ? File.ReadAllLines(StateFile).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>前回の異常終了で残ったスナップショットを消す。</summary>
    public static int CleanupStale()
    {
        if (!File.Exists(StateFile)) return 0;
        int n = 0;
        foreach (var id in File.ReadAllLines(StateFile).Where(l => l.Length > 0))
        {
            try { if (Delete(id)) n++; } catch (ManagementException) { }
            Record(id, add: false);
        }
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Delete(Id); } catch (ManagementException) { }
        Record(Id, add: false);
    }
}

