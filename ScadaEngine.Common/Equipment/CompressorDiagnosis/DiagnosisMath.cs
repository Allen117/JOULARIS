namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>診斷用統計工具（純函式，決定性）</summary>
public static class DiagnosisMath
{
    /// <summary>已排序序列的百分位（線性內插，p = 0..100）；空 → null</summary>
    public static double? PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        if (sorted.Count == 1) return sorted[0];
        var dPos = Math.Clamp(p, 0, 100) / 100.0 * (sorted.Count - 1);
        var nLo = (int)Math.Floor(dPos);
        var nHi = Math.Min(nLo + 1, sorted.Count - 1);
        return sorted[nLo] + (sorted[nHi] - sorted[nLo]) * (dPos - nLo);
    }

    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var list = values.Where(v => !double.IsNaN(v)).ToList();
        list.Sort();
        return PercentileSorted(list, p);
    }

    public static double? Median(IEnumerable<double> values) => Percentile(values, 50);

    /// <summary>訊號統計；nTotal = 期間分鐘數（覆蓋率分母）</summary>
    public static SignalStats Stats(IEnumerable<double> values, int nTotal)
    {
        var list = values.Where(v => !double.IsNaN(v)).ToList();
        list.Sort();
        if (list.Count == 0) return new SignalStats { nCount = 0, dCoveragePct = 0 };
        return new SignalStats
        {
            nCount = list.Count,
            dCoveragePct = nTotal > 0 ? Round(list.Count * 100.0 / nTotal, 1) : 0,
            dMin = Round(list[0], 3),
            dMax = Round(list[^1], 3),
            dMean = Round(list.Average(), 3),
            dP05 = Round(PercentileSorted(list, 5)!.Value, 3),
            dP50 = Round(PercentileSorted(list, 50)!.Value, 3),
            dP95 = Round(PercentileSorted(list, 95)!.Value, 3),
        };
    }

    /// <summary>
    /// 多窗口區間：中心 = 中位數；窗口 &lt; 10 → 最小 ~ 最大；≥ 10 → P10 ~ P90（排除單一極端窗口）。
    /// 空 → null。
    /// </summary>
    public static DiagnosisRange? WindowRange(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return null;
        var list = values.OrderBy(v => v).ToList();
        var dCenter = PercentileSorted(list, 50)!.Value;
        return list.Count < 10
            ? new DiagnosisRange(list[0], dCenter, list[^1])
            : new DiagnosisRange(PercentileSorted(list, 10)!.Value, dCenter, PercentileSorted(list, 90)!.Value);
    }

    /// <summary>樣本變異數（n−1）；n &lt; 2 → 0</summary>
    public static double SampleVariance(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var dMean = values.Average();
        return values.Sum(v => (v - dMean) * (v - dMean)) / (values.Count - 1);
    }

    public static double Round(double v, int nDigits) => Math.Round(v, nDigits, MidpointRounding.AwayFromZero);

    public static double? Round(double? v, int nDigits) => v == null ? null : Round(v.Value, nDigits);

    /// <summary>值是否為現場常見哨兵值（通訊失敗 / 未初始化填值）</summary>
    public static bool IsSentinel(double v) =>
        v is -9999 or 9999 or -999 or 99999 or -99999 or 65535 or 32767 or -32768 or 2147483647 or -2147483648;
}
