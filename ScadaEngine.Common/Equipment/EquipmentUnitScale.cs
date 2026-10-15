namespace ScadaEngine.Common.Equipment;

/// <summary>
/// 設備效率診斷 — 單位 → 基準單位換算係數表（純函式）。
/// 綁定時存 Scale = 原始值乘上後即為基準單位（EquipmentPointBinding.Scale），
/// Engine 聚合與 Web 即時計算都只做「值 × Scale」，不再各自判斷單位。
///
/// 基準單位：電力 kW、電能 kWh、冷量 RT、冰水流量 L/min、空氣量 m³/min、溫度 °C、壓力 bar、百分比 %。
/// 溫度只支援乘法係數（°C），°F 需偏移量故不收（現場點位請先以計算點位轉 °C）。
/// </summary>
public static class EquipmentUnitScale
{
    /// <summary>1 RT（美制冷凍噸）= 3.517 kW（冷量）</summary>
    public const double KwPerRt = 3.517;

    /// <summary>單位選項（canonical 顯示字串 + 換算係數）</summary>
    public sealed record UnitOption(string szUnit, double dScale);

    private static readonly Dictionary<EquipmentQuantity, UnitOption[]> Units = new()
    {
        [EquipmentQuantity.Power] = [new("kW", 1), new("W", 0.001), new("MW", 1000)],
        [EquipmentQuantity.Energy] = [new("kWh", 1), new("Wh", 0.001), new("MWh", 1000)],
        [EquipmentQuantity.Cooling] = [new("RT", 1), new("USRT", 1), new("kW", 1 / KwPerRt)],
        [EquipmentQuantity.WaterFlow] =
        [
            new("L/min", 1),
            new("L/s", 60),
            new("m³/h", 1000.0 / 60.0),
            new("GPM", 3.785411784),
        ],
        [EquipmentQuantity.AirFlow] =
        [
            new("m³/min", 1),
            new("m³/h", 1.0 / 60.0),
            new("Nm³/h", 1.0 / 60.0),
            new("cfm", 0.028316846592),
            new("L/min", 0.001),
        ],
        [EquipmentQuantity.Temperature] = [new("°C", 1)],
        [EquipmentQuantity.Pressure] = [new("bar", 1), new("kPa", 0.01), new("psi", 0.0689475729), new("MPa", 10)],
        [EquipmentQuantity.Percent] = [new("%", 1)],
        [EquipmentQuantity.Status] = [new("", 1)],
    };

    /// <summary>
    /// 現場點位單位字串的別名 → canonical 單位（比對前轉小寫、去空白、全形轉半形、³→3）。
    /// 掃描帶入時用來預選單位；認不得 → 回基準單位（係數 1），由使用者在設定頁改。
    /// </summary>
    private static readonly Dictionary<EquipmentQuantity, Dictionary<string, string>> Aliases = new()
    {
        [EquipmentQuantity.Power] = Map(("kw", "kW"), ("w", "W"), ("mw", "MW")),
        [EquipmentQuantity.Energy] = Map(("kwh", "kWh"), ("度", "kWh"), ("wh", "Wh"), ("mwh", "MWh")),
        [EquipmentQuantity.Cooling] = Map(("rt", "RT"), ("usrt", "USRT"), ("ton", "RT"), ("tons", "RT"),
            ("冷凍噸", "RT"), ("kw", "kW"), ("kwr", "kW")),
        [EquipmentQuantity.WaterFlow] = Map(("l/min", "L/min"), ("lpm", "L/min"), ("l/m", "L/min"),
            ("l/s", "L/s"), ("lps", "L/s"),
            ("m3/h", "m³/h"), ("m3/hr", "m³/h"), ("cmh", "m³/h"), ("t/h", "m³/h"),
            ("gpm", "GPM")),
        [EquipmentQuantity.AirFlow] = Map(("m3/min", "m³/min"), ("cmm", "m³/min"), ("nm3/min", "m³/min"),
            ("m3/h", "m³/h"), ("m3/hr", "m³/h"), ("cmh", "m³/h"),
            ("nm3/h", "Nm³/h"), ("nm3/hr", "Nm³/h"),
            ("cfm", "cfm"), ("scfm", "cfm"),
            ("l/min", "L/min"), ("lpm", "L/min")),
        [EquipmentQuantity.Temperature] = Map(("°c", "°C"), ("℃", "°C"), ("c", "°C"), ("degc", "°C")),
        [EquipmentQuantity.Pressure] = Map(("bar", "bar"), ("barg", "bar"), ("kpa", "kPa"),
            ("psi", "psi"), ("psig", "psi"), ("mpa", "MPa"), ("kg/cm2", "bar"), ("kgf/cm2", "bar")),
        [EquipmentQuantity.Percent] = Map(("%", "%"), ("pct", "%")),
    };

    private static Dictionary<string, string> Map(params (string alias, string unit)[] pairs) =>
        pairs.ToDictionary(p => p.alias, p => p.unit, StringComparer.Ordinal);

    /// <summary>某物理量可選的單位（第一個 = 基準單位）</summary>
    public static IReadOnlyList<UnitOption> UnitsOf(EquipmentQuantity quantity) =>
        Units.TryGetValue(quantity, out var arr) ? arr : [new UnitOption("", 1)];

    /// <summary>某物理量的基準單位</summary>
    public static string BaseUnitOf(EquipmentQuantity quantity) => UnitsOf(quantity)[0].szUnit;

    /// <summary>
    /// 單位 → 換算到基準單位的係數；不認得的單位回 null（呼叫端應拒絕存檔或退回 1）。
    /// 比對大小寫敏感於 canonical 字串，但也接受別名（lpm / m3/h …）。
    /// </summary>
    public static double? ScaleOf(EquipmentQuantity quantity, string? szUnit)
    {
        var szCanonical = Canonicalize(quantity, szUnit);
        if (szCanonical == null) return null;
        foreach (var u in UnitsOf(quantity))
            if (u.szUnit == szCanonical) return u.dScale;
        return null;
    }

    /// <summary>
    /// 現場單位字串 → canonical 單位；空字串 / null → 基準單位；認不得 → null。
    /// kg/cm² 近似 bar（0.98），工業現場慣例視同 bar。
    /// </summary>
    public static string? Canonicalize(EquipmentQuantity quantity, string? szRaw)
    {
        if (string.IsNullOrWhiteSpace(szRaw)) return BaseUnitOf(quantity);
        // 先比 canonical 本身
        foreach (var u in UnitsOf(quantity))
            if (u.szUnit == szRaw.Trim()) return u.szUnit;

        var szKey = NormalizeKey(szRaw);
        if (Aliases.TryGetValue(quantity, out var map) && map.TryGetValue(szKey, out var szUnit))
            return szUnit;
        return null;
    }

    /// <summary>單位別名比對鍵：全形轉半形、³/² → 3/2、去空白、小寫</summary>
    public static string NormalizeKey(string szRaw)
    {
        var chars = szRaw.Trim().Select(ch =>
            ch >= '\uFF01' && ch <= '\uFF5E' ? (char)(ch - 0xFEE0) : ch).ToArray();
        var s = new string(chars).Replace("³", "3").Replace("²", "2").Replace(" ", string.Empty);
        return s.ToLowerInvariant();
    }

    /// <summary>給前端用的別名表（quantity → alias → canonical）</summary>
    public static IReadOnlyDictionary<EquipmentQuantity, Dictionary<string, string>> AliasTable => Aliases;
}
