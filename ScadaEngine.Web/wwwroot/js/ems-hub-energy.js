/* EMS Hub — 主要電表用電三卡片（長條圖 / 子迴路圓餅圖 / 去年同期比較表）
   長條圖獨立一組日/月/年切換；圓餅圖與比較表共用另一組。
   UI 粒度對應後端：日→hour、月→day、年→month（同 /EMS/api/circuit-energy 協定）。 */
(function () {
    'use strict';

    var REFRESH_MS = 60000;
    var NO_MAIN_METER_MSG = '尚未設定主要電表\n請至「電表/迴路設定」頁勾選一顆主要電表';

    var _meter = null;          // { id, name, hasChildren }
    var _barGran = 'hour';      // 長條圖粒度（獨立）
    var _pdGran = 'hour';       // 圓餅 + 比較表共用粒度
    var _barChart = null;
    var _pieChart = null;
    var _refreshTimer = null;

    // 圓餅色盤 — 首色維持 EMS 綠（識別度），其餘跨全色相分佈以利分辨相鄰扇形
    var PIE_COLORS = ['#43a047', '#1e88e5', '#fb8c00', '#8e24aa', '#e53935',
                      '#00acc1', '#fdd835', '#6d4c41', '#ec407a', '#26a69a',
                      '#5c6bc0', '#c0ca33'];

    // ── 工具函式 ─────────────────────────────────────────────
    function pad2(n) { return n < 10 ? '0' + n : String(n); }

    function todayStr() {
        var d = new Date();
        return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
    }

    function thisMonthStr() {
        var d = new Date();
        return d.getFullYear() + '-' + pad2(d.getMonth() + 1);
    }

    function thisYearStr() { return String(new Date().getFullYear()); }

    function escHtml(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function fmtKwh(v) {
        if (v == null) return '--';
        return v.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    }

    // 去年同期 pivot 顯示字串（2/29 → 2/28，與後端 AddYears(-1) 行為一致）
    function lastYearPivotStr(gran, pivot) {
        if (gran === 'month') return String(parseInt(pivot, 10) - 1);
        var parts = pivot.split('-');
        var y = parseInt(parts[0], 10) - 1;
        if (gran === 'day') return y + '-' + parts[1];
        var mm = parts[1], dd = parts[2];
        if (mm === '02' && dd === '29') dd = '28';
        return y + '-' + mm + '-' + dd;
    }

    // ── 粒度切換元件（一組按鈕 + 三個 pivot 輸入框） ────────
    // 所在卡片被 /EmsCardSetting 關閉時元素不存在 → 回傳 null（pivotOf 對 null 回預設）
    function setupGranGroup(groupId, ids, onChange) {
        var group = document.getElementById(groupId);
        if (!group) return null;
        var inputs = {
            hour:  document.getElementById(ids.date),
            day:   document.getElementById(ids.month),
            month: document.getElementById(ids.year)
        };
        inputs.hour.value  = todayStr();
        inputs.day.value   = thisMonthStr();
        inputs.month.value = thisYearStr();

        group.querySelectorAll('.ems-gran-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                group.querySelectorAll('.ems-gran-btn').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
                var gran = btn.dataset.gran;
                Object.keys(inputs).forEach(function (k) {
                    inputs[k].style.display = (k === gran) ? '' : 'none';
                });
                onChange(gran);
            });
        });
        Object.keys(inputs).forEach(function (k) {
            inputs[k].addEventListener('change', function () {
                if (this.value) onChange(k);
            });
        });
        return inputs;
    }

    var _barInputs, _pdInputs;

    function pivotOf(inputs, gran) {
        // inputs=null（粒度控制所在卡片被關閉）→ 回今日/當月/今年預設
        var v = inputs && inputs[gran] ? inputs[gran].value : '';
        if (v) return v;
        return gran === 'hour' ? todayStr() : gran === 'day' ? thisMonthStr() : thisYearStr();
    }

    // ── 初始化 ───────────────────────────────────────────────
    // 三張卡（長條/圓餅/比較）可由 /EmsCardSetting 個別關閉（DOM 不渲染），各自以根元素存在與否防呆；
    // 三卡全關時整支不動作、不打 /EMS/api/main-meter
    function init() {
        var hasBar = !!document.getElementById('barChartWrap');
        var hasPie = !!document.getElementById('pieChartWrap');
        var hasYoy = !!document.getElementById('yoyTableWrap');
        if (!hasBar && !hasPie && !hasYoy) return;

        _barInputs = setupGranGroup('barGranGroup', { date: 'barPivotDate', month: 'barPivotMonth', year: 'barPivotYear' },
            function (gran) { _barGran = gran; loadBar(); });
        _pdInputs = setupGranGroup('pdGranGroup', { date: 'pdPivotDate', month: 'pdPivotMonth', year: 'pdPivotYear' },
            function (gran) { _pdGran = gran; loadPie(); loadYoy(); });

        fetch('/EMS/api/main-meter')
            .then(function (r) { return r.json(); })
            .then(function (m) {
                if (!m.hasMainMeter) {
                    showNoMainMeter();
                    return;
                }
                _meter = m;
                var elBarName = document.getElementById('barMeterName');
                if (elBarName) elBarName.textContent = m.name;
                loadBar();
                loadPie();
                loadYoy();
                startAutoRefresh();
            })
            .catch(function (e) { console.error('[ems-hub-energy] 載入主要電表失敗', e); });
    }

    // ── 未設定主要電表：三卡片統一提示（被關閉的卡片元素不存在 → 逐一防呆跳過）──
    function showNoMainMeter() {
        [['barEmpty', 'barChartWrap'], ['pieEmpty', 'pieChartWrap'], ['yoyEmpty', 'yoyTableWrap']]
            .forEach(function (pair) {
                var empty = document.getElementById(pair[0]);
                var body  = document.getElementById(pair[1]);
                if (!empty || !body) return;
                empty.textContent = NO_MAIN_METER_MSG;
                empty.style.display = '';
                body.style.display = 'none';
            });
        ['barGranGroup', 'pdGranGroup'].forEach(function (id) {
            var group = document.getElementById(id);
            if (!group) return;
            group.querySelectorAll('.ems-gran-btn').forEach(function (b) { b.disabled = true; });
        });
        ['barPivotDate', 'barPivotMonth', 'barPivotYear', 'pdPivotDate', 'pdPivotMonth', 'pdPivotYear']
            .forEach(function (id) {
                var el = document.getElementById(id);
                if (el) el.disabled = true;
            });
    }

    // ── 長條圖 ───────────────────────────────────────────────
    function loadBar() {
        if (!_meter || !document.getElementById('barChartWrap')) return;
        var pivot = pivotOf(_barInputs, _barGran);
        var url = '/EMS/api/circuit-energy?circuitId=' + encodeURIComponent(_meter.id) +
                  '&granularity=' + encodeURIComponent(_barGran) +
                  '&pivot=' + encodeURIComponent(pivot);
        fetch(url)
            .then(function (r) { return r.json(); })
            .then(function (data) { renderBar(data.labels, data.values); })
            .catch(function (e) { console.error('[ems-hub-energy] 長條圖載入失敗', e); });
    }

    function renderBar(labels, values) {
        var canvas = document.getElementById('mainMeterBarChart');
        if (!canvas || !window.Chart) return;

        if (_barChart) {
            _barChart.data.labels           = labels;
            _barChart.data.datasets[0].data = values;
            _barChart.update('none');
            return;
        }

        _barChart = new Chart(canvas.getContext('2d'), {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: 'rgba(67,160,71,0.55)',
                    borderColor: '#2e7d32',
                    borderWidth: 1,
                    borderRadius: 3
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        callbacks: {
                            label: function (ctx) {
                                var v = ctx.parsed.y;
                                return fmtKwh(v != null ? v : 0) + ' kWh';
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        ticks: { font: { size: 11 }, color: '#757575', maxTicksLimit: 16 },
                        grid: { display: false }
                    },
                    y: {
                        beginAtZero: true,
                        ticks: {
                            font: { size: 11 },
                            color: '#757575',
                            maxTicksLimit: 6,
                            // 依數值級距自動決定小數位數，避免 toFixed(0) 把 0.5 壓成 0 造成整排重複（比照 CircuitInfo Y 軸）
                            callback: function (v) {
                                if (v === 0) return '0';
                                var abs = Math.abs(v);
                                if (abs >= 10) return v.toFixed(0);
                                if (abs >= 1)  return (+v.toFixed(1)).toString();
                                return (+v.toFixed(2)).toString();
                            }
                        },
                        grid: { color: 'rgba(0,0,0,0.05)' }
                    }
                }
            }
        });
    }

    // ── 子迴路圓餅圖 ─────────────────────────────────────────
    function loadPie() {
        if (!_meter || !document.getElementById('pieChartWrap')) return;
        var pivot = pivotOf(_pdInputs, _pdGran);
        var url = '/EMS/api/main-meter-breakdown?granularity=' + encodeURIComponent(_pdGran) +
                  '&pivot=' + encodeURIComponent(pivot);
        fetch(url)
            .then(function (r) { return r.json(); })
            .then(function (data) { renderPie(data.items || []); })
            .catch(function (e) { console.error('[ems-hub-energy] 圓餅圖載入失敗', e); });
    }

    function renderPie(items) {
        var empty   = document.getElementById('pieEmpty');
        var wrap    = document.getElementById('pieChartWrap');
        var negNote = document.getElementById('pieNegNote');

        // 圓餅無法表達負值：負值迴路（Sign=-1 回饋/發電）改列於下方小字，不入餅
        var positives = items.filter(function (it) { return it.kwh > 0; });
        var negatives = items.filter(function (it) { return it.kwh < 0; });

        negNote.innerHTML = negatives.map(function (it) {
            return escHtml(it.name) + '：' + fmtKwh(it.kwh) + ' kWh（未列入占比）';
        }).join('<br>');

        if (positives.length === 0) {
            empty.textContent = '期間內無用電資料';
            empty.style.display = '';
            wrap.style.display = 'none';
            return;
        }
        empty.style.display = 'none';
        wrap.style.display = '';

        var labels = positives.map(function (it) { return it.name; });
        var values = positives.map(function (it) { return it.kwh; });
        var colors = positives.map(function (_, i) { return PIE_COLORS[i % PIE_COLORS.length]; });

        if (_pieChart) {
            _pieChart.data.labels                       = labels;
            _pieChart.data.datasets[0].data             = values;
            _pieChart.data.datasets[0].backgroundColor  = colors;
            _pieChart.update('none');
            return;
        }

        _pieChart = new Chart(document.getElementById('mainMeterPieChart').getContext('2d'), {
            type: 'pie',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: colors,
                    borderColor: '#fff',
                    borderWidth: 1.5
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        position: 'bottom',
                        labels: { font: { size: 11 }, boxWidth: 12, color: '#555' }
                    },
                    tooltip: {
                        callbacks: {
                            label: function (ctx) {
                                var total = ctx.dataset.data.reduce(function (a, b) { return a + b; }, 0);
                                var pct = total > 0 ? (ctx.parsed / total * 100).toFixed(1) : '0';
                                return ctx.label + ': ' + fmtKwh(ctx.parsed) + ' kWh (' + pct + '%)';
                            }
                        }
                    }
                }
            }
        });
    }

    // ── 去年同期比較表 ───────────────────────────────────────
    function loadYoy() {
        if (!_meter || !document.getElementById('yoyTableWrap')) return;
        var pivot = pivotOf(_pdInputs, _pdGran);
        var url = '/EMS/api/main-meter-yoy?granularity=' + encodeURIComponent(_pdGran) +
                  '&pivot=' + encodeURIComponent(pivot);
        fetch(url)
            .then(function (r) { return r.json(); })
            .then(function (data) { renderYoy(data.rows || [], pivot); })
            .catch(function (e) { console.error('[ems-hub-energy] 比較表載入失敗', e); });
    }

    function renderYoy(rows, pivot) {
        var lastPivot = lastYearPivotStr(_pdGran, pivot);
        document.getElementById('yoyPeriodText').textContent = pivot + ' vs ' + lastPivot;
        document.getElementById('yoyThCurrent').textContent  = '本期 kWh (' + pivot + ')';
        document.getElementById('yoyThLastYear').textContent = '去年同期 kWh (' + lastPivot + ')';

        var tbody = document.getElementById('yoyTableBody');
        if (rows.length === 0) {
            tbody.innerHTML = '<tr><td colspan="5" class="text-center text-muted py-3">無資料</td></tr>';
            return;
        }

        tbody.innerHTML = rows.map(function (r) {
            var diffTxt = (r.diffKwh > 0 ? '+' : '') + fmtKwh(r.diffKwh);
            var pctHtml = UiState.pctHtml(r.pctChange, 'ems-yoy-up', 'ems-yoy-down');
            var nameHtml = r.isMainMeter
                ? '<i class="fas fa-star ems-yoy-star me-1"></i><span class="fw-semibold">' + escHtml(r.name) + '</span>'
                : escHtml(r.name);
            return '<tr' + (r.isMainMeter ? ' class="ems-yoy-main-row"' : '') + '>' +
                   '<td>' + nameHtml + '</td>' +
                   '<td class="text-end">' + fmtKwh(r.currentKwh) + '</td>' +
                   '<td class="text-end">' + fmtKwh(r.lastYearKwh) + '</td>' +
                   '<td class="text-end">' + diffTxt + '</td>' +
                   '<td class="text-end">' + pctHtml + '</td>' +
                   '</tr>';
        }).join('');
    }

    // ── 自動刷新（僅長條圖：日模式且選的是今天；長條卡關閉時 _barInputs=null 不刷）──
    function startAutoRefresh() {
        clearTimeout(_refreshTimer);
        _refreshTimer = setTimeout(function tick() {
            if (_barGran === 'hour' && _barInputs && _barInputs.hour.value === todayStr()) {
                loadBar();
            }
            _refreshTimer = setTimeout(tick, REFRESH_MS);
        }, REFRESH_MS);
    }

    window._emsHubEnergy = { reloadBar: loadBar, reloadBreakdown: function () { loadPie(); loadYoy(); } };

    document.addEventListener('DOMContentLoaded', init);
})();
