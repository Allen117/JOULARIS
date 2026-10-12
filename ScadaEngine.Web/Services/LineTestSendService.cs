using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;

namespace ScadaEngine.Web.Services
{
    /// <summary>
    /// Line 測試發送服務 — 給「測試發送」按鈕使用
    /// - 讀取 LineSetting.json 取得 ChannelAccessToken
    /// - 同 GroupID 10 秒內只能發一次（後端 throttle，避免使用者狂按燒月額度）
    /// - 失敗訊息直接回傳給 controller，由 controller 換成 HTTP 狀態碼
    /// </summary>
    public class LineTestSendService
    {
        private const string c_szLinePushUrl = "https://api.line.me/v2/bot/message/push";

        private readonly ILogger<LineTestSendService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private readonly ConcurrentDictionary<string, DateTime> _lastTestAt = new();
        public const string FileName = "LineSetting.json";

        private LineSettingModel? _setting;
        private string? _szCachedPath;
        private DateTime _dtCachedWrite;
        private readonly object _settingLock = new();

        public LineTestSendService(ILogger<LineTestSendService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            var location = SharedSettingLocator.Resolve(FileName);
            _logger.LogInformation("通知設定檔路徑: {Path}（來源: {Source}）",
                location.szPath ?? $"(未找到 {FileName})", location.szSourceLabel);
        }

        public TestSendResult CheckThrottle(string szGroupId)
        {
            var setting = LoadSetting();
            int nThrottle = setting.TestSendThrottleSeconds > 0 ? setting.TestSendThrottleSeconds : 10;

            if (_lastTestAt.TryGetValue(szGroupId, out var dtLast))
            {
                var dtElapsed = DateTime.UtcNow - dtLast;
                if (dtElapsed.TotalSeconds < nThrottle)
                {
                    int nRemain = (int)Math.Ceiling(nThrottle - dtElapsed.TotalSeconds);
                    return TestSendResult.Throttled(nRemain);
                }
            }
            return TestSendResult.Ok();
        }

        public async Task<TestSendResult> SendTestAsync(string szGroupId, string szLabel, string szLanguage = "zh-TW")
        {
            var throttleCheck = CheckThrottle(szGroupId);
            if (!throttleCheck.isSuccess)
                return throttleCheck;

            var setting = LoadSetting();
            if (string.IsNullOrWhiteSpace(setting.ChannelAccessToken)
                || setting.ChannelAccessToken == "PASTE_YOUR_LINE_CHANNEL_ACCESS_TOKEN_HERE")
            {
                return TestSendResult.Failure("Line ChannelAccessToken 未設定，請聯絡管理員");
            }

            // 先記錄送出時間（即使後續 API 失敗也要記，避免重試風暴）
            _lastTestAt[szGroupId] = DateTime.UtcNow;

            bool isEn = szLanguage == "en";
            var szText = isEn
                ? $"📨 SCADA Test Message\n" +
                  $"Group: {szLabel}\n" +
                  $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                  $"If you receive this, your Line notification settings are correct."
                : $"📨 SCADA 測試訊息\n" +
                  $"群組: {szLabel}\n" +
                  $"時間: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                  $"若您看到這則訊息，代表 Line 通知設定正確。";

            try
            {
                using var client = _httpClientFactory.CreateClient("Line");
                client.Timeout = TimeSpan.FromSeconds(10);

                var payload = new
                {
                    to = szGroupId,
                    messages = new[] { new { type = "text", text = szText } }
                };
                var szJson = JsonSerializer.Serialize(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, c_szLinePushUrl)
                {
                    Content = new StringContent(szJson, Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setting.ChannelAccessToken);

                using var resp = await client.SendAsync(req);

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Line 測試發送成功: GroupId={Group}", szGroupId);
                    return TestSendResult.Ok();
                }

                var szBody = await resp.Content.ReadAsStringAsync();
                int nStatus = (int)resp.StatusCode;
                _logger.LogWarning("Line 測試發送被 API 拒絕: GroupId={Group}, Status={Status}, Body={Body}",
                    szGroupId, nStatus, szBody);

                // 將 Line 回應的 message 字段挑出來給使用者
                string szDetail = TryExtractLineErrorMessage(szBody) ?? szBody;
                return TestSendResult.Failure($"Line API 回應 {nStatus}: {szDetail}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Line 測試發送發生例外: GroupId={Group}", szGroupId);
                return TestSendResult.Failure($"發送失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 讀 LineSetting.json — 路徑由 SharedSettingLocator 解析（..\..\Shared → 本機 Setting → 開發 repo），
        /// 與 Engine 讀的是同一份（生產機 C:\SCADA\Shared\LineSetting.json）。
        /// 以檔案 LastWriteTime 當快取鍵：現場手改 token 後下一次測試發送即生效，不必重啟 Web。
        /// </summary>
        private LineSettingModel LoadSetting()
        {
            lock (_settingLock)
            {
                var location = SharedSettingLocator.Resolve(FileName);

                if (location.szPath == null)
                {
                    if (_setting == null)
                        _logger.LogWarning("找不到 {File}，測試發送功能將回報未設定；依序找過: {Searched}",
                            FileName, string.Join(" | ", location.szSearchedPaths));
                    _setting = new LineSettingModel { EnableNotification = false };
                    _szCachedPath = null;
                    return _setting;
                }

                var dtWrite = File.GetLastWriteTimeUtc(location.szPath);
                if (_setting != null && _szCachedPath == location.szPath && _dtCachedWrite == dtWrite)
                    return _setting;

                try
                {
                    var szJson = File.ReadAllText(location.szPath);
                    _setting = JsonSerializer.Deserialize<LineSettingModel>(
                        szJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new LineSettingModel();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "解析 {File} 失敗: {Path}", FileName, location.szPath);
                    _setting = new LineSettingModel { EnableNotification = false };
                }
                _szCachedPath = location.szPath;
                _dtCachedWrite = dtWrite;
                return _setting;
            }
        }

        private static string? TryExtractLineErrorMessage(string szBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(szBody);
                if (doc.RootElement.TryGetProperty("message", out var msg))
                    return msg.GetString();
            }
            catch { }
            return null;
        }
    }

    /// <summary>測試發送結果</summary>
    public class TestSendResult
    {
        public bool isSuccess { get; set; }
        public bool isThrottled { get; set; }
        public int nRetryAfterSeconds { get; set; }
        public string szMessage { get; set; } = string.Empty;

        public static TestSendResult Ok() =>
            new() { isSuccess = true, szMessage = "已送出" };

        public static TestSendResult Throttled(int nSeconds) =>
            new() { isSuccess = false, isThrottled = true, nRetryAfterSeconds = nSeconds, szMessage = $"請稍候 {nSeconds} 秒再試" };

        public static TestSendResult Failure(string szMessage) =>
            new() { isSuccess = false, szMessage = szMessage };
    }
}
