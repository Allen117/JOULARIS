using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 產氣量表/迴路階層 CRUD — 自參照樹結構，葉子綁 SID（空壓機累積式產氣量 Nm³ totalizer）。
/// 與 GasMeterCircuit（天然氣表，綁氣費）**平行、互不相干** — 壓縮空氣不可借用氣表樹，否則氣費會把它當天然氣計價。
/// 本體系只供空壓機能源申報（比能耗 kWh/Nm³）使用，無期別、無費率。
/// </summary>
public class AirMeterCircuitService
{
    private readonly ILogger<AirMeterCircuitService> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public AirMeterCircuitService(ILogger<AirMeterCircuitService> logger, DatabaseConfigService configService)
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

    private const string SelectColumns = @"
                    Id          AS nId,
                    Name        AS szName,
                    ParentId    AS nParentId,
                    SortOrder   AS nSortOrder,
                    SID         AS szSID,
                    UnitScale   AS dUnitScale,
                    MaxVolume   AS dMaxVolume,
                    [Sign]      AS nSign,
                    Description AS szDescription,
                    CreatedAt   AS dtCreatedAt,
                    UpdatedAt   AS dtUpdatedAt";

    // ============ 產氣量點位判定（單位 + 點位名稱雙條件） ============
    //
    // 判定規則（唯一入口 ResolveAirPointScale）：
    //   ① 單位必須可換算為 Nm³（_unitScaleMap：Nm³ / m³ 系=1、L 系=0.001）
    //   ② 且 點位**名稱**含空壓關鍵字（_nameKeywords）
    //   ③ 且 名稱**不含**天然氣字樣（_excludeKeywords）
    //
    // 為什麼一律要看名稱（與氣表不同，沒有「單位無歧義免名稱」的捷徑）：
    //   Nm³ / m³ / SCM 對天然氣表、自來水表、壓縮空氣都成立，單位字串無從分辨三者，
    //   點位名稱才是現場唯一可靠的訊號。
    // 為什麼要排除天然氣字樣：空壓關鍵字「氣量」也會命中「天然氣氣量」，
    //   錯放 = 比能耗拿天然氣用量當分母。
    // 刻意**不收**「度」：那是 kWh 俗稱（氣表收「度」是因為天然氣帳單 1 度 = 1 m³，空壓無此慣例）。

    /// <summary>
    /// 產氣量單位 → Nm³ 換算係數對照表（key 不分大小寫、比對前先 trim）。
    /// m³ 視同 Nm³（現場壓縮空氣流量計累積值多已換算為標準狀態；系統不做溫壓修正）。
    /// 可擴充：新單位直接加進對照表即可。
    /// </summary>
    private static readonly Dictionary<string, double> _unitScaleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Nm³ / m³ 系 → 1.0
        ["nm³"] = 1.0,
        ["nm3"] = 1.0,
        ["m³"] = 1.0,
        ["m3"] = 1.0,
        ["scm"] = 1.0,
        ["立方公尺"] = 1.0,
        ["立方米"] = 1.0,
        // L 系 → 0.001
        ["l"] = 0.001,
        ["nl"] = 0.001,
        ["liter"] = 0.001,
        ["litre"] = 0.001,
        ["公升"] = 0.001,
    };

    /// <summary>
    /// 點位**名稱**的空壓關鍵字（不分大小寫，子字串比對）。
    /// 刻意**不收單一個「氣」字** — 會誤命中天然氣 / 氣壓 / 冷氣 / 氣溫。可擴充：現場有別的命名慣例直接加進來。
    /// </summary>
    private static readonly string[] _nameKeywords =
    [
        // 中文
        "空壓", "壓縮空氣", "產氣", "氣量",
        // 英文
        "air", "compressor", "fad",
    ];

    /// <summary>
    /// 名稱含以下天然氣字樣 → 一律排除（即使同時命中空壓關鍵字，如「天然氣氣量」）。
    /// 只收**明確指向燃料氣**的詞；「用氣」「氣表」這類空壓現場也可能用的泛稱刻意不收。
    /// </summary>
    private static readonly string[] _excludeKeywords =
    [
        "天然氣", "瓦斯", "燃氣", "石油氣",
        "natural gas", "nat gas", "natgas", "lng", "cng", "lpg",
    ];

    /// <summary>
    /// 單位字串 → Nm³ 換算係數。Nm³/m³ 系回 1.0、L 系回 0.001，非產氣量單位回 null。
    /// ⚠ 這只是**單位換算**，不是「是不是產氣量點位」的判定 — 點位過濾一律走 <see cref="ResolveAirPointScale"/>。
    /// </summary>
    public static double? ResolveUnitScale(string? szUnit)
    {
        if (string.IsNullOrWhiteSpace(szUnit)) return null;
        return _unitScaleMap.TryGetValue(szUnit.Trim(), out var dScale) ? dScale : null;
    }

    /// <summary>點位名稱是否含空壓關鍵字且不含天然氣字樣（不分大小寫）</summary>
    public static bool HasAirNameKeyword(string? szName)
    {
        if (string.IsNullOrWhiteSpace(szName)) return false;
        var sz = szName.Trim();
        foreach (var kw in _excludeKeywords)
        {
            if (sz.Contains(kw, StringComparison.OrdinalIgnoreCase)) return false;
        }
        foreach (var kw in _nameKeywords)
        {
            if (sz.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// **產氣量點位判定的唯一入口** — 是產氣量點位回 Nm³ 換算係數，否則回 null。
    /// 條件：單位可換算 Nm³ **且** 名稱含空壓關鍵字 **且** 名稱不含天然氣字樣。
    /// 下拉過濾與存檔驗證共用同一支，避免「看得到卻存不了」或「繞過下拉硬 POST」。
    /// </summary>
    public static double? ResolveAirPointScale(string? szName, string? szUnit)
    {
        var dScale = ResolveUnitScale(szUnit);
        if (dScale == null) return null;
        return HasAirNameKeyword(szName) ? dScale : null;
    }

    // ============ CRUD ============

    /// <summary>取得所有節點（平坦清單，前端組樹）</summary>
    public async Task<List<AirMeterCircuitModel>> GetAllAsync()
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<AirMeterCircuitModel>($@"
            SELECT {SelectColumns}
            FROM    AirMeterCircuit
            ORDER BY ParentId, SortOrder");
        return rows.ToList();
    }

    /// <summary>取得單筆節點</summary>
    public async Task<AirMeterCircuitModel?> GetByIdAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AirMeterCircuitModel>($@"
            SELECT {SelectColumns}
            FROM    AirMeterCircuit
            WHERE   Id = @Id", new { Id = nId });
    }

    /// <summary>取得第一個根節點（ParentId IS NULL，SortOrder 最小）；無根節點回 null</summary>
    public async Task<AirMeterCircuitModel?> GetRootAsync()
    {
        using var conn = await GetConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<AirMeterCircuitModel>($@"
            SELECT TOP 1 {SelectColumns}
            FROM    AirMeterCircuit
            WHERE   ParentId IS NULL
            ORDER BY SortOrder, Id");
    }

    /// <summary>新增節點。若未指定 SortOrder，自動排到同層末端。根節點強制 Sign=1。</summary>
    public async Task<int> CreateAsync(AirMeterCircuitModel model)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var nNextSort = await conn.ExecuteScalarAsync<int>(@"
                SELECT ISNULL(MAX(SortOrder), -1) + 1
                FROM   AirMeterCircuit
                WHERE  (ParentId = @ParentId) OR (@ParentId IS NULL AND ParentId IS NULL)",
                new { ParentId = model.nParentId }, tran);

            var nSign = NormalizeSign(model.nSign, model.nParentId);

            var nId = await conn.QuerySingleAsync<int>(@"
                INSERT INTO AirMeterCircuit (Name, ParentId, SortOrder, SID, UnitScale, MaxVolume, [Sign], Description, CreatedAt)
                OUTPUT INSERTED.Id
                VALUES (@Name, @ParentId, @SortOrder, @SID, @UnitScale, @MaxVolume, @Sign, @Description, GETDATE())",
                new
                {
                    Name = model.szName,
                    ParentId = model.nParentId,
                    SortOrder = nNextSort,
                    SID = model.szSID,
                    UnitScale = model.dUnitScale,
                    MaxVolume = model.dMaxVolume,
                    Sign = nSign,
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

    /// <summary>更新節點（不動 ParentId / SortOrder，搬動走 UpdateSortOrderAsync）。根節點強制 Sign=1。</summary>
    public async Task<bool> UpdateAsync(AirMeterCircuitModel model)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            // ParentId 以 DB 現況為準（Update 不搬層），據此正規化 sign
            var nParentId = await conn.ExecuteScalarAsync<int?>(
                "SELECT ParentId FROM AirMeterCircuit WHERE Id = @Id", new { Id = model.nId }, tran);
            var nSign = NormalizeSign(model.nSign, nParentId);

            var nRows = await conn.ExecuteAsync(@"
                UPDATE  AirMeterCircuit
                SET     Name = @Name,
                        SID = @SID,
                        UnitScale = @UnitScale,
                        MaxVolume = @MaxVolume,
                        [Sign] = @Sign,
                        Description = @Description,
                        UpdatedAt = GETDATE()
                WHERE   Id = @Id",
                new
                {
                    Id = model.nId,
                    Name = model.szName,
                    SID = model.szSID,
                    UnitScale = model.dUnitScale,
                    MaxVolume = model.dMaxVolume,
                    Sign = nSign,
                    Description = model.szDescription
                }, tran);
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

    /// <summary>刪除節點（含所有子孫，遞迴 CTE 展開）</summary>
    public async Task<bool> DeleteAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var allIds = (await conn.QueryAsync<int>(@"
                WITH CTE AS (
                    SELECT Id FROM AirMeterCircuit WHERE Id = @Id
                    UNION ALL
                    SELECT t.Id FROM AirMeterCircuit t INNER JOIN CTE c ON t.ParentId = c.Id
                )
                SELECT Id FROM CTE", new { Id = nId }, tran)).ToList();

            if (allIds.Count == 0) { tran.Rollback(); return false; }

            await conn.ExecuteAsync("DELETE FROM AirMeterCircuit WHERE Id IN @Ids",
                new { Ids = allIds }, tran);
            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "刪除 AirMeterCircuit 節點 {Id} 失敗", nId);
            return false;
        }
    }

    /// <summary>檢查節點是否有子節點</summary>
    public async Task<bool> HasChildrenAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        var nCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM AirMeterCircuit WHERE ParentId = @Id", new { Id = nId });
        return nCount > 0;
    }

    /// <summary>
    /// 批次搬移 / 排序（前端拖曳完成後整批送回，每筆含 ParentId 才能處理跨層搬動）。
    /// 寫入前先以 <see cref="TreeSortValidator"/> 驗整棵樹（節點 / 父節點存在、無循環、只允許單一根（全廠））；根節點強制 Sign=+1。
    /// 防呆違規丟 <see cref="TreeSortException"/>（Controller 轉 400 + 錯誤代碼）；其他例外 rollback 後rethrow。
    /// </summary>
    public async Task UpdateSortOrderAsync(IEnumerable<(int nId, int? nParentId, int nSortOrder)> sortList)
    {
        var items = sortList.ToList();
        using var conn = await GetConnectionAsync();
        using var tran = conn.BeginTransaction();
        try
        {
            var current = (await conn.QueryAsync<(int nId, int? nParentId)>(
                "SELECT Id, ParentId FROM AirMeterCircuit", transaction: tran)).ToList();
            TreeSortValidator.Validate(current, items, allowMultiRoot: false);

            foreach (var (nId, nParentId, nSortOrder) in items)
            {
                await conn.ExecuteAsync(@"
                    UPDATE AirMeterCircuit
                    SET    ParentId = @ParentId, SortOrder = @SortOrder, [Sign] = CASE WHEN @ParentId IS NULL THEN 1 ELSE [Sign] END, UpdatedAt = GETDATE()
                    WHERE  Id = @Id",
                    new { Id = nId, ParentId = nParentId, SortOrder = nSortOrder }, tran);
            }
            tran.Commit();
        }
        catch (TreeSortException)
        {
            tran.Rollback();
            throw;
        }
        catch (Exception ex)
        {
            tran.Rollback();
            _logger.LogError(ex, "更新 AirMeterCircuit 排序失敗");
            throw;
        }
    }

    /// <summary>取得指定節點的直接子節點（不遞迴）</summary>
    public async Task<List<AirMeterCircuitModel>> GetDirectChildrenAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        var rows = await conn.QueryAsync<AirMeterCircuitModel>($@"
            SELECT {SelectColumns}
            FROM    AirMeterCircuit
            WHERE   ParentId = @Id
            ORDER BY SortOrder, Id", new { Id = nId });
        return rows.ToList();
    }

    /// <summary>葉子展開結果 — 葉子節點 + 從查詢根到葉子路徑上 sign 的乘積（不含查詢根本身的 sign）。</summary>
    public record LeafWithSign(AirMeterCircuitModel Leaf, int nEffectiveSign);

    /// <summary>
    /// 展開指定迴路下的所有葉子（綁 SID 的節點）。虛擬迴路會遞迴展開。
    /// 每筆附帶從查詢根到該葉子的 sign 乘積（不含查詢根自己的 sign — 查詢根對自己沒有方向意義）。
    /// </summary>
    public async Task<List<LeafWithSign>> GetLeavesUnderAsync(int nId)
    {
        using var conn = await GetConnectionAsync();
        // 遞迴 CTE：anchor 為查詢根，EffectiveSign 起始 1（不含自己的 sign）；
        // 子層 EffectiveSign = 父 EffectiveSign × 自己的 Sign。
        var rows = await conn.QueryAsync<(int nId, string szName, int? nParentId, int nSortOrder,
            string? szSID, double dUnitScale, double? dMaxVolume, int nSign,
            string? szDescription, DateTime dtCreatedAt, DateTime? dtUpdatedAt, int nEffectiveSign)>(@"
            ;WITH CTE AS (
                SELECT Id, Name, ParentId, SortOrder, SID, UnitScale, MaxVolume, [Sign],
                       Description, CreatedAt, UpdatedAt,
                       CAST(1 AS INT) AS EffectiveSign
                FROM   AirMeterCircuit WHERE Id = @Id
                UNION ALL
                SELECT t.Id, t.Name, t.ParentId, t.SortOrder, t.SID, t.UnitScale, t.MaxVolume, t.[Sign],
                       t.Description, t.CreatedAt, t.UpdatedAt,
                       CAST(c.EffectiveSign * t.[Sign] AS INT) AS EffectiveSign
                FROM   AirMeterCircuit t INNER JOIN CTE c ON t.ParentId = c.Id
            )
            SELECT  Id            AS nId,
                    Name          AS szName,
                    ParentId      AS nParentId,
                    SortOrder     AS nSortOrder,
                    SID           AS szSID,
                    UnitScale     AS dUnitScale,
                    MaxVolume     AS dMaxVolume,
                    [Sign]        AS nSign,
                    Description   AS szDescription,
                    CreatedAt     AS dtCreatedAt,
                    UpdatedAt     AS dtUpdatedAt,
                    EffectiveSign AS nEffectiveSign
            FROM    CTE
            WHERE   SID IS NOT NULL AND LEN(SID) > 0",
            new { Id = nId });

        return rows.Select(r => new LeafWithSign(
            new AirMeterCircuitModel
            {
                nId = r.nId,
                szName = r.szName,
                nParentId = r.nParentId,
                nSortOrder = r.nSortOrder,
                szSID = r.szSID,
                dUnitScale = r.dUnitScale,
                dMaxVolume = r.dMaxVolume,
                nSign = r.nSign,
                szDescription = r.szDescription,
                dtCreatedAt = r.dtCreatedAt,
                dtUpdatedAt = r.dtUpdatedAt
            },
            r.nEffectiveSign
        )).ToList();
    }
}
