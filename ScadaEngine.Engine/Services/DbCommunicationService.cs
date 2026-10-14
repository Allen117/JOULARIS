using System.Collections.Concurrent;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Communication.Mqtt;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Models;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// DB 來源通訊服務 — 為每個啟用的 DBCoordinator 啟動一個 polling loop，
/// 從 DBLatestData 統一入口表讀取資料，直接以工程值丟入 SCADA 既有 pipeline。
/// </summary>
public class DbCommunicationService : BackgroundService
{
    private readonly ILogger<DbCommunicationService> _logger;
    private readonly DbCoordinatorJsonLoader _loader;
    private readonly IDataRepository _dataRepository;
    private readonly MqttPublishService _mqttPublishService;
    private readonly RealtimeDataStorageService _realtimeStorage;
    private readonly HistoryDataStorageService _historyStorage;
    private readonly AlarmMonitorService _alarmMonitorService;
    private readonly CalculatedPointService _calculatedPointService;
    private readonly LicenseState _licenseState;

    /// <summary>
    /// PollingInterval 下限（避免使用者誤設過短把連線池打滿）
    /// </summary>
    private const int MIN_POLLING_INTERVAL_MS = 200;

    /// <summary>
    /// 重新載入序列化（避免 Reload 與正在跑的 polling 半更新）
    /// </summary>
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    /// <summary>
    /// 每個 Coordinator 對應的點位（依 Sequence 索引），key=CoordinatorId
    /// </summary>
    private readonly ConcurrentDictionary<int, CoordinatorRuntime> _runtimes = new();

    /// <summary>
    /// 服務取消來源（每次 Reload 重建）
    /// </summary>
    private CancellationTokenSource? _innerCts;

    public DbCommunicationService(
        ILogger<DbCommunicationService> logger,
        DbCoordinatorJsonLoader loader,
        IDataRepository dataRepository,
        MqttPublishService mqttPublishService,
        RealtimeDataStorageService realtimeStorage,
        HistoryDataStorageService historyStorage,
        AlarmMonitorService alarmMonitorService,
        CalculatedPointService calculatedPointService,
        LicenseState licenseState)
    {
        _logger = logger;
        _loader = loader;
        _dataRepository = dataRepository;
        _mqttPublishService = mqttPublishService;
        _realtimeStorage = realtimeStorage;
        _historyStorage = historyStorage;
        _alarmMonitorService = alarmMonitorService;
        _calculatedPointService = calculatedPointService;
        _licenseState = licenseState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DB 來源通訊服務啟動");

        try
        {
            // 等候 MQTT / DB 初始化（Worker 啟動有約 1 秒緩衝）
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            await ReloadAsync(stoppingToken);

            // 進入無限等待 — 各 Coordinator polling loop 都已被 ReloadAsync 啟動
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DB 來源通訊服務收到停止訊號");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB 來源通訊服務執行時發生錯誤");
        }
        finally
        {
            _innerCts?.Cancel();
        }
    }

    /// <summary>
    /// 重新載入 DBPoint/*.json，重建所有 Coordinator polling loops。
    /// 由 DbCoordinatorReloadSubscriber 在收到 SCADA/Sys/DbCoordinator/Reload 時呼叫。
    /// </summary>
    public async Task ReloadAsync(CancellationToken externalToken = default)
    {
        await _reloadGate.WaitAsync(externalToken);
        try
        {
            // 取消舊的所有 polling loops
            _innerCts?.Cancel();
            _innerCts?.Dispose();
            _innerCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            _runtimes.Clear();

            var loaded = await _loader.LoadAllAsync();

            foreach (var item in loaded)
            {
                if (!item.Coordinator.isMonitorEnabled)
                {
                    _logger.LogInformation("Coordinator {Name} 未啟用監控，略過", item.Coordinator.szName);
                    continue;
                }

                var pointBySid = item.Points.ToDictionary(p => p.szSID, StringComparer.Ordinal);

                if (pointBySid.Count == 0)
                {
                    _logger.LogInformation("Coordinator {Name} 無點位，略過 polling", item.Coordinator.szName);
                    continue;
                }

                var runtime = new CoordinatorRuntime
                {
                    Coordinator = item.Coordinator,
                    PointsBySid = pointBySid,
                    PollingIntervalMs = Math.Max(MIN_POLLING_INTERVAL_MS, item.Coordinator.nPollingInterval),
                    CommandTimeoutMs = item.Coordinator.nConnectTimeout
                };
                _runtimes[item.Coordinator.Id] = runtime;

                _ = Task.Run(() => RunPollingLoopAsync(runtime, _innerCts.Token));
            }

            _logger.LogInformation("DB 來源 Reload 完成: 啟動 {Count} 個 polling loop", _runtimes.Count);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task RunPollingLoopAsync(CoordinatorRuntime runtime, CancellationToken ct)
    {
        var szPrefix = $"DB{runtime.Coordinator.Id}-";
        _logger.LogInformation("Polling 啟動: {Name}, prefix={Prefix}, interval={Interval}ms",
            runtime.Coordinator.szName, szPrefix, runtime.PollingIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 授權失效時跳過 polling
                if (!_licenseState.IsValid)
                {
                    await Task.Delay(runtime.PollingIntervalMs, ct);
                    continue;
                }

                await PollOnceAsync(runtime, szPrefix, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coordinator {Name} polling 失敗（將於下個週期重試）", runtime.Coordinator.szName);
            }

            try
            {
                await Task.Delay(runtime.PollingIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Polling 停止: {Name}", runtime.Coordinator.szName);
    }

    private async Task PollOnceAsync(CoordinatorRuntime runtime, string szPrefix, CancellationToken ct)
    {
        // 嘗試讀 DBLatestData；exception 視為「讀失敗」走 Bad 分支
        List<LatestDataModel> rows;
        bool isReadOk;
        try
        {
            rows = (await _dataRepository.GetDbLatestDataByPrefixAsync(szPrefix, runtime.CommandTimeoutMs)).ToList();
            isReadOk = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coordinator {Name} 讀取 DBLatestData 失敗，將以 Bad quality 寫歷史（HasEverReadSuccessfully={Flag}）",
                runtime.Coordinator.szName, runtime.HasEverReadSuccessfully);
            rows = new List<LatestDataModel>();
            isReadOk = false;
        }

        if (!isReadOk)
        {
            // Q1=c：Engine 啟動後從未讀成功過 → 不寫，避免灌一堆 0
            if (!runtime.HasEverReadSuccessfully) return;

            // 用 LastSeen 的最近一次成功值 + Quality=Bad 餵給 HistoryDataStorageService
            var failHistoryBatch = new List<RealtimeDataModel>();
            foreach (var (szSid, point) in runtime.PointsBySid)
            {
                var fLastValue = runtime.LastSeen.TryGetValue(szSid, out var prev) ? prev.Value : 0f;
                failHistoryBatch.Add(new RealtimeDataModel
                {
                    szSID = szSid,
                    szTagName = point.szName,
                    fValue = fLastValue,
                    szUnit = point.szUnit ?? string.Empty,
                    szQuality = "Bad",
                    szDeviceIP = "DB",
                    nAddress = point.nSequence,
                    szCoordinatorName = runtime.Coordinator.szName,
                    dtTimestamp = DateTime.Now,
                    IsReadSuccess = false
                });
            }
            if (failHistoryBatch.Count > 0)
                _historyStorage.AddRealtimeDataBatch(failHistoryBatch);
            return;
        }

        // 讀成功 — 標記旗標
        runtime.HasEverReadSuccessfully = true;

        if (rows.Count == 0) return;

        // 兩個 batch 用途不同：
        //   historyBatch — 全部已配置的 row，無條件每分鐘存一筆（依靠 HistoryDataStorageService 的分鐘 dedup）
        //   changedBatch — 只放有變化的 row，給 MQTT / 警報 / 計算 / LatestData 使用
        var historyBatch = new List<RealtimeDataModel>();
        var changedBatch = new List<RealtimeDataModel>();
        var latestBatch = new List<LatestDataModel>();

        foreach (var row in rows)
        {
            if (!runtime.PointsBySid.TryGetValue(row.szSID, out var point))
            {
                if (runtime.UnknownSidLogged.Add(row.szSID))
                {
                    _logger.LogWarning("DBLatestData 出現未配置的 SID: {SID}（跳過後續 log）", row.szSID);
                }
                continue;
            }

            // DBLatestData.Value 即工程值（外部系統直接寫工程值，不再做 raw × Ratio 換算）
            var fEngValue = row.fValue;
            // Quality 規則：只有寫入端「明確寫 0」才是 Bad；NULL（SQL 端 ISNULL→1）、1、其他值一律 Good。
            // 動機：外部系統 / 手動 UPDATE 多半只改 Value 不碰 Quality，不能因此把整條下游（計算點 / 歷史 / 基線）判壞；
            // 真正要標壞的寫入端（如 Web WeatherFetchService）本來就明確寫 0，語意不變。
            var szQuality = row.nQuality != 0 ? "Good" : "Bad";
            var dtTs = row.dtTimestamp;

            // 不論變化與否都先放進 historyBatch — HistoryData 用 polling 當下時間（HistoryDataStorageService 內部 truncate 到分鐘）
            historyBatch.Add(new RealtimeDataModel
            {
                szSID = row.szSID,
                szTagName = point.szName,
                fValue = fEngValue,
                szUnit = point.szUnit ?? string.Empty,
                szQuality = szQuality,
                szDeviceIP = "DB",
                nAddress = point.nSequence,
                szCoordinatorName = runtime.Coordinator.szName,
                dtTimestamp = DateTime.Now,
                IsReadSuccess = true
            });

            // 變化偵測（值/品質/時間戳任一不同就算）— 只擋 MQTT / 警報 / 計算 / LatestData
            if (runtime.LastSeen.TryGetValue(row.szSID, out var prev)
                && Math.Abs(prev.Value - fEngValue) < float.Epsilon
                && prev.Quality == row.nQuality
                && prev.Timestamp == dtTs)
            {
                continue;
            }
            runtime.LastSeen[row.szSID] = (fEngValue, row.nQuality, dtTs);

            // 變化的 row — realtime 用 DBLatestData.Timestamp，反映外部寫入真實時間
            changedBatch.Add(new RealtimeDataModel
            {
                szSID = row.szSID,
                szTagName = point.szName,
                fValue = fEngValue,
                szUnit = point.szUnit ?? string.Empty,
                szQuality = szQuality,
                szDeviceIP = "DB",
                nAddress = point.nSequence,
                szCoordinatorName = runtime.Coordinator.szName,
                dtTimestamp = dtTs,
                IsReadSuccess = true
            });

            latestBatch.Add(new LatestDataModel
            {
                szSID = row.szSID,
                fValue = fEngValue,
                nQuality = row.nQuality,
                dtTimestamp = dtTs
            });
        }

        // 歷史資料：所有配置的 row 都餵入（分鐘 dedup 自動降頻）
        if (historyBatch.Count > 0)
            _historyStorage.AddRealtimeDataBatch(historyBatch);

        if (changedBatch.Count == 0) return;

        // LatestData — 只 UPSERT 有變化的（沒變化就跳過省 SQL，反正下一筆變化會覆蓋）
        if (latestBatch.Count > 0)
            await _dataRepository.SaveLatestDataAsync(latestBatch);

        // 餵入 RealtimeStorage 的 persistent cache（給 calc / LogicFlow 讀）
        _realtimeStorage.UpdatePersistentCacheOnly(changedBatch);

        // 發布 MQTT
        foreach (var rt in changedBatch)
        {
            try
            {
                await _mqttPublishService.PublishRealtimeDataAsync(rt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB 來源 {SID} MQTT 發布失敗", rt.szSID);
            }
        }

        // 警報評估
        try
        {
            await _alarmMonitorService.EvaluateBatchAsync(changedBatch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB 來源警報評估失敗");
        }

        // 計算點位（含警報二次評估）
        try
        {
            var calcResults = _calculatedPointService.CalculateAndPublish(changedBatch);
            if (calcResults.Count > 0)
                await _alarmMonitorService.EvaluateBatchAsync(calcResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB 來源計算點位失敗");
        }

        _logger.LogTrace("Coordinator {Name} 變更 {Count} 筆 / 歷史 {HCount} 筆已處理",
            runtime.Coordinator.szName, changedBatch.Count, historyBatch.Count);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _innerCts?.Cancel();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 單個 Coordinator 的執行期狀態
    /// </summary>
    private class CoordinatorRuntime
    {
        public DbCoordinatorModel Coordinator { get; set; } = new();
        public Dictionary<string, DbPointModel> PointsBySid { get; set; } = new();
        public int PollingIntervalMs { get; set; } = 1000;
        public int CommandTimeoutMs { get; set; } = 1000;

        /// <summary>變化偵測快取：SID → (engValue, quality, timestamp)</summary>
        public ConcurrentDictionary<string, (float Value, int Quality, DateTime Timestamp)> LastSeen { get; } = new();

        /// <summary>已 log 過的未知 SID（避免重複塞日誌）</summary>
        public HashSet<string> UnknownSidLogged { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Engine 啟動後是否曾經至少成功讀到 DBLatestData 一次。
        /// 用於控制讀失敗時是否寫 Bad 歷史（從未成功過就不寫，避免一啟動就連線失敗時灌 0 進歷史）。
        /// </summary>
        public bool HasEverReadSuccessfully { get; set; } = false;
    }
}
