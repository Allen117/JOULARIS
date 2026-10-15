namespace ScadaEngine.Common.Equipment;

/// <summary>
/// 設備效率診斷 — 角色鍵常數（唯一真相）。
/// 存進 EquipmentPointBinding.Role 的字串；前端 role-aliases.js 的 _equipRoleAliases 用同一組字串。
/// 每個角色對應一種物理量（<see cref="EquipmentQuantity"/>），決定可選單位與換算到基準單位的係數。
/// </summary>
public static class EquipmentRoles
{
    /// <summary>瞬時電力（kW）</summary>
    public const string KW = "KW";
    /// <summary>累積電能（kWh，累積式電表；邊界相減 + 溢位）</summary>
    public const string KWH = "KWH";
    /// <summary>直接冷量點（RT；冰機備選產出路徑）</summary>
    public const string RT = "RT";
    /// <summary>冰水流量（基準 L/min；冰機主產出路徑，配 CHWS + CHWR）</summary>
    public const string FLOW = "FLOW";
    /// <summary>冰水出水溫度（°C）</summary>
    public const string CHWS = "CHWS";
    /// <summary>冰水回水溫度（°C）</summary>
    public const string CHWR = "CHWR";
    /// <summary>冷卻水進水溫度（°C，進冷凝器）</summary>
    public const string CDWS = "CDWS";
    /// <summary>冷卻水出水溫度（°C，出冷凝器）</summary>
    public const string CDWR = "CDWR";
    /// <summary>蒸發飽和溫度（°C；選配，算蒸發器趨近溫度）</summary>
    public const string EVAPT = "EVAPT";
    /// <summary>冷凝飽和溫度（°C；選配，算冷凝器趨近溫度）</summary>
    public const string CONDT = "CONDT";
    /// <summary>負載率（%）</summary>
    public const string LOAD = "LOAD";
    /// <summary>運轉狀態（≥ 0.5 視為運轉）</summary>
    public const string RUN = "RUN";
    /// <summary>空氣量（基準 m³/min；空壓機產出）</summary>
    public const string AIRFLOW = "AIRFLOW";
    /// <summary>排氣壓力（基準 bar）</summary>
    public const string DISCHPRESS = "DISCHPRESS";
    /// <summary>排氣溫度（°C）</summary>
    public const string DISCHTEMP = "DISCHTEMP";
    /// <summary>加載狀態（≥ 0.5 視為加載）</summary>
    public const string LOADED = "LOADED";

    /// <summary>角色 → 物理量（決定單位選單與換算）</summary>
    public static EquipmentQuantity QuantityOf(string szRole) => szRole switch
    {
        KW => EquipmentQuantity.Power,
        KWH => EquipmentQuantity.Energy,
        RT => EquipmentQuantity.Cooling,
        FLOW => EquipmentQuantity.WaterFlow,
        CHWS or CHWR or CDWS or CDWR or EVAPT or CONDT or DISCHTEMP => EquipmentQuantity.Temperature,
        LOAD => EquipmentQuantity.Percent,
        RUN or LOADED => EquipmentQuantity.Status,
        AIRFLOW => EquipmentQuantity.AirFlow,
        DISCHPRESS => EquipmentQuantity.Pressure,
        _ => EquipmentQuantity.Unknown,
    };
}

/// <summary>角色的物理量類別 — 每類有一個基準單位，綁定時存換算到基準單位的係數</summary>
public enum EquipmentQuantity
{
    Unknown = 0,
    /// <summary>電力，基準 kW</summary>
    Power,
    /// <summary>電能，基準 kWh</summary>
    Energy,
    /// <summary>冷量，基準 RT</summary>
    Cooling,
    /// <summary>冰水流量，基準 L/min</summary>
    WaterFlow,
    /// <summary>空氣量，基準 m³/min</summary>
    AirFlow,
    /// <summary>溫度，基準 °C（僅支援乘法係數，不支援 °F 偏移）</summary>
    Temperature,
    /// <summary>壓力，基準 bar</summary>
    Pressure,
    /// <summary>百分比，基準 %</summary>
    Percent,
    /// <summary>狀態（0 / 1），無單位</summary>
    Status,
}
