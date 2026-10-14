namespace ScadaEngine.Web.Services;

/// <summary>
/// 自參照樹（EnergyCircuit / WaterCircuit / WaterMeterCircuit / GasMeterCircuit / LogicFlowTree）
/// 批次搬移 / 排序的後端防呆 — 把前端送來的 (Id, ParentId, SortOrder) 套到 DB 現況後，驗整棵樹：
///   1. 每個 Id 都存在
///   2. 每個 ParentId 都存在
///   3. 無循環（節點不能掛到自己或自己的子孫底下）
///   4. （可選）只允許單一根節點 — 自來水 / 天然氣體系的「全廠 = 樹根」語意
/// 違規丟 <see cref="TreeSortException"/>，Code 為前端 i18n 用的錯誤代碼。
/// </summary>
public static class TreeSortValidator
{
    public const string ErrMissingNode = "sort_missing_node";
    public const string ErrMissingParent = "sort_missing_parent";
    public const string ErrCycle = "sort_cycle";
    public const string ErrMultiRoot = "sort_multi_root";

    /// <summary>驗證並回傳套用後的 Id → ParentId 對照（供呼叫端判斷哪些列變成根節點）。</summary>
    public static Dictionary<int, int?> Validate(
        IEnumerable<(int nId, int? nParentId)> current,
        IEnumerable<(int nId, int? nParentId, int nSortOrder)> updates,
        bool allowMultiRoot)
    {
        var parentOf = current.ToDictionary(n => n.nId, n => n.nParentId);

        foreach (var (nId, nParentId, _) in updates)
        {
            if (!parentOf.ContainsKey(nId)) throw new TreeSortException(ErrMissingNode);
            parentOf[nId] = nParentId;
        }

        foreach (var (nId, nParentId) in parentOf)
        {
            if (nParentId.HasValue && !parentOf.ContainsKey(nParentId.Value))
                throw new TreeSortException(ErrMissingParent);
        }

        // 循環：從每個節點往上走，步數超過節點總數或回到自己即為循環
        foreach (var nStart in parentOf.Keys)
        {
            var nSteps = 0;
            int? nCur = parentOf[nStart];
            while (nCur.HasValue)
            {
                if (nCur.Value == nStart || ++nSteps > parentOf.Count)
                    throw new TreeSortException(ErrCycle);
                nCur = parentOf[nCur.Value];
            }
        }

        if (!allowMultiRoot && parentOf.Values.Count(p => p == null) > 1)
            throw new TreeSortException(ErrMultiRoot);

        return parentOf;
    }
}

/// <summary>樹搬移防呆失敗；<see cref="Code"/> 為錯誤代碼（對應前端 `*.err.{code}` i18n key）。</summary>
public class TreeSortException : InvalidOperationException
{
    public string Code { get; }
    public TreeSortException(string szCode) : base(szCode) { Code = szCode; }
}
