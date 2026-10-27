using ScadaEngine.Common.Data.Models;
using ScadaEngine.Web.Services.HistoryStatistics;

namespace ScadaEngine.Tests.History;

/// <summary>
/// 鎖住歷史統計（HistoryStatisticsCalculator）— 趨勢頁統計卡與匯出「統計」分頁共用。
/// 只算 Good；累計值差異值採分段相加，處理歸零 / 換表、單筆突波、小幅抖動。
/// </summary>
public class HistoryStatisticsCalculatorTests
{
    private static readonly DateTime _dtStart = new(2026, 10, 1, 0, 0, 0);

    /// <summary>每分鐘一筆；null = Bad（Value 帶 999 模擬斷線舊值）</summary>
    private static List<HistoryDataModel> Series(params double?[] aValues)
        => aValues.Select((v, i) => new HistoryDataModel
        {
            szSID = "T-S1",
            fValue = v.HasValue ? (float)v.Value : 999f,
            nQuality = v.HasValue ? 1 : 0,
            dtTimestamp = _dtStart.AddMinutes(i)
        }).ToList();

    private static HistoryPointStatistics Calc(string szUnit, List<HistoryDataModel> data, int nMinutes)
        => HistoryStatisticsCalculator.Calculate("點位", szUnit, data,
            _dtStart, _dtStart.AddMinutes(nMinutes), nIntervalMinutes: 1, dJitterTolerancePct: 1);

    [Fact]
    public void 統計只算Good()
    {
        var s = Calc("°C", Series(10, null, 20, null), 3);
        Assert.Equal(4, s.nCount);
        Assert.Equal(2, s.nValidCount);
        Assert.Equal(10, s.dMin);
        Assert.Equal(20, s.dMax);
        Assert.Equal(15, s.dAvg);
        Assert.Null(s.dDiff);
    }

    [Fact]
    public void NaN與Infinity排除()
    {
        var data = Series(10, 20);
        data.Add(new HistoryDataModel { fValue = float.NaN, nQuality = 1, dtTimestamp = _dtStart.AddMinutes(2) });
        data.Add(new HistoryDataModel { fValue = float.PositiveInfinity, nQuality = 1, dtTimestamp = _dtStart.AddMinutes(3) });
        var s = Calc("°C", data, 3);
        Assert.Equal(2, s.nValidCount);
        Assert.Equal(20, s.dMax);
    }

    [Fact]
    public void 標準差為母體標準差()
    {
        // 2,4,4,4,5,5,7,9 → 母體 σ = 2
        var s = Calc("°C", Series(2, 4, 4, 4, 5, 5, 7, 9), 7);
        Assert.Equal(2.0, s.dStdDev!.Value, 9);
    }

    [Fact]
    public void 整段Bad_數值皆空()
    {
        var s = Calc("kWh", Series(null, null, null), 2);
        Assert.True(s.isNoValidData);
        Assert.Null(s.dMin);
        Assert.Null(s.dAvg);
        Assert.Null(s.dStdDev);
        Assert.Null(s.dDiff);
        Assert.Equal(PointKind.Cumulative, s.kind);
    }

    [Fact]
    public void 差異值_歸零換表分段相加_起訖往內找()
    {
        // Bad,100,110,5,20,Bad → (110−100) + (20−5) = 25
        var s = Calc("kWh", Series(null, 100, 110, 5, 20, null), 5);
        Assert.Equal(25, s.dDiff!.Value, 9);
        Assert.Equal(1, s.nDropCount);
        Assert.Equal(0, s.nSpikeCount);
        // 起點 00:00、首筆有效 00:01 → 差 1 分鐘 = 1 個間隔，不算往內（需「超過」）
        Assert.False(s.isStartInset);
        Assert.False(s.isEndInset);
    }

    [Fact]
    public void 差異值_起訖超過一個間隔_標示往內找()
    {
        var s = Calc("kWh", Series(null, null, 100, 110, null, null), 5);
        Assert.True(s.isStartInset);
        Assert.True(s.isEndInset);
        Assert.Equal(10, s.dDiff!.Value, 9);
    }

    [Fact]
    public void 差異值_單筆突波剔除()
    {
        var r = HistoryStatisticsCalculator.CalculateDiff([100, 110, 99999, 120], 1);
        Assert.Equal(20, r.dDiff, 9);
        Assert.Equal(1, r.nSpikeCount);
        Assert.Equal(0, r.nDropCount);
    }

    [Fact]
    public void 差異值_小幅抖動不算下降()
    {
        var r = HistoryStatisticsCalculator.CalculateDiff([1000, 999.9, 1001], 1);
        Assert.Equal(1, r.dDiff, 9);
        Assert.Equal(0, r.nDropCount);
    }

    [Fact]
    public void 差異值_只有一筆為0()
    {
        var s = Calc("kWh", Series(null, 500, null), 2);
        Assert.Equal(0, s.dDiff);
    }

    [Fact]
    public void 差異值_非累計點位不算()
    {
        var s = Calc("kW", Series(1, 2, 3), 2);
        Assert.Equal(PointKind.Instant, s.kind);
        Assert.Null(s.dDiff);
    }
}
