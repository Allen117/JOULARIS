using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 產氣量表葉子層 hourly 聚合 — 純邏輯類（空壓機累積式產氣量 Nm³ totalizer，boundary 相減 + MaxVolume 溢位 + UnitScale 換算）。
/// 給 AirMeterLeafAggregationService（hourly Timer）使用。
/// 輸入單一 SID + 單一小時，回傳 AirMeterLeafHourlyModel 或 null（兩邊邊界都缺 → 不寫，sparse storage）。
/// 演算法與 GasMeterLeafAggregator 完全對稱，兩套刻意各自獨立（改產氣量邏輯不可能弄壞氣費）。
/// 與 Web 端 AirMeterUsageReportService 的 delta 語意必須一致。
/// </summary>
public class AirMeterLeafAggregator
{
    private readonly ILogger<AirMeterLeafAggregator> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public AirMeterLeafAggregator(
        ILogger<AirMeterLeafAggregator> logger,
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
    /// 計算 sid 在 [hourStart, hourStart+1hr) 的產氣量增量（Nm³）。
    /// 三段語意：
    ///   兩邊有 → Q=1 算 delta（原始單位相減後 × UnitScale）
    ///   只缺一邊 → Q=0, Delta=0（掉線 transition）
    ///   兩邊都缺 → 回傳 null（sparse storage，不寫該列）
    /// </summary>
    public async Task<AirMeterLeafHourlyModel?> ComputeAsync(
        string szSid, DateTime dtHourStart, double? dMaxVolume, double dUnitScale,
        int nMaxStalenessHours, string szLeafName = "")
    {
        await EnsureConnectionStringAsync();

        var dtHourEnd = dtHourStart.AddHours(1);
        double? fStart;
        double? fEnd;
        using (var conn = new SqlConnection(_szConnectionString))
        {
            await conn.OpenAsync();
            (fStart, fEnd) = await GetBoundaryValuesAsync(conn, szSid, dtHourStart, dtHourEnd, nMaxStalenessHours);
        }

        // 倒退且無 MaxVolume → delta 視為 0，此處補警告（純邏輯層不做 logging）
        if (fStart.HasValue && fEnd.HasValue && fEnd.Value < fStart.Value
            && !(dMaxVolume.HasValue && dMaxVolume.Value > 0))
        {
            _logger.LogWarning(
                "產氣量表葉子層聚合 {SID} ({Name}) 偵測到累積值倒退（{Start} → {End}）但未設定 MaxVolume，該段 delta 視為 0",
                szSid, szLeafName, fStart.Value, fEnd.Value);
        }

        return ComputeFromBoundaries(szSid, dtHourStart, fStart, fEnd, dMaxVolume, dUnitScale);
    }

    /// <summary>
    /// 純邏輯：由兩個邊界讀數算出該小時的聚合列（可單元測試，不觸 DB）。
    /// 兩邊都缺 → null；只缺一邊 → Q=0 Delta=0；兩邊都有 → delta 套溢位規則後 × UnitScale。
    /// </summary>
    public static AirMeterLeafHourlyModel? ComputeFromBoundaries(
        string szSid, DateTime dtHourStart, double? fStart, double? fEnd, double? dMaxVolume, double dUnitScale)
    {
        // 兩邊都缺 → sparse storage，不寫
        if (fStart == null && fEnd == null)
            return null;

        // 只缺一邊 → 掉線 transition，寫 Q=0 Delta=0
        if (fStart == null || fEnd == null)
        {
            return new AirMeterLeafHourlyModel
            {
                szSID = szSid,
                dtHourStart = dtHourStart,
                dDeltaNm3 = 0,
                nQuality = 0,
                isRolledOver = false
            };
        }

        // 兩邊都有 → 正常 delta（原始單位），套溢位規則後換算成 Nm³
        var (dDeltaRaw, isRolledOver) = CalcDeltaWithRollover(fStart.Value, fEnd.Value, dMaxVolume);
        return new AirMeterLeafHourlyModel
        {
            szSID = szSid,
            dtHourStart = dtHourStart,
            dDeltaNm3 = dDeltaRaw * dUnitScale,
            nQuality = 1,
            isRolledOver = isRolledOver
        };
    }

    /// <summary>
    /// 產氣量溢位/重置 delta（以點位原始單位計）— 與氣表 / 水表 CalcDeltaWithRollover 語意對稱。
    /// V_end &gt;= V_start: 正常累計
    /// V_end &lt; V_start &amp;&amp; MaxVolume 有設: (Max - Vs) + Ve
    /// V_end &lt; V_start &amp;&amp; MaxVolume 無設: 視為產氣量表重置/異常，回 0
    /// </summary>
    public static (double dDelta, bool isRolledOver) CalcDeltaWithRollover(
        double dStart, double dEnd, double? dMaxVolume)
    {
        if (dEnd >= dStart)
            return (dEnd - dStart, false);

        if (dMaxVolume.HasValue && dMaxVolume.Value > 0)
            return ((dMaxVolume.Value - dStart) + dEnd, true);

        return (0, false);
    }

    /// <summary>
    /// 取 sid 在 t0 與 t1 兩個時點各自的「最近一筆」HistoryData 值。
    /// 套 staleness window：source Timestamp 距 boundary &gt; maxStalenessHours 視為 null。
    /// 與 GasMeterLeafAggregator.GetBoundaryValuesAsync 行為一致。
    /// </summary>
    private static async Task<(double? fStart, double? fEnd)> GetBoundaryValuesAsync(
        SqlConnection conn, string szSid, DateTime dtT0, DateTime dtT1, int nMaxStalenessHours)
    {
        const string szSql = @"
            SELECT b.idx, ba.Value FROM (VALUES (0, @t0), (1, @t1)) AS b(idx, BoundaryTime)
            OUTER APPLY (
                SELECT TOP 1 Value FROM HistoryData WITH (NOLOCK)
                WHERE  SID = @sid
                   AND Timestamp <= b.BoundaryTime
                   AND Timestamp >= DATEADD(HOUR, -@maxStalenessHours, b.BoundaryTime)
                   AND Quality = 1
                ORDER BY Timestamp DESC
            ) ba
            ORDER BY b.idx";

        var rows = await conn.QueryAsync<(int idx, double? Value)>(szSql, new
        {
            sid = szSid,
            t0 = dtT0,
            t1 = dtT1,
            maxStalenessHours = nMaxStalenessHours
        });
        double? f0 = null, f1 = null;
        foreach (var r in rows)
        {
            if (r.idx == 0) f0 = r.Value;
            else if (r.idx == 1) f1 = r.Value;
        }
        return (f0, f1);
    }
}
