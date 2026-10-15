/* EMS Hub — 冰機效率 / 空壓機效率兩張卡（_CardChillerEfficiency / _CardCompressorEfficiency）。
   每張卡根元素 [data-eq-card][data-kind]；卡片可在 /EmsCardSetting 個別關閉（DOM 不渲染）→ 以根元素存在與否防呆。
   資料源 GET /EMS/api/equipment/card?kind=，30 秒輪詢：每台一列（運轉燈 / 名稱 / 即時主指標 + 評級）+ 底部本曆月累計。 */
(function () {
    'use strict';

    var REFRESH_MS = 30000;
    var CONFIG = {
        chiller: { unit: 'kW/RT', digits: 3, settingUrl: '/EquipmentEfficiencySetting?kind=chiller' },
        compressor: { unit: 'kW/(m³/min)', digits: 2, settingUrl: '/EquipmentEfficiencySetting?kind=compressor' }
    };

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function fmt(v, d) {
        if (v == null || isNaN(v)) return '--';
        return Number(v).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
    }

    function lamp(isRunning) {
        var cls = isRunning === true ? 'is-on' : isRunning === false ? 'is-off' : 'is-unknown';
        return '<span class="eq-run-lamp ' + cls + '"></span>';
    }

    function render(root, kind, d) {
        var cfg = CONFIG[kind];
        var body = root.querySelector('[data-eq-body]');
        var foot = root.querySelector('[data-eq-foot]');
        root.querySelector('[data-eq-unit]').textContent = cfg.unit;
        if (!d || d.deviceCount === 0) {
            body.innerHTML = '<div class="text-center text-muted py-4">' + esc(t('eq.card.not_configured')) +
                '<div class="mt-2"><a class="btn btn-sm btn-outline-primary" href="' + cfg.settingUrl + '">' +
                '<i class="fas fa-sliders-h me-1"></i>' + esc(t('eq.card.goto_setting')) + '</a></div></div>';
            foot.textContent = '';
            return;
        }
        body.innerHTML = '<div class="eq-card-list">' + (d.items || []).map(function (it) {
            return '<div class="eq-card-row">' + lamp(it.isRunning) +
                '<span class="eq-card-name" title="' + esc(it.name) + '">' + esc(it.name) + '</span>' +
                '<span class="eq-card-value">' + fmt(it.primary, cfg.digits) + '</span>' +
                '<span class="badge eq-grade-badge eq-grade-' + esc(it.grade) + '">' + esc(t('eq.grade.' + it.grade)) + '</span>' +
            '</div>';
        }).join('') + '</div>';
        var m = d.month || {};
        foot.innerHTML = esc(t('eq.card.month_summary', {
            month: d.monthLabel,
            value: fmt(m.primary, cfg.digits),
            unit: cfg.unit,
            hours: fmt(m.runHours, 1)
        }));
    }

    function load(root) {
        var kind = root.dataset.kind === 'compressor' ? 'compressor' : 'chiller';
        fetch('/EMS/api/equipment/card?kind=' + kind, { credentials: 'same-origin' })
            .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
            .then(function (d) { render(root, kind, d); })
            .catch(function () {
                root.querySelector('[data-eq-body]').innerHTML =
                    '<div class="text-center text-muted py-4">' + esc(t('eq.card.load_error')) + '</div>';
            });
    }

    function init() {
        var roots = document.querySelectorAll('[data-eq-card]');
        if (roots.length === 0) return;
        roots.forEach(load);
        setInterval(function () { roots.forEach(load); }, REFRESH_MS);
    }

    if (window.i18n && window.i18n.ready) window.i18n.ready(init);
    else document.addEventListener('DOMContentLoaded', init);
})();
