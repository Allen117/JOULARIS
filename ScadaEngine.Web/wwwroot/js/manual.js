// 線上使用手冊閱讀頁：依內文 h2 / h3 產生右側「本頁目錄」，捲動時高亮目前段落
(function () {
    'use strict';

    var content = document.getElementById('manualContent');
    var box = document.getElementById('manualOnPage');
    var list = document.getElementById('manualOnPageList');
    if (!content || !box || !list) return;

    var headings = Array.prototype.filter.call(content.querySelectorAll('h2, h3'), function (h) { return !!h.id; });
    if (headings.length < 2) return; // 一兩段的短章不需要本頁目錄

    var links = headings.map(function (h) {
        var li = document.createElement('li');
        li.className = h.tagName === 'H3' ? 'lv3' : 'lv2';
        var a = document.createElement('a');
        a.href = '#' + encodeURIComponent(h.id);
        a.textContent = h.textContent;
        li.appendChild(a);
        list.appendChild(li);
        return a;
    });
    box.hidden = false;

    function highlight() {
        var nActive = 0;
        for (var i = 0; i < headings.length; i++) {
            if (headings[i].getBoundingClientRect().top <= 80) nActive = i;
            else break;
        }
        links.forEach(function (a, i) { a.classList.toggle('active', i === nActive); });
    }

    var isTicking = false;
    window.addEventListener('scroll', function () {
        if (isTicking) return;
        isTicking = true;
        window.requestAnimationFrame(function () { highlight(); isTicking = false; });
    }, { passive: true });
    highlight();
})();
