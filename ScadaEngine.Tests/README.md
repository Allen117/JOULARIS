# ScadaEngine.Tests

自動化測試專案（xUnit）。完整說明見 [docs/測試指南.md](../docs/測試指南.md)。

## 跑測試

```powershell
dotnet test                              # 從方案根目錄，跑全部
dotnet test ScadaEngine.Tests            # 只跑本專案
dotnet test --filter "ResolveSeason"     # 跑特定類別/方法
```

## 目錄結構

按「被測對象」分資料夾，一個對象一個測試檔：

```
ScadaEngine.Tests/
├── Electricity/
│   ├── ResolveDayTypeTests.cs            ← 日別判定（假日/週末/平日）
│   └── ResolveSeasonTests.cs             ← 夏月/非夏月判定（含跨年區間）★含業務假設，見下
├── Baseline/
│   ├── BaselineRegressionEngineTests.cs  ← 線性預測 y=截距+Σ(係數·X)
│   └── BuildMonthAlignedRangesTests.cs   ← 區間切成曆月對齊 chunk
├── ScadaPage/
│   └── ParseActionTypeTests.cs           ← 控制動作字串→enum
├── Widget/
│   └── WidgetAccumulationCacheTests.cs   ← 日/時 bucket key 格式
├── ConditionCtrl/
│   └── ParseOperatorTests.cs             ← 比較符號→nOperator
├── OpcUa/
│   └── OpcUaClientHelperTests.cs         ← 讀回值/寫入值型別轉換
└── Equipment/                            ← 設備效率診斷（冰機 / 空壓機）
    ├── EquipmentMetricCalculatorTests.cs ← kW/RT、COP、EER、比功率、流量+ΔT→RT、評級、停機、負載 bin
    ├── EquipmentUnitScaleTests.cs        ← 單位→基準單位係數（L/min、m³/min、bar…）+ 必要角色
    ├── EquipmentHourlyAggregatorTests.cs ← 分鐘對齊算 RT、kWh 兩路徑、RunHours、sparse、Backfill payload
    └── EquipmentPeriodMetricTests.cs     ← 能量加權期間效率、Quality 過濾、覆蓋率、排名、即時組裝、存檔驗證
```

新增測試時照此 pattern：`{領域}/{被測方法}Tests.cs`，方法名用中文「情境_期望」。

## 目前覆蓋範圍（63 個測試）

鎖住的都是**純函式、正確性能從程式碼/數學自明**的邏輯，非我瞎猜的業務規則。

> ⚠️ **唯一含業務假設者**：`ResolveSeasonTests.cs` 的夏月日期（假設 6/1~9/30）。
> 綠燈只證明「程式符合這假設」，不證明「假設符合台電實際費率」——請懂費率者 review 那些 `InlineData`。

## 尚未覆蓋（後續優先）

- **LogicFlow 演算法節點**（COP、kW/RT 等）：`.cs` 由 `CSharpAlgorithmService` **執行期 Roslyn 動態編譯**、以 Content 出貨，`<Compile Remove="Algorithms\**\*.cs" />` 排除於正常組件外，故 ProjectReference 參考不到。需另建「編譯宿主整合測試」才能測。
- **需量 15 分鐘滑動視窗**：邏輯埋在 `DemandCalculatorService`（BackgroundService，private/instance），需重構抽出純函式才好測。
- **Dapper SQL alias 映射、電費逐時聚合觸發**：屬整合測試，需接測試 DB。
- **真實帳單對帳**：拿一份實際月帳單 vs 系統算出值，這才是驗證「產線資料正確」的手段。
