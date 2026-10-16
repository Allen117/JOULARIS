using static ScadaEngine.Common.Equipment.CompressorDiagnosis.DataQualityRules;

namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// P 規則：壓力過高（skill ① pressure-optimization，優先序 1）。
///
/// 1. 供氣壓力來源：SYS_SUPPLYPRESS（可信）→ 運轉中各機 DISCHPRESS 最高者（標「以排氣壓力代替」）→ 資料不足
/// 2. 觸發：運轉期間供氣壓力 P50 &gt; 需求壓力 × P1（預設 1.10，waste_catalog ①）
/// 3. 可降量（由資料推導）：
///      有末端壓力 → 末端 P05 − 需求（實測管損 = 供氣 − 末端 中位數）
///      無末端壓力 → 供氣 P05 − 需求 − 管損預留 P4（預設 0.3 bar），下一步改為「先量測末端壓力」
///    再無條件捨去到試驗步階 P3（預設 0.2 bar）的整數倍；不足一步 → 不觸發（壓力低谷限制降壓）
/// 4. 篩檢節能 = 可降 bar × 5% ~ 6%（P2，pressure-optimization 步驟 6）× 期間 kWh，中心取兩者平均
/// 5. 附帶檢查：壓力低谷（P10 以下）前 5 分鐘內是否有機台啟動（降壓可能讓他機更常啟動）、銘牌最高壓力、露點監測
/// </summary>
public static class PressureRule
{
    public static DiagnosisRuleResult Evaluate(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("P", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.P";
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

        var vals = new List<double>();
        for (var m = 0; m < ctx.n; m++)
            if (ctx.anyRunning[m] && !double.IsNaN(supply[m])) vals.Add(supply[m]);
        if (vals.Count < 60)
        {
            b.Miss("running_pressure_samples", A(("count", vals.Count)));
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        vals.Sort();
        var dP05 = DiagnosisMath.PercentileSorted(vals, 5)!.Value;
        var dP10 = DiagnosisMath.PercentileSorted(vals, 10)!.Value;
        var dP50 = DiagnosisMath.PercentileSorted(vals, 50)!.Value;
        var dP95 = DiagnosisMath.PercentileSorted(vals, 95)!.Value;
        var dRatio = b.P(DiagnosisParameters.PressMarginRatio);
        var dThreshold = dDemand.Value * dRatio;
        b.Ev("p_supply", A(("p05", dP05), ("p50", dP50), ("p95", dP95)));
        b.Ev("p_demand", A(("demand", dDemand.Value), ("ratio", dRatio), ("threshold", dThreshold)));

        // 可降量
        double dReducible;
        var isUseOk = ctx.use != null && ctx.qUse.isTrusted;
        if (isUseOk)
        {
            var useVals = new List<double>();
            var losses = new List<double>();
            for (var m = 0; m < ctx.n; m++)
            {
                if (!ctx.anyRunning[m] || double.IsNaN(ctx.use![m])) continue;
                useVals.Add(ctx.use[m]);
                if (!double.IsNaN(supply[m])) losses.Add(supply[m] - ctx.use[m]);
            }
            var dUseP05 = DiagnosisMath.Percentile(useVals, 5);
            if (dUseP05 == null)
            {
                b.Miss("use_pressure_samples");
                return b.Done(DiagnosisStatus.InsufficientData);
            }
            dReducible = dUseP05.Value - dDemand.Value;
            b.Ev("p_use", A(("p05", dUseP05.Value), ("loss", DiagnosisMath.Median(losses))));
        }
        else
        {
            var dReserve = b.P(DiagnosisParameters.PipeLossReserveBar);
            dReducible = dP05 - dDemand.Value - dReserve;
            b.Note("pipe_loss_assumed", A(("reserve", dReserve)));
        }

        var dStep = b.P(DiagnosisParameters.TrialStepBar);
        var dReducibleStep = dReducible > 0 ? Math.Floor(dReducible / dStep + 1e-9) * dStep : 0;
        b.Ev("p_reducible", A(("raw", dReducible), ("step", dStep), ("value", dReducibleStep)));

        // 壓力低谷時段的啟動（skill 步驟 3–4：降壓是否會讓另一台啟動）
        var nTroughStarts = 0;
        foreach (var c in ctx.comps)
        {
            foreach (var nStart in c.aStarts)
            {
                for (var m = Math.Max(0, nStart - 5); m < nStart; m++)
                {
                    if (!double.IsNaN(supply[m]) && supply[m] <= dP10) { nTroughStarts++; break; }
                }
            }
        }
        b.Ev("p_trough_starts", A(("count", nTroughStarts), ("p10", dP10)));
        if (nTroughStarts > 0) b.Note("lowering_may_start_extra", A(("count", nTroughStarts)));

        // 銘牌最高工作壓力
        var withMax = ctx.comps.Where(c => c.src.dMaxPressure is > 0).ToList();
        if (withMax.Count == 0) b.Note("rated_pressure_missing");
        foreach (var c in withMax)
        {
            if (!c.pressOk) continue;
            var dCp95 = DiagnosisMath.Percentile(Running(ctx, c, c.press!), 95);
            if (dCp95 != null && dCp95 > c.src.dMaxPressure!.Value)
                b.Note("above_rated_pressure", A(("name", c.src.szName), ("p95", dCp95.Value), ("max", c.src.dMaxPressure.Value)));
        }

        // 露點
        if (ctx.dewpoint != null && ctx.qDewpoint.isTrusted)
        {
            var dew = ctx.dewpoint.Where(v => !double.IsNaN(v)).ToList();
            b.Ev("p_dewpoint", A(("p50", DiagnosisMath.Median(dew)), ("p95", DiagnosisMath.Percentile(dew, 95))));
        }
        else
        {
            b.Note("dewpoint_unmonitored");
        }

        if (dP50 <= dThreshold)
            return b.Done(DiagnosisStatus.NotTriggered);
        if (dReducibleStep < dStep - 1e-9)
        {
            b.Note("trough_limits");
            return b.Done(DiagnosisStatus.NotTriggered);
        }

        var dLowPct = b.P(DiagnosisParameters.SavingPerBarLowPct);
        var dHighPct = b.P(DiagnosisParameters.SavingPerBarHighPct);
        if (dHighPct < dLowPct) (dLowPct, dHighPct) = (dHighPct, dLowPct);
        var dKwh = ctx.dPeriodKwh;
        b.r.savings = ctx.Savings(new DiagnosisRange(
            dReducibleStep * dLowPct / 100.0 * dKwh,
            dReducibleStep * (dLowPct + dHighPct) / 2.0 / 100.0 * dKwh,
            dReducibleStep * dHighPct / 100.0 * dKwh));
        b.r.szNextStep = isUseOk ? "pressure_trial" : "measure_end_pressure";
        b.r.aChecklist = ["record_setpoint", "step_down", "watch_end_pressure", "stop_condition", "restore"];
        return b.Done(DiagnosisStatus.Triggered);
    }

    private static IEnumerable<double> Running(DiagnosisContext ctx, CompressorState c, double[] arr)
    {
        for (var m = 0; m < ctx.n; m++)
            if ((!ctx.hasAnyState || c.IsRunning(m)) && !double.IsNaN(arr[m])) yield return arr[m];
    }
}
