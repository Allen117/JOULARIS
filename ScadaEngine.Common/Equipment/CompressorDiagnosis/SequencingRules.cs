using static ScadaEngine.Common.Equipment.CompressorDiagnosis.DataQualityRules;

namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// S 規則：運轉與排序（skill ③ compressor-operation-and-sequencing，優先序 3）。
/// 加卸載狀態：有可信 LOADED 點用之；否則運轉中 kW ≥ 判定額定 × S1（預設 20%）視為加載（標「推定」）。
///   S1 卸載比 &gt; 門檻（預設 20%）
///   S2 停機延遲試算：每段連續卸載，候選延遲 5/10/15/30 分鐘 → 省下卸載 kWh 與新增啟動次數，全部列出
///   S3 頻繁加卸載循環：每運轉小時循環次數 P50 / P95，P95 &gt; 參考線（預設 10 次/h）
///   S4 多台同時部分負載（定頻卸載 / VSD 未滿載）占多機運轉時間比例
///   S5 VSD 滿載而定頻機卸載（調節機順序倒置）
///   S6 有運轉但幾乎從不加載的機台（&lt; 1% 運轉時間）
/// </summary>
public static class SequencingRules
{
    /// <summary>S6：加載時間占運轉時間低於此比例視為「只卸載不加載」</summary>
    private const double NeverLoadedRatio = 0.01;

    public static List<DiagnosisRuleResult> Evaluate(DiagnosisContext ctx) =>
        [S1(ctx), S2(ctx), S3(ctx), S4(ctx), S5(ctx), S6(ctx)];

    private static IEnumerable<CompressorState> WithStates(DiagnosisContext ctx) => ctx.comps.Where(c => c.HasLoadStates);

    public static DiagnosisRuleResult S1(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("S1", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.S1";
        var list = WithStates(ctx).ToList();
        if (list.Count == 0)
        {
            b.Miss("kw_or_loaded");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (list.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; b.Note("load_inferred"); }
        var dWarn = b.P(DiagnosisParameters.UnloadRatioWarnPct);
        var isHit = false;
        foreach (var c in list)
        {
            var nUn = c.Count(MinuteState.Unloaded);
            var nLd = c.Count(MinuteState.Loaded);
            if (nUn + nLd == 0) continue;
            var dPct = nUn * 100.0 / (nUn + nLd);
            double? dKwh = null;
            if (c.kwOk)
            {
                double dSum = 0;
                for (var m = 0; m < ctx.n; m++)
                    if (c.state[m] == MinuteState.Unloaded && !double.IsNaN(c.kw![m])) dSum += c.kw[m] / 60.0;
                dKwh = dSum;
            }
            b.Ev("s1_unload", A(("name", c.src.szName), ("pct", dPct), ("hours", nUn / 60.0), ("kwh", dKwh)));
            if (dPct > dWarn) isHit = true;
        }
        if (!isHit) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.szNextStep = "review_unload";
        return b.Done(DiagnosisStatus.Triggered);
    }

    /// <summary>連續卸載段（起, 訖不含, 結束後狀態）</summary>
    public static List<(int nStart, int nEnd, MinuteState next)> UnloadedSegments(CompressorState c)
    {
        var list = new List<(int, int, MinuteState)>();
        var n = c.state.Length;
        var nSeg = -1;
        for (var m = 0; m <= n; m++)
        {
            var isUn = m < n && c.state[m] == MinuteState.Unloaded;
            if (isUn && nSeg < 0) nSeg = m;
            if (!isUn && nSeg >= 0)
            {
                list.Add((nSeg, m, m < n ? c.state[m] : MinuteState.Unknown));
                nSeg = -1;
            }
        }
        return list;
    }

    public static DiagnosisRuleResult S2(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("S2", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.S2";
        var list = WithStates(ctx).Where(c => c.kwOk).ToList();
        foreach (var c in WithStates(ctx).Where(c => !c.kwOk))
            b.Note("s2_skip_no_kw", A(("name", c.src.szName)));
        if (list.Count == 0)
        {
            b.Miss("unloaded_kw");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (list.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; b.Note("load_inferred"); }

        var candidates = new List<ShutdownDelayCandidate>();
        var isLimitUnknown = list.Any(c => c.src.nMaxStartsPerHour is not > 0);
        foreach (var nDelay in DiagnosisParameters.ShutdownDelayCandidates)
        {
            double dCenter = 0, dLow = 0, dHigh = 0;
            int nAdded = 0, nMaxPerHour = 0;
            var isFeasible = true;
            foreach (var c in list)
            {
                var unKw = new List<double>();
                for (var m = 0; m < ctx.n; m++)
                    if (c.state[m] == MinuteState.Unloaded && !double.IsNaN(c.kw![m])) unKw.Add(c.kw[m]);
                unKw.Sort();
                var dKw25 = DiagnosisMath.PercentileSorted(unKw, 25) ?? 0;
                var dKw75 = DiagnosisMath.PercentileSorted(unKw, 75) ?? 0;

                var startsPerHour = new Dictionary<int, int>();
                foreach (var s in c.aStarts) startsPerHour[s / 60] = startsPerHour.GetValueOrDefault(s / 60) + 1;
                var nSavedMin = 0;
                foreach (var (nS, nE, next) in UnloadedSegments(c))
                {
                    var nLen = nE - nS;
                    if (nLen <= nDelay) continue;
                    for (var m = nS + nDelay; m < nE; m++)
                        if (!double.IsNaN(c.kw![m])) dCenter += c.kw[m] / 60.0;
                    nSavedMin += nLen - nDelay;
                    // 本來就停機的段：只是更早停，不增加啟動；其餘（重新加載 / 期間結束）→ 多一次啟動
                    if (next != MinuteState.Off)
                    {
                        nAdded++;
                        startsPerHour[nE / 60] = startsPerHour.GetValueOrDefault(nE / 60) + 1;
                    }
                }
                dLow += nSavedMin * dKw25 / 60.0;
                dHigh += nSavedMin * dKw75 / 60.0;
                var nMax = startsPerHour.Count > 0 ? startsPerHour.Values.Max() : 0;
                nMaxPerHour = Math.Max(nMaxPerHour, nMax);
                if (c.src.nMaxStartsPerHour is > 0 && nMax > c.src.nMaxStartsPerHour.Value) isFeasible = false;
            }
            candidates.Add(new ShutdownDelayCandidate
            {
                nDelayMinutes = nDelay,
                kwhPeriod = DiagnosisContext.RoundRange(new DiagnosisRange(Math.Min(dLow, dHigh), dCenter, Math.Max(dLow, dHigh)), 1),
                nAddedStarts = nAdded,
                nMaxStartsPerHour = nMaxPerHour,
                isFeasible = isFeasible,
                isLimitUnknown = isLimitUnknown,
            });
        }
        b.r.aCandidates = candidates;
        if (isLimitUnknown) b.Note("max_starts_unknown");

        var best = candidates.Where(x => x.isFeasible && x.kwhPeriod.dCenter > 0)
            .OrderByDescending(x => x.kwhPeriod.dCenter).ThenByDescending(x => x.nDelayMinutes).FirstOrDefault();
        if (best == null) return b.Done(DiagnosisStatus.NotTriggered);
        b.Ev("s2_best", A(("delay", best.nDelayMinutes), ("starts", best.nAddedStarts), ("maxPerHour", best.nMaxStartsPerHour)));
        b.Note("current_timer_unknown");
        b.r.savings = ctx.Savings(best.kwhPeriod);
        b.r.szNextStep = "shutdown_delay_trial";
        b.r.aChecklist = ["record_setpoint", "set_delay", "watch_starts", "stop_condition", "restore"];
        return b.Done(DiagnosisStatus.Triggered);
    }

    /// <summary>該機 P95 循環次數是否超過參考線（E2 共用）</summary>
    public static bool IsFrequentCycling(CompressorState c, double dRef) =>
        c.aCyclesPerHour.Count > 0 && DiagnosisMath.PercentileSorted(c.aCyclesPerHour, 95)!.Value > dRef;

    public static DiagnosisRuleResult S3(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("S3", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.S3";
        var list = WithStates(ctx).Where(c => c.aCyclesPerHour.Count > 0).ToList();
        if (list.Count == 0)
        {
            b.Miss("kw_or_loaded");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (list.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; b.Note("load_inferred"); }
        var dRef = b.P(DiagnosisParameters.CyclesPerHourRef);
        var isHit = false;
        foreach (var c in list)
        {
            var dP50 = DiagnosisMath.PercentileSorted(c.aCyclesPerHour, 50)!.Value;
            var dP95 = DiagnosisMath.PercentileSorted(c.aCyclesPerHour, 95)!.Value;
            b.Ev("s3_cycles", A(("name", c.src.szName), ("p50", dP50), ("p95", dP95), ("minLoaded", c.nMinLoadedSegment),
                ("hours", c.aCyclesPerHour.Count)));
            if (dP95 > dRef) isHit = true;
        }
        if (!isHit) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.szNextStep = "control_band_storage";
        return b.Done(DiagnosisStatus.Triggered);
    }

    private static bool IsVsd(CompressorState c) => c.szControlType == CompressorControlType.Vsd;
    private static bool IsFixed(CompressorState c) => c.szControlType == CompressorControlType.Fixed;

    public static DiagnosisRuleResult S4(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("S4", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.S4";
        if (ctx.comps.Count < 2) return b.Done(DiagnosisStatus.NotApplicable);
        var list = WithStates(ctx).ToList();
        if (list.Count < 2)
        {
            b.Miss("kw_or_loaded");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (list.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; b.Note("load_inferred"); }
        if (list.Any(c => c.isControlTypeInferred)) { b.r.isInferred = true; b.Note("control_type_inferred"); }
        var dFull = b.P(DiagnosisParameters.VsdFullLoadPct);
        var dLimit = b.P(DiagnosisParameters.MultiPartLoadPct);

        int nMulti = 0, nPart = 0, nVsdBoth = 0;
        for (var m = 0; m < ctx.n; m++)
        {
            int nRun = 0, nPartNow = 0, nVsdMod = 0;
            foreach (var c in list)
            {
                if (!c.IsRunning(m)) continue;
                nRun++;
                var st = c.state[m];
                var isPart = st == MinuteState.Unloaded;
                if (st == MinuteState.Loaded && IsVsd(c))
                {
                    var dLoad = c.LoadPctAt(m);
                    if (dLoad != null && dLoad < dFull) { isPart = true; nVsdMod++; }
                }
                if (isPart) nPartNow++;
            }
            if (nRun < 2) continue;
            nMulti++;
            if (nPartNow >= 2) nPart++;
            if (nVsdMod >= 2) nVsdBoth++;
        }
        if (nMulti == 0)
        {
            b.Ev("s4_single");
            return b.Done(DiagnosisStatus.NotTriggered);
        }
        var dPct = nPart * 100.0 / nMulti;
        b.Ev("s4_partload", A(("pct", dPct), ("multiHours", nMulti / 60.0), ("vsdHours", nVsdBoth / 60.0)));
        if (dPct <= dLimit) return b.Done(DiagnosisStatus.NotTriggered);
        b.Note("not_quantified");
        b.r.szNextStep = "sequencing_review";
        return b.Done(DiagnosisStatus.Triggered);
    }

    public static DiagnosisRuleResult S5(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("S5", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.S5";
        var list = WithStates(ctx).ToList();
        var vsd = list.Where(IsVsd).Where(c => c.kwOk).ToList();
        var fix = list.Where(IsFixed).ToList();
        if (vsd.Count == 0 || fix.Count == 0) return b.Done(DiagnosisStatus.NotApplicable);
        if (vsd.Concat(fix).Any(c => c.isControlTypeInferred)) { b.r.isInferred = true; b.Note("control_type_inferred"); }
        if (fix.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; b.Note("load_inferred"); }
        var dFull = b.P(DiagnosisParameters.VsdFullLoadPct);
        var dLimit = b.P(DiagnosisParameters.OrderInvertPct);

        int nBoth = 0, nInvert = 0;
        for (var m = 0; m < ctx.n; m++)
        {
            if (!vsd.Any(c => c.IsRunning(m)) || !fix.Any(c => c.IsRunning(m))) continue;
            nBoth++;
            var isVsdFull = vsd.Any(c => c.LoadPctAt(m) is double d && d >= dFull);
            var isFixUnloaded = fix.Any(c => c.state[m] == MinuteState.Unloaded);
            if (isVsdFull && isFixUnloaded) nInvert++;
        }
        if (nBoth == 0)
        {
            b.Ev("s5_never_together");
            return b.Done(DiagnosisStatus.NotTriggered);
        }
        var dPct = nInvert * 100.0 / nBoth;
        b.Ev("s5_invert", A(("pct", dPct), ("hours", nBoth / 60.0)));
        if (dPct <= dLimit) return b.Done(DiagnosisStatus.NotTriggered);
        b.r.szNextStep = "vsd_as_trim";
        return b.Done(DiagnosisStatus.Triggered);
    }

    public static DiagnosisRuleResult S6(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("S6", ctx.p);
        b.r.szFormulaKey = "eq.diag.formula.S6";
        var list = ctx.comps.Where(c => c.HasLoadStates || c.state.Any(s => s == MinuteState.Off)).ToList();
        if (list.Count == 0 || !ctx.comps.Any(c => c.HasLoadStates))
        {
            b.Miss("kw_or_loaded");
            return b.Done(DiagnosisStatus.InsufficientData);
        }
        if (list.Any(c => c.isLoadInferred)) { b.P(DiagnosisParameters.UnloadKwRatio); b.r.isInferred = true; b.Note("load_inferred"); }

        double dCenter = 0, dLow = 0, dHigh = 0;
        var isHit = false;
        var hasKwh = false;
        foreach (var c in list)
        {
            var nRun = c.Count(MinuteState.Loaded) + c.Count(MinuteState.Unloaded);
            if (nRun < 60) continue;
            var nLd = c.Count(MinuteState.Loaded);
            if (nLd >= nRun * NeverLoadedRatio) continue;
            isHit = true;
            double? dKwh = null;
            if (c.kwOk)
            {
                var kws = new List<double>();
                for (var m = 0; m < ctx.n; m++)
                    if (c.IsRunning(m) && !double.IsNaN(c.kw![m])) kws.Add(c.kw[m]);
                kws.Sort();
                dKwh = kws.Sum() / 60.0;
                dCenter += dKwh.Value;
                dLow += kws.Count * (DiagnosisMath.PercentileSorted(kws, 25) ?? 0) / 60.0;
                dHigh += kws.Count * (DiagnosisMath.PercentileSorted(kws, 75) ?? 0) / 60.0;
                hasKwh = true;
            }
            b.Ev("s6_idle", A(("name", c.src.szName), ("hours", nRun / 60.0), ("loadedPct", nLd * 100.0 / nRun), ("kwh", dKwh)));
        }
        if (!isHit) return b.Done(DiagnosisStatus.NotTriggered);
        if (hasKwh) b.r.savings = ctx.Savings(new DiagnosisRange(Math.Min(dLow, dHigh), dCenter, Math.Max(dLow, dHigh)));
        b.r.szNextStep = "stop_standby";
        return b.Done(DiagnosisStatus.Triggered);
    }
}
