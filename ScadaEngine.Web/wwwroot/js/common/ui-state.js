// 共用 UI 狀態小 helper：警告文字 / 表格 stale 標記 / 年增減百分比
// 原則（docs/設計規範.md §圖示慣例「符號屬於元件、不屬於文案」）：
//   resx 字串一律純文字，警告三角 / 漲跌三角等圖示由這裡的元件提供一次，不寫進文案。
// 載入順序：_Layout 於 i18n.js 之後、各頁 JS 之前載入；掛 window.UiState。
(function () {
    'use strict';

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    var WARN_ICON_CLS = 'fas fa-exclamation-triangle';

    /** 警告圖示 + 已跳脫文字（供 template string / innerHTML 用）；空文字回空字串 */
    function warnHtml(text) {
        if (!text) return '';
        return '<i class="' + WARN_ICON_CLS + ' me-1" aria-hidden="true"></i>' + esc(text);
    }

    /**
     * DOM 版：把 el 內容換成「警告圖示 + 文字節點」；text 為空時清空並隱藏 el。
     * text 走 textNode 寫入，不經 innerHTML，無注入面。el 可傳元素或 id。
     */
    function setWarnText(el, text) {
        if (typeof el === 'string') el = document.getElementById(el);
        if (!el) return;
        while (el.firstChild) el.removeChild(el.firstChild);
        if (!text) { el.hidden = true; return; }
        var icon = document.createElement('i');
        icon.className = WARN_ICON_CLS + ' me-1';
        icon.setAttribute('aria-hidden', 'true');
        el.appendChild(icon);
        el.appendChild(document.createTextNode(text));
        el.hidden = false;
    }

    /** 表格單格的小型 stale 標記（琥珀三角 + hover tooltip）；前置一個空白與前文隔開 */
    function staleMarkHtml(cls, tip) {
        return ' <span class="' + esc(cls) + '" title="' + esc(tip) + '">'
            + '<i class="' + WARN_ICON_CLS + '" aria-hidden="true"></i></span>';
    }

    /** 表格單格的「部分區間」標記（藍色半圓 + hover tooltip）；與 stale 互斥，由呼叫端決定 */
    function partialMarkHtml(cls, tip) {
        return ' <span class="' + esc(cls) + '" title="' + esc(tip) + '">'
            + '<i class="fas fa-adjust" aria-hidden="true"></i></span>';
    }

    /** 年增減百分比：正 → upCls + caret-up，負 → downCls + caret-down，0 → 灰 0.0%，null → -- */
    function pctHtml(p, upCls, downCls) {
        if (p == null || isNaN(p)) return '<span class="text-muted">--</span>';
        if (p > 0) return '<span class="' + esc(upCls) + '"><i class="fas fa-caret-up me-1" aria-hidden="true"></i>+' + p.toFixed(1) + '%</span>';
        if (p < 0) return '<span class="' + esc(downCls) + '"><i class="fas fa-caret-down me-1" aria-hidden="true"></i>' + p.toFixed(1) + '%</span>';
        return '<span class="text-muted">0.0%</span>';
    }

    window.UiState = {
        warnHtml: warnHtml,
        setWarnText: setWarnText,
        staleMarkHtml: staleMarkHtml,
        partialMarkHtml: partialMarkHtml,
        pctHtml: pctHtml
    };
})();
