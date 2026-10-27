using ScadaEngine.Engine.Services;
using Xunit;

namespace ScadaEngine.Tests.Common;

/// <summary>
/// 鎖住 LicenseService 的 Bridge exe 路徑推算：由 Engine 執行檔目錄上兩層（安裝根目錄）\LicenseBridge\。
/// 程式碼不寫死安裝根目錄（C:\JOULARIS 等），根目錄由 Install.bat 的 %_ROOT% 決定，搬家自動跟著走。
/// </summary>
public class LicenseBridgePathTests
{
    [WindowsOnlyTheory]
    [InlineData(@"C:\JOULARIS\Engine\App\", @"C:\JOULARIS\LicenseBridge\ScadaEngine.LicenseBridge.exe")]
    [InlineData(@"C:\JOULARIS\Engine\App", @"C:\JOULARIS\LicenseBridge\ScadaEngine.LicenseBridge.exe")]
    [InlineData(@"D:\Site\Engine\App\", @"D:\Site\LicenseBridge\ScadaEngine.LicenseBridge.exe")]
    public void Resolves_Two_Levels_Up_Into_LicenseBridge(string szBaseDir, string szExpected)
    {
        Assert.Equal(szExpected, LicenseService.ResolveBridgeExePath(szBaseDir));
    }

    [WindowsOnlyFact]
    public void Dev_Bin_Path_Resolves_Without_Throwing_Even_If_Missing()
    {
        // 開發 bin（repo\ScadaEngine.Engine\bin\Debug\net8.0\）推算出的路徑不存在屬正常，只供 log
        var szPath = LicenseService.ResolveBridgeExePath(@"C:\repo\ScadaEngine.Engine\bin\Debug\net8.0\");

        Assert.Equal(@"C:\repo\ScadaEngine.Engine\bin\LicenseBridge\ScadaEngine.LicenseBridge.exe", szPath);
    }
}
