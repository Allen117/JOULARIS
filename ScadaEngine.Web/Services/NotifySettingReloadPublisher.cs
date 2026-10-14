using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ScadaEngine.Engine.Communication.Mqtt;

namespace ScadaEngine.Web.Services;

/// <summary>
/// Web 端通知設定熱重載 MQTT 發布者
/// Web 存完 EmailSetting / SmsSetting（寫到 C:\JOULARIS\Shared\）後發 SCADA/Sys/Notify/Reload，
/// Engine 的 NotifySettingReloadSubscriber 重讀同一份並套用，不必重啟 Engine。
/// 結果由 Engine 回 SCADA/Sys/Notify/Status（<see cref="NotifyReloadStatusCache"/> 依 requestId 配對）。
/// 連線模式比照 SmsCommandPublisher（QoS=1、Retain=false、失敗只記 log）。
/// </summary>
public class NotifySettingReloadPublisher : IHostedService, IDisposable
{
    public const string TOPIC = "SCADA/Sys/Notify/Reload";

    private readonly ILogger<NotifySettingReloadPublisher> _logger;
    private readonly MqttConfigService _mqttConfigService;
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _mqttOptions;
    private bool _disposed = false;

    public NotifySettingReloadPublisher(
        ILogger<NotifySettingReloadPublisher> logger,
        MqttConfigService mqttConfigService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mqttConfigService = mqttConfigService ?? throw new ArgumentNullException(nameof(mqttConfigService));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mqttSetting = await _mqttConfigService.LoadConfigAsync();
            var config = mqttSetting.MqttConfig;

            _mqttClient = new MqttFactory().CreateMqttClient();
            var clientId = $"ScadaWeb_NotifyReload_{Environment.ProcessId}";
            _mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(config.szBrokerIp, config.nPort)
                .WithClientId(clientId)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                .Build();

            var result = await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
                _logger.LogInformation("通知設定 Reload 發布者連線成功，ClientId: {ClientId}", clientId);
            else
                _logger.LogWarning("通知設定 Reload 發布者連線失敗: {Code}", result.ResultCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動通知設定 Reload 發布者時發生錯誤（不影響 Web 啟動）");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_mqttClient != null && _mqttClient.IsConnected)
                await _mqttClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止通知設定 Reload 發布者時發生錯誤");
        }
    }

    /// <summary>發布重載指令；requestId 供 Status 回報配對</summary>
    public async Task<bool> PublishReloadAsync(string szFileName, string szRequestId)
    {
        if (_mqttClient == null)
        {
            _logger.LogWarning("通知設定 Reload 發布者尚未初始化，無法送出指令");
            return false;
        }

        if (!_mqttClient.IsConnected)
        {
            try
            {
                if (_mqttOptions != null)
                    await _mqttClient.ConnectAsync(_mqttOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "通知設定 Reload 發布者重連失敗，指令未送出");
                return false;
            }
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { file = szFileName, requestId = szRequestId },
                new JsonSerializerOptions { PropertyNamingPolicy = null, WriteIndented = false });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(TOPIC)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            var result = await _mqttClient.PublishAsync(message);
            if (result.ReasonCode == MqttClientPublishReasonCode.Success)
            {
                _logger.LogInformation("通知設定 Reload 已發布: {Payload}", payload);
                return true;
            }
            _logger.LogWarning("通知設定 Reload 發布失敗: {Code}", result.ReasonCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發布通知設定 Reload 時發生錯誤");
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mqttClient?.Dispose();
            _disposed = true;
        }
    }
}
