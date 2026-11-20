namespace DiskVault.Storage;

public enum PointKind { Full, Incremental }

/// <summary>1 回のバックアップ結果。各ポイントは単独で完全に復元できる（合成フル）。</summary>
public sealed class RecoveryPoint
{
    /// <summary>フォルダ名（yyyyMMdd-HHmmss）。JSON には保存しない。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    public PointKind Kind { get; set; }
    public string Chain { get; set; } = "";
    public string Job { get; set; } = "";
    public string Machine { get; set; } = "";
    public string? OsVersion { get; set; }
    public string? Comment { get; set; }
    /// <summary>"writers"（VSS ライター連携＝アプリ整合）/ "crash"（クラッシュ整合）/ null（スナップショットなし）</summary>
    public string? Consistency { get; set; }
    /// <summary>既存のシャドウコピーから取ったときの、そのシャドウコピーの作成時刻（データの時点）。通常は null。</summary>
    public DateTime? SourceTime { get; set; }
    public List<VolumeRecord> Volumes { get; set; } = [];
    public List<DiskRecord> Disks { get; set; } = [];
    public PointStats Stats { get; set; } = new();
    public List<string> Warnings { get; set; } = [];

    /// <summary>ベアメタル復元に使える（システムを含むディスクの構成を持っている）。</summary>
    public bool BareMetalCapable => Disks.Count > 0 && Volumes.Any(v => v.IsSystem);
}

public sealed class PointStats
{
    public long UsedBytes { get; set; }
    public long ReadBytes { get; set; }
    public long NewBlocks { get; set; }
    public long NewBytes { get; set; }
    public long StoredBytes { get; set; }
    public double Seconds { get; set; }
}

public sealed class VolumeRecord
{
    public int Index { get; set; }
    /// <summary>"C:" など。ドライブ文字がなければ null。</summary>
    public string? Letter { get; set; }
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public string? VolumeGuid { get; set; }
    public long Size { get; set; }
    public int BlockSize { get; set; }
    public long UsedBytes { get; set; }
    public long BlockCount { get; set; }
    /// <summary>このボリュームがあるディスク（DiskRecord.Index）とディスク上の開始位置。</summary>
    public int? Disk { get; set; }
    public long? DiskOffset { get; set; }
    /// <summary>Windows が入っているボリューム。</summary>
    public bool IsSystem { get; set; }
    /// <summary>ブートローダーのあるボリューム（EFI システムパーティション / MBR のアクティブ）。</summary>
    public bool IsBoot { get; set; }
    public long UnreadableBytes { get; set; }

    public string Display => (Letter ?? (IsBoot ? "EFI" : T("(文字なし)"))) + (string.IsNullOrEmpty(Label) ? "" : $" [{Label}]") + $" {FileSystem}";
}

public sealed class DiskRecord
{
    public int Index { get; set; }
    public int? Number { get; set; }
    public string? Model { get; set; }
    public long Size { get; set; }
    public int SectorSize { get; set; } = 512;
    /// <summary>"GPT" / "MBR"</summary>
    public string Style { get; set; } = "GPT";
    public string? DiskGuid { get; set; }
    public uint? MbrSignature { get; set; }
    public List<PartitionRecord> Partitions { get; set; } = [];
}

public sealed class PartitionRecord
{
    public int Number { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    /// <summary>GPT: パーティション種別 GUID。</summary>
    public string? TypeGuid { get; set; }
    public string? PartitionGuid { get; set; }
    public string? Name { get; set; }
    public ulong Attributes { get; set; }
    /// <summary>MBR: パーティション種別バイト。</summary>
    public byte MbrType { get; set; }
    public bool Active { get; set; }
    /// <summary>バックアップしたボリューム（VolumeRecord.Index）。中身を保存しないパーティション（MSR など）は null。</summary>
    public int? Volume { get; set; }

    public const string EspType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    public const string MsrType = "e3c9e316-0b5c-4db8-817d-f92df00215ae";
    public const string BasicDataType = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7";
    public const string RecoveryType = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

    public string Kind => TypeGuid?.ToLowerInvariant() switch
    {
        EspType => T("EFI システム"),
        MsrType => T("MSR (予約)"),
        BasicDataType => T("ベーシックデータ"),
        RecoveryType => T("回復"),
        null => MbrType switch { 0x07 => "NTFS/exFAT", 0x0B or 0x0C => "FAT32", 0x27 => T("回復"), _ => T("種別 0x{0:X2}", MbrType) },
        _ => T("その他"),
    };
}

