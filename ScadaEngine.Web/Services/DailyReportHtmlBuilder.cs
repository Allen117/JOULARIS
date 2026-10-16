using System.Globalization;
using System.Text;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Features.DailyReport.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 能源日報 Email HTML 組版 — 吃 DailyReportData 產 email-safe HTML：
/// table 排版 + inline style，長條圖用純 HTML/CSS bar（div 高度百分比），零外部 CSS/JS/圖片依賴。
/// 區塊顯示依 DailyReportSetting.SectionFlags 過濾；警報明細僅列前 20 筆。
/// </summary>
public class DailyReportHtmlBuilder
{
    /// <summary>Email 警報明細上限（其餘顯示「請至系統查看」）</summary>
    public const int ALARM_EMAIL_LIMIT = 20;

    private const string COLOR_PRIMARY = "#43a047";   // EMS 主綠
    private const string COLOR_TEXT = "#333333";
    private const string COLOR_MUTED = "#888888";
    private const string COLOR_BORDER = "#dddddd";
    private const string COLOR_BG_HEAD = "#f1f8f4";
    private const string COLOR_DANGER = "#dc3545";
    private const string COLOR_WARN = "#ff9800";

    private readonly IStringLocalizer<DailyReportHtmlBuilder> _l;

    public DailyReportHtmlBuilder(IStringLocalizer<DailyReportHtmlBuilder> l)
    {
        _l = l;
    }

    /// <summary>Email 主旨（含測試標記）</summary>
    public string BuildSubject(DailyReportData data, bool isTest)
    {
        return WithCulture(data.szLanguage, () =>
            _l[isTest ? "mail.subject.test" : "mail.subject", data.szReportDate].Value);
    }

    /// <summary>完整 HTML 內文（依 setting.SectionFlags 過濾區塊；文字用全局語言渲染）</summary>
    public string Build(DailyReportData data, DailyReportSettingModel setting, bool isTest)
    {
        return WithCulture(data.szLanguage, () => BuildCore(data, setting, isTest));
    }

    private string WithCulture(string szLanguage, Func<string> fn)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(szLanguage);
            return fn();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private string BuildCore(DailyReportData data, DailyReportSettingModel setting, bool isTest)
    {
        var nFlags = setting.nSectionFlags;
        var sb = new StringBuilder(32 * 1024);
        sb.Append($"<div style=\"font-family:'Segoe UI',Arial,sans-serif;color:{COLOR_TEXT};max-width:720px;margin:0 auto;\">");

        // ── 標題列 ──
        sb.Append($"<div style=\"background:{COLOR_PRIMARY};color:#ffffff;padding:14px 20px;border-radius:6px 6px 0 0;\">");
        sb.Append($"<span style=\"font-size:18px;font-weight:bold;\">{H(_l["mail.title"].Value)}</span>");
        sb.Append($"<span style=\"font-size:16px;margin-left:12px;\">{H(data.szReportDate)}</span>");
        if (isTest)
            sb.Append($"<span style=\"background:#ffffff;color:{COLOR_PRIMARY};font-size:12px;padding:2px 8px;border-radius:10px;margin-left:12px;\">{H(_l["mail.test_badge"].Value)}</span>");
        if (setting.isHolidayHintEnabled && data.isReportDateHoliday)
            sb.Append($"<span style=\"background:{COLOR_WARN};color:#ffffff;font-size:12px;padding:2px 8px;border-radius:10px;margin-left:8px;\">{H(_l["mail.holiday_badge"].Value)}</span>");
        sb.Append("</div>");
        sb.Append($"<div style=\"border:1px solid {COLOR_BORDER};border-top:none;padding:16px 20px;border-radius:0 0 6px 6px;\">");

        sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-bottom:12px;\">{H(_l["mail.generated_at"].Value)}: {data.dtGeneratedAt:yyyy-MM-dd HH:mm}</div>");
        if (data.isStaleLastHour)
            sb.Append($"<div style=\"background:#fff3e0;border:1px solid {COLOR_WARN};color:#a05a00;font-size:13px;padding:8px 12px;border-radius:4px;margin-bottom:12px;\">&#9888; {H(_l["mail.stale_warning"].Value)}</div>");

        if (DailyReportSections.Has(nFlags, DailyReportSections.Alarm))
            AppendAlarmSection(sb, data);
        if (DailyReportSections.Has(nFlags, DailyReportSections.Electricity) && data.electricity.isAvailable)
            AppendHourlySection(sb, data.electricity, "electricity");
        if (DailyReportSections.Has(nFlags, DailyReportSections.Water) && data.water.isAvailable)
            AppendHourlySection(sb, data.water, "water");
        if (DailyReportSections.Has(nFlags, DailyReportSections.Gas) && data.gas.isAvailable)
            AppendHourlySection(sb, data.gas, "gas");
        if (DailyReportSections.Has(nFlags, DailyReportSections.Rth) && data.rth.isAvailable)
            AppendHourlySection(sb, data.rth, "rth");
        if (DailyReportSections.Has(nFlags, DailyReportSections.DayCompare) && data.dayComparisons.Count > 0)
            AppendDayCompareSection(sb, data);
        if (DailyReportSections.Has(nFlags, DailyReportSections.MtdCompare) && data.mtdComparisons.Count > 0)
            AppendMtdSection(sb, data);
        if (DailyReportSections.Has(nFlags, DailyReportSections.Insights) && data.insights.Count > 0)
            AppendInsightsSection(sb, data);
        if (DailyReportSections.Has(nFlags, DailyReportSections.CompressorDiagnosis) && data.compressorDiagnosis != null)
            AppendCompressorDiagnosisSection(sb, data.compressorDiagnosis);

        sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:11px;margin-top:16px;border-top:1px solid {COLOR_BORDER};padding-top:8px;\">{H(_l["mail.footer"].Value)}</div>");
        sb.Append("</div></div>");
        return sb.ToString();
    }

    // ────────────────────────── 區塊 ──────────────────────────

    private void AppendSectionTitle(StringBuilder sb, string szTitle)
    {
        sb.Append($"<div style=\"font-size:15px;font-weight:bold;color:{COLOR_PRIMARY};border-left:4px solid {COLOR_PRIMARY};padding-left:8px;margin:18px 0 8px 0;\">{H(szTitle)}</div>");
    }

    private void AppendAlarmSection(StringBuilder sb, DailyReportData data)
    {
        AppendSectionTitle(sb, _l["mail.section.alarm"].Value);
        var a = data.alarm;
        sb.Append($"<div style=\"font-size:13px;margin-bottom:6px;\">{H(_l["mail.alarm.occurred"].Value)}: <b>{a.nOccurredCount}</b>");
        sb.Append($" &nbsp;|&nbsp; {H(_l["mail.alarm.cleared"].Value)}: <b>{a.nClearedCount}</b>");
        sb.Append($" &nbsp;|&nbsp; {H(_l["mail.alarm.unack"].Value)}: <b style=\"color:{(a.nUnacknowledgedCount > 0 ? COLOR_DANGER : COLOR_TEXT)};\">{a.nUnacknowledgedCount}</b></div>");

        if (a.items.Count == 0)
        {
            sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:13px;\">{H(_l["mail.alarm.none"].Value)}</div>");
            return;
        }

        sb.Append($"<table cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;border-collapse:collapse;font-size:12px;\">");
        sb.Append($"<tr style=\"background:{COLOR_BG_HEAD};\">");
        foreach (var szCol in new[] { "mail.alarm.col.time", "mail.alarm.col.type", "mail.alarm.col.severity", "mail.alarm.col.message", "mail.alarm.col.status" })
            sb.Append($"<th style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:left;\">{H(_l[szCol].Value)}</th>");
        sb.Append("</tr>");

        foreach (var item in a.items.Take(ALARM_EMAIL_LIMIT))
        {
            var szType = _l[item.nEventType == 0 ? "mail.alarm.type.alarm" : "mail.alarm.type.fault"].Value;
            var szSeverity = _l[$"mail.severity.{Math.Clamp(item.nSeverity, 0, 3)}"].Value;
            var szStatus = item.dtClearedAt.HasValue
                ? _l["mail.alarm.status.cleared"].Value
                : _l["mail.alarm.status.active"].Value;
            var szStatusColor = item.dtClearedAt.HasValue ? COLOR_PRIMARY : COLOR_DANGER;
            sb.Append("<tr>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;white-space:nowrap;\">{item.dtOccurredAt:HH:mm:ss}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(szType)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(szSeverity)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(item.szMessage)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;color:{szStatusColor};white-space:nowrap;\">{H(szStatus)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        if (a.items.Count > ALARM_EMAIL_LIMIT)
            sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-top:4px;\">{H(_l["mail.alarm.more", a.items.Count - ALARM_EMAIL_LIMIT].Value)}</div>");
    }

    private void AppendHourlySection(StringBuilder sb, DailyReportEnergySection section, string szEnergyKey)
    {
        AppendSectionTitle(sb, _l["mail.section.hourly", _l[$"mail.energy.{szEnergyKey}"].Value, section.szUnit].Value);
        sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-bottom:4px;\">{H(section.szCircuitName)}</div>");

        var dMax = section.dHourly.Count > 0 ? section.dHourly.Max() : 0;
        // 純 HTML/CSS 直條圖：一列 24 格，每格內 div 高度按比例
        sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;border-collapse:collapse;table-layout:fixed;\">");
        sb.Append("<tr>");
        for (var i = 0; i < section.dHourly.Count; i++)
        {
            var nHeight = dMax > 1e-9 ? Math.Max(1, (int)Math.Round(section.dHourly[i] / dMax * 60)) : 1;
            var szBarColor = section.isHourlyStale.Count > i && section.isHourlyStale[i] ? COLOR_BORDER : COLOR_PRIMARY;
            sb.Append($"<td style=\"vertical-align:bottom;height:64px;padding:0 1px;\" title=\"{i:00}:00 = {FormatEnergy(section.dHourly[i], szEnergyKey)}\">");
            sb.Append($"<div style=\"background:{szBarColor};height:{nHeight}px;font-size:0;line-height:0;\">&nbsp;</div></td>");
        }
        sb.Append("</tr><tr>");
        for (var i = 0; i < section.dHourly.Count; i++)
        {
            var szLabel = i % 6 == 0 ? $"{i:00}" : "";
            sb.Append($"<td style=\"font-size:10px;color:{COLOR_MUTED};text-align:left;\">{szLabel}</td>");
        }
        sb.Append("</tr></table>");

        var nPeakHour = 0;
        for (var i = 1; i < section.dHourly.Count; i++)
            if (section.dHourly[i] > section.dHourly[nPeakHour]) nPeakHour = i;
        sb.Append($"<div style=\"font-size:13px;margin-top:4px;\">{H(_l["mail.total"].Value)}: <b>{FormatEnergy(section.dTotal, szEnergyKey)}</b> {H(section.szUnit)}");
        if (dMax > 1e-9)
            sb.Append($" &nbsp;|&nbsp; {H(_l["mail.peak_hour"].Value)}: {nPeakHour:00}:00 ({FormatEnergy(section.dHourly[nPeakHour], szEnergyKey)})");
        sb.Append("</div>");
    }

    private void AppendDayCompareSection(StringBuilder sb, DailyReportData data)
    {
        AppendSectionTitle(sb, _l["mail.section.day_compare"].Value);
        sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;border-collapse:collapse;font-size:12px;\">");
        sb.Append($"<tr style=\"background:{COLOR_BG_HEAD};\">");
        foreach (var szCol in new[] { "mail.col.energy", "mail.col.day", "mail.col.prevday", "mail.col.diff_prev", "mail.col.lastweek", "mail.col.diff_lastweek" })
            sb.Append($"<th style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{H(_l[szCol].Value)}</th>");
        sb.Append("</tr>");
        foreach (var row in data.dayComparisons)
        {
            sb.Append("<tr>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(_l[$"mail.energy.{row.szEnergy}"].Value)} ({H(row.szUnit)})</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\"><b>{FormatEnergy(row.dDay, row.szEnergy)}</b></td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatEnergy(row.dPrevDay, row.szEnergy)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatDiff(row.dDiffPrevPct)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatEnergy(row.dLastWeek, row.szEnergy)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatDiff(row.dDiffLastWeekPct)}</td>");
            sb.Append("</tr>");
        }
        // kWh/RTh 效率比值列（有 RTh 才有）
        if (data.efficiency != null)
        {
            var eff = data.efficiency;
            sb.Append($"<tr style=\"background:#fafafa;\">");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(_l["mail.efficiency.label"].Value)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\"><b>{FormatNullable(eff.dDay)}</b></td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatNullable(eff.dPrevDay)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatDiff(eff.dDiffPrevPct)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatNullable(eff.dLastWeek)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatDiff(eff.dDiffLastWeekPct)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        // 報告日與上週同日的假日/上班日狀態不同 → 標明「vs 上週同星期」的比較基準已改變
        var szShift = DailyReportInsightService.LastWeekBaselineShift(data);
        if (szShift != null)
        {
            sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-top:4px;\">"
                + $"{H(_l[$"mail.lastweek_note.{szShift}"].Value)}</div>");
        }

        if (data.weather?.dAvgTempDay != null)
        {
            sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-top:4px;\">{H(_l["mail.weather.line",
                FormatNullable(data.weather.dAvgTempDay),
                FormatNullable(data.weather.dAvgTempPrevDay),
                FormatNullable(data.weather.dAvgTempLastWeek)].Value)}</div>");
        }
    }

    private void AppendMtdSection(StringBuilder sb, DailyReportData data)
    {
        AppendSectionTitle(sb, _l["mail.section.mtd"].Value);
        var szRangeNote = data.mtdComparisons.Count > 0
            ? $"{data.mtdComparisons[0].szCurrentRange} vs {data.mtdComparisons[0].szLastYearRange}"
            : "";
        if (szRangeNote.Length > 0)
            sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-bottom:4px;\">{H(szRangeNote)}</div>");
        sb.Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;border-collapse:collapse;font-size:12px;\">");
        sb.Append($"<tr style=\"background:{COLOR_BG_HEAD};\">");
        foreach (var szCol in new[] { "mail.col.energy", "mail.col.mtd_current", "mail.col.mtd_lastyear", "mail.col.diff" })
            sb.Append($"<th style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{H(_l[szCol].Value)}</th>");
        sb.Append("</tr>");
        foreach (var row in data.mtdComparisons)
        {
            sb.Append("<tr>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(_l[$"mail.energy.{row.szEnergy}"].Value)} ({H(row.szUnit)})</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\"><b>{FormatEnergy(row.dCurrent, row.szEnergy)}</b></td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatEnergy(row.dLastYear, row.szEnergy)}</td>");
            sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;text-align:right;\">{FormatDiff(row.dDiffPct)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    private void AppendInsightsSection(StringBuilder sb, DailyReportData data)
    {
        AppendSectionTitle(sb, _l["mail.section.insights"].Value);
        sb.Append("<ul style=\"font-size:13px;margin:4px 0;padding-left:20px;\">");
        foreach (var insight in data.insights)
            sb.Append($"<li style=\"margin-bottom:4px;\">{H(insight.szText)}</li>");
        sb.Append("</ul>");
    }

    /// <summary>空壓系統診斷（文字已於快照生成時依全局語言渲染，這裡只排版）</summary>
    private void AppendCompressorDiagnosisSection(StringBuilder sb, DailyReportCompressorDiagnosis diag)
    {
        AppendSectionTitle(sb, _l["mail.section.compressor_diagnosis"].Value);
        sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:12px;margin-bottom:6px;\">{H(_l["mail.compressor_diagnosis.summary", diag.szPeriod, diag.nTriggered, diag.nPending].Value)}</div>");
        if (diag.items.Count > 0)
        {
            sb.Append($"<table style=\"border-collapse:collapse;width:100%;font-size:13px;\">");
            foreach (var item in diag.items)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;font-weight:bold;\">{H(item.szTitle)}{(item.isUnconfirmedDefault ? " *" : "")}</td>");
                sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;white-space:nowrap;\">{H(item.szSavings)}</td>");
                sb.Append($"<td style=\"border:1px solid {COLOR_BORDER};padding:4px 8px;\">{H(item.szNextStep)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }
        foreach (var szNotice in diag.aNotices)
            sb.Append($"<div style=\"color:{COLOR_MUTED};font-size:11px;margin-top:4px;\">{H(szNotice)}</div>");
    }

    // ────────────────────────── helpers ──────────────────────────

    private static string FormatDiff(double? dPct)
    {
        if (!dPct.HasValue) return "&#8212;";  // em dash
        var szColor = dPct.Value > 0 ? COLOR_DANGER : COLOR_PRIMARY;
        var szSign = dPct.Value > 0 ? "+" : "";
        return $"<span style=\"color:{szColor};\">{szSign}{dPct.Value:0.#}%</span>";
    }

    private static string FormatNullable(double? d) => d.HasValue ? d.Value.ToString("#,0.##") : "—";

    /// <summary>
    /// 能源別數值格式 — kWh 與 RT·h 一律一位小數（0 顯示為 0.0，與 /EnergyReport、
    /// /RefrigerationTonReport、/EnergyDeclaration、/DailyReport 頁面一致），
    /// 用水 / 用氣（m³、度）維持「最多兩位」。
    /// 效率比值 kWh/RTh 不走此路徑（沿用 FormatNullable，量級小需保留位數）。
    /// </summary>
    private static string FormatEnergy(double d, string szEnergy) =>
        d.ToString(szEnergy is "rth" or "electricity" ? "#,0.0" : "#,0.##");

    /// <summary>HTML encode（防止警報訊息 / 迴路名含標籤字元）</summary>
    private static string H(string? sz) => System.Net.WebUtility.HtmlEncode(sz ?? "");
}
