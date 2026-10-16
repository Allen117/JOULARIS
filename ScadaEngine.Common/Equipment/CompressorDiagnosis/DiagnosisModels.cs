using System.Text.Json.Serialization;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

// ═══════════════════════════════ 輸入 ═══════════════════════════════

/// <summary>空壓機控制型式（EquipmentProfile.ControlType）</summary>
public static class CompressorControlType
{
    /// <summary>定頻加卸載（load / unload）</summary>
    public const string Fixed = "fixed";
    /// <summary>變頻（VSD）</summary>
    public const string Vsd = "vsd";
    /// <summary>進氣節流（modulation）</summary>
    public const string Modulation = "modulation";

    public static readonly string[] All = [Fixed, Vsd, Modulation];

    public static bool IsValid(string? sz) => sz == Fixed || sz == Vsd || sz == Modulation;
}

/// <summary>
/// 單台空壓機在分析期間的逐分鐘序列（已換算基準單位：kW / m³/min / bar / 0-1 狀態）。
/// 陣列長度 = 期間分鐘數，index 0 = 期間起點；<see cref="double.NaN"/> = 該分鐘無值；陣列為 null = 該角色未綁。
/// </summary>
public sealed class CompressorSeries
{
    public int nId { get; set; }
    public string szName { get; set; } = string.Empty;
    public double? dRatedKw { get; set; }
    /// <summary>額定產氣量 m³/min</summary>
    public double? dRatedFlow { get; set; }
    /// <summary>fixed / vsd / modulation；null = 未填（由功率分佈推定）</summary>
    public string? szControlType { get; set; }
    /// <summary>VSD 最低負載 %（未填退回觀測值）</summary>
    public double? dMinLoadPct { get; set; }
    /// <summary>銘牌最高工作壓力 bar</summary>
    public double? dMaxPressure { get; set; }
    /// <summary>馬達每小時最多啟動次數</summary>
    public int? nMaxStartsPerHour { get; set; }

    public double[]? aKw { get; set; }
    public double[]? aFlow { get; set; }
    public double[]? aPress { get; set; }
    public double[]? aLoaded { get; set; }
    public double[]? aRun { get; set; }
}

/// <summary>系統級點位逐分鐘序列（皆可不綁 = null）</summary>
public sealed class SystemSeries
{
    /// <summary>總管 / 供氣壓力 bar</summary>
    public double[]? aSupplyPress { get; set; }
    /// <summary>末端（關鍵用氣點）壓力 bar</summary>
    public double[]? aUsePress { get; set; }
    /// <summary>系統總流量 m³/min</summary>
    public double[]? aFlow { get; set; }
    /// <summary>壓力露點 °C</summary>
    public double[]? aDewpoint { get; set; }
}

/// <summary>診斷輸入（全部由 Web Service 組好，規則引擎不碰 DB）</summary>
public sealed class CompressorDiagnosisInput
{
    public DateTime dtFrom { get; set; }
    /// <summary>期間訖（不含）</summary>
    public DateTime dtTo { get; set; }
    public int nMinutes => Math.Max(0, (int)Math.Round((dtTo - dtFrom).TotalMinutes));

    public List<CompressorSeries> aCompressors { get; set; } = [];
    public SystemSeries system { get; set; } = new();

    /// <summary>末端最低需求壓力 bar（場域）</summary>
    public double? dDemandPressBar { get; set; }
    /// <summary>生產時段（場域；null 或未設定 → L 規則待補）</summary>
    public ProductionSchedule? schedule { get; set; }

    /// <summary>劣化基準期 [起, 訖)；未設定 → E1 待設定</summary>
    public DateTime? dtBaselineFrom { get; set; }
    public DateTime? dtBaselineTo { get; set; }

    /// <summary>分析期間的 EquipmentHourly（各機 kWh / m³ / 運轉時數）</summary>
    public List<EquipmentHourlyModel> aHourly { get; set; } = [];
    /// <summary>基準期的 EquipmentHourly（E1 用）</summary>
    public List<EquipmentHourlyModel> aBaselineHourly { get; set; } = [];

    /// <summary>電價 NTD/kWh；null = 無法取得</summary>
    public double? dPrice { get; set; }
    /// <summary>tariff（期間 ElectricityCostHourly 平均）/ manual（設定頁手填）/ null</summary>
    public string? szPriceSource { get; set; }

    public DiagnosisParameters parameters { get; set; } = DiagnosisParameters.FromOverrides(null, null);
}

// ═══════════════════════════════ 結果 ═══════════════════════════════

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosisStatus
{
    /// <summary>觸發（有篩檢發現）</summary>
    Triggered,
    /// <summary>未觸發</summary>
    NotTriggered,
    /// <summary>資料不足（點名缺什麼，不出結論）</summary>
    InsufficientData,
    /// <summary>待補場域資料（需求壓力 / 生產時段）</summary>
    PendingSiteData,
    /// <summary>待設定（劣化基準期）</summary>
    PendingSetting,
    /// <summary>不適用（例：單台系統的多機排序規則）</summary>
    NotApplicable,
}

/// <summary>低 / 中心 / 高 區間</summary>
public sealed class DiagnosisRange
{
    public double dLow { get; set; }
    public double dCenter { get; set; }
    public double dHigh { get; set; }

    public DiagnosisRange() { }
    public DiagnosisRange(double dLow, double dCenter, double dHigh)
    {
        this.dLow = dLow;
        this.dCenter = dCenter;
        this.dHigh = dHigh;
    }

    public DiagnosisRange Scale(double k) => new(dLow * k, dCenter * k, dHigh * k);
}

/// <summary>篩檢節能（期間 kWh → 年化 kWh → NTD/年；低、高各自年化，不重新組合）</summary>
public sealed class DiagnosisSavings
{
    public DiagnosisRange kwhPeriod { get; set; } = new();
    public DiagnosisRange kwhYear { get; set; } = new();
    /// <summary>電價缺 → null</summary>
    public DiagnosisRange? ntdYear { get; set; }
}

/// <summary>
/// 一段可翻譯的文字：szKey = i18n key（eq.diag.*），args = 代入值（數字已四捨五入）。
/// 規則引擎不產生任何自然語言，只產生 key + 數字，顯示端依 culture 渲染。
/// </summary>
public sealed class DiagnosisText
{
    public string szKey { get; set; } = string.Empty;
    public Dictionary<string, object?> args { get; set; } = [];

    public DiagnosisText() { }
    public DiagnosisText(string szKey, Dictionary<string, object?>? args = null)
    {
        this.szKey = szKey;
        this.args = args ?? [];
    }
}

/// <summary>S2 停機延遲候選試算</summary>
public sealed class ShutdownDelayCandidate
{
    public int nDelayMinutes { get; set; }
    public DiagnosisRange kwhPeriod { get; set; } = new();
    /// <summary>因提早停機而新增的啟動次數（期間合計）</summary>
    public int nAddedStarts { get; set; }
    /// <summary>試算後各機「單一小時最多啟動次數」的最大者</summary>
    public int nMaxStartsPerHour { get; set; }
    /// <summary>超過某台 MaxStartsPerHour → false；未填上限的機台不否決</summary>
    public bool isFeasible { get; set; }
    /// <summary>有機台未填 MaxStartsPerHour（需人工對照銘牌）</summary>
    public bool isLimitUnknown { get; set; }
}

/// <summary>單條規則結果</summary>
public sealed class DiagnosisRuleResult
{
    /// <summary>D / P / L / S1..S6 / E1..E4</summary>
    public string szId { get; set; } = string.Empty;
    public DiagnosisStatus status { get; set; }
    /// <summary>L 規則評級：attention / significant；其他規則 null</summary>
    public string? szLevel { get; set; }
    /// <summary>判定用到推定值（無 LOADED 點以 kW 推定、控制型式推定、duty-cycle 上限估計）</summary>
    public bool isInferred { get; set; }
    /// <summary>證據（數字）</summary>
    public List<DiagnosisText> aEvidence { get; set; } = [];
    /// <summary>附註（降級來源、限制、提醒）</summary>
    public List<DiagnosisText> aNotes { get; set; } = [];
    /// <summary>缺什麼（資料不足 / 待補時點名）</summary>
    public List<DiagnosisText> aMissing { get; set; } = [];
    /// <summary>計算公式 i18n key</summary>
    public string? szFormulaKey { get; set; }
    public DiagnosisSavings? savings { get; set; }
    /// <summary>建議下一步 i18n key 後綴（eq.diag.next.*）</summary>
    public string? szNextStep { get; set; }
    /// <summary>試驗檢查清單 i18n key 後綴（eq.diag.check.*）</summary>
    public List<string> aChecklist { get; set; } = [];
    /// <summary>本條用到的參數鍵</summary>
    public List<string> aParamsUsed { get; set; } = [];
    /// <summary>用到但「未經現場確認的預設值」— 顯示「依預設值」旗標</summary>
    public List<string> aUnconfirmedDefaults { get; set; } = [];
    /// <summary>S2 候選</summary>
    public List<ShutdownDelayCandidate>? aCandidates { get; set; }
}

/// <summary>訊號統計（min / max / mean / p05 / p50 / p95 + 覆蓋率）</summary>
public sealed class SignalStats
{
    public int nCount { get; set; }
    public double dCoveragePct { get; set; }
    public double? dMin { get; set; }
    public double? dMax { get; set; }
    public double? dMean { get; set; }
    public double? dP05 { get; set; }
    public double? dP50 { get; set; }
    public double? dP95 { get; set; }
}

/// <summary>單台實測基線（measured baseline）</summary>
public sealed class CompressorBaseline
{
    public int nId { get; set; }
    public string szName { get; set; } = string.Empty;
    /// <summary>fixed / vsd / modulation / null（無法判定）</summary>
    public string? szControlType { get; set; }
    public bool isControlTypeInferred { get; set; }
    /// <summary>加卸載由 kW 推定（無可信 LOADED 點）</summary>
    public bool isLoadInferred { get; set; }
    /// <summary>未填額定 kW，以觀測最大值代替</summary>
    public bool isRatedKwObserved { get; set; }
    public SignalStats? kw { get; set; }
    public SignalStats? flow { get; set; }
    public SignalStats? press { get; set; }
    public double dLoadedPct { get; set; }
    public double dUnloadedPct { get; set; }
    public double dOffPct { get; set; }
    public double dUnknownPct { get; set; }
    public double dRunHours { get; set; }
    public int nStarts { get; set; }
    public double? dCyclesP50 { get; set; }
    public double? dCyclesP95 { get; set; }
    public double dKwh { get; set; }
}

/// <summary>診斷結果（整份序列化存 CompressorDiagnosisSnapshot.ResultJson）</summary>
public sealed class CompressorDiagnosisResult
{
    public DateTime dtFrom { get; set; }
    public DateTime dtTo { get; set; }
    public DateTime dtRunAt { get; set; }
    public double dDays { get; set; }
    public int nCompressors { get; set; }

    /// <summary>期間總耗電 kWh（EquipmentHourly 加總）</summary>
    public double dPeriodKwh { get; set; }
    /// <summary>期間比功率 kW/(m³/min) = 60·ΣkWh ÷ Σm³</summary>
    public double? dSpecificPower { get; set; }
    public double? dPrice { get; set; }
    public string? szPriceSource { get; set; }

    public List<CompressorBaseline> aBaseline { get; set; } = [];
    public SignalStats? supplyPress { get; set; }
    public SignalStats? usePress { get; set; }
    public SignalStats? sysFlow { get; set; }

    public List<DiagnosisRuleResult> aRules { get; set; } = [];
    /// <summary>重疊旗標（p_l_overlap / s2_s6_overlap）：節能不可直接相加</summary>
    public List<string> aOverlapFlags { get; set; } = [];
    /// <summary>全部規則用到的未確認預設參數（聯集）</summary>
    public List<string> aUnconfirmedDefaults { get; set; } = [];
    /// <summary>劣化基準期未設定 → 頁面提示</summary>
    public bool isBaselinePeriodMissing { get; set; }
}
