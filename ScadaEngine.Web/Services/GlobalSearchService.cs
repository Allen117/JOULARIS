using System.Security.Claims;
using ScadaEngine.Web.Features.GlobalSearch.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 全站搜尋索引服務 — 頁面註冊表為唯一真相來源，
/// 標題從 I18nResourceService 取 zh-TW / en 兩份字典，權限走 PermissionService 逐筆過濾。
/// 新增頁面進導覽列時，於 _registry 補一筆即可被搜尋到。
/// </summary>
public class GlobalSearchService
{
    /// <summary>註冊表條目：路由 + _Layout resx key + 選單圖示 + 同義關鍵字（中英混雜、空白分隔）</summary>
    private sealed record PageEntry(string Route, string ResxKey, string Icon, string Keywords);

    private static readonly PageEntry[] _registry =
    [
        // ── SCADA 體系 ──
        new("/ScadaPage",       "layout.menu.scadapage",        "fas fa-desktop",             "圖控 監控 首頁 畫面 mimic dashboard monitor"),
        new("/RealTime",        "layout.menu.realtime",         "fas fa-tachometer-alt",      "即時 數據 點位 realtime data point"),
        new("/ConditionCtrl",   "layout.menu.condition",        "fas fa-code-branch",         "條件 控制 邏輯 condition control"),
        new("/ScheduleSetting", "layout.menu.schedule",         "fas fa-calendar-alt",        "排程 時程 控制 schedule timer"),
        new("/HistoryData",     "layout.menu.trend",            "fas fa-chart-area",          "歷史 趨勢 曲線 history trend chart"),
        new("/EventLog",        "layout.menu.eventlog",         "fas fa-clipboard-list",      "事件 記錄 日誌 警報歷史 event log"),
        new("/AlarmSetting",    "layout.menu.alarm",            "fas fa-bell",                "警報 通知 line email 推播 alarm notify"),
        new("/WeatherSetting",  "layout.menu.weather",          "fas fa-cloud-sun",           "氣象 天氣 溫度 weather"),
        new("/AccountSetting",  "layout.menu.account",          "fas fa-users-cog",           "帳號 使用者 權限 密碼 account user permission"),

        // ── 工程師模式（CanAccessPage 只放行 Engineer）──
        new("/Designer",           "layout.menu.designer",          "fas fa-paint-brush",      "畫面 設計 圖控 編輯 designer draw"),
        new("/ModbusCoordinator",  "layout.menu.modbuscoordinator", "fas fa-network-wired",    "modbus 通訊 點位 來源 coordinator"),
        new("/DbCoordinator",      "layout.menu.dbcoordinator",     "fas fa-database",         "db 資料庫 點位 來源 database"),
        new("/OpcUaCoordinator",   "layout.menu.opcuacoordinator",  "fas fa-plug",             "opc ua opcua 通訊 點位 來源"),
        new("/CalcPoint",          "layout.menu.calcpoint",         "fas fa-calculator",       "計算 點位 公式 虛擬 calc formula"),
        new("/LogicFlow",          "layout.menu.logicflow",         "fas fa-project-diagram",  "流程 邏輯 演算法 flow algorithm"),

        // ── EMS 體系 ──
        new("/EMS",                        "layout.brand.ems",                       "fas fa-leaf",                "能源 管理 首頁 ems energy hub"),
        new("/ChilledWaterCircuitInfo",    "layout.menu.chilled_water_circuit_info", "fas fa-snowflake",           "迴路 資訊 空調 水系統 冰水 冷凍噸 冷量 rt chilled water circuit"),
        new("/ElectricityCircuitInfo",     "layout.menu.electricity_circuit_info",   "fas fa-bolt",                "迴路 資訊 電力 用電 kwh circuit electricity power"),
        new("/WaterCircuitInfo",           "layout.menu.water_circuit_info",         "fas fa-water",               "迴路 資訊 自來水 水 用水 水量 circuit water meter"),
        new("/GasCircuitInfo",             "layout.menu.gas_circuit_info",           "fas fa-fire",                "迴路 資訊 氣 用氣 天然氣 瓦斯 circuit gas"),
        new("/ChillerEfficiency",          "layout.menu.chiller_efficiency",         "fas fa-temperature-low",     "冰機 冰水主機 效率 診斷 kw/rt cop eer ashrae chiller efficiency"),
        new("/CompressorEfficiency",       "layout.menu.compressor_efficiency",      "fas fa-compress-arrows-alt", "空壓機 壓縮空氣 效率 診斷 比功率 cagi iso 11011 air compressor specific power"),
        new("/EquipmentEfficiencySetting", "layout.menu.equipment_efficiency_setting", "fas fa-gauge-high",        "設備 效率 設定 冰機 空壓機 掃描 點位 綁定 equipment efficiency setting"),
        new("/EnergyReport",               "layout.menu.energy_report",              "fas fa-chart-bar",           "報表 用電 電力 度數 kwh power electricity usage report"),
        new("/ElectricityCostReport",      "layout.menu.electricity_cost_report",    "fas fa-file-invoice-dollar", "報表 電費 帳單 金額 bill cost report"),
        new("/ContractCapacity",           "layout.menu.contract_capacity",          "fas fa-sliders-h",           "契約 契約容量 需量 超約 基本電費 最佳化 contract capacity demand optimization"),
        new("/RefrigerationTonReport",     "layout.menu.refrigeration_ton_report",   "fas fa-snowflake",           "報表 冷凍噸 冰水 空調 rt ton report"),
        new("/WaterUsageReport",           "layout.menu.water_usage_report",         "fas fa-water",               "報表 用水 水量 water usage report"),
        new("/WaterCostReport",            "layout.menu.water_cost_report",          "fas fa-hand-holding-usd",    "報表 水費 帳單 water cost bill report"),
        new("/GasUsageReport",             "layout.menu.gas_usage_report",           "fas fa-fire",                "報表 用氣 天然氣 瓦斯 gas usage report"),
        new("/GasCostReport",              "layout.menu.gas_cost_report",            "fas fa-receipt",             "報表 氣費 天然氣 瓦斯 帳單 gas cost bill report"),
        new("/DailyReport",                "layout.menu.daily_report",               "fas fa-newspaper",           "日報 能源 每日 摘要 daily report summary email"),
        new("/DailyReportSetting",         "layout.menu.daily_report_setting",       "fas fa-cog",                 "日報 設定 寄送 收件人 daily report setting mail recipient"),
        new("/EnergyDeclaration",          "layout.menu.energy_declaration",         "fas fa-file-signature",      "能源 申報 報表 declaration"),
        new("/AirCompressorDeclaration",   "layout.menu.air_compressor_declaration", "fas fa-wind",                "空壓 空壓機 壓縮空氣 能源 申報 比能耗 產氣 air compressor declaration"),
        new("/EnergyBaseline",             "layout.menu.energy_baseline",            "fas fa-bullseye",            "能源 基準 基線 baseline"),
        new("/ChilledWaterSystem",         "layout.menu.chilled_water",              "fas fa-tint",                "空調 水系統 冰水 迴路 設定 冷凍噸 chilled water"),
        new("/EnergyMeter",                "layout.menu.energy_meter",               "fas fa-sitemap",             "電表 迴路 設定 meter power pm"),
        new("/BillingPeriodSetting",       "layout.menu.billing_period",             "fas fa-calendar-check",      "電費 月結 週期 期別 billing period"),
        new("/TariffSetting",              "layout.menu.tariff_setting",             "fas fa-file-invoice-dollar", "電費 電價 費率 時間電價 tou tariff"),
        new("/WaterMeterSetting",          "layout.menu.water_meter",                "fas fa-faucet",              "自來水 水表 迴路 設定 water meter"),
        new("/WaterBillingPeriodSetting",  "layout.menu.water_billing_period",       "fas fa-calendar-day",        "水費 月結 週期 期別 water billing period"),
        new("/WaterTariffSetting",         "layout.menu.water_tariff_setting",       "fas fa-tint",                "水費 水價 費率 water tariff"),
        new("/GasMeterSetting",            "layout.menu.gas_meter",                  "fas fa-gas-pump",            "氣表 天然氣 瓦斯 迴路 設定 gas meter"),
        new("/GasBillingPeriodSetting",    "layout.menu.gas_billing_period",         "fas fa-calendar-day",        "氣費 月結 週期 期別 gas billing period"),
        new("/GasTariffSetting",           "layout.menu.gas_tariff_setting",         "fas fa-money-bill-wave",     "氣費 氣價 費率 天然氣 瓦斯 gas tariff"),
        new("/AirMeterSetting",            "layout.menu.air_meter",                  "fas fa-wind",                "產氣量 產氣量表 空壓 空壓機 壓縮空氣 迴路 設定 air flow meter compressor"),
        new("/HolidaySetting",             "layout.menu.holiday_setting",            "fas fa-calendar-day",        "假日 國定 行事曆 holiday"),
        new("/ShiftSetting",               "layout.menu.shift_setting",              "fas fa-user-clock",          "班別 班表 早班 晚班 輪班 shift"),
        new("/EmsCardSetting",             "layout.menu.ems_card_setting",           "fas fa-th-large",            "卡片 顯示 ems card"),
    ];

    private readonly I18nResourceService _i18n;

    public GlobalSearchService(I18nResourceService i18n)
    {
        _i18n = i18n;
    }

    /// <summary>
    /// 取得該使用者可見的搜尋索引（伺服器端過濾，無權限頁面不會出現在回傳中）。
    /// </summary>
    public List<SearchIndexEntry> GetIndexForUser(ClaimsPrincipal user)
    {
        var dictZh = _i18n.GetDictionary("zh-TW");
        var dictEn = _i18n.GetDictionary("en");

        var aResult = new List<SearchIndexEntry>(_registry.Length);
        foreach (var entry in _registry)
        {
            if (!PermissionService.CanAccessPage(user, entry.Route))
                continue;

            aResult.Add(new SearchIndexEntry
            {
                szRoute = entry.Route,
                szTitleZh = dictZh.TryGetValue(entry.ResxKey, out var szZh) ? szZh : entry.Route,
                szTitleEn = dictEn.TryGetValue(entry.ResxKey, out var szEn) ? szEn : entry.Route,
                szKeywords = entry.Keywords,
                szIcon = entry.Icon,
                isEms = PermissionService.IsEmsRoute(entry.Route)
            });
        }
        return aResult;
    }
}
