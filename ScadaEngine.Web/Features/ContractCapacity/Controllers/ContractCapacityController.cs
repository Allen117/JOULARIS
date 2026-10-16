using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Features.ContractCapacity.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.ContractCapacity.Controllers;

/// <summary>
/// 契約容量最佳化 — 以歷史需量回測經常契約容量，給出保守建議與最低成本點及回推節省金額。
/// 單價取自電費設定的採用方案，現行契約取自電費設定的契約容量歷史。
/// </summary>
[Authorize]
[Route("[controller]")]
public class ContractCapacityController : Controller
{
    private readonly ContractCapacityService _service;
    private readonly ContractCapacityExcelExporter _exporter;
    private readonly ILogger<ContractCapacityController> _logger;

    public ContractCapacityController(
        ContractCapacityService service,
        ContractCapacityExcelExporter exporter,
        ILogger<ContractCapacityController> logger)
    {
        _service = service;
        _exporter = exporter;
        _logger = logger;
    }

    [HttpGet("/ContractCapacity")]
    public IActionResult Index()
    {
        return View(new ContractCapacityViewModel());
    }

    /// <summary>可選迴路（啟用需量計算的電表）</summary>
    [HttpGet("api/circuits")]
    public async Task<IActionResult> GetCircuits()
    {
        try
        {
            return Ok(await _service.GetCircuitsAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "契約容量最佳化迴路清單載入失敗");
            return StatusCode(500, new { code = "server_error" });
        }
    }

    /// <summary>執行回測</summary>
    [HttpPost("api/analyze")]
    public async Task<IActionResult> Analyze([FromBody] ContractCapacityQuery query)
    {
        try
        {
            var result = await _service.AnalyzeAsync(query);
            if (!result.isSuccess) return BadRequest(new { code = result.szError });
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "契約容量回測失敗 circuit={Circuit} {From}~{To}", query.circuitId, query.from, query.to);
            return StatusCode(500, new { code = "server_error" });
        }
    }

    /// <summary>匯出 Excel（同回測條件重算一次）</summary>
    [HttpPost("api/export")]
    public async Task<IActionResult> Export([FromBody] ContractCapacityQuery query)
    {
        try
        {
            var result = await _service.AnalyzeAsync(query);
            if (!result.isSuccess) return BadRequest(new { code = result.szError });
            var szOperator = User.Identity?.Name ?? "anonymous";
            var bytes = _exporter.Export(result, szOperator);
            var szFileName = $"ContractCapacity_{result.szCircuitName}_{result.szFrom}_{result.szTo}_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", szFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "契約容量回測匯出失敗 circuit={Circuit} {From}~{To}", query.circuitId, query.from, query.to);
            return StatusCode(500, new { code = "server_error" });
        }
    }
}
