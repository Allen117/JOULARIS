/* ── HistoryData 頁面邏輯（IIFE 封裝）── */
(function () {
    'use strict';

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    // ── 從 Razor 注入的設定讀取資料 ──────────────────────────────────────
    var cfg = window.__historyConfig || {};
    var allPoints       = cfg.points       || [];
    var allCoordinators = cfg.coordinators || [];
    var currentCoordinatorId = cfg.initialCoordId || 0;
    var currentModbusId      = null;
    var currentCalcGroup     = null;   // null = 全部計算點位, string = 指定群組
    var currentSidPrefix     = null;   // null = 不以 SID 前綴篩選；'DB' / 'DB1-' 等切換到 DB 群組
    var currentDeviceGroup   = null;   // null = 不依 Device 篩；'' = 未分群桶；'子設備名' = 指定 Device（單站號內）

    // ── 為每個點位計算所屬設備/子設備名稱，作為顯示前綴 ──────────────────
    // isCalcSid 委派共用層 window.PointGrouping（分群解析單一真相）
    function isCalcSid(sid) { return window.PointGrouping.isCalcSid(sid); }

    (function enrichPointsWithDeviceLabel() {
        var getSidPfx = function (sid) { return window.PointGrouping.getSidPrefix(sid); };
        var isOfDev   = function (sid, nId) { var n = getSidPfx(sid); return n >= nId * 65536 && n < (nId + 1) * 65536; };
        allPoints.forEach(function (p) {
            if (isCalcSid(p.sid)) {
                // calcLabel 由 Razor 注入（此處執行時 i18n 字典尚未 fetch 完成，t() 會漏回 raw key）
                var grp = p.groupName || cfg.calcLabel || '';
                p.deviceLabel = grp;
                p.fullName = grp ? grp + ' / ' + p.name : p.name;
                return;
            }
            if (window.PointGrouping.isDmdSid(p.sid)) {
                // 需量點名已帶「需量」後綴、側欄另有獨立需量桶 — 不加前綴
                p.deviceLabel = '';
                p.fullName = p.name;
                return;
            }
            var szLabel = '';
            for (var di = 0; di < allCoordinators.length; di++) {
                var d = allCoordinators[di];
                if (!isOfDev(p.sid, d.nId)) continue;
                // coord 為 Hungarian（d.szModbusID…），point 為 camelCase（p.sid / p.deviceGroup）
                szLabel = window.PointGrouping.pointDeviceLabel(p.sid, p.deviceGroup, d);
                break;
            }
            p.deviceLabel = szLabel;
            p.fullName    = szLabel ? szLabel + ' / ' + p.name : p.name;
        });
    })();

    // 統計（含點位分類、累計值差異值 dDiff）一律由後端 HistoryStatisticsCalculator 算好，前端只顯示

    // ── 常數 ─────────────────────────────────────────────────────────────
    var CHART_COLORS = [
        { border: 'rgba(13,110,253,0.9)',  bg: 'rgba(13,110,253,0.12)'  },
        { border: 'rgba(220,53,69,0.9)',   bg: 'rgba(220,53,69,0.12)'   },
        { border: 'rgba(25,135,84,0.9)',   bg: 'rgba(25,135,84,0.12)'   },
        { border: 'rgba(255,193,7,0.9)',   bg: 'rgba(255,193,7,0.12)'   },
        { border: 'rgba(13,202,240,0.9)',  bg: 'rgba(13,202,240,0.12)'  },
        { border: 'rgba(111,66,193,0.9)',  bg: 'rgba(111,66,193,0.12)'  },
        { border: 'rgba(253,126,20,0.9)',  bg: 'rgba(253,126,20,0.12)'  },
        { border: 'rgba(102,16,242,0.9)',  bg: 'rgba(102,16,242,0.12)'  }
    ];
    var MAX_BASKET = 8;

    // Chart.js plugin：Y 軸 title 改水平、貼在 Y 軸 tick 數值欄的外側（左軸在 tick 數值左邊、右軸在右邊），
    // 垂直置中於資料區。需配合 layout.padding 左右預留空間（雙軸時設 70px）
    var horizontalAxisTitlePlugin = {
        id: 'horizontalAxisTitle',
        afterDatasetsDraw: function (chart) {
            var ctx = chart.ctx;
            var area = chart.chartArea;
            ['y', 'y1'].forEach(function (axisId) {
                var scale = chart.scales[axisId];
                if (!scale) return;
                var titleOpt = scale.options && scale.options.title;
                if (!titleOpt || !titleOpt.text) return;
                var fontSize   = (titleOpt.font && titleOpt.font.size)   || 11;
                var fontWeight = (titleOpt.font && titleOpt.font.weight) || 'normal';
                ctx.save();
                ctx.font = fontWeight + ' ' + fontSize + 'px sans-serif';
                ctx.fillStyle = titleOpt.color || '#666';
                ctx.textBaseline = 'middle';
                var midY = (area.top + area.bottom) / 2;
                if (scale.position === 'right') {
                    ctx.textAlign = 'left';
                    ctx.fillText(titleOpt.text, scale.right + 4, midY);
                } else {
                    ctx.textAlign = 'right';
                    ctx.fillText(titleOpt.text, scale.left - 4, midY);
                }
                ctx.restore();
            });
        }
    };

    // ── DOM refs ─────────────────────────────────────────────────────────
    var pointListContainer = document.getElementById('pointListContainer');
    var basketContainer    = document.getElementById('basketContainer');
    var dtStart            = document.getElementById('dtStart');
    var dtEnd              = document.getElementById('dtEnd');
    var btnQuery           = document.getElementById('btnQuery');
    var btnClear           = document.getElementById('btnClear');
    var chartContainer     = document.getElementById('chartContainer');
    var noDataMsg          = document.getElementById('noDataMsg');
    var loadingSpinner     = document.getElementById('loadingSpinner');
    var limitWarning       = document.getElementById('limitWarning');
    var statsRow           = document.getElementById('statsRow');
    var multiStatsWrapper  = document.getElementById('multiStatsWrapper');
    var chartTitle         = document.getElementById('chartTitle');
    var dataTableCard      = document.getElementById('dataTableCard');
    var tableBodyEl        = document.getElementById('tableBody');
    var dataTableBody      = document.getElementById('dataTableBody');
    var btnToggleTable     = document.getElementById('btnToggleTable');

    // ── 點位選取清單 ─────────────────────────────────────────────────────
    var getN = function (sid) { var m = sid.match(/-S(\d+)$/); return m ? parseInt(m[1]) : Infinity; };

    function renderPointList(nDbId, nModbusId, szCalcGroup, szSidPrefix, szDeviceGroup) {
        var pts;
        if (szSidPrefix) {
            // DB 群組（父或子）— 以 SID 前綴篩選
            pts = allPoints.filter(function (p) {
                return p.sid && p.sid.indexOf(szSidPrefix) === 0;
            });
        } else if (nDbId === -999) {
            // 計算點位 — 依群組篩選
            pts = allPoints.filter(function (p) {
                if (!isCalcSid(p.sid)) return false;
                if (szCalcGroup != null) {
                    return (p.groupName || '') === szCalcGroup;
                }
                return true;
            });
        } else if (nDbId <= 0) {
            pts = allPoints.slice();
        } else {
            pts = allPoints.filter(function (p) {
                var h = p.sid.indexOf('-');
                if (h < 0) return false;
                var num = parseInt(p.sid.substring(0, h));
                if (isNaN(num)) return false;
                if (szDeviceGroup != null) {
                    // 單站號內依 Device 分群篩選（'' = 未分群桶）
                    if (num < nDbId * 65536 || num >= (nDbId + 1) * 65536) return false;
                    return window.PointGrouping.getDeviceGroup(p) === szDeviceGroup;
                }
                if (nModbusId != null) {
                    var base = nDbId * 65536 + nModbusId * 256;
                    return num >= base && num < base + 256;
                }
                return num >= nDbId * 65536 && num < (nDbId + 1) * 65536;
            });
        }
        pts.sort(function (a, b) { return getN(a.sid) - getN(b.sid); });

        pointListContainer.innerHTML = pts.map(function (p, i) {
            var szDisplay = p.fullName || p.name;
            return '<div class="form-check py-1">'
                + '<input class="form-check-input point-checkbox" type="checkbox"'
                + ' value="' + p.sid + '" id="pt_' + i + '"'
                + ' data-name="' + szDisplay.replace(/"/g, '&quot;') + '"'
                + ' data-unit="' + p.unit.replace(/"/g, '&quot;') + '">'
                + '<label class="form-check-label small" for="pt_' + i + '">'
                + (p.deviceLabel ? '<span style="color:#0d6efd;">' + p.deviceLabel + '</span><span style="color:#999;margin:0 3px;">/</span>' : '')
                + p.name
                + '</label></div>';
        }).join('') || '<div class="text-muted small text-center py-3">' + t('history.alert.no_points_data') + '</div>';
    }

    // ── 待查詢清單（Basket）─────────────────────────────────────────────
    var basket = [];

    function renderBasket() {
        document.getElementById('basketCount').textContent = basket.length;
        var hint = document.getElementById('basketHint');

        if (basket.length === 0) {
            basketContainer.innerHTML = '';
            hint.style.display = 'none';
            return;
        }

        hint.style.display = '';
        basketContainer.innerHTML = basket.map(function (p, i) {
            var c = CHART_COLORS[i % CHART_COLORS.length];
            return '<div class="basket-item" data-sid="' + p.sid + '">'
                + '<div class="d-flex align-items-center gap-2 overflow-hidden">'
                + '<span class="color-dot" style="background:' + c.border + '"></span>'
                + '<span class="text-truncate">' + p.name + '</span>'
                + '</div>'
                + '<button class="btn-remove" onclick="window._history.removeFromBasket(\'' + p.sid + '\')" title="' + t('history.basket.button.remove') + '">'
                + '<i class="fas fa-times"></i>'
                + '</button></div>';
        }).join('');
    }

    function addToBasket(items) {
        var added = 0;
        for (var idx = 0; idx < items.length; idx++) {
            var item = items[idx];
            if (basket.some(function (b) { return b.sid === item.sid; })) continue;
            if (basket.length >= MAX_BASKET) {
                showAlert(t('history.alert.basket_full', { 0: MAX_BASKET }), 'warning');
                break;
            }
            basket.push(item);
            added++;
        }
        if (added > 0) renderBasket();
        else if (items.length > 0 && added === 0) showAlert(t('history.alert.already_in_basket'), 'info');
    }

    function removeFromBasket(sid) {
        basket = basket.filter(function (b) { return b.sid !== sid; });
        renderBasket();
    }

    // ── Chart ────────────────────────────────────────────────────────────
    var trendChart    = null;
    var stackedCharts = [];
    var currentChartType = 'line';
    var isSingleAxis  = false;
    var lastResults   = null;
    var lastQuery     = null;   // 匯出沿用查詢當下的點位 / 起訖 / 間隔，與畫面一致

    function destroyAllCharts() {
        if (trendChart) { trendChart.destroy(); trendChart = null; }
        stackedCharts.forEach(function (c) { c.destroy(); });
        stackedCharts = [];
    }

    // Bad 品質點位：圖表位置沿用前一個 Good 值，原始值保留供 tooltip 顯示
    function holdLastGoodValue(data, qualities) {
        var processed    = [];
        var originalVals = [];
        var lastGoodY    = null;
        for (var i = 0; i < data.length; i++) {
            var pt = data[i];
            originalVals.push(pt.y);
            if (qualities[i] === 'Good') {
                lastGoodY = pt.y;
                processed.push(pt);
            } else {
                processed.push({ x: pt.x, y: lastGoodY });
            }
        }
        return { data: processed, originalVals: originalVals };
    }

    function buildChart(datasets) {
        destroyAllCharts();
        var stackedContainer = document.getElementById('stackedChartsContainer');

        if (datasets.length >= 3) {
            chartContainer.style.display = 'none';
            buildStackedCharts(datasets);
            return;
        }

        stackedContainer.style.display = 'none';
        chartContainer.style.display = 'block';
        var ctx = document.getElementById('trendChart').getContext('2d');

        var isSingle = datasets.length === 1;
        var isDual   = datasets.length === 2 && !isSingleAxis;

        function niceRange(ds) {
            var vals = ds.data.map(function (d) { return d.y; }).filter(function (v) { return v != null; });
            if (!vals.length) return {};
            var lo = Math.min.apply(null, vals), hi = Math.max.apply(null, vals);
            var pad = (hi - lo) * 0.1 || Math.abs(lo) * 0.1 || 1;
            return { min: lo - pad, max: hi + pad };
        }

        var chartDatasets = datasets.map(function (ds, i) {
            var c = CHART_COLORS[i % CHART_COLORS.length];
            var ptColors = ds.qualities.map(function (q) {
                return q === 'Good' ? c.border : 'rgba(220,53,69,0.85)';
            });
            return {
                label:                ds.label + (ds.szUnit ? ' (' + ds.szUnit + ')' : ''),
                data:                 ds.data,
                yAxisID:              isDual ? (i === 0 ? 'y' : 'y1') : 'y',
                borderColor:          c.border,
                backgroundColor:      c.bg,
                pointBackgroundColor: isSingle ? ptColors : c.border,
                pointBorderColor:     'transparent',
                pointRadius:          ds.data.length > 500 ? 0 : 3,
                pointHoverRadius:     6,
                borderWidth:          2,
                fill:                 currentChartType === 'line' && isSingle,
                tension:              0.3
            };
        });

        var maxLen = Math.max.apply(null, datasets.map(function (d) { return d.data.length; }));

        var scaleX = {
            type: 'time',
            time: {
                tooltipFormat: 'yyyy-MM-dd HH:mm:ss',
                displayFormats: {
                    second: 'HH:mm:ss',
                    minute: 'MM-dd HH:mm',
                    hour:   'MM-dd HH:mm',
                    day:    'MM-dd'
                }
            },
            ticks: { maxTicksLimit: 10, maxRotation: 20 },
            grid:  { color: 'rgba(0,0,0,0.06)' }
        };

        // 雙軸 title 用 horizontalAxisTitlePlugin 改為水平繪製：Chart.js 內建 title 一律 display:false，
        // text/color/font 仍寫入 options 供 plugin 讀取
        var scaleY = {
            position: 'left',
            grid: { color: 'rgba(0,0,0,0.06)' },
            title: isDual ? {
                display: false,
                text: datasets[0].szUnit || datasets[0].label,
                color: CHART_COLORS[0].border,
                font: { size: 11, weight: 'bold' }
            } : { display: false }
        };
        if (isDual) Object.assign(scaleY, niceRange(datasets[0]));

        var scales = { x: scaleX, y: scaleY };

        if (isDual) {
            scales.y1 = Object.assign({
                position: 'right',
                grid: { drawOnChartArea: false },
                title: {
                    display: false,
                    text: datasets[1].szUnit || datasets[1].label,
                    color: CHART_COLORS[1].border,
                    font: { size: 11, weight: 'bold' }
                }
            }, niceRange(datasets[1]));
        }

        trendChart = new Chart(ctx, {
            type: currentChartType,
            data: { datasets: chartDatasets },
            plugins: [horizontalAxisTitlePlugin],
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: { duration: maxLen > 1000 ? 0 : 400 },
                interaction: { mode: 'index', intersect: false },
                layout: { padding: { left: isDual ? 70 : 0, right: isDual ? 70 : 0 } },
                plugins: {
                    legend: { display: true, position: 'top' },
                    tooltip: {
                        callbacks: {
                            label: function (ctx) {
                                var dsIdx = ctx.datasetIndex;
                                var ds    = datasets[dsIdx];
                                var q     = ds.qualities[ctx.dataIndex];
                                var origY = ds.originalVals ? ds.originalVals[ctx.dataIndex] : ctx.parsed.y;
                                var val   = ' ' + ctx.dataset.label + ': ' + Number(origY).toFixed(3);
                                return val + '  [' + q + ']';
                            }
                        }
                    }
                },
                scales: scales
            }
        });
    }

    // ── 堆疊小圖（3 個以上點位）─────────────────────────────────────────
    function buildStackedCharts(datasets) {
        var container = document.getElementById('stackedChartsContainer');
        container.innerHTML = '';
        stackedCharts = [];

        datasets.forEach(function (ds, i) {
            var c      = CHART_COLORS[i % CHART_COLORS.length];
            var isLast = (i === datasets.length - 1);

            var vals = ds.data.map(function (d) { return d.y; }).filter(function (v) { return v != null; });
            var yRange = {};
            if (vals.length) {
                var lo  = Math.min.apply(null, vals), hi = Math.max.apply(null, vals);
                var pad = (hi - lo) * 0.1 || Math.abs(lo) * 0.1 || 1;
                yRange = { min: lo - pad, max: hi + pad };
            }

            var unitLabel = ds.szUnit ? ' (' + ds.szUnit + ')' : '';
            var wrapper   = document.createElement('div');
            wrapper.className = 'mini-chart-wrapper';
            wrapper.innerHTML =
                '<div class="d-flex align-items-center gap-2 px-3 pt-2 pb-1">'
                + '<span style="width:10px;height:10px;border-radius:50%;background:' + c.border + ';flex-shrink:0;display:inline-block;"></span>'
                + '<span class="fw-semibold small">' + ds.label + unitLabel + '</span>'
                + '</div>'
                + '<div style="position:relative; height:170px; padding:0 0.5rem ' + (isLast ? '0.5rem' : '0') + ';">'
                + '<canvas id="miniChart_' + i + '"></canvas>'
                + '</div>';
            container.appendChild(wrapper);

            var canvas   = document.getElementById('miniChart_' + i);
            var ptColors = ds.qualities.map(function (q) {
                return q === 'Good' ? c.border : 'rgba(220,53,69,0.85)';
            });

            var chart = new Chart(canvas.getContext('2d'), {
                type: currentChartType,
                data: {
                    datasets: [{
                        data:                 ds.data,
                        borderColor:          c.border,
                        backgroundColor:      c.bg,
                        pointBackgroundColor: ptColors,
                        pointBorderColor:     'transparent',
                        pointRadius:          ds.data.length > 500 ? 0 : 2,
                        pointHoverRadius:     5,
                        borderWidth:          1.5,
                        fill:                 currentChartType === 'line',
                        tension:              0.3
                    }]
                },
                options: {
                    responsive:          true,
                    maintainAspectRatio: false,
                    animation:           { duration: ds.data.length > 1000 ? 0 : 300 },
                    interaction:         { mode: 'index', intersect: false },
                    plugins: {
                        legend: { display: false },
                        tooltip: {
                            callbacks: {
                                label: function (ctx) {
                                    var origY = ds.originalVals ? ds.originalVals[ctx.dataIndex] : ctx.parsed.y;
                                    var val   = ' ' + Number(origY).toFixed(3) + (ds.szUnit ? ' ' + ds.szUnit : '');
                                    var q     = ds.qualities[ctx.dataIndex];
                                    return val + '  [' + q + ']';
                                }
                            }
                        }
                    },
                    scales: {
                        x: {
                            type: 'time',
                            time: {
                                tooltipFormat:  'yyyy-MM-dd HH:mm:ss',
                                displayFormats: {
                                    second: 'HH:mm:ss',
                                    minute: 'MM-dd HH:mm',
                                    hour:   'MM-dd HH:mm',
                                    day:    'MM-dd'
                                }
                            },
                            ticks: { maxTicksLimit: 6, maxRotation: 0, display: isLast },
                            grid:  { color: 'rgba(0,0,0,0.06)' }
                        },
                        y: Object.assign({
                            position: 'left',
                            grid:     { color: 'rgba(0,0,0,0.06)' },
                            ticks:    { maxTicksLimit: 4 }
                        }, yRange)
                    }
                }
            });
            stackedCharts.push(chart);
        });

        container.style.display = '';
    }

    // ── 查詢 ─────────────────────────────────────────────────────────────
    function doQuery() {
        var szStart = dtStart.value;
        var szEnd   = dtEnd.value;
        var selInterval = document.getElementById('selInterval');
        var nInterval = selInterval ? (parseInt(selInterval.value, 10) || 0) : 0;

        if (basket.length === 0) { showAlert(t('history.alert.no_points_in_basket'), 'warning'); return; }
        if (!szStart || !szEnd)  { showAlert(t('history.alert.no_time_range'), 'warning'); return; }
        if (szStart >= szEnd)    { showAlert(t('history.alert.bad_time_range'), 'warning'); return; }

        noDataMsg.classList.add('d-none');
        chartContainer.style.display = 'none';
        document.getElementById('stackedChartsContainer').style.display = 'none';
        loadingSpinner.classList.remove('d-none');
        statsRow.style.display = 'none';
        multiStatsWrapper.style.display = 'none';
        limitWarning.classList.add('d-none');
        dataTableCard.classList.add('d-none');
        btnQuery.disabled = true;

        var queryParams = {
            sids: basket.map(function (p) { return p.sid; }),
            szStart: szStart,
            szEnd: szEnd,
            nInterval: nInterval
        };

        Promise.all(basket.map(function (p) {
            return fetch('/api/history/data?szSID=' + encodeURIComponent(p.sid)
                + '&szStart=' + encodeURIComponent(szStart)
                + '&szEnd=' + encodeURIComponent(szEnd)
                + '&nInterval=' + nInterval)
                .then(function (r) { return r.json(); });
        })).then(function (results) {
            loadingSpinner.classList.add('d-none');
            btnQuery.disabled = false;
            lastResults = results;
            lastQuery = queryParams;

            var failed = results.find(function (r) { return !r.success; });
            if (failed) {
                noDataMsg.innerHTML = '<i class="fas fa-exclamation-circle fa-3x mb-3 d-block text-danger opacity-50"></i><div>' + failed.message + '</div>';
                noDataMsg.classList.remove('d-none'); return;
            }
            if (!results.some(function (r) { return r.nCount > 0; })) {
                noDataMsg.innerHTML = '<i class="fas fa-inbox fa-3x mb-3 d-block opacity-25"></i><div>' + t('history.chart.no_data') + '</div>';
                noDataMsg.classList.remove('d-none'); return;
            }

            var limitedResults = results.filter(function (r) { return r.isLimited; });
            if (limitedResults.length > 0) {
                limitWarning.classList.remove('d-none');
                var szNames = limitedResults.map(function (r) { return r.szName; }).join('、');
                showAlert(t('history.alert.row_limit_exceeded', { 0: limitedResults[0].nMaxRecords, 1: szNames }), 'warning', 8000);
            }

            var btnAxis = document.getElementById('btnSingleAxis');
            var validCount = results.filter(function (r) { return r.nCount > 0; }).length;
            if (validCount === 2) {
                btnAxis.classList.remove('d-none');
            } else {
                btnAxis.classList.add('d-none');
                isSingleAxis = false;
                btnAxis.classList.remove('pressed');
            }

            chartTitle.innerHTML = results.length === 1
                ? '<i class="fas fa-chart-area text-primary me-2"></i>' + results[0].szName
                : '<i class="fas fa-chart-area text-primary me-2"></i>' + t('history.chart.title.queried', { 0: results.length });

            if (results.length === 1) {
                var json = results[0];
                var fmt = function (v) { return v != null ? Number(v).toFixed(3) + (json.szUnit ? ' ' + json.szUnit : '') : '--'; };
                document.getElementById('statMin').textContent    = fmt(json.dMin);
                document.getElementById('statMax').textContent    = fmt(json.dMax);
                document.getElementById('statAvg').textContent    = fmt(json.dAvg);
                document.getElementById('statStdDev').textContent = fmt(json.dStdDev);
                var statDiffCol = document.getElementById('statDiffCol');
                if (json.dDiff != null) {
                    document.getElementById('statDiff').textContent = fmt(json.dDiff);
                    statDiffCol.classList.remove('d-none');
                } else {
                    statDiffCol.classList.add('d-none');
                }
                statsRow.style.display = '';
            } else {
                var validResults = results.filter(function (r) { return r.nCount > 0; });
                var hasDiff = validResults.some(function (r) { return r.dDiff != null; });
                document.getElementById('multiStatsThDiff').classList.toggle('d-none', !hasDiff);
                document.getElementById('multiStatsTbody').innerHTML = validResults
                    .map(function (json) {
                        var fmt = function (v) { return v != null ? Number(v).toFixed(3) + (json.szUnit ? ' ' + json.szUnit : '') : '--'; };
                        var szDiffCell = hasDiff ? '<td class="text-end">' + fmt(json.dDiff) + '</td>' : '';
                        return '<tr>'
                            + '<td class="fw-semibold">' + json.szName + '</td>'
                            + '<td class="text-end">' + fmt(json.dMin) + '</td>'
                            + '<td class="text-end">' + fmt(json.dMax) + '</td>'
                            + '<td class="text-end">' + fmt(json.dAvg) + '</td>'
                            + '<td class="text-end">' + fmt(json.dStdDev) + '</td>'
                            + szDiffCell
                            + '</tr>';
                    }).join('');
                multiStatsWrapper.style.display = '';
            }

            buildChart(results.filter(function (r) { return r.nCount > 0; }).map(function (json) {
                var rawData = json.data.map(function (d) { return { x: d.t, y: d.v }; });
                var quals   = json.data.map(function (d) { return d.q; });
                var held    = holdLastGoodValue(rawData, quals);
                return {
                    label:        json.szName,
                    szUnit:       json.szUnit,
                    data:         held.data,
                    qualities:    quals,
                    originalVals: held.originalVals
                };
            }));

            document.getElementById('btnExportExcel').classList.remove('d-none');
            document.getElementById('footerQueryTime').textContent =
                t('history.chart.completed_at', { 0: new Date().toLocaleTimeString() });

            if (results.length === 1) {
                var json2 = results[0];
                document.getElementById('tableCount').textContent = json2.nCount.toLocaleString();
                dataTableBody.innerHTML = json2.data.map(function (d) {
                    return '<tr>'
                        + '<td><small>' + d.t.replace('T', ' ') + '</small></td>'
                        + '<td class="text-end fw-bold">' + Number(d.v).toFixed(3) + '</td>'
                        + '<td><span class="' + (d.q === 'Good' ? 'text-success' : 'text-danger') + '">' + d.q + '</span></td>'
                        + '</tr>';
                }).join('');
                dataTableCard.classList.remove('d-none');
            }

        }).catch(function (err) {
            loadingSpinner.classList.add('d-none');
            btnQuery.disabled = false;
            noDataMsg.innerHTML = '<i class="fas fa-exclamation-circle fa-3x mb-3 d-block text-danger opacity-50"></i><div>' + t('history.chart.query_failed', { 0: err.message }) + '</div>';
            noDataMsg.classList.remove('d-none');
        });
    }

    // ── 匯出 Excel ──────────────────────────────────────────────────────
    // 伺服器端產檔（POST /api/history/export）：各點位資料 + 「統計」+「圖表」分頁，
    // 沿用查詢當下參數重查；超過系統參數「匯出儲存格上限」回 400 export_limit
    function exportToExcel() {
        if (!lastQuery || !lastResults || !lastResults.some(function (r) { return r.nCount > 0; })) {
            showAlert(t('history.alert.no_export_data'), 'warning');
            return;
        }

        var btn = document.getElementById('btnExportExcel');
        var szBtnHtml = btn.innerHTML;
        btn.disabled = true;
        btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>' + t('history.chart.button.exporting');

        fetch('/api/history/export', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(lastQuery)
        }).then(function (res) {
            if (res.ok) {
                return res.blob().then(function (blob) {
                    downloadBlob(blob, fileNameFromDisposition(res.headers.get('Content-Disposition')));
                });
            }
            return res.json().catch(function () { return {}; }).then(function (err) {
                if (err.code === 'export_limit') {
                    showAlert(t('history.alert.export_limit', {
                        0: Number(err.nEstimatedCells).toLocaleString(),
                        1: Number(err.nMaxCells).toLocaleString()
                    }), 'warning', 10000);
                } else {
                    showAlert(t('history.alert.export_failed'), 'danger', 6000);
                }
            });
        }).catch(function () {
            showAlert(t('history.alert.export_failed'), 'danger', 6000);
        }).then(function () {
            btn.disabled = false;
            btn.innerHTML = szBtnHtml;
        });
    }

    // Content-Disposition 優先取 RFC 5987 filename*（中文檔名），否則 filename
    function fileNameFromDisposition(szHeader) {
        if (szHeader) {
            var m = szHeader.match(/filename\*=UTF-8''([^;]+)/i);
            if (m) return decodeURIComponent(m[1]);
            m = szHeader.match(/filename="?([^";]+)"?/i);
            if (m) return m[1];
        }
        return 'history.xlsx';
    }

    function downloadBlob(blob, szFileName) {
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = szFileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    }

    // ── 工具函式 ─────────────────────────────────────────────────────────
    function fmtDtLocal(dt) {
        var p = function (n) { return String(n).padStart(2, '0'); };
        return dt.getFullYear() + '-' + p(dt.getMonth()+1) + '-' + p(dt.getDate()) + ' ' + p(dt.getHours()) + ':' + p(dt.getMinutes());
    }

    function ceilToMinute(d) {
        if (d.getSeconds() > 0 || d.getMilliseconds() > 0) {
            return new Date(d.getFullYear(), d.getMonth(), d.getDate(),
                            d.getHours(), d.getMinutes() + 1, 0, 0);
        }
        return d;
    }

    function showAlert(msg, type, nDurationMs) {
        type = type || 'warning';
        var el = document.createElement('div');
        el.className = 'alert alert-' + type + ' alert-dismissible fade show mt-2';
        el.innerHTML = '<i class="fas fa-exclamation-triangle me-2"></i>' + msg + '<button type="button" class="btn-close" data-bs-dismiss="alert"></button>';
        var container = document.querySelector('.container-fluid');
        container.insertBefore(el, container.firstChild);
        setTimeout(function () { el.remove(); }, nDurationMs || 3000);
    }

    // ── 事件綁定 ─────────────────────────────────────────────────────────
    document.addEventListener('DOMContentLoaded', function () {

        if (window._fpInit) {
            window._fpInit.datetime(dtStart);
            window._fpInit.datetime(dtEnd);
        }

        renderPointList(currentCoordinatorId, currentModbusId, null, null);
        renderBasket();

        // ── 外部導航預載（ScadaPage 趨勢圖右鍵選單）──
        (function checkTrendPreload() {
            try {
                var raw = localStorage.getItem('SCADA_TREND_PRELOAD');
                if (!raw) return;
                localStorage.removeItem('SCADA_TREND_PRELOAD');
                var payload = JSON.parse(raw);
                if (!payload.sids || !payload.sids.length) return;
                var items = payload.sids.map(function (s) {
                    var match = allPoints.find(function (p) { return p.sid === s.sid; });
                    return {
                        sid:  s.sid,
                        name: match ? (match.fullName || match.name) : (s.name || s.sid),
                        unit: match ? match.unit : (s.unit || '')
                    };
                });
                addToBasket(items);
            } catch (e) {
                console.warn('Trend preload parse error:', e);
            }
        })();

        function clearAllCoordinatorActive() {
            document.querySelectorAll('.coordinator-item, .coordinator-toggle, .sub-item')
                .forEach(function (el) { el.classList.remove('active'); });
        }

        function applySelectionFromEl(el) {
            var prefix = el.dataset.sidPrefix;
            if (prefix) {
                currentSidPrefix = prefix;
                currentCoordinatorId = 0;
                currentModbusId = null;
                currentCalcGroup = null;
                currentDeviceGroup = null;
            } else if (el.hasAttribute('data-devicegroup')) {
                // 單站號內 Device 子設備（'' = 未分群桶）
                currentSidPrefix = null;
                currentCoordinatorId = parseInt(el.dataset.id) || 0;
                currentModbusId = null;
                currentCalcGroup = null;
                currentDeviceGroup = el.dataset.devicegroup;
            } else {
                currentSidPrefix = null;
                currentCoordinatorId = parseInt(el.dataset.id) || 0;
                currentModbusId = el.dataset.modbusid != null && el.dataset.modbusid !== ''
                    ? (parseInt(el.dataset.modbusid) || null)
                    : null;
                currentCalcGroup = el.dataset.calcgroup != null ? el.dataset.calcgroup : null;
                currentDeviceGroup = null;
            }
            renderPointList(currentCoordinatorId, currentModbusId, currentCalcGroup, currentSidPrefix, currentDeviceGroup);
        }

        document.querySelectorAll('.coordinator-item').forEach(function (item) {
            item.addEventListener('click', function (e) {
                e.preventDefault();
                clearAllCoordinatorActive();
                this.classList.add('active');
                applySelectionFromEl(this);
            });
        });

        document.querySelectorAll('.coordinator-toggle').forEach(function (toggle) {
            toggle.addEventListener('click', function (e) {
                e.preventDefault();
                var subMenu = this.nextElementSibling;
                var icon = this.querySelector('.toggle-icon');
                if (subMenu.style.display === 'none') {
                    subMenu.style.display = '';
                    icon.classList.add('open');
                } else {
                    subMenu.style.display = 'none';
                    icon.classList.remove('open');
                }
                clearAllCoordinatorActive();
                this.classList.add('active');
                applySelectionFromEl(this);
            });
        });

        document.querySelectorAll('.sub-item').forEach(function (item) {
            item.addEventListener('click', function (e) {
                e.preventDefault();
                clearAllCoordinatorActive();
                this.classList.add('active');
                applySelectionFromEl(this);
            });
        });

        document.getElementById('btnCheckAll').addEventListener('click', function () {
            pointListContainer.querySelectorAll('.point-checkbox').forEach(function (cb) { cb.checked = true; });
        });
        document.getElementById('btnUncheckAll').addEventListener('click', function () {
            pointListContainer.querySelectorAll('.point-checkbox').forEach(function (cb) { cb.checked = false; });
        });

        document.getElementById('btnAddToBasket').addEventListener('click', function () {
            var checked = Array.from(pointListContainer.querySelectorAll('.point-checkbox:checked'));
            if (checked.length === 0) { showAlert(t('history.alert.no_points_selected'), 'warning'); return; }
            addToBasket(checked.map(function (cb) { return { sid: cb.value, name: cb.dataset.name, unit: cb.dataset.unit }; }));
            checked.forEach(function (cb) { cb.checked = false; });
        });

        document.getElementById('btnClearBasket').addEventListener('click', function () {
            basket = [];
            renderBasket();
        });

        btnQuery.addEventListener('click', doQuery);

        btnClear.addEventListener('click', function () {
            destroyAllCharts();
            lastResults = null;
            isSingleAxis = false;
            var btnAxis = document.getElementById('btnSingleAxis');
            btnAxis.classList.add('d-none');
            btnAxis.classList.remove('pressed');
            btnAxis.innerHTML = '<i class="fas fa-arrows-alt-v me-1"></i>' + t('history.chart.axis_dual');
            document.getElementById('btnExportExcel').classList.add('d-none');
            chartContainer.style.display = 'none';
            document.getElementById('stackedChartsContainer').style.display = 'none';
            noDataMsg.innerHTML = '<i class="fas fa-chart-line fa-3x mb-3 d-block opacity-25"></i><div>' + t('history.chart.placeholder') + '</div>';
            noDataMsg.classList.remove('d-none');
            statsRow.style.display = 'none';
            multiStatsWrapper.style.display = 'none';
            dataTableCard.classList.add('d-none');
            limitWarning.classList.add('d-none');
            chartTitle.innerHTML = '<i class="fas fa-chart-area text-primary me-2"></i>' + t('history.chart.title.empty');
            document.getElementById('footerQueryTime').textContent = '';
        });

        document.querySelectorAll('.quick-range').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var h = parseInt(this.dataset.hours);
                var now = ceilToMinute(new Date());
                var start = new Date(now - h * 3600000);
                if (dtStart._flatpickr) {
                    dtStart._flatpickr.setDate(start, true);
                    dtEnd._flatpickr.setDate(now, true);
                } else {
                    dtStart.value = fmtDtLocal(start);
                    dtEnd.value   = fmtDtLocal(now);
                }
            });
        });

        document.querySelectorAll('#chartTypeGroup button').forEach(function (btn) {
            btn.addEventListener('click', function () {
                document.querySelectorAll('#chartTypeGroup button').forEach(function (b) { b.classList.remove('active'); });
                this.classList.add('active');
                currentChartType = this.dataset.type;
                if (trendChart || stackedCharts.length > 0) doQuery();
            });
        });

        document.getElementById('btnSingleAxis').addEventListener('click', function () {
            isSingleAxis = !isSingleAxis;
            this.classList.toggle('pressed', isSingleAxis);
            this.innerHTML = isSingleAxis
                ? '<i class="fas fa-arrows-alt-v me-1"></i>' + t('history.chart.axis_single')
                : '<i class="fas fa-arrows-alt-v me-1"></i>' + t('history.chart.axis_dual');
            if (lastResults) {
                buildChart(lastResults.filter(function (r) { return r.nCount > 0; }).map(function (json) {
                    var rawData = json.data.map(function (d) { return { x: d.t, y: d.v }; });
                    var quals   = json.data.map(function (d) { return d.q; });
                    var held    = holdLastGoodValue(rawData, quals);
                    return {
                        label:        json.szName,
                        szUnit:       json.szUnit,
                        data:         held.data,
                        qualities:    quals,
                        originalVals: held.originalVals
                    };
                }));
            }
        });

        btnToggleTable.addEventListener('click', function () {
            var hidden = tableBodyEl.classList.contains('d-none');
            tableBodyEl.classList.toggle('d-none', !hidden);
            btnToggleTable.innerHTML = hidden
                ? '<i class="fas fa-chevron-up me-1"></i>' + t('history.table.button.collapse')
                : '<i class="fas fa-chevron-down me-1"></i>' + t('history.table.button.expand');
        });

        // ── 深連結（ScadaPage 右鍵「歷史趨勢」開新分頁）──
        // /HistoryData?sid=A&sid=B&hours=1  或  ?sid=A&start=yyyy-MM-ddTHH:mm&end=yyyy-MM-ddTHH:mm
        // 帶入待查詢清單 + 設好時間 + 自動查詢；時間參數缺 / 無效 → 預設近 1 小時
        (function applyDeepLink() {
            var qs = new URLSearchParams(location.search);
            var sids = [];
            qs.getAll('sid').forEach(function (v) {
                (v || '').split(',').forEach(function (s) {
                    s = s.trim();
                    if (s && sids.indexOf(s) < 0) sids.push(s);
                });
            });
            if (!sids.length) return;

            addToBasket(sids.map(function (sid) {
                var match = allPoints.find(function (p) { return p.sid === sid; });
                return {
                    sid:  sid,
                    name: match ? (match.fullName || match.name) : sid,
                    unit: match ? match.unit : ''
                };
            }));

            var start = qs.get('start') ? new Date(qs.get('start').replace(' ', 'T')) : null;
            var end   = qs.get('end')   ? new Date(qs.get('end').replace(' ', 'T'))   : null;
            if (!start || !end || isNaN(start) || isNaN(end) || start >= end) {
                var h = parseFloat(qs.get('hours'));
                if (!(h > 0)) h = 1;
                end = ceilToMinute(new Date());
                start = new Date(end - h * 3600000);
            }
            if (dtStart._flatpickr) {
                dtStart._flatpickr.setDate(start, true);
                dtEnd._flatpickr.setDate(end, true);
            } else {
                dtStart.value = fmtDtLocal(start);
                dtEnd.value   = fmtDtLocal(end);
            }

            // 查詢結果標題 / 提示走 i18n，等字典載入後再查
            if (window.i18n && window.i18n.ready) window.i18n.ready(doQuery);
            else doQuery();
        })();
    });

    // ── 對外介面（供 onclick 等屬性呼叫）──────────────────────────────────
    window._history = {
        exportToExcel:   exportToExcel,
        removeFromBasket: removeFromBasket
    };

})();
