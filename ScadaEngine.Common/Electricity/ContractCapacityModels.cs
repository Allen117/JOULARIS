namespace ScadaEngine.Common.Electricity;

/// <summary>
/// 契約容量回測 — 單一計費月（電費期別）的輸入。
/// 經常契約為搜尋變數；其餘契約別（非夏月 / 半尖峰 / 週六半尖峰 + 離峰）依現行值固定計入。
/// 跨夏月與非夏月的計費月以 <see cref="dSummerShare"/>（夏月日數占比）加權兩季費用。
/// </summary>
public sealed record ContractMonthInput
{
    /// <summary>顯示標籤（如 2026-05）</summary>
    public string szLabel { get; init; } = string.Empty;

    /// <summary>當月尖峰時間（非時間電價 = 全時段）台電固定 15 分鐘窗口最高需量 kW；null = 無資料</summary>
    public double? dMaxDemandKw { get; init; }

    /// <summary>是否列入回測（窗口完整率達門檻、有適用電價方案）；false 的月份不列入任何合計與建議</summary>
    public bool isEvaluable { get; init; } = true;

    /// <summary>夏月日數占比 0~1（全夏月 = 1、全非夏月 = 0）</summary>
    public double dSummerShare { get; init; }

    /// <summary>經常契約單價（元/kW·月）— 夏月</summary>
    public double dRegularPriceSummer { get; init; }

    /// <summary>經常契約單價（元/kW·月）— 非夏月</summary>
    public double dRegularPriceNonSummer { get; init; }

    /// <summary>當月有效的現行經常契約 kW；null = 未設定契約容量（無法計算現行費用）</summary>
    public double? dCurrentRegularKw { get; init; }

    /// <summary>非夏月契約 kW（二段式 / 非時間電價）— 只在非夏月計費並放寬尖峰超約門檻</summary>
    public double dNonSummerKw { get; init; }

    /// <summary>非夏月契約單價（元/kW·月，僅非夏月適用）</summary>
    public double dNonSummerPrice { get; init; }

    /// <summary>半尖峰契約 kW（三段式）— 固定計費，不影響尖峰超約門檻</summary>
    public double dSemiPeakKw { get; init; }

    public double dSemiPeakPriceSummer { get; init; }
    public double dSemiPeakPriceNonSummer { get; init; }

    /// <summary>週六半尖峰契約 + 離峰契約 kW 合計（兩者同單價）</summary>
    public double dSatOffKw { get; init; }

    public double dSatOffPriceSummer { get; init; }
    public double dSatOffPriceNonSummer { get; init; }
}

/// <summary>單月需量費用拆解（元）</summary>
public sealed record ContractMonthCost(
    double dRegularKw,
    double dOverKw,
    double dRegularBase,
    double dOtherBase,
    double dPenalty)
{
    /// <summary>需量費用合計 = 經常契約基本費 + 其他契約別基本費 + 超約附加費</summary>
    public double dTotal => dRegularBase + dOtherBase + dPenalty;

    public bool isOver => dOverKw > 1e-9;
}

/// <summary>回測期間合計（只加總可評估月份）</summary>
public sealed record ContractPeriodCost(
    double dRegularBase,
    double dOtherBase,
    double dPenalty,
    int nOverMonths,
    int nEvaluableMonths)
{
    public double dTotal => dRegularBase + dOtherBase + dPenalty;
}

/// <summary>成本曲線一點</summary>
public sealed record ContractCurvePoint(double dRegularKw, double dBase, double dPenalty)
{
    public double dTotal => dBase + dPenalty;
}
