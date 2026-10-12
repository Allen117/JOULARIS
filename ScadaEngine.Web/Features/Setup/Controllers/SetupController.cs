using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Features.Setup.Models;
using ScadaEngine.Web.Services;
using System.Net;
using System.Security.Claims;

namespace ScadaEngine.Web.Features.Setup.Controllers;

/// <summary>
/// First-run 初始化：DB 尚無管理者（Admin / Engineer 皆無）、setup 未完成、且來源為本機時，引導建立第一組 Admin。
/// 三重守衛：adminOrEngineerCount==0 && !setupCompleted && loopback。完成後寫 SystemSettings 旗標永久關閉。
/// install-db.ps1 建的 engineer bootstrap 帳號存在時不觸發：直接以 engineer 登入、再於帳號設定頁建 Admin。
/// 取代舊有的 admin/admin 硬編碼後門（SEC-08）。
/// </summary>
[AllowAnonymous]
public class SetupController : Controller
{
    private readonly ILogger<SetupController> _logger;
    private readonly IDataRepository _repository;
    private readonly AccountSettingService _accountService;

    public SetupController(
        ILogger<SetupController> logger,
        IDataRepository repository,
        AccountSettingService accountService)
    {
        _logger = logger;
        _repository = repository;
        _accountService = accountService;
    }

    /// <summary>來源是否為本機（含 IPv4-mapped IPv6）</summary>
    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }

    /// <summary>三重守衛：未完成 setup、DB 無 Admin / Engineer、且來自本機才允許進入</summary>
    private async Task<bool> IsSetupAllowedAsync()
    {
        if (await _repository.IsSetupCompletedAsync()) return false;
        if (await _repository.GetAdminCountAsync() > 0) return false;
        return IsLoopback(HttpContext);
    }

    [HttpGet("/Setup")]
    public async Task<IActionResult> Index()
    {
        if (!await IsSetupAllowedAsync())
            return Redirect("/Login");
        return View();
    }

    [HttpGet("/Setup/CreateAdmin")]
    public async Task<IActionResult> CreateAdmin()
    {
        if (!await IsSetupAllowedAsync())
            return Redirect("/Login");
        return View(new SetupCreateAdminModel());
    }

    [HttpPost("/Setup/CreateAdmin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAdmin(SetupCreateAdminModel model)
    {
        if (!await IsSetupAllowedAsync())
            return Redirect("/Login");

        var szUsername = model.szUsername?.Trim() ?? "";
        var szPassword = model.szPassword ?? "";

        var szError = ValidateStrongPassword(szUsername, szPassword, model.szConfirmPassword);
        if (szError != null)
        {
            ModelState.AddModelError(string.Empty, szError);
            model.szPassword = null;
            model.szConfirmPassword = null;
            return View(model);
        }

        var (isSuccess, szMessage) = await _accountService.CreateUserAsync(
            szUsername, model.szRealName, szPassword, "Admin", null, isActive: true, isOperatorEngineer: false);

        if (!isSuccess)
        {
            ModelState.AddModelError(string.Empty, szMessage);
            model.szPassword = null;
            model.szConfirmPassword = null;
            return View(model);
        }

        // 一次性關閉 setup（即使日後 Admin 被全刪也不再自動重開）
        await _repository.MarkSetupCompletedAsync();

        // 直接登入剛建立的 Admin
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, szUsername),
            new(ClaimTypes.NameIdentifier, szUsername),
            new(ClaimTypes.Role, "Admin"),
            new("Permissions", "{}"),
            new("LoginTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        _logger.LogInformation("First-run setup 完成，已建立首組 Admin：{Username}", szUsername);
        return Redirect("/ScadaPage");
    }

    /// <summary>密碼強度：長度≥8、含英文+數字、不等於帳號、兩次一致</summary>
    private static string? ValidateStrongPassword(string szUsername, string szPassword, string? szConfirm)
    {
        if (string.IsNullOrWhiteSpace(szUsername))
            return "帳號不可為空";
        if (szPassword != szConfirm)
            return "兩次輸入的密碼不一致";
        if (szPassword.Length < 8)
            return "密碼長度至少 8 碼";
        if (!szPassword.Any(char.IsLetter) || !szPassword.Any(char.IsDigit))
            return "密碼須同時包含英文字母與數字";
        if (string.Equals(szPassword, szUsername, StringComparison.OrdinalIgnoreCase))
            return "密碼不可與帳號相同";
        return null;
    }
}
