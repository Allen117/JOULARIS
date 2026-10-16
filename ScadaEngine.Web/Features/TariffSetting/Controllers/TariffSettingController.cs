using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Features.TariffSetting.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.TariffSetting.Controllers;

/// <summary>
/// 電費設定 — 台電各類電價方案（累進 / 單一費率 / 時間電價）檢視與編輯 + 重新計算電費。
/// 自動計價由 ElectricityCostAggregationService 背景執行；本頁重算按鈕供換方案/改費率後回溯。
/// </summary>
[Authorize]
[Route("[controller]")]
public class TariffSettingController : Controller
{
    private readonly TariffSettingService _service;
    private readonly ElectricityCostService _costService;
    private readonly ILogger<TariffSettingController> _logger;

    public TariffSettingController(
        TariffSettingService service,
        ElectricityCostService costService,
        ILogger<TariffSettingController> logger)
    {
        _service = service;
        _costService = costService;
        _logger = logger;
    }

    [HttpGet("/TariffSetting")]
    public IActionResult Index()
    {
        return View(new TariffSettingViewModel());
    }

    /// <summary>
    /// 頂部卡片用 — 主要電表本期累計 kWh / 流動電費（同 EMS 電費狀態卡資料源）。
    /// 走本 Controller 讓權限跟著 /TariffSetting 頁，不依賴 /EMS 頁權限。
    /// </summary>
    [HttpGet("api/cost-summary")]
    public async Task<IActionResult> GetCostSummary()
    {
        try
        {
            return Ok(await _costService.GetStatusAsync(null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定頁累計卡片載入失敗");
            return StatusCode(500, new { message = "載入失敗" });
        }
    }

    /// <summary>整份設定（採用時間軸 + 方案目錄 + 衍生的今日採用方案）</summary>
    [HttpGet("api/config")]
    public async Task<IActionResult> GetConfig()
    {
        try
        {
            return Ok(await _service.GetConfigAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定載入失敗");
            return StatusCode(500, new { message = "設定載入失敗" });
        }
    }

    /// <summary>
    /// 儲存整份設定（採用時間軸 + 方案目錄）— 時間軸編輯用。
    /// szActivePlanId 為衍生欄位，由後端依今日重算，不吃前端傳入值。
    /// </summary>
    [HttpPost("api/config")]
    public async Task<IActionResult> SaveConfig([FromBody] SaveTariffConfigRequest dto)
    {
        try
        {
            await _service.SaveAdoptionsAndPlansAsync(dto.adoptions, dto.plans);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定整份儲存失敗");
            return StatusCode(500, new { message = "儲存失敗" });
        }
    }

    /// <summary>儲存契約容量歷史（整份覆蓋；契約容量最佳化回測依此逐月取有效契約）</summary>
    [HttpPost("api/contracts")]
    public async Task<IActionResult> SaveContracts([FromBody] SaveContractsRequest dto)
    {
        try
        {
            await _service.SaveContractsAsync(dto.contracts);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定契約容量儲存失敗");
            return StatusCode(500, new { message = "儲存失敗" });
        }
    }

    /// <summary>儲存單一方案（整份驗證：級距連續 / 時段覆蓋 24h 不重疊）。方案不存在時視為新增自建方案。</summary>
    [HttpPost("api/plan")]
    public async Task<IActionResult> SavePlan([FromBody] TariffPlan plan)
    {
        try
        {
            await _service.SavePlanAsync(plan);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定方案儲存失敗 {PlanId}", plan.szPlanId);
            return StatusCode(500, new { message = "儲存失敗" });
        }
    }

    /// <summary>刪除自建方案（台電 seed 方案與仍被採用時間軸引用的方案不可刪）</summary>
    [HttpDelete("api/plan/{planId}")]
    public async Task<IActionResult> DeletePlan(string planId)
    {
        try
        {
            await _service.DeletePlanAsync(planId);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定方案刪除失敗 {PlanId}", planId);
            return StatusCode(500, new { message = "刪除失敗" });
        }
    }

    /// <summary>設為採用方案（= 於採用時間軸補一筆「今日起採用」）</summary>
    [HttpPost("api/active")]
    public async Task<IActionResult> SetActive([FromBody] SetActivePlanRequest dto)
    {
        try
        {
            await _service.SetActivePlanAsync(dto.planId);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定採用方案切換失敗 {PlanId}", dto.planId);
            return StatusCode(500, new { message = "切換失敗" });
        }
    }

    /// <summary>
    /// 以「目前生效方案」費率重算指定區間電費（DELETE + INSERT 覆蓋）。
    /// 起訖皆為含日；區間上限 366 天；未選採用方案回 400。
    /// </summary>
    [HttpPost("api/recalculate")]
    public async Task<IActionResult> Recalculate([FromBody] RecalculateRequest dto)
    {
        if (!DateTime.TryParseExact(dto.start, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dtStart) ||
            !DateTime.TryParseExact(dto.end, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dtEnd))
            return BadRequest(new { errorCode = "invalid_range" });

        try
        {
            // 訖日為含日 → exclusive 邊界 = 隔日 00:00
            var result = await _costService.RecalculateRangeAsync(dtStart.Date, dtEnd.Date.AddDays(1));
            if (!result.isSuccess)
                return BadRequest(new { errorCode = result.szError });

            return Ok(new
            {
                success = true,
                hours = result.nHours,
                rows = result.nRows,
                from = result.dtFrom.ToString("yyyy-MM-dd"),
                to = result.dtToExclusive.AddDays(-1).ToString("yyyy-MM-dd")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費重算失敗 {Start} ~ {End}", dto.start, dto.end);
            return StatusCode(500, new { errorCode = "server_error" });
        }
    }

    /// <summary>還原單一方案為台電預設，回傳還原後方案</summary>
    [HttpPost("api/reset")]
    public async Task<IActionResult> ResetPlan([FromBody] ResetPlanRequest dto)
    {
        try
        {
            var plan = await _service.ResetPlanAsync(dto.planId);
            return Ok(plan);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "電費設定方案還原失敗 {PlanId}", dto.planId);
            return StatusCode(500, new { message = "還原失敗" });
        }
    }
}
