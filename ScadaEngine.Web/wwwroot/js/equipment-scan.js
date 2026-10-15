// ============================================================
// 設備效率診斷 — 掃描判定（純函式，不碰 DOM；node 可測）
// 掛 window._equipScan（plan 2026-10-15-equipment-efficiency-diagnosis 決策 2）
// ============================================================
// 流程：全點位（/Designer/Points + szDeviceLabel，由 PointPicker.load() 提供）
//   → 依 szDeviceLabel 分群（設備邊界 = 既有 Device 分群，point-grouping.js 四級 fallback）
//   → 每群以 _equipRoleMatch.resolveEquipRole(name, 該 kind 的角色集合) 算命中
//   → 判定該群是否為此 kind 的候選、缺哪些必要角色、是否已存在
//
// 比對規則：resolveEquipRole（整名 → 尾 token → 字尾式）落空時，掃描端再補一步
//   (d) 中文別名「包含式」：名稱含 ≥ 3 字的中文別名即命中（最長者優先）
//       → 吃「冰水出水溫度」「冷卻水進水溫度」這類別名後面再接「溫度」的命名；
//         2 字別名（功率 / 壓力 / 流量）不走包含式，避免「功率因數」被當 KW。
//   resolveEquipRole 本身行為不變（Designer 自動綁定不受影響）。
//
// 判定：
//   冰機特徵角色 FLOW / CHWS / CHWR / CDWS / CDWR / RT / EVAPT / CONDT；
//   空壓機特徵角色 AIRFLOW / DISCHPRESS / DISCHTEMP / LOADED；
//   群名含關鍵字（冰機 / 主機 / chiller / CH#；空壓 / air comp / compressor / AC#）+2 分。
//   score(本 kind) > 0 且 ≥ score(另一 kind) 才列為候選（同一群不會同時被兩邊搶，平手兩邊都列）。
// 必要角色（與 Common EquipmentKind.MissingRequired 同語意）：
//   電力 KW 或 KWH；冰機產出 FLOW + CHWS + CHWR 三者齊或 RT；空壓機產出 AIRFLOW。
// ============================================================
(function (root) {
    'use strict';

    var CHILLER = 'chiller', COMPRESSOR = 'compressor';

    var ROLES = {
        chiller: ['KW', 'KWH', 'FLOW', 'CHWS', 'CHWR', 'RT', 'CDWS', 'CDWR', 'EVAPT', 'CONDT', 'LOAD', 'RUN'],
        compressor: ['KW', 'KWH', 'AIRFLOW', 'DISCHPRESS', 'DISCHTEMP', 'LOADED', 'LOAD', 'RUN']
    };
    var SIGNATURE = {
        chiller: ['FLOW', 'CHWS', 'CHWR', 'CDWS', 'CDWR', 'RT', 'EVAPT', 'CONDT'],
        compressor: ['AIRFLOW', 'DISCHPRESS', 'DISCHTEMP', 'LOADED']
    };
    var KEYWORD = {
        chiller: /(冰機|冰水主機|主機|chiller|(^|[^a-z])ch[\s_-]*\d)/i,
        compressor: /(空壓|空氣壓縮|air[\s_-]*comp|compressor|(^|[^a-z])ac[\s_-]*\d)/i
    };
    // 「主要 SID」— 已存在判定用（任一相同即視為同一台）
    var PRIMARY_ROLES = ['KW', 'KWH', 'FLOW', 'RT', 'AIRFLOW'];

    function other(kind) { return kind === COMPRESSOR ? CHILLER : COMPRESSOR; }

    function aliasesOf() {
        return (root && root._equipRoleAliases) || {};
    }

    function normalize(sz) {
        var m = root && root._equipRoleMatch;
        if (m && m.normalize) return m.normalize(sz);
        return String(sz == null ? '' : sz).trim().toLowerCase();
    }

    // 中文（含 ≥ 3 個 CJK 字）別名，最長優先
    function _cjkContainsPairs(arrRoles) {
        var al = aliasesOf();
        var pairs = [];
        arrRoles.forEach(function (role) {
            (al[role] || []).forEach(function (a) {
                if (/^[一-鿿]{3,}$/.test(a)) pairs.push([role, a]);
            });
        });
        pairs.sort(function (x, y) { return y[1].length - x[1].length; });
        return pairs;
    }

    // 點位名 → 角色（限定 arrRoles）或 null
    function resolveScanRole(szName, arrRoles) {
        var m = root && root._equipRoleMatch;
        var r = m && m.resolveEquipRole ? m.resolveEquipRole(szName || '', arrRoles) : null;
        if (r) return r;
        var szNorm = normalize(szName);
        if (!szNorm) return null;
        var pairs = _cjkContainsPairs(arrRoles);
        for (var i = 0; i < pairs.length; i++) {
            if (szNorm.indexOf(pairs[i][1]) >= 0) return pairs[i][0];
        }
        return null;
    }

    // 與 C# EquipmentKind.MissingRequired 同語意；回缺少的必要項（'KW|KWH' / 'FLOW+CHWS+CHWR|RT' / 'AIRFLOW'）
    function missingRequired(kind, boundRoles) {
        var has = function (r) { return boundRoles.indexOf(r) >= 0; };
        var miss = [];
        if (!has('KW') && !has('KWH')) miss.push('KW|KWH');
        if (kind === COMPRESSOR) {
            if (!has('AIRFLOW')) miss.push('AIRFLOW');
        } else if (!(has('FLOW') && has('CHWS') && has('CHWR')) && !has('RT')) {
            miss.push('FLOW+CHWS+CHWR|RT');
        }
        return miss;
    }

    // 單位別名鍵（與 C# EquipmentUnitScale.NormalizeKey 同）
    function unitKey(sz) {
        var s = String(sz == null ? '' : sz).trim().replace(/[！-～]/g, function (ch) {
            return String.fromCharCode(ch.charCodeAt(0) - 0xfee0);
        });
        return s.replace(/³/g, '3').replace(/²/g, '2').replace(/ /g, '').toLowerCase();
    }

    // 點位單位 → 該角色可選單位的 canonical 字串；認不得 → 基準單位（第一個）
    // meta.roles[role] = { quantity, units: [{unit, scale}] }；meta.unitAliases[quantity] = { alias: unit }
    function guessUnit(role, szPointUnit, meta) {
        var ri = meta && meta.roles && meta.roles[role];
        if (!ri || !ri.units || ri.units.length === 0) return '';
        var szBase = ri.units[0].unit;
        if (!szPointUnit) return szBase;
        var szTrim = String(szPointUnit).trim();
        for (var i = 0; i < ri.units.length; i++) if (ri.units[i].unit === szTrim) return szTrim;
        var map = (meta.unitAliases && meta.unitAliases[ri.quantity]) || {};
        var hit = map[unitKey(szTrim)];
        return hit || szBase;
    }

    // 一群點位 × kind → 角色命中
    function _resolveGroup(points, kind, rtSids) {
        var arrRoles = ROLES[kind];
        var byRole = {};
        points.forEach(function (p) {
            var r = resolveScanRole(p.szName || '', arrRoles);
            if (!r && kind === CHILLER && rtSids && rtSids[p.szSid]) r = 'RT';   // WaterCircuit 葉子已綁的 RT 點
            if (r) (byRole[r] = byRole[r] || []).push(p);
        });
        return byRole;
    }

    function _score(kind, byRole, szLabel) {
        var n = 0;
        SIGNATURE[kind].forEach(function (r) { if (byRole[r]) n++; });
        var isKeyword = KEYWORD[kind].test(szLabel || '');
        return { nScore: n + (isKeyword ? 2 : 0), nSignature: n, isKeyword: isKeyword };
    }

    function _norm(sz) { return String(sz == null ? '' : sz).trim().toLowerCase(); }

    // existing = [{ name, sids: [...] }]（同 kind 的既有設備）
    function _findExisting(szName, primarySids, existing) {
        var szN = _norm(szName);
        for (var i = 0; i < (existing || []).length; i++) {
            var e = existing[i];
            if (szN && _norm(e.name) === szN) return e;
            var sids = e.sids || [];
            for (var j = 0; j < primarySids.length; j++) {
                if (sids.indexOf(primarySids[j]) >= 0) return e;
            }
        }
        return null;
    }

    /**
     * 掃描。
     * @param {Array} points  [{ szSid, szName, szUnit, szDeviceLabel }]
     * @param {string} kind   'chiller' | 'compressor'
     * @param {Object} opts   { rtSids: {sid:true}, existing: [{name, sids}], meta }
     * @returns {Array} 候選 [{ szLabel, szName, roles: {ROLE: {szSid, szName, szUnit, szUnitGuess, nHits, alts:[...] }},
     *                         arrMatchedRoles, arrMissing, isReady, nScore, isKeyword, szConfidence, existing }]
     */
    function scan(points, kind, opts) {
        opts = opts || {};
        var groups = {};
        (points || []).forEach(function (p) {
            var szLabel = p && p.szDeviceLabel ? String(p.szDeviceLabel).trim() : '';
            if (!szLabel || !p.szSid) return;
            (groups[szLabel] = groups[szLabel] || []).push(p);
        });

        var out = [];
        Object.keys(groups).sort().forEach(function (szLabel) {
            var pts = groups[szLabel];
            var mine = _resolveGroup(pts, kind, opts.rtSids);
            var theirs = _resolveGroup(pts, other(kind), opts.rtSids);
            var sMine = _score(kind, mine, szLabel);
            var sTheirs = _score(other(kind), theirs, szLabel);
            if (sMine.nScore <= 0 || sMine.nScore < sTheirs.nScore) return;

            var roles = {};
            var arrMatched = [];
            ROLES[kind].forEach(function (r) {
                var hits = mine[r];
                if (!hits || hits.length === 0) return;
                arrMatched.push(r);
                roles[r] = {
                    szSid: hits[0].szSid,
                    szName: hits[0].szName,
                    szUnit: hits[0].szUnit || '',
                    szUnitGuess: guessUnit(r, hits[0].szUnit, opts.meta),
                    nHits: hits.length,
                    alts: hits.map(function (h) { return { szSid: h.szSid, szName: h.szName, szUnit: h.szUnit || '' }; })
                };
            });
            var arrMissing = missingRequired(kind, arrMatched);
            var primarySids = PRIMARY_ROLES.filter(function (r) { return roles[r]; })
                .map(function (r) { return roles[r].szSid; });
            var existing = _findExisting(szLabel, primarySids, opts.existing);
            var szConfidence = arrMissing.length === 0
                ? (sMine.nSignature >= 2 || sMine.isKeyword ? 'high' : 'medium')
                : 'low';
            out.push({
                szLabel: szLabel,
                szName: szLabel,
                roles: roles,
                arrMatchedRoles: arrMatched,
                arrMissing: arrMissing,
                isReady: arrMissing.length === 0,
                nScore: sMine.nScore,
                isKeyword: sMine.isKeyword,
                szConfidence: szConfidence,
                existing: existing,
                arrGroupPoints: pts
            });
        });
        return out;
    }

    var api = {
        ROLES: ROLES,
        scan: scan,
        resolveScanRole: resolveScanRole,
        missingRequired: missingRequired,
        guessUnit: guessUnit,
        unitKey: unitKey
    };
    if (root) root._equipScan = api;
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : (typeof globalThis !== 'undefined' ? globalThis : null));
