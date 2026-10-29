using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Features.GasCostReport.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.GasCostReport.Controllers;

/// <summary>
/// 氣費報表 — 天然氣表迴路 × 月結期別區間 → 每期用氣量（m³）套天然氣分段累進之氣費。
/// 資料源 GasUsageReportService（用氣量）+ GasTariffService（費率選版），計算核心在 GasCostService。
/// 期別可設兩月一期（已刪除期別不列）。
/// </summary>
[Authorize]
[Route("[controller]")]
public class GasCostReportController : Controller
{
    private readonly GasMeterCircuitService _circuitService;
    private readonly GasCostService _costService;
    private readonly GasCostReportExcelExporter _exporter;
    private readonly ILogger<GasCostReportController> _logger;

    public GasCostReportController(
        GasMeterCircuitService circuitService,
        GasCostService costService,
        GasCostReportExcelExporter exporter,
        ILogger<GasCostReportController> logger)
    {
        _circuitService = circuitService;
        _costService = costService;
        _exporter = exporter;
        _logger = logger;
    }

    [HttpGet("/GasCostReport")]
    public IActionResult Index()
    {
        return View(new GasCostReportViewModel());
    }

    /// <summary>取得天然氣表迴路樹（給左側下拉用）</summary>
    [HttpGet("api/circuits")]
    public async Task<IActionResult> GetCircuits()
    {
        var nodes = await _circuitService.GetAllAsync();
        return Ok(nodes.Select(n => new
        {
            id = n.nId,
            name = n.szName,
            parentId = n.nParentId,
            sortOrder = n.nSortOrder,
            sid = n.szSID
        }));
    }

    /// <summary>查詢期別氣費（fromYm / toYm 格式 yyyy-MM，含頭尾）</summary>
    [HttpGet("api/query")]
    public async Task<IActionResult> Query(
        [FromQuery] int circuitId, [FromQuery] string fromYm, [FromQuery] string toYm)
    {
        if (!TryParseYm(fromYm, out var dtFrom) || !TryParseYm(toYm, out var dtTo) || dtTo < dtFrom)
            return BadRequest(new { message = "查詢區間格式不正確（yyyy-MM）" });

        try
        {
            var rows = await _costService.GetPeriodCostsAsync(
                circuitId, dtFrom.Year, dtFrom.Month, dtTo.Year, dtTo.Month);
            return Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "氣費報表查詢失敗 circuitId={CircuitId}", circuitId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>匯出 Excel</summary>
    [HttpPost("api/export")]
    public async Task<IActionResult> Export([FromBody] GasCostReportRequestDto dto)
    {
        if (!TryParseYm(dto.fromYm, out var dtFrom) || !TryParseYm(dto.toYm, out var dtTo) || dtTo < dtFrom)
            return BadRequest(new { message = "查詢區間格式不正確（yyyy-MM）" });

        try
        {
            var circuit = await _circuitService.GetByIdAsync(dto.circuitId);
            if (circuit == null)
                return BadRequest(new { message = $"天然氣表迴路 Id={dto.circuitId} 不存在" });

            var rows = await _costService.GetPeriodCostsAsync(
                dto.circuitId, dtFrom.Year, dtFrom.Month, dtTo.Year, dtTo.Month);
            var szOperator = User.Identity?.Name ?? "anonymous";
            var bytes = _exporter.Export(circuit.szName, dto.fromYm, dto.toYm, rows, szOperator);
            var szFileName = $"GasCostReport_{circuit.szName}_{dto.fromYm}_{dto.toYm}_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", szFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "氣費報表匯出失敗 circuitId={CircuitId}", dto.circuitId);
            return BadRequest(new { message = ex.Message });
        }
    }

    private static bool TryParseYm(string? szYm, out DateTime dtYm) =>
        DateTime.TryParseExact(szYm?.Trim() ?? string.Empty, "yyyy-MM",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out dtYm);
}
