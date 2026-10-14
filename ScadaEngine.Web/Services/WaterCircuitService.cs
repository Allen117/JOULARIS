using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 空調水系統迴路階層 CRUD — 自參照樹結構，葉子綁 RT 系列點位 SID。
/// 與 EnergyCircuitService 同 pattern，但無 Sign / MaxKwh（空調水系統為瞬時值）。
/// </summary>
public class WaterCircuitService
{
    private readonly ILogger<WaterCircuitService> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public WaterCircuitService(ILogger<WaterCircuitService> logger, DatabaseConfigService configService)
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

    public async Task<IEnumerable<WaterCircuitModel>> GetAllAsync()
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryAsync<WaterCircuitModel>(@"
            SELECT  Id          AS nId,
                    Name        AS szName,
                    ParentId    AS nParentId,
                    SortOrder   AS nSortOrder,
                    SID         AS szSID,
                    Description AS szDescription,
                    CreatedAt   AS dtCreatedAt,
                    UpdatedAt   AS dtUpdatedAt
            FROM    WaterCircuit
            ORDER BY ParentId, SortOrder");
    }

    public async Task<WaterCircuitModel?> GetByIdAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<WaterCircuitModel>(@"
            SELECT  Id          AS nId,
                    Name        AS szName,
                    ParentId    AS nParentId,
                    SortOrder   AS nSortOrder,
                    SID         AS szSID,
                    Description AS szDescription,
                    CreatedAt   AS dtCreatedAt,
                    UpdatedAt   AS dtUpdatedAt
            FROM    WaterCircuit
            WHERE   Id = @Id", new { Id = nId });
    }

    public async Task<int> CreateAsync(WaterCircuitModel model)
    {
        using var conn = await GetConnectionAsync();
        var nNextSort = await conn.ExecuteScalarAsync<int>(@"
            SELECT ISNULL(MAX(SortOrder), -1) + 1
            FROM   WaterCircuit
            WHERE  (ParentId = @ParentId) OR (@ParentId IS NULL AND ParentId IS NULL)",
            new { ParentId = model.nParentId });

        return await conn.QuerySingleAsync<int>(@"
            INSERT INTO WaterCircuit (Name, ParentId, SortOrder, SID, Description, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@Name, @ParentId, @SortOrder, @SID, @Description, GETDATE())",
            new
            {
                Name = model.szName,
                ParentId = model.nParentId,
                SortOrder = nNextSort,
                SID = model.szSID,
                Description = model.szDescription
            });
    }

    public async Task<bool> UpdateAsync(int nId, string szName, string? szSID, string? szDescription)
    {
        using var conn = await GetConnectionAsync();
        var nRows = await conn.ExecuteAsync(@"
            UPDATE  WaterCircuit
            SET     Name = @Name,
                    SID = @SID,
                    Description = @Description,
                    UpdatedAt = GETDATE()
            WHERE   Id = @Id",
            new { Id = nId, Name = szName, SID = szSID, Description = szDescription });
        return nRows > 0;
    }

    public async Task<bool> DeleteAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var allIds = (await conn.QueryAsync<int>(@"
                WITH CTE AS (
                    SELECT Id FROM WaterCircuit WHERE Id = @Id
                    UNION ALL
                    SELECT t.Id FROM WaterCircuit t INNER JOIN CTE c ON t.ParentId = c.Id
                )
                SELECT Id FROM CTE", new { Id = nId }, tran)).ToList();

            if (allIds.Count == 0) { tran.Rollback(); return false; }

            await conn.ExecuteAsync("DELETE FROM WaterCircuit WHERE Id IN @Ids",
                new { Ids = allIds }, tran);
            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "刪除 WaterCircuit 節點 {Id} 失敗", nId);
            return false;
        }
    }

    public async Task<bool> HasChildrenAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        var nCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM WaterCircuit WHERE ParentId = @Id", new { Id = nId });
        return nCount > 0;
    }

    /// <summary>
    /// 批次搬移 / 排序（前端拖曳完成後整批送回，每筆含 ParentId 才能處理跨層搬動）。
    /// 寫入前先以 <see cref="TreeSortValidator"/> 驗整棵樹（節點 / 父節點存在、無循環、允許多根）。
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
                "SELECT Id, ParentId FROM WaterCircuit", transaction: tran)).ToList();
            TreeSortValidator.Validate(current, items, allowMultiRoot: true);

            foreach (var (nId, nParentId, nSortOrder) in items)
            {
                await conn.ExecuteAsync(@"
                    UPDATE WaterCircuit
                    SET    ParentId = @ParentId, SortOrder = @SortOrder, UpdatedAt = GETDATE()
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
            _logger.LogError(ex, "更新 WaterCircuit 排序失敗");
            return false;
        }
    }

    /// <summary>
    /// 取得指定節點的直接子節點（不遞迴）— 給冷凍噸報表 Excel 匯出多欄展開用
    /// </summary>
    public async Task<List<WaterCircuitModel>> GetDirectChildrenAsync(int nParentId)
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<WaterCircuitModel>(@"
            SELECT  Id          AS nId,
                    Name        AS szName,
                    ParentId    AS nParentId,
                    SortOrder   AS nSortOrder,
                    SID         AS szSID,
                    Description AS szDescription,
                    CreatedAt   AS dtCreatedAt,
                    UpdatedAt   AS dtUpdatedAt
            FROM    WaterCircuit
            WHERE   ParentId = @Id
            ORDER BY SortOrder, Id", new { Id = nParentId });
        return rows.ToList();
    }

    /// <summary>
    /// 展開指定迴路下的所有葉子（綁 SID 的節點）— 供冷凍噸報表 on-demand 階層加總使用。
    /// 自參照遞迴 CTE。空調水系統無 sign，純正向加總。
    /// </summary>
    public async Task<List<WaterCircuitModel>> GetLeavesUnderAsync(int nCircuitId)
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<WaterCircuitModel>(@"
            ;WITH CTE AS (
                SELECT Id, Name, ParentId, SortOrder, SID, Description, CreatedAt, UpdatedAt
                FROM   WaterCircuit WHERE Id = @Id
                UNION ALL
                SELECT t.Id, t.Name, t.ParentId, t.SortOrder, t.SID, t.Description, t.CreatedAt, t.UpdatedAt
                FROM   WaterCircuit t INNER JOIN CTE c ON t.ParentId = c.Id
            )
            SELECT  Id          AS nId,
                    Name        AS szName,
                    ParentId    AS nParentId,
                    SortOrder   AS nSortOrder,
                    SID         AS szSID,
                    Description AS szDescription,
                    CreatedAt   AS dtCreatedAt,
                    UpdatedAt   AS dtUpdatedAt
            FROM    CTE
            WHERE   SID IS NOT NULL AND LEN(SID) > 0",
            new { Id = nCircuitId });
        return rows.ToList();
    }
}
