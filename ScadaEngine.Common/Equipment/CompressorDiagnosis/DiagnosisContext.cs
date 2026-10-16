using Calc = ScadaEngine.Common.Equipment.EquipmentMetricCalculator;

namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>單一訊號的資料品質評估（trusted data）</summary>
public sealed class SignalQuality
{
    public string szSignal { get; set; } = string.Empty;
    public bool isBound { get; set; }
    public double dCoveragePct { get; set; }
    /// <summary>平線（長時間完全相同的非零值）分鐘占有效非零分鐘 %</summary>
    public double dFlatlinePct { get; set; }
    public int nSentinelCount { get; set; }
    /// <summary>狀態點與功率矛盾 %（RUN / LOADED 才有）</summary>
    public double dConflictPct { get; set; }
    public int nLongestGapMinutes { get; set; }
    public int nGapCount { get; set; }
    public bool isTrusted { get; set; }
}

/// <summary>機台每分鐘狀態</summary>
public enum MinuteState : byte
{
    Unknown = 0,
    Off = 1,
    Unloaded = 2,
    Loaded = 3,
    /// <summary>運轉中但加卸載無法判定（無 LOADED 也無 kW）</summary>
    RunningUnknownLoad = 4,
}

/// <summary>單台機前處理結果（清洗後序列 + 品質 + 逐分鐘狀態）</summary>
public sealed class CompressorState
{
    public CompressorSeries src { get; init; } = new();
    public double[]? kw;
    public double[]? flow;
    public double[]? press;
    public double[]? loaded;
    public double[]? run;
    public SignalQuality qKw = new();
    public SignalQuality qFlow = new();
    public SignalQuality qPress = new();
    public SignalQuality qLoaded = new();
    public SignalQuality qRun = new();
    public MinuteState[] state = [];
    public bool isLoadInferred;
    /// <summary>判定加卸載用的額定 kW：銘牌值，未填 → 觀測最大值</summary>
    public double? dRatedKwEff;
    public bool isRatedKwObserved;
    public string? szControlType;
    public bool isControlTypeInferred;
    /// <summary>啟動（停機 → 運轉）發生的分鐘 index</summary>
    public List<int> aStarts = [];
    /// <summary>每個運轉小時（≥ 30 運轉分鐘）的加載循環次數</summary>
    public List<double> aCyclesPerHour = [];
    /// <summary>完整加載段最短分鐘數（無 → null）</summary>
    public int? nMinLoadedSegment;

    public bool kwOk => kw != null && qKw.isTrusted;
    public bool flowOk => flow != null && qFlow.isTrusted;
    public bool pressOk => press != null && qPress.isTrusted;

    public bool IsRunning(int m) => state[m] is MinuteState.Unloaded or MinuteState.Loaded or MinuteState.RunningUnknownLoad;

    /// <summary>可判定加卸載（有可信 LOADED 或可信 kW）且期間有運轉</summary>
    public bool HasLoadStates => state.Any(s => s is MinuteState.Loaded or MinuteState.Unloaded);

    public int Count(MinuteState s) => state.Count(x => x == s);

    /// <summary>kW ÷ 判定用額定 × 100；無 → null</summary>
    public double? LoadPctAt(int m)
    {
        if (!kwOk || dRatedKwEff is not > 0 || double.IsNaN(kw![m])) return null;
        return kw[m] / dRatedKwEff.Value * 100.0;
    }
}

/// <summary>規則結果建構器：記錄用到的參數，完成時標出未確認預設值</summary>
public sealed class RuleBuilder
{
    private readonly DiagnosisParameters _p;
    public DiagnosisRuleResult r { get; }

    public RuleBuilder(string szId, DiagnosisParameters p)
    {
        _p = p;
        r = new DiagnosisRuleResult { szId = szId };
    }

    public double P(string szKey)
    {
        if (!r.aParamsUsed.Contains(szKey)) r.aParamsUsed.Add(szKey);
        return _p[szKey];
    }

    public RuleBuilder Ev(string szKey, Dictionary<string, object?>? args = null) { r.aEvidence.Add(new DiagnosisText("eq.diag.ev." + szKey, args)); return this; }
    public RuleBuilder Note(string szKey, Dictionary<string, object?>? args = null) { r.aNotes.Add(new DiagnosisText("eq.diag.note." + szKey, args)); return this; }
    public RuleBuilder Miss(string szKey, Dictionary<string, object?>? args = null) { r.aMissing.Add(new DiagnosisText("eq.diag.miss." + szKey, args)); return this; }

    public DiagnosisRuleResult Done(DiagnosisStatus status)
    {
        r.status = status;
        r.aUnconfirmedDefaults = r.aParamsUsed.Where(_p.IsUnconfirmedDefault).ToList();
        return r;
    }
}

/// <summary>
/// 診斷共用上下文：前處理全部機台 + 系統點位、期間能耗 / 比功率分佈、年化與電價。
/// 由 <see cref="CompressorDiagnosisEngine"/> 建立後交給各規則（規則間不互相呼叫 DB，只讀這裡）。
/// </summary>
public sealed class DiagnosisContext
{
    public CompressorDiagnosisInput input { get; }
    public DiagnosisParameters p => input.parameters;
    public int n { get; }
    public List<CompressorState> comps { get; } = [];

    public double[]? supply;
    public double[]? use;
    public double[]? sysFlow;
    public double[]? dewpoint;
    public SignalQuality qSupply = new() { szSignal = CompressorSystemRoles.SupplyPress };
    public SignalQuality qUse = new() { szSignal = CompressorSystemRoles.UsePress };
    public SignalQuality qSysFlow = new() { szSignal = CompressorSystemRoles.Flow };
    public SignalQuality qDewpoint = new() { szSignal = CompressorSystemRoles.Dewpoint };

    /// <summary>任一台運轉（狀態全未知時 = 全 true，不排除任何分鐘）</summary>
    public bool[] anyRunning = [];
    public bool hasAnyState;

    public double dPeriodKwh;
    /// <summary>期間比功率（能量加權）</summary>
    public double? dPeriodSp;
    /// <summary>各運轉小時全場比功率（已排序）— L 規則換算 kWh 用 P25 / P50 / P75</summary>
    public List<double> aHourlySp = [];
    public double dDays;
    public double dAnnualFactor;

    public DiagnosisContext(CompressorDiagnosisInput input)
    {
        this.input = input;
        n = input.nMinutes;
        dDays = n / 1440.0;
        dAnnualFactor = dDays > 0 ? 365.0 / dDays : 0;

        foreach (var c in input.aCompressors) comps.Add(BuildCompressor(c, n, p));

        supply = Clean(input.system.aSupplyPress, n, false, p, false, qSupply);
        use = Clean(input.system.aUsePress, n, false, p, false, qUse);
        sysFlow = Clean(input.system.aFlow, n, false, p, false, qSysFlow);
        dewpoint = Clean(input.system.aDewpoint, n, true, p, false, qDewpoint);

        hasAnyState = comps.Any(c => c.state.Any(s => s != MinuteState.Unknown));
        anyRunning = new bool[n];
        for (var m = 0; m < n; m++)
            anyRunning[m] = !hasAnyState || comps.Any(c => c.IsRunning(m));

        BuildEnergy();
    }

    public DateTime TimeAt(int m) => input.dtFrom.AddMinutes(m);

    /// <summary>期間 kWh → 節能（年化 + NTD）</summary>
    public DiagnosisSavings Savings(DiagnosisRange kwhPeriod)
    {
        var k = new DiagnosisRange(Math.Max(0, kwhPeriod.dLow), Math.Max(0, kwhPeriod.dCenter), Math.Max(0, kwhPeriod.dHigh));
        var year = k.Scale(dAnnualFactor);
        return new DiagnosisSavings
        {
            kwhPeriod = RoundRange(k, 1),
            kwhYear = RoundRange(year, 0),
            ntdYear = input.dPrice is > 0 ? RoundRange(year.Scale(input.dPrice.Value), 0) : null,
        };
    }

    public static DiagnosisRange RoundRange(DiagnosisRange r, int nDigits) =>
        new(DiagnosisMath.Round(r.dLow, nDigits), DiagnosisMath.Round(r.dCenter, nDigits), DiagnosisMath.Round(r.dHigh, nDigits));

    /// <summary>供氣壓力來源：系統總管（可信）→ 運轉中各機排氣壓力最高者（降級）→ null</summary>
    public (double[]? series, bool isFromDischarge) SupplyPressure()
    {
        if (supply != null && qSupply.isTrusted) return (supply, false);
        var withPress = comps.Where(c => c.pressOk).ToList();
        if (withPress.Count == 0) return (null, false);
        var arr = new double[n];
        for (var m = 0; m < n; m++)
        {
            var dMax = double.NaN;
            foreach (var c in withPress)
            {
                // 狀態已知時只取運轉中機台（停機機台排氣壓力 = 管內殘壓，不代表供氣）
                if (hasAnyState && !c.IsRunning(m)) continue;
                var v = c.press![m];
                if (!double.IsNaN(v) && (double.IsNaN(dMax) || v > dMax)) dMax = v;
            }
            arr[m] = dMax;
        }
        return (arr, true);
    }

    // ─────────────────────────── 前處理 ───────────────────────────

    /// <summary>
    /// 清洗 + 品質評估：哨兵值與負值（&lt; −0.5）移除、−0.5..0 視為 0；覆蓋率、平線、缺口。
    /// isStatus = 狀態點（不檢查平線 — 長時間維持運轉是正常的）。
    /// </summary>
    public static double[]? Clean(double[]? raw, int n, bool isAllowNegative, DiagnosisParameters p, bool isStatus, SignalQuality q)
    {
        q.isBound = raw != null;
        if (raw == null) return null;
        var arr = new double[n];
        int nValid = 0, nSentinel = 0;
        for (var m = 0; m < n; m++)
        {
            var v = m < raw.Length ? raw[m] : double.NaN;
            if (double.IsNaN(v) || double.IsInfinity(v)) { arr[m] = double.NaN; continue; }
            if (DiagnosisMath.IsSentinel(v) || (!isAllowNegative && v < -0.5)) { arr[m] = double.NaN; nSentinel++; continue; }
            if (!isAllowNegative && v < 0) v = 0;
            arr[m] = v;
            nValid++;
        }
        q.nSentinelCount = nSentinel;
        q.dCoveragePct = n > 0 ? DiagnosisMath.Round(nValid * 100.0 / n, 1) : 0;

        // 缺口
        int nRun = 0, nLongest = 0, nGaps = 0;
        var nMaxGap = (int)p[DiagnosisParameters.MaxGapMinutes];
        for (var m = 0; m <= n; m++)
        {
            if (m < n && double.IsNaN(arr[m])) { nRun++; continue; }
            if (nRun > 0)
            {
                nLongest = Math.Max(nLongest, nRun);
                if (nRun > nMaxGap) nGaps++;
            }
            nRun = 0;
        }
        q.nLongestGapMinutes = nLongest;
        q.nGapCount = nGaps;

        // 平線：完全相同的非零值連續 ≥ FlatlineMinutes
        if (!isStatus)
        {
            var nFlatMin = (int)p[DiagnosisParameters.FlatlineMinutes];
            int nNonZero = 0, nFlat = 0, nSeg = 0;
            var dPrev = double.NaN;
            for (var m = 0; m <= n; m++)
            {
                var v = m < n ? arr[m] : double.NaN;
                var isCandidate = !double.IsNaN(v) && v != 0;
                if (isCandidate) nNonZero++;
                if (isCandidate && v == dPrev) { nSeg++; }
                else
                {
                    if (nSeg >= nFlatMin) nFlat += nSeg;
                    nSeg = isCandidate ? 1 : 0;
                }
                dPrev = isCandidate ? v : double.NaN;
            }
            q.dFlatlinePct = nNonZero > 0 ? DiagnosisMath.Round(nFlat * 100.0 / nNonZero, 1) : 0;
        }

        q.isTrusted = q.dCoveragePct >= p[DiagnosisParameters.MinCoveragePct]
            && q.dFlatlinePct <= p[DiagnosisParameters.FlatlineMaxPct];
        return arr;
    }

    public static CompressorState BuildCompressor(CompressorSeries src, int n, DiagnosisParameters p)
    {
        var s = new CompressorState
        {
            src = src,
            qKw = new SignalQuality { szSignal = EquipmentRoles.KW },
            qFlow = new SignalQuality { szSignal = EquipmentRoles.AIRFLOW },
            qPress = new SignalQuality { szSignal = EquipmentRoles.DISCHPRESS },
            qLoaded = new SignalQuality { szSignal = EquipmentRoles.LOADED },
            qRun = new SignalQuality { szSignal = EquipmentRoles.RUN },
        };
        s.kw = Clean(src.aKw, n, false, p, false, s.qKw);
        s.flow = Clean(src.aFlow, n, false, p, false, s.qFlow);
        s.press = Clean(src.aPress, n, false, p, false, s.qPress);
        s.loaded = Clean(src.aLoaded, n, false, p, true, s.qLoaded);
        s.run = Clean(src.aRun, n, false, p, true, s.qRun);

        var dThreshold = Calc.RunningThresholdKw(src.dRatedKw);
        var dConflictMax = p[DiagnosisParameters.StateConflictMaxPct];

        // 狀態點 vs 功率矛盾：RUN=0 但有功率 / RUN=1 但無功率；LOADED=1 但無功率
        if (s.kwOk)
        {
            if (s.run != null)
            {
                int nBoth = 0, nBad = 0;
                for (var m = 0; m < n; m++)
                {
                    if (double.IsNaN(s.run[m]) || double.IsNaN(s.kw![m])) continue;
                    nBoth++;
                    var isOn = s.run[m] >= 0.5;
                    if (isOn != (s.kw[m] > dThreshold)) nBad++;
                }
                s.qRun.dConflictPct = nBoth > 0 ? DiagnosisMath.Round(nBad * 100.0 / nBoth, 1) : 0;
                if (s.qRun.dConflictPct > dConflictMax) s.qRun.isTrusted = false;
            }
            if (s.loaded != null)
            {
                int nLoaded = 0, nBad = 0;
                for (var m = 0; m < n; m++)
                {
                    if (double.IsNaN(s.loaded[m]) || double.IsNaN(s.kw![m]) || s.loaded[m] < 0.5) continue;
                    nLoaded++;
                    if (s.kw[m] <= dThreshold) nBad++;
                }
                s.qLoaded.dConflictPct = nLoaded > 0 ? DiagnosisMath.Round(nBad * 100.0 / nLoaded, 1) : 0;
                if (s.qLoaded.dConflictPct > dConflictMax) s.qLoaded.isTrusted = false;
            }
        }

        // 判定用額定 kW：銘牌，未填 → 觀測最大值（SKILL：無額定值時用觀測最大值）
        if (src.dRatedKw is > 0) s.dRatedKwEff = src.dRatedKw;
        else if (s.kwOk)
        {
            var dMax = s.kw!.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            if (dMax > 0) { s.dRatedKwEff = dMax; s.isRatedKwObserved = true; }
        }

        // 逐分鐘狀態
        var isRunOk = s.run != null && s.qRun.isTrusted;
        var isLoadedOk = s.loaded != null && s.qLoaded.isTrusted;
        var dUnloadRatio = p[DiagnosisParameters.UnloadKwRatio];
        s.isLoadInferred = !isLoadedOk;
        s.state = new MinuteState[n];
        for (var m = 0; m < n; m++)
        {
            bool? running = null;
            if (isRunOk && !double.IsNaN(s.run![m])) running = s.run[m] >= 0.5;
            else if (s.kwOk && !double.IsNaN(s.kw![m])) running = s.kw[m] > dThreshold;

            if (running == null) { s.state[m] = MinuteState.Unknown; continue; }
            if (running == false) { s.state[m] = MinuteState.Off; continue; }

            if (isLoadedOk && !double.IsNaN(s.loaded![m]))
                s.state[m] = s.loaded[m] >= 0.5 ? MinuteState.Loaded : MinuteState.Unloaded;
            else if (s.kwOk && !double.IsNaN(s.kw![m]) && s.dRatedKwEff is > 0)
                s.state[m] = s.kw[m] >= s.dRatedKwEff.Value * dUnloadRatio ? MinuteState.Loaded : MinuteState.Unloaded;
            else
                s.state[m] = MinuteState.RunningUnknownLoad;
        }

        // 控制型式：欄位優先；未填 → 由運轉中 kW 分佈推定（雙峰 = 定頻、連續中間值 = VSD）
        if (CompressorControlType.IsValid(src.szControlType))
        {
            s.szControlType = src.szControlType;
        }
        else if (s.kwOk && s.dRatedKwEff is > 0)
        {
            int nRunning = 0, nMid = 0;
            for (var m = 0; m < n; m++)
            {
                if (!s.IsRunning(m) || double.IsNaN(s.kw![m])) continue;
                nRunning++;
                var r = s.kw[m] / s.dRatedKwEff.Value;
                if (r > 0.45 && r < 0.85) nMid++;
            }
            if (nRunning >= 60)
            {
                s.szControlType = nMid < nRunning * 0.2 ? CompressorControlType.Fixed : CompressorControlType.Vsd;
                s.isControlTypeInferred = true;
            }
        }

        // 啟動、加載循環、最短加載段
        var hourRunning = new Dictionary<int, int>();
        var hourCycles = new Dictionary<int, int>();
        var nSegStart = -1;
        int? nMinSeg = null;
        for (var m = 0; m < n; m++)
        {
            var st = s.state[m];
            var prev = m > 0 ? s.state[m - 1] : MinuteState.Unknown;
            if (s.IsRunning(m) && prev == MinuteState.Off) s.aStarts.Add(m);

            var nHour = m / 60;
            if (s.IsRunning(m)) hourRunning[nHour] = hourRunning.GetValueOrDefault(nHour) + 1;
            if (st == MinuteState.Loaded && prev == MinuteState.Unloaded)
                hourCycles[nHour] = hourCycles.GetValueOrDefault(nHour) + 1;

            // 完整加載段：前後皆為已知的非加載狀態（卸載 / 停機）
            if (st == MinuteState.Loaded && prev != MinuteState.Loaded)
                nSegStart = prev is MinuteState.Unloaded or MinuteState.Off ? m : -1;
            if (st != MinuteState.Loaded && prev == MinuteState.Loaded && nSegStart >= 0
                && st is MinuteState.Unloaded or MinuteState.Off)
            {
                var nLen = m - nSegStart;
                nMinSeg = nMinSeg == null ? nLen : Math.Min(nMinSeg.Value, nLen);
                nSegStart = -1;
            }
        }
        s.nMinLoadedSegment = nMinSeg;
        foreach (var (nHour, nRunMin) in hourRunning)
            if (nRunMin >= 30) s.aCyclesPerHour.Add(hourCycles.GetValueOrDefault(nHour));
        s.aCyclesPerHour.Sort();
        return s;
    }

    private void BuildEnergy()
    {
        var ids = comps.Select(c => c.src.nId).ToHashSet();
        var rows = input.aHourly.Where(r => ids.Contains(r.nProfileId)
            && r.dtHourStart >= input.dtFrom && r.dtHourStart < input.dtTo).ToList();
        dPeriodKwh = rows.Sum(r => r.dKwh);
        if (rows.Count == 0)
        {
            // 無 hourly（Engine 尚未聚合）→ 退回分鐘 kW 積分
            dPeriodKwh = comps.Where(c => c.kwOk).Sum(c => c.kw!.Where(v => !double.IsNaN(v)).Sum() / 60.0);
        }

        double dSumKwh = 0, dSumOut = 0;
        foreach (var g in rows.Where(r => r.nQuality == 1 && r.dOutputQty is > 0).GroupBy(r => r.dtHourStart))
        {
            var dKwh = g.Sum(r => r.dKwh);
            var dOut = g.Sum(r => r.dOutputQty!.Value);
            dSumKwh += dKwh;
            dSumOut += dOut;
            if (dOut > 0 && dKwh > 0) aHourlySp.Add(60.0 * dKwh / dOut);
        }
        aHourlySp.Sort();
        dPeriodSp = dSumOut > 0 ? 60.0 * dSumKwh / dSumOut : null;
    }
}
