namespace ScadaEngine.Web.Services;

/// <summary>
/// 空壓系統診斷排程 — 每日 02:00 後首次輪詢，對「昨日」生成最近 7 天滾動快照（不論能源日報區段是否啟用；
/// 無啟用空壓機則不算）。骨架同 <see cref="DailyReportSchedulerService"/>（30 秒輪詢 + 當日去重）；
/// 啟動時若昨日快照缺就補。能源日報生成時也會呼叫同一冪等方法，兩邊誰先到誰算。
/// </summary>
public class CompressorDiagnosisSchedulerService : BackgroundService
{
    private const int TRIGGER_HOUR = 2;

    private readonly ILogger<CompressorDiagnosisSchedulerService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public CompressorDiagnosisSchedulerService(ILogger<CompressorDiagnosisSchedulerService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 等 Web 啟動完成（DB schema 同步）再開跑；比日報晚一點避免同時撈大量分鐘資料
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DateTime? dtLastTriggeredDate = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var dtNow = DateTime.Now;
            if (dtNow.Hour >= TRIGGER_HOUR && dtLastTriggeredDate != dtNow.Date)
            {
                await RunAsync(dtNow.Date.AddDays(-1));
                dtLastTriggeredDate = dtNow.Date;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunAsync(DateTime dtReportDate)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<CompressorDiagnosisService>();
            await service.GetOrRunScheduledAsync(dtReportDate);
        }
        catch (Exception ex)
        {
            // 單次失敗不重試（避免 30 秒輪詢洗版），下次重啟 / 隔日再算
            _logger.LogError(ex, "空壓系統診斷排程失敗 ReportDate={ReportDate:yyyy-MM-dd}", dtReportDate);
        }
    }
}
