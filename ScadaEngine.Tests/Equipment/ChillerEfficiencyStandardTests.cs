using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Web.Services;
using Ctl = ScadaEngine.Common.Equipment.ChillerEfficiencyStandard.ChillerControl;

namespace ScadaEngine.Tests.Equipment;

/// <summary>
/// 鎖住冰機評級門檻（ChillerEfficiencyStandard.Resolve）：ASHRAE 90.1-2022 Table 6.8.1-3 數值、
/// 噸數級距邊界、定頻 → Path A / 變頻 → Path B、氣冷 EER 換算、缺額定 RT 取最寬鬆級距、機型未設定補位、手動模式。
/// 對錯 = 診斷頁即時卡 / 期間排名 / EMS 首頁卡的優可差全部判錯。
/// </summary>
public class ChillerEfficiencyStandardTests
{
    private static EquipmentProfileModel Ch(string? cooling = ChillerCoolingType.Water, string? type = ChillerCompressorType.Centrifugal,
        string? control = Ctl.Fixed, double? rated = 500, string? mode = null) => new()
    {
        szKind = EquipmentKind.Chiller, szName = "CH", szCoolingType = cooling, szChillerType = type,
        szControlType = control, dRatedOutput = rated, szThresholdMode = mode,
    };

    [Theory]
    [InlineData(149.9, 0.610, 0.695)]
    [InlineData(150, 0.610, 0.635)]
    [InlineData(299.9, 0.610, 0.635)]
    [InlineData(300, 0.560, 0.595)]
    [InlineData(399.9, 0.560, 0.595)]
    [InlineData(400, 0.560, 0.585)]
    [InlineData(600, 0.560, 0.585)]
    [InlineData(2000, 0.560, 0.585)]
    public void 水冷離心_級距邊界_定頻PathA_變頻PathB(double dRt, double dPathA, double dPathB)
    {
        Assert.Equal(dPathA, ChillerEfficiencyStandard.Resolve(Ch(rated: dRt, control: Ctl.Fixed)).dGood, 3);
        Assert.Equal(dPathB, ChillerEfficiencyStandard.Resolve(Ch(rated: dRt, control: Ctl.Vsd)).dGood, 3);
    }

    [Theory]
    [InlineData(74.9, 0.750, 0.780)]
    [InlineData(75, 0.720, 0.750)]
    [InlineData(150, 0.660, 0.680)]
    [InlineData(300, 0.610, 0.625)]
    [InlineData(600, 0.560, 0.585)]
    public void 水冷容積式_級距邊界(double dRt, double dPathA, double dPathB)
    {
        Assert.Equal(dPathA, ChillerEfficiencyStandard.Resolve(Ch(type: ChillerCompressorType.Screw, rated: dRt)).dGood, 3);
        Assert.Equal(dPathB, ChillerEfficiencyStandard.Resolve(Ch(type: ChillerCompressorType.Screw, rated: dRt, control: Ctl.Vsd)).dGood, 3);
    }

    [Fact]
    public void 螺旋渦卷往復三者同表_歸容積式()
    {
        foreach (var szType in new[] { ChillerCompressorType.Screw, ChillerCompressorType.Scroll, ChillerCompressorType.Reciprocating })
        {
            var r = ChillerEfficiencyStandard.Resolve(Ch(type: szType, rated: 350));
            Assert.Equal(0.610, r.dGood, 3);
            Assert.Equal(ChillerEfficiencyStandard.FamilyPositiveDisplacement, r.szFamily);
            Assert.Equal(szType, r.szChillerType);
        }
    }

    [Fact]
    public void 氣冷_EER換算kWRT_不分形式只分150RT()
    {
        foreach (var szType in new[] { ChillerCompressorType.Centrifugal, ChillerCompressorType.Screw, null })
        {
            foreach (var dRt in new[] { 50.0, 149.9, 150, 800 })
            {
                var a = ChillerEfficiencyStandard.Resolve(Ch(cooling: ChillerCoolingType.Air, type: szType, rated: dRt));
                var b = ChillerEfficiencyStandard.Resolve(Ch(cooling: ChillerCoolingType.Air, type: szType, rated: dRt, control: Ctl.Vsd));
                Assert.Equal(1.188, a.dGood, 3);    // 12 ÷ EER 10.1
                Assert.Equal(1.237, b.dGood, 3);    // 12 ÷ EER 9.7
                Assert.Equal(ChillerEfficiencyStandard.FamilyAir, a.szFamily);
                Assert.False(a.isTypeUnset);        // 氣冷 90.1 不分形式 → 形式未選不算缺
            }
        }
        Assert.Equal(150, ChillerEfficiencyStandard.Resolve(Ch(cooling: ChillerCoolingType.Air, rated: 149.9)).dBandMaxRt);
        var ge150 = ChillerEfficiencyStandard.Resolve(Ch(cooling: ChillerCoolingType.Air, rated: 150));
        Assert.Equal(150, ge150.dBandMinRt);
        Assert.Null(ge150.dBandMaxRt);
    }

    [Fact]
    public void 可差分界_為FL乘115_取三位()
    {
        var r = ChillerEfficiencyStandard.Resolve(Ch(rated: 500));
        Assert.Equal(0.560, r.dGood, 3);
        Assert.Equal(0.644, r.dFair, 3);
        Assert.Equal(r.dGood, r.dStandardFl);
        Assert.Equal(1.366, ChillerEfficiencyStandard.Resolve(Ch(cooling: ChillerCoolingType.Air, rated: 150)).dFair, 3);
        Assert.Equal(EquipmentThresholdMode.Standard, r.szMode);
        Assert.Equal(ChillerEfficiencyStandard.PathA, r.szPath);
        Assert.Equal(400, r.dBandMinRt);
        Assert.Equal(600, r.dBandMaxRt);
    }

    [Fact]
    public void 未填額定RT_取該系列最寬鬆級距並標旗()
    {
        var c = ChillerEfficiencyStandard.Resolve(Ch(rated: null));
        Assert.Equal(0.610, c.dGood, 3);
        Assert.True(c.isRatedMissing);
        Assert.Equal(150, c.dBandMaxRt);

        var pd = ChillerEfficiencyStandard.Resolve(Ch(type: ChillerCompressorType.Scroll, rated: 0, control: Ctl.Vsd));
        Assert.Equal(0.780, pd.dGood, 3);
        Assert.True(pd.isRatedMissing);

        var air = ChillerEfficiencyStandard.Resolve(Ch(cooling: ChillerCoolingType.Air, rated: null));
        Assert.Equal(1.188, air.dGood, 3);
        Assert.Equal(150, air.dBandMaxRt);

        Assert.False(ChillerEfficiencyStandard.Resolve(Ch(rated: 500)).isRatedMissing);
    }

    [Fact]
    public void 機型未設定_以水冷離心定頻計並標旗()
    {
        var r = ChillerEfficiencyStandard.Resolve(Ch(cooling: null, type: null, control: null, rated: 500));
        Assert.Equal(0.560, r.dGood, 3);
        Assert.Equal(ChillerCoolingType.Water, r.szCoolingType);
        Assert.Equal(ChillerCompressorType.Centrifugal, r.szChillerType);
        Assert.Equal(ChillerEfficiencyStandard.PathA, r.szPath);
        Assert.True(r.isTypeUnset);

        // 只缺變頻欄也算未設定（路徑以定頻補位）
        Assert.True(ChillerEfficiencyStandard.Resolve(Ch(control: null)).isTypeUnset);
        // 水冷缺形式
        Assert.True(ChillerEfficiencyStandard.Resolve(Ch(type: null)).isTypeUnset);
        Assert.False(ChillerEfficiencyStandard.Resolve(Ch()).isTypeUnset);
    }

    [Fact]
    public void 門檻模式_NULL與standard依標準_manual用欄位值()
    {
        var p = Ch(rated: 500, mode: null);
        p.dGoodThreshold = 0.9; p.dFairThreshold = 1.2;
        Assert.Equal(0.560, ChillerEfficiencyStandard.Resolve(p).dGood, 3);   // 依標準時欄位不使用

        p.szThresholdMode = EquipmentThresholdMode.Standard;
        Assert.Equal(0.560, ChillerEfficiencyStandard.Resolve(p).dGood, 3);

        p.szThresholdMode = EquipmentThresholdMode.Manual;
        var m = ChillerEfficiencyStandard.Resolve(p);
        Assert.Equal(0.9, m.dGood);
        Assert.Equal(1.2, m.dFair);
        Assert.Equal(EquipmentThresholdMode.Manual, m.szMode);
        Assert.Null(m.dStandardFl);

        // 手動但欄位空 → 手動預設值
        var m2 = ChillerEfficiencyStandard.Resolve(Ch(mode: EquipmentThresholdMode.Manual));
        Assert.Equal(0.560, m2.dGood, 3);
        Assert.Equal(0.644, m2.dFair, 3);
    }

    [Fact]
    public void 空壓機_不受影響_恆用欄位值或7與9()
    {
        var ac = new EquipmentProfileModel { szKind = EquipmentKind.Compressor, szName = "AC", szControlType = "vsd", dRatedOutput = 20 };
        var r = ChillerEfficiencyStandard.Resolve(ac);
        Assert.Equal(7.0, r.dGood);
        Assert.Equal(9.0, r.dFair);
        Assert.Equal(EquipmentThresholdMode.Manual, r.szMode);
        Assert.Null(r.szFamily);

        ac.dGoodThreshold = 6; ac.dFairThreshold = 8; ac.szThresholdMode = EquipmentThresholdMode.Standard;
        var r2 = ChillerEfficiencyStandard.Resolve(ac);
        Assert.Equal(6, r2.dGood);
        Assert.Equal(8, r2.dFair);
    }

    // ── 存檔驗證 ──────────────────────────────────────────────

    private static EquipmentProfileModel ChForSave() => new()
    {
        szKind = EquipmentKind.Chiller, szName = "CH1",
        aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "K" }, new() { szRole = EquipmentRoles.RT, szSID = "R" }],
    };

    [Fact]
    public void 存檔驗證_冰機允許定頻變頻_拒節流與非法機型()
    {
        var ok = ChForSave();
        ok.szControlType = " vsd "; ok.szCoolingType = "air"; ok.szChillerType = "scroll"; ok.szThresholdMode = "manual";
        EquipmentProfileService.ValidateAndNormalize(ok);
        Assert.Equal("vsd", ok.szControlType);
        Assert.Equal("air", ok.szCoolingType);
        Assert.Equal("scroll", ok.szChillerType);
        Assert.Equal("manual", ok.szThresholdMode);

        string Code(Action<EquipmentProfileModel> set)
        {
            var m = ChForSave();
            set(m);
            return Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(m)).szCode;
        }
        Assert.Equal("invalid_control_type", Code(m => m.szControlType = "modulation"));
        Assert.Equal("invalid_cooling_type", Code(m => m.szCoolingType = "evap"));
        Assert.Equal("invalid_chiller_type", Code(m => m.szChillerType = "turbo"));
        Assert.Equal("invalid_threshold_mode", Code(m => m.szThresholdMode = "auto"));

        var blank = ChForSave();
        blank.szCoolingType = " "; blank.szThresholdMode = "";
        EquipmentProfileService.ValidateAndNormalize(blank);
        Assert.Null(blank.szCoolingType);
        Assert.Null(blank.szThresholdMode);
    }

    [Fact]
    public void 存檔驗證_空壓機清空冰機機型欄位_ControlType語意不變()
    {
        var ac = new EquipmentProfileModel
        {
            szKind = EquipmentKind.Compressor, szName = "AC", szControlType = "modulation",
            szCoolingType = "water", szChillerType = "screw", szThresholdMode = "standard",
            aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "K" }, new() { szRole = EquipmentRoles.AIRFLOW, szSID = "F" }],
        };
        EquipmentProfileService.ValidateAndNormalize(ac);
        Assert.Equal("modulation", ac.szControlType);
        Assert.Null(ac.szCoolingType);
        Assert.Null(ac.szChillerType);
        Assert.Null(ac.szThresholdMode);
    }
}
