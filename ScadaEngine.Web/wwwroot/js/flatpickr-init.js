/* ── flatpickr 共用初始化（強制 24h、依 <html lang> 切換 zh-TW / en locale）──
 * 用法：
 *   window._fpInit.datetime('#dtStart');          // yyyy-MM-dd HH:mm
 *   window._fpInit.date('#dtDay');                // yyyy-MM-dd（可留白的純日期欄位用）
 *   window._fpInit.time(document.getElementById('txtStartTime'));  // HH:mm
 *
 * 載入順序：flatpickr.min.js → zh-tw.js → flatpickr-init.js → 你的 feature.js
 *
 * 寫值：use el._flatpickr.setDate(dateOrString, true) — 直接 el.value = 不會同步 picker 狀態
 * 讀值：el.value 照舊（flatpickr 把格式化字串寫回 input.value）
 */
(function () {
    'use strict';

    function applyLocaleOnce() {
        if (!window.flatpickr) return;
        var szLang = (document.documentElement.lang || '').toLowerCase();
        if (szLang.indexOf('zh') === 0 && window.flatpickr.l10ns && window.flatpickr.l10ns.zh_tw) {
            window.flatpickr.localize(window.flatpickr.l10ns.zh_tw);
        }
    }

    var COMMON = {
        time_24hr:       true,
        allowInput:      true,
        minuteIncrement: 1
    };

    function bindDateTime(target) {
        if (!window.flatpickr) return null;
        applyLocaleOnce();
        return window.flatpickr(target, Object.assign({}, COMMON, {
            enableTime: true,
            dateFormat: 'Y-m-d H:i'
        }));
    }

    // 純日期：原生 <input type="date"> 有值時沒問題，但允許空白的欄位在 zh-TW Chromium 會露出「yyyy/月/dd」佔位，改用這個
    function bindDate(target) {
        if (!window.flatpickr) return null;
        applyLocaleOnce();
        return window.flatpickr(target, Object.assign({}, COMMON, {
            dateFormat: 'Y-m-d'
        }));
    }

    function bindTime(target) {
        if (!window.flatpickr) return null;
        applyLocaleOnce();
        return window.flatpickr(target, Object.assign({}, COMMON, {
            enableTime: true,
            noCalendar: true,
            dateFormat: 'H:i'
        }));
    }

    window._fpInit = {
        datetime: bindDateTime,
        date:     bindDate,
        time:     bindTime
    };
})();
