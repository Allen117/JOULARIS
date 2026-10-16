using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Web.Features.EquipmentEfficiencySetting.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.EquipmentEfficiencySetting.Controllers;

/// <summary>
/// 設備效率設定（冰機 / 空壓機兩 tab）— 設備 CRUD、掃描所需中繼資料、回填歷史。
/// 掃描判定在前端（js/equipment-scan.js），點位清單走既有 /Designer/Points（PointPicker.load）。
/// </summary>
[Authorize]
public class EquipmentEfficiencySettingController : Controller
{
    private const string ROUTE = "/EquipmentEfficiencySetting";

    private readonly EquipmentProfileService _profileService;
    private readonly EquipmentBackfillPublisher _backfillPublisher;

    public EquipmentEfficiencySettingController(
        EquipmentProfileService profileService,
        EquipmentBackfillPublisher backfillPublisher)
    {
        _profileService = profileService;
        _backfillPublisher = backfillPublisher;
    }

    private bool CanAccess() => PermissionService.CanAccessPage(User, ROUTE);

    [HttpGet(ROUTE)]
    public IActionResult Index([FromQuery] string? kind)
    {
        if (!CanAccess()) return Redirect("/EMS");
        return View(new EquipmentEfficiencySettingViewModel
        {
            szInitialKind = EquipmentKind.IsValid(kind) ? kind! : EquipmentKind.Chiller,
        });
    }

    /// <summary>角色清單 / 單位表 / 別名表 / 預設門檻 / WaterCircuit RT 點（掃描與編輯共用）</summary>
    [HttpGet(ROUTE + "/api/meta")]
    public async Task<IActionResult> GetMeta()
    {
        if (!CanAccess()) return Forbid();

        object RolesOf(string szKind) => EquipmentKind.RolesOf(szKind).ToDictionary(r => r, r =>
        {
            var q = EquipmentRoles.QuantityOf(r);
            return (object)new
            {
                quantity = q.ToString(),
                units = EquipmentUnitScale.UnitsOf(q).Select(u => new { unit = u.szUnit, scale = u.dScale }),
            };
        });

        return Ok(new
        {
            kinds = EquipmentKind.All,
            order = EquipmentKind.All.ToDictionary(k => k, k => EquipmentKind.RolesOf(k)),
            roles = EquipmentKind.All.ToDictionary(k => k, RolesOf),
            unitAliases = EquipmentUnitScale.AliasTable.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            defaults = EquipmentKind.All.ToDictionary(k => k, k =>
            {
                var (dGood, dFair) = EquipmentKind.DefaultThresholds(k);
                return new { good = dGood, fair = dFair };
            }),
            defaultSpecificHeat = EquipmentMetricCalculator.DefaultSpecificHeat,
            rtSids = await _profileService.GetWaterCircuitRtSidsAsync(),
        });
    }

    [HttpGet(ROUTE + "/api/profiles")]
    public async Task<IActionResult> GetProfiles([FromQuery] string? kind)
    {
        if (!CanAccess()) return Forbid();
        if (kind != null && !EquipmentKind.IsValid(kind)) return BadRequest(new { error = "invalid_kind" });
        return Ok(await _profileService.GetAllAsync(kind));
    }

    [HttpPost(ROUTE + "/api/profiles")]
    public async Task<IActionResult> SaveProfile([FromBody] EquipmentProfileSaveRequest req)
    {
        if (!CanAccess()) return Forbid();
        try
        {
            var nId = await _profileService.SaveAsync(ToModel(req));
            return Ok(new { id = nId });
        }
        catch (EquipmentProfileValidationException ex)
        {
            return BadRequest(new { error = ex.szCode, detail = ex.szDetail });
        }
    }

    [HttpPost(ROUTE + "/api/profiles/batch")]
    public async Task<IActionResult> CreateBatch([FromBody] EquipmentBatchCreateRequest req)
    {
        if (!CanAccess()) return Forbid();
        if (req.aProfiles.Count == 0) return BadRequest(new { error = "empty" });
        try
        {
            var ids = await _profileService.CreateManyAsync(req.aProfiles.Select(ToModel).ToList());
            return Ok(new { ids });
        }
        catch (EquipmentProfileValidationException ex)
        {
            return BadRequest(new { error = ex.szCode, detail = ex.szDetail });
        }
    }

    [HttpDelete(ROUTE + "/api/profiles/{id:int}")]
    public async Task<IActionResult> DeleteProfile(int id)
    {
        if (!CanAccess()) return Forbid();
        return await _profileService.DeleteAsync(id) ? Ok(new { ok = true }) : NotFound(new { error = "not_found" });
    }

    /// <summary>回填歷史：發 MQTT 給 Engine 逐小時重算（非同步，Engine log 可見進度）</summary>
    [HttpPost(ROUTE + "/api/backfill")]
    public async Task<IActionResult> Backfill([FromBody] EquipmentBackfillRequest req)
    {
        if (!CanAccess()) return Forbid();
        var ci = CultureInfo.InvariantCulture;
        if (!DateTime.TryParseExact(req.szFrom, "yyyy-MM-dd", ci, DateTimeStyles.None, out var dtFrom)
            || !DateTime.TryParseExact(req.szTo, "yyyy-MM-dd", ci, DateTimeStyles.None, out var dtTo)
            || dtTo < dtFrom)
            return BadRequest(new { error = "invalid_range" });
        if ((dtTo - dtFrom).TotalDays > 366) return BadRequest(new { error = "range_too_long" });

        var isOk = await _backfillPublisher.PublishAsync(req.nProfileId, dtFrom, dtTo);
        return isOk ? Ok(new { ok = true }) : StatusCode(503, new { error = "mqtt_unavailable" });
    }

    private static EquipmentProfileModel ToModel(EquipmentProfileSaveRequest r) => new()
    {
        nId = r.nId,
        szKind = r.szKind,
        szName = r.szName,
        nSortOrder = r.nSortOrder,
        isEnabled = r.isEnabled,
        dRatedOutput = r.dRatedOutput,
        dRatedPower = r.dRatedPower,
        dMaxKwh = r.dMaxKwh,
        dSpecificHeat = r.dSpecificHeat,
        dGoodThreshold = r.dGoodThreshold,
        dFairThreshold = r.dFairThreshold,
        szDescription = r.szDescription,
        szControlType = r.szControlType,
        dMinLoadPct = r.dMinLoadPct,
        dMaxPressure = r.dMaxPressure,
        nMaxStartsPerHour = r.nMaxStartsPerHour,
        aBindings = r.aBindings.Select(b => new EquipmentPointBindingModel
        {
            szRole = b.szRole,
            szSID = b.szSID,
            szUnit = b.szUnit,
        }).ToList(),
    };
}
