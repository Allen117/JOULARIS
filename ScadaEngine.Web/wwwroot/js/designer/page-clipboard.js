// ============================================================
// Designer — 頁面層級複製 / 貼上
// ============================================================
// 內容：copyPage / pastePage、頁面綁定清除（stripPageBindings）、唯一命名。
// 依賴：state.js（arrPageTree / szCurrentPageId / nPageIdCounter）/
// widget-defs.js（isDesignerBindingKey / DESIGNER_BINDING_DELETE_KEYS）/
// page-tree.js（saveCurrentPageState / findPage / loadPageState /
// renderPageTree）/ ctx-menu.js（showDesignToast）。
// 對外共用：insertNewPage / makeUniqueSiblingName（page-import.js 匯入頁面檔用）。
//
// ⚠️ 與**元件層級** Ctrl+C / Ctrl+V（widget-core.js copySelectedWidgets /
// pasteWidgets）是兩回事：那邊複製選取元件且**保留**點位綁定（局部複製通常
// 就是要沿用同一點位）；這裡複製整頁且**清空**綁定（新增同型設備 = 版面照抄、
// 點位重綁）。本功能一律走右鍵選單，**不提供快捷鍵**，避免兩種相反語意撞鍵。
// ============================================================

const PAGE_CLIPBOARD_KEY = '_designer_page_clipboard';

// ============================================================
// 綁定清除
// ============================================================
// 規則（widget-defs.js 為單一真相）：
//   明確清單 DESIGNER_BINDING_KEYS + 前綴 /^szSid/ /^szCid/ → 設為空字串
//   DESIGNER_BINDING_DELETE_KEYS（nCircuitId / nScheduleId / szMetric）→ delete
//   arrCells[ri][ci] 每一格遞迴套同一組規則（沿用 stripAlarmProps 的走訪寫法）
function stripPropsBindings(objProps) {
    const result = {};
    for (const [k, v] of Object.entries(objProps)) {
        if (DESIGNER_BINDING_DELETE_KEYS.has(k)) continue;         // delete：JSON 內不留痕
        if (isDesignerBindingKey(k)) { result[k] = ''; continue; }
        if (k === 'arrCells' && Array.isArray(v)) {
            result[k] = v.map(row =>
                Array.isArray(row)
                    ? row.map(cell => (typeof cell === 'object' && cell !== null)
                        ? stripPropsBindings(cell)
                        : cell)
                    : row
            );
        } else {
            result[k] = v;
        }
    }
    return result;
}

// 整頁 widget state 清綁定（深拷貝，不動原頁面）
function stripPageBindings(arrWidgetState) {
    return (arrWidgetState || []).map(ws => ({
        szType: ws.szType,
        nX: ws.nX, nY: ws.nY, nW: ws.nW, nH: ws.nH,
        props: stripPropsBindings(ws.props || {})
    }));
}

// ============================================================
// 唯一命名：同層已存在則加「 (2)」「 (3)」遞增
// ============================================================
// 貼上：「原名 - 複製」起跳（makeUniquePageName）；匯入頁面檔：原名起跳（page-import.js）
function makeUniqueSiblingName(szCandidateBase, arrSiblings) {
    const setUsed = new Set((arrSiblings || []).map(p => p.szName));
    let szCandidate = szCandidateBase;
    let n = 2;
    while (setUsed.has(szCandidate)) szCandidate = `${szCandidateBase} (${n++})`;
    return szCandidate;
}

function makeUniquePageName(szBaseName, arrSiblings) {
    return makeUniqueSiblingName(szBaseName + t('designer.page.copy_suffix'), arrSiblings);
}

// ============================================================
// 建新頁並切換過去（pastePage / page-import.js 共用）
// ============================================================
// szParentId = null → 根頁面；否則為該頁的子頁。fnMakeName(arrSiblings) 決定頁名。
// objPage 提供 szIcon / 畫布尺寸 / 背景圖 / arrWidgetState（呼叫端已處理好綁定）。
// 回新頁；父頁不存在回 null（不建頁）。
function insertNewPage(szParentId, objPage, fnMakeName) {
    // 先把當前畫布存回樹，否則切頁時會用舊狀態覆蓋
    saveCurrentPageState();

    const parent = szParentId === null ? null : findPage(szParentId);
    if (szParentId !== null && !parent) return null;
    const arrSiblings = parent ? parent.arrChildren : arrPageTree;

    const newPage = {
        szId: 'p' + (++nPageIdCounter),
        szName: fnMakeName(arrSiblings),
        szIcon: objPage.szIcon || 'fa-file-alt',
        arrChildren: [],
        szBgDataUrl: objPage.szBgDataUrl || null,
        szBgFileName: objPage.szBgFileName || null,
        nCanvasW: objPage.nCanvasW || 1200,
        nCanvasH: objPage.nCanvasH || 800,
        arrWidgetState: objPage.arrWidgetState || []
    };

    arrSiblings.push(newPage);

    // 自動切換到新頁（建完就想開始改）
    szCurrentPageId = newPage.szId;
    loadPageState(newPage);
    renderPageTree();
    return newPage;
}

// ============================================================
// 複製頁面
// ============================================================
// 只複製該頁本身，不含子頁（決策 2：子頁通常是另一組設備，一起複製反而要刪）。
// 複製當下就清綁定 → 剪貼簿內容 = 所見即所得，貼上端不必再處理。
function copyPage(szPageId) {
    // 當前頁的畫布狀態還在 DOM 裡，先同步回 arrPageTree 才複製得到最新版面
    saveCurrentPageState();

    const page = findPage(szPageId);
    if (!page) return;

    const payload = {
        szName: page.szName,
        szIcon: page.szIcon || 'fa-file-alt',
        nCanvasW: page.nCanvasW || 1200,
        nCanvasH: page.nCanvasH || 800,
        szBgDataUrl: page.szBgDataUrl || null,
        szBgFileName: page.szBgFileName || null,
        arrWidgetState: stripPageBindings(page.arrWidgetState)
    };

    if (!writePageClipboard(payload)) return;
    showDesignToast('success',
        '<i class="fas fa-copy me-1"></i>' + escHtml(t('designer.page.copied', { name: page.szName })));
}

// 寫入 localStorage；背景圖 base64 可能撐爆 5MB 配額 → 退回「不含背景圖」版本
function writePageClipboard(payload) {
    try {
        localStorage.setItem(PAGE_CLIPBOARD_KEY, JSON.stringify(payload));
        return true;
    } catch (ex) {
        // QuotaExceededError（各瀏覽器 name 不一，一律當配額處理）
        if (!payload.szBgDataUrl) {
            showDesignToast('danger',
                '<i class="fas fa-exclamation-circle me-1"></i>' + escHtml(t('designer.page.copy_failed')));
            return false;
        }
        try {
            localStorage.setItem(PAGE_CLIPBOARD_KEY,
                JSON.stringify({ ...payload, szBgDataUrl: null, szBgFileName: null }));
            showDesignToast('warning',
                '<i class="fas fa-exclamation-triangle me-1"></i>' + escHtml(t('designer.page.copy_bg_skipped')));
            return true;
        } catch (ex2) {
            showDesignToast('danger',
                '<i class="fas fa-exclamation-circle me-1"></i>' + escHtml(t('designer.page.copy_failed')));
            return false;
        }
    }
}

function readPageClipboard() {
    try {
        const raw = localStorage.getItem(PAGE_CLIPBOARD_KEY);
        if (!raw) return null;
        const obj = JSON.parse(raw);
        return (obj && typeof obj.szName === 'string') ? obj : null;
    } catch (_) { return null; }
}

function hasPageClipboard() { return readPageClipboard() !== null; }

// ============================================================
// 貼上頁面
// ============================================================
// szParentId = null → 貼為根頁面；否則貼為該頁的子頁。貼完自動切換並選中。
function pastePage(szParentId) {
    const clip = readPageClipboard();
    if (!clip) return;

    const newPage = insertNewPage(szParentId, {
        ...clip,
        // 剪貼簿已是清過綁定的版本；再清一次以防舊格式剪貼簿（升級前複製、升級後貼上）
        arrWidgetState: stripPageBindings(clip.arrWidgetState)
    }, arrSiblings => makeUniquePageName(clip.szName, arrSiblings));
    if (!newPage) return;

    showDesignToast('success',
        '<i class="fas fa-paste me-1"></i>' + escHtml(t('designer.page.pasted', { name: newPage.szName })));
}
