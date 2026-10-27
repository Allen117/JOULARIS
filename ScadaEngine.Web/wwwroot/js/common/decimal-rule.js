// 點位小數位數自動判斷（前端共用單一真相）
// ============================================================
// 第一性原理：顯示幾位小數取決於「這是什麼量」，而量的身分幾乎都寫在單位上；
// 少數單位相同但精度需求不同的量（功因、次數、變頻器頻率）才需要看名稱。
// 只在 Designer「綁點位當下」呼叫一次，結果寫進 widget 的 nDecimalPlaces，
// 執行期直接讀屬性 — 本檔不參與執行期渲染（plan 2026-10-23-designer-auto-decimals）。
//
// 判斷順序：名稱特例 → 單位表 → 預設 1
//
// 主入口：window.DecimalRule
//   guess(szName, szUnit)  → 0–6 的整數
//   normalizeUnit(szUnit)  → 比對用正規化單位（去空白、小寫、m³→m3、℃→°c…）
// ============================================================
(function () {
    'use strict';

    const N_DEFAULT = 1;

    // 單位（已正規化）→ 位數
    const UNIT_DECIMALS = {
        // 電壓 / 電流
        'v': 1, 'kv': 2, 'a': 1,
        // 功率（MW 正規化後與 mW 同為 mw，現場不會出現 mW）
        'kw': 1, 'kva': 1, 'kvar': 1, 'mw': 2,
        // 電度
        'kwh': 1, '度': 1, 'kvarh': 1,
        // 溫度
        '°c': 1, '°f': 1,
        // 壓力
        'bar': 2, 'kg/cm2': 2, 'kgf/cm2': 2, 'mpa': 3, 'kpa': 1, 'pa': 1,
        // 流量 / 累積量
        'm3/h': 1, 'cmh': 1, 'lpm': 1, 'l/min': 1, 'nm3/min': 1, 'm3': 1, 'nm3': 1,
        // 濕度 / 百分比
        '%rh': 1, '%': 1,
        // 冷凍噸
        'rt': 1, 'rth': 1,
        // 效率
        'kw/rt': 2, 'kw/(m3/min)': 2, 'kw/(nm3/min)': 2,
        // 轉速 / 濃度
        'rpm': 0, 'ppm': 0
    };

    // PF 以「非英數字」為邊界（底線也算邊界），避免誤中 PFM / APF
    const RE_POWER_FACTOR = /功因|功率因數|cos\s*φ|(^|[^A-Za-z0-9])PF([^A-Za-z0-9]|$)/i;
    const RE_COUNT        = /次數|台數|狀態|count/i;
    const RE_VFD          = /變頻|VFD|輸出/i;

    function normalizeUnit(szUnit) {
        return String(szUnit == null ? '' : szUnit)
            .replace(/\s+/g, '')
            .toLowerCase()
            .replace(/³/g, '3')
            .replace(/²/g, '2')
            .replace(/\^/g, '')
            .replace(/℃/g, '°c')
            .replace(/℉/g, '°f')
            .replace(/º/g, '°')
            .replace(/\/hr$/, '/h');
    }

    function guess(szName, szUnit) {
        const name = String(szName == null ? '' : szName);
        const unit = normalizeUnit(szUnit);

        if (RE_POWER_FACTOR.test(name)) return unit === '%' ? 1 : 2;
        if (RE_COUNT.test(name)) return 0;
        if (unit === 'hz') return RE_VFD.test(name) ? 1 : 2;

        const n = UNIT_DECIMALS[unit];
        return n != null ? n : N_DEFAULT;
    }

    const api = { guess: guess, normalizeUnit: normalizeUnit };
    if (typeof window !== 'undefined') window.DecimalRule = api;
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
})();
