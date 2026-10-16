/* 冰機評級門檻依據 — 設定頁（equipment-setting.js）與診斷頁（ems-equipment.js）共用，須在兩者之前載入。
   - format(basis)：thresholdBasis（伺服端 Common ChillerEfficiencyStandard.Resolve 結果，或本檔 resolve 的 basis）
       → 依語系組字串，例「90.1 水冷離心 400–600RT 定頻(Path A) · 未填額定 RT」
   - resolve(std, p)：設定頁即時預覽 / 列表用。std = /EquipmentEfficiencySetting/api/meta 的 chillerStandard 表；
       邏輯逐行對照 ChillerEfficiencyStandard.Resolve（實際評級一律以伺服端為準，此處僅供顯示）。 */
(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }
    function r3(v) { return Math.round(v * 1000) / 1000; }

    var COOLING = ['water', 'air'];
    var TYPES = ['centrifugal', 'screw', 'scroll', 'reciprocating'];

    function bandText(b) {
        if (b.bandMax == null) return t('eq.basis.band_ge', { min: b.bandMin });
        if (!b.bandMin) return t('eq.basis.band_lt', { max: b.bandMax });
        return t('eq.basis.band_range', { min: b.bandMin, max: b.bandMax });
    }

    function typeText(b) {
        var szCooling = t('eq.cooling.' + b.cooling);
        if (b.family === 'centrifugal') return t('eq.basis.cooling_kind', { cooling: szCooling, kind: t('eq.ctype.centrifugal') });
        if (b.family === 'pd') {
            return t('eq.basis.cooling_kind', {
                cooling: szCooling,
                kind: t('eq.basis.with_type', { base: t('eq.basis.pd'), type: t('eq.ctype.' + b.chillerType) })
            });
        }
        // 氣冷：90.1 不分形式，有選才括號附註
        return b.chillerType ? t('eq.basis.with_type', { base: szCooling, type: t('eq.ctype.' + b.chillerType) }) : szCooling;
    }

    function format(b) {
        if (!b || b.mode !== 'standard') return t('eq.basis.manual');
        var s = t('eq.basis.std', { type: typeText(b), band: bandText(b), path: t(b.path === 'B' ? 'eq.basis.path_b' : 'eq.basis.path_a') });
        if (b.isTypeUnset) s += ' · ' + t('eq.basis.type_unset');
        if (b.isRatedMissing) s += ' · ' + t('eq.basis.rated_missing');
        return s;
    }

    // p 欄位沿用設定頁 Hungarian 命名（szCoolingType / szChillerType / szControlType / dRatedOutput / szThresholdMode / dGood/FairThreshold）
    function resolve(std, p, manualDefaults) {
        if (!std || p.szThresholdMode === 'manual') {
            var d = manualDefaults || {};
            return {
                good: p.dGoodThreshold != null ? p.dGoodThreshold : d.good,
                fair: p.dFairThreshold != null ? p.dFairThreshold : d.fair,
                basis: { mode: 'manual' }
            };
        }
        var isCoolingSet = COOLING.indexOf(p.szCoolingType) >= 0;
        var szCooling = isCoolingSet ? p.szCoolingType : 'water';
        var isTypeSet = TYPES.indexOf(p.szChillerType) >= 0;
        var szType = isTypeSet ? p.szChillerType : (szCooling === 'air' ? null : 'centrifugal');
        var isControlSet = p.szControlType === 'fixed' || p.szControlType === 'vsd';
        var szPath = p.szControlType === 'vsd' ? 'B' : 'A';
        var szFamily = szCooling === 'air' ? 'air' : (szType === 'centrifugal' ? 'centrifugal' : 'pd');
        var isRatedMissing = !(p.dRatedOutput > 0);

        var bands = std.bands.filter(function (b) { return b.family === szFamily; });
        var band = bands[0];
        if (!isRatedMissing) bands.forEach(function (b) { if (p.dRatedOutput >= b.minRt) band = b; });
        var dFl = szPath === 'B' ? band.pathB : band.pathA;
        return {
            good: dFl,
            fair: r3(dFl * std.fairFactor),
            basis: {
                mode: 'standard', cooling: szCooling, chillerType: szType, family: szFamily, path: szPath,
                bandMin: band.minRt, bandMax: band.maxRt, stdFl: dFl,
                isTypeUnset: !isCoolingSet || !isControlSet || (szCooling === 'water' && !isTypeSet),
                isRatedMissing: isRatedMissing
            }
        };
    }

    window.EquipmentThreshold = { format: format, resolve: resolve, COOLING: COOLING, TYPES: TYPES };
})();
