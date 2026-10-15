namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 設備效率診斷 — 設備層 hourly 預聚合（對應 EquipmentHourly 表）。
/// 一列 = 一台設備一小時：kWh 與產出（冰機 RT·h / 空壓機 m³）同一小時配對，
/// Web 期間效率 = Σ Kwh ÷ Σ OutputQty（能量加權）。
///
/// Quality 語意：
///   1 = kWh 與產出皆足量（計入期間效率分子分母與覆蓋率）
///   0 = kWh 足量但產出樣本不足（流量計 / 溫度計斷線）— OutputQty 為 null，期間效率不計
///   kWh 樣本不足 → 整列不寫（sparse，同 WaterLeafHourly）
/// 整小時停機且產出點缺值 → 視為「無可量測」，OutputQty=0、Quality=1（避免停機時段誤觸覆蓋率警示）
/// </summary>
public class EquipmentHourlyModel
{
    public int nProfileId { get; set; }
    /// <summary>小時起點（local time，與 HistoryData.Timestamp 同基準）</summary>
    public DateTime dtHourStart { get; set; }
    /// <summary>該小時耗電 kWh（KWH 累積點邊界相減優先，否則 AVG(kW) × 1h）</summary>
    public double dKwh { get; set; }
    /// <summary>該小時產出：冰機 RT·h（= AVG(分鐘 RT) × 1h）/ 空壓機 m³（= AVG(m³/min) × 60）；Quality=0 時 null</summary>
    public double? dOutputQty { get; set; }
    /// <summary>運轉時數 0~1（運轉樣本占比）</summary>
    public double dRunHours { get; set; }
    /// <summary>運轉期間平均負載率 %（停機整小時 → null）；期間負載分佈 bin 用</summary>
    public double? dAvgLoadPct { get; set; }
    /// <summary>空壓機加載時數 0~1（有 LOADED 點才有）</summary>
    public double? dLoadedHours { get; set; }
    /// <summary>空壓機運轉期間平均排氣壓力 bar（有 DISCHPRESS 點才有）</summary>
    public double? dAvgPressure { get; set; }
    /// <summary>產出有效樣本分鐘數（冰機 = 流量 + 兩溫度三者齊的分鐘；或 RT 點分鐘；空壓機 = 空氣量分鐘）</summary>
    public int nSampleCount { get; set; }
    public int nQuality { get; set; } = 1;
    public bool isBackfilled { get; set; }
    public DateTime dtCreatedAt { get; set; }
}
