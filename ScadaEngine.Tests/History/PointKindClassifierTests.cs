using ScadaEngine.Web.Services.HistoryStatistics;

namespace ScadaEngine.Tests.History;

/// <summary>
/// 鎖住歷史趨勢點位分類（PointKindClassifier）— 決定差異值算不算、匯出畫哪種圖。
/// 重點：單位完全比對，複合單位（m3/h）與名稱子字串（North、AHM3）不可誤判為累計值。
/// </summary>
public class PointKindClassifierTests
{
    private static readonly double[] _aAnalog = [21.5, 22.3, 23.1];

    [Theory]
    [InlineData("kWh", "kWh")]
    [InlineData(" KWH ", "kWh")]
    [InlineData("度", "kWh")]
    [InlineData("用電度數", "kWh")]
    [InlineData("MWh", "MWh")]
    [InlineData("RTh", "RTh")]
    [InlineData("m3", "m³")]
    [InlineData("m³", "m³")]
    [InlineData("Nm3", "Nm³")]
    public void 累計單位_歸到對應組(string szUnit, string szGroup)
    {
        var r = PointKindClassifier.Classify("電表A", szUnit, _aAnalog);
        Assert.Equal(PointKind.Cumulative, r.kind);
        Assert.Equal(szGroup, r.szUnitGroup);
    }

    [Theory]
    [InlineData("m3/h")]
    [InlineData("kWh/m3")]
    [InlineData("kW")]
    [InlineData("°C")]
    public void 複合或瞬時單位_為即時值(string szUnit)
    {
        var r = PointKindClassifier.Classify("流量計", szUnit, _aAnalog);
        Assert.Equal(PointKind.Instant, r.kind);
        Assert.Equal(szUnit, r.szUnitGroup);
    }

    [Theory]
    [InlineData("今日用電 kWh")]
    [InlineData("本月用電")]
    [InlineData("Energy Today")]
    [InlineData("Last Month kWh")]
    public void 名稱含期間字樣_為期間值(string szName)
    {
        var r = PointKindClassifier.Classify(szName, "kWh", _aAnalog);
        Assert.Equal(PointKind.Period, r.kind);
    }

    [Theory]
    [InlineData("North AHU 溫度")]
    [InlineData("AHM3 出風溫度")]
    [InlineData("流量 m3/h")]
    public void 單位空_名稱子字串不誤判(string szName)
    {
        var r = PointKindClassifier.Classify(szName, "", _aAnalog);
        Assert.Equal(PointKind.Instant, r.kind);
        Assert.Equal("", r.szUnitGroup);
    }

    [Theory]
    [InlineData("總用電 kWh", "kWh")]
    [InlineData("冰水累計 RTh", "RTh")]
    [InlineData("空壓產氣 Nm3", "Nm³")]
    [InlineData("自來水(m3)", "m³")]
    public void 單位空_名稱含獨立累計字樣_為累計值(string szName, string szGroup)
    {
        var r = PointKindClassifier.Classify(szName, null, _aAnalog);
        Assert.Equal(PointKind.Cumulative, r.kind);
        Assert.Equal(szGroup, r.szUnitGroup);
    }

    [Fact]
    public void 有效值全為0或1_為狀態值()
    {
        var r = PointKindClassifier.Classify("泵浦運轉", "", [0, 1, 1, 0]);
        Assert.Equal(PointKind.Status, r.kind);
    }

    [Fact]
    public void 累計單位優先於狀態判定()
    {
        var r = PointKindClassifier.Classify("電表", "kWh", [0, 1]);
        Assert.Equal(PointKind.Cumulative, r.kind);
    }

    [Fact]
    public void 無有效值_不判為狀態值()
    {
        var r = PointKindClassifier.Classify("溫度", "°C", []);
        Assert.Equal(PointKind.Instant, r.kind);
    }
}
