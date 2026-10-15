using ClosedXML.Excel;
using Microsoft.Extensions.Localization;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 空壓機能源申報 Excel 匯出 — 使用 ClosedXML（無 Office 依賴），版式與冰水版 <see cref="EnergyDeclarationExcelExporter"/> 同構。
/// 標題區 + 查詢條件（報表 / 用電迴路 / 產氣量來源 / 申報年度 / 查詢時間 / 操作者）+
/// 資料表（月份 / kWh / Nm³ / kWh/Nm³）12 列 + 合計列。
/// 透過 IStringLocalizer 取得當前 culture 的標題、表頭、警告等字串。
/// </summary>
public class AirCompressorDeclarationExcelExporter
{
    private readonly IStringLocalizer<AirCompressorDeclarationExcelExporter> _l;

    public AirCompressorDeclarationExcelExporter(IStringLocalizer<AirCompressorDeclarationExcelExporter> localizer)
    {
        _l = localizer;
    }

    /// <summary>產出 .xlsx 二進位內容</summary>
    public byte[] Export(AirCompressorDeclarationResult result, string szOperator)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add(_l["excel.sheet_name"].Value);
        const int nLastCol = 4;

        // 標題區
        ws.Cell(1, 1).Value = _l["excel.title"].Value;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Range(1, 1, 1, nLastCol).Merge();

        // 查詢條件
        ws.Cell(3, 1).Value = _l["excel.label.report"].Value;
        ws.Cell(3, 2).Value = result.szReportName;
        ws.Cell(4, 1).Value = _l["excel.label.energy_circuit"].Value;
        ws.Cell(4, 2).Value = result.szEnergyCircuitName;
        ws.Cell(5, 1).Value = _l["excel.label.air_circuit"].Value;
        ws.Cell(5, 2).Value = result.szAirMeterCircuitName;
        ws.Cell(6, 1).Value = _l["excel.label.year"].Value;
        ws.Cell(6, 2).Value = result.nYear;
        ws.Cell(7, 1).Value = _l["excel.label.query_time"].Value;
        ws.Cell(7, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ws.Cell(8, 1).Value = _l["excel.label.operator"].Value;
        ws.Cell(8, 2).Value = szOperator;

        for (var r = 3; r <= 8; r++)
        {
            ws.Range(r, 2, r, nLastCol).Merge();
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        // 警告列（兩側各自標示）
        var nDataStartRow = 10;
        var nWarnRow = 9;
        if (result.isHasKwhWarning)
        {
            ws.Cell(nWarnRow, 1).Value = _l["excel.label.kwh_warning"].Value;
            ws.Cell(nWarnRow, 1).Style.Font.FontColor = XLColor.Red;
            ws.Range(nWarnRow, 1, nWarnRow, nLastCol).Merge();
            nWarnRow++;
            nDataStartRow++;
        }
        if (result.isHasNm3Warning)
        {
            ws.Cell(nWarnRow, 1).Value = _l["excel.label.nm3_warning"].Value;
            ws.Cell(nWarnRow, 1).Style.Font.FontColor = XLColor.Red;
            ws.Range(nWarnRow, 1, nWarnRow, nLastCol).Merge();
            nDataStartRow++;
        }

        // 表頭
        ws.Cell(nDataStartRow, 1).Value = _l["excel.col.period"].Value;
        ws.Cell(nDataStartRow, 2).Value = _l["excel.col.kwh"].Value;
        ws.Cell(nDataStartRow, 3).Value = _l["excel.col.nm3"].Value;
        ws.Cell(nDataStartRow, 4).Value = _l["excel.col.specific_energy"].Value;
        ws.Range(nDataStartRow, 1, nDataStartRow, nLastCol).Style.Font.Bold = true;
        ws.Range(nDataStartRow, 1, nDataStartRow, nLastCol).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;

        // 資料列：kWh / Nm³ 小數一位（與頁面一致）；比能耗量級小，維持三位
        for (var i = 0; i < result.buckets.Count; i++)
        {
            var row = nDataStartRow + 1 + i;
            var bucket = result.buckets[i];
            ws.Cell(row, 1).Value = bucket.szLabel;
            ws.Cell(row, 2).Value = bucket.dKwh;
            ws.Cell(row, 2).Style.NumberFormat.Format = "#,##0.0";
            ws.Cell(row, 3).Value = bucket.dNm3;
            ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.0";
            WriteSpecificEnergy(ws.Cell(row, 4), bucket.dKwhPerNm3);
        }

        // 合計列
        var sumRow = nDataStartRow + 1 + result.buckets.Count;
        ws.Cell(sumRow, 1).Value = _l["excel.row.total"].Value;
        ws.Cell(sumRow, 2).Value = result.dTotalKwh;
        ws.Cell(sumRow, 2).Style.NumberFormat.Format = "#,##0.0";
        ws.Cell(sumRow, 3).Value = result.dTotalNm3;
        ws.Cell(sumRow, 3).Style.NumberFormat.Format = "#,##0.0";
        WriteSpecificEnergy(ws.Cell(sumRow, 4), result.dTotalKwhPerNm3);
        ws.Range(sumRow, 1, sumRow, nLastCol).Style.Font.Bold = true;
        ws.Range(sumRow, 1, sumRow, nLastCol).Style.Fill.BackgroundColor = XLColor.LightYellow;

        // 邊框
        var dataRange = ws.Range(nDataStartRow, 1, sumRow, nLastCol);
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        ws.Columns().AdjustToContents();
        for (var c = 1; c <= nLastCol; c++)
        {
            if (ws.Column(c).Width < 18) ws.Column(c).Width = 18;
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>比能耗儲存格：有值寫數字（0.000），null 寫「--」靠右（與頁面一致）</summary>
    private static void WriteSpecificEnergy(IXLCell cell, double? dValue)
    {
        if (dValue.HasValue)
        {
            cell.Value = dValue.Value;
            cell.Style.NumberFormat.Format = "#,##0.000";
        }
        else
        {
            cell.Value = "--";
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
    }
}
