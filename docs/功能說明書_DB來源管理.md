# 功能說明書：DB 來源管理 (DbCoordinator)

## 1. 功能概述

DB 來源是 Modbus / 計算點位之外的**第三類點位來源**。專供「資料早就被別的系統寫進 SQL 表了」這類場景使用：外部系統（PLC、第三方系統、手動匯入工具）只負責把每筆量測值（**已是工程值**）寫進統一入口表 `DBLatestData`，Engine 自動 polling 後直接 UPSERT LatestData / HistoryData、發布 MQTT，於是這些「外部資料」就能享有警報、計算點位、能源報表、即時數據等所有 SCADA 既有功能 — 對下游而言完全是透明的。

### 核心能力
- **零下游改動**：採既有 SID 字串型別與 `DB{Id}-S{N}` 格式，警報 / 計算 / 能源報表 / MQTT 全部不需改判斷邏輯
- **每個 Coordinator 100 個 slot**：Sequence 由 JSON 陣列順序自動產生（與 Modbus 對齊），1~100
- **設定來源 = Web 匯入 Excel**：`/DbCoordinator` 頁「匯入 Excel」上傳 `DB來源範本.xlsx`（一張工作表 = 一個 Coordinator）→ 預覽差異 → 確認 → Web 寫 `DBPoint/{SheetName}.json` 並自動發 Reload（見 §8.4）。JSON 仍是 Engine 的執行期唯一設定來源，使用者不再直接接觸；舊 `DB通訊檔案產生工具.xlsm` 巨集已移除（舊 xlsm 檔可直接上傳）
- **即時生效**：匯入／刪除／改名後 Web 自動發 MQTT `SCADA/Sys/DbCoordinator/Reload` → Engine 重讀 JSON、UPSERT DB、重建 polling loops，免重啟 Engine；頁面另保留「通知 Engine 重新載入 JSON」按鈕作為手動放檔或 MQTT 失敗時的補救
- **DBLatestData.Value 即工程值**：外部系統直接寫工程值，Engine 不再做任何縮放（不再有 Ratio）
- **UI 以唯讀為主，點位名稱/單位可編輯**：Web `/DbCoordinator` 顯示已載入結果（Coordinator + 點位列表）；點位**名稱與單位**可行內編輯並回寫 JSON（見 §8.3），結構性編輯（增刪點位、改 Min/Max、新增／刪除 Coordinator）透過「匯出 Excel → 修改 → 匯入 Excel」或「刪除來源」按鈕（見 §8.4）

---

## 2. 路由

| 方法 | 路由 | 說明 | 認證 |
|------|------|------|------|
| GET  | `/DbCoordinator`        | DB 來源管理頁面（清單 + 點位名稱/單位編輯） | 需登入 |
| POST | `/DbCoordinator/Reload` | 通知 Engine 重新載入 `DBPoint/*.json`（發 MQTT） | 需登入 |
| POST | `/DbCoordinator/UpdatePoint` | 更新單一點位名稱+單位（回寫 JSON + UPSERT DBPoints + 自動發 reload MQTT） | 需登入 |
| GET  | `/DbCoordinator/ImportTemplate` | 下載空白 Excel 範本（`DB來源範本.xlsx`） | Engineer |
| GET  | `/DbCoordinator/ExportExcel?workbook=` | 匯出現行設定為 .xlsx **下載到瀏覽器**（一個 JSON 一張工作表；`workbook` 省略 = 全部） | Engineer |
| POST | `/DbCoordinator/ExportToFolder?workbook=&force=` | 同上但**寫進來源資料夾**（`EngineDbPointConfig:WatchedFolder`，全部 → `DB來源設定.xlsx`、依來源 Excel → 原檔名；覆寫留 `.bak`）；Excel 開著 / 需確認覆寫回 409 | Engineer |
| GET  | `/DbCoordinator/FolderWorkbooks` | 來源資料夾內的 Excel 檔清單（含是否有未匯入修改） | Engineer |
| POST | `/DbCoordinator/ImportPreviewFromFolder` | `{ fileName }` 以來源資料夾內的 Excel 預覽，之後同樣走 ImportCommit | Engineer |
| POST | `/DbCoordinator/ImportPreview` | 上傳 .xlsx/.xlsm → 解析、驗證、比對既有 JSON → 預覽 + `token`（不寫檔） | Engineer |
| POST | `/DbCoordinator/ImportCommit` | 帶 token + 勾選結果提交 → 寫檔／刪檔 → **自動發 Reload**；預覽後設定被更動回 409 | Engineer |
| POST | `/DbCoordinator/DeleteSource` | `{ name }` 逐台刪除（JSON 移到 `_deleted/`）→ 自動發 Reload | Engineer |
| GET  | `/DbCoordinator/SourceStatus` | `{ loaded[], existing[] }` — 前端匯入後輪詢等 Engine Reload 寫完 DB 再重新整理 | Engineer |

---

## 3. SID 命名

```
DB{CoordinatorId}-S{Sequence}
```

例：`DB1-S1`、`DB1-S100`、`DB2-S5`

- `CoordinatorId` 由 `DBCoordinator` 表 Identity 產生
- `Name UNIQUE + UPSERT by Name` → 同名 sheet 永遠拿到同一個 Id → SID 永遠穩定
- 與 `{ModbusID}-S{N}`、`CALC-S{N}` 純文字前綴不撞號

---

## 4. 資料表

### 4.1 `DBCoordinator`

| 欄位 | 型態 | 說明 |
|------|------|------|
| `Id` | int IDENTITY PK | SID 中的 `{CoordinatorId}` |
| `Name` | nvarchar(100) UNIQUE | = sheet name = JSON 檔名 |
| `PollingInterval` | int (default 1000) | 毫秒；Engine 內 clamp 下限 200ms |
| `ConnectTimeout` | int (default 1000) | 毫秒；polling 讀 DBLatestData 的 SqlCommand.CommandTimeout（向上取整為秒，最小 1 秒） |
| `MonitorEnabled` | bit (default 1) | 整個 Coordinator 開關 |
| `CreatedAt` | datetime | 系統維護 |

### 4.2 `DBPoints`

| 欄位 | 型態 | 說明 |
|------|------|------|
| `SID` | nvarchar(100) PK | `DB{Id}-S{Seq}` |
| `CoordinatorId` | int | FK → DBCoordinator.Id |
| `Sequence` | int (1~100) | 序號；由載入器以 JSON 陣列索引+1 自動產生（JSON 不顯式指定，與 Modbus 對齊） |
| `Name` | nvarchar(100) | 點位名稱 |
| `Unit` | nvarchar(50) | 物理單位 |
| `Min` | float (default 0) | 顯示下限 + 控制寫入下限（寫入路徑做時驗證） |
| `Max` | float (default 100) | 顯示上限 + 控制寫入上限（寫入路徑做時驗證） |

### 4.3 `DBLatestData`（統一入口表）

| 欄位 | 型態 | 說明 |
|------|------|------|
| `SID` | nvarchar(100) PK | 例 `DB1-S5` |
| `Value` | float | **工程值**（外部系統直接寫工程值，Engine 不再做縮放） |
| `Timestamp` | datetime | 由外部系統決定 |
| `Quality` | int | **只有明確寫 0 才是 Bad**；1 / NULL / 其他值一律 Good（26.9.2 起，SQL 端 `ISNULL(Quality,1)`） |

> 外部系統只需 `INSERT/UPDATE WHERE SID='DB{n}-S{m}'`，**只改 Value 不碰 Quality 也可以**。Engine 寫入路徑（Phase 2）也統一 UPDATE 此表，欄位 schema 永遠不變。
>
> **Quality 語意（26.9.2 起）**：寫入端要主動宣告「這個值不可信」時才寫 `Quality=0`（例：Web `WeatherFetchService` 在 API 失敗 / 觀測過舊時寫 0，防止舊溫度餵能源基線）。匯入點位時的 seed 列 Quality=1；Engine 判 Bad 的唯一條件是「該列 Quality 明確為 0」或「整個 SQL 讀取失敗」。
> 26.9.1 之前 seed 為 0 且 Engine 要求 Quality 嚴格等於 1，導致外部 / 手動只改 Value 的點位永遠 Bad，下游計算點跟著 Bad 而 Web 即時頁卻顯示 GOOD。既有生產機升級後需一次性修正舊 seed 列（排除氣象 Coordinator 以免蓋掉其刻意寫的 0）：
>
> ```sql
> UPDATE DBLatestData SET Quality = 1
> WHERE Quality = 0
>   AND SID NOT IN (SELECT p.SID FROM DBPoints p JOIN DBCoordinator c ON c.Id = p.CoordinatorId WHERE c.Name = 'Weather');
> ```

---

## 5. Data Flow

```
外部系統（PLC / 第三方 / 手動）
    ↓ INSERT/UPDATE WHERE SID='DB1-S5'  （Value 直接寫工程值）
DBLatestData
    ↓ polling (每 PollingInterval ms，CommandTimeout = ConnectTimeout)：SELECT WHERE SID LIKE 'DB1-%'
DbCommunicationService (Engine BackgroundService)
    ↓
    ├─ HistoryData 路徑（無條件）：所有已配置 row → HistoryDataStorageService.AddRealtimeDataBatch()
    │   → 分鐘 key dedup + 每 60 秒 Timer 批次寫 HistoryData
    │   → Timestamp = polling 當下時間（截到分鐘），與 Modbus 一致
    │
    └─ 變化偵測（值/品質/Timestamp 任一不同才繼續，僅擋下方四條路徑）
        ↓ UPSERT LatestData (Timestamp = DBLatestData.Timestamp)
        ↓ MqttPublishService → SCADA/Realtime/{Name}/{SID}
        ↓ AlarmMonitorService.EvaluateBatchAsync
        ↓ CalculatedPointService.CalculateAndPublish
SCADA 既有 pipeline（警報 / 計算點 / 能源報表 / Web 即時數據）
```

讀失敗分支（`GetDbLatestDataByPrefixAsync` 拋例外）：

```
SQL exception
    ↓
若 HasEverReadSuccessfully == false → 直接 return（避免 Engine 剛啟動就連線失敗時灌一堆 0 進歷史）
若 HasEverReadSuccessfully == true → 對 PointsBySid 全部 SID 構造
    { Value = LastSeen 最近一次成功值（無則 0）, Quality = "Bad", dtTimestamp = Now }
    → 同樣餵給 HistoryDataStorageService（保留歷史軌跡，明確標 Bad）
```

關鍵設計：
- **HistoryData.Timestamp 用 polling 當下時間（分鐘對齊）**：與 Modbus 行為一致，每分鐘穩定一筆。`DBLatestData.Timestamp` 僅作變化偵測比對基準，不再寫進 HistoryData
  - 動機：外部系統若停止更新 DBLatestData（Timestamp 完全停滯），HistoryData 仍要有完整時間軸供 trending / 用電報表使用
  - 推翻早期決策（HistoryData.Timestamp = DBLatestData.Timestamp）— 該設計在外部停寫場景會出現大段空缺
- **DBLatestData.Value 即工程值**：外部系統把已換算好的工程值寫進來，Engine 不再做 raw × Ratio 換算（DB 來源語意上就應該是已處理過的值）
- **Quality 只認「明確 0」**：`szQuality = row.nQuality != 0 ? Good : Bad`，NULL 在 SQL 端已轉 1。Web 即時頁 `RefreshDbSourcesAsync` 用同一規則（SQL 失敗全 BAD；讀成功只有該列 Quality=0 才 BAD），三處（Engine / Web / 下游計算點）看到的品質一致
- **變化偵測只擋下游噪音通道**：MQTT / 警報 / 計算點 / LatestData 在「值/品質/Timestamp 三者皆相同」時跳過，避免靜態資料反覆刷；HistoryData 不擋，依靠分鐘 dedup 自然降頻
- **讀失敗 → 寫 Bad 歷史**：DBLatestData 連線 / SQL 失敗時，若曾經成功讀過至少一次，仍寫 HistoryData（Quality=Bad，Value 用最近一次成功值），讓 trending 能標出資料中斷區間
- **未配置 SID**：DBLatestData 出現 `DB99-S1` 但 Coordinator 99 不存在 → log warning 一次後跳過，不爆

---

## 6. 設定檔示範：`DBPoint/Production.json`

```jsonc
{
  "Name": "Production",
  "PollingInterval": 1000,
  "ConnectTimeout": 1000,
  "MonitorEnabled": true,
  "Points": [
    { "Name": "CH1MOA",   "Unit": "",    "Min": 0, "Max": 100 },
    { "Name": "CH1RUN",   "Unit": "",    "Min": 0, "Max": 100 },
    { "Name": "CH1ALARM", "Unit": "",    "Min": 0, "Max": 100 },
    { "Name": "CH2MOA",   "Unit": "",    "Min": 0, "Max": 100 }
  ]
}
```

JSON 內**不放 Id 也不放 Sequence**：
- `Id`：由 Engine 啟動時 UPSERT by Name 從 DBCoordinator 表取得
- `Sequence`：由載入器以 JSON 陣列索引+1 自動產生（與 Modbus 一致）

⚠️ **編輯守則**：在 Excel 中加新點位**一律加最後一行**，不要插入中間，否則後續點位的 SID 會位移，破壞既有的警報規則 / 計算點 / 畫面綁定。

對應產生：
- DBCoordinator: `Id=1, Name='Production', PollingInterval=1000, ConnectTimeout=1000, MonitorEnabled=1`
- DBPoints: `DB1-S1`（CH1MOA, Min=0, Max=100）、`DB1-S2`（CH1RUN）、`DB1-S3`（CH1ALARM）...

外部系統要做的：把工程值寫入 `DBLatestData`，例 `DB1-S2` 寫 Value=1（即工程值，不再有 Ratio 換算）。

---

## 7. Reload 機制（Web → Engine 即時生效）

```
使用者在 Web /DbCoordinator 匯入 Excel → 預覽確認 → Web 寫 JSON → Controller 自動發 Reload
（或手動放檔後按「通知 Engine 重新載入 JSON」）
    ↓
DbCoordinatorReloadPublisher (Web Singleton + IHostedService)
    ↓ MQTT 發布 SCADA/Sys/DbCoordinator/Reload   QoS=1, Retain=false
    ↓
DbCoordinatorReloadSubscriber (Engine BackgroundService)
    ↓ DbCommunicationService.ReloadAsync()
        1) 取消舊的所有 polling loops（CancellationTokenSource.Cancel）
        2) DbCoordinatorJsonLoader.LoadAllAsync 重讀全部 JSON、UPSERT DB
        3) 為每個 enabled Coordinator 啟動新的 polling loop
```

設計重點：
- **Retain=false**：Engine 重啟時不會 replay 最後一筆 reload（Engine 啟動本來就會做完整 reload）
- **SemaphoreSlim(1,1) 序列化**：避免 reload 與 polling loop 半更新狀態
- **失敗只 log**：MQTT broker 失聯時 reload 通知不擋 UI，使用者重按或重啟 Engine 即可

---

## 8. UI

### 8.1 Web 頁面 `/DbCoordinator`

採用與 `/ModbusCoordinator` 一致的「左側清單 + 右側詳情」雙欄佈局：

- **上方按鈕列**：「下載範本」「匯出 Excel（全部／依來源 Excel 檔）」「匯入 Excel」+「重新整理頁面」「通知 Engine 重新載入 JSON」
- **左側「DB 通訊」卡**：列出所有已載入的 DB Coordinator（依 `DBPoint/*.json` 順序），停用者顯示灰色「停用」徽章；DB 有紀錄但 JSON 已移除者顯示黃色「設定檔已移除」徽章；JSON 已匯入但 Engine 尚未載入（DB 無紀錄）者顯示藍色「待 Engine 載入」徽章（清單本身讀 DB，Coordinator 要等 Engine Reload 後才會出現）
- **右側「設備詳細資料」卡**：點擊左側項目後顯示該 Coordinator 的 Id / Name / PollingInterval / ConnectTimeout / MonitorEnabled / 點位數，下方附**點位列表**（SID / 名稱 / 單位 / Min / Max，名稱與單位可行內編輯）
- 預設自動載入第一筆 Coordinator；詳情卡右上「刪除來源」按鈕可逐台刪除（確認後移到 `_deleted/`、自動發 Reload）
- 結構性編輯（增刪點位、改 Min/Max、增刪 Coordinator）透過「匯出 Excel → 修改 → 匯入 Excel」，提交後自動通知 Engine 重載（見 §8.4）

### 8.2 SID 下拉清單整合

DB 來源 SID 已自動納入下列功能的「選擇 SID」下拉：
- 警報設定（`/AlarmSetting`）
- 條件控制（`/ConditionCtrl`）
- 計算點位的輸入變數（`/CalcPoint`）
- 歷史趨勢（`/History/Trend`）
- 畫面設計（`/Designer`）— 點位 GroupName="DB"
- 電表/迴路設定（`/EnergyMeter`）— 僅 Unit=kWh 的點位
- 即時數據快取預填（`/RealTime`）

### 8.3 點位名稱/單位編輯（回寫 JSON）

點位列表中雙擊名稱或單位（或點鉛筆 icon）進入行內編輯（名稱 + 單位同一編輯列），Enter / ✓ 儲存、Esc / ✗ 取消。

```
使用者儲存
    ↓ POST /DbCoordinator/UpdatePoint { id, sequence, newName, newUnit }
DbPointConfigFileService（Web，Scoped；static SemaphoreSlim 檔案互斥）
    1) 讀 WatchedFolder/{Name}.json（依 BOM 自動偵測編碼 — Excel 巨集產出為 UTF-16 LE）
    2) 只改 Points[sequence-1] 的 Name / Unit — 陣列順序與數量不動，SID 不位移
    3) 原子寫回：*.json.tmp → File.Replace（留 *.json.bak），統一 UTF-8 indented
       MirrorFolder 有設定時同步鏡像寫回原始碼資料夾
    4) 以剛寫回的 JSON 為準 UPSERT DBPoints（走既有 SaveDbPointsAsync，映射與 Engine 載入器一致）
    ↓ 成功後 Controller 自動發 reload MQTT（SCADA/Sys/DbCoordinator/Reload）
Engine 熱重載 → 之後 MQTT payload / Realtime 頁即帶新名稱；依 Unit 篩選的自動帶入功能
（如電表/迴路設定只列 Unit=kWh 點位）即認得新單位
```

設計要點：

- **只開放改 Name / Unit**：SID = `DB{Id}-S{陣列索引+1}`，增刪或重排會使後續 SID 位移、歷史資料錯接；改名/單位不動陣列結構，絕對安全。單位驗證：≤50 字元、可空白；名稱：非空、≤100 字元
- **JSON 仍為唯一真相來源**：只改 DB 會在下次 Engine 重載時被 JSON 蓋回去，故必須回寫檔案
- **寫檔路徑由 `appsettings.json` 的 `EngineDbPointConfig` 明定**（`WatchedFolder` + `MirrorFolder`，比照 `EngineModbusConfig` / `EngineOpcUaConfig` 慣例）；`WatchedFolder` 未設定時儲存回傳明確錯誤，不猜路徑
- **reload MQTT 發布失敗不回滾**：點位已存檔，UI 提示使用者手動按「通知 Engine 重新載入 JSON」

ℹ️ 預覽整份都不可勾選（工作表全是錯誤／無變更、無刪除候選）時，頂部黃底與底部狀態列會說明原因（例如上傳了空白範本）；規則與 Modbus 來源共用，見 `功能說明書_Modbus來源管理.md` §預覽分類與確認規則。

⚠️ **Excel 匯入覆蓋須知**：匯入 Excel 時若該工作表的名稱/單位與 Web 端改過的不同，預覽會列為「覆寫」並逐點顯示 `Name: 舊 → 新`，需勾選確認才會寫入 — 建議「先匯出再改」，讓 Excel 以現行 JSON 為底。

### 8.4 Excel 匯入／匯出／刪除（取代 xlsm 巨集）

共用框架與預覽／確認／刪除候選／SID 位移規則**與 Modbus 完全相同**，見 [功能說明書_Modbus來源管理.md](功能說明書_Modbus來源管理.md) §4.1；DB 來源的差異只有版面、驗證與提交後處理：

| 項目 | DB 來源 |
|------|---------|
| 範本版面 | 第 1 列：`A1~D1` 欄位標題、`F1=ConnectTimeout G1=1000`、`H1=PollingInterval I1=1000`、`J1=MonitorEnabled K1=TRUE`（以標籤掃描第 1 列，找不到才退回巨集的 G1/I1/K1）；第 2 列起 `A~D = Name / Unit / Min / Max` |
| 預設值 | ConnectTimeout／PollingInterval 空白 → 1000；MonitorEnabled 空白 → TRUE（接受 TRUE/FALSE/1/0/是/否/Y/N）；Min/Max 空白 → 0/100（與巨集相同） |
| 驗證 | 名稱 ≤100 字、單位 ≤50 字、Min/Max 數字；**超過 100 點列為錯誤**（巨集是靜默截斷） |
| JSON | `Name` = 工作表名稱、`Min/Max` 數字、`MonitorEnabled` 布林、頂層 `SourceWorkbook`；新檔 UTF-8 BOM（與 §8.3 回寫一致） |
| 提交後 | Controller 自動發 `SCADA/Sys/DbCoordinator/Reload`；Engine `ReloadAsync` 取消全部 polling loop、依**現有** JSON 重建 → 被刪除（移到 `_deleted/`）的 Coordinator 自然停止，**Engine 不需改** |
| 刪除 | `DBCoordinator` / `DBPoints` / 歷史資料都保留；頁面標示「設定檔已移除」；同名重新匯入 Id 不變、SID 一致 |

Adapter：`ScadaEngine.Web/Services/SourceExcel/DbPointExcelAdapter.cs`；範本：`ScadaEngine.Engine/DBPoint/DB來源範本.xlsx`；測試：`ScadaEngine.Tests/SourceExcel/DbPointExcelAdapterTests.cs` + `SourceExcelRoundTripTests.cs`。

**與 §8.3 熱編輯的關係**（plan 2026-10-12）：

- 熱編輯改 Name/Unit 以 `JsonNode` **原地修改**（`DbPointConfigFileService.ApplyPointEdit`，比照 Modbus），頂層 `SourceWorkbook` 與任何非 DTO 欄位原樣保留 — 之前走 DTO 反序列化再序列化會弄丟 `SourceWorkbook`，改完後「依檔案匯出」就篩不到該 Coordinator、同檔重匯也不會列為刪除候選
- 每次熱編輯在頂層記 `WebEditedAt`；拿修改前匯出的舊 Excel 重匯時，預覽會警告「會蓋掉網頁修改」並要求另外勾選（規則見 Modbus §4.1）。伺服器不保存上傳的 Excel，網頁修改不會回寫使用者電腦裡（下載）的檔案，請先「匯出 Excel」再編輯
- **來源資料夾內的 Excel**：「匯出 Excel」主按鈕直接寫進 `DBPoint` 來源資料夾（下拉保留「下載到瀏覽器」）、匯入下拉可「從來源資料夾匯入」；熱編輯 / 匯入 / 刪除成功後自動重產資料夾內受影響的 xlsx（不新建；Excel 開著或有未匯入修改時不覆寫並提示），頁面提示「有未匯入修改」的檔案。規則同 [Modbus §4.1「來源資料夾內的 Excel」](功能說明書_Modbus來源管理.md#41-excel-匯入匯出刪除取代-xlsm-巨集)
- 測試：`ScadaEngine.Tests/SourceExcel/DbPointHotEditPreserveTests.cs`、`WebEditedWarningTests.cs`

---

## 9. Phase 2（寫入路徑）

寫入路徑統一執行 `UPDATE DBLatestData SET Value=工程值, Timestamp=GETDATE(), Quality=1 WHERE SID=@sid`，並先驗證寫入值在 `DBPoints.Min ≤ Value ≤ Max` 範圍內（超出 → log warning 並回傳 false，不寫）。寫入語意採 last-writer-wins（與 SCADA 控制本來就是的語義一致）。

| 路徑 | 狀態 | 備註 |
|------|------|------|
| 手動控制頁面對所有 DB SID 開放（每個 DB 點都可讀可寫） | 暫不開工 | 由手動控制頁未來擴充 |
| **LogicFlow 節點輸出可寫** | **已實作** | output 節點綁 DB SID 時，`LogicFlowExecutionService.ExecuteControlWriteAsync` 以 SID 前綴 `DB` 分流走 `IDataRepository.UpdateDbLatestDataAsync`；ManualControl 互斥、啟動保護期、重試機制與 Modbus 寫入一致 |
| **MQTT `SCADA/Control/{SID}` 訂閱** | **已實作** | ScadaPage 手動控制送出 MQTT 後，`MqttControlSubscribeService.OnMessageReceivedAsync` 以 SID 前綴 `DB` 分流呼叫 `ExecuteDbControlAsync`，走 `IDataRepository.UpdateDbLatestDataAsync` 寫入 `DBLatestData`，不進入 Modbus 寫入路徑 |

---

## 10. 已知風險與緩解

| 風險 | 緩解 |
|------|------|
| PollingInterval 設過短把連線池打滿 | `DbCommunicationService` 內 clamp `MIN_POLLING_INTERVAL_MS=200` |
| Coordinator 已建但外部尚未接通 → 每秒空 query | `MonitorEnabled=false` 整個 Coordinator 暫停；Phase 1 不做更細的「無資料就退避」 |
| 外部系統與 Engine 同時寫 DBLatestData（Phase 2） | SQL Server row-level lock 處理；last-writer-wins 為控制系統合理語意 |
| 外部系統時鐘不準導致 Web 端 STALE 警示 | 使用者問題；Engine 不修正時間。Realtime / LatestData 的 Timestamp 仍來自 DBLatestData 以反映外部寫入真實時間，HistoryData 則用 polling 當下時間以保證時間軸完整 |
| 外部停止更新 DBLatestData 時 HistoryData trending 看到一條完美水平線 | **這是設計取捨** — HistoryData 寫入脫鉤外部 timestamp 才能在停寫場景仍有資料軌跡。若有「停滯偵測」需求應由外部寫入端把 Quality 改成 Bad，或另開 watchdog 議題 |

---

## 11. 相關檔案

### Engine
- `ScadaEngine.Engine/Models/DbCoordinatorModel.cs`
- `ScadaEngine.Engine/Models/DbPointModel.cs`
- `ScadaEngine.Engine/Services/DbCoordinatorJsonLoader.cs`
- `ScadaEngine.Engine/Services/DbCommunicationService.cs`
- `ScadaEngine.Engine/Services/DbCoordinatorReloadSubscriber.cs`
- `ScadaEngine.Engine/DBPoint/*.json`（含隨 Release 附的空白範本 `DB來源範本.xlsx`；舊 `DB通訊檔案產生工具.xlsm` 已移除）

### Web
- `ScadaEngine.Web/Features/DbCoordinator/Controllers/DbCoordinatorController.cs`
- `ScadaEngine.Web/Features/DbCoordinator/Models/DbCoordinatorViewModel.cs`
- `ScadaEngine.Web/Features/DbCoordinator/Models/UpdatePointRequest.cs` — 點位編輯（名稱+單位）請求 DTO
- `ScadaEngine.Web/Features/DbCoordinator/Models/DbPointJsonFileDtos.cs` — DBPoint JSON 檔結構（Web 端持有，Engine 端為 internal）
- `ScadaEngine.Web/Features/DbCoordinator/Views/Index.cshtml`
- `ScadaEngine.Web/Services/DbCoordinatorService.cs`
- `ScadaEngine.Web/Services/DbPointConfigFileService.cs` — 點位名稱/單位回寫 JSON + UPSERT DBPoints
- `ScadaEngine.Web/Services/SourceExcel/` — Excel 匯入共用框架（`SourceConfigFileIo` 檔案 I/O、`DbPointExcelAdapter`、`SourceExcelDiffBuilder`、`SourceExcelImportCoordinator`、`SourceExcelTemplateWriter`）
- `ScadaEngine.Web/Features/Shared/Models/SourceExcel*.cs`、`Features/Shared/Views/_SourceExcelImport.cshtml`、`wwwroot/js/common/source-excel-import.js`、`wwwroot/css/source-excel-import.css` — 匯入 DTO + 共用工具列／預覽 Modal
- `ScadaEngine.Web/Services/DbCoordinatorReloadPublisher.cs`
- `ScadaEngine.Web/wwwroot/css/dbcoordinator.css`
- `ScadaEngine.Web/wwwroot/js/dbcoordinator.js`
- `ScadaEngine.Web/appsettings.json` — `EngineDbPointConfig`（WatchedFolder + MirrorFolder）

### 共用
- `ScadaEngine.Engine/Data/Interfaces/IDataRepository.cs` — 新增 `GetAllDbCoordinatorsAsync` / `GetAllDbPointsAsync` / `GetDbPointsByCoordinatorIdAsync` / `SaveDbCoordinatorAsync` / `SaveDbPointsAsync` / `GetDbLatestDataByPrefixAsync`
- `ScadaEngine.Engine/Data/Repositories/SqlServerDataRepository.cs` — 對應實作
- `ScadaEngine.Engine/DatabaseSchema/DatabaseSchema.json` — DBCoordinator / DBPoints / DBLatestData 三張表

---

## 12. 時間模擬點位（TimeSim）

DB 來源的實際應用範例：兩個「時間點位」讓警報 / 計算點位 / LogicFlow / 趨勢能取得當日時間進度，
完全走 DB 來源設計本意（外部排程寫 `DBLatestData`），**Engine / Web 程式碼零改動**。

| SID | 名稱 | 值 | 範圍 |
|-----|------|----|------|
| `DB2-S1` | MinuteOfDay | 當日分鐘數 | 0–1439，跨日歸零 |
| `DB2-S2` | HourOfDay | 當日小時數 | 0–23，跨日歸零 |

> SID 中的 `DB2` 為本機 IDENTITY 配發結果，換環境部署會不同 — 一切以 `DBCoordinator.Name='TimeSim'` 定位。

組成：

- **`DBPoint/TimeSim.json`** — 獨立 coordinator（不塞 DB1：DB1 已滿 100 點上限；且獨立檔不會被 Excel 巨集覆蓋）
- **`DBPoint/UpdateTimeSim.sql`** — 每分鐘執行的 UPDATE：`分鐘 = DATEDIFF(MINUTE, 當日0點, GETDATE())`、`小時 = DATEPART(HOUR, GETDATE())`，同時更新 `Timestamp=GETDATE(), Quality=1`；以 `DBCoordinator.Name + DBPoints.Sequence` JOIN 定位 SID，不寫死 `DB{n}`
- **Windows 排程 `ScadaTimeSimUpdate`** — 每 1 分鐘以 SYSTEM 身分跑 `sqlcmd -i UpdateTimeSim.sql`。
  線上 SQL 為 **SQL Server 2019 Express（無 SQL Agent）**，故用 Task Scheduler 等價替代

成本：HistoryData 本來就是每點每分鐘固定 1 筆（分鐘 dedup），「每分鐘變化的點」不比靜止點多寫任何一筆；MQTT 分鐘點 1 次/分、小時點 24 次/天，可忽略。

維運須知：

- 排程停掉時值停滯（Timestamp 不再變化 → 不再推播），Engine 不會報錯 — 同外部系統停寫場景
- 排程管理：`Get-ScheduledTask -TaskName ScadaTimeSimUpdate`（停用 `Disable-ScheduledTask` / 啟用 `Enable-ScheduledTask`）
- 換機部署順序：放 `TimeSim.json` → Engine reload（產生 SID + seed `DBLatestData`）→ 註冊排程
