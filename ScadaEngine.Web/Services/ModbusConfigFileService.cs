using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScadaEngine.Engine.Communication.Modbus.Models;
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
