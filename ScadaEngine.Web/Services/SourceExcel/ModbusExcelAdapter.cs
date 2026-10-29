using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// Modbus 設備 Excel ⇄ JSON adapter。版面沿用舊 `Modbus通訊檔案產生工具.xlsm`（舊檔可直接上傳，巨集忽略）：
///
/// - 第 1 列：A1=IP 標籤 B1=IP、C1=Port D1=502、E1=ModbusID F1="1,2,3"、G1=ConnectTimeout H1=1000（空白預設 1000，修正巨集的 Integer 錯誤）、
///   I1=DeviceName J1="冰水主機1,冰水主機2,冷卻水塔"（子設備名稱，依 F1 站號順序對應；可整格留白或部分留空）
///   → 實際以「標籤文字」掃描第 1 列、取右側儲存格，標籤找不到才退回固定位置
/// - 第 2 列：欄位標題
/// - 第 3 列起：A~G = Name / Address / DataType / Ratio / Unit / Min / Max，**H = Device（新增，可留白）**
/// - I 欄：DataType 參考清單（範本改為下拉驗證來源）
///
/// JSON 輸出與巨集 / Web 熱編輯一致：Address / Ratio / Min / Max 皆字串、Device 一律輸出；
/// 新檔 UTF-16 LE BOM（既有檔沿用原編碼）。驗證規則與 Engine 載入一致（位址格式、DataType 白名單、BIT 型別只能配暫存器），
/// 讓 Engine 不會在載入時跳過點位而造成 SID 位移。
/// </summary>
public class ModbusExcelAdapter : ISourceExcelAdapter
{
    private const int COL_NAME = 1, COL_ADDRESS = 2, COL_DATATYPE = 3, COL_RATIO = 4,
                      COL_UNIT = 5, COL_MIN = 6, COL_MAX = 7, COL_DEVICE = 8, COL_TYPELIST = 9;
    private const int DATA_FIRST_ROW = 3;
    private const int TEMPLATE_VALIDATION_ROWS = 1000;

    private const int COL_DEVICENAME_LABEL = 9, COL_DEVICENAME_VALUE = 10;

    /// <summary>ModbusCoordinator.DeviceName 欄位長度（nvarchar(100)）</summary>
    public const int DEVICE_NAME_MAX_LENGTH = 100;

    /// <summary>JSON 頂層欄位：子站名稱逗號串（順序對應 ModbusId；選填，舊檔沒有 → Engine 不動 DB）</summary>
    public const string DEVICE_NAME_FIELD = "DeviceName";

    /// <summary>
    /// Excel 不帶、但匯入重寫 JSON 時要從既有檔保留的頂層欄位：
    /// 進階通訊參數（現場手改）與網頁改名紀錄（舊 Excel 重匯保護用）
    /// </summary>
    private static readonly string[] _preservedJsonFields = { "RetryCount", "NoResponseReconnectRounds", "UnreachableProbeIntervalMs", SourceJsonStamp.RENAMED_FROM };

    private static readonly string[] _headerFields = { "IP", "Port", "ModbusId", "ConnectTimeout", DEVICE_NAME_FIELD };
    private static readonly string[] _pointFields = { "Name", "Address", "DataType", "Ratio", "Unit", "Min", "Max", "Device" };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SourceExcelKind Kind => SourceExcelKind.Modbus;
    public string ConfigSection => SourceConfigFileIo.MODBUS_SECTION;
    public string TemplateFileName => "Modbus範本.xlsx";
    public string ExportFileNamePrefix => "Modbus設定";
    public string DefaultSheetName => "Device1";
    public IReadOnlyList<string> HeaderFields => _headerFields;
    public IReadOnlyList<string> PointFields => _pointFields;

    /// <summary>與巨集產出一致：UTF-16 LE with BOM</summary>
    public Encoding NewFileEncoding => new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

    // ───────────────────────────── Excel → 中介 ─────────────────────────────

    public ParsedSourceSheet ParseSheet(IXLWorksheet ws)
    {
        // 名稱原樣保留（不 Trim）：工作表名 = JSON 檔名 = Engine 設備名，既有「尾巴帶空白」的設備才對得回同一個檔
        var sheet = new ParsedSourceSheet { SheetName = ws.Name };

        if (!SourceConfigFileIo.IsSafeName(sheet.SheetName))
            sheet.Errors.Add(SheetIssue.General("srcxl.err.sheet_name_invalid"));
        else if (SourceConfigFileIo.HasEdgeWhitespace(sheet.SheetName))
            sheet.Warnings.Add(SheetIssue.General("srcxl.warn.sheet_name_whitespace"));

        ParseHeader(ws, sheet);
        ParsePoints(ws, sheet);

        if (sheet.Points.Count == 0)
            sheet.Errors.Add(SheetIssue.General("srcxl.err.no_points"));

        return sheet;
    }

    private static void ParseHeader(IXLWorksheet ws, ParsedSourceSheet sheet)
    {
        var szIp = SourceExcelCells.FindRow1Value(ws, "IP") ?? SourceExcelCells.ReadString(ws, 1, 2);
        var szPort = SourceExcelCells.FindRow1Value(ws, "Port") ?? SourceExcelCells.ReadString(ws, 1, 4);
        var szModbusId = SourceExcelCells.FindRow1Value(ws, "ModbusID", "ModbusId", "Modbus ID", "UnitId") ?? SourceExcelCells.ReadString(ws, 1, 6);
        var szTimeout = SourceExcelCells.FindRow1Value(ws, "ConnectTimeout", "Timeout") ?? SourceExcelCells.ReadString(ws, 1, 8);

        // IP
        if (szIp.Length == 0 || szIp.Any(char.IsWhiteSpace))
            sheet.Errors.Add(SheetIssue.At(1, "B", "srcxl.err.header_ip_required"));
        sheet.Header["IP"] = szIp;

        // Port（空白 → 502）
        if (szPort.Length == 0)
            szPort = "502";
        if (!SourceExcelCells.TryParsePositiveInt(szPort, out var nPort) || nPort > 65535)
            sheet.Errors.Add(SheetIssue.At(1, "D", "srcxl.err.header_port_invalid"));
        sheet.Header["Port"] = SourceExcelCells.NormalizeNumber(szPort);

        // ModbusId（必填；逗號分隔 0~255）
        var szNormalizedIds = NormalizeModbusIdList(szModbusId, out var isIdsValid);
        if (szNormalizedIds.Length == 0)
            sheet.Errors.Add(SheetIssue.At(1, "F", "srcxl.err.header_modbusid_required"));
        else if (!isIdsValid)
            sheet.Errors.Add(SheetIssue.At(1, "F", "srcxl.err.header_modbusid_invalid"));
        sheet.Header["ModbusId"] = szNormalizedIds;

        // ConnectTimeout（空白 → 1000）
        if (szTimeout.Length == 0)
            szTimeout = "1000";
        if (!SourceExcelCells.TryParsePositiveInt(szTimeout, out _))
            sheet.Errors.Add(SheetIssue.At(1, "H", "srcxl.err.header_timeout_invalid"));
        sheet.Header["ConnectTimeout"] = SourceExcelCells.NormalizeNumber(szTimeout);

        // DeviceName（選填）：沒有 I1 標籤（舊版 Excel）→ 不放進 Header，Reconcile 時沿用既有 JSON 值、不清空
        var szDeviceName = SourceExcelCells.FindRow1Value(ws, DEVICE_NAME_FIELD, "子設備名稱", "子設備名稱 DeviceName");
        if (szDeviceName != null)
        {
            var nStationCount = isIdsValid ? szNormalizedIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length : 0;
            var szNormalizedNames = NormalizeDeviceNameList(szDeviceName, nStationCount, out var nNameCount);
            if (nStationCount > 0 && nNameCount > nStationCount)
                sheet.Errors.Add(SheetIssue.At(1, "J", "srcxl.err.header_devicename_too_many", nNameCount, nStationCount));
            else if (szNormalizedNames.Length > DEVICE_NAME_MAX_LENGTH)
                sheet.Errors.Add(SheetIssue.At(1, "J", "srcxl.err.header_devicename_too_long", DEVICE_NAME_MAX_LENGTH));
            sheet.Header[DEVICE_NAME_FIELD] = szNormalizedNames;
        }
    }

    /// <summary>
    /// 子站名稱逗號串正規化：全形逗號視同半形、各段 Trim、去掉尾端空段；
    /// 全部空白 → 空字串；名稱數少於站號數 → 尾端補空段（對齊站號）。nNameCount = 去尾端空段後的名稱數（數量檢核用）
    /// </summary>
    public static string NormalizeDeviceNameList(string? sz, int nStationCount, out int nNameCount)
    {
        var parts = (sz ?? string.Empty).Replace('\uFF0C', ',')
            .Split(',')
            .Select(p => p.Trim())
            .ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
            parts.RemoveAt(parts.Count - 1);

        nNameCount = parts.Count;
        if (parts.Count == 0) return string.Empty;

        while (parts.Count < nStationCount)
            parts.Add(string.Empty);
        return string.Join(",", parts);
    }

    /// <summary>站號 → 名稱對應（名稱不足補空白）</summary>
    public static List<(string szModbusId, string szName)> PairStationNames(string? szModbusIds, string? szDeviceNames)
    {
        var ids = (szModbusIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = (szDeviceNames ?? string.Empty).Split(',').Select(n => n.Trim()).ToArray();
        return ids.Select((szId, i) => (szId, i < names.Length ? names[i] : string.Empty)).ToList();
    }

    /// <summary>"1, 2,3" → "1,2,3"；任一項非 0~255 整數 → isValid=false（仍回傳清理後字串供顯示）</summary>
    public static string NormalizeModbusIdList(string? sz, out bool isValid)
    {
        isValid = true;
        var parts = (sz ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SourceExcelCells.NormalizeNumber)
            .ToList();

        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n) || n < 0 || n > 255)
                isValid = false;
        }
        return string.Join(",", parts);
    }

    private static void ParsePoints(IXLWorksheet ws, ParsedSourceSheet sheet)
    {
        var nLastRow = SourceExcelCells.LastDataRow(ws, COL_NAME, DATA_FIRST_ROW);

        for (var r = DATA_FIRST_ROW; r <= nLastRow; r++)
        {
            var szName = SourceExcelCells.ReadString(ws, r, COL_NAME);
            if (szName.Length == 0)
            {
                // 名稱空白 → 整列略過（巨集 DB 版行為；Modbus 巨集會產生空點位，這裡統一為略過並提示）
                sheet.Warnings.Add(SheetIssue.At(r, "A", "srcxl.warn.blank_row_skipped"));
                continue;
            }

            var point = new ParsedSourcePoint { RowNumber = r };
            point.Fields["Name"] = szName;

            // Address：保留前導 0（6 位數擴充慣例靠長度區分）；數值儲存格已由 ReadString 轉成整數字串
            var szAddress = SourceExcelCells.ReadString(ws, r, COL_ADDRESS);
            if (!ModbusConfigFileService.IsValidAddress(szAddress))
                sheet.Errors.Add(SheetIssue.At(r, "B", "srcxl.err.address_invalid"));
            point.Fields["Address"] = szAddress;

            // DataType：白名單（大寫正規形）
            var szDataType = SourceExcelCells.ReadString(ws, r, COL_DATATYPE).ToUpperInvariant();
            if (!ModbusTagModel.SupportedDataTypes.Contains(szDataType))
                sheet.Errors.Add(SheetIssue.At(r, "C", "srcxl.err.datatype_unsupported"));
            else if (ModbusTagModel.TryParseBitIndex(szDataType, out _)
                     && ModbusConfigFileService.IsValidAddress(szAddress)
                     && !ModbusConfigFileService.IsRegisterAddress(szAddress))
                sheet.Errors.Add(SheetIssue.At(r, "C", "srcxl.err.bit_type_needs_register"));
            point.Fields["DataType"] = szDataType;

            // Ratio：空白 → 1
            var szRatio = SourceExcelCells.ReadString(ws, r, COL_RATIO);
            if (szRatio.Length == 0) szRatio = "1";
            if (!SourceExcelCells.TryParseNumber(szRatio, out _))
                sheet.Errors.Add(SheetIssue.At(r, "D", "srcxl.err.numeric_required", "Ratio"));
            point.Fields["Ratio"] = SourceExcelCells.NormalizeNumber(szRatio);

            point.Fields["Unit"] = SourceExcelCells.ReadString(ws, r, COL_UNIT);

            // Min / Max：可留白；有填必須是數字
            var szMin = SourceExcelCells.ReadString(ws, r, COL_MIN);
            if (szMin.Length > 0 && !SourceExcelCells.TryParseNumber(szMin, out _))
                sheet.Errors.Add(SheetIssue.At(r, "F", "srcxl.err.numeric_required", "Min"));
            point.Fields["Min"] = SourceExcelCells.NormalizeNumber(szMin);

            var szMax = SourceExcelCells.ReadString(ws, r, COL_MAX);
            if (szMax.Length > 0 && !SourceExcelCells.TryParseNumber(szMax, out _))
                sheet.Errors.Add(SheetIssue.At(r, "G", "srcxl.err.numeric_required", "Max"));
            point.Fields["Max"] = SourceExcelCells.NormalizeNumber(szMax);

            point.Fields["Device"] = SourceExcelCells.ReadString(ws, r, COL_DEVICE);

            sheet.Points.Add(point);
        }
    }

    // ───────────────────────────── JSON ⇄ 中介 ─────────────────────────────

    public ParsedSourceSheet? ParseJson(string szName, string szJson)
    {
        try
        {
            if (SourceConfigFileIo.ParseJsonObject(szJson) is not JsonObject root) return null;

            var sheet = new ParsedSourceSheet { SheetName = szName };
            sheet.Header["IP"] = (root["IP"]?.ToString() ?? string.Empty).Trim();
            sheet.Header["Port"] = SourceExcelCells.NormalizeNumber(root["Port"]?.ToString() ?? "502");
            sheet.Header["ModbusId"] = NormalizeModbusIdList(root["ModbusId"]?.ToString(), out _);
            sheet.Header["ConnectTimeout"] = SourceExcelCells.NormalizeNumber(root["ConnectTimeout"]?.ToString() ?? "1000");
            // DeviceName 欄位不存在（舊檔）→ 不放進 Header，與「明確清空」區分
            if (root[DEVICE_NAME_FIELD] is JsonValue deviceNameValue)
                sheet.Header[DEVICE_NAME_FIELD] = NormalizeDeviceNameList(deviceNameValue.ToString(), 0, out _);
            sheet.SourceWorkbook = root[SourceJsonStamp.SOURCE_WORKBOOK]?.ToString();
            sheet.WebEditedAt = SourceJsonStamp.ReadWebEdited(root);

            if (root["Tags"] is JsonArray tags)
            {
                var nIndex = 0;
                foreach (var tag in tags)
                {
                    nIndex++;
                    if (tag is not JsonObject t) continue;
                    var point = new ParsedSourcePoint { RowNumber = nIndex };
                    point.Fields["Name"] = (t["Name"]?.ToString() ?? string.Empty).Trim();
                    point.Fields["Address"] = (t["Address"]?.ToString() ?? string.Empty).Trim();
                    point.Fields["DataType"] = (t["DataType"]?.ToString() ?? string.Empty).Trim().ToUpperInvariant();
                    var szRatio = (t["Ratio"]?.ToString() ?? string.Empty).Trim();
                    point.Fields["Ratio"] = SourceExcelCells.NormalizeNumber(szRatio.Length == 0 ? "1" : szRatio);
                    point.Fields["Unit"] = (t["Unit"]?.ToString() ?? string.Empty).Trim();
                    point.Fields["Min"] = SourceExcelCells.NormalizeNumber(t["Min"]?.ToString());
                    point.Fields["Max"] = SourceExcelCells.NormalizeNumber(t["Max"]?.ToString());
                    point.Fields["Device"] = (t["Device"]?.ToString() ?? string.Empty).Trim();
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
            ["IP"] = sheet.Header.GetValueOrDefault("IP", string.Empty),
            ["Port"] = int.TryParse(sheet.Header.GetValueOrDefault("Port"), out var nPort) ? nPort : 502,
            ["ModbusId"] = sheet.Header.GetValueOrDefault("ModbusId", "1"),
            ["ConnectTimeout"] = int.TryParse(sheet.Header.GetValueOrDefault("ConnectTimeout"), out var nTimeout) ? nTimeout : 1000,
        };
        if (sheet.Header.TryGetValue(DEVICE_NAME_FIELD, out var szDeviceName))
            root[DEVICE_NAME_FIELD] = szDeviceName;
        if (!string.IsNullOrWhiteSpace(szSourceWorkbook))
            root["SourceWorkbook"] = szSourceWorkbook;

        var tags = new JsonArray();
        foreach (var p in sheet.Points)
        {
            tags.Add(new JsonObject
            {
                ["Name"] = p.Get("Name"),
                ["Address"] = p.Get("Address"),
                ["DataType"] = p.Get("DataType"),
                ["Ratio"] = p.Get("Ratio").Length == 0 ? "1" : p.Get("Ratio"),
                ["Unit"] = p.Get("Unit"),
                ["Min"] = p.Get("Min"),
                ["Max"] = p.Get("Max"),
                ["Device"] = p.Get("Device"),
            });
        }
        root["Tags"] = tags;

        return root.ToJsonString(_jsonOptions);
    }

    /// <summary>匯入重寫 JSON：Excel 不帶的頂層欄位（RetryCount / NoResponseReconnectRounds / RenamedFrom）從既有檔保留</summary>
    public string ToJson(ParsedSourceSheet sheet, string? szSourceWorkbook, string? szExistingJson)
    {
        var szJson = ToJson(sheet, szSourceWorkbook);
        if (string.IsNullOrWhiteSpace(szExistingJson)
            || SourceConfigFileIo.ParseJsonObject(szExistingJson) is not JsonObject existing
            || SourceConfigFileIo.ParseJsonObject(szJson) is not JsonObject root)
            return szJson;

        var isPreserved = false;
        foreach (var szField in _preservedJsonFields)
        {
            if (existing[szField] is { } value)
            {
                root[szField] = value.DeepClone();
                isPreserved = true;
            }
        }
        return isPreserved ? root.ToJsonString(_jsonOptions) : szJson;
    }

    /// <summary>舊版 Excel（第 1 列無 DeviceName 標籤）→ 沿用既有 JSON 的子站名稱，不視為清空</summary>
    public void Reconcile(ParsedSourceSheet sheet, ParsedSourceSheet? existing)
    {
        if (!sheet.Header.ContainsKey(DEVICE_NAME_FIELD)
            && existing != null && existing.Header.TryGetValue(DEVICE_NAME_FIELD, out var szExisting))
            sheet.Header[DEVICE_NAME_FIELD] = szExisting;
    }

    /// <summary>
    /// 匯入預覽「站號 → 名稱」對應（防順序錯置：數量檢核抓不到兩個名稱調換）。
    /// 單站號且兩邊都沒有名稱時不顯示。舊名以「同一站號」比對，不以位置。
    /// </summary>
    public IReadOnlyList<SheetDiffStationName>? BuildStationNames(ParsedSourceSheet uploaded, ParsedSourceSheet? existing)
    {
        var pairs = PairStationNames(uploaded.Header.GetValueOrDefault("ModbusId"), uploaded.Header.GetValueOrDefault(DEVICE_NAME_FIELD));
        var oldByStation = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existing != null)
        {
            foreach (var (szId, szName) in PairStationNames(existing.Header.GetValueOrDefault("ModbusId"), existing.Header.GetValueOrDefault(DEVICE_NAME_FIELD)))
                oldByStation.TryAdd(szId, szName);
        }

        if (pairs.Count <= 1 && pairs.All(p => p.szName.Length == 0) && oldByStation.Values.All(n => n.Length == 0))
            return null;

        return pairs.Select(p =>
        {
            var szOld = existing == null ? null : oldByStation.GetValueOrDefault(p.szModbusId, string.Empty);
            return new SheetDiffStationName
            {
                ModbusId = p.szModbusId,
                Name = p.szName,
                OldName = szOld,
                IsChanged = szOld != null && !string.Equals(szOld, p.szName, StringComparison.Ordinal),
            };
        }).ToList();
    }

    /// <summary>匯出用：舊 JSON 沒有 DeviceName 欄位 → 以 DB 的 ModbusCoordinator.DeviceName 補（之後第一次匯入即落進 JSON）</summary>
    public static void ApplyDeviceNameFallback(ParsedSourceSheet sheet, string? szDbDeviceName)
    {
        if (!sheet.Header.ContainsKey(DEVICE_NAME_FIELD) && !string.IsNullOrWhiteSpace(szDbDeviceName))
            sheet.Header[DEVICE_NAME_FIELD] = NormalizeDeviceNameList(szDbDeviceName, 0, out _);
    }

    // ───────────────────────────── 範本 / 匯出版面 ─────────────────────────────

    public void WriteSheet(IXLWorksheet ws, ParsedSourceSheet? sheet)
    {
        // 第 1 列：設備設定（標籤 / 值 成對）
        ws.Cell(1, 1).Value = "IP";
        ws.Cell(1, 2).Value = sheet?.Header.GetValueOrDefault("IP") ?? string.Empty;
        ws.Cell(1, 3).Value = "Port";
        ws.Cell(1, 4).Value = int.TryParse(sheet?.Header.GetValueOrDefault("Port"), out var nPort) ? nPort : 502;
        ws.Cell(1, 5).Value = "ModbusID";
        ws.Cell(1, 6).Value = sheet?.Header.GetValueOrDefault("ModbusId") ?? "1";
        ws.Cell(1, 6).Style.NumberFormat.Format = "@";
        ws.Cell(1, 7).Value = "ConnectTimeout";
        ws.Cell(1, 8).Value = int.TryParse(sheet?.Header.GetValueOrDefault("ConnectTimeout"), out var nTimeout) ? nTimeout : 1000;
        ws.Cell(1, COL_DEVICENAME_LABEL).Value = DEVICE_NAME_FIELD;
        ws.Cell(1, COL_DEVICENAME_VALUE).Value = sheet?.Header.GetValueOrDefault(DEVICE_NAME_FIELD) ?? string.Empty;
        ws.Cell(1, COL_DEVICENAME_VALUE).Style.NumberFormat.Format = "@";
        foreach (var c in new[] { 1, 3, 5, 7, COL_DEVICENAME_LABEL }) SourceExcelCells.StyleLabel(ws.Cell(1, c));

        // 第 2 列：欄位標題
        var aHeaders = new[] { "名稱 Name", "位址 Address", "資料型態 DataType", "倍率 Ratio", "單位 Unit", "最小值 Min", "最大值 Max", "子設備 Device" };
        for (var i = 0; i < aHeaders.Length; i++)
            ws.Cell(2, i + 1).Value = aHeaders[i];
        SourceExcelCells.StyleHeader(ws.Range(2, 1, 2, aHeaders.Length));

        // I 欄：DataType 清單（下拉來源）
        ws.Cell(2, COL_TYPELIST).Value = "DataType清單";
        SourceExcelCells.StyleHeader(ws.Range(2, COL_TYPELIST, 2, COL_TYPELIST));
        var aTypes = ModbusTagModel.SupportedDataTypes;
        for (var i = 0; i < aTypes.Length; i++)
            ws.Cell(DATA_FIRST_ROW + i, COL_TYPELIST).Value = aTypes[i];
        var nTypeListLastRow = DATA_FIRST_ROW + aTypes.Length - 1;

        // Address 欄文字格式（保留 6 位數前導 0）
        ws.Column(COL_ADDRESS).Style.NumberFormat.Format = "@";

        // 資料列
        var nRow = DATA_FIRST_ROW;
        if (sheet != null)
        {
            foreach (var p in sheet.Points)
            {
                ws.Cell(nRow, COL_NAME).Value = p.Get("Name");
                ws.Cell(nRow, COL_ADDRESS).Value = p.Get("Address");
                ws.Cell(nRow, COL_DATATYPE).Value = p.Get("DataType");
                WriteNumberOrText(ws.Cell(nRow, COL_RATIO), p.Get("Ratio"));
                ws.Cell(nRow, COL_UNIT).Value = p.Get("Unit");
                WriteNumberOrText(ws.Cell(nRow, COL_MIN), p.Get("Min"));
                WriteNumberOrText(ws.Cell(nRow, COL_MAX), p.Get("Max"));
                ws.Cell(nRow, COL_DEVICE).Value = p.Get("Device");
                nRow++;
            }
        }

        // 下拉驗證：DataType ← I 欄清單；數值欄
        var nValidationLastRow = Math.Max(nRow, DATA_FIRST_ROW + TEMPLATE_VALIDATION_ROWS);
        var dvType = ws.Range(DATA_FIRST_ROW, COL_DATATYPE, nValidationLastRow, COL_DATATYPE).CreateDataValidation();
        dvType.List(ws.Range(DATA_FIRST_ROW, COL_TYPELIST, nTypeListLastRow, COL_TYPELIST), true);
        var dvNum = ws.Range(DATA_FIRST_ROW, COL_RATIO, nValidationLastRow, COL_RATIO).CreateDataValidation();
        dvNum.Decimal.Between(-1e12, 1e12);
        var dvMinMax = ws.Range(DATA_FIRST_ROW, COL_MIN, nValidationLastRow, COL_MAX).CreateDataValidation();
        dvMinMax.Decimal.Between(-1e12, 1e12);

        // 版面
        ws.SheetView.FreezeRows(2);
        ws.Column(COL_NAME).Width = 24;
        ws.Column(COL_ADDRESS).Width = 14;
        ws.Column(COL_DATATYPE).Width = 18;
        ws.Column(COL_RATIO).Width = 10;
        ws.Column(COL_UNIT).Width = 10;
        ws.Column(COL_MIN).Width = 10;
        ws.Column(COL_MAX).Width = 12;
        ws.Column(COL_DEVICE).Width = 18;
        ws.Column(COL_TYPELIST).Width = 16;

        // 第 1 列的 IP / ModbusID 清單可能比資料欄寬長（例 "1,2,3,4,5,6,7,8"），右鄰是標籤無法溢出 → 放寬
        SourceExcelCells.FitColumnToRow1Value(ws, 2);
        SourceExcelCells.FitColumnToRow1Value(ws, 6);
        SourceExcelCells.FitColumnToRow1Value(ws, COL_DEVICENAME_VALUE);
    }

    /// <summary>可解析為數字就寫數值儲存格，否則寫文字（空字串 → 留白）</summary>
    private static void WriteNumberOrText(IXLCell cell, string sz)
    {
        if (sz.Length == 0) return;
        if (SourceExcelCells.TryParseNumber(sz, out var d)) cell.Value = d;
        else cell.Value = sz;
    }
}
