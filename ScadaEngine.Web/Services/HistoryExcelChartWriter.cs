using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ScadaEngine.Web.Services.HistoryStatistics;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 歷史趨勢匯出圖表後處理 — ClosedXML 0.102 不支援圖表，改以 DocumentFormat.OpenXml（ClosedXML 相依，已在專案內）
/// 在指定分頁加 DrawingsPart + ChartPart。
/// 圖表一律參照儲存格（改數字圖會跟著變）並附 strCache / numCache（不重算也能顯示，LibreOffice 亦同）。
/// 版面：從 E1 往下排，每張 22 列高、間隔 1 列。
/// </summary>
public static class HistoryExcelChartWriter
{
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    private const int AnchorCol = 4;       // E
    private const int AnchorColSpan = 10;  // E..N
    private const int ChartRows = 22;
    private const int ChartGapRows = 1;

    private static readonly string[] _aColors =
    [
        "4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47",
        "264478", "9E480E", "636363", "997300", "255E91", "43682B"
    ];

    /// <summary>在 <paramref name="szSheetName"/> 分頁加入圖表，回傳新的 .xlsx 內容</summary>
    public static byte[] AddCharts(byte[] xlsx, string szSheetName, IReadOnlyList<HistoryChartSpec> specs)
    {
        using var ms = new MemoryStream();
        ms.Write(xlsx, 0, xlsx.Length);
        ms.Position = 0;

        using (var doc = SpreadsheetDocument.Open(ms, true))
        {
            var wbPart = doc.WorkbookPart!;
            var sheet = wbPart.Workbook.Sheets!.Elements<Sheet>().First(s => s.Name == szSheetName);
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);

            var drawingsPart = wsPart.DrawingsPart ?? wsPart.AddNewPart<DrawingsPart>();
            var anchors = new List<XElement>();
            for (var i = 0; i < specs.Count; i++)
            {
                var chartPart = drawingsPart.AddNewPart<ChartPart>();
                WriteXml(chartPart, BuildChartSpace(specs[i]));
                anchors.Add(BuildAnchor(i, drawingsPart.GetIdOfPart(chartPart), specs[i].szTitle));
            }
            WriteXml(drawingsPart, new XDocument(new XElement(Xdr + "wsDr",
                new XAttribute(XNamespace.Xmlns + "xdr", Xdr.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
                anchors)));

            AttachDrawing(wsPart, wsPart.GetIdOfPart(drawingsPart));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// worksheet 加 &lt;drawing r:id&gt;。依 schema 順序，drawing 須在 legacyDrawing / tableParts / extLst 等之前。
    /// </summary>
    private static void AttachDrawing(WorksheetPart wsPart, string szRelId)
    {
        var ws = wsPart.Worksheet;
        if (ws.Elements<Drawing>().Any()) return;
        var drawing = new Drawing { Id = szRelId };
        var successor = ws.ChildElements.FirstOrDefault(e =>
            e is LegacyDrawing or LegacyDrawingHeaderFooter or DocumentFormat.OpenXml.Spreadsheet.Picture
              or OleObjects or Controls or WebPublishItems or TableParts or WorksheetExtensionList);
        if (successor != null) ws.InsertBefore(drawing, successor);
        else ws.Append(drawing);
        ws.Save();
    }

    private static void WriteXml(OpenXmlPart part, XDocument xml)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        xml.Save(stream, SaveOptions.DisableFormatting);
    }

    // ── Drawing anchor ───────────────────────────────────────────────────

    private static XElement BuildAnchor(int nIndex, string szChartRelId, string szTitle)
    {
        var nFromRow = nIndex * (ChartRows + ChartGapRows);
        XElement Marker(string szTag, int nCol, int nRow) => new(Xdr + szTag,
            new XElement(Xdr + "col", nCol), new XElement(Xdr + "colOff", 0),
            new XElement(Xdr + "row", nRow), new XElement(Xdr + "rowOff", 0));

        return new XElement(Xdr + "twoCellAnchor", new XAttribute("editAs", "oneCell"),
            Marker("from", AnchorCol, nFromRow),
            Marker("to", AnchorCol + AnchorColSpan, nFromRow + ChartRows),
            new XElement(Xdr + "graphicFrame", new XAttribute("macro", ""),
                new XElement(Xdr + "nvGraphicFramePr",
                    new XElement(Xdr + "cNvPr", new XAttribute("id", nIndex + 2), new XAttribute("name", szTitle)),
                    new XElement(Xdr + "cNvGraphicFramePr")),
                new XElement(Xdr + "xfrm",
                    new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                    new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData", new XAttribute("uri", C.NamespaceName),
                        new XElement(C + "chart", new XAttribute(R + "id", szChartRelId))))),
            new XElement(Xdr + "clientData"));
    }

    // ── Chart space ──────────────────────────────────────────────────────

    private static XDocument BuildChartSpace(HistoryChartSpec spec)
    {
        XElement plotArea;
        XElement? legend = null;
        switch (spec.type)
        {
            case HistoryChartType.Bar:
                plotArea = new XElement(C + "plotArea", new XElement(C + "layout"), BuildBar(spec),
                    CatAxis(1, 2), ValAxis(2, 1, "l", isGrid: true, szNumFmt: null, crossBetween: "between"));
                break;
            case HistoryChartType.Pie:
                plotArea = new XElement(C + "plotArea", new XElement(C + "layout"), BuildPie(spec));
                legend = Legend("b");
                break;
            default:
                plotArea = new XElement(C + "plotArea", new XElement(C + "layout"), BuildScatter(spec),
                    ValAxis(1, 2, "b", isGrid: false, szNumFmt: "mm-dd hh:mm", crossBetween: "midCat", xRange: XRange(spec)),
                    ValAxis(2, 1, "l", isGrid: true, szNumFmt: null, crossBetween: "midCat"));
                legend = Legend("b");
                break;
        }

        var chart = new XElement(C + "chart",
            Title(spec.szTitle),
            Val("autoTitleDeleted", 0),
            plotArea,
            legend,
            Val("plotVisOnly", 1),
            Val("dispBlanksAs", "gap"));

        return new XDocument(new XElement(C + "chartSpace",
            new XAttribute(XNamespace.Xmlns + "c", C.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
            Val("roundedCorners", 0),
            chart));
    }

    private static XElement BuildBar(HistoryChartSpec spec)
    {
        var s = spec.series[0];
        return new XElement(C + "barChart",
            Val("barDir", "col"), Val("grouping", "clustered"), Val("varyColors", 0),
            new XElement(C + "ser",
                Val("idx", 0), Val("order", 0),
                new XElement(C + "tx", new XElement(C + "v", s.szName)),
                SolidFill(_aColors[0]),
                Val("invertIfNegative", 0),
                DataLabels(szNumFmt: "0.00", szPos: "outEnd", isValue: true, isPercent: false),
                new XElement(C + "cat", StrRef(s)),
                new XElement(C + "val", NumRef(s, s.szYCol, s.adY, "General"))),
            Val("gapWidth", 80),
            Val("axId", 1), Val("axId", 2));
    }

    private static XElement BuildPie(HistoryChartSpec spec)
    {
        var s = spec.series[0];
        return new XElement(C + "pieChart",
            Val("varyColors", 1),
            new XElement(C + "ser",
                Val("idx", 0), Val("order", 0),
                new XElement(C + "tx", new XElement(C + "v", s.szName)),
                s.aszCategories.Select((_, i) => new XElement(C + "dPt",
                    Val("idx", i), Val("bubble3D", 0), SolidFill(_aColors[i % _aColors.Length]))),
                DataLabels(szNumFmt: "0.0%", szPos: "bestFit", isValue: false, isPercent: true),
                new XElement(C + "cat", StrRef(s)),
                new XElement(C + "val", NumRef(s, s.szYCol, s.adY, "General"))),
            Val("firstSliceAng", 0));
    }

    private static XElement BuildScatter(HistoryChartSpec spec)
        => new(C + "scatterChart",
            Val("scatterStyle", "lineMarker"), Val("varyColors", 0),
            spec.series.Select((s, i) => new XElement(C + "ser",
                Val("idx", i), Val("order", i),
                new XElement(C + "tx", new XElement(C + "v", s.szName)),
                new XElement(C + "spPr",
                    new XElement(A + "ln", new XAttribute("w", 19050),
                        new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", _aColors[i % _aColors.Length]))))),
                new XElement(C + "marker", Val("symbol", "none")),
                new XElement(C + "xVal", NumRef(s, s.szXCol, s.adX.Select(x => (double?)x).ToList(), "yyyy-mm-dd hh:mm")),
                new XElement(C + "yVal", NumRef(s, s.szYCol, s.adY, "General")),
                Val("smooth", 0))),
            Val("axId", 1), Val("axId", 2));

    // ── 座標軸 / 標題 / 圖例 ─────────────────────────────────────────────

    private static XElement CatAxis(int nId, int nCross) => new(C + "catAx",
        Val("axId", nId),
        new XElement(C + "scaling", Val("orientation", "minMax")),
        Val("delete", 0),
        Val("axPos", "b"),
        Val("majorTickMark", "out"), Val("minorTickMark", "none"), Val("tickLblPos", "low"),
        Val("crossAx", nCross), Val("crosses", "autoZero"),
        Val("auto", 1), Val("lblAlgn", "ctr"), Val("lblOffset", 100), Val("noMultiLvlLbl", 0));

    private static XElement ValAxis(int nId, int nCross, string szPos, bool isGrid, string? szNumFmt,
        string crossBetween, (double dMin, double dMax)? xRange = null)
    {
        var scaling = new XElement(C + "scaling", Val("orientation", "minMax"));
        if (xRange.HasValue)
        {
            scaling.Add(Val("max", Num(xRange.Value.dMax)));
            scaling.Add(Val("min", Num(xRange.Value.dMin)));
        }
        return new XElement(C + "valAx",
            Val("axId", nId),
            scaling,
            Val("delete", 0),
            Val("axPos", szPos),
            isGrid ? new XElement(C + "majorGridlines") : null,
            szNumFmt != null
                ? new XElement(C + "numFmt", new XAttribute("formatCode", szNumFmt), new XAttribute("sourceLinked", 0))
                : new XElement(C + "numFmt", new XAttribute("formatCode", "General"), new XAttribute("sourceLinked", 1)),
            Val("majorTickMark", "out"), Val("minorTickMark", "none"), Val("tickLblPos", szPos == "b" ? "low" : "nextTo"),
            Val("crossAx", nCross), Val("crosses", "autoZero"),
            Val("crossBetween", crossBetween));
    }

    private static (double, double)? XRange(HistoryChartSpec spec)
    {
        var xs = spec.series.SelectMany(s => s.adX).ToList();
        if (xs.Count == 0) return null;
        var dMin = xs.Min();
        var dMax = xs.Max();
        return dMax > dMin ? (dMin, dMax) : null;
    }

    private static XElement Title(string szText) => new(C + "title",
        new XElement(C + "tx", new XElement(C + "rich",
            new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
            new XElement(A + "p",
                new XElement(A + "pPr", new XElement(A + "defRPr", new XAttribute("sz", 1200), new XAttribute("b", 1))),
                new XElement(A + "r",
                    new XElement(A + "rPr", new XAttribute("lang", "zh-TW"), new XAttribute("sz", 1200), new XAttribute("b", 1)),
                    new XElement(A + "t", szText))))),
        Val("overlay", 0));

    private static XElement Legend(string szPos) => new(C + "legend", Val("legendPos", szPos), Val("overlay", 0));

    private static XElement DataLabels(string szNumFmt, string szPos, bool isValue, bool isPercent) => new(C + "dLbls",
        new XElement(C + "numFmt", new XAttribute("formatCode", szNumFmt), new XAttribute("sourceLinked", 0)),
        new XElement(C + "spPr", new XElement(A + "noFill"), new XElement(A + "ln", new XElement(A + "noFill"))),
        Val("dLblPos", szPos),
        Val("showLegendKey", 0), Val("showVal", isValue ? 1 : 0), Val("showCatName", 0),
        Val("showSerName", 0), Val("showPercent", isPercent ? 1 : 0), Val("showBubbleSize", 0));

    private static XElement SolidFill(string szColor) => new(C + "spPr",
        new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", szColor))));

    // ── 參照 + cache ─────────────────────────────────────────────────────

    private static string Ref(HistoryChartSeries s, string szCol)
        => $"'{s.szSheet.Replace("'", "''")}'!${szCol}${s.nFirstRow}:${szCol}${s.nLastRow}";

    private static XElement StrRef(HistoryChartSeries s) => new(C + "strRef",
        new XElement(C + "f", Ref(s, s.szXCol)),
        new XElement(C + "strCache",
            Val("ptCount", s.aszCategories.Count),
            s.aszCategories.Select((v, i) => new XElement(C + "pt", new XAttribute("idx", i), new XElement(C + "v", v)))));

    private static XElement NumRef(HistoryChartSeries s, string szCol, IReadOnlyList<double?> values, string szFormat)
        => new(C + "numRef",
            new XElement(C + "f", Ref(s, szCol)),
            new XElement(C + "numCache",
                new XElement(C + "formatCode", szFormat),
                Val("ptCount", values.Count),
                values.Select((v, i) => v.HasValue
                    ? new XElement(C + "pt", new XAttribute("idx", i), new XElement(C + "v", Num(v.Value)))
                    : null)));

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static XElement Val(string szName, object value) => new(C + szName, new XAttribute("val", value));
}
