using System.Text.Json.Serialization;

namespace ScadaEngine.Web.Features.Shared.Models;

/// <summary>工作表與既有 JSON 比對後的分類（JSON 以字串輸出，前端直接比對 "Added" 等）</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SheetDiffKind
{
    /// <summary>既有 JSON 不存在 → 新增設備</summary>
    Added,

    /// <summary>既有 JSON 存在且內容有差異 → 覆寫（需使用者確認）</summary>
    Overwrite,

    /// <summary>既有 JSON 存在且內容相同 → 不寫檔</summary>
    Unchanged,

    /// <summary>工作表有格式錯誤 → 不可匯入</summary>
    Error,
}

/// <summary>單一點位的欄位變更摘要</summary>
public class SheetDiffPointChange
{
    /// <summary>1-based 點位序號（= SID 尾碼 -S{N}）</summary>
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"Modified" / "Added"（尾端新增）/ "Removed"（尾端移除）</summary>
    public string Change { get; set; } = "Modified";

    /// <summary>如「Address: 30513 → 30514, Ratio: 1 → 0.1」；Added / Removed 時為空字串</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>預覽中一張工作表的比對結果</summary>
public class SheetDiff
{
    public string SheetName { get; set; } = string.Empty;
    public SheetDiffKind Kind { get; set; }

    /// <summary>設備層欄位變更「欄位: 舊 → 新」</summary>
    public List<string> HeaderChanges { get; } = new();

    public List<SheetDiffPointChange> PointChanges { get; } = new();

    public int OldPointCount { get; set; }
    public int NewPointCount { get; set; }

    /// <summary>true = 中間插入 / 刪除 / 換順序，第 SidShiftFromIndex 點起 SID 對到不同點位（高風險，需另外勾選）</summary>
    public bool HasSidShift { get; set; }

    /// <summary>1-based；HasSidShift=false 時為 0</summary>
    public int SidShiftFromIndex { get; set; }

    /// <summary>尾端新增 / 移除的點位數（不算 SID 位移，但移除會讓該 SID 的歷史孤立）</summary>
    public int TailAddedCount { get; set; }
    public int TailRemovedCount { get; set; }

    /// <summary>以 key（如 OPC UA 的 Seq）比對時，Excel 裡找不到的既有點位數（將移除；Seq 不回收）</summary>
    public int KeyedRemovedCount { get; set; }

    /// <summary>既有 JSON 最近一次網頁熱編輯時間（顯示用；null = 沒被網頁改過）</summary>
    public string? WebEditedAt { get; set; }

    /// <summary>
    /// true = 這張工作表是「覆寫」且有實際差異，但 JSON 在網頁修改過、而 Excel 匯出時間早於該修改（或 Excel 沒有匯出時間）
    /// → 匯入會蓋掉網頁上的修改，需另外勾選確認
    /// </summary>
    public bool HasWebEditConflict { get; set; }

    public List<SheetIssue> Errors { get; } = new();
    public List<SheetIssue> Warnings { get; } = new();

    /// <summary>既有 JSON 記錄的來源 Excel 檔名（null = 舊流程手動放入或新增）</summary>
    public string? ExistingSourceWorkbook { get; set; }

    /// <summary>新增的工作表若與某個刪除候選的點位名稱完全相同 → 很可能是工作表改名（= 新 CoordinatorId + 舊的被刪）</summary>
    public string? RenameHintFrom { get; set; }

    /// <summary>預覽勾選框預設值（新增/覆寫 = true；無變更/錯誤 = false）</summary>
    public bool DefaultChecked { get; set; }
}

/// <summary>既有設定（監控資料夾中的一個 JSON）的摘要</summary>
public class ExistingSourceInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON 內 SourceWorkbook（null = 未知來源）</summary>
    public string? SourceWorkbook { get; set; }

    /// <summary>刪除候選預設勾選（同一來源 Excel 這次沒帶到 = true）</summary>
    public bool DefaultChecked { get; set; }
}

/// <summary>ImportPreview 回應</summary>
public class SourceExcelPreviewResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }

    /// <summary>提交用 token（10 分鐘有效，綁定使用者）</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>上傳檔名（含副檔名）</summary>
    public string WorkbookName { get; set; } = string.Empty;

    /// <summary>Excel 活頁簿的匯出時間（ScadaExportedAt 自訂屬性；null = 範本 / 手工建立 / 從別的檔複製工作表）</summary>
    public string? ExportedAt { get; set; }

    /// <summary>此來源是否支援刪除候選 / 逐台刪除（OPC UA 不支援：刪除走頁面上的刪除按鈕）</summary>
    public bool SupportsDelete { get; set; } = true;

    public List<SheetDiff> Sheets { get; } = new();

    /// <summary>同一來源 Excel 先前匯入、這次沒有 → 刪除候選（預設勾選）</summary>
    public List<ExistingSourceInfo> DeleteCandidates { get; } = new();

    /// <summary>其他未包含在這次上傳的既有設定（預設不勾、收合）</summary>
    public List<ExistingSourceInfo> OtherExisting { get; } = new();
}

/// <summary>預覽暫存（IMemoryCache 內，提交時取出重新比對）</summary>
public class SourceExcelPreviewSession
{
    public SourceExcelKind Kind { get; set; }
    public string Username { get; set; } = string.Empty;
    public string WorkbookName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>解析後的工作表（key = 工作表名稱）</summary>
    public Dictionary<string, ParsedSourceSheet> Sheets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, SheetDiff> Diffs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>可刪除的既有設定名稱（刪除候選 + 其他）</summary>
    public HashSet<string> Deletable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>預覽當下各既有 JSON 的內容指紋（SHA-256 hex；不存在 = 空字串）— 提交前重新比對用</summary>
    public Dictionary<string, string> Fingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>從來源資料夾匯入時的檔名（瀏覽器上傳為 null）— 全部工作表都進 JSON 後，提交時直接重產該檔</summary>
    public string? FolderFileName { get; set; }
}
