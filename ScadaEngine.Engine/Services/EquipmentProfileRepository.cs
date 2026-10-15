using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 設備效率診斷 — 讀 EquipmentProfile + EquipmentPointBinding（Engine 只讀，寫入由 Web 設定頁負責）。
/// 聚合服務每次觸發都重讀 → 設定異動不需重啟 Engine。
/// </summary>
public class EquipmentProfileRepository
{
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public EquipmentProfileRepository(DatabaseConfigService configService)
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

    /// <summary>取得所有啟用中的設備（含角色綁定）</summary>
    public async Task<List<EquipmentProfileModel>> GetEnabledProfilesAsync()
    {
        using var conn = await OpenConnectionAsync();
        var profiles = (await conn.QueryAsync<EquipmentProfileModel>(@"
            SELECT Id AS nId, Kind AS szKind, Name AS szName, SortOrder AS nSortOrder, IsEnabled AS isEnabled,
                   RatedOutput AS dRatedOutput, RatedPower AS dRatedPower, MaxKwh AS dMaxKwh,
                   SpecificHeat AS dSpecificHeat, GoodThreshold AS dGoodThreshold, FairThreshold AS dFairThreshold,
                   Description AS szDescription, CreatedAt AS dtCreatedAt, UpdatedAt AS dtUpdatedAt
            FROM   EquipmentProfile
            WHERE  IsEnabled = 1
            ORDER BY Kind, SortOrder, Id")).ToList();
        if (profiles.Count == 0) return profiles;

        var bindings = await conn.QueryAsync<EquipmentPointBindingModel>(@"
            SELECT ProfileId AS nProfileId, Role AS szRole, SID AS szSID, Unit AS szUnit, Scale AS dScale,
                   UpdatedAt AS dtUpdatedAt
            FROM   EquipmentPointBinding");
        var byProfile = bindings.GroupBy(b => b.nProfileId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var p in profiles)
            p.aBindings = byProfile.TryGetValue(p.nId, out var list) ? list : [];
        return profiles;
    }
}
