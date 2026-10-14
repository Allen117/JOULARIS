using System.Globalization;
using ClosedXML.Excel;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// ClosedXML 儲存格讀取與值正規化的共用 helper（Modbus / DB adapter 共用，純函數）。
///
/// 正規化規則：所有值都轉成字串比對 —
/// - 數值儲存格 → 不帶尾零的最短表示（40001.0 → "40001"、0.10 → "0.1"），文字儲存格 → Trim 原文
/// - 布林儲存格 → "TRUE" / "FALSE"
/// 這樣 Excel（數值）與 JSON（字串或數值）兩邊讀出來的點位欄位才會一致，匯出 → 匯入才能判成「無變更」。
/// </summary>
public static class SourceExcelCells
{
    /// <summary>第 1 列標籤掃描範圍（欄數）</summary>
    private const int ROW1_SCAN_COLUMNS = 40;

    /// <summary>讀儲存格為正規化字串（空白 / 錯誤 → 空字串）</summary>
    public static string ReadString(IXLWorksheet ws, int nRow, int nCol) => ReadString(ws.Cell(nRow, nCol));

    public static string ReadString(IXLCell cell)
    {
        var v = cell.Value;
        switch (v.Type)
        {
            case XLDataType.Blank:
            case XLDataType.Error:
                return string.Empty;
            case XLDataType.Number:
                return FormatNumber(v.GetNumber());
            case XLDataType.Boolean:
                return v.GetBoolean() ? "TRUE" : "FALSE";
            case XLDataType.DateTime:
                return v.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case XLDataType.TimeSpan:
                return v.GetTimeSpan().ToString();
            default:
                return v.GetText().Trim();
        }
    }

    /// <summary>double → 最短無尾零表示（InvariantCulture）</summary>
    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return string.Empty;
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>可解析為數字的字串 → 正規化表示；否則原樣（Trim）回傳。呼叫端另以 <see cref="TryParseNumber"/> 判斷合法性</summary>
    public static string NormalizeNumber(string? sz)
    {
        var szTrim = (sz ?? string.Empty).Trim();
        return TryParseNumber(szTrim, out var d) ? FormatNumber(d) : szTrim;
    }

    public static bool TryParseNumber(string? sz, out double d)
        => double.TryParse((sz ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    /// <summary>正整數（> 0）解析；失敗回 false</summary>
    public static bool TryParsePositiveInt(string? sz, out int n)
    {
        n = 0;
        if (!TryParseNumber(sz, out var d)) return false;
        if (d <= 0 || d > int.MaxValue || Math.Abs(d - Math.Round(d)) > double.Epsilon) return false;
        n = (int)Math.Round(d);
        return true;
    }

    /// <summary>
    /// 布林正規化：空白 → TRUE（與巨集相同）；TRUE/1/是/Y/YES → "TRUE"；FALSE/0/否/N/NO → "FALSE"；其他 → isValid=false
    /// </summary>
    public static string NormalizeBool(string? sz, out bool isValid)
    {
        isValid = true;
        var szUpper = (sz ?? string.Empty).Trim().ToUpperInvariant();
        switch (szUpper)
        {
            case "":
            case "TRUE":
            case "1":
            case "是":
            case "Y":
            case "YES":
                return "TRUE";
            case "FALSE":
            case "0":
            case "否":
            case "N":
            case "NO":
                return "FALSE";
            default:
                isValid = false;
                return szUpper;
        }
    }

    /// <summary>
    /// 第 1 列標籤掃描：找到文字等於任一 label（不分大小寫、Trim）的儲存格，回傳其右側儲存格的值；找不到回 null。
    /// 讓舊版面（標籤位置不同）與新範本都能讀。
    /// </summary>
    public static string? FindRow1Value(IXLWorksheet ws, params string[] labels)
    {
        for (var nCol = 1; nCol <= ROW1_SCAN_COLUMNS; nCol++)
        {
            var cell = ws.Cell(1, nCol);
            if (cell.Value.Type != XLDataType.Text) continue;
            var szText = cell.Value.GetText().Trim();
            if (labels.Any(l => string.Equals(l, szText, StringComparison.OrdinalIgnoreCase)))
                return ReadString(ws, 1, nCol + 1);
        }
        return null;
    }

    /// <summary>名稱欄（nNameCol）最後一筆非空白所在列；沒有資料回 nFirstRow-1</summary>
    public static int LastDataRow(IXLWorksheet ws, int nNameCol, int nFirstRow)
    {
        var nLastUsed = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = nLastUsed; r >= nFirstRow; r--)
        {
            if (ReadString(ws, r, nNameCol).Length > 0) return r;
        }
        return nFirstRow - 1;
    }

    /// <summary>該列指定欄範圍是否全空白</summary>
    public static bool IsRowBlank(IXLWorksheet ws, int nRow, int nFirstCol, int nLastCol)
    {
        for (var c = nFirstCol; c <= nLastCol; c++)
        {
            if (ReadString(ws, nRow, c).Length > 0) return false;
        }
        return true;
    }

    public static string ColumnLetter(int nCol) => XLHelper.GetColumnLetterFromNumber(nCol);

    /// <summary>範本 / 匯出共用的表頭樣式</summary>
    public static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9ECEF");
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    /// <summary>設定列標籤（第 1 列 Key/Value 對）樣式</summary>
    public static void StyleLabel(IXLCell cell)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#495057");
    }

    /// <summary>
    /// 第 1 列設定值（IP、ModbusID 清單、EndpointUrl 等長文字）所在欄若比內容窄，放寬到容得下。
    /// 第 1 列右鄰是下一個標籤，文字無法溢出到隔壁，欄寬不足會被截斷看起來像值不完整。
    /// 須在資料欄寬設定之後呼叫（只放寬、不縮小）。
    /// </summary>
    public static void FitColumnToRow1Value(IXLWorksheet ws, int nCol)
    {
        var szText = ReadString(ws, 1, nCol);
        if (szText.Length == 0) return;
        var dNeeded = szText.Length + 2;
        if (ws.Column(nCol).Width < dNeeded)
            ws.Column(nCol).Width = dNeeded;
    }
}
