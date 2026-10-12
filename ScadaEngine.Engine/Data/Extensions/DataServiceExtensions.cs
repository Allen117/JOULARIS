using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Engine.Data.Repositories;
using ScadaEngine.Engine.Services;
using ScadaEngine.Engine.Communication.Mqtt;
using System.IO;
using ScadaEngine.Engine.Data.Services;
namespace ScadaEngine.Engine.Data.Extensions;

/// <summary>
/// 資料庫服務依賴注入擴充方法
/// </summary>
public static class DataServiceExtensions
{
    /// <summary>
    /// 資料服務擴充方法
    /// </summary>
    /// <param name="services">服務集合</param>
    /// <returns>服務集合</returns>
    public static IServiceCollection AddDataServices(this IServiceCollection services)
    {
        // 註冊資料庫配置服務為單例 — 路徑由 Common 的 DbSettingLocator 統一解析
        // （環境變數 → ..\..\Shared → 本機 Setting → 開發 repo；Engine / Web / ModbusServer 三者同一套，見 docs/架構.md）
        services.AddSingleton<DatabaseConfigService>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<DatabaseConfigService>>();
            return new DatabaseConfigService(logger, DbSettingLocator.Resolve());
        });

        // 註冊資料庫綱要服務為單例 - 智慧路徑偵測
        services.AddSingleton<DatabaseSchemaService>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<DatabaseSchemaService>>();
            
            // 使用應用程式基礎目錄作為設定檔路徑
            var szBasePath = AppDomain.CurrentDomain.BaseDirectory;
            var szSchemaPath = Path.Combine(szBasePath, "DatabaseSchema", "DatabaseSchema.json");
            
            // 記錄路徑偵測結果
            logger.LogInformation("資料庫綱要服務初始化");
            logger.LogInformation("應用程式基礎目錄: {BasePath}", szBasePath);
            logger.LogInformation("資料庫綱要檔路徑: {SchemaPath}", szSchemaPath);
            logger.LogInformation("綱要檔是否存在: {FileExists}", File.Exists(szSchemaPath));
            
            // 如果檔案不存在，嘗試備用路徑
            if (!File.Exists(szSchemaPath))
            {
                // 備用路徑1: 開發模式的相對路徑
                var szFallbackPath1 = Path.Combine(Directory.GetCurrentDirectory(), "DatabaseSchema", "DatabaseSchema.json");
                if (File.Exists(szFallbackPath1))
                {
                    logger.LogInformation("使用備用路徑1: {FallbackPath}", szFallbackPath1);
                    szSchemaPath = szFallbackPath1;
                }
                else
                {
                    // 備用路徑2: 專案根目錄
                    var szProjectRoot = Path.GetDirectoryName(szBasePath);
                    while (szProjectRoot != null && !Directory.GetFiles(szProjectRoot, "*.csproj").Any())
                    {
                        szProjectRoot = Path.GetDirectoryName(szProjectRoot);
                    }
                    
                    if (szProjectRoot != null)
                    {
                        var szFallbackPath2 = Path.Combine(szProjectRoot, "DatabaseSchema", "DatabaseSchema.json");
                        if (File.Exists(szFallbackPath2))
                        {
                            logger.LogInformation("使用備用路徑2 (專案根目錄): {FallbackPath}", szFallbackPath2);
                            szSchemaPath = szFallbackPath2;
                        }
                        else
                        {
                            logger.LogWarning("所有路徑都找不到資料庫綱要檔，將使用預設配置");
                        }
                    }
                    else
                    {
                        logger.LogWarning("無法找到專案根目錄，將使用預設配置");
                    }
                }
            }
            
            return new DatabaseSchemaService(logger, szSchemaPath);
        });

        // 註冊 MQTT 配置服務為單例 - 智慧路徑偵測
        services.AddSingleton<MqttConfigService>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<MqttConfigService>>();
            
            // 使用應用程式基礎目錄作為設定檔路徑
            var szBasePath = AppDomain.CurrentDomain.BaseDirectory;
            var szMqttConfigPath = Path.Combine(szBasePath, "MqttSetting", "MqttSetting.json");
            
            // 記錄路徑偵測結果
            logger.LogInformation("MQTT 配置服務初始化");
            logger.LogInformation("應用程式基礎目錄: {BasePath}", szBasePath);
            logger.LogInformation("MQTT 設定檔路徑: {ConfigPath}", szMqttConfigPath);
            logger.LogInformation("MQTT 設定檔是否存在: {FileExists}", File.Exists(szMqttConfigPath));
            
            // 如果檔案不存在，嘗試備用路徑
            if (!File.Exists(szMqttConfigPath))
            {
                // 備用路徑1: 開發模式的相對路徑
                var szFallbackPath1 = Path.Combine(Directory.GetCurrentDirectory(), "MqttSetting", "MqttSetting.json");
                if (File.Exists(szFallbackPath1))
                {
                    logger.LogInformation("使用備用路徑1: {FallbackPath}", szFallbackPath1);
                    szMqttConfigPath = szFallbackPath1;
                }
                else
                {
                    // 備用路徑2: 專案根目錄
                    var szProjectRoot = Path.GetDirectoryName(szBasePath);
                    while (szProjectRoot != null && !Directory.GetFiles(szProjectRoot, "*.csproj").Any())
                    {
                        szProjectRoot = Path.GetDirectoryName(szProjectRoot);
                    }
                    
                    if (szProjectRoot != null)
                    {
                        var szFallbackPath2 = Path.Combine(szProjectRoot, "MqttSetting", "MqttSetting.json");
                        if (File.Exists(szFallbackPath2))
                        {
                            logger.LogInformation("使用備用路徑2 (專案根目錄): {FallbackPath}", szFallbackPath2);
                            szMqttConfigPath = szFallbackPath2;
                        }
                        else
                        {
                            logger.LogWarning("所有路徑都找不到 MQTT 設定檔，將使用預設配置");
                        }
                    }
                    else
                    {
                        logger.LogWarning("無法找到專案根目錄，將使用預設配置");
                    }
                }
            }
            
            return new MqttConfigService(logger, szMqttConfigPath);
        });

        // 註冊資料庫初始化服務為單例
        services.AddSingleton<DatabaseInitializationService>();

        // 註冊資料存取介面與實作類別為單例
        services.AddSingleton<SqlServerDataRepository>();
        services.AddSingleton<IDataRepository>(provider => provider.GetRequiredService<SqlServerDataRepository>());

        // 註冊資料儲存服務為單例
        services.AddSingleton<HistoryDataStorageService>();
        services.AddSingleton<RealtimeDataStorageService>();

        // 註冊警報監控服務為單例
        services.AddSingleton<AlarmEventLogRepository>();
        services.AddSingleton<AlarmMqttPublisher>();
        services.AddSingleton<AlarmMonitorService>();

        // 註冊通知共用基礎設施（Line / Email 共用）
        services.AddHttpClient();
        services.AddSingleton<NotificationLocalizer>();
        services.AddSingleton<NotifyDeliveryLogger>();

        // 註冊 Line 通知相關服務為單例
        services.AddSingleton<LineNotifyTargetRepository>();
        services.AddSingleton<LineNotificationService>();

        // 註冊 Email 通知相關服務為單例
        services.AddSingleton<EmailGroupRepository>();
        services.AddSingleton<EmailSenderClient>();
        services.AddSingleton<EmailNotificationService>();

        // 註冊簡訊通知相關服務為單例（序列埠簡訊盒，ISmsTransport 抽象供未來 HTTP 閘道替換）
        services.AddSingleton<SmsTargetRepository>();
        services.AddSingleton<SmsModemClient>();
        services.AddSingleton<ISmsTransport>(provider => provider.GetRequiredService<SmsModemClient>());
        services.AddSingleton<SmsNotificationService>();

        // 通知設定檔熱重載（Web 存 Email / Sms / Line 設定 → MQTT SCADA/Sys/Notify/Reload → 重讀 Shared 檔套用到上面三個服務）
        services.AddSingleton<INotifySettingApplier, NotifyServicesSettingApplier>();
        services.AddSingleton<NotifySettingReloader>(sp => new NotifySettingReloader(
            sp.GetRequiredService<ILogger<NotifySettingReloader>>(),
            sp.GetRequiredService<INotifySettingApplier>(),
            sp.GetRequiredService<DatabaseConfigService>()));

        // 註冊 LogicFlow 資料存取服務為單例
        services.AddSingleton<LogicFlowRepository>();

        // 註冊 C# 演算法動態編譯服務為單例
        services.AddSingleton<CSharpAlgorithmService>();

        // 註冊計算點位服務為單例
        services.AddSingleton<CalculatedPointService>();

        // 註冊 DB 來源相關服務為單例（DbCommunicationService 同時作 HostedService 由 Program.cs 註冊）
        services.AddSingleton<DbCoordinatorJsonLoader>();
        services.AddSingleton<DbCommunicationService>();

        // 註冊 OPC UA 來源相關服務為單例（OpcUaCommunicationService 同時作 HostedService 由 Program.cs 註冊）
        services.AddSingleton<OpcUaConfigLoader>();
        services.AddSingleton<OpcUaCommunicationService>();

        return services;
    }

    /// <summary>
    /// 初始化資料庫服務
    /// </summary>
    /// <param name="serviceProvider">服務提供者</param>
    /// <returns>初始化任務</returns>
    public static async Task InitializeDatabaseAsync(this IServiceProvider serviceProvider)
    {
        try
        {
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("正在初始化資料庫服務...");

            // 1. 初始化資料庫結構
            var initService = serviceProvider.GetRequiredService<DatabaseInitializationService>();
            var isSchemaInitialized = await initService.InitializeDatabaseSchemaAsync();
            
            if (isSchemaInitialized)
            {
                logger.LogInformation("資料庫結構初始化成功");
            }
            else
            {
                logger.LogWarning("資料庫結構初始化失敗");
            }

            // 2. 初始化資料存取服務
            var dataRepository = serviceProvider.GetService<IDataRepository>();
            if (dataRepository is SqlServerDataRepository sqlRepository)
            {
                var isRepositoryInitialized = await sqlRepository.InitializeAsync();
                if (isRepositoryInitialized)
                {
                    var isConnected = await dataRepository.TestConnectionAsync();
                    if (isConnected)
                    {
                        logger.LogInformation("資料庫服務初始化成功並連線正常");
                    }
                    else
                    {
                        logger.LogWarning("資料庫服務初始化成功但連線測試失敗");
                    }
                }
                else
                {
                    logger.LogWarning("資料庫存取服務初始化失敗");
                }
            }

            // 3. 初始化資料儲存服務 (強制建立實例以啟動定時器)
            var historyService = serviceProvider.GetRequiredService<HistoryDataStorageService>();
            var realtimeService = serviceProvider.GetRequiredService<RealtimeDataStorageService>();
            logger.LogInformation("資料儲存服務已初始化: 歷史資料服務(每分鐘)和即時資料服務(每5秒)");

            // 4. 初始化警報監控服務（載入規則 + 還原活躍警報狀態）
            //    重要：此時 LineNotificationService 尚未 Initialize，還原期間呼叫 NotifyAsync 會被 skip
            var alarmMonitorService = serviceProvider.GetRequiredService<AlarmMonitorService>();
            await alarmMonitorService.InitializeAsync();
            logger.LogInformation("警報監控服務已初始化");

            // 5. 初始化 Line / Email / 簡訊通知服務 — 三檔路徑皆由 SharedSettingLocator 解析
            //    （..\..\Shared → 本機 Setting → 開發 repo，與 Web 同一套，見 docs/架構.md §共用設定檔解析順序）；
            //    之後 Web 存檔會發 MQTT SCADA/Sys/Notify/Reload 由 NotifySettingReloadSubscriber 熱重載，不必重啟
            var lineService = serviceProvider.GetRequiredService<LineNotificationService>();
            var lineSetting = LoadNotifySettingOrDisable<ScadaEngine.Common.Data.Models.LineSettingModel>(
                NotifySettingLoader.LineFileName, "Line", logger,
                () => new ScadaEngine.Common.Data.Models.LineSettingModel { EnableNotification = false });
            await lineService.InitializeAsync(lineSetting);

            var emailService = serviceProvider.GetRequiredService<EmailNotificationService>();
            var emailSetting = LoadNotifySettingOrDisable<ScadaEngine.Common.Data.Models.EmailSettingModel>(
                NotifySettingLoader.EmailFileName, "Email", logger,
                () => new ScadaEngine.Common.Data.Models.EmailSettingModel { EnableNotification = false });
            await emailService.InitializeAsync(emailSetting);

            // 簡訊：含簡訊盒 COM port 自動偵測
            var smsService = serviceProvider.GetRequiredService<SmsNotificationService>();
            var smsSetting = LoadNotifySettingOrDisable<ScadaEngine.Common.Data.Models.SmsSettingModel>(
                NotifySettingLoader.SmsFileName, "簡訊", logger,
                () => new ScadaEngine.Common.Data.Models.SmsSettingModel { EnableNotification = false });
            await smsService.InitializeAsync(smsSetting);

            // 6. 初始化計算點位服務（載入公式設定）
            var calculatedPointService = serviceProvider.GetRequiredService<CalculatedPointService>();
            await calculatedPointService.InitializeAsync();
            logger.LogInformation("計算點位服務已初始化");
        }
        catch (Exception ex)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "資料庫初始化過程中發生錯誤");
        }
    }

    /// <summary>
    /// 啟動時載入通知設定檔；找不到或解析失敗 → 回傳停用版預設物件（警報流程不受影響，只是該通道不發）。
    /// 路徑解析與讀檔細節在 <see cref="NotifySettingLoader"/>（與 MQTT 熱重載共用）。
    /// </summary>
    private static T LoadNotifySettingOrDisable<T>(string szFileName, string szChannelLabel, ILogger logger, Func<T> disabledFactory)
        where T : class
    {
        var result = NotifySettingLoader.Load<T>(szFileName, logger);
        if (result.isSuccess)
        {
            return result.setting!;
        }

        logger.LogWarning("{File} 無法使用（{Error}），{Channel} 通知功能將停用", szFileName, result.szError, szChannelLabel);
        return disabledFactory();
    }
}
