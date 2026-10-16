// 契約容量最佳化 — 以歷史需量回測經常契約容量（保守建議 / 最低成本點 / 成本曲線 / 逐月明細）。
// 計算全在後端（ContractCapacityService → Common ContractCapacityCalculator），本檔只負責查詢條件與呈現。
(function () {
    'use strict';

    var g_result = null;
    var g_curveChart = null;
    var g_monthlyChart = null;

    var COLOR_CUR = '#6c757d', COLOR_SAFE = '#198754', COLOR_MIN = '#fd7e14';

    document.addEventListener('DOMContentLoaded', function () {
        if (window.i18n) window.i18n.ready(init);
        else init();
    });

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    function $(id) { return document.getElementById(id); }

    // ── 初始化 ───────────────────────────────────────────

    async function init() {
        // 預設回測最近 12 個完整計費月（上個月往回 12 個月）
        var now = new Date();
        $('ccTo').value = ym(new Date(now.getFullYear(), now.getMonth() - 1, 1));
        $('ccFrom').value = ym(new Date(now.getFullYear(), now.getMonth() - 12, 1));

        try {
            var res = await fetch('/ContractCapacity/api/circuits');
            if (!res.ok) throw new Error(res.statusText);
            var circuits = await res.json();
            var sel = $('ccCircuit');
            if (circuits.length === 0) {
                sel.innerHTML = '<option value="">' + escapeHtml(t('contractcapacity.filter.no_circuit')) + '</option>';
                $('ccBtnRun').disabled = true;
                showMessage('warning', escapeHtml(t('contractcapacity.msg.no_circuit')));
                return;
            }
            sel.innerHTML = circuits.map(function (c) {
                return '<option value="' + c.nId + '">' + escapeHtml(c.szName) +
                    (c.isMainMeter ? ' \u2605' : '') + '</option>';
            }).join('');
        } catch (err) {
            console.error('contract capacity circuits load failed', err);
            showMessage('danger', escapeHtml(t('contractcapacity.msg.load_fail')));
        }
    }

    function buildQuery() {
        var circuitId = parseInt($('ccCircuit').value, 10);
        var from = $('ccFrom').value, to = $('ccTo').value;
        if (!circuitId || !from || !to) {
            alert(t('contractcapacity.err.invalid_range'));
            return null;
        }
        var maxValid = parseFloat($('ccMaxValid').value);
        return {
            circuitId: circuitId,
            from: from,
            to: to,
            roundKw: parseInt($('ccRound').value, 10) || 10,
            completenessPct: parseFloat($('ccThreshold').value) || 0,
            maxValidKw: maxValid > 0 ? maxValid : null
        };
    }

    // ── 回測 ─────────────────────────────────────────────

    async function run() {
        var q = buildQuery();
        if (!q) return;
        var btn = $('ccBtnRun');
        btn.disabled = true;
        showMessage('', '');
        try {
            var res = await fetch('/ContractCapacity/api/analyze', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(q)
            });
            if (!res.ok) throw new Error(await readError(res));
            g_result = await res.json();
            render(g_result);
        } catch (err) {
            g_result = null;
            $('ccResult').classList.add('d-none');
            $('ccBtnExport').disabled = true;
            showMessage('danger', escapeHtml(err.message));
        } finally {
            btn.disabled = false;
        }
    }

    async function readError(res) {
        var body = await res.json().catch(function () { return {}; });
        var code = body.code || 'server_error';
        return t('contractcapacity.err.' + code);
    }

    // ── 呈現 ─────────────────────────────────────────────

    function render(r) {
        renderPlanInfo(r);
        if (r.nEvaluableMonths === 0) {
            $('ccResult').classList.add('d-none');
            $('ccBtnExport').disabled = true;
            showMessage('warning', escapeHtml(t('contractcapacity.msg.no_evaluable', { 0: r.nMonths })) + reasonSummary(r));
            return;
        }
        var warnings = [];
        if (!r.current) warnings.push(t('contractcapacity.msg.no_contract'));
        if (r.isMultiplePlans) warnings.push(t('contractcapacity.msg.multiple_plans'));
        if (r.isCurrentChanged) warnings.push(t('contractcapacity.msg.contract_changed'));
        showMessage(warnings.length ? 'warning' : '', warnings.map(escapeHtml).join('<br>'));

        $('ccResult').classList.remove('d-none');
        $('ccBtnExport').disabled = false;
        renderKpis(r);
        renderCurve(r);
        renderMonthly(r);
        renderTable(r);
    }

    function renderPlanInfo(r) {
        var szPlan = r.szPlanId ? (r.szPlanName || t('tariff.plan.' + r.szPlanId)) : t('contractcapacity.msg.no_plan_short');
        var szCur = r.dCurrentKw != null ? fmt(r.dCurrentKw) + ' kW' : '—';
        $('ccPlanInfo').innerHTML = '<i class="fas fa-file-invoice-dollar me-1"></i>' + escapeHtml(szPlan) +
            ' \u00b7 ' + escapeHtml(t('contractcapacity.info.current_contract')) + ' ' + escapeHtml(szCur);
    }

    function reasonSummary(r) {
        var counts = {};
        r.months.forEach(function (m) { if (m.szReason) counts[m.szReason] = (counts[m.szReason] || 0) + 1; });
        var parts = Object.keys(counts).map(function (k) { return t('contractcapacity.reason.' + k) + ' \u00d7 ' + counts[k]; });
        return parts.length ? '<br><small>' + escapeHtml(parts.join('\u3001')) + '</small>' : '';
    }

    function savingHtml(s) {
        if (!s || s.dSaving == null) return '<span class="text-muted">' + escapeHtml(t('contractcapacity.kpi.saving_na')) + '</span>';
        var cls = s.dSaving >= 0 ? 'cc-save' : 'cc-over';
        var key = s.dSaving >= 0 ? 'contractcapacity.kpi.saving' : 'contractcapacity.kpi.extra_cost';
        return '<span class="' + cls + '">' + escapeHtml(t(key, { 0: fmt(Math.abs(s.dSaving)), 1: (Math.abs(s.dSavingPct || 0)).toFixed(1) })) + '</span>';
    }

    function renderKpis(r) {
        var nMonths = r.nEvaluableMonths;
        if (r.current) {
            $('kpiCurCost').textContent = 'NT$ ' + fmt(r.current.dTotal);
            var idle = r.current.dKw - r.dMaxDemandKw;
            $('kpiCurSub').innerHTML = escapeHtml(t('contractcapacity.kpi.current_sub', {
                0: fmt(r.current.dKw), 1: fmt(r.dMaxDemandKw), 2: nMonths
            })) + (idle > 0 ? '<br>' + escapeHtml(t('contractcapacity.kpi.idle', { 0: fmt(idle) })) : '') +
                (r.current.nOverMonths > 0 ? '<br><span class="cc-over">' + escapeHtml(t('contractcapacity.kpi.over_months', {
                    0: r.current.nOverMonths, 1: fmt(r.current.dPenalty)
                })) + '</span>' : '');
        } else {
            $('kpiCurCost').textContent = '--';
            $('kpiCurSub').textContent = t('contractcapacity.kpi.no_contract');
        }

        if (r.safe) {
            $('kpiSafeC').textContent = fmt(r.safe.dKw) + ' kW';
            $('kpiSafeSub').innerHTML = savingHtml(r.safe) + '<br><span class="text-muted">' +
                escapeHtml(t('contractcapacity.kpi.safe_sub', { 0: fmt(r.dMaxDemandKw), 1: r.nRoundKw })) + '</span>';
        }
        if (r.optimum) {
            $('kpiMinC').textContent = fmt(r.optimum.dKw) + ' kW';
            $('kpiMinSub').innerHTML = savingHtml(r.optimum) + '<br>' + (r.optimum.nOverMonths > 0
                ? '<span class="cc-over">' + escapeHtml(t('contractcapacity.kpi.over_months', { 0: r.optimum.nOverMonths, 1: fmt(r.optimum.dPenalty) })) + '</span>'
                : '<span class="text-muted">' + escapeHtml(t('contractcapacity.kpi.no_over')) + '</span>');
        }

        var all = r.months;
        var avg = all.reduce(function (a, m) { return a + m.dCompletenessPct; }, 0) / (all.length || 1);
        var worst = all.reduce(function (w, m) { return !w || m.dCompletenessPct < w.dCompletenessPct ? m : w; }, null);
        $('kpiDq').textContent = avg.toFixed(1) + '%';
        $('kpiDqSub').innerHTML = escapeHtml(t('contractcapacity.kpi.quality_sub', {
            0: r.nEvaluableMonths, 1: r.nMonths, 2: worst ? worst.dCompletenessPct.toFixed(1) : '--', 3: worst ? worst.szLabel : ''
        })) + '<br>' + escapeHtml(t('contractcapacity.kpi.quality_threshold', { 0: r.dCompletenessPct }));
    }

    // 垂直標線 plugin（成本曲線上標現行 / 保守 / 最低成本）
    var vlinePlugin = {
        id: 'ccVlines',
        afterDatasetsDraw: function (chart, args, opts) {
            var x = chart.scales.x, a = chart.chartArea, ctx = chart.ctx;
            (opts.lines || []).forEach(function (l) {
                var px = x.getPixelForValue(l.v);
                if (px < a.left || px > a.right) return;
                ctx.save();
                ctx.strokeStyle = l.color; ctx.lineWidth = 2; ctx.setLineDash(l.dash || []);
                ctx.beginPath(); ctx.moveTo(px, a.top); ctx.lineTo(px, a.bottom); ctx.stroke(); ctx.setLineDash([]);
                ctx.fillStyle = l.color; ctx.font = 'bold 12px "Segoe UI", sans-serif';
                ctx.textAlign = l.align || 'left';
                ctx.fillText(l.label, px + (l.align === 'right' ? -6 : 6), a.top + 14 + (l.dy || 0));
                ctx.restore();
            });
        }
    };

    function renderCurve(r) {
        if (g_curveChart) { g_curveChart.destroy(); g_curveChart = null; }
        if (r.nFlatLowKw != null) {
            $('ccFlatNote').textContent = t('contractcapacity.chart.flat_note', { 0: fmt(r.nFlatLowKw), 1: fmt(r.nFlatHighKw) });
        } else {
            $('ccFlatNote').textContent = '';
        }
        var pts = r.curve || [];
        if (pts.length === 0) return;
        var lines = [];
        if (r.optimum) lines.push({ v: r.optimum.dKw, color: COLOR_MIN, label: t('contractcapacity.scenario.optimum') + ' ' + fmt(r.optimum.dKw), align: 'right' });
        if (r.safe) lines.push({ v: r.safe.dKw, color: COLOR_SAFE, label: t('contractcapacity.scenario.safe') + ' ' + fmt(r.safe.dKw), dash: [6, 4], dy: 18, align: 'right' });
        if (r.current) lines.push({ v: r.current.dKw, color: COLOR_CUR, label: t('contractcapacity.scenario.current') + ' ' + fmt(r.current.dKw), dash: [2, 3] });

        var minTotal = Math.min.apply(null, pts.map(function (p) { return p.dBase + p.dPenalty; }));
        g_curveChart = new Chart($('ccCurve'), {
            type: 'line',
            data: {
                datasets: [
                    {
                        label: t('contractcapacity.chart.base_fee'),
                        data: pts.map(function (p) { return { x: p.dKw, y: p.dBase / 1e4 }; }),
                        fill: 'origin', backgroundColor: 'rgba(25,135,84,.25)', borderColor: COLOR_SAFE,
                        pointRadius: 0, stack: 's', borderWidth: 1.5
                    },
                    {
                        label: t('contractcapacity.chart.penalty'),
                        data: pts.map(function (p) { return { x: p.dKw, y: p.dPenalty / 1e4 }; }),
                        fill: '-1', backgroundColor: 'rgba(220,53,69,.25)', borderColor: '#dc3545',
                        pointRadius: 0, stack: 's', borderWidth: 1.5
                    }
                ]
            },
            options: {
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                scales: {
                    x: { type: 'linear', min: pts[0].dKw, max: pts[pts.length - 1].dKw, title: { display: true, text: t('contractcapacity.chart.x_contract') } },
                    y: { stacked: true, min: Math.max(0, Math.floor(minTotal / 1e4 * 0.85)), title: { display: true, text: t('contractcapacity.chart.y_cost') } }
                },
                plugins: {
                    legend: { position: 'bottom' },
                    tooltip: {
                        callbacks: {
                            title: function (items) { return items.length ? fmt(items[0].parsed.x) + ' kW' : ''; },
                            label: function (ctx) { return ctx.dataset.label + ': ' + ctx.parsed.y.toFixed(1); }
                        }
                    },
                    ccVlines: { lines: lines }
                }
            },
            plugins: [vlinePlugin]
        });
    }

    function renderMonthly(r) {
        if (g_monthlyChart) { g_monthlyChart.destroy(); g_monthlyChart = null; }
        var months = r.months;
        var datasets = [{
            type: 'bar', label: t('contractcapacity.chart.max_demand'), order: 2,
            data: months.map(function (m) { return m.dMaxDemandKw; }),
            backgroundColor: months.map(function (m) {
                if (!m.isEvaluable) return 'rgba(173,181,189,.6)';
                return m.dSummerShare > 0 ? 'rgba(13,110,253,.75)' : 'rgba(13,110,253,.4)';
            })
        }];
        if (r.current) {
            datasets.push({
                type: 'line', label: t('contractcapacity.scenario.current'), order: 1, stepped: 'middle',
                data: months.map(function (m) { return m.dCurrentKw; }),
                borderColor: COLOR_CUR, borderDash: [2, 3], pointRadius: 0, borderWidth: 2
            });
        }
        if (r.safe) datasets.push(flatLine(t('contractcapacity.scenario.safe') + ' ' + fmt(r.safe.dKw), r.safe.dKw, COLOR_SAFE, [6, 4], months.length));
        if (r.optimum) datasets.push(flatLine(t('contractcapacity.scenario.optimum') + ' ' + fmt(r.optimum.dKw), r.optimum.dKw, COLOR_MIN, [], months.length));

        g_monthlyChart = new Chart($('ccMonthly'), {
            data: { labels: months.map(function (m) { return m.szLabel; }), datasets: datasets },
            options: {
                maintainAspectRatio: false,
                scales: { y: { beginAtZero: false, title: { display: true, text: 'kW' } } },
                plugins: { legend: { position: 'bottom' } }
            }
        });
    }

    function flatLine(label, v, color, dash, n) {
        var data = [];
        for (var i = 0; i < n; i++) data.push(v);
        return { type: 'line', label: label, order: 1, data: data, borderColor: color, borderDash: dash, pointRadius: 0, borderWidth: 2 };
    }

    function renderTable(r) {
        $('thCur').textContent = t('contractcapacity.scenario.current') + (r.current ? ' ' + fmt(r.current.dKw) + ' kW' : '');
        $('thSafe').textContent = t('contractcapacity.scenario.safe') + (r.safe ? ' ' + fmt(r.safe.dKw) + ' kW' : '');
        $('thMin').textContent = t('contractcapacity.scenario.optimum') + (r.optimum ? ' ' + fmt(r.optimum.dKw) + ' kW' : '');

        $('ccBody').innerHTML = r.months.map(function (m) {
            var season = m.dSummerShare >= 0.999
                ? '<span class="badge bg-warning text-dark ms-1">' + escapeHtml(t('contractcapacity.table.summer')) + '</span>'
                : m.dSummerShare > 0
                    ? '<span class="badge bg-light text-dark border ms-1">' + escapeHtml(t('contractcapacity.table.cross_season')) + '</span>'
                    : '';
            var head = '<td>' + escapeHtml(m.szLabel) + season + '</td>' +
                '<td class="text-end">' + (m.dRegularPrice ? m.dRegularPrice.toFixed(1) : '—') + '</td>' +
                '<td class="text-end fw-semibold">' + (m.dMaxDemandKw != null ? fmt(m.dMaxDemandKw, 1) : '—') + '</td>' +
                '<td>' + (m.szPeakAt ? escapeHtml(m.szPeakAt.substring(5)) : '—') + '</td>' +
                '<td class="text-end">' + m.dCompletenessPct.toFixed(1) + '%' +
                (m.nExcludedWindows > 0 ? '<br><small class="text-muted">' + escapeHtml(t('contractcapacity.table.excluded', { 0: m.nExcludedWindows })) + '</small>' : '') + '</td>';
            if (!m.isEvaluable) {
                return '<tr class="cc-row-na">' + head + '<td colspan="6" class="text-center">' +
                    '<span class="badge bg-secondary">' + escapeHtml(t('contractcapacity.reason.' + m.szReason)) + '</span></td></tr>';
            }
            return '<tr>' + head + cells(m.cur) + cells(m.safe) + cells(m.optimum) + '</tr>';
        }).join('');

        function foot(s) {
            if (!s) return '<td colspan="2" class="text-end text-muted">—</td>';
            return '<td class="text-end">' + (s.nOverMonths ? escapeHtml(t('contractcapacity.table.n_months', { 0: s.nOverMonths })) : '—') +
                '</td><td class="text-end">' + fmt(s.dTotal) + '</td>';
        }
        function save(s) {
            if (!s || s.dSaving == null) return '<td colspan="2" class="text-end text-muted">—</td>';
            return '<td colspan="2" class="text-end ' + (s.dSaving >= 0 ? 'cc-save' : 'cc-over') + '">' + fmt(s.dSaving) + '</td>';
        }
        $('ccFoot').innerHTML =
            '<tr><td colspan="5">' + escapeHtml(t('contractcapacity.table.total', { 0: r.nEvaluableMonths })) + '</td>' +
            foot(r.current) + foot(r.safe) + foot(r.optimum) + '</tr>' +
            '<tr><td colspan="5">' + escapeHtml(t('contractcapacity.table.saving')) + '</td>' +
            '<td colspan="2" class="text-end text-muted">—</td>' + save(r.safe) + save(r.optimum) + '</tr>';
    }

    function cells(c) {
        if (!c) return '<td class="text-end text-muted">—</td><td class="text-end text-muted">—</td>';
        var over = c.dOverKw > 0 ? '<span class="cc-over">' + fmt(c.dOverKw, 1) + '</span>' : '<span class="text-muted">—</span>';
        var tot = fmt(c.dTotal) + (c.dPenalty > 0 ? '<br><small class="cc-over">' + escapeHtml(t('contractcapacity.table.incl_penalty', { 0: fmt(c.dPenalty) })) + '</small>' : '');
        return '<td class="text-end">' + over + '</td><td class="text-end">' + tot + '</td>';
    }

    // ── 匯出 ─────────────────────────────────────────────

    async function exportExcel() {
        var q = buildQuery();
        if (!q) return;
        try {
            var res = await fetch('/ContractCapacity/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(q)
            });
            if (!res.ok) throw new Error(await readError(res));
            var cd = res.headers.get('Content-Disposition') || '';
            var szFileName = 'ContractCapacity.xlsx';
            var m = cd.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)/i);
            if (m) szFileName = decodeURIComponent(m[1]);
            var blob = await res.blob();
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = szFileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        } catch (err) {
            alert(t('contractcapacity.msg.export_fail', { 0: err.message }));
        }
    }

    // ── 工具 ─────────────────────────────────────────────

    // html 由呼叫端負責跳脫（i18n 字串 / escapeHtml 過的內容）
    function showMessage(level, html) {
        var box = $('ccMessage');
        if (!level || !html) { box.innerHTML = ''; return; }
        box.innerHTML = '<div class="alert alert-' + level + ' py-2 small">' + html + '</div>';
    }

    function ym(d) {
        return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0');
    }

    function fmt(v, digits) {
        if (v == null || isNaN(v)) return '--';
        var d = digits || 0;
        return Number(v).toLocaleString(undefined, { minimumFractionDigits: d, maximumFractionDigits: d });
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    window._cc = { run: run, exportExcel: exportExcel };
})();
