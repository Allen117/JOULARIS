using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Communication.Mqtt;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 設備效率診斷 — Backfill MQTT 訂閱服務（仿 <see cref="EnergyLeafBackfillSubscriber"/>）。
/// 訂閱 SCADA/Sys/EquipmentHourly/Backfill，payload：{ "profileId": int | null, "from": "yyyy-MM-dd", "to": "yyyy-MM-dd" }
///   profileId = null → 全部啟用中設備；有值 → 只算該台（典型：設定頁改了綁定 / 單位後按「回填歷史」）
/// 逐小時重算並覆寫（IsBackfilled=1）；重算後判定資料不足的小時，舊列一併刪除（綁定改錯再改回時不殘留舊值）。
/// 按月分批 + 每批 sleep，避免壓住線上查詢。
/// </summary>
public class EquipmentHourlyBackfillSubscriber : BackgroundService
{
    private readonly ILogger<EquipmentHourlyBackfillSubscriber> _logger;
    private readonly MqttConfigService _mqttConfigService;
    private readonly EquipmentHourlyAggregator _aggregator;
    private readonly EquipmentHourlyRepository _repository;
    private readonly EquipmentProfileRepository _profileRepository;
    private readonly IConfiguration _configuration;
    private IMqttClient? _mqttClient;
    private bool _isSubscribed;
    private bool _disposed;

    /// <summary>單一 backfill 期間上限（天），防誤觸發跑十年</summary>
    public const int MAX_BACKFILL_DAYS = 400;

    public const string TOPIC = "SCADA/Sys/EquipmentHourly/Backfill";

    public EquipmentHourlyBackfillSubscriber(
        ILogger<EquipmentHourlyBackfillSubscriber> logger,
        MqttConfigService mqttConfigService,
        EquipmentHourlyAggregator aggregator,
        EquipmentHourlyRepository repository,
        EquipmentProfileRepository profileRepository,
        IConfiguration configuration)
    {
        _logger = logger;
        _mqttConfigService = mqttConfigService;
        _aggregator = aggregator;
        _repository = repository;
        _profileRepository = profileRepository;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("設備效率 Backfill 訂閱服務啟動 (Engine)");
        try
        {
            var mqttSetting = await _mqttConfigService.LoadConfigAsync();
            await InitializeMqttAsync(mqttSetting.MqttConfig);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_mqttClient?.IsConnected != true)
                    {
                        _logger.LogWarning("設備效率 Backfill 訂閱連線中斷，嘗試重新連線");
                        await ReconnectMqttAsync(mqttSetting.MqttConfig);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "設備效率 Backfill 訂閱監控迴圈發生錯誤");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設備效率 Backfill 訂閱服務執行時發生錯誤");
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
            var clientId = $"ScadaEngine_EquipmentBackfill_{Environment.ProcessId}";
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
                _logger.LogInformation("設備效率 Backfill 訂閱連線成功，ClientId: {ClientId}", clientId);
                await Task.Delay(500);
                await SubscribeTopicAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化設備效率 Backfill MQTT 連線時發生錯誤");
        }
    }

    private async Task SubscribeTopicAsync()
    {
        try
        {
            await _mqttClient!.SubscribeAsync(TOPIC);
            _isSubscribed = true;
            _logger.LogInformation("已訂閱設備效率 Backfill 主題: {Topic}", TOPIC);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "訂閱設備效率 Backfill 主題失敗");
        }
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        if (!_isSubscribed)
        {
            await Task.Delay(500);
            await SubscribeTopicAsync();
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        _isSubscribed = false;
        _logger.LogWarning("設備效率 Backfill MQTT 連線中斷: {Reason}", e.Reason);
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var szPayload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            _logger.LogInformation("收到設備效率 Backfill 訊號: {Payload}", szPayload);
            var req = ParsePayload(szPayload);
            if (req == null)
            {
                _logger.LogWarning("設備效率 Backfill payload 解析失敗，略過");
                return Task.CompletedTask;
            }
            // fire-and-forget — 可能跑數分鐘，不阻塞 MQTT callback
            _ = Task.Run(() => RunBackfillAsync(req.Value.nProfileId, req.Value.dtFrom, req.Value.dtTo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理設備效率 Backfill 訊息失敗");
        }
        return Task.CompletedTask;
    }

    /// <summary>payload → (profileId?, from, to exclusive)。to 含整天 → exclusive = to + 1 天；期間上限 400 天、不超過現在整點</summary>
    public static (int? nProfileId, DateTime dtFrom, DateTime dtTo)? ParsePayload(string szPayload)
    {
        if (string.IsNullOrWhiteSpace(szPayload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(szPayload);
            var root = doc.RootElement;
            int? nProfileId = null;
            if (root.TryGetProperty("profileId", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                nProfileId = idProp.GetInt32();
            if (!root.TryGetProperty("from", out var fromProp) || !root.TryGetProperty("to", out var toProp))
                return null;
            if (!DateTime.TryParse(fromProp.GetString(), out var dtFrom)) return null;
            if (!DateTime.TryParse(toProp.GetString(), out var dtTo)) return null;

            dtFrom = dtFrom.Date;
            dtTo = dtTo.Date.AddDays(1);
            if (dtTo <= dtFrom) return null;
            if ((dtTo - dtFrom).TotalDays > MAX_BACKFILL_DAYS) dtFrom = dtTo.AddDays(-MAX_BACKFILL_DAYS);
            return (nProfileId, dtFrom, dtTo);
        }
        catch
        {
            return null;
        }
    }

    private async Task RunBackfillAsync(int? nProfileFilter, DateTime dtFrom, DateTime dtToExclusive)
    {
        try
        {
            var nMinSamples = _configuration.GetValue<int?>("EquipmentAggregation:MinSamplesPerHour") ?? 30;
            var nMaxStale = _configuration.GetValue<int?>("EquipmentAggregation:MaxStalenessHours") ?? 2;
            var nSleepMs = _configuration.GetValue<int?>("EquipmentAggregation:BackfillBatchSleepMs") ?? 500;

            // 未結束的當前小時不算
            var dtNow = DateTime.Now;
            var dtCurrentHour = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, dtNow.Hour, 0, 0);
            if (dtToExclusive > dtCurrentHour) dtToExclusive = dtCurrentHour;

            var all = await _profileRepository.GetEnabledProfilesAsync();
            var profiles = nProfileFilter == null ? all : all.Where(p => p.nId == nProfileFilter.Value).ToList();
            if (profiles.Count == 0)
            {
                _logger.LogWarning("設備效率 Backfill：找不到符合條件的啟用設備 (profileId={Id})", nProfileFilter);
                return;
            }

            _logger.LogInformation("設備效率 Backfill 開始：設備 {Count} 台，期間 {From:yyyy-MM-dd} ~ {To:yyyy-MM-dd HH:mm}（exclusive）",
                profiles.Count, dtFrom, dtToExclusive);

            int nWritten = 0, nSparse = 0, nBatches = 0;
            foreach (var p in profiles)
            {
                var dtMonth = new DateTime(dtFrom.Year, dtFrom.Month, 1);
                while (dtMonth < dtToExclusive)
                {
                    var dtMonthEnd = dtMonth.AddMonths(1);
                    var dtBatchStart = dtMonth < dtFrom ? dtFrom : dtMonth;
                    var dtBatchEnd = dtMonthEnd > dtToExclusive ? dtToExclusive : dtMonthEnd;
                    for (var dtH = dtBatchStart; dtH < dtBatchEnd; dtH = dtH.AddHours(1))
                    {
                        try
                        {
                            var m = await _aggregator.ComputeAsync(p, dtH, nMinSamples, nMaxStale);
                            if (m == null)
                            {
                                await _repository.DeleteAsync(p.nId, dtH);
                                nSparse++;
                                continue;
                            }
                            m.isBackfilled = true;
                            await _repository.UpsertAsync(m);
                            nWritten++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "設備效率 Backfill 失敗於 {Name}(#{Id}) {Hour:yyyy-MM-dd HH}", p.szName, p.nId, dtH);
                        }
                    }
                    nBatches++;
                    if (nSleepMs > 0) await Task.Delay(nSleepMs);
                    dtMonth = dtMonthEnd;
                }
            }

            _logger.LogInformation("設備效率 Backfill 完成：{Batches} 批，寫入 {Written} 列、資料不足 {Sparse} 列",
                nBatches, nWritten, nSparse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設備效率 Backfill 執行失敗");
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
            }
            _mqttClient?.Dispose();
            await InitializeMqttAsync(mqttConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新連線設備效率 Backfill MQTT 時發生錯誤");
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_mqttClient != null)
            {
                if (_mqttClient.IsConnected) await _mqttClient.DisconnectAsync();
                _mqttClient.Dispose();
                _mqttClient = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理設備效率 Backfill MQTT 連線時發生錯誤");
        }
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient?.Dispose();
            _disposed = true;
        }
        base.Dispose();
    }
}
