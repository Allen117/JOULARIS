// 水費報表頁邏輯 — 自來水表迴路 × 月結期別區間 → 每期用水量（m³）套台水分段累進之水費。
// 每列可展開級距明細；長條圖為每期水費。協定見 Features/WaterCostReport/Controllers/WaterCostReportController.cs。
(function () {
    'use strict';

    let g_circuits = [];
    let g_rows = [];
    let g_chart = null;

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        document.getElementById('wcrMonthStart').addEventListener('change', refreshPeriodHint);
        document.getElementById('wcrMonthEnd').addEventListener('change', refreshPeriodHint);
        if (window.i18n) {
            window.i18n.ready(() => { loadCircuits(); refreshPeriodHint(); });
        } else {
            loadCircuits();
            refreshPeriodHint();
        }
    });

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    function initDefaults() {
        const today = (window._wcrInit && window._wcrInit.today) || new Date().toISOString().slice(0, 10);
        const ym = today.slice(0, 7);
        // 預設查近 6 期（含本期）
        const d = new Date(ym + '-01T00:00:00');
        d.setMonth(d.getMonth() - 5);
        const fromYm = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
        document.getElementById('wcrMonthStart').value = fromYm;
        document.getElementById('wcrMonthEnd').value = ym;
    }

    // 顯示所選起訖期別的實際日期區間（共用月結期別 API）
    async function refreshPeriodHint() {
        const hintEl = document.getElementById('wcrPeriodHint');
        if (!hintEl) return;
        const fromYm = document.getElementById('wcrMonthStart').value;
        const toYm = document.getElementById('wcrMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { hintEl.textContent = ''; return; }
        try {
            const res = await fetch(`/WaterBillingPeriodSetting/api/range?fromYm=${encodeURIComponent(fromYm)}&toYm=${encodeURIComponent(toYm)}`);
            if (!res.ok) { hintEl.textContent = ''; return; }
            const periods = await res.json();
            if (!periods.length) { hintEl.textContent = ''; return; }
            hintEl.textContent = t('watercostreport.period.hint',
                { 0: periods[0].start, 1: periods[periods.length - 1].end });
        } catch {
            hintEl.textContent = '';
        }
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/WaterCostReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('wcrCircuit');
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('watercostreport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('watercostreport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error('[watercostreport] load circuits failed', err);
        }
    }

    function buildIndentedList(nodes) {
        const out = [];
        const byParent = new Map();
        nodes.forEach(n => {
            const k = n.parentId == null ? 'null' : String(n.parentId);
            if (!byParent.has(k)) byParent.set(k, []);
            byParent.get(k).push(n);
        });
        byParent.forEach(arr => arr.sort((a, b) => a.sortOrder - b.sortOrder));
        function walk(parentKey, depth) {
            const arr = byParent.get(parentKey) || [];
            arr.forEach(n => {
                const prefix = '  '.repeat(depth) + (depth > 0 ? '└ ' : '');
                const tag = n.sid ? t('watercostreport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('wcrCircuit').value, 10);
        if (!circuitId) { alert(t('watercostreport.alert.select_circuit')); return null; }
        const fromYm = document.getElementById('wcrMonthStart').value;
        const toYm = document.getElementById('wcrMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { alert(t('watercostreport.alert.month_order')); return null; }
        return { circuitId, fromYm, toYm };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('wcrTableBody').innerHTML =
            `<tr><td colspan="5" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('watercostreport.table.querying'))}</td></tr>`;
        document.getElementById('btnWcrExport').disabled = true;

        try {
            const res = await fetch(`/WaterCostReport/api/query?circuitId=${req.circuitId}&fromYm=${encodeURIComponent(req.fromYm)}&toYm=${encodeURIComponent(req.toYm)}`);
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            g_rows = await res.json();
            renderTable();
            renderChart();
            document.getElementById('btnWcrExport').disabled = g_rows.length === 0;
        } catch (err) {
            document.getElementById('wcrTableBody').innerHTML =
                `<tr><td colspan="5" class="text-center text-danger py-3">${escapeHtml(t('watercostreport.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    function fmtCost(v) {
        return v.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    function fmtM3(v) {
        return v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }

    function tierRangeLabel(tr) {
        return tr.to == null
            ? t('watercostreport.detail.above', { 0: tr.from })
            : `${tr.from} ~ ${tr.to}`;
    }

    function renderTable() {
        const tbody = document.getElementById('wcrTableBody');
        if (g_rows.length === 0) {
            tbody.innerHTML = `<tr><td colspan="5" class="text-center text-muted py-3">${escapeHtml(t('watercostreport.table.no_data'))}</td></tr>`;
            document.getElementById('wcrTotalCost').textContent = '--';
            document.getElementById('wcrTotalM3').textContent = '--';
            return;
        }

        let totalM3 = 0, totalCost = 0;
        const html = g_rows.map((r, i) => {
            totalM3 += r.totalM3;
            totalCost += r.totalCost;
            const staleBadge = r.isStale
                ? ` <span class="badge bg-warning text-dark" title="${escapeHtml(t('watercostreport.badge.stale_hint'))}">${escapeHtml(t('watercostreport.badge.stale'))}</span>`
                : '';
            const detailRows = (r.tiers || []).map(tr => `
                <tr>
                    <td>${escapeHtml(tierRangeLabel(tr))}</td>
                    <td class="text-end">${tr.price}</td>
                    <td class="text-end">${fmtM3(tr.sliceM3)}</td>
                    <td class="text-end">${fmtCost(tr.sliceCost)}</td>
                </tr>`).join('');
            const detail = `
                <tr class="wcr-detail-row" id="wcrDetail${i}" style="display:none;">
                    <td colspan="5" class="p-2">
                        <table class="table table-sm table-bordered mb-0 wcr-detail-table">
                            <thead class="table-light">
                                <tr>
                                    <th>${escapeHtml(t('watercostreport.detail.col_range'))}</th>
                                    <th class="text-end">${escapeHtml(t('watercostreport.detail.col_price'))}</th>
                                    <th class="text-end">${escapeHtml(t('watercostreport.detail.col_m3'))}</th>
                                    <th class="text-end">${escapeHtml(t('watercostreport.detail.col_cost'))}</th>
                                </tr>
                            </thead>
                            <tbody>${detailRows}</tbody>
                        </table>
                    </td>
                </tr>`;
            return `
                <tr>
                    <td>${escapeHtml(r.periodLabel)}${staleBadge}</td>
                    <td class="text-end">${fmtM3(r.totalM3)}</td>
                    <td>${escapeHtml(r.planName || '--')}</td>
                    <td class="text-end">${fmtCost(r.totalCost)}</td>
                    <td class="text-center">
                        <button class="btn btn-outline-secondary btn-sm py-0" onclick="window._wcr.toggleDetail(${i})" title="${escapeHtml(t('watercostreport.table.col_detail'))}">
                            <i class="fas fa-chevron-down" id="wcrDetailIcon${i}"></i>
                        </button>
                    </td>
                </tr>` + detail;
        }).join('') +
            `<tr class="er-total"><td>${escapeHtml(t('watercostreport.table.total'))}</td><td class="text-end">${fmtM3(totalM3)}</td><td></td><td class="text-end">${fmtCost(totalCost)}</td><td></td></tr>`;

        tbody.innerHTML = html;
        document.getElementById('wcrTotalCost').textContent = fmtCost(totalCost);
        document.getElementById('wcrTotalM3').textContent = fmtM3(totalM3);
    }

    function toggleDetail(idx) {
        const row = document.getElementById('wcrDetail' + idx);
        const icon = document.getElementById('wcrDetailIcon' + idx);
        if (!row) return;
        const isHidden = row.style.display === 'none';
        row.style.display = isHidden ? '' : 'none';
        if (icon) icon.className = isHidden ? 'fas fa-chevron-up' : 'fas fa-chevron-down';
    }

    function renderChart() {
        if (g_chart) { g_chart.destroy(); g_chart = null; }
        const noDataEl = document.getElementById('wcrNoData');
        const canvas = document.getElementById('wcrChart');

        if (g_rows.length === 0) {
            canvas.style.display = 'none';
            noDataEl.style.display = 'flex';
            noDataEl.innerHTML =
                `<div class="text-center text-muted">
                    <i class="fas fa-hand-holding-water fa-2x mb-2 d-block opacity-50"></i>
                    ${escapeHtml(t('watercostreport.chart.no_data'))}
                 </div>`;
            return;
        }
        canvas.style.display = '';
        noDataEl.style.display = 'none';

        const labels = g_rows.map(r => r.periodLabel);
        const values = g_rows.map(r => r.totalCost);
        const circuitName = document.getElementById('wcrCircuit').selectedOptions[0]?.textContent.trim() || '';
        const ctx = canvas.getContext('2d');
        g_chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [{
                    label: t('watercostreport.chart.dataset_label', { 0: circuitName }),
                    data: values,
                    backgroundColor: 'rgba(30, 136, 229, 0.6)',
                    borderColor: 'rgba(30, 136, 229, 1)',
                    borderWidth: 1
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: true },
                    tooltip: {
                        callbacks: {
                            // 每根 bar 額外顯示該期用水量供對照
                            afterLabel: (item) => {
                                const r = g_rows[item.dataIndex];
                                return r ? `${fmtM3(r.totalM3)} m³` : undefined;
                            }
                        }
                    }
                },
                scales: {
                    y: { beginAtZero: true, title: { display: true, text: t('watercostreport.chart.y_axis') } },
                    x: { title: { display: true, text: t('watercostreport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/WaterCostReport/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'WaterCostReport.xlsx';
            const m = cd.match(/filename\*?=(?:UTF-8'')?["']?([^"';]+)/i);
            if (m) szFileName = decodeURIComponent(m[1]);
            const blob = await res.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = szFileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        } catch (err) {
            alert(t('watercostreport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._wcr = { query, exportExcel, toggleDetail };
})();
