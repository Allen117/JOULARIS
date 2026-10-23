namespace ScadaEngine.Web.Features.Manual.Models;

/// <summary>
/// 手冊書別定義 — 書目固定三本，內容在 Manual/{szKey}/ 下（_toc.json + 各章 .md）
/// </summary>
public sealed class ManualBook
{
    /// <summary>URL 與資料夾名稱（小寫），例 "ems"</summary>
    public string szKey { get; init; } = "";

    /// <summary>書名 resx key（Index / Book 兩個 View 的 resx 都要有）</summary>
    public string szTitleKey { get; init; } = "";

    /// <summary>簡介 resx key（書架卡片用）</summary>
    public string szDescKey { get; init; } = "";

    /// <summary>Font Awesome 圖示 class</summary>
    public string szIcon { get; init; } = "";

    /// <summary>僅 Engineer 角色可讀（整本，含章節與圖檔）</summary>
    public bool isEngineerOnly { get; init; }

    /// <summary>套 EMS 淡綠主題</summary>
    public bool isEmsTheme { get; init; }

    public static readonly ManualBook Scada = new()
    {
        szKey = "scada", szTitleKey = "manual.book.scada", szDescKey = "manual.book.scada_desc",
        szIcon = "fas fa-industry",
    };

    public static readonly ManualBook Ems = new()
    {
        szKey = "ems", szTitleKey = "manual.book.ems", szDescKey = "manual.book.ems_desc",
        szIcon = "fas fa-leaf", isEmsTheme = true,
    };

    public static readonly ManualBook Engineer = new()
    {
        szKey = "engineer", szTitleKey = "manual.book.engineer", szDescKey = "manual.book.engineer_desc",
        szIcon = "fas fa-tools", isEngineerOnly = true,
    };

    /// <summary>書架顯示順序</summary>
    public static readonly ManualBook[] All = [Scada, Ems, Engineer];

    /// <summary>依 URL 書別找書（不分大小寫），找不到回 null</summary>
    public static ManualBook? Find(string? szKey)
    {
        if (string.IsNullOrEmpty(szKey)) return null;
        return All.FirstOrDefault(b => string.Equals(b.szKey, szKey, StringComparison.OrdinalIgnoreCase));
    }
}
