using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Localization;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Web.Services;

/// <summary>
/// DB 來源點位名稱熱編輯服務 — 讀原 DBPoint/{Name}.json → 只改該點 Name / Unit → 原子寫回
/// （Watched + Mirror 雙寫）→ 以 JSON 為準 UPSERT DBPoints，之後由 Controller 發 MQTT reload。
/// JSON 為設定源、DB 為執行期快照（比照 OpcUaCoordinatorService，見 docs/plans 決策 2）。
///
/// 只開放改 Name / Unit：SID = DB{Id}-S{陣列索引+1}，增刪或重排會使後續 SID 位移、歷史資料錯接，
/// 故點位順序與數量一律不動。
///
/// 以 JsonNode **原地修改**（比照 ModbusConfigFileService）而非 DTO 反序列化再序列化：
/// 頂層 SourceWorkbook / NextSeq 等非 DTO 欄位原樣保留（DTO 往返會弄丟 SourceWorkbook，
/// 之後「依檔案匯出」篩不到、同檔重匯也不會列為刪除候選 — plan 2026-10-12 問題 A）。
/// 每次寫回在頂層記 WebEditedAt，供 Excel 匯入預覽判斷「拿舊 Excel 會蓋掉網頁修改」。
///
/// 檔案 I/O（路徑解析、原子寫檔、鏡像寫回）走共用 <see cref="SourceConfigFileIo"/>（appsettings `EngineDbPointConfig`），
/// 與 Excel 匯入同一份行為。現檔可能是舊巨集產的 UTF-16 LE BOM，讀取依 BOM 自動偵測；寫回統一 UTF-8（Engine 兩種都讀得動）。
/// </summary>
public class DbPointConfigFileService
{
    private const int MAX_POINTS = 100;
    private const int MAX_NAME_LENGTH = 100;
    private const int MAX_UNIT_LENGTH = 50;

    private readonly IDataRepository _repository;
    private readonly ILogger<DbPointConfigFileService> _logger;
    private readonly IStringLocalizer<DbPointConfigFileService> _l;
    private readonly SourceConfigFileIo _io;

    /// <summary>JSON 檔寫入互斥（static — Scoped service 跨請求共用）</summary>
    private static readonly SemaphoreSlim _fileGate = new(1, 1);

    private static readonly JsonSerializerOptions _jsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public DbPointConfigFileService(
        IDataRepository repository,
        ILogger<DbPointConfigFileService> logger,
        IStringLocalizer<DbPointConfigFileService> localizer,
        SourceConfigFileIo io)
    {
        _repository = repository;
        _logger = logger;
        _l = localizer;
        _io = io;
    }

    /// <summary>
    /// 更新單一點位（名稱 + 單位）：改 JSON（不動陣列結構）→ 原子雙寫 → 以 JSON 為準 UPSERT DBPoints。
    /// 單位可空白；自動帶入類功能（如電表設定篩 kWh）依 DBPoints.Unit 運作。
    /// </summary>
    public async Task<(bool isSuccess, string szMessage)> UpdatePointAsync(int nCoordinatorId, int nSequence, string szNewName, string szNewUnit)
    {
        szNewName = (szNewName ?? string.Empty).Trim();
        szNewUnit = (szNewUnit ?? string.Empty).Trim();
        if (szNewName.Length == 0)
            return (false, _l["dbpointcfg.svc.name_required"].Value);
        if (szNewName.Length > MAX_NAME_LENGTH)
            return (false, _l["dbpointcfg.svc.name_too_long"].Value);
        if (szNewUnit.Length > MAX_UNIT_LENGTH)
            return (false, _l["dbpointcfg.svc.unit_too_long"].Value);
        if (nSequence < 1 || nSequence > MAX_POINTS)
            return (false, _l["dbpointcfg.svc.sequence_invalid"].Value);

        var coordinators = (await _repository.GetAllDbCoordinatorsAsync()).ToList();
        var coordinator = coordinators.FirstOrDefault(c => c.Id == nCoordinatorId);
        if (coordinator == null)
            return (false, _l["dbpointcfg.svc.coordinator_not_found"].Value);

        await _fileGate.WaitAsync();
        try
        {
            var szFolder = _io.GetWatchedFolder(SourceConfigFileIo.DBPOINT_SECTION);
            if (szFolder == null)
                return (false, _l["dbpointcfg.svc.folder_not_configured"].Value);

            var szPath = SourceConfigFileIo.ResolveWithin(szFolder, coordinator.szName)
                         ?? Path.Combine(szFolder, coordinator.szName + ".json");
            if (!File.Exists(szPath))
                return (false, _l["dbpointcfg.svc.json_not_found", szPath].Value);

            // 依 BOM 自動偵測編碼（舊巨集產出為 UTF-16 LE BOM）
            var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szPath);

            var szNewJson = ApplyPointEdit(szJson, nSequence, szNewName, szNewUnit, out var points, out var nError);
            if (nError == PointEditError.InvalidJson)
            {
                _logger.LogWarning("DBPoint JSON 格式錯誤: {Path}", szPath);
                return (false, _l["dbpointcfg.svc.json_invalid"].Value);
            }
            if (nError == PointEditError.PointNotFound || szNewJson == null || points == null)
                return (false, _l["dbpointcfg.svc.point_not_found", nSequence].Value);

            SourceConfigFileIo.AtomicWrite(szPath, szNewJson, Encoding.UTF8);
            _io.MirrorWrite(SourceConfigFileIo.DBPOINT_SECTION, coordinator.szName, szNewJson, Encoding.UTF8);

            // 以剛寫回的 JSON 為準重建 DBPoints（走既有 SaveDbPointsAsync，DELETE+INSERT 冪等；
            // 映射邏輯與 Engine 載入器一致：Sequence = 索引+1、上限 100 點）
            await _repository.SaveDbPointsAsync(nCoordinatorId, BuildPointModels(nCoordinatorId, points));

            _logger.LogInformation("DB 來源點位已更新: {Coordinator} S{Seq} → Name={NewName}, Unit={NewUnit}",
                coordinator.szName, nSequence, szNewName, szNewUnit);
            return (true, _l["dbpointcfg.svc.save_success"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 DB 來源點位失敗: CoordinatorId={Id}, Seq={Seq}", nCoordinatorId, nSequence);
            return (false, _l["dbpointcfg.svc.json_write_failed", ex.Message].Value);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public enum PointEditError
    {
        None,
        InvalidJson,
        PointNotFound,
    }

    /// <summary>
    /// 純函數：在 JSON 文字上原地改 Points[nSequence-1] 的 Name / Unit，頂層加 WebEditedAt，其他欄位（含未知欄位）原樣保留。
    /// 回傳新 JSON 文字（錯誤時 null）；points 為修改後的 Points 陣列（供 UPSERT DB）。
    /// </summary>
    public static string? ApplyPointEdit(string szJson, int nSequence, string szNewName, string szNewUnit,
        out JsonArray? points, out PointEditError nError)
    {
        points = null;
        nError = PointEditError.None;

        JsonObject root;
        try
        {
            if (JsonNode.Parse(szJson, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is not JsonObject parsed)
            {
                nError = PointEditError.InvalidJson;
                return null;
            }
            root = parsed;
        }
        catch (JsonException)
        {
            nError = PointEditError.InvalidJson;
            return null;
        }

        // 欄位名稱不分大小寫（Engine loader 亦為 PropertyNameCaseInsensitive）
        var szPointsKey = root.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "Points", StringComparison.OrdinalIgnoreCase));
        if (szPointsKey == null || root[szPointsKey] is not JsonArray arr || nSequence < 1 || nSequence > arr.Count
            || arr[nSequence - 1] is not JsonObject target)
        {
            nError = PointEditError.PointNotFound;
            return null;
        }

        SetIgnoreCase(target, "Name", szNewName);
        SetIgnoreCase(target, "Unit", szNewUnit);
        SourceJsonStamp.StampWebEdited(root);

        points = arr;
        return root.ToJsonString(_jsonWriteOptions);
    }

    /// <summary>以既有鍵（不分大小寫）覆寫；沒有就新增</summary>
    private static void SetIgnoreCase(JsonObject obj, string szKey, string szValue)
    {
        var szExisting = obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, szKey, StringComparison.OrdinalIgnoreCase));
        obj[szExisting ?? szKey] = szValue;
    }

    /// <summary>Points 陣列 → DBPoints 模型（Sequence = 索引+1、上限 100 點）</summary>
    public static List<DbPointModel> BuildPointModels(int nCoordinatorId, JsonArray points)
    {
        var list = new List<DbPointModel>();
        for (var i = 0; i < points.Count && i < MAX_POINTS; i++)
        {
            if (points[i] is not JsonObject pt) continue;
            list.Add(new DbPointModel
            {
                szSID = $"DB{nCoordinatorId}-S{i + 1}",
                nCoordinatorId = nCoordinatorId,
                nSequence = i + 1,
                szName = GetIgnoreCase(pt, "Name") ?? string.Empty,
                szUnit = GetIgnoreCase(pt, "Unit") ?? string.Empty,
                fMin = ParseFloat(GetIgnoreCase(pt, "Min"), 0f),
                fMax = ParseFloat(GetIgnoreCase(pt, "Max"), 100f),
            });
        }
        return list;
    }

    private static string? GetIgnoreCase(JsonObject obj, string szKey)
    {
        var szExisting = obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, szKey, StringComparison.OrdinalIgnoreCase));
        return szExisting == null ? null : obj[szExisting]?.ToString();
    }

    private static float ParseFloat(string? sz, float fDefault)
        => float.TryParse((sz ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fDefault;
}
