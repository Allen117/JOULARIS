namespace ScadaEngine.Web.Features.Manual.Models;

/// <summary>章節存取判斷結果</summary>
public enum ManualAccessResult
{
    Ok,
    /// <summary>不在目錄（或 slug 不合法）→ 404</summary>
    NotFound,
    /// <summary>在目錄但登入者看不到 → AccessDenied</summary>
    Forbidden,
}
