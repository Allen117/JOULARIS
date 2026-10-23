(function () {
    'use strict';

    var REFRESH_MS = 60000;
    var MAIN_METER_REFRESH_MS = 5000;
    var _circuitId = '';
    var _refreshTimer = null;
    var _chart = null;
    var _dotTooltip = null;
    var _metas = []; // 與 chart labels 同步的狀態陣列：'ok' | 'bad' | 'missing' | 'future'

    // 主要電表資訊卡狀態
    var _mmMode = null;   // 'realtime-by-sid'（實體主表）| 'aggregated'（虛擬主表）
    var _mmFields = [];   // 實體：[{ key:'Voltage', sid }]；虛擬：[{ key:'Voltage' }] — 純 key，值由聚合 API 給
    var _mmTimer = null;

    // 點位未設定單位時的預設單位（PF 為無因次量，不顯示單位）
    var MM_DEFAULT_UNITS = { Voltage: 'V', Current: 'A', Power: 'kW', PowerFactor: '' };

    // ── 初始化 ───────────────────────────────────────────────
    // 卡片可由 /EmsCardSetting 關閉（DOM 不渲染），主表卡與需量卡各自以根元素存在與否防呆
    function init() {
        loadMainMeterInfo(); // 內部以 mainMeterCardWrap 防呆

        var sel = document.getElementById('demandCircuitSelect');
        if (!sel) return; // 需量卡未啟用 → 跳過綁定與輪詢

        var dot = document.getElementById('demandStatusDot');
        if (dot && window.bootstrap) {
            _dotTooltip = new bootstrap.Tooltip(dot, { trigger: 'hover', placement: 'right' });
        }

        loadCircuits();

        sel.addEventListener('change', function () {
            _circuitId = this.value;
            clearTimeout(_refreshTimer);
            if (_circuitId) refresh();
            else clearDisplay();
        });
    }

    // ── 主要電表資訊卡 ───────────────────────────────────────
    // 載入一次綁定資訊（節點名 + 4 組資料），依 mode 決定即時值來源：
    //   mode='realtime-by-sid'（實體主表）→ 走既有 /api/realtime/by-sids
    //   mode='aggregated'（虛擬主表）→ 走新的 /EMS/api/main-meter-values（後端聚合）
    // 舊 payload 無 mode 欄位時 fallback 為 by-sid（向下相容）
    function loadMainMeterInfo() {
        var wrap = document.getElementById('mainMeterCardWrap');
        if (!wrap) return;
        var szNotBound = wrap.getAttribute('data-notbound') || '未設定';

        fetch('/EMS/api/main-meter-info')
            .then(function (r) { return r.json(); })
            .then(function (info) {
                if (!info.hasMainMeter) return; // 無主要電表 → 整卡隱藏

                document.getElementById('mainMeterName').textContent = info.name || '';
                _mmMode = info.mode || 'realtime-by-sid';
                _mmFields = [];

                [
                    { key: 'Voltage', binding: info.voltage },
                    { key: 'Current', binding: info.current },
                    { key: 'Power', binding: info.power },
                    { key: 'PowerFactor', binding: info.powerFactor }
                ].forEach(function (f) {
                    var elName = document.getElementById('mm' + f.key + 'Name');
                    var elValue = document.getElementById('mm' + f.key + 'Value');
                    var elUnit = document.getElementById('mm' + f.key + 'Unit');

                    if (_mmMode === 'aggregated') {
                        // 虛擬主表：不顯示點位名（來源是子孫葉子聚合，寫死名稱反而誤導），僅顯示數值 + 單位
                        if (f.binding && f.binding.unit != null) {
                            elName.textContent = '';
                            elUnit.textContent = f.binding.unit || MM_DEFAULT_UNITS[f.key] || '';
                            _mmFields.push({ key: f.key });
                        } else {
                            elName.textContent = '';
                            elValue.textContent = szNotBound;
                            elValue.style.fontSize = '1rem';
                            elValue.style.color = '#9e9e9e';
                            elUnit.textContent = '';
                        }
                    } else {
                        // 實體主表：既有行為
                        if (f.binding && f.binding.sid) {
                            elName.textContent = f.binding.pointName || f.binding.sid;
                            elUnit.textContent = f.binding.unit || MM_DEFAULT_UNITS[f.key] || '';
                            _mmFields.push({ key: f.key, sid: f.binding.sid });
                        } else {
                            elName.textContent = '';
                            elValue.textContent = szNotBound;
                            elValue.style.fontSize = '1rem';
                            elValue.style.color = '#9e9e9e';
                            elUnit.textContent = '';
                        }
                    }
                });

                wrap.style.display = '';
                if (_mmFields.length > 0) {
                    if (_mmMode === 'aggregated') refreshMainMeterAggregated();
                    else refreshMainMeter();
                }
            })
            .catch(function (e) { console.error('載入主要電表資訊失敗', e); });
    }

    function refreshMainMeter() {
        clearTimeout(_mmTimer);
        fetch('/api/realtime/by-sids', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(_mmFields.map(function (f) { return f.sid; }))
        }).then(function (r) { return r.json(); })
          .then(function (res) {
              if (!res.success || !res.data) return;
              var map = {};
              res.data.forEach(function (d) { map[d.sid] = d; });
              _mmFields.forEach(function (f) {
                  var el = document.getElementById('mm' + f.key + 'Value');
                  var d = map[f.sid];
                  var v = d ? parseFloat(d.value) : NaN;
                  el.textContent = isNaN(v) ? '--' : formatMainMeterValue(f.key, v);
              });
          })
          .catch(function (e) { console.error('刷新主要電表即時值失敗', e); })
          .finally(function () {
              _mmTimer = setTimeout(refreshMainMeter, MAIN_METER_REFRESH_MS);
          });
    }

    // 主要電表數值格式：功率因數 3 位小數，其餘（電壓/電流/功率）1 位
    function formatMainMeterValue(szKey, dValue) {
        return szKey === 'PowerFactor' ? dValue.toFixed(3) : dValue.toFixed(1);
    }

    // 虛擬主表：拉後端聚合值（4 個 number|null）
    function refreshMainMeterAggregated() {
        clearTimeout(_mmTimer);
        fetch('/EMS/api/main-meter-values')
            .then(function (r) { return r.json(); })
            .then(function (res) {
                // 欄位名對應 DTO：voltage / current / power / powerFactor
                var mapping = {
                    'Voltage': res.voltage,
                    'Current': res.current,
                    'Power': res.power,
                    'PowerFactor': res.powerFactor
                };
                _mmFields.forEach(function (f) {
                    var el = document.getElementById('mm' + f.key + 'Value');
                    var v = mapping[f.key];
                    el.textContent = v == null ? '--' : formatMainMeterValue(f.key, v);
                });
            })
            .catch(function (e) { console.error('刷新主要電表聚合值失敗', e); })
            .finally(function () {
                _mmTimer = setTimeout(refreshMainMeterAggregated, MAIN_METER_REFRESH_MS);
            });
    }

    // ── 載入迴路下拉 ─────────────────────────────────────────
    // 預設優先選主要電表（若其本身有設定需量、在選單內），否則退回清單第一筆
    function loadCircuits() {
        Promise.all([
            fetch('/EMS/api/demand-circuits').then(function (r) { return r.json(); }),
            fetch('/EMS/api/main-meter').then(function (r) { return r.json(); }).catch(function () { return null; })
        ])
            .then(function (res) {
                var list = res[0];
                var main = res[1];
                var sel = document.getElementById('demandCircuitSelect');
                if (!list || list.length === 0) {
                    sel.innerHTML = '<option value="">尚無設定需量的迴路</option>';
                    return;
                }
                sel.innerHTML = list.map(function (c) {
                    return '<option value="' + escAttr(c.id) + '">' + escHtml(c.name) + '</option>';
                }).join('');

                var mainId = (main && main.hasMainMeter) ? String(main.id) : null;
                var hasMainInList = mainId && list.some(function (c) { return String(c.id) === mainId; });
                _circuitId = hasMainInList ? mainId : String(list[0].id);
                sel.value = _circuitId;
                refresh();
            })
            .catch(function (e) { console.error('載入需量迴路失敗', e); });
    }

    // ── 刷新（今日數值 + 趨勢圖同時更新）────────────────────
    function refresh() {
        if (!_circuitId) return;
        clearTimeout(_refreshTimer);

        Promise.all([
            fetch('/EMS/api/demand-today?circuitId=' + encodeURIComponent(_circuitId)).then(function (r) { return r.json(); }),
            fetch('/EMS/api/demand-trend?circuitId=' + encodeURIComponent(_circuitId)).then(function (r) { return r.json(); })
        ]).then(function (res) {
            renderValue(res[0]);
            renderChart(res[1]);
        }).catch(function (e) {
            console.error('刷新需量失敗', e);
        }).finally(function () {
            _refreshTimer = setTimeout(refresh, REFRESH_MS);
        });
    }

    // ── 渲染即時數值 ─────────────────────────────────────────
    function renderValue(data) {
        var elKW    = document.getElementById('demandCurrentKW');
        var elMax   = document.getElementById('demandMaxKW');
        var elMaxAt = document.getElementById('demandMaxAt');
        var elDot   = document.getElementById('demandStatusDot');

        if (!data.hasData) {
            elKW.textContent    = '--';
            elMax.textContent   = '--';
            elMaxAt.textContent = '';
            setDot(elDot, '#bdbdbd', '今日尚無資料');
            return;
        }

        elKW.textContent    = data.currentKW != null ? data.currentKW.toFixed(1) : '--';
        elMax.textContent   = data.maxKW != null ? data.maxKW.toFixed(1) + ' kW' : '--';
        elMaxAt.textContent = data.maxAt ? ' (' + data.maxAt + ')' : '';

        if (data.quality === 1) {
            setDot(elDot, '#66bb6a', '資料正常（15 分鐘滾動加權平均）');
        } else {
            setDot(elDot, '#ffb300', '資料不足（視窗內有效樣本 < 5）');
        }
    }

    function setDot(el, color, tipText) {
        if (!el) return;
        el.style.background = color;
        el.setAttribute('data-bs-original-title', tipText);
    }

    // ── 建立全日 1440 點序列（含 step-hold 補值）────────────
    function buildSeries(points) {
        // 以 "HH:mm" 為 key 的快速查找表
        var map = {};
        points.forEach(function (p) { map[p.t] = p; });

        var now     = new Date();
        var nowMin  = now.getHours() * 60 + now.getMinutes();
        var labels  = [];
        var values  = [];
        var metas   = [];
        var lastGood = null; // 最近一次 Quality=1 的值（用於 forward-fill）

        // 前向填充：逐分鐘建立全日序列
        for (var m = 0; m <= 1439; m++) {
            var lbl = pad2(Math.floor(m / 60)) + ':' + pad2(m % 60);
            labels.push(lbl);

            if (m > nowMin) {
                values.push(null);
                metas.push('future');
                continue;
            }

            var pt = map[lbl];
            if (!pt) {
                // 無資料：沿用前一個好值（水平線），可能為 null（圖線斷開）
                values.push(lastGood);
                metas.push('missing');
            } else if (pt.q === 1) {
                lastGood = pt.v;
                values.push(pt.v);
                metas.push('ok');
            } else {
                // Quality=0（資料不足）：沿用前一個好值，不更新 lastGood
                values.push(lastGood);
                metas.push('bad');
            }
        }

        // 後向填充：將最初段的 null（尚無任何好值前）補成第一個好值
        var firstGood = null;
        for (var i = 0; i < values.length; i++) {
            if (metas[i] !== 'future' && values[i] !== null) { firstGood = values[i]; break; }
        }
        if (firstGood !== null) {
            for (var i = 0; i < values.length; i++) {
                if (metas[i] === 'future') break;
                if (values[i] === null) values[i] = firstGood;
                else break;
            }
        }

        return { labels: labels, values: values, metas: metas };
    }

    // ── 渲染趨勢折線圖 ───────────────────────────────────────
    function renderChart(points) {
        var canvas = document.getElementById('demandTrendChart');
        if (!canvas || !window.Chart) return;

        var series = buildSeries(points);
        _metas = series.metas; // 供 tooltip callback 讀取

        if (_chart) {
            _chart.data.labels             = series.labels;
            _chart.data.datasets[0].data   = series.values;
            _chart.update('none');
            return;
        }

        _chart = new Chart(canvas.getContext('2d'), {
            type: 'line',
            data: {
                labels: series.labels,
                datasets: [{
                    data: series.values,
                    borderColor: '#43a047',
                    backgroundColor: 'rgba(67,160,71,0.08)',
                    fill: true,
                    stepped: 'before',   // 水平延伸到下一點再垂直跳
                    pointRadius: 0,
                    pointHoverRadius: 3,
                    borderWidth: 1.5,
                    spanGaps: false      // null（未來時段）處斷線
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        callbacks: {
                            label: function (ctx) {
                                if (ctx.parsed.y === null || ctx.parsed.y === undefined) return null;
                                var status = _metas[ctx.dataIndex];
                                if (status === 'missing') return '無資料';   // 無資料
                                if (status === 'bad')     return '資料不足'; // 資料不足
                                return ctx.parsed.y.toFixed(1) + ' kW';
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        ticks: {
                            maxTicksLimit: 8,
                            font: { size: 10 },
                            color: '#9e9e9e'
                        },
                        grid: { display: false }
                    },
                    y: {
                        beginAtZero: true,
                        ticks: {
                            font: { size: 10 },
                            color: '#9e9e9e',
                            maxTicksLimit: 6,
                            // 依數值級距自動決定小數位數，避免 toFixed(0) 把 0.5 壓成 0 造成整排重複（比照 CircuitInfo Y 軸）
                            callback: function (v) {
                                if (v == null) return '';
                                if (v === 0) return '0';
                                var abs = Math.abs(v);
                                if (abs >= 10) return v.toFixed(0);
                                if (abs >= 1)  return (+v.toFixed(1)).toString();
                                return (+v.toFixed(2)).toString();
                            }
                        },
                        grid: { color: 'rgba(0,0,0,0.04)' }
                    }
                }
            }
        });
    }

    // ── 清空顯示 ─────────────────────────────────────────────
    function clearDisplay() {
        var elKW  = document.getElementById('demandCurrentKW');
        var elMax = document.getElementById('demandMaxKW');
        var elAt  = document.getElementById('demandMaxAt');
        if (elKW)  elKW.textContent  = '--';
        if (elMax) elMax.textContent = '--';
        if (elAt)  elAt.textContent  = '';
        setDot(document.getElementById('demandStatusDot'), '#bdbdbd', '');

        if (_chart) {
            _chart.data.labels           = [];
            _chart.data.datasets[0].data = [];
            _metas = [];
            _chart.update('none');
        }
    }

    // ── 工具函式 ─────────────────────────────────────────────
    function pad2(n) { return n < 10 ? '0' + n : String(n); }

    function escHtml(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function escAttr(s) {
        return String(s).replace(/"/g, '&quot;');
    }

    window._ems = { refresh: refresh };

    document.addEventListener('DOMContentLoaded', init);
})();
