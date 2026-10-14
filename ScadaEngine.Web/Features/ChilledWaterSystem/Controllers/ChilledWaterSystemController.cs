using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.ChilledWaterSystem.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.ChilledWaterSystem.Controllers;

[Authorize]
[Route("[controller]")]
public class ChilledWaterSystemController : Controller
{
    private static readonly string[] WaterUnitKeywords = { "RT", "USRT", "噸", "頓", "冷凍噸" };

    private static bool IsWaterUnit(string? szUnit) =>
        !string.IsNullOrWhiteSpace(szUnit) &&
        WaterUnitKeywords.Any(k => szUnit.Contains(k, StringComparison.OrdinalIgnoreCase));

    private readonly WaterCircuitService _service;
    private readonly IDataRepository _repository;
    private readonly ILogger<ChilledWaterSystemController> _logger;

    public ChilledWaterSystemController(
        WaterCircuitService service,
        IDataRepository repository,
        ILogger<ChilledWaterSystemController> logger)
    {
        _service = service;
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("/ChilledWaterSystem")]
    public IActionResult Index() => View();

    [HttpGet("api/tree")]
    public async Task<IActionResult> GetTree()
    {
        var nodes = await _service.GetAllAsync();
        return Ok(nodes.Select(n => new WaterCircuitNodeViewModel
        {
            id = n.nId,
            name = n.szName,
            parentId = n.nParentId,
            sortOrder = n.nSortOrder,
            sid = n.szSID,
            description = n.szDescription
        }));
    }

    [HttpGet("api/sids")]
    public async Task<IActionResult> GetSidOptions()
    {
        var modbus = await _repository.GetAllModbusPointsAsync();
        var calc = await _repository.GetAllCalculatedPointsAsync();
        var dbPts = await _repository.GetAllDbPointsAsync();
        var coords = (await _repository.GetAllCoordinatorsAsync()).ToList();
        var dbCoords = (await _repository.GetAllDbCoordinatorsAsync()).ToList();

        string ResolveDeviceName(string szSID)
        {
            var nHyphen = szSID.IndexOf('-');
            if (nHyphen <= 0) return string.Empty;
            if (!int.TryParse(szSID[..nHyphen], out var nPrefix)) return string.Empty;

            var nCoordId = nPrefix / 65536;
            var nSubModbusId = (nPrefix % 65536) / 256;
            var coord = coords.FirstOrDefault(c => c.Id == nCoordId);
            if (coord == null) return string.Empty;

            var ids = (coord.szModbusID ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var names = (coord.szDeviceName ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (ids.Length <= 1) return coord.szName ?? string.Empty;

            for (int i = 0; i < ids.Length; i++)
            {
                if (int.TryParse(ids[i], out var nMid) && nMid == nSubModbusId)
                    return (i < names.Length && !string.IsNullOrWhiteSpace(names[i]))
                        ? names[i] : (coord.szName ?? string.Empty);
            }
            return coord.szName ?? string.Empty;
        }

        var list = modbus
            .Where(p => IsWaterUnit(p.szUnit))
            .Select(p => new WaterCircuitSidOptionDto
            {
                sid = p.szSID,
                name = p.szName,
                unit = p.szUnit ?? string.Empty,
                source = "Modbus",
                deviceName = ResolveDeviceName(p.szSID)
            })
            .Concat(calc
                .Where(p => IsWaterUnit(p.szUnit))
                .Select(p => new WaterCircuitSidOptionDto
                {
                    sid = p.szSID,
                    name = p.szName,
                    unit = p.szUnit ?? string.Empty,
                    source = "Calculated",
                    deviceName = p.szGroupName ?? string.Empty
                }))
            .Concat(dbPts
                .Where(p => IsWaterUnit(p.szUnit))
                .Select(p => new WaterCircuitSidOptionDto
                {
                    sid = p.szSID,
                    name = p.szName,
                    unit = p.szUnit ?? string.Empty,
                    source = "DB",
                    deviceName = dbCoords.FirstOrDefault(c => c.Id == p.nCoordinatorId)?.szName ?? string.Empty
                }));
        return Ok(list);
    }

    [HttpPost("api/tree")]
    public async Task<IActionResult> Create([FromBody] CreateWaterCircuitDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = "名稱不可為空" });

        var nId = await _service.CreateAsync(new WaterCircuitModel
        {
            szName = dto.name,
            nParentId = dto.parentId,
            szSID = string.IsNullOrWhiteSpace(dto.sid) ? null : dto.sid,
            szDescription = dto.description
        });
        return Ok(new { success = true, id = nId });
    }

    [HttpPut("api/tree/{nId}")]
    public async Task<IActionResult> Update(int nId, [FromBody] UpdateWaterCircuitDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = "名稱不可為空" });

        var ok = await _service.UpdateAsync(nId, dto.name,
            string.IsNullOrWhiteSpace(dto.sid) ? null : dto.sid,
            dto.description);
        return ok ? Ok(new { success = true }) : NotFound(new { success = false, message = "節點不存在" });
    }

    [HttpDelete("api/tree/{nId}")]
    public async Task<IActionResult> Delete(int nId, [FromQuery] bool force = false)
    {
        var hasChildren = await _service.HasChildrenAsync(nId);
        if (hasChildren && !force)
            return Conflict(new { success = false, message = "該迴路含子節點，刪除將一併移除所有子孫，請確認後再執行（force=true）" });

        var ok = await _service.DeleteAsync(nId);
        return ok ? Ok(new { success = true }) : NotFound(new { success = false, message = "節點不存在" });
    }

    [HttpPut("api/tree/sort")]
    public async Task<IActionResult> UpdateSort([FromBody] List<WaterCircuitSortDto> dtoList)
    {
        try
        {
            var ok = await _service.UpdateSortOrderAsync(dtoList.Select(d => (d.id, d.parentId, d.sortOrder)));
            return ok ? Ok(new { success = true }) : StatusCode(500, new { success = false, message = "sort_failed" });
        }
        catch (TreeSortException ex)
        {
            // 樹防呆（循環 / 節點不存在 / 父節點不存在）→ 400 + 錯誤代碼
            return BadRequest(new { success = false, message = ex.Code });
        }
    }
}
