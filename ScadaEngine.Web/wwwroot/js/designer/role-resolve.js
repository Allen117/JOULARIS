// ============================================================
// Designer — 角色綁定解析（plan 2026-10-21-designer-layout-import）
// 掛 window._roleResolve（node 亦可 require，供 scratchpad 斷言）
// ============================================================
// widget 的 props.objRoleBind = { 槽位 key: 角色 }（例：{ szSid: 'RUN' }、
// { szSidRun: 'RUN', szCidStartStop: 'STARTSTOP' }），本模組把它依「某台 Device
// 的點位集合」解析成實際 SID / 點位名。純函式、不碰 DOM。
//
// 使用端：
//   - designer/page-import.js（匯入頁面檔：每台設備一組 widget）
//   - P2 設備範本 resolveTemplate（plan 2026-10-14-designer-equipment-templates，
//     未開工）直接沿用 resolveRoleBindings，不另寫一份
//
// 角色判定唯一真相 = role-aliases.js 的 window._equipRoleMatch.resolveEquipRole
// （呼叫時才解析，載入順序只需在呼叫前兩者都已載入）。規則與 equip-autobind.js
// buildEquipSuggestions 相同：每個點位只解析一次、只落一個角色；同角色多筆取第一筆。
// ============================================================

(function (root) {
    'use strict';

    // 槽位 key → 點位名 key：szSid / szCid → szPointName；szSidRun → szRunName；szCidStartStop → szStartStopName
    function nameKeyOf(szSlotKey) {
        if (szSlotKey === 'szSid' || szSlotKey === 'szCid') return 'szPointName';
        const m = /^sz(?:Sid|Cid)(.+)$/.exec(szSlotKey || '');
        return m ? 'sz' + m[1] + 'Name' : szSlotKey + 'Name';
    }

    // 點位顯示名（與 picker / equip-autobind 寫入的 szFullName 一致）
    function pointDisplayName(p) {
        if (!p) return '';
        return p.szDeviceLabel ? p.szDeviceLabel + ' / ' + p.szName : p.szName;
    }

    // 一組 widget state 內用到的角色（去重、保留出現順序）
    function collectRoles(arrWidgetState) {
        const seen = [];
        (arrWidgetState || []).forEach(ws => {
            const bind = ws && ws.props && ws.props.objRoleBind;
            if (!bind) return;
            Object.keys(bind).forEach(k => { if (bind[k] && seen.indexOf(bind[k]) < 0) seen.push(bind[k]); });
        });
        return seen;
    }

    // 角色 → 點位命中（不寫 widget）；回 { arrItems: [{ szRole, point, bMulti, nHits }], nHit, nTotal, arrUnresolved }
    function matchRoles(arrRoles, arrDevicePoints) {
        const resolver = root._equipRoleMatch && root._equipRoleMatch.resolveEquipRole;
        const byRole = {};
        if (resolver && arrRoles.length > 0) {
            (arrDevicePoints || []).forEach(p => {
                const r = resolver(p.szName || '', arrRoles);
                if (r) (byRole[r] = byRole[r] || []).push(p);
            });
        }
        const arrItems = arrRoles.map(szRole => {
            const hits = byRole[szRole] || [];
            return { szRole: szRole, point: hits[0] || null, bMulti: hits.length > 1, nHits: hits.length };
        });
        const arrUnresolved = arrItems.filter(i => !i.point).map(i => i.szRole);
        return { arrItems: arrItems, nHit: arrItems.length - arrUnresolved.length, nTotal: arrItems.length, arrUnresolved: arrUnresolved };
    }

    // 命中點位帶入 min / max / 單位（規則與 picker.js 單點重選、equip-autobind _applyRange 一致）
    function _applyPointExtras(szType, props, szSlotKey, point) {
        if (szSlotKey === 'szSidFreq')        { props.nFreqMax = point.fMax ?? 60; return; }
        if (szSlotKey === 'szCidFreqSet')     { props.nFreqSetMin = point.fMin ?? 0; props.nFreqSetMax = point.fMax ?? 60; return; }
        if (szSlotKey === 'szSidLoad')        { props.nLoadMax = point.fMax ?? 100; return; }
        if (szType === 'realtimeValue' && szSlotKey === 'szSid') { props.szUnit = point.szUnit || ''; return; }
        if (szType === 'aoPoint' && szSlotKey === 'szCid') {
            props.szUnit = point.szUnit || '';
            props.fMin = point.fMin ?? 0;
            props.fMax = point.fMax ?? 100;
        }
    }

    // ── 主函式：依 objRoleBind 回填 SID / 名稱 ──
    // 回 { arrWidgetState（深拷貝，不動輸入）, arrWidgetHit（每個 widget 是否至少命中一個槽位；
    //      無 objRoleBind 的 widget 為 null）, nHit, nTotal, arrItems, arrUnresolved }
    // 未命中槽位寫空字串（不留舊值）
    function resolveRoleBindings(arrWidgetState, arrDevicePoints) {
        const arrRoles = collectRoles(arrWidgetState);
        const match = matchRoles(arrRoles, arrDevicePoints);
        const byRole = {};
        match.arrItems.forEach(i => { byRole[i.szRole] = i.point; });

        const arrHit = [];
        const arrOut = (arrWidgetState || []).map(ws => {
            const props = JSON.parse(JSON.stringify(ws.props || {}));
            const out = { ...ws, props: props };
            const bind = props.objRoleBind;
            if (!bind) { arrHit.push(null); return out; }
            let bAny = false;
            Object.keys(bind).forEach(szSlot => {
                const point = byRole[bind[szSlot]];
                const szNameKey = nameKeyOf(szSlot);
                if (point) {
                    props[szSlot] = point.szSid;
                    props[szNameKey] = pointDisplayName(point);
                    _applyPointExtras(ws.szType, props, szSlot, point);
                    bAny = true;
                } else {
                    props[szSlot] = '';
                    props[szNameKey] = '';
                }
            });
            arrHit.push(bAny);
            return out;
        });
        return {
            arrWidgetState: arrOut, arrWidgetHit: arrHit,
            nHit: match.nHit, nTotal: match.nTotal, arrItems: match.arrItems, arrUnresolved: match.arrUnresolved
        };
    }

    // ── 固定座位：有就坐、沒有就空著；整欄都沒命中才收掉該欄（plan 決策 4）──
    // arrWidgetState 每筆需有 nCol / nRow（同組座位欄列）；arrWidgetHit 對應 resolveRoleBindings 結果。
    // 未命中的角色 widget 移除，同座位的非角色 widget（角色短名 text 標籤）一起移除；
    // 整欄移除時，右側各欄往左移 nColPitch。回新陣列（不動輸入）。
    function dropEmptySeats(arrWidgetState, arrWidgetHit, nColPitch) {
        const colState = {};          // nCol → 'hit' | 'empty'
        const setEmptySeats = new Set();
        (arrWidgetState || []).forEach((ws, i) => {
            if (arrWidgetHit[i] === null || ws.nCol == null) return;
            if (arrWidgetHit[i]) colState[ws.nCol] = 'hit';
            else {
                if (!colState[ws.nCol]) colState[ws.nCol] = 'empty';
                setEmptySeats.add(ws.nCol + ',' + ws.nRow);
            }
        });
        const arrEmptyCols = Object.keys(colState).filter(c => colState[c] === 'empty').map(Number);
        const out = [];
        (arrWidgetState || []).forEach((ws, i) => {
            if (arrWidgetHit[i] === false) return;   // 沒命中 → 不建 widget
            if (arrWidgetHit[i] === null && ws.nCol != null && setEmptySeats.has(ws.nCol + ',' + ws.nRow)) return;   // 該座位的標籤
            if (ws.nCol == null || !nColPitch) { out.push(ws); return; }
            const nShift = arrEmptyCols.filter(c => c < ws.nCol).length;
            out.push(nShift ? { ...ws, nX: ws.nX - nShift * nColPitch } : ws);
        });
        return out;
    }

    // ── Device 建議：設備 ID ↔ Device 標籤 ──
    // 正規化：全形轉半形、小寫、去掉 - _ 空白 . 括號
    function normalizeEquipId(sz) {
        return String(sz == null ? '' : sz)
            .replace(/[！-～]/g, ch => String.fromCharCode(ch.charCodeAt(0) - 0xfee0))
            .replace(/　/g, ' ')
            .toLowerCase()
            .replace(/[-_\s.()（）]/g, '');
    }

    // 回最佳 Device 標籤或 ''：先整名相等；再「標籤含設備 ID」且 ID 後面不是數字
    // （CHP1 不會配到 CHP10）；多個候選取最短標籤
    function suggestDevice(szEquipId, arrDeviceLabels) {
        const szId = normalizeEquipId(szEquipId);
        if (!szId) return '';
        const arr = (arrDeviceLabels || []).filter(Boolean);
        const exact = arr.find(l => normalizeEquipId(l) === szId);
        if (exact) return exact;
        const cands = arr.filter(l => {
            const n = normalizeEquipId(l);
            let idx = n.indexOf(szId);
            while (idx >= 0) {
                if (!/[0-9]/.test(n.charAt(idx + szId.length))) return true;
                idx = n.indexOf(szId, idx + 1);
            }
            return false;
        });
        if (cands.length === 0) return '';
        cands.sort((a, b) => normalizeEquipId(a).length - normalizeEquipId(b).length);
        return cands[0];
    }

    // `__i18n__:key` 字串解析成當前語系（頁面檔的標題用；只處理第一層字串 props）
    function resolveI18nProps(props, fnT) {
        const out = {};
        Object.keys(props || {}).forEach(k => {
            const v = props[k];
            out[k] = (typeof v === 'string' && v.indexOf('__i18n__:') === 0 && fnT)
                ? fnT(v.slice('__i18n__:'.length))
                : v;
        });
        return out;
    }

    const api = {
        nameKeyOf: nameKeyOf,
        pointDisplayName: pointDisplayName,
        collectRoles: collectRoles,
        matchRoles: matchRoles,
        resolveRoleBindings: resolveRoleBindings,
        dropEmptySeats: dropEmptySeats,
        normalizeEquipId: normalizeEquipId,
        suggestDevice: suggestDevice,
        resolveI18nProps: resolveI18nProps
    };
    root._roleResolve = api;
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
