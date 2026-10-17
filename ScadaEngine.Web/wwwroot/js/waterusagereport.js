// 用水報表頁邏輯
(function () {
    'use strict';

    let g_circuits = [];
    let g_chart = null;
    let g_lastResult = null;
    let g_chartMode = 'total'; // 'total' | 'breakdown'

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        applyCurrentPeriodDefaults();
        document.getElementById('wurGranularity').addEventListener('change', () => {
            updatePeriodVisibility();
            refreshPeriodHint();
        });
        document.getElementById('wurMonthStart').addEventListener('change', refreshPeriodHint);
        document.getElementById('wurMonthEnd').addEventListener('change', refreshPeriodHint);
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
        const today = (window._wurInit && window._wurInit.today) || new Date().toISOString().slice(0, 10);
        const d = new Date(today);
        const ym = today.slice(0, 7);
        const ymStart = ym + '-01';
        // 時粒度：日期 + 0~23 時下拉（只顯示兩位數小時，無分鐘）
        const hourOptions = Array.from({ length: 24 }, (_, i) => {
            const hh = String(i).padStart(2, '0');
            return `<option value="${i}">${hh}</option>`;
        }).join('');
        const startHourSel = document.getElementById('wurHourStartHour');
        const endHourSel = document.getElementById('wurHourEndHour');
        startHourSel.innerHTML = hourOptions;
        endHourSel.innerHTML = hourOptions;
        document.getElementById('wurHourStartDate').value = today;
        startHourSel.value = '0';
        document.getElementById('wurHourEndDate').value = today;
        endHourSel.value = '23';
        document.getElementById('wurDayStart').value = ymStart;
        document.getElementById('wurDayEnd').value = today;
        // 班別粒度：預設單日（今天）— 一天內各班分段
        document.getElementById('wurShiftStart').value = today;
        document.getElementById('wurShiftEnd').value = today;
        document.getElementById('wurMonthStart').value = ym;
        document.getElementById('wurMonthEnd').value = ym;
        document.getElementById('wurYearStart').value = d.getFullYear();
        document.getElementById('wurYearEnd').value = d.getFullYear();
    }

    function updatePeriodVisibility() {
        const g = document.getElementById('wurGranularity').value;
        document.querySelectorAll('.wur-period').forEach(el => el.classList.remove('active'));
        document.querySelectorAll('.wur-period-' + g).forEach(el => el.classList.add('active'));
    }

    // 日粒度預設起訖 = 本期（今天所屬月結期別）起始～結束；失敗時保留 initDefaults 的自然月預設
    async function applyCurrentPeriodDefaults() {
        try {
            const res = await fetch('/WaterBillingPeriodSetting/api/current');
            if (!res.ok) return;
            const p = await res.json();
            if (p && p.start && p.end) {
                document.getElementById('wurDayStart').value = p.start;
                document.getElementById('wurDayEnd').value = p.end;
            }
        } catch { /* 期別 API 不可用時維持自然月預設 */ }
    }

    // 月粒度：顯示所選起訖期別的實際日期區間（期別可能非自然月）
    async function refreshPeriodHint() {
        const hintEl = document.getElementById('wurPeriodHint');
        if (!hintEl) return;
        if (document.getElementById('wurGranularity').value !== 'month') {
            hintEl.textContent = '';
            return;
        }
        const fromYm = document.getElementById('wurMonthStart').value;
        const toYm = document.getElementById('wurMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { hintEl.textContent = ''; return; }
        try {
            const res = await fetch(`/WaterBillingPeriodSetting/api/range?fromYm=${encodeURIComponent(fromYm)}&toYm=${encodeURIComponent(toYm)}`);
            if (!res.ok) { hintEl.textContent = ''; return; }
            const periods = await res.json();
            if (!periods.length) { hintEl.textContent = ''; return; }
            hintEl.textContent = t('waterusagereport.period.hint',
                { 0: periods[0].start, 1: periods[periods.length - 1].end });
        } catch {
            hintEl.textContent = '';
        }
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/WaterUsageReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('wurCircuit');
            // 排成樹狀縮排
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('waterusagereport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('waterusagereport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error(t('waterusagereport.console.load_circuit_failed'), err);
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
                const tag = n.sid ? t('waterusagereport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('wurCircuit').value, 10);
        if (!circuitId) { alert(t('waterusagereport.alert.select_circuit')); return null; }
        const g = document.getElementById('wurGranularity').value;
        let startStr, endStr;
        if (g === 'hour') {
            const hsDate = document.getElementById('wurHourStartDate').value;
            const heDate = document.getElementById('wurHourEndDate').value;
            const hsHour = document.getElementById('wurHourStartHour').value;
            const heHour = document.getElementById('wurHourEndHour').value;
            if (!hsDate || !heDate || hsHour === '' || heHour === '') {
                alert(t('waterusagereport.alert.hour_order')); return null;
            }
            const hsHH = String(parseInt(hsHour, 10)).padStart(2, '0');
            const heHH = String(parseInt(heHour, 10)).padStart(2, '0');
            startStr = `${hsDate}T${hsHH}:00:00`;
            endStr = `${heDate}T${heHH}:00:00`;
            if (new Date(endStr) < new Date(startStr)) { alert(t('waterusagereport.alert.hour_order')); return null; }
        } else if (g === 'day') {
            const ds = document.getElementById('wurDayStart').value;
            const de = document.getElementById('wurDayEnd').value;
            if (!ds || !de) { alert(t('waterusagereport.alert.day_order')); return null; }
            startStr = ds + 'T00:00:00';
            endStr = de + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('waterusagereport.alert.day_order')); return null; }
        } else if (g === 'month') {
            startStr = document.getElementById('wurMonthStart').value + '-01T00:00:00';
            endStr = document.getElementById('wurMonthEnd').value + '-01T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('waterusagereport.alert.month_order')); return null; }
        } else if (g === 'year') {
            const ys = document.getElementById('wurYearStart').value;
            const ye = document.getElementById('wurYearEnd').value;
            if (parseInt(ye, 10) < parseInt(ys, 10)) { alert(t('waterusagereport.alert.year_order')); return null; }
            startStr = ys + '-01-01T00:00:00';
            endStr = ye + '-01-01T00:00:00';
        } else if (g === 'shift') {
            const ss = document.getElementById('wurShiftStart').value;
            const se = document.getElementById('wurShiftEnd').value;
            if (!ss || !se) { alert(t('waterusagereport.alert.day_order')); return null; }
            startStr = ss + 'T00:00:00';
            endStr = se + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('waterusagereport.alert.day_order')); return null; }
        }
        return { circuitId, granularity: g, start: startStr, end: endStr };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('wurTableBody').innerHTML =
            `<tr><td colspan="2" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('waterusagereport.table.querying'))}</td></tr>`;
        document.getElementById('btnExport').disabled = true;

        try {
            const res = await fetch('/WaterUsageReport/api/query', {
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
            document.getElementById('btnExport').disabled = false;
        } catch (err) {
            document.getElementById('wurTableBody').innerHTML =
                `<tr><td colspan="2" class="text-center text-danger py-3">${escapeHtml(t('waterusagereport.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    function renderTable(data) {
        const tbody = document.getElementById('wurTableBody');
        if (!data.buckets || data.buckets.length === 0) {
            tbody.innerHTML = `<tr><td colspan="2" class="text-center text-muted py-3">${escapeHtml(t('waterusagereport.table.no_data'))}</td></tr>`;
        } else {
            const totalLabel = escapeHtml(t('waterusagereport.table.total'));
            const staleTipRaw = t('waterusagereport.tooltip.stale');
            const staleTip = escapeHtml(staleTipRaw);
            tbody.innerHTML = data.buckets.map(b => {
                const mark = b.isStale
                    ? UiState.staleMarkHtml('wur-stale-mark', staleTipRaw) : '';
                const rowAttr = b.isStale ? ` title="${staleTip}"` : '';
                return `
                <tr${rowAttr}>
                    <td>${escapeHtml(b.szLabel)}${mark}</td>
                    <td class="text-end">${b.dM3.toFixed(3)}</td>
                </tr>`;
            }).join('') +
                `<tr class="wur-total"><td>${totalLabel}</td><td class="text-end">${data.dTotalM3.toFixed(3)}</td></tr>`;
        }
        document.getElementById('wurTotal').textContent = data.dTotalM3.toFixed(3);
        UiState.setWarnText('wurWarnText', data.isHasWarning
            ? t('waterusagereport.warning.stale_data') : '');
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
        const btn = document.getElementById('btnToggleBreakdown');
        const text = document.getElementById('btnToggleBreakdownText');
        if (!btn || !text) return;
        const hasBreakdown = g_lastResult && Array.isArray(g_lastResult.children) && g_lastResult.children.length > 1;
        btn.classList.toggle('d-none', !hasBreakdown);
        text.textContent = g_chartMode === 'breakdown'
            ? t('waterusagereport.button.show_total')
            : t('waterusagereport.button.show_breakdown');
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
        const ctx = document.getElementById('wurChart').getContext('2d');

        const bBreakdown = g_chartMode === 'breakdown'
            && Array.isArray(data.children) && data.children.length > 1;

        let datasets;
        let tooltipCallbacks;
        let bStacked;

        // 斷線提示：依 bucket index 查主迴路 buckets[idx].isStale，兩種模式共用
        const staleTip = t('waterusagereport.tooltip.stale');
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
                // 每根 bar 顯示「子名稱: 值 m³」
                label: function (ctx) {
                    const v = ctx.parsed.y;
                    return `${ctx.dataset.label}: ${(v == null ? 0 : v).toFixed(3)} m³`;
                },
                // footer 顯示該 bucket 的合計
                footer: function (items) {
                    let sum = 0;
                    items.forEach(it => { if (it.parsed && it.parsed.y != null) sum += it.parsed.y; });
                    return t('waterusagereport.chart.breakdown_total_label', { 0: sum.toFixed(3) });
                },
                afterBody: staleAfterBody
            };
        } else {
            bStacked = false;
            tooltipCallbacks = { afterBody: staleAfterBody };
            const values = data.buckets.map(b => b.dM3);
            datasets = [{
                label: t('waterusagereport.chart.dataset_label', { 0: data.szCircuitName }),
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
                    // 不指定 beginAtZero — 含負值的虛擬迴路（A+B+C-D）需正確顯示負 bar
                    y: { stacked: bStacked, title: { display: true, text: t('waterusagereport.chart.y_axis') } },
                    x: { stacked: bStacked, title: { display: true, text: t('waterusagereport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/WaterUsageReport/api/export', {
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
            let szFileName = 'WaterUsageReport.xlsx';
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
            alert(t('waterusagereport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._wur = { query, exportExcel, toggleBreakdown };
})();
