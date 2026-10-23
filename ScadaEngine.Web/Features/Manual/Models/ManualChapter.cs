using System.Text.Json.Serialization;

namespace ScadaEngine.Web.Features.Manual.Models;

/// <summary>
/// _toc.json 的一章。可見性由 routes / roles 決定（見 ManualContentService.IsChapterVisible）
/// </summary>
public sealed class ManualChapter
{
    /// <summary>章節代號 = URL 最後一段 = 檔名（不含 .md），只允許小寫英數與連字號</summary>
    [JsonPropertyName("slug")]
    public string szSlug { get; set; } = "";

    [JsonPropertyName("title")]
    public string szTitle { get; set; } = "";

    /// <summary>英文標題，未填退回中文</summary>
    [JsonPropertyName("titleEn")]
    public string? szTitleEn { get; set; }

    /// <summary>目錄分群</summary>
    [JsonPropertyName("group")]
    public string szGroup { get; set; } = "";

    [JsonPropertyName("groupEn")]
    public string? szGroupEn { get; set; }

    /// <summary>
    /// 對應的功能頁路由，任一頁可進即可看本章；空 = 不綁頁面（快速開始、常見問題），一律可看。
    /// 也用於「從功能頁開手冊直接跳到對應章節」。
    /// </summary>
    [JsonPropertyName("routes")]
    public List<string> routes { get; set; } = new();

    /// <summary>限定角色（補 CanAccessPage 判不到的限制，例 /AccountSetting 實際僅 Admin、Engineer）；空 = 不限</summary>
    [JsonPropertyName("roles")]
    public List<string> roles { get; set; } = new();

    public string GetTitle(bool isEnglish) =>
        isEnglish && !string.IsNullOrWhiteSpace(szTitleEn) ? szTitleEn! : szTitle;

    public string GetGroup(bool isEnglish) =>
        isEnglish && !string.IsNullOrWhiteSpace(szGroupEn) ? szGroupEn! : szGroup;
}
