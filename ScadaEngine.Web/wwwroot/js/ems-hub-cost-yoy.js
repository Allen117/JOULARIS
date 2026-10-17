/* EMS Hub — 電費去年同期比較卡。
   主要電表 + 各直接子迴路，本期 vs 去年同期的「流動電費（元）」比較。
   自帶日/月/年切換（預設月），不與能源卡的 pdGranGroup 耦合 → 單獨開啟也能運作。
   「月」依帳單期別切界（後端 ParsePivotAsync/GetCostReportAsync），與電費報表一致。
   UI 粒度對應後端：日→hour、月→day、年→month。資料源 GET /EMS/api/main-meter-cost-yoy，60s 輪詢。 */
(function () {
    'use strict';

    var REFRESH_MS = 60000;
    var NO_MAIN_METER_MSG = '尚未設定主要電表\n請至「電表/迴路設定」頁勾選一顆主要電表';

    var _hasMeter = false;
    var _gran = 'day';          // 預設「月」檢視
    var _inputs = null;
    var _timer = null;

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
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    // 金額格式（元，0 位小數；千分位）
    function fmtNtd(v) {
        if (v == null) return '--';
        return v.toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
    }

    // 去年同期 pivot 顯示字串（2/29 → 2/28，與後端 LastYearPivot 行為一致）
    function lastYearPivotStr(gran, pivot) {
        if (gran === 'month') return String(parseInt(pivot, 10) - 1);
        var parts = pivot.split('-');
        var y = parseInt(parts[0], 10) - 1;
        if (gran === 'day') return y + '-' + parts[1];
        var mm = parts[1], dd = parts[2];
        if (mm === '02' && dd === '29') dd = '28';
        return y + '-' + mm + '-' + dd;
    }

    // ── 粒度切換元件（本卡自帶一組按鈕 + 三個 pivot 輸入框） ──
    function setupGranGroup() {
        var group = document.getElementById('costYoyGranGroup');
        if (!group) return null;
        var inputs = {
            hour:  document.getElementById('costYoyPivotDate'),
            day:   document.getElementById('costYoyPivotMonth'),
            month: document.getElementById('costYoyPivotYear')
        };
        inputs.hour.value  = todayStr();
        inputs.day.value   = thisMonthStr();
        inputs.month.value = thisYearStr();

        group.querySelectorAll('.ems-gran-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                group.querySelectorAll('.ems-gran-btn').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
                _gran = btn.dataset.gran;
                Object.keys(inputs).forEach(function (k) {
                    inputs[k].style.display = (k === _gran) ? '' : 'none';
                });
                load();
            });
        });
        Object.keys(inputs).forEach(function (k) {
            inputs[k].addEventListener('change', function () {
                if (this.value) { _gran = k; load(); }
            });
        });
        return inputs;
    }

    function pivotOf() {
        var v = _inputs && _inputs[_gran] ? _inputs[_gran].value : '';
        if (v) return v;
        return _gran === 'hour' ? todayStr() : _gran === 'day' ? thisMonthStr() : thisYearStr();
    }

    // ── 初始化 ───────────────────────────────────────────────
    // 卡片由 /EmsCardSetting 關閉時 DOM 不渲染 → 根元素不存在 → 整支不動作、不輪詢
    function init() {
        if (!document.getElementById('costYoyTableWrap')) return;
        _inputs = setupGranGroup();

        fetch('/EMS/api/main-meter')
            .then(function (r) { return r.json(); })
            .then(function (m) {
                if (!m.hasMainMeter) { showNoMainMeter(); return; }
                _hasMeter = true;
                load();
                _timer = setInterval(load, REFRESH_MS);
            })
            .catch(function (e) { console.error('[ems-hub-cost-yoy] 載入主要電表失敗', e); });
    }

    function showNoMainMeter() {
        var empty = document.getElementById('costYoyEmpty');
        var wrap  = document.getElementById('costYoyTableWrap');
        if (empty) { empty.textContent = NO_MAIN_METER_MSG; empty.style.display = ''; }
        if (wrap) wrap.style.display = 'none';
        var group = document.getElementById('costYoyGranGroup');
        if (group) group.querySelectorAll('.ems-gran-btn').forEach(function (b) { b.disabled = true; });
        ['costYoyPivotDate', 'costYoyPivotMonth', 'costYoyPivotYear'].forEach(function (id) {
            var el = document.getElementById(id);
            if (el) el.disabled = true;
        });
    }

    // ── 載入 + 渲染 ──────────────────────────────────────────
    function load() {
        if (!_hasMeter || !document.getElementById('costYoyTableWrap')) return;
        var pivot = pivotOf();
        var url = '/EMS/api/main-meter-cost-yoy?granularity=' + encodeURIComponent(_gran) +
                  '&pivot=' + encodeURIComponent(pivot);
        fetch(url)
            .then(function (r) { return r.json(); })
            .then(function (data) { render(data.rows || [], pivot, !!data.isEstimated); })
            .catch(function (e) { console.error('[ems-hub-cost-yoy] 比較表載入失敗', e); });
    }

    function render(rows, pivot, isEstimated) {
        var lastPivot = lastYearPivotStr(_gran, pivot);
        document.getElementById('costYoyThCurrent').textContent  = '本期電費 (元) (' + pivot + ')';
        document.getElementById('costYoyThLastYear').textContent = '去年同期 (元) (' + lastPivot + ')';

        var foot = document.getElementById('costYoyFoot');
        if (foot) foot.style.display = isEstimated ? '' : 'none';

        var tbody = document.getElementById('costYoyTableBody');
        if (rows.length === 0) {
            tbody.innerHTML = '<tr><td colspan="5" class="text-center text-muted py-3">無資料</td></tr>';
            return;
        }

        tbody.innerHTML = rows.map(function (r) {
            var diffTxt = (r.diffCost > 0 ? '+' : '') + fmtNtd(r.diffCost);
            var pctHtml = UiState.pctHtml(r.pctChange, 'ems-yoy-up', 'ems-yoy-down');
            var nameHtml = r.isMainMeter
                ? '<i class="fas fa-star ems-yoy-star me-1"></i><span class="fw-semibold">' + escHtml(r.name) + '</span>'
                : escHtml(r.name);
            return '<tr' + (r.isMainMeter ? ' class="ems-yoy-main-row"' : '') + '>' +
                   '<td>' + nameHtml + '</td>' +
                   '<td class="text-end">' + fmtNtd(r.currentCost) + '</td>' +
                   '<td class="text-end">' + fmtNtd(r.lastYearCost) + '</td>' +
                   '<td class="text-end">' + diffTxt + '</td>' +
                   '<td class="text-end">' + pctHtml + '</td>' +
                   '</tr>';
        }).join('');
    }

    document.addEventListener('DOMContentLoaded', init);
})();
