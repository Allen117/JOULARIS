// ============================================================
// Designer — 匯入頁面檔（plan 2026-10-21-designer-layout-import）
// ============================================================
// 頁面樹右鍵「匯入頁面檔…」→ 選 .joularis-page.json（由 skill joularis-layout-gen
// 從 pipe-diagram layout.json 產生）→ 對話框確認每台設備對到哪個 Device、預覽命中
// → 建新頁（底圖 + 排好位置 + 依角色解析好 SID 的 widget）。只在前端建頁，
// 使用者按「儲存」才走既有 /Designer/Save。
//
// ⚠️ 與「複製 / 貼上頁面」（page-clipboard.js）綁定語意相反：貼上**清空**綁定；
// 匯入則依 props.objRoleBind 在**當下站台**的點位重新解析，未命中才留空。
//
// 頁面檔格式（nVersion 1）：
//   { szFormat:'joularis-page', nVersion:1, szName, nCanvasW, nCanvasH, szBgFileName, szBgDataUrl,
//     arrEquipGroups:[{ szEquipId, szEquipType, szSourceType, nColPitch, ... }],
//     arrWidgetState:[{ szType, nX, nY, nW, nH, szGroup?, nCol?, nRow?, props:{ szTitle, objRoleBind? } }] }
//   szGroup / nCol / nRow 只在匯入時用（固定座位收欄），建頁時剝掉；props 只寫與預設不同的欄位，
//   匯入時與 getWidgetDefaultProps 合併，`__i18n__:` 標題解析成當前語系。
//
// 依賴：state.js（t / escHtml / arrPageTree）/ widget-defs.js（WIDGET_DEFS /
// getWidgetDefaultProps）/ picker.js（_ensurePickerData / arrAllPoints）/
// role-aliases.js + role-resolve.js（window._roleResolve）/ page-clipboard.js
// （insertNewPage / makeUniqueSiblingName）/ ctx-menu.js（showDesignToast）。
// ============================================================

const PAGE_IMPORT_FORMAT    = 'joularis-page';
const PAGE_IMPORT_VERSION   = 1;
const PAGE_IMPORT_MAX_BYTES = 8 * 1024 * 1024;   // 底圖 base64 為主

let _importParentId   = null;   // 右鍵所在節點（null = 根層）
let _importPage       = null;   // 已驗證的頁面檔內容
let _importFileName   = '';
let _importRows       = [];     // [{ szEquipId, szEquipType, nColPitch, arrWidgets, szDevice, bOpen }]
let _importPointsByDevice = {}; // Device 標籤 → 點位陣列
let _importDeviceLabels   = [];
let _importModal      = null;

// ============================================================
// 入口：右鍵選單 → 開檔案選擇
// ============================================================
function startPageImport(szParentId) {
    _importParentId = szParentId;
    const inp = document.getElementById('pageImportFile');
    if (!inp) return;
    inp.value = '';   // 同一個檔再選一次也要觸發 change
    inp.click();
}

document.getElementById('pageImportFile')?.addEventListener('change', e => {
    const f = e.target.files && e.target.files[0];
    if (f) onPageImportFileChosen(f);
});

async function onPageImportFileChosen(file) {
    if (file.size > PAGE_IMPORT_MAX_BYTES) {
        _importToastError(t('designer.import.too_large', { mb: PAGE_IMPORT_MAX_BYTES / 1024 / 1024 }));
        return;
    }
    let obj;
    try {
        obj = JSON.parse(await file.text());
    } catch (_) {
        _importToastError(t('designer.import.invalid', { reason: t('designer.import.reason.json') }));
        return;
    }
    const err = validatePageFile(obj);
    if (err) {
        _importToastError(t('designer.import.invalid', { reason: t(err.szKey, err.args) }));
        return;
    }

    try {
        await _ensurePickerData();   // 失敗時 picker.js 已 alert
    } catch (_) { return; }

    _importPage     = obj;
    _importFileName = file.name;
    _buildImportDeviceIndex();
    _importRows = obj.arrEquipGroups.map(g => ({
        szEquipId:   g.szEquipId,
        szEquipType: g.szEquipType || '',
        nColPitch:   g.nColPitch || 0,
        arrWidgets:  obj.arrWidgetState.filter(w => w.szGroup === g.szEquipId),
        szDevice:    window._roleResolve.suggestDevice(g.szEquipId, _importDeviceLabels),
        bOpen:       false
    }));
    renderPageImportModal();
    if (!_importModal) _importModal = new bootstrap.Modal(document.getElementById('pageImportModal'));
    _importModal.show();
}

// ============================================================
// 格式驗證（純函式；回 null 或 { szKey, args }）
// ============================================================
function validatePageFile(obj) {
    const reason = (szKey, args) => ({ szKey: 'designer.import.reason.' + szKey, args: args || {} });
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return reason('json');
    if (obj.szFormat !== PAGE_IMPORT_FORMAT) return reason('format', { format: PAGE_IMPORT_FORMAT });
    if (!Number.isInteger(obj.nVersion) || obj.nVersion < 1 || obj.nVersion > PAGE_IMPORT_VERSION) {
        return reason('version', { v: obj.nVersion, max: PAGE_IMPORT_VERSION });
    }
    for (const k of ['nCanvasW', 'nCanvasH']) {
        if (!(typeof obj[k] === 'number' && obj[k] > 0)) return reason('field', { field: k });
    }
    if (!Array.isArray(obj.arrWidgetState)) return reason('field', { field: 'arrWidgetState' });
    if (!Array.isArray(obj.arrEquipGroups)) return reason('field', { field: 'arrEquipGroups' });
    if (obj.szBgDataUrl != null && (typeof obj.szBgDataUrl !== 'string' || obj.szBgDataUrl.indexOf('data:image/') !== 0)) {
        return reason('field', { field: 'szBgDataUrl' });
    }
    const setGroups = new Set();
    for (const g of obj.arrEquipGroups) {
        if (!g || typeof g.szEquipId !== 'string' || !g.szEquipId) return reason('field', { field: 'arrEquipGroups[].szEquipId' });
        setGroups.add(g.szEquipId);
    }
    for (const w of obj.arrWidgetState) {
        if (!w || typeof w !== 'object') return reason('field', { field: 'arrWidgetState[]' });
        if (!WIDGET_DEFS[w.szType]) return reason('widget_type', { type: String(w.szType) });
        for (const k of ['nX', 'nY', 'nW', 'nH']) {
            if (typeof w[k] !== 'number') return reason('field', { field: 'arrWidgetState[].' + k });
        }
        if (w.props != null && typeof w.props !== 'object') return reason('field', { field: 'arrWidgetState[].props' });
        if (w.szGroup != null && !setGroups.has(w.szGroup)) return reason('group', { group: String(w.szGroup) });
    }
    return null;
}

// Device 分群：沿用 picker 的 szDeviceLabel（Tag.Device → 站號子設備 → Coordinator 名）
function _buildImportDeviceIndex() {
    _importPointsByDevice = {};
    (arrAllPoints || []).forEach(p => {
        if (!p.szDeviceLabel) return;
        (_importPointsByDevice[p.szDeviceLabel] = _importPointsByDevice[p.szDeviceLabel] || []).push(p);
    });
    _importDeviceLabels = Object.keys(_importPointsByDevice).sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
}

function _importRowMatch(row) {
    const R = window._roleResolve;
    return R.matchRoles(R.collectRoles(row.arrWidgets), _importPointsByDevice[row.szDevice] || []);
}

// ============================================================
// 對話框
// ============================================================
function renderPageImportModal() {
    const page = _importPage;
    const nPlaceholders = page.arrWidgetState.filter(w => !w.szGroup).length;
    document.getElementById('pageImportSummary').textContent = t('designer.import.summary', {
        name: _importPageName(), groups: _importRows.length, placeholders: nPlaceholders,
        w: page.nCanvasW, h: page.nCanvasH
    });

    const szOptions = _importDeviceLabels.map(l => `<option value="${escHtml(l)}">${escHtml(l)}</option>`).join('');
    const szRows = _importRows.map((row, idx) => {
        const m = _importRowMatch(row);
        const bNoDevice = !row.szDevice;
        const szHitCls = bNoDevice || m.nHit === 0 ? 'pi-hit-none' : (m.nHit < m.nTotal ? 'pi-hit-part' : 'pi-hit-full');
        const szDetail = row.bOpen ? `<tr class="pi-detail"><td colspan="5">${_renderImportRoleList(m)}</td></tr>` : '';
        return `<tr class="${bNoDevice ? 'pi-row-unmatched' : ''}">
            <td><button type="button" class="pi-toggle" onclick="togglePageImportRow(${idx})" title="${escHtml(t('designer.import.details'))}">
                <i class="fas fa-caret-${row.bOpen ? 'down' : 'right'}"></i></button></td>
            <td class="pi-equip">${escHtml(row.szEquipId)}</td>
            <td class="pi-type">${escHtml(t('designer.import.type.' + row.szEquipType))}</td>
            <td><select class="form-select form-select-sm pi-device" onchange="onPageImportDeviceChange(${idx}, this.value)">
                <option value="">${escHtml(t('designer.import.device_none'))}</option>${szOptions}</select></td>
            <td class="pi-hit ${szHitCls}">${m.nHit} / ${m.nTotal}</td>
        </tr>${szDetail}`;
    }).join('');

    document.getElementById('pageImportRows').innerHTML = szRows ||
        `<tr><td colspan="5" class="rt-empty">${escHtml(t('designer.import.no_groups'))}</td></tr>`;
    // select 的值用 DOM 設（option 文字含任意字元，不拼進 selected 屬性）
    document.querySelectorAll('#pageImportRows select.pi-device').forEach((sel, idx) => {
        sel.value = _importRows[idx].szDevice || '';
    });
    _updatePageImportTotal();
}

// 展開列：角色 → 點名 / 未找到 / 多筆命中（沿用 P1 設備預覽樣式）
function _renderImportRoleList(m) {
    const szRows = m.arrItems.map(item => {
        let szPoint, szTag = '', szRowCls = '';
        const szFull = item.point ? escHtml(window._roleResolve.pointDisplayName(item.point)) : '';
        if (item.point) {
            szPoint = szFull;
            if (item.bMulti) szTag = `<span class="rt-tag rt-multi">${escHtml(t('designer.equip.multi_hit'))}</span>`;
        } else {
            szPoint = '<span class="rt-na">&#x2014;</span>';
            szTag = `<span class="rt-tag rt-missing">${escHtml(t('designer.equip.not_found'))}</span>`;
            szRowCls = ' rt-row-missing';
        }
        return `<div class="rt-preview-row${szRowCls}">
            <span class="rt-role" style="flex-basis:84px;">${escHtml(t('designer.import.role.' + item.szRole))}</span>
            <span class="rt-arrow">&#x2192;</span>
            <span class="rt-point" title="${szFull}">${szPoint}</span>
            ${szTag}
        </div>`;
    }).join('');
    return `<div class="row-template-body"><div class="rt-preview-list">${szRows}</div></div>`;
}

function _updatePageImportTotal() {
    let nHit = 0, nTotal = 0;
    _importRows.forEach(row => { const m = _importRowMatch(row); nHit += m.nHit; nTotal += m.nTotal; });
    document.getElementById('pageImportTotal').textContent = t('designer.import.total_hits', { n: nHit, m: nTotal });
}

function onPageImportDeviceChange(idx, szValue) {
    if (!_importRows[idx]) return;
    _importRows[idx].szDevice = szValue;
    renderPageImportModal();
}

function togglePageImportRow(idx) {
    if (!_importRows[idx]) return;
    _importRows[idx].bOpen = !_importRows[idx].bOpen;
    renderPageImportModal();
}

function _importPageName() {
    if (_importPage && typeof _importPage.szName === 'string' && _importPage.szName.trim()) return _importPage.szName.trim();
    return (_importFileName || '').replace(/\.joularis-page\.json$/i, '').replace(/\.json$/i, '') || t('designer.page.new_name');
}

// ============================================================
// 確認 → 建頁
// ============================================================
function confirmPageImport() {
    if (!_importPage) return;
    const R = window._roleResolve;
    const bKeepMissing = !!document.getElementById('pageImportKeepMissing')?.checked;
    const nStamp = Date.now();
    let nHit = 0, nTotal = 0;
    const arrOut = [];

    _importRows.forEach((row, idx) => {
        const res = R.resolveRoleBindings(row.arrWidgets, _importPointsByDevice[row.szDevice] || []);
        nHit += res.nHit;
        nTotal += res.nTotal;
        // 固定座位：有就坐、沒有就空著、整欄沒命中才收欄（勾「保留未命中座位」時全部保留）
        const arrKept = bKeepMissing
            ? res.arrWidgetState
            : R.dropEmptySeats(res.arrWidgetState, res.arrWidgetHit, row.nColPitch);
        const szInstance = `ti_${nStamp}_${idx + 1}`;   // 同一台設備的 widget 共用（P3 整組重綁用）
        arrKept.forEach(ws => arrOut.push(_finalizeImportedWidget(ws, szInstance)));
    });
    // 未綁定佔位（感測器 / 面板）照原位建立
    _importPage.arrWidgetState.filter(w => !w.szGroup).forEach(ws => arrOut.push(_finalizeImportedWidget(ws, null)));

    const szBase = _importPageName();
    const newPage = insertNewPage(_importParentId, {
        szIcon:         'fa-project-diagram',
        nCanvasW:       Math.round(_importPage.nCanvasW),
        nCanvasH:       Math.round(_importPage.nCanvasH),
        szBgDataUrl:    _importPage.szBgDataUrl || null,
        szBgFileName:   _importPage.szBgFileName || null,
        arrWidgetState: arrOut
    }, arrSiblings => makeUniqueSiblingName(szBase, arrSiblings));

    _importModal?.hide();
    _importPage = null;
    _importRows = [];
    if (!newPage) return;
    showDesignToast(nHit > 0 ? 'success' : 'warning',
        '<i class="fas fa-file-import me-1"></i>' +
        escHtml(t('designer.import.done', { name: newPage.szName, n: nHit, m: nTotal })));
}

// 頁面檔 widget → Designer widget state：補齊預設 props、解析 __i18n__ 標題、剝掉匯入用欄位
function _finalizeImportedWidget(ws, szInstance) {
    const props = window._roleResolve.resolveI18nProps(
        { ...getWidgetDefaultProps(ws.szType), ...(ws.props || {}) }, t);
    if (szInstance) props.szTplInstance = szInstance;
    return {
        szType: ws.szType,
        nX: Math.round(ws.nX), nY: Math.round(ws.nY),
        nW: Math.round(ws.nW), nH: Math.round(ws.nH),
        props: props
    };
}

function _importToastError(szMsg) {
    showDesignToast('danger', '<i class="fas fa-exclamation-circle me-1"></i>' + escHtml(szMsg));
}
