using ScadaEngine.Web.Services;

namespace ScadaEngine.Tests.Common;

/// <summary>
/// 鎖住 TreeSortValidator — 四個迴路頁 + LogicFlow 拖曳搬移的後端防呆。
/// 這裡放行壞資料 = 報表遞迴 CTE 無窮迴圈、EMS 全廠卡片指到錯的根。
/// 規則：節點存在 / 父節點存在 / 無循環 / （可選）單一根。
/// </summary>
public class TreeSortValidatorTests
{
    // 現況：1(root) ─ 2 ─ 4；1 ─ 3
    private static List<(int nId, int? nParentId)> Tree() => new()
    {
        (1, null), (2, 1), (3, 1), (4, 2)
    };

    private static List<(int nId, int? nParentId, int nSortOrder)> Upd(params (int, int?, int)[] rows) => rows.ToList();

    [Fact]
    public void 同層換序_通過並回傳套用後對照()
    {
        var map = TreeSortValidator.Validate(Tree(), Upd((3, 1, 0), (2, 1, 1)), allowMultiRoot: true);
        Assert.Equal(1, map[2]);
        Assert.Equal(1, map[3]);
        Assert.Null(map[1]);
    }

    [Fact]
    public void 跨層搬移_通過()
    {
        var map = TreeSortValidator.Validate(Tree(), Upd((3, 2, 1)), allowMultiRoot: true);
        Assert.Equal(2, map[3]);
    }

    [Fact]
    public void 搬到自己底下_丟循環()
    {
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(Tree(), Upd((2, 2, 0)), allowMultiRoot: true));
        Assert.Equal(TreeSortValidator.ErrCycle, ex.Code);
    }

    [Fact]
    public void 搬到自己子孫底下_丟循環()
    {
        // 2 搬到 4 底下，而 4 本來在 2 底下
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(Tree(), Upd((2, 4, 0)), allowMultiRoot: true));
        Assert.Equal(TreeSortValidator.ErrCycle, ex.Code);
    }

    [Fact]
    public void 兩筆更新互換父子_丟循環()
    {
        // 前端一次送兩筆：2 → 4 底下、4 → 2 底下（整批套用後才看得出循環）
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(Tree(), Upd((2, 4, 0), (4, 2, 0)), allowMultiRoot: true));
        Assert.Equal(TreeSortValidator.ErrCycle, ex.Code);
    }

    [Fact]
    public void 節點不存在_丟缺節點()
    {
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(Tree(), Upd((99, 1, 0)), allowMultiRoot: true));
        Assert.Equal(TreeSortValidator.ErrMissingNode, ex.Code);
    }

    [Fact]
    public void 父節點不存在_丟缺父節點()
    {
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(Tree(), Upd((3, 99, 0)), allowMultiRoot: true));
        Assert.Equal(TreeSortValidator.ErrMissingParent, ex.Code);
    }

    [Fact]
    public void 單一根模式_拖出第二個根_丟多根()
    {
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(Tree(), Upd((3, null, 1)), allowMultiRoot: false));
        Assert.Equal(TreeSortValidator.ErrMultiRoot, ex.Code);
    }

    [Fact]
    public void 多根模式_拖出第二個根_通過()
    {
        var map = TreeSortValidator.Validate(Tree(), Upd((3, null, 1)), allowMultiRoot: true);
        Assert.Null(map[3]);
    }

    [Fact]
    public void 單一根模式_DB現況已多根_任何更新都擋()
    {
        // 舊資料已壞（兩個根）：即使這次只是同層換序，也不能放行，否則「全廠 = TOP 1 根」語意繼續漂
        var current = new List<(int, int?)> { (1, null), (5, null), (2, 1) };
        var ex = Assert.Throws<TreeSortException>(() =>
            TreeSortValidator.Validate(current, Upd((2, 1, 0)), allowMultiRoot: false));
        Assert.Equal(TreeSortValidator.ErrMultiRoot, ex.Code);
    }

    [Fact]
    public void 空更新_通過不動()
    {
        var map = TreeSortValidator.Validate(Tree(), Upd(), allowMultiRoot: false);
        Assert.Equal(4, map.Count);
    }
}
