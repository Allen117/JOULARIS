using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Common.Data.Models;
using ScadaEngine.Engine.Models;

namespace ScadaEngine.Engine.Data.Interfaces;

/// <summary>
/// 資料持久化存取介面，定義資料庫操作標準
/// </summary>
public interface IDataRepository
{
    /// <summary>
    /// 測試資料庫連線是否正常
    /// </summary>
    /// <returns>連線正常回傳 true，失敗回傳 false</returns>
    Task<bool> TestConnectionAsync();

    /// <summary>
    /// 查詢指定時間範圍的歷史資料
    /// </summary>
    /// <param name="szSID">點位識別碼</param>
    /// <param name="dtStartTime">開始時間</param>
    /// <param name="dtEndTime">結束時間</param>
    /// <returns>歷史資料清單</returns>
    Task<IEnumerable<RealtimeDataModel>> GetHistoryDataAsync(string szSID, DateTime dtStartTime, DateTime dtEndTime);

    /// <summary>
    /// 儲存歷史資料至 HistoryData 資料表
    /// </summary>
    /// <param name="historyDataList">歷史資料清單</param>
    /// <returns>成功儲存的資料筆數</returns>
    Task<int> SaveHistoryDataAsync(IEnumerable<HistoryDataModel> historyDataList);

    /// <summary>
    /// 儲存單筆歷史資料至 HistoryData 資料表
    /// </summary>
    /// <param name="historyData">歷史資料</param>
    /// <returns>儲存成功回傳 true，失敗回傳 false</returns>
    Task<bool> SaveHistoryDataAsync(HistoryDataModel historyData);

    /// <summary>
    /// 儲存最新資料至 LatestData 資料表 (INSERT or UPDATE)
    /// </summary>
    /// <param name="latestDataList">最新資料清單</param>
    /// <returns>成功儲存的資料筆數</returns>
    Task<int> SaveLatestDataAsync(IEnumerable<LatestDataModel> latestDataList);

    /// <summary>
    /// 儲存單筆最新資料至 LatestData 資料表 (INSERT or UPDATE)
    /// </summary>
    /// <param name="latestData">最新資料</param>
    /// <returns>儲存成功回傳 true，失敗回傳 false</returns>
    Task<bool> SaveLatestDataAsync(LatestDataModel latestData);

    /// <summary>
    /// 取得最新的即時資料 (用於Web監控介面)
    /// </summary>
    /// <param name="nLimit">限制筆數</param>
    /// <returns>最新資料清單</returns>
    Task<IEnumerable<LatestDataModel>> GetLatestDataAsync(int nLimit = 100);

    /// <summary>
    /// 取得最新資料的時間戳記
    /// </summary>
    /// <returns>最新時間戳記</returns>
    Task<DateTime> GetLatestTimestampAsync();

    /// <summary>
    /// 取得總點位數量
    /// </summary>
    /// <returns>點位總數</returns>
    Task<int> GetTotalTagCountAsync();

    /// <summary>
    /// 取得 Users 資料表中的使用者數量
    /// </summary>
    /// <returns>使用者總數</returns>
    Task<int> GetUserCountAsync();

    /// <summary>
    /// 取得 Users 資料表中啟用的 Admin 或 Engineer 角色使用者數量（first-run /Setup 守衛用：任一存在即不再引導建立 Admin）
    /// </summary>
    /// <returns>啟用的 Admin + Engineer 使用者總數</returns>
    Task<int> GetAdminCountAsync();

    /// <summary>
    /// 驗證使用者帳號密碼 (密碼以 SHA256 hex 儲存於 PasswordHash 欄位)
    /// </summary>
    /// <param name="szUsername">使用者名稱</param>
    /// <param name="szPassword">明文密碼</param>
    /// <returns>驗證成功回傳 true</returns>
    Task<bool> ValidateUserAsync(string szUsername, string szPassword);

    /// <summary>
    /// 查詢 first-run setup 是否已完成（SystemSettings.SetupCompleted='1'）。
    /// 表尚未建立或查無值時回傳 false（視為未完成）。用於「一次性、不自動重開」的管理者初始化流程。
    /// </summary>
    Task<bool> IsSetupCompletedAsync();

    /// <summary>
    /// 標記 first-run setup 已完成（UPSERT SystemSettings.SetupCompleted='1'）。
    /// 表不存在時會就地建立，確保全新 DB 也能寫入。
    /// </summary>
    Task MarkSetupCompletedAsync();

    /// <summary>
    /// 取得所有已設定的 ModbusPoints (用於Web初始化全點位清單)
    /// </summary>
    /// <returns>所有 ModbusPoint 清單</returns>
    Task<IEnumerable<ModbusPointModel>> GetAllModbusPointsAsync();

    /// <summary>
    /// 取得 ModbusCoordinator 資料表的所有設備清單
    /// </summary>
    /// <returns>所有 Coordinator 清單</returns>
    Task<IEnumerable<CoordinatorModel>> GetAllCoordinatorsAsync();

    /// <summary>
    /// 更新 ModbusCoordinator 的 DeviceName 欄位
    /// </summary>
    /// <param name="nId">Coordinator Id</param>
    /// <param name="szDeviceName">裝置名稱（逗點分隔）</param>
    /// <returns>更新成功回傳 true</returns>
    Task<bool> UpdateDeviceNameAsync(int nId, string szDeviceName);

    /// <summary>
    /// Coordinator 改名（Id 不變 → SID 不變）：交易內確認新名未被其他列使用（不分大小寫）後
    /// UPDATE Name WHERE Id AND Name = 舊名
    /// </summary>
    /// <returns>Success / NameTaken（新名已存在）/ NotFound（Id + 舊名對不上）</returns>
    Task<CoordinatorRenameResult> RenameCoordinatorAsync(int nId, string szOldName, string szNewName);

    /// <summary>
    /// 查詢 HistoryData 資料表中指定 SID 的歷史記錄
    /// </summary>
    /// <param name="szSID">點位識別碼</param>
    /// <param name="dtStartTime">開始時間</param>
    /// <param name="dtEndTime">結束時間</param>
    /// <param name="nMaxRecords">最大筆數上限，預設 5000</param>
    /// <param name="nIntervalMinutes">取樣間隔（分鐘）。0 = 原始；>0 = 對整點對齊取每 bucket 首筆，例如 15 = 每 HH:00/15/30/45 取首筆</param>
    /// <returns>歷史資料清單 (時間升冪)</returns>
    Task<IEnumerable<HistoryDataModel>> GetHistoryTableDataAsync(string szSID, DateTime dtStartTime, DateTime dtEndTime, int nMaxRecords = 5000, int nIntervalMinutes = 0);

    /// <summary>
    /// 取得所有條件控制規則
    /// </summary>
    Task<IEnumerable<ConditionControlRuleModel>> GetAllConditionControlRulesAsync();

    /// <summary>
    /// 全量覆寫條件控制規則（先清空再批次插入）
    /// </summary>
    /// <param name="rules">規則清單</param>
    /// <returns>儲存成功回傳 true</returns>
    Task<bool> SaveConditionControlRulesAsync(IEnumerable<ConditionControlRuleModel> rules);

    /// <summary>
    /// 儲存畫面設計至資料庫（先將舊版 IsPublished 清零，再插入新版）
    /// </summary>
    /// <param name="szName">設計名稱</param>
    /// <param name="pages">頁面清單（已展平的樹狀結構）</param>
    /// <returns>儲存成功回傳 true</returns>
    Task<bool> SaveDesignAsync(string szName, IEnumerable<ScadaDesignPageModel> pages);

    /// <summary>
    /// 讀取已發布的畫面設計（IsPublished=1）的所有頁面
    /// </summary>
    /// <returns>頁面平坦清單；若無發布版本回傳空集合</returns>
    Task<IEnumerable<ScadaDesignPageModel>> LoadPublishedDesignAsync();

    /// <summary>
    /// 依 Hash 去重寫入 Designer 圖片資產（同內容只存一份；已存在則略過）。
    /// </summary>
    /// <param name="asset">資產內容（szHash 須為內容 SHA-256 hex）</param>
    /// <returns>成功回傳 true</returns>
    Task<bool> UpsertDesignAssetAsync(ScadaDesignAssetModel asset);

    /// <summary>
    /// 依 Hash 讀取 Designer 圖片資產；不存在回傳 null。
    /// </summary>
    Task<ScadaDesignAssetModel?> GetDesignAssetAsync(string szHash);

    /// <summary>
    /// 取得所有使用者帳號資料
    /// </summary>
    /// <returns>使用者清單</returns>
    Task<IEnumerable<UserModel>> GetAllUsersAsync();

    /// <summary>
    /// 依帳號名稱取得單一使用者（含 Role），登入時用
    /// </summary>
    Task<UserModel?> GetUserByUsernameAsync(string szUsername);

    /// <summary>
    /// 新增使用者帳號（密碼以 SHA256 hex 儲存）
    /// </summary>
    /// <param name="user">使用者資料（szPasswordHash 欄位傳入明文密碼，方法內自動雜湊）</param>
    /// <returns>新增成功回傳 true</returns>
    Task<bool> CreateUserAsync(UserModel user);

    /// <summary>
    /// 更新使用者帳號（不含密碼）
    /// </summary>
    Task<bool> UpdateUserAsync(UserModel user);

    /// <summary>
    /// 刪除使用者帳號（同時刪除 UserPermissions）
    /// </summary>
    Task<bool> DeleteUserAsync(int nUserID);

    /// <summary>
    /// 取得使用者權限 JSON
    /// </summary>
    Task<UserPermissionModel?> GetUserPermissionsAsync(int nUserID);

    /// <summary>
    /// 儲存使用者權限 JSON（UPSERT）
    /// </summary>
    Task<bool> SaveUserPermissionsAsync(int nUserID, string szPermissionJson);

    /// <summary>
    /// 更新使用者最後登入時間
    /// </summary>
    /// <param name="szUsername">使用者名稱</param>
    /// <returns>更新成功回傳 true</returns>
    Task<bool> UpdateLastLoginAsync(string szUsername);

    /// <summary>
    /// 儲存手動控制值（UPSERT ManualControlValue，IsAuto=0）
    /// </summary>
    /// <param name="szSid">點位 SID</param>
    /// <param name="dValue">手動控制值</param>
    /// <returns>儲存成功回傳 true</returns>
    Task<bool> SaveManualControlValueAsync(string szSid, double dValue);

    /// <summary>
    /// 將點位標記為自動控制（UPSERT ManualControlValue，IsAuto=1）
    /// </summary>
    /// <param name="szSid">點位 SID</param>
    /// <returns>儲存成功回傳 true</returns>
    Task<bool> SetAutoControlAsync(string szSid);

    /// <summary>
    /// 確保 ManualControlValue 存在該 SID 的記錄（不存在時 INSERT IsAuto=1, Value=0）
    /// 已存在的記錄不會被覆蓋。
    /// </summary>
    Task EnsureManualControlEntryExistsAsync(string szSid);

    /// <summary>
    /// 讀取所有手動控制值與自動模式狀態
    /// </summary>
    /// <returns>SID → (Value, IsAuto) 字典</returns>
    Task<Dictionary<string, (double dValue, bool isAuto)>> LoadManualControlValuesAsync();

    #region CalculatedPoints 計算點位

    /// <summary>
    /// 取得所有計算點位設定
    /// </summary>
    Task<IEnumerable<CalculatedPointModel>> GetAllCalculatedPointsAsync();

    /// <summary>
    /// 新增計算點位
    /// </summary>
    Task<bool> CreateCalculatedPointAsync(CalculatedPointModel model);

    /// <summary>
    /// 更新計算點位
    /// </summary>
    Task<bool> UpdateCalculatedPointAsync(CalculatedPointModel model);

    /// <summary>
    /// 刪除計算點位
    /// </summary>
    Task<bool> DeleteCalculatedPointAsync(string szSID);

    /// <summary>
    /// 取得計算點位數量（用於產生下一個 SID）
    /// </summary>
    Task<int> GetCalculatedPointMaxIndexAsync();

    #endregion

    #region DB 來源 Coordinator / Points / LatestData

    /// <summary>
    /// 取得所有 DB 來源 Coordinator
    /// </summary>
    Task<IEnumerable<DbCoordinatorModel>> GetAllDbCoordinatorsAsync();

    /// <summary>
    /// 取得所有 DB 來源點位（不限 Coordinator）
    /// </summary>
    Task<IEnumerable<DbPointModel>> GetAllDbPointsAsync();

    /// <summary>
    /// 取得指定 Coordinator 的所有 DB 點位
    /// </summary>
    Task<IEnumerable<DbPointModel>> GetDbPointsByCoordinatorIdAsync(int nCoordinatorId);

    /// <summary>
    /// UPSERT DB Coordinator（依 Name 比對；存在則更新 PollingInterval/ConnectTimeout/MonitorEnabled，不存在則新增）
    /// </summary>
    /// <returns>(Id, PollingInterval, ConnectTimeout, MonitorEnabled)</returns>
    Task<(int nId, int nPollingInterval, int nConnectTimeout, bool isMonitorEnabled)> SaveDbCoordinatorAsync(DbCoordinatorModel coordinator);

    /// <summary>
    /// 全量覆寫指定 Coordinator 的點位（先刪後插，Transaction 包裹）
    /// </summary>
    Task<int> SaveDbPointsAsync(int nCoordinatorId, IEnumerable<DbPointModel> points);

    /// <summary>
    /// 依 SID 前綴讀取 DBLatestData（polling 用）
    /// </summary>
    /// <param name="szSidPrefix">例 'DB1-'，會以 LIKE @prefix + '%' 比對</param>
    /// <param name="nCommandTimeoutMs">SqlCommand.CommandTimeout（毫秒，向上取整為秒）</param>
    Task<IEnumerable<ScadaEngine.Common.Data.Models.LatestDataModel>> GetDbLatestDataByPrefixAsync(string szSidPrefix, int nCommandTimeoutMs = 1000);

    /// <summary>
    /// 更新 DBLatestData（LogicFlow 寫入用）：先驗證 DBPoints.Min ≤ Value ≤ Max，
    /// 範圍超出 / SID 不存在 → log warning + 回 false（不丟例外）；通過則
    /// UPDATE Value=@val, Timestamp=GETDATE(), Quality=1。
    /// </summary>
    /// <param name="szSid">DB 點位 SID（例 DB1-S5）</param>
    /// <param name="dValue">寫入工程值</param>
    /// <returns>寫入成功（受影響列數 > 0）回傳 true</returns>
    Task<bool> UpdateDbLatestDataAsync(string szSid, double dValue);

    #endregion

    #region OPC UA 來源 Coordinator / Points

    /// <summary>
    /// 取得所有 OPC UA 來源 Coordinator
    /// </summary>
    Task<IEnumerable<OpcUaCoordinatorModel>> GetAllOpcUaCoordinatorsAsync();

    /// <summary>
    /// 取得所有 OPC UA 來源點位（不限 Coordinator）
    /// </summary>
    Task<IEnumerable<OpcUaPointModel>> GetAllOpcUaPointsAsync();

    /// <summary>
    /// 取得指定 Coordinator 的所有 OPC UA 點位
    /// </summary>
    Task<IEnumerable<OpcUaPointModel>> GetOpcUaPointsByCoordinatorIdAsync(int nCoordinatorId);

    /// <summary>
    /// UPSERT OPC UA Coordinator（依 Name 比對；存在則更新連線設定，不存在則新增）
    /// </summary>
    /// <returns>Coordinator Id（SID 前綴 OPC{Id}- 依賴此值）</returns>
    Task<int> SaveOpcUaCoordinatorAsync(OpcUaCoordinatorModel coordinator);

    /// <summary>
    /// 全量覆寫指定 Coordinator 的 OPC UA 點位（先刪後插，Transaction 包裹）
    /// </summary>
    Task<int> SaveOpcUaPointsAsync(int nCoordinatorId, IEnumerable<OpcUaPointModel> points);

    /// <summary>
    /// 刪除 OPC UA Coordinator 及其所有點位（Transaction 包裹）。
    /// LatestData / HistoryData 舊資料比照既有行為保留不清除。
    /// </summary>
    Task<bool> DeleteOpcUaCoordinatorAsync(int nCoordinatorId);

    #endregion

    #region 需量計算

    /// <summary>取得 EnergyCircuit 中所有 IsDemandEnabled=1 且 SID IS NOT NULL 的 kWh 點位 SID（不重複）</summary>
    Task<IEnumerable<string>> GetDemandSidsAsync();

    /// <summary>UPSERT 需量計算結果至 DemandData</summary>
    Task UpsertDemandDataAsync(DemandDataModel model);

    /// <summary>取得所有 IsDemandEnabled=1 的電表/迴路（Name + kWh SID），供 Web 下拉選單用</summary>
    Task<IEnumerable<DemandCircuitModel>> GetCircuitsWithDemandAsync();

    /// <summary>
    /// 取得需量虛擬點位資訊：啟用需量的 kWh SID（去重）＋對應電表/迴路名稱。
    /// 同一 kWh SID 被多個迴路引用時取名稱最小者（MIN(Name)），與 GetDemandSidsAsync 的 SID 集合一致。
    /// 供 Engine 組虛擬點位名稱（DMD-{kWhSID}）與 Web 各選點器清單共用。
    /// </summary>
    Task<IEnumerable<DemandCircuitModel>> GetDemandPointInfosAsync();

    /// <summary>取得指定 kWh SID 今日的即時需量（最新一筆）與今日最大值</summary>
    Task<TodayDemandModel?> GetTodayDemandAsync(string szDemandSID);

    /// <summary>取得指定 kWh SID 今日所有 Quality=1 的需量趨勢點（升冪），供折線圖用</summary>
    Task<IEnumerable<DemandTrendPoint>> GetTodayDemandTrendAsync(string szDemandSID);

    /// <summary>取得所有可作為 EMS 需量選單的迴路（葉子＋含有啟用後裔的虛擬迴路）</summary>
    Task<IEnumerable<DemandCircuitModel>> GetCircuitsForDemandAsync();

    /// <summary>取得指定迴路（遞迴展開後裔加總）今日的即時需量與今日最大值</summary>
    Task<TodayDemandModel?> GetTodayDemandByCircuitIdAsync(int nCircuitId);

    /// <summary>取得指定迴路（遞迴展開後裔加總）今日所有需量趨勢點（升冪），供折線圖用</summary>
    Task<IEnumerable<DemandTrendPoint>> GetTodayDemandTrendByCircuitIdAsync(int nCircuitId);

    #endregion

    #region 迴路用電虛擬點位（NRGD-/NRGM-{circuitId}）

    /// <summary>
    /// 取得全部 EnergyCircuit 迴路 Id + Name（含虛擬節點），
    /// 供 Engine 組虛擬點位名稱與 Web 警報選點器「迴路用電」清單共用。
    /// </summary>
    Task<IEnumerable<DemandCircuitModel>> GetAllEnergyCircuitInfosAsync();

    /// <summary>
    /// 一次撈出 EnergyCircuit 全樹節點（Id/ParentId/Name/SID/MaxKwh/Sign），
    /// 供記憶體展開各迴路葉子與有效 sign（語意同 Web EnergyCircuitService.GetLeavesUnderAsync）。
    /// </summary>
    Task<IEnumerable<EnergyCircuitNodeModel>> GetAllEnergyCircuitNodesAsync();

    /// <summary>
    /// 取指定 SID 在四個時點（日初 / 月初 / 期別起日 / 現在）各自的最近一筆 Quality=1 HistoryData 值，
    /// 套 staleness window（距 boundary 超過 nMaxStalenessHours 視為 null）。
    /// 語意同 EnergyLeafAggregator.GetBoundaryValuesAsync / Web WidgetAccumulationService meter 模式。
    /// </summary>
    Task<(double? dDayStart, double? dMonthStart, double? dPeriodStart, double? dNow)> GetEnergyBoundaryValuesAsync(
        string szSID, DateTime dtDayStart, DateTime dtMonthStart, DateTime dtPeriodStart, DateTime dtNow, int nMaxStalenessHours);

    /// <summary>取得 BillingPeriods 全部自訂期別 rows，供 Common BillingPeriodResolver 推導當前期別（NRGP- 本月電度期界）</summary>
    Task<IEnumerable<BillingPeriodModel>> GetBillingPeriodRowsAsync();

    #endregion

    /// <summary>
    /// 釋放資源
    /// </summary>
    void Dispose();
}