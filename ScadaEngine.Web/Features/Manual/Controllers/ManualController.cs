using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Features.Manual.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.Manual.Controllers;

/// <summary>
/// 線上使用手冊 — SCADA / EMS 依登入者頁面權限顯示章節，工程師手冊僅 Engineer。
/// 權限一律在此判斷（PageAccessFilter 只做路徑完全比對，擋不到 /Manual/{book}/{slug}）。
/// </summary>
[Authorize]
[Route("Manual")]
public class ManualController : Controller
{
    private const string AccessDeniedPath = "/Login/AccessDenied";

    private readonly ManualContentService _service;
    private readonly IStringLocalizer<ManualController> _l;

    public ManualController(ManualContentService service, IStringLocalizer<ManualController> l)
    {
        _service = service;
        _l = l;
    }

    /// <summary>
    /// 書架。帶 from=功能頁路由時直接跳到對應章節；找不到則跳到該頁所屬的書（EMS / 工程師 / SCADA），
    /// 那本也看不到才顯示書架。
    /// </summary>
    [HttpGet("")]
    public IActionResult Index(string? from)
    {
        if (!string.IsNullOrEmpty(from))
        {
            var hit = _service.FindChapterByRoute(User, from);
            if (hit != null) return Redirect(ChapterUrl(hit.Value.book, hit.Value.chapter));

            var preferred = PermissionService.IsEmsRoute(from) ? ManualBook.Ems
                : PermissionService.IsEngineerRoute(from) ? ManualBook.Engineer
                : ManualBook.Scada;
            var chapters = _service.GetVisibleChapters(User, preferred);
            if (chapters.Count > 0) return Redirect(ChapterUrl(preferred, chapters[0]));
        }

        var vm = new ManualIndexViewModel { books = BuildShelf(null) };
        return View(vm);
    }

    /// <summary>書本入口 → 第一個看得到的章節</summary>
    [HttpGet("{book}")]
    public IActionResult Book(string book)
    {
        var target = ManualBook.Find(book);
        if (target == null) return NotFound();

        var chapters = _service.GetVisibleChapters(User, target);
        if (chapters.Count == 0) return Redirect(AccessDeniedPath);
        return Redirect(ChapterUrl(target, chapters[0]));
    }

    /// <summary>閱讀章節</summary>
    [HttpGet("{book}/{slug}")]
    public IActionResult Chapter(string book, string slug)
    {
        var target = ManualBook.Find(book);
        if (target == null) return NotFound();

        switch (_service.ResolveChapter(User, target, slug, out var chapter))
        {
            case ManualAccessResult.NotFound: return NotFound();
            case ManualAccessResult.Forbidden: return Redirect(AccessDeniedPath);
        }

        var isEnglish = CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var chapters = _service.GetVisibleChapters(User, target);
        int nIndex = chapters.FindIndex(c => c.szSlug == chapter!.szSlug);

        var vm = new ManualViewModel
        {
            book = target,
            otherBooks = BuildShelf(target),
            chapters = chapters,
            current = chapter!,
            szContentHtml = _service.RenderChapter(target, chapter!, isEnglish, _l["manual.img_todo"].Value, out var isFallback),
            isLanguageFallback = isFallback,
            isEnglish = isEnglish,
            prev = nIndex > 0 ? chapters[nIndex - 1] : null,
            next = nIndex >= 0 && nIndex < chapters.Count - 1 ? chapters[nIndex + 1] : null,
        };

        if (target.isEmsTheme) ViewData["EmsMode"] = true;
        return View("Book", vm);
    }

    /// <summary>手冊圖檔（經授權輸出，工程師手冊的圖同樣只給 Engineer）</summary>
    [HttpGet("{book}/images/{file}")]
    public IActionResult Image(string book, string file)
    {
        var target = ManualBook.Find(book);
        if (target == null) return NotFound();
        if (!_service.IsBookVisible(User, target)) return Forbid();

        var szPath = _service.GetImagePath(target, file);
        if (szPath == null) return NotFound();

        if (!new FileExtensionContentTypeProvider().TryGetContentType(szPath, out var szContentType))
            szContentType = "application/octet-stream";
        return PhysicalFile(szPath, szContentType);
    }

    private List<ManualShelfItem> BuildShelf(ManualBook? exclude)
    {
        var list = new List<ManualShelfItem>();
        foreach (var b in ManualBook.All)
        {
            if (b == exclude) continue;
            var chapters = _service.GetVisibleChapters(User, b);
            if (chapters.Count == 0) continue;
            list.Add(new ManualShelfItem { book = b, szFirstSlug = chapters[0].szSlug, nChapterCount = chapters.Count });
        }
        return list;
    }

    private static string ChapterUrl(ManualBook book, ManualChapter chapter) =>
        $"/Manual/{book.szKey}/{chapter.szSlug}";
}
