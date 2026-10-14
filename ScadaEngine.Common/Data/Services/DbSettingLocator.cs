namespace ScadaEngine.Common.Data.Services;

/// <summary>
/// dbSetting.json 的來源層（供啟動 log 標示「實際選中哪一層」）
/// 與 <see cref="SharedSettingSource"/> 一一對應；保留獨立型別是為了不動既有呼叫端（DatabaseConfigService / 測試）。
/// </summary>
public enum DbSettingSource
{
    /// <summary>環境變數 JOULARIS_DBSETTING 指定的絕對路徑（測試 / 多實例覆寫）</summary>
    EnvironmentVariable,
    /// <summary>生產機共用檔 &lt;BaseDirectory&gt;\..\..\Shared\dbSetting.json（= C:\JOULARIS\Shared\）</summary>
    Shared,
    /// <summary>本機 App\Setting\dbSetting.json（單機獨立安裝，例如 Gateway 裝在遠端機器）</summary>
    LocalSetting,
    /// <summary>開發模式：&lt;CWD&gt;\..\ScadaEngine.Engine\Setting\dbSetting.json（三專案都指向 repo 內 Engine 那份）</summary>
    DevRepo,
    /// <summary>四層都找不到（DatabaseConfigService 會退回預設配置）</summary>
    NotFound
}

/// <summary>
/// dbSetting.json 路徑解析結果
/// </summary>
/// <param name="szPath">選中的絕對路徑；四層都不存在時為 null</param>
/// <param name="eSource">選中的層</param>
/// <param name="szSearchedPaths">依序檢查過的候選路徑（找不到時供 log 列出）</param>
public sealed record DbSettingLocation(string? szPath, DbSettingSource eSource, IReadOnlyList<string> szSearchedPaths)
{
    /// <summary>來源的中文顯示名（log 用）</summary>
    public string szSourceLabel => eSource switch
    {
        DbSettingSource.EnvironmentVariable => "環境變數",
        DbSettingSource.Shared => "Shared",
        DbSettingSource.LocalSetting => "本機 Setting",
        DbSettingSource.DevRepo => "開發 repo",
        _ => "未找到"
    };
}

/// <summary>
/// dbSetting.json 路徑解析器 — <see cref="SharedSettingLocator"/> 的薄包裝：
/// 四層順序（環境變數 <c>JOULARIS_DBSETTING</c> → ..\..\Shared → 本機 Setting → 開發 repo）由
/// SharedSettingLocator 統一實作，EmailSetting / SmsSetting / LineSetting 走同一套（但無環境變數層）。
/// 詳見 docs/架構.md §共用設定檔解析順序。
/// </summary>
public static class DbSettingLocator
{
    /// <summary>環境變數名稱（與 Windows 服務名 JOULARIS* 一致）</summary>
    public const string EnvVarName = "JOULARIS_DBSETTING";

    /// <summary>設定檔檔名</summary>
    public const string FileName = "dbSetting.json";

    /// <summary>
    /// 以執行環境實際值解析（AppContext.BaseDirectory / 目前工作目錄 / 環境變數）
    /// </summary>
    public static DbSettingLocation Resolve()
    {
        return ToDbLocation(SharedSettingLocator.Resolve(FileName, EnvVarName));
    }

    /// <summary>
    /// 以指定的三個輸入解析（單元測試用；所有路徑皆以 File.Exists 判定）
    /// </summary>
    /// <param name="szBaseDirectory">應用程式基礎目錄（AppContext.BaseDirectory）</param>
    /// <param name="szCurrentDirectory">目前工作目錄（Directory.GetCurrentDirectory()）</param>
    /// <param name="szEnvOverride">環境變數 JOULARIS_DBSETTING 的值；null / 空白視為未設定</param>
    public static DbSettingLocation Resolve(string szBaseDirectory, string szCurrentDirectory, string? szEnvOverride)
    {
        return ToDbLocation(SharedSettingLocator.Resolve(FileName, szBaseDirectory, szCurrentDirectory, szEnvOverride));
    }

    /// <summary>
    /// 依優先順序列出候選（不檢查存在）；環境變數未設定時不列入第 1 層
    /// </summary>
    public static IReadOnlyList<(DbSettingSource eSource, string szPath)> BuildCandidates(
        string szBaseDirectory, string szCurrentDirectory, string? szEnvOverride)
    {
        return SharedSettingLocator.BuildCandidates(FileName, szBaseDirectory, szCurrentDirectory, szEnvOverride)
            .Select(c => (ToDbSource(c.eSource), c.szPath))
            .ToList();
    }

    private static DbSettingLocation ToDbLocation(SharedSettingLocation l)
        => new(l.szPath, ToDbSource(l.eSource), l.szSearchedPaths);

    private static DbSettingSource ToDbSource(SharedSettingSource e) => e switch
    {
        SharedSettingSource.EnvironmentVariable => DbSettingSource.EnvironmentVariable,
        SharedSettingSource.Shared => DbSettingSource.Shared,
        SharedSettingSource.LocalSetting => DbSettingSource.LocalSetting,
        SharedSettingSource.DevRepo => DbSettingSource.DevRepo,
        _ => DbSettingSource.NotFound
    };
}
