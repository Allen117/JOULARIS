using System.ComponentModel.DataAnnotations;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Communication.Modbus.Models;

/// <summary>
/// Modbus 點位標籤資料模型，封裝點位的完整屬性與物理量轉換邏輯
/// </summary>
public class ModbusTagModel
{
    /// <summary>
    /// 點位名稱代碼 (如 CH1RUN)
    /// </summary>
    public string szName { get; set; } = string.Empty;

    /// <summary>
    /// Modbus 暫存器地址 (5位數慣例格式如 40001；或 6位數擴充慣例如 430001，供 offset > 9999 的設備使用)
    /// </summary>
    public string szAddress { get; set; } = string.Empty;

    /// <summary>
    /// 資料型態 (Integer, FloatingPt, SwappedFP, Double)
    /// </summary>
    public string szDataType { get; set; } = string.Empty;

    /// <summary>
    /// 數值縮放比例，實際值 = 原始值 × Ratio
    /// </summary>
    public string szRatio { get; set; } = "1";

    /// <summary>
    /// 物理單位 (如 C, F, V, A)
    /// </summary>
    public string szUnit { get; set; } = string.Empty;

    /// <summary>
    /// 控制點位最大值
    /// </summary>
    public string szMax { get; set; } = string.Empty;

    /// <summary>
    /// 控制點位最小值
    /// </summary>
    public string szMin { get; set; } = string.Empty;

    /// <summary>
    /// 站號內子設備分群名稱（Tag.Device，optional）。一顆 PLC（單一站號）內放多台設備時用來分群；
    /// 留白代表未分群（見 plan 決策 5 留白必須）。多站號 Coordinator 會忽略此欄（決策 4）。
    /// </summary>
    public string szDevice { get; set; } = string.Empty;

    /// <summary>
    /// 點位唯一識別碼 (SID)，格式為 XXX-SN
    /// </summary>
    public string szSID { get; set; } = string.Empty;

    /// <summary>
    /// 解析後的實際 Modbus 地址 (0-based，已扣除前綴)
    /// </summary>
    public int nParsedAddress { get; private set; }

    /// <summary>
    /// 解析後的縮放比例
    /// </summary>
    public float fRatio { get; private set; } = 1.0f;

    /// <summary>
    /// Modbus 功能碼 (03=Holding, 04=Input, 01=Coil, 02=Discrete)
    /// </summary>
    public byte nFunctionCode { get; private set; }

    /// <summary>
    /// 暫存器數量 (根據資料型態決定)
    /// </summary>
    public int nRegisterCount { get; private set; } = 1;

    /// <summary>
    /// 最近一次 Validate() 的語意檢查失敗原因（如 BIT 型別配到 Coil 位址），成功或格式性失敗時為 null。
    /// 由持有 logger 的呼叫端取用寫 log。
    /// </summary>
    public string? szValidationError { get; private set; }

    /// <summary>
    /// single (float) 能逐一表達整數的上限 2^23。DEC10K3 整數部分超過此值後 0.0001 的小數解析度完全消失。
    /// </summary>
    private const double SINGLE_EXACT_INT_LIMIT = 8388608.0;

    /// <summary>尚未被取走的解碼警告訊息</summary>
    private string? _szPendingDecodeWarning;

    /// <summary>已回報過的警告種類 — 同一點位同一種問題只回報一次，避免採集迴圈刷 log</summary>
    private readonly HashSet<string> _reportedWarningKinds = new();

    /// <summary>
    /// Engine 支援的資料型態白名單（大寫正規形）。
    /// 這是唯一真相來源 — ModbusPointModel.Validate 與 Web 的 ModbusConfigFileService.SupportedDataTypes
    /// 都引用此清單，避免新增型別時漏改其中一處。
    /// </summary>
    public static readonly string[] SupportedDataTypes = BuildSupportedDataTypes();

    private static string[] BuildSupportedDataTypes()
    {
        var typeList = new List<string>
        {
            "INTEGER", "UINTEGER", "FLOATINGPT", "SWAPPEDFP", "DOUBLE", "SWAPPEDDOUBLE", "UINT32BE",
            "DEC10K3", "BCD"
        };

        // BIT0–BIT15：位元索引寫在型別字串裡（不動 Address 格式，詳見 docs/功能說明書_Engine核心.md）
        for (var i = 0; i <= 15; i++)
            typeList.Add($"BIT{i}");

        return typeList.ToArray();
    }

    /// <summary>
    /// 解析 BIT0–BIT15 型別字串的位元索引
    /// </summary>
    /// <param name="szDataType">資料型態字串</param>
    /// <param name="nBitIndex">解析出的位元索引 (0–15)，失敗時為 -1</param>
    /// <returns>是 BIT 型別且索引合法回傳 true</returns>
    public static bool TryParseBitIndex(string? szDataType, out int nBitIndex)
    {
        nBitIndex = -1;

        if (string.IsNullOrWhiteSpace(szDataType))
            return false;

        var szUpper = szDataType.Trim().ToUpperInvariant();
        if (!szUpper.StartsWith("BIT") || szUpper.Length < 4 || szUpper.Length > 5)
            return false;

        var szIndex = szUpper.Substring(3);
        if (!szIndex.All(char.IsAsciiDigit))
            return false;

        var nParsed = int.Parse(szIndex);
        if (nParsed < 0 || nParsed > 15)
            return false;

        nBitIndex = nParsed;
        return true;
    }

    /// <summary>
    /// 取出並清除待回報的解碼警告訊息，無警告回傳 null（由持有 logger 的呼叫端寫 log）
    /// </summary>
    public string? TakeDecodeWarning()
    {
        var szWarning = _szPendingDecodeWarning;
        _szPendingDecodeWarning = null;
        return szWarning;
    }

    /// <summary>
    /// 登記一則解碼警告 — 同一 kind 在此點位生命週期內只回報一次
    /// </summary>
    private void RaiseDecodeWarningOnce(string szKind, string szMessage)
    {
        if (_reportedWarningKinds.Add(szKind))
            _szPendingDecodeWarning = szMessage;
    }

    /// <summary>
    /// 解析地址格式並設定功能碼與實際地址
    /// </summary>
    /// <returns>解析成功回傳 true，失敗回傳 false</returns>
    public bool ParseAddress()
    {
        if (string.IsNullOrEmpty(szAddress))
            return false;

        try
        {
            var szTrimmed = szAddress.Trim();
            var nFullAddress = int.Parse(szTrimmed);

            // 6位數擴充慣例（供 offset > 9999 的設備）：與 5位數慣例的數值範圍重疊但意義不同
            // （如 045000 = Coil offset 44999，45000 = Holding offset 4999），只能靠字串長度（含前導 0）區分
            if (szTrimmed.Length == 6)
            {
                if (nFullAddress >= 400001 && nFullAddress <= 465536)
                {
                    // 4xxxxx: Holding Registers (Function Code 03)
                    nFunctionCode = 3;
                    nParsedAddress = nFullAddress - 400001; // 0-based
                }
                else if (nFullAddress >= 300001 && nFullAddress <= 365536)
                {
                    // 3xxxxx: Input Registers (Function Code 04)
                    nFunctionCode = 4;
                    nParsedAddress = nFullAddress - 300001; // 0-based
                }
                else if (nFullAddress >= 100001 && nFullAddress <= 165536)
                {
                    // 1xxxxx: Discrete Inputs (Function Code 02)
                    nFunctionCode = 2;
                    nParsedAddress = nFullAddress - 100001; // 0-based
                }
                else if (nFullAddress >= 1 && nFullAddress <= 65536)
                {
                    // 0xxxxx: Coils (Function Code 01)
                    nFunctionCode = 1;
                    nParsedAddress = nFullAddress - 1; // 0-based
                }
                else
                {
                    return false;
                }

                return true;
            }

            if (szTrimmed.Length > 6)
                return false;

            // 解析 5位數慣例地址格式
            if (nFullAddress >= 40000 && nFullAddress <= 49999)
            {
                // 4xxxx: Holding Registers (Function Code 03)
                nFunctionCode = 3;
                nParsedAddress = nFullAddress - 40001; // 0-based
            }
            else if (nFullAddress >= 30000 && nFullAddress <= 39999)
            {
                // 3xxxx: Input Registers (Function Code 04)
                nFunctionCode = 4;
                nParsedAddress = nFullAddress - 30001; // 0-based
            }
            else if (nFullAddress >= 10000 && nFullAddress <= 19999)
            {
                // 1xxxx: Discrete Inputs (Function Code 02)
                nFunctionCode = 2;
                nParsedAddress = nFullAddress - 10001; // 0-based
            }
            else if (nFullAddress >= 1 && nFullAddress <= 9999)
            {
                // 0xxxx: Coils (Function Code 01)
                nFunctionCode = 1;
                nParsedAddress = nFullAddress - 1; // 0-based
            }
            else
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析縮放比例並設定暫存器數量
    /// </summary>
    /// <returns>解析成功回傳 true，失敗回傳 false</returns>
    public bool ParseRatioAndRegisterCount()
    {
        if (!float.TryParse(szRatio, out var tempRatio))
        {
            fRatio = 1.0f;
        }
        else
        {
            fRatio = tempRatio;
        }

        // 根據資料型態設定暫存器數量
        switch (szDataType.ToUpper())
        {
            case "INTEGER":
                nRegisterCount = 1;
                break;
            case "UINTEGER":
                nRegisterCount = 1;
                break;
            case "FLOATINGPT":
                nRegisterCount = 2;
                break;
            case "SWAPPEDFP":
                nRegisterCount = 2;
                break;
            case "DOUBLE":
                nRegisterCount = 4;
                break;
            case "SWAPPEDDOUBLE":
                nRegisterCount = 4;
                break;
            case "UINT32BE":
                nRegisterCount = 2;
                break;
            case "DEC10K3":
                nRegisterCount = 3;
                break;
            case "BCD":
                nRegisterCount = 1;
                break;
            default:
                // BIT0–BIT15 與未知型別皆視為單一暫存器
                nRegisterCount = 1;
                break;
        }

        return true;
    }

    /// <summary>
    /// 根據資料型態與 Ratio 計算實際物理量
    /// </summary>
    /// <param name="rawData">原始暫存器資料</param>
    /// <returns>計算後的物理量</returns>
    public float CalculatePhysicalValue(ushort[] rawData)
    {
        if (rawData == null || rawData.Length < nRegisterCount)
            return 0.0f;

        float fRawValue = 0.0f;

        switch (szDataType.ToUpper())
        {
            case "INTEGER":
                fRawValue = (short)rawData[0]; // 有號整數
                break;
            case "UINTEGER":
                fRawValue = rawData[0]; // 無號整數
                break;
            case "FLOATINGPT":
                if (rawData.Length >= 2)
                {
                    // 對應 ModScan "Floating Pt": 其實際邏輯為 Low Word First (CDAB)
                    // 將第一個暫存器 rawData[0] 當作低位字組
                    var bytes = new byte[4];
                    bytes[0] = (byte)(rawData[0] & 0xFF);      
                    bytes[1] = (byte)(rawData[0] >> 8);         
                    bytes[2] = (byte)(rawData[1] & 0xFF);       
                    bytes[3] = (byte)(rawData[1] >> 8);         
                    fRawValue = BitConverter.ToSingle(bytes, 0);
                }
                break;
            case "SWAPPEDFP":
                if (rawData.Length >= 2)
                {
                    // 對應 ModScan "Swapped FP": 其實際邏輯為 High Word First (ABCD)
                    // 將第一個暫存器 rawData[0] 當作高位字組
                    var bytes = new byte[4];
                    bytes[0] = (byte)(rawData[1] & 0xFF);      
                    bytes[1] = (byte)(rawData[1] >> 8);      
                    bytes[2] = (byte)(rawData[0] & 0xFF);    
                    bytes[3] = (byte)(rawData[0] >> 8);       
                    fRawValue = BitConverter.ToSingle(bytes, 0);
                }
                break;
            case "DOUBLE":
                if (rawData.Length >= 4)
                {
                    // 對應 ModScan "Double": 其實際邏輯為 Low Word First (GHEFCDAB)
                    var bytes = new byte[8];
                    bytes[0] = (byte)(rawData[0] & 0xFF);       
                    bytes[1] = (byte)(rawData[0] >> 8);         
                    bytes[2] = (byte)(rawData[1] & 0xFF);       
                    bytes[3] = (byte)(rawData[1] >> 8);         
                    bytes[4] = (byte)(rawData[2] & 0xFF);       
                    bytes[5] = (byte)(rawData[2] >> 8);         
                    bytes[6] = (byte)(rawData[3] & 0xFF);       
                    bytes[7] = (byte)(rawData[3] >> 8);         
                    fRawValue = (float)BitConverter.ToDouble(bytes, 0);
                }
                break;
            case "SWAPPEDDOUBLE":
                if (rawData.Length >= 4)
                {
                    // 對應 ModScan "Swapped Double": 其實際邏輯為 High Word First (ABCDEFGH)
                    var bytes = new byte[8];
                    bytes[0] = (byte)(rawData[3] & 0xFF);
                    bytes[1] = (byte)(rawData[3] >> 8);
                    bytes[2] = (byte)(rawData[2] & 0xFF);
                    bytes[3] = (byte)(rawData[2] >> 8);
                    bytes[4] = (byte)(rawData[1] & 0xFF);
                    bytes[5] = (byte)(rawData[1] >> 8);
                    bytes[6] = (byte)(rawData[0] & 0xFF);
                    bytes[7] = (byte)(rawData[0] >> 8);
                    fRawValue = (float)BitConverter.ToDouble(bytes, 0);
                }
                break;
            case "UINT32BE":
                if (rawData.Length >= 2)
                {
                    // UINT32 Big-Endian: rawData[0] 為高位字組，rawData[1] 為低位字組 (ABCD)
                    var bytes = new byte[4];
                    bytes[0] = (byte)(rawData[1] & 0xFF);
                    bytes[1] = (byte)(rawData[1] >> 8);
                    bytes[2] = (byte)(rawData[0] & 0xFF);
                    bytes[3] = (byte)(rawData[0] >> 8);
                    fRawValue = BitConverter.ToUInt32(bytes, 0);
                }
                break;
            case "DEC10K3":
                if (rawData.Length >= 3)
                {
                    // base-10000 十進位分段（非 BCD）：每個 word 各存一段純二進位 0–9999
                    // R0 = 整數高位段 (×10000)、R1 = 整數低位段 (×1)、R2 = 小數四位 (×0.0001)
                    var dIntegerPart = rawData[0] * 10000.0 + rawData[1];

                    if (dIntegerPart > SINGLE_EXACT_INT_LIMIT)
                    {
                        RaiseDecodeWarningOnce("DEC10K3_PRECISION",
                            $"DEC10K3 點位 {szName} 整數部分 {dIntegerPart} 超過 single 可精確表達上限 {SINGLE_EXACT_INT_LIMIT}，小數位已失真");
                    }

                    // 先在 double 內合併整數與小數，最後才降階為 single，避免中間步驟額外損失精度
                    fRawValue = (float)(dIntegerPart + rawData[2] * 0.0001);
                }
                break;
            case "BCD":
                fRawValue = DecodeBcd16(rawData[0]);
                break;
            default:
                // BIT0–BIT15：取單一暫存器的指定位元，回 0 或 1
                if (TryParseBitIndex(szDataType, out var nBitIndex))
                    fRawValue = (rawData[0] >> nBitIndex) & 0x1;
                break;
        }

        return fRawValue * fRatio;
    }

    /// <summary>
    /// 16-bit BCD 解碼：4 個 nibble 各代表一位十進位數字 (0x1234 → 1234)。
    /// 任一 nibble 落在 A–F 代表設備回傳異常或型別設錯，視為資料無效回傳 0 並登記一次警告 —
    /// 硬把 0xA 當 10 累加會產生「看起來合理但錯誤」的數值，比明確回 0 更危險。
    /// </summary>
    /// <param name="nRaw">原始暫存器值</param>
    /// <returns>解碼後的十進位數值，非法 nibble 回傳 0</returns>
    private float DecodeBcd16(ushort nRaw)
    {
        var nValue = 0;
        var nWeight = 1;

        for (var i = 0; i < 4; i++)
        {
            var nNibble = (nRaw >> (i * 4)) & 0xF;

            if (nNibble > 9)
            {
                RaiseDecodeWarningOnce("BCD_INVALID_NIBBLE",
                    $"BCD 點位 {szName} 原始值 0x{nRaw:X4} 含非法 nibble (A–F)，視為資料無效回傳 0");
                return 0.0f;
            }

            nValue += nNibble * nWeight;
            nWeight *= 10;
        }

        return nValue;
    }

    /// <summary>
    /// 驗證點位設定是否有效
    /// </summary>
    /// <returns>驗證成功回傳 true</returns>
    public bool Validate()
    {
        szValidationError = null;

        if (string.IsNullOrEmpty(szName) || string.IsNullOrEmpty(szAddress))
            return false;

        if (!ParseAddress() || !ParseRatioAndRegisterCount())
            return false;

        // BIT0–BIT15 取的是「暫存器內的某個位元」，只在 Holding(4xxxx, FC3) / Input(3xxxx, FC4) 有意義。
        // Coil / Discrete 位址本身就是單一位元，再取 bit 必為設定錯誤，寧可擋下不採集。
        if (TryParseBitIndex(szDataType, out _) && nFunctionCode != 3 && nFunctionCode != 4)
        {
            szValidationError = $"{szDataType} 只能用於 Holding (4xxxx) / Input (3xxxx) 暫存器位址，" +
                                $"目前位址 {szAddress} 為 Coil/Discrete";
            return false;
        }

        return true;
    }
}

/// <summary>
/// Modbus 設備配置資料模型
/// </summary>
public class ModbusDeviceConfigModel
{
    /// <summary>
    /// Modbus 設備 IP 地址
    /// </summary>
    [Required]
    public string szIP { get; set; } = string.Empty;

    /// <summary>
    /// TCP 通訊埠號 (預設 502)
    /// </summary>
    public int nPort { get; set; } = 502;

    /// <summary>
    /// 設備站號 (Unit ID)，多個站號以逗點分隔
    /// </summary>
    public string szModbusId { get; set; } = "1";

    /// <summary>
    /// 連線逾時時間 (毫秒)
    /// </summary>
    public int nConnectTimeout { get; set; } = 1000;

    /// <summary>
    /// 資料庫配置 ID，用於 SID 動態生成
    /// </summary>
    public int nDatabaseId { get; set; } = 0;

    /// <summary>
    /// Coordinator 名稱 (對應 JSON 設定檔名稱，即 ModbusCoordinator.Name)
    /// </summary>
    public string szCoordinatorName { get; set; } = string.Empty;

    /// <summary>
    /// 採集週期 (毫秒)，從資料庫 ModbusCoordinator.DelayTime 讀取
    /// </summary>
    public int nCollectionIntervalMs { get; set; } = 1000;

    /// <summary>
    /// 站層讀取失敗（逾時 / 例外碼）同段重送次數（JSON 選填 RetryCount，預設 2；0~5）
    /// </summary>
    public int nRetryCount { get; set; } = 2;

    /// <summary>
    /// 整台 Coordinator 連續幾輪「所有站都沒回」才主動斷 TCP 重連（JSON 選填 NoResponseReconnectRounds，預設 5）
    /// </summary>
    public int nNoResponseReconnectRounds { get; set; } = 5;

    /// <summary>
    /// 已判定不可達的站號多久探測一次（毫秒；JSON 選填 UnreachableProbeIntervalMs，預設 30000，0 = 每輪都探測）。
    /// 探測只問一次、不重送；間隔內跳過不問，讓同條連線其他站不被死站拖慢
    /// </summary>
    public int nUnreachableProbeIntervalMs { get; set; } = 30000;

    /// <summary>
    /// 子站名稱逗號串（順序對應 szModbusId；JSON 選填 DeviceName）。
    /// null = JSON 沒有此欄位（舊檔）→ 載入時不動 DB；空字串 = 明確清空。
    /// </summary>
    public string? szDeviceName { get; set; }

    /// <summary>
    /// 點位標籤清單
    /// </summary>
    public List<ModbusTagModel> tagList { get; set; } = new();

    /// <summary>
    /// 解析 ModbusId 字串為陣列
    /// </summary>
    /// <returns>站號陣列</returns>
    public byte[] GetModbusIdArray()
    {
        if (string.IsNullOrEmpty(szModbusId))
            return new byte[] { 1 };

        try
        {
            return szModbusId.Split(',')
                           .Select(id => byte.Parse(id.Trim()))
                           .ToArray();
        }
        catch
        {
            return new byte[] { 1 };
        }
    }

    /// <summary>
    /// 驗證設備配置是否有效
    /// </summary>
    /// <returns>驗證成功回傳 true</returns>
    public bool Validate()
    {
        if (string.IsNullOrEmpty(szIP) || nPort <= 0 || nPort > 65535)
            return false;

        if (tagList == null || tagList.Count == 0)
            return false;

        return tagList.All(tag => tag.Validate());
    }
}