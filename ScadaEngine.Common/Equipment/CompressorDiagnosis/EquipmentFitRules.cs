using ScadaEngine.Common.Data.Models;
using static ScadaEngine.Common.Equipment.CompressorDiagnosis.DataQualityRules;

namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// E 規則：設備適配與生命週期（skill ④ equipment-fit-and-lifecycle，優先序 4）。
///   E1 同工況比功率劣化：基準期 vs 分析期，EquipmentHourly 依「流量格 × 壓力格」配對，
///      兩期同格皆 ≥ 最少小時數才比；差異 % 依分析期 kWh 加權，區間 = ± 1.96 × 合成標準誤；
///      區間下限 &gt; 0 且中心 &gt; 劣化門檻（預設 5%）才判「劣化」
///   E2 過大：定頻 = 加載率 &lt; 門檻（預設 50%）且頻繁循環；VSD = 運轉時間在最低負載附近占比 &gt; 門檻（預設 30%）
///   E3 過小：全部機台滿載（定頻加載 / VSD ≥ 滿載 %）而供氣壓力仍低於需求壓力的分鐘數 ≥ 門檻
///   E4 進氣節流機型：依平均負載建議改 VSD（&lt; 60%）或定頻加卸載 + 儲氣
/// </summary>
public static class EquipmentFitRules
{
    /// <summary>E1 只取「整小時運轉」的小時（RunHours ≥ 0.95），避免啟停暫態拉高比功率</summary>
    private const double FullHourRun = 0.95;
    /// <summary>E4 建議改 VSD 的平均負載上限 %</summary>
    private const double ModulationVsdBelowPct = 60;

    public static List<DiagnosisRuleResult> Evaluate(DiagnosisContext ctx) => [E1(ctx), E2(ctx), E3(ctx), E4(ctx)];

    private static bool IsValidHour(EquipmentHourlyModel r) =>
        r.nQuality == 1 && r.dOutputQty is > 0 && r.dKwh > 0 && r.dRunHours >= FullHourRun;

    /// <summary>單台 E1 計算結果（供單元測試）</summary>
    public sealed record DegradeResult(double dDiffPct, double dLowPct, double dHighPct, int nCells, int nRecentHours, double dRecentKwh);

    /// <summary>
    /// 同工況比較（純函式）。nFlowBins 依 dRatedFlow 等分（未填 → 兩期觀測最大流量）；壓力格寬 dPressBin（AvgPressure 缺 → 不分壓力格）。
    /// 無可比格 → null。
    /// </summary>
    public static DegradeResult? CompareMatched(
        IEnumerable<EquipmentHourlyModel> baseline, IEnumerable<EquipmentHourlyModel> recent,
        double? dRatedFlow, int nFlowBins, double dPressBin, int nMinHours)
    {
        var b = baseline.Where(IsValidHour).ToList();
        var r = recent.Where(IsValidHour).ToList();
        if (b.Count == 0 || r.Count == 0) return null;
        var dMaxFlow = dRatedFlow is > 0 ? dRatedFlow.Value : b.Concat(r).Max(x => x.dOutputQty!.Value / 60.0);
        if (dMaxFlow <= 0 || nFlowBins <= 0) return null;
        var dWidth = dMaxFlow / nFlowBins;

        (int, int) Cell(EquipmentHourlyModel x)
        {
            var nF = Math.Min(nFlowBins - 1, (int)Math.Floor(x.dOutputQty!.Value / 60.0 / dWidth));
            var nP = x.dAvgPressure is double p && dPressBin > 0 ? (int)Math.Floor(p / dPressBin) : -1;
            return (nF, nP);
        }
        static double Sp(EquipmentHourlyModel x) => 60.0 * x.dKwh / x.dOutputQty!.Value;

        var bCells = b.GroupBy(Cell).ToDictionary(g => g.Key, g => g.ToList());
        var rCells = r.GroupBy(Cell).ToDictionary(g => g.Key, g => g.ToList());
        double dW = 0, dSum = 0;
        var parts = new List<(double w, double se)>();
        int nCells = 0, nHours = 0;
        double dKwh = 0;
        foreach (var (key, rList) in rCells.OrderBy(kv => kv.Key))
        {
            if (!bCells.TryGetValue(key, out var bList)) continue;
            if (rList.Count < nMinHours || bList.Count < nMinHours) continue;
            var bSp = bList.Select(Sp).ToList();
            var rSp = rList.Select(Sp).ToList();
            var dMb = bSp.Average();
            var dMr = rSp.Average();
            if (dMb <= 0) continue;
            var dDiff = (dMr - dMb) / dMb * 100.0;
            var dSe = Math.Sqrt(DiagnosisMath.SampleVariance(rSp) / rSp.Count + DiagnosisMath.SampleVariance(bSp) / bSp.Count) / dMb * 100.0;
            var dWeight = rList.Sum(x => x.dKwh);
            dW += dWeight;
            dSum += dWeight * dDiff;
            parts.Add((dWeight, dSe));
            nCells++;
            nHours += rList.Count;
            dKwh += dWeight;
        }
        if (nCells == 0 || dW <= 0) return null;
        var dCenter = dSum / dW;
        var dSeTotal = Math.Sqrt(parts.Sum(x => (x.w / dW) * (x.w / dW) * x.se * x.se));
        return new DegradeResult(dCenter, dCenter - 1.96 * dSeTotal, dCenter + 1.96 * dSeTotal, nCells, nHours, dKwh);
    }

    public static DiagnosisRuleResult E1(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("E1", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.E1";
        var input = ctx.input;
        if (input.dtBaselineFrom == null || input.dtBaselineTo == null || input.dtBaselineTo <= input.dtBaselineFrom)
        {
            b.Miss("baseline_period");
            return b.Done(DiagnosisStatus.PendingSetting);
        }
        if (input.dtBaselineFrom < input.dtTo && input.dtBaselineTo > input.dtFrom) b.Note("baseline_overlap");

        var nBins = (int)b.P(DiagnosisParameters.FlowBins);
        var dPressBin = b.P(DiagnosisParameters.PressBinBar);
        var nMinHours = (int)b.P(DiagnosisParameters.MinHoursPerCell);
        var dDegrade = b.P(DiagnosisParameters.DegradePct);

        double dLow = 0, dCenter = 0, dHigh = 0;
        int nCompared = 0;
        var isHit = false;
        foreach (var c in ctx.comps)
        {
            var id = c.src.nId;
            var res = CompareMatched(
                input.aBaselineHourly.Where(x => x.nProfileId == id && x.dtHourStart >= input.dtBaselineFrom && x.dtHourStart < input.dtBaselineTo),
                input.aHourly.Where(x => x.nProfileId == id && x.dtHourStart >= input.dtFrom && x.dtHourStart < input.dtTo),
                c.src.dRatedFlow, nBins, dPressBin, nMinHours);
            if (res == null)
            {
                b.Miss("matched_cells", A(("name", c.src.szName), ("min", nMinHours)));
                continue;
            }
            nCompared++;
            if (c.src.dRatedFlow is not > 0) b.Note("flow_bin_observed", A(("name", c.src.szName)));
            var isDegraded = res.dLowPct > 0 && res.dDiffPct > dDegrade;
            b.Ev("e1_diff", A(("name", c.src.szName), ("pct", res.dDiffPct), ("low", res.dLowPct), ("high", res.dHighPct),
                ("cells", res.nCells), ("hours", res.nRecentHours), ("degraded", isDegraded)));
            if (!isDegraded) continue;
            isHit = true;
            // 劣化多耗電 = 分析期 kWh × D ÷ (100 + D)（以基準效率會用的電為基準）
            var dPeriodKwh = input.aHourly.Where(x => x.nProfileId == id && x.dtHourStart >= input.dtFrom && x.dtHourStart < input.dtTo).Sum(x => x.dKwh);
            double Excess(double d) => d <= 0 ? 0 : dPeriodKwh * d / (100.0 + d);
            dLow += Excess(res.dLowPct);
            dCenter += Excess(res.dDiffPct);
            dHigh += Excess(res.dHighPct);
        }
        if (input.aHourly.Any(x => x.dAvgPressure == null && x.nQuality == 1)) b.Note("pressure_not_matched");
        if (nCompared == 0) return b.Done(DiagnosisStatus.InsufficientData);
        if (!isHit) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.savings = ctx.Savings(new DiagnosisRange(dLow, dCenter, dHigh));
        b.r.szNextStep = "maintenance_check";
        return b.Done(DiagnosisStatus.Triggered);
    }

    public static DiagnosisRuleResult E2(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("E2", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.E2";
        var list = ctx.comps.Where(c => c.HasLoadStates && c.szControlType is CompressorControlType.Fixed or CompressorControlType.Vsd).ToList();
        if (list.Count == 0)
        {
            b.Miss("kw_or_loaded");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (list.Any(c => c.isControlTypeInferred)) { b.r.isInferred = true; b.Note("control_type_inferred"); }
        var isHit = false;
        foreach (var c in list)
        {
            var nLd = c.Count(MinuteState.Loaded);
            var nRun = nLd + c.Count(MinuteState.Unloaded);
            if (nRun < 60) continue;
            if (c.szControlType == CompressorControlType.Fixed)
            {
                if (c.isLoadInferred) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; }
                var dLimit = b.P(DiagnosisParameters.OversizeLoadPct);
                var dRef = b.P(DiagnosisParameters.CyclesPerHourRef);
                var dLoadedPct = nLd * 100.0 / nRun;
                var isFreq = SequencingRules.IsFrequentCycling(c, dRef);
                var isOver = dLoadedPct < dLimit && isFreq;
                b.Ev("e2_fixed", A(("name", c.src.szName), ("loadedPct", dLoadedPct), ("frequent", isFreq), ("oversized", isOver)));
                if (isOver) isHit = true;
            }
            else
            {
                if (!c.kwOk || c.dRatedKwEff is not > 0) { b.Miss("vsd_kw", A(("name", c.src.szName))); continue; }
                var loads = new List<double>();
                for (var m = 0; m < ctx.n; m++)
                    if (c.IsRunning(m) && c.LoadPctAt(m) is double d) loads.Add(d);
                if (loads.Count < 60) continue;
                double dMin;
                if (c.src.dMinLoadPct is > 0) dMin = c.src.dMinLoadPct.Value;
                else
                {
                    dMin = DiagnosisMath.Percentile(loads, 5)!.Value;
                    b.Note("min_load_observed", A(("name", c.src.szName), ("pct", dMin)));
                }
                var dLimit = b.P(DiagnosisParameters.VsdMinLoadFractionPct);
                var dFrac = loads.Count(x => x <= dMin * 1.05) * 100.0 / loads.Count;
                var isOver = dFrac > dLimit;
                b.Ev("e2_vsd", A(("name", c.src.szName), ("minLoad", dMin), ("pct", dFrac), ("oversized", isOver)));
                if (isOver) isHit = true;
            }
        }
        if (!isHit) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.szNextStep = "right_size_review";
        return b.Done(DiagnosisStatus.Triggered);
    }

    public static DiagnosisRuleResult E3(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("E3", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.E3";
        var dDemand = ctx.input.dDemandPressBar;
        if (dDemand is not > 0)
        {
            b.Miss("demand_pressure");
            return b.Done(DiagnosisStatus.PendingSiteData);
        }
        var (supply, isFromDischarge) = ctx.SupplyPressure();
        if (supply == null)
        {
            b.Miss("supply_pressure");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (isFromDischarge) b.Note("press_from_discharge");
        if (ctx.comps.Count == 0 || ctx.comps.Any(c => !c.HasLoadStates))
        {
            b.Miss("kw_or_loaded");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (ctx.comps.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; }
        var dFull = b.P(DiagnosisParameters.VsdFullLoadPct);
        var dLimit = b.P(DiagnosisParameters.UndersizeMinMinutes);

        var nShort = 0;
        var dMinPress = double.NaN;
        for (var m = 0; m < ctx.n; m++)
        {
            if (double.IsNaN(supply[m]) || supply[m] >= dDemand.Value) continue;
            var isAllFull = ctx.comps.All(c =>
                c.state[m] == MinuteState.Loaded
                && (c.szControlType != CompressorControlType.Vsd || (c.LoadPctAt(m) is double d && d >= dFull)));
            if (!isAllFull) continue;
            nShort++;
            if (double.IsNaN(dMinPress) || supply[m] < dMinPress) dMinPress = supply[m];
        }
        b.Ev("e3_short", A(("minutes", nShort), ("minPress", double.IsNaN(dMinPress) ? null : dMinPress), ("demand", dDemand.Value)));
        if (nShort < dLimit) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.szNextStep = "leak_then_capacity";
        return b.Done(DiagnosisStatus.Triggered);
    }

    public static DiagnosisRuleResult E4(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("E4", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.E4";
        // 只看設定頁明確填的節流機型（功率分佈無法可靠區分節流與 VSD）
        var list = ctx.comps.Where(c => c.src.szControlType == CompressorControlType.Modulation).ToList();
        if (list.Count == 0) return b.Done(DiagnosisStatus.NotApplicable);
        foreach (var c in list)
        {
            var loads = new List<double>();
            for (var m = 0; m < ctx.n; m++)
                if (c.IsRunning(m) && c.LoadPctAt(m) is double d) loads.Add(d);
            double? dAvg = loads.Count > 0 ? loads.Average() : null;
            var szSuggest = dAvg == null ? "unknown" : dAvg < ModulationVsdBelowPct ? "vsd" : "fixed_storage";
            b.Ev("e4_modulation", A(("name", c.src.szName), ("avgLoad", dAvg), ("suggest", szSuggest)));
        }
        b.r.szNextStep = "replace_modulation";
        return b.Done(DiagnosisStatus.Triggered);
    }
}
