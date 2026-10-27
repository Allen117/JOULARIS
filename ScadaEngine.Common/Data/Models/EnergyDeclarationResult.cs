namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 能源申報查詢結果 — 頁面固定格式：某份申報報表在指定年度的 12 個曆月 bucket
/// （每月 1 號 00:00 ~ 次月 1 號 00:00），每個 bucket 同時含 kWh 與 RT·h。
/// 純資料結構，由 EnergyDeclarationService 合併 EnergyReportResult + RefrigerationTonReportResult 產出，
/// View / Excel 匯出使用。效率欄 dKwhPerRtHour = kWh ÷ RT·h，RT·h 為 0 或缺值時為 null（前端顯示 --）。
/// </summary>
public class EnergyDeclarationResult
{
    /// <summary>申報報表設定 Id</summary>
    public int nReportId { get; set; }

    /// <summary>申報報表名稱（顯示用）</summary>
    public string szReportName { get; set; } = string.Empty;

    /// <summary>用電迴路名稱（顯示用）</summary>
    public string szEnergyCircuitName { get; set; } = string.Empty;

    /// <summary>空調水系統迴路名稱（顯示用）</summary>
    public string szWaterCircuitName { get; set; } = string.Empty;

    /// <summary>申報年度</summary>
    public int nYear { get; set; }

    /// <summary>查詢起點（= 該年 1/1 00:00）</summary>
    public DateTime dtStart { get; set; }

    /// <summary>查詢終點（= 次年 1/1 00:00，exclusive）</summary>
    public DateTime dtEnd { get; set; }

    /// <summary>12 個曆月 bucket 的標籤（yyyy-MM）與 kWh / RT·h / 效率值</summary>
    public List<EnergyDeclarationBucket> buckets { get; set; } = new();

    /// <summary>區間總用電量（所有 bucket kWh 加總）</summary>
    public double dTotalKwh { get; set; }

    /// <summary>區間總冷量（所有 bucket RT·h 加總）</summary>
    public double dTotalRtHour { get; set; }

    /// <summary>區間整體效率 = 總 kWh ÷ 總 RT·h；總 RT·h 為 0 時為 null</summary>
    public double? dTotalKwhPerRtHour { get; set; }

    /// <summary>用電側是否有警告（缺資料 / 溢位無 MaxKwh）</summary>
    public bool isHasKwhWarning { get; set; }

    /// <summary>冷凍噸側是否有警告（WaterLeafHourly 覆蓋率不足）</summary>
    public bool isHasRtWarning { get; set; }
}

/// <summary>單一 bucket 的時間 + 用電量 + 冷量 + 效率</summary>
public class EnergyDeclarationBucket
{
    /// <summary>bucket 起始時間（含）</summary>
    public DateTime dtBucketStart { get; set; }

    /// <summary>顯示用標籤，例如 "2026-05-05 13:00"、"2026-05"、"2026"</summary>
    public string szLabel { get; set; } = string.Empty;

    /// <summary>該 bucket 用電量（kWh）</summary>
    public double dKwh { get; set; }

    /// <summary>該 bucket 冷量（RT·h）</summary>
    public double dRtHour { get; set; }

    /// <summary>效率 = kWh ÷ RT·h；RT·h 為 0 或缺值時為 null（前端顯示 --）</summary>
    public double? dKwhPerRtHour { get; set; }

    /// <summary>
    /// kWh 側該曆月 bucket 邊界值抓不到（電表可能斷線 / 缺資料）→ true。
    /// 由 kWh 曆月 bucket 的 isStale 帶入，前端據此在 kWh 柱/格 hover 提示斷線。
    /// </summary>
    public bool isKwhStale { get; set; }

    /// <summary>kWh 側該曆月邊界缺讀數、改以月內首/末筆讀數起算（電表期中上線）→ true；由 kWh bucket 的 isPartial 帶入。</summary>
    public bool isKwhPartial { get; set; }
}
