using ScadaEngine.Common.Electricity;

namespace ScadaEngine.Tests.Electricity;

/// <summary>
/// 契約容量回測計算核心 — 以設計預覽情境（高壓二段式、現行 1,500 kW、12 個月最高需量 968~1,396 kW）鎖住金額。
/// 單價 = tariff-taipower-defaults.json hv_tou2.regular_kw（夏月 223.6 / 非夏月 166.9），夏月 5/16~10/15。
/// </summary>
public class ContractCapacityCalculatorTests
{
    private const double PS = 223.6, PN = 166.9;

    private static ContractMonthInput M(string szLabel, double dDemand, double dShare, double? dCurrent = 1500,
        bool isEvaluable = true) => new()
    {
        szLabel = szLabel,
        dMaxDemandKw = dDemand,
        dSummerShare = dShare,
        dRegularPriceSummer = PS,
        dRegularPriceNonSummer = PN,
        dCurrentRegularKw = dCurrent,
        isEvaluable = isEvaluable,
    };

    private static List<ContractMonthInput> PreviewMonths() =>
    [
        M("2025-10", 1236, 15 / 31.0),
        M("2025-11", 1088, 0),
        M("2025-12", 1042, 0),
        M("2026-01", 1015, 0),
        M("2026-02", 968, 0),
        M("2026-03", 1074, 0),
        M("2026-04", 1142, 0),
        M("2026-05", 1226, 16 / 31.0),
        M("2026-06", 1318, 1),
        M("2026-07", 1372, 1),
        M("2026-08", 1396, 1),
        M("2026-09", 1301, 1),
    ];

    // ── 單月 ──

    [Fact]
    public void MonthCost_NotOver_OnlyBaseFee()
    {
        var c = ContractCapacityCalculator.MonthCost(M("x", 900, 1), 1000);
        Assert.Equal(1000 * PS, c.dTotal, 6);
        Assert.Equal(0, c.dPenalty);
        Assert.False(c.isOver);
    }

    [Fact]
    public void MonthCost_ExactlyAtContract_NotOver()
    {
        var c = ContractCapacityCalculator.MonthCost(M("x", 1000, 1), 1000);
        Assert.Equal(0, c.dPenalty);
        Assert.False(c.isOver);
    }

    [Fact]
    public void MonthCost_OverWithin10Percent_DoubleRate()
    {
        // 超約 50 kW（≤ 10% × 1000）→ 50 × 2 × 單價
        var c = ContractCapacityCalculator.MonthCost(M("x", 1050, 1), 1000);
        Assert.Equal(50, c.dOverKw, 6);
        Assert.Equal(50 * 2 * PS, c.dPenalty, 6);
        Assert.Equal(1000 * PS + 50 * 2 * PS, c.dTotal, 6);
    }

    [Fact]
    public void MonthCost_OverBeyond10Percent_DoubleThenTriple()
    {
        // 超約 150 kW：前 100 kW × 2 倍 + 後 50 kW × 3 倍
        var c = ContractCapacityCalculator.MonthCost(M("x", 1150, 0), 1000);
        Assert.Equal(150, c.dOverKw, 6);
        Assert.Equal(PN * (100 * 2 + 50 * 3), c.dPenalty, 6);
    }

    [Fact]
    public void MonthCost_CrossSeason_WeightsByDays()
    {
        // 2025-10：夏月 15/31 天 → 加權單價 194.3
        var m = M("2025-10", 0, 15 / 31.0);
        Assert.Equal(194.3, ContractCapacityCalculator.WeightedPrice(PS, PN, m.dSummerShare), 1);
        Assert.Equal(196.2, ContractCapacityCalculator.WeightedPrice(PS, PN, 16 / 31.0), 1);
        var c = ContractCapacityCalculator.MonthCost(m, 1000);
        Assert.Equal(1000 * (PS * 15 / 31.0 + PN * 16 / 31.0), c.dTotal, 6);
    }

    [Fact]
    public void SummerShare_CountsDays()
    {
        static bool IsSummer(DateTime d) => d.Month * 100 + d.Day is >= 516 and <= 1015;
        Assert.Equal(15 / 31.0, ContractCapacityCalculator.SummerShare(new DateTime(2025, 10, 1), new DateTime(2025, 11, 1), IsSummer), 9);
        Assert.Equal(16 / 31.0, ContractCapacityCalculator.SummerShare(new DateTime(2026, 5, 1), new DateTime(2026, 6, 1), IsSummer), 9);
        Assert.Equal(1, ContractCapacityCalculator.SummerShare(new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), IsSummer), 9);
    }

    [Fact]
    public void MonthCost_NonSummerContract_RaisesThresholdAndBilledOnlyInNonSummer()
    {
        var m = M("x", 1100, 0) with { dNonSummerKw = 100, dNonSummerPrice = PN };
        // 非夏月門檻 1000 + 100 = 1100 → 不超約；非夏月契約計 100 × 166.9
        var c = ContractCapacityCalculator.MonthCost(m, 1000);
        Assert.False(c.isOver);
        Assert.Equal(100 * PN, c.dOtherBase, 6);

        // 夏月：非夏月契約不計費、門檻只有經常契約 → 超約 100
        var s = ContractCapacityCalculator.MonthCost(m with { dSummerShare = 1 }, 1000);
        Assert.Equal(0, s.dOtherBase, 6);
        Assert.Equal(100, s.dOverKw, 6);
    }

    [Fact]
    public void MonthCost_SatOffContract_OffsetByHalfRegular()
    {
        // 週六半尖峰 + 離峰 800 kW，經常 1000 → 800 − 500 = 300 kW 計費
        var m = M("x", 0, 1) with { dSatOffKw = 800, dSatOffPriceSummer = 44.7, dSatOffPriceNonSummer = 33.3 };
        Assert.Equal(300 * 44.7, ContractCapacityCalculator.MonthCost(m, 1000).dOtherBase, 6);
        // 經常 2000 → 800 − 1000 < 0 → 按 0
        Assert.Equal(0, ContractCapacityCalculator.MonthCost(m, 2000).dOtherBase, 6);
    }

    // ── 預覽情境年金額 ──

    [Fact]
    public void PreviewScenario_YearCosts()
    {
        var months = PreviewMonths();
        Assert.Equal(3_429_450, Math.Round(ContractCapacityCalculator.PeriodCost(months, 1500)!.dTotal));
        Assert.Equal(3_200_820, Math.Round(ContractCapacityCalculator.PeriodCost(months, 1400)!.dTotal));
        Assert.Equal(3_034_888, Math.Round(ContractCapacityCalculator.PeriodCost(months, 1236)!.dTotal));
        // null = 逐月套用現行契約（全年 1,500）
        Assert.Equal(3_429_450, Math.Round(ContractCapacityCalculator.PeriodCost(months, null)!.dTotal));
    }

    [Fact]
    public void PreviewScenario_SafeAndOptimum()
    {
        var months = PreviewMonths();
        Assert.Equal(1400, ContractCapacityCalculator.SafeContract(months, 10));
        var opt = ContractCapacityCalculator.FindOptimum(months)!;
        Assert.Equal(1236, opt.nMinKw);
        Assert.Equal(3_034_888, Math.Round(opt.dMinCost));
        Assert.Equal(1194, opt.nFlatLowKw);
        Assert.Equal(1310, opt.nFlatHighKw);
        Assert.Equal(4, ContractCapacityCalculator.PeriodCost(months, opt.nMinKw)!.nOverMonths);
    }

    [Fact]
    public void SafeContract_RoundsUp()
    {
        var months = new List<ContractMonthInput> { M("a", 1391, 1) };
        Assert.Equal(1400, ContractCapacityCalculator.SafeContract(months, 10));
        Assert.Equal(1400, ContractCapacityCalculator.SafeContract([M("a", 1400, 1)], 10));
        Assert.Equal(1391, ContractCapacityCalculator.SafeContract(months, 1));
    }

    // ── 不可評估 / 契約變更 ──

    [Fact]
    public void NonEvaluableMonth_ExcludedFromTotalsAndRecommendations()
    {
        var months = PreviewMonths();
        // 加一個資料不完整的異常尖峰月 → 不列入
        months.Add(M("2026-10", 9999, 0, isEvaluable: false));
        Assert.Equal(1400, ContractCapacityCalculator.SafeContract(months, 10));
        Assert.Equal(1236, ContractCapacityCalculator.FindOptimum(months)!.nMinKw);
        var cost = ContractCapacityCalculator.PeriodCost(months, 1500)!;
        Assert.Equal(12, cost.nEvaluableMonths);
        Assert.Equal(3_429_450, Math.Round(cost.dTotal));
    }

    [Fact]
    public void CurrentContractChange_AppliedPerMonth()
    {
        var months = new List<ContractMonthInput>
        {
            M("a", 900, 1, dCurrent: 1500),
            M("b", 900, 1, dCurrent: 1000),
        };
        var cur = ContractCapacityCalculator.PeriodCost(months, null)!;
        Assert.Equal(1500 * PS + 1000 * PS, cur.dTotal, 6);
    }

    [Fact]
    public void CurrentContractMissing_ReturnsNull()
    {
        var months = new List<ContractMonthInput> { M("a", 900, 1, dCurrent: null) };
        Assert.Null(ContractCapacityCalculator.PeriodCost(months, null));
        Assert.NotNull(ContractCapacityCalculator.PeriodCost(months, 1000));
    }

    [Fact]
    public void NoEvaluableMonth_NoRecommendation()
    {
        var months = new List<ContractMonthInput> { M("a", 900, 1, isEvaluable: false) };
        Assert.Null(ContractCapacityCalculator.SafeContract(months, 10));
        Assert.Null(ContractCapacityCalculator.FindOptimum(months));
    }
}
