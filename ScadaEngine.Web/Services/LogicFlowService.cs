using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.LogicFlow.Models;

namespace ScadaEngine.Web.Services;

public class LogicFlowService
{
    private readonly ILogger<LogicFlowService> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public LogicFlowService(ILogger<LogicFlowService> logger, DatabaseConfigService configService)
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

    // ============ Tree CRUD ============

    /// <summary>取得所有樹節點（平坦清單，前端自行組裝樹狀結構）</summary>
    public async Task<IEnumerable<LogicFlowTreeNode>> GetAllNodesAsync()
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryAsync<LogicFlowTreeNode>(
            "SELECT Id, ParentId, Name, NodeType, SortOrder, IsEnabled FROM LogicFlowTree ORDER BY SortOrder");
    }

    /// <summary>新增節點</summary>
    public async Task<int> CreateNodeAsync(int? nParentId, string szName, string szNodeType, int nSortOrder)
    {
        using var conn = await GetConnectionAsync();
        // 邏輯節點預設停用，資料夾預設啟用
        var isEnabled = szNodeType != "logic";
        var nId = await conn.QuerySingleAsync<int>(@"
            INSERT INTO LogicFlowTree (ParentId, Name, NodeType, SortOrder, IsEnabled)
            OUTPUT INSERTED.Id
            VALUES (@ParentId, @Name, @NodeType, @SortOrder, @IsEnabled)",
            new { ParentId = nParentId, Name = szName, NodeType = szNodeType, SortOrder = nSortOrder, IsEnabled = isEnabled });

        // 若為 logic 節點，一併建立空白 Diagram
        if (szNodeType == "logic")
        {
            await conn.ExecuteAsync(
                "INSERT INTO LogicFlowDiagram (TreeId, DiagramJson) VALUES (@TreeId, @Json)",
                new { TreeId = nId, Json = "{\"nodes\":[],\"edges\":[]}" });
        }

        return nId;
    }

    /// <summary>重新命名節點</summary>
    public async Task<bool> RenameNodeAsync(int nId, string szName)
    {
        using var conn = await GetConnectionAsync();
        var nRows = await conn.ExecuteAsync(
            "UPDATE LogicFlowTree SET Name = @Name, UpdatedAt = GETDATE() WHERE Id = @Id",
            new { Id = nId, Name = szName });
        return nRows > 0;
    }

    /// <summary>刪除節點（含所有子節點與對應 Diagram）</summary>
    public async Task<bool> DeleteNodeAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            // 遞迴收集所有子孫 Id
            var allIds = await conn.QueryAsync<int>(@"
                WITH CTE AS (
                    SELECT Id FROM LogicFlowTree WHERE Id = @Id
                    UNION ALL
                    SELECT t.Id FROM LogicFlowTree t INNER JOIN CTE c ON t.ParentId = c.Id
                )
                SELECT Id FROM CTE", new { Id = nId }, tran);

            var idList = allIds.ToList();
            if (idList.Count == 0) { tran.Rollback(); return false; }

            // 刪除 Diagram
            await conn.ExecuteAsync(
                "DELETE FROM LogicFlowDiagram WHERE TreeId IN @Ids",
                new { Ids = idList }, tran);

            // 刪除樹節點
            await conn.ExecuteAsync(
                "DELETE FROM LogicFlowTree WHERE Id IN @Ids",
                new { Ids = idList }, tran);

            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "刪除 LogicFlowTree 節點 {Id} 時發生錯誤", nId);
            return false;
        }
    }

    /// <summary>
    /// 複製節點（資料夾遞迴含所有子孫；邏輯連同 Diagram）到指定父層。
    /// 複製出的 Diagram 一律先過 <see cref="LogicFlowDiagramBindingStripper"/> 清除點位綁定 ——
    /// 綁定殘留 = 新邏輯一啟用就寫進來源設備的 Modbus 暫存器（決策 4：清綁定在後端做）。
    /// </summary>
    /// <param name="nSourceId">來源節點 Id</param>
    /// <param name="nTargetParentId">目標父層 Id；null = 貼到根層</param>
    /// <param name="szCopySuffix">複製後綴（i18n，如「 - 複製」）；同層重名時再補「 (2)」遞增</param>
    /// <returns>新節點 Id；來源不存在回 null</returns>
    public async Task<int?> CopyNodeAsync(int nSourceId, int? nTargetParentId, string szCopySuffix)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            // 來源子樹（含自身）；依 Id 排序確保父節點先於子節點建立
            var srcNodes = (await conn.QueryAsync<LogicFlowTreeNode>(@"
                WITH CTE AS (
                    SELECT Id, ParentId, Name, NodeType, SortOrder, IsEnabled
                    FROM LogicFlowTree WHERE Id = @Id
                    UNION ALL
                    SELECT t.Id, t.ParentId, t.Name, t.NodeType, t.SortOrder, t.IsEnabled
                    FROM LogicFlowTree t INNER JOIN CTE c ON t.ParentId = c.Id
                )
                SELECT Id, ParentId, Name, NodeType, SortOrder, IsEnabled FROM CTE ORDER BY Id",
                new { Id = nSourceId }, tran)).ToList();

            if (srcNodes.Count == 0) { tran.Rollback(); return null; }

            var srcRoot = srcNodes.First(n => n.Id == nSourceId);

            // 目標父層必須存在且為資料夾（決策 8 的退回同層由前端解析，這裡只做防呆）
            if (nTargetParentId.HasValue)
            {
                var szParentType = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT NodeType FROM LogicFlowTree WHERE Id = @Id",
                    new { Id = nTargetParentId.Value }, tran);
                if (szParentType != "folder") { tran.Rollback(); return null; }
            }

            // 同層既有名稱 → 決定唯一名稱；排序接在最後
            var siblings = (await conn.QueryAsync<string>(
                nTargetParentId.HasValue
                    ? "SELECT Name FROM LogicFlowTree WHERE ParentId = @ParentId"
                    : "SELECT Name FROM LogicFlowTree WHERE ParentId IS NULL",
                new { ParentId = nTargetParentId }, tran)).ToList();

            var szNewRootName = MakeUniqueName(srcRoot.Name, szCopySuffix, siblings);
            var nRootSortOrder = siblings.Count;

            // 逐節點建立，idMap 把來源 ParentId 對應到新節點 Id
            var idMap = new Dictionary<int, int>();
            var logicIds = new List<(int nSourceId, int nNewId)>();

            foreach (var src in srcNodes)
            {
                var isRoot = src.Id == nSourceId;
                // 邏輯一律停用（決策 3：點位還沒綁，啟用等於讓 Engine 排一條不完整的邏輯）
                var isEnabled = src.NodeType != "logic";

                int? nParentId = isRoot
                    ? nTargetParentId
                    : (src.ParentId.HasValue && idMap.TryGetValue(src.ParentId.Value, out var nMapped) ? nMapped : null);

                var nNewId = await conn.QuerySingleAsync<int>(@"
                    INSERT INTO LogicFlowTree (ParentId, Name, NodeType, SortOrder, IsEnabled)
                    OUTPUT INSERTED.Id
                    VALUES (@ParentId, @Name, @NodeType, @SortOrder, @IsEnabled)",
                    new
                    {
                        ParentId = nParentId,
                        Name = isRoot ? szNewRootName : src.Name,
                        NodeType = src.NodeType,
                        SortOrder = isRoot ? nRootSortOrder : src.SortOrder,
                        IsEnabled = isEnabled
                    }, tran);

                idMap[src.Id] = nNewId;
                if (src.NodeType == "logic") logicIds.Add((src.Id, nNewId));
            }

            // 逐條複製流程圖（清綁定後寫入，Version 從 0 起算）
            foreach (var (nSrcLogicId, nNewLogicId) in logicIds)
            {
                var szJson = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT DiagramJson FROM LogicFlowDiagram WHERE TreeId = @TreeId",
                    new { TreeId = nSrcLogicId }, tran);

                await conn.ExecuteAsync(
                    "INSERT INTO LogicFlowDiagram (TreeId, DiagramJson, Version) VALUES (@TreeId, @Json, 0)",
                    new { TreeId = nNewLogicId, Json = LogicFlowDiagramBindingStripper.Strip(szJson) }, tran);
            }

            tran.Commit();
            return idMap[nSourceId];
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "複製 LogicFlowTree 節點 {Id} 到父層 {ParentId} 時發生錯誤", nSourceId, nTargetParentId);
            return null;
        }
    }

    /// <summary>「原名 + 後綴」；同層已存在則遞增為「原名 + 後綴 (2)」、「(3)」…</summary>
    private static string MakeUniqueName(string szBaseName, string szCopySuffix, IEnumerable<string> siblingNames)
    {
        var used = new HashSet<string>(siblingNames, StringComparer.OrdinalIgnoreCase);
        var szCandidate = szBaseName + szCopySuffix;
        var n = 2;
        while (used.Contains(szCandidate))
            szCandidate = $"{szBaseName}{szCopySuffix} ({n++})";
        // Name 欄位為 nvarchar(100)，超長截斷避免 INSERT 直接炸
        return szCandidate.Length > 100 ? szCandidate[..100] : szCandidate;
    }

    /// <summary>批次更新排序（前端拖曳排序後整批送回）</summary>
    public async Task<bool> UpdateSortOrderAsync(IEnumerable<(int nId, int nSortOrder)> sortList)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            foreach (var (nId, nSortOrder) in sortList)
            {
                await conn.ExecuteAsync(
                    "UPDATE LogicFlowTree SET SortOrder = @SortOrder, UpdatedAt = GETDATE() WHERE Id = @Id",
                    new { Id = nId, SortOrder = nSortOrder }, tran);
            }
            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "更新 LogicFlowTree 排序時發生錯誤");
            return false;
        }
    }

    /// <summary>切換啟用/停用（含所有子孫節點）</summary>
    public async Task<bool> ToggleEnabledAsync(int nId, bool isEnabled)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            // 遞迴收集自身與所有子孫 Id
            var allIds = await conn.QueryAsync<int>(@"
                WITH CTE AS (
                    SELECT Id FROM LogicFlowTree WHERE Id = @Id
                    UNION ALL
                    SELECT t.Id FROM LogicFlowTree t INNER JOIN CTE c ON t.ParentId = c.Id
                )
                SELECT Id FROM CTE", new { Id = nId }, tran);

            var idList = allIds.ToList();
            if (idList.Count == 0) { tran.Rollback(); return false; }

            await conn.ExecuteAsync(
                "UPDATE LogicFlowTree SET IsEnabled = @IsEnabled, UpdatedAt = GETDATE() WHERE Id IN @Ids",
                new { IsEnabled = isEnabled, Ids = idList }, tran);

            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "切換 LogicFlowTree 節點 {Id} 啟用狀態時發生錯誤", nId);
            return false;
        }
    }

    public const string ErrMoveMissingNode = "move_missing_node";
    public const string ErrMoveTargetNotFolder = "move_target_not_folder";

    /// <summary>
    /// 搬移節點（前端拖曳）：改 ParentId + 插到目標層第 nSortOrder 位，新舊兩層 SortOrder 重新編號。
    /// 防呆（違規丟 <see cref="TreeSortException"/>）：
    ///   - 目標父層必須存在且為 folder（logic 不能有子項）；null = 根層
    ///   - 不能搬到自己或自己的子孫底下（循環）
    ///   - 目標資料夾已停用 → 搬入的子樹一併停用（Engine 只看每條 logic 自己的 IsEnabled，
    ///     不看父資料夾；不連動會變成「UI 看起來關了、Engine 還在跑」）
    /// </summary>
    /// <returns>因目標資料夾停用而被一併停用的節點數（0 = 無連動）</returns>
    public async Task<int> MoveNodeAsync(int nId, int? nTargetParentId, int nSortOrder)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var all = (await conn.QueryAsync<LogicFlowTreeNode>(
                "SELECT Id, ParentId, Name, NodeType, SortOrder, IsEnabled FROM LogicFlowTree",
                transaction: tran)).ToList();

            var node = all.FirstOrDefault(n => n.Id == nId)
                ?? throw new TreeSortException(ErrMoveMissingNode);

            LogicFlowTreeNode? target = null;
            if (nTargetParentId.HasValue)
            {
                target = all.FirstOrDefault(n => n.Id == nTargetParentId.Value);
                if (target == null || target.NodeType != "folder")
                    throw new TreeSortException(ErrMoveTargetNotFolder);
            }

            // 循環 / 存在性
            TreeSortValidator.Validate(
                all.Select(n => (n.Id, n.ParentId)),
                new[] { (nId, nTargetParentId, nSortOrder) },
                allowMultiRoot: true);

            var oldParentId = node.ParentId;
            var isParentChanged = oldParentId != nTargetParentId;

            // 新層：排除自己後依現有順序插入
            var siblings = all.Where(n => n.ParentId == nTargetParentId && n.Id != nId)
                .OrderBy(n => n.SortOrder).ThenBy(n => n.Id).ToList();
            var nInsertAt = Math.Clamp(nSortOrder, 0, siblings.Count);
            siblings.Insert(nInsertAt, node);

            for (var i = 0; i < siblings.Count; i++)
            {
                await conn.ExecuteAsync(
                    "UPDATE LogicFlowTree SET ParentId = @ParentId, SortOrder = @SortOrder, UpdatedAt = GETDATE() WHERE Id = @Id",
                    new { Id = siblings[i].Id, ParentId = nTargetParentId, SortOrder = i }, tran);
            }

            // 舊層補洞
            if (isParentChanged)
            {
                var oldSiblings = all.Where(n => n.ParentId == oldParentId && n.Id != nId)
                    .OrderBy(n => n.SortOrder).ThenBy(n => n.Id).ToList();
                for (var i = 0; i < oldSiblings.Count; i++)
                {
                    await conn.ExecuteAsync(
                        "UPDATE LogicFlowTree SET SortOrder = @SortOrder, UpdatedAt = GETDATE() WHERE Id = @Id",
                        new { Id = oldSiblings[i].Id, SortOrder = i }, tran);
                }
            }

            // 目標資料夾停用 → 子樹連動停用
            var nDisabled = 0;
            if (target != null && !target.IsEnabled)
            {
                nDisabled = await conn.ExecuteAsync(@"
                    WITH CTE AS (
                        SELECT Id FROM LogicFlowTree WHERE Id = @Id
                        UNION ALL
                        SELECT t.Id FROM LogicFlowTree t INNER JOIN CTE c ON t.ParentId = c.Id
                    )
                    UPDATE LogicFlowTree SET IsEnabled = 0, UpdatedAt = GETDATE()
                    WHERE Id IN (SELECT Id FROM CTE) AND IsEnabled = 1", new { Id = nId }, tran);
            }

            tran.Commit();
            return nDisabled;
        }
        catch (TreeSortException)
        {
            tran.Rollback();
            throw;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "搬移 LogicFlowTree 節點 {Id} 到父層 {ParentId} 時發生錯誤", nId, nTargetParentId);
            throw;
        }
    }

    // ============ Diagram CRUD ============

    /// <summary>取得流程圖 JSON</summary>
    public async Task<LogicFlowDiagramDto?> GetDiagramAsync(int nTreeId)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<LogicFlowDiagramDto>(
            "SELECT TreeId, DiagramJson, Version FROM LogicFlowDiagram WHERE TreeId = @TreeId",
            new { TreeId = nTreeId });
    }

    /// <summary>儲存流程圖 JSON（樂觀鎖）</summary>
    public async Task<bool> SaveDiagramAsync(int nTreeId, string szDiagramJson, int nExpectedVersion)
    {
        using var conn = await GetConnectionAsync();
        var nRows = await conn.ExecuteAsync(@"
            UPDATE LogicFlowDiagram
            SET DiagramJson = @Json, Version = Version + 1, UpdatedAt = GETDATE()
            WHERE TreeId = @TreeId AND Version = @ExpectedVersion",
            new { TreeId = nTreeId, Json = szDiagramJson, ExpectedVersion = nExpectedVersion });
        return nRows > 0;
    }

    // ============ 歷史值查詢（前端預覽用） ============

    /// <summary>取得某點位「N 分鐘前」的歷史值（與 Engine 同規則：目標時間往前 5 分鐘窗內
    /// 最近一筆 Quality=1；查無 → found=false 視為 Bad）</summary>
    public async Task<(bool isFound, double dValue, DateTime dtTimestamp)> GetHistoryValueAtAsync(string szSid, int nOffsetMinutes)
    {
        using var conn = await GetConnectionAsync();
        var dtTarget = DateTime.Now.AddMinutes(-nOffsetMinutes);
        var row = await conn.QuerySingleOrDefaultAsync<(double dValue, DateTime dtTimestamp)>(@"
            SELECT TOP 1 CAST(Value AS float), Timestamp
            FROM HistoryData
            WHERE SID = @Sid AND Quality = 1
              AND Timestamp BETWEEN @WindowStart AND @Target
            ORDER BY Timestamp DESC",
            new { Sid = szSid, WindowStart = dtTarget.AddMinutes(-5), Target = dtTarget });

        // 無資料時 Dapper 回傳 default tuple（Timestamp = DateTime.MinValue）
        return row.dtTimestamp == default
            ? (false, 0, DateTime.MinValue)
            : (true, row.dValue, row.dtTimestamp);
    }
}
