/* 設備效率診斷頁 — /ChillerEfficiency 與 /CompressorEfficiency 共用（讀根元素 data-kind）。
   上半「即時」：GET /EMS/api/equipment/realtime?kind=，30 秒輪詢，每台一張小卡（主指標 + 評級色 + 輔助指標）
   下半「累計」：GET /EMS/api/equipment/period?kind=&granularity=&pivot=（曆制：日→hour、月→day、年→month）
     tiles（ΣkWh / Σ產出 / 期間平均效率 = ΣkWh ÷ Σ產出 / 運轉時數 / 平均負載率）+ 效率趨勢（每台一條 + 全場加權線）
     + 負載率分佈（≤25 / ≤50 / ≤75 / >75，對應 AHRI 550/590 IPLV 四點）+ 設備排名 + 覆蓋率警示
   兩類設備差別只在 CONFIG（比照 ems-circuit.js 用 data-kind 一支 JS 吃多種能源）。 */
(function () {
    'use strict';

    var REALTIME_MS = 30000;
    var PERIOD_TODAY_MS = 300000;   // 日檢視看今天時 5 分鐘刷新（hourly 聚合每小時才更新一次）

    var CONFIG = {
        chiller: {
            primaryUnit: 'kW/RT', primaryDigits: 3,
            secondaryKey: 'eq.metric.cop', secondaryUnit: '', secondaryDigits: 2,
            tertiaryKey: 'eq.metric.eer', tertiaryUnit: '', tertiaryDigits: 1,
            outputKey: 'eq.metric.cooling', outputUnit: 'RT', outputPeriodUnit: 'RT·h', outputDigits: 1,
            aux: [
                ['loadPct', 'eq.metric.load', '%', 0],
                ['chwDeltaT', 'eq.metric.chw_dt', '°C', 1],
                ['cdwDeltaT', 'eq.metric.cdw_dt', '°C', 1],
                ['evapApproach', 'eq.metric.evap_approach', '°C', 1],
                ['condApproach', 'eq.metric.cond_approach', '°C', 1]
            ]
        },
        compressor: {
            primaryUnit: 'kW/(m³/min)', primaryDigits: 2,
            secondaryKey: 'eq.metric.kw_per_100cfm', secondaryUnit: 'kW/100cfm', secondaryDigits: 2,
            tertiaryKey: 'eq.metric.specific_energy', tertiaryUnit: 'kWh/m³', tertiaryDigits: 3,
            outputKey: 'eq.metric.airflow', outputUnit: 'm³/min', outputPeriodUnit: 'm³', outputDigits: 1,
            aux: [
                ['loadPct', 'eq.metric.load', '%', 0],
                ['pressBar', 'eq.metric.press', 'bar', 2],
                ['tempC', 'eq.metric.disch_temp', '°C', 1]
            ]
        }
    };

    var LINE_COLORS = ['#0288d1', '#fb8c00', '#8e24aa', '#e53935', '#00acc1', '#6d4c41', '#ec407a', '#5c6bc0', '#c0ca33', '#26a69a'];

    var _kind = 'chiller';
    var _cfg = CONFIG.chiller;
    var _gran = 'hour';
    var _rtTimer = null, _periodTimer = null;
    var _trendChart = null, _loadChart = null;
    var _hasDevices = null;

    // ── 工具 ──────────────────────────────────────────────
    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }
    function $(id) { return document.getElementById(id); }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function fmt(v, d) {
        if (v == null || isNaN(v)) return '--';
        return Number(v).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
    }
    function pad2(n) { return n < 10 ? '0' + n : String(n); }
    function todayStr() { var d = new Date(); return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); }
    function monthStr() { var d = new Date(); return d.getFullYear() + '-' + pad2(d.getMonth() + 1); }
    function timeStr(s) {
        if (!s) return '';
        var d = new Date(s);
        return isNaN(d) ? '' : pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
    }
    function gradeText(g) { return t('eq.grade.' + g); }
    function gradeBadge(g) { return '<span class="badge eq-grade-badge eq-grade-' + esc(g) + '">' + esc(gradeText(g)) + '</span>'; }

    // ── 即時 ──────────────────────────────────────────────
    function loadRealtime() {
        return fetch('/EMS/api/equipment/realtime?kind=' + _kind, { credentials: 'same-origin' })
            .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
            .then(renderRealtime)
            .catch(function (err) {
                $('eqdRealtime').innerHTML = '<div class="text-danger small p-2">' + esc(t('eq.diag.load_error')) + ' ' + esc(err.message) + '</div>';
            });
    }

    function renderRealtime(data) {
        var items = (data && data.items) || [];
        var isFirst = _hasDevices === null;
        _hasDevices = items.length > 0;
        $('eqdEmpty').style.display = _hasDevices ? 'none' : '';
        $('eqdMain').style.display = _hasDevices ? '' : 'none';
        if (!_hasDevices) return;
        if (isFirst) loadPeriod();
        $('eqdRealtimeTime').textContent = t('eq.diag.updated_at', { time: timeStr(data.serverTime) });
        $('eqdRealtime').innerHTML = items.map(rtCardHtml).join('');
    }

    function runLamp(isRunning) {
        var cls = isRunning === true ? 'is-on' : isRunning === false ? 'is-off' : 'is-unknown';
        var key = isRunning === true ? 'eq.diag.running' : isRunning === false ? 'eq.diag.stopped' : 'eq.diag.run_unknown';
        return '<span class="eq-run-lamp ' + cls + '" title="' + esc(t(key)) + '"></span>';
    }

    function rtCardHtml(it) {
        var c = _cfg;
        var aux = c.aux.map(function (a) {
            var v = it[a[0]];
            if (v == null) return '';
            return '<div class="eq-aux"><span class="text-muted">' + esc(t(a[1])) + '</span><span>' + fmt(v, a[3]) + ' ' + esc(a[2]) + '</span></div>';
        }).join('');
        if (_kind === 'compressor' && it.isLoaded != null) {
            aux += '<div class="eq-aux"><span class="text-muted">' + esc(t('eq.metric.loaded_state')) + '</span><span>' +
                esc(t(it.isLoaded ? 'eq.diag.loaded' : 'eq.diag.unloaded')) + '</span></div>';
        }
        var szSecondary = it.secondary != null
            ? esc(t(c.secondaryKey)) + ' ' + fmt(it.secondary, c.secondaryDigits) + (c.secondaryUnit ? ' ' + esc(c.secondaryUnit) : '')
            : '';
        var szSource = (_kind === 'chiller' && it.outputSource === 'flow' && it.rtPoint != null)
            ? '<div class="small text-muted">' + esc(t('eq.diag.rt_point_compare', { v: fmt(it.rtPoint, 1) })) + '</div>' : '';
        return '<div class="eq-rt-card eq-grade-' + esc(it.grade) + '">' +
            '<div class="eq-rt-head">' + runLamp(it.isRunning) + '<span class="eq-rt-name" title="' + esc(it.name) + '">' + esc(it.name) + '</span>' + gradeBadge(it.grade) + '</div>' +
            '<div class="eq-rt-primary"><span class="eq-rt-value">' + fmt(it.primary, c.primaryDigits) + '</span><span class="eq-rt-unit">' + esc(c.primaryUnit) + '</span></div>' +
            '<div class="eq-rt-sub small">' + szSecondary + '</div>' +
            '<div class="eq-aux-list">' +
              '<div class="eq-aux"><span class="text-muted">' + esc(t('eq.metric.power')) + '</span><span>' + fmt(it.kw, 1) + ' kW</span></div>' +
              '<div class="eq-aux"><span class="text-muted">' + esc(t(c.outputKey)) + '</span><span>' + fmt(it.output, c.outputDigits) + ' ' + esc(c.outputUnit) + '</span></div>' +
              aux +
            '</div>' + szSource +
            '<div class="eq-rt-foot small text-muted">' + (it.lastUpdate ? esc(timeStr(it.lastUpdate)) : esc(t('eq.diag.no_data'))) +
              (it.good != null ? '<span class="ms-auto">' + esc(t('eq.diag.threshold', { good: it.good, fair: it.fair })) + '</span>' : '') + '</div>' +
        '</div>';
    }

    // ── 期間 ──────────────────────────────────────────────
    function currentPivot() {
        if (_gran === 'hour') return $('eqdPivotDate').value || todayStr();
        if (_gran === 'day') return $('eqdPivotMonth').value || monthStr();
        return $('eqdPivotYear').value || String(new Date().getFullYear());
    }

    function loadPeriod() {
        if (!_hasDevices) return Promise.resolve();
        var url = '/EMS/api/equipment/period?kind=' + _kind + '&granularity=' + _gran + '&pivot=' + encodeURIComponent(currentPivot());
        return fetch(url, { credentials: 'same-origin' })
            .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
            .then(renderPeriod)
            .catch(function (err) {
                $('eqdTiles').innerHTML = '<div class="text-danger small p-2">' + esc(t('eq.diag.load_error')) + ' ' + esc(err.message) + '</div>';
            });
    }

    function tile(szLabel, szValue, szUnit, szExtra) {
        return '<div class="eq-tile"><div class="eq-tile-label">' + esc(szLabel) + '</div>' +
            '<div class="eq-tile-value">' + szValue + (szUnit ? '<span class="eq-tile-unit">' + esc(szUnit) + '</span>' : '') + '</div>' +
            (szExtra ? '<div class="eq-tile-extra small text-muted">' + szExtra + '</div>' : '') + '</div>';
    }

    function renderPeriod(d) {
        var c = _cfg, tot = d.totals || {};
        // 覆蓋率警示
        var cov = d.coverage || {};
        var warn = $('eqdCoverage');
        if (cov.isBelowThreshold) {
            warn.style.display = '';
            warn.textContent = t('eq.diag.coverage_warn', { pct: fmt(cov.coveragePct, 1), name: cov.worstName || '', missing: cov.missingHours, threshold: cov.thresholdPct });
        } else warn.style.display = 'none';

        var szPrimaryExtra = tot.secondary != null
            ? esc(t(c.secondaryKey)) + ' ' + fmt(tot.secondary, c.secondaryDigits) + (c.secondaryUnit ? ' ' + esc(c.secondaryUnit) : '')
              + (tot.tertiary != null ? ' · ' + esc(t(c.tertiaryKey)) + ' ' + fmt(tot.tertiary, c.tertiaryDigits) + (c.tertiaryUnit ? ' ' + esc(c.tertiaryUnit) : '') : '')
            : '';
        var html =
            tile(t('eq.diag.tile_primary'), fmt(tot.primary, c.primaryDigits), c.primaryUnit, szPrimaryExtra) +
            tile(t('eq.diag.tile_kwh'), fmt(tot.kwh, 0), 'kWh') +
            tile(t('eq.diag.tile_output', { name: t(c.outputKey) }), fmt(tot.output, 0), c.outputPeriodUnit) +
            tile(t('eq.diag.tile_run_hours'), fmt(tot.runHours, 1), 'h') +
            tile(t('eq.diag.tile_avg_load'), fmt(tot.avgLoadPct, 0), '%');
        if (_kind === 'compressor') {
            html += tile(t('eq.diag.tile_loaded_ratio'), fmt(tot.loadedRatio == null ? null : tot.loadedRatio * 100, 0), '%') +
                    tile(t('eq.diag.tile_avg_press'), fmt(tot.avgPressure, 2), 'bar');
        }
        $('eqdTiles').innerHTML = html;

        renderTrend(d);
        renderLoadBins(d);
        renderRanking(d);
    }

    function renderTrend(d) {
        var ctx = $('eqdTrendChart');
        if (!ctx || typeof Chart === 'undefined') return;
        var datasets = (d.series || []).map(function (s, i) {
            var col = LINE_COLORS[i % LINE_COLORS.length];
            return { label: s.name, data: s.values, borderColor: col, backgroundColor: col, borderWidth: 1.5, pointRadius: 2, spanGaps: false, tension: 0.2 };
        });
        datasets.push({
            label: t('eq.diag.fleet'), data: d.fleet, borderColor: '#2e7d32', backgroundColor: '#2e7d32',
            borderWidth: 3, borderDash: [6, 4], pointRadius: 0, spanGaps: false, tension: 0.2
        });
        if (_trendChart) _trendChart.destroy();
        _trendChart = new Chart(ctx, {
            type: 'line',
            data: { labels: d.labels, datasets: datasets },
            options: {
                responsive: true, maintainAspectRatio: false, animation: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { position: 'bottom', labels: { boxWidth: 12, font: { size: 11 } } },
                    tooltip: { callbacks: { label: function (it) { return it.dataset.label + ': ' + fmt(it.parsed.y, _cfg.primaryDigits) + ' ' + _cfg.primaryUnit; } } }
                },
                scales: { y: { beginAtZero: false, title: { display: true, text: _cfg.primaryUnit } } }
            }
        });
    }

    function renderLoadBins(d) {
        var ctx = $('eqdLoadChart');
        if (!ctx || typeof Chart === 'undefined') return;
        var bins = d.loadBins || [];
        var labels = ['≤ 25%', '25–50%', '50–75%', '> 75%'];
        if (_loadChart) _loadChart.destroy();
        _loadChart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [
                    { label: t('eq.diag.bin_kwh_pct'), data: bins.map(function (b) { return b.kwhPct; }), backgroundColor: '#fb8c00' },
                    { label: t('eq.diag.bin_output_pct', { name: t(_cfg.outputKey) }), data: bins.map(function (b) { return b.outputPct; }), backgroundColor: '#0288d1' }
                ]
            },
            options: {
                responsive: true, maintainAspectRatio: false, animation: false,
                plugins: {
                    legend: { position: 'bottom', labels: { boxWidth: 12, font: { size: 11 } } },
                    tooltip: {
                        callbacks: {
                            label: function (it) { return it.dataset.label + ': ' + fmt(it.parsed.y, 1) + '%'; },
                            afterBody: function (items) {
                                var b = bins[items[0].dataIndex] || {};
                                return [t('eq.diag.bin_hours', { h: fmt(b.hours, 1) }),
                                        t('eq.diag.bin_efficiency', { v: fmt(b.primary, _cfg.primaryDigits), unit: _cfg.primaryUnit })];
                            }
                        }
                    }
                },
                scales: { y: { beginAtZero: true, max: 100, ticks: { callback: function (v) { return v + '%'; } } } }
            }
        });
    }

    function renderRanking(d) {
        var c = _cfg;
        var rows = (d.ranking || []).map(function (r, i) {
            return '<tr>' +
                '<td class="text-muted">' + (r.primary != null ? (i + 1) : '') + '</td>' +
                '<td class="fw-semibold">' + esc(r.name) + '</td>' +
                '<td class="text-end">' + fmt(r.primary, c.primaryDigits) + '</td>' +
                '<td>' + gradeBadge(r.grade) + '</td>' +
                '<td class="text-end">' + fmt(r.secondary, c.secondaryDigits) + '</td>' +
                '<td class="text-end">' + fmt(r.kwh, 0) + '</td>' +
                '<td class="text-end">' + fmt(r.output, 0) + '</td>' +
                '<td class="text-end">' + fmt(r.runHours, 1) + '</td>' +
                '<td class="text-end">' + fmt(r.avgLoadPct, 0) + '</td>' +
                '<td class="text-end small text-muted">' + esc(r.good) + ' / ' + esc(r.fair) + '</td>' +
                '<td class="text-end' + (r.coveragePct < ((d.coverage || {}).thresholdPct || 90) ? ' text-warning fw-semibold' : '') + '">' + fmt(r.coveragePct, 0) + '%</td>' +
            '</tr>';
        }).join('');
        $('eqdRanking').innerHTML =
            '<table class="table table-sm table-hover align-middle mb-0 eq-rank-table"><thead><tr>' +
              '<th style="width:36px;">#</th>' +
              '<th>' + esc(t('eq.diag.col_name')) + '</th>' +
              '<th class="text-end">' + esc(c.primaryUnit) + '</th>' +
              '<th>' + esc(t('eq.diag.col_grade')) + '</th>' +
              '<th class="text-end">' + esc(t(c.secondaryKey)) + '</th>' +
              '<th class="text-end">kWh</th>' +
              '<th class="text-end">' + esc(c.outputPeriodUnit) + '</th>' +
              '<th class="text-end">' + esc(t('eq.diag.col_run_hours')) + '</th>' +
              '<th class="text-end">' + esc(t('eq.diag.col_avg_load')) + '</th>' +
              '<th class="text-end">' + esc(t('eq.diag.col_threshold')) + '</th>' +
              '<th class="text-end">' + esc(t('eq.diag.col_coverage')) + '</th>' +
            '</tr></thead><tbody>' + rows + '</tbody></table>';
    }

    // ── 粒度 / pivot ──────────────────────────────────────
    function setupGran() {
        $('eqdPivotDate').value = todayStr();
        $('eqdPivotMonth').value = monthStr();
        $('eqdPivotYear').value = String(new Date().getFullYear());
        var inputs = { hour: $('eqdPivotDate'), day: $('eqdPivotMonth'), month: $('eqdPivotYear') };
        document.querySelectorAll('#eqdGranGroup .ems-gran-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                document.querySelectorAll('#eqdGranGroup .ems-gran-btn').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
                _gran = btn.dataset.gran;
                Object.keys(inputs).forEach(function (k) { inputs[k].style.display = k === _gran ? '' : 'none'; });
                loadPeriod();
            });
        });
        Object.keys(inputs).forEach(function (k) { inputs[k].addEventListener('change', loadPeriod); });
    }

    function init() {
        var root = $('eqdRoot');
        if (!root) return;
        _kind = root.dataset.kind === 'compressor' ? 'compressor' : 'chiller';
        _cfg = CONFIG[_kind];
        setupGran();
        loadRealtime();
        _rtTimer = setInterval(loadRealtime, REALTIME_MS);
        _periodTimer = setInterval(function () {
            if (_gran === 'hour' && currentPivot() === todayStr()) loadPeriod();
        }, PERIOD_TODAY_MS);
    }

    if (window.i18n && window.i18n.ready) window.i18n.ready(init);
    else document.addEventListener('DOMContentLoaded', init);
})();
