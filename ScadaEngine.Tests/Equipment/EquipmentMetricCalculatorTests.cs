using ScadaEngine.Common.Equipment;
using Calc = ScadaEngine.Common.Equipment.EquipmentMetricCalculator;

namespace ScadaEngine.Tests.Equipment;

/// <summary>
/// 鎖住設備效率指標公式（ASHRAE 90.1 / AHRI 550/590 冰機、ISO 11011 / CAGI 空壓機）。
/// 對錯 = 冰機 / 空壓機診斷頁、EMS 卡片與 Engine hourly 聚合的每個效率數字都算錯，且肉眼難以察覺。
/// </summary>
public class EquipmentMetricCalculatorTests
{
    // ── 流量 + ΔT → RT ─────────────────────────────────────────

    [Theory]
    [InlineData(1000, 7, 12, 99.18)]   // 1,000 L/min、ΔT 5 °C
    [InlineData(1000, 7, 8, 19.84)]    // ΔT 1 °C
    [InlineData(3000, 6, 11.5, 327.3)] // 3,000 L/min、ΔT 5.5 °C
    public void 流量加溫差_換算冷凍噸(double flow, double chws, double chwr, double expected)
    {
        Assert.Equal(expected, Calc.CoolingRtFromFlow(flow, chws, chwr)!.Value, 1);
    }

    [Fact]
    public void 經驗法則對照_GPM24倍RT除以華氏溫差()
    {
        // 業界口訣 GPM = 24 × RT / ΔT°F → 264.17 GPM、ΔT 9 °F ≈ 99 RT
        var dRt = Calc.CoolingRtFromFlow(1000, 7, 12)!.Value;
        Assert.InRange(dRt, 98.5, 99.5);
    }

    [Theory]
    [InlineData(0, 7, 12)]     // 流量 0（泵停）
    [InlineData(-5, 7, 12)]    // 負流量雜訊
    [InlineData(1000, 7, 7)]   // ΔT 0
    [InlineData(1000, 12, 7)]  // ΔT 為負（出回水對調 / 停機雜訊）
    public void 流量或溫差不為正_冷量為0(double flow, double chws, double chwr)
    {
        Assert.Equal(0, Calc.CoolingRtFromFlow(flow, chws, chwr));
    }

    [Fact]
    public void 任一輸入缺值_回null()
    {
        Assert.Null(Calc.CoolingRtFromFlow(null, 7, 12));
        Assert.Null(Calc.CoolingRtFromFlow(1000, null, 12));
        Assert.Null(Calc.CoolingRtFromFlow(1000, 7, null));
        Assert.Null(Calc.CoolingRtFromFlow(double.NaN, 7, 12));
    }

    [Fact]
    public void 比熱可調_乙二醇系統冷量下降()
    {
        var dWater = Calc.CoolingRtFromFlow(1000, 7, 12)!.Value;
        var dGlycol = Calc.CoolingRtFromFlow(1000, 7, 12, 3.6)!.Value;
        Assert.Equal(dWater * 3.6 / 4.186, dGlycol, 6);
    }

    // ── COP / EER / kW/100cfm 換算 ──────────────────────────────

    [Theory]
    [InlineData(0.6, 5.862, 20.0)]
    [InlineData(0.703, 5.003, 17.07)]
    [InlineData(1.0, 3.517, 12.0)]
    public void kW每RT_換算COP與EER(double kwPerRt, double cop, double eer)
    {
        Assert.Equal(cop, Calc.CopFromKwPerRt(kwPerRt)!.Value, 2);
        Assert.Equal(eer, Calc.EerFromKwPerRt(kwPerRt)!.Value, 2);
    }

    [Fact]
    public void 比功率_換算kW每100cfm()
    {
        Assert.Equal(19.82, Calc.KwPer100Cfm(7.0)!.Value, 2);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void 除數非正_回null(double b)
    {
        Assert.Null(Calc.SafeDiv(10, b));
        Assert.Null(Calc.CopFromKwPerRt(b));
    }

    // ── 評級邊界 ────────────────────────────────────────────────

    [Theory]
    [InlineData(0.60, EquipmentGrade.Good)]
    [InlineData(0.65, EquipmentGrade.Good)]   // = Good 算優
    [InlineData(0.70, EquipmentGrade.Fair)]
    [InlineData(0.85, EquipmentGrade.Fair)]   // = Fair 算可
    [InlineData(0.86, EquipmentGrade.Poor)]
    public void 評級邊界_等於門檻歸較好級(double value, EquipmentGrade expected)
    {
        Assert.Equal(expected, Calc.Grade(value, 0.65, 0.85));
    }

    [Fact]
    public void 評級_無值為NoData_缺門檻為Fair()
    {
        Assert.Equal(EquipmentGrade.NoData, Calc.Grade(null, 0.65, 0.85));
        Assert.Equal(EquipmentGrade.Fair, Calc.Grade(0.3, null, 0.85));
    }

    // ── 運轉判定 ────────────────────────────────────────────────

    [Fact]
    public void 運轉判定_RUN點優先於kW()
    {
        Assert.True(Calc.IsRunning(1, 0, 500));
        Assert.False(Calc.IsRunning(0, 300, 500));
    }

    [Theory]
    [InlineData(25.0, 500.0, false)]  // = 5% 不算運轉
    [InlineData(25.1, 500.0, true)]
    [InlineData(0.8, null, false)]    // 無額定 → 1 kW 門檻
    [InlineData(1.2, null, true)]
    public void 運轉判定_無RUN點用kW門檻(double kw, double? ratedKw, bool expected)
    {
        Assert.Equal(expected, Calc.IsRunning(null, kw, ratedKw));
    }

    [Fact]
    public void 運轉判定_皆無資料為未知()
    {
        Assert.Null(Calc.IsRunning(null, null, 500));
    }

    // ── 負載率 bin ──────────────────────────────────────────────

    [Theory]
    [InlineData(10.0, 0)]
    [InlineData(25.0, 0)]
    [InlineData(25.1, 1)]
    [InlineData(50.0, 1)]
    [InlineData(75.0, 2)]
    [InlineData(75.1, 3)]
    [InlineData(120.0, 3)]
    public void 負載率分bin_邊界含上界(double load, int expected)
    {
        Assert.Equal(expected, Calc.LoadBinIndex(load));
    }

    [Fact]
    public void 負載率null_bin為負1()
    {
        Assert.Equal(-1, Calc.LoadBinIndex(null));
    }

    // ── 冰機即時 ────────────────────────────────────────────────

    [Fact]
    public void 冰機即時_運轉中算全部指標()
    {
        var r = Calc.ComputeChillerInstant(new ChillerInstantInput
        {
            dKw = 300, dRt = 500, dChws = 7, dChwr = 12, dCdws = 30, dCdwr = 35,
            dEvapT = 5.5, dCondT = 36.5, dRatedRt = 1000, dRatedKw = 600, dGood = 0.65, dFair = 0.85,
        });
        Assert.True(r.isRunning);
        Assert.Equal(0.6, r.dKwPerRt!.Value, 6);
        Assert.Equal(5.862, r.dCop!.Value, 3);
        Assert.Equal(20.0, r.dEer!.Value, 6);
        Assert.Equal(50.0, r.dLoadPct!.Value, 6);
        Assert.Equal(5.0, r.dChwDeltaT!.Value, 6);
        Assert.Equal(5.0, r.dCdwDeltaT!.Value, 6);
        Assert.Equal(1.5, r.dEvapApproach!.Value, 6);
        Assert.Equal(1.5, r.dCondApproach!.Value, 6);
        Assert.Equal(EquipmentGrade.Good, r.grade);
    }

    [Fact]
    public void 冰機即時_LOAD點優先於RT除以額定()
    {
        var r = Calc.ComputeChillerInstant(new ChillerInstantInput
        {
            dKw = 300, dRt = 500, dLoadPct = 63, dRatedRt = 1000, dRatedKw = 600,
        });
        Assert.Equal(63, r.dLoadPct);
    }

    [Fact]
    public void 冰機即時_停機為Off且效率為null()
    {
        var r = Calc.ComputeChillerInstant(new ChillerInstantInput
        {
            dKw = 10, dRt = 0, dChws = 12, dChwr = 12.2, dRatedKw = 600, dGood = 0.65, dFair = 0.85,
        });
        Assert.False(r.isRunning);
        Assert.Equal(EquipmentGrade.Off, r.grade);
        Assert.Null(r.dKwPerRt);
        Assert.Null(r.dCop);
        Assert.Equal(0.2, r.dChwDeltaT!.Value, 6);   // 溫度類照算
    }

    [Fact]
    public void 冰機即時_運轉中但冷量為0_資料不足()
    {
        var r = Calc.ComputeChillerInstant(new ChillerInstantInput
        {
            dKw = 300, dRt = 0, dRatedKw = 600, dGood = 0.65, dFair = 0.85,
        });
        Assert.True(r.isRunning);
        Assert.Null(r.dKwPerRt);
        Assert.Equal(EquipmentGrade.NoData, r.grade);
    }

    [Fact]
    public void 冰機即時_未綁冷媒溫度_趨近溫度為null()
    {
        var r = Calc.ComputeChillerInstant(new ChillerInstantInput { dKw = 300, dRt = 500, dChws = 7, dCdwr = 35 });
        Assert.Null(r.dEvapApproach);
        Assert.Null(r.dCondApproach);
    }

    // ── 空壓機即時 ──────────────────────────────────────────────

    [Fact]
    public void 空壓機即時_比功率與kW每100cfm()
    {
        var r = Calc.ComputeCompressorInstant(new CompressorInstantInput
        {
            dKw = 70, dFlow = 10, dPressBar = 7, dTempC = 85, dLoaded = 1, dRatedKw = 100, dGood = 7, dFair = 9,
        });
        Assert.True(r.isRunning);
        Assert.Equal(7.0, r.dSpecificPower!.Value, 6);
        Assert.Equal(19.82, r.dKwPer100Cfm!.Value, 2);
        Assert.Equal(70.0, r.dLoadPct!.Value, 6);   // kW ÷ 額定 kW
        Assert.True(r.isLoaded);
        Assert.Equal(EquipmentGrade.Good, r.grade);
    }

    [Fact]
    public void 空壓機即時_比功率超過Fair為差()
    {
        var r = Calc.ComputeCompressorInstant(new CompressorInstantInput
        {
            dKw = 95, dFlow = 10, dRatedKw = 100, dGood = 7, dFair = 9,
        });
        Assert.Equal(EquipmentGrade.Poor, r.grade);
    }

    [Fact]
    public void 空壓機即時_停機為Off()
    {
        var r = Calc.ComputeCompressorInstant(new CompressorInstantInput
        {
            dKw = 2, dFlow = 0, dPressBar = 6.5, dRatedKw = 100, dGood = 7, dFair = 9,
        });
        Assert.False(r.isRunning);
        Assert.Equal(EquipmentGrade.Off, r.grade);
        Assert.Null(r.dSpecificPower);
        Assert.Equal(6.5, r.dPressBar);
    }

    // ── 期間（能量加權）─────────────────────────────────────────

    [Fact]
    public void 期間冰機_kW每RT為總kWh除以總RTh()
    {
        var m = Calc.ComputePeriod(EquipmentKind.Chiller, 6000, 10000, 20);
        Assert.Equal(0.6, m.dPrimary!.Value, 6);
        Assert.Equal(5.862, m.dSecondary!.Value, 3);
        Assert.Equal(20.0, m.dTertiary!.Value, 6);
    }

    [Fact]
    public void 期間空壓機_比功率為kWh每m3乘60()
    {
        // 70 kWh / 600 m³（= 10 m³/min × 60 min）→ 0.1167 kWh/m³ → 7 kW/(m³/min)
        var m = Calc.ComputePeriod(EquipmentKind.Compressor, 70, 600, 1);
        Assert.Equal(7.0, m.dPrimary!.Value, 6);
        Assert.Equal(70.0 / 600.0, m.dTertiary!.Value, 9);
        Assert.Equal(19.82, m.dSecondary!.Value, 2);
    }

    [Fact]
    public void 期間_產出為0_效率為null()
    {
        var m = Calc.ComputePeriod(EquipmentKind.Chiller, 100, 0, 0);
        Assert.Null(m.dPrimary);
        Assert.Null(m.dSecondary);
    }
}
