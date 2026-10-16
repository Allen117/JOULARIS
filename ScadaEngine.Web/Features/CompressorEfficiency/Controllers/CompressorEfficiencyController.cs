using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;
using ScadaEngine.Web.Features.CompressorEfficiency.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.CompressorEfficiency.Controllers;

/// <summary>
/// 設備效率診斷頁（compressor）— 效率儀表板資料走 /EMS/api/equipment/*?kind=compressor（js/ems-equipment.js）。
/// 「系統診斷」tab（空壓系統規則式診斷，js/compressor-diagnosis.js）：
///   - 最新排程快照：有頁面權限即可看
///   - 診斷設定讀寫、指定期間重算：限 Engineer（前端隱藏 + 本處 PermissionService.IsEngineer 雙重擋）
/// </summary>
[Authorize]
public class CompressorEfficiencyController : Controller
{
    private const string ROUTE = "/CompressorEfficiency";

    private readonly CompressorDiagnosisService _diagnosisService;

    public CompressorEfficiencyController(CompressorDiagnosisService diagnosisService)
    {
        _diagnosisService = diagnosisService;
    }

    private bool CanAccess() => PermissionService.CanAccessPage(User, ROUTE);
    private bool IsEngineer() => CanAccess() && PermissionService.IsEngineer(User);

    [HttpGet(ROUTE)]
    public IActionResult Index()
    {
        if (!CanAccess())
            return Redirect("/EMS");
        ViewData["IsEngineer"] = PermissionService.IsEngineer(User);
        return View();
    }

    /// <summary>最新排程快照（每日 02:00 最近 7 天滾動）；尚無 → snapshot = null</summary>
    [HttpGet(ROUTE + "/api/diagnosis/latest")]
    public async Task<IActionResult> GetLatest()
    {
        if (!CanAccess()) return Forbid();
        return Ok(new
        {
            hasCompressors = await _diagnosisService.HasCompressorsAsync(),
            snapshot = await _diagnosisService.GetLatestScheduledAsync(),
        });
    }

    /// <summary>診斷設定 + 參數定義（預設值 / 範圍 / 出處）+ 系統點位角色單位表</summary>
    [HttpGet(ROUTE + "/api/diagnosis/setting")]
    public async Task<IActionResult> GetSetting()
    {
        if (!IsEngineer()) return Forbid();
        return Ok(new
        {
            setting = await _diagnosisService.GetSettingAsync(),
            parameters = DiagnosisParameters.Definitions.Select(d => new
            {
                key = d.szKey, defaultValue = d.dDefault, min = d.dMin, max = d.dMax, source = d.szSource, group = d.szGroup,
            }),
            sysRoles = CompressorSystemRoles.All.ToDictionary(r => r, r =>
            {
                var q = CompressorSystemRoles.QuantityOf(r);
                return EquipmentUnitScale.UnitsOf(q).Select(u => u.szUnit).ToList();
            }),
            unitAliases = EquipmentUnitScale.AliasTable.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            sysQuantities = CompressorSystemRoles.All.ToDictionary(r => r, r => CompressorSystemRoles.QuantityOf(r).ToString()),
            defaultPeriodDays = DiagnosisParameters.DefaultPeriodDays,
            maxPeriodDays = DiagnosisParameters.MaxPeriodDays,
        });
    }

    [HttpPost(ROUTE + "/api/diagnosis/setting")]
    public async Task<IActionResult> SaveSetting([FromBody] CompressorDiagnosisSettingModel req)
    {
        if (!IsEngineer()) return Forbid();
        try
        {
            await _diagnosisService.SaveSettingAsync(req, User.Identity?.Name);
            return Ok(new { ok = true });
        }
        catch (CompressorDiagnosisValidationException ex)
        {
            return BadRequest(new { error = ex.szCode, detail = ex.szDetail });
        }
    }

    /// <summary>指定期間重算（含訖日，上限 92 天）；結果存非排程快照並回傳</summary>
    [HttpPost(ROUTE + "/api/diagnosis/run")]
    public async Task<IActionResult> Run([FromBody] CompressorDiagnosisRunRequest req)
    {
        if (!IsEngineer()) return Forbid();
        var dtFrom = CompressorDiagnosisService.ParseDate(req.szFrom);
        var dtTo = CompressorDiagnosisService.ParseDate(req.szTo);
        if (dtFrom == null || dtTo == null || dtTo < dtFrom) return BadRequest(new { error = "invalid_range" });
        var dtToExclusive = dtTo.Value.AddDays(1);
        if ((dtToExclusive - dtFrom.Value).TotalDays > DiagnosisParameters.MaxPeriodDays)
            return BadRequest(new { error = "range_too_long", detail = DiagnosisParameters.MaxPeriodDays.ToString() });

        var result = await _diagnosisService.RunAsync(dtFrom.Value, dtToExclusive);
        if (result == null) return BadRequest(new { error = "no_compressor" });
        var szUser = User.Identity?.Name;
        var nId = await _diagnosisService.SaveSnapshotAsync(result, isScheduled: false, szUser);
        return Ok(new CompressorDiagnosisSnapshotDto
        {
            nId = nId, dtRunAt = result.dtRunAt, isScheduled = false, szRunBy = szUser, result = result,
        });
    }
}
