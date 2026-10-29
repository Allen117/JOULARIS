using Microsoft.Extensions.Localization;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Web.Features.ScadaPage.Models;
using ScadaEngine.Web.Resources;
using System.Globalization;
using System.Text.Json;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 將 ScadaPage 控制動作（按鈕 / AO / DO / 泵浦）寫入 EventLog（EventType=3 資訊）。
///
/// 對外只暴露 LogAsync(actionType, cid, displayName, value, username) 一支方法。
/// 內部依 actionType 決定 i18n MessageKey 與 args，並組出當前 culture 的 fallback Message。
/// 顯示時 EventLog 頁面透過 AlarmMessageLocalizer.Localize 依使用者 culture 翻譯。
/// </summary>
public class ControlEventLogger
{
    private readonly ILogger<ControlEventLogger> _logger;
    private readonly EventLogService _eventLogService;
    private readonly IStringLocalizer<SharedResource> _l;

    private const byte EVENT_TYPE_INFO = 3;
    private const byte SEVERITY_NONE   = 4;

    public ControlEventLogger(
        ILogger<ControlEventLogger> logger,
        EventLogService eventLogService,
        IStringLocalizer<SharedResource> localizer)
    {
        _logger          = logger;
        _eventLogService = eventLogService;
        _l               = localizer;
    }

    /// <summary>
    /// 寫入一筆控制動作 EventLog。
    /// </summary>
    public async Task LogAsync(
        ControlActionType actionType,
        string szCid,
        string szDisplayName,
        double dValue,
        string szUsername)
    {
        if (actionType == ControlActionType.Unknown) return;
        if (string.IsNullOrWhiteSpace(szCid)) return;

        var szName = string.IsNullOrWhiteSpace(szDisplayName) ? szCid : szDisplayName;
        var szUser = string.IsNullOrWhiteSpace(szUsername)    ? "anonymous" : szUsername;

        var (szKey, aArgs) = BuildKeyAndArgs(actionType, szName, dValue, szUser);
        if (szKey is null) return;

        await WriteEventAsync(szKey, aArgs, szCid, actionType);
    }

    /// <summary>
    /// 寫入一筆點位組態變更 EventLog（ModbusCoordinator 點位熱編輯稽核）。
    /// value 為文字型變更摘要（如「Address: 30513 → 30514」），與控制動作的數值 value 不同故獨立一支。
    /// </summary>
    public async Task LogPointConfigChangedAsync(
        string szSid,
        string szPointName,
        string szChangeSummary,
        string szUsername)
    {
        if (string.IsNullOrWhiteSpace(szSid)) return;

        var args = new Dictionary<string, string>
        {
            ["username"] = string.IsNullOrWhiteSpace(szUsername) ? "anonymous" : szUsername,
            ["name"]     = string.IsNullOrWhiteSpace(szPointName) ? szSid : szPointName,
            ["value"]    = szChangeSummary,
        };

        await WriteEventAsync("control.action.point_config_changed", args, szSid, ControlActionType.PointConfigChanged);
    }

    /// <summary>
    /// 寫入一筆 Modbus 設備層組態變更 EventLog（改名「舊 → 新」、子設備名稱變更）。
    /// SID 用該設備首個站號的 SID 前綴（{Id*65536 + 首個ModbusId*256 + 1}，不帶 -S 序號）。
    /// </summary>
    public async Task LogCoordinatorConfigChangedAsync(
        string szSidPrefix,
        string szCoordinatorName,
        string szChangeSummary,
        string szUsername)
    {
        if (string.IsNullOrWhiteSpace(szSidPrefix)) return;

        var args = new Dictionary<string, string>
        {
            ["username"] = string.IsNullOrWhiteSpace(szUsername) ? "anonymous" : szUsername,
            ["name"]     = szCoordinatorName,
            ["value"]    = szChangeSummary,
        };

        await WriteEventAsync("control.action.coordinator_config_changed", args, szSidPrefix, ControlActionType.CoordinatorConfigChanged);
    }

    /// <summary>組出 fallback Message 並寫入 EventLog（共用收尾）</summary>
    private async Task WriteEventAsync(
        string szKey,
        Dictionary<string, string> aArgs,
        string szCid,
        ControlActionType actionType)
    {
        var szArgsJson = JsonSerializer.Serialize(aArgs);
        var szMessage  = FormatFallback(szKey, aArgs);

        var model = new EventLogModel
        {
            szSID         = szCid,
            nEventType    = EVENT_TYPE_INFO,
            nSeverity     = SEVERITY_NONE,
            szMessage     = szMessage,
            szMessageKey  = szKey,
            szMessageArgs = szArgsJson,
            dtOccurredAt  = DateTime.Now,
        };

        try
        {
            await _eventLogService.InsertEventAsync(model);
        }
        catch (Exception ex)
        {
            // 控制 EventLog 寫入失敗不影響控制本身（控制 MQTT 已發送），僅記 log
            _logger.LogWarning(ex, "寫入控制 EventLog 失敗 SID={Cid} ActionType={Type}", szCid, actionType);
        }
    }

    /// <summary>
    /// 依 actionType 產出 (MessageKey, args dict)。
    /// args dict 順序固定，AlarmMessageLocalizer.ParseArgs 依 key 順序映射至 {0}/{1}/{2}...
    /// </summary>
    private static (string? szKey, Dictionary<string, string> aArgs) BuildKeyAndArgs(
        ControlActionType actionType, string szName, double dValue, string szUser)
    {
        var args = new Dictionary<string, string>
        {
            ["username"] = szUser,
            ["name"]     = szName,
        };

        switch (actionType)
        {
            case ControlActionType.Button:
                args["value"] = FormatNumber(dValue);
                return ("control.action.button_pressed", args);

            case ControlActionType.AoManual:
                args["value"] = FormatNumber(dValue);
                return ("control.action.ao_manual_set", args);

            case ControlActionType.AoAuto:
                return ("control.action.ao_switch_auto", args);

            case ControlActionType.DoSet:
                // 數值非 0 視為 ON，0 視為 OFF — ON/OFF 為通用詞不需 i18n
                return (dValue != 0 ? "control.action.do_set_on" : "control.action.do_set_off", args);

            case ControlActionType.DoAuto:
                return ("control.action.do_switch_auto", args);

            case ControlActionType.PumpStartStop:
                return (dValue != 0 ? "control.action.pump_start" : "control.action.pump_stop", args);

            case ControlActionType.PumpFreq:
                args["value"] = FormatNumber(dValue);
                return ("control.action.pump_freq_set", args);

            case ControlActionType.PumpAuto:
                return ("control.action.pump_switch_auto", args);

            default:
                return (null, args);
        }
    }

    /// <summary>
    /// 寫入 DB 的 fallback Message — 依當前 culture 預先格式化（舊資料 / localizer 找不到 key 時備用）
    /// </summary>
    private string FormatFallback(string szKey, Dictionary<string, string> args)
    {
        var ls = _l[szKey];
        if (ls.ResourceNotFound) return $"{args.GetValueOrDefault("username")} {szKey} {args.GetValueOrDefault("name")}";

        var aOrder = ArgOrderForKey(szKey);
        var aValues = aOrder.Select(k => (object)args.GetValueOrDefault(k, string.Empty)).ToArray();
        try { return string.Format(ls.Value, aValues); }
        catch { return ls.Value; }
    }

    /// <summary>
    /// 公開供 AlarmMessageLocalizer 共用 — 確保 args dict ↔ {0}{1}{2} 對應一致
    /// </summary>
    public static string[] ArgOrderForKey(string szKey) => szKey switch
    {
        "control.action.button_pressed"   => new[] { "username", "name", "value" },
        "control.action.ao_manual_set"    => new[] { "username", "name", "value" },
        "control.action.ao_switch_auto"   => new[] { "username", "name" },
        "control.action.do_set_on"        => new[] { "username", "name" },
        "control.action.do_set_off"       => new[] { "username", "name" },
        "control.action.do_switch_auto"   => new[] { "username", "name" },
        "control.action.pump_start"       => new[] { "username", "name" },
        "control.action.pump_stop"        => new[] { "username", "name" },
        "control.action.pump_freq_set"    => new[] { "username", "name", "value" },
        "control.action.pump_switch_auto"     => new[] { "username", "name" },
        "control.action.point_config_changed" => new[] { "username", "name", "value" },
        "control.action.coordinator_config_changed" => new[] { "username", "name", "value" },
        _ => Array.Empty<string>()
    };

    private static string FormatNumber(double d)
    {
        // 整數不顯示小數；浮點最多 3 位
        if (Math.Abs(d - Math.Truncate(d)) < 1e-9) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return Math.Round(d, 3).ToString(CultureInfo.InvariantCulture);
    }
}
