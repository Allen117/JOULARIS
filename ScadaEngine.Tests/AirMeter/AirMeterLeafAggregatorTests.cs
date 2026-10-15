using ScadaEngine.Engine.Services;

namespace ScadaEngine.Tests.AirMeter;

/// <summary>
/// 鎖住 AirMeterLeafAggregator 純邏輯：boundary 相減、MaxVolume 溢位歸零、UnitScale L→Nm³ 換算、
/// 邊界缺值三段語意（兩缺不寫 / 缺一邊 Q=0 / 兩有 Q=1）。
/// 對錯 = 空壓機能源申報的每一 Nm³ 產氣量（比能耗分母）都算錯。與氣表 GasMeterLeafAggregator 語意對稱。
/// </summary>
public class AirMeterLeafAggregatorTests
{
    private static readonly DateTime Hour = new(2026, 10, 15, 10, 0, 0);

    [Theory]
    [InlineData(100.0, 130.5, 30.5)]   // 正常累計
    [InlineData(0.0, 0.0, 0.0)]        // 空壓機停機無產氣
    public void 正常累計_結尾減開頭(double start, double end, double expected)
    {
        var (dDelta, isRolledOver) = AirMeterLeafAggregator.CalcDeltaWithRollover(start, end, 10000);
        Assert.Equal(expected, dDelta, 10);
        Assert.False(isRolledOver);
    }

    [Fact]
    public void 溢位歸零_有MaxVolume_套環繞公式()
    {
        // 9990 → 15，Max=10000：(10000 - 9990) + 15 = 25
        var (dDelta, isRolledOver) = AirMeterLeafAggregator.CalcDeltaWithRollover(9990, 15, 10000);
        Assert.Equal(25, dDelta, 10);
        Assert.True(isRolledOver);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void 倒退但無有效MaxVolume_delta視為0(double? maxVolume)
    {
        var (dDelta, isRolledOver) = AirMeterLeafAggregator.CalcDeltaWithRollover(500, 20, maxVolume);
        Assert.Equal(0, dDelta, 10);
        Assert.False(isRolledOver);
    }

    [Fact]
    public void 兩邊界都缺_回null不寫列()
    {
        Assert.Null(AirMeterLeafAggregator.ComputeFromBoundaries("A1-S1", Hour, null, null, 10000, 1.0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 只缺一邊_掉線transition_Q0Delta0(bool missingStart)
    {
        double? fStart = missingStart ? null : 100.0;
        double? fEnd = missingStart ? 100.0 : null;

        var model = AirMeterLeafAggregator.ComputeFromBoundaries("A1-S1", Hour, fStart, fEnd, 10000, 1.0);

        Assert.NotNull(model);
        Assert.Equal(0, model!.nQuality);
        Assert.Equal(0, model.dDeltaNm3, 10);
        Assert.Equal("A1-S1", model.szSID);
        Assert.Equal(Hour, model.dtHourStart);
    }

    [Fact]
    public void 兩邊都有_Q1_正常寫入()
    {
        var model = AirMeterLeafAggregator.ComputeFromBoundaries("A1-S1", Hour, 120.0, 123.5, 10000, 1.0);

        Assert.NotNull(model);
        Assert.Equal(1, model!.nQuality);
        Assert.Equal(3.5, model.dDeltaNm3, 10);
    }

    [Fact]
    public void UnitScale_溢位先以原始單位計算再換算()
    {
        // NL 系點位，Max=100000 NL：99000 → 500，delta 原始 = 1500 NL = 1.5 Nm³
        var model = AirMeterLeafAggregator.ComputeFromBoundaries("A1-S2", Hour, 99000.0, 500.0, 100000, 0.001);

        Assert.NotNull(model);
        Assert.Equal(1.5, model!.dDeltaNm3, 10);
        Assert.True(model.isRolledOver);
    }
}
