namespace ScadaEngine.Common.Equipment.CompressorDiagnosis;

/// <summary>
/// 生產時段（場域事實）：每週排程 + 假日。
/// 一週七天各自 0..N 段 [起分, 訖分)（0..1440，訖 1440 = 24:00）；跨午夜請拆兩段（當日到 24:00 + 隔日 00:00 起）。
/// 假日（/HolidaySetting）在 <see cref="isHolidayNonProduction"/> 為 true 時整天視為非生產。
/// </summary>
public sealed class ProductionSchedule
{
    /// <summary>(星期 0=日..6=六, 起分, 訖分)</summary>
    public List<(int nDay, int nStartMin, int nEndMin)> aRanges { get; set; } = [];
    public bool isHolidayNonProduction { get; set; } = true;
    public HashSet<DateTime> holidays { get; set; } = [];

    /// <summary>至少有一段生產時段才算已設定（全空 = 待補場域資料）</summary>
    public bool IsConfigured => aRanges.Any(r => r.nEndMin > r.nStartMin);

    public bool IsProduction(DateTime dt)
    {
        if (isHolidayNonProduction && holidays.Contains(dt.Date)) return false;
        var nDay = (int)dt.DayOfWeek;
        var nMin = dt.Hour * 60 + dt.Minute;
        foreach (var r in aRanges)
            if (r.nDay == nDay && nMin >= r.nStartMin && nMin < r.nEndMin) return true;
        return false;
    }

    /// <summary>"HH:mm" → 分鐘（"24:00" → 1440）；格式錯 → null</summary>
    public static int? ParseHm(string? sz)
    {
        if (string.IsNullOrWhiteSpace(sz)) return null;
        var parts = sz.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h < 0 || h > 24 || m < 0 || m > 59 || (h == 24 && m != 0)) return null;
        return h * 60 + m;
    }
}

/// <summary>
/// 空壓系統級點位角色（皆可不綁；未綁時規則退回各機資料並標「降級」）。
/// 存在 CompressorDiagnosisSetting 的 JSON（不進 EquipmentPointBinding，那張表是設備層）。
/// </summary>
public static class CompressorSystemRoles
{
    /// <summary>總管 / 供氣壓力（bar）— 未綁退回各機 DISCHPRESS 最高者</summary>
    public const string SupplyPress = "SYS_SUPPLYPRESS";
    /// <summary>末端（關鍵用氣點）壓力（bar）— 未綁則管損未知</summary>
    public const string UsePress = "SYS_USEPRESS";
    /// <summary>系統總流量（m³/min）— 未綁退回各機 AIRFLOW 加總，再無 → duty-cycle 法</summary>
    public const string Flow = "SYS_FLOW";
    /// <summary>壓力露點（°C）— 未綁則降壓建議附註人工確認</summary>
    public const string Dewpoint = "SYS_DEWPOINT";

    public static readonly string[] All = [SupplyPress, UsePress, Flow, Dewpoint];

    public static EquipmentQuantity QuantityOf(string szRole) => szRole switch
    {
        SupplyPress or UsePress => EquipmentQuantity.Pressure,
        Flow => EquipmentQuantity.AirFlow,
        Dewpoint => EquipmentQuantity.Temperature,
        _ => EquipmentQuantity.Unknown,
    };
}
