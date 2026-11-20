using DiskVault.Platform;
using DiskVault.Source;
using DiskVault.Storage;

namespace DiskVault.Engine;

/// <summary>バックアップ元の一覧と、それに必要なスナップショット（Dispose で削除）。</summary>
public sealed class SourcePlan : IDisposable
{
    public List<VolumeInput> Volumes { get; } = [];
    public List<DiskRecord> Disks { get; } = [];
    /// <summary>スナップショット作成時の警告（失敗した VSS ライターなど）。復元ポイントに記録する。</summary>
    public List<string> Warnings { get; } = [];
    /// <summary>"writers"（アプリ整合）/ "crash"（クラッシュ整合）/ "shadow"（既存のシャドウコピーだけから読んだ）/ "offline" / ""（スナップショットなし）</summary>
    public string Consistency { get; internal set; } = "";
    internal List<IDisposable> Resources { get; } = [];
    internal VssSet? Set { get; set; }
    /// <summary>既存のシャドウコピーから読むボリューム（ボリューム GUID パス → シャドウコピー）。</summary>
    internal Dictionary<string, ShadowCopyInfo> Shadow { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>既存のシャドウコピーから読んだとき、その作成時刻（いちばん古いもの）。</summary>
    public DateTime? SourceTime => Shadow.Count > 0 ? Shadow.Values.Min(x => x.Created) : null;
    internal HashSet<string> ShadowUsed { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>VSS ライターの状態（ライター連携したときだけ）。</summary>
    public IReadOnlyList<(string Writer, string State, int Error)> Writers => Set?.Writers ?? [];

    /// <summary>バックアップが成功したら呼ぶ（VSS ライターに完了を伝える）。</summary>
    public void Complete()
    {
        Set?.BackupComplete();
        if (Set != null) Warnings.AddRange(Set.Warnings.Except(Warnings));
    }

    public void Dispose()
    {
        foreach (var r in Resources) r.Dispose();
        Resources.Clear();
    }
}

public static class SourcePlanner
{
    /// <summary>
    /// 指定（"system" / "physical:N" / "C:" / "\\?\Volume{..}\" / "disk:img" / "img[@off:len]"）からバックアップ元を組み立てる。
    /// ライブのボリュームは VSS スナップショットを作ってから読む（NTFS/ReFS）。FAT（EFI システムパーティション）は直接読む。
    /// </summary>
    /// offline（既定は WinPE のとき true）: Windows が動いていないディスクなので、スナップショットを使わずボリュームを直接読む。
    /// shadows: 既存のシャドウコピーの ID。そのボリュームは新しいスナップショットを作らずにシャドウコピーから読む（ほかのボリュームはいつもどおり）。
    /// 指定の "shadow:{ID}" は、そのシャドウコピーのボリューム 1 つだけをバックアップする。
    public static SourcePlan Build(IEnumerable<string> specs, Action<string>? log = null, bool useWriters = true, bool vssFullBackup = false, bool? offline = null,
        IEnumerable<string>? shadows = null)
    {
        log ??= _ => { };
        var plan = new SourcePlan();
        bool off = offline ?? Sys.IsWinPE;
        var snapNeeded = new List<(VolumeRecord Rec, string Volume, Action<Func<IVolumeSource>> Set)>();
        List<VolumeInfo>? live = null;
        List<VolumeInfo> Live() => live ??= VolumeInfo.Enumerate();
        try
        {
            var specList = specs.Select(x => x.Trim()).ToList();
            var shadowIds = (shadows ?? []).Concat(specList.Where(x => x.StartsWith("shadow:", StringComparison.OrdinalIgnoreCase)).Select(x => x[7..])).ToList();
            if (shadowIds.Count > 0)
            {
                if (off) throw new NotSupportedException(T("回復環境ではシャドウコピーからのバックアップはできません。"));
                foreach (var id in shadowIds)
                {
                    var sc = ShadowCopies.Find(id);
                    if (!plan.Shadow.TryAdd(sc.VolumeName, sc))
                        throw new ArgumentException(T("同じボリューム（{0}）のシャドウコピーが 2 つ指定されています。", sc.VolumeText));
                    log(T("シャドウコピーから読みます: {0}（{1:yyyy/MM/dd HH:mm}{2}）", sc.VolumeText, sc.Created,
                        sc.RestorePoint is { Length: > 0 } rp ? T("、復元ポイント「{0}」", rp) : ""));
                }
            }
            foreach (var spec in specList)
            {
                if (spec.Equals("system", StringComparison.OrdinalIgnoreCase))
                    AddSystemDisk(plan, Live(), off ? null : snapNeeded, log);
                else if (spec.StartsWith("physical:", StringComparison.OrdinalIgnoreCase))
                    AddPhysicalDisk(plan, int.Parse(spec[9..]), Live(), off ? null : snapNeeded, log);
                else if (spec.StartsWith("shadow:", StringComparison.OrdinalIgnoreCase))
                {
                    var sc = plan.Shadow.Values.First(x => string.Equals(x.Id.Trim('{', '}'), spec[7..].Trim().Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
                    var v = VolumeInfo.Find(sc.VolumeName) ?? throw new IOException(T("ボリューム {0} が見つかりません。", sc.VolumeName));
                    AddLiveVolume(plan, v, snapNeeded, isSystem: v.IsSystem, isBoot: false);
                }
                else if (spec.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
                    AddDiskImage(plan, spec[5..]);
                else if (IsLiveVolume(spec))
                {
                    var v = VolumeInfo.Find(spec) ?? throw new IOException(T("ボリューム {0} が見つかりません。", spec));
                    AddLiveVolume(plan, v, off ? null : snapNeeded, isSystem: v.IsSystem, isBoot: false);
                }
                else AddImageFile(plan, spec);
            }
            if (plan.Volumes.Count == 0) throw new ArgumentException(T("バックアップするボリュームがありません。"));
            foreach (var sc in plan.Shadow.Values.Where(x => !plan.ShadowUsed.Contains(x.VolumeName)))
                throw new ArgumentException(T("シャドウコピーのボリューム {0} は、バックアップする範囲に入っていません。", sc.VolumeText));
            if (plan.Shadow.Count > 0 && snapNeeded.Count == 0) plan.Consistency = "shadow";
            if (off)
            {
                plan.Consistency = "offline";
                log(T("回復環境（Windows 停止中）のため、スナップショットを使わずにボリュームを直接読みます。"));
                return plan;
            }
            if (snapNeeded.Count == 0) return plan;
            VssSnapshot.CleanupStale();
            if (useWriters)
            {
                try
                {
                    var vs = VssSet.Create(snapNeeded.Select(s => s.Volume).ToList(), vssFullBackup, log);
                    plan.Set = vs;
                    plan.Resources.Add(vs);
                    plan.Consistency = "writers";
                    plan.Warnings.AddRange(vs.Warnings);
                    foreach (var (rec, vol, set) in snapNeeded)
                    {
                        var dev = vs.DeviceFor(vol);
                        set(() => HandleSource.OpenDevice(dev));
                    }
                    return plan;
                }
                catch (IOException e)
                {
                    log(T("VSS ライターと連携したスナップショットを作れませんでした（{0}）。クラッシュ整合で続けます。", e.Message));
                    plan.Warnings.Add(T("VSS ライター連携に失敗したため、クラッシュ整合でバックアップしました: {0}", e.Message));
                }
            }
            plan.Consistency = "crash";
            foreach (var (rec, vol, set) in snapNeeded)
            {
                log(T("スナップショット作成: {0}", rec.Display.Trim()));
                var snap = VssSnapshot.Create(vol);
                plan.Resources.Add(snap);
                var dev = snap.DeviceObject;
                set(() => HandleSource.OpenDevice(dev));
            }
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    static bool IsLiveVolume(string s) =>
        (s.TrimEnd('\\').Length == 2 && s[1] == ':') || s.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);

    static void AddImageFile(SourcePlan plan, string spec)
    {
        string path = spec;
        long off = 0, len = -1;
        int at = spec.LastIndexOf('@');
        if (at > 2 && !File.Exists(spec))
        {
            path = spec[..at];
            var r = spec[(at + 1)..].Split(':');
            off = long.Parse(r[0]);
            if (r.Length > 1) len = long.Parse(r[1]);
        }
        if (!File.Exists(path)) throw new FileNotFoundException(T("{0} が見つかりません。", path));
        var full = Path.GetFullPath(path);
        plan.Volumes.Add(new VolumeInput
        {
            Record = new VolumeRecord { Label = Path.GetFileName(path), VolumeGuid = $"file:{full}@{off}" },
            Open = () => HandleSource.OpenFile(full, off, len),
        });
    }

    static void AddDiskImage(SourcePlan plan, string path)
    {
        var full = Path.GetFullPath(path);
        DiskRecord disk;
        using (var d = HandleSource.OpenFile(full)) disk = PartitionTable.Read(d);
        disk.Index = plan.Disks.Count;
        disk.Model = "image:" + Path.GetFileName(full);
        foreach (var p in disk.Partitions)
        {
            if (string.Equals(p.TypeGuid, PartitionRecord.MsrType, StringComparison.OrdinalIgnoreCase)) continue;
            var rec = new VolumeRecord
            {
                Label = $"{Path.GetFileName(full)}#{p.Number}",
                VolumeGuid = p.PartitionGuid ?? $"file:{full}#{p.Number}",
                Disk = disk.Index,
                DiskOffset = p.Offset,
                IsBoot = string.Equals(p.TypeGuid, PartitionRecord.EspType, StringComparison.OrdinalIgnoreCase) || p.Active,
                IsSystem = string.Equals(p.TypeGuid, PartitionRecord.BasicDataType, StringComparison.OrdinalIgnoreCase) || p.MbrType == 0x07,
            };
            p.Volume = plan.Volumes.Count;
            long off = p.Offset, len = p.Length;
            plan.Volumes.Add(new VolumeInput { Record = rec, Open = () => HandleSource.OpenFile(full, off, len) });
        }
        plan.Disks.Add(disk);
    }

    static void AddLiveVolume(SourcePlan plan, VolumeInfo v, List<(VolumeRecord, string, Action<Func<IVolumeSource>>)>? snaps, bool isSystem, bool isBoot,
        PartitionRecord? part = null, int? diskIndex = null)
    {
        var rec = new VolumeRecord
        {
            Letter = v.Letter,
            Label = v.Label,
            FileSystem = v.FileSystem,
            VolumeGuid = v.GuidPath,
            Disk = diskIndex,
            DiskOffset = part?.Offset ?? v.DiskOffset,
            IsSystem = isSystem,
            IsBoot = isBoot,
        };
        var input = new Holder { Record = rec };
        var key = v.GuidPath.EndsWith('\\') ? v.GuidPath : v.GuidPath + "\\";
        if (plan.Shadow.TryGetValue(key, out var shadow))
        {
            // 既存のシャドウコピーから読む（新しいスナップショットは作らない。シャドウコピーは消さない）
            var dev = shadow.DeviceObject;
            input.Open = () => HandleSource.OpenDevice(dev);
            plan.ShadowUsed.Add(key);
        }
        else if (snaps != null && v.FileSystem is "NTFS" or "ReFS")
        {
            snaps.Add((rec, v.GuidPath, f => input.Open = f));
        }
        else
        {
            // FAT/exFAT はスナップショットを使わずに直接読む（EFI システムパーティションは小さく、通常は書き込まれない）
            var path = v.GuidPath.TrimEnd('\\');
            input.Open = () => HandleSource.OpenDevice(path);
        }
        if (part != null) part.Volume = plan.Volumes.Count;
        plan.Volumes.Add(new VolumeInput { Record = rec, Open = () => (input.Open ?? throw new InvalidOperationException(T("スナップショット未作成")))() });
    }

    sealed class Holder
    {
        public required VolumeRecord Record;
        public Func<IVolumeSource>? Open;
    }

    static void AddSystemDisk(SourcePlan plan, List<VolumeInfo> live, List<(VolumeRecord, string, Action<Func<IVolumeSource>>)>? snaps, Action<string> log)
    {
        var sys = live.FirstOrDefault(v => v.IsSystem) ?? throw new IOException(T("Windows のボリュームが見つかりません。"));
        if (sys.Disk is not int dn) throw new NotSupportedException(T("システムボリュームが複数ディスクにまたがっています（ダイナミックディスク等は非対応）。"));
        AddPhysicalDisk(plan, dn, live, snaps, log, system: true);
    }

    /// <summary>物理ディスク 1 台をパーティション構成ごと加える（ベアメタル復元できる形）。</summary>
    static void AddPhysicalDisk(SourcePlan plan, int dn, List<VolumeInfo> live, List<(VolumeRecord, string, Action<Func<IVolumeSource>>)>? snaps, Action<string> log,
        bool system = false)
    {
        if (plan.Disks.Any(d => d.Number == dn)) return;
        var disk = DiskLayout.Read(dn);
        disk.Index = plan.Disks.Count;
        log(T("{0}: ディスク {1}（{2}、{3}、{4}、パーティション {5} 個）", system ? T("システムディスク") : T("ディスク"), dn, disk.Model, Fmt.Size(disk.Size), disk.Style, disk.Partitions.Count));
        foreach (var p in disk.Partitions)
        {
            bool esp = string.Equals(p.TypeGuid, PartitionRecord.EspType, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(p.TypeGuid, PartitionRecord.MsrType, StringComparison.OrdinalIgnoreCase)) continue;
            var v = live.FirstOrDefault(x => x.Disk == dn && x.DiskOffset == p.Offset);
            if (v != null)
            {
                AddLiveVolume(plan, v, snaps, isSystem: v.IsSystem, isBoot: esp || p.Active, part: p, diskIndex: disk.Index);
                continue;
            }
            // ボリュームとして見えないパーティション（OEM 領域など）は物理ディスクから直接読む
            var rec = new VolumeRecord { Label = T("パーティション {0}", p.Number), VolumeGuid = p.PartitionGuid, Disk = disk.Index, DiskOffset = p.Offset, IsBoot = esp || p.Active };
            long off = p.Offset, len = p.Length;
            p.Volume = plan.Volumes.Count;
            plan.Volumes.Add(new VolumeInput { Record = rec, Open = () => HandleSource.OpenDevice($@"\\.\PhysicalDrive{dn}", off, len) });
        }
        if (system && !disk.Partitions.Any(p => p.Volume is int i && plan.Volumes[i].Record.IsBoot))
            log(T("警告: システムディスクにブートパーティションが見つかりません。復元後に起動できない可能性があります。"));
        plan.Disks.Add(disk);
    }
}


