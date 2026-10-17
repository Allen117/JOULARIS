// 空壓機能源申報頁邏輯 — 申報報表設定 CRUD + 年度 12 曆月查詢/匯出（頁面固定格式）
// 與冰水版 energydeclaration.js 同構：第二來源換成產氣量表迴路（Nm³），效率換成比能耗 kWh/Nm³。
// 後端錯誤一律回 { code }，此處以 airdeclaration.err.{code} 依語系翻譯。
(function () {
    'use strict';

    let g_configs = [];
    let g_energyCircuits = [];
    let g_airCircuits = [];
    let g_chart = null;
    let g_editingId = null;   // null = 新增模式
    let g_modal = null;

    document.addEventListener('DOMContentLoaded', () => {
        initDefaults();
        // 等 i18n 字典載入後再 fetch（placeholder 字串需要翻譯）
        if (window.i18n) {
            window.i18n.ready(loadAll);
        } else {
            loadAll();
        }
    });

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    // 後端錯誤代碼 → 當前語系訊息；查無對應 key 時回原始代碼
    function errorText(code) {
        if (!code) return t('airdeclaration.err.unknown');
        const key = 'airdeclaration.err.' + code;
        const s = t(key);
        return s === key ? code : s;
    }

    async function readError(res) {
        const err = await res.json().catch(() => ({}));
        return errorText(err.code || res.statusText);
    }

    function initDefaults() {
        const yearEl = document.getElementById('acdYear');
        yearEl.value = yearEl.dataset.defaultYear || new Date().getFullYear();
    }

    // ---------- 載入設定 + 兩棵迴路樹 ----------

    async function loadAll() {
        try {
            const [configRes, energyRes, airRes] = await Promise.all([
                fetch('/AirCompressorDeclaration/api/reports'),
                fetch('/AirCompressorDeclaration/api/circuits'),
                fetch('/AirCompressorDeclaration/api/airmetercircuits')
            ]);
            g_configs = await configRes.json();
            g_energyCircuits = await energyRes.json();
            g_airCircuits = await airRes.json();
            renderConfigTable();
            renderReportSelect();
            fillCircuitSelect('acdModalEnergyCircuit', g_energyCircuits, t('airdeclaration.circuit.meter_tag'));
            fillCircuitSelect('acdModalAirCircuit', g_airCircuits, t('airdeclaration.circuit.air_meter_tag'));
        } catch (err) {
            console.error(t('airdeclaration.console.load_failed'), err);
        }
    }

    function circuitName(circuits, id) {
        const c = circuits.find(x => x.id === id);
        return c ? c.name : null;
    }

    // 扁平清單 → 縮排下拉項目；實體表（有 SID）加 meterTag 字尾、群組無字尾靠縮排辨識
    function buildIndentedList(nodes, meterTag) {
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
                const tag = n.sid ? meterTag : '';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function fillCircuitSelect(selId, circuits, meterTag) {
        const sel = document.getElementById(selId);
        const items = buildIndentedList(circuits, meterTag);
        const placeholder = items.length === 0
            ? t('airdeclaration.modal.no_circuit')
            : t('airdeclaration.modal.select_circuit');
        sel.innerHTML = `<option value="">${escapeHtml(placeholder)}</option>` +
            items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
    }

    // ---------- 設定清單 ----------

    function missingCell() {
        return `<span class="acd-missing"><i class="fas fa-exclamation-triangle me-1"></i>${escapeHtml(t('airdeclaration.config.deleted_source'))}</span>`;
    }

    function renderConfigTable() {
        const tbody = document.getElementById('acdConfigBody');
        if (!g_configs || g_configs.length === 0) {
            tbody.innerHTML = `<tr><td colspan="5" class="text-center text-muted py-3">${escapeHtml(t('airdeclaration.config.empty'))}</td></tr>`;
            return;
        }
        tbody.innerHTML = g_configs.map(c => {
            const eName = circuitName(g_energyCircuits, c.energyCircuitId);
            const aName = circuitName(g_airCircuits, c.airMeterCircuitId);
            return `
                <tr>
                    <td class="fw-semibold">${escapeHtml(c.name)}</td>
                    <td>${eName ? escapeHtml(eName) : missingCell()}</td>
                    <td>${aName ? escapeHtml(aName) : missingCell()}</td>
                    <td class="text-muted">${escapeHtml(c.description || '')}</td>
                    <td class="text-end">
                        <button class="btn btn-outline-primary btn-sm me-1" onclick="window._acd.openEditModal(${c.id})" title="${escapeHtml(t('airdeclaration.button.edit'))}">
                            <i class="fas fa-edit"></i>
                        </button>
                        <button class="btn btn-outline-danger btn-sm" onclick="window._acd.deleteConfig(${c.id})" title="${escapeHtml(t('airdeclaration.button.delete'))}">
                            <i class="fas fa-trash-alt"></i>
                        </button>
                    </td>
                </tr>`;
        }).join('');
    }

    function renderReportSelect() {
        const sel = document.getElementById('acdReport');
        const prev = sel.value;
        sel.innerHTML = (!g_configs || g_configs.length === 0)
            ? `<option value="">${escapeHtml(t('airdeclaration.select.empty'))}</option>`
            : `<option value="">${escapeHtml(t('airdeclaration.select.placeholder'))}</option>` +
            g_configs.map(c => `<option value="${c.id}">${escapeHtml(c.name)}</option>`).join('');
        if (prev && g_configs.some(c => String(c.id) === prev)) sel.value = prev;
    }

    // ---------- 新增 / 編輯 Modal ----------

    function getModal() {
        if (!g_modal) g_modal = new bootstrap.Modal(document.getElementById('acdConfigModal'));
        return g_modal;
    }

    function openCreateModal() {
        g_editingId = null;
        document.getElementById('acdModalTitle').textContent = t('airdeclaration.modal.title_add');
        document.getElementById('acdModalName').value = '';
        document.getElementById('acdModalEnergyCircuit').value = '';
        document.getElementById('acdModalAirCircuit').value = '';
        document.getElementById('acdModalDescription').value = '';
        getModal().show();
    }

    function openEditModal(id) {
        const c = g_configs.find(x => x.id === id);
        if (!c) return;
        g_editingId = id;
        document.getElementById('acdModalTitle').textContent = t('airdeclaration.modal.title_edit');
        document.getElementById('acdModalName').value = c.name;
        document.getElementById('acdModalEnergyCircuit').value = String(c.energyCircuitId);
        document.getElementById('acdModalAirCircuit').value = String(c.airMeterCircuitId);
        document.getElementById('acdModalDescription').value = c.description || '';
        getModal().show();
    }

    async function saveConfig() {
        const name = document.getElementById('acdModalName').value.trim();
        const energyCircuitId = parseInt(document.getElementById('acdModalEnergyCircuit').value, 10);
        const airMeterCircuitId = parseInt(document.getElementById('acdModalAirCircuit').value, 10);
        const description = document.getElementById('acdModalDescription').value.trim();

        if (!name) { alert(t('airdeclaration.alert.name_required')); return; }
        if (!energyCircuitId) { alert(t('airdeclaration.alert.energy_required')); return; }
        if (!airMeterCircuitId) { alert(t('airdeclaration.alert.air_required')); return; }

        const url = g_editingId == null
            ? '/AirCompressorDeclaration/api/reports'
            : `/AirCompressorDeclaration/api/reports/${g_editingId}`;
        const method = g_editingId == null ? 'POST' : 'PUT';

        try {
            const res = await fetch(url, {
                method,
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, energyCircuitId, airMeterCircuitId, description })
            });
            if (!res.ok) throw new Error(await readError(res));
            getModal().hide();
            await loadAll();
        } catch (err) {
            alert(t('airdeclaration.alert.save_failed', { 0: err.message }));
        }
    }

    async function deleteConfig(id) {
        const c = g_configs.find(x => x.id === id);
        if (!c) return;
        if (!confirm(t('airdeclaration.config.confirm_delete', { 0: c.name }))) return;
        try {
            const res = await fetch(`/AirCompressorDeclaration/api/reports/${id}`, { method: 'DELETE' });
            if (!res.ok) throw new Error(await readError(res));
            await loadAll();
        } catch (err) {
            alert(t('airdeclaration.alert.delete_failed', { 0: err.message }));
        }
    }

    // ---------- 查詢 / 匯出 ----------

    function buildRequest() {
        const reportId = parseInt(document.getElementById('acdReport').value, 10);
        if (!reportId) { alert(t('airdeclaration.alert.select_report')); return null; }
        const year = parseInt(document.getElementById('acdYear').value, 10);
        if (!year || year < 2000 || year > 2100) {
            alert(t('airdeclaration.alert.year_invalid')); return null;
        }
        return { reportId, year };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('acdTableBody').innerHTML =
            `<tr><td colspan="4" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('airdeclaration.table.querying'))}</td></tr>`;
        document.getElementById('btnAcdExport').disabled = true;

        try {
            const res = await fetch('/AirCompressorDeclaration/api/query', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) throw new Error(await readError(res));
            const data = await res.json();
            renderTable(data);
            renderChart(data);
            document.getElementById('btnAcdExport').disabled = false;
        } catch (err) {
            document.getElementById('acdTableBody').innerHTML =
                `<tr><td colspan="4" class="text-center text-danger py-3">${escapeHtml(t('airdeclaration.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    // kWh / Nm³ 顯示一律小數一位 + 千分位，與 Excel「#,##0.0」對齊（只是顯示層，API 精度不變）
    function fmt1(v) {
        if (v == null) return '--';
        return Number(v).toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    // 比能耗 kWh/Nm³ 量級約 0.1，維持 3 位小數
    function fmtEff(v) {
        return (v == null) ? '--' : Number(v).toFixed(3);
    }

    function staleMark(isStale, tipRaw) {
        return isStale ? UiState.staleMarkHtml('acd-stale-mark', tipRaw) : '';
    }

    function renderTable(data) {
        const tbody = document.getElementById('acdTableBody');
        if (!data.buckets || data.buckets.length === 0) {
            tbody.innerHTML = `<tr><td colspan="4" class="text-center text-muted py-3">${escapeHtml(t('airdeclaration.table.no_data'))}</td></tr>`;
        } else {
            const totalLabel = escapeHtml(t('airdeclaration.table.total'));
            const kwhTipRaw = t('airdeclaration.tooltip.kwh_stale');
            const nm3TipRaw = t('airdeclaration.tooltip.nm3_stale');
            const kwhTip = escapeHtml(kwhTipRaw);
            const nm3Tip = escapeHtml(nm3TipRaw);
            tbody.innerHTML = data.buckets.map(b => `
                <tr>
                    <td>${escapeHtml(b.szLabel)}</td>
                    <td class="text-end"${b.isKwhStale ? ` title="${kwhTip}"` : ''}>${fmt1(b.dKwh)}${staleMark(b.isKwhStale, kwhTipRaw)}</td>
                    <td class="text-end"${b.isNm3Stale ? ` title="${nm3Tip}"` : ''}>${fmt1(b.dNm3)}${staleMark(b.isNm3Stale, nm3TipRaw)}</td>
                    <td class="text-end">${fmtEff(b.dKwhPerNm3)}</td>
                </tr>`).join('') +
                `<tr class="acd-total">
                    <td>${totalLabel}</td>
                    <td class="text-end">${fmt1(data.dTotalKwh)}</td>
                    <td class="text-end">${fmt1(data.dTotalNm3)}</td>
                    <td class="text-end">${fmtEff(data.dTotalKwhPerNm3)}</td>
                </tr>`;
        }
        document.getElementById('acdTotalKwh').textContent = fmt1(data.dTotalKwh);
        document.getElementById('acdTotalNm3').textContent = fmt1(data.dTotalNm3);
        document.getElementById('acdTotalEff').textContent = fmtEff(data.dTotalKwhPerNm3);

        const warns = [];
        if (data.isHasKwhWarning) warns.push(t('airdeclaration.warning.kwh'));
        if (data.isHasNm3Warning) warns.push(t('airdeclaration.warning.nm3'));
        UiState.setWarnText('acdWarnText', warns.join(' / '));
    }

    // 雙 Y 軸單位水平畫在左右軸正上方（Chart.js 的 y 軸 title 固定直立）
    const axisUnitPlugin = {
        id: 'acdAxisUnits',
        afterDraw(chart) {
            const y = chart.scales.y;
            const y1 = chart.scales.y1;
            const ctx2 = chart.ctx;
            ctx2.save();
            ctx2.font = '12px ' + (Chart.defaults.font.family || 'sans-serif');
            ctx2.fillStyle = Chart.defaults.color || '#666';
            ctx2.textBaseline = 'bottom';
            if (y) {
                ctx2.textAlign = 'right';
                ctx2.fillText('kWh', y.right, y.top - 6);
            }
            if (y1) {
                ctx2.textAlign = 'left';
                ctx2.fillText('Nm³', y1.left, y1.top - 6);
            }
            ctx2.restore();
        }
    };

    function renderChart(data) {
        const labels = data.buckets.map(b => b.szLabel);
        if (g_chart) g_chart.destroy();
        const ctx = document.getElementById('acdChart').getContext('2d');

        g_chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [
                    {
                        type: 'bar',
                        label: t('airdeclaration.chart.kwh_label'),
                        data: data.buckets.map(b => b.dKwh),
                        backgroundColor: 'rgba(13, 110, 253, 0.6)',
                        borderColor: 'rgba(13, 110, 253, 1)',
                        borderWidth: 1,
                        yAxisID: 'y'
                    },
                    {
                        type: 'line',
                        label: t('airdeclaration.chart.nm3_label'),
                        data: data.buckets.map(b => b.dNm3),
                        borderColor: 'rgba(32, 201, 151, 1)',
                        backgroundColor: 'rgba(32, 201, 151, 0.2)',
                        borderWidth: 2,
                        pointRadius: 3,
                        tension: 0.2,
                        yAxisID: 'y1'
                    }
                ]
            },
            plugins: [axisUnitPlugin],
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                layout: { padding: { top: 22 } },
                plugins: {
                    legend: { display: true },
                    tooltip: {
                        callbacks: {
                            label: function (ctx) {
                                return `${ctx.dataset.label}: ${fmt1(ctx.parsed.y)}`;
                            },
                            // footer 顯示該月比能耗
                            footer: function (items) {
                                if (!items.length) return '';
                                const b = data.buckets[items[0].dataIndex];
                                return t('airdeclaration.chart.specific_energy_label', { 0: fmtEff(b ? b.dKwhPerNm3 : null) });
                            },
                            // 任一側資料不完整時附註提示
                            afterBody: function (items) {
                                if (!items.length) return undefined;
                                const b = data.buckets[items[0].dataIndex];
                                if (!b) return undefined;
                                const notes = [];
                                if (b.isKwhStale) notes.push(t('airdeclaration.tooltip.kwh_stale'));
                                if (b.isNm3Stale) notes.push(t('airdeclaration.tooltip.nm3_stale'));
                                return notes.length ? notes : undefined;
                            }
                        }
                    }
                },
                scales: {
                    // 不指定 beginAtZero — 含負值的虛擬迴路（A+B-C）需正確顯示負 bar
                    y: { position: 'left' },
                    y1: {
                        position: 'right',
                        grid: { drawOnChartArea: false }
                    },
                    x: { title: { display: true, text: t('airdeclaration.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/AirCompressorDeclaration/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) throw new Error(await readError(res));
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'AirCompressorDeclaration.xlsx';
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
            alert(t('airdeclaration.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._acd = { openCreateModal, openEditModal, saveConfig, deleteConfig, query, exportExcel };
})();
