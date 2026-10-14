namespace ScadaEngine.Web.Features.Shared.Models;

/// <summary>POST {base}/ImportCommit 請求 — 帶預覽 token 與使用者勾選結果</summary>
public class SourceExcelCommitRequest
{
    public string Token { get; set; } = string.Empty;

    /// <summary>要匯入（新增 + 覆寫）的工作表名稱</summary>
    public List<string> ImportSheets { get; set; } = new();

    /// <summary>要刪除的既有設定名稱</summary>
    public List<string> DeleteNames { get; set; } = new();

    /// <summary>使用者已勾選「我了解 SID 位移後果」</summary>
    public bool AcknowledgeSidShift { get; set; }

    /// <summary>使用者已勾選「我了解會覆寫網頁上的修改」（拿舊 Excel 重新匯入時）</summary>
    public bool AcknowledgeWebEdit { get; set; }
}

/// <summary>POST {base}/DeleteSource 請求</summary>
public class SourceExcelDeleteRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>ImportCommit / DeleteSource 回應</summary>
public class SourceExcelCommitResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>true = 預覽後設定已被更動，需重新預覽（HTTP 409）</summary>
    public bool Conflict { get; set; }

    public List<string> Imported { get; } = new();
    public List<string> Deleted { get; } = new();

    /// <summary>資料夾內 Excel 未自動重產的提示（被 Excel 開啟 / 有未匯入修改 / 非系統匯出）；全部成功或無受影響檔案為 null</summary>
    public string? WorkbookNotice { get; set; }
}

/// <summary>POST {base}/ImportPreviewFromFolder 請求 — 以來源資料夾內的 Excel 檔預覽（不經瀏覽器上傳）</summary>
public class SourceExcelFolderImportRequest
{
    /// <summary>資料夾內檔名（含副檔名，不含路徑）</summary>
    public string FileName { get; set; } = string.Empty;
}

/// <summary>POST {base}/ExportToFolder 回應</summary>
public class SourceExcelFolderExportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>實際寫入的完整路徑（成功時；前端直接顯示，不自行組路徑）</summary>
    public string? Path { get; set; }

    /// <summary>true = 資料夾內同名檔有未匯入修改或非系統匯出，需使用者確認後帶 force=true 重送（HTTP 409）</summary>
    public bool NeedsConfirm { get; set; }

    /// <summary>true = 檔案被 Excel 開啟鎖住（HTTP 409）</summary>
    public bool Locked { get; set; }

    /// <summary>沒進活頁簿的設定（已含理由的顯示文字：JSON 無法解析 / 名稱不能當工作表名）；Message 已併入，前端不必再組</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>GET {base}/FolderWorkbooks 回應項目 — 來源資料夾內的 Excel 檔（不含範本）</summary>
public class SourceExcelFolderWorkbook
{
    public string Name { get; set; } = string.Empty;

    /// <summary>檔案修改時間（顯示用 yyyy-MM-dd HH:mm:ss）</summary>
    public string ModifiedAt { get; set; } = string.Empty;

    /// <summary>活頁簿 ScadaExportedAt（顯示用；非系統匯出為 null）</summary>
    public string? ExportedAt { get; set; }

    /// <summary>系統匯出後又在 Excel 存檔過、尚未在網頁匯入（修改時間晚於 ScadaExportedAt）</summary>
    public bool HasUnimportedChanges { get; set; }
}

/// <summary>共用 partial `_SourceExcelImport.cshtml` 的 ViewModel（工具列 + 預覽 Modal）</summary>
public class SourceExcelImportViewModel
{
    /// <summary>Controller 路由前綴，如 "/ModbusCoordinator"</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>"modbus" / "dbpoint" — 前端用來切換說明文字</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>既有設定的來源 Excel 檔名（不重複）— 匯出下拉用</summary>
    public List<string> Workbooks { get; set; } = new();
}
