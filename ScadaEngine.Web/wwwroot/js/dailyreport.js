// 能源日報瀏覽頁 — 讀 /DailyReport/api/report?date= 渲染各區塊卡片
(function () {
    'use strict';

    var ALARM_COLLAPSE_LIMIT = 20;   // Web 頁預設收合前 20 筆
    var SEC = { alarm: 1, electricity: 2, water: 4, gas: 8, rth: 16, dayCompare: 32, mtdCompare: 64, insights: 128, compressorDiagnosis: 256 };

    var g_charts = {};          // energyKey -> Chart instance
    var g_alarmItems = [];
    var g_isAlarmExpanded = false;
    var g_sectionFlags = 255;

    document.addEventListener('DOMContentLoaded', function () {
        loadSetting().then(load);
        document.getElementById('drDate').addEventListener('change', load);
    });

    function $(id) { return document.getElementById(id); }
    function show(id, isShow) { $(id).classList.toggle('d-none', !isShow); }

    function loadSetting() {
        // SectionFlags 由設定決定 Web 頁與 Email 一致的顯示範圍；讀不到就全開
        return fetch('/DailyReport/api/setting')
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (setting) { if (setting) g_sectionFlags = setting.nSectionFlags; })
            .catch(function () { });
    }

    function load() {
        var szDate = $('drDate').value;
        if (!szDate) return;
        show('drLoading', true);
        show('drContent', false);
        show('drError', false);
        show('drBadges', false);

        fetch('/DailyReport/api/report?date=' + encodeURIComponent(szDate))
            .then(function (res) {
                if (!res.ok) {
                    return res.json().catch(function () { return {}; }).then(function (err) {
                        throw new Error(err.message || res.statusText);
                    });
                }
                return res.json();
            })
            .then(render)
            .catch(function (err) {
                show('drLoading', false);
                var el = $('drError');
                el.textContent = '日報載入失敗：' + err.message;
                show('drError', true);
            });
    }

    function render(payload) {
        var data = payload.data;
        show('drLoading', false);
        show('drContent', true);
        show('drBadges', true);

        // ── 徽章列 ──
        show('drBadgeHoliday', !!data.isReportDateHoliday);
        show('drBadgeStale', !!data.isStaleLastHour);
        show('drBadgeAdhoc', !payload.isSnapshot);
        var mailBadge = $('drBadgeMail');
        if (payload.isSnapshot) {
            var mailText = { 0: '未寄送', 1: '已寄送', 2: '寄送失敗', 3: '寄送停用' }[payload.nMailStatus] || '';
            mailBadge.textContent = 'Email：' + mailText;
            mailBadge.title = payload.szMailDetail || '';
            show('drBadgeMail', !!mailText);
        } else {
            show('drBadgeMail', false);
        }
        $('drGeneratedAt').textContent = '產生時間 ' + fmtDateTime(data.dtGeneratedAt);

        renderAlarms(data);
        renderHourly('electricity', data.electricity, SEC.electricity);
        renderHourly('water', data.water, SEC.water);
        renderHourly('gas', data.gas, SEC.gas);
        renderHourly('rth', data.rth, SEC.rth);
        renderDayCompare(data);
        renderMtd(data);
        renderInsights(data);
        renderCompressorDiagnosis(data);
    }

    // ── 警報摘要 ──
    function renderAlarms(data) {
        var isShow = hasFlag(SEC.alarm);
        show('drCardAlarm', isShow);
        if (!isShow) return;

        var a = data.alarm || { nOccurredCount: 0, nClearedCount: 0, nUnacknowledgedCount: 0, items: [] };
        $('drAlarmCounts').innerHTML =
            '發生 <b>' + a.nOccurredCount + '</b>　已恢復 <b>' + a.nClearedCount + '</b>　未確認 ' +
            '<b class="' + (a.nUnacknowledgedCount > 0 ? 'text-danger' : '') + '">' + a.nUnacknowledgedCount + '</b>';

        g_alarmItems = a.items || [];
        g_isAlarmExpanded = false;
        renderAlarmRows();
    }

    function renderAlarmRows() {
        var body = $('drAlarmBody');
        if (g_alarmItems.length === 0) {
            body.innerHTML = '<tr><td colspan="6" class="text-center text-muted py-3">前一日無警報/故障事件</td></tr>';
            show('drAlarmMoreWrap', false);
            return;
        }
        var items = g_isAlarmExpanded ? g_alarmItems : g_alarmItems.slice(0, ALARM_COLLAPSE_LIMIT);
        var html = '';
        items.forEach(function (item) {
            var szType = item.nEventType === 0 ? '警報' : '故障';
            var szTypeClass = item.nEventType === 0 ? 'text-danger' : 'text-warning';
            var szSeverity = ['緊急', '高', '中', '低'][Math.min(Math.max(item.nSeverity, 0), 3)];
            var szStatus = item.dtClearedAt ? '<span class="text-success">已恢復</span>' : '<span class="text-danger">未恢復</span>';
            var szAck = item.isAcknowledged ? '<i class="fas fa-check text-success"></i>' : '<span class="text-muted">—</span>';
            html += '<tr>' +
                '<td>' + fmtTime(item.dtOccurredAt) + '</td>' +
                '<td class="' + szTypeClass + '">' + szType + '</td>' +
                '<td>' + szSeverity + '</td>' +
                '<td>' + escapeHtml(item.szMessage) + '</td>' +
                '<td>' + szStatus + '</td>' +
                '<td class="text-center">' + szAck + '</td>' +
                '</tr>';
        });
        body.innerHTML = html;

        var isHasMore = g_alarmItems.length > ALARM_COLLAPSE_LIMIT;
        show('drAlarmMoreWrap', isHasMore);
        if (isHasMore) {
            $('drAlarmMoreBtn').innerHTML = g_isAlarmExpanded
                ? '<i class="fas fa-chevron-up me-1"></i>收合'
                : '<i class="fas fa-chevron-down me-1"></i>展開全部（共 ' + g_alarmItems.length + ' 筆）';
        }
    }

    function toggleAlarms() {
        g_isAlarmExpanded = !g_isAlarmExpanded;
        renderAlarmRows();
    }

    // ── 時報表 ──
    function renderHourly(key, section, flag) {
        var szCardId = 'drCard' + cap(key);
        var isShow = hasFlag(flag) && section && section.isAvailable;
        show(szCardId, isShow);
        if (!isShow) return;

        $('drName' + cap(key)).textContent = section.szCircuitName || '';
        $('drTotal' + cap(key)).textContent = fmtNum(section.dTotal, digitsOf(key));

        var labels = [];
        for (var i = 0; i < 24; i++) labels.push(String(i).padStart(2, '0') + ':00');
        var colors = section.dHourly.map(function (_, i) {
            return (section.isHourlyStale && section.isHourlyStale[i]) ? '#adb5bd' : undefined;
        });

        if (g_charts[key]) g_charts[key].destroy();
        var ctx = $('drChart' + cap(key)).getContext('2d');
        // 不指定顏色時交給 Chart.js 預設（吃 CSS variable 較難）— 統一給 primary 色，stale 灰色
        var baseColor = getComputedStyle(document.body).getPropertyValue('--bs-primary').trim() || '#0d6efd';
        g_charts[key] = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    data: section.dHourly,
                    backgroundColor: section.dHourly.map(function (_, i) { return colors[i] || baseColor; }),
                    borderWidth: 0
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        callbacks: {
                            label: function (c) {
                                var szStale = (section.isHourlyStale && section.isHourlyStale[c.dataIndex]) ? '（資料缺漏）' : '';
                                return fmtNum(c.parsed.y, digitsOf(key)) + ' ' + section.szUnit + szStale;
                            }
                        }
                    }
                },
                scales: {
                    x: { ticks: { maxRotation: 0, autoSkip: true, maxTicksLimit: 12 } },
                    y: { beginAtZero: true }
                }
            }
        });
    }

    // ── 單日比較 ──
    function renderDayCompare(data) {
        var isShow = hasFlag(SEC.dayCompare) && data.dayComparisons && data.dayComparisons.length > 0;
        show('drCardDayCompare', isShow);
        if (!isShow) return;

        var html = '';
        data.dayComparisons.forEach(function (row) {
            html += '<tr>' +
                '<td>' + energyName(row.szEnergy) + '（' + row.szUnit + '）</td>' +
                '<td class="text-end fw-bold">' + fmtNum(row.dDay, digitsOf(row.szEnergy)) + '</td>' +
                '<td class="text-end">' + fmtNum(row.dPrevDay, digitsOf(row.szEnergy)) + '</td>' +
                '<td class="text-end">' + fmtDiff(row.dDiffPrevPct) + '</td>' +
                '<td class="text-end">' + fmtNum(row.dLastWeek, digitsOf(row.szEnergy)) + '</td>' +
                '<td class="text-end">' + fmtDiff(row.dDiffLastWeekPct) + '</td>' +
                '</tr>';
        });
        if (data.efficiency) {
            var eff = data.efficiency;
            html += '<tr class="table-light">' +
                '<td>kWh/RTh（每冷凍噸耗電）</td>' +
                '<td class="text-end fw-bold">' + fmtNullable(eff.dDay) + '</td>' +
                '<td class="text-end">' + fmtNullable(eff.dPrevDay) + '</td>' +
                '<td class="text-end">' + fmtDiff(eff.dDiffPrevPct) + '</td>' +
                '<td class="text-end">' + fmtNullable(eff.dLastWeek) + '</td>' +
                '<td class="text-end">' + fmtDiff(eff.dDiffLastWeekPct) + '</td>' +
                '</tr>';
        }
        $('drDayCompareBody').innerHTML = html;

        // 報告日與上週同日一個放假、一個上班 → 「vs 上週」的比較基準已改變，明白標出來。
        // 兩天狀態相同時不顯示（同一個星期幾本來就同為平日或同為週末，狀態不同必然來自國定假日或補班日）。
        // 判定邏輯與後端 DailyReportInsightService.LastWeekBaselineShift 一致。
        var noteEl = $('drLastWeekNote');
        if (data.isReportDateHoliday !== data.isLastWeekHoliday) {
            noteEl.innerHTML = '<i class="fas fa-info-circle me-1"></i>比較基準不同：報告日為' +
                (data.isReportDateHoliday ? '假日' : '上班日') + '，上週同星期為' +
                (data.isLastWeekHoliday ? '假日' : '上班日') + '，「vs 上週」的差異僅供參考。';
            show('drLastWeekNote', true);
        } else {
            show('drLastWeekNote', false);
        }

        var weatherLine = $('drWeatherLine');
        if (data.weather && data.weather.dAvgTempDay != null) {
            weatherLine.textContent = '外氣日均溫：報告日 ' + fmtNullable(data.weather.dAvgTempDay) + '°C、前日 ' +
                fmtNullable(data.weather.dAvgTempPrevDay) + '°C、上週同星期 ' + fmtNullable(data.weather.dAvgTempLastWeek) + '°C';
            show('drWeatherLine', true);
        } else {
            show('drWeatherLine', false);
        }
    }

    // ── 月累計比較 ──
    function renderMtd(data) {
        var isShow = hasFlag(SEC.mtdCompare) && data.mtdComparisons && data.mtdComparisons.length > 0;
        show('drCardMtd', isShow);
        if (!isShow) return;

        var html = '';
        data.mtdComparisons.forEach(function (row) {
            html += '<tr>' +
                '<td>' + energyName(row.szEnergy) + '（' + row.szUnit + '）</td>' +
                '<td class="text-end fw-bold">' + fmtNum(row.dCurrent, digitsOf(row.szEnergy)) + '</td>' +
                '<td class="text-end">' + fmtNum(row.dLastYear, digitsOf(row.szEnergy)) + '</td>' +
                '<td class="text-end">' + fmtDiff(row.dDiffPct) + '</td>' +
                '</tr>';
        });
        $('drMtdBody').innerHTML = html;
        var first = data.mtdComparisons[0];
        $('drMtdRange').textContent = first.szCurrentRange + '　vs　' + first.szLastYearRange;
    }

    // ── 智慧摘要 ──
    function renderInsights(data) {
        var isShow = hasFlag(SEC.insights) && data.insights && data.insights.length > 0;
        show('drCardInsights', isShow);
        if (!isShow) return;

        var icons = { holiday: 'fa-calendar-day', weather: 'fa-cloud-sun', alarm: 'fa-bell', efficiency: 'fa-tachometer-alt', none: 'fa-info-circle' };
        var html = '';
        data.insights.forEach(function (insight) {
            var szIcon = icons[insight.szCategory] || 'fa-info-circle';
            html += '<li><i class="fas ' + szIcon + ' text-primary me-2"></i>' + escapeHtml(insight.szText) + '</li>';
        });
        $('drInsightList').innerHTML = html;
    }

    // ── 空壓系統診斷（文字已於快照生成時依日報語言渲染）──
    function renderCompressorDiagnosis(data) {
        var diag = data.compressorDiagnosis;
        var isShow = hasFlag(SEC.compressorDiagnosis) && !!diag;
        show('drCardCompressorDiag', isShow);
        if (!isShow) return;

        $('drCompressorDiagSummary').textContent = '診斷期間 ' + diag.szPeriod + '：觸發 ' + diag.nTriggered + ' 項、待補資料 ' + diag.nPending + ' 項';
        var html = (diag.items || []).map(function (item) {
            return '<tr><td class="fw-semibold">' + escapeHtml(item.szTitle) + (item.isUnconfirmedDefault ? ' <span class="badge bg-warning text-dark">*</span>' : '') + '</td>' +
                '<td class="text-nowrap">' + escapeHtml(item.szSavings || '—') + '</td>' +
                '<td>' + escapeHtml(item.szNextStep) + '</td></tr>';
        }).join('');
        $('drCompressorDiagBody').innerHTML = html || '<tr><td colspan="3" class="text-muted text-center">本期無觸發項目</td></tr>';
        $('drCompressorDiagNotices').innerHTML = (diag.aNotices || []).map(function (sz) {
            return '<div>' + escapeHtml(sz) + '</div>';
        }).join('');
    }

    // ── helpers ──
    function hasFlag(flag) { return (g_sectionFlags & flag) !== 0; }
    function cap(sz) { return sz.charAt(0).toUpperCase() + sz.slice(1); }
    function energyName(key) {
        return { electricity: '用電', water: '用水', gas: '用氣', rth: '冷凍噸' }[key] || key;
    }
    // nDigits 給定時固定顯示該位數（RT·h 一律 1 位，與冷凍噸報表／能源申報一致）；
    // 未給定 → 沿用各能源別原本的「最多 2 位」
    function fmtNum(d, nDigits) {
        if (d == null) return '—';
        if (nDigits == null) return Number(d).toLocaleString('en-US', { maximumFractionDigits: 2 });
        return Number(d).toLocaleString('en-US', {
            minimumFractionDigits: nDigits,
            maximumFractionDigits: nDigits
        });
    }
    /// 能源別 → 顯示位數（kWh 與 RT·h 一律 1 位，0 顯示為 0.0；用水/用氣 null = 預設最多 2 位）
    function digitsOf(szEnergy) {
        return (szEnergy === 'rth' || szEnergy === 'electricity') ? 1 : null;
    }
    function fmtNullable(d) { return d == null ? '—' : fmtNum(d); }
    function fmtDiff(d) {
        if (d == null) return '<span class="text-muted">—</span>';
        var szClass = d > 0 ? 'text-danger' : 'text-success';
        var szSign = d > 0 ? '+' : '';
        return '<span class="' + szClass + '">' + szSign + d.toFixed(1) + '%</span>';
    }
    function fmtTime(sz) {
        var dt = new Date(sz);
        return isNaN(dt) ? '' : String(dt.getHours()).padStart(2, '0') + ':' + String(dt.getMinutes()).padStart(2, '0') + ':' + String(dt.getSeconds()).padStart(2, '0');
    }
    function fmtDateTime(sz) {
        var dt = new Date(sz);
        if (isNaN(dt)) return '';
        var p = function (n) { return String(n).padStart(2, '0'); };
        return dt.getFullYear() + '-' + p(dt.getMonth() + 1) + '-' + p(dt.getDate()) + ' ' + p(dt.getHours()) + ':' + p(dt.getMinutes());
    }
    function escapeHtml(sz) {
        var div = document.createElement('div');
        div.textContent = sz == null ? '' : sz;
        return div.innerHTML;
    }

    window._dr = { load: load, toggleAlarms: toggleAlarms };
})();
