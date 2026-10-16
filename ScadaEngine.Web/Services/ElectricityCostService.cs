using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.ElectricityCostReport.Models;
using ScadaEngine.Web.Features.Ems.Models;
using ScadaEngine.Web.Features.TariffSetting.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 電費計算核心 — 逐時計價（EnergyLeafHourly → ElectricityCostHourly）+ 查詢彙總（EMS 電費狀態卡）。
///
/// 計價規則（詳見 docs/功能說明書_電費設定.md §電費計算）：
/// - 一列 = 一葉子 × 一小時 × 一時段；kWh 快照自 EnergyLeafHourly.DeltaKwh（未套 sign，查詢時走 EffectiveSign 加總）
/// - tou：依（日別 × 季節）時段表落段，邊界非整點的小時按分鐘比例分攤拆列；DayType 判定假日優先於星期
///   （Holidays 表 → sun_offday；週六 → sat；週日 → sun_offday；其餘 → weekday）
/// - flat：整小時單一時段 all，單價依季節
/// - progressive：只存 kWh（Period=all，UnitPrice/Cost=NULL）— 累進單價是月總度數的函數，查詢時套級距
/// - 重算依「採用時間軸」逐日選版（TariffSettingService.SelectPlanForDate）覆蓋區間（DELETE + INSERT，分塊交易）；
///   某天查無適用方案 → 該天跳過不產列（改費率不會追溯改寫歷史電費）
/// </summary>
public class ElectricityCostService
{
    /// <summary>非 tou 方案的統一時段代碼</summary>
    public const string PeriodAll = "all";

    /// <summary>重算單次交易的分塊天數（避免長交易鎖表）</summary>
    private const int ChunkDays = 7;

    /// <summary>單筆重算區間上限（天）— 防呆</summary>
    public const int MaxRecalculateDays = 366;

    private readonly ILogger<ElectricityCostService> _logger;
    private readonly DatabaseConfigService _configService;
    private readonly TariffSettingService _tariffService;
    private readonly HolidayService _holidayService;
    private readonly EnergyCircuitService _circuitService;
    private readonly BillingPeriodService _billingPeriodService;
    private readonly EnergyReportService _reportService;
    private string _szConnectionString = string.Empty;

    public ElectricityCostService(
        ILogger<ElectricityCostService> logger,
        DatabaseConfigService configService,
        TariffSettingService tariffService,
        HolidayService holidayService,
        EnergyCircuitService circuitService,
        BillingPeriodService billingPeriodService,
        EnergyReportService reportService)
    {
        _logger = logger;
        _configService = configService;
        _tariffService = tariffService;
        _holidayService = holidayService;
        _circuitService = circuitService;
        _billingPeriodService = billingPeriodService;
        _reportService = reportService;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ════════════════════════════════════════════════════════════
    //  計價核心（寫入 ElectricityCostHourly）
    // ════════════════════════════════════════════════════════════

    /// <summary>重算結果摘要</summary>
    public record RecalculateResult(bool isSuccess, string? szError, int nHours, int nRows,
        DateTime dtFrom, DateTime dtToExclusive);

    /// <summary>
    /// 依採用時間軸「逐日選版」重算區間 [dtFrom, dtToExclusive) 的電費（DELETE + INSERT 覆蓋，7 天一塊分交易）。
    /// 區間內完全沒有適用方案時回 isSuccess=false（不動既有資料）；部分天數無方案則該天不產列。
    /// 時間自動截整點。
    /// </summary>
    public async Task<RecalculateResult> RecalculateRangeAsync(
        DateTime dtFrom, DateTime dtToExclusive, CancellationToken ct = default)
    {
        dtFrom = TruncateHour(dtFrom);
        dtToExclusive = TruncateHour(dtToExclusive);
        if (dtToExclusive <= dtFrom)
            return new RecalculateResult(false, "invalid_range", 0, 0, dtFrom, dtToExclusive);
        if ((dtToExclusive - dtFrom).TotalDays > MaxRecalculateDays)
            return new RecalculateResult(false, "range_too_large", 0, 0, dtFrom, dtToExclusive);

        var config = await _tariffService.GetConfigAsync();
        // 逐日快取選版（同一天內共用同一方案，避免每小時重查）；區間 <= 366 天故最多 367 筆
        var planByDay = BuildPlanByDay(config, dtFrom, dtToExclusive);
        if (planByDay.Count == 0)
            return new RecalculateResult(false, "no_active_plan", 0, 0, dtFrom, dtToExclusive);

        var holidays = await _holidayService.GetAllAsync();

        var nTotalHours = 0;
        var nTotalRows = 0;
        using var conn = await GetConnectionAsync();

        for (var dtChunk = dtFrom; dtChunk < dtToExclusive; dtChunk = dtChunk.AddDays(ChunkDays))
        {
            ct.ThrowIfCancellationRequested();
            var dtChunkEnd = dtChunk.AddDays(ChunkDays) < dtToExclusive ? dtChunk.AddDays(ChunkDays) : dtToExclusive;

            var sourceRows = (await conn.QueryAsync<(string SID, DateTime HourStart, double DeltaKwh, byte Quality)>(@"
                SELECT SID, HourStart, DeltaKwh, Quality
                FROM   EnergyLeafHourly WITH (NOLOCK)
                WHERE  HourStart >= @dtChunk AND HourStart < @dtChunkEnd",
                new { dtChunk, dtChunkEnd })).ToList();

            var costRows = new List<CostRow>(sourceRows.Count * 2);
            var pricedHours = new HashSet<DateTime>();
            foreach (var src in sourceRows)
            {
                // 該日未採用任何方案 → 不產列。搭配下方無條件 DELETE，效果是「該日舊列被清掉且不重寫」。
                // 這是刻意的：時間軸上沒有採用方案的日子，留著舊方案算出的金額等於讓錯誤數字無聲存活，
                // 比缺資料更糟。且本表是衍生資料 —— 來源 EnergyLeafHourly 未被動到，
                // 使用者補上該期間的採用紀錄後重算即可完整還原（含 Kwh 快照）。
                if (!planByDay.TryGetValue(src.HourStart.Date, out var plan)) continue;
                AppendCostRows(costRows, plan, holidays, src.SID, src.HourStart, src.DeltaKwh, src.Quality);
                pricedHours.Add(src.HourStart);
            }

            using var tran = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "DELETE FROM ElectricityCostHourly WHERE HourStart >= @dtChunk AND HourStart < @dtChunkEnd",
                    new { dtChunk, dtChunkEnd }, tran);
                await BulkInsertAsync(conn, tran, costRows);
                tran.Commit();
            }
            catch
            {
                tran.Rollback();
                throw;
            }

            nTotalHours += pricedHours.Count;
            nTotalRows += costRows.Count;
        }

        var szPlanIds = string.Join(",", planByDay.Values.Select(p => p.szPlanId).Distinct());
        _logger.LogInformation(
            "電費重算完成：{From:yyyy-MM-dd HH:00} ~ {To:yyyy-MM-dd HH:00}，方案 {PlanIds}，小時數 {Hours}、寫入 {Rows} 列",
            dtFrom, dtToExclusive, szPlanIds, nTotalHours, nTotalRows);
        return new RecalculateResult(true, null, nTotalHours, nTotalRows, dtFrom, dtToExclusive);
    }

    /// <summary>
    /// 建立「日期 → 該日適用方案」對照（無適用方案的日子不入表）。
    /// 選版邏輯集中在 TariffSettingService.SelectPlanForDate，本處只負責逐日展開與快取。
    /// </summary>
    private static Dictionary<DateTime, TariffPlan> BuildPlanByDay(
        TariffConfig config, DateTime dtFrom, DateTime dtToExclusive)
    {
        var map = new Dictionary<DateTime, TariffPlan>();
        var dtLastDay = dtToExclusive.AddTicks(-1).Date;
        for (var dtDay = dtFrom.Date; dtDay <= dtLastDay; dtDay = dtDay.AddDays(1))
        {
            var plan = TariffSettingService.SelectPlanForDate(config, dtDay);
            if (plan != null) map[dtDay] = plan;
        }
        return map;
    }

    private record CostRow(string szSID, DateTime dtHourStart, string szPeriod,
        double dKwh, double? dUnitPrice, double? dCost,
        string szPlanId, string szPlanType, string szSeason, byte nQuality);

    /// <summary>單一（葉子 × 小時）→ 產出 1~N 列計價結果（tou 跨時段邊界的小時拆多列）</summary>
    private void AppendCostRows(List<CostRow> output, TariffPlan plan, HashSet<DateTime> holidays,
        string szSid, DateTime dtHourStart, double dDeltaKwh, byte nQuality)
    {
        var dtDay = dtHourStart.Date;
        var szSeason = ResolveSeason(plan, dtDay) ? "summer" : "nonsummer";

        switch (plan.szType)
        {
            case "flat":
                {
                    var dPrice = szSeason == "summer" ? plan.flatRate?.dSummer ?? 0 : plan.flatRate?.dNonSummer ?? 0;
                    output.Add(new CostRow(szSid, dtHourStart, PeriodAll, dDeltaKwh, dPrice, dDeltaKwh * dPrice,
                        plan.szPlanId, plan.szType, szSeason, nQuality));
                    return;
                }
            case "progressive":
                // 累進單價是月總度數的函數 → 只存 kWh，查詢時套級距
                output.Add(new CostRow(szSid, dtHourStart, PeriodAll, dDeltaKwh, null, null,
                    plan.szPlanId, plan.szType, szSeason, nQuality));
                return;
            case "tou":
                break;
            default:
                return;
        }

        // ── tou：分鐘級落段 ──────────────────────────────
        var szDayType = ResolveDayType(dtDay, holidays);
        var spans = BuildDaySpans(plan, szDayType, szSeason);
        var nHourStartMin = dtHourStart.Hour * 60;
        var nHourEndMin = nHourStartMin + 60;

        // 同時段可能被邊界切成多段（如 07:30 邊界）→ 以時段代碼合併 kWh 與金額
        var byPeriod = new Dictionary<string, (double dKwh, double dCost, double dPrice)>();
        foreach (var span in spans)
        {
            var nOverlap = Math.Min(span.nEndMin, nHourEndMin) - Math.Max(span.nStartMin, nHourStartMin);
            if (nOverlap <= 0) continue;
            var dShare = dDeltaKwh * nOverlap / 60.0;
            if (byPeriod.TryGetValue(span.szPeriod, out var acc))
                byPeriod[span.szPeriod] = (acc.dKwh + dShare, acc.dCost + dShare * span.dPrice, span.dPrice);
            else
                byPeriod[span.szPeriod] = (dShare, dShare * span.dPrice, span.dPrice);
        }

        foreach (var (szPeriod, v) in byPeriod)
        {
            // 同時段被多列費率覆蓋時以金額回推有效單價（正常情況 = 該時段單價）
            var dUnit = v.dKwh > 1e-12 ? v.dCost / v.dKwh : v.dPrice;
            output.Add(new CostRow(szSid, dtHourStart, szPeriod, v.dKwh, dUnit, v.dCost,
                plan.szPlanId, plan.szType, szSeason, nQuality));
        }
    }

    /// <summary>批次 INSERT（每 200 列一句，10 參數/列 &lt; SQL Server 2100 參數上限）</summary>
    private static async Task BulkInsertAsync(SqlConnection conn, SqlTransaction tran, List<CostRow> rows)
    {
        const int BatchSize = 200;
        for (var nOffset = 0; nOffset < rows.Count; nOffset += BatchSize)
        {
            var batch = rows.Skip(nOffset).Take(BatchSize).ToList();
            var sb = new StringBuilder(
                "INSERT INTO ElectricityCostHourly (SID, HourStart, Period, Kwh, UnitPrice, Cost, PlanId, PlanType, Season, Quality) VALUES ");
            var dynParams = new DynamicParameters();
            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@s{i},@h{i},@p{i},@k{i},@u{i},@c{i},@pi{i},@pt{i},@se{i},@q{i})");
                var r = batch[i];
                dynParams.Add($"@s{i}", r.szSID);
                dynParams.Add($"@h{i}", r.dtHourStart);
                dynParams.Add($"@p{i}", r.szPeriod);
                dynParams.Add($"@k{i}", r.dKwh);
                dynParams.Add($"@u{i}", r.dUnitPrice);
                dynParams.Add($"@c{i}", r.dCost);
                dynParams.Add($"@pi{i}", r.szPlanId);
                dynParams.Add($"@pt{i}", r.szPlanType);
                dynParams.Add($"@se{i}", r.szSeason);
                dynParams.Add($"@q{i}", (int)r.nQuality);
            }
            await conn.ExecuteAsync(sb.ToString(), dynParams, tran);
        }
    }

    // ── 落段工具 ─────────────────────────────────────────

    /// <summary>指定日期是否落在方案夏月區間（MM-dd 含頭尾；起 &gt; 訖視為跨年）</summary>
    public static bool ResolveSeason(TariffPlan plan, DateTime dtDay)
    {
        if (!TryParseMonthDay(plan.szSummerStart, out var nSm, out var nSd) ||
            !TryParseMonthDay(plan.szSummerEnd, out var nEm, out var nEd))
            return false;
        var nDayKey = dtDay.Month * 100 + dtDay.Day;
        var nStartKey = nSm * 100 + nSd;
        var nEndKey = nEm * 100 + nEd;
        return nStartKey <= nEndKey
            ? nDayKey >= nStartKey && nDayKey <= nEndKey
            : nDayKey >= nStartKey || nDayKey <= nEndKey;
    }

    /// <summary>DayType 判定 — 假日優先於星期：Holidays → sun_offday；週六 → sat；週日 → sun_offday；其餘 → weekday</summary>
    public static string ResolveDayType(DateTime dtDay, HashSet<DateTime> holidays)
    {
        if (holidays.Contains(dtDay.Date)) return "sun_offday";
        return dtDay.DayOfWeek switch
        {
            DayOfWeek.Saturday => "sat",
            DayOfWeek.Sunday => "sun_offday",
            _ => "weekday",
        };
    }

    /// <summary>
    /// 指定時刻落在 tou 方案的哪個時段（peak / semipeak / offpeak）；非 tou 方案或查無時段回 null。
    /// 季節 / 日別判定同計價（ResolveSeason / ResolveDayType）。契約容量回測用來挑尖峰時間的需量窗口。
    /// </summary>
    public static string? ResolvePeriodAt(TariffPlan plan, DateTime dt, HashSet<DateTime> holidays)
    {
        if (plan.szType != "tou") return null;
        var dtDay = dt.Date;
        var szSeason = ResolveSeason(plan, dtDay) ? "summer" : "nonsummer";
        var nMinute = dt.Hour * 60 + dt.Minute;
        foreach (var span in BuildDaySpans(plan, ResolveDayType(dtDay, holidays), szSeason))
        {
            if (nMinute >= span.nStartMin && nMinute < span.nEndMin) return span.szPeriod;
        }
        return null;
    }

    private record DaySpan(int nStartMin, int nEndMin, string szPeriod, double dPrice);

    /// <summary>
    /// 展開（日別 × 季節）組的時段列為當日分鐘區間（跨午夜切兩段 — 兩段都屬於「被判定為該日別的日子」）。
    /// </summary>
    private static List<DaySpan> BuildDaySpans(TariffPlan plan, string szDayType, string szSeason)
    {
        var spans = new List<DaySpan>();
        foreach (var rate in plan.flowRates)
        {
            if (rate.szDayType != szDayType || rate.szSeason != szSeason) continue;
            foreach (var szRange in rate.ranges)
            {
                var parts = szRange.Split('-');
                if (parts.Length != 2 ||
                    !TryParseTimeToMinutes(parts[0], out var nStart) ||
                    !TryParseTimeToMinutes(parts[1], out var nEnd))
                    continue;
                if (nStart == nEnd) continue;
                if (nStart < nEnd)
                {
                    spans.Add(new DaySpan(nStart, nEnd, rate.szPeriod, rate.dPrice));
                }
                else
                {
                    spans.Add(new DaySpan(nStart, 1440, rate.szPeriod, rate.dPrice));
                    if (nEnd > 0) spans.Add(new DaySpan(0, nEnd, rate.szPeriod, rate.dPrice));
                }
            }
        }
        return spans;
    }

    private static bool TryParseTimeToMinutes(string szTime, out int nMinutes)
    {
        nMinutes = 0;
        var parts = szTime.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var nHour) || !int.TryParse(parts[1], out var nMin)) return false;
        if (nHour == 24 && nMin == 0) { nMinutes = 1440; return true; }
        if (nHour < 0 || nHour > 23 || nMin < 0 || nMin > 59) return false;
        nMinutes = nHour * 60 + nMin;
        return true;
    }

    private static bool TryParseMonthDay(string szMonthDay, out int nMonth, out int nDay)
    {
        nMonth = 0; nDay = 0;
        if (string.IsNullOrWhiteSpace(szMonthDay)) return false;
        var parts = szMonthDay.Split('-');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out nMonth) && int.TryParse(parts[1], out nDay);
    }

    private static DateTime TruncateHour(DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0);

    // ════════════════════════════════════════════════════════════
    //  查詢彙總（EMS 電費狀態卡）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 取得指定迴路（未指定 = 主要電表，無主要電表則第一個根節點）本期電費狀態。
    /// 方案依「本期起日」選版（跨版本換方案時卡片顯示的是本期實際適用的方案）。
    /// - tou / flat：kWh 與金額為葉子列 × EffectiveSign 精確加總
    /// - progressive：卡片級距金額只對根迴路精確；子迴路以 kWh 占比分攤（isEstimated=true）
    /// - surcharge（簡易型月總度數超額加價）：同 progressive 邏輯，門檻套根迴路總量
    /// </summary>
    public async Task<EmsElectricityCostDto> GetStatusAsync(int? nCircuitId)
    {
        var dto = new EmsElectricityCostDto();

        var config = await _tariffService.GetConfigAsync();
        var period = await _billingPeriodService.GetCurrentPeriodAsync(DateTime.Today);
        // 本期中途才首次採用（生效日晚於本期起日）時起日查無方案 → 退回今日採用方案，
        // 否則卡片顯示「尚未選擇方案」，而報表（逐日選版）卻已有生效日之後的金額
        var plan = TariffSettingService.SelectPlanForDate(config, period.dtStart)
            ?? TariffSettingService.SelectPlanForDate(config, DateTime.Today);
        if (plan == null) return dto;   // hasPlan = false

        dto.hasPlan = true;
        dto.planId = plan.szPlanId;
        dto.planType = plan.szType;
        dto.planCategory = plan.szCategory;

        // 根迴路 = 主要電表；未設定時取第一個根節點
        var root = await _circuitService.GetMainMeterAsync();
        if (root == null)
        {
            var all = (await _circuitService.GetAllAsync()).ToList();
            root = all.Where(c => c.nParentId == null).OrderBy(c => c.nSortOrder).ThenBy(c => c.nId).FirstOrDefault();
        }
        if (root == null) return dto;   // hasCircuit = false

        var target = nCircuitId == null ? root : await _circuitService.GetByIdAsync(nCircuitId.Value) ?? root;
        dto.hasCircuit = true;
        dto.circuitId = target.nId;
        dto.circuitName = target.szName;
        dto.isRootCircuit = target.nId == root.nId;
        dto.periodLabel = period.szLabel;

        using var conn = await GetConnectionAsync();

        // 目標迴路葉子（含 EffectiveSign）本期彙總
        var targetLeaves = await _circuitService.GetLeavesUnderAsync(target.nId);
        var targetAgg = await QueryPeriodAggAsync(conn, targetLeaves, period.dtStart, period.dtEndExclusive);
        dto.lastHour = targetAgg.dtLastHour?.ToString("yyyy-MM-dd HH:00");

        var dtToday = DateTime.Today;
        var todayAgg = dtToday >= period.dtStart && dtToday < period.dtEndExclusive
            ? await QueryPeriodAggAsync(conn, targetLeaves, dtToday, period.dtEndExclusive)
            : new PeriodAgg();

        dto.totalKwh = Math.Round(targetAgg.dTotalKwh, 2);
        dto.todayKwh = Math.Round(todayAgg.dTotalKwh, 2);

        switch (plan.szType)
        {
            case "tou":
                BuildTouStatus(dto, plan, targetAgg, todayAgg);
                break;
            case "flat":
                BuildFlatStatus(dto, plan, targetAgg, todayAgg);
                break;
            case "progressive":
                await BuildProgressiveStatusAsync(dto, plan, conn, root, target, targetAgg, period.dtStart, period.dtEndExclusive);
                break;
        }

        // 簡易型 tou 的月總度數超額加價（門檻套根迴路總量；子迴路占比分攤）
        if (plan.surcharge != null && plan.szType == "tou")
            await ApplySurchargeAsync(dto, plan, conn, root, target, targetAgg, period.dtStart, period.dtEndExclusive);

        return dto;
    }

    private static void BuildTouStatus(EmsElectricityCostDto dto, TariffPlan plan, PeriodAgg agg, PeriodAgg todayAgg)
    {
        // 方案定義的時段順序：peak → semipeak → offpeak（只列方案有定義的）
        string[] order = ["peak", "semipeak", "offpeak"];
        var defined = plan.flowRates.Select(r => r.szPeriod).Distinct().ToHashSet();
        double dTotalCost = 0;
        foreach (var szPeriod in order)
        {
            if (!defined.Contains(szPeriod)) continue;
            agg.byPeriod.TryGetValue(szPeriod, out var v);
            dto.periods.Add(new EmsCostPeriodItemDto
            {
                period = szPeriod,
                kwh = Math.Round(v.dKwh, 2),
                cost = Math.Round(v.dCost, 1),
            });
            dTotalCost += v.dCost;
        }
        dto.totalCost = Math.Round(dTotalCost, 1);
        dto.todayCost = Math.Round(todayAgg.byPeriod.Values.Sum(v => v.dCost), 1);
    }

    private static void BuildFlatStatus(EmsElectricityCostDto dto, TariffPlan plan, PeriodAgg agg, PeriodAgg todayAgg)
    {
        var isSummer = ResolveSeason(plan, DateTime.Today);
        dto.flat = new EmsCostFlatDto
        {
            season = isSummer ? "summer" : "nonsummer",
            unitPrice = isSummer ? plan.flatRate?.dSummer ?? 0 : plan.flatRate?.dNonSummer ?? 0,
        };
        dto.totalCost = Math.Round(agg.byPeriod.Values.Sum(v => v.dCost), 1);
        dto.todayCost = Math.Round(todayAgg.byPeriod.Values.Sum(v => v.dCost), 1);
    }

    private async Task BuildProgressiveStatusAsync(EmsElectricityCostDto dto, TariffPlan plan,
        SqlConnection conn, Common.Data.Models.EnergyCircuitModel root, Common.Data.Models.EnergyCircuitModel target,
        PeriodAgg targetAgg, DateTime dtStart, DateTime dtEndExclusive)
    {
        // 級距永遠套「根迴路（主要電表）」本期總量 — 累進單價是全戶月總度數的函數
        var rootAgg = target.nId == root.nId
            ? targetAgg
            : await QueryPeriodAggAsync(conn, await _circuitService.GetLeavesUnderAsync(root.nId), dtStart, dtEndExclusive);

        var dRootKwh = Math.Max(0, rootAgg.dTotalKwh);
        // 夏月/非夏月混月：以根迴路 kWh 的夏月占比對每級單價加權（占比法，與台電按日數比例分計近似）
        var dSummerShare = dRootKwh > 1e-9 ? Math.Clamp(rootAgg.dSummerKwh / dRootKwh, 0, 1) : (ResolveSeason(plan, DateTime.Today) ? 1 : 0);
        var (dRootCost, nTierIdx) = ApplyTiers(plan.tiers, dRootKwh, dSummerShare);

        var prog = new EmsCostProgressiveDto { tierIndex = nTierIdx };
        if (nTierIdx >= 0 && nTierIdx < plan.tiers.Count)
        {
            prog.tierFrom = plan.tiers[nTierIdx].nFrom;
            prog.tierTo = plan.tiers[nTierIdx].nTo;
        }

        if (target.nId == root.nId)
        {
            dto.totalCost = Math.Round(dRootCost, 1);
        }
        else
        {
            // 子迴路：kWh 占比分攤（參考估算）
            var dShare = dRootKwh > 1e-9 ? Math.Max(0, targetAgg.dTotalKwh) / dRootKwh : 0;
            dto.totalCost = Math.Round(dRootCost * dShare, 1);
            dto.isEstimated = true;
        }
        dto.progressive = prog;
        dto.todayCost = null;   // 累進無法歸屬單日金額
    }

    private async Task ApplySurchargeAsync(EmsElectricityCostDto dto, TariffPlan plan,
        SqlConnection conn, Common.Data.Models.EnergyCircuitModel root, Common.Data.Models.EnergyCircuitModel target,
        PeriodAgg targetAgg, DateTime dtStart, DateTime dtEndExclusive)
    {
        var surcharge = plan.surcharge!;
        var rootAgg = target.nId == root.nId
            ? targetAgg
            : await QueryPeriodAggAsync(conn, await _circuitService.GetLeavesUnderAsync(root.nId), dtStart, dtEndExclusive);

        var dRootKwh = Math.Max(0, rootAgg.dTotalKwh);
        if (dRootKwh <= surcharge.nOverKwh) return;

        var dRootSurcharge = (dRootKwh - surcharge.nOverKwh) * surcharge.dPrice;
        double dApplied;
        if (target.nId == root.nId)
        {
            dApplied = dRootSurcharge;
        }
        else
        {
            var dShare = dRootKwh > 1e-9 ? Math.Max(0, targetAgg.dTotalKwh) / dRootKwh : 0;
            dApplied = dRootSurcharge * dShare;
            dto.isEstimated = true;
        }

        dto.surcharge = new EmsCostSurchargeDto
        {
            overKwh = surcharge.nOverKwh,
            amount = Math.Round(dApplied, 1),
        };
        dto.totalCost = Math.Round((dto.totalCost ?? 0) + dApplied, 1);
    }

    /// <summary>級距套算 — 回傳（總金額, 目前落點級距 index；總量 0 時 index=0）</summary>
    private static (double dCost, int nTierIndex) ApplyTiers(List<TariffTier> tiers, double dTotalKwh, double dSummerShare)
    {
        double dCost = 0;
        var nTierIdx = 0;
        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            var dLower = Math.Max(0, tier.nFrom - 1);              // 級距下界（度，exclusive 累計基準）
            var dUpper = tier.nTo.HasValue ? (double)tier.nTo.Value : double.MaxValue;
            if (dTotalKwh <= dLower) break;
            var dSlice = Math.Min(dTotalKwh, dUpper) - dLower;
            var dPrice = tier.dSummer * dSummerShare + tier.dNonSummer * (1 - dSummerShare);
            dCost += dSlice * dPrice;
            nTierIdx = i;
            if (dTotalKwh <= dUpper) break;
        }
        return (dCost, nTierIdx);
    }

    // ── 期別彙總查詢 ─────────────────────────────────────

    private class PeriodAgg
    {
        /// <summary>時段 → (kWh, 金額)，已套 EffectiveSign</summary>
        public Dictionary<string, (double dKwh, double dCost)> byPeriod { get; } = new();
        public double dTotalKwh { get; set; }
        /// <summary>Season=summer 的 kWh（progressive 夏月占比用）</summary>
        public double dSummerKwh { get; set; }
        public DateTime? dtLastHour { get; set; }
    }

    /// <summary>
    /// 查詢一組葉子在 [dtStart, dtEndExclusive) 的 ElectricityCostHourly 彙總（套 EffectiveSign）。
    /// 一條 SQL per-SID GROUP BY，sign 在記憶體套用（同 EnergyReportService 模式）。
    /// </summary>
    private static async Task<PeriodAgg> QueryPeriodAggAsync(
        SqlConnection conn, List<EnergyCircuitService.LeafWithSign> leaves,
        DateTime dtStart, DateTime dtEndExclusive)
    {
        var agg = new PeriodAgg();
        if (leaves.Count == 0) return agg;

        var signBySid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in leaves)
        {
            if (!string.IsNullOrWhiteSpace(l.Leaf.szSID))
                signBySid[l.Leaf.szSID!] = l.nEffectiveSign;
        }
        if (signBySid.Count == 0) return agg;

        var rows = await conn.QueryAsync<(string SID, string Period, string Season, double Kwh, double? Cost, DateTime LastHour)>(@"
            SELECT SID, Period, Season,
                   SUM(Kwh)  AS Kwh,
                   SUM(Cost) AS Cost,
                   MAX(HourStart) AS LastHour
            FROM   ElectricityCostHourly WITH (NOLOCK)
            WHERE  SID IN @sids AND HourStart >= @dtStart AND HourStart < @dtEndExclusive
            GROUP BY SID, Period, Season",
            new { sids = signBySid.Keys.ToList(), dtStart, dtEndExclusive });

        foreach (var r in rows)
        {
            if (!signBySid.TryGetValue(r.SID, out var nSign)) continue;
            var dKwh = r.Kwh * nSign;
            var dCost = (r.Cost ?? 0) * nSign;
            agg.dTotalKwh += dKwh;
            if (r.Season == "summer") agg.dSummerKwh += dKwh;
            if (agg.byPeriod.TryGetValue(r.Period, out var v))
                agg.byPeriod[r.Period] = (v.dKwh + dKwh, v.dCost + dCost);
            else
                agg.byPeriod[r.Period] = (dKwh, dCost);
            if (agg.dtLastHour == null || r.LastHour > agg.dtLastHour) agg.dtLastHour = r.LastHour;
        }
        return agg;
    }

    // ════════════════════════════════════════════════════════════
    //  電費報表（/ElectricityCostReport）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 電費報表查詢 — bucket 切界沿用用電報表基礎設施（時/日/年 BuildBoundaries；月 = 月結期別）。
    /// tou/flat：SUM(Cost × EffectiveSign) 精確；progressive：期別累計 kWh 套級距 → bucket kWh 占比分攤。
    /// 金額只含流動電費（不含基本電費與簡易型超額加價）。
    /// </summary>
    public async Task<CostReportResult> GetCostReportAsync(
        int nCircuitId, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var circuit = await _circuitService.GetByIdAsync(nCircuitId)
            ?? throw new InvalidOperationException($"迴路 Id={nCircuitId} 不存在");

        var (ranges, labels) = await BuildCostBucketRangesAsync(szGranularity, dtStart, dtEnd);
        var config = await _tariffService.GetConfigAsync();
        var periods = await GetCoveringPeriodsAsync(ranges[0].dtStart, ranges[^1].dtEnd);
        var root = await ResolveRootCircuitAsync();
        var isRoot = root != null && root.nId == nCircuitId;

        var result = new CostReportResult
        {
            circuitId = nCircuitId,
            circuitName = circuit.szName,
            granularity = szGranularity,
            start = ranges[0].dtStart,
            end = ranges[^1].dtEnd,
        };

        using var conn = await GetConnectionAsync();
        var rootProg = new Lazy<Task<Dictionary<(int, string), ProgPeriodAgg>>>(
            () => ComputeRootProgAggAsync(conn, root, szGranularity, ranges, periods));

        var series = await ComputeCostSeriesAsync(conn, nCircuitId, isRoot, szGranularity, ranges, periods, config, rootProg);
        FillCostResult(result, labels, series, szGranularity, isRoot);
        return result;
    }

    /// <summary>
    /// 單一迴路在區間內的流動電費總額（薄包 GetCostReportAsync 取 totalCost）— EMS 電費去年同期比較卡用。
    /// 語意同 EnergyReportService.GetTotalKwhAsync：回傳值未套用迴路自身對父層的 Sign（由呼叫端補乘 nChildSign）。
    /// progressive 子迴路為 kWh 占比分攤估算（isEstimated=true）。
    /// </summary>
    public async Task<(double dTotalCost, bool isEstimated)> GetTotalCostAsync(
        int nCircuitId, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var report = await GetCostReportAsync(nCircuitId, szGranularity, dtStart, dtEnd);
        return (report.totalCost, report.isEstimated);
    }

    /// <summary>同 GetCostReportAsync，再展開直接子迴路每欄電費 series — Excel 匯出用（本身是葉子則不展開）</summary>
    public async Task<CostReportResult> GetCostReportWithChildrenAsync(
        int nCircuitId, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var circuit = await _circuitService.GetByIdAsync(nCircuitId)
            ?? throw new InvalidOperationException($"迴路 Id={nCircuitId} 不存在");

        var (ranges, labels) = await BuildCostBucketRangesAsync(szGranularity, dtStart, dtEnd);
        var config = await _tariffService.GetConfigAsync();
        var periods = await GetCoveringPeriodsAsync(ranges[0].dtStart, ranges[^1].dtEnd);
        var root = await ResolveRootCircuitAsync();
        var isRoot = root != null && root.nId == nCircuitId;

        var result = new CostReportResult
        {
            circuitId = nCircuitId,
            circuitName = circuit.szName,
            granularity = szGranularity,
            start = ranges[0].dtStart,
            end = ranges[^1].dtEnd,
        };

        using var conn = await GetConnectionAsync();
        var rootProg = new Lazy<Task<Dictionary<(int, string), ProgPeriodAgg>>>(
            () => ComputeRootProgAggAsync(conn, root, szGranularity, ranges, periods));

        var series = await ComputeCostSeriesAsync(conn, nCircuitId, isRoot, szGranularity, ranges, periods, config, rootProg);
        FillCostResult(result, labels, series, szGranularity, isRoot);

        // 自己是葉子 → 不展開（同用電報表 Excel 格式相容邏輯）
        if (!string.IsNullOrEmpty(circuit.szSID))
            return result;

        foreach (var child in await _circuitService.GetDirectChildrenAsync(nCircuitId))
        {
            // 子迴路內部 leaves 的 sign 已由 GetLeavesUnderAsync 累乘（相對於 child），child 對父的方向在這裡補乘
            var nChildSign = child.nSign == -1 ? -1 : 1;
            var childSeries = await ComputeCostSeriesAsync(conn, child.nId, isRoot: false, szGranularity, ranges, periods, config, rootProg);
            var childDto = new CostReportChildSeries { circuitId = child.nId, name = child.szName };
            double dTotal = 0;
            for (var i = 0; i < labels.Count; i++)
            {
                var dVal = childSeries.dCost[i] * nChildSign;
                childDto.costPerBucket.Add(Math.Round(dVal, 1));
                dTotal += dVal;
            }
            childDto.totalCost = Math.Round(dTotal, 1);
            if (childSeries.isEstimated) result.isEstimated = true;
            result.children.Add(childDto);
        }
        return result;
    }

    // ── 報表內部 ─────────────────────────────────────────

    private class ProgPeriodAgg
    {
        public double dKwh;
        public double dSummerKwh;
        /// <summary>bucket index → 該期別內落在此 bucket 的 progressive kWh</summary>
        public Dictionary<int, double> perBucket = new();
    }

    private record CostSeries(double[] dKwh, double[] dCost, int nRowCount, bool isEstimated);

    /// <summary>bucket 切界：月粒度 = 月結期別；其餘沿用 EnergyReportService.BuildBoundaries/BuildLabels</summary>
    private async Task<(List<(DateTime dtStart, DateTime dtEnd)> ranges, List<string> labels)>
        BuildCostBucketRangesAsync(string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        if (szGranularity == "month")
        {
            var periods = await _billingPeriodService.GetPeriodRangesAsync(dtStart, dtEnd);
            return (periods.Select(p => (p.dtStart, p.dtEndExclusive)).ToList(),
                    periods.Select(p => p.szLabel).ToList());
        }
        var boundaries = _reportService.BuildBoundaries(szGranularity, dtStart, dtEnd);
        var ranges = new List<(DateTime, DateTime)>(boundaries.Count - 1);
        for (var i = 0; i < boundaries.Count - 1; i++)
            ranges.Add((boundaries[i], boundaries[i + 1]));
        return (ranges, _reportService.BuildLabels(szGranularity, boundaries));
    }

    /// <summary>取得涵蓋查詢範圍的月結期別（前後各多抓一個月 — 期別可能非自然月，跨月日期歸屬前後期）</summary>
    private async Task<List<Common.Data.Models.BillingPeriodRange>> GetCoveringPeriodsAsync(DateTime dtStart, DateTime dtEndExclusive)
    {
        return await _billingPeriodService.GetPeriodRangesAsync(dtStart.AddMonths(-1), dtEndExclusive.AddMonths(1));
    }

    /// <summary>根迴路 = 主要電表；未設定時取第一個根節點（同 EMS 電費卡語意）</summary>
    private async Task<Common.Data.Models.EnergyCircuitModel?> ResolveRootCircuitAsync()
    {
        var root = await _circuitService.GetMainMeterAsync();
        if (root != null) return root;
        var all = (await _circuitService.GetAllAsync()).ToList();
        return all.Where(c => c.nParentId == null).OrderBy(c => c.nSortOrder).ThenBy(c => c.nId).FirstOrDefault();
    }

    /// <summary>
    /// 對單一迴路計算每 bucket 的 kWh / 電費。
    /// hour 粒度以小時列查詢，其餘以日彙總列查詢（一日必落單一 bucket / 單一期別）。
    /// </summary>
    private async Task<CostSeries> ComputeCostSeriesAsync(
        SqlConnection conn, int nCircuitId, bool isRoot, string szGranularity,
        List<(DateTime dtStart, DateTime dtEnd)> ranges,
        List<Common.Data.Models.BillingPeriodRange> periods,
        TariffConfig config,
        Lazy<Task<Dictionary<(int, string), ProgPeriodAgg>>> rootProgLazy)
    {
        var nBuckets = ranges.Count;
        var dKwh = new double[nBuckets];
        var dCost = new double[nBuckets];
        var isEstimated = false;

        var leaves = await _circuitService.GetLeavesUnderAsync(nCircuitId);
        var rows = await QueryCostRowsAsync(conn, leaves, szGranularity, ranges[0].dtStart, ranges[^1].dtEnd);
        if (rows.Count == 0)
            return new CostSeries(dKwh, dCost, 0, false);

        // progressive 累計：(期別 idx, PlanId) → 期別 kWh + 夏月 kWh + per-bucket kWh
        var prog = new Dictionary<(int, string), ProgPeriodAgg>();

        foreach (var r in rows)
        {
            var nBucket = FindRangeIndex(ranges, r.dtTime);
            if (nBucket < 0) continue;
            dKwh[nBucket] += r.dKwh;

            if (r.szPlanType == "progressive")
            {
                var nPeriod = FindPeriodIndex(periods, r.dtTime);
                if (nPeriod < 0) continue;   // 找不到期別 → 該部分金額略過（kWh 已計）
                var key = (nPeriod, r.szPlanId);
                if (!prog.TryGetValue(key, out var agg))
                    prog[key] = agg = new ProgPeriodAgg();
                agg.dKwh += r.dKwh;
                if (r.szSeason == "summer") agg.dSummerKwh += r.dKwh;
                agg.perBucket.TryGetValue(nBucket, out var dPrev);
                agg.perBucket[nBucket] = dPrev + r.dKwh;
            }
            else
            {
                dCost[nBucket] += r.dCost;
            }
        }

        // progressive：期別級距金額 → bucket 占比分攤
        if (prog.Count > 0)
        {
            var rootProg = isRoot ? prog : await rootProgLazy.Value;
            foreach (var (key, agg) in prog)
            {
                var plan = config.plans.FirstOrDefault(p =>
                    string.Equals(p.szPlanId, key.Item2, StringComparison.OrdinalIgnoreCase));
                if (plan == null || plan.tiers.Count == 0) continue;   // 方案已不存在 → 金額略過

                if (!rootProg.TryGetValue(key, out var rootAgg) || rootAgg.dKwh <= 1e-9) continue;
                var dRootKwh = Math.Max(0, rootAgg.dKwh);
                var dSummerShare = Math.Clamp(Math.Max(0, rootAgg.dSummerKwh) / dRootKwh, 0, 1);
                var (dRootCost, _) = ApplyTiers(plan.tiers, dRootKwh, dSummerShare);

                // 子迴路：kWh 占比分攤（估算）；根迴路：全額
                var dCircuitCost = isRoot ? dRootCost : dRootCost * Math.Max(0, agg.dKwh) / dRootKwh;
                if (!isRoot) isEstimated = true;

                if (agg.dKwh > 1e-9)
                {
                    foreach (var (nBucket, dBucketKwh) in agg.perBucket)
                        dCost[nBucket] += dCircuitCost * dBucketKwh / agg.dKwh;
                }
                // 非月粒度的分攤（bucket ≠ 期別）一律視為估算
                if (szGranularity != "month") isEstimated = true;
            }
        }

        return new CostSeries(dKwh, dCost, rows.Count, isEstimated);
    }

    /// <summary>根迴路的 progressive 期別彙總（級距分母；僅子迴路查詢時才真的執行）</summary>
    private async Task<Dictionary<(int, string), ProgPeriodAgg>> ComputeRootProgAggAsync(
        SqlConnection conn, Common.Data.Models.EnergyCircuitModel? root, string szGranularity,
        List<(DateTime dtStart, DateTime dtEnd)> ranges,
        List<Common.Data.Models.BillingPeriodRange> periods)
    {
        var result = new Dictionary<(int, string), ProgPeriodAgg>();
        if (root == null) return result;

        var leaves = await _circuitService.GetLeavesUnderAsync(root.nId);
        var rows = await QueryCostRowsAsync(conn, leaves, szGranularity, ranges[0].dtStart, ranges[^1].dtEnd);
        foreach (var r in rows)
        {
            if (r.szPlanType != "progressive") continue;
            var nPeriod = FindPeriodIndex(periods, r.dtTime);
            if (nPeriod < 0) continue;
            var key = (nPeriod, r.szPlanId);
            if (!result.TryGetValue(key, out var agg))
                result[key] = agg = new ProgPeriodAgg();
            agg.dKwh += r.dKwh;
            if (r.szSeason == "summer") agg.dSummerKwh += r.dKwh;
        }
        return result;
    }

    private record CostRowAgg(DateTime dtTime, string szPlanId, string szPlanType, string szSeason, double dKwh, double dCost);

    /// <summary>
    /// 查詢一組葉子在區間內的計價列（已套 EffectiveSign）。
    /// hour 粒度按小時分組；其餘按日分組（一日必落單一 bucket / 期別，避免全年逐時列灌爆記憶體）。
    /// </summary>
    private static async Task<List<CostRowAgg>> QueryCostRowsAsync(
        SqlConnection conn, List<EnergyCircuitService.LeafWithSign> leaves,
        string szGranularity, DateTime dtStart, DateTime dtEndExclusive)
    {
        var result = new List<CostRowAgg>();
        if (leaves.Count == 0) return result;

        var signBySid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in leaves)
        {
            if (!string.IsNullOrWhiteSpace(l.Leaf.szSID))
                signBySid[l.Leaf.szSID!] = l.nEffectiveSign;
        }
        if (signBySid.Count == 0) return result;

        var szTimeExpr = szGranularity == "hour" ? "HourStart" : "CAST(HourStart AS date)";
        var rows = await conn.QueryAsync<(string SID, DateTime T, string PlanId, string PlanType, string Season, double Kwh, double? Cost)>($@"
            SELECT SID, {szTimeExpr} AS T, PlanId, PlanType, Season,
                   SUM(Kwh)  AS Kwh,
                   SUM(Cost) AS Cost
            FROM   ElectricityCostHourly WITH (NOLOCK)
            WHERE  SID IN @sids AND HourStart >= @dtStart AND HourStart < @dtEndExclusive
            GROUP BY SID, {szTimeExpr}, PlanId, PlanType, Season",
            new { sids = signBySid.Keys.ToList(), dtStart, dtEndExclusive });

        foreach (var r in rows)
        {
            if (!signBySid.TryGetValue(r.SID, out var nSign)) continue;
            result.Add(new CostRowAgg(r.T, r.PlanId, r.PlanType, r.Season, r.Kwh * nSign, (r.Cost ?? 0) * nSign));
        }
        return result;
    }

    /// <summary>時刻 → bucket index（ranges 依起點排序；期別可能重疊 → 取起點最晚者）；無命中回 -1</summary>
    private static int FindRangeIndex(List<(DateTime dtStart, DateTime dtEnd)> ranges, DateTime dt)
    {
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            if (ranges[i].dtStart <= dt && dt < ranges[i].dtEnd) return i;
        }
        return -1;
    }

    /// <summary>時刻 → 期別 index（重疊取起點最晚者，同 GetCurrentPeriodAsync 語意）；無命中回 -1</summary>
    private static int FindPeriodIndex(List<Common.Data.Models.BillingPeriodRange> periods, DateTime dt)
    {
        var nBest = -1;
        for (var i = 0; i < periods.Count; i++)
        {
            if (periods[i].dtStart <= dt && dt < periods[i].dtEndExclusive
                && (nBest < 0 || periods[i].dtStart > periods[nBest].dtStart))
                nBest = i;
        }
        return nBest;
    }

    private static void FillCostResult(CostReportResult result, List<string> labels, CostSeries series,
        string szGranularity, bool isRoot)
    {
        double dTotalKwh = 0, dTotalCost = 0;
        for (var i = 0; i < labels.Count; i++)
        {
            result.buckets.Add(new CostReportBucketDto
            {
                label = labels[i],
                kwh = Math.Round(series.dKwh[i], 2),
                cost = Math.Round(series.dCost[i], 1),
            });
            dTotalKwh += series.dKwh[i];
            dTotalCost += series.dCost[i];
        }
        result.totalKwh = Math.Round(dTotalKwh, 2);
        result.totalCost = Math.Round(dTotalCost, 1);
        result.hasData = series.nRowCount > 0;
        result.isEstimated = series.isEstimated;
    }
}
