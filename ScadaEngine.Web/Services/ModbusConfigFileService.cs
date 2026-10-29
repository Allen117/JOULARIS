using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Models;
using ScadaEngine.Web.Features.ModbusCoordinator.Models;
using ScadaEngine.Web.Services.SourceExcel;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 讀寫 Engine 執行目錄下的 Modbus JSON 設定檔（點位熱編輯）。
///
/// - 只准「原地編輯」點位欄位（Name / Address / DataType / Ratio / Unit / Min / Max），
///   點位數量、順序與設備層欄位（IP / Port / ModbusId / ConnectTimeout）一律鎖死 —
///   SID 由陣列索引產生、控制指令用 TagIndex 定位，結構一變就是歷史資料錯位 + 控制寫錯暫存器。
///   DataType 限 Engine 支援的型態白名單（影響暫存器讀取長度與控制轉換）。
/// - 檔案 I/O（路徑解析、原子寫檔、編碼偵測、鏡像寫回）走共用 <see cref="SourceConfigFileIo"/>，
///   與 Excel 匯入（SourceExcelImportCoordinator）同一份行為。
/// - 保留原檔編碼（現場檔案為 UTF-16 LE with BOM，工具產生）。
/// </summary>
public class ModbusConfigFileService
{
    private readonly ILogger<ModbusConfigFileService> _logger;
    private readonly SourceConfigFileIo _io;

    /// <summary>寫檔序列化鎖 — 防止兩個 Admin 同時存檔互相覆蓋</summary>
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Engine 支援的資料型態 — 直接引用 Engine 端白名單（唯一真相來源 ModbusTagModel.SupportedDataTypes），
    /// 新增型別時不會漏改這一側。Engine 端比對時 ToUpper，故清單為大寫正規形；UI 下拉同此清單。
    /// </summary>
    public static readonly string[] SupportedDataTypes = ModbusTagModel.SupportedDataTypes;

    public ModbusConfigFileService(SourceConfigFileIo io, ILogger<ModbusConfigFileService> logger)
    {
        _logger = logger;
        _io = io;
    }

    /// <summary>
    /// 讀取指定 Coordinator（= JSON 檔名，不含副檔名）的點位清單，檔案不存在回傳 null
    /// </summary>
    public async Task<ModbusPointsFileModel?> GetPointsAsync(string szCoordinatorName)
    {
        var szFilePath = _io.ResolveConfigFilePath(SourceConfigFileIo.MODBUS_SECTION, szCoordinatorName);
        if (szFilePath == null || !File.Exists(szFilePath))
            return null;

        var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szFilePath);
        var root = JsonNode.Parse(szJson);
        if (root == null) return null;

        var model = new ModbusPointsFileModel
        {
            CoordinatorName = szCoordinatorName,
            IP = root["IP"]?.ToString() ?? string.Empty,
            Port = int.TryParse(root["Port"]?.ToString(), out var nPort) ? nPort : 502,
            ModbusId = root["ModbusId"]?.ToString() ?? string.Empty,
            ConnectTimeout = int.TryParse(root["ConnectTimeout"]?.ToString(), out var nTimeout) ? nTimeout : 1000,
        };

        if (root["Tags"] is JsonArray tags)
        {
            foreach (var tag in tags)
            {
                if (tag == null) continue;
                model.Points.Add(new ModbusPointDto
                {
                    Name = tag["Name"]?.ToString() ?? string.Empty,
                    Address = tag["Address"]?.ToString() ?? string.Empty,
                    DataType = tag["DataType"]?.ToString() ?? string.Empty,
                    Ratio = tag["Ratio"]?.ToString() ?? "1",
                    Unit = tag["Unit"]?.ToString() ?? string.Empty,
                    Min = tag["Min"]?.ToString() ?? string.Empty,
                    Max = tag["Max"]?.ToString() ?? string.Empty,
                    Device = tag["Device"]?.ToString() ?? string.Empty,
                });
            }
        }

        return model;
    }

    /// <summary>
    /// 只讀取設備層連線端點（IP / Port），不解析點位 — 清單頁右側詳情顯示用；檔案不存在回傳 null
    /// </summary>
    public async Task<(string szIp, int nPort)?> GetEndpointAsync(string szCoordinatorName)
    {
        var szFilePath = _io.ResolveConfigFilePath(SourceConfigFileIo.MODBUS_SECTION, szCoordinatorName);
        if (szFilePath == null || !File.Exists(szFilePath))
            return null;

        var (szJson, _) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szFilePath);
        var root = JsonNode.Parse(szJson);
        if (root == null) return null;

        return (
            root["IP"]?.ToString() ?? string.Empty,
            int.TryParse(root["Port"]?.ToString(), out var nPort) ? nPort : 502);
    }

    /// <summary>
    /// 原地更新點位欄位並原子寫回。存檔前重讀原檔驗證結構（數量、DataType）未變，不合即拒絕。
    /// 無任何欄位變更時不寫檔（不觸發 Engine 重載）。
    /// </summary>
    public async Task<ModbusPointsUpdateResult> UpdatePointsAsync(string szCoordinatorName, List<ModbusPointDto> newPoints)
    {
        var result = new ModbusPointsUpdateResult();

        var szFilePath = _io.ResolveConfigFilePath(SourceConfigFileIo.MODBUS_SECTION, szCoordinatorName);
        if (szFilePath == null || !File.Exists(szFilePath))
        {
            result.nError = ModbusPointsUpdateError.FileNotFound;
            return result;
        }

        await _writeLock.WaitAsync();
        try
        {
            // 重讀原檔 — 以檔案現況為準驗證結構
            var (szJson, encoding) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szFilePath);
            var root = JsonNode.Parse(szJson);
            if (root == null || root["Tags"] is not JsonArray tags)
            {
                result.nError = ModbusPointsUpdateError.FileNotFound;
                return result;
            }

            // 結構鎖：點位數量必須一致（禁止增刪與排序）
            if (newPoints.Count != tags.Count)
            {
                result.nError = ModbusPointsUpdateError.StructureChanged;
                return result;
            }

            // 逐點驗證 + 計算變更
            for (int i = 0; i < tags.Count; i++)
            {
                var tag = tags[i]!;
                var p = newPoints[i];

                var szError = ValidatePointFields(p);
                if (szError != null)
                {
                    result.nError = ModbusPointsUpdateError.InvalidPoint;
                    result.nInvalidRow = i + 1;
                    result.szInvalidReason = szError;
                    return result;
                }

                var szSummary = BuildChangeSummary(tag, p);
                if (szSummary.Length > 0)
                {
                    result.changes.Add(new ModbusPointChange
                    {
                        nTagIndex = i,
                        szPointName = p.Name.Trim(),
                        szSummary = szSummary,
                    });
                }
            }

            // 無變更 → 不寫檔、不觸發重載
            if (result.changes.Count == 0)
            {
                result.isSuccess = true;
                return result;
            }

            // 套用變更（只動 Tags[i] 的可編輯欄位，其他內容原樣保留）
            foreach (var change in result.changes)
            {
                var tag = tags[change.nTagIndex]!;
                var p = newPoints[change.nTagIndex];
                tag["Name"] = p.Name.Trim();
                tag["Address"] = p.Address.Trim();
                tag["DataType"] = (p.DataType ?? string.Empty).Trim();
                tag["Ratio"] = (p.Ratio ?? "1").Trim();
                tag["Unit"] = (p.Unit ?? string.Empty).Trim();
                tag["Min"] = (p.Min ?? string.Empty).Trim();
                tag["Max"] = (p.Max ?? string.Empty).Trim();
                tag["Device"] = (p.Device ?? string.Empty).Trim();
            }

            // 記錄網頁修改時間：之後拿「修改前匯出的舊 Excel」重匯時，預覽會警告「會蓋掉網頁修改」
            if (root is JsonObject rootObj)
                SourceJsonStamp.StampWebEdited(rootObj);

            var szNewJson = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            SourceConfigFileIo.AtomicWrite(szFilePath, szNewJson, encoding);
            _io.MirrorWrite(SourceConfigFileIo.MODBUS_SECTION, szCoordinatorName, szNewJson, encoding);

            result.isSuccess = true;
            _logger.LogInformation("Modbus 點位設定已更新: {File}, 變更 {Count} 點", szFilePath, result.changes.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 Modbus 點位設定失敗: {Name}", szCoordinatorName);
            result.nError = ModbusPointsUpdateError.WriteFailed;
            result.changes.Clear();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Excel 工作表名稱禁用字元（工作表名 = JSON 檔名 = 設備名稱）</summary>
    private static readonly char[] _excelSheetForbiddenChars = { ':', '\\', '/', '?', '*', '[', ']' };

    /// <summary>Excel 工作表名稱長度上限</summary>
    public const int COORDINATOR_NAME_MAX_LENGTH = 31;

    /// <summary>
    /// 新設備名稱檢核（不含重名，重名需讀檔與 DB）：非空、檔名與 Excel 工作表皆合法、≤ 31 字、與原名不同（不分大小寫）
    /// </summary>
    public static ModbusRenameError ValidateNewName(string szOldName, string? szNewName)
    {
        if (string.IsNullOrWhiteSpace(szNewName))
            return ModbusRenameError.Empty;
        if (!SourceConfigFileIo.IsSafeName(szNewName) || SourceConfigFileIo.HasEdgeWhitespace(szNewName)
            || szNewName.IndexOfAny(_excelSheetForbiddenChars) >= 0)
            return ModbusRenameError.InvalidChars;
        if (szNewName.Length > COORDINATOR_NAME_MAX_LENGTH)
            return ModbusRenameError.TooLong;
        if (string.Equals(szOldName, szNewName, StringComparison.OrdinalIgnoreCase))
            return ModbusRenameError.Unchanged;
        return ModbusRenameError.None;
    }

    /// <summary>
    /// Coordinator 改名（plan 2026-10-29 決策 4：DB 先、檔案後，Id 永不變 → SID / 歷史 / 警報 / 畫面綁定不斷）：
    /// 寫鎖內 ① 檢核與重名（JSON + DB，不分大小寫）② DB UPDATE Name WHERE Id（Engine 尚未載入者無 DB 列則略過）
    /// ③ 原子改檔名 X.json → Y.json（Engine watcher 收到 Renamed：停舊名迴圈、以新名載入 → SaveCoordinatorAsync 以新名命中原 Id）
    /// ④ 新檔蓋 WebEditedAt + 記 RenamedFrom（同編碼原子寫）⑤ 鏡像改名。③ 失敗回滾 DB 名稱。
    /// DB 改名與改檔名之間只隔一次 File.Move，Engine 不會有機會以舊名重新 INSERT 出孤兒列。
    /// </summary>
    public async Task<ModbusRenameResult> RenameAsync(string szOldName, string szNewName, IDataRepository repository)
    {
        var result = new ModbusRenameResult();

        result.nError = ValidateNewName(szOldName, szNewName);
        if (result.nError != ModbusRenameError.None)
            return result;

        var szOldPath = _io.ResolveConfigFilePath(SourceConfigFileIo.MODBUS_SECTION, szOldName);
        var szNewPath = _io.ResolveConfigFilePath(SourceConfigFileIo.MODBUS_SECTION, szNewName);
        if (szOldPath == null || !File.Exists(szOldPath))
        {
            result.nError = ModbusRenameError.FileNotFound;
            return result;
        }
        if (szNewPath == null)
        {
            result.nError = ModbusRenameError.InvalidChars;
            return result;
        }

        await _writeLock.WaitAsync();
        try
        {
            // 重名：資料夾既有 JSON 或 DB 既有 Coordinator（不分大小寫；Windows 檔名本就不分）
            var isFileTaken = File.Exists(szNewPath)
                || _io.ListConfigNames(SourceConfigFileIo.MODBUS_SECTION).Any(n => string.Equals(n, szNewName, StringComparison.OrdinalIgnoreCase));
            var coordinators = (await repository.GetAllCoordinatorsAsync()).ToList();
            var target = coordinators.FirstOrDefault(c => string.Equals(c.szName, szOldName, StringComparison.Ordinal))
                         ?? coordinators.FirstOrDefault(c => string.Equals(c.szName, szOldName, StringComparison.OrdinalIgnoreCase));
            var isDbTaken = coordinators.Any(c => c.Id != target?.Id && string.Equals(c.szName, szNewName, StringComparison.OrdinalIgnoreCase));
            if (isFileTaken || isDbTaken)
            {
                result.nError = ModbusRenameError.NameTaken;
                return result;
            }

            // 先備好新內容（失敗不動任何東西）
            var (szJson, encoding) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szOldPath);
            var root = SourceConfigFileIo.ParseJsonObject(szJson);
            if (root == null)
            {
                result.nError = ModbusRenameError.WriteFailed;
                return result;
            }
            SourceJsonStamp.StampWebEdited(root);
            SourceJsonStamp.AppendRenamedFrom(root, szOldName);
            var szNewJson = root.ToJsonString(_jsonWriteOptions);

            // ② DB 改名（Id 不變）
            if (target != null)
            {
                var dbResult = await repository.RenameCoordinatorAsync(target.Id, target.szName, szNewName);
                if (dbResult != CoordinatorRenameResult.Success)
                {
                    result.nError = dbResult == CoordinatorRenameResult.NameTaken ? ModbusRenameError.NameTaken : ModbusRenameError.WriteFailed;
                    return result;
                }
            }

            // ③ 原子改檔名；失敗回滾 DB
            try
            {
                File.Move(szOldPath, szNewPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Modbus 設定檔改名失敗，回滾 DB 名稱: {Old} → {New}", szOldName, szNewName);
                if (target != null)
                    await repository.RenameCoordinatorAsync(target.Id, szNewName, target.szName);
                result.nError = ModbusRenameError.WriteFailed;
                return result;
            }

            // ④ 蓋章（內容除 WebEditedAt / RenamedFrom 外與原檔相同；失敗不影響改名本身）
            try
            {
                SourceConfigFileIo.AtomicWrite(szNewPath, szNewJson, encoding);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "改名後寫入 WebEditedAt 失敗（改名已完成）: {Path}", szNewPath);
            }

            // 舊名的原子寫備份（X.json.bak）一併清掉，避免資料夾留下看似另一台設備的殘檔
            TryDelete(szOldPath + ".bak");

            // ⑤ dev 鏡像
            _io.MirrorRename(SourceConfigFileIo.MODBUS_SECTION, szOldName, szNewName, szNewJson, encoding);

            result.isSuccess = true;
            result.nCoordinatorId = target?.Id;
            result.szModbusId = target?.szModbusID ?? root["ModbusId"]?.ToString() ?? string.Empty;
            _logger.LogInformation("Modbus 設備改名: {Old} → {New}（CoordinatorId={Id}）", szOldName, szNewName, target?.Id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modbus 設備改名失敗: {Old} → {New}", szOldName, szNewName);
            result.nError = ModbusRenameError.WriteFailed;
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 更新子站名稱（plan 2026-10-29 決策 6：JSON 為單一真相、DB 為執行期副本）：
    /// 正規化 + 數量 / 長度檢核 → 寫 JSON DeviceName（蓋 WebEditedAt，同編碼原子寫 + 鏡像）→ 寫 DB。
    /// 設定檔已移除（DB 有、JSON 無）的設備只寫 DB（維持舊行為）。名稱未變不寫檔（不觸發 Engine 重載）。
    /// </summary>
    public async Task<ModbusDeviceNameResult> UpdateDeviceNamesAsync(int nCoordinatorId, string? szDeviceName, IDataRepository repository)
    {
        var result = new ModbusDeviceNameResult();

        var coordinator = (await repository.GetAllCoordinatorsAsync()).FirstOrDefault(c => c.Id == nCoordinatorId);
        if (coordinator == null)
        {
            result.nError = ModbusDeviceNameError.NotFound;
            return result;
        }
        result.szCoordinatorName = coordinator.szName;
        result.szModbusId = coordinator.szModbusID;
        result.szOldDeviceName = coordinator.szDeviceName ?? string.Empty;

        var nStationCount = coordinator.szModbusID.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
        var szNormalized = ModbusExcelAdapter.NormalizeDeviceNameList(szDeviceName, nStationCount, out var nNameCount);
        if (nNameCount > nStationCount)
        {
            result.nError = ModbusDeviceNameError.TooMany;
            return result;
        }
        if (szNormalized.Length > ModbusExcelAdapter.DEVICE_NAME_MAX_LENGTH)
        {
            result.nError = ModbusDeviceNameError.TooLong;
            return result;
        }
        result.szDeviceName = szNormalized;

        await _writeLock.WaitAsync();
        try
        {
            var szFilePath = _io.ResolveConfigFilePath(SourceConfigFileIo.MODBUS_SECTION, coordinator.szName);
            if (szFilePath != null && File.Exists(szFilePath))
            {
                var (szJson, encoding) = await SourceConfigFileIo.ReadAllTextDetectEncodingAsync(szFilePath);
                var root = SourceConfigFileIo.ParseJsonObject(szJson);
                if (root == null)
                {
                    result.nError = ModbusDeviceNameError.WriteFailed;
                    return result;
                }

                var szCurrent = root[ModbusExcelAdapter.DEVICE_NAME_FIELD] is JsonValue v ? v.ToString() : null;
                if (!string.Equals(szCurrent, szNormalized, StringComparison.Ordinal))
                {
                    root[ModbusExcelAdapter.DEVICE_NAME_FIELD] = szNormalized;
                    SourceJsonStamp.StampWebEdited(root);
                    var szNewJson = root.ToJsonString(_jsonWriteOptions);
                    SourceConfigFileIo.AtomicWrite(szFilePath, szNewJson, encoding);
                    _io.MirrorWrite(SourceConfigFileIo.MODBUS_SECTION, coordinator.szName, szNewJson, encoding);
                }
            }

            if (!await repository.UpdateDeviceNameAsync(nCoordinatorId, szNormalized))
            {
                // JSON 已寫入：Engine 重載時會以 JSON 值同步 DB，這裡回報失敗讓使用者知道頁面顯示可能暫時落後
                result.nError = ModbusDeviceNameError.WriteFailed;
                return result;
            }

            result.isSuccess = true;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新子站名稱失敗: CoordinatorId={Id}", nCoordinatorId);
            result.nError = ModbusDeviceNameError.WriteFailed;
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly JsonSerializerOptions _jsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void TryDelete(string szPath)
    {
        try
        {
            if (File.Exists(szPath)) File.Delete(szPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "刪除殘檔失敗（忽略）: {Path}", szPath);
        }
    }

    /// <summary>
    /// 驗證 Modbus 位址格式 — 與 Engine ModbusTagModel.ParseAddress 相同：
    /// 5 位數慣例（0xxxx Coil 1-9999、1xxxx Discrete、3xxxx Input、4xxxx Holding）
    /// 或 6 位數擴充慣例（000001-065536 / 1xxxxx / 3xxxxx / 4xxxxx，offset 上限 65535）。
    /// 兩慣例數值範圍重疊但意義不同，靠字串長度（含前導 0）區分。
    /// </summary>
    public static bool IsValidAddress(string? szAddress)
    {
        if (string.IsNullOrWhiteSpace(szAddress))
            return false;

        var sz = szAddress.Trim();
        if (sz.Length > 6 || !sz.All(char.IsDigit) || !int.TryParse(sz, out var n))
            return false;

        if (sz.Length == 6)
        {
            return (n >= 1 && n <= 65536)
                || (n >= 100001 && n <= 165536)
                || (n >= 300001 && n <= 365536)
                || (n >= 400001 && n <= 465536);
        }

        return (n >= 1 && n <= 9999)
            || (n >= 10000 && n <= 19999)
            || (n >= 30000 && n <= 39999)
            || (n >= 40000 && n <= 49999);
    }

    /// <summary>
    /// 位址是否為暫存器（Holding 4xxxx / Input 3xxxx）— BIT0–BIT15 型別只能配暫存器位址，
    /// Coil / Discrete 配 BIT 會被 Engine 載入時跳過（進而造成後續 SID 位移），須在設定端擋下。
    /// 呼叫前應先通過 <see cref="IsValidAddress"/>。
    /// </summary>
    public static bool IsRegisterAddress(string? szAddress)
    {
        if (!IsValidAddress(szAddress)) return false;
        var sz = szAddress!.Trim();
        var n = int.Parse(sz);
        if (sz.Length == 6)
            return (n >= 300001 && n <= 365536) || (n >= 400001 && n <= 465536);
        return (n >= 30000 && n <= 39999) || (n >= 40000 && n <= 49999);
    }

    /// <summary>驗證單點可編輯欄位，回傳 null 表示合法，否則回傳原因（技術描述）</summary>
    private static string? ValidatePointFields(ModbusPointDto p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            return "Name is required";

        if (!IsValidAddress(p.Address))
            return "invalid Address";

        if (!SupportedDataTypes.Contains((p.DataType ?? string.Empty).Trim().ToUpperInvariant()))
            return "unsupported DataType";

        if (!float.TryParse((p.Ratio ?? "1").Trim(), out _))
            return "Ratio must be numeric";

        if (!string.IsNullOrWhiteSpace(p.Min) && !float.TryParse(p.Min.Trim(), out _))
            return "Min must be numeric";

        if (!string.IsNullOrWhiteSpace(p.Max) && !float.TryParse(p.Max.Trim(), out _))
            return "Max must be numeric";

        return null;
    }

    /// <summary>比對舊(檔案) / 新(請求) 欄位，產出「欄位: 舊 → 新」摘要；無變更回傳空字串</summary>
    private static string BuildChangeSummary(JsonNode tag, ModbusPointDto p)
    {
        var aDiffs = new List<string>();

        void Compare(string szField, string szNewValue)
        {
            var szOld = tag[szField]?.ToString() ?? string.Empty;
            if (!string.Equals(szOld, szNewValue, StringComparison.Ordinal))
                aDiffs.Add($"{szField}: {szOld} → {szNewValue}");
        }

        Compare("Name", p.Name.Trim());
        Compare("Address", p.Address.Trim());
        Compare("DataType", (p.DataType ?? string.Empty).Trim());
        Compare("Ratio", (p.Ratio ?? "1").Trim());
        Compare("Unit", (p.Unit ?? string.Empty).Trim());
        Compare("Min", (p.Min ?? string.Empty).Trim());
        Compare("Max", (p.Max ?? string.Empty).Trim());
        Compare("Device", (p.Device ?? string.Empty).Trim());

        return string.Join(", ", aDiffs);
    }
}
