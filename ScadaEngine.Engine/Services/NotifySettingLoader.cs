using System.Text.Json;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 通知設定檔（EmailSetting / SmsSetting / LineSetting）載入器 — Engine 啟動載入與 MQTT 熱重載共用。
/// 路徑由 Common 的 <see cref="SharedSettingLocator"/> 解析（..\..\Shared → 本機 Setting → 開發 repo），
/// 與 Web 同一套順序，所以 Web 存檔寫的和 Engine 讀的一定是同一份。
/// 載入結果不丟例外：找不到 / 解析失敗都回 <see cref="LoadResult{T}.szError"/>，由呼叫端決定停用或保留舊設定。
/// </summary>
public static class NotifySettingLoader
{
    public const string EmailFileName = "EmailSetting.json";
    public const string SmsFileName = "SmsSetting.json";
    public const string LineFileName = "LineSetting.json";

    /// <summary>熱重載允許的檔名白名單（Reload 指令帶其他檔名一律忽略）</summary>
    public static readonly IReadOnlyList<string> AllFileNames = new[] { EmailFileName, SmsFileName, LineFileName };

    private static readonly JsonSerializerOptions c_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>載入結果：成功時 setting 非 null 且 szError 為 null</summary>
    public sealed record LoadResult<T>(T? setting, SharedSettingLocation location, string? szError) where T : class
    {
        public bool isSuccess => setting != null && szError == null;
    }

    /// <summary>是否為允許載入的通知設定檔名（大小寫不敏感）</summary>
    public static bool IsKnownFile(string? szFileName)
        => szFileName != null && AllFileNames.Contains(szFileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解析路徑 + 讀檔 + 反序列化。每次呼叫都印一行「通知設定檔路徑」log（啟動 / 重載皆同格式）。
    /// </summary>
    /// <param name="szFileName">EmailSetting.json / SmsSetting.json / LineSetting.json</param>
    /// <param name="logger">log 輸出</param>
    /// <param name="locate">路徑解析（預設 SharedSettingLocator.Resolve；單元測試可注入指到暫存目錄）</param>
    public static LoadResult<T> Load<T>(string szFileName, ILogger logger, Func<string, SharedSettingLocation>? locate = null)
        where T : class
    {
        var location = (locate ?? (f => SharedSettingLocator.Resolve(f)))(szFileName);

        logger.LogInformation("通知設定檔路徑: {Path}（來源: {Source}）",
            location.szPath ?? $"(未找到 {szFileName})", location.szSourceLabel);

        if (location.szPath == null)
        {
            logger.LogWarning("找不到 {File}，依序找過: {Searched}", szFileName, string.Join(" | ", location.szSearchedPaths));
            return new LoadResult<T>(null, location, "找不到設定檔");
        }

        try
        {
            var szJson = File.ReadAllText(location.szPath);
            var setting = JsonSerializer.Deserialize<T>(szJson, c_jsonOptions);
            if (setting == null)
            {
                return new LoadResult<T>(null, location, "檔案內容為空（JSON null）");
            }

            logger.LogInformation("{File} 已載入: {Path}", szFileName, location.szPath);
            return new LoadResult<T>(setting, location, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析 {File} 失敗: {Path}", szFileName, location.szPath);
            return new LoadResult<T>(null, location, ex.Message);
        }
    }
}
