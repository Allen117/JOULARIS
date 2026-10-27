using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.History.Models;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.HistoryStatistics;

namespace ScadaEngine.Web.Features.History.Controllers;

[Authorize]
public class HistoryController : Controller
{
    private readonly IDataRepository _dataRepository;
    private readonly HistoryQueryService _queryService;
    private readonly SystemParameterService _systemParameterService;
    private readonly HistoryExcelExporter _excelExporter;
    private readonly ILogger<HistoryController> _logger;
    private readonly IStringLocalizer<HistoryController> _l;

    public HistoryController(
        IDataRepository dataRepository,
        HistoryQueryService queryService,
        SystemParameterService systemParameterService,
        HistoryExcelExporter excelExporter,
        ILogger<HistoryController> logger,
        IStringLocalizer<HistoryController> localizer)
    {
        _dataRepository = dataRepository;
        _queryService = queryService;
        _systemParameterService = systemParameterService;
        _excelExporter = excelExporter;
        _logger = logger;
        _l = localizer;
    }

    /// <summary>
    /// GET /HistoryData — 歷史資料頁面
    /// </summary>
    [HttpGet("/HistoryData")]
    public async Task<IActionResult> HistoryData()
    {
        var coordinatorList = (await _dataRepository.GetAllCoordinatorsAsync()).ToList();
        var dbCoordinatorList = (await _dataRepository.GetAllDbCoordinatorsAsync()).ToList();
        var pointList = (await _dataRepository.GetAllModbusPointsAsync()).ToList();

        // 合併計算點位
        var calcPointsAll = (await _dataRepository.GetAllCalculatedPointsAsync())
            .Where(c => c.isEnabled).ToList();
        pointList.AddRange(calcPointsAll.Select(c => new ModbusPointModel { szSID = c.szSID, szName = c.szName, szUnit = c.szUnit }));

        // 合併 DB 來源點位
        pointList.AddRange((await _dataRepository.GetAllDbPointsAsync())
            .Select(p => new ModbusPointModel { szSID = p.szSID, szName = p.szName, szUnit = p.szUnit ?? string.Empty }));

        // 合併 OPC UA 來源點位
        pointList.AddRange((await _dataRepository.GetAllOpcUaPointsAsync())
            .Select(p => new ModbusPointModel { szSID = p.szSID, szName = p.szName, szUnit = p.szUnit ?? string.Empty }));

        // 合併需量虛擬點位（DMD-{kWhSID}，側欄「需量」桶走 data-sid-prefix="DMD-"）
        var szDemandSuffix = _l["history.demand.point_suffix"].Value;
        pointList.AddRange((await _dataRepository.GetDemandPointInfosAsync())
            .Select(d => new ModbusPointModel { szSID = $"DMD-{d.szSID}", szName = $"{d.szName} {szDemandSuffix}", szUnit = "kW" }));

        // 計算點位群組名稱（供側欄分群）
        var calcPointGroups = calcPointsAll
            .Select(c => c.szGroupName)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct().OrderBy(g => g).ToList();

        // SID → GroupName 對照（供前端 JS 過濾）
        var calcGroupMap = calcPointsAll.ToDictionary(c => c.szSID, c => c.szGroupName ?? "");

        var viewModel = new HistoryTrendViewModel
        {
            CoordinatorList = coordinatorList,
            DbCoordinatorList = dbCoordinatorList,
            PointList = pointList,
            CalcPointGroups = calcPointGroups,
            CalcGroupMap = calcGroupMap,
            dtStartTime = DateTime.Now.AddHours(-24),
            dtEndTime = DateTime.Now
        };

        return View(viewModel);
    }

    /// <summary>
    /// GET /api/history/data?szSID=xxx&szStart=...&szEnd=...&nInterval=15
    /// 查詢 HistoryData 資料表並回傳 JSON，供前端 Chart.js 使用。
    /// nInterval：取樣間隔（分鐘），0 = 原始；>0 = 整點對齊取每 bucket 首筆
    /// 統計走 HistoryStatisticsCalculator（只算 Good、累計值差異值分段相加），與匯出「統計」分頁同一份。
    /// </summary>
    [HttpGet("~/api/history/data")]
    public async Task<IActionResult> GetHistoryData(
        [FromQuery] string szSID,
        [FromQuery] string szStart,
        [FromQuery] string szEnd,
        [FromQuery] int nInterval = 0)
    {
        if (string.IsNullOrWhiteSpace(szSID))
            return BadRequest(new { success = false, message = "SID 不可為空" });

        if (!DateTime.TryParse(szStart, out var dtStart))
            dtStart = DateTime.Now.AddHours(-24);

        if (!DateTime.TryParse(szEnd, out var dtEnd))
            dtEnd = DateTime.Now;

        if (dtStart >= dtEnd)
            return BadRequest(new { success = false, message = "開始時間必須早於結束時間" });

        if (nInterval < 0) nInterval = 0;

        try
        {
            var param = await _systemParameterService.GetAsync();
            var points = await _queryService.ResolvePointsAsync([szSID], _l["history.demand.point_suffix"].Value);
            points.TryGetValue(szSID, out var point);

            var r = await _queryService.QueryPointAsync(szSID, point, dtStart, dtEnd, nInterval,
                param.nHistoryMaxRecordsPerPoint, param.dCumulativeJitterTolerancePct);
            var st = r.stats;

            return Ok(new
            {
                success      = true,
                szSID        = r.szSID,
                szName       = r.szName,
                szUnit       = r.szUnit,
                nCount       = st.nCount,
                nValidCount  = st.nValidCount,
                nMaxRecords  = param.nHistoryMaxRecordsPerPoint,
                isLimited    = r.isLimited,
                szPointKind  = st.kind.ToString(),
                szUnitGroup  = st.szUnitGroup,
                dMin         = st.dMin,
                dMax         = st.dMax,
                dAvg         = st.dAvg,
                dStdDev      = st.dStdDev,
                dDiff        = st.dDiff,
                nDropCount   = st.nDropCount,
                nSpikeCount  = st.nSpikeCount,
                isStartInset = st.isStartInset,
                isEndInset   = st.isEndInset,
                data         = r.data.Select(d => new
                {
                    t = d.dtTimestamp.ToString("yyyy-MM-ddTHH:mm:ss"),
                    v = d.fValue,
                    q = d.nQuality == 1 ? "Good" : "Bad"
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢歷史資料失敗: SID={SID}", szSID);
            return StatusCode(500, new { success = false, message = "查詢失敗，請稍後再試" });
        }
    }

    /// <summary>
    /// POST /api/history/export — 依趨勢頁同一組參數重查並產生 Excel（資料分頁 + 統計 + 圖表）。
    /// 先以時間與間隔預估儲存格數（不查 DB），超過系統參數上限回 400 + 預估值與上限。
    /// </summary>
    [HttpPost("~/api/history/export")]
    public async Task<IActionResult> Export([FromBody] HistoryExportRequest request)
    {
        var sids = (request.sids ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (sids.Count == 0)
            return BadRequest(new { success = false, code = "no_points" });
        if (!DateTime.TryParse(request.szStart, out var dtStart) || !DateTime.TryParse(request.szEnd, out var dtEnd) || dtStart >= dtEnd)
            return BadRequest(new { success = false, code = "bad_time_range" });
        var nInterval = Math.Max(request.nInterval, 0);

        try
        {
            var param = await _systemParameterService.GetAsync();
            var nEstimated = HistoryQueryService.EstimateExportCells(sids.Count, dtStart, dtEnd, nInterval, param.nHistoryMaxRecordsPerPoint);
            if (nEstimated > param.nHistoryExportMaxCells)
            {
                return BadRequest(new
                {
                    success = false,
                    code = "export_limit",
                    nEstimatedCells = nEstimated,
                    nMaxCells = param.nHistoryExportMaxCells
                });
            }

            var points = await _queryService.ResolvePointsAsync(sids, _l["history.demand.point_suffix"].Value);
            var results = new List<HistoryPointResult>();
            foreach (var szSID in sids)
            {
                points.TryGetValue(szSID, out var point);
                results.Add(await _queryService.QueryPointAsync(szSID, point, dtStart, dtEnd, nInterval,
                    param.nHistoryMaxRecordsPerPoint, param.dCumulativeJitterTolerancePct));
            }

            var bytes = _excelExporter.Export(results, dtStart, dtEnd, nInterval);
            var szFileName = $"{_excelExporter.FileNamePrefix}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", szFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "匯出歷史資料失敗: SIDs={SIDs}", string.Join(",", sids));
            return StatusCode(500, new { success = false, code = "export_failed" });
        }
    }
}
