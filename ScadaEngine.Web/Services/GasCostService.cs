using ScadaEngine.Web.Features.GasCostReport.Models;
using ScadaEngine.Web.Features.GasTariffSetting.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 氣費計算 — 對「該迴路自身期別總用氣量」套天然氣分段累進級距（1 度 = 1 m³）。
/// 每迴路獨立套級距（不做占比分攤 — 與電費 progressive 的根迴路分攤不同，
/// 天然氣表迴路彼此獨立計量，各自就是一只錶）。
/// 方案版本依「期別起日」選用（GasTariffService.SelectPlanForDate）；
/// 計費週期走**氣費**期別（GasBillingPeriodService，支援兩月一期，與電/水費期別各自獨立）；
/// 用氣量來源 GasUsageReportService（isHasWarning → isStale 註記）。
///
/// ⚠️ 級距與期別長度**解耦**：兩月一期時使用者在費率頁直接填「一期」的級距數字，
///    本服務不做任何依期別天數/月數的倍率換算（決策 7）。
/// </summary>
public class GasCostService
{
    private readonly GasTariffService _tariffService;
    private readonly GasUsageReportService _usageService;
    private readonly GasMeterCircuitService _circuitService;
    private readonly GasBillingPeriodService _billingPeriodService;
    private readonly ILogger<GasCostService> _logger;

    public GasCostService(
        GasTariffService tariffService,
        GasUsageReportService usageService,
        GasMeterCircuitService circuitService,
        GasBillingPeriodService billingPeriodService,
        ILogger<GasCostService> logger)
    {
        _tariffService = tariffService;
        _usageService = usageService;
        _circuitService = circuitService;
        _billingPeriodService = billingPeriodService;
        _logger = logger;
    }

    // ---------- static 純邏輯（單元測試用） ----------

    /// <summary>
    /// 分段累進套算 — 演算法同 WaterCostService.ApplyTiers：
    /// lower = max(0, nFrom-1)；upper = nTo ?? +∞；slice = min(total, upper) - lower；
    /// cost += slice × dPrice；total &lt;= lower 即 break。
    /// 回傳（總金額, 目前落點級距 index；總量 0 或空級距時 index=0）。
    /// </summary>
    public static (double dCost, int nTopTierIdx) ApplyTiers(double dTotalM3, List<GasTariffTier> tiers)
    {
        double dCost = 0;
        var nTopTierIdx = 0;
        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            var dLower = Math.Max(0, tier.nFrom - 1);              // 級距下界（度，exclusive 累計基準）
            var dUpper = tier.nTo.HasValue ? (double)tier.nTo.Value : double.MaxValue;
            if (dTotalM3 <= dLower) break;
            var dSlice = Math.Min(dTotalM3, dUpper) - dLower;
            dCost += dSlice * tier.dPrice;
            nTopTierIdx = i;
            if (dTotalM3 <= dUpper) break;
        }
        return (dCost, nTopTierIdx);
    }

    /// <summary>級距明細列 — 每級距的落點度數與金額（未達的級距 slice=0，全列輸出供 UI 顯示完整表）</summary>
    public static List<GasCostTierRowDto> BuildTierRows(double dTotalM3, List<GasTariffTier> tiers)
    {
        var rows = new List<GasCostTierRowDto>(tiers.Count);
        foreach (var tier in tiers)
        {
            var dLower = Math.Max(0, tier.nFrom - 1);
            var dUpper = tier.nTo.HasValue ? (double)tier.nTo.Value : double.MaxValue;
            var dSlice = Math.Max(0, Math.Min(dTotalM3, dUpper) - dLower);
            rows.Add(new GasCostTierRowDto
            {
                from = tier.nFrom,
                to = tier.nTo,
                price = tier.dPrice,
                sliceM3 = Math.Round(dSlice, 2),
                sliceCost = Math.Round(dSlice * tier.dPrice, 1),
            });
        }
        return rows;
    }

    // ---------- 查詢 ----------

    /// <summary>
    /// 本期氣費狀態（EMS 氣費狀態卡用）— nCircuitId = null 取根迴路；期別 = 今天所屬**氣費**月結期別。
    /// 無方案（hasPlan=false）時仍回用氣量，金額為 0。
    /// </summary>
    public async Task<GasCostStatusDto> GetStatusAsync(int? nCircuitId)
    {
        var dto = new GasCostStatusDto();

        var circuit = nCircuitId == null
            ? await _circuitService.GetRootAsync()
            : await _circuitService.GetByIdAsync(nCircuitId.Value);
        if (circuit == null) return dto;

        dto.circuitId = circuit.nId;
        dto.circuitName = circuit.szName;

        var period = await _billingPeriodService.GetCurrentPeriodAsync(DateTime.Today);
        dto.periodLabel = period.szLabel;
        dto.periodStart = period.dtStart;
        dto.periodEndExclusive = period.dtEndExclusive;

        var (dTotalM3, isHasWarning) = await _usageService.GetTotalM3Async(
            circuit.nId, period.dtStart, period.dtEndExclusive);
        dto.totalM3 = Math.Round(Math.Max(0, dTotalM3), 2);
        dto.isStale = isHasWarning;

        var config = await _tariffService.GetConfigAsync();
        var plan = GasTariffService.SelectPlanForDate(config, period.dtStart);
        if (plan == null) return dto;   // hasPlan = false

        dto.hasPlan = true;
        dto.planId = plan.szPlanId;
        dto.planName = plan.szName;
        dto.effectiveDate = plan.szEffectiveDate;

        var (dCost, _) = ApplyTiers(dto.totalM3, plan.tiers);
        dto.totalCost = Math.Round(dCost, 1);
        dto.tiers = BuildTierRows(dto.totalM3, plan.tiers);
        return dto;
    }

    /// <summary>
    /// 期別區間氣費 — [fromYM, toYM]（含頭尾）每期一列，各期依「期別起日」選用當時生效方案版本。
    /// 已刪除（skipped）的期別不會出現在結果中（兩月一期時一年 6 列）。
    /// </summary>
    public async Task<List<GasCostPeriodRow>> GetPeriodCostsAsync(
        int nCircuitId, int nFromYear, int nFromMonth, int nToYear, int nToMonth)
    {
        var circuit = await _circuitService.GetByIdAsync(nCircuitId)
            ?? throw new InvalidOperationException($"天然氣表迴路 Id={nCircuitId} 不存在");

        var periods = await _billingPeriodService.GetPeriodRangesAsync(
            new DateTime(nFromYear, nFromMonth, 1), new DateTime(nToYear, nToMonth, 1));
        var config = await _tariffService.GetConfigAsync();

        var rows = new List<GasCostPeriodRow>(periods.Count);
        foreach (var period in periods)
        {
            var (dTotalM3, isHasWarning) = await _usageService.GetTotalM3Async(
                circuit.nId, period.dtStart, period.dtEndExclusive);
            var dM3 = Math.Round(Math.Max(0, dTotalM3), 2);

            var row = new GasCostPeriodRow
            {
                periodYear = period.nYear,
                periodMonth = period.nMonth,
                periodLabel = period.szLabel,
                periodStart = period.dtStart,
                periodEnd = period.dtEndInclusive,
                totalM3 = dM3,
                isStale = isHasWarning,
            };

            var plan = GasTariffService.SelectPlanForDate(config, period.dtStart);
            if (plan != null)
            {
                row.planId = plan.szPlanId;
                row.planName = plan.szName;
                var (dCost, _) = ApplyTiers(dM3, plan.tiers);
                row.totalCost = Math.Round(dCost, 1);
                row.tiers = BuildTierRows(dM3, plan.tiers);
            }
            rows.Add(row);
        }
        return rows;
    }
}
