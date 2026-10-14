using System.Text.Json.Nodes;
using ClosedXML.Excel;
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
/// plan 2026-10-12 問題 B：「網頁修改時間 vs Excel 匯出時間」衝突判定矩陣、活頁簿自訂屬性往返、
/// DiffBuilder 只在真的會覆寫時標衝突、Coordinator 提交需另外確認、匯入寫檔後 WebEditedAt 清除、先匯出再匯入不警告。
/// </summary>
public class WebEditedWarningTests : IDisposable
{
    private readonly string _szFolder;
    private readonly SourceExcelImportCoordinator _coordinator;
    private readonly ModbusExcelAdapter _modbus = new();

    public WebEditedWarningTests()
    {
        _szFolder = Path.Combine(Path.GetTempPath(), "srcxl_webedit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_szFolder);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{SourceConfigFileIo.MODBUS_SECTION}:WatchedFolder"] = _szFolder,
        }).Build();

        var io = new SourceConfigFileIo(config, new StubEnv(_szFolder), NullLogger<SourceConfigFileIo>.Instance);
        _coordinator = new SourceExcelImportCoordinator(io, new MemoryCache(new MemoryCacheOptions()),
            new StubLocalizer<SourceExcelImportCoordinator>(), NullLogger<SourceExcelImportCoordinator>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szFolder, recursive: true); } catch { /* ignore */ }
    }

    // ── 判定矩陣 ──

    public static IEnumerable<object?[]> ConflictMatrix()
    {
        var t0 = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(8));
        yield return new object?[] { null, null, false };                       // 沒被網頁改過、Excel 無匯出時間 → 不警告
        yield return new object?[] { null, t0, false };                         // 沒被網頁改過 → 不警告
        yield return new object?[] { t0, null, true };                          // 改過、Excel 無匯出時間（範本 / 手工） → 警告
        yield return new object?[] { t0, t0.AddMinutes(-5), true };             // 匯出早於修改（舊檔） → 警告
        yield return new object?[] { t0, t0, false };                           // 同一時刻 → 不警告
        yield return new object?[] { t0, t0.AddMinutes(5), false };             // 先匯出再修改 Excel → 不警告
        yield return new object?[] { t0, t0.ToOffset(TimeSpan.Zero).AddMinutes(5), false }; // 不同時區表示同一瞬間 +5 分 → 不警告
    }

    [Theory]
    [MemberData(nameof(ConflictMatrix))]
    public void IsWebEditConflict_Matrix(DateTimeOffset? webEditedAt, DateTimeOffset? exportedAt, bool isExpected)
        => Assert.Equal(isExpected, SourceJsonStamp.IsWebEditConflict(webEditedAt, exportedAt));

    [Fact]
    public void Stamp_RoundTrip_JsonAndWorkbook()
    {
        var root = new JsonObject();
        SourceJsonStamp.StampWebEdited(root);
        var dt = SourceJsonStamp.ReadWebEdited(root);
        Assert.NotNull(dt);
        Assert.True((DateTimeOffset.Now - dt!.Value).Duration() < TimeSpan.FromMinutes(1));

        var stamp = new DateTimeOffset(2026, 10, 12, 14, 3, 21, TimeSpan.FromHours(8));
        byte[] bytes;
        using (var wb = new XLWorkbook())
        {
            wb.Worksheets.Add("S");
            SourceJsonStamp.StampExported(wb, stamp);
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            bytes = ms.ToArray();
        }
        using (var wb2 = new XLWorkbook(new MemoryStream(bytes)))
        {
            Assert.Equal(stamp, SourceJsonStamp.ReadExported(wb2));
        }

        using var plain = new XLWorkbook();
        plain.Worksheets.Add("S");
        Assert.Null(SourceJsonStamp.ReadExported(plain));
    }

    [Fact]
    public void Template_HasNoExportStamp_Export_Has()
    {
        using var tpl = new XLWorkbook(new MemoryStream(SourceExcelTemplateWriter.BuildTemplate(_modbus)));
        Assert.Null(SourceJsonStamp.ReadExported(tpl));

        using var exp = new XLWorkbook(new MemoryStream(SourceExcelTemplateWriter.Build(_modbus, new[] { Sheet("D", "A") }, isExport: true)));
        Assert.NotNull(SourceJsonStamp.ReadExported(exp));
    }

    // ── DiffBuilder ──

    [Fact]
    public void Diff_Unchanged_NoConflictEvenIfWebEdited()
    {
        var existing = Sheet("D", "A", "B");
        existing.WebEditedAt = DateTimeOffset.Now;
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "B"), existing, _modbus, exportedAt: null);
        Assert.Equal(SheetDiffKind.Unchanged, diff.Kind);
        Assert.False(diff.HasWebEditConflict);
        Assert.NotNull(diff.WebEditedAt);
    }

    [Fact]
    public void Diff_Changed_ConflictOnlyWhenExportOlderOrUnknown()
    {
        var t0 = DateTimeOffset.Now;
        var existing = Sheet("D", "A", "B");
        existing.WebEditedAt = t0;
        var uploaded = Sheet("D", "A", "B改");

        Assert.True(SourceExcelDiffBuilder.Build(uploaded, existing, _modbus, exportedAt: null).HasWebEditConflict);
        Assert.True(SourceExcelDiffBuilder.Build(uploaded, existing, _modbus, exportedAt: t0.AddMinutes(-1)).HasWebEditConflict);
        Assert.False(SourceExcelDiffBuilder.Build(uploaded, existing, _modbus, exportedAt: t0.AddMinutes(1)).HasWebEditConflict);

        var notEdited = Sheet("D", "A", "B");
        Assert.False(SourceExcelDiffBuilder.Build(uploaded, notEdited, _modbus, exportedAt: null).HasWebEditConflict);
    }

    // ── Coordinator 端到端 ──

    [Fact]
    public async Task OldWorkbook_OverwritingWebEdit_RequiresAck_ThenClearsWebEditedAt()
    {
        // 既有 JSON 曾被網頁熱編輯（有 WebEditedAt）
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(_modbus.ToJson(Sheet("GW1", "A", "B-web"), "盤A.xlsx")));
        SourceJsonStamp.StampWebEdited(root);
        File.WriteAllText(PathOf("GW1"), root.ToJsonString(), _modbus.NewFileEncoding);

        // 拿「沒有匯出時間」的舊 Excel（範本 / 修改前的檔）重匯，內容不同
        using var xlsx = new MemoryStream(SourceExcelTemplateWriter.Build(_modbus, new[] { Sheet("GW1", "A", "B-old") }));
        var preview = await _coordinator.PreviewAsync(_modbus, xlsx, "盤A.xlsx", "eng");
        Assert.True(preview.Success, preview.Message);
        Assert.Null(preview.ExportedAt);
        var diff = Assert.Single(preview.Sheets);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.True(diff.HasWebEditConflict);
        Assert.NotNull(diff.WebEditedAt);

        var noAck = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.False(noAck.Success);
        Assert.Contains("web_edit_unacknowledged", noAck.Message);

        var ack = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" }, AcknowledgeWebEdit = true }, "eng");
        Assert.True(ack.Success, ack.Message);

        // 匯入後 JSON 不帶 WebEditedAt（Excel 與 JSON 重新一致）
        var after = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(PathOf("GW1"))));
        Assert.False(after.ContainsKey(SourceJsonStamp.WEB_EDITED_AT));
        Assert.Equal("B-old", after["Tags"]![1]!["Name"]?.ToString());
    }

    [Fact]
    public async Task ExportLatest_ThenEdit_ThenImport_NoConflict()
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(_modbus.ToJson(Sheet("GW1", "A", "B"), "盤A.xlsx")));
        root[SourceJsonStamp.WEB_EDITED_AT] = SourceJsonStamp.Format(DateTimeOffset.Now.AddMinutes(-10));
        File.WriteAllText(PathOf("GW1"), root.ToJsonString(), _modbus.NewFileEncoding);

        // 匯出最新版（帶 ScadaExportedAt = 現在）→ 在 Excel 裡改一欄 → 匯入
        var exported = await _coordinator.ExportAsync(_modbus, null);
        byte[] edited;
        using (var wb = new XLWorkbook(new MemoryStream(exported)))
        {
            wb.Worksheet("GW1").Cell(4, 1).Value = "B-excel";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            edited = ms.ToArray();
        }

        var preview = await _coordinator.PreviewAsync(_modbus, new MemoryStream(edited), "盤A.xlsx", "eng");
        Assert.True(preview.Success, preview.Message);
        Assert.NotNull(preview.ExportedAt);
        var diff = Assert.Single(preview.Sheets);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.False(diff.HasWebEditConflict);

        var commit = await _coordinator.CommitAsync(_modbus, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "GW1" } }, "eng");
        Assert.True(commit.Success, commit.Message);
    }

    // ── helpers / stubs ──

    private string PathOf(string szName) => Path.Combine(_szFolder, szName + ".json");

    private static ParsedSourceSheet Sheet(string szName, params string[] pointNames)
    {
        var s = new ParsedSourceSheet { SheetName = szName };
        s.Header["IP"] = "10.0.0.1"; s.Header["Port"] = "502"; s.Header["ModbusId"] = "1"; s.Header["ConnectTimeout"] = "1000";
        var nAddr = 40001;
        foreach (var szPoint in pointNames)
        {
            var p = new ParsedSourcePoint { RowNumber = s.Points.Count + 3 };
            p.Fields["Name"] = szPoint; p.Fields["Address"] = (nAddr++).ToString(); p.Fields["DataType"] = "INTEGER";
            p.Fields["Ratio"] = "1"; p.Fields["Unit"] = ""; p.Fields["Min"] = "0"; p.Fields["Max"] = "100"; p.Fields["Device"] = "";
            s.Points.Add(p);
        }
        return s;
    }

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

    private sealed class StubLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments]
            => new(name, name + ":" + string.Join("|", arguments.Select(a => a?.ToString())));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}
