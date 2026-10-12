using System.Collections.Concurrent;
using System.Text.Json;

namespace ScadaEngine.Web.Services;

/// <summary>Engine 回報的單次通知設定重載結果（SCADA/Sys/Notify/Status payload）</summary>
public sealed record NotifyReloadResult(string szFile, bool isSuccess, string szMessage, string szSource, string szPath, string szTime);

/// <summary>
/// Singleton — 由 MqttRealtimeSubscriberService 餵入 SCADA/Sys/Notify/Status，
/// 讓存檔的 HTTP 請求能「發 Reload → 等 Engine 回應 → 回傳結果」在同一回合完成。
/// 以 requestId 配對：存檔端先 <see cref="Register"/>，Engine 回報進來時 TrySetResult；
/// 等不到（Engine 離線 / MQTT 未連）由呼叫端 timeout 判定。
/// </summary>
public class NotifyReloadStatusCache
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NotifyReloadResult>> _pending = new();
    private readonly ConcurrentDictionary<string, NotifyReloadResult> _lastByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>各檔最近一次結果（診斷用）</summary>
    public IReadOnlyDictionary<string, NotifyReloadResult> LastByFile => _lastByFile;

    /// <summary>登記一個待回應的 requestId，之後用 <see cref="WaitAsync"/> 等</summary>
    public void Register(string szRequestId)
    {
        _pending[szRequestId] = new TaskCompletionSource<NotifyReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>等 Engine 回報；逾時回 null。無論結果都會移除登記。</summary>
    public async Task<NotifyReloadResult?> WaitAsync(string szRequestId, TimeSpan timeout)
    {
        if (!_pending.TryGetValue(szRequestId, out var tcs))
            return null;

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return completed == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            _pending.TryRemove(szRequestId, out _);
        }
    }

    /// <summary>MQTT 收到 Status 時呼叫（payload 壞掉就略過）</summary>
    public void Update(string szPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(szPayloadJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(szPayloadJson);
            var root = doc.RootElement;

            var result = new NotifyReloadResult(
                szFile: GetString(root, "file"),
                isSuccess: root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True,
                szMessage: GetString(root, "message"),
                szSource: GetString(root, "source"),
                szPath: GetString(root, "path"),
                szTime: GetString(root, "time"));

            if (!string.IsNullOrEmpty(result.szFile))
                _lastByFile[result.szFile] = result;

            var szRequestId = GetString(root, "requestId");
            if (!string.IsNullOrEmpty(szRequestId) && _pending.TryGetValue(szRequestId, out var tcs))
                tcs.TrySetResult(result);
        }
        catch (JsonException)
        {
            // Engine 回報格式異常：不影響 Web，等待端會自然 timeout
        }
    }

    private static string GetString(JsonElement root, string szName)
        => root.TryGetProperty(szName, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? string.Empty) : string.Empty;
}
