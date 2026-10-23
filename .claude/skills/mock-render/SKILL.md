---
name: mock-render
description: 用模擬（假）數字離線渲染本專案 Web 任一頁面並截圖，不登入站台、不碰 DB（線上主機唯讀安全）。觸發詞：「帶數字渲染 XX 頁」「模擬截圖」「demo 畫面」「假資料渲染」。適用：簡報/展示圖、版面確認。不適用：驗證真實資料或後端邏輯（那要走真站台）。
---

# 頁面模擬渲染截圖（通用）

## 原理（為什麼不走真站台）

登入無後門帳號（SEC-08 已移除 admin/admin）、本機是線上生產主機 → 不登入、不碰 DB。
改用**真實前端資產**離線組頁：View/partial 的 HTML + `wwwroot/` 的真實 CSS/JS + Chart.js，
在 `<head>` 攔截 `fetch` 餵假 JSON、覆寫 `Date` 固定時鐘，頁面 JS 照常執行 → Playwright 以 `file://` 開啟截圖。
畫面 = 真版面 + 指定數字，且換機器可用（`{{WWWROOT}}` 佔位符由 render script 依 repo 位置代入）。

## 結構

```
assets/shell-ems.html      ← EMS 模式外殼（淡綠導覽列 + ems.css + 時鐘/fetch-mock 骨架）
assets/shell-scada.html    ← SCADA 模式外殼（深藍導覽列，無 ems.css）
recipes/<page>.html        ← 每頁一份完成品（外殼 + 該頁 HTML + MOCK + 頁面 JS），做過就留著重用
scripts/render_page.py     ← 代入 {{WWWROOT}} → Playwright 截圖；不帶 --html 會列出現有 recipes
```

## 流程

1. **先查 `recipes/` 有沒有現成的**（有 → 複製到 scratchpad 改 MOCK 數字即可，跳到步驟 4）
2. 沒有 → 做新 recipe：
   - 判斷頁面歸屬（EMS 綠 / SCADA 藍，看 `PermissionService.EmsRoutes` 或路由），複製對應 shell 到 scratchpad
   - 讀該頁三樣東西：`Features/<X>/Views/*.cshtml`（HTML，Localizer 字串查 `Resources/*.resx` 代入 zh-TW）、
     `wwwroot/js/<x>.js`（吃哪些 API、值怎麼格式化）、`Features/<X>/Controllers/<X>Controller.cs`（API 回傳形狀）
   - HTML 貼進 shell 的 PAGE_CONTENT、JS `<script>` 掛在 PAGE_SCRIPTS（順序照該頁 `@section Scripts`）、
     頁面 css `<link>` 加進 head、MOCK 物件照 Controller 回傳形狀填假資料
3. 執行：`python .claude/skills/mock-render/scripts/render_page.py --html <harness> --out <png>`
   - `--hash <情境>`：附在 URL 後的 `#片段`，同一份 recipe 讀 `location.hash` 切情境（開 Modal、右鍵選單、切分頁）
   - `--selector "<css>"`：只截該元素（例 `#activeAlarmPanel`、`#ruleModal .modal-content`），預設整頁
   - macOS 沒有 pip 版 playwright 時：`python3 -m venv <scratchpad>/venv && <scratchpad>/venv/bin/pip install playwright`，腳本會自動改用系統 Google Chrome（不必 `playwright install`）
   - **一律假設成果呈現在 1920×1080 螢幕視窗**：渲染帶 `--width 1920 --height 1080`，讓版面依 1920 寬排版（頁面比 1080 高時 full_page 仍完整截出，不會裁切）
4. 用 Read 檢視 png 確認（數字、版面、圖表高度）再交付
5. 新 recipe 驗證 OK 後存回 `recipes/<page>.html`，並在下方登錄表加一列

## 已知眉角（踩過的坑）

- **Chart.js 動畫必關**（shell 已含 `Chart.defaults.animation = false`，勿移除），否則截到半高柱
- **假數字要自洽**：占比合計 = 總表 = 長條加總、子項差異加總 = 總表差異 —— 使用者會驗算
- **時鐘**：shell 的「模擬時鐘」區塊改 `new RealDate(...)` 一行即可；若頁面 JS 依「現在」切資料
  （如需量曲線只畫到現在），mock 資料的終點要跟著改
- 截圖 `OSError: Invalid argument` → 舊 png 被預覽程式鎖住，換輸出檔名
- Razor precompiled 與此法無關 —— 這裡不跑 Razor，`.cshtml` 只是拿 HTML 骨架的來源
- 頁面若用 `window.i18n.t(...)`（i18n 範圍頁）：shell 沒載 `i18n.js`，最簡做法是在 PAGE_SCRIPTS 最前面
  塞 `window.i18n = { t: function (k, a) { return ({...字典...})[k] || k; } }` 只填該頁用到的 key
- ~~SignalR / MQTT 即時頁要另外 stub~~ 已查證：ScadaPage 全走 fetch 輪詢（`/Designer/Load` + `/api/realtime/latest` 1 秒 + `/api/scadapage/accumulation`、`/api/scadapage/circuit-metric` 30 秒 + `/Realtime/ActiveAlarms` 3 秒），fetch-mock 即可，無 SignalR
- **CSS transition 在 headless 凍結**：`document.timeline.currentTime` 停在 0，transition 永遠卡在起始值（症狀：警報面板 collapse 的 `max-height` 280→0 收不起來，inline style 蓋了也沒用，因為 computed 回報的是動畫中的值）。shell 已無解法，**recipe 的 `<style>` 要加** `*, *::before, *::after { transition: none !important; animation: none !important; }`
- **`szBgDataUrl` 餵 SVG data URL**：`renderScadaCanvas` 用不帶引號的 `url(...)` 塞 CSS，`encodeURIComponent` 不編碼 `( ) '`，SVG 內含 `rgba(...)`/`url(#id)` 會截斷 CSS → encode 後要再 `.replace(/\(/g,'%28').replace(/\)/g,'%29').replace(/'/g,'%27')`

## 手冊截圖（`ScadaEngine.Web/Manual/{book}/images/`）

- 用途：補線上使用手冊的 `IMG-TODO`，輸出 `--out ScadaEngine.Web/Manual/<book>/images/<id>.png --width 1920 --height 1080`，再把該行 IMG-TODO 換成 `![說明](images/<id>.png)`
- 假資料用**通用設備名**（冰水泵 P-01、空調箱 AHU-01、電表 M-01、operator01 / 王小明、example.com），不寫具體建物 / 迴路 / 客戶名稱
- ⚠️ 標「已過時」的舊 recipe 缺現行頁面新增的 JS / HTML（跑不起來或欄位舊）；手冊用的 `manual-*` 是現行版本，要做新圖優先從它們複製
- SCADA / EMS 手冊以 Admin 視角：recipe 內移除 navbar「工程師模式」那個 `li`（shell 保留，工程師手冊要用）
- Engineer 手冊：保留「工程師模式」、使用者顯示改 engineer
- 渲染器 log 有 `[pageerror]` 就是頁面 JS 拋例外（含語法錯誤），先修再截，別在 recipe 內嵌修補副本繞過
- 未確認警報鈴鐺：shell 的 `#alarmSoundNav` 加 `as-has-pending`、`.as-count` 去 `d-none` 填數字

## Recipes 登錄

| recipe | 頁面 | 內容 | 備註 |
|---|---|---|---|
| `recipes/ems-index.html` | /EMS 首頁（EMS 綠） | 電力五卡：主要電表資訊 / 今日即時需量 / 用電長條 / 子迴路圓餅 / 去年同期比較 | API 形狀對照表在檔頭註解；水/氣/電費卡未含，要加時讀 `EmsCardRegistry.cs` + `EmsController.cs` 對應 action，照現有模式補 |
| `recipes/ems-index-fivebuildings.html` | /EMS 首頁（EMS 綠） | 五棟辦公大樓情境（運航/24Hr/一般/第一訓練/備勤），台電總表=五棟加總；今日 15:42 快照 | 由 ems-index 衍生換數字：今日長條逐時（00~15 時，16 時起 0）加總 = 圓餅五棟加總 = YoY 本期總表 = 10,691.0；YoY 子項差異合計 = 總表差異 −121.6；demand-trend 尖峰 995.0(11:00)/現值 812.4 對齊長條與 main-meter-values（11.4kV/42.86A/812.4kW/PF0.96）|
| `recipes/scadapage-power-overview.html` ⚠️ 已過時 | /ScadaPage 即時監控（SCADA 藍） | 「電力迴路總攬」圖面：SVG 單線圖背景（父台電電表 + 子生產大樓A/B、辦公大樓）+ realtimeValue kW + cmetric day_kwh，含頁面樹側欄與警報面板（收合） | 圖面全在 `/Designer/Load` mock 內組（SVG 以 JS 函數產生、widget state JSON.stringify）；改數字動 `/api/realtime/latest` 與 `/api/scadapage/circuit-metric` 兩個 mock，注意子迴路加總 = 父迴路 |
| `recipes/scadapage-demand-metric.html` ⚠️ 已過時 | /ScadaPage 即時監控（SCADA 藍） | 「電力迴路總攬」+ 第五指標即時需量 demand_kw：每卡三欄位（即時功率 SID / day_kwh / 下半 demand_kw），四顆需量元件展示 ok / stale（灰值）/ no_data（--）三態 | 由 scadapage-power-overview 衍生（卡片加高 230、canvas 700、`--height 1100`）；需量 mock 自洽 298.6+275.0=573.6=父（no_data 卡不計入）；i18n 字典多 `scadapage.cmetric.demand_kw` |
| `recipes/logicflow-hvac-optimize.html` ⚠️ 已過時 | /LogicFlow 流程圖控制（SCADA 藍） | 空調最佳化邏輯：3 溫度 + 2 用電讀取點位 → 空調最佳化演算法（虛構，雙輸出 freq1/freq2）→ 排程 A 接點×2（ON）→ 冰水泵 46.5 Hz / 冷卻水 42 Hz 寫入點位（自動） | 用真實 logicflow/*.js 跑求值：mock `/LogicFlow/api/{tree,diagram/2,algorithms,algo-eval/hvac_optimize,timer-state/2}` + `/api/realtime/by-sids`、`/api/control/manual-values`、`/api/schedules`；window.i18n 為內嵌 stub 字典；尾端 autoSelect script 自動展開並選取邏輯；輸出節點 x 勿超過 ~1000（畫布 overflow hidden 會裁掉更右邊）；排程 ON/OFF 由 mock 時鐘（週三 15:42）對排程 08:00-18:00 平日決定 |
| `recipes/logicflow-fuzzy-pump-default.html` | /LogicFlow 流程圖控制（SCADA 藍） | 壓差/溫差 Fuzzy 水泵頻率兩顆 variadic 演算法「從調色盤拉出的預設方塊」（含 inputs_default 自動常數 + 接線），驗節點外觀/寬度用 | diagram mock 為空，尾端 script 等 `S.diagramVersion===1`（診斷圖 async 載入完成，否則節點會被載入結果覆寫）後走真實 `S.addNodeToCanvas('algorithm', ...)` 建立；algorithms mock 為 variadic 完整形狀（inputsFixed/inputsRepeat/outputsRepeat/`inputsAutoRepeat:{key,port,field}`/inputDefaults）；截圖建議 `--height 1500`（預設節點高近 940px） |
| `recipes/energyreport-month-yoy.html` ⚠️ 已過時 | /EnergyReport 用電報表（EMS 綠） | 月粒度 + 去年同期比較：2026/1~2026/8 台電總表用電長條（本期藍 + 去年橘並列）+ 右側明細表五欄（時段/本期/去年/差異/增減%，漲紅▲跌綠▼）+ 合計列 | 走真實 `energyreport.js`：mock `/EnergyReport/api/{circuits,query}` + `/BillingPeriodSetting/api/{current,range}`；`query` 回傳 `EnergyReportResult`（buckets 帶 dKwh/dLastYearKwh/dDiffKwh/dPctChange，isYoy=true）；window.i18n 為內嵌 stub 字典；尾端 driver 設月粒度＋2026-01~2026-08＋選迴路 id=1＋觸發 `_er.query()`；數字自洽（合計=各月加總、差異合計=本期−去年）；`--height 900` |
| `recipes/electricity-circuitinfo-fivebuildings.html` | /ElectricityCircuitInfo 電力迴路用電資訊（EMS 綠）| 左迴路樹（台電總表 + 五棟辦公大樓）+ 右三卡：年(各月 kWh)/月(各日 kWh)/日(各時 kWh)；預設選台電總表 | 走真實 `ems-circuit.js`（四能源別共用，data-kind=electricity）：mock `/EMS/api/circuit-tree` + `/EMS/api/circuit-energy`（**handler 收 url、依 granularity 分流** month/day/hour）；label 格式 month→yyyy-MM、day→MM/dd、hour→HH:00（同 `EnergyReportService.BuildLabels`）；三卡各自獨立 pivot，driver 在 DOMContentLoaded 同步把 pivot 設成完整期間（年 2025 全 12 月 / 月 2026-08 全 31 日 / 日 2026-09-08 全 24 時），趕在 tree 選取微任務前生效；頁高由 js `fitPageHeight()` 依視窗算，1920×1080 三卡剛好一屏 |
| `recipes/energyreport-fivebuildings.html` ⚠️ 已過時 | /EnergyReport 用電報表（EMS 綠） | 月粒度 2026/1~2026/8 + 子迴路明細堆疊：台電總表 = 五棟辦公大樓（運航/24Hr/第一訓練/備勤/一般）加總，堆疊長條 + 右側月合計明細表（合計 4,091,000 kWh）| 由 energyreport-month-yoy 衍生：關 YoY、`query` 回 `children:[{szName,dKwhPerBucket}]` 五棟；driver 查完 `Promise.resolve(query()).then(...toggleBreakdown())` 切明細堆疊；每月各棟加總 = 該月 dKwh、合計 = 各月加總；i18n stub 多 `energyreport.button.show_total/show_breakdown` |
| `recipes/refrigerationtonreport-month.html` | /RefrigerationTonReport 冷凍噸報表（EMS 綠） | 月粒度 2025-01~2025-12 單迴路（冷凍主機群）12 月 RT·h 藍柱（季節型、YoY 穩定的冷量需求）+ 右側明細表二欄（時段/冷量 RT·h）+ 合計 575,000 RT·h | 走真實 `refrigerationtonreport.js`：mock `/RefrigerationTonReport/api/{circuits,query}`；`query` 回傳 `RefrigerationTonReportResult`（buckets 帶 dRtHour、children:[] 保持總計模式不顯明細鈕）；window.i18n stub；driver 設月粒度＋2025-01~2025-12＋選 circuit id=1＋`_rt.query()`；合計=各月加總；`--height 900` |
| `recipes/energydeclaration-month.html` | /EnergyDeclaration 能源申報（EMS 綠） | 年度 2025 十二曆月：kWh 藍柱（左軸）+ RT·h 綠線（右軸，與冷凍噸報表同一基底）+ 明細表四欄（月份/kWh/RT·h/效率 kWh·RTh⁻¹）+ 三合計，效率逐月 1.020→0.910 下降＝效率提升 | 走真實 `energydeclaration.js`：mock `/EnergyDeclaration/api/{reports,circuits,watercircuits,query}`（3 GET 都要回，config 表靠 id 對名）；`query` 回傳 `EnergyDeclarationResult`（buckets 帶 dKwh/dRtHour/dKwhPerRtHour/isKwhStale）；kWh=RT·h×效率比（皆整數）、bucket 效率=kWh/RT·h、總效率=總kWh/總RT·h 全自洽；window.i18n stub；需 bootstrap bundle（Modal）；driver 設 edYear=2025＋選 report id=1＋`_ed.query()`；`--height 1050` |
| `recipes/energybaseline-model-flightops.html` | /EnergyBaseline 能源基準 ISO50001 · Tab1 基線建模（EMS 綠） | 航空辦公園區五棟大樓各一模型（左清單），運航大樓選中：Y=總用電、X=CDD26+值勤人數+航班架次（月粒度、2023~24 共 24 樣本）；回歸結果 5 統計卡（R²0.9121）+ 係數表 p-value + 基線公式 + 24 點實際vs預測散布圖 | 走真實 `energy-baseline.js`：mock `/EnergyBaseline/api/{circuits,points,devices,models,models/1}` + POST `{models,models/1/run}`。**必含 `#enbPointPickerModal` 否則 `new bootstrap.Modal(null)` 拋錯令 loadAll 不跑**；X 點位設 Calculated（szGroupName 供顯示）；散布圖靠 run flow：driver `selectModel(1)`→400ms→`saveModel(true)`，且 **mock 把第 2 次以後的 `GET models/1` 延遲 12s** 否則 runRegression 尾端的 selectModel 會用 renderStoredResult(scatter=[]) 蓋掉散布圖；window.i18n stub 填 enb.* key；`--height 1180` |
| `recipes/energybaseline-enpi-flightops.html` | /EnergyBaseline 能源基準 ISO50001 · Tab2 EnPI/節能量報告（EMS 綠） | 運航大樓凍結基線、報告期 2025 1~8 月：4 摘要卡（實際 1,445,200／預測 1,511,300／節能量 66,100／整體 EnPI 0.9563）+ 雙軸圖（實際綠柱/預測灰柱/累計節能量橘線）+ 8 列明細 | 由 model recipe 衍生：模型全設 frozen（EnPI 下拉只列凍結）；mock POST `/EnergyBaseline/api/enpi/query` 回 `EnPIReportResult`（buckets 帶 dActual/dPredicted/dSavings/dCumulativeSavings/dEnpi、szTargetUnit='kWh' → 明細 1 位小數）；節能量=預測−實際、累計、EnPI=實際/預測、合計全 JS 計算自洽；driver `new bootstrap.Tab([data-bs-target="#tabEnpi"]).show()`＋等下拉 option '1'＋設期間＋`queryEnpi()`；`--height 1080` |
| `recipes/energybaseline-seu-fivebuildings.html` | /EnergyBaseline 能源基準 ISO50001 · Tab3 SEU 鑑別（EMS 綠） | 五棟辦公大樓年用電帕累托（合計 6,280,000 kWh）：3 深綠柱（SEU）+2 淺綠柱 + 累計占比橘線 + 排名表（運航41.40%→24Hr66.24%→備勤81.85% 跨 80% 門檻標 SEU，一般/第一訓練不標） | 由 model recipe 衍生：mock POST `/EnergyBaseline/api/seu` 回 `SeuAnalysisResult`（hasSource/sourceName/totalKwh/items[id,name,kwh,pct,cumPct,isSeu]）；pct/cumPct/isSeu 由 JS 從 kwh 陣列算（isSeu = 前一項累計 < 門檻）；driver 切 #tabSeu＋設期間＋`querySeu()`；`--height 1080` |
| `recipes/chillerefficiency-month.html` | /ChillerEfficiency 冰機效率診斷（EMS 綠） | 四台不同機型、門檻依 ASHRAE 90.1-2022 Table 6.8.1-3（優 ≤ FL、可 ≤ FL×1.15）：CH-1 水冷離心 500RT 變頻 0.585/0.673 優、CH-2 水冷離心 500RT 定頻 0.560/0.644 結垢劣化（期間可、即時差）、CH-3 水冷螺旋 350RT 0.610/0.702 差、CH-4 氣冷螺旋 150RT 1.188/1.366 可 + 月粒度 2026-09 逐日 kW/RT 趨勢+ 負載率分佈 + 排名評級 | 共用 `_EquipmentDiagnosis` partial + 真實 `equipment-threshold.js` + `ems-equipment.js`（DEV 每台帶 `basis` → 即時 / 排名 `thresholdBasis` 依據字串）；mock `/EMS/api/equipment/{realtime,period}`。**period 由 recipe 內 `buildPeriod()` 以決定性亂數從逐日 run/load/eff 推出**，序列 / 全場 / tiles / 負載 bin / 排名同源自洽；改設備或情境只動 `DEV` 物件。driver 在 DOMContentLoaded 設 pivotMonth 並點「月」鈕（此時 `_hasDevices` 仍 null，等 realtime 回來才以 day 粒度查）；`--height 1080` |
| `recipes/compressorefficiency-month.html` | /CompressorEfficiency 空壓機效率診斷（EMS 綠） | 即時四卡（AC-1 優 6.21 / AC-2 可 / AC-3 差 10.35 卸載空轉 / AC-4 停機）+ 2026-09 逐日 kW/(m³/min) 趨勢（AC-1 9/14–15 保養由 AC-4 備機頂上）+ 加載時間比 / 平均排氣壓力 tiles + 排名 | 與冰機 recipe 同檔結構，只差 `var KIND`；兩檔由同一產生腳本輸出，改其一記得同步另一 |
| `recipes/compressorefficiency-diagnosis.html` | /CompressorEfficiency 空壓機效率診斷 ·「系統診斷」tab（EMS 綠，Engineer 視角） | 三台空壓機 7 天（AC-1 定頻基載 + 非生產洩漏循環 / AC-2 VSD 調節 / AC-3 只卸載不加載）：P / L / S1 / S2 / S4 / S5 / S6 觸發、E1 待設定、三個重疊旗標、實測基線表 | `DIAG_LATEST.snapshot.result` 是**真實 Common `CompressorDiagnosisEngine` 對合成分鐘序列的輸出**（scratch console harness 跑出 JSON 貼入），非手填；`DIAG_SETTING` 為設定 tab mock（參數定義取自 `DiagnosisParameters.cs`）。i18n stub = `EquipmentEfficiency.resx` 全字典；driver 在 DOMContentLoaded 後 50ms 點 `#eqdTabs [data-tab="diagnosis"]`，改成 `"setting"` 即渲染診斷設定 tab；`--height 1080` |
| `recipes/contractcapacity-optimize.html` | /ContractCapacity 契約容量最佳化（EMS 綠，設計預覽；正式頁已實作，金額由 `ContractCapacityCalculatorTests` 鎖住） | 高壓二段式台電總表回測 2025-10~2026-09：現行 1,500 kW / 保守零超約 1,400 kW（年省 228,630）/ 最低成本 1,236 kW（年省 394,562、超約 4 月）+ 資料品質卡 + 成本曲線（基本費綠 + 超約附加費紅堆疊、三條垂直標線）+ 逐月最高需量 vs 三條契約線 + 逐月明細三方案對照 | 無頁面 JS/API，全部內嵌計算：只動 `MONTHS`（逐月最高需量 / 發生時刻 / 完整率 / 夏月日數占比）與 `CUR`，保守值 / 最低成本 / 平底區間 / 合計皆由公式推出自洽；單價取自 `tariff-taipower-defaults.json` hv_tou2.regular_kw；超約 10% 內 2 倍、超出部分 3 倍；`--height 1080` |
| `recipes/manual-scada-login.html` | /Login 登入頁（手冊 quick-start-01） | `_LoginLayout` + `Login/Index.cshtml` 結構，帳號欄填 operator01 | 不走 shell、無 API；console 一個 ERR_FILE_NOT_FOUND 不影響畫面 |
| `recipes/manual-scada-layout.html` | 導覽列 / 全站搜尋 / 403（quick-start-02/03、faq-01） | `--hash nav`（預設，紅框標左選單 + 右工具列，鈴鐺 3）/ `search`（輸入「報表」的結果）/ `denied` | mock `/api/globalsearch/index`（形狀照 `GlobalSearchService`）；真實 `global-search.js`，先清 localStorage `scadaGlobalSearchFreq` 免頻率影響排序；高度 nav 150 / search 900 / denied 760 |
| `recipes/manual-scada-scada-page.html` | /ScadaPage 即時監控（scada-page-01~04） | 通用冰水系統圖（CT-01、CWP-01、CH-01、CHP-01/02 + 5 數值），頁面樹 7 頁、警報面板 3 筆 | `--hash` 不帶＝整頁 / `ctx-point` / `ctx-pump`（子選單展開）/ `panel` + `--selector "#activeAlarmPanel"`；mock `/Designer/Load`、`/api/realtime/latest`、`/Realtime/ActiveAlarms`、`/api/alarm-rules`、`/api/control/manual-values`；冰機「設定溫度」讀 manual-values；右鍵選單掛 body 末端，子選單要對第一列送 mouseenter；**Index.cshtml 新增的 widget-trend.js 等 JS 要照 `@section Scripts` 全載**（舊兩份 scadapage-* recipe 就是缺它而初始化失敗） |
| `recipes/manual-scada-realtime.html` | /RealTime 即時數據（realtime-01/02） | 預設 MCC-01 全 GOOD；`--hash ahu` 選 AHU-02 呈現正常 / 過舊黃 / STALE / BAD 紅 / NO_DATA 灰 | `_realtimeInitData` 與 mock `/api/realtime/latest` 同由 `POINTS` 產生；列底色照 `GetCssRowClass` |
| `recipes/manual-scada-history.html` | /HistoryData 趨勢圖（history-01/02） | 預設 3 點多點查詢；`--hash single` 單點 + 明細展開 | head 內 `history.replaceState('?sid=…&hours=24')` 走頁面深連結自動查詢；driver 的 DOMContentLoaded 要掛在 history.js 之前；mock `/api/history/data` 依 query 產序列（統計只算 Good）；`toLocaleTimeString` 固定 24h（headless 預設 en-US） |
| `recipes/manual-scada-event-log.html` | /EventLog 事件記錄（event-log-01/02） | 13 筆：發生中 / 已恢復警報、控制操作、通知摘要 | mock `/api/eventlog/query`；driver 按 24h 再 `_eventLog.queryEvents()`；02 截 `.query-card.position-relative` 並對 `.msg-tooltip-wrap` mouseenter |
| `recipes/manual-scada-alarm-setting.html` | /AlarmSetting 警報設定（alarm-setting-01~03） | 8 條規則（上下限 / DI / 前置條件 / 延遲 / 停用）；`--hash add-modal`（截 `#ruleModal .modal-content`，高 1500）/ `tab-email` | 無 API，資料在 `window._alarmInitData`；**i18n stub 的 `ready` 要非同步**，否則前置條件欄顯示 SID |
| `recipes/manual-scada-schedule.html` / `-schedule-modal.html` | /ScheduleSetting 排程（schedule-01/02） | 5 筆排程；modal 版 `showAddModal()` 並填範例 | 資料走 `window._scheduleInitData`，「目前狀態」依 mock 時鐘算 |
| `recipes/manual-scada-condition-ctrl.html` / `-condition-ctrl-pair.html` | /ConditionCtrl 條件控制（condition-ctrl-01/02） | 5 條規則；pair 版為一組開 / 關相反規則 | 資料走 `window._condCtrlData`；pair 版 `--width 1280 --selector '.container-fluid.mt-4 > .card:last-child'` |
| `recipes/manual-scada-weather.html` / `-weather-status.html` | /WeatherSetting 氣象（weather-01/02） | 四卡、測站臺北、測試連線成功；status 版為「觀測資料過舊」失敗 | mock `/WeatherSetting/api/{setting,stations,test}`；改 `lastFetchMessage`（ok / missing / stale / error）換狀態；status 版 `--selector '.weather-setting-page > .card:last-child'` |
| `recipes/manual-scada-account.html` / `-account-edit.html` | /AccountSetting 帳號管理（account-01/02） | 6 帳號（含停用、從未登入）；edit 版開 operator01（User）頁面權限 | mock `/AccountSetting/GetPermissions/3`；可設定頁清單取自 `PermissionService.cs` 寫入 `data-configurable-pages`；只有 User 角色才顯示權限區 |
| `recipes/manual-ems-home.html` | /EMS 首頁（ems-home-01） | 五卡，總表 = 空調系統 + 動力設備 + 照明插座 | 由 ems-index 換新 navbar 並改通用名，數字沿用 |
| `recipes/manual-ems-card-setting.html` | /EmsCardSetting（ems-card-setting-01） | 版面預覽 + 隱藏氣量三卡與空壓機效率 | mock `/EmsCardSetting/api/cards`（`{cards:[{cardKey,nameKey,gridCss,icon,isVisible}]}`，照 `EmsCardRegistry`） |
| `recipes/manual-ems-circuit-info.html` | /ElectricityCircuitInfo（circuit-info-01） | 總表 + 五子迴路，年 / 月 / 日三卡 | 由 electricity-circuitinfo-fivebuildings 換 navbar 改名 |
| `recipes/manual-ems-energy-report.html` | /EnergyReport（energy-report-01） | 月粒度 YoY | 舊 recipe 的 PAGE_CONTENT 過時（`initDefaults` 遇 null 拋錯）→ 換成現行 cshtml 轉出的 HTML、i18n 全量字典 |
| `recipes/manual-ems-electricity-cost.html` | /ElectricityCostReport（electricity-cost-01） | 2026/01~08 總表，電費 = kWh × 逐月單價 | mock `/ElectricityCostReport/api/{circuits,query}` + BillingPeriod `{current,range}`；driver 等迴路載入後 `_ecr.query()` |
| `recipes/manual-ems-contract-capacity.html` | /ContractCapacity 正式頁（contract-capacity-01） | 回測 2025-10~2026-09 | 走真實 `contractcapacity.js`，mock `/ContractCapacity/api/{circuits,analyze}`（analyze 由內嵌公式從 `MONTHS` 推出）；時鐘 2026-10-09 讓預設區間對齊；載 TariffSetting resx 才有方案名 |
| `recipes/manual-ems-water-report.html` / `-gas-report.html` | /WaterUsageReport、/GasUsageReport | 用水月粒度 / 用氣兩月一期 6 期 | mock `/{Water,Gas}UsageReport/api/{circuits,query}` + `/{Water,Gas}BillingPeriodSetting/api/{current,range}`；非自然月 label `yyyy-MM-dd~MM-dd` |
| `recipes/manual-ems-water-cost.html` / `-gas-cost.html` | /WaterCostReport、/GasCostReport | 級距切片照 `WaterCostService` 公式 | 水費版展開最後一期並取消 `.table-responsive` max-height |
| `recipes/manual-ems-refrigeration-ton.html` | /RefrigerationTonReport | 2025 全年 | 由 refrigerationtonreport-month 換 navbar |
| `recipes/manual-ems-daily-report.html` / `-daily-report-setting.html` | /DailyReport、/DailyReportSetting | 2026-09-08 日報；4 位收件人 | 頁面不走 resx；mock `/DailyReport/api/{setting,report,recipients}`，`nSectionFlags` 255；比較 / 摘要由逐時陣列推出 |
| `recipes/manual-ems-energy-declaration.html` | /EnergyDeclaration | `--hash modal` 開新增視窗 | 由 energydeclaration-month 換 shell |
| `recipes/manual-ems-air-compressor-declaration.html` | /AirCompressorDeclaration | kWh = 產氣量 × 比能耗；`--hash modal` | mock `/AirCompressorDeclaration/api/{reports,circuits,airmetercircuits,query}` |
| `recipes/manual-ems-energy-baseline.html` / `-energy-baseline-enpi.html` | /EnergyBaseline Tab1 / Tab2 | 區域 A~E，X = 冷氣度時 / 在場人數 / 生產批數 | 由 flightops 兩份改通用名，眉角同原份（Tab1 `--height 1180`） |
| `recipes/manual-ems-chiller-efficiency.html` / `-compressor-diagnosis.html` | /ChillerEfficiency、/CompressorEfficiency 系統診斷 | CH-1~4 / AC-1~3 | 換 shell，資料不變；診斷頁太長只截頂端 |
| `recipes/manual-ems-equipment-efficiency-setting.html` | /EquipmentEfficiencySetting 掃描結果視窗 | CH-1/2 高信心、CH-3 低信心缺流量 | `/api/meta` 為 Common 真實輸出 JSON、`/api/profiles` 空 → 自動掃描；覆寫 `PointPicker.load()` 餵帶 `szDeviceLabel` 的點位，信心度由真實 `equipment-scan.js` 算 |
| `recipes/manual-ems-electricity-meter-setting.html` | /EnergyMeter（electricity-meter-setting-01~03） | 不帶＝詳情 / `mi`＝電表資訊視窗 / `dnd`＝拖曳落點標註（`--height 700 --selector ".col-md-5 .card"`） | mock `/EnergyMeter/api/{tree,sids,points}`；每台需有電能 / 電壓 / 電流 / 功率 / 功因，`role-aliases.js` 才算得出建議 |
| `recipes/manual-ems-{water,gas,air}-meter-setting.html` | /WaterMeterSetting、/GasMeterSetting、/AirMeterSetting | 三頁同構；氣表開新增子節點視窗 | mock `/{Route}/api/{tree,sids}`（帶 `unitScale`）；氣表選點走真實選點器（`setBoundPoint` 未暴露） |
| `recipes/manual-ems-chilled-water-system.html` | /ChilledWaterSystem | 兩個根節點，選 CH-03 | mock `/ChilledWaterSystem/api/{tree,sids}` |
| `recipes/manual-ems-tariff.html` / `-water-tariff.html` | /TariffSetting、/WaterTariffSetting | 高壓二段式、採用時間軸 / 契約歷史各 2 段；台水 4 段累進 | config 取自 `Setting/tariff-taipower-defaults.json`、`water-tariff-taiwater-defaults.json`；i18n 要同時放 `tariffsetting.*` 與 `tariff.*`；電費頁整頁很長 |
| `recipes/manual-ems-billing-period.html` / `-gas-billing-period.html` | /BillingPeriodSetting、/GasBillingPeriodSetting | 第 7 期 1 天空窗警告；氣費兩月一期 + 已刪除期別展開 | 空窗警告由前端比對相鄰期，不讀 gapDays；氣費 `{periods,skipped}`，driver 對 `#gbpSkippedBox` 加 `show` |
| `recipes/manual-ems-holiday.html` / `-shift.html` | /HolidaySetting、/ShiftSetting | 2026 年曆（假日為示意）；早 / 中 / 夜 / 假日班 | weekdays 用 .NET DayOfWeek（0 = 週日） |
| `recipes/manual-engineer-modbus-source.html` | /ModbusCoordinator（modbus-source-01/02） | `--hash preview` 開匯入預覽（截 `#srcxlPreviewModal .modal-content`） | 資料走 `window._modbusCoordinatorData`；mock `FolderWorkbooks` + `ImportPreviewFromFolder` |
| `recipes/manual-engineer-db-source.html` | /DbCoordinator | 三個 DB 來源，SID `DB1-Sn` | 資料走 `window._dbCoordData` |
| `recipes/manual-engineer-opcua-source.html` | /OpcUaCoordinator | PLC-01 兩組設備，跳過 S8 示範 Seq 不回收 | 資料走 `window._opcuaData`；26.19.1 前 `opcuacoordinator.js` 有語法錯誤，已修，recipe 載原檔 |
| `recipes/manual-engineer-calc-point.html` | /CalcPoint 新增視窗 | 冷凍噸公式 3 變數，預覽 151.77 RT | `addVariableRow()` 不吃參數 → 先加空列再填；mock `/CalcPoint/Preview`；`--height 780` |
| `recipes/manual-engineer-system-parameter.html` | /SystemParameter | 歷史趨勢三參數 | mock `/SystemParameter/api/config`；`--height 620` |
| `recipes/manual-engineer-roles-accounts.html` | /AccountSetting（Engineer 視角） | 新增使用者視窗角色選 Engineer | 由 manual-scada-account 改 |
| `recipes/manual-engineer-designer.html` | /Designer | 冰水系統圖，選中 AI 點位看「小數點位數」 | `/Designer/Load` 沿用 manual-scada-scada-page 圖面；其他 Designer API 回空；設計模式數值顯示「--」為正常 |
| `recipes/manual-engineer-logic-flow.html` | /LogicFlow | freq2 回 Warning → 橘色邊線 | 由 logicflow-hvac-optimize 衍生並補載 `point-grouping.js`、`tree-dnd.js` |
| `recipes/manual-engineer-license.html` | 授權失效橫幅（route=none） | 即時監控頁 + `#license-warning-banner` | 以 manual-scada-scada-page 為底，橫幅設 `display:flex` |
| `recipes/manual-engineer-modbus-gateway.html` | ModbusServer :5041 對照表 | 23 點，四種來源、五種狀態 | 直接用 `ScadaEngine.ModbusServer/Web/index.html` 原檔注入 mock `/api/points`，停掉 `setInterval` |
