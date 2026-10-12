namespace ScadaEngine.Common.Data.Services;

/// <summary>
/// dbSetting.json 的來源層（供啟動 log 標示「實際選中哪一層」）
/// </summary>
public enum DbSettingSource
{
    /// <summary>環境變數 JOULARIS_DBSETTING 指定的絕對路徑（測試 / 多實例覆寫）</summary>
    EnvironmentVariable,
    /// <summary>生產機共用檔 &lt;BaseDirectory&gt;\..\..\Shared\dbSetting.json（= C:\SCADA\Shared\）</summary>
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
/// dbSetting.json 路徑解析器 — Engine / Web / ModbusServer 三個 app 共用同一套順序，
/// 第一個「檔案存在」的候選勝出：
/// <list type="number">
///   <item>環境變數 <c>JOULARIS_DBSETTING</c>（絕對路徑；存在但檔不在 → 略過往下找）</item>
///   <item><c>&lt;BaseDirectory&gt;\..\..\Shared\dbSetting.json</c>（生產機 C:\SCADA\&lt;App&gt;\App\ → C:\SCADA\Shared\）</item>
///   <item><c>&lt;BaseDirectory&gt;\Setting\dbSetting.json</c>（單機獨立安裝）</item>
///   <item><c>&lt;CWD&gt;\..\ScadaEngine.Engine\Setting\dbSetting.json</c>，找不到再從 BaseDirectory 逐層往上找
///         <c>&lt;祖先&gt;\ScadaEngine.Engine\Setting\dbSetting.json</c>（開發模式 dotnet run / F5，不依賴 CWD）</item>
/// </list>
/// 第 2 層刻意排在第 3 層之前：共用檔存在就一定贏過本機複本，杜絕 stale 複本被優先讀到。
/// 程式碼不出現 C:\SCADA 等寫死磁碟路徑，全靠 BaseDirectory 相對推算。
/// 詳見 docs/架構.md §dbSetting.json 解析順序。
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
        return Resolve(
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable(EnvVarName));
    }

    /// <summary>
    /// 以指定的三個輸入解析（單元測試用；所有路徑皆以 File.Exists 判定）
    /// </summary>
    /// <param name="szBaseDirectory">應用程式基礎目錄（AppContext.BaseDirectory）</param>
    /// <param name="szCurrentDirectory">目前工作目錄（Directory.GetCurrentDirectory()）</param>
    /// <param name="szEnvOverride">環境變數 JOULARIS_DBSETTING 的值；null / 空白視為未設定</param>
    public static DbSettingLocation Resolve(string szBaseDirectory, string szCurrentDirectory, string? szEnvOverride)
    {
        var candidates = BuildCandidates(szBaseDirectory, szCurrentDirectory, szEnvOverride);
        var szSearched = candidates.Select(c => c.szPath).ToList();

        foreach (var (eSource, szPath) in candidates)
        {
            if (File.Exists(szPath))
            {
                return new DbSettingLocation(szPath, eSource, szSearched);
            }
        }

        return new DbSettingLocation(null, DbSettingSource.NotFound, szSearched);
    }

    /// <summary>
    /// 依優先順序列出候選（不檢查存在）；環境變數未設定時不列入第 1 層
    /// </summary>
    public static IReadOnlyList<(DbSettingSource eSource, string szPath)> BuildCandidates(
        string szBaseDirectory, string szCurrentDirectory, string? szEnvOverride)
    {
        var list = new List<(DbSettingSource, string)>(12);

        if (!string.IsNullOrWhiteSpace(szEnvOverride))
        {
            list.Add((DbSettingSource.EnvironmentVariable, Path.GetFullPath(szEnvOverride.Trim())));
        }

        list.Add((DbSettingSource.Shared,
            Path.GetFullPath(Path.Combine(szBaseDirectory, "..", "..", "Shared", FileName))));

        list.Add((DbSettingSource.LocalSetting,
            Path.GetFullPath(Path.Combine(szBaseDirectory, "Setting", FileName))));

        // 第 4 層（開發模式）：先看 CWD 上一層（dotnet run 於專案目錄的慣例），
        // 再從 BaseDirectory 一路往上找 repo 根（bin\Debug\net8.0 → 專案 → repo），
        // 讓「啟動時把 CWD 切到 BaseDirectory」的 app（ModbusServer）或從 repo 根 dotnet run --project 也找得到
        var szDevCandidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(szCurrentDirectory, "..", "ScadaEngine.Engine", "Setting", FileName))
        };
        var dir = new DirectoryInfo(Path.GetFullPath(szBaseDirectory));
        for (var nLevel = 0; dir != null && nLevel < MaxAncestorLevels; nLevel++, dir = dir.Parent)
        {
            szDevCandidates.Add(Path.Combine(dir.FullName, "ScadaEngine.Engine", "Setting", FileName));
        }
        foreach (var szDev in szDevCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            list.Add((DbSettingSource.DevRepo, szDev));
        }

        return list;
    }

    /// <summary>第 4 層從 BaseDirectory 往上找 repo 根的最大層數（bin\Debug\net8.0\ 到 repo 根是 4 層，留餘裕）</summary>
    private const int MaxAncestorLevels = 6;
}
