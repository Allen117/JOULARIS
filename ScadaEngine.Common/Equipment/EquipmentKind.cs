namespace ScadaEngine.Common.Equipment;

/// <summary>
/// 設備效率診斷 — 設備種類（chiller / compressor）與每類的角色清單、必要角色、預設門檻。
/// 兩類設備的差別只在本檔的表格常數 + <see cref="EquipmentMetricCalculator"/> 的指標公式，
/// 設定頁 / 掃描 / 聚合 / 診斷頁 / 卡片全部共用（以 Kind 字串區分）。
/// </summary>
public static class EquipmentKind
{
    public const string Chiller = "chiller";
    public const string Compressor = "compressor";

    public static readonly string[] All = [Chiller, Compressor];

    public static bool IsValid(string? szKind) =>
        szKind == Chiller || szKind == Compressor;

    /// <summary>冰機角色（順序 = 設定頁顯示順序）</summary>
    public static readonly string[] ChillerRoles =
    [
        EquipmentRoles.KW, EquipmentRoles.KWH,
        EquipmentRoles.FLOW, EquipmentRoles.CHWS, EquipmentRoles.CHWR, EquipmentRoles.RT,
        EquipmentRoles.CDWS, EquipmentRoles.CDWR,
        EquipmentRoles.EVAPT, EquipmentRoles.CONDT,
        EquipmentRoles.LOAD, EquipmentRoles.RUN,
    ];

    /// <summary>空壓機角色（順序 = 設定頁顯示順序）</summary>
    public static readonly string[] CompressorRoles =
    [
        EquipmentRoles.KW, EquipmentRoles.KWH,
        EquipmentRoles.AIRFLOW, EquipmentRoles.DISCHPRESS, EquipmentRoles.DISCHTEMP,
        EquipmentRoles.LOADED, EquipmentRoles.LOAD, EquipmentRoles.RUN,
    ];

    public static IReadOnlyList<string> RolesOf(string szKind) =>
        szKind == Compressor ? CompressorRoles : ChillerRoles;

    /// <summary>角色是否屬於該設備種類</summary>
    public static bool HasRole(string szKind, string szRole) => RolesOf(szKind).Contains(szRole);

    /// <summary>
    /// 手動門檻預設值（主指標 ≤ Good 優、≤ Fair 可、&gt; Fair 差）— 僅供起點，應依銘牌 / 驗收報告調整。
    /// 冰機 0.560 / 0.644 = 90.1 水冷離心 ≥300RT 定頻標準值 × (1, 1.15)，僅在切「手動」時帶入；
    /// 冰機預設走「依標準」，門檻由 <see cref="ChillerEfficiencyStandard.Resolve"/> 依機型查表。
    /// </summary>
    public static (double dGood, double dFair) DefaultThresholds(string szKind) =>
        szKind == Compressor ? (7.0, 9.0) : (0.560, 0.644);

    /// <summary>
    /// 必要角色是否齊備（可存檔 / 可帶入判定）。
    /// 電力：KW 或 KWH 擇一。產出：冰機 = FLOW + CHWS + CHWR 三者齊（主路徑）或 RT（備選）；空壓機 = AIRFLOW。
    /// 回傳缺少的必要項（空 = 齊備）；項目以「角色鍵或 'KW|KWH'、'FLOW+CHWS+CHWR|RT'」表示，由前端翻譯。
    /// </summary>
    public static List<string> MissingRequired(string szKind, ICollection<string> boundRoles)
    {
        var missing = new List<string>();
        if (!boundRoles.Contains(EquipmentRoles.KW) && !boundRoles.Contains(EquipmentRoles.KWH))
            missing.Add("KW|KWH");

        if (szKind == Compressor)
        {
            if (!boundRoles.Contains(EquipmentRoles.AIRFLOW)) missing.Add(EquipmentRoles.AIRFLOW);
        }
        else
        {
            var hasFlowPath = boundRoles.Contains(EquipmentRoles.FLOW)
                && boundRoles.Contains(EquipmentRoles.CHWS)
                && boundRoles.Contains(EquipmentRoles.CHWR);
            if (!hasFlowPath && !boundRoles.Contains(EquipmentRoles.RT))
                missing.Add("FLOW+CHWS+CHWR|RT");
        }
        return missing;
    }
}
