using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 電表/迴路階層 CRUD — 自參照樹結構，葉子綁 SID。
/// </summary>
public class EnergyCircuitService
{
    private readonly ILogger<EnergyCircuitService> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public EnergyCircuitService(ILogger<EnergyCircuitService> logger, DatabaseConfigService configService)
    {
        _logger = logger;
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

    /// <summary>取得所有節點（平坦清單，前端組樹）</summary>
    public async Task<IEnumerable<EnergyCircuitModel>> GetAllAsync()
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryAsync<EnergyCircuitModel>(@"
            SELECT  Id               AS nId,
                    Name             AS szName,
                    ParentId         AS nParentId,
                    SortOrder        AS nSortOrder,
                    SID              AS szSID,
                    MaxKwh           AS dMaxKwh,
                    [Sign]           AS nSign,
                    IsDemandEnabled  AS isIsDemandEnabled,
                    IsMainMeter      AS isIsMainMeter,
                    VoltageSID       AS szVoltageSID,
                    CurrentSID       AS szCurrentSID,
                    PowerSID         AS szPowerSID,
                    PowerFactorSID   AS szPowerFactorSID,
                    Description      AS szDescription,
                    CreatedAt        AS dtCreatedAt,
                    UpdatedAt        AS dtUpdatedAt
            FROM    EnergyCircuit
            ORDER BY ParentId, SortOrder");
    }

    /// <summary>取得單筆節點</summary>
    public async Task<EnergyCircuitModel?> GetByIdAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<EnergyCircuitModel>(@"
            SELECT  Id               AS nId,
                    Name             AS szName,
                    ParentId         AS nParentId,
                    SortOrder        AS nSortOrder,
                    SID              AS szSID,
                    MaxKwh           AS dMaxKwh,
                    [Sign]           AS nSign,
                    IsDemandEnabled  AS isIsDemandEnabled,
                    IsMainMeter      AS isIsMainMeter,
                    VoltageSID       AS szVoltageSID,
                    CurrentSID       AS szCurrentSID,
                    PowerSID         AS szPowerSID,
                    PowerFactorSID   AS szPowerFactorSID,
                    Description      AS szDescription,
                    CreatedAt        AS dtCreatedAt,
                    UpdatedAt        AS dtUpdatedAt
            FROM    EnergyCircuit
            WHERE   Id = @Id", new { Id = nId });
    }

    /// <summary>新增節點。若未指定 SortOrder，自動排到同層末端。根節點強制 Sign=1。主要電表全系統唯一：勾選時同交易內先取消其他列。</summary>
    public async Task<int> CreateAsync(EnergyCircuitModel model)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var nNextSort = await conn.ExecuteScalarAsync<int>(@"
                SELECT ISNULL(MAX(SortOrder), -1) + 1
                FROM   EnergyCircuit
                WHERE  (ParentId = @ParentId) OR (@ParentId IS NULL AND ParentId IS NULL)",
                new { ParentId = model.nParentId }, tran);

            var nSign = NormalizeSign(model.nSign, model.nParentId);

            if (model.isIsMainMeter)
                await conn.ExecuteAsync(
                    "UPDATE EnergyCircuit SET IsMainMeter = 0, UpdatedAt = GETDATE() WHERE IsMainMeter = 1", transaction: tran);

            var nId = await conn.QuerySingleAsync<int>(@"
                INSERT INTO EnergyCircuit (Name, ParentId, SortOrder, SID, MaxKwh, [Sign], IsDemandEnabled, IsMainMeter, VoltageSID, CurrentSID, PowerSID, PowerFactorSID, Description, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@Name, @ParentId, @SortOrder, @SID, @MaxKwh, @Sign, @IsDemandEnabled, @IsMainMeter, @VoltageSID, @CurrentSID, @PowerSID, @PowerFactorSID, @Description, GETDATE())",
                new
                {
                    Name = model.szName,
                    ParentId = model.nParentId,
                    SortOrder = nNextSort,
                    SID = model.szSID,
                    MaxKwh = model.dMaxKwh,
                    Sign = nSign,
                    IsDemandEnabled = model.isIsDemandEnabled,
                    IsMainMeter = model.isIsMainMeter,
                    VoltageSID = model.szVoltageSID,
                    CurrentSID = model.szCurrentSID,
                    PowerSID = model.szPowerSID,
                    PowerFactorSID = model.szPowerFactorSID,
                    Description = model.szDescription
                }, tran);
            tran.Commit();
            return nId;
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }

    /// <summary>更新節點（不動 ParentId / SortOrder，改用 MoveAsync）。根節點強制 Sign=1。主要電表全系統唯一：勾選時同交易內先取消其他列。</summary>
    public async Task<bool> UpdateAsync(int nId, string szName, string? szSID, double? dMaxKwh, int nSign, bool isIsDemandEnabled, bool isIsMainMeter,
        string? szVoltageSID, string? szCurrentSID, string? szPowerSID, string? szPowerFactorSID, string? szDescription)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var nParentId = await conn.ExecuteScalarAsync<int?>(
                "SELECT ParentId FROM EnergyCircuit WHERE Id = @Id", new { Id = nId }, tran);
            var nSignNormalized = NormalizeSign(nSign, nParentId);

            if (isIsMainMeter)
                await conn.ExecuteAsync(
                    "UPDATE EnergyCircuit SET IsMainMeter = 0, UpdatedAt = GETDATE() WHERE IsMainMeter = 1 AND Id <> @Id",
                    new { Id = nId }, tran);

            var nRows = await conn.ExecuteAsync(@"
                UPDATE  EnergyCircuit
                SET     Name = @Name,
                        SID = @SID,
                        MaxKwh = @MaxKwh,
                        [Sign] = @Sign,
                        IsDemandEnabled = @IsDemandEnabled,
                        IsMainMeter = @IsMainMeter,
                        VoltageSID = @VoltageSID,
                        CurrentSID = @CurrentSID,
                        PowerSID = @PowerSID,
                        PowerFactorSID = @PowerFactorSID,
                        Description = @Description,
                        UpdatedAt = GETDATE()
                WHERE   Id = @Id",
                new { Id = nId, Name = szName, SID = szSID, MaxKwh = dMaxKwh, Sign = nSignNormalized, IsDemandEnabled = isIsDemandEnabled, IsMainMeter = isIsMainMeter,
                      VoltageSID = szVoltageSID, CurrentSID = szCurrentSID, PowerSID = szPowerSID, PowerFactorSID = szPowerFactorSID, Description = szDescription }, tran);
            tran.Commit();
            return nRows > 0;
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }

    /// <summary>規格化 sign：限定 ±1，根節點強制為 +1。</summary>
    private static int NormalizeSign(int nSign, int? nParentId)
    {
        if (nParentId == null) return 1;
        return nSign == -1 ? -1 : 1;
    }

    /// <summary>刪除節點（含所有子孫）</summary>
    public async Task<bool> DeleteAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var allIds = (await conn.QueryAsync<int>(@"
                WITH CTE AS (
                    SELECT Id FROM EnergyCircuit WHERE Id = @Id
                    UNION ALL
                    SELECT t.Id FROM EnergyCircuit t INNER JOIN CTE c ON t.ParentId = c.Id
                )
                SELECT Id FROM CTE", new { Id = nId }, tran)).ToList();

            if (allIds.Count == 0) { tran.Rollback(); return false; }

            await conn.ExecuteAsync("DELETE FROM EnergyCircuit WHERE Id IN @Ids",
                new { Ids = allIds }, tran);
            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "刪除 EnergyCircuit 節點 {Id} 失敗", nId);
            return false;
        }
    }

    /// <summary>取得主要電表（IsMainMeter = 1，全系統唯一）；未設定時回 null</summary>
    public async Task<EnergyCircuitModel?> GetMainMeterAsync()
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<EnergyCircuitModel>(@"
            SELECT TOP 1
                    Id               AS nId,
                    Name             AS szName,
                    ParentId         AS nParentId,
                    SortOrder        AS nSortOrder,
                    SID              AS szSID,
                    MaxKwh           AS dMaxKwh,
                    [Sign]           AS nSign,
                    IsDemandEnabled  AS isIsDemandEnabled,
                    IsMainMeter      AS isIsMainMeter,
                    VoltageSID       AS szVoltageSID,
                    CurrentSID       AS szCurrentSID,
                    PowerSID         AS szPowerSID,
                    PowerFactorSID   AS szPowerFactorSID,
                    Description      AS szDescription,
                    CreatedAt        AS dtCreatedAt,
                    UpdatedAt        AS dtUpdatedAt
            FROM    EnergyCircuit
            WHERE   IsMainMeter = 1");
    }

    /// <summary>檢查節點是否有子節點</summary>
    public async Task<bool> HasChildrenAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        var nCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM EnergyCircuit WHERE ParentId = @Id", new { Id = nId });
        return nCount > 0;
    }

    /// <summary>
    /// 批次搬移 / 排序（前端拖曳完成後整批送回，每筆含 ParentId 才能處理跨層搬動）。
    /// 寫入前先以 <see cref="TreeSortValidator"/> 驗整棵樹（節點 / 父節點存在、無循環、允許多根）；根節點強制 Sign=+1。
    /// 防呆違規丟 <see cref="TreeSortException"/>（Controller 轉 400 + 錯誤代碼）；其他例外 rollback 後回 false。
    /// </summary>
    public async Task<bool> UpdateSortOrderAsync(IEnumerable<(int nId, int? nParentId, int nSortOrder)> sortList)
    {
        var items = sortList.ToList();
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var current = (await conn.QueryAsync<(int nId, int? nParentId)>(
                "SELECT Id, ParentId FROM EnergyCircuit", transaction: tran)).ToList();
            TreeSortValidator.Validate(current, items, allowMultiRoot: true);

            foreach (var (nId, nParentId, nSortOrder) in items)
            {
                await conn.ExecuteAsync(@"
                    UPDATE EnergyCircuit
                    SET    ParentId = @ParentId, SortOrder = @SortOrder, [Sign] = CASE WHEN @ParentId IS NULL THEN 1 ELSE [Sign] END, UpdatedAt = GETDATE()
                    WHERE  Id = @Id",
                    new { Id = nId, ParentId = nParentId, SortOrder = nSortOrder }, tran);
            }
            tran.Commit();
            return true;
        }
        catch (TreeSortException)
        {
            tran.Rollback();
            throw;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "更新 EnergyCircuit 排序失敗");
            return false;
        }
    }

    /// <summary>取得指定節點的直接子節點（不遞迴）— 給 Excel 匯出多欄展開用</summary>
    public async Task<List<EnergyCircuitModel>> GetDirectChildrenAsync(int nParentId)
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<EnergyCircuitModel>(@"
            SELECT  Id               AS nId,
                    Name             AS szName,
                    ParentId         AS nParentId,
                    SortOrder        AS nSortOrder,
                    SID              AS szSID,
                    MaxKwh           AS dMaxKwh,
                    [Sign]           AS nSign,
                    IsDemandEnabled  AS isIsDemandEnabled,
                    IsMainMeter      AS isIsMainMeter,
                    Description      AS szDescription,
                    CreatedAt        AS dtCreatedAt,
                    UpdatedAt        AS dtUpdatedAt
            FROM    EnergyCircuit
            WHERE   ParentId = @Id
            ORDER BY SortOrder, Id", new { Id = nParentId });
        return rows.ToList();
    }

    /// <summary>葉子展開結果 — 葉子節點 + 從查詢根到葉子路徑上 sign 的乘積（不含查詢根本身的 sign）。</summary>
    public record LeafWithSign(EnergyCircuitModel Leaf, int nEffectiveSign);

    /// <summary>
    /// 展開指定迴路下的所有葉子（綁 SID 的節點）。虛擬迴路會遞迴展開。
    /// 每筆附帶從查詢根到該葉子的 sign 乘積（不含查詢根自己的 sign — 查詢根對自己沒有方向意義）。
    /// 也帶回葉子自身的 V/I/P/PF 電表資訊 SIDs，供虛擬主要電表聚合使用（一般報表計算會忽略這 4 欄）。
    /// </summary>
    public async Task<List<LeafWithSign>> GetLeavesUnderAsync(int nCircuitId)
    {
        using var conn = await GetConnectionAsync();
        // 遞迴 CTE：anchor 為查詢根，EffectiveSign 起始 1（不含自己的 sign）；
        // 子層 EffectiveSign = 父 EffectiveSign × 自己的 Sign。
        var rows = await conn.QueryAsync<(int nId, string szName, int? nParentId, int nSortOrder,
            string? szSID, double? dMaxKwh, int nSign,
            string? szVoltageSID, string? szCurrentSID, string? szPowerSID, string? szPowerFactorSID,
            string? szDescription, DateTime dtCreatedAt, DateTime? dtUpdatedAt, int nEffectiveSign)>(@"
            ;WITH CTE AS (
                SELECT Id, Name, ParentId, SortOrder, SID, MaxKwh, [Sign],
                       VoltageSID, CurrentSID, PowerSID, PowerFactorSID,
                       Description, CreatedAt, UpdatedAt,
                       CAST(1 AS INT) AS EffectiveSign
                FROM   EnergyCircuit WHERE Id = @Id
                UNION ALL
                SELECT t.Id, t.Name, t.ParentId, t.SortOrder, t.SID, t.MaxKwh, t.[Sign],
                       t.VoltageSID, t.CurrentSID, t.PowerSID, t.PowerFactorSID,
                       t.Description, t.CreatedAt, t.UpdatedAt,
                       CAST(c.EffectiveSign * t.[Sign] AS INT) AS EffectiveSign
                FROM   EnergyCircuit t INNER JOIN CTE c ON t.ParentId = c.Id
            )
            SELECT  Id             AS nId,
                    Name           AS szName,
                    ParentId       AS nParentId,
                    SortOrder      AS nSortOrder,
                    SID            AS szSID,
                    MaxKwh         AS dMaxKwh,
                    [Sign]         AS nSign,
                    VoltageSID     AS szVoltageSID,
                    CurrentSID     AS szCurrentSID,
                    PowerSID       AS szPowerSID,
                    PowerFactorSID AS szPowerFactorSID,
                    Description    AS szDescription,
                    CreatedAt      AS dtCreatedAt,
                    UpdatedAt      AS dtUpdatedAt,
                    EffectiveSign  AS nEffectiveSign
            FROM    CTE
            WHERE   SID IS NOT NULL AND LEN(SID) > 0",
            new { Id = nCircuitId });

        return rows.Select(r => new LeafWithSign(
            new EnergyCircuitModel
            {
                nId = r.nId,
                szName = r.szName,
                nParentId = r.nParentId,
                nSortOrder = r.nSortOrder,
                szSID = r.szSID,
                dMaxKwh = r.dMaxKwh,
                nSign = r.nSign,
                szVoltageSID = r.szVoltageSID,
                szCurrentSID = r.szCurrentSID,
                szPowerSID = r.szPowerSID,
                szPowerFactorSID = r.szPowerFactorSID,
                szDescription = r.szDescription,
                dtCreatedAt = r.dtCreatedAt,
                dtUpdatedAt = r.dtUpdatedAt
            },
            r.nEffectiveSign
        )).ToList();
    }
}
