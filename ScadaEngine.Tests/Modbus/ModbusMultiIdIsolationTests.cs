using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Communication.Modbus.Services;

namespace ScadaEngine.Tests.Modbus;

/// <summary>
/// 多 ID Coordinator 單站斷線隔離：同一條 TCP 掛 3 站，中間一站不回，其餘站每輪照常 Good、TCP 不重建；
/// 寬限期（20 × ConnectTimeout）後只有死站自己的點位變 Bad；整台無回應 / Gateway 斷線維持整台行為。
/// </summary>
public class ModbusMultiIdIsolationTests
{
    private const int CONNECT_TIMEOUT_MS = 100; // 寬限期 = 20 × 100 = 2 秒
    private const int DATABASE_ID = 1;

    private static ModbusDeviceConfigModel MakeConfig(int nPort, string szModbusId, int nNoResponseRounds = 5, int nProbeIntervalMs = 30000) => new()
    {
        nUnreachableProbeIntervalMs = nProbeIntervalMs,
        szIP = "127.0.0.1",
        nPort = nPort,
        szModbusId = szModbusId,
        nConnectTimeout = CONNECT_TIMEOUT_MS,
        nDatabaseId = DATABASE_ID,
        szCoordinatorName = "TestCoord",
        nNoResponseReconnectRounds = nNoResponseRounds,
        tagList = new List<ModbusTagModel>
        {
            new() { szName = "T1", szAddress = "40001", szDataType = "Integer" },
            new() { szName = "T2", szAddress = "40011", szDataType = "Integer" }
        }
    };

    private static string Sid(byte nModbusId, int nTagNo) => $"{DATABASE_ID * 65536 + nModbusId * 256 + 1}-S{nTagNo}";

    private static ModbusCommunicationService MakeService(ModbusDeviceConfigModel config)
        => new(config, NullLogger<ModbusCommunicationService>.Instance);

    /// <summary>死站集合可在測試中途改變（模擬恢復）；requestCounts 記各站收到的請求數</summary>
    private static FakeModbusGateway MakeGateway(HashSet<byte> deadStations, System.Collections.Concurrent.ConcurrentDictionary<byte, int>? requestCounts = null)
        => new(req =>
        {
            requestCounts?.AddOrUpdate(req[6], 1, (_, n) => n + 1);
            lock (deadStations)
            {
                if (deadStations.Contains(req[6])) return null;
            }
            return FakeModbusGateway.BuildResponse(req, FakeModbusGateway.BuildRegisterData(req));
        });

    [Fact]
    public async Task 中間站不回_其餘站每輪Good_TCP不重建()
    {
        var deadStations = new HashSet<byte> { 2 };
        using var gateway = MakeGateway(deadStations);
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2,3"));
        Assert.True(await service.ConnectAsync());

        for (int nRound = 0; nRound < 4; nRound++)
        {
            var result = await service.ReadAllTagsAsync();

            Assert.Contains(result, r => r.szSID == Sid(1, 1) && r.szQuality == "Good" && r.fValue == 100);
            Assert.Contains(result, r => r.szSID == Sid(3, 2) && r.szQuality == "Good" && r.fValue == 310);
            // 寬限期內死站不產生任何資料
            Assert.DoesNotContain(result, r => r.szSID == Sid(2, 1));
            Assert.True(service.IsConnected);
        }

        Assert.Equal(1, gateway.nAcceptedCount);
    }

    [Fact]
    public async Task 寬限期後只有死站標Bad_恢復後回Good且TCP未重建()
    {
        var deadStations = new HashSet<byte> { 2 };
        using var gateway = MakeGateway(deadStations);
        // 探測間隔 0 = 每輪都探測（只問一次），恢復後下一輪即回 Good
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2,3", nProbeIntervalMs: 0));
        Assert.True(await service.ConnectAsync());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<ScadaEngine.Common.Data.Models.RealtimeDataModel> result;
        do
        {
            result = await service.ReadAllTagsAsync();
        } while (sw.ElapsedMilliseconds < 20 * CONNECT_TIMEOUT_MS + 500);

        Assert.Contains(result, r => r.szSID == Sid(2, 1) && r.szQuality == "Bad");
        Assert.Contains(result, r => r.szSID == Sid(2, 2) && r.szQuality == "Bad");
        Assert.All(result.Where(r => !r.szSID.StartsWith($"{DATABASE_ID * 65536 + 2 * 256 + 1}-")),
                   r => Assert.Equal("Good", r.szQuality));

        lock (deadStations) deadStations.Clear();
        result = await service.ReadAllTagsAsync();

        Assert.Contains(result, r => r.szSID == Sid(2, 2) && r.szQuality == "Good" && r.fValue == 210);
        Assert.Equal(1, gateway.nAcceptedCount);
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task 已知死站_探測間隔內跳過不問_其他站不被拖慢()
    {
        var deadStations = new HashSet<byte> { 2 };
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<byte, int>();
        using var gateway = MakeGateway(deadStations, counts);
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2,3", nProbeIntervalMs: 60_000));
        Assert.True(await service.ConnectAsync());

        await service.ReadAllTagsAsync();              // 第 1 輪：判定站 2 不可達（1 + 重送 2 = 3 次）
        Assert.Equal(3, counts.GetValueOrDefault((byte)2));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.ReadAllTagsAsync(); // 第 2 輪：站 2 跳過
        sw.Stop();

        Assert.Equal(3, counts.GetValueOrDefault((byte)2));
        Assert.True(sw.ElapsedMilliseconds < CONNECT_TIMEOUT_MS, $"第 2 輪耗時 {sw.ElapsedMilliseconds}ms，死站仍在拖慢輪詢");
        Assert.Contains(result, r => r.szSID == Sid(1, 1) && r.szQuality == "Good");
        Assert.Contains(result, r => r.szSID == Sid(3, 1) && r.szQuality == "Good");
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task 已知死站_到探測時間只問一次_有回應即恢復()
    {
        const int PROBE_MS = 400;
        var deadStations = new HashSet<byte> { 2 };
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<byte, int>();
        using var gateway = MakeGateway(deadStations, counts);
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2,3", nProbeIntervalMs: PROBE_MS));
        Assert.True(await service.ConnectAsync());

        await service.ReadAllTagsAsync();
        Assert.Equal(3, counts.GetValueOrDefault((byte)2));

        // 到探測時間，仍無回應 → 只問 1 次、不重送
        await Task.Delay(PROBE_MS + 50);
        await service.ReadAllTagsAsync();
        Assert.Equal(4, counts.GetValueOrDefault((byte)2));

        // 恢復供電：下一次探測有回應 → 回 Good，之後每輪正常輪詢
        lock (deadStations) deadStations.Clear();
        await Task.Delay(PROBE_MS + 50);
        var result = await service.ReadAllTagsAsync();
        Assert.Contains(result, r => r.szSID == Sid(2, 1) && r.szQuality == "Good" && r.fValue == 200);

        var nBefore = counts.GetValueOrDefault((byte)2);
        await service.ReadAllTagsAsync();
        Assert.Equal(nBefore + 1, counts.GetValueOrDefault((byte)2));
        Assert.Equal(1, gateway.nAcceptedCount);
    }

    [Fact]
    public async Task 全部站都死_不跳過_每輪只問一次()
    {
        var deadStations = new HashSet<byte> { 1, 2 };
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<byte, int>();
        using var gateway = MakeGateway(deadStations, counts);
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2", nNoResponseRounds: 10, nProbeIntervalMs: 60_000));
        Assert.True(await service.ConnectAsync());

        await service.ReadAllTagsAsync();
        await service.ReadAllTagsAsync();

        // 第 1 輪各 3 次、第 2 輪不跳過但不重送各 1 次
        Assert.Equal(4, counts.GetValueOrDefault((byte)1));
        Assert.Equal(4, counts.GetValueOrDefault((byte)2));
    }

    [Fact]
    public async Task 整台無回應_連續K輪後主動斷線()
    {
        var deadStations = new HashSet<byte> { 1, 2 };
        using var gateway = MakeGateway(deadStations);
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2", nNoResponseRounds: 2));
        Assert.True(await service.ConnectAsync());

        await service.ReadAllTagsAsync();
        Assert.True(service.IsConnected);

        await service.ReadAllTagsAsync();
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task Gateway斷線_整台斷線並於寬限期後全部Bad()
    {
        var deadStations = new HashSet<byte>();
        using var gateway = MakeGateway(deadStations);
        using var service = MakeService(MakeConfig(gateway.nPort, "1,2"));
        Assert.True(await service.ConnectAsync());

        var result = await service.ReadAllTagsAsync();
        Assert.Equal(4, result.Count(r => r.szQuality == "Good"));

        gateway.DropAllClients();
        await Task.Delay(100);
        result = await service.ReadAllTagsAsync();

        Assert.False(service.IsConnected);
        Assert.DoesNotContain(result, r => r.szQuality == "Bad"); // 寬限期內不推 Bad

        await Task.Delay(20 * CONNECT_TIMEOUT_MS + 200);
        result = await service.ReadAllTagsAsync();

        Assert.Equal(4, result.Count(r => r.szQuality == "Bad"));
    }

    [Fact]
    public async Task 單ID_正常讀取不退化()
    {
        using var gateway = MakeGateway(new HashSet<byte>());
        using var service = MakeService(MakeConfig(gateway.nPort, "7"));
        Assert.True(await service.ConnectAsync());

        var result = await service.ReadAllTagsAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.szSID == Sid(7, 1) && r.fValue == 700 && r.szQuality == "Good" && r.szCoordinatorName == "TestCoord");
        Assert.Contains(result, r => r.szSID == Sid(7, 2) && r.fValue == 710);
    }

    [Fact]
    public async Task 遲到回應_不會串到下一站()
    {
        // 站 1 第一次故意晚回（超過逾時），遲到框落在站 2 讀取期間 — 站 2 只能收到自己的值
        var nStation1Count = 0;
        using var gateway = new FakeModbusGateway(req =>
        {
            if (req[6] == 1 && Interlocked.Increment(ref nStation1Count) == 1)
                Thread.Sleep(CONNECT_TIMEOUT_MS + 50);
            return FakeModbusGateway.BuildResponse(req, FakeModbusGateway.BuildRegisterData(req));
        });
        var config = MakeConfig(gateway.nPort, "1,2");
        config.nRetryCount = 0;
        using var service = MakeService(config);
        Assert.True(await service.ConnectAsync());

        var result = await service.ReadAllTagsAsync();

        Assert.Contains(result, r => r.szSID == Sid(2, 1) && r.fValue == 200);
        Assert.Contains(result, r => r.szSID == Sid(2, 2) && r.fValue == 210);
        Assert.True(service.IsConnected);
    }
}
