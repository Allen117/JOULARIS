using ScadaEngine.Common.Electricity;
using ScadaEngine.Web.Features.TariffSetting.Models;
using ScadaEngine.Web.Services;
using Sample = ScadaEngine.Web.Services.ContractCapacityService.DemandSample;

namespace ScadaEngine.Tests.Electricity;

/// <summary>
/// 契約容量回測 — 台電固定 15 分鐘窗口過濾、窗口完整率、尖峰時段篩選、方案 → 計算輸入組裝。
/// </summary>
public class ContractCapacityWindowTests
{
    private static readonly DateTime Start = new(2026, 7, 1);
    private static readonly DateTime End = new(2026, 7, 2);   // 1 天 = 96 個窗口
    private static readonly DateTime FarFuture = new(2030, 1, 1);

    private static Sample S(DateTime t, double kw, byte q = 1) => new() { dtTimestamp = t, dDemandKw = kw, nQuality = q };

    private static List<Sample> FullDay(double kw)
    {
        var list = new List<Sample>();
        for (var t = Start.AddMinutes(15); t <= End; t = t.AddMinutes(15)) list.Add(S(t, kw));
        return list;
    }

    [Fact]
    public void OnlyQuarterHourSamples_CountAsWindows()
    {
        var samples = FullDay(100);
        samples.Add(S(Start.AddHours(10).AddMinutes(7), 999));   // 非整刻 → 忽略
        var stats = ContractCapacityService.SummarizeWindows(samples, Start, End, FarFuture, _ => true, null);
        Assert.Equal(96, stats.nExpected);
        Assert.Equal(96, stats.nGood);
        Assert.Equal(100, stats.dMaxKw);
        Assert.Equal(1.0, stats.dCompleteness, 9);
    }

    [Fact]
    public void WindowBoundary_StartExcluded_EndIncluded()
    {
        // Timestamp = 期別起點 00:00 代表前一期的最後一窗 → 不算；Timestamp = 訖點 00:00 代表本期最後一窗 → 算
        var samples = new List<Sample> { S(Start, 500), S(End, 200) };
        var stats = ContractCapacityService.SummarizeWindows(samples, Start, End, FarFuture, _ => true, null);
        Assert.Equal(1, stats.nGood);
        Assert.Equal(200, stats.dMaxKw);
        Assert.Equal(End.AddMinutes(-15), stats.dtPeakWindowStart);
    }

    [Fact]
    public void BadQualityAndNegative_AreMissing_OutliersExcluded()
    {
        var samples = FullDay(100);
        samples[0] = S(samples[0].dtTimestamp, 300, q: 0);
        samples[1] = S(samples[1].dtTimestamp, -5);
        samples[2] = S(samples[2].dtTimestamp, 99999);
        var stats = ContractCapacityService.SummarizeWindows(samples, Start, End, FarFuture, _ => true, dMaxValidKw: 5000);
        Assert.Equal(93, stats.nGood);
        Assert.Equal(1, stats.nExcluded);
        Assert.Equal(100, stats.dMaxKw);
        Assert.Equal(93 / 96.0, stats.dCompleteness, 9);
    }

    [Fact]
    public void OngoingPeriod_ExpectedUpToNow()
    {
        var now = Start.AddHours(6).AddMinutes(7);   // 06:07 → 取整 06:00 → 24 窗
        var stats = ContractCapacityService.SummarizeWindows(FullDay(100), Start, End, now, _ => true, null);
        Assert.Equal(24, stats.nExpected);
        Assert.Equal(24, stats.nGood);
    }

    [Fact]
    public void MaxDemand_OnlyFromBillingWindows_ButCompletenessCountsAll()
    {
        var samples = FullDay(100);
        // 凌晨 03:00 窗口高峰，但非尖峰時段
        var idx = samples.FindIndex(x => x.dtTimestamp == Start.AddHours(3).AddMinutes(15));
        samples[idx] = S(samples[idx].dtTimestamp, 800);
        var stats = ContractCapacityService.SummarizeWindows(samples, Start, End, FarFuture,
            ws => ws.Hour >= 9, null);
        Assert.Equal(100, stats.dMaxKw);
        Assert.Equal(96, stats.nGood);
    }

    [Fact]
    public void ResolvePeriodAt_Hv2Tier_WeekdaySummerPeak()
    {
        var plan = new TariffPlan
        {
            szPlanId = "hv_tou2", szType = "tou", szSummerStart = "05-16", szSummerEnd = "10-15",
            flowRates =
            [
                new() { szDayType = "weekday", szSeason = "summer", szPeriod = "peak", ranges = ["09:00-24:00"] },
                new() { szDayType = "weekday", szSeason = "summer", szPeriod = "offpeak", ranges = ["00:00-09:00"] },
                new() { szDayType = "sat", szSeason = "summer", szPeriod = "semipeak", ranges = ["09:00-24:00"] },
                new() { szDayType = "sat", szSeason = "summer", szPeriod = "offpeak", ranges = ["00:00-09:00"] },
            ],
        };
        var holidays = new HashSet<DateTime>();
        Assert.Equal("peak", ElectricityCostService.ResolvePeriodAt(plan, new DateTime(2026, 7, 1, 13, 45, 0), holidays)); // 週三
        Assert.Equal("offpeak", ElectricityCostService.ResolvePeriodAt(plan, new DateTime(2026, 7, 1, 8, 45, 0), holidays));
        Assert.Equal("semipeak", ElectricityCostService.ResolvePeriodAt(plan, new DateTime(2026, 7, 4, 13, 45, 0), holidays)); // 週六
    }

    [Fact]
    public void BuildInput_OnlyBillsContractTypesInPlan()
    {
        var plan = new TariffPlan
        {
            szPlanId = "hv_tou2",
            baseFees =
            [
                new() { szKey = "regular_kw", szUnit = "kw", dSummer = 223.6, dNonSummer = 166.9 },
                new() { szKey = "nonsummer_kw", szUnit = "kw", dSummer = null, dNonSummer = 166.9 },
                new() { szKey = "sat_semipeak_kw", szUnit = "kw", dSummer = 44.7, dNonSummer = 33.3 },
                new() { szKey = "offpeak_kw", szUnit = "kw", dSummer = 44.7, dNonSummer = 33.3 },
            ],
        };
        var contract = new ContractCapacityRecord
        {
            szEffectiveDate = "2025-01-01",
            kw = new() { ["regular_kw"] = 1500, ["nonsummer_kw"] = 100, ["semipeak_kw"] = 999, ["sat_semipeak_kw"] = 50, ["offpeak_kw"] = 200 },
        };
        var input = ContractCapacityService.BuildInput("2026-07", plan, contract, 1300, 1, true);
        Assert.Equal(1500, input.dCurrentRegularKw);
        Assert.Equal(100, input.dNonSummerKw);
        Assert.Equal(0, input.dSemiPeakKw);          // 二段式方案無半尖峰契約 → 不計
        Assert.Equal(250, input.dSatOffKw);
        Assert.Equal(44.7, input.dSatOffPriceSummer);
        Assert.Equal(223.6, input.dRegularPriceSummer);

        // 夏月：經常 1500×223.6 + 週六半尖峰/離峰 max(250 − (1500+100)×0.5, 0)=0
        Assert.Equal(1500 * 223.6, ContractCapacityCalculator.MonthCost(input, 1500).dTotal, 6);
    }

    [Fact]
    public void BuildInput_NoContract_CurrentIsNull()
    {
        var plan = new TariffPlan { baseFees = [new() { szKey = "regular_kw", dSummer = 1, dNonSummer = 1 }] };
        Assert.Null(ContractCapacityService.BuildInput("x", plan, null, 1, 1, true).dCurrentRegularKw);
    }
}
