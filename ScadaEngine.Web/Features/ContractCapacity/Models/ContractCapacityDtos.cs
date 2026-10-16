namespace ScadaEngine.Web.Features.ContractCapacity.Models;

/// <summary>契約容量最佳化頁 ViewModel（目前無需傳值，保留擴充）</summary>
public class ContractCapacityViewModel
{
}

/// <summary>回測查詢條件</summary>
public class ContractCapacityQuery
{
    /// <summary>計費迴路（EnergyCircuit.Id，須啟用需量）</summary>
    public int circuitId { get; set; }

    /// <summary>起始計費月 yyyy-MM（電費期別；未自訂期別時 = 曆月）</summary>
    public string from { get; set; } = string.Empty;

    /// <summary>結束計費月 yyyy-MM（含）</summary>
    public string to { get; set; } = string.Empty;

    /// <summary>保守建議取整單位 kW</summary>
    public int roundKw { get; set; } = 10;

    /// <summary>窗口完整率門檻 %（低於門檻的月份不可評估）</summary>
    public double completenessPct { get; set; } = 95;

    /// <summary>異常值上限 kW（超過視為電表跳動排除）；null = 不排除</summary>
    public double? maxValidKw { get; set; }
}

/// <summary>可選迴路（啟用需量計算的電表）</summary>
public class ContractCircuitOption
{
    public int nId { get; set; }
    public string szName { get; set; } = string.Empty;
    public bool isMainMeter { get; set; }
}

/// <summary>回測結果</summary>
public class ContractCapacityResult
{
    public bool isSuccess { get; set; }

    /// <summary>錯誤代碼：circuit_not_found / invalid_range / range_too_large</summary>
    public string? szError { get; set; }

    public int nCircuitId { get; set; }
    public string szCircuitName { get; set; } = string.Empty;
    public string szFrom { get; set; } = string.Empty;
    public string szTo { get; set; } = string.Empty;
    public int nRoundKw { get; set; }
    public double dCompletenessPct { get; set; }
    public double? dMaxValidKw { get; set; }

    /// <summary>最後一個計費月採用的電價方案 Id（前端以 i18n tariff.plan.{id} 顯示；自建方案帶 szPlanName）</summary>
    public string? szPlanId { get; set; }
    public string? szPlanName { get; set; }

    /// <summary>回測期間內採用過一個以上的方案</summary>
    public bool isMultiplePlans { get; set; }

    /// <summary>最後一個計費月有效的現行經常契約 kW；null = 未設定</summary>
    public double? dCurrentKw { get; set; }

    /// <summary>回測期間內現行經常契約有變更</summary>
    public bool isCurrentChanged { get; set; }

    public int nMonths { get; set; }
    public int nEvaluableMonths { get; set; }

    /// <summary>可評估月份的最高需量</summary>
    public double? dMaxDemandKw { get; set; }

    /// <summary>現行（逐月套用當時契約）— 未設定契約容量時為 null</summary>
    public ContractScenarioDto? current { get; set; }

    /// <summary>保守建議（回測期零超約）</summary>
    public ContractScenarioDto? safe { get; set; }

    /// <summary>最低成本點</summary>
    public ContractScenarioDto? optimum { get; set; }

    /// <summary>成本曲線平底區間（與最低點差 ≤ 1%）</summary>
    public int? nFlatLowKw { get; set; }
    public int? nFlatHighKw { get; set; }

    public List<ContractCurvePointDto> curve { get; set; } = [];
    public List<ContractMonthDto> months { get; set; } = [];
}

/// <summary>單一方案的回測期合計</summary>
public class ContractScenarioDto
{
    /// <summary>經常契約 kW（現行方案若逐月不同則為最後一個月的值）</summary>
    public double dKw { get; set; }
    public double dTotal { get; set; }
    public double dRegularBase { get; set; }
    public double dOtherBase { get; set; }
    public double dPenalty { get; set; }
    public int nOverMonths { get; set; }

    /// <summary>相較現行節省金額（現行未設定 → null）</summary>
    public double? dSaving { get; set; }
    public double? dSavingPct { get; set; }
}

public class ContractCurvePointDto
{
    public double dKw { get; set; }
    public double dBase { get; set; }
    public double dPenalty { get; set; }
}

/// <summary>逐月明細</summary>
public class ContractMonthDto
{
    public string szLabel { get; set; } = string.Empty;
    public string szStart { get; set; } = string.Empty;
    public string szEnd { get; set; } = string.Empty;
    public double dSummerShare { get; set; }

    /// <summary>經常契約單價（依夏月日數加權，顯示用）</summary>
    public double dRegularPrice { get; set; }

    /// <summary>尖峰時間（非時間電價 / 電動車 = 全時段）固定 15 分鐘窗口最高需量</summary>
    public double? dMaxDemandKw { get; set; }

    /// <summary>最高需量窗口起點（yyyy-MM-dd HH:mm）</summary>
    public string? szPeakAt { get; set; }

    public int nExpectedWindows { get; set; }
    public int nGoodWindows { get; set; }
    public int nExcludedWindows { get; set; }
    public double dCompletenessPct { get; set; }

    public bool isEvaluable { get; set; }

    /// <summary>不可評估原因：no_plan / no_demand_tariff / low_completeness / no_data</summary>
    public string? szReason { get; set; }

    public double? dCurrentKw { get; set; }
    public ContractMonthCostDto? cur { get; set; }
    public ContractMonthCostDto? safe { get; set; }
    public ContractMonthCostDto? optimum { get; set; }
}

public class ContractMonthCostDto
{
    public double dOverKw { get; set; }
    public double dPenalty { get; set; }
    public double dTotal { get; set; }
}
