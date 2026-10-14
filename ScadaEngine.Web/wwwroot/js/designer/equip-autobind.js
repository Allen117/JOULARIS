// ============================================================
// Designer — 設備級自動綁定（plan 2026-10-14-designer-equipment-autobind）
// 掛 window._equipAutobind
// ============================================================
// 泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機 拖到畫布 → picker 停在「設備」葉節點
// → 在該設備的點位集合內依名稱比對各槽位 → 預覽 → 確認整台帶入。
//
// 內容：
//   - EQUIP_SLOT_ROLES[szWidgetType]：各型別的槽位 → 角色對照（含 i18n label key）
//   - buildEquipSuggestions(arrCandidates, szWidgetType)：純函式（node 可測，不碰 DOM）
//   - renderEquipPreview(container, result)：picker 內預覽區 render
//   - applyToWidget(el, result, szDeviceLabel, bClearMissing)：寫 props + min/max + szTitle + 重繪 + toast
//
// 依賴（皆於呼叫時解析，載入順序只需晚於 role-aliases.js）：
//   role-aliases.js（window._equipRoleMatch.resolveEquipRole）
//   designer/state.js（t / escHtml）、widget-core.js（renderWidget）、
//   prop-panel.js（renderPropPanel）、ctx-menu.js（showDesignToast）
// 槽位 key 全部沿用既有 szSid* / szCid* / sz*Name，不新增 props key，
// 儲存格式與複製頁面清空規則（isDesignerBindingKey）零改動。
// ============================================================

(function () {
    'use strict';

    const EQUIP_TYPES = ['pump', 'coolingTower', 'ahuFan', 'chiller'];

    // 槽位定義（szRole 對應 window._equipRoleAliases 的 key）
    const SLOT = {
        RUN:          { szSlotKey: 'szSidRun',       szNameKey: 'szRunName',       szRole: 'RUN',       szLabelKey: 'designer.prop.pump.run_status' },
        FAULT:        { szSlotKey: 'szSidFault',     szNameKey: 'szFaultName',     szRole: 'FAULT',     szLabelKey: 'designer.prop.pump.fault_status' },
        MODE:         { szSlotKey: 'szSidMode',      szNameKey: 'szModeName',      szRole: 'MODE',      szLabelKey: 'designer.prop.pump.mode_status' },
        MODE_CHILLER: { szSlotKey: 'szSidMode',      szNameKey: 'szModeName',      szRole: 'MODE',      szLabelKey: 'designer.prop.motor.remote_local' },
        FREQ:         { szSlotKey: 'szSidFreq',      szNameKey: 'szFreqName',      szRole: 'FREQ',      szLabelKey: 'designer.prop.pump.frequency' },
        STARTSTOP:    { szSlotKey: 'szCidStartStop', szNameKey: 'szStartStopName', szRole: 'STARTSTOP', szLabelKey: 'designer.prop.pump.start_stop' },
        FREQSET:      { szSlotKey: 'szCidFreqSet',   szNameKey: 'szFreqSetName',   szRole: 'FREQSET',   szLabelKey: 'designer.prop.pump.freq_set' },
        LOAD:         { szSlotKey: 'szSidLoad',      szNameKey: 'szLoadName',      szRole: 'LOAD',      szLabelKey: 'designer.prop.motor.load' },
        CHWOUT:       { szSlotKey: 'szSidChwOut',    szNameKey: 'szChwOutName',    szRole: 'CHWOUT',    szLabelKey: 'designer.prop.motor.chw_out' },
        SETTEMP:      { szSlotKey: 'szCidSetTemp',   szNameKey: 'szSetTempName',   szRole: 'SETTEMP',   szLabelKey: 'designer.prop.motor.set_temp' },
        WATERTEMP:    { szSlotKey: 'szSidWaterTemp', szNameKey: 'szWaterTempName', szRole: 'WATERTEMP', szLabelKey: 'designer.prop.motor.water_temp' }
    };

    // 各元件型別的槽位清單（順序 = 預覽與屬性面板順序）；只解析該型別需要的角色
    const EQUIP_SLOT_ROLES = {
        pump:         [SLOT.RUN, SLOT.FAULT, SLOT.MODE, SLOT.FREQ, SLOT.STARTSTOP, SLOT.FREQSET],
        coolingTower: [SLOT.RUN, SLOT.FAULT, SLOT.MODE, SLOT.FREQ, SLOT.WATERTEMP, SLOT.STARTSTOP, SLOT.FREQSET],
        ahuFan:       [SLOT.RUN, SLOT.FAULT, SLOT.MODE, SLOT.FREQ, SLOT.STARTSTOP, SLOT.FREQSET],
        chiller:      [SLOT.RUN, SLOT.FAULT, SLOT.MODE_CHILLER, SLOT.LOAD, SLOT.CHWOUT, SLOT.STARTSTOP, SLOT.SETTEMP]
    };

    function isEquipType(szType) {
        return EQUIP_TYPES.indexOf(szType) >= 0;
    }

    // 點位顯示名（與單槽位重選寫入的 szFullName 一致）
    function _pointDisplayName(p) {
        if (!p) return '';
        return p.szDeviceLabel ? p.szDeviceLabel + ' / ' + p.szName : p.szName;
    }

    // ── 純函式：候選點位集合 × 型別 → 各槽位命中結果 ──
    // arrCandidates = picker 目前葉節點的點位集合（呼叫端已篩好）
    // 回 { arrItems: [{ szSlotKey, szNameKey, szRole, szLabelKey, point, bMulti, nHits }], nHit, nTotal }
    function buildEquipSuggestions(arrCandidates, szWidgetType) {
        const slots = EQUIP_SLOT_ROLES[szWidgetType];
        if (!slots) return { arrItems: [], nHit: 0, nTotal: 0 };
        const resolver = window._equipRoleMatch && window._equipRoleMatch.resolveEquipRole;
        const arrRoles = slots.map(s => s.szRole);
        // 每個點位只解析一次、只落一個角色（同一點位不會同時進兩個槽位）
        const byRole = {};
        if (resolver) {
            (arrCandidates || []).forEach(p => {
                const r = resolver(p.szName || '', arrRoles);
                if (r) (byRole[r] = byRole[r] || []).push(p);
            });
        }
        let nHit = 0;
        const arrItems = slots.map(s => {
            const hits = byRole[s.szRole] || [];
            if (hits.length > 0) nHit++;
            return {
                szSlotKey: s.szSlotKey, szNameKey: s.szNameKey, szRole: s.szRole, szLabelKey: s.szLabelKey,
                point: hits[0] || null, bMulti: hits.length > 1, nHits: hits.length
            };
        });
        return { arrItems: arrItems, nHit: nHit, nTotal: slots.length };
    }

    // ── 槽位 min/max 帶入規則（與 picker.js 單槽位重選完全一致）──
    function _applyRange(props, szSlotKey, point) {
        if (szSlotKey === 'szSidFreq') {
            props.nFreqMax = point.fMax ?? 60;
        } else if (szSlotKey === 'szCidFreqSet') {
            props.nFreqSetMin = point.fMin ?? 0;
            props.nFreqSetMax = point.fMax ?? 60;
        } else if (szSlotKey === 'szSidLoad') {
            props.nLoadMax = point.fMax ?? 100;
        }
    }

    // ── 純套用（不重繪；node 可測）── 回傳帶入數
    // bClearMissing=true：未命中槽位清空（「重新指定設備」整台覆蓋語意）
    function applyToProps(props, result, szDeviceLabel, bClearMissing) {
        let n = 0;
        (result.arrItems || []).forEach(item => {
            if (item.point) {
                props[item.szSlotKey] = item.point.szSid;
                props[item.szNameKey] = _pointDisplayName(item.point);
                _applyRange(props, item.szSlotKey, item.point);
                n++;
            } else if (bClearMissing) {
                props[item.szSlotKey] = '';
                props[item.szNameKey] = '';
            }
        });
        if (szDeviceLabel) props.szTitle = szDeviceLabel;
        return n;
    }

    // ── 套用到畫布元件：寫 props → 重繪元件與屬性面板 → toast 摘要 ──
    function applyToWidget(el, result, szDeviceLabel, bClearMissing) {
        if (!el || !el.widgetProps || !result) return 0;
        const n = applyToProps(el.widgetProps, result, szDeviceLabel, bClearMissing);
        if (typeof renderWidget === 'function') renderWidget(el);
        if (typeof renderPropPanel === 'function') renderPropPanel(el);
        if (typeof showDesignToast === 'function') {
            showDesignToast(n > 0 ? 'info' : 'warning',
                '<i class="fas fa-magic me-1"></i>' +
                escHtml(t('designer.equip.toast_applied', { n: n, m: result.nTotal })));
        }
        return n;
    }

    // 元件目前是否已有任一槽位綁定（重新指定設備前 confirm 用）
    function hasAnyBinding(props, szWidgetType) {
        const slots = EQUIP_SLOT_ROLES[szWidgetType] || [];
        return slots.some(s => !!(props && props[s.szSlotKey]));
    }

    // ── picker 內預覽區 render（沿用列範本面板的 .row-template-body 樣式）──
    function renderEquipPreview(container, result) {
        if (!container) return;
        if (!result || result.nTotal === 0) {
            container.innerHTML = '';
            return;
        }
        const bNone = result.arrItems.every(i => !i.point);
        const szRows = result.arrItems.map(item => {
            let szPoint, szTag = '', szRowCls = '';
            const szTitleAttr = item.point ? escHtml(_pointDisplayName(item.point)) : '';
            if (item.point) {
                szPoint = szTitleAttr;
                if (item.bMulti) {
                    szTag = `<span class="rt-tag rt-multi">${escHtml(t('designer.equip.multi_hit'))}</span>`;
                }
            } else {
                szPoint = '<span class="rt-na">&#x2014;</span>';
                szTag = `<span class="rt-tag rt-missing">${escHtml(t('designer.equip.not_found'))}</span>`;
                szRowCls = ' rt-row-missing';
            }
            return `<div class="rt-preview-row${szRowCls}">
                <span class="rt-role" style="flex-basis:84px;">${escHtml(t(item.szLabelKey))}</span>
                <span class="rt-arrow">&#x2192;</span>
                <span class="rt-point" title="${szTitleAttr}">${szPoint}</span>
                ${szTag}
            </div>`;
        }).join('');

        const szHits = escHtml(t('designer.equip.preview_hits', { n: result.nHit, m: result.nTotal }));
        container.innerHTML =
            '<div class="row-template-body">' +
                `<div class="rt-detected${bNone ? ' rt-detected-none' : ''}"><i class="fas fa-magic me-1"></i>${szHits}</div>` +
                (bNone ? `<div class="rt-empty">${escHtml(t('designer.equip.no_candidates'))}</div>` : '') +
                `<div class="rt-preview-title">${escHtml(t('designer.equip.preview_title'))}</div>` +
                `<div class="rt-preview-list">${szRows}</div>` +
                `<div class="rt-adjust-hint" style="margin-top:8px;">${escHtml(t('designer.equip.preview_hint'))}</div>` +
            '</div>';
    }

    window._equipAutobind = {
        EQUIP_TYPES:          EQUIP_TYPES,
        EQUIP_SLOT_ROLES:     EQUIP_SLOT_ROLES,
        isEquipType:          isEquipType,
        buildEquipSuggestions: buildEquipSuggestions,
        applyToProps:         applyToProps,
        applyToWidget:        applyToWidget,
        hasAnyBinding:        hasAnyBinding,
        renderEquipPreview:   renderEquipPreview
    };
})();
