using System.Globalization;
using System.Text.Json.Nodes;
using ClosedXML.Excel;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// 「網頁修改時間 vs Excel 匯出時間」的小工具（plan 2026-10-12 決策 2）：
///
/// - 網頁熱編輯（Modbus / DB / OPC UA）寫入 JSON 頂層 <c>WebEditedAt</c>（ISO 8601 含時區，如 2026-10-12T14:03:21.1234567+08:00）
/// - 匯出的 Excel 在活頁簿自訂屬性寫入 <c>ScadaExportedAt</c>（同格式；使用者在 Excel 內修改、另存新檔都會保留）
/// - 匯入預覽時：某張工作表屬「覆寫」且有實際差異，而 JSON 的 WebEditedAt 晚於 Excel 匯出時間（或 Excel 沒有匯出時間，
///   例如範本或手工建立的檔案）→ 標示「網頁修改過」並要求另外勾選確認
/// - 匯入寫檔不帶 WebEditedAt，代表 Excel 與 JSON 重新一致
///
/// Engine 各 loader 反序列化時忽略未知欄位（SourceWorkbook / NextSeq 已是先例），WebEditedAt 不影響採集。
/// </summary>
public static class SourceJsonStamp
{
    /// <summary>JSON 頂層欄位名稱</summary>
    public const string WEB_EDITED_AT = "WebEditedAt";

    /// <summary>JSON 頂層欄位名稱 — 來源 Excel 檔名</summary>
    public const string SOURCE_WORKBOOK = "SourceWorkbook";

    /// <summary>JSON 頂層欄位名稱 — 網頁改名前的舊名稱清單（字串陣列，最近的在後）</summary>
    public const string RENAMED_FROM = "RenamedFrom";

    /// <summary>活頁簿自訂屬性名稱</summary>
    public const string EXPORTED_AT_PROPERTY = "ScadaExportedAt";

    /// <summary>目前時間的字串表示（ISO 8601 round-trip 格式，含本地時區）</summary>
    public static string Now() => Format(DateTimeOffset.Now);

    public static string Format(DateTimeOffset dt) => dt.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>解析 ISO 8601 字串；空白或格式錯誤回 null</summary>
    public static DateTimeOffset? Parse(string? sz)
    {
        if (string.IsNullOrWhiteSpace(sz)) return null;
        return DateTimeOffset.TryParse(sz.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
    }

    /// <summary>在 JSON 頂層寫入 WebEditedAt = 現在</summary>
    public static void StampWebEdited(JsonObject root) => root[WEB_EDITED_AT] = Now();

    /// <summary>讀 JSON 頂層 WebEditedAt；沒有或壞掉回 null</summary>
    public static DateTimeOffset? ReadWebEdited(JsonObject root) => Parse(root[WEB_EDITED_AT]?.ToString());

    /// <summary>讀 JSON 頂層 RenamedFrom；沒有或格式不符回空清單</summary>
    public static List<string> ReadRenamedFrom(JsonObject root)
        => root[RENAMED_FROM] is JsonArray array
            ? array.Select(n => n?.ToString() ?? string.Empty).Where(n => n.Length > 0).ToList()
            : new List<string>();

    /// <summary>在 RenamedFrom 追加一個舊名稱（不重複，不分大小寫）</summary>
    public static void AppendRenamedFrom(JsonObject root, string szOldName)
    {
        var names = ReadRenamedFrom(root);
        names.RemoveAll(n => string.Equals(n, szOldName, StringComparison.OrdinalIgnoreCase));
        names.Add(szOldName);
        var array = new JsonArray();
        foreach (var szName in names) array.Add(szName);
        root[RENAMED_FROM] = array;
    }

    /// <summary>匯出時在活頁簿寫入 ScadaExportedAt 自訂屬性</summary>
    public static void StampExported(IXLWorkbook workbook, DateTimeOffset? now = null)
        => workbook.CustomProperties.Add(EXPORTED_AT_PROPERTY, Format(now ?? DateTimeOffset.Now));

    /// <summary>讀活頁簿的 ScadaExportedAt 自訂屬性；沒有（範本、手工建立、從別的檔複製工作表）回 null</summary>
    public static DateTimeOffset? ReadExported(IXLWorkbook workbook)
    {
        try
        {
            var prop = workbook.CustomProperties.FirstOrDefault(p =>
                string.Equals(p.Name, EXPORTED_AT_PROPERTY, StringComparison.OrdinalIgnoreCase));
            return prop == null ? null : Parse(prop.Value?.ToString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 是否「拿舊 Excel 蓋掉網頁修改」：JSON 曾被網頁修改（有 WebEditedAt），且 Excel 沒有匯出時間、或匯出時間早於網頁修改。
    /// 沒被網頁修改過 → false；先匯出最新版再修改（匯出時間 ≥ 修改時間）→ false。
    /// </summary>
    public static bool IsWebEditConflict(DateTimeOffset? webEditedAt, DateTimeOffset? exportedAt)
    {
        if (!webEditedAt.HasValue) return false;
        if (!exportedAt.HasValue) return true;
        return webEditedAt.Value > exportedAt.Value;
    }

    /// <summary>顯示用：本地時間 yyyy-MM-dd HH:mm:ss</summary>
    public static string ToDisplay(DateTimeOffset dt) => dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
