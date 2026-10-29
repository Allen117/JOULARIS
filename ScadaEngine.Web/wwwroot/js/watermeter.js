// 水表/迴路設定頁邏輯（IIFE 封裝，對外掛在 window._wm）
// 累積式水表（m³/L）— 與空調水系統（冷凍噸 RT）無關
(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    let g_nodes = [];           // 平坦清單
    let g_sidOptions = [];      // 水量點位選項（api/sids，僅 m³/L 系單位，含 unitScale）
    let g_selectedId = null;
    let g_modal = null;
    let g_ppModal = null;       // 兩步驟選點器 Modal

    // ============ 初始化 ============
    function init() {
        g_modal = new bootstrap.Modal(document.getElementById('circuitModal'));
        g_ppModal = new bootstrap.Modal(document.getElementById('wmPointPickerModal'));
        document.getElementsByName('wmType').forEach(r => r.addEventListener('change', updateMeterFieldsVisibility));
        const maxVolEl = document.getElementById('wmMaxVolume');
        maxVolEl.addEventListener('input', () => { maxVolEl.value = formatThousand(maxVolEl.value); });
        document.getElementById('ppSearch').addEventListener('input', renderPickerPointList);
        bindTreeDnd();
        Promise.all([loadTree(), loadSidOptions()]);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => window.i18n.ready(init));
    } else {
        window.i18n.ready(init);
    }

    function formatThousand(v) {
        if (v == null) return '';
        const digits = String(v).replace(/\D/g, '');
        if (!digits) return '';
        return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    }
    function parseThousand(v) {
        if (v == null || String(v).trim() === '') return null;
        const digits = String(v).replace(/\D/g, '');
        return digits === '' ? null : parseInt(digits, 10);
    }

    // 換算係數顯示：1 → "1"、0.001 → "0.001"（避免科學記號）
    function formatScale(dScale) {
        if (dScale == null) return '';
        return Number(dScale).toFixed(6).replace(/\.?0+$/, '');
    }

    function apiErrorMessage(json) {
        const code = json && json.message ? json.message : 'unknown';
        const key = 'watermeter.err.' + code;
        const s = t(key);
        return s === key ? code : s;
    }

    async function loadTree() {
        try {
            const res = await fetch('/WaterMeterSetting/api/tree');
            g_nodes = await res.json();
            renderTree();
            if (g_selectedId) renderDetail(g_selectedId);
        } catch (err) {
            console.error('[WaterMeterSetting] 載入樹失敗', err);
            document.getElementById('treeContainer').innerHTML =
                '<div class="text-danger small">' + t('watermeter.tree.load_failed') + '：' + escapeHtml(err.message) + '</div>';
        }
    }

    async function loadSidOptions() {
        try {
            const res = await fetch('/WaterMeterSetting/api/sids');
            g_sidOptions = await res.json();
        } catch (err) {
            console.error('[WaterMeterSetting] 載入水量點位清單失敗', err);
        }
    }

    function pointBySid(szSid) {
        if (!szSid) return null;
        return g_sidOptions.find(o => o.sid === szSid) || null;
    }

    function pointLabelOf(o) {
        return [o.coordName, o.deviceName, o.name].filter(s => s).join(' - ');
    }

    // 設備複合鍵「source|coordName」，避免不同來源同名設備互撞
    function deviceKeyOf(o) {
        return o.source + '|' + (o.coordName || t('watermeter.group.unspecified'));
    }

    // ============ 樹渲染 ============
    function renderTree() {
        const root = document.getElementById('treeContainer');
        const roots = g_nodes.filter(n => n.parentId == null)
            .sort((a, b) => a.sortOrder - b.sortOrder);
        if (roots.length === 0) {
            root.innerHTML = '<div class="text-center text-muted py-4">' +
                '<i class="fas fa-inbox fa-3x mb-2 d-block"></i><div>' + t('watermeter.tree.empty') + '</div></div>';
            return;
        }
        root.innerHTML = roots.map(n => renderNode(n)).join('');
        bindNodeEvents();
    }

    function renderNode(node) {
        const children = g_nodes.filter(n => n.parentId === node.id)
            .sort((a, b) => a.sortOrder - b.sortOrder);
        const isMeter = !!node.sid;
        const hasChildren = children.length > 0;
        const signBadge = node.sign === -1
            ? '<span class="wm-sign-neg" title="' + escapeHtml(t('watermeter.sign.neg_tooltip')) + '">−</span>'
            : '';

        return `<div class="tree-item">
            <div class="tree-node ${g_selectedId === node.id ? 'active' : ''}" data-id="${node.id}" draggable="true">
                <i class="fas fa-caret-down tree-toggle ${hasChildren ? '' : 'invisible'}"></i>
                <i class="fas ${isMeter ? 'fa-tint is-meter' : 'fa-folder'} tree-icon"></i>
                ${signBadge}
                <span class="tree-name">${escapeHtml(node.name)}</span>
                <span class="tree-actions">
                    <button title="${escapeHtml(t('watermeter.btn.add_child'))}" onclick="event.stopPropagation();window._wm.openCreateModal(${node.id})">
                        <i class="fas fa-plus"></i>
                    </button>
                    <button title="${escapeHtml(t('watermeter.btn.edit'))}" onclick="event.stopPropagation();window._wm.openEditModal(${node.id})">
                        <i class="fas fa-edit"></i>
                    </button>
                    <button class="del" title="${escapeHtml(t('watermeter.btn.delete'))}" onclick="event.stopPropagation();window._wm.deleteNode(${node.id})">
                        <i class="fas fa-trash-alt"></i>
                    </button>
                </span>
            </div>
            ${hasChildren ? `<div class="tree-children">${children.map(renderNode).join('')}</div>` : ''}
        </div>`;
    }

    function bindNodeEvents() {
        document.querySelectorAll('#treeContainer .tree-node').forEach(el => {
            el.addEventListener('click', () => {
                const nId = parseInt(el.dataset.id, 10);
                g_selectedId = nId;
                document.querySelectorAll('#treeContainer .tree-node').forEach(n => n.classList.remove('active'));
                el.classList.add('active');
                renderDetail(nId);
            });
        });
    }

    // ============ 拖曳搬移（共用 TreeDnd；跨層可搬）============
    // 規則：
    //   - 單一根（全廠）：不能拖成第二個根 → 根節點不可拖、不能放到根節點前後、不能放到空白處
    //   - 不能拖到自己 / 自己的子孫底下（TreeDnd.plan 擋）
    //   - 放到「實體表」底下會讓該表連自身一起加總 → 放下前確認
    //   - 跨層搬動會立刻改變上層迴路加總 → 放下前確認
    //   - 後端 TreeSortValidator 再驗一次（循環 / 多根 / 節點存在），根節點 Sign 強制 +1
    function bindTreeDnd() {
        if (!window.TreeDnd) return;
        window.TreeDnd.bind({
            container: document.getElementById('treeContainer'),
            nodeSelector: '.tree-node',
            getNodes: () => g_nodes,
            canDrop: canDropNode,
            onDrop: dropNode
        });
    }

    function canDropNode(nDragId, nTargetId, pos) {
        const drag = g_nodes.find(n => n.id === nDragId);
        if (!drag || drag.parentId == null) return false;          // 根節點不可拖
        if (pos === 'root') return false;                             // 單一根：不給變第二個根
        const target = g_nodes.find(n => n.id === nTargetId);
        if (!target) return false;
        if ((pos === 'before' || pos === 'after') && target.parentId == null) return false;
        return true;
    }

    async function dropNode(nDragId, nTargetId, pos) {
        const r = window.TreeDnd.plan(g_nodes, nDragId, nTargetId, pos);
        if (!r.ok) {
            if (r.error === 'cycle') alert(t('watermeter.err.sort_cycle'));
            return;
        }
        if (!r.changed) return;
        if (r.parentChanged) {
            const drag = g_nodes.find(n => n.id === nDragId);
            const parent = g_nodes.find(n => n.id === r.parentId);
            let msg = t('watermeter.msg.confirm_move', { name: drag.name, parent: parent ? parent.name : '' });
            if (parent && parent.sid) msg += '\n' + t('watermeter.msg.confirm_move_into_meter');
            if (!confirm(msg)) return;
        }
        try {
            const res = await fetch('/WaterMeterSetting/api/tree/sort', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(r.items)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(apiErrorMessage(err));
            }
            await loadTree();
        } catch (err) {
            alert(t('watermeter.msg.sort_failed', { msg: err.message }));
        }
    }

    // ============ 詳情渲染 ============
    function renderDetail(nId) {
        const node = g_nodes.find(n => n.id === nId);
        if (!node) return;
        const isMeter = !!node.sid;
        const sidOpt = isMeter ? pointBySid(node.sid) : null;
        const sidLabelHtml = sidOpt
            ? escapeHtml(pointLabelOf(sidOpt))
            : (isMeter ? UiState.warnHtml(t('watermeter.detail.point_missing')) : '');

        document.getElementById('detailTitle').innerHTML =
            `<i class="fas ${isMeter ? 'fa-tint text-primary' : 'fa-folder text-secondary'} me-1"></i>${escapeHtml(node.name)}`;

        const isRoot = node.parentId == null;
        const signLabel = node.sign === -1
            ? '<span class="wm-badge-sign-neg"><i class="fas fa-minus me-1"></i>' + escapeHtml(t('watermeter.form.sign_neg')) + '</span>'
            : '<span class="wm-badge-sign-pos"><i class="fas fa-plus me-1"></i>' + escapeHtml(t('watermeter.form.sign_pos')) + '</span>';

        const szScaleText = sidOpt
            ? t('watermeter.form.unit_scale_display', { unit: sidOpt.unit, scale: formatScale(sidOpt.unitScale) })
            : t('watermeter.form.unit_scale_display', { unit: '?', scale: formatScale(node.unitScale) });

        const html = `
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.type'))}</div>
                <div class="wm-detail-value">
                    ${isMeter ? '<span class="wm-badge-meter"><i class="fas fa-tint me-1"></i>' + escapeHtml(t('watermeter.badge.meter')) + '</span>'
                              : '<span class="wm-badge-virtual"><i class="fas fa-folder me-1"></i>' + escapeHtml(t('watermeter.badge.virtual')) + '</span>'}
                </div>
            </div>
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.name'))}</div>
                <div class="wm-detail-value">${escapeHtml(node.name)}</div>
            </div>
            ${isRoot ? '' : `
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.sign'))}</div>
                <div class="wm-detail-value">${signLabel}</div>
            </div>`}
            ${isMeter ? `
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.point'))}</div>
                <div class="wm-detail-value">${sidLabelHtml}</div>
            </div>
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.unit_scale'))}</div>
                <div class="wm-detail-value">${escapeHtml(szScaleText)}</div>
            </div>
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.max_volume'))}</div>
                <div class="wm-detail-value">${node.maxVolume != null ? formatThousand(Math.trunc(node.maxVolume)) : '<span class="text-muted">' + escapeHtml(t('watermeter.detail.not_set')) + '</span>'}</div>
            </div>` : ''}
            <div class="wm-detail-row">
                <div class="wm-detail-label">${escapeHtml(t('watermeter.detail.desc'))}</div>
                <div class="wm-detail-value">${node.description ? escapeHtml(node.description) : '<span class="text-muted">' + escapeHtml(t('watermeter.detail.none')) + '</span>'}</div>
            </div>
            <div class="mt-3 d-flex gap-2">
                <button class="btn btn-sm btn-primary" onclick="window._wm.openCreateModal(${node.id})">
                    <i class="fas fa-plus me-1"></i>${escapeHtml(t('watermeter.btn.add_child'))}
                </button>
                <button class="btn btn-sm btn-outline-primary" onclick="window._wm.openEditModal(${node.id})">
                    <i class="fas fa-edit me-1"></i>${escapeHtml(t('watermeter.btn.edit'))}
                </button>
                <button class="btn btn-sm btn-outline-danger" onclick="window._wm.deleteNode(${node.id})">
                    <i class="fas fa-trash-alt me-1"></i>${escapeHtml(t('watermeter.btn.delete'))}
                </button>
            </div>`;
        document.getElementById('detailArea').innerHTML = html;
    }

    // ============ Modal 開啟 ============
    function openCreateModal(parentId) {
        document.getElementById('modalTitle').textContent =
            parentId == null ? t('watermeter.modal.create_title') : t('watermeter.modal.create_child_title');
        document.getElementById('wmId').value = '';
        document.getElementById('wmParentId').value = parentId == null ? '' : parentId;
        document.getElementById('wmName').value = '';
        setBoundPoint(null);
        document.getElementById('wmMaxVolume').value = formatThousand(1000000000);
        document.getElementById('wmDesc').value = '';
        document.getElementById('wmTypeVirtual').checked = true;
        document.getElementById('wmSignPos').checked = true;
        updateMeterFieldsVisibility();
        updateSignRowVisibility(parentId);
        g_modal.show();
    }

    function openEditModal(nId) {
        const node = g_nodes.find(n => n.id === nId);
        if (!node) return;
        document.getElementById('modalTitle').textContent = t('watermeter.modal.edit_title', { name: node.name });
        document.getElementById('wmId').value = node.id;
        document.getElementById('wmParentId').value = node.parentId == null ? '' : node.parentId;
        document.getElementById('wmName').value = node.name;
        setBoundPoint(node.sid || null, node.unitScale);
        document.getElementById('wmMaxVolume').value = node.maxVolume == null ? '' : formatThousand(Math.trunc(node.maxVolume));
        document.getElementById('wmDesc').value = node.description || '';
        if (node.sid) document.getElementById('wmTypeMeter').checked = true;
        else document.getElementById('wmTypeVirtual').checked = true;
        if (node.sign === -1) document.getElementById('wmSignNeg').checked = true;
        else document.getElementById('wmSignPos').checked = true;
        updateMeterFieldsVisibility();
        updateSignRowVisibility(node.parentId);
        g_modal.show();
    }

    function updateMeterFieldsVisibility() {
        const isMeter = document.getElementById('wmTypeMeter').checked;
        document.getElementById('wmMeterFields').style.display = isMeter ? '' : 'none';
    }

    function updateSignRowVisibility(parentId) {
        // 根節點（無父）不可選反向 — 隱藏整列並強制 +
        const signRow = document.getElementById('wmSignRow');
        if (parentId == null) {
            signRow.style.display = 'none';
            document.getElementById('wmSignPos').checked = true;
        } else {
            signRow.style.display = '';
        }
    }

    // 把綁定點位寫進 hidden 欄位並更新顯示（含單位 → UnitScale 換算說明，唯讀）
    // dFallbackScale：點位已不在清單時（點位被移除）顯示 DB 存的係數
    function setBoundPoint(szSid, dFallbackScale) {
        const opt = pointBySid(szSid);
        document.getElementById('wmSid').value = szSid || '';
        document.getElementById('wmUnitScale').value = opt ? opt.unitScale : (dFallbackScale != null ? dFallbackScale : 1);
        const disp = document.getElementById('wmPointDisplay');
        const info = document.getElementById('wmUnitScaleInfo');
        if (!szSid) {
            disp.textContent = t('watermeter.form.point_unbound');
            disp.classList.add('text-muted');
            info.style.display = 'none';
            return;
        }
        disp.classList.remove('text-muted');
        if (opt) {
            disp.classList.remove('ui-warn-text');
            disp.textContent = pointLabelOf(opt) + (opt.unit ? ' (' + opt.unit + ')' : '');
            info.style.display = '';
            info.textContent = t('watermeter.form.unit_scale_display', {
                unit: opt.unit, scale: formatScale(opt.unitScale)
            });
        } else {
            disp.classList.add('ui-warn-text');
            UiState.setWarnText(disp, t('watermeter.detail.point_missing') + ' (' + szSid + ')');
            info.style.display = 'none';
        }
    }

    // ============ 儲存 ============
    async function saveCircuit() {
        const szId = document.getElementById('wmId').value;
        const szParentId = document.getElementById('wmParentId').value;
        const szName = document.getElementById('wmName').value.trim();
        const isMeter = document.getElementById('wmTypeMeter').checked;
        const szSid = isMeter ? document.getElementById('wmSid').value : '';
        const dUnitScale = isMeter ? parseFloat(document.getElementById('wmUnitScale').value) || 1 : 1;
        const nMaxVolume = isMeter ? parseThousand(document.getElementById('wmMaxVolume').value) : null;
        const szDesc = document.getElementById('wmDesc').value;
        const isRoot = szParentId === '';
        const nSign = isRoot ? 1 : (document.getElementById('wmSignNeg').checked ? -1 : 1);

        if (!szName) { alert(t('watermeter.msg.name_required')); return; }
        if (isMeter && !szSid) { alert(t('watermeter.msg.sid_required')); return; }

        const dto = {
            name: szName,
            sid: szSid || null,
            unitScale: dUnitScale,
            maxVolume: nMaxVolume,
            sign: nSign,
            description: szDesc || null
        };

        try {
            let res;
            if (szId) {
                res = await fetch(`/WaterMeterSetting/api/tree/${szId}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(dto)
                });
            } else {
                dto.parentId = szParentId === '' ? null : parseInt(szParentId, 10);
                res = await fetch('/WaterMeterSetting/api/tree', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(dto)
                });
            }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(apiErrorMessage(err));
            }
            g_modal.hide();
            await loadTree();
        } catch (err) {
            alert(t('watermeter.msg.save_failed', { msg: err.message }));
        }
    }

    // ============ 刪除 ============
    async function deleteNode(nId) {
        const node = g_nodes.find(n => n.id === nId);
        if (!node) return;
        if (!confirm(t('watermeter.msg.confirm_delete', { name: node.name }))) return;

        try {
            let res = await fetch(`/WaterMeterSetting/api/tree/${nId}`, { method: 'DELETE' });
            if (res.status === 409) {
                if (!confirm(t('watermeter.msg.confirm_delete_children'))) return;
                res = await fetch(`/WaterMeterSetting/api/tree/${nId}?force=true`, { method: 'DELETE' });
            }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(apiErrorMessage(err));
            }
            if (g_selectedId === nId) g_selectedId = null;
            await loadTree();
            if (!g_selectedId) {
                document.getElementById('detailTitle').innerHTML =
                    '<i class="fas fa-hand-pointer me-1"></i>' + escapeHtml(t('watermeter.detail.placeholder_title'));
                document.getElementById('detailArea').innerHTML =
                    '<div class="text-center text-muted py-5"><i class="fas fa-sitemap fa-4x mb-3 d-block" style="opacity:.3"></i><p>' +
                    escapeHtml(t('watermeter.detail.placeholder_body')) + '</p></div>';
            }
        } catch (err) {
            alert(t('watermeter.msg.delete_failed', { msg: err.message }));
        }
    }

    // ============ 兩步驟點位選擇器（設備 → 點位清單＋搜尋）============
    let g_pp = null; // { deviceKey, subUnit, pickedSid }

    function openPointPicker() {
        g_pp = { deviceKey: null, subUnit: null, pickedSid: document.getElementById('wmSid').value || null };

        // 已綁定 → 預先定位到該點位的設備/子單元
        const bound = pointBySid(g_pp.pickedSid);
        if (bound) {
            g_pp.deviceKey = deviceKeyOf(bound);
            g_pp.subUnit = bound.deviceName || null;
        }

        document.getElementById('ppSearch').value = '';
        g_modal.hide();
        renderPickerDeviceList();
        renderPickerPointList();
        g_ppModal.show();
    }

    // bConfirm=true 時把選取寫回表單 hidden 欄位；一律回到編輯 Modal
    function closePointPicker(bConfirm) {
        if (bConfirm && g_pp && g_pp.pickedSid) {
            setBoundPoint(g_pp.pickedSid);
        }
        g_ppModal.hide();
        g_modal.show();
    }

    // 清除綁定並返回
    function clearPickedPoint() {
        setBoundPoint(null);
        g_ppModal.hide();
        g_modal.show();
    }

    // ── 步驟 1：設備清單（多子單元可展開）──
    function renderPickerDeviceList() {
        const box = document.getElementById('ppDeviceList');
        const groups = [
            { source: 'Modbus', label: t('watermeter.group.modbus') },
            { source: 'Calculated', label: t('watermeter.group.calculated') },
            { source: 'DB', label: t('watermeter.group.db') }
        ];
        let html = '';
        groups.forEach(g => {
            const names = [...new Set(g_sidOptions.filter(o => o.source === g.source)
                .map(o => o.coordName || t('watermeter.group.unspecified')))].sort();
            if (names.length === 0) return;
            html += `<div class="pp-group-label">${escapeHtml(g.label)}</div>`;
            names.forEach(n => {
                const szKey = g.source + '|' + n;
                const list = g_sidOptions.filter(o => deviceKeyOf(o) === szKey);
                const subUnits = [...new Set(list.map(o => o.deviceName || ''))].filter(s => s !== '')
                    .sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
                const isOpen = g_pp.deviceKey === szKey;
                html += `<div class="pp-device-item ${isOpen && !subUnits.length ? 'selected' : ''} ${isOpen ? 'open' : ''}"
                              data-key="${escapeHtml(szKey)}">
                    <i class="fas ${subUnits.length ? (isOpen ? 'fa-caret-down' : 'fa-caret-right') : 'fa-server'} me-1"></i>${escapeHtml(n)}
                </div>`;
                if (subUnits.length && isOpen) {
                    html += subUnits.map(s =>
                        `<div class="pp-subunit-item ${g_pp.subUnit === s ? 'selected' : ''}"
                              data-key="${escapeHtml(szKey)}" data-sub="${escapeHtml(s)}">
                            <i class="fas fa-microchip me-1"></i>${escapeHtml(s)}
                        </div>`).join('');
                }
            });
        });
        box.innerHTML = html || '<div class="text-muted small p-2">' + escapeHtml(t('watermeter.picker.no_data')) + '</div>';

        box.querySelectorAll('.pp-device-item').forEach(el => {
            el.addEventListener('click', () => {
                const szKey = el.dataset.key;
                const hasSub = g_sidOptions.some(o => deviceKeyOf(o) === szKey && (o.deviceName || '') !== '');
                if (g_pp.deviceKey === szKey && hasSub) {
                    g_pp.deviceKey = null;   // 再點一次收合
                } else {
                    g_pp.deviceKey = szKey;
                }
                g_pp.subUnit = null;
                renderPickerDeviceList();
                renderPickerPointList();
            });
        });
        box.querySelectorAll('.pp-subunit-item').forEach(el => {
            el.addEventListener('click', () => {
                g_pp.deviceKey = el.dataset.key;
                g_pp.subUnit = el.dataset.sub;
                renderPickerDeviceList();
                renderPickerPointList();
            });
        });
    }

    // ── 步驟 2：點位清單（搜尋過濾 + 已綁高亮 + 單位/換算係數顯示）──
    function renderPickerPointList() {
        if (!g_pp) return;
        const box = document.getElementById('ppPointList');
        const btnConfirm = document.getElementById('ppConfirmBtn');
        if (!g_pp.deviceKey) {
            box.innerHTML = '<div class="text-muted small p-2">' + escapeHtml(t('watermeter.picker.pick_device_first')) + '</div>';
            btnConfirm.disabled = !g_pp.pickedSid;
            return;
        }
        const hasSub = g_sidOptions.some(o => deviceKeyOf(o) === g_pp.deviceKey && (o.deviceName || '') !== '');
        if (hasSub && !g_pp.subUnit) {
            box.innerHTML = '<div class="text-muted small p-2">' + escapeHtml(t('watermeter.picker.pick_subunit_first')) + '</div>';
            btnConfirm.disabled = !g_pp.pickedSid;
            return;
        }

        const szFilter = document.getElementById('ppSearch').value.trim().toLowerCase();
        let list = g_sidOptions.filter(o => deviceKeyOf(o) === g_pp.deviceKey &&
            (!hasSub || (o.deviceName || '') === g_pp.subUnit));
        if (szFilter) {
            // 清單不顯示 SID，搜尋也只比對名稱，免得命中看不到的欄位
            list = list.filter(o => (o.name || '').toLowerCase().includes(szFilter));
        }

        if (list.length === 0) {
            box.innerHTML = '<div class="text-muted small p-2">' + escapeHtml(t('watermeter.picker.no_match')) + '</div>';
        } else {
            box.innerHTML = list.map(o =>
                `<div class="point-list-item ${g_pp.pickedSid === o.sid ? 'selected' : ''}" data-sid="${escapeHtml(o.sid)}">
                    <div>
                        <div class="point-name">${escapeHtml(o.name)}</div>
                    </div>
                    <span class="point-unit">${escapeHtml(o.unit)} × ${escapeHtml(formatScale(o.unitScale))}</span>
                </div>`).join('');
            box.querySelectorAll('.point-list-item').forEach(el => {
                el.addEventListener('click', () => {
                    g_pp.pickedSid = el.dataset.sid;
                    box.querySelectorAll('.point-list-item').forEach(n => n.classList.remove('selected'));
                    el.classList.add('selected');
                    btnConfirm.disabled = false;
                });
            });
            const sel = box.querySelector('.point-list-item.selected');
            if (sel) sel.scrollIntoView({ block: 'nearest' });
        }
        btnConfirm.disabled = !g_pp.pickedSid;
    }

    // ============ 工具 ============
    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    // 對外介面
    window._wm = {
        openCreateModal, openEditModal, saveCircuit, deleteNode,
        openPointPicker, closePointPicker, clearPickedPoint
    };
})();
