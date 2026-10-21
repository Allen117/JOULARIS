using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Data.Interfaces;
using ScadaEngine.Web.Services.HistoryStatistics;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 歷史趨勢查詢 — /api/history/data（逐點位）與 /api/history/export（整批）共用：
/// 點位名稱 / 單位解析、HistoryData 查詢（每點位筆數上限取自系統參數）、統計計算。
/// </summary>
public class HistoryQueryService
{
    private readonly IDataRepository _dataRepository;

    public HistoryQueryService(IDataRepository dataRepository)
    {
        _dataRepository = dataRepository;
    }

    /// <summary>
    /// 解析點位名稱與單位（Modbus → 計算點位 → DB 來源 → OPC UA → 需量虛擬點位），
    /// 各來源只在有對應 SID 時才載入一次。查無者不放進結果。
    /// </summary>
    public async Task<Dictionary<string, ModbusPointModel>> ResolvePointsAsync(
        IReadOnlyCollection<string> sids, string szDemandSuffix)
    {
        var result = new Dictionary<string, ModbusPointModel>(StringComparer.Ordinal);
        var wanted = sids.ToHashSet(StringComparer.Ordinal);

        foreach (var p in await _dataRepository.GetAllModbusPointsAsync())
            if (wanted.Contains(p.szSID)) result.TryAdd(p.szSID, p);

        List<string> Missing(string szPrefix) =>
            wanted.Where(s => !result.ContainsKey(s) && s.StartsWith(szPrefix, StringComparison.Ordinal)).ToList();

        if (Missing("CALC-").Count > 0)
            foreach (var c in await _dataRepository.GetAllCalculatedPointsAsync())
                if (wanted.Contains(c.szSID))
                    result.TryAdd(c.szSID, new ModbusPointModel { szSID = c.szSID, szName = c.szName, szUnit = c.szUnit });

        if (Missing("DB").Count > 0)
            foreach (var d in await _dataRepository.GetAllDbPointsAsync())
                if (wanted.Contains(d.szSID))
                    result.TryAdd(d.szSID, new ModbusPointModel { szSID = d.szSID, szName = d.szName, szUnit = d.szUnit ?? string.Empty });

        if (Missing("OPC").Count > 0)
            foreach (var o in await _dataRepository.GetAllOpcUaPointsAsync())
                if (wanted.Contains(o.szSID))
                    result.TryAdd(o.szSID, new ModbusPointModel { szSID = o.szSID, szName = o.szName, szUnit = o.szUnit ?? string.Empty });

        if (Missing("DMD-").Count > 0)
            foreach (var dm in await _dataRepository.GetDemandPointInfosAsync())
            {
                var szSid = $"DMD-{dm.szSID}";
                if (wanted.Contains(szSid))
                    result.TryAdd(szSid, new ModbusPointModel { szSID = szSid, szName = $"{dm.szName} {szDemandSuffix}", szUnit = "kW" });
            }

        return result;
    }

    /// <summary>查詢單一點位資料並計算統計</summary>
    public async Task<HistoryPointResult> QueryPointAsync(
        string szSID, ModbusPointModel? point, DateTime dtStart, DateTime dtEnd, int nInterval,
        int nMaxRecords, double dJitterTolerancePct)
    {
        var data = (await _dataRepository.GetHistoryTableDataAsync(szSID, dtStart, dtEnd, nMaxRecords, nInterval)).ToList();
        var szName = point?.szName ?? szSID;
        var szUnit = point?.szUnit ?? string.Empty;

        return new HistoryPointResult
        {
            szSID = szSID,
            szName = szName,
            szUnit = szUnit,
            data = data,
            isLimited = data.Count >= nMaxRecords,
            stats = HistoryStatisticsCalculator.Calculate(szName, szUnit, data, dtStart, dtEnd, nInterval, dJitterTolerancePct)
        };
    }

    /// <summary>
    /// 匯出預估儲存格數 = Σ每點位 min(期間分鐘數 ÷ max(間隔, 1), 每點位筆數上限)。
    /// 只用時間與間隔估算，不查 DB。
    /// </summary>
    public static long EstimateExportCells(int nPointCount, DateTime dtStart, DateTime dtEnd, int nInterval, int nMaxRecordsPerPoint)
    {
        var dMinutes = Math.Max(0, (dtEnd - dtStart).TotalMinutes);
        var nPerPoint = (long)Math.Ceiling(dMinutes / Math.Max(nInterval, 1));
        return nPointCount * Math.Min(nPerPoint, nMaxRecordsPerPoint);
    }
}
