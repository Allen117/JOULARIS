using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using ScadaEngine.Web.Features.Manual.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 線上使用手冊內容服務 — 讀 Manual/{book}/_toc.json 與各章 .md，依登入者權限過濾章節，Markdown 轉 HTML。
/// 書架、目錄、直連章節三處共用本類別的可見性判斷，不各寫一份。
/// 檔案異動依 LastWriteTime 自動失效快取（改手冊不用重啟 Web）。
/// </summary>
public class ManualContentService
{
    /// <summary>章節 slug 白名單格式（另需列在 _toc.json），雙重防路徑穿越</summary>
    private static readonly Regex _slugRegex = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>圖檔名格式：不含路徑分隔、不含 ..；不收 svg（同源 svg 可夾帶 script）</summary>
    private static readonly Regex _imageNameRegex = new(@"^[a-z0-9][a-z0-9_-]*(\.[a-z0-9_-]+)*\.(png|jpg|jpeg|gif|webp)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>待補圖標記：&lt;!-- IMG-TODO: {id} | {說明} | {其他，如 route=/EnergyReport} --&gt;（獨佔一行）</summary>
    private static readonly Regex _imgTodoRegex = new(
        @"^[ \t]*<!--\s*IMG-TODO:\s*(?<id>[^|]+?)\s*\|\s*(?<cap>[^|]+?)\s*(\|[^\r\n]*?)?-->[ \t]*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>其他 HTML 註解（寫作者備忘）直接移除，避免 DisableHtml 後被當文字顯示</summary>
    private static readonly Regex _commentRegex = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    private const string ImgTodoToken = "MANUALIMGTODO";

    /// <summary>中文不轉 &amp;#x...; 實體（Default 只放行 Basic Latin）</summary>
    private static readonly HtmlEncoder _encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseListExtras()
        .DisableHtml()
        .Build();

    private readonly string _szRootPath;
    private readonly ILogger<ManualContentService>? _logger;
    private readonly ConcurrentDictionary<string, (DateTime dtWrite, List<ManualChapter> chapters)> _tocCache = new();
    private readonly ConcurrentDictionary<string, (DateTime dtWrite, string szHtml)> _htmlCache = new();

    public ManualContentService(string szRootPath, ILogger<ManualContentService>? logger = null)
    {
        _szRootPath = szRootPath;
        _logger = logger;
    }

    // ─────────────────────────── 權限 ───────────────────────────

    /// <summary>
    /// 章節可見 = (roles 未設 或 登入者角色在 roles 內) 且 (routes 為空 或 任一 route CanAccessPage)
    /// </summary>
    public static bool IsChapterVisible(ClaimsPrincipal user, ManualChapter chapter)
    {
        if (chapter.roles.Count > 0 && !chapter.roles.Any(user.IsInRole))
            return false;
        if (chapter.routes.Count == 0)
            return true;
        return chapter.routes.Any(r => PermissionService.CanAccessPage(user, r));
    }

    /// <summary>本書登入者看得到的章節；書本身看不到時回空清單</summary>
    public List<ManualChapter> GetVisibleChapters(ClaimsPrincipal user, ManualBook book)
    {
        if (user.Identity?.IsAuthenticated != true) return new();
        if (book.isEngineerOnly && !PermissionService.IsEngineer(user)) return new();

        var all = LoadToc(book);
        if (book.isEngineerOnly) return all;

        var visible = all.Where(c => IsChapterVisible(user, c)).ToList();
        // 書可見 = 至少一章「有綁頁面且可見」；只剩快速開始 / 常見問題這類不綁頁章節的書不顯示
        return visible.Any(c => c.routes.Count > 0) ? visible : new();
    }

    public bool IsBookVisible(ClaimsPrincipal user, ManualBook book) =>
        GetVisibleChapters(user, book).Count > 0;

    /// <summary>
    /// 判斷章節存取結果：整本看不到 → Forbidden（不論 slug，避免以 404 / 403 差異探出章節清單）；
    /// 不在目錄或 slug 不合法 → NotFound；在目錄但本章看不到 → Forbidden
    /// </summary>
    public ManualAccessResult ResolveChapter(ClaimsPrincipal user, ManualBook book, string? szSlug, out ManualChapter? chapter)
    {
        chapter = null;
        var visible = GetVisibleChapters(user, book);
        if (visible.Count == 0) return ManualAccessResult.Forbidden;
        if (string.IsNullOrEmpty(szSlug) || !_slugRegex.IsMatch(szSlug)) return ManualAccessResult.NotFound;

        var target = LoadToc(book).FirstOrDefault(c => c.szSlug == szSlug);
        if (target == null) return ManualAccessResult.NotFound;

        if (!visible.Any(c => c.szSlug == szSlug)) return ManualAccessResult.Forbidden;

        chapter = target;
        return ManualAccessResult.Ok;
    }

    /// <summary>
    /// 從功能頁開手冊：找登入者看得到、routes 含該頁的章節（依 SCADA → EMS → 工程師順序，
    /// 但工程師專屬頁優先找工程師手冊）。找不到回 null。
    /// </summary>
    public (ManualBook book, ManualChapter chapter)? FindChapterByRoute(ClaimsPrincipal user, string? szRoute)
    {
        if (string.IsNullOrEmpty(szRoute)) return null;

        var books = PermissionService.IsEngineerRoute(szRoute)
            ? new[] { ManualBook.Engineer, ManualBook.Scada, ManualBook.Ems }
            : ManualBook.All;

        foreach (var book in books)
        {
            var hit = GetVisibleChapters(user, book)
                .FirstOrDefault(c => c.routes.Contains(szRoute, StringComparer.OrdinalIgnoreCase));
            if (hit != null) return (book, hit);
        }
        return null;
    }

    // ─────────────────────────── 內容 ───────────────────────────

    /// <summary>
    /// 讀章節並轉 HTML。英文介面優先讀 {book}/en/{slug}.md，沒有就退回中文（isFallback=true）。
    /// 呼叫前須先經 ResolveChapter 確認可見。
    /// </summary>
    public string RenderChapter(ManualBook book, ManualChapter chapter, bool isEnglish, string szImgTodoLabel, out bool isFallback)
    {
        isFallback = false;
        var szPath = Path.Combine(_szRootPath, book.szKey, chapter.szSlug + ".md");
        if (isEnglish)
        {
            var szEnPath = Path.Combine(_szRootPath, book.szKey, "en", chapter.szSlug + ".md");
            if (File.Exists(szEnPath)) szPath = szEnPath;
            else isFallback = true;
        }

        if (!File.Exists(szPath))
        {
            _logger?.LogWarning("手冊章節檔不存在：{Path}", szPath);
            return "";
        }

        var dtWrite = File.GetLastWriteTimeUtc(szPath);
        var szCacheKey = szPath + "|" + szImgTodoLabel;
        if (_htmlCache.TryGetValue(szCacheKey, out var cached) && cached.dtWrite == dtWrite)
            return cached.szHtml;

        var szHtml = RenderMarkdown(File.ReadAllText(szPath), szImgTodoLabel);
        _htmlCache[szCacheKey] = (dtWrite, szHtml);
        return szHtml;
    }

    /// <summary>
    /// Markdown → HTML：IMG-TODO 標記先換成佔位字、其餘註解移除，Markdig（停用 raw HTML）轉換後再把佔位字換成提示框
    /// </summary>
    public static string RenderMarkdown(string szMarkdown, string szImgTodoLabel)
    {
        var todos = new List<(string szId, string szCaption)>();
        var szText = _imgTodoRegex.Replace(szMarkdown, m =>
        {
            todos.Add((m.Groups["id"].Value.Trim(), m.Groups["cap"].Value.Trim()));
            return "\n" + ImgTodoToken + (todos.Count - 1) + "\n";
        });
        szText = _commentRegex.Replace(szText, "");

        var szHtml = Markdown.ToHtml(szText, _pipeline);

        for (int i = 0; i < todos.Count; i++)
        {
            var (szId, szCaption) = todos[i];
            var szBox = $"<div class=\"manual-img-todo\" data-img-id=\"{_encoder.Encode(szId)}\">" +
                        $"<i class=\"fas fa-image me-2\"></i><span class=\"manual-img-todo-label\">{_encoder.Encode(szImgTodoLabel)}</span>" +
                        $"{_encoder.Encode(szCaption)}</div>";
            szHtml = szHtml.Replace($"<p>{ImgTodoToken}{i}</p>", szBox);
        }
        return szHtml;
    }

    /// <summary>圖檔實體路徑（Manual/{book}/images/{name}）；檔名不合法或不存在回 null。呼叫前須先確認書可見</summary>
    public string? GetImagePath(ManualBook book, string? szFileName)
    {
        if (string.IsNullOrEmpty(szFileName) || !_imageNameRegex.IsMatch(szFileName)) return null;
        var szPath = Path.Combine(_szRootPath, book.szKey, "images", szFileName);
        return File.Exists(szPath) ? szPath : null;
    }

    /// <summary>讀 _toc.json（快取，檔案異動自動重讀）；slug 格式不合法的章節丟棄並記 log</summary>
    public List<ManualChapter> LoadToc(ManualBook book)
    {
        var szPath = Path.Combine(_szRootPath, book.szKey, "_toc.json");
        if (!File.Exists(szPath))
        {
            _logger?.LogWarning("手冊目錄檔不存在：{Path}", szPath);
            return new();
        }

        var dtWrite = File.GetLastWriteTimeUtc(szPath);
        if (_tocCache.TryGetValue(szPath, out var cached) && cached.dtWrite == dtWrite)
            return cached.chapters;

        List<ManualChapter> chapters;
        try
        {
            chapters = JsonSerializer.Deserialize<List<ManualChapter>>(File.ReadAllText(szPath),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "手冊目錄檔格式錯誤：{Path}", szPath);
            return new();
        }

        var valid = new List<ManualChapter>();
        foreach (var c in chapters)
        {
            if (!_slugRegex.IsMatch(c.szSlug ?? "") || valid.Any(v => v.szSlug == c.szSlug))
            {
                _logger?.LogWarning("手冊目錄 {Book} 略過不合法或重複的 slug：{Slug}", book.szKey, c.szSlug);
                continue;
            }
            valid.Add(c);
        }

        _tocCache[szPath] = (dtWrite, valid);
        return valid;
    }
}
