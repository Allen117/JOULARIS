// LogicFlow 左側樹狀選單 + 右側內容區切換
(function () {
    const S = window.__lfNS;

    // =========== 平坦 → 樹狀 ===========
    function buildTree(flat) {
        const map = {};
        const roots = [];
        for (const n of flat) {
            map[n.id] = {
                id: n.id, name: n.name, type: n.nodeType, isEnabled: n.isEnabled,
                sortOrder: n.sortOrder, parentId: n.parentId,
                children: [], expanded: S.expandedSet.has(n.id)
            };
        }
        for (const n of flat) {
            const node = map[n.id];
            if (n.parentId && map[n.parentId]) {
                map[n.parentId].children.push(node);
            } else {
                roots.push(node);
            }
        }
        const sortFn = (a, b) => a.sortOrder - b.sortOrder;
        function sortAll(nodes) { nodes.sort(sortFn); nodes.forEach(n => sortAll(n.children)); }
        sortAll(roots);
        return roots;
    }

    async function loadTree() {
        try {
            S.flatNodes = await S.apiFetch('/tree');
            S.treeData = buildTree(S.flatNodes);
            renderTree();
        } catch (e) {
            document.getElementById('treeContainer').innerHTML =
                `<p class="text-danger text-center small mt-5"><i class="fas fa-exclamation-triangle me-1"></i>${S.escHtml(S.t("logicflow.error.load_failed", { msg: e.message }))}</p>`;
        }
    }

    function renderTree() {
        const container = document.getElementById('treeContainer');
        if (S.treeData.length === 0) {
            container.innerHTML = '<p class="text-muted text-center small mt-5">' + S.escHtml(S.t('logicflow.tree.empty')) + '</p>';
            return;
        }
        container.innerHTML = buildNodes(S.treeData);
    }

    function buildNodes(nodes) {
        let html = '';
        for (const n of nodes) {
            const isFolder = n.type === 'folder';
            const isActive = n.id === S.selectedId ? ' active' : '';
            const isDisabled = !n.isEnabled ? ' disabled-node' : '';

            let icon, iconTitle;
            if (isFolder && n.isEnabled) {
                icon = n.expanded ? 'fas fa-folder-open text-warning' : 'fas fa-folder text-warning';
                iconTitle = S.t('logicflow.tree.folder');
            } else if (isFolder && !n.isEnabled) {
                icon = n.expanded ? 'fas fa-folder-open text-secondary' : 'fas fa-folder text-secondary';
                iconTitle = S.t('logicflow.tree.folder_disabled');
            } else if (n.isEnabled) {
                icon = 'fas fa-file-code text-info';
                iconTitle = S.t('logicflow.tree.logic_enabled');
            } else {
                icon = 'fas fa-file-code text-secondary';
                iconTitle = S.t('logicflow.tree.logic_disabled');
            }

            html += '<div class="tree-node">';
            html += `<div class="tree-item${isActive}${isDisabled}" data-id="${n.id}" draggable="true" onclick="window._lf.select(${n.id})" ondblclick="window._lf.toggle(${n.id})">`;

            if (isFolder) {
                html += `<span class="tree-toggle" onclick="event.stopPropagation(); window._lf.toggle(${n.id})">`;
                html += n.expanded ? '<i class="fas fa-caret-down"></i>' : '<i class="fas fa-caret-right"></i>';
                html += '</span>';
            } else {
                html += '<span style="width:16px;display:inline-block"></span>';
            }

            html += `<span class="node-icon" title="${iconTitle}"><i class="${icon}"></i></span>`;
            html += `<span class="node-label" title="${S.escHtml(n.name)}">${S.escHtml(n.name)}</span>`;

            // 操作按鈕
            html += '<span class="node-actions">';
            if (isFolder) {
                html += `<button title="${S.escHtml(S.t('logicflow.action.add_folder'))}" onclick="event.stopPropagation(); window._lf.addChild(${n.id},'folder')"><i class="fas fa-folder-plus"></i></button>`;
                html += `<button title="${S.escHtml(S.t('logicflow.action.add_logic'))}" onclick="event.stopPropagation(); window._lf.addChild(${n.id},'logic')"><i class="fas fa-file-circle-plus"></i></button>`;
            }
            if (n.isEnabled) {
                html += `<button title="${S.escHtml(S.t('logicflow.action.disable'))}" onclick="event.stopPropagation(); window._lf.toggleEnabled(${n.id},false)"><i class="fas fa-toggle-on text-success"></i></button>`;
            } else {
                html += `<button title="${S.escHtml(S.t('logicflow.action.enable'))}" onclick="event.stopPropagation(); window._lf.toggleEnabled(${n.id},true)"><i class="fas fa-toggle-off text-secondary"></i></button>`;
            }
            html += `<button title="${S.escHtml(S.t('logicflow.action.rename'))}" onclick="event.stopPropagation(); window._lf.rename(${n.id})"><i class="fas fa-pen"></i></button>`;
            html += `<button title="${S.escHtml(S.t('logicflow.action.delete'))}" onclick="event.stopPropagation(); window._lf.remove(${n.id})"><i class="fas fa-trash-alt text-danger"></i></button>`;
            html += '</span>';
            html += '</div>';

            if (isFolder && n.children.length > 0 && n.expanded) {
                html += '<div class="tree-children">' + buildNodes(n.children) + '</div>';
            }

            html += '</div>';
        }
        return html;
    }

    // =========== 操作 ===========
    function select(id) {
        S.selectedId = id;
        const node = S.findNode(id, S.treeData);
        renderTree();
        updateContent(node);
    }

    function toggle(id) {
        const node = S.findNode(id, S.treeData);
        if (!node || node.type !== 'folder') return;
        node.expanded = !node.expanded;
        if (node.expanded) S.expandedSet.add(id); else S.expandedSet.delete(id);
        renderTree();
    }

    async function addRoot(type) {
        const name = type === 'folder' ? S.t('logicflow.default.new_folder') : S.t('logicflow.default.new_logic');
        const sortOrder = S.treeData.length;
        try {
            const res = await S.apiFetch('/tree', {
                method: 'POST',
                body: JSON.stringify({ parentId: null, name, nodeType: type, sortOrder })
            });
            S.selectedId = res.id;
            await loadTree();
            updateContent(S.findNode(res.id, S.treeData));
        } catch (e) { alert(S.t('logicflow.error.add_failed', { msg: e.message })); }
    }

    async function addChild(parentId, type) {
        const parent = S.findNode(parentId, S.treeData);
        if (!parent || parent.type !== 'folder') return;
        S.expandedSet.add(parentId);
        const name = type === 'folder' ? '新資料夾' : '新邏輯';
        const sortOrder = parent.children.length;
        try {
            const res = await S.apiFetch('/tree', {
                method: 'POST',
                body: JSON.stringify({ parentId, name, nodeType: type, sortOrder })
            });
            S.selectedId = res.id;
            await loadTree();
            updateContent(S.findNode(res.id, S.treeData));
        } catch (e) { alert(S.t('logicflow.error.add_failed', { msg: e.message })); }
    }

    function rename(id) {
        S.renameTargetId = id;
        const node = S.findNode(id, S.treeData);
        if (!node) return;
        document.getElementById('renameInput').value = node.name;
        new bootstrap.Modal(document.getElementById('renameModal')).show();
        setTimeout(() => document.getElementById('renameInput').select(), 300);
    }

    async function confirmRename() {
        const val = document.getElementById('renameInput').value.trim();
        if (!val || S.renameTargetId == null) return;
        try {
            await S.apiFetch(`/tree/${S.renameTargetId}/rename`, {
                method: 'PUT',
                body: JSON.stringify({ name: val })
            });
            await loadTree();
            if (S.selectedId === S.renameTargetId) {
                updateContent(S.findNode(S.renameTargetId, S.treeData));
            }
        } catch (e) { alert(S.t('logicflow.error.rename_failed', { msg: e.message })); }
        bootstrap.Modal.getInstance(document.getElementById('renameModal'))?.hide();
        S.renameTargetId = null;
    }

    async function remove(id) {
        if (!confirm(S.t('logicflow.confirm.delete_item'))) return;
        try {
            await S.apiFetch(`/tree/${id}`, { method: 'DELETE' });
            if (S.selectedId === id) { S.selectedId = null; clearContent(); }
            await loadTree();
        } catch (e) { alert(S.t('logicflow.error.delete_failed', { msg: e.message })); }
    }

    async function toggleEnabled(id, isEnabled) {
        try {
            await S.apiFetch(`/tree/${id}/toggle`, {
                method: 'PUT',
                body: JSON.stringify({ isEnabled })
            });
            await loadTree();
            if (S.selectedId === id) {
                updateContent(S.findNode(id, S.treeData));
            }
        } catch (e) { alert(S.t('logicflow.error.toggle_failed', { msg: e.message })); }
    }

    // =========== 樹層級複製 / 貼上（右鍵選單；刻意無快捷鍵，決策 1） ===========
    // 與畫布 Ctrl+C/V 的差別：這裡複製的是整條邏輯 / 整個資料夾（含所有子項），
    // 且貼上後**點位綁定全部清空**（由後端 LogicFlowDiagramBindingStripper 執行）。
    function readTreeClipboard() {
        try {
            const raw = localStorage.getItem(S.TREE_CLIPBOARD_KEY);
            if (!raw) return null;
            const obj = JSON.parse(raw);
            return (obj && typeof obj.id === 'number') ? obj : null;
        } catch (_) { return null; }
    }

    function copyTreeNode(id) {
        const node = S.findNode(id, S.treeData);
        if (!node) return;
        localStorage.setItem(S.TREE_CLIPBOARD_KEY, JSON.stringify({ id: node.id, name: node.name }));
        alert(S.t('logicflow.success.copy', { name: node.name }));
    }

    // 目標解析（決策 8）：資料夾 → 貼為子項；logic 不能有子項 → 退回其所在層；
    // 空白處 / 目標已不存在 → 根層
    function resolvePasteParentId(targetId) {
        if (targetId == null) return null;
        const target = S.findNode(targetId, S.treeData);
        if (!target) return null;
        if (target.type === 'folder') return target.id;
        return target.parentId != null ? target.parentId : null;
    }

    async function pasteTreeNode(targetId) {
        const clip = readTreeClipboard();
        if (!clip) return;

        // 來源可能在別的分頁被刪掉了 —— flatNodes 是剛 loadTree 的快照，足以判斷
        if (!S.flatNodes.some(n => n.id === clip.id)) {
            localStorage.removeItem(S.TREE_CLIPBOARD_KEY);
            alert(S.t('logicflow.error.clipboard_source_missing'));
            return;
        }

        const parentId = resolvePasteParentId(targetId);
        try {
            const res = await S.apiFetch(`/tree/${clip.id}/copy`, {
                method: 'POST',
                body: JSON.stringify({ targetParentId: parentId })
            });
            if (parentId != null) S.expandedSet.add(parentId);
            S.selectedId = res.id;
            await loadTree();
            updateContent(S.findNode(res.id, S.treeData));
        } catch (e) { alert(S.t('logicflow.error.copy_failed', { msg: e.message })); }
    }

    // =========== 拖曳搬移（決策：只能放進資料夾 / 根層；邏輯不能有子項） ===========
    // 防呆：
    //   - 'into' 只允許目標為資料夾（logic 不能有子項）；放到空白處 = 搬到根層末端
    //   - 不能搬到自己 / 自己的子孫底下（TreeDnd.plan 擋，後端 TreeSortValidator 再擋一次）
    //   - 目標資料夾已停用 → 先確認，搬入後子樹由後端一併停用（Engine 只看每條 logic 自己的 IsEnabled）
    function canDropNode(nDragId, nTargetId, pos) {
        if (pos === 'root') return true;
        const target = S.flatNodes.find(n => n.id === nTargetId);
        if (!target) return false;
        if (pos === 'into') return target.nodeType === 'folder';
        return true;
    }

    function moveErrorMessage(e) {
        // apiFetch 失敗時丟 Error(回應原文)；後端 400 帶 { message: code }
        let code = null;
        try { code = JSON.parse(e.message).message; } catch (_) { /* 非 JSON */ }
        if (!code) return e.message;
        const key = 'logicflow.err.' + code;
        const txt = S.t(key);
        return txt === key ? code : txt;
    }

    function subtreeHasEnabled(nRootId) {
        const stack = [nRootId];
        while (stack.length) {
            const nId = stack.pop();
            const n = S.flatNodes.find(x => x.id === nId);
            if (!n) continue;
            if (n.isEnabled) return true;
            S.flatNodes.filter(c => c.parentId === nId).forEach(c => stack.push(c.id));
        }
        return false;
    }

    async function dropNode(nDragId, nTargetId, pos) {
        const r = window.TreeDnd.plan(S.flatNodes, nDragId, nTargetId, pos);
        if (!r.ok) {
            if (r.error === 'cycle') alert(S.t('logicflow.err.sort_cycle'));
            return;
        }
        if (!r.changed) return;

        const parent = r.parentId != null ? S.flatNodes.find(n => n.id === r.parentId) : null;
        if (parent && !parent.isEnabled && subtreeHasEnabled(nDragId)) {
            if (!confirm(S.t('logicflow.confirm.move_into_disabled', { name: parent.name }))) return;
        }

        const nSortOrder = r.items.find(i => i.id === nDragId).sortOrder;
        try {
            const res = await S.apiFetch(`/tree/${nDragId}/move`, {
                method: 'PUT',
                body: JSON.stringify({ targetParentId: r.parentId, sortOrder: nSortOrder })
            });
            if (r.parentId != null) S.expandedSet.add(r.parentId);
            await loadTree();
            // 連動停用可能改到目前開啟的邏輯 → 重新載入右側（標題徽章 / 啟用狀態）
            if (res && res.disabledCount > 0 && S.selectedId != null) {
                updateContent(S.findNode(S.selectedId, S.treeData));
            }
        } catch (e) { alert(S.t('logicflow.error.move_failed', { msg: moveErrorMessage(e) })); }
    }

    // ── 右鍵選單 ──
    function showTreeCtxMenu(e, targetId) {
        e.preventDefault();
        e.stopPropagation();
        S.hideCtxMenu();
        S.treeCtxTargetId = targetId;

        const menu = document.getElementById('treeCtxMenu');
        if (!menu) return;

        // 空白區：只有「貼上（貼到根層）」；節點上：複製 + 貼上
        const copyItem = menu.querySelector('.tree-ctx-copy');
        const pasteItem = menu.querySelector('.tree-ctx-paste');
        const pasteLabel = menu.querySelector('.tree-ctx-paste-label');
        copyItem.style.display = targetId == null ? 'none' : '';

        const clip = readTreeClipboard();
        // 剪貼簿為空 → 不顯示「貼上」（兩項都不顯示時整個選單就不必彈）
        pasteItem.style.display = clip ? '' : 'none';
        if (clip) {
            pasteLabel.textContent = targetId == null
                ? S.t('logicflow.ctx.tree_paste_root')
                : S.t('logicflow.ctx.tree_paste');
            pasteItem.title = clip.name || '';
        }
        if (targetId == null && !clip) return;

        S.showMenuAt(menu, e.clientX, e.clientY);
    }

    // 樹容器事件一次性綁定（樹 HTML 每次 renderTree 都整塊重建，故用委派）
    function attachTreeEvents() {
        const container = document.getElementById('treeContainer');
        if (!container) return;

        container.addEventListener('contextmenu', (e) => {
            const item = e.target.closest('.tree-item');
            showTreeCtxMenu(e, item ? parseInt(item.dataset.id) : null);
        });

        // 拖曳搬移（共用 TreeDnd；委派綁一次，樹重繪不用重綁）
        if (window.TreeDnd) {
            window.TreeDnd.bind({
                container,
                nodeSelector: '.tree-item',
                getNodes: () => S.flatNodes,
                canDrop: canDropNode,
                onDrop: dropNode
            });
        }

        const menu = document.getElementById('treeCtxMenu');
        if (!menu) return;
        menu.querySelector('.tree-ctx-copy').addEventListener('click', (e) => {
            e.stopPropagation();
            S.hideCtxMenu();
            if (S.treeCtxTargetId != null) copyTreeNode(S.treeCtxTargetId);
        });
        menu.querySelector('.tree-ctx-paste').addEventListener('click', (e) => {
            e.stopPropagation();
            const targetId = S.treeCtxTargetId;
            S.hideCtxMenu();
            pasteTreeNode(targetId);
        });
    }

    // =========== 右側內容 ===========
    function updateContent(node) {
        const title = document.getElementById('contentTitle');
        const area = document.getElementById('contentArea');
        if (!node) { clearContent(); return; }

        if (node.type === 'folder') {
            const folderColor = node.isEnabled ? 'text-warning' : 'text-secondary';
            const folderBadge = node.isEnabled
                ? '<span class="badge bg-success ms-2">' + S.escHtml(S.t('logicflow.status.enabled')) + '</span>'
                : '<span class="badge bg-secondary ms-2">' + S.escHtml(S.t('logicflow.status.disabled')) + '</span>';
            title.innerHTML = `<i class="fas fa-folder-open ${folderColor} me-1"></i>${S.escHtml(node.name)}${folderBadge}`;
            const childCount = node.children ? node.children.length : 0;
            area.innerHTML = `
                <div class="text-center text-muted mt-5">
                    <i class="fas fa-folder-open fa-4x mb-3 d-block" style="opacity:.3"></i>
                    <p>${S.escHtml(S.t('logicflow.folder.label', { name: node.name }))}</p>
                    <p class="small">${S.escHtml(S.t('logicflow.folder.contains_items', { count: childCount }))}</p>
                    ${!node.isEnabled ? '<p class="small text-danger"><i class="fas fa-ban me-1"></i>' + S.escHtml(S.t('logicflow.folder.disabled_warning')) + '</p>' : ''}
                </div>`;
        } else {
            const statusColor = node.isEnabled ? 'text-info' : 'text-secondary';
            const statusBadge = node.isEnabled
                ? '<span class="badge bg-success ms-2">' + S.escHtml(S.t('logicflow.status.enabled')) + '</span>'
                : '<span class="badge bg-secondary ms-2">' + S.escHtml(S.t('logicflow.status.disabled')) + '</span>';
            title.innerHTML = `<i class="fas fa-file-code ${statusColor} me-1"></i>${S.escHtml(node.name)}${statusBadge}`;
            area.innerHTML = '<div id="diagramCanvas" class="diagram-canvas"></div>';
            // 只檢查自身邏輯是否啟用（不受上層資料夾影響）
            S._isLogicEnabled = !!node.isEnabled;
            S.initCanvas(node.id);
        }
    }

    function clearContent() {
        S.stopRealtimePolling();
        document.getElementById('contentTitle').innerHTML = '<i class="fas fa-hand-pointer me-1"></i>' + S.escHtml(S.t('logicflow.hint.select_logic'));
        document.getElementById('contentArea').innerHTML = `
            <div class="text-center text-muted mt-5">
                <i class="fas fa-project-diagram fa-4x mb-3 d-block" style="opacity:.3"></i>
                <p>${S.escHtml(S.t('logicflow.hint.select_to_edit'))}</p>
            </div>`;
    }

    // 暴露給其他模組
    S.loadTree = loadTree;
    S.renderTree = renderTree;
    S.select = select;
    S.toggle = toggle;
    S.addRoot = addRoot;
    S.addChild = addChild;
    S.rename = rename;
    S.confirmRename = confirmRename;
    S.remove = remove;
    S.toggleEnabled = toggleEnabled;
    S.copyTreeNode = copyTreeNode;
    S.pasteTreeNode = pasteTreeNode;
    S.showTreeCtxMenu = showTreeCtxMenu;
    S.attachTreeEvents = attachTreeEvents;
    S.updateContent = updateContent;
    S.clearContent = clearContent;
})();
