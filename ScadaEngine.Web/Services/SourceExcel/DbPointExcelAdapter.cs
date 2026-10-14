using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// DB 來源 Coordinator Excel ⇄ JSON adapter。版面沿用舊 `DB通訊檔案產生工具.xlsm`（舊檔可直接上傳）：
///
/// - 第 1 列：A1~D1 欄位標題（名稱/單位/最小值/最大值）；F1=ConnectTimeout G1=1000、H1=PollingInterval I1=1000、
///   J1=MonitorEnabled K1=TRUE（空白預設 1000 / 1000 / TRUE，與巨集相同）→ 以標籤掃描第 1 列、找不到退回 G1/I1/K1
/// - 第 2 列起：A~D = Name / Unit / Min / Max（Min/Max 空白預設 0/100）
/// - 上限 100 點（巨集靜默截斷，這裡改成明確報錯）
///
/// JSON 輸出與 Web 熱編輯（DbPointConfigFileService）一致：Min/Max 數字、新檔 UTF-8 BOM；
/// 頂層 Name = 工作表名稱（Engine 載入時以 Name 為 Coordinator 名稱，檔名為 fallback）。
/// </summary>
public class DbPointExcelAdapter : ISourceExcelAdapter
{
    private const int COL_NAME = 1, COL_UNIT = 2, COL_MIN = 3, COL_MAX = 4;
    private const int DATA_FIRST_ROW = 2;
    private const int MAX_POINTS = 100;
    private const int MAX_NAME_LENGTH = 100;
    private const int MAX_UNIT_LENGTH = 50;
    private const int TEMPLATE_VALIDATION_ROWS = 100;

    private static readonly string[] _headerFields = { "PollingInterval", "ConnectTimeout", "MonitorEnabled" };
    private static readonly string[] _pointFields = { "Name", "Unit", "Min", "Max" };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SourceExcelKind Kind => SourceExcelKind.DbPoint;
    public string ConfigSection => SourceConfigFileIo.DBPOINT_SECTION;
    public string TemplateFileName => "DB來源範本.xlsx";
    public string ExportFileNamePrefix => "DB來源設定";
    public string DefaultSheetName => "DB1";
    public IReadOnlyList<string> HeaderFields => _headerFields;
    public IReadOnlyList<string> PointFields => _pointFields;

    /// <summary>與 DbPointConfigFileService 寫回一致：UTF-8 with BOM</summary>
    public Encoding NewFileEncoding => Encoding.UTF8;

    // ───────────────────────────── Excel → 中介 ─────────────────────────────

    public ParsedSourceSheet ParseSheet(IXLWorksheet ws)
    {
        var sheet = new ParsedSourceSheet { SheetName = ws.Name.Trim() };

        if (!SourceConfigFileIo.IsSafeName(sheet.SheetName))
            sheet.Errors.Add(SheetIssue.General("srcxl.err.sheet_name_invalid"));

        ParseHeader(ws, sheet);
        ParsePoints(ws, sheet);

        if (sheet.Points.Count == 0)
            sheet.Errors.Add(SheetIssue.General("srcxl.err.no_points"));

        return sheet;
    }

    private static void ParseHeader(IXLWorksheet ws, ParsedSourceSheet sheet)
    {
        var szTimeout = SourceExcelCells.FindRow1Value(ws, "ConnectTimeout", "Timeout") ?? SourceExcelCells.ReadString(ws, 1, 7);
        var szPolling = SourceExcelCells.FindRow1Value(ws, "PollingInterval", "Polling") ?? SourceExcelCells.ReadString(ws, 1, 9);
        var szMonitor = SourceExcelCells.FindRow1Value(ws, "MonitorEnabled", "Monitor") ?? SourceExcelCells.ReadString(ws, 1, 11);

        if (szTimeout.Length == 0) szTimeout = "1000";
        if (!SourceExcelCells.TryParsePositiveInt(szTimeout, out _))
            sheet.Errors.Add(SheetIssue.At(1, "G", "srcxl.err.header_timeout_invalid"));
        sheet.Header["ConnectTimeout"] = SourceExcelCells.NormalizeNumber(szTimeout);

        if (szPolling.Length == 0) szPolling = "1000";
        if (!SourceExcelCells.TryParsePositiveInt(szPolling, out _))
            sheet.Errors.Add(SheetIssue.At(1, "I", "srcxl.err.header_polling_invalid"));
        sheet.Header["PollingInterval"] = SourceExcelCells.NormalizeNumber(szPolling);

        var szMonitorNorm = SourceExcelCells.NormalizeBool(szMonitor, out var isMonitorValid);
        if (!isMonitorValid)
            sheet.Errors.Add(SheetIssue.At(1, "K", "srcxl.err.header_monitor_invalid"));
        sheet.Header["MonitorEnabled"] = isMonitorValid ? szMonitorNorm : "TRUE";
    }

    private static void ParsePoints(IXLWorksheet ws, ParsedSourceSheet sheet)
    {
        var nLastRow = SourceExcelCells.LastDataRow(ws, COL_NAME, DATA_FIRST_ROW);

        for (var r = DATA_FIRST_ROW; r <= nLastRow; r++)
        {
            var szName = SourceExcelCells.ReadString(ws, r, COL_NAME);
            if (szName.Length == 0)
            {
                sheet.Warnings.Add(SheetIssue.At(r, "A", "srcxl.warn.blank_row_skipped"));
                continue;
            }

            if (sheet.Points.Count >= MAX_POINTS)
            {
                sheet.Errors.Add(SheetIssue.At(r, "A", "srcxl.err.too_many_points", MAX_POINTS));
                break;
            }

            var point = new ParsedSourcePoint { RowNumber = r };

            if (szName.Length > MAX_NAME_LENGTH)
                sheet.Errors.Add(SheetIssue.At(r, "A", "srcxl.err.name_too_long", MAX_NAME_LENGTH));
            point.Fields["Name"] = szName;

            var szUnit = SourceExcelCells.ReadString(ws, r, COL_UNIT);
            if (szUnit.Length > MAX_UNIT_LENGTH)
                sheet.Errors.Add(SheetIssue.At(r, "B", "srcxl.err.unit_too_long", MAX_UNIT_LENGTH));
            point.Fields["Unit"] = szUnit;

            // Min / Max：空白預設 0 / 100（與巨集相同）；有填必須是數字
            var szMin = SourceExcelCells.ReadString(ws, r, COL_MIN);
            if (szMin.Length == 0) szMin = "0";
            if (!SourceExcelCells.TryParseNumber(szMin, out _))
                sheet.Errors.Add(SheetIssue.At(r, "C", "srcxl.err.numeric_required", "Min"));
            point.Fields["Min"] = SourceExcelCells.NormalizeNumber(szMin);

            var szMax = SourceExcelCells.ReadString(ws, r, COL_MAX);
            if (szMax.Length == 0) szMax = "100";
            if (!SourceExcelCells.TryParseNumber(szMax, out _))
                sheet.Errors.Add(SheetIssue.At(r, "D", "srcxl.err.numeric_required", "Max"));
            point.Fields["Max"] = SourceExcelCells.NormalizeNumber(szMax);

            sheet.Points.Add(point);
        }
    }

    // ───────────────────────────── JSON ⇄ 中介 ─────────────────────────────

    public ParsedSourceSheet? ParseJson(string szName, string szJson)
    {
        try
        {
            if (JsonNode.Parse(szJson) is not JsonObject root) return null;

            var sheet = new ParsedSourceSheet { SheetName = szName };
            sheet.Header["PollingInterval"] = SourceExcelCells.NormalizeNumber(root["PollingInterval"]?.ToString() ?? "1000");
            sheet.Header["ConnectTimeout"] = SourceExcelCells.NormalizeNumber(root["ConnectTimeout"]?.ToString() ?? "1000");
            sheet.Header["MonitorEnabled"] = SourceExcelCells.NormalizeBool(root["MonitorEnabled"]?.ToString(), out var isValid) is var szMon && isValid ? szMon : "TRUE";
            sheet.SourceWorkbook = root[SourceJsonStamp.SOURCE_WORKBOOK]?.ToString();
            sheet.WebEditedAt = SourceJsonStamp.ReadWebEdited(root);

            if (root["Points"] is JsonArray points)
            {
                var nIndex = 0;
                foreach (var pt in points)
                {
                    nIndex++;
                    if (pt is not JsonObject p) continue;
                    var point = new ParsedSourcePoint { RowNumber = nIndex };
                    point.Fields["Name"] = (p["Name"]?.ToString() ?? string.Empty).Trim();
                    point.Fields["Unit"] = (p["Unit"]?.ToString() ?? string.Empty).Trim();
                    var szMin = p["Min"]?.ToString();
                    point.Fields["Min"] = SourceExcelCells.NormalizeNumber(string.IsNullOrWhiteSpace(szMin) ? "0" : szMin);
                    var szMax = p["Max"]?.ToString();
                    point.Fields["Max"] = SourceExcelCells.NormalizeNumber(string.IsNullOrWhiteSpace(szMax) ? "100" : szMax);
                    sheet.Points.Add(point);
                }
            }

            return sheet;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson(ParsedSourceSheet sheet, string? szSourceWorkbook)
    {
        var root = new JsonObject
        {
            ["Name"] = sheet.SheetName,
            ["PollingInterval"] = int.TryParse(sheet.Header.GetValueOrDefault("PollingInterval"), out var nPolling) ? nPolling : 1000,
            ["ConnectTimeout"] = int.TryParse(sheet.Header.GetValueOrDefault("ConnectTimeout"), out var nTimeout) ? nTimeout : 1000,
            ["MonitorEnabled"] = !string.Equals(sheet.Header.GetValueOrDefault("MonitorEnabled"), "FALSE", StringComparison.OrdinalIgnoreCase),
        };
        if (!string.IsNullOrWhiteSpace(szSourceWorkbook))
            root["SourceWorkbook"] = szSourceWorkbook;

        var points = new JsonArray();
        foreach (var p in sheet.Points)
        {
            points.Add(new JsonObject
            {
                ["Name"] = p.Get("Name"),
                ["Unit"] = p.Get("Unit"),
                ["Min"] = SourceExcelCells.TryParseNumber(p.Get("Min"), out var dMin) ? dMin : 0d,
                ["Max"] = SourceExcelCells.TryParseNumber(p.Get("Max"), out var dMax) ? dMax : 100d,
            });
        }
        root["Points"] = points;

        return root.ToJsonString(_jsonOptions);
    }

    // ───────────────────────────── 範本 / 匯出版面 ─────────────────────────────

    public void WriteSheet(IXLWorksheet ws, ParsedSourceSheet? sheet)
    {
        // 第 1 列：欄位標題（A~D）+ 設定對（F/G、H/I、J/K，與巨集讀取位置 G1/I1/K1 一致）
        var aHeaders = new[] { "名稱 Name", "單位 Unit", "最小值 Min", "最大值 Max" };
        for (var i = 0; i < aHeaders.Length; i++)
            ws.Cell(1, i + 1).Value = aHeaders[i];
        SourceExcelCells.StyleHeader(ws.Range(1, 1, 1, aHeaders.Length));

        ws.Cell(1, 6).Value = "ConnectTimeout";
        ws.Cell(1, 7).Value = int.TryParse(sheet?.Header.GetValueOrDefault("ConnectTimeout"), out var nTimeout) ? nTimeout : 1000;
        ws.Cell(1, 8).Value = "PollingInterval";
        ws.Cell(1, 9).Value = int.TryParse(sheet?.Header.GetValueOrDefault("PollingInterval"), out var nPolling) ? nPolling : 1000;
        ws.Cell(1, 10).Value = "MonitorEnabled";
        ws.Cell(1, 11).Value = !string.Equals(sheet?.Header.GetValueOrDefault("MonitorEnabled"), "FALSE", StringComparison.OrdinalIgnoreCase);
        foreach (var c in new[] { 6, 8, 10 }) SourceExcelCells.StyleLabel(ws.Cell(1, c));

        var dvMonitor = ws.Cell(1, 11).CreateDataValidation();
        dvMonitor.List("\"TRUE,FALSE\"", true);

        // 資料列
        var nRow = DATA_FIRST_ROW;
        if (sheet != null)
        {
            foreach (var p in sheet.Points)
            {
                ws.Cell(nRow, COL_NAME).Value = p.Get("Name");
                ws.Cell(nRow, COL_UNIT).Value = p.Get("Unit");
                ws.Cell(nRow, COL_MIN).Value = SourceExcelCells.TryParseNumber(p.Get("Min"), out var dMin) ? dMin : 0d;
                ws.Cell(nRow, COL_MAX).Value = SourceExcelCells.TryParseNumber(p.Get("Max"), out var dMax) ? dMax : 100d;
                nRow++;
            }
        }

        var nValidationLastRow = DATA_FIRST_ROW + TEMPLATE_VALIDATION_ROWS - 1;
        var dvMinMax = ws.Range(DATA_FIRST_ROW, COL_MIN, nValidationLastRow, COL_MAX).CreateDataValidation();
        dvMinMax.Decimal.Between(-1e12, 1e12);

        ws.SheetView.FreezeRows(1);
        ws.Column(COL_NAME).Width = 28;
        ws.Column(COL_UNIT).Width = 12;
        ws.Column(COL_MIN).Width = 12;
        ws.Column(COL_MAX).Width = 12;
        ws.Column(6).Width = 16;
        ws.Column(8).Width = 16;
        ws.Column(10).Width = 16;
    }
}
