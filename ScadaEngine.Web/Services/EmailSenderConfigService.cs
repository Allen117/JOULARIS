using System.Text.Json;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.AlarmSetting.Models;

namespace ScadaEngine.Web.Services
{
    /// <summary>
    /// EmailSetting.json 讀寫服務 — Web UI 編輯 SMTP 設定時直接寫 JSON 檔
    /// 路徑由 Common 的 SharedSettingLocator 解析（..\..\Shared → 本機 Setting → 開發 repo），與 Engine 同一套：
    /// 生產機 = C:\SCADA\Shared\EmailSetting.json、開發模式 = repo 的 ScadaEngine.Engine/Setting/EmailSetting.json。
    /// 讀寫走同一條解析路徑，不會再有「讀 Engine 那份、寫 Web 複本」的分家。
    /// 存檔後由 controller 發 MQTT SCADA/Sys/Notify/Reload 讓 Engine 熱重載。
    /// 密碼空字串時保留既有密碼（避免每次儲存都要重輸入）
    /// </summary>
    public class EmailSenderConfigService
    {
        public const string FileName = "EmailSetting.json";

        private readonly ILogger<EmailSenderConfigService> _logger;
        private readonly object _fileLock = new();

        public EmailSenderConfigService(ILogger<EmailSenderConfigService> logger)
        {
            _logger = logger;

            var location = SharedSettingLocator.Resolve(FileName);
            _logger.LogInformation("通知設定檔路徑: {Path}（來源: {Source}）",
                location.szPath ?? $"(未找到 {FileName})", location.szSourceLabel);
        }

        /// <summary>讀取設定（不回傳密碼明文，只回傳 hasPassword 旗標）</summary>
        public EmailSenderConfigDto Read()
        {
            var setting = LoadFromFile();
            return new EmailSenderConfigDto
            {
                enableNotification = setting.EnableNotification,
                smtpHost = setting.SmtpHost,
                smtpPort = setting.SmtpPort,
                useSsl = setting.UseSsl,
                useStartTls = setting.UseStartTls,
                username = setting.Username,
                password = string.Empty,
                fromAddress = setting.FromAddress,
                fromDisplayName = setting.FromDisplayName,
                ratePerMinute = setting.RatePerMinute,
                testSendThrottleSeconds = setting.TestSendThrottleSeconds,
                hasPassword = !string.IsNullOrEmpty(setting.Password)
            };
        }

        /// <summary>寫入設定。dto.password 為空字串 → 保留檔案內既有密碼。</summary>
        public bool Save(EmailSenderConfigDto dto)
        {
            try
            {
                lock (_fileLock)
                {
                    var existing = LoadFromFile();
                    var szNewPassword = string.IsNullOrEmpty(dto.password) ? existing.Password : dto.password;

                    var setting = new EmailSettingModel
                    {
                        EnableNotification = dto.enableNotification,
                        SmtpHost = (dto.smtpHost ?? string.Empty).Trim(),
                        SmtpPort = dto.smtpPort > 0 ? dto.smtpPort : 587,
                        UseSsl = dto.useSsl,
                        UseStartTls = dto.useStartTls,
                        Username = (dto.username ?? string.Empty).Trim(),
                        Password = szNewPassword ?? string.Empty,
                        FromAddress = (dto.fromAddress ?? string.Empty).Trim(),
                        FromDisplayName = (dto.fromDisplayName ?? "SCADA Engine").Trim(),
                        RatePerMinute = dto.ratePerMinute > 0 ? dto.ratePerMinute : 10,
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

        /// <summary>給其他 Web 服務取用完整設定（含密碼）— 不對外暴露</summary>
        public EmailSettingModel LoadFromFile()
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
                        return new EmailSettingModel { EnableNotification = false };
                    }
                    var szJson = File.ReadAllText(location.szPath);
                    var setting = JsonSerializer.Deserialize<EmailSettingModel>(
                        szJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return setting ?? new EmailSettingModel { EnableNotification = false };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "讀取 {File} 失敗", FileName);
                return new EmailSettingModel { EnableNotification = false };
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
