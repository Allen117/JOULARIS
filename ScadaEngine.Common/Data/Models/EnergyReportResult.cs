namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 用電報表查詢結果 — 對應某迴路在某粒度下的 N 個 bucket。
/// 純資料結構，由 EnergyReportService 產出，View / Excel 匯出使用。
/// </summary>
public class EnergyReportResult
{
    /// <summary>查詢的迴路 Id</summary>
    public int nCircuitId { get; set; }

    /// <summary>迴路名稱（顯示用）</summary>
    public string szCircuitName { get; set; } = string.Empty;

    /// <summary>粒度：hour / day / month / year</summary>
    public string szGranularity { get; set; } = string.Empty;

    /// <summary>查詢起點（含）</summary>
    public DateTime dtStart { get; set; }

    /// <summary>查詢終點（含），用於顯示</summary>
    public DateTime dtEnd { get; set; }

    /// <summary>各 bucket 的時間標籤與 kWh 值</summary>
    public List<EnergyReportBucket> buckets { get; set; } = new();

    /// <summary>區間總用電量（所有 bucket 加總）</summary>
    public double dTotalKwh { get; set; }

    /// <summary>是否任一葉子有缺資料/溢位無 MaxKwh 警告</summary>
    public bool isHasWarning { get; set; }

    /// <summary>
    /// 是否為「去年同期比較」模式（僅月粒度 + 使用者勾選時 true）。
    /// true 時各 bucket 的 dLastYearKwh / dDiffKwh / dPctChange 已填，View / Excel 據此增欄。
    /// </summary>
    public bool isYoy { get; set; }

    /// <summary>
    /// 直接子迴路的拆解（僅 Excel 匯出使用，預設為空）。
    /// 查詢 API 不會填這個欄位；只有 GetReportWithChildrenAsync 會展開。
    /// </summary>
    public List<EnergyReportChildSeries> children { get; set; } = new();
}

/// <summary>單一 bucket 的時間 + 用電量</summary>
public class EnergyReportBucket
{
    /// <summary>bucket 起始時間（含）</summary>
    public DateTime dtBucketStart { get; set; }

    /// <summary>顯示用標籤，例如 "2026-05-05 13:00"、"2026-05"、"2026"</summary>
    public string szLabel { get; set; } = string.Empty;

    /// <summary>該 bucket 用電量（kWh）</summary>
    public double dKwh { get; set; }

    /// <summary>
    /// 該 bucket 任一葉子的邊界值抓不到（staleness window 內無 Quality=1 資料 / 缺資料）→ true。
    /// 前端據此在該柱/格 hover 提示「電表資料不完整、可能斷線」。
    /// </summary>
    public bool isStale { get; set; }

    /// <summary>
    /// 該 bucket 任一葉子的期初 / 期末邊界缺讀數，改以區間內第一筆 / 最後一筆有效讀數相減（電表期中上線或期末斷線）→ true。
    /// 數值為「自首筆讀數起、至末筆讀數止」的累計，前端提示「資料不完整，僅統計有讀數的區段」。與 isStale 互斥。
    /// </summary>
    public bool isPartial { get; set; }

    /// <summary>
    /// 去年同期（去年同月的帳單期別）該 bucket 的 kWh；
    /// 去年該期未設定/無資料時為 null（View / Excel 顯示 "--"）。僅 YOY 模式填值。
    /// </summary>
    public double? dLastYearKwh { get; set; }

    /// <summary>本期 − 去年同期（kWh）；去年為 null 時亦為 null。僅 YOY 模式填值。</summary>
    public double? dDiffKwh { get; set; }

    /// <summary>增減百分比 = 差異 / |去年同期| × 100；去年為 0 或 null 時為 null。僅 YOY 模式填值。</summary>
    public double? dPctChange { get; set; }
}

/// <summary>
/// 父迴路下的單一直接子節點 series — 給 Excel 匯出多欄展開用。
/// dKwhPerBucket 與父 result.buckets 同 index 對齊，順序一致。
/// </summary>
public class EnergyReportChildSeries
{
    /// <summary>子迴路 Id</summary>
    public int nCircuitId { get; set; }

    /// <summary>子迴路名稱（顯示用）</summary>
    public string szName { get; set; } = string.Empty;

    /// <summary>各 bucket kWh，與父 buckets 同 index 對齊</summary>
    public List<double> dKwhPerBucket { get; set; } = new();

    /// <summary>該子迴路在區間內的合計</summary>
    public double dTotalKwh { get; set; }
}
