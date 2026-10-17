// 冷凍噸報表頁邏輯（對標 energyreport.js；數值單位 kWh → RT·h）
(function () {
    'use strict';

    let g_circuits = [];
    let g_chart = null;
    let g_lastResult = null;
    let g_chartMode = 'total'; // 'total' | 'breakdown'

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        document.getElementById('rtGranularity').addEventListener('change', updatePeriodVisibility);
        updatePeriodVisibility();
        if (window.i18n) {
            window.i18n.ready(loadCircuits);
        } else {
            loadCircuits();
        }
    });

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    function initDefaults() {
        const today = (window._rtInit && window._rtInit.today) || new Date().toISOString().slice(0, 10);
        const d = new Date(today);
        const ym = today.slice(0, 7);
        const ymStart = ym + '-01';
        const hourOptions = Array.from({ length: 24 }, (_, i) => {
            const hh = String(i).padStart(2, '0');
            return `<option value="${i}">${hh}</option>`;
        }).join('');
        const startHourSel = document.getElementById('rtHourStartHour');
        const endHourSel = document.getElementById('rtHourEndHour');
        startHourSel.innerHTML = hourOptions;
        endHourSel.innerHTML = hourOptions;
        document.getElementById('rtHourStartDate').value = today;
        startHourSel.value = '0';
        document.getElementById('rtHourEndDate').value = today;
        endHourSel.value = '23';
        document.getElementById('rtDayStart').value = ymStart;
        document.getElementById('rtDayEnd').value = today;
        document.getElementById('rtMonthStart').value = ym;
        document.getElementById('rtMonthEnd').value = ym;
        document.getElementById('rtYearStart').value = d.getFullYear();
        document.getElementById('rtYearEnd').value = d.getFullYear();
    }

    function updatePeriodVisibility() {
        const g = document.getElementById('rtGranularity').value;
        document.querySelectorAll('.rt-period').forEach(el => el.classList.remove('active'));
        document.querySelectorAll('.rt-period-' + g).forEach(el => el.classList.add('active'));
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/RefrigerationTonReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('rtCircuit');
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('refrigerationtonreport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('refrigerationtonreport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error(t('refrigerationtonreport.console.load_circuit_failed'), err);
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
                const tag = n.sid ? t('refrigerationtonreport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('rtCircuit').value, 10);
        if (!circuitId) { alert(t('refrigerationtonreport.alert.select_circuit')); return null; }
        const g = document.getElementById('rtGranularity').value;
        let startStr, endStr;
        if (g === 'hour') {
            const hsDate = document.getElementById('rtHourStartDate').value;
            const heDate = document.getElementById('rtHourEndDate').value;
            const hsHour = document.getElementById('rtHourStartHour').value;
            const heHour = document.getElementById('rtHourEndHour').value;
            if (!hsDate || !heDate || hsHour === '' || heHour === '') {
                alert(t('refrigerationtonreport.alert.hour_order')); return null;
            }
            const hsHH = String(parseInt(hsHour, 10)).padStart(2, '0');
            const heHH = String(parseInt(heHour, 10)).padStart(2, '0');
            startStr = `${hsDate}T${hsHH}:00:00`;
            endStr = `${heDate}T${heHH}:00:00`;
            if (new Date(endStr) < new Date(startStr)) { alert(t('refrigerationtonreport.alert.hour_order')); return null; }
        } else if (g === 'day') {
            const ds = document.getElementById('rtDayStart').value;
            const de = document.getElementById('rtDayEnd').value;
            if (!ds || !de) { alert(t('refrigerationtonreport.alert.day_order')); return null; }
            startStr = ds + 'T00:00:00';
            endStr = de + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('refrigerationtonreport.alert.day_order')); return null; }
        } else if (g === 'month') {
            startStr = document.getElementById('rtMonthStart').value + '-01T00:00:00';
            endStr = document.getElementById('rtMonthEnd').value + '-01T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('refrigerationtonreport.alert.month_order')); return null; }
        } else if (g === 'year') {
            const ys = document.getElementById('rtYearStart').value;
            const ye = document.getElementById('rtYearEnd').value;
            if (parseInt(ye, 10) < parseInt(ys, 10)) { alert(t('refrigerationtonreport.alert.year_order')); return null; }
            startStr = ys + '-01-01T00:00:00';
            endStr = ye + '-01-01T00:00:00';
        }
        return { circuitId, granularity: g, start: startStr, end: endStr };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('rtTableBody').innerHTML =
            `<tr><td colspan="2" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('refrigerationtonreport.table.querying'))}</td></tr>`;
        document.getElementById('rtBtnExport').disabled = true;

        try {
            const res = await fetch('/RefrigerationTonReport/api/query', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const data = await res.json();
            g_lastResult = data;
            g_chartMode = 'total';
            updateToggleButton();
            renderTable(data);
            renderChart(data);
            document.getElementById('rtBtnExport').disabled = false;
        } catch (err) {
            document.getElementById('rtTableBody').innerHTML =
                `<tr><td colspan="2" class="text-center text-danger py-3">${escapeHtml(t('refrigerationtonreport.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    function renderTable(data) {
        const tbody = document.getElementById('rtTableBody');
        if (!data.buckets || data.buckets.length === 0) {
            tbody.innerHTML = `<tr><td colspan="2" class="text-center text-muted py-3">${escapeHtml(t('refrigerationtonreport.table.no_data'))}</td></tr>`;
        } else {
            const totalLabel = escapeHtml(t('refrigerationtonreport.table.total'));
            tbody.innerHTML = data.buckets.map(b => `
                <tr>
                    <td>${escapeHtml(b.szLabel)}</td>
                    <td class="text-end">${b.dRtHour.toFixed(1)}</td>
                </tr>`).join('') +
                `<tr class="rt-total"><td>${totalLabel}</td><td class="text-end">${data.dTotalRtHour.toFixed(1)}</td></tr>`;
        }
        document.getElementById('rtTotal').textContent = data.dTotalRtHour.toFixed(1);
        UiState.setWarnText('rtWarnText', data.isHasWarning
            ? t('refrigerationtonreport.warning.data_incomplete') : '');
    }

    function pickColor(i, alpha) {
        const n = 12;
        const h = Math.round((i % n) * (360 / n));
        const a = (alpha == null) ? 0.7 : alpha;
        return `hsla(${h}, 65%, 50%, ${a})`;
    }

    function updateToggleButton() {
        const btn = document.getElementById('rtBtnToggleBreakdown');
        const text = document.getElementById('rtBtnToggleBreakdownText');
        if (!btn || !text) return;
        const hasBreakdown = g_lastResult && Array.isArray(g_lastResult.children) && g_lastResult.children.length > 1;
        btn.classList.toggle('d-none', !hasBreakdown);
        text.textContent = g_chartMode === 'breakdown'
            ? t('refrigerationtonreport.button.show_total')
            : t('refrigerationtonreport.button.show_breakdown');
    }

    function toggleBreakdown() {
        if (!g_lastResult) return;
        g_chartMode = (g_chartMode === 'total') ? 'breakdown' : 'total';
        updateToggleButton();
        renderChart(g_lastResult);
    }

    function renderChart(data) {
        const labels = data.buckets.map(b => b.szLabel);
        if (g_chart) g_chart.destroy();
        const ctx = document.getElementById('rtChart').getContext('2d');

        const bBreakdown = g_chartMode === 'breakdown'
            && Array.isArray(data.children) && data.children.length > 1;

        let datasets;
        let tooltipCallbacks;
        let bStacked;

        if (bBreakdown) {
            bStacked = true;
            datasets = data.children.map((child, i) => ({
                label: child.szName,
                data: child.dRtHourPerBucket,
                backgroundColor: pickColor(i, 0.7),
                borderColor: pickColor(i, 1),
                borderWidth: 1
            }));
            tooltipCallbacks = {
                label: function (ctx) {
                    const v = ctx.parsed.y;
                    return `${ctx.dataset.label}: ${(v == null ? 0 : v).toFixed(1)} RT·h`;
                },
                footer: function (items) {
                    let sum = 0;
                    items.forEach(it => { if (it.parsed && it.parsed.y != null) sum += it.parsed.y; });
                    return t('refrigerationtonreport.chart.breakdown_total_label', { 0: sum.toFixed(1) });
                }
            };
        } else {
            bStacked = false;
            const values = data.buckets.map(b => b.dRtHour);
            datasets = [{
                label: t('refrigerationtonreport.chart.dataset_label', { 0: data.szCircuitName }),
                data: values,
                backgroundColor: 'rgba(13, 110, 253, 0.6)',
                borderColor: 'rgba(13, 110, 253, 1)',
                borderWidth: 1
            }];
        }

        g_chart = new Chart(ctx, {
            type: 'bar',
            data: { labels, datasets },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: true },
                    tooltip: tooltipCallbacks ? { mode: 'index', intersect: false, callbacks: tooltipCallbacks } : undefined
                },
                interaction: bBreakdown ? { mode: 'index', intersect: false } : undefined,
                scales: {
                    y: { beginAtZero: true, stacked: bStacked, title: { display: true, text: t('refrigerationtonreport.chart.y_axis') } },
                    x: { stacked: bStacked, title: { display: true, text: t('refrigerationtonreport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/RefrigerationTonReport/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'RefrigerationTonReport.xlsx';
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
            alert(t('refrigerationtonreport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._rt = { query, exportExcel, toggleBreakdown };
})();
