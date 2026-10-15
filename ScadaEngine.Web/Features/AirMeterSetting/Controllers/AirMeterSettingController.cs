using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.AirMeterSetting.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.AirMeterSetting.Controllers;

[Authorize]
[Route("[controller]")]
public class AirMeterSettingController : Controller
{
    private readonly AirMeterCircuitService _service;
    private readonly IDataRepository _repository;
    private readonly ILogger<AirMeterSettingController> _logger;

    public AirMeterSettingController(
        AirMeterCircuitService service,
        IDataRepository repository,
        ILogger<AirMeterSettingController> logger)
    {
        _service = service;
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("/AirMeterSetting")]
    public IActionResult Index()
    {
        // 頁面標題由 View 端 @Localizer 依語系設定
        return View();
    }

    [HttpGet("api/tree")]
    public async Task<IActionResult> GetTree()
    {
        var nodes = await _service.GetAllAsync();
        return Ok(nodes.Select(n => new AirMeterCircuitNodeViewModel
        {
            id = n.nId,
            name = n.szName,
            parentId = n.nParentId,
            sortOrder = n.nSortOrder,
            sid = n.szSID,
            unitScale = n.dUnitScale,
            maxVolume = n.dMaxVolume,
            sign = n.nSign,
            description = n.szDescription
        }));
    }

    /// <summary>組裝全點位清單（Modbus + Calculated + DB，含 coordName/deviceName 分組欄位）</summary>
    private async Task<List<AirMeterSidOptionDto>> BuildPointOptionsAsync()
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
                return new AirMeterSidOptionDto
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
                .Select(p => new AirMeterSidOptionDto
                {
                    sid = p.szSID,
                    name = p.szName,
                    unit = p.szUnit ?? string.Empty,
                    source = "Calculated",
                    coordName = p.szGroupName ?? string.Empty,
                    deviceName = string.Empty
                }))
            .Concat(dbPts
                .Select(p => new AirMeterSidOptionDto
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

    /// <summary>
    /// 由 SID 反查該點位的名稱 + 單位並推導 UnitScale — 存檔時一律由伺服器定案，不採信前端傳值。
    /// 前端算錯或被竄改會讓該產氣量表所有歷史產氣量差 1000 倍且無從察覺。
    /// 與下拉過濾共用 <see cref="AirMeterCircuitService.ResolveAirPointScale"/>，
    /// 因此「不在下拉裡的點位」硬 POST 也會被擋（非產氣量點位 → null）。
    /// 查無該點位（點位已刪）→ 回 null 由呼叫端拒絕存檔。
    /// </summary>
    private async Task<double?> ResolveUnitScaleForSidAsync(string szSid)
    {
        var options = await BuildPointOptionsAsync();
        var point = options.FirstOrDefault(o => string.Equals(o.sid, szSid, StringComparison.OrdinalIgnoreCase));
        return point == null ? null : AirMeterCircuitService.ResolveAirPointScale(point.name, point.unit);
    }

    /// <summary>
    /// 產氣量點位清單 — **單位 + 點位名稱雙條件**過濾，每筆帶 unitScale：
    /// 單位可換算為 Nm³（Nm³/m³ 系 / L 系）**且** 名稱含空壓關鍵字 **且** 名稱不含天然氣字樣。
    /// Nm³ / m³ 對天然氣表、水表、壓縮空氣都成立，單位無從分辨 → 名稱是唯一可靠訊號。
    /// 詳見 docs/功能說明書_產氣量表.md。
    /// </summary>
    [HttpGet("api/sids")]
    public async Task<IActionResult> GetSidOptions()
    {
        var list = await BuildPointOptionsAsync();
        var result = new List<AirMeterSidOptionDto>();
        foreach (var o in list)
        {
            var dScale = AirMeterCircuitService.ResolveAirPointScale(o.name, o.unit);
            if (dScale == null) continue;
            o.unitScale = dScale.Value;
            result.Add(o);
        }
        return Ok(result);
    }

    [HttpPost("api/tree")]
    public async Task<IActionResult> Create([FromBody] CreateAirMeterCircuitDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = "name_required" });
        if (dto.sign != 1 && dto.sign != -1)
            return BadRequest(new { success = false, message = "invalid_sign" });

        // 實體產氣量表 = 有綁 SID；虛擬迴路的 UnitScale 固定 1、MaxVolume 強制 NULL
        var isPhysical = !string.IsNullOrWhiteSpace(dto.sid);
        double dUnitScale = 1.0;
        if (isPhysical)
        {
            var dResolved = await ResolveUnitScaleForSidAsync(dto.sid!);
            if (dResolved == null)
                return BadRequest(new { success = false, message = "invalid_air_point" });
            dUnitScale = dResolved.Value;
        }

        var nId = await _service.CreateAsync(new AirMeterCircuitModel
        {
            szName = dto.name,
            nParentId = dto.parentId,
            szSID = isPhysical ? dto.sid : null,
            dUnitScale = dUnitScale,
            dMaxVolume = isPhysical ? dto.maxVolume : null,
            nSign = dto.sign,
            szDescription = dto.description
        });
        return Ok(new { success = true, id = nId });
    }

    [HttpPut("api/tree/{nId}")]
    public async Task<IActionResult> Update(int nId, [FromBody] UpdateAirMeterCircuitDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = "name_required" });
        if (dto.sign != 1 && dto.sign != -1)
            return BadRequest(new { success = false, message = "invalid_sign" });

        // 實體產氣量表 = 有綁 SID；虛擬迴路的 UnitScale 固定 1、MaxVolume 強制 NULL
        var isPhysical = !string.IsNullOrWhiteSpace(dto.sid);
        double dUnitScale = 1.0;
        if (isPhysical)
        {
            var dResolved = await ResolveUnitScaleForSidAsync(dto.sid!);
            if (dResolved == null)
                return BadRequest(new { success = false, message = "invalid_air_point" });
            dUnitScale = dResolved.Value;
        }

        var ok = await _service.UpdateAsync(new AirMeterCircuitModel
        {
            nId = nId,
            szName = dto.name,
            szSID = isPhysical ? dto.sid : null,
            dUnitScale = dUnitScale,
            dMaxVolume = isPhysical ? dto.maxVolume : null,
            nSign = dto.sign,
            szDescription = dto.description
        });
        return ok ? Ok(new { success = true }) : NotFound(new { success = false, message = "node_not_found" });
    }

    [HttpDelete("api/tree/{nId}")]
    public async Task<IActionResult> Delete(int nId, [FromQuery] bool force = false)
    {
        var hasChildren = await _service.HasChildrenAsync(nId);
        if (hasChildren && !force)
            return Conflict(new { success = false, message = "has_children" });

        var ok = await _service.DeleteAsync(nId);
        return ok ? Ok(new { success = true }) : NotFound(new { success = false, message = "node_not_found" });
    }

    [HttpPut("api/tree/sort")]
    public async Task<IActionResult> UpdateSort([FromBody] List<AirMeterCircuitSortDto> dtoList)
    {
        try
        {
            await _service.UpdateSortOrderAsync(dtoList.Select(d => (d.id, d.parentId, d.sortOrder)));
            return Ok(new { success = true });
        }
        catch (TreeSortException ex)
        {
            // 樹防呆（循環 / 節點不存在 / 父節點不存在 / 多根）→ 400 + 錯誤代碼
            return BadRequest(new { success = false, message = ex.Code });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 AirMeterCircuit 排序失敗");
            return StatusCode(500, new { success = false, message = "sort_failed" });
        }
    }
}
