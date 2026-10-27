using System.Security.Claims;
using System.Text.Json;
using ScadaEngine.Web.Features.SystemParameter.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.SystemParameter;

/// <summary>
/// 鎖住系統參數：缺鍵補預設（舊資料不需遷移）、範圍驗證、/SystemParameter 僅 Engineer 可進（Admin / User 皆擋）。
/// </summary>
public class SystemParameterTests
{
    [Fact]
    public void 缺鍵_反序列化補預設值()
    {
        var c = JsonSerializer.Deserialize<SystemParameterConfig>("{\"nHistoryExportMaxCells\":80000}")!;
        Assert.Equal(80000, c.nHistoryExportMaxCells);
        Assert.Equal(5000, c.nHistoryMaxRecordsPerPoint);
        Assert.Equal(1, c.dCumulativeJitterTolerancePct);
    }

    [Fact]
    public void 預設值通過驗證()
    {
        Assert.Null(SystemParameterService.Validate(new SystemParameterConfig()));
    }

    [Theory]
    [InlineData(99, 50000, 1, "sysparam.error.history_max_records")]
    [InlineData(5000, 999, 1, "sysparam.error.history_export_max_cells")]
    [InlineData(5000, 50000, -0.1, "sysparam.error.jitter")]
    [InlineData(5000, 50000, double.NaN, "sysparam.error.jitter")]
    public void 超出範圍_回錯誤key(int nMaxRecords, int nMaxCells, double dJitter, string szKey)
    {
        var c = new SystemParameterConfig
        {
            nHistoryMaxRecordsPerPoint = nMaxRecords,
            nHistoryExportMaxCells = nMaxCells,
            dCumulativeJitterTolerancePct = dJitter
        };
        Assert.Equal(szKey, SystemParameterService.Validate(c));
    }

    [Theory]
    [InlineData("Engineer", true)]
    [InlineData("Admin", false)]
    [InlineData("User", false)]
    public void 系統參數頁_僅Engineer可進(string szRole, bool isAllowed)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "u"), new Claim(ClaimTypes.Role, szRole)], "test"));
        Assert.Equal(isAllowed, PermissionService.CanAccessPage(user, "/SystemParameter"));
    }

    [Fact]
    public void 匯出預估格數_每點位取筆數上限()
    {
        var dtStart = new DateTime(2026, 10, 1);
        // 24h、1 分鐘間隔 = 1440 筆/點，8 點 = 11,520
        Assert.Equal(11520, HistoryQueryService.EstimateExportCells(8, dtStart, dtStart.AddDays(1), 1, 5000));
        // 7 天 = 10,080 筆/點 → 截在 5,000，8 點 = 40,000（預設 5 萬上限不觸發）
        Assert.Equal(40000, HistoryQueryService.EstimateExportCells(8, dtStart, dtStart.AddDays(7), 1, 5000));
        // 間隔 0 視為 1 分鐘
        Assert.Equal(60, HistoryQueryService.EstimateExportCells(1, dtStart, dtStart.AddHours(1), 0, 5000));
    }
}
