// 用電報表頁邏輯
(function () {
    'use strict';

    let g_circuits = [];
    let g_chart = null;
    let g_lastResult = null;
    let g_chartMode = 'total'; // 'total' | 'breakdown'

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        applyCurrentPeriodDefaults();
        document.getElementById('erGranularity').addEventListener('change', () => {
            updatePeriodVisibility();
            refreshPeriodHint();
        });
        document.getElementById('erMonthStart').addEventListener('change', refreshPeriodHint);
        document.getElementById('erMonthEnd').addEventListener('change', refreshPeriodHint);
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
        const today = (window._erInit && window._erInit.today) || new Date().toISOString().slice(0, 10);
        const d = new Date(today);
        const ym = today.slice(0, 7);
        const ymStart = ym + '-01';
        // 時粒度：日期 + 0~23 時下拉（只顯示兩位數小時，無分鐘）
        const hourOptions = Array.from({ length: 24 }, (_, i) => {
            const hh = String(i).padStart(2, '0');
            return `<option value="${i}">${hh}</option>`;
        }).join('');
        const startHourSel = document.getElementById('erHourStartHour');
        const endHourSel = document.getElementById('erHourEndHour');
        startHourSel.innerHTML = hourOptions;
        endHourSel.innerHTML = hourOptions;
        document.getElementById('erHourStartDate').value = today;
        startHourSel.value = '0';
        document.getElementById('erHourEndDate').value = today;
        endHourSel.value = '23';
        document.getElementById('erDayStart').value = ymStart;
        document.getElementById('erDayEnd').value = today;
        // 班別粒度：預設單日（今天）— 一天內各班分段
        document.getElementById('erShiftStart').value = today;
        document.getElementById('erShiftEnd').value = today;
        document.getElementById('erMonthStart').value = ym;
        document.getElementById('erMonthEnd').value = ym;
        document.getElementById('erYearStart').value = d.getFullYear();
        document.getElementById('erYearEnd').value = d.getFullYear();
    }

    function updatePeriodVisibility() {
        const g = document.getElementById('erGranularity').value;
        document.querySelectorAll('.er-period').forEach(el => el.classList.remove('active'));
        document.querySelectorAll('.er-period-' + g).forEach(el => el.classList.add('active'));
    }

    // 日粒度預設起訖 = 本期（今天所屬月結期別）起始～結束；失敗時保留 initDefaults 的自然月預設
    async function applyCurrentPeriodDefaults() {
        try {
            const res = await fetch('/BillingPeriodSetting/api/current');
            if (!res.ok) return;
            const p = await res.json();
            if (p && p.start && p.end) {
                document.getElementById('erDayStart').value = p.start;
                document.getElementById('erDayEnd').value = p.end;
            }
        } catch { /* 期別 API 不可用時維持自然月預設 */ }
    }

    // 月粒度：顯示所選起訖期別的實際日期區間（期別可能非自然月）
    async function refreshPeriodHint() {
        const hintEl = document.getElementById('erPeriodHint');
        if (!hintEl) return;
        if (document.getElementById('erGranularity').value !== 'month') {
            hintEl.textContent = '';
            return;
        }
        const fromYm = document.getElementById('erMonthStart').value;
        const toYm = document.getElementById('erMonthEnd').value;
        if (!fromYm || !toYm || toYm < fromYm) { hintEl.textContent = ''; return; }
        try {
            const res = await fetch(`/BillingPeriodSetting/api/range?fromYm=${encodeURIComponent(fromYm)}&toYm=${encodeURIComponent(toYm)}`);
            if (!res.ok) { hintEl.textContent = ''; return; }
            const periods = await res.json();
            if (!periods.length) { hintEl.textContent = ''; return; }
            hintEl.textContent = t('energyreport.period.hint',
                { 0: periods[0].start, 1: periods[periods.length - 1].end });
        } catch {
            hintEl.textContent = '';
        }
    }

    async function loadCircuits() {
        try {
            const res = await fetch('/EnergyReport/api/circuits');
            g_circuits = await res.json();
            const sel = document.getElementById('erCircuit');
            // 排成樹狀縮排
            const items = buildIndentedList(g_circuits);
            sel.innerHTML = items.length === 0
                ? `<option value="">${escapeHtml(t('energyreport.select.empty'))}</option>`
                : `<option value="">${escapeHtml(t('energyreport.select.placeholder'))}</option>` +
                items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
        } catch (err) {
            console.error(t('energyreport.console.load_circuit_failed'), err);
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
                const tag = n.sid ? t('energyreport.circuit.meter_tag') : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function buildRequest() {
        const circuitId = parseInt(document.getElementById('erCircuit').value, 10);
        if (!circuitId) { alert(t('energyreport.alert.select_circuit')); return null; }
        const g = document.getElementById('erGranularity').value;
        let startStr, endStr;
        if (g === 'hour') {
            const hsDate = document.getElementById('erHourStartDate').value;
            const heDate = document.getElementById('erHourEndDate').value;
            const hsHour = document.getElementById('erHourStartHour').value;
            const heHour = document.getElementById('erHourEndHour').value;
            if (!hsDate || !heDate || hsHour === '' || heHour === '') {
                alert(t('energyreport.alert.hour_order')); return null;
            }
            const hsHH = String(parseInt(hsHour, 10)).padStart(2, '0');
            const heHH = String(parseInt(heHour, 10)).padStart(2, '0');
            startStr = `${hsDate}T${hsHH}:00:00`;
            endStr = `${heDate}T${heHH}:00:00`;
            if (new Date(endStr) < new Date(startStr)) { alert(t('energyreport.alert.hour_order')); return null; }
        } else if (g === 'day') {
            const ds = document.getElementById('erDayStart').value;
            const de = document.getElementById('erDayEnd').value;
            if (!ds || !de) { alert(t('energyreport.alert.day_order')); return null; }
            startStr = ds + 'T00:00:00';
            endStr = de + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('energyreport.alert.day_order')); return null; }
        } else if (g === 'month') {
            startStr = document.getElementById('erMonthStart').value + '-01T00:00:00';
            endStr = document.getElementById('erMonthEnd').value + '-01T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('energyreport.alert.month_order')); return null; }
        } else if (g === 'year') {
            const ys = document.getElementById('erYearStart').value;
            const ye = document.getElementById('erYearEnd').value;
            if (parseInt(ye, 10) < parseInt(ys, 10)) { alert(t('energyreport.alert.year_order')); return null; }
            startStr = ys + '-01-01T00:00:00';
            endStr = ye + '-01-01T00:00:00';
        } else if (g === 'shift') {
            const ss = document.getElementById('erShiftStart').value;
            const se = document.getElementById('erShiftEnd').value;
            if (!ss || !se) { alert(t('energyreport.alert.day_order')); return null; }
            startStr = ss + 'T00:00:00';
            endStr = se + 'T00:00:00';
            if (new Date(endStr) < new Date(startStr)) { alert(t('energyreport.alert.day_order')); return null; }
        }
        // 去年同期比較：僅月粒度有效（checkbox 只在月粒度顯示）
        const yoyEl = document.getElementById('erYoy');
        const yoy = (g === 'month') && !!(yoyEl && yoyEl.checked);
        return { circuitId, granularity: g, start: startStr, end: endStr, yoy };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        const nLoadCols = req.yoy ? 5 : 2;
        document.getElementById('erTableBody').innerHTML =
            `<tr><td colspan="${nLoadCols}" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('energyreport.table.querying'))}</td></tr>`;
        document.getElementById('btnExport').disabled = true;

        try {
            const res = await fetch('/EnergyReport/api/query', {
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
            const nErrCols = req.yoy ? 5 : 2;
            document.getElementById('erTableBody').innerHTML =
                `<tr><td colspan="${nErrCols}" class="text-center text-danger py-3">${escapeHtml(t('energyreport.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    // kWh 顯示一律小數一位（0 → 0.0，整欄右對齊才切齊），千分位保留。
    // 計算與 API 回傳精度不變 —— 這裡只是顯示層。
    function fmtKwh(v) {
        if (v == null) return '--';
        return Number(v).toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    // 差異：帶正負號的 kWh
    function fmtDiff(v) {
        if (v == null) return '--';
        return (v > 0 ? '+' : '') + fmtKwh(v);
    }

    // 增減%：漲紅（caret-up）跌綠（caret-down），null → --；三角圖示由 UiState 提供
    function pctCellHtml(p) {
        return UiState.pctHtml(p, 'er-yoy-up', 'er-yoy-down');
    }

    // 依 YOY 與否重建表頭；回傳欄數（供空狀態 colspan 用）
    function applyTableHead(isYoy) {
        const head = document.getElementById('erTableHeadRow');
        const period = `<th>${escapeHtml(t('energyreport.table.col_period'))}</th>`;
        const kwh = `<th class="text-end">${escapeHtml(t('energyreport.table.col_kwh'))}</th>`;
        if (!isYoy) { head.innerHTML = period + kwh; return 2; }
        head.innerHTML = period + kwh +
            `<th class="text-end">${escapeHtml(t('energyreport.table.col_lastyear'))}</th>` +
            `<th class="text-end">${escapeHtml(t('energyreport.table.col_diff'))}</th>` +
            `<th class="text-end">${escapeHtml(t('energyreport.table.col_pct'))}</th>`;
        return 5;
    }

    function renderTable(data) {
        const isYoy = !!data.isYoy;
        const nCols = applyTableHead(isYoy);
        const tbody = document.getElementById('erTableBody');
        const totalLabel = escapeHtml(t('energyreport.table.total'));
        const staleTipRaw = t('energyreport.tooltip.stale');
        const staleTip = escapeHtml(staleTipRaw);
        const partialTipRaw = t('energyreport.tooltip.partial');
        const partialTip = escapeHtml(partialTipRaw);
        // 斷線（琥珀三角）與部分區間（藍色半圓）兩種標記，互斥
        const markOf = b => b.isStale ? UiState.staleMarkHtml('er-stale-mark', staleTipRaw)
            : b.isPartial ? UiState.partialMarkHtml('er-partial-mark', partialTipRaw) : '';
        const rowAttrOf = b => b.isStale ? ` title="${staleTip}"` : b.isPartial ? ` title="${partialTip}"` : '';

        if (!data.buckets || data.buckets.length === 0) {
            tbody.innerHTML = `<tr><td colspan="${nCols}" class="text-center text-muted py-3">${escapeHtml(t('energyreport.table.no_data'))}</td></tr>`;
        } else if (!isYoy) {
            tbody.innerHTML = data.buckets.map(b => {
                const mark = markOf(b);
                const rowAttr = rowAttrOf(b);
                return `
                <tr${rowAttr}>
                    <td>${escapeHtml(b.szLabel)}${mark}</td>
                    <td class="text-end">${fmtKwh(b.dKwh)}</td>
                </tr>`;
            }).join('') +
                `<tr class="er-total"><td>${totalLabel}</td><td class="text-end">${fmtKwh(data.dTotalKwh)}</td></tr>`;
        } else {
            // YOY：期別 / 本期 / 去年同期 / 差異 / 增減%
            const rows = data.buckets.map(b => {
                const mark = markOf(b);
                const rowAttr = rowAttrOf(b);
                return `
                <tr${rowAttr}>
                    <td>${escapeHtml(b.szLabel)}${mark}</td>
                    <td class="text-end">${fmtKwh(b.dKwh)}</td>
                    <td class="text-end">${fmtKwh(b.dLastYearKwh)}</td>
                    <td class="text-end">${fmtDiff(b.dDiffKwh)}</td>
                    <td class="text-end">${pctCellHtml(b.dPctChange)}</td>
                </tr>`;
            }).join('');
            // 合計列 YOY 只加總「有去年同期」的可比 bucket
            const matched = data.buckets.filter(b => b.dLastYearKwh != null);
            let lyTotalHtml = '--', diffTotalHtml = '--', pctTotalHtml = pctCellHtml(null);
            if (matched.length) {
                const curMatched = matched.reduce((s, b) => s + b.dKwh, 0);
                const lyTotal = matched.reduce((s, b) => s + b.dLastYearKwh, 0);
                const diffTotal = curMatched - lyTotal;
                lyTotalHtml = fmtKwh(lyTotal);
                diffTotalHtml = fmtDiff(diffTotal);
                pctTotalHtml = pctCellHtml(lyTotal === 0 ? null : diffTotal / Math.abs(lyTotal) * 100);
            }
            tbody.innerHTML = rows +
                `<tr class="er-total">
                    <td>${totalLabel}</td>
                    <td class="text-end">${fmtKwh(data.dTotalKwh)}</td>
                    <td class="text-end">${lyTotalHtml}</td>
                    <td class="text-end">${diffTotalHtml}</td>
                    <td class="text-end">${pctTotalHtml}</td>
                </tr>`;
        }
        document.getElementById('erTotal').textContent = fmtKwh(data.dTotalKwh);
        UiState.setWarnText('erWarnText', data.isHasWarning
            ? t('energyreport.warning.kwh_overflow') : '');
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
            ? t('energyreport.button.show_total')
            : t('energyreport.button.show_breakdown');
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
        const ctx = document.getElementById('erChart').getContext('2d');

        const bBreakdown = g_chartMode === 'breakdown'
            && Array.isArray(data.children) && data.children.length > 1;

        let datasets;
        let tooltipCallbacks;
        let bStacked;

        // 斷線 / 部分區間提示：依 bucket index 查主迴路 buckets[idx]，兩種模式共用
        const staleTip = t('energyreport.tooltip.stale');
        const partialTip = t('energyreport.tooltip.partial');
        const staleAfterBody = function (items) {
            if (!items || !items.length) return undefined;
            const b = data.buckets[items[0].dataIndex];
            if (!b) return undefined;
            return b.isStale ? staleTip : b.isPartial ? partialTip : undefined;
        };

        if (bBreakdown) {
            bStacked = true;
            datasets = data.children.map((child, i) => ({
                label: child.szName,
                data: child.dKwhPerBucket,
                backgroundColor: pickColor(i, 0.7),
                borderColor: pickColor(i, 1),
                borderWidth: 1
            }));
            tooltipCallbacks = {
                // 每根 bar 顯示「子名稱: 值 kWh」
                label: function (ctx) {
                    const v = ctx.parsed.y;
                    return `${ctx.dataset.label}: ${fmtKwh(v == null ? 0 : v)} kWh`;
                },
                // footer 顯示該 bucket 的合計
                footer: function (items) {
                    let sum = 0;
                    items.forEach(it => { if (it.parsed && it.parsed.y != null) sum += it.parsed.y; });
                    return t('energyreport.chart.breakdown_total_label', { 0: fmtKwh(sum) });
                },
                afterBody: staleAfterBody
            };
        } else {
            bStacked = false;
            tooltipCallbacks = { afterBody: staleAfterBody };
            const values = data.buckets.map(b => b.dKwh);
            datasets = [{
                label: t('energyreport.chart.dataset_label', { 0: data.szCircuitName }),
                data: values,
                backgroundColor: 'rgba(13, 110, 253, 0.6)',
                borderColor: 'rgba(13, 110, 253, 1)',
                borderWidth: 1
            }];
            // YOY：疊一組淡橘「去年同期」對照序列（並列 bar）
            if (data.isYoy) {
                datasets.push({
                    label: t('energyreport.chart.lastyear_label'),
                    data: data.buckets.map(b => b.dLastYearKwh),
                    backgroundColor: 'rgba(255, 159, 64, 0.45)',
                    borderColor: 'rgba(255, 159, 64, 1)',
                    borderWidth: 1
                });
            }
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
                    y: { stacked: bStacked, title: { display: true, text: t('energyreport.chart.y_axis') } },
                    x: { stacked: bStacked, title: { display: true, text: t('energyreport.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/EnergyReport/api/export', {
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
            let szFileName = 'EnergyReport.xlsx';
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
            alert(t('energyreport.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._er = { query, exportExcel, toggleBreakdown };
})();
