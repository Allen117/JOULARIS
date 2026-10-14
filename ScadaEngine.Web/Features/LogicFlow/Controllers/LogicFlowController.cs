using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ScadaEngine.Engine.Services;
using ScadaEngine.Web.Features.LogicFlow.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.LogicFlow.Controllers;

[Authorize(Roles = "Engineer")]
[Route("[controller]")]
public class LogicFlowController : Controller
{
    private readonly LogicFlowService _service;
    private readonly MqttRealtimeSubscriberService _mqttService;
    private readonly CSharpAlgorithmService _csharpAlgoService;
    private readonly ILogger<LogicFlowController> _logger;
    private readonly IStringLocalizer<LogicFlowController> _l;

    public LogicFlowController(
        LogicFlowService service,
        MqttRealtimeSubscriberService mqttService,
        CSharpAlgorithmService csharpAlgoService,
        ILogger<LogicFlowController> logger,
        IStringLocalizer<LogicFlowController> localizer)
    {
        _service = service;
        _mqttService = mqttService;
        _csharpAlgoService = csharpAlgoService;
        _logger = logger;
        _l = localizer;
    }

    [HttpGet("/LogicFlow")]
    public IActionResult Index()
    {
        return View();
    }

    // ============ Tree API ============

    [HttpGet("api/tree")]
    public async Task<IActionResult> GetTree()
    {
        var nodes = await _service.GetAllNodesAsync();
        return Ok(nodes);
    }

    [HttpPost("api/tree")]
    public async Task<IActionResult> CreateNode([FromBody] CreateNodeDto dto)
    {
        var nId = await _service.CreateNodeAsync(dto.ParentId, dto.Name, dto.NodeType, dto.SortOrder);
        return Ok(new { id = nId });
    }

    [HttpPut("api/tree/{nId}/rename")]
    public async Task<IActionResult> RenameNode(int nId, [FromBody] RenameNodeDto dto)
    {
        var ok = await _service.RenameNodeAsync(nId, dto.Name);
        return ok ? Ok(new { success = true }) : NotFound();
    }

    [HttpDelete("api/tree/{nId}")]
    public async Task<IActionResult> DeleteNode(int nId)
    {
        var ok = await _service.DeleteNodeAsync(nId);
        return ok ? Ok(new { success = true }) : NotFound();
    }

    /// <summary>複製節點到指定父層（資料夾遞迴含所有子項；複製出的邏輯一律停用且點位綁定全清）</summary>
    [HttpPost("api/tree/{nId}/copy")]
    public async Task<IActionResult> CopyNode(int nId, [FromBody] CopyNodeDto dto)
    {
        var nNewId = await _service.CopyNodeAsync(nId, dto.TargetParentId, _l["logicflow.copy.suffix"].Value);
        if (nNewId == null)
            return NotFound(new { success = false, message = _l["logicflow.api.copy_failed"].Value });
        return Ok(new { id = nNewId.Value });
    }

    [HttpPut("api/tree/{nId}/toggle")]
    public async Task<IActionResult> ToggleEnabled(int nId, [FromBody] ToggleEnabledDto dto)
    {
        var ok = await _service.ToggleEnabledAsync(nId, dto.IsEnabled);
        return ok ? Ok(new { success = true }) : NotFound();
    }

    /// <summary>拖曳搬移節點：改父層 + 插入位置；防呆失敗回 400 + 錯誤代碼（前端 `logicflow.err.{code}`）</summary>
    [HttpPut("api/tree/{nId}/move")]
    public async Task<IActionResult> MoveNode(int nId, [FromBody] MoveNodeDto dto)
    {
        try
        {
            var nDisabled = await _service.MoveNodeAsync(nId, dto.TargetParentId, dto.SortOrder);
            return Ok(new { success = true, disabledCount = nDisabled });
        }
        catch (TreeSortException ex)
        {
            return BadRequest(new { success = false, message = ex.Code });
        }
        catch (Exception)
        {
            return StatusCode(500, new { success = false, message = "move_failed" });
        }
    }

    [HttpPut("api/tree/sort")]
    public async Task<IActionResult> UpdateSortOrder([FromBody] List<SortOrderDto> dtoList)
    {
        var sortList = dtoList.Select(d => (d.Id, d.SortOrder));
        var ok = await _service.UpdateSortOrderAsync(sortList);
        return ok ? Ok(new { success = true }) : StatusCode(500);
    }

    // ============ Diagram API ============

    [HttpGet("api/diagram/{nTreeId}")]
    public async Task<IActionResult> GetDiagram(int nTreeId)
    {
        var diagram = await _service.GetDiagramAsync(nTreeId);
        if (diagram == null) return NotFound();
        return Ok(diagram);
    }

    [HttpPut("api/diagram/{nTreeId}")]
    public async Task<IActionResult> SaveDiagram(int nTreeId, [FromBody] SaveDiagramDto dto)
    {
        var ok = await _service.SaveDiagramAsync(nTreeId, dto.DiagramJson, dto.Version);
        if (!ok) return Conflict(new { success = false, message = _l["logicflow.api.version_conflict"].Value });
        return Ok(new { success = true });
    }

    // ============ History Value API (前端預覽用) ============

    /// <summary>取得某點位「N 分鐘前」的歷史值（同 Engine 規則：target−5min 窗、Quality=1）；
    /// 前端依 (sid, offset, 分鐘) 快取查詢結果，不會每輪 eval 都打此 API</summary>
    [HttpGet("api/history-value")]
    public async Task<IActionResult> GetHistoryValueAt([FromQuery] string sid, [FromQuery] int offsetMinutes)
    {
        if (string.IsNullOrWhiteSpace(sid) || offsetMinutes < 1 || offsetMinutes > 43200)
            return BadRequest(new { error = "invalid sid or offsetMinutes (1-43200)" });

        var (isFound, dValue, dtTimestamp) = await _service.GetHistoryValueAtAsync(sid, offsetMinutes);
        return Ok(new
        {
            found = isFound,
            value = isFound ? dValue : (double?)null,
            timestamp = isFound ? dtTimestamp.ToString("yyyy-MM-dd HH:mm") : null
        });
    }

    // ============ Timer State API ============

    /// <summary>取得指定邏輯的 TP 計時器狀態（由 Engine 透過 MQTT 同步）</summary>
    [HttpGet("api/timer-state/{nTreeId}")]
    public IActionResult GetTimerState(int nTreeId)
    {
        var states = _mqttService.GetTimerStates(nTreeId);
        return Ok(states);
    }

    // ============ Algorithm Eval API (前端預覽用，代理呼叫 Python FastAPI) ============

    private static readonly HttpClient _algoHttpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:8100"),
        Timeout = TimeSpan.FromSeconds(3)
    };

    [HttpPost("api/algo-eval/{szAlgoName}")]
    public async Task<IActionResult> EvalAlgorithm(string szAlgoName, [FromBody] AlgoEvalRequest req)
    {
        var inputs = req.Inputs ?? new Dictionary<string, double>();

        // ★ 優先嘗試 C# 演算法（同步 in-process，零延遲）
        if (_csharpAlgoService.TryEvaluate(szAlgoName, inputs, out var csResult, out var csStatus, out var csPerOutput))
        {
            var quality = csStatus.Severity == "Error" ? "Bad" : "Good";
            // 把 perOutput 攤平成 JSON-friendly map
            var perOutput = csPerOutput.ToDictionary(
                kv => kv.Key,
                kv => (object)new { statusCodeId = kv.Value.CodeId, statusCodeName = kv.Value.CodeName, severity = kv.Value.Severity });
            return Ok(new
            {
                result = csResult,
                statusCodeId = csStatus.CodeId,
                statusCodeName = csStatus.CodeName,
                severity = csStatus.Severity,
                quality,
                perOutput
            });
        }

        // ★ 退回 Python FastAPI（variadic 演算法須帶上 n）
        try
        {
            var payloadObj = req.N.HasValue
                ? (object)new { inputs, n = req.N.Value }
                : (object)new { inputs };
            var payload = System.Text.Json.JsonSerializer.Serialize(payloadObj);
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await _algoHttpClient.PostAsync($"/algorithms/{szAlgoName}/evaluate", content);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return Content(json, "application/json");
            }
            return StatusCode((int)response.StatusCode);
        }
        catch
        {
            return StatusCode(503, new { error = "Algorithm service unavailable" });
        }
    }

    // ============ Algorithm API ============

    /// <summary>掃描 Engine/Algorithms/ 資料夾及子資料夾，回傳可用的演算法清單（.py + .cs）</summary>
    [HttpGet("api/algorithms")]
    public IActionResult GetAlgorithms()
    {
        var szAlgoDir = ResolveAlgorithmsDir();
        if (szAlgoDir == null)
            return Ok(Array.Empty<object>());

        var algorithms = new List<object>();

        // 掃描 .py（排除 main.py 與所有底線開頭的工具檔，如 _status.py / __pycache__）
        foreach (var szFilePath in Directory.GetFiles(szAlgoDir, "*.py", SearchOption.AllDirectories))
        {
            var szFileName = Path.GetFileName(szFilePath);
            if (szFileName == "main.py" || szFileName.StartsWith("_")) continue;
            algorithms.Add(ParseAlgorithmMetadata(szFilePath, szAlgoDir, "#", "python", _logger));
        }

        // 掃描 .cs（排除所有底線開頭的工具檔，如 _AlgorithmStatus.cs）
        foreach (var szFilePath in Directory.GetFiles(szAlgoDir, "*.cs", SearchOption.AllDirectories))
        {
            var szFileName = Path.GetFileName(szFilePath);
            if (szFileName.StartsWith("_")) continue;
            algorithms.Add(ParseAlgorithmMetadata(szFilePath, szAlgoDir, "//", "csharp", _logger));
        }

        return Ok(algorithms);
    }

    /// <summary>解析 'key:label, key2:label2' 格式為 [{key, label}]，無冒號時 label 等於 key</summary>
    private static List<object> ParseKeyLabelList(string raw)
    {
        var list = new List<object>();
        foreach (var seg in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = seg.IndexOf(':');
            if (idx >= 0)
            {
                var k = seg[..idx].Trim();
                var lbl = seg[(idx + 1)..].Trim();
                list.Add(new { key = k, label = string.IsNullOrEmpty(lbl) ? k : lbl });
            }
            else
            {
                list.Add(new { key = seg, label = seg });
            }
        }
        return list;
    }

    /// <summary>解析 'key:port:field, ...' 為 [{key, port, field}]（@inputs_auto_repeat 用）；格式不足三段的項目略過</summary>
    private static List<object> ParseAutoRepeatList(string raw)
    {
        var list = new List<object>();
        foreach (var seg in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = seg.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || parts.Take(3).Any(string.IsNullOrEmpty)) continue;
            list.Add(new { key = parts[0], port = parts[1], field = parts[2] });
        }
        return list;
    }

    /// <summary>解析 'key=1.5, key2=0.3' 為 {key: 1.5}（@inputs_default 用）；非數值項目略過</summary>
    private static Dictionary<string, double> ParseDefaultsMap(string raw)
    {
        var map = new Dictionary<string, double>();
        foreach (var seg in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = seg.IndexOf('=');
            if (idx <= 0) continue;
            var k = seg[..idx].Trim();
            if (double.TryParse(seg[(idx + 1)..].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                map[k] = v;
        }
        return map;
    }

    /// <summary>通用 metadata 解析：支援 # (Python) 和 // (C#) 前綴。第一版 .cs 不支援 variadic（忽略並 log warning）。</summary>
    private static object ParseAlgorithmMetadata(string szFilePath, string szAlgoDir, string szPrefix, string szLanguage, ILogger logger)
    {
        var szName = Path.GetFileNameWithoutExtension(szFilePath);
        var szLabel = szName;
        var inputs = new List<object> { new { key = "in", label = "in" } };
        var outputs = new List<object> { new { key = "out", label = "out" } };
        var szDescription = "";
        var isVariadic = false;
        var inputsRepeat = new List<object>();
        var inputsFixed = new List<object>();
        var outputsRepeat = new List<object>();
        var outputsFixed = new List<object>();
        var inputsAutoRepeat = new List<object>();
        var inputDefaults = new Dictionary<string, double>();

        var szRelDir = Path.GetDirectoryName(szFilePath)!;
        var szGroup = szRelDir == szAlgoDir ? "" : Path.GetFileName(szRelDir);

        var lines = System.IO.File.ReadLines(szFilePath).Take(15);
        var szAlgo = $"{szPrefix} @algorithm:";
        var szIn = $"{szPrefix} @inputs:";
        var szOut = $"{szPrefix} @outputs:";
        var szDesc = $"{szPrefix} @description:";
        var szVariadic = $"{szPrefix} @variadic:";
        var szInRepeat = $"{szPrefix} @inputs_repeat:";
        var szInFixed = $"{szPrefix} @inputs_fixed:";
        var szOutRepeat = $"{szPrefix} @outputs_repeat:";
        var szOutFixed = $"{szPrefix} @outputs_fixed:";
        var szInAutoRepeat = $"{szPrefix} @inputs_auto_repeat:";
        var szInDefault = $"{szPrefix} @inputs_default:";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(szAlgo))
                szLabel = trimmed[szAlgo.Length..].Trim();
            else if (trimmed.StartsWith(szInAutoRepeat))
                inputsAutoRepeat = ParseAutoRepeatList(trimmed[szInAutoRepeat.Length..]);
            else if (trimmed.StartsWith(szInDefault))
                inputDefaults = ParseDefaultsMap(trimmed[szInDefault.Length..]);
            else if (trimmed.StartsWith(szInRepeat))
                inputsRepeat = ParseKeyLabelList(trimmed[szInRepeat.Length..]);
            else if (trimmed.StartsWith(szInFixed))
                inputsFixed = ParseKeyLabelList(trimmed[szInFixed.Length..]);
            else if (trimmed.StartsWith(szOutRepeat))
                outputsRepeat = ParseKeyLabelList(trimmed[szOutRepeat.Length..]);
            else if (trimmed.StartsWith(szOutFixed))
                outputsFixed = ParseKeyLabelList(trimmed[szOutFixed.Length..]);
            else if (trimmed.StartsWith(szIn))
                inputs = ParseKeyLabelList(trimmed[szIn.Length..]);
            else if (trimmed.StartsWith(szOut))
                outputs = ParseKeyLabelList(trimmed[szOut.Length..]);
            else if (trimmed.StartsWith(szDesc))
                szDescription = trimmed[szDesc.Length..].Trim();
            else if (trimmed.StartsWith(szVariadic))
            {
                var v = trimmed[szVariadic.Length..].Trim().ToLowerInvariant();
                isVariadic = v is "true" or "1" or "yes";
            }
        }

        // .cs 演算法第一版不支援 variadic：忽略標記並警告
        if (szLanguage == "csharp" && isVariadic)
        {
            logger.LogWarning(
                "C# 演算法 {Name} 標記了 @variadic: true，但第一版尚未支援 .cs variadic — 已忽略該標記，視為一般演算法",
                szName);
            isVariadic = false;
            inputsRepeat = new List<object>();
            inputsFixed = new List<object>();
            outputsRepeat = new List<object>();
            outputsFixed = new List<object>();
            inputsAutoRepeat = new List<object>();
        }

        return new
        {
            name = szName,
            label = szLabel,
            group = szGroup,
            inputs,
            outputs,
            description = szDescription,
            language = szLanguage,
            variadic = isVariadic,
            inputsRepeat,
            inputsFixed,
            outputsRepeat,
            outputsFixed,
            inputsAutoRepeat,
            inputDefaults,
        };
    }

    /// <summary>依序探測多個候選路徑，回傳第一個存在的 Algorithms 資料夾（開發 + 部署皆適用）</summary>
    private static string? ResolveAlgorithmsDir()
    {
        // ⚠️ 部署候選一律以 BaseDirectory（執行檔所在）為錨點，不可用 CurrentDirectory 推導：
        // Web 以 Windows 服務執行時 sc.exe 不設工作目錄，CWD = C:\Windows\System32，
        // 「CWD\..\..\Engine\App\Algorithms」會展開成 C:\Engine\App\Algorithms —— 一個與本站台無關的路徑，
        // 只要機器上剛好有該殘留資料夾就會被選中，演算法清單靜默停在舊內容（2026-09-14 正式站實際踩過）。
        var szBaseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            // 開發環境：Web 與 Engine 平行放在 solution 根目錄下（改 .py 免重 build 即生效，故排第一）
            Path.Combine(Directory.GetCurrentDirectory(), "..", "ScadaEngine.Engine", "Algorithms"),
            // 部署環境（同層）：Algorithms 直接放在 Web\App\Algorithms
            Path.Combine(szBaseDir, "Algorithms"),
            // 部署環境（分棟）：Web 在 C:\JOULARIS\Web\App，Engine 在 C:\JOULARIS\Engine\App
            Path.Combine(szBaseDir, "..", "..", "Engine", "App", "Algorithms"),
        };

        foreach (var szPath in candidates)
        {
            var szFull = Path.GetFullPath(szPath);
            if (Directory.Exists(szFull))
                return szFull;
        }
        return null;
    }
}
