using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.CompressorEfficiency.Controllers;

/// <summary>
/// 設備效率診斷頁（compressor）— 僅權限 + 回 View；資料走 /EMS/api/equipment/*?kind=compressor，
/// 前端 js/ems-equipment.js 依根元素 data-kind 切換指標（冰機 / 空壓機兩頁共用一支 JS）。
/// </summary>
[Authorize]
public class CompressorEfficiencyController : Controller
{
    [HttpGet("/CompressorEfficiency")]
    public IActionResult Index()
    {
        if (!PermissionService.CanAccessPage(User, "/CompressorEfficiency"))
            return Redirect("/EMS");
        return View();
    }
}
