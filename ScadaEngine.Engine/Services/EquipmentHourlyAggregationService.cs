using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// 設備效率診斷 — 設備層 hourly 預聚合背景服務（冰機 / 空壓機）。
/// 啟動時 catch-up 過去 N 小時（缺漏才補），主迴圈每 30 秒檢查是否過了 XX:06，到了就聚合剛結束的上個小時。
///
/// 每次觸發都重讀 EquipmentProfile + EquipmentPointBinding → Web 設定頁存檔後下一個整點自動生效，不需重啟 Engine。
/// 觸發分鐘錯開：電 / 冷凍噸 / 自來水 XX:02、氣 XX:03、產氣量表 XX:04、Web 電費 XX:05 → 本服務 XX:06。
/// 對標 <see cref="WaterLeafAggregationService"/>。
/// </summary>
public class EquipmentHourlyAggregationService : BackgroundService
{
    private readonly ILogger<EquipmentHourlyAggregationService> _logger;
    private readonly EquipmentHourlyAggregator _aggregator;
    private readonly EquipmentHourlyRepository _repository;
    private readonly EquipmentProfileRepository _profileRepository;
    private readonly IConfiguration _configuration;

    /// <summary>觸發時點（每小時第 6 分鐘）</summary>
    public const int TRIGGER_MINUTE = 6;

    public EquipmentHourlyAggregationService(
        ILogger<EquipmentHourlyAggregationService> logger,
        EquipmentHourlyAggregator aggregator,
        EquipmentHourlyRepository repository,
        EquipmentProfileRepository profileRepository,
        IConfiguration configuration)
    {
        _logger = logger;
        _aggregator = aggregator;
        _repository = repository;
        _profileRepository = profileRepository;
        _configuration = configuration;
    }

    private int MinSamples => _configuration.GetValue<int?>("EquipmentAggregation:MinSamplesPerHour") ?? 30;
    private int MaxStalenessHours => _configuration.GetValue<int?>("EquipmentAggregation:MaxStalenessHours") ?? 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nCatchUpHours = _configuration.GetValue<int?>("EquipmentAggregation:CatchUpHours") ?? 24;
        _logger.LogInformation(
            "設備效率 hourly 預聚合服務啟動，MinSamplesPerHour={Min}, MaxStalenessHours={Stale}, CatchUpHours={CatchUp}, TriggerMinute=XX:{Trig:D2}",
            MinSamples, MaxStalenessHours, nCatchUpHours, TRIGGER_MINUTE);

        try
        {
            await CatchUpAsync(nCatchUpHours, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設備效率聚合啟動 catch-up 失敗");
        }

        DateTime? dtLastTriggeredHour = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dtNow = DateTime.Now;
                if (dtNow.Minute >= TRIGGER_MINUTE)
                {
                    var dtTarget = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, dtNow.Hour, 0, 0).AddHours(-1);
                    if (dtLastTriggeredHour != dtTarget)
                    {
                        await AggregateHourAsync(dtTarget, stoppingToken);
                        dtLastTriggeredHour = dtTarget;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "設備效率聚合主迴圈發生錯誤");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("設備效率 hourly 預聚合服務已停止");
    }

    private async Task CatchUpAsync(int nCatchUpHours, CancellationToken stoppingToken)
    {
        if (nCatchUpHours <= 0) return;
        var profiles = await _profileRepository.GetEnabledProfilesAsync();
        if (profiles.Count == 0)
        {
            _logger.LogInformation("設備效率聚合 catch-up：尚未設定任何冰機 / 空壓機");
            return;
        }

        var dtNow = DateTime.Now;
        var dtEnd = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, dtNow.Hour, 0, 0);   // exclusive
        var dtStart = dtEnd.AddHours(-nCatchUpHours);
        int nFilled = 0, nSkipped = 0, nSparse = 0;

        foreach (var p in profiles)
        {
            if (stoppingToken.IsCancellationRequested) break;
            var existing = await _repository.GetExistingHoursAsync(p.nId, dtStart, dtEnd);
            for (var dtH = dtStart; dtH < dtEnd; dtH = dtH.AddHours(1))
            {
                if (existing.Contains(dtH)) { nSkipped++; continue; }
                var m = await SafeComputeAsync(p, dtH);
                if (m == null) { nSparse++; continue; }
                await _repository.UpsertAsync(m);
                nFilled++;
            }
        }

        _logger.LogInformation(
            "設備效率聚合 catch-up 完成：設備 {Count} 台，寫入 {Filled} 列、已存在跳過 {Skipped} 列、資料不足跳過 {Sparse} 列",
            profiles.Count, nFilled, nSkipped, nSparse);
    }

    private async Task AggregateHourAsync(DateTime dtHourStart, CancellationToken stoppingToken)
    {
        var profiles = await _profileRepository.GetEnabledProfilesAsync();
        if (profiles.Count == 0) return;

        int nWritten = 0, nSparse = 0;
        foreach (var p in profiles)
        {
            if (stoppingToken.IsCancellationRequested) break;
            var m = await SafeComputeAsync(p, dtHourStart);
            if (m == null) { nSparse++; continue; }
            await _repository.UpsertAsync(m);
            nWritten++;
        }

        _logger.LogInformation(
            "已聚合 {Count} 台設備的 {Hour:yyyy-MM-dd HH} 效率資料（寫入 {Written} 列、資料不足跳過 {Sparse} 列）",
            profiles.Count, dtHourStart, nWritten, nSparse);
    }

    /// <summary>單台失敗不影響其他台</summary>
    private async Task<EquipmentHourlyModel?> SafeComputeAsync(EquipmentProfileModel p, DateTime dtHour)
    {
        try
        {
            return await _aggregator.ComputeAsync(p, dtHour, MinSamples, MaxStalenessHours);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設備效率聚合失敗：{Name}(#{Id}) {Hour:yyyy-MM-dd HH}", p.szName, p.nId, dtHour);
            return null;
        }
    }
}
