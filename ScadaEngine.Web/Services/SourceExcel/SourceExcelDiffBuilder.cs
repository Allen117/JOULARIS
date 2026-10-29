using ScadaEngine.Web.Features.Shared.Models;

namespace ScadaEngine.Web.Services.SourceExcel;

/// <summary>
/// 上傳工作表 vs 既有 JSON 的差異比對（來源無關、純函數）。
///
/// - 新增 / 覆寫 / 無變更 / 錯誤 分類
/// - 設備層欄位變更、逐點欄位變更
/// - SID 位移偵測：逐點比對 Name。只在尾端增減 → 一般覆寫；中間插入、刪除或換順序 → HasSidShift（高風險，需另外勾選）。
///   同位置名稱不同但該名稱在另一側也不存在 → 視為原地改名（不算位移）
/// - 鍵值式比對（adapter.PointKeyField 非 null，OPC UA 的 Seq）：以 key 對點 → 新增 / 修改 / 移除，不做位移偵測
/// - 網頁修改衝突：覆寫且有差異時，若既有 JSON 的 WebEditedAt 晚於 Excel 匯出時間（或 Excel 沒有匯出時間）→ HasWebEditConflict（需另外勾選）
/// - 刪除候選分兩區：既有 JSON 的 SourceWorkbook 與這次上傳的檔名相同（不分大小寫、忽略副檔名）且這次沒有的工作表
///   → 刪除候選（預設勾選）；其他未包含的設備 → 另列（預設不勾）
/// </summary>
public static class SourceExcelDiffBuilder
{
    /// <param name="exportedAt">上傳 Excel 的匯出時間（活頁簿 ScadaExportedAt）；null = 不明來源</param>
    public static SheetDiff Build(ParsedSourceSheet uploaded, ParsedSourceSheet? existing, ISourceExcelAdapter adapter, DateTimeOffset? exportedAt = null)
    {
        var diff = new SheetDiff
        {
            SheetName = uploaded.SheetName,
            NewPointCount = uploaded.Points.Count,
            OldPointCount = existing?.Points.Count ?? 0,
            ExistingSourceWorkbook = existing?.SourceWorkbook,
            WebEditedAt = existing?.WebEditedAt is { } dtEdited ? SourceJsonStamp.ToDisplay(dtEdited) : null,
        };
        diff.Errors.AddRange(uploaded.Errors);
        diff.Warnings.AddRange(uploaded.Warnings);

        if (!uploaded.IsValid)
        {
            diff.Kind = SheetDiffKind.Error;
            diff.DefaultChecked = false;
            return diff;
        }

        diff.StationNames = adapter.BuildStationNames(uploaded, existing);

        if (existing == null)
        {
            diff.Kind = SheetDiffKind.Added;
            diff.DefaultChecked = true;
            return diff;
        }

        // 設備層欄位
        foreach (var szField in adapter.HeaderFields)
        {
            var szOld = existing.Header.GetValueOrDefault(szField, string.Empty);
            var szNew = uploaded.Header.GetValueOrDefault(szField, string.Empty);
            if (!string.Equals(szOld, szNew, StringComparison.Ordinal))
                diff.HeaderChanges.Add($"{szField}: {szOld} → {szNew}");
        }

        if (adapter.PointKeyField is { } szKey)
            BuildKeyedPointChanges(diff, existing, uploaded, adapter, szKey);
        else
            BuildPositionalPointChanges(diff, existing, uploaded, adapter);

        var isChanged = diff.HeaderChanges.Count > 0 || diff.PointChanges.Count > 0;
        diff.Kind = isChanged ? SheetDiffKind.Overwrite : SheetDiffKind.Unchanged;
        diff.DefaultChecked = isChanged;

        // 網頁修改衝突：只有真的會覆寫時才警告（無變更就不會寫檔，不必嚇人）
        diff.HasWebEditConflict = isChanged && SourceJsonStamp.IsWebEditConflict(existing.WebEditedAt, exportedAt);
        return diff;
    }

    /// <summary>鍵值式：以 key 欄位對點。key 空白的上傳點位視為新增（Reconcile 應已配號，這裡保險）</summary>
    private static void BuildKeyedPointChanges(SheetDiff diff, ParsedSourceSheet existing, ParsedSourceSheet uploaded, ISourceExcelAdapter adapter, string szKey)
    {
        var existingByKey = new Dictionary<string, ParsedSourcePoint>(StringComparer.Ordinal);
        foreach (var p in existing.Points)
        {
            var k = p.Get(szKey);
            if (k.Length > 0) existingByKey.TryAdd(k, p);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in uploaded.Points)
        {
            var k = p.Get(szKey);
            int.TryParse(k, out var nIndex);
            if (k.Length > 0 && existingByKey.TryGetValue(k, out var old))
            {
                seen.Add(k);
                var szSummary = BuildPointSummary(old, p, adapter.PointFields);
                if (szSummary.Length > 0)
                    diff.PointChanges.Add(new SheetDiffPointChange { Index = nIndex, Name = p.Name, Change = "Modified", Summary = szSummary });
            }
            else
            {
                diff.PointChanges.Add(new SheetDiffPointChange { Index = nIndex, Name = p.Name, Change = "Added" });
                diff.TailAddedCount++;
            }
        }

        foreach (var (k, old) in existingByKey)
        {
            if (seen.Contains(k)) continue;
            int.TryParse(k, out var nIndex);
            diff.PointChanges.Add(new SheetDiffPointChange { Index = nIndex, Name = old.Name, Change = "Removed" });
            diff.KeyedRemovedCount++;
        }

        // 顯示順序：依 key 數值
        diff.PointChanges.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    /// <summary>位置式：陣列索引 = SID 序號；同位置比欄位、尾端增減另計、偵測 SID 位移</summary>
    private static void BuildPositionalPointChanges(SheetDiff diff, ParsedSourceSheet existing, ParsedSourceSheet uploaded, ISourceExcelAdapter adapter)
    {
        var nCommon = Math.Min(existing.Points.Count, uploaded.Points.Count);
        for (var i = 0; i < nCommon; i++)
        {
            var szSummary = BuildPointSummary(existing.Points[i], uploaded.Points[i], adapter.PointFields);
            if (szSummary.Length > 0)
            {
                diff.PointChanges.Add(new SheetDiffPointChange
                {
                    Index = i + 1,
                    Name = uploaded.Points[i].Name,
                    Change = "Modified",
                    Summary = szSummary,
                });
            }
        }
        for (var i = nCommon; i < uploaded.Points.Count; i++)
        {
            diff.PointChanges.Add(new SheetDiffPointChange { Index = i + 1, Name = uploaded.Points[i].Name, Change = "Added" });
        }
        for (var i = nCommon; i < existing.Points.Count; i++)
        {
            diff.PointChanges.Add(new SheetDiffPointChange { Index = i + 1, Name = existing.Points[i].Name, Change = "Removed" });
        }
        diff.TailAddedCount = Math.Max(0, uploaded.Points.Count - existing.Points.Count);
        diff.TailRemovedCount = Math.Max(0, existing.Points.Count - uploaded.Points.Count);

        // SID 位移
        var nShiftIndex = DetectSidShift(existing.Points, uploaded.Points);
        if (nShiftIndex > 0)
        {
            diff.HasSidShift = true;
            diff.SidShiftFromIndex = nShiftIndex;
        }
    }

    /// <summary>「欄位: 舊 → 新」逗號串接；無變更回空字串</summary>
    public static string BuildPointSummary(ParsedSourcePoint oldPoint, ParsedSourcePoint newPoint, IReadOnlyList<string> fields)
    {
        var aDiffs = new List<string>();
        foreach (var szField in fields)
        {
            var szOld = oldPoint.Get(szField);
            var szNew = newPoint.Get(szField);
            if (!string.Equals(szOld, szNew, StringComparison.Ordinal))
                aDiffs.Add($"{szField}: {szOld} → {szNew}");
        }
        return string.Join(", ", aDiffs);
    }

    /// <summary>
    /// 回傳 1-based「第 N 點起 SID 位移」；0 = 無位移。
    /// 規則：同位置名稱不同時，若舊名稱出現在新清單別處、或新名稱出現在舊清單別處 → 位移（插入/刪除/重排）；
    /// 兩邊都找不到 → 原地改名，不算位移。
    /// </summary>
    public static int DetectSidShift(IReadOnlyList<ParsedSourcePoint> oldPoints, IReadOnlyList<ParsedSourcePoint> newPoints)
    {
        var oldNames = new HashSet<string>(oldPoints.Select(p => p.Name), StringComparer.Ordinal);
        var newNames = new HashSet<string>(newPoints.Select(p => p.Name), StringComparer.Ordinal);

        var nCommon = Math.Min(oldPoints.Count, newPoints.Count);
        for (var i = 0; i < nCommon; i++)
        {
            var szOld = oldPoints[i].Name;
            var szNew = newPoints[i].Name;
            if (string.Equals(szOld, szNew, StringComparison.Ordinal)) continue;

            if (newNames.Contains(szOld) || oldNames.Contains(szNew))
                return i + 1;
        }
        return 0;
    }

    /// <summary>來源 Excel 檔名正規化：去路徑、去副檔名、Trim、小寫</summary>
    public static string NormalizeWorkbookName(string? szName)
    {
        if (string.IsNullOrWhiteSpace(szName)) return string.Empty;
        var sz = szName.Trim();
        try
        {
            sz = Path.GetFileNameWithoutExtension(sz);
        }
        catch (ArgumentException)
        {
            // 含非法字元就直接用原字串
        }
        return sz.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 把既有設定分成「刪除候選（同來源 Excel、這次沒有）」與「其他未包含」兩區。
    /// </summary>
    public static (List<ExistingSourceInfo> candidates, List<ExistingSourceInfo> others) ClassifyExisting(
        IEnumerable<ExistingSourceInfo> existing,
        IEnumerable<string> uploadedSheetNames,
        string szWorkbookName)
    {
        var uploaded = new HashSet<string>(uploadedSheetNames, StringComparer.OrdinalIgnoreCase);
        var szWorkbookKey = NormalizeWorkbookName(szWorkbookName);

        var candidates = new List<ExistingSourceInfo>();
        var others = new List<ExistingSourceInfo>();

        foreach (var e in existing.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (uploaded.Contains(e.Name)) continue;

            var isSameWorkbook = szWorkbookKey.Length > 0
                                 && string.Equals(NormalizeWorkbookName(e.SourceWorkbook), szWorkbookKey, StringComparison.Ordinal);
            e.DefaultChecked = isSameWorkbook;
            (isSameWorkbook ? candidates : others).Add(e);
        }

        return (candidates, others);
    }

    /// <summary>
    /// 新增工作表若與某刪除候選的點位名稱序列完全相同 → 很可能是工作表改名；回傳候選名稱，否則 null
    /// </summary>
    public static string? FindRenameHint(ParsedSourceSheet added, IEnumerable<ParsedSourceSheet> deleteCandidates)
    {
        var aNewNames = added.Points.Select(p => p.Name).ToList();
        if (aNewNames.Count == 0) return null;

        foreach (var c in deleteCandidates)
        {
            if (c.Points.Count == aNewNames.Count && c.Points.Select(p => p.Name).SequenceEqual(aNewNames, StringComparer.Ordinal))
                return c.SheetName;
        }
        return null;
    }
}
