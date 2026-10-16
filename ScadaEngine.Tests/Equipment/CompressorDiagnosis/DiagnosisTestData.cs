using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;

namespace ScadaEngine.Tests.Equipment.CompressorDiagnosis;

/// <summary>合成分鐘序列產生器（診斷規則測試共用）</summary>
internal static class DiagnosisTestData
{
    /// <summary>2026-10-05 為星期一</summary>
    public static readonly DateTime From = new(2026, 10, 5);

    public static double[] Fill(int n, Func<int, double> f) => Enumerable.Range(0, n).Select(f).ToArray();

    /// <summary>決定性抖動 ±1%（避免合成序列被資料品質規則判成平線）</summary>
    public static double J(int m) => ((m * 7919L) % 11 - 5) / 500.0;

    /// <summary>
    /// 定頻機：isLoaded(m) → 100 kW / 20 m³/min；運轉卸載 → 15 kW / 0（低於 S1 推定門檻 20%）；停機 → 0（皆帶 ±1% 抖動）。
    /// isRunning 預設全程運轉。
    /// </summary>
    public static CompressorSeries Fixed(int id, int n, Func<int, bool> isLoaded, Func<int, bool>? isRunning = null,
        double ratedKw = 100, double? ratedFlow = 20, string? controlType = CompressorControlType.Fixed, bool withLoadedPoint = false,
        double press = 7.0)
    {
        isRunning ??= _ => true;
        return new CompressorSeries
        {
            nId = id,
            szName = "AC" + id,
            dRatedKw = ratedKw,
            dRatedFlow = ratedFlow,
            szControlType = controlType,
            aKw = Fill(n, m => (!isRunning(m) ? 0 : isLoaded(m) ? ratedKw : ratedKw * 0.15) * (1 + J(m))),
            aFlow = Fill(n, m => (isRunning(m) && isLoaded(m) ? (ratedFlow ?? 20) : 0) * (1 + J(m))),
            aPress = Fill(n, m => (isRunning(m) ? press : press * 0.95) * (1 + J(m))),
            aLoaded = withLoadedPoint ? Fill(n, m => isRunning(m) && isLoaded(m) ? 1 : 0) : null,
        };
    }

    /// <summary>每小時一列 EquipmentHourly（kWh = 平均 kW、m³ = 平均流量 × 60）</summary>
    public static List<EquipmentHourlyModel> Hourly(CompressorSeries c, DateTime dtFrom, int n)
    {
        var rows = new List<EquipmentHourlyModel>();
        for (var h = 0; h < n / 60; h++)
        {
            var kw = c.aKw!.Skip(h * 60).Take(60).Average();
            double? fl = c.aFlow == null ? null : c.aFlow.Skip(h * 60).Take(60).Average();
            rows.Add(new EquipmentHourlyModel
            {
                nProfileId = c.nId,
                dtHourStart = dtFrom.AddHours(h),
                dKwh = kw,
                dOutputQty = fl * 60,
                dRunHours = kw > 0 ? 1 : 0,
                dAvgPressure = 7.0,
                nQuality = 1,
            });
        }
        return rows;
    }

    public static CompressorDiagnosisInput Input(int nDays, params CompressorSeries[] comps)
    {
        var n = nDays * 1440;
        var input = new CompressorDiagnosisInput
        {
            dtFrom = From,
            dtTo = From.AddDays(nDays),
            aCompressors = comps.ToList(),
            parameters = DiagnosisParameters.FromOverrides(null, null),
        };
        foreach (var c in comps) input.aHourly.AddRange(Hourly(c, From, n));
        return input;
    }

    /// <summary>週一~週五 08:00–17:00 生產</summary>
    public static ProductionSchedule WeekdaySchedule() => new()
    {
        aRanges = Enumerable.Range(1, 5).Select(d => (d, 8 * 60, 17 * 60)).ToList(),
    };

    public static DiagnosisRuleResult Rule(CompressorDiagnosisResult r, string id) => r.aRules.Single(x => x.szId == id);
}
