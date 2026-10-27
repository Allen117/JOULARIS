/* ── 日期 / 時間輸入滾輪調值（全站共用，由 _Layout 載入，各頁不必再引）──
 *
 * 涵蓋範圍（事件委派在 document，動態產生的輸入框也吃得到）：
 *   1. 原生 <input type="date">  → ±1 天；<input type="month"> → ±1 月
 *   2. flatpickr 文字框（window._fpInit 綁的）：datetime → ±1 天；time（noCalendar）→ ±minuteIncrement 分
 *   3. flatpickr 彈出面板：
 *        - 日曆格 / 月份列  → 換月（滾輪向下 = 下個月，同捲動時間軸直覺）
 *        - 年份數字框       → ±1 年
 *        - 時 / 分 / 秒數字框 → ±1（直接派發 flatpickr 自己的 increment 事件，分→時進位、24h 迴繞照它的規則）
 *
 * 方向慣例：數字框 / 輸入框「滾輪向上 = 往後（+）」，與欄位旁的 ▲▼ 箭頭一致（Chromium number input 慣例）；
 *           只有日曆格換月反過來（向下 = 往後），因為那是在捲一張時間軸。
 *
 * 安全閥：輸入框本身**必須已取得焦點**（先點一下再滾），游標只是掃過時照常捲頁面，避免捲到一半把日期改掉。
 *         flatpickr 面板只在開啟時存在，面板上滾動一律攔下。
 *         Ctrl + 滾輪（瀏覽器縮放）不攔。觸控板小幅 deltaY 會累積到門檻才動一格，不會一掃跳十幾天。
 *
 * 改值後派發 input + change（bubbles），各頁既有 addEventListener('change') 照常觸發；
 * flatpickr 走 setDate(d, true) 觸發 onChange；尊重 min / max（超界不動）。
 */
(function () {
    'use strict';

    var PIXEL_THRESHOLD = 40;   // deltaMode 0（像素）累積門檻；滑鼠一格通常 100，觸控板一次 2～10

    var _acc = 0;
    var _accTarget = null;
    var _accAt = 0;

    function pad(n) { return (n < 10 ? '0' : '') + n; }
    function fmtDate(d)  { return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }
    function fmtMonth(d) { return d.getFullYear() + '-' + pad(d.getMonth() + 1); }

    function parseDate(s) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || '');
        return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null;
    }
    function parseMonth(s) {
        var m = /^(\d{4})-(\d{2})$/.exec(s || '');
        return m ? new Date(+m[1], +m[2] - 1, 1) : null;
    }

    // ISO 格式字串可直接字典序比較 min / max
    function withinBounds(el, s) {
        if (el.min && s < el.min) return false;
        if (el.max && s > el.max) return false;
        return true;
    }

    function fire(el) {
        el.dispatchEvent(new Event('input',  { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
    }

    /* ── 1. 原生 date / month ── */
    function stepNative(el, delta) {
        var isMonth = el.type === 'month';
        var d = isMonth ? parseMonth(el.value) : parseDate(el.value);
        if (!d) d = isMonth ? new Date(new Date().getFullYear(), new Date().getMonth(), 1) : new Date();  // 空白 → 從本月 / 今天起算
        if (isMonth) d.setMonth(d.getMonth() + delta); else d.setDate(d.getDate() + delta);
        var s = isMonth ? fmtMonth(d) : fmtDate(d);
        if (!withinBounds(el, s)) return;
        el.value = s;
        fire(el);
    }

    /* ── 2. flatpickr 文字框 ── */
    function stepFpInput(fp, delta) {
        var base = fp.selectedDates && fp.selectedDates[0] ? new Date(fp.selectedDates[0].getTime()) : new Date();
        if (fp.config.noCalendar) base.setMinutes(base.getMinutes() + delta * (fp.config.minuteIncrement || 1));
        else                       base.setDate(base.getDate() + delta);
        fp.setDate(base, true);
    }

    /* ── 3. flatpickr 面板 ── */
    function fpOfCalendar(cal) {
        var inputs = document.querySelectorAll('.flatpickr-input');
        for (var i = 0; i < inputs.length; i++) {
            var fp = inputs[i]._flatpickr;
            if (fp && fp.calendarContainer === cal) return fp;
        }
        return null;
    }

    // flatpickr 內部 ▲▼ 箭頭就是對 numInput 派這個事件：監聽端讀 e.delta 做加減、進位、迴繞與 onChange
    function fpIncrement(numInput, delta) {
        var ev = new Event('increment', { bubbles: true, cancelable: true });
        ev.delta = delta;
        numInput.dispatchEvent(ev);
    }

    function stepCalendar(cal, t, delta) {
        var fp = fpOfCalendar(cal);
        if (!fp) return;
        var wrap = t.closest('.numInputWrapper');
        var num = wrap && wrap.querySelector('input.numInput');
        if (num) { fpIncrement(num, delta); return; }           // 年 / 時 / 分 / 秒
        if (t.closest('.flatpickr-time')) return;               // 時間列的冒號、AM/PM 等非數字處不動
        fp.changeMonth(-delta);                                 // 日曆格 / 月份列：向下 = 下個月
    }

    document.addEventListener('wheel', function (e) {
        if (e.ctrlKey || !e.deltaY) return;
        var t = e.target;
        if (!(t instanceof Element)) return;

        var cal = t.closest('.flatpickr-calendar');
        var isInput = !cal && t.tagName === 'INPUT' && document.activeElement === t && !t.disabled && !t.readOnly;
        var fp = isInput ? t._flatpickr : null;
        var isNative = isInput && (t.type === 'date' || t.type === 'month');
        if (!cal && !fp && !isNative) return;

        e.preventDefault();

        // 觸控板累積：同一目標、300ms 內連續事件累加，過門檻才動一格
        var now = Date.now();
        if (t !== _accTarget || now - _accAt > 300) _acc = 0;
        _accTarget = t; _accAt = now;
        _acc += e.deltaY;
        var threshold = e.deltaMode === 0 ? PIXEL_THRESHOLD : 1;
        if (Math.abs(_acc) < threshold) return;
        var delta = _acc < 0 ? 1 : -1;   // 向上 = +
        _acc = 0;

        if (cal)           stepCalendar(cal, t, delta);
        else if (fp)       stepFpInput(fp, delta);
        else               stepNative(t, delta);
    }, { passive: false });
})();
