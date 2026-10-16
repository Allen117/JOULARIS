namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// 空壓系統規則式診斷 — 進入點（純函式，不碰 DB、不呼叫任何 LLM / 外部服務；同輸入必同輸出）。
///
/// 移植 compressor-analysis-skill 的「領域知識層」：
///   前置  D 資料品質（trusted-data）+ 實測基線（measured-baseline）
///   ①    P 壓力過高（pressure-optimization）
///   ②    L 閒置 / 洩漏（leakage-and-idle-loss）
///   ③    S1–S6 運轉與排序（compressor-operation-and-sequencing）
///   ④    E1–E4 設備適配（equipment-fit-and-lifecycle）
///   經濟  期間 kWh × 365 ÷ 天數 × 電價（economics.md；低、高各自年化，不重新組合）
/// LLM 工作流層（訪談、checkpoint、報告撰寫）不移植：判斷改由規則 + 設定頁場域事實，文字改由 i18n 模板代入數字。
/// 所有節能皆為「篩檢估計」，結論只到建議下一步（試驗 / 量測 / 維修），不宣稱已驗證節能。
/// </summary>
public static class CompressorDiagnosisEngine
{
    public static CompressorDiagnosisResult Run(CompressorDiagnosisInput input, DateTime dtRunAt)
    {
        var ctx = new DiagnosisContext(input);
        var result = new CompressorDiagnosisResult
        {
            dtFrom = input.dtFrom,
            dtTo = input.dtTo,
            dtRunAt = dtRunAt,
            dDays = DiagnosisMath.Round(ctx.dDays, 2),
            nCompressors = ctx.comps.Count,
            dPeriodKwh = DiagnosisMath.Round(ctx.dPeriodKwh, 1),
            dSpecificPower = DiagnosisMath.Round(ctx.dPeriodSp, 3),
            dPrice = DiagnosisMath.Round(input.dPrice, 4),
            szPriceSource = input.dPrice is > 0 ? input.szPriceSource : null,
            isBaselinePeriodMissing = input.dtBaselineFrom == null || input.dtBaselineTo == null,
        };

        result.aBaseline = ctx.comps.Select(c => BuildBaseline(ctx, c)).ToList();
        if (ctx.supply != null) result.supplyPress = DiagnosisMath.Stats(RunningValues(ctx, ctx.supply), ctx.n);
        if (ctx.use != null) result.usePress = DiagnosisMath.Stats(RunningValues(ctx, ctx.use), ctx.n);
        if (ctx.sysFlow != null) result.sysFlow = DiagnosisMath.Stats(ctx.sysFlow, ctx.n);

        result.aRules.Add(DataQualityRules.Evaluate(ctx));
        result.aRules.Add(PressureRule.Evaluate(ctx));
        result.aRules.Add(IdleLossRule.Evaluate(ctx));
        result.aRules.AddRange(SequencingRules.Evaluate(ctx));
        result.aRules.AddRange(EquipmentFitRules.Evaluate(ctx));

        bool Hit(string szId) => result.aRules.Any(r => r.szId == szId && r.status == DiagnosisStatus.Triggered);
        // 降壓後洩漏量隨之變小 → P 與 L 節能不可直接相加（portfolio-synthesis 重疊旗標）
        if (Hit("P") && Hit("L")) result.aOverlapFlags.Add("p_l_overlap");
        // 從不加載的機台，其卸載時間同時被 S2 停機延遲試算計入
        if (Hit("S2") && Hit("S6")) result.aOverlapFlags.Add("s2_s6_overlap");
        // L 以實測比功率換算非生產時段耗電，其中含卸載空轉；S2 / S6 也從同一段卸載時間估節能
        if (Hit("L") && (Hit("S2") || Hit("S6"))) result.aOverlapFlags.Add("l_s_overlap");

        result.aUnconfirmedDefaults = result.aRules.SelectMany(r => r.aUnconfirmedDefaults)
            .Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        return result;
    }

    private static IEnumerable<double> RunningValues(DiagnosisContext ctx, double[] arr)
    {
        for (var m = 0; m < ctx.n; m++)
            if (ctx.anyRunning[m]) yield return arr[m];
    }

    private static CompressorBaseline BuildBaseline(DiagnosisContext ctx, CompressorState c)
    {
        IEnumerable<double> Running(double[] arr)
        {
            for (var m = 0; m < ctx.n; m++)
                if (!ctx.hasAnyState || c.IsRunning(m)) yield return arr[m];
        }

        var n = Math.Max(1, ctx.n);
        var nLd = c.Count(MinuteState.Loaded);
        var nUn = c.Count(MinuteState.Unloaded);
        var nOff = c.Count(MinuteState.Off);
        var nRunUnknown = c.Count(MinuteState.RunningUnknownLoad);
        var dKwh = ctx.input.aHourly.Where(r => r.nProfileId == c.src.nId
            && r.dtHourStart >= ctx.input.dtFrom && r.dtHourStart < ctx.input.dtTo).Sum(r => r.dKwh);
        return new CompressorBaseline
        {
            nId = c.src.nId,
            szName = c.src.szName,
            szControlType = c.szControlType,
            isControlTypeInferred = c.isControlTypeInferred,
            isLoadInferred = c.isLoadInferred,
            isRatedKwObserved = c.isRatedKwObserved,
            kw = c.kw == null ? null : DiagnosisMath.Stats(Running(c.kw), ctx.n),
            flow = c.flow == null ? null : DiagnosisMath.Stats(Running(c.flow), ctx.n),
            press = c.press == null ? null : DiagnosisMath.Stats(Running(c.press), ctx.n),
            dLoadedPct = DiagnosisMath.Round(nLd * 100.0 / n, 1),
            dUnloadedPct = DiagnosisMath.Round(nUn * 100.0 / n, 1),
            dOffPct = DiagnosisMath.Round(nOff * 100.0 / n, 1),
            dUnknownPct = DiagnosisMath.Round((ctx.n - nLd - nUn - nOff) * 100.0 / n, 1),
            dRunHours = DiagnosisMath.Round((nLd + nUn + nRunUnknown) / 60.0, 1),
            nStarts = c.aStarts.Count,
            dCyclesP50 = DiagnosisMath.Round(DiagnosisMath.PercentileSorted(c.aCyclesPerHour, 50), 1),
            dCyclesP95 = DiagnosisMath.Round(DiagnosisMath.PercentileSorted(c.aCyclesPerHour, 95), 1),
            dKwh = DiagnosisMath.Round(dKwh, 1),
        };
    }
}
