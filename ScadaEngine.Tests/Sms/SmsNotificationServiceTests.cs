using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Engine.Services;

namespace ScadaEngine.Tests.Sms;

/// <summary>
/// 鎖住 SmsNotificationService 路由與費用防護：
///   severity 路由（MaxSeverity 邊界）、Critical 繞過限流、每分鐘限流擋下超量、
///   DailyQuota 硬上限與 0=不限制、SendRecovery=false 不發恢復簡訊。
/// 對錯 = 該收到警報的人沒收到，或警報風暴把簡訊費燒光。
/// 發送走背景佇列 → 以輪詢等待假 transport 收到預期封數。
/// </summary>
public class SmsNotificationServiceTests
{
    // ── 測試替身 ──

    private class FakeTransport : ISmsTransport
    {
        public readonly List<(string szPhone, string szText)> Sent = new();
        public event Action<SmsModemStatus>? StatusChanged { add { } remove { } }
        public Task InitializeAsync(SmsSettingModel setting) => Task.CompletedTask;
        public Task<bool> RescanAsync() => Task.FromResult(true);
        public Task<bool> ReloadAsync(SmsSettingModel setting) => Task.FromResult(true);
        public SmsModemStatus GetStatus() => new();
        public Task<SmsSendResult> SendAsync(string szPhoneNumber, string szText)
        {
            lock (Sent) Sent.Add((szPhoneNumber, szText));
            return Task.FromResult(SmsSendResult.Ok());
        }
        public int Count { get { lock (Sent) return Sent.Count; } }
    }

    private class FakeRepo : SmsTargetRepository
    {
        private readonly IReadOnlyList<SmsNotifyTargetModel> _targets;
        public FakeRepo(IReadOnlyList<SmsNotifyTargetModel> targets)
            : base(NullLogger<SmsTargetRepository>.Instance,
                   new DatabaseConfigService(NullLogger<DatabaseConfigService>.Instance, "./nonexistent.json"))
        {
            _targets = targets;
        }
        public override Task<IReadOnlyList<SmsNotifyTargetModel>> GetEnabledTargetsAsync()
            => Task.FromResult(_targets);
    }

    private class FakeDeliveryLogger : NotifyDeliveryLogger
    {
        public FakeDeliveryLogger()
            : base(NullLogger<NotifyDeliveryLogger>.Instance,
                   new DatabaseConfigService(NullLogger<DatabaseConfigService>.Instance, "./nonexistent.json")) { }
        public override Task LogAsync(string szSID, Channel channel, Status status, string szDetail, long? nRelatedEventId = null)
            => Task.CompletedTask; // 測試不落 DB
    }

    private static SmsNotifyTargetModel Target(string szPhone, byte nMaxSeverity, string szLang = "zh-TW") => new()
    {
        nId = 1,
        szPhoneNumber = szPhone,
        szLabel = szPhone,
        nMaxSeverity = nMaxSeverity,
        szLanguage = szLang,
        isEnabled = true
    };

    private static NotifyContext Ctx(byte nSeverity, string szSid = "1-S1") => new()
    {
        nSeverity = nSeverity,
        szSID = szSid,
        szName = "TestPoint",
        szMessageKey = "alarm.high_exceed",
        args = new Dictionary<string, string?> { ["name"] = "TestPoint", ["threshold"] = "100" },
        dtTime = DateTime.Now,
        nRelatedEventId = 99,
        nAlarmRuleId = 1
    };

    private static async Task<SmsNotificationService> CreateAsync(
        FakeTransport transport, IReadOnlyList<SmsNotifyTargetModel> targets, SmsSettingModel setting)
    {
        var svc = new SmsNotificationService(
            NullLogger<SmsNotificationService>.Instance,
            new FakeRepo(targets),
            transport,
            new NotificationLocalizer(NullLogger<NotificationLocalizer>.Instance),
            new FakeDeliveryLogger());
        await svc.InitializeAsync(setting);
        return svc;
    }

    /// <summary>等背景佇列把預期封數送完（最長 5 秒）</summary>
    private static async Task WaitForSendsAsync(FakeTransport transport, int nExpected)
    {
        var dtDeadline = DateTime.UtcNow.AddSeconds(5);
        while (transport.Count < nExpected && DateTime.UtcNow < dtDeadline)
            await Task.Delay(20);
    }

    // ── severity 路由 ──

    [Fact]
    public async Task 路由_只有MaxSeverity大於等於警報嚴重度的號碼收到()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel>
        {
            Target("0911111111", 0),  // 只收 Critical → severity=2 不收
            Target("0922222222", 1),  // 收到 High → 不收
            Target("0933333333", 2),  // 收到 Medium → 收
            Target("0944444444", 3)   // 全收 → 收
        };
        using var svc = await CreateAsync(transport, targets, new SmsSettingModel { EnableNotification = true });

        await svc.NotifyAsync(Ctx(nSeverity: 2));
        await WaitForSendsAsync(transport, 2);

        Assert.Equal(2, transport.Count);
        var phones = transport.Sent.Select(s => s.szPhone).ToList();
        Assert.Contains("0933333333", phones);
        Assert.Contains("0944444444", phones);
    }

    // ── 限流 ──

    [Fact]
    public async Task 限流_超過每分鐘上限的非Critical進buffer不即時發送()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = true, RatePerMinute = 2 });

        for (int i = 0; i < 5; i++)
            await svc.NotifyAsync(Ctx(nSeverity: 1));
        await WaitForSendsAsync(transport, 2);
        await Task.Delay(200); // 確認不會多送

        Assert.Equal(2, transport.Count);
    }

    [Fact]
    public async Task 限流_Critical繞過限流全數送出()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = true, RatePerMinute = 1 });

        for (int i = 0; i < 3; i++)
            await svc.NotifyAsync(Ctx(nSeverity: 0));
        await WaitForSendsAsync(transport, 3);

        Assert.Equal(3, transport.Count);
    }

    // ── 每日上限 ──

    [Fact]
    public async Task 每日上限_達標後停發含Critical()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = true, RatePerMinute = 100, DailyQuota = 3 });

        for (int i = 0; i < 6; i++)
            await svc.NotifyAsync(Ctx(nSeverity: 0));
        await WaitForSendsAsync(transport, 3);
        await Task.Delay(200);

        Assert.Equal(3, transport.Count);
    }

    [Fact]
    public async Task 每日上限_設0為不限制()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = true, RatePerMinute = 100, DailyQuota = 0 });

        for (int i = 0; i < 5; i++)
            await svc.NotifyAsync(Ctx(nSeverity: 0));
        await WaitForSendsAsync(transport, 5);

        Assert.Equal(5, transport.Count);
    }

    // ── 恢復通知開關 ──

    [Fact]
    public async Task 恢復通知_關閉時不發送()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = true, SendRecovery = false });

        await svc.NotifyClearedAsync(Ctx(nSeverity: 1));
        await Task.Delay(300);

        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task 恢復通知_開啟時發送()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = true, SendRecovery = true });

        await svc.NotifyClearedAsync(Ctx(nSeverity: 1));
        await WaitForSendsAsync(transport, 1);

        Assert.Equal(1, transport.Count);
    }

    // ── 總開關 ──

    [Fact]
    public async Task 總開關_關閉時完全不發送()
    {
        var transport = new FakeTransport();
        var targets = new List<SmsNotifyTargetModel> { Target("0911111111", 3) };
        using var svc = await CreateAsync(transport, targets,
            new SmsSettingModel { EnableNotification = false });

        await svc.NotifyAsync(Ctx(nSeverity: 0));
        await Task.Delay(300);

        Assert.Equal(0, transport.Count);
    }
}
