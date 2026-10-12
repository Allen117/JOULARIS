using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 通知設定套用端抽象 — 讓 <see cref="NotifySettingReloader"/> 可用 fake 驗證「解析失敗時不套用」。
/// 正式實作 <see cref="NotifyServicesSettingApplier"/> 直接呼叫三個通知服務既有的初始化 / 重載方法。
/// </summary>
public interface INotifySettingApplier
{
    Task ApplyLineAsync(LineSettingModel setting);
    Task ApplyEmailAsync(EmailSettingModel setting);
    Task ApplySmsAsync(SmsSettingModel setting);
}

/// <summary>正式套用端：Line / Email 再跑一次 InitializeAsync（本就是整份設定替換）；簡訊走 ReloadSettingAsync（關舊 port 重掃、不開第二個 worker）</summary>
public class NotifyServicesSettingApplier : INotifySettingApplier
{
    private readonly LineNotificationService _line;
    private readonly EmailNotificationService _email;
    private readonly SmsNotificationService _sms;

    public NotifyServicesSettingApplier(LineNotificationService line, EmailNotificationService email, SmsNotificationService sms)
    {
        _line = line;
        _email = email;
        _sms = sms;
    }

    public Task ApplyLineAsync(LineSettingModel setting) => _line.InitializeAsync(setting);
    public Task ApplyEmailAsync(EmailSettingModel setting) => _email.InitializeAsync(setting);
    public Task ApplySmsAsync(SmsSettingModel setting) => _sms.ReloadSettingAsync(setting);
}

/// <summary>單次重載結果（會原樣發到 SCADA/Sys/Notify/Status 給 Web 顯示）</summary>
public sealed record NotifySettingReloadResult(
    string szFileName, bool isSuccess, string szMessage, string? szPath, string szSource);

/// <summary>
/// 通知設定檔熱重載核心：讀檔（<see cref="NotifySettingLoader"/>）→ 套用（<see cref="INotifySettingApplier"/>）→ 寫 EventLog。
/// 失敗（找不到 / JSON 壞 / 套用丟例外）一律**保留舊設定**：載入失敗時根本不呼叫 applier，
/// 套用失敗時服務內部的設定引用已是整份替換，不會讀到半套。
/// 同一時間只跑一個重載（SemaphoreSlim），Web 連點存檔也不會交錯。
/// </summary>
public class NotifySettingReloader
{
    private readonly ILogger<NotifySettingReloader> _logger;
    private readonly INotifySettingApplier _applier;
    private readonly DatabaseConfigService? _configService;
    private readonly Func<string, SharedSettingLocation>? _locate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="configService">寫 EventLog 用；null = 不寫（單元測試）</param>
    /// <param name="locate">路徑解析覆寫（單元測試指到暫存目錄）；null = SharedSettingLocator.Resolve</param>
    public NotifySettingReloader(
        ILogger<NotifySettingReloader> logger,
        INotifySettingApplier applier,
        DatabaseConfigService? configService = null,
        Func<string, SharedSettingLocation>? locate = null)
    {
        _logger = logger;
        _applier = applier;
        _configService = configService;
        _locate = locate;
    }

    /// <summary>
    /// 重載指定檔。未知檔名直接回失敗（不碰任何服務）；呼叫端（MQTT 訂閱）可自行決定是否回報。
    /// </summary>
    public async Task<NotifySettingReloadResult> ReloadAsync(string? szRequestedFile)
    {
        var szFileName = NotifySettingLoader.AllFileNames
            .FirstOrDefault(f => string.Equals(f, szRequestedFile, StringComparison.OrdinalIgnoreCase));
        if (szFileName == null)
        {
            _logger.LogWarning("通知設定重載：未知檔名 {File}，忽略", szRequestedFile);
            return new NotifySettingReloadResult(szRequestedFile ?? string.Empty, false, "未知的設定檔名，已忽略", null, "未找到");
        }

        await _gate.WaitAsync();
        try
        {
            return szFileName switch
            {
                NotifySettingLoader.EmailFileName => await ReloadOneAsync<EmailSettingModel>(szFileName, _applier.ApplyEmailAsync,
                    s => $"SMTP={s.SmtpHost}:{s.SmtpPort}, 啟用={s.EnableNotification}, 每分鐘上限={s.RatePerMinute}"),
                NotifySettingLoader.SmsFileName => await ReloadOneAsync<SmsSettingModel>(szFileName, _applier.ApplySmsAsync,
                    s => $"ComPort={s.ComPort}, BaudRate={s.BaudRate}, 啟用={s.EnableNotification}, 每日上限={s.DailyQuota}"),
                _ => await ReloadOneAsync<LineSettingModel>(szFileName, _applier.ApplyLineAsync,
                    s => $"啟用={s.EnableNotification}, 每分鐘上限={s.RatePerMinute}")   // token 絕不進 log / EventLog
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NotifySettingReloadResult> ReloadOneAsync<T>(
        string szFileName, Func<T, Task> apply, Func<T, string> describe) where T : class
    {
        var load = NotifySettingLoader.Load<T>(szFileName, _logger, _locate);
        var szSource = load.location.szSourceLabel;
        var szPath = load.location.szPath;

        if (!load.isSuccess)
        {
            var szMsg = $"{szFileName} 重載失敗，保留舊設定：{load.szError}";
            _logger.LogWarning("{Message}（路徑: {Path}）", szMsg, szPath ?? "(未找到)");
            await WriteEventLogAsync(szMsg, nSeverity: 2);
            return new NotifySettingReloadResult(szFileName, false, szMsg, szPath, szSource);
        }

        try
        {
            await apply(load.setting!);
            var szOk = $"{szFileName} 已重載（來源 {szSource}）：{describe(load.setting!)}";
            _logger.LogInformation("{Message}", szOk);
            await WriteEventLogAsync(szOk, nSeverity: 3);
            return new NotifySettingReloadResult(szFileName, true, szOk, szPath, szSource);
        }
        catch (Exception ex)
        {
            var szMsg = $"{szFileName} 套用失敗，保留舊設定：{ex.Message}";
            _logger.LogError(ex, "{Message}", szMsg);
            await WriteEventLogAsync(szMsg, nSeverity: 2);
            return new NotifySettingReloadResult(szFileName, false, szMsg, szPath, szSource);
        }
    }

    /// <summary>EventLog（EventType=3 資訊、SID=_system；成功 Severity=3、失敗 Severity=2），寫失敗只記 log</summary>
    private async Task WriteEventLogAsync(string szMessage, int nSeverity)
    {
        if (_configService == null) return;

        try
        {
            var szConnectionString = await _configService.GetConnectionStringAsync();
            const string szSql = @"
                INSERT INTO EventLog (SID, EventType, Severity, Message, OccurredAt)
                VALUES ('_system', 3, @Severity, @Message, GETDATE())";

            using var connection = new SqlConnection(szConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(szSql, new
            {
                Severity = nSeverity,
                Message = szMessage.Length <= 500 ? szMessage : szMessage.Substring(0, 497) + "..."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入通知設定重載 EventLog 失敗");
        }
    }
}
