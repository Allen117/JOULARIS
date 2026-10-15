namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 產氣量曆月查詢結果 — 某個產氣量表迴路在指定年度的 12 個曆月 bucket（Nm³）。
/// 由 AirMeterUsageReportService.GetCalendarMonthlyReportAsync 產出，供空壓機能源申報合併使用
/// （本體系目前不做獨立產氣報表頁）。
/// </summary>
public class AirMeterUsageReportResult
{
    /// <summary>查詢的迴路 Id</summary>
    public int nCircuitId { get; set; }

    /// <summary>迴路名稱（顯示用）</summary>
    public string szCircuitName { get; set; } = string.Empty;

    /// <summary>查詢起點（= 該年 1/1 00:00）</summary>
    public DateTime dtStart { get; set; }

    /// <summary>查詢終點（= 次年 1/1 00:00，exclusive）</summary>
    public DateTime dtEnd { get; set; }

    /// <summary>各曆月 bucket 的標籤與產氣量（Nm³）</summary>
    public List<AirMeterUsageReportBucket> buckets { get; set; } = new();

    /// <summary>區間總產氣量（Nm³，所有 bucket 加總）</summary>
    public double dTotalNm3 { get; set; }

    /// <summary>是否任一葉子有掉線 transition（Quality=0）警告</summary>
    public bool isHasWarning { get; set; }
}

/// <summary>單一曆月 bucket 的產氣量</summary>
public class AirMeterUsageReportBucket
{
    /// <summary>bucket 起始時間（含）</summary>
    public DateTime dtBucketStart { get; set; }

    /// <summary>顯示用標籤（yyyy-MM）</summary>
    public string szLabel { get; set; } = string.Empty;

    /// <summary>該 bucket 產氣量（Nm³）</summary>
    public double dNm3 { get; set; }

    /// <summary>該 bucket 內有任一葉子掉線 transition（Quality=0）→ true</summary>
    public bool isStale { get; set; }
}
