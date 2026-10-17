(function () {
    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var POLL_INTERVAL_MS = 3000;
    var API_URL = '/Realtime/ActiveAlarms';
    var ACK_URL = '/Realtime/AcknowledgeAlarm';

    var $panel = null;
    var $body = null;
    var $countBadge = null;
    var $list = null;
    var $connWarn = null;
    var $ackFilter = null;
    var pollTimer = null;
    var isCollapsed = false;
    var lastAlarms = [];
    var lastTotalCount = -1;
    var ackFilter = 'all';
    var elapsedTimer = null;

    function init() {
        $panel = document.getElementById('activeAlarmPanel');
        if (!$panel) return;

        $body = $panel.querySelector('.alarm-panel-body');
        $countBadge = $panel.querySelector('.alarm-count-badge');
        $list = $panel.querySelector('.alarm-list');
        $connWarn = $panel.querySelector('.alarm-conn-warn');
        $ackFilter = document.getElementById('alarmAckFilter');

        var $toggle = $panel.querySelector('.alarm-toggle-trigger');
        if ($toggle) {
            $toggle.addEventListener('click', function () {
                isCollapsed = !isCollapsed;
                $panel.classList.toggle('collapsed', isCollapsed);
            });
        }

        if ($ackFilter) {
            $ackFilter.addEventListener('change', function () {
                ackFilter = $ackFilter.value || 'all';
                render(lastAlarms);
            });
        }

        if ($list) {
            $list.addEventListener('dblclick', onItemDblClick);
        }

        fetchAlarms();
        pollTimer = setInterval(fetchAlarms, POLL_INTERVAL_MS);
        elapsedTimer = setInterval(updateElapsedLabels, 30000);
    }

    function fetchAlarms() {
        fetch(API_URL, { credentials: 'same-origin' })
            .then(function (resp) { return resp.json(); })
            .then(function (json) {
                if (!json || !json.success) {
                    showConnWarn(t('alarm.panel.fetch_failed'));
                    return;
                }
                hideConnWarn();
                if (!json.isConnected) {
                    showConnWarn(t('alarm.panel.mqtt_disconnected'));
                }
                lastAlarms = json.data || [];
                render(lastAlarms);
                // 廣播給 ScadaPage 著色（有前置條件的規則改看實際 active 警報）
                try { window.dispatchEvent(new CustomEvent('scada:active-alarms', { detail: lastAlarms })); } catch (_) { /* 舊瀏覽器 */ }
            })
            .catch(function () {
                showConnWarn(t('alarm.panel.no_connection'));
            });
    }

    function render(alarms) {
        // 總數（未篩選）控制 badge 與自動折疊
        var totalCount = alarms.length;
        if ($countBadge) {
            $countBadge.textContent = totalCount;
            $countBadge.classList.toggle('zero', totalCount === 0);
        }

        // 自動折疊：首次載入依現況決定；之後僅在警報數「跨越 0」邊界時切換，其餘時候尊重使用者手動操作
        var isFirstRender = lastTotalCount === -1;
        if (isFirstRender) {
            if (totalCount === 0 && !isCollapsed) {
                isCollapsed = true;
                $panel.classList.add('collapsed');
            }
        } else {
            if (lastTotalCount === 0 && totalCount > 0 && isCollapsed) {
                isCollapsed = false;
                $panel.classList.remove('collapsed');
            } else if (lastTotalCount > 0 && totalCount === 0 && !isCollapsed) {
                isCollapsed = true;
                $panel.classList.add('collapsed');
            }
        }
        lastTotalCount = totalCount;

        // 套用篩選
        var filtered = alarms;
        if (ackFilter === 'unacked') {
            filtered = alarms.filter(function (x) { return !x.isAcknowledged; });
        } else if (ackFilter === 'acked') {
            filtered = alarms.filter(function (x) { return !!x.isAcknowledged; });
        }

        if (!$list) return;
        if (filtered.length === 0) {
            var emptyMsg = totalCount === 0
                ? t('alarm.panel.empty.no_active')
                : t('alarm.panel.empty.no_match');
            $list.innerHTML = '<li class="alarm-empty"><i class="fas fa-check-circle text-success"></i>' + emptyMsg + '</li>';
            return;
        }

        var html = '';
        for (var i = 0; i < filtered.length; i++) {
            html += renderItem(filtered[i]);
        }
        $list.innerHTML = html;
    }

    function renderItem(a) {
        var elapsed = elapsedText(a.occurredAtMs);
        var ackBadge = a.isAcknowledged
            ? '<span class="alarm-ack-badge ack">' + t('alarm.panel.ack.acked') + '</span>'
            : '<span class="alarm-ack-badge unack">' + t('alarm.panel.ack.unack') + '</span>';
        var ackBy = a.acknowledgedBy ? escapeHtml(a.acknowledgedBy) : '<span class="alarm-ack-by-empty">—</span>';
        var titleAttr = a.isAcknowledged
            ? t('alarm.panel.ack.title.acked', { 0: a.acknowledgedBy || '' })
            : t('alarm.panel.ack.title.dblclick');
        return '<li class="alarm-item' + (a.isAcknowledged ? ' is-acked' : '') + '"'
            + ' data-sid="' + escapeAttr(a.sid) + '"'
            + ' data-type="' + escapeAttr(a.type) + '"'
            + ' title="' + titleAttr + '">'
            + '<span class="alarm-occurred-at">' + escapeHtml(a.occurredAt) + '</span>'
            + '<span class="alarm-severity-bar" style="background:' + escapeAttr(a.severityColor) + '"></span>'
            + '<span class="alarm-severity-label" style="background:' + escapeAttr(a.severityColor) + '">'
            + escapeHtml(a.severityLabel) + '</span>'
            + '<span class="alarm-message" title="' + escapeAttr(a.message) + '">' + escapeHtml(a.message) + '</span>'
            + '<span class="alarm-elapsed" data-occurred-ms="' + a.occurredAtMs + '">' + escapeHtml(elapsed) + '</span>'
            + '<span class="alarm-ack-status">' + ackBadge + '</span>'
            + '<span class="alarm-ack-by">' + ackBy + '</span>'
            + '</li>';
    }

    function onItemDblClick(ev) {
        var $li = ev.target.closest && ev.target.closest('.alarm-item');
        if (!$li) return;
        if ($li.classList.contains('is-acked')) return;
        if ($li.dataset.busy === '1') return;

        var sid = $li.getAttribute('data-sid');
        var type = $li.getAttribute('data-type');
        if (!sid || !type) return;

        $li.dataset.busy = '1';
        $li.style.opacity = '0.5';

        fetch(ACK_URL, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sid: sid, type: type })
        })
            .then(function (resp) {
                return resp.json().then(function (json) {
                    return { ok: resp.ok, json: json };
                });
            })
            .then(function (r) {
                if (!r.ok || !r.json || !r.json.success) {
                    alert((r.json && r.json.error) || t('alarm.panel.ack.failed'));
                    $li.style.opacity = '';
                    delete $li.dataset.busy;
                    return;
                }
                // 同步本地資料 → 直接 re-render（如使用者選了「僅未確認」，該列會被過濾掉）
                var ackBy = r.json.acknowledgedBy || '';
                for (var i = 0; i < lastAlarms.length; i++) {
                    var a = lastAlarms[i];
                    if (a.sid === sid && a.type === type) {
                        a.isAcknowledged = true;
                        a.acknowledgedBy = ackBy;
                        break;
                    }
                }
                render(lastAlarms);
            })
            .catch(function () {
                alert(t('alarm.panel.ack.failed_with'));
                $li.style.opacity = '';
                delete $li.dataset.busy;
            });
    }

    function updateElapsedLabels() {
        if (!$list) return;
        var nodes = $list.querySelectorAll('.alarm-elapsed[data-occurred-ms]');
        for (var i = 0; i < nodes.length; i++) {
            var ms = parseInt(nodes[i].getAttribute('data-occurred-ms'), 10);
            if (!isNaN(ms)) {
                nodes[i].textContent = elapsedText(ms);
            }
        }
    }

    function elapsedText(occurredMs) {
        var diff = Date.now() - occurredMs;
        if (diff < 0) diff = 0;
        var sec = Math.floor(diff / 1000);
        if (sec < 60) return t('alarm.panel.elapsed.sec', { 0: sec });
        var min = Math.floor(sec / 60);
        if (min < 60) return t('alarm.panel.elapsed.min', { 0: min });
        var hour = Math.floor(min / 60);
        if (hour < 24) return t('alarm.panel.elapsed.hour', { 0: hour });
        var day = Math.floor(hour / 24);
        return t('alarm.panel.elapsed.day', { 0: day });
    }

    function showConnWarn(msg) {
        if (!$connWarn) return;
        UiState.setWarnText($connWarn, msg);
        $connWarn.style.display = 'block';
    }

    function hideConnWarn() {
        if (!$connWarn) return;
        $connWarn.style.display = 'none';
    }

    function escapeHtml(s) {
        if (s === null || s === undefined) return '';
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function escapeAttr(s) {
        return escapeHtml(s);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    window._activeAlarmPanel = {
        refresh: fetchAlarms
    };
})();
