# 部署與升級

本章說明如何建立 Release 安裝包、在現場伺服器首次安裝或升級，以及舊站台從 `C:\SCADA` 搬到 `C:\JOULARIS` 的遷移。生產機一律以 Release 包的 `Install.bat` 安裝或升級。

## 系統組成與服務

| 元件 | Windows 服務名 | 安裝位置 | 說明 |
|------|---------------|---------|------|
| Engine | `JoularisEngine` | `C:\JOULARIS\Engine\App` | 資料採集、警報、計算、流程圖、備份 |
| Web | `JoularisWeb` | `C:\JOULARIS\Web\App` | 網頁介面，HTTP 5038 / HTTPS 7189 |
| License Bridge | `JoularisLicense` | `C:\JOULARIS\LicenseBridge` | HASP 加密狗驗證（見 [授權加密狗](license)） |
| Modbus Gateway | `JoularisModbusGateway` | `C:\JOULARIS\ModbusServer\App` | 選配，另行安裝（見 [對外 Modbus TCP Gateway](modbus-gateway)） |

共用設定檔集中在 `C:\JOULARIS\Shared\`：`dbSetting.json`（資料庫連線）、`EmailSetting.json`、`SmsSetting.json`、`LineSetting.json`。Engine、Web、Gateway 讀同一份。

安裝根目錄的唯一來源是 `Install.bat` 開頭的 `set "_ROOT=C:\JOULARIS"`；程式本身不寫死任何磁碟路徑。

## 目標機前置需求

安裝前請確認目標機已具備：

1. **ASP.NET Core Runtime 8.0.x (x64)**：一包同時涵蓋 Engine / Web / Gateway。下載：<https://dotnet.microsoft.com/download/dotnet/8.0> → ASP.NET Core Runtime → Windows x64。無網路的現場請把離線安裝檔放進 USB
2. **.NET Framework 4.8**：License Bridge 使用。Windows Server 2016 / 2019 與舊版 Windows 10 出廠沒有，需離線補裝並重開機
3. **SQL Server**（Express 亦可），執行安裝的 Windows 帳號需為 SQL sysadmin（安裝 SQL 的帳號預設即是）
4. **MQTT Broker**（例如 Mosquitto），預設 `127.0.0.1:1883`
5. **HASP USB 加密狗**插上；必要時安裝 Sentinel / HASP 驅動（`haspdinst.exe -i`）

> ⚠️ 注意：`Install.bat` 一開始就會檢查 .NET Framework 4.8 與 .NET 8 runtime，缺任一項就印出下載網址並**中止，機器完全不動**。只裝了 .NET 9 不算數，必須是 8.x。

> ⚠️ 注意：舊版 self-contained 的生產機升級到目前版本前，**務必先裝好 ASP.NET Core Runtime 8**，否則升完服務起不來。

## 建立 Release 包

在開發機的 repo 根目錄執行：

| 環境 | 指令 | 產出 |
|------|------|------|
| Windows | `.\BuildRelease.ps1`（或雙擊 `BuildRelease.bat`） | 完整包（含 Python 基礎包） |
| Windows | `.\BuildRelease.ps1 -NoPython` | 只含程式的更新包，體積較小 |
| macOS | `./BuildRelease.command -NoPython` | 同上；需 `~/.dotnet` 的 .NET 8 SDK 與 dotnet tool 版 pwsh |

產物位於 `Release\SCADA_Release_{日期時間}_v{版號}\`：

```
SCADA_Release_xxxxxxxx_xxxx_v26.x.x\
├── Install.bat                  主安裝 / 升級（Engine + Web + License）
├── InstallModbusServer.bat      Gateway 獨立安裝
├── reset-engineer-password.ps1  工程師工具，不落地伺服器
├── PythonRuntime\               Python 基礎包（-NoPython 時沒有）
├── Shared\                      共用設定檔範本
├── Engine\App\  Web\App\  LicenseBridge\App\  ModbusServer\App\
```

- 只改了程式、Python 沒變時用 `-NoPython`；首裝或 Python 套件有變更時用完整包
- `Install.bat` 會比對 Python 基礎包版號，相同就跳過，不重複複製
- Mac 無法產生 PythonRuntime；要在 Mac 產完整包，需先從任一 Windows 機複製 `C:\JOULARIS\Engine\App\PythonRuntime\` 到 repo 的 `ScadaEngine.Engine/PythonRuntime/`
- 版號來自 repo 根目錄 `Directory.Build.props`，會顯示在包名、Web 頁尾與服務描述

## 安裝 / 升級步驟

1. 把整個 Release 資料夾複製到目標機（USB、網路磁碟皆可）
2. **關閉 services.msc**，以及開在 `C:\JOULARIS` 底下的檔案總管、編輯器、命令列視窗
3. 對 `Install.bat` 按右鍵 → **以系統管理員身分執行**
4. 依畫面進度完成：

| 步驟 | 內容 |
|------|------|
| 前置檢查 | 系統管理員、.NET Framework 4.8、.NET 8 runtime |
| [0/8] | 偵測到舊根目錄 `C:\SCADA` 時自動遷移（見下節） |
| [1/8] | 停止既有服務；舊服務名自動刪除改用新名 |
| [2/8] | 安裝 Engine（升級時先備份現場設定、清除 App 根目錄舊程式檔、覆蓋、再還原設定）、鋪 Python 基礎包 |
| [3/8] | 安裝 Web |
| [4/8] | 安裝 License Bridge |
| [5/8] | 資料庫設定：搬移共用設定檔到 `Shared\`，執行 `install-db.ps1`（首裝時會要求設定 `engineer` 密碼） |
| [6/8] | 產生內網 HTTPS 憑證（已存在則略過，見 [內網 HTTPS](https)） |
| [7/8] | 防火牆放行 5038 / 7189 |
| [8/8] | 啟動服務，並列出三個服務的狀態 |

5. 確認最後三個服務皆為 `RUNNING`

> 💡 提示：一鍵部署（在生產機本機同時建包並安裝）可用 repo 根目錄的 `QuickDeployAll.bat`（系統管理員執行）：先執行 `BuildRelease.ps1`，再自動以最新的 Release 包跑 `Install.bat`。它**不會**動 Modbus Gateway。

> ⚠️ 注意：**不要**在生產機用各專案 `Scripts\QuickDeploy.bat` 的 Update（選項 6）。它會用 repo 版設定檔**直接覆蓋**現場設定（不備份），而且停服務後的進程清理會誤殺另一個服務的進程。生產機更新一律走 `QuickDeployAll.bat` 或 `Install.bat`。

## 首次安裝後要做的事

1. 編輯 `C:\JOULARIS\Shared\dbSetting.json`，填入 SQL Server 位址、資料庫名稱、帳號、密碼（三個程式共用這一份），然後重啟 `JoularisEngine` 與 `JoularisWeb`
2. 確認 `C:\JOULARIS\Engine\App\MqttSetting\MqttSetting.json` 的 broker 位址
3. 用 `engineer` 登入 `http://localhost:5038`，依 [角色體系與帳號 SOP](roles-accounts) 建帳號
4. 到 Modbus / DB / OPC UA 來源頁匯入 Excel 建立點位（首裝時三個資料夾只有空白範本）
5. Email / 簡訊在 **系統設定 → 警報設定** 的對應分頁設定；Line 需手動在 `Shared\LineSetting.json` 填 token
6. 視需要調整 `Engine\App\Setting\DbMaintenanceSetting.json` 的每週備份排程（見 [資料庫建立與備份](database-backup)）

## 升級時保留什麼

| 項目 | 升級後 |
|------|--------|
| Engine 的 `Modbus` / `DBPoint` / `OpcUaPoint` / `Setting` / `MqttSetting` | 保留現場版 |
| Web 的 `Setting` / `MqttSetting` | 保留現場版 |
| `Shared\` 四個共用設定檔 | 已存在就完全不動 |
| HTTPS 憑證 `Web\App\certs\*.pfx` | 沿用既有，不重產 |
| 程式、網頁、`Setting\*.ps1` 腳本、`DatabaseSchema.json` | 換成新版 |

從舊佈局（各 App 各自一份設定檔）升級時，`Install.bat` 會把 `dbSetting` 等四檔搬到 `Shared\`。若畫面出現 `[WARN] ... .web-backup`，表示 Web 那份設定與 Engine 那份內容不同，請人工比對 `Shared\EmailSetting.json` 與 `.web-backup`，把較新的 SMTP 帳密合併進去（或在 Web 設定頁重存一次），確認後刪除 `.web-backup`。

> ⚠️ 注意：Web 服務帳號需對 `C:\JOULARIS\Shared\` 有寫入權（預設 LocalSystem 沒問題）。若現場改成受限帳號，Web 儲存 SMTP / 簡訊設定會失敗。

## 從 C:\SCADA 遷移到 C:\JOULARIS

26.6.0 以前的站台安裝在 `C:\SCADA`。新版 `Install.bat` 偵測到 `C:\SCADA\Engine\App` 存在、且 `C:\JOULARIS\Engine\App` 不存在時，會在 [0/8] 自動執行 `Engine\App\Setting\migrate-install-root.ps1`，一次完成「搬家 + 升級」：

1. 停止相關服務與殘留進程
2. 資料檔位於 `C:\SCADA` 底下的資料庫先離線
3. 整個資料夾改名為 `C:\JOULARIS`
4. 資料庫改指新路徑後上線
5. 服務、設定檔、排程工作改指新根目錄
6. 列出仍含舊路徑的防火牆規則、環境變數供人工處理

- 停機時間約 1～2 分鐘
- 想先預演：`powershell -ExecutionPolicy Bypass -File <Release包>\Engine\App\Setting\migrate-install-root.ps1 -WhatIf`（只列出會做什麼，不動機器）
- 執行紀錄附加在 `C:\JOULARIS\install-log.txt`；腳本可重跑
- 最常見失敗原因是有視窗開在 `C:\SCADA` 底下導致檔案被鎖，關掉後重跑 `Install.bat` 即可

> ⚠️ 注意：遷移後**不得再執行 26.5.0 以前的 Install.bat**，它會在 `C:\SCADA` 重新安裝並把服務指回去。若誤跑，用新版 `Install.bat` 再跑一次，再人工刪除 `C:\SCADA`。

## 安裝後驗證

1. `sc query JoularisEngine`、`sc query JoularisWeb`、`sc query JoularisLicense` 皆為 `RUNNING`
2. 開 `http://localhost:5038` 可登入，頁尾版號為本次安裝的版號
3. **services.msc** 中服務描述的版號與建包日期正確
4. 頁面頂端**沒有**紅色「Engine 授權驗證失效」橫幅
5. 即時數據頁的點位有在更新

## 常見狀況

| 狀況 | 原因 / 處理 |
|------|------------|
| Engine / Web 服務裝好卻是 STOPPED，License 正常 | 未裝 ASP.NET Core Runtime 8；或 App 根目錄殘留舊 self-contained 版的 runtime 檔（`Install.bat` 會自動清除，手動搶修時把 App 根目錄中新包沒有的檔案搬走，子資料夾不要動） |
| 服務一直是「停用」 | 舊站台曾被改成停用；新版 `Install.bat` 會自動改回自動啟動 |
| 舊服務名（`ScadaEngineService` 等）還在 | 安裝時 services.msc 開著，舊服務只標記待刪除，重開機後消失，不影響功能 |
| Web 與 Engine 好像連到不同資料庫 | 比對兩者啟動 log 的「資料庫設定檔路徑」行，應同為 `C:\JOULARIS\Shared\dbSetting.json`，見 [疑難排解](troubleshooting) |

## 回滾到舊版

回滾到使用 `Shared\` 之前的舊版 Release（26.4.x / 26.5.x 以前）時：

1. 先把 `C:\JOULARIS\Shared\` 內的設定檔複製到 `Engine\App\Setting\` 與 `Web\App\Setting\`（兩處都要；回滾到 26.4.x 以前只需 `dbSetting.json`，26.5.x 以前四檔都要）
2. 再執行舊版 `Install.bat`
3. `Shared\` 資料夾留著無妨，日後再升回新版會直接沿用
