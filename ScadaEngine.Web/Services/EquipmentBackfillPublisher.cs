using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using ScadaEngine.Engine.Communication.Mqtt;
using ScadaEngine.Engine.Services;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 設備效率診斷 — Web 端「回填歷史」MQTT 發布者。
/// 發 SCADA/Sys/EquipmentHourly/Backfill（topic 常數取自 Engine 的 <see cref="EquipmentHourlyBackfillSubscriber"/>，單一真相），
/// Engine 收到後逐小時重算 EquipmentHourly。QoS=1、Retain=false（控制訊號不殘留，避免 Engine 重啟又跑一次）。
/// Singleton + IHostedService（啟動時連 broker）；失敗只回 false，不拋例外。
/// </summary>
public class EquipmentBackfillPublisher : IHostedService, IDisposable
{
    private readonly ILogger<EquipmentBackfillPublisher> _logger;
    private readonly MqttConfigService _mqttConfigService;
    private IMqttClient? _mqttClient;
    private MqttClientOptions? _mqttOptions;
    private bool _disposed;

    public EquipmentBackfillPublisher(ILogger<EquipmentBackfillPublisher> logger, MqttConfigService mqttConfigService)
    {
        _logger = logger;
        _mqttConfigService = mqttConfigService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = (await _mqttConfigService.LoadConfigAsync()).MqttConfig;
            _mqttClient = new MqttFactory().CreateMqttClient();
            _mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(config.szBrokerIp, config.nPort)
                .WithClientId($"ScadaWeb_EquipmentBackfill_{Environment.ProcessId}")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                .Build();
            var result = await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
                _logger.LogWarning("設備效率 Backfill 發布者連線失敗: {Code}", result.ResultCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動設備效率 Backfill 發布者時發生錯誤（不影響 Web 啟動）");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_mqttClient?.IsConnected == true) await _mqttClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止設備效率 Backfill 發布者時發生錯誤");
        }
    }

    /// <summary>發布回填請求；nProfileId = null 表全部設備。成功送達 broker 回 true</summary>
    public async Task<bool> PublishAsync(int? nProfileId, DateTime dtFrom, DateTime dtTo)
    {
        if (_mqttClient == null) return false;
        if (!_mqttClient.IsConnected)
        {
            try
            {
                if (_mqttOptions != null) await _mqttClient.ConnectAsync(_mqttOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "設備效率 Backfill 發布者重連失敗");
                return false;
            }
        }

        try
        {
            var szPayload = JsonSerializer.Serialize(new
            {
                profileId = nProfileId,
                from = dtFrom.ToString("yyyy-MM-dd"),
                to = dtTo.ToString("yyyy-MM-dd"),
            });
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(EquipmentHourlyBackfillSubscriber.TOPIC)
                .WithPayload(szPayload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();
            var result = await _mqttClient.PublishAsync(message);
            var isOk = result.ReasonCode == MqttClientPublishReasonCode.Success;
            if (isOk) _logger.LogInformation("已發布設備效率 Backfill: {Payload}", szPayload);
            else _logger.LogWarning("設備效率 Backfill 發布失敗: {Code}", result.ReasonCode);
            return isOk;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "發布設備效率 Backfill 時發生錯誤");
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
