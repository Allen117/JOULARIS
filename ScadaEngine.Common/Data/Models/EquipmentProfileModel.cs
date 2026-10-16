namespace ScadaEngine.Common.Data.Models;

/// <summary>
/// 設備效率診斷 — 設備主檔（對應 EquipmentProfile 表）。
/// 一列 = 一台冰機或空壓機；角色點位綁定在 <see cref="aBindings"/>（EquipmentPointBinding 子表）。
/// 與空調水系統迴路（WaterCircuit）/ 自來水表 / 氣表三套迴路皆無關；冰機 RT 點位可同時被 WaterCircuit 葉子引用。
/// </summary>
public class EquipmentProfileModel
{
    public int nId { get; set; }
    /// <summary>chiller / compressor（見 Common.Equipment.EquipmentKind）</summary>
    public string szKind { get; set; } = string.Empty;
    public string szName { get; set; } = string.Empty;
    public int nSortOrder { get; set; }
    public bool isEnabled { get; set; } = true;
    /// <summary>額定產出：冰機 RT / 空壓機 m³/min</summary>
    public double? dRatedOutput { get; set; }
    /// <summary>額定電力 kW（停機判定 5% 門檻、空壓機負載率）</summary>
    public double? dRatedPower { get; set; }
    /// <summary>KWH 累積點溢位上限（同 EnergyCircuit.MaxKwh 語意）</summary>
    public double? dMaxKwh { get; set; }
    /// <summary>流量 + ΔT 算 RT 用的比熱 kJ/(kg·K)，null = 4.186（水）</summary>
    public double? dSpecificHeat { get; set; }
    /// <summary>主指標 ≤ Good → 優</summary>
    public double? dGoodThreshold { get; set; }
    /// <summary>主指標 ≤ Fair → 可，&gt; Fair → 差</summary>
    public double? dFairThreshold { get; set; }
    public string? szDescription { get; set; }
    /// <summary>空壓機控制型式 fixed / vsd / modulation（Common CompressorControlType）；null = 診斷時由功率分佈推定</summary>
    public string? szControlType { get; set; }
    /// <summary>空壓機 VSD 最低負載 %；null = 診斷退回觀測值</summary>
    public double? dMinLoadPct { get; set; }
    /// <summary>空壓機銘牌最高工作壓力 bar</summary>
    public double? dMaxPressure { get; set; }
    /// <summary>空壓機馬達每小時最多啟動次數</summary>
    public int? nMaxStartsPerHour { get; set; }
    public DateTime dtCreatedAt { get; set; }
    public DateTime? dtUpdatedAt { get; set; }

    /// <summary>角色點位綁定（不對應欄位，由 Service 另查子表填入）</summary>
    public List<EquipmentPointBindingModel> aBindings { get; set; } = [];

    /// <summary>取某角色的綁定（無 → null）</summary>
    public EquipmentPointBindingModel? GetBinding(string szRole) =>
        aBindings.FirstOrDefault(b => string.Equals(b.szRole, szRole, StringComparison.Ordinal));
}

/// <summary>
/// 設備效率診斷 — 角色點位綁定（對應 EquipmentPointBinding 表，PK = ProfileId + Role）。
/// Scale = 原始值乘上後即為基準單位（見 Common.Equipment.EquipmentUnitScale）。
/// </summary>
public class EquipmentPointBindingModel
{
    public int nProfileId { get; set; }
    /// <summary>角色鍵（見 Common.Equipment.EquipmentRoles）</summary>
    public string szRole { get; set; } = string.Empty;
    public string szSID { get; set; } = string.Empty;
    /// <summary>使用者選的原始單位（顯示 / 回填下拉用）</summary>
    public string? szUnit { get; set; }
    public double dScale { get; set; } = 1;
    public DateTime? dtUpdatedAt { get; set; }
}
