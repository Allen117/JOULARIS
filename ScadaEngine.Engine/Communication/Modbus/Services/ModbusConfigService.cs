using Newtonsoft.Json;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Data.Repositories;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Models;

namespace ScadaEngine.Engine.Communication.Modbus.Services;

/// <summary>
/// Modbus 設定檔讀取服務，負責載入 JSON 配置並建立設備物件
/// </summary>
public class ModbusConfigService
{
    private readonly ILogger<ModbusConfigService> _logger;
    private readonly string _szConfigFolderPath;
    private readonly SqlServerDataRepository _dataRepository;

    public ModbusConfigService(ILogger<ModbusConfigService> logger, SqlServerDataRepository dataRepository)
    {
        _logger = logger;
        _szConfigFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Modbus");
        _dataRepository = dataRepository;
    }

    /// <summary>
    /// 載入指定資料夾下所有 Modbus JSON 設定檔
    /// </summary>
    /// <returns>設備配置清單</returns>
    public async Task<List<ModbusDeviceConfigModel>> LoadAllDeviceConfigsAsync()
    {
        var configList = new List<ModbusDeviceConfigModel>();

        try
        {
            // 檢查設定檔資料夾是否存在
            if (!Directory.Exists(_szConfigFolderPath))
            {
                _logger.LogWarning("Modbus 設定檔資料夾不存在: {FolderPath}", _szConfigFolderPath);
                return configList;
            }

            // 搜尋所有 JSON 檔案
            var jsonFiles = Directory.GetFiles(_szConfigFolderPath, "*.json", SearchOption.TopDirectoryOnly);
            
            if (jsonFiles.Length == 0)
            {
                _logger.LogWarning("未找到任何 Modbus 設定檔案於: {FolderPath}", _szConfigFolderPath);
                return configList;
            }

            _logger.LogInformation("找到 {Count} 個 Modbus 設定檔案", jsonFiles.Length);

            // 並行載入所有設定檔
            var loadTasks = jsonFiles.Select(async szFilePath =>
            {
                try
                {
                    var config = await LoadSingleDeviceConfigAsync(szFilePath);
                    if (config != null)
                    {
                        // 生成點位 SID
                        await GenerateTagSIDsAsync(config, szFilePath);
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "載入設定檔失敗: {FilePath}", szFilePath);
                }
                return null;
            });

            var results = await Task.WhenAll(loadTasks);
            configList.AddRange(results.Where(config => config != null)!);

            _logger.LogInformation("成功載入 {Count} 個有效的 Modbus 設備配置", configList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入 Modbus 設定檔時發生錯誤");
        }

        return configList;
    }

    /// <summary>
    /// 載入單個 Modbus 設定檔
    /// </summary>
    /// <param name="szFilePath">設定檔路徑</param>
    /// <returns>設備配置物件</returns>
    private async Task<ModbusDeviceConfigModel?> LoadSingleDeviceConfigAsync(string szFilePath)
    {
        try
        {
            var szJsonContent = await File.ReadAllTextAsync(szFilePath);
            
            if (string.IsNullOrWhiteSpace(szJsonContent))
            {
                _logger.LogWarning("設定檔內容為空: {FilePath}", szFilePath);
                return null;
            }

            // 反序列化 JSON 設定
            var config = JsonConvert.DeserializeObject<ModbusDeviceConfigModel>(szJsonContent);
            
            if (config == null)
            {
                _logger.LogWarning("無法解析設定檔 JSON 格式: {FilePath}", szFilePath);
                return null;
            }

            // 映射 JSON 屬性到 Model 屬性
            MapJsonToConfigModel(config, szJsonContent);

            // 驗證設定有效性
            if (!config.Validate())
            {
                _logger.LogWarning("設定檔驗證失敗: {FilePath}", szFilePath);
                return null;
            }

            _logger.LogDebug("成功載入設定檔: {FilePath}, IP: {IP}, 點位數量: {TagCount}", 
                           szFilePath, config.szIP, config.tagList.Count);

            return config;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON 格式錯誤於設定檔: {FilePath}", szFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入設定檔時發生未預期錯誤: {FilePath}", szFilePath);
        }

        return null;
    }

    /// <summary>
    /// 映射 JSON 屬性到配置模型 (處理 JSON 屬性名稱與 Model 屬性的對應)
    /// </summary>
    /// <param name="config">配置模型物件</param>
    /// <param name="szJsonContent">JSON 內容</param>
    private void MapJsonToConfigModel(ModbusDeviceConfigModel config, string szJsonContent)
    {
        try
        {
            // 使用動態物件解析 JSON 以處理屬性名稱對應
            dynamic? jsonObj = JsonConvert.DeserializeObject(szJsonContent);
            
            if (jsonObj == null) return;

            // 映射根節點屬性
            config.szIP = jsonObj.IP ?? string.Empty;
            config.nPort = jsonObj.Port ?? 502;
            config.szModbusId = jsonObj.ModbusId ?? "1";
            config.nConnectTimeout = jsonObj.ConnectTimeout ?? 1000;

            // 選填通訊參數（Excel 範本不放，進階現場手改 JSON）
            int nRetryCount = jsonObj.RetryCount ?? 2;
            config.nRetryCount = Math.Clamp(nRetryCount, 0, 5);
            int nNoResponseRounds = jsonObj.NoResponseReconnectRounds ?? 5;
            config.nNoResponseReconnectRounds = Math.Max(1, nNoResponseRounds);
            int nProbeIntervalMs = jsonObj.UnreachableProbeIntervalMs ?? 30000;
            config.nUnreachableProbeIntervalMs = Math.Clamp(nProbeIntervalMs, 0, 3_600_000);

            // 子站名稱：欄位不存在（舊檔）→ null，載入時不覆蓋 DB 既有值
            Newtonsoft.Json.Linq.JToken? deviceNameToken = jsonObj.DeviceName;
            config.szDeviceName = deviceNameToken is null || deviceNameToken.Type == Newtonsoft.Json.Linq.JTokenType.Null
                ? null
                : deviceNameToken.ToString();

            // 映射點位清單
            if (jsonObj.Tags != null)
            {
                config.tagList.Clear();
                
                foreach (var tagJson in jsonObj.Tags)
                {
                    var tag = new ModbusTagModel
                    {
                        szName = tagJson.Name ?? string.Empty,
                        szAddress = tagJson.Address ?? string.Empty,
                        szDataType = tagJson.DataType ?? "Integer",
                        szRatio = tagJson.Ratio?.ToString() ?? "1",
                        szUnit = tagJson.Unit ?? string.Empty,
                        szMax = tagJson.Max?.ToString() ?? string.Empty,
                        szMin = tagJson.Min?.ToString() ?? string.Empty,
                        // 站號內子設備分群（optional，舊設定檔無此欄 → 空字串，行為不變）
                        szDevice = tagJson.Device ?? string.Empty
                    };

                    // 驗證並解析點位設定
                    if (tag.Validate())
                    {
                        config.tagList.Add(tag);
                    }
                    else if (!string.IsNullOrEmpty(tag.szValidationError))
                    {
                        // 語意設定錯誤（如 BIT 型別配到 Coil 位址）— 明確錯誤，需現場修正
                        _logger.LogError("點位設定無效，已跳過: {TagName} ({Address}) — {Reason}",
                                         tag.szName, tag.szAddress, tag.szValidationError);
                    }
                    else
                    {
                        _logger.LogWarning("點位設定無效，已跳過: {TagName} ({Address})", tag.szName, tag.szAddress);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "映射 JSON 屬性時發生錯誤");
        }
    }

    /// <summary>
    /// 為設備配置儲存資料庫 ID 並將點位插入 ModbusPoints 表
    /// </summary>
    /// <param name="config">設備配置</param>
    /// <param name="szFilePath">設定檔路徑</param>
    private async Task GenerateTagSIDsAsync(ModbusDeviceConfigModel config, string szFilePath)
    {
        try
        {
            // 將配置儲存到資料庫並取得真實 ID 與採集週期
            var (nDatabaseId, nDelayTime) = await GetConfigDatabaseIdAsync(config, szFilePath);

            // 將 DatabaseId 儲存到配置中，供後續動態 SID 生成使用
            config.nDatabaseId = nDatabaseId;

            // 從 DB 讀取採集週期（DelayTime），若為 0 則使用預設 1000ms
            config.nCollectionIntervalMs = nDelayTime > 0 ? nDelayTime : 1000;

            // 將 Coordinator 名稱 (JSON 檔名) 儲存到配置中，供發布 MQTT 時附帶
            config.szCoordinatorName = Path.GetFileNameWithoutExtension(szFilePath);

            _logger.LogInformation("設備配置已設定: DatabaseId={DatabaseId}, IP={IP}, ModbusId={ModbusId}, CoordinatorName={CoordinatorName}, 採集週期={IntervalMs}ms, 點位數量={TagCount}",
                                 nDatabaseId, config.szIP, config.szModbusId, config.szCoordinatorName, config.nCollectionIntervalMs, config.tagList.Count);

            // 將所有點位插入 ModbusPoints 表
            await InsertTagsToModbusPointsAsync(config, nDatabaseId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定 Database ID 時發生錯誤");
        }
    }

    /// <summary>
    /// 將點位資料插入 ModbusPoints 表
    /// </summary>
    /// <param name="config">設備配置</param>
    /// <param name="nDatabaseId">資料庫 ID</param>
    private async Task InsertTagsToModbusPointsAsync(ModbusDeviceConfigModel config, int nDatabaseId)
    {
        try
        {
            if (!config.tagList.Any())
            {
                _logger.LogWarning("設備 {DatabaseId} 沒有點位資料，跳過 ModbusPoints 插入", nDatabaseId);
                return;
            }

            var pointList = new List<ModbusPointModel>();

            // 解析多個 ModbusId (用逗號分隔)
            var modbusIds = config.szModbusId.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => int.TryParse(id, out _))
                .Select(int.Parse)
                .ToList();

            if (!modbusIds.Any())
            {
                _logger.LogWarning("設備 {DatabaseId} 的 ModbusId 格式錯誤: {ModbusId}", nDatabaseId, config.szModbusId);
                return;
            }

            // 站號 / Device 互斥（plan 決策 4）：多站號時每個站號即一台設備，
            // 站號優先、Tag.Device 靜默忽略 → DeviceGroup 一律留 null（規則見 ResolveDeviceGroup）。
            var isMultiStation = modbusIds.Count > 1;

            // 為每個 ModbusId 生成點位
            foreach (var nModbusId in modbusIds)
            {
                for (int nTagIndex = 0; nTagIndex < config.tagList.Count; nTagIndex++)
                {
                    var tag = config.tagList[nTagIndex];

                    // 生成 SID:
                    var szSID = (nDatabaseId * 65536 + nModbusId * 256 +1).ToString() + "-S" + (nTagIndex+1).ToString();

                    var szDeviceGroup = ModbusPointModel.ResolveDeviceGroup(tag.szDevice, isMultiStation);

                    // 建立 ModbusPointModel
                    var point = ModbusPointModel.FromTag(tag, szSID, szDeviceGroup);
                    
                    if (point.Validate())
                    {
                        pointList.Add(point);
                    }
                    else
                    {
                        _logger.LogWarning("點位驗證失敗，跳過: Name={Name}, Address={Address}", tag.szName, tag.szAddress);
                    }
                }
            }

            // 批量插入到 ModbusPoints 表
            var nInsertedCount = await _dataRepository.SaveModbusPointsAsync(nDatabaseId, pointList);
            
            _logger.LogInformation("設備 {DatabaseId} 成功插入 {Count} 個點位到 ModbusPoints 表", 
                nDatabaseId, nInsertedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "插入點位到 ModbusPoints 表時發生錯誤: DatabaseId={DatabaseId}", nDatabaseId);
        }
    }

    /// <summary>
    /// 將設定檔內容儲存到資料庫並取得 ID
    /// </summary>
    /// <param name="config">設備配置</param>
    /// <param name="szFilePath">設定檔路徑</param>
    /// <returns>(資料庫 ID, DB 中的 DelayTime)</returns>
    private async Task<(int nId, int nDelayTime)> GetConfigDatabaseIdAsync(ModbusDeviceConfigModel config, string szFilePath)
    {
        try
        {
            var szFileName = Path.GetFileNameWithoutExtension(szFilePath);

            // 建立 Coordinator 模型
            var coordinator = new CoordinatorModel
            {
                szName = szFileName,
                szModbusID = config.szModbusId,
                nDelayTime = config.nConnectTimeout,
                isMonitorEnabled = true
            };

            // 儲存或更新到資料庫並取得 ID 與 DelayTime
            var (nDatabaseId, nDelayTime) = // 子站名稱：null（舊 JSON 無此欄位）→ Repository 不動 DB 的 DeviceName
            await _dataRepository.SaveCoordinatorAsync(coordinator, config.szDeviceName);

            _logger.LogInformation("Coordinator 配置已儲存到資料庫: ID={Id}, Name={Name}, ModbusID={ModbusID}, DelayTime={DelayTime}ms",
                                 nDatabaseId, coordinator.szName, coordinator.szModbusID, nDelayTime);

            return (nDatabaseId, nDelayTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存 Coordinator 配置到資料庫時發生錯誤: {FilePath}", szFilePath);

            // 發生錯誤時使用檔案名稱 Hash 作為備用方案
            var szFileName = Path.GetFileNameWithoutExtension(szFilePath);
            var nHashCode = Math.Abs(szFileName.GetHashCode());
            return (nHashCode % 1000 + 1, 1000);
        }
    }

    /// <summary>
    /// 重新載入指定設定檔
    /// </summary>
    /// <param name="szFilePath">設定檔路徑</param>
    /// <returns>重新載入的設備配置</returns>
    public async Task<ModbusDeviceConfigModel?> ReloadDeviceConfigAsync(string szFilePath)
    {
        // 防呆：只有真正的 .json 才可進入載入流程（見 IsJsonConfigFile 說明）
        if (!IsJsonConfigFile(szFilePath))
        {
            _logger.LogDebug("忽略非 .json 設定檔的重載請求: {FilePath}", szFilePath);
            return null;
        }

        _logger.LogInformation("重新載入 Modbus 設定檔: {FilePath}", szFilePath);

        var config = await LoadSingleDeviceConfigAsync(szFilePath);
        
        if (config != null)
        {
            await GenerateTagSIDsAsync(config, szFilePath);
            _logger.LogInformation("成功重新載入設備配置: {IP}", config.szIP);
        }
        
        return config;
    }

    /// <summary>
    /// 監控設定檔變更
    /// </summary>
    /// <param name="onConfigChanged">設定檔新增 / 變更時的回調函式（參數：設定檔完整路徑）</param>
    /// <param name="onConfigDeleted">
    /// 設定檔移除時的回調函式（參數：被移除的設定檔完整路徑）。
    /// Web 端「刪除設備」是把 {name}.json 搬到 _deleted/ 子資料夾，Windows 對搬離監控範圍的檔案只發 Deleted 事件（已實測）；
    /// 同資料夾內 X.json → Y.json 改名也視為 X 移除 + Y 新增。
    /// </param>
    /// <returns>檔案監控器</returns>
    public FileSystemWatcher CreateConfigFileWatcher(Action<string> onConfigChanged, Action<string>? onConfigDeleted = null)
    {
        // 資料夾不存在時 FileSystemWatcher 建構會丟例外 → 先建好（新裝機可能只有空資料夾甚至沒有）
        Directory.CreateDirectory(_szConfigFolderPath);

        var watcher = new FileSystemWatcher(_szConfigFolderPath)
        {
            Filter = "*.json",
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };

        watcher.Deleted += (sender, e) =>
        {
            if (!IsJsonConfigFile(e.FullPath)) return;
            _logger.LogInformation("偵測到設定檔移除: {FilePath}", e.FullPath);
            onConfigDeleted?.Invoke(e.FullPath!);
        };

        watcher.Changed += async (sender, e) =>
        {
            if (!IsJsonConfigFile(e.FullPath)) return;
            _logger.LogInformation("偵測到設定檔變更: {FilePath}", e.FullPath);

            // 延遲一下避免檔案被鎖定
            await Task.Delay(500);
            onConfigChanged(e.FullPath!);
        };

        watcher.Created += async (sender, e) =>
        {
            if (!IsJsonConfigFile(e.FullPath)) return;
            _logger.LogInformation("偵測到新設定檔: {FilePath}", e.FullPath);

            await Task.Delay(500);
            onConfigChanged(e.FullPath!);
        };

        // 原子替換（File.Replace / 先寫 tmp 再改名）在 Windows 以 rename 落地，只會產生 Renamed 事件。
        // 注意：filter "*.json" 對 rename 是「舊名或新名任一符合就觸發」，X.json → X.json.bak 這一步
        // 舊名符合而以 e.FullPath=X.json.bak 觸發，IsJsonConfigFile 守衛在此擋掉，避免吃到 .bak 生幽靈 coordinator。
        watcher.Renamed += async (sender, e) =>
        {
            var isOldJson = IsJsonConfigFile(e.OldFullPath);
            var isNewJson = IsJsonConfigFile(e.FullPath);
            var isSameDir = string.Equals(Path.GetDirectoryName(e.OldFullPath), Path.GetDirectoryName(e.FullPath),
                                          StringComparison.OrdinalIgnoreCase);

            // 舊名是 .json 且「改成另一個 .json 名」或「搬離資料夾」→ 舊設備視為移除。
            // X.json → X.json.bak（File.Replace 中間步驟，同資料夾、新名非 .json）不算移除，否則每次熱編輯都會把設備停掉。
            if (isOldJson && (isNewJson || !isSameDir))
            {
                _logger.LogInformation("偵測到設定檔改名 / 搬離，舊設定視為移除: {OldPath}", e.OldFullPath);
                onConfigDeleted?.Invoke(e.OldFullPath!);
            }

            if (!isNewJson) return;

            // 新名落在監控資料夾外（搬進子資料夾）不載入
            if (!string.Equals(Path.GetDirectoryName(e.FullPath), _szConfigFolderPath.TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase))
                return;

            _logger.LogInformation("偵測到設定檔改名（原子替換）: {FilePath}", e.FullPath);

            await Task.Delay(500);
            onConfigChanged(e.FullPath!);
        };

        return watcher;
    }

    /// <summary>
    /// 副檔名守衛：只有真正的 .json 設定檔才該進入載入流程。
    /// FileSystemWatcher 的 "*.json" filter 對 rename 事件是「舊名或新名任一符合就觸發」，
    /// Web 原子存檔 File.Replace 會把 X.json 改名成 X.json.bak（舊名符合 filter），
    /// 以 e.FullPath=X.json.bak 觸發 Renamed；若不擋，GetFileNameWithoutExtension("X.json.bak")
    /// = "X.json" 會被當成 Coordinator 名稱寫進 DB，形成幽靈 coordinator。
    /// </summary>
    private static bool IsJsonConfigFile(string? szFilePath)
        => !string.IsNullOrEmpty(szFilePath)
           && Path.GetExtension(szFilePath).Equals(".json", StringComparison.OrdinalIgnoreCase);
}