using ScadaEngine.Common.Equipment.CompressorDiagnosis;

namespace ScadaEngine.Web.Features.CompressorEfficiency.Models;

/// <summary>
/// 空壓系統診斷場域設定（整份序列化存 CompressorDiagnosisSetting.SettingJson，單列 Id=1）。
/// v1 所有啟用中的空壓機 = 一個系統；設備層欄位（ControlType 等）在 EquipmentProfile，不在這裡。
/// </summary>
public class CompressorDiagnosisSettingModel
{
    /// <summary>末端最低需求壓力 bar（P / E3 規則；未填 → 待補場域資料）</summary>
    public double? dDemandPressBar { get; set; }

    /// <summary>系統級點位：角色（CompressorSystemRoles）→ 綁定；未綁的角色不出現</summary>
    public Dictionary<string, CompressorSystemPointBinding> sysPoints { get; set; } = [];

    /// <summary>生產週排程（L 規則；全空 → 待補場域資料）</summary>
    public List<CompressorProductionRange> aProductionSchedule { get; set; } = [];

    /// <summary>/HolidaySetting 假日整天視為非生產</summary>
    public bool isHolidayNonProduction { get; set; } = true;

    /// <summary>劣化基準期 yyyy-MM-dd（含訖日）；未設定 → E1 待設定</summary>
    public string? szBaselineFrom { get; set; }
    public string? szBaselineTo { get; set; }

    /// <summary>手填電價 NTD/kWh — 期間電費逐時表（ElectricityCostHourly）無資料時使用</summary>
    public double? dManualPrice { get; set; }

    /// <summary>參數覆寫（鍵見 DiagnosisParameters）</summary>
    public Dictionary<string, double> paramOverrides { get; set; } = [];

    /// <summary>工程師按「確認」沿用預設值的參數鍵（消除「依預設值」旗標）</summary>
    public List<string> aConfirmedParams { get; set; } = [];

    public DateTime? dtUpdatedAt { get; set; }
    public string? szUpdatedBy { get; set; }
}

/// <summary>系統級點位綁定（Scale 由伺服端依單位重算）</summary>
public class CompressorSystemPointBinding
{
    public string szSID { get; set; } = string.Empty;
    public string? szUnit { get; set; }
    public double dScale { get; set; } = 1;
}

/// <summary>生產時段一段：星期 0=日..6=六，"HH:mm"（訖可為 24:00）</summary>
public class CompressorProductionRange
{
    public int nDay { get; set; }
    public string szStart { get; set; } = "08:00";
    public string szEnd { get; set; } = "17:00";
}

/// <summary>Engineer 指定期間重算（日期 yyyy-MM-dd，含訖日）</summary>
public class CompressorDiagnosisRunRequest
{
    public string szFrom { get; set; } = string.Empty;
    public string szTo { get; set; } = string.Empty;
}

/// <summary>快照（API 回傳）</summary>
public class CompressorDiagnosisSnapshotDto
{
    public int nId { get; set; }
    public DateTime dtRunAt { get; set; }
    public bool isScheduled { get; set; }
    public string? szRunBy { get; set; }
    public CompressorDiagnosisResult result { get; set; } = new();
}
