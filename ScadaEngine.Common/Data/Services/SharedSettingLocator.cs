namespace ScadaEngine.Common.Data.Services;

/// <summary>
/// 共用設定檔的來源層（供啟動 log 標示「實際選中哪一層」）
/// </summary>
public enum SharedSettingSource
{
    /// <summary>環境變數指定的絕對路徑（僅 dbSetting.json 有此層；測試 / 多實例覆寫）</summary>
    EnvironmentVariable,
    /// <summary>生產機共用檔 &lt;BaseDirectory&gt;\..\..\Shared\&lt;檔名&gt;（= C:\SCADA\Shared\）</summary>
    Shared,
    /// <summary>本機 App\Setting\&lt;檔名&gt;（單機獨立安裝，例如 Gateway 裝在遠端機器）</summary>
    LocalSetting,
    /// <summary>開發模式：&lt;CWD&gt;\..\ScadaEngine.Engine\Setting\&lt;檔名&gt;（三專案都指向 repo 內 Engine 那份）</summary>
    DevRepo,
    /// <summary>所有層都找不到（呼叫端自行決定退回預設或停用）</summary>
    NotFound
}

/// <summary>
/// 共用設定檔路徑解析結果
/// </summary>
/// <param name="szFileName">設定檔檔名（例 EmailSetting.json）</param>
/// <param name="szPath">選中的絕對路徑；所有層都不存在時為 null</param>
/// <param name="eSource">選中的層</param>
/// <param name="szSearchedPaths">依序檢查過的候選路徑（找不到時供 log 列出）</param>
public sealed record SharedSettingLocation(
    string szFileName, string? szPath, SharedSettingSource eSource, IReadOnlyList<string> szSearchedPaths)
{
    /// <summary>來源的中文顯示名（log 用）</summary>
    public string szSourceLabel => eSource switch
    {
        SharedSettingSource.EnvironmentVariable => "環境變數",
        SharedSettingSource.Shared => "Shared",
        SharedSettingSource.LocalSetting => "本機 Setting",
        SharedSettingSource.DevRepo => "開發 repo",
        _ => "未找到"
    };
}

/// <summary>
/// 「機器層級、跨 app 共用」設定檔的路徑解析器 — dbSetting / EmailSetting / SmsSetting / LineSetting
/// 皆走同一套順序，Engine / Web / ModbusServer 三個 app 解析結果一致，第一個「檔案存在」的候選勝出：
/// <list type="number">
///   <item>環境變數（僅在呼叫端給了 <c>szEnvOverride</c> 時列入；絕對路徑；存在但檔不在 → 略過往下找）</item>
///   <item><c>&lt;BaseDirectory&gt;\..\..\Shared\&lt;檔名&gt;</c>（生產機 C:\SCADA\&lt;App&gt;\App\ → C:\SCADA\Shared\）</item>
///   <item><c>&lt;BaseDirectory&gt;\Setting\&lt;檔名&gt;</c>（單機獨立安裝 / 舊佈局）</item>
///   <item><c>&lt;CWD&gt;\..\ScadaEngine.Engine\Setting\&lt;檔名&gt;</c>，找不到再從 BaseDirectory 逐層往上找
///         <c>&lt;祖先&gt;\ScadaEngine.Engine\Setting\&lt;檔名&gt;</c>（開發模式 dotnet run / F5，不依賴 CWD）</item>
/// </list>
/// 第 2 層刻意排在第 3 層之前：共用檔存在就一定贏過本機複本，杜絕 stale 複本被優先讀到。
/// 程式碼不出現 C:\SCADA 等寫死磁碟路徑，全靠 BaseDirectory 相對推算。
/// 詳見 docs/架構.md §共用設定檔解析順序。
/// </summary>
public static class SharedSettingLocator
{
    /// <summary>第 4 層從 BaseDirectory 往上找 repo 根的最大層數（bin\Debug\net8.0\ 到 repo 根是 4 層，留餘裕）</summary>
    private const int MaxAncestorLevels = 6;

    /// <summary>
    /// 以執行環境實際值解析（AppContext.BaseDirectory / 目前工作目錄 / 環境變數）
    /// </summary>
    /// <param name="szFileName">設定檔檔名（例 EmailSetting.json）</param>
    /// <param name="szEnvVarName">環境變數名稱；null 代表此檔不提供環境變數覆寫層</param>
    public static SharedSettingLocation Resolve(string szFileName, string? szEnvVarName = null)
    {
        var szEnvOverride = string.IsNullOrWhiteSpace(szEnvVarName)
            ? null
            : Environment.GetEnvironmentVariable(szEnvVarName);

        return Resolve(szFileName, AppContext.BaseDirectory, Directory.GetCurrentDirectory(), szEnvOverride);
    }

    /// <summary>
    /// 以指定的輸入解析（單元測試用；所有路徑皆以 File.Exists 判定）
    /// </summary>
    /// <param name="szFileName">設定檔檔名</param>
    /// <param name="szBaseDirectory">應用程式基礎目錄（AppContext.BaseDirectory）</param>
    /// <param name="szCurrentDirectory">目前工作目錄（Directory.GetCurrentDirectory()）</param>
    /// <param name="szEnvOverride">環境變數的值；null / 空白視為未設定（不列入第 1 層）</param>
    public static SharedSettingLocation Resolve(
        string szFileName, string szBaseDirectory, string szCurrentDirectory, string? szEnvOverride)
    {
        var candidates = BuildCandidates(szFileName, szBaseDirectory, szCurrentDirectory, szEnvOverride);
        var szSearched = candidates.Select(c => c.szPath).ToList();

        foreach (var (eSource, szPath) in candidates)
        {
            if (File.Exists(szPath))
            {
                return new SharedSettingLocation(szFileName, szPath, eSource, szSearched);
            }
        }

        return new SharedSettingLocation(szFileName, null, SharedSettingSource.NotFound, szSearched);
    }

    /// <summary>
    /// 解析「要寫入時」的目標路徑：檔案存在就寫回同一份（讀寫同一條解析路徑，避免讀 Shared 寫本機的分家）；
    /// 所有層都不存在時 → 生產佈局（Shared 資料夾存在）寫 Shared，否則寫開發 repo 第一個候選。
    /// 回傳的 <c>isFallback</c> 為 true 代表走了「都不存在」的退路，呼叫端應 log warning。
    /// </summary>
    public static (string szPath, bool isFallback) ResolveForWrite(string szFileName, string? szEnvVarName = null)
    {
        var location = Resolve(szFileName, szEnvVarName);
        if (location.szPath != null)
            return (location.szPath, false);

        return (FallbackWritePath(szFileName, AppContext.BaseDirectory, Directory.GetCurrentDirectory()), true);
    }

    /// <summary>
    /// 「都不存在」時的寫入退路（單元測試用；與 <see cref="ResolveForWrite(string, string?)"/> 同邏輯）
    /// </summary>
    public static string FallbackWritePath(string szFileName, string szBaseDirectory, string szCurrentDirectory)
    {
        var candidates = BuildCandidates(szFileName, szBaseDirectory, szCurrentDirectory, null);

        var szShared = candidates.First(c => c.eSource == SharedSettingSource.Shared).szPath;
        var szSharedDir = Path.GetDirectoryName(szShared);
        if (!string.IsNullOrEmpty(szSharedDir) && Directory.Exists(szSharedDir))
            return szShared;

        return candidates.First(c => c.eSource == SharedSettingSource.DevRepo).szPath;
    }

    /// <summary>
    /// 依優先順序列出候選（不檢查存在）；環境變數未設定時不列入第 1 層
    /// </summary>
    public static IReadOnlyList<(SharedSettingSource eSource, string szPath)> BuildCandidates(
        string szFileName, string szBaseDirectory, string szCurrentDirectory, string? szEnvOverride)
    {
        if (string.IsNullOrWhiteSpace(szFileName))
            throw new ArgumentException("設定檔檔名不可為空", nameof(szFileName));

        var list = new List<(SharedSettingSource, string)>(12);

        if (!string.IsNullOrWhiteSpace(szEnvOverride))
        {
            list.Add((SharedSettingSource.EnvironmentVariable, Path.GetFullPath(szEnvOverride.Trim())));
        }

        list.Add((SharedSettingSource.Shared,
            Path.GetFullPath(Path.Combine(szBaseDirectory, "..", "..", "Shared", szFileName))));

        list.Add((SharedSettingSource.LocalSetting,
            Path.GetFullPath(Path.Combine(szBaseDirectory, "Setting", szFileName))));

        // 第 4 層（開發模式）：先看 CWD 上一層（dotnet run 於專案目錄的慣例），
        // 再從 BaseDirectory 一路往上找 repo 根（bin\Debug\net8.0 → 專案 → repo），
        // 讓「啟動時把 CWD 切到 BaseDirectory」的 app（ModbusServer）或從 repo 根 dotnet run --project 也找得到
        var szDevCandidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(szCurrentDirectory, "..", "ScadaEngine.Engine", "Setting", szFileName))
        };
        var dir = new DirectoryInfo(Path.GetFullPath(szBaseDirectory));
        for (var nLevel = 0; dir != null && nLevel < MaxAncestorLevels; nLevel++, dir = dir.Parent)
        {
            szDevCandidates.Add(Path.Combine(dir.FullName, "ScadaEngine.Engine", "Setting", szFileName));
        }
        foreach (var szDev in szDevCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            list.Add((SharedSettingSource.DevRepo, szDev));
        }

        return list;
    }
}
