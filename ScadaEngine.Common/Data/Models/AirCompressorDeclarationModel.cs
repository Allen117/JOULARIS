namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 空壓機能源申報報表設定 — 對應 AirCompressorDeclarationReport 資料表。
/// 一列 = 一份申報報表，成對綁定用電迴路（EnergyCircuit）與產氣量表迴路（AirMeterCircuit）。
/// 與冰水版 <see cref="EnergyDeclarationModel"/> 同構但刻意分表（權限、單位各自獨立）。
/// </summary>
public class AirCompressorDeclarationModel
{
    public int nId { get; set; }
    public string szName { get; set; } = string.Empty;
    /// <summary>用電度數來源 — EnergyCircuit.Id</summary>
    public int nEnergyCircuitId { get; set; }
    /// <summary>產氣量來源 — AirMeterCircuit.Id</summary>
    public int nAirMeterCircuitId { get; set; }
    public int nSortOrder { get; set; }
    public string? szDescription { get; set; }
    public DateTime dtCreatedAt { get; set; }
    public DateTime? dtUpdatedAt { get; set; }
}
