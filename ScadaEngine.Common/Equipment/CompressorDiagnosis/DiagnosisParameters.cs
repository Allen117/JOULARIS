namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// 參數定義：鍵、預設值、合法範圍、出處（skill = compressor-analysis-skill 原文數字；system = 本系統經驗值，僅供篩檢排序；
/// data = 預設由資料推導、此值只是退回）、群組（dq / p / l / s / e）。
/// </summary>
public sealed record DiagnosisParamDef(string szKey, double dDefault, double dMin, double dMax, string szSource, string szGroup);

/// <summary>
/// 空壓系統診斷參數（全部可在設定頁覆寫）。
/// 「依預設值」旗標：規則用到的參數若既沒被覆寫、也沒被工程師按「確認」，結果上標「依預設值（未經現場確認）」—
/// 取代原 skill 由 LLM 在 checkpoint 向使用者確認的步驟。
/// </summary>
public sealed class DiagnosisParameters
{
    // ── 資料品質 ──
    public const string MinCoveragePct = "dq.min_coverage_pct";
    public const string FlatlineMinutes = "dq.flatline_minutes";
    public const string FlatlineMaxPct = "dq.flatline_max_pct";
    public const string StateConflictMaxPct = "dq.state_conflict_max_pct";
    public const string MaxGapMinutes = "dq.max_gap_minutes";
    // ── 壓力 ──
    public const string PressMarginRatio = "p.margin_ratio";
    public const string SavingPerBarLowPct = "p.saving_per_bar_low_pct";
    public const string SavingPerBarHighPct = "p.saving_per_bar_high_pct";
    public const string TrialStepBar = "p.trial_step_bar";
    public const string PipeLossReserveBar = "p.pipe_loss_reserve_bar";
    // ── 洩漏 / 閒置 ──
    public const string LeakWarnPct = "l.warn_pct";
    public const string LeakSignificantPct = "l.significant_pct";
    public const string MinIdleWindows = "l.min_windows";
    public const string MinWindowMinutes = "l.min_window_minutes";
    // ── 運轉與排序 ──
    public const string UnloadKwRatio = "s.unload_kw_ratio";
    public const string UnloadRatioWarnPct = "s.unload_ratio_warn_pct";
    public const string CyclesPerHourRef = "s.cycles_per_hour_ref";
    public const string MultiPartLoadPct = "s.multi_partload_pct";
    public const string OrderInvertPct = "s.order_invert_pct";
    public const string VsdFullLoadPct = "s.vsd_full_load_pct";
    // ── 設備適配 ──
    public const string DegradePct = "e.degrade_pct";
    public const string FlowBins = "e.flow_bins";
    public const string PressBinBar = "e.press_bin_bar";
    public const string MinHoursPerCell = "e.min_hours_per_cell";
    public const string OversizeLoadPct = "e.oversize_load_pct";
    public const string VsdMinLoadFractionPct = "e.vsd_minload_fraction_pct";
    public const string UndersizeMinMinutes = "e.undersize_min_minutes";

    /// <summary>S2 停機延遲候選（分鐘）— 全部列出、不預判</summary>
    public static readonly int[] ShutdownDelayCandidates = [5, 10, 15, 30];

    /// <summary>手動重算期間預設天數 / 上限；排程快照固定最近 7 天</summary>
    public const int DefaultPeriodDays = 30;
    public const int MaxPeriodDays = 92;
    public const int ScheduledPeriodDays = 7;

    public static readonly DiagnosisParamDef[] Definitions =
    [
        new(MinCoveragePct, 90, 50, 100, "system", "dq"),
        new(FlatlineMinutes, 240, 30, 1440, "system", "dq"),
        new(FlatlineMaxPct, 10, 0, 100, "system", "dq"),
        new(StateConflictMaxPct, 5, 0, 100, "system", "dq"),
        new(MaxGapMinutes, 60, 5, 1440, "system", "dq"),

        new(PressMarginRatio, 1.10, 1.0, 2.0, "skill", "p"),
        new(SavingPerBarLowPct, 5, 0, 20, "skill", "p"),
        new(SavingPerBarHighPct, 6, 0, 20, "skill", "p"),
        new(TrialStepBar, 0.2, 0.05, 2, "system", "p"),
        new(PipeLossReserveBar, 0.3, 0, 3, "system", "p"),

        new(LeakWarnPct, 10, 0, 100, "system", "l"),
        new(LeakSignificantPct, 20, 0, 100, "system", "l"),
        new(MinIdleWindows, 3, 1, 60, "system", "l"),
        new(MinWindowMinutes, 60, 10, 1440, "system", "l"),

        new(UnloadKwRatio, 0.20, 0.05, 0.6, "skill", "s"),
        new(UnloadRatioWarnPct, 20, 0, 100, "skill", "s"),
        new(CyclesPerHourRef, 10, 1, 120, "system", "s"),
        new(MultiPartLoadPct, 20, 0, 100, "system", "s"),
        new(OrderInvertPct, 10, 0, 100, "system", "s"),
        new(VsdFullLoadPct, 95, 50, 100, "system", "s"),

        new(DegradePct, 5, 0, 100, "system", "e"),
        new(FlowBins, 10, 2, 50, "system", "e"),
        new(PressBinBar, 0.5, 0.1, 5, "system", "e"),
        new(MinHoursPerCell, 10, 1, 500, "system", "e"),
        new(OversizeLoadPct, 50, 0, 100, "system", "e"),
        new(VsdMinLoadFractionPct, 30, 0, 100, "system", "e"),
        new(UndersizeMinMinutes, 30, 1, 10000, "system", "e"),
    ];

    private static readonly Dictionary<string, DiagnosisParamDef> DefByKey =
        Definitions.ToDictionary(d => d.szKey, StringComparer.Ordinal);

    public static bool IsKnown(string szKey) => DefByKey.ContainsKey(szKey);

    public static DiagnosisParamDef? DefOf(string szKey) => DefByKey.TryGetValue(szKey, out var d) ? d : null;

    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unconfirmed = new(StringComparer.Ordinal);

    private DiagnosisParameters() { }

    /// <summary>
    /// 覆寫值（超出範圍夾回、未知鍵忽略）+ 已確認鍵 → 參數組。
    /// 未覆寫且未確認的鍵 = 未經現場確認的預設值。
    /// </summary>
    public static DiagnosisParameters FromOverrides(IDictionary<string, double>? overrides, IEnumerable<string>? confirmed)
    {
        var p = new DiagnosisParameters();
        var confirmedSet = new HashSet<string>(confirmed ?? [], StringComparer.Ordinal);
        foreach (var d in Definitions)
        {
            if (overrides != null && overrides.TryGetValue(d.szKey, out var v) && !double.IsNaN(v) && !double.IsInfinity(v))
            {
                p._values[d.szKey] = Math.Clamp(v, d.dMin, d.dMax);
            }
            else
            {
                p._values[d.szKey] = d.dDefault;
                if (!confirmedSet.Contains(d.szKey)) p._unconfirmed.Add(d.szKey);
            }
        }
        return p;
    }

    public double this[string szKey] => _values.TryGetValue(szKey, out var v) ? v : throw new KeyNotFoundException(szKey);

    public bool IsUnconfirmedDefault(string szKey) => _unconfirmed.Contains(szKey);
}
