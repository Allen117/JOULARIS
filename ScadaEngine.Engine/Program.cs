using ScadaEngine.Engine;
using ScadaEngine.Engine.Services;
using ScadaEngine.Engine.Communication.Modbus.Extensions;
using ScadaEngine.Engine.Communication.Mqtt.Extensions;
using ScadaEngine.Engine.Data.Extensions;
using Serilog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.DependencyInjection;
// 檢查是否以 Windows Service 運行
var isWindowsService = WindowsServiceHelpers.IsWindowsService();

// 配置 Serilog，Windows Service 時使用檔案日誌
var logConfigBuilder = new LoggerConfiguration();
if (isWindowsService)
{
    // Windows Service 模式：主要使用檔案日誌
    logConfigBuilder
        .WriteTo.File(
            path: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log", "ScadaEngine-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}"
        )
        .WriteTo.File(
            path: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Log", "ScadaEngine-Error-.log"),
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 90
        )
        .MinimumLevel.Information();
}
else
{
    // 開發模式：使用設定檔 + Console
    logConfigBuilder.ReadFrom.Configuration(Host.CreateApplicationBuilder(args).Configuration);
}

Log.Logger = logConfigBuilder.CreateLogger();

try
{
    Log.Information("SCADA 引擎服務啟動中... (模式: {ServiceMode})", isWindowsService ? "Windows Service" : "Console Application");

    var builder = Host.CreateApplicationBuilder(args);

    // 配置 Windows Service 支援
    if (isWindowsService)
    {
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "JOULARIS Engine";
        });
    }

    // 使用 Serilog 作為日誌提供者
    builder.Services.AddSerilog();

    // 註冊 Worker 背景服務
    builder.Services.AddHostedService<Worker>();

    // 註冊 Modbus 相關服務
    builder.Services.AddModbusServices();

    // 註冊 MQTT 相關服務
    builder.Services.AddMqttServices();

    // 註冊資料庫相關服務 (包含儲存服務)
    builder.Services.AddDataServices();

    // 授權守衛
    builder.Services.AddSingleton<LicenseState>();
    builder.Services.AddSingleton<LicenseService>();
    builder.Services.AddHostedService<LicenseService>(provider =>
        provider.GetRequiredService<LicenseService>());
    builder.Services.AddHostedService<LicenseVerifySubscriber>();

    // 註冊條件控制服務
    builder.Services.AddHostedService<ConditionControlService>();

    // 註冊 LogicFlow 後端執行服務
    builder.Services.AddHostedService<LogicFlowExecutionService>();

    // 註冊警報規則 Reload 訂閱服務（聽 Web 端規則異動 MQTT 通知）
    builder.Services.AddHostedService<AlarmRuleReloadSubscriber>();

    // 註冊簡訊盒 MQTT 橋接服務（發布狀態 + 代 Web 執行測試發送/重掃 port）
    builder.Services.AddHostedService<SmsMqttBridge>();

    // 通知設定檔熱重載訂閱（SCADA/Sys/Notify/Reload → 重讀 Shared 的 Email / Sms / Line 設定，不重啟 Engine）
    builder.Services.AddHostedService<NotifySettingReloadSubscriber>();

    // 註冊 DB 來源通訊服務（雙註冊：Singleton 供 ReloadSubscriber 注入呼叫 + HostedService 啟動 polling）
    builder.Services.AddHostedService<DbCommunicationService>(provider =>
        provider.GetRequiredService<DbCommunicationService>());

    // 註冊 DB 來源 Reload 訂閱服務（聽 Web 端 JSON 異動 MQTT 通知）
    builder.Services.AddHostedService<DbCoordinatorReloadSubscriber>();

    // 註冊 OPC UA 來源通訊服務（雙註冊：Singleton 供 ReloadSubscriber / 控制分流注入呼叫 + HostedService 啟動 polling）
    builder.Services.AddHostedService<OpcUaCommunicationService>(provider =>
        provider.GetRequiredService<OpcUaCommunicationService>());

    // 註冊 OPC UA 來源 Reload 訂閱服務（聽 Web 端 JSON 異動 MQTT 通知）
    builder.Services.AddHostedService<OpcUaReloadSubscriber>();

    // 註冊葉子層 hourly 預聚合 — 純邏輯 + 資料存取
    builder.Services.AddSingleton<EnergyLeafHourlyRepository>();
    builder.Services.AddSingleton<EnergyLeafAggregator>();

    // 註冊葉子層 hourly 預聚合背景服務（XX:02 觸發 + 啟動 catch-up）
    builder.Services.AddHostedService<EnergyLeafAggregationService>();

    // 註冊葉子層 Backfill MQTT 訂閱服務（接 SCADA/Sys/EnergyLeafHourly/Backfill）
    builder.Services.AddHostedService<EnergyLeafBackfillSubscriber>();

    // 註冊冷凍噸葉子層 hourly 預聚合（AVG × 1h，與電表預聚合對稱）
    builder.Services.AddSingleton<WaterLeafHourlyRepository>();
    builder.Services.AddSingleton<WaterLeafAggregator>();
    builder.Services.AddHostedService<WaterLeafAggregationService>();

    // 註冊水表葉子層 hourly 預聚合（boundary 相減 + MaxVolume 溢位 + UnitScale 換算，與電表預聚合對稱）
    builder.Services.AddSingleton<WaterMeterLeafHourlyRepository>();
    builder.Services.AddSingleton<WaterMeterLeafAggregator>();
    builder.Services.AddHostedService<WaterMeterLeafAggregationService>();

    // 註冊氣表葉子層 hourly 預聚合（XX:03 觸發，與電表/水表錯開；演算法與水表對稱但完全獨立）
    builder.Services.AddSingleton<GasMeterLeafHourlyRepository>();
    builder.Services.AddSingleton<GasMeterLeafAggregator>();
    builder.Services.AddHostedService<GasMeterLeafAggregationService>();

    // 註冊產氣量表葉子層 hourly 預聚合（空壓機 Nm³ totalizer，XX:04 觸發；演算法與氣表對稱但完全獨立，只供空壓機能源申報）
    builder.Services.AddSingleton<AirMeterLeafHourlyRepository>();
    builder.Services.AddSingleton<AirMeterLeafAggregator>();
    builder.Services.AddHostedService<AirMeterLeafAggregationService>();

    // 註冊需量計算服務（每分鐘計算各電表迴路 15min 滑動 TWA 功率）
    builder.Services.AddHostedService<DemandCalculatorService>();

    // 註冊迴路用電虛擬點位服務（每 5 分鐘計算各迴路每日/每月/期別累積 kWh，NRGD-/NRGM-/NRGP- 三路發布）
    builder.Services.AddHostedService<CircuitEnergyVirtualPointService>();

    // 註冊每週資料庫自動備份服務（DbMaintenanceSetting.json 排程，A/B 兩檔輪替，不依賴 SQL Agent）
    builder.Services.AddHostedService<ScadaEngine.Engine.Data.Services.DatabaseBackupService>();

    var host = builder.Build();

    // 初始化資料庫服務
    await host.Services.InitializeDatabaseAsync();

    // 初始化 MQTT 連線
    await host.Services.InitializeMqttServiceAsync();

    Log.Information("SCADA 引擎服務初始化完成，開始運行");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SCADA 引擎服務啟動失敗");
}
finally
{
    Log.Information("SCADA 引擎服務已停止");
    await Log.CloseAndFlushAsync();
}
