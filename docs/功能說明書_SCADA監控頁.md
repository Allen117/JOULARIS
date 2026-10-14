# 功能說明書：SCADA 監控頁 (ScadaPage)

## 1. 功能概述

SCADA 監控頁是本系統的**核心即時視覺化操控介面**，讓使用者透過圖形化畫面即時監看設備狀態並下達控制指令。頁面內容由「畫面設計 (Designer)」頁面建立並發布，ScadaPage 負責載入已發布的設計並綁定即時數據。

### 核心能力
- **載入已發布設計**：從 Designer 取得頁面定義（包含畫布尺寸、背景圖、Widget 佈局）
- **即時數據綁定**：每秒向 `/api/realtime/latest` 輪詢，驅動 Widget 顯示即時值
- **控制指令發送**：透過 `/api/control/write` API 下達 MQTT 控制指令寫入設備
- **頁面樹導覽**：多頁面、多層級的樹狀結構切換
- **權限控制**：按頁面粒度控制「可檢視 / 可操控」權限
- **即時曲線**：畫面上可擺放會自己往左滾動的「近 N 分鐘」趨勢圖（`trendChart` widget，§7.4 (13)）—— 掛載時 backfill 補滿、之後複用同一份每秒即時值 append，穩態零 DB 查詢

> **新增同型設備的畫面**：Designer 頁面樹支援**整頁複製貼上**（右鍵「複製此頁」/「貼上為子頁面」），複製出的頁面版面全同、**點位綁定全清空**，只要重綁點位即可。與畫布上 `Ctrl+C`/`Ctrl+V`（元件層級、**保留**綁定）是兩回事 → [功能說明書_圖面與邏輯複製.md](功能說明書_圖面與邏輯複製.md)
- **警報著色**：結合警報規則，超限時自動變色提示

---

## 2. 路由

| 方法 | 路由 | 說明 | 認證 |
|------|------|------|------|
| GET | `/ScadaPage` | 監控頁面 | 需登入 |
| POST | `/api/control/write` | 控制指令寫入 | 需登入 |
| GET | `/api/control/manual-values` | 取得所有手動控制值 | 需登入 |
| POST | `/api/scadapage/accumulation` | 累積量元件批次查詢（當日/當月累積，30 秒輪詢） | 需登入 |
| POST | `/api/scadapage/circuit-metric` | 迴路指標元件批次查詢（五指標，30 秒輪詢，上限 50 筆/批） | 需登入 |

### 依賴的外部 API
| 方法 | 路由 | 用途 |
|------|------|------|
| GET | `/Designer/Load` | 載入已發布的設計頁面定義 |
| GET | `/api/realtime/latest` | 取得即時數據（每秒輪詢） |
| GET | `/api/alarm-rules` | 取得警報規則（初始化時載入一次） |

---

## 3. 檔案結構

```
Features/ScadaPage/
├── Controllers/
│   ├── ScadaPageController.cs    ← GET /ScadaPage 頁面入口
│   └── ControlController.cs      ← POST /api/control/write, GET /api/control/manual-values
├── Models/
│   └── ControlWriteDto.cs        ← 控制指令 DTO
└── Views/
    └── Index.cshtml              ← 頁面結構（頁面樹 + 畫布區）

Views/Shared/
└── _Layout.cshtml                ← 共用版面配置

wwwroot/
├── js/common/trend-window.js     ← 即時曲線時間窗／點數計算（Designer 與 ScadaPage 共用單一真相）
├── lib/chartjs/                  ← Chart.js 4.4.0 + date-fns adapter（即時曲線與歷史趨勢頁共用）
├── css/scadapage.css             ← 元件互動樣式（hover 標籤、警報脈動、泵旋轉、按鈕 3D 效果）
└── js/scadapage/                 ← 核心渲染引擎模組群（2026-08 由單體 scadapage.js 拆分）
    ├── state.js                  ← 共享狀態 + 跨模組 helper（最先載入）
    ├── widget-points.js          ← 控制鈕/即時值/DI/AO/DO/半圓儀表 builder
    ├── widget-pump.js            ← 水泵 SVG + Linear Gauge 拖拽
    ├── widget-pipe.js            ← 管路流動元件（圖形共用 common/pipe-svg.js）
    ├── widget-motor.js           ← 馬達型設備（冷卻塔/風扇/冰機）
    ├── widget-table.js           ← Table Widget
    ├── widget-trend.js           ← 即時曲線（滾動趨勢圖）Chart.js 生命週期 / backfill / append
    ├── ctx-menus-points.js       ← 點位右鍵選單（警報設定/歷史趨勢/快速趨勢/趨勢清單）+ AO/DO 右鍵與控制寫入
    ├── alarm-quick-edit.js       ← 右鍵「警報設定…」就地彈窗（表單共用 common/alarm-rule-form.js）
    ├── quick-trend.js            ← 右鍵「快速趨勢」浮動小窗（backfill + 每秒即時接續）
    ├── ctx-menus-equip.js        ← 泵浦/馬達右鍵選單與控制寫入
    ├── circuit-metric.js         ← 30 秒慢輪詢（SID 累積量 + 迴路指標）
    ├── tree.js                   ← 頁面樹/切頁/畫布縮放/側欄收合
    ├── render.js                 ← 渲染分派器 + 1 秒輪詢更新
    └── index.js                  ← 初始化進入點（最後載入）
```

---

## 4. 資料流架構

```
使用者開啟 /ScadaPage
    │
    ├─ Controller 注入權限資料 → window._isAdmin, window._scadaPagePerms
    │
    ├─ JS 初始化 ───────────────────────────────────────────────────
    │   ├─ fetch('/Designer/Load')          → 取得已發布設計（頁面樹 + Widget 定義）
    │   ├─ fetch('/api/alarm-rules')        → 載入警報規則（快取至 _alarmRuleMap）
    │   ├─ fetch('/api/control/manual-values') → 載入手動控制值快取（_aoManualValueMap）
    │   └─ fetchAndUpdateGauges()            → 首次取得即時數據
    │
    ├─ setInterval(fetchAndUpdateGauges, 1000)  ← 每秒輪詢即時數據
    │   └─ fetch('/api/realtime/latest')
    │       └─ updateScadaWidgets(data)     → 更新所有 Widget 顯示
    │
    └─ 使用者控制操作 ─────────────────────────────────────────────
        ├─ controlBtn 點擊        → POST /api/control/write { cid, value }
        ├─ AO 右鍵「手動控制」     → POST /api/control/write { cid, value, mode:"manual" }
        ├─ AO 右鍵「自動控制」     → POST /api/control/write { cid, mode:"auto" }
        ├─ DO 右鍵「ON/OFF」       → POST /api/control/write { cid, value }
        ├─ DO 右鍵「自動控制」     → POST /api/control/write { cid, mode:"auto" }
        ├─ Pump 啟動/停止          → POST /api/control/write { cid, value:1/0 }
        ├─ Pump 啟停切自動         → POST /api/control/write { cidStartStop, mode:"auto" }
        ├─ Pump 頻率設定           → POST /api/control/write { cidFreqSet, value:Hz值 }
        ├─ Pump 頻率切自動         → POST /api/control/write { cidFreqSet, mode:"auto" }
        ├─ Pump Gauge 拖拽         → mouseup 時 POST /api/control/write { cid, value:Hz值 }
        └─ 非控制點右鍵選單       → 警報設定… / 歷史趨勢（開新頁）/ 快速趨勢 / 加入趨勢圖清單（§8 點位右鍵選單）
```

---

## 5. 控制器說明

### 5.1 ScadaPageController

簡單的頁面入口控制器，主要負責將權限資料注入前端。

| Action | 說明 |
|--------|------|
| `Index()` | 取得使用者角色 (`IsAdmin`)、頁面層級權限 (`scadaPages`)，透過 `ViewData` 注入前端 |

**權限注入機制**：
- `window._isAdmin`：布林值，Admin 擁有所有頁面的完整權限
- `window._scadaPagePerms`：JSON 物件 `{ pageId: { canView, canControl } }`

### 5.2 ControlController

API 控制器，處理所有控制指令的接收與派發。

#### POST `/api/control/write`

接收 `ControlWriteDto`，根據 `mode` 欄位分三種模式處理：

| 模式 | mode 值 | MQTT 發送 | DB 儲存 | 用途 |
|------|---------|-----------|---------|------|
| **手動** | `""` (預設) | 發送至 Engine | 儲存手動控制值 | 使用者手動操作 |
| **自動** | `"auto"` | 不發送 | 標記為自動控制 | 切換回自動控制邏輯 |
| **LogicFlow** | `"logicflow"` | 發送至 Engine | 不儲存 | 流程圖自動控制 |

**MQTT 控制指令流程**：
```
Web POST /api/control/write
    → MqttRealtimeSubscriberService.PublishControlCommandAsync(cid, value)
    → MQTT Broker (Topic: SCADA/Control/{CID})
    → Engine 接收並執行 Modbus 寫入
```

#### GET `/api/control/manual-values`

回傳所有已儲存的手動控制值，格式：
```json
{
  "196865-S10": { "value": 50.0, "isAuto": false },
  "196865-S20": { "value": 0,    "isAuto": true  }
}
```

---

## 6. 資料模型

### ControlWriteDto

| 屬性 | 型別 | 說明 |
|------|------|------|
| `cid` | string | 控制點位 ID (CID)，即目標 SID |
| `value` | double | 寫入數值（預設 1） |
| `mode` | string | 控制模式：`""` / `"manual"` / `"auto"` / `"logicflow"` |
| `szCid` | string | cid 別名（Log 用） |
| `nValue` | double | value 別名（Log 用） |

---

## 7. 前端渲染引擎 (js/scadapage/ 模組群)

原單體 `scadapage.js`（IIFE，2,700 行）於 2026-08 純搬移拆分為 `js/scadapage/` 12 個模組：**去 IIFE、global scope、state.js 集中共享狀態**，與 `js/designer/` 拆分同模式；載入順序僅約束 state.js 最先、index.js 最後。不對外暴露 `window._xx`（所有互動透過 DOM 事件處理）。

### 7.1 初始化流程

```
DOMContentLoaded
    → _initScadaLayout()      ← 還原側欄收合狀態、算畫布工作區高度、掛 ResizeObserver
    → initScadaViewer()       ← 載入設計、建構頁面樹、渲染首頁
    → _loadAlarmRules()       ← 載入警報規則快取
    → _loadManualControlValues() ← 載入手動控制值快取
    → fetchAndUpdateGauges()  ← 首次取得即時數據
    → setInterval(1000ms)     ← 每秒輪詢更新
```

### 7.2 設計載入與頁面樹

1. 呼叫 `GET /Designer/Load` 取得已發布設計
2. 建構 `nodeMap`（以 `szPageSid` 為 key）
3. 透過 `szParentPageSid` 建構父子關係，形成多層樹結構
4. 依 `nSortOrder` 排序
5. **權限篩選**：非 Admin 使用者只能看到 `canView=true` 的頁面
6. 渲染左側頁面樹，自動選取第一頁

### 頁面樹摺疊（Collapse / Expand）

- 有子節點的父頁面在名稱左側顯示 caret 圖示（純視覺指示，不可點擊）：
  - **展開時**：`fa-caret-down`（灰色）
  - **摺疊時**：`fa-caret-right`（藍色加粗），同時 hover 提示「雙擊展開子項 (N)」，作為「下面還有子畫面」的視覺暗示
- **互動方式**：
  - **單擊**父節點 → 選取該頁面（與葉節點一致）
  - **雙擊**父節點 → 切換摺疊／展開狀態
  - 葉節點不接受雙擊
- 葉節點以等寬空白佔位保持與父節點對齊
- `user-select:none` 防止雙擊選字
- 摺疊狀態以 localStorage key `scadaPage_collapsed_v1` 持久化（陣列形式存所有摺疊節點 ID），跨重新整理保留

### 側欄整體收合（Sidebar Toggle）

上述「頁面樹摺疊」是**節點層級**；側欄本身另有**整體收合**，用於把畫布放到最大：

- 側欄右緣**垂直置中**有一顆 22×44 px 小按鈕（`#scadaTreeToggle`），單擊即收合／展開
- 收合時側欄寬度 160px → 0（.2s 動畫），面板淡出，按鈕內 chevron 旋轉 180°；按鈕本身跨在邊界上（`right:-11px`），收合後仍露在畫布左緣可再展開
- 狀態以 localStorage key `scadaPage_sideCollapsed_v1`（`'1'` / `'0'`）持久化；還原初始狀態時暫加 `.notrans` 關閉動畫，避免進頁面看到側欄滑動
- 收合改變畫布工作區寬度，以 `ResizeObserver` 監看 `.scada-canvas-viewport` 逐影格重算 `_applyCanvasScale()`，畫布跟著平順放大／縮小
- i18n key：`scadapage.tree.collapse` / `scadapage.tree.expand`（按鈕 title，隨狀態切換）

### 頁面節點屬性

| 屬性 | 說明 |
|------|------|
| `szPageSid` | 頁面唯一識別碼 |
| `szPageName` | 頁面顯示名稱 |
| `szPageIcon` | Font Awesome 圖示 class |
| `szParentPageSid` | 父頁面 ID（null 表示根節點） |
| `nSortOrder` | 排序順序 |
| `nCanvasW` / `nCanvasH` | 畫布寬高（預設 1200×800） |
| `szBgDataUrl` | 背景圖 Data URL |
| `szWidgetStateJson` | Widget 狀態 JSON 陣列 |

### 7.3 畫布渲染

- 設定畫布寬高與背景圖
- 遍歷 `arrWidgetState` 逐一渲染 Widget
- 套用等比縮放（`_applyCanvasScale`）讓畫布自適應容器大小
- 監聽 `window.resize` 事件自動重算縮放；另以 `ResizeObserver` 監看畫布工作區（側欄收合／展開改變寬度）與未恢復警報面板（收合／展開改變高度）

#### 畫布工作區高度（警報面板貼底）

- `.scada-canvas-viewport` 的 CSS `height: calc(100vh - 240px)` 只是 JS 執行前的估計值
- 實際高度由 `_applyCanvasViewportHeight()` 反推：`視窗高 − 工作區 top − (警報面板高 + 16px margin) − footer 高 − 8px 留白`
- 效果：未恢復警報面板收合時空出的高度由畫布接手，面板永遠貼在螢幕底部，中間不留空白帶；面板展開時畫布自動縮回
- 差異 < 1px 不重設高度，避免與 `ResizeObserver` 互相反覆觸發
- `min-height` 由 480px 降為 320px — 警報面板全滿（body 上限 280px）時仍要讓面板貼得到底

### 7.4 Widget 類型

系統支援 **8 種 Widget**：

#### (1) gauge — 半圓儀表板

| 屬性 | 說明 |
|------|------|
| `szSid` | 綁定的點位 SID |
| `fMin` / `fMax` | 量程範圍 |
| `szUnit` | 單位 |
| `szColor` | 主色 (預設 `#00c0ff`) |
| `szHighColor` / `szLowColor` | 高低警報色 |
| `szBgColor` | 背景色 |
| `szTitle` | Hover 顯示標題 |

- SVG 半圓弧形，200×145 viewBox
- 即時更新弧形角度與數值
- 品質 BAD 時顯示「斷線」
- 結合警報規則，超限時弧形與數值變色
- 右鍵可加入趨勢圖清單

#### (2) realtimeValue — 即時數值顯示

| 屬性 | 說明 |
|------|------|
| `szSid` | 綁定的點位 SID |
| `nFontSize` | 字體大小（預設 28） |
| `szFontColor` | 字體色 |
| `szUnit` | 單位 |
| `szHighColor` / `szLowColor` | 警報變色 |
| `szValueMode` | 顯示模式：不帶（即時值，預設）/ `day`（當日累積）/ `month`（當月累積） |
| `szAccKind` | 累積計算方式：`meter`（累積讀值差值）/ `integrate`（瞬時值時間積分）— 僅累積模式 |
| `dMaxValue` | meter 溢位上限（如電錶最大讀值），未設時倒退視為歸零 — 僅 meter |
| `szAccUnit` | 累積顯示單位（如 kW 積分後填 kWh），空則沿用 `szUnit` — 僅累積模式 |
| `nAccDecimals` | 累積值小數位數（0–4，預設 1；迴路指標模式沿用此鍵）— 僅累積/迴路指標模式 |
| `nCircuitId` / `szCircuitName` / `szMetric` | 迴路指標綁定（與 `szSid` 互斥；見下方「迴路指標模式」）— 僅迴路指標模式 |

- 純數值顯示，支援單位標示
- 品質 BAD 時顯示紅色「斷線」
- 結合警報規則變色
- 右鍵可加入趨勢圖清單

**顯示模式 = 當日/當月累積**（plan 2026-07-14-scadapage-accumulation-widget）：

- Designer 屬性面板「顯示模式」select 切換；切回即時值時 5 個累積鍵全部 `delete`，`szWidgetStateJson` 不留新欄位（既有頁面 JSON 完全向後相容）
- 執行期改掛 **`scada-rt-acc`** class（不掛 `scada-rt-value`），1 秒即時迴圈自然跳過，由獨立 **30 秒輪詢** `fetchAndUpdateAccumulations()` → `POST /api/scadapage/accumulation`（批次、以 sid+mode+kind+max 去重）更新
- 執行期畫面不顯示 badge，hover tooltip 顯示「點位名稱 + 日累/月累」（i18n：`scadapage.acc.day_badge` / `scadapage.acc.month_badge`）；Designer 編輯期保留左上角「日累/月累」badge 供辨識
- 兩種累積計算：
  - **meter**：當期累積 = 現值 − 期初（今日 00:00 / 本月 1 號 00:00）最近一筆 Quality=1 邊界值，溢位語意同 EnergyReport（`end<start` 且有 MaxValue → `(Max−Vs)+Ve`；無 → 視為歸零記 0 + 警告）。已知限制：整期只偵測一次溢位
  - **integrate**：對 HistoryData 每分鐘值做左矩形時間積分（值 × Δt 小時），段長 clamp 至 `min(下一筆, 期末, 本筆+MaxGap 5 分)`，掉線時段不灌水
- 後端 `WidgetAccumulationService`（Scoped）+ `WidgetAccumulationCache`（Singleton）分層快取：完成日（L1）/今日完成小時（L2）bucket 永久快取、當前小時尾段（L3）即時算、結果 30 秒微快取（L4）；穩態每 SID 每輪詢僅掃當前小時 ≤60 筆，整月掃描只在首次補洞發生一次；跨日/跨月由 cache key 含日期自然切期 + 每日 prune
- 狀態呈現：`no_data`（期初/期內無資料）→ 灰字 `--`；`stale`（點位資料過舊）→ 值以灰色呈現；累積模式**不套用**警報高低限著色（警報規則針對瞬時值）
- 設定：`appsettings.json` `ScadaPageAccumulation`（`MaxStalenessHours` 預設 2、`MaxGapMinutes` 預設 5、`ResultCacheSeconds` 預設 30）
- 綁定 SID 恰為某迴路的 kWh 累積讀值 SID、且溢位上限空白時，自動帶入該迴路的 `MaxKwh`（綁定當下與切到累積模式時皆會嘗試）

**顯示模式 = 迴路指標**（plan 2026-07-23-designer-table-ems-accumulation-autofill）：

AI 點位除綁單一 SID 外，也可在 picker 的「**迴路**」來源分頁直接綁 `EnergyCircuit`（含**虛擬迴路**如虛擬主要電表），依綁定型別自動切換屬性面板（SID 型顯示模式區塊 ↔ 迴路指標五選）。

五指標語意（「本月」刻意三分，曆月與期別可同表比對）：

| `szMetric` | 名稱 | 期界 | 計算來源 |
|------------|------|------|---------|
| `day_kwh` | 本日度數 | 曆日（今日 00:00 起） | `EnergyReportService.GetBucketKwhForRangesAsync`（遞迴葉子 × EffectiveSign、staleness、溢位，與用電報表同核心） |
| `month_kwh` | 本月度數 | 曆月（本月 1 號 00:00 起） | 同上 |
| `period_kwh` | 本月電度 | 月結期別（BillingPeriods） | `ElectricityCostService.GetStatusAsync().totalKwh` — 與 EMS 電費狀態卡**同一支方法、零重算** |
| `period_cost` | 本月電費 | 月結期別 | 同上 `.totalCost`（progressive/surcharge 子迴路為占比分攤估算 `isEstimated`，tooltip 註記「（估算）」） |
| `demand_kw` | 即時需量 | 今日最新一筆（每分鐘一筆，kW） | `IDataRepository.GetTodayDemandByCircuitIdAsync` — 與 EMS 今日即時需量卡**同一支方法、零重算**（遞迴 `IsDemandEnabled=1` 葉子按 Timestamp SUM 聚合，虛擬迴路亦支援） |

- 綁定鍵：`nCircuitId` + `szCircuitName`（快照，執行期以 id 為準）+ `szMetric`，與 `szSid` 互斥（綁迴路清 SID 鍵與 SID 型累積鍵；改綁點位時迴路鍵全 `delete`）— 未使用者 JSON 零改動
- 執行期掛 **`scada-rt-cmetric`** class（1 秒即時迴圈與 SID 累積輪詢皆不觸碰），由獨立 **30 秒輪詢** `fetchAndUpdateCircuitMetrics()` → `POST /api/scadapage/circuit-metric`（批次、以 circuitId+metric 去重、**超過 50 筆自動分批**）更新
- 後端 `WidgetCircuitMetricService`（Scoped）+ `WidgetCircuitMetricCache`（Singleton，結果 TTL 60 秒 + per-key 鎖防 stampede）；30s 輪詢 × 60s 快取下負載與 EMS 首頁同量級。設定：`appsettings.json` `ScadaPageCircuitMetric:ResultCacheSeconds`（預設 60）
- 狀態呈現：`no_data`（迴路已刪除/無綁 SID 葉子）與 `no_plan`（電費指標但未選電價方案）→ 灰字 `--`；`stale`（部分邊界值缺）→ 值以灰色呈現
- `demand_kw` 狀態對應（`WidgetCircuitMetricService.MapDemandResult` 純函數）：當日查無資料或無 `IsDemandEnabled` 後裔 → `no_data`；最新一筆 `Quality=0`（資料不足，DemandKW 固定 0）→ `no_data`（不顯示誤導性的 0）；最新一筆距今 **> 5 分鐘**（Engine 停擺）→ `stale` 值保留灰色；其餘 → `ok`
- 單位自動決定：kWh 指標固定 kWh；`demand_kw` 固定 kW；`period_cost` 依語系顯示「元 / NT$」。hover tooltip = 迴路名 + 指標名
- Designer 編輯期左上角綠色 badge 顯示指標縮寫（日度/月度/期度/期費/需量）
- 迴路被刪除後：執行期回 `no_data` 灰字（依 `nCircuitId` 查無迴路），Designer 顯示快照名 — 不自動清 JSON

#### (3) diPoint — DI 數位輸入點位

| 屬性 | 說明 |
|------|------|
| `szSid` | 綁定的點位 SID |
| `szDisplayMode` | 顯示模式：`indicator`（圓形燈號）/ `text`（文字） |
| `szOnColor` / `szOffColor` | ON/OFF 顏色 |
| `szOnLabel` / `szOffLabel` | ON/OFF 文字（text 模式） |
| `nIndicatorSize` | 燈號直徑 |
| `szAlarmColor` | 警報色 |

- ON/OFF 判定邏輯：值為 1、'1'、true、'ON' 或 ≥1 視為 ON
- 結合 DI 警報規則，觸發條件匹配時脈動動畫 (`di-alarm-pulse`)
- 右鍵可加入趨勢圖清單

#### (4) controlBtn — 控制按鈕

| 屬性 | 說明 |
|------|------|
| `szCid` | 控制目標 CID |
| `szBtnLabel` | 按鈕文字 |
| `szBtnIcon` | Font Awesome 圖示 |
| `fCtrlValue` | 下達數值（預設 1） |
| `szBtnColor` | 按鈕顏色 |

- 點擊時彈出 `confirm` 確認對話框
- 發送 `POST /api/control/write { cid, value }`（預設手動模式）
- 未綁定 CID 時按鈕 disabled
- 需有 `canControl` 權限才綁定點擊事件

#### (5) aoPoint — AO 類比輸出點位

| 屬性 | 說明 |
|------|------|
| `szCid` | 控制目標 CID |
| `szDisplayName` | 顯示名稱 |
| `szUnit` | 單位 |
| `fMin` / `fMax` / `fStep` | 輸入範圍與步進 |
| `nDecimalPlaces` | 小數位數 |
| `szMenuManualLabel` | 右鍵「手動控制」選項文字 |
| `szMenuAutoLabel` | 右鍵「自動控制」選項文字 |

**右鍵選單操作**：
- **手動控制**：輸入數值 → 範圍驗證 → `POST { cid, value, mode:"manual" }`
- **自動控制**：confirm 確認 → `POST { cid, mode:"auto" }`
- 當前模式以藍色外框高亮 (`_applyActiveStyle`)

#### (6) doPoint — DO 數位輸出點位

| 屬性 | 說明 |
|------|------|
| `szCid` | 控制目標 CID |
| `szDisplayName` | 顯示名稱 |
| `nOnValue` / `nOffValue` | ON/OFF 寫入值（預設 1/0） |
| `szMenuOnLabel` / `szMenuOffLabel` | 右鍵 ON/OFF 選項文字 |
| `szMenuAutoLabel` | 右鍵「自動控制」選項文字 |

**右鍵選單操作**：
- **ON**：confirm → `POST { cid, value: nOnValue }`
- **OFF**：confirm → `POST { cid, value: nOffValue }`
- **自動控制**：confirm → `POST { cid, mode:"auto" }`
- 當前狀態以藍色外框高亮

#### (7) pump — 水泵元件

| 屬性 | 說明 |
|------|------|
| `szSidRun` | 運轉狀態 SID（DI 讀取） |
| `szSidFault` | 故障狀態 SID（DI 讀取） |
| `szSidMode` | 手/自動模式 SID |
| `szSidFreq` | 頻率回授 SID |
| `szCidStartStop` | 啟停控制 CID |
| `szCidFreqSet` | 頻率設定 CID |
| `nFreqSetMin` / `nFreqSetMax` | 頻率設定範圍 |
| `nFreqMax` | 頻率 Gauge 最大刻度 |
| `szRunColor` / `szStopColor` / `szFaultColor` | 各狀態色 |
| `szManualColor` / `szAutoColor` | 手/自動模式色 |
| `szOutletDir` | 出口方向（right / left / up） |

**SVG 渲染**：
- 泵體（含基座、管路、葉片）以 SVG 繪製
- 運轉中：葉片旋轉動畫 (`pump-spin`)
- 故障：整體變紅
- 手/自動：泵體顏色切換（黃/藍）
- 頻率 Linear Gauge：直條圖顯示目前頻率

**右鍵選單**（二級結構）：
- **啟動停止**（子選單）：啟動 / 停止 / 自動控制
- **頻率設定**（子選單）：輸入框 + 確定按鈕 / 自動控制
- **監控點位**（勾選框）：可勾選運轉、故障、模式、頻率等 SID 加入趨勢圖

> 頻率設定的「確定」走手動模式（M 角標亮起），「自動控制」對 `szCidFreqSet` 標記為自動（M 角標熄滅）；與啟動停止的手/自切換相互獨立。

**Gauge 拖拽**：
- 滑鼠在頻率 Gauge 直條上拖拽可即時調整
- mousedown → mousemove 更新視覺 → mouseup 發送控制指令

#### (8) table — 資料表格

| 屬性 | 說明 |
|------|------|
| `nCols` / `nRows` | 欄位數 / 列數（欄數不設上限，ScadaPage 依 `nCols` 完整顯示） |
| `szHeaderColor` | 表頭背景色 |
| `szBodyBgOdd` / `szBodyBgEven` | 奇/偶數資料列底色（`null` = 沿用預設外觀） |
| `szBorderColor` | 資料列分隔線色（`null` = 預設 `#f0f0f0`） |
| `arrCells` | 二維陣列，每格可綁定 SID 或迴路指標 |
| `arrColDecimals` | 各欄小數位數（迴路指標 cell 亦沿用） |

- 每格可綁定 AI 或 DI 點位
- AI：數值顯示 + 警報變色
- DI：ON/OFF 文字 + 警報變色
- 品質 BAD 時顯示「斷線」
- 右鍵可針對單一儲存格加入趨勢圖

**迴路指標 cell**（plan 2026-07-23）：資料 cell 也可在 picker「迴路」分頁綁 `EnergyCircuit` 五指標
（cell 級新鍵 `nCircuitId` / `szCircuitName` / `szMetric`，與 `szSid` 互斥，解除綁定時全 `delete`）。
五指標語意與計算來源同 realtimeValue「迴路指標模式」一節。執行期 td 掛 `scada-cmetric-cell` class 且**不帶 `data-sid`**
→ 1 秒 `td[data-sid]` 路徑不觸碰，由 30 秒 `fetchAndUpdateCircuitMetrics()` 更新；`title` 屬性 hover 顯示「迴路名＋指標名」。

**表頭驅動整列自動帶入**（plan 2026-07-23 決策 2）：表格慣例 = 第 1 欄（col 0）列名、第 1 列（row 0）欄名。
在某資料 cell 綁定**迴路**、或綁定的**點位 SID 反查命中迴路**（`EnergyCircuit` 的 SID/VoltageSID/CurrentSID/PowerSID/PowerFactorSID 五欄比對）時：

- 即時值欄（V/A/kW/PF/kWh）與指標欄（本日/本月…）**一律進同一個確認清單 modal**（`cmetricFillModal`，樣式複用列範本 modal）：
  逐列顯示「欄名 → 將帶入的點位/指標」＋標籤（即時值/指標/未找到對應點位/已綁定不覆蓋），可帶入項預設勾選、
  可逐項取消客製，按「帶入勾選項目」才套用（「不帶入」則完全不動）；套用後 toast 摘要
- **即時值欄建議來源順序**（v8）：(1) **同設備點位優先** — 在剛綁定點位的同一設備（szDeviceLabel 相同）下以點位名
  比對角色（走共用 `window._roleMatch.resolveRole`，規則見下方「點位名→角色比對」），列＝設備的心智模型優先；
  (2) 找不到才退回**迴路五 SID 欄**（迴路綁定/虛擬迴路情境的唯一來源）。兩者皆無 → 該欄標「未找到對應點位」
- 只填空白 cell（未綁 SID 也未綁迴路指標）；已綁定 cell 顯示於清單但鎖定不可勾（**不覆蓋**）
- **回落規則（保留固定樣板，v8）**：表頭**任一欄命中**（即時值或指標皆算）即由本流程接手；
  綁一般點位且表頭**完全無命中**時才回落既有「列範本」建議流程（plan 2026-06-01）。
  綁**迴路**時一律由本流程接手（列範本需點位名比對，迴路綁定無從回落）
- **列範本落格改「表頭優先」**（v6，回應「沒依第一列名順序排列」）：列範本套用時每個角色先找**表頭命中該角色的空白欄對號入座**
  （左右都找，不限綁定欄右側；表頭正規化與別名沿用 `_roleAliases` + `_normalizeHeaderText`），
  沒命中表頭的角色才照舊自綁定欄右鄰循序遞補，且**不搶表頭命中其他角色的欄**；
  建議面板預覽逐列顯示「帶入欄『表頭名』」讓使用者確認落點，欄位不足亦標示

比對規則：表頭先**正規化**（trim、全形→半形、去尾端括號單位後綴如「電壓(V)」「本月電費（元）」、壓縮空白、小寫）再與別名表**完全相符**。
別名表集中於 `picker.js` 的 `CIRCUIT_HEADER_ALIASES` 常數，現場要加詞只改一處：

| 帶入 | 表頭別名 |
|------|---------|
| `VoltageSID` 即時值 | V / 電壓 / 電壓V / Voltage / Volt |
| `CurrentSID` 即時值 | A / 電流 / 電流A / Current / Amp / Amps |
| `PowerSID` 即時值 | kW / 功率 / 即時功率 / 瞬時功率 / 實功 / Power |
| `PowerFactorSID` 即時值 | PF / 功因 / 功率因數 / Power Factor / cosφ / cosphi |
| `SID`（kWh 累積讀值）即時值 | kWh / 電度 / 度數 / 累積電度 / 電表讀值 / 累計度數 / Energy |
| 指標 `day_kwh` | 本日度數 / 本日用電 / 今日度數 / 今日用電 / 當日度數 / 當日用電 / 當日累積 / 日用電量 / 本日kWh / 今日kWh / 當日kWh / 日kWh / 本日用電量 / Today kWh / Daily kWh / Day kWh |
| 指標 `month_kwh` | 本月度數 / 本月用電 / 當月度數 / 當月用電 / 當月累積 / 月用電量 / 本月kWh / 當月kWh / 月kWh / 本月用電量 / Month kWh / Monthly kWh |
| 指標 `period_kwh` | 本月電度 / 本期電度 / 本期度數 / 本期用電 / 本期kWh / Period kWh / Billing kWh |
| 指標 `period_cost` | 本月電費 / 本期電費 / 當月電費 / 電費 / 電費金額 / Cost / Period Cost / Electricity Cost |

自訂表頭不命中就不自動帶（仍可手動逐 cell 綁定）；迴路未設定某欄 SID（如無 PF 點）時該欄略過。

**點位名→角色比對（`window._roleMatch.resolveRole`，2026-09-15 改）**：上表是「**表頭欄名文字**→角色」的完全相符比對（`_matchHeaderRole`），本段是「**點位名**→角色」的比對，兩者分開、規則不同。同設備點位建議（`picker.js` 的 `_findSameDevicePointForRole`）與列範本建議（`row-template.js` 的 `matchRole`）皆改走共用 `window._roleMatch.resolveRole`（規則集中於 `role-aliases.js`，現場加詞只改 `_roleAliases` 一處）：把點位名正規化後以 `- _ 空白` 切成 tokens，依「**整名 → 開頭 token → 尾段 token**」順序查 `_roleAliases`，任一命中即定角色。
- **開頭優先於尾段** 是為了吃使用者「角色在前、相別在後」的命名：`V_a`/`V_b`/`V_c` → 電壓、`I_avg` → 電流（不再把尾段 `a`/`avg` 當角色，電壓欄不會被電流搶、反之亦然）
- **整名先查** 是為了不拆散本身就是完整別名的複合詞（`power_factor` → PF、`real_power` → kW、`l-n` → V）
- **設備前綴式**（`PM-1-V`、`北101-A`）開頭 token 非角色 → 自然退回尾段判定，行為與舊版一致；`row-template` 的 `matchRole` 僅在**開頭 token 命中角色**時才把設備標籤當前綴（風格 B'），故前綴式仍走原「切分隔符取末段」風格 A

**預設底色樣式（2026-07-04）**：Designer 屬性面板提供 5 套一鍵樣式色塊 —
經典深灰 / SCADA 藍 / EMS 綠 / 深色 / 極簡白（定義於 `widget-defs.js` 的 `TABLE_STYLE_PRESETS`）。

- **一次性填色**：點選 preset 是把顏色**複製**進 props（表頭色、奇/偶列底色、框線色，並**整組覆蓋**所有儲存格字色），不儲存 preset key，套用後仍可用「表頭底色 / 奇數列底色 / 偶數列底色 / 框線色」欄位個別微調
- **向下相容**：三個新 props 預設 `null` — 舊存檔與未套樣式的新表格完全維持現行外觀（Designer 淡灰斑馬紋、執行期白底），不會因本功能改變既有場域

#### (9) text — 靜態文字標籤

純文字顯示，可設定字型、大小、顏色、粗體、斜體、背景色，不綁定任何數據。

#### (10) 馬達型設備 — 冷卻水塔 / 空調箱風扇 / 冰機（coolingTower / ahuFan / chiller）

仿水泵（pump）的「馬達型設備」家族，**依設備差異客製**綁定與控制能力。三者的 SVG 圖形（本體 + 主數值條）由 **`wwwroot/js/common/motor-equip-svg.js`（`window.MotorEquip.build`）統一產生，供 Designer 預覽與 ScadaPage 執行期共用同一份圖**（避免兩處走樣）。此模組由 Designer 與 ScadaPage 兩頁的 View 各自 `<script>` 引入。

**各設備綁定集**：

| 設備 | SID 監控 | CID 控制 | 主數值條 | 旋轉 |
|------|----------|----------|----------|------|
| 冷卻水塔 `coolingTower` | 運轉 / 故障 / 手自動 / **風扇頻率** / **出水溫**(僅 tooltip) | 啟停 / 頻率設定 / 自動 | 頻率（VFD，可拖曳） | 頂部風扇 |
| 空調箱風扇 `ahuFan` | 運轉 / 故障 / 手自動 / **頻率** | 啟停 / 頻率設定 / 自動 | 頻率（VFD，可拖曳） | 中央風扇 |
| 冰機 `chiller` | 運轉 / 故障 / **遠端/現場**(=szSidMode) / **負載%** / **冰水出水溫**(僅 tooltip) | 啟停 / **冰水設定溫度** / 自動 | 負載%（唯讀，**底部水平橫條**） | 無（狀態以機身雙筒顏色表示） |

props 命名沿用 pump 慣例（`szSidXxx` + `szXxxName` 成對）：共同 `szSidRun/szSidFault/szSidMode/szCidStartStop`；VFD 型另有 `szSidFreq/szCidFreqSet/nFreqSetMin/Max/nFreqMax`，冷卻水塔加 `szSidWaterTemp`；冰機為 `szSidLoad/szSidChwOut/szCidSetTemp/nLoadMax`。外觀色與 pump 相同（`szRunColor/szStopColor/szFaultColor/szManualColor/szAutoColor/szBgColor`），三者**不提供出口方向**。屬性面板每條綁定列已綁時有「重選」＋「✕ 取消綁定」（清空 SID＋名稱）。

> **冷卻水塔圖形**為**等軸測方箱塔體**（2026-07 依使用者參考圖重繪）：左面浪板紋、右面 X 型斜撐面板、頂部黃色雙層安全欄杆＋小馬達、上置圓形風扇（6 葉）。葉輪畫在壓扁前座標、外層再套 `scale(1,0.5)` 等軸壓扁，旋轉沿用 `pump-spin`（transform-origin 落在內層群組，旋轉在局部座標系進行故呈橢圓轉動）。葉輪盤面＝狀態色、箱體＝手自動模式色，語意與其他馬達型設備一致；Designer 元件庫縮圖（Index.cshtml inline SVG）為同構簡化版。

> **冰機圖形**為**水冷式雙筒身前視圖**（2026-07 依現場照片重繪，Artifact 預覽 v4 定稿）：上筒＝冷凝器、下筒＝蒸發器；**不畫接管**（2026-07-23 拿掉原本左右四支法蘭接管——管路 pipe 元件由使用者自由決定對接位置）；後方帶螺旋壓縮機輪廓與殼間連通管，左前控制箱含固定深色 HMI 螢幕（不變色）、指示燈、壓力錶。**顏色職責**與其他馬達型設備不同：**機身雙筒＝狀態色**（運轉綠/停機灰/故障紅，頂部小警示燈同色）；`szSidMode` 對冰機語意為**遠端/現場**（1=遠端→控制箱面板灰、0=現場→面板深黃 `szManualColor`，預設 `#c79100`，屬性面板標籤「現場面板顏色」，自動色不適用）。負載% 為**底部水平橫條**（軌道 x10 寬66、% 文字置中條內，`viewBox 0 0 120 124`；無條時 `0 0 120 110`），右下角為設定溫度覆蓋層——**設定溫度切手動時值前顯示小 M**（吃 `szCidSetTemp` 的 isAuto，掛 `scada-mode-badge` class 由既有 `_toggleModeBadge` 輪詢驅動），與右上角「開關手動 M」（`szCidStartStop`）各自獨立。Designer 元件庫縮圖為同構簡化版。

**執行期行為**（`js/scadapage/widget-motor.js` + `ctx-menus-equip.js`，class = `.scada-motor`，`dataset.motorType` 區分）：
- 狀態判定同 pump：故障 > 運轉 > 停止，運轉時風扇 `pump-spin` 旋轉（冰機不轉）
- 手動模式顯示 M 角標（`szCidStartStop`＝開關手動，右上；VFD 型另有 `szCidFreqSet`；冰機另有**設定溫度手動 M**顯示於右下設定值旁，吃 `szCidSetTemp` 的 isAuto）
- **右鍵選單**：啟動停止（子選單）+ VFD 頻率設定（子選單）/ 冰機設定溫度（項目）+ 監控點位趨勢圖
- **VFD 頻率條拖曳**：與 pump 共用同一 mousedown 處理（選擇器擴充為 `.scada-pump, .scada-motor`，重用 `pump-gauge-*` class）
- **冰機設定溫度**：**無上下限**，於元件**右下角常駐顯示**目前設定值，**雙擊即可編輯**（prompt 輸入 → 寫入 `szCidSetTemp`，`actionType='ao_manual'`）；顯示值取自 AO 手動值快取
- 斷線（Bad quality）**圖形照畫**（停止灰）並於元件上方置中顯示紅字「斷線」，hover 仍浮出「標題 — 斷線」（2026-07-23 改版，原本整塊換成純文字）；hover 平時顯示「標題 — 狀態 — 模式 — 主數值 — 額外溫度」

> **控制 EventLog**：啟停 / 頻率 / 自動沿用 pump 的 `pump_start_stop` / `pump_freq` / `pump_auto` actionType，訊息帶設備名稱（`displayName`）以資區別（例「啟動泵浦「1F 冷卻水塔」」）；冰機設定溫度走 `ao_manual`。此為刻意選擇的零後端改動方案。

> **設計決策**：採「抽共用核心」策略——三設備共用一份 `MotorEquip` 圖形模組與一套執行期邏輯（右鍵 / 拖曳 / 輪詢），並重用 pump 既有 helper（`_buildModeBadgeHtml` / `_pumpStartStop` 等）。水泵本身未遷移以避免生產回歸風險。詳見 `docs/plans/_archive/2026-07-07-designer-冰機冷卻水塔空調箱風扇元件.md`。

**設備級自動綁定（Designer，2026-10-14；pump / coolingTower / ahuFan / chiller 共用）**

綁定單位從「點位」提升到「設備」：元件 = 某台設備的一個實例，使用者只回答「這是哪台 Device」，各槽位點位自動對上（Ignition UDT + Template / WinCC Faceplate 的實例化心智模型）。純前端、不動儲存格式。

- **流程**：工具箱拖四種設備元件到畫布 → 開既有 picker（標題「選擇設備」）→ 走到設備**葉節點**（單站號整站 / 多站號子站 / 單站號 Device 子群 / 未分群桶 / DB 來源群組 / 計算點位群組）時**不進點位清單**，改在同一區就地顯示**命中預覽**（每個槽位 → 命中點位名 / 「未找到」/ 「多筆命中（取第一筆）」，頂部「命中 N / M 個槽位」）→「確認」建立元件並整台帶入（`szTitle` = 葉節點標籤、各槽位 `szSid*/szCid*` + `sz*Name`、頻率 / 頻率設定 / 負載的 min/max 依點位 fMin/fMax 帶入，規則與單槽位重選相同）→ toast「已帶入 N / M」。「返回」可換設備；**關閉 picker（取消 / X）仍建立未綁定元件**（保留先排版後綁點的工作流）。
- **重新指定設備**：屬性面板 SID 監控段頂部按鈕 → 同一流程；元件已有任一槽位綁定時確認前先 confirm「將以『X』的點位覆蓋目前綁定，未命中的槽位會清空」→ 命中槽位覆蓋、未命中清空（元件 = 設備實例，換設備就整台換；要補單一槽位仍用既有「重選」）。
- **候選集合** = picker 第二步原本會列的那組點位（`picker.js` 的 `_pointInPickedDevice`，與點位清單同一條篩選）。Device 欄空白的站 → 候選 = 整站，同角色多筆命中機率高（面板會標示、取第一筆），在 Modbus 來源頁補 Device 欄即自動變好。
- **槽位 → 角色對照**（`js/designer/equip-autobind.js` 的 `EQUIP_SLOT_ROLES`，只解析該型別需要的角色）：pump = RUN / FAULT / MODE / FREQ / STARTSTOP / FREQSET；coolingTower 另加 WATERTEMP；ahuFan 同 pump；chiller = RUN / FAULT / MODE / LOAD / CHWOUT / STARTSTOP / SETTEMP。
- **點位名 → 設備角色**（`role-aliases.js` 的 `window._equipRoleAliases` + `_equipRoleMatch.resolveEquipRole`，與電表角色 `_roleMatch.resolveRole` **完全獨立**）：正規化（全形→半形、去尾括號單位、小寫）後依「**整名 → 尾 token（以 `- _ 空白` 切、≥ 2 段）→ 字尾式（單段、別名長度由長到短 endsWith）**」三段比對。三種命名風格皆吃：純角色 `運轉` / `故障` / `頻率設定`、分隔符 `PUMP-1-RUN` / `冰水泵1_運轉`、字尾式 `CH1RUN` / `CH1MOA` / `CWP1ALARM`。`on / st / sp / fb` 短別名只在前綴非空時成立（`CH1ON` ✓、單獨 `ON` ✗）；`出水溫` 同時在 CHWOUT 與 WATERTEMP，靠型別白名單不衝突。現場要加詞只改 `_equipRoleAliases` 一張表。
- **限制**：名稱不含任何角色字（如 `Modbus61`）→「未找到」，v1 不猜；整台帶入不新增任何 props key，複製頁面清空綁定規則（`isDesignerBindingKey`）自動涵蓋。

#### (11) pipe — 管路流動元件（正交折線）

**正交折線（orthogonal polyline）管路**（2026-07 改版）：一條管由一串節點（`arrPoints`）組成，每段仍為水平或垂直（不變量：任兩相鄰節點共 x 或共 y），可自由拉出 L 型／ㄇ 型／多轉角走線 — **一條折線管＝一個元件＝一份綁定**，取代舊「多段直管拼接」做法（轉角流動不連續、綁定分散）。

**資料模型**：`props.arrPoints = [{x,y}, ...]`（≥2 點，座標相對 widget 左上角 px）。widget 外框（left/top/width/height）＝節點 bounding box ＋ 線寬留白（`PipeSvg.padOf` = ⌈粗細/2⌉+2），節點編輯後自動重算。`szOrient` 保留為相容欄位不再寫入。

**舊存檔相容**：載入時無 `arrPoints` → 由 `szOrient` ＋ widget 寬高推導 2 節點直管（h：左中→右中；v：上中→下中，端點內縮粗細/2 使 round cap 與舊版圓角端等視覺）。ScadaPage 執行期推導**不回寫**；Designer 端 `preparePipeWidget` 於渲染時轉為新格式，重存即落 `arrPoints`。

**流動動畫技術**：SVG 同一條 `<path>` 疊三層 — 底層 track（停止/斷線色實線）、上層 flow（流動色 `stroke-dasharray: 12 10` ＋ `stroke-dashoffset` keyframes `pipe-dash`）、頂層 hit（加寬透明 stroke，唯一 `pointer-events` 命中區）。`stroke-linejoin/linecap: round` 使轉角自然圓滑、dash 沿整條路徑**連續過彎**（瀏覽器原生處理，任意段數零額外成本）。流向以 `animation-direction:reverse` 切換，流速檔 1-5 映射動畫時長（`PipeSvg.SPEED_DUR`）。viewBox 與 widget 尺寸 1:1（大小由節點決定、無非等比縮放，舊版「CSS gradient 防縮放扭曲」的顧慮不復存在）。

**Designer 節點編輯**（widget-core.js）：

- 選取管路 → 顯示節點手把（`.pipe-node`；端點實心、中間節點空心）
- **拖曳節點**：自由拖（貼齊 1px 格），即時正交修正 — 只動直接鄰點：鄰點是端點→沿共用段原方向跟隨（原水平繼承 y、原垂直繼承 x）；鄰點是中間節點→修正在不破壞其外側線段的軸上（外側原水平→改鄰點 x、原垂直→改鄰點 y），任何案例（含連續共線節點）修正後全鏈仍正交、無需連鎖擴散。放開時去除拖到重合的重複節點
- **Ctrl+點擊管身**：於滑鼠位置（投影至最近線段上）插入節點（插入當下共線，拖開即折彎）。僅在該管路**已選取**時生效；未選取時 Ctrl+點擊走畫布共通選取（Ctrl 剃除，見下方「畫布共通編輯操作」）（2026-09 由「雙擊管身於線段中點插入」改版，較直覺且可直接點在想折彎的位置）
- **Ctrl+點擊或右鍵節點**：刪除；刪後前後兩點不共軸→自動補一個轉角點維持正交；少於 2 點禁刪
- 右下角 **resize 鈕停用**（隱藏＋`startResize`/`setSize` 短路），大小完全由節點決定，屬性面板寬高欄唯讀；整體拖移（管身 mousedown）照舊
- 命中只認管身加寬透明 stroke（容器 `pointer-events:none`）— bounding box 空白區不會擋到底下元件

**綁定（二擇一互斥）** — `szBindMode` 為單一真相（`''` 未綁 / `'di'` / `'analog'`），DI 與「類比量＋閾值」共用同一 `szSid` 欄，任一時刻僅一種生效：

| 模式 | 流動判定 |
|------|----------|
| DI (`di`) | 即時值為 ON（1 / true / ≥1）→ 流動；OFF → 靜止 |
| 類比 (`analog`) | 即時值 `> 閾值`（或 `≥`，由 `szCompare` 決定）→ 流動；否則靜止 |

於屬性面板點另一種綁定並完成選點時，若已綁另一模式且有值 → **跳 confirm**：確認則清除原綁定改綁新的、取消則完全不動（互斥收斂在 `picker.js` 的 `confirmPointPick` pipe 分支）。首次進入類比模式以點位中間值作為預設閾值。

props：`szBindMode / szSid / szPointName / fThreshold / szCompare('gt'|'gte') / arrPoints([{x,y}...]) / szOrient('h'|'v'，相容欄位) / nThickness / szFlowColor / szStopColor / szBadColor / nSpeed(1-5) / szDir('fwd'|'rev') / szBgColor`。

- 拖入畫布**直接建立**（不先開 picker，預設 2 節點水平直管），綁定於屬性面板完成
- 斷線（Bad quality）→ 以 `szBadColor` 靜止顯示、不流動，tooltip 顯示「斷線」
- 未綁定 → 純裝飾，固定流動
- hover 管身顯示「標題 — 狀態（流動 / 靜止 / 斷線）— 數值(類比)」tooltip；右鍵管身開趨勢圖照舊
- 圖形共用 `wwwroot/js/common/pipe-svg.js`（`window.PipeSvg.build`）：Designer `buildPipeHtml`（widget-defs.js）與 ScadaPage `buildPipeViewHtml`（js/scadapage/widget-pipe.js）皆為薄包裝，同 `motor-equip-svg.js` 單一真相模式；樣式 `.pipe-svg / .pipe-svg-track / .pipe-svg-flow / .pipe-svg-hit`（舊 `.pipe-h / .pipe-v` CSS 保留一版防未升級快取頁面）

#### (12) image — 圖片/動畫元件（自訂 GIF）

**用途**：匯入使用者自有的 GIF 動畫（或靜態圖）作為設備圖，依綁定點位狀態切換「動 / 不動」。GIF 天生 `<img>` 自動播放，切成靜止圖即「停」，無需自行做影格控制。

**兩張圖**：
- `szAnimSrc`：**動畫圖（GIF）** — 綁定條件成立時顯示（播放）
- `szStillSrc`：**靜止圖** — 綁定條件不成立時顯示（停止）；未提供時 fallback 為「動畫圖 + 灰階降透明」當已停視覺提示

**兩種載入來源**（widget 一律只存一個 URL 字串，渲染端源無關）：
1. **內建圖庫**：`wwwroot/img/designer-gifs/{分類}/{名稱}_anim.*` + `{名稱}_still.*`，`GET /Designer/gallery` 掃目錄動態列出（免手維護 manifest），屬性面板縮圖 grid 點選即用，存靜態 URL（如 `/img/designer-gifs/Fan/fan_anim.gif`）
2. **自訂上傳**：`POST /Designer/asset`（`Roles=Engineer`，單檔 ≤2MB，限 gif/png/jpeg/webp/svg），以內容 **SHA-256 去重**存 `ScadaDesignAsset` 表（base64 於 `nvarchar(MAX)`；同內容只存一份），回傳 `/Designer/asset/{hash}` URL。`GET /Designer/asset/{hash}` 提供內容（內容定址 → `Cache-Control: immutable` 長快取）

**綁定（二擇一互斥，沿用管路 `szBindMode` 模型）**：`''` 未綁（純裝飾固定播放）/ `'di'`（值 ON→動）/ `'analog'`（`> 閾值` 或 `≥`，由 `szCompare` 決定→動）。斷線（Bad quality）→ 靜止圖 + 灰階。

props：`szAnimSrc / szStillSrc / szBindMode / szSid / szPointName / fThreshold / szCompare('gt'|'gte') / szFit('contain'|'cover'|'fill') / szBgColor`。

- 拖入畫布**直接建立**（不先開 picker），圖片與綁定於屬性面板設定；Designer 畫布本體固定顯示動畫圖（`run`）表明這是動畫元件
- 圖形共用 `wwwroot/js/common/image-widget.js`（`window.ImageWidget.build(props, szState, 提示字)`）：Designer `buildImageHtml`（widget-defs.js）與 ScadaPage 執行期（render.js `.scada-image` 分派）皆為薄包裝，同 `pipe-svg.js` 單一真相模式
- 執行期依 `_imgState` 比對，狀態不變不重繪（避免每秒換 innerHTML 讓 GIF 重播閃爍）

> **儲存設計（plan 2026-09-18）**：GIF 不塞進頁 `WidgetStateJson`（會膨脹、同圖多份），改以 `ScadaDesignAsset` 去重表 + URL 引用。孤兒資產（widget 刪除後）目前 append-only 不即時回收，日後如需要再補離線 GC。

#### (13) trendChart — 即時曲線（滾動趨勢圖）

**用途**：在監控畫面上直接呈現單一點位「近 N 分鐘」的**會自己往左滾動**的曲線。與 `/History/Trend`（查詢式、要按下查詢才畫）語意不同 —— 這是右端進、左端出的即時滾動圖。對應南亞大里標案 2-7 即時曲線。

**一 widget 一點位**（plan 2026-09-29 決策 2）：`szSid` 單值，要看多個點位就拉多張圖並列，不做單圖多筆疊圖。資料層維持 per-SID，日後要擴 `arrSeries` 是加法不是改寫。

props：`szSid / szPointName / szUnit / nWindowSec（時間窗秒數，預設 1800）/ nSampleSec（畫點間隔秒，預設 5）/ fYMin / fYMax（null＝自動）/ nGridCount（Y 軸區隔線數，預設 5）/ szLineColor / nLineWidth / szBgColor / szBorderColor / szGridColor / isShowLegend`。

**預設配色（2026-09-29 起）**：白底 `#ffffff`、灰格線 `#cccccc`、黑框 `#000000`、藍線 `#0d6efd`，仿現場報表外觀，拖到深色底圖上格線仍清楚。`defaultProps` 與三處 `||` fallback（Designer `buildTrendChartHtml`、ScadaPage `widget-trend.js` 格線、`render.js` 邊框）同值，不存在兩套預設；既有已存檔 widget 的 props 已寫死顏色，外觀不受影響。

**資料來源分兩段（plan 決策 1）—— 穩態下整頁零 DB 查詢**：

1. **掛載時 backfill 一次**：`GET /api/history/data?szSID=…&szStart=now-N&szEnd=now&nInterval=0` 補滿時間窗 → 開頁曲線**一開始就是滿的**，不是從空白慢慢長。一頁 N 張圖限流 **3 併發**（`TREND_BACKFILL_CONCURRENCY`），避免首載尖峰打 DB。失敗不重試。
2. **之後只靠既有每秒 `/api/realtime/latest`**：`updateScadaWidgets` 末尾掃 `.scada-trend[data-sid]` → `pushTrendPoint()` append，再 `tickTrendCharts()` 統一推進 x 軸 min/max、丟棄超窗舊點、`chart.update('none')`。**不做週期性 DB 查詢**（即時值本來就已經在前端手上，再查 DB 是重複做功）。

**誠實呈現故障（plan 決策 3）**：append 以**點位自身 timestamp** 去重，時間沒前進就拒收。Engine 停擺時曲線停在最後有效時間，不會畫出一條「持續有值」的平直假線 —— 把故障畫成正常比沒有曲線更糟。品質 BAD 期間插入 `y=null` + `spanGaps:false` 呈現斷點，不用 0 或前值補。

**點數護欄（plan 決策 7）**：點數 = 時間窗 ÷ 畫點間隔，硬上限 **3600 點**。超過時自動放大畫點間隔（不截時間窗），Designer 屬性面板同步顯示被放大後的實際間隔。算法在 `wwwroot/js/common/trend-window.js`（`window.TrendWindow`），Designer 與執行期共用同一份，避免面板說 360 點、實際畫 720 點。

**縮放清晰度（plan 決策 4，2026-09-29 修正）**：畫布用 `transform: scale`（刻意避開 CSS zoom 放大系統游標），canvas 是 bitmap，只要 bitmap 與螢幕像素不是「整數尺寸 + 整數起點」一對一，瀏覽器就會整張重取樣而發糊。初版把 `devicePixelRatio` 設為 `window.devicePixelRatio × fScale`，但 Chart.js 會把寬高 floor 成整數、`translate(-50%, -50%)` 又讓起點落在小數像素（實測 525px bitmap 攤在 525.36 裝置像素、起點 x=331.68），格線與文字被抹成 2px 灰邊。現做法：canvas 外包一層 `.scada-trend-plot`，由 `_trendSnapPlot()` 設 `scale(1/fScale)` 抵銷畫布縮放，使 canvas 以螢幕原生 DPR 渲染；plot 尺寸取「× DPR 恰為整數」的整數 CSS px（`_trendFitPx`），再以 `translate` 把起點吸附到整數裝置像素。字級 / 線寬 / 內距改由 `_trendApplyScaleOpts()` 乘上 fScale，外觀仍與 Designer 設計尺寸等比（格線維持 1px）。`applyTrendChartScale(fScale)` 每次都重新吸附（側欄收合等位移也會改變起點小數）。Chart.js 事件座標走 `offsetX`（元素本地座標），抵銷後即 chart 座標，tooltip 命中不需補償。

**生命週期（plan 決策 5）**：實例存 `state.js` 的 `_trendCharts`（key = widget 容器 DOM id）。`renderScadaCanvas` 會整塊重建畫布 DOM，故重建前一律 `destroyAllTrendCharts()`；實例不 destroy 會留下 resize listener，切幾次頁就洩漏（24h 不關的圖控站會真的發作）。Chart 實例必須等 widget 都 append 進 DOM 之後才由 `initTrendCharts(canvas)` 統一建立（Chart.js 要量得到容器尺寸）。

- Designer 編輯期**畫固定假波形**（SVG，不連線、不輪詢、不查 DB；plan 決策 6）：格線數／線色／線寬／邊框色／Y 軸範圍／時間窗即時反映，左上角顯示綁定點位名。`.widget-trend .widget-body` padding 歸零，讓編輯期尺寸 = 執行期尺寸
- 拖入畫布**先開 picker 選點位**（同 gauge / AI 點位）；屬性面板「重選」或**雙擊畫布上的元件**皆走 `rerouteTrendChartPoint()`
- 整頁複製（page-clipboard）時 `szSid` / `szPointName` 由既有前綴規則自動清空 → 執行期顯示「未綁定點位」，不建 Chart、不查 DB
- 右鍵為點位選單（`onTrendContextMenu`，與其他 widget 一致，見 §8 點位右鍵選單）

> **重繪節奏調校**：資料收集固定跟著 1 秒即時輪詢，畫面重繪由 `widget-trend.js` 的 `TREND_REDRAW_MS`（預設 1000）控制。一頁多圖若實測 CPU 吃緊，把它調大即可（資料完整性不受影響）。

#### 畫布共通編輯操作（選取 / 移動 / 對齊）

Designer 畫布上所有元件共用一套選取與定位規則（widget-core.js / canvas.js）：

- **選取修飾鍵**：一般點擊 = 單選並清除其他；**Shift+點擊 = 連選（加入選取集合）**；**Ctrl+點擊 = 剃除（自選取集合移出）**。框選（拖曳空白處畫框）同樣支援 Shift / Ctrl 累加，不清空既有選取。
- **最小步進 / 最小尺寸皆為 1px**：`snapGrid` 預設格改為 1（原 10），拖曳移動與 resize 均可 1px 微調；resize 最小尺寸統一放寬為 1×1（不再套用各元件 `nMinW/nMinH`）。背景 10px 網格線僅為視覺參考，不再限制落點。
- **智慧對齊線**：拖曳 / 縮放時自動偵測鄰近元件的**左 / 中 / 右**與**上 / 中 / 下**，距離 ≤ 6px（`ALIGN_SNAP`）即吸附對齊並畫出洋紅參考線（`.designer-align-guide`）；多選群組以整體 bounding box 對齊。放開滑鼠即清除參考線。（`computeAlignGuides` / `drawAlignGuides`）
- **多選群組寬高同步**：選取 ≥ 2 個元件（Shift 連選 / 框選 / `Ctrl+A` 全選）時，屬性面板改為群組面板，顯示選取數與**寬 / 高**兩欄；輸入任一欄即**同步套用到所有選取元件**（各自尊重尺寸鎖定與最小值 —— 折線管路、鎖定尺寸的表格會略過）。`renderMultiSelectPropPanel` / `setSizeMulti`，寬高規則與單選共用 `_applySizeTo`。剃除到剩 1 個時自動切回單選屬性面板。
- **文字元件選取即編輯**：選取 `text` 靜態文字元件時，屬性面板自動 focus 並全選「文字內容」欄，可直接輸入取代（與表格儲存格點選 focus 行為一致）。（`renderPropPanel` text 分支 → `txtContentInput`）
- **雙擊快編**（2026-09-29）：
  - **單一綁定元件**（儀錶板 / 控制按鈕 / AI / DI / AO / DO 點位 / 即時曲線）雙擊 → 直接開點位 picker，已綁定時自動定位到原 SID（DI 綁排程時停在排程分頁、AI 綁迴路時停在迴路分頁）。實作為 `onWidgetDblClick` 型別對照表 `WIDGET_DBLCLICK_REROUTE` 呼叫既有 `rerouteXxxPoint()`，與屬性面板「重選」同一條路徑
  - **文字元件**雙擊 → focus「文字內容」欄並全選，直接打字即覆蓋
  - **多綁定元件**（水泵 / 冷卻水塔 / 空調箱風扇 / 冰機 / 管路 / 圖片 / 表格）雙擊不動作 —— 無法推斷要改哪個綁定欄位，請用屬性面板各欄位的「重選」
  - 多選狀態下雙擊某元件 → 先收斂為單選該元件再開 picker（`rerouteXxxPoint` 以 `selectedEl` 為作用對象）；按住 Ctrl / Shift 的雙擊、雙擊縮放把手／刪除鈕／管路節點不觸發
  - `renderWidget` 每次改屬性都會重跑，dblclick 以 `el._isDblClickBound` 旗標確保每個元素只綁一次（否則改幾次屬性後雙擊會連開多次 picker）

> **元件庫分類（Designer）**：元件庫改為三類 — 顯示元件（表格 / 儀錶板 / 即時曲線 / 文字）、點位與控制（控制按鈕 / AI / DI / AO / DO）、設備與動畫（水泵 / 管路 / 冷卻水塔 / 空調箱風扇 / 冰機 / 圖片動畫）。各類獨立捲動（`.widget-cat-items` overflow-y:auto），`.designer-outer` 釘視窗高使面板本身不捲，避免 100% 時多餘的整體捲軸。
> 每項為**單行列表選單**（2026-09-29）：左側 18px 寬小 icon 前綴 + 右側名稱，行高約 29px（舊版為大圖示在上、文字在下的兩行卡片）。純 CSS 改版（`.widget-lib-item` row 排列），Index.cshtml markup 不變：Font Awesome icon 統一 14px（`!important` 蓋過個別項目 inline 字級），inline SVG 以 CSS 覆寫為寬 18px、`height:auto` 依各自 viewBox 保持原比例（管路 28×16 不變形）。拖放行為不變。

---

## 8. 即時數據更新機制

### 輪詢策略
- **頻率**：每 1 秒呼叫 `fetchAndUpdateGauges()`
- **API**：`GET /api/realtime/latest`
- **快取**：更新後的資料存入 `lastData` 陣列，供頁面切換時立即套用
- **累積量元件另走慢輪詢**：每 30 秒 `fetchAndUpdateAccumulations()` → `POST /api/scadapage/accumulation`（換頁時於 `renderScadaCanvas` 立即補打一次，不等下一輪）；頁面上無累積元件時不發請求
- **迴路指標元件另走慢輪詢**：每 30 秒 `fetchAndUpdateCircuitMetrics()` → `POST /api/scadapage/circuit-metric`（同樣換頁立即補打；以 circuitId+metric 去重、超過 50 筆分批）；頁面上無迴路指標元件時不發請求

### Widget 更新邏輯 (`updateScadaWidgets`)

```
建立四個索引 Map：
    sidMap         → { SID: 數值(float) }（有效數值）
    sidValueMap    → { SID: 原始值 }（含 '--'）
    sidQualityMap  → { SID: 品質(大寫) }
    sidTsMap       → { SID: 點位自身 timestamp }（即時曲線 append 去重用）

依序更新各類 Widget：
    ├─ .scada-gauge        → 重建 SVG（含警報色判定）
    ├─ .scada-rt-value     → 重建 HTML（含警報色判定）
    ├─ .scada-di-point     → 重建 HTML（含 DI 警報脈動）
    ├─ .scada-pump         → 比對 stateKey 決定是否重建 SVG / 僅更新 Gauge
    ├─ .scada-pipe         → 依 bindMode（DI ON/OFF｜類比 vs 閾值｜Bad）比對 pipeKey 決定是否重建
    ├─ .scada-image        → 依 bindMode（DI ON/OFF｜類比 vs 閾值｜Bad）比對 _imgState 切換 GIF/靜止圖
    ├─ .scada-table td[data-sid] → 逐格更新文字與色彩
    └─ .scada-trend[data-sid] → pushTrendPoint() append 新點，末尾 tickTrendCharts() 推進時間窗並重繪
```

> **即時曲線的 append 路徑**：`pushTrendPoint(el.id, sidTsMap[sid], sidValueMap[sid], sidQualityMap[sid])` 以**點位自身 timestamp** 判斷是否前進 —— 沒前進就拒收（Engine 停擺不灌重複假點）；未滿畫點間隔也拒收；品質 BAD 插入 `y=null` 形成斷點。所有圖 append 完才由 `tickTrendCharts()` 統一推進 x 軸、丟棄超窗舊點、每張圖只 `update('none')` 一次。**穩態下不再查 DB**（backfill 只在 widget 掛載時做一次）。詳見 §7.4 (13)。

### 品質處理
- `BAD`：Gauge 顯示「斷線」、RT Value 顯示紅色「斷線」、DI/Pump 顯示紅色「斷線」
- 正常品質：依警報規則決定是否變色

### 警報著色邏輯
- 初始化時載入 `/api/alarm-rules`，建構 `_alarmRuleMap[SID]`
- AI 點位：高限觸發 → `szHighColor` (紅)、低限觸發 → `szLowColor` (橘)
- DI 點位：觸發條件 (ON/OFF) 匹配時 → `szAlarmColor` (紅) + 脈動動畫
- Deadband 計算：`fVal >= (dAlarmHighValue - dDeadbandHigh)` 或 `fVal <= (dAlarmLowValue + dDeadbandLow)`
- **有前置條件的規則（`isPrecondition`）改看實際 active 警報**（2026-10-01）：前端不知道 Engine 閘門 / 延遲計時狀態，自算門檻會「被遮蔽卻仍著紅」。改由 `state.js` 的 `_activeAlarmKeys`（`SID:high|low|di`）判斷，資料來自 `active-alarm-panel.js` 既有 3 秒輪詢廣播的 `scada:active-alarms` 事件（不另開輪詢），故此類規則著色有最多 3 秒延遲。無前置條件的規則維持前端自算（行為不變）
- 本點警報延遲（`AlarmDelaySec`）不影響前端著色：越限當下即上色，Engine 端滿延遲才寫 EventLog / 通知

### 點位右鍵選單（非控制點）

適用：gauge / table 資料格 / realtimeValue（SID 模式）/ diPoint（SID 模式）/ pipe / image / trendChart（已綁點位者）。選單頂端顯示點位名稱，項目：

| 項目 | 顯示條件 | 行為 |
|------|----------|------|
| 警報設定… | Admin / Engineer（`window._isAdmin`） | 開 `#scadaAlarmModal`（`_AlarmRuleForm` partial，與 AlarmSetting 頁同一份表單）：`GET /api/alarm-rules/by-sid/{sid}` 有規則則編輯、無則新增；SID 固定為該點，標頭只顯示點位名稱（不帶 SID）；可刪除；DI ON/OFF 標籤取本頁 widget 設定；前置點位以共用選點器（比照 Designer 綁定點位）挑選。**「恢復通知」Line / Email / 簡訊勾選不在此彈窗顯示**（partial view-data `ArfHideRecoveryNotify`），統一在警報設定頁管理，彈窗存檔沿用規則既有值（新規則用預設 開 / 開 / 開）。存檔 / 刪除後 `_loadAlarmRules()` 刷新著色規則。server 端 POST/DELETE 另有角色檢查（User 打 API 回 403） |
| 歷史趨勢（開新頁） | 有 `/HistoryData` 頁權限（`window._canHistory`，Controller 以 `PermissionService.CanAccessPage` 注入） | `window.open('/HistoryData?sid=…&hours=1')`，歷史頁自動帶入點位、設好時間並查詢 |
| 快速趨勢（1 小時） | 能看本頁即可 | 開浮動小窗（見下） |
| 加入趨勢圖清單 | 全部 | 原行為：寫 `localStorage['SCADA_TREND_PRELOAD']` 佇列 |

**設備類元件（泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機）右鍵**：無控制權限且未綁任何監控點位時，選單沒有項目 → 直接不顯示（2026-10-01 修正：原本會冒出空白小方塊）。

**快速趨勢浮窗**（`quick-trend.js`）：fixed 掛 body（不在畫布 `transform: scale` 內）、標題列可拖曳、一次一窗（再點別的點即替換內容）。時間範圍 15 分 / 1 時（預設）/ 8 時；開窗或切範圍時 `GET /api/history/data` backfill 一次（8 時用 `nInterval=1` 分鐘取樣，避免撞 5000 筆上限），之後由 `updateScadaWidgets` 每秒呼叫 `_quickTrendOnData()` 接續即時值並推進時間窗（同即時曲線：以點位自身 timestamp 去重、品質 BAD 斷線不補值）。有歷史頁權限時標題列另有「開新頁看歷史趨勢」按鈕。**切頁（`selectScadaPage`）自動關窗**。

---

## 9. 控制指令流程

### 共通確認機制
所有控制操作（controlBtn 除外的「自動」模式）都會先彈出 `confirm()` 對話框確認。

### 回饋機制
- 成功：右下角 Toast 提示（綠色，3 秒後消失）
- 失敗：`alert()` 顯示錯誤訊息
- MQTT 未連線：回傳 HTTP 503

### 手動控制值快取
- 初始化時載入 `_aoManualValueMap`
- 每次手動/自動控制成功後立即更新快取
- 右鍵選單中以藍色外框高亮當前模式
- **跨分頁同步**：`/api/realtime/latest` 每秒 polling 回傳的 `isAuto` 欄位會反向更新 `_aoManualValueMap`，所以別人在別處切手動 / 切自動時，本分頁在下一秒內看到狀態變化

### 手動模式 M 角標（控制元件視覺指示）

控制元件（AO / DO / Pump）在「手動模式」時右上角顯示橘色圓形 `M` badge，用以一眼分辨「目前由人工強制控制中」與「自動 / LogicFlow 控制中」。

- **資料來源**：`ManualControlValue` 表的 `IsAuto` 欄位（false = 手動模式 → 顯示 badge）
- **同步路徑**：
  1. **Singleton 快取**：`MqttRealtimeSubscriberService._manualAutoMap`（`RefreshManualAutoMapAsync()` 在啟動時 + 每次 `/api/realtime/latest` 呼叫時刷新）
  2. **API 投影**：`GetLatestData()` 回傳的每筆 `data[]` 元素加 `isAuto: true | false | null`（null = 該點位無 `ManualControlValue` 紀錄）
  3. **前端 toggle**：`updateScadaWidgets` 從 polling 回傳建 `sidIsAutoMap`，掃 `.scada-mode-badge[data-cid]` 統一 toggle 顯示
  4. **Optimistic 即時反饋**：8 個寫入路徑（AO 手動/自動、DO 手動/自動、Pump 啟停/頻率/變頻拖拽/切自動）在 `_aoManualValueMap` 更新後同步呼叫 `_toggleModeBadge(szCid, isAuto)`，操作者不必等 polling
  5. **寫入路徑通知**：`ControlController` 寫完 DB 後呼叫 `_mqttService.UpdateManualAutoFlag()` 同步 Singleton 快取，避免 1 秒內其他分頁讀到舊狀態
- **Pump 雙 badge**：
  - 有頻率 gauge（`bHasFreq=true`）→ 泵本體（`szCidStartStop`）badge 在左上、變頻器（`szCidFreqSet`）badge 在右上
  - 無頻率 gauge → 泵本體 badge 在右上（單一）
- **PLC mode 顏色保留**：Pump 既有 `szSidMode` 來自 PLC 的「自動 / 手動」訊號（運維人員在現場面板切）控制泵身顏色（橘黃 = 手動、藍 = 自動），與 M badge 是兩個獨立語意層，互不覆蓋
- **i18n**：badge 字面 `M` 中英通用不走 i18n；hover tooltip 走 `scadapage.badge.manual_mode_tooltip`（zh-TW「手動模式」/ en「Manual mode」）
- **樣式**：`.scada-mode-badge` 定義於 `wwwroot/css/scadapage.css`（14×14 圓形、`#f59e0b` 橘、`top:-4px; right:-4px`，預設 `display:none`，由 JS 控制）

### 控制行為審計（EventLog）

所有人為觸發的控制動作（按鈕、AO 手動寫值、AO 切自動、DO ON/OFF、DO 切自動、泵浦啟停、頻率設定、泵浦切自動）在 **MQTT 發送成功**（或自動模式 DB 標記成功）後會寫入一筆 `EventLog`：

| 欄位 | 內容 |
|------|------|
| `EventType` | `3`（資訊） |
| `Severity` | `4`（無） — 專為審計性紀錄新增的一級，視覺以淡灰 badge 區分 |
| `SID` | 控制點 CID |
| `MessageKey` | `control.action.*`（10 種，由 `ControlActionType` 決定） |
| `MessageArgs` | JSON `{ username, name, value? }` |
| `Message` | 寫入時 culture 預先格式化的字串（fallback） |

顯示時 EventLog 頁面透過 `AlarmMessageLocalizer` 依使用者當下 culture 翻譯。前端 `js/scadapage/ctx-menus-*.js` 9 處 `/api/control/write` 呼叫各自帶 `actionType` 與 `displayName`（取 `el.dataset.szDisplayName || szTitle`），後端 `ControlController` 解析後委派 `ControlEventLogger.LogAsync` 寫入。

**不會寫入的情境**：
- MQTT 發送失敗（503）
- 自動模式 DB 標記失敗
- `mode=logicflow`（Engine/LogicFlow 自動觸發，非人類操作）
- 前端未帶 `actionType`（相容舊版前端）

**使用者識別**：取 `User.Identity?.Name`（cookie 失效不會走到這 — `[Authorize]` 已擋下）；空值 fallback `"anonymous"` 防呆。

**EventLog 頁面顯示**：因控制動作為一次性操作、無「恢復」「確認」概念，`EventType=3` 的紀錄在 EventLog 頁面的「恢復時間 / 確認狀態 / 確認者」三欄統一顯示 `-`（`eventlog.js` `renderTable` / `exportCSV` 以 `eventType === 3` 為判定基準，未來其他 Info 來源也自動套用）。

**已知限制**：每次控制都會寫一筆，工地高頻操作可能撐大 `EventLog` 表，目前無自動清檔策略。

---

## 10. 權限控制

### 檢視權限 (`canView`)
- 控制頁面樹中哪些頁面可見
- `initScadaViewer()` 中 `filterByPerm()` 遞迴過濾不可見頁面

### 操控權限 (`canControl`)
- 控制 Widget 是否可下達指令
- controlBtn：有權限才綁定 click 事件
- AO/DO/Pump：有權限才綁定 contextmenu 事件
- 無權限時 Widget 僅顯示、不可操作

### Admin 特權
- `window._isAdmin = true` 時略過所有權限檢查

---

## 11. 趨勢圖整合

各 Widget 的右鍵選單皆包含「趨勢圖」選項，點擊後：

1. 查找點位資訊（名稱、單位）
2. 寫入 `localStorage['SCADA_TREND_PRELOAD']`
3. 格式：`{ sids: [{ sid, name, unit }, ...] }`
4. 使用者手動切換到「歷史趨勢」頁面即可查看
5. 重複加入時 Toast 提示「選取的點位已在趨勢圖清單中」

> **與即時曲線 widget 的分工**：本節的「趨勢圖」是把點位丟給 `/History/Trend` 做**查詢式**回顧（任意時間區間、多點疊圖、Excel 匯出）。畫面上要**持續滾動的即時曲線**請用 §7.4 (13) 的 `trendChart` widget —— 兩者資料來源不同（查詢 vs backfill + 即時 append），互不取代。`trendChart` 自己也支援右鍵加入趨勢圖清單。

---

## 12. 畫布等比縮放

`_applyCanvasScale()` 函式：
1. 計算可用空間（父容器寬高 - 24px 邊距）
2. 取得畫布設計尺寸（`nCanvasW × nCanvasH`）
3. 計算縮放比例：`Math.min(可用寬 / 畫布寬, 可用高 / 畫布高)`
4. 以 `transform: translate(-50%,-50%) scale(fScale)` 等比縮放（**刻意不用 CSS `zoom`** —— Chromium 下 zoom 會連系統游標一起放大，F11 全螢幕 `fScale > 1` 時游標進入畫布會忽然變大）
5. 設定 `overflow: hidden` 防止溢出
6. 監聽 `window.resize` 自動重算；側欄／警報面板摺疊動畫另由 `ResizeObserver` 每影格重算
7. 尾端呼叫 `applyTrendChartScale(fScale)` 讓即時曲線的 canvas 抵銷畫布縮放並吸附到整數裝置像素（canvas 是 bitmap，被 CSS 縮放會模糊；見 §7.4 (13)）

---

## 13. UI 佈局

```
┌─────────────────────────────────────────────┐
│  _Layout.cshtml（Navbar）                     │
├──────────┬──────────────────────────────────┤
│ 頁面樹    │  畫布區                            │
│ (160px)  │  ┌──────────────────────────────┐ │
│          │  │  scadaCanvas                  │ │
│ ◆ 首頁   │  │  (等比縮放顯示)                │ │
│  ├ 子頁1  │  │                               │ │
│  └ 子頁2  │  │  [gauge] [rtValue] [table]   │ │
│ ◆ 系統   │  │  [pump]  [diPoint] [ctrlBtn] │ │
│          │  │  [aoPoint] [doPoint] [text]   │ │
│          │  │                               │ │
│          │  └──────────────────────────────┘ │
├──────────┴──────────────────────────────────┤
│  _Layout.cshtml（Footer）                     │
└─────────────────────────────────────────────┘
```

---

## 14. CSS 樣式 (scadapage.css)

### Hover 標籤
所有 Widget 的 `.scada-hover-label` 預設 `display:none`，hover 時 `display:block`。

**標籤文字 fallback（2026-10-01）**：綁點元件（儀表 / 即時值 / 累積值 / DI / 管路）的 hover 文字以 Designer 填的 `szTitle` 優先；**未填標題時改用點位名稱**（即時輪詢 `/api/realtime/latest` 的 `name`，由 `updateScadaWidgets()` 內 `hoverTitle()` 決定），讓每個綁點元件滑過都看得到點名。表格 SID 儲存格則以原生 `title` 屬性顯示點位名稱。名稱在第一次輪詢（≤1 秒；累積值為 30 秒輪詢）後才帶入。AO / DO / 控制鈕原本即以 `szPointName → szTitle → CID` 顯示，不變。

### 動畫

| 動畫名稱 | 效果 | 用途 |
|----------|------|------|
| `di-alarm-pulse` | 1s 週期透明度脈動 | DI 警報觸發 |
| `pump-spin` | 1.5s 連續旋轉 | 水泵運轉中 |

### 控制按鈕 3D 效果 (`.ctrl-btn-exec`)
- 漸層背景 + 多層陰影模擬立體感
- Hover：`translateY(-1px)` 上浮
- Active：`translateY(1px)` + inset 陰影下壓

### AO/DO 浮動按鈕
- `.ao-point-label-btn` / `.do-point-label-btn`
- 相同的 hover 上浮 / active 下壓效果

---

## 15. 右鍵選單系統

### 共通設計
- 固定定位 (`position:fixed`)，`z-index:99999`
- 白色背景 + 陰影 + 圓角
- 智慧定位（`_positionContextMenu`）：超出視窗邊界時自動翻轉
- 點擊外部區域自動關閉
- 開啟新選單前先關閉所有舊選單（`_removeAllContextMenus`）

### 選單類型

| 選單 | 觸發 Widget | 選項 |
|------|------------|------|
| 趨勢圖選單 | gauge, rtValue, diPoint, table cell | 加入趨勢圖清單 |
| AO 選單 | aoPoint | 手動控制（輸入框）、自動控制 |
| DO 選單 | doPoint | ON、OFF、自動控制 |
| Pump 選單 | pump | 啟停（二級子選單）、頻率設定、監控點位勾選 + 趨勢圖 |

---

## 16. 依賴關係

### 後端依賴
| 服務 | 用途 |
|------|------|
| `PermissionService` | 取得使用者角色與頁面權限 |
| `MqttRealtimeSubscriberService` | 發送 MQTT 控制指令 |
| `IDataRepository` | 儲存/載入手動控制值、標記自動控制 |

### 前端依賴
| 來源 | 用途 |
|------|------|
| `Designer` 功能 | 提供已發布設計資料 (`/Designer/Load`) |
| `Realtime` 功能 | 提供即時數據 (`/api/realtime/latest`) |
| `AlarmSetting` 功能 | 提供警報規則 (`/api/alarm-rules`) |
| `History` 功能 | 即時曲線掛載時 backfill 時間窗 (`/api/history/data`，每個 widget 只打一次) |
| Chart.js 4.4.0 + date-fns adapter | 即時曲線繪製（`lib/chartjs/`，與歷史趨勢頁共用同一份） |
| Bootstrap 5 | Alert 元件（Toast） |
| Font Awesome 6 | 圖示（按鈕、選單、樹節點） |
| localStorage | 趨勢圖預載資料 (`SCADA_TREND_PRELOAD`) |
