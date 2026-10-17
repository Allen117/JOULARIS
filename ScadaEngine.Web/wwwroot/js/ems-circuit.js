(function () {
    'use strict';

    // ── 能源別設定 ────────────────────────────────────────────────────────────
    // 四頁（空調水系統 / 電力 / 自來水 / 氣）共用本檔，差異只有 endpoint、單位與色系 —
    // 後端四組 API 回傳契約一致（labels[] / values[]），期別語意已各自在後端 Parse*Pivot* 吸收
    // （空調水系統走曆制 ParseCalendarPivot，刻意不吃月結期別）。
    // View 只需在 .ems-circuit-page 上標 data-kind，其餘由此表決定。
    var CONFIG = {
        chilledwater: {
            treeUrl:  '/EMS/api/chilled-water-circuit-tree',
            usageUrl: '/EMS/api/chilled-water-rth',
            unit:     'RT·h',
            fill:     'rgba(0,131,143,0.55)',
            border:   '#00838f'
        },
        electricity: {
            treeUrl:  '/EMS/api/circuit-tree',
            usageUrl: '/EMS/api/circuit-energy',
            unit:     'kWh',
            fill:     'rgba(67,160,71,0.55)',
            border:   '#2e7d32'
        },
        water: {
            treeUrl:  '/EMS/api/water-circuit-tree',
            usageUrl: '/EMS/api/water-usage',
            unit:     'm³',
            fill:     'rgba(2,136,209,0.55)',
            border:   '#0277bd'
        },
        gas: {
            treeUrl:  '/EMS/api/gas-circuit-tree',
            usageUrl: '/EMS/api/gas-usage',
            unit:     'm³',
            fill:     'rgba(230,81,0,0.55)',
            border:   '#e65100'
        }
    };

    var POLL_MS = 60000;
    var _cfg = CONFIG.electricity;
    var _circuitId = null;
    var _pollTimer = null;
    var _charts = { month: null, day: null, hour: null };

    // ── 工具函式 ───────────────────────────────────────────────────────────────
    function pad2(n) { return n < 10 ? '0' + n : String(n); }

    function todayStr() {
        var d = new Date();
        return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
    }

    function thisMonthStr() {
        var d = new Date();
        return d.getFullYear() + '-' + pad2(d.getMonth() + 1);
    }

    function thisYearStr() {
        return String(new Date().getFullYear());
    }

    // ── 動態填滿可視高度（消除整頁拉霸）─────────────────────────────────────
    function fitPageHeight() {
        var page = document.querySelector('.ems-circuit-page');
        if (!page) return;
        var nav  = document.querySelector('.navbar');
        var foot = document.querySelector('.footer');
        var h = window.innerHeight
              - (nav  ? nav.offsetHeight  : 0)
              - (foot ? foot.offsetHeight : 0);
        page.style.height = h + 'px';
    }

    // ── 初始化 ────────────────────────────────────────────────────────────────
    function init() {
        var page = document.querySelector('.ems-circuit-page');
        var kind = page && page.dataset.kind;
        if (kind && CONFIG[kind]) _cfg = CONFIG[kind];

        fitPageHeight();
        window.addEventListener('resize', fitPageHeight);

        document.getElementById('yearPicker').value  = thisYearStr();
        document.getElementById('monthPicker').value = thisMonthStr();
        document.getElementById('datePicker').value  = todayStr();

        document.getElementById('yearPicker').addEventListener('change', function () {
            if (_circuitId !== null) fetchAndRender('month', this.value);
        });
        document.getElementById('monthPicker').addEventListener('change', function () {
            if (_circuitId !== null) fetchAndRender('day', this.value);
        });
        document.getElementById('datePicker').addEventListener('change', function () {
            if (_circuitId === null) return;
            stopPoll();
            fetchAndRender('hour', this.value);
            startPoll();
        });

        loadTree();
    }

    // ── 迴路樹 ────────────────────────────────────────────────────────────────
    function loadTree() {
        fetch(_cfg.treeUrl)
            .then(function (r) { return r.json(); })
            .then(function (nodes) { renderTree(nodes); })
            .catch(function (e) { console.error('[ems-circuit] 載入迴路樹失敗', e); });
    }

    function renderTree(nodes) {
        var map = {};
        nodes.forEach(function (n) {
            map[n.id] = { id: n.id, name: n.name, parentId: n.parentId, sortOrder: n.sortOrder, children: [] };
        });
        var roots = [];
        nodes.forEach(function (n) {
            if (n.parentId && map[n.parentId]) {
                map[n.parentId].children.push(map[n.id]);
            } else {
                roots.push(map[n.id]);
            }
        });
        sortTree(roots);

        var container = document.getElementById('circuitTree');
        container.innerHTML = '';
        container.appendChild(buildTreeEl(roots));

        container.addEventListener('click', function (e) {
            var item = e.target.closest('.ems-tree-item[data-id]');
            if (!item) return;

            if (e.target.classList.contains('ems-tree-expand')) {
                item.classList.toggle('collapsed');
                return;
            }

            selectNode(item);
        });

        // 預設選取最上層的第一個根節點
        var firstRoot = container.querySelector('.ems-tree-list > .ems-tree-item[data-id]');
        if (firstRoot) selectNode(firstRoot);
    }

    function selectNode(item) {
        var container = document.getElementById('circuitTree');
        container.querySelectorAll('.ems-tree-item').forEach(function (el) {
            el.classList.remove('selected');
        });
        item.classList.add('selected');
        _circuitId = parseInt(item.dataset.id, 10);
        document.getElementById('selectedCircuitName').textContent = item.dataset.name;
        loadAllCharts();
    }

    function sortTree(list) {
        list.sort(function (a, b) { return a.sortOrder - b.sortOrder; });
        list.forEach(function (n) { sortTree(n.children); });
    }

    function buildTreeEl(nodes) {
        var ul = document.createElement('ul');
        ul.className = 'ems-tree-list';
        nodes.forEach(function (n) {
            var li = document.createElement('li');
            li.className = 'ems-tree-item';
            li.dataset.id   = n.id;
            li.dataset.name = n.name;

            var icon = document.createElement('span');
            icon.className = n.children.length > 0 ? 'ems-tree-expand' : 'ems-tree-expand-placeholder';
            if (n.children.length > 0) icon.innerHTML = '<i class="fas fa-chevron-down" aria-hidden="true"></i>';
            li.appendChild(icon);

            var label = document.createElement('span');
            label.className   = 'ems-tree-label';
            label.textContent = n.name;
            li.appendChild(label);

            if (n.children.length > 0) li.appendChild(buildTreeEl(n.children));
            ul.appendChild(li);
        });
        return ul;
    }

    // ── 圖表載入 ──────────────────────────────────────────────────────────────
    function loadAllCharts() {
        var yearPivot  = document.getElementById('yearPicker').value  || thisYearStr();
        var monthPivot = document.getElementById('monthPicker').value || thisMonthStr();
        var datePivot  = document.getElementById('datePicker').value  || todayStr();

        stopPoll();
        fetchAndRender('month', yearPivot);
        fetchAndRender('day',   monthPivot);
        fetchAndRender('hour',  datePivot);
        startPoll();
    }

    function fetchAndRender(granularity, pivot) {
        if (_circuitId === null) return;
        var url = _cfg.usageUrl + '?circuitId=' + encodeURIComponent(_circuitId) +
                  '&granularity=' + encodeURIComponent(granularity) +
                  '&pivot='       + encodeURIComponent(pivot);

        fetch(url)
            .then(function (r) { return r.json(); })
            .then(function (data) {
                renderBar(granularity, data.labels, data.values);
                // 空調水系統 / 自來水 / 氣 API 才有 hasWarning（區間內有時段資料缺漏）；電力頁無對應元素 → no-op
                setWarn(granularity, data.hasWarning === true);
            })
            .catch(function (e) { console.error('[ems-circuit] fetch 失敗', granularity, e); });
    }

    // ── 資料缺漏警示（空調水系統 / 自來水 / 氣）───────────────────────────────
    function setWarn(granularity, isWarn) {
        var el = document.querySelector('.ems-circuit-warn[data-warn="' + granularity + '"]');
        if (!el) return;
        el.style.display = isWarn ? '' : 'none';
    }

    // ── 長條圖渲染 ────────────────────────────────────────────────────────────
    function renderBar(granularity, labels, values) {
        var canvasIds = { month: 'chartMonth', day: 'chartDay', hour: 'chartHour' };
        var canvas = document.getElementById(canvasIds[granularity]);
        if (!canvas || !window.Chart) return;

        if (_charts[granularity]) {
            _charts[granularity].data.labels           = labels;
            _charts[granularity].data.datasets[0].data = values;
            _charts[granularity].update('none');
            return;
        }

        _charts[granularity] = new Chart(canvas.getContext('2d'), {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: _cfg.fill,
                    borderColor: _cfg.border,
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
                                // 固定小數一位（0 → 0.0）+ 千分位，與 EMS 卡片／各報表的數值顯示一致
                                return Number(v != null ? v : 0).toLocaleString('en-US', {
                                    minimumFractionDigits: 1, maximumFractionDigits: 1
                                }) + ' ' + _cfg.unit;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        ticks: { font: { size: 11 }, color: '#757575' },
                        grid: { display: false }
                    },
                    y: {
                        beginAtZero: true,
                        ticks: {
                            font: { size: 11 },
                            color: '#757575',
                            maxTicksLimit: 6,
                            // 依數值級距自動決定小數位數，避免 toFixed(0) 把 0.5 壓成 0 造成整排重複
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

    // ── 自動刷新（日卡片，60s）────────────────────────────────────────────────
    function startPoll() {
        stopPoll();
        _pollTimer = setTimeout(function doPoll() {
            console.log('[ems-circuit] 日卡片 auto-refresh');
            fetchAndRender('hour', document.getElementById('datePicker').value || todayStr());
            _pollTimer = setTimeout(doPoll, POLL_MS);
        }, POLL_MS);
    }

    function stopPoll() {
        if (_pollTimer) { clearTimeout(_pollTimer); _pollTimer = null; }
    }

    window._emsCircuit = { reload: loadAllCharts };
    document.addEventListener('DOMContentLoaded', init);
})();
