using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Web.Features.TariffSetting.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 電費設定 — 台電各類電價方案 + 使用者自建方案的讀寫與驗證，以及「採用時間軸」選版。
/// 台電預設值：Setting/tariff-taipower-defaults.json（唯讀 seed，隨程式部署）；
/// 使用者設定：SystemSettings.electricity_tariff（整份 JSON，整份載入整份儲存）。
/// DB 無值時回傳 seed；DB 有值時以 DB 為準，seed 新增的方案自動補齊（by szPlanId）。
///
/// 選版語意（與水費差異）：計價某日時取 adoptions 中生效日 &lt;= 該日的最新一筆；
/// 查無適用（日期早於所有生效日 / 時間軸為空 / 指向已刪方案）一律回 null = 該時段不計價
/// （水費是退回最早方案 — 水費必然有台水公告費率，電費則沿用「未選方案不計算」的既有行為）。
/// </summary>
public class TariffSettingService
{
    private const string SettingKey = "electricity_tariff";

    /// <summary>使用者自建方案的類別代碼（與台電 lighting/lv/hv/ehv 分開列示）</summary>
    public const string CustomCategory = "custom";

    /// <summary>契約容量歷史可記錄的契約別（= 基本電費 kW 類 szKey）</summary>
    public static readonly string[] ContractKeys =
        ["regular_kw", "nonsummer_kw", "semipeak_kw", "sat_semipeak_kw", "offpeak_kw"];

    /// <summary>舊資料遷移用的極早生效日 — 任何歷史日期都選得到，確保歷史電費數字不變</summary>
    private const string LegacyEffectiveDate = "2000-01-01";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private readonly ILogger<TariffSettingService> _logger;
    private readonly DatabaseConfigService _configService;
    private readonly IWebHostEnvironment _env;
    private string _szConnectionString = string.Empty;

    // seed 檔內容快取（部署後不變，重啟才重讀）
    private static volatile TariffConfig? _cachedSeed;

    public TariffSettingService(
        ILogger<TariffSettingService> logger,
        DatabaseConfigService configService,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _configService = configService;
        _env = env;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ---------- seed ----------

    /// <summary>載入台電預設 seed（缺檔明確報錯 — 部署缺漏要立即暴露，不能靜默給空設定）</summary>
    public TariffConfig GetSeed()
    {
        var cached = _cachedSeed;
        if (cached != null) return cached;

        var szPath = Path.Combine(_env.ContentRootPath, "Setting", "tariff-taipower-defaults.json");
        if (!File.Exists(szPath))
            throw new FileNotFoundException($"找不到台電電價預設檔：{szPath}", szPath);

        var seed = JsonSerializer.Deserialize<TariffConfig>(File.ReadAllText(szPath), _jsonOptions)
            ?? throw new InvalidDataException($"台電電價預設檔解析失敗：{szPath}");
        _cachedSeed = seed;
        return seed;
    }

    // ---------- 讀取 ----------

    /// <summary>
    /// 取得整份設定 — DB 無值回 seed；有值以 DB 為準並補齊 seed 新增方案。
    /// 舊資料（只有 szActivePlanId、無 adoptions）在記憶體中自動遷移為一筆極早生效的採用紀錄。
    /// </summary>
    public async Task<TariffConfig> GetConfigAsync()
    {
        var seed = GetSeed();

        string? szJson;
        using (var conn = await GetConnectionAsync())
        {
            szJson = await conn.QueryFirstOrDefaultAsync<string?>(
                "SELECT SettingValue FROM SystemSettings WHERE SettingKey = @SettingKey",
                new { SettingKey });
        }

        if (string.IsNullOrWhiteSpace(szJson))
            return Clone(seed);

        TariffConfig config;
        try
        {
            config = JsonSerializer.Deserialize<TariffConfig>(szJson, _jsonOptions) ?? Clone(seed);
        }
        catch (JsonException ex)
        {
            // DB 內容損毀 → 回 seed（不覆寫 DB，留給使用者儲存時重建）
            _logger.LogError(ex, "SystemSettings.{Key} JSON 解析失敗，改用台電預設", SettingKey);
            return Clone(seed);
        }

        // seed 新增方案（台電新版電價表）自動補齊
        var savedIds = config.plans.Select(p => p.szPlanId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var seedPlan in seed.plans)
        {
            if (!savedIds.Contains(seedPlan.szPlanId))
                config.plans.Add(Clone(seedPlan));
        }

        MigrateLegacyActivePlan(config);
        return config;
    }

    // ---------- 寫入 ----------

    /// <summary>
    /// 儲存單一方案（整份設定讀出 → 替換／新增 → 存回）。驗證失敗丟 ArgumentException。
    /// 既有方案：szCategory/szType 以既有值為準（防前端竄改造成渲染錯亂），custom 方案例外可改型態；
    /// 不存在的方案：視為新增自建方案，強制歸入 custom 類別。
    /// </summary>
    public async Task SavePlanAsync(TariffPlan plan)
    {
        var config = await GetConfigAsync();
        var nIdx = config.plans.FindIndex(p =>
            string.Equals(p.szPlanId, plan.szPlanId, StringComparison.OrdinalIgnoreCase));

        if (nIdx < 0)
        {
            // 新增 → 一律為使用者自建方案（台電 seed 方案只能由 seed 檔帶入）
            plan.szCategory = CustomCategory;
        }
        else if (IsCustom(config.plans[nIdx]))
        {
            // 自建方案：類別鎖 custom，型態可改（三型皆可自建）
            plan.szCategory = CustomCategory;
        }
        else
        {
            plan.szCategory = config.plans[nIdx].szCategory;
            plan.szType = config.plans[nIdx].szType;
            plan.szName = config.plans[nIdx].szName;   // seed 方案顯示名走 i18n，不吃前端輸入
        }

        var szError = ValidatePlan(plan);
        if (szError != null)
            throw new ArgumentException(szError);
        if (IsCustom(plan) && string.IsNullOrWhiteSpace(plan.szName))
            throw new ArgumentException("自建方案名稱不可為空");

        if (nIdx < 0) config.plans.Add(plan);
        else config.plans[nIdx] = plan;

        await SaveConfigAsync(config);
        _logger.LogInformation("電費設定已{Action}方案 {PlanId}", nIdx < 0 ? "新增" : "更新", plan.szPlanId);
    }

    /// <summary>
    /// 設為採用方案 = 在時間軸補（或覆蓋）一筆「今日起採用」。
    /// 時間軸才是唯一真相，因此本操作不直接寫 szActivePlanId（存檔時由 adoptions 重算）。
    /// </summary>
    public async Task SetActivePlanAsync(string szPlanId)
    {
        var config = await GetConfigAsync();
        if (!config.plans.Any(p => string.Equals(p.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"方案不存在：{szPlanId}");

        var szToday = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        config.adoptions.RemoveAll(a => a.szEffectiveDate == szToday);
        config.adoptions.Add(new TariffAdoption { szEffectiveDate = szToday, szPlanId = szPlanId });

        await SaveConfigAsync(config);
        _logger.LogInformation("電費設定自 {Date} 起採用方案 {PlanId}", szToday, szPlanId);
    }

    /// <summary>還原單一方案為台電預設，回傳還原後的方案。自建方案無 seed 可還原 → 明確報錯。</summary>
    public async Task<TariffPlan> ResetPlanAsync(string szPlanId)
    {
        var seed = GetSeed();
        var seedPlan = seed.plans.FirstOrDefault(p =>
            string.Equals(p.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase));
        if (seedPlan == null)
        {
            var config0 = await GetConfigAsync();
            var existing = config0.plans.FirstOrDefault(p =>
                string.Equals(p.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase));
            throw new ArgumentException(existing != null
                ? $"自建方案無台電預設可還原：{szPlanId}"
                : $"方案不存在：{szPlanId}");
        }

        var config = await GetConfigAsync();
        var nIdx = config.plans.FindIndex(p =>
            string.Equals(p.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase));
        var restored = Clone(seedPlan);
        if (nIdx >= 0) config.plans[nIdx] = restored;
        else config.plans.Add(restored);

        await SaveConfigAsync(config);
        _logger.LogInformation("電費設定方案 {PlanId} 已還原台電預設", szPlanId);
        return restored;
    }

    /// <summary>
    /// 刪除自建方案 — 台電 seed 方案不可刪（載入時會自動補回）；
    /// 仍被採用時間軸引用者拒絕（否則該時段的歷史會突然沒方案可算）。
    /// </summary>
    public async Task DeletePlanAsync(string szPlanId)
    {
        var config = await GetConfigAsync();
        var plan = config.plans.FirstOrDefault(p =>
            string.Equals(p.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"方案不存在：{szPlanId}");

        if (!IsCustom(plan))
            throw new ArgumentException($"台電預設方案不可刪除：{szPlanId}");

        var used = config.adoptions
            .Where(a => string.Equals(a.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.szEffectiveDate)
            .ToList();
        if (used.Count > 0)
            throw new ArgumentException($"方案仍被採用時間軸引用（生效日 {string.Join("、", used)}），請先移除該採用紀錄再刪除方案");

        config.plans.RemoveAll(p => string.Equals(p.szPlanId, szPlanId, StringComparison.OrdinalIgnoreCase));
        await SaveConfigAsync(config);
        _logger.LogInformation("電費設定已刪除自建方案 {PlanId}", szPlanId);
    }

    /// <summary>
    /// 存回採用時間軸 + 方案目錄（電費設定頁「儲存採用時間軸」）— 契約容量歷史沿用 DB 現值，
    /// 避免前端只送兩個欄位時把 contracts 清空。
    /// </summary>
    public async Task SaveAdoptionsAndPlansAsync(List<TariffAdoption>? adoptions, List<TariffPlan>? plans)
    {
        var existing = await GetConfigAsync();
        await SaveConfigAsync(new TariffConfig
        {
            adoptions = adoptions ?? [],
            plans = plans ?? [],
            contracts = existing.contracts,
        });
    }

    /// <summary>存回契約容量歷史（整份覆蓋 contracts，方案與時間軸沿用現值）。驗證失敗丟 ArgumentException。</summary>
    public async Task SaveContractsAsync(List<ContractCapacityRecord>? contracts)
    {
        var config = await GetConfigAsync();
        config.contracts = (contracts ?? [])
            .OrderBy(c => c.szEffectiveDate?.Trim(), StringComparer.Ordinal)
            .ToList();
        await SaveConfigAsync(config);
        _logger.LogInformation("電費設定契約容量歷史已更新（{Count} 筆）", config.contracts.Count);
    }

    /// <summary>
    /// 儲存整份設定 — 存前驗證所有方案與採用時間軸，並重算衍生欄位 szActivePlanId。
    /// 驗證失敗丟 ArgumentException。
    /// </summary>
    public async Task SaveConfigAsync(TariffConfig config)
    {
        config.plans ??= [];
        config.adoptions ??= [];
        if (config.plans.Count == 0)
            throw new ArgumentException("至少須保留一個方案");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in config.plans)
        {
            var szPlanError = ValidatePlan(plan);
            if (szPlanError != null)
                throw new ArgumentException($"{PlanDisplay(plan)}：{szPlanError}");
            if (!ids.Add(plan.szPlanId))
                throw new ArgumentException($"方案 Id 重複：{plan.szPlanId}");
        }

        var (isValid, szError) = ValidateAdoptions(config);
        if (!isValid)
            throw new ArgumentException(szError);

        config.contracts ??= [];
        var (isContractsValid, szContractsError) = ValidateContracts(config.contracts);
        if (!isContractsValid)
            throw new ArgumentException(szContractsError);

        // szActivePlanId 為衍生欄位 — 每次存檔由時間軸反推今日採用方案
        config.szActivePlanId = SelectPlanForDate(config, DateTime.Today)?.szPlanId ?? string.Empty;

        var szJson = JsonSerializer.Serialize(config, _jsonOptions);
        const string szSql = @"
            IF EXISTS (SELECT * FROM SystemSettings WHERE SettingKey = @SettingKey)
                UPDATE SystemSettings SET SettingValue = @szJson, UpdatedAt = GETDATE() WHERE SettingKey = @SettingKey;
            ELSE
                INSERT INTO SystemSettings (SettingKey, SettingValue, UpdatedAt) VALUES (@SettingKey, @szJson, GETDATE());";
        using var conn = await GetConnectionAsync();
        await conn.ExecuteAsync(szSql, new { SettingKey, szJson });
    }

    // ---------- static 純邏輯（單元測試用） ----------

    /// <summary>是否為使用者自建方案</summary>
    public static bool IsCustom(TariffPlan plan) =>
        string.Equals(plan.szCategory, CustomCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 依日期選用生效方案 — adoptions 中生效日 &lt;= dtDate 的最新一筆，再由 plans 查方案。
    /// 查無適用（時間軸為空 / 日期早於所有生效日 / 指向已刪除方案）一律回 null = 該時段不計價。
    /// 同一生效日有多筆時取後定義者（ValidateAdoptions 已擋重複，此為防禦性行為）。
    /// </summary>
    public static TariffPlan? SelectPlanForDate(TariffConfig config, DateTime dtDate)
    {
        if (config?.adoptions == null || config.adoptions.Count == 0 || config.plans == null) return null;

        TariffAdoption? best = null;
        var dtBest = DateTime.MinValue;
        foreach (var adoption in config.adoptions)
        {
            if (!TryParseDate(adoption.szEffectiveDate, out var dtEffective)) continue;
            if (dtEffective > dtDate.Date) continue;
            if (best == null || dtEffective >= dtBest)
            {
                best = adoption;
                dtBest = dtEffective;
            }
        }
        if (best == null) return null;

        var szBestPlanId = best.szPlanId;
        return config.plans.FirstOrDefault(p =>
            string.Equals(p.szPlanId, szBestPlanId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 依日期選用有效的契約容量紀錄 — 生效日 &lt;= dtDate 的最新一筆；查無（未設定 / 早於所有生效日）回 null。
    /// </summary>
    public static ContractCapacityRecord? SelectContractForDate(TariffConfig config, DateTime dtDate)
    {
        if (config?.contracts == null || config.contracts.Count == 0) return null;

        ContractCapacityRecord? best = null;
        var dtBest = DateTime.MinValue;
        foreach (var record in config.contracts)
        {
            if (!TryParseDate(record.szEffectiveDate, out var dtEffective)) continue;
            if (dtEffective > dtDate.Date) continue;
            if (best == null || dtEffective >= dtBest)
            {
                best = record;
                dtBest = dtEffective;
            }
        }
        return best;
    }

    /// <summary>契約紀錄取某契約別 kW（缺 key 或負值視為 0）</summary>
    public static double ContractKw(ContractCapacityRecord? record, string szKey) =>
        record?.kw != null && record.kw.TryGetValue(szKey, out var dKw) && dKw > 0 ? dKw : 0;

    /// <summary>
    /// 契約容量歷史驗證 — 生效日 yyyy-MM-dd 且不重複；kW 不可為負；key 須為 <see cref="ContractKeys"/>；
    /// 每筆至少一個契約別 &gt; 0。空清單合法。
    /// </summary>
    public static (bool isValid, string szError) ValidateContracts(List<ContractCapacityRecord>? contracts)
    {
        if (contracts == null || contracts.Count == 0) return (true, string.Empty);

        var seenDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in contracts)
        {
            var szDate = record.szEffectiveDate?.Trim() ?? string.Empty;
            if (!TryParseDate(szDate, out _))
                return (false, $"契約容量生效日格式不正確（應為 yyyy-MM-dd）：{record.szEffectiveDate}");
            if (!seenDates.Add(szDate))
                return (false, $"契約容量生效日重複：{szDate}");

            var kw = record.kw ?? [];
            foreach (var (szKey, dKw) in kw)
            {
                if (!ContractKeys.Contains(szKey))
                    return (false, $"契約容量生效日 {szDate} 含未知的契約別：{szKey}");
                if (double.IsNaN(dKw) || dKw < 0)
                    return (false, $"契約容量生效日 {szDate} 的 kW 不可為負數");
            }
            if (!kw.Values.Any(v => v > 0))
                return (false, $"契約容量生效日 {szDate} 至少須填一個契約別的 kW");
        }
        return (true, string.Empty);
    }

    /// <summary>
    /// 舊資料相容（記憶體遷移，不寫 DB）— 只有 szActivePlanId 而無 adoptions 時，
    /// 補一筆極早生效日的採用紀錄，使任何歷史日期都選到同一方案 → 歷史電費數字完全不變。
    /// </summary>
    public static void MigrateLegacyActivePlan(TariffConfig config)
    {
        if (config == null) return;
        config.adoptions ??= [];
        if (config.adoptions.Count > 0) return;
        if (string.IsNullOrWhiteSpace(config.szActivePlanId)) return;

        config.adoptions.Add(new TariffAdoption
        {
            szEffectiveDate = LegacyEffectiveDate,
            szPlanId = config.szActivePlanId,
        });
    }

    /// <summary>
    /// 採用時間軸驗證 — 生效日須為 yyyy-MM-dd；szPlanId 非空且須存在於 plans；同一生效日不可重複。
    /// 空清單合法（= 尚未採用任何方案）。
    /// </summary>
    public static (bool isValid, string szError) ValidateAdoptions(TariffConfig config)
    {
        if (config == null) return (false, "設定不可為空");
        if (config.adoptions == null || config.adoptions.Count == 0) return (true, string.Empty);

        var planIds = (config.plans ?? []).Select(p => p.szPlanId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var adoption in config.adoptions)
        {
            var szDate = adoption.szEffectiveDate?.Trim() ?? string.Empty;
            if (!TryParseDate(szDate, out _))
                return (false, $"採用生效日格式不正確（應為 yyyy-MM-dd）：{adoption.szEffectiveDate}");
            if (string.IsNullOrWhiteSpace(adoption.szPlanId))
                return (false, $"採用生效日 {szDate} 未選擇方案");
            if (!planIds.Contains(adoption.szPlanId))
                return (false, $"採用生效日 {szDate} 指向不存在的方案：{adoption.szPlanId}");
            if (!seenDates.Add(szDate))
                return (false, $"採用生效日重複：{szDate}");
        }
        return (true, string.Empty);
    }

    // ---------- 驗證 ----------

    private static readonly string[] _dayTypes = ["weekday", "sat", "sun_offday"];
    private static readonly string[] _seasons = ["summer", "nonsummer"];

    /// <summary>方案驗證 — 回傳錯誤訊息，null = 通過</summary>
    public static string? ValidatePlan(TariffPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.szPlanId))
            return "方案 Id 不可為空";

        if (!TryParseMonthDay(plan.szSummerStart, out _, out _) ||
            !TryParseMonthDay(plan.szSummerEnd, out _, out _))
            return "夏月起訖日期格式不正確（MM-dd）";

        foreach (var fee in plan.baseFees)
        {
            if (fee.dSummer is < 0 || fee.dNonSummer is < 0)
                return "基本電費單價不可為負數";
        }

        if (plan.surcharge != null)
        {
            if (plan.surcharge.nOverKwh <= 0)
                return "超額加價門檻度數必須大於 0";
            if (plan.surcharge.dPrice < 0)
                return "超額加價單價不可為負數";
        }

        return plan.szType switch
        {
            "progressive" => ValidateTiers(plan.tiers),
            "flat" => ValidateFlat(plan.flatRate),
            "tou" => ValidateFlowRates(plan.flowRates),
            _ => $"未知的方案型態：{plan.szType}",
        };
    }

    private static string? ValidateTiers(List<TariffTier> tiers)
    {
        if (tiers.Count == 0)
            return "累進級距不可為空";

        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            if (tier.dSummer < 0 || tier.dNonSummer < 0)
                return "級距單價不可為負數";

            var isLast = i == tiers.Count - 1;
            if (isLast)
            {
                if (tier.nTo != null)
                    return "最後一級距上限必須為「以上」（不設上限）";
            }
            else
            {
                if (tier.nTo == null)
                    return "只有最後一級距可以不設上限";
                if (tier.nTo <= tier.nFrom)
                    return $"級距上限必須大於下限（{tier.nFrom}~{tier.nTo}）";
                if (tiers[i + 1].nFrom != tier.nTo + 1)
                    return $"級距必須連續：{tier.nTo} 度之後應接 {tier.nTo + 1} 度";
            }
        }
        return null;
    }

    private static string? ValidateFlat(TariffFlatRate? flatRate)
    {
        if (flatRate == null)
            return "單一費率不可為空";
        if (flatRate.dSummer < 0 || flatRate.dNonSummer < 0)
            return "流動電費單價不可為負數";
        return null;
    }

    /// <summary>
    /// TOU 驗證 — 每（日別 × 季節）組：時段聯集覆蓋 00:00–24:00 且互不重疊（允許跨午夜）。
    /// </summary>
    private static string? ValidateFlowRates(List<TariffFlowRate> flowRates)
    {
        if (flowRates.Count == 0)
            return "時間電價時段不可為空";

        foreach (var rate in flowRates)
        {
            if (rate.dPrice < 0)
                return "流動電費單價不可為負數";
        }

        foreach (var szDayType in _dayTypes)
        {
            foreach (var szSeason in _seasons)
            {
                var group = flowRates
                    .Where(r => r.szDayType == szDayType && r.szSeason == szSeason)
                    .ToList();
                if (group.Count == 0)
                    return $"缺少時段定義：{DayTypeLabel(szDayType)} × {SeasonLabel(szSeason)}";

                var szError = ValidateCoverage(group, szDayType, szSeason);
                if (szError != null) return szError;
            }
        }
        return null;
    }

    private static string? ValidateCoverage(List<TariffFlowRate> group, string szDayType, string szSeason)
    {
        var szWhere = $"{DayTypeLabel(szDayType)} × {SeasonLabel(szSeason)}";
        // 展開為分鐘區間（跨午夜切成兩段）
        var intervals = new List<(int nStart, int nEnd)>();
        foreach (var rate in group)
        {
            if (rate.ranges.Count == 0)
                return $"{szWhere}：時段列缺少時間區間";
            foreach (var szRange in rate.ranges)
            {
                var parts = szRange.Split('-');
                if (parts.Length != 2 ||
                    !TryParseTime(parts[0], out var nStart) ||
                    !TryParseTime(parts[1], out var nEnd))
                    return $"{szWhere}：時間區間格式不正確（{szRange}，應為 HH:mm-HH:mm）";
                if (nStart == nEnd)
                    return $"{szWhere}：時間區間起訖不可相同（{szRange}）";

                if (nStart < nEnd)
                {
                    intervals.Add((nStart, nEnd));
                }
                else
                {
                    // 跨午夜 → 拆兩段
                    intervals.Add((nStart, 1440));
                    if (nEnd > 0) intervals.Add((0, nEnd));
                }
            }
        }

        intervals.Sort((a, b) => a.nStart.CompareTo(b.nStart));
        var nCursor = 0;
        foreach (var (nStart, nEnd) in intervals)
        {
            if (nStart < nCursor)
                return $"{szWhere}：時段重疊（{ToHHmm(nStart)} 前後）";
            if (nStart > nCursor)
                return $"{szWhere}：時段有空隙（{ToHHmm(nCursor)}~{ToHHmm(nStart)} 未涵蓋）";
            nCursor = nEnd;
        }
        if (nCursor != 1440)
            return $"{szWhere}：時段有空隙（{ToHHmm(nCursor)}~24:00 未涵蓋）";
        return null;
    }

    // ---------- 工具 ----------

    /// <summary>解析 "yyyy-MM-dd"（採用時間軸生效日）</summary>
    private static bool TryParseDate(string? szDate, out DateTime dtDate) =>
        DateTime.TryParseExact(szDate?.Trim() ?? string.Empty, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out dtDate);

    /// <summary>錯誤訊息用的方案顯示字（自建有名稱，seed 只有 i18n key → 用 Id）</summary>
    private static string PlanDisplay(TariffPlan plan) =>
        string.IsNullOrWhiteSpace(plan.szName) ? plan.szPlanId : $"{plan.szName}（{plan.szPlanId}）";

    /// <summary>解析 "HH:mm" 為當日分鐘數；"24:00" 視為 1440</summary>
    private static bool TryParseTime(string szTime, out int nMinutes)
    {
        nMinutes = 0;
        var parts = szTime.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var nHour) || !int.TryParse(parts[1], out var nMin)) return false;
        if (nHour == 24 && nMin == 0) { nMinutes = 1440; return true; }
        if (nHour < 0 || nHour > 23 || nMin < 0 || nMin > 59) return false;
        nMinutes = nHour * 60 + nMin;
        return true;
    }

    /// <summary>解析 "MM-dd"（以閏年 2000 驗 2/29 合法）</summary>
    private static bool TryParseMonthDay(string szMonthDay, out int nMonth, out int nDay)
    {
        nMonth = 0; nDay = 0;
        if (string.IsNullOrWhiteSpace(szMonthDay)) return false;
        var parts = szMonthDay.Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out nMonth) || !int.TryParse(parts[1], out nDay)) return false;
        if (nMonth < 1 || nMonth > 12) return false;
        return nDay >= 1 && nDay <= DateTime.DaysInMonth(2000, nMonth);
    }

    private static string ToHHmm(int nMinutes) => $"{nMinutes / 60:00}:{nMinutes % 60:00}";

    private static string DayTypeLabel(string szDayType) => szDayType switch
    {
        "weekday" => "週一至週五",
        "sat" => "週六",
        "sun_offday" => "週日及離峰日",
        _ => szDayType,
    };

    private static string SeasonLabel(string szSeason) =>
        szSeason == "summer" ? "夏月" : "非夏月";

    /// <summary>深拷貝（避免 seed 快取被呼叫端修改污染）</summary>
    private static T Clone<T>(T obj) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj, _jsonOptions), _jsonOptions)!;
}
