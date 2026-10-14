using System.Text;
using ClosedXML.Excel;
using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// 各來源（Modbus / DB / OPC UA）要實作的 Excel ⇄ JSON 轉換介面。
/// 實作必須是**純函數、無 I/O、無 DI 依賴**（Singleton 註冊、可直接單元測試）：
/// 預覽、token、確認、提交前重新比對、寫檔、刪檔、Modal UI 都在共用的 SourceExcelImportCoordinator，
/// adapter 只負責「版面解析 + 驗證規則 + JSON 序列化 + 範本版面」。
///
/// 點位比對方式由 <see cref="PointKeyField"/> 決定：
/// - null（Modbus / DB）：位置式 — 陣列索引 = SID 序號，中間插入 / 刪除視為 SID 位移
/// - 非 null（OPC UA 的 "Seq"）：鍵值式 — 以該欄位對點，不做位移偵測；配號 / 對回既有點位在 <see cref="Reconcile"/> 完成
/// </summary>
public interface ISourceExcelAdapter
{
    SourceExcelKind Kind { get; }

    /// <summary>appsettings 區段（SourceConfigFileIo.MODBUS_SECTION / DBPOINT_SECTION / OPCUA_SECTION）</summary>
    string ConfigSection { get; }

    /// <summary>點位比對 key 欄位名稱（null = 位置式比對）</summary>
    string? PointKeyField => null;

    /// <summary>
    /// 是否支援「同檔刪除候選」與逐台刪除（移到 _deleted/）。
    /// OPC UA 的刪除語意是「刪 JSON + 刪 DB 列」，走頁面刪除按鈕，不走匯入流程（plan 2026-10-12 決策 6）。
    /// </summary>
    bool SupportsDeleteCandidates => true;

    /// <summary>下載範本的檔名（含 .xlsx）</summary>
    string TemplateFileName { get; }

    /// <summary>匯出檔名前綴（含 .xlsx 由呼叫端補）</summary>
    string ExportFileNamePrefix { get; }

    /// <summary>空白範本的工作表名稱</summary>
    string DefaultSheetName { get; }

    /// <summary>設備層欄位（比對差異的顯示順序）</summary>
    IReadOnlyList<string> HeaderFields { get; }

    /// <summary>點位欄位（比對差異的顯示順序；必含 "Name"）</summary>
    IReadOnlyList<string> PointFields { get; }

    /// <summary>新建 JSON 檔的編碼（既有檔沿用原編碼）</summary>
    Encoding NewFileEncoding { get; }

    /// <summary>解析一張工作表（含驗證；錯誤/提示寫進 sheet.Errors / Warnings，不丟例外）</summary>
    ParsedSourceSheet ParseSheet(IXLWorksheet ws);

    /// <summary>把既有 JSON 解析成同樣的中介表示（供比對與匯出）；格式壞掉回 null</summary>
    ParsedSourceSheet? ParseJson(string szName, string szJson);

    /// <summary>
    /// 預覽時、比對前的對帳步驟（existing = 既有 JSON 解析結果，不存在為 null）。
    /// 位置式 adapter 不需實作；鍵值式 adapter（OPC UA）在此把 Excel 列對回既有 Seq、為新列配號、
    /// 把「未知 Seq」等需要既有資料才能判定的錯誤寫進 sheet.Errors。必須是純函數（可重入、不做 I/O）。
    /// </summary>
    void Reconcile(ParsedSourceSheet sheet, ParsedSourceSheet? existing) { }

    /// <summary>序列化為 Engine 可載入的 JSON 文字；szSourceWorkbook 非空時寫入頂層 SourceWorkbook</summary>
    string ToJson(ParsedSourceSheet sheet, string? szSourceWorkbook);

    /// <summary>
    /// 需要既有 JSON 才能序列化的來源（OPC UA：保留原密碼、NextSeq）用這個多載；預設轉呼叫舊版。
    /// szExistingJson = 提交當下重讀的既有檔內容（與預覽指紋一致），不存在為 null。
    /// </summary>
    string ToJson(ParsedSourceSheet sheet, string? szSourceWorkbook, string? szExistingJson) => ToJson(sheet, szSourceWorkbook);

    /// <summary>把工作表版面寫進 ws（sheet=null → 空白範本，含預設值與下拉驗證）</summary>
    void WriteSheet(IXLWorksheet ws, ParsedSourceSheet? sheet);
}
