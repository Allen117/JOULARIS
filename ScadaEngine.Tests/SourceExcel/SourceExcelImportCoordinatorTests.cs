using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Tests.SourceExcel;

/// <summary>
/// SourceExcelImportCoordinator 端到端（檔案層）測試：用暫存資料夾當 WatchedFolder，
/// 走完 預覽 → token → 提交 → 寫檔／刪檔 → 衝突偵測 → SID 位移確認 → 匯出往返，不碰 DB、不碰 Engine。
/// </summary>
public class SourceExcelImportCoordinatorTests : IDisposable
{
    private readonly string _szFolder;
    private readonly SourceExcelImportCoordinator _coordinator;
    private readonly ModbusExcelAdapter _modbus = new();
    private readonly DbPointExcelAdapter _db = new();

    public SourceExcelImportCoordinatorTests()
    {
        _szFolder = Path.Combine(Path.GetTempPath(), "srcxl_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_szFolder);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{SourceConfigFileIo.MODBUS_SECTION}:WatchedFolder"] = _szFolder,
            [$"{SourceConfigFileIo.DBPOINT_SECTION}:WatchedFolder"] = _szFolder,
        }).Build();

        var io = new SourceConfigFileIo(config, new StubEnv(_szFolder), NullLogger<SourceConfigFileIo>.Instance);
        _coordinator = new SourceExcelImportCoordinator(
            io,
            new MemoryCache(new MemoryCacheOptions()),
            new StubLocalizer<SourceExcelImportCoordinator>(),
            NullLogger<SourceExcelImportCoordinator>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szFolder, recursive: true); } catch { /* ignore */ }
    }

    // ── helpers ──

    private static ParsedSourceSheet ModbusSheet(string szName, params string[] pointNames)
    {
        var s = new ParsedSourceSheet { SheetName = szName };
        s.Header["IP"] = "10.0.0.1"; s.Header["Port"] = "502"; s.Header["ModbusId"] = "1"; s.Header["ConnectTimeout"] = "1000";
        var nAddr = 40001;
        foreach (var szPoint in pointNames)
        {
            var p = new ParsedSourcePoint();
            p.Fields["Name"] = szPoint; p.Fields["Address"] = (nAddr++).ToString(); p.Fields["DataType"] = "INTEGER";
            p.Fields["Ratio"] = "1"; p.Fields["Unit"] = ""; p.Fields["Min"] = "0"; p.Fields["Max"] = "100"; p.Fields["Device"] = "";
            s.Points.Add(p);
        }
        return s;
    }

    private static MemoryStream Xlsx(ISourceExcelAdapter adapter, params ParsedSourceSheet[] sheets)
        => new(SourceExcelTemplateWriter.Build(adapter, sheets));

    private string PathOf(string szName) => Path.Combine(_szFolder, szName + ".json");

    private void WriteExisting(string szName, ParsedSourceSheet sheet, string? szSourceWorkbook, Encoding? enc = null)
        => File.WriteAllText(PathOf(szName), _modbus.ToJson(sheet, szSourceWorkbook), enc ?? _modbus.NewFileEncoding);

    // ── 新增 → 提交 ──

    [Fact]
    public async Task Preview_NewSheets_Added_Then_Commit_WritesJsonWithSourceWorkbook_Utf16()
    {
        using var xlsx = Xlsx(_modbus, ModbusSheet("GW1", "A", "B"), ModbusSheet("GW2", "C"));
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");

        Assert.True(preview.Success, preview.Message);
        Assert.Equal(2, preview.Sheets.Count);
        Assert.All(preview.Sheets, s => Assert.Equal(SheetDiffKind.Added, s.Kind));
        Assert.Empty(preview.DeleteCandidates);
        Assert.NotEmpty(preview.Token);

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest
        {
            Token = preview.Token, ImportSheets = new() { "GW1", "GW2" },
        }, "eng");

        Assert.True(commit.Success, commit.Message);
        Assert.Equal(new[] { "GW1", "GW2" }, commit.Imported);
        Assert.True(File.Exists(PathOf("GW1")));
        Assert.True(File.Exists(PathOf("GW2")));

        // 新檔 UTF-16 LE BOM（與巨集一致）
        var bytes = File.ReadAllBytes(PathOf("GW1"));
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        var parsed = _modbus.ParseJson("GW1", File.ReadAllText(PathOf("GW1")));
        Assert.NotNull(parsed);
        Assert.Equal("盤A.xlsx", parsed!.SourceWorkbook);
        Assert.Equal(new[] { "A", "B" }, parsed.Points.Select(p => p.Name));

        // token 用過即失效
        var again = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.False(again.Success);
        Assert.Contains("preview_expired", again.Message);
    }

    [Fact]
    public async Task Commit_TokenOfAnotherUser_Rejected()
    {
        using var xlsx = Xlsx(_modbus, ModbusSheet("GW1", "A"));
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");
        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "someone-else");
        Assert.False(commit.Success);
        Assert.Contains("preview_user_mismatch", commit.Message);
        Assert.False(File.Exists(PathOf("GW1")));
    }

    // ── 覆寫 / 無變更 / 刪除候選 ──

    [Fact]
    public async Task Preview_SameWorkbook_MissingSheet_IsDeleteCandidate_Commit_MovesToDeleted()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        WriteExisting("GW2", ModbusSheet("GW2", "B"), "盤A.xlsx");
        WriteExisting("OtherPanel", ModbusSheet("OtherPanel", "Z"), "盤B.xlsx");
        WriteExisting("Legacy", ModbusSheet("Legacy", "L"), null);

        using var xlsx = Xlsx(_modbus, ModbusSheet("GW1", "A"));   // 與既有相同 → 無變更；GW2 沒帶 → 刪除候選
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤a.XLSX", "eng");

        Assert.True(preview.Success);
        Assert.Equal(SheetDiffKind.Unchanged, Assert.Single(preview.Sheets).Kind);
        var candidate = Assert.Single(preview.DeleteCandidates);
        Assert.Equal("GW2", candidate.Name);
        Assert.True(candidate.DefaultChecked);
        Assert.Equal(new[] { "Legacy", "OtherPanel" }, preview.OtherExisting.Select(o => o.Name));
        Assert.All(preview.OtherExisting, o => Assert.False(o.DefaultChecked));

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest
        {
            Token = preview.Token, ImportSheets = new() { "GW1" }, DeleteNames = new() { "GW2" },
        }, "eng");

        Assert.True(commit.Success, commit.Message);
        Assert.Empty(commit.Imported);                         // 無變更不寫
        Assert.Equal(new[] { "GW2" }, commit.Deleted);
        Assert.False(File.Exists(PathOf("GW2")));
        var backups = Directory.GetFiles(Path.Combine(_szFolder, SourceConfigFileIo.DELETED_SUBFOLDER), "GW2_*.json");
        Assert.Single(backups);
        Assert.True(File.Exists(PathOf("OtherPanel")));         // 其他盤沒勾 → 保留
    }

    [Fact]
    public async Task Preview_Overwrite_ShowsChanges_Commit_PreservesExistingEncoding()
    {
        // 既有檔為 UTF-8（Web 熱編輯寫過的 DB 檔那種情境）→ 覆寫後仍為 UTF-8
        WriteExisting("GW1", ModbusSheet("GW1", "A", "B"), "盤A.xlsx", new UTF8Encoding(false));

        var uploaded = ModbusSheet("GW1", "A", "B", "C");
        uploaded.Points[0].Fields["Ratio"] = "0.1";
        using var xlsx = Xlsx(_modbus, uploaded);
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");

        var diff = Assert.Single(preview.Sheets);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.False(diff.HasSidShift);
        Assert.Equal(1, diff.TailAddedCount);
        Assert.Contains(diff.PointChanges, c => c.Index == 1 && c.Summary == "Ratio: 1 → 0.1");

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.True(commit.Success, commit.Message);

        var bytes = File.ReadAllBytes(PathOf("GW1"));
        Assert.False(bytes[0] == 0xFF && bytes[1] == 0xFE);   // 沒被改成 UTF-16
        Assert.Equal(3, _modbus.ParseJson("GW1", File.ReadAllText(PathOf("GW1")))!.Points.Count);
    }

    // ── SID 位移 ──

    [Fact]
    public async Task Commit_SidShift_RequiresAcknowledge()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A", "B", "C"), "盤A.xlsx");
        using var xlsx = Xlsx(_modbus, ModbusSheet("GW1", "A", "X", "B", "C"));
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");

        var diff = Assert.Single(preview.Sheets);
        Assert.True(diff.HasSidShift);
        Assert.Equal(2, diff.SidShiftFromIndex);

        var denied = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.False(denied.Success);
        Assert.Contains("sid_shift_unacknowledged", denied.Message);
        Assert.Equal(3, _modbus.ParseJson("GW1", File.ReadAllText(PathOf("GW1")))!.Points.Count);   // 未寫

        var ok = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" }, AcknowledgeSidShift = true }, "eng");
        Assert.True(ok.Success, ok.Message);
        Assert.Equal(4, _modbus.ParseJson("GW1", File.ReadAllText(PathOf("GW1")))!.Points.Count);
    }

    // ── 提交前重新比對 ──

    [Fact]
    public async Task Commit_WhenExistingFileChangedAfterPreview_Conflict()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        using var xlsx = Xlsx(_modbus, ModbusSheet("GW1", "A", "B"));
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");

        // 有人同時熱編輯
        WriteExisting("GW1", ModbusSheet("GW1", "A-renamed"), "盤A.xlsx");

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.False(commit.Success);
        Assert.True(commit.Conflict);
        Assert.Contains("GW1", commit.Message);
        Assert.Equal("A-renamed", _modbus.ParseJson("GW1", File.ReadAllText(PathOf("GW1")))!.Points[0].Name);   // 沒被蓋掉
    }

    [Fact]
    public async Task Commit_SelectionOutsidePreview_Rejected()
    {
        using var xlsx = Xlsx(_modbus, ModbusSheet("GW1", "A"));
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");
        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" }, DeleteNames = new() { "NotInPreview" } }, "eng");
        Assert.False(commit.Success);
        Assert.Contains("invalid_selection", commit.Message);
        Assert.False(File.Exists(PathOf("GW1")));
    }

    [Fact]
    public async Task Preview_SheetWithErrors_CannotBeCommitted()
    {
        var bad = ModbusSheet("GW1", "A");
        bad.Points[0].Fields["Address"] = "99999";     // 位址錯 → 錯誤
        using var xlsx = Xlsx(_modbus, bad);
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");

        var diff = Assert.Single(preview.Sheets);
        Assert.Equal(SheetDiffKind.Error, diff.Kind);
        var err = Assert.Single(diff.Errors);
        Assert.Contains("srcxl.err.address_invalid", err.Message);   // 已翻譯（stub 回 key）且含列欄格式
        Assert.StartsWith("srcxl.issue.format", err.Message);

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.False(commit.Success);
        Assert.Contains("sheet_has_errors", commit.Message);
    }

    // ── 逐台刪除 ──

    [Fact]
    public async Task DeleteAsync_MovesToDeleted_And_UnknownFails()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), null);

        var ok = await _coordinator.DeleteAsync(_modbus, "GW1", "eng");
        Assert.True(ok.Success, ok.Message);
        Assert.False(File.Exists(PathOf("GW1")));
        Assert.Single(Directory.GetFiles(Path.Combine(_szFolder, SourceConfigFileIo.DELETED_SUBFOLDER)));

        var missing = await _coordinator.DeleteAsync(_modbus, "GW1", "eng");
        Assert.False(missing.Success);

        var unsafeName = await _coordinator.DeleteAsync(_modbus, "..\\x", "eng");
        Assert.False(unsafeName.Success);
    }

    // ── 匯出往返 ──

    [Fact]
    public async Task Export_Then_Preview_AllUnchanged_And_FilterByWorkbook()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A", "B"), "盤A.xlsx");
        WriteExisting("GW2", ModbusSheet("GW2", "C"), "盤B.xlsx");

        // 全部匯出 → 再匯入 → 全部無變更（上傳檔名與既有不同 → 無刪除候選，其他區列 0 筆因兩張都包含）
        var all = await _coordinator.ExportAsync(_modbus, null);
        using (var ms = new MemoryStream(all))
        {
            var preview = await _coordinator.PreviewAsync(_modbus, ms, "export.xlsx", "eng");
            Assert.True(preview.Success, preview.Message);
            Assert.Equal(2, preview.Sheets.Count);
            Assert.All(preview.Sheets, s => Assert.Equal(SheetDiffKind.Unchanged, s.Kind));
            Assert.Empty(preview.DeleteCandidates);
            Assert.Empty(preview.OtherExisting);
        }

        // 依來源檔匯出 → 只有該盤
        var onlyA = await _coordinator.ExportAsync(_modbus, "盤A");
        using (var ms = new MemoryStream(onlyA))
        using (var wb = new ClosedXML.Excel.XLWorkbook(ms))
        {
            Assert.Equal(new[] { "GW1" }, wb.Worksheets.Select(w => w.Name));
        }

        // 既有一覽帶 SourceWorkbook
        var existing = await _coordinator.ListExistingAsync(_modbus);
        Assert.Equal(new[] { "GW1", "GW2" }, existing.Select(e => e.Name));
        Assert.Equal("盤B.xlsx", existing[1].SourceWorkbook);
    }

    [Fact]
    public async Task DbPoint_Preview_Commit_WritesUtf8_WithNameAndNumbers()
    {
        var sheet = new ParsedSourceSheet { SheetName = "DB1" };
        sheet.Header["PollingInterval"] = "2000"; sheet.Header["ConnectTimeout"] = "1000"; sheet.Header["MonitorEnabled"] = "TRUE";
        var p = new ParsedSourcePoint(); p.Fields["Name"] = "度數"; p.Fields["Unit"] = "kWh"; p.Fields["Min"] = "0"; p.Fields["Max"] = "100";
        sheet.Points.Add(p);

        using var xlsx = Xlsx(_db, sheet);
        var preview = await _coordinator.PreviewAsync(_db, xlsx, "DB來源.xlsx", "eng");
        Assert.True(preview.Success, preview.Message);
        Assert.Equal(SheetDiffKind.Added, Assert.Single(preview.Sheets).Kind);

        var commit = await _coordinator.CommitAsync(_db, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "DB1" } }, "eng");
        Assert.True(commit.Success, commit.Message);

        var bytes = File.ReadAllBytes(PathOf("DB1"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));   // UTF-8 BOM（與 DbPointConfigFileService 一致）
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(PathOf("DB1")));
        Assert.Equal("DB1", doc.RootElement.GetProperty("Name").GetString());
        Assert.Equal(2000, doc.RootElement.GetProperty("PollingInterval").GetInt32());
        Assert.Equal("DB來源.xlsx", doc.RootElement.GetProperty("SourceWorkbook").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Number, doc.RootElement.GetProperty("Points")[0].GetProperty("Min").ValueKind);
    }

    [Fact]
    public async Task Preview_UnreadableFile_Fails()
    {
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("not an xlsx"));
        var preview = await _coordinator.PreviewAsync(_modbus, garbage, "x.xlsx", "eng");
        Assert.False(preview.Success);
        Assert.Contains("file_unreadable", preview.Message);
    }

    // ── 來源資料夾內的 Excel（plan D 段）──

    private string XlsxPathOf(string szFileName) => Path.Combine(_szFolder, szFileName);

    /// <summary>模擬使用者在 Excel 存檔：只把修改時間往後推（內容不變）</summary>
    private void TouchLater(string szFileName)
        => File.SetLastWriteTime(XlsxPathOf(szFileName), DateTime.Now.AddMinutes(5));

    private static List<string> SheetNamesOf(string szPath)
    {
        using var wb = new ClosedXML.Excel.XLWorkbook(szPath);
        return wb.Worksheets.Select(w => w.Name).ToList();
    }

    [Fact]
    public async Task ExportToFolder_All_WritesFixedName_StampedAndMtimeAligned_JsonUntouched()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        WriteExisting("GW2", ModbusSheet("GW2", "B"), "盤B.xlsx");

        var result = await _coordinator.ExportToFolderAsync(_modbus, null, false, "eng");

        Assert.True(result.Success, result.Message);
        Assert.Equal(XlsxPathOf("Modbus設定.xlsx"), result.Path);
        Assert.Equal(new[] { "GW1", "GW2" }, SheetNamesOf(result.Path!));

        using (var wb = new ClosedXML.Excel.XLWorkbook(result.Path!))
        {
            var exportedAt = SourceJsonStamp.ReadExported(wb);
            Assert.NotNull(exportedAt);
            Assert.Equal(exportedAt!.Value.UtcDateTime, File.GetLastWriteTimeUtc(result.Path!));
        }

        // xlsx 不是設定檔：一覽只有 JSON
        Assert.Equal(new[] { "GW1", "GW2" }, (await _coordinator.ListExistingAsync(_modbus)).Select(e => e.Name));
        var list = _coordinator.ListFolderWorkbooks(_modbus);
        Assert.False(Assert.Single(list).HasUnimportedChanges);
    }

    [Fact]
    public async Task ExportToFolder_ByWorkbook_UsesSourceWorkbookName()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsm");
        WriteExisting("GW2", ModbusSheet("GW2", "B"), "盤B.xlsx");

        var result = await _coordinator.ExportToFolderAsync(_modbus, "盤A.xlsm", false, "eng");

        Assert.True(result.Success, result.Message);
        Assert.Equal(XlsxPathOf("盤A.xlsx"), result.Path);
        Assert.Equal(new[] { "GW1" }, SheetNamesOf(result.Path!));
    }

    [Fact]
    public async Task ExportToFolder_TemplateNameOrPathEscape_Rejected()
    {
        Assert.False((await _coordinator.ExportToFolderAsync(_modbus, "Modbus範本.xlsx", false, "eng")).Success);
        Assert.False((await _coordinator.ExportToFolderAsync(_modbus, "..\\evil.xlsx", false, "eng")).Success);
        Assert.Empty(Directory.GetFiles(_szFolder, "*.xlsx"));
    }

    [Fact]
    public async Task ExportToFolder_FileOpenInExcel_Locked_FileUnchanged()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, null, false, "eng")).Success);
        var szPath = XlsxPathOf("Modbus設定.xlsx");
        var before = File.ReadAllBytes(szPath);

        // Excel 開檔 = 允許他人讀、拒絕寫入
        using (new FileStream(szPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await _coordinator.ExportToFolderAsync(_modbus, null, true, "eng");
            Assert.False(result.Success);
            Assert.True(result.Locked);
            Assert.Contains("folder_locked", result.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(szPath));
        Assert.False(File.Exists(szPath + ".tmp"));
    }

    [Fact]
    public async Task ExportToFolder_UnimportedChangesOrForeignFile_NeedsConfirm_ThenForceOverwrites()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, null, false, "eng")).Success);
        TouchLater("Modbus設定.xlsx");

        var confirm = await _coordinator.ExportToFolderAsync(_modbus, null, false, "eng");
        Assert.False(confirm.Success);
        Assert.True(confirm.NeedsConfirm);
        Assert.Contains("folder_confirm_unimported", confirm.Message);

        var forced = await _coordinator.ExportToFolderAsync(_modbus, null, true, "eng");
        Assert.True(forced.Success, forced.Message);
        Assert.True(File.Exists(XlsxPathOf("Modbus設定.xlsx.bak")));

        // 非系統匯出的同名檔（沒有 ScadaExportedAt）同樣要確認
        File.WriteAllBytes(XlsxPathOf("盤A.xlsx"), SourceExcelTemplateWriter.Build(_modbus, new[] { ModbusSheet("GW1", "A") }));
        var foreign = await _coordinator.ExportToFolderAsync(_modbus, "盤A.xlsx", false, "eng");
        Assert.True(foreign.NeedsConfirm);
        Assert.Contains("folder_confirm_not_exported", foreign.Message);
    }

    [Fact]
    public async Task ListFolderWorkbooks_ExcludesTemplateAndLockFiles_FlagsUnimported()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, null, false, "eng")).Success);
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, "盤A.xlsx", false, "eng")).Success);
        File.WriteAllBytes(XlsxPathOf("Modbus範本.xlsx"), SourceExcelTemplateWriter.BuildTemplate(_modbus));
        File.WriteAllText(XlsxPathOf("~$盤A.xlsx"), "lock");
        TouchLater("盤A.xlsx");

        var list = _coordinator.ListFolderWorkbooks(_modbus);

        Assert.Equal(new[] { "盤A.xlsx", "Modbus設定.xlsx" }, list.Select(w => w.Name));   // 修改時間新到舊
        Assert.True(list[0].HasUnimportedChanges);
        Assert.False(list[1].HasUnimportedChanges);
    }

    [Fact]
    public async Task Refresh_AfterJsonChange_RegeneratesStamped_SkipsUnimportedAndForeign_NeverCreates()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, null, false, "eng")).Success);   // Modbus設定.xlsx
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, "盤A.xlsx", false, "eng")).Success); // 盤A.xlsx
        TouchLater("盤A.xlsx");
        File.WriteAllBytes(XlsxPathOf("盤B.xlsx"), SourceExcelTemplateWriter.Build(_modbus, new[] { ModbusSheet("GW2", "B") }));

        // 模擬熱編輯新增一台（JSON 已寫好），之後重產
        WriteExisting("GW2", ModbusSheet("GW2", "B"), "盤B.xlsx");
        var szNotice = await _coordinator.RefreshFolderWorkbooksAsync(_modbus);

        Assert.Equal(new[] { "GW1", "GW2" }, SheetNamesOf(XlsxPathOf("Modbus設定.xlsx")));   // 全部匯出檔已重產
        Assert.NotNull(szNotice);
        Assert.Contains("folder_reason_unimported:盤A.xlsx", szNotice);
        Assert.Contains("folder_reason_not_exported:盤B.xlsx", szNotice);
        Assert.False(File.Exists(XlsxPathOf("盤C.xlsx")));
        Assert.Equal(3, Directory.GetFiles(_szFolder, "*.xlsx").Length);
    }

    [Fact]
    public async Task Refresh_NoWorkbooksInFolder_ReturnsNull_CreatesNothing()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        Assert.Null(await _coordinator.RefreshFolderWorkbooksAsync(_modbus));
        Assert.Empty(Directory.GetFiles(_szFolder, "*.xlsx"));
    }

    [Fact]
    public async Task PreviewFromFolder_FullImport_RegeneratesFile_NoLongerFlaggedUnimported()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, "盤A.xlsx", false, "eng")).Success);

        // 在 Excel 改了點位名稱並存檔
        File.WriteAllBytes(XlsxPathOf("盤A.xlsx"), SourceExcelTemplateWriter.Build(_modbus, new[] { ModbusSheet("GW1", "A2") }, isExport: true, exportedAt: DateTimeOffset.Now.AddMinutes(-10)));
        Assert.True(Assert.Single(_coordinator.ListFolderWorkbooks(_modbus)).HasUnimportedChanges);

        var preview = await _coordinator.PreviewFromFolderAsync(_modbus, "盤A.xlsx", "eng");
        Assert.True(preview.Success, preview.Message);
        Assert.Equal(SheetDiffKind.Overwrite, Assert.Single(preview.Sheets).Kind);

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.True(commit.Success, commit.Message);
        Assert.Null(commit.WorkbookNotice);

        Assert.Equal("A2", _modbus.ParseJson("GW1", File.ReadAllText(PathOf("GW1")))!.Points[0].Name);
        Assert.False(Assert.Single(_coordinator.ListFolderWorkbooks(_modbus)).HasUnimportedChanges);
    }

    [Fact]
    public async Task PreviewFromFolder_PartialImport_KeepsFile_StillFlaggedUnimported()
    {
        WriteExisting("GW1", ModbusSheet("GW1", "A"), "盤A.xlsx");
        WriteExisting("GW2", ModbusSheet("GW2", "B"), "盤A.xlsx");
        Assert.True((await _coordinator.ExportToFolderAsync(_modbus, "盤A.xlsx", false, "eng")).Success);

        File.WriteAllBytes(XlsxPathOf("盤A.xlsx"), SourceExcelTemplateWriter.Build(_modbus,
            new[] { ModbusSheet("GW1", "A2"), ModbusSheet("GW2", "B2") }, isExport: true, exportedAt: DateTimeOffset.Now.AddMinutes(-10)));
        var before = File.ReadAllBytes(XlsxPathOf("盤A.xlsx"));

        var preview = await _coordinator.PreviewFromFolderAsync(_modbus, "盤A.xlsx", "eng");
        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.True(commit.Success, commit.Message);

        // GW2 的修改還沒進 JSON → 不可重產洗掉
        Assert.Equal(before, File.ReadAllBytes(XlsxPathOf("盤A.xlsx")));
        Assert.Contains("folder_reason_unimported:盤A.xlsx", commit.WorkbookNotice);
        Assert.True(Assert.Single(_coordinator.ListFolderWorkbooks(_modbus)).HasUnimportedChanges);
    }

    [Fact]
    public async Task PreviewFromFolder_TemplateOrMissingOrEscape_Fails()
    {
        File.WriteAllBytes(XlsxPathOf("Modbus範本.xlsx"), SourceExcelTemplateWriter.BuildTemplate(_modbus));
        Assert.Contains("folder_file_not_found", (await _coordinator.PreviewFromFolderAsync(_modbus, "Modbus範本.xlsx", "eng")).Message);
        Assert.Contains("folder_file_not_found", (await _coordinator.PreviewFromFolderAsync(_modbus, "沒有.xlsx", "eng")).Message);
        Assert.Contains("folder_file_not_found", (await _coordinator.PreviewFromFolderAsync(_modbus, "..\\x.xlsx", "eng")).Message);
        Assert.Contains("folder_file_not_found", (await _coordinator.PreviewFromFolderAsync(_modbus, "GW1.json", "eng")).Message);
    }

    // ── stubs ──

    private sealed class StubEnv : IWebHostEnvironment
    {
        public StubEnv(string szRoot) { ContentRootPath = szRoot; WebRootPath = szRoot; }
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>回傳 key（有參數時附 ":arg|arg"），讓測試能以 key 斷言訊息</summary>
    private sealed class StubLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments]
            => new(name, name + ":" + string.Join("|", arguments.Select(a => a?.ToString())));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}
