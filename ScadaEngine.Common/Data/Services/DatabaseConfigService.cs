using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Common.Data.Services;

/// <summary>
/// 資料庫配置服務，負責載入和管理資料庫設定檔案
/// 此為共用服務，可供 Engine、Web 和 Algorithm 專案使用
/// </summary>
public class DatabaseConfigService
{
    private readonly ILogger<DatabaseConfigService> _logger;
    private readonly string _szConfigPath;
    private readonly DbSettingLocation? _location;

    /// <summary>
    /// 建構函式 — 直接指定路徑（測試或特殊情境用；一般 app 請走 <see cref="DatabaseConfigService(ILogger{DatabaseConfigService}, DbSettingLocation)"/>）
    /// </summary>
    /// <param name="logger">日誌記錄器</param>
    /// <param name="szConfigPath">設定檔路徑（預設為 "./Setting/dbSetting.json"）</param>
    public DatabaseConfigService(ILogger<DatabaseConfigService> logger, string szConfigPath = "./Setting/dbSetting.json")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _szConfigPath = szConfigPath;
    }

    /// <summary>
    /// 建構函式 — 接受 <see cref="DbSettingLocator.Resolve()"/> 的解析結果（Engine / Web / ModbusServer 統一入口）。
    /// 建構時印一行「資料庫設定檔路徑 + 來源」供現場排查三個 app 是否讀同一份；四層都找不到時列出搜尋過的路徑並警告。
    /// </summary>
    /// <param name="logger">日誌記錄器</param>
    /// <param name="location">DbSettingLocator 解析結果</param>
    public DatabaseConfigService(ILogger<DatabaseConfigService> logger, DbSettingLocation location)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _location = location ?? throw new ArgumentNullException(nameof(location));

        if (location.szPath != null)
        {
            _szConfigPath = location.szPath;
            _logger.LogInformation("資料庫設定檔路徑: {ConfigPath}（來源: {Source}）", location.szPath, location.szSourceLabel);
        }
        else
        {
            // 保留第 3 層（本機 Setting）作為 _szConfigPath，讓 LoadConfigAsync 的「檔案不存在」警告有具體路徑可印
            _szConfigPath = location.szSearchedPaths.FirstOrDefault(p => p.EndsWith(Path.Combine("Setting", DbSettingLocator.FileName), StringComparison.OrdinalIgnoreCase))
                            ?? location.szSearchedPaths.LastOrDefault()
                            ?? "./Setting/dbSetting.json";
            _logger.LogWarning("找不到 {FileName}，將使用預設配置。已依序搜尋: {Searched}",
                DbSettingLocator.FileName, string.Join(" ; ", location.szSearchedPaths));
        }
    }

    /// <summary>實際使用的設定檔路徑（log / 診斷用）</summary>
    public string szConfigPath => _szConfigPath;

    /// <summary>路徑解析結果（以字串路徑建構時為 null）</summary>
    public DbSettingLocation? Location => _location;

    /// <summary>
    /// 載入資料庫配置設定
    /// </summary>
    /// <returns>資料庫配置模型，載入失敗時回傳預設配置</returns>
    public async Task<DatabaseConfigModel> LoadConfigAsync()
    {
        try
        {
            if (!File.Exists(_szConfigPath))
            {
                _logger.LogWarning("資料庫設定檔案不存在: {ConfigPath}，使用預設配置", _szConfigPath);
                return CreateDefaultConfig();
            }

            var szJsonContent = await File.ReadAllTextAsync(_szConfigPath);
            
            if (string.IsNullOrWhiteSpace(szJsonContent))
            {
                _logger.LogWarning("資料庫設定檔案為空，使用預設配置");
                return CreateDefaultConfig();
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
                // 移除 PropertyNamingPolicy，使用 JsonPropertyName 屬性直接對應
            };

            var dbConfig = JsonSerializer.Deserialize<DatabaseConfigModel>(szJsonContent, options);
            
            if (dbConfig == null)
            {
                _logger.LogError("無法解析資料庫設定檔案，使用預設配置");
                return CreateDefaultConfig();
            }

            // 驗證必要參數
            if (dbConfig.IsValid())
            {
                _logger.LogInformation("成功載入資料庫配置: Server={DatabaseAddress}, Database={DataBaseName}, Account={DataBaseAccount}", 
                    dbConfig.szDatabaseAddress, 
                    dbConfig.szDataBaseName, 
                    dbConfig.szDataBaseAccount);
                return dbConfig;
            }
            else
            {
                _logger.LogWarning("資料庫配置驗證失敗，使用預設配置");
                return CreateDefaultConfig();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "解析資料庫設定檔案時發生 JSON 格式錯誤");
            return CreateDefaultConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入資料庫設定檔案時發生錯誤");
            return CreateDefaultConfig();
        }
    }

    /// <summary>
    /// 建立預設資料庫配置
    /// </summary>
    /// <returns>預設資料庫配置模型</returns>
    private DatabaseConfigModel CreateDefaultConfig()
    {
        return new DatabaseConfigModel
        {
            szDatabaseAddress = "127.0.0.1",
            szDataBaseName = "wsnCsharp",
            szDataBaseAccount = "wsn1",
            szDataBasePassword = "wsn2"
        };
    }

    /// <summary>
    /// 取得連線字串
    /// </summary>
    /// <returns>資料庫連線字串</returns>
    public async Task<string> GetConnectionStringAsync()
    {
        var config = await LoadConfigAsync();
        return config.BuildConnectionString();
    }
}