using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.ModbusCoordinator.Models;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Web.Features.ModbusCoordinator.Controllers;

[Authorize(Roles = "Engineer")]
public class ModbusCoordinatorController : Controller
{
    private const string XLSX_MIME = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IDataRepository _repository;
    private readonly ModbusConfigFileService _configFileService;
    private readonly ControlEventLogger _controlEventLogger;
    private readonly SourceExcelImportCoordinator _importCoordinator;
    private readonly ModbusExcelAdapter _excelAdapter;
    private readonly IStringLocalizer<ModbusCoordinatorController> _l;

    public ModbusCoordinatorController(
        IDataRepository repository,
        ModbusConfigFileService configFileService,
        ControlEventLogger controlEventLogger,
        SourceExcelImportCoordinator importCoordinator,
        ModbusExcelAdapter excelAdapter,
        IStringLocalizer<ModbusCoordinatorController> localizer)
    {
        _repository = repository;
        _configFileService = configFileService;
        _controlEventLogger = controlEventLogger;
        _importCoordinator = importCoordinator;
        _excelAdapter = excelAdapter;
        _l = localizer;
    }

    [HttpGet("/ModbusCoordinator")]
    public async Task<IActionResult> Index()
    {
        var coordinators = (await _repository.GetAllCoordinatorsAsync()).ToList();

        // 既有 JSON 一覽：Coordinator 在 DB 中存在、但 JSON 不存在 → 頁面標示「設定檔已移除」
        var existing = await _importCoordinator.ListExistingAsync(_excelAdapter);
        var existingNames = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        ViewBag.MissingConfigNames = coordinators
            .Where(c => !existingNames.Contains(c.szName))
            .Select(c => c.szName)
            .ToList();

        // 反向：JSON 已存在但 DB 還沒有 → Engine 尚未載入（Engine 未啟動 / watcher 未接到 / 設定驗證失敗），頁面列為「待 Engine 載入」
        var coordinatorNames = new HashSet<string>(coordinators.Select(c => c.szName), StringComparer.OrdinalIgnoreCase);
        ViewBag.PendingConfigNames = existing
            .Select(e => e.Name)
            .Where(n => !coordinatorNames.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 右側詳情顯示 IP / Port：DB 的 ModbusCoordinator 表沒有連線端點，從各設備 JSON 讀（設定檔已移除者無資料）
        var endpoints = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in coordinators)
        {
            var endpoint = await _configFileService.GetEndpointAsync(c.szName);
            if (endpoint.HasValue)
                endpoints[c.szName] = new { ip = endpoint.Value.szIp, port = endpoint.Value.nPort };
        }
        ViewBag.Endpoints = endpoints;

        ViewBag.SourceExcel = new SourceExcelImportViewModel
        {
            BaseUrl = "/ModbusCoordinator",
            Kind = "modbus",
            Workbooks = existing
                .Select(e => e.SourceWorkbook)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => w!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        return View(coordinators);
    }

    [HttpPost("/ModbusCoordinator/UpdateDeviceName")]
    public async Task<IActionResult> UpdateDeviceName([FromBody] UpdateDeviceNameRequest request)
    {
        if (request == null || request.Id <= 0)
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.param_error"].Value });

        var isSuccess = await _repository.UpdateDeviceNameAsync(request.Id, request.DeviceName ?? "");
        if (isSuccess)
            return Ok(new { success = true });

        return StatusCode(500, new { success = false, message = _l["modbuscoordinator.api.update_failed"].Value });
    }

    /// <summary>讀取指定設備（JSON 檔）的點位清單 — 點位熱編輯用，限 Engineer</summary>
    [HttpGet("/ModbusCoordinator/Points/{name}")]
    public async Task<IActionResult> Points(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.param_error"].Value });

        var model = await _configFileService.GetPointsAsync(name);
        if (model == null)
            return NotFound(new { success = false, message = _l["modbuscoordinator.api.file_not_found"].Value });

        return Ok(new
        {
            success = true,
            coordinatorName = model.CoordinatorName,
            ip = model.IP,
            port = model.Port,
            modbusId = model.ModbusId,
            connectTimeout = model.ConnectTimeout,
            points = model.Points,
        });
    }

    /// <summary>原地更新點位欄位（數量/順序/DataType 鎖死）— 限 Engineer，成功後每個變更點位寫一筆 EventLog 稽核</summary>
    [HttpPost("/ModbusCoordinator/UpdatePoints")]
    public async Task<IActionResult> UpdatePoints([FromBody] UpdateModbusPointsRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.CoordinatorName)
            || request.Points == null || request.Points.Count == 0)
        {
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.param_error"].Value });
        }

        var result = await _configFileService.UpdatePointsAsync(request.CoordinatorName, request.Points);

        if (!result.isSuccess)
        {
            return result.nError switch
            {
                ModbusPointsUpdateError.FileNotFound =>
                    NotFound(new { success = false, message = _l["modbuscoordinator.api.file_not_found"].Value }),
                ModbusPointsUpdateError.StructureChanged =>
                    BadRequest(new { success = false, message = _l["modbuscoordinator.api.structure_changed"].Value }),
                ModbusPointsUpdateError.InvalidPoint =>
                    BadRequest(new { success = false, message = $"{_l["modbuscoordinator.api.invalid_point"].Value} (#{result.nInvalidRow}: {result.szInvalidReason})" }),
                _ =>
                    StatusCode(500, new { success = false, message = _l["modbuscoordinator.api.save_failed"].Value }),
            };
        }

        // EventLog 稽核 — SID 依 Engine 規則：{Id*65536 + 首個ModbusId*256 + 1}-S{index+1}
        if (result.changes.Count > 0)
        {
            var coordinator = (await _repository.GetAllCoordinatorsAsync())
                .FirstOrDefault(c => string.Equals(c.szName, request.CoordinatorName, StringComparison.OrdinalIgnoreCase));

            if (coordinator != null)
            {
                var szFirstModbusId = coordinator.szModbusID
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                var nFirstModbusId = int.TryParse(szFirstModbusId, out var nId) ? nId : 1;
                var szUsername = User.Identity?.Name ?? "anonymous";

                foreach (var change in result.changes)
                {
                    var szSid = $"{coordinator.Id * 65536 + nFirstModbusId * 256 + 1}-S{change.nTagIndex + 1}";
                    await _controlEventLogger.LogPointConfigChangedAsync(szSid, change.szPointName, change.szSummary, szUsername);
                }
            }
        }

        return Ok(new { success = true, changedCount = result.changes.Count });
    }

    // ───────────────────────────── Excel 匯入 / 匯出 / 刪除（流程見 SourceExcelImportCoordinator） ─────────────────────────────

    /// <summary>下載空白 Excel 範本</summary>
    [HttpGet("/ModbusCoordinator/ImportTemplate")]
    public IActionResult ImportTemplate()
        => File(_importCoordinator.BuildTemplate(_excelAdapter), XLSX_MIME, _excelAdapter.TemplateFileName);

    /// <summary>匯出現行設定（全部，或 ?workbook= 指定來源 Excel 檔）</summary>
    [HttpGet("/ModbusCoordinator/ExportExcel")]
    public async Task<IActionResult> ExportExcel(string? workbook)
    {
        var bytes = await _importCoordinator.ExportAsync(_excelAdapter, workbook);
        var szStamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var szFileName = string.IsNullOrWhiteSpace(workbook)
            ? $"{_excelAdapter.ExportFileNamePrefix}_{szStamp}.xlsx"
            : $"{Path.GetFileNameWithoutExtension(workbook)}_{szStamp}.xlsx";
        return File(bytes, XLSX_MIME, szFileName);
    }

    /// <summary>上傳 Excel → 解析、驗證、比對既有設定 → 回傳預覽與 token（不寫檔）</summary>
    [HttpPost("/ModbusCoordinator/ImportPreview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.param_error"].Value });

        var szExt = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (szExt != ".xlsx" && szExt != ".xlsm")
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.excel_ext_invalid"].Value });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        var result = await _importCoordinator.PreviewAsync(_excelAdapter, ms, file.FileName, User.Identity?.Name ?? "anonymous");
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>帶預覽 token + 勾選結果提交寫檔；預覽後設定被更動回 409</summary>
    [HttpPost("/ModbusCoordinator/ImportCommit")]
    public async Task<IActionResult> ImportCommit([FromBody] SourceExcelCommitRequest request)
    {
        if (request == null)
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.param_error"].Value });

        var result = await _importCoordinator.CommitAsync(_excelAdapter, request, User.Identity?.Name ?? "anonymous");
        if (result.Success) return Ok(result);
        return result.Conflict ? StatusCode(409, result) : BadRequest(result);
    }

    /// <summary>
    /// 匯入後輪詢用：DB 已載入的 Coordinator 名稱 + 資料夾現有 JSON 名稱。
    /// 匯入寫檔 → Engine watcher 去抖 1 秒 + 延遲 0.5 秒才寫 DB，頁面若立刻重新整理會把新設備判成「待 Engine 載入」，
    /// 前端改為輪詢此端點、等名稱出現在 loaded 再重新整理。
    /// </summary>
    [HttpGet("/ModbusCoordinator/SourceStatus")]
    public async Task<IActionResult> SourceStatus()
    {
        var coordinators = await _repository.GetAllCoordinatorsAsync();
        return Ok(new
        {
            loaded = coordinators.Select(c => c.szName).ToList(),
            existing = _importCoordinator.GetExistingNames(_excelAdapter).ToList(),
        });
    }

    /// <summary>逐台刪除設備設定（移到 _deleted/ 備份；Engine watcher 收到 Deleted 即停止採集）</summary>
    [HttpPost("/ModbusCoordinator/DeleteSource")]
    public async Task<IActionResult> DeleteSource([FromBody] SourceExcelDeleteRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { success = false, message = _l["modbuscoordinator.api.param_error"].Value });

        var result = await _importCoordinator.DeleteAsync(_excelAdapter, request.Name, User.Identity?.Name ?? "anonymous");
        return result.Success ? Ok(result) : NotFound(result);
    }
}
