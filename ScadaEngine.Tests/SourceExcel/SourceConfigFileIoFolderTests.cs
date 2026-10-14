using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Web.Services.SourceExcel;
using Xunit;

namespace ScadaEngine.Tests.SourceExcel;

/// <summary>
/// 鎖住 SourceConfigFileIo.GetWatchedFolder 的「相對 ContentRoot 解析」，以及 repo 內 appsettings 的實際值：
/// - 生產 appsettings.json：../../Engine/App/&lt;X&gt;（Web\App → 安裝根目錄\Engine\App\&lt;X&gt;），不寫死安裝根目錄
/// - 開發 appsettings.Development.json：落在 repo 的 ScadaEngine.Engine 底下（dev Engine 實際讀的位置），不碰生產 Engine 資料夾
/// </summary>
public class SourceConfigFileIoFolderTests
{
    private static readonly string[] c_szSections =
    {
        SourceConfigFileIo.MODBUS_SECTION, SourceConfigFileIo.OPCUA_SECTION, SourceConfigFileIo.DBPOINT_SECTION,
    };

    private static SourceConfigFileIo CreateIo(string szContentRoot, IDictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new SourceConfigFileIo(config, new StubEnv(szContentRoot), NullLogger<SourceConfigFileIo>.Instance);
    }

    [Fact]
    public void Relative_WatchedFolder_Resolves_Against_ContentRoot()
    {
        var io = CreateIo(@"C:\JOULARIS\Web\App\", new Dictionary<string, string?>
        {
            [$"{SourceConfigFileIo.MODBUS_SECTION}:WatchedFolder"] = "../../Engine/App/Modbus",
        });

        Assert.Equal(@"C:\JOULARIS\Engine\App\Modbus", io.GetWatchedFolder(SourceConfigFileIo.MODBUS_SECTION));
    }

    [Fact]
    public void Missing_WatchedFolder_Returns_Null()
    {
        var io = CreateIo(@"C:\JOULARIS\Web\App\", new Dictionary<string, string?>());

        Assert.Null(io.GetWatchedFolder(SourceConfigFileIo.MODBUS_SECTION));
    }

    [Theory]
    [InlineData("appsettings.json", @"C:\JOULARIS\Web\App\", @"C:\JOULARIS\Engine\App\", false)]
    [InlineData("appsettings.Development.json", @"C:\repo\ScadaEngine.Web\", @"C:\repo\ScadaEngine.Engine\", true)]
    public void Repo_Appsettings_WatchedFolders_Are_Relative_And_Land_In_Engine(
        string szFileName, string szContentRoot, string szExpectedPrefix, bool isMirrorAllowed)
    {
        var szPath = Path.Combine(FindRepoRoot(), "ScadaEngine.Web", szFileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(szPath));

        foreach (var szSection in c_szSections)
        {
            var szWatched = doc.RootElement.GetProperty(szSection).GetProperty("WatchedFolder").GetString()!;
            Assert.False(Path.IsPathRooted(szWatched), $"{szFileName} {szSection} 不可寫死絕對路徑：{szWatched}");
            // 正式環境不鏡像（Engine\App 就是唯一一份）；dev 的 Modbus / DBPoint 寫 Engine bin 後鏡像回 repo
            if (!isMirrorAllowed)
                Assert.False(doc.RootElement.GetProperty(szSection).TryGetProperty("MirrorFolder", out _),
                    $"{szFileName} {szSection} 不應設 MirrorFolder");

            var io = CreateIo(szContentRoot, new Dictionary<string, string?> { [$"{szSection}:WatchedFolder"] = szWatched });
            Assert.StartsWith(szExpectedPrefix, io.GetWatchedFolder(szSection));
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScadaEngine.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 ScadaEngine.sln（測試需在 repo 內執行）");
    }

    private sealed class StubEnv : IWebHostEnvironment
    {
        public StubEnv(string szRoot) { ContentRootPath = szRoot; WebRootPath = szRoot; }
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
