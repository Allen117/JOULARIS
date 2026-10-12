using System.Collections.Concurrent;
using System.Threading.Channels;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 簡訊通知服務 — 警報觸發 / 恢復時經簡訊盒發送 SMS
/// 設計重點（路由模型照 LineNotificationService，硬體差異另加兩層防護）：
///   1. 每號碼獨立 MaxSeverity + Language 路由；Critical (severity=0) 繞過限流
///   2. 每號碼 1 分鐘滑動視窗限流（預設 10 封/分），超過進 buffer，視窗結束發摘要
///   3. 序列埠一次只能送一封且每封 5~10 秒 → 所有發送走背景佇列，絕不阻塞警報主流程
///   4. DailyQuota 每日總量硬上限（防警報風暴燒簡訊費 / SIM 被電信商停用），跨日歸零
///   5. SendRecovery=false 時不發恢復簡訊（省一半費用的開關）
///   6. 發送結果寫 EventLog 摘要（共用 NotifyDeliveryLogger，Channel=Sms）
///   7. _isInitialized 旗標：Engine 啟動還原舊警報時呼叫的 Notify 一律 skip
/// </summary>
public class SmsNotificationService : IDisposable
{
    private readonly ILogger<SmsNotificationService> _logger;
    private readonly SmsTargetRepository _targetRepo;
    private readonly ISmsTransport _transport;
    private readonly NotificationLocalizer _localizer;
    private readonly NotifyDeliveryLogger _deliveryLogger;

    private SmsSettingModel _setting = new();
    private bool _isInitialized = false;

    /// <summary>每號碼各自的滑動視窗狀態（key = 電話號碼）</summary>
    private readonly ConcurrentDictionary<string, PhoneRateState> _rateStates = new();

    private readonly Timer _flushTimer;

    /// <summary>發送佇列 — 序列埠慢速發送與警報主流程解耦；滿載丟棄新工作並記 log</summary>
    private readonly Channel<SmsJob> _queue = Channel.CreateBounded<SmsJob>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;

    /// <summary>每日發送計數（含摘要與測試），跨日自動歸零</summary>
    private readonly object _quotaLock = new();
    private DateTime _dtQuotaDate = DateTime.Today;
    private int _nSentToday = 0;
    private bool _isQuotaAlarmLogged = false;

    public SmsNotificationService(
        ILogger<SmsNotificationService> logger,
        SmsTargetRepository targetRepo,
        ISmsTransport transport,
        NotificationLocalizer localizer,
        NotifyDeliveryLogger deliveryLogger)
    {
        _logger = logger;
        _targetRepo = targetRepo;
        _transport = transport;
        _localizer = localizer;
        _deliveryLogger = deliveryLogger;

        _flushTimer = new Timer(async _ => await FlushExpiredWindowsAsync(),
            null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsEnabled => _isInitialized && _setting.EnableNotification;

    public async Task InitializeAsync(SmsSettingModel setting)
    {
        _setting = setting ?? new SmsSettingModel();
        if (_setting.RatePerMinute <= 0)
            _setting.RatePerMinute = 10;

        await _transport.InitializeAsync(_setting);

        _workerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        _flushTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _isInitialized = true;
        _logger.LogInformation("簡訊通知服務初始化完成 (啟用={Enabled}, 每分鐘上限={Rate}, 每日上限={Quota}, 恢復通知={Recovery})",
            _setting.EnableNotification, _setting.RatePerMinute, _setting.DailyQuota, _setting.SendRecovery);
    }

    /// <summary>
    /// 執行期換設定（Web 存 SmsSetting.json 後經 MQTT 熱重載）。
    /// 與 <see cref="InitializeAsync"/> 的差別：不再開第二個佇列 worker、不重設每日計數；
    /// 設定物件整份替換（發送端拿到新或舊都是完整一份），再交由 transport 關舊 port、依新設定重掃。
    /// </summary>
    public async Task ReloadSettingAsync(SmsSettingModel setting)
    {
        var newSetting = setting ?? new SmsSettingModel();
        if (newSetting.RatePerMinute <= 0)
            newSetting.RatePerMinute = 10;

        _setting = newSetting;
        var isConnected = await _transport.ReloadAsync(_setting);

        if (!_isInitialized)
        {
            // 啟動時 InitializeAsync 尚未跑到（理論上不會，保險）：補起 worker 與 flush timer
            _workerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
            _flushTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            _isInitialized = true;
        }

        _logger.LogInformation("簡訊通知設定已重載 (啟用={Enabled}, 每分鐘上限={Rate}, 每日上限={Quota}, 恢復通知={Recovery}, 簡訊盒連線={Connected})",
            _setting.EnableNotification, _setting.RatePerMinute, _setting.DailyQuota, _setting.SendRecovery, isConnected);
    }

    /// <summary>
    /// 警報觸發時呼叫 — 對所有符合「MaxSeverity >= 此警報嚴重度」的號碼發簡訊
    /// </summary>
    public async Task NotifyAsync(NotifyContext ctx)
    {
        if (!_isInitialized || !_setting.EnableNotification)
            return;

        try
        {
            var targets = await _targetRepo.GetEnabledTargetsAsync();
            if (targets.Count == 0)
            {
                await _deliveryLogger.LogAsync(ctx.szSID, NotifyDeliveryLogger.Channel.Sms,
                    NotifyDeliveryLogger.Status.NoTarget, "無啟用的簡訊號碼", ctx.nRelatedEventId);
                return;
            }

            var matched = targets.Where(t => t.nMaxSeverity >= ctx.nSeverity).ToList();
            if (matched.Count == 0)
            {
                await _deliveryLogger.LogAsync(ctx.szSID, NotifyDeliveryLogger.Channel.Sms,
                    NotifyDeliveryLogger.Status.NoTarget,
                    $"無號碼符合嚴重度 {ctx.nSeverity}", ctx.nRelatedEventId);
                return;
            }

            var job = new SmsJob { szSID = ctx.szSID, nRelatedEventId = ctx.nRelatedEventId, isRecovery = false };

            foreach (var target in matched)
            {
                if (ctx.nSeverity == 0)
                {
                    // Critical 繞過限流，永遠單獨送
                    job.items.Add(new SendItem
                    {
                        szPhone = target.szPhoneNumber,
                        szLabel = target.szLabel,
                        szText = FormatSingleAlert(ctx, target.szLanguage, isRecovery: false)
                    });
                }
                else if (TryPassRateWindow(target, ctx))
                {
                    job.items.Add(new SendItem
                    {
                        szPhone = target.szPhoneNumber,
                        szLabel = target.szLabel,
                        szText = FormatSingleAlert(ctx, target.szLanguage, isRecovery: false)
                    });
                }
                else
                {
                    job.nBuffered++;
                }
            }

            EnqueueJob(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "簡訊通知發送流程發生例外: SID={SID}", ctx.szSID);
        }
    }

    /// <summary>
    /// 警報恢復時呼叫 — SendRecovery=false 時整體略過（省費開關）；不走 rate limit
    /// </summary>
    public async Task NotifyClearedAsync(NotifyContext ctx)
    {
        if (!_isInitialized || !_setting.EnableNotification || !_setting.SendRecovery)
            return;

        try
        {
            var targets = await _targetRepo.GetEnabledTargetsAsync();
            var matched = targets.Where(t => t.nMaxSeverity >= ctx.nSeverity).ToList();
            if (matched.Count == 0)
            {
                await _deliveryLogger.LogAsync(ctx.szSID, NotifyDeliveryLogger.Channel.Sms,
                    NotifyDeliveryLogger.Status.NoTarget, "無號碼符合嚴重度（恢復通知）", ctx.nRelatedEventId);
                return;
            }

            var job = new SmsJob { szSID = ctx.szSID, nRelatedEventId = ctx.nRelatedEventId, isRecovery = true };
            foreach (var target in matched)
            {
                job.items.Add(new SendItem
                {
                    szPhone = target.szPhoneNumber,
                    szLabel = target.szLabel,
                    szText = FormatSingleAlert(ctx, target.szLanguage, isRecovery: true)
                });
            }
            EnqueueJob(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "簡訊恢復通知發送流程發生例外: SID={SID}", ctx.szSID);
        }
    }

    /// <summary>
    /// 測試發送（MQTT test 命令用）— 不進佇列直接發（transport 內部自會序列化），
    /// 消耗每日額度，結果由呼叫端與本方法各自回報
    /// </summary>
    public async Task<SmsSendResult> SendTestAsync(string szPhoneNumber, string szLabel, string szLanguage)
    {
        if (!_isInitialized || !_setting.EnableNotification)
            return SmsSendResult.Fail("簡訊通知未啟用");

        if (!TryConsumeQuota())
            return SmsSendResult.Fail($"已達每日發送上限 {_setting.DailyQuota} 封");

        var args = new Dictionary<string, string?>
        {
            ["label"] = szLabel,
            ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        var szText = _localizer.Format(szLanguage, "notify.body.test.sms", args);

        var result = await _transport.SendAsync(szPhoneNumber, szText);
        await _deliveryLogger.LogAsync("_system", NotifyDeliveryLogger.Channel.Sms,
            result.isSuccess ? NotifyDeliveryLogger.Status.AllSent : NotifyDeliveryLogger.Status.AllFailed,
            result.isSuccess
                ? $"測試發送成功: {szLabel} ({szPhoneNumber})"
                : $"測試發送失敗: {szLabel} ({szPhoneNumber}) — {result.szError}");
        return result;
    }

    /// <summary>今日已發送封數與上限（供 MQTT 狀態發布）</summary>
    public (int nSentToday, int nDailyQuota) GetQuotaState()
    {
        lock (_quotaLock)
        {
            RollQuotaDateLocked();
            return (_nSentToday, _setting.DailyQuota);
        }
    }

    // ── 限流視窗（notify 呼叫端執行，純記憶體、不阻塞）──

    /// <summary>回傳 true = 本封可即刻發送；false = 已進 buffer 等摘要</summary>
    private bool TryPassRateWindow(SmsNotifyTargetModel target, NotifyContext ctx)
    {
        var state = _rateStates.GetOrAdd(target.szPhoneNumber, _ => new PhoneRateState());

        List<BufferedMessage>? snapshot = null;
        DateTime windowStart;
        bool sendNow;

        lock (state.Lock)
        {
            if (DateTime.UtcNow - state.WindowStart >= TimeSpan.FromMinutes(1))
            {
                if (state.Buffer.Count > 0)
                    snapshot = new List<BufferedMessage>(state.Buffer);
                state.WindowStart = DateTime.UtcNow;
                state.Count = 0;
                state.Buffer.Clear();
            }

            if (state.Count < _setting.RatePerMinute)
            {
                state.Count++;
                sendNow = true;
            }
            else
            {
                state.Buffer.Add(new BufferedMessage
                {
                    nSeverity = ctx.nSeverity,
                    dtTime = ctx.dtTime,
                    szLanguage = target.szLanguage,
                    szLabel = target.szLabel
                });
                sendNow = false;
            }
            windowStart = state.WindowStart;
        }

        if (snapshot != null)
            EnqueueSummary(target.szPhoneNumber, target.szLabel, snapshot);

        return sendNow;
    }

    private async Task FlushExpiredWindowsAsync()
    {
        if (!_isInitialized) return;

        try
        {
            foreach (var kv in _rateStates)
            {
                var state = kv.Value;
                List<BufferedMessage>? snapshot = null;
                lock (state.Lock)
                {
                    if (DateTime.UtcNow - state.WindowStart < TimeSpan.FromMinutes(1))
                        continue;
                    if (state.Buffer.Count > 0)
                        snapshot = new List<BufferedMessage>(state.Buffer);
                    state.WindowStart = DateTime.UtcNow;
                    state.Count = 0;
                    state.Buffer.Clear();
                }
                if (snapshot != null)
                    EnqueueSummary(kv.Key, snapshot[0].szLabel, snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "簡訊摘要 flush 發生例外");
        }
        await Task.CompletedTask;
    }

    private void EnqueueSummary(string szPhone, string szLabel, List<BufferedMessage> snapshot)
    {
        var szLanguage = snapshot[0].szLanguage;
        var args = new Dictionary<string, string?>
        {
            ["count"] = snapshot.Count.ToString(),
            ["critical"] = snapshot.Count(m => m.nSeverity == 0).ToString(),
            ["high"] = snapshot.Count(m => m.nSeverity == 1).ToString(),
            ["medium"] = snapshot.Count(m => m.nSeverity == 2).ToString(),
            ["low"] = snapshot.Count(m => m.nSeverity == 3).ToString()
        };
        var szText = _localizer.Format(szLanguage, "notify.body.summary.sms", args);

        EnqueueJob(new SmsJob
        {
            szSID = "_summary",
            nRelatedEventId = 0,
            isRecovery = false,
            items = { new SendItem { szPhone = szPhone, szLabel = szLabel, szText = szText } }
        });
    }

    // ── 背景佇列 ──

    private void EnqueueJob(SmsJob job)
    {
        if (job.items.Count == 0 && job.nBuffered == 0)
            return;

        if (job.items.Count == 0)
        {
            // 全部進了限流 buffer — 直接記 EventLog，不佔佇列
            _ = _deliveryLogger.LogAsync(job.szSID, NotifyDeliveryLogger.Channel.Sms,
                NotifyDeliveryLogger.Status.RateLimited,
                $"號碼 {job.nBuffered} 個全數限流，已排入摘要", job.nRelatedEventId == 0 ? null : job.nRelatedEventId);
            return;
        }

        if (!_queue.Writer.TryWrite(job))
        {
            _logger.LogWarning("簡訊發送佇列已滿（200 件），丟棄工作: SID={SID}, 項目 {Count} 個",
                job.szSID, job.items.Count);
            _ = _deliveryLogger.LogAsync(job.szSID, NotifyDeliveryLogger.Channel.Sms,
                NotifyDeliveryLogger.Status.AllFailed, "發送佇列已滿，工作被丟棄",
                job.nRelatedEventId == 0 ? null : job.nRelatedEventId);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                int nSuccess = 0, nFailed = 0, nQuotaSkipped = 0;
                var failedLabels = new List<string>();

                foreach (var item in job.items)
                {
                    if (ct.IsCancellationRequested) return;

                    if (!TryConsumeQuota())
                    {
                        nQuotaSkipped++;
                        continue;
                    }

                    var result = await _transport.SendAsync(item.szPhone, item.szText);
                    if (result.isSuccess) nSuccess++;
                    else { nFailed++; failedLabels.Add(item.szLabel); }
                }

                await LogJobSummaryAsync(job, nSuccess, nFailed, nQuotaSkipped, failedLabels);
            }
        }
        catch (OperationCanceledException) { /* 正常關閉 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "簡訊發送佇列 worker 異常終止");
        }
    }

    private async Task LogJobSummaryAsync(SmsJob job, int nSuccess, int nFailed, int nQuotaSkipped, List<string> failedLabels)
    {
        NotifyDeliveryLogger.Status status;
        if (nFailed == 0 && nQuotaSkipped == 0) status = NotifyDeliveryLogger.Status.AllSent;
        else if (nSuccess == 0 && nFailed == 0) status = NotifyDeliveryLogger.Status.RateLimited;
        else if (nSuccess == 0) status = NotifyDeliveryLogger.Status.AllFailed;
        else status = NotifyDeliveryLogger.Status.PartialFailed;

        string szPrefix = job.isRecovery ? "[恢復] " : (job.szSID == "_summary" ? "[摘要] " : string.Empty);
        var parts = new List<string> { $"{szPrefix}號碼 {job.items.Count} 個，成功 {nSuccess}" };
        if (nFailed > 0) parts.Add($"失敗 {nFailed}（{string.Join(",", failedLabels)}）");
        if (nQuotaSkipped > 0) parts.Add($"達每日上限略過 {nQuotaSkipped}");
        if (job.nBuffered > 0) parts.Add($"限流排入摘要 {job.nBuffered}");

        await _deliveryLogger.LogAsync(job.szSID, NotifyDeliveryLogger.Channel.Sms, status,
            string.Join("、", parts), job.nRelatedEventId == 0 ? null : job.nRelatedEventId);
    }

    // ── 每日額度 ──

    /// <summary>嘗試消耗一封每日額度；達上限回傳 false（DailyQuota<=0 = 不限制）</summary>
    private bool TryConsumeQuota()
    {
        lock (_quotaLock)
        {
            RollQuotaDateLocked();

            if (_setting.DailyQuota > 0 && _nSentToday >= _setting.DailyQuota)
            {
                if (!_isQuotaAlarmLogged)
                {
                    _isQuotaAlarmLogged = true;
                    _logger.LogWarning("已達每日簡訊上限 {Quota} 封，今日停發", _setting.DailyQuota);
                    _ = _deliveryLogger.LogAsync("_system", NotifyDeliveryLogger.Channel.Sms,
                        NotifyDeliveryLogger.Status.RateLimited,
                        $"已達每日簡訊上限 {_setting.DailyQuota} 封，今日停止發送（明日自動恢復）");
                }
                return false;
            }

            _nSentToday++;
            return true;
        }
    }

    private void RollQuotaDateLocked()
    {
        if (_dtQuotaDate != DateTime.Today)
        {
            _dtQuotaDate = DateTime.Today;
            _nSentToday = 0;
            _isQuotaAlarmLogged = false;
        }
    }

    // ── 訊息格式化（UCS2 單封 70 字上限，模板精簡、SmsModemClient 端仍會保險截斷）──

    private string FormatSingleAlert(NotifyContext ctx, string szLanguage, bool isRecovery)
    {
        var szSeverity = _localizer.SeverityLabel(szLanguage, ctx.nSeverity);
        var szDescription = _localizer.Format(szLanguage, ctx.szMessageKey, ctx.args);

        var args = new Dictionary<string, string?>
        {
            ["severity"] = szSeverity,
            ["time"] = ctx.dtTime.ToString("MM/dd HH:mm"),
            ["message"] = szDescription
        };

        var szKey = isRecovery ? "notify.body.cleared.sms" : "notify.body.triggered.sms";
        return _localizer.Format(szLanguage, szKey, args);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _flushTimer.Dispose();
        _cts.Dispose();
    }

    // ── 內部狀態 ──

    private class PhoneRateState
    {
        public DateTime WindowStart { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
        public List<BufferedMessage> Buffer { get; } = new();
        public object Lock { get; } = new();
    }

    private class BufferedMessage
    {
        public byte nSeverity;
        public DateTime dtTime;
        public string szLanguage = "zh-TW";
        public string szLabel = string.Empty;
    }

    private class SendItem
    {
        public string szPhone = string.Empty;
        public string szLabel = string.Empty;
        public string szText = string.Empty;
    }

    private class SmsJob
    {
        public string szSID = string.Empty;
        public long nRelatedEventId;
        public bool isRecovery;
        public int nBuffered;
        public List<SendItem> items { get; } = new();
    }
}
