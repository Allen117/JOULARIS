namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 產氣量表 — 葉子層 hourly 預聚合資料模型（對應 AirMeterLeafHourly 表）。
/// 一列 = 一個葉子 SID 在某個小時的產氣量增量（Nm³，已套 UnitScale 換算與 MaxVolume 溢位規則，未套 sign）。
/// Quality=0 表示「掉線事件 transition」（只缺一邊邊界值），DeltaNm3=0。
/// 兩邊都缺 → 不寫該列（sparse storage）。與 <see cref="GasMeterLeafHourlyModel"/> 對稱但各自獨立。
/// </summary>
public class AirMeterLeafHourlyModel
{
    public string szSID { get; set; } = string.Empty;

    /// <summary>小時起點（local time，與 HistoryData.Timestamp 同基準）</summary>
    public DateTime dtHourStart { get; set; }

    /// <summary>該小時產氣量增量（Nm³，已套 UnitScale 與溢位規則，未套 sign）。Quality=0 時恆為 0。</summary>
    public double dDeltaNm3 { get; set; }

    /// <summary>1=正常累計、0=掉線 transition（只缺一邊邊界）</summary>
    public int nQuality { get; set; } = 1;

    /// <summary>是否觸發 MaxVolume 溢位修正</summary>
    public bool isRolledOver { get; set; }

    public DateTime dtCreatedAt { get; set; }
}
