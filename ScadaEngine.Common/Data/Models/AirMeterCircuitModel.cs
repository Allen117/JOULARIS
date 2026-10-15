namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 產氣量表迴路階層 — 對應 AirMeterCircuit 資料表（空壓機累積式產氣量 Nm³ totalizer）。
/// 結構與 <see cref="GasMeterCircuitModel"/> 對稱但**完全獨立**：氣表保留給天然氣（綁氣費），
/// 壓縮空氣產氣量另走本體系，只供空壓機能源申報（比能耗 kWh/Nm³）使用。
/// </summary>
public class AirMeterCircuitModel
{
    public int nId { get; set; }
    public string szName { get; set; } = string.Empty;
    public int? nParentId { get; set; }
    public int nSortOrder { get; set; }
    public string? szSID { get; set; }

    /// <summary>點位原始單位 → Nm³ 的換算係數（Nm³/m³ 系=1、L 系=0.001）。綁定時依點位單位定案</summary>
    public double dUnitScale { get; set; } = 1.0;

    /// <summary>產氣量表累積最大值（以點位原始單位計），用於溢位/歸零判定。葉子才有意義</summary>
    public double? dMaxVolume { get; set; }

    /// <summary>對父節點的貢獻方向：+1=正向加入、-1=反向扣減。根節點固定 +1</summary>
    public int nSign { get; set; } = 1;

    public string? szDescription { get; set; }
    public DateTime dtCreatedAt { get; set; }
    public DateTime? dtUpdatedAt { get; set; }
}
