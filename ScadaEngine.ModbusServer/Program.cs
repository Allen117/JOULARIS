using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.ModbusServer.Core;
using ScadaEngine.ModbusServer.Models;
using ScadaEngine.ModbusServer.Services;
using Serilog;

// Windows Service 模式 CWD 是 system32 — 統一切到執行檔目錄，之後全部走相對路徑
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var isWindowsService = WindowsServiceHelpers.IsWindowsService();

// Serilog：服務模式檔案日誌、開發模式 Console（仿 Engine Program.cs）
var logConfigBuilder = new LoggerConfiguration();
if (isWindowsService)
{
    logConfigBuilder
        .WriteTo.File(
            path: Path.Combine(AppContext.BaseDirectory, "Log", "ModbusGateway-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: Path.Combine(AppContext.BaseDirectory, "Log", "ModbusGateway-Error-.log"),
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 90)
        .MinimumLevel.Information();
}
else
{
    logConfigBuilder
        .WriteTo.Console()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning);
}
Log.Logger = logConfigBuilder.CreateLogger();

try
{
    Log.Information("SCADA Modbus Gateway 啟動中... (模式: {ServiceMode})",
        isWindowsService ? "Windows Service" : "Console Application");

    // 設定載入（啟動時一次；改設定需重啟服務）
    var setting = LoadJsonFile<ModbusServerSettingModel>("./Setting/ModbusServerSetting.json") ?? new ModbusServerSettingModel();
    var mqttSetting = LoadJsonFile<MqttSettingRootModel>("./MqttSetting/MqttSetting.json") ?? new MqttSettingRootModel();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    if (isWindowsService)
    {
        builder.Services.AddWindowsService(options => options.ServiceName = "JOULARIS Modbus Gateway");
    }

    builder.Host.UseSerilog();
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(setting.nWebPort));

    builder.Services.AddSingleton(setting);
    builder.Services.AddSingleton(mqttSetting);
    // dbSetting.json 路徑由 Common 的 DbSettingLocator 統一解析（環境變數 → ..\..\Shared → 本機 Setting → 開發 repo）
    // 同機裝在 C:\SCADA\ModbusServer\App 時讀 C:\SCADA\Shared\；裝在別台機器時落到本機 Setting\dbSetting.json
    builder.Services.AddSingleton(sp =>
        new DatabaseConfigService(sp.GetRequiredService<ILogger<DatabaseConfigService>>(), DbSettingLocator.Resolve()));
    builder.Services.AddSingleton<GatewayRepository>();
    builder.Services.AddSingleton<PointCatalogService>();

    // 雙註冊：Singleton 供 API 注入 + HostedService 跑背景迴圈（專案慣例）
    builder.Services.AddSingleton<RealtimeCacheService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RealtimeCacheService>());
    builder.Services.AddSingleton<ModbusTcpServerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ModbusTcpServerService>());

    var app = builder.Build();

    // ── 內建瀏覽網頁（唯讀工具頁，不做登入 — 靠防火牆/內網管控）──
    app.MapGet("/", () =>
        Results.File(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"), "text/html; charset=utf-8"));

    // 對照表 + 即時值（前端每 5 秒輪詢；值/狀態與 Modbus 供值同一套規則）
    app.MapGet("/api/points", (PointCatalogService catalog, RealtimeCacheService cache,
        ModbusTcpServerService modbus, ModbusServerSettingModel cfg) =>
    {
        var entries = catalog.GetSnapshot();
        var activeSids = catalog.GetActiveSids();
        var isCatalogKnown = catalog.HasScannedOk;
        var dtNow = DateTime.Now;

        var points = entries.Select(entry =>
        {
            cache.TryGetValue(entry.szSID, out var item);
            var isActive = !isCatalogKnown || activeSids.Contains(entry.szSID);
            var fServed = ServedValueRules.Compute(item, isActive, cfg.IsHoldLast, cfg.nStaleSeconds, dtNow);
            return new
            {
                address = entry.nAddress,
                // 6 位數擴充表示法（300001+）：位址段超過 9999，4 位數 3xxxx 表示法不夠用
                address3x = 300001 + entry.nAddress,
                sid = entry.szSID,
                name = entry.szName,
                source = entry.szSource,
                unit = entry.szUnit,
                rawAddress = entry.szRawAddress,
                value = float.IsNaN(fServed) ? (float?)null : fServed,
                status = ServedValueRules.GetStatusLabel(item, isActive, cfg.nStaleSeconds, dtNow),
                timestamp = item != null && item.hasData && item.dtTimestamp > DateTime.MinValue
                    ? item.dtTimestamp.ToString("yyyy-MM-dd HH:mm:ss")
                    : null,
            };
        });

        return Results.Json(new
        {
            server = new
            {
                modbusPort = cfg.nModbusListenPort,
                webPort = cfg.nWebPort,
                unitId = cfg.nUnitId,
                wordOrder = FloatRegisterEncoder.ParseWordOrder(cfg.szWordOrder).ToString(),
                badQualityMode = cfg.IsHoldLast ? "HoldLast" : "NaN",
                modbusRunning = modbus.IsRunning,
                mqttConnected = cache.IsConnected,
                catalogScanned = isCatalogKnown,
                lastScanAt = catalog.dtLastScanAt > DateTime.MinValue
                    ? catalog.dtLastScanAt.ToString("yyyy-MM-dd HH:mm:ss")
                    : null,
                totalPoints = entries.Length,
                activePoints = isCatalogKnown ? entries.Count(e => activeSids.Contains(e.szSID)) : entries.Length,
            },
            points,
        });
    });

    // 重新掃描點位（append-only：既有位址不變，新點附加在該來源段尾）
    app.MapPost("/api/rescan", async (RealtimeCacheService cache) =>
    {
        try
        {
            var (nAdded, nTotal) = await cache.RescanAsync();
            return Results.Json(new { ok = true, added = nAdded, total = nTotal });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "手動重掃點位失敗");
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
        }
    });

    Log.Information("SCADA Modbus Gateway 初始化完成：Modbus TCP :{ModbusPort} / 瀏覽網頁 http://localhost:{WebPort}",
        setting.nModbusListenPort, setting.nWebPort);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SCADA Modbus Gateway 啟動失敗");
}
finally
{
    Log.Information("SCADA Modbus Gateway 已停止");
    await Log.CloseAndFlushAsync();
}

static T? LoadJsonFile<T>(string szPath) where T : class
{
    if (!File.Exists(szPath))
    {
        Log.Warning("設定檔不存在: {Path}，使用預設值", szPath);
        return null;
    }
    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
    return JsonSerializer.Deserialize<T>(File.ReadAllText(szPath), options);
}
