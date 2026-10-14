using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// Excel 匯入共用流程（Modbus / DB 來源共用，各來源差異交給 <see cref="ISourceExcelAdapter"/>）：
///
/// 1. <see cref="PreviewAsync"/>：解析 + 驗證 + 與既有 JSON 比對 → 回傳預覽 + previewToken（解析結果暫存 IMemoryCache 10 分鐘、綁定使用者），**不寫檔**
/// 2. <see cref="CommitAsync"/>：帶 token + 使用者勾選的匯入／刪除清單 → 提交前重新比對（既有檔指紋變了就拒絕，避免靜默蓋掉別人的熱編輯）
///    → 原子寫檔 / 移到 _deleted/ → 清 token
/// 3. <see cref="DeleteAsync"/>：逐台刪除（不必上傳 Excel）
/// 4. <see cref="BuildTemplate"/> / <see cref="ExportAsync"/>：空白範本、現行設定匯出（全部或依來源 Excel 檔）
/// 5. 來源資料夾內的 Excel：<see cref="ExportToFolderAsync"/> 匯出直接寫進 WatchedFolder、<see cref="PreviewFromFolderAsync"/> 從資料夾匯入、
///    <see cref="RefreshFolderWorkbooksAsync"/> 設定變更後自動重產（plan 2026-10-12 D 段）
///
/// 匯入時在 JSON 頂層寫入 SourceWorkbook（上傳檔名），作為下次「同檔刪除候選」的判定依據（決策 5）。
/// Scoped 註冊（依賴 IStringLocalizer）；寫檔互斥為 static 跨請求共用。
/// </summary>
public class SourceExcelImportCoordinator
{
    private const string CACHE_PREFIX = "srcxl:";
    private static readonly TimeSpan PREVIEW_TTL = TimeSpan.FromMinutes(10);

    /// <summary>提交 / 刪除互斥（static — Scoped service 跨請求共用；與熱編輯的鎖不同，但彼此靠「提交前指紋重比對」互保）</summary>
    private static readonly SemaphoreSlim _commitGate = new(1, 1);

    private readonly SourceConfigFileIo _io;
    private readonly IMemoryCache _cache;
    private readonly IStringLocalizer<SourceExcelImportCoordinator> _l;
    private readonly ILogger<SourceExcelImportCoordinator> _logger;

    public SourceExcelImportCoordinator(
        SourceConfigFileIo io,
        IMemoryCache cache,
        IStringLocalizer<SourceExcelImportCoordinator> localizer,
        ILogger<SourceExcelImportCoordinator> logger)
    {
        _io = io;
        _cache = cache;
        _l = localizer;
        _logger = logger;
    }

    // ───────────────────────────── 預覽 ─────────────────────────────

    /// <param name="szFolderFileName">從來源資料夾匯入時的檔名（瀏覽器上傳為 null）</param>
    public async Task<SourceExcelPreviewResult> PreviewAsync(ISourceExcelAdapter adapter, Stream xlsxStream, string szWorkbookFileName, string szUsername, string? szFolderFileName = null)
    {
        var result = new SourceExcelPreviewResult { WorkbookName = Path.GetFileName(szWorkbookFileName ?? string.Empty) };

        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null)
        {
            result.Message = _l["srcxl.svc.folder_not_configured", adapter.ConfigSection].Value;
            return result;
        }

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(xlsxStream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Excel 讀取失敗: {File}", result.WorkbookName);
            result.Message = _l["srcxl.svc.file_unreadable"].Value;
            return result;
        }

        using (workbook)
        {
            if (workbook.Worksheets.Count == 0)
            {
                result.Message = _l["srcxl.svc.no_sheets"].Value;
                return result;
            }

            var session = new SourceExcelPreviewSession
            {
                Kind = adapter.Kind,
                Username = szUsername,
                WorkbookName = result.WorkbookName,
                FolderFileName = szFolderFileName,
            };

            // 活頁簿匯出時間（匯出功能寫入；範本 / 手工建立 / 從別的檔複製工作表 → null = 不明來源）
            var exportedAt = SourceJsonStamp.ReadExported(workbook);
            result.ExportedAt = exportedAt is { } dtExported ? SourceJsonStamp.ToDisplay(dtExported) : null;
            result.SupportsDelete = adapter.SupportsDeleteCandidates;

            // 既有設定一覽（名稱 + SourceWorkbook）
            var existingInfos = await ListExistingAsync(adapter);
            var existingParsed = new Dictionary<string, ParsedSourceSheet>(StringComparer.OrdinalIgnoreCase);

            foreach (var ws in workbook.Worksheets)
            {
                var sheet = adapter.ParseSheet(ws);
                var szName = sheet.SheetName;

                if (session.Sheets.ContainsKey(szName))
                    continue; // Excel 不允許同名工作表（大小寫不同視為同一個檔名 → 取第一個）

                ParsedSourceSheet? existing = null;
                var szPath = SourceConfigFileIo.ResolveWithin(szFolder, szName);
                if (szPath != null && File.Exists(szPath))
                {
                    var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szPath);
                    existing = adapter.ParseJson(szName, szJson);
                    if (existing == null)
                        sheet.Warnings.Add(SheetIssue.General("srcxl.svc.existing_unreadable"));
                    session.Fingerprints[szName] = Fingerprint(szPath);
                }
                else
                {
                    session.Fingerprints[szName] = string.Empty;
                }

                // 鍵值式來源（OPC UA）在此對回既有 Seq / 配新號 / 標記未知 Seq；位置式來源為 no-op
                adapter.Reconcile(sheet, existing);

                var diff = SourceExcelDiffBuilder.Build(sheet, existing, adapter, exportedAt);
                if (existing == null && szPath != null && File.Exists(szPath))
                {
                    // 既有檔存在但解析不了 → 仍是覆寫（整份蓋掉），不能算新增
                    diff.Kind = diff.Kind == SheetDiffKind.Added ? SheetDiffKind.Overwrite : diff.Kind;
                    diff.DefaultChecked = diff.Kind == SheetDiffKind.Overwrite;
                }
                LocalizeIssues(diff.Errors);
                LocalizeIssues(diff.Warnings);

                session.Sheets[szName] = sheet;
                session.Diffs[szName] = diff;
                if (existing != null) existingParsed[szName] = existing;
                result.Sheets.Add(diff);
            }

            // 刪除候選 / 其他既有（OPC UA 不支援：刪除走頁面上的刪除按鈕）
            var (candidates, others) = adapter.SupportsDeleteCandidates
                ? SourceExcelDiffBuilder.ClassifyExisting(existingInfos, session.Sheets.Keys, result.WorkbookName)
                : (new List<ExistingSourceInfo>(), new List<ExistingSourceInfo>());
            result.DeleteCandidates.AddRange(candidates);
            result.OtherExisting.AddRange(others);
            foreach (var e in candidates.Concat(others))
            {
                session.Deletable.Add(e.Name);
                var szPath = SourceConfigFileIo.ResolveWithin(szFolder, e.Name);
                session.Fingerprints[e.Name] = szPath != null && File.Exists(szPath) ? Fingerprint(szPath) : string.Empty;
            }

            // 工作表改名提示：新增的工作表點位名稱與某刪除候選完全相同
            if (candidates.Count > 0)
            {
                var candidateSheets = new List<ParsedSourceSheet>();
                foreach (var c in candidates)
                {
                    var szPath = SourceConfigFileIo.ResolveWithin(szFolder, c.Name);
                    if (szPath == null || !File.Exists(szPath)) continue;
                    var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szPath);
                    var parsed = adapter.ParseJson(c.Name, szJson);
                    if (parsed != null) candidateSheets.Add(parsed);
                }
                foreach (var diff in result.Sheets.Where(d => d.Kind == SheetDiffKind.Added))
                    diff.RenameHintFrom = SourceExcelDiffBuilder.FindRenameHint(session.Sheets[diff.SheetName], candidateSheets);
            }

            result.Token = Guid.NewGuid().ToString("N");
            _cache.Set(CACHE_PREFIX + result.Token, session, new MemoryCacheEntryOptions { SlidingExpiration = PREVIEW_TTL });
            result.Success = true;

            _logger.LogInformation("Excel 匯入預覽: {Kind} {File} 工作表 {Count} 張（新增 {Added}／覆寫 {Over}／無變更 {Same}／錯誤 {Err}），刪除候選 {Del}",
                adapter.Kind, result.WorkbookName, result.Sheets.Count,
                result.Sheets.Count(s => s.Kind == SheetDiffKind.Added),
                result.Sheets.Count(s => s.Kind == SheetDiffKind.Overwrite),
                result.Sheets.Count(s => s.Kind == SheetDiffKind.Unchanged),
                result.Sheets.Count(s => s.Kind == SheetDiffKind.Error),
                result.DeleteCandidates.Count);
            return result;
        }
    }

    // ───────────────────────────── 提交 ─────────────────────────────

    public async Task<SourceExcelCommitResult> CommitAsync(ISourceExcelAdapter adapter, SourceExcelCommitRequest request, string szUsername)
    {
        var result = new SourceExcelCommitResult();

        if (string.IsNullOrWhiteSpace(request.Token)
            || !_cache.TryGetValue(CACHE_PREFIX + request.Token, out SourceExcelPreviewSession? session)
            || session == null
            || session.Kind != adapter.Kind)
        {
            result.Message = _l["srcxl.svc.preview_expired"].Value;
            return result;
        }

        if (!string.Equals(session.Username, szUsername, StringComparison.Ordinal))
        {
            result.Message = _l["srcxl.svc.preview_user_mismatch"].Value;
            return result;
        }

        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null)
        {
            result.Message = _l["srcxl.svc.folder_not_configured", adapter.ConfigSection].Value;
            return result;
        }

        var importNames = (request.ImportSheets ?? new List<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var deleteNames = (request.DeleteNames ?? new List<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (importNames.Count == 0 && deleteNames.Count == 0)
        {
            result.Message = _l["srcxl.svc.nothing_selected"].Value;
            return result;
        }

        // 勾選項目必須都在預覽內
        var aUnknown = importNames.Where(n => !session.Diffs.ContainsKey(n))
            .Concat(deleteNames.Where(n => !session.Deletable.Contains(n)))
            .ToList();
        if (aUnknown.Count > 0)
        {
            result.Message = _l["srcxl.svc.invalid_selection", string.Join(", ", aUnknown)].Value;
            return result;
        }

        // 有錯誤的工作表不可匯入；無變更的直接略過
        foreach (var szName in importNames)
        {
            if (session.Diffs[szName].Kind == SheetDiffKind.Error)
            {
                result.Message = _l["srcxl.svc.sheet_has_errors", szName].Value;
                return result;
            }
        }
        importNames = importNames.Where(n => session.Diffs[n].Kind != SheetDiffKind.Unchanged).ToList();

        // SID 位移需另外確認
        var aShift = importNames.Where(n => session.Diffs[n].HasSidShift).ToList();
        if (aShift.Count > 0 && !request.AcknowledgeSidShift)
        {
            result.Message = _l["srcxl.svc.sid_shift_unacknowledged", string.Join(", ", aShift)].Value;
            return result;
        }

        // 會蓋掉網頁修改（拿舊 Excel 重匯）需另外確認
        var aWebEdit = importNames.Where(n => session.Diffs[n].HasWebEditConflict).ToList();
        if (aWebEdit.Count > 0 && !request.AcknowledgeWebEdit)
        {
            result.Message = _l["srcxl.svc.web_edit_unacknowledged", string.Join(", ", aWebEdit)].Value;
            return result;
        }

        await _commitGate.WaitAsync();
        try
        {
            // 提交前重新比對：既有檔內容若與預覽當下不同 → 拒絕
            var aChanged = new List<string>();
            foreach (var szName in importNames.Concat(deleteNames))
            {
                var szPath = SourceConfigFileIo.ResolveWithin(szFolder, szName);
                var szNow = szPath != null && File.Exists(szPath) ? Fingerprint(szPath) : string.Empty;
                if (!string.Equals(session.Fingerprints.GetValueOrDefault(szName, string.Empty), szNow, StringComparison.Ordinal))
                    aChanged.Add(szName);
            }
            if (aChanged.Count > 0)
            {
                _cache.Remove(CACHE_PREFIX + request.Token);
                result.Conflict = true;
                result.Message = _l["srcxl.svc.changed_since_preview", string.Join(", ", aChanged)].Value;
                return result;
            }

            // 寫檔
            foreach (var szName in importNames)
            {
                var szPath = SourceConfigFileIo.ResolveWithin(szFolder, szName);
                if (szPath == null) continue;

                var sheet = session.Sheets[szName];

                // 既有檔（指紋已核對過）：沿用原編碼；鍵值式來源另需既有內容（保留原密碼 / NextSeq）
                var encoding = adapter.NewFileEncoding;
                string? szExistingJson = null;
                if (File.Exists(szPath))
                    (szExistingJson, encoding) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szPath);

                // 匯入寫檔不帶 WebEditedAt（adapter 從 sheet 序列化、不會帶入）→ Excel 與 JSON 重新一致
                var szJson = adapter.ToJson(sheet, session.WorkbookName, szExistingJson);

                SourceConfigFileIo.AtomicWrite(szPath, szJson, encoding);
                _io.MirrorWrite(adapter.ConfigSection, szName, szJson, encoding);
                result.Imported.Add(szName);
            }

            // 刪檔（移到 _deleted/）
            foreach (var szName in deleteNames)
            {
                if (_io.MoveToDeleted(adapter.ConfigSection, szName) != null)
                    result.Deleted.Add(szName);
            }

            _cache.Remove(CACHE_PREFIX + request.Token);
            result.Success = true;
            result.Message = _l["srcxl.svc.commit_success", result.Imported.Count, result.Deleted.Count].Value;

            // 重產資料夾內受影響的 xlsx。從資料夾匯入且每張工作表都已進 JSON（匯入或本來就相同）→ 該檔不套未匯入保護，
            // 否則剛匯入的檔（修改時間晚於匯出時間）會一直被當成「有未匯入修改」而不再自動更新
            FolderWorkbookTarget? forced = null;
            if (session.FolderFileName != null
                && session.Diffs.Values.All(d => d.Kind == SheetDiffKind.Unchanged || result.Imported.Contains(d.SheetName, StringComparer.OrdinalIgnoreCase)))
                forced = new FolderWorkbookTarget(session.FolderFileName, session.Sheets.Keys.ToList());
            result.WorkbookNotice = await RefreshFolderWorkbooksAsync(adapter, forced);

            _logger.LogInformation("Excel 匯入提交: {Kind} {File} by {User} — 匯入 [{Imported}] 刪除 [{Deleted}]",
                adapter.Kind, session.WorkbookName, szUsername,
                string.Join(", ", result.Imported), string.Join(", ", result.Deleted));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excel 匯入提交失敗: {Kind} {File}", adapter.Kind, session.WorkbookName);
            result.Message = _l["srcxl.svc.write_failed", ex.Message].Value;
            return result;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    // ───────────────────────────── 逐台刪除 ─────────────────────────────

    public async Task<SourceExcelCommitResult> DeleteAsync(ISourceExcelAdapter adapter, string szName, string szUsername)
    {
        var result = new SourceExcelCommitResult();
        szName = (szName ?? string.Empty).Trim();

        if (!adapter.SupportsDeleteCandidates)
        {
            result.Message = _l["srcxl.svc.delete_not_supported"].Value;
            return result;
        }

        if (!SourceConfigFileIo.IsSafeName(szName))
        {
            result.Message = _l["srcxl.svc.delete_not_found", szName].Value;
            return result;
        }

        if (_io.GetWatchedFolder(adapter.ConfigSection) == null)
        {
            result.Message = _l["srcxl.svc.folder_not_configured", adapter.ConfigSection].Value;
            return result;
        }

        await _commitGate.WaitAsync();
        try
        {
            var szBackup = _io.MoveToDeleted(adapter.ConfigSection, szName);
            if (szBackup == null)
            {
                result.Message = _l["srcxl.svc.delete_not_found", szName].Value;
                return result;
            }

            result.Deleted.Add(szName);
            result.Success = true;
            result.Message = _l["srcxl.svc.delete_success", szName].Value;
            result.WorkbookNotice = await RefreshFolderWorkbooksAsync(adapter);
            _logger.LogInformation("來源設定刪除: {Kind} {Name} by {User} → {Backup}", adapter.Kind, szName, szUsername, szBackup);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "來源設定刪除失敗: {Kind} {Name}", adapter.Kind, szName);
            result.Message = _l["srcxl.svc.write_failed", ex.Message].Value;
            return result;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    // ───────────────────────────── 範本 / 匯出 / 一覽 ─────────────────────────────

    public byte[] BuildTemplate(ISourceExcelAdapter adapter) => SourceExcelTemplateWriter.BuildTemplate(adapter);

    /// <summary>匯出現行設定（下載用）；szWorkbookFilter 非空時只匯出 SourceWorkbook 相同（忽略大小寫與副檔名）的設定。無法匯出的設定記 log（下載沒有訊息管道）</summary>
    public async Task<byte[]> ExportAsync(ISourceExcelAdapter adapter, string? szWorkbookFilter)
    {
        var aSkipped = new List<string>();
        var sheets = await CollectSheetsAsync(adapter, szWorkbookFilter, aSkipped);
        // 匯出帶 ScadaExportedAt：之後拿這份改完再匯入，不會被判成「蓋掉網頁修改」
        var bytes = BuildExport(adapter, sheets, aSkipped, null);
        LogSkipped(adapter, aSkipped);
        return bytes;
    }

    /// <summary>
    /// 組匯出活頁簿；工作表名稱不合 Excel 規則（超過 31 字、含 [ ] : * ? / \）者會被略過，理由寫進 skipped。
    /// 之前無聲略過，使用者只看到「某台設備沒出現在 Excel 裡」卻不知道為什麼。
    /// </summary>
    private byte[] BuildExport(ISourceExcelAdapter adapter, List<ParsedSourceSheet> sheets, List<string> skipped, DateTimeOffset? exportedAt)
    {
        var aBadNames = new List<string>();
        var bytes = SourceExcelTemplateWriter.Build(adapter, sheets, aBadNames, isExport: true, exportedAt: exportedAt);
        foreach (var szName in aBadNames)
            skipped.Add(_l["srcxl.svc.skip_reason_sheet_name", szName].Value);
        return bytes;
    }

    private void LogSkipped(ISourceExcelAdapter adapter, List<string> skipped)
    {
        if (skipped.Count > 0)
            _logger.LogWarning("Excel 匯出略過 {Count} 個設定: {Kind} {Skipped}", skipped.Count, adapter.Kind, string.Join("、", skipped));
    }

    /// <summary>
    /// 讀現行 JSON 為匯出用的中介表示；szWorkbookFilter 非空時只取 SourceWorkbook 相同（忽略大小寫與副檔名）者。
    /// JSON 解析不了（尾逗號 / 註解已在 ParseJsonObject 放行，剩下的是真的壞檔）→ 理由寫進 skipped 而不是無聲丟掉。
    /// </summary>
    private async Task<List<ParsedSourceSheet>> CollectSheetsAsync(ISourceExcelAdapter adapter, string? szWorkbookFilter, List<string>? skipped = null)
    {
        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        var sheets = new List<ParsedSourceSheet>();
        if (szFolder != null)
        {
            var szFilterKey = SourceExcelDiffBuilder.NormalizeWorkbookName(szWorkbookFilter);
            foreach (var szName in _io.ListConfigNames(adapter.ConfigSection))
            {
                var szPath = SourceConfigFileIo.ResolveWithin(szFolder, szName);
                if (szPath == null)
                {
                    skipped?.Add(_l["srcxl.svc.skip_reason_name_unsafe", szName].Value);
                    continue;
                }
                if (!File.Exists(szPath)) continue;

                var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szPath);
                var parsed = adapter.ParseJson(szName, szJson);
                if (parsed == null)
                {
                    skipped?.Add(_l["srcxl.svc.skip_reason_unparseable", szName].Value);
                    continue;
                }

                if (szFilterKey.Length > 0
                    && !string.Equals(SourceExcelDiffBuilder.NormalizeWorkbookName(parsed.SourceWorkbook), szFilterKey, StringComparison.Ordinal))
                    continue;

                sheets.Add(parsed);
            }
        }
        return sheets;
    }

    /// <summary>既有設定一覽：名稱 + JSON 內 SourceWorkbook（讀不到 → null）</summary>
    public async Task<List<ExistingSourceInfo>> ListExistingAsync(ISourceExcelAdapter adapter)
    {
        var list = new List<ExistingSourceInfo>();
        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null) return list;

        foreach (var szName in _io.ListConfigNames(adapter.ConfigSection))
        {
            var info = new ExistingSourceInfo { Name = szName };
            try
            {
                var szPath = SourceConfigFileIo.ResolveWithin(szFolder, szName);
                if (szPath != null && File.Exists(szPath))
                {
                    var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szPath);
                    if (SourceConfigFileIo.ParseJsonObject(szJson) is { } root)
                        info.SourceWorkbook = root["SourceWorkbook"]?.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "讀取 SourceWorkbook 失敗: {Name}", szName);
            }
            list.Add(info);
        }
        return list;
    }

    /// <summary>既有設定名稱集合（頁面標示「設定檔已移除」用）</summary>
    public HashSet<string> GetExistingNames(ISourceExcelAdapter adapter)
        => new(_io.ListConfigNames(adapter.ConfigSection), StringComparer.OrdinalIgnoreCase);

    // ───────────────────────────── 來源資料夾內的 Excel（plan 2026-10-12 D 段） ─────────────────────────────
    //
    // 匯出直接寫進 WatchedFolder（Engine watcher 與 ListConfigNames 只看 *.json，xlsx 不會被誤讀）。JSON 仍是唯一真相，資料夾內 xlsx 只是最新一次匯出的快照：
    // - 檔名固定：全部匯出 → {ExportFileNamePrefix}.xlsx；依來源 Excel 匯出 → 該 SourceWorkbook 檔名（副檔名改 .xlsx），每次覆寫、留一份 .bak
    // - 寫完把檔案修改時間對齊 ScadaExportedAt；之後修改時間晚於它 = 在 Excel 存檔過、還沒在網頁匯入（「未匯入修改」）
    // - 熱編輯 / 匯入 / 刪除成功後自動重產受影響的 xlsx（不主動新建）；被 Excel 開啟、有未匯入修改、非系統匯出 → 不覆寫並回傳提示
    // - 不做「Excel 存檔自動匯入」：會跳過預覽與 SID 位移、覆寫網頁修改等確認

    /// <summary>檔案修改時間與匯出時間的容許誤差（檔案系統時間解析度）</summary>
    private static readonly TimeSpan MODIFIED_TOLERANCE = TimeSpan.FromSeconds(2);

    /// <summary>資料夾內 xlsx 寫入互斥（匯出 / 自動重產共用，static 跨請求）</summary>
    private static readonly SemaphoreSlim _workbookGate = new(1, 1);

    /// <summary>可從資料夾匯入的副檔名</summary>
    private static readonly string[] FOLDER_WORKBOOK_EXTENSIONS = { ".xlsx", ".xlsm" };

    /// <summary>剛從資料夾完整匯入的檔案：略過未匯入保護，以 SheetNames（該檔原有的工作表）＋ SourceWorkbook 相符者重產</summary>
    public sealed record FolderWorkbookTarget(string FileName, IReadOnlyCollection<string> SheetNames);

    /// <summary>匯出寫入資料夾的檔名：szWorkbookFilter 空白 → {Prefix}.xlsx，否則 = 該來源 Excel 檔名改 .xlsx；不合法或與範本同名回 null</summary>
    public static string? GetFolderExportFileName(ISourceExcelAdapter adapter, string? szWorkbookFilter)
    {
        string szBase;
        try
        {
            szBase = string.IsNullOrWhiteSpace(szWorkbookFilter)
                ? adapter.ExportFileNamePrefix
                : Path.GetFileNameWithoutExtension(szWorkbookFilter.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (!SourceConfigFileIo.IsSafeName(szBase)) return null;

        var szFileName = szBase + ".xlsx";
        return string.Equals(szFileName, adapter.TemplateFileName, StringComparison.OrdinalIgnoreCase) ? null : szFileName;
    }

    /// <summary>匯出現行設定並寫進來源資料夾（固定檔名覆寫）。同名檔被 Excel 開啟 → Locked；有未匯入修改 / 非系統匯出且未帶 isForce → NeedsConfirm</summary>
    public async Task<SourceExcelFolderExportResult> ExportToFolderAsync(ISourceExcelAdapter adapter, string? szWorkbookFilter, bool isForce, string szUsername)
    {
        var result = new SourceExcelFolderExportResult();
        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null)
        {
            result.Message = _l["srcxl.svc.folder_not_configured", adapter.ConfigSection].Value;
            return result;
        }

        var szFileName = GetFolderExportFileName(adapter, szWorkbookFilter);
        var szPath = szFileName == null ? null : ResolveWorkbookWithin(szFolder, szFileName);
        if (szPath == null)
        {
            result.Message = _l["srcxl.svc.folder_name_invalid", szWorkbookFilter ?? string.Empty].Value;
            return result;
        }

        await _workbookGate.WaitAsync();
        try
        {
            if (File.Exists(szPath))
            {
                if (IsLockedForWrite(szPath))
                {
                    result.Locked = true;
                    result.Message = _l["srcxl.svc.folder_locked", szFileName!].Value;
                    return result;
                }
                if (!isForce)
                {
                    var (exportedAt, dtModified) = ReadWorkbookStamp(szPath);
                    if (exportedAt == null || IsUnimported(exportedAt, dtModified))
                    {
                        result.NeedsConfirm = true;
                        result.Message = exportedAt == null
                            ? _l["srcxl.svc.folder_confirm_not_exported", szFileName!].Value
                            : _l["srcxl.svc.folder_confirm_unimported", szFileName!].Value;
                        return result;
                    }
                }
            }

            // 依來源 Excel 匯出卻沒有任何設定對得上 → 不寫空白檔
            var aSkipped = new List<string>();
            var sheets = await CollectSheetsAsync(adapter, szWorkbookFilter, aSkipped);
            if (sheets.Count == 0 && !string.IsNullOrWhiteSpace(szWorkbookFilter))
            {
                result.Message = _l["srcxl.svc.folder_no_matching", szWorkbookFilter].Value;
                return result;
            }

            Directory.CreateDirectory(szFolder);
            var dtExported = DateTimeOffset.Now;
            var bytes = BuildExport(adapter, sheets, aSkipped, dtExported);
            WriteWorkbookFile(szPath, bytes, dtExported);

            result.Success = true;
            result.Path = szPath;
            result.Skipped = aSkipped;
            // 有設定沒進活頁簿 → 訊息明講哪些、為什麼（壞 JSON / 名稱不能當工作表名），不再讓使用者自己發現少了一台
            result.Message = aSkipped.Count == 0
                ? _l["srcxl.svc.folder_exported", szPath].Value
                : _l["srcxl.svc.folder_exported_skipped", szPath, string.Join("、", aSkipped)].Value;
            LogSkipped(adapter, aSkipped);
            _logger.LogInformation("Excel 匯出到來源資料夾: {Kind} {Path} by {User}", adapter.Kind, szPath, szUsername);
            return result;
        }
        catch (IOException ex) when (IsLockViolation(ex))
        {
            result.Locked = true;
            result.Message = _l["srcxl.svc.folder_locked", szFileName!].Value;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excel 匯出到來源資料夾失敗: {Kind} {Path}", adapter.Kind, szPath);
            result.Message = _l["srcxl.svc.write_failed", ex.Message].Value;
            return result;
        }
        finally
        {
            _workbookGate.Release();
        }
    }

    /// <summary>來源資料夾內的 Excel 檔（.xlsx / .xlsm，不含範本與 Excel 暫存鎖檔），修改時間新到舊</summary>
    public List<SourceExcelFolderWorkbook> ListFolderWorkbooks(ISourceExcelAdapter adapter)
    {
        var list = new List<SourceExcelFolderWorkbook>();
        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null || !Directory.Exists(szFolder)) return list;

        foreach (var szPath in EnumerateFolderWorkbooks(adapter, szFolder).OrderByDescending(p => File.GetLastWriteTime(p)))
        {
            var (exportedAt, dtModified) = ReadWorkbookStamp(szPath);
            list.Add(new SourceExcelFolderWorkbook
            {
                Name = Path.GetFileName(szPath),
                ModifiedAt = SourceJsonStamp.ToDisplay(dtModified),
                ExportedAt = exportedAt is { } dt ? SourceJsonStamp.ToDisplay(dt) : null,
                HasUnimportedChanges = IsUnimported(exportedAt, dtModified),
            });
        }
        return list;
    }

    /// <summary>以來源資料夾內的 Excel 預覽（不經瀏覽器上傳）；後續提交流程與上傳相同</summary>
    public async Task<SourceExcelPreviewResult> PreviewFromFolderAsync(ISourceExcelAdapter adapter, string szFileName, string szUsername)
    {
        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null)
            return new SourceExcelPreviewResult { Message = _l["srcxl.svc.folder_not_configured", adapter.ConfigSection].Value };

        var szPath = ResolveWorkbookWithin(szFolder, (szFileName ?? string.Empty).Trim());
        if (szPath == null || !File.Exists(szPath)
            || string.Equals(Path.GetFileName(szPath), adapter.TemplateFileName, StringComparison.OrdinalIgnoreCase))
            return new SourceExcelPreviewResult { Message = _l["srcxl.svc.folder_file_not_found", szFileName ?? string.Empty].Value };

        var szName = Path.GetFileName(szPath);
        MemoryStream ms;
        try
        {
            // Excel 開著時仍可讀（Excel 只拒絕寫入）；先整份讀進記憶體，不長時間佔用檔案
            ms = new MemoryStream(await ReadSharedAsync(szPath));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "來源資料夾 Excel 讀取失敗: {Path}", szPath);
            return new SourceExcelPreviewResult { WorkbookName = szName, Message = _l["srcxl.svc.file_unreadable"].Value };
        }

        using (ms)
            return await PreviewAsync(adapter, ms, szName, szUsername, szName);
    }

    /// <summary>
    /// 設定變更（熱編輯 / 匯入 / 刪除）後重產來源資料夾內受影響的 xlsx：
    /// {Prefix}.xlsx（含全部設定）＋ 檔名對得上某設定 SourceWorkbook 的 xlsx；不主動新建、範本與 .xlsm 不碰。
    /// 被 Excel 開啟 / 有未匯入修改 / 非系統匯出 → 不覆寫，回傳提示文字；全部成功或沒有受影響檔案回 null。
    /// 失敗只記 log，不影響呼叫端（JSON 已存檔）。
    /// </summary>
    public async Task<string?> RefreshFolderWorkbooksAsync(ISourceExcelAdapter adapter, FolderWorkbookTarget? forced = null)
    {
        var szFolder = _io.GetWatchedFolder(adapter.ConfigSection);
        if (szFolder == null || !Directory.Exists(szFolder)) return null;

        var aSkipped = new List<string>();
        await _workbookGate.WaitAsync();
        try
        {
            var aWorkbooks = EnumerateFolderWorkbooks(adapter, szFolder)
                .Where(p => Path.GetExtension(p).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (aWorkbooks.Count == 0) return null;

            var aUnparseable = new List<string>();
            var all = await CollectSheetsAsync(adapter, null, aUnparseable);
            LogSkipped(adapter, aUnparseable);
            foreach (var szPath in aWorkbooks)
            {
                var szFileName = Path.GetFileName(szPath);
                var isForced = forced != null && string.Equals(szFileName, forced.FileName, StringComparison.OrdinalIgnoreCase);
                var sheets = SelectSheetsFor(adapter, szFileName, all, isForced ? forced!.SheetNames : null);
                if (sheets.Count == 0) continue; // 與任何現行設定無關的檔案

                if (IsLockedForWrite(szPath))
                {
                    aSkipped.Add(_l["srcxl.svc.folder_reason_locked", szFileName].Value);
                    continue;
                }
                if (!isForced)
                {
                    var (exportedAt, dtModified) = ReadWorkbookStamp(szPath);
                    if (exportedAt == null)
                    {
                        aSkipped.Add(_l["srcxl.svc.folder_reason_not_exported", szFileName].Value);
                        continue;
                    }
                    if (IsUnimported(exportedAt, dtModified))
                    {
                        aSkipped.Add(_l["srcxl.svc.folder_reason_unimported", szFileName].Value);
                        continue;
                    }
                }

                try
                {
                    var dtExported = DateTimeOffset.Now;
                    var aBadNames = new List<string>();
                    WriteWorkbookFile(szPath, BuildExport(adapter, sheets, aBadNames, dtExported), dtExported);
                    LogSkipped(adapter, aBadNames);
                    _logger.LogInformation("來源資料夾 Excel 已重產: {Path}（{Count} 張工作表）", szPath, sheets.Count);
                }
                catch (IOException ex) when (IsLockViolation(ex))
                {
                    aSkipped.Add(_l["srcxl.svc.folder_reason_locked", szFileName].Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "來源資料夾 Excel 重產失敗（設定已存檔，不影響採集）: {Kind}", adapter.Kind);
        }
        finally
        {
            _workbookGate.Release();
        }

        return aSkipped.Count == 0 ? null : _l["srcxl.svc.folder_refresh_skipped", string.Join("、", aSkipped)].Value;
    }

    /// <summary>某個資料夾 xlsx 應包含的工作表：{Prefix} = 全部；其他 = SourceWorkbook 相符者（＋ forcedNames 指定者）</summary>
    private static List<ParsedSourceSheet> SelectSheetsFor(ISourceExcelAdapter adapter, string szFileName, List<ParsedSourceSheet> all, IReadOnlyCollection<string>? forcedNames)
    {
        if (string.Equals(Path.GetFileNameWithoutExtension(szFileName), adapter.ExportFileNamePrefix, StringComparison.OrdinalIgnoreCase))
            return all;

        var szKey = SourceExcelDiffBuilder.NormalizeWorkbookName(szFileName);
        var forcedSet = forcedNames == null ? null : new HashSet<string>(forcedNames, StringComparer.OrdinalIgnoreCase);
        return all
            .Where(s => string.Equals(SourceExcelDiffBuilder.NormalizeWorkbookName(s.SourceWorkbook), szKey, StringComparison.Ordinal)
                        || (forcedSet?.Contains(s.SheetName) ?? false))
            .ToList();
    }

    /// <summary>資料夾頂層的 .xlsx / .xlsm（排除範本與 Excel 的 ~$ 暫存鎖檔）</summary>
    private static IEnumerable<string> EnumerateFolderWorkbooks(ISourceExcelAdapter adapter, string szFolder)
        => Directory.GetFiles(szFolder, "*.xls*", SearchOption.TopDirectoryOnly)
            .Where(p => FOLDER_WORKBOOK_EXTENSIONS.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal))
            .Where(p => !string.Equals(Path.GetFileName(p), adapter.TemplateFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>在資料夾內解析 Excel 檔名（含副檔名），含逸出防護；不合法回 null</summary>
    private static string? ResolveWorkbookWithin(string szFolder, string szFileName)
    {
        if (!SourceConfigFileIo.IsSafeName(szFileName)
            || szFileName.StartsWith("~$", StringComparison.Ordinal)
            || !FOLDER_WORKBOOK_EXTENSIONS.Contains(Path.GetExtension(szFileName), StringComparer.OrdinalIgnoreCase))
            return null;

        var szFullPath = Path.GetFullPath(Path.Combine(szFolder, szFileName));
        return szFullPath.StartsWith(szFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? szFullPath
            : null;
    }

    /// <summary>讀活頁簿 ScadaExportedAt 與檔案修改時間；讀不到（壞檔、非 Excel）的 exportedAt 回 null</summary>
    private (DateTimeOffset? exportedAt, DateTime dtModified) ReadWorkbookStamp(string szPath)
    {
        var dtModified = File.GetLastWriteTime(szPath);
        try
        {
            using var fs = new FileStream(szPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var workbook = new XLWorkbook(fs);
            return (SourceJsonStamp.ReadExported(workbook), dtModified);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "讀取 Excel 匯出時間失敗: {Path}", szPath);
            return (null, dtModified);
        }
    }

    /// <summary>系統匯出後又被修改（修改時間晚於 ScadaExportedAt，超過容許誤差）</summary>
    private static bool IsUnimported(DateTimeOffset? exportedAt, DateTime dtModified)
        => exportedAt.HasValue && new DateTimeOffset(dtModified) > exportedAt.Value + MODIFIED_TOLERANCE;

    /// <summary>檔案是否被其他程式（Excel）開啟而無法寫入</summary>
    private static bool IsLockedForWrite(string szPath)
    {
        try
        {
            using var fs = new FileStream(szPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>共用 / 鎖定違規（ERROR_SHARING_VIOLATION 32、ERROR_LOCK_VIOLATION 33）</summary>
    private static bool IsLockViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>允許其他程式同時開啟的讀檔</summary>
    private static async Task<byte[]> ReadSharedAsync(string szPath)
    {
        await using var fs = new FileStream(szPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 原子寫 xlsx：*.xlsx.tmp → 既有檔 File.Replace 留 *.xlsx.bak／新檔 File.Move；
    /// 完成後把修改時間對齊匯出時間（之後在 Excel 存檔 → 修改時間晚於匯出時間 = 有未匯入修改）。失敗會清掉 tmp 再拋出。
    /// </summary>
    private static void WriteWorkbookFile(string szPath, byte[] bytes, DateTimeOffset dtExported)
    {
        var szTmpPath = szPath + ".tmp";
        try
        {
            File.WriteAllBytes(szTmpPath, bytes);
            if (File.Exists(szPath))
                File.Replace(szTmpPath, szPath, szPath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(szTmpPath, szPath);
        }
        catch
        {
            try { File.Delete(szTmpPath); } catch { /* ignore */ }
            throw;
        }
        File.SetLastWriteTimeUtc(szPath, dtExported.UtcDateTime);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void LocalizeIssues(List<SheetIssue> issues)
    {
        foreach (var issue in issues)
        {
            var szText = _l[issue.Key, issue.Args].Value;
            issue.Message = issue.Row > 0
                ? _l["srcxl.issue.format", issue.Row, issue.Column, szText].Value
                : szText;
        }
    }

    /// <summary>檔案內容 SHA-256（hex）</summary>
    private static string Fingerprint(string szPath)
    {
        using var stream = File.OpenRead(szPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
