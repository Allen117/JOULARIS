using ScadaEngine.Common.Data.Services;
using Xunit;

namespace ScadaEngine.Tests.Common;

/// <summary>
/// 鎖住 dbSetting.json 四層解析順序（環境變數 → ..\..\Shared → 本機 Setting → 開發 repo）。
/// 每個測試用獨立暫存目錄模擬 C:\JOULARIS\&lt;App&gt;\App\ 佈局與 repo 佈局，不碰真實環境變數。
/// 重點：Shared 存在時本機 Setting 複本「一定輸」— 這是當年 Web 讀到 stale 複本的 bug 根因。
/// </summary>
public class DbSettingLocatorTests : IDisposable
{
    private readonly string _szRoot;

    // 模擬 C:\JOULARIS\<App>\App\ 佈局：Root\Engine\App\ 為 BaseDirectory，Root\Shared\ 為共用檔位置
    private readonly string _szBaseDir;
    private readonly string _szSharedFile;
    private readonly string _szLocalFile;

    // 模擬開發 repo 佈局：Root\repo\ScadaEngine.Web\ 為 CWD，Root\repo\ScadaEngine.Engine\Setting\ 為第 4 層
    private readonly string _szCwd;
    private readonly string _szDevFile;

    public DbSettingLocatorTests()
    {
        _szRoot = Path.Combine(Path.GetTempPath(), "DbSettingLocatorTests_" + Guid.NewGuid().ToString("N"));
        _szBaseDir = Path.Combine(_szRoot, "Engine", "App");
        _szSharedFile = Path.Combine(_szRoot, "Shared", DbSettingLocator.FileName);
        _szLocalFile = Path.Combine(_szBaseDir, "Setting", DbSettingLocator.FileName);
        _szCwd = Path.Combine(_szRoot, "repo", "ScadaEngine.Web");
        _szDevFile = Path.Combine(_szRoot, "repo", "ScadaEngine.Engine", "Setting", DbSettingLocator.FileName);

        Directory.CreateDirectory(_szBaseDir);
        Directory.CreateDirectory(_szCwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szRoot, recursive: true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    private static void Touch(string szPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(szPath)!);
        File.WriteAllText(szPath, "{}");
    }

    [Fact]
    public void Candidates_Are_In_Fixed_Order_And_Env_Only_When_Set()
    {
        var withEnv = DbSettingLocator.BuildCandidates(_szBaseDir, _szCwd, @"C:\x\dbSetting.json");
        Assert.Equal(DbSettingSource.EnvironmentVariable, withEnv[0].eSource);
        Assert.Equal(DbSettingSource.Shared, withEnv[1].eSource);
        Assert.Equal(DbSettingSource.LocalSetting, withEnv[2].eSource);
        // 第 4 層（開發 repo）可有多個候選（CWD 上一層 + BaseDirectory 祖先鏈），但一律排在最後
        Assert.True(withEnv.Count >= 4);
        Assert.All(withEnv.Skip(3), c => Assert.Equal(DbSettingSource.DevRepo, c.eSource));

        var noEnv = DbSettingLocator.BuildCandidates(_szBaseDir, _szCwd, null);
        Assert.Equal(withEnv.Count - 1, noEnv.Count);
        Assert.Equal(DbSettingSource.Shared, noEnv[0].eSource);

        var blankEnv = DbSettingLocator.BuildCandidates(_szBaseDir, _szCwd, "   ");
        Assert.Equal(noEnv.Count, blankEnv.Count);
    }

    [Fact]
    public void Shared_Path_Is_Two_Levels_Above_BaseDirectory()
    {
        var candidates = DbSettingLocator.BuildCandidates(_szBaseDir, _szCwd, null);
        Assert.Equal(Path.GetFullPath(_szSharedFile), candidates[0].szPath);
        Assert.Equal(Path.GetFullPath(_szLocalFile), candidates[1].szPath);
        Assert.Equal(Path.GetFullPath(_szDevFile), candidates[2].szPath);
    }

    [Fact]
    public void Env_File_Exists_Wins_Over_Everything()
    {
        var szEnvFile = Path.Combine(_szRoot, "override", "my.json");
        Touch(szEnvFile);
        Touch(_szSharedFile);
        Touch(_szLocalFile);
        Touch(_szDevFile);

        var r = DbSettingLocator.Resolve(_szBaseDir, _szCwd, szEnvFile);

        Assert.Equal(DbSettingSource.EnvironmentVariable, r.eSource);
        Assert.Equal(Path.GetFullPath(szEnvFile), r.szPath);
        Assert.Equal("環境變數", r.szSourceLabel);
    }

    [Fact]
    public void Env_Set_But_File_Missing_Falls_Through()
    {
        Touch(_szSharedFile);

        var szEnvMissing = Path.Combine(_szRoot, "nope.json");
        var r = DbSettingLocator.Resolve(_szBaseDir, _szCwd, szEnvMissing);

        Assert.Equal(DbSettingSource.Shared, r.eSource);
        Assert.Equal(Path.GetFullPath(_szSharedFile), r.szPath);
        // 搜尋清單仍記錄環境變數那條（排第一），供 log 說明為何沒選它
        Assert.Equal(Path.GetFullPath(szEnvMissing), r.szSearchedPaths[0]);
    }

    [Fact]
    public void Shared_Beats_Local_Setting_When_Both_Exist()
    {
        Touch(_szSharedFile);
        Touch(_szLocalFile);

        var r = DbSettingLocator.Resolve(_szBaseDir, _szCwd, null);

        Assert.Equal(DbSettingSource.Shared, r.eSource);
        Assert.Equal(Path.GetFullPath(_szSharedFile), r.szPath);
        Assert.Equal("Shared", r.szSourceLabel);
    }

    [Fact]
    public void Local_Setting_Used_When_No_Shared()
    {
        Touch(_szLocalFile);
        Touch(_szDevFile);

        var r = DbSettingLocator.Resolve(_szBaseDir, _szCwd, null);

        Assert.Equal(DbSettingSource.LocalSetting, r.eSource);
        Assert.Equal(Path.GetFullPath(_szLocalFile), r.szPath);
        Assert.Equal("本機 Setting", r.szSourceLabel);
    }

    [Fact]
    public void Dev_Repo_Used_When_Only_It_Exists()
    {
        Touch(_szDevFile);

        var r = DbSettingLocator.Resolve(_szBaseDir, _szCwd, null);

        Assert.Equal(DbSettingSource.DevRepo, r.eSource);
        Assert.Equal(Path.GetFullPath(_szDevFile), r.szPath);
        Assert.Equal("開發 repo", r.szSourceLabel);
    }

    [Fact]
    public void Nothing_Found_Returns_NotFound_Without_Throwing()
    {
        var r = DbSettingLocator.Resolve(_szBaseDir, _szCwd, null);

        Assert.Equal(DbSettingSource.NotFound, r.eSource);
        Assert.Null(r.szPath);
        Assert.Equal("未找到", r.szSourceLabel);
        Assert.True(r.szSearchedPaths.Count >= 3);
    }

    [Fact]
    public void Dev_Repo_Found_By_Walking_Up_From_BaseDirectory_When_Cwd_Is_Useless()
    {
        // ModbusServer 啟動時把 CWD 切到 BaseDirectory（bin\Debug\net8.0），CWD 上一層找不到 repo，
        // 須靠 BaseDirectory 祖先鏈找到 repo\ScadaEngine.Engine\Setting\dbSetting.json
        Touch(_szDevFile);
        var szGwBinBase = Path.Combine(_szRoot, "repo", "ScadaEngine.ModbusServer", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(szGwBinBase);

        var r = DbSettingLocator.Resolve(szGwBinBase, szGwBinBase, null);

        Assert.Equal(DbSettingSource.DevRepo, r.eSource);
        Assert.Equal(Path.GetFullPath(_szDevFile), r.szPath);
    }

    [Fact]
    public void Production_Layout_Does_Not_Hit_Dev_Repo_Candidates()
    {
        // 生產佈局（Root\Engine\App）往上走到 Root 都沒有 ScadaEngine.Engine 資料夾 → 不得誤判為開發 repo
        Touch(_szLocalFile);

        var r = DbSettingLocator.Resolve(_szBaseDir, _szBaseDir, null);

        Assert.Equal(DbSettingSource.LocalSetting, r.eSource);
    }

    [Fact]
    public void Three_Apps_On_Same_Machine_Resolve_To_Same_Shared_File()
    {
        // C:\JOULARIS\Engine\App、C:\JOULARIS\Web\App、C:\JOULARIS\ModbusServer\App 三個 BaseDirectory → 同一份 Shared
        Touch(_szSharedFile);
        var szWebBase = Path.Combine(_szRoot, "Web", "App");
        var szGwBase = Path.Combine(_szRoot, "ModbusServer", "App");
        Directory.CreateDirectory(szWebBase);
        Directory.CreateDirectory(szGwBase);

        var engine = DbSettingLocator.Resolve(_szBaseDir, _szCwd, null);
        var web = DbSettingLocator.Resolve(szWebBase, _szCwd, null);
        var gw = DbSettingLocator.Resolve(szGwBase, _szCwd, null);

        Assert.All(new[] { engine, web, gw }, r => Assert.Equal(DbSettingSource.Shared, r.eSource));
        Assert.Equal(engine.szPath, web.szPath);
        Assert.Equal(engine.szPath, gw.szPath);
    }
}
