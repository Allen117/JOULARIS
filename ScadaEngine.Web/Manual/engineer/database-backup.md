# 資料庫建立與備份

本章說明系統資料庫如何建立、應用帳號與權限怎麼設定、每週自動備份如何運作，以及災難時如何還原。

## 整體分工

| 功能 | 由誰執行 | 時機 | 需要的權限 |
|------|---------|------|-----------|
| 建立資料庫、應用帳號、備份資料夾權限、`engineer` 帳號 | `install-db.ps1` | 安裝時（`Install.bat` 步驟 [5/8] 自動執行） | 執行者為 SQL sysadmin + 本機系統管理員 |
| 資料庫不存在時自動建立（安全網） | Engine / Web 啟動時 | 資料庫不存在 | 應用帳號有 `dbcreator`（沒有就略過並記 log） |
| 建表、補欄位 | Engine / Web 啟動時 | 每次啟動 | 目標資料庫 `db_owner` |
| 每週完整備份 | Engine | 預設每週日 03:00 | 目標資料庫 `db_owner` |

原則是「安裝時用高權限、平常只用低權限」：sa 密碼不寫進任何設定檔，Engine 與 Web 平常只用 `dbSetting.json` 裡的應用帳號。

## 相關設定檔

### dbSetting.json（資料庫連線）

位置：`C:\JOULARIS\Shared\dbSetting.json`，Engine、Web、Modbus Gateway 共用。

```json
{
  "DatabaseAddress": "127.0.0.1",
  "DataBaseName": "wsnCsharp",
  "DataBaseAccount": "wsn",
  "DataBasePassword": "wsn"
}
```

修改後需重啟 `JoularisEngine` 與 `JoularisWeb`（有裝 Gateway 也一併重啟）。

### DbMaintenanceSetting.json（建庫路徑與備份排程）

位置：`C:\JOULARIS\Engine\App\Setting\DbMaintenanceSetting.json`

| 欄位 | 預設 | 說明 |
|------|------|------|
| `DataFileFolder` | `C:\JOULARIS\Database` | 自動建立資料庫時的資料檔位置（不影響既有資料庫） |
| `BackupFolder` | `C:\JOULARIS\Backup` | 備份檔位置 |
| `BackupEnabled` | `true` | 每週備份總開關 |
| `BackupDayOfWeek` | `Sunday` | `Sunday` ～ `Saturday` |
| `BackupTime` | `03:00` | 24 小時制 |

修改後需重啟 Engine 才生效（排程在服務啟動時讀取）。檔案不存在或格式錯誤時採用上述預設值。

## install-db.ps1 做了什麼

`Install.bat` 會自動執行，一般不需手動。腳本可重複執行（已完成的步驟會跳過）：

1. 偵測 SQL Server 服務帳號，建立 `Database` / `Backup` 資料夾並授予 SQL 服務帳號寫入權
2. 資料庫不存在 → 建立並設為 `RECOVERY SIMPLE`；已存在 → 跳過（若為 FULL 復原模式會印出警告，不自動修改）
3. 建立應用 SQL 登入帳號並授予目標資料庫 `db_owner`（已存在則跳過）
4. 建立工程師 bootstrap 帳號 `engineer`（console 輸入密碼，至少 8 碼；已有任何 Engineer 帳號則跳過）

腳本讀取的 dbSetting 順序：環境變數 `JOULARIS_DBSETTING` → `C:\JOULARIS\Shared\dbSetting.json` → 腳本同資料夾。

### 何時需要手動執行

- `Install.bat` 執行時腳本失敗（安裝不會中斷，只印警告與重跑指令）
- 既有站台的資料庫是手動建立的，需要補建備份資料夾與權限

手動執行（系統管理員 PowerShell）：

```powershell
powershell -ExecutionPolicy Bypass -File C:\JOULARIS\Engine\App\Setting\install-db.ps1
```

具名執行個體可加參數，例如 `-ServerInstance "localhost\SQLEXPRESS"`；非互動執行可用 `-EngineerPassword` 帶入密碼。

> ⚠️ 注意：自動建庫的安全網只建資料庫，**不會**建備份資料夾與權限。`install-db.ps1` 失敗時務必補跑，否則每週備份會因資料夾權限不足而失敗。

## 啟動時自動建表與補欄位

Engine 與 Web 每次啟動都會依 `DatabaseSchema.json` 檢查資料表：

- 缺表 → 自動建立
- 缺欄位 → 自動加上
- **只加不減不改**：多出來的欄位、型別不一致都不會被修改，只在 log 記警告

因此升級新版後，第一個啟動的服務會自動補齊新欄位，不需手動跑 SQL 腳本。

## 每週自動備份

- **輪替兩份**：`{資料庫名}_A.bak` 與 `{資料庫名}_B.bak` 交替覆寫，永遠保留「本次 + 上一次」
- **壓縮**：Standard / Enterprise 版自動壓縮；Express 版不壓縮
- **失敗重試**：當天每小時重試；跨日則等下週
- **停機錯過**：排定時間 Engine 沒在跑，該週略過；Engine 啟動時若發現最新備份超過 8 天會記警告
- **結果可查**：成功 / 失敗各寫一筆事件記錄（資訊類，SID 為 `_system`），可在 **歷史資料 → 事件記錄** 查詢

> ⚠️ 注意：備份檔由 **SQL Server 服務**寫入，資料夾權限是給 SQL 服務帳號而不是 Engine。資料庫在另一台主機時，`BackupFolder` 指的是**資料庫那台**的路徑。

> ⚠️ 注意：備份與資料庫在同一台同一顆硬碟，只能防資料庫損毀，**防不了硬碟故障**。請把 `C:\JOULARIS\Backup` 納入定期外拷（NAS / 異地）。

## 還原（災難復原）

1. 停止 `JoularisEngine`、`JoularisWeb`（有 Gateway 也一併停止）
2. 到 `C:\JOULARIS\Backup` 確認 `_A.bak` / `_B.bak` 哪一份較新
3. 以 sysadmin 在 SSMS 執行：

```sql
RESTORE DATABASE [wsnCsharp]
FROM DISK = N'C:\JOULARIS\Backup\wsnCsharp_A.bak'
WITH REPLACE;
```

4. 啟動服務，確認登入與即時資料正常

## 運維注意

- 系統只做每週完整備份，不做交易記錄備份；資料庫請維持 `RECOVERY SIMPLE`，否則交易記錄檔會持續膨脹
- SQL Server Express 單一資料庫上限 10 GB，長期資料量大的站台請評估升級版本
- 26.6.0 以前位於 `C:\SCADA` 的站台，遷移時資料檔在舊根目錄下的資料庫會一起搬到新根目錄，`DbMaintenanceSetting.json` 的路徑也會自動改寫，見 [部署與升級](deployment)
