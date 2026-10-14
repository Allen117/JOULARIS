using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// OPC UA Server Excel ⇄ JSON adapter（plan 2026-10-12 決策 3、4、5）。版面沿用舊 `OPCUA通訊檔案產生工具.xlsm`（舊檔可直接上傳）：
///
/// - 第 1 列：標籤 / 值成對 — EndpointUrl、Username、Password、ConnectTimeout、PollingInterval、MonitorEnabled、MaxNodesPerRead
///   → 以標籤文字掃描第 1 列取右側儲存格，找不到才退回固定位置（B1 / D1 / F1 / H1 / J1 / L1 / N1）
/// - 第 2 列：欄位標題
/// - 第 3 列起：A~H = Name / TagName(NodeId) / ControlType / Ratio / Unit / Min / Max / Device，**I = Seq（新增）**
///
/// 一張工作表 = 一個 Server = 一個 JSON；多個 Device 以 Device 欄分組（空白時從 NodeId 的 `s=` 之後、第一個 `.` 之前推得，推不出用工作表名稱）。
///
/// **點位以 Seq 比對，不以位置比對**（SID = OPC{Id}-S{Seq}，Seq 與陣列位置無關）：
/// - 有 Seq 的列 → 必須是既有 JSON 中存在的 Seq（否則報錯「未知的 Seq」）；既有 JSON 不存在時（新 Server）照單接受
/// - 沒有 Seq 的列 → 先以 TagName 對回既有點位（舊 xlsm 相容、誤清空 Seq 的保險），對不到 → 新點位從 max(NextSeq, 最大 Seq+1) 起配號
/// - 既有點位在 Excel 找不到 → 預覽列為「將移除」（Seq 不回收）
/// 對帳在 <see cref="Reconcile"/>（預覽時、比對前）完成；<see cref="ToJson(ParsedSourceSheet, string?, string?)"/> 只負責序列化。
///
/// **密碼**：匯出一律留空；匯入時 Password 欄空白 = 保留原密碼（與網頁 SaveServer 行為一致）。
/// Header["Password"] 存遮罩（比對 / 顯示用）、Header["PasswordRaw"] 存實際值（序列化用、不列入 HeaderFields）。
///
/// JSON 輸出與 OpcUaCoordinatorService 寫回一致：Ratio 字串、Min/Max 數字（空白不輸出）、NextSeq 單調遞增、UTF-8 BOM。
/// </summary>
public class OpcUaExcelAdapter : ISourceExcelAdapter
{
    private const int COL_NAME = 1, COL_TAGNAME = 2, COL_CONTROLTYPE = 3, COL_RATIO = 4,
                      COL_UNIT = 5, COL_MIN = 6, COL_MAX = 7, COL_DEVICE = 8, COL_SEQ = 9;
    private const int DATA_FIRST_ROW = 3;
    private const int TEMPLATE_VALIDATION_ROWS = 1000;
    private const int MIN_POLLING_INTERVAL = 200;
    private const int MIN_CONNECT_TIMEOUT = 1000;
    private const string PASSWORD_MASK = "********";
    private const string NEXT_SEQ_HEADER = "NextSeq";
    private const string PASSWORD_RAW_HEADER = "PasswordRaw";

    private static readonly string[] _headerFields = { "EndpointUrl", "Username", "Password", "PollingInterval", "ConnectTimeout", "MonitorEnabled", "MaxNodesPerRead" };
    private static readonly string[] _pointFields = { "Name", "TagName", "ControlType", "Ratio", "Unit", "Min", "Max", "Device" };

    /// <summary>名稱 = JSON 檔名 = MQTT 主題段（與 OpcUaCoordinatorService 相同規則）</summary>
    private static readonly Regex _nameRegex = new("^[A-Za-z0-9_-]{1,50}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SourceExcelKind Kind => SourceExcelKind.OpcUa;
    public string ConfigSection => SourceConfigFileIo.OPCUA_SECTION;
    public string TemplateFileName => "OPCUA範本.xlsx";
    public string ExportFileNamePrefix => "OPCUA設定";
    public string DefaultSheetName => "OpcUa1";
    public IReadOnlyList<string> HeaderFields => _headerFields;
    public IReadOnlyList<string> PointFields => _pointFields;
    public string? PointKeyField => "Seq";
    public bool SupportsDeleteCandidates => false;

    /// <summary>與 OpcUaCoordinatorService 寫回一致：UTF-8 with BOM</summary>
    public Encoding NewFileEncoding => Encoding.UTF8;

    // ───────────────────────────── Excel → 中介 ─────────────────────────────

    public ParsedSourceSheet ParseSheet(IXLWorksheet ws)
    {
        var sheet = new ParsedSourceSheet { SheetName = ws.Name.Trim() };

        if (!_nameRegex.IsMatch(sheet.SheetName))
            sheet.Errors.Add(SheetIssue.General("srcxl.err.opcua_name_invalid"));

        ParseHeader(ws, sheet);
        ParsePoints(ws, sheet);

        if (sheet.Points.Count == 0)
            sheet.Errors.Add(SheetIssue.General("srcxl.err.no_points"));

        return sheet;
    }

    private static void ParseHeader(IXLWorksheet ws, ParsedSourceSheet sheet)
    {
        var szEndpoint = SourceExcelCells.FindRow1Value(ws, "EndpointUrl", "Endpoint") ?? SourceExcelCells.ReadString(ws, 1, 2);
        var szUsername = SourceExcelCells.FindRow1Value(ws, "Username", "User") ?? SourceExcelCells.ReadString(ws, 1, 4);
        var szPassword = SourceExcelCells.FindRow1Value(ws, "Password") ?? SourceExcelCells.ReadString(ws, 1, 6);
        var szTimeout = SourceExcelCells.FindRow1Value(ws, "ConnectTimeout", "Timeout") ?? SourceExcelCells.ReadString(ws, 1, 8);
        var szPolling = SourceExcelCells.FindRow1Value(ws, "PollingInterval", "Polling") ?? SourceExcelCells.ReadString(ws, 1, 10);
        var szMonitor = SourceExcelCells.FindRow1Value(ws, "MonitorEnabled", "Monitor") ?? SourceExcelCells.ReadString(ws, 1, 12);
        var szMaxNodes = SourceExcelCells.FindRow1Value(ws, "MaxNodesPerRead") ?? SourceExcelCells.ReadString(ws, 1, 14);

        if (!szEndpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase) || szEndpoint.Any(char.IsWhiteSpace))
            sheet.Errors.Add(SheetIssue.At(1, "B", "srcxl.err.header_endpoint_invalid"));
        sheet.Header["EndpointUrl"] = szEndpoint;
        sheet.Header["Username"] = szUsername;

        // 密碼：遮罩進 Header["Password"]（比對 / 顯示），實際值另存（序列化）
        sheet.Header["Password"] = szPassword.Length > 0 ? PASSWORD_MASK : string.Empty;
        if (szPassword.Length > 0)
            sheet.Header[PASSWORD_RAW_HEADER] = szPassword;

        if (szTimeout.Length == 0) szTimeout = "5000";
        if (!SourceExcelCells.TryParsePositiveInt(szTimeout, out var nTimeout))
            sheet.Errors.Add(SheetIssue.At(1, "H", "srcxl.err.header_timeout_invalid"));
        else if (nTimeout < MIN_CONNECT_TIMEOUT)
            sheet.Errors.Add(SheetIssue.At(1, "H", "srcxl.err.header_timeout_min", MIN_CONNECT_TIMEOUT));
        sheet.Header["ConnectTimeout"] = SourceExcelCells.NormalizeNumber(szTimeout);

        if (szPolling.Length == 0) szPolling = "1000";
        if (!SourceExcelCells.TryParsePositiveInt(szPolling, out var nPolling))
            sheet.Errors.Add(SheetIssue.At(1, "J", "srcxl.err.header_polling_invalid"));
        else if (nPolling < MIN_POLLING_INTERVAL)
            sheet.Errors.Add(SheetIssue.At(1, "J", "srcxl.err.header_polling_min", MIN_POLLING_INTERVAL));
        sheet.Header["PollingInterval"] = SourceExcelCells.NormalizeNumber(szPolling);

        var szMonitorNorm = SourceExcelCells.NormalizeBool(szMonitor, out var isMonitorValid);
        if (!isMonitorValid)
            sheet.Errors.Add(SheetIssue.At(1, "L", "srcxl.err.header_monitor_invalid"));
        sheet.Header["MonitorEnabled"] = isMonitorValid ? szMonitorNorm : "TRUE";

        if (szMaxNodes.Length == 0) szMaxNodes = "0";
        if (!SourceExcelCells.TryParseNumber(szMaxNodes, out var dMaxNodes) || dMaxNodes < 0 || Math.Abs(dMaxNodes - Math.Round(dMaxNodes)) > double.Epsilon)
            sheet.Errors.Add(SheetIssue.At(1, "N", "srcxl.err.header_maxnodes_invalid"));
        sheet.Header["MaxNodesPerRead"] = SourceExcelCells.NormalizeNumber(szMaxNodes);
    }

    private static void ParsePoints(IXLWorksheet ws, ParsedSourceSheet sheet)
    {
        var nLastRow = SourceExcelCells.LastDataRow(ws, COL_NAME, DATA_FIRST_ROW);
        var seenSeq = new HashSet<int>();

        for (var r = DATA_FIRST_ROW; r <= nLastRow; r++)
        {
            var szName = SourceExcelCells.ReadString(ws, r, COL_NAME);
            if (szName.Length == 0)
            {
                sheet.Warnings.Add(SheetIssue.At(r, "A", "srcxl.warn.blank_row_skipped"));
                continue;
            }

            var point = new ParsedSourcePoint { RowNumber = r };
            point.Fields["Name"] = szName;

            var szTagName = SourceExcelCells.ReadString(ws, r, COL_TAGNAME);
            if (szTagName.Length == 0)
                sheet.Errors.Add(SheetIssue.At(r, "B", "srcxl.err.nodeid_required"));
            else if (!IsValidNodeId(szTagName))
                sheet.Errors.Add(SheetIssue.At(r, "B", "srcxl.err.nodeid_invalid"));
            point.Fields["TagName"] = szTagName;

            var szControlType = SourceExcelCells.ReadString(ws, r, COL_CONTROLTYPE).ToUpperInvariant();
            if (szControlType is not ("" or "AO" or "DO"))
                sheet.Errors.Add(SheetIssue.At(r, "C", "srcxl.err.controltype_invalid"));
            point.Fields["ControlType"] = szControlType;

            var szRatio = SourceExcelCells.ReadString(ws, r, COL_RATIO);
            if (szRatio.Length == 0) szRatio = "1";
            if (!SourceExcelCells.TryParseNumber(szRatio, out var dRatio))
                sheet.Errors.Add(SheetIssue.At(r, "D", "srcxl.err.numeric_required", "Ratio"));
            else if (dRatio == 0)
                sheet.Errors.Add(SheetIssue.At(r, "D", "srcxl.err.ratio_zero"));
            point.Fields["Ratio"] = SourceExcelCells.NormalizeNumber(szRatio);

            point.Fields["Unit"] = SourceExcelCells.ReadString(ws, r, COL_UNIT);

            var szMin = SourceExcelCells.ReadString(ws, r, COL_MIN);
            var isMinOk = szMin.Length == 0 || SourceExcelCells.TryParseNumber(szMin, out _);
            if (!isMinOk)
                sheet.Errors.Add(SheetIssue.At(r, "F", "srcxl.err.numeric_required", "Min"));
            point.Fields["Min"] = SourceExcelCells.NormalizeNumber(szMin);

            var szMax = SourceExcelCells.ReadString(ws, r, COL_MAX);
            var isMaxOk = szMax.Length == 0 || SourceExcelCells.TryParseNumber(szMax, out _);
            if (!isMaxOk)
                sheet.Errors.Add(SheetIssue.At(r, "G", "srcxl.err.numeric_required", "Max"));
            point.Fields["Max"] = SourceExcelCells.NormalizeNumber(szMax);

            if (isMinOk && isMaxOk && szMin.Length > 0 && szMax.Length > 0
                && SourceExcelCells.TryParseNumber(szMin, out var dMin) && SourceExcelCells.TryParseNumber(szMax, out var dMax) && dMin >= dMax)
                sheet.Errors.Add(SheetIssue.At(r, "G", "srcxl.err.minmax_invalid"));

            var szDevice = SourceExcelCells.ReadString(ws, r, COL_DEVICE);
            if (szDevice.Length == 0)
                szDevice = InferDevice(szTagName, sheet.SheetName);
            point.Fields["Device"] = szDevice;

            // Seq：空白 = 新增（或以 TagName 對回）；有填必須是正整數且不重複
            var szSeq = SourceExcelCells.ReadString(ws, r, COL_SEQ);
            if (szSeq.Length > 0)
            {
                if (!SourceExcelCells.TryParsePositiveInt(szSeq, out var nSeq))
                    sheet.Errors.Add(SheetIssue.At(r, "I", "srcxl.err.seq_invalid"));
                else if (!seenSeq.Add(nSeq))
                    sheet.Errors.Add(SheetIssue.At(r, "I", "srcxl.err.seq_duplicate", nSeq));
                szSeq = SourceExcelCells.NormalizeNumber(szSeq);
            }
            point.Fields["Seq"] = szSeq;

            sheet.Points.Add(point);
        }
    }

    /// <summary>NodeId 格式（與 OpcUaCoordinatorService.SavePointsAsync 相同：交給 OPC UA SDK 解析）</summary>
    public static bool IsValidNodeId(string szNodeId)
    {
        try
        {
            Opc.Ua.NodeId.Parse(szNodeId.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Device 空白時沿用舊轉換器規則：NodeId 的 `s=` 字串識別碼中、第一個 `.` 之前（ns=2;s=D1.T → D1）；推不出用工作表名稱。
    /// </summary>
    public static string InferDevice(string szNodeId, string szSheetName)
    {
        foreach (var part in (szNodeId ?? string.Empty).Split(';'))
        {
            var p = part.Trim();
            if (!p.StartsWith("s=", StringComparison.OrdinalIgnoreCase)) continue;
            var szId = p.Substring(2);
            var nDot = szId.IndexOf('.');
            if (nDot > 0) return szId.Substring(0, nDot).Trim();
            break;
        }
        return szSheetName;
    }

    // ───────────────────────────── 對帳（Seq） ─────────────────────────────

    /// <summary>
    /// 預覽時把 Excel 列對回既有 Seq / 為新列配號（純函數、可重入）：
    /// 1. 有 Seq 的列：既有 JSON 存在時必須是既有 Seq（否則 srcxl.err.seq_unknown）
    /// 2. 沒有 Seq 的列：先以 TagName 對回尚未被認領的既有點位（附提示），對不到 → 從 max(NextSeq, 最大 Seq+1) 起配新號
    /// 結果寫回 point.Fields["Seq"]，sheet.Header["NextSeq"] = 配號後的下一個號碼（供 ToJson）。
    /// </summary>
    public void Reconcile(ParsedSourceSheet sheet, ParsedSourceSheet? existing)
    {
        var existingSeqs = new HashSet<int>();
        var seqByTag = new Dictionary<string, int>(StringComparer.Ordinal);
        var nNext = 1;

        if (existing != null)
        {
            foreach (var p in existing.Points)
            {
                if (!int.TryParse(p.Get("Seq"), out var n) || n <= 0) continue;
                existingSeqs.Add(n);
                seqByTag.TryAdd(p.Get("TagName"), n);
                nNext = Math.Max(nNext, n + 1);
            }
            if (int.TryParse(existing.Header.GetValueOrDefault(NEXT_SEQ_HEADER), out var nExistingNext))
                nNext = Math.Max(nNext, nExistingNext);
        }

        var assigned = new HashSet<int>();

        // 第一輪：明確填了 Seq 的列
        foreach (var p in sheet.Points)
        {
            if (!int.TryParse(p.Get("Seq"), out var n) || n <= 0) continue;
            if (existing != null && !existingSeqs.Contains(n))
                sheet.Errors.Add(SheetIssue.At(p.RowNumber, "I", "srcxl.err.seq_unknown", n));
            assigned.Add(n);
            nNext = Math.Max(nNext, n + 1);
        }

        // 第二輪：空白 Seq → TagName 對回 / 配新號
        foreach (var p in sheet.Points)
        {
            if (p.Get("Seq").Length > 0) continue;

            if (existing != null && seqByTag.TryGetValue(p.Get("TagName"), out var nMatched) && assigned.Add(nMatched))
            {
                p.Fields["Seq"] = nMatched.ToString(CultureInfo.InvariantCulture);
                sheet.Warnings.Add(SheetIssue.At(p.RowNumber, "I", "srcxl.warn.seq_matched_by_nodeid", nMatched));
                continue;
            }

            while (!assigned.Add(nNext)) nNext++;
            p.Fields["Seq"] = nNext.ToString(CultureInfo.InvariantCulture);
            nNext++;
        }

        sheet.Header[NEXT_SEQ_HEADER] = nNext.ToString(CultureInfo.InvariantCulture);
    }

    // ───────────────────────────── JSON ⇄ 中介 ─────────────────────────────

    public ParsedSourceSheet? ParseJson(string szName, string szJson)
    {
        try
        {
            if (SourceConfigFileIo.ParseJsonObject(szJson) is not JsonObject root) return null;

            var sheet = new ParsedSourceSheet { SheetName = szName };
            sheet.Header["EndpointUrl"] = (root["EndpointUrl"]?.ToString() ?? string.Empty).Trim();
            sheet.Header["Username"] = (root["Username"]?.ToString() ?? string.Empty).Trim();
            sheet.Header["Password"] = string.Empty;   // 既有密碼永不進中介表示 / Excel；匯入空白 = 保留
            sheet.Header["PollingInterval"] = SourceExcelCells.NormalizeNumber(root["PollingInterval"]?.ToString() is { Length: > 0 } szPolling ? szPolling : "1000");
            sheet.Header["ConnectTimeout"] = SourceExcelCells.NormalizeNumber(root["ConnectTimeout"]?.ToString() is { Length: > 0 } szTimeout ? szTimeout : "5000");
            sheet.Header["MonitorEnabled"] = SourceExcelCells.NormalizeBool(root["MonitorEnabled"]?.ToString(), out var isValid) is var szMon && isValid ? szMon : "TRUE";
            sheet.Header["MaxNodesPerRead"] = SourceExcelCells.NormalizeNumber(root["MaxNodesPerRead"]?.ToString() is { Length: > 0 } szMax ? szMax : "0");
            sheet.Header[NEXT_SEQ_HEADER] = SourceExcelCells.NormalizeNumber(root["NextSeq"]?.ToString() is { Length: > 0 } szNext ? szNext : "1");
            sheet.SourceWorkbook = root[SourceJsonStamp.SOURCE_WORKBOOK]?.ToString();
            sheet.WebEditedAt = SourceJsonStamp.ReadWebEdited(root);

            var nIndex = 0;
            if (root["Devices"] is JsonArray devices)
            {
                foreach (var dev in devices)
                {
                    if (dev is not JsonObject d) continue;
                    var szDevice = (d["Name"]?.ToString() ?? string.Empty).Trim();
                    if (d["Tags"] is not JsonArray tags) continue;

                    foreach (var tag in tags)
                    {
                        nIndex++;
                        if (tag is not JsonObject t) continue;
                        var point = new ParsedSourcePoint { RowNumber = nIndex };
                        point.Fields["Seq"] = SourceExcelCells.NormalizeNumber(t["Seq"]?.ToString());
                        point.Fields["Name"] = (t["Name"]?.ToString() ?? string.Empty).Trim();
                        point.Fields["TagName"] = (t["TagName"]?.ToString() ?? string.Empty).Trim();
                        point.Fields["ControlType"] = (t["ControlType"]?.ToString() ?? string.Empty).Trim().ToUpperInvariant();
                        var szRatio = (t["Ratio"]?.ToString() ?? string.Empty).Trim();
                        point.Fields["Ratio"] = SourceExcelCells.NormalizeNumber(szRatio.Length == 0 ? "1" : szRatio);
                        point.Fields["Unit"] = (t["Unit"]?.ToString() ?? string.Empty).Trim();
                        point.Fields["Min"] = SourceExcelCells.NormalizeNumber(t["Min"]?.ToString());
                        point.Fields["Max"] = SourceExcelCells.NormalizeNumber(t["Max"]?.ToString());
                        point.Fields["Device"] = szDevice;
                        sheet.Points.Add(point);
                    }
                }
            }

            return sheet;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson(ParsedSourceSheet sheet, string? szSourceWorkbook) => ToJson(sheet, szSourceWorkbook, null);

    /// <summary>
    /// 序列化：密碼空白 → 沿用既有 JSON 的 Password；NextSeq = max(對帳結果, 既有 NextSeq, 最大 Seq+1)；
    /// 既有 JSON 的其他未知頂層欄位原樣保留（WebEditedAt 除外 — 匯入後 Excel 與 JSON 重新一致）。
    /// Seq 仍空白的點位（未經 Reconcile 直接呼叫）依序補號。
    /// </summary>
    public string ToJson(ParsedSourceSheet sheet, string? szSourceWorkbook, string? szExistingJson)
    {
        JsonObject? existingRoot = null;
        if (!string.IsNullOrWhiteSpace(szExistingJson))
        {
            existingRoot = SourceConfigFileIo.ParseJsonObject(szExistingJson);
        }

        var szPassword = sheet.Header.GetValueOrDefault(PASSWORD_RAW_HEADER, string.Empty);
        if (szPassword.Length == 0)
            szPassword = existingRoot?["Password"]?.ToString() ?? string.Empty;

        var root = new JsonObject();

        // 既有未知頂層欄位先放（之後已知欄位覆寫，順序固定在後）
        if (existingRoot != null)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Name", "EndpointUrl", "Username", "Password", "PollingInterval", "ConnectTimeout", "MonitorEnabled",
                "MaxNodesPerRead", "NextSeq", "Devices", SourceJsonStamp.SOURCE_WORKBOOK, SourceJsonStamp.WEB_EDITED_AT,
            };
            foreach (var kv in existingRoot)
            {
                if (!known.Contains(kv.Key))
                    root[kv.Key] = kv.Value?.DeepClone();
            }
        }

        root["Name"] = sheet.SheetName;
        root["EndpointUrl"] = sheet.Header.GetValueOrDefault("EndpointUrl", string.Empty);
        root["Username"] = sheet.Header.GetValueOrDefault("Username", string.Empty);
        root["Password"] = szPassword;
        root["PollingInterval"] = int.TryParse(sheet.Header.GetValueOrDefault("PollingInterval"), out var nPolling) ? nPolling : 1000;
        root["ConnectTimeout"] = int.TryParse(sheet.Header.GetValueOrDefault("ConnectTimeout"), out var nTimeout) ? nTimeout : 5000;
        root["MonitorEnabled"] = !string.Equals(sheet.Header.GetValueOrDefault("MonitorEnabled"), "FALSE", StringComparison.OrdinalIgnoreCase);
        root["MaxNodesPerRead"] = int.TryParse(sheet.Header.GetValueOrDefault("MaxNodesPerRead"), out var nMaxNodes) ? nMaxNodes : 0;

        // Seq：已對帳者沿用；仍空白者依序補號
        var nNext = 1;
        if (int.TryParse(sheet.Header.GetValueOrDefault(NEXT_SEQ_HEADER), out var nHeaderNext)) nNext = Math.Max(nNext, nHeaderNext);
        if (int.TryParse(existingRoot?["NextSeq"]?.ToString(), out var nExistingNext)) nNext = Math.Max(nNext, nExistingNext);
        var used = new HashSet<int>();
        foreach (var p in sheet.Points)
        {
            if (int.TryParse(p.Get("Seq"), out var n) && n > 0)
            {
                used.Add(n);
                nNext = Math.Max(nNext, n + 1);
            }
        }
        var seqs = new Dictionary<ParsedSourcePoint, int>();
        foreach (var p in sheet.Points)
        {
            if (int.TryParse(p.Get("Seq"), out var n) && n > 0)
            {
                seqs[p] = n;
                continue;
            }
            while (!used.Add(nNext)) nNext++;
            seqs[p] = nNext++;
        }
        root["NextSeq"] = nNext;

        if (!string.IsNullOrWhiteSpace(szSourceWorkbook))
            root[SourceJsonStamp.SOURCE_WORKBOOK] = szSourceWorkbook;

        // Devices：依 Device 欄分組，保持首次出現順序
        var devices = new JsonArray();
        var tagsByDevice = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var p in sheet.Points)
        {
            var szDevice = p.Get("Device");
            if (!tagsByDevice.TryGetValue(szDevice, out var tags))
            {
                tags = new JsonArray();
                tagsByDevice[szDevice] = tags;
                devices.Add(new JsonObject { ["Name"] = szDevice, ["Tags"] = tags });
            }

            var tag = new JsonObject
            {
                ["Seq"] = seqs[p],
                ["Name"] = p.Get("Name"),
                ["TagName"] = p.Get("TagName"),
                ["ControlType"] = p.Get("ControlType"),
                ["Ratio"] = p.Get("Ratio").Length == 0 ? "1" : p.Get("Ratio"),
                ["Unit"] = p.Get("Unit"),
            };
            if (SourceExcelCells.TryParseNumber(p.Get("Min"), out var dMin)) tag["Min"] = (float)dMin;
            if (SourceExcelCells.TryParseNumber(p.Get("Max"), out var dMax)) tag["Max"] = (float)dMax;
            tags.Add(tag);
        }
        root["Devices"] = devices;

        return root.ToJsonString(_jsonOptions);
    }

    // ───────────────────────────── 範本 / 匯出版面 ─────────────────────────────

    public void WriteSheet(IXLWorksheet ws, ParsedSourceSheet? sheet)
    {
        // 第 1 列：連線設定（標籤 / 值 成對）
        ws.Cell(1, 1).Value = "EndpointUrl";
        ws.Cell(1, 2).Value = sheet?.Header.GetValueOrDefault("EndpointUrl") ?? "opc.tcp://192.168.1.10:4840";
        ws.Cell(1, 3).Value = "Username";
        ws.Cell(1, 4).Value = sheet?.Header.GetValueOrDefault("Username") ?? string.Empty;
        ws.Cell(1, 5).Value = "Password";
        ws.Cell(1, 6).Value = string.Empty;   // 匯出不含密碼；空白 = 保留原密碼
        ws.Cell(1, 7).Value = "ConnectTimeout";
        ws.Cell(1, 8).Value = int.TryParse(sheet?.Header.GetValueOrDefault("ConnectTimeout"), out var nTimeout) ? nTimeout : 5000;
        ws.Cell(1, 9).Value = "PollingInterval";
        ws.Cell(1, 10).Value = int.TryParse(sheet?.Header.GetValueOrDefault("PollingInterval"), out var nPolling) ? nPolling : 1000;
        ws.Cell(1, 11).Value = "MonitorEnabled";
        ws.Cell(1, 12).Value = !string.Equals(sheet?.Header.GetValueOrDefault("MonitorEnabled"), "FALSE", StringComparison.OrdinalIgnoreCase);
        ws.Cell(1, 13).Value = "MaxNodesPerRead";
        ws.Cell(1, 14).Value = int.TryParse(sheet?.Header.GetValueOrDefault("MaxNodesPerRead"), out var nMaxNodes) ? nMaxNodes : 0;
        ws.Cell(1, 16).Value = "※ Password 留空 = 保留原密碼；Seq 欄勿改，新增點位留空；Device 留空 = 依 NodeId 的 s= 第一段推得";
        ws.Cell(1, 16).Style.Font.FontColor = XLColor.FromHtml("#6C757D");
        foreach (var c in new[] { 1, 3, 5, 7, 9, 11, 13 }) SourceExcelCells.StyleLabel(ws.Cell(1, c));

        var dvMonitor = ws.Cell(1, 12).CreateDataValidation();
        dvMonitor.List("\"TRUE,FALSE\"", true);

        // 第 2 列：欄位標題
        var aHeaders = new[] { "名稱 Name", "TagName (NodeId)", "ControlType", "倍率 Ratio", "單位 Unit", "最小值 Min", "最大值 Max", "子設備 Device", "Seq（勿改；新增留空）" };
        for (var i = 0; i < aHeaders.Length; i++)
            ws.Cell(2, i + 1).Value = aHeaders[i];
        SourceExcelCells.StyleHeader(ws.Range(2, 1, 2, aHeaders.Length));

        // 資料列
        var nRow = DATA_FIRST_ROW;
        if (sheet != null)
        {
            foreach (var p in sheet.Points)
            {
                ws.Cell(nRow, COL_NAME).Value = p.Get("Name");
                ws.Cell(nRow, COL_TAGNAME).Value = p.Get("TagName");
                ws.Cell(nRow, COL_CONTROLTYPE).Value = p.Get("ControlType");
                WriteNumberOrText(ws.Cell(nRow, COL_RATIO), p.Get("Ratio"));
                ws.Cell(nRow, COL_UNIT).Value = p.Get("Unit");
                WriteNumberOrText(ws.Cell(nRow, COL_MIN), p.Get("Min"));
                WriteNumberOrText(ws.Cell(nRow, COL_MAX), p.Get("Max"));
                ws.Cell(nRow, COL_DEVICE).Value = p.Get("Device");
                WriteNumberOrText(ws.Cell(nRow, COL_SEQ), p.Get("Seq"));
                nRow++;
            }
        }

        // 下拉驗證：ControlType（空白 / AO / DO）；數值欄
        var nValidationLastRow = Math.Max(nRow, DATA_FIRST_ROW + TEMPLATE_VALIDATION_ROWS);
        var dvControl = ws.Range(DATA_FIRST_ROW, COL_CONTROLTYPE, nValidationLastRow, COL_CONTROLTYPE).CreateDataValidation();
        dvControl.List("\"AO,DO\"", true);
        dvControl.IgnoreBlanks = true;
        var dvRatio = ws.Range(DATA_FIRST_ROW, COL_RATIO, nValidationLastRow, COL_RATIO).CreateDataValidation();
        dvRatio.Decimal.Between(-1e12, 1e12);
        var dvMinMax = ws.Range(DATA_FIRST_ROW, COL_MIN, nValidationLastRow, COL_MAX).CreateDataValidation();
        dvMinMax.Decimal.Between(-1e12, 1e12);
        var dvSeq = ws.Range(DATA_FIRST_ROW, COL_SEQ, nValidationLastRow, COL_SEQ).CreateDataValidation();
        dvSeq.WholeNumber.GreaterThan(0);

        // Seq 欄灰底提醒「勿改」
        ws.Range(DATA_FIRST_ROW, COL_SEQ, nValidationLastRow, COL_SEQ).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F3F5");
        ws.Cell(2, COL_SEQ).Style.Fill.BackgroundColor = XLColor.FromHtml("#DEE2E6");

        // 版面
        ws.SheetView.FreezeRows(2);
        ws.Column(COL_NAME).Width = 24;
        ws.Column(COL_TAGNAME).Width = 28;
        ws.Column(COL_CONTROLTYPE).Width = 12;
        ws.Column(COL_RATIO).Width = 10;
        ws.Column(COL_UNIT).Width = 10;
        ws.Column(COL_MIN).Width = 10;
        ws.Column(COL_MAX).Width = 12;
        ws.Column(COL_DEVICE).Width = 18;
        ws.Column(COL_SEQ).Width = 22;

        // 第 1 列的 EndpointUrl / Username 可能比資料欄寬長，右鄰是標籤無法溢出 → 放寬
        SourceExcelCells.FitColumnToRow1Value(ws, 2);
        SourceExcelCells.FitColumnToRow1Value(ws, 4);
    }

    /// <summary>可解析為數字就寫數值儲存格，否則寫文字（空字串 → 留白）</summary>
    private static void WriteNumberOrText(IXLCell cell, string sz)
    {
        if (sz.Length == 0) return;
        if (SourceExcelCells.TryParseNumber(sz, out var d)) cell.Value = d;
        else cell.Value = sz;
    }
}
