using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using ScadaEngine.Engine.Communication.Mqtt;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 通知設定檔熱重載 MQTT 訂閱（Engine 端）— 比照 AlarmRuleReloadSubscriber / SmsMqttBridge 的控制 topic 模式：
///   訂閱 SCADA/Sys/Notify/Reload（Web→Engine，Retain=false）：{"file":"EmailSetting.json","requestId":"..."}
///   發布 SCADA/Sys/Notify/Status（Engine→Web，Retain=false）：{"file","requestId","success","message","source","path","time"}
/// Web 存完 EmailSetting / SmsSetting 後發 Reload，Engine 重讀同一份（Shared）並套用到既有服務，
/// 不必重啟 Engine（採集 / 控制 / 警報不中斷）。實際重載邏輯在 <see cref="NotifySettingReloader"/>。
/// </summary>
public class NotifySettingReloadSubscriber : BackgroundService
{
    public const string TOPIC_RELOAD = "SCADA/Sys/Notify/Reload";
    public const string TOPIC_STATUS = "SCADA/Sys/Notify/Status";

    private readonly ILogger<NotifySettingReloadSubscriber> _logger;
    private readonly MqttConfigService _mqttConfigService;
    private readonly MqttPublishService _mqttPublishService;
    private readonly NotifySettingReloader _reloader;

    private IMqttClient? _mqttClient;
    private bool _isSubscribed = false;
    private bool _disposed = false;

    public NotifySettingReloadSubscriber(
        ILogger<NotifySettingReloadSubscriber> logger,
        MqttConfigService mqttConfigService,
        MqttPublishService mqttPublishService,
        NotifySettingReloader reloader)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mqttConfigService = mqttConfigService ?? throw new ArgumentNullException(nameof(mqttConfigService));
        _mqttPublishService = mqttPublishService ?? throw new ArgumentNullException(nameof(mqttPublishService));
        _reloader = reloader ?? throw new ArgumentNullException(nameof(reloader));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("通知設定 Reload 訂閱服務啟動 (Engine)");

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
                        _logger.LogWarning("通知設定 Reload 訂閱連線中斷，嘗試重新連線");
                        await ReconnectMqttAsync(mqttSetting.MqttConfig);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "通知設定 Reload 訂閱監控迴圈發生錯誤");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通知設定 Reload 訂閱服務執行時發生錯誤");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ── 命令處理 ──

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var szPayload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            if (string.IsNullOrWhiteSpace(szPayload)) return;

            string? szFile = null;
            string? szRequestId = null;
            using (var doc = JsonDocument.Parse(szPayload))
            {
                if (doc.RootElement.TryGetProperty("file", out var fileProp)) szFile = fileProp.GetString();
                if (doc.RootElement.TryGetProperty("requestId", out var reqProp)) szRequestId = reqProp.GetString();
            }

            _logger.LogInformation("收到通知設定 Reload 通知: file={File}, requestId={RequestId}", szFile, szRequestId);

            if (!NotifySettingLoader.IsKnownFile(szFile))
            {
                _logger.LogWarning("通知設定 Reload 檔名不在白名單，忽略: {File}", szFile);
                return;
            }

            var result = await _reloader.ReloadAsync(szFile);
            await PublishStatusAsync(result, szRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "處理通知設定 Reload 訊息失敗");
        }
    }

    private async Task PublishStatusAsync(NotifySettingReloadResult result, string? szRequestId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                file = result.szFileName,
                requestId = szRequestId ?? string.Empty,
                success = result.isSuccess,
                message = result.szMessage,
                source = result.szSource,
                path = result.szPath ?? string.Empty,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }, new JsonSerializerOptions { PropertyNamingPolicy = null, WriteIndented = false });

            await _mqttPublishService.PublishRawJsonAsync(TOPIC_STATUS, payload, isRetain: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發布通知設定重載狀態失敗");
        }
    }

    // ── MQTT 連線（比照 AlarmRuleReloadSubscriber）──

    private async Task InitializeMqttAsync(object mqttConfig)
    {
        try
        {
            dynamic config = mqttConfig;
            _mqttClient = new MqttFactory().CreateMqttClient();

            var clientId = $"ScadaEngine_NotifyReload_{Environment.ProcessId}";
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
                _logger.LogInformation("通知設定 Reload 訂閱連線成功，ClientId: {ClientId}", clientId);
                await Task.Delay(500);
                await SubscribeTopicAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化通知設定 Reload MQTT 連線時發生錯誤");
        }
    }

    private async Task SubscribeTopicAsync()
    {
        try
        {
            await _mqttClient!.SubscribeAsync(TOPIC_RELOAD);
            _isSubscribed = true;
            _logger.LogInformation("已訂閱通知設定 Reload 主題: {Topic}", TOPIC_RELOAD);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "訂閱通知設定 Reload 主題失敗");
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
        _logger.LogWarning("通知設定 Reload MQTT 連線中斷: {Reason}", e.Reason);
        return Task.CompletedTask;
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
            _logger.LogError(ex, "重新連線通知設定 Reload MQTT 時發生錯誤");
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_mqttClient != null)
            {
                if (_mqttClient.IsConnected)
                    await _mqttClient.DisconnectAsync();
                _mqttClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理通知設定 Reload MQTT 連線時發生錯誤");
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
