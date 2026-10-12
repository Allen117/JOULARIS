using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Engine.Services;
using Xunit;

namespace ScadaEngine.Tests.Notification;

/// <summary>
/// 鎖住通知設定熱重載的三個不變量：
/// (1) payload 檔名只重載對應檔、(2) 未知檔名不碰任何服務、(3) 解析失敗 / 找不到 → 不呼叫套用（保留舊設定）。
/// 以 fake applier 記錄呼叫，路徑解析指到暫存目錄，不碰 DB（configService=null → 不寫 EventLog）。
/// </summary>
public class NotifySettingReloaderTests : IDisposable
{
    private sealed class FakeApplier : INotifySettingApplier
    {
        public List<string> Calls { get; } = new();
        public LineSettingModel? Line;
        public EmailSettingModel? Email;
        public SmsSettingModel? Sms;
        public Exception? ThrowOnApply;

        public Task ApplyLineAsync(LineSettingModel setting) { Calls.Add("Line"); Line = setting; return Throw(); }
        public Task ApplyEmailAsync(EmailSettingModel setting) { Calls.Add("Email"); Email = setting; return Throw(); }
        public Task ApplySmsAsync(SmsSettingModel setting) { Calls.Add("Sms"); Sms = setting; return Throw(); }

        private Task Throw() => ThrowOnApply != null ? Task.FromException(ThrowOnApply) : Task.CompletedTask;
    }

    private readonly string _szDir;
    private readonly FakeApplier _applier = new();
    private readonly NotifySettingReloader _reloader;

    public NotifySettingReloaderTests()
    {
        _szDir = Path.Combine(Path.GetTempPath(), "NotifySettingReloaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_szDir);

        _reloader = new NotifySettingReloader(
            NullLogger<NotifySettingReloader>.Instance,
            _applier,
            configService: null,
            locate: Locate);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szDir, recursive: true); } catch { }
    }

    /// <summary>模擬 SharedSettingLocator：暫存目錄內有該檔 → Shared，否則 NotFound</summary>
    private SharedSettingLocation Locate(string szFileName)
    {
        var szPath = Path.Combine(_szDir, szFileName);
        return File.Exists(szPath)
            ? new SharedSettingLocation(szFileName, szPath, SharedSettingSource.Shared, new[] { szPath })
            : new SharedSettingLocation(szFileName, null, SharedSettingSource.NotFound, new[] { szPath });
    }

    private void WriteFile(string szFileName, string szContent)
        => File.WriteAllText(Path.Combine(_szDir, szFileName), szContent);

    [Fact]
    public async Task Email_Payload_Reloads_Only_Email()
    {
        WriteFile("EmailSetting.json", """{"EnableNotification":true,"SmtpHost":"smtp.new.local","SmtpPort":2525,"FromAddress":"a@b"}""");
        WriteFile("SmsSetting.json", """{"EnableNotification":true,"ComPort":"COM9"}""");

        var r = await _reloader.ReloadAsync("EmailSetting.json");

        Assert.True(r.isSuccess);
        Assert.Equal(new[] { "Email" }, _applier.Calls);
        Assert.Equal("smtp.new.local", _applier.Email!.SmtpHost);
        Assert.Equal(2525, _applier.Email.SmtpPort);
        Assert.Contains("smtp.new.local", r.szMessage);
        Assert.Equal("Shared", r.szSource);
    }

    [Fact]
    public async Task Sms_Payload_Reloads_Only_Sms_Case_Insensitive()
    {
        WriteFile("SmsSetting.json", """{"EnableNotification":true,"ComPort":"COM7","BaudRate":9600}""");

        var r = await _reloader.ReloadAsync("smssetting.json");

        Assert.True(r.isSuccess);
        Assert.Equal(new[] { "Sms" }, _applier.Calls);
        Assert.Equal("COM7", _applier.Sms!.ComPort);
        Assert.Equal("SmsSetting.json", r.szFileName);
    }

    [Fact]
    public async Task Line_Payload_Reloads_Line_And_Never_Leaks_Token()
    {
        WriteFile("LineSetting.json", """{"EnableNotification":true,"ChannelAccessToken":"SECRET-TOKEN-123","RatePerMinute":5}""");

        var r = await _reloader.ReloadAsync("LineSetting.json");

        Assert.True(r.isSuccess);
        Assert.Equal(new[] { "Line" }, _applier.Calls);
        Assert.Equal("SECRET-TOKEN-123", _applier.Line!.ChannelAccessToken);
        Assert.DoesNotContain("SECRET-TOKEN-123", r.szMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dbSetting.json")]
    [InlineData("MqttSetting.json")]
    [InlineData("../EmailSetting.json")]
    public async Task Unknown_File_Is_Ignored_Without_Touching_Services(string? szFile)
    {
        WriteFile("EmailSetting.json", """{"EnableNotification":true,"SmtpHost":"x","FromAddress":"a@b"}""");

        var r = await _reloader.ReloadAsync(szFile);

        Assert.False(r.isSuccess);
        Assert.Empty(_applier.Calls);
    }

    [Fact]
    public async Task Broken_Json_Keeps_Old_Setting_And_Reports_Failure()
    {
        WriteFile("EmailSetting.json", "{ this is not json");

        var r = await _reloader.ReloadAsync("EmailSetting.json");

        Assert.False(r.isSuccess);
        Assert.Empty(_applier.Calls);
        Assert.Contains("保留舊設定", r.szMessage);
        Assert.Equal("EmailSetting.json", r.szFileName);
    }

    [Fact]
    public async Task Missing_File_Keeps_Old_Setting_And_Reports_Failure()
    {
        var r = await _reloader.ReloadAsync("SmsSetting.json");

        Assert.False(r.isSuccess);
        Assert.Empty(_applier.Calls);
        Assert.Null(r.szPath);
        Assert.Equal("未找到", r.szSource);
    }

    [Fact]
    public async Task Apply_Exception_Is_Reported_As_Failure_Not_Thrown()
    {
        WriteFile("SmsSetting.json", """{"EnableNotification":true,"ComPort":"COM3"}""");
        _applier.ThrowOnApply = new InvalidOperationException("port busy");

        var r = await _reloader.ReloadAsync("SmsSetting.json");

        Assert.False(r.isSuccess);
        Assert.Contains("port busy", r.szMessage);
        Assert.Contains("保留舊設定", r.szMessage);
    }

    [Fact]
    public async Task Reload_Succeeds_Again_After_A_Failure()
    {
        WriteFile("EmailSetting.json", "{ broken");
        var bad = await _reloader.ReloadAsync("EmailSetting.json");
        Assert.False(bad.isSuccess);

        WriteFile("EmailSetting.json", """{"EnableNotification":false,"SmtpHost":"ok.local","FromAddress":"a@b"}""");
        var good = await _reloader.ReloadAsync("EmailSetting.json");

        Assert.True(good.isSuccess);
        Assert.Equal(new[] { "Email" }, _applier.Calls);
    }
}
