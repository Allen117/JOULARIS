using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Tests.SourceExcel;

/// <summary>
/// 差異比對 / SID 位移 / 刪除候選分區（plan 2026-10-06 決策 5、7）。
/// </summary>
public class SourceExcelDiffTests
{
    private static readonly ModbusExcelAdapter _modbus = new();

    private static ParsedSourceSheet Sheet(string szName, params string[] pointNames)
    {
        var s = new ParsedSourceSheet { SheetName = szName };
        s.Header["IP"] = "10.0.0.1"; s.Header["Port"] = "502"; s.Header["ModbusId"] = "1"; s.Header["ConnectTimeout"] = "1000";
        var nAddr = 40001;
        foreach (var szPoint in pointNames)
        {
            var p = new ParsedSourcePoint { RowNumber = s.Points.Count + 3 };
            p.Fields["Name"] = szPoint; p.Fields["Address"] = (nAddr++).ToString(); p.Fields["DataType"] = "INTEGER";
            p.Fields["Ratio"] = "1"; p.Fields["Unit"] = ""; p.Fields["Min"] = "0"; p.Fields["Max"] = "100"; p.Fields["Device"] = "";
            s.Points.Add(p);
        }
        return s;
    }

    [Fact]
    public void Build_NoExisting_Added()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "B"), null, _modbus);
        Assert.Equal(SheetDiffKind.Added, diff.Kind);
        Assert.True(diff.DefaultChecked);
        Assert.Equal(0, diff.OldPointCount);
        Assert.Equal(2, diff.NewPointCount);
    }

    [Fact]
    public void Build_Identical_Unchanged()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "B"), Sheet("D", "A", "B"), _modbus);
        Assert.Equal(SheetDiffKind.Unchanged, diff.Kind);
        Assert.False(diff.DefaultChecked);
        Assert.Empty(diff.HeaderChanges);
        Assert.Empty(diff.PointChanges);
        Assert.False(diff.HasSidShift);
    }

    [Fact]
    public void Build_HeaderChange_Overwrite()
    {
        var uploaded = Sheet("D", "A"); uploaded.Header["IP"] = "10.0.0.9";
        var diff = SourceExcelDiffBuilder.Build(uploaded, Sheet("D", "A"), _modbus);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.Equal("IP: 10.0.0.1 → 10.0.0.9", Assert.Single(diff.HeaderChanges));
    }

    [Fact]
    public void Build_PointFieldChange_SummaryPerField()
    {
        var uploaded = Sheet("D", "A", "B");
        uploaded.Points[1].Fields["Ratio"] = "0.1";
        uploaded.Points[1].Fields["Unit"] = "kW";
        var diff = SourceExcelDiffBuilder.Build(uploaded, Sheet("D", "A", "B"), _modbus);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        var change = Assert.Single(diff.PointChanges);
        Assert.Equal(2, change.Index);
        Assert.Equal("Modified", change.Change);
        Assert.Equal("Ratio: 1 → 0.1, Unit:  → kW", change.Summary);
        Assert.False(diff.HasSidShift);
    }

    [Fact]
    public void Build_TailAppend_NoShift()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "B", "C"), Sheet("D", "A", "B"), _modbus);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.False(diff.HasSidShift);
        Assert.Equal(1, diff.TailAddedCount);
        Assert.Equal("Added", Assert.Single(diff.PointChanges).Change);
    }

    [Fact]
    public void Build_TailRemove_NoShift_ButReported()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A"), Sheet("D", "A", "B"), _modbus);
        Assert.False(diff.HasSidShift);
        Assert.Equal(1, diff.TailRemovedCount);
        var c = Assert.Single(diff.PointChanges);
        Assert.Equal("Removed", c.Change);
        Assert.Equal("B", c.Name);
    }

    [Fact]
    public void Build_MiddleInsert_SidShiftFromThatIndex()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "X", "B", "C"), Sheet("D", "A", "B", "C"), _modbus);
        Assert.True(diff.HasSidShift);
        Assert.Equal(2, diff.SidShiftFromIndex);
    }

    [Fact]
    public void Build_MiddleDelete_SidShift()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "C"), Sheet("D", "A", "B", "C"), _modbus);
        Assert.True(diff.HasSidShift);
        Assert.Equal(2, diff.SidShiftFromIndex);
    }

    [Fact]
    public void Build_Reorder_SidShift()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "B", "A"), Sheet("D", "A", "B"), _modbus);
        Assert.True(diff.HasSidShift);
        Assert.Equal(1, diff.SidShiftFromIndex);
    }

    [Fact]
    public void Build_InPlaceRename_NotShift()
    {
        var diff = SourceExcelDiffBuilder.Build(Sheet("D", "A", "B2", "C"), Sheet("D", "A", "B", "C"), _modbus);
        Assert.False(diff.HasSidShift);
        Assert.Equal(SheetDiffKind.Overwrite, diff.Kind);
        Assert.Equal("Name: B → B2", Assert.Single(diff.PointChanges).Summary);
    }

    [Fact]
    public void Build_UploadedHasErrors_KindError()
    {
        var uploaded = Sheet("D", "A");
        uploaded.Errors.Add(SheetIssue.At(3, "B", "srcxl.err.address_invalid"));
        var diff = SourceExcelDiffBuilder.Build(uploaded, Sheet("D", "A"), _modbus);
        Assert.Equal(SheetDiffKind.Error, diff.Kind);
        Assert.False(diff.DefaultChecked);
        Assert.Single(diff.Errors);
    }

    // ── 刪除候選分區 ──

    [Theory]
    [InlineData("盤A.xlsx", "盤a")]
    [InlineData("  盤A.XLSM ", "盤a")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeWorkbookName_Cases(string? szInput, string szExpected)
    {
        Assert.Equal(szExpected, SourceExcelDiffBuilder.NormalizeWorkbookName(szInput));
    }

    /// <summary>含磁碟機代號的 Windows 完整路徑：Path.GetFileNameWithoutExtension 只在 Windows 認反斜線分隔</summary>
    [ScadaEngine.Tests.Common.WindowsOnlyFact]
    public void NormalizeWorkbookName_WindowsFullPath()
    {
        Assert.Equal("盤a", SourceExcelDiffBuilder.NormalizeWorkbookName("C:\\x\\盤A.xlsx"));
    }

    [Fact]
    public void ClassifyExisting_SameWorkbookMissingSheet_IsCandidate_OthersSeparate()
    {
        var existing = new List<ExistingSourceInfo>
        {
            new() { Name = "GW1", SourceWorkbook = "盤A.xlsx" },      // 這次有上傳 → 不列
            new() { Name = "GW2", SourceWorkbook = "盤A.xlsx" },      // 同檔、這次沒有 → 候選（預設勾）
            new() { Name = "GW9", SourceWorkbook = "盤B.xlsx" },      // 其他盤 → 其他（不勾）
            new() { Name = "Legacy", SourceWorkbook = null },         // 舊流程手動放的 → 其他
        };

        var (candidates, others) = SourceExcelDiffBuilder.ClassifyExisting(existing, new[] { "GW1" }, "盤a.XLSX");

        var c = Assert.Single(candidates);
        Assert.Equal("GW2", c.Name);
        Assert.True(c.DefaultChecked);

        Assert.Equal(new[] { "GW9", "Legacy" }, others.Select(o => o.Name));
        Assert.All(others, o => Assert.False(o.DefaultChecked));
    }

    [Fact]
    public void ClassifyExisting_RenamedWorkbook_OldDevicesFallToOthers()
    {
        var existing = new List<ExistingSourceInfo> { new() { Name = "GW2", SourceWorkbook = "盤A.xlsx" } };
        var (candidates, others) = SourceExcelDiffBuilder.ClassifyExisting(existing, new[] { "GW1" }, "盤A_v2.xlsx");
        Assert.Empty(candidates);
        Assert.Single(others);
    }

    [Fact]
    public void FindRenameHint_SamePointNames_ReturnsCandidate()
    {
        var added = Sheet("NewName", "A", "B");
        var candidates = new[] { Sheet("Other", "X"), Sheet("OldName", "A", "B") };
        Assert.Equal("OldName", SourceExcelDiffBuilder.FindRenameHint(added, candidates));
        Assert.Null(SourceExcelDiffBuilder.FindRenameHint(Sheet("N", "A", "C"), candidates));
    }
}
