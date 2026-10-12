using ScadaEngine.Common.Data.Services;
using Xunit;

namespace ScadaEngine.Tests.Common;

/// <summary>
/// 鎖住共用設定檔（EmailSetting / SmsSetting / LineSetting）的解析順序：
/// ..\..\Shared → 本機 Setting → 開發 repo（通知三檔無環境變數層）。
/// 每個測試用獨立暫存目錄模擬 C:\SCADA\&lt;App&gt;\App\ 佈局與 repo 佈局。
/// 重點：Shared 存在時本機 Setting 複本「一定輸」，且 Engine / Web 兩個 BaseDirectory 解析到同一份。
/// </summary>
public class SharedSettingLocatorTests : IDisposable
{
    private static readonly string[] c_szNotifyFiles = { "EmailSetting.json", "SmsSetting.json", "LineSetting.json" };

    private readonly string _szRoot;
    private readonly string _szEngineBase;
    private readonly string _szWebBase;
    private readonly string _szCwd;

    public SharedSettingLocatorTests()
    {
        _szRoot = Path.Combine(Path.GetTempPath(), "SharedSettingLocatorTests_" + Guid.NewGuid().ToString("N"));
        _szEngineBase = Path.Combine(_szRoot, "Engine", "App");
        _szWebBase = Path.Combine(_szRoot, "Web", "App");
        _szCwd = Path.Combine(_szRoot, "repo", "ScadaEngine.Web");

        Directory.CreateDirectory(_szEngineBase);
        Directory.CreateDirectory(_szWebBase);
        Directory.CreateDirectory(_szCwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szRoot, recursive: true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    private string SharedFile(string szFileName) => Path.Combine(_szRoot, "Shared", szFileName);
    private string LocalFile(string szFileName) => Path.Combine(_szEngineBase, "Setting", szFileName);
    private string DevFile(string szFileName) => Path.Combine(_szRoot, "repo", "ScadaEngine.Engine", "Setting", szFileName);

    private static void Touch(string szPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(szPath)!);
        File.WriteAllText(szPath, "{}");
    }

    [Theory]
    [InlineData("EmailSetting.json")]
    [InlineData("SmsSetting.json")]
    [InlineData("LineSetting.json")]
    public void Candidates_Without_Env_Start_At_Shared_Then_Local_Then_DevRepo(string szFileName)
    {
        var candidates = SharedSettingLocator.BuildCandidates(szFileName, _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.Shared, candidates[0].eSource);
        Assert.Equal(Path.GetFullPath(SharedFile(szFileName)), candidates[0].szPath);
        Assert.Equal(SharedSettingSource.LocalSetting, candidates[1].eSource);
        Assert.Equal(Path.GetFullPath(LocalFile(szFileName)), candidates[1].szPath);
        Assert.All(candidates.Skip(2), c => Assert.Equal(SharedSettingSource.DevRepo, c.eSource));
        Assert.Equal(Path.GetFullPath(DevFile(szFileName)), candidates[2].szPath);
        // 候選路徑一律以該檔名結尾（不會混到別檔）
        Assert.All(candidates, c => Assert.EndsWith(szFileName, c.szPath));
    }

    [Fact]
    public void Env_Layer_Only_Present_When_Caller_Supplies_It()
    {
        var withEnv = SharedSettingLocator.BuildCandidates("EmailSetting.json", _szEngineBase, _szCwd, @"C:\x\e.json");
        var noEnv = SharedSettingLocator.BuildCandidates("EmailSetting.json", _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.EnvironmentVariable, withEnv[0].eSource);
        Assert.Equal(withEnv.Count - 1, noEnv.Count);
        Assert.DoesNotContain(noEnv, c => c.eSource == SharedSettingSource.EnvironmentVariable);
    }

    [Theory]
    [InlineData("EmailSetting.json")]
    [InlineData("SmsSetting.json")]
    [InlineData("LineSetting.json")]
    public void Shared_Beats_Local_And_DevRepo_When_All_Exist(string szFileName)
    {
        Touch(SharedFile(szFileName));
        Touch(LocalFile(szFileName));
        Touch(DevFile(szFileName));

        var r = SharedSettingLocator.Resolve(szFileName, _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.Shared, r.eSource);
        Assert.Equal(Path.GetFullPath(SharedFile(szFileName)), r.szPath);
        Assert.Equal("Shared", r.szSourceLabel);
        Assert.Equal(szFileName, r.szFileName);
    }

    [Theory]
    [InlineData("EmailSetting.json")]
    [InlineData("SmsSetting.json")]
    [InlineData("LineSetting.json")]
    public void Local_Setting_Used_When_No_Shared(string szFileName)
    {
        Touch(LocalFile(szFileName));
        Touch(DevFile(szFileName));

        var r = SharedSettingLocator.Resolve(szFileName, _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.LocalSetting, r.eSource);
        Assert.Equal(Path.GetFullPath(LocalFile(szFileName)), r.szPath);
        Assert.Equal("本機 Setting", r.szSourceLabel);
    }

    [Theory]
    [InlineData("EmailSetting.json")]
    [InlineData("SmsSetting.json")]
    [InlineData("LineSetting.json")]
    public void Dev_Repo_Used_When_Only_It_Exists(string szFileName)
    {
        Touch(DevFile(szFileName));

        var r = SharedSettingLocator.Resolve(szFileName, _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.DevRepo, r.eSource);
        Assert.Equal(Path.GetFullPath(DevFile(szFileName)), r.szPath);
        Assert.Equal("開發 repo", r.szSourceLabel);
    }

    [Theory]
    [InlineData("EmailSetting.json")]
    [InlineData("SmsSetting.json")]
    [InlineData("LineSetting.json")]
    public void Nothing_Found_Returns_NotFound_Without_Throwing(string szFileName)
    {
        var r = SharedSettingLocator.Resolve(szFileName, _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.NotFound, r.eSource);
        Assert.Null(r.szPath);
        Assert.Equal("未找到", r.szSourceLabel);
        Assert.True(r.szSearchedPaths.Count >= 3);
    }

    [Fact]
    public void Files_Resolve_Independently_Of_Each_Other()
    {
        // Email 在 Shared、Sms 只在本機、Line 只在 repo：三者各自選對，不互相影響
        Touch(SharedFile("EmailSetting.json"));
        Touch(LocalFile("SmsSetting.json"));
        Touch(DevFile("LineSetting.json"));

        var email = SharedSettingLocator.Resolve("EmailSetting.json", _szEngineBase, _szCwd, null);
        var sms = SharedSettingLocator.Resolve("SmsSetting.json", _szEngineBase, _szCwd, null);
        var line = SharedSettingLocator.Resolve("LineSetting.json", _szEngineBase, _szCwd, null);

        Assert.Equal(SharedSettingSource.Shared, email.eSource);
        Assert.Equal(SharedSettingSource.LocalSetting, sms.eSource);
        Assert.Equal(SharedSettingSource.DevRepo, line.eSource);
    }

    [Fact]
    public void Engine_And_Web_Resolve_To_Same_Shared_File()
    {
        foreach (var szFile in c_szNotifyFiles)
        {
            Touch(SharedFile(szFile));
            // Web 本機殘留複本也要輸給 Shared
            Touch(Path.Combine(_szWebBase, "Setting", szFile));

            var engine = SharedSettingLocator.Resolve(szFile, _szEngineBase, _szCwd, null);
            var web = SharedSettingLocator.Resolve(szFile, _szWebBase, _szCwd, null);

            Assert.Equal(SharedSettingSource.Shared, engine.eSource);
            Assert.Equal(SharedSettingSource.Shared, web.eSource);
            Assert.Equal(engine.szPath, web.szPath);
        }
    }

    [Fact]
    public void Production_Layout_Does_Not_Hit_Dev_Repo_Candidates()
    {
        Touch(LocalFile("EmailSetting.json"));

        var r = SharedSettingLocator.Resolve("EmailSetting.json", _szEngineBase, _szEngineBase, null);

        Assert.Equal(SharedSettingSource.LocalSetting, r.eSource);
    }

    [Fact]
    public void Fallback_Write_Path_Is_Shared_When_Shared_Folder_Exists()
    {
        // 生產佈局：Shared 資料夾在、但該檔被手動刪除 → Web 存檔應寫回 Shared，不可寫到 CWD 相對路徑
        Directory.CreateDirectory(Path.Combine(_szRoot, "Shared"));

        var szPath = SharedSettingLocator.FallbackWritePath("EmailSetting.json", _szEngineBase, _szEngineBase);

        Assert.Equal(Path.GetFullPath(SharedFile("EmailSetting.json")), szPath);
    }

    [Fact]
    public void Fallback_Write_Path_Is_Dev_Repo_When_No_Shared_Folder()
    {
        var szPath = SharedSettingLocator.FallbackWritePath("EmailSetting.json", _szEngineBase, _szCwd);

        Assert.Equal(Path.GetFullPath(DevFile("EmailSetting.json")), szPath);
        Assert.True(Path.IsPathRooted(szPath));
    }

    [Fact]
    public void Blank_FileName_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            SharedSettingLocator.BuildCandidates(" ", _szEngineBase, _szCwd, null));
    }
}
