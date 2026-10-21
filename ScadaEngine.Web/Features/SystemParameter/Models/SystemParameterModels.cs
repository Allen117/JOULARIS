namespace ScadaEngine.Web.Features.SystemParameter.Models;

/// <summary>
/// 系統參數整份組態 — 對應 SystemSettings.system_parameters 的 JSON value。
/// 屬性初始值即預設值：DB 缺鍵（舊資料、新加參數）反序列化時自動補預設，不需遷移。
/// 新增參數：加屬性 + 預設值 + <see cref="SystemParameterLimits"/> 範圍 + 頁面欄位 + resx。
/// </summary>
public class SystemParameterConfig
{
    /// <summary>歷史趨勢查詢與匯出共用的每點位最多筆數</summary>
    public int nHistoryMaxRecordsPerPoint { get; set; } = SystemParameterLimits.HistoryMaxRecordsPerPoint.nDefault;

    /// <summary>歷史資料匯出儲存格上限（Σ點位 × 預估筆數），總量最後防線</summary>
    public int nHistoryExportMaxCells { get; set; } = SystemParameterLimits.HistoryExportMaxCells.nDefault;

    /// <summary>累計值抖動容差（%）：降幅在 |前值| × 容差% 內視為抖動，不算歸零 / 換表</summary>
    public double dCumulativeJitterTolerancePct { get; set; } = SystemParameterLimits.dJitterDefault;
}

/// <summary>各參數的預設值與允許範圍（Service 驗證與頁面提示共用）</summary>
public static class SystemParameterLimits
{
    public static readonly (int nDefault, int nMin, int nMax) HistoryMaxRecordsPerPoint = (5000, 100, 100000);
    public static readonly (int nDefault, int nMin, int nMax) HistoryExportMaxCells = (50000, 1000, 1000000);
    public const double dJitterDefault = 1;
    public const double dJitterMin = 0;
    public const double dJitterMax = 50;
}

/// <summary>GET api/config 回應：目前值 + 預設值 + 範圍（頁面顯示用）</summary>
public class SystemParameterResponse
{
    public SystemParameterConfig config { get; set; } = new();
    public SystemParameterConfig defaults { get; set; } = new();
    public DateTime? dtUpdatedAt { get; set; }
}
