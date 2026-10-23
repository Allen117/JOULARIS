// 能源申報頁邏輯 — 申報報表設定 CRUD + 年度 12 曆月查詢/匯出（頁面固定格式）
(function () {
    'use strict';

    let g_configs = [];
    let g_energyCircuits = [];
    let g_waterCircuits = [];
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

    function initDefaults() {
        const today = (window._edInit && window._edInit.today) || new Date().toISOString().slice(0, 10);
        document.getElementById('edYear').value = new Date(today).getFullYear();
    }

    // ---------- 載入設定 + 兩棵迴路樹 ----------

    async function loadAll() {
        try {
            const [configRes, energyRes, waterRes] = await Promise.all([
                fetch('/EnergyDeclaration/api/reports'),
                fetch('/EnergyDeclaration/api/circuits'),
                fetch('/EnergyDeclaration/api/watercircuits')
            ]);
            g_configs = await configRes.json();
            g_energyCircuits = await energyRes.json();
            g_waterCircuits = await waterRes.json();
            renderConfigTable();
            renderReportSelect();
            fillCircuitSelect('edModalEnergyCircuit', g_energyCircuits);
            fillCircuitSelect('edModalWaterCircuit', g_waterCircuits);
        } catch (err) {
            console.error(t('energydeclaration.console.load_failed'), err);
        }
    }

    function circuitName(circuits, id) {
        const c = circuits.find(x => x.id === id);
        return c ? c.name : null;
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
                const tag = n.sid ? ' [⚡]' : ' [📁]';
                out.push({ id: n.id, label: prefix + n.name + tag });
                walk(String(n.id), depth + 1);
            });
        }
        walk('null', 0);
        return out;
    }

    function fillCircuitSelect(selId, circuits) {
        const sel = document.getElementById(selId);
        const items = buildIndentedList(circuits);
        sel.innerHTML = `<option value="">${escapeHtml(t('energydeclaration.modal.select_circuit'))}</option>` +
            items.map(it => `<option value="${it.id}">${escapeHtml(it.label)}</option>`).join('');
    }

    // ---------- 設定清單 ----------

    function renderConfigTable() {
        const tbody = document.getElementById('edConfigBody');
        if (!g_configs || g_configs.length === 0) {
            tbody.innerHTML = `<tr><td colspan="5" class="text-center text-muted py-3">${escapeHtml(t('energydeclaration.config.empty'))}</td></tr>`;
            return;
        }
        const missing = escapeHtml(t('energydeclaration.config.deleted_circuit'));
        tbody.innerHTML = g_configs.map(c => {
            const eName = circuitName(g_energyCircuits, c.energyCircuitId);
            const wName = circuitName(g_waterCircuits, c.waterCircuitId);
            return `
                <tr>
                    <td class="fw-semibold">${escapeHtml(c.name)}</td>
                    <td>${eName ? escapeHtml(eName) : `<span class="ed-missing"><i class="fas fa-exclamation-triangle me-1"></i>${missing}</span>`}</td>
                    <td>${wName ? escapeHtml(wName) : `<span class="ed-missing"><i class="fas fa-exclamation-triangle me-1"></i>${missing}</span>`}</td>
                    <td class="text-muted">${escapeHtml(c.description || '')}</td>
                    <td class="text-end">
                        <button class="btn btn-outline-primary btn-sm me-1" onclick="window._ed.openEditModal(${c.id})" title="${escapeHtml(t('energydeclaration.button.edit'))}">
                            <i class="fas fa-edit"></i>
                        </button>
                        <button class="btn btn-outline-danger btn-sm" onclick="window._ed.deleteConfig(${c.id})" title="${escapeHtml(t('energydeclaration.button.delete'))}">
                            <i class="fas fa-trash-alt"></i>
                        </button>
                    </td>
                </tr>`;
        }).join('');
    }

    function renderReportSelect() {
        const sel = document.getElementById('edReport');
        const prev = sel.value;
        sel.innerHTML = (!g_configs || g_configs.length === 0)
            ? `<option value="">${escapeHtml(t('energydeclaration.select.empty'))}</option>`
            : `<option value="">${escapeHtml(t('energydeclaration.select.placeholder'))}</option>` +
            g_configs.map(c => `<option value="${c.id}">${escapeHtml(c.name)}</option>`).join('');
        if (prev && g_configs.some(c => String(c.id) === prev)) sel.value = prev;
    }

    // ---------- 新增 / 編輯 Modal ----------

    function getModal() {
        if (!g_modal) g_modal = new bootstrap.Modal(document.getElementById('edConfigModal'));
        return g_modal;
    }

    function openCreateModal() {
        g_editingId = null;
        document.getElementById('edModalTitle').textContent = t('energydeclaration.modal.title_add');
        document.getElementById('edModalName').value = '';
        document.getElementById('edModalEnergyCircuit').value = '';
        document.getElementById('edModalWaterCircuit').value = '';
        document.getElementById('edModalDescription').value = '';
        getModal().show();
    }

    function openEditModal(id) {
        const c = g_configs.find(x => x.id === id);
        if (!c) return;
        g_editingId = id;
        document.getElementById('edModalTitle').textContent = t('energydeclaration.modal.title_edit');
        document.getElementById('edModalName').value = c.name;
        document.getElementById('edModalEnergyCircuit').value = String(c.energyCircuitId);
        document.getElementById('edModalWaterCircuit').value = String(c.waterCircuitId);
        document.getElementById('edModalDescription').value = c.description || '';
        getModal().show();
    }

    async function saveConfig() {
        const name = document.getElementById('edModalName').value.trim();
        const energyCircuitId = parseInt(document.getElementById('edModalEnergyCircuit').value, 10);
        const waterCircuitId = parseInt(document.getElementById('edModalWaterCircuit').value, 10);
        const description = document.getElementById('edModalDescription').value.trim();

        if (!name) { alert(t('energydeclaration.alert.name_required')); return; }
        if (!energyCircuitId) { alert(t('energydeclaration.alert.energy_required')); return; }
        if (!waterCircuitId) { alert(t('energydeclaration.alert.water_required')); return; }

        const url = g_editingId == null
            ? '/EnergyDeclaration/api/reports'
            : `/EnergyDeclaration/api/reports/${g_editingId}`;
        const method = g_editingId == null ? 'POST' : 'PUT';

        try {
            const res = await fetch(url, {
                method,
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, energyCircuitId, waterCircuitId, description })
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            getModal().hide();
            await loadAll();
        } catch (err) {
            alert(t('energydeclaration.alert.save_failed', { 0: err.message }));
        }
    }

    async function deleteConfig(id) {
        const c = g_configs.find(x => x.id === id);
        if (!c) return;
        if (!confirm(t('energydeclaration.config.confirm_delete', { 0: c.name }))) return;
        try {
            const res = await fetch(`/EnergyDeclaration/api/reports/${id}`, { method: 'DELETE' });
            if (!res.ok) throw new Error(res.statusText);
            await loadAll();
        } catch (err) {
            alert(t('energydeclaration.alert.delete_failed', { 0: err.message }));
        }
    }

    // ---------- 查詢 / 匯出 ----------

    function buildRequest() {
        const reportId = parseInt(document.getElementById('edReport').value, 10);
        if (!reportId) { alert(t('energydeclaration.alert.select_report')); return null; }
        const year = parseInt(document.getElementById('edYear').value, 10);
        if (!year || year < 2000 || year > 2100) {
            alert(t('energydeclaration.alert.year_invalid')); return null;
        }
        return { reportId, year };
    }

    async function query() {
        const req = buildRequest();
        if (!req) return;

        document.getElementById('edTableBody').innerHTML =
            `<tr><td colspan="4" class="text-center text-muted py-3"><div class="spinner-border spinner-border-sm text-primary"></div> ${escapeHtml(t('energydeclaration.table.querying'))}</td></tr>`;
        document.getElementById('btnEdExport').disabled = true;

        try {
            const res = await fetch('/EnergyDeclaration/api/query', {
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
            document.getElementById('btnEdExport').disabled = false;
        } catch (err) {
            document.getElementById('edTableBody').innerHTML =
                `<tr><td colspan="4" class="text-center text-danger py-3">${escapeHtml(t('energydeclaration.alert.query_failed', { 0: err.message }))}</td></tr>`;
        }
    }

    // kWh / RT·h 顯示一律小數一位（0 → 0.0）+ 千分位，與本頁 Excel 匯出的
    // 「#,##0.0」對齊。計算與 API 回傳精度不變 —— 這裡只是顯示層。
    function fmt1(v) {
        if (v == null) return '--';
        return Number(v).toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    // 效率比值 kWh/RT·h 量級約 0.5～1.5，收成 1 位會失去解析度，維持 3 位
    function fmtEff(v) {
        return (v == null) ? '--' : v.toFixed(3);
    }

    function renderTable(data) {
        const tbody = document.getElementById('edTableBody');
        if (!data.buckets || data.buckets.length === 0) {
            tbody.innerHTML = `<tr><td colspan="4" class="text-center text-muted py-3">${escapeHtml(t('energydeclaration.table.no_data'))}</td></tr>`;
        } else {
            const totalLabel = escapeHtml(t('energydeclaration.table.total'));
            const staleTip = escapeHtml(t('energydeclaration.tooltip.stale'));
            const partialTip = escapeHtml(t('energydeclaration.tooltip.partial'));
            tbody.innerHTML = data.buckets.map(b => {
                const tip = b.isKwhStale ? staleTip : b.isKwhPartial ? partialTip : '';
                const mark = b.isKwhStale
                    ? ` <span class="ed-stale-mark" title="${staleTip}">\u26a0</span>`
                    : b.isKwhPartial ? ` <span class="ed-partial-mark" title="${partialTip}">\u25d0</span>` : '';
                return `
                <tr>
                    <td>${escapeHtml(b.szLabel)}</td>
                    <td class="text-end"${tip ? ` title="${tip}"` : ''}>${fmt1(b.dKwh)}${mark}</td>
                    <td class="text-end">${fmt1(b.dRtHour)}</td>
                    <td class="text-end">${fmtEff(b.dKwhPerRtHour)}</td>
                </tr>`;
            }).join('') +
                `<tr class="ed-total">
                    <td>${totalLabel}</td>
                    <td class="text-end">${fmt1(data.dTotalKwh)}</td>
                    <td class="text-end">${fmt1(data.dTotalRtHour)}</td>
                    <td class="text-end">${fmtEff(data.dTotalKwhPerRtHour)}</td>
                </tr>`;
        }
        document.getElementById('edTotalKwh').textContent = fmt1(data.dTotalKwh);
        document.getElementById('edTotalRt').textContent = fmt1(data.dTotalRtHour);
        document.getElementById('edTotalEff').textContent = fmtEff(data.dTotalKwhPerRtHour);

        const warns = [];
        if (data.isHasKwhWarning) warns.push(t('energydeclaration.warning.kwh'));
        if (data.isHasRtWarning) warns.push(t('energydeclaration.warning.rt'));
        document.getElementById('edWarnText').textContent = warns.join(' / ');
    }

    // 雙 Y 軸單位改水平顯示：Chart.js 的 y 軸 title 固定直立，
    // 改用 plugin 把單位水平畫在左右軸正上方（搭配 layout.padding.top 預留空間）
    const axisUnitPlugin = {
        id: 'edAxisUnits',
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
                ctx2.fillText('RT·h', y1.left, y1.top - 6);
            }
            ctx2.restore();
        }
    };

    function renderChart(data) {
        const labels = data.buckets.map(b => b.szLabel);
        if (g_chart) g_chart.destroy();
        const ctx = document.getElementById('edChart').getContext('2d');

        g_chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [
                    {
                        type: 'bar',
                        label: t('energydeclaration.chart.kwh_label'),
                        data: data.buckets.map(b => b.dKwh),
                        backgroundColor: 'rgba(13, 110, 253, 0.6)',
                        borderColor: 'rgba(13, 110, 253, 1)',
                        borderWidth: 1,
                        yAxisID: 'y'
                    },
                    {
                        type: 'line',
                        label: t('energydeclaration.chart.rt_label'),
                        data: data.buckets.map(b => b.dRtHour),
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
                            // kWh / RT·h 兩條資料集都收成小數一位（Chart.js 預設會直接印出全精度）
                            label: function (ctx) {
                                return `${ctx.dataset.label}: ${fmt1(ctx.parsed.y)}`;
                            },
                            // footer 顯示該時段效率
                            footer: function (items) {
                                if (!items.length) return '';
                                const b = data.buckets[items[0].dataIndex];
                                return t('energydeclaration.chart.efficiency_label', { 0: fmtEff(b ? b.dKwhPerRtHour : null) });
                            },
                            // kWh 側斷線 / 部分區間時附註提示（依 bucket index 查 isKwhStale / isKwhPartial）
                            afterBody: function (items) {
                                if (!items.length) return undefined;
                                const b = data.buckets[items[0].dataIndex];
                                if (!b) return undefined;
                                return b.isKwhStale ? t('energydeclaration.tooltip.stale')
                                    : b.isKwhPartial ? t('energydeclaration.tooltip.partial') : undefined;
                            }
                        }
                    }
                },
                scales: {
                    // 不指定 beginAtZero — 含負值的虛擬迴路（A+B+C-D）需正確顯示負 bar
                    // 單位標籤由 axisUnitPlugin 水平畫在軸頂，不用會被旋轉 90° 的 scale title
                    y: { position: 'left' },
                    y1: {
                        position: 'right',
                        grid: { drawOnChartArea: false }
                    },
                    x: { title: { display: true, text: t('energydeclaration.chart.x_axis') } }
                }
            }
        });
    }

    async function exportExcel() {
        const req = buildRequest();
        if (!req) return;
        try {
            const res = await fetch('/EnergyDeclaration/api/export', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(req)
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                throw new Error(err.message || res.statusText);
            }
            const cd = res.headers.get('Content-Disposition') || '';
            let szFileName = 'EnergyDeclaration.xlsx';
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
            alert(t('energydeclaration.alert.export_failed', { 0: err.message }));
        }
    }

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    window._ed = { openCreateModal, openEditModal, saveConfig, deleteConfig, query, exportExcel };
})();
