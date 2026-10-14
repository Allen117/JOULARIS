// 樹狀清單拖曳搬移共用模組（掛 window.TreeDnd）
// 供 EnergyMeter / ChilledWaterSystem / WaterMeterSetting / GasMeterSetting / LogicFlow / Designer 頁面樹共用。
//
// 設計：
//   - plan()：純函式，吃平坦清單 [{ id, parentId, sortOrder }] 算出「拖 dragId 到 targetId 的 pos」後，
//     新舊兩層需要回寫的 { id, parentId, sortOrder } 清單；自帶 self / 循環（拖進自己子孫）防呆。
//   - bind()：用事件委派把 HTML5 DnD 綁在容器上一次即可（樹每次 render 整塊重建也不用重綁）。
//     依滑鼠在目標列的垂直位置判定三區：上 1/4 = before、下 1/4 = after、中間 = into（變成子節點）。
//     canDrop(dragId, targetId, pos) 回 false 時依序退回 before/after，都不行就不給放。
//     放在容器空白處 = pos 'root'（搬到根層末端），由 canDrop(dragId, null, 'root') 決定是否允許。
(function () {
    'use strict';

    const CLS_DRAGGING = 'dragging';
    const CLS_BEFORE = 'drop-before';
    const CLS_AFTER = 'drop-after';
    const CLS_INTO = 'drop-into';
    const CLS_ROOT = 'drop-root';
    const ALL_CLS = [CLS_BEFORE, CLS_AFTER, CLS_INTO];

    function pidOf(n) { return n.parentId == null ? null : n.parentId; }

    /** ancestorId 的子樹內是否含 id（不含 ancestorId 自己） */
    function isDescendant(nodes, ancestorId, id) {
        const byId = new Map(nodes.map(n => [n.id, n]));
        let cur = byId.get(id);
        const seen = new Set();
        while (cur && pidOf(cur) != null) {
            if (seen.has(cur.id)) return false; // 資料已壞（循環），當作不是子孫避免無窮迴圈
            seen.add(cur.id);
            if (pidOf(cur) === ancestorId) return true;
            cur = byId.get(pidOf(cur));
        }
        return false;
    }

    /**
     * 規劃搬移。
     * @returns {{ ok:true, parentId, parentChanged:boolean, changed:boolean, items:[{id,parentId,sortOrder}] } | { ok:false, error:'self'|'cycle'|'missing' }}
     */
    function plan(nodes, dragId, targetId, pos) {
        const drag = nodes.find(n => n.id === dragId);
        if (!drag) return { ok: false, error: 'missing' };

        let target = null;
        if (pos !== 'root') {
            target = nodes.find(n => n.id === targetId);
            if (!target) return { ok: false, error: 'missing' };
            if (target.id === dragId) return { ok: false, error: 'self' };
            if (isDescendant(nodes, dragId, target.id)) return { ok: false, error: 'cycle' };
        }

        const oldParent = pidOf(drag);
        const newParent = pos === 'into' ? target.id : (pos === 'root' ? null : pidOf(target));
        const sortFn = (a, b) => a.sortOrder - b.sortOrder || a.id - b.id;

        const siblings = nodes.filter(n => pidOf(n) === newParent && n.id !== dragId).sort(sortFn);
        let nInsertAt;
        if (pos === 'before') nInsertAt = siblings.findIndex(n => n.id === target.id);
        else if (pos === 'after') nInsertAt = siblings.findIndex(n => n.id === target.id) + 1;
        else nInsertAt = siblings.length;
        if (nInsertAt < 0) nInsertAt = siblings.length;
        siblings.splice(nInsertAt, 0, drag);

        const items = siblings.map((n, i) => ({ id: n.id, parentId: newParent, sortOrder: i }));
        const parentChanged = oldParent !== newParent;
        if (parentChanged) {
            nodes.filter(n => pidOf(n) === oldParent && n.id !== dragId).sort(sortFn)
                .forEach((n, i) => items.push({ id: n.id, parentId: oldParent, sortOrder: i }));
        }

        // 同層且順序沒變 → 不必送
        const before = nodes.filter(n => pidOf(n) === newParent).sort(sortFn).map(n => n.id).join(',');
        const after = siblings.map(n => n.id).join(',');
        const changed = parentChanged || before !== after;

        return { ok: true, parentId: newParent, parentChanged, changed, items };
    }

    function clearMarks(container) {
        container.querySelectorAll('.' + ALL_CLS.join(',.')).forEach(el => el.classList.remove(...ALL_CLS));
        container.classList.remove(CLS_ROOT);
    }

    /**
     * 綁定拖曳（事件委派，綁一次）。
     * @param {Object} o
     * @param {HTMLElement} o.container       樹容器
     * @param {string} o.nodeSelector         節點列 selector（需帶 draggable="true" 與 data-id）
     * @param {(el:HTMLElement)=>any} [o.idOf] 由列元素取 id（預設 parseInt(data-id)）
     * @param {()=>Array} [o.getNodes]       平坦清單取得函式；有給就在拖曳中擋掉「自己的子孫」落點
     * @param {(dragId, targetId, pos)=>boolean} o.canDrop  規則防呆；pos ∈ before|after|into|root
     * @param {(dragId, targetId, pos)=>void} o.onDrop      放下回呼（可 async）
     * @param {number} [o.edgeRatio=0.25]     上下緣判定比例
     */
    function bind(o) {
        const container = o.container;
        if (!container || container.__treeDndBound) return;
        container.__treeDndBound = true;
        const idOf = o.idOf || (el => parseInt(el.dataset.id, 10));
        const edge = o.edgeRatio || 0.25;
        let dragId = null;

        function rowOf(e) {
            const el = e.target && e.target.closest ? e.target.closest(o.nodeSelector) : null;
            return el && container.contains(el) ? el : null;
        }

        function zoneOf(e, el) {
            const r = el.getBoundingClientRect();
            const y = e.clientY - r.top;
            if (y < r.height * edge) return 'before';
            if (y > r.height * (1 - edge)) return 'after';
            return 'into';
        }

        // 回傳可放的 pos，或 null
        function resolve(e, el) {
            if (dragId == null) return null;
            if (!el) return o.canDrop(dragId, null, 'root') ? 'root' : null;
            const targetId = idOf(el);
            if (targetId === dragId) return null;
            // 自己的子孫一律不給放（拖曳中就不顯示落點，不等放下才報錯）
            if (o.getNodes && isDescendant(o.getNodes(), dragId, targetId)) return null;
            const z = zoneOf(e, el);
            const order = z === 'into' ? ['into', (e.clientY - el.getBoundingClientRect().top) < el.getBoundingClientRect().height / 2 ? 'before' : 'after']
                : [z];
            for (const p of order) {
                if (o.canDrop(dragId, targetId, p)) return p;
            }
            return null;
        }

        container.addEventListener('dragstart', (e) => {
            const el = rowOf(e);
            if (!el) return;
            dragId = idOf(el);
            el.classList.add(CLS_DRAGGING);
            e.dataTransfer.effectAllowed = 'move';
            try { e.dataTransfer.setData('text/plain', String(dragId)); } catch (_) { /* 相容 */ }
        });

        container.addEventListener('dragend', () => {
            dragId = null;
            container.querySelectorAll('.' + CLS_DRAGGING).forEach(el => el.classList.remove(CLS_DRAGGING));
            clearMarks(container);
        });

        container.addEventListener('dragover', (e) => {
            if (dragId == null) return;
            const el = rowOf(e);
            const pos = resolve(e, el);
            clearMarks(container);
            if (!pos) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            if (pos === 'root') container.classList.add(CLS_ROOT);
            else el.classList.add(pos === 'before' ? CLS_BEFORE : pos === 'after' ? CLS_AFTER : CLS_INTO);
        });

        container.addEventListener('dragleave', (e) => {
            if (!container.contains(e.relatedTarget)) clearMarks(container);
        });

        container.addEventListener('drop', async (e) => {
            if (dragId == null) return;
            const el = rowOf(e);
            const pos = resolve(e, el);
            clearMarks(container);
            if (!pos) return;
            e.preventDefault();
            const nDrag = dragId;
            const nTarget = el ? idOf(el) : null;
            dragId = null;
            await o.onDrop(nDrag, nTarget, pos);
        });
    }

    window.TreeDnd = { plan, bind, isDescendant };
})();
