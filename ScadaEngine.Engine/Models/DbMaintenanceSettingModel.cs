using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScadaEngine.Engine.Models;

/// <summary>
/// 資料庫維護設定 — 對應 ScadaEngine.Engine/Setting/DbMaintenanceSetting.json
/// 含自動建 DB 的實體檔案路徑與每週自動備份排程。
/// 注意：DataFileFolder / BackupFolder 的檔案 I/O 由 SQL Server 服務進程執行，
/// 資料夾需授權 SQL 服務帳號寫入（由 Setting/install-db.ps1 處理）。
/// </summary>
public class DbMaintenanceSettingModel
{
    /// <summary>自動建 DB 時 MDF/LDF 存放資料夾（僅新建 DB 時使用，不影響既有 DB）</summary>
    public string DataFileFolder { get; set; } = @"C:\Scada\Database";

    /// <summary>備份檔（.bak）存放資料夾</summary>
    public string BackupFolder { get; set; } = @"C:\Scada\Backup";

    /// <summary>每週自動備份總開關 — false 時排程服務閒置，不影響其他功能</summary>
    public bool BackupEnabled { get; set; } = true;

    /// <summary>備份執行星期（Sunday ~ Saturday）</summary>
    public string BackupDayOfWeek { get; set; } = "Sunday";

    /// <summary>備份執行時間（24 小時制 HH:mm）</summary>
    public string BackupTime { get; set; } = "03:00";

    /// <summary>解析 BackupDayOfWeek，無效值退回 Sunday</summary>
    public DayOfWeek GetBackupDayOfWeek()
    {
        return Enum.TryParse<DayOfWeek>(BackupDayOfWeek, ignoreCase: true, out var day)
            ? day
            : DayOfWeek.Sunday;
    }

    /// <summary>解析 BackupTime 為當日觸發時刻，無效值退回 03:00</summary>
    public TimeSpan GetBackupTime()
    {
        return TimeSpan.TryParseExact(BackupTime, @"hh\:mm", null, out var time)
            ? time
            : new TimeSpan(3, 0, 0);
    }

    /// <summary>
    /// 載入 DbMaintenanceSetting.json — 路徑偵測與 LineSetting/EmailSetting 同邏輯，
    /// 另加開發模式相對路徑 fallback（Web dotnet run 時讀 repo 內 Engine 那份）。
    /// 本檔為 Engine 專屬設定、不走 DbSettingLocator 的 Shared\ 機制（那只管 dbSetting.json）。
    /// 檔案不存在或解析失敗時回傳預設值（C:\Scada 路徑 + 週日 03:00）。
    /// </summary>
    public static DbMaintenanceSettingModel LoadFromDefaultPaths(ILogger logger)
    {
        var szCandidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Setting", "DbMaintenanceSetting.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Setting", "DbMaintenanceSetting.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "ScadaEngine.Engine", "Setting", "DbMaintenanceSetting.json")
        };

        var szPath = szCandidates.FirstOrDefault(File.Exists);
        if (szPath == null)
        {
            logger.LogWarning("找不到 DbMaintenanceSetting.json，使用預設維護設定 (主要路徑: {Path})", szCandidates[0]);
            return new DbMaintenanceSettingModel();
        }

        try
        {
            var szJson = File.ReadAllText(szPath);
            var setting = JsonSerializer.Deserialize<DbMaintenanceSettingModel>(
                szJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
            logger.LogInformation("DbMaintenanceSetting.json 已載入: {Path}", szPath);
            return setting ?? new DbMaintenanceSettingModel();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析 DbMaintenanceSetting.json 失敗，使用預設維護設定");
            return new DbMaintenanceSettingModel();
        }
    }
}
