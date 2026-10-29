using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ScadaEngine.Engine.Communication.Modbus.Services;

/// <summary>
/// 站層讀取失敗種類
/// </summary>
public enum ModbusStationErrorKind
{
    /// <summary>逾時：等不到對上 tid / 站號 / 功能碼的回應</summary>
    Timeout,

    /// <summary>該站回 FC|0x80 例外碼</summary>
    ExceptionCode
}

/// <summary>
/// 站層錯誤（某個 ModbusID 沒回應 / 回例外碼）— 與 TCP 連線無關，連線保持，換下一站繼續讀。
/// 連線層錯誤（SocketException / IOException / 遠端關閉）不用這個型別。
/// </summary>
public class ModbusStationException : Exception
{
    public ModbusStationErrorKind Kind { get; }

    /// <summary>例外碼（Kind = ExceptionCode 時有效）</summary>
    public byte nExceptionCode { get; }

    /// <summary>
    /// 該站視為「不可達」：逾時，或 Gateway 回 0x0A（路徑不可用）/ 0x0B（目標設備無回應）。
    /// 其餘例外碼（非法位址等）代表站有回、只是該區塊設定有問題。
    /// </summary>
    public bool IsStationUnreachable => Kind == ModbusStationErrorKind.Timeout || nExceptionCode is 0x0A or 0x0B;

    public ModbusStationException(ModbusStationErrorKind kind, byte nExceptionCode, string szMessage)
        : base(szMessage)
    {
        Kind = kind;
        this.nExceptionCode = nExceptionCode;
    }
}

/// <summary>
/// Modbus TCP 讀取傳輸層（FC1~4）。收框規則移植 iAUTO tgCoordinator.vb WaitModbusResponse / DrainSocket（現場驗證過）：
/// <list type="bullet">
/// <item>每筆請求 Transaction ID 遞增、跳過 0（0 保留給「Gateway 不帶回 tid」）</item>
/// <item>收到的位元組累積後依 MBAP 長度切框，tid + 站號 + 功能碼 + 位元組數全對上才收；遲到 / 無主框整框略過繼續等</item>
/// <item>Protocol ID ≠ 0 或長度不合理 → 不是框頭，滑 1 byte 重新對齊</item>
/// <item>退路：開頭 tid / 站號 / 功能碼 / 位元組數對上且長度夠就收，不看 MBAP 長度欄（有些 Gateway 填錯）</item>
/// <item>FC|0x80 → 該站例外碼（站層錯誤，非斷線）</item>
/// </list>
/// FluentModbus 不比對 tid，逾時後 Gateway 遲到的回應會被下一筆請求誤收（串台），故讀取改走本類別。
/// 非執行緒安全：一條連線同一時間只允許一筆請求（Modbus TCP 循序協定）。
/// </summary>
public sealed class ModbusTcpTransport : IDisposable
{
    private const int MBAP_HEADER_LENGTH = 7;
    private const int MAX_FRAME_LENGTH = 260;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ushort _nTransactionId;
    private readonly byte[] _accBuffer = new byte[8192];

    /// <summary>等待回應逾時（毫秒）</summary>
    public int nResponseTimeoutMs { get; set; } = 1000;

    /// <summary>上一筆請求略過的無主框數（診斷 / 測試用）</summary>
    public int nLastSkippedFrameCount { get; private set; }

    /// <summary>上一筆請求使用的 Transaction ID（測試用）</summary>
    public ushort nLastTransactionId => _nTransactionId;

    public bool IsConnected => _tcpClient?.Connected == true && _stream != null;

    /// <summary>
    /// 建立 TCP 連線；失敗丟 SocketException / TimeoutException
    /// </summary>
    public void Connect(IPEndPoint endPoint, int nConnectTimeoutMs)
    {
        Close();

        var tcpClient = new TcpClient { NoDelay = true };
        try
        {
            var connectTask = tcpClient.ConnectAsync(endPoint.Address, endPoint.Port);
            try
            {
                if (!connectTask.Wait(nConnectTimeoutMs))
                    throw new TimeoutException($"連線逾時 {nConnectTimeoutMs}ms: {endPoint}");
            }
            catch (AggregateException ae) when (ae.InnerException != null)
            {
                throw ae.InnerException;
            }

            var stream = tcpClient.GetStream();
            stream.WriteTimeout = nConnectTimeoutMs;
            _tcpClient = tcpClient;
            _stream = stream;
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 讀取一段資料（FC1 Coils / FC2 Discrete Inputs / FC3 Holding / FC4 Input Registers），
    /// 回傳 PDU 資料位元組（暫存器為 Big Endian 原樣、線圈為 LSB 在前的位元組，與 FluentModbus 回傳格式相同）。
    /// </summary>
    /// <exception cref="ModbusStationException">站層錯誤：逾時或例外碼，連線保持</exception>
    /// <exception cref="IOException">連線層錯誤：寫入失敗、遠端關閉</exception>
    public byte[] Read(byte nUnitId, byte nFunctionCode, ushort nStartAddress, ushort nCount)
    {
        if (nFunctionCode is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(nFunctionCode), nFunctionCode, "僅支援 FC1~4 讀取");

        var nExpectedByteCount = nFunctionCode is 1 or 2 ? (nCount + 7) / 8 : nCount * 2;
        if (nCount == 0 || nExpectedByteCount > 250)
            throw new ArgumentOutOfRangeException(nameof(nCount), nCount, "讀取數量超出單框上限");

        var stream = _stream ?? throw new IOException("Modbus TCP 未連線");

        _nTransactionId = (ushort)(_nTransactionId + 1);
        if (_nTransactionId == 0) _nTransactionId = 1;

        var request = new byte[12];
        request[0] = (byte)(_nTransactionId >> 8);
        request[1] = (byte)(_nTransactionId & 0xFF);
        request[2] = 0;
        request[3] = 0;
        request[4] = 0;
        request[5] = 6;
        request[6] = nUnitId;
        request[7] = nFunctionCode;
        request[8] = (byte)(nStartAddress >> 8);
        request[9] = (byte)(nStartAddress & 0xFF);
        request[10] = (byte)(nCount >> 8);
        request[11] = (byte)(nCount & 0xFF);

        stream.Write(request, 0, request.Length);

        return WaitResponse(request, nExpectedByteCount);
    }

    /// <summary>
    /// 清空接收緩衝殘料（逾時 / 每輪開頭呼叫），讓串流重新對齊。連線層錯誤吞掉，留給下一筆請求回報。
    /// </summary>
    public void Drain()
    {
        try
        {
            var socket = _tcpClient?.Client;
            var stream = _stream;
            if (socket == null || stream == null) return;

            var drainBuffer = new byte[4096];
            while (socket.Available > 0)
            {
                if (stream.Read(drainBuffer, 0, Math.Min(socket.Available, drainBuffer.Length)) <= 0)
                    break;
            }
        }
        catch
        {
        }
    }

    private byte[] WaitResponse(byte[] request, int nExpectedByteCount)
    {
        var socket = _tcpClient!.Client;
        var stream = _stream!;
        var nCount = 0;
        var nSkip = 0;
        var swWait = Stopwatch.StartNew();
        nLastSkippedFrameCount = 0;

        while (true)
        {
            var nRemainingMs = nResponseTimeoutMs - (int)swWait.ElapsedMilliseconds;
            var nPollMicroseconds = Math.Clamp(nRemainingMs, 0, 10) * 1000;

            if (socket.Poll(nPollMicroseconds, SelectMode.SelectRead))
            {
                var nAvailable = socket.Available;
                if (nAvailable == 0)
                    throw new IOException("Modbus TCP 遠端已關閉連線");

                // 累積滿了（正常不會發生）就整包丟掉重來
                if (nCount >= _accBuffer.Length) nCount = 0;
                var nRead = stream.Read(_accBuffer, nCount, Math.Min(nAvailable, _accBuffer.Length - nCount));
                if (nRead <= 0)
                    throw new IOException("Modbus TCP 遠端已關閉連線");
                nCount += nRead;

                while (nCount >= 8)
                {
                    var isTidOk = (_accBuffer[0] == request[0] && _accBuffer[1] == request[1])
                                  || (_accBuffer[0] == 0 && _accBuffer[1] == 0);

                    // 退路：開頭 tid / 站號 / 功能碼 / 位元組數對上且長度夠就收，不看 MBAP 長度欄與 Protocol ID
                    if (isTidOk && nCount >= nExpectedByteCount + 9
                        && _accBuffer[6] == request[6] && _accBuffer[7] == request[7]
                        && _accBuffer[8] == nExpectedByteCount)
                    {
                        nLastSkippedFrameCount = nSkip;
                        var data = new byte[nExpectedByteCount];
                        Array.Copy(_accBuffer, 9, data, 0, nExpectedByteCount);
                        return data;
                    }

                    var nFrameLength = 6 + _accBuffer[4] * 256 + _accBuffer[5];
                    if (_accBuffer[2] != 0 || _accBuffer[3] != 0 || nFrameLength < MBAP_HEADER_LENGTH + 2 || nFrameLength > MAX_FRAME_LENGTH)
                    {
                        // 不是框頭，滑 1 byte 重新對齊
                        Array.Copy(_accBuffer, 1, _accBuffer, 0, nCount - 1);
                        nCount--;
                        continue;
                    }

                    // 框還沒收齊，繼續等（對的回應若先到齊，上面的退路會先收）
                    if (nCount < nFrameLength) break;

                    if (isTidOk && _accBuffer[6] == request[6] && _accBuffer[7] == (request[7] | 0x80))
                    {
                        nLastSkippedFrameCount = nSkip;
                        var nExceptionCode = _accBuffer[8];
                        throw new ModbusStationException(ModbusStationErrorKind.ExceptionCode, nExceptionCode,
                            $"例外碼 0x{nExceptionCode:X2}");
                    }

                    // 不是這筆請求的框，整框丟掉繼續找
                    nSkip++;
                    Array.Copy(_accBuffer, nFrameLength, _accBuffer, 0, nCount - nFrameLength);
                    nCount -= nFrameLength;
                }
            }

            if (swWait.ElapsedMilliseconds >= nResponseTimeoutMs)
            {
                nLastSkippedFrameCount = nSkip;
                throw new ModbusStationException(ModbusStationErrorKind.Timeout, 0,
                    $"逾時 {nResponseTimeoutMs}ms，略過框數 {nSkip}");
            }
        }
    }

    /// <summary>
    /// 關閉連線
    /// </summary>
    public void Close()
    {
        try
        {
            _stream?.Dispose();
            _tcpClient?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _stream = null;
            _tcpClient = null;
        }
    }

    public void Dispose() => Close();
}
