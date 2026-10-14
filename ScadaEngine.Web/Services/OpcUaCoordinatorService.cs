using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Opc.Ua.Client;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Services;
using ScadaEngine.Web.Features.OpcUaCoordinator.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Web.Services;

/// <summary>
/// OPC UA 來源設定服務 — 讀 DB 供顯示；寫入時回寫 OpcUaPoint/*.json（source of truth）
/// 並同步 UPSERT DB，之後由 Controller 發 MQTT reload 通知 Engine 熱重載。
/// JSON 為設定源、DB 為執行期快照（比照 DBPoint 線，見 docs/plans 決策 2）。
///
/// 寫檔路徑由 appsettings.json 的 EngineOpcUaConfig 明定（比照 EngineModbusConfig）：
/// WatchedFolder = Engine 實際讀取的部署資料夾；MirrorFolder（可選，dev 用）鏡像寫回原始碼資料夾。
/// 原子寫檔（*.json.tmp → File.Replace → 留 *.json.bak）。
/// </summary>
public class OpcUaCoordinatorService
{
    private readonly IDataRepository _repository;
    private readonly ILogger<OpcUaCoordinatorService> _logger;
    private readonly IStringLocalizer<OpcUaCoordinatorService> _l;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;

    /// <summary>JSON 檔寫入互斥（static — Scoped service 跨請求共用）</summary>
    private static readonly SemaphoreSlim _fileGate = new(1, 1);

    /// <summary>名稱 = JSON 檔名 = MQTT 主題段，限制安全字元</summary>
    private static readonly Regex _nameRegex = new("^[A-Za-z0-9_-]{1,50}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions _jsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions _jsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public OpcUaCoordinatorService(
        IDataRepository repository,
        ILogger<OpcUaCoordinatorService> logger,
        IStringLocalizer<OpcUaCoordinatorService> localizer,
        IConfiguration configuration,
        IWebHostEnvironment env)
    {
        _repository = repository;
        _logger = logger;
        _l = localizer;
        _configuration = configuration;
        _env = env;
    }

    /// <summary>
    /// 每次呼叫即時解析監控資料夾（Engine 實際讀取的 OpcUaPoint 位置）— 不在建構子快取，
    /// appsettings.json 熱重載改路徑即生效。相對路徑以 Web ContentRoot 為基準；未設定回傳 null（呼叫端明確報錯）。
    /// 比照 ModbusConfigFileService 的 EngineModbusConfig 慣例，禁止猜測式 fallback。
    /// </summary>
    private string? GetWatchedFolder()
    {
        var szWatched = _configuration["EngineOpcUaConfig:WatchedFolder"];
        return string.IsNullOrWhiteSpace(szWatched)
            ? null
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, szWatched));
    }

    /// <summary>每次呼叫即時解析鏡像資料夾（可選，dev 用 — 同步寫回原始碼資料夾避免 rebuild 後設定倒退）</summary>
    private string? GetMirrorFolder()
    {
        var szMirror = _configuration["EngineOpcUaConfig:MirrorFolder"];
        return string.IsNullOrWhiteSpace(szMirror)
            ? null
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, szMirror));
    }

    /// <summary>
    /// 取得所有 Server + Device 分組點位（供 Index 頁顯示；不含明文密碼）
    /// </summary>
    public async Task<List<OpcUaServerListItemDto>> GetAllAsync()
    {
        var coordinators = (await _repository.GetAllOpcUaCoordinatorsAsync()).ToList();
        var allPoints = (await _repository.GetAllOpcUaPointsAsync()).ToList();

        var pointsByCoord = allPoints.GroupBy(p => p.nCoordinatorId)
                                     .ToDictionary(g => g.Key, g => g.OrderBy(p => p.nSequence).ToList());

        var result = new List<OpcUaServerListItemDto>();
        foreach (var c in coordinators)
        {
            pointsByCoord.TryGetValue(c.Id, out var points);
            points ??= new List<OpcUaPointModel>();

            // 依 DeviceName 分組（保持點位 Sequence 順序，空 DeviceName 歸入 ''）
            var devices = new List<OpcUaDeviceListItemDto>();
            var deviceMap = new Dictionary<string, OpcUaDeviceListItemDto>(StringComparer.Ordinal);
            foreach (var p in points)
            {
                var szDevice = p.szDeviceName ?? string.Empty;
                if (!deviceMap.TryGetValue(szDevice, out var device))
                {
                    device = new OpcUaDeviceListItemDto { name = szDevice };
                    deviceMap[szDevice] = device;
                    devices.Add(device);
                }
                device.tags.Add(new OpcUaPointListItemDto
                {
                    sid = p.szSID,
                    seq = p.nSequence,
                    name = p.szName,
                    tagName = p.szTagName,
                    controlType = p.szControlType ?? string.Empty,
                    ratio = p.fRatio,
                    unit = p.szUnit ?? string.Empty,
                    min = p.fMin,
                    max = p.fMax
                });
            }

            result.Add(new OpcUaServerListItemDto
            {
                id = c.Id,
                name = c.szName,
                endpointUrl = c.szEndpointUrl,
                username = c.szUsername,
                hasPassword = !string.IsNullOrEmpty(c.szPassword),
                pollingInterval = c.nPollingInterval,
                connectTimeout = c.nConnectTimeout,
                monitorEnabled = c.isMonitorEnabled,
                devices = devices
            });
        }
        return result;
    }

    /// <summary>
    /// 新增/編輯 Server 連線設定（不動點位）。回寫 JSON + UPSERT DB。
    /// </summary>
    public async Task<(bool isSuccess, string szMessage, int nId)> SaveServerAsync(SaveOpcUaServerRequest request)
    {
        var szName = (request.Name ?? string.Empty).Trim();
        var szEndpoint = (request.EndpointUrl ?? string.Empty).Trim();

        if (!szEndpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
            return (false, _l["opcuacoord.svc.endpoint_invalid"].Value, 0);
        if (request.PollingInterval < 200)
            return (false, _l["opcuacoord.svc.interval_invalid"].Value, 0);
        if (request.ConnectTimeout < 1000)
            return (false, _l["opcuacoord.svc.timeout_invalid"].Value, 0);

        var coordinators = (await _repository.GetAllOpcUaCoordinatorsAsync()).ToList();

        OpcUaCoordinatorModel? existing = null;
        if (request.Id > 0)
        {
            existing = coordinators.FirstOrDefault(c => c.Id == request.Id);
            if (existing == null)
                return (false, _l["opcuacoord.svc.not_found"].Value, 0);
            // 名稱 = 檔名 = MQTT 主題段 = UPSERT key，不允許改名（改名會產生新 Id、SID 漂移）
            szName = existing.szName;
        }
        else
        {
            if (!_nameRegex.IsMatch(szName))
                return (false, _l["opcuacoord.svc.name_invalid"].Value, 0);
            if (coordinators.Any(c => string.Equals(c.szName, szName, StringComparison.OrdinalIgnoreCase)))
                return (false, _l["opcuacoord.svc.name_duplicate"].Value, 0);
        }

        // 密碼空字串：編輯 = 保留原密碼；新增 = 無密碼
        var szPassword = request.Password ?? string.Empty;
        if (string.IsNullOrEmpty(szPassword) && existing != null)
            szPassword = existing.szPassword;

        await _fileGate.WaitAsync();
        try
        {
            var szFolder = GetWatchedFolder();
            if (szFolder == null)
                return (false, _l["opcuacoord.svc.folder_not_configured"].Value, 0);
            Directory.CreateDirectory(szFolder);
            var szPath = Path.Combine(szFolder, szName + ".json");

            // 讀舊檔保留 Devices / NextSeq；不存在則建新
            var file = await ReadJsonFileAsync(szPath) ?? new OpcUaJsonFile();
            file.Name = szName;
            file.EndpointUrl = szEndpoint;
            file.Username = (request.Username ?? string.Empty).Trim();
            file.Password = szPassword;
            file.PollingInterval = request.PollingInterval;
            file.ConnectTimeout = request.ConnectTimeout;
            file.MonitorEnabled = request.MonitorEnabled;
            file.WebEditedAt = SourceJsonStamp.Now();   // Excel 匯入預覽據此判斷「拿舊 Excel 會蓋掉網頁修改」

            await WriteJsonFileAsync(szPath, file);

            // 同步 UPSERT DB（Engine reload 也會再 UPSERT 一次，冪等）
            var nId = await _repository.SaveOpcUaCoordinatorAsync(new OpcUaCoordinatorModel
            {
                szName = szName,
                szEndpointUrl = szEndpoint,
                szUsername = file.Username,
                szPassword = szPassword,
                nPollingInterval = request.PollingInterval,
                nConnectTimeout = request.ConnectTimeout,
                isMonitorEnabled = request.MonitorEnabled
            });

            _logger.LogInformation("OPC UA Server 已儲存: {Name} (Id={Id}) → {Path}", szName, nId, szPath);
            return (true, _l["opcuacoord.svc.save_success"].Value, nId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存 OPC UA Server 失敗: {Name}", szName);
            return (false, _l["opcuacoord.svc.json_write_failed", ex.Message].Value, 0);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>
    /// 刪除 Server：刪 JSON 檔 + 刪 DB Coordinator/Points。
    /// LatestData / HistoryData 舊資料比照既有行為保留。
    /// </summary>
    public async Task<(bool isSuccess, string szMessage)> DeleteServerAsync(int nId)
    {
        var coordinators = (await _repository.GetAllOpcUaCoordinatorsAsync()).ToList();
        var target = coordinators.FirstOrDefault(c => c.Id == nId);
        if (target == null)
            return (false, _l["opcuacoord.svc.not_found"].Value);

        await _fileGate.WaitAsync();
        try
        {
            var szFolder = GetWatchedFolder();
            if (szFolder == null)
                return (false, _l["opcuacoord.svc.folder_not_configured"].Value);

            var szPath = Path.Combine(szFolder, target.szName + ".json");
            if (File.Exists(szPath))
                File.Delete(szPath);
            MirrorDelete(target.szName + ".json");

            await _repository.DeleteOpcUaCoordinatorAsync(nId);

            _logger.LogInformation("OPC UA Server 已刪除: {Name} (Id={Id})", target.szName, nId);
            return (true, _l["opcuacoord.svc.delete_success"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除 OPC UA Server 失敗: Id={Id}", nId);
            return (false, _l["opcuacoord.svc.json_write_failed", ex.Message].Value);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>
    /// 全量儲存指定 Server 的 Devices + 點位。
    /// Seq=0 的新點位由此配號（NextSeq 單調遞增、刪除不回收 — SID 被 HistoryData/警報/計算點位引用不可漂移）。
    /// </summary>
    public async Task<(bool isSuccess, string szMessage)> SavePointsAsync(SaveOpcUaPointsRequest request)
    {
        var coordinators = (await _repository.GetAllOpcUaCoordinatorsAsync()).ToList();
        var coordinator = coordinators.FirstOrDefault(c => c.Id == request.Id);
        if (coordinator == null)
            return (false, _l["opcuacoord.svc.not_found"].Value);

        // ── 驗證 ──
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSeq = new HashSet<int>();
        foreach (var device in request.Devices)
        {
            var szDevice = (device.Name ?? string.Empty).Trim();
            if (szDevice.Length == 0)
                return (false, _l["opcuacoord.svc.device_name_required"].Value);
            if (!deviceNames.Add(szDevice))
                return (false, _l["opcuacoord.svc.device_name_duplicate", szDevice].Value);

            foreach (var tag in device.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag.Name))
                    return (false, _l["opcuacoord.svc.tag_name_required", szDevice].Value);
                if (string.IsNullOrWhiteSpace(tag.TagName))
                    return (false, _l["opcuacoord.svc.nodeid_required", tag.Name].Value);

                try
                {
                    Opc.Ua.NodeId.Parse(tag.TagName.Trim());
                }
                catch
                {
                    return (false, _l["opcuacoord.svc.nodeid_invalid", tag.TagName].Value);
                }

                var szControlType = (tag.ControlType ?? string.Empty).Trim().ToUpperInvariant();
                if (szControlType is not ("" or "AO" or "DO"))
                    return (false, _l["opcuacoord.svc.controltype_invalid", tag.Name].Value);
                tag.ControlType = szControlType;

                if (tag.Ratio == 0f)
                    return (false, _l["opcuacoord.svc.ratio_zero", tag.Name].Value);
                if (tag.Min.HasValue && tag.Max.HasValue && tag.Min.Value >= tag.Max.Value)
                    return (false, _l["opcuacoord.svc.minmax_invalid", tag.Name].Value);

                if (tag.Seq > 0 && !seenSeq.Add(tag.Seq))
                    return (false, _l["opcuacoord.svc.seq_duplicate", tag.Seq].Value);
            }
        }

        await _fileGate.WaitAsync();
        try
        {
            var szFolder = GetWatchedFolder();
            if (szFolder == null)
                return (false, _l["opcuacoord.svc.folder_not_configured"].Value);
            Directory.CreateDirectory(szFolder);
            var szPath = Path.Combine(szFolder, coordinator.szName + ".json");

            // JSON 不存在（外部刪檔等）→ 以 DB 快照重建連線設定
            var file = await ReadJsonFileAsync(szPath) ?? new OpcUaJsonFile
            {
                Name = coordinator.szName,
                EndpointUrl = coordinator.szEndpointUrl,
                Username = coordinator.szUsername,
                Password = coordinator.szPassword,
                PollingInterval = coordinator.nPollingInterval,
                ConnectTimeout = coordinator.nConnectTimeout,
                MonitorEnabled = coordinator.isMonitorEnabled
            };

            // NextSeq 單調遞增：涵蓋 JSON 記錄值、DB 既有點位、本次傳入的最大 Seq
            var dbPoints = (await _repository.GetOpcUaPointsByCoordinatorIdAsync(request.Id)).ToList();
            var nNextSeq = Math.Max(file.NextSeq, 1);
            if (dbPoints.Count > 0)
                nNextSeq = Math.Max(nNextSeq, dbPoints.Max(p => p.nSequence) + 1);
            if (seenSeq.Count > 0)
                nNextSeq = Math.Max(nNextSeq, seenSeq.Max() + 1);

            // 組 JSON Devices + DB 點位清單（Seq=0 → 配新號）
            var jsonDevices = new List<OpcUaJsonDevice>();
            var pointModels = new List<OpcUaPointModel>();
            foreach (var device in request.Devices)
            {
                var jsonDevice = new OpcUaJsonDevice { Name = device.Name.Trim() };
                foreach (var tag in device.Tags)
                {
                    var nSeq = tag.Seq > 0 ? tag.Seq : nNextSeq++;
                    jsonDevice.Tags.Add(new OpcUaJsonTag
                    {
                        Seq = nSeq,
                        Name = tag.Name.Trim(),
                        TagName = tag.TagName.Trim(),
                        ControlType = tag.ControlType,
                        Ratio = tag.Ratio.ToString("G"),
                        Unit = (tag.Unit ?? string.Empty).Trim(),
                        Min = tag.Min,
                        Max = tag.Max
                    });
                    pointModels.Add(new OpcUaPointModel
                    {
                        szSID = $"OPC{request.Id}-S{nSeq}",
                        nCoordinatorId = request.Id,
                        szDeviceName = jsonDevice.Name,
                        nSequence = nSeq,
                        szName = tag.Name.Trim(),
                        szTagName = tag.TagName.Trim(),
                        szControlType = tag.ControlType,
                        fRatio = tag.Ratio,
                        szUnit = (tag.Unit ?? string.Empty).Trim(),
                        fMin = tag.Min,
                        fMax = tag.Max
                    });
                }
                jsonDevices.Add(jsonDevice);
            }

            file.Devices = jsonDevices;
            file.NextSeq = nNextSeq;
            file.WebEditedAt = SourceJsonStamp.Now();

            await WriteJsonFileAsync(szPath, file);
            await _repository.SaveOpcUaPointsAsync(request.Id, pointModels);

            _logger.LogInformation("OPC UA 點位已儲存: {Name} (Id={Id}), Device {DeviceCount} 組, 點位 {Count}",
                coordinator.szName, request.Id, jsonDevices.Count, pointModels.Count);
            return (true, _l["opcuacoord.svc.points_saved", pointModels.Count].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存 OPC UA 點位失敗: Id={Id}", request.Id);
            return (false, _l["opcuacoord.svc.json_write_failed", ex.Message].Value);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>
    /// 測試讀取單一 NodeId（驗證 Endpoint / 帳密 / NodeId）。
    /// Password 空且 ServerId>0 時，使用該 Server 已儲存的密碼。
    /// </summary>
    public async Task<(bool isSuccess, string szMessage)> TestReadAsync(TestReadOpcUaRequest request)
    {
        var szEndpoint = (request.EndpointUrl ?? string.Empty).Trim();
        if (!szEndpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
            return (false, _l["opcuacoord.svc.endpoint_invalid"].Value);

        Opc.Ua.NodeId nodeId;
        try
        {
            nodeId = Opc.Ua.NodeId.Parse((request.NodeId ?? string.Empty).Trim());
        }
        catch
        {
            return (false, _l["opcuacoord.svc.nodeid_invalid", request.NodeId ?? string.Empty].Value);
        }

        var szPassword = request.Password ?? string.Empty;
        if (string.IsNullOrEmpty(szPassword) && request.ServerId > 0)
        {
            var coordinators = await _repository.GetAllOpcUaCoordinatorsAsync();
            szPassword = coordinators.FirstOrDefault(c => c.Id == request.ServerId)?.szPassword ?? string.Empty;
        }

        Opc.Ua.Client.ISession? session = null;
        try
        {
            session = await OpcUaClientHelper.CreateSessionAsync(
                szEndpoint, request.Username ?? string.Empty, szPassword, 5000, "ScadaWeb_TestRead");

            var dv = await OpcUaClientHelper.ReadSingleValueAsync(session, nodeId);
            if (dv == null || !Opc.Ua.StatusCode.IsGood(dv.StatusCode))
                return (false, _l["opcuacoord.svc.test_bad_status", dv.StatusCode.ToString()].Value);

            var szRaw = dv.Value?.ToString() ?? "null";
            if (OpcUaClientHelper.TryConvertToDouble(dv.Value, out var dRaw))
            {
                var fRatio = request.Ratio == 0f ? 1.0f : request.Ratio;
                var dEng = dRaw * fRatio;
                return (true, _l["opcuacoord.svc.test_success", szRaw, dEng.ToString("G6")].Value);
            }

            // 非數值型別（字串等）— 顯示原始值，提醒無法進數值 pipeline
            return (true, _l["opcuacoord.svc.test_success_nonnumeric", szRaw].Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OPC UA 測試讀取失敗: {Endpoint} {NodeId}", szEndpoint, request.NodeId);
            return (false, _l["opcuacoord.svc.test_failed", ex.Message].Value);
        }
        finally
        {
            await OpcUaClientHelper.CloseSessionSafelyAsync(session);
        }
    }

    /// <summary>
    /// 以 JSON 為準同步 DB（Excel 匯入提交後呼叫）：UPSERT Coordinator → 依 Devices/Tags 重建 OpcUaPoints。
    /// 與 SavePointsAsync 落地的效果相同（先 JSON、再 DB、Controller 再發 reload + 同步 Web 快取），
    /// 若只發 reload，Web 端快取同步可能早於 Engine UPSERT，新點位會暫時不顯示（plan 2026-10-12 決策 7）。
    /// JSON 不存在或壞掉回 false（匯入框架已驗證過，理論上不會發生）。
    /// Seq ≤ 0 的點位（手寫檔）比照 Engine loader 補號，不寫回 JSON（Engine reload 會再 UPSERT 一次，冪等）。
    /// </summary>
    public async Task<bool> SyncDbFromJsonAsync(string szName)
    {
        var szFolder = GetWatchedFolder();
        if (szFolder == null) return false;

        var szPath = Path.Combine(szFolder, szName + ".json");
        var file = await ReadJsonFileAsync(szPath);
        if (file == null) return false;

        var szCoordName = string.IsNullOrWhiteSpace(file.Name) ? szName : file.Name;
        var nId = await _repository.SaveOpcUaCoordinatorAsync(new OpcUaCoordinatorModel
        {
            szName = szCoordName,
            szEndpointUrl = file.EndpointUrl,
            szUsername = file.Username,
            szPassword = file.Password,
            nPollingInterval = file.PollingInterval > 0 ? file.PollingInterval : 1000,
            nConnectTimeout = file.ConnectTimeout > 0 ? file.ConnectTimeout : 5000,
            isMonitorEnabled = file.MonitorEnabled
        });

        var nMaxSeq = file.Devices.SelectMany(d => d.Tags).Where(t => t.Seq > 0).Select(t => t.Seq).DefaultIfEmpty(0).Max();
        var pointModels = new List<OpcUaPointModel>();
        var seenSeq = new HashSet<int>();
        foreach (var device in file.Devices)
        {
            foreach (var tag in device.Tags)
            {
                var nSeq = tag.Seq > 0 ? tag.Seq : ++nMaxSeq;
                if (!seenSeq.Add(nSeq)) continue; // 重複 Seq → 後者跳過（與 Engine loader 一致）

                pointModels.Add(new OpcUaPointModel
                {
                    szSID = $"OPC{nId}-S{nSeq}",
                    nCoordinatorId = nId,
                    szDeviceName = device.Name,
                    nSequence = nSeq,
                    szName = tag.Name,
                    szTagName = tag.TagName,
                    szControlType = (tag.ControlType ?? string.Empty).Trim().ToUpperInvariant() is var ct && ct is "AO" or "DO" ? ct : string.Empty,
                    fRatio = float.TryParse(tag.Ratio, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f != 0f ? f : 1.0f,
                    szUnit = tag.Unit ?? string.Empty,
                    fMin = tag.Min,
                    fMax = tag.Max
                });
            }
        }
        await _repository.SaveOpcUaPointsAsync(nId, pointModels);

        _logger.LogInformation("OPC UA 設定已由 JSON 同步至 DB: {Name} (Id={Id}), 點位 {Count}", szCoordName, nId, pointModels.Count);
        return true;
    }

    private static async Task<OpcUaJsonFile?> ReadJsonFileAsync(string szPath)
    {
        if (!File.Exists(szPath)) return null;
        try
        {
            var szJson = await File.ReadAllTextAsync(szPath);
            return JsonSerializer.Deserialize<OpcUaJsonFile>(szJson, _jsonReadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 原子寫檔 + 鏡像寫回（比照 ModbusConfigFileService）：
    /// 先寫 *.json.tmp 再 File.Replace 原子替換（留 *.json.bak 備份），
    /// 確保 Engine reload 任何瞬間讀到的都是完整舊檔或完整新檔；新檔（無舊檔可替換）直接 Move。
    /// </summary>
    private Task WriteJsonFileAsync(string szPath, OpcUaJsonFile file)
    {
        var szJson = JsonSerializer.Serialize(file, _jsonWriteOptions);

        var szTmpPath = szPath + ".tmp";
        File.WriteAllText(szTmpPath, szJson, System.Text.Encoding.UTF8);
        if (File.Exists(szPath))
            File.Replace(szTmpPath, szPath, szPath + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(szTmpPath, szPath);

        MirrorWrite(Path.GetFileName(szPath), szJson);
        return Task.CompletedTask;
    }

    /// <summary>dev 環境鏡像寫回原始碼資料夾（失敗僅記 log，不影響主寫入）</summary>
    private void MirrorWrite(string szFileName, string szContent)
    {
        var szMirrorFolder = GetMirrorFolder();
        if (szMirrorFolder == null) return;

        try
        {
            if (!Directory.Exists(szMirrorFolder)) return;
            var szMirrorPath = Path.Combine(szMirrorFolder, szFileName);
            File.WriteAllText(szMirrorPath, szContent, System.Text.Encoding.UTF8);
            _logger.LogInformation("OPC UA 設定已鏡像寫回: {File}", szMirrorPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OPC UA 鏡像寫回失敗（不影響主寫入）: {File}", szFileName);
        }
    }

    /// <summary>鏡像資料夾同步刪檔（失敗僅記 log，不影響主刪除）</summary>
    private void MirrorDelete(string szFileName)
    {
        var szMirrorFolder = GetMirrorFolder();
        if (szMirrorFolder == null) return;

        try
        {
            var szMirrorPath = Path.Combine(szMirrorFolder, szFileName);
            if (File.Exists(szMirrorPath))
                File.Delete(szMirrorPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OPC UA 鏡像刪檔失敗（不影響主刪除）: {File}", szFileName);
        }
    }
}
