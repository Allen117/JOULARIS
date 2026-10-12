# 功能說明書：Modbus 來源管理 (ModbusCoordinator)

## 1. 功能概述

`/ModbusCoordinator` 頁面顯示 Engine 端 Modbus 設備登記（左側清單 + 右側詳情雙欄佈局），並提供兩項編輯能力：

右側詳情顯示 Name / **IP / Port** / ModbusID / DelayTime / MonitorEnabled（不顯示 DB 自增 Id）。IP / Port 不在 `ModbusCoordinator` 表內，由 Controller 以 `ModbusConfigFileService.GetEndpointAsync` 逐台讀設備 JSON 頂層 `IP` / `Port`，以 `window._modbusEndpoints`（名稱 → `{ip, port}`）帶給前端；「設定檔已移除」的設備查不到則留空。多站號子項目也顯示同一台的 IP / Port。

1. **子設備名稱編輯**：多站號（ModbusID 逗號分隔）設備可為每個站號取名（寫 `ModbusCoordinator.DeviceName`，主權在 DB）
2. **點位熱編輯**（限 Admin）：選擇設備後，右側詳情卡片標題列出現「點位設定」按鈕，點擊彈出 Modal 視窗，原地編輯設備 JSON 內點位的 Name / Address / DataType / Ratio / Unit / Min / Max / **子設備（Device）**，存檔後 **不需重啟 Engine**，數秒內以新設定採集
3. **站號內子設備分群（Tag.Device）**：一顆 PLC（單一站號）內放多台設備時，可為每個點位標註所屬子設備做分群，讓點位選擇器可展開瀏覽（見 §站號內子設備分群）
4. **Excel 匯入／匯出／刪除**（限 Engineer）：頁首「下載範本／匯出 Excel／匯入 Excel」，上傳 .xlsx（或舊 .xlsm，巨集忽略）→ 預覽差異（新增／覆寫／無變更／錯誤 + 刪除候選）→ 勾選確認 → 寫 JSON，Engine watcher 數秒內生效；每台設備另有「刪除設備」按鈕。**取代舊 `Modbus通訊檔案產生工具.xlsm` 巨集流程**（見 §4.1）

### 點位熱編輯核心設計

- **主權在 JSON**：點位欄位的唯一真相是 Engine 執行目錄的 `Modbus/*.json` — Engine 每次重載與每筆控制指令都會重讀 JSON 並整批覆寫 `ModbusPoints` 表，因此 Web **只寫 JSON 檔、不寫 DB**（DB 會被 Engine 自動同步）
- **結構鎖死**：SID 由陣列索引產生（`-S{index+1}`）、控制指令用 TagIndex 定位 — 禁止新增、刪除、排序點位；`IP / Port / ModbusId / ConnectTimeout / 檔名` 唯讀。後端存檔前重讀原檔驗證點位數量未變，不合即回 400。DataType 可改但限 Engine 支援白名單（`INTEGER / UINTEGER / FLOATINGPT / SWAPPEDFP / DOUBLE / SWAPPEDDOUBLE / UINT32BE / DEC10K3 / BCD / BIT0`–`BIT15`，UI 為下拉選單）。白名單唯一真相來源為 Engine 的 `ModbusTagModel.SupportedDataTypes`，Web 的 `ModbusConfigFileService.SupportedDataTypes` 直接引用它，前端 `modbuscoordinator.js` 的 `DATA_TYPES` 手動對齊（BIT 以迴圈產生）。各型別語意見 [功能說明書_Engine核心.md](功能說明書_Engine核心.md) §4.3

#### DataType 欄的兩段式下拉

`BIT0`–`BIT15` 攤平會讓下拉長達 26 項，故 UI 拆成兩段（`makeDataTypeCell`）：

- **型別下拉**只放 10 項，BIT 收斂為單一的群組代表值 `BIT`（**僅存在於 UI，本身不是合法 DataType**）
- 選到 `BIT` 才顯示右側 0–15 的**位元索引下拉**，其餘型別隱藏
- 兩者由 `syncDataType()` 合成後寫進同格的隱藏欄位 `[data-field="dataType"]`

**儲存格式不變**，JSON 寫入的仍是 `BIT5` 這種單一字串，後端與 Engine 零改動；`collectAndValidatePoints` / `countChanges` 照舊讀該隱藏欄位。

`parseBitIndex()` 刻意只認大寫精確格式 `^BIT(\d{1,2})$`（與後端白名單同規則）— `Bit5` 這類舊寫法會落到「保留原值為第一個選項」路徑，才不會在使用者未操作時被靜默正規化成 `BIT5` 而多算一筆變更。
- **原子寫檔**：先寫 `*.json.tmp`（不觸發 Engine watcher）→ `File.Replace` 原子替換 → 保留 `*.json.bak` 備份。控制路徑每筆指令都直接讀 JSON 且失敗不重試，原子替換保證任何瞬間讀到完整舊檔或完整新檔
- **保留原檔編碼**：現場檔案為 UTF-16 LE with BOM（Excel 工具產生），寫回時偵測 BOM 沿用原編碼
- **Engine 端配合**：
  - watcher 補訂 `Renamed` 事件 — `File.Replace` 在 Windows 以 rename 落地，原本只訂 Changed/Created 收不到
  - per-file 去抖 1 秒 — 一次存檔常觸發多個事件，去抖後設備只斷線重連一次
  - **副檔名守衛**（`IsJsonConfigFile`）— watcher 的 `*.json` filter 對 rename 是「舊名或新名任一符合就觸發」，`File.Replace` 把 `X.json → X.json.bak`（舊名符合）會以 `e.FullPath=X.json.bak` 觸發 Renamed；若不擋，`GetFileNameWithoutExtension("X.json.bak")="X.json"` 會被當 Coordinator 名寫進 DB 生幽靈。三個 handler 與 `ReloadDeviceConfigAsync` 皆先檢查副檔名為 `.json` 才放行

## 站號內子設備分群（Tag.Device）

**痛點**：一顆 PLC（單一站號）放 5 種設備、每設備 5 個點時，25 個點在點位選擇器同一層平鋪、找設備要滾很久。Modbus 原本缺「站號內子設備」這一分群層（計算點位有 `GroupName`、DB 來源有 Coordinator 群組、OPC UA 有 Devices 分組，只有 Modbus 沒有）。

### 資料鏈與主權

- **主權在 `Modbus.json` 的 `Tag.Device`**（optional 字串欄）。Engine 載入 JSON → 寫進 `ModbusPoints.DeviceGroup`（每次重載重寫的投影，與其他欄位同機制；DB 只是投影，改 JSON 才是改分群）
- 未填 `Device` 的既有設定檔**行為完全不變**（留白 = 未分群，可一台一台慢慢補）
- 熱編輯 Modal 的「子設備」欄寫回 JSON 的 `Tag.Device` → Engine watcher 重載 → `DeviceGroup` 更新，**免重啟**

### 決策 4：站號 / Device 互斥

現場的多站號 Coordinator，一個站號本來就是一台設備，不會再有站號內分設備需求。因此兩種分群來源**互斥**，由站號數決定走哪條、永不疊加：

| Coordinator 情況 | 分群依據 | Device 欄 |
|---|---|---|
| 多站號（ModbusId 逗號分隔） | 站號 → `DeviceName`（現況邏輯不變） | **忽略**：Modal 內 disable + 提示；Engine `ResolveDeviceGroup` 一律寫 null |
| 單站號 + 有 Device | `Tag.Device` | 可編輯 |
| 單站號 + 無 Device | Coordinator 名（直接可點） | 可編輯（留白） |

互斥規則的單一真相 = `ModbusPointModel.ResolveDeviceGroup(device, isMultiStation)`（Engine 載入 JSON 與 Web 熱編輯共用）。

### 四級 fallback 鏈（點位設備標籤）

點位在選擇器顯示的設備標籤走：`Tag.Device` → 多站號的站號子設備名 → Coordinator 名 →（設備清單的「未分群」桶）。

### 前端共用層 `point-grouping.js`

「SID → 站號內子設備」的解析（`split(',')` + `CoordinatorId*65536 + ModbusId*256` 落點）原本被複製 5+ 份、fallback 各自走偏。已收斂為 `wwwroot/js/common/point-grouping.js`（`window.PointGrouping`），對外提供 `parseCoord` / `subOfSid` / `pointDeviceLabel`（四級鏈）/ `coordDeviceGroups`（單站號盤點 Device、多站號回 null）等。designer / logicflow / calcpoint 點位選擇器據此讓「單站號有 Device」的 Coordinator 可展開子選單 + 「未分群」桶；eventlog / energy-baseline / history 的設備標籤亦走同一支。

> ℹ️ 舊 `Modbus通訊檔案產生工具.xlsm` 巨集不輸出 `Device` 欄，已於 2026-10 移除；改由 Web「匯入 Excel」（§4.1）產生 JSON，範本 H 欄即 Device。用**舊版面 xlsm 直接上傳**時 Device 會是空白，預覽差異會顯示 `Device: X → ` — 建議「先匯出再改」。

## 2. 路由

| 方法 | 路由 | 說明 | 認證 |
|------|------|------|------|
| GET  | `/ModbusCoordinator` | 設備清單頁 | 需登入 |
| POST | `/ModbusCoordinator/UpdateDeviceName` | 更新子設備名稱（寫 DB） | 需登入 |
| GET  | `/ModbusCoordinator/Points/{name}` | 讀取設備 JSON 點位清單 | **Admin** |
| POST | `/ModbusCoordinator/UpdatePoints` | 原地更新點位欄位（寫 JSON） | **Admin** |
| GET  | `/ModbusCoordinator/ImportTemplate` | 下載空白 Excel 範本（`Modbus範本.xlsx`） | Engineer |
| GET  | `/ModbusCoordinator/ExportExcel?workbook=` | 匯出現行設定為 .xlsx（一個 JSON 一張工作表；`workbook` 省略 = 全部，指定 = 只匯出該來源 Excel 檔的設備） | Engineer |
| POST | `/ModbusCoordinator/ImportPreview` | 上傳 .xlsx/.xlsm（multipart `file`）→ 解析、驗證、比對既有 JSON → 回傳預覽 + `token`（**不寫檔**） | Engineer |
| POST | `/ModbusCoordinator/ImportCommit` | `{ token, importSheets[], deleteNames[], acknowledgeSidShift }` → 提交前重新比對 → 寫檔／刪檔；預覽後設定被更動回 **409** | Engineer |
| POST | `/ModbusCoordinator/DeleteSource` | `{ name }` 逐台刪除（JSON 移到 `_deleted/`） | Engineer |
| GET  | `/ModbusCoordinator/SourceStatus` | `{ loaded[], existing[] }` — DB 已載入的 Coordinator 名稱 + 資料夾 JSON 名稱，前端匯入後輪詢用 | Engineer |

`{name}` = Coordinator 名稱 = JSON 檔名（不含副檔名）。非 Admin 直接呼叫回 403，頁面上不渲染編輯卡片。

## 3. 設定（Web appsettings.json）

```json
"EngineModbusConfig": {
  "WatchedFolder": "../ScadaEngine.Engine/bin/Debug/net8.0/Modbus",
  "MirrorFolder": "../ScadaEngine.Engine/Modbus"
}
```

| 鍵 | 說明 |
|----|------|
| `WatchedFolder` | Engine watcher 監控的資料夾 = Engine **執行目錄**的 `Modbus\`。dev 為 `bin/Debug/net8.0/Modbus`；**publish 部署時填 Engine exe 同層 `Modbus\` 的絕對路徑** |
| `MirrorFolder` | 可選。dev 時鏡像寫回原始碼資料夾，避免 rebuild 後設定倒退；**正式環境留空** |

## 4. 存檔流程

```
Web UI 存檔（Admin）
  → 後端重讀原檔驗證（數量一致、DataType 未變、Address/Ratio/Min/Max 格式）
  → 只改 Tags[i] 可編輯欄位 → 寫 *.json.tmp → File.Replace 原子替換（留 .bak）
  → Engine watcher（Renamed）→ 去抖 1 秒 → 停舊採集 → 起新採集 → ModbusPoints 刪+插
  → 每個有變更的點位寫一筆 EventLog 稽核（EventType=3，key control.action.point_config_changed，
     args {username, name, value=變更摘要如 "Address: 30513 → 30514"}，zh-TW/en 自動翻譯）
```

驗證規則（前後端一致）：

- Name 必填；Address 支援兩種慣例（與 Engine `ParseAddress` 一致，靠**字串長度含前導 0** 區分）：
  - 5 位數：`1–9999`（Coil）、`1xxxx`（Discrete）、`3xxxx`（Input）、`4xxxx`（Holding）
  - 6 位數擴充（offset 可達 65535）：`000001–065536`（Coil）、`1xxxxx`（Discrete）、`3xxxxx`（Input）、`4xxxxx`（Holding），如 `430001` = Holding offset 30000
  - ⚠️ 兩慣例數值重疊但意義不同：`045000` = Coil offset 44999，`45000` = Holding offset 4999 — 前導 0 有語意，不可省略
- DataType 需在 Engine 支援白名單內（大小寫不拘，Engine 端 ToUpper 比對）；變更會影響暫存器讀取長度與數值轉換，確認框有註明
- Ratio 必須為數字；Min / Max 為數字或留空
- 無任何欄位變更時不寫檔（不觸發重連）

## 4.1 Excel 匯入／匯出／刪除（取代 xlsm 巨集）

**第一性原理**：舊巨集只做「儲存格 → 欄位」直接對應，沒有任何邏輯需要 Excel 本身；真正要消除的是「啟用巨集、每張表按一次、手動搬檔」這些人工步驟。JSON 保留為 Engine 內部存檔格式（熱編輯、watcher、升級備份都建立在它上面），使用者不再接觸。

### 流程

```
Install.bat → 填 Modbus範本.xlsx（任何能編輯 xlsx 的軟體；或頁面「匯出 Excel」拿現行設定來改）
  → Engineer 登入 /ModbusCoordinator → 「匯入 Excel」上傳
  → POST ImportPreview：ClosedXML 解析每張工作表 → 驗證 → 與既有 JSON 比對 → 預覽（不寫檔；結果以 token 暫存 IMemoryCache 10 分鐘、綁定使用者）
  → 使用者勾選 → 有覆寫／刪除時確認對話框列出名稱 → POST ImportCommit
  → 提交前重新比對既有檔 SHA-256（有人同時熱編輯 → 409 要求重新預覽）
  → System.Text.Json 寫 JSON（原子寫檔 tmp → File.Replace；新檔 UTF-16 LE BOM、既有檔沿用原編碼；頂層多寫 SourceWorkbook=上傳檔名）
  → Engine watcher Created/Renamed → 去抖 1 秒 → 重載；刪除 → Deleted → 依「檔名 → IP:Port」對照停止採集
  → 前端輪詢 GET SourceStatus（DB 已載入名稱 + 資料夾 JSON 名稱；1 秒一次、最多 20 秒）等匯入的名稱都被 Engine 寫進 DB 才 reload
     （頁面清單讀 DB；太早 reload 會把新設備判成「待 Engine 載入」）；頁面上有「待 Engine 載入」項目時也背景輪詢 60 秒自動刷新
```

### 範本版面（沿用舊 xlsm，舊檔可直接上傳）

| 位置 | 內容 |
|------|------|
| 第 1 列 | `A1=IP` `B1=值`、`C1=Port` `D1=502`、`E1=ModbusID` `F1=1,2,3`、`G1=ConnectTimeout` `H1=1000`（以**標籤文字**掃描第 1 列取右側值，找不到才退回固定位置；Port 空白 → 502、ConnectTimeout 空白 → 1000） |
| 第 2 列 | 欄位標題 |
| 第 3 列起 | `A~G = Name / Address / DataType / Ratio / Unit / Min / Max`，**`H = Device`（可留白）**；`I` 欄 DataType 清單（C 欄下拉驗證來源） |
| 工作表名稱 | = 設備名稱 = JSON 檔名；一個 Excel 可放多張工作表（現場習慣**一盤／一 Gateway 一份 Excel、一盤多設備**） |

- Address 欄為文字格式，保留 6 位數擴充慣例的前導 0（`000001`）；數值儲存格 `40001` 讀成 `"40001"`
- 名稱空白的列整列略過並在預覽提示「第 N 列已略過」；最後一筆有名稱的列之後不讀
- 驗證（與 Engine 載入一致，錯誤指出「工作表 + 列 + 欄 + 原因」，該張不可匯入、其他合法工作表仍可）：IP 必填、Port 1~65535、ModbusID 0~255 逗號分隔、Address 格式、DataType 白名單、**BIT 型別只能配 3xxxx/4xxxx**（否則 Engine 載入會跳過該點 → 後續 SID 位移）、Ratio/Min/Max 數字
- JSON 字串經 System.Text.Json 跳脫（修正巨集名稱含 `"` / `\` 會壞檔的問題）

### 預覽分類與確認規則

| 分類 | 條件 | 預設勾選 |
|------|------|---------|
| 新增 | 既有 JSON 不存在 | ✅ |
| 覆寫 | 既有 JSON 存在且有差異（列出設備層欄位與逐點「欄位: 舊 → 新」） | ✅（提交前確認框列出名稱） |
| 無變更 | 內容相同（匯出後不改直接匯入即此狀態） | 不可勾 |
| 錯誤 | 格式錯誤 | 不可勾 |
| 刪除候選 | 既有 JSON 的 `SourceWorkbook` 與這次上傳檔名相同（不分大小寫、忽略副檔名）、但這次沒有的工作表 | ✅（確認框列出） |
| 其他未包含 | 其他盤或舊流程手動放入（`SourceWorkbook` 不同或缺） | ☐（預設收合） |

- **整份都不可勾選時**（工作表全是錯誤／無變更、也沒有刪除候選）：預覽頂部顯示黃底說明、底部狀態列紅字「沒有可匯入的項目：N 張工作表有錯誤…」而非「尚未勾選任何項目」。常見情境是直接上傳**空白範本**（IP 未填、無點位）—— 修正 Excel 後重新上傳即可，勾選框並非故障

- **SID 位移**（高風險、不擋）：逐點比對 Name，只在尾端增減 → 一般覆寫；中間插入／刪除／換順序 → 標示「第 N 點起 SID 位移」，需另外勾選「我了解歷史資料與控制對應會錯位」才能提交。同位置改名且新舊名稱都不在另一側 → 視為原地改名，不算位移
- **工作表改名**：新增的工作表點位名稱與某刪除候選完全相同 → 提示「可能是改名：會建立新 CoordinatorId，歷史不會接上」
- Excel 改檔名（`盤A_v2.xlsx`）時舊設備落到「其他」區不會自動勾選 → 預覽每筆都顯示來源檔名

### 刪除語意

- 刪除 = `{name}.json` 移到 `Modbus/_deleted/{name}_{yyyyMMddHHmmss}.json`（子資料夾不在 watcher 與載入範圍；人工搬回即復原）
- Engine 收到 `Deleted` → 依 ModbusCollectionManager 的「檔名 → IP:Port」對照 `StopDeviceCollectionAsync`；兩個設定檔共用同一 IP:Port 時另一個還在就不停
- `ModbusCoordinator` / `ModbusPoints` 與歷史資料**都不刪**：同名重新匯入時 CoordinatorId 不變、SID 一致、歷史接得上；頁面對「DB 有 Coordinator 但 JSON 不存在」者標示「設定檔已移除」並隱藏點位設定
- 反向狀態「**待 Engine 載入**」：JSON 已匯入但 DB 還沒有紀錄（Engine 未啟動、設定驗證失敗）→ 側欄列出該檔名並提示去看 Engine；頁面清單本身是讀 DB，Coordinator 要等 Engine 載入 JSON 後才會出現。**最常見原因是 Web 與 Engine 的 `Setting\dbSetting.json` 指向不同資料庫**（兩邊各有一份、升級時都保留，改其中一邊容易忘另一邊；2026-10-06 現場即如此：Engine `wsn_TG`、Web `wsnCsharp`）→ 頁面提示已列為檢查點 (2)
- Engine 空載（`Modbus\` 沒有任何 JSON，新裝機必然如此）**仍會啟動 watcher**（2026-10-06 修正：原本無設備就 return、watcher 沒掛 → 匯入後要等 Engine 重啟才採集）；資料夾不存在會先建立
- 同資料夾 `X.json → Y.json` 改名也視為 X 移除 + Y 新增；`File.Replace` 的 `X.json → X.json.bak` 中間步驟（同資料夾、新名非 .json）不算移除

### 相關元件

| 元件 | 用途 |
|------|------|
| `Services/SourceExcel/SourceConfigFileIo.cs` | 共用檔案 I/O：路徑防護、BOM 編碼偵測、原子寫檔、鏡像、`_deleted/`（熱編輯與匯入共用） |
| `Services/SourceExcel/ISourceExcelAdapter.cs` + `ModbusExcelAdapter.cs` / `DbPointExcelAdapter.cs` | 各來源版面解析、驗證、JSON 序列化、範本版面（純函數，Singleton） |
| `Services/SourceExcel/SourceExcelDiffBuilder.cs` | 新增／覆寫／無變更、SID 位移、刪除候選分區 |
| `Services/SourceExcel/SourceExcelImportCoordinator.cs` | 預覽 → token → 提交前重比對 → 寫檔／刪檔；範本與匯出（Scoped） |
| `Services/SourceExcel/SourceExcelTemplateWriter.cs` | ClosedXML 組 workbook |
| `Features/Shared/Views/_SourceExcelImport.cshtml` + `wwwroot/js/common/source-excel-import.js` + `css/source-excel-import.css` | 工具列 + 預覽／確認 Modal（兩頁共用，`data-srcxl-base` 參數化） |
| `ScadaEngine.Engine/Modbus/Modbus範本.xlsx` | 隨 Release 附的空白範本（由 TemplateWriter 產生） |
| `ScadaEngine.Tests/SourceExcel/*` | 解析／驗證／差異／往返單元測試 |

## 5. 已知限制與運維注意

- **改 Address 後 SID 不變**：歷史資料無縫接續 — 同一 SID 前後量測不同實體暫存器屬運維語意責任（存檔確認框已註明）
- **點位改名傳播範圍是「部分」**：只存 SID、顯示時現查的功能會自動更新（警報規則、ConditionCtrl、History/Trend、Realtime、EnergyMeter 副標）；抄存名稱副本的不會（ScadaPage/Designer 元件標籤、LogicFlow 節點 `pointName`、EventLog 既有事件） — 需至該頁重新綁定
- **重載空窗 1~3 秒**：該設備畫面值短暫凍結（非 Bad Quality）
- **存檔瞬間在途的控制指令**會用舊位址成功寫入（一筆）；位址變更屬排程性操作，接受此窗口
- **手動改檔仍有效**：直接編輯 JSON（Excel 工具流程）同樣觸發熱重載，去抖同樣生效
- Engine csproj 已改 `Modbus\*.json` 萬用字元 — **部署新版後所有 `Modbus\` 下的 JSON（含 Modbus2.json）都會生效**，Engine 會開始採集這些設備（DB 建新 Coordinator、嘗試連線），部署時需知會運維

## 6. 相關元件

| 元件 | 用途 |
|------|------|
| `ScadaEngine.Web/Services/ModbusConfigFileService.cs` | 讀寫 Modbus JSON（驗證、原子替換、鏡像）— Singleton |
| `Features/ModbusCoordinator/Models/ModbusPointEditDtos.cs` | 點位 DTO + 更新請求/結果 |
| `ScadaEngine.Web/Services/ControlEventLogger.cs` | `LogPointConfigChangedAsync` 稽核寫入 |
| `wwwroot/js/modbuscoordinator.js` | 點位表格載入 / 驗證 / 存檔（IIFE） |
| `ScadaEngine.Engine/.../ModbusCollectionManager.cs` | watcher 去抖重載；「檔名 → IP:Port」對照，設定檔移除時停止採集 |
| `ScadaEngine.Engine/.../ModbusConfigService.cs` | watcher（Changed / Created / Renamed / **Deleted**） |
| `Services/SourceExcel/*`、`Features/Shared/Views/_SourceExcelImport.cshtml`、`js/common/source-excel-import.js` | Excel 匯入／匯出／刪除（見 §4.1） |
