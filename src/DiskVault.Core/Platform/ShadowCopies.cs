using System.Management;

namespace DiskVault.Platform;

/// <summary>Windows に既にあるシャドウコピー（システムの復元ポイント・以前のバージョン・他のバックアップソフトが残したもの）。</summary>
public sealed class ShadowCopyInfo
{
    public required string Id { get; init; }
    /// <summary>元のボリューム（\\?\Volume{GUID}\）。</summary>
    public required string VolumeName { get; init; }
    /// <summary>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</summary>
    public required string DeviceObject { get; init; }
    public DateTime Created { get; init; }
    /// <summary>同じ時刻に作られたシステムの復元ポイントの説明（「Windows Update」など）。なければ null。</summary>
    public string? RestorePoint { get; set; }
    /// <summary>元のボリュームのドライブ文字（"C:"）。なければ null。</summary>
    public string? Letter { get; set; }
    public string? Label { get; set; }

    public string VolumeText => Letter ?? (string.IsNullOrEmpty(Label) ? T("(文字なし)") : Label);
}

public static class ShadowCopies
{
    /// <summary>シャドウコピーの一覧（新しい順）。管理者権限が必要。DiskVault が作業中に作ったものは除く。</summary>
    public static List<ShadowCopyInfo> List()
    {
        if (!Native.IsAdmin()) throw new UnauthorizedAccessException(T("シャドウコピーの一覧には管理者権限が必要です。"));
        var list = new List<ShadowCopyInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT ID, VolumeName, DeviceObject, InstallDate FROM Win32_ShadowCopy");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var created = o["InstallDate"] is string d ? ManagementDateTimeConverter.ToDateTime(d) : default;
                    list.Add(new ShadowCopyInfo
                    {
                        Id = (string)o["ID"],
                        VolumeName = (string)o["VolumeName"],
                        DeviceObject = (string)o["DeviceObject"],
                        Created = created,
                    });
                }
        }
        catch (ManagementException e) { throw new IOException(T("シャドウコピーの一覧を取得できません: {0}", e.Message)); }

        var own = VssSnapshot.OwnIds();
        list.RemoveAll(x => own.Contains(x.Id));

        // ドライブ文字・ラベル
        var vols = VolumeInfo.Enumerate();
        foreach (var x in list)
        {
            var v = vols.FirstOrDefault(v => string.Equals(v.GuidPath, x.VolumeName, StringComparison.OrdinalIgnoreCase));
            x.Letter = v?.Letter;
            x.Label = v?.Label;
        }

        // システムの復元ポイントの説明（復元ポイントの作成時刻から数分以内に作られたシャドウコピーに付ける）
        foreach (var (time, desc) in RestorePoints())
            foreach (var x in list.Where(x => x.RestorePoint == null && x.Created >= time.AddMinutes(-2) && x.Created <= time.AddMinutes(10)))
                x.RestorePoint = desc;

        return list.OrderByDescending(x => x.Created).ToList();
    }

    public static ShadowCopyInfo Find(string id)
    {
        id = id.Trim();
        if (!id.StartsWith('{')) id = "{" + id + "}";
        return List().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? throw new IOException(T("シャドウコピー {0} が見つかりません（Windows に削除された可能性があります）。", id));
    }

    static List<(DateTime Time, string Description)> RestorePoints()
    {
        var r = new List<(DateTime, string)>();
        try
        {
            using var s = new ManagementObjectSearcher(@"\\.\root\default", "SELECT CreationTime, Description FROM SystemRestore");
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (o["CreationTime"] is string t)
                        r.Add((ManagementDateTimeConverter.ToDateTime(t), o["Description"] as string ?? ""));
        }
        catch (ManagementException) { } // システムの復元が無効なら空
        catch (UnauthorizedAccessException) { }
        return r;
    }
}
