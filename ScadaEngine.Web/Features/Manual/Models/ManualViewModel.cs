namespace ScadaEngine.Web.Features.Manual.Models;

/// <summary>書架頁（/Manual）</summary>
public sealed class ManualIndexViewModel
{
    public List<ManualShelfItem> books { get; set; } = new();
}

/// <summary>書架上的一本書</summary>
public sealed class ManualShelfItem
{
    public ManualBook book { get; set; } = ManualBook.Scada;
    public string szFirstSlug { get; set; } = "";
    public int nChapterCount { get; set; }
}

/// <summary>閱讀頁（/Manual/{book}/{slug}）</summary>
public sealed class ManualViewModel
{
    public ManualBook book { get; set; } = ManualBook.Scada;

    /// <summary>登入者看得到的其他書（切換用）</summary>
    public List<ManualShelfItem> otherBooks { get; set; } = new();

    /// <summary>本書登入者看得到的章節（已依權限過濾，順序同 _toc.json）</summary>
    public List<ManualChapter> chapters { get; set; } = new();

    public ManualChapter current { get; set; } = new();

    /// <summary>章節內文 HTML（Markdig 產出，已停用 raw HTML）</summary>
    public string szContentHtml { get; set; } = "";

    /// <summary>英文介面但本章尚無英文版，顯示中文並提示</summary>
    public bool isLanguageFallback { get; set; }

    public bool isEnglish { get; set; }

    public ManualChapter? prev { get; set; }
    public ManualChapter? next { get; set; }
}
