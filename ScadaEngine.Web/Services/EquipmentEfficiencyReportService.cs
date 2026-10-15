using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Web.Features.Ems.Models;
using Calc = ScadaEngine.Common.Equipment.EquipmentMetricCalculator;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 設備效率診斷 — 期間儀表板：讀 EquipmentHourly（Engine 預聚合）依曆制 bucket 加總。
///
/// 規則（<see cref="BuildPeriod"/> 純函式，單元測試鎖住）：
///   - 期間效率 = Σ kWh ÷ Σ 產出（**能量加權**，不是逐時效率的平均）
///   - 只有 Quality=1 的小時進分子分母；Quality=0（產出樣本不足）不計，並反映在覆蓋率
///   - 運轉時數含所有已寫入的小時（kWh 足量即可判定運轉）
///   - 負載分佈：Quality=1 且有 AvgLoadPct 的小時，依 ≤25 / ≤50 / ≤75 / &gt;75 分 bin
///   - 覆蓋率：每台實際 Quality=1 小時 ÷ 應有小時（未來時數不計入分母），報最差那台（同冷凍噸語意）
/// 期間切界：曆制（日 / 曆月 / 曆年），刻意不走任何月結期別 — 沿用 <see cref="RefrigerationTonReportService"/> 的
/// BuildBoundaries / BuildLabels，與 /ChilledWaterCircuitInfo 同語意。
/// </summary>
public class EquipmentEfficiencyReportService
{
    private readonly DatabaseConfigService _configService;
    private readonly EquipmentProfileService _profileService;
    private readonly RefrigerationTonReportService _rtReport;
    private readonly int _nMinCoveragePercent;
    private string _szConnectionString = string.Empty;

    public EquipmentEfficiencyReportService(
        DatabaseConfigService configService,
        EquipmentProfileService profileService,
        RefrigerationTonReportService rtReport,
        IConfiguration configuration)
    {
        _configService = configService;
        _profileService = profileService;
        _rtReport = rtReport;
        _nMinCoveragePercent = configuration.GetValue<int?>("EquipmentEfficiency:MinCoveragePercent") ?? 90;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>期間儀表板；dtEnd 為「含訖」語意（最後一格 bucket 起點，同 ParseCalendarPivot）</summary>
    public async Task<EquipmentPeriodDto> GetPeriodAsync(string szKind, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var profiles = await _profileService.GetAllAsync(szKind, isEnabledOnly: true);
        var boundaries = _rtReport.BuildBoundaries(szGranularity, dtStart, dtEnd);
        var labels = _rtReport.BuildLabels(szGranularity, boundaries);
        var rows = await GetRowsAsync(profiles.Select(p => p.nId).ToArray(), boundaries[0], boundaries[^1]);
        var dto = BuildPeriod(szKind, profiles, rows, boundaries, labels, DateTime.Now, _nMinCoveragePercent);
        dto.granularity = szGranularity;
        return dto;
    }

    /// <summary>本曆月 1 號 ~ 現在的總計（EMS 卡片底部用）</summary>
    public async Task<EquipmentPeriodTotalsDto> GetMonthToDateAsync(string szKind, List<EquipmentProfileModel> profiles)
    {
        var dtNow = DateTime.Now;
        var dtMonth = new DateTime(dtNow.Year, dtNow.Month, 1);
        var boundaries = new List<DateTime> { dtMonth, dtMonth.AddMonths(1) };
        var rows = await GetRowsAsync(profiles.Select(p => p.nId).ToArray(), dtMonth, dtMonth.AddMonths(1));
        return BuildPeriod(szKind, profiles, rows, boundaries, ["month"], dtNow, _nMinCoveragePercent).totals;
    }

    private async Task<List<EquipmentHourlyModel>> GetRowsAsync(int[] ids, DateTime dtFrom, DateTime dtToExclusive)
    {
        if (ids.Length == 0) return [];
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<EquipmentHourlyModel>(@"
            SELECT ProfileId AS nProfileId, HourStart AS dtHourStart, Kwh AS dKwh, OutputQty AS dOutputQty,
                   RunHours AS dRunHours, AvgLoadPct AS dAvgLoadPct, LoadedHours AS dLoadedHours,
                   AvgPressure AS dAvgPressure, SampleCount AS nSampleCount, Quality AS nQuality,
                   IsBackfilled AS isBackfilled, CreatedAt AS dtCreatedAt
            FROM   EquipmentHourly WITH (NOLOCK)
            WHERE  ProfileId IN @Ids AND HourStart >= @From AND HourStart < @To",
            new { Ids = ids, From = dtFrom, To = dtToExclusive });
        return rows.ToList();
    }

    /// <summary>累加器（kWh / 產出只收 Quality=1；運轉時數收所有列）</summary>
    private sealed class Acc
    {
        public double dKwh, dOutput, dRun, dLoadWeighted, dLoadRun, dLoaded, dLoadedRun, dPressWeighted, dPressRun;

        public void Add(EquipmentHourlyModel r)
        {
            dRun += r.dRunHours;
            if (r.dLoadedHours != null) { dLoaded += r.dLoadedHours.Value; dLoadedRun += r.dRunHours; }
            if (r.dAvgPressure != null && r.dRunHours > 0) { dPressWeighted += r.dAvgPressure.Value * r.dRunHours; dPressRun += r.dRunHours; }
            if (r.nQuality != 1 || r.dOutputQty == null) return;
            dKwh += r.dKwh;
            dOutput += r.dOutputQty.Value;
            if (r.dAvgLoadPct != null && r.dRunHours > 0) { dLoadWeighted += r.dAvgLoadPct.Value * r.dRunHours; dLoadRun += r.dRunHours; }
        }

        public EquipmentPeriodTotalsDto ToTotals(string szKind)
        {
            var m = Calc.ComputePeriod(szKind, dKwh, dOutput, dRun);
            return new EquipmentPeriodTotalsDto
            {
                kwh = Math.Round(dKwh, 3),
                output = Math.Round(dOutput, 3),
                primary = m.dPrimary,
                secondary = m.dSecondary,
                tertiary = m.dTertiary,
                runHours = Math.Round(dRun, 2),
                avgLoadPct = dLoadRun > 0 ? dLoadWeighted / dLoadRun : null,
                loadedRatio = dLoadedRun > 0 ? Math.Min(1.0, dLoaded / dLoadedRun) : null,
                avgPressure = dPressRun > 0 ? dPressWeighted / dPressRun : null,
            };
        }
    }

    /// <summary>
    /// 純函式：設備 + hourly 列 + bucket 邊界（N+1 個）→ 期間儀表板 DTO。
    /// </summary>
    public static EquipmentPeriodDto BuildPeriod(
        string szKind,
        IReadOnlyList<EquipmentProfileModel> profiles,
        IReadOnlyList<EquipmentHourlyModel> rows,
        IReadOnlyList<DateTime> boundaries,
        IReadOnlyList<string> labels,
        DateTime dtNow,
        int nMinCoveragePercent)
    {
        var nBuckets = boundaries.Count - 1;
        var dto = new EquipmentPeriodDto
        {
            kind = szKind,
            deviceCount = profiles.Count,
            labels = labels.ToList(),
        };
        if (nBuckets <= 0) return dto;

        var total = new Acc();
        var fleet = Enumerable.Range(0, nBuckets).Select(_ => new Acc()).ToArray();
        var bins = Enumerable.Range(0, Calc.LoadBinCount).Select(_ => new Acc()).ToArray();
        var byProfile = rows.GroupBy(r => r.nProfileId).ToDictionary(g => g.Key, g => g.ToList());

        // 應有小時數：bucket 起訖夾到「現在」，未來時數不計入分母
        var nExpected = (int)Math.Round(Enumerable.Range(0, nBuckets).Sum(i =>
        {
            var dtS = boundaries[i] < dtNow ? boundaries[i] : dtNow;
            var dtE = boundaries[i + 1] < dtNow ? boundaries[i + 1] : dtNow;
            var dHours = (dtE - dtS).TotalHours;
            return dHours > 0 ? dHours : 0;
        }));
        var coverage = new EquipmentCoverageDto { thresholdPct = nMinCoveragePercent, expectedHours = nExpected, actualHours = nExpected };

        foreach (var p in profiles)
        {
            var (dDefGood, dDefFair) = EquipmentKind.DefaultThresholds(p.szKind);
            var dGood = p.dGoodThreshold ?? dDefGood;
            var dFair = p.dFairThreshold ?? dDefFair;
            var acc = new Acc();
            var perBucket = Enumerable.Range(0, nBuckets).Select(_ => new Acc()).ToArray();
            var nGoodHours = 0;

            var list = byProfile.TryGetValue(p.nId, out var found) ? found : new List<EquipmentHourlyModel>();
            foreach (var r in list)
            {
                var nIdx = FindBucketIndex(boundaries, r.dtHourStart);
                if (nIdx < 0) continue;
                acc.Add(r);
                perBucket[nIdx].Add(r);
                fleet[nIdx].Add(r);
                total.Add(r);
                if (r.nQuality == 1 && r.dOutputQty != null)
                {
                    if (r.dtHourStart < dtNow) nGoodHours++;
                    var nBin = Calc.LoadBinIndex(r.dAvgLoadPct);
                    if (nBin >= 0) bins[nBin].Add(r);
                }
            }

            dto.series.Add(new EquipmentTrendSeriesDto
            {
                id = p.nId,
                name = p.szName,
                values = perBucket.Select(b => Calc.ComputePeriod(szKind, b.dKwh, b.dOutput, b.dRun).dPrimary).ToList(),
            });

            var t = acc.ToTotals(szKind);
            var dCoverage = RefrigerationTonReportService.CalcCoveragePercent(nGoodHours, nExpected);
            dto.ranking.Add(new EquipmentRankDto
            {
                id = p.nId,
                name = p.szName,
                kwh = t.kwh,
                output = t.output,
                primary = t.primary,
                secondary = t.secondary,
                runHours = t.runHours,
                avgLoadPct = t.avgLoadPct,
                grade = RankGrade(t.primary, acc.dRun, list.Count > 0, dGood, dFair).ToString(),
                good = dGood,
                fair = dFair,
                coveragePct = Math.Round(dCoverage, 1),
            });

            if (nExpected > 0 && dCoverage < coverage.coveragePct)
            {
                coverage.coveragePct = Math.Round(dCoverage, 1);
                coverage.actualHours = nGoodHours;
                coverage.missingHours = Math.Max(0, nExpected - nGoodHours);
                coverage.worstName = p.szName;
            }
        }

        coverage.isBelowThreshold = profiles.Count > 0 && nExpected > 0 && coverage.coveragePct < nMinCoveragePercent;
        dto.coverage = coverage;
        dto.totals = total.ToTotals(szKind);
        dto.fleet = fleet.Select(b => Calc.ComputePeriod(szKind, b.dKwh, b.dOutput, b.dRun).dPrimary).ToList();

        var dBinKwh = bins.Sum(b => b.dKwh);
        var dBinOut = bins.Sum(b => b.dOutput);
        dto.loadBins = bins.Select((b, i) => new EquipmentLoadBinDto
        {
            bin = i,
            hours = Math.Round(b.dRun, 2),
            kwh = Math.Round(b.dKwh, 3),
            output = Math.Round(b.dOutput, 3),
            kwhPct = dBinKwh > 0 ? Math.Round(b.dKwh / dBinKwh * 100.0, 1) : 0,
            outputPct = dBinOut > 0 ? Math.Round(b.dOutput / dBinOut * 100.0, 1) : 0,
            primary = Calc.ComputePeriod(szKind, b.dKwh, b.dOutput, b.dRun).dPrimary,
        }).ToList();

        // 排名：主指標越低越好；無值（停機 / 資料不足）排最後
        dto.ranking = dto.ranking
            .OrderBy(r => r.primary == null ? 1 : 0)
            .ThenBy(r => r.primary ?? double.MaxValue)
            .ThenBy(r => r.name, StringComparer.Ordinal)
            .ToList();
        return dto;
    }

    /// <summary>期間評級：有效率值 → 依門檻；整期有資料但都沒運轉 → Off；其餘 → NoData</summary>
    public static EquipmentGrade RankGrade(double? dPrimary, double dRunHours, bool hasRows, double dGood, double dFair)
    {
        if (dPrimary != null) return Calc.Grade(dPrimary, dGood, dFair);
        return hasRows && dRunHours <= 0 ? EquipmentGrade.Off : EquipmentGrade.NoData;
    }

    /// <summary>boundaries[i] ≤ t &lt; boundaries[i+1] 的 i；範圍外 -1</summary>
    private static int FindBucketIndex(IReadOnlyList<DateTime> boundaries, DateTime dt)
    {
        int lo = 0, hi = boundaries.Count - 1;
        if (dt < boundaries[0] || dt >= boundaries[hi]) return -1;
        while (lo < hi - 1)
        {
            var mid = (lo + hi) >> 1;
            if (dt < boundaries[mid]) hi = mid;
            else lo = mid;
        }
        return lo;
    }
}
