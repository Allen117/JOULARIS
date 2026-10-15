using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Web.Features.AirCompressorDeclaration.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.AirCompressorDeclaration.Controllers;

/// <summary>
/// 空壓機能源申報（EMS 子頁）— 成對綁「用電迴路 kWh」＋「產氣量表迴路 Nm³」，年度 12 曆月比能耗 kWh/Nm³。
/// 與冰水版 /EnergyDeclaration 同構；錯誤一律回 { code }，前端依 airdeclaration.err.{code} 翻譯。
/// </summary>
[Authorize]
[Route("[controller]")]
public class AirCompressorDeclarationController : Controller
{
    private readonly AirCompressorDeclarationService _declarationService;
    private readonly EnergyCircuitService _energyCircuitService;
    private readonly AirMeterCircuitService _airMeterCircuitService;
    private readonly AirCompressorDeclarationExcelExporter _exporter;
    private readonly ILogger<AirCompressorDeclarationController> _logger;

    public AirCompressorDeclarationController(
        AirCompressorDeclarationService declarationService,
        EnergyCircuitService energyCircuitService,
        AirMeterCircuitService airMeterCircuitService,
        AirCompressorDeclarationExcelExporter exporter,
        ILogger<AirCompressorDeclarationController> logger)
    {
        _declarationService = declarationService;
        _energyCircuitService = energyCircuitService;
        _airMeterCircuitService = airMeterCircuitService;
        _exporter = exporter;
        _logger = logger;
    }

    [HttpGet("/AirCompressorDeclaration")]
    public IActionResult Index()
    {
        // 頁面標題由 View 端 @Localizer 依語系設定
        return View(new AirCompressorDeclarationViewModel());
    }

    // ---------- 申報報表設定 CRUD ----------

    /// <summary>取得所有申報報表設定（來源名稱由前端以迴路清單解析）</summary>
    [HttpGet("api/reports")]
    public async Task<IActionResult> GetReports()
    {
        var reports = await _declarationService.GetAllAsync();
        return Ok(reports.Select(r => new
        {
            id = r.nId,
            name = r.szName,
            energyCircuitId = r.nEnergyCircuitId,
            airMeterCircuitId = r.nAirMeterCircuitId,
            sortOrder = r.nSortOrder,
            description = r.szDescription
        }));
    }

    [HttpPost("api/reports")]
    public async Task<IActionResult> CreateReport([FromBody] SaveAirCompressorDeclarationDto dto)
    {
        var szError = await ValidateAsync(dto);
        if (szError != null) return BadRequest(new { code = szError });

        var nId = await _declarationService.CreateAsync(new AirCompressorDeclarationModel
        {
            szName = dto.name.Trim(),
            nEnergyCircuitId = dto.energyCircuitId,
            nAirMeterCircuitId = dto.airMeterCircuitId,
            szDescription = string.IsNullOrWhiteSpace(dto.description) ? null : dto.description.Trim()
        });
        return Ok(new { id = nId });
    }

    [HttpPut("api/reports/{nId:int}")]
    public async Task<IActionResult> UpdateReport(int nId, [FromBody] SaveAirCompressorDeclarationDto dto)
    {
        var szError = await ValidateAsync(dto);
        if (szError != null) return BadRequest(new { code = szError });

        var isOk = await _declarationService.UpdateAsync(
            nId, dto.name.Trim(), dto.energyCircuitId, dto.airMeterCircuitId,
            string.IsNullOrWhiteSpace(dto.description) ? null : dto.description.Trim());
        return isOk ? Ok() : NotFound(new { code = "report_not_found" });
    }

    [HttpDelete("api/reports/{nId:int}")]
    public async Task<IActionResult> DeleteReport(int nId)
    {
        var isOk = await _declarationService.DeleteAsync(nId);
        return isOk ? Ok() : NotFound(new { code = "report_not_found" });
    }

    /// <summary>後端驗證：名稱必填、兩個來源必填且必須存在（成對）。回錯誤代碼，null = 通過</summary>
    private async Task<string?> ValidateAsync(SaveAirCompressorDeclarationDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return "name_required";
        if (dto.energyCircuitId <= 0 || dto.airMeterCircuitId <= 0)
            return "source_required";
        if (await _energyCircuitService.GetByIdAsync(dto.energyCircuitId) == null)
            return "energy_circuit_missing";
        if (await _airMeterCircuitService.GetByIdAsync(dto.airMeterCircuitId) == null)
            return "air_circuit_missing";
        return null;
    }

    // ---------- 來源清單（設定彈窗下拉 + 名稱解析用） ----------

    /// <summary>用電迴路樹（扁平清單，前端組縮排）— 與冰水申報頁同一棵 EnergyCircuit</summary>
    [HttpGet("api/circuits")]
    public async Task<IActionResult> GetEnergyCircuits()
    {
        var nodes = await _energyCircuitService.GetAllAsync();
        return Ok(nodes.Select(n => new
        {
            id = n.nId,
            name = n.szName,
            parentId = n.nParentId,
            sortOrder = n.nSortOrder,
            sid = n.szSID
        }));
    }

    /// <summary>產氣量表迴路樹（扁平清單，前端組縮排）</summary>
    [HttpGet("api/airmetercircuits")]
    public async Task<IActionResult> GetAirMeterCircuits()
    {
        var nodes = await _airMeterCircuitService.GetAllAsync();
        return Ok(nodes.Select(n => new
        {
            id = n.nId,
            name = n.szName,
            parentId = n.nParentId,
            sortOrder = n.nSortOrder,
            sid = n.szSID
        }));
    }

    // ---------- 查詢 / 匯出 ----------

    [HttpPost("api/query")]
    public async Task<IActionResult> Query([FromBody] AirCompressorDeclarationQueryDto dto)
    {
        try
        {
            var result = await _declarationService.GetDeclarationReportAsync(dto.reportId, dto.year);
            return Ok(result);
        }
        catch (AirCompressorDeclarationException ex)
        {
            _logger.LogWarning("空壓機能源申報查詢失敗 reportId={ReportId} year={Year}: {Msg}", dto.reportId, dto.year, ex.Message);
            return BadRequest(new { code = ex.Code });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "空壓機能源申報查詢失敗 reportId={ReportId} year={Year}", dto.reportId, dto.year);
            return StatusCode(500, new { code = "query_failed" });
        }
    }

    [HttpPost("api/export")]
    public async Task<IActionResult> Export([FromBody] AirCompressorDeclarationQueryDto dto)
    {
        try
        {
            var result = await _declarationService.GetDeclarationReportAsync(dto.reportId, dto.year);
            var szOperator = User.Identity?.Name ?? "anonymous";
            var bytes = _exporter.Export(result, szOperator);
            var szFileName = $"AirCompressorDeclaration_{result.szReportName}_{result.nYear}_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", szFileName);
        }
        catch (AirCompressorDeclarationException ex)
        {
            _logger.LogWarning("空壓機能源申報匯出失敗 reportId={ReportId} year={Year}: {Msg}", dto.reportId, dto.year, ex.Message);
            return BadRequest(new { code = ex.Code });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "空壓機能源申報匯出失敗 reportId={ReportId} year={Year}", dto.reportId, dto.year);
            return StatusCode(500, new { code = "export_failed" });
        }
    }
}
