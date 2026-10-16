using System.Text.Json;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Web.Features.ContractCapacity.Models;
using ScadaEngine.Web.Features.TariffSetting.Models;
using ScadaEngine.Web.Services;
using Sample = ScadaEngine.Web.Services.ContractCapacityService.DemandSample;

namespace ScadaEngine.Tests.Electricity;

/// <summary>
/// 契約容量回測整條管線（無 DB）：DemandData 樣本 → 計費月 × 台電 seed 方案（hv_tou2）× 契約歷史 → 建議值。
/// 以設計預覽情境建構一年每 15 分鐘的樣本，驗證結果與計算核心測試的金額一致。
/// </summary>
public class ContractCapacityPipelineTests
{
    private static readonly (int y, int m, double d)[] PreviewPeaks =
    [
        (2025, 10, 1236), (2025, 11, 1088), (2025, 12, 1042), (2026, 1, 1015), (2026, 2, 968), (2026, 3, 1074),
        (2026, 4, 1142), (2026, 5, 1226), (2026, 6, 1318), (2026, 7, 1372), (2026, 8, 1396), (2026, 9, 1301),
    ];

    private static TariffConfig SeedConfig()
    {
        var szPath = Path.Combine(AppContext.BaseDirectory, "Setting", "tariff-taipower-defaults.json");
        var config = JsonSerializer.Deserialize<TariffConfig>(File.ReadAllText(szPath))!;
        config.adoptions = [new TariffAdoption { szEffectiveDate = "2000-01-01", szPlanId = "hv_tou2" }];
        config.contracts = [new ContractCapacityRecord { szEffectiveDate = "2025-01-01", kw = new() { ["regular_kw"] = 1500 } }];
        return config;
    }

    private static List<BillingPeriodRange> NaturalMonths()
    {
        var list = new List<BillingPeriodRange>();
        for (var d = new DateTime(2025, 10, 1); d < new DateTime(2026, 10, 1); d = d.AddMonths(1))
            list.Add(new BillingPeriodRange { nYear = d.Year, nMonth = d.Month, dtStart = d, dtEndExclusive = d.AddMonths(1) });
        return list;
    }

    /// <summary>每 15 分鐘基載 500 kW；每月第一個週三 14:00–14:15 窗口為該月尖峰；每月第一個週日凌晨插 2000 kW 離峰尖峰（不應影響經常契約）</summary>
    private static List<Sample> BuildSamples()
    {
        var samples = new List<Sample>();
        var peaks = PreviewPeaks.ToDictionary(p => (p.y, p.m), p => p.d);
        for (var t = new DateTime(2025, 10, 1, 0, 15, 0); t <= new DateTime(2026, 10, 1); t = t.AddMinutes(15))
        {
            var ws = t.AddMinutes(-15);
            var dKw = 500.0;
            var firstWed = FirstOf(ws.Year, ws.Month, DayOfWeek.Wednesday);
            var firstSun = FirstOf(ws.Year, ws.Month, DayOfWeek.Sunday);
            if (ws == firstWed.AddHours(14)) dKw = peaks[(ws.Year, ws.Month)];
            if (ws == firstSun.AddHours(3)) dKw = 2000;
            samples.Add(new Sample { dtTimestamp = t, dDemandKw = dKw, nQuality = 1 });
        }
        return samples;
    }

    private static DateTime FirstOf(int y, int m, DayOfWeek dow)
    {
        var d = new DateTime(y, m, 1);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d;
    }

    private static ContractCapacityResult Run(TariffConfig config, List<Sample> samples)
    {
        var result = ContractCapacityService.NewResult(new ContractCapacityQuery
        {
            circuitId = 1, from = "2025-10", to = "2026-09", roundKw = 10, completenessPct = 95,
        });
        result.szCircuitName = "台電總表";
        ContractCapacityService.ComputeResult(result, NaturalMonths(), config, [], samples, new DateTime(2026, 10, 16));
        return result;
    }

    [Fact]
    public void PreviewScenario_EndToEnd()
    {
        var r = Run(SeedConfig(), BuildSamples());

        Assert.True(r.isSuccess);
        Assert.Equal(12, r.nEvaluableMonths);
        Assert.Equal(1396, r.dMaxDemandKw);
        Assert.Equal("hv_tou2", r.szPlanId);
        Assert.Equal(1500, r.dCurrentKw);
        Assert.Equal(3_429_450, Math.Round(r.current!.dTotal));
        Assert.Equal(1400, r.safe!.dKw);
        Assert.Equal(3_200_820, Math.Round(r.safe.dTotal));
        Assert.Equal(228_630, Math.Round(r.safe.dSaving!.Value));
        Assert.Equal(1236, r.optimum!.dKw);
        Assert.Equal(394_562, Math.Round(r.optimum.dSaving!.Value));
        Assert.Equal(4, r.optimum.nOverMonths);
        Assert.Equal(1194, r.nFlatLowKw);
        Assert.Equal(1310, r.nFlatHighKw);

        var oct = r.months[0];
        Assert.Equal(194.3, oct.dRegularPrice, 1);
        Assert.Equal(100, oct.dCompletenessPct, 6);
        Assert.Equal("2025-10-01 14:00", oct.szPeakAt);
        Assert.NotEmpty(r.curve);
    }

    [Fact]
    public void LowCompletenessMonth_NotEvaluable()
    {
        var samples = BuildSamples();
        // 2026-02 抽掉 10% 的樣本（但保留尖峰那筆）→ 完整率約 90% < 95%
        var feb = samples.Where(s => s.dtTimestamp.Year == 2026 && s.dtTimestamp.Month == 2 && s.dDemandKw == 500)
            .Where((_, i) => i % 10 == 0).ToHashSet();
        samples.RemoveAll(feb.Contains);
        var r = Run(SeedConfig(), samples);
        var febRow = r.months.Single(m => m.szLabel == "2026-02");
        Assert.False(febRow.isEvaluable);
        Assert.Equal("low_completeness", febRow.szReason);
        Assert.Equal(11, r.nEvaluableMonths);
    }

    [Fact]
    public void ContractChangedMidBacktest_CurrentUsesEachMonth()
    {
        var config = SeedConfig();
        config.contracts.Add(new ContractCapacityRecord { szEffectiveDate = "2026-04-01", kw = new() { ["regular_kw"] = 1450 } });
        var r = Run(config, BuildSamples());
        Assert.True(r.isCurrentChanged);
        Assert.Equal(1450, r.dCurrentKw);
        Assert.Equal(1500, r.months[0].dCurrentKw);
        Assert.Equal(1450, r.months[^1].dCurrentKw);
    }

    [Fact]
    public void NoContract_RecommendationsWithoutSavings()
    {
        var config = SeedConfig();
        config.contracts = [];
        var r = Run(config, BuildSamples());
        Assert.Null(r.current);
        Assert.Equal(1400, r.safe!.dKw);
        Assert.Null(r.safe.dSaving);
    }
}
