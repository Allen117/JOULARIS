using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Common.Electricity;
using ScadaEngine.Web.Features.ContractCapacity.Models;
using ScadaEngine.Web.Features.TariffSetting.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 契約容量最佳化回測 — 取需量 → 每計費月固定窗口最高需量（含窗口完整率）→ 套單價與契約歷史 → 呼叫
/// <see cref="ContractCapacityCalculator"/>。詳見 docs/功能說明書_契約容量最佳化.md。
///
/// - 計費月 = 電費期別（BillingPeriods；未自訂時為曆月），與電費報表一致
/// - 台電固定 15 分鐘窗口：DemandData 每分鐘以 ΔkWh×4 算「過去 15 分鐘」，因此 Timestamp 分鐘 ∈ {0,15,30,45}
///   的樣本即等於窗口 [Timestamp−15, Timestamp) 的需量；只取 Quality=1 且非負值
/// - 經常契約比較的是尖峰時間需量（tou 方案依時段表判定窗口起點所屬時段）；非時間電價與電動車方案取全時段
/// - 每個計費月的電價方案與現行契約都取「期別起日」當天有效者
/// </summary>
public class ContractCapacityService
{
    /// <summary>單次回測計費月數上限</summary>
    public const int MaxMonths = 24;

    /// <summary>固定窗口長度（分鐘）</summary>
    public const int WindowMinutes = 15;

    private readonly ILogger<ContractCapacityService> _logger;
    private readonly DatabaseConfigService _configService;
    private readonly TariffSettingService _tariffService;
    private readonly HolidayService _holidayService;
    private readonly BillingPeriodService _billingPeriodService;
    private string _szConnectionString = string.Empty;

    public ContractCapacityService(
        ILogger<ContractCapacityService> logger,
        DatabaseConfigService configService,
        TariffSettingService tariffService,
        HolidayService holidayService,
        BillingPeriodService billingPeriodService)
    {
        _logger = logger;
        _configService = configService;
        _tariffService = tariffService;
        _holidayService = holidayService;
        _billingPeriodService = billingPeriodService;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>可選迴路 = 啟用需量計算且綁定 kWh 點位的電表（主要電表排最前）</summary>
    public async Task<List<ContractCircuitOption>> GetCircuitsAsync()
    {
        const string szSql = @"
            SELECT Id AS nId, Name AS szName, IsMainMeter AS isMainMeter
            FROM   EnergyCircuit
            WHERE  IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''
            ORDER  BY IsMainMeter DESC, SortOrder, Id";
        using var conn = await GetConnectionAsync();
        return (await conn.QueryAsync<ContractCircuitOption>(szSql)).ToList();
    }

    /// <summary>執行回測</summary>
    public async Task<ContractCapacityResult> AnalyzeAsync(ContractCapacityQuery query)
    {
        var result = NewResult(query);

        if (!TryParseYearMonth(query.from, out var dtFromYM) || !TryParseYearMonth(query.to, out var dtToYM) ||
            dtToYM < dtFromYM)
            return Fail(result, "invalid_range");
        var nMonthCount = (dtToYM.Year - dtFromYM.Year) * 12 + dtToYM.Month - dtFromYM.Month + 1;
        if (nMonthCount > MaxMonths)
            return Fail(result, "range_too_large");

        using var conn = await GetConnectionAsync();
        var circuit = await conn.QueryFirstOrDefaultAsync<(string Name, string SID)>(@"
            SELECT Name, SID FROM EnergyCircuit
            WHERE  Id = @Id AND IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''",
            new { Id = query.circuitId });
        if (circuit.SID == null)
            return Fail(result, "circuit_not_found");
        result.szCircuitName = circuit.Name;

        var periods = await _billingPeriodService.GetPeriodRangesAsync(dtFromYM, dtToYM);
        var config = await _tariffService.GetConfigAsync();
        var holidays = await _holidayService.GetAllAsync();

        var dtQueryStart = periods.Min(p => p.dtStart);
        var dtQueryEnd = periods.Max(p => p.dtEndExclusive);
        var samples = (await conn.QueryAsync<DemandSample>(@"
            SELECT [Timestamp] AS dtTimestamp, DemandKW AS dDemandKw, Quality AS nQuality
            FROM   DemandData WITH (NOLOCK)
            WHERE  SID = @SID AND [Timestamp] > @dtQueryStart AND [Timestamp] <= @dtQueryEnd
                   AND DATEPART(MINUTE, [Timestamp]) % 15 = 0",
            new { circuit.SID, dtQueryStart, dtQueryEnd }, commandTimeout: 120)).ToList();

        ComputeResult(result, periods, config, holidays, samples, DateTime.Now);

        _logger.LogInformation(
            "契約容量回測：迴路 {Circuit} {From}~{To}，可評估 {Eval}/{Total} 月，保守 {Safe} kW、最低成本 {Min} kW",
            result.szCircuitName, query.from, query.to, result.nEvaluableMonths, result.nMonths,
            result.safe?.dKw, result.optimum?.dKw);
        return result;
    }

    /// <summary>查詢條件 → 結果骨架（參數正規化）</summary>
    public static ContractCapacityResult NewResult(ContractCapacityQuery query) => new()
    {
        nCircuitId = query.circuitId,
        szFrom = query.from,
        szTo = query.to,
        nRoundKw = query.roundKw > 0 ? query.roundKw : 10,
        dCompletenessPct = Math.Clamp(query.completenessPct, 0, 100),
        dMaxValidKw = query.maxValidKw is > 0 ? query.maxValidKw : null,
    };

    /// <summary>
    /// 回測主體（無 DB）：計費月 × 方案 / 契約選版 × 固定窗口統計 → 計算核心 → 填入 result。
    /// 與 DB 讀取分離，讓「需量樣本 → 建議值」整條管線可離線測試。
    /// </summary>
    public static void ComputeResult(ContractCapacityResult result, IReadOnlyList<BillingPeriodRange> periods,
        TariffConfig config, HashSet<DateTime> holidays, IReadOnlyList<DemandSample> samples, DateTime dtNow)
    {
        var inputs = new List<ContractMonthInput>(periods.Count);
        var planIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TariffPlan? lastPlan = null;

        foreach (var period in periods)
        {
            var month = new ContractMonthDto
            {
                szLabel = BillingPeriodService.BuildLabel(period),
                szStart = period.dtStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                szEnd = period.dtEndInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            result.months.Add(month);

            var plan = TariffSettingService.SelectPlanForDate(config, period.dtStart);
            var regularFee = plan?.baseFees.FirstOrDefault(f => f.szKey == "regular_kw");
            var contract = TariffSettingService.SelectContractForDate(config, period.dtStart);

            // 窗口篩選：tou（電動車除外）只取尖峰時間；其餘全時段
            var isPeakOnly = plan != null && plan.szType == "tou" &&
                             !plan.szPlanId.EndsWith("_ev", StringComparison.OrdinalIgnoreCase);
            var stats = SummarizeWindows(samples, period.dtStart, period.dtEndExclusive, dtNow,
                dtWindowStart => !isPeakOnly ||
                                 ElectricityCostService.ResolvePeriodAt(plan!, dtWindowStart, holidays) == "peak",
                result.dMaxValidKw);

            month.nExpectedWindows = stats.nExpected;
            month.nGoodWindows = stats.nGood;
            month.nExcludedWindows = stats.nExcluded;
            month.dCompletenessPct = stats.dCompleteness * 100;
            month.dMaxDemandKw = stats.dMaxKw;
            month.szPeakAt = stats.dtPeakWindowStart?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            if (plan == null) month.szReason = "no_plan";
            else if (regularFee == null) month.szReason = "no_demand_tariff";
            else if (month.dCompletenessPct < result.dCompletenessPct) month.szReason = "low_completeness";
            else if (stats.dMaxKw == null) month.szReason = "no_data";
            month.isEvaluable = month.szReason == null;

            var dShare = plan == null ? 0 : ContractCapacityCalculator.SummerShare(
                period.dtStart, period.dtEndExclusive, d => ElectricityCostService.ResolveSeason(plan, d));
            month.dSummerShare = dShare;

            var input = BuildInput(month.szLabel, plan, contract, stats.dMaxKw, dShare, month.isEvaluable);
            month.dRegularPrice = ContractCapacityCalculator.WeightedPrice(
                input.dRegularPriceSummer, input.dRegularPriceNonSummer, dShare);
            month.dCurrentKw = input.dCurrentRegularKw;
            inputs.Add(input);

            if (plan != null)
            {
                planIds.Add(plan.szPlanId);
                lastPlan = plan;
            }
        }

        result.szPlanId = lastPlan?.szPlanId;
        result.szPlanName = lastPlan != null && TariffSettingService.IsCustom(lastPlan) ? lastPlan.szName : null;
        result.isMultiplePlans = planIds.Count > 1;
        result.nMonths = inputs.Count;
        result.nEvaluableMonths = inputs.Count(m => m.isEvaluable);
        result.dMaxDemandKw = ContractCapacityCalculator.MaxEvaluableDemand(inputs);

        var currentKws = inputs.Where(m => m.isEvaluable && m.dCurrentRegularKw != null)
            .Select(m => m.dCurrentRegularKw!.Value).Distinct().ToList();
        result.dCurrentKw = inputs.LastOrDefault(m => m.dCurrentRegularKw != null)?.dCurrentRegularKw;
        result.isCurrentChanged = currentKws.Count > 1;

        FillScenarios(result, inputs);
        result.isSuccess = true;
    }

    // ─────────────────────────── 組裝 ───────────────────────────

    /// <summary>方案 + 契約紀錄 → 計算核心輸入（只計入方案 baseFees 有的契約別）</summary>
    public static ContractMonthInput BuildInput(string szLabel, TariffPlan? plan, ContractCapacityRecord? contract,
        double? dMaxDemandKw, double dSummerShare, bool isEvaluable)
    {
        TariffBaseFee? Fee(string szKey) => plan?.baseFees.FirstOrDefault(f => f.szKey == szKey);
        double Kw(string szKey) => Fee(szKey) != null ? TariffSettingService.ContractKw(contract, szKey) : 0;

        var regular = Fee("regular_kw");
        var nonSummer = Fee("nonsummer_kw");
        var semiPeak = Fee("semipeak_kw");
        var satOffPrice = Fee("sat_semipeak_kw") ?? Fee("offpeak_kw");

        return new ContractMonthInput
        {
            szLabel = szLabel,
            dMaxDemandKw = dMaxDemandKw,
            isEvaluable = isEvaluable,
            dSummerShare = dSummerShare,
            dRegularPriceSummer = regular?.dSummer ?? 0,
            dRegularPriceNonSummer = regular?.dNonSummer ?? 0,
            dCurrentRegularKw = contract != null && regular != null
                ? TariffSettingService.ContractKw(contract, "regular_kw")
                : null,
            dNonSummerKw = Kw("nonsummer_kw"),
            dNonSummerPrice = nonSummer?.dNonSummer ?? 0,
            dSemiPeakKw = Kw("semipeak_kw"),
            dSemiPeakPriceSummer = semiPeak?.dSummer ?? 0,
            dSemiPeakPriceNonSummer = semiPeak?.dNonSummer ?? 0,
            dSatOffKw = Kw("sat_semipeak_kw") + Kw("offpeak_kw"),
            dSatOffPriceSummer = satOffPrice?.dSummer ?? 0,
            dSatOffPriceNonSummer = satOffPrice?.dNonSummer ?? 0,
        };
    }

    private static void FillScenarios(ContractCapacityResult result, List<ContractMonthInput> inputs)
    {
        if (result.nEvaluableMonths == 0) return;

        var currentCost = ContractCapacityCalculator.PeriodCost(inputs, null);
        var dSafe = ContractCapacityCalculator.SafeContract(inputs, result.nRoundKw);
        var optimum = ContractCapacityCalculator.FindOptimum(inputs);

        if (currentCost != null && result.dCurrentKw != null)
            result.current = ToScenario(result.dCurrentKw.Value, currentCost, null);
        if (dSafe != null)
            result.safe = ToScenario(dSafe.Value, ContractCapacityCalculator.PeriodCost(inputs, dSafe.Value)!, currentCost);
        if (optimum != null)
        {
            result.optimum = ToScenario(optimum.nMinKw,
                ContractCapacityCalculator.PeriodCost(inputs, optimum.nMinKw)!, currentCost);
            result.nFlatLowKw = optimum.nFlatLowKw;
            result.nFlatHighKw = optimum.nFlatHighKw;
        }

        // 逐月三方案
        for (var i = 0; i < inputs.Count; i++)
        {
            var m = inputs[i];
            var dto = result.months[i];
            if (m.dCurrentRegularKw != null) dto.cur = ToMonthCost(ContractCapacityCalculator.MonthCost(m, m.dCurrentRegularKw.Value));
            if (dSafe != null) dto.safe = ToMonthCost(ContractCapacityCalculator.MonthCost(m, dSafe.Value));
            if (optimum != null) dto.optimum = ToMonthCost(ContractCapacityCalculator.MonthCost(m, optimum.nMinKw));
        }

        // 成本曲線：涵蓋最低成本 / 保守 / 現行三條線
        var candidates = new List<double>();
        if (optimum != null) candidates.Add(optimum.nMinKw);
        if (dSafe != null) candidates.Add(dSafe.Value);
        if (result.dCurrentKw is > 0) candidates.Add(result.dCurrentKw.Value);
        if (candidates.Count > 0)
        {
            var dLo = Math.Floor(candidates.Min() * 0.8);
            var dHi = Math.Ceiling(candidates.Max() * 1.1);
            result.curve = ContractCapacityCalculator.CostCurve(inputs, dLo, dHi)
                .Select(p => new ContractCurvePointDto { dKw = p.dRegularKw, dBase = p.dBase, dPenalty = p.dPenalty })
                .ToList();
        }
    }

    private static ContractScenarioDto ToScenario(double dKw, ContractPeriodCost cost, ContractPeriodCost? current)
    {
        var dto = new ContractScenarioDto
        {
            dKw = dKw,
            dTotal = cost.dTotal,
            dRegularBase = cost.dRegularBase,
            dOtherBase = cost.dOtherBase,
            dPenalty = cost.dPenalty,
            nOverMonths = cost.nOverMonths,
        };
        if (current != null)
        {
            dto.dSaving = current.dTotal - cost.dTotal;
            dto.dSavingPct = current.dTotal > 0 ? dto.dSaving / current.dTotal * 100 : null;
        }
        return dto;
    }

    private static ContractMonthCostDto ToMonthCost(ContractMonthCost c) =>
        new() { dOverKw = c.dOverKw, dPenalty = c.dPenalty, dTotal = c.dTotal };

    // ─────────────────────────── 固定窗口統計（純函式，單元測試用） ───────────────────────────

    /// <summary>DemandData 一列（僅整刻樣本）</summary>
    public sealed record DemandSample
    {
        public DateTime dtTimestamp { get; init; }
        public double dDemandKw { get; init; }
        public byte nQuality { get; init; }
    }

    /// <summary>單一計費月的固定窗口統計</summary>
    public sealed record MonthDemandStats(double? dMaxKw, DateTime? dtPeakWindowStart, int nExpected, int nGood, int nExcluded)
    {
        /// <summary>窗口完整率 0~1 = 有效窗口數 ÷ 應有窗口數</summary>
        public double dCompleteness => nExpected == 0 ? 0 : (double)nGood / nExpected;
    }

    /// <summary>
    /// 統計 [dtStart, dtEndExclusive) 內台電固定 15 分鐘窗口：
    /// - 窗口 [t−15, t) 由 Timestamp = t（分鐘 ∈ {0,15,30,45}、秒 = 0）的樣本代表，t ∈ (dtStart, 有效訖]
    /// - 有效訖 = min(dtEndExclusive, 現在往下取整刻) — 進行中的月份只算已過的窗口
    /// - 有效樣本：Quality=1 且需量 ≥ 0；超過 dMaxValidKw 的視為電表跳動，計入排除數、不算有效
    /// - 最高需量只看 isBillingWindow(窗口起點) 為 true 的窗口（尖峰時間）
    /// </summary>
    public static MonthDemandStats SummarizeWindows(IEnumerable<DemandSample> samples,
        DateTime dtStart, DateTime dtEndExclusive, DateTime dtNow,
        Func<DateTime, bool> isBillingWindow, double? dMaxValidKw)
    {
        var dtNowFloor = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, dtNow.Hour,
            dtNow.Minute / WindowMinutes * WindowMinutes, 0);
        var dtEffEnd = dtEndExclusive < dtNowFloor ? dtEndExclusive : dtNowFloor;
        var nExpected = dtEffEnd > dtStart ? (int)((dtEffEnd - dtStart).TotalMinutes / WindowMinutes) : 0;

        double? dMax = null;
        DateTime? dtPeak = null;
        int nGood = 0, nExcluded = 0;
        foreach (var s in samples)
        {
            var t = s.dtTimestamp;
            if (t <= dtStart || t > dtEffEnd) continue;
            if (t.Minute % WindowMinutes != 0 || t.Second != 0 || t.Millisecond != 0) continue;
            if (s.nQuality != 1 || double.IsNaN(s.dDemandKw) || s.dDemandKw < 0) continue;
            if (dMaxValidKw != null && s.dDemandKw > dMaxValidKw.Value)
            {
                nExcluded++;
                continue;
            }

            nGood++;
            var dtWindowStart = t.AddMinutes(-WindowMinutes);
            if (!isBillingWindow(dtWindowStart)) continue;
            if (dMax == null || s.dDemandKw > dMax.Value)
            {
                dMax = s.dDemandKw;
                dtPeak = dtWindowStart;
            }
        }
        return new MonthDemandStats(dMax, dtPeak, nExpected, nGood, nExcluded);
    }

    // ─────────────────────────── 工具 ───────────────────────────

    private static ContractCapacityResult Fail(ContractCapacityResult result, string szError)
    {
        result.isSuccess = false;
        result.szError = szError;
        return result;
    }

    private static bool TryParseYearMonth(string? sz, out DateTime dt) =>
        DateTime.TryParseExact(sz?.Trim() ?? string.Empty, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out dt);
}
