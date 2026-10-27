namespace ScadaEngine.Web.Services.HistoryStatistics;

/// <summary>匯出圖表種類</summary>
public enum HistoryChartType
{
    /// <summary>累計值差異值長條圖（每單位組一張）</summary>
    Bar,
    /// <summary>累計值差異值占比圓餅圖（同單位 ≥2 個非零點位才畫）</summary>
    Pie,
    /// <summary>即時值趨勢（XY 散佈圖直線連接，每單位組一張）</summary>
    Line
}

/// <summary>
/// 圖表數列 — 參照儲存格範圍（1-based 列號、欄字母）並附 cache，Excel 不重算也能直接顯示。
/// 長條 / 圓餅：X 為文字類別（<see cref="aszCategories"/>）；折線：X 為時間（Excel 序列值，<see cref="adX"/>）。
/// </summary>
public sealed class HistoryChartSeries
{
    public string szName { get; init; } = string.Empty;
    public string szSheet { get; init; } = string.Empty;
    public int nFirstRow { get; init; }
    public int nLastRow { get; init; }
    public string szXCol { get; init; } = "A";
    public string szYCol { get; init; } = "B";
    public IReadOnlyList<string> aszCategories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<double> adX { get; init; } = Array.Empty<double>();

    /// <summary>Y 值 cache；null = 空白儲存格（Bad），折線於此斷開</summary>
    public IReadOnlyList<double?> adY { get; init; } = Array.Empty<double?>();
}

/// <summary>一張圖表</summary>
public sealed class HistoryChartSpec
{
    public HistoryChartType type { get; init; }
    public string szTitle { get; init; } = string.Empty;
    public List<HistoryChartSeries> series { get; init; } = new();
}
