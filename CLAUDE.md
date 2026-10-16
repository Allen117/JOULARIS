# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

始終用繁體中文說明

依照 馬斯克 第一性原理

## Documentation Rules

新增或修改功能後，須同步更新 `docs/` 下對應的功能說明書。

## 實作計畫（Plan）規則

當使用者說「**規劃**」「**計畫**」「**plan**」「**先想清楚**」，
或任務符合以下**任一**條件時，先建立 plan.md 再動工：

- 跨多個檔案的新功能或重構
- 預計需跨多個對話才能完成
- 涉及設計決策（DB schema、架構選型、介面設計）

**流程**：

1. 依 `docs/plans/_template.md` 結構建立
2. 檔名：`docs/plans/YYYY-MM-DD-{kebab-任務名}.md`
3. **寫完 plan 後一律停下，等使用者明確說「OK」「開工」「執行」「動手」才開始實作**
   - ⚠️ 使用者回答 plan 內的提問**不算**動工授權，僅是補資訊以完善 plan
   - ⚠️ 即使提問都答完、plan 看似完整，也要等明確的 go-ahead，不可自行推進
   - 若 plan 因使用者回覆而需更新，先改 plan、再次停下等確認
   - ❌ 不可自設「回答完問題就動手」「說 OK 就動手」等暗示性條件來繞過此規則
4. 實作中即時更新勾選狀態
5. 實作完成後先停下，等使用者驗證
6. 使用者明確說「沒問題」「OK」「可以」「過了」之後，自動執行 `git add` 相關檔案 → `git commit`（訊息走專案風格）→ `git push`，再回填 commit hash 與**成本總結**（見下方 Token / 成本記錄）並把 plan 搬到 `docs/plans/_archive/`
   - ✅ 授權 scope 僅限「plan 流程收尾 + 使用者明確確認」，不代表其他情境也預先授權 push
   - ⚠️ 若 pre-commit hook 失敗，修好後建**新** commit，禁止 `--no-verify`
   - ⚠️ 若 push 被 reject（非 fast-forward 等），停下回報，不可 force push

### Token / 成本記錄（報告用）

每份 plan 都要在 plan.md 的「討論過程與成本記錄」段（見 `_template.md`）記錄成本，供未來報告使用：

- **寫完 plan 時**：記錄 (a) 討論過程摘要 — 提了哪些問題、使用者怎麼回、因此 plan 改了什麼；(b) 撰寫 plan 至此花費的 token 數（input / output 分列）與**使用模型**
- **實作期間**：每次 `git commit` / `git push` 後，追記該階段累計 token（跨對話的 plan：每個對話收尾前先把該對話用量寫進 plan，避免遺失）
- **Archive 時**：回填**總 token**（input / output / cache read / cache write 分列）、**模型**、**對應 API 費用** — 費用依「當下」官方牌價換算並列出計算式；牌價必須現查（`claude-api` skill 或 https://platform.claude.com/docs/en/about-claude/pricing ），**不可憑記憶報價**
- **token 取得方式**（模型無法自省用量，禁止估計或編造）：
  1. 解析本 session transcript：`%USERPROFILE%\.claude\projects\` 下**對應本專案**的資料夾（名稱 = 工作目錄路徑轉 kebab-case，形如 `c--Users-<帳號>-Desktop-ScadaEngine`）中**最近修改**的 `<session-id>.jsonl`，加總各則 assistant 訊息的 `message.usage`（`input_tokens` / `output_tokens` / `cache_read_input_tokens` / `cache_creation_input_tokens`）
     - ⚠️ 勿寫死帳號 —— 不同機器/帳號路徑不同；用 `ls -t <projects 目錄>/*.jsonl | head -1` 取最新那份
  2. 若無法讀取，請使用者執行 `/cost` 並貼上結果
- 若某階段數字取不到，如實標「未取得」，不可補估

**不需要 plan.md**：單檔小修、問答、讀碼、bug 根因調查、使用者已給出明確一步到位指令。

個別 plan.md 已由 `docs/plans/.gitignore` 排除，不會進 git。詳細工作流見 `docs/plans/README.md`。

## Git Commit 訊息格式

- **Subject（第一行）**：≤ 72 字元（約 30 個中文字），一句話講清楚改了什麼，讓 `git log --oneline` 可掃
- 空一行後接 **body**：詳細內容照舊全寫（動機、設計決策、相容性、bug 修正、i18n / docs 同步），資訊量不減，只是從單行 subject 搬進 body，建議 `-` 條列分項
- 歷史 commit 為千字單行 subject 舊格式，**不回改**；讀舊 history 時知悉即可

## 版本號規則（YY.A.B）

- 格式 `YY.A.B`：YY = 西元年末兩位（2026 → 26）、A = 功能版、B = 修正版
- **有新增功能 → A+1、B 歸 0**；只修 bug / 重構 / 文案 / 文件 → **B+1**
- 跨年：該年首次異動時 YY 換新、A 與 B 歸 0 後再依本次性質遞增（例 `27.1.0` 或 `27.0.1`）
- 唯一真相來源：repo 根目錄 `Directory.Build.props` 的 `<Version>`，Web footer、Windows 服務描述、Release 包名、exe 檔案屬性皆由此帶出
- **每次程式改完、commit 前，必須向使用者提出版本提議並等確認**，例：「本次新增 X 功能，26.1.0 → 26.2.0？」。使用者確認後才改版號，並與程式同一個 commit
  - 走 plan 流程者，併入「實作完成後停下等使用者驗證」那一步一起問
  - 「功能 vs 修正」由 Claude 提議、使用者裁定；拿不準就問，不可自行決定後直接 commit

## Project Overview

.NET 8 SCADA 工業監控系統，包含 Engine（Modbus 資料採集 + MQTT 發布）與 Web（ASP.NET Core MVC 儀表板）。

## 專案專屬規則檔

**Web 專屬規則已移至 [ScadaEngine.Web/CLAUDE.md](ScadaEngine.Web/CLAUDE.md)**（Feature Folder + MVC 架構、Razor 前端分離、flatpickr 時間輸入、Web Project Structure、UI 設計系統、i18n 規則）。觸碰該目錄檔案時會自動載入；若在規劃／討論 Web 功能而尚未讀取該目錄檔案，先讀該檔。

## Build & Run

兩個專案各自 `dotnet run`，Web 跑在 **5038** (HTTP) / **7189** (HTTPS)。
Engine 是背景服務，無 HTTP endpoint。

> ⚠️ Razor views 是 **precompiled**，改 .cshtml 必須 `dotnet build` 才生效。

### 測試撞埠時可暫停生產服務（2026-09-24 使用者授權）

Web 埠 5038 寫死在 Program.cs（`ListenAnyIP`，環境變數無效），生產服務 `JoularisWeb` 佔住雙棧時開發實例起不來。
**為了驗證改動而需要起開發 Web 實例時，允許 `Stop-Service JoularisWeb` 暫停生產服務**（Engine 對應 `JoularisEngine` 同理；四個服務名見 docs/功能說明書_部署與遠端更新.md §服務命名）。

- 驗完**同一輪必須復原**：關掉開發實例（路徑過濾殺進程，見下節）→ `Start-Service` 把停掉的服務全部拉回來，並確認 `Get-Service` 為 Running
- 停/啟服務要在回覆中明講，讓使用者知道生產監控有中斷窗口

### 部署到 C:\JOULARIS（本機 = 生產機）

- **Release 包為 framework-dependent（瘦身）**：三個 .NET 8 app（Engine/Web/ModbusServer）不再自帶 runtime，**目標機須先裝 `ASP.NET Core Runtime 8.0.x (x64)`**（一包覆蓋三者）。`Install.bat` 開頭會偵測，缺就中止不動機器。**升級既有 self-contained 生產機前，務必先裝好 runtime**。只鎖 major=8（只有 .NET 9 不會跑）。Python 抽成 package 根目錄的版號化基礎包，Install.bat 只在缺/版本變更才鋪；只改 C# 碼時可 `BuildRelease.ps1 -NoPython` 產 app-only 小包。詳見 docs/功能說明書_部署與遠端更新.md
- **一鍵全部署**：repo 根目錄 `QuickDeployAll.bat`（系統管理員執行）＝ `BuildRelease.ps1` 打包 → 最新 Release 包 `Install.bat` 安裝 Engine + Web（升級自動備份/還原現場設定）。**不碰 Modbus Gateway** — 要裝/更新 Gateway 另跑 Release 包內 `InstallModbusServer.bat`
- **安裝根目錄唯一真相 = Install.bat 的 `set "_ROOT=C:\JOULARIS"`**（26.6.0 起；之前是 `C:\SCADA`）。程式碼**不得**寫死任何根目錄 — Shared / LicenseBridge / Algorithms / Python / Web 的 WatchedFolder 都以執行檔位置相對推算。舊機偵測到 `C:\SCADA` 時 Install.bat 先跑 `Engine\App\Setting\migrate-install-root.ps1` 自動搬家（服務 / SQL 資料檔 / 排程 / 設定檔改指；可 `-WhatIf` 預演）→ docs/功能說明書_部署與遠端更新.md §安裝根目錄遷移
- ⚠️ 各專案 `Scripts\QuickDeploy.bat` 的 Update（選 6）會用 **repo 版設定檔硬蓋** `C:\JOULARIS\...` 現場設定（無備份），且停服務後的進程清理用 `ScadaEngine*` 萬用字元會**誤殺另一個服務的進程**；生產機更新一律走 QuickDeployAll / Install.bat

### 卡住的 Web 進程（鎖住 bin 導致 build 失敗）

⚠️ 這台機器上**同時會有兩個 `ScadaEngine.Web` 進程**：
`C:\JOULARIS\Web\App\` 是**部署站台**、`...\Desktop\ScadaEngine\ScadaEngine.Web\bin\Debug\` 才是開發 build。
只有後者會鎖住 build 輸出，**不分路徑盲殺會一併關掉正式站台**，務必用路徑過濾：

```powershell
Get-Process -Name 'ScadaEngine.Web' -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -like '*\Desktop\ScadaEngine\ScadaEngine.Web\bin\*' } |
  Stop-Process -Force
```

停之前先 `Get-Process -Name 'ScadaEngine.Web' | Select-Object Id, Path` 確認打到的是哪一個。
另：為驗證而啟動的開發 Web 進程，**驗完必須在同一輪關掉**，否則下次 build 會被自己鎖住。

---

## Solution Architecture

```
ScadaEngine.sln
├── ScadaEngine.Common        — Shared models & DB config service (class library)
├── ScadaEngine.Algorithm     — Algorithm utilities (class library, currently minimal)
├── ScadaEngine.Engine        — .NET 8 Worker Service (Modbus → MQTT publisher)
├── ScadaEngine.Web           — .NET 8 ASP.NET Core MVC (dashboard, http://localhost:5038)
├── ScadaEngine.LicenseBridge — net48 Windows Service，HASP USB 加密狗授權驗證，靠 Named Pipe 供 Engine 呼叫
└── ScadaEngine.ModbusServer  — .NET 8 對外 Modbus TCP Gateway（FC4 float32 供第三方讀即時值 + :5041 對照表網頁；獨立安裝 InstallModbusServer.bat，只引用 Common）
```

### 跨模組設計

任務牽涉以下任一主題，先讀 **[docs/架構.md](docs/架構.md)**（含 TOC）：

- **資料流**：Modbus / DB 來源 / OPC UA 來源 → HistoryData / LatestData / MQTT → Web；對外 Modbus TCP Gateway 分支（ScadaEngine.ModbusServer，append-only 位址對照 `AddressMap.json`）→ docs/功能說明書_ModbusServerGateway.md
- **警報系統**：Alarm MQTT 推播 + 規則熱重載（Engine ↔ Web）
- **通知系統**：Line / Email / 簡訊三通道推播（每群組/號碼可選 zh-TW / en，觸發 + 恢復皆通知；寄送結果寫 EventLog 摘要，EventType=3）。Engine 端訊息字典 `Resources/notification.{zh-TW,en}.json`，Web UI 在 `AlarmSetting` 第三 tab 管理 Email 群組與規則路由、第四 tab 管理簡訊號碼。SMTP 走 **MailKit**；簡訊走序列埠簡訊盒（**System.IO.Ports** + 標準 AT 指令、UCS2 70 字、COM 自動偵測，無網路可用；Web 測試發送/重掃經 MQTT `SCADA/Sys/Sms/Cmd|Status` 由 Engine 代執行）→ docs/功能說明書_簡訊通知.md。三檔設定（Email / Sms / Line）生產機在 `C:\JOULARIS\Shared\`，Engine 讀、Web 讀寫同一份；Web 存檔後發 `SCADA/Sys/Notify/Reload|Status` 熱重載，不重啟 Engine → docs/架構.md §共用設定檔解析順序
- **用電報表**：On-demand 計算 + 葉子層 Hourly 預聚合 + Staleness Window
- **電費計算**：Web 逐時計價（EnergyLeafHourly → ElectricityCostHourly，XX:05 觸發）+ EMS 電費狀態卡 + /HolidaySetting 假日（TOU 落 sun_offday）
- **契約容量最佳化**：`/ContractCapacity` on-demand 讀 DemandData 整刻樣本（台電固定 15 分鐘窗口）× 電費設定單價與「契約容量歷史」（`electricity_tariff.contracts`）→ Common `ContractCapacityCalculator` 回測保守建議 / 最低成本點（超約 2/3 倍依台電電價表）→ docs/功能說明書_契約容量最佳化.md
- **用水報表與水費**：累積式水表 `WaterMeter*` 體系（boundary 相減 + MaxVolume 溢位 + UnitScale 換算 m³）+ 台水流動水費分段累進 on-demand（無逐時表）；與冷凍噸 WaterCircuit 無關
- **用氣報表與氣費**：累積式天然氣表 `GasMeter*` 體系（與水表平行複製、各自獨立；Engine XX:03 聚合）。兩處刻意差異：點位判定走**單位 + 點位名稱關鍵字雙條件**（單位納入「度」但靠名稱擋掉電表點位，水表僅看單位）；氣費期別多 `IsSkipped` 支援**兩月一期**
- **產氣量表與空壓機能源申報**：累積式產氣量 `AirMeter*` 體系（與氣表平行複製、**互不相干** — 壓縮空氣不可借氣表樹否則被計氣費；Engine XX:04 聚合；點位判定單位 + 名稱關鍵字 + 排除天然氣字樣）→ `/AirCompressorDeclaration` 與冰水申報同構，kWh 同一支曆月方法，12 曆月比能耗 kWh/Nm³
- **月結期別四分**：電費 `BillingPeriods` / 水費 `WaterBillingPeriods` / 氣費 `GasBillingPeriods`（各自獨立設定頁，氣費多 IsSkipped）/ 曆月（冷凍噸、能源申報、空壓機能源申報、能源基線、設備效率診斷刻意不走期別）— 對照表見 docs/架構.md §用電報表
- **設備效率診斷（冰機 / 空壓機）**：`EquipmentProfile` + `EquipmentPointBinding`（設定頁前端掃描 Device 分群判定 + 手動補）→ Engine **XX:06** `EquipmentHourly` 設備層預聚合（冷量 = 流量 + 進出水溫**分鐘對齊**逐分鐘算 RT）→ 即時 / 期間效率（**能量加權** ΣkWh ÷ Σ產出、曆制）+ EMS 兩張卡；公式單一真相 Common `EquipmentMetricCalculator`（冰機 ASHRAE kW/RT、空壓機 ISO 11011 / CAGI 比功率）→ docs/功能說明書_設備效率診斷.md
- **能源日報**：Web 每日 02:00 生成前一日快照（電/水/氣/RTh root 合計 + 警報摘要 + 單日/MTD 比較 + 規則式提示，不串 LLM）→ `DailyReportSnapshot` JSON + HTML Email（SMTP 沿用 EmailSetting.json、收件清單獨立 `DailyReportRecipients`）；Email 與 /DailyReport 頁讀同一份快照，SectionFlags 顯示端過濾
- **資料庫自動建立與備份**：DB 不存在時啟動安全網自建（無權限優雅降級）+ install-db.ps1 一次性安裝 + Engine 每週 BACKUP（A/B 輪替、結果寫 EventLog）
- **資料表用途對照**：各表 Key 欄位 + 用途一覽 + SID 格式（完整定義以 DatabaseSchema.json 為準）
- **演算法 status 協定**：LogicFlow 節點回傳結構 + per-output port 錯誤傳遞
- **HASP 授權守衛（LicenseBridge）**：net48 bridge 服務靠 Named Pipe 驗加密狗；Engine 每 30 分鐘驗，失敗即暫停 Modbus 採集 + 發 MQTT `SCADA/Sys/License/Status`

---

## Key Configuration Files

| File | Purpose |
|------|---------|
| `dbSetting.json`（開發 `ScadaEngine.Engine/Setting/`；生產機 **`C:\JOULARIS\Shared\`**） | SQL Server connection，Engine / Web / ModbusServer **共用一份**。三者皆由 Common `DbSettingLocator`（`SharedSettingLocator` 薄包裝）四層解析（環境變數 `JOULARIS_DBSETTING` → `..\..\Shared` → 本機 `Setting` → 開發 repo），啟動 log 印實際路徑與來源；升級由 Install.bat 自動從舊佈局搬移 → docs/架構.md §共用設定檔解析順序 |
| `ScadaEngine.Engine/MqttSetting/MqttSetting.json` | MQTT broker IP/port/topic/retain |
| `ScadaEngine.Engine/Modbus/Modbus.json` | Modbus device definitions (IP, port, tags) |
| `ScadaEngine.Engine/DatabaseSchema/DatabaseSchema.json` | 建表 + 欄位自動同步的**唯一真相來源** — 加欄位只改此檔，Engine 與 Web 啟動時自動補缺欄位（只加不減不改，詳見 docs/架構.md §資料庫結構初始化與欄位同步） |
| `ScadaEngine.Engine/DBPoint/*.json` | DB 來源 Coordinator 點位定義（由 Web「DB 來源」頁**匯入 Excel** 產生，範本 `DB來源範本.xlsx`；Name/Unit 可在頁面熱編輯回寫）。Modbus 同理走「Modbus 來源」頁匯入 `Modbus範本.xlsx`，共用 `ScadaEngine.Web/Services/SourceExcel/`。細節見 docs/架構.md §資料流 + docs/功能說明書_DB來源管理.md §8.4 / _Modbus來源管理.md §4.1 |
| `ScadaEngine.Engine/OpcUaPoint/*.json` | OPC UA 來源定義（一檔一 Server 含 Devices 分組；Web「OPC UA 來源」頁全欄位動態編輯回寫，免重啟；亦可同頁匯入 `OPCUA範本.xlsx`，點位以 Seq 欄比對不漂移、匯出不含密碼）。三種來源的網頁熱編輯在 JSON 記 `WebEditedAt`、匯出 Excel 記 `ScadaExportedAt`，拿舊 Excel 重匯會警告並要求勾選確認。細節見 docs/架構.md §資料流 + docs/功能說明書_OPCUA通訊.md |
| `ScadaEngine.Engine/appsettings.json` → `EquipmentAggregation` | 設備效率 hourly 聚合：`MinSamplesPerHour`(30) / `MaxStalenessHours`(2) / `CatchUpHours`(24) / `BackfillBatchSleepMs`(500)。設備與綁定在 DB（Web 設定頁維護，Engine 每小時重讀免重啟）→ docs/功能說明書_設備效率診斷.md §6 |
| `ScadaEngine.Engine/Setting/DbMaintenanceSetting.json` | 自動建 DB 路徑 + 每週備份排程。同資料夾 `install-db.ps1` 為安裝腳本（idempotent，已綁入部署流程）。細節見 docs/架構.md §資料庫自動建立與每週備份 |
| `LineSetting.json`（開發 `ScadaEngine.Engine/Setting/`；生產機 **`C:\JOULARIS\Shared\`**） | Line Messaging API token + rate limit。Engine / Web 共用一份，`SharedSettingLocator` 解析（同 dbSetting 但無環境變數層） |
| `EmailSetting.json`（同上） | SMTP host/port/帳密 + rate limit（MailKit）。Web「警報設定 → Email」存檔寫同一份，之後發 MQTT `SCADA/Sys/Notify/Reload` 由 Engine 熱重載，不重啟 |
| `SmsSetting.json`（同上） | 簡訊盒設定（ComPort auto/手動、BaudRate、SimPin、限流、每日上限、恢復通知開關）。Web 存檔後 MQTT 熱重載 + 重掃序列埠。細節見 docs/功能說明書_簡訊通知.md |
| `ScadaEngine.Engine/Resources/notification.{zh-TW,en}.json` | Engine 通知訊息字典（Line + Email + 簡訊共用，依群組/號碼 Language 切換）|
| `ScadaEngine.ModbusServer/Setting/ModbusServerSetting.json` | 對外 Modbus Gateway：監聽埠(502)/UnitId/WordOrder/NaN 或 HoldLast/網頁埠(5041，刻意避開 CDPSvc 的 5040)/位址段界 — **僅啟動載入**。同專案 `AddressMap.json`（執行期自動產生）為位址對照持久化，**升級必保留、勿刪**。細節見 docs/功能說明書_ModbusServerGateway.md |

開發模式三個專案各自在自己目錄 `dotnet run`（MqttSetting 等仍以 CWD 相對路徑讀）；`dbSetting.json` 由 `DbSettingLocator` 解析，三者都讀 repo 內 Engine 那份、不依賴 CWD。

---

## Database Schema (SQL Server: `wsnCsharp`)

欄位定義唯一真相來源 = `ScadaEngine.Engine/DatabaseSchema/DatabaseSchema.json`（加欄位只改此檔，Engine / Web 啟動自動補缺欄位，只加不減不改）。**各表 Key 欄位與用途對照 + SID 格式** → [docs/架構.md](docs/架構.md) §資料表用途對照。

---

## Naming Conventions

This codebase uses Hungarian notation throughout:

| Prefix | Type | Example |
|--------|------|---------|
| `sz` | string | `szName`, `szBrokerIp` |
| `n` | int | `nPort`, `nTotalPoints` |
| `f` | float | `fValue`, `fRatio` |
| `d` | double | `dValue` |
| `dt` | DateTime | `dtTimestamp`, `dtLastUpdated` |
| `is` | bool | `isConnected`, `isMonitorEnabled` |
| `_` prefix | private field | `_logger`, `_mqttClient` |

---

## Key Patterns & Pitfalls

### Dapper Column Mapping
`CoordinatorModel` and other models use Hungarian property names (`szName`, `szModbusID`) that don't match DB column names (`Name`, `ModbusID`). Dapper maps by property name by default — the `[Column]` attribute is NOT used. Always use SQL aliases:
```sql
SELECT Name AS szName, ModbusID AS szModbusID, ...
FROM ModbusCoordinator
```

### MQTT Retain Flag
Engine publishes with `Retain=true`. When restarting Engine, old retained messages (without `name` field) remain on the broker. A full restart of both Engine and broker clears stale retained messages.

### IDataRepository (Scoped)
Defined in `ScadaEngine.Engine` but used by both Engine and Web. Web registers `SqlServerDataRepository` as Scoped. `MqttRealtimeSubscriberService` (Singleton) accesses it via `IServiceProvider.CreateScope()`.
