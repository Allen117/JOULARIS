# SCADA i18n 術語表（zh-TW ↔ en）

新詞翻譯前先查表。新增詞彙時於對應分類補列、保持字典序。

## 品牌 / 系統名

| zh-TW | en | 備註 |
|---|---|---|
| SCADA 監控系統 | SCADA Monitoring System | 主標題 |
| SCADA 智慧能源管理平台 | SCADA Smart Energy Platform | footer 主品牌 |
| SCADA | SCADA | brand，不翻 |
| ITRI | ITRI | logo，不翻 |

## 主功能 / 頁面（對應路由）

| zh-TW | en | 路由 / 備註 |
|---|---|---|
| 全站搜尋 | Global Search | Ctrl+K 命令面板（_Layout 共用元件，無獨立路由） |
| 即時監控 | SCADA Page | `/ScadaPage` |
| 即時數據 | Realtime Data | `/RealTime` |
| 控制邏輯 | Control Logic | top-level menu |
| 條件控制 | Condition Control | `/ConditionCtrl` |
| 流程圖控制 | Logic Flow | `/LogicFlow` |
| 排程設定 | Schedule | `/ScheduleSetting` |
| 歷史資料 | History | top-level menu |
| 趨勢圖 | Trend | `/History/Trend` |
| 事件記錄 | Event Log | `/EventLog` |
| 能源管理 | Energy Management | top-level menu |
| 迴路資訊 | Circuit Info | EMS navbar dropdown 群組標題 |
| 電力迴路資訊 | Electricity Circuit Info | `/ElectricityCircuitInfo`；navbar dropdown 與 Ctrl+K 搜尋共用同一 key |
| 自來水迴路資訊 | Water Meter Circuit Info | `/WaterCircuitInfo` |
| 空調水系統迴路 | Chilled Water Circuit | `/ChilledWaterCircuitInfo`（冷凍噸 RT·h 迴路資訊頁）|
| 氣迴路資訊 | Gas Circuit Info | `/GasCircuitInfo` |
| 用電報表 | Energy Report | `/EnergyReport` |
| 電費報表 | Electricity Cost Report | `/ElectricityCostReport` |
| 冷凍噸報表 | Refrigeration Ton Report | `/RefrigerationTonReport` |
| 能源申報 | Energy Declaration | `/EnergyDeclaration` |
| 電表/迴路設定 | Energy Meter / Circuit | `/EnergyMeter` |
| 自來水表/迴路設定 | Water Meter / Circuit | `/WaterMeterSetting`（累積式水表，非空調水系統） |
| 用水報表 | Water Usage Report | `/WaterUsageReport` |
| 水費設定 | Water Tariff Settings | `/WaterTariffSetting` |
| 水費報表 | Water Cost Report | `/WaterCostReport` |
| 水費月結週期 | Water Billing Cycle | `/WaterBillingPeriodSetting` |
| 氣表/迴路設定 | Gas Meter / Circuit | `/GasMeterSetting`（累積式天然氣表） |
| 用氣報表 | Gas Usage Report | `/GasUsageReport` |
| 氣費設定 | Gas Tariff Settings | `/GasTariffSetting` |
| 氣費報表 | Gas Cost Report | `/GasCostReport` |
| 氣費月結週期 | Gas Billing Cycle | `/GasBillingPeriodSetting` |
| 空調水系統迴路設定 | Chilled Water Circuit Settings | `/ChilledWaterSystem` |
| 月結週期設定 | Billing Cycle Settings | `/BillingPeriodSetting` |
| 電費設定 | Tariff Settings | `/TariffSetting` |
| 國定假日設定 | National Holidays / National Holiday Settings | `/HolidaySetting`（選單用短形） |
| 班別設定 | Shift Settings | `/ShiftSetting` |
| 班別 | Shift | 報表粒度選項 + 班別設定；跨日班 = Overnight |
| 非班別 | Off-shift | 班別粒度報表中未被班次涵蓋時段的補桶列 |
| 系統設定 | System Settings | top-level menu |
| 工程師模式 | Engineer Mode | top-level menu，僅 Engineer 角色可見（含畫面設計/點位設定/流程圖控制） |
| 畫面設計 | Designer | `/Designer` |
| 警報設定 | Alarm Settings | `/AlarmSetting` |
| 點位設定 | Point Settings | submenu |
| Modbus 來源 | Modbus Source | `/ModbusCoordinator` |
| DB 來源 | DB Source | `/DbCoordinator` |
| 計算點位 | Calculated Points | `/CalcPoint` |
| 氣象資料 | Weather Data | `/WeatherSetting`（選單）；頁標題「氣象資料來源設定 / Weather Data Source」 |
| 帳號管理 | Account Management | `/AccountSetting` |
| 個人資料 | Profile | `/Account/Profile` |

## 領域名詞

| zh-TW | en | 備註 |
|---|---|---|
| 點位 | Point | 通用詞 |
| 設備 / 協調器 | Device / Coordinator | 設備層 |
| 迴路 | Circuit | 能源管理用，**不**翻成 Loop |
| 月結週期 / 結算週期 | Billing Cycle | 月粒度報表期界設定 |
| 電費月結週期 | Electricity Billing Cycle | `/BillingPeriodSetting`；DB 表 `BillingPeriods` |
| 水費月結週期 | Water Billing Cycle | `/WaterBillingPeriodSetting`；DB 表 `WaterBillingPeriods`，與電費各自獨立 |
| 氣費月結週期 | Gas Billing Cycle | `/GasBillingPeriodSetting`；DB 表 `GasBillingPeriods`，與電/水各自獨立；多 `IsSkipped` 支援兩月一期 |
| 期別 | Billing Period | 一期 = 一個 YYYY-MM 結算區間 |
| 曆月 | Calendar Month | 自然月（1 日～月底）；冷凍噸報表 / 能源申報 / 能源基線刻意用此，不走期別 |
| 空窗 | Gap | 期別間未被涵蓋的日數 |
| 重疊 | Overlap | 期別間重複涵蓋的日數 |
| 電價方案 | Tariff Plan | 台電電價表一種方案 |
| 採用時間軸 | Adoption Timeline | 「哪天起改用哪個方案」的紀錄清單（`adoptions`） |
| 採用紀錄 | Adoption | 時間軸上一列 = 生效日 + 方案 |
| 生效日 | Effective Date | 該採用紀錄開始適用的日期 |
| 自訂（方案類別） | Custom | 使用者自建方案的類別，與台電四類並列 |
| 新增方案 / 複製方案 | Add Plan / Duplicate Plan | 自建方案操作 |
| 全量重算 | Recalculate All | 依採用時間軸從最早生效日重算至今（前端分段） |
| 非時間電價（累進） | Non-TOU (Progressive) | 表燈累進級距計價 |
| 時間電價 | Time-of-Use (TOU) | 依時段計價 |
| 簡易型 / 標準型時間電價 | Simple / Standard TOU | 表燈 TOU 兩型 |
| 二段式 / 三段式 | Two-Tier / Three-Tier | TOU 段數 |
| 尖峰 / 半尖峰 / 離峰 | Peak / Semi-Peak / Off-Peak | TOU 時段別 |
| 夏月 / 非夏月 | Summer / Non-Summer | 表燈低壓 6/1–9/30；高壓特高壓 5/16–10/15 |
| 基本電費 | Base Charge | 按戶或按契約容量（瓩）計收 |
| 流動電費 | Energy Charge | 按度計收 |
| 經常契約 | Regular Contract | 契約容量項目 |
| 離峰日 | Off-Peak Day | 台電指定假日，計價視同週日 |
| 國定假日 | National Holiday | Holidays 表標註日，TOU 計價落 sun_offday |
| 電費狀態 | Electricity Cost | EMS 卡片標題 |
| 重新計算電費 | Recalculate Cost | TariffSetting 按鈕 |
| 級距 | Tier | 累進計價分段（tariffsetting.section.tiers 沿用「累進級距」） |
| 超額加價 | Surcharge | 簡易型 TOU 月總度數超額加價 |
| 估算 | Estimated (Est.) | 子迴路級距/加價金額占比分攤註記 |
| 今日小計 | Today (subtotal) | EMS 電費卡 |
| 本期 | Period / Current Period | 月結期別語境 |
| 不含基本電費 | Excludes basic (demand) charges | EMS 電費卡註記 |
| 批次生產時間電價 | Batch Production TOU | 高壓/特高壓生產性質限定 |
| 表燈（住商） | Lighting (Residential & Commercial) | 台電用戶類別 |
| 低壓 / 高壓 / 特高壓電力 | Low / High / Extra High Voltage Power | 台電用戶類別 |
| 壓差 / 差壓 | Differential Pressure (dP) | 供回水壓差控制語境 |
| 壓差計 | Differential Pressure Sensor | 直接量測 ΔP 的傳感器 |
| 供水壓 / 回水壓 | Supply Pressure / Return Pressure | dp_fuzzy_pump_freq 輸入 port |
| 計算點 / 計算點位 | Calculated Point | 同義 |
| DB 來源 | DB Source | DBLatestData polling 系列 |
| Modbus 來源 | Modbus Source | Modbus TCP polling 系列 |
| 系統總覽 | System Overview | ScadaPage 樹 |
| 設備名稱 | Device Name | 與「設備」區分 |
| 設備詳細資料 | Device Details | ModbusCoordinator/DbCoord 卡片標題 |
| 裝置資料 | Device Info | ModbusCoordinator 子項目標題 |
| 延遲時間 | Delay Time | Modbus DelayTime |
| 啟用監控 | Monitoring Enabled | MonitorEnabled |
| 輪詢間隔 | Polling Interval | DB PollingInterval |
| 連線逾時 | Connect Timeout | DB ConnectTimeout |
| 點位數 | Point Count | DB Coordinator 點位數 |

## 警報相關

| zh-TW | en | 備註 |
|---|---|---|
| 警報 | Alarm | 不用 alert |
| 警告 | Warning | EventType=2 |
| 故障 | Fault | EventType=1 |
| 資訊 | Info | EventType=3 |
| 系統 | System | EventType=4 |
| 觸發 | Triggered | 警報狀態 |
| 已恢復 | Cleared / Recovered | 警報狀態 |
| 未恢復 | Active | 警報狀態 |
| 嚴重程度 | Severity | 通用詞 |
| 緊急 | Critical | severity=0 |
| 高 | High | severity=1 |
| 中 | Medium | severity=2 |
| 低 | Low | severity=3 |
| 已確認 | Acknowledged | 警報已被人員確認 |
| 未確認 | Unack'd | 縮寫，UI 緊湊 |
| 確認警報 | Acknowledge | 動詞 |
| 觸發值 | Trigger Value | 警報觸發當下的數值 |
| 門檻值 | Threshold | 上下限值 |
| 條件 | Condition | 警報判定運算子 |
| 超過上限 | exceeds upper limit | high 警報訊息 |
| 低於下限 | below lower limit | low 警報訊息 |
| 警報音 | Alarm Sound | _Layout 頂端列鈴鐺 |
| 消音 | Silence | 停止目前警報音，新警報仍會再響（≠ 確認） |
| 暫時靜音 | Snooze | N 分鐘內一律不響，到期自動恢復 |
| 取消靜音 | Unmute | 清除消音 / 暫時靜音 |

## 數據品質

| zh-TW | en | 備註 |
|---|---|---|
| 品質 | Quality | Good/Bad |
| 良好 | Good | quality=1 |
| 不良 | Bad | quality=0 |

## 連線狀態

| zh-TW | en | 備註 |
|---|---|---|
| 連線中… | Connecting… | |
| 已連線 | Connected | |
| 已斷線 | Disconnected | |
| 連線正常 | Connected | Realtime 頁 |
| 連線異常 | Disconnected | Realtime 頁 |

## 通用 UI 詞彙

| zh-TW | en | 備註 |
|---|---|---|
| 儲存 | Save | 動詞祈使 |
| 儲存變更 | Save Changes | 動詞祈使 |
| 取消 | Cancel | 動詞祈使 |
| 確認 | Confirm | 動詞祈使 |
| 確定 | OK | 簡短確認 |
| 刪除 | Delete | 動詞祈使 |
| 確認刪除 | Confirm / Delete | 動詞祈使 |
| 新增 | Add | 動詞祈使 |
| 編輯 | Edit | 動詞祈使 |
| 查詢 | Query | 動詞祈使 |
| 匯出 | Export | 動詞祈使 |
| 匯入 | Import | 動詞祈使 |
| 重新整理 | Refresh | 動詞祈使 |
| 重設 | Reset | 動詞祈使 |
| 套用 | Apply | 動詞祈使 |
| 設定 | Settings | 名詞 |
| 載入中… | Loading… | 進行式 |
| 處理中 | Processing | 進行式 |
| 查詢中… | Querying… | 進行式 |
| 儲存中... | Saving... | 進行式 |
| 全選 | Select All | |
| 清除 | Clear | |
| 清除全部 | Clear All | |
| 搜尋 | Search | 動詞 |
| 篩選 | Filter | |
| 關閉 | Close | 動詞 |
| 展開 | Expand | 動詞 |
| 收合 | Collapse | 動詞 |

## 表單欄位

| zh-TW | en | 備註 |
|---|---|---|
| 名稱 | Name | |
| 帳號 | Username | |
| 姓名 | Name | （人名） |
| 密碼 | Password | |
| 確認密碼 | Confirm Password | |
| 角色 | Role | |
| 部門 | Department | |
| 狀態 | Status | |
| 啟用 | Active | |
| 停用 | Disabled | |
| 啟用帳號 | Account active | |
| 部門 | Department | |
| 操作 | Actions | 表格欄位 |
| 說明 / 備註 | Description / Remarks | |

## 時間 / 日期

| zh-TW | en | 備註 |
|---|---|---|
| 起始時間 / 開始時間 | Start Time | |
| 結束時間 | End Time | |
| 時間戳記 | Timestamp | |
| 發生時間 | Occurred At | EventLog |
| 恢復時間 | Cleared At | EventLog |
| 最後登入 | Last Login | |
| 建立時間 | Created At | |
| 最後更新 | Last update | footer |
| 伺服器時間 | Server Time | footer |
| 快速 | Quick | 快速時間範圍 |

## 用電報表 / 能源

| zh-TW | en | 備註 |
|---|---|---|
| 用電報表 | Energy Report | |
| 電費報表 | Electricity Cost Report | 結果為電費（元）的用電報表對應頁 |
| 電費分布 | Electricity Cost | ElectricityCostReport chart 標題 |
| 冷凍噸報表 | Refrigeration Ton Report | |
| 冷凍噸 / 冷量 | Refrigeration Ton / Cooling Energy | RT 為瞬時，RT·h 為累計冷量 |
| RT | RT | 冷凍噸單位，不翻 |
| RT·h | RT·h | 冷量單位（ton-hours），不翻；類比 kWh |
| 用電量 | Energy | (kWh) |
| 用電量分布 | Energy Consumption | chart 標題 |
| 冷量分布 | Cooling Energy | RefrigerationTonReport chart 標題 |
| 能源申報 | Energy Declaration | 頁面/選單名 |
| 申報報表 | Declaration Report | 使用者自訂的申報報表設定 |
| 冷凍噸數 | Refrigeration (RT·h) | 申報報表欄位，值為 RT·h 累計 |
| 效率 (kWh/RTh) | Efficiency (kWh/RTh) | 用電量 ÷ 冷凍噸數，單位不翻 |
| 數據明細 | Data Detail | 表格區塊 |
| 時段 | Period | 表格欄 |
| 粒度 / 單位 | Granularity | 時 / 日 / 月 / 年 |
| 時 | Hour | granularity |
| 日 | Day | granularity |
| 月 | Month | granularity |
| 年 | Year | granularity |
| 起月 | From Month | |
| 訖月 | To Month | |
| 起年 | From Year | |
| 訖年 | To Year | |
| 合計 | Total | |
| 總計 | Total | 與「合計」同義 |
| 查詢區間 | Query Range | Excel 標籤 |
| 操作者 | Operator | Excel 標籤（不是「比較運算子」） |

## 用水報表 / 水費

| zh-TW | en | 備註 |
|---|---|---|
| 水表 | Water Meter | 累積式水量表（m³/L），與空調水系統（Chilled Water）區分 |
| 空調水系統 | Chilled Water System | 冰水主機冷量體系（`WaterCircuit` / RT·h）；顯示名一律用「空調水系統」，勿簡稱「水系統」 |
| 自來水迴路 | Water Meter Circuit | 累積式水表迴路（`WaterMeterCircuit` / m³）；顯示名一律加「自來水」前綴以與空調水系統區隔 |
| 用水量 | Water Usage | (m³) |
| 用水量分布 | Water Usage | chart 標題 |
| 流動水費 | Water Usage Charge | 台水按度計收（分段累進）；不含基本費 |
| 基本費 | Base Fee | 台水按口徑計收，本系統**不**計算 |
| 分段累進 | Progressive Tiers | 台水流動水費級距計價 |
| 級距 | Tier | 與電費「級距」同詞 |
| 以上 | and above | 末級級距上限（nTo=null） |
| 度（水） | Unit (m³) | 1 度 = 1 立方公尺；UI 一律顯示 m³ 避免與電度混淆 |
| 立方公尺 / 立方米 | Cubic Meter (m³) | 單位不翻，顯示 m³ |
| 公升 | Liter (L) | UnitScale=0.001 自動換算 m³ |
| 溢位上限 | Rollover Max Value | 水表累積讀數歸零上限（MaxVolume） |
| 生效日 | Effective Date | 水費方案版本生效日期 |
| 方案版本 | Plan Version | 依期別起日選用生效方案 |
| 台水 | Taiwan Water Corporation | 品牌名，可縮寫 Taiwater |
| 水費狀態 | Water Cost | EMS 卡片標題 |
| 用水量長條圖 | Water Usage Bar Chart | EMS 卡名 |
| 子迴路用水占比圓餅圖 | Sub-circuit Water Share Pie | EMS 卡名 |

## 用氣報表 / 氣費（天然氣）

| zh-TW | en | 備註 |
|---|---|---|
| 天然氣 | Natural Gas | 領域名詞 |
| 氣表 | Gas Meter | 累積式天然氣量表（m³ / Nm³ / 度） |
| 用氣量 | Gas Usage | (m³) |
| 用氣量分布 | Gas Consumption | chart 標題 |
| 流動氣費 | Gas Flow Charge | 按度計收（分段累進）；不含基本費 |
| 氣費 | Gas Cost | 帳單金額 |
| 度（氣） | Unit (m³) | 天然氣 1 度 = 1 m³；UI 一律顯示 m³ |
| 氣度 / 天然氣度 / 瓦斯度 | Gas Unit | 現場點位單位字樣，UnitScale=1 |
| 標準立方公尺 | Normal Cubic Meter (Nm³) | 單位不翻，顯示 Nm³ |
| 熱值 | Calorific Value | 本系統**不**做熱值換算 |
| 供氣事業 | Gas Utility | 無全國統一費率，費率依合約 |
| 兩月一期 | Two-month Billing Cycle | 供氣事業常見抄表週期 |
| 刪除此期 | Delete Period | 期別設定頁動作（寫 IsSkipped=1，日數併入前一期） |
| 復原 | Restore | 復原已刪除的期別 |
| 已刪除的期別（可復原） | Deleted periods (restorable) | 期別設定頁摺疊區 |
| 氣費狀態 | Gas Cost | EMS 卡片標題 |
| 用氣量長條圖 | Gas Usage Bar Chart | EMS 卡名 |
| 子迴路用氣占比圓餅圖 | Sub-circuit Gas Share Pie | EMS 卡名 |

## 能源基準（ISO 50001）

| zh-TW | en | 備註 |
|---|---|---|
| 能源基準 | Energy Baseline | 頁面/選單名（/EnergyBaseline） |
| 能源基線 | Energy Baseline (EnB) | ISO 50001 EnB，與「能源基準」同物；選單用「能源基準」 |
| 基線期 | Baseline Period | 建模用的歷史期間 |
| 報告期 | Reporting Period | EnPI 比較期間 |
| 相關變數 | Relevant Variable | ISO 50001 用語，回歸的 X |
| 目標能耗 | Target Energy | 回歸的 Y |
| 回歸 / 複線性回歸 | Regression / Multiple Linear Regression | |
| 係數 | Coefficient | β |
| 截距 | Intercept | β0 |
| 調整後 R² | Adjusted R² | |
| CV(RMSE) | CV(RMSE) | 不翻，IPMVP / ASHRAE 慣用 |
| 凍結 / 已凍結 | Freeze / Frozen | 基線係數固定 |
| 草稿 | Draft | 基線模型狀態 |
| 能源績效指標 | Energy Performance Indicator (EnPI) | |
| 節能量 | Savings | 基線預測 − 實際 |
| 累計節能量 | Cumulative Savings | |
| 重大能源使用 | Significant Energy Use (SEU) | ISO 50001 6.3 |
| 累計占比 | Cumulative Share | 帕累托 |
| 帕累托 | Pareto | |

## 氣象資料（/WeatherSetting）

| zh-TW | en | 備註 |
|---|---|---|
| 氣象資料來源 | Weather Data Source | 頁標題 |
| 中央氣象署 | Central Weather Administration (CWA) | 品牌名，縮寫 CWA 不翻 |
| 授權碼 | Authorization Key | CWA 開放資料 API key |
| 測站 | Weather Station | CWA 觀測站 |
| 自動站 | Automatic (Station) | 資料集 O-A0001-001 |
| 署屬站 | Staffed (Station) | 資料集 O-A0003-001（署屬有人站） |
| 外氣溫度 | Outdoor Temperature | DB 來源 Weather S1 |
| 外氣相對濕度 | Outdoor Relative Humidity | DB 來源 Weather S2 |
| 外氣濕球溫度 | Outdoor Wet-Bulb Temperature | DB 來源 Weather S3（由 S1/S2 推導） |
| 外氣焓值 | Outdoor Air Enthalpy | DB 來源 Weather S4（由 S1/S2 推導） |
| 外氣露點溫度 | Outdoor Dew Point Temperature | DB 來源 Weather S5（由 S1/S2 推導） |
| 濕球溫度 | Wet-Bulb Temperature | Stull (2011) 經驗式；CalcPoint 內建函數 WetBulb(T,RH) |
| 焓值 | Enthalpy | 濕空氣比焓 kJ/kg（乾空氣基準）；CalcPoint 內建函數 Enthalpy(T,RH) |
| 露點溫度 | Dew Point Temperature | Magnus 式反解；CalcPoint 內建函數 DewPoint(T,RH) |
| 乾球溫度 | Dry-Bulb Temperature | 一般氣溫 |
| 公式範本 | Formula Template | CalcPoint 建立 modal 下拉 |
| 觀測時間 | Observation Time | CWA ObsTime |
| 缺測 | Missing (Observation) | CWA 哨兵值 -99 |
| 抓取間隔 | Fetch Interval | 分鐘 |
| 過舊 | Too Old / Stale | 觀測時間距今 > 60 分 |

## 趨勢圖

| zh-TW | en | 備註 |
|---|---|---|
| 歷史趨勢圖 | Historical Trend | |
| 即時曲線 | Realtime Trend | Designer trendChart widget（滾動趨勢圖） |
| 時間窗 | Time Window | 曲線呈現「近 N 分鐘」的長度 |
| 畫點間隔 | Plot Interval | 每幾秒畫一個點 |
| 格線數 | Grid Lines | Y 軸區隔線條數 |
| 線寬 | Line Width | |
| 線色 | Line Color | |
| 格線色 | Grid Color | |
| Y 軸上限 | Y Max | 留空＝自動 |
| Y 軸下限 | Y Min | 留空＝自動 |
| 自動 | Auto | Y 軸範圍留空時的顯示字 |
| 顯示圖例 | Show Legend | |
| 點位選取 | Select Points | |
| 待查詢清單 | Query List | |
| 加入待查詢清單 | Add to Query List | |
| 數值 | Value | |
| 最小值 | Min | |
| 最大值 | Max | |
| 平均值 | Avg | |
| 標準差 | Std Dev | |
| 筆數 | Count | |
| 折線 | Line | chart type |
| 柱狀 | Bar | chart type |
| 雙軸 | Dual | axis mode |
| 單軸 | Single | axis mode |
| 統計摘要 | Summary | Excel sheet 名 |
| 快速趨勢 | Quick Trend | ScadaPage 右鍵浮動小窗 |
| 歷史趨勢（開新頁） | Historical Trend (new tab) | ScadaPage 右鍵 |
| 加入趨勢圖清單 | Add to Trend List | ScadaPage 右鍵 |

## 事件記錄

| zh-TW | en | 備註 |
|---|---|---|
| 事件記錄查詢 | Event Log Query | |
| 事件類型 | Event Type | |
| 嚴重程度 | Severity | |
| 確認狀態 | Ack Status | |
| 確認者 | Acked By | |
| 點位篩選 | Point Filter | |
| 未選擇 | None | placeholder |
| 全部 | All | filter |
| 發生中 | Active | cleared_at 為空 |
| 共 N 筆記錄 | N record(s) found | |

## 帳號管理

| zh-TW | en | 備註 |
|---|---|---|
| 使用者清單 | User List | |
| 共 N 筆 | N record(s) | |
| 新增使用者 | Add User | |
| 編輯使用者 | Edit User | |
| 確認登出 | Confirm Log Out | |
| 登入 | Log In | |
| 登出 | Log Out | |
| 個人資料 | Profile | |
| 頁面存取權限 | Page Access Permissions | |
| SCADA 頁面 | SCADA Pages | 帳號管理權限卡片 |
| EMS 頁面 | EMS Pages | 帳號管理權限卡片 |
| 即時監控子頁面 | SCADA Page Subpages | |
| 可檢視 | View | 表頭 |
| 可控制 | Control | 表頭 |
| 尚未登入 | Never logged in | |

## 條件控制 (ConditionCtrl)

| zh-TW | en | 備註 |
|---|---|---|
| 條件控制 | Condition Control | 頁面標題 |
| 條件控制設定 | Condition Control Settings | 副標 |
| 新增條件控制規則 | Add Condition Control Rule | 卡片標題 |
| 條件控制規則清單 | Condition Control Rules | 卡片標題 |
| 條件點位 | Condition Point | 表頭 |
| 參考點位 | Reference Point | 表單欄位 |
| 控制點位 | Control Point | 表頭/欄位 |
| 條件設備 / 設備 | Device | 上下文裡的 Device |
| 子設備 | Sub Device | |
| 計算點位 | Calculated Points | 設備下拉選項 |
| 全部設備 | All Devices | 設備下拉預設 |
| 請選擇設備 / 請選擇子設備 / 請選擇點位 | Select Device / Sub Device / Point | 下拉 placeholder |
| 運算子 | Operator | 比較運算子 |
| 數值 | Value | 表頭/欄位 |
| 控制值 | Control Value | 表頭/欄位 |
| 輸入比較數值 | Enter comparison value | placeholder |
| 輸入控制值 | Enter control value | placeholder |
| 輸入備註說明 | Enter remarks | placeholder |
| 新增規則 | Add Rule | 按鈕 |
| 儲存規則至資料庫 | Save Rules to DB | 按鈕 |
| 清除全部 | Clear All | 按鈕 |
| 尚未新增任何條件控制規則 | No condition control rules yet | 空狀態 |
| 請填寫上方表單後按下「新增規則」 | Fill the form above and click "Add Rule" | 空狀態提示 |
| 大於 | Greater Than | 運算子提示 |
| 小於 | Less Than | 運算子提示 |
| 大於等於 | Greater or Equal | 運算子提示 |
| 小於等於 | Less or Equal | 運算子提示 |
| 等於 | Equal | 運算子提示 |
| 不等於 | Not Equal | 運算子提示 |
| 請選擇條件點位 | Please select a condition point | 表單錯誤 |
| 請輸入條件數值 | Please enter condition value | 表單錯誤 |
| 請選擇控制點位 | Please select a control point | 表單錯誤 |
| 請輸入控制值 | Please enter control value | 表單錯誤 |
| 條件點位與控制點位不能相同 | Condition and control points cannot be the same | 表單錯誤 |
| 確定要清除所有規則嗎？ | Clear all rules? | confirm |
| 已儲存 {N} 筆規則 | {N} rule(s) saved | API 成功訊息 |
| 儲存失敗，請查看 Engine 日誌 | Save failed. See Engine logs. | API 錯誤訊息 |
| 網路錯誤，請稍後再試 | Network error. Please try again. | API 錯誤訊息 |
| 點擊載入至表單 | Click to load into form | tooltip |
| DB 來源 | DB Source | suffix |

## 流程圖控制 (LogicFlow)

### 基本架構

| zh-TW | en | 備註 |
|---|---|---|
| 流程圖控制 | Logic Flow | 頁面 |
| 邏輯流程 | Logic Flow | 樹根 |
| 資料夾 | Folder | 樹節點 |
| 邏輯 | Logic | 樹節點 |
| 新增邏輯 | Add Logic | 按鈕 |
| 新增資料夾 | Add Folder | 按鈕 |
| 開啟畫布 | Open Canvas | 按鈕 |
| 儲存畫布 | Save Canvas | 按鈕 |
| 啟用邏輯 | Enable Logic | 動詞 |
| 停用邏輯 | Disable Logic | 動詞 |
| 重新命名 | Rename | 動詞 |
| 重新整理 | Refresh | 動詞 |
| 展開全部 | Expand All | |
| 收合全部 | Collapse All | |
| 節點 | Node | 通用 |
| 連線 | Connection / Wire | LogicFlow 內 |
| 畫布 | Canvas | |
| 工具列 | Toolbar | |
| 屬性 / 設定 | Properties / Config | 節點 config |
| 輸入埠 / 輸出埠 | Input Port / Output Port | |
| 歷史值讀取 | Historical Value Read | input / contact 點位模式選項 |
| 讀取歷史值 | Read historical value | 點位選擇器 checkbox |
| N 分鐘前 | N minutes ago | 歷史值 offset 欄位 |
| 複製 | Copy | 樹右鍵（樹層級，含子項、清空綁定） |
| 貼上 | Paste | 樹右鍵 |
| 貼到根層 | Paste to Root | 樹空白區右鍵 |
| - 複製 | - Copy | 複製後綴（同層重名再補「 (2)」） |
| 已複製「{name}」 | "{name}" copied | toast |
| 複製失敗 | Copy failed | API 錯誤 |
| 來源項目已不存在 | Source item no longer exists | 剪貼簿來源已被刪除 |
| 未綁定 | Not bound | 節點徽章（input / output / 接點無點位） |

### 節點類型 (Node displayName)

| zh-TW | en | 備註 |
|---|---|---|
| 讀取點位 | Read Point | 節點 |
| 寫入點位 | Write Point | 節點 |
| A接點 | NO Contact | Normally Open（決策 5） |
| B接點 | NC Contact | Normally Closed（決策 5） |
| 常數 | Constant | 節點 |
| 排程 | Schedule | 節點，與排程設定對應 |
| 比較 | Compare | 節點群類別 |
| 數學運算 | Math | 節點群類別 |
| 邏輯閘 | Logic Gate | 節點群類別 |
| 計時器 | Timer | 節點群類別 |
| 計數器 | Counter | 節點群類別 |
| 輸出 | Output | 通用 |
| 控制 | Control | 通用 |
| 動作 | Action | 通用 |

### 運算子 / 邏輯 (Operator labels)

| zh-TW | en | 備註 |
|---|---|---|
| 大於 (>) | Greater Than (>) | 比較 |
| 小於 (<) | Less Than (<) | 比較 |
| 大於等於 (≥) | Greater or Equal (≥) | 比較 |
| 小於等於 (≤) | Less or Equal (≤) | 比較 |
| 等於 (=) | Equal (=) | 比較 |
| 不等於 (≠) | Not Equal (≠) | 比較 |
| 加 | Add | 數學 |
| 減 | Subtract | 數學 |
| 乘 | Multiply | 數學 |
| 除 | Divide | 數學 |
| 取餘數 | Modulo | 數學 |
| 平方 | Square | 數學 |
| 開根號 | Square Root | 數學 |
| 絕對值 | Absolute | 數學 |
| 反相 | Invert / NOT | 邏輯閘 |
| 及 (AND) | AND | 邏輯閘 |
| 或 (OR) | OR | 邏輯閘 |
| 互斥或 (XOR) | XOR | 邏輯閘 |
| 反及 (NAND) | NAND | 邏輯閘 |
| 反或 (NOR) | NOR | 邏輯閘 |

### 計時器 / 計數器

| zh-TW | en | 備註 |
|---|---|---|
| 通電延遲 (TON) | On-Delay (TON) | timer |
| 斷電延遲 (TOF) | Off-Delay (TOF) | timer |
| 脈衝 (TP) | Pulse (TP) | timer |
| 上數計數器 (CTU) | Up Counter (CTU) | counter |
| 下數計數器 (CTD) | Down Counter (CTD) | counter |
| 上下數計數器 (CTUD) | Up/Down Counter (CTUD) | counter |
| 預設值 | Preset Value | 計時/計數參數 |
| 累積時間 | Elapsed Time | timer |
| 重設 | Reset | counter 動詞 |

### Modal / 對話框

| zh-TW | en | 備註 |
|---|---|---|
| 編輯節點設定 | Edit Node Settings | modal 標題 |
| 節點設定 | Node Settings | modal 標題 |
| 請選擇排程 | Select schedule | placeholder |
| 確定要刪除這個節點嗎？ | Delete this node? | confirm |
| 確定要刪除這個邏輯嗎？ | Delete this logic? | confirm |
| 確定要刪除這個資料夾嗎？ | Delete this folder? | confirm |
| 確定要儲存畫布變更嗎？ | Save canvas changes? | confirm |
| 確定要啟用此邏輯嗎？ | Enable this logic? | confirm |
| 確定要停用此邏輯嗎？ | Disable this logic? | confirm |
| 畫布有未儲存變更，確定要離開嗎？ | Unsaved changes. Leave anyway? | confirm |
| 名稱不能為空 | Name cannot be empty | validate |
| 名稱已存在 | Name already exists | validate |
| 名稱包含不允許的字元 | Name contains invalid characters | validate |
| 版本衝突，請重新整理頁面 | Version conflict. Please refresh. | API 錯誤 |
| 此邏輯不存在或已被刪除 | Logic not found or deleted | API 錯誤 |
| 儲存成功 | Saved | toast |
| 已啟用 | Enabled | toast |
| 已停用 | Disabled | toast |

## 點位設定 (ModbusCoordinator / DbCoordinator)

| zh-TW | en | 備註 |
|---|---|---|
| Modbus 通訊 | Modbus Communication | ModbusCoordinator 卡片標題 |
| DB 通訊 | DB Communication | DbCoordinator 卡片標題 |
| 重新整理頁面 | Refresh Page | DbCoordinator 按鈕 |
| 通知 Engine 重新載入 JSON | Notify Engine to Reload JSON | DbCoordinator 按鈕 |
| 通知中… | Notifying… | DbCoordinator |
| 已通知 Engine 重新載入 JSON | Notified Engine to reload JSON | DbCoordinator API 成功 |
| 通知失敗（請確認 MQTT broker 是否運作） | Notification failed. Please check MQTT broker. | DbCoordinator API 失敗 |
| 請從左側選擇 DB 來源 | Please select a DB source from the sidebar | empty state |
| 尚無 DB 來源 | No DB Source | empty state |
| 尚未載入任何 DB 來源 Coordinator | No DB source coordinator loaded | empty state |
| 尚無設備資料 | No Device Data | empty state |
| 請選擇設備 | Please Select a Device | empty state |
| 儲存名稱 | Save Name | 按鈕 |
| 點位設定 | Point Configuration | 點位熱編輯卡片標題 |
| 儲存點位 | Save Points | 按鈕 |
| 位址 | Address | Modbus 暫存器位址（5 位數慣例）表頭 |
| 資料型態 | Data Type | 表頭（唯讀欄位） |
| 倍率 | Ratio | 表頭 |
| 連線逾時 | Connect Timeout | 設備唯讀資訊列 |
| 無變更 | No changes | 存檔提示 |
| 短暫斷線重連 | briefly disconnect and reconnect | 存檔確認框 |
| 下載範本 | Download Template | Excel 匯入工具列（_SourceExcelImport partial） |
| 匯出 Excel | Export Excel | 工具列 |
| 依來源 Excel 檔匯出 | Export by source workbook | 匯出下拉 |
| 匯入 Excel | Import Excel | 工具列 |
| 匯入預覽 | Import Preview | Modal 標題 |
| 確認匯入 | Confirm Import | Modal 主按鈕 |
| 新增 / 覆寫 / 無變更 / 錯誤 | New / Overwrite / Unchanged / Error | 預覽狀態徽章 |
| 刪除候選 | Delete candidates | 預覽區塊 |
| 其他未包含在這份 Excel 的既有設定 | Other existing configs not in this workbook | 預覽收合區 |
| SID 位移 | SID shift | 高風險警示（中間插入/刪除/重排點位） |
| 我了解歷史資料與控制對應會錯位，仍要覆寫 | I understand history and control mappings will be misaligned; overwrite anyway | 高風險確認勾選 |
| 刪除設備 / 刪除來源 | Delete Device / Delete Source | ModbusCoordinator / DbCoordinator 詳情卡按鈕 |
| 設定檔已移除 | Config removed | 側欄徽章（DB 有 Coordinator 但 JSON 不存在） |
| 來源：{name} | Source: {name} | 既有設定的 SourceWorkbook |
| Excel 匯出時間 | Workbook exported at | 預覽 Modal 頂部（活頁簿 ScadaExportedAt） |
| 在網頁修改過 | Edited on the web | 覆寫網頁修改警告（JSON WebEditedAt） |
| 我了解會覆寫網頁上的修改，仍要匯入 | I understand web edits will be overwritten; import anyway | 確認勾選 |
| 將移除 {count} 個點位 | {count} point(s) will be removed | OPC UA 鍵值式比對（Seq 不回收） |

## 計算點位 (CalcPoint)

| zh-TW | en | 備註 |
|---|---|---|
| 計算點位設定 | Calculated Points Settings | 頁面副標 |
| 新增公式 | Add Formula | 按鈕 |
| 新增計算點位 | Add Calculated Point | Modal 標題 |
| 編輯計算點位 | Edit Calculated Point | Modal 標題 |
| 已設定的計算點位 | Configured Calculated Points | 卡片標題 |
| 公式 / 計算公式 | Formula | 表頭 / 欄位 |
| 群組 / 群組名稱 | Group / Group Name | 表頭 / 欄位 |
| 輸入變數對應 | Input Variable Mapping | Modal 區塊 |
| 變數名稱 | Variable Name | 變數列表頭 |
| 對應點位 | Mapped Point | 變數列表頭 |
| 新增變數 | Add Variable | 按鈕 |
| 即時預覽 | Live Preview | 按鈕 |
| 計算中… | Calculating… | 預覽狀態 |
| 計算結果無效 (NaN/Infinity) | Invalid result (NaN/Infinity) | 錯誤 |
| 公式計算失敗 | Formula evaluation failed | 錯誤 |
| 計算成功 | Calculation succeeded | Service msg |
| 選擇點位來源 | Select Point Source | Picker 標題 |
| 設備點位 | Device Point | Picker step 0 |
| 從 Modbus 設備選擇原始點位 | Select raw points from Modbus devices | Picker 說明 |
| 從公式衍生的計算點位 | Calculated points derived from formulas | Picker 說明 |
| 未分組 | Ungrouped | Picker 群組 |
| 選擇設備 | Select Device | Picker step 1 |
| 選擇計算點位群組 | Select Calculated Point Group | Picker step 1 |
| 選擇點位 / 選擇計算點位 | Select Point / Select Calculated Point | Picker step 2 |
| 確認選擇 | Confirm Selection | Picker 確認鈕 |
| 確認刪除 | Confirm Delete | 按鈕 / Modal 標題 |
| 此操作無法復原 | This action cannot be undone | 刪除 modal |
| Engine 每 60 秒自動重載設定 | Engine auto-reloads config every 60 seconds | footer 提示 |
| {n} 個點位 | {n} points | Picker 計數 |
| 共 {n} 筆 | {n} total | 表格筆數 |
| — 未選擇 — | — Not Selected — | 變數列預設 |
| 無法載入設備/點位清單 | Failed to load device/point list | Picker 載入失敗 |
| 無符合點位 | No Matching Points | Picker 空狀態 |
| 搜尋點位名稱… | Search point name… | Picker placeholder |
| 請輸入名稱 | Please enter a name | validate |
| 請輸入公式 / 請先輸入公式 | Please enter a formula / Please enter a formula first | validate |
| 至少需要一個輸入變數 | At least one input variable is required | validate |
| 名稱不可為空 | Name cannot be empty | validate (Service) |
| 公式不可為空 | Formula cannot be empty | validate (Service) |
| SID 不可為空 | SID cannot be empty | validate (Service) |
| 輸入變數對應格式錯誤 | Invalid input variable mapping format | validate (Service) |
| 新增成功 / 新增失敗 | Added / Add failed | Service msg |
| 更新成功 / 更新失敗 | Updated / Update failed | Service msg |
| 刪除成功 / 刪除失敗 | Deleted / Delete failed | Service msg |
| 儲存成功 / 儲存失敗 | Saved / Save failed | API msg |
| 操作失敗 | Operation failed | 通用錯誤 |
| 網路錯誤 | Network error | 通用錯誤 |
| 參數錯誤 | Invalid parameters | API 錯誤 |
| 更新失敗 | Update failed | API 錯誤 |

## 警報觸發訊息（結構化 i18n key）

| key | zh-TW 模板 | en 模板 |
|---|---|---|
| `alarm.high_exceed` | `{0} 超過上限 {1}` | `{0} exceeds upper limit {1}` |
| `alarm.low_below` | `{0} 低於下限 {1}` | `{0} below lower limit {1}` |
| `alarm.di_triggered` | `{0} 狀態為 {1} 觸發警報` | `{0} state {1} triggered alarm` |

`{0}` = 點位名（user input，可能仍是中文）；`{1}` = threshold 或 state（state 為使用者自填的 DiOnLabel/DiOffLabel，可能仍是中文）。

## 警報設定 (AlarmSetting)

| zh-TW | en | 備註 |
|---|---|---|
| 警報設定 | Alarm Settings | 頁面 |
| 警報規則 | Alarm Rules | tab |
| Line 通知設定 | Line Notification | tab |
| 新增規則 | Add Rule | 按鈕 |
| 新增 Line 群組 | Add Line Group | 按鈕 |
| 上限警報 / 下限警報 / DI 警報 | High Alarm / Low Alarm / DI Alarm | 規則類型 |
| 閾值 | Threshold | 表單 |
| 死區 | Deadband | 表單 |
| 觸發狀態 | Trigger State | DI |
| 接收嚴重度上限 | Max Severity | Line 群組 |
| 只收 緊急 | Critical only | Line option |
| 緊急 + 高 | Critical + High | Line option |
| 全收 | All | Line option |
| 由 Designer DI 點位設定自動帶入 | Auto-populated from Designer DI point settings | hint |
| 計算點位 | Calc Points | 選點器設備選項 |
| 需量點位 | Demand Points | 選點器設備選項（DMD- 虛擬點位） |
| 迴路用電 | Circuit Energy | 選點器設備選項（NRGD-/NRGM- 虛擬點位） |
| 每日用電 | Daily kWh | 迴路用電點位後綴（曆日，今日 00:00 起） |
| 每月用電 | Monthly kWh | 迴路用電點位後綴（曆月，非電費期別） |
| 本月電度 | Period kWh | 迴路用電點位後綴（電費期別，同 Designer 迴路指標） |
| 群組 / 未分組 | Group / Ungrouped | 計算點兩層瀏覽 |
| 迴路 / 請選擇迴路 / 請選擇群組 | Circuit / Select a circuit / Select a group | 第二層下拉 |
| 測試發送 | Test Send | Line 按鈕 |
| 發送中 | Sending | 按鈕 loading |
| 測試訊息已送出，請檢查群組 | Test message sent. Please check the group. | toast |
| 通知語系 | Notification Language | Line / Email 群組欄位 |
| Email 通知設定 | Email Notification | tab |
| SMTP 設定 | SMTP Settings | 按鈕 / Modal |
| 新增 Email 群組 | Add Email Group | 按鈕 |
| 新增收件人 | Add Recipient | 按鈕 |
| 群組識別名稱 | Group Identifier | Email 群組 (Name 欄) |
| 群組顯示名稱 | Group Display Name | Email 群組 (Label 欄) |
| 收件人 / 收件 Email | Recipient / Recipient Email | Email 收件人 |
| 顯示名稱 | Display Name | Email 收件人 |
| SMTP 主機 / 連接埠 | SMTP Host / Port | Email config |
| SMTP 帳號 / 密碼 | SMTP Username / Password | Email config |
| 寄件者 Email / 顯示名稱 | From Address / From Display Name | Email config |
| 每群組每分鐘上限 | Rate per minute per group | rate limit |
| 測試寄送節流（秒） | Test send throttle (seconds) | throttle |
| 啟用 Email 通知 | Enable Email notifications | 總開關 |
| 規則對應 | Rule Mapping | Email 群組 → 警報規則 |
| 對應規則 | Map Rules | 按鈕 |
| 警報觸發 / 警報恢復 | Alarm Triggered / Alarm Cleared | 通知訊息主旨 |
| 通知摘要 | Notification Summary | EventLog |
| 通知通道 | Notify Channel | EventLog 欄位 |
| 收件人數 | Recipients | 表格欄位 |
| 前置條件 | Precondition | 規則欄位（連鎖遮蔽：另一點位條件成立 N 秒才判斷本點警報） |
| 前置點位 | Precondition Point | 前置條件綁定的點位 |
| 持續 | Hold For | 前置條件須連續成立秒數 |
| 警報延遲 | Alarm Delay | 本點 on-delay（越限持續 N 秒才觸發） |
| 恢復通知 | Clear Notification | 規則欄位：恢復時發送的通道（Line / Email / 簡訊勾選） |

## 畫面設計 (Designer)

| zh-TW | en | 備註 |
|---|---|---|
| 畫面設計 | Screen Designer | 頁面 |
| 元件庫 | Components | 左側面板 |
| 屬性 | Properties | 右側面板 |
| 頁面 | Pages | 左上面板 |
| 表格 / 儀錶板 / 文字 / 控制按鈕 | Table / Gauge / Text / Control Button | 元件 |
| AI 點位 / DI 點位 / AO 點位 / DO 點位 | AI Point / DI Point / AO Point / DO Point | 元件 |
| 水泵 | Pump | 元件 |
| 工具列 | Toolbar | |
| 匯入圖片 | Import Image | 工具列 |
| 清除 | Clear | 工具列（與 LogicFlow Clear All 區分） |
| 儲存中… | Saving… | 工具列 |
| 主頁面 | Main Page | 預設根節點 |
| 新頁面 | New Page | addPage 預設名 |
| 複製此頁 | Copy This Page | 頁面樹右鍵（不含子頁、清空綁定） |
| 貼上為子頁面 | Paste as Subpage | 頁面樹右鍵 |
| 貼上為根頁面 | Paste as Root Page | 頁面樹空白區右鍵 |
| - 複製 | - Copy | 複製後綴（同層重名再補「 (2)」） |
| 已複製「{name}」 | "{name}" copied | toast |
| 已貼上「{name}」 | "{name}" pasted | toast |
| 背景圖過大，已略過背景圖 | Background image too large — skipped | localStorage 配額不足 |
| 燈號 / 文字 | Indicator / Text | DI 顯示模式 |
| ON 文字 / OFF 文字 | ON Text / OFF Text | DI 標籤 |
| 警報顏色 / 警報字色 | Alarm Color / Alarm Text Color | DI alarm |
| 顯示名稱 | Display Name | AO/DO |
| 預設寫入值 | Default Write Value | AO |
| 步進值 | Step | AO |
| 小數點位數 | Decimal Places | AO |
| 「手動控制」選單文字 | "Manual Control" menu text | AO |
| 「自動控制」選單文字 | "Auto Control" menu text | AO/DO |
| 「手動ON」選單文字 | "Manual ON" menu text | DO |
| 「手動OFF」選單文字 | "Manual OFF" menu text | DO |
| 留空則不顯示 | Leave blank to hide | placeholder |
| 出水口方向 | Outlet Direction | pump |
| 運轉狀態 / 故障狀態 / 手自動狀態 / 頻率 | Run Status / Fault Status / Manual/Auto Status / Frequency | pump SID |
| 遠端/現場 | Remote/Local | 冰機 szSidMode 語意（1=遠端、0=現場→控制箱面板深黃） |
| 現場面板顏色 | Local Panel Color | 冰機外觀（szManualColor，預設 #c79100） |
| 啟動停止 / 頻率設定 | Start/Stop / Frequency Set | pump CID |
| 重選 / 綁定 / 清除 | Reselect / Bind / Clear | binding action |
| 未綁定 | (not bound) | UI status |
| 未綁定 SID / CID | SID not bound / CID not bound | UI status |
| 透明背景 | Transparent background | |
| 重選 | Reselect | binding |
| 標題列 / 資料列 | Header Row / Data Row | 表格 cell |
| 點位屬性 | Point Type | 表格 cell |
| 小數位數（整欄） | Decimal Places (whole column) | 表格 cell |
| 即時值 / 當日累積 / 當月累積 | Realtime Value / Daily Total / Monthly Total | AI 點位顯示模式 |
| 累積計算方式 | Accumulation Type | AI 點位累積 |
| 累積讀值型（電錶） | Cumulative Reading (Meter) | AI 點位累積 |
| 瞬時值積分型 | Instantaneous Integration | AI 點位累積 |
| 溢位上限 | Rollover Max Value | AI 點位累積 meter |
| 累積單位 | Accumulated Unit | AI 點位累積（如 kW 積分後 kWh） |
| 日累 / 月累 | DAY / MON | 累積元件左上角 badge |
| 不限 | Unlimited | placeholder |
| 選擇點位來源 / 選擇設備 / 選擇點位 | Select Point Source / Select Device / Select Point | picker |
| 選擇計算點位群組 / 選擇計算點位 | Select Calc Group / Select Calc Point | picker |
| 選擇 DB 來源 / 選擇 DB 來源點位 | Select DB Source / Select DB Source Point | picker |
| 設備清單 | Device List | picker |
| 尚無 DB 來源點位 / 尚無設備 / 無符合點位 | No DB source points / No devices / No matching points | picker empty |
| 確定要清除畫布上所有元件？ | Are you sure you want to clear all widgets from the canvas? | confirm |
| 確定要刪除「{name}」？ | Delete "{name}"? | confirm |
| 已成功儲存至資料庫 | Successfully saved to database | toast |
| 儲存失敗：{error} | Save failed: {error} | toast |
| 網路錯誤：{error} | Network error: {error} | toast |
| 未知錯誤 | Unknown error | toast |
| 迴路 | Circuit | picker 來源分頁（能源迴路） |
| 選擇迴路 | Select Circuit | picker |
| 虛擬 | Virtual | 迴路樹未綁 SID 節點標記 |
| 綁定迴路 | Bound Circuit | 屬性面板 |
| 顯示指標 | Metric | 迴路指標下拉 |
| 本日度數 | Today kWh | 迴路指標（曆日，今日 00:00 起） |
| 本月度數 | Month kWh (calendar) | 迴路指標（曆月，1 號 00:00 起） |
| 本月電度 | Period kWh (billing) | 迴路指標（月結期別制，同 EMS EnergyBar 月視圖） |
| 本月電費 | Period Cost | 迴路指標（月結期別制，同 EMS 電費狀態卡） |
| 即時需量 | Real-time Demand | 迴路指標（kW，同 EMS 今日即時需量卡） |
| 日度 / 月度 / 期度 / 期費 / 需量 | D-kWh / M-kWh / P-kWh / P-Cost / Demand | 迴路指標 badge 縮寫 |
| （估算） | (estimated) | 子迴路電費占比分攤 tooltip 註記 |
| 管路 | Pipe | 元件（正交折線流動管路） |
| 折線管路 | Polyline pipe | 2026-07 改版：一條管 = 一串正交節點 |
| 節點 | Node | 折線管路轉折點（拖曳折彎、雙擊插入、右鍵刪除） |
| 正交 | Orthogonal | 任兩相鄰節點共 x 或共 y（僅水平/垂直段） |
| 流向 | Flow direction | pipe 正向/逆向 |
| 流速 | Flow speed | pipe 1..5 檔 |
| 設備級自動綁定 | Equipment-level auto-binding | 泵浦 / 馬達型元件拖放即問 Device、整台帶入（2026-10-14） |
| 選擇設備 | Select Equipment | 設備模式 picker 標題（與點位 picker 的 Select Device 區分：此處選的是「一台設備」而非站） |
| 重新指定設備 | Reassign Equipment | 設備型元件屬性面板按鈕（整台覆蓋） |
| 槽位 | Slot | 設備元件的綁定欄位（運轉 / 故障 / 頻率…） |
| 命中 {n} / {m} 個槽位 | Matched {n} / {m} slots | 設備預覽頂部 |
| 未找到 | Not found | 設備預覽槽位 tag |
| 多筆命中（取第一筆） | Multiple matches (first used) | 設備預覽槽位 tag |
| 已帶入 {n} / {m} 個槽位 | Applied {n} / {m} slots | toast |

## EMS 卡片顯示設定 (EmsCardSetting)

| zh-TW | en | 備註 |
|-------|----|------|
| 卡片顯示設定 | Card Display Settings | 頁面標題 / 選單 |
| 版面預覽 | Layout Preview | 預覽區卡標題 |
| 已隱藏的卡片 | Hidden Cards | 隱藏區卡標題 |
| 隱藏此卡片 | Hide this card | ✕ 按鈕 title |
| 加回 | Add back | 隱藏區按鈕 |
| 無 — 所有卡片皆顯示中 | None — all cards are visible | 隱藏區空狀態 |
| 所有卡片皆已隱藏 | All cards are hidden | 預覽區空狀態 |
| 已儲存，重新整理 EMS 首頁即生效 | Saved. Reload the EMS home page to apply. | toast |
| 尚未啟用任何卡片 | No cards enabled | /EMS 全關提示（ems.no_cards） |
| 主要電表資訊 | Main Meter Info | 卡名 |
| 今日即時需量 | Today's Real-time Demand | 卡名 |
| 主要電表用電長條圖 | Main Meter Energy Bar Chart | 卡名 |
| 子迴路用電占比圓餅圖 | Sub-circuit Energy Share Pie | 卡名 |
| 去年同期比較 | Year-over-Year Comparison | 卡名 / 用電報表 YOY checkbox |
| 去年同期 (kWh) | Last Year (kWh) | 用電報表 YOY 欄／Excel |
| 差異 (kWh) | Diff (kWh) | 用電報表 YOY 欄／Excel |
| 增減% | Change % | 用電報表 YOY 欄／Excel |
| 電費狀態 | Electricity Cost Status | 卡名 |
| 用氣量長條圖 | Gas Usage Bar Chart | 卡名 |
| 子迴路用氣占比圓餅圖 | Sub-circuit Gas Usage Pie | 卡名 |
| 氣費狀態 | Gas Cost Status | 卡名 |

## 翻譯原則

1. **動詞用祈使句**（Save / Cancel / Delete），不要 ing 形式
2. **縮寫用大寫**（OK / SCADA / DB），常見縮寫不翻
3. **進行式加省略號**（Loading… / Querying…），與 zh-TW `…` 對應
4. **品牌詞不翻**（SCADA、ITRI）
5. **領域名詞優先業界慣例**（Modbus、MQTT、SID 不翻）
6. **避免直譯**，例如「迴路」翻 Circuit 不翻 Loop（電力業界 Circuit）
7. **時間軸統一 ISO 8601**：`yyyy-MM-dd HH:mm:ss`，不依 culture 在地化
