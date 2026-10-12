# dbSetting.json 單一真相來源：Engine / Web / ModbusServer 共用一份

**狀態**: 進行中 <!-- 進行中 | 已完成 | 廢棄 -->
**建立**: 2026-10-06
**最後更新**: 2026-10-12
**相關 commit**: <!-- 完成後回填 commit hash -->

---

## 目標 & 背景

「這台機器要連哪個 SQL Server」是**機器層級的事實**，一台機器只有一個答案。
但現況是三個 .NET app 各帶一份 `dbSetting.json`，一致性靠「打包時複製」而不是靠結構，所以會漂：

| 位置 | 內容 | 誰在用 | 問題 |
|---|---|---|---|
| `ScadaEngine.Engine/Setting/dbSetting.json` | JOULARIS_DB / engineer | Engine；開發時 Web fallback 也讀這份 | 唯一正本，OK |
| `ScadaEngine.Web/SQLSetting/dbSetting.json` | wsnCsharp / wsn | **沒人用** | 第一個 commit 留下的死檔，程式零引用；`ScadaEngine.Web.csproj:30` 指的是 `Setting\dbSetting.json`，該檔根本不存在 |
| `ScadaEngine.ModbusServer/Setting/dbSetting.json` | wsnCsharp / wsn | Gateway，`./Setting/` CWD 相對路徑 | repo 裡躺著第二組舊帳密 |
| 生產機 `C:\SCADA\Web\App\Setting\dbSetting.json` | `BuildRelease.ps1:193` 打包時從 Engine 複製 | Web 服務 | **Web 優先讀自己這份**（`Program.cs:129-131`）；現場改了 Engine 的密碼 / DB 名，Web 不會跟著變。Install.bat 升級備份/還原又是 Engine、Web 各自備各自還，兩份一旦分家就永久分家 |

目前生產機兩份內容剛好相同（已 diff 確認），只是運氣。

**目標**：把檔案放到一個**不屬於任何 app 的位置**，三個 app 都去那裡讀；升級時靠「位置不在覆蓋範圍內」存活，不再靠備份/還原。

## 驗收條件

> 生產機升級由使用者於 2026-10-12 14:17 親自執行 Install.bat（Claude 執行被 auto mode 分類器擋下）；結果見進度日誌

- [x] 生產機只有一份 `C:\SCADA\Shared\dbSetting.json`；`Engine\App\Setting\`、`Web\App\Setting\`、`ModbusServer\App\Setting\` 下**不再有** dbSetting.json
- [x] 改 `Shared\dbSetting.json` 後重啟三個服務，三者連到同一個新 DB（log 各自印出同一路徑）— 升級後 Engine / Web log 皆印 `C:\SCADA\Shared\dbSetting.json（來源: Shared）`；未另改內容換 DB 測（現場只有一個 DB）
- [x] 三個 app 啟動 log 都有一行「資料庫設定檔路徑: <實際選中的絕對路徑>（來源: 環境變數 / Shared / 本機 Setting / 開發 repo）」
- [x] 開發模式（`dotnet run` 各自目錄）：Engine / Web / ModbusServer 三者都讀 `ScadaEngine.Engine/Setting/dbSetting.json`，不需任何額外設定
- [x] 環境變數 `JOULARIS_DBSETTING=<絕對路徑>` 存在時三者都優先用它（供測試 / 多實例）— 三者共用同一 Locator，優先順序由單元測試鎖住；未在三個 app 實機設環境變數逐一跑
- [x] 舊站台（只有 `Engine\App\Setting\dbSetting.json`）跑 Install.bat 升級：自動搬到 `Shared\`，Web 舊複本被刪，升級後三服務正常連 DB
- [x] Install.bat 全新安裝：`Shared\dbSetting.json` 由包內 Engine 範本寫入，`install-db.ps1` 讀得到 — 暫存目錄模擬通過；本機為升級情境，全新安裝未實機跑
- [x] 再跑一次 Install.bat 升級：`Shared\dbSetting.json` 內容不變（不被包內範本覆蓋）— 暫存目錄模擬通過；實機只跑一次升級
- [x] Gateway 單獨裝在**另一台機器**（找不到 `..\..\Shared\`）：fallback 讀自己 `App\Setting\dbSetting.json`，行為同今日 — Locator 單元測試 `Local_Setting_Used_When_No_Shared` + InstallModbusServer.bat 從 example 鋪檔；未實機裝第二台
- [x] `install-db.ps1`、`reset-engineer-password.ps1` 改讀 `Shared\`，舊路徑仍可當 fallback
- [x] repo 裡 `ScadaEngine.Web/SQLSetting/` 刪除、csproj 死條目移除、`dotnet build` 全綠
- [x] `dotnet test` 全綠（含新增的 `DbSettingLocator` 單元測試）— 752/752

## 檔案異動清單

### 新增

- [x] `ScadaEngine.Common/Data/Services/DbSettingLocator.cs` — 共用路徑解析器（見決策 2）
- [x] `ScadaEngine.Tests/Common/DbSettingLocatorTests.cs` — 解析順序單元測試
- [x] `ScadaEngine.ModbusServer/Setting/dbSetting.example.json` — 取代現有 dbSetting.json（內容改為 JOULARIS_DB 範本，密碼留空）

### 修改

- [x] `ScadaEngine.Common/Data/Services/DatabaseConfigService.cs` — 建構子接受 `DbSettingLocator` 結果；`LoadConfigAsync` log 印出實際路徑與來源
- [x] `ScadaEngine.Engine/Data/Extensions/DataServiceExtensions.cs:22-75` — 刪掉自家 40 行路徑偵測，改呼叫 `DbSettingLocator.Resolve()`
- [x] `ScadaEngine.Web/Program.cs:124-133` — 同上
- [x] `ScadaEngine.ModbusServer/Program.cs:65-66` — 同上（不再用預設 `./Setting/dbSetting.json`）
- [x] `ScadaEngine.Web/ScadaEngine.Web.csproj:30-32` — 移除指向不存在檔案的 `<None Update="Setting\dbSetting.json">`
- [x] `ScadaEngine.ModbusServer/ScadaEngine.ModbusServer.csproj` — dbSetting.json → dbSetting.example.json 的 CopyToOutput（既有 `Setting\*.json` 通配已涵蓋，csproj 本身不需改；另 **Engine csproj** 加 `<None Remove>`/`<Content Remove>` 讓 dbSetting.json 不再進 bin / publish，見踩雷）
- [x] `BuildRelease.ps1`
  - [x] `:193-196` — 刪「Copy shared DB config from Engine → Web\App\Setting」
  - [x] 新增：把 Engine 的 dbSetting.json 放到 Release 包根目錄 `Shared\dbSetting.json`（當範本），**不再**放進 `Engine\App\Setting\`
  - [x] Install.bat 範本 `:360-378` 備份段 — `Engine\Setting` / `Web\Setting` 的 xcopy 改成排除 dbSetting.json（或備份後在還原段 del）
  - [x] Install.bat 範本 `:476-496` 還原段 — 新增 **遷移邏輯**：`if not exist C:\SCADA\Shared\dbSetting.json` 且 `exist %_BACKUP%\Engine\Setting\dbSetting.json` → move 到 Shared；全新安裝則 copy 包內 `Shared\dbSetting.json`；最後 `del` Engine / Web `App\Setting\dbSetting.json` 殘留
  - [x] Install.bat 範本 `:501-507` — install-db.ps1 提示文字路徑改 Shared
  - [x] Install.bat 範本 `:562` — 完成訊息「DB connection」路徑改 `C:\SCADA\Shared\dbSetting.json`
  - [x] InstallModbusServer.bat 範本 `:644-678, :751` — 備份/還原排除 dbSetting.json；若同機已有 `Shared\` 就不鋪本機 Setting 複本，否則鋪 example 並提示填寫
- [x] `ScadaEngine.Engine/Setting/install-db.ps1:44` — 讀取順序改環境變數 → `..\..\..\Shared\dbSetting.json`（腳本在 `Engine\App\Setting\`，往上是三層不是兩層）→ 同資料夾（fallback）
- [x] `ScadaEngine.Engine/Setting/reset-engineer-password.ps1:39-46` — 候選清單加 `C:\SCADA\Shared\dbSetting.json` 放最前
- [x] `ScadaEngine.Engine/Scripts/DeployService.ps1:278, 431, 476, 608` — 路徑提示與設定檔清單
- [x] `ScadaEngine.Web/Scripts/DeployWebService.ps1` — 同上
- [x] `ScadaEngine.ModbusServer/Scripts/DeployModbusServer.ps1:123` — 同上
- [x] `ScadaEngine.Engine/Models/DbMaintenanceSettingModel.cs:46-47` — 註解提到「同 dbSetting.json」的 Web fallback 慣例，改寫說明（DbMaintenanceSetting 本身**不搬**，仍是 Engine 專屬）

### 刪除

- [x] `ScadaEngine.Web/SQLSetting/dbSetting.json`（整個 `SQLSetting/` 資料夾）
- [x] `ScadaEngine.ModbusServer/Setting/dbSetting.json`（改 example）

### 資料庫

- 無 schema 異動

## 關鍵設計決策

### 決策 1：共用檔放 `C:\SCADA\Shared\dbSetting.json`，程式以「BaseDirectory 往上兩層」找，不寫死磁碟路徑
- **選擇**: 三個 app 安裝路徑皆為 `C:\SCADA\<App>\App\`，所以 `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Shared", "dbSetting.json"))` 在三者都會落到同一個檔，程式碼不出現 `C:\SCADA`
- **理由**: 「連哪個 DB」是機器層級事實，不該由 Engine 擁有；Web / Gateway 也不該依賴 Engine 安裝目錄存在。放在 `Shared\` 語意正確，且升級流程**天然不會覆蓋**它（Install.bat 只動 `<App>\App\`），備份/還原清單反而可以**減少**一項
- **放棄的選項**:
  - *Web 直接讀 `..\..\Engine\App\Setting\dbSetting.json`*：零新資料夾，但 Web 與 Engine 安裝耦合，Web 單獨部署的機器會找不到；且 Engine 自家 Setting 仍在備份/還原清單，語意上仍是「Engine 的檔」
  - *環境變數 / 機器層連線字串*：對現場工程師不可見、不可 diff、不好備份；保留為最高優先覆寫而非主要機制
  - *`%ProgramData%\JOULARIS\`*：標準位置，但與專案「一切都在 C:\SCADA」的慣例衝突，現場人員找不到
  - *存 DB*：雞生蛋

### 決策 2：解析順序統一放在 `ScadaEngine.Common` 的 `DbSettingLocator`，三個 app 共用
- **選擇**: 固定四層，第一個存在的檔勝出，並回傳 `(路徑, 來源列舉)` 給 log：
  1. 環境變數 `JOULARIS_DBSETTING`（絕對路徑；明確覆寫，測試 / 多實例用）
  2. `<BaseDirectory>\..\..\Shared\dbSetting.json`（生產機共用檔）
  3. `<BaseDirectory>\Setting\dbSetting.json`（單機獨立安裝，例如 Gateway 裝在遠端）
  4. `<CWD>\..\ScadaEngine.Engine\Setting\dbSetting.json`（開發模式，三專案都指向 repo 裡 Engine 那份）
- **理由**: Engine 現在自己寫了 40 行路徑偵測、Web 另一套、ModbusServer 用預設值，三套邏輯三種行為。Common 已經是三者共同引用的 class library，放這裡零新依賴。第 3 層放在第 2 層之後是刻意的：**共用檔存在就一定贏過本機複本**，杜絕 stale 複本被優先讀到（這正是今日 Web 的 bug）
- **放棄的選項**: 把順序寫進各 app 的 appsettings — 又變三份

### 決策 3：升級路徑用「搬移 + 刪殘留」，不留相容雙讀
- **選擇**: Install.bat 升級時若 `Shared\` 不存在，把備份的 Engine dbSetting.json **move** 過去，然後 **del** Engine / Web `App\Setting\dbSetting.json`。程式端雖有第 3 層 fallback，但生產機不留複本
- **理由**: 若留複本，第 2 層已存在時第 3 層永遠不會被讀到，等於垃圾；且日後有人手改複本會以為生效，製造新的困惑。一次搬乾淨
- **放棄的選項**: 保留複本當「備援」— 備援一個永遠不會被讀的檔沒有意義，反而誤導

### 決策 4：Gateway 遠端安裝情境靠第 3 層自然支援，不另設開關
- **選擇**: Gateway 裝在別台機器時 `..\..\Shared\` 不存在，自動落到 `App\Setting\dbSetting.json`；InstallModbusServer.bat 偵測同機是否已有 `Shared\`，有就不鋪本機複本，沒有就鋪 example 並提示填寫
- **理由**: 遠端機器連的 SQL Server 位址本來就不是 127.0.0.1，那份設定本來就該獨立；不需要額外設定旗標

### 決策 5：`DatabaseSchema.json` 的 Engine → Web 複製**不動**
- **理由**: 它與程式碼同版號、現場不會改，複製是安全的，不屬於「機器層級事實」

## 實作步驟

1. [x] Common：新增 `DbSettingLocator`（四層解析 + 來源列舉），`DatabaseConfigService` 接收結果並 log 路徑與來源
2. [x] Tests：`DbSettingLocatorTests` — 用暫存目錄模擬四層各自存在 / 不存在，鎖住優先順序（含「Shared 存在時本機複本必輸」）
3. [x] Engine / Web / ModbusServer 三處 DI 改呼叫 Locator；刪 Engine 自家偵測碼
4. [x] repo 清理：刪 `Web/SQLSetting/`、修 Web csproj 死條目、ModbusServer dbSetting.json → example
5. [x] `dotnet build` + `dotnet test` 全綠；開發模式三個 `dotnet run` 確認 log 都印 repo Engine 路徑（2026-10-12 實跑 bin exe、CWD=專案目錄，三者皆印 `…\ScadaEngine.Engine\Setting\dbSetting.json（來源: 開發 repo）`）
6. [x] `BuildRelease.ps1`：Release 包結構加 `Shared\`、刪 Web 複製、Install.bat / InstallModbusServer.bat 範本的備份 / 還原 / 遷移 / 提示文字
7. [x] `install-db.ps1`、`reset-engineer-password.ps1`、三支 Deploy*.ps1 路徑更新
8. [x] 本機（= 生產機）實測升級（使用者執行，2026-10-12 14:17）：先備份 `C:\SCADA`，跑 `QuickDeployAll.bat`，驗 `Shared\` 出現、舊複本消失、三服務 Running 且 log 路徑一致（需停/啟生產服務，回覆中明講中斷窗口）
9. [x] 文件同步（見下）
10. [x] 停下等使用者驗證 + 提版號（2026-10-12 回覆中提 26.3.3 → 26.4.0）

## 已知風險 / 待釐清

- ⚠️ **升級遷移是單向的**：搬到 `Shared\` 後若要回滾到舊版 Release 包，舊版 Web 會找不到 `App\Setting\dbSetting.json`。緩解：Install.bat 搬移前把原檔多留一份到 `C:\SCADA\_ConfigBackup\`（升級結束才 rmdir，現有流程已是如此），並在 docs 寫明回滾步驟
- ⚠️ 現場若有人手動在 `Web\App\Setting\` 放過**不同內容**的 dbSetting.json（刻意讓 Web 連別的 DB），升級後會被統一。目前本機已 diff 確認兩份相同；其他站台需在升級前由人工確認
- ⚠️ `install-db.ps1` 是「在 config restore 之後」執行，改成讀 `Shared\` 後要確認執行順序仍在遷移步驟之後
- ❓ 環境變數名稱 `JOULARIS_DBSETTING` 是否 OK？（與服務名 JOULARIS 一致）
- ❓ Gateway 裝在同機但**刻意要連不同 DB** 的情境是否存在？若存在，第 1 層環境變數可覆蓋，但 Windows 服務要設 per-service 環境變數需走 registry `Environment` 多字串值，較麻煩。目前假設不存在

## 測試策略

- [x] **本次是否動到核心計算 / 關鍵邏輯？**
  - [x] 是 → 補**單元測試**（`DbSettingLocatorTests`）：
    - 四層各自單獨存在時選中正確層、來源列舉正確
    - 環境變數存在但檔不存在 → 不選它、往下找（並 log warning）
    - Shared 與本機 Setting 同時存在 → **一定選 Shared**
    - 四層都不存在 → 回傳 null / 預設，不丟例外
  - [ ] 否
- [ ] 是否動到 DB 查詢 / 寫入或跨模組資料流？ 否（只改「設定檔從哪讀」，連線本身不變）
- [ ] 是否動到關鍵使用者路徑？ 否 → E2E ☒ 跳過
- [x] 手動驗證：步驟 8 的本機升級實測（三服務 log 路徑一致；Web 登入由使用者確認；Gateway 本機未安裝，不適用）
- [x] 收尾：`dotnet test` 全綠

## 文件同步

- [x] `docs/架構.md` — 新增小節「dbSetting.json 解析順序」（四層 + 來源 log），資料流章節提到「Web 讀 Engine 設定」的地方改寫
- [x] `docs/功能說明書_Release打包與升級.md` — Release 包結構加 `Shared\`；升級遷移邏輯；回滾步驟
- [x] `docs/功能說明書_資料庫自動建立與備份.md` — install-db.ps1 讀取路徑
- [x] `docs/功能說明書_ModbusServerGateway.md` — Gateway 同機 / 異機兩種安裝的 dbSetting 行為
- [x] `docs/功能說明書_登入.md`、`docs/功能說明書_Engine核心.md` — 提到 dbSetting 路徑處同步
- [x] `CLAUDE.md` — Key Configuration Files 表 `dbSetting.json` 那列改為「生產機 `C:\SCADA\Shared\`，開發 repo Engine/Setting；解析順序見 docs/架構.md」；刪掉文末「Web reads Engine's dbSetting.json via a relative path」那段舊敘述
- [x] `ScadaEngine.Engine/Scripts/README.md`、`.github/copilot-instructions.md` — 路徑提示

---

## 進度日誌

### 2026-10-06
- 調查完現況（三份半 dbSetting、Web 優先讀 stale 複本、SQLSetting 死檔），寫完 plan，等使用者 go-ahead

### 2026-10-12
- 使用者「將 plan 執行完畢」→ 動工。步驟 1–7、9 完成：Locator + 測試（752/752 綠）、三 app DI、repo 清理、BuildRelease / Install.bat 遷移邏輯、install-db / reset / Deploy 腳本、文件同步
- 開發模式實跑三 app（Web 停生產 `JoularisWeb` 25 秒、Engine 停生產 `JoularisEngine` 25 秒，Gateway 本機未安裝不需停；驗完皆已 `Start-Service` 復原 Running）：三者 log 均印 repo Engine 路徑、來源「開發 repo」
- Release 包 `Release\SCADA_Release_20261012_1356_v26.3.3`（-NoPython）已打好並靜態驗證：`Shared\dbSetting.json` 在、Engine/Web `App\Setting` 無 dbSetting、Gateway 只有 example、Install.bat 含遷移段
- Install.bat 遷移段抽出到暫存目錄模擬三情境（舊佈局升級 → move、重跑 → 不覆蓋、全新 → 鋪範本、Engine/Web 複本皆刪）全部符合
- **步驟 8 生產機實測被擋**：Claude 執行 Install.bat 被 auto mode 分類器拒絕（判定危險）。現場備份已先做到 scratchpad `scada_backup_20261012_1354\`（Engine/Web 的 Setting + MqttSetting；兩份 dbSetting md5 相同 55764c06…）。待使用者自行以系統管理員執行該包 Install.bat 後驗 ⏸ 項目
- 期間另一 session 推進 26.3.2、26.3.3 兩個 commit（Setup 守衛 / 登入版型），本 plan 的檔案與其無交集；commit 時只挑本 plan 檔案
- **14:17 使用者親自執行 Install.bat 升級成功**（install-log RESULT: SUCCESS）：`C:\SCADA\Shared\dbSetting.json` 出現且 md5 = 升級前現場那份（55764c06…，非包內範本）、`Engine\App\Setting` 與 `Web\App\Setting` 的 dbSetting.json 已刪、三服務 Running、Engine log 與新出現的 `Web\App\Log\Web-20261012.log` 都印 `資料庫設定檔路徑: C:\SCADA\Shared\dbSetting.json（來源: Shared）`
- 使用者確認「成功了可以 commit & push」；版號 26.3.3 → 26.4.0（新增共用設定檔機制 + Web 日誌 sink，屬功能版）

## 討論過程與成本記錄

### 討論過程摘要

- 使用者提問：「Web 跟 Engine 兩個都有 DBSetting，但應該要一樣，要怎麼設計好」
- Claude 調查後指出實際是三份半（含 Web/SQLSetting 死檔與 ModbusServer 第三份），並說明生產機 Web 優先讀自己複本導致的分家風險
- Claude 提出「`C:\SCADA\Shared\` 共用檔 + Common 統一四層解析器 + 升級自動遷移」方案；使用者回「寫一個 md 然後 commit & push」，未對方案提出修改，plan 依原提案撰寫
- 本 plan 依使用者指示強制加入 git（`git add -f`，`docs/plans/.gitignore` 預設排除）

### 成本記錄

| 階段 | 模型 | Input | Output | Cache Read | Cache Write | 備註 |
|------|------|-------|--------|------------|-------------|------|
| Plan 撰寫 | claude-fable-5-1 | 228 | 15,994 | 587,765 | 57,162 | 寫完 plan 時填（含前段調查討論） |
| 實作（2026-10-12 本對話，步驟 1–7、9 + 開發模式驗證 + 打包；commit 前） | claude-fable-5-1 | 2,082 | 96,984 | 11,945,429 | 234,317 | 由 transcript 依 message.id 去重加總（66 則）；步驟 8 待使用者執行，commit 後再追記 |

### Archive 總結（搬進 _archive 前回填）

- **總 token**：input __ / output __ / cache read __ / cache write __
- **模型**：
- **API 費用（當下牌價，附計算式）**：牌價查詢日 YYYY-MM-DD，來源 __
  - 計算式：

## 完成後補充

### 實際做法 vs 原計畫差異
- **第 4 層（開發 repo）加「BaseDirectory 祖先鏈」**：原計畫只看 `<CWD>\..\ScadaEngine.Engine`，但 ModbusServer 啟動即 `Directory.SetCurrentDirectory(BaseDirectory)`，CWD 上一層是 `bin\Debug`，找不到 → 補從 BaseDirectory 往上最多 6 層找 `ScadaEngine.Engine\Setting\dbSetting.json`（生產佈局 `C:\SCADA\Engine\App` 往上不會有該資料夾，測試鎖住不誤判）
- **Web 補 Serilog 預設 sink**：Web `UseSerilog` 只 `ReadFrom.Configuration`，appsettings 沒 `Serilog` 區段 → ILogger 零輸出（生產也是），驗收條件「三 app 啟動 log 有路徑行」在 Web 根本看不到。比照 Engine / Gateway：無 Serilog 區段時服務模式寫 `Log\Web-.log`（30 天輪替）、開發模式印 Console；有區段則完全照 appsettings。這是超出 plan 的小擴充，但沒有它驗收條件無法成立
- **Engine csproj 用 `<None Remove>`+`<Content Remove>` 而非 `CopyToOutputDirectory=Never`**：Worker SDK 下 `Never` 沒生效（詳細 build log 仍 `_SourceItemsToCopyToOutputDirectory` 複製），Remove 才真的不進 bin / publish；連帶 Web / Tests 透過 ProjectReference 遞移拖進來的複本也消失
- **Install.bat 備份段不改**：原計畫「xcopy 排除 dbSetting.json 或備份後在還原段 del」→ 採後者的變形：照舊備份 + 還原（舊佈局升級需要還原後那份當搬移來源），還原後再 move → Shared、del 兩個 App\Setting 複本。`_ConfigBackup` 仍於還原段末 rmdir（原計畫想多留一份給回滾；改為在文件寫回滾步驟 = 把 Shared 那份 copy 回兩個 Setting 再跑舊版）
- **install-db.ps1 的 Shared 相對路徑是 `..\..\..\`**（腳本在 `C:\SCADA\Engine\App\Setting\`，plan 寫的 `..\..\` 少一層）；另加環境變數 `JOULARIS_DBSETTING` 當第一順位與 Locator 一致
- **reset-engineer-password.ps1** 候選清單除 `C:\SCADA\Shared` 外，再加「包根 `Shared\`」（工程師在 Release 包根目錄執行時）與環境變數
- 步驟 8 未完成（見進度日誌）

### 踩到的雷
- Worker SDK（Engine）對 `None Update … Never` 不理會，仍複製 json 到輸出；要用 `Remove`。驗證方式：`dotnet build -v:d | grep dbSetting` 看 `_SourceItemsToCopyToOutputDirectory`
- bin 內舊複本不會因 csproj 改動自動消失；ProjectReference 會把 Engine 的 content 拖進 Web / Tests 的 bin，三個 bin 都要清一次才看得到真實行為
- 從 **Git Bash 啟動 cmd 跑 Install.bat 會誤判「.NET Framework 4.8 未安裝」**：bat 用 `… | find "Release"`，MSYS PATH 先找到 Unix `find`。安裝腳本只能從 Windows 殼（PowerShell / 檔案總管）跑；中止發生在動任何檔案之前，無副作用
- Claude Code auto mode 分類器**拒絕執行生產機 Install.bat**（含停/啟服務 + 覆蓋 C:\SCADA），即使 CLAUDE.md 已授權停服務；這類「真正動生產機」的步驟要預期由使用者親手跑
- PowerShell 5.1 讀無 BOM UTF-8 的 .ps1 會把中文當 ANSI → parse error（已在 memory），驗證腳本一律 `utf-8-sig` 或純 ASCII
- Claude Code 的 Bash 工具 heredoc 會吃掉一層反斜線（`\\n` → 換行），含 Windows 路徑的 Python 編輯腳本用 Write 工具寫檔再執行，別走 heredoc
- docs/*.md 多為 CRLF，用 Python 做字串比對前先正規化行尾，寫回時還原

### 對 memory / CLAUDE.md 的更新建議
- memory（已寫）：Bash heredoc 吃反斜線 → 用 Write 寫腳本；Install.bat 不能從 Git Bash 跑；auto mode 分類器擋生產安裝
- CLAUDE.md：Key Configuration Files 的 dbSetting 列已改寫、文末 Web 相對路徑舊敘述已刪（本次已做）
