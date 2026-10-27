// ============================================================
// common/point-picker.js — 共用點位選擇器（window.PointPicker）
// 流程比照 Designer 綁定點位（js/designer/picker.js）：
//   來源（設備點位 / 計算點位 / DB 來源 / OPC UA）→ 設備（多站號展開、單站號 Device 分群）/ 群組 → 點位
//   每層可搜尋，點選後按「確認」（或雙擊點位）回傳。
// Designer 的 picker 與其全域狀態綁死（widget / cell 綁定），無法直接搬用，故另做此獨立元件；
// 字串沿用 designer.picker.* key（resx 全站合併為同一份 i18n 字典）。
// Modal DOM 由本檔建立；可疊在另一個 Bootstrap modal 之上（警報規則彈窗內開啟）。
//
// 用法：
//   PointPicker.open({ excludeSid: 'X', filter: function (sid) { return true; } })
//     .then(function (p) { if (p) ... p.sid / p.name / p.unit / p.label });
//   PointPicker.load() → Promise<{ devices, points }>（點位清單，可供他處做 SID → 名稱反查）
// ============================================================
(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }
    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    var PG = function () { return window.PointGrouping; };

    var SRC_DEVICE = 'device', SRC_CALC = 'calc', SRC_DB = 'db', SRC_OPC = 'opc';

    var _loadPromise = null;
    var _devices = [];
    var _points = [];          // [{ szSid, szName, szUnit, szGroupName, szDeviceGroup, szDeviceLabel, isCoordLabel }]

    var _modal = null, _el = null;
    var _opts = {};
    var _resolve = null;
    var _picked = null;        // 選定點位物件
    // 目前位置：source + (devId, modbusId, deviceGroup) 或 group
    var _nav = {};

    // ── 資料 ──
    function load() {
        if (_loadPromise) return _loadPromise;
        _loadPromise = Promise.all([fetch('/Designer/Devices'), fetch('/Designer/Points')])
            .then(function (rs) {
                if (!rs[0].ok || !rs[1].ok) throw new Error('HTTP ' + rs[0].status + '/' + rs[1].status);
                return Promise.all([rs[0].json(), rs[1].json()]);
            })
            .then(function (arr) {
                _devices = arr[0] || [];
                _points = arr[1] || [];
                _points.forEach(function (p) {
                    p.szDeviceLabel = deviceLabelOf(p);
                    p.isCoordLabel = isCoordLabelOf(p);
                });
                return { devices: _devices, points: _points };
            })
            .catch(function (err) { _loadPromise = null; throw err; });
        return _loadPromise;
    }

    function sourceOf(sid) {
        if (PG().isCalcSid(sid)) return SRC_CALC;
        if (PG().isDbSid(sid)) return SRC_DB;
        if (PG().isOpcSid(sid)) return SRC_OPC;
        if (PG().getSidPrefix(sid) >= 0) return SRC_DEVICE;
        return '';   // 需量 / 迴路等虛擬點位：不提供
    }

    function deviceLabelOf(p) {
        var src = sourceOf(p.szSid);
        if (src === SRC_CALC) return p.szGroupName || t('designer.picker.source.calc');
        if (src === SRC_DB) return p.szGroupName || t('designer.picker.db_source_default');
        if (src === SRC_OPC) return p.szGroupName || 'OPC UA';
        for (var i = 0; i < _devices.length; i++) {
            if (PG().coordContainsSid(p.szSid, _devices[i])) return PG().pointDeviceLabel(p.szSid, p.szDeviceGroup, _devices[i]);
        }
        return '';
    }

    // Modbus 點位標籤是否只是 level 3 的 Coordinator 名（未定義 Tag.Device、亦非多站號具名子站）。
    // 設備效率掃描據此只認通訊端明確定義的子設備（equipment-scan.js）。非 Modbus 點位回 undefined
    function isCoordLabelOf(p) {
        if (sourceOf(p.szSid) !== SRC_DEVICE) return undefined;
        if (PG().getDeviceGroup(p)) return false;
        for (var i = 0; i < _devices.length; i++) {
            if (!PG().coordContainsSid(p.szSid, _devices[i])) continue;
            var sub = PG().subOfSid(p.szSid, _devices[i]);
            return !(sub && sub.subName);
        }
        return undefined;
    }

    function candidates() {
        return _points.filter(function (p) {
            if (!p.szSid || !sourceOf(p.szSid)) return false;
            if (_opts.excludeSid && p.szSid === _opts.excludeSid) return false;
            if (_opts.filter && !_opts.filter(p.szSid)) return false;
            return true;
        });
    }

    // ── Modal DOM ──
    function ensureDom() {
        if (_el) return;
        _el = document.createElement('div');
        _el.className = 'modal fade pp-modal';
        _el.tabIndex = -1;
        _el.setAttribute('data-bs-backdrop', 'static');
        _el.innerHTML =
            '<div class="modal-dialog modal-dialog-centered modal-dialog-scrollable" style="max-width:480px;">' +
              '<div class="modal-content">' +
                '<div class="modal-header">' +
                  '<h6 class="modal-title"><i class="fas fa-tachometer-alt text-warning me-1"></i><span class="pp-title"></span></h6>' +
                  '<button type="button" class="btn-close btn-sm pp-cancel" aria-label="Close"></button>' +
                '</div>' +
                '<div class="modal-body">' +
                  '<div class="pp-nav" style="display:none;">' +
                    '<button type="button" class="pp-back"><i class="fas fa-arrow-left"></i> ' + esc(t('designer.picker.back')) + '</button>' +
                    '<span class="pp-crumb"></span>' +
                  '</div>' +
                  '<input type="text" class="form-control form-control-sm mb-2 pp-search" style="display:none;" />' +
                  '<div class="pp-list"></div>' +
                '</div>' +
                '<div class="modal-footer">' +
                  '<button type="button" class="btn btn-secondary btn-sm pp-cancel">' + esc(t('designer.picker.cancel')) + '</button>' +
                  '<button type="button" class="btn btn-primary btn-sm pp-confirm" disabled>' + esc(t('designer.picker.confirm')) + '</button>' +
                '</div>' +
              '</div>' +
            '</div>';
        document.body.appendChild(_el);

        _el.querySelectorAll('.pp-cancel').forEach(function (b) { b.addEventListener('click', function () { finish(null); }); });
        _el.querySelector('.pp-confirm').addEventListener('click', function () { if (_picked) finish(_picked); });
        _el.querySelector('.pp-back').addEventListener('click', goBack);
        _el.querySelector('.pp-search').addEventListener('input', function () { renderCurrent(); });
        _el.querySelector('.pp-list').addEventListener('click', onListClick);
        _el.querySelector('.pp-list').addEventListener('dblclick', function (ev) {
            var it = ev.target.closest('.pp-item[data-sid]');
            if (it) { selectPoint(it); finish(_picked); }
        });

        // 疊在另一個 modal 上：提高 z-index、backdrop 跟著墊高；關閉後若底下還有 modal，補回 body.modal-open（否則底層無法捲動）
        _el.addEventListener('show.bs.modal', function () { _el.style.zIndex = 1075; });
        _el.addEventListener('shown.bs.modal', function () {
            var bds = document.querySelectorAll('.modal-backdrop');
            if (bds.length) bds[bds.length - 1].style.zIndex = 1070;
        });
        _el.addEventListener('hidden.bs.modal', function () {
            if (document.querySelector('.modal.show')) document.body.classList.add('modal-open');
            if (_resolve) { var r = _resolve; _resolve = null; r(null); }
        });
        _modal = new bootstrap.Modal(_el);
    }

    function finish(result) {
        var r = _resolve;
        _resolve = null;
        _modal.hide();
        if (r) r(result ? {
            sid: result.szSid,
            name: result.szName || result.szSid,
            unit: result.szUnit || '',
            label: (result.szDeviceLabel ? result.szDeviceLabel + ' / ' : '') + (result.szName || result.szSid)
        } : null);
    }

    // ── 開啟 ──
    function open(opts) {
        _opts = opts || {};
        ensureDom();
        if (_resolve) { _resolve(null); _resolve = null; }
        return load().then(function () {
            return new Promise(function (resolve) {
                _resolve = resolve;
                showSources();
                _modal.show();
            });
        }, function (err) {
            alert(t('designer.picker.load_error') + '\n' + err.message);
            return null;
        });
    }

    // ── 步驟 ──
    function setStep(szTitle, szCrumb, isSearch, szPlaceholder) {
        _el.querySelector('.pp-title').textContent = szTitle;
        _el.querySelector('.pp-nav').style.display = szCrumb != null ? '' : 'none';
        _el.querySelector('.pp-crumb').textContent = szCrumb || '';
        var s = _el.querySelector('.pp-search');
        s.style.display = isSearch ? '' : 'none';
        s.value = '';
        s.placeholder = szPlaceholder || '';
        _picked = null;
        _el.querySelector('.pp-confirm').disabled = true;
    }

    function showSources() {
        _nav = { step: 0 };
        setStep(t('designer.picker.title.source'), null, false);
        renderCurrent();
    }

    function showGroupStep(src) {
        _nav = { step: 1, src: src };
        var szTitle = src === SRC_DEVICE ? t('designer.picker.title.device')
                    : src === SRC_CALC ? t('designer.picker.title.calc_group')
                    : src === SRC_DB ? t('designer.picker.title.db_source')
                    : t('pp.title.opc_source');
        setStep(szTitle, sourceLabel(src), src === SRC_DEVICE, t('designer.picker.device_search_placeholder'));
        renderCurrent();
    }

    function showPointStep(nav, szCrumb) {
        _nav = nav;
        _nav.step = 2;
        setStep(t('designer.picker.title.point'), szCrumb, true, t('designer.picker.search_placeholder'));
        renderCurrent();
    }

    function goBack() {
        if (_nav.step === 2) showGroupStep(_nav.src);
        else showSources();
    }

    function sourceLabel(src) {
        return src === SRC_DEVICE ? t('designer.picker.source.device')
             : src === SRC_CALC ? t('designer.picker.source.calc')
             : src === SRC_DB ? t('designer.picker.source.db')
             : t('pp.source.opc');
    }

    function renderCurrent() {
        var list = _el.querySelector('.pp-list');
        var szQ = _el.querySelector('.pp-search').value.trim().toLowerCase();
        if (_nav.step === 0) list.innerHTML = renderSources();
        else if (_nav.step === 1) list.innerHTML = _nav.src === SRC_DEVICE ? renderDevices(szQ) : renderGroups(_nav.src);
        else list.innerHTML = renderPoints(szQ);
    }

    // szTrail：'right'（可進下一層）/ 'down'（可展開）/ ''（無）
    function item(attrs, szIcon, szColor, szName, szSub, szTrail, szExtraCls) {
        return '<div class="pp-item' + (szExtraCls ? ' ' + szExtraCls : '') + '" ' + attrs + '>' +
            '<i class="fas ' + szIcon + ' pp-item-icon" style="color:' + szColor + ';"></i>' +
            '<div class="pp-item-text"><div class="pp-item-name">' + szName + '</div>' +
            (szSub ? '<div class="pp-item-sub">' + esc(szSub) + '</div>' : '') + '</div>' +
            (szTrail === 'right' ? '<i class="fas fa-chevron-right pp-item-chev"></i>'
             : szTrail === 'down' ? '<i class="fas fa-chevron-down pp-item-chev pp-toggle-icon"></i>' : '') +
            '</div>';
    }
    function empty(szIcon, szMsg) {
        return '<div class="pp-empty"><i class="fas ' + szIcon + '"></i>' + esc(szMsg) + '</div>';
    }
    function countLabel(n) { return t('designer.picker.points_count', { count: n }); }

    function renderSources() {
        var c = candidates();
        var srcs = [
            { src: SRC_DEVICE, icon: 'fa-server', color: '#0d6efd', desc: t('designer.picker.source.device_desc') },
            { src: SRC_CALC, icon: 'fa-calculator', color: '#d39e00', desc: t('designer.picker.source.calc_desc') },
            { src: SRC_DB, icon: 'fa-database', color: '#198754', desc: t('designer.picker.source.db_desc') },
            { src: SRC_OPC, icon: 'fa-network-wired', color: '#6f42c1', desc: t('pp.source.opc_desc') }
        ].filter(function (s) { return c.some(function (p) { return sourceOf(p.szSid) === s.src; }); });
        if (!srcs.length) return empty('fa-inbox', t('designer.picker.no_matching_points'));
        return srcs.map(function (s) {
            return item('data-src="' + s.src + '"', s.icon, s.color, esc(sourceLabel(s.src)), s.desc, 'right', 'pp-item-lg');
        }).join('');
    }

    function deviceMatches(d, szQ) {
        if ((d.szName || '').toLowerCase().indexOf(szQ) >= 0) return true;
        if (PG().parseCoord(d).deviceNames.some(function (n) { return (n || '').toLowerCase().indexOf(szQ) >= 0; })) return true;
        var dg = PG().coordDeviceGroups(d, _points);
        return !!(dg && dg.names.some(function (n) { return n.toLowerCase().indexOf(szQ) >= 0; }));
    }

    function renderDevices(szQ) {
        var c = candidates();
        var devs = _devices.filter(function (d) {
            return c.some(function (p) { return PG().coordContainsSid(p.szSid, d); }) && (!szQ || deviceMatches(d, szQ));
        });
        if (!devs.length) return empty('fa-plug', t(szQ ? 'designer.picker.no_matching_points' : 'designer.picker.no_devices'));
        return devs.map(function (d) {
            var nPts = c.filter(function (p) { return PG().coordContainsSid(p.szSid, d); }).length;
            var coord = PG().parseCoord(d);
            var sub = '';
            if (coord.modbusIds.length > 1) {
                // 多站號：可展開的站號子清單
                sub = coord.modbusIds.map(function (mid, j) {
                    var label = (j < coord.deviceNames.length && coord.deviceNames[j]) ? coord.deviceNames[j] : mid;
                    return item('data-dev="' + d.nId + '" data-mid="' + esc(mid) + '" data-crumb="' + esc(label) + '"',
                        'fa-microchip', '#6ea8fe', esc(label), '', 'right', 'pp-item-sub-row');
                }).join('');
            } else {
                var dg = PG().coordDeviceGroups(d, _points);
                if (dg && dg.names.length) {
                    // 單站號 + Device 分群（+ 未分群桶）
                    sub = dg.names.map(function (g) {
                        return item('data-dev="' + d.nId + '" data-dg="' + esc(g) + '" data-crumb="' + esc(g) + '"',
                            'fa-microchip', '#6ea8fe', esc(g), countLabel(dg.countByGroup[g] || 0), 'right', 'pp-item-sub-row');
                    }).join('');
                    if (dg.hasUngrouped) {
                        sub += item('data-dev="' + d.nId + '" data-dg="" data-crumb="' + esc(t('designer.picker.ungrouped')) + '"',
                            'fa-inbox', '#6c757d', esc(t('designer.picker.ungrouped')), '', 'right', 'pp-item-sub-row');
                    }
                }
            }
            if (sub) {
                return item('data-toggle="1"', 'fa-server', '#0d6efd', esc(d.szName), countLabel(nPts), 'down')
                     + '<div class="pp-sub" style="display:' + (szQ ? '' : 'none') + ';">' + sub + '</div>';
            }
            return item('data-dev="' + d.nId + '" data-crumb="' + esc(d.szName) + '"', 'fa-server', '#0d6efd', esc(d.szName), countLabel(nPts), 'right');
        }).join('');
    }

    function renderGroups(src) {
        var c = candidates().filter(function (p) { return sourceOf(p.szSid) === src; });
        var groups = {};
        c.forEach(function (p) { var g = p.szGroupName || ''; groups[g] = (groups[g] || 0) + 1; });
        var names = Object.keys(groups).sort();
        // 計算點位：只有「未分組」一桶時直接進點位清單（同 Designer 無群組時平鋪）
        var icon = src === SRC_CALC ? 'fa-layer-group' : src === SRC_DB ? 'fa-database' : 'fa-network-wired';
        var color = src === SRC_CALC ? '#d39e00' : src === SRC_DB ? '#198754' : '#6f42c1';
        return names.map(function (g) {
            var szLabel = g || (src === SRC_DB ? t('designer.picker.db_source_default') : t('designer.picker.ungrouped'));
            return item('data-grp="' + esc(g) + '" data-crumb="' + esc(szLabel) + '"', g ? icon : 'fa-inbox', g ? color : '#6c757d',
                esc(szLabel), countLabel(groups[g]), 'right');
        }).join('') || empty('fa-inbox', t('designer.picker.no_matching_points'));
    }

    function pointsInNav() {
        var n = _nav;
        return candidates().filter(function (p) {
            if (n.src === SRC_DEVICE) {
                if (n.mid != null) {
                    var pfx = PG().getSidPrefix(p.szSid);
                    var base = PG().subRangeBase(n.dev, n.mid);
                    return pfx >= base && pfx < base + 256;
                }
                var d = _devices.find(function (x) { return x.nId === n.dev; });
                if (!d || !PG().coordContainsSid(p.szSid, d)) return false;
                return n.dg == null || PG().getDeviceGroup(p) === n.dg;
            }
            return sourceOf(p.szSid) === n.src && (p.szGroupName || '') === n.grp;
        });
    }

    function renderPoints(szQ) {
        var pts = pointsInNav().filter(function (p) { return !szQ || (p.szName || '').toLowerCase().indexOf(szQ) >= 0; });
        if (!pts.length) return empty('fa-inbox', t('designer.picker.no_matching_points'));
        return pts.map(function (p) {
            var szName = (p.szDeviceLabel ? '<span class="pp-item-dev">' + esc(p.szDeviceLabel) + '</span><span class="pp-item-slash">/</span>' : '') + esc(p.szName);
            return '<div class="pp-item" data-sid="' + esc(p.szSid) + '">' +
                '<i class="fas fa-circle pp-item-dot"></i>' +
                '<div class="pp-item-text"><div class="pp-item-name">' + szName + '</div></div>' +
                '<span class="pp-item-unit">' + esc(p.szUnit || '') + '</span></div>';
        }).join('');
    }

    function selectPoint(el) {
        _el.querySelectorAll('.pp-item.selected').forEach(function (x) { x.classList.remove('selected'); });
        el.classList.add('selected');
        var sid = el.dataset.sid;
        _picked = _points.find(function (p) { return p.szSid === sid; }) || null;
        _el.querySelector('.pp-confirm').disabled = !_picked;
    }

    function onListClick(ev) {
        var it = ev.target.closest('.pp-item');
        if (!it) return;
        if (it.dataset.src) {
            var src = it.dataset.src;
            // 計算點位全部未分組 → 直接列點位
            if (src === SRC_CALC && !candidates().some(function (p) { return sourceOf(p.szSid) === SRC_CALC && p.szGroupName; })) {
                showPointStep({ src: SRC_CALC, grp: '' }, sourceLabel(SRC_CALC));
            } else {
                showGroupStep(src);
            }
        } else if (it.dataset.toggle) {
            var sub = it.nextElementSibling;
            var isOpen = sub.style.display !== 'none';
            sub.style.display = isOpen ? 'none' : '';
            var ic = it.querySelector('.pp-toggle-icon');
            if (ic) ic.style.transform = isOpen ? '' : 'rotate(180deg)';
        } else if (it.dataset.dev) {
            showPointStep({
                src: SRC_DEVICE,
                dev: parseInt(it.dataset.dev, 10),
                mid: it.dataset.mid != null ? it.dataset.mid : null,
                dg: it.dataset.dg != null ? it.dataset.dg : null
            }, it.dataset.crumb);
        } else if (it.dataset.grp != null) {
            showPointStep({ src: _nav.src, grp: it.dataset.grp }, it.dataset.crumb);
        } else if (it.dataset.sid) {
            selectPoint(it);
        }
    }

    window.PointPicker = { open: open, load: load };
})();
