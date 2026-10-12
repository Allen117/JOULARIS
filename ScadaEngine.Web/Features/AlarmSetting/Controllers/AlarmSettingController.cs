using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Models;
using ScadaEngine.Web.Features.AlarmSetting.Models;
using ScadaEngine.Web.Services;

namespace ScadaEngine.Web.Features.AlarmSetting.Controllers;

[Authorize]
[Route("[controller]")]
public class AlarmSettingController : Controller
{
    private readonly AlarmRuleService _alarmRuleService;
    private readonly LineTargetService _lineTargetService;
    private readonly LineTestSendService _lineTestSendService;
    private readonly EmailGroupService _emailGroupService;
    private readonly EmailSenderConfigService _emailSenderConfigService;
    private readonly EmailTestSendService _emailTestSendService;
    private readonly SmsTargetService _smsTargetService;
    private readonly SmsSenderConfigService _smsSenderConfigService;
    private readonly SmsTestSendService _smsTestSendService;
    private readonly SmsCommandPublisher _smsCommandPublisher;
    private readonly SmsStatusCache _smsStatusCache;
    private readonly NotifySettingReloadPublisher _notifyReloadPublisher;
    private readonly NotifyReloadStatusCache _notifyReloadStatusCache;
    private readonly IDataRepository _dataRepository;
    private readonly ILogger<AlarmSettingController> _logger;
    private readonly IStringLocalizer<AlarmSettingController> _l;

    public AlarmSettingController(
        AlarmRuleService alarmRuleService,
        LineTargetService lineTargetService,
        LineTestSendService lineTestSendService,
        EmailGroupService emailGroupService,
        EmailSenderConfigService emailSenderConfigService,
        EmailTestSendService emailTestSendService,
        SmsTargetService smsTargetService,
        SmsSenderConfigService smsSenderConfigService,
        SmsTestSendService smsTestSendService,
        SmsCommandPublisher smsCommandPublisher,
        SmsStatusCache smsStatusCache,
        NotifySettingReloadPublisher notifyReloadPublisher,
        NotifyReloadStatusCache notifyReloadStatusCache,
        IDataRepository dataRepository,
        ILogger<AlarmSettingController> logger,
        IStringLocalizer<AlarmSettingController> localizer)
    {
        _alarmRuleService = alarmRuleService;
        _lineTargetService = lineTargetService;
        _lineTestSendService = lineTestSendService;
        _emailGroupService = emailGroupService;
        _emailSenderConfigService = emailSenderConfigService;
        _emailTestSendService = emailTestSendService;
        _smsTargetService = smsTargetService;
        _smsSenderConfigService = smsSenderConfigService;
        _smsTestSendService = smsTestSendService;
        _smsCommandPublisher = smsCommandPublisher;
        _smsStatusCache = smsStatusCache;
        _notifyReloadPublisher = notifyReloadPublisher;
        _notifyReloadStatusCache = notifyReloadStatusCache;
        _dataRepository = dataRepository;
        _logger = logger;
        _l = localizer;
    }

    /// <summary>管理頁面</summary>
    [HttpGet("/AlarmSetting")]
    public async Task<IActionResult> Index()
    {
        var coordinatorList = (await _dataRepository.GetAllCoordinatorsAsync()).ToList();
        var pointList = (await _dataRepository.GetAllModbusPointsAsync())
            .OrderBy(p =>
            {
                var idx = p.szSID.IndexOf("-S", StringComparison.OrdinalIgnoreCase);
                return idx >= 0 && int.TryParse(p.szSID[(idx + 2)..], out var n) ? n : int.MaxValue;
            })
            .ToList();

        // 合併計算點位（另備 SID→群組 map 供選點器兩層瀏覽，不動 ModbusPointModel）
        var calcPointModels = (await _dataRepository.GetAllCalculatedPointsAsync())
            .Where(c => c.isEnabled)
            .ToList();
        pointList.AddRange(calcPointModels
            .Select(c => new ModbusPointModel { szSID = c.szSID, szName = c.szName, szUnit = c.szUnit }));
        var calcGroupMap = calcPointModels.ToDictionary(c => c.szSID, c => c.szGroupName ?? string.Empty);

        // 合併 DB 來源點位
        var dbPoints = (await _dataRepository.GetAllDbPointsAsync())
            .Select(p => new ModbusPointModel { szSID = p.szSID, szName = p.szName, szUnit = p.szUnit ?? string.Empty });
        pointList.AddRange(dbPoints);

        // 合併 OPC UA 來源點位
        var opcUaPoints = (await _dataRepository.GetAllOpcUaPointsAsync())
            .Select(p => new ModbusPointModel { szSID = p.szSID, szName = p.szName, szUnit = p.szUnit ?? string.Empty });
        pointList.AddRange(opcUaPoints);

        // 合併需量虛擬點位（DMD-{kWhSID}，供上限警報規則）
        var szDemandSuffix = _l["alarm.demand.point_suffix"].Value;
        pointList.AddRange((await _dataRepository.GetDemandPointInfosAsync())
            .Select(d => new ModbusPointModel { szSID = $"DMD-{d.szSID}", szName = $"{d.szName} {szDemandSuffix}", szUnit = "kW" }));

        // 合併迴路用電虛擬點位（NRGD-/NRGM-/NRGP-{circuitId}，全部迴路含虛擬節點，Engine 每 5 分鐘發布）
        var szNrgDailySuffix = _l["alarm.nrg.daily_suffix"].Value;
        var szNrgMonthlySuffix = _l["alarm.nrg.monthly_suffix"].Value;
        var szNrgPeriodSuffix = _l["alarm.nrg.period_suffix"].Value;
        var circuits = (await _dataRepository.GetAllEnergyCircuitInfosAsync()).ToList();
        foreach (var c in circuits)
        {
            pointList.Add(new ModbusPointModel { szSID = $"NRGD-{c.nId}", szName = $"{c.szName} {szNrgDailySuffix}", szUnit = "kWh" });
            pointList.Add(new ModbusPointModel { szSID = $"NRGM-{c.nId}", szName = $"{c.szName} {szNrgMonthlySuffix}", szUnit = "kWh" });
            pointList.Add(new ModbusPointModel { szSID = $"NRGP-{c.nId}", szName = $"{c.szName} {szNrgPeriodSuffix}", szUnit = "kWh" });
        }

        var rules = (await _alarmRuleService.GetAllRulesAsync()).ToList();
        var lineTargets = (await _lineTargetService.GetAllAsync()).ToList();

        // Email 群組（含 recipients + ruleIds 對應）
        var emailGroups = (await _emailGroupService.GetAllGroupsAsync()).ToList();
        var emailGroupBundles = new List<object>();
        foreach (var g in emailGroups)
        {
            var recipients = await _emailGroupService.GetRecipientsByGroupAsync(g.nId);
            var ruleIds = await _emailGroupService.GetRuleIdsByGroupAsync(g.nId);
            emailGroupBundles.Add(new
            {
                id = g.nId,
                name = g.szName,
                label = g.szLabel,
                maxSeverity = (int)g.nMaxSeverity,
                language = g.szLanguage,
                isEnabled = g.isEnabled,
                remarks = g.szRemarks,
                recipients = recipients.Select(r => new
                {
                    id = r.nId,
                    emailAddress = r.szEmailAddress,
                    displayName = r.szDisplayName,
                    isEnabled = r.isEnabled
                }).ToList(),
                alarmRuleIds = ruleIds
            });
        }
        var emailSenderConfig = _emailSenderConfigService.Read();

        // 從已發布 Designer 設計擷取 DI 點位的 ON/OFF 標籤，供前端同步顯示
        var designPages = await _dataRepository.LoadPublishedDesignAsync();
        var diLabelMap = ExtractDiLabelsFromDesigns(designPages);

        var smsTargets = (await _smsTargetService.GetAllAsync()).ToList();
        var smsSenderConfig = _smsSenderConfigService.Read();

        ViewBag.CoordinatorList = coordinatorList;
        ViewBag.PointList = pointList;
        ViewBag.Rules = rules;
        ViewBag.LineTargets = lineTargets;
        ViewBag.EmailGroups = emailGroupBundles;
        ViewBag.EmailSenderConfig = emailSenderConfig;
        ViewBag.SmsTargets = smsTargets;
        ViewBag.SmsSenderConfig = smsSenderConfig;
        ViewBag.DiLabelMap = diLabelMap;
        ViewBag.CalcGroupMap = calcGroupMap;
        ViewBag.Circuits = circuits;

        return View();
    }

    /// <summary>
    /// 解析 Designer 已發布頁面的 widget JSON，依 SID 蒐集 DI 點位的 ON/OFF 標籤。
    /// 包含頂層 diPoint widget 與 table widget 內 szPointType=DI 的 cell。
    /// 同 SID 已被多 widget 綁定時：以「非預設 ON/OFF」優先；否則保留先寫入者。
    /// </summary>
    private Dictionary<string, DiLabelDto> ExtractDiLabelsFromDesigns(IEnumerable<ScadaDesignPageModel> pages)
    {
        var map = new Dictionary<string, DiLabelDto>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.szWidgetStateJson)) continue;

            try
            {
                using var doc = JsonDocument.Parse(page.szWidgetStateJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var ws in doc.RootElement.EnumerateArray())
                {
                    if (ws.ValueKind != JsonValueKind.Object) continue;
                    var szType = ws.TryGetProperty("szType", out var t) ? t.GetString() : null;
                    if (!ws.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) continue;

                    if (szType == "diPoint")
                    {
                        var szSid = props.TryGetProperty("szSid", out var s) ? s.GetString() : null;
                        if (string.IsNullOrEmpty(szSid)) continue;
                        var on = props.TryGetProperty("szOnLabel", out var o) ? (o.GetString() ?? "ON") : "ON";
                        var off = props.TryGetProperty("szOffLabel", out var f) ? (f.GetString() ?? "OFF") : "OFF";
                        UpsertLabel(map, szSid, on, off);
                    }
                    else if (szType == "table")
                    {
                        if (!props.TryGetProperty("arrCells", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                        foreach (var row in arr.EnumerateArray())
                        {
                            if (row.ValueKind != JsonValueKind.Array) continue;
                            foreach (var cell in row.EnumerateArray())
                            {
                                if (cell.ValueKind != JsonValueKind.Object) continue;
                                var szPT = cell.TryGetProperty("szPointType", out var p1) ? p1.GetString() : null;
                                if (!string.Equals(szPT, "DI", StringComparison.OrdinalIgnoreCase)) continue;
                                var szSid = cell.TryGetProperty("szSid", out var s2) ? s2.GetString() : null;
                                if (string.IsNullOrEmpty(szSid)) continue;
                                var on = cell.TryGetProperty("szOnLabel", out var o2) ? (o2.GetString() ?? "ON") : "ON";
                                var off = cell.TryGetProperty("szOffLabel", out var f2) ? (f2.GetString() ?? "OFF") : "OFF";
                                UpsertLabel(map, szSid, on, off);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析 Designer 頁面 DI 標籤失敗：PageSid={PageSid}", page.szPageSid);
            }
        }

        return map;
    }

    private static void UpsertLabel(Dictionary<string, DiLabelDto> map, string szSid, string szOn, string szOff)
    {
        var isDefault = szOn == "ON" && szOff == "OFF";
        if (!map.TryGetValue(szSid, out var existing))
        {
            map[szSid] = new DiLabelDto { onLabel = szOn, offLabel = szOff };
            return;
        }
        // 已有預設值且新值非預設 → 用更具體的覆蓋
        var existingIsDefault = existing.onLabel == "ON" && existing.offLabel == "OFF";
        if (existingIsDefault && !isDefault)
            map[szSid] = new DiLabelDto { onLabel = szOn, offLabel = szOff };
    }

    // ── Line 通知收件群組 CRUD ──

    [HttpGet("~/api/line-targets")]
    public async Task<IActionResult> GetAllLineTargets()
    {
        var targets = await _lineTargetService.GetAllAsync();
        return Ok(targets);
    }

    [HttpPost("~/api/line-targets")]
    public async Task<IActionResult> SaveLineTarget([FromBody] LineTargetSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.groupId))
            return BadRequest(new { success = false, message = _l["alarm.error.group_id_required"].Value });
        if (string.IsNullOrWhiteSpace(dto.label))
            return BadRequest(new { success = false, message = _l["alarm.error.label_required"].Value });
        if (dto.maxSeverity > 3)
            return BadRequest(new { success = false, message = _l["alarm.error.max_severity_range"].Value });

        var isSuccess = await _lineTargetService.SaveAsync(dto);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.saved"].Value });
        return StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });
    }

    [HttpDelete("~/api/line-targets/{id}")]
    public async Task<IActionResult> DeleteLineTarget(int id)
    {
        var isSuccess = await _lineTargetService.DeleteAsync(id);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.deleted"].Value });
        return NotFound(new { success = false, message = _l["alarm.error.group_not_found"].Value });
    }

    [HttpPost("~/api/line-targets/{id}/toggle")]
    public async Task<IActionResult> ToggleLineTarget(int id, [FromBody] LineTargetToggleDto dto)
    {
        var isSuccess = await _lineTargetService.ToggleEnabledAsync(id, dto.isEnabled);
        if (isSuccess)
            return Ok(new { success = true });
        return StatusCode(500, new { success = false, message = _l["alarm.error.toggle_failed"].Value });
    }

    [HttpPost("~/api/line-targets/{id}/test")]
    public async Task<IActionResult> TestLineTarget(int id)
    {
        var target = await _lineTargetService.GetByIdAsync(id);
        if (target == null)
            return NotFound(new { success = false, message = _l["alarm.error.group_not_found"].Value });

        var result = await _lineTestSendService.SendTestAsync(target.szGroupId, target.szLabel, target.szLanguage ?? "zh-TW");
        if (result.isSuccess)
            return Ok(new { success = true, message = result.szMessage });

        if (result.isThrottled)
        {
            Response.Headers["Retry-After"] = result.nRetryAfterSeconds.ToString();
            return StatusCode(429, new
            {
                success = false,
                throttled = true,
                retryAfter = result.nRetryAfterSeconds,
                message = result.szMessage
            });
        }

        return StatusCode(502, new { success = false, message = result.szMessage });
    }

    /// <summary>取得所有規則（AJAX）</summary>
    [HttpGet("~/api/alarm-rules")]
    public async Task<IActionResult> GetAllRules()
    {
        var rules = await _alarmRuleService.GetAllRulesAsync();
        return Ok(rules);
    }

    /// <summary>取得單一 SID 的規則（ScadaPage 右鍵警報設定彈窗用）；無規則回 rule = null</summary>
    [HttpGet("~/api/alarm-rules/by-sid/{sid}")]
    public async Task<IActionResult> GetRuleBySid(string sid)
    {
        var rule = await _alarmRuleService.GetRuleBySidAsync(sid);
        return Ok(new { success = true, rule });
    }

    /// <summary>新增或更新規則（僅 Admin / Engineer）</summary>
    [HttpPost("~/api/alarm-rules")]
    [Authorize(Roles = "Admin,Engineer")]
    public async Task<IActionResult> SaveRule([FromBody] AlarmRuleSaveDto dto)
    {
        var szErrorKey = AlarmRuleService.ValidateRule(dto);
        if (szErrorKey != null)
            return BadRequest(new { success = false, message = _l[szErrorKey].Value });

        var isSuccess = await _alarmRuleService.SaveRuleAsync(dto);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.rule_saved"].Value });

        return StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });
    }

    /// <summary>刪除規則（僅 Admin / Engineer）</summary>
    [HttpDelete("~/api/alarm-rules/{id}")]
    [Authorize(Roles = "Admin,Engineer")]
    public async Task<IActionResult> DeleteRule(int id)
    {
        var isSuccess = await _alarmRuleService.DeleteRuleAsync(id);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.deleted"].Value });

        return NotFound(new { success = false, message = _l["alarm.error.rule_not_found"].Value });
    }

    // ── Email SMTP 設定 ──

    [HttpGet("~/api/email-config")]
    public IActionResult GetEmailConfig()
    {
        return Ok(_emailSenderConfigService.Read());
    }

    [HttpPost("~/api/email-config")]
    public async Task<IActionResult> SaveEmailConfig([FromBody] EmailSenderConfigDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.smtpHost))
            return BadRequest(new { success = false, message = _l["alarm.error.smtp_host_required"].Value });
        if (string.IsNullOrWhiteSpace(dto.fromAddress))
            return BadRequest(new { success = false, message = _l["alarm.error.from_address_required"].Value });

        var isSuccess = _emailSenderConfigService.Save(dto);
        if (!isSuccess)
            return StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });

        var (szReload, szMessage) = await ReloadEngineNotifySettingAsync(EmailSenderConfigService.FileName);
        return Ok(new { success = true, reload = szReload, message = szMessage });
    }

    // ── Email 群組 CRUD ──

    [HttpGet("~/api/email-groups")]
    public async Task<IActionResult> GetEmailGroups()
    {
        var groups = await _emailGroupService.GetAllGroupsAsync();
        return Ok(groups);
    }

    [HttpPost("~/api/email-groups")]
    public async Task<IActionResult> SaveEmailGroup([FromBody] EmailGroupSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.name))
            return BadRequest(new { success = false, message = _l["alarm.error.group_name_required"].Value });
        if (string.IsNullOrWhiteSpace(dto.label))
            return BadRequest(new { success = false, message = _l["alarm.error.label_required"].Value });
        if (dto.maxSeverity > 3)
            return BadRequest(new { success = false, message = _l["alarm.error.max_severity_range"].Value });

        var nId = await _emailGroupService.SaveGroupAsync(dto);
        if (nId > 0)
            return Ok(new { success = true, id = nId, message = _l["alarm.success.saved"].Value });
        return StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });
    }

    [HttpDelete("~/api/email-groups/{id}")]
    public async Task<IActionResult> DeleteEmailGroup(int id)
    {
        var isSuccess = await _emailGroupService.DeleteGroupAsync(id);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.deleted"].Value });
        return NotFound(new { success = false, message = _l["alarm.error.group_not_found"].Value });
    }

    [HttpPost("~/api/email-groups/{id}/toggle")]
    public async Task<IActionResult> ToggleEmailGroup(int id, [FromBody] EmailGroupToggleDto dto)
    {
        var isSuccess = await _emailGroupService.ToggleGroupEnabledAsync(id, dto.isEnabled);
        return isSuccess ? Ok(new { success = true })
                         : StatusCode(500, new { success = false, message = _l["alarm.error.toggle_failed"].Value });
    }

    // ── Email 收件人 CRUD ──

    [HttpGet("~/api/email-groups/{groupId}/recipients")]
    public async Task<IActionResult> GetEmailRecipients(int groupId)
    {
        var recipients = await _emailGroupService.GetRecipientsByGroupAsync(groupId);
        return Ok(recipients);
    }

    [HttpPost("~/api/email-recipients")]
    public async Task<IActionResult> SaveEmailRecipient([FromBody] EmailRecipientSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.emailAddress))
            return BadRequest(new { success = false, message = _l["alarm.error.email_required"].Value });
        if (dto.groupId <= 0 && (!dto.id.HasValue || dto.id.Value <= 0))
            return BadRequest(new { success = false, message = _l["alarm.error.group_required"].Value });

        var isSuccess = await _emailGroupService.SaveRecipientAsync(dto);
        return isSuccess ? Ok(new { success = true, message = _l["alarm.success.saved"].Value })
                         : StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });
    }

    [HttpDelete("~/api/email-recipients/{id}")]
    public async Task<IActionResult> DeleteEmailRecipient(int id)
    {
        var isSuccess = await _emailGroupService.DeleteRecipientAsync(id);
        return isSuccess ? Ok(new { success = true })
                         : NotFound(new { success = false, message = _l["alarm.error.recipient_not_found"].Value });
    }

    // ── Email 群組-規則對應 ──

    [HttpPost("~/api/email-groups/{id}/rules")]
    public async Task<IActionResult> SaveEmailGroupRules(int id, [FromBody] List<int> ruleIds)
    {
        var dto = new EmailGroupRuleMappingDto { groupId = id, alarmRuleIds = ruleIds ?? new List<int>() };
        var isSuccess = await _emailGroupService.SaveMappingAsync(dto);
        return isSuccess ? Ok(new { success = true, message = _l["alarm.success.saved"].Value })
                         : StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });
    }

    // ── 簡訊收訊號碼 CRUD ──

    [HttpGet("~/api/sms-targets")]
    public async Task<IActionResult> GetAllSmsTargets()
    {
        var targets = await _smsTargetService.GetAllAsync();
        return Ok(targets);
    }

    [HttpPost("~/api/sms-targets")]
    public async Task<IActionResult> SaveSmsTarget([FromBody] SmsTargetSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.phoneNumber))
            return BadRequest(new { success = false, message = _l["alarm.error.phone_required"].Value });
        if (!System.Text.RegularExpressions.Regex.IsMatch(dto.phoneNumber.Trim(), @"^\+?\d{8,15}$"))
            return BadRequest(new { success = false, message = _l["alarm.error.phone_invalid"].Value });
        if (string.IsNullOrWhiteSpace(dto.label))
            return BadRequest(new { success = false, message = _l["alarm.error.label_required"].Value });
        if (dto.maxSeverity > 3)
            return BadRequest(new { success = false, message = _l["alarm.error.max_severity_range"].Value });

        var isSuccess = await _smsTargetService.SaveAsync(dto);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.saved"].Value });
        return StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });
    }

    [HttpDelete("~/api/sms-targets/{id}")]
    public async Task<IActionResult> DeleteSmsTarget(int id)
    {
        var isSuccess = await _smsTargetService.DeleteAsync(id);
        if (isSuccess)
            return Ok(new { success = true, message = _l["alarm.success.deleted"].Value });
        return NotFound(new { success = false, message = _l["alarm.error.target_not_found"].Value });
    }

    [HttpPost("~/api/sms-targets/{id}/toggle")]
    public async Task<IActionResult> ToggleSmsTarget(int id, [FromBody] SmsTargetToggleDto dto)
    {
        var isSuccess = await _smsTargetService.ToggleEnabledAsync(id, dto.isEnabled);
        if (isSuccess)
            return Ok(new { success = true });
        return StatusCode(500, new { success = false, message = _l["alarm.error.toggle_failed"].Value });
    }

    [HttpPost("~/api/sms-targets/{id}/test")]
    public async Task<IActionResult> TestSmsTarget(int id)
    {
        var target = await _smsTargetService.GetByIdAsync(id);
        if (target == null)
            return NotFound(new { success = false, message = _l["alarm.error.target_not_found"].Value });

        var result = await _smsTestSendService.SendTestAsync(
            target.szPhoneNumber, target.szLabel, target.szLanguage ?? "zh-TW");
        if (result.isSuccess)
            return Ok(new { success = true, message = result.szMessage });

        if (result.isThrottled)
        {
            Response.Headers["Retry-After"] = result.nRetryAfterSeconds.ToString();
            return StatusCode(429, new
            {
                success = false,
                throttled = true,
                retryAfter = result.nRetryAfterSeconds,
                message = result.szMessage
            });
        }

        return StatusCode(502, new { success = false, message = result.szMessage });
    }

    // ── 簡訊盒設定 ──

    [HttpGet("~/api/sms-config")]
    public IActionResult GetSmsConfig()
    {
        return Ok(_smsSenderConfigService.Read());
    }

    [HttpPost("~/api/sms-config")]
    public async Task<IActionResult> SaveSmsConfig([FromBody] SmsSenderConfigDto dto)
    {
        var isSuccess = _smsSenderConfigService.Save(dto);
        if (!isSuccess)
            return StatusCode(500, new { success = false, message = _l["alarm.error.save_failed"].Value });

        var (szReload, szMessage) = await ReloadEngineNotifySettingAsync(SmsSenderConfigService.FileName);
        return Ok(new { success = true, reload = szReload, message = szMessage });
    }

    /// <summary>
    /// 存檔後請 Engine 熱重載該設定檔（MQTT SCADA/Sys/Notify/Reload → 等 SCADA/Sys/Notify/Status）。
    /// 回傳 (reload 狀態碼, 使用者訊息)：ok = Engine 已套用；failed = Engine 回報失敗（保留舊設定）；
    /// timeout = MQTT 未連或 Engine 離線（Engine 下次啟動本就會讀 Shared 新檔，結果一致）。
    /// </summary>
    private async Task<(string szReload, string szMessage)> ReloadEngineNotifySettingAsync(string szFileName)
    {
        var szRequestId = Guid.NewGuid().ToString("N");
        _notifyReloadStatusCache.Register(szRequestId);

        var isPublished = await _notifyReloadPublisher.PublishReloadAsync(szFileName, szRequestId);
        if (!isPublished)
        {
            await _notifyReloadStatusCache.WaitAsync(szRequestId, TimeSpan.Zero);   // 只為移除登記
            return ("timeout", _l["alarm.warn.saved_engine_no_response"].Value);
        }

        var result = await _notifyReloadStatusCache.WaitAsync(szRequestId, TimeSpan.FromSeconds(8));
        if (result == null)
            return ("timeout", _l["alarm.warn.saved_engine_no_response"].Value);
        if (!result.isSuccess)
        {
            _logger.LogWarning("Engine 回報 {File} 重載失敗: {Message}", szFileName, result.szMessage);
            return ("failed", _l["alarm.warn.saved_engine_reload_failed", result.szMessage].Value);
        }
        return ("ok", _l["alarm.success.saved_reloaded"].Value);
    }

    // ── 簡訊盒狀態（來自 SCADA/Sys/Sms/Status retained，Engine 每 60 秒與狀態變化時更新）──

    [HttpGet("~/api/sms-status")]
    public IActionResult GetSmsStatus()
    {
        var szJson = _smsStatusCache.PayloadJson;
        if (string.IsNullOrEmpty(szJson))
            return Ok(new { available = false });
        return Content($"{{\"available\":true,\"receivedAt\":\"{_smsStatusCache.ReceivedAt:yyyy-MM-dd HH:mm:ss}\",\"status\":{szJson}}}",
            "application/json");
    }

    /// <summary>重掃簡訊盒 COM port（Engine 代為執行）</summary>
    [HttpPost("~/api/sms-rescan")]
    public async Task<IActionResult> RescanSmsModem()
    {
        var isPublished = await _smsCommandPublisher.PublishRescanAsync();
        if (isPublished)
            return Ok(new { success = true, message = _l["alarm.success.sms_rescan_sent"].Value });
        return StatusCode(502, new { success = false, message = _l["alarm.error.mqtt_not_connected"].Value });
    }

    // ── Email 測試寄送 ──

    [HttpPost("~/api/email-recipients/{id}/test")]
    public async Task<IActionResult> TestEmailSend(int id)
    {
        // 取出收件人對應群組（用於語系）
        var groups = await _emailGroupService.GetAllGroupsAsync();
        EmailRecipientModel? recipient = null;
        EmailGroupModel? group = null;
        foreach (var g in groups)
        {
            var rs = await _emailGroupService.GetRecipientsByGroupAsync(g.nId);
            recipient = rs.FirstOrDefault(x => x.nId == id);
            if (recipient != null) { group = g; break; }
        }
        if (recipient == null || group == null)
            return NotFound(new { success = false, message = _l["alarm.error.recipient_not_found"].Value });

        var result = await _emailTestSendService.SendTestAsync(
            recipient.szEmailAddress, recipient.szDisplayName, group.szLanguage, group.szLabel);

        if (result.isSuccess)
            return Ok(new { success = true, message = result.szMessage });
        if (result.isThrottled)
        {
            Response.Headers["Retry-After"] = result.nRetryAfterSeconds.ToString();
            return StatusCode(429, new
            {
                success = false, throttled = true,
                retryAfter = result.nRetryAfterSeconds,
                message = result.szMessage
            });
        }
        return StatusCode(502, new { success = false, message = result.szMessage });
    }
}
