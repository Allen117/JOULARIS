namespace ScadaEngine.Web.Features.ModbusCoordinator.Models;

public class UpdateDeviceNameRequest
{
    public int Id { get; set; }
    public string? DeviceName { get; set; }
}

/// <summary>Coordinator（設備名稱 = JSON 檔名）改名請求</summary>
public class RenameCoordinatorRequest
{
    public string? OldName { get; set; }
    public string? NewName { get; set; }
}

public enum ModbusRenameError
{
    None = 0,
    /// <summary>新名稱空白</summary>
    Empty,
    /// <summary>含檔名非法字元或 Excel 工作表禁用字元（: \ / ? * [ ]）、或含「..」</summary>
    InvalidChars,
    /// <summary>超過 Excel 工作表名稱上限 31 字</summary>
    TooLong,
    /// <summary>與原名相同（不分大小寫）</summary>
    Unchanged,
    /// <summary>與既有 Coordinator / JSON 重名（不分大小寫）</summary>
    NameTaken,
    /// <summary>原設定檔不存在</summary>
    FileNotFound,
    /// <summary>寫檔 / DB 失敗（已回滾）</summary>
    WriteFailed,
}

public class ModbusRenameResult
{
    public bool isSuccess { get; set; }
    public ModbusRenameError nError { get; set; }

    /// <summary>DB 中的 Coordinator Id（Engine 尚未載入的設定檔為 null）</summary>
    public int? nCoordinatorId { get; set; }

    /// <summary>Coordinator 站號清單（EventLog SID 用）</summary>
    public string szModbusId { get; set; } = string.Empty;
}

public enum ModbusDeviceNameError
{
    None = 0,
    /// <summary>名稱數量多於站號數</summary>
    TooMany,
    /// <summary>超過 DB 欄位長度</summary>
    TooLong,
    /// <summary>找不到 Coordinator</summary>
    NotFound,
    WriteFailed,
}

public class ModbusDeviceNameResult
{
    public bool isSuccess { get; set; }
    public ModbusDeviceNameError nError { get; set; }

    /// <summary>實際寫入的正規化名稱串</summary>
    public string szDeviceName { get; set; } = string.Empty;

    /// <summary>變更前名稱串（稽核用）</summary>
    public string szOldDeviceName { get; set; } = string.Empty;

    public string szCoordinatorName { get; set; } = string.Empty;
    public string szModbusId { get; set; } = string.Empty;
}
