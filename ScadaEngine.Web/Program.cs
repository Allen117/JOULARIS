using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Data.Services;
using ScadaEngine.Engine.Data.Repositories;
using ScadaEngine.Engine.Communication.Mqtt;
using ScadaEngine.Engine.Communication.Modbus.Services;
using ScadaEngine.Web.Services;
using Serilog;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// 支援以 Windows Service 模式執行
builder.Host.UseWindowsService();

// 設定監聽埠（部署時使用，launchSettings.json 只在開發時有效）
// HTTP 一律開；HTTPS 僅在 certs/scada-web.pfx 存在時啟用（自簽憑證，見 certs/generate-https-cert.ps1）
var szPfxPath = Path.Combine(builder.Environment.ContentRootPath, "certs", "scada-web.pfx");
var isHttpsEnabled = File.Exists(szPfxPath);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5038); // HTTP — 同網段 http://<本機IP>:5038
    if (isHttpsEnabled)
    {
        // HTTPS — 同網段 https://<本機IP>:7189（自簽，client 首次連需按「繼續前往」）
        options.ListenAnyIP(7189, listen => listen.UseHttps(szPfxPath, "ScadaWeb"));
    }
});

// 有憑證才強制 HTTPS 轉址（無憑證的部署維持純 HTTP，不會被導去死掉的埠）
if (isHttpsEnabled)
{
    builder.Services.AddHttpsRedirection(options => options.HttpsPort = 7189);
}

// 配置 Serilog 日誌：appsettings 有 "Serilog" 區段就照它；沒有（預設）則比照 Engine / ModbusServer —
// 服務模式寫 Log\Web-.log（以 BaseDirectory 組絕對路徑，服務 CWD 是 system32）、開發模式印 Console。
// 沒這段時 ILogger 零輸出，連「資料庫設定檔路徑」這種排障必看的啟動 log 都看不到。
builder.Host.UseSerilog((context, config) =>
{
    config.ReadFrom.Configuration(context.Configuration);
    if (context.Configuration.GetSection("Serilog").Exists()) return;

    config.MinimumLevel.Information()
          .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
          .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning);
    if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    {
        config.WriteTo.File(
            path: Path.Combine(AppContext.BaseDirectory, "Log", "Web-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    }
    else
    {
        config.WriteTo.Console();
    }
});

// 多語系：resx 集中放在 Resources/，IStringLocalizer / IViewLocalizer / DataAnnotations 都吃這份
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Add services to the container.
builder.Services
    .AddControllersWithViews(options =>
    {
        options.Filters.Add<ScadaEngine.Web.Filters.PageAccessFilter>();
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options =>
    {
        options.DataAnnotationLocalizerProvider = (type, factory) =>
            factory.Create(typeof(ScadaEngine.Web.Resources.SharedResource));
    });

// 支援的語系（zh-TW 為預設，缺 key 時 fallback 至 zh-TW）
var aSupportedCultures = new[]
{
    new CultureInfo("zh-TW"),
    new CultureInfo("en")
};
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("zh-TW", "zh-TW");
    options.SupportedCultures = aSupportedCultures;
    options.SupportedUICultures = aSupportedCultures;
    options.FallBackToParentUICultures = true;

    // Provider 順序：Cookie → QueryString → AcceptLanguage
    options.RequestCultureProviders.Clear();
    options.RequestCultureProviders.Add(new CookieRequestCultureProvider());
    options.RequestCultureProviders.Add(new QueryStringRequestCultureProvider());
    options.RequestCultureProviders.Add(new AcceptLanguageHeaderRequestCultureProvider());
});

// 配置視圖引擎以支援 Features 資料夾結構
builder.Services.Configure<Microsoft.AspNetCore.Mvc.Razor.RazorViewEngineOptions>(options =>
{
    options.ViewLocationFormats.Clear();
    options.ViewLocationFormats.Add("/Views/{1}/{0}.cshtml");
    options.ViewLocationFormats.Add("/Views/Shared/{0}.cshtml");
    options.ViewLocationFormats.Add("/Features/{1}/Views/{0}.cshtml");
});

// 配置 Cookie 認證
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login/Index";
        options.LogoutPath = "/Login/Logout";
        options.AccessDeniedPath = "/Login/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(4);
        options.SlidingExpiration = true;
        options.Cookie.Name = "ScadaAuth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // /api/* 角色不符回 403（預設 302 導 AccessDenied 頁，fetch 會誤拿 200 HTML）
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });

// 全域授權後備：預設所有端點都要登入，未標 [AllowAnonymous] 者一律擋
// （一次堵住所有「忘了加 [Authorize]」的匿名破口；白名單：Login / I18n / Setup / 靜態檔）
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// 註冊資料庫配置服務 — dbSetting.json 路徑由 Common 的 DbSettingLocator 統一解析
// （環境變數 → ..\..\Shared → 本機 Setting → 開發 repo；與 Engine / ModbusServer 同一套，見 docs/架構.md）
builder.Services.AddSingleton<DatabaseConfigService>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<DatabaseConfigService>>();
    return new DatabaseConfigService(logger, DbSettingLocator.Resolve());
});

builder.Services.AddSingleton<DatabaseSchemaService>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<DatabaseSchemaService>>();
    var localSchema = Path.Combine(AppContext.BaseDirectory, "DatabaseSchema", "DatabaseSchema.json");
    var engineSchema = Path.Combine("..", "ScadaEngine.Engine", "DatabaseSchema", "DatabaseSchema.json");
    var schemaPath = File.Exists(localSchema) ? localSchema : engineSchema;
    return new DatabaseSchemaService(logger, schemaPath);
});
builder.Services.AddSingleton<DatabaseInitializationService>();
builder.Services.AddScoped<IDataRepository, SqlServerDataRepository>();

// 註冊 Engine 專案的通訊服務配置服務（Web 即時監控需要）
builder.Services.AddSingleton<MqttConfigService>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<MqttConfigService>>();
    var localMqttConfig = Path.Combine(AppContext.BaseDirectory, "MqttSetting", "MqttSetting.json");
    var devMqttConfig = Path.Combine("MqttSetting", "MqttSetting.json");
    var mqttConfigPath = File.Exists(localMqttConfig) ? localMqttConfig : devMqttConfig;
    return new MqttConfigService(logger, mqttConfigPath);
});
// Web 專案不需要 ModbusConfigService，僅訂閱 MQTT 即時資料
// builder.Services.AddSingleton<ModbusConfigService>(...);

// 暫時註釋通訊服務，避免啟動時死鎖
/*
// 註冊通訊服務 (延遲實例化，避免啟動時立即連線)
builder.Services.AddTransient<MqttPublishService>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<MqttPublishService>>();
    var mqttConfigService = serviceProvider.GetRequiredService<MqttConfigService>();
    // 移除同步等待，改為在使用時才載入配置
    return new MqttPublishService(logger, null);
});

builder.Services.AddTransient<MqttControlSubscribeService>();
builder.Services.AddTransient<ModbusCollectionManager>();
*/

//註冊Web專用服務（用於登入功能與即時監控）
builder.Services.AddScoped<ScadaEngine.Web.Services.WebDatabaseService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EventLogService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.LogicFlowService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.AlarmRuleService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.AccountSettingService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.CalcPointService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.ScheduleSettingService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.LineTargetService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.SmsTargetService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EmailGroupService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnergyCircuitService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterCircuitService>();
// 月結週期（期別）— 電費與水費各一套，彼此獨立（內含各自的 static 快取，寫入時失效）
builder.Services.AddScoped<ScadaEngine.Web.Services.BillingPeriodService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterBillingPeriodService>();
// 氣費期別 — 第三套，比電/水兩套多「刪除此期（IsSkipped）」以支援兩月一期
builder.Services.AddScoped<ScadaEngine.Web.Services.GasBillingPeriodService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnergyReportService>();
// ISO 50001 能源基準 — OLS 回歸引擎（純數學無狀態 → Singleton）+ 基線模型/取樣/SEU + EnPI 報告
builder.Services.AddSingleton<ScadaEngine.Web.Services.BaselineRegressionEngine>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnergyBaselineService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnPIReportService>();
// 虛擬主要電表 V/I/P/PF 聚合器（依 EnergyCircuitService + MqttRealtimeSubscriberService；Scoped 對齊 EnergyCircuitService）
builder.Services.AddScoped<ScadaEngine.Web.Services.MainMeterAggregationService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.RefrigerationTonReportService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnergyDeclarationService>();
// 電費設定 — 台電電價方案（SystemSettings JSON + Setting/tariff-taipower-defaults.json seed）
builder.Services.AddScoped<ScadaEngine.Web.Services.TariffSettingService>();
// 國定假日設定 — Holidays 表讀寫（static 快取，寫入時失效；TOU 計價 DayType 判定用）
builder.Services.AddScoped<ScadaEngine.Web.Services.HolidayService>();
// 班別設定 — SystemSettings JSON 讀寫 + 日期範圍展開（電/水/氣用量報表「班別」粒度共用）
builder.Services.AddScoped<ScadaEngine.Web.Services.ShiftScheduleService>();
// 電費計算核心 — 逐時計價 + EMS 電費狀態卡查詢彙總
builder.Services.AddScoped<ScadaEngine.Web.Services.ElectricityCostService>();
// 電費逐時計價背景服務（每小時 XX:05 重算近 48h rolling window，啟動 catch-up 近 N 天）
builder.Services.AddHostedService<ScadaEngine.Web.Services.ElectricityCostAggregationService>();
// EMS 首頁卡片顯示設定 — EmsCardRegistry merge DB 覆寫（/EmsCardSetting 維護、/EMS 渲染共用）
builder.Services.AddScoped<ScadaEngine.Web.Services.EmsCardSettingService>();
// 能源日報 — 聚合四報表 + 比較 + 規則式提示 → 快照；02:00 排程生成 + 寄送（/DailyReport 瀏覽、/DailyReportSetting 設定）
builder.Services.AddScoped<ScadaEngine.Web.Services.DailyReportService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.DailyReportInsightService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.DailyReportHtmlBuilder>();
builder.Services.AddScoped<ScadaEngine.Web.Services.DailyReportMailService>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.DailyReportSchedulerService>();
// 水表（累積式 m³/L）— 迴路樹 CRUD + 用水報表（WaterMeterLeafHourly 加總）+ 台水流動水費（SystemSettings water_tariff）
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterMeterCircuitService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterUsageReportService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterTariffService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterCostService>();
// 氣表（累積式天然氣 m³/Nm³/度）— 迴路樹 CRUD + 用氣報表（GasMeterLeafHourly 加總）+ 天然氣流動氣費（SystemSettings gas_tariff）
builder.Services.AddScoped<ScadaEngine.Web.Services.GasMeterCircuitService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.GasUsageReportService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.GasTariffService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.GasCostService>();
// 產氣量表（空壓機累積式 Nm³）— 迴路樹 CRUD + 曆月產氣量（AirMeterLeafHourly 加總）；與氣表平行、互不相干
builder.Services.AddScoped<ScadaEngine.Web.Services.AirMeterCircuitService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.AirMeterUsageReportService>();
// 空壓機能源申報 — 用電迴路 kWh + 產氣量表 Nm³ 成對 → 年度 12 曆月比能耗 kWh/Nm³
builder.Services.AddScoped<ScadaEngine.Web.Services.AirCompressorDeclarationService>();
// 契約容量最佳化 — DemandData 固定 15 分鐘窗口每月最高需量 × 電費設定單價 / 契約歷史 → 回測保守建議與最低成本點
builder.Services.AddScoped<ScadaEngine.Web.Services.ContractCapacityService>();
// 氣象資料來源 — CWA API client（Singleton：測站清單快取）+ 設定表讀寫（Scoped）+ 背景抓取（寫 Weather DBLatestData）
builder.Services.AddSingleton<ScadaEngine.Web.Services.WeatherCwaClient>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WeatherSettingService>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.WeatherFetchService>();
// ScadaPage 累積量元件 — 任意 SID 當日/當月累積（meter 差值 / integrate 積分）+ 分層 bucket 快取
builder.Services.AddSingleton<ScadaEngine.Web.Services.WidgetAccumulationCache>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WidgetAccumulationService>();
// ScadaPage 迴路指標元件 — 本日/本月度數（EnergyReport 同核心）+ 本期電度/電費（EMS 電費卡同源）+ 60s 結果快取
builder.Services.AddSingleton<ScadaEngine.Web.Services.WidgetCircuitMetricCache>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WidgetCircuitMetricService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.DbCoordinatorService>();
// DB 來源點位名稱熱編輯 — 回寫 DBPoint/*.json + UPSERT DBPoints（Scoped：依賴 IDataRepository 與 IStringLocalizer）
builder.Services.AddScoped<ScadaEngine.Web.Services.DbPointConfigFileService>();
// OPC UA 來源設定（Scoped：依賴 IDataRepository 與 IStringLocalizer）
builder.Services.AddScoped<ScadaEngine.Web.Services.OpcUaCoordinatorService>();
// Designer 列範本 JSON 讀寫（Singleton：內含 SemaphoreSlim 檔案鎖）
builder.Services.AddSingleton<ScadaEngine.Web.Services.DesignerTemplateService>();

// 來源設定 JSON 共用檔案 I/O（Modbus / DBPoint：路徑防護、原子寫檔、鏡像、刪除備份）— 熱編輯與 Excel 匯入共用
builder.Services.AddSingleton<ScadaEngine.Web.Services.SourceExcel.SourceConfigFileIo>();
// Modbus 點位熱編輯 — 讀寫 Engine 執行目錄 Modbus JSON（原子替換，Engine watcher 自動重載）
builder.Services.AddSingleton<ScadaEngine.Web.Services.ModbusConfigFileService>();
// Excel 匯入（Modbus / DB 來源）：adapter 為純解析/序列化（Singleton）；Coordinator 依賴 IStringLocalizer → Scoped；預覽暫存走 IMemoryCache
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ScadaEngine.Web.Services.SourceExcel.ModbusExcelAdapter>();
builder.Services.AddSingleton<ScadaEngine.Web.Services.SourceExcel.DbPointExcelAdapter>();
builder.Services.AddSingleton<ScadaEngine.Web.Services.SourceExcel.OpcUaExcelAdapter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.SourceExcel.SourceExcelImportCoordinator>();
// Scoped：依賴 IStringLocalizer<T>（Scoped），且 exporter 本身無狀態
builder.Services.AddScoped<ScadaEngine.Web.Services.EnergyReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.RefrigerationTonReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnergyDeclarationExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.AirCompressorDeclarationExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.ContractCapacityExcelExporter>();
// 歷史趨勢：查詢 + 統計（/api/history/data 與匯出共用）、匯出 Excel（統計 / 圖表分頁）、工程師專屬系統參數（SystemSettings system_parameters）
builder.Services.AddScoped<ScadaEngine.Web.Services.HistoryQueryService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.HistoryExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.SystemParameterService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.ElectricityCostReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EnPIReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterUsageReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.WaterCostReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.GasUsageReportExcelExporter>();
builder.Services.AddScoped<ScadaEngine.Web.Services.GasCostReportExcelExporter>();

// Line 測試發送（內含 throttle 字典 → 必須 Singleton 才能跨請求保留狀態）
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ScadaEngine.Web.Services.LineTestSendService>();

// Email 設定檔讀寫 + 測試寄送（Singleton：throttle 字典需跨請求保留）
builder.Services.AddSingleton<ScadaEngine.Web.Services.EmailSenderConfigService>();
builder.Services.AddSingleton<ScadaEngine.Web.Services.EmailTestSendService>();

// 簡訊設定檔讀寫 + 測試發送（Singleton：throttle 字典需跨請求保留；實際發送由 Engine 經 MQTT 代執行）
builder.Services.AddSingleton<ScadaEngine.Web.Services.SmsSenderConfigService>();
builder.Services.AddSingleton<ScadaEngine.Web.Services.SmsTestSendService>();

// 註冊 C# 演算法服務（供 LogicFlow 前端預覽用）
builder.Services.AddSingleton<ScadaEngine.Engine.Services.CSharpAlgorithmService>();

// 多語系：把 .resx 打包成 JSON 給前端 i18n.js（Singleton 內部有 ConcurrentDictionary 快取）
builder.Services.AddSingleton<ScadaEngine.Web.Services.I18nResourceService>();

// 全站搜尋索引（Ctrl+K 命令面板 — 註冊表 + 雙語標題，依登入者權限過濾）
builder.Services.AddSingleton<ScadaEngine.Web.Services.GlobalSearchService>();

// 警報訊息 i18n 翻譯器（依當前 culture 套 messageKey + args → 顯示字串）
builder.Services.AddScoped<ScadaEngine.Web.Services.AlarmMessageLocalizer>();

// ScadaPage 控制動作 EventLog 寫入器（EventType=3 資訊）
builder.Services.AddScoped<ScadaEngine.Web.Services.ControlEventLogger>();

// 授權狀態快取（Singleton — 由 MqttRealtimeSubscriberService 更新，Controller/Layout 讀取）
builder.Services.AddSingleton<ScadaEngine.Web.Services.LicenseStatusCache>();

// 簡訊盒狀態快取（Singleton — 由 MqttRealtimeSubscriberService 訂閱 SCADA/Sys/Sms/Status 更新）
builder.Services.AddSingleton<ScadaEngine.Web.Services.SmsStatusCache>();

// 通知設定熱重載結果快取（Singleton — 由 MqttRealtimeSubscriberService 訂閱 SCADA/Sys/Notify/Status 更新，存檔請求依 requestId 等結果）
builder.Services.AddSingleton<ScadaEngine.Web.Services.NotifyReloadStatusCache>();

// 註冊即時監控 MQTT 訂閱服務
builder.Services.AddSingleton<ScadaEngine.Web.Services.MqttRealtimeSubscriberService>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.MqttRealtimeSubscriberService>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.MqttRealtimeSubscriberService>());

// 註冊未恢復警報 MQTT 訂閱服務（雙註冊：Singleton + HostedService）
builder.Services.AddSingleton<ScadaEngine.Web.Services.MqttAlarmSubscriberService>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.MqttAlarmSubscriberService>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.MqttAlarmSubscriberService>());

// 註冊警報規則 Reload 發布者（雙註冊：Singleton 供 AlarmRuleService 注入呼叫 + HostedService 啟動時連 broker）
builder.Services.AddSingleton<ScadaEngine.Web.Services.AlarmRuleReloadPublisher>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.AlarmRuleReloadPublisher>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.AlarmRuleReloadPublisher>());

// 註冊簡訊命令發布者（雙註冊：Singleton 供 Controller / SmsTestSendService 注入呼叫 + HostedService 啟動時連 broker）
builder.Services.AddSingleton<ScadaEngine.Web.Services.SmsCommandPublisher>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.SmsCommandPublisher>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.SmsCommandPublisher>());

// 通知設定熱重載發布者（Web 存 Email / Sms 設定後發 SCADA/Sys/Notify/Reload，Engine 重讀 Shared 檔、不重啟）
builder.Services.AddSingleton<ScadaEngine.Web.Services.NotifySettingReloadPublisher>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.NotifySettingReloadPublisher>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.NotifySettingReloadPublisher>());

// 註冊 DB 來源 Reload 發布者（雙註冊：Singleton 供 Controller 注入呼叫 + HostedService 啟動時連 broker）
builder.Services.AddSingleton<ScadaEngine.Web.Services.DbCoordinatorReloadPublisher>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.DbCoordinatorReloadPublisher>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.DbCoordinatorReloadPublisher>());

// 註冊 OPC UA 來源 Reload 發布者（雙註冊：Singleton 供 Controller 注入呼叫 + HostedService 啟動時連 broker）
builder.Services.AddSingleton<ScadaEngine.Web.Services.OpcUaReloadPublisher>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.OpcUaReloadPublisher>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.OpcUaReloadPublisher>());

// 設備效率診斷（冰機 / 空壓機）— 設備主檔 CRUD + 即時指標（MQTT 快取）+ 期間儀表板（EquipmentHourly）
builder.Services.AddScoped<ScadaEngine.Web.Services.EquipmentProfileService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EquipmentRealtimeService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.EquipmentEfficiencyReportService>();
// 設備效率「回填歷史」發布者（雙註冊：Singleton 供 Controller 注入呼叫 + HostedService 啟動時連 broker）
builder.Services.AddSingleton<ScadaEngine.Web.Services.EquipmentBackfillPublisher>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.EquipmentBackfillPublisher>(provider =>
    provider.GetRequiredService<ScadaEngine.Web.Services.EquipmentBackfillPublisher>());
// 空壓系統規則式診斷（移植 compressor-analysis-skill 領域規則，不經 LLM）— 設定 / 快照 / 每日 02:00 最近 7 天滾動快照
builder.Services.AddScoped<ScadaEngine.Web.Services.CompressorDiagnosisService>();
builder.Services.AddScoped<ScadaEngine.Web.Services.CompressorDiagnosisTextRenderer>();
builder.Services.AddHostedService<ScadaEngine.Web.Services.CompressorDiagnosisSchedulerService>();

var app = builder.Build();

Console.WriteLine("正在啟動 SCADA Web 應用程式...");

// 通知設定檔（Email / Sms / Line）路徑：三個 Singleton 建構時各印一行「通知設定檔路徑 … 來源」，
// 這裡強制建構，讓啟動 log 就能與 Engine 的三行比對是否同一份（否則要等第一次用到才印）
app.Services.GetRequiredService<ScadaEngine.Web.Services.EmailSenderConfigService>();
app.Services.GetRequiredService<ScadaEngine.Web.Services.SmsSenderConfigService>();
app.Services.GetRequiredService<ScadaEngine.Web.Services.LineTestSendService>();

// 啟動時同步 DB 結構（schema-driven 建表 + 補缺欄位）— 消滅 Engine/Web 部署順序依賴，誰先重啟誰補。
// 失敗僅 log 不擋啟動（DB 連不上時 Web 照常起，之後 Engine 或手動初始化可補）
try
{
    var dbInitLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var dbInitService = app.Services.GetRequiredService<DatabaseInitializationService>();
    var isDbSchemaSynced = await dbInitService.InitializeDatabaseSchemaAsync();
    if (!isDbSchemaSynced)
    {
        dbInitLogger.LogWarning("Web 啟動時資料庫結構同步失敗（不擋啟動）— 請確認 DB 連線與 DatabaseSchema.json");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Web 啟動時資料庫結構同步發生錯誤（不擋啟動）: {ex.Message}");
}

try
{
    Console.WriteLine("配置 HTTP 請求管道...");
    
    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    Console.WriteLine("設置中介軟體...");
    if (isHttpsEnabled)
    {
        app.UseHttpsRedirection();
    }
    app.UseStaticFiles();
    app.UseRouting();

    // 多語系中介軟體（位置：UseRouting 之後、UseAuthentication 之前）
    var localizationOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value;
    app.UseRequestLocalization(localizationOptions);

    // 認證和授權中介軟體
    app.UseAuthentication();
    app.UseAuthorization();

    Console.WriteLine("設置路由...");
    // 根路徑重導向至登入頁（未登入也可達，交由 Login 流程處理）
    app.MapGet("/", () => Results.Redirect("/Login")).AllowAnonymous();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Login}/{action=Index}/{id?}");

    Console.WriteLine("準備啟動 Web 伺服器...");
    Console.WriteLine("應用程式將在以下 URL 可用:");
    Console.WriteLine("- HTTP : http://<本機IP>:5038");
    if (isHttpsEnabled)
    {
        Console.WriteLine("- HTTPS: https://<本機IP>:7189（自簽憑證，內網 client 首次連需按「繼續前往」）");
    }
    else
    {
        Console.WriteLine("- HTTPS: 未啟用（缺 certs/scada-web.pfx，執行 certs/generate-https-cert.ps1 產生）");
    }
    Console.WriteLine("啟動 Web 伺服器...");

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"啟動應用程式時發生錯誤: {ex.Message}");
    Console.WriteLine($"完整錯誤信息: {ex}");
    throw;
}
