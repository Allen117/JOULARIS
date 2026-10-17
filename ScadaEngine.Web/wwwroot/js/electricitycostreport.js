// 電費報表頁邏輯 — 比照用電報表（energyreport.js），結果為電費（元）。
// 資料源 ElectricityCostHourly；tou/flat 為落表精確值，progressive 為期別級距按 kWh 占比分攤（後端 isEstimated 註記）。
(function () {
    'use strict';

    let g_circuits = [];
    let g_chart = null;

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        applyCurrentPeriodDefaults();
        document.getElementById('ecrGranularity').addEventListener('change', () => {
            updatePeriodVisibility();
            refreshPeriodHint();
        });
        document.getElementById('ecrMonthStart').addEventListener('change', refreshPeriodHint);
        document.getElementById('ecrMonthEnd').addEventListener('change', refreshPeriodHint);
        updatePeriodVisibility();
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
        const today = (window._ecrInit && window._ecrInit.today) || new Date().toISOString().slice(0, 10);
        const d = new Date(today);
        const ym = today.slice(0, 7);
        const ymStart = ym + '-01';
        const hourOptions = Array.from({ length: 24 }, (_, i) => {
            const hh = String(i).padStart(2, '0');
            return `<option value="${i}">${hh}</option>`;
        }).join('');
        const startHourSel = document.getElementById('ecrHourStartHour');
        const endHourSel = document.getElementById('ecrHourEndHour');
        startHourSel.innerHTML = hourOptions;
        endHourSel.innerHTML = hourOptions;
        document.getElementById('ecrHourStartDate').value = today;
        startHourSel.value = '0';
        document.getElementById('ecrHourEndDate').value = today;
        endHourSel.value = '23';
        document.getElementById('ecrDayStart').value = ymStart;
        document.getElementById('ecrDayEnd').value = today;
        document.getElementById('ecrMonthStart').value = ym;
        document.getElementById('ecrMonthEnd').value = ym;
        document.getElementById('ecrYearStart').value = d.getFullYear();
        document.getElementById('ecrYearEnd').value = d.getFullYear();
    }

    function updatePeriodVisibility() {
        const g = document.getElementById('ecrGranularity').value;
        document.querySelectorAll('.er-period').forEach(el => el.classList.remove('active'));
        document.querySelectorAll('.er-period-' + g).forEach(el => el.classList.add('active'));
    }

    // 日粒度預設起訖 = 本期（今天所屬月結期別）；失敗時保留自然月預設
    async function applyCurrentPeriodDefaults() {
        try {
            const res = await fetch('/BillingPeriodSetting/api/current');
            if (!res.ok) return;
            const p = await res.json();
            if (p && p.start && p.end) {
                document.getElementById('ecrDayStart').value = p.start;
                document.getElementById('ecrDayEnd').value = p.end;
            }
        } catch { /* 期別 API 不可用時維持自然月預設 */ }
    }

    // 月粒度：顯示所選起訖期別的實際日期區間
    async function refreshPeriodHint() {
        const hintEl = document.getElementById('ecrPeriodHint');
        if (!hintEl) return;
        if (document.getElementById('ecrGranularity').value !== 'month') {
            hintEl.textContent = '';
            return;
        }
        const fromYm = document.getElementById('ecrMonthStart').value;
        const toYm = document.getElementById('ecrMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { hintEl.textContent = ''; return; }
        try {
            const res = await fetch(`/BillingPeriodSetting/api/range?fromYm=${encodeURIComponent(fromYm)}&toYm=${encodeURIComponent(toYm)}`);
            if (!res.ok) { hintEl.textContent = ''; return; }
            const periods = await res.json();
            if (!periods.length) { hintEl.textContent = ''; return; }
            hintEl.textContent = t('costreport.period.hint',
                { 0: periods[0].start, 1: periods[periods.length - 1].end });
        } catch {
            hintEl.textContent = '';
        }
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/ElectricityCostReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('ecrCircuit');
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('costreport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('costreport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error('[costreport] load circuits failed', err);
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
                const tag = n.sid ? t('costreport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('ecrCircuit').value, 10);
        if (!circuitId) { alert(t('costreport.alert.select_circuit')); return null; }
        const g = document.getElementById('ecrGranularity').value;
        let startStr, endStr;
        if (g === 'hour') {
            const hsDate = document.getElementById('ecrHourStartDate').value;
            const heDate = document.getElementById('ecrHourEndDate').value;
            const hsHour = document.getElementById('ecrHourStartHour').value;
            const heHour = document.getElementById('ecrHourEndHour').value;
            if (!hsDate || !heDate || hsHour === '' || heHour === '') {
                alert(t('costreport.alert.hour_order')); return null;
            }
            const hsHH = String(parseInt(hsHour, 10)).padStart(2, '0');
            const heHH = String(parseInt(heHour, 10)).padStart(2, '0');
            startStr = `${hsDate}T${hsHH}:00:00`;
            endStr = `${heDate}T${heHH}:00:00`;
            if (new Date(endStr) < new Date(startStr)) { alert(t('costreport.alert.hour_order')); return null; }
        } else if (g === 'day') {
            const ds = document.getElementById('ecrDayStart').value;
            const de = document.getElementById('ecrDayEnd').value;
            if (!ds || !de) { alert(t('costreport.alert.day_order')); return null; }
            startStr = ds + 'T00:00:00';
            endStr = de + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('costreport.alert.day_order')); return null; }
        } else if (g === 'month') {
            startStr = document.getElementById('ecrMonthStart').value + '-01T00:00:00';
            endStr = document.getElementById('ecrMonthEnd').value + '-01T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('costreport.alert.month_order')); return null; }
        } else if (g === 'year') {
            const ys = document.getElementById('ecrYearStart').value;
            const ye = document.getElementById('ecrYearEnd').value;
            if (parseInt(ye, 10) < parseInt(ys, 10)) { alert(t('costreport.alert.year_order')); return null; }
            startStr = ys + '-01-01T00:00:00';
            endStr = ye + '-01-01T00:00:00';
        }
        return { circuitId, granularity: g, start: startStr, end: endStr };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('ecrTableBody').innerHTML =
            `<tr><td colspan="3" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('costreport.table.querying'))}</td></tr>`;
        document.getElementById('btnEcrExport').disabled = true;

        try {
            const res = await fetch('/ElectricityCostReport/api/query', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const data = await res.json();
            renderTable(data);
            renderChart(data);
            document.getElementById('btnEcrExport').disabled = !data.hasData;
        } catch (err) {
            document.getElementById('ecrTableBody').innerHTML =
                `<tr><td colspan="3" class="text-center text-danger py-3">${escapeHtml(t('costreport.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    function fmtCost(v) {
        return v.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    // kWh 顯示一律小數一位（0 → 0.0），與用電報表／能源申報／日報一致
    function fmtKwh(v) {
        return v.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    function renderTable(data) {
        const tbody = document.getElementById('ecrTableBody');
        if (!data.hasData) {
            tbody.innerHTML = `<tr><td colspan="3" class="text-center text-muted py-3">${escapeHtml(t('costreport.table.no_data'))}</td></tr>`;
            document.getElementById('ecrTotalCost').textContent = '--';
            document.getElementById('ecrTotalKwh').textContent = '--';
            UiState.setWarnText('ecrEstimateText', '');
            return;
        }
        const totalLabel = escapeHtml(t('costreport.table.total'));
        tbody.innerHTML = data.buckets.map(b => `
            <tr>
                <td>${escapeHtml(b.label)}</td>
                <td class="text-end">${fmtKwh(b.kwh)}</td>
                <td class="text-end">${fmtCost(b.cost)}</td>
            </tr>`).join('') +
            `<tr class="er-total"><td>${totalLabel}</td><td class="text-end">${fmtKwh(data.totalKwh)}</td><td class="text-end">${fmtCost(data.totalCost)}</td></tr>`;
        document.getElementById('ecrTotalCost').textContent = fmtCost(data.totalCost);
        document.getElementById('ecrTotalKwh').textContent = fmtKwh(data.totalKwh);
        UiState.setWarnText('ecrEstimateText', data.isEstimated
            ? t('costreport.note.estimated') : '');
    }

    function renderChart(data) {
        if (g_chart) { g_chart.destroy(); g_chart = null; }
        const noDataEl = document.getElementById('ecrNoData');
        const canvas = document.getElementById('ecrChart');

        if (!data.hasData) {
            canvas.style.display = 'none';
            noDataEl.style.display = 'flex';
            noDataEl.innerHTML =
                `<div class="text-center text-muted">
                    <i class="fas fa-file-invoice-dollar fa-2x mb-2 d-block opacity-50"></i>
                    ${escapeHtml(t('costreport.chart.no_data'))}
                    <div class="small mt-1">${escapeHtml(t('costreport.chart.no_data_hint'))}</div>
                 </div>`;
            return;
        }
        canvas.style.display = '';
        noDataEl.style.display = 'none';

        const labels = data.buckets.map(b => b.label);
        const values = data.buckets.map(b => b.cost);
        const ctx = canvas.getContext('2d');
        g_chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [{
                    label: t('costreport.chart.dataset_label', { 0: data.circuitName }),
                    data: values,
                    backgroundColor: 'rgba(67, 160, 71, 0.6)',
                    borderColor: 'rgba(67, 160, 71, 1)',
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
                            // 每根 bar 額外顯示該 bucket kWh 供對照
                            afterLabel: (item) => {
                                const b = data.buckets[item.dataIndex];
                                return b ? `${fmtKwh(b.kwh)} kWh` : undefined;
                            }
                        }
                    }
                },
                scales: {
                    // 不指定 beginAtZero — 含負值的虛擬迴路（A+B+C-D）需正確顯示負 bar
                    y: { title: { display: true, text: t('costreport.chart.y_axis') } },
                    x: { title: { display: true, text: t('costreport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/ElectricityCostReport/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'ElectricityCostReport.xlsx';
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
            alert(t('costreport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._ecr = { query, exportExcel };
})();
