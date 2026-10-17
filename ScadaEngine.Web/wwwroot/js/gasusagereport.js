// 用氣報表頁邏輯（月粒度走氣費期別，已刪除期別不產生 bucket → 兩月一期時一年 6 柱）
(function () {
    'use strict';

    let g_circuits = [];
    let g_chart = null;
    let g_lastResult = null;
    let g_chartMode = 'total'; // 'total' | 'breakdown'

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        applyCurrentPeriodDefaults();
        document.getElementById('gurGranularity').addEventListener('change', () => {
            updatePeriodVisibility();
            refreshPeriodHint();
        });
        document.getElementById('gurMonthStart').addEventListener('change', refreshPeriodHint);
        document.getElementById('gurMonthEnd').addEventListener('change', refreshPeriodHint);
        updatePeriodVisibility();
        // 等 i18n 字典載入後再 fetch 迴路（其中 placeholder 字串需要翻譯）
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
        const today = (window._gurInit && window._gurInit.today) || new Date().toISOString().slice(0, 10);
        const d = new Date(today);
        const ym = today.slice(0, 7);
        const ymStart = ym + '-01';
        // 時粒度：日期 + 0~23 時下拉（只顯示兩位數小時，無分鐘）
        const hourOptions = Array.from({ length: 24 }, (_, i) => {
            const hh = String(i).padStart(2, '0');
            return `<option value="${i}">${hh}</option>`;
        }).join('');
        const startHourSel = document.getElementById('gurHourStartHour');
        const endHourSel = document.getElementById('gurHourEndHour');
        startHourSel.innerHTML = hourOptions;
        endHourSel.innerHTML = hourOptions;
        document.getElementById('gurHourStartDate').value = today;
        startHourSel.value = '0';
        document.getElementById('gurHourEndDate').value = today;
        endHourSel.value = '23';
        document.getElementById('gurDayStart').value = ymStart;
        document.getElementById('gurDayEnd').value = today;
        // 班別粒度：預設單日（今天）— 一天內各班分段
        document.getElementById('gurShiftStart').value = today;
        document.getElementById('gurShiftEnd').value = today;
        document.getElementById('gurMonthStart').value = ym;
        document.getElementById('gurMonthEnd').value = ym;
        document.getElementById('gurYearStart').value = d.getFullYear();
        document.getElementById('gurYearEnd').value = d.getFullYear();
    }

    function updatePeriodVisibility() {
        const g = document.getElementById('gurGranularity').value;
        document.querySelectorAll('.gur-period').forEach(el => el.classList.remove('active'));
        document.querySelectorAll('.gur-period-' + g).forEach(el => el.classList.add('active'));
    }

    // 日粒度預設起訖 = 本期（今天所屬氣費期別）起始～結束；失敗時保留 initDefaults 的自然月預設
    async function applyCurrentPeriodDefaults() {
        try {
            const res = await fetch('/GasBillingPeriodSetting/api/current');
            if (!res.ok) return;
            const p = await res.json();
            if (p && p.start && p.end) {
                document.getElementById('gurDayStart').value = p.start;
                document.getElementById('gurDayEnd').value = p.end;
            }
        } catch { /* 期別 API 不可用時維持自然月預設 */ }
    }

    // 月粒度：顯示所選起訖期別的實際日期區間 + 實際期數（兩月一期時期數少於月份數）
    async function refreshPeriodHint() {
        const hintEl = document.getElementById('gurPeriodHint');
        if (!hintEl) return;
        if (document.getElementById('gurGranularity').value !== 'month') {
            hintEl.textContent = '';
            return;
        }
        const fromYm = document.getElementById('gurMonthStart').value;
        const toYm = document.getElementById('gurMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { hintEl.textContent = ''; return; }
        try {
            const res = await fetch(`/GasBillingPeriodSetting/api/range?fromYm=${encodeURIComponent(fromYm)}&toYm=${encodeURIComponent(toYm)}`);
            if (!res.ok) { hintEl.textContent = ''; return; }
            const periods = await res.json();
            if (!periods.length) { hintEl.textContent = ''; return; }
            hintEl.textContent = t('gasusagereport.period.hint',
                { 0: periods[0].start, 1: periods[periods.length - 1].end, 2: periods.length });
        } catch {
            hintEl.textContent = '';
        }
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/GasUsageReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('gurCircuit');
            // 排成樹狀縮排
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('gasusagereport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('gasusagereport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error('[gasusagereport] load circuits failed', err);
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
                const tag = n.sid ? t('gasusagereport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('gurCircuit').value, 10);
        if (!circuitId) { alert(t('gasusagereport.alert.select_circuit')); return null; }
        const g = document.getElementById('gurGranularity').value;
        let startStr, endStr;
        if (g === 'hour') {
            const hsDate = document.getElementById('gurHourStartDate').value;
            const heDate = document.getElementById('gurHourEndDate').value;
            const hsHour = document.getElementById('gurHourStartHour').value;
            const heHour = document.getElementById('gurHourEndHour').value;
            if (!hsDate || !heDate || hsHour === '' || heHour === '') {
                alert(t('gasusagereport.alert.hour_order')); return null;
            }
            const hsHH = String(parseInt(hsHour, 10)).padStart(2, '0');
            const heHH = String(parseInt(heHour, 10)).padStart(2, '0');
            startStr = `${hsDate}T${hsHH}:00:00`;
            endStr = `${heDate}T${heHH}:00:00`;
            if (new Date(endStr) < new Date(startStr)) { alert(t('gasusagereport.alert.hour_order')); return null; }
        } else if (g === 'day') {
            const ds = document.getElementById('gurDayStart').value;
            const de = document.getElementById('gurDayEnd').value;
            if (!ds || !de) { alert(t('gasusagereport.alert.day_order')); return null; }
            startStr = ds + 'T00:00:00';
            endStr = de + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('gasusagereport.alert.day_order')); return null; }
        } else if (g === 'month') {
            startStr = document.getElementById('gurMonthStart').value + '-01T00:00:00';
            endStr = document.getElementById('gurMonthEnd').value + '-01T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('gasusagereport.alert.month_order')); return null; }
        } else if (g === 'year') {
            const ys = document.getElementById('gurYearStart').value;
            const ye = document.getElementById('gurYearEnd').value;
            if (parseInt(ye, 10) < parseInt(ys, 10)) { alert(t('gasusagereport.alert.year_order')); return null; }
            startStr = ys + '-01-01T00:00:00';
            endStr = ye + '-01-01T00:00:00';
        } else if (g === 'shift') {
            const ss = document.getElementById('gurShiftStart').value;
            const se = document.getElementById('gurShiftEnd').value;
            if (!ss || !se) { alert(t('gasusagereport.alert.day_order')); return null; }
            startStr = ss + 'T00:00:00';
            endStr = se + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('gasusagereport.alert.day_order')); return null; }
        }
        return { circuitId, granularity: g, start: startStr, end: endStr };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('gurTableBody').innerHTML =
            `<tr><td colspan="2" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('gasusagereport.table.querying'))}</td></tr>`;
        document.getElementById('btnGurExport').disabled = true;

        try {
            const res = await fetch('/GasUsageReport/api/query', {
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
            // 每次重查重置為「合計」預設，避免上次明細狀態殘留
            g_chartMode = 'total';
            updateToggleButton();
            renderTable(data);
            renderChart(data);
            document.getElementById('btnGurExport').disabled = false;
        } catch (err) {
            document.getElementById('gurTableBody').innerHTML =
                `<tr><td colspan="2" class="text-center text-danger py-3">${escapeHtml(t('gasusagereport.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    function renderTable(data) {
        const tbody = document.getElementById('gurTableBody');
        if (!data.buckets || data.buckets.length === 0) {
            tbody.innerHTML = `<tr><td colspan="2" class="text-center text-muted py-3">${escapeHtml(t('gasusagereport.table.no_data'))}</td></tr>`;
        } else {
            const totalLabel = escapeHtml(t('gasusagereport.table.total'));
            const staleTipRaw = t('gasusagereport.tooltip.stale');
            const staleTip = escapeHtml(staleTipRaw);
            tbody.innerHTML = data.buckets.map(b => {
                const mark = b.isStale
                    ? UiState.staleMarkHtml('gur-stale-mark', staleTipRaw) : '';
                const rowAttr = b.isStale ? ` title="${staleTip}"` : '';
                return `
                <tr${rowAttr}>
                    <td>${escapeHtml(b.szLabel)}${mark}</td>
                    <td class="text-end">${b.dM3.toFixed(3)}</td>
                </tr>`;
            }).join('') +
                `<tr class="gur-total"><td>${totalLabel}</td><td class="text-end">${data.dTotalM3.toFixed(3)}</td></tr>`;
        }
        document.getElementById('gurTotal').textContent = data.dTotalM3.toFixed(3);
        UiState.setWarnText('gurWarnText', data.isHasWarning
            ? t('gasusagereport.warning.stale_data') : '');
    }

    // HSL 等距 12 色循環調色盤；超出時取模重用
    function pickColor(i, alpha) {
        const n = 12;
        const h = Math.round((i % n) * (360 / n));
        const a = (alpha == null) ? 0.7 : alpha;
        return `hsla(${h}, 65%, 50%, ${a})`;
    }

    // 切換按鈕顯示條件：data.children 存在且 > 1
    function updateToggleButton() {
        const btn = document.getElementById('btnGurToggleBreakdown');
        const text = document.getElementById('btnGurToggleBreakdownText');
        if (!btn || !text) return;
        const hasBreakdown = g_lastResult && Array.isArray(g_lastResult.children) && g_lastResult.children.length > 1;
        btn.classList.toggle('d-none', !hasBreakdown);
        text.textContent = g_chartMode === 'breakdown'
            ? t('gasusagereport.button.show_total')
            : t('gasusagereport.button.show_breakdown');
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
        const ctx = document.getElementById('gurChart').getContext('2d');

        const bBreakdown = g_chartMode === 'breakdown'
            && Array.isArray(data.children) && data.children.length > 1;

        let datasets;
        let tooltipCallbacks;
        let bStacked;

        // 斷線提示：依 bucket index 查主迴路 buckets[idx].isStale，兩種模式共用
        const staleTip = t('gasusagereport.tooltip.stale');
        const staleAfterBody = function (items) {
            if (!items || !items.length) return undefined;
            const idx = items[0].dataIndex;
            return (data.buckets[idx] && data.buckets[idx].isStale) ? staleTip : undefined;
        };

        if (bBreakdown) {
            bStacked = true;
            datasets = data.children.map((child, i) => ({
                label: child.szName,
                data: child.dM3PerBucket,
                backgroundColor: pickColor(i, 0.7),
                borderColor: pickColor(i, 1),
                borderWidth: 1
            }));
            tooltipCallbacks = {
                label: function (ctx) {
                    const v = ctx.parsed.y;
                    return `${ctx.dataset.label}: ${(v == null ? 0 : v).toFixed(3)} m³`;
                },
                footer: function (items) {
                    let sum = 0;
                    items.forEach(it => { if (it.parsed && it.parsed.y != null) sum += it.parsed.y; });
                    return t('gasusagereport.chart.breakdown_total_label', { 0: sum.toFixed(3) });
                },
                afterBody: staleAfterBody
            };
        } else {
            bStacked = false;
            tooltipCallbacks = { afterBody: staleAfterBody };
            const values = data.buckets.map(b => b.dM3);
            datasets = [{
                label: t('gasusagereport.chart.dataset_label', { 0: data.szCircuitName }),
                data: values,
                backgroundColor: 'rgba(230, 126, 34, 0.6)',
                borderColor: 'rgba(211, 84, 0, 1)',
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
                    // 不指定 beginAtZero — 含負值的虛擬迴路（A+B+C-D）需正確顯示負 bar
                    y: { stacked: bStacked, title: { display: true, text: t('gasusagereport.chart.y_axis') } },
                    x: { stacked: bStacked, title: { display: true, text: t('gasusagereport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/GasUsageReport/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            // 從 Content-Disposition 解析檔名（fallback 預設名）
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'GasUsageReport.xlsx';
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
            alert(t('gasusagereport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._gur = { query, exportExcel, toggleBreakdown };
})();
