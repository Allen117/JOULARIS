using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 簡訊傳輸抽象 — 路由層（SmsNotificationService）與硬體層解耦。
/// 目前唯一實作 SmsModemClient（序列埠 GSM/4G Modem，標準 AT 指令）；
/// 未來若要接 HTTP 型簡訊閘道，新增實作即可，不動路由層。
/// </summary>
public interface ISmsTransport
{
    Task InitializeAsync(SmsSettingModel setting);

    /// <summary>發送一封簡訊（內部序列化，一次只送一封）</summary>
    Task<SmsSendResult> SendAsync(string szPhoneNumber, string szText);

    /// <summary>強制重新掃描/連線簡訊盒（MQTT rescan 命令用）</summary>
    Task<bool> RescanAsync();

    /// <summary>
    /// 執行期換設定（Web 存 SmsSetting.json 後經 MQTT 熱重載）：關閉舊 port → 依新設定重新偵測；
    /// 新設定為停用則只關 port、停健康檢查。與 <see cref="RescanAsync"/> 共用同一把 lock，不會跟發送中途打架。
    /// </summary>
    Task<bool> ReloadAsync(SmsSettingModel setting);

    SmsModemStatus GetStatus();

    /// <summary>狀態變化時觸發（連線/斷線/訊號更新），供 MQTT 狀態發布</summary>
    event Action<SmsModemStatus>? StatusChanged;
}

public class SmsSendResult
{
    public bool isSuccess { get; set; }
    public string szError { get; set; } = string.Empty;

    public static SmsSendResult Ok() => new() { isSuccess = true };
    public static SmsSendResult Fail(string szError) => new() { isSuccess = false, szError = szError };
}

/// <summary>簡訊盒即時狀態快照（發布至 SCADA/Sys/Sms/Status）</summary>
public class SmsModemStatus
{
    public bool isConnected { get; set; }
    public string szPort { get; set; } = string.Empty;
    public int nBaudRate { get; set; }

    /// <summary>AT+CSQ 的 rssi 值：0~31（越大越好），-1 = 未知/未連線</summary>
    public int nSignalCsq { get; set; } = -1;

    /// <summary>SIM 狀態：READY / SIM PIN / NOT_INSERTED / UNKNOWN</summary>
    public string szSimStatus { get; set; } = "UNKNOWN";

    public string szLastError { get; set; } = string.Empty;
    public DateTime dtLastUpdated { get; set; } = DateTime.Now;

    public SmsModemStatus Clone() => (SmsModemStatus)MemberwiseClone();
}
