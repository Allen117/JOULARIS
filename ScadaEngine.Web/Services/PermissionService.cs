using System.Security.Claims;
using System.Text.Json;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 權限檢查服務 — 從 ClaimsPrincipal 解析使用者角色與權限
/// </summary>
public static class PermissionService
{
    /// <summary>
    /// 系統中可配置的主頁面路由清單（Admin 可授權給 User 的範圍 — 不含工程師專屬頁）
    /// </summary>
    public static readonly (string Route, string Name)[] ConfigurablePages =
    [
        ("/ScadaPage",      "即時監控"),
        ("/RealTime",       "即時數據"),
        ("/ConditionCtrl",  "條件控制"),
        ("/HistoryData",    "歷史資料"),
        ("/EventLog",       "事件記錄"),
        ("/EMS",            "能源管理首頁"),
        ("/ChilledWaterSystem", "空調水系統迴路設定"),
        ("/ChilledWaterCircuitInfo", "空調水系統迴路"),
        ("/ElectricityCircuitInfo", "電力迴路資訊"),
        ("/WaterCircuitInfo", "自來水迴路資訊"),
        ("/GasCircuitInfo", "氣迴路資訊"),
        ("/ChillerEfficiency", "冰機效率診斷"),
        ("/CompressorEfficiency", "空壓機效率診斷"),
        ("/EquipmentEfficiencySetting", "設備效率設定"),
        ("/EnergyMeter",    "電表/迴路設定"),
        ("/EnergyReport",   "用電報表"),
        ("/ElectricityCostReport", "電費報表"),
        ("/ContractCapacity", "契約容量最佳化"),
        ("/RefrigerationTonReport", "冷凍噸報表"),
        ("/WaterMeterSetting", "自來水表/迴路設定"),
        ("/WaterUsageReport", "用水報表"),
        ("/WaterTariffSetting", "水費設定"),
        ("/WaterCostReport", "水費報表"),
        ("/GasMeterSetting", "氣表/迴路設定"),
        ("/GasUsageReport", "用氣報表"),
        ("/GasTariffSetting", "氣費設定"),
        ("/GasCostReport", "氣費報表"),
        ("/AirMeterSetting", "產氣量表/迴路設定"),
        ("/DailyReport",    "能源日報"),
        ("/DailyReportSetting", "日報設定"),
        ("/EnergyDeclaration", "能源申報"),
        ("/AirCompressorDeclaration", "空壓機能源申報"),
        ("/BillingPeriodSetting", "電費月結週期設定"),
        ("/WaterBillingPeriodSetting", "水費月結週期設定"),
        ("/GasBillingPeriodSetting", "氣費月結週期設定"),
        ("/TariffSetting",  "電費設定"),
        ("/HolidaySetting", "國定假日設定"),
        ("/ShiftSetting",   "班別設定"),
        ("/EmsCardSetting", "EMS卡片顯示設定"),
        ("/EnergyBaseline", "能源基準"),
        ("/WeatherSetting", "氣象資料"),
        ("/AccountSetting", "帳號管理"),
    ];

    /// <summary>
    /// 工程師模式專屬頁面 — 僅 Engineer 角色可存取，Admin / User 一律擋（含選單與直連）。
    /// 對應 Controller 另掛 [Authorize(Roles = "Engineer")] 擋子 API；
    /// 唯 Designer 的 Points / Devices / Load 三個唯讀 API 為執行期共用（ScadaPage / EventLog），維持一般 [Authorize]。
    /// </summary>
    public static readonly (string Route, string Name)[] EngineerPages =
    [
        ("/Designer",       "畫面設計"),
        ("/ModbusCoordinator", "Modbus來源"),
        ("/DbCoordinator",  "DB 來源"),
        ("/OpcUaCoordinator", "OPC UA 來源"),
        ("/CalcPoint",      "計算點位"),
        ("/LogicFlow",      "流程圖控制"),
        ("/SystemParameter", "系統參數"),
    ];

    /// <summary>
    /// 判斷是否為 Admin 角色
    /// </summary>
    public static bool IsAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole("Admin");
    }

    /// <summary>
    /// 判斷是否為 Engineer 角色（工程師模式）
    /// </summary>
    public static bool IsEngineer(ClaimsPrincipal user)
    {
        return user.IsInRole("Engineer");
    }

    /// <summary>
    /// 路徑是否為工程師模式專屬頁面
    /// </summary>
    public static bool IsEngineerRoute(string? szPath)
    {
        if (string.IsNullOrEmpty(szPath)) return false;
        foreach (var (route, _) in EngineerPages)
        {
            if (string.Equals(route, szPath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// EMS 體系所有頁面（含 hub 與子頁）。
    /// 新增 EMS 族頁面時加進這裡，_Layout 會自動套 EmsMode（淡綠主題 + EMS brand）。
    /// 第一個元素固定為 /EMS（hub），其餘為子頁。
    /// </summary>
    public static readonly string[] EmsRoutes =
    [
        "/EMS",
        "/ChilledWaterSystem",
        "/ChilledWaterCircuitInfo",
        "/ElectricityCircuitInfo",
        "/WaterCircuitInfo",
        "/GasCircuitInfo",
        "/ChillerEfficiency",
        "/CompressorEfficiency",
        "/EquipmentEfficiencySetting",
        "/EnergyMeter",
        "/EnergyReport",
        "/ElectricityCostReport",
        "/ContractCapacity",
        "/RefrigerationTonReport",
        "/WaterMeterSetting",
        "/WaterUsageReport",
        "/WaterTariffSetting",
        "/WaterCostReport",
        "/GasMeterSetting",
        "/GasUsageReport",
        "/GasTariffSetting",
        "/GasCostReport",
        "/AirMeterSetting",
        "/DailyReport",
        "/DailyReportSetting",
        "/EnergyDeclaration",
        "/AirCompressorDeclaration",
        "/BillingPeriodSetting",
        "/WaterBillingPeriodSetting",
        "/GasBillingPeriodSetting",
        "/TariffSetting",
        "/HolidaySetting",
        "/ShiftSetting",
        "/EmsCardSetting",
        "/EnergyBaseline",
    ];

    /// <summary>
    /// 路徑是否屬於 EMS 體系（_Layout 判斷是否套淡綠主題用）
    /// </summary>
    public static bool IsEmsRoute(string? szPath)
    {
        if (string.IsNullOrEmpty(szPath)) return false;
        return EmsRoutes.Contains(szPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 檢查使用者是否可存取指定主頁面路由
    /// </summary>
    public static bool CanAccessPage(ClaimsPrincipal user, string szRoute)
    {
        if (!user.Identity?.IsAuthenticated ?? true)
            return false;

        // 工程師專屬頁短路判斷：只認 Engineer 角色，Admin 與 User 權限 JSON 殘值皆無效
        if (IsEngineerRoute(szRoute))
            return IsEngineer(user);

        // Engineer 對一般頁面比照 Admin 全放行（工程師建置時需要驗證監控成果）
        if (IsAdmin(user) || IsEngineer(user))
            return true;

        var permData = GetPermissionData(user);
        if (permData.pages.Contains(szRoute, StringComparer.OrdinalIgnoreCase))
            return true;

        // /EMS 為 Hub 頁，子頁任一可看就放行（避免使用者要繞道才能到子頁）
        if (string.Equals(szRoute, "/EMS", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var r in EmsRoutes)
            {
                if (string.Equals(r, "/EMS", StringComparison.OrdinalIgnoreCase)) continue;
                if (permData.pages.Contains(r, StringComparer.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        return false;
    }

    /// <summary>
    /// 從 Claims 解析 PermissionData
    /// </summary>
    public static PermissionData GetPermissionData(ClaimsPrincipal user)
    {
        var szJson = user.FindFirstValue("Permissions");
        if (string.IsNullOrEmpty(szJson) || szJson == "{}")
            return new PermissionData();

        try
        {
            return JsonSerializer.Deserialize<PermissionData>(szJson) ?? new PermissionData();
        }
        catch
        {
            return new PermissionData();
        }
    }

    /// <summary>
    /// 檢查 User 是否可檢視指定 ScadaPage 子頁面
    /// </summary>
    public static bool CanViewScadaPage(ClaimsPrincipal user, string szPageSid)
    {
        if (IsAdmin(user) || IsEngineer(user))
            return true;

        var permData = GetPermissionData(user);
        return permData.scadaPages.TryGetValue(szPageSid, out var perm) && perm.canView;
    }

    /// <summary>
    /// 檢查 User 是否可右鍵控制指定 ScadaPage 子頁面
    /// </summary>
    public static bool CanControlScadaPage(ClaimsPrincipal user, string szPageSid)
    {
        if (IsAdmin(user) || IsEngineer(user))
            return true;

        var permData = GetPermissionData(user);
        return permData.scadaPages.TryGetValue(szPageSid, out var perm) && perm.canControl;
    }
}
