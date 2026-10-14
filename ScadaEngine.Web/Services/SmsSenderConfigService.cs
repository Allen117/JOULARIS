using System.Text.Json;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.AlarmSetting.Models;

namespace ScadaEngine.Web.Services
{
    /// <summary>
    /// SmsSetting.json 讀寫服務 — Web UI 編輯簡訊盒設定時直接寫 JSON 檔
    /// 路徑由 Common 的 SharedSettingLocator 解析（..\..\Shared → 本機 Setting → 開發 repo），與 Engine 同一套：
    /// 生產機 = C:\JOULARIS\Shared\SmsSetting.json、開發模式 = repo 的 ScadaEngine.Engine/Setting/SmsSetting.json。
    /// 存檔後由 controller 發 MQTT SCADA/Sys/Notify/Reload，Engine 熱重載並重掃序列埠（改 COM / 啟用不必重啟）；
    /// DB 收訊號碼則另走 60 秒快取。
    /// </summary>
    public class SmsSenderConfigService
    {
        public const string FileName = "SmsSetting.json";

        private readonly ILogger<SmsSenderConfigService> _logger;
        private readonly object _fileLock = new();

        public SmsSenderConfigService(ILogger<SmsSenderConfigService> logger)
        {
            _logger = logger;

            var location = SharedSettingLocator.Resolve(FileName);
            _logger.LogInformation("通知設定檔路徑: {Path}（來源: {Source}）",
                location.szPath ?? $"(未找到 {FileName})", location.szSourceLabel);
        }

        public SmsSenderConfigDto Read()
        {
            var setting = LoadFromFile();
            return new SmsSenderConfigDto
            {
                enableNotification = setting.EnableNotification,
                comPort = setting.ComPort,
                baudRate = setting.BaudRate,
                simPin = setting.SimPin,
                ratePerMinute = setting.RatePerMinute,
                dailyQuota = setting.DailyQuota,
                sendRecovery = setting.SendRecovery,
                testSendThrottleSeconds = setting.TestSendThrottleSeconds
            };
        }

        public bool Save(SmsSenderConfigDto dto)
        {
            try
            {
                lock (_fileLock)
                {
                    var setting = new SmsSettingModel
                    {
                        EnableNotification = dto.enableNotification,
                        ComPort = string.IsNullOrWhiteSpace(dto.comPort) ? "auto" : dto.comPort.Trim(),
                        BaudRate = dto.baudRate >= 0 ? dto.baudRate : 0,
                        SimPin = (dto.simPin ?? string.Empty).Trim(),
                        RatePerMinute = dto.ratePerMinute > 0 ? dto.ratePerMinute : 10,
                        DailyQuota = dto.dailyQuota >= 0 ? dto.dailyQuota : 100,
                        SendRecovery = dto.sendRecovery,
                        TestSendThrottleSeconds = dto.testSendThrottleSeconds > 0 ? dto.testSendThrottleSeconds : 10
                    };

                    var szPath = ResolveWritePath();
                    var szJson = JsonSerializer.Serialize(setting, new JsonSerializerOptions { WriteIndented = true });
                    Directory.CreateDirectory(Path.GetDirectoryName(szPath)!);
                    File.WriteAllText(szPath, szJson);
                    _logger.LogInformation("已更新 {File}: {Path}", FileName, szPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "儲存 {File} 失敗", FileName);
                return false;
            }
        }

        /// <summary>給其他 Web 服務取用完整設定 — 測試發送 throttle 秒數等</summary>
        public SmsSettingModel LoadFromFile()
        {
            try
            {
                lock (_fileLock)
                {
                    var location = SharedSettingLocator.Resolve(FileName);
                    if (location.szPath == null)
                    {
                        _logger.LogWarning("找不到 {File}，使用預設值；依序找過: {Searched}",
                            FileName, string.Join(" | ", location.szSearchedPaths));
                        return new SmsSettingModel { EnableNotification = false };
                    }
                    var szJson = File.ReadAllText(location.szPath);
                    var setting = JsonSerializer.Deserialize<SmsSettingModel>(
                        szJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return setting ?? new SmsSettingModel { EnableNotification = false };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "讀取 {File} 失敗", FileName);
                return new SmsSettingModel { EnableNotification = false };
            }
        }

        /// <summary>寫入目標 = 解析到的那一份；都不存在時退到 Shared（生產）或開發 repo，並 log warning</summary>
        private string ResolveWritePath()
        {
            var (szPath, isFallback) = SharedSettingLocator.ResolveForWrite(FileName);
            if (isFallback)
                _logger.LogWarning("{File} 不存在於任何解析層，將新建於: {Path}", FileName, szPath);
            return szPath;
        }
    }
}
