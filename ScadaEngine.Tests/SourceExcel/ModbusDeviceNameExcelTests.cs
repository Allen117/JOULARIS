using System.Text.Json.Nodes;
using ClosedXML.Excel;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Tests.SourceExcel;

/// <summary>
/// Modbus 子站名稱（DeviceName）進 Excel / JSON（plan 2026-10-29 範圍 C）：
/// I1=DeviceName 標籤、J1=依 F1 站號順序的逗號串；舊 Excel（無標籤）/ 舊 JSON（無欄位）不清空既有值；
/// 匯入重寫 JSON 保留 Excel 不帶的進階欄位（RetryCount / NoResponseReconnectRounds / RenamedFrom）。
/// </summary>
public class ModbusDeviceNameExcelTests
{
    private readonly ModbusExcelAdapter _adapter = new();

    private static IXLWorksheet NewSheet(XLWorkbook wb, string szModbusId, string? szDeviceName)
    {
        var ws = wb.Worksheets.Add("GW1");
        ws.Cell("A1").Value = "IP"; ws.Cell("B1").Value = "10.0.0.1";
        ws.Cell("C1").Value = "Port"; ws.Cell("D1").Value = 502;
        ws.Cell("E1").Value = "ModbusID"; ws.Cell("F1").Value = szModbusId;
        ws.Cell("G1").Value = "ConnectTimeout"; ws.Cell("H1").Value = 1000;
        if (szDeviceName != null)
        {
            ws.Cell("I1").Value = "DeviceName"; ws.Cell("J1").Value = szDeviceName;
        }
        ws.Cell("A3").Value = "T1"; ws.Cell("B3").Value = "40001"; ws.Cell("C3").Value = "INTEGER";
        return ws;
    }

    private static ParsedSourceSheet Sheet(string szModbusId, string? szDeviceName)
    {
        var s = new ParsedSourceSheet { SheetName = "GW1" };
        s.Header["IP"] = "10.0.0.1"; s.Header["Port"] = "502"; s.Header["ModbusId"] = szModbusId; s.Header["ConnectTimeout"] = "1000";
        if (szDeviceName != null) s.Header["DeviceName"] = szDeviceName;
        var p = new ParsedSourcePoint();
        p.Fields["Name"] = "T1"; p.Fields["Address"] = "40001"; p.Fields["DataType"] = "INTEGER"; p.Fields["Ratio"] = "1";
        s.Points.Add(p);
        return s;
    }

    [Theory]
    [InlineData("冰水主機1,冰水主機2,冷卻水塔", "冰水主機1,冰水主機2,冷卻水塔")]
    [InlineData(" 冰水主機1 ,, 冷卻水塔 ", "冰水主機1,,冷卻水塔")]
    [InlineData("冰水主機1", "冰水主機1,,")]             // 不足補空白，對齊站號
    [InlineData("冰水主機1，冰水主機2", "冰水主機1,冰水主機2,")] // 全形逗號視同半形
    [InlineData("", "")]                                  // 整格留白 = 顯示站號
    [InlineData("A,B,C,,", "A,B,C")]                       // 尾端多餘逗號不算名稱
    public void ParseSheet_DeviceName_依站號正規化(string szCell, string szExpected)
    {
        using var wb = new XLWorkbook();
        var sheet = _adapter.ParseSheet(NewSheet(wb, "1,2,5", szCell));

        Assert.True(sheet.IsValid, string.Join(";", sheet.Errors.Select(e => e.Key)));
        Assert.Equal(szExpected, sheet.Header["DeviceName"]);
    }

    [Fact]
    public void ParseSheet_名稱多於站號_錯誤()
    {
        using var wb = new XLWorkbook();
        var sheet = _adapter.ParseSheet(NewSheet(wb, "1,2", "A,B,C"));

        var err = Assert.Single(sheet.Errors);
        Assert.Equal("srcxl.err.header_devicename_too_many", err.Key);
        Assert.Equal("J", err.Column);
    }

    [Fact]
    public void ParseSheet_超過DB欄位長度_錯誤()
    {
        using var wb = new XLWorkbook();
        var sheet = _adapter.ParseSheet(NewSheet(wb, "1", new string('名', 101)));

        Assert.Contains(sheet.Errors, e => e.Key == "srcxl.err.header_devicename_too_long");
    }

    [Fact]
    public void 舊版Excel無標籤_不放Header_Reconcile沿用既有值()
    {
        using var wb = new XLWorkbook();
        var sheet = _adapter.ParseSheet(NewSheet(wb, "1,2", null));
        Assert.False(sheet.Header.ContainsKey("DeviceName"));

        _adapter.Reconcile(sheet, Sheet("1,2", "甲,乙"));

        Assert.Equal("甲,乙", sheet.Header["DeviceName"]);
        var diff = SourceExcelDiffBuilder.Build(sheet, Sheet("1,2", "甲,乙"), _adapter);
        Assert.Equal(SheetDiffKind.Unchanged, diff.Kind);
    }

    [Fact]
    public void JSON往返_有欄位寫出_舊檔無欄位不放Header()
    {
        var szJson = _adapter.ToJson(Sheet("1,2", "甲,乙"), "盤A.xlsx");
        Assert.Equal("甲,乙", JsonNode.Parse(szJson)!["DeviceName"]!.ToString());
        Assert.Equal("甲,乙", _adapter.ParseJson("GW1", szJson)!.Header["DeviceName"]);

        var szOld = _adapter.ToJson(Sheet("1,2", null), null);
        Assert.Null(JsonNode.Parse(szOld)!["DeviceName"]);
        Assert.False(_adapter.ParseJson("GW1", szOld)!.Header.ContainsKey("DeviceName"));
    }

    [Fact]
    public void 匯入重寫JSON_保留進階欄位與改名紀錄()
    {
        var existing = JsonNode.Parse(_adapter.ToJson(Sheet("1", null), null))!.AsObject();
        existing["RetryCount"] = 3;
        existing["NoResponseReconnectRounds"] = 8;
        SourceJsonStamp.AppendRenamedFrom(existing, "舊名");

        var szJson = _adapter.ToJson(Sheet("1", "甲"), "盤A.xlsx", existing.ToJsonString());
        var root = JsonNode.Parse(szJson)!.AsObject();

        Assert.Equal(3, root["RetryCount"]!.GetValue<int>());
        Assert.Equal(8, root["NoResponseReconnectRounds"]!.GetValue<int>());
        Assert.Equal(new[] { "舊名" }, SourceJsonStamp.ReadRenamedFrom(root));
        Assert.Equal("甲", root["DeviceName"]!.ToString());
    }

    [Fact]
    public void WriteSheet_第1列I1標籤J1值_可再解析()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("GW1");
        _adapter.WriteSheet(ws, Sheet("1,2,5", "冰水主機1,,冷卻水塔"));

        Assert.Equal("DeviceName", ws.Cell("I1").GetText());
        Assert.Equal("冰水主機1,,冷卻水塔", ws.Cell("J1").GetText());
        Assert.Equal("冰水主機1,,冷卻水塔", _adapter.ParseSheet(ws).Header["DeviceName"]);
    }

    [Fact]
    public void 預覽站號對應_以站號比對舊名_標示調換()
    {
        // 名稱數量相同但兩站調換 — 數量檢核抓不到，靠逐站對應顯示
        var uploaded = Sheet("1,2", "乙,甲");
        var diff = SourceExcelDiffBuilder.Build(uploaded, Sheet("1,2", "甲,乙"), _adapter);

        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.NotNull(diff.StationNames);
        Assert.Collection(diff.StationNames!,
            n => { Assert.Equal("1", n.ModbusId); Assert.Equal("乙", n.Name); Assert.Equal("甲", n.OldName); Assert.True(n.IsChanged); },
            n => { Assert.Equal("2", n.ModbusId); Assert.Equal("甲", n.Name); Assert.Equal("乙", n.OldName); Assert.True(n.IsChanged); });
    }

    [Fact]
    public void 預覽站號對應_單站號無名稱不顯示()
    {
        Assert.Null(SourceExcelDiffBuilder.Build(Sheet("1", ""), Sheet("1", null), _adapter).StationNames);
    }

    [Fact]
    public void 匯出DB補值_只補JSON沒有的欄位()
    {
        var legacy = Sheet("1,2", null);
        ModbusExcelAdapter.ApplyDeviceNameFallback(legacy, "DB甲,DB乙");
        Assert.Equal("DB甲,DB乙", legacy.Header["DeviceName"]);

        var cleared = Sheet("1,2", "");
        ModbusExcelAdapter.ApplyDeviceNameFallback(cleared, "DB甲,DB乙");
        Assert.Equal("", cleared.Header["DeviceName"]); // JSON 明確清空 → 不被 DB 舊值蓋回
    }
}
