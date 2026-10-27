# 功能說明書：EMS 能源管理 Hub 頁面

## 1. 功能概述

`/EMS` 是「能源管理」模組的進入點 Hub 頁，集中呈現該模組所有子頁功能。

| 子頁 | 路由 |
|------|------|
| 電力迴路資訊 | `/ElectricityCircuitInfo` |
| 自來水迴路資訊 | `/WaterCircuitInfo` |
| 氣迴路資訊 | `/GasCircuitInfo` |
| 空調水系統迴路 | `/ChilledWaterCircuitInfo` |
| 冰機效率診斷 | `/ChillerEfficiency` |（→ [功能說明書_設備效率診斷.md](功能說明書_設備效率診斷.md)）
| 空壓機效率診斷 | `/CompressorEfficiency` |
| 設備效率設定 | `/EquipmentEfficiencySetting` |（冰機 / 空壓機兩 tab，掃描帶入 + 手動）
| 空調水系統迴路設定 | `/ChilledWaterSystem` |（樹可拖曳搬移，無 Sign、允許多根 → [功能說明書_階層樹拖曳搬移.md](功能說明書_階層樹拖曳搬移.md)）
| 電表/迴路設定 | `/EnergyMeter` |
| 用電報表 | `/EnergyReport` |
| 電費報表 | `/ElectricityCostReport` |
| 冷凍噸報表 | `/RefrigerationTonReport` |
| 能源申報 | `/EnergyDeclaration` |
| 空壓機能源申報 | `/AirCompressorDeclaration` |（→ [功能說明書_空壓機能源申報.md](功能說明書_空壓機能源申報.md)）
| 產氣量表/迴路設定 | `/AirMeterSetting` |（→ [功能說明書_產氣量表.md](功能說明書_產氣量表.md)）
| 電費月結週期 | `/BillingPeriodSetting` |
| 水費月結週期 | `/WaterBillingPeriodSetting` |
| 電費設定 | `/TariffSetting` |
| 國定假日設定 | `/HolidaySetting` |
| 卡片顯示設定 | `/EmsCardSetting` |
| 能源基準 | `/EnergyBaseline` |

點主 navbar 的「能源管理」會直接進入 `/EMS`（原本 dropdown 形式取消）；進入後 navbar 切換為淡綠/白主題、brand 變成「EMS 能源管理」、主選單只剩 EMS 子頁選單（其餘隱藏），且每個選單項目都以 `canAccess()` 依帳號權限個別過濾。

## 2. 路由 & 權限

| 方法 | 路由 | 說明 | 認證 |
|------|------|------|------|
| GET | `/EMS` | EMS Hub 頁 | 需登入 |

權限規則：

- `/EMS` 與全部 EMS 子頁都已加入 `PermissionService.ConfigurablePages`，帳號管理 UI 可逐頁勾選；Admin 角色不走勾選、預設全部可看（`IsAdmin` 直接放行）。
- 額外規則：使用者**未勾 /EMS** 但**勾了任一個子頁**時，`CanAccessPage("/EMS")` 仍回 true（`CanAccessPage` 內對 `/EMS` 的特例，遍歷 `EmsRoutes`）。這保證使用者一定能從主 navbar 走進子頁。
- 主 navbar「能源管理」入口同樣以 `canAccess("/EMS")` 判斷（等價於「勾了 /EMS 或任一子頁」），與上述特例單一來源，不另列子頁清單。
- 所有子頁皆無權限的使用者進 `/EMS` 會被 `EmsController.Index` redirect 回 `/ScadaPage`，避免進空殼頁。

## 3. 視覺設計

由 `wwwroot/css/ems.css` 透過 `body.ems-mode` 限定，與其他頁面藍底 navbar 完全分離：

| 元件 | 樣式 |
|------|------|
| body 背景 | `#f1f8f4` |
| navbar 背景 | `linear-gradient(135deg, #e8f5e9 0%, #ffffff 100%)`，底邊框 `#c8e6c9` |
| navbar brand 字色 | `#2e7d32`（粗體） |
| navbar brand icon | `#43a047`（葉子 icon） |
| nav-link 字色 | `#2e7d32`，hover `#1b5e20` |
| nav-link active | 背景 `#66bb6a` 白字，圓角 6px |
| dropdown hover | 背景 `#e8f5e9` |

色票採 Material Design Green 系列，對白/淡綠底對比足夠。

## 4. Layout 模式切換

共用 `Views/Shared/_Layout.cshtml`，靠**路由表自動偵測** + ViewData fallback。`_Layout` 內：

```csharp
bool isEmsMode = ViewData["EmsMode"] as bool? == true
    || PermissionService.IsEmsRoute(Context.Request.Path.Value);
```

`PermissionService.EmsRoutes` 為「EMS 體系」的權威清單：

```csharp
public static readonly string[] EmsRoutes =
[
    "/EMS",
    "/ChilledWaterSystem",
    "/ElectricityCircuitInfo",
    "/WaterCircuitInfo",
    "/GasCircuitInfo",
    "/EnergyMeter",
    "/EnergyReport",
    "/ElectricityCostReport",
    "/RefrigerationTonReport",
    "/EnergyDeclaration",
    "/AirCompressorDeclaration",
    "/AirMeterSetting",
    "/BillingPeriodSetting",
    "/WaterBillingPeriodSetting",
    "/TariffSetting",
    "/HolidaySetting",
    "/EmsCardSetting",
    "/EnergyBaseline",
];
```

只要 `Context.Request.Path` 命中清單，`_Layout` 就會自動套淡綠主題、改 brand、隱藏其他模組選單。**未來新增 EMS 體系的頁面，只要把路由加進這個陣列即可**，不需要動 Controller 或 Layout。

EmsMode 開關控制五件事：

1. `<body>` 加 `ems-mode` class，吃 `ems.css` 樣式覆蓋（navbar + footer）
2. `<nav>` class 從 `navbar-dark bg-primary` 換成 `navbar-light`
3. brand href 從 `/ScadaPage` 換成 `/EMS`、icon 從 `fa-industry` 換成 `fa-leaf`、字串走 `layout.brand.ems` 而非 `layout.brand`
4. navbar 主選單清單從「ScadaPage/RealTime/控制邏輯/歷史資料/能源管理/系統設定」整個改成 EMS 子頁選單（迴路資訊 dropdown（電力/水/氣）+ 報表/EMS 設定/歷史 dropdown），每項依 `canAccess()` 過濾
5. navbar 右側「語系」左邊加上「← 回 SCADA」連結（`layout.ems.back_scada`，直連 `/ScadaPage`），讓使用者隨時跳回主模組

footer 在 EmsMode 下背景換成 `linear-gradient(135deg, #66bb6a → #43a047)` 配白字；語系切換 / 使用者選單 / 登出 modal / 版本資訊完全共用。

## 5. 主 navbar 對 /EMS 的入口改造

原本 `_Layout.cshtml` 的「能源管理」是一個 dropdown，內含 dropdown-item 直連各子頁。後改為左側單一 nav-link；目前再改為**右側返回連結**，與 EMS 模式的「回 SCADA」完全對稱 — 位置在 navbar 右側「語系」左邊：

```cshtml
else if (!isEmsMode && canAccess("/EMS"))
{
    <li class="nav-item">
        <a class="nav-link" href="/EMS">
            <i class="fas fa-arrow-left me-1"></i>
            @Localizer["layout.scada.back_ems"]
        </a>
    </li>
}
```

（與 `isEmsMode && canAccess("/ScadaPage")` 的「回 SCADA」是同一組 if/else — 兩模式各只顯示自己的返回連結。）

`canAccess("/EMS")` 內含「勾了任一 EMS 子頁即放行」特例，因此新增 EMS 子頁時**不需要**回來改這個判斷式。

新流程：SCADA navbar 右側「← 回 EMS」 → `/EMS` → EMS 模式 navbar 子頁選單 → 子頁。單一動線，使用者不會被 dropdown 與 hub 兩種入口混淆。

## 6. i18n

新增 key 在 `Resources/Views.Shared._Layout.{,en}.resx`：

| Key | zh-TW | en |
|-----|-------|----|
| `layout.brand.ems` | EMS 能源管理 | EMS Energy Management |
| `layout.scada.back_ems` | 回 EMS | Back to EMS |

（原左側選單用的 `layout.menu.energy` 已隨入口右移移除。）

`/EMS` 頁面 ViewLocalizer 走 `Resources/Features.Ems.Views.Index.{,en}.resx`，目前只有：

| Key | zh-TW | en |
|-----|-------|----|
| `ems.title` | 能源管理 | Energy Management |

主選單的「空調水系統/電表/用電報表/冷凍噸」字串走原本就有的 `layout.menu.*`，這次不動。

## 7. 主內容區

Hub 主內容為卡片式 dashboard（`container-fluid` + Bootstrap grid，單一 `.row.g-3` 自動換行）。
六張卡片各自拆成 `Features/Ems/Views/_CardXxx.cshtml` partial，`Index.cshtml` 依 **/EmsCardSetting 生效順序** `@foreach <partial>` 渲染 —
關閉的卡片完全不渲染（DOM 不存在、對應輪詢不發生），全部關閉時顯示「尚未啟用任何卡片」提示（`ems.no_cards`）。
三支驅動 JS（`ems.js` / `ems-hub-energy.js` / `ems-hub-cost.js`）依「其相關卡片是否至少一張可見」條件載入，
JS 內部同時以「根元素是否存在」防呆（雙保險），詳見 §9。

| 卡片 | 欄寬 (xl) | 說明 |
|------|-----------|------|
| 今日即時需量 | col-xl-4 | 迴路下拉 + 即時 kW + 今日最高需量 + 全日趨勢折線圖，60s 自動刷新（`ems.js`）；下拉**預設優先選主要電表**（若主錶本身有設定需量、在選單內），否則退回清單第一筆 |
| 主要電表用電長條圖 | col-xl-8 | 主要電表的日/月/年用電長條圖（`ems-hub-energy.js`），詳見 §7.1 |
| 子迴路用電圓餅圖 | col-xl-4 | 主要電表直接子迴路用電占比（`ems-hub-energy.js`），詳見 §7.2 |
| 去年同期比較表 | col-xl-8 | 主要電表 + 直接子迴路本期 vs 去年同期比較（`ems-hub-energy.js`），詳見 §7.3 |
| 電費狀態 | col-xl-4 | 本期（月結期別）各時段 kWh 與流動電費，依採用方案型態自適應（`ems-hub-cost.js`），詳見 §7.6 |
| 電費去年同期比較 | col-xl-8 | 主要電表 + 直接子迴路本期 vs 去年同期**流動電費（元）**比較，自帶日/月/年切換（`ems-hub-cost-yoy.js`），詳見 §7.7 |
| 冰機效率 / 空壓機效率 | col-xl-4（各一張） | 每台一列（運轉燈 / 名稱 / 即時 kW/RT 或 kW/(m³/min) + 評級）+ 底部本曆月累計平均效率・運轉時數，30s 輪詢（`ems-hub-equipment.js`），詳見 §7.8 |

### 7.1 主要電表用電長條圖卡片

- 「主要電表」= `EnergyCircuit.IsMainMeter = 1`（全系統唯一，於 /EnergyMeter 頁勾選）。前端先打 `GET /EMS/api/main-meter` 拿 id，再走既有 `GET /EMS/api/circuit-energy` 取資料 — 與 /ElectricityCircuitInfo 三張長條圖同一計算核心，數字完全一致
- header 內「日 / 月 / 年」切換鈕（**預設「日」**）+ 對應 pivot 選擇器：日→`<input type="date">`、月→`<input type="month">`、年→年份數字。UI 粒度對應後端 granularity：日→`hour`、月→`day`、年→`month`
- 「日」模式且選的是今天時，每 60 秒自動刷新（比照 /ElectricityCircuitInfo 日卡片）；其他模式/日期不輪詢

### 7.2 子迴路用電圓餅圖卡片

- `GET /EMS/api/main-meter-breakdown?granularity=&pivot=` — 後端自行解析主要電表（前端不帶 circuitId），回傳各**直接子迴路**在所選期間的用電量（已乘子迴路 Sign）；**無子迴路時回主要電表自己一筆**（圓餅顯示自己 100%）
- 期間總量 = 該粒度所有 bucket 加總（重用 `EnergyReportService` 同一計算核心，staleness window / 溢位規則與長條圖一致）
- **負值處理**：期間總量為負的子迴路（Sign=-1 回饋/發電）不進圓餅，改在卡片下方以小字列出「XX：-N kWh（未列入占比）」；全部 ≤ 0 時顯示「期間內無用電資料」
- tooltip 顯示 `名稱: N kWh (P%)`，圖例置底
- 色盤 `PIE_COLORS` 首色維持 EMS 綠（識別度），其餘**跨全色相分佈**（藍/橙/紫/紅/青/黃/棕/粉…）以利分辨相鄰扇形
- 日/月/年切換與時間選擇器**與比較表共用同一組**（長條圖獨立一組）

### 7.3 去年同期比較表卡片

- `GET /EMS/api/main-meter-yoy?granularity=&pivot=` — 首列為主要電表（★ 標示 + 底色），其後各直接子迴路；欄位：本期 kWh、去年同期 kWh、差異 kWh、增減 %
- 去年同期換算：後端重建去年 pivot 字串（年 −1，hour 粒度 2/29 → 2/28）再走同一期別解析 — 月/日粒度會取**去年**的期別設定
- 去年同期為 0 或無資料時增減 % 顯示 `--`；增（▲ 紅）/ 減（▼ 綠）
- 期間與圓餅圖同步，header 右側顯示「本期 vs 去年同期」pivot 字串
- 列數多時 `#yoyTableWrap` 自帶捲軸（`max-height:250px; overflow-y:auto`，表頭 sticky）— 避免撐高整個 EMS 頁產生外層捲軸

### 7.4 未設定主要電表

三張卡片（長條圖 / 圓餅 / 比較表）統一顯示「尚未設定主要電表，請至電表/迴路設定頁勾選」提示並停用切換鈕，不噴錯；既有需量卡片不受影響。

### 7.5 API 一覽（本頁新增）

| 方法 | 路由 | 回應 |
|------|------|------|
| GET | `/EMS/api/main-meter` | `{ hasMainMeter, id, name, hasChildren }` |
| GET | `/EMS/api/main-meter-info` | `{ hasMainMeter, mode, name, voltage, current, power, powerFactor }` — `mode='realtime-by-sid'`（實體主表：`voltage={sid,pointName,unit}`）或 `mode='aggregated'`（虛擬主表：`voltage={unit}`）。點位未設定 `unit` 時前端 `MM_DEFAULT_UNITS` 補預設（V/A/kW，PF 無因次不顯示）供每個數值顯示單位。顯示位數：電壓/電流/功率 1 位小數、功率因數 2 位（實體/虛擬主表一致，`formatMainMeterValue`） |
| GET | `/EMS/api/main-meter-values` | `{ voltage, current, power, powerFactor }` 皆 `number\|null` — 僅虛擬主表用；由 `MainMeterAggregationService.ComputeAsync` 現算 |
| GET | `/EMS/api/main-meter-breakdown?granularity=&pivot=` | `{ hasMainMeter, meterName, items: [{ id, name, kwh }] }` |
| GET | `/EMS/api/main-meter-yoy?granularity=&pivot=` | `{ hasMainMeter, rows: [{ id, name, isMainMeter, currentKwh, lastYearKwh, diffKwh, pctChange }] }` |
| GET | `/EMS/api/electricity-cost?circuitId=` | `EmsElectricityCostDto`（`{ hasPlan, hasCircuit, planId, planType, totalKwh, totalCost, periods[], progressive, flat, surcharge, ... }`）— 電費狀態卡，見 §7.6 |
| GET | `/EMS/api/equipment/realtime?kind=` | `EquipmentRealtimeDto`（每台即時主 / 次指標、評級、輔助指標）— 冰機 / 空壓機診斷頁與卡片，見 §7.8 |
| GET | `/EMS/api/equipment/period?kind=&granularity=&pivot=` | `EquipmentPeriodDto`（tiles / 趨勢 / 負載分佈 / 排名 / 覆蓋率）— **曆制**（`ParseCalendarPivot`），不走期別 |
| GET | `/EMS/api/equipment/card?kind=` | `EquipmentCardDto`（即時列表 + 本曆月累計）— 設備效率卡 |
| GET | `/EMS/api/main-meter-cost-yoy?granularity=&pivot=` | `{ hasMainMeter, isEstimated, rows: [{ id, name, isMainMeter, currentCost, lastYearCost, diffCost, pctChange }] }` — 電費去年同期比較卡，見 §7.7 |

granularity / pivot 協定同 `/EMS/api/circuit-energy`（`hour`→`yyyy-MM-dd`、`day`→`yyyy-MM`、`month`→`yyyy`）。DTO 位於 `Features/Ems/Models/EmsMainMeterDtos.cs`；服務層新增 `EnergyCircuitService.GetMainMeterAsync()` 與 `EnergyReportService.GetTotalKwhAsync()`（bucket 加總公開包裝）。

**pivot 展開套用月結期別**（`EmsController.ParsePivotAsync`，見 [功能說明書_能源管理.md](功能說明書_能源管理.md) §12）：

- `month`（年檢視）：pivot = 年份 → 該年 **1～12 月期別** 12 根柱（期別非自然月時柱標籤顯示完整期間）
- `day`（月檢視）：pivot = YYYY-MM → 該**期別**實際起訖日逐日展開（非 1 號～月底）；日圖加總 = 年檢視該期柱值
- `hour`（日檢視）：pivot = YYYY-MM-DD → 該自然日，不受期別影響

**電 / 水各走各的期別**（2026-08-05 拆分）：pivot 解析拆成兩支 —

| 函式 | 期別來源 | 服務的 API |
|---|---|---|
| `ParsePivotAsync` | 電費期別（`BillingPeriodService`） | `circuit-energy` / `main-meter-breakdown` / `main-meter-yoy` / `main-meter-cost-yoy` |
| `ParseWaterPivotAsync` | **水費期別**（`WaterBillingPeriodService`） | `water-usage` / `water-breakdown` |

> 刻意拆成兩支函式而非加 `bool isWater` 參數：呼叫點若用錯，前者是編譯期選錯函式（改動時看得見），
> 後者是漏傳參數後**靜默走錯期別**，數字錯了也查不出來。

> ⚠️ 已知限制：主要電表綁 SID 且掛有子迴路時，計算核心 `GetLeavesUnderAsync` 會把主錶自身 + 子孫葉子一併加總（/ElectricityCircuitInfo、/EnergyReport 亦同此行為）。目前線上僅單一主錶無子迴路，尚未觸發；未來在主錶下掛分錶時需另案處理「主錶量測 vs 分錶加總」的語意。

### 7.6 電費狀態卡（2026-07 新增）

- `GET /EMS/api/electricity-cost?circuitId=`（未帶 circuitId = 主要電表；未設主要電表退回第一個根節點），60s 輪詢（`ems-hub-cost.js`）
- 「本期」= `BillingPeriodService.GetCurrentPeriodAsync(今天)` 的月結期別；資料源 `ElectricityCostHourly`（Web 背景服務逐時計價，見 [功能說明書_電費設定.md](功能說明書_電費設定.md) §電費計算）
- 依採用方案型態自適應：`tou` 各時段（尖峰/半尖峰/離峰）kWh + 電費明細表（含簡易型超額加價列）；`progressive` 累計 kWh + 落點級距；`flat` 累計 kWh + 當季單價
- 迴路下拉（`/EMS/api/circuit-tree` 組樹縮排）可切子迴路 — kWh/電費依葉子 × EffectiveSign 加總；**級距/加價金額在子迴路為 kWh 占比分攤估算**（顯示「估算」badge + 底部註記）
- 固定註記「不含基本電費」；底部顯示「資料計算至 yyyy-MM-dd HH:00」
- 未選採用方案 → 顯示「尚未選擇電費方案」+ /TariffSetting 連結；未建迴路 → 引導至 /EnergyMeter

### 7.7 電費去年同期比較卡（2026-07 新增）

結構比照 §7.3 去年同期比較表，但比的是**流動電費（元）**而非 kWh；與電費狀態卡（§7.6）互補（後者無跨年度比較）。

- `GET /EMS/api/main-meter-cost-yoy?granularity=&pivot=` — 首列為主要電表（★ 標示 + 底色），其後各直接子迴路；欄位：本期電費(元)、去年同期(元)、差異(元)、增減 %
- 金額 = 各葉子 × EffectiveSign 的 `ElectricityCostHourly` 流動電費加總（薄包 `ElectricityCostService.GetTotalCostAsync` → `GetCostReportAsync(...).totalCost`；子迴路對父的 Sign 由 controller `BuildCostYoyRowAsync` 補乘，語意同 `GetTotalKwhAsync`）
- **「月」依帳單期別切界**（`ParsePivotAsync` day 分支走 `BillingPeriodService.GetPeriodAsync`；`GetCostReportAsync` month 粒度走 `GetPeriodRangesAsync`），數字與**電費報表**對得上
- 去年同期換算同 §7.3（後端重建去年 pivot，hour 粒度 2/29 → 2/28，取去年期別設定）；去年為 0 或無資料時增減 % 顯示 `--`
- **自帶一組日/月/年切換（預設「月」）**，不與能源卡的 `pdGranGroup` 耦合 → 此卡單獨開啟也能運作（決策見 plan）
- progressive（累進）方案子迴路電費為 kWh 占比分攤估算（同電費卡）；任一列估算時卡片底部顯示「金額只含流動電費…累進方案子迴路為估算值」註記
- 金額只含流動電費，不含基本電費

### 7.8 冰機效率 / 空壓機效率卡（2026-10 新增）

- 卡片鍵 `ChillerEfficiency` / `CompressorEfficiency`（`EmsCardRegistry` 第 14、15 筆，可各自隱藏 / 排序）；partial `_Card{Chiller,Compressor}Efficiency.cshtml`，根元素 `[data-eq-card][data-kind]`
- 每類**一張設備列表卡**而非每台一張 — 註冊表是靜態的，每台一張會讓 /EmsCardSetting 跟著設備數變動
- 資料源 `GET /EMS/api/equipment/card?kind=`（30s 輪詢）：即時主指標與評級同診斷頁（`EquipmentRealtimeService`），底部本曆月 1 號～現在的能量加權平均效率與運轉時數（`EquipmentEfficiencyReportService.GetMonthToDateAsync`）
- 標題連到 `/ChillerEfficiency` / `/CompressorEfficiency`；0 台 → 「尚未設定」+ 設定頁連結
- 指標定義、評級與 Engine 聚合 → [功能說明書_設備效率診斷.md](功能說明書_設備效率診斷.md)

## 8. 檔案位置

```
ScadaEngine.Web/
├── Features/Ems/
│   ├── Controllers/EmsController.cs        ← +main-meter / breakdown / yoy / cost-yoy API；Index() 組 EmsIndexViewModel
│   ├── Models/EmsMainMeterDtos.cs          ← 新 API 的回應 DTO（含 EmsMainMeterCostYoyDto / EmsCostYoyRowDto）
│   ├── Models/EmsCardRegistry.cs           ← 卡片註冊表（唯一真相來源）
│   ├── Models/EmsIndexViewModel.cs         ← 可見卡片清單（順序即渲染順序）
│   ├── Views/_Card{MainMeter,Demand,EnergyBar,EnergyPie,Yoy,Cost,CostYoy}.cshtml ← 七張卡 partial
│   └── Views/Index.cshtml                  ← @foreach <partial> 依生效順序渲染
├── Services/
│   ├── EnergyCircuitService.cs             ← +GetMainMeterAsync()
│   ├── EnergyReportService.cs              ← +GetTotalKwhAsync()
│   └── ElectricityCostService.cs          ← +GetTotalCostAsync()（薄包 GetCostReportAsync.totalCost）
├── wwwroot/js/ems-hub-energy.js            ← 三張新卡片邏輯（IIFE）
├── wwwroot/js/ems-hub-cost.js              ← 電費狀態卡（IIFE；DTO：Models/EmsElectricityCostDtos.cs）
├── wwwroot/js/ems-hub-cost-yoy.js          ← 電費去年同期比較卡（IIFE，自帶期間切換）
├── wwwroot/js/ems-hub-equipment.js         ← 冰機 / 空壓機效率卡（IIFE；DTO：Models/EmsEquipmentDtos.cs）
├── Resources/
│   ├── Features.Ems.Views.Index.resx
│   └── Features.Ems.Views.Index.en.resx
├── Services/PermissionService.cs        ← +/EMS、+_aEmsChildren 規則
├── Views/Shared/_Layout.cshtml          ← +EmsMode 開關、能源管理 dropdown → 直連
├── Resources/
│   ├── Views.Shared._Layout.resx        ← +layout.brand.ems
│   └── Views.Shared._Layout.en.resx     ← +layout.brand.ems
└── wwwroot/css/ems.css                  ← 淡綠/白主題覆蓋
```

## 9. 卡片顯示設定（/EmsCardSetting，2026-07 新增）

EMS 導覽列「能管設定」→「設定」子選單中的「卡片顯示設定」。頁面為**版面預覽**互動：

- **版面預覽區**：卡片依實際 `col-*` 欄寬以縮圖呈現（綠 header + icon + 卡名），**拖曳（HTML5 drag & drop）調整順序**、header 右上 **× 隱藏**
- **已隱藏的卡片區**：被隱藏的卡片列成 pill，點「**加回**」回到預覽最後
- 按「儲存」整份寫入（預覽 DOM 順序 = 顯示順序），重新整理 /EMS 即生效

### 9.1 架構 — 註冊表 + DB 覆寫

- **`EmsCardRegistry`（`Features/Ems/Models/EmsCardRegistry.cs`）是卡片定義的唯一真相來源**：
  每張卡 `CardKey / PartialViewName / NameResxKey / GridColumnCss / IconCss / 預設順序`
  （GridColumnCss / IconCss 供設定頁預覽用，**須與 partial 的外框 col-* 與 header icon 一致**）。
  設定頁與 /EMS 渲染都吃「註冊表 merge DB」後的生效清單。
- **DB `EmsCardSetting` 表只存覆寫**（每卡一列：`CardKey PK / IsVisible / SortOrder / UpdatedAt`；
  `DatabaseSchema.json` 定義，啟動安全網自建）。merge 規則：
  - DB 有列 → 用 DB 的 IsVisible / SortOrder
  - DB 無列（新卡片）→ 預設顯示、排在所有 DB 列之後（依註冊表預設順序）
  - DB 有但註冊表沒有（卡片被移除）→ 忽略該列，不渲染、不列在設定頁
  - 表空 = 全開、預設順序
- 儲存為交易內 DELETE 全表 → 依前端陣列順序 INSERT（SortOrder 正規化 1..N），只收註冊表內合法 CardKey。
- 讀取不快取 — `/EMS` 每次請求即時讀 DB（小表全掃成本可忽略，存檔後重整立即生效）。

### 9.2 API

| 方法 | 路由 | 說明 |
|------|------|------|
| GET | `/EmsCardSetting/api/cards` | `{ cards: [{ cardKey, nameKey, gridCss, icon, isVisible }] }` — 順序即生效順序；卡名由前端以 `nameKey` 走 `window.i18n` 翻譯，`gridCss`/`icon` 供版面預覽 |
| POST | `/EmsCardSetting/api/cards` | `{ cards: [{ cardKey, isVisible }] }` — 陣列順序 = 顯示順序 |

### 9.3 耦合防呆

- 圓餅卡內的 `pdGranGroup` 同時驅動圓餅＋去年同期兩卡：只關圓餅卡時 `setupGranGroup` 回 null、
  `pivotOf(null, gran)` 回今日/當月/今年預設 — 比較卡以預設粒度照常運作。
- 長條/圓餅/比較三卡全關時 `ems-hub-energy.js` 直接 return，不打 `/EMS/api/main-meter`。
- `ems.js` 拆兩段：主表卡（`mainMeterCardWrap` 不存在跳過）、需量卡（`demandCircuitSelect` 不存在跳過綁定與輪詢）；
  `ems-hub-cost.js` 以 `costCircuitSelect` 防呆。

### 9.4 新增 EMS 首頁卡片 SOP

**不需要改 /EmsCardSetting 設定頁本身**，新卡片會自動列出（預設顯示、排最後）：

1. 卡片 HTML 寫成 `Features/Ems/Views/_CardXxx.cshtml` partial（含自己的 `col-*` 外框 div；
   需要 i18n 的字串走 partial 自己的 `Resources/Features.Ems.Views._CardXxx.{,en}.resx`）
2. `EmsCardRegistry.Cards` 加一筆（CardKey / partial 名 / NameResxKey / GridColumnCss / IconCss / 預設順序；
   col-* 與 icon 要跟 partial 一致，設定頁預覽才對得上）
3. `Resources/Features.EmsCardSetting.Views.Index.{,en}.resx` 加 `emscard.name.{key}`（設定頁顯示的卡名）
4. 該卡驅動 JS 的 init **必須以根元素存在與否防呆**（卡片被關閉時 DOM 不渲染）；
   若新增獨立 JS，`Features/Ems/Views/Index.cshtml` 的 `@section Scripts` 加對應可見性條件

### 9.5 權限與 i18n

- `/EmsCardSetting` 已加入 `PermissionService.ConfigurablePages`（帳號管理可單獨勾選）與 `EmsRoutes`（自動套 EMS 綠主題）；直連由全域 `PageAccessFilter` 擋。
- 設定頁 resx：`Features.EmsCardSetting.Views.Index.{,en}.resx`（頁面字串 + `emscard.name.*` 卡名）；選單鍵 `layout.menu.ems_card_setting`。
