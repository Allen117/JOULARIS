using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.DailyReport.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 能源日報核心服務 — 聚合四報表（電/水/氣/RTh）+ EventLog 警報摘要 + 單日比較（前日/上週同星期）
/// + MTD 年同期比較 + 假日/天氣附註 → DailyReportData（可序列化 JSON），
/// 並負責 DailyReportSetting / DailyReportRecipients / DailyReportSnapshot 三張表的讀寫。
/// 比較口徑 = 各能源別「根迴路合計」（多 root 加總）；細節見 docs/架構.md §能源日報。
/// </summary>
public class DailyReportService
{
    private readonly DatabaseConfigService _configService;
    private readonly EnergyCircuitService _energyCircuitService;
    private readonly WaterMeterCircuitService _waterMeterCircuitService;
    private readonly GasMeterCircuitService _gasMeterCircuitService;
    private readonly WaterCircuitService _waterCircuitService;
    private readonly EnergyReportService _energyReportService;
    private readonly WaterUsageReportService _waterUsageReportService;
    private readonly GasUsageReportService _gasUsageReportService;
    private readonly RefrigerationTonReportService _rtReportService;
    private readonly HolidayService _holidayService;
    private readonly AlarmMessageLocalizer _alarmMessageLocalizer;
    private readonly DailyReportInsightService _insightService;
    private readonly CompressorDiagnosisService _compressorDiagnosisService;
    private readonly CompressorDiagnosisTextRenderer _compressorDiagnosisRenderer;
    private readonly ILogger<DailyReportService> _logger;
    private string? _szConnectionString;

    /// <summary>比較區間 index 對應（BuildComparisonRanges 回傳順序）</summary>
    private const int RANGE_DAY = 0;
    private const int RANGE_PREV_DAY = 1;
    private const int RANGE_LAST_WEEK = 2;
    private const int RANGE_MTD = 3;
    private const int RANGE_MTD_LAST_YEAR = 4;

    public DailyReportService(
        DatabaseConfigService configService,
        EnergyCircuitService energyCircuitService,
        WaterMeterCircuitService waterMeterCircuitService,
        GasMeterCircuitService gasMeterCircuitService,
        WaterCircuitService waterCircuitService,
        EnergyReportService energyReportService,
        WaterUsageReportService waterUsageReportService,
        GasUsageReportService gasUsageReportService,
        RefrigerationTonReportService rtReportService,
        HolidayService holidayService,
        AlarmMessageLocalizer alarmMessageLocalizer,
        DailyReportInsightService insightService,
        CompressorDiagnosisService compressorDiagnosisService,
        CompressorDiagnosisTextRenderer compressorDiagnosisRenderer,
        ILogger<DailyReportService> logger)
    {
        _configService = configService;
        _energyCircuitService = energyCircuitService;
        _waterMeterCircuitService = waterMeterCircuitService;
        _gasMeterCircuitService = gasMeterCircuitService;
        _waterCircuitService = waterCircuitService;
        _energyReportService = energyReportService;
        _waterUsageReportService = waterUsageReportService;
        _gasUsageReportService = gasUsageReportService;
        _rtReportService = rtReportService;
        _holidayService = holidayService;
        _alarmMessageLocalizer = alarmMessageLocalizer;
        _insightService = insightService;
        _compressorDiagnosisService = compressorDiagnosisService;
        _compressorDiagnosisRenderer = compressorDiagnosisRenderer;
        _logger = logger;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ────────────────────────── 日期推導（靜態純函式，單元測試對象）──────────────────────────

    /// <summary>本月 1 日 ~ 報告日（訖 = 報告日 +1，exclusive）</summary>
    public static (DateTime dtStart, DateTime dtEndExclusive) BuildMtdRange(DateTime dtReportDate)
    {
        var d = dtReportDate.Date;
        return (new DateTime(d.Year, d.Month, 1), d.AddDays(1));
    }

    /// <summary>去年同月 1 日 ~ 同日數（去年該月天數不足時取 min，處理 2/29 邊界）</summary>
    public static (DateTime dtStart, DateTime dtEndExclusive) BuildMtdLastYearRange(DateTime dtReportDate)
    {
        var d = dtReportDate.Date;
        var nLyYear = d.Year - 1;
        var dtLyStart = new DateTime(nLyYear, d.Month, 1);
        var nDays = Math.Min(d.Day, DateTime.DaysInMonth(nLyYear, d.Month));
        return (dtLyStart, dtLyStart.AddDays(nDays));
    }

    /// <summary>
    /// 五組比較區間 [起, 訖)：[0]=報告日 D、[1]=前日 D-1、[2]=上週同星期 D-7、[3]=本月 MTD、[4]=去年同月 MTD
    /// </summary>
    public static List<(DateTime dtStart, DateTime dtEnd)> BuildComparisonRanges(DateTime dtReportDate)
    {
        var d = dtReportDate.Date;
        var (dtMtdStart, dtMtdEnd) = BuildMtdRange(d);
        var (dtLyStart, dtLyEnd) = BuildMtdLastYearRange(d);
        return new List<(DateTime, DateTime)>
        {
            (d, d.AddDays(1)),
            (d.AddDays(-1), d),
            (d.AddDays(-7), d.AddDays(-6)),
            (dtMtdStart, dtMtdEnd),
            (dtLyStart, dtLyEnd),
        };
    }

    /// <summary>差異 %（基準 ~0 時回 null 避免除零）；四捨五入至 1 位</summary>
    public static double? CalcDiffPercent(double dCurrent, double dBase)
    {
        if (Math.Abs(dBase) < 1e-9) return null;
        return Math.Round((dCurrent - dBase) / dBase * 100.0, 1);
    }

    // ────────────────────────── 日報生成 ──────────────────────────

    /// <summary>
    /// 生成指定報告日的完整日報內容（不落 DB — 快照儲存由呼叫端決定）。
    /// 智慧提示與警報訊息以 DailyReportSetting.Language 全局語言渲染。
    /// </summary>
    public async Task<DailyReportData> BuildAsync(DateTime dtReportDate)
    {
        var setting = await GetSettingAsync();
        var dtDate = dtReportDate.Date;

        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            // 快照文字（警報訊息 / 提示）固定用全局語言渲染，與檢視者 culture 無關
            CultureInfo.CurrentUICulture = new CultureInfo(setting.szLanguage);

            var data = new DailyReportData
            {
                szReportDate = dtDate.ToString("yyyy-MM-dd"),
                dtGeneratedAt = DateTime.Now,
                szLanguage = setting.szLanguage,
                isReportDateHoliday = await _holidayService.IsHolidayAsync(dtDate),
                isPrevDayHoliday = await _holidayService.IsHolidayAsync(dtDate.AddDays(-1)),
                isLastWeekHoliday = await _holidayService.IsHolidayAsync(dtDate.AddDays(-7)),
            };

            data.alarm = await BuildAlarmSummaryAsync(dtDate);

            // 四能源別：24 小時序列 + 各自的有效 root 清單（比較用）
            var (elec, elecRoots) = await BuildElectricitySectionAsync(dtDate);
            var (water, waterRoots) = await BuildWaterSectionAsync(dtDate);
            var (gas, gasRoots) = await BuildGasSectionAsync(dtDate);
            var (rth, rthRoots) = await BuildRthSectionAsync(dtDate);
            data.electricity = elec;
            data.water = water;
            data.gas = gas;
            data.rth = rth;

            // 五組比較區間各能源別合計
            var ranges = BuildComparisonRanges(dtDate);
            var elecTotals = elec.isAvailable ? await GetElectricityTotalsAsync(elecRoots, ranges) : null;
            var waterTotals = water.isAvailable ? await GetWaterTotalsAsync(waterRoots, ranges) : null;
            var gasTotals = gas.isAvailable ? await GetGasTotalsAsync(gasRoots, ranges) : null;
            var rthTotals = rth.isAvailable ? await GetRthTotalsAsync(rthRoots, ranges) : null;

            AddComparisonRows(data, "electricity", "kWh", elecTotals, ranges);
            AddComparisonRows(data, "water", "m³", waterTotals, ranges);
            AddComparisonRows(data, "gas", "m³", gasTotals, ranges);
            AddComparisonRows(data, "rth", "RT·h", rthTotals, ranges);

            // kWh/RTh 效率比值（兩者皆有設定才算）
            if (elecTotals != null && rthTotals != null)
            {
                double? Ratio(double dKwh, double dRth) => dRth > 1e-9 ? Math.Round(dKwh / dRth, 3) : null;
                var eff = new DailyReportEfficiency
                {
                    dDay = Ratio(elecTotals[RANGE_DAY], rthTotals[RANGE_DAY]),
                    dPrevDay = Ratio(elecTotals[RANGE_PREV_DAY], rthTotals[RANGE_PREV_DAY]),
                    dLastWeek = Ratio(elecTotals[RANGE_LAST_WEEK], rthTotals[RANGE_LAST_WEEK]),
                };
                if (eff.dDay.HasValue && eff.dPrevDay.HasValue)
                    eff.dDiffPrevPct = CalcDiffPercent(eff.dDay.Value, eff.dPrevDay.Value);
                if (eff.dDay.HasValue && eff.dLastWeek.HasValue)
                    eff.dDiffLastWeekPct = CalcDiffPercent(eff.dDay.Value, eff.dLastWeek.Value);
                data.efficiency = eff;
            }

            data.weather = await BuildWeatherAsync(dtDate);

            // 最後一小時（23 時）缺資料 → 快照可能不完整（Engine XX:03 聚合已完成後才生成，正常不會缺）
            static bool LastHourStale(DailyReportEnergySection s) =>
                s.isAvailable && s.isHourlyStale.Count == 24 && s.isHourlyStale[23];
            data.isStaleLastHour = LastHourStale(elec) || LastHourStale(water) || LastHourStale(gas);

            data.insights = _insightService.BuildInsights(data, setting);

            // 空壓系統診斷：取（或生成）報告日最近 7 天滾動排程快照，依全局語言渲染摘要。
            // 生成時一律填入（區段開關 SectionFlags=256 於顯示端過濾，同其他區塊）；失敗不影響日報其餘內容
            try
            {
                var snap = await _compressorDiagnosisService.GetOrRunScheduledAsync(dtDate);
                if (snap != null) data.compressorDiagnosis = _compressorDiagnosisRenderer.BuildDailySection(snap.result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "日報空壓系統診斷區段生成失敗 ReportDate={ReportDate:yyyy-MM-dd}", dtDate);
            }
            return data;
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    /// <summary>前一日 EventLog 警報 + 故障（EventType 0+1）明細與統計</summary>
    private async Task<DailyReportAlarmSummary> BuildAlarmSummaryAsync(DateTime dtDate)
    {
        using var conn = await GetConnectionAsync();
        var rows = (await conn.QueryAsync<AlarmRow>(@"
            SELECT OccurredAt AS dtOccurredAt, SID AS szSID, EventType AS nEventType, Severity AS nSeverity,
                   Message AS szMessage, MessageKey AS szMessageKey, MessageArgs AS szMessageArgs,
                   ClearedAt AS dtClearedAt, IsAcknowledged AS isAcknowledged
            FROM EventLog WITH (NOLOCK)
            WHERE EventType IN (0, 1) AND OccurredAt >= @dtStart AND OccurredAt < @dtEnd
            ORDER BY OccurredAt DESC",
            new { dtStart = dtDate, dtEnd = dtDate.AddDays(1) })).ToList();

        var summary = new DailyReportAlarmSummary
        {
            nOccurredCount = rows.Count,
            nClearedCount = rows.Count(r => r.dtClearedAt.HasValue),
            nUnacknowledgedCount = rows.Count(r => !r.isAcknowledged),
        };
        foreach (var r in rows)
        {
            summary.items.Add(new DailyReportAlarmItem
            {
                dtOccurredAt = r.dtOccurredAt,
                szSID = r.szSID,
                nEventType = r.nEventType,
                nSeverity = r.nSeverity,
                szMessage = _alarmMessageLocalizer.Localize(r.szMessageKey, r.szMessageArgs, r.szMessage),
                dtClearedAt = r.dtClearedAt,
                isAcknowledged = r.isAcknowledged,
            });
        }
        return summary;
    }

    private class AlarmRow
    {
        public DateTime dtOccurredAt { get; set; }
        public string szSID { get; set; } = "";
        public int nEventType { get; set; }
        public int nSeverity { get; set; }
        public string szMessage { get; set; } = "";
        public string? szMessageKey { get; set; }
        public string? szMessageArgs { get; set; }
        public DateTime? dtClearedAt { get; set; }
        public bool isAcknowledged { get; set; }
    }

    // ────────────────────────── 四能源別 24 小時區塊 ──────────────────────────

    private async Task<(DailyReportEnergySection section, List<int> rootIds)> BuildElectricitySectionAsync(DateTime dtDate)
    {
        var section = NewSection("kWh");
        var rootIds = new List<int>();
        var szNames = new List<string>();
        var roots = (await _energyCircuitService.GetAllAsync())
            .Where(c => c.nParentId == null).OrderBy(c => c.nSortOrder).ThenBy(c => c.nId);
        foreach (var root in roots)
        {
            if ((await _energyCircuitService.GetLeavesUnderAsync(root.nId)).Count == 0) continue;
            rootIds.Add(root.nId);
            szNames.Add(root.szName);
        }
        if (rootIds.Count == 0) return (section, rootIds);

        section.isAvailable = true;
        section.szCircuitName = string.Join(" + ", szNames);
        foreach (var nId in rootIds)
        {
            var rpt = await _energyReportService.GetReportAsync(nId, "hour", dtDate, dtDate.AddHours(23));
            for (var i = 0; i < 24 && i < rpt.buckets.Count; i++)
            {
                section.dHourly[i] = Math.Round(section.dHourly[i] + rpt.buckets[i].dKwh, 3);
                section.isHourlyStale[i] = section.isHourlyStale[i] || rpt.buckets[i].isStale;
            }
            section.dTotal = Math.Round(section.dTotal + rpt.dTotalKwh, 3);
            section.isHasWarning |= rpt.isHasWarning;
        }
        return (section, rootIds);
    }

    private async Task<(DailyReportEnergySection section, List<int> rootIds)> BuildWaterSectionAsync(DateTime dtDate)
    {
        var section = NewSection("m³");
        var rootIds = new List<int>();
        var szNames = new List<string>();
        var roots = (await _waterMeterCircuitService.GetAllAsync())
            .Where(c => c.nParentId == null).OrderBy(c => c.nSortOrder).ThenBy(c => c.nId);
        foreach (var root in roots)
        {
            if ((await _waterMeterCircuitService.GetLeavesUnderAsync(root.nId)).Count == 0) continue;
            rootIds.Add(root.nId);
            szNames.Add(root.szName);
        }
        if (rootIds.Count == 0) return (section, rootIds);

        section.isAvailable = true;
        section.szCircuitName = string.Join(" + ", szNames);
        foreach (var nId in rootIds)
        {
            var rpt = await _waterUsageReportService.GetReportAsync(nId, "hour", dtDate, dtDate.AddHours(23));
            for (var i = 0; i < 24 && i < rpt.buckets.Count; i++)
            {
                section.dHourly[i] = Math.Round(section.dHourly[i] + rpt.buckets[i].dM3, 3);
                section.isHourlyStale[i] = section.isHourlyStale[i] || rpt.buckets[i].isStale;
            }
            section.dTotal = Math.Round(section.dTotal + rpt.dTotalM3, 3);
            section.isHasWarning |= rpt.isHasWarning;
        }
        return (section, rootIds);
    }

    private async Task<(DailyReportEnergySection section, List<int> rootIds)> BuildGasSectionAsync(DateTime dtDate)
    {
        var section = NewSection("m³");
        var rootIds = new List<int>();
        var szNames = new List<string>();
        var roots = (await _gasMeterCircuitService.GetAllAsync())
            .Where(c => c.nParentId == null).OrderBy(c => c.nSortOrder).ThenBy(c => c.nId);
        foreach (var root in roots)
        {
            if ((await _gasMeterCircuitService.GetLeavesUnderAsync(root.nId)).Count == 0) continue;
            rootIds.Add(root.nId);
            szNames.Add(root.szName);
        }
        if (rootIds.Count == 0) return (section, rootIds);

        section.isAvailable = true;
        section.szCircuitName = string.Join(" + ", szNames);
        foreach (var nId in rootIds)
        {
            var rpt = await _gasUsageReportService.GetReportAsync(nId, "hour", dtDate, dtDate.AddHours(23));
            for (var i = 0; i < 24 && i < rpt.buckets.Count; i++)
            {
                section.dHourly[i] = Math.Round(section.dHourly[i] + rpt.buckets[i].dM3, 3);
                section.isHourlyStale[i] = section.isHourlyStale[i] || rpt.buckets[i].isStale;
            }
            section.dTotal = Math.Round(section.dTotal + rpt.dTotalM3, 3);
            section.isHasWarning |= rpt.isHasWarning;
        }
        return (section, rootIds);
    }

    private async Task<(DailyReportEnergySection section, List<int> rootIds)> BuildRthSectionAsync(DateTime dtDate)
    {
        var section = NewSection("RT·h");
        var rootIds = new List<int>();
        var szNames = new List<string>();
        var roots = (await _waterCircuitService.GetAllAsync())
            .Where(c => c.nParentId == null).OrderBy(c => c.nSortOrder).ThenBy(c => c.nId);
        foreach (var root in roots)
        {
            // 冷凍噸體系葉子 = 綁 SID 的節點（無 sign 概念）
            if ((await _waterCircuitService.GetLeavesUnderAsync(root.nId)).Count == 0) continue;
            rootIds.Add(root.nId);
            szNames.Add(root.szName);
        }
        if (rootIds.Count == 0) return (section, rootIds);

        section.isAvailable = true;
        section.szCircuitName = string.Join(" + ", szNames);
        foreach (var nId in rootIds)
        {
            var rpt = await _rtReportService.GetReportAsync(nId, "hour", dtDate, dtDate.AddHours(23));
            for (var i = 0; i < 24 && i < rpt.buckets.Count; i++)
                section.dHourly[i] = Math.Round(section.dHourly[i] + rpt.buckets[i].dRtHour, 3);
            // RTh bucket 無 per-bucket staleness 訊號，僅有覆蓋率警告
            section.dTotal = Math.Round(section.dTotal + rpt.dTotalRtHour, 3);
            section.isHasWarning |= rpt.isHasWarning;
        }
        return (section, rootIds);
    }

    private static DailyReportEnergySection NewSection(string szUnit)
    {
        var section = new DailyReportEnergySection { szUnit = szUnit };
        for (var i = 0; i < 24; i++)
        {
            section.dHourly.Add(0);
            section.isHourlyStale.Add(false);
        }
        return section;
    }

    // ────────────────────────── 五組比較區間合計 ──────────────────────────

    private async Task<double[]> GetElectricityTotalsAsync(List<int> rootIds, List<(DateTime dtStart, DateTime dtEnd)> ranges)
    {
        var totals = new double[ranges.Count];
        foreach (var nId in rootIds)
        {
            var (sums, _) = await _energyReportService.GetBucketKwhForRangesAsync(nId, ranges);
            for (var i = 0; i < ranges.Count; i++) totals[i] += sums[i];
        }
        for (var i = 0; i < totals.Length; i++) totals[i] = Math.Round(totals[i], 3);
        return totals;
    }

    private async Task<double[]> GetWaterTotalsAsync(List<int> rootIds, List<(DateTime dtStart, DateTime dtEnd)> ranges)
    {
        var totals = new double[ranges.Count];
        foreach (var nId in rootIds)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                var (dTotal, _) = await _waterUsageReportService.GetTotalM3Async(nId, ranges[i].dtStart, ranges[i].dtEnd);
                totals[i] += dTotal;
            }
        }
        for (var i = 0; i < totals.Length; i++) totals[i] = Math.Round(totals[i], 3);
        return totals;
    }

    private async Task<double[]> GetGasTotalsAsync(List<int> rootIds, List<(DateTime dtStart, DateTime dtEnd)> ranges)
    {
        var totals = new double[ranges.Count];
        foreach (var nId in rootIds)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                var (dTotal, _) = await _gasUsageReportService.GetTotalM3Async(nId, ranges[i].dtStart, ranges[i].dtEnd);
                totals[i] += dTotal;
            }
        }
        for (var i = 0; i < totals.Length; i++) totals[i] = Math.Round(totals[i], 3);
        return totals;
    }

    private async Task<double[]> GetRthTotalsAsync(List<int> rootIds, List<(DateTime dtStart, DateTime dtEnd)> ranges)
    {
        var totals = new double[ranges.Count];
        foreach (var nId in rootIds)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                // RTh 服務無自訂區間 API — 用日粒度（dtEnd inclusive = 訖日前一天）取 dTotalRtHour
                var rpt = await _rtReportService.GetReportAsync(nId, "day", ranges[i].dtStart, ranges[i].dtEnd.AddDays(-1));
                totals[i] += rpt.dTotalRtHour;
            }
        }
        for (var i = 0; i < totals.Length; i++) totals[i] = Math.Round(totals[i], 3);
        return totals;
    }

    private static void AddComparisonRows(
        DailyReportData data, string szEnergy, string szUnit, double[]? totals,
        List<(DateTime dtStart, DateTime dtEnd)> ranges)
    {
        if (totals == null) return;
        data.dayComparisons.Add(new DailyReportComparisonRow
        {
            szEnergy = szEnergy,
            szUnit = szUnit,
            dDay = totals[RANGE_DAY],
            dPrevDay = totals[RANGE_PREV_DAY],
            dLastWeek = totals[RANGE_LAST_WEEK],
            dDiffPrevPct = CalcDiffPercent(totals[RANGE_DAY], totals[RANGE_PREV_DAY]),
            dDiffLastWeekPct = CalcDiffPercent(totals[RANGE_DAY], totals[RANGE_LAST_WEEK]),
        });
        data.mtdComparisons.Add(new DailyReportMtdRow
        {
            szEnergy = szEnergy,
            szUnit = szUnit,
            dCurrent = totals[RANGE_MTD],
            dLastYear = totals[RANGE_MTD_LAST_YEAR],
            dDiffPct = CalcDiffPercent(totals[RANGE_MTD], totals[RANGE_MTD_LAST_YEAR]),
            szCurrentRange = $"{ranges[RANGE_MTD].dtStart:yyyy-MM-dd} ~ {ranges[RANGE_MTD].dtEnd.AddDays(-1):yyyy-MM-dd}",
            szLastYearRange = $"{ranges[RANGE_MTD_LAST_YEAR].dtStart:yyyy-MM-dd} ~ {ranges[RANGE_MTD_LAST_YEAR].dtEnd.AddDays(-1):yyyy-MM-dd}",
        });
    }

    /// <summary>
    /// 外氣日均溫（D / D-1 / D-7）— Weather Coordinator S1 對 HistoryData 取 Quality=1 日均。
    /// Weather 來源未設定（無 Coordinator 或無資料）→ 回 null，天氣類提示自動停用。
    /// </summary>
    private async Task<DailyReportWeather?> BuildWeatherAsync(DateTime dtDate)
    {
        try
        {
            using var conn = await GetConnectionAsync();
            var szSid = await conn.QueryFirstOrDefaultAsync<string>(@"
                SELECT p.SID FROM DBPoints p
                JOIN DBCoordinator c ON c.Id = p.CoordinatorId
                WHERE c.Name = 'Weather' AND p.Sequence = 1");
            if (string.IsNullOrEmpty(szSid)) return null;

            var rows = (await conn.QueryAsync<(DateTime k, double v)>(@"
                SELECT CONVERT(date, Timestamp) AS k, AVG(Value) AS v
                FROM HistoryData WITH (NOLOCK)
                WHERE SID = @szSid AND Timestamp >= @dtMin AND Timestamp < @dtMax AND Quality = 1
                GROUP BY CONVERT(date, Timestamp)",
                new { szSid, dtMin = dtDate.AddDays(-7), dtMax = dtDate.AddDays(1) }))
                .ToDictionary(r => r.k.Date, r => r.v);

            if (rows.Count == 0) return null;
            double? Pick(DateTime d) => rows.TryGetValue(d.Date, out var v) ? Math.Round(v, 1) : null;
            return new DailyReportWeather
            {
                dAvgTempDay = Pick(dtDate),
                dAvgTempPrevDay = Pick(dtDate.AddDays(-1)),
                dAvgTempLastWeek = Pick(dtDate.AddDays(-7)),
            };
        }
        catch (Exception ex)
        {
            // 天氣為輔助資訊，查詢失敗不擋日報生成
            _logger.LogWarning(ex, "日報外氣均溫查詢失敗，略過天氣區塊");
            return null;
        }
    }

    // ────────────────────────── DailyReportSetting（單列 Id=1）──────────────────────────

    public async Task<DailyReportSettingModel> GetSettingAsync()
    {
        using var conn = await GetConnectionAsync();
        var setting = await conn.QueryFirstOrDefaultAsync<DailyReportSettingModel>(@"
            SELECT Id AS nId, IsMailEnabled AS isMailEnabled, SectionFlags AS nSectionFlags,
                   DiffThresholdPercent AS dDiffThresholdPercent, Language AS szLanguage,
                   IsHolidayHintEnabled AS isHolidayHintEnabled, UpdatedAt AS dtUpdatedAt
            FROM DailyReportSetting WHERE Id = 1");
        return setting ?? new DailyReportSettingModel();
    }

    public async Task SaveSettingAsync(DailyReportSettingModel setting)
    {
        if (setting.szLanguage != "zh-TW" && setting.szLanguage != "en")
            throw new ArgumentException($"不支援的語言: {setting.szLanguage}");
        if (setting.dDiffThresholdPercent <= 0 || setting.dDiffThresholdPercent > 100)
            throw new ArgumentException("差異門檻須在 1–100% 之間");

        using var conn = await GetConnectionAsync();
        var nAffected = await conn.ExecuteAsync(@"
            UPDATE DailyReportSetting
            SET IsMailEnabled = @isMailEnabled, SectionFlags = @nSectionFlags,
                DiffThresholdPercent = @dDiffThresholdPercent, Language = @szLanguage,
                IsHolidayHintEnabled = @isHolidayHintEnabled, UpdatedAt = GETDATE()
            WHERE Id = 1", setting);
        if (nAffected == 0)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO DailyReportSetting (Id, IsMailEnabled, SectionFlags, DiffThresholdPercent, Language, IsHolidayHintEnabled, UpdatedAt)
                VALUES (1, @isMailEnabled, @nSectionFlags, @dDiffThresholdPercent, @szLanguage, @isHolidayHintEnabled, GETDATE())", setting);
        }
    }

    // ────────────────────────── DailyReportRecipients ──────────────────────────

    public async Task<List<DailyReportRecipientModel>> GetRecipientsAsync()
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<DailyReportRecipientModel>(@"
            SELECT Id AS nId, EmailAddress AS szEmailAddress, DisplayName AS szDisplayName, IsEnabled AS isEnabled
            FROM DailyReportRecipients ORDER BY Id");
        return rows.ToList();
    }

    public async Task SaveRecipientAsync(DailyReportRecipientModel recipient)
    {
        if (!MimeKit.MailboxAddress.TryParse(recipient.szEmailAddress?.Trim(), out _))
            throw new ArgumentException($"Email 格式不正確: {recipient.szEmailAddress}");

        using var conn = await GetConnectionAsync();
        if (recipient.nId == 0)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO DailyReportRecipients (EmailAddress, DisplayName, IsEnabled, CreatedAt)
                VALUES (@szEmailAddress, @szDisplayName, @isEnabled, GETDATE())",
                new { szEmailAddress = recipient.szEmailAddress!.Trim(), recipient.szDisplayName, recipient.isEnabled });
        }
        else
        {
            await conn.ExecuteAsync(@"
                UPDATE DailyReportRecipients
                SET EmailAddress = @szEmailAddress, DisplayName = @szDisplayName, IsEnabled = @isEnabled, UpdatedAt = GETDATE()
                WHERE Id = @nId",
                new { szEmailAddress = recipient.szEmailAddress!.Trim(), recipient.szDisplayName, recipient.isEnabled, recipient.nId });
        }
    }

    public async Task DeleteRecipientAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM DailyReportRecipients WHERE Id = @nId", new { nId });
    }

    public async Task ToggleRecipientAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE DailyReportRecipients
            SET IsEnabled = CASE WHEN IsEnabled = 1 THEN 0 ELSE 1 END, UpdatedAt = GETDATE()
            WHERE Id = @nId", new { nId });
    }

    // ────────────────────────── DailyReportSnapshot ──────────────────────────

    public async Task<DailyReportSnapshotMeta?> GetSnapshotMetaAsync(DateTime dtReportDate)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<DailyReportSnapshotMeta>(@"
            SELECT Id AS nId, ReportDate AS dtReportDate, GeneratedAt AS dtGeneratedAt,
                   MailStatus AS nMailStatus, MailDetail AS szMailDetail
            FROM DailyReportSnapshot WHERE ReportDate = @dtReportDate",
            new { dtReportDate = dtReportDate.Date });
    }

    /// <summary>讀快照 payload（不存在回 null）</summary>
    public async Task<DailyReportData?> GetSnapshotDataAsync(DateTime dtReportDate)
    {
        using var conn = await GetConnectionAsync();
        var szJson = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT PayloadJson FROM DailyReportSnapshot WHERE ReportDate = @dtReportDate",
            new { dtReportDate = dtReportDate.Date });
        if (string.IsNullOrEmpty(szJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<DailyReportData>(szJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "日報快照 JSON 解析失敗 ReportDate={ReportDate}", dtReportDate.Date);
            return null;
        }
    }

    /// <summary>UPSERT 快照（同日重生成覆蓋 payload 並重設 MailStatus）</summary>
    public async Task SaveSnapshotAsync(DateTime dtReportDate, DailyReportData data, byte nMailStatus)
    {
        var szJson = JsonSerializer.Serialize(data);
        using var conn = await GetConnectionAsync();
        var nAffected = await conn.ExecuteAsync(@"
            UPDATE DailyReportSnapshot
            SET PayloadJson = @szJson, GeneratedAt = GETDATE(), MailStatus = @nMailStatus, MailDetail = NULL
            WHERE ReportDate = @dtReportDate",
            new { szJson, nMailStatus, dtReportDate = dtReportDate.Date });
        if (nAffected == 0)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO DailyReportSnapshot (ReportDate, PayloadJson, GeneratedAt, MailStatus)
                VALUES (@dtReportDate, @szJson, GETDATE(), @nMailStatus)",
                new { dtReportDate = dtReportDate.Date, szJson, nMailStatus });
        }
    }

    public async Task UpdateMailStatusAsync(DateTime dtReportDate, byte nMailStatus, string? szDetail)
    {
        using var conn = await GetConnectionAsync();
        await conn.ExecuteAsync(@"
            UPDATE DailyReportSnapshot SET MailStatus = @nMailStatus, MailDetail = @szDetail
            WHERE ReportDate = @dtReportDate",
            new { nMailStatus, szDetail, dtReportDate = dtReportDate.Date });
    }
}
