// ============================================================
// Designer — 表格列範本（自動帶入建議面板）
// ============================================================
// 內容：matchRole 純函式（v1 = 最後一段直接比對 aliases，未來可平滑
// 升級為模糊比對）、tryAutoFill 主流程、建議面板的 render / 三鍵動作
// （直接帶入 / 調整順序 / 不帶入）。
// 依賴：role-aliases.js（window._roleAliases 別名表，須先載入）、
// picker.js（arrAllPoints）、widget-defs.js（initArrCells /
// _defaultDataCell）、widget-core.js（renderWidget）、prop-panel.js
// （renderTableCellPropPanel）、ctx-menu.js（showDesignToast）。
// ============================================================

(function () {

    // ── i18n shortcut ──
    function t(key, args) {
        return window.i18n && window.i18n.t ? window.i18n.t(key, args) : key;
    }

    // ── 角色目錄與別名（自 role-aliases.js 共用模組載入）──
    // 同時兼任：(1) matchRole 的比對來源、(2)「未使用角色」候選清單
    const ROLE_ALIASES = window._roleAliases || {};
    const ALL_ROLES = Object.keys(ROLE_ALIASES);
    if (ALL_ROLES.length === 0) {
        console.warn('[row-template] window._roleAliases 未載入 — 請確認 role-aliases.js 先於本檔載入');
    }

    // ── 角色別名查詢（lower-case 字串 → roleLabel 或 null）──
    function _findRoleByAlias(szCandidate) {
        if (!szCandidate) return null;
        const szLowered = szCandidate.toLowerCase();
        const hits = [];
        for (const role of ALL_ROLES) {
            if (ROLE_ALIASES[role].includes(szLowered)) {
                hits.push(role);
            }
        }
        if (hits.length === 0) return null;
        if (hits.length > 1) {
            console.warn('[row-template] multiple role hits for "' + szCandidate + '":', hits, '— picked', hits[0]);
        }
        return hits[0];
    }

    // ── 比對純函式（pluggable，未來換模糊比對只動這支）──
    // 接受 point object（含 szName + szDeviceLabel），支援三種命名風格：
    //  風格 B'（開頭 token 式，使用者實際命名）：點位名「角色在前、相別在後」，
    //                       開頭 token 命中角色 + 有設備標籤 e.g.
    //                       szDeviceLabel = "北101", szName = "V_a" / "I_avg"
    //                       → prefix = "北101", role = "V" / "A"（不切 szSeparator，
    //                       亦不被尾段 a / avg 誤判；先於風格 A/B 判定）
    //  風格 A（plan 原樣本）：點位名本身含分隔符  e.g. szName = "PM-1-V"
    //                       → prefix = "PM-1", role = "V"（開頭 pm 非角色 → 走此）
    //  風格 B（點位名整個就是角色）：szDeviceLabel = "北101", szName = "V"
    //                       → prefix = "北101", role = "V"
    // 回傳 { roleLabel, prefix, lastSegment, bDeviceAsPrefix } 或 null
    function matchRole(point, szSep) {
        if (!point) return null;
        const szName = point.szName || '';
        if (!szName) return null;

        // 風格 B'：開頭 token 命中角色 + 有設備標籤 → 設備標籤當前綴（先於 A/B）
        const leadingRole = window._roleMatch && window._roleMatch.leadingRole;
        if (leadingRole && point.szDeviceLabel) {
            const role = leadingRole(szName);
            if (role) {
                return { roleLabel: role, prefix: point.szDeviceLabel, lastSegment: szName, bDeviceAsPrefix: true };
            }
        }

        // 風格 A：點位名切分隔符
        if (szSep) {
            const nIdx = szName.lastIndexOf(szSep);
            if (nIdx > 0) {
                const szPrefix  = szName.substring(0, nIdx);
                const szLastSeg = szName.substring(nIdx + szSep.length);
                if (szPrefix && szLastSeg) {
                    const role = _findRoleByAlias(szLastSeg);
                    if (role) {
                        return { roleLabel: role, prefix: szPrefix, lastSegment: szLastSeg, bDeviceAsPrefix: false };
                    }
                }
            }
        }

        // 風格 B：點位名整個就是角色，前綴用設備標籤
        if (point.szDeviceLabel) {
            const role = _findRoleByAlias(szName);
            if (role) {
                return { roleLabel: role, prefix: point.szDeviceLabel, lastSegment: szName, bDeviceAsPrefix: true };
            }
        }

        return null;
    }

    // ── 範本 cache（首次用時 fetch）──
    let _template = null; // { szSeparator, arrRoles }

    async function _ensureTemplate() {
        if (_template) return _template;
        try {
            const r = await fetch('/Designer/Templates');
            if (!r.ok) throw new Error('HTTP ' + r.status);
            const j = await r.json();
            _template = {
                szSeparator: j.szSeparator || '-',
                arrRoles:    Array.isArray(j.arrRoles) && j.arrRoles.length > 0
                                ? j.arrRoles.slice()
                                : ['V', 'A', 'KW', 'PF', 'KWH']
            };
        } catch (err) {
            console.warn('[row-template] failed to load templates, using built-in defaults:', err);
            _template = { szSeparator: '-', arrRoles: ['V', 'A', 'KW', 'PF', 'KWH'] };
        }
        return _template;
    }

    async function _saveTemplate(tpl) {
        try {
            const r = await fetch('/Designer/Templates', {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ szSeparator: tpl.szSeparator, arrRoles: tpl.arrRoles })
            });
            if (!r.ok) return false;
            const j = await r.json();
            return !!j.success;
        } catch (err) {
            console.warn('[row-template] save failed:', err);
            return false;
        }
    }

    // ── 依目標 role + prefix 在 arrAllPoints 找出第一個匹配點位 ──
    // bDeviceAsPrefix 用來鎖定命名風格，避免跨風格錯配
    function _findPointForRole(szPrefix, szRole, szSep, bDeviceAsPrefix) {
        // picker.js 用 `let arrAllPoints` 宣告 — script-scoped、不會掛到 window，
        // 故走名稱解析（同 realm 跨 <script> 的 let 共享）而非 window.arrAllPoints。
        if (typeof arrAllPoints === 'undefined' || !arrAllPoints) return null;
        for (const p of arrAllPoints) {
            const m = matchRole(p, szSep);
            if (!m) continue;
            if (m.roleLabel !== szRole) continue;
            if (m.bDeviceAsPrefix !== bDeviceAsPrefix) continue;
            if (m.prefix !== szPrefix) continue;
            return p;
        }
        return null;
    }

    // ── 計算「預覽」與「直接帶入」共用的角色 → 點位對映 ──
    // 回傳 [{ szRole, point | null }]，順序依 arrRolesOrder，已排除 pickedRole
    function _buildAssignments(arrRolesOrder, szPickedRole, szPrefix, szSep, bDeviceAsPrefix) {
        const out = [];
        for (const role of arrRolesOrder) {
            if (role === szPickedRole) continue;
            out.push({ szRole: role, point: _findPointForRole(szPrefix, role, szSep, bDeviceAsPrefix) });
        }
        return out;
    }

    // ── 表頭 → 角色比對（正規化沿用 picker.js 的 _normalizeHeaderText：
    //    trim / 全形→半形 / 去尾端括號單位 / 小寫；別名走 _roleAliases）──
    function _matchHeaderRole(szHeaderText) {
        if (!szHeaderText) return null;
        const szNorm = (typeof _normalizeHeaderText === 'function')
            ? _normalizeHeaderText(szHeaderText)
            : String(szHeaderText).trim().toLowerCase();
        if (!szNorm) return null;
        return _findRoleByAlias(szNorm);
    }

    // ── 計算每個 assignment 的目標欄（表頭優先，v6 依使用者回饋）──
    // pass 1：第一列表頭命中該角色且 cell 空白 → 對號入座（依表頭位置，不論左右順序）
    // pass 2：其餘沿用舊制 — 自「點選欄位」右鄰起循序找空欄；
    //         但不搶「表頭命中其他角色」的欄（避免張冠李戴），未找到點位的角色仍預留空欄
    // 回傳 arrTargets（與 assignments 同 index；值 = colIdx 或 null = 無欄可放）
    function _computePlacement(props, nRowIdx, nPickedCol, assignments) {
        const headers = props.arrCells[0] || [];
        const row     = props.arrCells[nRowIdx] || [];
        const targets  = new Array(assignments.length).fill(null);
        const consumed = new Set();

        // 各欄表頭命中的角色（跳過使用者剛綁的欄）
        const headerRoleByCol = {};
        for (let ci = 1; ci < row.length; ci++) {
            if (ci === nPickedCol) continue;
            const role = _matchHeaderRole(headers[ci] ? headers[ci].szText : '');
            if (role) headerRoleByCol[ci] = role;
        }
        const bAnyHeader = Object.keys(headerRoleByCol).length > 0;

        function isCellFree(ci) {
            const cell = row[ci];
            return !!cell && !cell.szSid && cell.nCircuitId == null;
        }

        // pass 1：表頭對號入座（含未找到點位的角色 — 該欄保留給它，維持表頭語意）
        if (bAnyHeader) {
            assignments.forEach((a, i) => {
                for (const szCi of Object.keys(headerRoleByCol)) {
                    const ci = +szCi;
                    if (consumed.has(ci) || headerRoleByCol[ci] !== a.szRole || !isCellFree(ci)) continue;
                    targets[i] = ci;
                    consumed.add(ci);
                    break;
                }
            });
        }

        // pass 2：循序遞補（舊行為）
        let ci = nPickedCol + 1;
        assignments.forEach((a, i) => {
            if (targets[i] != null) return;
            while (ci < row.length && (consumed.has(ci) || !isCellFree(ci)
                   || (bAnyHeader && headerRoleByCol[ci] && headerRoleByCol[ci] !== a.szRole))) ci++;
            if (ci >= row.length) return;               // 欄位用盡
            targets[i] = ci;
            consumed.add(ci);
            ci++;
        });
        return targets;
    }

    // ── 把 assignments 依目標欄寫入 arrCells ── 回傳 { nFilled, nMissingPoint }
    function _applyAssignments(widgetEl, nRowIdx, nPickedCol, assignments) {
        const props = widgetEl.widgetProps;
        if (typeof initArrCells === 'function') initArrCells(props);
        const row = props.arrCells[nRowIdx];
        if (!row) return { nFilled: 0, nMissingPoint: 0 };

        const targets = _computePlacement(props, nRowIdx, nPickedCol, assignments);
        let nFilled = 0;
        let nMissingPoint = 0;
        assignments.forEach((a, i) => {
            const ci = targets[i];
            if (ci == null) return;                     // 無欄可放
            if (!a.point) { nMissingPoint++; return; }  // 預留空欄，不寫入
            const cell = row[ci];
            const p = a.point;
            cell.szSid       = p.szSid;
            cell.szPointName = p.szDeviceLabel ? (p.szDeviceLabel + ' / ' + p.szName) : p.szName;
            if (typeof _applyAutoColDecimals === 'function') {
                _applyAutoColDecimals(props, ci, cell, cell.szPointName, p.szUnit);
            }
            // 表格 DI Cell 綁定 SID 時，繼承同 SID 已存在的 ON/OFF 標籤
            if (cell.szPointType === 'DI' && typeof _findDiLabelsForSid === 'function') {
                const diLabels = _findDiLabelsForSid(p.szSid);
                if (diLabels) {
                    cell.szOnLabel  = diLabels.szOnLabel;
                    cell.szOffLabel = diLabels.szOffLabel;
                }
            }
            nFilled++;
        });
        return { nFilled, nMissingPoint };
    }

    // ── escape util（沿用 picker.js / state.js 的 escHtml）──
    function _esc(s) {
        return typeof escHtml === 'function' ? escHtml(s == null ? '' : String(s)) : String(s == null ? '' : s);
    }

    // ============================================================
    // 建議面板狀態 + 渲染
    // ============================================================
    let _modalInstance = null;

    // 建議面板「當前」工作中的範本（adjustOrder 編輯區的本地副本）
    // 套用一次 → 用這份 apply，不寫回 _template
    // 套用並存為預設 → 用這份 apply + _saveTemplate
    let _workingRoles = null;

    // 偵測 context
    let _ctx = null; // { widgetEl, nRowIdx, nPickedCol, szPickedRole, szPrefix, szSep, bDeviceAsPrefix }

    function _getModal() {
        if (_modalInstance) return _modalInstance;
        const el = document.getElementById('rowTemplateModal');
        if (!el) return null;
        _modalInstance = new bootstrap.Modal(el);
        return _modalInstance;
    }

    function _renderPreview(arrRolesOrder) {
        const assignments = _buildAssignments(arrRolesOrder, _ctx.szPickedRole, _ctx.szPrefix, _ctx.szSep, _ctx.bDeviceAsPrefix);
        if (assignments.length === 0) {
            return '<div class="rt-empty">' + _esc(t('designer.row_template.preview_missing')) + '</div>';
        }
        // 目標欄（表頭優先落格）— 讓使用者確認每個角色會帶進哪一欄
        const props = _ctx.widgetEl ? _ctx.widgetEl.widgetProps : null;
        const targets = props ? _computePlacement(props, _ctx.nRowIdx, _ctx.nPickedCol, assignments) : [];
        const headers = props ? (props.arrCells[0] || []) : [];
        return assignments.map((a, i) => {
            const bFound = !!a.point;
            const szTag  = bFound
                ? '<span class="rt-tag rt-found">' + _esc(t('designer.row_template.preview_found')) + '</span>'
                : '<span class="rt-tag rt-missing">' + _esc(t('designer.row_template.preview_missing')) + '</span>';
            const szPointName = bFound
                ? (a.point.szDeviceLabel
                    ? '<span class="rt-prefix">' + _esc(a.point.szDeviceLabel) + '</span> / ' + _esc(a.point.szName)
                    : _esc(a.point.szName))
                : '<span class="rt-na">—</span>';
            // 目標欄標示：有表頭字用表頭字，否則用欄序（#n）；無欄可放顯示「欄位不足」
            let szColNote;
            const nTargetCol = targets[i];
            if (nTargetCol != null) {
                const szHeaderText = (headers[nTargetCol] && headers[nTargetCol].szText || '').trim();
                szColNote = '<span class="rt-prefix" style="flex-shrink:0;">' +
                    _esc(t('designer.row_template.preview_target_col', { col: szHeaderText || ('#' + nTargetCol) })) + '</span>';
            } else {
                szColNote = '<span class="rt-na" style="flex-shrink:0;font-size:11px;">' +
                    _esc(t('designer.row_template.preview_no_col')) + '</span>';
            }
            return '<div class="rt-preview-row' + (bFound ? '' : ' rt-row-missing') + '">' +
                '<span class="rt-role">' + _esc(a.szRole) + '</span>' +
                '<span class="rt-arrow">&#x2192;</span>' +
                '<span class="rt-point">' + szPointName + '</span>' +
                szColNote +
                szTag +
                '</div>';
        }).join('');
    }

    function _renderSuggestView() {
        const body = document.getElementById('rowTemplateBody');
        if (!body) return;
        body.innerHTML =
            '<div class="rt-detected">' +
                _esc(t('designer.row_template.detected_role', {
                    role:   _ctx.szPickedRole,
                    prefix: _ctx.szPrefix
                })) +
            '</div>' +
            '<div class="rt-preview-title">' + _esc(t('designer.row_template.preview_title')) + '</div>' +
            '<div class="rt-preview-list">' + _renderPreview(_template.arrRoles) + '</div>';

        const footer = document.getElementById('rowTemplateFooter');
        footer.innerHTML =
            '<button type="button" class="btn btn-sm btn-outline-secondary" id="btnRtSkip">' +
                '<i class="fas fa-times me-1"></i>' + _esc(t('designer.row_template.btn_skip')) +
            '</button>' +
            '<button type="button" class="btn btn-sm btn-outline-info" id="btnRtAdjust">' +
                '<i class="fas fa-sort me-1"></i>' + _esc(t('designer.row_template.btn_adjust')) +
            '</button>' +
            '<button type="button" class="btn btn-sm btn-success" id="btnRtFill">' +
                '<i class="fas fa-magic me-1"></i>' + _esc(t('designer.row_template.btn_fill')) +
            '</button>';

        document.getElementById('btnRtSkip').onclick   = _onSkip;
        document.getElementById('btnRtAdjust').onclick = _onShowAdjust;
        document.getElementById('btnRtFill').onclick   = () => _doFill(_template.arrRoles);
    }

    function _renderAdjustView() {
        const body = document.getElementById('rowTemplateBody');
        if (!body) return;

        // 角色項目（含 ↑ ↓ ✕）
        const szRoleRows = _workingRoles.map((role, idx) => {
            const bFirst = idx === 0;
            const bLast  = idx === _workingRoles.length - 1;
            return '<div class="rt-role-item" data-idx="' + idx + '">' +
                '<span class="rt-role-name">' + _esc(role) + '</span>' +
                '<button type="button" class="rt-icon-btn" data-action="up" ' +
                    (bFirst ? 'disabled' : '') + ' title="' + _esc(t('designer.row_template.move_up')) + '">' +
                    '<i class="fas fa-arrow-up"></i></button>' +
                '<button type="button" class="rt-icon-btn" data-action="down" ' +
                    (bLast ? 'disabled' : '') + ' title="' + _esc(t('designer.row_template.move_down')) + '">' +
                    '<i class="fas fa-arrow-down"></i></button>' +
                '<button type="button" class="rt-icon-btn rt-icon-danger" data-action="remove" ' +
                    'title="' + _esc(t('designer.row_template.remove_role')) + '">' +
                    '<i class="fas fa-times"></i></button>' +
                '</div>';
        }).join('');

        // 未使用角色（從 ALL_ROLES 扣掉 _workingRoles）
        const arrUnused = ALL_ROLES.filter(r => _workingRoles.indexOf(r) < 0);
        const szUnused = arrUnused.length === 0
            ? '<div class="rt-empty">—</div>'
            : arrUnused.map(role =>
                '<button type="button" class="rt-add-chip" data-role="' + _esc(role) + '">' +
                    '<i class="fas fa-plus me-1"></i>' + _esc(role) +
                '</button>'
            ).join('');

        body.innerHTML =
            '<div class="rt-section-title">' + _esc(t('designer.row_template.adjust_title')) + '</div>' +
            '<div class="rt-adjust-hint">' + _esc(t('designer.row_template.adjust_hint')) + '</div>' +
            '<div class="rt-role-list">' + szRoleRows + '</div>' +
            '<div class="rt-section-title rt-mt">' + _esc(t('designer.row_template.unused_roles')) + '</div>' +
            '<div class="rt-unused-wrap">' + szUnused + '</div>' +
            '<div class="rt-section-title rt-mt">' + _esc(t('designer.row_template.preview_title')) + '</div>' +
            '<div class="rt-preview-list">' + _renderPreview(_workingRoles) + '</div>';

        const footer = document.getElementById('rowTemplateFooter');
        footer.innerHTML =
            '<button type="button" class="btn btn-sm btn-outline-secondary" id="btnRtBack">' +
                '<i class="fas fa-arrow-left me-1"></i>' + _esc(t('designer.row_template.btn_back')) +
            '</button>' +
            '<button type="button" class="btn btn-sm btn-outline-success" id="btnRtApplyOnce">' +
                '<i class="fas fa-check me-1"></i>' + _esc(t('designer.row_template.btn_apply_once')) +
            '</button>' +
            '<button type="button" class="btn btn-sm btn-success" id="btnRtApplySave">' +
                '<i class="fas fa-save me-1"></i>' + _esc(t('designer.row_template.btn_apply_save')) +
            '</button>';

        document.getElementById('btnRtBack').onclick      = _renderSuggestView;
        document.getElementById('btnRtApplyOnce').onclick = () => _doFill(_workingRoles, false);
        document.getElementById('btnRtApplySave').onclick = () => _doFill(_workingRoles, true);

        // 綁定 reorder / remove
        body.querySelectorAll('.rt-role-item button').forEach(btn => {
            btn.onclick = (ev) => {
                ev.preventDefault();
                const item = btn.closest('.rt-role-item');
                const idx  = parseInt(item.dataset.idx, 10);
                const action = btn.dataset.action;
                if (action === 'up' && idx > 0) {
                    const tmp = _workingRoles[idx - 1];
                    _workingRoles[idx - 1] = _workingRoles[idx];
                    _workingRoles[idx]     = tmp;
                } else if (action === 'down' && idx < _workingRoles.length - 1) {
                    const tmp = _workingRoles[idx + 1];
                    _workingRoles[idx + 1] = _workingRoles[idx];
                    _workingRoles[idx]     = tmp;
                } else if (action === 'remove') {
                    _workingRoles.splice(idx, 1);
                    if (_workingRoles.length === 0) {
                        // 至少保留一個 — 撤銷
                        _workingRoles.push(ALL_ROLES[0]);
                    }
                }
                _renderAdjustView();
            };
        });

        // 綁定「加入」
        body.querySelectorAll('.rt-add-chip').forEach(btn => {
            btn.onclick = (ev) => {
                ev.preventDefault();
                const role = btn.dataset.role;
                if (role && _workingRoles.indexOf(role) < 0) {
                    _workingRoles.push(role);
                    _renderAdjustView();
                }
            };
        });
    }

    function _onShowAdjust() {
        _workingRoles = _template.arrRoles.slice();
        _renderAdjustView();
    }

    function _onSkip() {
        const m = _getModal();
        if (m) m.hide();
    }

    async function _doFill(arrRolesOrder, bSaveAsDefault) {
        const m = _getModal();
        const { nFilled, nMissingPoint } = _applyAssignments(
            _ctx.widgetEl, _ctx.nRowIdx, _ctx.nPickedCol,
            _buildAssignments(arrRolesOrder, _ctx.szPickedRole, _ctx.szPrefix, _ctx.szSep, _ctx.bDeviceAsPrefix)
        );
        if (typeof renderWidget === 'function') renderWidget(_ctx.widgetEl);
        // 重新高亮 picked cell + 重渲 prop panel
        const sel = _ctx.widgetEl.querySelector(
            '.w-table [data-row="' + _ctx.nRowIdx + '"][data-col="' + _ctx.nPickedCol + '"]');
        if (sel) sel.classList.add('selected-cell');
        if (typeof renderTableCellPropPanel === 'function') {
            renderTableCellPropPanel(_ctx.widgetEl, _ctx.nRowIdx, _ctx.nPickedCol);
        }

        if (bSaveAsDefault) {
            const ok = await _saveTemplate({ szSeparator: _ctx.szSep, arrRoles: arrRolesOrder.slice() });
            if (ok) {
                _template.arrRoles = arrRolesOrder.slice();
            } else if (typeof showDesignToast === 'function') {
                showDesignToast('danger',
                    '<i class="fas fa-exclamation-circle me-1"></i>' +
                    _esc(t('designer.row_template.save_failed')));
            }
        }

        // toast 結果
        if (typeof showDesignToast === 'function') {
            const szMsg = nMissingPoint > 0
                ? t('designer.row_template.toast_filled_with_missing', { count: nFilled, missing: nMissingPoint })
                : t('designer.row_template.toast_filled', { count: nFilled });
            showDesignToast('info', '<i class="fas fa-magic me-1"></i>' + _esc(szMsg));
        }

        if (m) m.hide();
    }

    // ============================================================
    // 對外入口：picker 確認後呼叫
    // ============================================================
    async function tryAutoFill(widgetEl, nRowIdx, nPickedCol, point) {
        try {
            if (!widgetEl || !point || nRowIdx == null || nPickedCol == null) return;
            if (nRowIdx < 1) return; // 標題列不觸發

            const tpl = await _ensureTemplate();
            const m   = matchRole(point, tpl.szSeparator);
            if (!m) return; // 沉默通過

            _ctx = {
                widgetEl:        widgetEl,
                nRowIdx:         nRowIdx,
                nPickedCol:      nPickedCol,
                szPickedRole:    m.roleLabel,
                szPrefix:        m.prefix,
                szSep:           tpl.szSeparator,
                bDeviceAsPrefix: m.bDeviceAsPrefix
            };
            _renderSuggestView();
            const modal = _getModal();
            if (modal) modal.show();
        } catch (err) {
            console.warn('[row-template] tryAutoFill error:', err);
        }
    }

    // 暴露至 window 供 picker.js / 其他模組呼叫
    window._rowTemplate = {
        tryAutoFill: tryAutoFill,
        matchRole:   matchRole,           // 純函式，便於日後升級為模糊比對
        computePlacement: _computePlacement,   // 純函式（表頭優先落格），供測試
        ROLE_ALIASES: ROLE_ALIASES
    };

})();
