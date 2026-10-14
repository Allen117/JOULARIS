namespace ScadaEngine.Web.Features.Shared.Models;

/// <summary>Excel 匯入支援的來源種類（各自一個 adapter）</summary>
public enum SourceExcelKind
{
    Modbus,
    DbPoint,
    OpcUa,
}

/// <summary>
/// 一張工作表（= 一台設備 / 一個 Coordinator = 一個 JSON）解析後的來源無關中介表示。
/// Excel 與既有 JSON 都解析成這個型別再比對，欄位值一律正規化後的字串（數值去尾零、布林大寫）。
/// </summary>
public class ParsedSourceSheet
{
    /// <summary>工作表名稱 = 設備名稱 = JSON 檔名（不含副檔名）</summary>
    public string SheetName { get; set; } = string.Empty;

    /// <summary>設備層欄位（Modbus：IP/Port/ModbusId/ConnectTimeout；DB：PollingInterval/ConnectTimeout/MonitorEnabled）</summary>
    public Dictionary<string, string> Header { get; } = new(StringComparer.Ordinal);

    /// <summary>點位（陣列順序即 SID 序號）</summary>
    public List<ParsedSourcePoint> Points { get; } = new();

    /// <summary>阻擋性錯誤 — 有任一筆即不可匯入該張工作表</summary>
    public List<SheetIssue> Errors { get; } = new();

    /// <summary>提示（空白列略過等），不阻擋匯入</summary>
    public List<SheetIssue> Warnings { get; } = new();

    /// <summary>來源 Excel 檔名（由既有 JSON 的 SourceWorkbook 讀出；Excel 解析時為 null）</summary>
    public string? SourceWorkbook { get; set; }

    /// <summary>最近一次網頁熱編輯時間（由既有 JSON 的 WebEditedAt 讀出；Excel 解析時為 null；匯入寫檔後清除）</summary>
    public DateTimeOffset? WebEditedAt { get; set; }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>單一點位：來源列號 + 欄位名→正規化字串值</summary>
public class ParsedSourcePoint
{
    /// <summary>Excel 列號（1-based；由 JSON 解析時為陣列索引+1）</summary>
    public int RowNumber { get; set; }

    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

    public string Name => Fields.TryGetValue("Name", out var sz) ? sz : string.Empty;

    public string Get(string szField) => Fields.TryGetValue(szField, out var sz) ? sz : string.Empty;
}

/// <summary>工作表解析問題（錯誤或提示）— adapter 只給 key + args，由 Coordinator 依 culture 翻成 Message</summary>
public class SheetIssue
{
    /// <summary>1-based 列號；0 表示與列無關</summary>
    public int Row { get; set; }

    /// <summary>Excel 欄位字母（A/B/…）；空字串表示與欄無關</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>資源鍵（srcxl.err.* / srcxl.warn.*）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>格式化參數（{0} {1} …）</summary>
    public object[] Args { get; set; } = Array.Empty<object>();

    /// <summary>已翻譯的顯示文字（Coordinator 填入）</summary>
    public string Message { get; set; } = string.Empty;

    public static SheetIssue At(int nRow, string szColumn, string szKey, params object[] args)
        => new() { Row = nRow, Column = szColumn, Key = szKey, Args = args };

    public static SheetIssue General(string szKey, params object[] args)
        => new() { Row = 0, Column = string.Empty, Key = szKey, Args = args };
}
