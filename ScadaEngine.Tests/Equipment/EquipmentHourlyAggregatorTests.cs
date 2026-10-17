using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Engine.Services;
using R = ScadaEngine.Common.Equipment.EquipmentRoles;

namespace ScadaEngine.Tests.Equipment;

/// <summary>
/// 鎖住 EquipmentHourlyAggregator 純邏輯：分鐘對齊算 RT（三者齊才算）、kWh 兩路徑優先序、
/// RunHours 判定、樣本不足 sparse、停機整小時不誤觸 Quality=0、負載率 / 加載 / 壓力。
/// 對錯 = 冰機 / 空壓機期間效率（ΣkWh ÷ Σ產出）的分子或分母整小時算錯。
/// </summary>
public class EquipmentHourlyAggregatorTests
{
    private static readonly DateTime Hour = new(2026, 10, 15, 10, 0, 0);
    private const int MinSamples = 30;

    private static EquipmentProfileModel Chiller(params string[] roles) => new()
    {
        nId = 1,
        szKind = EquipmentKind.Chiller,
        szName = "CH-1",
        dRatedOutput = 500,
        dRatedPower = 300,
        aBindings = roles.Select(r => new EquipmentPointBindingModel { nProfileId = 1, szRole = r, szSID = "S-" + r }).ToList(),
    };

    private static EquipmentProfileModel Compressor(params string[] roles) => new()
    {
        nId = 2,
        szKind = EquipmentKind.Compressor,
        szName = "AC-1",
        dRatedOutput = 12,
        dRatedPower = 90,
        aBindings = roles.Select(r => new EquipmentPointBindingModel { nProfileId = 2, szRole = r, szSID = "S-" + r }).ToList(),
    };

    /// <summary>分鐘 0..n-1 皆為同一值</summary>
    private static Dictionary<int, double> Const(double v, int n = 60, int nStart = 0) =>
        Enumerable.Range(nStart, n).ToDictionary(m => m, _ => v);

    private static EquipmentHourlyAggregator.RoleSamples Samples(params (string role, Dictionary<int, double> d)[] items)
    {
        var s = new EquipmentHourlyAggregator.RoleSamples();
        foreach (var (role, d) in items) s[role] = d;
        return s;
    }

    // ── 冰機：流量 + ΔT 分鐘對齊 ─────────────────────────────

    [Fact]
    public void 冰機_流量加溫差_整小時穩態()
    {
        var p = Chiller(R.KW, R.FLOW, R.CHWS, R.CHWR);
        var s = Samples((R.KW, Const(60)), (R.FLOW, Const(1000)), (R.CHWS, Const(7)), (R.CHWR, Const(12)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(60, m.dKwh, 6);
        Assert.Equal(99.18, m.dOutputQty!.Value, 2);   // RT·h
        Assert.Equal(1.0, m.dRunHours, 6);
        Assert.Equal(1, m.nQuality);
        Assert.Equal(60, m.nSampleCount);
    }

    [Fact]
    public void 冰機_分鐘對齊_只算三者齊的分鐘()
    {
        var p = Chiller(R.KW, R.FLOW, R.CHWS, R.CHWR);
        // 流量 60 分鐘、出水 0..39、回水 10..59 → 齊的只有 10..39 共 30 分鐘
        var s = Samples((R.KW, Const(60)), (R.FLOW, Const(1000)),
            (R.CHWS, Const(7, 40, 0)), (R.CHWR, Const(12, 50, 10)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(30, m.nSampleCount);
        Assert.Equal(1, m.nQuality);
        Assert.Equal(99.18, m.dOutputQty!.Value, 2);
    }

    [Fact]
    public void 冰機_變流量_逐分鐘相乘不等於平均相乘()
    {
        var p = Chiller(R.KW, R.FLOW, R.CHWS, R.CHWR);
        // 前 30 分：1,000 LPM × ΔT 2；後 30 分：200 LPM × ΔT 8 → 逐分鐘 = (19.84×2 + 3.97×8)/2 = 35.71
        // 若用 AVG(flow)=600 × AVG(ΔT)=5 → 59.51（錯）
        var flow = Const(1000, 30, 0).Concat(Const(200, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var chwr = Const(9, 30, 0).Concat(Const(15, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, Const(30)), (R.FLOW, flow), (R.CHWS, Const(7)), (R.CHWR, chwr));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        var dExpected = (EquipmentMetricCalculator.CoolingRtFromFlow(1000, 7, 9)!.Value
                       + EquipmentMetricCalculator.CoolingRtFromFlow(200, 7, 15)!.Value) / 2;
        Assert.Equal(dExpected, m.dOutputQty!.Value, 6);
        Assert.Equal(35.71, m.dOutputQty!.Value, 2);
    }

    [Fact]
    public void 冰機_有流量路徑時_RT點不採用()
    {
        var p = Chiller(R.KW, R.FLOW, R.CHWS, R.CHWR, R.RT);
        var s = Samples((R.KW, Const(60)), (R.FLOW, Const(1000)), (R.CHWS, Const(7)), (R.CHWR, Const(12)),
            (R.RT, Const(500)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(99.18, m.dOutputQty!.Value, 2);
    }

    [Fact]
    public void 冰機_無流量路徑_用RT點()
    {
        var p = Chiller(R.KW, R.RT);
        var s = Samples((R.KW, Const(300)), (R.RT, Const(450)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(450, m.dOutputQty!.Value, 6);
        Assert.Equal(90, m.dAvgLoadPct!.Value, 6);   // 450 / 額定 500
    }

    [Fact]
    public void 冰機_RT點負值_分鐘層當0再平均()
    {
        // 前 30 分鐘 400 RT、後 30 分鐘 −50 RT（停機溫差雜訊）
        // 分鐘層 clamp → AVG = (400×30 + 0×30) / 60 = 200；若在平均層 clamp 會得 175，若不 clamp 會得 175 且被低報
        var p = Chiller(R.KW, R.RT);
        var rt = Const(400, 30).Concat(Const(-50, 30, 30)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var s = Samples((R.KW, Const(300)), (R.RT, rt));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(200, m.dOutputQty!.Value, 6);
        Assert.Equal(60, m.nSampleCount);   // 負值分鐘仍算有效樣本（有量測到沒冷量，不是缺值）
        Assert.Equal(1, m.nQuality);
    }

    [Fact]
    public void 冰機_RT點整小時負值_產出0非負()
    {
        var p = Chiller(R.KW, R.RT);
        var s = Samples((R.KW, Const(300)), (R.RT, Const(-20)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(0, m.dOutputQty!.Value, 6);
        Assert.Equal(1, m.nQuality);
    }

    // ── kWh 路徑 ───────────────────────────────────────────

    [Fact]
    public void kWh_KWH累積點優先於kW平均()
    {
        var p = Chiller(R.KW, R.KWH, R.RT);
        var s = Samples((R.KW, Const(300)), (R.RT, Const(450)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, 287.5, MinSamples)!;
        Assert.Equal(287.5, m.dKwh, 6);
    }

    [Fact]
    public void kWh_KWH缺邊界_退回kW平均()
    {
        var p = Chiller(R.KW, R.KWH, R.RT);
        var s = Samples((R.KW, Const(300)), (R.RT, Const(450)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(300, m.dKwh, 6);
    }

    [Fact]
    public void kWh_樣本不足_不寫列()
    {
        var p = Chiller(R.KW, R.RT);
        var s = Samples((R.KW, Const(300, 29)), (R.RT, Const(450)));
        Assert.Null(EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples));
    }

    [Theory]
    [InlineData(100.0, 130.5, null, 30.5, false)]
    [InlineData(99990.0, 15.0, 100000.0, 25.0, true)]
    public void KWH邊界相減_含溢位(double start, double end, double? max, double expected, bool rolled)
    {
        var r = EquipmentHourlyAggregator.CalcKwhDelta(start, end, max)!.Value;
        Assert.Equal(expected, r.dDelta, 6);
        Assert.Equal(rolled, r.isRolledOver);
    }

    [Fact]
    public void KWH倒退且無MaxKwh_視為0並標記()
    {
        var r = EquipmentHourlyAggregator.CalcKwhDelta(500, 10, null)!.Value;
        Assert.Equal(0, r.dDelta);
        Assert.True(r.isRolledBackWithoutMax);
    }

    [Fact]
    public void KWH任一端缺值_回null()
    {
        Assert.Null(EquipmentHourlyAggregator.CalcKwhDelta(null, 10, null));
        Assert.Null(EquipmentHourlyAggregator.CalcKwhDelta(10, null, null));
    }

    // ── 產出不足 / 停機 ─────────────────────────────────────

    [Fact]
    public void 運轉中產出樣本不足_Quality0且產出null()
    {
        var p = Chiller(R.KW, R.FLOW, R.CHWS, R.CHWR);
        var s = Samples((R.KW, Const(200)), (R.FLOW, Const(1000, 10)), (R.CHWS, Const(7)), (R.CHWR, Const(12)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(0, m.nQuality);
        Assert.Null(m.dOutputQty);
        Assert.Equal(200, m.dKwh, 6);
    }

    [Fact]
    public void 整小時停機且產出點缺值_產出0且Quality1()
    {
        var p = Chiller(R.KW, R.FLOW, R.CHWS, R.CHWR);
        var s = Samples((R.KW, Const(0.5)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(1, m.nQuality);
        Assert.Equal(0, m.dOutputQty);
        Assert.Equal(0, m.dRunHours);
        Assert.Null(m.dAvgLoadPct);
    }

    // ── RunHours ───────────────────────────────────────────

    [Fact]
    public void RunHours_有RUN點用RUN占比()
    {
        var p = Chiller(R.KW, R.RT, R.RUN);
        var runs = Const(1, 45, 0).Concat(Const(0, 15, 45)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, Const(200)), (R.RT, Const(300)), (R.RUN, runs));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(0.75, m.dRunHours, 6);
    }

    [Fact]
    public void RunHours_無RUN點用kW超過額定5百分比()
    {
        var p = Chiller(R.KW, R.RT);   // 額定 300 kW → 門檻 15 kW
        var kw = Const(200, 20, 0).Concat(Const(15, 40, 20)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, kw), (R.RT, Const(100)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(20.0 / 60.0, m.dRunHours, 6);
    }

    [Fact]
    public void RunHours_只有KWH_本小時kWh超過門檻即1()
    {
        var p = Chiller(R.KWH, R.RT);
        var s = Samples((R.RT, Const(100)));
        Assert.Equal(1, EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, 120, MinSamples)!.dRunHours);
        Assert.Equal(0, EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, 10, MinSamples)!.dRunHours);
    }

    // ── 負載率 ─────────────────────────────────────────────

    [Fact]
    public void 負載率_LOAD點只取運轉中分鐘()
    {
        var p = Chiller(R.KW, R.RT, R.RUN, R.LOAD);
        var runs = Const(1, 30, 0).Concat(Const(0, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var load = Const(80, 30, 0).Concat(Const(0, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, Const(150)), (R.RT, Const(200)), (R.RUN, runs), (R.LOAD, load));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(80, m.dAvgLoadPct!.Value, 6);
    }

    [Fact]
    public void 負載率_無LOAD點_冰機為運轉期間平均RT除以額定()
    {
        var p = Chiller(R.KW, R.RT, R.RUN);
        var runs = Const(1, 30, 0).Concat(Const(0, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var rt = Const(250, 30, 0).Concat(Const(0, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, Const(150)), (R.RT, rt), (R.RUN, runs));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(125, m.dOutputQty!.Value, 6);           // RT·h 含停機分鐘
        Assert.Equal(50, m.dAvgLoadPct!.Value, 6);           // 運轉期間 250 RT / 500
    }

    // ── 空壓機 ─────────────────────────────────────────────

    [Fact]
    public void 空壓機_空氣量換算m3_加載與壓力()
    {
        var p = Compressor(R.KW, R.AIRFLOW, R.LOADED, R.DISCHPRESS);
        var loaded = Const(1, 48, 0).Concat(Const(0, 12, 48)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, Const(70)), (R.AIRFLOW, Const(10)), (R.LOADED, loaded), (R.DISCHPRESS, Const(7)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(600, m.dOutputQty!.Value, 6);           // 10 m³/min × 60
        Assert.Equal(70, m.dKwh, 6);
        Assert.Equal(0.8, m.dLoadedHours!.Value, 6);
        Assert.Equal(7, m.dAvgPressure!.Value, 6);
        Assert.Equal(80, m.dAvgLoadPct!.Value, 6);           // 加載 0.8 / 運轉 1.0
    }

    [Fact]
    public void 空壓機_壓力只取運轉中分鐘()
    {
        var p = Compressor(R.KW, R.AIRFLOW, R.DISCHPRESS);   // 額定 90 kW → 門檻 4.5 kW
        var kw = Const(70, 30, 0).Concat(Const(1, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var press = Const(7, 30, 0).Concat(Const(3, 30, 30)).ToDictionary(x => x.Key, x => x.Value);
        var s = Samples((R.KW, kw), (R.AIRFLOW, Const(5)), (R.DISCHPRESS, press));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        Assert.Equal(7, m.dAvgPressure!.Value, 6);
        Assert.Equal(0.5, m.dRunHours, 6);
    }

    [Fact]
    public void 空壓機_期間比功率與即時公式一致()
    {
        // 穩態一小時：kW 70、10 m³/min → hourly 70 kWh / 600 m³ → 期間比功率 7，與即時 70/10 相同
        var p = Compressor(R.KW, R.AIRFLOW);
        var s = Samples((R.KW, Const(70)), (R.AIRFLOW, Const(10)));
        var m = EquipmentHourlyAggregator.ComputeFromSamples(p, Hour, s, null, MinSamples)!;
        var period = EquipmentMetricCalculator.ComputePeriod(EquipmentKind.Compressor, m.dKwh, m.dOutputQty!.Value, m.dRunHours);
        var instant = EquipmentMetricCalculator.ComputeCompressorInstant(new CompressorInstantInput { dKw = 70, dFlow = 10, dRatedKw = 90 });
        Assert.Equal(instant.dSpecificPower!.Value, period.dPrimary!.Value, 9);
    }

    // ── Backfill payload ──────────────────────────────────

    [Fact]
    public void Backfill_payload解析_to含整天()
    {
        var r = EquipmentHourlyBackfillSubscriber.ParsePayload("{\"profileId\":3,\"from\":\"2026-10-01\",\"to\":\"2026-10-14\"}")!.Value;
        Assert.Equal(3, r.nProfileId);
        Assert.Equal(new DateTime(2026, 10, 1), r.dtFrom);
        Assert.Equal(new DateTime(2026, 10, 15), r.dtTo);
    }

    [Fact]
    public void Backfill_payload_profileId為null代表全部()
    {
        var r = EquipmentHourlyBackfillSubscriber.ParsePayload("{\"profileId\":null,\"from\":\"2026-10-01\",\"to\":\"2026-10-01\"}")!.Value;
        Assert.Null(r.nProfileId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"from\":\"2026-10-05\",\"to\":\"2026-10-01\"}")]
    [InlineData("not json")]
    public void Backfill_payload不合法_回null(string payload)
    {
        Assert.Null(EquipmentHourlyBackfillSubscriber.ParsePayload(payload));
    }
}
