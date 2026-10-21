namespace ScadaEngine.Web.Services.HistoryStatistics;

/// <summary>
/// 點位類型 — 決定是否算差異值、匯出時畫哪種圖。
/// 判定規則見 <see cref="PointKindClassifier"/>。
/// </summary>
public enum PointKind
{
    /// <summary>即時值（溫度、壓力、功率…）→ 每單位一張折線</summary>
    Instant,
    /// <summary>累計值（kWh、RTh、m³…）→ 差異值 + 長條 / 圓餅</summary>
    Cumulative,
    /// <summary>期間值（今日用電、本月用電…）→ 不算差異值、不畫圖</summary>
    Period,
    /// <summary>狀態值（有效值全為 0 / 1）→ 不畫圖；平均值即運轉率</summary>
    Status
}

/// <summary>點位分類結果</summary>
public sealed record PointClassification(PointKind kind, string szUnitGroup);

/// <summary>
/// 單一點位的統計結果 — 趨勢頁 API 與 Excel 匯出共用同一份。
/// 只用 Quality=Good 且為有限數的值；整段無有效值時數值欄皆 null。
/// </summary>
public sealed class HistoryPointStatistics
{
    public PointKind kind { get; init; }

    /// <summary>單位分組（累計值為正規化單位如 kWh / m³；即時值為 trim 後原單位，可為空）</summary>
    public string szUnitGroup { get; init; } = string.Empty;

    /// <summary>總筆數（含 Bad）</summary>
    public int nCount { get; init; }

    /// <summary>有效筆數（Good 且有限數）</summary>
    public int nValidCount { get; init; }

    public double? dMin { get; init; }
    public double? dMax { get; init; }
    public double? dAvg { get; init; }

    /// <summary>母體標準差</summary>
    public double? dStdDev { get; init; }

    /// <summary>差異值（僅累計值；分段相加，見 HistoryStatisticsCalculator.CalculateDiff）</summary>
    public double? dDiff { get; init; }

    /// <summary>判定為歸零 / 換表的下降次數</summary>
    public int nDropCount { get; init; }

    /// <summary>被剔除的單筆突波數</summary>
    public int nSpikeCount { get; init; }

    /// <summary>首筆有效值距查詢起點超過一個取樣間隔（差異值起點往內找）</summary>
    public bool isStartInset { get; init; }

    /// <summary>末筆有效值距查詢終點超過一個取樣間隔（差異值終點往內找）</summary>
    public bool isEndInset { get; init; }

    public DateTime? dtFirstValid { get; init; }
    public DateTime? dtLastValid { get; init; }

    public bool isNoValidData => nValidCount == 0;
}

/// <summary>單一點位查詢結果（資料 + 統計），趨勢頁 API 與匯出共用</summary>
public sealed class HistoryPointResult
{
    public string szSID { get; init; } = string.Empty;
    public string szName { get; init; } = string.Empty;
    public string szUnit { get; init; } = string.Empty;
    public IReadOnlyList<ScadaEngine.Common.Data.Models.HistoryDataModel> data { get; init; }
        = Array.Empty<ScadaEngine.Common.Data.Models.HistoryDataModel>();
    public HistoryPointStatistics stats { get; init; } = new();
    public bool isLimited { get; init; }
}

/// <summary>差異值計算細節</summary>
public sealed record CumulativeDiffResult(double dDiff, int nDropCount, int nSpikeCount);
