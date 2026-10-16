namespace ScadaEngine.Web.Features.DailyReport.Models;

/// <summary>
/// 日報快照 payload — 整份序列化存 DailyReportSnapshot.PayloadJson。
/// Email 與 Web 頁讀同一份，歷史日報不受後續資料回補影響。
/// 生成時一律填入全部區塊，SectionFlags 於「顯示端」（Web 頁 / Email 組版）過濾。
/// </summary>
public class DailyReportData
{
    /// <summary>報告日（= 資料日）yyyy-MM-dd</summary>
    public string szReportDate { get; set; } = "";
    public DateTime dtGeneratedAt { get; set; }
    /// <summary>生成當下的全局語言（智慧提示 / 警報訊息已用此語言渲染）</summary>
    public string szLanguage { get; set; } = "zh-TW";
    public bool isReportDateHoliday { get; set; }
    /// <summary>前日（D-1）是否假日</summary>
    public bool isPrevDayHoliday { get; set; }
    /// <summary>上週同星期（D-7）是否假日</summary>
    public bool isLastWeekHoliday { get; set; }
    /// <summary>生成時報告日最後一小時（23 時）缺資料 → 快照可能不完整</summary>
    public bool isStaleLastHour { get; set; }

    public DailyReportAlarmSummary alarm { get; set; } = new();
    public DailyReportEnergySection electricity { get; set; } = new();
    public DailyReportEnergySection water { get; set; } = new();
    public DailyReportEnergySection gas { get; set; } = new();
    public DailyReportEnergySection rth { get; set; } = new();

    public List<DailyReportComparisonRow> dayComparisons { get; set; } = new();
    public List<DailyReportMtdRow> mtdComparisons { get; set; } = new();
    /// <summary>kWh/RTh 效率比值（電或 RTh 缺任一 → null）</summary>
    public DailyReportEfficiency? efficiency { get; set; }
    /// <summary>外氣日均溫（Weather Coordinator 未設定 → null）</summary>
    public DailyReportWeather? weather { get; set; }
    public List<DailyReportInsight> insights { get; set; } = new();
    /// <summary>空壓系統診斷摘要（最近 7 天滾動排程快照；無空壓機 / 診斷失敗 → null）</summary>
    public DailyReportCompressorDiagnosis? compressorDiagnosis { get; set; }
}

/// <summary>
/// 空壓系統診斷摘要 — 文字已於日報生成時依全局語言渲染（同智慧提示），Email 與 /DailyReport 直接顯示。
/// 只列觸發的規則；其餘以計數帶過，詳情請至 /CompressorEfficiency「系統診斷」。
/// </summary>
public class DailyReportCompressorDiagnosis
{
    /// <summary>診斷期間顯示字串，如 2026-10-09 ~ 2026-10-15</summary>
    public string szPeriod { get; set; } = "";
    public int nTriggered { get; set; }
    /// <summary>資料不足 / 待補場域資料 / 待設定 的規則數</summary>
    public int nPending { get; set; }
    public List<DailyReportCompressorDiagnosisItem> items { get; set; } = new();
    /// <summary>附註（重疊不可相加、基準期未設定、依預設值）</summary>
    public List<string> aNotices { get; set; } = new();
}

public class DailyReportCompressorDiagnosisItem
{
    public string szRuleId { get; set; } = "";
    public string szTitle { get; set; } = "";
    /// <summary>篩檢節能文字（NTD/年 區間，無電價則 kWh/年）；無節能估計 → 空字串</summary>
    public string szSavings { get; set; } = "";
    public string szNextStep { get; set; } = "";
    /// <summary>結論用到未經現場確認的預設參數</summary>
    public bool isUnconfirmedDefault { get; set; }
}

/// <summary>前一日 EventLog 警報 + 故障（EventType 0+1）摘要</summary>
public class DailyReportAlarmSummary
{
    /// <summary>當日發生筆數</summary>
    public int nOccurredCount { get; set; }
    /// <summary>當日發生且已恢復筆數</summary>
    public int nClearedCount { get; set; }
    /// <summary>當日發生且未確認筆數</summary>
    public int nUnacknowledgedCount { get; set; }
    /// <summary>明細（全量存快照；Web 頁預設收合前 20 筆、Email 僅列前 20 筆）</summary>
    public List<DailyReportAlarmItem> items { get; set; } = new();
}

public class DailyReportAlarmItem
{
    public DateTime dtOccurredAt { get; set; }
    public string szSID { get; set; } = "";
    /// <summary>0=警報(Alarm) 1=故障(Fault)</summary>
    public int nEventType { get; set; }
    /// <summary>0=緊急 1=高 2=中 3=低</summary>
    public int nSeverity { get; set; }
    public string szMessage { get; set; } = "";
    public DateTime? dtClearedAt { get; set; }
    public bool isAcknowledged { get; set; }
}

/// <summary>單一能源別的時報表區塊（24 小時 bar + 日總量）</summary>
public class DailyReportEnergySection
{
    /// <summary>false = 該體系未設定迴路（Web / Email 隱藏此區塊）</summary>
    public bool isAvailable { get; set; }
    /// <summary>根迴路名稱（多 root 時以「+」串接）</summary>
    public string szCircuitName { get; set; } = "";
    /// <summary>kWh / m³ / RT·h</summary>
    public string szUnit { get; set; } = "";
    /// <summary>24 格逐時用量（index = 小時 0–23）</summary>
    public List<double> dHourly { get; set; } = new();
    /// <summary>各小時是否缺資料（RTh 體系無 per-bucket 訊號，固定 false）</summary>
    public List<bool> isHourlyStale { get; set; } = new();
    public double dTotal { get; set; }
    public bool isHasWarning { get; set; }
}

/// <summary>單日比較：報告日 D vs 前日（D-1）vs 上週同星期（D-7）</summary>
public class DailyReportComparisonRow
{
    /// <summary>electricity / water / gas / rth</summary>
    public string szEnergy { get; set; } = "";
    public string szUnit { get; set; } = "";
    public double dDay { get; set; }
    public double dPrevDay { get; set; }
    public double dLastWeek { get; set; }
    /// <summary>vs 前日差異 %（基準為 0 時 null）</summary>
    public double? dDiffPrevPct { get; set; }
    /// <summary>vs 上週同星期差異 %（基準為 0 時 null）</summary>
    public double? dDiffLastWeekPct { get; set; }
}

/// <summary>月累計比較：本月 1 日～報告日 vs 去年同月同日數</summary>
public class DailyReportMtdRow
{
    public string szEnergy { get; set; } = "";
    public string szUnit { get; set; } = "";
    public double dCurrent { get; set; }
    public double dLastYear { get; set; }
    public double? dDiffPct { get; set; }
    /// <summary>本期區間顯示字串，如 2026-08-01 ~ 2026-08-05</summary>
    public string szCurrentRange { get; set; } = "";
    public string szLastYearRange { get; set; } = "";
}

/// <summary>kWh/RTh 每冷凍噸耗電比值（RTh 為 0 的日子該值為 null）</summary>
public class DailyReportEfficiency
{
    public double? dDay { get; set; }
    public double? dPrevDay { get; set; }
    public double? dLastWeek { get; set; }
    public double? dDiffPrevPct { get; set; }
    public double? dDiffLastWeekPct { get; set; }
}

/// <summary>外氣日均溫（來源 HistoryData 的 Weather Coordinator S1，Quality=1）</summary>
public class DailyReportWeather
{
    public double? dAvgTempDay { get; set; }
    public double? dAvgTempPrevDay { get; set; }
    public double? dAvgTempLastWeek { get; set; }
}

/// <summary>規則式智慧提示（文字已於生成時依全局語言渲染）</summary>
public class DailyReportInsight
{
    /// <summary>holiday / weather / alarm / efficiency / none</summary>
    public string szCategory { get; set; } = "";
    public string szText { get; set; } = "";
}

/// <summary>規則命中結果（EvaluateRules 靜態輸出，由 InsightService 渲染成文字）</summary>
public class DailyReportInsightHit
{
    public string szCategory { get; set; } = "";
    /// <summary>resx key，如 insight.holiday.lastweek_offday</summary>
    public string szKey { get; set; } = "";
    /// <summary>能源別 key（electricity/…）；有值時渲染端將其顯示名插入 args[0]</summary>
    public string? szEnergyKey { get; set; }
    public object[] args { get; set; } = Array.Empty<object>();
}

// ────────────────────────── 設定 / 收件人 / 快照 ──────────────────────────

/// <summary>DailyReportSetting 單列（Id=1）</summary>
public class DailyReportSettingModel
{
    public int nId { get; set; } = 1;
    public bool isMailEnabled { get; set; }
    public int nSectionFlags { get; set; } = DailyReportSections.All;
    public double dDiffThresholdPercent { get; set; } = 15;
    public string szLanguage { get; set; } = "zh-TW";
    public bool isHolidayHintEnabled { get; set; } = true;
    public DateTime? dtUpdatedAt { get; set; }
}

/// <summary>SectionFlags bitmask 定義</summary>
public static class DailyReportSections
{
    public const int Alarm = 1;
    public const int Electricity = 2;
    public const int Water = 4;
    public const int Gas = 8;
    public const int Rth = 16;
    public const int DayCompare = 32;
    public const int MtdCompare = 64;
    public const int Insights = 128;
    /// <summary>空壓系統診斷（預設關閉 — 不含在 All 內）</summary>
    public const int CompressorDiagnosis = 256;
    /// <summary>新設定的預設值（全部既有區塊；空壓系統診斷需另行勾選）</summary>
    public const int All = 255;

    public static bool Has(int nFlags, int nSection) => (nFlags & nSection) != 0;
}

public class DailyReportRecipientModel
{
    public int nId { get; set; }
    public string szEmailAddress { get; set; } = "";
    public string? szDisplayName { get; set; }
    public bool isEnabled { get; set; } = true;
}

/// <summary>日報寄送結果（MailService 回傳，供快照 MailStatus 更新與 API 回覆）</summary>
public class DailyReportMailResult
{
    public int nSuccess { get; set; }
    public int nFail { get; set; }
    public string szDetail { get; set; } = "";
    /// <summary>寫回 DailyReportSnapshot.MailStatus：1=成功（全數送達）2=失敗（含部分失敗）</summary>
    public byte nMailStatus => nFail == 0 && nSuccess > 0 ? (byte)1 : (byte)2;
}

/// <summary>DailyReportSnapshot 列（不含 PayloadJson）</summary>
public class DailyReportSnapshotMeta
{
    public int nId { get; set; }
    public DateTime dtReportDate { get; set; }
    public DateTime dtGeneratedAt { get; set; }
    /// <summary>0=未寄 1=成功 2=失敗 3=停用</summary>
    public byte nMailStatus { get; set; }
    public string? szMailDetail { get; set; }
}
