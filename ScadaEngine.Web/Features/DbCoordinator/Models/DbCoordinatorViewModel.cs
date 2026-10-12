namespace ScadaEngine.Web.Features.DbCoordinator.Models;

/// <summary>
/// 「DB 來源」頁面 — 序列化用 DTO（避免 Razor 直接吃 Engine Model 造成 namespace 依賴擴散）
/// </summary>
public class DbCoordinatorListItemDto
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    public int pollingInterval { get; set; }
    public int connectTimeout { get; set; }
    public bool monitorEnabled { get; set; }

    /// <summary>true = DB 有此 Coordinator 但 DBPoint/{Name}.json 已不存在（被刪除）→ 頁面標示「設定檔已移除」</summary>
    public bool configMissing { get; set; }

    /// <summary>true = DBPoint/{Name}.json 已存在但 Engine 尚未載入（DB 無紀錄）→ 頁面列為「待 Engine 載入」，id=0、無點位</summary>
    public bool pendingEngine { get; set; }

    public List<DbPointListItemDto> points { get; set; } = new();
}

public class DbPointListItemDto
{
    public string sid { get; set; } = string.Empty;
    public int sequence { get; set; }
    public string name { get; set; } = string.Empty;
    public string unit { get; set; } = string.Empty;
    public float min { get; set; }
    public float max { get; set; }
}
