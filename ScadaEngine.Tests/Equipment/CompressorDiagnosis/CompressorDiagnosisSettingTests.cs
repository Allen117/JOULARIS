using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;
using ScadaEngine.Web.Features.CompressorEfficiency.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.Equipment.CompressorDiagnosis;

/// <summary>
/// 鎖住空壓系統診斷設定驗證（CompressorDiagnosisService.ValidateAndNormalize）、排程快照期間（最近 7 天滾動）、
/// 設備層新欄位驗證（EquipmentProfileService.ValidateAndNormalize：ControlType / MinLoadPct / 冰機清空）。
/// 對錯 = 設定頁存進壞資料（單位係數錯、時段倒置）或日報引用錯期間的快照。
/// </summary>
public class CompressorDiagnosisSettingTests
{
    [Fact]
    public void 設定_系統點位單位由伺服端重算係數_未綁略過()
    {
        var m = new CompressorDiagnosisSettingModel
        {
            sysPoints = new()
            {
                [CompressorSystemRoles.SupplyPress] = new CompressorSystemPointBinding { szSID = " S1 ", szUnit = "kPa", dScale = 999 },
                [CompressorSystemRoles.Flow] = new CompressorSystemPointBinding { szSID = "" },
            },
        };
        CompressorDiagnosisService.ValidateAndNormalize(m);
        Assert.Single(m.sysPoints);
        Assert.Equal("S1", m.sysPoints[CompressorSystemRoles.SupplyPress].szSID);
        Assert.Equal(0.01, m.sysPoints[CompressorSystemRoles.SupplyPress].dScale);
    }

    [Fact]
    public void 設定_單位無法換算或角色錯誤_拒絕()
    {
        var bad = new CompressorDiagnosisSettingModel
        {
            sysPoints = new() { [CompressorSystemRoles.SupplyPress] = new CompressorSystemPointBinding { szSID = "S1", szUnit = "°C" } },
        };
        Assert.Equal("invalid_unit", Assert.Throws<CompressorDiagnosisValidationException>(() => CompressorDiagnosisService.ValidateAndNormalize(bad)).szCode);

        var badRole = new CompressorDiagnosisSettingModel
        {
            sysPoints = new() { ["KW"] = new CompressorSystemPointBinding { szSID = "S1" } },
        };
        Assert.Equal("invalid_role", Assert.Throws<CompressorDiagnosisValidationException>(() => CompressorDiagnosisService.ValidateAndNormalize(badRole)).szCode);
    }

    [Fact]
    public void 設定_生產時段起須早於訖_24點可用_排序正規化()
    {
        var m = new CompressorDiagnosisSettingModel
        {
            aProductionSchedule =
            [
                new() { nDay = 2, szStart = "8:00", szEnd = "24:00" },
                new() { nDay = 1, szStart = "08:00", szEnd = "17:00" },
            ],
        };
        CompressorDiagnosisService.ValidateAndNormalize(m);
        Assert.Equal(1, m.aProductionSchedule[0].nDay);
        Assert.Equal("08:00", m.aProductionSchedule[1].szStart);
        Assert.Equal("24:00", m.aProductionSchedule[1].szEnd);

        var bad = new CompressorDiagnosisSettingModel { aProductionSchedule = [new() { nDay = 1, szStart = "17:00", szEnd = "08:00" }] };
        Assert.Equal("invalid_schedule", Assert.Throws<CompressorDiagnosisValidationException>(() => CompressorDiagnosisService.ValidateAndNormalize(bad)).szCode);
    }

    [Fact]
    public void 設定_參數超出範圍拒絕_未知確認鍵剔除_基準期需成對()
    {
        var bad = new CompressorDiagnosisSettingModel { paramOverrides = new() { [DiagnosisParameters.PressMarginRatio] = 5 } };
        Assert.Equal("param_range", Assert.Throws<CompressorDiagnosisValidationException>(() => CompressorDiagnosisService.ValidateAndNormalize(bad)).szCode);

        var m = new CompressorDiagnosisSettingModel { aConfirmedParams = ["x.unknown", DiagnosisParameters.TrialStepBar, DiagnosisParameters.TrialStepBar] };
        CompressorDiagnosisService.ValidateAndNormalize(m);
        Assert.Equal([DiagnosisParameters.TrialStepBar], m.aConfirmedParams);

        var half = new CompressorDiagnosisSettingModel { szBaselineFrom = "2026-01-01" };
        Assert.Equal("baseline_incomplete", Assert.Throws<CompressorDiagnosisValidationException>(() => CompressorDiagnosisService.ValidateAndNormalize(half)).szCode);
    }

    [Fact]
    public void 排程快照期間_報告日往前共7天_訖不含()
    {
        var (dtFrom, dtTo) = CompressorDiagnosisService.ScheduledPeriod(new DateTime(2026, 10, 15, 13, 0, 0));
        Assert.Equal(new DateTime(2026, 10, 9), dtFrom);
        Assert.Equal(new DateTime(2026, 10, 16), dtTo);
    }

    private static EquipmentProfileModel Compressor() => new()
    {
        szKind = EquipmentKind.Compressor,
        szName = "AC1",
        aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "K" }, new() { szRole = EquipmentRoles.AIRFLOW, szSID = "F" }],
    };

    [Fact]
    public void 設備欄位_控制型式需合法_最低負載0到100_冰機清空空壓機專屬欄位()
    {
        var m = Compressor();
        m.szControlType = "turbo";
        Assert.Equal("invalid_control_type", Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(m)).szCode);

        var m2 = Compressor();
        m2.dMinLoadPct = 120;
        Assert.Equal("min_load_pct_range", Assert.Throws<EquipmentProfileValidationException>(() => EquipmentProfileService.ValidateAndNormalize(m2)).szCode);

        var m3 = Compressor();
        m3.szControlType = " vsd ";
        m3.nMaxStartsPerHour = 0;
        EquipmentProfileService.ValidateAndNormalize(m3);
        Assert.Equal(CompressorControlType.Vsd, m3.szControlType);
        Assert.Null(m3.nMaxStartsPerHour);

        var ch = new EquipmentProfileModel
        {
            szKind = EquipmentKind.Chiller, szName = "CH1", szControlType = "vsd", dMaxPressure = 8,
            aBindings = [new() { szRole = EquipmentRoles.KW, szSID = "K" }, new() { szRole = EquipmentRoles.RT, szSID = "R" }],
        };
        EquipmentProfileService.ValidateAndNormalize(ch);
        Assert.Equal("vsd", ch.szControlType);   // 冰機 ControlType = 定頻 / 變頻（90.1 Path A / B）
        Assert.Null(ch.dMaxPressure);
    }
}
