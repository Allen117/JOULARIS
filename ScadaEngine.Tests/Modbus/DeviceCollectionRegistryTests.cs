using ScadaEngine.Engine.Communication.Modbus.Services;

namespace ScadaEngine.Tests.Modbus;

/// <summary>
/// 採集迴圈登記簿測試 — 鎖住「熱重載結果 = 重啟 Engine 結果」：
/// 同 IP:Port 多個設定檔以設定檔名為鍵各自獨立，重載 / 刪除只影響該檔，同名重入序列化不生孤兒。
/// 以假服務 + 假迴圈（等到取消才結束）模擬，不連真設備。
/// </summary>
public class DeviceCollectionRegistryTests
{
    /// <summary>假通訊服務：記錄 IP:Port、版本、迴圈是否仍在跑、是否已 Dispose</summary>
    private sealed class FakeService : IDisposable
    {
        public string szEndpoint { get; init; } = "";
        public int nVersion { get; init; }
        public volatile bool isLoopRunning;
        public volatile bool isDisposed;
        public void Dispose() => isDisposed = true;
    }

    /// <summary>所有假迴圈的執行數（跨服務），用來抓孤兒</summary>
    private int _nActiveLoops;

    private Task FakeLoopAsync(FakeService service, CancellationToken ct, int nStopDelayMs = 0)
        => Task.Run(async () =>
        {
            Interlocked.Increment(ref _nActiveLoops);
            service.isLoopRunning = true;
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // 模擬斷線 / 連線逾時時停止較久
                if (nStopDelayMs > 0) await Task.Delay(nStopDelayMs);
            }
            finally
            {
                service.isLoopRunning = false;
                Interlocked.Decrement(ref _nActiveLoops);
            }
        });

    private Task<bool> ReplaceAsync(DeviceCollectionRegistry<FakeService> registry, string szName, string szEndpoint, int nVersion = 1, int nStopDelayMs = 0)
        => registry.ReplaceAsync(szName,
            () => new FakeService { szEndpoint = szEndpoint, nVersion = nVersion },
            (svc, ct) => FakeLoopAsync(svc, ct, nStopDelayMs));

    private async Task WaitLoopsAsync(int nExpected)
    {
        for (int i = 0; i < 200 && Volatile.Read(ref _nActiveLoops) != nExpected; i++)
            await Task.Delay(10);
        Assert.Equal(nExpected, Volatile.Read(ref _nActiveLoops));
    }

    [Fact]
    public async Task 同IP三檔同時重載_三個迴圈都在跑()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();

        await Task.WhenAll(
            ReplaceAsync(registry, "2F電表", "172.16.12.180:502"),
            ReplaceAsync(registry, "2F流量計", "172.16.12.180:502"),
            ReplaceAsync(registry, "2F露點計", "172.16.12.180:502"));

        Assert.Equal(3, registry.Count);
        Assert.True(registry.IsRunning("2F電表"));
        Assert.True(registry.IsRunning("2F流量計"));
        Assert.True(registry.IsRunning("2F露點計"));
        await WaitLoopsAsync(3);
    }

    [Fact]
    public async Task 熱編輯單檔_只重啟該檔_同IP他檔不受影響()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await ReplaceAsync(registry, "全廠總用電", "172.16.12.183:502");
        await ReplaceAsync(registry, "分迴路用電", "172.16.12.183:502");
        var other = registry.GetService("分迴路用電")!;
        var oldEdited = registry.GetService("全廠總用電")!;

        await ReplaceAsync(registry, "全廠總用電", "172.16.12.183:502", nVersion: 2);

        Assert.Same(other, registry.GetService("分迴路用電"));
        Assert.True(other.isLoopRunning);
        Assert.False(other.isDisposed);
        Assert.True(oldEdited.isDisposed);
        Assert.False(oldEdited.isLoopRunning);
        Assert.Equal(2, registry.GetService("全廠總用電")!.nVersion);
        await WaitLoopsAsync(2);
    }

    [Fact]
    public async Task 刪除單檔_只停該檔()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await ReplaceAsync(registry, "A", "10.0.0.1:502");
        await ReplaceAsync(registry, "B", "10.0.0.1:502");
        var removed = registry.GetService("A")!;

        Assert.True(await registry.RemoveAsync("a")); // 不分大小寫

        Assert.False(registry.IsRunning("A"));
        Assert.True(registry.IsRunning("B"));
        Assert.True(removed.isDisposed);
        Assert.False(await registry.RemoveAsync("A"));
        await WaitLoopsAsync(1);
    }

    [Fact]
    public async Task 單檔改IP_舊連線停_同IP他檔不受影響()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await ReplaceAsync(registry, "A", "10.0.0.1:502");
        await ReplaceAsync(registry, "B", "10.0.0.1:502");
        var oldA = registry.GetService("A")!;
        var b = registry.GetService("B")!;

        await ReplaceAsync(registry, "A", "10.0.0.2:502");

        Assert.True(oldA.isDisposed);
        Assert.Equal("10.0.0.2:502", registry.GetService("A")!.szEndpoint);
        Assert.Same(b, registry.GetService("B"));
        Assert.True(b.isLoopRunning);
        await WaitLoopsAsync(2);
    }

    [Fact]
    public async Task 同名重載進行中又被改_序列化_只剩一個最新迴圈()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await ReplaceAsync(registry, "A", "10.0.0.1:502", nVersion: 1, nStopDelayMs: 300);

        // 第一次重載停舊迴圈要 300ms（模擬連線逾時），期間再來兩次變更
        var t2 = ReplaceAsync(registry, "A", "10.0.0.1:502", nVersion: 2, nStopDelayMs: 300);
        await Task.Delay(20);
        var t3 = ReplaceAsync(registry, "A", "10.0.0.1:502", nVersion: 3);
        await Task.WhenAll(t2, t3);

        Assert.Equal(1, registry.Count);
        Assert.Equal(3, registry.GetService("A")!.nVersion);
        await WaitLoopsAsync(1);
    }

    [Fact]
    public async Task 建立新服務失敗_舊迴圈保留()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await ReplaceAsync(registry, "A", "10.0.0.1:502");
        var old = registry.GetService("A")!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceAsync("A",
            () => throw new InvalidOperationException("bad config"),
            (svc, ct) => FakeLoopAsync(svc, ct)));

        Assert.Same(old, registry.GetService("A"));
        Assert.True(old.isLoopRunning);
        Assert.False(old.isDisposed);
        await WaitLoopsAsync(1);
    }

    [Fact]
    public async Task 舊迴圈異常結束_不阻斷起新迴圈()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await registry.ReplaceAsync("A",
            () => new FakeService(),
            (svc, ct) => Task.FromException(new IOException("socket closed")));

        Assert.True(await ReplaceAsync(registry, "A", "10.0.0.1:502", nVersion: 2));
        Assert.Equal(2, registry.GetService("A")!.nVersion);
        await WaitLoopsAsync(1);
    }

    [Fact]
    public async Task StopAll_全部停止_之後不再起新迴圈()
    {
        var registry = new DeviceCollectionRegistry<FakeService>();
        await Task.WhenAll(
            ReplaceAsync(registry, "A", "10.0.0.1:502"),
            ReplaceAsync(registry, "B", "10.0.0.1:502"),
            ReplaceAsync(registry, "C", "10.0.0.2:502"));
        var services = new[] { "A", "B", "C" }.Select(n => registry.GetService(n)!).ToList();

        await registry.StopAllAsync();

        Assert.Equal(0, registry.Count);
        Assert.All(services, s => Assert.True(s.isDisposed));
        await WaitLoopsAsync(0);

        Assert.False(await ReplaceAsync(registry, "A", "10.0.0.1:502"));
        Assert.Equal(0, registry.Count);
        await WaitLoopsAsync(0);
    }
}
