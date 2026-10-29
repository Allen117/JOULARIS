namespace ScadaEngine.Engine.Models;

/// <summary>
/// Coordinator 改名結果（IDataRepository.RenameCoordinatorAsync）
/// </summary>
public enum CoordinatorRenameResult
{
    Success,

    /// <summary>新名稱已被其他 Coordinator 使用（不分大小寫）</summary>
    NameTaken,

    /// <summary>Id + 舊名稱對不上（已被別人改名或刪除）</summary>
    NotFound
}
