using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Communication.Mqtt;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Communication.Modbus.Services;

/// <summary>
/// Modbus 批量讀取分組
/// </summary>
public class ModbusBatchGroup
{
    /// <summary>
    /// Modbus 功能碼
    /// </summary>
    public byte nFunctionCode { get; set; }

    /// <summary>
    /// 批次起始地址
    /// </summary>
    public ushort nStartAddress { get; set; }

    /// <summary>
    /// 批次暫存器數量
    /// </summary>
    public ushort nRegisterCount { get; set; }

    /// <summary>
    /// 此批次包含的點位清單
    /// </summary>
    public List<ModbusTagModel> tagList { get; set; } = new();
}

/// <summary>
/// Modbus 通訊服務，負責與單個 Modbus 設備（一個設定檔 = 一條 TCP，可掛多個 ModbusID）的連線與資料讀取。
/// <para>
/// 錯誤分三類（多 ID 單站斷線隔離，規則移植 iAUTO tgCoordinator.vb）：
/// <list type="bullet">
/// <item><b>站層</b>（逾時 / 例外碼 / 框對不上）：同段重送 RetryCount 次 → 仍失敗就跳過該站剩餘批次、換下一站，TCP 不斷</item>
/// <item><b>連線層</b>（SocketException / IOException / 遠端關閉）：才標記斷線、關 TCP，等 ShouldReconnect 重連</item>
/// <item><b>其他</b>（解碼錯等）：只記 log</item>
/// </list>
/// Bad 寬限期 per-ModbusID：該站首次失敗起超過 20 × ConnectTimeout 才把「它自己的」點位標 Bad；
/// 連線層斷線時所有站號共用同一時間點（維持整台斷線行為）。
/// 整台連續 NoResponseReconnectRounds 輪所有站都沒回 → 主動斷 TCP 重連（Gateway 卡死保險）。
/// </para>
/// <para>
/// 已知死站降級探測：某站判定不可達後，平時跳過不問（不佔用其他站的輪詢時間），每 UnreachableProbeIntervalMs
/// 探測一次且只問一次、不重送；有回應即恢復正常輪詢。所有站都不可達時不跳過（沒有健康站要保護，也讓整台重連保險照常計數）。
/// </para>
/// </summary>
public class ModbusCommunicationService : IDisposable
{
    private readonly ModbusDeviceConfigModel _deviceConfig;
    private readonly ILogger<ModbusCommunicationService> _logger;
    private readonly MqttPublishService? _mqttPublishService;
    private ModbusTcpTransport? _transport;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private bool _isDisposed = false;
    private DateTime _dtLastSuccessfulRead = DateTime.MinValue;
    private DateTime _dtLastReconnectAttempt = DateTime.MinValue;

    /// <summary>
    /// 各 ModbusID 首次失敗時間（無回應或連線斷線）；該站有回應即移除
    /// </summary>
    private readonly Dictionary<byte, DateTime> _dtFirstFailureByStation = new();

    /// <summary>
    /// 已記過「區塊讀取失敗」log 的 (站號, 功能碼, 起始位址)；恢復時移除並記一次，避免每輪灌 log
    /// </summary>
    private readonly HashSet<(byte nModbusId, byte nFunctionCode, ushort nStartAddress)> _failedSegmentKeys = new();

    /// <summary>
    /// 已判定不可達的站號 → 下次探測時間。站有回應即移除（恢復正常輪詢）
    /// </summary>
    private readonly Dictionary<byte, DateTime> _dtNextProbeByUnreachableStation = new();

    /// <summary>
    /// 連續「所有站都沒回」的輪數
    /// </summary>
    private int _nNoResponseRounds = 0;

    /// <summary>
    /// 取得重連延遲時間（connectTimeout 的 20 倍），亦為單站 Bad 寬限期
    /// </summary>
    private TimeSpan ReconnectDelay
    {
        get
        {
            var delayMs = _deviceConfig.nConnectTimeout * 20;
            return TimeSpan.FromMilliseconds(delayMs);
        }
    }

    /// <summary>
    /// 上一次發布的數值快取，用於偵測值變化 (SID -> Value)
    /// </summary>
    private readonly ConcurrentDictionary<string, float> _lastPublishedValues = new();

    /// <summary>
    /// 上一次發布的品質狀態快取，用於偵測品質變化 (SID -> Quality)
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastPublishedQuality = new();

    /// <summary>
    /// 上一次發布的時間快取，用於定期重新發布未變化的資料點 (SID -> DateTime)
    /// 避免 Web 端因長時間未收到更新而標記為 STALE
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastPublishedTime = new();

    /// <summary>
    /// 定期重新發布的間隔（分鐘），需小於 Web 端 STALE 門檻（5 分鐘）
    /// </summary>
    private const int REPUBLISH_INTERVAL_MINUTES = 3;

    /// <summary>
    /// 批次讀取結果
    /// </summary>
    private enum BatchOutcome
    {
        /// <summary>讀到資料</summary>
        Success,
        /// <summary>站有回但回例外碼（非法位址等），該區塊無資料</summary>
        StationException,
        /// <summary>站不可達（重送後仍逾時 / Gateway 回 0x0A、0x0B）</summary>
        StationUnreachable,
        /// <summary>連線層錯誤或未連線</summary>
        ConnectionLost,
        /// <summary>未讀（空批次 / 不支援功能碼 / 解碼錯）</summary>
        Skipped
    }

    /// <summary>
    /// 設備連線狀態
    /// </summary>
    public bool IsConnected { get; private set; } = false;

    /// <summary>
    /// 設備 IP 地址
    /// </summary>
    public string DeviceIP => _deviceConfig.szIP;

    /// <summary>
    /// 設備埠號
    /// </summary>
    public int DevicePort => _deviceConfig.nPort;

    /// <summary>
    /// 點位數量
    /// </summary>
    public int TagCount => _deviceConfig.tagList.Count;

    public ModbusCommunicationService(ModbusDeviceConfigModel deviceConfig, ILogger<ModbusCommunicationService> logger, MqttPublishService? mqttPublishService = null)
    {
        _deviceConfig = deviceConfig ?? throw new ArgumentNullException(nameof(deviceConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mqttPublishService = mqttPublishService;
    }

    /// <summary>
    /// 建立 Modbus 連線（記錄重連嘗試時間）
    /// </summary>
    /// <returns>連線成功回傳 true</returns>
    public async Task<bool> ConnectAsync()
    {
        await _connectionSemaphore.WaitAsync();
        try
        {
            if (_isDisposed)
                return false;

            _dtLastReconnectAttempt = DateTime.Now;
            _logger.LogInformation("嘗試連線 Modbus: {IP}:{Port}, 嘗試時間={AttemptTime}",
                                 _deviceConfig.szIP, _deviceConfig.nPort, _dtLastReconnectAttempt.ToString("HH:mm:ss.fff"));

            // 如果已連線，先斷開
            DisconnectInternal();

            try
            {
                if (!IPAddress.TryParse(_deviceConfig.szIP, out var ipAddress))
                {
                    _logger.LogError("無效的 IP 地址: {IP}", _deviceConfig.szIP);
                    return false;
                }

                var transport = new ModbusTcpTransport { nResponseTimeoutMs = _deviceConfig.nConnectTimeout };
                transport.Connect(new IPEndPoint(ipAddress, _deviceConfig.nPort), _deviceConfig.nConnectTimeout);
                _transport = transport;

                IsConnected = true;
                _dtLastSuccessfulRead = DateTime.Now;
                _nNoResponseRounds = 0;

                _logger.LogInformation("Modbus 連線建立成功: {IP}:{Port}, 成功時間={SuccessTime}",
                                     _deviceConfig.szIP, _deviceConfig.nPort, _dtLastSuccessfulRead.ToString("HH:mm:ss.fff"));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Modbus 連線建立失敗: {IP}:{Port}, 嘗試時間={AttemptTime}",
                               _deviceConfig.szIP, _deviceConfig.nPort, _dtLastReconnectAttempt.ToString("HH:mm:ss.fff"));

                DisconnectInternal();
                RecordConnectionFailure();
                return false;
            }
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// 斷開 Modbus 連線
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _connectionSemaphore.WaitAsync();
        try
        {
            DisconnectInternal();
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// 內部斷線實作 (不加鎖)
    /// </summary>
    private void DisconnectInternal()
    {
        if (_transport != null)
        {
            try
            {
                _transport.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "斷開 Modbus 連線時發生錯誤");
            }
            finally
            {
                _transport = null;
            }
        }
        IsConnected = false;
    }

    /// <summary>
    /// 讀取所有點位資料並發布至 MQTT — 循序處理每個 ModbusID（共用同一條 TCP）
    /// </summary>
    /// <returns>即時資料清單</returns>
    public async Task<List<RealtimeDataModel>> ReadAllTagsAsync()
    {
        var resultList = new List<RealtimeDataModel>();

        try
        {
            var modbusIds = _deviceConfig.GetModbusIdArray();
            var isConnectedAtStart = IsConnected;
            var nAttemptedStations = 0;
            var nRespondedStations = 0;

            // 每輪開頭清一次殘框（上一輪逾時後遲到的回應），讓串流重新對齊
            if (IsConnected)
                _transport?.Drain();

            // 循序處理每個 ModbusId（Modbus TCP 為 request-response 協定，共用同一個 TCP 連線不可並行）
            for (int index = 0; index < modbusIds.Length; index++)
            {
                var nModbusId = modbusIds[index];
                try
                {
                    var isConnectedBefore = IsConnected;
                    var (tagDataList, isResponded, isAttempted) = await ReadTagsForModbusIdAsync(nModbusId);

                    // 死站探測間隔內跳過不問的站不算「嘗試」（不計入整台無回應輪數）
                    if (isConnectedBefore && isAttempted)
                    {
                        nAttemptedStations++;
                        if (isResponded) nRespondedStations++;
                    }

                    if (tagDataList.Count > 0)
                    {
                        // 偵測值變化並發布至 MQTT Broker
                        if (_mqttPublishService != null && _mqttPublishService.IsConnected)
                        {
                            var changedDataList = DetectValueChanges(tagDataList);

                            if (changedDataList.Count > 0)
                            {
                                var nPublishedCount = await _mqttPublishService.PublishBatchRealtimeDataAsync(changedDataList);
                                _logger.LogDebug("ModbusId {ModbusId} 已發布 {PublishedCount}/{ChangeCount} 筆變化資料至 MQTT (原始總數: {TotalCount})",
                                               nModbusId, nPublishedCount, changedDataList.Count, tagDataList.Count);
                            }
                        }

                        resultList.AddRange(tagDataList);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "輪詢 ModbusId {ModbusId} 時發生錯誤", nModbusId);
                }
            }

            // 只統計真正成功讀取的資料（排除 Bad Quality）
            var nSuccessCount = resultList.Count(r => r.IsReadSuccess);
            if (nSuccessCount > 0)
                _dtLastSuccessfulRead = DateTime.Now;

            // 整台 Coordinator 連續 K 輪「所有站都沒回」→ 主動斷 TCP 重連（Gateway 卡死的保險）
            if (isConnectedAtStart && IsConnected && nAttemptedStations > 0)
            {
                if (nRespondedStations == 0)
                {
                    _nNoResponseRounds++;
                    if (_nNoResponseRounds >= Math.Max(1, _deviceConfig.nNoResponseReconnectRounds))
                    {
                        _logger.LogWarning("Modbus {IP}:{Port} 連續 {Rounds} 輪所有站號皆無回應，主動斷線重連",
                                           _deviceConfig.szIP, _deviceConfig.nPort, _nNoResponseRounds);
                        _nNoResponseRounds = 0;
                        DisconnectInternal();
                    }
                }
                else
                {
                    _nNoResponseRounds = 0;
                }
            }

            _logger.LogTrace("完成循序輪詢所有 ModbusId，總計讀取 {Count} 個點位資料", resultList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取 Modbus 資料時發生錯誤: {IP}", _deviceConfig.szIP);
            MarkConnectionLost();
        }

        return resultList;
    }

    /// <summary>
    /// 讀取指定 ModbusId 的所有點位資料 — 智慧批量讀取（循序，批次間加延遲）。
    /// 站不可達時跳過該站剩餘批次（避免一台死站一輪吃掉 批次數 × 重送 × 逾時），寬限期後只把該站點位標 Bad。
    /// </summary>
    /// <param name="nModbusId">Modbus 站號</param>
    /// <returns>(即時資料清單, 該站本輪是否有回應, 本輪是否有實際送出請求)</returns>
    private async Task<(List<RealtimeDataModel> resultList, bool isResponded, bool isAttempted)> ReadTagsForModbusIdAsync(byte nModbusId)
    {
        var resultList = new List<RealtimeDataModel>();
        var isResponded = false;
        var isAttempted = false;

        try
        {
            // 首先驗證所有點位是否正確解析
            var validTags = new List<ModbusTagModel>();
            foreach (var tag in _deviceConfig.tagList)
            {
                if (tag.ParseAddress() && tag.ParseRatioAndRegisterCount())
                {
                    validTags.Add(tag);
                }
                else
                {
                    _logger.LogWarning("點位 {TagName} 驗證失敗: 地址={Address}", tag.szName, tag.szAddress);
                }
            }

            if (validTags.Count == 0)
            {
                _logger.LogWarning("ModbusId {ModbusId} 沒有有效的點位配置", nModbusId);
                return (resultList, isResponded, isAttempted);
            }

            var batchGroups = OptimizeBatchReads(validTags, nModbusId);

            // 已知死站、未到探測時間 → 這輪不問；寬限期後照常產生 Bad（品質維持 Bad、定期重發不中斷）
            if (IsConnected && ShouldSkipUnreachableStation(nModbusId))
            {
                RecordStationFailure(nModbusId);
                if (IsStationFailureTimeout(nModbusId))
                {
                    foreach (var batchGroup in batchGroups)
                        resultList.AddRange(BuildBadQualityData(batchGroup, nModbusId));
                }
                return (resultList, isResponded, isAttempted);
            }

            var unreadBatches = new List<ModbusBatchGroup>();
            var isUnreachable = false;
            var nSuccessBatches = 0;

            for (int nBatchIndex = 0; nBatchIndex < batchGroups.Count; nBatchIndex++)
            {
                var batchGroup = batchGroups[nBatchIndex];

                // 站已判定不可達 / 連線已斷 → 剩餘批次不讀
                if (isUnreachable || !IsConnected)
                {
                    unreadBatches.Add(batchGroup);
                    continue;
                }

                isAttempted = true;
                var outcome = ReadBatchGroup(batchGroup, nModbusId, resultList);
                switch (outcome)
                {
                    case BatchOutcome.Success:
                        isResponded = true;
                        nSuccessBatches++;
                        break;
                    case BatchOutcome.StationException:
                        isResponded = true;
                        break;
                    case BatchOutcome.StationUnreachable:
                        isUnreachable = true;
                        unreadBatches.Add(batchGroup);
                        break;
                    case BatchOutcome.ConnectionLost:
                        unreadBatches.Add(batchGroup);
                        break;
                }

                // 批次間延遲 20ms，讓 Modbus 設備有時間處理
                if (nBatchIndex < batchGroups.Count - 1 && !isUnreachable && IsConnected)
                {
                    await Task.Delay(20);
                }
            }

            if (isResponded)
            {
                // 站有回應 → 該站存活，清除失敗計時、恢復正常輪詢
                ClearStationFailure(nModbusId);
                ClearUnreachableStation(nModbusId);
            }
            else if (unreadBatches.Count > 0)
            {
                // 判定不可達 → 降級為定期探測（連線斷導致的未讀不算，那是整台的事）
                if (isUnreachable)
                    MarkUnreachableStation(nModbusId);

                // 站沒回（或連線斷）→ 寬限期後只把這一站的點位標 Bad
                RecordStationFailure(nModbusId);
                if (IsStationFailureTimeout(nModbusId))
                {
                    foreach (var batchGroup in unreadBatches)
                        resultList.AddRange(BuildBadQualityData(batchGroup, nModbusId));
                }
            }

            // 站有回但所有批次都回例外碼（例如某段位址非法拖累整批）→ 回退到單點位讀取
            if (isResponded && nSuccessBatches == 0 && !isUnreachable && IsConnected)
            {
                _logger.LogDebug("ModbusId {ModbusId} 批量讀取皆回例外碼，回退到單點位讀取模式", nModbusId);
                ReadSingleTagsFallback(validTags, nModbusId, resultList);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取 ModbusId {ModbusId} 時發生未預期錯誤", nModbusId);
        }

        return (resultList, isResponded, isAttempted);
    }

    /// <summary>
    /// 智慧批量讀取：將連續地址分組成最多100暫存器的批次
    /// </summary>
    /// <param name="tagList">點位清單</param>
    /// <param name="nModbusId">Modbus 站號</param>
    /// <returns>批量讀取分組</returns>
    private List<ModbusBatchGroup> OptimizeBatchReads(List<ModbusTagModel> tagList, byte nModbusId)
    {
        var batchGroups = new List<ModbusBatchGroup>();
        _logger.LogTrace("開始為 ModbusId {ModbusId} 優化批量讀取，總計 {TagCount} 個點位", nModbusId, tagList.Count);

        // 按功能碼分組
        var functionGroups = tagList.GroupBy(tag => tag.nFunctionCode);

        foreach (var funcGroup in functionGroups)
        {
            // 按地址排序
            var sortedTags = funcGroup.OrderBy(tag => tag.nParsedAddress).ToList();

            // 分割成多個批次，每批次最多100個暫存器
            var nCurrentBatchStart = -1;
            var nCurrentBatchEnd = -1;
            var currentBatchTags = new List<ModbusTagModel>();

            for (int i = 0; i < sortedTags.Count; i++)
            {
                var tag = sortedTags[i];
                var nTagStartAddress = tag.nParsedAddress;
                var nTagEndAddress = tag.nParsedAddress + tag.nRegisterCount - 1;

                if (nCurrentBatchStart == -1)
                {
                    // 第一個點位，開始新批次
                    nCurrentBatchStart = nTagStartAddress;
                    nCurrentBatchEnd = nTagEndAddress;
                    currentBatchTags.Add(tag);
                }
                else
                {
                    // 計算如果加入這個點位後的批次總長度
                    var nNewBatchEnd = Math.Max(nCurrentBatchEnd, nTagEndAddress);
                    var nNewBatchLength = nNewBatchEnd - nCurrentBatchStart + 1;

                    // 檢查是否超過100暫存器限制
                    if (nNewBatchLength <= 100)
                    {
                        nCurrentBatchEnd = nNewBatchEnd;
                        currentBatchTags.Add(tag);
                    }
                    else
                    {
                        // 超過限制，完成當前批次並開始新批次
                        var nCurrentBatchLength = nCurrentBatchEnd - nCurrentBatchStart + 1;

                        batchGroups.Add(new ModbusBatchGroup
                        {
                            nFunctionCode = funcGroup.Key,
                            nStartAddress = (ushort)nCurrentBatchStart,
                            nRegisterCount = (ushort)nCurrentBatchLength,
                            tagList = new List<ModbusTagModel>(currentBatchTags)
                        });

                        nCurrentBatchStart = nTagStartAddress;
                        nCurrentBatchEnd = nTagEndAddress;
                        currentBatchTags.Clear();
                        currentBatchTags.Add(tag);
                    }
                }
            }

            // 處理最後一個批次 (真正的最後批次才加3個額外暫存器)
            if (currentBatchTags.Count > 0)
            {
                var nFinalBatchLength = nCurrentBatchEnd - nCurrentBatchStart + 1;

                // 只在處理最後一個功能碼組的最後批次時才加3個額外暫存器
                var isLastFunctionGroup = funcGroup.Key == functionGroups.Max(g => g.Key);
                if (isLastFunctionGroup)
                {
                    if (funcGroup.Key == 3 || funcGroup.Key == 4) // 只對保持寄存器和輸入寄存器加3
                    {
                        nFinalBatchLength += 3;
                    }
                }

                batchGroups.Add(new ModbusBatchGroup
                {
                    nFunctionCode = funcGroup.Key,
                    nStartAddress = (ushort)nCurrentBatchStart,
                    nRegisterCount = (ushort)nFinalBatchLength,
                    tagList = new List<ModbusTagModel>(currentBatchTags)
                });
            }
        }

        return batchGroups;
    }

    /// <summary>
    /// 讀取批次組資料並分配給各個點位（結果附加到 resultList）
    /// </summary>
    private BatchOutcome ReadBatchGroup(ModbusBatchGroup batchGroup, byte nModbusId, List<RealtimeDataModel> resultList)
    {
        if (batchGroup.tagList.Count == 0)
            return BatchOutcome.Skipped;

        if (batchGroup.nFunctionCode is < 1 or > 4)
        {
            _logger.LogWarning("不支援的功能碼批量讀取: {FunctionCode}", batchGroup.nFunctionCode);
            return BatchOutcome.Skipped;
        }

        if (!IsConnected || _transport == null)
            return BatchOutcome.ConnectionLost;

        var segmentKey = (nModbusId, batchGroup.nFunctionCode, batchGroup.nStartAddress);

        try
        {
            var data = ReadWithRetry(nModbusId, batchGroup.nFunctionCode, batchGroup.nStartAddress, batchGroup.nRegisterCount);
            var batchRawData = batchGroup.nFunctionCode is 1 or 2
                ? ConvertBitsToUshorts(data, batchGroup.nRegisterCount)
                : ConvertBytesToUshorts(data, batchGroup.nRegisterCount);

            if (_failedSegmentKeys.Remove(segmentKey))
            {
                _logger.LogInformation("Modbus {IP}:{Port} ModbusID {ModbusId} 區塊讀取恢復 (FC{FC}, 起始位址 {StartAddress})",
                                       _deviceConfig.szIP, _deviceConfig.nPort, nModbusId, batchGroup.nFunctionCode, batchGroup.nStartAddress);
            }

            // 將批量讀取的資料分配給各個點位
            foreach (var tag in batchGroup.tagList)
            {
                var nGlobalTagIndex = _deviceConfig.tagList.FindIndex(t => t.szName == tag.szName);
                if (nGlobalTagIndex == -1)
                {
                    _logger.LogWarning("找不到點位 {TagName} 的全域索引", tag.szName);
                    nGlobalTagIndex = 0;
                }

                var nRelativeOffset = tag.nParsedAddress - batchGroup.nStartAddress;

                if (nRelativeOffset >= 0 && nRelativeOffset + tag.nRegisterCount <= batchRawData.Length)
                {
                    var tagRawData = new ushort[tag.nRegisterCount];
                    Array.Copy(batchRawData, nRelativeOffset, tagRawData, 0, tag.nRegisterCount);

                    var realtimeData = CreateRealtimeDataFromTag(tag, tagRawData, nModbusId, nGlobalTagIndex);
                    if (realtimeData != null)
                    {
                        resultList.Add(realtimeData);
                    }
                    else
                    {
                        _logger.LogWarning("點位 {TagName} 建立即時資料失敗", tag.szName);
                    }
                }
                else
                {
                    _logger.LogError("點位 {TagName} 的地址範圍超出批次資料: 相對偏移={RelativeOffset}, 暫存器數量={RegisterCount}, 批次大小={BatchSize}",
                                     tag.szName, nRelativeOffset, tag.nRegisterCount, batchRawData.Length);
                }
            }

            return BatchOutcome.Success;
        }
        catch (ModbusStationException ex)
        {
            if (_failedSegmentKeys.Add(segmentKey))
            {
                _logger.LogWarning("Modbus {IP}:{Port} ModbusID {ModbusId} 區塊讀取失敗 (FC{FC}, 起始位址 {StartAddress}, 長度 {Count}, 重送 {Retry} 次): {Reason}。恢復前不再重複記錄",
                                   _deviceConfig.szIP, _deviceConfig.nPort, nModbusId, batchGroup.nFunctionCode,
                                   batchGroup.nStartAddress, batchGroup.nRegisterCount, _deviceConfig.nRetryCount, ex.Message);
            }
            return ex.IsStationUnreachable ? BatchOutcome.StationUnreachable : BatchOutcome.StationException;
        }
        catch (Exception ex) when (IsConnectionLevelError(ex))
        {
            _logger.LogError(ex, "Modbus {IP}:{Port} 連線中斷（ModbusID {ModbusId}, FC{FC}, 起始位址 {StartAddress}）",
                             _deviceConfig.szIP, _deviceConfig.nPort, nModbusId, batchGroup.nFunctionCode, batchGroup.nStartAddress);
            MarkConnectionLost();
            return BatchOutcome.ConnectionLost;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取批次組資料時發生非連線錯誤（下次輪詢將重試）: ModbusID={ModbusId}, FunctionCode={FunctionCode}, StartAddress={StartAddress}, Count={RegisterCount}",
                               nModbusId, batchGroup.nFunctionCode, batchGroup.nStartAddress, batchGroup.nRegisterCount);
            return BatchOutcome.Skipped;
        }
    }

    /// <summary>
    /// 讀一段資料；站層失敗（逾時 / 例外碼）先清殘框再同段重送 RetryCount 次，仍失敗才丟出
    /// </summary>
    private byte[] ReadWithRetry(byte nModbusId, byte nFunctionCode, ushort nStartAddress, ushort nCount)
    {
        // 已知死站（探測中）只問一次、不重送
        var nRetryCount = _dtNextProbeByUnreachableStation.ContainsKey(nModbusId) ? 0 : Math.Clamp(_deviceConfig.nRetryCount, 0, 5);
        for (int nAttempt = 0; ; nAttempt++)
        {
            var transport = _transport ?? throw new IOException("Modbus TCP 未連線");
            try
            {
                return transport.Read(nModbusId, nFunctionCode, nStartAddress, nCount);
            }
            catch (ModbusStationException)
            {
                // 清掉遲到 / 無主殘框再重送，避免害到下一筆
                transport.Drain();
                if (nAttempt >= nRetryCount)
                    throw;
            }
        }
    }

    /// <summary>
    /// 單點位讀取回退：每點一筆請求、不重送；站不可達或連線斷即中止
    /// </summary>
    private void ReadSingleTagsFallback(List<ModbusTagModel> validTags, byte nModbusId, List<RealtimeDataModel> resultList)
    {
        for (int i = 0; i < validTags.Count; i++)
        {
            var tag = validTags[i];
            var nGlobalTagIndex = _deviceConfig.tagList.FindIndex(t => t.szName == tag.szName);
            if (nGlobalTagIndex == -1) nGlobalTagIndex = i;

            if (!IsConnected || _transport == null)
                return;

            try
            {
                var nCount = tag.nFunctionCode is 1 or 2 ? (ushort)1 : (ushort)tag.nRegisterCount;
                var data = _transport.Read(nModbusId, tag.nFunctionCode, (ushort)tag.nParsedAddress, nCount);
                var rawData = tag.nFunctionCode is 1 or 2
                    ? ConvertBitsToUshorts(data, 1)
                    : ConvertBytesToUshorts(data, tag.nRegisterCount);

                var realtimeData = CreateRealtimeDataFromTag(tag, rawData, nModbusId, nGlobalTagIndex);
                if (realtimeData != null)
                    resultList.Add(realtimeData);
            }
            catch (ModbusStationException ex)
            {
                _transport?.Drain();
                if (ex.IsStationUnreachable)
                    return;
                _logger.LogDebug("單點位讀取失敗: {TagName} ({Reason})", tag.szName, ex.Message);
            }
            catch (Exception ex) when (IsConnectionLevelError(ex))
            {
                _logger.LogError(ex, "單點位讀取時連線中斷: {TagName}", tag.szName);
                MarkConnectionLost();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "單點位讀取失敗: {TagName}", tag.szName);
            }
        }
    }

    /// <summary>
    /// 為批次內所有點位產生 Bad Quality 資料
    /// </summary>
    private List<RealtimeDataModel> BuildBadQualityData(ModbusBatchGroup batchGroup, byte nModbusId)
    {
        var resultList = new List<RealtimeDataModel>();
        foreach (var tag in batchGroup.tagList)
        {
            var nGlobalTagIndex = _deviceConfig.tagList.FindIndex(t => t.szName == tag.szName);
            if (nGlobalTagIndex == -1) nGlobalTagIndex = 0;

            var nXXX = _deviceConfig.nDatabaseId * 65536 + nModbusId * 256 + 1;
            var nN = nGlobalTagIndex + 1;

            resultList.Add(new RealtimeDataModel
            {
                dtTimestamp = DateTime.Now,
                szSID = $"{nXXX}-S{nN}",
                szCoordinatorName = _deviceConfig.szCoordinatorName,
                szTagName = tag.szName,
                fValue = 0.0f,
                szUnit = tag.szUnit,
                szQuality = "Bad",
                szDeviceIP = _deviceConfig.szIP,
                nAddress = int.Parse(tag.szAddress),
                IsReadSuccess = false
            });
        }
        return resultList;
    }

    /// <summary>
    /// 將位元組陣列轉換為 ushort 陣列 (Modbus Big Endian 格式)
    /// </summary>
    /// <param name="bytes">位元組陣列</param>
    /// <param name="nRegisterCount">預期的暫存器數量</param>
    /// <returns>ushort 陣列</returns>
    private ushort[] ConvertBytesToUshorts(byte[] bytes, int nRegisterCount)
    {
        var ushortArray = new ushort[nRegisterCount];
        for (int i = 0; i < nRegisterCount && i * 2 + 1 < bytes.Length; i++)
        {
            // Modbus 使用 Big Endian：高位元組在前
            ushortArray[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        }
        _logger.LogTrace("Modbus 字節轉換: {ByteCount} bytes → {RegisterCount} registers: {Data}",
                        Math.Min(bytes.Length, nRegisterCount * 2), nRegisterCount, string.Join(",", ushortArray));
        return ushortArray;
    }

    /// <summary>
    /// 將位元組陣列轉換為 ushort 陣列 (用於 Coils 和 Discrete Inputs)
    /// 每個位元對應一個 ushort，值為 0 或 1
    /// </summary>
    /// <param name="bytes">位元組陣列</param>
    /// <param name="nBitCount">預期的位元數量</param>
    /// <returns>ushort 陣列</returns>
    private ushort[] ConvertBitsToUshorts(byte[] bytes, int nBitCount)
    {
        var ushortArray = new ushort[nBitCount];

        for (int i = 0; i < nBitCount; i++)
        {
            var nByteIndex = i / 8;      // 確定是第幾個位元組
            var nBitIndex = i % 8;       // 確定是位元組內的第幾個位元

            if (nByteIndex < bytes.Length)
            {
                // 檢查對應位元是否為 1
                var isSet = (bytes[nByteIndex] & (1 << nBitIndex)) != 0;
                ushortArray[i] = (ushort)(isSet ? 1 : 0);
            }
            else
            {
                ushortArray[i] = 0; // 超出範圍的位元設為 0
            }
        }

        _logger.LogTrace("Modbus 位元轉換: {ByteCount} bytes → {BitCount} bits: {Data}",
                        bytes.Length, nBitCount, string.Join(",", ushortArray));
        return ushortArray;
    }

    /// <summary>
    /// 取出點位解碼過程登記的警告並寫 log。
    /// ModbusTagModel 是純資料模型不持有 logger，且同一種警告每個點位只回報一次，不會刷屏。
    /// </summary>
    /// <param name="tag">點位模型</param>
    private void LogDecodeWarningIfAny(ModbusTagModel tag)
    {
        var szWarning = tag.TakeDecodeWarning();
        if (!string.IsNullOrEmpty(szWarning))
            _logger.LogWarning("Modbus 解碼警告: {Warning}", szWarning);
    }

    /// <summary>
    /// 從點位模型建立即時資料
    /// </summary>
    /// <param name="tag">點位模型</param>
    /// <param name="rawData">原始暫存器資料</param>
    /// <param name="nModbusId">Modbus 站號</param>
    /// <param name="nTagIndex">點位索引</param>
    /// <returns>即時資料模型</returns>
    private RealtimeDataModel? CreateRealtimeDataFromTag(ModbusTagModel tag, ushort[] rawData, byte nModbusId, int nTagIndex)
    {
        try
        {
            // 計算物理量
            var fPhysicalValue = tag.CalculatePhysicalValue(rawData);
            LogDecodeWarningIfAny(tag);

            // 動態生成 SID
            var nXXX = _deviceConfig.nDatabaseId * 65536 + nModbusId * 256 + 1;
            var nN = nTagIndex + 1;
            var szDynamicSID = $"{nXXX}-S{nN}";

            return new RealtimeDataModel
            {
                dtTimestamp = DateTime.Now,
                szSID = szDynamicSID,
                szCoordinatorName = _deviceConfig.szCoordinatorName,
                szTagName = tag.szName,
                fValue = fPhysicalValue,
                szUnit = tag.szUnit,
                szQuality = "Good",
                szDeviceIP = _deviceConfig.szIP,
                nAddress = int.Parse(tag.szAddress),
                IsReadSuccess = true  // 成功讀取並處理
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理點位資料時發生錯誤: {TagName}", tag.szName);
            return null;
        }
    }

    /// <summary>
    /// 連線層錯誤：標記斷線、關 TCP、所有站號開始計失敗寬限
    /// </summary>
    private void MarkConnectionLost()
    {
        DisconnectInternal();
        RecordConnectionFailure();
        _nNoResponseRounds = 0;
    }

    /// <summary>
    /// 記錄指定站號首次失敗時間（已記錄則不覆蓋）
    /// </summary>
    private void RecordStationFailure(byte nModbusId)
    {
        if (!_dtFirstFailureByStation.ContainsKey(nModbusId))
        {
            _dtFirstFailureByStation[nModbusId] = DateTime.Now;
            _logger.LogDebug("ModbusId {ModbusId} 開始計失敗寬限: {IP}:{Port}, 寬限={Delay}ms",
                             nModbusId, _deviceConfig.szIP, _deviceConfig.nPort, ReconnectDelay.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 站號恢復回應，清除失敗計時
    /// </summary>
    private void ClearStationFailure(byte nModbusId)
    {
        if (_dtFirstFailureByStation.Remove(nModbusId, out var dtFirstFailure)
            && DateTime.Now - dtFirstFailure > ReconnectDelay)
        {
            _logger.LogInformation("Modbus {IP}:{Port} ModbusID {ModbusId} 恢復回應", _deviceConfig.szIP, _deviceConfig.nPort, nModbusId);
        }
    }

    /// <summary>
    /// 已知死站本輪是否跳過：未到探測時間，且還有其他站不是死站（全部都死就每輪照問，沒有健康站要保護）
    /// </summary>
    private bool ShouldSkipUnreachableStation(byte nModbusId)
    {
        if (!_dtNextProbeByUnreachableStation.TryGetValue(nModbusId, out var dtNextProbe))
            return false;
        if (DateTime.Now >= dtNextProbe)
            return false;
        return !_deviceConfig.GetModbusIdArray().All(_dtNextProbeByUnreachableStation.ContainsKey);
    }

    /// <summary>
    /// 站判定不可達（含探測仍無回應）→ 排定下次探測時間；首次進入記一次 log
    /// </summary>
    private void MarkUnreachableStation(byte nModbusId)
    {
        var nIntervalMs = Math.Max(0, _deviceConfig.nUnreachableProbeIntervalMs);
        var isFirst = !_dtNextProbeByUnreachableStation.ContainsKey(nModbusId);
        _dtNextProbeByUnreachableStation[nModbusId] = DateTime.Now.AddMilliseconds(nIntervalMs);
        if (isFirst)
        {
            _logger.LogWarning("Modbus {IP}:{Port} ModbusID {ModbusId} 判定無回應，改為每 {Interval} 秒探測一次（單次、不重送），其他站照常輪詢",
                               _deviceConfig.szIP, _deviceConfig.nPort, nModbusId, nIntervalMs / 1000.0);
        }
    }

    /// <summary>
    /// 站恢復回應 → 解除探測模式
    /// </summary>
    private void ClearUnreachableStation(byte nModbusId)
    {
        if (_dtNextProbeByUnreachableStation.Remove(nModbusId))
        {
            _logger.LogInformation("Modbus {IP}:{Port} ModbusID {ModbusId} 探測有回應，恢復正常輪詢",
                                   _deviceConfig.szIP, _deviceConfig.nPort, nModbusId);
        }
    }

    /// <summary>
    /// 指定站號失敗是否已超過寬限期（20 × ConnectTimeout）
    /// </summary>
    private bool IsStationFailureTimeout(byte nModbusId)
    {
        return _dtFirstFailureByStation.TryGetValue(nModbusId, out var dtFirstFailure)
               && DateTime.Now - dtFirstFailure > ReconnectDelay;
    }

    /// <summary>
    /// 連線失敗：所有站號共用同一時間點開始計寬限（已在計的站號不覆蓋）
    /// </summary>
    private void RecordConnectionFailure()
    {
        foreach (var nModbusId in _deviceConfig.GetModbusIdArray())
            RecordStationFailure(nModbusId);
    }

    /// <summary>
    /// 檢查連線是否需要重新建立（加入重連嘗試時間間隔控制，避免頻繁重連）
    /// </summary>
    /// <returns>需要重連回傳 true</returns>
    public bool ShouldReconnect()
    {
        // 如果已連線，不需要重連
        if (IsConnected)
            return false;

        var now = DateTime.Now;

        // 檢查距離上次重連嘗試是否已經過了延遲時間
        if (_dtLastReconnectAttempt != DateTime.MinValue)
        {
            var timeSinceLastAttempt = now - _dtLastReconnectAttempt;
            if (timeSinceLastAttempt < ReconnectDelay)
                return false;
        }

        // 如果連線從未成功或最後成功讀取時間超過重連延遲，則允許重連
        bool canReconnect = _dtLastSuccessfulRead == DateTime.MinValue ||
                           now - _dtLastSuccessfulRead > ReconnectDelay;

        if (canReconnect)
        {
            _logger.LogDebug("設備 {IP}:{Port} 滿足重連條件: 上次成功={LastSuccess}, 上次嘗試={LastAttempt}, 當前={Now}, 延遲={Delay}ms",
                           _deviceConfig.szIP, _deviceConfig.nPort,
                           _dtLastSuccessfulRead == DateTime.MinValue ? "從未連線" : _dtLastSuccessfulRead.ToString("HH:mm:ss"),
                           _dtLastReconnectAttempt == DateTime.MinValue ? "從未嘗試" : _dtLastReconnectAttempt.ToString("HH:mm:ss"),
                           now.ToString("HH:mm:ss"), ReconnectDelay.TotalMilliseconds);
        }

        return canReconnect;
    }

    /// <summary>
    /// 取得設備狀態資訊
    /// </summary>
    /// <returns>設備狀態</returns>
    public object GetDeviceStatus()
    {
        return new
        {
            IP = _deviceConfig.szIP,
            Port = _deviceConfig.nPort,
            IsConnected = IsConnected,
            TagCount = _deviceConfig.tagList.Count,
            LastSuccessfulRead = _dtLastSuccessfulRead,
            ModbusIds = string.Join(",", _deviceConfig.GetModbusIdArray()),
            FailingModbusIds = string.Join(",", _dtFirstFailureByStation.Keys.OrderBy(n => n)),
            ProbingModbusIds = string.Join(",", _dtNextProbeByUnreachableStation.Keys.OrderBy(n => n))
        };
    }

    /// <summary>
    /// 偵測值變化或品質變化，只返回有變化的資料點
    /// </summary>
    /// <param name="currentDataList">當前讀取的資料清單</param>
    /// <returns>發生值變化或品質變化的資料清單</returns>
    private List<RealtimeDataModel> DetectValueChanges(List<RealtimeDataModel> currentDataList)
    {
        var changedDataList = new List<RealtimeDataModel>();

        foreach (var currentData in currentDataList)
        {
            if (string.IsNullOrEmpty(currentData.szSID))
                continue;

            bool hasValueChanged = false;
            bool hasQualityChanged = false;

            // 檢查值是否發生變化
            if (_lastPublishedValues.TryGetValue(currentData.szSID, out var lastValue))
            {
                // 比較浮點數值，使用小的容差值避免浮點數精度問題
                if (Math.Abs(currentData.fValue - lastValue) > 0.0001f)
                {
                    hasValueChanged = true;
                }
            }
            else
            {
                // 第一次讀取值，視為有變化
                hasValueChanged = true;
            }

            // 檢查品質是否發生變化
            if (_lastPublishedQuality.TryGetValue(currentData.szSID, out var lastQuality))
            {
                if (!string.Equals(currentData.szQuality, lastQuality, StringComparison.OrdinalIgnoreCase))
                {
                    hasQualityChanged = true;
                }
            }
            else
            {
                // 第一次讀取品質，視為有變化
                hasQualityChanged = true;
            }

            // 檢查是否超過定期重新發布間隔（防止 Web 端標記為 STALE）
            bool isRepublishDue = false;
            if (_lastPublishedTime.TryGetValue(currentData.szSID, out var dtLastPublished))
            {
                if (DateTime.Now.Subtract(dtLastPublished).TotalMinutes >= REPUBLISH_INTERVAL_MINUTES)
                {
                    isRepublishDue = true;
                }
            }

            bool hasChanged = hasValueChanged || hasQualityChanged || isRepublishDue;

            if (hasChanged)
            {
                _lastPublishedValues.AddOrUpdate(currentData.szSID, currentData.fValue, (szKey, szOldValue) => currentData.fValue);
                _lastPublishedQuality.AddOrUpdate(currentData.szSID, currentData.szQuality, (szKey, szOldQuality) => currentData.szQuality);
                _lastPublishedTime.AddOrUpdate(currentData.szSID, DateTime.Now, (szKey, _) => DateTime.Now);

                changedDataList.Add(currentData);
            }
        }

        return changedDataList;
    }

    /// <summary>
    /// 連線層錯誤判定：只有 socket / IO 層真的壞了才算（站層逾時、例外碼走 ModbusStationException，不在此列）
    /// </summary>
    /// <param name="ex">異常物件</param>
    /// <returns>是否為連線層錯誤</returns>
    private static bool IsConnectionLevelError(Exception ex)
    {
        return ex is SocketException ||
               ex is IOException ||
               ex is ObjectDisposedException;
    }

    /// <summary>
    /// 釋放資源
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            // 同步等待斷線完成，確保 TCP 連線確實關閉
            DisconnectInternal();
            _connectionSemaphore.Dispose();

            // 清理值變化快取
            _lastPublishedValues.Clear();
            _lastPublishedQuality.Clear();
            _lastPublishedTime.Clear();
        }
    }
}
