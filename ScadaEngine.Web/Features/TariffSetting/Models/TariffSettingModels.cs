namespace ScadaEngine.Web.Features.TariffSetting.Models;

/// <summary>
/// 電費設定整份組態 — 對應 SystemSettings.electricity_tariff 的 JSON value。
/// 台電預設值來自 Setting/tariff-taipower-defaults.json（唯讀 seed），
/// 使用者修改整份存回 DB；seed 新增方案時載入端自動補齊（by szPlanId）。
///
/// 「方案目錄 + 採用時間軸」雙層結構：
/// - plans = 目錄（台電 20 個 seed 方案為不同用戶類別「並存」的選項，彼此不是版本關係）
/// - adoptions = 唯一真相的採用時間軸（哪天起改用哪個方案），計價時依日期選版 → 歷史電費不被改費率追溯改寫
/// </summary>
public class TariffConfig
{
    /// <summary>
    /// 目前（今天）採用的方案 Id — 【衍生欄位】，每次存檔時由 adoptions 反推寫入。
    /// 唯一真相是 adoptions；此欄位只為既有讀取點與前端 badge 方便，不可單獨修改。
    /// </summary>
    public string szActivePlanId { get; set; } = string.Empty;

    /// <summary>採用時間軸（唯一真相）— 空清單 = 尚未採用任何方案（該時段不計價）</summary>
    public List<TariffAdoption> adoptions { get; set; } = [];

    /// <summary>方案目錄（台電 seed + 使用者自建）</summary>
    public List<TariffPlan> plans { get; set; } = [];

    /// <summary>
    /// 契約容量歷史（生效日 + 各契約別 kW）— 契約容量最佳化回測依每個計費月取當時有效值。
    /// 空清單 = 未設定（回測仍可給建議值，但無法算現行費用與節省金額）。
    /// </summary>
    public List<ContractCapacityRecord> contracts { get; set; } = [];
}

/// <summary>契約容量紀錄 — 「szEffectiveDate 起各契約別為 kw」（含當日）</summary>
public class ContractCapacityRecord
{
    /// <summary>生效日（yyyy-MM-dd，含當日）</summary>
    public string szEffectiveDate { get; set; } = string.Empty;

    /// <summary>
    /// 各契約別 kW — key = 基本電費 szKey（regular_kw / nonsummer_kw / semipeak_kw / sat_semipeak_kw / offpeak_kw）；
    /// 缺 key 視為 0。只有採用方案 baseFees 內有的 key 才會計費。
    /// </summary>
    public Dictionary<string, double> kw { get; set; } = [];
}

/// <summary>採用時間軸一列 — 「szEffectiveDate 起改用 szPlanId」（含當日）</summary>
public class TariffAdoption
{
    /// <summary>生效日（yyyy-MM-dd，含當日）</summary>
    public string szEffectiveDate { get; set; } = string.Empty;

    /// <summary>該日起採用的方案 Id（須存在於 TariffConfig.plans）</summary>
    public string szPlanId { get; set; } = string.Empty;
}

/// <summary>
/// 單一電價方案 — 統一模型容納台電三種型態：
/// progressive（累進級距）/ flat（單一費率+基本費）/ tou（時間電價時段+基本費）。
/// 方案與類別名稱走 i18n key（tariff.plan.{szPlanId} / tariff.category.{szCategory}），不存於 JSON。
/// </summary>
public class TariffPlan
{
    /// <summary>方案穩定識別碼（如 hv_tou2），同時是 i18n key 後綴</summary>
    public string szPlanId { get; set; } = string.Empty;

    /// <summary>
    /// 自建方案顯示名（使用者輸入，不在 i18n 範圍）；
    /// 空字串 = 沿用 i18n key tariff.plan.{szPlanId}（台電 seed 方案一律留空）
    /// </summary>
    public string szName { get; set; } = string.Empty;

    /// <summary>類別：lighting（表燈住商）| lv（低壓電力）| hv（高壓電力）| ehv（特高壓電力）| custom（使用者自建）</summary>
    public string szCategory { get; set; } = string.Empty;

    /// <summary>型態：progressive | flat | tou</summary>
    public string szType { get; set; } = string.Empty;

    /// <summary>夏月起（月-日，如 06-01；含當日）</summary>
    public string szSummerStart { get; set; } = string.Empty;

    /// <summary>夏月訖（月-日，如 09-30；含當日）</summary>
    public string szSummerEnd { get; set; } = string.Empty;

    /// <summary>方案備註 i18n key（可變動尖峰/批次生產/EV 適用條件等），null = 無</summary>
    public string? szNoteKey { get; set; }

    /// <summary>累進級距（progressive 用，其餘型態為空陣列）</summary>
    public List<TariffTier> tiers { get; set; } = [];

    /// <summary>單一費率（flat 用，其餘 null）</summary>
    public TariffFlatRate? flatRate { get; set; }

    /// <summary>基本電費項目（tou / flat 用）</summary>
    public List<TariffBaseFee> baseFees { get; set; } = [];

    /// <summary>流動電費時段列（tou 用）</summary>
    public List<TariffFlowRate> flowRates { get; set; } = [];

    /// <summary>月總度數超額加價（簡易型限定），null = 無</summary>
    public TariffSurcharge? surcharge { get; set; }
}

/// <summary>累進級距一列 — 每月用電 [nFrom, nTo] 度（含），nTo = null 表「以上」</summary>
public class TariffTier
{
    public int nFrom { get; set; }
    public int? nTo { get; set; }
    /// <summary>夏月每度單價（元）</summary>
    public double dSummer { get; set; }
    /// <summary>非夏月每度單價（元）</summary>
    public double dNonSummer { get; set; }
}

/// <summary>單一費率（低壓電力非時間電價）— 流動電費每度單價</summary>
public class TariffFlatRate
{
    public double dSummer { get; set; }
    public double dNonSummer { get; set; }
}

/// <summary>
/// 基本電費項目 — szKey 為固定詞彙（i18n key = tariff.basefee.{szKey}）：
/// per_household / per_household_1p / per_household_3p / device_household / device_kw /
/// demand_household / regular_kw / nonsummer_kw / semipeak_kw / sat_semipeak_kw / offpeak_kw
/// </summary>
public class TariffBaseFee
{
    public string szKey { get; set; } = string.Empty;
    /// <summary>計價單位：household（每戶每月）| kw（每瓩每月）</summary>
    public string szUnit { get; set; } = string.Empty;
    /// <summary>夏月單價（元）；null = 該季不適用（顯示 —）</summary>
    public double? dSummer { get; set; }
    /// <summary>非夏月單價（元）；null = 該季不適用</summary>
    public double? dNonSummer { get; set; }
}

/// <summary>
/// 流動電費時段列 — 一列 = 日別 × 季節 × 時段別 的時間區間組與單價。
/// 驗證單位為（日別 × 季節）：組內各列 ranges 聯集須覆蓋 24h 且互不重疊（允許跨午夜）。
/// </summary>
public class TariffFlowRate
{
    /// <summary>日別：weekday（週一至週五）| sat（週六）| sun_offday（週日及離峰日）</summary>
    public string szDayType { get; set; } = string.Empty;

    /// <summary>季節：summer | nonsummer</summary>
    public string szSeason { get; set; } = string.Empty;

    /// <summary>時段別：peak | semipeak | offpeak（預設顯示名走 i18n tariff.period.{szPeriod}）</summary>
    public string szPeriod { get; set; } = string.Empty;

    /// <summary>使用者自訂區間名稱；null/空 = 用 szPeriod 的 i18n 預設名</summary>
    public string? szName { get; set; }

    /// <summary>時間區間（"HH:mm-HH:mm"，可多段；"24:00" 允許作為訖點；起 &gt; 訖 = 跨午夜）</summary>
    public List<string> ranges { get; set; } = [];

    /// <summary>每度單價（元）</summary>
    public double dPrice { get; set; }
}

/// <summary>月總度數超額加價（簡易型時間電價限定）</summary>
public class TariffSurcharge
{
    /// <summary>門檻度數（超過部分加價）</summary>
    public int nOverKwh { get; set; }
    /// <summary>每度加價（元）</summary>
    public double dPrice { get; set; }
}

// ---------- Request DTOs ----------

/// <summary>設為採用方案（= 自今日起採用，等同在時間軸補一筆今日生效）</summary>
public class SetActivePlanRequest
{
    public string planId { get; set; } = string.Empty;
}

/// <summary>
/// 整份設定存回 — 只收 adoptions 與 plans；szActivePlanId 是衍生欄位，由後端重算，
/// 刻意不開放前端傳入（避免時間軸與指標打架）。
/// </summary>
public class SaveTariffConfigRequest
{
    public List<TariffAdoption> adoptions { get; set; } = [];
    public List<TariffPlan> plans { get; set; } = [];
}

/// <summary>契約容量歷史存回（整份覆蓋 contracts，不動方案與時間軸）</summary>
public class SaveContractsRequest
{
    public List<ContractCapacityRecord> contracts { get; set; } = [];
}

/// <summary>還原單一方案為台電預設</summary>
public class ResetPlanRequest
{
    public string planId { get; set; } = string.Empty;
}

/// <summary>電費設定頁 ViewModel（目前無需傳值，保留擴充）</summary>
public class TariffSettingViewModel
{
}
