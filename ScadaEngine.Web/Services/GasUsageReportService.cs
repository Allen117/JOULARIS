using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 用氣報表 — 直接加總 Engine 預聚合表 GasMeterLeafHourly（sparse storage，缺小時=無列）。
/// 流程：依粒度產生 N 個 bucket 的 [起, 訖) 邊界對（月粒度 = **氣費**期別，見 GasBillingPeriodService，
/// 已刪除的期別不產生 bucket → 兩月一期時一年 6 個 bucket）
/// → 一次撈出迴路下所有葉子在總區間內的 hourly 列 →
/// 每列依 HourStart 落入的 bucket 累加 DeltaM3 × 該葉子 EffectiveSign = 該 bucket 用氣量。
/// DeltaM3 已由 Engine 換算為 m³（UnitScale / 溢位皆已處理），此處不再套 dUnitScale / dMaxVolume。
/// 未來時段 bucket 自然無列 → 0，無需夾 clamp。
/// </summary>
public class GasUsageReportService
{
    private readonly ILogger<GasUsageReportService> _logger;
    private readonly DatabaseConfigService _configService;
    private readonly GasMeterCircuitService _circuitService;
    private readonly GasBillingPeriodService _billingPeriodService;
    private readonly ShiftScheduleService _shiftScheduleService;
    private string _szConnectionString = string.Empty;

    public GasUsageReportService(
        ILogger<GasUsageReportService> logger,
        DatabaseConfigService configService,
        GasMeterCircuitService circuitService,
        GasBillingPeriodService billingPeriodService,
        ShiftScheduleService shiftScheduleService)
    {
        _logger = logger;
        _configService = configService;
        _circuitService = circuitService;
        _billingPeriodService = billingPeriodService;
        _shiftScheduleService = shiftScheduleService;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// 取得報表結果。
    /// </summary>
    /// <param name="nCircuitId">天然氣表迴路 Id（葉子或虛擬皆可）</param>
    /// <param name="szGranularity">hour / day / month / year / shift</param>
    /// <param name="dtStart">區間起點（含），時/日/班別粒度需精確到天，月需精確到月，年需精確到年</param>
    /// <param name="dtEnd">區間終點（含），同上</param>
    public async Task<GasUsageReportResult> GetReportAsync(
        int nCircuitId, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var circuit = await GetCircuitOrThrowAsync(nCircuitId);
        var (calcRanges, labels, displayRanges, groupOf) = await BuildBucketRangesAsync(szGranularity, dtStart, dtEnd);

        var result = new GasUsageReportResult
        {
            nCircuitId = nCircuitId,
            szCircuitName = circuit.szName,
            szGranularity = szGranularity,
            dtStart = displayRanges.Count > 0 ? displayRanges[0].dtStart : dtStart,
            dtEnd = displayRanges.Count > 0 ? displayRanges[^1].dtEnd : dtEnd,
        };

        using var conn = await GetConnectionAsync();
        var (bucketSums, bHasWarning, staleFlags) =
            await ComputeDisplayBucketSumsAsync(nCircuitId, calcRanges, groupOf, labels.Count, conn);

        FillBucketsAndTotal(result, displayRanges, labels, bucketSums, staleFlags);
        result.isHasWarning = bHasWarning;
        return result;
    }

    /// <summary>
    /// 同 GetReportAsync，再額外展開「直接子迴路」每個的 bucket series — 給 Excel 匯出多欄使用。
    /// 若查詢的迴路本身就是葉子（綁 SID），children 保持空，匯出格式維持 2 欄。
    /// </summary>
    public async Task<GasUsageReportResult> GetReportWithChildrenAsync(
        int nCircuitId, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var circuit = await GetCircuitOrThrowAsync(nCircuitId);
        var (calcRanges, labels, displayRanges, groupOf) = await BuildBucketRangesAsync(szGranularity, dtStart, dtEnd);

        var result = new GasUsageReportResult
        {
            nCircuitId = nCircuitId,
            szCircuitName = circuit.szName,
            szGranularity = szGranularity,
            dtStart = displayRanges.Count > 0 ? displayRanges[0].dtStart : dtStart,
            dtEnd = displayRanges.Count > 0 ? displayRanges[^1].dtEnd : dtEnd,
        };

        using var conn = await GetConnectionAsync();
        var (bucketSums, bHasWarning, staleFlags) =
            await ComputeDisplayBucketSumsAsync(nCircuitId, calcRanges, groupOf, labels.Count, conn);
        FillBucketsAndTotal(result, displayRanges, labels, bucketSums, staleFlags);
        result.isHasWarning = bHasWarning;

        // 自己就是葉子 → 不展開子層（匯出格式維持 2 欄）
        if (!string.IsNullOrEmpty(circuit.szSID))
            return result;

        var children = await _circuitService.GetDirectChildrenAsync(nCircuitId);
        foreach (var child in children)
        {
            // 子迴路內部 leaves 的 sign 已由 GetLeavesUnderAsync 累乘（相對於 child），
            // child 自己對父的方向需在這裡額外乘上。
            var (childSums, childWarning, _) =
                await ComputeDisplayBucketSumsAsync(child.nId, calcRanges, groupOf, labels.Count, conn);
            var nChildSign = child.nSign == -1 ? -1 : 1;
            var series = new GasUsageReportChildSeries
            {
                nCircuitId = child.nId,
                szName = child.szName,
            };
            double dTotal = 0;
            for (var i = 0; i < labels.Count; i++)
            {
                var dValue = childSums[i] * nChildSign;
                series.dM3PerBucket.Add(Math.Round(dValue, 3));
                dTotal += dValue;
            }
            series.dTotalM3 = Math.Round(dTotal, 3);
            if (childWarning) result.isHasWarning = true;
            result.children.Add(series);
        }

        return result;
    }

    /// <summary>
    /// 取得指定迴路在單一 [起, 訖) 區間內的總用氣量（m³）與資料完整性警告。
    /// 與 GetReportAsync 共用同一計算核心，確保「期間總量」與長條圖各柱總和完全對得上。
    /// 注意：回傳值未套用迴路自身對父層的 Sign — 子迴路呼叫端需比照 GetReportWithChildrenAsync 額外乘上。
    /// </summary>
    public async Task<(double dTotalM3, bool isHasWarning)> GetTotalM3Async(
        int nCircuitId, DateTime dtStart, DateTime dtEndExclusive)
    {
        using var conn = await GetConnectionAsync();
        var ranges = new List<(DateTime dtStart, DateTime dtEnd)> { (dtStart, dtEndExclusive) };
        var (bucketSums, bHasWarning, _) = await ComputeBucketSumsForCircuitAsync(nCircuitId, ranges, conn);
        return (Math.Round(bucketSums[0], 3), bHasWarning);
    }

    /// <summary>
    /// 取得指定迴路在某粒度區間內的總用氣量（m³）與資料完整性警告 = 該粒度所有 bucket 的加總。
    /// 與 GetReportAsync 共用同一 bucket 切法（月粒度走期別），確保「期間總量」與長條圖各柱總和完全對得上。
    /// 注意：回傳值未套用迴路自身對父層的 Sign — 子迴路呼叫端需比照 GetReportWithChildrenAsync 額外乘上。
    /// </summary>
    public async Task<(double dTotalM3, bool isHasWarning)> GetTotalM3Async(
        int nCircuitId, string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        // 總量 = 各計算區間加總，與是否合併為顯示 bucket 無關 → 直接用 calcRanges
        var (calcRanges, _, _, _) = await BuildBucketRangesAsync(szGranularity, dtStart, dtEnd);
        using var conn = await GetConnectionAsync();
        var (bucketSums, bHasWarning, _) = await ComputeBucketSumsForCircuitAsync(nCircuitId, calcRanges, conn);
        return (Math.Round(bucketSums.Sum(), 3), bHasWarning);
    }

    private async Task<GasMeterCircuitModel> GetCircuitOrThrowAsync(int nCircuitId)
    {
        var all = await _circuitService.GetAllAsync();
        var circuit = all.FirstOrDefault(c => c.nId == nCircuitId);
        if (circuit == null)
            throw new InvalidOperationException($"天然氣表迴路 Id={nCircuitId} 不存在");
        return circuit;
    }

    /// <summary>
    /// 產生 bucket 計畫：calcRanges 供計算、displayRanges/labels 供顯示、
    /// groupOf 非 null 時（班別粒度）表示 calcRanges[i] 屬於第 groupOf[i] 個顯示 bucket。
    /// 月粒度 = 氣費期別：dtStart/dtEnd 的年月視為期別編號，期界由 GasBillingPeriodService 解析
    /// （期別間可能空窗/重疊 → 不共用邊界點；已刪除期別不產生 bucket）；班別粒度由 ShiftScheduleService 展開；
    /// 其餘粒度沿用連續邊界切法（calcRanges == displayRanges、groupOf = null）。
    /// </summary>
    private async Task<(List<(DateTime dtStart, DateTime dtEnd)> calcRanges, List<string> labels,
            List<(DateTime dtStart, DateTime dtEnd)> displayRanges, List<int>? groupOf)>
        BuildBucketRangesAsync(string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        if (szGranularity == "month")
        {
            var periods = await _billingPeriodService.GetPeriodRangesAsync(dtStart, dtEnd);
            var monthRanges = periods.Select(p => (p.dtStart, p.dtEndExclusive)).ToList();
            return (monthRanges, periods.Select(p => p.szLabel).ToList(), monthRanges, null);
        }
        if (szGranularity == "shift")
        {
            var plan = await _shiftScheduleService.BuildBucketPlanAsync(dtStart, dtEnd);
            return (plan.flatRanges, plan.displayLabels, plan.displayRanges, plan.groupOf);
        }
        var boundaries = BuildBoundaries(szGranularity, dtStart, dtEnd);
        var ranges = new List<(DateTime, DateTime)>(boundaries.Count - 1);
        for (var i = 0; i < boundaries.Count - 1; i++)
            ranges.Add((boundaries[i], boundaries[i + 1]));
        return (ranges, BuildLabels(szGranularity, boundaries), ranges, null);
    }

    /// <summary>
    /// 計算 + （班別粒度時）依 groupOf 合併為顯示 bucket。
    /// groupOf = null（一般粒度）→ 直接回傳計算結果。
    /// </summary>
    private async Task<(double[] bucketSums, bool isHasWarning, bool[] staleFlags)> ComputeDisplayBucketSumsAsync(
        int nCircuitId, List<(DateTime dtStart, DateTime dtEnd)> calcRanges, List<int>? groupOf,
        int nDisplayBuckets, SqlConnection conn)
    {
        var (flatSums, bHasWarning, flatStale) = await ComputeBucketSumsForCircuitAsync(nCircuitId, calcRanges, conn);
        if (groupOf == null) return (flatSums, bHasWarning, flatStale);
        var (sums, stale) = ShiftScheduleService.MergeByGroup(flatSums, flatStale, groupOf, nDisplayBuckets);
        return (sums, bHasWarning, stale);
    }

    /// <summary>
    /// 對單一迴路（葉子或虛擬皆可）計算每個 bucket 的 m³ 累計。
    /// 核心：取葉子 → 一次撈總區間內所有葉子的 GasMeterLeafHourly 列 →
    /// 依 HourStart 落入的 bucket 累加 DeltaM3 × EffectiveSign（期別 bucket 可能重疊 → 逐 bucket 判斷）。
    /// Quality=0（掉線 transition）列：數值仍計入（Engine 已寫入可用的 delta），
    /// 但該 bucket 標記 isStale 供前端提示資料不完整。
    /// </summary>
    private async Task<(double[] bucketSums, bool isHasWarning, bool[] staleFlags)> ComputeBucketSumsForCircuitAsync(
        int nCircuitId, List<(DateTime dtStart, DateTime dtEnd)> ranges, SqlConnection conn)
    {
        var nBuckets = ranges.Count;
        var bucketSums = new double[nBuckets];
        var staleFlags = new bool[nBuckets];
        if (nBuckets == 0) return (bucketSums, false, staleFlags);

        var leaves = await _circuitService.GetLeavesUnderAsync(nCircuitId);
        // SID → 有效方向；同一 SID 若出現在多個分支則方向相加（+1 + -1 = 0 自然抵銷）
        var signBySid = new Dictionary<string, int>();
        foreach (var leafWithSign in leaves)
        {
            var szSid = leafWithSign.Leaf.szSID;
            if (string.IsNullOrEmpty(szSid)) continue;
            signBySid[szSid] = signBySid.GetValueOrDefault(szSid) + leafWithSign.nEffectiveSign;
        }
        if (signBySid.Count == 0) return (bucketSums, false, staleFlags);

        var dtMin = ranges.Min(r => r.dtStart);
        var dtMax = ranges.Max(r => r.dtEnd);

        var rows = await conn.QueryAsync<(string SID, DateTime HourStart, double DeltaM3, byte Quality)>(
            @"SELECT SID, HourStart, DeltaM3, Quality
              FROM GasMeterLeafHourly WITH (NOLOCK)
              WHERE SID IN @sids AND HourStart >= @dtMin AND HourStart < @dtMax",
            new { sids = signBySid.Keys.ToList(), dtMin, dtMax });

        foreach (var row in rows)
        {
            if (!signBySid.TryGetValue(row.SID, out var nSign)) continue;
            for (var i = 0; i < nBuckets; i++)
            {
                if (row.HourStart < ranges[i].dtStart || row.HourStart >= ranges[i].dtEnd) continue;
                bucketSums[i] += row.DeltaM3 * nSign;
                if (row.Quality == 0) staleFlags[i] = true;
            }
        }

        var bHasWarning = staleFlags.Any(f => f);
        return (bucketSums, bHasWarning, staleFlags);
    }

    private static void FillBucketsAndTotal(
        GasUsageReportResult result, List<(DateTime dtStart, DateTime dtEnd)> ranges, List<string> labels,
        double[] bucketSums, bool[] staleFlags)
    {
        for (var i = 0; i < labels.Count; i++)
        {
            result.buckets.Add(new GasUsageReportBucket
            {
                dtBucketStart = ranges[i].dtStart,
                szLabel = labels[i],
                dM3 = Math.Round(bucketSums[i], 3),
                isStale = staleFlags[i]
            });
            result.dTotalM3 += bucketSums[i];
        }
        result.dTotalM3 = Math.Round(result.dTotalM3, 3);
    }

    /// <summary>產生 N+1 個邊界時刻（含起點與終點）— 切法與 WaterUsageReportService 完全一致</summary>
    public List<DateTime> BuildBoundaries(string szGranularity, DateTime dtStart, DateTime dtEnd)
    {
        var list = new List<DateTime>();
        switch (szGranularity)
        {
            case "hour":
                {
                    // dtStart=起時、dtEnd=訖時（皆截到整點）；產出起時 ~ 訖時隔小時的每小時邊界
                    var hourStart = new DateTime(dtStart.Year, dtStart.Month, dtStart.Day, dtStart.Hour, 0, 0);
                    var hourEndInclusive = new DateTime(dtEnd.Year, dtEnd.Month, dtEnd.Day, dtEnd.Hour, 0, 0);
                    var hourEndExclusive = hourEndInclusive.AddHours(1);
                    if (hourEndExclusive <= hourStart)
                        hourEndExclusive = hourStart.AddHours(1);
                    for (var t = hourStart; t <= hourEndExclusive; t = t.AddHours(1))
                        list.Add(t);
                    break;
                }
            case "day":
                {
                    // dtStart=起日，dtEnd=訖日；產出起日 00:00 ~ 訖日隔日 00:00 的每日邊界
                    var dayStart = dtStart.Date;
                    var dayEndExclusive = dtEnd.Date.AddDays(1);
                    if (dayEndExclusive <= dayStart)
                        dayEndExclusive = dayStart.AddDays(1);
                    for (var t = dayStart; t <= dayEndExclusive; t = t.AddDays(1))
                        list.Add(t);
                    break;
                }
            case "month":
                // 月粒度期界由 GasBillingPeriodService 解析（每期一對 [起, 訖) 邊界，可能不連續、可能被刪除），
                // 不能用單一連續邊界列表表達 — 走 BuildBucketRangesAsync
                throw new ArgumentException("月粒度期界由 GasBillingPeriodService 解析，不支援 BuildBoundaries");
            case "year":
                {
                    // dtStart=當年 1/1，dtEnd=當年 1/1
                    var t = new DateTime(dtStart.Year, 1, 1);
                    var endYear = new DateTime(dtEnd.Year, 1, 1);
                    while (t <= endYear)
                    {
                        list.Add(t);
                        t = t.AddYears(1);
                    }
                    list.Add(endYear.AddYears(1));
                    break;
                }
            default:
                throw new ArgumentException($"未知粒度: {szGranularity}");
        }
        return list;
    }

    /// <summary>由邊界陣列產出 N 個 bucket 的顯示標籤 — 格式與 WaterUsageReportService 完全一致</summary>
    public List<string> BuildLabels(string szGranularity, List<DateTime> boundaries)
    {
        var ci = CultureInfo.InvariantCulture;
        var labels = new List<string>(boundaries.Count - 1);
        // 日粒度跨年時用 yyyy-MM-dd 避免 MM/dd 重複
        var bDayCrossYear = szGranularity == "day"
            && boundaries.Count >= 2
            && boundaries[0].Year != boundaries[^2].Year;
        // 時粒度跨日時加上日期前綴避免 HH:00 重複（同年用 MM/dd HH:00，跨年用 yyyy-MM-dd HH:00）
        var bHourCrossDay = szGranularity == "hour"
            && boundaries.Count >= 2
            && boundaries[0].Date != boundaries[^2].Date;
        var bHourCrossYear = bHourCrossDay && boundaries[0].Year != boundaries[^2].Year;
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var t = boundaries[i];
            labels.Add(szGranularity switch
            {
                "hour" => bHourCrossYear ? t.ToString("yyyy-MM-dd HH:00", ci)
                        : bHourCrossDay ? t.ToString("MM/dd HH:00", ci)
                        : t.ToString("HH:00", ci),
                "day" => bDayCrossYear ? t.ToString("yyyy-MM-dd", ci) : t.ToString("MM/dd", ci),
                "month" => t.ToString("yyyy-MM", ci),
                "year" => t.ToString("yyyy", ci),
                _ => t.ToString("yyyy-MM-dd HH:mm", ci)
            });
        }
        return labels;
    }
}
