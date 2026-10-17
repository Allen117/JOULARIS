// 空調水系統迴路設定頁邏輯（IIFE 封裝，對外掛在 window._cw）
// 結構仿 energymeter.js，差別：無 sign / maxKwh，SID 下拉走 /ChilledWaterSystem/api/sids（後端僅列 RT 系列單位）
(function () {
    'use strict';

    let g_nodes = [];
    let g_sidOptions = [];
    let g_selectedId = null;
    let g_modal = null;

    function t(key, args) {
        return window.i18n ? window.i18n.t(key, args) : key;
    }

    document.addEventListener('DOMContentLoaded', async () => {
        bindTreeDnd();
        g_modal = new bootstrap.Modal(document.getElementById('circuitModal'));
        document.getElementsByName('cwType').forEach(r => r.addEventListener('change', updatePointFieldsVisibility));
        document.getElementById('cwDevice').addEventListener('change', onDeviceChange);
        await Promise.all([loadTree(), loadSidOptions()]);
    });

    async function loadTree() {
        try {
            const res = await fetch('/ChilledWaterSystem/api/tree');
            g_nodes = await res.json();
            renderTree();
            if (g_selectedId) renderDetail(g_selectedId);
        } catch (err) {
            console.error('[ChilledWater] 載入樹失敗', err);
            document.getElementById('treeContainer').innerHTML =
                '<div class="text-danger small">' + escapeHtml(t('chilledwater.error.load_tree', { msg: err.message })) + '</div>';
        }
    }

    async function loadSidOptions() {
        try {
            const res = await fetch('/ChilledWaterSystem/api/sids');
            g_sidOptions = await res.json();
            renderDeviceOptions();
        } catch (err) {
            console.error('[ChilledWater] 載入 SID 清單失敗', err);
        }
    }

    function renderDeviceOptions() {
        const placeholder = t('chilledwater.option.unassigned');
        const devices = [...new Set(g_sidOptions.map(o => o.deviceName || placeholder))].sort();
        const sel = document.getElementById('cwDevice');
        const placeholderOpt = t('chilledwater.modal.device_placeholder');
        sel.innerHTML = '<option value="">' + escapeHtml(placeholderOpt) + '</option>' +
            devices.map(d => `<option value="${escapeHtml(d)}">${escapeHtml(d)}</option>`).join('');
    }

    function renderSidOptionsForDevice(szDeviceName) {
        const sel = document.getElementById('cwSid');
        if (!szDeviceName) {
            sel.innerHTML = '<option value="">' + escapeHtml(t('chilledwater.modal.sid_placeholder')) + '</option>';
            sel.disabled = true;
            return;
        }
        const placeholder = t('chilledwater.option.unassigned');
        const list = g_sidOptions.filter(o => (o.deviceName || placeholder) === szDeviceName);
        const optPlaceholder = t('chilledwater.modal.sid_select');
        sel.innerHTML = '<option value="">' + escapeHtml(optPlaceholder) + '</option>' +
            list.map(o => `<option value="${escapeHtml(o.sid)}">${escapeHtml(o.name)} (${escapeHtml(o.unit)})</option>`).join('');
        sel.disabled = false;
    }

    function onDeviceChange() {
        renderSidOptionsForDevice(document.getElementById('cwDevice').value);
    }

    // ============ 拖曳搬移（共用 TreeDnd；跨層可搬、允許多根、無正負號）============
    // 規則：不能拖到自己 / 子孫底下（TreeDnd.plan 擋）；放到綁點位的節點底下或跨層搬動前先確認；
    // 後端 TreeSortValidator 再驗循環 / 節點存在
    function bindTreeDnd() {
        if (!window.TreeDnd) return;
        window.TreeDnd.bind({
            container: document.getElementById('treeContainer'),
            nodeSelector: '.tree-node',
            getNodes: () => g_nodes,
            canDrop: () => true,
            onDrop: dropNode
        });
    }

    function sortErrorMessage(json) {
        const code = json && json.message ? json.message : 'unknown';
        const key = 'chilledwater.err.' + code;
        const s = t(key);
        return s === key ? code : s;
    }

    async function dropNode(nDragId, nTargetId, pos) {
        const r = window.TreeDnd.plan(g_nodes, nDragId, nTargetId, pos);
        if (!r.ok) {
            if (r.error === 'cycle') alert(t('chilledwater.err.sort_cycle'));
            return;
        }
        if (!r.changed) return;
        if (r.parentChanged) {
            const drag = g_nodes.find(n => n.id === nDragId);
            const parent = g_nodes.find(n => n.id === r.parentId);
            let msg = parent
                ? t('chilledwater.msg.confirm_move', { name: drag.name, parent: parent.name })
                : t('chilledwater.msg.confirm_move_root', { name: drag.name });
            if (parent && parent.sid) msg += '\n' + t('chilledwater.msg.confirm_move_into_point');
            if (!confirm(msg)) return;
        }
        try {
            const res = await fetch('/ChilledWaterSystem/api/tree/sort', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(r.items)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(sortErrorMessage(err));
            }
            await loadTree();
        } catch (err) {
            alert(t('chilledwater.msg.sort_failed', { msg: err.message }));
        }
    }

    function renderTree() {
        const root = document.getElementById('treeContainer');
        const roots = g_nodes.filter(n => n.parentId == null).sort((a, b) => a.sortOrder - b.sortOrder);
        if (roots.length === 0) {
            root.innerHTML = '<div class="text-center text-muted py-4">' +
                '<i class="fas fa-inbox fa-3x mb-2 d-block"></i><div>' +
                escapeHtml(t('chilledwater.tree.empty')) + '</div></div>';
            return;
        }
        root.innerHTML = roots.map(n => renderNode(n)).join('');
        bindNodeEvents();
    }

    function renderNode(node) {
        const children = g_nodes.filter(n => n.parentId === node.id).sort((a, b) => a.sortOrder - b.sortOrder);
        const isPoint = !!node.sid;
        const hasChildren = children.length > 0;

        return `<div class="tree-item">
            <div class="tree-node ${g_selectedId === node.id ? 'active' : ''}" data-id="${node.id}" draggable="true">
                <i class="fas fa-caret-down tree-toggle ${hasChildren ? '' : 'invisible'}"></i>
                <i class="fas ${isPoint ? 'fa-snowflake is-point' : 'fa-folder'} tree-icon"></i>
                <span class="tree-name">${escapeHtml(node.name)}</span>
                <span class="tree-actions">
                    <button title="${escapeHtml(t('chilledwater.action.add_child'))}" onclick="event.stopPropagation();window._cw.openCreateModal(${node.id})">
                        <i class="fas fa-plus"></i>
                    </button>
                    <button title="${escapeHtml(t('chilledwater.action.edit'))}" onclick="event.stopPropagation();window._cw.openEditModal(${node.id})">
                        <i class="fas fa-edit"></i>
                    </button>
                    <button class="del" title="${escapeHtml(t('chilledwater.action.delete'))}" onclick="event.stopPropagation();window._cw.deleteNode(${node.id})">
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

    function renderDetail(nId) {
        const node = g_nodes.find(n => n.id === nId);
        if (!node) return;
        const isPoint = !!node.sid;
        const sidOpt = isPoint ? g_sidOptions.find(o => o.sid === node.sid) : null;
        const sidLabelHtml = sidOpt
            ? escapeHtml(sidOpt.deviceName ? `${sidOpt.deviceName} - ${sidOpt.name}` : sidOpt.name)
            : (isPoint ? UiState.warnHtml(t('chilledwater.detail.sid_missing')) : '');

        document.getElementById('detailTitle').innerHTML =
            `<i class="fas ${isPoint ? 'fa-snowflake text-info' : 'fa-folder text-secondary'} me-1"></i>${escapeHtml(node.name)}`;

        const html = `
            <div class="cw-detail-row">
                <div class="cw-detail-label">${escapeHtml(t('chilledwater.detail.type'))}</div>
                <div class="cw-detail-value">
                    ${isPoint
                        ? '<span class="cw-badge-point"><i class="fas fa-snowflake me-1"></i>' + escapeHtml(t('chilledwater.modal.type_point')) + '</span>'
                        : '<span class="cw-badge-virtual"><i class="fas fa-folder me-1"></i>' + escapeHtml(t('chilledwater.modal.type_virtual')) + '</span>'}
                </div>
            </div>
            <div class="cw-detail-row">
                <div class="cw-detail-label">${escapeHtml(t('chilledwater.detail.name'))}</div>
                <div class="cw-detail-value">${escapeHtml(node.name)}</div>
            </div>
            ${isPoint ? `
            <div class="cw-detail-row">
                <div class="cw-detail-label">${escapeHtml(t('chilledwater.detail.point'))}</div>
                <div class="cw-detail-value">${sidLabelHtml}</div>
            </div>
            <div class="cw-detail-row">
                <div class="cw-detail-label">${escapeHtml(t('chilledwater.detail.unit'))}</div>
                <div class="cw-detail-value">${sidOpt ? escapeHtml(sidOpt.unit) : '<span class="text-muted">-</span>'}</div>
            </div>` : ''}
            <div class="cw-detail-row">
                <div class="cw-detail-label">${escapeHtml(t('chilledwater.detail.desc'))}</div>
                <div class="cw-detail-value">${node.description ? escapeHtml(node.description) : '<span class="text-muted">' + escapeHtml(t('chilledwater.detail.none')) + '</span>'}</div>
            </div>
            <div class="mt-3 d-flex gap-2">
                <button class="btn btn-sm btn-primary" onclick="window._cw.openCreateModal(${node.id})">
                    <i class="fas fa-plus me-1"></i>${escapeHtml(t('chilledwater.action.add_child'))}
                </button>
                <button class="btn btn-sm btn-outline-primary" onclick="window._cw.openEditModal(${node.id})">
                    <i class="fas fa-edit me-1"></i>${escapeHtml(t('chilledwater.action.edit'))}
                </button>
                <button class="btn btn-sm btn-outline-danger" onclick="window._cw.deleteNode(${node.id})">
                    <i class="fas fa-trash-alt me-1"></i>${escapeHtml(t('chilledwater.action.delete'))}
                </button>
            </div>`;
        document.getElementById('detailArea').innerHTML = html;
    }

    function openCreateModal(parentId) {
        document.getElementById('modalTitle').textContent = parentId == null
            ? t('chilledwater.modal.title_create')
            : t('chilledwater.modal.title_create_child');
        document.getElementById('cwId').value = '';
        document.getElementById('cwParentId').value = parentId == null ? '' : parentId;
        document.getElementById('cwName').value = '';
        document.getElementById('cwDevice').value = '';
        renderSidOptionsForDevice('');
        document.getElementById('cwDesc').value = '';
        document.getElementById('cwTypeVirtual').checked = true;
        updatePointFieldsVisibility();
        g_modal.show();
    }

    function openEditModal(nId) {
        const node = g_nodes.find(n => n.id === nId);
        if (!node) return;
        document.getElementById('modalTitle').textContent = t('chilledwater.modal.title_edit') + '：' + node.name;
        document.getElementById('cwId').value = node.id;
        document.getElementById('cwParentId').value = node.parentId == null ? '' : node.parentId;
        document.getElementById('cwName').value = node.name;
        const placeholder = t('chilledwater.option.unassigned');
        const opt = node.sid ? g_sidOptions.find(o => o.sid === node.sid) : null;
        const szDev = opt ? (opt.deviceName || placeholder) : '';
        document.getElementById('cwDevice').value = szDev;
        renderSidOptionsForDevice(szDev);
        document.getElementById('cwSid').value = node.sid || '';
        document.getElementById('cwDesc').value = node.description || '';
        if (node.sid) document.getElementById('cwTypePoint').checked = true;
        else document.getElementById('cwTypeVirtual').checked = true;
        updatePointFieldsVisibility();
        g_modal.show();
    }

    function updatePointFieldsVisibility() {
        const isPoint = document.getElementById('cwTypePoint').checked;
        document.getElementById('cwPointFields').style.display = isPoint ? '' : 'none';
    }

    async function saveCircuit() {
        const szId = document.getElementById('cwId').value;
        const szParentId = document.getElementById('cwParentId').value;
        const szName = document.getElementById('cwName').value.trim();
        const isPoint = document.getElementById('cwTypePoint').checked;
        const szSid = isPoint ? document.getElementById('cwSid').value : '';
        const szDesc = document.getElementById('cwDesc').value;

        if (!szName) { alert(t('chilledwater.error.name_required')); return; }
        if (isPoint && !szSid) { alert(t('chilledwater.error.sid_required')); return; }

        const dto = {
            name: szName,
            sid: szSid || null,
            description: szDesc || null
        };

        try {
            let res;
            if (szId) {
                res = await fetch(`/ChilledWaterSystem/api/tree/${szId}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(dto)
                });
            } else {
                dto.parentId = szParentId === '' ? null : parseInt(szParentId, 10);
                res = await fetch('/ChilledWaterSystem/api/tree', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(dto)
                });
            }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            g_modal.hide();
            await loadTree();
        } catch (err) {
            alert(t('chilledwater.error.save_failed', { msg: err.message }));
        }
    }

    async function deleteNode(nId) {
        const node = g_nodes.find(n => n.id === nId);
        if (!node) return;
        if (!confirm(t('chilledwater.confirm.delete', { name: node.name }))) return;

        try {
            let res = await fetch(`/ChilledWaterSystem/api/tree/${nId}`, { method: 'DELETE' });
            if (res.status === 409) {
                if (!confirm(t('chilledwater.confirm.delete_with_children'))) return;
                res = await fetch(`/ChilledWaterSystem/api/tree/${nId}?force=true`, { method: 'DELETE' });
            }
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            if (g_selectedId === nId) g_selectedId = null;
            await loadTree();
            if (!g_selectedId) {
                document.getElementById('detailTitle').innerHTML =
                    '<i class="fas fa-hand-pointer me-1"></i>' + escapeHtml(t('chilledwater.hint.select_node'));
                document.getElementById('detailArea').innerHTML =
                    '<div class="text-center text-muted py-5"><i class="fas fa-tint fa-4x mb-3 d-block" style="opacity:.3"></i><p>' +
                    escapeHtml(t('chilledwater.hint.select_to_view')) + '</p></div>';
            }
        } catch (err) {
            alert(t('chilledwater.error.delete_failed', { msg: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._cw = { openCreateModal, openEditModal, saveCircuit, deleteNode };
})();
