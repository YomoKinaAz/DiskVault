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
    /// <summary>"writers"（アプリ整合）/ "crash"（クラッシュ整合）/ ""（スナップショットなし）</summary>
    public string Consistency { get; internal set; } = "";
    internal List<IDisposable> Resources { get; } = [];
    internal VssSet? Set { get; set; }
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
    public static SourcePlan Build(IEnumerable<string> specs, Action<string>? log = null, bool useWriters = true, bool vssFullBackup = false, bool? offline = null)
    {
        log ??= _ => { };
        var plan = new SourcePlan();
        bool off = offline ?? Sys.IsWinPE;
        var snapNeeded = new List<(VolumeRecord Rec, string Volume, Action<Func<IVolumeSource>> Set)>();
        List<VolumeInfo>? live = null;
        List<VolumeInfo> Live() => live ??= VolumeInfo.Enumerate();
        try
        {
            foreach (var raw in specs)
            {
                var spec = raw.Trim();
                if (spec.Equals("system", StringComparison.OrdinalIgnoreCase))
                    AddSystemDisk(plan, Live(), off ? null : snapNeeded, log);
                else if (spec.StartsWith("physical:", StringComparison.OrdinalIgnoreCase))
                    AddPhysicalDisk(plan, int.Parse(spec[9..]), Live(), off ? null : snapNeeded, log);
                else if (spec.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
                    AddDiskImage(plan, spec[5..]);
                else if (IsLiveVolume(spec))
                {
                    var v = VolumeInfo.Find(spec) ?? throw new IOException($"ボリューム {spec} が見つかりません。");
                    AddLiveVolume(plan, v, off ? null : snapNeeded, isSystem: v.IsSystem, isBoot: false);
                }
                else AddImageFile(plan, spec);
            }
            if (plan.Volumes.Count == 0) throw new ArgumentException("バックアップするボリュームがありません。");
            if (off)
            {
                plan.Consistency = "offline";
                log("回復環境（Windows 停止中）のため、スナップショットを使わずにボリュームを直接読みます。");
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
                    log($"VSS ライターと連携したスナップショットを作れませんでした（{e.Message}）。クラッシュ整合で続けます。");
                    plan.Warnings.Add($"VSS ライター連携に失敗したため、クラッシュ整合でバックアップしました: {e.Message}");
                }
            }
            plan.Consistency = "crash";
            foreach (var (rec, vol, set) in snapNeeded)
            {
                log($"スナップショット作成: {rec.Display.Trim()}");
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
        if (!File.Exists(path)) throw new FileNotFoundException($"{path} が見つかりません。");
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
        if (snaps != null && v.FileSystem is "NTFS" or "ReFS")
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
        plan.Volumes.Add(new VolumeInput { Record = rec, Open = () => (input.Open ?? throw new InvalidOperationException("スナップショット未作成"))() });
    }

    sealed class Holder
    {
        public required VolumeRecord Record;
        public Func<IVolumeSource>? Open;
    }

    static void AddSystemDisk(SourcePlan plan, List<VolumeInfo> live, List<(VolumeRecord, string, Action<Func<IVolumeSource>>)>? snaps, Action<string> log)
    {
        var sys = live.FirstOrDefault(v => v.IsSystem) ?? throw new IOException("Windows のボリュームが見つかりません。");
        if (sys.Disk is not int dn) throw new NotSupportedException("システムボリュームが複数ディスクにまたがっています（ダイナミックディスク等は非対応）。");
        AddPhysicalDisk(plan, dn, live, snaps, log, system: true);
    }

    /// <summary>物理ディスク 1 台をパーティション構成ごと加える（ベアメタル復元できる形）。</summary>
    static void AddPhysicalDisk(SourcePlan plan, int dn, List<VolumeInfo> live, List<(VolumeRecord, string, Action<Func<IVolumeSource>>)>? snaps, Action<string> log,
        bool system = false)
    {
        if (plan.Disks.Any(d => d.Number == dn)) return;
        var disk = DiskLayout.Read(dn);
        disk.Index = plan.Disks.Count;
        log($"{(system ? "システムディスク" : "ディスク")}: ディスク {dn}（{disk.Model}、{Fmt.Size(disk.Size)}、{disk.Style}、パーティション {disk.Partitions.Count} 個）");
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
            var rec = new VolumeRecord { Label = $"パーティション {p.Number}", VolumeGuid = p.PartitionGuid, Disk = disk.Index, DiskOffset = p.Offset, IsBoot = esp || p.Active };
            long off = p.Offset, len = p.Length;
            p.Volume = plan.Volumes.Count;
            plan.Volumes.Add(new VolumeInput { Record = rec, Open = () => HandleSource.OpenDevice($@"\\.\PhysicalDrive{dn}", off, len) });
        }
        if (system && !disk.Partitions.Any(p => p.Volume is int i && plan.Volumes[i].Record.IsBoot))
            log("警告: システムディスクにブートパーティションが見つかりません。復元後に起動できない可能性があります。");
        plan.Disks.Add(disk);
    }
}


