using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.Equipment;

/// <summary>
/// 鎖住設備效率期間儀表板（EquipmentEfficiencyReportService.BuildPeriod）與即時組裝（EquipmentRealtimeService.BuildItem）：
/// 能量加權平均（ΣkWh ÷ Σ產出，不是逐時效率平均）、Quality=0 不進分子分母、負載率四段 bin、
/// 覆蓋率（未來時數不計入分母、報最差那台）、排名排序、即時值 × Scale 換算與冷量來源優先序。
/// 對錯 = 診斷頁與 EMS 卡片顯示的期間效率、排名、覆蓋率警示全部失真。
/// </summary>
public class EquipmentPeriodMetricTests
{
    private static readonly DateTime Day = new(2026, 10, 1);
    private static readonly DateTime FarFuture = new(2030, 1, 1);

    private static EquipmentProfileModel Chiller(int id, string name, double? good = 0.65, double? fair = 0.85) => new()
    {
        nId = id, szKind = EquipmentKind.Chiller, szName = name, dGoodThreshold = good, dFairThreshold = fair,
    };

    private static EquipmentHourlyModel Row(int id, int hour, double kwh, double? output, double run = 1,
        double? load = null, int quality = 1) => new()
    {
        nProfileId = id, dtHourStart = Day.AddHours(hour), dKwh = kwh, dOutputQty = output, dRunHours = run,
        dAvgLoadPct = load, nQuality = quality,
    };

    /// <summary>單日 24 個逐時 bucket</summary>
    private static List<DateTime> HourBoundaries() => Enumerable.Range(0, 25).Select(h => Day.AddHours(h)).ToList();
    private static List<string> HourLabels() => Enumerable.Range(0, 24).Select(h => $"{h:D2}:00").ToList();

    private static ScadaEngine.Web.Features.Ems.Models.EquipmentPeriodDto Build(
        IReadOnlyList<EquipmentProfileModel> profiles, IReadOnlyList<EquipmentHourlyModel> rows, DateTime? now = null) =>
        EquipmentEfficiencyReportService.BuildPeriod(EquipmentKind.Chiller, profiles, rows,
            HourBoundaries(), HourLabels(), now ?? FarFuture, 90);

    [Fact]
    public void 期間效率為能量加權_不是逐時效率平均()
    {
        // 小時 0：100 kWh / 200 RT·h = 0.5；小時 1：10 kWh / 10 RT·h = 1.0
        // 逐時平均 = 0.75（錯）；能量加權 = 110 / 210 = 0.5238（對）
        var d = Build([Chiller(1, "CH-1")], [Row(1, 0, 100, 200), Row(1, 1, 10, 10)]);
        Assert.Equal(110.0 / 210.0, d.totals.primary!.Value, 9);
        Assert.Equal(110, d.totals.kwh, 6);
        Assert.Equal(210, d.totals.output, 6);
    }

    [Fact]
    public void Quality0小時_不進效率分子分母_但計入運轉時數()
    {
        var d = Build([Chiller(1, "CH-1")], [Row(1, 0, 60, 100), Row(1, 1, 80, null, run: 1, quality: 0)]);
        Assert.Equal(60, d.totals.kwh, 6);
        Assert.Equal(0.6, d.totals.primary!.Value, 9);
        Assert.Equal(2, d.totals.runHours, 6);
    }

    [Fact]
    public void 全場加權線與各台趨勢線_逐bucket計算()
    {
        var d = Build([Chiller(1, "CH-1"), Chiller(2, "CH-2")],
            [Row(1, 0, 60, 100), Row(2, 0, 90, 100), Row(1, 5, 70, 100)]);
        Assert.Equal(24, d.fleet.Count);
        Assert.Equal(150.0 / 200.0, d.fleet[0]!.Value, 9);
        Assert.Null(d.fleet[1]);
        Assert.Equal(0.7, d.fleet[5]!.Value, 9);
        var s1 = d.series.Single(s => s.id == 1);
        Assert.Equal(0.6, s1.values[0]!.Value, 9);
        Assert.Null(s1.values[1]);
    }

    [Fact]
    public void 負載率分bin_kWh與產出占比()
    {
        var d = Build([Chiller(1, "CH-1")],
        [
            Row(1, 0, 30, 50, load: 20),    // bin 0
            Row(1, 1, 30, 50, load: 25),    // bin 0（含上界）
            Row(1, 2, 40, 100, load: 60),   // bin 2
            Row(1, 3, 100, 200, load: 90),  // bin 3
            Row(1, 4, 50, 80),              // 無負載率 → 不分 bin
        ]);
        Assert.Equal(4, d.loadBins.Count);
        Assert.Equal(60, d.loadBins[0].kwh, 6);
        Assert.Equal(0, d.loadBins[1].kwh, 6);
        Assert.Equal(40, d.loadBins[2].kwh, 6);
        Assert.Equal(100, d.loadBins[3].kwh, 6);
        Assert.Equal(30.0, d.loadBins[0].kwhPct, 1);   // 60 / 200
        Assert.Equal(25.0, d.loadBins[0].outputPct, 1); // 100 / 400
        Assert.Equal(0.6, d.loadBins[0].primary!.Value, 9);
        Assert.Null(d.loadBins[1].primary);
    }

    [Fact]
    public void 覆蓋率_報最差那台_未來時數不計入分母()
    {
        var dtNow = Day.AddHours(10);   // 今天才過 10 小時 → 分母 10
        var rows = Enumerable.Range(0, 10).Select(h => Row(1, h, 60, 100)).ToList();
        rows.AddRange(Enumerable.Range(0, 5).Select(h => Row(2, h, 60, 100)));
        var d = Build([Chiller(1, "CH-1"), Chiller(2, "CH-2")], rows, dtNow);
        Assert.Equal(10, d.coverage.expectedHours);
        Assert.Equal(50.0, d.coverage.coveragePct, 1);
        Assert.Equal("CH-2", d.coverage.worstName);
        Assert.Equal(5, d.coverage.missingHours);
        Assert.True(d.coverage.isBelowThreshold);
        Assert.Equal(100.0, d.ranking.Single(r => r.id == 1).coveragePct, 1);
    }

    [Fact]
    public void 覆蓋率_Quality0小時算缺漏()
    {
        var dtNow = Day.AddHours(2);
        var d = Build([Chiller(1, "CH-1")], [Row(1, 0, 60, 100), Row(1, 1, 60, null, quality: 0)], dtNow);
        Assert.Equal(50.0, d.coverage.coveragePct, 1);
    }

    [Fact]
    public void 覆蓋率_全部到齊不警示()
    {
        var dtNow = Day.AddHours(3);
        var d = Build([Chiller(1, "CH-1")], Enumerable.Range(0, 3).Select(h => Row(1, h, 60, 100)).ToList(), dtNow);
        Assert.False(d.coverage.isBelowThreshold);
        Assert.Equal(100.0, d.coverage.coveragePct, 1);
    }

    [Fact]
    public void 排名_效率由低到高_無值排最後_評級依各台門檻()
    {
        var d = Build(
            [Chiller(1, "A", 0.5, 0.6), Chiller(2, "B"), Chiller(3, "C")],
            [Row(1, 0, 70, 100), Row(2, 0, 60, 100), Row(3, 0, 0, 0, run: 0)]);
        Assert.Equal(["B", "A", "C"], d.ranking.Select(r => r.name).ToArray());
        Assert.Equal("Good", d.ranking[0].grade);   // 0.6 ≤ 0.65
        Assert.Equal("Poor", d.ranking[1].grade);   // 0.7 > A 的 Fair 0.6
        Assert.Equal("Off", d.ranking[2].grade);    // 整期未運轉
    }

    [Fact]
    public void 排名_整期無資料為NoData()
    {
        var d = Build([Chiller(1, "A")], []);
        Assert.Equal("NoData", d.ranking[0].grade);
        Assert.Null(d.totals.primary);
    }

    [Fact]
    public void 範圍外的列不計入()
    {
        var rows = new List<EquipmentHourlyModel>
        {
            Row(1, 0, 60, 100),
            new() { nProfileId = 1, dtHourStart = Day.AddDays(1), dKwh = 999, dOutputQty = 1, dRunHours = 1, nQuality = 1 },
        };
        var d = Build([Chiller(1, "A")], rows);
        Assert.Equal(60, d.totals.kwh, 6);
    }

    // ── 即時組裝 ────────────────────────────────────────────

    private static readonly DateTime Ts = new(2026, 10, 15, 10, 30, 0);

    [Fact]
    public void 即時_流量路徑優先_值乘Scale換算()
    {
        var p = new EquipmentProfileModel
        {
            nId = 1, szKind = EquipmentKind.Chiller, szName = "CH-1", dRatedPower = 600,
            aBindings =
            [
                new() { szRole = EquipmentRoles.KW, szSID = "kw", dScale = 1 },
                new() { szRole = EquipmentRoles.FLOW, szSID = "flow", dScale = 1000.0 / 60.0 },   // m³/h → L/min
                new() { szRole = EquipmentRoles.CHWS, szSID = "s", dScale = 1 },
                new() { szRole = EquipmentRoles.CHWR, szSID = "r", dScale = 1 },
                new() { szRole = EquipmentRoles.RT, szSID = "rt", dScale = 1 },
            ],
        };
        var cache = new Dictionary<string, (double, DateTime)>(StringComparer.OrdinalIgnoreCase)
        {
            ["kw"] = (59.5, Ts), ["flow"] = (60, Ts), ["s"] = (7, Ts), ["r"] = (12, Ts), ["rt"] = (95, Ts),
        };
        var it = EquipmentRealtimeService.BuildItem(p, cache);
        Assert.Equal("flow", it.outputSource);
        Assert.Equal(99.18, it.output!.Value, 2);    // 60 m³/h = 1,000 L/min、ΔT 5 → 99.18 RT
        Assert.Equal(95, it.rtPoint);                 // RT 點僅對照
        Assert.Equal(0.6, it.primary!.Value, 2);
        Assert.Equal("Good", it.grade);
        Assert.True(it.isRunning);
        Assert.Equal(Ts, it.lastUpdate);
    }

    [Fact]
    public void 即時_流量路徑缺值_退回RT點()
    {
        var p = new EquipmentProfileModel
        {
            nId = 1, szKind = EquipmentKind.Chiller, szName = "CH-1",
            aBindings =
            [
                new() { szRole = EquipmentRoles.KW, szSID = "kw", dScale = 1 },
                new() { szRole = EquipmentRoles.FLOW, szSID = "flow", dScale = 1 },
                new() { szRole = EquipmentRoles.CHWS, szSID = "s", dScale = 1 },
                new() { szRole = EquipmentRoles.CHWR, szSID = "r", dScale = 1 },
                new() { szRole = EquipmentRoles.RT, szSID = "rt", dScale = 1 },
            ],
        };
        var cache = new Dictionary<string, (double, DateTime)> { ["kw"] = (300, Ts), ["rt"] = (500, Ts) };
        var it = EquipmentRealtimeService.BuildItem(p, cache);
        Assert.Equal("rt", it.outputSource);
        Assert.Equal(0.6, it.primary!.Value, 6);
    }

    [Fact]
    public void 即時_完全沒有即時值_評級NoData且運轉未知()
    {
        var p = new EquipmentProfileModel
        {
            nId = 1, szKind = EquipmentKind.Compressor, szName = "AC-1",
            aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "kw", dScale = 1 }],
        };
        var it = EquipmentRealtimeService.BuildItem(p, new Dictionary<string, (double, DateTime)>());
        Assert.False(it.hasData);
        Assert.Equal("NoData", it.grade);
        Assert.Null(it.isRunning);
    }

    [Fact]
    public void 即時_未設門檻用預設值()
    {
        var p = new EquipmentProfileModel { nId = 1, szKind = EquipmentKind.Compressor, szName = "AC-1" };
        var it = EquipmentRealtimeService.BuildItem(p, new Dictionary<string, (double, DateTime)>());
        Assert.Equal(7.0, it.good);
        Assert.Equal(9.0, it.fair);
    }

    // ── 存檔驗證 ────────────────────────────────────────────

    [Fact]
    public void 存檔驗證_Scale由單位重算()
    {
        var m = new EquipmentProfileModel
        {
            szKind = EquipmentKind.Chiller, szName = " CH-1 ",
            aBindings =
            [
                new() { szRole = EquipmentRoles.KW, szSID = "kw", szUnit = "kW", dScale = 999 },
                new() { szRole = EquipmentRoles.FLOW, szSID = "f", szUnit = "LPM" },
                new() { szRole = EquipmentRoles.CHWS, szSID = "s" },
                new() { szRole = EquipmentRoles.CHWR, szSID = "r" },
                new() { szRole = EquipmentRoles.RT, szSID = "" },   // 空白 = 未綁，丟掉
            ],
        };
        EquipmentProfileService.ValidateAndNormalize(m);
        Assert.Equal("CH-1", m.szName);
        Assert.Equal(4, m.aBindings.Count);
        Assert.Equal(1, m.GetBinding(EquipmentRoles.KW)!.dScale);
        Assert.Equal("L/min", m.GetBinding(EquipmentRoles.FLOW)!.szUnit);
        Assert.Equal("°C", m.GetBinding(EquipmentRoles.CHWS)!.szUnit);
    }

    [Theory]
    [InlineData("pump", "invalid_kind")]
    [InlineData("chiller", "missing_required")]
    public void 存檔驗證_種類錯誤或缺必要角色(string kind, string code)
    {
        var m = new EquipmentProfileModel
        {
            szKind = kind, szName = "X",
            aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "kw" }],
        };
        var ex = Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(m));
        Assert.Equal(code, ex.szCode);
    }

    [Fact]
    public void 存檔驗證_角色不屬於該種類_與單位無法換算()
    {
        var wrongRole = new EquipmentProfileModel
        {
            szKind = EquipmentKind.Compressor, szName = "AC",
            aBindings = [new() { szRole = EquipmentRoles.CHWS, szSID = "s" }],
        };
        Assert.Equal("invalid_role",
            Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(wrongRole)).szCode);

        var badUnit = new EquipmentProfileModel
        {
            szKind = EquipmentKind.Compressor, szName = "AC",
            aBindings =
            [
                new() { szRole = EquipmentRoles.KW, szSID = "kw" },
                new() { szRole = EquipmentRoles.AIRFLOW, szSID = "f", szUnit = "gallon/day" },
            ],
        };
        Assert.Equal("invalid_unit",
            Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(badUnit)).szCode);
    }

    [Fact]
    public void 存檔驗證_優門檻不可大於可門檻()
    {
        var m = new EquipmentProfileModel
        {
            szKind = EquipmentKind.Compressor, szName = "AC", dGoodThreshold = 9, dFairThreshold = 7,
            aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "kw" }, new() { szRole = EquipmentRoles.AIRFLOW, szSID = "f" }],
        };
        Assert.Equal("threshold_order",
            Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(m)).szCode);
    }
}
