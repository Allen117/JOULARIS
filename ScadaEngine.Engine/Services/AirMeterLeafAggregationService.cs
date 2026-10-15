namespace ScadaEngine.Engine.Services;

/// <summary>
/// 產氣量表葉子層 hourly 預聚合背景服務。
/// 啟動時做 catch-up（過去 N 小時 × 所有葉子，缺漏才補算），主迴圈每 30 秒檢查是否到 XX:04:00，
/// 到達就針對「剛結束的上個小時」聚合一次。
///
/// 觸發時點刻意錯開：電表 XX:02 / 水表 XX:02 / 氣表 XX:03 / 產氣量表 **XX:04**，避免聚合服務同時掃 HistoryData 邊界值。
///
/// Engine 完全不參與階層加總 — sign 權重、虛擬節點仍由 Web on-demand 計算，
/// 確保「迴路結構改變即時生效不需回填」。與 GasMeterLeafAggregationService 對稱但各自獨立（設定區段 AirMeterAggregation）。
/// </summary>
public class AirMeterLeafAggregationService : BackgroundService
{
    private readonly ILogger<AirMeterLeafAggregationService> _logger;
    private readonly AirMeterLeafAggregator _aggregator;
    private readonly AirMeterLeafHourlyRepository _repository;
    private readonly IConfiguration _configuration;

    /// <summary>觸發時點（每小時的第 4 分鐘）— 與電表/水表/氣表聚合錯開</summary>
    private const int TRIGGER_MINUTE = 4;

    public AirMeterLeafAggregationService(
        ILogger<AirMeterLeafAggregationService> logger,
        AirMeterLeafAggregator aggregator,
        AirMeterLeafHourlyRepository repository,
        IConfiguration configuration)
    {
        _logger = logger;
        _aggregator = aggregator;
        _repository = repository;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nMaxStalenessHours = _configuration.GetValue<int?>("AirMeterAggregation:MaxStalenessHours") ?? 2;
        var nCatchUpHours = _configuration.GetValue<int?>("AirMeterAggregation:CatchUpHours") ?? 24;

        _logger.LogInformation(
            "產氣量表葉子層 hourly 預聚合服務啟動，MaxStalenessHours={Staleness}, CatchUpHours={CatchUp}, TriggerMinute=XX:{Min:D2}",
            nMaxStalenessHours, nCatchUpHours, TRIGGER_MINUTE);

        // 啟動時 catch-up — 補過去 N 小時缺漏
        try
        {
            await CatchUpAsync(nCatchUpHours, nMaxStalenessHours, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "產氣量表葉子層聚合啟動 catch-up 失敗");
        }

        // 主迴圈：每 30 秒檢查是否到 XX:04
        DateTime? dtLastTriggeredHour = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dtNow = DateTime.Now;
                if (dtNow.Minute >= TRIGGER_MINUTE)
                {
                    var dtTargetHour = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, dtNow.Hour, 0, 0).AddHours(-1);
                    if (dtLastTriggeredHour != dtTargetHour)
                    {
                        await AggregateHourAsync(dtTargetHour, nMaxStalenessHours, stoppingToken);
                        dtLastTriggeredHour = dtTargetHour;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "產氣量表葉子層聚合主迴圈發生錯誤");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("產氣量表葉子層 hourly 預聚合服務已停止");
    }

    /// <summary>啟動時 catch-up — 過去 N 小時每葉子若 AirMeterLeafHourly 缺對應列就補算</summary>
    private async Task CatchUpAsync(int nCatchUpHours, int nMaxStalenessHours, CancellationToken stoppingToken)
    {
        if (nCatchUpHours <= 0) return;

        var leaves = await _repository.GetAllLeafSidsAsync();
        if (leaves.Count == 0)
        {
            _logger.LogInformation("產氣量表葉子層聚合 catch-up: 無葉子節點可聚合");
            return;
        }

        var dtNow = DateTime.Now;
        // 上個小時整點往前推 nCatchUpHours 小時
        var dtCurrentHourStart = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, dtNow.Hour, 0, 0);
        var dtCatchUpEnd = dtCurrentHourStart;                       // exclusive，不含當前未結束的小時
        var dtCatchUpStart = dtCatchUpEnd.AddHours(-nCatchUpHours);

        _logger.LogInformation(
            "產氣量表葉子層聚合 catch-up 開始：葉子 {LeafCount} 個，範圍 {From:yyyy-MM-dd HH:mm} ~ {To:yyyy-MM-dd HH:mm}",
            leaves.Count, dtCatchUpStart, dtCatchUpEnd);

        int nFilled = 0, nSkipped = 0, nSparseSkipped = 0;
        foreach (var leaf in leaves)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var existing = await _repository.GetExistingHoursAsync(leaf.szSID, dtCatchUpStart, dtCatchUpEnd);
            for (var dtH = dtCatchUpStart; dtH < dtCatchUpEnd; dtH = dtH.AddHours(1))
            {
                if (existing.Contains(dtH)) { nSkipped++; continue; }
                var model = await _aggregator.ComputeAsync(
                    leaf.szSID, dtH, leaf.dMaxVolume, leaf.dUnitScale, nMaxStalenessHours, leaf.szName);
                if (model == null) { nSparseSkipped++; continue; }
                await _repository.UpsertAsync(model);
                nFilled++;
            }
        }

        _logger.LogInformation(
            "產氣量表葉子層聚合 catch-up 完成：寫入 {Filled} 列、已存在跳過 {Skipped} 列、sparse 跳過 {SparseSkipped} 列",
            nFilled, nSkipped, nSparseSkipped);
    }

    /// <summary>對指定小時針對所有葉子做聚合 + UPSERT</summary>
    private async Task AggregateHourAsync(DateTime dtHourStart, int nMaxStalenessHours, CancellationToken stoppingToken)
    {
        var leaves = await _repository.GetAllLeafSidsAsync();
        if (leaves.Count == 0)
        {
            _logger.LogInformation("產氣量表葉子層聚合 {Hour:yyyy-MM-dd HH}: 無葉子節點", dtHourStart);
            return;
        }

        int nWritten = 0, nSparseSkipped = 0;
        foreach (var leaf in leaves)
        {
            if (stoppingToken.IsCancellationRequested) break;
            var model = await _aggregator.ComputeAsync(
                leaf.szSID, dtHourStart, leaf.dMaxVolume, leaf.dUnitScale, nMaxStalenessHours, leaf.szName);
            if (model == null) { nSparseSkipped++; continue; }
            await _repository.UpsertAsync(model);
            nWritten++;
        }

        _logger.LogInformation(
            "已聚合 {LeafCount} 個產氣量表葉子 SID 的 {Hour:yyyy-MM-dd HH} 產氣（寫入 {Written} 列、sparse 跳過 {Sparse} 列）",
            leaves.Count, dtHourStart, nWritten, nSparseSkipped);
    }
}
