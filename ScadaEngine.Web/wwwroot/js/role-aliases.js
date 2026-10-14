// ============================================================
// 電表角色別名表（共用模組）— 掛 window._roleAliases + window._roleMatch
// ============================================================
// 使用端：
//   - designer/picker.js（同設備點位角色比對，_findSameDevicePointForRole）
//   - designer/row-template.js（表格列範本自動帶入建議）
//   - energymeter.js（主要電表資訊點位自動比對）
// key   = canonical label (顯示用)
// value = aliases array (全 lower-case，比對時 candidate 也轉小寫)
//
// 比對規則（window._roleMatch.resolveRole）：把點位名正規化後以
// `- _ 空白` 切成 tokens，依「整名 → 開頭 token → 尾段 token」順序查別名，
// 任一命中即定。開頭優先於尾段是為了吃使用者「角色在前、相別在後」的命名
// （V_a / I_avg → 角色 V / A，而非把尾段 a / avg 當角色）；整名先查是為了
// 不拆散本身就是完整別名的複合詞（power_factor / real_power / l-n）。
// 設備前綴式（PM-1-V）開頭 pm 非角色 → 自然退回尾段 v，兩種命名同時成立。
//
// 載入順序：本檔須先於「呼叫 _roleMatch 的使用端」載入；resolveRole 內對
// picker.js 頂層 _normalizeHeaderText 的引用在**呼叫時**才解析（runtime），
// 故 picker.js 早於或晚於本檔載入皆可，只要呼叫發生在兩者都載入之後。
// ============================================================

(function () {
    'use strict';

    window._roleAliases = {
        'V':    ['v', 'u', 'voltage', 'volt', 'volts', 'vrms', 'vll', 'vln', 'l-l', 'l-n',
                 'vab', 'vbc', 'vca', 'van', 'vbn', 'vcn',
                 '電壓', '三相電壓', '線電壓', '相電壓'],
        'A':    ['a', 'i', 'amp', 'amps', 'ampere', 'amperes', 'current', 'irms',
                 'ia', 'ib', 'ic',
                 '電流', '三相電流'],
        'KW':   ['kw', 'p', 'w', 'mw', 'watt', 'watts', 'kilowatt', 'kilowatts',
                 'power', 'active', 'active_power', 'activepower',
                 'real_power', 'realpower', 'actpower',
                 '功率', '有效功率', '實功', '實功率', '主動功率'],
        'KVA':  ['kva', 's', 'apparent', 'apparent_power', 'apparentpower',
                 '視在功率'],
        'KVAr': ['kvar', 'kvars', 'q', 'var', 'vars',
                 'reactive', 'reactive_power', 'reactivepower',
                 '無效功率', '無功功率', '虛功', '虛功率'],
        'PF':   ['pf', 'powerfactor', 'power_factor', 'cosphi', 'cos_phi',
                 'cos', 'cosΦ', 'cos∅', 'λ', 'factor',
                 '功率因數'],
        'KWH':  ['kwh', 'kw_h', 'wh', 'mwh', 'kilowatthour', 'kilowatthours',
                 'energy', 'consumption', 'accumulated',
                 'kwh_total', 'kwhtotal', 'total_kwh',
                 '度數', '用電量', '電能', '累計用電', '累積電能', '總電能',
                 '用電度數', '總耗電'],
        'Hz':   ['hz', 'f', 'frequency', 'freq', 'cycle',
                 '頻率', '赫茲', '週期'],
        'KWHr': ['kwhr', 'kvarh', 'reactiveenergy', 'reactive_energy',
                 'kvarh_total', 'kvarhtotal',
                 '無功電能', '累計無功', '無功累計']
    };

    // ── 名稱正規化（優先沿用 picker.js 的 _normalizeHeaderText，缺席時自帶等價輕量版）──
    // 輕量版：trim → 全形轉半形（含全形空白）→ 去尾端括號單位 → 壓縮空白 → 小寫
    function _normalize(szRaw) {
        if (szRaw == null) return '';
        if (typeof _normalizeHeaderText === 'function') return _normalizeHeaderText(szRaw);
        let s = String(szRaw).trim();
        s = s.replace(/[！-～]/g, ch => String.fromCharCode(ch.charCodeAt(0) - 0xfee0))
             .replace(/　/g, ' ');
        let prev;
        do { prev = s; s = s.replace(/\s*\([^()]*\)\s*$/, ''); } while (s !== prev);
        return s.replace(/\s+/g, ' ').trim().toLowerCase();
    }

    // 正規化字串切 tokens（吃 - _ 空白）
    function _tokenize(szNorm) {
        return szNorm ? szNorm.split(/[-_\s]+/).filter(Boolean) : [];
    }

    // 單一 token（已 lower-case）→ canonical 角色 label 或 null（多命中取第一、警告）
    function _byAlias(szTok) {
        if (!szTok) return null;
        const hits = [];
        for (const role of Object.keys(window._roleAliases)) {
            if (window._roleAliases[role].indexOf(szTok) >= 0) hits.push(role);
        }
        if (hits.length === 0) return null;
        if (hits.length > 1) {
            console.warn('[role-match] multiple role hits for "' + szTok + '":', hits, '— picked', hits[0]);
        }
        return hits[0];
    }

    // 點位名 → canonical 角色 label 或 null（整名 → 開頭 token → 尾段 token）
    function resolveRole(szRaw) {
        const szNorm = _normalize(szRaw);
        if (!szNorm) return null;
        const toks = _tokenize(szNorm);
        let r = _byAlias(szNorm);                     // 整名（吃 power_factor / l-n 等複合別名）
        if (r) return r;
        if (toks.length > 0) { r = _byAlias(toks[0]); if (r) return r; }   // 開頭 token 優先
        if (toks.length > 1) { r = _byAlias(toks[toks.length - 1]); if (r) return r; } // 尾段回退
        return null;
    }

    // 點位名的「開頭 token」是否命中角色（供 row-template 判定風格 B）→ 角色 label 或 null
    function leadingRole(szRaw) {
        const toks = _tokenize(_normalize(szRaw));
        return toks.length > 0 ? _byAlias(toks[0]) : null;
    }

    window._roleMatch = {
        resolveRole: resolveRole,   // 整名 → 開頭 → 尾段
        leadingRole: leadingRole,   // 僅開頭 token
        byAlias:     _byAlias,      // 單 token
        normalize:   _normalize
    };

    // ============================================================
    // 設備角色別名表（plan 2026-10-14-designer-equipment-autobind）
    // 掛 window._equipRoleAliases + window._equipRoleMatch
    // ============================================================
    // 使用端：designer/equip-autobind.js（泵浦 / 冷卻水塔 / 空調箱風扇 / 冰機
    // 拖到畫布後，在同一台 Device 的點位集合內依名稱比對各槽位）。
    // 與上方電表角色（_roleAliases / _roleMatch）完全獨立，互不影響。
    //
    // 比對規則（resolveEquipRole，整名 → 尾 token → 字尾式）：
    //   (a) 整名命中別名           → 吃「運轉」「故障」「頻率設定」這類純角色命名
    //   (b) 以 - _ 空白 切 tokens，tokens ≥ 2 時尾 token 命中
    //                               → PUMP-1-RUN、冰水泵1_運轉
    //   (c) 單 token 且前綴非空時，依「別名長度由長到短」試 endsWith
    //                               → CH1RUN、CWP1ALARM、冰水泵1頻率設定
    //   `on / st / sp / fb` 這類短別名只在前綴非空（(b)/(c)）時成立，
    //   單獨 `ON` / `SP` 不算角色，壓低誤判。
    //   `出水溫` 同時在 CHWOUT 與 WATERTEMP：呼叫端以 arrRoles 限定該元件
    //   型別需要的角色（冰機不找 WATERTEMP、冷卻塔不找 CHWOUT），不衝突。
    // ============================================================

    window._equipRoleAliases = {
        RUN:       ['run', 'running', 'on', 'status', 'st', '運轉', '運行', '啟動狀態'],
        FAULT:     ['alarm', 'alm', 'fault', 'flt', 'trip', 'err', 'error', 'fail', '故障', '警報', '跳脫'],
        MODE:      ['moa', 'hoa', 'mode', 'auto', 'am', 'ma', 'remote', 'rem', 'local', 'loc',
                    '手自動', '遠端', '現場', '模式'],
        FREQ:      ['hz', 'freq', 'frequency', 'spd', 'speed', 'fb', 'hzfb', 'rpm', '頻率', '轉速'],
        STARTSTOP: ['cmd', 'start', 'startstop', 'ss', 'onoff', 'runcmd', '啟停', '啟動', '開關'],
        FREQSET:   ['sp', 'hzsp', 'freqsp', 'speedsp', 'sethz', 'setfreq', '頻率設定', '設定頻率'],
        LOAD:      ['load', 'pct', 'rla', '負載'],
        CHWOUT:    ['chws', 'chwout', 'chwst', 'lchwt', '冰水出水', '出水溫'],
        SETTEMP:   ['chwsp', 'tsp', 'settemp', '設定溫度', '溫度設定'],
        WATERTEMP: ['cwout', 'cws', 'cwst', '出水溫', '冷卻水出水']
    };

    // 只能在「前綴非空」時成立的短別名（整名單獨出現不算）
    const _EQUIP_SHORT_ALIASES = new Set(['on', 'st', 'sp', 'fb']);

    // 依 arrRoles（null = 全部）取得 [role, alias] 平鋪清單，別名長度由長到短（字尾式用）
    function _equipAliasPairs(arrRoles) {
        const roles = arrRoles || Object.keys(window._equipRoleAliases);
        const pairs = [];
        roles.forEach(role => {
            (window._equipRoleAliases[role] || []).forEach(alias => pairs.push([role, alias]));
        });
        pairs.sort((x, y) => y[1].length - x[1].length);
        return pairs;
    }

    // 整字命中（szTok 已 lower-case）→ role 或 null；bAllowShort=false 時排除短別名
    function _equipByAlias(szTok, arrRoles, bAllowShort) {
        if (!szTok) return null;
        if (!bAllowShort && _EQUIP_SHORT_ALIASES.has(szTok)) return null;
        const roles = arrRoles || Object.keys(window._equipRoleAliases);
        for (const role of roles) {
            const arr = window._equipRoleAliases[role];
            if (arr && arr.indexOf(szTok) >= 0) return role;
        }
        return null;
    }

    // 點位名 → 設備角色（RUN / FAULT / …）或 null
    // arrRoles：限定要解析的角色（省略 = 全部）
    function resolveEquipRole(szRaw, arrRoles) {
        const szNorm = _normalize(szRaw);
        if (!szNorm) return null;
        // (a) 整名（短別名不算）
        let r = _equipByAlias(szNorm, arrRoles, false);
        if (r) return r;
        const toks = _tokenize(szNorm);
        if (toks.length >= 2) {
            // (b) 尾 token（前綴非空 → 短別名可）
            return _equipByAlias(toks[toks.length - 1], arrRoles, true);
        }
        // (c) 字尾式：單 token、前綴非空
        for (const [role, alias] of _equipAliasPairs(arrRoles)) {
            if (szNorm.length > alias.length && szNorm.endsWith(alias)) return role;
        }
        return null;
    }

    window._equipRoleMatch = {
        resolveEquipRole: resolveEquipRole,   // 整名 → 尾 token → 字尾式
        normalize:        _normalize
    };
})();
