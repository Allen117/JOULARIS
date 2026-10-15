using ScadaEngine.Common.Equipment;
using Q = ScadaEngine.Common.Equipment.EquipmentQuantity;

namespace ScadaEngine.Tests.Equipment;

/// <summary>
/// 鎖住設備效率診斷的單位換算係數（綁定時存 Scale，原始值 × Scale = 基準單位）。
/// 對錯 = 流量差 60 倍、冷量差 3.5 倍這類整數級錯誤，效率數字全部失真。
/// </summary>
public class EquipmentUnitScaleTests
{
    [Theory]
    [InlineData("L/min", 1.0)]
    [InlineData("L/s", 60.0)]
    [InlineData("m³/h", 16.6667)]
    [InlineData("GPM", 3.7854)]
    public void 冰水流量_基準LPM(string unit, double expected)
    {
        Assert.Equal(expected, EquipmentUnitScale.ScaleOf(Q.WaterFlow, unit)!.Value, 4);
    }

    [Theory]
    [InlineData("m³/min", 1.0)]
    [InlineData("m³/h", 1.0 / 60.0)]
    [InlineData("Nm³/h", 1.0 / 60.0)]
    [InlineData("cfm", 0.028317)]
    [InlineData("L/min", 0.001)]
    public void 空氣量_基準m3每分(string unit, double expected)
    {
        Assert.Equal(expected, EquipmentUnitScale.ScaleOf(Q.AirFlow, unit)!.Value, 6);
    }

    [Theory]
    [InlineData("RT", 1.0)]
    [InlineData("USRT", 1.0)]
    [InlineData("kW", 1.0 / 3.517)]
    public void 冷量_基準RT(string unit, double expected)
    {
        Assert.Equal(expected, EquipmentUnitScale.ScaleOf(Q.Cooling, unit)!.Value, 6);
    }

    [Theory]
    [InlineData("bar", 1.0)]
    [InlineData("kPa", 0.01)]
    [InlineData("psi", 0.068948)]
    [InlineData("MPa", 10.0)]
    public void 壓力_基準bar(string unit, double expected)
    {
        Assert.Equal(expected, EquipmentUnitScale.ScaleOf(Q.Pressure, unit)!.Value, 6);
    }

    [Theory]
    [InlineData("LPM", "L/min")]
    [InlineData("lpm", "L/min")]
    [InlineData("m3/h", "m³/h")]
    [InlineData("CMH", "m³/h")]
    [InlineData("ｇｐｍ", "GPM")]   // 全形
    [InlineData("", "L/min")]       // 空 → 基準
    public void 冰水流量_現場單位別名(string raw, string canonical)
    {
        Assert.Equal(canonical, EquipmentUnitScale.Canonicalize(Q.WaterFlow, raw));
    }

    [Theory]
    [InlineData("CMM", "m³/min")]
    [InlineData("Nm3/h", "Nm³/h")]
    [InlineData("SCFM", "cfm")]
    public void 空氣量_現場單位別名(string raw, string canonical)
    {
        Assert.Equal(canonical, EquipmentUnitScale.Canonicalize(Q.AirFlow, raw));
    }

    [Theory]
    [InlineData("℃")]
    [InlineData("°C")]
    [InlineData("degC")]
    public void 溫度_攝氏別名係數1(string raw)
    {
        Assert.Equal(1.0, EquipmentUnitScale.ScaleOf(Q.Temperature, raw));
    }

    [Fact]
    public void 不認得的單位_回null()
    {
        Assert.Null(EquipmentUnitScale.ScaleOf(Q.WaterFlow, "furlong/fortnight"));
        Assert.Null(EquipmentUnitScale.ScaleOf(Q.Temperature, "°F"));   // 華氏需偏移，刻意不收
    }

    [Fact]
    public void 每類第一個單位為基準且係數為1()
    {
        foreach (Q q in Enum.GetValues(typeof(Q)))
        {
            if (q == Q.Unknown) continue;
            Assert.Equal(1.0, EquipmentUnitScale.UnitsOf(q)[0].dScale);
        }
    }

    [Fact]
    public void 必要角色_冰機需電力加流量溫度或RT()
    {
        Assert.Empty(EquipmentKind.MissingRequired(EquipmentKind.Chiller, ["KW", "FLOW", "CHWS", "CHWR"]));
        Assert.Empty(EquipmentKind.MissingRequired(EquipmentKind.Chiller, ["KWH", "RT"]));
        Assert.Equal(["FLOW+CHWS+CHWR|RT"], EquipmentKind.MissingRequired(EquipmentKind.Chiller, ["KW", "FLOW", "CHWS"]));
        Assert.Equal(["KW|KWH"], EquipmentKind.MissingRequired(EquipmentKind.Chiller, ["RT"]));
    }

    [Fact]
    public void 必要角色_空壓機需電力加空氣量()
    {
        Assert.Empty(EquipmentKind.MissingRequired(EquipmentKind.Compressor, ["KW", "AIRFLOW"]));
        Assert.Equal(["KW|KWH", "AIRFLOW"], EquipmentKind.MissingRequired(EquipmentKind.Compressor, ["DISCHPRESS"]));
    }
}
