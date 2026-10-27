using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.EnergyReport;

/// <summary>
/// 鎖住 EnergyReportService.ResolveBucketEndpoints：bucket 邊界缺讀數時的 fallback 規則。
/// 電表期中上線（期初缺）/ 期末斷線（期末缺）的 bucket 要能以區間內首 / 末筆讀數補位並標 isPartial；
/// 兩邊界齊全時絕不能被 fallback 值蓋掉（回歸不變）；補位後仍缺任一邊 → 交由呼叫端標 stale。
/// </summary>
public class ResolveBucketEndpointsTests
{
    [Fact]
    public void 兩邊界齊全_原值不動且非partial()
    {
        var (s, e, partial) = EnergyReportService.ResolveBucketEndpoints(100, 150, 120, 140);
        Assert.Equal(100, s);
        Assert.Equal(150, e);
        Assert.False(partial);
    }

    [Fact]
    public void 期初缺_改用區間內首筆並標partial()
    {
        var (s, e, partial) = EnergyReportService.ResolveBucketEndpoints(null, 150, 120, 140);
        Assert.Equal(120, s);
        Assert.Equal(150, e);
        Assert.True(partial);
    }

    [Fact]
    public void 期末缺_改用區間內末筆並標partial()
    {
        var (s, e, partial) = EnergyReportService.ResolveBucketEndpoints(100, null, 120, 140);
        Assert.Equal(100, s);
        Assert.Equal(140, e);
        Assert.True(partial);
    }

    [Fact]
    public void 兩邊皆缺_區間內有讀數_首末筆相減並標partial()
    {
        var (s, e, partial) = EnergyReportService.ResolveBucketEndpoints(null, null, 120, 140);
        Assert.Equal(120, s);
        Assert.Equal(140, e);
        Assert.True(partial);
    }

    [Fact]
    public void 兩邊皆缺_區間內亦無讀數_回null交由呼叫端標stale()
    {
        var (s, e, partial) = EnergyReportService.ResolveBucketEndpoints(null, null, null, null);
        Assert.Null(s);
        Assert.Null(e);
        Assert.False(partial);
    }

    [Fact]
    public void 期初缺且區間內無讀數_回null()
    {
        var (s, e, partial) = EnergyReportService.ResolveBucketEndpoints(null, 150, null, null);
        Assert.Null(s);
        Assert.Null(e);
        Assert.False(partial);
    }
}
