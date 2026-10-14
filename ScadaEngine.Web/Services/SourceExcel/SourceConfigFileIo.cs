using System.Text;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// Engine 來源設定 JSON（Modbus / DBPoint）的共用檔案 I/O —
/// 路徑解析與逸出防護、BOM 編碼偵測、原子寫檔、dev 鏡像寫回、刪除備份。
///
/// 由 ModbusConfigFileService / DbPointConfigFileService（熱編輯）與 SourceExcelImportCoordinator（Excel 匯入）共用，
/// 確保三條寫檔路徑的行為一致：
/// - 原子寫檔：先寫 *.json.tmp（不符 Engine watcher 的 *.json filter）再 File.Replace 替換，留 *.json.bak；
///   新檔走 File.Move（Windows 以 rename 落地 → Engine 收到 Renamed/Created）
/// - 刪除：移到 {folder}/_deleted/{name}_{yyyyMMddHHmmss}.json（子資料夾不在 Engine 載入範圍），可人工搬回復原
/// - 資料夾每次呼叫即時解析 appsettings（reloadOnChange 改路徑即生效）；相對路徑以 Web ContentRoot 為基準
///
/// Singleton 註冊（無狀態、無鎖 — 寫入互斥由各呼叫端自持）。
/// </summary>
public class SourceConfigFileIo
{
    /// <summary>appsettings 區段名稱 — Modbus 來源（WatchedFolder / MirrorFolder）</summary>
    public const string MODBUS_SECTION = "EngineModbusConfig";

    /// <summary>appsettings 區段名稱 — DB 來源</summary>
    public const string DBPOINT_SECTION = "EngineDbPointConfig";

    /// <summary>appsettings 區段名稱 — OPC UA 來源（OpcUaCoordinatorService 熱編輯與 Excel 匯入共用同一資料夾）</summary>
    public const string OPCUA_SECTION = "EngineOpcUaConfig";

    /// <summary>刪除備份子資料夾名稱</summary>
    public const string DELETED_SUBFOLDER = "_deleted";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SourceConfigFileIo> _logger;

    public SourceConfigFileIo(IConfiguration configuration, IWebHostEnvironment env, ILogger<SourceConfigFileIo> logger)
    {
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    /// <summary>Engine 實際讀取的資料夾（絕對路徑）；appsettings 未設定回傳 null（呼叫端明確報錯，禁止猜測式 fallback）</summary>
    public string? GetWatchedFolder(string szSection)
    {
        var szWatched = _configuration[$"{szSection}:WatchedFolder"];
        return string.IsNullOrWhiteSpace(szWatched)
            ? null
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, szWatched));
    }

    /// <summary>dev 鏡像資料夾（可選）— 同步寫回原始碼資料夾避免 rebuild 後設定倒退；未設定回傳 null</summary>
    public string? GetMirrorFolder(string szSection)
    {
        var szMirror = _configuration[$"{szSection}:MirrorFolder"];
        return string.IsNullOrWhiteSpace(szMirror)
            ? null
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, szMirror));
    }

    /// <summary>名稱可否安全作為檔名（不含路徑字元、不含 ..、非空白）</summary>
    public static bool IsSafeName(string? szName)
    {
        if (string.IsNullOrWhiteSpace(szName)) return false;
        if (szName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (szName.Contains("..")) return false;
        if (szName.Trim() != szName) return false;
        return true;
    }

    /// <summary>
    /// 解析設定名稱（= JSON 檔名，不含副檔名）為監控資料夾內的完整路徑；
    /// 資料夾未設定、名稱不安全或逸出資料夾一律回 null。
    /// </summary>
    public string? ResolveConfigFilePath(string szSection, string szName)
    {
        var szFolder = GetWatchedFolder(szSection);
        return szFolder == null ? null : ResolveWithin(szFolder, szName);
    }

    /// <summary>在指定資料夾內解析 {name}.json，含逸出防護</summary>
    public static string? ResolveWithin(string szFolder, string szName)
    {
        if (!IsSafeName(szName)) return null;

        var szFullPath = Path.GetFullPath(Path.Combine(szFolder, szName + ".json"));
        if (!szFullPath.StartsWith(szFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return null;

        return szFullPath;
    }

    /// <summary>列出監控資料夾頂層所有 *.json 的名稱（不含副檔名）；資料夾未設定或不存在回空清單</summary>
    public IReadOnlyList<string> ListConfigNames(string szSection)
    {
        var szFolder = GetWatchedFolder(szSection);
        if (szFolder == null || !Directory.Exists(szFolder)) return Array.Empty<string>();

        return Directory.GetFiles(szFolder, "*.json", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>讀檔並依 BOM 偵測編碼（無 BOM 視為 UTF-8）；回傳內容與實際編碼，供寫回時沿用</summary>
    public static async Task<(string szText, Encoding encoding)> ReadAllTextDetectEncodingAsync(string szFilePath)
    {
        using var reader = new StreamReader(szFilePath, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var szText = await reader.ReadToEndAsync();
        return (szText, reader.CurrentEncoding);
    }

    /// <summary>
    /// 原子寫檔：寫 *.json.tmp（不觸發 Engine watcher）→ 既有檔 File.Replace 原子替換並留 *.json.bak；
    /// 新檔 File.Move。控制路徑每筆指令都直接讀 JSON 且失敗不重試，半份 JSON 會讓控制無聲失敗 — 原子替換杜絕此窗口。
    /// </summary>
    public static void AtomicWrite(string szFilePath, string szContent, Encoding encoding)
    {
        var szTmpPath = szFilePath + ".tmp";
        var szBakPath = szFilePath + ".bak";

        File.WriteAllText(szTmpPath, szContent, encoding);
        if (File.Exists(szFilePath))
            File.Replace(szTmpPath, szFilePath, szBakPath, ignoreMetadataErrors: true);
        else
            File.Move(szTmpPath, szFilePath);
    }

    /// <summary>dev 環境鏡像寫回原始碼資料夾（資料夾不存在即略過；失敗僅記 log，不影響主寫入）</summary>
    public void MirrorWrite(string szSection, string szName, string szContent, Encoding encoding)
    {
        var szMirrorFolder = GetMirrorFolder(szSection);
        if (szMirrorFolder == null) return;

        try
        {
            if (!Directory.Exists(szMirrorFolder)) return;
            var szMirrorPath = ResolveWithin(szMirrorFolder, szName);
            if (szMirrorPath == null) return;

            File.WriteAllText(szMirrorPath, szContent, encoding);
            _logger.LogInformation("來源設定已鏡像寫回: {File}", szMirrorPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "鏡像寫回失敗（不影響主寫入）: {Name}", szName);
        }
    }

    /// <summary>
    /// 刪除 = 移到 {folder}/_deleted/{name}_{timestamp}.json（可人工搬回復原）。
    /// 鏡像資料夾（若存在同名檔）同步搬到其 _deleted/。回傳備份檔完整路徑；原檔不存在回 null。
    /// </summary>
    public string? MoveToDeleted(string szSection, string szName)
    {
        var szFolder = GetWatchedFolder(szSection);
        if (szFolder == null) return null;

        var szPath = ResolveWithin(szFolder, szName);
        if (szPath == null || !File.Exists(szPath)) return null;

        var szStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var szBackup = MoveIntoDeletedFolder(szFolder, szPath, szName, szStamp);
        _logger.LogInformation("來源設定已移除（備份於 {Backup}）", szBackup);

        // 鏡像同步（dev）
        var szMirrorFolder = GetMirrorFolder(szSection);
        if (szMirrorFolder != null && Directory.Exists(szMirrorFolder))
        {
            try
            {
                var szMirrorPath = ResolveWithin(szMirrorFolder, szName);
                if (szMirrorPath != null && File.Exists(szMirrorPath))
                    MoveIntoDeletedFolder(szMirrorFolder, szMirrorPath, szName, szStamp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "鏡像刪除失敗（不影響主刪除）: {Name}", szName);
            }
        }

        return szBackup;
    }

    private static string MoveIntoDeletedFolder(string szFolder, string szPath, string szName, string szStamp)
    {
        var szDeletedDir = Path.Combine(szFolder, DELETED_SUBFOLDER);
        Directory.CreateDirectory(szDeletedDir);

        var szBackup = Path.Combine(szDeletedDir, $"{szName}_{szStamp}.json");
        var nSuffix = 1;
        while (File.Exists(szBackup))
            szBackup = Path.Combine(szDeletedDir, $"{szName}_{szStamp}_{nSuffix++}.json");

        File.Move(szPath, szBackup);
        return szBackup;
    }
}
