using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Features.Login.Models;
using ScadaEngine.Engine.Data.Interfaces;
using System.Net;
using System.Security.Claims;

namespace ScadaEngine.Web.Features.Login.Controllers;

/// <summary>
/// 登入控制器
/// </summary>
public class LoginController : Controller
{
    private readonly ILogger<LoginController> _logger;
    private readonly IDataRepository _dataRepository;

    public LoginController(ILogger<LoginController> logger, IDataRepository dataRepository)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));
    }

    /// <summary>來源是否為本機（含 IPv4-mapped IPv6）</summary>
    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// 顯示登入頁面 (GET)
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Index()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect("/ScadaPage");

        // First-run：DB 尚無 Admin / Engineer 且 setup 未完成 → 本機導向初始化頁，遠端則顯示指引
        // （engineer bootstrap 帳號存在時可直接用它登入，再視需要於帳號設定頁建 Admin）
        if (!await _dataRepository.IsSetupCompletedAsync()
            && await _dataRepository.GetAdminCountAsync() == 0)
        {
            if (IsLoopback(HttpContext))
                return Redirect("/Setup");
            ViewData["SetupHint"] = true;
        }

        return View(new LoginModel());
    }

    /// <summary>
    /// 處理登入表單提交 (POST)
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LoginModel loginModel)
    {
        if (!ModelState.IsValid)
            return View(loginModel);

        if (!loginModel.IsInputValid())
        {
            ModelState.AddModelError(string.Empty, "使用者名稱或密碼格式錯誤");
            loginModel.ClearSensitiveData();
            return View(loginModel);
        }

        // DB 驗證（已移除 admin/admin 預設後門，SEC-08）
        bool isDbAuth = await _dataRepository.ValidateUserAsync(loginModel.szUserName!, loginModel.szPassword!);
        if (!isDbAuth)
        {
            _logger.LogWarning("登入失敗：使用者={UserName}", loginModel.szUserName);
            ModelState.AddModelError(string.Empty, "使用者名稱或密碼錯誤");
            loginModel.ClearSensitiveData();
            return View(loginModel);
        }

        var dbUser = await _dataRepository.GetUserByUsernameAsync(loginModel.szUserName!);
        if (dbUser == null)
        {
            // 理論上不會發生（isDbAuth 成立代表帳號存在且啟用）；防禦性拒絕，避免落到預設角色
            _logger.LogWarning("登入驗證通過但查無使用者：{UserName}", loginModel.szUserName);
            ModelState.AddModelError(string.Empty, "使用者名稱或密碼錯誤");
            loginModel.ClearSensitiveData();
            return View(loginModel);
        }

        // 取得使用者角色資訊
        string szRole = dbUser.szRole;
        string szPermissionJson = "{}";

        // User 角色需載入權限設定
        if (szRole == "User")
        {
            var perm = await _dataRepository.GetUserPermissionsAsync(dbUser.nUserID);
            szPermissionJson = perm?.szPermissionJson ?? "{}";
        }

        // 建立認證 Cookie
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, loginModel.szUserName!),
            new(ClaimTypes.NameIdentifier, loginModel.szUserName!),
            new(ClaimTypes.Role, szRole),
            new("Permissions", szPermissionJson),
            new("LoginTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        };

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = loginModel.isRememberMe,
            ExpiresUtc = loginModel.isRememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(4)
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(claimsIdentity),
            authProperties);

        // 更新最後登入時間至 DB
        await _dataRepository.UpdateLastLoginAsync(loginModel.szUserName!);

        // 既有安裝適配：首次有 Admin / Engineer 成功登入即鎖定 first-run setup（一次性、不自動重開）
        if ((szRole == "Admin" || szRole == "Engineer") && !await _dataRepository.IsSetupCompletedAsync())
        {
            await _dataRepository.MarkSetupCompletedAsync();
        }

        _logger.LogInformation("使用者登入成功：{UserName}", loginModel.szUserName);

        loginModel.ClearSensitiveData();
        return Redirect("/ScadaPage");
    }

    /// <summary>
    /// 登出
    /// </summary>
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var szUserName = User.Identity?.Name ?? "未知使用者";
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        _logger.LogInformation("使用者登出：{UserName}", szUserName);
        return RedirectToAction("Index", "Login");
    }

    /// <summary>
    /// 無權限存取
    /// </summary>
    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        _logger.LogWarning("無權限存取嘗試，使用者={UserName}", User.Identity?.Name ?? "匿名");
        return View();
    }
}
