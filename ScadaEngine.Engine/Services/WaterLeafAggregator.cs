using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 冷凍噸 — 葉子層 hourly 聚合（純邏輯類）。
/// 給 <see cref="WaterLeafAggregationService"/> 共用。
/// 輸入單一 SID + 單一小時，回傳 <see cref="WaterLeafHourlyModel"/> 或 null（資料不足 → 不寫，sparse storage）。
///
/// 演算法 (與 EnergyLeafAggregator 不同)：
///   - RT 是「瞬時冷凍噸」，HistoryData 已被分鐘級 dedup（每分鐘最多 1 筆）
///   - 負 RT 樣本逐筆 clamp 為 0 再平均（冷量不可能為負；與設備效率的 RT 點 / ΔT ≤ 0 規則一致）
///   - 該小時冷量 RT·h = AVG(RT samples) × 1h（rectangle 積分）
///   - SampleCount &lt; MinSamples（預設 30）→ 視為資料不足，回 null 不寫
///     避免「缺資料時段被低報為 0」的物理錯誤
/// </summary>
public class WaterLeafAggregator
{
    private readonly ILogger<WaterLeafAggregator> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public WaterLeafAggregator(
        ILogger<WaterLeafAggregator> logger,
        DatabaseConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    private async Task EnsureConnectionStringAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
    }

    /// <summary>
    /// 計算 sid 在 [hourStart, hourStart+1hr) 的 RT·h。
    /// 樣本數 &lt; nMinSamples → 回 null（sparse storage，不寫該列）
    /// 樣本數 ≥ nMinSamples → 回 WaterLeafHourlyModel（RtHour = AVG × 1h）
    /// </summary>
    public async Task<WaterLeafHourlyModel?> ComputeAsync(
        string szSid, DateTime dtHourStart, int nMinSamples, string szLeafName = "")
    {
        await EnsureConnectionStringAsync();

        var dtHourEnd = dtHourStart.AddHours(1);
        double? fAvg;
        int nCount;
        using (var conn = new SqlConnection(_szConnectionString))
        {
            await conn.OpenAsync();
            (fAvg, nCount) = await GetHourAvgAndCountAsync(conn, szSid, dtHourStart, dtHourEnd);
        }

        if (nCount < nMinSamples || fAvg == null)
            return null;

        // RT·h = AVG(RT) × 1h；單位上 1h 乘以瞬時功率（RT）= 冷量（RT·h）
        return new WaterLeafHourlyModel
        {
            szSID = szSid,
            dtHourStart = dtHourStart,
            dRtHour = fAvg.Value,
            nSampleCount = nCount,
            nQuality = 1,
            isBackfilled = false
        };
    }

    /// <summary>
    /// 取 sid 在 [t0, t1) 區間內所有 Quality=1 樣本的 AVG 與 COUNT。
    /// 負 RT 樣本在逐筆層 clamp 為 0 再平均（冷量不可能為負；負值 = 停機 / 泵停溫差雜訊），
    /// COUNT 仍計入該筆（它是「有量測到沒冷量」，不是缺值）。
    /// </summary>
    private static async Task<(double? fAvg, int nCount)> GetHourAvgAndCountAsync(
        SqlConnection conn, string szSid, DateTime dtT0, DateTime dtT1)
    {
        const string szSql = @"
            SELECT AVG(CASE WHEN Value < 0 THEN 0.0 ELSE CAST(Value AS float) END) AS Avg,
                   COUNT_BIG(1) AS Cnt
            FROM   HistoryData WITH (NOLOCK)
            WHERE  SID = @sid
               AND Timestamp >= @t0
               AND Timestamp <  @t1
               AND Quality = 1";

        var row = await conn.QuerySingleAsync<(double? Avg, long Cnt)>(szSql, new
        {
            sid = szSid,
            t0 = dtT0,
            t1 = dtT1
        });
        return (row.Avg, (int)row.Cnt);
    }
}
