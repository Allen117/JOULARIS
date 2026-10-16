using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using ScadaEngine.Common.Equipment.CompressorDiagnosis;
using ScadaEngine.Web.Features.DailyReport.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 空壓系統診斷 → 伺服端文字（能源日報用）。字串與前端共用 class-less <c>Resources/EquipmentEfficiency.resx</c>
/// 的 eq.diag.* key（前端 i18n.t 與本類同一份字典，不重複維護），佔位符為具名 {name}。
/// 依呼叫當下 CurrentUICulture 渲染（日報生成時已切到全局語言）。
/// </summary>
public class CompressorDiagnosisTextRenderer
{
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private readonly IStringLocalizer _l;

    public CompressorDiagnosisTextRenderer(IStringLocalizerFactory factory)
    {
        _l = factory.Create("EquipmentEfficiency", typeof(CompressorDiagnosisTextRenderer).Assembly.GetName().Name!);
    }

    public string T(string szKey, IReadOnlyDictionary<string, object?>? args = null)
    {
        var sz = _l[szKey].Value;
        if (args == null || args.Count == 0) return sz;
        return Placeholder.Replace(sz, m => args.TryGetValue(m.Groups[1].Value, out var v) ? Format(v) : m.Value);
    }

    private static string Format(object? v) => v switch
    {
        null => "--",
        double d => d.ToString("#,0.##", CultureInfo.InvariantCulture),
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number =>
            je.GetDouble().ToString("#,0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string N0(double d) => d.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>快照 → 日報區段（只列觸發的規則）</summary>
    public DailyReportCompressorDiagnosis BuildDailySection(CompressorDiagnosisResult r)
    {
        var section = new DailyReportCompressorDiagnosis
        {
            szPeriod = $"{r.dtFrom:yyyy-MM-dd} ~ {r.dtTo.AddDays(-1):yyyy-MM-dd}",
            nTriggered = r.aRules.Count(x => x.status == DiagnosisStatus.Triggered),
            nPending = r.aRules.Count(x => x.status is DiagnosisStatus.InsufficientData
                or DiagnosisStatus.PendingSiteData or DiagnosisStatus.PendingSetting),
        };
        foreach (var rule in r.aRules.Where(x => x.status == DiagnosisStatus.Triggered))
        {
            var szTitle = T("eq.diag.rule." + rule.szId);
            if (rule.szLevel != null) szTitle += "（" + T("eq.diag.level." + rule.szLevel) + "）";
            var szSavings = "";
            if (rule.savings?.ntdYear is { } ntd)
                szSavings = T("eq.diag.savings.ntd_year", new Dictionary<string, object?> { ["low"] = N0(ntd.dLow), ["high"] = N0(ntd.dHigh) });
            else if (rule.savings != null)
                szSavings = T("eq.diag.savings.kwh_year", new Dictionary<string, object?> { ["low"] = N0(rule.savings.kwhYear.dLow), ["high"] = N0(rule.savings.kwhYear.dHigh) });
            section.items.Add(new DailyReportCompressorDiagnosisItem
            {
                szRuleId = rule.szId,
                szTitle = szTitle,
                szSavings = szSavings,
                szNextStep = rule.szNextStep == null ? "" : T("eq.diag.next." + rule.szNextStep),
                isUnconfirmedDefault = rule.aUnconfirmedDefaults.Count > 0,
            });
        }
        foreach (var f in r.aOverlapFlags) section.aNotices.Add(T("eq.diag.overlap." + f));
        if (r.isBaselinePeriodMissing) section.aNotices.Add(T("eq.diag.baseline_missing"));
        if (section.items.Any(x => x.isUnconfirmedDefault)) section.aNotices.Add(T("eq.diag.default_flag_hint"));
        section.aNotices.Add(T("eq.diag.screening_disclaimer"));
        return section;
    }
}
