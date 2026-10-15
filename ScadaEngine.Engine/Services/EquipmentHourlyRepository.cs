using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 設備效率診斷 — EquipmentHourly 表的資料存取（UPSERT / 查既有列 / 刪列）。
/// 對標 <see cref="WaterLeafHourlyRepository"/>，key 由 SID 換成 ProfileId。
/// </summary>
public class EquipmentHourlyRepository
{
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public EquipmentHourlyRepository(DatabaseConfigService configService)
    {
        _configService = configService;
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>某設備在 [from, to) 已存在的 HourStart 集合（catch-up 跳過用）</summary>
    public async Task<HashSet<DateTime>> GetExistingHoursAsync(int nProfileId, DateTime dtFrom, DateTime dtTo)
    {
        using var conn = await OpenConnectionAsync();
        var rows = await conn.QueryAsync<DateTime>(@"
            SELECT HourStart FROM EquipmentHourly
            WHERE  ProfileId = @Id AND HourStart >= @From AND HourStart < @To",
            new { Id = nProfileId, From = dtFrom, To = dtTo });
        return new HashSet<DateTime>(rows);
    }

    /// <summary>UPSERT 一列（同 ProfileId + HourStart 已存在則覆寫）</summary>
    public async Task UpsertAsync(EquipmentHourlyModel m)
    {
        using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(@"
            MERGE EquipmentHourly WITH (HOLDLOCK) AS tgt
            USING (SELECT @ProfileId AS ProfileId, @HourStart AS HourStart) AS src
               ON tgt.ProfileId = src.ProfileId AND tgt.HourStart = src.HourStart
            WHEN MATCHED THEN
                UPDATE SET Kwh = @Kwh, OutputQty = @OutputQty, RunHours = @RunHours, AvgLoadPct = @AvgLoadPct,
                           LoadedHours = @LoadedHours, AvgPressure = @AvgPressure, SampleCount = @SampleCount,
                           Quality = @Quality, IsBackfilled = @IsBackfilled, CreatedAt = GETDATE()
            WHEN NOT MATCHED THEN
                INSERT (ProfileId, HourStart, Kwh, OutputQty, RunHours, AvgLoadPct, LoadedHours, AvgPressure,
                        SampleCount, Quality, IsBackfilled, CreatedAt)
                VALUES (@ProfileId, @HourStart, @Kwh, @OutputQty, @RunHours, @AvgLoadPct, @LoadedHours, @AvgPressure,
                        @SampleCount, @Quality, @IsBackfilled, GETDATE());",
            new
            {
                ProfileId = m.nProfileId,
                HourStart = m.dtHourStart,
                Kwh = m.dKwh,
                OutputQty = m.dOutputQty,
                RunHours = m.dRunHours,
                AvgLoadPct = m.dAvgLoadPct,
                LoadedHours = m.dLoadedHours,
                AvgPressure = m.dAvgPressure,
                SampleCount = m.nSampleCount,
                Quality = (byte)m.nQuality,
                IsBackfilled = m.isBackfilled,
            });
    }

    /// <summary>刪除某設備某小時的列（backfill 重算後若改判資料不足，舊列要清掉）</summary>
    public async Task DeleteAsync(int nProfileId, DateTime dtHourStart)
    {
        using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM EquipmentHourly WHERE ProfileId = @Id AND HourStart = @H",
            new { Id = nProfileId, H = dtHourStart });
    }
}
