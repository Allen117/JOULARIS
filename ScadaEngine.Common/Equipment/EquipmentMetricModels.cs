namespace ScadaEngine.Common.Equipment;

/// <summary>效率評級（主指標越低越好：≤ Good 優、≤ Fair 可、&gt; Fair 差）</summary>
public enum EquipmentGrade
{
    /// <summary>資料不足（缺點位值 / 斷線 / 產出為 0 無法相除）</summary>
    NoData = 0,
    /// <summary>停機</summary>
    Off,
    Good,
    Fair,
    Poor,
}

/// <summary>冰機即時輸入（皆已換算到基準單位；null = 未綁或無值）</summary>
public sealed record ChillerInstantInput
{
    public double? dKw { get; init; }
    /// <summary>冷量 RT（呼叫端已依「流量 + ΔT 優先、RT 點備選」決定）</summary>
    public double? dRt { get; init; }
    public double? dChws { get; init; }
    public double? dChwr { get; init; }
    public double? dCdws { get; init; }
    public double? dCdwr { get; init; }
    public double? dEvapT { get; init; }
    public double? dCondT { get; init; }
    public double? dRun { get; init; }
    public double? dLoadPct { get; init; }
    public double? dRatedRt { get; init; }
    public double? dRatedKw { get; init; }
    public double? dGood { get; init; }
    public double? dFair { get; init; }
}

/// <summary>冰機即時結果</summary>
public sealed record ChillerInstantResult
{
    public bool isRunning { get; init; }
    public double? dKwPerRt { get; init; }
    public double? dCop { get; init; }
    public double? dEer { get; init; }
    public double? dLoadPct { get; init; }
    public double? dChwDeltaT { get; init; }
    public double? dCdwDeltaT { get; init; }
    public double? dEvapApproach { get; init; }
    public double? dCondApproach { get; init; }
    public EquipmentGrade grade { get; init; }
}

/// <summary>空壓機即時輸入（皆已換算到基準單位）</summary>
public sealed record CompressorInstantInput
{
    public double? dKw { get; init; }
    /// <summary>空氣量 m³/min</summary>
    public double? dFlow { get; init; }
    public double? dPressBar { get; init; }
    public double? dTempC { get; init; }
    public double? dLoaded { get; init; }
    public double? dRun { get; init; }
    public double? dLoadPct { get; init; }
    public double? dRatedKw { get; init; }
    public double? dRatedFlow { get; init; }
    public double? dGood { get; init; }
    public double? dFair { get; init; }
}

/// <summary>空壓機即時結果</summary>
public sealed record CompressorInstantResult
{
    public bool isRunning { get; init; }
    /// <summary>比功率 kW/(m³/min)</summary>
    public double? dSpecificPower { get; init; }
    /// <summary>kW/100 cfm（CAGI 慣用）</summary>
    public double? dKwPer100Cfm { get; init; }
    public double? dLoadPct { get; init; }
    public bool? isLoaded { get; init; }
    public double? dPressBar { get; init; }
    public double? dTempC { get; init; }
    public double? dFlow { get; init; }
    public EquipmentGrade grade { get; init; }
}

/// <summary>期間（累計）指標 — 能量加權：ΣkWh ÷ Σ產出</summary>
public sealed record EquipmentPeriodMetric
{
    public double dKwh { get; init; }
    /// <summary>冰機 RT·h / 空壓機 m³</summary>
    public double dOutput { get; init; }
    public double dRunHours { get; init; }
    /// <summary>主指標：冰機 kW/RT、空壓機 kW/(m³/min)</summary>
    public double? dPrimary { get; init; }
    /// <summary>冰機 COP；空壓機 kW/100cfm</summary>
    public double? dSecondary { get; init; }
    /// <summary>冰機 EER；空壓機比能耗 kWh/m³</summary>
    public double? dTertiary { get; init; }
}
