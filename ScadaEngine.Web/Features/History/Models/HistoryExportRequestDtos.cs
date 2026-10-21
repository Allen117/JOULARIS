namespace ScadaEngine.Web.Features.History.Models;

/// <summary>
/// POST /api/history/export 請求 — 與趨勢頁查詢同一組參數（點位 / 起訖 / 間隔），伺服器重查後產檔。
/// </summary>
public class HistoryExportRequest
{
    public List<string> sids { get; set; } = new();
    public string szStart { get; set; } = string.Empty;
    public string szEnd { get; set; } = string.Empty;
    public int nInterval { get; set; }
}
