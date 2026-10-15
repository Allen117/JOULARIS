namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 空壓機能源申報查詢結果 — 某份申報報表在指定年度的 12 個曆月 bucket，
/// 每個 bucket 同時含用電量 kWh、產氣量 Nm³、比能耗 kWh/Nm³。
/// 由 AirCompressorDeclarationService 合併 EnergyReportResult + AirMeterUsageReportResult 產出，View / Excel 匯出使用。
/// 比能耗 = kWh ÷ Nm³，Nm³ ≤ 0 時為 null（前端顯示 --）。
/// </summary>
public class AirCompressorDeclarationResult
{
    /// <summary>申報報表設定 Id</summary>
    public int nReportId { get; set; }

    /// <summary>申報報表名稱（顯示用）</summary>
    public string szReportName { get; set; } = string.Empty;

    /// <summary>用電迴路名稱（顯示用）</summary>
    public string szEnergyCircuitName { get; set; } = string.Empty;

    /// <summary>產氣量表迴路名稱（顯示用）</summary>
    public string szAirMeterCircuitName { get; set; } = string.Empty;

    /// <summary>申報年度</summary>
    public int nYear { get; set; }

    /// <summary>查詢起點（= 該年 1/1 00:00）</summary>
    public DateTime dtStart { get; set; }

    /// <summary>查詢終點（= 次年 1/1 00:00，exclusive）</summary>
    public DateTime dtEnd { get; set; }

    /// <summary>12 個曆月 bucket</summary>
    public List<AirCompressorDeclarationBucket> buckets { get; set; } = new();

    /// <summary>總用電量（所有 bucket kWh 加總）</summary>
    public double dTotalKwh { get; set; }

    /// <summary>總產氣量（所有 bucket Nm³ 加總）</summary>
    public double dTotalNm3 { get; set; }

    /// <summary>整體比能耗 = 總 kWh ÷ 總 Nm³；總 Nm³ ≤ 0 時為 null</summary>
    public double? dTotalKwhPerNm3 { get; set; }

    /// <summary>用電側是否有警告（缺資料 / 溢位無 MaxKwh）</summary>
    public bool isHasKwhWarning { get; set; }

    /// <summary>產氣側是否有警告（AirMeterLeafHourly 有掉線 transition）</summary>
    public bool isHasNm3Warning { get; set; }
}

/// <summary>單一曆月 bucket 的用電量 + 產氣量 + 比能耗</summary>
public class AirCompressorDeclarationBucket
{
    /// <summary>bucket 起始時間（含）</summary>
    public DateTime dtBucketStart { get; set; }

    /// <summary>顯示用標籤（yyyy-MM）</summary>
    public string szLabel { get; set; } = string.Empty;

    /// <summary>該月用電量（kWh）</summary>
    public double dKwh { get; set; }

    /// <summary>該月產氣量（Nm³）</summary>
    public double dNm3 { get; set; }

    /// <summary>比能耗 = kWh ÷ Nm³；Nm³ ≤ 0 時為 null（前端顯示 --）</summary>
    public double? dKwhPerNm3 { get; set; }

    /// <summary>kWh 側該月電表資料不完整（可能斷線）</summary>
    public bool isKwhStale { get; set; }

    /// <summary>Nm³ 側該月有產氣量表掉線 transition</summary>
    public bool isNm3Stale { get; set; }
}
