using System.Text.RegularExpressions;

namespace ScadaEngine.Web.Services.HistoryStatistics;

/// <summary>
/// 點位類型判定 — 歷史趨勢統計與匯出圖表的單一真相（前端不再自行判斷）。
/// 依序判斷，命中即停：
///   1. 名稱含期間字樣（今日 / 本月 / today…）→ 期間值
///   2. 單位「完全等於」累計單位別名 → 累計值，歸到 kWh / MWh / Wh / RTh / m³ / Nm³ 組
///   3. 單位為空，名稱含獨立的 kWh / MWh / RTh / Nm3 / m3（前後不接英數字、名稱不含「/」）→ 累計值
///   4. 有效值全為 0 或 1 → 狀態值
///   5. 其餘 → 即時值，依 trim 後單位分組
/// 單位採完全比對，不會被 m3/h、kWh/m3 這類複合單位誤判。
/// </summary>
public static class PointKindClassifier
{
    private static readonly string[] _aPeriodKeywords =
    [
        "今日", "本日", "本月", "昨日", "上月",
        "today", "yesterday", "this month", "last month"
    ];

    /// <summary>累計單位別名（trim + 小寫後完全比對）→ 單位組</summary>
    private static readonly Dictionary<string, string> _cumulativeUnitAliases = BuildAliases();

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string szGroup, params string[] aAlias)
        {
            foreach (var a in aAlias) map[a] = szGroup;
        }

        // 沿用原 history.js KWH_UNIT_ALIASES（與 designer/row-template.js 對齊），Wh / MWh 量級不同另成一組
        Add("kWh",
            "kwh", "kw_h", "kw-h", "kw·h", "kilowatthour", "kilowatthours",
            "energy", "consumption", "accumulated",
            "kwh_total", "kwhtotal", "total_kwh",
            "度", "度數", "用電量", "電能", "累計用電", "累積電能", "總電能", "用電度數", "總耗電");
        Add("Wh", "wh");
        Add("MWh", "mwh");
        Add("RTh", "rth", "rt-h", "rt·h", "rt_h", "rthr", "冷凍噸時", "冷凍噸小時");
        Add("m³", "m3", "m³", "m^3", "cbm", "立方公尺", "立方米");
        Add("Nm³", "nm3", "nm³", "nm^3", "標準立方公尺");
        return map;
    }

    // 單位為空時的名稱後備：獨立出現的累計單位字樣（長者優先，避免 Nm3 被拆成 m3）
    private static readonly Regex _rxNameUnit = new(
        @"(?<![A-Za-z0-9])(nm3|nm³|kwh|mwh|rth|m3|m³)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>判定點位類型與單位分組</summary>
    /// <param name="szName">點位名稱</param>
    /// <param name="szUnit">點位單位（可空）</param>
    /// <param name="validValues">有效值（Good 且有限數），用於判定狀態值</param>
    public static PointClassification Classify(string? szName, string? szUnit, IReadOnlyCollection<double> validValues)
    {
        var szNameLower = (szName ?? string.Empty).ToLowerInvariant();
        var szUnitTrim = (szUnit ?? string.Empty).Trim();

        if (_aPeriodKeywords.Any(k => szNameLower.Contains(k)))
            return new PointClassification(PointKind.Period, szUnitTrim);

        var szCumGroup = GetCumulativeUnitGroup(szUnitTrim);
        if (szCumGroup != null)
            return new PointClassification(PointKind.Cumulative, szCumGroup);

        if (szUnitTrim.Length == 0 && !szNameLower.Contains('/'))
        {
            var m = _rxNameUnit.Match(szName ?? string.Empty);
            if (m.Success)
                return new PointClassification(PointKind.Cumulative, NameTokenToGroup(m.Groups[1].Value));
        }

        if (validValues.Count > 0 && validValues.All(v => v == 0 || v == 1))
            return new PointClassification(PointKind.Status, szUnitTrim);

        return new PointClassification(PointKind.Instant, szUnitTrim);
    }

    /// <summary>單位是否為累計單位；是則回傳單位組，否則 null</summary>
    public static string? GetCumulativeUnitGroup(string? szUnit)
    {
        var szKey = (szUnit ?? string.Empty).Trim().ToLowerInvariant();
        if (szKey.Length == 0) return null;
        return _cumulativeUnitAliases.TryGetValue(szKey, out var szGroup) ? szGroup : null;
    }

    private static string NameTokenToGroup(string szToken) => szToken.ToLowerInvariant() switch
    {
        "kwh" => "kWh",
        "mwh" => "MWh",
        "rth" => "RTh",
        "nm3" or "nm³" => "Nm³",
        _ => "m³"
    };
}
