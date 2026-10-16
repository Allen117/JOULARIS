namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// D 規則：資料品質（對應 skill trusted-data.md）。
/// 檢查覆蓋率、平線、哨兵值、狀態點與功率矛盾、長時間缺口。
/// 不可信的訊號在 <see cref="DiagnosisContext"/> 已被標 isTrusted=false，依賴它的規則會自動降級或標「資料不足」；
/// 本規則只負責把問題列出來（觸發 = 有任何問題）。未綁的點不算問題（由各規則點名缺什麼）。
/// </summary>
public static class DataQualityRules
{
    public static DiagnosisRuleResult Evaluate(DiagnosisContext ctx)
    {
        var b = new RuleBuilder("D", ctx.p);
        var dMinCov = b.P(DiagnosisParameters.MinCoveragePct);
        var dFlatMax = b.P(DiagnosisParameters.FlatlineMaxPct);
        b.P(DiagnosisParameters.FlatlineMinutes);
        var dConflictMax = b.P(DiagnosisParameters.StateConflictMaxPct);
        var nMaxGap = b.P(DiagnosisParameters.MaxGapMinutes);

        var nIssues = 0;
        void Check(string szName, SignalQuality q)
        {
            if (!q.isBound) return;
            if (q.dCoveragePct < dMinCov)
            {
                b.Ev("dq_coverage", A(("name", szName), ("signal", q.szSignal), ("pct", q.dCoveragePct), ("min", dMinCov)));
                nIssues++;
            }
            if (q.dFlatlinePct > dFlatMax)
            {
                b.Ev("dq_flatline", A(("name", szName), ("signal", q.szSignal), ("pct", q.dFlatlinePct)));
                nIssues++;
            }
            if (q.nSentinelCount > 0)
            {
                b.Ev("dq_sentinel", A(("name", szName), ("signal", q.szSignal), ("count", q.nSentinelCount)));
                nIssues++;
            }
            if (q.dConflictPct > dConflictMax)
            {
                b.Ev("dq_conflict", A(("name", szName), ("signal", q.szSignal), ("pct", q.dConflictPct)));
                nIssues++;
            }
            if (q.nGapCount > 0)
            {
                b.Ev("dq_gap", A(("name", szName), ("signal", q.szSignal), ("count", q.nGapCount),
                    ("longest", q.nLongestGapMinutes), ("max", nMaxGap)));
                nIssues++;
            }
        }

        foreach (var c in ctx.comps)
        {
            Check(c.src.szName, c.qKw);
            Check(c.src.szName, c.qFlow);
            Check(c.src.szName, c.qPress);
            Check(c.src.szName, c.qLoaded);
            Check(c.src.szName, c.qRun);
            if (c.state.All(s => s == MinuteState.Unknown))
            {
                b.Miss("state_signals", A(("name", c.src.szName)));
                nIssues++;
            }
        }
        const string SYS = "SYS";
        Check(SYS, ctx.qSupply);
        Check(SYS, ctx.qUse);
        Check(SYS, ctx.qSysFlow);
        Check(SYS, ctx.qDewpoint);

        b.r.szFormulaKey = "eq.diag.formula.D";
        if (nIssues == 0)
        {
            b.Ev("dq_ok");
            return b.Done(DiagnosisStatus.NotTriggered);
        }
        b.r.szNextStep = "fix_data";
        return b.Done(DiagnosisStatus.Triggered);
    }

    /// <summary>args 簡寫</summary>
    public static Dictionary<string, object?> A(params (string k, object? v)[] kv)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in kv) d[k] = v is double dv ? DiagnosisMath.Round(dv, 3) : v;
        return d;
    }
}
