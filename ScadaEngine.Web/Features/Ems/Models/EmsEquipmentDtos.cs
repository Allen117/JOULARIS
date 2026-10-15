namespace ScadaEngine.Web.Features.Ems.Models;

// 設備效率診斷（冰機 / 空壓機）— /EMS/api/equipment/* 回傳 DTO。
// 主 / 次 / 第三指標依 kind 解讀：
//   chiller    → primary = kW/RT、secondary = COP、tertiary = EER；output = RT（即時）/ RT·h（期間）
//   compressor → primary = kW/(m³/min)、secondary = kW/100cfm、tertiary = 比能耗 kWh/m³（僅期間）；
//                output = m³/min（即時）/ m³（期間）

/// <summary>即時：單台設備</summary>
public class EquipmentRealtimeItemDto
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    /// <summary>Good / Fair / Poor / Off / NoData</summary>
    public string grade { get; set; } = "NoData";
    /// <summary>運轉狀態：true / false / null（無 RUN 點也無 kW 值 → 未知）</summary>
    public bool? isRunning { get; set; }
    /// <summary>至少一個已綁角色有即時值</summary>
    public bool hasData { get; set; }
    public double? primary { get; set; }
    public double? secondary { get; set; }
    public double? tertiary { get; set; }
    public double? kw { get; set; }
    /// <summary>冰機 RT（流量 + ΔT 優先）/ 空壓機 m³/min</summary>
    public double? output { get; set; }
    public double? loadPct { get; set; }
    public double? good { get; set; }
    public double? fair { get; set; }

    // 冰機輔助
    public double? chws { get; set; }
    public double? chwr { get; set; }
    public double? chwDeltaT { get; set; }
    public double? cdwDeltaT { get; set; }
    public double? evapApproach { get; set; }
    public double? condApproach { get; set; }
    /// <summary>直接 RT 點現值（同時綁流量路徑時僅供對照）</summary>
    public double? rtPoint { get; set; }
    /// <summary>冷量來源：flow / rt / null</summary>
    public string? outputSource { get; set; }

    // 空壓機輔助
    public double? pressBar { get; set; }
    public double? tempC { get; set; }
    public bool? isLoaded { get; set; }

    /// <summary>綁定角色中最新一筆即時值時間</summary>
    public DateTime? lastUpdate { get; set; }
}

/// <summary>即時：整類設備</summary>
public class EquipmentRealtimeDto
{
    public string kind { get; set; } = string.Empty;
    public List<EquipmentRealtimeItemDto> items { get; set; } = [];
    public DateTime serverTime { get; set; }
}

/// <summary>期間：總計 tiles</summary>
public class EquipmentPeriodTotalsDto
{
    public double kwh { get; set; }
    public double output { get; set; }
    public double? primary { get; set; }
    public double? secondary { get; set; }
    public double? tertiary { get; set; }
    public double runHours { get; set; }
    /// <summary>運轉時數加權平均負載率 %</summary>
    public double? avgLoadPct { get; set; }
    /// <summary>空壓機：加載時數 ÷ 運轉時數（0~1）</summary>
    public double? loadedRatio { get; set; }
    /// <summary>空壓機：運轉時數加權平均排氣壓力 bar</summary>
    public double? avgPressure { get; set; }
}

/// <summary>期間：單台趨勢線（每 bucket 一個主指標值，無資料為 null）</summary>
public class EquipmentTrendSeriesDto
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    public List<double?> values { get; set; } = [];
}

/// <summary>期間：負載率分佈（≤25 / ≤50 / ≤75 / &gt;75，對應 AHRI 550/590 IPLV 四點）</summary>
public class EquipmentLoadBinDto
{
    /// <summary>0..3</summary>
    public int bin { get; set; }
    public double hours { get; set; }
    public double kwh { get; set; }
    public double output { get; set; }
    /// <summary>占已分 bin 總 kWh 的 %</summary>
    public double kwhPct { get; set; }
    /// <summary>占已分 bin 總產出的 %</summary>
    public double outputPct { get; set; }
    public double? primary { get; set; }
}

/// <summary>期間：設備排名列</summary>
public class EquipmentRankDto
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    public double kwh { get; set; }
    public double output { get; set; }
    public double? primary { get; set; }
    public double? secondary { get; set; }
    public double runHours { get; set; }
    public double? avgLoadPct { get; set; }
    public string grade { get; set; } = "NoData";
    public double? good { get; set; }
    public double? fair { get; set; }
    public double coveragePct { get; set; }
}

/// <summary>期間：資料覆蓋率（沿用冷凍噸語意：未來時數不計入分母；報最差的那台）</summary>
public class EquipmentCoverageDto
{
    public int expectedHours { get; set; }
    public int actualHours { get; set; }
    public double coveragePct { get; set; } = 100;
    public int missingHours { get; set; }
    public string? worstName { get; set; }
    public int thresholdPct { get; set; }
    public bool isBelowThreshold { get; set; }
}

/// <summary>期間儀表板（/EMS/api/equipment/period）</summary>
public class EquipmentPeriodDto
{
    public string kind { get; set; } = string.Empty;
    public string granularity { get; set; } = string.Empty;
    public int deviceCount { get; set; }
    public List<string> labels { get; set; } = [];
    public EquipmentPeriodTotalsDto totals { get; set; } = new();
    public List<EquipmentTrendSeriesDto> series { get; set; } = [];
    /// <summary>全場加權線（每 bucket ΣkWh ÷ Σ產出）</summary>
    public List<double?> fleet { get; set; } = [];
    public List<EquipmentLoadBinDto> loadBins { get; set; } = [];
    public List<EquipmentRankDto> ranking { get; set; } = [];
    public EquipmentCoverageDto coverage { get; set; } = new();
}

/// <summary>EMS 首頁卡片（/EMS/api/equipment/card）</summary>
public class EquipmentCardDto
{
    public string kind { get; set; } = string.Empty;
    public int deviceCount { get; set; }
    public List<EquipmentRealtimeItemDto> items { get; set; } = [];
    /// <summary>本曆月 1 號 ~ 現在</summary>
    public EquipmentPeriodTotalsDto month { get; set; } = new();
    public string monthLabel { get; set; } = string.Empty;
}
