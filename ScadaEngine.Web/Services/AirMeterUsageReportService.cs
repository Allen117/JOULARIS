using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 產氣量曆月查詢 — 直接加總 Engine 預聚合表 AirMeterLeafHourly（sparse storage，缺小時 = 無列）。
/// 只提供空壓機能源申報需要的 <see cref="GetCalendarMonthlyReportAsync"/>（曆月切界，不走任何期別），
/// 不做獨立產氣報表頁。
///
/// 計算核心與 <see cref="GasUsageReportService"/> 同構：取迴路下所有葉子 → 一次撈總區間內的 hourly 列 →
/// 依 HourStart 落入的 bucket 累加 DeltaNm3 × 葉子 EffectiveSign（sign / 虛擬節點 on-demand，迴路結構改變即時生效）。
/// DeltaNm3 已由 Engine 換算為 Nm³（UnitScale / 溢位皆已處理），此處不再套 dUnitScale / dMaxVolume。
/// </summary>
public class AirMeterUsageReportService
{
    private readonly ILogger<AirMeterUsageReportService> _logger;
    private readonly DatabaseConfigService _configService;
    private readonly AirMeterCircuitService _circuitService;
    private string _szConnectionString = string.Empty;

    public AirMeterUsageReportService(
        ILogger<AirMeterUsageReportService> logger,
        DatabaseConfigService configService,
        AirMeterCircuitService circuitService)
    {
        _logger = logger;
        _configService = configService;
        _circuitService = circuitService;
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
    /// 指定年度 12 個曆月（每月 1 號 00:00 ~ 次月 1 號 00:00）的產氣量 Nm³。
    /// 切界共用 <see cref="EnergyReportService.BuildCalendarMonthRanges"/>，與 kWh 側 bucket 必然逐一對齊。
    /// 迴路不存在 → 擲 InvalidOperationException（申報 Service 據此轉成「來源已不存在」訊息）。
    /// </summary>
    public async Task<AirMeterUsageReportResult> GetCalendarMonthlyReportAsync(int nCircuitId, int nYear)
    {
        var circuit = await _circuitService.GetByIdAsync(nCircuitId);
        if (circuit == null)
            throw new InvalidOperationException($"產氣量表迴路 Id={nCircuitId} 不存在");

        var (ranges, labels) = EnergyReportService.BuildCalendarMonthRanges(nYear);

        var result = new AirMeterUsageReportResult
        {
            nCircuitId = nCircuitId,
            szCircuitName = circuit.szName,
            dtStart = ranges[0].dtStart,
            dtEnd = ranges[^1].dtEnd,
        };

        using var conn = await GetConnectionAsync();
        var (bucketSums, staleFlags) = await ComputeBucketSumsForCircuitAsync(nCircuitId, ranges, conn);

        for (var i = 0; i < labels.Count; i++)
        {
            result.buckets.Add(new AirMeterUsageReportBucket
            {
                dtBucketStart = ranges[i].dtStart,
                szLabel = labels[i],
                dNm3 = Math.Round(bucketSums[i], 3),
                isStale = staleFlags[i]
            });
            result.dTotalNm3 += bucketSums[i];
        }
        result.dTotalNm3 = Math.Round(result.dTotalNm3, 3);
        result.isHasWarning = staleFlags.Any(f => f);
        return result;
    }

    /// <summary>
    /// 對單一迴路（葉子或虛擬皆可）計算每個 bucket 的 Nm³ 累計。
    /// Quality=0（掉線 transition）列：數值仍計入（恆為 0），但該 bucket 標記 isStale 供前端提示資料不完整。
    /// </summary>
    private async Task<(double[] bucketSums, bool[] staleFlags)> ComputeBucketSumsForCircuitAsync(
        int nCircuitId, List<(DateTime dtStart, DateTime dtEnd)> ranges, SqlConnection conn)
    {
        var nBuckets = ranges.Count;
        var bucketSums = new double[nBuckets];
        var staleFlags = new bool[nBuckets];
        if (nBuckets == 0) return (bucketSums, staleFlags);

        var leaves = await _circuitService.GetLeavesUnderAsync(nCircuitId);
        // SID → 有效方向；同一 SID 若出現在多個分支則方向相加（+1 + -1 = 0 自然抵銷）
        var signBySid = new Dictionary<string, int>();
        foreach (var leafWithSign in leaves)
        {
            var szSid = leafWithSign.Leaf.szSID;
            if (string.IsNullOrEmpty(szSid)) continue;
            signBySid[szSid] = signBySid.GetValueOrDefault(szSid) + leafWithSign.nEffectiveSign;
        }
        if (signBySid.Count == 0) return (bucketSums, staleFlags);

        var dtMin = ranges.Min(r => r.dtStart);
        var dtMax = ranges.Max(r => r.dtEnd);

        var rows = await conn.QueryAsync<(string SID, DateTime HourStart, double DeltaNm3, byte Quality)>(
            @"SELECT SID, HourStart, DeltaNm3, Quality
              FROM AirMeterLeafHourly WITH (NOLOCK)
              WHERE SID IN @sids AND HourStart >= @dtMin AND HourStart < @dtMax",
            new { sids = signBySid.Keys.ToList(), dtMin, dtMax });

        foreach (var row in rows)
        {
            if (!signBySid.TryGetValue(row.SID, out var nSign)) continue;
            for (var i = 0; i < nBuckets; i++)
            {
                if (row.HourStart < ranges[i].dtStart || row.HourStart >= ranges[i].dtEnd) continue;
                bucketSums[i] += row.DeltaNm3 * nSign;
                if (row.Quality == 0) staleFlags[i] = true;
            }
        }

        return (bucketSums, staleFlags);
    }
}
