using ScadaEngine.Common.Data.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.AirMeter;

/// <summary>
/// 鎖住 AirCompressorDeclarationService.Merge：kWh 與 Nm³ 依 bucket index 對齊合併、比能耗計算。
/// 對錯 = 申報表上每個月的 kWh/Nm³ 都錯，且數字看起來合理、不易察覺。
/// </summary>
public class AirCompressorDeclarationMergeTests
{
    private static readonly AirCompressorDeclarationModel Report = new()
    {
        nId = 7, szName = "空壓一廠", nEnergyCircuitId = 1, nAirMeterCircuitId = 2
    };

    private static EnergyReportResult Kwh(params double[] values)
    {
        var r = new EnergyReportResult { szCircuitName = "空壓機房總電表", dtStart = new DateTime(2026, 1, 1), dtEnd = new DateTime(2027, 1, 1) };
        for (var i = 0; i < values.Length; i++)
        {
            r.buckets.Add(new EnergyReportBucket { dtBucketStart = new DateTime(2026, i + 1, 1), szLabel = $"2026-{i + 1:D2}", dKwh = values[i] });
            r.dTotalKwh += values[i];
        }
        return r;
    }

    private static AirMeterUsageReportResult Air(params double[] values)
    {
        var r = new AirMeterUsageReportResult { szCircuitName = "空壓機房產氣總表" };
        for (var i = 0; i < values.Length; i++)
        {
            r.buckets.Add(new AirMeterUsageReportBucket { dtBucketStart = new DateTime(2026, i + 1, 1), szLabel = $"2026-{i + 1:D2}", dNm3 = values[i] });
            r.dTotalNm3 += values[i];
        }
        return r;
    }

    [Fact]
    public void 每月比能耗_等於kWh除以Nm3_三位小數()
    {
        var result = AirCompressorDeclarationService.Merge(Report, 2026, Kwh(1000, 1234), Air(8000, 10000));

        Assert.Equal(2, result.buckets.Count);
        Assert.Equal(0.125, result.buckets[0].dKwhPerNm3);
        Assert.Equal(0.123, result.buckets[1].dKwhPerNm3);   // 0.1234 → 0.123
        Assert.Equal("2026-01", result.buckets[0].szLabel);
        Assert.Equal(1000, result.buckets[0].dKwh);
        Assert.Equal(8000, result.buckets[0].dNm3);
    }

    [Theory]
    [InlineData(0.0)]      // 空壓機停機 / 產氣量表整月無資料
    [InlineData(-5.0)]     // 虛擬迴路扣減後為負
    public void Nm3小於等於0_比能耗為null(double dNm3)
    {
        var result = AirCompressorDeclarationService.Merge(Report, 2026, Kwh(500), Air(dNm3));
        Assert.Null(result.buckets[0].dKwhPerNm3);
    }

    [Fact]
    public void 合計比能耗_等於總kWh除以總Nm3_不是各月比能耗平均()
    {
        // 各月比能耗 0.1 與 0.2（簡單平均 0.15）；兩月產氣量不等，加權後應為 5000 / 30000 = 0.167
        var result = AirCompressorDeclarationService.Merge(Report, 2026, Kwh(1000, 4000), Air(10000, 20000));

        Assert.Equal(5000, result.dTotalKwh);
        Assert.Equal(30000, result.dTotalNm3);
        Assert.Equal(0.167, result.dTotalKwhPerNm3);          // 5000 / 30000，非 (0.1 + 0.2) / 2 = 0.15
    }

    [Fact]
    public void 總Nm3為0_合計比能耗為null()
    {
        var result = AirCompressorDeclarationService.Merge(Report, 2026, Kwh(100, 200), Air(0, 0));
        Assert.Null(result.dTotalKwhPerNm3);
    }

    [Fact]
    public void bucket數不一致_擲錯不硬合併()
    {
        var ex = Assert.Throws<AirCompressorDeclarationException>(
            () => AirCompressorDeclarationService.Merge(Report, 2026, Kwh(1, 2, 3), Air(1, 2)));
        Assert.Equal("bucket_mismatch", ex.Code);
    }

    [Fact]
    public void 兩側斷線旗標與警告_各自帶入()
    {
        var kwh = Kwh(100, 200);
        kwh.buckets[1].isStale = true;
        kwh.isHasWarning = true;
        var air = Air(1000, 2000);
        air.buckets[0].isStale = true;
        air.isHasWarning = true;

        var result = AirCompressorDeclarationService.Merge(Report, 2026, kwh, air);

        Assert.False(result.buckets[0].isKwhStale);
        Assert.True(result.buckets[0].isNm3Stale);
        Assert.True(result.buckets[1].isKwhStale);
        Assert.False(result.buckets[1].isNm3Stale);
        Assert.True(result.isHasKwhWarning);
        Assert.True(result.isHasNm3Warning);
        Assert.Equal("空壓機房總電表", result.szEnergyCircuitName);
        Assert.Equal("空壓機房產氣總表", result.szAirMeterCircuitName);
        Assert.Equal("空壓一廠", result.szReportName);
    }
}
