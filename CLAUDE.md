# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

始終用繁體中文說明

依照 馬斯克 第一性原理

## Documentation Rules

新增或修改功能後，須同步更新 `docs/` 下對應的功能說明書。

改到使用者看得到的操作流程（按鈕、欄位、步驟、選單位置）時，另須同步更新線上使用手冊 `ScadaEngine.Web/Manual/{scada,ems,engineer}/` 對應章節；新增功能頁要在該書 `_toc.json` 加章節並填 `routes`。寫作規範見 `ScadaEngine.Web/Manual/README.md`，架構見 docs/功能說明書_使用手冊.md。

修改過 `docs/功能說明書_*.md` 後，須檢查並同步修改對應的使用手冊內容（`ScadaEngine.Web/Manual/{scada,ems,engineer}/` 中涵蓋同一功能的章節），確保手冊與說明書描述一致；若該功能尚無手冊章節或該次修改不影響手冊內容，在回覆中明講。

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
6. 使用者明確說「沒問題」「OK」「可以」「過了」之後，自動執行 `git add` 相關檔案 → `git commit`（訊息走專案風格）→ `git push`，再回填 commit hash 與**成本總結**（記錄方式見 [docs/plans/CLAUDE.md](docs/plans/CLAUDE.md)，寫 plan 前先讀）並把 plan 搬到 `docs/plans/_archive/`
   - ✅ 授權 scope 僅限「plan 流程收尾 + 使用者明確確認」，不代表其他情境也預先授權 push
   - ⚠️ 若 pre-commit hook 失敗，修好後建**新** commit，禁止 `--no-verify`
   - ⚠️ 若 push 被 reject（非 fast-forward 等），停下回報，不可 force push

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

子目錄 CLAUDE.md 在讀到該目錄檔案時自動載入；**規劃／討論該專案功能而尚未讀過該目錄檔案時，先讀對應那份**：

- [ScadaEngine.Web/CLAUDE.md](ScadaEngine.Web/CLAUDE.md)：Feature Folder + MVC、Razor 前端分離、flatpickr、UI 設計系統、i18n
- [ScadaEngine.Engine/CLAUDE.md](ScadaEngine.Engine/CLAUDE.md)：Engine 專屬設定檔、每小時聚合排程、MQTT Retain、IDataRepository
- [ScadaEngine.ModbusServer/CLAUDE.md](ScadaEngine.ModbusServer/CLAUDE.md)：AddressMap append-only、獨立安裝
- [ScadaEngine.LicenseBridge/CLAUDE.md](ScadaEngine.LicenseBridge/CLAUDE.md)：net48 x86、Named Pipe
- [docs/plans/CLAUDE.md](docs/plans/CLAUDE.md)：plan 的 Token / 成本記錄

## Build & Run

兩個專案各自 `dotnet run`，Web 跑在 **5038** (HTTP) / **7189** (HTTPS)。
Engine 是背景服務，無 HTTP endpoint。

> ⚠️ Razor views 是 **precompiled**，改 .cshtml 必須 `dotnet build` 才生效。

**部署、停/啟生產服務、build 被進程鎖住** → 用 `scada-deploy` skill。兩條紅線不論何時都成立：

- 生產機更新一律走 `QuickDeployAll.bat` / Release 包 `Install.bat`；各專案 `Scripts\QuickDeploy.bat` 會無備份硬蓋 `C:\JOULARIS\` 現場設定
- 停 `ScadaEngine.Web` 進程一律按路徑過濾：`C:\JOULARIS\Web\App\` 底下那支是正式站台

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

任務牽涉以下任一主題，先讀 **[docs/架構.md](docs/架構.md)**（含 TOC），再依該節指向的功能說明書深入：

- **資料流**：Modbus / DB 來源 / OPC UA 來源 → HistoryData / LatestData / MQTT → Web；對外 Modbus Gateway 分支
- **警報系統**：Alarm MQTT 推播 + 規則熱重載（Engine ↔ Web）
- **通知系統**：Line / Email（MailKit）/ 簡訊（序列埠簡訊盒 AT 指令）三通道，Web 存檔發 MQTT `SCADA/Sys/Notify/Reload` 熱重載 → docs/功能說明書_簡訊通知.md
- **用電報表**：On-demand + 葉子層 Hourly 預聚合 + Staleness Window
- **電費計算**：Web XX:05 逐時計價 + EMS 電費卡 + /HolidaySetting 假日
- **契約容量最佳化**：DemandData 整刻樣本回測，Common `ContractCapacityCalculator` → docs/功能說明書_契約容量最佳化.md
- **用水報表與水費**：累積式 `WaterMeter*` 體系 + 台水分段累進 on-demand；與冷凍噸 WaterCircuit 無關
- **用氣報表與氣費**：`GasMeter*`（與水表平行、獨立），點位判定單位 + 名稱關鍵字雙條件，氣費期別多 `IsSkipped`（兩月一期）
- **產氣量表與空壓機能源申報**：`AirMeter*`（與氣表**互不相干**，壓縮空氣不可借氣表樹否則被計氣費）
- **月結期別四分**：電費 / 水費 / 氣費各自期別設定，冷凍噸、能源申報、能源基線、設備效率診斷刻意走曆月
- **設備效率診斷**：`EquipmentHourly` 設備層預聚合、能量加權、公式單一真相 Common `EquipmentMetricCalculator` → docs/功能說明書_設備效率診斷.md
- **能源日報**：Web 每日 02:00 生成快照，Email 與 /DailyReport 讀同一份
- **資料庫自動建立與備份**：啟動安全網自建 + install-db.ps1 + Engine 每週 BACKUP
- **資料表用途對照**：各表 Key 欄位 + SID 格式
- **演算法 status 協定**：LogicFlow 節點回傳結構 + per-output port 錯誤傳遞
- **HASP 授權守衛**：LicenseBridge Named Pipe 驗加密狗，失敗暫停採集

---

## Key Configuration Files

| File | Purpose |
|------|---------|
| `dbSetting.json`（開發 `ScadaEngine.Engine/Setting/`；生產機 **`C:\JOULARIS\Shared\`**） | SQL Server connection，Engine / Web / ModbusServer **共用一份**。三者皆由 Common `DbSettingLocator`（`SharedSettingLocator` 薄包裝）四層解析（環境變數 `JOULARIS_DBSETTING` → `..\..\Shared` → 本機 `Setting` → 開發 repo），啟動 log 印實際路徑與來源；升級由 Install.bat 自動從舊佈局搬移 → docs/架構.md §共用設定檔解析順序 |
| `LineSetting.json`（開發 `ScadaEngine.Engine/Setting/`；生產機 **`C:\JOULARIS\Shared\`**） | Line Messaging API token + rate limit。Engine / Web 共用一份，`SharedSettingLocator` 解析（同 dbSetting 但無環境變數層） |
| `EmailSetting.json`（同上） | SMTP host/port/帳密 + rate limit（MailKit）。Web「警報設定 → Email」存檔寫同一份，之後發 MQTT `SCADA/Sys/Notify/Reload` 由 Engine 熱重載，不重啟 |
| `SmsSetting.json`（同上） | 簡訊盒設定（ComPort auto/手動、BaudRate、SimPin、限流、每日上限、恢復通知開關）。Web 存檔後 MQTT 熱重載 + 重掃序列埠。細節見 docs/功能說明書_簡訊通知.md |

Engine 專屬設定（MqttSetting、Modbus / DBPoint / OpcUaPoint 來源、EquipmentAggregation、DbMaintenance、通知字典）見 [ScadaEngine.Engine/CLAUDE.md](ScadaEngine.Engine/CLAUDE.md)；ModbusServerSetting / AddressMap 見 [ScadaEngine.ModbusServer/CLAUDE.md](ScadaEngine.ModbusServer/CLAUDE.md)。

開發模式三個專案各自在自己目錄 `dotnet run`（MqttSetting 等仍以 CWD 相對路徑讀）；`dbSetting.json` 由 `DbSettingLocator` 解析，三者都讀 repo 內 Engine 那份、不依賴 CWD。

---

## Database Schema (SQL Server)

DB 名由 `dbSetting.json` 的 `DataBaseName` 決定，**各站不同、不要寫死**（repo 範本 `JOULARIS_DB`；程式 fallback `wsnCsharp`；現場另見 `wsn_TG` 等）。要查 DB 時先讀實際生效的 dbSetting.json。

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
