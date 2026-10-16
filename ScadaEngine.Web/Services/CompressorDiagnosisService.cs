using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;
using ScadaEngine.Web.Features.CompressorEfficiency.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 空壓系統規則式診斷 — Web 端資料組裝 + 設定 / 快照存取。
///
/// 本服務只做 DB 存取：
///   ① 撈 HistoryData「分鐘平均」（同 EquipmentHourlyAggregator 的 SQL 模式；KWH 累積點不撈）
///   ② 撈 EquipmentHourly（分析期 + 劣化基準期）、ElectricityCostHourly 期間平均單價、假日
///   ③ 組成 <see cref="CompressorDiagnosisInput"/> 交給 Common <see cref="CompressorDiagnosisEngine"/>（純函式，不呼叫 LLM）
/// 排程快照（最近 7 天滾動）由 <see cref="CompressorDiagnosisSchedulerService"/> 每日 02:00 與能源日報生成時呼叫
/// <see cref="GetOrRunScheduledAsync"/>（冪等，同一報告日只算一次）。
/// </summary>
public class CompressorDiagnosisService
{
    /// <summary>排程快照序列化：同一報告日避免日報與排程服務同時各算一份</summary>
    private static readonly SemaphoreSlim _scheduledLock = new(1, 1);

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    /// <summary>分鐘序列要撈的機台角色（KWH 為累積值，期間 kWh 走 EquipmentHourly）</summary>
    private static readonly string[] SeriesRoles =
        [EquipmentRoles.KW, EquipmentRoles.AIRFLOW, EquipmentRoles.DISCHPRESS, EquipmentRoles.LOADED, EquipmentRoles.RUN];

    private readonly DatabaseConfigService _configService;
    private readonly EquipmentProfileService _profileService;
    private readonly HolidayService _holidayService;
    private readonly ILogger<CompressorDiagnosisService> _logger;
    private string _szConnectionString = string.Empty;

    public CompressorDiagnosisService(
        DatabaseConfigService configService,
        EquipmentProfileService profileService,
        HolidayService holidayService,
        ILogger<CompressorDiagnosisService> logger)
    {
        _configService = configService;
        _profileService = profileService;
        _holidayService = holidayService;
        _logger = logger;
    }

    private async Task<SqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            _szConnectionString = await _configService.GetConnectionStringAsync();
        var conn = new SqlConnection(_szConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ─────────────────────────── 設定 ───────────────────────────

    public async Task<CompressorDiagnosisSettingModel> GetSettingAsync()
    {
        using var conn = await GetConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<(string? SettingJson, DateTime? UpdatedAt, string? UpdatedBy)>(
            "SELECT SettingJson, UpdatedAt, UpdatedBy FROM CompressorDiagnosisSetting WHERE Id = 1");
        CompressorDiagnosisSettingModel? m = null;
        if (!string.IsNullOrWhiteSpace(row.SettingJson))
        {
            try { m = JsonSerializer.Deserialize<CompressorDiagnosisSettingModel>(row.SettingJson, _json); }
            catch (JsonException ex) { _logger.LogWarning(ex, "空壓系統診斷設定 JSON 解析失敗，改用預設值"); }
        }
        m ??= new CompressorDiagnosisSettingModel();
        m.dtUpdatedAt = row.UpdatedAt;
        m.szUpdatedBy = row.UpdatedBy;
        return m;
    }

    /// <summary>
    /// 驗證 + 正規化（純函式，供單元測試）：需求壓力 / 電價非負、系統點位角色合法且單位可換算（Scale 伺服端重算）、
    /// 生產時段格式（起 &lt; 訖、星期 0..6）、基準期日期格式與先後、參數鍵已知且在範圍內（夾回）、確認清單去未知鍵。
    /// 違規丟 <see cref="CompressorDiagnosisValidationException"/>。
    /// </summary>
    public static void ValidateAndNormalize(CompressorDiagnosisSettingModel m)
    {
        if (m.dDemandPressBar is < 0 or > 100) throw new CompressorDiagnosisValidationException("demand_pressure_range");
        if (m.dDemandPressBar is 0) m.dDemandPressBar = null;
        if (m.dManualPrice is < 0 or > 1000) throw new CompressorDiagnosisValidationException("price_range");
        if (m.dManualPrice is 0) m.dManualPrice = null;

        var points = new Dictionary<string, CompressorSystemPointBinding>(StringComparer.Ordinal);
        foreach (var (szRole, b) in m.sysPoints ?? [])
        {
            if (b == null || string.IsNullOrWhiteSpace(b.szSID)) continue;
            if (!CompressorSystemRoles.All.Contains(szRole)) throw new CompressorDiagnosisValidationException("invalid_role", szRole);
            var quantity = CompressorSystemRoles.QuantityOf(szRole);
            var szUnit = EquipmentUnitScale.Canonicalize(quantity, b.szUnit);
            var dScale = szUnit == null ? null : EquipmentUnitScale.ScaleOf(quantity, szUnit);
            if (dScale == null) throw new CompressorDiagnosisValidationException("invalid_unit", $"{szRole}:{b.szUnit}");
            points[szRole] = new CompressorSystemPointBinding { szSID = b.szSID.Trim(), szUnit = szUnit, dScale = dScale.Value };
        }
        m.sysPoints = points;

        var ranges = new List<CompressorProductionRange>();
        foreach (var r in m.aProductionSchedule ?? [])
        {
            var nS = ProductionSchedule.ParseHm(r.szStart);
            var nE = ProductionSchedule.ParseHm(r.szEnd);
            if (r.nDay < 0 || r.nDay > 6 || nS == null || nE == null || nE <= nS)
                throw new CompressorDiagnosisValidationException("invalid_schedule", $"{r.nDay}:{r.szStart}-{r.szEnd}");
            ranges.Add(new CompressorProductionRange { nDay = r.nDay, szStart = FormatHm(nS.Value), szEnd = FormatHm(nE.Value) });
        }
        m.aProductionSchedule = ranges.OrderBy(r => r.nDay).ThenBy(r => r.szStart, StringComparer.Ordinal).ToList();

        m.szBaselineFrom = string.IsNullOrWhiteSpace(m.szBaselineFrom) ? null : m.szBaselineFrom.Trim();
        m.szBaselineTo = string.IsNullOrWhiteSpace(m.szBaselineTo) ? null : m.szBaselineTo.Trim();
        if ((m.szBaselineFrom == null) != (m.szBaselineTo == null))
            throw new CompressorDiagnosisValidationException("baseline_incomplete");
        if (m.szBaselineFrom != null)
        {
            var dtF = ParseDate(m.szBaselineFrom);
            var dtT = ParseDate(m.szBaselineTo);
            if (dtF == null || dtT == null || dtT < dtF) throw new CompressorDiagnosisValidationException("baseline_range");
        }

        var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (szKey, dValue) in m.paramOverrides ?? [])
        {
            var def = DiagnosisParameters.DefOf(szKey) ?? throw new CompressorDiagnosisValidationException("invalid_param", szKey);
            if (double.IsNaN(dValue) || dValue < def.dMin || dValue > def.dMax)
                throw new CompressorDiagnosisValidationException("param_range", $"{szKey}:{def.dMin}~{def.dMax}");
            overrides[szKey] = dValue;
        }
        m.paramOverrides = overrides;
        m.aConfirmedParams = (m.aConfirmedParams ?? []).Where(DiagnosisParameters.IsKnown).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public async Task SaveSettingAsync(CompressorDiagnosisSettingModel m, string? szUser)
    {
        ValidateAndNormalize(m);
        var szJson = JsonSerializer.Serialize(m, _json);
        using var conn = await GetConnectionAsync();
        var nRows = await conn.ExecuteAsync(
            "UPDATE CompressorDiagnosisSetting SET SettingJson = @Json, UpdatedAt = GETDATE(), UpdatedBy = @User WHERE Id = 1",
            new { Json = szJson, User = szUser });
        if (nRows == 0)
            await conn.ExecuteAsync(
                "INSERT INTO CompressorDiagnosisSetting (Id, SettingJson, UpdatedAt, UpdatedBy) VALUES (1, @Json, GETDATE(), @User)",
                new { Json = szJson, User = szUser });
    }

    private static string FormatHm(int nMin) => $"{nMin / 60:D2}:{nMin % 60:D2}";

    public static DateTime? ParseDate(string? sz) =>
        DateTime.TryParseExact(sz, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;

    // ─────────────────────────── 執行 ───────────────────────────

    /// <summary>有啟用中的空壓機才有診斷可做</summary>
    public async Task<bool> HasCompressorsAsync() =>
        (await _profileService.GetAllAsync(EquipmentKind.Compressor, isEnabledOnly: true)).Count > 0;

    /// <summary>指定期間 [dtFrom, dtTo) 執行診斷（不落 DB）；無啟用空壓機 → null</summary>
    public async Task<CompressorDiagnosisResult?> RunAsync(DateTime dtFrom, DateTime dtTo)
    {
        var profiles = await _profileService.GetAllAsync(EquipmentKind.Compressor, isEnabledOnly: true);
        if (profiles.Count == 0) return null;
        var setting = await GetSettingAsync();
        var input = await BuildInputAsync(profiles, setting, dtFrom, dtTo);
        return CompressorDiagnosisEngine.Run(input, DateTime.Now);
    }

    /// <summary>
    /// 組輸入：分鐘序列（各機 5 角色 + 系統 4 角色）、hourly、電價、生產時段、參數。
    /// </summary>
    private async Task<CompressorDiagnosisInput> BuildInputAsync(
        List<EquipmentProfileModel> profiles, CompressorDiagnosisSettingModel setting, DateTime dtFrom, DateTime dtTo)
    {
        var input = new CompressorDiagnosisInput
        {
            dtFrom = dtFrom,
            dtTo = dtTo,
            dDemandPressBar = setting.dDemandPressBar,
            parameters = DiagnosisParameters.FromOverrides(setting.paramOverrides, setting.aConfirmedParams),
        };
        var n = input.nMinutes;

        using var conn = await GetConnectionAsync();
        foreach (var p in profiles)
        {
            var s = new CompressorSeries
            {
                nId = p.nId,
                szName = p.szName,
                dRatedKw = p.dRatedPower,
                dRatedFlow = p.dRatedOutput,
                szControlType = p.szControlType,
                dMinLoadPct = p.dMinLoadPct,
                dMaxPressure = p.dMaxPressure,
                nMaxStartsPerHour = p.nMaxStartsPerHour,
            };
            foreach (var szRole in SeriesRoles)
            {
                var b = p.GetBinding(szRole);
                if (b == null || string.IsNullOrEmpty(b.szSID)) continue;
                var arr = await LoadMinuteSeriesAsync(conn, b.szSID, b.dScale, dtFrom, dtTo, n);
                switch (szRole)
                {
                    case EquipmentRoles.KW: s.aKw = arr; break;
                    case EquipmentRoles.AIRFLOW: s.aFlow = arr; break;
                    case EquipmentRoles.DISCHPRESS: s.aPress = arr; break;
                    case EquipmentRoles.LOADED: s.aLoaded = arr; break;
                    case EquipmentRoles.RUN: s.aRun = arr; break;
                }
            }
            input.aCompressors.Add(s);
        }

        async Task<double[]?> Sys(string szRole) =>
            setting.sysPoints.TryGetValue(szRole, out var b) && !string.IsNullOrEmpty(b.szSID)
                ? await LoadMinuteSeriesAsync(conn, b.szSID, b.dScale, dtFrom, dtTo, n)
                : null;
        input.system = new SystemSeries
        {
            aSupplyPress = await Sys(CompressorSystemRoles.SupplyPress),
            aUsePress = await Sys(CompressorSystemRoles.UsePress),
            aFlow = await Sys(CompressorSystemRoles.Flow),
            aDewpoint = await Sys(CompressorSystemRoles.Dewpoint),
        };

        var ids = profiles.Select(p => p.nId).ToArray();
        input.aHourly = await LoadHourlyAsync(conn, ids, dtFrom, dtTo);

        var dtBaseFrom = ParseDate(setting.szBaselineFrom);
        var dtBaseTo = ParseDate(setting.szBaselineTo);
        if (dtBaseFrom != null && dtBaseTo != null)
        {
            input.dtBaselineFrom = dtBaseFrom;
            input.dtBaselineTo = dtBaseTo.Value.AddDays(1);   // 含訖日 → 不含
            input.aBaselineHourly = await LoadHourlyAsync(conn, ids, input.dtBaselineFrom.Value, input.dtBaselineTo.Value);
        }

        var schedule = new ProductionSchedule
        {
            isHolidayNonProduction = setting.isHolidayNonProduction,
            holidays = await _holidayService.GetAllAsync(),
        };
        foreach (var r in setting.aProductionSchedule)
        {
            var nS = ProductionSchedule.ParseHm(r.szStart);
            var nE = ProductionSchedule.ParseHm(r.szEnd);
            if (nS != null && nE != null) schedule.aRanges.Add((r.nDay, nS.Value, nE.Value));
        }
        input.schedule = schedule;

        // 電價：期間電費逐時表實際平均單價優先；無（未設電價方案 / 累進制）→ 設定頁手填
        var cost = await conn.QueryFirstOrDefaultAsync<(double? C, double? K)>(@"
            SELECT SUM(Cost) AS C, SUM(Kwh) AS K FROM ElectricityCostHourly WITH (NOLOCK)
            WHERE  HourStart >= @From AND HourStart < @To AND Cost IS NOT NULL",
            new { From = dtFrom, To = dtTo }, commandTimeout: 120);
        if (cost.C is > 0 && cost.K is > 0)
        {
            input.dPrice = cost.C.Value / cost.K.Value;
            input.szPriceSource = "tariff";
        }
        else if (setting.dManualPrice is > 0)
        {
            input.dPrice = setting.dManualPrice;
            input.szPriceSource = "manual";
        }
        return input;
    }

    /// <summary>單一 SID 的分鐘平均（× Scale 換算基準單位）；無值分鐘 = NaN</summary>
    private static async Task<double[]> LoadMinuteSeriesAsync(SqlConnection conn, string szSid, double dScale, DateTime dtFrom, DateTime dtTo, int n)
    {
        var arr = new double[n];
        Array.Fill(arr, double.NaN);
        var rows = await conn.QueryAsync<(int M, double V)>(@"
            SELECT DATEDIFF(MINUTE, @t0, Timestamp) AS M, AVG(CAST(Value AS float)) AS V
            FROM   HistoryData WITH (NOLOCK)
            WHERE  SID = @sid AND Timestamp >= @t0 AND Timestamp < @t1 AND Quality = 1
            GROUP BY DATEDIFF(MINUTE, @t0, Timestamp)",
            new { sid = szSid, t0 = dtFrom, t1 = dtTo }, commandTimeout: 300);
        var k = dScale == 0 ? 1 : dScale;
        foreach (var r in rows)
            if (r.M >= 0 && r.M < n && !double.IsNaN(r.V)) arr[r.M] = r.V * k;
        return arr;
    }

    private static async Task<List<EquipmentHourlyModel>> LoadHourlyAsync(SqlConnection conn, int[] ids, DateTime dtFrom, DateTime dtTo)
    {
        if (ids.Length == 0) return [];
        var rows = await conn.QueryAsync<EquipmentHourlyModel>(@"
            SELECT ProfileId AS nProfileId, HourStart AS dtHourStart, Kwh AS dKwh, OutputQty AS dOutputQty,
                   RunHours AS dRunHours, AvgLoadPct AS dAvgLoadPct, LoadedHours AS dLoadedHours,
                   AvgPressure AS dAvgPressure, SampleCount AS nSampleCount, Quality AS nQuality
            FROM   EquipmentHourly WITH (NOLOCK)
            WHERE  ProfileId IN @Ids AND HourStart >= @From AND HourStart < @To",
            new { Ids = ids, From = dtFrom, To = dtTo }, commandTimeout: 120);
        return rows.ToList();
    }

    // ─────────────────────────── 快照 ───────────────────────────

    /// <summary>排程快照期間：報告日 D → [D−6 00:00, D+1 00:00)（最近 7 天滾動）</summary>
    public static (DateTime dtFrom, DateTime dtTo) ScheduledPeriod(DateTime dtReportDate)
    {
        var dtTo = dtReportDate.Date.AddDays(1);
        return (dtTo.AddDays(-DiagnosisParameters.ScheduledPeriodDays), dtTo);
    }

    /// <summary>
    /// 取（或生成）報告日 D 的排程快照（冪等）。無啟用空壓機 → null。
    /// 能源日報生成與 02:00 排程都呼叫這裡，誰先到誰算。
    /// </summary>
    public async Task<CompressorDiagnosisSnapshotDto?> GetOrRunScheduledAsync(DateTime dtReportDate)
    {
        var (dtFrom, dtTo) = ScheduledPeriod(dtReportDate);
        await _scheduledLock.WaitAsync();
        try
        {
            var existing = await GetScheduledAsync(dtTo);
            if (existing != null) return existing;
            var result = await RunAsync(dtFrom, dtTo);
            if (result == null) return null;
            var nId = await SaveSnapshotAsync(result, isScheduled: true, szRunBy: null);
            _logger.LogInformation("空壓系統診斷排程快照完成 {From:yyyy-MM-dd} ~ {To:yyyy-MM-dd}（#{Id}）", dtFrom, dtTo.AddDays(-1), nId);
            return new CompressorDiagnosisSnapshotDto { nId = nId, dtRunAt = result.dtRunAt, isScheduled = true, result = result };
        }
        finally
        {
            _scheduledLock.Release();
        }
    }

    public async Task<int> SaveSnapshotAsync(CompressorDiagnosisResult result, bool isScheduled, string? szRunBy)
    {
        using var conn = await GetConnectionAsync();
        return await conn.QuerySingleAsync<int>(@"
            INSERT INTO CompressorDiagnosisSnapshot (RunAt, PeriodFrom, PeriodTo, IsScheduled, ResultJson, RunBy)
            OUTPUT INSERTED.Id
            VALUES (@RunAt, @From, @To, @IsScheduled, @Json, @RunBy)",
            new
            {
                RunAt = result.dtRunAt, From = result.dtFrom, To = result.dtTo, IsScheduled = isScheduled,
                Json = JsonSerializer.Serialize(result, _json), RunBy = szRunBy,
            });
    }

    private async Task<CompressorDiagnosisSnapshotDto?> GetScheduledAsync(DateTime dtPeriodTo)
    {
        using var conn = await GetConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<SnapshotRow>(@"
            SELECT TOP 1 Id, RunAt, IsScheduled, ResultJson, RunBy FROM CompressorDiagnosisSnapshot
            WHERE  IsScheduled = 1 AND PeriodTo = @To ORDER BY Id DESC", new { To = dtPeriodTo });
        return ToDto(row);
    }

    /// <summary>最新一筆排程快照（一般使用者「系統診斷」tab）</summary>
    public async Task<CompressorDiagnosisSnapshotDto?> GetLatestScheduledAsync()
    {
        using var conn = await GetConnectionAsync();
        var row = await conn.QueryFirstOrDefaultAsync<SnapshotRow>(@"
            SELECT TOP 1 Id, RunAt, IsScheduled, ResultJson, RunBy FROM CompressorDiagnosisSnapshot
            WHERE  IsScheduled = 1 ORDER BY PeriodTo DESC, Id DESC");
        return ToDto(row);
    }

    private sealed class SnapshotRow
    {
        public int Id { get; set; }
        public DateTime RunAt { get; set; }
        public bool IsScheduled { get; set; }
        public string ResultJson { get; set; } = string.Empty;
        public string? RunBy { get; set; }
    }

    private CompressorDiagnosisSnapshotDto? ToDto(SnapshotRow? row)
    {
        if (row == null) return null;
        try
        {
            var result = JsonSerializer.Deserialize<CompressorDiagnosisResult>(row.ResultJson, _json);
            if (result == null) return null;
            return new CompressorDiagnosisSnapshotDto
            {
                nId = row.Id, dtRunAt = row.RunAt, isScheduled = row.IsScheduled, szRunBy = row.RunBy, result = result,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "空壓系統診斷快照 #{Id} JSON 解析失敗", row.Id);
            return null;
        }
    }
}
