namespace ScadaEngine.Web.Features.ScadaPage.Models;

/// <summary>
/// ScadaPage 控制動作類型 — 對應 EventLog 訊息 i18n key 與 args 結構
/// </summary>
public enum ControlActionType
{
    Unknown        = 0,
    Button         = 1,
    AoManual       = 2,
    AoAuto         = 3,
    DoSet          = 4,
    DoAuto         = 5,
    PumpStartStop  = 6,
    PumpFreq       = 7,
    PumpAuto       = 8,

    /// <summary>Modbus 點位組態變更（ModbusCoordinator 頁點位熱編輯稽核，非 ScadaPage 控制）</summary>
    PointConfigChanged = 9,

    /// <summary>Modbus 設備層組態變更（改名 / 子設備名稱，ModbusCoordinator 頁稽核）</summary>
    CoordinatorConfigChanged = 10,
}

public static class ControlActionTypeExtensions
{
    public static ControlActionType ParseActionType(string? sz)
    {
        return sz switch
        {
            "button"          => ControlActionType.Button,
            "ao_manual"       => ControlActionType.AoManual,
            "ao_auto"         => ControlActionType.AoAuto,
            "do_set"          => ControlActionType.DoSet,
            "do_auto"         => ControlActionType.DoAuto,
            "pump_start_stop" => ControlActionType.PumpStartStop,
            "pump_freq"       => ControlActionType.PumpFreq,
            "pump_auto"       => ControlActionType.PumpAuto,
            _                 => ControlActionType.Unknown,
        };
    }
}
