using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Features.DbCoordinator.Models;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Web.Features.DbCoordinator.Controllers;

[Authorize(Roles = "Engineer")]
public class DbCoordinatorController : Controller
{
    private const string XLSX_MIME = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly DbCoordinatorService _service;
    private readonly DbPointConfigFileService _pointConfigService;
    private readonly DbCoordinatorReloadPublisher _reloadPublisher;
    private readonly SourceExcelImportCoordinator _importCoordinator;
    private readonly DbPointExcelAdapter _excelAdapter;
    private readonly ILogger<DbCoordinatorController> _logger;
    private readonly IStringLocalizer<DbCoordinatorController> _l;

    public DbCoordinatorController(
        DbCoordinatorService service,
        DbPointConfigFileService pointConfigService,
        DbCoordinatorReloadPublisher reloadPublisher,
        SourceExcelImportCoordinator importCoordinator,
        DbPointExcelAdapter excelAdapter,
        ILogger<DbCoordinatorController> logger,
        IStringLocalizer<DbCoordinatorController> localizer)
    {
        _service = service;
        _pointConfigService = pointConfigService;
        _reloadPublisher = reloadPublisher;
        _importCoordinator = importCoordinator;
        _excelAdapter = excelAdapter;
        _logger = logger;
        _l = localizer;
    }

    [HttpGet("/DbCoordinator")]
    public async Task<IActionResult> Index()
    {
        var data = await _service.GetAllAsync();

        // 既有 JSON 一覽：Coordinator 在 DB 中存在、但 JSON 不存在 → 標示「設定檔已移除」
        var existing = await _importCoordinator.ListExistingAsync(_excelAdapter);
        var existingNames = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

        var dto = data.Select(d => new DbCoordinatorListItemDto
        {
            id = d.Coordinator.Id,
            name = d.Coordinator.szName,
            pollingInterval = d.Coordinator.nPollingInterval,
            connectTimeout = d.Coordinator.nConnectTimeout,
            monitorEnabled = d.Coordinator.isMonitorEnabled,
            configMissing = !existingNames.Contains(d.Coordinator.szName),
            points = d.Points.Select(p => new DbPointListItemDto
            {
                sid = p.szSID,
                sequence = p.nSequence,
                name = p.szName,
                unit = p.szUnit ?? string.Empty,
                min = p.fMin,
                max = p.fMax
            }).ToList()
        }).ToList();

        // 反向：JSON 已存在但 DB 還沒有 → Engine 尚未載入（Engine 未啟動 / Reload 沒送到 / 設定驗證失敗），列為「待 Engine 載入」
        var coordinatorNames = new HashSet<string>(dto.Select(d => d.name), StringComparer.OrdinalIgnoreCase);
        dto.AddRange(existing
            .Where(e => !coordinatorNames.Contains(e.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new DbCoordinatorListItemDto { id = 0, name = e.Name, pendingEngine = true, monitorEnabled = true }));

        ViewBag.CoordinatorListJson = System.Text.Json.JsonSerializer.Serialize(dto);
        ViewBag.SourceExcel = new SourceExcelImportViewModel
        {
            BaseUrl = "/DbCoordinator",
            Kind = "dbpoint",
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

    /// <summary>
    /// 更新單一點位（名稱 + 單位）：回寫 DBPoint/*.json + UPSERT DBPoints，成功後發 reload MQTT 讓 Engine 熱重載
    /// </summary>
    [HttpPost("/DbCoordinator/UpdatePoint")]
    public async Task<IActionResult> UpdatePoint([FromBody] UpdatePointRequest request)
    {
        var (isSuccess, szMessage) = await _pointConfigService.UpdatePointAsync(
            request.Id, request.Sequence, request.NewName, request.NewUnit);

        var isReloadSent = false;
        string? szWorkbookNotice = null;
        if (isSuccess)
        {
            isReloadSent = await _reloadPublisher.PublishReloadAsync();
            if (!isReloadSent)
                _logger.LogWarning("點位更新成功但 reload MQTT 發布失敗: CoordinatorId={Id}, Seq={Seq}",
                    request.Id, request.Sequence);

            // 重產來源資料夾內對應的 xlsx（Excel 開著 / 有未匯入修改 → 不覆寫，回傳提示）
            szWorkbookNotice = await _importCoordinator.RefreshFolderWorkbooksAsync(_excelAdapter);
        }

        return Json(new
        {
            success = isSuccess,
            message = isSuccess && !isReloadSent
                ? _l["dbcoord.api.rename_saved_reload_failed"].Value
                : szMessage,
            reloadSent = isReloadSent,
            workbookNotice = szWorkbookNotice
        });
    }

    /// <summary>
    /// 觸發 Engine 重新載入 DBPoint/*.json（手動放檔或 reload 失敗時的補救按鈕）
    /// </summary>
    [HttpPost("/DbCoordinator/Reload")]
    public async Task<IActionResult> Reload()
    {
        var isSuccess = await _reloadPublisher.PublishReloadAsync();
        return Json(new
        {
            success = isSuccess,
            message = isSuccess
                ? _l["dbcoord.api.reload_success"].Value
                : _l["dbcoord.api.reload_failure"].Value
        });
    }

    // ───────────────────────────── Excel 匯入 / 匯出 / 刪除（流程見 SourceExcelImportCoordinator；提交後自動發 Reload） ─────────────────────────────

    /// <summary>下載空白 Excel 範本</summary>
    [HttpGet("/DbCoordinator/ImportTemplate")]
    public IActionResult ImportTemplate()
        => File(_importCoordinator.BuildTemplate(_excelAdapter), XLSX_MIME, _excelAdapter.TemplateFileName);

    /// <summary>匯出現行設定（全部，或 ?workbook= 指定來源 Excel 檔）</summary>
    [HttpGet("/DbCoordinator/ExportExcel")]
    public async Task<IActionResult> ExportExcel(string? workbook)
    {
        var bytes = await _importCoordinator.ExportAsync(_excelAdapter, workbook);
        var szStamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var szFileName = string.IsNullOrWhiteSpace(workbook)
            ? $"{_excelAdapter.ExportFileNamePrefix}_{szStamp}.xlsx"
            : $"{Path.GetFileNameWithoutExtension(workbook)}_{szStamp}.xlsx";
        return File(bytes, XLSX_MIME, szFileName);
    }

    /// <summary>匯出現行設定並寫進 Engine 來源資料夾（固定檔名覆寫）；Excel 開著或需確認覆寫回 409</summary>
    [HttpPost("/DbCoordinator/ExportToFolder")]
    public async Task<IActionResult> ExportToFolder(string? workbook, bool force = false)
    {
        var result = await _importCoordinator.ExportToFolderAsync(_excelAdapter, workbook, force, User.Identity?.Name ?? "anonymous");
        if (result.Success) return Ok(result);
        return result.Locked || result.NeedsConfirm ? StatusCode(409, result) : BadRequest(result);
    }

    /// <summary>來源資料夾內的 Excel 檔（從資料夾匯入下拉 + 未匯入修改提示用）</summary>
    [HttpGet("/DbCoordinator/FolderWorkbooks")]
    public IActionResult FolderWorkbooks() => Ok(_importCoordinator.ListFolderWorkbooks(_excelAdapter));

    /// <summary>以來源資料夾內的 Excel 預覽（不經瀏覽器上傳），之後同樣走 ImportCommit</summary>
    [HttpPost("/DbCoordinator/ImportPreviewFromFolder")]
    public async Task<IActionResult> ImportPreviewFromFolder([FromBody] SourceExcelFolderImportRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.FileName))
            return BadRequest(new { success = false, message = _l["dbcoord.api.param_error"].Value });

        var result = await _importCoordinator.PreviewFromFolderAsync(_excelAdapter, request.FileName, User.Identity?.Name ?? "anonymous");
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>上傳 Excel → 解析、驗證、比對既有設定 → 回傳預覽與 token（不寫檔）</summary>
    [HttpPost("/DbCoordinator/ImportPreview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = _l["dbcoord.api.param_error"].Value });

        var szExt = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (szExt != ".xlsx" && szExt != ".xlsm")
            return BadRequest(new { success = false, message = _l["dbcoord.api.excel_ext_invalid"].Value });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        var result = await _importCoordinator.PreviewAsync(_excelAdapter, ms, file.FileName, User.Identity?.Name ?? "anonymous");
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>帶預覽 token + 勾選結果提交寫檔，成功後自動發 Reload MQTT；預覽後設定被更動回 409</summary>
    [HttpPost("/DbCoordinator/ImportCommit")]
    public async Task<IActionResult> ImportCommit([FromBody] SourceExcelCommitRequest request)
    {
        if (request == null)
            return BadRequest(new { success = false, message = _l["dbcoord.api.param_error"].Value });

        var result = await _importCoordinator.CommitAsync(_excelAdapter, request, User.Identity?.Name ?? "anonymous");
        if (!result.Success)
            return result.Conflict ? StatusCode(409, result) : BadRequest(result);

        await PublishReloadOrAppendWarningAsync(result);
        return Ok(result);
    }

    /// <summary>匯入後輪詢用：DB 已載入的 Coordinator 名稱 + 資料夾現有 JSON 名稱（前端等 Engine Reload 寫完 DB 再重新整理）</summary>
    [HttpGet("/DbCoordinator/SourceStatus")]
    public async Task<IActionResult> SourceStatus()
    {
        var data = await _service.GetAllAsync();
        return Ok(new
        {
            loaded = data.Select(d => d.Coordinator.szName).ToList(),
            existing = _importCoordinator.GetExistingNames(_excelAdapter).ToList(),
        });
    }

    /// <summary>逐台刪除來源設定（移到 _deleted/ 備份），成功後自動發 Reload 讓 Engine 依現有 JSON 重建</summary>
    [HttpPost("/DbCoordinator/DeleteSource")]
    public async Task<IActionResult> DeleteSource([FromBody] SourceExcelDeleteRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { success = false, message = _l["dbcoord.api.param_error"].Value });

        var result = await _importCoordinator.DeleteAsync(_excelAdapter, request.Name, User.Identity?.Name ?? "anonymous");
        if (!result.Success) return NotFound(result);

        await PublishReloadOrAppendWarningAsync(result);
        return Ok(result);
    }

    /// <summary>DB 來源 Engine 無 watcher，寫檔後由 Web 發 Reload；失敗只在訊息後附註（檔案已寫入）</summary>
    private async Task PublishReloadOrAppendWarningAsync(SourceExcelCommitResult result)
    {
        var isReloadSent = await _reloadPublisher.PublishReloadAsync();
        if (!isReloadSent)
        {
            _logger.LogWarning("DB 來源 Excel 匯入/刪除已寫檔，但 reload MQTT 發布失敗");
            result.Message = $"{result.Message} {_l["dbcoord.api.import_saved_reload_failed"].Value}";
        }
    }
}
