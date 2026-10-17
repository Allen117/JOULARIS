// 氣費報表頁邏輯 — 氣表迴路 × 月結期別區間 → 每期用氣量（m³）套天然氣分段累進之氣費。
// 每列可展開級距明細；長條圖為每期氣費。已刪除（兩月一期）的期別不會出現。
// 協定見 Features/GasCostReport/Controllers/GasCostReportController.cs。
(function () {
    'use strict';

    let g_circuits = [];
    let g_rows = [];
    let g_chart = null;

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        document.getElementById('gcrMonthStart').addEventListener('change', refreshPeriodHint);
        document.getElementById('gcrMonthEnd').addEventListener('change', refreshPeriodHint);
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
        const today = (window._gcrInit && window._gcrInit.today) || new Date().toISOString().slice(0, 10);
        const ym = today.slice(0, 7);
        // 預設查近 12 個月（兩月一期時約 6 期）
        const d = new Date(ym + '-01T00:00:00');
        d.setMonth(d.getMonth() - 11);
        const fromYm = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
        document.getElementById('gcrMonthStart').value = fromYm;
        document.getElementById('gcrMonthEnd').value = ym;
    }

    // 顯示所選起訖期別的實際日期區間 + 實際期數（氣費期別可為兩月一期）
    async function refreshPeriodHint() {
        const hintEl = document.getElementById('gcrPeriodHint');
        if (!hintEl) return;
        const fromYm = document.getElementById('gcrMonthStart').value;
        const toYm = document.getElementById('gcrMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { hintEl.textContent = ''; return; }
        try {
            const res = await fetch(`/GasBillingPeriodSetting/api/range?fromYm=${encodeURIComponent(fromYm)}&toYm=${encodeURIComponent(toYm)}`);
            if (!res.ok) { hintEl.textContent = ''; return; }
            const periods = await res.json();
            if (!periods.length) { hintEl.textContent = ''; return; }
            hintEl.textContent = t('gascostreport.period.hint',
                { 0: periods[0].start, 1: periods[periods.length - 1].end, 2: periods.length });
        } catch {
            hintEl.textContent = '';
        }
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/GasCostReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('gcrCircuit');
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('gascostreport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('gascostreport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error('[gascostreport] load circuits failed', err);
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
                const tag = n.sid ? t('gascostreport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('gcrCircuit').value, 10);
        if (!circuitId) { alert(t('gascostreport.alert.select_circuit')); return null; }
        const fromYm = document.getElementById('gcrMonthStart').value;
        const toYm = document.getElementById('gcrMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { alert(t('gascostreport.alert.month_order')); return null; }
        return { circuitId, fromYm, toYm };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('gcrTableBody').innerHTML =
            `<tr><td colspan="5" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('gascostreport.table.querying'))}</td></tr>`;
        document.getElementById('btnGcrExport').disabled = true;

        try {
            const res = await fetch(`/GasCostReport/api/query?circuitId=${req.circuitId}&fromYm=${encodeURIComponent(req.fromYm)}&toYm=${encodeURIComponent(req.toYm)}`);
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            g_rows = await res.json();
            renderTable();
            renderChart();
            document.getElementById('btnGcrExport').disabled = g_rows.length === 0;
        } catch (err) {
            document.getElementById('gcrTableBody').innerHTML =
                `<tr><td colspan="5" class="text-center text-danger py-3">${escapeHtml(t('gascostreport.alert.query_failed', { 0: err.message }))}</td></tr>`;
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
            ? t('gascostreport.detail.above', { 0: tr.from })
            : `${tr.from} ~ ${tr.to}`;
    }

    function renderTable() {
        const tbody = document.getElementById('gcrTableBody');
        if (g_rows.length === 0) {
            tbody.innerHTML = `<tr><td colspan="5" class="text-center text-muted py-3">${escapeHtml(t('gascostreport.table.no_data'))}</td></tr>`;
            document.getElementById('gcrTotalCost').textContent = '--';
            document.getElementById('gcrTotalM3').textContent = '--';
            return;
        }

        let totalM3 = 0, totalCost = 0;
        const html = g_rows.map((r, i) => {
            totalM3 += r.totalM3;
            totalCost += r.totalCost;
            const staleBadge = r.isStale
                ? ` <span class="badge bg-warning text-dark" title="${escapeHtml(t('gascostreport.badge.stale_hint'))}">${escapeHtml(t('gascostreport.badge.stale'))}</span>`
                : '';
            const detailRows = (r.tiers || []).map(tr => `
                <tr>
                    <td>${escapeHtml(tierRangeLabel(tr))}</td>
                    <td class="text-end">${tr.price}</td>
                    <td class="text-end">${fmtM3(tr.sliceM3)}</td>
                    <td class="text-end">${fmtCost(tr.sliceCost)}</td>
                </tr>`).join('');
            const detail = `
                <tr class="gcr-detail-row" id="gcrDetail${i}" style="display:none;">
                    <td colspan="5" class="p-2">
                        <table class="table table-sm table-bordered mb-0 gcr-detail-table">
                            <thead class="table-light">
                                <tr>
                                    <th>${escapeHtml(t('gascostreport.detail.col_range'))}</th>
                                    <th class="text-end">${escapeHtml(t('gascostreport.detail.col_price'))}</th>
                                    <th class="text-end">${escapeHtml(t('gascostreport.detail.col_m3'))}</th>
                                    <th class="text-end">${escapeHtml(t('gascostreport.detail.col_cost'))}</th>
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
                        <button class="btn btn-outline-secondary btn-sm py-0" onclick="window._gcr.toggleDetail(${i})" title="${escapeHtml(t('gascostreport.table.col_detail'))}">
                            <i class="fas fa-chevron-down" id="gcrDetailIcon${i}"></i>
                        </button>
                    </td>
                </tr>` + detail;
        }).join('') +
            `<tr class="er-total"><td>${escapeHtml(t('gascostreport.table.total'))}</td><td class="text-end">${fmtM3(totalM3)}</td><td></td><td class="text-end">${fmtCost(totalCost)}</td><td></td></tr>`;

        tbody.innerHTML = html;
        document.getElementById('gcrTotalCost').textContent = fmtCost(totalCost);
        document.getElementById('gcrTotalM3').textContent = fmtM3(totalM3);
    }

    function toggleDetail(idx) {
        const row = document.getElementById('gcrDetail' + idx);
        const icon = document.getElementById('gcrDetailIcon' + idx);
        if (!row) return;
        const isHidden = row.style.display === 'none';
        row.style.display = isHidden ? '' : 'none';
        if (icon) icon.className = isHidden ? 'fas fa-chevron-up' : 'fas fa-chevron-down';
    }

    function renderChart() {
        if (g_chart) { g_chart.destroy(); g_chart = null; }
        const noDataEl = document.getElementById('gcrNoData');
        const canvas = document.getElementById('gcrChart');

        if (g_rows.length === 0) {
            canvas.style.display = 'none';
            noDataEl.style.display = 'flex';
            noDataEl.innerHTML =
                `<div class="text-center text-muted">
                    <i class="fas fa-fire fa-2x mb-2 d-block opacity-50"></i>
                    ${escapeHtml(t('gascostreport.chart.no_data'))}
                 </div>`;
            return;
        }
        canvas.style.display = '';
        noDataEl.style.display = 'none';

        const labels = g_rows.map(r => r.periodLabel);
        const values = g_rows.map(r => r.totalCost);
        const circuitName = document.getElementById('gcrCircuit').selectedOptions[0]?.textContent.trim() || '';
        const ctx = canvas.getContext('2d');
        g_chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [{
                    label: t('gascostreport.chart.dataset_label', { 0: circuitName }),
                    data: values,
                    backgroundColor: 'rgba(230, 126, 34, 0.6)',
                    borderColor: 'rgba(211, 84, 0, 1)',
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
                            // 每根 bar 額外顯示該期用氣量供對照
                            afterLabel: (item) => {
                                const r = g_rows[item.dataIndex];
                                return r ? `${fmtM3(r.totalM3)} m³` : undefined;
                            }
                        }
                    }
                },
                scales: {
                    y: { beginAtZero: true, title: { display: true, text: t('gascostreport.chart.y_axis') } },
                    x: { title: { display: true, text: t('gascostreport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/GasCostReport/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'GasCostReport.xlsx';
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
            alert(t('gascostreport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._gcr = { query, exportExcel, toggleDetail };
})();
