using System.Collections.Concurrent;

namespace ScadaEngine.Engine.Communication.Modbus.Services;

/// <summary>
/// 採集迴圈登記簿 — 以「設定檔名」（= Coordinator 名稱，不分大小寫）為唯一鍵，記錄每個設定檔正在跑的
/// 通訊服務 + 取消令牌 + 迴圈 Task。純記帳、不碰 IO，供 <see cref="ModbusCollectionManager"/> 使用並可單元測試。
///
/// 一個設定檔 = 一個通訊服務 = 一條連線；同 IP:Port 的多個設定檔各自獨立，互不停掉。
/// 同名的 Replace / Remove 以 per-name 鎖序列化（避免重載交錯再生孤兒迴圈），不同名完全並行。
/// </summary>
/// <typeparam name="TService">通訊服務型別（停止迴圈後 Dispose）</typeparam>
public sealed class DeviceCollectionRegistry<TService> where TService : class, IDisposable
{
    private sealed record Entry(TService Service, CancellationTokenSource Cts, Task Loop);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nameLocks = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isStopped = false;

    /// <summary>目前執行中的迴圈數</summary>
    public int Count => _entries.Count;

    /// <summary>目前執行中的設定檔名</summary>
    public IReadOnlyCollection<string> Names => _entries.Keys.ToList();

    /// <summary>該設定檔是否有迴圈在跑</summary>
    public bool IsRunning(string szName) => _entries.ContainsKey(szName);

    /// <summary>取得該設定檔目前的通訊服務（無則 null）</summary>
    public TService? GetService(string szName) => _entries.TryGetValue(szName, out var entry) ? entry.Service : null;

    /// <summary>
    /// 以新服務取代同名舊迴圈：先建立新服務（失敗則丟出例外、舊迴圈維持不動）→ 停舊 → 起新。
    /// </summary>
    /// <param name="szName">設定檔名</param>
    /// <param name="createService">建立通訊服務（不應做 IO）</param>
    /// <param name="runLoop">採集迴圈本體，須在取消時結束</param>
    /// <returns>是否已啟動新迴圈（StopAll 之後呼叫一律 false）</returns>
    public async Task<bool> ReplaceAsync(string szName, Func<TService> createService, Func<TService, CancellationToken, Task> runLoop)
    {
        var nameLock = _nameLocks.GetOrAdd(szName, _ => new SemaphoreSlim(1, 1));
        await nameLock.WaitAsync();
        try
        {
            if (_isStopped) return false;

            var service = createService();

            if (_entries.TryRemove(szName, out var oldEntry))
                await StopEntryAsync(oldEntry);

            var cts = new CancellationTokenSource();
            Task loop;
            try
            {
                loop = runLoop(service, cts.Token);
            }
            catch
            {
                cts.Dispose();
                service.Dispose();
                throw;
            }

            _entries[szName] = new Entry(service, cts, loop);
            return true;
        }
        finally
        {
            nameLock.Release();
        }
    }

    /// <summary>停止並移除該設定檔的迴圈</summary>
    /// <returns>原本是否有迴圈在跑</returns>
    public async Task<bool> RemoveAsync(string szName)
    {
        var nameLock = _nameLocks.GetOrAdd(szName, _ => new SemaphoreSlim(1, 1));
        await nameLock.WaitAsync();
        try
        {
            if (!_entries.TryRemove(szName, out var entry)) return false;
            await StopEntryAsync(entry);
            return true;
        }
        finally
        {
            nameLock.Release();
        }
    }

    /// <summary>停止全部迴圈；之後的 Replace 一律拒絕（關機中不再起新迴圈）</summary>
    public async Task StopAllAsync()
    {
        _isStopped = true;
        // 列舉「曾取過鎖的名稱」而非 _entries：進行中的 Replace 已先 GetOrAdd 鎖，會被這裡排隊等到並移除
        await Task.WhenAll(_nameLocks.Keys.ToList().Select(RemoveAsync));
    }

    private static async Task StopEntryAsync(Entry entry)
    {
        try
        {
            entry.Cts.Cancel();
            try
            {
                await entry.Loop;
            }
            catch (Exception)
            {
                // 正常取消，或迴圈自身錯誤（迴圈內已記 log）— 不可阻斷後續的起新迴圈 / 停其他迴圈
            }
        }
        finally
        {
            entry.Service.Dispose();
            entry.Cts.Dispose();
        }
    }
}
