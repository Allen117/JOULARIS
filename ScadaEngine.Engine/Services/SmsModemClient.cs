using System.IO.Ports;
using System.Text;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 序列埠 GSM/4G 簡訊盒通訊封裝 — 只用 3GPP TS 27.005 標準 AT 指令，不綁廠牌。
/// 設計重點：
///   1. ComPort="auto" 時自動掃描：逐 port × baud（115200→9600）送 AT probe，
///      再以 AT+CPIN? 確認是插了 SIM 的 modem（USB modem 常虛擬多個 port，只有 probe 能分辨）
///   2. 序列埠為獨占資源：Engine 全程持有，所有操作以 SemaphoreSlim 序列化（一次一封）
///   3. 發送流程：AT+CMGS="UCS2號碼" → 等 '>' → UCS2 內文 + Ctrl+Z → 等 +CMGS/OK
///   4. 連續失敗 2 次或 port 消失 → 標記斷線，下次發送自動重掃
///   5. 每 60 秒健康檢查（AT+CSQ / AT+CPIN?），狀態變化經 StatusChanged 事件發布 MQTT
/// </summary>
public class SmsModemClient : ISmsTransport, IDisposable
{
    private static readonly int[] c_baudCandidates = { 115200, 9600 };

    private readonly ILogger<SmsModemClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _healthTimer;

    private SmsSettingModel _setting = new();
    private SerialPort? _port;
    private int _nConsecutiveFailures = 0;
    private readonly SmsModemStatus _status = new();

    public event Action<SmsModemStatus>? StatusChanged;

    public SmsModemClient(ILogger<SmsModemClient> logger)
    {
        _logger = logger;
        _healthTimer = new Timer(async _ => await HealthCheckAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync(SmsSettingModel setting)
    {
        _setting = setting ?? new SmsSettingModel();
        if (!_setting.EnableNotification)
        {
            _logger.LogInformation("簡訊通知未啟用，略過簡訊盒連線");
            return;
        }

        await _lock.WaitAsync();
        try { ConnectLocked(); }
        finally { _lock.Release(); }

        _healthTimer.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public SmsModemStatus GetStatus()
    {
        lock (_status) return _status.Clone();
    }

    public async Task<bool> RescanAsync()
    {
        await _lock.WaitAsync();
        try
        {
            ClosePortLocked();
            return ConnectLocked();
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> ReloadAsync(SmsSettingModel setting)
    {
        await _lock.WaitAsync();
        try
        {
            _setting = setting ?? new SmsSettingModel();
            ClosePortLocked();

            if (!_setting.EnableNotification)
            {
                _healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
                UpdateStatus(s => { s.isConnected = false; s.szPort = string.Empty; s.szLastError = string.Empty; });
                _logger.LogInformation("簡訊設定重載：通知已停用，簡訊盒 port 已關閉");
                return true;
            }

            var isConnected = ConnectLocked();
            _healthTimer.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            _logger.LogInformation("簡訊設定重載：ComPort={Port}, BaudRate={Baud}, 連線={Connected}",
                _setting.ComPort, _setting.BaudRate, isConnected);
            return isConnected;
        }
        finally { _lock.Release(); }
    }

    public async Task<SmsSendResult> SendAsync(string szPhoneNumber, string szText)
    {
        if (string.IsNullOrWhiteSpace(szPhoneNumber))
            return SmsSendResult.Fail("電話號碼為空");

        await _lock.WaitAsync();
        try
        {
            if (_port == null || !_port.IsOpen)
            {
                if (!ConnectLocked())
                    return SmsSendResult.Fail($"簡訊盒未連線: {_status.szLastError}");
            }

            var result = SendLocked(szPhoneNumber, szText);

            if (result.isSuccess)
            {
                _nConsecutiveFailures = 0;
            }
            else
            {
                _nConsecutiveFailures++;
                _logger.LogWarning("簡訊發送失敗 ({Fail} 連續): {Phone}, {Error}",
                    _nConsecutiveFailures, szPhoneNumber, result.szError);
                if (_nConsecutiveFailures >= 2)
                {
                    // 連續失敗視為 modem 異常（拔除/當機），斷線待下次發送重掃
                    ClosePortLocked();
                    UpdateStatus(s => { s.isConnected = false; s.szLastError = result.szError; });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "簡訊發送未預期例外: {Phone}", szPhoneNumber);
            ClosePortLocked();
            UpdateStatus(s => { s.isConnected = false; s.szLastError = ex.Message; });
            return SmsSendResult.Fail(ex.Message);
        }
        finally { _lock.Release(); }
    }

    // ── 連線 / 掃描（呼叫端須已持有 _lock）──

    private bool ConnectLocked()
    {
        var ports = _setting.ComPort.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? SerialPort.GetPortNames().Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { _setting.ComPort };

        var bauds = _setting.BaudRate > 0 ? new[] { _setting.BaudRate } : c_baudCandidates;

        if (ports.Length == 0)
        {
            UpdateStatus(s => { s.isConnected = false; s.szLastError = "系統無任何 COM port"; });
            _logger.LogWarning("簡訊盒掃描失敗：系統無任何 COM port");
            return false;
        }

        foreach (var szPort in ports)
        {
            foreach (var nBaud in bauds)
            {
                if (TryProbePortLocked(szPort, nBaud))
                {
                    _logger.LogInformation("簡訊盒連線成功: {Port} @ {Baud}, SIM={Sim}, CSQ={Csq}",
                        szPort, nBaud, _status.szSimStatus, _status.nSignalCsq);
                    return true;
                }
            }
        }

        UpdateStatus(s => { s.isConnected = false; s.szLastError = "掃描完所有 COM port，未找到可用的簡訊盒"; });
        _logger.LogWarning("簡訊盒掃描失敗：候選 port [{Ports}] 皆無標準 AT modem 回應", string.Join(",", ports));
        return false;
    }

    private bool TryProbePortLocked(string szPortName, int nBaud)
    {
        SerialPort? port = null;
        try
        {
            port = new SerialPort(szPortName, nBaud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 2000,
                NewLine = "\r\n",
                Encoding = Encoding.ASCII,
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            // 基本 AT 對答（有些 modem 剛開 port 需要一點時間）
            var szResp = SendCommand(port, "AT", TimeSpan.FromSeconds(2));
            if (!SmsAtHelper.IsSuccessResponse(szResp))
            {
                port.Dispose();
                return false;
            }

            SendCommand(port, "ATE0", TimeSpan.FromSeconds(2)); // 關 echo，簡化回應解析

            // 確認是插了 SIM 的 modem（排除 USB modem 的 diagnostic / NMEA port）
            var szCpinResp = SendCommand(port, "AT+CPIN?", TimeSpan.FromSeconds(3));
            var szSim = SmsAtHelper.ParseCpin(szCpinResp);

            if (szSim.Equals("SIM PIN", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_setting.SimPin))
            {
                SendCommand(port, $"AT+CPIN=\"{_setting.SimPin}\"", TimeSpan.FromSeconds(5));
                Thread.Sleep(2000); // SIM 解鎖後需要時間註冊
                szCpinResp = SendCommand(port, "AT+CPIN?", TimeSpan.FromSeconds(3));
                szSim = SmsAtHelper.ParseCpin(szCpinResp);
            }

            if (!szSim.Equals("READY", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Port {Port} 有 AT 回應但 SIM 非 READY ({Sim})，跳過", szPortName, szSim);
                UpdateStatus(s => { s.szSimStatus = szSim; s.szLastError = $"SIM 狀態: {szSim}"; });
                port.Dispose();
                return false;
            }

            // 設定文字模式 + UCS2 字集（中文必要）
            if (!SmsAtHelper.IsSuccessResponse(SendCommand(port, "AT+CMGF=1", TimeSpan.FromSeconds(2))) ||
                !SmsAtHelper.IsSuccessResponse(SendCommand(port, "AT+CSCS=\"UCS2\"", TimeSpan.FromSeconds(2))))
            {
                _logger.LogWarning("Port {Port} 不支援文字模式或 UCS2 字集，跳過", szPortName);
                port.Dispose();
                return false;
            }

            int nCsq = SmsAtHelper.ParseCsq(SendCommand(port, "AT+CSQ", TimeSpan.FromSeconds(2)));

            _port = port;
            _nConsecutiveFailures = 0;
            UpdateStatus(s =>
            {
                s.isConnected = true;
                s.szPort = szPortName;
                s.nBaudRate = nBaud;
                s.szSimStatus = "READY";
                s.nSignalCsq = nCsq;
                s.szLastError = string.Empty;
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Probe {Port}@{Baud} 失敗: {Msg}", szPortName, nBaud, ex.Message);
            try { port?.Dispose(); } catch { /* port 已損毀 */ }
            return false;
        }
    }

    // ── 發送（呼叫端須已持有 _lock，且 _port 已開）──

    private SmsSendResult SendLocked(string szPhoneNumber, string szText)
    {
        var port = _port!;
        port.DiscardInBuffer();

        var szTruncated = SmsAtHelper.TruncateForSms(szText);
        var szNumberHex = SmsAtHelper.EncodeUcs2Hex(szPhoneNumber);
        var szBodyHex = SmsAtHelper.EncodeUcs2Hex(szTruncated);

        // 步驟 1：AT+CMGS="<UCS2 號碼>" → 等 '>' 提示
        port.Write($"AT+CMGS=\"{szNumberHex}\"\r");
        var szPrompt = ReadUntil(port, buf => buf.Contains('>') || buf.Contains("ERROR"), TimeSpan.FromSeconds(10));
        if (!szPrompt.Contains('>'))
        {
            // 送 ESC 取消未完成的 CMGS，避免 modem 卡在輸入模式
            try { port.Write(new byte[] { 0x1B }, 0, 1); } catch { /* 已斷線 */ }
            return SmsSendResult.Fail($"CMGS 未取得輸入提示: {SmsAtHelper.ExtractError(szPrompt)}");
        }

        // 步驟 2：UCS2 內文 + Ctrl+Z（0x1A）→ 等 +CMGS / OK（網路端最長可到 30 秒）
        port.Write(szBodyHex);
        port.Write(new byte[] { 0x1A }, 0, 1);
        var szResp = ReadUntil(port, SmsAtHelper.IsFinalResponse, TimeSpan.FromSeconds(30));

        if (SmsAtHelper.IsSuccessResponse(szResp) && szResp.Contains("+CMGS"))
        {
            _logger.LogDebug("簡訊發送成功: {Phone}, {Len} 字", szPhoneNumber, szTruncated.Length);
            return SmsSendResult.Ok();
        }
        return SmsSendResult.Fail(SmsAtHelper.ExtractError(szResp));
    }

    // ── 健康檢查（60 秒；發送中則跳過本輪）──

    private async Task HealthCheckAsync()
    {
        if (!_setting.EnableNotification) return;
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            if (_port == null || !_port.IsOpen)
            {
                // 斷線狀態下每輪嘗試重連（含 USB 重新插回的情境）
                ConnectLocked();
                return;
            }

            var szCsqResp = SendCommand(_port, "AT+CSQ", TimeSpan.FromSeconds(2));
            if (!SmsAtHelper.IsFinalResponse(szCsqResp))
            {
                _logger.LogWarning("簡訊盒健康檢查無回應，標記斷線待重掃");
                ClosePortLocked();
                UpdateStatus(s => { s.isConnected = false; s.szLastError = "健康檢查無回應"; });
                return;
            }
            int nCsq = SmsAtHelper.ParseCsq(szCsqResp);
            var szSim = SmsAtHelper.ParseCpin(SendCommand(_port, "AT+CPIN?", TimeSpan.FromSeconds(3)));
            UpdateStatus(s => { s.nSignalCsq = nCsq; s.szSimStatus = szSim; });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "簡訊盒健康檢查例外，標記斷線");
            ClosePortLocked();
            UpdateStatus(s => { s.isConnected = false; s.szLastError = ex.Message; });
        }
        finally { _lock.Release(); }
    }

    // ── 序列埠低階工具 ──

    private static string SendCommand(SerialPort port, string szCommand, TimeSpan timeout)
    {
        port.DiscardInBuffer();
        port.Write(szCommand + "\r");
        return ReadUntil(port, SmsAtHelper.IsFinalResponse, timeout);
    }

    private static string ReadUntil(SerialPort port, Func<string, bool> isComplete, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        var dtDeadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < dtDeadline)
        {
            try
            {
                if (port.BytesToRead > 0)
                {
                    sb.Append(port.ReadExisting());
                    if (isComplete(sb.ToString()))
                        return sb.ToString();
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
            catch (TimeoutException) { /* 繼續等到 deadline */ }
        }
        return sb.ToString();
    }

    private void ClosePortLocked()
    {
        try { _port?.Dispose(); } catch { /* port 可能已被系統移除 */ }
        _port = null;
    }

    private void UpdateStatus(Action<SmsModemStatus> mutate)
    {
        SmsModemStatus snapshot;
        lock (_status)
        {
            mutate(_status);
            _status.dtLastUpdated = DateTime.Now;
            snapshot = _status.Clone();
        }
        try { StatusChanged?.Invoke(snapshot); }
        catch (Exception ex) { _logger.LogError(ex, "SMS 狀態變化事件處理失敗"); }
    }

    public void Dispose()
    {
        _healthTimer.Dispose();
        try { _port?.Dispose(); } catch { /* ignore */ }
    }
}
