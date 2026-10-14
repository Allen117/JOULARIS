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
/// OpcUaExcelAdapter（plan 2026-10-12 問題 C）：版面解析 / 驗證 / Device 推導 / Seq 對帳與配號（SID 不漂移）/ 密碼保留 /
/// JSON 序列化 / 匯出往返 / 鍵值式 diff / Coordinator 不產刪除候選。
/// 版面：第 1 列標籤值成對；第 3 列起 A~I = Name / TagName / ControlType / Ratio / Unit / Min / Max / Device / Seq。
/// </summary>
public class OpcUaExcelAdapterTests : IDisposable
{
    private readonly OpcUaExcelAdapter _adapter = new();
    private readonly string _szFolder;
    private readonly SourceExcelImportCoordinator _coordinator;

    public OpcUaExcelAdapterTests()
    {
        _szFolder = Path.Combine(Path.GetTempPath(), "srcxl_opcua_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_szFolder);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{SourceConfigFileIo.OPCUA_SECTION}:WatchedFolder"] = _szFolder,
        }).Build();
        var io = new SourceConfigFileIo(config, new StubEnv(_szFolder), NullLogger<SourceConfigFileIo>.Instance);
        _coordinator = new SourceExcelImportCoordinator(io, new MemoryCache(new MemoryCacheOptions()),
            new StubLocalizer<SourceExcelImportCoordinator>(), NullLogger<SourceExcelImportCoordinator>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szFolder, recursive: true); } catch { /* ignore */ }
    }

    private const string EXISTING_JSON = """
    {
      "Name": "OpcUa1",
      "EndpointUrl": "opc.tcp://192.168.1.10:4840",
      "Username": "user1",
      "Password": "secret",
      "PollingInterval": 1000,
      "ConnectTimeout": 5000,
      "MonitorEnabled": true,
      "MaxNodesPerRead": 0,
      "NextSeq": 5,
      "CustomTop": "keep",
      "Devices": [
        { "Name": "D1", "Tags": [
          { "Seq": 1, "Name": "D1T", "TagName": "ns=2;s=D1.T", "ControlType": "", "Ratio": "1", "Unit": "°C", "Min": 0, "Max": 100 },
          { "Seq": 2, "Name": "D1F", "TagName": "ns=2;s=D1.F", "ControlType": "AO", "Ratio": "0.1", "Unit": "" } ] },
        { "Name": "D2", "Tags": [
          { "Seq": 3, "Name": "D2T", "TagName": "ns=2;s=D2.T", "ControlType": "", "Ratio": "1", "Unit": "", "Min": 0, "Max": 100 },
          { "Seq": 4, "Name": "D2X", "TagName": "ns=2;s=D2.X", "ControlType": "DO", "Ratio": "1", "Unit": "" } ] }
      ]
    }
    """;

    // ── helpers ──

    private static (XLWorkbook wb, IXLWorksheet ws) NewSheet(string szName = "OpcUa1", string szEndpoint = "opc.tcp://10.0.0.1:4840",
        string? szPassword = null, object? timeout = null, object? polling = null, object? monitor = null, object? maxNodes = null)
    {
        var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(szName);
        ws.Cell("A1").Value = "EndpointUrl"; ws.Cell("B1").Value = szEndpoint;
        ws.Cell("C1").Value = "Username"; ws.Cell("D1").Value = "user1";
        ws.Cell("E1").Value = "Password"; if (szPassword != null) ws.Cell("F1").Value = szPassword;
        ws.Cell("G1").Value = "ConnectTimeout"; if (timeout != null) ws.Cell("H1").Value = XLCellValue.FromObject(timeout);
        ws.Cell("I1").Value = "PollingInterval"; if (polling != null) ws.Cell("J1").Value = XLCellValue.FromObject(polling);
        ws.Cell("K1").Value = "MonitorEnabled"; if (monitor != null) ws.Cell("L1").Value = XLCellValue.FromObject(monitor);
        ws.Cell("M1").Value = "MaxNodesPerRead"; if (maxNodes != null) ws.Cell("N1").Value = XLCellValue.FromObject(maxNodes);
        ws.Cell("A2").Value = "名稱"; ws.Cell("B2").Value = "TagName"; ws.Cell("I2").Value = "Seq";
        return (wb, ws);
    }

    private static void Row(IXLWorksheet ws, int r, object? name, object? tagName, object? controlType = null, object? ratio = null,
        object? unit = null, object? min = null, object? max = null, object? device = null, object? seq = null)
    {
        void Set(int c, object? v) { if (v != null) ws.Cell(r, c).Value = XLCellValue.FromObject(v); }
        Set(1, name); Set(2, tagName); Set(3, controlType); Set(4, ratio); Set(5, unit); Set(6, min); Set(7, max); Set(8, device); Set(9, seq);
    }

    private static string Errors(ParsedSourceSheet s) => string.Join("; ", s.Errors.Select(e => $"{e.Row}{e.Column}:{e.Key}:{string.Join(",", e.Args)}"));

    private static int[] Seqs(ParsedSourceSheet s) => s.Points.Select(p => int.Parse(p.Get("Seq"))).ToArray();

    private string PathOf(string szName) => Path.Combine(_szFolder, szName + ".json");

    // ── 解析 ──

    [Fact]
    public void Parse_StandardLayout_HeaderPointsAndDeviceInference()
    {
        var (wb, ws) = NewSheet(szPassword: "pw", timeout: 3000, polling: 500, monitor: false, maxNodes: 200);
        using (wb)
        {
            Row(ws, 3, "T", "ns=2;s=D1.T", null, 1, "°C", 0, 100, null, 1);
            Row(ws, 4, "F", "ns=2;s=D1.F", "ao", 0.1, null, null, null, "Dev", 2);
            Row(ws, 5, "I", "i=2255", "do", null, null, null, null, null, null);

            var sheet = _adapter.ParseSheet(ws);

            Assert.True(sheet.IsValid, Errors(sheet));
            Assert.Equal("opc.tcp://10.0.0.1:4840", sheet.Header["EndpointUrl"]);
            Assert.Equal("user1", sheet.Header["Username"]);
            Assert.Equal("********", sheet.Header["Password"]);
            Assert.Equal("pw", sheet.Header["PasswordRaw"]);
            Assert.Equal("3000", sheet.Header["ConnectTimeout"]);
            Assert.Equal("500", sheet.Header["PollingInterval"]);
            Assert.Equal("FALSE", sheet.Header["MonitorEnabled"]);
            Assert.Equal("200", sheet.Header["MaxNodesPerRead"]);

            Assert.Equal(3, sheet.Points.Count);
            Assert.Equal("D1", sheet.Points[0].Get("Device"));        // 由 NodeId s=D1.T 推得
            Assert.Equal("Dev", sheet.Points[1].Get("Device"));       // 明填優先
            Assert.Equal("OpcUa1", sheet.Points[2].Get("Device"));    // 數值 NodeId 推不出 → 工作表名稱
            Assert.Equal("AO", sheet.Points[1].Get("ControlType"));
            Assert.Equal("DO", sheet.Points[2].Get("ControlType"));
            Assert.Equal("0.1", sheet.Points[1].Get("Ratio"));
            Assert.Equal("1", sheet.Points[2].Get("Ratio"));          // 空白 → 1
            Assert.Equal("1", sheet.Points[0].Get("Seq"));
            Assert.Equal("", sheet.Points[2].Get("Seq"));
        }
    }

    [Fact]
    public void Parse_OldXlsmLayout_NoSeqNoMaxNodes_Defaults()
    {
        // 舊巨集版面：第 1 列只到 PollingInterval、沒有 Seq 欄
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("OpcUa1");
        ws.Cell("A1").Value = "EndpointUrl"; ws.Cell("B1").Value = "opc.tcp://192.168.1.10:4840";
        ws.Cell("C1").Value = "Username"; ws.Cell("D1").Value = "user1";
        ws.Cell("E1").Value = "Password"; ws.Cell("F1").Value = "pass1";
        ws.Cell("G1").Value = "ConnectTimeout"; ws.Cell("H1").Value = 1000;
        ws.Cell("I1").Value = "PollingInterval"; ws.Cell("J1").Value = 1000;
        ws.Cell("A2").Value = "名稱"; ws.Cell("B2").Value = "TagName";
        Row(ws, 3, "D1T", "ns=2;s=D1.T", null, 1, null, 0, 100);
        Row(ws, 4, "D2T", "ns=2;s=D2.T", null, 1, null, 0, 100);

        var sheet = _adapter.ParseSheet(ws);
        Assert.True(sheet.IsValid, Errors(sheet));
        Assert.Equal("TRUE", sheet.Header["MonitorEnabled"]);
        Assert.Equal("0", sheet.Header["MaxNodesPerRead"]);
        Assert.All(sheet.Points, p => Assert.Equal("", p.Get("Seq")));
        Assert.Equal(new[] { "D1", "D2" }, sheet.Points.Select(p => p.Get("Device")));
    }

    [Fact]
    public void Parse_ValidationErrors()
    {
        var (wb, ws) = NewSheet(szName: "bad name!", szEndpoint: "http://x", timeout: 500, polling: 100, monitor: "maybe", maxNodes: -1);
        using (wb)
        {
            Row(ws, 3, "NoTag", null);
            Row(ws, 4, "BadTag", "not a node id ;; =");
            Row(ws, 5, "BadCtl", "ns=2;s=A.B", "AI");
            Row(ws, 6, "Ratio0", "ns=2;s=A.C", null, 0);
            Row(ws, 7, "MinMax", "ns=2;s=A.D", null, 1, null, 10, 5);
            Row(ws, 8, "SeqNeg", "ns=2;s=A.E", null, 1, null, null, null, null, -1);
            Row(ws, 9, "SeqDup1", "ns=2;s=A.F", null, 1, null, null, null, null, 7);
            Row(ws, 10, "SeqDup2", "ns=2;s=A.G", null, 1, null, null, null, null, 7);

            var sheet = _adapter.ParseSheet(ws);
            var keys = sheet.Errors.Select(e => e.Key).ToList();

            Assert.Contains("srcxl.err.opcua_name_invalid", keys);
            Assert.Contains("srcxl.err.header_endpoint_invalid", keys);
            Assert.Contains("srcxl.err.header_timeout_min", keys);
            Assert.Contains("srcxl.err.header_polling_min", keys);
            Assert.Contains("srcxl.err.header_monitor_invalid", keys);
            Assert.Contains("srcxl.err.header_maxnodes_invalid", keys);
            Assert.Contains(sheet.Errors, e => e.Row == 3 && e.Key == "srcxl.err.nodeid_required");
            Assert.Contains(sheet.Errors, e => e.Row == 4 && e.Key == "srcxl.err.nodeid_invalid");
            Assert.Contains(sheet.Errors, e => e.Row == 5 && e.Key == "srcxl.err.controltype_invalid");
            Assert.Contains(sheet.Errors, e => e.Row == 6 && e.Key == "srcxl.err.ratio_zero");
            Assert.Contains(sheet.Errors, e => e.Row == 7 && e.Key == "srcxl.err.minmax_invalid");
            Assert.Contains(sheet.Errors, e => e.Row == 8 && e.Key == "srcxl.err.seq_invalid");
            Assert.Contains(sheet.Errors, e => e.Row == 10 && e.Key == "srcxl.err.seq_duplicate");
        }
    }

    [Theory]
    [InlineData("ns=2;s=D1.T", "D1")]
    [InlineData("ns=2;s=Plant.Line1.Temp", "Plant")]
    [InlineData("s=NoDot", "Sheet")]
    [InlineData("ns=2;i=1001", "Sheet")]
    [InlineData("", "Sheet")]
    public void InferDevice_Rules(string szNodeId, string szExpected)
        => Assert.Equal(szExpected, OpcUaExcelAdapter.InferDevice(szNodeId, "Sheet"));

    // ── Seq 對帳 ──

    [Fact]
    public void Reconcile_RoundTrip_SeqUnchanged()
    {
        var existing = _adapter.ParseJson("OpcUa1", EXISTING_JSON)!;
        var xlsx = SourceExcelTemplateWriter.Build(_adapter, new[] { existing }, isExport: true);
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var uploaded = _adapter.ParseSheet(wb.Worksheets.First());
        Assert.True(uploaded.IsValid, Errors(uploaded));

        _adapter.Reconcile(uploaded, existing);
        Assert.True(uploaded.IsValid, Errors(uploaded));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Seqs(uploaded));

        var diff = SourceExcelDiffBuilder.Build(uploaded, existing, _adapter);
        Assert.Equal(SheetDiffKind.Unchanged, diff.Kind);
    }

    [Fact]
    public void Reconcile_DeleteMiddle_Reorder_Insert_ExistingSeqStable_NewFromNextSeq()
    {
        var existing = _adapter.ParseJson("OpcUa1", EXISTING_JSON)!;   // Seq 1..4、NextSeq 5
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "D2X", "ns=2;s=D2.X", "DO", 1, null, null, null, "D2", 4);   // 調到最前
            Row(ws, 4, "NEW", "ns=2;s=D3.N", null, 1, null, null, null, "D3", null); // 插入新列（無 Seq）
            Row(ws, 5, "D1T", "ns=2;s=D1.T", null, 1, "°C", 0, 100, "D1", 1);
            Row(ws, 6, "D1F-renamed", "ns=2;s=D1.F", "AO", 0.1, null, null, null, "D1", 2);
            // Seq 3（D2T）刪掉

            var uploaded = _adapter.ParseSheet(ws);
            _adapter.Reconcile(uploaded, existing);
            Assert.True(uploaded.IsValid, Errors(uploaded));
            Assert.Equal(new[] { 4, 5, 1, 2 }, Seqs(uploaded));
            Assert.Equal("6", uploaded.Header["NextSeq"]);

            var diff = SourceExcelDiffBuilder.Build(uploaded, existing, _adapter);
            Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
            Assert.False(diff.HasSidShift);
            Assert.Equal(1, diff.KeyedRemovedCount);
            Assert.Contains(diff.PointChanges, c => c.Change == "Removed" && c.Index == 3 && c.Name == "D2T");
            Assert.Contains(diff.PointChanges, c => c.Change == "Added" && c.Index == 5 && c.Name == "NEW");
            Assert.Contains(diff.PointChanges, c => c.Change == "Modified" && c.Index == 2 && c.Summary.Contains("Name: D1F → D1F-renamed"));
            Assert.DoesNotContain(diff.PointChanges, c => c.Index == 1 || c.Index == 4);   // 純換順序不算變更

            var root = Assert.IsType<JsonObject>(JsonNode.Parse(_adapter.ToJson(uploaded, "o.xlsx", EXISTING_JSON)));
            Assert.Equal(6, root["NextSeq"]!.GetValue<int>());
            Assert.Equal("secret", root["Password"]?.ToString());     // Excel 密碼空白 → 保留原密碼
            Assert.Equal("keep", root["CustomTop"]?.ToString());      // 未知頂層欄位保留
            Assert.Equal("o.xlsx", root["SourceWorkbook"]?.ToString());
            Assert.False(root.ContainsKey(SourceJsonStamp.WEB_EDITED_AT));
            var devices = Assert.IsType<JsonArray>(root["Devices"]);
            Assert.Equal(new[] { "D2", "D3", "D1" }, devices.Select(d => d!["Name"]!.ToString()));
            Assert.Equal(5, devices[1]!["Tags"]![0]!["Seq"]!.GetValue<int>());
        }
    }

    [Fact]
    public void Reconcile_NoSeq_MatchesByNodeId_OldXlsmNoRenumber()
    {
        var existing = _adapter.ParseJson("OpcUa1", EXISTING_JSON)!;
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "D2T", "ns=2;s=D2.T", null, 1, null, 0, 100);   // 無 Seq，TagName 對回 3
            Row(ws, 4, "D1T", "ns=2;s=D1.T", null, 1, "°C", 0, 100);   // 對回 1
            Row(ws, 5, "Brand new", "ns=2;s=D9.Z", null, 1);            // 對不到 → 5

            var uploaded = _adapter.ParseSheet(ws);
            _adapter.Reconcile(uploaded, existing);
            Assert.True(uploaded.IsValid, Errors(uploaded));
            Assert.Equal(new[] { 3, 1, 5 }, Seqs(uploaded));
            Assert.Equal(2, uploaded.Warnings.Count(w => w.Key == "srcxl.warn.seq_matched_by_nodeid"));
        }
    }

    [Fact]
    public void Reconcile_UnknownSeq_Error_AndNewServerAcceptsAnySeq()
    {
        var existing = _adapter.ParseJson("OpcUa1", EXISTING_JSON)!;
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "X", "ns=2;s=A.B", null, 1, null, null, null, null, 99);
            var uploaded = _adapter.ParseSheet(ws);
            _adapter.Reconcile(uploaded, existing);
            Assert.Contains(uploaded.Errors, e => e.Row == 3 && e.Key == "srcxl.err.seq_unknown");

            // 既有 JSON 不存在（新 Server）→ Seq 照單接受，空白者接在最大號之後
            Row(ws, 4, "Y", "ns=2;s=A.C", null, 1);
            var fresh = _adapter.ParseSheet(ws);
            _adapter.Reconcile(fresh, null);
            Assert.True(fresh.IsValid, Errors(fresh));
            Assert.Equal(new[] { 99, 100 }, Seqs(fresh));
            Assert.Equal("101", fresh.Header["NextSeq"]);
        }
    }

    [Fact]
    public void ToJson_WithoutReconcile_AssignsSequentially_AndPasswordFromExcel()
    {
        var (wb, ws) = NewSheet(szPassword: "newpw");
        using (wb)
        {
            Row(ws, 3, "A", "ns=2;s=A.1", null, 1, null, 0, 10);
            Row(ws, 4, "B", "ns=2;s=A.2", null, 1);
            var sheet = _adapter.ParseSheet(ws);

            var root = Assert.IsType<JsonObject>(JsonNode.Parse(_adapter.ToJson(sheet, null)));
            Assert.Equal("newpw", root["Password"]?.ToString());
            Assert.Equal("OpcUa1", root["Name"]?.ToString());
            Assert.Equal(3, root["NextSeq"]!.GetValue<int>());
            var tags = Assert.IsType<JsonArray>(root["Devices"]![0]!["Tags"]);
            Assert.Equal(1, tags[0]!["Seq"]!.GetValue<int>());
            Assert.Equal(2, tags[1]!["Seq"]!.GetValue<int>());
            Assert.Equal("1", tags[0]!["Ratio"]?.ToString());
            Assert.Equal(10f, tags[0]!["Max"]!.GetValue<float>());
            Assert.False(((JsonObject)tags[1]!).ContainsKey("Min"));   // 空白不輸出
            Assert.False(root.ContainsKey("SourceWorkbook"));
        }
    }

    [Fact]
    public void Export_DoesNotContainPassword()
    {
        var existing = _adapter.ParseJson("OpcUa1", EXISTING_JSON)!;
        using var wb = new XLWorkbook(new MemoryStream(SourceExcelTemplateWriter.Build(_adapter, new[] { existing }, isExport: true)));
        var ws = wb.Worksheets.First();
        Assert.Equal("Password", ws.Cell("E1").GetString());
        Assert.True(ws.Cell("F1").IsEmpty());
        Assert.DoesNotContain("secret", string.Join("|", ws.CellsUsed().Select(c => c.GetString())));
        Assert.Equal(1, ws.Cell(3, 9).GetValue<int>());   // Seq 欄
    }

    // ── Coordinator：不產刪除候選、不支援逐台刪除、提交寫檔 ──

    [Fact]
    public async Task Coordinator_OpcUa_NoDeleteCandidates_DeleteUnsupported_CommitKeepsPassword()
    {
        File.WriteAllText(PathOf("OpcUa1"), EXISTING_JSON, _adapter.NewFileEncoding);
        File.WriteAllText(PathOf("OpcUa2"), EXISTING_JSON.Replace("\"OpcUa1\"", "\"OpcUa2\"").Replace("\"CustomTop\": \"keep\",", "\"SourceWorkbook\": \"o.xlsx\","), _adapter.NewFileEncoding);

        var (wb, ws) = NewSheet(szEndpoint: "opc.tcp://192.168.1.10:4840");
        byte[] bytes;
        using (wb)
        {
            Row(ws, 3, "D1T", "ns=2;s=D1.T", null, 1, "°C", 0, 100, "D1", 1);
            Row(ws, 4, "NEW", "ns=2;s=D1.N", null, 1, null, null, null, "D1");
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            bytes = ms.ToArray();
        }

        var preview = await _coordinator.PreviewAsync(_adapter, new MemoryStream(bytes), "o.xlsx", "eng");
        Assert.True(preview.Success, preview.Message);
        Assert.False(preview.SupportsDelete);
        Assert.Empty(preview.DeleteCandidates);   // OpcUa2 同來源 o.xlsx 但 OPC UA 不產刪除候選
        Assert.Empty(preview.OtherExisting);
        var diff = Assert.Single(preview.Sheets);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.Equal(3, diff.KeyedRemovedCount);
        Assert.Contains(diff.PointChanges, c => c.Change == "Added" && c.Index == 5);

        var commit = await _coordinator.CommitAsync(_adapter, new SourceExcelCommitRequest { Token = preview.Token, ImportSheets = new() { "OpcUa1" } }, "eng");
        Assert.True(commit.Success, commit.Message);

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(PathOf("OpcUa1"))));
        Assert.Equal("secret", root["Password"]?.ToString());
        Assert.Equal(6, root["NextSeq"]!.GetValue<int>());
        Assert.Equal("o.xlsx", root["SourceWorkbook"]?.ToString());
        var tags = Assert.IsType<JsonArray>(root["Devices"]![0]!["Tags"]);
        Assert.Equal(new[] { 1, 5 }, tags.Select(t => t!["Seq"]!.GetValue<int>()));

        var del = await _coordinator.DeleteAsync(_adapter, "OpcUa2", "eng");
        Assert.False(del.Success);
        Assert.Contains("delete_not_supported", del.Message);
        Assert.True(File.Exists(PathOf("OpcUa2")));
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

    private sealed class StubLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments]
            => new(name, name + ":" + string.Join("|", arguments.Select(a => a?.ToString())));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}
