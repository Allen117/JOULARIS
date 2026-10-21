using System.Globalization;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Web.Services.HistoryStatistics;

/// <summary>
/// 歷史資料統計 — 趨勢頁 /api/history/data 與 Excel 匯出共用的唯一實作。
/// 有效值 = Quality=Good（nQuality == 1）且為有限數；Bad 列的 Value 是斷線前的舊值，計入會扭曲統計。
/// </summary>
public static class HistoryStatisticsCalculator
{
    /// <summary>
    /// 計算單一點位統計（含分類與累計值差異值）。
    /// </summary>
    /// <param name="data">依時間遞增的歷史資料</param>
    /// <param name="dtStart">查詢起點（判斷差異值起點是否往內找）</param>
    /// <param name="dtEnd">查詢終點</param>
    /// <param name="nIntervalMinutes">查詢取樣間隔（分鐘），0 視為 1</param>
    /// <param name="dJitterTolerancePct">累計值抖動容差（%）</param>
    public static HistoryPointStatistics Calculate(
        string? szName, string? szUnit, IReadOnlyList<HistoryDataModel> data,
        DateTime dtStart, DateTime dtEnd, int nIntervalMinutes, double dJitterTolerancePct)
    {
        var valid = data.Where(IsValid).ToList();
        var values = valid.Select(d => ToDouble(d.fValue)).ToList();
        var cls = PointKindClassifier.Classify(szName, szUnit, values);

        if (valid.Count == 0)
        {
            return new HistoryPointStatistics
            {
                kind = cls.kind,
                szUnitGroup = cls.szUnitGroup,
                nCount = data.Count,
                nValidCount = 0
            };
        }

        var dAvg = values.Average();
        var dStdDev = Math.Sqrt(values.Average(v => (v - dAvg) * (v - dAvg)));

        var tsInterval = TimeSpan.FromMinutes(Math.Max(nIntervalMinutes, 1));
        var dtFirst = valid[0].dtTimestamp;
        var dtLast = valid[^1].dtTimestamp;

        CumulativeDiffResult? diff = cls.kind == PointKind.Cumulative
            ? CalculateDiff(values, dJitterTolerancePct)
            : null;

        return new HistoryPointStatistics
        {
            kind = cls.kind,
            szUnitGroup = cls.szUnitGroup,
            nCount = data.Count,
            nValidCount = valid.Count,
            dMin = values.Min(),
            dMax = values.Max(),
            dAvg = dAvg,
            dStdDev = dStdDev,
            dDiff = diff?.dDiff,
            nDropCount = diff?.nDropCount ?? 0,
            nSpikeCount = diff?.nSpikeCount ?? 0,
            isStartInset = dtFirst - dtStart > tsInterval,
            isEndInset = dtEnd - dtLast > tsInterval,
            dtFirstValid = dtFirst,
            dtLastValid = dtLast
        };
    }

    /// <summary>
    /// float → double 取最短十進位表示（21.3f → 21.3，而非 21.299999237…），
    /// 與前端 JSON 顯示一致；統計與匯出儲存格都走這支。
    /// </summary>
    public static double ToDouble(float f)
        => float.IsFinite(f) ? double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : f;

    /// <summary>有效值判定：Good 且為有限數（排除 NaN / ±Infinity）</summary>
    public static bool IsValid(HistoryDataModel d) => d.nQuality == 1 && float.IsFinite(d.fValue);

    /// <summary>
    /// 累計值差異值 = 各段（段末 − 段初）相加。依時間走過有效值：
    ///   突波：v[i] &gt; prev 且 v[i+1] &lt; v[i] 且 v[i+1] &gt;= prev → 丟掉 v[i]（只處理單筆）
    ///   抖動：v &lt; prev 且降幅 ≤ |prev| × 容差% → 丟掉 v，prev 不更新
    ///   下降：v &lt; prev 且超過容差 → 視為歸零 / 換表，結算本段、以 v 開新段
    /// 不用「最大 − 最小」：歸零 / 換表會算錯。
    /// </summary>
    public static CumulativeDiffResult CalculateDiff(IReadOnlyList<double> values, double dJitterTolerancePct)
    {
        if (values.Count < 2) return new CumulativeDiffResult(0, 0, 0);

        var dSegStart = values[0];
        var dPrev = values[0];
        var dDiff = 0.0;
        var nDrops = 0;
        var nSpikes = 0;

        for (var i = 1; i < values.Count; i++)
        {
            var v = values[i];

            if (v > dPrev && i + 1 < values.Count && values[i + 1] < v && values[i + 1] >= dPrev)
            {
                nSpikes++;
                continue;
            }

            if (v < dPrev)
            {
                if (dPrev - v <= Math.Abs(dPrev) * dJitterTolerancePct / 100.0)
                    continue;

                dDiff += dPrev - dSegStart;
                dSegStart = v;
                dPrev = v;
                nDrops++;
                continue;
            }

            dPrev = v;
        }

        dDiff += dPrev - dSegStart;
        return new CumulativeDiffResult(dDiff, nDrops, nSpikes);
    }
}
