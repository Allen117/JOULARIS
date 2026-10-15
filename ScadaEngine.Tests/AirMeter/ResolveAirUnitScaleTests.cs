using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.AirMeter;

/// <summary>
/// 鎖住 AirMeterCircuitService.ResolveUnitScale：點位單位字串 → Nm³ 換算係數。
/// 這是「綁定時定案」的單位判定（寫進 AirMeterCircuit.UnitScale，之後不追溯），
/// 對錯 = 該產氣量表所有歷史產氣量差 1000 倍 → 比能耗 kWh/Nm³ 差 1000 倍。
///
/// ⚠️ 與氣表的**刻意差異**：產氣量表**不收**「度」（那是 kWh 俗稱；氣表收是因為天然氣帳單 1 度 = 1 m³）。
/// </summary>
public class ResolveAirUnitScaleTests
{
    [Theory]
    [InlineData("Nm³")]
    [InlineData("Nm3")]
    [InlineData("NM3")]         // 不分大小寫
    [InlineData("m³")]
    [InlineData("m3")]
    [InlineData("SCM")]
    [InlineData("立方公尺")]
    [InlineData("立方米")]
    [InlineData(" Nm3 ")]       // 前後空白先 trim
    public void 立方公尺系單位_係數為1(string szUnit)
    {
        Assert.Equal(1.0, AirMeterCircuitService.ResolveUnitScale(szUnit));
    }

    [Theory]
    [InlineData("L")]
    [InlineData("NL")]
    [InlineData("Liter")]
    [InlineData("litre")]
    [InlineData("公升")]
    public void 公升系單位_係數為千分之一(string szUnit)
    {
        Assert.Equal(0.001, AirMeterCircuitService.ResolveUnitScale(szUnit));
    }

    /// <summary>「度」在氣表 = 1.0、在產氣量表 = null；明文對照，避免日後有人把兩張對照表「統一」</summary>
    [Fact]
    public void 度_在氣表回1_在產氣量表回null()
    {
        Assert.Equal(1.0, GasMeterCircuitService.ResolveUnitScale("度"));
        Assert.Null(AirMeterCircuitService.ResolveUnitScale("度"));
    }

    [Theory]
    [InlineData("kWh")]
    [InlineData("度")]
    [InlineData("kW")]
    [InlineData("bar")]
    [InlineData("°C")]
    [InlineData("Nm³/min")]     // 瞬時流量，非累積量
    [InlineData("Nm³/h")]
    [InlineData("m³/min")]
    [InlineData("CMH")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 非累積產氣量單位_回null(string? szUnit)
    {
        Assert.Null(AirMeterCircuitService.ResolveUnitScale(szUnit));
    }
}

/// <summary>
/// 鎖住 <c>AirMeterCircuitService.ResolveAirPointScale</c> — **產氣量點位判定的唯一入口**
/// （點位下拉過濾與存檔驗證共用同一支）。
///
/// 條件：單位可換算 Nm³ **且** 名稱含空壓關鍵字 **且** 名稱不含天然氣字樣。
/// 名稱條件存在理由：Nm³ / m³ 對天然氣表、水表、壓縮空氣都成立 —
/// 錯放天然氣點位 = 比能耗拿天然氣用量當分母；錯放水表 = 拿水量當分母。
/// </summary>
public class ResolveAirPointScaleTests
{
    [Theory]
    [InlineData("空壓機房總產氣量")]
    [InlineData("1號空壓機累積")]
    [InlineData("壓縮空氣總表")]
    [InlineData("產氣累積量")]
    [InlineData("A線氣量累計")]
    [InlineData("Air Totalizer")]
    [InlineData("COMPRESSOR_01_FLOW_TOTAL")]    // 不分大小寫
    [InlineData("FAD_Total")]
    public void 名稱含空壓關鍵字_判定為產氣量點位(string szName)
    {
        Assert.True(AirMeterCircuitService.HasAirNameKeyword(szName));
    }

    [Theory]
    [InlineData("天然氣氣量累計")]     // 「氣量」命中但含天然氣字樣 → 排除
    [InlineData("鍋爐瓦斯氣量")]
    [InlineData("燃氣產氣量")]
    [InlineData("LNG Air Mix")]        // 英文天然氣字樣也排除
    [InlineData("Natural Gas Compressor")]
    public void 名稱含天然氣字樣_即使命中空壓關鍵字也排除(string szName)
    {
        Assert.False(AirMeterCircuitService.HasAirNameKeyword(szName));
    }

    [Theory]
    [InlineData("冷氣主機電流")]       // 「氣」單字刻意不當關鍵字
    [InlineData("氣溫")]
    [InlineData("自來水累積量")]
    [InlineData("總用電度數")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 名稱不含空壓關鍵字_不判定為產氣量點位(string? szName)
    {
        Assert.False(AirMeterCircuitService.HasAirNameKeyword(szName));
    }

    [Theory]
    [InlineData("空壓機房總產氣量", "Nm³", 1.0)]
    [InlineData("Air Totalizer", "m3", 1.0)]
    [InlineData("壓縮空氣總表", "SCM", 1.0)]
    [InlineData("產氣累積量", "NL", 0.001)]       // L 系換算係數 0.001
    public void 名稱有關鍵字且單位可換算_回換算係數(string szName, string szUnit, double dExpected)
    {
        Assert.Equal(dExpected, AirMeterCircuitService.ResolveAirPointScale(szName, szUnit));
    }

    /// <summary>天然氣表點位（含氣表測試用的命名）不得出現在產氣量表下拉 — 與氣表體系互不相干</summary>
    [Theory]
    [InlineData("天然氣累積量", "m³")]
    [InlineData("鍋爐瓦斯用量", "m³")]
    [InlineData("累積量", "Nm³")]              // 氣表因單位無歧義放行；產氣量表沒有此捷徑 → 排除
    [InlineData("LNG_Totalizer", "Nm3")]
    [InlineData("天然氣氣量", "Nm³")]
    public void 天然氣點位_在產氣量表回null(string szName, string szUnit)
    {
        Assert.Null(AirMeterCircuitService.ResolveAirPointScale(szName, szUnit));
    }

    [Theory]
    [InlineData("自來水累積量", "m³")]
    [InlineData("冷卻塔補水量", "公升")]
    public void 水表點位_名稱無空壓關鍵字_一律排除(string szName, string szUnit)
    {
        Assert.NotNull(AirMeterCircuitService.ResolveUnitScale(szUnit));
        Assert.Null(AirMeterCircuitService.ResolveAirPointScale(szName, szUnit));
    }

    [Theory]
    [InlineData("空壓機瞬時流量", "Nm³/min")]   // 名稱有關鍵字但單位是流量 → 仍排除
    [InlineData("空壓機出口壓力", "bar")]
    [InlineData("空壓機總用電", "度")]          // 空壓機的電表點位 → 排除（kWh 俗稱）
    [InlineData("空壓機總用電", "kWh")]
    [InlineData("空壓機房總產氣量", "")]
    [InlineData("空壓機房總產氣量", null)]
    public void 單位不可換算_名稱有關鍵字也排除(string szName, string? szUnit)
    {
        Assert.Null(AirMeterCircuitService.ResolveAirPointScale(szName, szUnit));
    }
}
