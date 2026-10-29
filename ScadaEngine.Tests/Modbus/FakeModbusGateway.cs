using System.Net;
using System.Net.Sockets;

namespace ScadaEngine.Tests.Modbus;

/// <summary>
/// 測試用假 Modbus TCP Gateway：每收到一筆 12 byte 讀取請求就呼叫 handler，handler 回傳要寫回的原始位元組
/// （可夾殘框 / 亂碼 / 多框；null = 不回應，模擬死站）。
/// </summary>
internal sealed class FakeModbusGateway : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<byte[], byte[]?> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();
    private int _nAcceptedCount;

    public int nPort { get; }

    /// <summary>累計接受的 TCP 連線數（驗證「單站失敗不重建 TCP」）</summary>
    public int nAcceptedCount => Volatile.Read(ref _nAcceptedCount);

    public FakeModbusGateway(Func<byte[], byte[]?> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        nPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    /// <summary>關掉所有已連線的 client（模擬 Gateway 斷線）</summary>
    public void DropAllClients()
    {
        lock (_clients)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }
            Interlocked.Increment(ref _nAcceptedCount);
            lock (_clients) _clients.Add(client);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var request = new byte[12];
            while (!_cts.IsCancellationRequested)
            {
                var nRead = 0;
                while (nRead < 12)
                {
                    var n = await stream.ReadAsync(request.AsMemory(nRead, 12 - nRead), _cts.Token);
                    if (n == 0) return;
                    nRead += n;
                }

                var response = _handler((byte[])request.Clone());
                if (response != null && response.Length > 0)
                    await stream.WriteAsync(response, _cts.Token);
            }
        }
        catch
        {
        }
    }

    /// <summary>組正常讀取回應框（FC1~4）</summary>
    public static byte[] BuildResponse(byte[] request, byte[] data, ushort? nTidOverride = null)
    {
        var frame = new byte[9 + data.Length];
        var nTid = nTidOverride ?? (ushort)(request[0] << 8 | request[1]);
        frame[0] = (byte)(nTid >> 8);
        frame[1] = (byte)(nTid & 0xFF);
        frame[4] = (byte)((3 + data.Length) >> 8);
        frame[5] = (byte)((3 + data.Length) & 0xFF);
        frame[6] = request[6];
        frame[7] = request[7];
        frame[8] = (byte)data.Length;
        Array.Copy(data, 0, frame, 9, data.Length);
        return frame;
    }

    /// <summary>組例外回應框（FC|0x80）</summary>
    public static byte[] BuildException(byte[] request, byte nExceptionCode)
        => new byte[] { request[0], request[1], 0, 0, 0, 3, request[6], (byte)(request[7] | 0x80), nExceptionCode };

    /// <summary>依請求的數量產生暫存器資料：每個暫存器值 = 站號 × 100 + 位址 offset</summary>
    public static byte[] BuildRegisterData(byte[] request)
    {
        var nStart = request[8] << 8 | request[9];
        var nCount = request[10] << 8 | request[11];
        var data = new byte[nCount * 2];
        for (int i = 0; i < nCount; i++)
        {
            var nValue = (ushort)(request[6] * 100 + nStart + i);
            data[i * 2] = (byte)(nValue >> 8);
            data[i * 2 + 1] = (byte)(nValue & 0xFF);
        }
        return data;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        DropAllClients();
    }
}
