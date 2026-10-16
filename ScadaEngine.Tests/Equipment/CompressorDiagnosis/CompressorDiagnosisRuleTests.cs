using System.Text.Json;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;
using static ScadaEngine.Tests.Equipment.CompressorDiagnosis.DiagnosisTestData;

namespace ScadaEngine.Tests.Equipment.CompressorDiagnosis;

/// <summary>
/// 鎖住空壓系統規則式診斷（Common CompressorDiagnosisEngine）每條規則的觸發 / 不觸發邊界、資料不足降級、
/// 卸載推定、循環計數、停機延遲試算扣重啟限制、年化與電價、P+L 重疊旗標、「依預設值」旗標、決定性。
/// 對錯 = /CompressorEfficiency「系統診斷」與能源日報空壓區段給出錯誤的篩檢結論或節能金額。
/// </summary>
public class CompressorDiagnosisRuleTests
{
    private static CompressorDiagnosisResult Run(CompressorDiagnosisInput input) =>
        CompressorDiagnosisEngine.Run(input, new DateTime(2026, 10, 16, 2, 0, 0));

    private static bool HasNote(DiagnosisRuleResult r, string key) => r.aNotes.Any(x => x.szKey == "eq.diag.note." + key);
    private static bool HasMiss(DiagnosisRuleResult r, string key) => r.aMissing.Any(x => x.szKey == "eq.diag.miss." + key);

    // ─────────────────────────── 決定性 / 參數 ───────────────────────────

    [Fact]
    public void 同輸入兩次結果完全相同()
    {
        var n = 3 * 1440;
        var make = () =>
        {
            var input = Input(3, Fixed(1, n, m => m % 10 < 6), Fixed(2, n, m => m % 7 < 2));
            input.dDemandPressBar = 6.0;
            input.schedule = WeekdaySchedule();
            input.dPrice = 3.2;
            input.szPriceSource = "manual";
            return input;
        };
        var a = JsonSerializer.Serialize(Run(make()));
        var b = JsonSerializer.Serialize(Run(make()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void 參數_覆寫夾回範圍_未知鍵忽略_未覆寫且未確認才算未確認預設()
    {
        var p = DiagnosisParameters.FromOverrides(
            new Dictionary<string, double> { [DiagnosisParameters.PressMarginRatio] = 5.0, ["x.unknown"] = 1 },
            [DiagnosisParameters.TrialStepBar]);
        Assert.Equal(2.0, p[DiagnosisParameters.PressMarginRatio]);                 // 上限 2.0
        Assert.False(p.IsUnconfirmedDefault(DiagnosisParameters.PressMarginRatio)); // 已覆寫
        Assert.False(p.IsUnconfirmedDefault(DiagnosisParameters.TrialStepBar));     // 已確認
        Assert.True(p.IsUnconfirmedDefault(DiagnosisParameters.PipeLossReserveBar));
        Assert.Equal(0.3, p[DiagnosisParameters.PipeLossReserveBar]);
    }

    [Fact]
    public void 依預設值旗標_全部確認後消失()
    {
        var n = 2 * 1440;
        var input = Input(2, Fixed(1, n, _ => true));
        input.dDemandPressBar = 6.0;
        Assert.Contains(DiagnosisParameters.PressMarginRatio, Rule(Run(input), "P").aUnconfirmedDefaults);

        input.parameters = DiagnosisParameters.FromOverrides(null, DiagnosisParameters.Definitions.Select(d => d.szKey));
        var r = Run(input);
        Assert.Empty(Rule(r, "P").aUnconfirmedDefaults);
        Assert.Empty(r.aUnconfirmedDefaults);
    }

    // ─────────────────────────── 前處理 / 資料品質 ───────────────────────────

    [Fact]
    public void 清洗_哨兵值移除並計數_覆蓋率_缺口()
    {
        var p = DiagnosisParameters.FromOverrides(null, null);
        var raw = Fill(1000, m => m < 100 ? double.NaN : m == 500 ? 9999 : 5 + J(m));
        var q = new SignalQuality();
        var arr = DiagnosisContext.Clean(raw, 1000, false, p, false, q)!;
        Assert.True(double.IsNaN(arr[500]));
        Assert.Equal(1, q.nSentinelCount);
        Assert.Equal(89.9, q.dCoveragePct);      // (1000 − 100 − 1) / 1000
        Assert.Equal(100, q.nLongestGapMinutes);
        Assert.Equal(1, q.nGapCount);            // > 60 分的缺口一段
        Assert.False(q.isTrusted);               // 覆蓋率 < 90%
    }

    [Fact]
    public void 清洗_長時間完全相同非零值判平線_零值不算()
    {
        var p = DiagnosisParameters.FromOverrides(null, null);
        var q = new SignalQuality();
        DiagnosisContext.Clean(Fill(1440, _ => 7.0), 1440, false, p, false, q);
        Assert.Equal(100, q.dFlatlinePct);
        Assert.False(q.isTrusted);

        var q0 = new SignalQuality();
        DiagnosisContext.Clean(Fill(1440, m => m < 1000 ? 0 : 5 + J(m)), 1440, false, p, false, q0);
        Assert.Equal(0, q0.dFlatlinePct);
        Assert.True(q0.isTrusted);
    }

    [Fact]
    public void 無LOADED點以kW推定加卸載_有LOADED但與功率矛盾則退回推定()
    {
        var n = 1440;
        var p = DiagnosisParameters.FromOverrides(null, null);
        var c = DiagnosisContext.BuildCompressor(Fixed(1, n, m => m % 10 < 5), n, p);
        Assert.True(c.isLoadInferred);
        Assert.Equal(MinuteState.Loaded, c.state[0]);
        Assert.Equal(MinuteState.Unloaded, c.state[7]);

        // LOADED 恆為 1，但一半時間功率只有卸載功率以下（停機）→ 矛盾 > 5% → 不可信
        var src = Fixed(2, n, _ => true, isRunning: m => m % 2 == 0);
        src.aLoaded = Fill(n, _ => 1);
        var c2 = DiagnosisContext.BuildCompressor(src, n, p);
        Assert.False(c2.qLoaded.isTrusted);
        Assert.True(c2.isLoadInferred);
    }

    [Fact]
    public void 控制型式未填時由功率分佈推定()
    {
        var n = 1440;
        var p = DiagnosisParameters.FromOverrides(null, null);
        var fix = DiagnosisContext.BuildCompressor(Fixed(1, n, m => m % 10 < 5, controlType: null), n, p);
        Assert.Equal(CompressorControlType.Fixed, fix.szControlType);
        Assert.True(fix.isControlTypeInferred);

        var vsdSrc = Fixed(2, n, _ => true, controlType: null);
        vsdSrc.aKw = Fill(n, m => 30 + (m % 70));   // 30 ~ 99 kW 連續
        var vsd = DiagnosisContext.BuildCompressor(vsdSrc, n, p);
        Assert.Equal(CompressorControlType.Vsd, vsd.szControlType);
    }

    [Fact]
    public void D規則_資料乾淨不觸發_有哨兵值觸發()
    {
        var n = 1440;
        var clean = Run(Input(1, Fixed(1, n, m => m % 10 < 5)));
        Assert.Equal(DiagnosisStatus.NotTriggered, Rule(clean, "D").status);

        var src = Fixed(1, n, m => m % 10 < 5);
        src.aKw![10] = 65535;
        var dirty = Run(Input(1, src));
        Assert.Equal(DiagnosisStatus.Triggered, Rule(dirty, "D").status);
        Assert.Contains(Rule(dirty, "D").aEvidence, e => e.szKey == "eq.diag.ev.dq_sentinel");
    }

    // ─────────────────────────── P 壓力 ───────────────────────────

    [Fact]
    public void P_未填需求壓力_待補場域資料()
    {
        var n = 1440;
        var r = Rule(Run(Input(1, Fixed(1, n, _ => true))), "P");
        Assert.Equal(DiagnosisStatus.PendingSiteData, r.status);
        Assert.True(HasMiss(r, "demand_pressure"));
    }

    [Fact]
    public void P_P50剛好不超過需求乘裕度不觸發_超過即觸發()
    {
        var n = 2 * 1440;
        // 供氣壓力 P50 = 7.0（各機排氣壓力代替）
        var notHit = Input(2, Fixed(1, n, _ => true));
        notHit.dDemandPressBar = 6.37;   // × 1.10 = 7.007 ≥ 7.0
        Assert.Equal(DiagnosisStatus.NotTriggered, Rule(Run(notHit), "P").status);

        var hit = Input(2, Fixed(1, n, _ => true));
        hit.dDemandPressBar = 6.36;      // × 1.10 = 6.996 < 7.0
        var r = Rule(Run(hit), "P");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.True(HasNote(r, "press_from_discharge"));
    }

    [Fact]
    public void P_無末端壓力_扣管損預留_捨去到試驗步階_節能5到6百分比()
    {
        var n = 2 * 1440;
        var input = Input(2, Fixed(1, n, _ => true));
        input.dDemandPressBar = 6.0;
        var result = Run(input);
        var r = Rule(result, "P");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.True(HasNote(r, "pipe_loss_assumed"));
        Assert.Equal("measure_end_pressure", r.szNextStep);
        // P05 = 6.93 → 6.93 − 6.0 − 0.3 = 0.63 → 捨去到 0.2 倍數 = 0.6 bar
        var dKwh = result.dPeriodKwh;
        Assert.Equal(0.6 * 0.05 * dKwh, r.savings!.kwhPeriod.dLow, 0);
        Assert.Equal(0.6 * 0.055 * dKwh, r.savings.kwhPeriod.dCenter, 0);
        Assert.Equal(0.6 * 0.06 * dKwh, r.savings.kwhPeriod.dHigh, 0);
    }

    [Fact]
    public void P_有末端壓力_可降量用末端P05_下一步為降壓試驗()
    {
        var n = 2 * 1440;
        var input = Input(2, Fixed(1, n, _ => true));
        input.dDemandPressBar = 6.0;
        input.system.aSupplyPress = Fill(n, m => 7.0 * (1 + J(m)));
        input.system.aUsePress = Fill(n, m => 6.6 * (1 + J(m)));
        var r = Rule(Run(input), "P");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.False(HasNote(r, "pipe_loss_assumed"));
        Assert.False(HasNote(r, "press_from_discharge"));
        Assert.Equal("pressure_trial", r.szNextStep);
        // 末端 P05 = 6.6 × 0.99 = 6.534 → 0.534 → 0.4 bar
        var ev = r.aEvidence.Single(e => e.szKey == "eq.diag.ev.p_reducible");
        Assert.Equal(0.4, Convert.ToDouble(ev.args["value"]), 6);
    }

    [Fact]
    public void P_裕度足但壓力低谷限制_可降不足一步不觸發()
    {
        var n = 2 * 1440;
        var input = Input(2, Fixed(1, n, _ => true));
        input.dDemandPressBar = 6.5;
        input.parameters = DiagnosisParameters.FromOverrides(new Dictionary<string, double> { [DiagnosisParameters.PressMarginRatio] = 1.0 }, null);
        var r = Rule(Run(input), "P");   // 6.93 − 6.5 − 0.3 = 0.13 < 0.2
        Assert.Equal(DiagnosisStatus.NotTriggered, r.status);
        Assert.True(HasNote(r, "trough_limits"));
    }

    // ─────────────────────────── L 洩漏 / 閒置 ───────────────────────────

    /// <summary>生產時段全加載；非生產時段每 20 分鐘加載 3 分鐘（殘餘流量 ≈ 生產的 15%）</summary>
    private static CompressorSeries LeakyCompressor(int n, double? ratedFlow = 20)
    {
        var sched = WeekdaySchedule();
        return Fixed(1, n, m => sched.IsProduction(From.AddMinutes(m)) || m % 20 < 3, ratedFlow: ratedFlow);
    }

    [Fact]
    public void L_未設定生產時段_待補場域資料()
    {
        var n = 7 * 1440;
        var r = Rule(Run(Input(7, LeakyCompressor(n))), "L");
        Assert.Equal(DiagnosisStatus.PendingSiteData, r.status);
    }

    [Fact]
    public void L_有流量路徑_洩漏比15百分比為注意_出區間節能()
    {
        var n = 7 * 1440;
        var input = Input(7, LeakyCompressor(n));
        input.schedule = WeekdaySchedule();
        var r = Rule(Run(input), "L");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.Equal("attention", r.szLevel);
        Assert.False(r.isInferred);
        Assert.NotNull(r.savings);
        Assert.True(r.savings!.kwhPeriod.dLow <= r.savings.kwhPeriod.dCenter);
        Assert.True(r.savings.kwhPeriod.dCenter <= r.savings.kwhPeriod.dHigh);
        var ratio = Convert.ToDouble(r.aEvidence.Single(e => e.szKey == "eq.diag.ev.l_flow").args["ratio"]);
        Assert.InRange(ratio, 14, 16);
    }

    [Fact]
    public void L_無流量點改走dutycycle並標推定_缺額定產氣量則資料不足()
    {
        var n = 7 * 1440;
        var src = LeakyCompressor(n);
        src.aFlow = null;
        var input = Input(7, src);
        input.aHourly.ForEach(h => h.dOutputQty = null);
        input.schedule = WeekdaySchedule();
        var r = Rule(Run(input), "L");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.True(r.isInferred);
        Assert.True(HasNote(r, "duty_cycle_upper_bound"));

        var src2 = LeakyCompressor(n, ratedFlow: null);
        src2.aFlow = null;
        var input2 = Input(7, src2);
        input2.schedule = WeekdaySchedule();
        var r2 = Rule(Run(input2), "L");
        Assert.Equal(DiagnosisStatus.InsufficientData, r2.status);
        Assert.True(HasMiss(r2, "rated_flow"));
        Assert.Equal("pressure_decay_test", r2.szNextStep);
    }

    [Fact]
    public void L_非生產窗口不足3個不同日_資料不足()
    {
        var n = 2 * 1440;   // 週一、週二 → 窗口只落在 2 個日期
        var input = Input(2, LeakyCompressor(n));
        input.schedule = WeekdaySchedule();
        var r = Rule(Run(input), "L");
        Assert.Equal(DiagnosisStatus.InsufficientData, r.status);
        Assert.True(HasMiss(r, "idle_windows"));
    }

    // ─────────────────────────── S 運轉與排序 ───────────────────────────

    [Fact]
    public void S1_卸載比剛好20百分比不觸發_30百分比觸發()
    {
        var n = 1440;
        Assert.Equal(DiagnosisStatus.NotTriggered, Rule(Run(Input(1, Fixed(1, n, m => m % 10 < 8))), "S1").status);
        var r = Rule(Run(Input(1, Fixed(1, n, m => m % 10 < 7))), "S1");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.True(r.isInferred);   // 無 LOADED 點
    }

    [Fact]
    public void S2_卸載後重新加載每段多一次啟動_超過每小時啟動上限的候選被否決()
    {
        var n = 1440;
        // 每 30 分：加載 10 / 卸載 20 → 每小時 2 段卸載
        var src = Fixed(1, n, m => m % 30 < 10);
        src.nMaxStartsPerHour = 1;
        var r = Rule(Run(Input(1, src)), "S2");
        Assert.Equal(DiagnosisStatus.NotTriggered, r.status);
        Assert.All(r.aCandidates!.Where(c => c.nDelayMinutes < 20), c => Assert.False(c.isFeasible));

        src.nMaxStartsPerHour = 2;
        var r2 = Rule(Run(Input(1, src)), "S2");
        Assert.Equal(DiagnosisStatus.Triggered, r2.status);
        var best = r2.aCandidates!.Single(c => c.nDelayMinutes == 5);
        Assert.True(best.isFeasible);
        Assert.Equal(48, best.nAddedStarts);          // 每小時 2 段 × 24
        Assert.Equal(2, best.nMaxStartsPerHour);
        // 每段省 15 分 × 15 kW
        Assert.Equal(48 * 15 * 15 / 60.0, best.kwhPeriod.dCenter, 0);
    }

    [Fact]
    public void S2_卸載段本來就以停機結束_不增加啟動次數()
    {
        var n = 1440;
        // 每小時：加載 30 → 卸載 20 → 停機 10
        var src = Fixed(1, n, m => m % 60 < 30, isRunning: m => m % 60 < 50);
        var r = Rule(Run(Input(1, src)), "S2");
        var c5 = r.aCandidates!.Single(c => c.nDelayMinutes == 5);
        Assert.Equal(0, c5.nAddedStarts);
        Assert.True(c5.isLimitUnknown);
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
    }

    [Fact]
    public void S3_每小時循環P95超過參考線才觸發()
    {
        var n = 1440;
        Assert.Equal(DiagnosisStatus.NotTriggered, Rule(Run(Input(1, Fixed(1, n, m => m % 20 < 10))), "S3").status); // 3 次/h
        var r = Rule(Run(Input(1, Fixed(1, n, m => m % 4 < 2))), "S3");                                                 // 15 次/h
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.Equal(15.0, Convert.ToDouble(r.aEvidence[0].args["p95"]), 6);
    }

    [Fact]
    public void S4_單台不適用_兩台同時卸載觸發()
    {
        var n = 1440;
        Assert.Equal(DiagnosisStatus.NotApplicable, Rule(Run(Input(1, Fixed(1, n, _ => true))), "S4").status);
        var r = Rule(Run(Input(1, Fixed(1, n, m => m % 10 < 5), Fixed(2, n, m => m % 10 < 5))), "S4");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
    }

    [Fact]
    public void S5_VSD滿載而定頻卸載_順序倒置觸發()
    {
        var n = 1440;
        var vsd = Fixed(1, n, _ => true, controlType: CompressorControlType.Vsd);   // 恆 100% 滿載
        var fix = Fixed(2, n, m => m % 10 < 5);
        Assert.Equal(DiagnosisStatus.Triggered, Rule(Run(Input(1, vsd, fix)), "S5").status);
        Assert.Equal(DiagnosisStatus.NotApplicable, Rule(Run(Input(1, Fixed(3, n, _ => true), fix)), "S5").status);
    }

    [Fact]
    public void S6_運轉但從不加載的機台_觸發並以實測kW估節能()
    {
        var n = 1440;
        var r = Rule(Run(Input(1, Fixed(1, n, _ => true), Fixed(2, n, _ => false))), "S6");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.Equal(15 * 24, r.savings!.kwhPeriod.dCenter, 0);
    }

    // ─────────────────────────── E 設備適配 ───────────────────────────

    private static List<EquipmentHourlyModel> SpRows(DateTime start, int hours, double sp, double noise)
    {
        return Enumerable.Range(0, hours).Select(h =>
        {
            var dSp = sp * (1 + noise * ((h % 5) - 2) / 2.0);
            return new EquipmentHourlyModel
            {
                nProfileId = 1, dtHourStart = start.AddHours(h), dOutputQty = 15 * 60, dKwh = dSp * 15, dRunHours = 1,
                dAvgPressure = 7.0, nQuality = 1,
            };
        }).ToList();
    }

    [Fact]
    public void E1_未設定基準期_待設定並於結果提示()
    {
        var n = 1440;
        var result = Run(Input(1, Fixed(1, n, _ => true)));
        Assert.Equal(DiagnosisStatus.PendingSetting, Rule(result, "E1").status);
        Assert.True(result.isBaselinePeriodMissing);
    }

    [Fact]
    public void E1_同工況比功率惡化10百分比判劣化_僅雜訊不判()
    {
        var n = 2 * 1440;
        var dtBase = From.AddDays(-30);
        var input = Input(2, Fixed(1, n, _ => true));
        input.dtBaselineFrom = dtBase;
        input.dtBaselineTo = dtBase.AddDays(2);
        input.aBaselineHourly = SpRows(dtBase, 48, 5.0, 0.01);
        input.aHourly = SpRows(From, 48, 5.5, 0.01);
        var r = Rule(Run(input), "E1");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.InRange(Convert.ToDouble(r.aEvidence[0].args["pct"]), 9.9, 10.1);
        Assert.True(r.savings!.kwhPeriod.dCenter > 0);

        input.aHourly = SpRows(From, 48, 5.0, 0.01);
        Assert.Equal(DiagnosisStatus.NotTriggered, Rule(Run(input), "E1").status);
    }

    [Fact]
    public void E1_各格兩期都要足夠小時數才比()
    {
        var res = EquipmentFitRules.CompareMatched(
            SpRows(From.AddDays(-30), 9, 5.0, 0), SpRows(From, 48, 5.5, 0), 20, 10, 0.5, 10);
        Assert.Null(res);
    }

    [Fact]
    public void E3_全部滿載仍低於需求壓力_判過小()
    {
        var n = 1440;
        var input = Input(1, Fixed(1, n, _ => true));
        input.dDemandPressBar = 7.5;
        Assert.Equal(DiagnosisStatus.Triggered, Rule(Run(input), "E3").status);
        input.dDemandPressBar = 6.0;
        Assert.Equal(DiagnosisStatus.NotTriggered, Rule(Run(input), "E3").status);
    }

    [Fact]
    public void E4_無節流機型不適用_有則觸發()
    {
        var n = 1440;
        Assert.Equal(DiagnosisStatus.NotApplicable, Rule(Run(Input(1, Fixed(1, n, _ => true))), "E4").status);
        var r = Rule(Run(Input(1, Fixed(1, n, m => m % 2 == 0, controlType: CompressorControlType.Modulation))), "E4");
        Assert.Equal(DiagnosisStatus.Triggered, r.status);
        Assert.Equal("vsd", r.aEvidence[0].args["suggest"]);
    }

    // ─────────────────────────── 經濟 / 重疊 ───────────────────────────

    [Fact]
    public void 年化與電價_低高各自年化_無電價不出金額()
    {
        var n = 7 * 1440;
        var input = Input(7, Fixed(1, n, _ => true));
        input.dDemandPressBar = 6.0;
        input.dPrice = 4.0;
        var r = Rule(Run(input), "P");
        Assert.Equal(r.savings!.kwhPeriod.dCenter * 365 / 7, r.savings.kwhYear.dCenter, 0);
        Assert.InRange(r.savings.ntdYear!.dHigh, r.savings.kwhYear.dHigh * 4.0 - 4, r.savings.kwhYear.dHigh * 4.0 + 4);

        input.dPrice = null;
        Assert.Null(Rule(Run(input), "P").savings!.ntdYear);
    }

    [Fact]
    public void P與L同時觸發_標重疊旗標()
    {
        var n = 7 * 1440;
        var input = Input(7, LeakyCompressor(n));
        input.schedule = WeekdaySchedule();
        input.dDemandPressBar = 6.0;
        var result = Run(input);
        Assert.Equal(DiagnosisStatus.Triggered, Rule(result, "P").status);
        Assert.Equal(DiagnosisStatus.Triggered, Rule(result, "L").status);
        Assert.Contains("p_l_overlap", result.aOverlapFlags);
        // 非生產時段的卸載空轉：L 與 S2 都計入
        Assert.Equal(DiagnosisStatus.Triggered, Rule(result, "S2").status);
        Assert.Contains("l_s_overlap", result.aOverlapFlags);
    }

    // ─────────────────────────── 生產時段 / 區間工具 ───────────────────────────

    [Fact]
    public void 生產時段_解析HHmm_假日整天非生產()
    {
        Assert.Equal(1440, ProductionSchedule.ParseHm("24:00"));
        Assert.Equal(510, ProductionSchedule.ParseHm("08:30"));
        Assert.Null(ProductionSchedule.ParseHm("24:30"));
        Assert.Null(ProductionSchedule.ParseHm("abc"));

        var s = WeekdaySchedule();
        Assert.True(s.IsProduction(From.AddHours(9)));
        s.holidays.Add(From);
        Assert.False(s.IsProduction(From.AddHours(9)));
        s.isHolidayNonProduction = false;
        Assert.True(s.IsProduction(From.AddHours(9)));
    }

    [Fact]
    public void 窗口區間_少於10個取最小最大_10個以上取P10到P90()
    {
        var r = DiagnosisMath.WindowRange([3, 1, 2])!;
        Assert.Equal((1.0, 2.0, 3.0), (r.dLow, r.dCenter, r.dHigh));
        var r10 = DiagnosisMath.WindowRange(Enumerable.Range(0, 11).Select(i => (double)i).ToList())!;
        Assert.Equal(1.0, r10.dLow, 6);
        Assert.Equal(5.0, r10.dCenter, 6);
        Assert.Equal(9.0, r10.dHigh, 6);
    }
}
