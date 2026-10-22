// ============================================================
// Designer — 右鍵選單 + Toast
// ============================================================
// 內容：頁面樹右鍵、畫布右鍵、表格 cell 右鍵、Toast 顯示。
// 依賴：state.js / page-tree.js（addPage / editPage / deletePage /
// countPages）/ page-import.js（startPageImport）/ prop-panel.js（onTableCellClick / clearCellSid /
// nSelectedCellRow / nSelectedCellCol）/ picker.js（openCellPointPicker）/
// widget-core.js（selectWidget / createWidget）/ widget-defs.js
// （initArrCells）。
// ============================================================

const ctxMenu    = document.getElementById('ctxMenu');
let arrCtxActions = [];

function showCtxMenu(e, szPageId) {
    const items = [];

    // 頁面層級複製貼上一律走右鍵（決策 1：不提供快捷鍵，避免與元件層級 Ctrl+C/V 的
    // 「保留綁定」語意撞鍵）；剪貼簿為空時「貼上」不顯示
    const bHasClip = hasPageClipboard();

    if (szPageId === null) {
        // 空白區右鍵
        items.push({ szIcon: 'fa-plus', szLabel: t('designer.ctx.add_page'), fn: () => addPage(null) });
        items.push({ isDivider: true });
        if (bHasClip) {
            items.push({ szIcon: 'fa-paste', szLabel: t('designer.ctx.paste_as_root'), fn: () => pastePage(null) });
        }
        items.push({ szIcon: 'fa-file-import', szLabel: t('designer.ctx.import_page'), fn: () => startPageImport(null) });
    } else {
        // 節點右鍵
        items.push({ szIcon: 'fa-pen',  szLabel: t('designer.ctx.edit'),         fn: () => editPage(szPageId) });
        items.push({ szIcon: 'fa-plus', szLabel: t('designer.ctx.add_subpage'),  fn: () => addPage(szPageId) });
        items.push({ isDivider: true });
        items.push({ szIcon: 'fa-copy', szLabel: t('designer.ctx.copy_page'),    fn: () => copyPage(szPageId) });
        if (bHasClip) {
            items.push({ szIcon: 'fa-paste', szLabel: t('designer.ctx.paste_as_subpage'), fn: () => pastePage(szPageId) });
        }
        items.push({ szIcon: 'fa-file-import', szLabel: t('designer.ctx.import_page'), fn: () => startPageImport(szPageId) });
        if (countPages(arrPageTree) > 1) {
            items.push({ isDivider: true });
            items.push({ szIcon: 'fa-trash-alt', szLabel: t('designer.ctx.delete'), fn: () => deletePage(szPageId), isDanger: true });
        }
    }

    arrCtxActions = items.filter(i => !i.isDivider).map(i => i.fn);
    let nActionIdx = 0;

    ctxMenu.innerHTML = items.map(item => {
        if (item.isDivider) return `<div class="ctx-divider"></div>`;
        const szClass = 'ctx-item' + (item.isDanger ? ' ctx-danger' : '');
        const idx = nActionIdx++;
        return `<div class="${szClass}" data-idx="${idx}">
                    <i class="fas ${item.szIcon}" style="width:14px;opacity:.7;"></i>${item.szLabel}
                </div>`;
    }).join('');

    // 顯示後計算位置（避免超出視窗）
    ctxMenu.style.display = 'block';
    ctxMenu.style.left = '0px';
    ctxMenu.style.top  = '0px';
    const nMenuW = ctxMenu.offsetWidth;
    const nMenuH = ctxMenu.offsetHeight;
    let nLeft = e.clientX;
    let nTop  = e.clientY;
    if (nLeft + nMenuW > window.innerWidth)  nLeft = e.clientX - nMenuW;
    if (nTop  + nMenuH > window.innerHeight) nTop  = e.clientY - nMenuH;
    ctxMenu.style.left = nLeft + 'px';
    ctxMenu.style.top  = nTop  + 'px';
}

ctxMenu.addEventListener('click', e => {
    const item = e.target.closest('.ctx-item');
    if (!item) return;
    const fn = arrCtxActions[parseInt(item.dataset.idx)];
    if (fn) fn();
    hideCtxMenu();
});

function hideCtxMenu() { ctxMenu.style.display = 'none'; }

document.addEventListener('click',   hideCtxMenu);
document.addEventListener('keydown', e => { if (e.key === 'Escape') hideCtxMenu(); });

// 表格儲存格右鍵選單
function onTableCellCtxMenu(e, widgetEl, nRow, nCol) {
    selectWidget(widgetEl);
    nSelectedCellRow = nRow;
    nSelectedCellCol = nCol;
    const isHeader = (nRow === 0);
    const props = widgetEl.widgetProps;
    initArrCells(props);
    const cell = props.arrCells[nRow]?.[nCol];
    if (!cell) return;

    const items = [];
    items.push({ szIcon: 'fa-pen', szLabel: t('designer.ctx.edit_cell'), fn: () => {
        onTableCellClick(widgetEl, nRow, nCol);
    }});
    if (!isHeader) {
        items.push({ szIcon: 'fa-link', szLabel: t('designer.ctx.bind_point'), fn: () => {
            openCellPointPicker(nRow, nCol);
        }});
        if (cell.szSid) {
            items.push({ szIcon: 'fa-unlink', szLabel: t('designer.ctx.clear_binding'), fn: () => {
                clearCellSid(nRow, nCol);
            }});
        }
    }

    arrCtxActions = items.map(i => i.fn);
    ctxMenu.innerHTML = items.map((item, idx) => `
        <div class="ctx-item" data-idx="${idx}">
            <i class="fas ${item.szIcon}" style="width:14px;opacity:.7;"></i>${item.szLabel}
        </div>
    `).join('');
    ctxMenu.style.display = 'block';
    ctxMenu.style.left = '0px';
    ctxMenu.style.top  = '0px';
    const nMenuW = ctxMenu.offsetWidth;
    const nMenuH = ctxMenu.offsetHeight;
    let nLeft = e.clientX;
    let nTop  = e.clientY;
    if (nLeft + nMenuW > window.innerWidth)  nLeft = e.clientX - nMenuW;
    if (nTop  + nMenuH > window.innerHeight) nTop  = e.clientY - nMenuH;
    ctxMenu.style.left = nLeft + 'px';
    ctxMenu.style.top  = nTop  + 'px';
}

function showCanvasCtxMenu(e, nX, nY) {
    const items = [
        { szIcon: 'fa-font', szLabel: t('designer.ctx.add_text'), fn: () => createWidget('text', nX, nY) }
    ];
    arrCtxActions = items.map(i => i.fn);
    ctxMenu.innerHTML = items.map((item, idx) => `
        <div class="ctx-item" data-idx="${idx}">
            <i class="fas ${item.szIcon}" style="width:14px;opacity:.7;"></i>${item.szLabel}
        </div>
    `).join('');
    ctxMenu.style.display = 'block';
    ctxMenu.style.left = '0px';
    ctxMenu.style.top  = '0px';
    const nMenuW = ctxMenu.offsetWidth;
    const nMenuH = ctxMenu.offsetHeight;
    let nLeft = e.clientX;
    let nTop  = e.clientY;
    if (nLeft + nMenuW > window.innerWidth)  nLeft = e.clientX - nMenuW;
    if (nTop  + nMenuH > window.innerHeight) nTop  = e.clientY - nMenuH;
    ctxMenu.style.left = nLeft + 'px';
    ctxMenu.style.top  = nTop  + 'px';
}

// ============================================================
// Toast 顯示
// ============================================================
function showDesignToast(szType, szHtml) {
    const wrap = document.createElement('div');
    wrap.style.cssText = 'position:fixed;bottom:24px;right:24px;z-index:9999;min-width:260px;';
    wrap.innerHTML = `
        <div class="alert alert-${szType} alert-dismissible fade show mb-0 shadow-sm" style="font-size:13px;">
            ${szHtml}
            <button type="button" class="btn-close btn-sm" data-bs-dismiss="alert"></button>
        </div>`;
    document.body.appendChild(wrap);
    setTimeout(() => { wrap.querySelector('.alert')?.classList.remove('show'); setTimeout(() => wrap.remove(), 300); }, 3500);
}
