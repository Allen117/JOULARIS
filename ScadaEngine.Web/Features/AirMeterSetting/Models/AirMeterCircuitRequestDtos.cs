namespace ScadaEngine.Web.Features.AirMeterSetting.Models;

/// <summary>新增產氣量表迴路節點請求</summary>
public class CreateAirMeterCircuitDto
{
    public int? parentId { get; set; }
    public string name { get; set; } = "新迴路";
    public string? sid { get; set; }
    /// <summary>點位原始單位 → Nm³ 換算係數；伺服器存檔時依點位單位重新定案（Nm³/m³=1 / L=0.001）</summary>
    public double unitScale { get; set; } = 1.0;
    /// <summary>產氣量表累積最大值（以點位原始單位計），溢位/歸零判定用；留空表示不處理溢位</summary>
    public double? maxVolume { get; set; }
    /// <summary>對父貢獻方向：+1 / -1，預設 +1。根節點伺服器端會強制覆寫為 +1</summary>
    public int sign { get; set; } = 1;
    public string? description { get; set; }
}

/// <summary>更新產氣量表迴路節點請求</summary>
public class UpdateAirMeterCircuitDto
{
    public string name { get; set; } = string.Empty;
    public string? sid { get; set; }
    /// <summary>點位原始單位 → Nm³ 換算係數；伺服器存檔時依點位單位重新定案（Nm³/m³=1 / L=0.001）</summary>
    public double unitScale { get; set; } = 1.0;
    /// <summary>產氣量表累積最大值（以點位原始單位計），溢位/歸零判定用；留空表示不處理溢位</summary>
    public double? maxVolume { get; set; }
    /// <summary>對父貢獻方向：+1 / -1。根節點伺服器端會強制覆寫為 +1</summary>
    public int sign { get; set; } = 1;
    public string? description { get; set; }
}

/// <summary>單筆排序資訊（拖曳完成後整批送回）</summary>
public class AirMeterCircuitSortDto
{
    public int id { get; set; }
    public int? parentId { get; set; }
    public int sortOrder { get; set; }
}

/// <summary>產氣量點位下拉清單項目</summary>
public class AirMeterSidOptionDto
{
    public string sid { get; set; } = string.Empty;
    public string name { get; set; } = string.Empty;
    public string unit { get; set; } = string.Empty;
    /// <summary>該點位單位 → Nm³ 換算係數（Nm³/m³ 系=1、L 系=0.001）</summary>
    public double unitScale { get; set; } = 1.0;
    public string source { get; set; } = string.Empty; // "Modbus" / "Calculated" / "DB"
    public string coordName { get; set; } = string.Empty; // 通訊設備層：協調器名 / 計算群組名 / DB 來源名
    public string deviceName { get; set; } = string.Empty; // 子單元層：多 ID 協調器的子設備名，無子單元則為空字串
}
