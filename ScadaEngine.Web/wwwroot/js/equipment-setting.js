/* 設備效率設定（/EquipmentEfficiencySetting）— 冰機 / 空壓機兩 tab 共用。
   - 進入某 tab 時若該類 0 台 → 自動掃描，有候選就彈「掃描到 N 台疑似 X，要帶入嗎？」
   - 掃描判定：js/equipment-scan.js（純函式）；點位清單：PointPicker.load()（/Designer/Points + 設備標籤）
   - 手動新增 / 編輯：各角色用共用 PointPicker（來源 → 設備 → 點位）綁定，單位下拉決定換算係數（伺服端重算）
   - 回填歷史：POST api/backfill → MQTT → Engine 逐小時重算 EquipmentHourly
   - 冰機評級門檻：預設「依標準」（ASHRAE 90.1 表 × 冷卻方式 / 形式 / 變頻 / 額定 RT），編輯框即時預覽；「手動」才用輸入值
   依賴載入順序：point-grouping.js → point-picker.js → role-aliases.js → equipment-scan.js → equipment-threshold.js → 本檔 */
(function () {
    'use strict';

    var API = '/EquipmentEfficiencySetting/api';
    var KINDS = ['chiller', 'compressor'];

    var _meta = null;
    var _kind = 'chiller';
    var _profiles = { chiller: [], compressor: [] };
    var _pointBySid = {};
    var _points = [];
    var _autoScanned = {};
    var _scanList = [];        // 目前掃描結果（含使用者在面板內的修改）
    var _editing = null;       // 編輯中的設備（含 aBindings 草稿）
    var _backfillTarget = null;

    // ── 工具 ──────────────────────────────────────────────
    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function $(id) { return document.getElementById(id); }
    function num(v) {
        if (v === '' || v == null) return null;
        var n = parseFloat(v);
        return isNaN(n) ? null : n;
    }
    function fmtNum(v) { return v == null ? '' : String(v); }
    function pad2(n) { return n < 10 ? '0' + n : String(n); }
    function dateStr(d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); }

    function toast(szMsg, szLevel) {
        var host = $('eqsToast');
        if (!host) return;
        var el = document.createElement('div');
        el.className = 'eqs-toast alert alert-' + (szLevel || 'success') + ' py-2 px-3 mb-2 shadow-sm';
        el.textContent = szMsg;
        host.appendChild(el);
        setTimeout(function () { el.remove(); }, 4000);
    }

    function fetchJson(url, opts) {
        return fetch(url, Object.assign({ credentials: 'same-origin' }, opts || {})).then(function (r) {
            return r.json().catch(function () { return {}; }).then(function (body) {
                if (!r.ok) { var e = new Error(body.error || ('HTTP ' + r.status)); e.body = body; throw e; }
                return body;
            });
        });
    }
    function postJson(url, data, method) {
        return fetchJson(url, {
            method: method || 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: data == null ? undefined : JSON.stringify(data)
        });
    }

    function errText(err) {
        var b = err && err.body;
        if (b && b.error) {
            var szDetail = b.detail || '';
            if (b.error === 'missing_required') szDetail = missingText(szDetail.split(','));
            else if (b.error === 'invalid_role' || b.error === 'duplicate_role') szDetail = roleLabel(szDetail);
            return t('eq.err.' + b.error, { detail: szDetail });
        }
        return (err && err.message) || String(err);
    }

    function kindLabel(kind) { return t('eq.kind.' + kind); }
    function roleLabel(role) { return t('eq.role.' + role); }
    function missingText(arr) {
        return (arr || []).filter(Boolean).map(function (m) {
            if (m === 'KW|KWH') return t('eq.missing.power');
            if (m === 'FLOW+CHWS+CHWR|RT') return t('eq.missing.chiller_output');
            if (m === 'AIRFLOW') return t('eq.missing.airflow');
            return m;
        }).join('、');
    }
    function rolesOf(kind) { return (_meta && _meta.order && _meta.order[kind]) || window._equipScan.ROLES[kind]; }
    function unitsOf(kind, role) {
        var r = _meta && _meta.roles && _meta.roles[kind] && _meta.roles[kind][role];
        return (r && r.units) || [{ unit: '', scale: 1 }];
    }
    // 必要角色標記（KW/KWH 擇一；冰機 FLOW+CHWS+CHWR 或 RT；空壓機 AIRFLOW）
    function isRequiredRole(kind, role) {
        if (role === 'KW' || role === 'KWH') return true;
        if (kind === 'compressor') return role === 'AIRFLOW';
        return role === 'FLOW' || role === 'CHWS' || role === 'CHWR' || role === 'RT';
    }
    function pointLabel(sid) {
        var p = _pointBySid[sid];
        if (!p) return sid;
        return (p.szDeviceLabel ? p.szDeviceLabel + ' / ' : '') + (p.szName || sid);
    }

    // ── 載入 ──────────────────────────────────────────────
    function loadProfiles() {
        return Promise.all(KINDS.map(function (k) {
            return fetchJson(API + '/profiles?kind=' + k).then(function (list) { _profiles[k] = list || []; });
        })).then(function () {
            KINDS.forEach(function (k) {
                var b = document.querySelector('[data-count="' + k + '"]');
                if (b) b.textContent = _profiles[k].length;
            });
        });
    }

    function loadPoints() {
        if (!window.PointPicker) return Promise.resolve();
        return window.PointPicker.load().then(function (d) {
            _points = d.points || [];
            _pointBySid = {};
            _points.forEach(function (p) { _pointBySid[p.szSid] = p; });
        }).catch(function () { _points = []; });
    }

    // ── Tabs / 清單 ───────────────────────────────────────
    function selectKind(kind) {
        _kind = kind;
        document.querySelectorAll('#eqsTabs .nav-link').forEach(function (a) {
            a.classList.toggle('active', a.dataset.kind === kind);
        });
        try { history.replaceState(null, '', '/EquipmentEfficiencySetting?kind=' + kind); } catch (e) { /* ignore */ }
        renderList();
        if (_profiles[kind].length === 0 && !_autoScanned[kind]) {
            _autoScanned[kind] = true;
            runScan(kind, true);
        }
    }

    function renderList() {
        var host = $('eqsListBody');
        var list = _profiles[_kind];
        if (list.length === 0) {
            host.innerHTML =
                '<div class="text-center text-muted py-5">' +
                  '<i class="fas fa-inbox fa-2x mb-2 d-block opacity-50"></i>' +
                  esc(t('eq.setting.empty', { kind: kindLabel(_kind) })) +
                  '<div class="mt-3 d-flex justify-content-center gap-2">' +
                    '<button class="btn btn-sm btn-outline-primary" onclick="window._eqs.scan()"><i class="fas fa-magic me-1"></i>' + esc(t('eq.setting.btn_scan')) + '</button>' +
                    '<button class="btn btn-sm btn-primary" onclick="window._eqs.add()"><i class="fas fa-plus me-1"></i>' + esc(t('eq.setting.btn_add')) + '</button>' +
                  '</div>' +
                '</div>';
            return;
        }
        var szRatedUnit = _kind === 'compressor' ? 'm³/min' : 'RT';
        var szMetricUnit = _kind === 'compressor' ? 'kW/(m³/min)' : 'kW/RT';
        var rows = list.map(function (p) {
            var chips = (p.aBindings || []).map(function (b) {
                return '<span class="badge eqs-role-chip" title="' + esc(pointLabel(b.szSID) + (b.szUnit ? ' [' + b.szUnit + ']' : '')) + '">' +
                    esc(roleLabel(b.szRole)) + '</span>';
            }).join(' ');
            return '<tr>' +
                '<td class="text-muted">' + esc(p.nSortOrder) + '</td>' +
                '<td class="fw-semibold">' + esc(p.szName) + '</td>' +
                '<td>' + (p.isEnabled
                    ? '<span class="badge bg-success">' + esc(t('eq.setting.enabled')) + '</span>'
                    : '<span class="badge bg-secondary">' + esc(t('eq.setting.disabled')) + '</span>') + '</td>' +
                '<td class="eqs-chips">' + chips + '</td>' +
                '<td class="text-nowrap small">' + (p.dRatedOutput != null ? esc(p.dRatedOutput) + ' ' + szRatedUnit : '--') +
                    ' / ' + (p.dRatedPower != null ? esc(p.dRatedPower) + ' kW' : '--') + '</td>' +
                thresholdCellHtml(p, szMetricUnit) +
                '<td class="text-end text-nowrap">' +
                    '<button class="btn btn-sm btn-outline-primary py-0 me-1" title="' + esc(t('eq.setting.edit')) + '" onclick="window._eqs.edit(' + p.nId + ')"><i class="fas fa-pen"></i></button>' +
                    '<button class="btn btn-sm btn-outline-secondary py-0 me-1" title="' + esc(t('eq.setting.backfill')) + '" onclick="window._eqs.backfill(' + p.nId + ')"><i class="fas fa-history"></i></button>' +
                    '<button class="btn btn-sm btn-outline-danger py-0" title="' + esc(t('eq.setting.delete')) + '" onclick="window._eqs.remove(' + p.nId + ')"><i class="fas fa-trash"></i></button>' +
                '</td>' +
            '</tr>';
        }).join('');
        host.innerHTML =
            '<div class="table-responsive"><table class="table table-sm table-hover align-middle mb-0 eqs-table">' +
              '<thead><tr>' +
                '<th style="width:50px;">' + esc(t('eq.setting.col_sort')) + '</th>' +
                '<th>' + esc(t('eq.setting.col_name')) + '</th>' +
                '<th style="width:70px;">' + esc(t('eq.setting.col_status')) + '</th>' +
                '<th>' + esc(t('eq.setting.col_roles')) + '</th>' +
                '<th>' + esc(t('eq.setting.col_rated')) + '</th>' +
                '<th>' + esc(t('eq.setting.col_threshold')) + '</th>' +
                '<th style="width:120px;"></th>' +
              '</tr></thead><tbody>' + rows + '</tbody></table></div>';
    }

    // 門檻欄：冰機顯示解析後門檻 + 依據（依標準 / 手動），空壓機維持手動值
    function thresholdCellHtml(p, szMetricUnit) {
        if (p.szKind !== 'chiller') {
            return '<td class="text-nowrap small">' + esc(fmtNum(p.dGoodThreshold)) + ' / ' + esc(fmtNum(p.dFairThreshold)) + ' <span class="text-muted">' + szMetricUnit + '</span></td>';
        }
        var th = resolveChiller(p);
        return '<td class="small"><span class="text-nowrap">' + esc(fmt3(th.good)) + ' / ' + esc(fmt3(th.fair)) + ' <span class="text-muted">' + szMetricUnit + '</span></span>' +
            '<div class="text-muted eqs-th-basis">' + esc(window.EquipmentThreshold.format(th.basis)) + '</div></td>';
    }
    function resolveChiller(p) {
        return window.EquipmentThreshold.resolve(_meta.chillerStandard, p, (_meta.defaults || {}).chiller);
    }
    function fmt3(v) { return v == null ? '--' : Number(v).toFixed(3); }

    // 冰機新增 / 掃描帶入預設：依標準、水冷離心定頻（使用者再依現場機型調整）
    var CHILLER_DEFAULTS = { szCoolingType: 'water', szChillerType: 'centrifugal', szControlType: 'fixed', szThresholdMode: 'standard' };
    function chillerDefaults(kind) { return kind === 'chiller' ? CHILLER_DEFAULTS : {}; }

    // ── 掃描 ──────────────────────────────────────────────
    function existingOf(kind) {
        return _profiles[kind].map(function (p) {
            return { name: p.szName, sids: (p.aBindings || []).map(function (b) { return b.szSID; }) };
        });
    }

    function runScan(kind, isAuto) {
        loadPoints().then(function () {
            var rtSids = {};
            ((_meta && _meta.rtSids) || []).forEach(function (s) { rtSids[s] = true; });
            var result = window._equipScan.scan(_points, kind, { rtSids: rtSids, existing: existingOf(kind), meta: { roles: _meta.roles[kind], unitAliases: _meta.unitAliases } });
            if (isAuto && result.filter(function (c) { return !c.existing; }).length === 0) return;
            _scanList = result.map(function (c) {
                var bindings = {};
                rolesOf(kind).forEach(function (r) {
                    var h = c.roles[r];
                    bindings[r] = h ? { szSID: h.szSid, szUnit: h.szUnitGuess || unitsOf(kind, r)[0].unit, nHits: h.nHits } : null;
                });
                return {
                    szKind: kind, szName: c.szName, szLabel: c.szLabel, bindings: bindings,
                    arrMatched: c.arrMatchedRoles, szConfidence: c.szConfidence, existing: c.existing,
                    groupPoints: c.arrGroupPoints, isChecked: c.isReady && !c.existing
                };
            });
            renderScanModal(kind, isAuto);
            bootstrap.Modal.getOrCreateInstance($('eqsScanModal')).show();
        });
    }

    function candMissing(c) {
        var bound = Object.keys(c.bindings).filter(function (r) { return c.bindings[r] && c.bindings[r].szSID; });
        return window._equipScan.missingRequired(c.szKind, bound);
    }

    function unitSelectHtml(kind, role, szSelected, szAttr) {
        return '<select class="form-select form-select-sm eqs-unit" ' + szAttr + '>' +
            unitsOf(kind, role).map(function (u) {
                return '<option value="' + esc(u.unit) + '"' + (u.unit === szSelected ? ' selected' : '') + '>' + esc(u.unit || '—') + '</option>';
            }).join('') + '</select>';
    }

    function renderScanModal(kind, isAuto) {
        var nNew = _scanList.filter(function (c) { return !c.existing; }).length;
        $('eqsScanTitle').textContent = isAuto
            ? t('eq.scan.title_auto', { n: nNew, kind: kindLabel(kind) })
            : t('eq.scan.title', { n: _scanList.length, kind: kindLabel(kind) });
        var host = $('eqsScanBody');
        if (_scanList.length === 0) {
            host.innerHTML = '<div class="text-center text-muted py-4">' + esc(t('eq.scan.none', { kind: kindLabel(kind) })) + '</div>';
            updateScanFooter();
            return;
        }
        host.innerHTML = '<div class="small text-muted mb-2">' + esc(t('eq.scan.hint')) + '</div>' +
            _scanList.map(function (c, i) { return scanCardHtml(c, i); }).join('');
        updateScanFooter();
    }

    function scanCardHtml(c, i) {
        var miss = candMissing(c);
        var isReady = miss.length === 0;
        var szBadges =
            (c.existing ? '<span class="badge bg-secondary me-1">' + esc(t('eq.scan.existing')) + '</span>' : '') +
            '<span class="badge eqs-conf-' + c.szConfidence + ' me-1">' + esc(t('eq.scan.conf_' + c.szConfidence)) + '</span>' +
            (isReady ? '' : '<span class="badge bg-warning text-dark">' + esc(t('eq.scan.missing', { list: missingText(miss) })) + '</span>');
        var szBasis = (c.arrMatched || []).map(function (r) { return esc(roleLabel(r)); }).join('、') || '—';
        // 下拉兩段：本設備（群內全部點位）＋ 其他設備中名稱命中此角色者（流量計 / 電表常不在主機子設備內）
        var optHtml = function (p, szSel) {
            // 群外點位帶設備前綴，群內只顯示點位名
            var szText = (p.szDeviceLabel && p.szDeviceLabel !== c.szLabel) ? pointLabel(p.szSid) : (p.szName || p.szSid);
            return '<option value="' + esc(p.szSid) + '"' + (p.szSid === szSel ? ' selected' : '') + '>' +
                esc(szText) + (p.szUnit ? ' (' + esc(p.szUnit) + ')' : '') + '</option>';
        };
        var opts = function (role) {
            var b = c.bindings[role];
            var szSel = b && b.szSID ? b.szSID : '';
            var own = c.groupPoints;
            var inGroup = {};
            own.forEach(function (p) { inGroup[p.szSid] = true; });
            var arrRoles = window._equipScan.ROLES[c.szKind];
            var others = _points.filter(function (p) {
                if (!p.szSid || inGroup[p.szSid]) return false;
                if (p.szSid === szSel) return true;   // 經「其他…」挑選的群外點位
                return window._equipScan.resolveScanRole(p.szName || '', arrRoles) === role;
            });
            if (szSel && !inGroup[szSel] && !others.some(function (p) { return p.szSid === szSel; }) && _pointBySid[szSel]) others.unshift(_pointBySid[szSel]);
            var szHtml = '<option value="">' + esc(t('eq.scan.unbound')) + '</option>';
            if (others.length === 0) return szHtml + own.map(function (p) { return optHtml(p, szSel); }).join('');
            return szHtml +
                '<optgroup label="' + esc(t('eq.scan.opt_own')) + '">' + own.map(function (p) { return optHtml(p, szSel); }).join('') + '</optgroup>' +
                '<optgroup label="' + esc(t('eq.scan.opt_other')) + '">' + others.map(function (p) { return optHtml(p, szSel); }).join('') + '</optgroup>';
        };
        var rows = rolesOf(c.szKind).map(function (role) {
            var b = c.bindings[role];
            return '<tr>' +
                '<td class="text-nowrap">' + esc(roleLabel(role)) + (isRequiredRole(c.szKind, role) ? ' <span class="text-danger">*</span>' : '') +
                    (b && b.nHits > 1 ? ' <span class="badge bg-info text-dark" title="' + esc(t('eq.scan.multi_hint')) + '">' + esc(t('eq.scan.multi', { n: b.nHits })) + '</span>' : '') + '</td>' +
                '<td><div class="input-group input-group-sm">' +
                    '<select class="form-select form-select-sm" data-scan="' + i + '" data-role="' + role + '" data-field="sid">' + opts(role) + '</select>' +
                    '<button class="btn btn-outline-secondary" type="button" title="' + esc(t('eq.scan.pick_other')) + '" onclick="window._eqs.scanPick(' + i + ',\'' + role + '\')"><i class="fas fa-ellipsis-h"></i></button>' +
                '</div></td>' +
                '<td style="width:120px;">' + unitSelectHtml(c.szKind, role, b ? b.szUnit : '', 'data-scan="' + i + '" data-role="' + role + '" data-field="unit"') + '</td>' +
            '</tr>';
        }).join('');
        return '<div class="card mb-2 eqs-scan-card' + (c.isChecked ? ' is-checked' : '') + '" data-card="' + i + '">' +
            '<div class="card-header py-1 px-2 d-flex align-items-center gap-2 flex-wrap">' +
              '<input class="form-check-input mt-0" type="checkbox" data-scan="' + i + '" data-field="check"' + (c.isChecked ? ' checked' : '') + (isReady ? '' : ' disabled') + ' />' +
              '<input class="form-control form-control-sm eqs-scan-name" data-scan="' + i + '" data-field="name" value="' + esc(c.szName) + '" />' +
              '<span class="small text-muted">' + esc(t('eq.scan.device')) + '：' + esc(c.szLabel) + '</span>' +
              '<span class="ms-auto">' + szBadges + '</span>' +
            '</div>' +
            '<div class="card-body p-2">' +
              '<div class="small mb-1"><span class="text-muted">' + esc(t('eq.scan.basis')) + '：</span>' + szBasis + '</div>' +
              '<details' + (isReady ? '' : ' open') + '><summary class="small text-primary">' + esc(t('eq.scan.roles_detail')) + '</summary>' +
                '<table class="table table-sm mb-0 mt-1 eqs-scan-roles"><tbody>' + rows + '</tbody></table>' +
              '</details>' +
            '</div>' +
        '</div>';
    }

    function updateScanFooter() {
        var n = _scanList.filter(function (c) { return c.isChecked; }).length;
        var btn = $('eqsScanImport');
        btn.disabled = n === 0;
        btn.textContent = t('eq.scan.import', { n: n });
    }

    function rerenderScanCard(i) {
        var old = document.querySelector('.eqs-scan-card[data-card="' + i + '"]');
        if (!old) return;
        // 重繪前記住「角色點位」是否展開，避免使用者改一個下拉就被收起來
        var oldDetails = old.querySelector('details');
        var isOpen = oldDetails && oldDetails.open;
        var wrap = document.createElement('div');
        wrap.innerHTML = scanCardHtml(_scanList[i], i);
        var card = wrap.firstChild;
        if (isOpen) card.querySelector('details').open = true;
        old.replaceWith(card);
        updateScanFooter();
    }

    function onScanChange(ev) {
        var el = ev.target;
        var i = el.dataset.scan;
        if (i == null) return;
        var c = _scanList[+i];
        var szField = el.dataset.field;
        if (szField === 'check') { c.isChecked = el.checked; el.closest('.eqs-scan-card').classList.toggle('is-checked', el.checked); updateScanFooter(); return; }
        if (szField === 'name') { c.szName = el.value; return; }
        var role = el.dataset.role;
        if (szField === 'sid') {
            if (!el.value) c.bindings[role] = null;
            else {
                var p = _pointBySid[el.value];
                c.bindings[role] = { szSID: el.value, szUnit: window._equipScan.guessUnit(role, p && p.szUnit, { roles: _meta.roles[c.szKind], unitAliases: _meta.unitAliases }), nHits: 1 };
            }
        } else if (szField === 'unit' && c.bindings[role]) {
            c.bindings[role].szUnit = el.value;
        }
        var isReady = candMissing(c).length === 0;
        if (!isReady) c.isChecked = false;
        rerenderScanCard(+i);
    }

    function scanPick(i, role) {
        var c = _scanList[i];
        window.PointPicker.open({}).then(function (p) {
            if (!p) return;
            c.bindings[role] = { szSID: p.sid, szUnit: window._equipScan.guessUnit(role, p.unit, { roles: _meta.roles[c.szKind], unitAliases: _meta.unitAliases }), nHits: 1 };
            rerenderScanCard(i);
        });
    }

    function importScan() {
        var picked = _scanList.filter(function (c) { return c.isChecked; });
        if (picked.length === 0) return;
        var defaults = _meta.defaults[_kind] || {};
        var body = {
            aProfiles: picked.map(function (c) {
                return Object.assign({
                    nId: 0, szKind: c.szKind, szName: (c.szName || c.szLabel).trim(), isEnabled: true,
                    dGoodThreshold: defaults.good, dFairThreshold: defaults.fair
                }, chillerDefaults(c.szKind), {
                    aBindings: Object.keys(c.bindings).filter(function (r) { return c.bindings[r] && c.bindings[r].szSID; })
                        .map(function (r) { return { szRole: r, szSID: c.bindings[r].szSID, szUnit: c.bindings[r].szUnit }; })
                });
            })
        };
        var btn = $('eqsScanImport');
        btn.disabled = true;
        postJson(API + '/profiles/batch', body).then(function (res) {
            bootstrap.Modal.getOrCreateInstance($('eqsScanModal')).hide();
            toast(t('eq.scan.imported', { n: (res.ids || []).length }));
            return loadProfiles().then(renderList);
        }).catch(function (err) {
            toast(errText(err), 'danger');
            btn.disabled = false;
        });
    }

    // ── 手動新增 / 編輯 ──────────────────────────────────
    function openEdit(p) {
        var defaults = (_meta.defaults && _meta.defaults[_kind]) || {};
        _editing = p ? JSON.parse(JSON.stringify(p)) : Object.assign({
            nId: 0, szKind: _kind, szName: '', nSortOrder: 0, isEnabled: true,
            dRatedOutput: null, dRatedPower: null, dMaxKwh: null, dSpecificHeat: null,
            dGoodThreshold: defaults.good, dFairThreshold: defaults.fair, szDescription: '', aBindings: [],
            szControlType: null, dMinLoadPct: null, dMaxPressure: null, nMaxStartsPerHour: null,
            szCoolingType: null, szChillerType: null, szThresholdMode: null
        }, chillerDefaults(_kind));
        renderEdit();
        bootstrap.Modal.getOrCreateInstance($('eqsEditModal')).show();
    }

    function editBinding(role) {
        return (_editing.aBindings || []).find(function (b) { return b.szRole === role; }) || null;
    }

    function renderEdit() {
        var e = _editing;
        var kind = e.szKind;
        var isCh = kind === 'chiller';
        $('eqsEditTitle').textContent = (e.nId ? t('eq.edit.title_edit') : t('eq.edit.title_add', { kind: kindLabel(kind) }));
        var field = function (szKey, szLabel, szVal, szType, szExtra) {
            return '<div class="col-6 col-md-4"><label class="form-label small mb-0">' + esc(szLabel) + '</label>' +
                '<input class="form-control form-control-sm" data-edit="' + szKey + '" type="' + (szType || 'number') + '" step="any" value="' + esc(szVal == null ? '' : szVal) + '"' + (szExtra || '') + ' /></div>';
        };
        var roleRows = rolesOf(kind).map(function (role) {
            var b = editBinding(role);
            return '<tr>' +
                '<td class="text-nowrap">' + esc(roleLabel(role)) + (isRequiredRole(kind, role) ? ' <span class="text-danger">*</span>' : '') + '</td>' +
                '<td class="small">' + (b ? esc(pointLabel(b.szSID)) + ' <span class="text-muted">(' + esc(b.szSID) + ')</span>' : '<span class="text-muted">' + esc(t('eq.scan.unbound')) + '</span>') + '</td>' +
                '<td style="width:120px;">' + (b ? unitSelectHtml(kind, role, b.szUnit, 'data-edit-unit="' + role + '"') : '') + '</td>' +
                '<td class="text-end text-nowrap" style="width:90px;">' +
                    '<button class="btn btn-sm btn-outline-primary py-0 me-1" type="button" onclick="window._eqs.editPick(\'' + role + '\')"><i class="fas fa-crosshairs"></i></button>' +
                    (b ? '<button class="btn btn-sm btn-outline-secondary py-0" type="button" onclick="window._eqs.editClear(\'' + role + '\')"><i class="fas fa-times"></i></button>' : '') +
                '</td></tr>';
        }).join('');
        var miss = window._equipScan.missingRequired(kind, (e.aBindings || []).map(function (b) { return b.szRole; }));
        $('eqsEditBody').innerHTML =
            '<div class="row g-2 mb-2">' +
              '<div class="col-12 col-md-8"><label class="form-label small mb-0">' + esc(t('eq.edit.name')) + ' <span class="text-danger">*</span></label>' +
                '<input class="form-control form-control-sm" data-edit="szName" maxlength="100" value="' + esc(e.szName) + '" /></div>' +
              '<div class="col-6 col-md-2">' + '<label class="form-label small mb-0">' + esc(t('eq.edit.sort')) + '</label>' +
                '<input class="form-control form-control-sm" data-edit="nSortOrder" type="number" value="' + esc(e.nSortOrder || '') + '" /></div>' +
              '<div class="col-6 col-md-2 d-flex align-items-end"><div class="form-check mb-1">' +
                '<input class="form-check-input" type="checkbox" id="eqsEditEnabled" data-edit="isEnabled"' + (e.isEnabled ? ' checked' : '') + ' />' +
                '<label class="form-check-label small" for="eqsEditEnabled">' + esc(t('eq.setting.enabled')) + '</label></div></div>' +
              field('dRatedOutput', isCh ? t('eq.edit.rated_rt') : t('eq.edit.rated_flow'), e.dRatedOutput) +
              field('dRatedPower', t('eq.edit.rated_kw'), e.dRatedPower) +
              field('dMaxKwh', t('eq.edit.max_kwh'), e.dMaxKwh) +
              field('dGoodThreshold', t('eq.edit.good', { unit: isCh ? 'kW/RT' : 'kW/(m³/min)' }), e.dGoodThreshold) +
              field('dFairThreshold', t('eq.edit.fair', { unit: isCh ? 'kW/RT' : 'kW/(m³/min)' }), e.dFairThreshold) +
              (isCh ? field('dSpecificHeat', t('eq.edit.specific_heat'), e.dSpecificHeat, 'number', ' placeholder="' + esc(_meta.defaultSpecificHeat) + '"') : '') +
              '<div class="col-12"><div class="form-text mt-0">' + esc(t(isCh ? 'eq.edit.threshold_hint_chiller' : 'eq.edit.threshold_hint')) + '</div></div>' +
            '</div>' +
            (isCh ? chillerFieldsHtml(e) : diagFieldsHtml(e)) +
            '<h6 class="small fw-semibold mb-1">' + esc(t('eq.edit.roles')) + '</h6>' +
            '<div class="small text-muted mb-1">' + esc(t(isCh ? 'eq.edit.roles_hint_chiller' : 'eq.edit.roles_hint_compressor')) + '</div>' +
            '<table class="table table-sm align-middle mb-1"><tbody>' + roleRows + '</tbody></table>' +
            (miss.length ? '<div class="alert alert-warning py-1 px-2 small mb-1">' + esc(t('eq.edit.missing', { list: missingText(miss) })) + '</div>' : '') +
            '<div class="mb-0"><label class="form-label small mb-0">' + esc(t('eq.edit.description')) + '</label>' +
              '<input class="form-control form-control-sm" data-edit="szDescription" maxlength="500" value="' + esc(e.szDescription || '') + '" /></div>';
        $('eqsEditSave').disabled = miss.length > 0;
        if (isCh) updateThresholdPreview();
    }

    // 冰機機型 + 門檻模式（依標準 → 門檻由 90.1 表帶出、優 / 可輸入框鎖定；手動 → 用輸入值）
    function chillerFieldsHtml(e) {
        var sel = function (szKey, szLabel, aValues, szCur, fnText) {
            return '<div class="col-6 col-md-3"><label class="form-label small mb-0">' + esc(szLabel) + '</label>' +
                '<select class="form-select form-select-sm" data-edit="' + szKey + '">' + aValues.map(function (v) {
                    return '<option value="' + v + '"' + ((szCur || '') === v ? ' selected' : '') + '>' + esc(fnText(v)) + '</option>';
                }).join('') + '</select></div>';
        };
        var unset = function (fn) { return function (v) { return v ? fn(v) : t('eq.edit.unset'); }; };
        return '<h6 class="small fw-semibold mb-1">' + esc(t('eq.edit.chiller_model')) + '</h6>' +
            '<div class="row g-2 mb-2">' +
              sel('szCoolingType', t('eq.edit.cooling_type'), [''].concat(window.EquipmentThreshold.COOLING), e.szCoolingType,
                  unset(function (v) { return t('eq.cooling.' + v); })) +
              sel('szChillerType', t('eq.edit.chiller_type'), [''].concat(window.EquipmentThreshold.TYPES), e.szChillerType,
                  unset(function (v) { return t('eq.ctype.' + v); })) +
              sel('szControlType', t('eq.edit.chiller_control'), ['', 'fixed', 'vsd'], e.szControlType,
                  unset(function (v) { return t('eq.chctrl.' + v); })) +
              sel('szThresholdMode', t('eq.edit.threshold_mode'), ['standard', 'manual'], e.szThresholdMode || 'standard',
                  function (v) { return t('eq.thmode.' + v); }) +
              '<div class="col-12"><div class="alert alert-light border py-1 px-2 small mb-0" id="eqsThPreview"></div></div>' +
            '</div>';
    }

    function updateThresholdPreview() {
        var host = $('eqsThPreview');
        if (!host) return;
        collectEditFields();
        var e = _editing;
        var isStd = e.szThresholdMode !== 'manual';
        var elGood = document.querySelector('#eqsEditBody [data-edit="dGoodThreshold"]');
        var elFair = document.querySelector('#eqsEditBody [data-edit="dFairThreshold"]');
        var th = resolveChiller(e);
        [elGood, elFair].forEach(function (el) { if (el) el.disabled = isStd; });
        if (isStd) {
            // 鎖定框只顯示解析值；不寫回 _editing（DB 的 Good/Fair 保留給切回手動時用）
            if (elGood) elGood.value = th.good;
            if (elFair) elFair.value = th.fair;
            host.innerHTML = esc(t('eq.edit.th_preview_std', { fl: fmt3(th.basis.stdFl), good: fmt3(th.good), fair: fmt3(th.fair) })) +
                '<div class="text-muted">' + esc(window.EquipmentThreshold.format(th.basis)) + '</div>';
        } else {
            if (elGood && elGood.value === '') { elGood.value = th.good; e.dGoodThreshold = th.good; }
            if (elFair && elFair.value === '') { elFair.value = th.fair; e.dFairThreshold = th.fair; }
            host.textContent = t('eq.edit.th_preview_manual');
        }
    }

    function onEditChange(ev) {
        var k = ev.target && ev.target.dataset && ev.target.dataset.edit;
        if (!_editing || _editing.szKind !== 'chiller') return;
        if (k === 'szCoolingType' || k === 'szChillerType' || k === 'szControlType' || k === 'szThresholdMode' || k === 'dRatedOutput') {
            if (k === 'szThresholdMode' && ev.target.value === 'manual') {
                // 切回手動：輸入框還是依標準時的解析值 → 改回 DB 原值（空則由 updateThresholdPreview 帶預設）
                var elGood = document.querySelector('#eqsEditBody [data-edit="dGoodThreshold"]');
                var elFair = document.querySelector('#eqsEditBody [data-edit="dFairThreshold"]');
                if (elGood) elGood.value = _editing.dGoodThreshold == null ? '' : _editing.dGoodThreshold;
                if (elFair) elFair.value = _editing.dFairThreshold == null ? '' : _editing.dFairThreshold;
                [elGood, elFair].forEach(function (el) { if (el) el.disabled = false; });
            }
            updateThresholdPreview();
        }
    }

    // 空壓系統診斷欄位（空壓機限定，皆可不填 → 診斷端退回推定 / 觀測值）
    function diagFieldsHtml(e) {
        var ctrl = ['', 'fixed', 'vsd', 'modulation'].map(function (v) {
            return '<option value="' + v + '"' + ((e.szControlType || '') === v ? ' selected' : '') + '>' + esc(t(v ? 'eq.ctrl.' + v : 'eq.ctrl.auto')) + '</option>';
        }).join('');
        var input = function (szKey, szLabel, v, szStep) {
            return '<div class="col-6 col-md-3"><label class="form-label small mb-0">' + esc(szLabel) + '</label>' +
                '<input class="form-control form-control-sm" data-edit="' + szKey + '" type="number" min="0" step="' + (szStep || 'any') + '" value="' + esc(v == null ? '' : v) + '" /></div>';
        };
        return '<h6 class="small fw-semibold mb-1">' + esc(t('eq.edit.diag_fields')) + '</h6>' +
            '<div class="row g-2 mb-2">' +
              '<div class="col-6 col-md-3"><label class="form-label small mb-0">' + esc(t('eq.edit.control_type')) + '</label>' +
                '<select class="form-select form-select-sm" data-edit="szControlType">' + ctrl + '</select></div>' +
              input('dMinLoadPct', t('eq.edit.min_load_pct'), e.dMinLoadPct) +
              input('dMaxPressure', t('eq.edit.max_pressure'), e.dMaxPressure) +
              input('nMaxStartsPerHour', t('eq.edit.max_starts'), e.nMaxStartsPerHour, '1') +
              '<div class="col-12"><div class="form-text mt-0">' + esc(t('eq.edit.diag_fields_hint')) + '</div></div>' +
            '</div>';
    }

    function collectEditFields() {
        document.querySelectorAll('#eqsEditBody [data-edit]').forEach(function (el) {
            var k = el.dataset.edit;
            if (el.disabled) return;   // 依標準時鎖定的優 / 可框顯示的是解析值，不收
            if (k === 'isEnabled') _editing.isEnabled = el.checked;
            else if (k === 'szName' || k === 'szDescription') _editing[k] = el.value;
            else if (k === 'szControlType' || k === 'szCoolingType' || k === 'szChillerType' || k === 'szThresholdMode') _editing[k] = el.value || null;
            else if (k === 'nMaxStartsPerHour') _editing.nMaxStartsPerHour = el.value === '' ? null : (parseInt(el.value, 10) || null);
            else if (k === 'nSortOrder') _editing.nSortOrder = parseInt(el.value, 10) || 0;
            else _editing[k] = num(el.value);
        });
        document.querySelectorAll('#eqsEditBody [data-edit-unit]').forEach(function (el) {
            var b = editBinding(el.dataset.editUnit);
            if (b) b.szUnit = el.value;
        });
    }

    function editPick(role) {
        collectEditFields();
        var cur = editBinding(role);
        window.PointPicker.open({}).then(function (p) {
            if (!p) return;
            if (!_pointBySid[p.sid]) loadPoints();
            var szUnit = window._equipScan.guessUnit(role, p.unit, { roles: _meta.roles[_editing.szKind], unitAliases: _meta.unitAliases });
            if (cur) { cur.szSID = p.sid; cur.szUnit = szUnit; }
            else _editing.aBindings.push({ szRole: role, szSID: p.sid, szUnit: szUnit });
            renderEdit();
        });
    }

    function editClear(role) {
        collectEditFields();
        _editing.aBindings = _editing.aBindings.filter(function (b) { return b.szRole !== role; });
        renderEdit();
    }

    function saveEdit() {
        collectEditFields();
        var e = _editing;
        var body = {
            nId: e.nId || 0, szKind: e.szKind, szName: (e.szName || '').trim(), nSortOrder: e.nSortOrder || 0,
            isEnabled: !!e.isEnabled, dRatedOutput: e.dRatedOutput, dRatedPower: e.dRatedPower, dMaxKwh: e.dMaxKwh,
            dSpecificHeat: e.dSpecificHeat, dGoodThreshold: e.dGoodThreshold, dFairThreshold: e.dFairThreshold,
            szDescription: e.szDescription,
            szControlType: e.szControlType || null, dMinLoadPct: e.dMinLoadPct, dMaxPressure: e.dMaxPressure,
            nMaxStartsPerHour: e.nMaxStartsPerHour,
            szCoolingType: e.szCoolingType || null, szChillerType: e.szChillerType || null, szThresholdMode: e.szThresholdMode || null,
            aBindings: e.aBindings.map(function (b) { return { szRole: b.szRole, szSID: b.szSID, szUnit: b.szUnit }; })
        };
        if (!body.szName) { toast(t('eq.err.name_required'), 'warning'); return; }
        postJson(API + '/profiles', body).then(function () {
            bootstrap.Modal.getOrCreateInstance($('eqsEditModal')).hide();
            toast(t('eq.edit.saved'));
            return loadProfiles().then(renderList);
        }).catch(function (err) { toast(errText(err), 'danger'); });
    }

    function edit(nId) {
        var p = _profiles[_kind].find(function (x) { return x.nId === nId; });
        if (p) openEdit(p);
    }

    function remove(nId) {
        var p = _profiles[_kind].find(function (x) { return x.nId === nId; });
        if (!p || !confirm(t('eq.setting.delete_confirm', { name: p.szName }))) return;
        postJson(API + '/profiles/' + nId, null, 'DELETE').then(function () {
            toast(t('eq.setting.deleted'));
            return loadProfiles().then(renderList);
        }).catch(function (err) { toast(errText(err), 'danger'); });
    }

    // ── 回填歷史 ──────────────────────────────────────────
    function backfill(nId) {
        _backfillTarget = nId == null ? null : nId;
        var p = nId == null ? null : _profiles[_kind].find(function (x) { return x.nId === nId; });
        $('eqsBackfillTarget').textContent = p ? p.szName : t('eq.backfill.all');
        var dTo = new Date();
        var dFrom = new Date(); dFrom.setDate(dFrom.getDate() - 30);
        $('eqsBackfillFrom').value = dateStr(dFrom);
        $('eqsBackfillTo').value = dateStr(dTo);
        bootstrap.Modal.getOrCreateInstance($('eqsBackfillModal')).show();
    }

    function runBackfill() {
        postJson(API + '/backfill', { nProfileId: _backfillTarget, szFrom: $('eqsBackfillFrom').value, szTo: $('eqsBackfillTo').value })
            .then(function () {
                bootstrap.Modal.getOrCreateInstance($('eqsBackfillModal')).hide();
                toast(t('eq.backfill.sent'));
            })
            .catch(function (err) { toast(errText(err), 'danger'); });
    }

    // ── 初始化 ────────────────────────────────────────────
    function init() {
        var root = $('eqSettingRoot');
        if (!root) return;
        var szInit = root.dataset.kind === 'compressor' ? 'compressor' : 'chiller';
        document.querySelectorAll('#eqsTabs .nav-link').forEach(function (a) {
            a.addEventListener('click', function (ev) { ev.preventDefault(); selectKind(a.dataset.kind); });
        });
        $('eqsBtnScan').addEventListener('click', function () { runScan(_kind, false); });
        $('eqsBtnAdd').addEventListener('click', function () { openEdit(null); });
        $('eqsBtnBackfillAll').addEventListener('click', function () { backfill(null); });
        $('eqsScanBody').addEventListener('change', onScanChange);
        $('eqsScanImport').addEventListener('click', importScan);
        $('eqsScanSelectAll').addEventListener('click', function () {
            _scanList.forEach(function (c, i) { if (candMissing(c).length === 0) { c.isChecked = true; } });
            renderScanModal(_kind, false);
        });
        $('eqsEditSave').addEventListener('click', saveEdit);
        $('eqsEditBody').addEventListener('change', onEditChange);
        $('eqsEditBody').addEventListener('input', function (ev) {
            if (ev.target && ev.target.dataset && ev.target.dataset.edit === 'dRatedOutput') onEditChange(ev);
        });
        $('eqsBackfillRun').addEventListener('click', runBackfill);

        Promise.all([fetchJson(API + '/meta').then(function (m) { _meta = m; }), loadPoints()])
            .then(loadProfiles)
            .then(function () { selectKind(szInit); })
            .catch(function (err) {
                $('eqsListBody').innerHTML = '<div class="alert alert-danger m-2">' + esc(errText(err)) + '</div>';
            });
    }

    window._eqs = {
        scan: function () { runScan(_kind, false); },
        add: function () { openEdit(null); },
        edit: edit,
        remove: remove,
        backfill: backfill,
        scanPick: scanPick,
        editPick: editPick,
        editClear: editClear
    };

    if (window.i18n && window.i18n.ready) window.i18n.ready(init);
    else document.addEventListener('DOMContentLoaded', init);
})();
