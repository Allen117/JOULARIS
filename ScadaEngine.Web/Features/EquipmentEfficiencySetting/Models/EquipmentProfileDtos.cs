namespace ScadaEngine.Web.Features.EquipmentEfficiencySetting.Models;

/// <summary>設定頁 ViewModel</summary>
public class EquipmentEfficiencySettingViewModel
{
    /// <summary>初始 tab：chiller / compressor（?kind=）</summary>
    public string szInitialKind { get; set; } = "chiller";
}

/// <summary>單一角色綁定（Scale 不收 — 伺服端依單位重算）</summary>
public class EquipmentBindingRequest
{
    public string szRole { get; set; } = string.Empty;
    public string szSID { get; set; } = string.Empty;
    public string? szUnit { get; set; }
}

/// <summary>新增 / 更新一台設備（nId = 0 → 新增）</summary>
public class EquipmentProfileSaveRequest
{
    public int nId { get; set; }
    public string szKind { get; set; } = string.Empty;
    public string szName { get; set; } = string.Empty;
    public int nSortOrder { get; set; }
    public bool isEnabled { get; set; } = true;
    public double? dRatedOutput { get; set; }
    public double? dRatedPower { get; set; }
    public double? dMaxKwh { get; set; }
    public double? dSpecificHeat { get; set; }
    public double? dGoodThreshold { get; set; }
    public double? dFairThreshold { get; set; }
    public string? szDescription { get; set; }
    public List<EquipmentBindingRequest> aBindings { get; set; } = [];
}

/// <summary>掃描帶入：多台一次寫入</summary>
public class EquipmentBatchCreateRequest
{
    public List<EquipmentProfileSaveRequest> aProfiles { get; set; } = [];
}

/// <summary>回填歷史（nProfileId = null → 全部；日期 yyyy-MM-dd，含訖日）</summary>
public class EquipmentBackfillRequest
{
    public int? nProfileId { get; set; }
    public string szFrom { get; set; } = string.Empty;
    public string szTo { get; set; } = string.Empty;
}
