using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Features.SystemParameter.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.SystemParameter.Controllers;

/// <summary>
/// 系統參數 — 工程師專屬（頁面另由 PermissionService.EngineerPages + PageAccessFilter 擋 Admin / User）。
/// 存 SystemSettings.system_parameters 整份 JSON；存檔後下一個請求即生效。
/// </summary>
[Authorize(Roles = "Engineer")]
[Route("[controller]")]
public class SystemParameterController : Controller
{
    private readonly SystemParameterService _service;
    private readonly ILogger<SystemParameterController> _logger;

    public SystemParameterController(SystemParameterService service, ILogger<SystemParameterController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("/SystemParameter")]
    public IActionResult Index() => View();

    /// <summary>目前值 + 預設值 + 最後更新時間</summary>
    [HttpGet("api/config")]
    public async Task<IActionResult> GetConfig()
    {
        var (config, dtUpdatedAt) = await _service.GetWithTimestampAsync();
        return Ok(new SystemParameterResponse { config = config, defaults = new SystemParameterConfig(), dtUpdatedAt = dtUpdatedAt });
    }

    /// <summary>儲存整份參數（範圍驗證失敗回 400 + resx key）</summary>
    [HttpPost("api/config")]
    public async Task<IActionResult> SaveConfig([FromBody] SystemParameterConfig config)
    {
        try
        {
            await _service.SaveAsync(config);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, code = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "系統參數儲存失敗");
            return StatusCode(500, new { success = false, code = "sysparam.msg.save_fail" });
        }
    }
}
