using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Common.Equipment;
using Calc = ScadaEngine.Common.Equipment.EquipmentMetricCalculator;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 設備效率診斷 — 單台設備 × 單小時 hourly 聚合。
/// 給 <see cref="EquipmentHourlyAggregationService"/>（XX:06 排程 + catch-up）與
/// <see cref="EquipmentHourlyBackfillSubscriber"/>（MQTT 觸發）共用。
///
/// DB 存取（<see cref="ComputeAsync"/>）只做兩件事：
///   ① 一次查出該設備所有角色 SID 在該小時的「分鐘平均」樣本（HistoryData 已分鐘級 dedup）
///   ② 有 KWH 累積點時，查兩端邊界值算 kWh 增量（同 EnergyLeafAggregator：staleness window + MaxKwh 溢位）
/// 其餘全部在純函式 <see cref="ComputeFromSamples"/>（單元測試鎖住）：
///   - kWh：KWH 邊界相減優先；KWH 未綁或兩端缺值 → AVG(kW) × 1h
///   - 冰機冷量：FLOW + CHWS + CHWR 三者齊 → **分鐘對齊**逐分鐘算 RT 再 AVG × 1h（變流量系統不可用 AVG(flow) × AVG(ΔT)）；
///              否則 RT 點 AVG × 1h
///   - 空壓機空氣量：AVG(m³/min) × 60 → m³
///   - RunHours：RUN 點樣本占比；無 RUN 點 → kW &gt; 額定 × 5% 樣本占比；只有 KWH → 本小時 kWh &gt; 門檻即 1
/// </summary>
public class EquipmentHourlyAggregator
{
    private readonly ILogger<EquipmentHourlyAggregator> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public EquipmentHourlyAggregator(ILogger<EquipmentHourlyAggregator> logger, DatabaseConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    /// <summary>角色 → (分鐘 offset 0..59 → 已換算到基準單位的值)</summary>
    public sealed class RoleSamples : Dictionary<string, Dictionary<int, double>>
    {
        public Dictionary<int, double> Get(string szRole) =>
            TryGetValue(szRole, out var d) ? d : [];
    }

    /// <summary>計算一台設備一小時；資料不足（kWh 無法求得）→ null（不寫列）</summary>
    public async Task<EquipmentHourlyModel?> ComputeAsync(
        EquipmentProfileModel profile, DateTime dtHourStart, int nMinSamples, int nMaxStalenessHours)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();

        var dtHourEnd = dtHourStart.AddHours(1);
        var samples = new RoleSamples();
        double? dKwhDelta = null;

        using (var conn = new SqlConnection(_szConnectionString))
        {
            await conn.OpenAsync();

            // ① 所有瞬時角色的分鐘樣本（KWH 是累積值，不走分鐘平均）
            var instant = profile.aBindings
                .Where(b => b.szRole != EquipmentRoles.KWH && !string.IsNullOrEmpty(b.szSID))
                .ToList();
            var aSids = instant.Select(b => b.szSID).Distinct().ToArray();
            if (aSids.Length > 0)
            {
                var rows = await conn.QueryAsync<(string SID, int M, double V)>(@"
                    SELECT SID, DATEDIFF(MINUTE, @t0, Timestamp) AS M, AVG(CAST(Value AS float)) AS V
                    FROM   HistoryData WITH (NOLOCK)
                    WHERE  SID IN @sids AND Timestamp >= @t0 AND Timestamp < @t1 AND Quality = 1
                    GROUP BY SID, DATEDIFF(MINUTE, @t0, Timestamp)",
                    new { sids = aSids, t0 = dtHourStart, t1 = dtHourEnd });
                var bySid = rows.GroupBy(r => r.SID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var b in instant)
                {
                    if (!bySid.TryGetValue(b.szSID, out var list)) continue;
                    var dScale = b.dScale == 0 ? 1 : b.dScale;
                    var d = new Dictionary<int, double>();
                    foreach (var r in list)
                        if (r.M >= 0 && r.M < 60 && !double.IsNaN(r.V)) d[r.M] = r.V * dScale;
                    samples[b.szRole] = d;
                }
            }

            // ② KWH 累積點：邊界相減
            var kwh = profile.GetBinding(EquipmentRoles.KWH);
            if (kwh != null && !string.IsNullOrEmpty(kwh.szSID))
            {
                var (dStart, dEnd) = await GetBoundaryValuesAsync(conn, kwh.szSID, dtHourStart, dtHourEnd, nMaxStalenessHours);
                var delta = CalcKwhDelta(dStart, dEnd, profile.dMaxKwh);
                if (delta != null)
                {
                    if (delta.Value.isRolledBackWithoutMax)
                        _logger.LogWarning(
                            "設備效率聚合 {Name}(#{Id}) KWH 點 {SID} 累積值倒退（{Start} → {End}）但未設定 MaxKwh，該小時 kWh 視為 0",
                            profile.szName, profile.nId, kwh.szSID, dStart, dEnd);
                    dKwhDelta = delta.Value.dDelta * (kwh.dScale == 0 ? 1 : kwh.dScale);
                }
            }
        }

        return ComputeFromSamples(profile, dtHourStart, samples, dKwhDelta, nMinSamples);
    }

    /// <summary>
    /// KWH 累積點兩端邊界 → 增量（原始讀數單位，未乘 Scale）。任一端缺 → null。
    /// 倒退：有 MaxKwh → (Max − Vs) + Ve；無 → 0 並標記（呼叫端警告）。與 EnergyLeafAggregator 語意一致。
    /// </summary>
    public static (double dDelta, bool isRolledOver, bool isRolledBackWithoutMax)? CalcKwhDelta(
        double? dStart, double? dEnd, double? dMaxKwh)
    {
        if (dStart == null || dEnd == null) return null;
        if (dEnd.Value >= dStart.Value) return (dEnd.Value - dStart.Value, false, false);
        if (dMaxKwh is > 0) return ((dMaxKwh.Value - dStart.Value) + dEnd.Value, true, false);
        return (0, false, true);
    }

    /// <summary>
    /// 純邏輯：分鐘樣本（已換算基準單位）+ KWH 增量（已換算 kWh，null = 無 / 缺）→ 一列 hourly。
    /// kWh 無法求得 → null（sparse，不寫列）。
    /// </summary>
    public static EquipmentHourlyModel? ComputeFromSamples(
        EquipmentProfileModel profile, DateTime dtHourStart, RoleSamples samples, double? dKwhDelta, int nMinSamples)
    {
        var isCompressor = profile.szKind == EquipmentKind.Compressor;
        var kw = samples.Get(EquipmentRoles.KW);
        var run = samples.Get(EquipmentRoles.RUN);

        // ── kWh：KWH 邊界相減優先，否則 AVG(kW) × 1h ──
        double? dKwh = dKwhDelta;
        if (dKwh == null && kw.Count >= nMinSamples && kw.Count > 0)
            dKwh = kw.Values.Average();
        if (dKwh == null) return null;

        // ── 產出：逐分鐘值 ──
        Dictionary<int, double> outMin;
        if (isCompressor)
        {
            outMin = samples.Get(EquipmentRoles.AIRFLOW);
        }
        else
        {
            var hasFlowPath = profile.GetBinding(EquipmentRoles.FLOW) != null
                && profile.GetBinding(EquipmentRoles.CHWS) != null
                && profile.GetBinding(EquipmentRoles.CHWR) != null;
            if (hasFlowPath)
            {
                var flow = samples.Get(EquipmentRoles.FLOW);
                var chws = samples.Get(EquipmentRoles.CHWS);
                var chwr = samples.Get(EquipmentRoles.CHWR);
                var dCp = profile.dSpecificHeat ?? Calc.DefaultSpecificHeat;
                outMin = [];
                // 分鐘對齊：三者在同一分鐘都有值才算（缺任一的分鐘不計）
                foreach (var (m, dFlow) in flow)
                {
                    if (!chws.TryGetValue(m, out var dS) || !chwr.TryGetValue(m, out var dR)) continue;
                    var rt = Calc.CoolingRtFromFlow(dFlow, dS, dR, dCp);
                    if (rt != null) outMin[m] = rt.Value;
                }
            }
            else
            {
                outMin = samples.Get(EquipmentRoles.RT);
            }
        }
        var nOutMinutes = outMin.Count;
        var isOutputValid = nOutMinutes >= nMinSamples && nOutMinutes > 0;
        double? dOutput = null;
        if (isOutputValid)
        {
            var dAvg = outMin.Values.Average();
            dOutput = isCompressor ? dAvg * 60.0 : dAvg;   // m³ = m³/min × 60；RT·h = RT × 1h
        }

        // ── 運轉 ──
        var dThreshold = Calc.RunningThresholdKw(profile.dRatedPower);
        double dRunFraction;
        if (run.Count > 0) dRunFraction = run.Values.Count(v => v >= 0.5) / (double)run.Count;
        else if (kw.Count > 0) dRunFraction = kw.Values.Count(v => v > dThreshold) / (double)kw.Count;
        else dRunFraction = dKwh.Value > dThreshold ? 1 : 0;   // 只有 KWH：該小時平均功率 = kWh / 1h

        bool? RunningAt(int m)
        {
            if (run.TryGetValue(m, out var r)) return r >= 0.5;
            if (kw.TryGetValue(m, out var k)) return k > dThreshold;
            return null;
        }

        // 整小時停機且產出點缺值 → 無可量測（OutputQty = 0、Quality = 1），避免停機時段誤觸覆蓋率警示
        var nQuality = 1;
        if (!isOutputValid)
        {
            if (dRunFraction <= 0) dOutput = 0;
            else nQuality = 0;
        }

        // ── 空壓機：加載時數、運轉期間平均壓力 ──
        double? dLoadedHours = null, dAvgPressure = null;
        if (isCompressor)
        {
            var loaded = samples.Get(EquipmentRoles.LOADED);
            if (loaded.Count > 0) dLoadedHours = loaded.Values.Count(v => v >= 0.5) / (double)loaded.Count;
            var press = samples.Get(EquipmentRoles.DISCHPRESS);
            if (press.Count > 0 && dRunFraction > 0)
            {
                var running = press.Where(p => RunningAt(p.Key) == true).Select(p => p.Value).ToList();
                // 每分鐘運轉狀態未知（只有 KWH）→ 退回全小時平均
                if (running.Count == 0 && press.Keys.All(m => RunningAt(m) == null)) running = press.Values.ToList();
                if (running.Count > 0) dAvgPressure = running.Average();
            }
        }

        // ── 運轉期間平均負載率 ──
        double? dAvgLoad = null;
        if (dRunFraction > 0)
        {
            var load = samples.Get(EquipmentRoles.LOAD);
            var loadRunning = load.Where(p => RunningAt(p.Key) != false).Select(p => p.Value).ToList();
            if (loadRunning.Count > 0)
                dAvgLoad = loadRunning.Average();
            else if (!isCompressor && dOutput != null && isOutputValid && profile.dRatedOutput is > 0)
                dAvgLoad = dOutput.Value / dRunFraction / profile.dRatedOutput.Value * 100.0;
            else if (isCompressor && dLoadedHours != null)
                dAvgLoad = Math.Min(100.0, dLoadedHours.Value / dRunFraction * 100.0);
            else if (profile.dRatedPower is > 0)
                dAvgLoad = dKwh.Value / dRunFraction / profile.dRatedPower.Value * 100.0;
        }

        return new EquipmentHourlyModel
        {
            nProfileId = profile.nId,
            dtHourStart = dtHourStart,
            dKwh = dKwh.Value,
            dOutputQty = dOutput,
            dRunHours = dRunFraction,
            dAvgLoadPct = dAvgLoad,
            dLoadedHours = dLoadedHours,
            dAvgPressure = dAvgPressure,
            nSampleCount = nOutMinutes,
            nQuality = nQuality,
            isBackfilled = false,
        };
    }

    /// <summary>取 SID 在 t0 / t1 兩時點各自最近一筆值（staleness window 內），同 EnergyLeafAggregator</summary>
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
