using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ScadaEngine.Engine.Communication.Modbus.Services;
using ScadaEngine.Engine.Communication.Mqtt;

namespace ScadaEngine.Engine.Services;

/// <summary>
/// HASP 授權守衛：
///   啟動時立即驗證，之後每 30 分鐘重複；
///   驗證失敗 → 暫停 Modbus 採集 + 更新 LicenseState；
///   驗證恢復 → 恢復採集；
///   結果以 Retain MQTT 發布至 SCADA/Sys/License/Status。
/// 驗證透過 Named Pipe 委派給 32-bit JoularisLicense Bridge 服務（Pipe 名稱維持 ScadaEngineLicense）。
/// </summary>
public class LicenseService : BackgroundService
{
    private static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromMinutes(30);

    private const string PIPE_NAME  = "ScadaEngineLicense";
    private const string BRIDGE_SVC = "JoularisLicense";
    private const string BRIDGE_EXE_NAME = "ScadaEngine.LicenseBridge.exe";
    private const string MQTT_STATUS_TOPIC = "SCADA/Sys/License/Status";

    private readonly ILogger<LicenseService> _logger;
    private readonly LicenseState _licenseState;
    private readonly ModbusCollectionManager _modbusCollectionManager;
    private readonly MqttPublishService _mqttPublishService;

    private bool _wasSuspended = false;

    // 部署佈局 <root>\Engine\App\ → <root>\LicenseBridge\；不寫死安裝根目錄（Install.bat 的 %_ROOT% 為唯一真相）
    private readonly string _szBridgeExePath = ResolveBridgeExePath(AppContext.BaseDirectory);

    /// <summary>
    /// 由 Engine 執行檔目錄推算 Bridge exe 路徑：上兩層（安裝根目錄）\LicenseBridge\ScadaEngine.LicenseBridge.exe。
    /// 不檢查存在與否 — 開發 bin 下推算出的路徑不存在屬正常，僅供 log 與 sc create 使用。
    /// </summary>
    public static string ResolveBridgeExePath(string szBaseDir)
    {
        string szRoot = Path.GetFullPath(Path.Combine(szBaseDir, "..", ".."));
        return Path.Combine(szRoot, "LicenseBridge", BRIDGE_EXE_NAME);
    }

    public LicenseService(
        ILogger<LicenseService> logger,
        LicenseState licenseState,
        ModbusCollectionManager modbusCollectionManager,
        MqttPublishService mqttPublishService)
    {
        _logger = logger;
        _licenseState = licenseState;
        _modbusCollectionManager = modbusCollectionManager;
        _mqttPublishService = mqttPublishService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("授權守衛服務啟動，Bridge 路徑: {Path}，等待 MQTT/DB 初始化 (5 秒)...", _szBridgeExePath);
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync();

            try
            {
                await Task.Delay(CHECK_INTERVAL, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>立即執行一次授權驗證，更新狀態並通知各服務。</summary>
    public async Task CheckAsync()
    {
        _logger.LogInformation("開始 HASP 授權驗證 (via Bridge)...");
        bool isValid;
        string reason;

        try
        {
            (isValid, reason) = await VerifyViaBridgeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HASP 驗證時發生例外");
            isValid = false;
            reason = $"例外: {ex.Message}";
        }

        _licenseState.Update(isValid, reason);

        if (isValid)
        {
            _logger.LogInformation("HASP 授權驗證通過");
            if (_wasSuspended)
            {
                _modbusCollectionManager.Resume();
                _wasSuspended = false;
                _logger.LogInformation("Modbus 採集已恢復");
            }
        }
        else
        {
            _logger.LogWarning("HASP 授權驗證失敗: {Reason}", reason);
            if (!_wasSuspended)
            {
                _modbusCollectionManager.Suspend();
                _wasSuspended = true;
                _logger.LogWarning("Modbus 採集已暫停");
            }
        }

        await PublishStatusAsync(isValid, reason);
    }

    // ── Bridge 驗證邏輯 ─────────────────────────────────────────────────────────

    private async Task<(bool isValid, string reason)> VerifyViaBridgeAsync()
    {
        // 第一次嘗試連 Pipe（Bridge 已在跑）
        if (TryConnectAndVerify(out var r1))
            return r1;

        // Pipe 不通 → 嘗試啟動 Bridge 服務
        _logger.LogWarning("Named Pipe 不通，嘗試啟動 Bridge 服務...");
        try
        {
            EnsureBridgeService();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "啟動 Bridge 服務失敗");
            return (false, $"Bridge 服務啟動失敗: {ex.Message}");
        }

        // 等服務啟動後再試一次
        await Task.Delay(5000);

        if (TryConnectAndVerify(out var r2))
            return r2;

        return (false, "Bridge 服務啟動後仍無法連線 Named Pipe");
    }

    private static bool TryConnectAndVerify(out (bool isValid, string reason) result)
    {
        result = default;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut);
            pipe.Connect(3000); // 3 秒 timeout

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            writer.WriteLine("verify");
            var response = reader.ReadLine();

            result = response switch
            {
                "ok"                                          => (true, ""),
                var s when s?.StartsWith("fail:") == true    => (false, s[5..]),
                _                                             => (false, $"Bridge 回應異常: {response}")
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureBridgeService()
    {
        // 查詢服務是否存在（1060 = 找不到服務）
        var queryOut = RunSc($"query {BRIDGE_SVC}");
        bool exists = !queryOut.Contains("1060");

        if (!exists)
        {
            _logger.LogInformation("Bridge 服務不存在，建立服務...");
            RunSc($"create {BRIDGE_SVC} binPath= \"{_szBridgeExePath}\" DisplayName= \"JOULARIS License Bridge\" start= auto");
        }

        _logger.LogInformation("啟動 Bridge 服務...");
        RunSc($"start {BRIDGE_SVC}");
    }

    private static string RunSc(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo("sc.exe", args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        return output;
    }

    // ── MQTT 發布 ────────────────────────────────────────────────────────────────

    private async Task PublishStatusAsync(bool isValid, string reason)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                valid = isValid,
                checkedAt = _licenseState.CheckedAt.ToString("o"),
                reason
            });
            await _mqttPublishService.PublishRawJsonAsync(MQTT_STATUS_TOPIC, payload, isRetain: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "發布授權狀態 MQTT 失敗（狀態已記錄於 LicenseState）");
        }
    }
}
