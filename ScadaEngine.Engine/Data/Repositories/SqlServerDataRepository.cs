using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Dapper;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Engine.Data.Services;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Data.Services;
using ScadaEngine.Engine.Models;
namespace ScadaEngine.Engine.Data.Repositories;

/// <summary>
/// SQL Server 資料存取實作類別，負責實際的資料庫操作
/// </summary>
public class SqlServerDataRepository : IDataRepository, IDisposable
{
    private readonly ILogger<SqlServerDataRepository> _logger;
    private readonly DatabaseConfigService _configService;
    private string _szConnectionString = string.Empty;
    private bool _isDisposed = false;

    /// <summary>
    /// 建構函式
    /// </summary>
    /// <param name="logger">日誌記錄器</param>
    /// <param name="configService">資料庫配置服務</param>
    public SqlServerDataRepository(ILogger<SqlServerDataRepository> logger, DatabaseConfigService configService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    /// <summary>
    /// 初始化資料庫連線字串
    /// </summary>
    /// <returns>初始化成功回傳 true，失敗回傳 false</returns>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            _szConnectionString = await _configService.GetConnectionStringAsync();
            _logger.LogInformation("資料庫連線字串已初始化");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化資料庫連線字串時發生錯誤");
            return false;
        }
    }

    /// <summary>
    /// 測試資料庫連線是否正常
    /// </summary>
    /// <returns>連線正常回傳 true，失敗回傳 false</returns>
    public async Task<bool> TestConnectionAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            
            var result = await connection.QuerySingleAsync<int>("SELECT 1");
            
            _logger.LogInformation("資料庫連線測試成功");
            return result == 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "資料庫連線測試失敗");
            return false;
        }
    }

    /// <summary>
    /// 查詢指定時間範圍的歷史資料
    /// </summary>
    /// <param name="szSID">點位識別碼</param>
    /// <param name="dtStartTime">開始時間</param>
    /// <param name="dtEndTime">結束時間</param>
    /// <returns>歷史資料清單</returns>
    public async Task<IEnumerable<RealtimeDataModel>> GetHistoryDataAsync(string szSID, DateTime dtStartTime, DateTime dtEndTime)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT SID, TagName, Value, Unit, Quality, DeviceIP, Timestamp, Address
                FROM RealtimeData 
                WHERE SID = @SID AND Timestamp BETWEEN @StartTime AND @EndTime
                ORDER BY Timestamp DESC";

            var parameters = new
            {
                SID = szSID,
                StartTime = dtStartTime,
                EndTime = dtEndTime
            };

            var result = await connection.QueryAsync<RealtimeDataModel>(szSql, parameters);
            
            _logger.LogDebug("查詢歷史資料完成: SID={SID}, 筆數={Count}", szSID, result.Count());
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢歷史資料時發生錯誤: SID={SID}", szSID);
            return Enumerable.Empty<RealtimeDataModel>();
        }
    }


    #region Coordinator 相關方法

    /// <summary>
    /// 新增或更新 Coordinator 配置
    /// </summary>
    /// <param name="coordinator">Coordinator 模型</param>
    /// <param name="szDeviceNameFromConfig">JSON 的子站名稱逗號串；null = JSON 無此欄位（舊檔）→ 不動 DB 既有值</param>
    /// <returns>(資料庫 ID, DB 中的 DelayTime)</returns>
    public async Task<(int nId, int nDelayTime)> SaveCoordinatorAsync(CoordinatorModel coordinator, string? szDeviceNameFromConfig = null)
    {
        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // 檢查是否已存在相同的 Name 配置
            const string szCheckSql = "SELECT Id, DelayTime FROM ModbusCoordinator WHERE Name = @Name";
            var existing = await connection.QuerySingleOrDefaultAsync<(int Id, int DelayTime)?>(szCheckSql, new { Name = coordinator.szName });

            if (existing.HasValue)
            {
                // 更新現有記錄的 ModbusID（不覆蓋 DelayTime，由 DB 管理採集週期）
                // DeviceName 只在 JSON 有此欄位時覆寫（JSON 為子站名稱單一真相，DB 為執行期副本）
                const string szUpdateSql = @"
                    UPDATE ModbusCoordinator
                    SET ModbusID = @ModbusID,
                        DeviceName = CASE WHEN @HasDeviceName = 1 THEN @DeviceName ELSE DeviceName END
                    WHERE Id = @Id";

                await connection.ExecuteAsync(szUpdateSql, new
                {
                    ModbusID = coordinator.szModbusID,
                    HasDeviceName = szDeviceNameFromConfig != null,
                    DeviceName = szDeviceNameFromConfig ?? string.Empty,
                    Id = existing.Value.Id
                });

                _logger.LogDebug("更新 Coordinator 配置: ID={Id}, Name={Name}, ModbusID={ModbusID}, DelayTime={DelayTime}ms",
                    existing.Value.Id, coordinator.szName, coordinator.szModbusID, existing.Value.DelayTime);
                return (existing.Value.Id, existing.Value.DelayTime);
            }
            else
            {
                // 新增記錄（DelayTime 預設 1000ms）
                var nDefaultDelay = coordinator.nDelayTime > 0 ? coordinator.nDelayTime : 1000;
                const string szInsertSql = @"
                    INSERT INTO ModbusCoordinator (Name, ModbusID, DeviceName, DelayTime, MonitorEnabled)
                    VALUES (@Name, @ModbusID, @DeviceName, @DelayTime, @MonitorEnabled);
                    SELECT SCOPE_IDENTITY();";

                var newId = await connection.QuerySingleAsync<int>(szInsertSql, new
                {
                    Name = coordinator.szName,
                    DeviceName = szDeviceNameFromConfig,
                    ModbusID = coordinator.szModbusID,
                    DelayTime = nDefaultDelay,
                    MonitorEnabled = coordinator.isMonitorEnabled
                });

                _logger.LogDebug("新增 Coordinator 配置: ID={Id}, Name={Name}, ModbusID={ModbusID}, DelayTime={DelayTime}ms",
                    newId, coordinator.szName, coordinator.szModbusID, nDefaultDelay);
                return (newId, nDefaultDelay);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存 Coordinator 配置時發生錯誤: {Name}", coordinator.szName);
            throw;
        }
    }

    /// <summary>
    /// 根據 ModbusID 查詢 Coordinator
    /// </summary>
    /// <param name="szModbusID">Modbus ID</param>
    /// <returns>Coordinator 模型</returns>
    public async Task<CoordinatorModel?> GetCoordinatorByModbusIdAsync(string szModbusID)
    {
        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = "SELECT Id, Name, ModbusID, DelayTime, MonitorEnabled FROM ModbusCoordinator WHERE ModbusID = @ModbusID";
            var result = await connection.QuerySingleOrDefaultAsync<CoordinatorModel>(szSql, new { ModbusID = szModbusID });

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 Coordinator 配置時發生錯誤: ModbusID={ModbusID}", szModbusID);
            return null;
        }
    }

    /// <summary>
    /// 取得 ModbusCoordinator 資料表的所有設備清單
    /// </summary>
    public async Task<IEnumerable<CoordinatorModel>> GetAllCoordinatorsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT Id,
                       Name          AS szName,
                       ModbusID      AS szModbusID,
                       DeviceName    AS szDeviceName,
                       DelayTime     AS nDelayTime,
                       MonitorEnabled AS isMonitorEnabled
                FROM ModbusCoordinator
                ORDER BY Id";

            var result = await connection.QueryAsync<CoordinatorModel>(szSql);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢所有 Coordinator 時發生錯誤");
            return [];
        }
    }

    /// <summary>
    /// 更新 ModbusCoordinator 的 DeviceName 欄位
    /// </summary>
    public async Task<bool> UpdateDeviceNameAsync(int nId, string szDeviceName)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                UPDATE ModbusCoordinator
                SET DeviceName = @szDeviceName
                WHERE Id = @nId";

            var nRows = await connection.ExecuteAsync(szSql, new { nId, szDeviceName });
            return nRows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 Coordinator DeviceName 時發生錯誤 (Id={Id})", nId);
            return false;
        }
    }

    /// <summary>
    /// Coordinator 改名（Id 不變 → SID 不變）
    /// </summary>
    public async Task<CoordinatorRenameResult> RenameCoordinatorAsync(int nId, string szOldName, string szNewName)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);

        // 不分大小寫比對（與 Web / 檔名一致，不依賴 DB collation）
        const string szCheckSql = @"
            SELECT COUNT(1) FROM ModbusCoordinator WITH (UPDLOCK, HOLDLOCK)
            WHERE Id <> @nId AND LOWER(Name) = LOWER(@szNewName)";
        var nTaken = await connection.ExecuteScalarAsync<int>(szCheckSql, new { nId, szNewName }, transaction);
        if (nTaken > 0)
        {
            transaction.Rollback();
            return CoordinatorRenameResult.NameTaken;
        }

        const string szUpdateSql = @"
            UPDATE ModbusCoordinator SET Name = @szNewName
            WHERE Id = @nId AND Name = @szOldName";
        var nRows = await connection.ExecuteAsync(szUpdateSql, new { nId, szOldName, szNewName }, transaction);
        if (nRows != 1)
        {
            transaction.Rollback();
            return CoordinatorRenameResult.NotFound;
        }

        transaction.Commit();
        _logger.LogInformation("Coordinator 改名: Id={Id}, {Old} → {New}", nId, szOldName, szNewName);
        return CoordinatorRenameResult.Success;
    }

    #endregion

    #region 歷史資料操作方法

    /// <summary>
    /// 批量儲存歷史資料至 HistoryData 資料表
    /// </summary>
    /// <param name="historyDataList">歷史資料清單</param>
    /// <returns>成功儲存的資料筆數</returns>
    public async Task<int> SaveHistoryDataAsync(IEnumerable<HistoryDataModel> historyDataList)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        var nSuccessCount = 0;

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // SQL 插入語句，對應 DatabaseSchema.json 中的 HistoryData 表格結構
            // 使用 MERGE 避免重複插入相同的 SID + Timestamp 組合
            const string szSql = @"
                MERGE HistoryData AS target
                USING (VALUES (@SID, @Value, @Quality, @Timestamp)) AS source (SID, Value, Quality, Timestamp)
                ON target.SID = source.SID AND target.Timestamp = source.Timestamp
                WHEN MATCHED THEN
                    UPDATE SET Value = source.Value, Quality = source.Quality
                WHEN NOT MATCHED THEN
                    INSERT (SID, Value, Quality, Timestamp)
                    VALUES (source.SID, source.Value, source.Quality, source.Timestamp);";

            foreach (var data in historyDataList)
            {
                try
                {
                    var parameters = new
                    {
                        SID = data.szSID,
                        Value = data.fValue,
                        Quality = data.nQuality,
                        Timestamp = data.dtTimestamp
                    };

                    var nRowsAffected = await connection.ExecuteAsync(szSql, parameters);
                    if (nRowsAffected > 0)
                    {
                        nSuccessCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "儲存歷史資料失敗: SID={SID}, Timestamp={Timestamp}", 
                        data.szSID, data.dtTimestamp);
                }
            }

            // _logger.LogInformation("批量儲存歷史資料完成: 成功={SuccessCount}, 總計={TotalCount}",
            //     nSuccessCount, historyDataList.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量儲存歷史資料時發生錯誤");
        }

        return nSuccessCount;
    }

    /// <summary>
    /// 儲存單筆歷史資料至 HistoryData 資料表
    /// </summary>
    /// <param name="historyData">歷史資料</param>
    /// <returns>儲存成功回傳 true，失敗回傳 false</returns>
    public async Task<bool> SaveHistoryDataAsync(HistoryDataModel historyData)
    {
        var result = await SaveHistoryDataAsync(new[] { historyData });
        return result > 0;
    }

    #endregion

    #region 最新資料操作方法

    /// <summary>
    /// 批量儲存最新資料至 LatestData 資料表
    /// </summary>
    /// <param name="latestDataList">最新資料清單</param>
    /// <returns>成功儲存的資料筆數</returns>
    public async Task<int> SaveLatestDataAsync(IEnumerable<LatestDataModel> latestDataList)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        var nSuccessCount = 0;

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // SQL MERGE 語句，對應 DatabaseSchema.json 中的 LatestData 表格結構
            const string szSql = @"
                MERGE LatestData AS target
                USING (VALUES (@SID, @Value, @Timestamp, @Quality)) AS source (SID, Value, Timestamp, Quality)
                ON target.SID = source.SID
                WHEN MATCHED THEN
                    UPDATE SET Value = source.Value, Timestamp = source.Timestamp, Quality = source.Quality
                WHEN NOT MATCHED THEN
                    INSERT (SID, Value, Timestamp, Quality)
                    VALUES (source.SID, source.Value, source.Timestamp, source.Quality);";

            foreach (var data in latestDataList)
            {
                try
                {
                    var parameters = new
                    {
                        SID = data.szSID,
                        Value = data.fValue,
                        Timestamp = data.dtTimestamp,
                        Quality = data.nQuality
                    };

                    var nRowsAffected = await connection.ExecuteAsync(szSql, parameters);
                    if (nRowsAffected > 0)
                    {
                        nSuccessCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "儲存最新資料失敗: SID={SID}, Timestamp={Timestamp}", 
                        data.szSID, data.dtTimestamp);
                }
            }

            _logger.LogInformation("批量儲存最新資料完成: 成功={SuccessCount}, 總計={TotalCount}", 
                nSuccessCount, latestDataList.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量儲存最新資料時發生錯誤");
        }

        return nSuccessCount;
    }

    /// <summary>
    /// 儲存單筆最新資料至 LatestData 資料表
    /// </summary>
    /// <param name="latestData">最新資料</param>
    /// <returns>儲存成功回傳 true，失敗回傳 false</returns>
    public async Task<bool> SaveLatestDataAsync(LatestDataModel latestData)
    {
        var result = await SaveLatestDataAsync(new[] { latestData });
        return result > 0;
    }

    #endregion

    /// <summary>
    /// 取得最新的即時資料 (用於Web監控介面)
    /// </summary>
    /// <param name="nLimit">限制筆數</param>
    /// <returns>最新資料清單</returns>
    public async Task<IEnumerable<LatestDataModel>> GetLatestDataAsync(int nLimit = 100)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT TOP (@Limit)
                    SID       AS szSID,
                    Value     AS fValue,
                    Quality   AS nQuality,
                    Timestamp AS dtTimestamp
                FROM LatestData
                ORDER BY Timestamp DESC";

            var result = await connection.QueryAsync<LatestDataModel>(szSql, new { Limit = nLimit });
            
            _logger.LogDebug("查詢最新資料完成: 筆數={Count}", result.Count());
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢最新資料時發生錯誤");
            return Enumerable.Empty<LatestDataModel>();
        }
    }

    /// <summary>
    /// 取得最新資料的時間戳記
    /// </summary>
    /// <returns>最新時間戳記</returns>
    public async Task<DateTime> GetLatestTimestampAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = "SELECT ISNULL(MAX(Timestamp), '1900-01-01') FROM LatestData";
            var result = await connection.QuerySingleAsync<DateTime>(szSql);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢最新時間戳記時發生錯誤");
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 取得總點位數量
    /// </summary>
    /// <returns>點位總數</returns>
    public async Task<int> GetTotalTagCountAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = "SELECT COUNT(DISTINCT SID) FROM LatestData";
            var result = await connection.QuerySingleAsync<int>(szSql);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢點位總數時發生錯誤");
            return 0;
        }
    }

    /// <summary>
    /// 取得 Users 資料表中的使用者數量
    /// </summary>
    /// <returns>使用者總數</returns>
    public async Task<int> GetUserCountAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = "SELECT COUNT(*) FROM Users";
            var result = await connection.QuerySingleAsync<int>(szSql);
            
            _logger.LogDebug("Users 表中共有 {UserCount} 位使用者", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 Users 表使用者數量時發生錯誤");
            return 0; // 錯誤時回傳 0，這樣會顯示測試帳號提示
        }
    }

    /// <summary>
    /// 取得 Users 資料表中 Admin 角色的使用者數量
    /// </summary>
    public async Task<int> GetAdminCountAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // Admin 或 Engineer 任一存在即視為「已有管理者」：install-db.ps1 建的 engineer bootstrap 帳號
            // 可直接登入並在帳號設定頁建 Admin，不必再被 first-run /Setup 擋住
            const string szSql = "SELECT COUNT(*) FROM Users WHERE Role IN ('Admin', 'Engineer') AND IsActive = 1";
            var result = await connection.QuerySingleAsync<int>(szSql);

            _logger.LogDebug("Users 表中共有 {AdminCount} 位啟用的 Admin / Engineer 使用者", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 Admin / Engineer 使用者數量時發生錯誤");
            return 0;
        }
    }

    /// <summary>
    /// 驗證使用者帳號密碼 (密碼以 SHA256 hex 儲存於 PasswordHash 欄位)
    /// </summary>
    public async Task<bool> ValidateUserAsync(string szUsername, string szPassword)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT PasswordHash FROM Users
                WHERE Username = @Username AND IsActive = 1";

            var storedHash = await connection.QuerySingleOrDefaultAsync<string>(
                szSql, new { Username = szUsername });

            if (storedHash == null)
                return false;

            var inputHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(szPassword))).ToLower();

            return string.Equals(storedHash.Trim(), inputHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "驗證使用者 {Username} 時發生錯誤", szUsername);
            return false;
        }
    }

    /// <summary>
    /// 查詢 first-run setup 是否已完成（SystemSettings.SetupCompleted='1'）。
    /// 表尚未建立或查無值時回傳 false。
    /// </summary>
    public async Task<bool> IsSetupCompletedAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES
                           WHERE TABLE_NAME = 'SystemSettings' AND TABLE_TYPE = 'BASE TABLE')
                    SELECT SettingValue FROM SystemSettings WHERE SettingKey = 'SetupCompleted';";

            var szValue = await connection.QuerySingleOrDefaultAsync<string>(szSql);
            return szValue == "1";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查詢 SetupCompleted 旗標時發生錯誤，視為未完成");
            return false;
        }
    }

    /// <summary>
    /// 標記 first-run setup 已完成（UPSERT SystemSettings.SetupCompleted='1'）。
    /// 表不存在時就地建立，確保全新 DB 也能寫入。
    /// </summary>
    public async Task MarkSetupCompletedAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES
                               WHERE TABLE_NAME = 'SystemSettings' AND TABLE_TYPE = 'BASE TABLE')
                    CREATE TABLE [SystemSettings] (
                        [SettingKey]   NVARCHAR(100) NOT NULL CONSTRAINT [PK_SystemSettings] PRIMARY KEY,
                        [SettingValue] NVARCHAR(MAX) NULL,
                        [UpdatedAt]    DATETIME NOT NULL DEFAULT GETDATE()
                    );

                IF EXISTS (SELECT * FROM SystemSettings WHERE SettingKey = 'SetupCompleted')
                    UPDATE SystemSettings SET SettingValue = '1', UpdatedAt = GETDATE() WHERE SettingKey = 'SetupCompleted';
                ELSE
                    INSERT INTO SystemSettings (SettingKey, SettingValue, UpdatedAt) VALUES ('SetupCompleted', '1', GETDATE());";

            await connection.ExecuteAsync(szSql);
            _logger.LogInformation("已標記 first-run setup 完成（SystemSettings.SetupCompleted=1）");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "標記 SetupCompleted 旗標時發生錯誤");
        }
    }

    /// <summary>
    /// 取得所有使用者帳號資料
    /// </summary>
    public async Task<IEnumerable<UserModel>> GetAllUsersAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT UserID      AS nUserID,
                       Username    AS szUsername,
                       RealName    AS szRealName,
                       PasswordHash AS szPasswordHash,
                       Role        AS szRole,
                       Department  AS szDepartment,
                       IsActive    AS isActive,
                       LastLoginAt AS dtLastLoginAt,
                       CreatedAt   AS dtCreatedAt,
                       UpdatedAt   AS dtUpdatedAt
                FROM Users
                ORDER BY UserID";

            var users = await connection.QueryAsync<UserModel>(szSql);
            _logger.LogDebug("取得 {Count} 筆使用者資料", users.Count());
            return users;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 Users 表所有使用者時發生錯誤");
            return Enumerable.Empty<UserModel>();
        }
    }

    /// <summary>
    /// 新增使用者帳號（密碼以 SHA256 hex 儲存）
    /// </summary>
    public async Task<bool> CreateUserAsync(UserModel user)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // 將明文密碼轉為 SHA256 hex
            var szHashedPassword = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(user.szPasswordHash))).ToLower();

            const string szSql = @"
                INSERT INTO Users (Username, RealName, PasswordHash, Role, Department, IsActive, CreatedAt, UpdatedAt)
                VALUES (@Username, @RealName, @PasswordHash, @Role, @Department, @IsActive, GETDATE(), GETDATE())";

            var nRows = await connection.ExecuteAsync(szSql, new
            {
                Username = user.szUsername,
                RealName = string.IsNullOrEmpty(user.szRealName) ? (string?)null : user.szRealName,
                PasswordHash = szHashedPassword,
                Role = user.szRole,
                Department = string.IsNullOrEmpty(user.szDepartment) ? (string?)null : user.szDepartment,
                IsActive = user.isActive
            });

            _logger.LogInformation("新增使用者 {Username} 成功", user.szUsername);
            return nRows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增使用者 {Username} 時發生錯誤", user.szUsername);
            return false;
        }
    }

    /// <summary>
    /// 依帳號名稱取得單一使用者（含 Role），登入時用
    /// </summary>
    public async Task<UserModel?> GetUserByUsernameAsync(string szUsername)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT UserID      AS nUserID,
                       Username    AS szUsername,
                       RealName    AS szRealName,
                       PasswordHash AS szPasswordHash,
                       Role        AS szRole,
                       Department  AS szDepartment,
                       IsActive    AS isActive,
                       LastLoginAt AS dtLastLoginAt,
                       CreatedAt   AS dtCreatedAt,
                       UpdatedAt   AS dtUpdatedAt
                FROM Users
                WHERE Username = @Username";

            return await connection.QuerySingleOrDefaultAsync<UserModel>(szSql, new { Username = szUsername });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢使用者 {Username} 時發生錯誤", szUsername);
            return null;
        }
    }

    /// <summary>
    /// 更新使用者最後登入時間
    /// </summary>
    public async Task<bool> UpdateLastLoginAsync(string szUsername)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                UPDATE Users SET LastLoginAt = GETDATE()
                WHERE Username = @Username";

            var nRows = await connection.ExecuteAsync(szSql, new { Username = szUsername });
            return nRows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新使用者 {Username} 最後登入時間時發生錯誤", szUsername);
            return false;
        }
    }

    /// <summary>
    /// 更新使用者帳號（不含密碼）
    /// </summary>
    public async Task<bool> UpdateUserAsync(UserModel user)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                UPDATE Users SET
                    RealName   = @RealName,
                    Role       = @Role,
                    Department = @Department,
                    IsActive   = @IsActive,
                    UpdatedAt  = GETDATE()
                WHERE UserID = @UserID";

            var nRows = await connection.ExecuteAsync(szSql, new
            {
                UserID     = user.nUserID,
                RealName   = string.IsNullOrEmpty(user.szRealName) ? (string?)null : user.szRealName,
                Role       = user.szRole,
                Department = string.IsNullOrEmpty(user.szDepartment) ? (string?)null : user.szDepartment,
                IsActive   = user.isActive
            });

            _logger.LogInformation("更新使用者 UserID={UserID} 成功", user.nUserID);
            return nRows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新使用者 UserID={UserID} 時發生錯誤", user.nUserID);
            return false;
        }
    }

    /// <summary>
    /// 刪除使用者帳號（同時刪除 UserPermissions）
    /// </summary>
    public async Task<bool> DeleteUserAsync(int nUserID)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await connection.ExecuteAsync(
                    "DELETE FROM UserPermissions WHERE UserID = @UserID",
                    new { UserID = nUserID }, transaction: transaction);

                var nRows = await connection.ExecuteAsync(
                    "DELETE FROM Users WHERE UserID = @UserID",
                    new { UserID = nUserID }, transaction: transaction);

                await transaction.CommitAsync();
                _logger.LogInformation("刪除使用者 UserID={UserID} 成功", nUserID);
                return nRows > 0;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除使用者 UserID={UserID} 時發生錯誤", nUserID);
            return false;
        }
    }

    /// <summary>
    /// 取得使用者權限 JSON
    /// </summary>
    public async Task<UserPermissionModel?> GetUserPermissionsAsync(int nUserID)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT UserID         AS nUserID,
                       PermissionJson AS szPermissionJson,
                       UpdatedAt      AS dtUpdatedAt
                FROM UserPermissions
                WHERE UserID = @UserID";

            return await connection.QuerySingleOrDefaultAsync<UserPermissionModel>(
                szSql, new { UserID = nUserID });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢使用者權限 UserID={UserID} 時發生錯誤", nUserID);
            return null;
        }
    }

    /// <summary>
    /// 儲存使用者權限 JSON（UPSERT）
    /// </summary>
    public async Task<bool> SaveUserPermissionsAsync(int nUserID, string szPermissionJson)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                MERGE UserPermissions AS tgt
                USING (SELECT @UserID AS UserID) AS src ON tgt.UserID = src.UserID
                WHEN MATCHED THEN UPDATE SET PermissionJson = @PermissionJson, UpdatedAt = GETDATE()
                WHEN NOT MATCHED THEN INSERT (UserID, PermissionJson, UpdatedAt)
                    VALUES (@UserID, @PermissionJson, GETDATE());";

            await connection.ExecuteAsync(szSql, new
            {
                UserID = nUserID,
                PermissionJson = szPermissionJson
            });

            _logger.LogInformation("儲存使用者權限 UserID={UserID} 成功", nUserID);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存使用者權限 UserID={UserID} 時發生錯誤", nUserID);
            return false;
        }
    }

    #region ModbusPoints 相關方法

    /// <summary>
    /// 儲存 ModbusPoints 到資料庫 (批量處理，採用先刪後插入策略)
    /// </summary>
    /// <param name="nCoordinatorId">Coordinator ID</param>
    /// <param name="pointList">ModbusPoint 清單</param>
    /// <returns>成功處理的點位數量</returns>
    public async Task<int> SaveModbusPointsAsync(int nCoordinatorId, IEnumerable<ModbusPointModel> pointList)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        var points = pointList.ToList();
        if (!points.Any())
        {
            _logger.LogWarning("ModbusPoints 清單為空，跳過儲存作業");
            return 0;
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // 使用交易確保資料一致性
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Step 1: 只刪除此 Coordinator 對應的 SID 範圍
                // SID 格式: {nDatabaseId*65536 + nModbusId*256 + 1}-S{N}
                // 每個 Coordinator 的數字前綴落在 [nCoordinatorId*65536, (nCoordinatorId+1)*65536) 範圍內
                var szDeleteSql = @"
                    DELETE FROM ModbusPoints
                    WHERE CAST(SUBSTRING(SID, 1, CHARINDEX('-S', SID) - 1) AS BIGINT)
                          BETWEEN @MinSidValue AND @MaxSidValue";

                var nDeletedCount = await connection.ExecuteAsync(szDeleteSql, new
                {
                    MinSidValue = (long)nCoordinatorId * 65536,
                    MaxSidValue = (long)(nCoordinatorId + 1) * 65536 - 1
                }, transaction: transaction);

                _logger.LogInformation("已清空 ModbusPoints 表，刪除 {Count} 個舊點位", nDeletedCount);

                // Step 2: 批量插入新點位
                const string szInsertSql = @"
                    INSERT INTO ModbusPoints
                    (SID, Name, Address, DataType, Ratio, Unit, Min, Max, DeviceGroup)
                    VALUES
                    (@SID, @Name, @Address, @DataType, @Ratio, @Unit, @Min, @Max, @DeviceGroup)";

                var nInsertedCount = 0;
                var nValidationFailures = 0;
                var nInsertFailures = 0;

                foreach (var point in points)
                {
                    if (point.Validate())
                    {
                        try
                        {
                            await connection.ExecuteAsync(szInsertSql, new
                            {
                                SID = point.szSID,
                                Name = point.szName,
                                Address = point.szAddress,
                                DataType = point.szDataType,
                                Ratio = point.fRatio,
                                Unit = point.szUnit,
                                Min = point.fMin,
                                Max = point.fMax,
                                DeviceGroup = point.szDeviceGroup
                            }, transaction);

                            nInsertedCount++;
                        }
                        catch (Exception ex)
                        {
                            nInsertFailures++;
                            _logger.LogError(ex, "插入點位失敗: SID={SID}, Name={Name}, Address={Address}", 
                                point.szSID, point.szName, point.szAddress);
                        }
                    }
                    else
                    {
                        nValidationFailures++;
                        _logger.LogWarning("點位驗證失敗，跳過: SID={SID}, Name={Name}, Address={Address}, DataType={DataType}", 
                            point.szSID, point.szName, point.szAddress, point.szDataType);
                    }
                }

                // 提交交易
                await transaction.CommitAsync();

                _logger.LogInformation("成功儲存 Coordinator {CoordinatorId} 的點位到 ModbusPoints 表: " +
                    "總共={Total}, 成功={Success}, 驗證失敗={ValidationFail}, 插入失敗={InsertFail}", 
                    nCoordinatorId, points.Count, nInsertedCount, nValidationFailures, nInsertFailures);

                if (nValidationFailures > 0 || nInsertFailures > 0)
                {
                    _logger.LogWarning("發現問題: {Total} 個點位中有 {Problems} 個未成功插入", 
                        points.Count, nValidationFailures + nInsertFailures);
                }

                return nInsertedCount;
            }
            catch
            {
                // 發生錯誤時回滾交易
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存 ModbusPoints 時發生錯誤: CoordinatorId={CoordinatorId}", nCoordinatorId);
            return 0;
        }
    }

    /// <summary>
    /// 查詢指定 Coordinator 的所有 ModbusPoints
    /// </summary>
    /// <param name="nCoordinatorId">Coordinator ID</param>
    /// <returns>ModbusPoint 清單</returns>
    public async Task<IEnumerable<ModbusPointModel>> GetModbusPointsByCoordinatorAsync(int nCoordinatorId)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT SID, Name, Address, DataType, Ratio, Unit, Min, Max
                FROM ModbusPoints 
                WHERE SID LIKE @SidPrefix
                ORDER BY SID";

            // SID 格式為 XXX-SN，計算該 DatabaseId 對應的 SID 前綴
            var szSidPrefix = $"{nCoordinatorId * 65536}%";
            
            var result = await connection.QueryAsync<ModbusPointModel>(szSql, 
                new { SidPrefix = szSidPrefix });

            _logger.LogDebug("查詢 Coordinator {CoordinatorId} 的點位完成: 筆數={Count}", 
                nCoordinatorId, result.Count());

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 ModbusPoints 時發生錯誤: CoordinatorId={CoordinatorId}", nCoordinatorId);
            return Enumerable.Empty<ModbusPointModel>();
        }
    }

    /// <summary>
    /// 取得所有已設定的 ModbusPoints (用於Web初始化全點位清單)
    /// </summary>
    public async Task<IEnumerable<ModbusPointModel>> GetAllModbusPointsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    SID         AS szSID,
                    Name        AS szName,
                    Address     AS szAddress,
                    DataType    AS szDataType,
                    Ratio       AS fRatio,
                    Unit        AS szUnit,
                    Min         AS fMin,
                    Max         AS fMax,
                    DeviceGroup AS szDeviceGroup
                FROM ModbusPoints
                ORDER BY SID";

            var result = await connection.QueryAsync<ModbusPointModel>(szSql);

            _logger.LogDebug("查詢所有 ModbusPoints 完成: 筆數={Count}", result.Count());
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢所有 ModbusPoints 時發生錯誤");
            return Enumerable.Empty<ModbusPointModel>();
        }
    }

    #endregion

    /// <summary>
    /// 查詢 HistoryData 資料表中指定 SID 的歷史記錄
    /// </summary>
    public async Task<IEnumerable<HistoryDataModel>> GetHistoryTableDataAsync(
        string szSID, DateTime dtStartTime, DateTime dtEndTime, int nMaxRecords = 5000, int nIntervalMinutes = 0)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
        {
            await InitializeAsync();
        }

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            string szSql;
            if (nIntervalMinutes > 0)
            {
                // 取樣模式：對 Timestamp 按 N 分鐘整點對齊分 bucket，每 bucket 取最早一筆
                szSql = @"
                    WITH bucketed AS (
                        SELECT SID       AS szSID,
                               Value     AS fValue,
                               Quality   AS nQuality,
                               Timestamp AS dtTimestamp,
                               ROW_NUMBER() OVER (
                                   PARTITION BY DATEADD(MINUTE,
                                       (DATEDIFF(MINUTE, 0, Timestamp) / @IntervalMin) * @IntervalMin, 0)
                                   ORDER BY Timestamp ASC
                               ) AS rn
                        FROM HistoryData
                        WHERE SID = @SID
                          AND Timestamp BETWEEN @StartTime AND @EndTime
                    )
                    SELECT TOP (@MaxRecords) szSID, fValue, nQuality, dtTimestamp
                    FROM bucketed
                    WHERE rn = 1
                    ORDER BY dtTimestamp ASC";
            }
            else
            {
                szSql = @"
                    SELECT TOP (@MaxRecords)
                        SID       AS szSID,
                        Value     AS fValue,
                        Quality   AS nQuality,
                        Timestamp AS dtTimestamp
                    FROM HistoryData
                    WHERE SID = @SID
                      AND Timestamp BETWEEN @StartTime AND @EndTime
                    ORDER BY Timestamp ASC";
            }

            var result = await connection.QueryAsync<HistoryDataModel>(szSql, new
            {
                MaxRecords  = nMaxRecords,
                SID         = szSID,
                StartTime   = dtStartTime,
                EndTime     = dtEndTime,
                IntervalMin = nIntervalMinutes
            });

            _logger.LogDebug("查詢 HistoryData 完成: SID={SID}, 間隔={Interval}min, 筆數={Count}", szSID, nIntervalMinutes, result.Count());
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 HistoryData 時發生錯誤: SID={SID}", szSID);
            return Enumerable.Empty<HistoryDataModel>();
        }
    }

    #region 條件控制規則操作方法

    /// <summary>
    /// 取得所有條件控制規則
    /// </summary>
    public async Task<IEnumerable<ConditionControlRuleModel>> GetAllConditionControlRulesAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT Id,
                       ConditionPointSID AS szConditionPointSID,
                       Operator          AS nOperator,
                       ConditionValue    AS dConditionValue,
                       ControlPointSID   AS szControlPointSID,
                       ControlValue      AS dControlValue,
                       Remarks           AS szRemarks,
                       IsEnabled         AS isEnabled,
                       CreatedAt         AS dtCreatedAt
                FROM ConditionControlRules
                ORDER BY Id";

            var result = await connection.QueryAsync<ConditionControlRuleModel>(szSql);
            _logger.LogDebug("查詢 ConditionControlRules 完成: {Count} 筆", result.Count());
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 ConditionControlRules 時發生錯誤");
            return [];
        }
    }

    /// <summary>
    /// 全量覆寫條件控制規則（先清空再批次插入）
    /// </summary>
    public async Task<bool> SaveConditionControlRulesAsync(IEnumerable<ConditionControlRuleModel> rules)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        var ruleList = rules.ToList();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await connection.ExecuteAsync("DELETE FROM ConditionControlRules", transaction: transaction);

                const string szInsertSql = @"
                    INSERT INTO ConditionControlRules
                        (ConditionPointSID, Operator, ConditionValue, ControlPointSID, ControlValue, Remarks, IsEnabled)
                    VALUES
                        (@ConditionPointSID, @Operator, @ConditionValue, @ControlPointSID, @ControlValue, @Remarks, @IsEnabled)";

                foreach (var r in ruleList)
                {
                    await connection.ExecuteAsync(szInsertSql, new
                    {
                        ConditionPointSID = r.szConditionPointSID,
                        Operator          = r.nOperator,
                        ConditionValue    = r.dConditionValue,
                        ControlPointSID   = r.szControlPointSID,
                        ControlValue      = r.dControlValue,
                        Remarks           = r.szRemarks,
                        IsEnabled         = r.isEnabled
                    }, transaction);
                }

                await transaction.CommitAsync();
                _logger.LogInformation("儲存 ConditionControlRules 完成: {Count} 筆", ruleList.Count);
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存 ConditionControlRules 時發生錯誤");
            return false;
        }
    }

    #endregion

    #region ScadaDesign 相關方法

    /// <summary>
    /// 儲存畫面設計（先清除舊版 IsPublished，再插入新版 + 頁面資料）
    /// </summary>
    public async Task<bool> SaveDesignAsync(string szName, IEnumerable<ScadaDesignPageModel> pages)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        var pageList = pages.ToList();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Step 1: 將所有舊版標記為未發布
                await connection.ExecuteAsync(
                    "UPDATE ScadaDesign SET IsPublished = 0 WHERE IsPublished = 1",
                    transaction: transaction);

                // Step 2: 插入新版設計，取得 Id
                var nDesignId = await connection.QuerySingleAsync<int>(@"
                    INSERT INTO ScadaDesign (Name, IsPublished, SavedAt)
                    OUTPUT INSERTED.Id
                    VALUES (@Name, 1, GETDATE())",
                    new { Name = szName },
                    transaction: transaction);

                // Step 3: 批量插入頁面
                const string szPageSql = @"
                    INSERT INTO ScadaDesignPage
                        (DesignId, PageSid, ParentPageSid, SortOrder,
                         PageName, PageIcon, CanvasW, CanvasH,
                         BgFileName, BgDataUrl, WidgetStateJson)
                    VALUES
                        (@DesignId, @PageSid, @ParentPageSid, @SortOrder,
                         @PageName, @PageIcon, @CanvasW, @CanvasH,
                         @BgFileName, @BgDataUrl, @WidgetStateJson)";

                foreach (var page in pageList)
                {
                    await connection.ExecuteAsync(szPageSql, new
                    {
                        DesignId        = nDesignId,
                        PageSid         = page.szPageSid,
                        ParentPageSid   = page.szParentPageSid,
                        SortOrder       = page.nSortOrder,
                        PageName        = page.szPageName,
                        PageIcon        = page.szPageIcon,
                        CanvasW         = page.nCanvasW,
                        CanvasH         = page.nCanvasH,
                        BgFileName      = page.szBgFileName,
                        BgDataUrl       = page.szBgDataUrl,
                        WidgetStateJson = page.szWidgetStateJson
                    }, transaction: transaction);
                }

                await transaction.CommitAsync();
                _logger.LogInformation("SaveDesignAsync 完成：設計 Id={Id}，共 {Count} 頁", nDesignId, pageList.Count);
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveDesignAsync 時發生錯誤");
            return false;
        }
    }

    /// <summary>
    /// 讀取已發布的畫面設計（IsPublished=1）的所有頁面
    /// </summary>
    public async Task<IEnumerable<ScadaDesignPageModel>> LoadPublishedDesignAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            const string szSql = @"
                SELECT
                    p.PageSid         AS szPageSid,
                    p.ParentPageSid   AS szParentPageSid,
                    p.SortOrder       AS nSortOrder,
                    p.PageName        AS szPageName,
                    p.PageIcon        AS szPageIcon,
                    p.CanvasW         AS nCanvasW,
                    p.CanvasH         AS nCanvasH,
                    p.BgFileName      AS szBgFileName,
                    p.BgDataUrl       AS szBgDataUrl,
                    p.WidgetStateJson AS szWidgetStateJson
                FROM ScadaDesignPage p
                INNER JOIN ScadaDesign d ON p.DesignId = d.Id
                WHERE d.IsPublished = 1
                ORDER BY p.SortOrder";

            var pages = await connection.QueryAsync<ScadaDesignPageModel>(szSql);
            _logger.LogDebug("LoadPublishedDesignAsync 完成：共 {Count} 頁", pages.Count());
            return pages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadPublishedDesignAsync 時發生錯誤");
            return Enumerable.Empty<ScadaDesignPageModel>();
        }
    }

    /// <summary>
    /// 依 Hash 去重寫入 Designer 圖片資產（同內容只存一份；已存在則略過）。
    /// </summary>
    public async Task<bool> UpsertDesignAssetAsync(ScadaDesignAssetModel asset)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            // 內容定址：同 Hash 即同內容，已存在無需覆寫
            const string szSql = @"
                IF NOT EXISTS (SELECT 1 FROM ScadaDesignAsset WHERE Hash = @Hash)
                    INSERT INTO ScadaDesignAsset (Hash, ContentType, DataBase64, ByteSize, CreatedAt)
                    VALUES (@Hash, @ContentType, @DataBase64, @ByteSize, GETDATE())";
            await connection.ExecuteAsync(szSql, new
            {
                Hash        = asset.szHash,
                ContentType = asset.szContentType,
                DataBase64  = asset.szDataBase64,
                ByteSize    = asset.nByteSize
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertDesignAssetAsync 時發生錯誤（Hash={Hash}）", asset.szHash);
            return false;
        }
    }

    /// <summary>
    /// 依 Hash 讀取 Designer 圖片資產；不存在回傳 null。
    /// </summary>
    public async Task<ScadaDesignAssetModel?> GetDesignAssetAsync(string szHash)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            const string szSql = @"
                SELECT Hash        AS szHash,
                       ContentType AS szContentType,
                       DataBase64  AS szDataBase64,
                       ByteSize    AS nByteSize
                FROM ScadaDesignAsset
                WHERE Hash = @Hash";
            return await connection.QuerySingleOrDefaultAsync<ScadaDesignAssetModel>(szSql, new { Hash = szHash });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDesignAssetAsync 時發生錯誤（Hash={Hash}）", szHash);
            return null;
        }
    }

    #endregion

    #region ManualControlValue

    /// <inheritdoc/>
    public async Task<bool> SaveManualControlValueAsync(string szSid, double dValue)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        const string sql = @"
            MERGE ManualControlValue AS tgt
            USING (SELECT @SID AS SID) AS src ON tgt.SID = src.SID
            WHEN MATCHED THEN UPDATE SET Value = @Value, IsAuto = 0, UpdatedAt = GETDATE()
            WHEN NOT MATCHED THEN INSERT (SID, Value, IsAuto, UpdatedAt) VALUES (@SID, @Value, 0, GETDATE());";
        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_szConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync(sql, new { SID = szSid, Value = dValue });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveManualControlValueAsync 失敗 SID={SID}", szSid);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetAutoControlAsync(string szSid)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        const string sql = @"
            MERGE ManualControlValue AS tgt
            USING (SELECT @SID AS SID) AS src ON tgt.SID = src.SID
            WHEN MATCHED THEN UPDATE SET IsAuto = 1, UpdatedAt = GETDATE()
            WHEN NOT MATCHED THEN INSERT (SID, Value, IsAuto, UpdatedAt) VALUES (@SID, 0, 1, GETDATE());";
        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_szConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync(sql, new { SID = szSid });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetAutoControlAsync 失敗 SID={SID}", szSid);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task EnsureManualControlEntryExistsAsync(string szSid)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        const string sql = @"
            IF NOT EXISTS (SELECT 1 FROM ManualControlValue WHERE SID = @SID)
                INSERT INTO ManualControlValue (SID, Value, IsAuto, UpdatedAt) VALUES (@SID, 0, 1, GETDATE())";
        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_szConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync(sql, new { SID = szSid });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureManualControlEntryExistsAsync 失敗 SID={SID}", szSid);
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, (double dValue, bool isAuto)>> LoadManualControlValuesAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        const string sql = "SELECT SID, Value, IsAuto FROM ManualControlValue";
        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(_szConnectionString);
            await conn.OpenAsync();
            var rows = await conn.QueryAsync<(string SID, double Value, bool IsAuto)>(sql);
            return rows.ToDictionary(r => r.SID, r => (r.Value, r.IsAuto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadManualControlValuesAsync 失敗");
            return new Dictionary<string, (double, bool)>();
        }
    }

    #endregion

    #region CalculatedPoints 計算點位

    /// <inheritdoc/>
    public async Task<IEnumerable<CalculatedPointModel>> GetAllCalculatedPointsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    SID             AS szSID,
                    Name            AS szName,
                    Unit            AS szUnit,
                    GroupName       AS szGroupName,
                    Formula         AS szFormula,
                    InputMappings   AS szInputMappings,
                    IsEnabled       AS isEnabled,
                    CreatedAt       AS dtCreatedAt,
                    UpdatedAt       AS dtUpdatedAt
                FROM CalculatedPoints
                ORDER BY SID";

            var result = await connection.QueryAsync<CalculatedPointModel>(szSql);
            _logger.LogDebug("查詢 CalculatedPoints 完成: {Count} 筆", result.Count());
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 CalculatedPoints 時發生錯誤");
            return Enumerable.Empty<CalculatedPointModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CreateCalculatedPointAsync(CalculatedPointModel model)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                INSERT INTO CalculatedPoints (SID, Name, Unit, GroupName, Formula, InputMappings, IsEnabled, CreatedAt)
                VALUES (@SID, @Name, @Unit, @GroupName, @Formula, @InputMappings, @IsEnabled, GETDATE())";

            await connection.ExecuteAsync(szSql, new
            {
                SID = model.szSID,
                Name = model.szName,
                Unit = model.szUnit,
                GroupName = model.szGroupName,
                Formula = model.szFormula,
                InputMappings = model.szInputMappings,
                IsEnabled = model.isEnabled
            });

            _logger.LogInformation("新增計算點位成功: SID={SID}, Name={Name}", model.szSID, model.szName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增計算點位失敗: SID={SID}", model.szSID);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateCalculatedPointAsync(CalculatedPointModel model)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                UPDATE CalculatedPoints
                SET Name = @Name, Unit = @Unit, GroupName = @GroupName,
                    Formula = @Formula, InputMappings = @InputMappings,
                    IsEnabled = @IsEnabled, UpdatedAt = GETDATE()
                WHERE SID = @SID";

            await connection.ExecuteAsync(szSql, new
            {
                SID = model.szSID,
                Name = model.szName,
                Unit = model.szUnit,
                GroupName = model.szGroupName,
                Formula = model.szFormula,
                InputMappings = model.szInputMappings,
                IsEnabled = model.isEnabled
            });

            _logger.LogInformation("更新計算點位成功: SID={SID}", model.szSID);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新計算點位失敗: SID={SID}", model.szSID);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteCalculatedPointAsync(string szSID)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            await connection.ExecuteAsync("DELETE FROM CalculatedPoints WHERE SID = @SID", new { SID = szSID });
            _logger.LogInformation("刪除計算點位成功: SID={SID}", szSID);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除計算點位失敗: SID={SID}", szSID);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<int> GetCalculatedPointMaxIndexAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // 從現有 SID (CALC-S{N}) 中取最大的 N
            const string szSql = @"
                SELECT ISNULL(MAX(CAST(SUBSTRING(SID, 7, LEN(SID) - 6) AS INT)), 0)
                FROM CalculatedPoints
                WHERE SID LIKE 'CALC-S%'";

            var nMaxIndex = await connection.QuerySingleAsync<int>(szSql);
            return nMaxIndex;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得計算點位最大索引失敗");
            return 0;
        }
    }

    #endregion

    #region DB 來源 Coordinator / Points / LatestData

    /// <inheritdoc/>
    public async Task<IEnumerable<DbCoordinatorModel>> GetAllDbCoordinatorsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    Id,
                    Name            AS szName,
                    PollingInterval AS nPollingInterval,
                    ConnectTimeout  AS nConnectTimeout,
                    MonitorEnabled  AS isMonitorEnabled,
                    CreatedAt       AS dtCreatedAt
                FROM DBCoordinator
                ORDER BY Id";

            return await connection.QueryAsync<DbCoordinatorModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 DBCoordinator 時發生錯誤");
            return Enumerable.Empty<DbCoordinatorModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<DbPointModel>> GetAllDbPointsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    SID            AS szSID,
                    CoordinatorId  AS nCoordinatorId,
                    Sequence       AS nSequence,
                    Name           AS szName,
                    Unit           AS szUnit,
                    [Min]          AS fMin,
                    [Max]          AS fMax
                FROM DBPoints
                ORDER BY CoordinatorId, Sequence";

            return await connection.QueryAsync<DbPointModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 DBPoints 時發生錯誤");
            return Enumerable.Empty<DbPointModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<DbPointModel>> GetDbPointsByCoordinatorIdAsync(int nCoordinatorId)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    SID            AS szSID,
                    CoordinatorId  AS nCoordinatorId,
                    Sequence       AS nSequence,
                    Name           AS szName,
                    Unit           AS szUnit,
                    [Min]          AS fMin,
                    [Max]          AS fMax
                FROM DBPoints
                WHERE CoordinatorId = @CoordinatorId
                ORDER BY Sequence";

            return await connection.QueryAsync<DbPointModel>(szSql, new { CoordinatorId = nCoordinatorId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 Coordinator {Id} 的 DBPoints 時發生錯誤", nCoordinatorId);
            return Enumerable.Empty<DbPointModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<(int nId, int nPollingInterval, int nConnectTimeout, bool isMonitorEnabled)> SaveDbCoordinatorAsync(DbCoordinatorModel coordinator)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();

        // UPSERT by Name — 同名 sheet 永遠拿到同一個 Id
        const string szCheckSql = "SELECT Id, PollingInterval, ConnectTimeout, MonitorEnabled FROM DBCoordinator WHERE Name = @Name";
        var existing = await connection.QuerySingleOrDefaultAsync<(int Id, int PollingInterval, int ConnectTimeout, bool MonitorEnabled)?>(
            szCheckSql, new { Name = coordinator.szName });

        if (existing.HasValue)
        {
            const string szUpdateSql = @"
                UPDATE DBCoordinator
                SET PollingInterval = @PollingInterval,
                    ConnectTimeout  = @ConnectTimeout,
                    MonitorEnabled  = @MonitorEnabled
                WHERE Id = @Id";
            await connection.ExecuteAsync(szUpdateSql, new
            {
                PollingInterval = coordinator.nPollingInterval,
                ConnectTimeout = coordinator.nConnectTimeout,
                MonitorEnabled = coordinator.isMonitorEnabled,
                Id = existing.Value.Id
            });
            _logger.LogDebug("更新 DBCoordinator: Id={Id}, Name={Name}, Interval={Interval}, Timeout={Timeout}, Enabled={Enabled}",
                existing.Value.Id, coordinator.szName, coordinator.nPollingInterval, coordinator.nConnectTimeout, coordinator.isMonitorEnabled);
            return (existing.Value.Id, coordinator.nPollingInterval, coordinator.nConnectTimeout, coordinator.isMonitorEnabled);
        }

        const string szInsertSql = @"
            INSERT INTO DBCoordinator (Name, PollingInterval, ConnectTimeout, MonitorEnabled)
            VALUES (@Name, @PollingInterval, @ConnectTimeout, @MonitorEnabled);
            SELECT CAST(SCOPE_IDENTITY() AS INT);";
        var nNewId = await connection.QuerySingleAsync<int>(szInsertSql, new
        {
            Name = coordinator.szName,
            PollingInterval = coordinator.nPollingInterval,
            ConnectTimeout = coordinator.nConnectTimeout,
            MonitorEnabled = coordinator.isMonitorEnabled
        });
        _logger.LogInformation("新增 DBCoordinator: Id={Id}, Name={Name}", nNewId, coordinator.szName);
        return (nNewId, coordinator.nPollingInterval, coordinator.nConnectTimeout, coordinator.isMonitorEnabled);
    }

    /// <inheritdoc/>
    public async Task<int> SaveDbPointsAsync(int nCoordinatorId, IEnumerable<DbPointModel> points)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        var pointList = points.ToList();
        if (pointList.Count == 0)
        {
            _logger.LogDebug("Coordinator {Id} 無 DB 點位，跳過儲存", nCoordinatorId);
            // 仍要清空舊資料，避免遺留
        }

        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await connection.ExecuteAsync(
                "DELETE FROM DBPoints WHERE CoordinatorId = @CoordinatorId",
                new { CoordinatorId = nCoordinatorId },
                transaction: transaction);

            const string szInsertSql = @"
                INSERT INTO DBPoints (SID, CoordinatorId, Sequence, Name, Unit, [Min], [Max])
                VALUES (@SID, @CoordinatorId, @Sequence, @Name, @Unit, @Min, @Max)";

            // DBLatestData 預先 seed：沒 row 就寫一筆 Value=0 / Quality=1；已存在則保留外部既有值
            // Quality 語意（26.9.2 起）：只有寫入端「明確寫 0」才是 Bad；seed 給 1，
            // 外部系統 / 手動 UPDATE 只改 Value 不碰 Quality 也不會被判 Bad
            const string szSeedLatestSql = @"
                INSERT INTO DBLatestData (SID, Value, Timestamp, Quality)
                SELECT @SID, 0, GETDATE(), 1
                WHERE NOT EXISTS (SELECT 1 FROM DBLatestData WHERE SID = @SID)";

            var nInserted = 0;
            var nSeeded = 0;
            foreach (var p in pointList)
            {
                if (!p.Validate())
                {
                    _logger.LogWarning("DB 點位驗證失敗，跳過: SID={SID}, Name={Name}", p.szSID, p.szName);
                    continue;
                }

                await connection.ExecuteAsync(szInsertSql, new
                {
                    SID = p.szSID,
                    CoordinatorId = nCoordinatorId,
                    Sequence = p.nSequence,
                    Name = p.szName,
                    Unit = p.szUnit ?? string.Empty,
                    Min = p.fMin,
                    Max = p.fMax
                }, transaction);
                nInserted++;

                var nAffected = await connection.ExecuteAsync(szSeedLatestSql,
                    new { SID = p.szSID }, transaction);
                if (nAffected > 0) nSeeded++;
            }

            await transaction.CommitAsync();
            _logger.LogInformation("DBPoints 全量覆寫完成: CoordinatorId={Id}, 寫入={Count}, DBLatestData seed={Seeded}",
                nCoordinatorId, nInserted, nSeeded);
            return nInserted;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "儲存 DBPoints 失敗: CoordinatorId={Id}", nCoordinatorId);
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<LatestDataModel>> GetDbLatestDataByPrefixAsync(string szSidPrefix, int nCommandTimeoutMs = 1000)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        // 注意：例外不在此 swallow，往上拋給 caller — caller（DbCommunicationService、
        // MqttRealtimeSubscriberService）需要分辨「讀失敗」vs「無資料」以決定是否寫 Bad quality
        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();

        // Quality NULL 視為 Good（=1）：只有寫入端明確寫 0 才是 Bad（見 DbCommunicationService 品質規則）
        const string szSql = @"
            SELECT
                SID                          AS szSID,
                ISNULL(Value, 0)             AS fValue,
                ISNULL(Timestamp, GETDATE()) AS dtTimestamp,
                ISNULL(Quality, 1)           AS nQuality
            FROM DBLatestData
            WHERE SID LIKE @Prefix";

        // ADO.NET CommandTimeout 單位是秒，向上取整；最小 1 秒
        var nTimeoutSec = Math.Max(1, (int)Math.Ceiling(nCommandTimeoutMs / 1000.0));
        return await connection.QueryAsync<LatestDataModel>(
            new CommandDefinition(szSql, new { Prefix = szSidPrefix + "%" }, commandTimeout: nTimeoutSec));
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateDbLatestDataAsync(string szSid, double dValue)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            // 1) 取得 Min/Max 做範圍驗證（同時驗證 SID 存在）
            const string szRangeSql = "SELECT [Min] AS fMin, [Max] AS fMax FROM DBPoints WHERE SID = @Sid";
            var range = await connection.QuerySingleOrDefaultAsync<(float fMin, float fMax)?>(
                szRangeSql, new { Sid = szSid });

            if (!range.HasValue)
            {
                _logger.LogWarning("[DBLatestData 寫入] SID 不存在於 DBPoints: {SID}", szSid);
                return false;
            }

            if (dValue < range.Value.fMin || dValue > range.Value.fMax)
            {
                _logger.LogWarning("[DBLatestData 寫入] 值超出範圍 reject: SID={SID} Value={Value} Min={Min} Max={Max}",
                    szSid, dValue, range.Value.fMin, range.Value.fMax);
                return false;
            }

            // 2) UPDATE DBLatestData
            const string szUpdateSql = @"
                UPDATE DBLatestData
                SET Value = @Value,
                    Timestamp = GETDATE(),
                    Quality = 1
                WHERE SID = @Sid";

            var nAffected = await connection.ExecuteAsync(szUpdateSql, new { Sid = szSid, Value = dValue });

            if (nAffected <= 0)
            {
                _logger.LogWarning("[DBLatestData 寫入] DBLatestData 無對應 SID 列（DBPoints 有但 DBLatestData 缺）: {SID}", szSid);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DBLatestData 寫入] 寫入失敗: SID={SID} Value={Value}", szSid, dValue);
            return false;
        }
    }

    #endregion

    #region OPC UA 來源 Coordinator / Points

    /// <inheritdoc/>
    public async Task<IEnumerable<OpcUaCoordinatorModel>> GetAllOpcUaCoordinatorsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    Id,
                    Name            AS szName,
                    EndpointUrl     AS szEndpointUrl,
                    Username        AS szUsername,
                    Password        AS szPassword,
                    PollingInterval AS nPollingInterval,
                    ConnectTimeout  AS nConnectTimeout,
                    MonitorEnabled  AS isMonitorEnabled,
                    CreatedAt       AS dtCreatedAt
                FROM OpcUaCoordinator
                ORDER BY Id";

            return await connection.QueryAsync<OpcUaCoordinatorModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 OpcUaCoordinator 時發生錯誤");
            return Enumerable.Empty<OpcUaCoordinatorModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<OpcUaPointModel>> GetAllOpcUaPointsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    SID            AS szSID,
                    CoordinatorId  AS nCoordinatorId,
                    DeviceName     AS szDeviceName,
                    Sequence       AS nSequence,
                    Name           AS szName,
                    TagName        AS szTagName,
                    ControlType    AS szControlType,
                    Ratio          AS fRatio,
                    Unit           AS szUnit,
                    [Min]          AS fMin,
                    [Max]          AS fMax
                FROM OpcUaPoints
                ORDER BY CoordinatorId, Sequence";

            return await connection.QueryAsync<OpcUaPointModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 OpcUaPoints 時發生錯誤");
            return Enumerable.Empty<OpcUaPointModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<OpcUaPointModel>> GetOpcUaPointsByCoordinatorIdAsync(int nCoordinatorId)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();

            const string szSql = @"
                SELECT
                    SID            AS szSID,
                    CoordinatorId  AS nCoordinatorId,
                    DeviceName     AS szDeviceName,
                    Sequence       AS nSequence,
                    Name           AS szName,
                    TagName        AS szTagName,
                    ControlType    AS szControlType,
                    Ratio          AS fRatio,
                    Unit           AS szUnit,
                    [Min]          AS fMin,
                    [Max]          AS fMax
                FROM OpcUaPoints
                WHERE CoordinatorId = @CoordinatorId
                ORDER BY Sequence";

            return await connection.QueryAsync<OpcUaPointModel>(szSql, new { CoordinatorId = nCoordinatorId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查詢 Coordinator {Id} 的 OpcUaPoints 時發生錯誤", nCoordinatorId);
            return Enumerable.Empty<OpcUaPointModel>();
        }
    }

    /// <inheritdoc/>
    public async Task<int> SaveOpcUaCoordinatorAsync(OpcUaCoordinatorModel coordinator)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();

        // UPSERT by Name — 同名 JSON 檔永遠拿到同一個 Id（SID 前綴 OPC{Id}- 依賴此值）
        const string szCheckSql = "SELECT Id FROM OpcUaCoordinator WHERE Name = @Name";
        var nExistingId = await connection.QuerySingleOrDefaultAsync<int?>(
            szCheckSql, new { Name = coordinator.szName });

        if (nExistingId.HasValue)
        {
            const string szUpdateSql = @"
                UPDATE OpcUaCoordinator
                SET EndpointUrl     = @EndpointUrl,
                    Username        = @Username,
                    Password        = @Password,
                    PollingInterval = @PollingInterval,
                    ConnectTimeout  = @ConnectTimeout,
                    MonitorEnabled  = @MonitorEnabled
                WHERE Id = @Id";
            await connection.ExecuteAsync(szUpdateSql, new
            {
                EndpointUrl = coordinator.szEndpointUrl,
                Username = coordinator.szUsername,
                Password = coordinator.szPassword,
                PollingInterval = coordinator.nPollingInterval,
                ConnectTimeout = coordinator.nConnectTimeout,
                MonitorEnabled = coordinator.isMonitorEnabled,
                Id = nExistingId.Value
            });
            _logger.LogDebug("更新 OpcUaCoordinator: Id={Id}, Name={Name}, Endpoint={Endpoint}",
                nExistingId.Value, coordinator.szName, coordinator.szEndpointUrl);
            return nExistingId.Value;
        }

        const string szInsertSql = @"
            INSERT INTO OpcUaCoordinator (Name, EndpointUrl, Username, Password, PollingInterval, ConnectTimeout, MonitorEnabled)
            VALUES (@Name, @EndpointUrl, @Username, @Password, @PollingInterval, @ConnectTimeout, @MonitorEnabled);
            SELECT CAST(SCOPE_IDENTITY() AS INT);";
        var nNewId = await connection.QuerySingleAsync<int>(szInsertSql, new
        {
            Name = coordinator.szName,
            EndpointUrl = coordinator.szEndpointUrl,
            Username = coordinator.szUsername,
            Password = coordinator.szPassword,
            PollingInterval = coordinator.nPollingInterval,
            ConnectTimeout = coordinator.nConnectTimeout,
            MonitorEnabled = coordinator.isMonitorEnabled
        });
        _logger.LogInformation("新增 OpcUaCoordinator: Id={Id}, Name={Name}", nNewId, coordinator.szName);
        return nNewId;
    }

    /// <inheritdoc/>
    public async Task<int> SaveOpcUaPointsAsync(int nCoordinatorId, IEnumerable<OpcUaPointModel> points)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        var pointList = points.ToList();

        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await connection.ExecuteAsync(
                "DELETE FROM OpcUaPoints WHERE CoordinatorId = @CoordinatorId",
                new { CoordinatorId = nCoordinatorId },
                transaction: transaction);

            const string szInsertSql = @"
                INSERT INTO OpcUaPoints (SID, CoordinatorId, DeviceName, Sequence, Name, TagName, ControlType, Ratio, Unit, [Min], [Max])
                VALUES (@SID, @CoordinatorId, @DeviceName, @Sequence, @Name, @TagName, @ControlType, @Ratio, @Unit, @Min, @Max)";

            var nInserted = 0;
            foreach (var p in pointList)
            {
                if (!p.Validate())
                {
                    _logger.LogWarning("OPC UA 點位驗證失敗，跳過: SID={SID}, Name={Name}", p.szSID, p.szName);
                    continue;
                }

                await connection.ExecuteAsync(szInsertSql, new
                {
                    SID = p.szSID,
                    CoordinatorId = nCoordinatorId,
                    DeviceName = p.szDeviceName ?? string.Empty,
                    Sequence = p.nSequence,
                    Name = p.szName,
                    TagName = p.szTagName,
                    ControlType = p.szControlType ?? string.Empty,
                    Ratio = p.fRatio,
                    Unit = p.szUnit ?? string.Empty,
                    Min = p.fMin,
                    Max = p.fMax
                }, transaction);
                nInserted++;
            }

            await transaction.CommitAsync();
            _logger.LogInformation("OpcUaPoints 全量覆寫完成: CoordinatorId={Id}, 寫入={Count}", nCoordinatorId, nInserted);
            return nInserted;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "儲存 OpcUaPoints 失敗: CoordinatorId={Id}", nCoordinatorId);
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteOpcUaCoordinatorAsync(int nCoordinatorId)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        using var connection = new SqlConnection(_szConnectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await connection.ExecuteAsync(
                "DELETE FROM OpcUaPoints WHERE CoordinatorId = @Id",
                new { Id = nCoordinatorId }, transaction: transaction);

            var nAffected = await connection.ExecuteAsync(
                "DELETE FROM OpcUaCoordinator WHERE Id = @Id",
                new { Id = nCoordinatorId }, transaction: transaction);

            await transaction.CommitAsync();
            _logger.LogInformation("刪除 OpcUaCoordinator: Id={Id}, 受影響={Affected}", nCoordinatorId, nAffected);
            return nAffected > 0;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "刪除 OpcUaCoordinator 失敗: Id={Id}", nCoordinatorId);
            return false;
        }
    }

    #endregion

    #region 需量計算

    public async Task<IEnumerable<string>> GetDemandSidsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = "SELECT DISTINCT SID FROM EnergyCircuit WHERE IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''";
            return await connection.QueryAsync<string>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得需量 SID 清單失敗");
            return Enumerable.Empty<string>();
        }
    }

    public async Task UpsertDemandDataAsync(DemandDataModel model)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                MERGE DemandData AS target
                USING (VALUES (@SID, @Timestamp, @DemandKW, @WindowStart, @SampleCount, @Quality))
                    AS source (SID, Timestamp, DemandKW, WindowStart, SampleCount, Quality)
                ON target.SID = source.SID AND target.Timestamp = source.Timestamp
                WHEN MATCHED THEN
                    UPDATE SET DemandKW    = source.DemandKW,
                               WindowStart = source.WindowStart,
                               SampleCount = source.SampleCount,
                               Quality     = source.Quality
                WHEN NOT MATCHED THEN
                    INSERT (SID, Timestamp, DemandKW, WindowStart, SampleCount, Quality)
                    VALUES (source.SID, source.Timestamp, source.DemandKW, source.WindowStart, source.SampleCount, source.Quality);";

            await connection.ExecuteAsync(szSql, new
            {
                SID         = model.szSID,
                Timestamp   = model.dtTimestamp,
                DemandKW    = model.dDemandKW,
                WindowStart = model.dtWindowStart,
                SampleCount = model.nSampleCount,
                Quality     = model.nQuality
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UPSERT DemandData 失敗: SID={SID}", model.szSID);
        }
    }

    public async Task<IEnumerable<DemandCircuitModel>> GetCircuitsWithDemandAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                SELECT Name AS szName, SID AS szSID
                FROM EnergyCircuit
                WHERE IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''
                ORDER BY Name";
            return await connection.QueryAsync<DemandCircuitModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得需量迴路清單失敗");
            return Enumerable.Empty<DemandCircuitModel>();
        }
    }

    public async Task<IEnumerable<DemandCircuitModel>> GetDemandPointInfosAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            // 同 SID 被多迴路引用時取 MIN(Name)，SID 集合與 GetDemandSidsAsync 一致
            const string szSql = @"
                SELECT SID AS szSID, MIN(Name) AS szName
                FROM EnergyCircuit
                WHERE IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''
                GROUP BY SID
                ORDER BY MIN(Name)";
            return await connection.QueryAsync<DemandCircuitModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得需量虛擬點位資訊失敗");
            return Enumerable.Empty<DemandCircuitModel>();
        }
    }

    public async Task<TodayDemandModel?> GetTodayDemandAsync(string szDemandSID)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                SELECT
                    curr.dCurrentKW,
                    curr.dtTimestamp,
                    curr.nQuality,
                    mx.dMaxKW,
                    mx.dtMaxAt
                FROM (
                    SELECT TOP 1
                        CASE WHEN Quality = 1 THEN DemandKW ELSE NULL END AS dCurrentKW,
                        Timestamp  AS dtTimestamp,
                        Quality    AS nQuality
                    FROM DemandData
                    WHERE SID = @SID
                      AND CAST(Timestamp AS date) = CAST(GETDATE() AS date)
                    ORDER BY Timestamp DESC
                ) curr
                OUTER APPLY (
                    SELECT TOP 1
                        DemandKW  AS dMaxKW,
                        Timestamp AS dtMaxAt
                    FROM DemandData
                    WHERE SID = @SID
                      AND CAST(Timestamp AS date) = CAST(GETDATE() AS date)
                      AND Quality = 1
                    ORDER BY DemandKW DESC, Timestamp ASC
                ) mx";

            return await connection.QueryFirstOrDefaultAsync<TodayDemandModel>(szSql, new { SID = szDemandSID });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得今日需量失敗: SID={SID}", szDemandSID);
            return null;
        }
    }

    public async Task<IEnumerable<DemandTrendPoint>> GetTodayDemandTrendAsync(string szDemandSID)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                SELECT Timestamp AS dtTimestamp, DemandKW AS dDemandKW, Quality AS nQuality
                FROM DemandData
                WHERE SID = @SID
                  AND CAST(Timestamp AS date) = CAST(GETDATE() AS date)
                ORDER BY Timestamp ASC";

            return await connection.QueryAsync<DemandTrendPoint>(szSql, new { SID = szDemandSID });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得今日需量趨勢失敗: SID={SID}", szDemandSID);
            return Enumerable.Empty<DemandTrendPoint>();
        }
    }

    public async Task<IEnumerable<DemandCircuitModel>> GetCircuitsForDemandAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                ;WITH Leaves AS (
                    SELECT Id FROM EnergyCircuit
                    WHERE IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''
                ),
                Ancestors AS (
                    SELECT Id, Name, ParentId FROM EnergyCircuit WHERE Id IN (SELECT Id FROM Leaves)
                    UNION ALL
                    SELECT e.Id, e.Name, e.ParentId
                    FROM EnergyCircuit e INNER JOIN Ancestors a ON e.Id = a.ParentId
                )
                SELECT DISTINCT Id AS nId, Name AS szName FROM Ancestors ORDER BY Name";
            return await connection.QueryAsync<DemandCircuitModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得需量迴路清單（含虛擬迴路）失敗");
            return Enumerable.Empty<DemandCircuitModel>();
        }
    }

    public async Task<TodayDemandModel?> GetTodayDemandByCircuitIdAsync(int nCircuitId)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                ;WITH Descendants AS (
                    SELECT Id, SID, IsDemandEnabled
                    FROM EnergyCircuit WHERE Id = @CircuitId
                    UNION ALL
                    SELECT t.Id, t.SID, t.IsDemandEnabled
                    FROM EnergyCircuit t INNER JOIN Descendants d ON t.ParentId = d.Id
                ),
                Sids AS (
                    SELECT SID FROM Descendants
                    WHERE IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''
                ),
                TodayAgg AS (
                    SELECT Timestamp,
                           SUM(DemandKW) AS TotalKW,
                           SUM(CASE WHEN Quality=1 THEN 1 ELSE 0 END) AS GoodCount,
                           COUNT(*) AS TotalCount
                    FROM DemandData
                    WHERE SID IN (SELECT SID FROM Sids)
                      AND CAST(Timestamp AS DATE) = CAST(GETDATE() AS DATE)
                    GROUP BY Timestamp
                )
                SELECT
                    (SELECT TOP 1 TotalKW   FROM TodayAgg ORDER BY Timestamp DESC) AS dCurrentKW,
                    (SELECT TOP 1 Timestamp FROM TodayAgg ORDER BY Timestamp DESC) AS dtTimestamp,
                    (SELECT TOP 1 CASE WHEN GoodCount = TotalCount THEN 1 ELSE 0 END
                                  FROM TodayAgg ORDER BY Timestamp DESC) AS nQuality,
                    (SELECT TOP 1 TotalKW   FROM TodayAgg ORDER BY TotalKW DESC, Timestamp ASC) AS dMaxKW,
                    (SELECT TOP 1 Timestamp FROM TodayAgg ORDER BY TotalKW DESC, Timestamp ASC) AS dtMaxAt";

            return await connection.QueryFirstOrDefaultAsync<TodayDemandModel>(szSql, new { CircuitId = nCircuitId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得今日需量失敗: CircuitId={CircuitId}", nCircuitId);
            return null;
        }
    }

    public async Task<IEnumerable<DemandTrendPoint>> GetTodayDemandTrendByCircuitIdAsync(int nCircuitId)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                ;WITH Descendants AS (
                    SELECT Id, SID, IsDemandEnabled
                    FROM EnergyCircuit WHERE Id = @CircuitId
                    UNION ALL
                    SELECT t.Id, t.SID, t.IsDemandEnabled
                    FROM EnergyCircuit t INNER JOIN Descendants d ON t.ParentId = d.Id
                ),
                Sids AS (
                    SELECT SID FROM Descendants
                    WHERE IsDemandEnabled = 1 AND SID IS NOT NULL AND SID <> ''
                )
                SELECT
                    Timestamp AS dtTimestamp,
                    SUM(DemandKW) AS dDemandKW,
                    CASE WHEN SUM(CASE WHEN Quality=1 THEN 1 ELSE 0 END) = COUNT(*) THEN 1 ELSE 0 END AS nQuality
                FROM DemandData
                WHERE SID IN (SELECT SID FROM Sids)
                  AND CAST(Timestamp AS DATE) = CAST(GETDATE() AS DATE)
                GROUP BY Timestamp
                ORDER BY Timestamp ASC";

            return await connection.QueryAsync<DemandTrendPoint>(szSql, new { CircuitId = nCircuitId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得今日需量趨勢失敗: CircuitId={CircuitId}", nCircuitId);
            return Enumerable.Empty<DemandTrendPoint>();
        }
    }

    #endregion

    #region 迴路用電虛擬點位（NRGD-/NRGM-{circuitId}）

    public async Task<IEnumerable<DemandCircuitModel>> GetAllEnergyCircuitInfosAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                SELECT Id AS nId, Name AS szName
                FROM EnergyCircuit
                ORDER BY Name";
            return await connection.QueryAsync<DemandCircuitModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得全部迴路清單失敗");
            return Enumerable.Empty<DemandCircuitModel>();
        }
    }

    public async Task<IEnumerable<EnergyCircuitNodeModel>> GetAllEnergyCircuitNodesAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                SELECT Id       AS nId,
                       ParentId AS nParentId,
                       Name     AS szName,
                       SID      AS szSID,
                       MaxKwh   AS dMaxKwh,
                       [Sign]   AS nSign
                FROM EnergyCircuit";
            return await connection.QueryAsync<EnergyCircuitNodeModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得 EnergyCircuit 全樹失敗");
            return Enumerable.Empty<EnergyCircuitNodeModel>();
        }
    }

    public async Task<(double? dDayStart, double? dMonthStart, double? dPeriodStart, double? dNow)> GetEnergyBoundaryValuesAsync(
        string szSID, DateTime dtDayStart, DateTime dtMonthStart, DateTime dtPeriodStart, DateTime dtNow, int nMaxStalenessHours)
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            // 四時點各取最近一筆（OUTER APPLY TOP 1 seek），語意同 EnergyLeafAggregator.GetBoundaryValuesAsync
            const string szSql = @"
                SELECT b.idx, ba.Value FROM (VALUES (0, @tDay), (1, @tMonth), (2, @tPeriod), (3, @tNow)) AS b(idx, BoundaryTime)
                OUTER APPLY (
                    SELECT TOP 1 Value FROM HistoryData WITH (NOLOCK)
                    WHERE  SID = @sid
                       AND Timestamp <= b.BoundaryTime
                       AND Timestamp >= DATEADD(HOUR, -@maxStalenessHours, b.BoundaryTime)
                       AND Quality = 1
                    ORDER BY Timestamp DESC
                ) ba
                ORDER BY b.idx";

            var rows = await connection.QueryAsync<(int idx, double? Value)>(szSql, new
            {
                sid = szSID,
                tDay = dtDayStart,
                tMonth = dtMonthStart,
                tPeriod = dtPeriodStart,
                tNow = dtNow,
                maxStalenessHours = nMaxStalenessHours
            });

            double? dDay = null, dMonth = null, dPeriod = null, dNowVal = null;
            foreach (var r in rows)
            {
                if (r.idx == 0) dDay = r.Value;
                else if (r.idx == 1) dMonth = r.Value;
                else if (r.idx == 2) dPeriod = r.Value;
                else if (r.idx == 3) dNowVal = r.Value;
            }
            return (dDay, dMonth, dPeriod, dNowVal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得迴路用電邊界值失敗: SID={SID}", szSID);
            return (null, null, null, null);
        }
    }

    public async Task<IEnumerable<BillingPeriodModel>> GetBillingPeriodRowsAsync()
    {
        if (string.IsNullOrEmpty(_szConnectionString))
            await InitializeAsync();

        try
        {
            using var connection = new SqlConnection(_szConnectionString);
            await connection.OpenAsync();
            const string szSql = @"
                SELECT PeriodYear AS nPeriodYear, PeriodMonth AS nPeriodMonth,
                       StartDate AS dtStartDate, EndDate AS dtEndDate, UpdatedAt AS dtUpdatedAt
                FROM   BillingPeriods";
            return await connection.QueryAsync<BillingPeriodModel>(szSql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取得電費月結週期 rows 失敗");
            return Enumerable.Empty<BillingPeriodModel>();
        }
    }

    #endregion

    /// <summary>
    /// 釋放資源
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _logger.LogInformation("SQL Server 資料存取服務已釋放資源");
        }
    }
}