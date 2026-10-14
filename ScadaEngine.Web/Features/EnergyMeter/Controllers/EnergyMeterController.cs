using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.EnergyMeter.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.EnergyMeter.Controllers;

[Authorize]
[Route("[controller]")]
public class EnergyMeterController : Controller
{
    private readonly EnergyCircuitService _service;
    private readonly IDataRepository _repository;
    private readonly ILogger<EnergyMeterController> _logger;

    public EnergyMeterController(
        EnergyCircuitService service,
        IDataRepository repository,
        ILogger<EnergyMeterController> logger)
    {
        _service = service;
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("/EnergyMeter")]
    public IActionResult Index()
    {
        ViewData["Title"] = "電表/迴路設定";
        return View();
    }

    [HttpGet("api/tree")]
    public async Task<IActionResult> GetTree()
    {
        var nodes = await _service.GetAllAsync();
        return Ok(nodes.Select(n => new EnergyCircuitNodeViewModel
        {
            id = n.nId,
            name = n.szName,
            parentId = n.nParentId,
            sortOrder = n.nSortOrder,
            sid = n.szSID,
            maxKwh = n.dMaxKwh,
            sign = n.nSign,
            isDemandEnabled = n.isIsDemandEnabled,
            isMainMeter = n.isIsMainMeter,
            voltageSid = n.szVoltageSID,
            currentSid = n.szCurrentSID,
            powerSid = n.szPowerSID,
            powerFactorSid = n.szPowerFactorSID,
            description = n.szDescription
        }));
    }

    /// <summary>組裝全點位清單（Modbus + Calculated + DB，含 coordName/deviceName 分組欄位）— api/sids 與 api/points 共用</summary>
    private async Task<List<CircuitSidOptionDto>> BuildPointOptionsAsync()
    {
        var modbus = await _repository.GetAllModbusPointsAsync();
        var calc = await _repository.GetAllCalculatedPointsAsync();
        var dbPts = await _repository.GetAllDbPointsAsync();
        var coords = (await _repository.GetAllCoordinatorsAsync()).ToList();
        var dbCoords = (await _repository.GetAllDbCoordinatorsAsync()).ToList();

        (string szCoordName, string szSubUnit) ResolveDevice(string szSID)
        {
            // SID 前綴 = coord.Id * 65536 + subModbusId * 256 + pointSeq
            var nHyphen = szSID.IndexOf('-');
            if (nHyphen <= 0) return (string.Empty, string.Empty);
            if (!int.TryParse(szSID[..nHyphen], out var nPrefix)) return (string.Empty, string.Empty);

            var nCoordId = nPrefix / 65536;
            var nSubModbusId = (nPrefix % 65536) / 256;
            var coord = coords.FirstOrDefault(c => c.Id == nCoordId);
            if (coord == null) return (string.Empty, string.Empty);

            var szName = coord.szName ?? string.Empty;
            var ids = (coord.szModbusID ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var names = (coord.szDeviceName ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // 單 ID 協調器：無子單元層
            if (ids.Length <= 1) return (szName, string.Empty);

            // 多 ID 協調器：找與 subModbusId 相符的子設備當子單元；DeviceName 未填時用「ID {n}」區分
            for (int i = 0; i < ids.Length; i++)
            {
                if (int.TryParse(ids[i], out var nMid) && nMid == nSubModbusId)
                    return (szName, (i < names.Length && !string.IsNullOrWhiteSpace(names[i]))
                        ? names[i] : $"ID {nSubModbusId}");
            }
            return (szName, $"ID {nSubModbusId}");
        }

        return modbus
            .Select(p =>
            {
                var (szCoordName, szSubUnit) = ResolveDevice(p.szSID);
                return new CircuitSidOptionDto
                {
                    sid = p.szSID,
                    name = p.szName,
                    unit = p.szUnit ?? string.Empty,
                    source = "Modbus",
                    coordName = szCoordName,
                    deviceName = szSubUnit
                };
            })
            .Concat(calc
                .Select(p => new CircuitSidOptionDto
                {
                    sid = p.szSID,
                    name = p.szName,
                    unit = p.szUnit ?? string.Empty,
                    source = "Calculated",
                    coordName = p.szGroupName ?? string.Empty,
                    deviceName = string.Empty
                }))
            .Concat(dbPts
                .Select(p => new CircuitSidOptionDto
                {
                    sid = p.szSID,
                    name = p.szName,
                    unit = p.szUnit ?? string.Empty,
                    source = "DB",
                    coordName = dbCoords.FirstOrDefault(c => c.Id == p.nCoordinatorId)?.szName ?? string.Empty,
                    deviceName = string.Empty
                }))
            .ToList();
    }

    [HttpGet("api/sids")]
    public async Task<IActionResult> GetSidOptions()
    {
        var list = await BuildPointOptionsAsync();
        return Ok(list.Where(o => string.Equals(o.unit, "kWh", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>全部點位（不限單位）— 電表資訊自動比對與兩步驟選點器用</summary>
    [HttpGet("api/points")]
    public async Task<IActionResult> GetAllPoints()
    {
        return Ok(await BuildPointOptionsAsync());
    }

    [HttpPost("api/tree")]
    public async Task<IActionResult> Create([FromBody] CreateCircuitDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = "名稱不可為空" });
        if (dto.sign != 1 && dto.sign != -1)
            return BadRequest(new { success = false, message = "貢獻方向只能為 +1 或 -1" });

        // 主要電表：實體 / 虛擬皆可（虛擬主表由 EMS 頁自動聚合子孫葉子的 V/I/P/PF）
        var isMainMeter = dto.isMainMeter;
        // 電表資訊 4 SIDs：實體電表可獨立設定（無論是否為主要電表）；虛擬迴路強制 NULL
        var isPhysical = !string.IsNullOrWhiteSpace(dto.sid);
        var nId = await _service.CreateAsync(new EnergyCircuitModel
        {
            szName = dto.name,
            nParentId = dto.parentId,
            szSID = isPhysical ? dto.sid : null,
            dMaxKwh = dto.maxKwh,
            nSign = dto.sign,
            isIsDemandEnabled = dto.isDemandEnabled,
            isIsMainMeter = isMainMeter,
            szVoltageSID = isPhysical && !string.IsNullOrWhiteSpace(dto.voltageSid) ? dto.voltageSid : null,
            szCurrentSID = isPhysical && !string.IsNullOrWhiteSpace(dto.currentSid) ? dto.currentSid : null,
            szPowerSID = isPhysical && !string.IsNullOrWhiteSpace(dto.powerSid) ? dto.powerSid : null,
            szPowerFactorSID = isPhysical && !string.IsNullOrWhiteSpace(dto.powerFactorSid) ? dto.powerFactorSid : null,
            szDescription = dto.description
        });
        return Ok(new { success = true, id = nId });
    }

    [HttpPut("api/tree/{nId}")]
    public async Task<IActionResult> Update(int nId, [FromBody] UpdateCircuitDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = "名稱不可為空" });
        if (dto.sign != 1 && dto.sign != -1)
            return BadRequest(new { success = false, message = "貢獻方向只能為 +1 或 -1" });

        // 主要電表：實體 / 虛擬皆可（虛擬主表由 EMS 頁自動聚合子孫葉子的 V/I/P/PF）
        var isMainMeter = dto.isMainMeter;
        // 電表資訊 4 SIDs：實體電表可獨立設定（無論是否為主要電表）；虛擬迴路強制 NULL
        var isPhysical = !string.IsNullOrWhiteSpace(dto.sid);
        var ok = await _service.UpdateAsync(nId, dto.name,
            isPhysical ? dto.sid : null,
            dto.maxKwh, dto.sign,
            dto.isDemandEnabled,
            isMainMeter,
            isPhysical && !string.IsNullOrWhiteSpace(dto.voltageSid) ? dto.voltageSid : null,
            isPhysical && !string.IsNullOrWhiteSpace(dto.currentSid) ? dto.currentSid : null,
            isPhysical && !string.IsNullOrWhiteSpace(dto.powerSid) ? dto.powerSid : null,
            isPhysical && !string.IsNullOrWhiteSpace(dto.powerFactorSid) ? dto.powerFactorSid : null,
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
    public async Task<IActionResult> UpdateSort([FromBody] List<CircuitSortDto> dtoList)
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
