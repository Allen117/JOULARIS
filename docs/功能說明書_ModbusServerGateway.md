# 功能說明書 — Modbus Server Gateway（即時資料對外 Modbus TCP Server）

獨立專案 `ScadaEngine.ModbusServer`：把系統所有即時點位值以標準 **Modbus TCP / FC4（Input Registers）/ float32** 對外提供，讓第三方 SCADA / PLC / HMI 用現成 Modbus 驅動即可讀取本系統即時資料。自帶輕量瀏覽網頁（位址對照表 + 即時值），**獨立安裝**（`InstallModbusServer.bat`，不在主 `Install.bat` 範圍內）。

---

## 目錄

- [定位與架構](#定位與架構)
- [對接規格（給第三方）](#對接規格給第三方)
- [位址規劃與 AddressMap](#位址規劃與-addressmap)
- [資料來源與品質規則](#資料來源與品質規則)
- [內建瀏覽網頁](#內建瀏覽網頁)
- [設定檔](#設定檔)
- [安裝 / 升級 / 解除安裝](#安裝--升級--解除安裝)
- [常見問題](#常見問題)

---

## 定位與架構

```
Engine ──MQTT(SCADA/Realtime/+/+)──▶ ┌──────────────────────────────┐
                                     │  ScadaEngine.ModbusServer    │
Engine ──SQL(LatestData 啟動預填)──▶ │  ┌────────────────────────┐  │
外部系統 ──SQL(DBLatestData 輪詢)──▶ │  │ RealtimeCache (SID→值) │  │
                                     │  └───────┬──────┬─────────┘  │
                                     │   AddressMap    │            │
                                     │        ▼        ▼            │
                                     │  FluentModbus   Kestrel      │
                                     │  ModbusTcpServer 瀏覽網頁    │
                                     └──────┬──────────┬────────────┘
                                        :502 FC4    :5041 HTTP
```

- **獨立進程 / 獨立安裝**：客戶不一定需要對外 gateway；Modbus Server 開 TCP listener 與 Engine 採集職責無關，獨立進程掛掉不影響採集本體。
- **只引用 `ScadaEngine.Common`**，不引用 Engine 專案 — 保持輕量可獨立部署（不夾帶 Python runtime / OPC UA client / 簡訊等）。
- **推送式更新**：快取值每秒（`RegisterSweepMs`）編碼寫進 server register buffer，Modbus 讀取端永遠拿到最新已寫值，不做 per-request 查 DB。
- 可裝在同一台或另外的機器（設定檔指向主機的 SQL / MQTT 即可）。

| 項目 | 值 |
|------|-----|
| 專案 | `ScadaEngine.ModbusServer`（net8.0，Kestrel + Worker） |
| Windows 服務名 | `JoularisModbusGateway`（顯示名 JOULARIS Modbus Gateway；描述帶 Backend 角色 + 版號。v26.1.0 前為 `ScadaModbusGatewayService`，InstallModbusServer.bat 升級時自動停用並刪除舊名） |
| 部署路徑 | `C:\JOULARIS\ModbusServer\App` |
| 預設埠 | Modbus TCP **502** / 瀏覽網頁 HTTP **5041**（安裝時可改，見下；刻意避開 Win10/11 內建 CDPSvc 必占的 5040） |
| 日誌 | 服務模式寫 `Log\ModbusGateway-*.log`（Serilog，30 天輪替） |

## 對接規格（給第三方）

| 項目 | 規格 |
|------|------|
| 協定 | Modbus TCP |
| Function Code | **FC4 Read Input Registers**（唯讀 — 量測值語意） |
| Unit ID | **必須用設定檔的 `UnitId`（預設 1）**。用錯 unit id 連線會被直接關閉（FluentModbus 行為），不是回例外碼 |
| 資料型別 | 每點 **float32 = 2 registers**，位址一律偶數對齊（0, 2, 4, …） |
| Word order | 預設 **CDAB**（低字組在前 = ModScan「Floating Pt」）；設定可切 `ABCD`（= ModScan「Swapped FP」；本專案採集端 DataType `SWAPPEDFP` 同術語） |
| 品質不良 | 該點回報 **NaN（0x7FC00000）**；設定可切 `HoldLast`（保持最後值） |
| 位址表示 | 0-based register 位址；網頁同時顯示 6 位數擴充表示法 **300001 + 位址**（位址段超過 9999，4 位數 3xxxx 表示法不夠用） |

**批次讀取相容性**：NaN 是值的位元編碼、**不是 Modbus 例外** — 一次 FC4 讀一整串 register 時整包正常成功，只有壞點自己那 2 個 register 呈 NaN 位元樣式（ModScan 顯示 NaN / 1.#QNAN），其餘點不受影響。

CDAB 編碼範例：float 的 IEEE754 big-endian 四位元組記為 `A B C D`（A=最高位元組），則線路上 `reg[n] = (C,D)`、`reg[n+1] = (A,B)`。123.456f（0x42F6E979）→ 線路位元組 `E9 79 42 F6`。

## 位址規劃與 AddressMap

### 分段配置（0-based，預設段界，`ModbusServerSetting.json` 可調）

| 來源類型 | 起始位址 | 範圍 | 可容納點數 | SID 樣式 |
|----------|---------|------|-----------|----------|
| Modbus 採集點 | 0 | 0–9999 | 5000 點 | `196865-S1` |
| 計算點位 | 10000 | 10000–13999 | 2000 點 | `CALC-S1` |
| DB 來源 | 14000 | 14000–19999 | 3000 點 | `DB1-S1` |
| OPC UA 來源 | 20000 | 20000–25999 | 3000 點 | `OPC1-S1` |
| 保留 | 26000 | 26000–65535 | — | — |

### 配址規則（append-only，位址永遠不變）

1. 首次啟動掃描四類點位表（`ModbusPoints` / `CalculatedPoints`(僅啟用) / `DBPoints` / `OpcUaPoints`），段內依 **SID 自然排序**（S2 < S10）自段首依序配 0, 2, 4, …，寫入 **`AddressMap.json`**（app 根目錄）。
2. 之後每次啟動/重掃**只把新點 append 到該來源段尾**，永不重排、永不回收 — 下游 PLC/HMI 寫死的位址永久有效。
3. **刪除/停用的點位**：位址保留成空洞，值依品質規則回報（預設 NaN），網頁標示「已停用」。
4. 段溢位（新點配不進段界）→ log 錯誤並沿用既有對照，需擴大段容量後重啟（⚠️ 改段界僅限初次規劃期 — 既有配址若落在新段界外不會自動搬移）。
5. `AddressMap.json` **是位址對照的持久化真相，升級時必須保留**（`InstallModbusServer.bat` 升級流程自動備份還原）；刪掉重建 = 全部位址重排 = 現場事故。檔案損毀時服務會拒絕啟動並要求人工處理（不會拿空表重配）。

### 點位重掃時機

- 啟動時
- 收到 MQTT `SCADA/Sys/DbCoordinator/Reload` 或 `SCADA/Sys/OpcUaCoordinator/Reload`（Web 端改點後自動發布）
- 網頁「重新掃描點位」按鈕（`POST /api/rescan`）

## 資料來源與品質規則

三路合流進同一份快取（複製 Web `MqttRealtimeSubscriberService` 的成熟模式）：

1. **MQTT `SCADA/Realtime/+/+`**（主要路徑，含 retain 立即補值）— Modbus / 計算 / OPC UA 點位
2. **啟動 / 重掃時讀 `LatestData` 預填**（不覆蓋已到的 MQTT 值）
3. **DB 來源點位不走 MQTT** — 每 `DbPollSeconds`（預設 2 秒）輪詢 `DBLatestData`；SQL 讀取成功即 GOOD（不看時間戳），失敗全標 BAD

對外供值判定（Modbus 與網頁同一套規則）：

| 情況 | 預設（NaN 模式） | HoldLast 模式 |
|------|------------------|---------------|
| 從未有值 | NaN | NaN |
| 品質 GOOD 且新鮮 | 實際值 | 實際值 |
| 品質 Bad | NaN | 最後值 |
| MQTT 點位超過 `StaleSeconds`（預設 300）未更新（Engine 斷線） | NaN | 最後值 |
| 點位已刪除/停用 | NaN | 最後值 |

Engine 重啟 / MQTT 斷線後自動重連（30 秒檢查週期）+ broker retained 訊息補值，值自動恢復更新。

## 內建瀏覽網頁

`http://<主機>:5041/`（埠可改）。**唯讀工具頁、不做登入**（無控制能力，靠防火牆/內網管控）。

- 欄位：位址（0-based + 3xxxxx 兩種表示法）、SID、名稱、來源類型、原始位址（Modbus=採集位址、OPC=TagName、DB=`DB{Coordinator}#{Seq}`）、即時值、單位、狀態（GOOD/BAD/STALE/NO_DATA/已停用）、更新時間
- 關鍵字搜尋 + 來源類型/狀態篩選；**每 5 秒**自動刷新（打的是本進程記憶體快取的 HTTP API，不經 MQTT broker）
- 頂部狀態卡：Modbus 監聽狀態、Word order / Unit ID、MQTT 連線、點位總數/存活數、最後掃描時間
- 「重新掃描點位」按鈕：Engine 加點後手動觸發（或等 Reload MQTT 自動觸發）

API：`GET /api/points`（對照表 + 即時值 JSON）、`POST /api/rescan`。

## 設定檔

### `Setting/ModbusServerSetting.json`

```json
{
  "ModbusListenAddress": "0.0.0.0",
  "ModbusListenPort": 502,
  "UnitId": 1,
  "WordOrder": "CDAB",          // 或 "ABCD"
  "BadQualityMode": "NaN",      // 或 "HoldLast"
  "WebPort": 5041,                // 預設避開 CDPSvc 必占的 5040
  "DbPollSeconds": 2,           // DBLatestData 輪詢間隔
  "StaleSeconds": 300,          // MQTT 點位視為斷線的門檻
  "RegisterSweepMs": 1000,      // 快取 → register buffer 掃寫週期
  "Segments": { "Modbus": { "Start": 0, "CapacityPoints": 5000 }, ... }
}
```

僅啟動時載入，改後需重啟服務。

### 其他

| 檔案 | 用途 |
|------|------|
| dbSetting.json（SQL 連線，格式同 Engine） | **與 Engine / Web 同機**：讀 `C:\JOULARIS\Shared\dbSetting.json`（三者共用一份，`Setting\` 不留複本）。**裝在別台機器**：`InstallModbusServer.bat` 從 `Setting/dbSetting.example.json` 鋪一份 `Setting/dbSetting.json`，改成主機 IP / 帳密。解析順序（環境變數 → `..\..\Shared` → 本機 `Setting` → 開發 repo）見 [架構.md §共用設定檔解析順序](架構.md#共用設定檔解析順序)；啟動 log 印「資料庫設定檔路徑 … 來源」可核對 |
| `MqttSetting/MqttSetting.json` | MQTT broker（格式同 Engine） |
| `AddressMap.json` | 配址持久化（自動產生，**勿刪**，升級自動保留） |

## 安裝 / 升級 / 解除安裝

### 正式站台 — `InstallModbusServer.bat`（release 包根目錄，管理員執行）

刻意**不併入主 `Install.bat`** — 客戶可能不需要對外 gateway。流程：

1. 偵測升級 → 備份 `Setting/`、`MqttSetting/`、`AddressMap.json`
2. 停既有服務 → xcopy → 還原站台設定
3. **埠占用檢查**（讀站台設定的實際埠）：502 / 5041 已被占用時顯示占用進程，**提示輸入替代埠**（建議 1502 / 5042；直接 Enter 用原埠重測），迴圈重驗直到可用；確定後自動寫回 `ModbusServerSetting.json`。占用者是本服務自己（升級情境）視為正常跳過
4. sc.exe 註冊（failure restart 同 Engine）→ 防火牆開**實際選定的埠** → 啟動

> 預設網頁埠取 **5041** 是因為 Windows 10/11 內建 CDPSvc（svchost）幾乎必占 5040（2026-09-11 本機實測撞上後，使用者裁定改預設）。若 5041 也被占用，安裝程式會自動偵測並引導改埠。

### 開發機 — `Scripts\DeployModbusServer.ps1` / `QuickDeploy.bat`

`install / uninstall / start / stop / restart / update / logs / status`，仿 Engine `DeployService.ps1`。uninstall 會停服務、刪服務、移除防火牆規則，並詢問是否刪檔案（刪檔案會失去 `AddressMap.json`）。

### Console 驗證

`dotnet run`（或直接跑 exe）即 console 模式，log 出在 console。搭配 ModScan / mbpoll 讀 FC4 比對網頁即時值。

## 常見問題

| 症狀 | 原因 / 處理 |
|------|-------------|
| 客戶端一連就斷、讀不到 | Unit ID 不對 — 必須用設定檔 `UnitId`（預設 1）。FluentModbus 對未註冊 unit id 直接斷線 |
| float 值亂碼 | Word order 不合 — 預設 CDAB（ModScan「Floating Pt」）；客戶端若解成 ABCD 請切換其驅動設定或本服務 `WordOrder` |
| 讀到 NaN | 該點品質 Bad / Engine 斷線逾時 / 點位已刪除 — 開瀏覽網頁看該位址狀態欄 |
| 全部點 NaN 但網頁正常 | 讀的位址不對（每點 2 registers、偶數位址）；或讀成 FC3（本服務只供 FC4） |
| 網頁埠起不來 | `WebPort` 被其他程式占用（勿用 5040 — Windows CDPSvc 必占）— 改埠重啟；服務端 log 會列印占用訊息並每 30 秒重試 |
| Engine 加點後 gateway 沒有新點 | 按網頁「重新掃描點位」；Modbus 採集點新增目前無 Reload MQTT，需手動重掃或重啟 |
| 段滿（log 出現「位址段已滿」） | 擴大該段 `CapacityPoints` 後重啟；⚠️ 不可改既有段的 `Start` |

---

**單元測試**：`ScadaEngine.Tests/ModbusServer/` — 配址 append-only 全規則、float 編碼（ABCD/CDAB/NaN）、FluentModbus loopback 線路位元組實測（buffer bytes = wire bytes）、供值規則。
