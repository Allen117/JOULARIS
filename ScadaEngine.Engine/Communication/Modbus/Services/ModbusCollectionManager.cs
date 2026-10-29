using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Communication.Modbus.Services;
using ScadaEngine.Engine.Communication.Mqtt;
using System.Collections.Concurrent;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Communication.Modbus.Services;

/// <summary>
/// Modbus 採集管理器，負責管理多個設備的獨立執行緒採集作業
/// </summary>
public class ModbusCollectionManager : IDisposable
{
    private readonly ModbusConfigService _configService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModbusCollectionManager> _logger;
    private readonly MqttPublishService? _mqttPublishService;
    
    /// <summary>
    /// 執行中的採集迴圈，以設定檔名（= Coordinator 名稱，不分大小寫）為鍵。
    /// 每個設定檔各自一個 ModbusCommunicationService（各自一條連線），同 IP:Port 的多個設定檔互不影響；
    /// 同名重載 / 移除序列化，避免交錯再生孤兒迴圈。
    /// </summary>
    private readonly DeviceCollectionRegistry<ModbusCommunicationService> _registry = new();
    
    private FileSystemWatcher? _configFileWatcher;
    private bool _isDisposed = false;

    /// <summary>per-file 重載去抖計時器 — 一次存檔 FileSystemWatcher 常觸發 2~3 次事件，去抖後只重載一次（設備只斷線重連一次）</summary>
    private readonly ConcurrentDictionary<string, System.Threading.Timer> _reloadDebounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private const int RELOAD_DEBOUNCE_MS = 1000;

    /// <summary>true 時各採集迴圈跳過資料讀取（連線保持）</summary>
    private volatile bool _isSuspended = false;

    /// <summary>
    /// 預設採集週期 (毫秒)，當設備未設定時使用
    /// </summary>
    public int CollectionIntervalMs { get; set; } = 1000;

    /// <summary>
    /// 資料採集事件，當有新資料產生時觸發
    /// </summary>
    public event EventHandler<RealtimeDataCollectedEventArgs>? DataCollected;

    /// <summary>
    /// 設備狀態變更事件
    /// </summary>
    public event EventHandler<DeviceStatusChangedEventArgs>? DeviceStatusChanged;

    public ModbusCollectionManager(
        ModbusConfigService configService,
        IServiceProvider serviceProvider,
        ILogger<ModbusCollectionManager> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // 嘗試取得 MQTT 發布服務 (可選)
        _mqttPublishService = _serviceProvider.GetService<MqttPublishService>();
    }

    /// <summary>
    /// 啟動 Modbus 採集服務
    /// </summary>
    public async Task StartAsync()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ModbusCollectionManager));

        try
        {
            _logger.LogInformation("啟動 Modbus 採集管理器");

            // 載入所有設備配置
            var deviceConfigs = await _configService.LoadAllDeviceConfigsAsync();

            // 啟動配置檔案監控 — 一定要在「有沒有設備」判斷之前：
            // 新裝機 Modbus\ 是空的（Release 只帶範本），設備全靠 Web 匯入 Excel 事後寫入，
            // 若空載時不掛 watcher，匯入的 JSON 要等 Engine 重啟才會被採集。
            StartConfigFileWatcher();

            if (deviceConfigs.Count == 0)
            {
                _logger.LogWarning("未找到有效的 Modbus 設備配置，採集服務將以空載模式執行（watcher 已啟動，匯入設定後自動開始採集）");
                return;
            }

            // 為每個設定檔建立獨立的採集執行緒（同 IP:Port 多檔各自一條）
            foreach (var config in deviceConfigs)
            {
                await StartOrReplaceDeviceCollectionAsync(config);
            }

            _logger.LogInformation("Modbus 採集管理器啟動完成，共管理 {DeviceCount} 個設備", deviceConfigs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動 Modbus 採集管理器時發生錯誤");
            throw;
        }
    }

    /// <summary>
    /// 啟動（或取代同名舊的）單一設定檔採集執行緒 — 同名舊迴圈先停再起新；同 IP:Port 的其他設定檔不受影響
    /// </summary>
    /// <param name="deviceConfig">設備配置</param>
    private async Task StartOrReplaceDeviceCollectionAsync(ModbusDeviceConfigModel deviceConfig)
    {
        var szName = GetCollectionName(deviceConfig);
        var nIntervalMs = deviceConfig.nCollectionIntervalMs > 0 ? deviceConfig.nCollectionIntervalMs : CollectionIntervalMs;

        try
        {
            var isStarted = await _registry.ReplaceAsync(szName,
                () =>
                {
                    var logger = _serviceProvider.GetRequiredService<ILoggerFactory>()
                                                .CreateLogger<ModbusCommunicationService>();
                    return new ModbusCommunicationService(deviceConfig, logger, _mqttPublishService);
                },
                (communicationService, token) => RunDeviceCollectionLoopAsync(szName, communicationService, nIntervalMs, token));

            if (!isStarted)
            {
                _logger.LogInformation("採集管理器停止中，略過啟動: {Name} ({DeviceIP}:{DevicePort})", szName, deviceConfig.szIP, deviceConfig.nPort);
                return;
            }

            _logger.LogInformation("啟動設備採集執行緒: {Name} ({DeviceIP}:{DevicePort}), 採集週期={IntervalMs}ms",
                szName, deviceConfig.szIP, deviceConfig.nPort, nIntervalMs);

            var communicationService = _registry.GetService(szName);
            if (communicationService != null)
                OnDeviceStatusChanged(szName, "Started", communicationService.GetDeviceStatus());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動設備採集執行緒失敗: {Name} ({DeviceIP}:{DevicePort})", szName, deviceConfig.szIP, deviceConfig.nPort);
        }
    }

    /// <summary>
    /// 執行設備採集迴圈
    /// </summary>
    /// <param name="szDeviceKey">設定檔名（事件 DeviceKey）</param>
    /// <param name="communicationService">通訊服務</param>
    /// <param name="nIntervalMs">該設備的採集週期 (毫秒)</param>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task RunDeviceCollectionLoopAsync(string szDeviceKey, ModbusCommunicationService communicationService, int nIntervalMs, CancellationToken cancellationToken)
    {
        _logger.LogDebug("開始設備採集迴圈: {Name} ({DeviceIP}:{DevicePort})", szDeviceKey, communicationService.DeviceIP, communicationService.DevicePort);

        try
        {
            // 首次連線
            _logger.LogInformation("首次連線設備: {Name} ({DeviceIP}:{DevicePort})", szDeviceKey, communicationService.DeviceIP, communicationService.DevicePort);
            var isFirstConnected = await communicationService.ConnectAsync();
            OnDeviceStatusChanged(szDeviceKey,
                isFirstConnected ? "Connected" : "Disconnected",
                communicationService.GetDeviceStatus());

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 檢查連線狀態，斷線時嘗試重連
                    if (!communicationService.IsConnected)
                    {
                        if (communicationService.ShouldReconnect())
                        {
                            _logger.LogInformation("嘗試重新連線設備: {Name} ({DeviceIP})", szDeviceKey, communicationService.DeviceIP);

                            var isConnected = await communicationService.ConnectAsync();

                            OnDeviceStatusChanged(szDeviceKey,
                                isConnected ? "Connected" : "Disconnected",
                                communicationService.GetDeviceStatus());
                        }
                    }

                    // 授權暫停中：保持連線但跳過資料讀取
                    if (_isSuspended)
                    {
                        await Task.Delay(nIntervalMs, cancellationToken);
                        continue;
                    }

                    // 執行資料採集（即使未連線也要嘗試，以產生 Bad Quality 資料）
                    var realtimeDataList = await communicationService.ReadAllTagsAsync();

                    if (realtimeDataList.Count > 0)
                    {
                        // 觸發資料採集事件
                        OnDataCollected(szDeviceKey, realtimeDataList);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，跳出迴圈
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "設備採集發生錯誤: {Name} ({DeviceIP})", szDeviceKey, communicationService.DeviceIP);
                    
                    OnDeviceStatusChanged(szDeviceKey, "Error", new { 
                        Error = ex.Message, 
                        Status = communicationService.GetDeviceStatus() 
                    });
                }

                // 等待下一次採集週期
                try
                {
                    await Task.Delay(nIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            // 斷開連線
            await communicationService.DisconnectAsync();
            OnDeviceStatusChanged(szDeviceKey, "Stopped", communicationService.GetDeviceStatus());
            
            _logger.LogDebug("設備採集迴圈結束: {Name} ({DeviceIP}:{DevicePort})", szDeviceKey, communicationService.DeviceIP, communicationService.DevicePort);
        }
    }

    /// <summary>
    /// 啟動配置檔案監控
    /// </summary>
    private void StartConfigFileWatcher()
    {
        try
        {
            _configFileWatcher = _configService.CreateConfigFileWatcher(DebounceReloadDeviceConfig, HandleConfigFileDeleted);

            _logger.LogInformation("配置檔案監控已啟動");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動配置檔案監控失敗");
        }
    }

    /// <summary>
    /// 去抖後重新載入設備配置 — 同一檔案 1 秒內連續變更只執行最後一次
    /// </summary>
    /// <param name="szChangedFilePath">變更的配置檔案路徑</param>
    private void DebounceReloadDeviceConfig(string szChangedFilePath)
    {
        if (_isDisposed) return;

        var timer = _reloadDebounceTimers.GetOrAdd(szChangedFilePath, szPath => new System.Threading.Timer(async state =>
        {
            var szFile = (string)state!;
            _logger.LogInformation("配置檔案變更，重新載入: {FilePath}", szFile);

            try
            {
                await ReloadDeviceConfigAsync(szFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新載入配置檔案失敗: {FilePath}", szFile);
            }
        }, szChangedFilePath, Timeout.Infinite, Timeout.Infinite));

        timer.Change(RELOAD_DEBOUNCE_MS, Timeout.Infinite);
    }

    /// <summary>
    /// 重新載入設備配置
    /// </summary>
    /// <param name="szConfigFilePath">配置檔案路徑</param>
    private async Task ReloadDeviceConfigAsync(string szConfigFilePath)
    {
        try
        {
            var newConfig = await _configService.ReloadDeviceConfigAsync(szConfigFilePath);
            
            if (newConfig == null)
            {
                _logger.LogWarning("重新載入的配置檔案無效: {FilePath}", szConfigFilePath);
                return;
            }

            // 只停 / 起該設定檔自己的迴圈（含改 IP/Port：同名取代即停掉舊連線）；同 IP:Port 的其他設定檔不受影響
            await StartOrReplaceDeviceCollectionAsync(newConfig);

            _logger.LogInformation("設備配置重新載入完成: {Name} ({DeviceIP}:{DevicePort})", GetCollectionName(newConfig), newConfig.szIP, newConfig.nPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新載入設備配置時發生錯誤");
        }
    }

    /// <summary>
    /// 設定檔被移除（Web 刪除設備 → 搬到 _deleted/、或改名）— 依檔名停止該設定檔的採集（同 IP:Port 其他設定檔不受影響）。
    /// FileSystemWatcher 回調執行緒不可阻塞，丟到 Task.Run。
    /// </summary>
    /// <param name="szDeletedFilePath">被移除的設定檔完整路徑</param>
    private void HandleConfigFileDeleted(string szDeletedFilePath)
    {
        if (_isDisposed) return;
        _ = Task.Run(() => RemoveDeviceForConfigAsync(szDeletedFilePath));
    }

    private async Task RemoveDeviceForConfigAsync(string szDeletedFilePath)
    {
        try
        {
            var szName = Path.GetFileNameWithoutExtension(szDeletedFilePath);

            // 取消尚未執行的重載（避免對已不存在的檔案重載）
            if (_reloadDebounceTimers.TryRemove(szDeletedFilePath, out var timer))
                timer.Dispose();

            var service = _registry.GetService(szName);
            var szEndpoint = service != null ? $"{service.DeviceIP}:{service.DevicePort}" : "";

            if (!await _registry.RemoveAsync(szName))
            {
                _logger.LogDebug("設定檔移除但無對應採集（可能本就載入失敗）: {Name}", szName);
                return;
            }

            _logger.LogInformation("設定檔已移除，停止採集: {Name} ({Endpoint})。資料庫與歷史資料保留", szName, szEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理設定檔移除時發生錯誤: {FilePath}", szDeletedFilePath);
        }
    }

    /// <summary>暫停所有設備資料讀取（連線保持，供授權守衛呼叫）</summary>
    public void Suspend()
    {
        _isSuspended = true;
        _logger.LogWarning("Modbus 採集已暫停（授權守衛）");
    }

    /// <summary>恢復所有設備資料讀取（供授權守衛呼叫）</summary>
    public void Resume()
    {
        _isSuspended = false;
        _logger.LogInformation("Modbus 採集已恢復（授權守衛）");
    }

    /// <summary>
    /// 停止所有採集服務
    /// </summary>
    public async Task StopAsync()
    {
        if (_isDisposed)
            return;

        _logger.LogInformation("停止 Modbus 採集管理器");

        try
        {
            // 停止配置檔案監控
            _configFileWatcher?.Dispose();
            _configFileWatcher = null;

            // 停止重載去抖計時器
            foreach (var timer in _reloadDebounceTimers.Values)
            {
                timer.Dispose();
            }
            _reloadDebounceTimers.Clear();

            // 停止所有設定檔的採集迴圈（之後延遲觸發的重載不會再起新迴圈）
            await _registry.StopAllAsync();

            _logger.LogInformation("Modbus 採集管理器已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止 Modbus 採集管理器時發生錯誤");
        }
    }

    /// <summary>
    /// 採集迴圈鍵 = 設定檔名（ModbusConfigService 以檔名填 szCoordinatorName）；理論上不會空，保險起見退回 IP:Port
    /// </summary>
    private static string GetCollectionName(ModbusDeviceConfigModel deviceConfig)
        => string.IsNullOrEmpty(deviceConfig.szCoordinatorName)
            ? $"{deviceConfig.szIP}:{deviceConfig.nPort}"
            : deviceConfig.szCoordinatorName;

    /// <summary>
    /// 觸發資料採集事件
    /// </summary>
    /// <param name="szDeviceKey">設定檔名</param>
    /// <param name="realtimeDataList">即時資料清單</param>
    private void OnDataCollected(string szDeviceKey, List<RealtimeDataModel> realtimeDataList)
    {
        try
        {
            DataCollected?.Invoke(this, new RealtimeDataCollectedEventArgs(szDeviceKey, realtimeDataList));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "觸發資料採集事件時發生錯誤");
        }
    }

    /// <summary>
    /// 觸發設備狀態變更事件
    /// </summary>
    /// <param name="szDeviceKey">設定檔名</param>
    /// <param name="szStatus">狀態描述</param>
    /// <param name="statusData">狀態資料</param>
    private void OnDeviceStatusChanged(string szDeviceKey, string szStatus, object statusData)
    {
        try
        {
            DeviceStatusChanged?.Invoke(this, new DeviceStatusChangedEventArgs(szDeviceKey, szStatus, statusData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "觸發設備狀態變更事件時發生錯誤");
        }
    }

    /// <summary>
    /// 釋放資源
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            Task.Run(async () =>
            {
                await StopAsync();
            }).Wait(TimeSpan.FromSeconds(10));
        }
    }
}

/// <summary>
/// 即時資料採集事件參數
/// </summary>
public class RealtimeDataCollectedEventArgs : EventArgs
{
    /// <summary>設定檔名（= Coordinator 名稱）；同 IP:Port 可能有多個設定檔，故不用 IP:Port</summary>
    public string DeviceKey { get; }
    public List<RealtimeDataModel> RealtimeDataList { get; }
    public DateTime Timestamp { get; } = DateTime.Now;

    public RealtimeDataCollectedEventArgs(string deviceKey, List<RealtimeDataModel> realtimeDataList)
    {
        DeviceKey = deviceKey;
        RealtimeDataList = realtimeDataList;
    }
}

/// <summary>
/// 設備狀態變更事件參數
/// </summary>
public class DeviceStatusChangedEventArgs : EventArgs
{
    /// <summary>設定檔名（= Coordinator 名稱）</summary>
    public string DeviceKey { get; }
    public string Status { get; }
    public object StatusData { get; }
    public DateTime Timestamp { get; } = DateTime.Now;

    public DeviceStatusChangedEventArgs(string deviceKey, string status, object statusData)
    {
        DeviceKey = deviceKey;
        Status = status;
        StatusData = statusData;
    }
}