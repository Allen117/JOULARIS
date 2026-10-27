using System.Security.Claims;
using System.Text.Json;
using ScadaEngine.Web.Features.Manual.Models;
using ScadaEngine.Web.Services;
using Xunit;

namespace ScadaEngine.Tests.Manual;

/// <summary>
/// 鎖住線上使用手冊的權限邊界與內容轉換：
/// 工程師手冊只給 Engineer；SCADA / EMS 依頁面權限過濾章節、整本看不到就藏；
/// slug 白名單防路徑穿越；IMG-TODO 標記轉提示框、raw HTML 不輸出。
/// 每個測試用獨立暫存目錄模擬 Manual/ 資料夾。
/// </summary>
public class ManualContentServiceTests : IDisposable
{
    private readonly string _szRoot;
    private readonly ManualContentService _service;

    public ManualContentServiceTests()
    {
        _szRoot = Path.Combine(Path.GetTempPath(), "ManualContentServiceTests_" + Guid.NewGuid().ToString("N"));

        WriteToc("scada", new[]
        {
            Chapter("quick-start", "快速開始"),
            Chapter("scada-page", "監控畫面", routes: new[] { "/ScadaPage" }),
            Chapter("account", "帳號管理", routes: new[] { "/AccountSetting" }, roles: new[] { "Admin", "Engineer" }),
            Chapter("faq", "常見問題"),
        });
        WriteToc("ems", new[]
        {
            Chapter("ems-home", "EMS 首頁", routes: new[] { "/EMS" }),
            Chapter("energy-report", "用電報表", routes: new[] { "/EnergyReport" }),
            Chapter("water", "用水報表", routes: new[] { "/WaterUsageReport", "/WaterCostReport" }),
        });
        WriteToc("engineer", new[]
        {
            Chapter("designer", "畫面設計", routes: new[] { "/Designer" }),
            Chapter("deploy", "部署與升級"),
        });
        File.WriteAllText(Path.Combine(_szRoot, "ems", "energy-report.md"), "# 用電報表\n\n內文");

        _service = new ManualContentService(_szRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_szRoot, recursive: true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    // ─────────────── 工程師手冊 ───────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("User")]
    public void EngineerBook_NonEngineer_Hidden(string szRole)
    {
        var user = MakeUser(szRole);
        Assert.Empty(_service.GetVisibleChapters(user, ManualBook.Engineer));
        Assert.Equal(ManualAccessResult.Forbidden, _service.ResolveChapter(user, ManualBook.Engineer, "deploy", out _));
        // 整本看不到時不存在的 slug 也回 Forbidden，不以 404 洩漏章節清單
        Assert.Equal(ManualAccessResult.Forbidden, _service.ResolveChapter(user, ManualBook.Engineer, "no-such-chapter", out _));
    }

    [Fact]
    public void EngineerBook_Engineer_AllChaptersVisible()
    {
        var user = MakeUser("Engineer");
        Assert.Equal(new[] { "designer", "deploy" }, Slugs(_service.GetVisibleChapters(user, ManualBook.Engineer)));
    }

    [Fact]
    public void Engineer_SeesAllThreeBooks()
    {
        var user = MakeUser("Engineer");
        Assert.All(ManualBook.All, b => Assert.True(_service.IsBookVisible(user, b)));
    }

    // ─────────────── Admin ───────────────

    [Fact]
    public void Admin_SeesScadaAndEmsWithAccountChapter()
    {
        var user = MakeUser("Admin");
        Assert.True(_service.IsBookVisible(user, ManualBook.Scada));
        Assert.True(_service.IsBookVisible(user, ManualBook.Ems));
        Assert.Contains("account", Slugs(_service.GetVisibleChapters(user, ManualBook.Scada)));
    }

    // ─────────────── User 依權限 ───────────────

    [Fact]
    public void User_OnlyEnergyReport_EmsShowsThatChapter_ScadaHidden()
    {
        var user = MakeUser("User", "/EnergyReport");

        // /EMS hub 對 User 是「任一 EMS 子頁可看就放行」，所以首頁章節也可見
        Assert.Equal(new[] { "ems-home", "energy-report" }, Slugs(_service.GetVisibleChapters(user, ManualBook.Ems)));

        // SCADA 只剩不綁頁的快速開始 / 常見問題 → 整本不顯示、直連拒絕（連不綁頁章節也拒絕）
        Assert.False(_service.IsBookVisible(user, ManualBook.Scada));
        Assert.Equal(ManualAccessResult.Forbidden, _service.ResolveChapter(user, ManualBook.Scada, "quick-start", out _));
        Assert.Equal(ManualAccessResult.Forbidden, _service.ResolveChapter(user, ManualBook.Scada, "scada-page", out _));
    }

    [Fact]
    public void User_NoEmsPermission_EmsBookHidden()
    {
        var user = MakeUser("User", "/ScadaPage");
        Assert.False(_service.IsBookVisible(user, ManualBook.Ems));
        Assert.Equal(new[] { "quick-start", "scada-page", "faq" }, Slugs(_service.GetVisibleChapters(user, ManualBook.Scada)));
    }

    [Fact]
    public void User_AnyOfMultipleRoutes_ChapterVisible()
    {
        var user = MakeUser("User", "/WaterCostReport");
        Assert.Contains("water", Slugs(_service.GetVisibleChapters(user, ManualBook.Ems)));
    }

    [Fact]
    public void User_RolesChapter_HiddenEvenWithLeftoverPagePermission()
    {
        // 權限 JSON 有 /AccountSetting 殘值，但該頁實際僅 Admin / Engineer → 章節仍看不到
        var user = MakeUser("User", "/ScadaPage", "/AccountSetting");
        Assert.DoesNotContain("account", Slugs(_service.GetVisibleChapters(user, ManualBook.Scada)));
        Assert.Equal(ManualAccessResult.Forbidden, _service.ResolveChapter(user, ManualBook.Scada, "account", out _));
    }

    [Fact]
    public void Anonymous_NothingVisible()
    {
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.All(ManualBook.All, b => Assert.False(_service.IsBookVisible(anon, b)));
    }

    // ─────────────── slug 白名單 ───────────────

    [Theory]
    [InlineData("../engineer/deploy")]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("Energy-Report")]
    [InlineData("not-in-toc")]
    [InlineData("")]
    public void ResolveChapter_InvalidOrUnknownSlug_NotFound(string szSlug)
    {
        var user = MakeUser("Engineer");
        Assert.Equal(ManualAccessResult.NotFound, _service.ResolveChapter(user, ManualBook.Ems, szSlug, out var chapter));
        Assert.Null(chapter);
    }

    [Fact]
    public void LoadToc_DropsIllegalSlug()
    {
        WriteToc("ems", new[] { Chapter("ok", "OK"), Chapter("../bad", "Bad"), Chapter("ok", "Dup") });
        var service = new ManualContentService(_szRoot);
        Assert.Equal(new[] { "ok" }, Slugs(service.LoadToc(ManualBook.Ems)));
    }

    [Theory]
    [InlineData("../_toc.json")]
    [InlineData("a/b.png")]
    [InlineData("x.svg")]
    [InlineData("missing.png")]
    public void GetImagePath_RejectsUnsafeOrMissing(string szFile)
    {
        Assert.Null(_service.GetImagePath(ManualBook.Ems, szFile));
    }

    // ─────────────── 功能頁 → 章節 ───────────────

    [Fact]
    public void FindChapterByRoute_RespectsPermission()
    {
        var hit = _service.FindChapterByRoute(MakeUser("User", "/EnergyReport"), "/EnergyReport");
        Assert.NotNull(hit);
        Assert.Equal("energy-report", hit!.Value.chapter.szSlug);

        Assert.Null(_service.FindChapterByRoute(MakeUser("Admin"), "/Designer"));
        Assert.Equal("designer", _service.FindChapterByRoute(MakeUser("Engineer"), "/Designer")!.Value.chapter.szSlug);
    }

    // ─────────────── Markdown 轉換 ───────────────

    [Fact]
    public void RenderMarkdown_ImgTodo_BecomesPlaceholderBox()
    {
        var szHtml = ManualContentService.RenderMarkdown(
            "步驟一\n<!-- IMG-TODO: ems/energy-report-01 | 用電報表查詢結果 | route=/EnergyReport -->\n步驟二", "待補圖：");

        Assert.Contains("class=\"manual-img-todo\"", szHtml);
        Assert.Contains("data-img-id=\"ems/energy-report-01\"", szHtml);
        Assert.Contains("用電報表查詢結果", szHtml);
        Assert.DoesNotContain("IMG-TODO", szHtml);
        Assert.DoesNotContain("MANUALIMGTODO", szHtml);
    }

    [Fact]
    public void RenderMarkdown_RawHtmlAndCommentsNotEmitted()
    {
        var szHtml = ManualContentService.RenderMarkdown("<!-- 作者備忘 -->\n<script>alert(1)</script>\n\n## 標題", "x");

        Assert.DoesNotContain("<script>", szHtml);
        Assert.DoesNotContain("作者備忘", szHtml);
        Assert.Contains("<h2 id=", szHtml);
    }

    [Fact]
    public void RenderChapter_EnglishFallsBackToChinese()
    {
        var chapter = _service.LoadToc(ManualBook.Ems).First(c => c.szSlug == "energy-report");
        var szHtml = _service.RenderChapter(ManualBook.Ems, chapter, isEnglish: true, "x", out var isFallback);
        Assert.True(isFallback);
        Assert.Contains("用電報表", szHtml);
    }

    // ─────────────── helpers ───────────────

    private static ClaimsPrincipal MakeUser(string szRole, params string[] pages)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "tester"),
            new(ClaimTypes.Role, szRole),
        };
        if (pages.Length > 0)
            claims.Add(new Claim("Permissions", JsonSerializer.Serialize(new { pages, scadaPages = new { } })));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static object Chapter(string szSlug, string szTitle, string[]? routes = null, string[]? roles = null) =>
        new { slug = szSlug, title = szTitle, group = "", routes = routes ?? Array.Empty<string>(), roles = roles ?? Array.Empty<string>() };

    private void WriteToc(string szBook, object[] chapters)
    {
        var szDir = Path.Combine(_szRoot, szBook);
        Directory.CreateDirectory(szDir);
        var szPath = Path.Combine(szDir, "_toc.json");
        File.WriteAllText(szPath, JsonSerializer.Serialize(chapters));
        // 同一測試內重寫目錄檔時確保 LastWriteTime 變動，快取會失效
        File.SetLastWriteTimeUtc(szPath, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    }

    private static string[] Slugs(IEnumerable<ManualChapter> chapters) => chapters.Select(c => c.szSlug).ToArray();
}
