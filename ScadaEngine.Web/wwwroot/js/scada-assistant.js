/*
 * 右下角智慧助理對話窗（AI智慧幫手）
 * ------------------------------------------------------------------
 * 全站 widget：折疊氣泡 ↔ 展開對話窗。無自由文字輸入，只以腳本按鈕 / 日期控制項推進。
 * 以「逐句吐字 + 打字指示器 + 刻意延遲」製造 AI 問答感（非真 LLM）。
 *
 * 首個功能：區間效率分析
 *   menu → pickReport（GET /EnergyDeclaration/api/reports）
 *        → pickRange（原生 <input type="date">）
 *        → result（POST /EnergyDeclaration/api/interval-analysis）逐句吐出
 *
 * 所有字串走 window.i18n.t('assistant.*')；DOM 由 JS 建立（掛載點 #scadaAssistantRoot）。
 * 折疊 / 展開狀態記憶於 localStorage，對話內容保留在 DOM（縮放不清空）。
 */
(function () {
    'use strict';

    var LS_OPEN = 'sa_open';
    var GAP_MS = 380;          // 兩句訊息之間的間隔
    var MIN_TYPING = 500;      // 打字指示器最短顯示
    var MAX_TYPING = 1600;     // 打字指示器最長顯示
    var MS_PER_CHAR = 22;      // 依訊息長度估打字時間

    var _root, _bubble, _panel, _body, _actions;
    var _typingEl = null;
    var _hasStarted = false;   // 是否已顯示過首則問候（縮放後不重建）
    var _busy = false;         // 逐句吐字 / 分析中，鎖住互動避免重入

    function t(szKey, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(szKey, args) : szKey;
    }

    // ---------- DOM 建立 ----------

    function build() {
        _root = document.getElementById('scadaAssistantRoot');
        if (!_root) return;

        // 折疊氣泡
        _bubble = document.createElement('button');
        _bubble.type = 'button';
        _bubble.className = 'sa-bubble';
        _bubble.title = t('assistant.bubble_tooltip');
        _bubble.setAttribute('aria-label', t('assistant.bubble_tooltip'));
        _bubble.innerHTML = '<i class="fas fa-robot"></i>';
        _bubble.addEventListener('click', open);

        // 展開面板
        _panel = document.createElement('div');
        _panel.className = 'sa-panel';
        _panel.hidden = true;

        var header = document.createElement('div');
        header.className = 'sa-header';
        header.innerHTML =
            '<div class="sa-avatar"><i class="fas fa-robot"></i></div>' +
            '<div class="sa-h-text">' +
                '<div class="sa-h-title"></div>' +
                '<div class="sa-h-sub"><span class="sa-dot"></span><span class="sa-h-status"></span></div>' +
            '</div>';
        header.querySelector('.sa-h-title').textContent = t('assistant.title');
        header.querySelector('.sa-h-status').textContent = t('assistant.status_online');

        var btnMin = document.createElement('button');
        btnMin.type = 'button';
        btnMin.className = 'sa-btn-min';
        btnMin.title = t('assistant.minimize');
        btnMin.setAttribute('aria-label', t('assistant.minimize'));
        btnMin.innerHTML = '<i class="fas fa-minus"></i>';
        btnMin.addEventListener('click', close);
        header.appendChild(btnMin);

        _body = document.createElement('div');
        _body.className = 'sa-body';

        _panel.appendChild(header);
        _panel.appendChild(_body);

        _root.appendChild(_bubble);
        _root.appendChild(_panel);

        // 還原開合狀態
        if (localStorage.getItem(LS_OPEN) === '1') open();
    }

    // ---------- 開 / 合 ----------

    function open() {
        _bubble.hidden = true;
        _panel.hidden = false;
        localStorage.setItem(LS_OPEN, '1');
        if (!_hasStarted) {
            _hasStarted = true;
            startMenu();
        }
    }

    function close() {
        _panel.hidden = true;
        _bubble.hidden = false;
        localStorage.setItem(LS_OPEN, '0');
    }

    // ---------- 訊息基元 ----------

    function scrollBottom() {
        _body.scrollTop = _body.scrollHeight;
    }

    /** 附加一則 bot 氣泡（含小頭像）；kind = 'good' | 'warn' 時氣泡左側加色條（取代文案內的 emoji 判定符號） */
    function appendBot(szText, szKind) {
        var row = document.createElement('div');
        row.className = 'sa-row sa-row-bot';
        var avatar = document.createElement('div');
        avatar.className = 'sa-mini-avatar';
        avatar.innerHTML = '<i class="fas fa-robot"></i>';
        var msg = document.createElement('div');
        msg.className = 'sa-msg sa-msg-bot';
        if (szKind === 'good' || szKind === 'warn') msg.classList.add('sa-msg-' + szKind);
        msg.textContent = szText;
        row.appendChild(avatar);
        row.appendChild(msg);
        _body.appendChild(row);
        scrollBottom();
    }

    /** 附加一則 user 氣泡（回顯使用者的按鈕選擇，強化對話感） */
    function appendUser(szText) {
        var row = document.createElement('div');
        row.className = 'sa-row sa-row-user';
        var msg = document.createElement('div');
        msg.className = 'sa-msg sa-msg-user';
        msg.textContent = szText;
        row.appendChild(msg);
        _body.appendChild(row);
        scrollBottom();
    }

    function showTyping() {
        if (_typingEl) return;
        _typingEl = document.createElement('div');
        _typingEl.className = 'sa-row sa-row-bot';
        _typingEl.setAttribute('aria-label', t('assistant.typing_aria'));
        _typingEl.innerHTML =
            '<div class="sa-mini-avatar"><i class="fas fa-robot"></i></div>' +
            '<div class="sa-msg sa-msg-bot sa-typing"><span></span><span></span><span></span></div>';
        _body.appendChild(_typingEl);
        scrollBottom();
    }

    function hideTyping() {
        if (_typingEl) { _typingEl.remove(); _typingEl = null; }
    }

    function typingDelay(szText) {
        var d = MIN_TYPING + (szText ? szText.length * MS_PER_CHAR : 0);
        return Math.min(d, MAX_TYPING);
    }

    /**
     * 逐句吐出多則 bot 訊息：每句先顯示打字指示器（依長度延遲）→ 換成氣泡 → 間隔 → 下一句。
     * 每則可為字串或 { text, kind }（kind 見 appendBot）。
     * 全程 _busy=true，結束呼叫 onDone。
     */
    function botSay(aMessages, onDone) {
        _busy = true;
        var i = 0;
        function next() {
            if (i >= aMessages.length) {
                _busy = false;
                if (onDone) onDone();
                return;
            }
            var item = aMessages[i++];
            var szMsg = (item && typeof item === 'object') ? item.text : item;
            var szKind = (item && typeof item === 'object') ? item.kind : null;
            showTyping();
            setTimeout(function () {
                hideTyping();
                appendBot(szMsg, szKind);
                setTimeout(next, GAP_MS);
            }, typingDelay(szMsg));
        }
        next();
    }

    // ---------- 動作區（按鈕 / 控制項） ----------

    /** 建立（或清空重建）動作區，附在訊息流末端 */
    function newActions() {
        if (_actions) _actions.remove();
        _actions = document.createElement('div');
        _actions.className = 'sa-actions';
        _body.appendChild(_actions);
        scrollBottom();
        return _actions;
    }

    function clearActions() {
        if (_actions) { _actions.remove(); _actions = null; }
    }

    function makeOption(szLabel, szIcon, onClick) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'sa-opt';
        btn.innerHTML = '<i class="fas ' + szIcon + '"></i>';
        var span = document.createElement('span');
        span.textContent = szLabel;
        btn.appendChild(span);
        btn.addEventListener('click', function () {
            if (_busy) return;
            onClick();
        });
        return btn;
    }

    function makeDisabledOption(szLabel) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'sa-opt';
        btn.disabled = true;
        var span = document.createElement('span');
        span.textContent = szLabel;
        var badge = document.createElement('span');
        badge.className = 'sa-badge';
        badge.textContent = t('assistant.menu.coming_soon');
        btn.appendChild(span);
        btn.appendChild(badge);
        return btn;
    }

    function makeBackLink() {
        var a = document.createElement('button');
        a.type = 'button';
        a.className = 'sa-btn-ghost';
        a.textContent = t('assistant.btn_back_menu');
        a.addEventListener('click', function () {
            if (_busy) return;
            restart();
        });
        return a;
    }

    // ---------- 流程：主選單 ----------

    function startMenu() {
        clearActions();
        botSay([t('assistant.greeting')], function () {
            var box = newActions();
            box.appendChild(makeOption(
                t('assistant.menu.interval_efficiency'), 'fa-chart-line', onPickIntervalFlow));
            box.appendChild(makeDisabledOption(t('assistant.menu.placeholder_anomaly')));
            box.appendChild(makeDisabledOption(t('assistant.menu.placeholder_advice')));
        });
    }

    function restart() {
        _body.innerHTML = '';
        _actions = null;
        _typingEl = null;
        startMenu();
    }

    // ---------- 流程：區間效率分析 ----------

    function onPickIntervalFlow() {
        appendUser(t('assistant.menu.interval_efficiency'));
        clearActions();
        botSay([t('assistant.interval.intro')], loadReports);
    }

    function loadReports() {
        showTyping();
        fetch('/EnergyDeclaration/api/reports', { credentials: 'same-origin' })
            .then(function (r) {
                if (!r.ok) throw new Error('http ' + r.status);
                return r.json();
            })
            .then(function (reports) {
                hideTyping();
                if (!reports || !reports.length) {
                    botSay([t('assistant.interval.no_reports')], showRestartActions);
                    return;
                }
                botSay([t('assistant.interval.pick_report')], function () {
                    showReportOptions(reports);
                });
            })
            .catch(function () {
                hideTyping();
                botSay([t('assistant.error.generic')], showRestartActions);
            });
    }

    function showReportOptions(reports) {
        var box = newActions();
        reports.forEach(function (rep) {
            box.appendChild(makeOption(rep.name, 'fa-file-signature', function () {
                onPickReport(rep);
            }));
        });
        box.appendChild(makeBackLink());
    }

    function onPickReport(rep) {
        appendUser(rep.name);
        clearActions();
        botSay([t('assistant.interval.pick_range', { name: rep.name })], function () {
            showRangeCard(rep);
        });
    }

    function showRangeCard(rep) {
        var box = newActions();

        var card = document.createElement('div');
        card.className = 'sa-range-card';

        var dtEnd = new Date();
        var dtStart = new Date();
        dtStart.setDate(dtStart.getDate() - 7);   // 預設近 7 天

        var startInput = makeDateField(t('assistant.interval.label_start'), toYmd(dtStart));
        var endInput = makeDateField(t('assistant.interval.label_end'), toYmd(dtEnd));
        card.appendChild(startInput.field);
        card.appendChild(endInput.field);

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'sa-btn-primary';
        btn.textContent = t('assistant.interval.btn_analyze');
        btn.addEventListener('click', function () {
            if (_busy) return;
            onAnalyze(rep, startInput.input.value, endInput.input.value);
        });
        card.appendChild(btn);

        box.appendChild(card);
        box.appendChild(makeBackLink());
    }

    function makeDateField(szLabel, szValue) {
        var field = document.createElement('div');
        field.className = 'sa-range-field';
        var label = document.createElement('label');
        label.textContent = szLabel;
        var input = document.createElement('input');
        input.type = 'date';
        input.value = szValue;
        field.appendChild(label);
        field.appendChild(input);
        return { field: field, input: input };
    }

    function onAnalyze(rep, szStart, szEnd) {
        if (!szStart || !szEnd) return;
        if (szEnd < szStart) {                    // yyyy-MM-dd 字串可直接字典序比較
            botSay([t('assistant.error.date_range')]);
            return;
        }
        appendUser(szStart + ' ~ ' + szEnd);
        clearActions();
        botSay([t('assistant.interval.analyzing', { name: rep.name })], function () {
            showTyping();
            fetch('/EnergyDeclaration/api/interval-analysis', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({ reportId: rep.id, start: szStart, end: szEnd })
            })
                .then(function (r) {
                    if (!r.ok) throw new Error('http ' + r.status);
                    return r.json();
                })
                .then(function (data) {
                    hideTyping();
                    presentResult(data, szStart, szEnd);
                })
                .catch(function () {
                    hideTyping();
                    botSay([t('assistant.error.generic')], showRestartActions);
                });
        });
    }

    function presentResult(data, szStart, szEnd) {
        if (data && data.szErrorCode === 'circuit_deleted') {
            botSay([t('assistant.error.circuit_deleted')], showRestartActions);
            return;
        }

        var aLines = [];
        aLines.push(t('assistant.result.range', { start: szStart, end: szEnd }));
        aLines.push(t('assistant.result.total_kwh', { value: fmtNum(data.dTotalKwh, 1) }));
        aLines.push(t('assistant.result.total_rth', { value: fmtNum(data.dTotalRtHour, 1) }));
        if (data.dEfficiency == null) {
            aLines.push(t('assistant.result.efficiency_na'));
        } else {
            aLines.push(t('assistant.result.efficiency', { value: fmtNum(data.dEfficiency, 3) }));
        }
        // 覆蓋率低於門檻時給帶數字的提示（幾 % / 低於幾 % / 缺幾小時），
        // 泛泛的 stale_warning 只留給其他資料警告（例如電表累積值倒退）
        if (data.isLowCoverage) {
            aLines.push({ kind: 'warn', text: t('assistant.result.low_coverage', {
                percent: fmtNum(data.dCoveragePercent, 1),
                threshold: data.nCoverageThresholdPercent,
                leaf: data.szCoverageWorstLeafName,
                hours: data.nCoverageMissingHours
            }) });
        } else if (data.isStaleWarning) {
            aLines.push({ kind: 'warn', text: t('assistant.result.stale_warning') });
        }
        // 判定句依 verdict code 帶氣泡色條：good → 綠、poor / low_coverage → 琥珀，其餘無色條
        var szVerdict = data.szVerdictCode || 'insufficient';
        var szVerdictKind = szVerdict === 'good' ? 'good' : ((szVerdict === 'poor' || szVerdict === 'low_coverage') ? 'warn' : null);
        aLines.push({ kind: szVerdictKind, text: t('assistant.verdict.' + szVerdict, {
            threshold: data.nCoverageThresholdPercent
        }) });

        botSay(aLines, function () {
            botSay([t('assistant.restart_prompt')], showRestartActions);
        });
    }

    function showRestartActions() {
        var box = newActions();
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'sa-btn-primary';
        btn.textContent = t('assistant.btn_restart');
        btn.addEventListener('click', function () {
            if (_busy) return;
            restart();
        });
        box.appendChild(btn);
    }

    // ---------- 小工具 ----------

    function toYmd(d) {
        function p(n) { return String(n).padStart(2, '0'); }
        return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate());
    }

    // min = max = nDigits：位數固定才對得齊，且 0 會顯示成 0.0 而不是 0
    function fmtNum(n, nDigits) {
        if (n == null || isNaN(n)) return '--';
        return Number(n).toLocaleString(undefined, {
            minimumFractionDigits: nDigits,
            maximumFractionDigits: nDigits
        });
    }

    // ---------- 啟動 ----------

    function boot() {
        if (!document.getElementById('scadaAssistantRoot')) return;
        if (window.i18n && window.i18n.ready) {
            window.i18n.ready(build);
        } else {
            build();
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }

    window.ScadaAssistant = { open: open, close: close, restart: restart };
})();
