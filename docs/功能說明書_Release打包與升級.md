# Release 打包與升級說明

`BuildRelease.ps1`（開發機執行）→ 產出可攜的 `Release\SCADA_Release_yyyyMMdd_HHmm\` 資料夾 → 拷到 USB → 現場 `Install.bat`（系統管理員）安裝或升級。本文說明**哪些檔案會進包、升級時哪些會被保留、哪些會被換成新版**。

## 一、打包產物結構

```
SCADA_Release_yyyyMMdd_HHmm\
├── Install.bat              <- 現場安裝/升級 Engine + Web + LicenseBridge（右鍵以系統管理員執行）
├── InstallModbusServer.bat  <- 對外 Modbus Gateway 獨立安裝（不由 Install.bat 帶）
├── reset-engineer-password.ps1  <- 工程師工具，隨 USB 攜帶，不落地伺服器
├── Shared\dbSetting.json   <- DB 連線範本 → 現場 C:\SCADA\Shared\（Engine / Web / Gateway 共用一份，見 §dbSetting.json 遷移與回滾）
├── Engine\App\             <- Engine 執行檔 + 設定（Setting\ 不含 dbSetting.json）
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
- **資料夾本身保留**（含工具）：`Install.bat` 靠「`C:\SCADA\Engine\App\Modbus` 資料夾是否存在」判斷升級與否（見下節），空目錄也必須在，否則第二次安裝會被誤判成首裝。

> Web 端這三個資料夾是 ProjectReference 遞移帶進來的**死檔**（Web 實讀 `C:\SCADA\Engine\App\...`），打包時直接整個刪除，不留。

## 三、Install.bat：首次安裝 vs 升級

Install.bat 以 **`C:\SCADA\Engine\App\Modbus` 資料夾是否存在**為哨兵判斷 `_IS_UPGRADE`。

### 升級流程（`_IS_UPGRADE=1`）：先備份 → 整包覆蓋 → 再還原

1. **備份現場設定**到 `C:\SCADA\_ConfigBackup`：
   - Engine：`Modbus`、`Setting`（舊佈局時含 dbSetting.json）、`MqttSetting`、`DBPoint`
   - Web：`Setting`、`MqttSetting`
2. **停服務** → `xcopy /Y` 用 release 包整包覆蓋 `Engine\App` / `Web\App` / `LicenseBridge`
3. **還原現場設定**蓋回步驟 2 的新版設定 → **現場設定最後勝出**
4. **dbSetting.json 遷移到 `C:\SCADA\Shared\`**（見下方 §dbSetting.json 遷移與回滾）→ 之後才跑 `install-db.ps1`

淨結果：

| 項目 | 升級後 |
|------|--------|
| Modbus / DBPoint 現場裝置定義 | **保留現場版**（release 空資料夾不會蓋掉；`*.xlsx` 範本更新為新版；舊版殘留的 `*.xlsm` 放著無害）；Web 匯入功能建立的 `_deleted/` 備份子資料夾隨整個資料夾一起備份／還原 |
| OpcUaPoint 現場定義 | **保留現場版**（不在備份清單，但 release 不帶 json、xcopy 不刪，故原封不動） |
| dbSetting.json | **保留現場版**，但位置改為 **`C:\SCADA\Shared\dbSetting.json`**（自舊佈局升級時從 `Engine\App\Setting` 自動搬過去、Web 複本刪除；`Shared\` 已存在則完全不動，不會被包內範本蓋掉） |
| MqttSetting.json | **保留現場版** |
| 程式執行檔、wwwroot | 換成 release 新版 |
| `Setting\*.ps1`（install-db 等腳本） | **一律換 release 新版**（腳本屬程式，不還原舊版） |
| `DatabaseSchema.json` | 換 release 新版（schema 唯一真相來源，只加不減） |
| HTTPS 憑證 `certs\*.pfx` | 沿用既有，不重產（見 docs/功能說明書_內網HTTPS部署.md） |

> ⚠️ 因此**升級不會**把你在 `C:\SCADA` 上的 DB 連線與 Modbus 裝置設定換成 release 包裡的預設值。

### dbSetting.json 遷移與回滾

`C:\SCADA\Shared\dbSetting.json` 是 Engine / Web / Gateway **共用的唯一一份** DB 連線設定（解析順序見 [架構.md §dbSetting.json 解析順序](架構.md#dbsettingjson-解析順序)）。它在所有 `<App>\App\` 之外，所以升級的「整包覆蓋」天然碰不到它，也不在備份／還原清單裡。Install.bat 在還原現場設定之後、`install-db.ps1` 之前執行：

| 現場狀態 | Install.bat 動作 |
|---------|-----------------|
| `Shared\dbSetting.json` 已存在 | 不動（包內範本**不會**覆蓋） |
| 不存在，但 `Engine\App\Setting\dbSetting.json` 存在（舊佈局升級，剛從備份還原） | `move` 到 `Shared\`（印 `[MIGRATE]`） |
| 都不存在（全新安裝） | 把包內 `Shared\dbSetting.json` 範本 `copy` 過去 |
| 收尾 | 一律 `del` `Engine\App\Setting\dbSetting.json` 與 `Web\App\Setting\dbSetting.json` 殘留，避免現場改到一份永遠不被讀的檔 |

⚠️ 舊佈局若 Web 複本與 Engine 複本內容**不同**（刻意讓 Web 連別的 DB），升級後以 **Engine 那份**為準統一；升級前請人工 diff 確認。

**回滾到 Shared 之前的舊版 Release 包**（26.3.x 以前）：舊版 Engine / Web 只讀自己的 `App\Setting\dbSetting.json`，而新版已把它搬走，直接跑舊版 Install.bat 會讓兩個 app 讀到包內預設值。步驟：

1. 先 `copy C:\SCADA\Shared\dbSetting.json C:\SCADA\Engine\App\Setting\` 與 `...\Web\App\Setting\`（兩份都要）
2. 再跑舊版 Install.bat（它會備份／還原這兩份）
3. `Shared\` 資料夾留著無害；之後再升回新版會偵測到 `Shared\` 已存在而直接沿用

### 首次安裝（`_IS_UPGRADE=0`）

無備份/還原；release 包內容直接落地，`C:\SCADA\Shared\dbSetting.json` 由包內範本寫入（現場改這一份即可，三個 app 共用）。Modbus / DBPoint 只有 `*.xlsx` 空白範本、OpcUaPoint 只有 `*.xlsm` 工具，無任何裝置定義 → Modbus / DB 由現場工程師填範本後在 Web 匯入（自動寫 `*.json`、免重啟）；OPC UA 用工具產生 `*.json` 後放入對應資料夾。

## 四、ModbusServer Gateway（獨立）

`InstallModbusServer.bat` 與 Install.bat **解耦**，需另外執行。升級同樣備份/還原現場 `Setting`、`MqttSetting`，並特別保留執行期產生的 **`AddressMap.json`**（對外 Modbus 位址對照，append-only，絕不可丟）。

dbSetting.json：包內只帶 `Setting\dbSetting.example.json`。安裝時若同機已有 `C:\SCADA\Shared\dbSetting.json`（與 Engine / Web 同機）就直接共用、並刪掉本機 `Setting\dbSetting.json` 殘留；若沒有（Gateway 裝在另一台機器）則從 example 鋪一份 `Setting\dbSetting.json` 並提示填 SQL Server IP / 帳密。詳見 docs/功能說明書_ModbusServerGateway.md。

## 相關檔案

- [BuildRelease.ps1](../BuildRelease.ps1) — 打包腳本（Install.bat / InstallModbusServer.bat 由它內嵌產生）
- [ScadaEngine.Engine.csproj](../ScadaEngine.Engine/ScadaEngine.Engine.csproj) — `CopyToOutputDirectory` 決定 publish 帶哪些設定
- 根目錄 `QuickDeployAll.bat` — 一鍵「打包 → 安裝」
