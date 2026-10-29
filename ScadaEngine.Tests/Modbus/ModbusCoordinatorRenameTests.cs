using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Models;
using ScadaEngine.Web.Features.ModbusCoordinator.Models;
using ScadaEngine.Web.Features.Shared.Models;
using ScadaEngine.Web.Services;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Tests.Modbus;

/// <summary>
/// Coordinator 改名（plan 2026-10-29 範圍 B）與子站名稱寫回（範圍 C）的檔案層 + 假 DB 測試：
/// DB 先改且 Id 不變、檔名原子改名、內容與編碼保留、WebEditedAt / RenamedFrom 蓋章、重名與非法字元擋下、
/// 檔案改名失敗回滾 DB；舊 Excel 重匯（改名前工作表名）預設不勾選。
/// </summary>
public class ModbusCoordinatorRenameTests : IDisposable
{
    private readonly string _szFolder;
    private readonly SourceConfigFileIo _io;
    private readonly ModbusConfigFileService _service;
    private readonly FakeRepository _fake;
    private readonly IDataRepository _repository;
    private readonly ModbusExcelAdapter _adapter = new();

    public ModbusCoordinatorRenameTests()
    {
        _szFolder = Path.Combine(Path.GetTempPath(), "mbrename_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_szFolder);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{SourceConfigFileIo.MODBUS_SECTION}:WatchedFolder"] = _szFolder,
        }).Build();
        _io = new SourceConfigFileIo(config, new StubEnv(_szFolder), NullLogger<SourceConfigFileIo>.Instance);
        _service = new ModbusConfigFileService(_io, NullLogger<ModbusConfigFileService>.Instance);
        _repository = DispatchProxy.Create<IDataRepository, FakeRepository>();
        _fake = (FakeRepository)(object)_repository;
    }

    public void Dispose()
    {
        try { Directory.Delete(_szFolder, recursive: true); } catch { /* ignore */ }
    }

    private string PathOf(string szName) => Path.Combine(_szFolder, szName + ".json");

    private void WriteJson(string szName, string szModbusId = "1,2", Encoding? encoding = null)
    {
        var sheet = new ParsedSourceSheet { SheetName = szName };
        sheet.Header["IP"] = "10.0.0.1"; sheet.Header["Port"] = "502"; sheet.Header["ModbusId"] = szModbusId; sheet.Header["ConnectTimeout"] = "1000";
        var p = new ParsedSourcePoint();
        p.Fields["Name"] = "溫度"; p.Fields["Address"] = "40001"; p.Fields["DataType"] = "INTEGER"; p.Fields["Ratio"] = "1";
        sheet.Points.Add(p);
        File.WriteAllText(PathOf(szName), _adapter.ToJson(sheet, "盤A.xlsx"), encoding ?? _adapter.NewFileEncoding);
    }

    [Fact]
    public async Task 改名_DB先改Id不變_檔名改名_內容編碼保留並蓋章()
    {
        WriteJson("冰機盤");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 7, szName = "冰機盤", szModbusID = "1,2" });

        var result = await _service.RenameAsync("冰機盤", "冰水主機盤", _repository);

        Assert.True(result.isSuccess, result.nError.ToString());
        Assert.Equal(7, result.nCoordinatorId);
        Assert.Equal("冰水主機盤", Assert.Single(_fake.Coordinators).szName);
        Assert.Equal(7, _fake.Coordinators[0].Id);
        Assert.False(File.Exists(PathOf("冰機盤")));
        Assert.True(File.Exists(PathOf("冰水主機盤")));

        var (szJson, encoding) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(PathOf("冰水主機盤"));
        Assert.IsType<UnicodeEncoding>(encoding); // UTF-16 LE 原樣
        var root = JsonNode.Parse(szJson)!.AsObject();
        Assert.Equal("10.0.0.1", root["IP"]!.ToString());
        Assert.Equal("盤A.xlsx", root["SourceWorkbook"]!.ToString());
        Assert.NotNull(SourceJsonStamp.ReadWebEdited(root));
        Assert.Equal(new[] { "冰機盤" }, SourceJsonStamp.ReadRenamedFrom(root));
        Assert.Equal("溫度", root["Tags"]![0]!["Name"]!.ToString());
    }

    [Theory]
    [InlineData("", ModbusRenameError.Empty)]
    [InlineData("A:B", ModbusRenameError.InvalidChars)]
    [InlineData("A[1]", ModbusRenameError.InvalidChars)]
    [InlineData("A/B", ModbusRenameError.InvalidChars)]
    [InlineData(" A", ModbusRenameError.InvalidChars)]
    [InlineData("1234567890123456789012345678901X", ModbusRenameError.TooLong)]
    [InlineData("冰機盤", ModbusRenameError.Unchanged)]
    public void 名稱檢核(string szNew, ModbusRenameError expected)
    {
        Assert.Equal(expected, ModbusConfigFileService.ValidateNewName("冰機盤", szNew));
    }

    [Fact]
    public async Task 與既有JSON重名_不分大小寫_拒絕且不動DB()
    {
        WriteJson("GW1");
        WriteJson("gw2");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 1, szName = "GW1", szModbusID = "1" });

        var result = await _service.RenameAsync("GW1", "GW2", _repository);

        Assert.Equal(ModbusRenameError.NameTaken, result.nError);
        Assert.Equal("GW1", _fake.Coordinators[0].szName);
        Assert.True(File.Exists(PathOf("GW1")));
    }

    [Fact]
    public async Task 與DB既有Coordinator重名_拒絕()
    {
        WriteJson("GW1");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 1, szName = "GW1", szModbusID = "1" });
        _fake.Coordinators.Add(new CoordinatorModel { Id = 2, szName = "已刪設備", szModbusID = "1" }); // JSON 已移除但 DB 還在

        var result = await _service.RenameAsync("GW1", "已刪設備", _repository);

        Assert.Equal(ModbusRenameError.NameTaken, result.nError);
    }

    [Fact]
    public async Task 檔案改名失敗_回滾DB名稱()
    {
        WriteJson("GW1");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 3, szName = "GW1", szModbusID = "1" });

        // 鎖住原檔（不允許刪除 / 改名）→ File.Move 失敗
        using (new FileStream(PathOf("GW1"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await _service.RenameAsync("GW1", "GW9", _repository);
            Assert.Equal(ModbusRenameError.WriteFailed, result.nError);
        }

        Assert.Equal("GW1", _fake.Coordinators[0].szName);
        Assert.True(File.Exists(PathOf("GW1")));
        Assert.False(File.Exists(PathOf("GW9")));
    }

    [Fact]
    public async Task Engine尚未載入的設定檔_只改檔名()
    {
        WriteJson("待載入");

        var result = await _service.RenameAsync("待載入", "新設備", _repository);

        Assert.True(result.isSuccess);
        Assert.Null(result.nCoordinatorId);
        Assert.True(File.Exists(PathOf("新設備")));
    }

    [Fact]
    public async Task 子站名稱_寫JSON與DB_正規化()
    {
        WriteJson("GW1", "1,2,5");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 4, szName = "GW1", szModbusID = "1,2,5", szDeviceName = "" });

        var result = await _service.UpdateDeviceNamesAsync(4, " 冰水主機1 ,冰水主機2", _repository);

        Assert.True(result.isSuccess, result.nError.ToString());
        Assert.Equal("冰水主機1,冰水主機2,", result.szDeviceName);
        Assert.Equal("冰水主機1,冰水主機2,", _fake.Coordinators[0].szDeviceName);
        var root = JsonNode.Parse((await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(PathOf("GW1"))).szText)!.AsObject();
        Assert.Equal("冰水主機1,冰水主機2,", root["DeviceName"]!.ToString());
        Assert.NotNull(SourceJsonStamp.ReadWebEdited(root));
    }

    [Fact]
    public async Task 子站名稱_多於站號_拒絕()
    {
        WriteJson("GW1", "1,2");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 4, szName = "GW1", szModbusID = "1,2" });

        var result = await _service.UpdateDeviceNamesAsync(4, "A,B,C", _repository);

        Assert.Equal(ModbusDeviceNameError.TooMany, result.nError);
    }

    [Fact]
    public async Task 改名後拿舊Excel重匯_舊名工作表與新名刪除候選預設不勾()
    {
        WriteJson("冰機盤");
        _fake.Coordinators.Add(new CoordinatorModel { Id = 7, szName = "冰機盤", szModbusID = "1,2" });

        // 改名前匯出的 Excel
        var sheet = _adapter.ParseJson("冰機盤", (await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(PathOf("冰機盤"))).szText)!;
        var oldXlsx = SourceExcelTemplateWriter.Build(_adapter, new[] { sheet });

        Assert.True((await _service.RenameAsync("冰機盤", "冰水主機盤", _repository)).isSuccess);

        var coordinator = new SourceExcelImportCoordinator(_io, new MemoryCache(new MemoryCacheOptions()),
            new StubLocalizer<SourceExcelImportCoordinator>(), NullLogger<SourceExcelImportCoordinator>.Instance);
        using var ms = new MemoryStream(oldXlsx);
        var preview = await coordinator.PreviewAsync(_adapter, ms, "盤A.xlsx", "eng");

        var diff = Assert.Single(preview.Sheets);
        Assert.Equal(SheetDiffKind.Added, diff.Kind);
        Assert.Equal("冰水主機盤", diff.RenamedTo);
        Assert.False(diff.DefaultChecked);
        var candidate = Assert.Single(preview.DeleteCandidates);
        Assert.Equal("冰水主機盤", candidate.Name);
        Assert.False(candidate.DefaultChecked);
    }

    // ── stubs ──

    /// <summary>只實作改名 / 子站名稱用到的三支方法，其餘呼叫即丟例外（不該被用到）</summary>
    public class FakeRepository : DispatchProxy
    {
        public List<CoordinatorModel> Coordinators { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IDataRepository.GetAllCoordinatorsAsync):
                    return Task.FromResult<IEnumerable<CoordinatorModel>>(Coordinators.Select(Clone).ToList());

                case nameof(IDataRepository.RenameCoordinatorAsync):
                {
                    var nId = (int)args![0]!; var szOld = (string)args[1]!; var szNew = (string)args[2]!;
                    if (Coordinators.Any(c => c.Id != nId && string.Equals(c.szName, szNew, StringComparison.OrdinalIgnoreCase)))
                        return Task.FromResult(CoordinatorRenameResult.NameTaken);
                    var target = Coordinators.FirstOrDefault(c => c.Id == nId && c.szName == szOld);
                    if (target == null) return Task.FromResult(CoordinatorRenameResult.NotFound);
                    target.szName = szNew;
                    return Task.FromResult(CoordinatorRenameResult.Success);
                }

                case nameof(IDataRepository.UpdateDeviceNameAsync):
                {
                    var target = Coordinators.FirstOrDefault(c => c.Id == (int)args![0]!);
                    if (target != null) target.szDeviceName = (string)args![1]!;
                    return Task.FromResult(target != null);
                }

                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }

        private static CoordinatorModel Clone(CoordinatorModel c) => new()
        {
            Id = c.Id, szName = c.szName, szModbusID = c.szModbusID, szDeviceName = c.szDeviceName,
            nDelayTime = c.nDelayTime, isMonitorEnabled = c.isMonitorEnabled,
        };
    }

    private sealed class StubEnv : IWebHostEnvironment
    {
        public StubEnv(string szRoot) { ContentRootPath = szRoot; WebRootPath = szRoot; }
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}
