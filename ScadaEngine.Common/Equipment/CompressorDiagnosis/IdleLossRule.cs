using static ScadaEngine.Common.Equipment.CompressorDiagnosis.DataQualityRules;

namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// L 規則：閒置 / 洩漏（skill ② leakage-and-idle-loss，優先序 2）。
///
/// 非生產窗口 = 依生產時段（每週排程 + 假日）切出的連續非生產區段，長度 ≥ L 最短窗口（預設 60 分）。
/// 窗口需分布在 ≥ L2（預設 3）個不同日，否則資料不足（skill 步驟 3：多窗口取區間，不取單一有利窗口）。
///
/// 路徑 A（有可信流量：SYS_FLOW，或所有機台 AIRFLOW 皆可信時加總）：
///   每窗口殘餘流量 = 窗口內平均流量；生產基準 = 生產時段且有機台運轉時的平均流量
/// 路徑 B（無可信流量）：duty-cycle 法 — 每窗口 Σ(各機加載比 × 額定產氣量)，標「上限估計」；缺額定產氣量 → 資料不足
/// 兩路徑皆不成立 → 資料不足，下一步建議停機保壓衰減測試。
///
/// 中心 = 窗口中位數；區間 = 窗口數 &lt; 10 取最小 ~ 最大，≥ 10 取 P10 ~ P90。
/// 洩漏比 = 中心 ÷ 生產基準；≥ 顯著門檻 → significant、≥ 注意門檻 → attention（觸發）。
/// kWh = 流量 × 比功率 × 非生產時數：低 = 流量低 × 比功率 P25、中心 = 中位 × P50、高 = 流量高 × P75（運轉小時比功率分佈）。
/// </summary>
public static class IdleLossRule
{
    public static DiagnosisRuleResult Evaluate(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("L", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.L";
        var schedule = ctx.input.schedule;
        if (schedule == null || !schedule.IsConfigured)
        {
            b.Miss("production_schedule");
            return b.Done(DiagnosisStatus.PendingSiteData);
        }

        var isProd = new bool[ctx.n];
        var nNonProd = 0;
        for (var m = 0; m < ctx.n; m++)
        {
            isProd[m] = schedule.IsProduction(ctx.TimeAt(m));
            if (!isProd[m]) nNonProd++;
        }

        // 非生產窗口
        var nMinLen = (int)b.P(DiagnosisParameters.MinWindowMinutes);
        var windows = new List<(int nStart, int nEnd)>();
        var nSeg = -1;
        for (var m = 0; m <= ctx.n; m++)
        {
            var isIdle = m < ctx.n && !isProd[m];
            if (isIdle && nSeg < 0) nSeg = m;
            if (!isIdle && nSeg >= 0)
            {
                if (m - nSeg >= nMinLen) windows.Add((nSeg, m));
                nSeg = -1;
            }
        }

        // 流量來源
        double[]? flow = null;
        var isDutyCycle = false;
        if (ctx.sysFlow != null && ctx.qSysFlow.isTrusted)
        {
            flow = ctx.sysFlow;
        }
        else if (ctx.comps.Count > 0 && ctx.comps.All(c => c.flowOk))
        {
            flow = new double[ctx.n];
            for (var m = 0; m < ctx.n; m++)
            {
                double dSum = 0;
                var isOk = true;
                foreach (var c in ctx.comps)
                {
                    var v = c.flow![m];
                    if (double.IsNaN(v))
                    {
                        // 停機機台流量缺值視為 0；運轉中缺值 → 該分鐘不可用
                        if (c.state[m] == MinuteState.Off) continue;
                        isOk = false;
                        break;
                    }
                    dSum += v;
                }
                flow[m] = isOk ? dSum : double.NaN;
            }
            b.Note("flow_from_compressors");
        }
        else
        {
            var noRated = ctx.comps.Where(c => c.src.dRatedFlow is not > 0).Select(c => c.src.szName).ToList();
            var noState = ctx.comps.Where(c => !c.HasLoadStates).Select(c => c.src.szName).ToList();
            if (ctx.comps.Count == 0 || noRated.Count > 0 || noState.Count > 0)
            {
                b.Miss("flow_signal");
                if (noRated.Count > 0) b.Miss("rated_flow", A(("names", string.Join(", ", noRated))));
                if (noState.Count > 0) b.Miss("load_state", A(("names", string.Join(", ", noState))));
                b.r.szNextStep = "pressure_decay_test";
                return b.Done(DiagnosisStatus.InsufficientData);
            }
            isDutyCycle = true;
            b.r.isInferred = true;
            b.Note("duty_cycle_upper_bound");
        }

        double? MeanFlow(int nStart, int nEnd, bool isProductionFilter)
        {
            double dSum = 0;
            int nCnt = 0, nTotal = 0;
            for (var m = nStart; m < nEnd; m++)
            {
                if (isProductionFilter && (!isProd[m] || !ctx.anyRunning[m])) continue;
                nTotal++;
                double v;
                if (isDutyCycle)
                {
                    v = 0;
                    var isOk = true;
                    foreach (var c in ctx.comps)
                    {
                        var st = c.state[m];
                        if (st == MinuteState.Loaded) v += c.src.dRatedFlow!.Value;
                        else if (st is MinuteState.Unknown or MinuteState.RunningUnknownLoad) { isOk = false; break; }
                    }
                    if (!isOk) continue;
                }
                else
                {
                    v = flow![m];
                    if (double.IsNaN(v)) continue;
                }
                dSum += v;
                nCnt++;
            }
            // 窗口內有效分鐘需過半，避免缺值窗口代表性不足
            if (nCnt == 0 || nCnt < nTotal * 0.5) return null;
            return dSum / nCnt;
        }

        var windowFlows = new List<double>();
        var days = new HashSet<DateTime>();
        foreach (var (nS, nE) in windows)
        {
            var v = MeanFlow(nS, nE, false);
            if (v == null) continue;
            windowFlows.Add(v.Value);
            days.Add(ctx.TimeAt(nS).Date);
        }
        var nNeed = (int)b.P(DiagnosisParameters.MinIdleWindows);
        b.Ev("l_windows", A(("count", windowFlows.Count), ("days", days.Count), ("hours", nNonProd / 60.0)));
        if (days.Count < nNeed)
        {
            b.Miss("idle_windows", A(("have", days.Count), ("need", nNeed)));
            return b.Done(DiagnosisStatus.InsufficientData);
        }

        var dProd = MeanFlow(0, ctx.n, true);
        if (dProd is not > 0)
        {
            b.Miss("production_flow");
            return b.Done(DiagnosisStatus.InsufficientData);
        }

        var range = DiagnosisMath.WindowRange(windowFlows)!;
        var dRatio = range.dCenter / dProd.Value * 100.0;
        b.Ev("l_flow", A(("low", range.dLow), ("center", range.dCenter), ("high", range.dHigh), ("prod", dProd.Value), ("ratio", dRatio)));

        var dWarn = b.P(DiagnosisParameters.LeakWarnPct);
        var dSig = b.P(DiagnosisParameters.LeakSignificantPct);
        if (dRatio < dWarn) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.szLevel = dRatio >= dSig ? "significant" : "attention";
        b.Note("includes_legit_use");

        if (ctx.aHourlySp.Count == 0)
        {
            b.Note("no_specific_power");
        }
        else
        {
            var dHours = nNonProd / 60.0;
            var sp25 = DiagnosisMath.PercentileSorted(ctx.aHourlySp, 25)!.Value;
            var sp50 = DiagnosisMath.PercentileSorted(ctx.aHourlySp, 50)!.Value;
            var sp75 = DiagnosisMath.PercentileSorted(ctx.aHourlySp, 75)!.Value;
            b.Ev("l_sp", A(("p25", sp25), ("p50", sp50), ("p75", sp75)));
            // 比功率 kW/(m³/min) × 流量 m³/min = kW
            b.r.savings = ctx.Savings(new DiagnosisRange(
                range.dLow * sp25 * dHours, range.dCenter * sp50 * dHours, range.dHigh * sp75 * dHours));
        }
        b.r.szNextStep = "leak_survey";
        b.r.aChecklist = ["leak_ultrasonic", "pressure_decay_test", "isolate_idle_branches"];
        return b.Done(DiagnosisStatus.Triggered);
    }
}
