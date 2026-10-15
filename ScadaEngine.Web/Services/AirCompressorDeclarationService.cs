using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 空壓機能源申報 — 申報報表設定 CRUD + 合併查詢（與冰水版 <see cref="EnergyDeclarationService"/> 同構）。
/// 查詢不自建聚合邏輯：kWh 委派 <see cref="EnergyReportService.GetCalendarMonthlyReportAsync"/>（與冰水申報同一支，
/// 同迴路同年度數字必然一致），Nm³ 委派 <see cref="AirMeterUsageReportService.GetCalendarMonthlyReportAsync"/>
/// （AirMeterLeafHourly 預聚合）。兩邊皆用 EnergyReportService.BuildCalendarMonthRanges 切界 → bucket 依 index 對齊合併。
/// </summary>
public class AirCompressorDeclarationService
{
    private readonly ILogger<AirCompressorDeclarationService> _logger;
    private readonly DatabaseConfigService _configService;
    private readonly EnergyReportService _energyReportService;
    private readonly AirMeterUsageReportService _airReportService;
    private string _szConnectionString = string.Empty;

    public AirCompressorDeclarationService(
        ILogger<AirCompressorDeclarationService> logger,
        DatabaseConfigService configService,
        EnergyReportService energyReportService,
        AirMeterUsageReportService airReportService)
    {
        _logger = logger;
        _configService = configService;
        _energyReportService = energyReportService;
        _airReportService = airReportService;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private const string SelectColumns = @"
                   Id AS nId, Name AS szName,
                   EnergyCircuitId AS nEnergyCircuitId, AirMeterCircuitId AS nAirMeterCircuitId,
                   SortOrder AS nSortOrder, Description AS szDescription,
                   CreatedAt AS dtCreatedAt, UpdatedAt AS dtUpdatedAt";

    // ---------- 設定 CRUD ----------

    public async Task<List<AirCompressorDeclarationModel>> GetAllAsync()
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<AirCompressorDeclarationModel>($@"
            SELECT {SelectColumns}
            FROM   AirCompressorDeclarationReport
            ORDER BY SortOrder, Id");
        return rows.ToList();
    }

    public async Task<AirCompressorDeclarationModel?> GetByIdAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<AirCompressorDeclarationModel>($@"
            SELECT {SelectColumns}
            FROM   AirCompressorDeclarationReport
            WHERE  Id = @nId", new { nId });
    }

    public async Task<int> CreateAsync(AirCompressorDeclarationModel model)
    {
        const string szSql = @"
            INSERT INTO AirCompressorDeclarationReport (Name, EnergyCircuitId, AirMeterCircuitId, SortOrder, Description)
            OUTPUT INSERTED.Id
            VALUES (@szName, @nEnergyCircuitId, @nAirMeterCircuitId,
                    ISNULL((SELECT MAX(SortOrder) + 1 FROM AirCompressorDeclarationReport), 0), @szDescription)";
        using var conn = await GetConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(szSql, new
        {
            model.szName,
            model.nEnergyCircuitId,
            model.nAirMeterCircuitId,
            model.szDescription
        });
    }

    public async Task<bool> UpdateAsync(int nId, string szName, int nEnergyCircuitId, int nAirMeterCircuitId, string? szDescription)
    {
        const string szSql = @"
            UPDATE AirCompressorDeclarationReport
            SET    Name = @szName, EnergyCircuitId = @nEnergyCircuitId, AirMeterCircuitId = @nAirMeterCircuitId,
                   Description = @szDescription, UpdatedAt = GETDATE()
            WHERE  Id = @nId";
        using var conn = await GetConnectionAsync();
        var nAffected = await conn.ExecuteAsync(szSql, new { nId, szName, nEnergyCircuitId, nAirMeterCircuitId, szDescription });
        return nAffected > 0;
    }

    public async Task<bool> DeleteAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        var nAffected = await conn.ExecuteAsync(
            "DELETE FROM AirCompressorDeclarationReport WHERE Id = @nId", new { nId });
        return nAffected > 0;
    }

    // ---------- 合併查詢 ----------

    /// <summary>
    /// 取得申報報表結果 — 頁面固定格式：指定年度 → 12 個曆月 bucket（每月 1 號 00:00 ~ 次月 1 號）。
    /// 綁定的來源若已被刪除，擲出 <see cref="AirCompressorDeclarationException"/>（代碼區分來源別，前端依語系翻譯）。
    /// </summary>
    public async Task<AirCompressorDeclarationResult> GetDeclarationReportAsync(int nReportId, int nYear)
    {
        if (nYear < 2000 || nYear > 2100)
            throw new AirCompressorDeclarationException("year_invalid", $"年度 {nYear} 超出範圍（2000~2100）");

        var report = await GetByIdAsync(nReportId);
        if (report == null)
            throw new AirCompressorDeclarationException("report_not_found", $"申報報表 Id={nReportId} 不存在");

        EnergyReportResult kwhResult;
        try
        {
            kwhResult = await _energyReportService.GetCalendarMonthlyReportAsync(report.nEnergyCircuitId, nYear);
        }
        catch (InvalidOperationException)
        {
            throw new AirCompressorDeclarationException("energy_circuit_missing",
                $"申報報表「{report.szName}」綁定的用電迴路 Id={report.nEnergyCircuitId} 已不存在，請重新編輯設定");
        }

        AirMeterUsageReportResult airResult;
        try
        {
            airResult = await _airReportService.GetCalendarMonthlyReportAsync(report.nAirMeterCircuitId, nYear);
        }
        catch (InvalidOperationException)
        {
            throw new AirCompressorDeclarationException("air_circuit_missing",
                $"申報報表「{report.szName}」綁定的產氣量表迴路 Id={report.nAirMeterCircuitId} 已不存在，請重新編輯設定");
        }

        if (kwhResult.buckets.Count != airResult.buckets.Count)
        {
            _logger.LogError(
                "空壓機能源申報 bucket 數不一致 kWh={KwhCount} Nm3={Nm3Count} reportId={ReportId}",
                kwhResult.buckets.Count, airResult.buckets.Count, nReportId);
        }
        return Merge(report, nYear, kwhResult, airResult);
    }

    /// <summary>
    /// 純邏輯合併（可單元測試，不觸 DB）：依 bucket index 對齊 kWh 與 Nm³，計算每月與整體比能耗。
    /// 兩邊 bucket 數不一致 → 擲 AirCompressorDeclarationException("bucket_mismatch")（兩邊皆 12 曆月，不一致代表日後演算法分岐）。
    /// Nm³ ≤ 0 → 比能耗為 null（空壓機停機、或產氣量表掉線整月無資料）。
    /// </summary>
    public static AirCompressorDeclarationResult Merge(
        AirCompressorDeclarationModel report, int nYear,
        EnergyReportResult kwhResult, AirMeterUsageReportResult airResult)
    {
        if (kwhResult.buckets.Count != airResult.buckets.Count)
            throw new AirCompressorDeclarationException("bucket_mismatch", "用電與產氣量報表時段數不一致，無法合併");

        var result = new AirCompressorDeclarationResult
        {
            nReportId = report.nId,
            szReportName = report.szName,
            szEnergyCircuitName = kwhResult.szCircuitName,
            szAirMeterCircuitName = airResult.szCircuitName,
            nYear = nYear,
            dtStart = kwhResult.dtStart,
            dtEnd = kwhResult.dtEnd,
            dTotalKwh = kwhResult.dTotalKwh,
            dTotalNm3 = airResult.dTotalNm3,
            isHasKwhWarning = kwhResult.isHasWarning,
            isHasNm3Warning = airResult.isHasWarning,
        };

        for (var i = 0; i < kwhResult.buckets.Count; i++)
        {
            var dKwh = kwhResult.buckets[i].dKwh;
            var dNm3 = airResult.buckets[i].dNm3;
            result.buckets.Add(new AirCompressorDeclarationBucket
            {
                dtBucketStart = kwhResult.buckets[i].dtBucketStart,
                szLabel = kwhResult.buckets[i].szLabel,
                dKwh = dKwh,
                dNm3 = dNm3,
                dKwhPerNm3 = CalcSpecificEnergy(dKwh, dNm3),
                isKwhStale = kwhResult.buckets[i].isStale,
                isNm3Stale = airResult.buckets[i].isStale,
            });
        }

        result.dTotalKwhPerNm3 = CalcSpecificEnergy(result.dTotalKwh, result.dTotalNm3);
        return result;
    }

    /// <summary>比能耗 kWh/Nm³（三位小數）；Nm³ ≤ 0 回 null</summary>
    public static double? CalcSpecificEnergy(double dKwh, double dNm3) =>
        dNm3 > 0 ? Math.Round(dKwh / dNm3, 3) : null;
}
