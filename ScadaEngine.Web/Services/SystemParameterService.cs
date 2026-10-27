using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.SystemParameter.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 系統參數（工程師專屬「系統參數」頁）— 讀寫 SystemSettings.system_parameters（整份 JSON）。
/// 讀取時缺鍵補預設（屬性初始值）；DB 無值 / 損毀 / 讀取失敗一律回預設，不擋住呼叫端功能。
/// 每次呼叫都讀 DB（單筆 key 查詢），存檔後下一個請求即生效。
/// </summary>
public class SystemParameterService
{
    private const string SettingKey = "system_parameters";

    private readonly ILogger<SystemParameterService> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;

    public SystemParameterService(ILogger<SystemParameterService> logger, DatabaseConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>取得目前參數（失敗回預設並記 log）</summary>
    public async Task<SystemParameterConfig> GetAsync()
        => (await GetWithTimestampAsync()).config;

    /// <summary>取得目前參數與最後更新時間</summary>
    public async Task<(SystemParameterConfig config, DateTime? dtUpdatedAt)> GetWithTimestampAsync()
    {
        try
        {
            using var conn = await GetConnectionAsync();
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT SettingValue, UpdatedAt FROM SystemSettings WHERE SettingKey = @SettingKey",
                new { SettingKey });

            string? szValue = row?.SettingValue;
            DateTime? dtUpdatedAt = row?.UpdatedAt;
            if (string.IsNullOrWhiteSpace(szValue))
                return (new SystemParameterConfig(), null);

            var config = JsonSerializer.Deserialize<SystemParameterConfig>(szValue);
            if (config == null || Validate(config) != null)
            {
                _logger.LogError("SystemSettings.{Key} 內容無效，改用預設值", SettingKey);
                return (new SystemParameterConfig(), dtUpdatedAt);
            }
            return (config, dtUpdatedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取 SystemSettings.{Key} 失敗，改用預設值", SettingKey);
            return (new SystemParameterConfig(), null);
        }
    }

    /// <summary>儲存整份參數。驗證失敗丟 ArgumentException（訊息為 resx key）。</summary>
    public async Task SaveAsync(SystemParameterConfig config)
    {
        var szError = Validate(config);
        if (szError != null) throw new ArgumentException(szError);

        var szJson = JsonSerializer.Serialize(config);
        const string szSql = @"
            IF EXISTS (SELECT 1 FROM SystemSettings WHERE SettingKey = @SettingKey)
                UPDATE SystemSettings SET SettingValue = @szJson, UpdatedAt = GETDATE() WHERE SettingKey = @SettingKey;
            ELSE
                INSERT INTO SystemSettings (SettingKey, SettingValue, UpdatedAt) VALUES (@SettingKey, @szJson, GETDATE());";
        using var conn = await GetConnectionAsync();
        await conn.ExecuteAsync(szSql, new { SettingKey, szJson });
        _logger.LogInformation("系統參數已更新：{Json}", szJson);
    }

    /// <summary>範圍檢查；通過回 null，否則回錯誤訊息的 resx key</summary>
    public static string? Validate(SystemParameterConfig c)
    {
        var r = SystemParameterLimits.HistoryMaxRecordsPerPoint;
        if (c.nHistoryMaxRecordsPerPoint < r.nMin || c.nHistoryMaxRecordsPerPoint > r.nMax)
            return "sysparam.error.history_max_records";

        var e = SystemParameterLimits.HistoryExportMaxCells;
        if (c.nHistoryExportMaxCells < e.nMin || c.nHistoryExportMaxCells > e.nMax)
            return "sysparam.error.history_export_max_cells";

        if (!double.IsFinite(c.dCumulativeJitterTolerancePct)
            || c.dCumulativeJitterTolerancePct < SystemParameterLimits.dJitterMin
            || c.dCumulativeJitterTolerancePct > SystemParameterLimits.dJitterMax)
            return "sysparam.error.jitter";

        return null;
    }
}
