/* 空壓系統規則式診斷 — /CompressorEfficiency「系統診斷」與「診斷設定」tab。
   - 系統診斷（全部可見）：GET api/diagnosis/latest → 最新排程快照（每日 02:00 最近 7 天滾動）
       Engineer 另可指定期間 POST api/diagnosis/run 重算，並展開證據 / 公式 / 停機延遲候選
   - 診斷設定（Engineer）：GET/POST api/diagnosis/setting — 需求壓力、系統級點位、生產時段、劣化基準期、手填電價、參數覆寫 / 確認
   規則引擎在伺服端（Common CompressorDiagnosisEngine，不經 LLM）；本檔只把 key + 數字依 culture 渲染成文字。
   依賴：i18n.js；Engineer 另需 point-grouping.js → point-picker.js（選點位） */
(function () {
    'use strict';

    var API = '/CompressorEfficiency/api/diagnosis';
    var GROUPS = [
        ['pre', ['D']],
        ['p1', ['P']],
        ['p2', ['L']],
        ['p3', ['S1', 'S2', 'S3', 'S4', 'S5', 'S6']],
        ['p4', ['E1', 'E2', 'E3', 'E4']]
    ];
    var STATUS_CLASS = {
        Triggered: 'bg-danger',
        NotTriggered: 'bg-success',
        InsufficientData: 'bg-secondary',
        PendingSiteData: 'bg-warning text-dark',
        PendingSetting: 'bg-warning text-dark',
        NotApplicable: 'bg-light text-muted border'
    };
    var SYS_ROLES = ['SYS_SUPPLYPRESS', 'SYS_USEPRESS', 'SYS_FLOW', 'SYS_DEWPOINT'];
    var DOW_ORDER = [1, 2, 3, 4, 5, 6, 0];

    var _isEngineer = false;
    var _loaded = {};
    var _latest = null;        // 最新排程快照
    var _shown = null;         // 目前顯示的快照（排程或手動）
    var _meta = null;          // 設定 API 回傳的參數定義 / 角色單位
    var _setting = null;       // 設定草稿
    var _pointBySid = {};

    // ── 工具 ──────────────────────────────────────────────
    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }
    function $(id) { return document.getElementById(id); }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function fmt(v, d) {
        if (v == null || v === '' || isNaN(v)) return '--';
        return Number(v).toLocaleString('en-US', { minimumFractionDigits: d || 0, maximumFractionDigits: d == null ? 2 : d });
    }
    function pad2(n) { return n < 10 ? '0' + n : String(n); }
    function dateStr(d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); }
    function dtStr(s) {
        if (!s) return '--';
        var d = new Date(s);
        return isNaN(d) ? String(s) : dateStr(d) + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
    }
    function num(v) {
        if (v === '' || v == null) return null;
        var n = parseFloat(v);
        return isNaN(n) ? null : n;
    }

    function fetchJson(url, opts) {
        return fetch(url, Object.assign({ credentials: 'same-origin' }, opts || {})).then(function (r) {
            return r.json().catch(function () { return {}; }).then(function (body) {
                if (!r.ok) { var e = new Error(body.error || ('HTTP ' + r.status)); e.body = body; throw e; }
                return body;
            });
        });
    }
    function postJson(url, data) {
        return fetchJson(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(data)
        });
    }
    function errText(err) {
        var b = err && err.body;
        if (b && b.error) return t('eq.diag.err.' + b.error, { detail: b.detail || '' });
        return (err && err.message) || String(err);
    }
    function toast(szMsg, szLevel) {
        var host = $('cdgToast');
        if (!host) {
            host = document.createElement('div');
            host.id = 'cdgToast';
            host.className = 'cdg-toast-host';
            document.body.appendChild(host);
        }
        var el = document.createElement('div');
        el.className = 'alert alert-' + (szLevel || 'success') + ' py-2 px-3 mb-2 shadow-sm';
        el.textContent = szMsg;
        host.appendChild(el);
        setTimeout(function () { el.remove(); }, 4000);
    }

    /** 引擎輸出的 {szKey, args} → 文字（訊號 / 建議代碼先翻譯，數字格式化） */
    function renderText(dt) {
        var a = {};
        Object.keys(dt.args || {}).forEach(function (k) {
            var v = dt.args[k];
            if (k === 'signal') v = String(v).indexOf('SYS_') === 0 ? t('eq.diag.sys.' + v) : t('eq.role.' + v);
            else if (k === 'name' && v === 'SYS') v = t('eq.diag.ui.system');
            else if (k === 'suggest') v = t('eq.diag.ui.suggest_' + v);
            else if (typeof v === 'number') v = fmt(v);
            else if (v == null) v = '--';
            a[k] = v;
        });
        return t(dt.szKey, a);
    }
    function paramLabel(key) { return t('eq.diag.param.' + key); }

    // ── tabs ──────────────────────────────────────────────
    function showTab(tab) {
        var panes = { efficiency: 'eqdPaneEfficiency', diagnosis: 'eqdPaneDiagnosis', setting: 'eqdPaneSetting' };
        if (!panes[tab] || !$(panes[tab])) tab = 'efficiency';
        Object.keys(panes).forEach(function (k) {
            var el = $(panes[k]);
            if (el) el.style.display = k === tab ? '' : 'none';
        });
        document.querySelectorAll('#eqdTabs .nav-link').forEach(function (a) {
            a.classList.toggle('active', a.dataset.tab === tab);
        });
        if (tab === 'efficiency') window.dispatchEvent(new Event('resize'));   // Chart.js 在隱藏時建立需重算尺寸
        if (tab === 'diagnosis' && !_loaded.diagnosis) { _loaded.diagnosis = true; loadLatest(); }
        if (tab === 'setting' && !_loaded.setting) { _loaded.setting = true; loadSetting(); }
    }

    // ── 系統診斷 ──────────────────────────────────────────
    function loadLatest() {
        var root = $('cdgRoot');
        root.innerHTML = '<div class="text-center text-muted py-4">' + esc(t('eq.diag.ui.loading')) + '</div>';
        fetchJson(API + '/latest').then(function (res) {
            _latest = res.snapshot;
            if (!res.hasCompressors) {
                root.innerHTML = '<div class="text-center text-muted py-5"><i class="fas fa-inbox fa-2x mb-2 d-block opacity-50"></i>' +
                    esc(t('eq.diag.ui.no_compressor')) + '</div>';
                return;
            }
            renderDiagnosis(_latest);
        }).catch(function (err) {
            root.innerHTML = '<div class="alert alert-danger m-2">' + esc(errText(err)) + '</div>';
        });
    }

    function runControlsHtml() {
        if (!_isEngineer) return '';
        var dTo = new Date(); dTo.setDate(dTo.getDate() - 1);
        var dFrom = new Date(dTo); dFrom.setDate(dFrom.getDate() - 29);
        return '<div class="d-flex align-items-center gap-2 flex-wrap cdg-run">' +
            '<label class="small mb-0">' + esc(t('eq.diag.ui.from')) + '</label>' +
            '<input type="date" class="form-control form-control-sm" id="cdgRunFrom" value="' + dateStr(dFrom) + '" />' +
            '<label class="small mb-0">' + esc(t('eq.diag.ui.to')) + '</label>' +
            '<input type="date" class="form-control form-control-sm" id="cdgRunTo" value="' + dateStr(dTo) + '" />' +
            '<button type="button" class="btn btn-sm btn-primary" id="cdgRunBtn"><i class="fas fa-play me-1"></i>' + esc(t('eq.diag.ui.run')) + '</button>' +
            (_shown && !_shown.isScheduled && _latest ? '<button type="button" class="btn btn-sm btn-outline-secondary" id="cdgBackLatest">' + esc(t('eq.diag.ui.back_latest')) + '</button>' : '') +
            '</div>';
    }

    function bindRunControls() {
        var btn = $('cdgRunBtn');
        if (btn) btn.addEventListener('click', runManual);
        var back = $('cdgBackLatest');
        if (back) back.addEventListener('click', function () { renderDiagnosis(_latest); });
    }

    function runManual() {
        var body = { szFrom: $('cdgRunFrom').value, szTo: $('cdgRunTo').value };
        var btn = $('cdgRunBtn');
        btn.disabled = true;
        btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>' + esc(t('eq.diag.ui.running'));
        postJson(API + '/run', body).then(function (snap) {
            renderDiagnosis(snap);
        }).catch(function (err) {
            toast(errText(err), 'danger');
            btn.disabled = false;
            btn.innerHTML = '<i class="fas fa-play me-1"></i>' + esc(t('eq.diag.ui.run'));
        });
    }

    function renderDiagnosis(snap) {
        _shown = snap;
        var root = $('cdgRoot');
        if (!snap) {
            root.innerHTML = '<div class="card shadow-sm"><div class="card-body">' +
                '<div class="text-muted mb-2"><i class="fas fa-info-circle me-1"></i>' + esc(t('eq.diag.ui.no_snapshot')) +
                (_isEngineer ? ' ' + esc(t('eq.diag.ui.no_snapshot_engineer')) : '') + '</div>' + runControlsHtml() + '</div></div>';
            bindRunControls();
            return;
        }
        var r = snap.result;
        var dtTo = new Date(r.dtTo); dtTo.setDate(dtTo.getDate() - 1);
        var szPeriod = dateStr(new Date(r.dtFrom)) + ' ~ ' + dateStr(dtTo);

        var html = '<div class="card shadow-sm mb-2"><div class="card-body py-2 d-flex justify-content-between align-items-center flex-wrap gap-2">' +
            '<div><span class="fw-semibold">' + esc(t('eq.diag.ui.period')) + '：' + esc(szPeriod) + '</span>' +
            ' <span class="badge ' + (snap.isScheduled ? 'bg-primary' : 'bg-secondary') + ' ms-1">' + esc(t(snap.isScheduled ? 'eq.diag.ui.scheduled' : 'eq.diag.ui.manual')) + '</span>' +
            '<span class="small text-muted ms-2">' + esc(t('eq.diag.ui.run_at')) + ' ' + esc(dtStr(snap.dtRunAt)) + (snap.szRunBy ? ' · ' + esc(snap.szRunBy) : '') + '</span></div>' +
            runControlsHtml() + '</div></div>';

        html += noticesHtml(r);
        html += tilesHtml(r);
        GROUPS.forEach(function (g) {
            var rules = g[1].map(function (id) { return (r.aRules || []).find(function (x) { return x.szId === id; }); }).filter(Boolean);
            if (!rules.length) return;
            html += '<div class="cdg-group-title">' + esc(t('eq.diag.group.' + g[0])) + '</div>';
            html += '<div class="cdg-rules">' + rules.map(function (rule) { return ruleHtml(rule, r); }).join('') + '</div>';
        });
        html += baselineHtml(r);
        root.innerHTML = html;
        bindRunControls();
        var go = root.querySelectorAll('[data-goto-setting]');
        go.forEach(function (a) { a.addEventListener('click', function (ev) { ev.preventDefault(); showTab('setting'); }); });
    }

    function noticesHtml(r) {
        var out = '';
        if (r.isBaselinePeriodMissing) {
            out += '<div class="alert alert-warning py-1 px-2 small mb-2"><i class="fas fa-calendar-alt me-1"></i>' + esc(t('eq.diag.baseline_missing')) +
                (_isEngineer ? ' <a href="#setting" data-goto-setting>' + esc(t('eq.diag.ui.goto_setting')) + '</a>' : '') + '</div>';
        }
        if (r.aUnconfirmedDefaults && r.aUnconfirmedDefaults.length) {
            out += '<div class="alert alert-info py-1 px-2 small mb-2"><i class="fas fa-flag me-1"></i>' +
                esc(t('eq.diag.ui.unconfirmed_banner', { list: r.aUnconfirmedDefaults.map(paramLabel).join('、') })) +
                (_isEngineer ? ' <a href="#setting" data-goto-setting>' + esc(t('eq.diag.ui.goto_setting')) + '</a>' : '') + '</div>';
        }
        (r.aOverlapFlags || []).forEach(function (f) {
            out += '<div class="alert alert-secondary py-1 px-2 small mb-2"><i class="fas fa-layer-group me-1"></i>' + esc(t('eq.diag.overlap.' + f)) + '</div>';
        });
        return out;
    }

    function tilesHtml(r) {
        var nTrig = (r.aRules || []).filter(function (x) { return x.status === 'Triggered'; }).length;
        var nPend = (r.aRules || []).filter(function (x) {
            return x.status === 'InsufficientData' || x.status === 'PendingSiteData' || x.status === 'PendingSetting';
        }).length;
        var szPrice = r.dPrice != null
            ? fmt(r.dPrice, 2) + ' NT$/kWh <span class="small text-muted">(' + esc(t(r.szPriceSource === 'manual' ? 'eq.diag.ui.price_manual' : 'eq.diag.ui.price_tariff')) + ')</span>'
            : '<span class="small text-muted">' + esc(t('eq.diag.ui.price_none')) + '</span>';
        function tile(szLabel, szValue, szCls) {
            return '<div class="cdg-tile ' + (szCls || '') + '"><div class="cdg-tile-label">' + esc(szLabel) + '</div><div class="cdg-tile-value">' + szValue + '</div></div>';
        }
        return '<div class="cdg-tiles mb-2">' +
            tile(t('eq.diag.ui.tile_triggered'), String(nTrig), nTrig > 0 ? 'is-hit' : '') +
            tile(t('eq.diag.ui.tile_pending'), String(nPend), nPend > 0 ? 'is-pending' : '') +
            tile(t('eq.diag.ui.tile_kwh'), fmt(r.dPeriodKwh, 0) + ' kWh') +
            tile(t('eq.diag.ui.tile_sp'), r.dSpecificPower != null ? fmt(r.dSpecificPower, 2) + ' kW/(m³/min)' : '--') +
            tile(t('eq.diag.ui.tile_price'), szPrice) +
            '</div>' +
            '<div class="small text-muted mb-2"><i class="fas fa-info-circle me-1"></i>' + esc(t('eq.diag.screening_disclaimer')) + '；' + esc(t('eq.diag.ui.not_additive')) + '</div>';
    }

    function savingsHtml(s) {
        if (!s) return '';
        var szMain = s.ntdYear
            ? t('eq.diag.savings.ntd_year', { low: fmt(s.ntdYear.dLow, 0), high: fmt(s.ntdYear.dHigh, 0) })
            : t('eq.diag.savings.kwh_year', { low: fmt(s.kwhYear.dLow, 0), high: fmt(s.kwhYear.dHigh, 0) });
        var szSub = fmt(s.kwhYear.dLow, 0) + ' ~ ' + fmt(s.kwhYear.dHigh, 0) + ' kWh/yr · ' +
            fmt(s.kwhPeriod.dLow, 0) + ' ~ ' + fmt(s.kwhPeriod.dHigh, 0) + ' kWh';
        return '<div class="cdg-savings"><i class="fas fa-coins me-1"></i>' + esc(szMain) + '<div class="small text-muted">' + esc(szSub) + '</div></div>';
    }

    function listHtml(items, szIcon, szCls) {
        if (!items || !items.length) return '';
        return '<ul class="cdg-list ' + (szCls || '') + '">' + items.map(function (x) {
            return '<li><i class="fas ' + szIcon + ' me-1"></i>' + esc(renderText(x)) + '</li>';
        }).join('') + '</ul>';
    }

    function ruleHtml(rule) {
        var badges = '<span class="badge ' + (STATUS_CLASS[rule.status] || 'bg-secondary') + '">' + esc(t('eq.diag.status.' + rule.status)) + '</span>';
        if (rule.szLevel) badges += ' <span class="badge ' + (rule.szLevel === 'significant' ? 'bg-danger' : 'bg-warning text-dark') + '">' + esc(t('eq.diag.level.' + rule.szLevel)) + '</span>';
        if (rule.isInferred) badges += ' <span class="badge bg-light text-dark border">' + esc(t('eq.diag.ui.inferred')) + '</span>';
        if (rule.aUnconfirmedDefaults && rule.aUnconfirmedDefaults.length) {
            badges += ' <span class="badge bg-info text-dark" title="' + esc(t('eq.diag.ui.default_flag_title', { list: rule.aUnconfirmedDefaults.map(paramLabel).join('、') })) + '">' +
                '<i class="fas fa-flag me-1"></i>' + esc(t('eq.diag.ui.default_flag')) + '</span>';
        }
        var body = savingsHtml(rule.savings);
        if (rule.szNextStep) body += '<div class="cdg-next"><span class="fw-semibold">' + esc(t('eq.diag.ui.next_step')) + '：</span>' + esc(t('eq.diag.next.' + rule.szNextStep)) + '</div>';
        if (rule.aMissing && rule.aMissing.length) {
            body += '<div class="small fw-semibold mt-1">' + esc(t('eq.diag.ui.missing')) + '</div>' + listHtml(rule.aMissing, 'fa-circle-exclamation text-warning');
        }
        if (rule.aNotes && rule.aNotes.length) body += listHtml(rule.aNotes, 'fa-info-circle text-muted', 'small text-muted');
        if (rule.aChecklist && rule.aChecklist.length && rule.status === 'Triggered') {
            body += '<details class="mt-1"><summary class="small">' + esc(t('eq.diag.ui.checklist')) + '</summary><ol class="small mb-1">' +
                rule.aChecklist.map(function (c) { return '<li>' + esc(t('eq.diag.check.' + c)) + '</li>'; }).join('') + '</ol></details>';
        }
        if (_isEngineer && ((rule.aEvidence && rule.aEvidence.length) || rule.szFormulaKey)) {
            body += '<details class="mt-1"' + (rule.status === 'Triggered' ? ' open' : '') + '><summary class="small">' + esc(t('eq.diag.ui.evidence')) + '</summary>' +
                listHtml(rule.aEvidence, 'fa-chart-bar text-primary', 'small') +
                (rule.szFormulaKey ? '<div class="small text-muted cdg-formula"><span class="fw-semibold">' + esc(t('eq.diag.ui.formula')) + '：</span>' + esc(t(rule.szFormulaKey)) + '</div>' : '') +
                candidatesHtml(rule.aCandidates) + '</details>';
        }
        return '<div class="card shadow-sm cdg-rule cdg-' + esc(rule.status) + '"><div class="card-body p-2">' +
            '<div class="d-flex justify-content-between align-items-start gap-1 mb-1 cdg-rule-head"><div class="fw-semibold">' +
            '<span class="text-muted me-1">' + esc(rule.szId) + '</span>' + esc(t('eq.diag.rule.' + rule.szId)) + '</div>' +
            '<div class="cdg-badges">' + badges + '</div></div>' + body + '</div></div>';
    }

    function candidatesHtml(list) {
        if (!list || !list.length) return '';
        return '<div class="small fw-semibold mt-1">' + esc(t('eq.diag.ui.candidates')) + '</div>' +
            '<div class="table-responsive"><table class="table table-sm small mb-1 cdg-candidates"><thead><tr>' +
            '<th>' + esc(t('eq.diag.ui.col_delay')) + '</th><th>' + esc(t('eq.diag.ui.col_kwh_period')) + '</th>' +
            '<th>' + esc(t('eq.diag.ui.col_added_starts')) + '</th><th>' + esc(t('eq.diag.ui.col_max_starts')) + '</th>' +
            '<th>' + esc(t('eq.diag.ui.col_feasible')) + '</th></tr></thead><tbody>' +
            list.map(function (c) {
                return '<tr><td>' + c.nDelayMinutes + '</td><td>' + fmt(c.kwhPeriod.dLow, 0) + ' ~ ' + fmt(c.kwhPeriod.dHigh, 0) + '（' + fmt(c.kwhPeriod.dCenter, 0) + '）</td>' +
                    '<td>' + c.nAddedStarts + '</td><td>' + c.nMaxStartsPerHour + '</td>' +
                    '<td>' + (c.isFeasible ? '<span class="text-success">' + esc(t('eq.diag.ui.feasible_yes')) + '</span>' : '<span class="text-danger">' + esc(t('eq.diag.ui.feasible_no')) + '</span>') + '</td></tr>';
            }).join('') + '</tbody></table></div>';
    }

    function baselineHtml(r) {
        if (!r.aBaseline || !r.aBaseline.length) return '';
        var rows = r.aBaseline.map(function (b) {
            var szCtrl = b.szControlType ? t('eq.ctrl.' + b.szControlType) : '--';
            if (b.szControlType && b.isControlTypeInferred) szCtrl += ' <span class="badge bg-light text-dark border">' + esc(t('eq.diag.ui.inferred')) + '</span>';
            var szKw = b.kw ? fmt(b.kw.dP05, 1) + ' / ' + fmt(b.kw.dP50, 1) + ' / ' + fmt(b.kw.dP95, 1) : '--';
            return '<tr><td class="text-nowrap">' + esc(b.szName) + (b.isLoadInferred ? ' <span class="badge bg-light text-dark border" title="' + esc(t('eq.diag.note.load_inferred')) + '">' + esc(t('eq.diag.ui.load_inferred_short')) + '</span>' : '') + '</td>' +
                '<td class="text-nowrap">' + (b.szControlType ? esc(t('eq.ctrl.' + b.szControlType)) + (b.isControlTypeInferred ? ' <span class="badge bg-light text-dark border">' + esc(t('eq.diag.ui.inferred')) + '</span>' : '') : '--') + '</td>' +
                '<td class="text-end">' + szKw + '</td>' +
                '<td class="text-end">' + (b.flow ? fmt(b.flow.dP50, 2) : '--') + '</td>' +
                '<td class="text-end">' + (b.press ? fmt(b.press.dP50, 2) : '--') + '</td>' +
                '<td class="text-end text-nowrap">' + fmt(b.dLoadedPct, 1) + ' / ' + fmt(b.dUnloadedPct, 1) + ' / ' + fmt(b.dOffPct, 1) + ' / ' + fmt(b.dUnknownPct, 1) + '</td>' +
                '<td class="text-end">' + fmt(b.dRunHours, 1) + '</td>' +
                '<td class="text-end">' + b.nStarts + '</td>' +
                '<td class="text-end">' + fmt(b.dCyclesP50, 1) + ' / ' + fmt(b.dCyclesP95, 1) + '</td>' +
                '<td class="text-end">' + fmt(b.dKwh, 0) + '</td></tr>';
        }).join('');
        var cols = ['col_name', 'col_ctrl', 'col_kw', 'col_flow', 'col_press', 'col_state', 'col_run_hours', 'col_starts', 'col_cycles', 'col_kwh'];
        return '<details class="card shadow-sm mt-3"' + (_isEngineer ? ' open' : '') + '><summary class="card-header py-2 eq-green-header"><i class="fas fa-ruler-combined me-1"></i>' + esc(t('eq.diag.ui.baseline_title')) + '</summary>' +
            '<div class="card-body p-2 table-responsive"><table class="table table-sm small mb-0 align-middle"><thead><tr>' +
            cols.map(function (c, i) { return '<th' + (i > 1 ? ' class="text-end"' : '') + '>' + esc(t('eq.diag.ui.' + c)) + '</th>'; }).join('') +
            '</tr></thead><tbody>' + rows + '</tbody></table></div></details>';
    }

    // ── 診斷設定（Engineer）──────────────────────────────
    function loadSetting() {
        var root = $('cdgSettingRoot');
        root.innerHTML = '<div class="text-center text-muted py-4">' + esc(t('eq.diag.ui.loading')) + '</div>';
        var pPoints = window.PointPicker ? window.PointPicker.load().then(function (d) {
            ((d && d.points) || d || []).forEach(function (p) { if (p && p.szSid) _pointBySid[p.szSid] = p; });
        }).catch(function () { }) : Promise.resolve();
        Promise.all([fetchJson(API + '/setting'), pPoints]).then(function (res) {
            _meta = res[0];
            _setting = JSON.parse(JSON.stringify(_meta.setting || {}));
            _setting.sysPoints = _setting.sysPoints || {};
            _setting.aProductionSchedule = _setting.aProductionSchedule || [];
            _setting.paramOverrides = _setting.paramOverrides || {};
            _setting.aConfirmedParams = _setting.aConfirmedParams || [];
            renderSetting();
        }).catch(function (err) {
            root.innerHTML = '<div class="alert alert-danger m-2">' + esc(errText(err)) + '</div>';
        });
    }

    function pointLabel(sid) {
        var p = _pointBySid[sid];
        if (!p) return sid;
        return (p.szDeviceLabel ? p.szDeviceLabel + ' / ' : '') + (p.szName || sid);
    }

    function timeOptions(szSel) {
        var out = '';
        for (var m = 0; m <= 1440; m += 30) {
            var sz = pad2(Math.floor(m / 60)) + ':' + pad2(m % 60);
            out += '<option value="' + sz + '"' + (sz === szSel ? ' selected' : '') + '>' + sz + '</option>';
        }
        return out;
    }

    function renderSetting() {
        var s = _setting;
        var html = '';
        var szUpdated = s.dtUpdatedAt ? t('eq.diag.ui.updated_by', { at: dtStr(s.dtUpdatedAt), by: s.szUpdatedBy || '' }) : '';
        html += '<div class="d-flex justify-content-between align-items-center flex-wrap gap-2 mb-2">' +
            '<div class="small text-muted">' + esc(szUpdated) + '</div>' +
            '<button type="button" class="btn btn-sm btn-primary" id="cdgSave"><i class="fas fa-save me-1"></i>' + esc(t('eq.diag.ui.save')) + '</button></div>';

        html += '<div class="row g-3"><div class="col-xl-6">';
        // 場域資料
        html += card('fa-industry', t('eq.diag.ui.site_title'),
            '<div class="row g-2">' +
            field('dDemandPressBar', t('eq.diag.ui.demand_press'), s.dDemandPressBar, t('eq.diag.ui.demand_press_hint')) +
            field('dManualPrice', t('eq.diag.ui.manual_price'), s.dManualPrice, t('eq.diag.ui.manual_price_hint')) +
            '</div>' +
            '<div class="small text-muted mt-2"><i class="fas fa-info-circle me-1"></i>' + esc(t('eq.diag.ui.device_fields_hint')) +
            ' <a href="/EquipmentEfficiencySetting?kind=compressor">/EquipmentEfficiencySetting</a></div>');

        // 系統級點位
        var sysRows = SYS_ROLES.map(function (role) {
            var b = s.sysPoints[role];
            var units = (_meta.sysRoles && _meta.sysRoles[role]) || [''];
            return '<tr><td class="text-nowrap">' + esc(t('eq.diag.sys.' + role)) + '<div class="small text-muted">' + esc(t('eq.diag.sys_hint.' + role)) + '</div></td>' +
                '<td class="small">' + (b && b.szSID ? esc(pointLabel(b.szSID)) + ' <span class="text-muted">(' + esc(b.szSID) + ')</span>' : '<span class="text-muted">' + esc(t('eq.diag.ui.unbound')) + '</span>') + '</td>' +
                '<td style="width:110px;">' + (b && b.szSID ? '<select class="form-select form-select-sm" data-sys-unit="' + role + '">' +
                    units.map(function (u) { return '<option value="' + esc(u) + '"' + (u === b.szUnit ? ' selected' : '') + '>' + esc(u || '—') + '</option>'; }).join('') + '</select>' : '') + '</td>' +
                '<td class="text-end text-nowrap">' +
                    '<button type="button" class="btn btn-sm btn-outline-primary py-0 me-1" data-sys-pick="' + role + '" title="' + esc(t('eq.diag.ui.pick')) + '"><i class="fas fa-crosshairs"></i></button>' +
                    (b && b.szSID ? '<button type="button" class="btn btn-sm btn-outline-secondary py-0" data-sys-clear="' + role + '" title="' + esc(t('eq.diag.ui.clear')) + '"><i class="fas fa-times"></i></button>' : '') +
                '</td></tr>';
        }).join('');
        html += card('fa-diagram-project', t('eq.diag.ui.sys_title'),
            '<table class="table table-sm align-middle mb-0"><tbody>' + sysRows + '</tbody></table>');

        // 劣化基準期
        html += card('fa-calendar-alt', t('eq.diag.ui.baseline_period_title'),
            '<div class="row g-2"><div class="col-6"><label class="form-label small mb-0">' + esc(t('eq.diag.ui.from')) + '</label>' +
            '<input type="date" class="form-control form-control-sm" id="cdgBaseFrom" value="' + esc(s.szBaselineFrom || '') + '" /></div>' +
            '<div class="col-6"><label class="form-label small mb-0">' + esc(t('eq.diag.ui.to')) + '</label>' +
            '<input type="date" class="form-control form-control-sm" id="cdgBaseTo" value="' + esc(s.szBaselineTo || '') + '" /></div></div>' +
            '<div class="form-text">' + esc(t('eq.diag.ui.baseline_period_hint')) + '</div>');
        html += '</div><div class="col-xl-6">';

        // 生產時段
        var schedRows = DOW_ORDER.map(function (d) {
            var ranges = s.aProductionSchedule.map(function (r, i) { return { r: r, i: i }; }).filter(function (x) { return x.r.nDay === d; });
            var cells = ranges.length ? ranges.map(function (x) {
                return '<div class="d-flex align-items-center gap-1 mb-1">' +
                    '<select class="form-select form-select-sm cdg-time" data-sched-start="' + x.i + '">' + timeOptions(x.r.szStart) + '</select>' +
                    '<span>–</span>' +
                    '<select class="form-select form-select-sm cdg-time" data-sched-end="' + x.i + '">' + timeOptions(x.r.szEnd) + '</select>' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary py-0" data-sched-del="' + x.i + '"><i class="fas fa-times"></i></button></div>';
            }).join('') : '<span class="small text-muted">' + esc(t('eq.diag.ui.schedule_none')) + '</span>';
            return '<tr><td class="text-nowrap" style="width:60px;">' + esc(t('eq.diag.ui.dow' + d)) + '</td><td>' + cells + '</td>' +
                '<td class="text-end" style="width:40px;"><button type="button" class="btn btn-sm btn-outline-primary py-0" data-sched-add="' + d + '" title="' + esc(t('eq.diag.ui.schedule_add')) + '"><i class="fas fa-plus"></i></button></td></tr>';
        }).join('');
        html += card('fa-clock', t('eq.diag.ui.schedule_title'),
            '<div class="d-flex justify-content-between align-items-center mb-1 flex-wrap gap-2"><span class="small text-muted">' + esc(t('eq.diag.ui.schedule_hint')) + '</span>' +
            '<button type="button" class="btn btn-sm btn-outline-primary" id="cdgFillWeekday">' + esc(t('eq.diag.ui.schedule_fill_weekday')) + '</button></div>' +
            '<table class="table table-sm align-middle mb-1"><tbody>' + schedRows + '</tbody></table>' +
            '<div class="form-check"><input class="form-check-input" type="checkbox" id="cdgHolidayOff"' + (s.isHolidayNonProduction !== false ? ' checked' : '') + ' />' +
            '<label class="form-check-label small" for="cdgHolidayOff">' + esc(t('eq.diag.ui.holiday_off')) + '</label></div>');
        html += '</div></div>';

        // 參數
        html += card('fa-sliders-h', t('eq.diag.ui.params_title'),
            '<div class="d-flex justify-content-between align-items-center mb-1 flex-wrap gap-2"><span class="small text-muted">' + esc(t('eq.diag.ui.params_hint')) + '</span>' +
            '<button type="button" class="btn btn-sm btn-outline-primary" id="cdgConfirmAll"><i class="fas fa-check-double me-1"></i>' + esc(t('eq.diag.ui.confirm_all')) + '</button></div>' +
            paramsTableHtml(), 'mt-3');

        $('cdgSettingRoot').innerHTML = html;
        bindSetting();
    }

    function card(szIcon, szTitle, szBody, szExtra) {
        return '<div class="card shadow-sm mb-3 ' + (szExtra || '') + '"><div class="card-header py-2 eq-green-header"><i class="fas ' + szIcon + ' me-1"></i>' + esc(szTitle) + '</div>' +
            '<div class="card-body p-2">' + szBody + '</div></div>';
    }
    function field(key, szLabel, v, szHint) {
        return '<div class="col-sm-6"><label class="form-label small mb-0">' + esc(szLabel) + '</label>' +
            '<input type="number" step="any" min="0" class="form-control form-control-sm" data-field="' + key + '" value="' + esc(v == null ? '' : v) + '" />' +
            '<div class="form-text mt-0">' + esc(szHint) + '</div></div>';
    }

    function paramState(key) {
        if (Object.prototype.hasOwnProperty.call(_setting.paramOverrides, key)) return 'overridden';
        if (_setting.aConfirmedParams.indexOf(key) >= 0) return 'confirmed';
        return 'default';
    }

    function paramsTableHtml() {
        var groups = {};
        (_meta.parameters || []).forEach(function (p) { (groups[p.group] = groups[p.group] || []).push(p); });
        var html = '<div class="table-responsive"><table class="table table-sm align-middle small mb-0 cdg-params"><thead><tr>' +
            '<th>' + esc(t('eq.diag.ui.col_param')) + '</th><th style="width:120px;">' + esc(t('eq.diag.ui.col_value')) + '</th>' +
            '<th class="text-end">' + esc(t('eq.diag.ui.col_default')) + '</th><th class="text-end">' + esc(t('eq.diag.ui.col_range')) + '</th>' +
            '<th>' + esc(t('eq.diag.ui.col_source')) + '</th><th>' + esc(t('eq.diag.ui.col_state_param')) + '</th><th></th></tr></thead><tbody>';
        ['dq', 'p', 'l', 's', 'e'].forEach(function (g) {
            if (!groups[g]) return;
            html += '<tr class="table-light"><td colspan="7" class="fw-semibold">' + esc(t('eq.diag.pgroup.' + g)) + '</td></tr>';
            groups[g].forEach(function (p) {
                var st = paramState(p.key);
                var v = st === 'overridden' ? _setting.paramOverrides[p.key] : '';
                var stBadge = st === 'overridden' ? '<span class="badge bg-primary">' + esc(t('eq.diag.ui.param_overridden')) + '</span>'
                    : st === 'confirmed' ? '<span class="badge bg-success">' + esc(t('eq.diag.ui.param_confirmed')) + '</span>'
                    : '<span class="badge bg-info text-dark">' + esc(t('eq.diag.ui.param_default')) + '</span>';
                var btn = st === 'default'
                    ? '<button type="button" class="btn btn-sm btn-outline-success py-0" data-param-confirm="' + p.key + '">' + esc(t('eq.diag.ui.confirm')) + '</button>'
                    : st === 'confirmed' ? '<button type="button" class="btn btn-sm btn-outline-secondary py-0" data-param-unconfirm="' + p.key + '">' + esc(t('eq.diag.ui.unconfirm')) + '</button>' : '';
                html += '<tr><td>' + esc(paramLabel(p.key)) + '<div class="text-muted cdg-param-key">' + esc(p.key) + '</div></td>' +
                    '<td><input type="number" step="any" class="form-control form-control-sm" data-param="' + p.key + '" min="' + p.min + '" max="' + p.max + '" placeholder="' + esc(p.defaultValue) + '" value="' + esc(v) + '" /></td>' +
                    '<td class="text-end">' + esc(p.defaultValue) + '</td><td class="text-end text-nowrap">' + esc(p.min) + ' ~ ' + esc(p.max) + '</td>' +
                    '<td><span class="badge bg-light text-dark border">' + esc(t('eq.diag.src.' + p.source)) + '</span></td>' +
                    '<td>' + stBadge + '</td><td class="text-end">' + btn + '</td></tr>';
            });
        });
        return html + '</tbody></table></div>';
    }

    /** 表單 → _setting（重新渲染前先收集，避免輸入遺失） */
    function collectSetting() {
        var root = $('cdgSettingRoot');
        root.querySelectorAll('[data-field]').forEach(function (el) { _setting[el.dataset.field] = num(el.value); });
        root.querySelectorAll('[data-sys-unit]').forEach(function (el) {
            var b = _setting.sysPoints[el.dataset.sysUnit];
            if (b) b.szUnit = el.value;
        });
        root.querySelectorAll('[data-sched-start]').forEach(function (el) { _setting.aProductionSchedule[+el.dataset.schedStart].szStart = el.value; });
        root.querySelectorAll('[data-sched-end]').forEach(function (el) { _setting.aProductionSchedule[+el.dataset.schedEnd].szEnd = el.value; });
        root.querySelectorAll('[data-param]').forEach(function (el) {
            var v = num(el.value);
            if (v == null) delete _setting.paramOverrides[el.dataset.param];
            else _setting.paramOverrides[el.dataset.param] = v;
        });
        var hf = $('cdgHolidayOff'); if (hf) _setting.isHolidayNonProduction = hf.checked;
        var bf = $('cdgBaseFrom'); if (bf) _setting.szBaselineFrom = bf.value || null;
        var bt = $('cdgBaseTo'); if (bt) _setting.szBaselineTo = bt.value || null;
    }

    function guessUnit(role, szRaw) {
        var units = (_meta.sysRoles && _meta.sysRoles[role]) || [''];
        if (!szRaw) return units[0];
        if (units.indexOf(szRaw) >= 0) return szRaw;
        var q = _meta.sysQuantities && _meta.sysQuantities[role];
        var aliases = (_meta.unitAliases && _meta.unitAliases[q]) || {};
        var key = String(szRaw).trim().replace(/[！-～]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0xFEE0); })
            .replace(/³/g, '3').replace(/²/g, '2').replace(/\s/g, '').toLowerCase();
        return aliases[key] || units[0];
    }

    function bindSetting() {
        var root = $('cdgSettingRoot');
        $('cdgSave').addEventListener('click', saveSetting);
        $('cdgFillWeekday').addEventListener('click', function () {
            collectSetting();
            _setting.aProductionSchedule = [1, 2, 3, 4, 5].map(function (d) { return { nDay: d, szStart: '08:00', szEnd: '17:00' }; });
            renderSetting();
        });
        $('cdgConfirmAll').addEventListener('click', function () {
            collectSetting();
            (_meta.parameters || []).forEach(function (p) {
                if (paramState(p.key) === 'default') _setting.aConfirmedParams.push(p.key);
            });
            renderSetting();
        });
        root.addEventListener('click', function (ev) {
            var el = ev.target.closest('button');
            if (!el) return;
            var d = el.dataset;
            if (d.sysPick) {
                collectSetting();
                if (!window.PointPicker) return;
                window.PointPicker.open({}).then(function (p) {
                    if (!p) return;
                    _pointBySid[p.sid] = _pointBySid[p.sid] || { szSid: p.sid, szName: p.name || p.sid };
                    _setting.sysPoints[d.sysPick] = { szSID: p.sid, szUnit: guessUnit(d.sysPick, p.unit), dScale: 1 };
                    renderSetting();
                });
            } else if (d.sysClear) {
                collectSetting();
                delete _setting.sysPoints[d.sysClear];
                renderSetting();
            } else if (d.schedAdd != null) {
                collectSetting();
                _setting.aProductionSchedule.push({ nDay: +d.schedAdd, szStart: '08:00', szEnd: '17:00' });
                renderSetting();
            } else if (d.schedDel != null) {
                collectSetting();
                _setting.aProductionSchedule.splice(+d.schedDel, 1);
                renderSetting();
            } else if (d.paramConfirm) {
                collectSetting();
                _setting.aConfirmedParams.push(d.paramConfirm);
                renderSetting();
            } else if (d.paramUnconfirm) {
                collectSetting();
                _setting.aConfirmedParams = _setting.aConfirmedParams.filter(function (k) { return k !== d.paramUnconfirm; });
                renderSetting();
            }
        });
    }

    function saveSetting() {
        collectSetting();
        var body = {
            dDemandPressBar: _setting.dDemandPressBar,
            dManualPrice: _setting.dManualPrice,
            sysPoints: _setting.sysPoints,
            aProductionSchedule: _setting.aProductionSchedule,
            isHolidayNonProduction: _setting.isHolidayNonProduction !== false,
            szBaselineFrom: _setting.szBaselineFrom,
            szBaselineTo: _setting.szBaselineTo,
            paramOverrides: _setting.paramOverrides,
            aConfirmedParams: _setting.aConfirmedParams
        };
        postJson(API + '/setting', body).then(function () {
            toast(t('eq.diag.ui.saved'));
            _loaded.setting = false;
            loadSetting();
        }).catch(function (err) { toast(errText(err), 'danger'); });
    }

    // ── 初始化 ────────────────────────────────────────────
    function init() {
        var root = $('cdgRoot');
        if (!root) return;
        _isEngineer = root.dataset.engineer === '1';
        document.querySelectorAll('#eqdTabs .nav-link').forEach(function (a) {
            a.addEventListener('click', function (ev) {
                ev.preventDefault();
                history.replaceState(null, '', '#' + a.dataset.tab);
                showTab(a.dataset.tab);
            });
        });
        var hash = (location.hash || '').replace('#', '');
        if (hash === 'diagnosis' || hash === 'setting') showTab(hash);
    }

    if (window.i18n && window.i18n.ready) window.i18n.ready(init);
    else document.addEventListener('DOMContentLoaded', init);
})();
