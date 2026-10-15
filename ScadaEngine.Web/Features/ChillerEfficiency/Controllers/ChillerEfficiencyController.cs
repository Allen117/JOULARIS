using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.ChillerEfficiency.Controllers;

/// <summary>
/// 設備效率診斷頁（chiller）— 僅權限 + 回 View；資料走 /EMS/api/equipment/*?kind=chiller，
/// 前端 js/ems-equipment.js 依根元素 data-kind 切換指標（冰機 / 空壓機兩頁共用一支 JS）。
/// </summary>
[Authorize]
public class ChillerEfficiencyController : Controller
{
    [HttpGet("/ChillerEfficiency")]
    public IActionResult Index()
    {
        if (!PermissionService.CanAccessPage(User, "/ChillerEfficiency"))
            return Redirect("/EMS");
        return View();
    }
}
