using ClosedXML.Excel;
using Microsoft.Extensions.Localization;
using ScadaEngine.Web.Services.HistoryStatistics;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 歷史趨勢 Excel 匯出 — ClosedXML 寫表，再交 <see cref="HistoryExcelChartWriter"/> 補圖表。
/// 分頁順序：統計 → 圖表 → 各點位資料（每次都有，不詢問）。
///   統計：每點位一列（類型 / 筆數 / 有效筆數 / 極值 / 平均 / 標準差 / 差異值 / 備註）
///   圖表：A～C 累計點位資料表（同單位相鄰、組內非零在前）+ 長條 / 圓餅 / 折線
///   資料：時間 / 數值 / 品質 / 有效值（Good 才填，折線參照此欄 → Bad 處斷線）
/// </summary>
public class HistoryExcelExporter
{
    private const string ValueFormat = "0.000";
    private const string DiffFormat = "#,##0.00";
    private const string TimeFormat = "yyyy-mm-dd hh:mm:ss";

    private readonly IStringLocalizer<HistoryExcelExporter> _l;

    public HistoryExcelExporter(IStringLocalizer<HistoryExcelExporter> localizer)
    {
        _l = localizer;
    }

    /// <summary>下載檔名前綴</summary>
    public string FileNamePrefix => _l["excel.filename"].Value;

    /// <summary>產出 .xlsx 二進位內容（含圖表）</summary>
    public byte[] Export(IReadOnlyList<HistoryPointResult> results, DateTime dtStart, DateTime dtEnd, int nInterval)
    {
        using var workbook = new XLWorkbook();
        var szStatsSheet = _l["excel.sheet.stats"].Value;
        var szChartSheet = _l["excel.sheet.chart"].Value;
        var wsStats = workbook.Worksheets.Add(szStatsSheet);
        var wsChart = workbook.Worksheets.Add(szChartSheet);

        // ── 各點位資料分頁（先建，圖表要參照分頁名稱與列號）──
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { szStatsSheet, szChartSheet };
        var dataSheets = new Dictionary<HistoryPointResult, string>();
        foreach (var r in results.Where(r => r.data.Count > 0))
        {
            var szName = UniqueSheetName(r.szName.Length > 0 ? r.szName : r.szSID, usedNames);
            WriteDataSheet(workbook.Worksheets.Add(szName), r);
            dataSheets[r] = szName;
        }

        WriteStatsSheet(wsStats, results, dtStart, dtEnd, nInterval);

        var specs = new List<HistoryChartSpec>();
        specs.AddRange(WriteChartTable(wsChart, results, szChartSheet));
        specs.AddRange(BuildLineCharts(results, dataSheets));
        if (specs.Count == 0)
            wsChart.Cell(1, 1).Value = _l["excel.chart.none"].Value;

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return specs.Count == 0
            ? ms.ToArray()
            : HistoryExcelChartWriter.AddCharts(ms.ToArray(), szChartSheet, specs);
    }

    // ── 資料分頁 ─────────────────────────────────────────────────────────

    private void WriteDataSheet(IXLWorksheet ws, HistoryPointResult r)
    {
        var szUnitSuffix = r.szUnit.Length > 0 ? $" ({r.szUnit})" : string.Empty;
        ws.Cell(1, 1).Value = _l["excel.col.timestamp"].Value;
        ws.Cell(1, 2).Value = _l["excel.col.value"].Value + szUnitSuffix;
        ws.Cell(1, 3).Value = _l["excel.col.quality"].Value;
        ws.Cell(1, 4).Value = _l["excel.col.valid"].Value + szUnitSuffix;
        ws.Range(1, 1, 1, 4).Style.Font.Bold = true;

        var nRow = 2;
        foreach (var d in r.data)
        {
            var dValue = HistoryStatisticsCalculator.ToDouble(d.fValue);
            ws.Cell(nRow, 1).Value = d.dtTimestamp;
            if (double.IsFinite(dValue)) ws.Cell(nRow, 2).Value = dValue;
            ws.Cell(nRow, 3).Value = d.nQuality == 1 ? "Good" : "Bad";
            if (HistoryStatisticsCalculator.IsValid(d)) ws.Cell(nRow, 4).Value = dValue;
            nRow++;
        }

        ws.Column(1).Style.NumberFormat.Format = TimeFormat;
        ws.Column(1).Width = 21;
        ws.Column(2).Width = 16;
        ws.Column(3).Width = 8;
        ws.Column(4).Width = 16;
        ws.SheetView.FreezeRows(1);
    }

    // ── 統計分頁 ─────────────────────────────────────────────────────────

    private void WriteStatsSheet(IXLWorksheet ws, IReadOnlyList<HistoryPointResult> results,
        DateTime dtStart, DateTime dtEnd, int nInterval)
    {
        ws.Cell(1, 1).Value = _l["excel.stats.range", dtStart.ToString("yyyy-MM-dd HH:mm"), dtEnd.ToString("yyyy-MM-dd HH:mm")].Value;
        ws.Cell(2, 1).Value = nInterval > 0
            ? _l["excel.stats.interval", nInterval].Value
            : _l["excel.stats.interval_raw"].Value;

        string[] heads =
        [
            _l["excel.stats.col.point"].Value, _l["excel.stats.col.unit"].Value, _l["excel.stats.col.kind"].Value,
            _l["excel.stats.col.count"].Value, _l["excel.stats.col.valid_count"].Value,
            _l["excel.stats.col.min"].Value, _l["excel.stats.col.max"].Value, _l["excel.stats.col.avg"].Value,
            _l["excel.stats.col.std_dev"].Value, _l["excel.stats.col.diff"].Value, _l["excel.stats.col.remark"].Value,
        ];
        const int nHeadRow = 4;
        for (var i = 0; i < heads.Length; i++) ws.Cell(nHeadRow, i + 1).Value = heads[i];
        var headRange = ws.Range(nHeadRow, 1, nHeadRow, heads.Length);
        headRange.Style.Font.Bold = true;
        headRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        var nRow = nHeadRow + 1;
        foreach (var r in results)
        {
            var st = r.stats;
            ws.Cell(nRow, 1).Value = r.szName;
            ws.Cell(nRow, 2).Value = r.szUnit;
            ws.Cell(nRow, 3).Value = _l[$"excel.kind.{st.kind.ToString().ToLowerInvariant()}"].Value;
            ws.Cell(nRow, 4).Value = st.nCount;
            ws.Cell(nRow, 5).Value = st.nValidCount;
            SetNumber(ws.Cell(nRow, 6), st.dMin, ValueFormat);
            SetNumber(ws.Cell(nRow, 7), st.dMax, ValueFormat);
            SetNumber(ws.Cell(nRow, 8), st.dAvg, ValueFormat);
            SetNumber(ws.Cell(nRow, 9), st.dStdDev, ValueFormat);
            SetNumber(ws.Cell(nRow, 10), st.dDiff, ValueFormat);
            ws.Cell(nRow, 11).Value = BuildRemark(r);
            nRow++;
        }

        ws.Column(1).Width = 32;
        ws.Column(2).Width = 9;
        ws.Column(3).Width = 10;
        ws.Columns(4, 5).Width = 9;
        ws.Columns(6, 10).Width = 14;
        ws.Column(11).Width = 60;
        ws.SheetView.FreezeRows(nHeadRow);
    }

    private string BuildRemark(HistoryPointResult r)
    {
        var st = r.stats;
        var parts = new List<string>();
        if (st.isNoValidData)
            parts.Add(_l["excel.remark.no_valid"].Value);
        if (r.isLimited)
            parts.Add(_l["excel.remark.limited", st.nCount].Value);
        if (st.kind == PointKind.Status && !st.isNoValidData)
            parts.Add(_l["excel.remark.status_avg"].Value);
        if (st.kind == PointKind.Cumulative && !st.isNoValidData)
        {
            if (st.nDropCount > 0) parts.Add(_l["excel.remark.drop", st.nDropCount].Value);
            if (st.nSpikeCount > 0) parts.Add(_l["excel.remark.spike", st.nSpikeCount].Value);
            if (st.isStartInset) parts.Add(_l["excel.remark.start_inset", st.dtFirstValid!.Value.ToString("yyyy-MM-dd HH:mm")].Value);
            if (st.isEndInset) parts.Add(_l["excel.remark.end_inset", st.dtLastValid!.Value.ToString("yyyy-MM-dd HH:mm")].Value);
        }
        return string.Join("；", parts);
    }

    // ── 圖表分頁：累計值資料表 + 長條 / 圓餅 ─────────────────────────────

    /// <summary>
    /// A～C 寫累計點位（有有效值者）資料表：同單位組相鄰、組內差異值非零在前（圓餅只參照非零連續段）。
    /// 回傳長條（每組一張）與圓餅（非零 ≥2 才畫）圖表定義。
    /// </summary>
    private List<HistoryChartSpec> WriteChartTable(IXLWorksheet ws, IReadOnlyList<HistoryPointResult> results, string szSheet)
    {
        var specs = new List<HistoryChartSpec>();
        var groups = results
            .Where(r => r.stats.kind == PointKind.Cumulative && r.stats.dDiff.HasValue)
            .GroupBy(r => r.stats.szUnitGroup)
            .ToList();
        if (groups.Count == 0) return specs;

        ws.Cell(1, 1).Value = _l["excel.chart.col.point"].Value;
        ws.Cell(1, 2).Value = _l["excel.chart.col.diff"].Value;
        ws.Cell(1, 3).Value = _l["excel.chart.col.unit"].Value;
        ws.Range(1, 1, 1, 3).Style.Font.Bold = true;
        ws.Range(1, 1, 1, 3).Style.Fill.BackgroundColor = XLColor.LightGray;

        var bars = new List<HistoryChartSpec>();
        var pies = new List<HistoryChartSpec>();
        var nRow = 2;
        foreach (var g in groups)
        {
            var ordered = g.Where(r => r.stats.dDiff!.Value != 0)
                .Concat(g.Where(r => r.stats.dDiff!.Value == 0))
                .ToList();
            var nFirst = nRow;
            foreach (var r in ordered)
            {
                ws.Cell(nRow, 1).Value = r.szName;
                ws.Cell(nRow, 2).Value = r.stats.dDiff!.Value;
                ws.Cell(nRow, 2).Style.NumberFormat.Format = DiffFormat;
                ws.Cell(nRow, 3).Value = g.Key;
                nRow++;
            }

            bars.Add(new HistoryChartSpec
            {
                type = HistoryChartType.Bar,
                szTitle = _l["excel.chart.bar_title", g.Key].Value,
                series = [CategorySeries(szSheet, nFirst, ordered)]
            });

            var nonZero = ordered.Where(r => r.stats.dDiff!.Value > 0).ToList();
            if (nonZero.Count >= 2)
            {
                pies.Add(new HistoryChartSpec
                {
                    type = HistoryChartType.Pie,
                    szTitle = _l["excel.chart.pie_title", g.Key].Value,
                    series = [CategorySeries(szSheet, nFirst, nonZero)]
                });
            }
        }

        ws.Column(1).Width = 32;
        ws.Column(2).Width = 14;
        ws.Column(3).Width = 8;

        specs.AddRange(bars);
        specs.AddRange(pies);
        return specs;
    }

    private HistoryChartSeries CategorySeries(string szSheet, int nFirstRow, List<HistoryPointResult> rows) => new()
    {
        szName = _l["excel.chart.col.diff"].Value,
        szSheet = szSheet,
        nFirstRow = nFirstRow,
        nLastRow = nFirstRow + rows.Count - 1,
        szXCol = "A",
        szYCol = "B",
        aszCategories = rows.Select(r => r.szName).ToList(),
        adY = rows.Select(r => r.stats.dDiff).ToList()
    };

    // ── 即時值折線（每單位組一張；單位空 → 每點位自成一組）────────────────

    private List<HistoryChartSpec> BuildLineCharts(IReadOnlyList<HistoryPointResult> results,
        Dictionary<HistoryPointResult, string> dataSheets)
    {
        var instant = results
            .Where(r => r.stats.kind == PointKind.Instant && !r.stats.isNoValidData && dataSheets.ContainsKey(r))
            .ToList();

        var specs = new List<HistoryChartSpec>();
        foreach (var g in instant.GroupBy(r => r.stats.szUnitGroup.Length > 0 ? "U:" + r.stats.szUnitGroup : "P:" + r.szSID))
        {
            var first = g.First();
            var szLabel = first.stats.szUnitGroup.Length > 0 ? first.stats.szUnitGroup : first.szName;
            specs.Add(new HistoryChartSpec
            {
                type = HistoryChartType.Line,
                szTitle = _l["excel.chart.line_title", szLabel].Value,
                series = g.Select(r => new HistoryChartSeries
                {
                    szName = r.szName,
                    szSheet = dataSheets[r],
                    nFirstRow = 2,
                    nLastRow = r.data.Count + 1,
                    szXCol = "A",
                    szYCol = "D",
                    adX = r.data.Select(d => d.dtTimestamp.ToOADate()).ToList(),
                    adY = r.data.Select(d => HistoryStatisticsCalculator.IsValid(d)
                        ? HistoryStatisticsCalculator.ToDouble(d.fValue) : (double?)null).ToList()
                }).ToList()
            });
        }
        return specs;
    }

    // ── 工具 ─────────────────────────────────────────────────────────────

    private static void SetNumber(IXLCell cell, double? dValue, string szFormat)
    {
        if (!dValue.HasValue) return;
        cell.Value = dValue.Value;
        cell.Style.NumberFormat.Format = szFormat;
    }

    /// <summary>Excel 分頁名：去除 []:*?/\、最長 31 字、不分大小寫唯一</summary>
    private static string UniqueSheetName(string szRaw, HashSet<string> used)
    {
        var szBase = new string(szRaw.Select(c => "[]:*?/\\".Contains(c) ? '_' : c).ToArray()).Trim().Trim('\'');
        if (szBase.Length == 0) szBase = "Sheet";
        if (szBase.Length > 31) szBase = szBase[..31];

        var szName = szBase;
        var n = 1;
        while (used.Contains(szName))
        {
            var szSuffix = $"({n++})";
            szName = (szBase.Length + szSuffix.Length > 31 ? szBase[..(31 - szSuffix.Length)] : szBase) + szSuffix;
        }
        used.Add(szName);
        return szName;
    }
}
