# DiskVault

**Disk-image backup and bare-metal recovery for Windows** — full / incremental image backups from VSS snapshots, schedules, e-mail notifications, and a WinPE recovery USB/ISO that restores the whole disk (including Windows) onto new hardware.

[日本語の説明はこちら ↓](#diskvault-イメージバックアップ)

> The user interface is available in English and Japanese (Settings → Display language; "Auto" follows the Windows language).

## Features

- **Full / incremental backups** from VSS snapshots. Only NTFS clusters in use are read (256 KiB blocks), deduplicated, compressed with zstd, and optionally encrypted with AES-256-GCM.
- **Restore points**: every point is a synthetic full and can be restored on its own. Old points are pruned by a retention count and unused data is garbage-collected.
- **Schedules**: incrementals daily (selected weekdays) or every N hours; fulls weekly or monthly; runs missed while the PC was off are run once at the next start.
- **Destinations**: local drives, USB disks and NAS (SMB shares with stored credentials).
- **E-mail notifications** via SMTP (STARTTLS / SSL), always or only on warnings and failures.
- **Restore**: single files, whole volumes, or the whole disk (bare-metal, GPT/MBR with partition GUIDs). NTFS partitions can be grown or shrunk, and moved (drag the partition bar in the UI); boot sectors are fixed up accordingly.
- **VSS writers**: application-consistent snapshots (SQL Server etc.) with fallback to crash-consistent.
- **Back up from existing shadow copies**: pick a shadow copy Windows already keeps (System Restore points, Previous Versions) and back up the disk as it was at that moment — e.g. from before a bad update or a malware infection (dvault shadows, --shadow <ID>, or "From a shadow copy…" on the dashboard).
- **Recovery media**: builds a WinPE USB stick or ISO with the Windows ADK; it boots straight into DiskVault's recovery UI, which can also take an offline full backup of the stopped Windows disk.
- **Service + UI + CLI**: a LocalSystem service runs the schedules; `DiskVault.exe` is the UI and `dvault` is the command-line tool.

## Install

Download the MSI from [Releases](https://github.com/YomoKinaAz/DiskVault/releases). Requires Windows 10 2004 or later (x64). The .NET runtime is included.

## Build

```powershell
.\build.ps1              # self-contained build into dist\ and run the selftest
.\build.ps1 -Installer   # also build out\DiskVault-<version>-x64.msi (WiX 6)
```

Requires the .NET 10 SDK. `dvault selftest` needs no admin rights; the tests under `tests\` that touch real disks or VSS must be run as administrator, and the NTFS tests use ntfs-3g in WSL.

## Code signing policy

Release binaries are built from this repository by GitHub Actions ([build.yml](.github/workflows/build.yml)). We intend to apply for free code signing from the SignPath Foundation; until it is approved, releases are unsigned.

- Committers and reviewers: [YomoKinaAz](https://github.com/YomoKinaAz)
- Approvers: [YomoKinaAz](https://github.com/YomoKinaAz)

**Privacy**: this program does not transfer any information to other networked systems unless specifically requested by the user (backups to a NAS you configure, e-mail notifications through your SMTP server, and downloading the Windows ADK from Microsoft when you create recovery media). See the [privacy policy](https://yomokinaaz.github.io/DiskVault/privacy.html).

## License

[MIT](LICENSE). Third-party components: .NET (MIT), ZstdSharp (MIT), MailKit / MimeKit (MIT). The installer is built with WiX Toolset.

---

# DiskVault イメージバックアップ

Windows 用のディスクイメージ バックアップです。

- **フル／増分バックアップ**: VSS スナップショットから、NTFS の使用中ブロックだけを 256KiB 単位で読みます。重複排除と zstd 圧縮をかけ、AES-256 で暗号化することもできます。
- **復元ポイント**: どのポイントも単独で完全に復元できます（合成フル）。古いポイントは保持数に従って自動で削除し、使われなくなったデータを回収します。
- **スケジュール**: 増分は「毎日・曜日指定」または「N 時間ごと」、フルは「毎週」または「毎月」で指定します。電源オフで実行できなかった回は、次の起動時に 1 回だけ実行します。
- **メール通知**: SMTP（STARTTLS / SSL）で送ります。毎回送るか、警告・失敗のときだけ送るかを選べます。
- **復元**: ファイル単位・ボリューム単位・ディスク全体（ベアメタル復元。GPT/MBR とパーティション GUID を含む）で復元できます。ディスク全体の復元では NTFS パーティションを広げる（復元後に FSCTL_EXTEND_VOLUME）ことも、縮める（使用中データが収まる範囲でオフライン書き換え）こともでき、パーティションの開始位置も動かせます（GUI ではバーをマウスでドラッグ）。開始位置が変わったときはブートセクタの開始セクタ（hidden sectors）も書き換えます。
- **既存のシャドウコピーからバックアップ**: Windows に残っているシャドウコピー（システムの復元ポイントや「以前のバージョン」）を選んで、その時点の内容でバックアップします。更新やウイルスで壊れる前の状態を取っておけます（ダッシュボードの「シャドウコピーから…」、dvault shadows と --shadow <ID>）。
- **VSS ライター連携**: IVssBackupComponents で全ボリュームを 1 つのスナップショットセットにし、SQL Server などのライターと連携します（アプリ整合）。失敗したらクラッシュ整合に切り替えて警告を残します。「VSS 完全バックアップ」を選ぶとトランザクションログを切り捨てます（既定はコピー）。
- **回復メディア**: Windows ADK + WinPE アドオンで、USB メモリまたは ISO を作ります。起動すると DiskVault の回復画面が開きます。作り終えたら、同じ画面の「アンインストール」（`dvault media --uninstall-adk`）で ADK と WinPE アドオンを削除できます。
- **回復環境からのバックアップ**: 回復メディアで起動し、「バックアップ」画面（文字版は `dvault recover` の 6）から、停止中の Windows のディスク全体をフルバックアップできます。スナップショットは使わずに直接読み、`<保存先>\<PC 名>\回復環境` に保存します（PC 名はオフラインのレジストリから取得）。

## 構成

| フォルダ | 内容 |
|---|---|
| `src/DiskVault.Core` | 保存形式（Storage）、NTFS 読み取り（Ntfs）、バックアップ／復元エンジン（Engine）、VSS・ディスク・GPT・回復メディア（Platform）、ジョブ・スケジュール・メール・IPC（Jobs） |
| `src/DiskVault.App` | GUI（WPF、`DiskVault.exe`）。起動時に自分で管理者に昇格します。`--recovery` で回復画面になります（WinPE では自動） |
| `src/DiskVault.Service` | サービス `DiskVault`（LocalSystem、`DiskVaultService.exe install / uninstall / --console`） |
| `src/DiskVault.Cli` | `dvault`（バックアップ・復元・検証・回復メディア作成・`recover`・`selftest`） |

データ（設定・履歴・ログ）は `%ProgramData%\DiskVault` に置きます（Administrators と SYSTEM だけが読めます）。

### 保存形式（バックアップ先）

```
<保存先>\<PC名>\<ジョブ>\
  repo.json                        ブロックサイズ・圧縮レベル・暗号化（PBKDF2 で包んだ鍵）
  chains\<chain>\packs\xx\*.pack   ブロック本体（zstd、任意で AES-GCM）。*.idx は索引（pack から作り直せる）
  points\<id>\point.json           復元ポイント（ボリューム・ディスク構成・統計）。最後に書く＝確定
  points\<id>\vol<N>.map           ブロック番号 → ブロック ID（SHA-256／暗号化時は HMAC）
```

「フル」は新しいチェーン（データ群）を作り、「増分」は同じチェーンに変わったブロックだけを追加します。

## ビルド

```powershell
.\build.ps1          # dist\ に自己完結版（GUI・CLI・サービス）を出力し、selftest を実行
```

## テスト

- `dvault selftest` … 保存形式・暗号化・重複排除・世代整理・破損検出・GPT ディスクの復元・スケジュール・ジョブ・メール（ローカルの疑似 SMTP）・スケジューラー・パイプ制御（管理者権限は不要）
- `tests\ntfs_test.ps1` … WSL の ntfs-3g で作った NTFS で「フル → 増分 × 2」を取り、各ポイントの復元をマウントしてハッシュで照合し、ファイル単位の取り出しも確認
- `tests\resize_test.ps1` … 復元時の NTFS 縮小・パーティション拡張（ntfsresize --check とハッシュ照合）
- `tests\admin_vss_resize.ps1` … 管理者として実行。VSS ライター連携のスナップショットと、VHDX への拡張／縮小復元（chkdsk・ハッシュ照合）
- `tests\live_admin.ps1` … 管理者として実行。この PC の EFI・回復パーティションを VSS からバックアップ・復元し、回復メディアの ISO を作る
- `tests\vm.py`, `tests\make_bmr.py` … WSL の QEMU（KVM + OVMF）で回復メディアを起動し、ベアメタル復元を確認

## 制限

- 増分は使用中ブロックを毎回読み、ハッシュで変化を判定します（変更追跡ドライバはありません）。書き込むのは変わった分だけですが、読み取りはボリューム全体になります。
- NTFS の縮小は「最後の使用中クラスタ」までしか縮められません（データの移動・デフラグはしません）。FAT/exFAT のパーティションはサイズを変えられません。
- ダイナミックディスク・記憶域スペース・MBR の論理ドライブには対応していません。BitLocker のボリュームは復号された状態でバックアップされ、復元後は暗号化されていません。
- ファイル単位の復元は NTFS だけに対応しています（EFS で暗号化されたファイルは取り出せません）。

