namespace ScadaEngine.Common.Electricity;

/// <summary>
/// 契約容量回測純函式 — 輸入逐月最高需量、單價與固定契約別，輸出需量費用、保守建議與最低成本點。
/// 無 DB 依賴，由單元測試鎖住（金額會直接寫進提案，被默默改壞的代價很高）。
///
/// 規則出處：台電「電價表」（114 年 11 月 17 日經濟部經授能字第 11400334600 號函同意備查）
///   - 超約附加費：超出契約容量 10% 以下部分按 2 倍、超過 10% 部分按 3 倍計收基本電費（第四、五章 七、超約用電）
///   - 二段式尖峰超約門檻：夏月 = 經常契約；非夏月 = 經常 + 非夏月契約
///   - 週六半尖峰 / 離峰契約基本費 = 單價 × max((週六半尖峰 + 離峰) − (經常 + 非夏月或半尖峰) × 0.5, 0)
///   - 非夏月契約只在非夏月計費
/// 本版只評估尖峰時間超約（經常契約）；半尖峰 / 週六半尖峰 / 離峰時間的超約不計入（說明書已註明限制）。
/// 跨夏月與非夏月的計費月：兩季各算一次費用，以夏月日數占比加權（本系統慣例，電價表未明載）。
/// </summary>
public static class ContractCapacityCalculator
{
    /// <summary>超約 10% 以下部分倍率</summary>
    public const double PenaltyRateTier1 = 2.0;

    /// <summary>超約超過 10% 部分倍率</summary>
    public const double PenaltyRateTier2 = 3.0;

    /// <summary>超約級距（契約容量的 10%）</summary>
    public const double PenaltyTierRatio = 0.1;

    /// <summary>週六半尖峰 / 離峰契約扣抵係數</summary>
    public const double SatOffOffsetRatio = 0.5;

    /// <summary>成本曲線平底區間預設容差（與最低點差 ≤ 1%）</summary>
    public const double DefaultFlatTolerance = 0.01;

    private const double Epsilon = 1e-9;

    // ─────────────────────────── 單月 ───────────────────────────

    /// <summary>單月需量費用（以指定經常契約 kW 計）。最高需量 null 視為 0（只計基本費）。</summary>
    public static ContractMonthCost MonthCost(ContractMonthInput m, double dRegularKw)
    {
        var dShare = Math.Clamp(m.dSummerShare, 0, 1);
        var summer = dShare > Epsilon ? SeasonCost(m, dRegularKw, isSummer: true) : default;
        var nonSummer = dShare < 1 - Epsilon ? SeasonCost(m, dRegularKw, isSummer: false) : default;

        var dOver = Math.Max(dShare > Epsilon ? summer.dOver : 0, dShare < 1 - Epsilon ? nonSummer.dOver : 0);
        return new ContractMonthCost(
            dRegularKw,
            dOver,
            dShare * summer.dRegularBase + (1 - dShare) * nonSummer.dRegularBase,
            dShare * summer.dOtherBase + (1 - dShare) * nonSummer.dOtherBase,
            dShare * summer.dPenalty + (1 - dShare) * nonSummer.dPenalty);
    }

    private readonly record struct SeasonResult(double dOver, double dRegularBase, double dOtherBase, double dPenalty);

    private static SeasonResult SeasonCost(ContractMonthInput m, double dC, bool isSummer)
    {
        var dRegularPrice = isSummer ? m.dRegularPriceSummer : m.dRegularPriceNonSummer;
        var dRegularBase = dC * dRegularPrice;

        // 其他契約別：非夏月契約只在非夏月計費；週六半尖峰 + 離峰扣抵 (經常 + 非夏月或半尖峰) × 0.5
        var dOther = 0.0;
        if (!isSummer) dOther += m.dNonSummerKw * m.dNonSummerPrice;
        dOther += m.dSemiPeakKw * (isSummer ? m.dSemiPeakPriceSummer : m.dSemiPeakPriceNonSummer);
        var dSatOffBillable = Math.Max(m.dSatOffKw - (dC + m.dNonSummerKw + m.dSemiPeakKw) * SatOffOffsetRatio, 0);
        dOther += dSatOffBillable * (isSummer ? m.dSatOffPriceSummer : m.dSatOffPriceNonSummer);

        // 尖峰超約：門檻夏月 = 經常；非夏月 = 經常 + 非夏月契約
        var dThreshold = dC + (isSummer ? 0 : m.dNonSummerKw);
        var dDemand = m.dMaxDemandKw ?? 0;
        var dOver = Math.Max(dDemand - dThreshold, 0);
        var dTier1 = Math.Min(dOver, PenaltyTierRatio * dThreshold);
        var dTier2 = Math.Max(dDemand - (1 + PenaltyTierRatio) * dThreshold, 0);
        var dPenalty = dRegularPrice * (PenaltyRateTier1 * dTier1 + PenaltyRateTier2 * dTier2);

        return new SeasonResult(dOver, dRegularBase, dOther, dPenalty);
    }

    // ─────────────────────────── 期間 ───────────────────────────

    /// <summary>
    /// 回測期間合計（只加總可評估月份）。
    /// dRegularKw = null → 逐月套用當月有效的現行契約；任一可評估月份未設定現行契約時回 null。
    /// </summary>
    public static ContractPeriodCost? PeriodCost(IReadOnlyList<ContractMonthInput> months, double? dRegularKw)
    {
        double dBase = 0, dOther = 0, dPenalty = 0;
        int nOver = 0, nEval = 0;
        foreach (var m in months)
        {
            if (!m.isEvaluable) continue;
            var dC = dRegularKw ?? m.dCurrentRegularKw;
            if (dC == null) return null;
            var cost = MonthCost(m, dC.Value);
            dBase += cost.dRegularBase;
            dOther += cost.dOtherBase;
            dPenalty += cost.dPenalty;
            if (cost.isOver) nOver++;
            nEval++;
        }
        return new ContractPeriodCost(dBase, dOther, dPenalty, nOver, nEval);
    }

    /// <summary>期間總費用（給搜尋用，固定 C）</summary>
    private static double PeriodTotal(IReadOnlyList<ContractMonthInput> months, double dC)
    {
        var dSum = 0.0;
        foreach (var m in months)
        {
            if (!m.isEvaluable) continue;
            dSum += MonthCost(m, dC).dTotal;
        }
        return dSum;
    }

    // ─────────────────────────── 建議值 ───────────────────────────

    /// <summary>
    /// 保守建議 — 回測期每個可評估月份都不超約的最小經常契約，無條件進位至 nRoundKw。
    /// 非全夏月的月份可扣除非夏月契約（非夏月門檻 = 經常 + 非夏月）。無可評估月份回 null。
    /// </summary>
    public static double? SafeContract(IReadOnlyList<ContractMonthInput> months, int nRoundKw)
    {
        var nRound = Math.Max(nRoundKw, 1);
        double? dRequired = null;
        foreach (var m in months)
        {
            if (!m.isEvaluable || m.dMaxDemandKw == null) continue;
            var dD = m.dMaxDemandKw.Value;
            var dShare = Math.Clamp(m.dSummerShare, 0, 1);
            var dNeed = dShare > Epsilon ? dD : dD - m.dNonSummerKw;
            dRequired = dRequired == null ? dNeed : Math.Max(dRequired.Value, dNeed);
        }
        if (dRequired == null) return null;
        var dRounded = Math.Ceiling(Math.Max(dRequired.Value, 0) / nRound - Epsilon) * nRound;
        return Math.Max(dRounded, nRound);
    }

    /// <summary>最低成本搜尋結果 + 平底區間（與最低點差 ≤ 容差的經常契約範圍）</summary>
    public sealed record OptimumResult(int nMinKw, double dMinCost, int nFlatLowKw, int nFlatHighKw);

    /// <summary>
    /// 以 1 kW 為步距在 [1, ceil(回測期最高需量)+1] 全域搜尋最低成本經常契約（同成本取較小值）。
    /// 高於最高需量後不再有超約、基本費隨 C 單調增加，因此上界足夠。無可評估月份回 null。
    /// </summary>
    public static OptimumResult? FindOptimum(IReadOnlyList<ContractMonthInput> months,
        double dFlatTolerance = DefaultFlatTolerance)
    {
        var dMaxDemand = MaxEvaluableDemand(months);
        if (dMaxDemand == null || dMaxDemand.Value <= 0) return null;

        var nUpper = (int)Math.Ceiling(dMaxDemand.Value) + 1;
        var costs = new double[nUpper + 1];
        var nMin = 1;
        var dMin = double.MaxValue;
        for (var c = 1; c <= nUpper; c++)
        {
            costs[c] = PeriodTotal(months, c);
            if (costs[c] < dMin - Epsilon)
            {
                dMin = costs[c];
                nMin = c;
            }
        }

        var dLimit = dMin * (1 + dFlatTolerance) + Epsilon;
        int nLo = nMin, nHi = nMin;
        for (var c = 1; c <= nUpper; c++)
        {
            if (costs[c] > dLimit) continue;
            if (c < nLo) nLo = c;
            if (c > nHi) nHi = c;
        }
        return new OptimumResult(nMin, dMin, nLo, nHi);
    }

    /// <summary>成本曲線（[dFromKw, dToKw]，約 nPoints 點；步距取整 kW）</summary>
    public static List<ContractCurvePoint> CostCurve(IReadOnlyList<ContractMonthInput> months,
        double dFromKw, double dToKw, int nPoints = 120)
    {
        var list = new List<ContractCurvePoint>();
        if (dToKw <= dFromKw || nPoints < 2) return list;
        var dStep = Math.Max(1, Math.Ceiling((dToKw - dFromKw) / nPoints));
        for (var c = Math.Max(1, Math.Floor(dFromKw)); c <= dToKw + Epsilon; c += dStep)
        {
            var cost = PeriodCost(months, c)!;
            list.Add(new ContractCurvePoint(c, cost.dRegularBase + cost.dOtherBase, cost.dPenalty));
        }
        return list;
    }

    // ─────────────────────────── 工具 ───────────────────────────

    /// <summary>可評估月份的最高需量；無則 null</summary>
    public static double? MaxEvaluableDemand(IReadOnlyList<ContractMonthInput> months)
    {
        double? dMax = null;
        foreach (var m in months)
        {
            if (!m.isEvaluable || m.dMaxDemandKw == null) continue;
            dMax = dMax == null ? m.dMaxDemandKw : Math.Max(dMax.Value, m.dMaxDemandKw.Value);
        }
        return dMax;
    }

    /// <summary>夏月日數占比：[dtStart, dtEndExclusive) 內 isSummer(日) 為 true 的天數 ÷ 總天數</summary>
    public static double SummerShare(DateTime dtStart, DateTime dtEndExclusive, Func<DateTime, bool> isSummer)
    {
        var nTotal = 0;
        var nSummer = 0;
        for (var d = dtStart.Date; d < dtEndExclusive.Date; d = d.AddDays(1))
        {
            nTotal++;
            if (isSummer(d)) nSummer++;
        }
        return nTotal == 0 ? 0 : (double)nSummer / nTotal;
    }

    /// <summary>依夏月占比加權的單價（顯示用）</summary>
    public static double WeightedPrice(double dSummer, double dNonSummer, double dSummerShare) =>
        dSummer * dSummerShare + dNonSummer * (1 - dSummerShare);
}
