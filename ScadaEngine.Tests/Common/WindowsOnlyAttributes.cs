using Xunit;

namespace ScadaEngine.Tests.Common;

/// <summary>
/// 僅在 Windows 執行的 Fact：測試內容依賴 Windows 路徑語意（磁碟機代號、反斜線分隔）。
/// 在 macOS / Linux（例如用 BuildRelease.command 的開發機）上自動標記 Skip，避免整套測試被平台差異擋紅燈。
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "僅 Windows 路徑語意適用，非 Windows 平台略過";
    }
}

/// <summary>同 <see cref="WindowsOnlyFactAttribute"/>，Theory 版。</summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "僅 Windows 路徑語意適用，非 Windows 平台略過";
    }
}
