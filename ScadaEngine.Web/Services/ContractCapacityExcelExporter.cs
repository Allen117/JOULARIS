using ClosedXML.Excel;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Features.ContractCapacity.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 契約容量最佳化 Excel 匯出 — ClosedXML。
/// 標題 + 回測條件 + 三方案摘要（現行 / 保守 / 最低成本）+ 逐月明細 + 回推聲明。
/// 方案名稱沿用電費設定頁的 tariff.plan.{id} 字串（同一份 resx，不重複維護）。
/// </summary>
public class ContractCapacityExcelExporter
{
    private const string NumberFormat = "#,##0";
    private const string KwFormat = "#,##0.0";

    private readonly IStringLocalizer<ContractCapacityExcelExporter> _l;
    private readonly IStringLocalizer _tariff;

    public ContractCapacityExcelExporter(
        IStringLocalizer<ContractCapacityExcelExporter> localizer,
        IStringLocalizerFactory factory)
    {
        _l = localizer;
        _tariff = factory.Create("Features.TariffSetting.Views.Index",
            typeof(ContractCapacityExcelExporter).Assembly.GetName().Name!);
    }

    /// <summary>產出 .xlsx 二進位內容</summary>
    public byte[] Export(ContractCapacityResult result, string szOperator)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add(_l["excel.sheet_name"].Value);
        const int nLastCol = 15;

        ws.Cell(1, 1).Value = _l["excel.title"].Value;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Range(1, 1, 1, nLastCol).Merge();

        // ── 回測條件 ──
        var conditions = new (string szLabel, string szValue)[]
        {
            (_l["excel.label.circuit"].Value, result.szCircuitName),
            (_l["excel.label.plan"].Value, PlanName(result)),
            (_l["excel.label.range"].Value, $"{result.szFrom} ~ {result.szTo}"),
            (_l["excel.label.evaluable"].Value, $"{result.nEvaluableMonths} / {result.nMonths}"),
            (_l["excel.label.threshold"].Value, $"{result.dCompletenessPct:0.#}%"),
            (_l["excel.label.max_valid"].Value, result.dMaxValidKw?.ToString("#,##0.#") ?? "—"),
            (_l["excel.label.round"].Value, $"{result.nRoundKw} kW"),
            (_l["excel.label.query_time"].Value, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            (_l["excel.label.operator"].Value, szOperator),
        };
        var nRow = 3;
        foreach (var (szLabel, szValue) in conditions)
        {
            ws.Cell(nRow, 1).Value = szLabel;
            ws.Cell(nRow, 1).Style.Font.Bold = true;
            ws.Cell(nRow, 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Range(nRow, 1, nRow, 2).Merge();
            ws.Cell(nRow, 3).Value = szValue;
            ws.Range(nRow, 3, nRow, 6).Merge();
            nRow++;
        }

        // ── 三方案摘要 ──
        nRow++;
        string[] summaryHeads =
        [
            _l["excel.col.scenario"].Value, _l["excel.col.contract_kw"].Value, _l["excel.col.total"].Value,
            _l["excel.col.regular_base"].Value, _l["excel.col.other_base"].Value, _l["excel.col.penalty"].Value,
            _l["excel.col.over_months"].Value, _l["excel.col.saving"].Value, _l["excel.col.saving_pct"].Value,
        ];
        WriteHeader(ws, nRow, summaryHeads);
        nRow++;
        WriteScenario(ws, nRow++, _l["excel.scenario.current"].Value, result.current, isCurrent: true);
        WriteScenario(ws, nRow++, _l["excel.scenario.safe"].Value, result.safe, isCurrent: false);
        WriteScenario(ws, nRow++, _l["excel.scenario.optimum"].Value, result.optimum, isCurrent: false);
        if (result.nFlatLowKw != null && result.nFlatHighKw != null)
        {
            ws.Cell(nRow, 1).Value = string.Format(_l["excel.label.flat_range"].Value,
                result.nFlatLowKw.Value.ToString("#,##0"), result.nFlatHighKw.Value.ToString("#,##0"));
            ws.Range(nRow, 1, nRow, 9).Merge();
            nRow++;
        }

        // ── 逐月明細 ──
        nRow++;
        string[] monthHeads =
        [
            _l["excel.col.month"].Value, _l["excel.col.start"].Value, _l["excel.col.end"].Value,
            _l["excel.col.price"].Value, _l["excel.col.max_demand"].Value, _l["excel.col.peak_at"].Value,
            _l["excel.col.completeness"].Value, _l["excel.col.status"].Value, _l["excel.col.current_kw"].Value,
            _l["excel.col.cur_over"].Value, _l["excel.col.cur_cost"].Value,
            _l["excel.col.safe_over"].Value, _l["excel.col.safe_cost"].Value,
            _l["excel.col.opt_over"].Value, _l["excel.col.opt_cost"].Value,
        ];
        WriteHeader(ws, nRow, monthHeads);
        var nHeaderRow = nRow;
        nRow++;
        foreach (var m in result.months)
        {
            ws.Cell(nRow, 1).Value = m.szLabel;
            ws.Cell(nRow, 2).Value = m.szStart;
            ws.Cell(nRow, 3).Value = m.szEnd;
            SetNumber(ws.Cell(nRow, 4), m.dRegularPrice, "#,##0.0");
            SetNumber(ws.Cell(nRow, 5), m.dMaxDemandKw, KwFormat);
            ws.Cell(nRow, 6).Value = m.szPeakAt ?? "—";
            SetNumber(ws.Cell(nRow, 7), m.dCompletenessPct / 100, "0.0%");
            ws.Cell(nRow, 8).Value = m.isEvaluable
                ? _l["excel.status.ok"].Value
                : _l[$"excel.status.{m.szReason}"].Value;
            SetNumber(ws.Cell(nRow, 9), m.dCurrentKw, NumberFormat);
            WriteMonthCost(ws, nRow, 10, m.isEvaluable ? m.cur : null);
            WriteMonthCost(ws, nRow, 12, m.isEvaluable ? m.safe : null);
            WriteMonthCost(ws, nRow, 14, m.isEvaluable ? m.optimum : null);
            if (!m.isEvaluable) ws.Range(nRow, 1, nRow, nLastCol).Style.Font.FontColor = XLColor.Gray;
            nRow++;
        }
        ws.Range(nHeaderRow, 1, nRow - 1, nLastCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(nHeaderRow, 1, nRow - 1, nLastCol).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        // ── 聲明 ──
        nRow++;
        foreach (var szKey in new[] { "excel.note.backtest", "excel.note.scope", "excel.note.season" })
        {
            ws.Cell(nRow, 1).Value = _l[szKey].Value;
            ws.Cell(nRow, 1).Style.Font.Italic = true;
            ws.Range(nRow, 1, nRow, nLastCol).Merge();
            nRow++;
        }

        ws.Columns(1, nLastCol).AdjustToContents();
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 16);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private string PlanName(ContractCapacityResult result)
    {
        if (string.IsNullOrEmpty(result.szPlanId)) return "—";
        var szName = !string.IsNullOrWhiteSpace(result.szPlanName)
            ? result.szPlanName!
            : _tariff[$"tariff.plan.{result.szPlanId}"].Value;
        return result.isMultiplePlans ? $"{szName} {_l["excel.label.multiple_plans"].Value}" : szName;
    }

    private static void WriteHeader(IXLWorksheet ws, int nRow, string[] heads)
    {
        for (var i = 0; i < heads.Length; i++)
        {
            var cell = ws.Cell(nRow, i + 1);
            cell.Value = heads[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }
    }

    private void WriteScenario(IXLWorksheet ws, int nRow, string szName, ContractScenarioDto? s, bool isCurrent)
    {
        ws.Cell(nRow, 1).Value = szName;
        ws.Cell(nRow, 1).Style.Font.Bold = true;
        if (s == null)
        {
            ws.Cell(nRow, 2).Value = isCurrent ? _l["excel.label.no_contract"].Value : "—";
            ws.Range(nRow, 2, nRow, 9).Merge();
            return;
        }
        SetNumber(ws.Cell(nRow, 2), s.dKw, NumberFormat);
        SetNumber(ws.Cell(nRow, 3), s.dTotal, NumberFormat);
        SetNumber(ws.Cell(nRow, 4), s.dRegularBase, NumberFormat);
        SetNumber(ws.Cell(nRow, 5), s.dOtherBase, NumberFormat);
        SetNumber(ws.Cell(nRow, 6), s.dPenalty, NumberFormat);
        ws.Cell(nRow, 7).Value = s.nOverMonths;
        SetNumber(ws.Cell(nRow, 8), s.dSaving, NumberFormat);
        SetNumber(ws.Cell(nRow, 9), s.dSavingPct / 100, "0.0%");
    }

    private static void WriteMonthCost(IXLWorksheet ws, int nRow, int nCol, ContractMonthCostDto? c)
    {
        if (c == null)
        {
            ws.Cell(nRow, nCol).Value = "—";
            ws.Cell(nRow, nCol + 1).Value = "—";
            return;
        }
        SetNumber(ws.Cell(nRow, nCol), c.dOverKw, KwFormat);
        SetNumber(ws.Cell(nRow, nCol + 1), c.dTotal, NumberFormat);
        if (c.dOverKw > 0)
            ws.Range(nRow, nCol, nRow, nCol + 1).Style.Font.FontColor = XLColor.Red;
    }

    private static void SetNumber(IXLCell cell, double? dValue, string szFormat)
    {
        if (dValue == null)
        {
            cell.Value = "—";
            return;
        }
        cell.Value = dValue.Value;
        cell.Style.NumberFormat.Format = szFormat;
    }
}
