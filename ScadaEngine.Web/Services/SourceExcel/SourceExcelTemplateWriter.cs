using ClosedXML.Excel;
using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// 範本與匯出的 .xlsx 產生器（ClosedXML，無 Office 依賴）。版面由各 adapter 的 WriteSheet 提供，這裡只負責組 workbook。
/// 一個 JSON = 一張工作表；沒有任何工作表時產生一張空白範本（adapter.DefaultSheetName）。
/// </summary>
public static class SourceExcelTemplateWriter
{
    /// <summary>Excel 工作表名稱上限</summary>
    private const int MAX_SHEET_NAME_LENGTH = 31;

    /// <summary>空白範本（不帶匯出時間 — 用範本建的檔視為「不明來源」，覆寫網頁修改時會警告）</summary>
    public static byte[] BuildTemplate(ISourceExcelAdapter adapter) => Build(adapter, Array.Empty<ParsedSourceSheet>());

    /// <summary>
    /// 匯出：每張 sheet 一個工作表。名稱不合 Excel 規則（超過 31 字、含 []:*?/\）的 sheet 會被略過並回報在 skipped。
    /// isExport = true 時在活頁簿自訂屬性寫入 ScadaExportedAt（匯入時用來判斷是否會蓋掉網頁修改）；
    /// exportedAt 指定匯出時間（寫入來源資料夾時要讓檔案修改時間與它對齊），null = 現在。
    /// </summary>
    public static byte[] Build(ISourceExcelAdapter adapter, IEnumerable<ParsedSourceSheet> sheets, List<string>? skipped = null, bool isExport = false, DateTimeOffset? exportedAt = null)
    {
        using var workbook = new XLWorkbook();
        var nAdded = 0;
        if (isExport)
            SourceJsonStamp.StampExported(workbook, exportedAt);

        foreach (var sheet in sheets)
        {
            if (!IsValidSheetName(sheet.SheetName))
            {
                skipped?.Add(sheet.SheetName);
                continue;
            }

            var ws = workbook.Worksheets.Add(sheet.SheetName);
            adapter.WriteSheet(ws, sheet);
            nAdded++;
        }

        if (nAdded == 0)
        {
            var ws = workbook.Worksheets.Add(adapter.DefaultSheetName);
            adapter.WriteSheet(ws, null);
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Excel 工作表名稱規則：1~31 字、不含 [ ] : * ? / \</summary>
    public static bool IsValidSheetName(string? szName)
    {
        if (string.IsNullOrWhiteSpace(szName) || szName.Length > MAX_SHEET_NAME_LENGTH) return false;
        return szName.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) < 0;
    }
}
