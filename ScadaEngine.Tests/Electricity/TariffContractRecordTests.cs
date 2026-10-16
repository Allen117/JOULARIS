using ScadaEngine.Web.Features.TariffSetting.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.Electricity;

/// <summary>電費設定「契約容量歷史」— 依日期選版與驗證</summary>
public class TariffContractRecordTests
{
    private static ContractCapacityRecord R(string szDate, double dRegular) =>
        new() { szEffectiveDate = szDate, kw = new() { ["regular_kw"] = dRegular } };

    [Fact]
    public void SelectContractForDate_PicksLatestEffective()
    {
        var config = new TariffConfig { contracts = [R("2026-03-01", 1200), R("2025-01-01", 1500)] };
        Assert.Null(TariffSettingService.SelectContractForDate(config, new DateTime(2024, 12, 31)));
        Assert.Equal(1500, TariffSettingService.ContractKw(
            TariffSettingService.SelectContractForDate(config, new DateTime(2026, 2, 28)), "regular_kw"));
        Assert.Equal(1200, TariffSettingService.ContractKw(
            TariffSettingService.SelectContractForDate(config, new DateTime(2026, 3, 1)), "regular_kw"));
    }

    [Fact]
    public void ContractKw_MissingKeyIsZero()
    {
        Assert.Equal(0, TariffSettingService.ContractKw(R("2026-01-01", 100), "offpeak_kw"));
        Assert.Equal(0, TariffSettingService.ContractKw(null, "regular_kw"));
    }

    [Fact]
    public void ValidateContracts_Rules()
    {
        Assert.True(TariffSettingService.ValidateContracts([]).isValid);
        Assert.True(TariffSettingService.ValidateContracts([R("2026-01-01", 100)]).isValid);
        Assert.False(TariffSettingService.ValidateContracts([R("2026/01/01", 100)]).isValid);
        Assert.False(TariffSettingService.ValidateContracts([R("2026-01-01", 100), R("2026-01-01", 200)]).isValid);
        Assert.False(TariffSettingService.ValidateContracts([R("2026-01-01", -1)]).isValid);
        Assert.False(TariffSettingService.ValidateContracts([R("2026-01-01", 0)]).isValid);
        Assert.False(TariffSettingService.ValidateContracts(
            [new ContractCapacityRecord { szEffectiveDate = "2026-01-01", kw = new() { ["foo_kw"] = 1 } }]).isValid);
    }
}
