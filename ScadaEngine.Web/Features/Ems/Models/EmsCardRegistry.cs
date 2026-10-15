namespace ScadaEngine.Web.Features.Ems.Models;

/// <summary>
/// 單張 EMS 首頁卡片的定義（程式內靜態註冊，不進 DB）。
/// </summary>
/// <param name="szCardKey">卡片鍵（DB EmsCardSetting.CardKey 對應值）</param>
/// <param name="szPartialViewName">Features/Ems/Views/ 下的 partial 檔名（不含 .cshtml）</param>
/// <param name="szNameResxKey">卡片顯示名稱 resx key（Features.EmsCardSetting.Views.Index.resx）</param>
/// <param name="szGridColumnCss">卡片外框 col-* class（設定頁版面預覽用 — /EMS 實際渲染的外框在 partial 內，兩處須一致）</param>
/// <param name="szIconCss">Font Awesome icon class（設定頁預覽/隱藏清單顯示用，與卡片 header icon 一致）</param>
/// <param name="nDefaultOrder">預設顯示順序（DB 無覆寫時使用；新卡片排在所有 DB 列之後依此排序）</param>
public record EmsCardDefinition(
    string szCardKey, string szPartialViewName, string szNameResxKey,
    string szGridColumnCss, string szIconCss, int nDefaultOrder);

/// <summary>
/// EMS 首頁卡片註冊表 — 卡片定義的唯一真相來源。
/// 設定頁（/EmsCardSetting）與 /EMS 渲染都吃「本表 merge DB 覆寫」後的生效清單，
/// DB（EmsCardSetting 表）只存覆寫（隱藏/順序）。
///
/// 新增卡片 SOP（不需要改 /EmsCardSetting 頁本身）：
/// ① 卡片 HTML 寫成 Features/Ems/Views/_CardXxx.cshtml partial（含自己的 col-* 外框）
/// ② 本表加一筆（col-* / icon 與 partial 一致，設定頁預覽才對得上）
/// ③ Features.EmsCardSetting.Views.Index.resx（zh/en）加 emscard.name.{key}
/// ④ 該卡驅動 JS 的 init 必須以「根元素是否存在」防呆（卡片被關閉時 DOM 不渲染）
/// </summary>
public static class EmsCardRegistry
{
    public static readonly EmsCardDefinition[] Cards =
    [
        new("MainMeter", "_CardMainMeter", "emscard.name.mainmeter", "col-12",                     "fa-star",                1),
        new("Demand",    "_CardDemand",    "emscard.name.demand",    "col-md-6 col-lg-5 col-xl-4", "fa-tachometer-alt",      2),
        new("EnergyBar", "_CardEnergyBar", "emscard.name.energybar", "col-md-6 col-lg-7 col-xl-8", "fa-chart-bar",           3),
        new("EnergyPie", "_CardEnergyPie", "emscard.name.energypie", "col-md-6 col-lg-5 col-xl-4", "fa-chart-pie",           4),
        new("Yoy",       "_CardYoy",       "emscard.name.yoy",       "col-md-6 col-lg-7 col-xl-8", "fa-balance-scale",       5),
        new("Cost",      "_CardCost",      "emscard.name.cost",      "col-md-6 col-lg-5 col-xl-4", "fa-file-invoice-dollar", 6),
        new("CostYoy",   "_CardCostYoy",   "emscard.name.costyoy",   "col-md-6 col-lg-7 col-xl-8", "fa-scale-balanced",      7),
        new("WaterUsageBar", "_CardWaterUsageBar", "emscard.name.waterusagebar", "col-md-6 col-lg-7 col-xl-8", "fa-chart-bar",           8),
        new("WaterUsagePie", "_CardWaterUsagePie", "emscard.name.waterusagepie", "col-md-6 col-lg-5 col-xl-4", "fa-chart-pie",           9),
        new("WaterCost",     "_CardWaterCost",     "emscard.name.watercost",     "col-md-6 col-lg-5 col-xl-4", "fa-hand-holding-droplet", 10),
        new("GasUsageBar",   "_CardGasUsageBar",   "emscard.name.gasusagebar",   "col-md-6 col-lg-7 col-xl-8", "fa-chart-bar",           11),
        new("GasUsagePie",   "_CardGasUsagePie",   "emscard.name.gasusagepie",   "col-md-6 col-lg-5 col-xl-4", "fa-chart-pie",           12),
        new("GasCost",       "_CardGasCost",       "emscard.name.gascost",       "col-md-6 col-lg-5 col-xl-4", "fa-fire",                13),
        new("ChillerEfficiency",    "_CardChillerEfficiency",    "emscard.name.chillerefficiency",    "col-md-6 col-lg-5 col-xl-4", "fa-temperature-low",     14),
        new("CompressorEfficiency", "_CardCompressorEfficiency", "emscard.name.compressorefficiency", "col-md-6 col-lg-5 col-xl-4", "fa-compress-arrows-alt", 15),
    ];

    /// <summary>卡片鍵是否存在於註冊表（Save 白名單檢查用）</summary>
    public static bool Contains(string szCardKey) =>
        Cards.Any(c => string.Equals(c.szCardKey, szCardKey, StringComparison.OrdinalIgnoreCase));
}
