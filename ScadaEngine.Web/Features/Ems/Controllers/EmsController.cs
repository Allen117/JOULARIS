using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.EnergyMeter.Models;
using ScadaEngine.Web.Features.Ems.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.Ems.Controllers;

/// <summary>
/// 能源管理 Hub — /EMS 進入點
/// </summary>
[Authorize]
public class EmsController : Controller
{
    private readonly IDataRepository _repo;
    private readonly EnergyCircuitService _circuitService;
    private readonly EnergyReportService _reportService;
    private readonly BillingPeriodService _billingPeriodService;
    private readonly MainMeterAggregationService _aggregation;
    private readonly ElectricityCostService _costService;
    private readonly EmsCardSettingService _cardSettingService;
    private readonly WaterBillingPeriodService _waterBillingPeriodService;
    private readonly WaterMeterCircuitService _waterCircuitService;
    private readonly WaterUsageReportService _waterReportService;
    private readonly WaterCostService _waterCostService;
    private readonly GasBillingPeriodService _gasBillingPeriodService;
    private readonly GasMeterCircuitService _gasCircuitService;
    private readonly GasUsageReportService _gasReportService;
    private readonly GasCostService _gasCostService;
    private readonly WaterCircuitService _chilledWaterCircuitService;
    private readonly RefrigerationTonReportService _rtReportService;
    private readonly EquipmentRealtimeService _equipRealtimeService;
    private readonly EquipmentEfficiencyReportService _equipReportService;
    private readonly EquipmentProfileService _equipProfileService;

    public EmsController(
        IDataRepository repo,
        EnergyCircuitService circuitService,
        EnergyReportService reportService,
        BillingPeriodService billingPeriodService,
        MainMeterAggregationService aggregation,
        ElectricityCostService costService,
        EmsCardSettingService cardSettingService,
        WaterBillingPeriodService waterBillingPeriodService,
        WaterMeterCircuitService waterCircuitService,
        WaterUsageReportService waterReportService,
        WaterCostService waterCostService,
        GasBillingPeriodService gasBillingPeriodService,
        GasMeterCircuitService gasCircuitService,
        GasUsageReportService gasReportService,
        GasCostService gasCostService,
        WaterCircuitService chilledWaterCircuitService,
        RefrigerationTonReportService rtReportService,
        EquipmentRealtimeService equipRealtimeService,
        EquipmentEfficiencyReportService equipReportService,
        EquipmentProfileService equipProfileService)
    {
        _repo                 = repo;
        _circuitService       = circuitService;
        _reportService        = reportService;
        _billingPeriodService = billingPeriodService;
        _aggregation          = aggregation;
        _costService          = costService;
        _cardSettingService   = cardSettingService;
        _waterBillingPeriodService = waterBillingPeriodService;
        _waterCircuitService  = waterCircuitService;
        _waterReportService   = waterReportService;
        _waterCostService     = waterCostService;
        _gasBillingPeriodService = gasBillingPeriodService;
        _gasCircuitService    = gasCircuitService;
        _gasReportService     = gasReportService;
        _gasCostService       = gasCostService;
        _chilledWaterCircuitService = chilledWaterCircuitService;
        _rtReportService      = rtReportService;
        _equipRealtimeService = equipRealtimeService;
        _equipReportService   = equipReportService;
        _equipProfileService  = equipProfileService;
    }

    [HttpGet("/EMS")]
    public async Task<IActionResult> Index()
    {
        if (!PermissionService.IsAdmin(User))
        {
            bool hasAny = PermissionService.EmsRoutes
                .Where(r => !string.Equals(r, "/EMS", StringComparison.OrdinalIgnoreCase))
                .Any(r => PermissionService.CanAccessPage(User, r));
            if (!hasAny)
                return Redirect("/ScadaPage");
        }

        var aCards = await _cardSettingService.GetEffectiveCardsAsync();
        return View(new EmsIndexViewModel
        {
            aVisibleCards = aCards.Where(c => c.isVisible).Select(c => c.Definition).ToList()
        });
    }

    /// <summary>取得所有可作為 EMS 需量選單的迴路（葉子＋含有啟用後裔的虛擬迴路）</summary>
    [HttpGet("/EMS/api/demand-circuits")]
    public async Task<IActionResult> GetDemandCircuits()
    {
        var circuits = await _repo.GetCircuitsForDemandAsync();
        return Ok(circuits.Select(c => new { id = c.nId, name = c.szName }));
    }

    /// <summary>取得指定迴路今日即時需量與今日最高需量</summary>
    [HttpGet("/EMS/api/demand-today")]
    public async Task<IActionResult> GetDemandToday([FromQuery] int? circuitId)
    {
        if (circuitId == null)
            return BadRequest(new { error = "circuitId 不得為空" });

        var result = await _repo.GetTodayDemandByCircuitIdAsync(circuitId.Value);
        if (result == null)
            return Ok(new { hasData = false });

        return Ok(new
        {
            hasData   = true,
            currentKW = result.dCurrentKW,
            maxKW     = result.dMaxKW,
            maxAt     = result.dtMaxAt.HasValue ? result.dtMaxAt.Value.ToString("HH:mm") : null,
            quality   = result.nQuality
        });
    }

    /// <summary>取得指定迴路今日需量趨勢資料（折線圖用）</summary>
    [HttpGet("/EMS/api/demand-trend")]
    public async Task<IActionResult> GetDemandTrend([FromQuery] int? circuitId)
    {
        if (circuitId == null)
            return BadRequest(new { error = "circuitId 不得為空" });

        var points = await _repo.GetTodayDemandTrendByCircuitIdAsync(circuitId.Value);
        return Ok(points.Select(p => new
        {
            t = p.dtTimestamp.ToString("HH:mm"),
            v = p.dDemandKW,
            q = p.nQuality
        }));
    }

    /// <summary>
    /// 取得主要電表資訊卡資料。
    /// 實體主表 → mode='realtime-by-sid'，回 4 組 { sid, pointName, unit }（前端走 /api/realtime/by-sids 輪詢）；
    /// 虛擬主表 → mode='aggregated'，回 4 組 { unit }（前端走 /EMS/api/main-meter-values 輪詢聚合值）。
    /// </summary>
    [HttpGet("/EMS/api/main-meter-info")]
    public async Task<IActionResult> GetMainMeterInfo()
    {
        var main = await _circuitService.GetMainMeterAsync();
        if (main == null)
            return Ok(new { hasMainMeter = false });

        // SID → (點位名, 單位) 查找表（Modbus + Calculated + DB 全來源）
        var modbus = await _repo.GetAllModbusPointsAsync();
        var calc = await _repo.GetAllCalculatedPointsAsync();
        var dbPts = await _repo.GetAllDbPointsAsync();
        var lookup = new Dictionary<string, (string szName, string szUnit)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in modbus) lookup.TryAdd(p.szSID, (p.szName, p.szUnit ?? string.Empty));
        foreach (var p in calc) lookup.TryAdd(p.szSID, (p.szName, p.szUnit ?? string.Empty));
        foreach (var p in dbPts) lookup.TryAdd(p.szSID, (p.szName, p.szUnit ?? string.Empty));

        // 虛擬主表：unit 取自「參與聚合的第一顆子孫葉子」對應角色的 SID（無綁定 → unit=""）
        bool isVirtualMain = string.IsNullOrWhiteSpace(main.szSID);
        if (isVirtualMain)
        {
            var leaves = await _circuitService.GetLeavesUnderAsync(main.nId);
            var ordered = leaves
                .OrderBy(l => l.Leaf.nSortOrder)
                .ThenBy(l => l.Leaf.nId)
                .ToList();

            string FirstUnit(Func<Common.Data.Models.EnergyCircuitModel, string?> pickSid)
            {
                foreach (var l in ordered)
                {
                    var szSid = pickSid(l.Leaf);
                    if (!string.IsNullOrWhiteSpace(szSid) && lookup.TryGetValue(szSid, out var v))
                        return v.szUnit;
                }
                return string.Empty;
            }

            return Ok(new
            {
                hasMainMeter = true,
                mode        = "aggregated",
                name        = main.szName,
                voltage     = new { unit = FirstUnit(x => x.szVoltageSID) },
                current     = new { unit = FirstUnit(x => x.szCurrentSID) },
                power       = new { unit = FirstUnit(x => x.szPowerSID) },
                powerFactor = new { unit = FirstUnit(x => x.szPowerFactorSID) }
            });
        }

        // 實體主表：維持既有 by-sid 語意
        object? Resolve(string? szSid)
        {
            if (string.IsNullOrWhiteSpace(szSid)) return null;
            return lookup.TryGetValue(szSid, out var v)
                ? new { sid = szSid, pointName = v.szName, unit = v.szUnit }
                : new { sid = szSid, pointName = szSid, unit = string.Empty };
        }

        return Ok(new
        {
            hasMainMeter = true,
            mode        = "realtime-by-sid",
            name        = main.szName,
            voltage     = Resolve(main.szVoltageSID),
            current     = Resolve(main.szCurrentSID),
            power       = Resolve(main.szPowerSID),
            powerFactor = Resolve(main.szPowerFactorSID)
        });
    }

    /// <summary>
    /// 虛擬主要電表的 V/I/P/PF 聚合值（每 5 秒由 ems.js 輪詢一次）。
    /// 實體主表不走此 API — 呼叫時回 hasMainMeter=false 或 hasMainMeter=true 但無值（前端不會呼叫，此為防呆）。
    /// </summary>
    [HttpGet("/EMS/api/main-meter-values")]
    public async Task<IActionResult> GetMainMeterValues()
    {
        var main = await _circuitService.GetMainMeterAsync();
        if (main == null || !string.IsNullOrWhiteSpace(main.szSID))
            return Ok(new EmsMainMeterValuesDto());

        return Ok(await _aggregation.ComputeAsync(main.nId));
    }

    /// <summary>
    /// 電費狀態卡 — 指定迴路（未指定 = 主要電表/根迴路）本期各時段 kWh 與流動電費。
    /// 依採用方案型態自適應（tou 時段明細 / progressive 級距 / flat 當季單價）；未選方案回 hasPlan=false。
    /// </summary>
    [HttpGet("/EMS/api/electricity-cost")]
    public async Task<IActionResult> GetElectricityCost([FromQuery] int? circuitId)
    {
        return Ok(await _costService.GetStatusAsync(circuitId));
    }

    /// <summary>取得完整迴路階層（flat 清單，前端組樹）</summary>
    [HttpGet("/EMS/api/circuit-tree")]
    public async Task<IActionResult> GetCircuitTree()
    {
        var nodes = await _circuitService.GetAllAsync();
        return Ok(nodes.Select(n => new EnergyCircuitNodeViewModel
        {
            id              = n.nId,
            name            = n.szName,
            parentId        = n.nParentId,
            sortOrder       = n.nSortOrder,
            sid             = n.szSID,
            maxKwh          = n.dMaxKwh,
            sign            = n.nSign,
            isDemandEnabled = n.isIsDemandEnabled,
            description     = n.szDescription
        }));
    }

    /// <summary>取得指定迴路的累計用電資料（長條圖用）</summary>
    /// <param name="circuitId">迴路 ID</param>
    /// <param name="granularity">month / day / hour</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/circuit-energy")]
    public async Task<IActionResult> GetCircuitEnergy(
        [FromQuery] int? circuitId,
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (circuitId == null || string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "circuitId, granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try
        {
            (dtStart, dtEnd) = await ParsePivotAsync(granularity, pivot);
        }
        catch
        {
            return BadRequest(new { error = "pivot 格式不正確" });
        }

        var result = await _reportService.GetReportAsync(circuitId.Value, granularity, dtStart, dtEnd);
        return Ok(new EmsCircuitEnergyDto
        {
            labels = result.buckets.Select(b => b.szLabel).ToList(),
            values = result.buckets.Select(b => b.dKwh).ToList()
        });
    }

    /// <summary>取得主要電表基本資訊（IsMainMeter = 1，全系統唯一）</summary>
    [HttpGet("/EMS/api/main-meter")]
    public async Task<IActionResult> GetMainMeter()
    {
        var meter = await _circuitService.GetMainMeterAsync();
        if (meter == null)
            return Ok(new EmsMainMeterDto { hasMainMeter = false });

        return Ok(new EmsMainMeterDto
        {
            hasMainMeter = true,
            id           = meter.nId,
            name         = meter.szName,
            hasChildren  = await _circuitService.HasChildrenAsync(meter.nId)
        });
    }

    /// <summary>主要電表直接子迴路在區間內的用電量拆解（圓餅圖用）；無子迴路時回主要電表自己一筆</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/main-meter-breakdown")]
    public async Task<IActionResult> GetMainMeterBreakdown(
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParsePivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var meter = await _circuitService.GetMainMeterAsync();
        if (meter == null)
            return Ok(new EmsMainMeterBreakdownDto { hasMainMeter = false });

        var dto = new EmsMainMeterBreakdownDto { hasMainMeter = true, meterName = meter.szName };

        var children = await _circuitService.GetDirectChildrenAsync(meter.nId);
        if (children.Count == 0)
        {
            dto.items.Add(new EmsBreakdownItemDto
            {
                id   = meter.nId,
                name = meter.szName,
                kwh  = await _reportService.GetTotalKwhAsync(meter.nId, granularity, dtStart, dtEnd)
            });
            return Ok(dto);
        }

        foreach (var child in children)
        {
            // 子迴路內部 leaves 的 sign 已由計算核心累乘（相對於 child），child 自己對父的方向在這裡補乘
            var nChildSign = child.nSign == -1 ? -1 : 1;
            var dKwh = await _reportService.GetTotalKwhAsync(child.nId, granularity, dtStart, dtEnd);
            dto.items.Add(new EmsBreakdownItemDto
            {
                id   = child.nId,
                name = child.szName,
                kwh  = Math.Round(dKwh * nChildSign, 3)
            });
        }
        return Ok(dto);
    }

    /// <summary>主要電表 + 各直接子迴路的本期 vs 去年同期用電比較（比較表用）；首列為主要電表</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/main-meter-yoy")]
    public async Task<IActionResult> GetMainMeterYoy(
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParsePivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var meter = await _circuitService.GetMainMeterAsync();
        if (meter == null)
            return Ok(new EmsMainMeterYoyDto { hasMainMeter = false });

        // 去年同期：重建去年 pivot 再走同一解析（月/日粒度會取去年期別設定，2/29 → 2/28）
        DateTime dtLastStart, dtLastEnd;
        try { (dtLastStart, dtLastEnd) = await ParsePivotAsync(granularity, LastYearPivot(granularity, pivot)); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var dto = new EmsMainMeterYoyDto { hasMainMeter = true };
        dto.rows.Add(await BuildYoyRowAsync(meter.nId, meter.szName, true, 1,
            granularity, dtStart, dtEnd, dtLastStart, dtLastEnd));

        foreach (var child in await _circuitService.GetDirectChildrenAsync(meter.nId))
        {
            var nChildSign = child.nSign == -1 ? -1 : 1;
            dto.rows.Add(await BuildYoyRowAsync(child.nId, child.szName, false, nChildSign,
                granularity, dtStart, dtEnd, dtLastStart, dtLastEnd));
        }
        return Ok(dto);
    }

    private async Task<EmsYoyRowDto> BuildYoyRowAsync(
        int nCircuitId, string szName, bool isMainMeter, int nSign,
        string granularity, DateTime dtStart, DateTime dtEnd, DateTime dtLastStart, DateTime dtLastEnd)
    {
        var dCurrent  = Math.Round(await _reportService.GetTotalKwhAsync(nCircuitId, granularity, dtStart, dtEnd) * nSign, 3);
        var dLastYear = Math.Round(await _reportService.GetTotalKwhAsync(nCircuitId, granularity, dtLastStart, dtLastEnd) * nSign, 3);
        var dDiff     = Math.Round(dCurrent - dLastYear, 3);
        return new EmsYoyRowDto
        {
            id          = nCircuitId,
            name        = szName,
            isMainMeter = isMainMeter,
            currentKwh  = dCurrent,
            lastYearKwh = dLastYear,
            diffKwh     = dDiff,
            // 去年為 0（含無資料）時無法算增減比，回 null 由前端顯示 --；負底取絕對值保留增減方向語意
            pctChange   = dLastYear == 0 ? null : Math.Round(dDiff / Math.Abs(dLastYear) * 100, 1)
        };
    }

    /// <summary>主要電表 + 各直接子迴路的本期 vs 去年同期「流動電費」比較（電費比較表用）；首列為主要電表</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/main-meter-cost-yoy")]
    public async Task<IActionResult> GetMainMeterCostYoy(
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParsePivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var meter = await _circuitService.GetMainMeterAsync();
        if (meter == null)
            return Ok(new EmsMainMeterCostYoyDto { hasMainMeter = false });

        // 去年同期：重建去年 pivot 再走同一解析（月/日粒度會取去年期別設定，2/29 → 2/28）
        DateTime dtLastStart, dtLastEnd;
        try { (dtLastStart, dtLastEnd) = await ParsePivotAsync(granularity, LastYearPivot(granularity, pivot)); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var dto = new EmsMainMeterCostYoyDto { hasMainMeter = true };
        dto.rows.Add(await BuildCostYoyRowAsync(meter.nId, meter.szName, true, 1,
            granularity, dtStart, dtEnd, dtLastStart, dtLastEnd, dto));

        foreach (var child in await _circuitService.GetDirectChildrenAsync(meter.nId))
        {
            var nChildSign = child.nSign == -1 ? -1 : 1;
            dto.rows.Add(await BuildCostYoyRowAsync(child.nId, child.szName, false, nChildSign,
                granularity, dtStart, dtEnd, dtLastStart, dtLastEnd, dto));
        }
        return Ok(dto);
    }

    private async Task<EmsCostYoyRowDto> BuildCostYoyRowAsync(
        int nCircuitId, string szName, bool isMainMeter, int nSign,
        string granularity, DateTime dtStart, DateTime dtEnd, DateTime dtLastStart, DateTime dtLastEnd,
        EmsMainMeterCostYoyDto dto)
    {
        var (dCurRaw, isEstCur)   = await _costService.GetTotalCostAsync(nCircuitId, granularity, dtStart, dtEnd);
        var (dLastRaw, isEstLast) = await _costService.GetTotalCostAsync(nCircuitId, granularity, dtLastStart, dtLastEnd);
        if (isEstCur || isEstLast) dto.isEstimated = true;

        var dCurrent  = Math.Round(dCurRaw * nSign, 1);
        var dLastYear = Math.Round(dLastRaw * nSign, 1);
        var dDiff     = Math.Round(dCurrent - dLastYear, 1);
        return new EmsCostYoyRowDto
        {
            id           = nCircuitId,
            name         = szName,
            isMainMeter  = isMainMeter,
            currentCost  = dCurrent,
            lastYearCost = dLastYear,
            diffCost     = dDiff,
            // 去年為 0（含無資料）時無法算增減比，回 null 由前端顯示 --；負底取絕對值保留增減方向語意
            pctChange    = dLastYear == 0 ? null : Math.Round(dDiff / Math.Abs(dLastYear) * 100, 1)
        };
    }

    // ────────────────────────── 水表三卡 API ──────────────────────────

    /// <summary>取得自來水表迴路完整階層（flat 清單，前端組樹；用水量卡根迴路名 + 水費卡下拉用）</summary>
    [HttpGet("/EMS/api/water-circuit-tree")]
    public async Task<IActionResult> GetWaterCircuitTree()
    {
        var nodes = await _waterCircuitService.GetAllAsync();
        return Ok(nodes.Select(n => new
        {
            id        = n.nId,
            name      = n.szName,
            parentId  = n.nParentId,
            sortOrder = n.nSortOrder
        }));
    }

    /// <summary>取得指定自來水表迴路的用水量（長條圖用）；circuitId 空 = 根迴路（全廠）</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/water-usage")]
    public async Task<IActionResult> GetWaterUsage(
        [FromQuery] int? circuitId,
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParseWaterPivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        int nCircuitId;
        if (circuitId.HasValue)
        {
            nCircuitId = circuitId.Value;
        }
        else
        {
            var root = await _waterCircuitService.GetRootAsync();
            if (root == null)
                return Ok(new EmsWaterUsageDto());   // 未建根迴路 → 空資料（前端顯示無資料）
            nCircuitId = root.nId;
        }

        var result = await _waterReportService.GetReportAsync(nCircuitId, granularity, dtStart, dtEnd);
        return Ok(new EmsWaterUsageDto
        {
            labels     = result.buckets.Select(b => b.szLabel).ToList(),
            values     = result.buckets.Select(b => b.dM3).ToList(),
            hasWarning = result.isHasWarning
        });
    }

    /// <summary>根迴路直接子迴路在區間內的用水量拆解（圓餅圖用）；無子迴路時回根迴路自己一筆</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/water-breakdown")]
    public async Task<IActionResult> GetWaterBreakdown(
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParseWaterPivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var root = await _waterCircuitService.GetRootAsync();
        if (root == null)
            return Ok(new EmsWaterBreakdownDto { hasRoot = false });

        var dto = new EmsWaterBreakdownDto { hasRoot = true };

        var children = await _waterCircuitService.GetDirectChildrenAsync(root.nId);
        if (children.Count == 0)
        {
            var (dTotalM3, isWarn) = await _waterReportService.GetTotalM3Async(root.nId, granularity, dtStart, dtEnd);
            if (isWarn) dto.hasWarning = true;
            dto.items.Add(new EmsWaterBreakdownItemDto
            {
                id   = root.nId,
                name = root.szName,
                m3   = Math.Round(dTotalM3, 3)
            });
            return Ok(dto);
        }

        foreach (var child in children)
        {
            // 子迴路內部葉子的 sign 已由計算核心累乘（相對於 child），child 自己對父的方向在這裡補乘
            // （負值不入餅，由前端列於下方小字 — 比照 main-meter-breakdown）
            var nChildSign = child.nSign == -1 ? -1 : 1;
            var (dTotalM3, isWarn) = await _waterReportService.GetTotalM3Async(child.nId, granularity, dtStart, dtEnd);
            if (isWarn) dto.hasWarning = true;
            dto.items.Add(new EmsWaterBreakdownItemDto
            {
                id   = child.nId,
                name = child.szName,
                m3   = Math.Round(dTotalM3 * nChildSign, 3)
            });
        }
        return Ok(dto);
    }

    /// <summary>水費狀態卡 — 指定自來水表迴路（未指定 = 根迴路）本期累計 m³ 與級距水費</summary>
    [HttpGet("/EMS/api/water-cost")]
    public async Task<IActionResult> GetWaterCost([FromQuery] int? circuitId)
    {
        return Ok(await _waterCostService.GetStatusAsync(circuitId));
    }

    // ────────────────────────── 氣表三卡 API ──────────────────────────

    /// <summary>取得氣表迴路完整階層（flat 清單，前端組樹；用氣量卡根迴路名 + 氣費卡下拉用）</summary>
    [HttpGet("/EMS/api/gas-circuit-tree")]
    public async Task<IActionResult> GetGasCircuitTree()
    {
        var nodes = await _gasCircuitService.GetAllAsync();
        return Ok(nodes.Select(n => new
        {
            id        = n.nId,
            name      = n.szName,
            parentId  = n.nParentId,
            sortOrder = n.nSortOrder,
            sid       = n.szSID
        }));
    }

    /// <summary>取得指定氣表迴路的用氣量（長條圖用）；circuitId 空 = 根迴路（全廠）</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/gas-usage")]
    public async Task<IActionResult> GetGasUsage(
        [FromQuery] int? circuitId,
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParseGasPivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        int nCircuitId;
        if (circuitId.HasValue)
        {
            nCircuitId = circuitId.Value;
        }
        else
        {
            var root = await _gasCircuitService.GetRootAsync();
            if (root == null)
                return Ok(new EmsGasUsageDto());   // 未建根迴路 → 空資料（前端顯示無資料）
            nCircuitId = root.nId;
        }

        var result = await _gasReportService.GetReportAsync(nCircuitId, granularity, dtStart, dtEnd);
        return Ok(new EmsGasUsageDto
        {
            labels     = result.buckets.Select(b => b.szLabel).ToList(),
            values     = result.buckets.Select(b => b.dM3).ToList(),
            hasWarning = result.isHasWarning
        });
    }

    /// <summary>根迴路直接子迴路在區間內的用氣量拆解（圓餅圖用）；無子迴路時回根迴路自己一筆</summary>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/gas-breakdown")]
    public async Task<IActionResult> GetGasBreakdown(
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = await ParseGasPivotAsync(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var root = await _gasCircuitService.GetRootAsync();
        if (root == null)
            return Ok(new EmsGasBreakdownDto { hasRoot = false });

        var dto = new EmsGasBreakdownDto { hasRoot = true };

        var children = await _gasCircuitService.GetDirectChildrenAsync(root.nId);
        if (children.Count == 0)
        {
            var (dTotalM3, isWarn) = await _gasReportService.GetTotalM3Async(root.nId, granularity, dtStart, dtEnd);
            if (isWarn) dto.hasWarning = true;
            dto.items.Add(new EmsGasBreakdownItemDto
            {
                id   = root.nId,
                name = root.szName,
                m3   = Math.Round(dTotalM3, 3)
            });
            return Ok(dto);
        }

        foreach (var child in children)
        {
            // 子迴路內部葉子的 sign 已由計算核心累乘（相對於 child），child 自己對父的方向在這裡補乘
            // （負值不入餅，由前端列於下方小字 — 比照 water-breakdown）
            var nChildSign = child.nSign == -1 ? -1 : 1;
            var (dTotalM3, isWarn) = await _gasReportService.GetTotalM3Async(child.nId, granularity, dtStart, dtEnd);
            if (isWarn) dto.hasWarning = true;
            dto.items.Add(new EmsGasBreakdownItemDto
            {
                id   = child.nId,
                name = child.szName,
                m3   = Math.Round(dTotalM3 * nChildSign, 3)
            });
        }
        return Ok(dto);
    }

    /// <summary>氣費狀態卡 — 指定氣表迴路（未指定 = 根迴路）本期累計 m³ 與級距氣費</summary>
    [HttpGet("/EMS/api/gas-cost")]
    public async Task<IActionResult> GetGasCost([FromQuery] int? circuitId)
    {
        return Ok(await _gasCostService.GetStatusAsync(circuitId));
    }

    // ─────────────────── 空調水系統（冷凍噸）迴路資訊 API ───────────────────

    /// <summary>取得空調水系統迴路完整階層（flat 清單，前端組樹）</summary>
    [HttpGet("/EMS/api/chilled-water-circuit-tree")]
    public async Task<IActionResult> GetChilledWaterCircuitTree()
    {
        var nodes = await _chilledWaterCircuitService.GetAllAsync();
        return Ok(nodes.Select(n => new
        {
            id        = n.nId,
            name      = n.szName,
            parentId  = n.nParentId,
            sortOrder = n.nSortOrder
        }));
    }

    /// <summary>取得指定空調水系統迴路的冷量 RT·h（長條圖用）</summary>
    /// <param name="circuitId">迴路 ID</param>
    /// <param name="granularity">month / day / hour（同 circuit-energy）</param>
    /// <param name="pivot">month=年份(2026)；day=年月(2026-06)；hour=日期(2026-06-29)</param>
    [HttpGet("/EMS/api/chilled-water-rth")]
    public async Task<IActionResult> GetChilledWaterRth(
        [FromQuery] int? circuitId,
        [FromQuery] string? granularity,
        [FromQuery] string? pivot)
    {
        if (circuitId == null || string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "circuitId, granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = ParseCalendarPivot(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        var result = await _rtReportService.GetReportAsync(circuitId.Value, granularity, dtStart, dtEnd);
        return Ok(new EmsChilledWaterRthDto
        {
            labels     = result.buckets.Select(b => b.szLabel).ToList(),
            values     = result.buckets.Select(b => b.dRtHour).ToList(),
            hasWarning = result.isHasWarning
        });
    }

    // ─────────────────── 設備效率診斷（冰機 / 空壓機）API ───────────────────

    /// <summary>診斷頁 / 卡片共用的權限：該 kind 的診斷頁或 EMS 首頁可看即可</summary>
    private bool CanViewEquipment(string szKind) =>
        PermissionService.CanAccessPage(User, szKind == ScadaEngine.Common.Equipment.EquipmentKind.Compressor
            ? "/CompressorEfficiency" : "/ChillerEfficiency")
        || PermissionService.CanAccessPage(User, "/EMS");

    /// <summary>即時：每台主指標 + 輔助指標 + 評級（30 秒輪詢）</summary>
    [HttpGet("/EMS/api/equipment/realtime")]
    public async Task<IActionResult> GetEquipmentRealtime([FromQuery] string? kind)
    {
        if (!ScadaEngine.Common.Equipment.EquipmentKind.IsValid(kind)) return BadRequest(new { error = "invalid_kind" });
        if (!CanViewEquipment(kind!)) return Forbid();
        return Ok(await _equipRealtimeService.GetRealtimeAsync(kind!));
    }

    /// <summary>
    /// 期間儀表板（曆制）：granularity = hour（日檢視，pivot=yyyy-MM-dd）/ day（月，pivot=yyyy-MM）/ month（年，pivot=yyyy）
    /// </summary>
    [HttpGet("/EMS/api/equipment/period")]
    public async Task<IActionResult> GetEquipmentPeriod(
        [FromQuery] string? kind, [FromQuery] string? granularity, [FromQuery] string? pivot)
    {
        if (!ScadaEngine.Common.Equipment.EquipmentKind.IsValid(kind)) return BadRequest(new { error = "invalid_kind" });
        if (!CanViewEquipment(kind!)) return Forbid();
        if (string.IsNullOrWhiteSpace(granularity) || string.IsNullOrWhiteSpace(pivot))
            return BadRequest(new { error = "granularity, pivot 皆為必填" });

        DateTime dtStart, dtEnd;
        try { (dtStart, dtEnd) = ParseCalendarPivot(granularity, pivot); }
        catch { return BadRequest(new { error = "pivot 格式不正確" }); }

        return Ok(await _equipReportService.GetPeriodAsync(kind!, granularity, dtStart, dtEnd));
    }

    /// <summary>EMS 首頁卡片：每台即時主指標 + 本曆月累計平均效率與運轉時數</summary>
    [HttpGet("/EMS/api/equipment/card")]
    public async Task<IActionResult> GetEquipmentCard([FromQuery] string? kind)
    {
        if (!ScadaEngine.Common.Equipment.EquipmentKind.IsValid(kind)) return BadRequest(new { error = "invalid_kind" });
        if (!CanViewEquipment(kind!)) return Forbid();

        var realtime = await _equipRealtimeService.GetRealtimeAsync(kind!);
        var profiles = await _equipProfileService.GetAllAsync(kind!, isEnabledOnly: true);
        var dtNow = DateTime.Now;
        return Ok(new EquipmentCardDto
        {
            kind = kind!,
            deviceCount = profiles.Count,
            items = realtime.items,
            month = await _equipReportService.GetMonthToDateAsync(kind!, profiles),
            monthLabel = dtNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// pivot → 報表服務的 (dtStart, dtEnd)，**電費期別**版（用電長條/圓餅/去年同期/電費卡）。
    /// month（年檢視）：pivot=年份 → 期別 1~12 月（報表月粒度語意 = 含頭尾期別）；
    /// day（月檢視）：pivot=YYYY-MM → 該期別的實際起訖日（日粒度 dtEnd 為含訖日）；
    /// hour（日檢視）：pivot=YYYY-MM-DD → 該日（維持自然日，不受期別影響）。
    ///
    /// ⚠️ 水表卡片請用 <see cref="ParseWaterPivotAsync"/>、氣表卡片請用 <see cref="ParseGasPivotAsync"/>、
    /// 空調水系統（冷凍噸）與設備效率診斷請用 <see cref="ParseCalendarPivot"/>（不走期別）—
    /// 刻意拆成四支而非加參數，讓「呼叫錯期別」變成編譯期選錯函式（看得見），
    /// 而不是靜默走錯期別（查不出來）。
    /// </summary>
    private Task<(DateTime Start, DateTime End)> ParsePivotAsync(string granularity, string pivot)
        => ParsePivotCoreAsync(granularity, pivot, PeriodKind.Electricity);

    /// <summary>
    /// pivot → (dtStart, dtEnd)，**水費期別**版（用水長條/圓餅/水費卡）。語意同電費版，
    /// 差別只在 day（月檢視）取的是水費期別的實際起訖日。
    /// </summary>
    private Task<(DateTime Start, DateTime End)> ParseWaterPivotAsync(string granularity, string pivot)
        => ParsePivotCoreAsync(granularity, pivot, PeriodKind.Water);

    /// <summary>
    /// pivot → (dtStart, dtEnd)，**氣費期別**版（用氣長條/圓餅/氣費卡）。語意同電/水費版，
    /// 差別只在 day（月檢視）取的是氣費期別的實際起訖日（該月被「刪除」時取吸收它的那一期）。
    /// </summary>
    private Task<(DateTime Start, DateTime End)> ParseGasPivotAsync(string granularity, string pivot)
        => ParsePivotCoreAsync(granularity, pivot, PeriodKind.Gas);

    /// <summary>
    /// pivot → (dtStart, dtEnd)，**曆制**版（空調水系統冷凍噸卡）。**刻意不吃任何月結期別** —
    /// <see cref="RefrigerationTonReportService"/> 月粒度已固定走曆月（綁能源申報、且冷凍噸不是算錢的報表），
    /// 這裡若套上水費／電費期別，同一迴路在本頁與 /RefrigerationTonReport 會出現不同數字。
    /// 無期別查詢 → 同步方法即可，不需 async。
    /// </summary>
    private static (DateTime Start, DateTime End) ParseCalendarPivot(string granularity, string pivot)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        switch (granularity)
        {
            case "month":
                {
                    var nYear = int.Parse(pivot);
                    return (new DateTime(nYear, 1, 1), new DateTime(nYear, 12, 1));
                }
            case "day":
                {
                    var dtYM = DateTime.ParseExact(pivot + "-01", "yyyy-MM-dd", ci);
                    // dtEnd 採「含訖」語意 = 最後一格 bucket 的起點（該曆月最後一天）
                    return (dtYM, dtYM.AddMonths(1).AddDays(-1));
                }
            case "hour":
                {
                    var dtDay = DateTime.ParseExact(pivot, "yyyy-MM-dd", ci);
                    return (dtDay, dtDay.AddHours(23));
                }
            default:
                throw new ArgumentException("不支援的 granularity");
        }
    }

    /// <summary>期別來源三選一 — 電費 / 水費 / 氣費各自獨立設定</summary>
    private enum PeriodKind { Electricity, Water, Gas }

    private async Task<(DateTime Start, DateTime End)> ParsePivotCoreAsync(
        string granularity, string pivot, PeriodKind kind)
    {
        switch (granularity)
        {
            case "month":
                {
                    var nYear = int.Parse(pivot);
                    return (new DateTime(nYear, 1, 1), new DateTime(nYear, 12, 1));
                }
            case "day":
                {
                    var dtYM = DateTime.ParseExact(pivot + "-01", "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture);
                    var period = kind switch
                    {
                        PeriodKind.Water => await _waterBillingPeriodService.GetPeriodAsync(dtYM.Year, dtYM.Month),
                        // 氣費期別可被刪除（兩月一期）→ 取吸收該月的那一期；全年皆刪則退回自然月
                        PeriodKind.Gas => await _gasBillingPeriodService.GetPeriodAsync(dtYM.Year, dtYM.Month)
                                          ?? new BillingPeriodRange
                                          {
                                              nYear = dtYM.Year,
                                              nMonth = dtYM.Month,
                                              dtStart = dtYM,
                                              dtEndExclusive = dtYM.AddMonths(1),
                                          },
                        _ => await _billingPeriodService.GetPeriodAsync(dtYM.Year, dtYM.Month),
                    };
                    return (period.dtStart, period.dtEndInclusive);
                }
            case "hour":
                {
                    var dtDay = DateTime.ParseExact(pivot, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture);
                    // dtEnd 需與 day 粒度一致採「含訖」語意 = 最後一格 bucket 的起點；
                    // 給 exclusive next-day midnight 會讓 BuildBoundaries 多生 07-07 00:00~01:00
                    // 這格，觸發 bHourCrossDay=true → 標籤變成 "MM/dd HH:00"
                    return (dtDay, dtDay.AddHours(23));
                }
            default:
                throw new ArgumentException("不支援的 granularity");
        }
    }

    /// <summary>去年同期 pivot 字串（與前端 lastYearPivotStr 同邏輯；hour 粒度 2/29 → 2/28）</summary>
    private static string LastYearPivot(string granularity, string pivot)
    {
        switch (granularity)
        {
            case "month":
                return (int.Parse(pivot) - 1).ToString();
            case "day":
                {
                    var aParts = pivot.Split('-');
                    return $"{int.Parse(aParts[0]) - 1}-{aParts[1]}";
                }
            default:
                {
                    var aParts = pivot.Split('-');
                    var szDay = aParts[1] == "02" && aParts[2] == "29" ? "28" : aParts[2];
                    return $"{int.Parse(aParts[0]) - 1}-{aParts[1]}-{szDay}";
                }
        }
    }
}
