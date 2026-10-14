using ClosedXML.Excel;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Tests.SourceExcel;

/// <summary>
/// ModbusExcelAdapter 解析 / 驗證 / JSON 序列化測試（plan 2026-10-06 決策 3、8）。
/// 版面：B1=IP D1=Port F1=ModbusID H1=ConnectTimeout；第 3 列起 A~H = Name/Address/DataType/Ratio/Unit/Min/Max/Device。
/// </summary>
public class ModbusExcelAdapterTests
{
    private readonly ModbusExcelAdapter _adapter = new();

    /// <summary>建立標準版面工作表（含表頭與第 1 列設定），再交給 fill 填資料列</summary>
    /// <summary>傳入此哨兵表示「該儲存格留白」（null = 用預設值）</summary>
    private static readonly object Blank = new();

    private static void SetCell(IXLWorksheet ws, string szAddress, object? value, object szDefault)
    {
        if (ReferenceEquals(value, Blank)) return;
        ws.Cell(szAddress).Value = XLCellValue.FromObject(value ?? szDefault);
    }

    private static (XLWorkbook wb, IXLWorksheet ws) NewSheet(string szName = "Dev1", string szIp = "192.168.1.10", object? port = null,
        object? modbusId = null, object? timeout = null)
    {
        var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(szName);
        ws.Cell("A1").Value = "IP"; ws.Cell("B1").Value = szIp;
        ws.Cell("C1").Value = "Port"; SetCell(ws, "D1", port, 502);
        ws.Cell("E1").Value = "ModbusID"; SetCell(ws, "F1", modbusId, "1");
        ws.Cell("G1").Value = "ConnectTimeout"; SetCell(ws, "H1", timeout, 1000);
        ws.Cell("A2").Value = "名稱"; ws.Cell("B2").Value = "位址"; ws.Cell("C2").Value = "DataType";
        return (wb, ws);
    }

    private static void Row(IXLWorksheet ws, int r, object? name, object? address, object? type, object? ratio = null,
        object? unit = null, object? min = null, object? max = null, object? device = null)
    {
        void Set(int c, object? v) { if (v != null) ws.Cell(r, c).Value = XLCellValue.FromObject(v); }
        Set(1, name); Set(2, address); Set(3, type); Set(4, ratio); Set(5, unit); Set(6, min); Set(7, max); Set(8, device);
    }

    // ── 匯出版面 ──

    [Fact]
    public void WriteSheet_LongModbusIdList_ColumnWideEnough()
    {
        // F 欄預設寬 10（Min 欄），"1,2,3,4,5,6,7,8" 15 字會被 G1 標籤遮住看起來像截斷
        var sheet = new ParsedSourceSheet { SheetName = "Dev1" };
        sheet.Header["IP"] = "192.168.147.100"; sheet.Header["Port"] = "502";
        sheet.Header["ModbusId"] = "1,2,3,4,5,6,7,8"; sheet.Header["ConnectTimeout"] = "500";

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Dev1");
        _adapter.WriteSheet(ws, sheet);

        Assert.Equal(XLDataType.Text, ws.Cell("F1").Value.Type);
        Assert.Equal("1,2,3,4,5,6,7,8", ws.Cell("F1").GetText());
        Assert.True(ws.Column(6).Width >= "1,2,3,4,5,6,7,8".Length + 2, $"F 欄寬 {ws.Column(6).Width}");
        Assert.True(ws.Column(2).Width >= "192.168.147.100".Length + 2, $"B 欄寬 {ws.Column(2).Width}");

        // 資料欄（Min）仍不受短值影響：短 ModbusID 不會把欄縮窄
        var sheet2 = new ParsedSourceSheet { SheetName = "Dev2" };
        sheet2.Header["IP"] = "10.0.0.1"; sheet2.Header["ModbusId"] = "1";
        var ws2 = wb.Worksheets.Add("Dev2");
        _adapter.WriteSheet(ws2, sheet2);
        Assert.Equal(10, ws2.Column(6).Width, 1);
    }

    // ── 正常解析 ──

    [Fact]
    public void Parse_StandardLayout_HeaderAndPoints()
    {
        var (wb, ws) = NewSheet(modbusId: "1,2,3", timeout: 500);
        using (wb)
        {
            Row(ws, 3, "V", 30513, "SWAPPEDFP", 1, "V", 0, 300);
            Row(ws, 4, "kWh", 30001, "UINT32BE", 0.1, "kWh", 0, 1000000, "電表A");

            var sheet = _adapter.ParseSheet(ws);

            Assert.True(sheet.IsValid, string.Join("; ", sheet.Errors.Select(e => e.Key)));
            Assert.Equal("Dev1", sheet.SheetName);
            Assert.Equal("192.168.1.10", sheet.Header["IP"]);
            Assert.Equal("502", sheet.Header["Port"]);
            Assert.Equal("1,2,3", sheet.Header["ModbusId"]);
            Assert.Equal("500", sheet.Header["ConnectTimeout"]);
            Assert.Equal(2, sheet.Points.Count);

            // 數值儲存格 → 不帶小數的字串
            Assert.Equal("30513", sheet.Points[0].Get("Address"));
            Assert.Equal("1", sheet.Points[0].Get("Ratio"));
            Assert.Equal("0", sheet.Points[0].Get("Min"));
            Assert.Equal("300", sheet.Points[0].Get("Max"));
            Assert.Equal("", sheet.Points[0].Get("Device"));

            Assert.Equal("0.1", sheet.Points[1].Get("Ratio"));
            Assert.Equal("1000000", sheet.Points[1].Get("Max"));
            Assert.Equal("電表A", sheet.Points[1].Get("Device"));
        }
    }

    [Fact]
    public void Parse_BlankOptionalHeaderCells_UseDefaults()
    {
        // H1 空白 → 1000（巨集 Integer 錯誤修正）；D1 空白 → 502
        var (wb, ws) = NewSheet(port: Blank, timeout: Blank);
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "INTEGER");
            var sheet = _adapter.ParseSheet(ws);
            Assert.True(sheet.IsValid);
            Assert.Equal("502", sheet.Header["Port"]);
            Assert.Equal("1000", sheet.Header["ConnectTimeout"]);
        }
    }

    [Fact]
    public void Parse_TextAddressWithLeadingZeros_Preserved()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            ws.Cell(3, 2).Style.NumberFormat.Format = "@";
            Row(ws, 3, "Coil", "000001", "INTEGER");
            var sheet = _adapter.ParseSheet(ws);
            Assert.True(sheet.IsValid);
            Assert.Equal("000001", sheet.Points[0].Get("Address"));
        }
    }

    [Fact]
    public void Parse_BlankRatio_DefaultsToOne_And_DataTypeUpperCased()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "integer");
            var sheet = _adapter.ParseSheet(ws);
            Assert.True(sheet.IsValid);
            Assert.Equal("1", sheet.Points[0].Get("Ratio"));
            Assert.Equal("INTEGER", sheet.Points[0].Get("DataType"));
            Assert.Equal("", sheet.Points[0].Get("Min"));
        }
    }

    [Fact]
    public void Parse_OldXlsmLayout_NoDeviceColumn_DeviceEmpty()
    {
        // 舊 xlsm 只有 A~G，H 欄不存在
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "CH1MOA", 40001, "INTEGER", 1, null, 0, 100);
            var sheet = _adapter.ParseSheet(ws);
            Assert.True(sheet.IsValid);
            Assert.Equal("", sheet.Points[0].Get("Device"));
        }
    }

    [Fact]
    public void Parse_LabelsAtNonStandardPositions_FoundByScanning()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Dev1");
        ws.Cell("C1").Value = "IP"; ws.Cell("D1").Value = "10.0.0.5";
        ws.Cell("E1").Value = "Port"; ws.Cell("F1").Value = 1502;
        ws.Cell("G1").Value = "ModbusID"; ws.Cell("H1").Value = 7;
        Row(ws, 3, "P1", 40001, "INTEGER");

        var sheet = _adapter.ParseSheet(ws);
        Assert.True(sheet.IsValid);
        Assert.Equal("10.0.0.5", sheet.Header["IP"]);
        Assert.Equal("1502", sheet.Header["Port"]);
        Assert.Equal("7", sheet.Header["ModbusId"]);
    }

    // ── 空白列 ──

    [Fact]
    public void Parse_BlankNameRowInMiddle_SkippedWithWarning_RowsAfterLastNameIgnored()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "INTEGER");
            Row(ws, 4, null, 40002, "INTEGER");      // 名稱空白 → 略過 + 提示
            Row(ws, 5, "P3", 40003, "INTEGER");
            Row(ws, 6, null, null, null, null, null, 0, 100);   // 最後一筆名稱之後 → 不理

            var sheet = _adapter.ParseSheet(ws);
            Assert.True(sheet.IsValid);
            Assert.Equal(2, sheet.Points.Count);
            Assert.Equal(new[] { "P1", "P3" }, sheet.Points.Select(p => p.Name));
            var warn = Assert.Single(sheet.Warnings);
            Assert.Equal("srcxl.warn.blank_row_skipped", warn.Key);
            Assert.Equal(4, warn.Row);
        }
    }

    // ── 驗證錯誤：含工作表列/欄 ──

    [Fact]
    public void Parse_InvalidAddress_ErrorAtColumnB()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "P1", 50001, "INTEGER");
            var sheet = _adapter.ParseSheet(ws);
            var err = Assert.Single(sheet.Errors);
            Assert.Equal("srcxl.err.address_invalid", err.Key);
            Assert.Equal(3, err.Row);
            Assert.Equal("B", err.Column);
        }
    }

    [Fact]
    public void Parse_UnsupportedDataType_ErrorAtColumnC()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "STRING");
            var sheet = _adapter.ParseSheet(ws);
            var err = Assert.Single(sheet.Errors);
            Assert.Equal("srcxl.err.datatype_unsupported", err.Key);
            Assert.Equal("C", err.Column);
        }
    }

    [Fact]
    public void Parse_BitTypeOnCoilAddress_Error()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "P1", 1, "BIT3");          // Coil 配 BIT → Engine 會跳過該點 → 必須在匯入端擋下
            Row(ws, 4, "P2", 40001, "BIT3");      // Holding 配 BIT → OK
            var sheet = _adapter.ParseSheet(ws);
            var err = Assert.Single(sheet.Errors);
            Assert.Equal("srcxl.err.bit_type_needs_register", err.Key);
            Assert.Equal(3, err.Row);
        }
    }

    [Fact]
    public void Parse_NonNumericMin_ErrorAtColumnF()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "INTEGER", 1, "", "abc", 100);
            var sheet = _adapter.ParseSheet(ws);
            var err = Assert.Single(sheet.Errors);
            Assert.Equal("srcxl.err.numeric_required", err.Key);
            Assert.Equal("F", err.Column);
            Assert.Equal("Min", err.Args[0]);
        }
    }

    [Fact]
    public void Parse_MissingIp_HeaderError()
    {
        var (wb, ws) = NewSheet(szIp: "");
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "INTEGER");
            var sheet = _adapter.ParseSheet(ws);
            var err = Assert.Single(sheet.Errors);
            Assert.Equal("srcxl.err.header_ip_required", err.Key);
            Assert.Equal(1, err.Row);
            Assert.Equal("B", err.Column);
        }
    }

    [Fact]
    public void Parse_ModbusIdOutOfRange_HeaderError()
    {
        var (wb, ws) = NewSheet(modbusId: "1,300");
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "INTEGER");
            var sheet = _adapter.ParseSheet(ws);
            Assert.Contains(sheet.Errors, e => e.Key == "srcxl.err.header_modbusid_invalid");
        }
    }

    [Fact]
    public void Parse_NoPoints_Error()
    {
        var (wb, ws) = NewSheet();
        using (wb)
        {
            var sheet = _adapter.ParseSheet(ws);
            Assert.Contains(sheet.Errors, e => e.Key == "srcxl.err.no_points");
        }
    }

    [Theory]
    [InlineData("1, 2,3", "1,2,3", true)]
    [InlineData("1", "1", true)]
    [InlineData("0,255", "0,255", true)]
    [InlineData("1,256", "1,256", false)]
    [InlineData("a", "a", false)]
    [InlineData("", "", true)]
    public void NormalizeModbusIdList_Cases(string szInput, string szExpected, bool isExpectedValid)
    {
        var sz = ModbusExcelAdapter.NormalizeModbusIdList(szInput, out var isValid);
        Assert.Equal(szExpected, sz);
        Assert.Equal(isExpectedValid, isValid);
    }

    // ── JSON ──

    [Fact]
    public void ToJson_StringTypedFields_EscapesQuotesAndBackslash()
    {
        var sheet = new ParsedSourceSheet { SheetName = "Dev1" };
        sheet.Header["IP"] = "192.168.1.10"; sheet.Header["Port"] = "502";
        sheet.Header["ModbusId"] = "1"; sheet.Header["ConnectTimeout"] = "1000";
        var p = new ParsedSourcePoint { RowNumber = 3 };
        p.Fields["Name"] = "溫度 \"A\" \\ 1"; p.Fields["Address"] = "40001"; p.Fields["DataType"] = "INTEGER";
        p.Fields["Ratio"] = "0.1"; p.Fields["Unit"] = "°C"; p.Fields["Min"] = "0"; p.Fields["Max"] = "100"; p.Fields["Device"] = "";
        sheet.Points.Add(p);

        var szJson = _adapter.ToJson(sheet, "盤A.xlsx");

        // 合法 JSON 且欄位型別與巨集一致（Address/Ratio/Min/Max 為字串）
        using var doc = System.Text.Json.JsonDocument.Parse(szJson);
        var root = doc.RootElement;
        Assert.Equal("192.168.1.10", root.GetProperty("IP").GetString());
        Assert.Equal(502, root.GetProperty("Port").GetInt32());
        Assert.Equal("1", root.GetProperty("ModbusId").GetString());
        Assert.Equal(1000, root.GetProperty("ConnectTimeout").GetInt32());
        Assert.Equal("盤A.xlsx", root.GetProperty("SourceWorkbook").GetString());
        var tag = root.GetProperty("Tags")[0];
        Assert.Equal("溫度 \"A\" \\ 1", tag.GetProperty("Name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.String, tag.GetProperty("Address").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.String, tag.GetProperty("Ratio").ValueKind);
        Assert.Equal("0.1", tag.GetProperty("Ratio").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.String, tag.GetProperty("Min").ValueKind);
        Assert.Equal("", tag.GetProperty("Device").GetString());
    }

    [Fact]
    public void ParseJson_ExistingMacroFormat_NormalizesAndReadsSourceWorkbook()
    {
        const string szJson = """
        {
          "IP": "192.168.147.1",
          "Port": 502,
          "ModbusId": "1, 2",
          "ConnectTimeout": 500,
          "SourceWorkbook": "盤A.xlsx",
          "Tags": [
            { "Name": "V", "Address": "30513", "DataType": "swappedfp", "Ratio": "1.0", "Unit": "", "Min": "0", "Max": "300" },
            { "Name": "I", "Address": "30515", "DataType": "SWAPPEDFP", "Ratio": "1", "Unit": "", "Min": "", "Max": "" }
          ]
        }
        """;

        var sheet = _adapter.ParseJson("Modbus", szJson);
        Assert.NotNull(sheet);
        Assert.Equal("盤A.xlsx", sheet!.SourceWorkbook);
        Assert.Equal("1,2", sheet.Header["ModbusId"]);
        Assert.Equal("500", sheet.Header["ConnectTimeout"]);
        Assert.Equal(2, sheet.Points.Count);
        Assert.Equal("SWAPPEDFP", sheet.Points[0].Get("DataType"));
        Assert.Equal("1", sheet.Points[0].Get("Ratio"));           // "1.0" → "1"
        Assert.Equal("", sheet.Points[1].Get("Min"));
        Assert.Equal("", sheet.Points[0].Get("Device"));           // 舊檔無 Device
    }

    [Fact]
    public void ParseJson_Invalid_ReturnsNull()
    {
        Assert.Null(_adapter.ParseJson("X", "{ not json"));
        Assert.Null(_adapter.ParseJson("X", "[1,2]"));
    }

    [Fact]
    public void ToJson_ThenParseJson_RoundTripsFields()
    {
        var (wb, ws) = NewSheet(modbusId: "3", timeout: 800);
        using (wb)
        {
            Row(ws, 3, "P1", 40001, "INTEGER", 1, "A", 0, 100, "泵A");
            Row(ws, 4, "P2", 30002, "BIT5", 2.5, "", null, null);
            var parsed = _adapter.ParseSheet(ws);
            Assert.True(parsed.IsValid);

            var back = _adapter.ParseJson("Dev1", _adapter.ToJson(parsed, null));
            Assert.NotNull(back);
            Assert.Equal(parsed.Header, back!.Header);
            Assert.Equal(parsed.Points.Count, back.Points.Count);
            for (var i = 0; i < parsed.Points.Count; i++)
                Assert.Equal(parsed.Points[i].Fields, back.Points[i].Fields);
        }
    }
}
