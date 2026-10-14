using System.Text.Json.Nodes;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Tests.SourceExcel;

/// <summary>
/// plan 2026-10-12 問題 A：DB 來源熱編輯改 Name / Unit 時，JSON 頂層的 SourceWorkbook 與任何非 DTO 欄位必須原樣保留，
/// 並寫入 WebEditedAt。純函數 <see cref="DbPointConfigFileService.ApplyPointEdit"/> 可直接測，不碰 DB。
/// </summary>
public class DbPointHotEditPreserveTests
{
    private const string EXISTING = """
    {
      "Name": "DB1",
      "PollingInterval": 1000,
      "ConnectTimeout": 1000,
      "MonitorEnabled": true,
      "SourceWorkbook": "盤A.xlsx",
      "CustomNote": "hand-written field",
      "Points": [
        { "Name": "A", "Unit": "kWh", "Min": 0, "Max": 100, "Extra": "keep-me" },
        { "Name": "B", "Unit": "", "Min": 0, "Max": 50 }
      ]
    }
    """;

    [Fact]
    public void ApplyPointEdit_PreservesSourceWorkbookAndUnknownFields_AndStampsWebEditedAt()
    {
        var szNew = DbPointConfigFileService.ApplyPointEdit(EXISTING, 2, "B改", "°C", out var points, out var nError);

        Assert.Equal(DbPointConfigFileService.PointEditError.None, nError);
        Assert.NotNull(szNew);
        Assert.NotNull(points);

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(szNew!));
        Assert.Equal("盤A.xlsx", root["SourceWorkbook"]?.ToString());
        Assert.Equal("hand-written field", root["CustomNote"]?.ToString());
        Assert.Equal("DB1", root["Name"]?.ToString());
        Assert.True(root["MonitorEnabled"]!.GetValue<bool>());

        var arr = Assert.IsType<JsonArray>(root["Points"]);
        Assert.Equal(2, arr.Count);
        Assert.Equal("A", arr[0]!["Name"]?.ToString());
        Assert.Equal("keep-me", arr[0]!["Extra"]?.ToString());     // 點位層未知欄位亦保留
        Assert.Equal("B改", arr[1]!["Name"]?.ToString());
        Assert.Equal("°C", arr[1]!["Unit"]?.ToString());
        Assert.Equal(50, arr[1]!["Max"]!.GetValue<int>());

        var edited = SourceJsonStamp.ReadWebEdited(root);
        Assert.NotNull(edited);
        Assert.True((DateTimeOffset.Now - edited!.Value).Duration() < TimeSpan.FromMinutes(1));

        // Excel adapter 讀回時也看得到（「依檔案匯出」篩選依據）
        var parsed = new DbPointExcelAdapter().ParseJson("DB1", szNew!);
        Assert.NotNull(parsed);
        Assert.Equal("盤A.xlsx", parsed!.SourceWorkbook);
        Assert.NotNull(parsed.WebEditedAt);
    }

    [Fact]
    public void ApplyPointEdit_CaseInsensitiveKeys_NoDuplicateKeyAdded()
    {
        const string szLower = """{ "name": "x", "points": [ { "name": "A", "unit": "u", "min": 0, "max": 1 } ] }""";
        var szNew = DbPointConfigFileService.ApplyPointEdit(szLower, 1, "A2", "v", out var points, out var nError);

        Assert.Equal(DbPointConfigFileService.PointEditError.None, nError);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(szNew!));
        var pt = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(root["points"])[0]);
        Assert.Equal("A2", pt["name"]?.ToString());
        Assert.Equal("v", pt["unit"]?.ToString());
        Assert.False(pt.ContainsKey("Name"));   // 沿用原鍵大小寫，不另加 "Name"

        var models = DbPointConfigFileService.BuildPointModels(7, points!);
        var m = Assert.Single(models);
        Assert.Equal("DB7-S1", m.szSID);
        Assert.Equal("A2", m.szName);
        Assert.Equal("v", m.szUnit);
        Assert.Equal(1f, m.fMax);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ApplyPointEdit_SequenceOutOfRange_PointNotFound(int nSeq)
    {
        var szNew = DbPointConfigFileService.ApplyPointEdit(EXISTING, nSeq, "X", "", out _, out var nError);
        Assert.Null(szNew);
        Assert.Equal(DbPointConfigFileService.PointEditError.PointNotFound, nError);
    }

    [Fact]
    public void ApplyPointEdit_InvalidJson_ReportsInvalid()
    {
        var szNew = DbPointConfigFileService.ApplyPointEdit("{ not json", 1, "X", "", out _, out var nError);
        Assert.Null(szNew);
        Assert.Equal(DbPointConfigFileService.PointEditError.InvalidJson, nError);
    }

    [Fact]
    public void BuildPointModels_CapsAt100_AndDefaultsMinMax()
    {
        var arr = new JsonArray();
        for (var i = 0; i < 105; i++) arr.Add(new JsonObject { ["Name"] = $"P{i}" });

        var models = DbPointConfigFileService.BuildPointModels(3, arr);
        Assert.Equal(100, models.Count);
        Assert.Equal("DB3-S100", models[^1].szSID);
        Assert.Equal(0f, models[0].fMin);
        Assert.Equal(100f, models[0].fMax);
    }
}
