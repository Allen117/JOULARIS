using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 設備效率診斷 — 設備主檔 + 角色綁定 CRUD（EquipmentProfile / EquipmentPointBinding）。
/// 整台存檔在同一交易內（主檔 + 綁定全刪全寫），Scale 一律由伺服端依單位重算（前端不可指定係數）。
/// Engine 每小時重讀這兩張表，存檔後下一個整點生效（不需重啟）。
/// </summary>
public class EquipmentProfileService
{
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    private const string SELECT_PROFILE = @"
        SELECT Id AS nId, Kind AS szKind, Name AS szName, SortOrder AS nSortOrder, IsEnabled AS isEnabled,
               RatedOutput AS dRatedOutput, RatedPower AS dRatedPower, MaxKwh AS dMaxKwh,
               SpecificHeat AS dSpecificHeat, GoodThreshold AS dGoodThreshold, FairThreshold AS dFairThreshold,
               Description AS szDescription, ControlType AS szControlType, MinLoadPct AS dMinLoadPct,
               MaxPressure AS dMaxPressure, MaxStartsPerHour AS nMaxStartsPerHour,
               CreatedAt AS dtCreatedAt, UpdatedAt AS dtUpdatedAt
        FROM   EquipmentProfile";

    public EquipmentProfileService(DatabaseConfigService configService)
    {
        _configService = configService;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>取得設備（含綁定）；szKind = null 全部；isEnabledOnly 只回啟用中</summary>
    public async Task<List<EquipmentProfileModel>> GetAllAsync(string? szKind = null, bool isEnabledOnly = false)
    {
        using var conn = await GetConnectionAsync();
        var szWhere = new List<string>();
        if (szKind != null) szWhere.Add("Kind = @Kind");
        if (isEnabledOnly) szWhere.Add("IsEnabled = 1");
        var szSql = SELECT_PROFILE
            + (szWhere.Count > 0 ? " WHERE " + string.Join(" AND ", szWhere) : string.Empty)
            + " ORDER BY Kind, SortOrder, Id";
        var profiles = (await conn.QueryAsync<EquipmentProfileModel>(szSql, new { Kind = szKind })).ToList();
        if (profiles.Count == 0) return profiles;

        var ids = profiles.Select(p => p.nId).ToArray();
        var bindings = await conn.QueryAsync<EquipmentPointBindingModel>(@"
            SELECT ProfileId AS nProfileId, Role AS szRole, SID AS szSID, Unit AS szUnit, Scale AS dScale,
                   UpdatedAt AS dtUpdatedAt
            FROM   EquipmentPointBinding
            WHERE  ProfileId IN @Ids", new { Ids = ids });
        var byProfile = bindings.GroupBy(b => b.nProfileId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var p in profiles)
            p.aBindings = byProfile.TryGetValue(p.nId, out var list) ? list : [];
        return profiles;
    }

    public async Task<EquipmentProfileModel?> GetByIdAsync(int nId)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(p => p.nId == nId);
    }

    /// <summary>
    /// 驗證 + 正規化（純函式，供單元測試）：kind 合法、名稱必填、角色屬於該 kind、
    /// 單位可換算（Scale 由單位重算）、必要角色齊備、門檻 Good ≤ Fair、數值非負。
    /// </summary>
    public static void ValidateAndNormalize(EquipmentProfileModel m)
    {
        if (!EquipmentKind.IsValid(m.szKind)) throw new EquipmentProfileValidationException("invalid_kind", m.szKind);
        m.szName = (m.szName ?? string.Empty).Trim();
        if (m.szName.Length == 0) throw new EquipmentProfileValidationException("name_required");
        if (m.szName.Length > 100) throw new EquipmentProfileValidationException("name_too_long");

        foreach (var (dValue, szField) in new[]
                 {
                     (m.dRatedOutput, "rated_output"), (m.dRatedPower, "rated_power"), (m.dMaxKwh, "max_kwh"),
                     (m.dSpecificHeat, "specific_heat"), (m.dGoodThreshold, "good"), (m.dFairThreshold, "fair"),
                     (m.dMinLoadPct, "min_load_pct"), (m.dMaxPressure, "max_pressure"), ((double?)m.nMaxStartsPerHour, "max_starts"),
                 })
        {
            if (dValue != null && (double.IsNaN(dValue.Value) || dValue.Value < 0))
                throw new EquipmentProfileValidationException("negative_value", szField);
        }
        if (m.dSpecificHeat is 0) m.dSpecificHeat = null;

        // 空壓系統診斷欄位：僅空壓機有意義，冰機一律清空；皆可不填（診斷端退回推定 / 觀測值）
        if (m.szKind == EquipmentKind.Compressor)
        {
            m.szControlType = string.IsNullOrWhiteSpace(m.szControlType) ? null : m.szControlType.Trim();
            if (m.szControlType != null && !CompressorControlType.IsValid(m.szControlType))
                throw new EquipmentProfileValidationException("invalid_control_type", m.szControlType);
            if (m.dMinLoadPct is > 100) throw new EquipmentProfileValidationException("min_load_pct_range");
            if (m.dMinLoadPct is 0) m.dMinLoadPct = null;
            if (m.dMaxPressure is 0) m.dMaxPressure = null;
            if (m.nMaxStartsPerHour is 0) m.nMaxStartsPerHour = null;
        }
        else
        {
            m.szControlType = null;
            m.dMinLoadPct = null;
            m.dMaxPressure = null;
            m.nMaxStartsPerHour = null;
        }
        if (m.dGoodThreshold != null && m.dFairThreshold != null && m.dGoodThreshold > m.dFairThreshold)
            throw new EquipmentProfileValidationException("threshold_order");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<EquipmentPointBindingModel>();
        foreach (var b in m.aBindings)
        {
            if (string.IsNullOrWhiteSpace(b.szSID)) continue;   // 空白 = 未綁
            if (!EquipmentKind.HasRole(m.szKind, b.szRole))
                throw new EquipmentProfileValidationException("invalid_role", b.szRole);
            if (!seen.Add(b.szRole)) throw new EquipmentProfileValidationException("duplicate_role", b.szRole);

            var quantity = EquipmentRoles.QuantityOf(b.szRole);
            var szUnit = EquipmentUnitScale.Canonicalize(quantity, b.szUnit);
            var dScale = szUnit == null ? null : EquipmentUnitScale.ScaleOf(quantity, szUnit);
            if (dScale == null) throw new EquipmentProfileValidationException("invalid_unit", $"{b.szRole}:{b.szUnit}");

            cleaned.Add(new EquipmentPointBindingModel
            {
                nProfileId = m.nId,
                szRole = b.szRole,
                szSID = b.szSID.Trim(),
                szUnit = szUnit,
                dScale = dScale.Value,
            });
        }
        m.aBindings = cleaned;

        var missing = EquipmentKind.MissingRequired(m.szKind, cleaned.Select(b => b.szRole).ToList());
        if (missing.Count > 0) throw new EquipmentProfileValidationException("missing_required", string.Join(",", missing));
    }

    /// <summary>新增或更新一台（nId = 0 → 新增）；回傳 Id</summary>
    public async Task<int> SaveAsync(EquipmentProfileModel m)
    {
        ValidateAndNormalize(m);
        using var conn = await GetConnectionAsync();
        using var tx = conn.BeginTransaction();
        var nId = await SaveCoreAsync(conn, tx, m);
        tx.Commit();
        return nId;
    }

    /// <summary>掃描帶入：多台一次寫入（同一交易，任一台驗證失敗整批不寫）；回傳新 Id 清單</summary>
    public async Task<List<int>> CreateManyAsync(List<EquipmentProfileModel> list)
    {
        foreach (var m in list)
        {
            m.nId = 0;
            ValidateAndNormalize(m);
        }
        using var conn = await GetConnectionAsync();
        using var tx = conn.BeginTransaction();
        var ids = new List<int>();
        foreach (var m in list) ids.Add(await SaveCoreAsync(conn, tx, m));
        tx.Commit();
        return ids;
    }

    private static async Task<int> SaveCoreAsync(SqlConnection conn, SqlTransaction tx, EquipmentProfileModel m)
    {
        var param = new
        {
            Id = m.nId,
            Kind = m.szKind,
            Name = m.szName,
            SortOrder = m.nSortOrder,
            IsEnabled = m.isEnabled,
            RatedOutput = m.dRatedOutput,
            RatedPower = m.dRatedPower,
            MaxKwh = m.dMaxKwh,
            SpecificHeat = m.dSpecificHeat,
            GoodThreshold = m.dGoodThreshold,
            FairThreshold = m.dFairThreshold,
            Description = string.IsNullOrWhiteSpace(m.szDescription) ? null : m.szDescription.Trim(),
            ControlType = m.szControlType,
            MinLoadPct = m.dMinLoadPct,
            MaxPressure = m.dMaxPressure,
            MaxStartsPerHour = m.nMaxStartsPerHour,
        };

        int nId;
        if (m.nId <= 0)
        {
            // 新設備排在同類最後；SortOrder 未指定（0）時自動遞增
            var nSort = m.nSortOrder > 0 ? m.nSortOrder : await conn.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM EquipmentProfile WHERE Kind = @Kind", new { Kind = m.szKind }, tx);
            nId = await conn.QuerySingleAsync<int>(@"
                INSERT INTO EquipmentProfile (Kind, Name, SortOrder, IsEnabled, RatedOutput, RatedPower, MaxKwh,
                                              SpecificHeat, GoodThreshold, FairThreshold, Description,
                                              ControlType, MinLoadPct, MaxPressure, MaxStartsPerHour, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@Kind, @Name, @SortOrder, @IsEnabled, @RatedOutput, @RatedPower, @MaxKwh,
                        @SpecificHeat, @GoodThreshold, @FairThreshold, @Description,
                        @ControlType, @MinLoadPct, @MaxPressure, @MaxStartsPerHour, GETDATE())",
                new
                {
                    param.Kind, param.Name, SortOrder = nSort, param.IsEnabled, param.RatedOutput, param.RatedPower,
                    param.MaxKwh, param.SpecificHeat, param.GoodThreshold, param.FairThreshold, param.Description,
                    param.ControlType, param.MinLoadPct, param.MaxPressure, param.MaxStartsPerHour,
                }, tx);
        }
        else
        {
            // Kind 不可經更新改變（冰機改空壓機 = 綁定全錯，應刪除重建）
            var nRows = await conn.ExecuteAsync(@"
                UPDATE EquipmentProfile
                SET    Name = @Name, SortOrder = @SortOrder, IsEnabled = @IsEnabled, RatedOutput = @RatedOutput,
                       RatedPower = @RatedPower, MaxKwh = @MaxKwh, SpecificHeat = @SpecificHeat,
                       GoodThreshold = @GoodThreshold, FairThreshold = @FairThreshold, Description = @Description,
                       ControlType = @ControlType, MinLoadPct = @MinLoadPct, MaxPressure = @MaxPressure,
                       MaxStartsPerHour = @MaxStartsPerHour, UpdatedAt = GETDATE()
                WHERE  Id = @Id AND Kind = @Kind", param, tx);
            if (nRows == 0) throw new EquipmentProfileValidationException("not_found", m.nId.ToString());
            nId = m.nId;
            await conn.ExecuteAsync("DELETE FROM EquipmentPointBinding WHERE ProfileId = @Id", new { Id = nId }, tx);
        }

        foreach (var b in m.aBindings)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO EquipmentPointBinding (ProfileId, Role, SID, Unit, Scale, UpdatedAt)
                VALUES (@ProfileId, @Role, @SID, @Unit, @Scale, GETDATE())",
                new { ProfileId = nId, Role = b.szRole, SID = b.szSID, Unit = b.szUnit, Scale = b.dScale }, tx);
        }
        return nId;
    }

    /// <summary>刪除一台（連同綁定與 hourly 聚合列）</summary>
    public async Task<bool> DeleteAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM EquipmentPointBinding WHERE ProfileId = @Id", new { Id = nId }, tx);
        await conn.ExecuteAsync("DELETE FROM EquipmentHourly WHERE ProfileId = @Id", new { Id = nId }, tx);
        var nRows = await conn.ExecuteAsync("DELETE FROM EquipmentProfile WHERE Id = @Id", new { Id = nId }, tx);
        tx.Commit();
        return nRows > 0;
    }

    /// <summary>空調水系統迴路（WaterCircuit）葉子已綁的 RT 點 SID — 掃描時直接當冰機 RT 角色候選</summary>
    public async Task<List<string>> GetWaterCircuitRtSidsAsync()
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<string>(
            "SELECT DISTINCT SID FROM WaterCircuit WHERE SID IS NOT NULL AND LEN(SID) > 0");
        return rows.ToList();
    }
}
