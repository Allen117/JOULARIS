using System.Globalization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.HistoryStatistics;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace ScadaEngine.Tests.History;

/// <summary>
/// 歷史趨勢匯出讀回測試 — 產檔後以 ClosedXML / OpenXml SDK 讀回，鎖住：
/// 分頁組成（統計 + 圖表 + 各點位資料）、統計值、圖表數量依「類型 × 單位」、
/// 圓餅「同單位非零 ≥2 才畫」、圖表參照儲存格且帶 cache、OpenXml schema 驗證無錯（Excel 不跳修復）。
/// </summary>
public class HistoryExcelExporterTests
{
    private static readonly DateTime _dtStart = new(2026, 10, 1, 0, 0, 0);
    private static readonly DateTime _dtEnd = _dtStart.AddMinutes(10);

    private static HistoryExcelExporter CreateExporter()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance);
        return new HistoryExcelExporter(new StringLocalizer<HistoryExcelExporter>(factory));
    }

    /// <summary>每分鐘一筆；null = Bad</summary>
    private static HistoryPointResult Point(string szSID, string szName, string szUnit, params double?[] aValues)
    {
        var data = aValues.Select((v, i) => new HistoryDataModel
        {
            szSID = szSID,
            fValue = v.HasValue ? (float)v.Value : 999f,
            nQuality = v.HasValue ? 1 : 0,
            dtTimestamp = _dtStart.AddMinutes(i)
        }).ToList();
        return new HistoryPointResult
        {
            szSID = szSID,
            szName = szName,
            szUnit = szUnit,
            data = data,
            stats = HistoryStatisticsCalculator.Calculate(szName, szUnit, data, _dtStart, _dtEnd, 1, 1)
        };
    }

    private static List<HistoryPointResult> MixedPoints() =>
    [
        Point("1-S1", "電表A", "kWh", 100, 105, 110),     // diff 10
        Point("1-S2", "電表B", "kWh", 0, 20),             // diff 20
        Point("1-S3", "電表C", "kWh", 50, 50),            // diff 0 → 不進圓餅
        Point("1-S4", "水表", "m3", 1, 3),                // m³ 只有 1 點 → 只有長條
        Point("1-S5", "冰水出水溫度", "°C", 20, 21, null, 22),
        Point("1-S6", "冰水回水溫度", "°C", 30, 31),
        Point("1-S7", "壓力", "", 1.5, 2.5),              // 單位空 → 自成一張折線
        Point("1-S8", "泵浦運轉", "", 0, 1, 0),           // 狀態值 → 不畫
        Point("1-S9", "今日用電", "kWh", 5, 10),          // 期間值 → 不畫
        Point("1-S10", "斷線電表", "kWh"),                // 無資料 → 無資料分頁
    ];

    private static T WithCulture<T>(string szCulture, Func<T> fn)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(szCulture);
            return fn();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static byte[] Export(List<HistoryPointResult> points, string szCulture = "zh-TW")
        => WithCulture(szCulture, () => CreateExporter().Export(points, _dtStart, _dtEnd, 1));

    [Fact]
    public void 分頁組成_統計圖表在前_無資料點位不建資料頁()
    {
        using var wb = new XLWorkbook(new MemoryStream(Export(MixedPoints())));
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal("統計", names[0]);
        Assert.Equal("圖表", names[1]);
        Assert.Equal(2 + 9, names.Count);
        Assert.DoesNotContain("斷線電表", names);
    }

    [Fact]
    public void 統計分頁_數值與備註()
    {
        using var wb = new XLWorkbook(new MemoryStream(Export(MixedPoints())));
        var ws = wb.Worksheet("統計");
        // 表頭第 4 列，資料自第 5 列依輸入順序
        Assert.Equal("電表A", ws.Cell(5, 1).GetString());
        Assert.Equal("累計值", ws.Cell(5, 3).GetString());
        Assert.Equal(10, ws.Cell(5, 10).GetDouble(), 9);

        // 冰水出水溫度：Bad 不算 → 有效 3 筆、平均 21
        Assert.Equal(4, ws.Cell(9, 4).GetDouble());
        Assert.Equal(3, ws.Cell(9, 5).GetDouble());
        Assert.Equal(21, ws.Cell(9, 8).GetDouble(), 9);
        Assert.True(ws.Cell(9, 10).IsEmpty());

        Assert.Equal("狀態值", ws.Cell(12, 3).GetString());
        Assert.Equal("期間值", ws.Cell(13, 3).GetString());

        // 無資料：數值欄空、備註
        Assert.True(ws.Cell(14, 6).IsEmpty());
        Assert.Contains("整段無有效資料", ws.Cell(14, 11).GetString());
    }

    [Fact]
    public void 資料分頁_有效值欄Bad留空()
    {
        using var wb = new XLWorkbook(new MemoryStream(Export(MixedPoints())));
        var ws = wb.Worksheet("冰水出水溫度");
        Assert.Equal("Bad", ws.Cell(4, 3).GetString());
        Assert.False(ws.Cell(4, 2).IsEmpty());
        Assert.True(ws.Cell(4, 4).IsEmpty());
        Assert.Equal(22, ws.Cell(5, 4).GetDouble(), 9);
    }

    [Fact]
    public void 圖表數量依類型乘單位_且帶參照與cache()
    {
        using var doc = SpreadsheetDocument.Open(new MemoryStream(Export(MixedPoints())), false);
        var charts = doc.WorkbookPart!.WorksheetParts
            .Where(w => w.DrawingsPart != null)
            .SelectMany(w => w.DrawingsPart!.ChartParts)
            .Select(c => c.ChartSpace)
            .ToList();

        // 長條：kWh、m³；圓餅：kWh（m³ 只有 1 點、kWh 的 0 不算）；折線：°C、壓力
        Assert.Equal(5, charts.Count);
        Assert.Equal(2, charts.Count(c => c.Descendants<C.BarChart>().Any()));
        Assert.Equal(1, charts.Count(c => c.Descendants<C.PieChart>().Any()));
        Assert.Equal(2, charts.Count(c => c.Descendants<C.ScatterChart>().Any()));

        // 圓餅只含非零的 2 點
        var pie = charts.Single(c => c.Descendants<C.PieChart>().Any());
        Assert.Equal(2u, pie.Descendants<C.StringCache>().Single().PointCount!.Val!.Value);
        Assert.Equal("'圖表'!$A$2:$A$3", pie.Descendants<C.Formula>().First().Text);

        // °C 折線兩條數列，參照各自資料分頁 D 欄；Bad 不入 cache
        var line = charts.First(c => c.Descendants<C.ScatterChart>().Any()
                                     && c.Descendants<C.ScatterChartSeries>().Count() == 2);
        var yFormulas = line.Descendants<C.YValues>().Select(y => y.Descendants<C.Formula>().Single().Text).ToList();
        Assert.Contains("'冰水出水溫度'!$D$2:$D$5", yFormulas);
        var yCache = line.Descendants<C.YValues>().First().Descendants<C.NumberingCache>().Single();
        Assert.Equal(4u, yCache.PointCount!.Val!.Value);
        Assert.Equal(3, yCache.Elements<C.NumericPoint>().Count());

        Assert.All(charts, c => Assert.NotEmpty(c.Descendants<C.NumberingCache>()));
    }

    [Fact]
    public void OpenXml結構驗證無錯()
    {
        using var doc = SpreadsheetDocument.Open(new MemoryStream(Export(MixedPoints())), false);
        var errors = new OpenXmlValidator().Validate(doc)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void 單點位_仍有統計與圖表分頁()
    {
        using var wb = new XLWorkbook(new MemoryStream(Export([Point("1-S1", "溫度", "°C", 20, 21)])));
        Assert.Equal(new[] { "統計", "圖表", "溫度" }, wb.Worksheets.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void 無可畫圖表_圖表分頁有說明且無drawing()
    {
        var bytes = Export([Point("1-S8", "泵浦運轉", "", 0, 1)]);
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            Assert.Contains("無可繪製", wb.Worksheet("圖表").Cell(1, 1).GetString());
        using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        Assert.All(doc.WorkbookPart!.WorksheetParts, w => Assert.Null(w.DrawingsPart));
    }

    [Fact]
    public void 英文語系_分頁名與類型()
    {
        using var wb = new XLWorkbook(new MemoryStream(Export(MixedPoints(), "en")));
        Assert.Equal("Statistics", wb.Worksheets.First().Name);
        Assert.Equal("Charts", wb.Worksheets.Skip(1).First().Name);
        Assert.Equal("Cumulative", wb.Worksheet("Statistics").Cell(5, 3).GetString());
    }
}
