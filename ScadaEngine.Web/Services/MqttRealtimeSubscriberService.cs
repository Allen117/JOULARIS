using System.Collections.Concurrent;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using ScadaEngine.Web.Features.Realtime.Models;
using ScadaEngine.Engine.Communication.Mqtt;
using ScadaEngine.Engine.Data.Interfaces;

namespace ScadaEngine.Web.Services;

/// <summary>
/// MQTT 即時資料訂閱服務 (Web專用)
/// </summary>
public class MqttRealtimeSubscriberService : BackgroundService, IDisposable
{
    private readonly ILogger<MqttRealtimeSubscriberService> _logger;
    private readonly MqttConfigService _mqttConfigService;
    private readonly IServiceProvider _serviceProvider;
    private IMqttClient? _mqttClient;
    private bool _isConnected = false;
    private bool _disposed = false;

    // 即時資料快取 (執行緒安全)
    private readonly ConcurrentDictionary<string, RealtimeDataItemModel> _realtimeDataCache = new();

    // LogicFlow TP 計時器狀態快取 (key: "treeId-nodeId")
    private readonly ConcurrentDictionary<string, TimerStateItem> _timerStateCache = new();

    private readonly LicenseStatusCache _licenseStatusCache;
    private readonly SmsStatusCache _smsStatusCache;
    private readonly NotifyReloadStatusCache _notifyReloadStatusCache;

    // 手動/自動模式快取 (key: SID/CID, value: isAuto)
    // 來源：ManualControlValue 表，由 RefreshManualAutoMapAsync 同步
    private readonly ConcurrentDictionary<string, bool> _manualAutoMap = new();

    // DB 來源刷新節流（by-sids 300ms 輪詢時擋住高頻直讀 DBLatestData）
    private DateTime _dtLastDbSourceRefresh = DateTime.MinValue;
    private static readonly TimeSpan DB_SOURCE_REFRESH_MIN_INTERVAL = TimeSpan.FromSeconds(1);

    // MQTT 即時資料主題 (格式: SCADA/Realtime/{coordinatorName}/{SID})
    private const string REALTIME_TOPIC = "SCADA/Realtime/+/+";
    private const string TIMER_STATE_TOPIC = "SCADA/LogicFlow/TimerState";
    private const string LICENSE_STATUS_TOPIC = "SCADA/Sys/License/Status";
    private const string SMS_STATUS_TOPIC = "SCADA/Sys/Sms/Status";
    private const string NOTIFY_RELOAD_STATUS_TOPIC = "SCADA/Sys/Notify/Status";

    // 資料更新事件
    public event Action<RealtimeDataItemModel>? DataUpdated;
    public event Action<List<RealtimeDataItemModel>>? AllDataUpdated;

    public MqttRealtimeSubscriberService(
        ILogger<MqttRealtimeSubscriberService> logger,
        MqttConfigService mqttConfigService,
        IServiceProvider serviceProvider,
        LicenseStatusCache licenseStatusCache,
        SmsStatusCache smsStatusCache,
        NotifyReloadStatusCache notifyReloadStatusCache)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mqttConfigService = mqttConfigService ?? throw new ArgumentNullException(nameof(mqttConfigService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _licenseStatusCache = licenseStatusCache ?? throw new ArgumentNullException(nameof(licenseStatusCache));
        _smsStatusCache = smsStatusCache ?? throw new ArgumentNullException(nameof(smsStatusCache));
        _notifyReloadStatusCache = notifyReloadStatusCache ?? throw new ArgumentNullException(nameof(notifyReloadStatusCache));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MQTT 即時資料訂閱服務啟動 (Web)");

        try
        {
            // 載入 MQTT 配置
            var mqttSetting = await _mqttConfigService.LoadConfigAsync();
            var mqttConfig = mqttSetting.MqttConfig;

            await InitializeMqttAsync(mqttConfig);

            // 從資料庫載入全部已設定點位，預填快取（無資料佔位）
            await InitializePointCacheAsync();

            // 持續監控連線狀態
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_mqttClient?.IsConnected != true)
                    {
                        _logger.LogWarning("MQTT 即時資料連線中斷，嘗試重新連線");
                        await ReconnectMqttAsync(mqttConfig);
                    }

                    // 定期清理過期資料
                    CleanupExpiredData();

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MQTT 即時資料服務監控迴圈發生錯誤");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT 即時資料服務執行時發生錯誤");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task InitializeMqttAsync(object mqttConfig)
    {
        try
        {
            dynamic config = mqttConfig;
            _mqttClient = new MqttFactory().CreateMqttClient();

            var clientId = $"ScadaWeb_Realtime_{Environment.ProcessId}";
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer((string)config.szBrokerIp, (int)config.nPort)
                .WithClientId(clientId)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                .Build();

            _mqttClient.ConnectedAsync += OnConnectedAsync;
            _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

            var result = await _mqttClient.ConnectAsync(options);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                _logger.LogInformation("MQTT 即時資料連線成功，ClientId: {ClientId}", clientId);
                await Task.Delay(500);
                await SubscribeToRealtimeTopicAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化 MQTT 即時資料連線時發生錯誤");
        }
    }

    private async Task SubscribeToRealtimeTopicAsync()
    {
        try
        {
            await _mqttClient!.SubscribeAsync(REALTIME_TOPIC);
            await _mqttClient!.SubscribeAsync(TIMER_STATE_TOPIC);
            await _mqttClient!.SubscribeAsync(LICENSE_STATUS_TOPIC);
            await _mqttClient!.SubscribeAsync(SMS_STATUS_TOPIC);
            await _mqttClient!.SubscribeAsync(NOTIFY_RELOAD_STATUS_TOPIC);
            _isConnected = true;
            _logger.LogInformation("已訂閱即時資料主題: {Topic}, {TimerTopic}, {LicenseTopic}, {SmsTopic}",
                REALTIME_TOPIC, TIMER_STATE_TOPIC, LICENSE_STATUS_TOPIC, SMS_STATUS_TOPIC);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "訂閱即時資料主題失敗");
        }
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        _logger.LogInformation("MQTT 即時資料已連線");
        if (!_isConnected)
        {
            await Task.Delay(500);
            await SubscribeToRealtimeTopicAsync();
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        _isConnected = false;
        _logger.LogWarning("MQTT 即時資料連線中斷: {Reason}", e.Reason);
        await Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var szTopic = e.ApplicationMessage.Topic;
            var szPayload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

            // 授權狀態
            if (szTopic == LICENSE_STATUS_TOPIC)
            {
                ParseLicenseStatusMessage(szPayload);
                return;
            }

            // 簡訊盒狀態（retained JSON 原樣快取，controller 直接透傳前端）
            if (szTopic == SMS_STATUS_TOPIC)
            {
                _smsStatusCache.Update(szPayload);
                return;
            }

            // 通知設定熱重載結果（Engine 回報；存檔請求以 requestId 等這一筆）
            if (szTopic == NOTIFY_RELOAD_STATUS_TOPIC)
            {
                _notifyReloadStatusCache.Update(szPayload);
                return;
            }

            // LogicFlow TP 計時器狀態
            if (szTopic == TIMER_STATE_TOPIC)
            {
                ParseTimerStateMessage(szPayload);
                return;
            }

            // 解析子主題
            var subTopic = szTopic.Replace("SCADA/Realtime/", "");

            // 解析 JSON 資料（以大小寫不分的字典處理 PascalCase / camelCase 混用情況）
            using var jsonDoc = JsonDocument.Parse(szPayload);
            var props = jsonDoc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

            // DB 來源點位不透過 MQTT 更新快取（Web 端定期直讀 DBLatestData，避免雙路徑互覆寫）
            var szParsedSid = props.TryGetValue("sid", out var sidPropEarly) ? sidPropEarly.GetString() ?? "" : "";
            if (szParsedSid.StartsWith("DB", StringComparison.Ordinal))
            {
                return;
            }

            // 除錯：記錄原始 JSON 資料
            _logger.LogInformation("收到MQTT原始資料: {Topic} -> {Payload}", szTopic, szPayload);

            // 解析 timestamp：優先嘗試 Unix 毫秒 (long)，次則 ISO 字串
            DateTime parsedTimestamp = DateTime.Now;
            if (props.TryGetValue("timestamp", out var tsProp))
            {
                if (tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt64(out var tsMs))
                    parsedTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime;
                else if (tsProp.ValueKind == JsonValueKind.String &&
                         DateTime.TryParse(tsProp.GetString(), out var tsDt))
                    parsedTimestamp = tsDt;
            }

            var dataItem = new RealtimeDataItemModel
            {
                szSubTopic = subTopic,
                szSID = props.TryGetValue("sid", out var sidProp) ? sidProp.GetString() ?? "" : "",
                szName = props.TryGetValue("name", out var nameProp) ? nameProp.GetString() ?? "" : subTopic,
                dValue = props.TryGetValue("value", out var valueProp) ?
                        (valueProp.ValueKind == JsonValueKind.Number ? valueProp.GetDouble() :
                         (double.TryParse(valueProp.GetString(), out var dblVal) ? dblVal : 0.0)) : 0.0,
                szQuality = props.TryGetValue("quality", out var qualityProp) ? qualityProp.GetString() ?? "UNKNOWN" : "UNKNOWN",
                dtTimestamp = parsedTimestamp,
                szUnit = props.TryGetValue("unit", out var unitProp) ? unitProp.GetString() ?? "" : "",
                hasData = true
            };

            // 更新快取 (以 SID 為 key，與 InitializePointCacheAsync 預填的 key 格式一致)
            _realtimeDataCache.AddOrUpdate(dataItem.szSID, dataItem, (key, oldValue) => dataItem);

            // 觸發事件
            DataUpdated?.Invoke(dataItem);

            _logger.LogDebug("收到即時資料: {SubTopic} = {Value} {Unit} (品質: {Quality})", 
                           subTopic, dataItem.dValue, dataItem.szUnit, dataItem.szQuality);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理即時資料訊息失敗: {Topic}", e.ApplicationMessage.Topic);
        }

        await Task.CompletedTask;
    }

    /// <summary>解析授權狀態 MQTT 訊息，更新 LicenseStatusCache</summary>
    private void ParseLicenseStatusMessage(string szPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(szPayload);
            var root = doc.RootElement;
            var isValid = root.TryGetProperty("valid", out var validEl) && validEl.GetBoolean();
            var checkedAt = root.TryGetProperty("checkedAt", out var dtEl) &&
                            DateTime.TryParse(dtEl.GetString(), out var dt) ? dt : DateTime.UtcNow;
            var reason = root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
            _licenseStatusCache.Update(isValid, checkedAt, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析授權狀態 MQTT 訊息失敗");
        }
    }

    /// <summary>解析 TP 計時器狀態 MQTT 訊息</summary>
    private void ParseTimerStateMessage(string szPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(szPayload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("timers", out var timersEl)) return;

            foreach (var prop in timersEl.EnumerateObject())
            {
                var item = new TimerStateItem
                {
                    Phase = prop.Value.TryGetProperty("phase", out var p) ? p.GetString() ?? "" : "",
                    PhaseEndMs = prop.Value.TryGetProperty("phaseEndMs", out var pe) ? pe.GetInt64() : 0,
                    HasHeld = prop.Value.TryGetProperty("hasHeld", out var hh) && hh.GetBoolean()
                };
                _timerStateCache[prop.Name] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析 TP 計時器狀態訊息失敗");
        }
    }

    private async Task ReconnectMqttAsync(object mqttConfig)
    {
        try
        {
            if (_mqttClient != null)
            {
                await _mqttClient.DisconnectAsync();
                await Task.Delay(2000);
                await InitializeMqttAsync(mqttConfig);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新連線 MQTT 即時資料時發生錯誤");
        }
    }

    private void CleanupExpiredData()
    {
        try
        {
            // 過期項目保留 hasData=true，僅更新品質標記為 STALE
            // SCADA 系統應始終顯示最後已知數值，由前端 isRecent / GetCssRowClass 處理視覺提示
            var staleItems = _realtimeDataCache
                .Where(kvp => kvp.Value.hasData
                            && kvp.Value.szQuality != "STALE"
                            && DateTime.Now.Subtract(kvp.Value.dtTimestamp).TotalMinutes > 5)
                .ToList();

            foreach (var kvp in staleItems)
            {
                kvp.Value.szQuality = "STALE";
            }

            if (staleItems.Any())
            {
                _logger.LogDebug("標記過期資料為 STALE（保留數值顯示）: {Count} 筆", staleItems.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理過期資料時發生錯誤");
        }
    }

    /// <summary>
    /// 從資料庫讀取所有已設定點位與最新數值，預填快取
    /// 有 LatestData 的點位立即顯示目前數值，其餘顯示為 NO_DATA
    /// </summary>
    private async Task InitializePointCacheAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDataRepository>();
            var allPoints = await repository.GetAllModbusPointsAsync();
            var pointList = allPoints.ToList();

            // 讀取 LatestData 表，取得所有點位的目前數值
            var latestDataList = await repository.GetLatestDataAsync(nLimit: 100000);
            var latestDataMap = latestDataList.ToDictionary(x => x.szSID, x => x);

            int nWithValue = 0;
            foreach (var point in pointList)
            {
                RealtimeDataItemModel item;
                if (latestDataMap.TryGetValue(point.szSID, out var latestData))
                {
                    // 有最新數值：填入實際資料，讓初次顯示即可看到目前數值
                    item = new RealtimeDataItemModel
                    {
                        szSubTopic = point.szSID,
                        szSID = point.szSID,
                        szName = point.szName,
                        szUnit = point.szUnit,
                        szQuality = latestData.nQuality == 1 ? "GOOD" : "BAD",
                        dValue = latestData.fValue,
                        dtTimestamp = latestData.dtTimestamp,
                        hasData = true
                    };
                    nWithValue++;
                }
                else
                {
                    // 無最新數值：佔位
                    item = new RealtimeDataItemModel
                    {
                        szSubTopic = point.szSID,
                        szSID = point.szSID,
                        szName = point.szName,
                        szUnit = point.szUnit,
                        szQuality = "NO_DATA",
                        dValue = 0,
                        dtTimestamp = DateTime.MinValue,
                        hasData = false
                    };
                }
                // TryAdd：若 MQTT 已先到資料則不覆蓋
                _realtimeDataCache.TryAdd(point.szSID, item);
            }

            // 預填計算點位
            var calcPoints = await repository.GetAllCalculatedPointsAsync();
            var calcList = calcPoints.Where(c => c.isEnabled).ToList();
            int nCalcWithValue = 0;
            foreach (var cp in calcList)
            {
                RealtimeDataItemModel item;
                if (latestDataMap.TryGetValue(cp.szSID, out var latestData))
                {
                    item = new RealtimeDataItemModel
                    {
                        szSubTopic = cp.szSID,
                        szSID = cp.szSID,
                        szName = cp.szName,
                        szUnit = cp.szUnit,
                        szQuality = latestData.nQuality == 1 ? "GOOD" : "BAD",
                        dValue = latestData.fValue,
                        dtTimestamp = latestData.dtTimestamp,
                        hasData = true
                    };
                    nCalcWithValue++;
                }
                else
                {
                    item = new RealtimeDataItemModel
                    {
                        szSubTopic = cp.szSID,
                        szSID = cp.szSID,
                        szName = cp.szName,
                        szUnit = cp.szUnit,
                        szQuality = "NO_DATA",
                        dValue = 0,
                        dtTimestamp = DateTime.MinValue,
                        hasData = false
                    };
                }
                _realtimeDataCache.TryAdd(cp.szSID, item);
            }

            _logger.LogInformation("預填 Modbus + 計算點位快取完成，共 {Count} 個點位（含 {CalcCount} 個計算點位），其中 {WithValue} 個有最新數值",
                pointList.Count + calcList.Count, calcList.Count, nWithValue + nCalcWithValue);

            // 預填 OPC UA 點位（即時值走 MQTT，與 Modbus 同路徑；先佔位讓 UI 開機即可見全部配置點位）
            await SyncOpcUaPointCacheAsync();

            // DB 來源走獨立資料路徑：直接從 DBLatestData 表讀取（不依賴 LatestData / MQTT）
            await RefreshDbSourcesAsync();

            // 初次載入手動/自動模式快取
            await RefreshManualAutoMapAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "預填點位快取時發生錯誤，將等待 MQTT 資料自動建立快取");
        }
    }

    /// <summary>
    /// 同步 OPC UA 點位快取：預填佔位（有 LatestData 用最新值）+ 剔除已刪除點位。
    /// 啟動時與 Web「OPC UA 來源」頁存檔後呼叫 — 新增點位立即出現在 UI、刪除點位立即消失。
    /// OPC UA 即時值走 MQTT 更新路徑（SID 前綴 OPC 不會被 DB 分流跳過）。
    /// </summary>
    public async Task SyncOpcUaPointCacheAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDataRepository>();
            var opcPoints = (await repository.GetAllOpcUaPointsAsync()).ToList();
            var configuredSids = new HashSet<string>(opcPoints.Select(p => p.szSID), StringComparer.Ordinal);

            // 剔除快取中已刪除的 OPC UA 點位
            foreach (var szCachedSid in _realtimeDataCache.Keys.Where(k => k.StartsWith("OPC", StringComparison.Ordinal)).ToList())
            {
                if (!configuredSids.Contains(szCachedSid))
                    _realtimeDataCache.TryRemove(szCachedSid, out _);
            }

            if (opcPoints.Count == 0) return;

            var latestDataList = await repository.GetLatestDataAsync(nLimit: 100000);
            var latestDataMap = latestDataList.ToDictionary(x => x.szSID, x => x);

            int nWithValue = 0;
            foreach (var point in opcPoints)
            {
                RealtimeDataItemModel item;
                if (latestDataMap.TryGetValue(point.szSID, out var latestData))
                {
                    item = new RealtimeDataItemModel
                    {
                        szSubTopic = point.szSID,
                        szSID = point.szSID,
                        szName = point.szName,
                        szUnit = point.szUnit,
                        szQuality = latestData.nQuality == 1 ? "GOOD" : "BAD",
                        dValue = latestData.fValue,
                        dtTimestamp = latestData.dtTimestamp,
                        hasData = true
                    };
                    nWithValue++;
                }
                else
                {
                    item = new RealtimeDataItemModel
                    {
                        szSubTopic = point.szSID,
                        szSID = point.szSID,
                        szName = point.szName,
                        szUnit = point.szUnit,
                        szQuality = "NO_DATA",
                        dValue = 0,
                        dtTimestamp = DateTime.MinValue,
                        hasData = false
                    };
                }
                // TryAdd：若 MQTT 已先到資料則不覆蓋
                _realtimeDataCache.TryAdd(point.szSID, item);
            }

            _logger.LogInformation("同步 OPC UA 點位快取完成，共 {Count} 個點位，其中 {WithValue} 個有最新數值",
                opcPoints.Count, nWithValue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "同步 OPC UA 點位快取時發生錯誤");
        }
    }

    /// <summary>
    /// 重新從 ManualControlValue 表讀取手動/自動旗標，更新到快取。
    /// 由 /api/realtime/latest 每秒呼叫一次（與 RefreshDbSourcesAsync 同節奏），讓跨分頁切換能在 ≤1 秒內同步。
    /// 表內無紀錄者代表「未進入手動模式」，在快取中以「不存在」表達。
    /// </summary>
    public async Task RefreshManualAutoMapAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDataRepository>();
            var dict = await repository.LoadManualControlValuesAsync();

            // 寫入新值
            foreach (var kv in dict)
            {
                _manualAutoMap[kv.Key] = kv.Value.isAuto;
            }
            // 移除 DB 已不存在的 key（防止資料庫被外部清理後快取殘留）
            var toRemove = _manualAutoMap.Keys.Where(k => !dict.ContainsKey(k)).ToList();
            foreach (var k in toRemove) _manualAutoMap.TryRemove(k, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新手動/自動模式快取時發生錯誤");
        }
    }

    /// <summary>
    /// 取得手動/自動模式快取快照。
    /// Key 為 SID/CID（兩者在 Modbus 點位是同一字串）。
    /// Value 為 isAuto：true=自動模式、false=手動模式。
    /// Key 不存在於回傳字典 = 該點位「沒有手動控制紀錄」（未曾切過手動）。
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetManualAutoMap()
    {
        return _manualAutoMap;
    }

    /// <summary>
    /// 寫入路徑通知：ControlController 寫完 ManualControlValue 後立即同步本機快取，
    /// 讓「自己這個分頁」optimistic update 後不必等下一次 polling 才正確。
    /// 其他分頁仍走 RefreshManualAutoMapAsync 的下一個 1 秒週期同步。
    /// </summary>
    public void UpdateManualAutoFlag(string szSid, bool isAuto)
    {
        if (string.IsNullOrWhiteSpace(szSid)) return;
        _manualAutoMap[szSid] = isAuto;
    }

    /// <summary>
    /// 節流版 DB 來源刷新：距上次刷新不足 1 秒直接跳過。
    /// 供高頻輪詢的呼叫端（/api/realtime/by-sids，LogicFlow 畫布 300ms）使用 —
    /// DB 來源點位不透過 MQTT 更新快取，不刷新的話 by-sids 只會回啟動時的預填快照。
    /// </summary>
    public async Task RefreshDbSourcesThrottledAsync()
    {
        if (DateTime.Now - _dtLastDbSourceRefresh < DB_SOURCE_REFRESH_MIN_INTERVAL) return;
        _dtLastDbSourceRefresh = DateTime.Now;
        await RefreshDbSourcesAsync();
    }

    /// <summary>
    /// 重新從 DBLatestData 表讀取所有 DB 來源點位，更新到快取。
    /// 品質規則（與 Engine DbCommunicationService 一致）：
    ///   SQL 查詢丟 exception → 全部 BAD；
    ///   讀成功 → 該列 Quality 明確為 0 才 BAD，其餘（1 / NULL / 無列）GOOD。
    /// </summary>
    public async Task RefreshDbSourcesAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDataRepository>();

            var dbPoints = (await repository.GetAllDbPointsAsync()).ToList();
            if (dbPoints.Count == 0) return;

            // 嘗試讀 DBLatestData；只要 SQL 不丟 exception 就視為 GOOD
            bool isSqlOk;
            Dictionary<string, ScadaEngine.Common.Data.Models.LatestDataModel> dbLatestMap;
            try
            {
                var rows = await repository.GetDbLatestDataByPrefixAsync("DB");
                dbLatestMap = rows.ToDictionary(x => x.szSID, x => x);
                isSqlOk = true;
            }
            catch (Exception sqlEx)
            {
                _logger.LogWarning(sqlEx, "讀取 DBLatestData 失敗，將所有 DB 來源點位標記為 BAD");
                dbLatestMap = new Dictionary<string, ScadaEngine.Common.Data.Models.LatestDataModel>();
                isSqlOk = false;
            }

            foreach (var dp in dbPoints)
            {
                var hasRow = dbLatestMap.TryGetValue(dp.szSID, out var row);
                // 寫入端明確寫 Quality=0（如氣象抓取失敗）才標 BAD，讓畫面與 Engine / 下游計算點一致
                var isRowBad = hasRow && row!.nQuality == 0;
                var item = new RealtimeDataItemModel
                {
                    szSubTopic = dp.szSID,
                    szSID = dp.szSID,
                    szName = dp.szName,
                    szUnit = dp.szUnit ?? string.Empty,
                    szQuality = isSqlOk && !isRowBad ? "GOOD" : "BAD",
                    dValue = hasRow ? row!.fValue : 0,
                    dtTimestamp = hasRow ? row!.dtTimestamp : DateTime.MinValue,
                    hasData = isSqlOk,
                    // DB 來源以 SQL 讀取成功代表通訊正常，不依時間戳判斷新舊
                    isFreshBypass = isSqlOk
                };
                _realtimeDataCache[dp.szSID] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新 DB 來源點位時發生未預期錯誤");
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_mqttClient != null)
            {
                if (_mqttClient.IsConnected)
                {
                    await _mqttClient.DisconnectAsync();
                }
                _mqttClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理 MQTT 即時資料連線時發生錯誤");
        }
    }

    /// <summary>
    /// 取得所有即時資料
    /// </summary>
    public List<RealtimeDataItemModel> GetAllRealtimeData()
    {
        return _realtimeDataCache.Values.OrderBy(x => x.szSubTopic).ToList();
    }

    /// <summary>
    /// 按 SID 集合取得即時資料（輕量查詢，避免回傳全部）
    /// </summary>
    public List<RealtimeDataItemModel> GetRealtimeDataBySids(IEnumerable<string> sids)
    {
        var result = new List<RealtimeDataItemModel>();
        foreach (var sid in sids)
        {
            if (_realtimeDataCache.TryGetValue(sid, out var item))
                result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// 取得連線狀態
    /// </summary>
    public bool IsConnected => _isConnected && _mqttClient?.IsConnected == true;

    /// <summary>
    /// 取得快取統計
    /// </summary>
    public (int total, int active) GetDataStatistics()
    {
        var total = _realtimeDataCache.Count;
        var active = _realtimeDataCache.Count(kvp => kvp.Value.isRecent);
        return (total, active);
    }

    /// <summary>
    /// 發布控制指令到 MQTT (供 ControlController 呼叫)
    /// Topic: SCADA/Control/{CID}
    /// </summary>
    public async Task<bool> PublishControlCommandAsync(string szCid, double dValue)
    {
        if (_mqttClient?.IsConnected != true)
        {
            _logger.LogWarning("MQTT 未連線，無法發布控制指令 CID={Cid}", szCid);
            return false;
        }

        try
        {
            var szTopic = $"SCADA/Control/{szCid}";
            var payload = JsonSerializer.Serialize(new
            {
                mid = Guid.NewGuid().ToString(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                value = dValue.ToString(),
                unit = ""
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(szTopic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(message);
            _logger.LogInformation("已發布控制指令: Topic={Topic}, Payload={Payload}", szTopic, payload);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發布控制指令失敗: CID={Cid}", szCid);
            return false;
        }
    }

    /// <summary>
    /// 取得指定 TreeId 的所有 TP 計時器狀態
    /// </summary>
    public Dictionary<string, TimerStateItem> GetTimerStates(int nTreeId)
    {
        var szPrefix = $"{nTreeId}-";
        return _timerStateCache
            .Where(kv => kv.Key.StartsWith(szPrefix))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// 取得所有 TP 計時器狀態
    /// </summary>
    public Dictionary<string, TimerStateItem> GetAllTimerStates()
    {
        return _timerStateCache.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public new void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient?.Dispose();
            _disposed = true;
        }
        base.Dispose();
    }
}

/// <summary>TP 計時器狀態項目</summary>
public class TimerStateItem
{
    public string Phase { get; set; } = "";
    public long PhaseEndMs { get; set; }
    public bool HasHeld { get; set; }
}