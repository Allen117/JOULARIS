namespace ScadaEngine.Common.Equipment;

/// <summary>
/// 設備效率指標純函式 — Engine（hourly 聚合）與 Web（即時卡 / 期間儀表板）共用同一組公式，
/// 避免「現在 0.62 但本小時平均 0.70」因兩邊公式不同而被當 bug。
///
/// 標準出處：
///   冰機 — ASHRAE 90.1 / AHRI 550/590：主指標 kW/RT；COP = 3.517 ÷ (kW/RT)、EER = 12 ÷ (kW/RT)；
///          期間效率 = ΣkWh ÷ ΣRT·h（能量加權，IPLV / ASHRAE Guideline 22 精神，不是瞬時比值平均）。
///   空壓機 — ISO 11011 / CAGI：比功率 kW/(m³/min)；kW/100cfm = 比功率 × 2.8317（100 cfm = 2.8317 m³/min）。
///
/// 除零 / null 一律回 null，不丟例外。
/// </summary>
public static class EquipmentMetricCalculator
{
    /// <summary>EER（Btu/h per W）= 12 ÷ (kW/RT)</summary>
    public const double EerFactor = 12.0;

    /// <summary>100 cfm 換算 m³/min</summary>
    public const double M3MinPer100Cfm = 2.8316846592;

    /// <summary>水比熱預設值 kJ/(kg·K)（乙二醇系統請於設定頁調整）</summary>
    public const double DefaultSpecificHeat = 4.186;

    /// <summary>停機判定：kW 低於額定 kW 的此比例視為停機</summary>
    public const double RunningKwRatio = 0.05;

    /// <summary>未填額定 kW 時的停機判定門檻（kW）— 避免待機雜訊被當運轉</summary>
    public const double RunningKwFallback = 1.0;

    /// <summary>負載率分 bin 上界（%）：≤25 / ≤50 / ≤75 / &gt;75，對應 AHRI 550/590 IPLV 四點（25/50/75/100%）</summary>
    public static readonly double[] LoadBinUpper = [25, 50, 75];

    public const int LoadBinCount = 4;

    // ─────────────────────────── 基本公式 ───────────────────────────

    /// <summary>
    /// 冰水流量 + 進出水溫 → 冷量 RT。
    /// RT = (L/min ÷ 60) × ΔT × 比熱 ÷ 3.517（水密度取 1 kg/L）；ΔT = 回水 − 出水。
    /// 例：1,000 L/min、ΔT 5 °C → 99.18 RT（ΔT 1 °C → 19.84 RT）。
    /// 流量 ≤ 0 或 ΔT ≤ 0 → 0（沒有冷量：泵停 / 停機時的溫差雜訊）；任一輸入 null / NaN → null。
    /// </summary>
    public static double? CoolingRtFromFlow(double? dFlowLpm, double? dChws, double? dChwr,
        double dSpecificHeat = DefaultSpecificHeat)
    {
        if (dFlowLpm == null || dChws == null || dChwr == null) return null;
        if (double.IsNaN(dFlowLpm.Value) || double.IsNaN(dChws.Value) || double.IsNaN(dChwr.Value)) return null;
        var dDeltaT = dChwr.Value - dChws.Value;
        if (dFlowLpm.Value <= 0 || dDeltaT <= 0) return 0;
        var dCp = dSpecificHeat > 0 ? dSpecificHeat : DefaultSpecificHeat;
        return dFlowLpm.Value / 60.0 * dDeltaT * dCp / EquipmentUnitScale.KwPerRt;
    }

    /// <summary>
    /// 直接讀取的 RT 點位 → 非負冷量。負值 → 0（蒸發器側不可能產生負冷量；負值 = 停機 / 泵停時的溫差雜訊或感測器偏差），
    /// 與 <see cref="CoolingRtFromFlow"/> 的 ΔT ≤ 0 → 0 對稱。null / NaN 原樣回傳（缺值語意不變）。
    /// 只套在「分鐘樣本」層，不套在小時平均或樹合併層（bucket 的 Sign 負值合法）。
    /// </summary>
    public static double? ClampRtPoint(double? dRt)
        => dRt == null || double.IsNaN(dRt.Value) ? dRt : Math.Max(0, dRt.Value);

    /// <summary>a ÷ b；b ≤ 0、任一 null 或 NaN → null</summary>
    public static double? SafeDiv(double? a, double? b)
    {
        if (a == null || b == null) return null;
        if (b.Value <= 0 || double.IsNaN(a.Value) || double.IsNaN(b.Value)) return null;
        return a.Value / b.Value;
    }

    private static double? Sub(double? a, double? b) => a == null || b == null ? null : a.Value - b.Value;

    /// <summary>kW/RT → COP（= 3.517 ÷ kW/RT）</summary>
    public static double? CopFromKwPerRt(double? dKwPerRt) => SafeDiv(EquipmentUnitScale.KwPerRt, dKwPerRt);

    /// <summary>kW/RT → EER（= 12 ÷ kW/RT）</summary>
    public static double? EerFromKwPerRt(double? dKwPerRt) => SafeDiv(EerFactor, dKwPerRt);

    /// <summary>比功率 kW/(m³/min) → kW/100cfm</summary>
    public static double? KwPer100Cfm(double? dSpecificPower) =>
        dSpecificPower == null ? null : dSpecificPower.Value * M3MinPer100Cfm;

    /// <summary>停機判定門檻（kW）：額定 × 5%，未填額定用 1 kW</summary>
    public static double RunningThresholdKw(double? dRatedKw) =>
        dRatedKw is > 0 ? dRatedKw.Value * RunningKwRatio : RunningKwFallback;

    /// <summary>運轉判定：有 RUN 值 → RUN ≥ 0.5；否則 kW &gt; 門檻；兩者皆無 → null（未知）</summary>
    public static bool? IsRunning(double? dRun, double? dKw, double? dRatedKw)
    {
        if (dRun != null) return dRun.Value >= 0.5;
        if (dKw != null) return dKw.Value > RunningThresholdKw(dRatedKw);
        return null;
    }

    /// <summary>
    /// 評級：null → NoData；≤ Good → Good；≤ Fair → Fair；否則 Poor（主指標越低越好）。
    /// 門檻任一缺 → 有值一律 Fair（不亂給優 / 差）。
    /// </summary>
    public static EquipmentGrade Grade(double? dValue, double? dGood, double? dFair)
    {
        if (dValue == null || double.IsNaN(dValue.Value)) return EquipmentGrade.NoData;
        if (dGood == null || dFair == null) return EquipmentGrade.Fair;
        if (dValue.Value <= dGood.Value) return EquipmentGrade.Good;
        if (dValue.Value <= dFair.Value) return EquipmentGrade.Fair;
        return EquipmentGrade.Poor;
    }

    /// <summary>負載率 → bin index（0:≤25 / 1:≤50 / 2:≤75 / 3:&gt;75）；null / NaN → -1</summary>
    public static int LoadBinIndex(double? dLoadPct)
    {
        if (dLoadPct == null || double.IsNaN(dLoadPct.Value)) return -1;
        for (var i = 0; i < LoadBinUpper.Length; i++)
            if (dLoadPct.Value <= LoadBinUpper[i]) return i;
        return LoadBinCount - 1;
    }

    // ─────────────────────────── 即時 ───────────────────────────

    /// <summary>冰機即時指標。停機 → grade=Off、效率類指標 null（溫度類照算）。</summary>
    public static ChillerInstantResult ComputeChillerInstant(ChillerInstantInput i)
    {
        var running = IsRunning(i.dRun, i.dKw, i.dRatedKw);
        var dChwDt = Sub(i.dChwr, i.dChws);
        var dCdwDt = Sub(i.dCdwr, i.dCdws);
        // 蒸發器趨近 = 冰水出水 − 蒸發飽和溫度；冷凝器趨近 = 冷凝飽和溫度 − 冷卻水出水
        var dEvapApp = Sub(i.dChws, i.dEvapT);
        var dCondApp = Sub(i.dCondT, i.dCdwr);

        if (running == false)
        {
            return new ChillerInstantResult
            {
                isRunning = false,
                dChwDeltaT = dChwDt,
                dCdwDeltaT = dCdwDt,
                dEvapApproach = dEvapApp,
                dCondApproach = dCondApp,
                grade = EquipmentGrade.Off,
            };
        }

        var dKwPerRt = i.dKw is >= 0 ? SafeDiv(i.dKw, i.dRt) : null;
        var dLoad = i.dLoadPct ?? (SafeDiv(i.dRt, i.dRatedRt) is double r ? r * 100.0 : null);
        return new ChillerInstantResult
        {
            isRunning = running == true,
            dKwPerRt = dKwPerRt,
            dCop = CopFromKwPerRt(dKwPerRt),
            dEer = EerFromKwPerRt(dKwPerRt),
            dLoadPct = dLoad,
            dChwDeltaT = dChwDt,
            dCdwDeltaT = dCdwDt,
            dEvapApproach = dEvapApp,
            dCondApproach = dCondApp,
            grade = Grade(dKwPerRt, i.dGood, i.dFair),
        };
    }

    /// <summary>空壓機即時指標。停機 → grade=Off、比功率 null（壓力 / 溫度 / 流量照列）。</summary>
    public static CompressorInstantResult ComputeCompressorInstant(CompressorInstantInput i)
    {
        var running = IsRunning(i.dRun, i.dKw, i.dRatedKw);
        bool? isLoaded = i.dLoaded == null ? null : i.dLoaded.Value >= 0.5;

        if (running == false)
        {
            return new CompressorInstantResult
            {
                isRunning = false,
                isLoaded = isLoaded,
                dPressBar = i.dPressBar,
                dTempC = i.dTempC,
                dFlow = i.dFlow,
                grade = EquipmentGrade.Off,
            };
        }

        var dSp = i.dKw is >= 0 ? SafeDiv(i.dKw, i.dFlow) : null;
        var dLoad = i.dLoadPct ?? (SafeDiv(i.dKw, i.dRatedKw) is double r ? r * 100.0 : null);
        return new CompressorInstantResult
        {
            isRunning = running == true,
            dSpecificPower = dSp,
            dKwPer100Cfm = KwPer100Cfm(dSp),
            dLoadPct = dLoad,
            isLoaded = isLoaded,
            dPressBar = i.dPressBar,
            dTempC = i.dTempC,
            dFlow = i.dFlow,
            grade = Grade(dSp, i.dGood, i.dFair),
        };
    }

    // ─────────────────────────── 期間 ───────────────────────────

    /// <summary>
    /// 期間指標（能量加權）。冰機：kW/RT = ΣkWh ÷ ΣRT·h、COP、EER。
    /// 空壓機：比功率 = ΣkWh ÷ Σm³ × 60（kWh/m³ × 60 min/h = kW/(m³/min)）、kW/100cfm、比能耗 kWh/m³。
    /// </summary>
    public static EquipmentPeriodMetric ComputePeriod(string szKind, double dSumKwh, double dSumOutput, double dRunHours)
    {
        if (szKind == EquipmentKind.Compressor)
        {
            var dSpecificEnergy = SafeDiv(dSumKwh, dSumOutput);
            var dSp = dSpecificEnergy == null ? null : dSpecificEnergy * 60.0;
            return new EquipmentPeriodMetric
            {
                dKwh = dSumKwh,
                dOutput = dSumOutput,
                dRunHours = dRunHours,
                dPrimary = dSp,
                dSecondary = KwPer100Cfm(dSp),
                dTertiary = dSpecificEnergy,
            };
        }

        var dKwPerRt = SafeDiv(dSumKwh, dSumOutput);
        return new EquipmentPeriodMetric
        {
            dKwh = dSumKwh,
            dOutput = dSumOutput,
            dRunHours = dRunHours,
            dPrimary = dKwPerRt,
            dSecondary = CopFromKwPerRt(dKwPerRt),
            dTertiary = EerFromKwPerRt(dKwPerRt),
        };
    }
}
