using System.Net;
using ScadaEngine.Engine.Communication.Modbus.Services;

namespace ScadaEngine.Tests.Modbus;

/// <summary>
/// ModbusTcpTransport 收框規則（移植 iAUTO WaitModbusResponse / DrainSocket）：
/// tid 比對、遲到框略過、tid=0 退路、例外碼、滑動重新對齊、逾時、遠端關閉。
/// </summary>
public class ModbusTcpTransportTests
{
    private static ModbusTcpTransport Connect(FakeModbusGateway gateway, int nTimeoutMs = 300)
    {
        var transport = new ModbusTcpTransport { nResponseTimeoutMs = nTimeoutMs };
        transport.Connect(new IPEndPoint(IPAddress.Loopback, gateway.nPort), 1000);
        return transport;
    }

    [Fact]
    public void 正常回應_回傳資料位元組()
    {
        using var gateway = new FakeModbusGateway(req => FakeModbusGateway.BuildResponse(req, FakeModbusGateway.BuildRegisterData(req)));
        using var transport = Connect(gateway);

        var data = transport.Read(5, 3, 10, 2);

        // 站號 5 × 100 + offset 10 = 510 (0x01FE)、511 (0x01FF)，Big Endian 原樣
        Assert.Equal(new byte[] { 0x01, 0xFE, 0x01, 0xFF }, data);
    }

    [Fact]
    public void 遲到框_tid不符整框略過_收到正確那一框()
    {
        using var gateway = new FakeModbusGateway(req =>
        {
            var nTid = (ushort)(req[0] << 8 | req[1]);
            var stale = FakeModbusGateway.BuildResponse(req, new byte[] { 0xDE, 0xAD }, (ushort)(nTid + 100)); // 避開 0（0 走 Gateway 不帶 tid 退路）
            var correct = FakeModbusGateway.BuildResponse(req, new byte[] { 0x12, 0x34 });
            return stale.Concat(correct).ToArray();
        });
        using var transport = Connect(gateway);

        var data = transport.Read(1, 3, 0, 1);

        Assert.Equal(new byte[] { 0x12, 0x34 }, data);
        Assert.Equal(1, transport.nLastSkippedFrameCount);
    }

    [Fact]
    public void Gateway不帶回tid_tid為0也收()
    {
        using var gateway = new FakeModbusGateway(req => FakeModbusGateway.BuildResponse(req, new byte[] { 0x00, 0x2A }, 0));
        using var transport = Connect(gateway);

        Assert.Equal(new byte[] { 0x00, 0x2A }, transport.Read(1, 4, 0, 1));
    }

    [Fact]
    public void 站號不符的框略過_逾時()
    {
        using var gateway = new FakeModbusGateway(req =>
        {
            var other = (byte[])req.Clone();
            other[6] = (byte)(req[6] + 1);
            return FakeModbusGateway.BuildResponse(other, new byte[] { 0x00, 0x01 });
        });
        using var transport = Connect(gateway, 200);

        var ex = Assert.Throws<ModbusStationException>(() => transport.Read(1, 3, 0, 1));
        Assert.Equal(ModbusStationErrorKind.Timeout, ex.Kind);
        Assert.Equal(1, transport.nLastSkippedFrameCount);
    }

    [Theory]
    [InlineData((byte)0x02, false)]
    [InlineData((byte)0x0B, true)]
    [InlineData((byte)0x0A, true)]
    public void 例外碼_站層錯誤且依碼判定不可達(byte nCode, bool isUnreachable)
    {
        using var gateway = new FakeModbusGateway(req => FakeModbusGateway.BuildException(req, nCode));
        using var transport = Connect(gateway);

        var ex = Assert.Throws<ModbusStationException>(() => transport.Read(1, 3, 0, 1));
        Assert.Equal(ModbusStationErrorKind.ExceptionCode, ex.Kind);
        Assert.Equal(nCode, ex.nExceptionCode);
        Assert.Equal(isUnreachable, ex.IsStationUnreachable);
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public void 開頭亂碼_滑動重新對齊後收到()
    {
        using var gateway = new FakeModbusGateway(req =>
            new byte[] { 0x55, 0xAA, 0x01, 0x02, 0x03 }.Concat(FakeModbusGateway.BuildResponse(req, new byte[] { 0xBE, 0xEF })).ToArray());
        using var transport = Connect(gateway);

        Assert.Equal(new byte[] { 0xBE, 0xEF }, transport.Read(1, 3, 0, 1));
    }

    [Fact]
    public void 不回應_逾時丟站層錯誤_連線保持()
    {
        using var gateway = new FakeModbusGateway(_ => null);
        using var transport = Connect(gateway, 150);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ModbusStationException>(() => transport.Read(1, 3, 0, 1));
        Assert.Equal(ModbusStationErrorKind.Timeout, ex.Kind);
        Assert.True(ex.IsStationUnreachable);
        Assert.InRange(sw.ElapsedMilliseconds, 140, 1500);
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public void 逾時後遲到的回應_不會被下一筆同站同功能碼誤收()
    {
        var nRequestCount = 0;
        byte[]? lateResponse = null;
        using var gateway = new FakeModbusGateway(req =>
        {
            if (Interlocked.Increment(ref nRequestCount) == 1)
            {
                // 第一筆故意晚回（超過 client 逾時）
                lateResponse = FakeModbusGateway.BuildResponse(req, new byte[] { 0xDE, 0xAD });
                Thread.Sleep(250);
                return lateResponse;
            }
            return FakeModbusGateway.BuildResponse(req, new byte[] { 0x00, 0x07 });
        });
        using var transport = Connect(gateway, 100);

        Assert.Throws<ModbusStationException>(() => transport.Read(1, 3, 0, 1));
        Thread.Sleep(300); // 讓遲到框落進 socket

        // 不先 Drain：靠 tid 比對也必須略過遲到框
        var data = transport.Read(1, 3, 0, 1);
        Assert.Equal(new byte[] { 0x00, 0x07 }, data);
        Assert.Equal(1, transport.nLastSkippedFrameCount);
    }

    [Fact]
    public void Drain_清掉殘框()
    {
        var nRequestCount = 0;
        using var gateway = new FakeModbusGateway(req =>
        {
            if (Interlocked.Increment(ref nRequestCount) == 1)
            {
                Thread.Sleep(200);
                return FakeModbusGateway.BuildResponse(req, new byte[] { 0xDE, 0xAD });
            }
            return FakeModbusGateway.BuildResponse(req, new byte[] { 0x00, 0x08 });
        });
        using var transport = Connect(gateway, 100);

        Assert.Throws<ModbusStationException>(() => transport.Read(1, 3, 0, 1));
        Thread.Sleep(250);
        transport.Drain();

        Assert.Equal(new byte[] { 0x00, 0x08 }, transport.Read(1, 3, 0, 1));
        Assert.Equal(0, transport.nLastSkippedFrameCount);
    }

    [Fact]
    public void 線圈回應位元組數依數量換算()
    {
        using var gateway = new FakeModbusGateway(req => FakeModbusGateway.BuildResponse(req, new byte[] { 0b0000_0101, 0b0000_0001 }));
        using var transport = Connect(gateway);

        Assert.Equal(new byte[] { 0x05, 0x01 }, transport.Read(1, 1, 0, 9));
    }

    [Fact]
    public void 遠端關閉_丟連線層IOException()
    {
        FakeModbusGateway? gatewayRef = null;
        using var gateway = new FakeModbusGateway(_ =>
        {
            gatewayRef!.DropAllClients();
            return null;
        });
        gatewayRef = gateway;
        using var transport = Connect(gateway, 1000);

        Assert.Throws<IOException>(() => transport.Read(1, 3, 0, 1));
    }
}
