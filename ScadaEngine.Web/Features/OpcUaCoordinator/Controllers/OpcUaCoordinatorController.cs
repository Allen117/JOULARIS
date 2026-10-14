using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Features.OpcUaCoordinator.Models;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Web.Features.OpcUaCoordinator.Controllers;

/// <summary>
/// OPC UA 來源設定頁 — Server / Device / 點位全欄位動態編輯（免重啟 Engine）。
/// 存檔流程：驗證 → 回寫 OpcUaPoint/*.json → UPSERT DB → 發 MQTT reload → 同步 Web 快取。
/// Excel 範本 / 匯出 / 匯入走共用 SourceExcelImportCoordinator + OpcUaExcelAdapter；匯入提交後由 Web 依 JSON UPSERT DB 再 reload。
/// </summary>
[Authorize(Roles = "Engineer")]
public class OpcUaCoordinatorController : Controller
{
    private const string XLSX_MIME = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly OpcUaCoordinatorService _service;
    private readonly OpcUaReloadPublisher _reloadPublisher;
    private readonly MqttRealtimeSubscriberService _mqttService;
    private readonly SourceExcelImportCoordinator _importCoordinator;
    private readonly OpcUaExcelAdapter _excelAdapter;
    private readonly ILogger<OpcUaCoordinatorController> _logger;
    private readonly IStringLocalizer<OpcUaCoordinatorController> _l;

    public OpcUaCoordinatorController(
        OpcUaCoordinatorService service,
        OpcUaReloadPublisher reloadPublisher,
        MqttRealtimeSubscriberService mqttService,
        SourceExcelImportCoordinator importCoordinator,
        OpcUaExcelAdapter excelAdapter,
        ILogger<OpcUaCoordinatorController> logger,
        IStringLocalizer<OpcUaCoordinatorController> localizer)
    {
        _service = service;
        _reloadPublisher = reloadPublisher;
        _mqttService = mqttService;
        _importCoordinator = importCoordinator;
        _excelAdapter = excelAdapter;
        _logger = logger;
        _l = localizer;
    }

    [HttpGet("/OpcUaCoordinator")]
    public async Task<IActionResult> Index()
    {
        var servers = await _service.GetAllAsync();
        ViewBag.ServerListJson = System.Text.Json.JsonSerializer.Serialize(servers);

        var existing = await _importCoordinator.ListExistingAsync(_excelAdapter);
        ViewBag.SourceExcel = new SourceExcelImportViewModel
        {
            BaseUrl = "/OpcUaCoordinator",
            Kind = "opcua",
            Workbooks = existing
                .Select(e => e.SourceWorkbook)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => w!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        return View();
    }

    [HttpPost("/OpcUaCoordinator/SaveServer")]
    public async Task<IActionResult> SaveServer([FromBody] SaveOpcUaServerRequest request)
    {
        var (isSuccess, szMessage, nId) = await _service.SaveServerAsync(request);
        if (!isSuccess)
            return BadRequest(new { success = false, message = szMessage });

        await NotifyEngineAsync();
        return Json(new { success = true, message = szMessage, id = nId, workbookNotice = await RefreshFolderWorkbooksAsync() });
    }

    [HttpPost("/OpcUaCoordinator/DeleteServer")]
    public async Task<IActionResult> DeleteServer([FromBody] DeleteOpcUaServerRequest request)
    {
        var (isSuccess, szMessage) = await _service.DeleteServerAsync(request.Id);
        if (!isSuccess)
            return BadRequest(new { success = false, message = szMessage });

        await NotifyEngineAsync();
        return Json(new { success = true, message = szMessage, workbookNotice = await RefreshFolderWorkbooksAsync() });
    }

    [HttpPost("/OpcUaCoordinator/SavePoints")]
    public async Task<IActionResult> SavePoints([FromBody] SaveOpcUaPointsRequest request)
    {
        var (isSuccess, szMessage) = await _service.SavePointsAsync(request);
        if (!isSuccess)
            return BadRequest(new { success = false, message = szMessage });

        await NotifyEngineAsync();
        return Json(new { success = true, message = szMessage, workbookNotice = await RefreshFolderWorkbooksAsync() });
    }

    /// <summary>
    /// 測試讀取單一 NodeId（不落地、不影響採集）
    /// </summary>
    [HttpPost("/OpcUaCoordinator/TestRead")]
    public async Task<IActionResult> TestRead([FromBody] TestReadOpcUaRequest request)
    {
        var (isSuccess, szMessage) = await _service.TestReadAsync(request);
        return Json(new { success = isSuccess, message = szMessage });
    }

    // ───────────────────────────── Excel 範本 / 匯出 / 匯入（流程見 SourceExcelImportCoordinator） ─────────────────────────────

    /// <summary>下載空白 Excel 範本</summary>
    [HttpGet("/OpcUaCoordinator/ImportTemplate")]
    public IActionResult ImportTemplate()
        => File(_importCoordinator.BuildTemplate(_excelAdapter), XLSX_MIME, _excelAdapter.TemplateFileName);

    /// <summary>匯出現行設定（全部，或 ?workbook= 指定來源 Excel 檔）；不含密碼</summary>
    [HttpGet("/OpcUaCoordinator/ExportExcel")]
    public async Task<IActionResult> ExportExcel(string? workbook)
    {
        var bytes = await _importCoordinator.ExportAsync(_excelAdapter, workbook);
        var szStamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var szFileName = string.IsNullOrWhiteSpace(workbook)
            ? $"{_excelAdapter.ExportFileNamePrefix}_{szStamp}.xlsx"
            : $"{Path.GetFileNameWithoutExtension(workbook)}_{szStamp}.xlsx";
        return File(bytes, XLSX_MIME, szFileName);
    }

    /// <summary>匯出現行設定並寫進 Engine 來源資料夾（固定檔名覆寫；不含密碼）；Excel 開著或需確認覆寫回 409</summary>
    [HttpPost("/OpcUaCoordinator/ExportToFolder")]
    public async Task<IActionResult> ExportToFolder(string? workbook, bool force = false)
    {
        var result = await _importCoordinator.ExportToFolderAsync(_excelAdapter, workbook, force, User.Identity?.Name ?? "anonymous");
        if (result.Success) return Ok(result);
        return result.Locked || result.NeedsConfirm ? StatusCode(409, result) : BadRequest(result);
    }

    /// <summary>來源資料夾內的 Excel 檔（從資料夾匯入下拉 + 未匯入修改提示用）</summary>
    [HttpGet("/OpcUaCoordinator/FolderWorkbooks")]
    public IActionResult FolderWorkbooks() => Ok(_importCoordinator.ListFolderWorkbooks(_excelAdapter));

    /// <summary>以來源資料夾內的 Excel 預覽（不經瀏覽器上傳），之後同樣走 ImportCommit</summary>
    [HttpPost("/OpcUaCoordinator/ImportPreviewFromFolder")]
    public async Task<IActionResult> ImportPreviewFromFolder([FromBody] SourceExcelFolderImportRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.FileName))
            return BadRequest(new { success = false, message = _l["opcuacoordinator.api.param_error"].Value });

        var result = await _importCoordinator.PreviewFromFolderAsync(_excelAdapter, request.FileName, User.Identity?.Name ?? "anonymous");
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>上傳 Excel → 解析、驗證、Seq 對帳、比對既有設定 → 回傳預覽與 token（不寫檔）</summary>
    [HttpPost("/OpcUaCoordinator/ImportPreview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = _l["opcuacoordinator.api.param_error"].Value });

        var szExt = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (szExt != ".xlsx" && szExt != ".xlsm")
            return BadRequest(new { success = false, message = _l["opcuacoordinator.api.excel_ext_invalid"].Value });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        var result = await _importCoordinator.PreviewAsync(_excelAdapter, ms, file.FileName, User.Identity?.Name ?? "anonymous");
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// 帶預覽 token + 勾選結果提交寫檔；預覽後設定被更動回 409。
    /// 寫檔後由 Web 依 JSON UPSERT DB（與網頁存檔效果相同）再發 reload + 同步快取（plan 2026-10-12 決策 7）。
    /// </summary>
    [HttpPost("/OpcUaCoordinator/ImportCommit")]
    public async Task<IActionResult> ImportCommit([FromBody] SourceExcelCommitRequest request)
    {
        if (request == null)
            return BadRequest(new { success = false, message = _l["opcuacoordinator.api.param_error"].Value });

        var result = await _importCoordinator.CommitAsync(_excelAdapter, request, User.Identity?.Name ?? "anonymous");
        if (!result.Success)
            return result.Conflict ? StatusCode(409, result) : BadRequest(result);

        foreach (var szName in result.Imported)
        {
            if (!await _service.SyncDbFromJsonAsync(szName))
                _logger.LogWarning("OPC UA Excel 匯入已寫檔，但依 JSON 同步 DB 失敗: {Name}（Engine reload 仍會 UPSERT）", szName);
        }
        await NotifyEngineAsync();
        return Ok(result);
    }

    /// <summary>匯入後輪詢用：DB 已載入的 Server 名稱 + 資料夾現有 JSON 名稱（Web 匯入提交時已自行 UPSERT，通常立即到齊）</summary>
    [HttpGet("/OpcUaCoordinator/SourceStatus")]
    public async Task<IActionResult> SourceStatus()
    {
        var servers = await _service.GetAllAsync();
        return Ok(new
        {
            loaded = servers.Select(s => s.name).ToList(),
            existing = _importCoordinator.GetExistingNames(_excelAdapter).ToList(),
        });
    }

    /// <summary>
    /// 存檔後通知：發 MQTT reload 給 Engine + 同步 Web 端點位快取（新增即出現、刪除即剔除）。
    /// Reload 發布失敗只 log 不擋存檔結果 — JSON/DB 已落地，Engine 重啟後仍會載入。
    /// </summary>
    private async Task NotifyEngineAsync()
    {
        var isPublished = await _reloadPublisher.PublishReloadAsync();
        if (!isPublished)
            _logger.LogWarning("OPC UA Reload 通知發布失敗（設定已存檔，Engine 需等下次重啟或手動 reload 才生效）");

        await _mqttService.SyncOpcUaPointCacheAsync();
    }

    /// <summary>網頁存檔後重產來源資料夾內對應的 xlsx（Excel 開著 / 有未匯入修改 → 不覆寫，回傳提示；不影響存檔結果）</summary>
    private Task<string?> RefreshFolderWorkbooksAsync() => _importCoordinator.RefreshFolderWorkbooksAsync(_excelAdapter);
}
