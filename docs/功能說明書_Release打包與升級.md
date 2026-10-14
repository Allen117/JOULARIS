# Release 打包與升級說明

`BuildRelease.ps1`（開發機執行）→ 產出可攜的 `Release\SCADA_Release_yyyyMMdd_HHmm\` 資料夾 → 拷到 USB → 現場 `Install.bat`（系統管理員）安裝或升級。本文說明**哪些檔案會進包、升級時哪些會被保留、哪些會被換成新版**。

## 一、打包產物結構

```
SCADA_Release_yyyyMMdd_HHmm\
├── Install.bat              <- 現場安裝/升級 Engine + Web + LicenseBridge（右鍵以系統管理員執行）
├── InstallModbusServer.bat  <- 對外 Modbus Gateway 獨立安裝（不由 Install.bat 帶）
├── reset-engineer-password.ps1  <- 工程師工具，隨 USB 攜帶，不落地伺服器
├── Shared\                 <- dbSetting / EmailSetting / SmsSetting / LineSetting 範本 → 現場 C:\JOULARIS\Shared\（Engine / Web 共用一份，見 §共用設定檔遷移與回滾）
├── Engine\App\             <- Engine 執行檔 + 設定（Setting\ 不含上述四檔，只留 *.example.json）
├── Web\App\                <- Web 執行檔 + wwwroot + 設定
├── LicenseBridge\App\      <- net48 HASP 授權橋接（需 USB 加密狗）
└── ModbusServer\App\       <- 對外 Modbus TCP Gateway
```

四個專案各自 `dotnet publish -c Release --self-contained`（LicenseBridge 為 net48，靠目標機內建 .NET Framework 4.8）。

## 二、「現場資料夾」只帶產生工具，不帶 dev 裝置定義

Engine 的三個資料夾 `Modbus` / `DBPoint` / `OpcUaPoint` 裝的是**各站台自有的現場資料**（裝置點位定義），**不是產品**。但 `dotnet publish` 會依 [ScadaEngine.Engine.csproj](../ScadaEngine.Engine/ScadaEngine.Engine.csproj) 的 `CopyToOutputDirectory`，把 repo 內開發用的 `*.json` / `*.json.example` 一起帶進 `Engine\App`。若不處理，這些 dev 檔案會隨 release **外流到客戶伺服器**（首裝直接落地；升級因 `xcopy /Y` 只加不刪也會殘留）。

因此 `BuildRelease.ps1` 在 publish 之後，會把這三個資料夾**清空、只回填範本／產生工具**：

| 資料夾 | 保留（進包） | 移除（不進包） |
|--------|-------------|----------------|
| `Modbus/` | `Modbus範本.xlsx`（空白 Excel 範本） | 所有 `*.json` 裝置定義 |
| `DBPoint/` | `DB來源範本.xlsx`（空白 Excel 範本） | `*.json`、`*.sql` 測試檔 |
| `OpcUaPoint/` | `OPCUA通訊檔案產生工具.xlsm` | `*.json`、`*.json.example` 範例 |

- Modbus / DBPoint 保留 `*.xlsx` 範本：現場工程師用任何能編輯 xlsx 的軟體填好後，到 Web「Modbus 來源」「DB 來源」頁**匯入 Excel**（預覽差異 → 確認 → 自動生效），不再需要 Office 與巨集；舊 `*.xlsm` 巨集已於 2026-10 移除（舊 xlsm 檔仍可直接上傳）。見 docs/功能說明書_Modbus來源管理.md §4.1、_DB來源管理.md §8.4
- OpcUaPoint 仍保留 `*.xlsm` 產生工具（見 docs/功能說明書_OPCUA通訊.md）。
- 不帶 `*.json.example`：範例對現場無用，只增雜訊。
- **資料夾本身保留**（含工具）：`Install.bat` 靠「`C:\JOULARIS\Engine\App\Modbus` 資料夾是否存在」判斷升級與否（見下節），空目錄也必須在，否則第二次安裝會被誤判成首裝。

> Web 端這三個資料夾是 ProjectReference 遞移帶進來的**死檔**（Web 實讀 `C:\JOULARIS\Engine\App\...`），打包時直接整個刪除，不留。

## 三、Install.bat：首次安裝 vs 升級

安裝根目錄由 Install.bat 開頭的 **`set "_ROOT=C:\JOULARIS"` 一處定義**（26.6.0 起；以下路徑皆為 `%_ROOT%` 展開後的值），全腳本路徑由它推導。

**`[0/8]` 舊根遷移**：偵測到 `_OLDROOT`（`C:\SCADA\Engine\App`）存在、`%_ROOT%\Engine\App` 不存在時，先呼叫包內 `Engine\App\Setting\migrate-install-root.ps1` 把整個舊根搬到新根（服務 / SQL 資料檔 / 排程 / 設定檔一併改指），失敗即中止不升級；成功後下面的升級流程照常在 `%_ROOT%` 下進行。細節見 [部署與遠端更新 §安裝根目錄遷移](功能說明書_部署與遠端更新.md#安裝根目錄遷移cscada--cjoularis2660-起)。

Install.bat 以 **`C:\JOULARIS\Engine\App\Modbus` 資料夾是否存在**為哨兵判斷 `_IS_UPGRADE`。

### 升級流程（`_IS_UPGRADE=1`）：先備份 → 整包覆蓋 → 再還原

1. **備份現場設定**到 `C:\JOULARIS\_ConfigBackup`：
   - Engine：`Modbus`、`Setting`（舊佈局時含 dbSetting / EmailSetting / SmsSetting / LineSetting）、`MqttSetting`、`DBPoint`
   - Web：`Setting`、`MqttSetting`
2. **停服務** → `xcopy /Y` 用 release 包整包覆蓋 `Engine\App` / `Web\App` / `LicenseBridge`
3. **還原現場設定**蓋回步驟 2 的新版設定 → **現場設定最後勝出**
4. **四個共用設定檔遷移到 `C:\JOULARIS\Shared\`**（見下方 §共用設定檔遷移與回滾）→ 之後才跑 `install-db.ps1`

淨結果：

| 項目 | 升級後 |
|------|--------|
| Modbus / DBPoint 現場裝置定義 | **保留現場版**（release 空資料夾不會蓋掉；`*.xlsx` 範本更新為新版；舊版殘留的 `*.xlsm` 放著無害）；Web 匯入功能建立的 `_deleted/` 備份子資料夾隨整個資料夾一起備份／還原 |
| OpcUaPoint 現場定義 | **保留現場版**（不在備份清單，但 release 不帶 json、xcopy 不刪，故原封不動） |
| dbSetting / EmailSetting / SmsSetting / LineSetting | **保留現場版**，但位置改為 **`C:\JOULARIS\Shared\`**（自舊佈局升級時從 `Engine\App\Setting` 自動搬過去、Web 複本刪除，Web 複本內容不同時另存 `.web-backup`；`Shared\` 已存在則完全不動，不會被包內範本蓋掉） |
| MqttSetting.json | **保留現場版** |
| 程式執行檔、wwwroot | 換成 release 新版 |
| `Setting\*.ps1`（install-db 等腳本） | **一律換 release 新版**（腳本屬程式，不還原舊版） |
| `DatabaseSchema.json` | 換 release 新版（schema 唯一真相來源，只加不減） |
| HTTPS 憑證 `certs\*.pfx` | 沿用既有，不重產（見 docs/功能說明書_內網HTTPS部署.md） |

> ⚠️ 因此**升級不會**把你在 `C:\JOULARIS` 上的 DB 連線與 Modbus 裝置設定換成 release 包裡的預設值。

### 共用設定檔遷移與回滾

`C:\JOULARIS\Shared\` 下四檔是 Engine / Web **共用的唯一一份**（dbSetting 另含 Gateway；解析順序見 [架構.md §共用設定檔解析順序](架構.md#共用設定檔解析順序)）：

| 檔案 | 用途 | 誰寫 |
|---|---|---|
| `dbSetting.json` | DB 連線 | 手動 / install-db.ps1 |
| `EmailSetting.json` | SMTP | Web「警報設定 → Email」（存檔後 Engine MQTT 熱重載） |
| `SmsSetting.json` | 簡訊盒 | Web「警報設定 → 簡訊」（存檔後 Engine 熱重載 + 重掃序列埠） |
| `LineSetting.json` | Line token | 手動（含機密，不進 git；包內為 placeholder） |

它們在所有 `<App>\App\` 之外，所以升級的「整包覆蓋」天然碰不到它，也不在備份／還原清單裡。Install.bat 在還原現場設定之後、`install-db.ps1` 之前，對四檔**逐一**執行（子程序 `:MIGRATE_SHARED`）：

| 現場狀態 | Install.bat 動作 |
|---------|-----------------|
| `Shared\<檔>` 已存在 | 不動（包內範本**不會**覆蓋） |
| 不存在，但 `Engine\App\Setting\<檔>` 存在（舊佈局升級，剛從備份還原） | `move` 到 `Shared\`（印 `[MIGRATE]`）。**若 `Web\App\Setting\<檔>` 也存在且內容不同**（`fc /B`），印 `[WARN]` 並把 Web 那份另存 **`Shared\<檔>.web-backup`** 供人工合併 —— 通知三檔以前 Web UI 會寫進 Web 複本，Web 那份才可能是最新 |
| 只有 `Web\App\Setting\<檔>` | `move` Web 那份到 `Shared\` |
| 都不存在（全新安裝） | 把包內 `Shared\<檔>` 範本 `copy` 過去（Email / Sms 為 example 內容、Line 為 placeholder token → Engine 視為未設定、該通道安全停用） |
| 收尾 | 一律 `del` `Engine\App\Setting\<檔>` 與 `Web\App\Setting\<檔>` 殘留，避免現場改到一份永遠不被讀的檔 |

再跑一次升級：`Shared\` 四檔已存在 → 全部不動，內容不變（idempotent）。

⚠️ 升級後若看到 `[WARN] ... .web-backup`，請人工 diff `Shared\EmailSetting.json` 與 `.web-backup`，把 Web 那份較新的 SMTP 帳密合併進去（或直接在 Web 設定頁重存一次），確認後刪掉 `.web-backup`。

⚠️ Web 服務帳號需對 `C:\JOULARIS\Shared\` 有寫入權（`sc create` 預設 LocalSystem 無問題；若現場改成受限帳號，要對該資料夾另授權，否則 Web 存 SMTP / 簡訊設定會回「儲存失敗」）。

**回滾到 Shared 之前的舊版 Release 包**：舊版 Engine / Web 只讀自己的 `App\Setting\`，而新版已把檔搬走，直接跑舊版 Install.bat 會讓兩個 app 讀到包內預設值。步驟：

1. 先把 `C:\JOULARIS\Shared\` 下要回滾的檔 `copy` 到 `C:\JOULARIS\Engine\App\Setting\` 與 `...\Web\App\Setting\`（兩份都要）：回滾到 26.4.x 以前只需 dbSetting；回滾到 26.5.x 以前四檔都要
2. 再跑舊版 Install.bat（它會備份／還原這些複本）
3. `Shared\` 資料夾留著無害；之後再升回新版會偵測到 `Shared\` 已存在而直接沿用

### 首次安裝（`_IS_UPGRADE=0`）

無備份/還原；release 包內容直接落地，`C:\SCADA\Shared\` 四檔由包內範本寫入（dbSetting 現場改這一份即可，三個 app 共用；Email / Sms 到 Web 警報設定頁填、Line 手動填 token）。Modbus / DBPoint 只有 `*.xlsx` 空白範本、OpcUaPoint 只有 `*.xlsm` 工具，無任何裝置定義 → Modbus / DB 由現場工程師填範本後在 Web 匯入（自動寫 `*.json`、免重啟）；OPC UA 用工具產生 `*.json` 後放入對應資料夾。

## 四、ModbusServer Gateway（獨立）

`InstallModbusServer.bat` 與 Install.bat **解耦**，需另外執行。升級同樣備份/還原現場 `Setting`、`MqttSetting`，並特別保留執行期產生的 **`AddressMap.json`**（對外 Modbus 位址對照，append-only，絕不可丟）。

dbSetting.json：包內只帶 `Setting\dbSetting.example.json`。安裝時若同機已有 `C:\JOULARIS\Shared\dbSetting.json`（與 Engine / Web 同機）就直接共用、並刪掉本機 `Setting\dbSetting.json` 殘留；若沒有（Gateway 裝在另一台機器）則從 example 鋪一份 `Setting\dbSetting.json` 並提示填 SQL Server IP / 帳密。詳見 docs/功能說明書_ModbusServerGateway.md。

## 相關檔案

- [BuildRelease.ps1](../BuildRelease.ps1) — 打包腳本（Install.bat / InstallModbusServer.bat 由它內嵌產生）
- [ScadaEngine.Engine.csproj](../ScadaEngine.Engine/ScadaEngine.Engine.csproj) — `CopyToOutputDirectory` 決定 publish 帶哪些設定
- 根目錄 `QuickDeployAll.bat` — 一鍵「打包 → 安裝」
