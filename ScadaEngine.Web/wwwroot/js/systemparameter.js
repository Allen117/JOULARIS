// 系統參數頁（工程師專屬）— 讀寫 SystemSettings.system_parameters。
// 資料模型見 Features/SystemParameter/Models/SystemParameterModels.cs；欄位以 input[data-key] 對應 JSON 屬性。
// 整份載入整份儲存；存檔後下一個請求即生效（不需重啟）。
(function () {
    'use strict';

    var g_defaults = null;

    document.addEventListener('DOMContentLoaded', function () {
        if (window.i18n) window.i18n.ready(load);
        else load();
    });

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    function inputs() {
        return Array.prototype.slice.call(document.querySelectorAll('input[data-key]'));
    }

    function fill(config) {
        inputs().forEach(function (el) {
            var v = config[el.dataset.key];
            el.value = v != null ? v : '';
        });
    }

    async function load() {
        try {
            var res = await fetch('/SystemParameter/api/config');
            if (!res.ok) throw new Error(res.statusText);
            var json = await res.json();
            g_defaults = json.defaults;
            fill(json.config);
            showUpdatedAt(json.dtUpdatedAt);
        } catch (err) {
            console.error('system parameter load failed', err);
            alert(t('sysparam.msg.load_fail'));
        }
    }

    function showUpdatedAt(szDt) {
        document.getElementById('spUpdatedAt').textContent = szDt
            ? t('sysparam.updated_at', { 0: szDt.replace('T', ' ').substring(0, 19) })
            : t('sysparam.updated_never');
    }

    async function save() {
        var config = {};
        for (var i = 0; i < inputs().length; i++) {
            var el = inputs()[i];
            var v = Number(el.value);
            if (el.value === '' || !isFinite(v) || !el.checkValidity()) {
                el.focus();
                alert(t('sysparam.msg.invalid', { 0: el.closest('.sp-row').querySelector('.sp-label').textContent }));
                return;
            }
            config[el.dataset.key] = v;
        }

        var btn = document.getElementById('btnSpSave');
        btn.disabled = true;
        try {
            var res = await fetch('/SystemParameter/api/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(config)
            });
            var json = await res.json().catch(function () { return {}; });
            if (!res.ok) {
                alert(t(json.code || 'sysparam.msg.save_fail'));
                return;
            }
            alert(t('sysparam.msg.saved'));
            load();
        } catch (e) {
            alert(t('sysparam.msg.save_fail'));
        } finally {
            btn.disabled = false;
        }
    }

    // 只填回預設值到畫面，按「儲存」才寫入
    function resetDefaults() {
        if (g_defaults) fill(g_defaults);
    }

    window._sp = {
        save: save,
        resetDefaults: resetDefaults
    };
})();
