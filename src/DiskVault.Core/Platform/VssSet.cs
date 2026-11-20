using System.Runtime.InteropServices;

namespace DiskVault.Platform;

/// <summary>
/// VSS ライターと連携したスナップショットセット（アプリ整合）。IVssBackupComponents を直接使う（vshadow と同じ手順）。
///   InitializeForBackup → SetBackupState → GatherWriterMetadata → StartSnapshotSet → AddToSnapshotSet（全ボリューム）
///   → PrepareForBackup → DoSnapshotSet → GatherWriterStatus → …バックアップ… → BackupComplete
/// コンテキストは VSS_CTX_BACKUP（非永続）なので、プロセスが落ちてもスナップショットは自動で消える。
/// </summary>
public sealed class VssSet : IDisposable
{
    readonly IVssBackupComponents _c;
    Guid _setId;
    readonly Dictionary<string, string> _devices = new(StringComparer.OrdinalIgnoreCase);
    bool _completed, _disposed;

    /// <summary>ライターの状態（失敗したものは警告にする）。</summary>
    public List<(string Writer, string State, int Error)> Writers { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>元のボリューム名（\\?\Volume{..}\）→ スナップショットのデバイス。</summary>
    public string DeviceFor(string volume) => _devices[Norm(volume)];

    static string Norm(string v) => v.EndsWith('\\') ? v : v + "\\";

    VssSet(IVssBackupComponents c, Guid setId) { _c = c; _setId = setId; }

    /// <summary>
    /// ライター連携のスナップショットを作る。fullBackup = true なら「VSS 完全バックアップ」（SQL Server・Exchange のログを切り捨てる）、
    /// false なら「コピー」（ほかのバックアップに影響しない）。
    /// </summary>
    public static VssSet Create(IReadOnlyList<string> volumes, bool fullBackup, Action<string>? log = null)
    {
        log ??= _ => { };
        if (!Native.IsAdmin()) throw new UnauthorizedAccessException(T("スナップショットの作成には管理者権限が必要です。管理者として実行してください。"));
        Check(CreateVssBackupComponentsInternal(out var c), T("VSS の初期化"));
        var set = new VssSet(c, Guid.Empty);
        try
        {
            Check(c.InitializeForBackup(null), "InitializeForBackup");
            Check(c.SetContext(0 /* VSS_CTX_BACKUP */), "SetContext");
            Check(c.SetBackupState(false, true, fullBackup ? 1 /* VSS_BT_FULL */ : 5 /* VSS_BT_COPY */, false), "SetBackupState");
            log(T("VSS ライターの情報を集めています…"));
            Run(c.GatherWriterMetadata(out var a1), a1, "GatherWriterMetadata");
            Check(c.StartSnapshotSet(out var setId), "StartSnapshotSet");
            set._setId = setId;
            var snapIds = new List<(string Vol, Guid Id)>();
            foreach (var v in volumes.Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Check(c.AddToSnapshotSet(v, Guid.Empty, out var sid), $"AddToSnapshotSet {v}");
                snapIds.Add((v, sid));
            }
            log(T("ライターにバックアップの準備をさせています…"));
            Run(c.PrepareForBackup(out var a2), a2, "PrepareForBackup");
            set.CollectWriterStatus();
            log(T("スナップショットを作成しています（{0} ボリューム同時）…", snapIds.Count));
            Run(c.DoSnapshotSet(out var a3), a3, "DoSnapshotSet");
            set.CollectWriterStatus();
            foreach (var (vol, sid) in snapIds)
            {
                Check(c.GetSnapshotProperties(sid, out var prop), "GetSnapshotProperties");
                try { set._devices[vol] = Marshal.PtrToStringUni(prop.SnapshotDeviceObject)!; }
                finally { VssFreeSnapshotPropertiesInternal(ref prop); }
            }
            var ok = set.Writers.Count(w => w.Error == 0 && (w.State == StateText(1) || w.State == StateText(5)));
            log(T("VSS ライター: {0} 個（正常 {1}）{2}", set.Writers.Count, ok, set.Warnings.Count > 0 ? T("、問題あり {0}", set.Warnings.Count) : ""));
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    void CollectWriterStatus()
    {
        Run(_c.GatherWriterStatus(out var a), a, "GatherWriterStatus");
        Check(_c.GetWriterStatusCount(out uint n), "GetWriterStatusCount");
        Writers.Clear();
        for (uint i = 0; i < n; i++)
        {
            if (_c.GetWriterStatus(i, out _, out _, out var name, out int state, out int failure) != 0) continue;
            var st = StateText(state);
            Writers.Add((name ?? "?", st, failure));
            if (failure != 0 || state is 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15)
            {
                var msg = T("VSS ライター「{0}」が失敗しました（{1}、0x{2:X8}）。このアプリのデータはクラッシュ整合になります。", name, st, failure);
                if (!Warnings.Contains(msg)) Warnings.Add(msg);
            }
        }
        _c.FreeWriterStatus();
    }

    /// <summary>Writers の State（表示用の文字列）が失敗状態か。</summary>
    public static bool IsFailedState(string state) => FailedStates.Any(s => StateText(s) == state);
    static readonly int[] FailedStates = [7, 8, 9, 10, 11, 12, 13, 14, 15];

    static string StateText(int s) => s switch
    {
        1 => T("安定"), 2 => T("フリーズ待ち"), 3 => T("書き込み抑止待ち"), 4 => T("書き込み再開待ち"), 5 => T("完了待ち"),
        6 => T("完了待ち"), 7 => T("フリーズ前に失敗"), 8 => T("フリーズ時に失敗"), 9 => T("書き込み抑止時に失敗"), 10 => T("再開時に失敗"),
        11 => T("完了処理で失敗"), 12 => T("失敗"), 13 => T("復元前に失敗"), 14 => T("復元後に失敗"), 15 => T("バックアップ終了時に失敗"),
        _ => T("状態 {0}", s),
    };

    /// <summary>バックアップが成功したことをライターに伝える（VSS 完全バックアップならここでログが切り捨てられる）。</summary>
    public void BackupComplete()
    {
        if (_completed) return;
        _completed = true;
        try { Run(_c.BackupComplete(out var a), a, "BackupComplete"); }
        catch (IOException e) { Warnings.Add(e.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_completed) _c.AbortBackup();
            if (_setId != Guid.Empty) _c.DeleteSnapshots(_setId, 2 /* VSS_OBJECT_SNAPSHOT_SET */, true, out _, out _);
        }
        catch (COMException) { }
        Marshal.ReleaseComObject(_c);
    }

    // ---- COM ----

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new IOException(T("VSS（{0}）が失敗しました: {1}", what, Describe(hr)));
    }

    /// <summary>非同期の VSS 処理を完了まで待つ。</summary>
    static void Run(int hr, IVssAsync? async, string what)
    {
        Check(hr, what);
        if (async == null) return;
        try
        {
            Check(async.Wait(uint.MaxValue), what);
            Check(async.QueryStatus(out int result, 0), what);
            Check(result, what);
        }
        finally { Marshal.ReleaseComObject(async); }
    }

    static string Describe(int hr) => (uint)hr switch
    {
        0x80042302 => T("VSS サービスでエラーが起きました"),
        0x80042308 => T("指定したオブジェクトがありません"),
        0x8004230C => T("このボリュームはスナップショットに対応していません"),
        0x8004230F => T("スナップショット プロバイダーでエラーが起きました"),
        0x80042312 => T("スナップショットの数が上限に達しています"),
        0x80042316 => T("別のスナップショット処理が実行中です"),
        0x80042317 => T("スナップショット数の上限です"),
        0x8004231F => T("スナップショット用の領域が足りません"),
        0x80042318 => T("ライターがタイムアウトしました"),
        0x8004230A => T("ライターの状態が不正です"),
        0x80070005 => T("アクセスが拒否されました"),
        _ => $"0x{hr:X8}",
    };

    [DllImport("vssapi.dll", ExactSpelling = true)]
    static extern int CreateVssBackupComponentsInternal(out IVssBackupComponents components);

    [DllImport("vssapi.dll", ExactSpelling = true)]
    static extern void VssFreeSnapshotPropertiesInternal(ref VssSnapshotProp prop);

    [StructLayout(LayoutKind.Sequential)]
    struct VssSnapshotProp
    {
        public Guid SnapshotId, SnapshotSetId;
        public int SnapshotsCount;
        public nint SnapshotDeviceObject, OriginalVolumeName, OriginatingMachine, ServiceMachine, ExposedName, ExposedPath;
        public Guid ProviderId;
        public int SnapshotAttributes;
        public long CreationTimestamp;
        public int Status;
    }

    [ComImport, Guid("507C37B4-CF5B-4e95-B0AF-14EB9767467E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVssAsync
    {
        [PreserveSig] int Cancel();
        [PreserveSig] int Wait(uint milliseconds);
        [PreserveSig] int QueryStatus(out int hrResult, int reserved);
    }

    /// <summary>vsbackup.h の IVssBackupComponents（vtable の順番どおり。使わないメソッドは枠だけ）。</summary>
    [ComImport, Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVssBackupComponents
    {
        [PreserveSig] int GetWriterComponentsCount(out uint count);
        [PreserveSig] int GetWriterComponents(uint index, out nint components);
        [PreserveSig] int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? xml);
        [PreserveSig] int SetBackupState([MarshalAs(UnmanagedType.U1)] bool selectComponents, [MarshalAs(UnmanagedType.U1)] bool bootableSystemState, int backupType, [MarshalAs(UnmanagedType.U1)] bool partialFileSupport);
        [PreserveSig] int InitializeForRestore([MarshalAs(UnmanagedType.BStr)] string xml);
        [PreserveSig] int SetRestoreState(int restoreType);
        [PreserveSig] int GatherWriterMetadata(out IVssAsync async);
        [PreserveSig] int GetWriterMetadataCount(out uint count);
        [PreserveSig] int GetWriterMetadata(uint index, out Guid instanceId, out nint metadata);
        [PreserveSig] int FreeWriterMetadata();
        [PreserveSig] int AddComponent(Guid instanceId, Guid writerId, int componentType, [MarshalAs(UnmanagedType.LPWStr)] string? logicalPath, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PrepareForBackup(out IVssAsync async);
        [PreserveSig] int AbortBackup();
        [PreserveSig] int GatherWriterStatus(out IVssAsync async);
        [PreserveSig] int GetWriterStatusCount(out uint count);
        [PreserveSig] int FreeWriterStatus();
        [PreserveSig] int GetWriterStatus(uint index, out Guid instanceId, out Guid writerId, [MarshalAs(UnmanagedType.BStr)] out string name, out int state, out int failure);
        [PreserveSig] int SetBackupSucceeded();
        [PreserveSig] int SetBackupOptions();
        [PreserveSig] int SetSelectedForRestore();
        [PreserveSig] int SetRestoreOptions();
        [PreserveSig] int SetAdditionalRestores();
        [PreserveSig] int SetPreviousBackupStamp();
        [PreserveSig] int SaveAsXML([MarshalAs(UnmanagedType.BStr)] out string xml);
        [PreserveSig] int BackupComplete(out IVssAsync async);
        [PreserveSig] int AddAlternativeLocationMapping();
        [PreserveSig] int AddRestoreSubcomponent();
        [PreserveSig] int SetFileRestoreStatus();
        [PreserveSig] int AddNewTarget();
        [PreserveSig] int SetRangesFilePath();
        [PreserveSig] int PreRestore(out IVssAsync async);
        [PreserveSig] int PostRestore(out IVssAsync async);
        [PreserveSig] int SetContext(int context);
        [PreserveSig] int StartSnapshotSet(out Guid snapshotSetId);
        [PreserveSig] int AddToSnapshotSet([MarshalAs(UnmanagedType.LPWStr)] string volumeName, Guid providerId, out Guid snapshotId);
        [PreserveSig] int DoSnapshotSet(out IVssAsync async);
        [PreserveSig] int DeleteSnapshots(Guid sourceObjectId, int sourceObjectType, [MarshalAs(UnmanagedType.Bool)] bool forceDelete, out int deletedSnapshots, out Guid nonDeletedSnapshotId);
        [PreserveSig] int ImportSnapshots(out IVssAsync async);
        [PreserveSig] int BreakSnapshotSet(Guid snapshotSetId);
        [PreserveSig] int GetSnapshotProperties(Guid snapshotId, out VssSnapshotProp prop);
    }
}

