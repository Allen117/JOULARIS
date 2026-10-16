// 電費設定頁邏輯 — 台電各類電價方案 + 使用者自建方案（累進 / 單一費率 / 時間電價）檢視與編輯，
// 外加「採用時間軸」（哪天起改用哪個方案）與全量重算。
// 資料模型見 Features/TariffSetting/Models/TariffSettingModels.cs；台電預設 seed 見 Setting/tariff-taipower-defaults.json。
// 時間輸入用 flatpickr（24h）；訖時 00:00 代表 24:00（當日結束），起時晚於訖時代表跨午夜。
// 生效日為 date-only → 用原生 <input type="date">（無 AM/PM 問題，不需 flatpickr）。
(function () {
    'use strict';

    var CATEGORIES = ['lighting', 'lv', 'hv', 'ehv', 'custom'];
    var CUSTOM = 'custom';
    var PLAN_TYPES = ['progressive', 'flat', 'tou'];
    var DAY_TYPES = ['weekday', 'sat', 'sun_offday'];
    var SEASONS = ['summer', 'nonsummer'];
    var PERIOD_ORDER = { peak: 0, semipeak: 1, offpeak: 2 };
    // 契約容量歷史可記錄的契約別（與後端 TariffSettingService.ContractKeys 一致）
    var CONTRACT_KEYS = ['regular_kw', 'nonsummer_kw', 'semipeak_kw', 'sat_semipeak_kw', 'offpeak_kw'];

    // 全量重算單段天數上限（後端 MaxRecalculateDays = 366，留 1 天餘裕給含頭含尾）
    var SEGMENT_DAYS = 365;

    var g_config = null;      // 整份 TariffConfig（伺服器版 + 本機新增方案）
    var g_plan = null;        // 目前顯示方案的工作副本（collect() 時由 DOM 回填）
    var g_dirty = false;
    var g_newPlanIds = [];    // 尚未存到伺服器的新方案 Id（刪除時只需本機移除）

    document.addEventListener('DOMContentLoaded', function () {
        if (window.i18n) window.i18n.ready(load);
        else load();
    });

    function t(key, args) {
        return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key;
    }

    // ── 載入 ─────────────────────────────────────────────

    async function load() {
        try {
            var res = await fetch('/TariffSetting/api/config');
            if (!res.ok) throw new Error(res.statusText);
            g_config = await res.json();
        } catch (err) {
            console.error('tariff config load failed', err);
            document.getElementById('tsPlanContainer').innerHTML =
                '<div class="text-center text-danger py-4">' + escapeHtml(t('tariffsetting.msg.load_fail')) + '</div>';
            return;
        }

        if (!g_config.adoptions) g_config.adoptions = [];
        if (!g_config.contracts) g_config.contracts = [];
        fillCategorySelect();
        loadCostSummary();

        // 預設選到採用方案；未設定則第一類別第一方案
        var active = findPlan(g_config.szActivePlanId);
        var initial = active || g_config.plans[0];
        if (initial) {
            document.getElementById('tsCategory').value = initial.szCategory;
            fillPlanSelect(initial.szCategory);
            document.getElementById('tsPlan').value = initial.szPlanId;
            selectPlan(initial.szPlanId);
        }
        updateActiveCard();
        renderAdoptions();
        renderContracts();

        document.getElementById('tsCategory').addEventListener('change', onCategoryChange);
        document.getElementById('tsPlan').addEventListener('change', onPlanChange);

        // 任一編輯 → 標記未儲存（container 常駐，掛一次即可）
        var container = document.getElementById('tsPlanContainer');
        container.addEventListener('input', function () { g_dirty = true; });
        container.addEventListener('change', function () { g_dirty = true; });
    }

    function findPlan(planId) {
        if (!planId || !g_config) return null;
        return g_config.plans.find(function (p) { return p.szPlanId === planId; }) || null;
    }

    // 頂部累計卡片 — 主要電表本期 kWh / 流動電費（同 EMS 電費狀態卡資料源）
    async function loadCostSummary() {
        try {
            var res = await fetch('/TariffSetting/api/cost-summary');
            if (!res.ok) return;   // 失敗維持 -- 預設
            var d = await res.json();
            if (!d.hasPlan || !d.hasCircuit) return;

            document.getElementById('tsAccKwh').textContent =
                d.totalKwh.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
            if (d.totalCost != null) {
                document.getElementById('tsAccCost').textContent =
                    d.totalCost.toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
            }
            var hint = t('tariffsetting.card.acc_period_hint', { 0: d.periodLabel, 1: d.circuitName });
            document.getElementById('tsAccKwhHint').textContent = hint;
            document.getElementById('tsAccCostHint').textContent = hint;
        } catch (err) {
            console.error('cost summary load failed', err);
        }
    }

    function isCustom(p) { return !!p && p.szCategory === CUSTOM; }

    // 自建方案顯示使用者輸入的名稱；台電 seed 方案走 i18n key
    function planName(p) {
        return p.szName ? p.szName : t('tariff.plan.' + p.szPlanId);
    }

    function planLabel(p) {
        return t('tariff.category.' + p.szCategory) + '－' + planName(p);
    }

    // ── 方案選單 ─────────────────────────────────────────

    function fillCategorySelect() {
        var sel = document.getElementById('tsCategory');
        sel.innerHTML = CATEGORIES.map(function (c) {
            return '<option value="' + c + '">' + escapeHtml(t('tariff.category.' + c)) + '</option>';
        }).join('');
    }

    function fillPlanSelect(category) {
        var sel = document.getElementById('tsPlan');
        var activeSuffix = ' (' + t('tariffsetting.badge.active') + ')';
        var plans = g_config.plans.filter(function (p) { return p.szCategory === category; });
        sel.innerHTML = plans.map(function (p) {
            var suffix = p.szPlanId === g_config.szActivePlanId ? activeSuffix : '';
            return '<option value="' + escapeHtml(p.szPlanId) + '">' + escapeHtml(planName(p) + suffix) + '</option>';
        }).join('');
        if (plans.length === 0) {
            sel.innerHTML = '<option value="">' + escapeHtml(t('tariffsetting.select.empty')) + '</option>';
        }
        document.getElementById('tsCategoryDesc').textContent = t('tariff.category_desc.' + category);
    }

    function onCategoryChange() {
        if (!confirmDiscardDirty()) {
            document.getElementById('tsCategory').value = g_plan.szCategory;
            return;
        }
        var category = document.getElementById('tsCategory').value;
        fillPlanSelect(category);
        var first = document.getElementById('tsPlan').value;
        if (first) selectPlan(first);
        else renderEmptyCategory();
    }

    // 類別下無任何方案（自訂類別初始狀態）→ 清空編輯區
    function renderEmptyCategory() {
        g_plan = null;
        g_dirty = false;
        document.getElementById('tsPlanTitle').textContent = t('tariffsetting.editor.no_plan');
        document.getElementById('tsTypeBadge').innerHTML = '';
        document.getElementById('tsPlanContainer').innerHTML =
            '<div class="text-center text-muted py-4">' + escapeHtml(t('tariffsetting.editor.no_plan_hint')) + '</div>';
        updateActiveCard();
    }

    function onPlanChange() {
        if (!confirmDiscardDirty()) {
            document.getElementById('tsPlan').value = g_plan.szPlanId;
            return;
        }
        selectPlan(document.getElementById('tsPlan').value);
    }

    function confirmDiscardDirty() {
        if (!g_dirty) return true;
        return confirm(t('tariffsetting.confirm.switch_dirty'));
    }

    function selectPlan(planId) {
        var plan = findPlan(planId);
        if (!plan) return;
        g_plan = JSON.parse(JSON.stringify(plan));
        g_dirty = false;
        renderPlan();
    }

    function updateActiveCard() {
        var active = findPlan(g_config.szActivePlanId);
        document.getElementById('tsActivePlanName').textContent = active ? planLabel(active) : t('tariffsetting.card.no_active');
        var isActive = g_plan && g_plan.szPlanId === g_config.szActivePlanId;
        document.getElementById('tsActiveBadge').classList.toggle('d-none', !isActive);
        document.getElementById('btnTsSetActive').classList.toggle('d-none', !!isActive || !g_plan);

        // 只有自建方案可刪（台電 seed 方案刪了下次載入也會自動補回）
        var btnDel = document.getElementById('btnTsDelPlan');
        btnDel.disabled = !isCustom(g_plan);
        btnDel.title = isCustom(g_plan) ? '' : t('tariffsetting.msg.seed_undeletable');
    }

    // ── 渲染 ─────────────────────────────────────────────

    function renderPlan() {
        var p = g_plan;
        if (!p) { renderEmptyCategory(); return; }
        document.getElementById('tsPlanTitle').textContent = planLabel(p);
        document.getElementById('tsTypeBadge').innerHTML =
            '<span class="badge ' + (p.szType === 'tou' ? 'bg-primary' : 'bg-secondary') + '">' +
            escapeHtml(t('tariff.type.' + p.szType)) + '</span>';

        var html = renderIdentity(p);
        if (p.szNoteKey) {
            html += '<div class="alert alert-info py-2 ts-note"><i class="fas fa-info-circle me-1"></i>' +
                escapeHtml(t(p.szNoteKey)) + '</div>';
        }
        html += renderSummerRange(p);

        if (p.szType === 'progressive') {
            html += renderTiers(p);
        } else {
            if (p.baseFees.length > 0) html += renderBaseFees(p);
            if (p.szType === 'flat') html += renderFlatRate(p);
            if (p.szType === 'tou') html += renderFlowRates(p);
            if (p.surcharge) html += renderSurcharge(p);
        }

        var container = document.getElementById('tsPlanContainer');
        container.innerHTML = html;

        // flatpickr 綁定（禁原生 time input — CLAUDE.md 規範）
        container.querySelectorAll('.ts-time').forEach(function (el) {
            if (window._fpInit) window._fpInit.time(el);
        });
        // 夏月日下拉依月份重建
        container.querySelectorAll('.ts-summer-month').forEach(function (el) {
            el.addEventListener('change', function () { rebuildDayOptions(el); });
        });
        // 自建方案改型態 → 換骨架重繪
        var typeSel = document.getElementById('tsPlanType');
        if (typeSel) typeSel.addEventListener('change', function () { onTypeChange(typeSel.value); });

        updateActiveCard();
    }

    // 方案名稱 / 型態（名稱與型態僅自建方案可編輯；seed 方案顯示 i18n 名稱且唯讀）
    function renderIdentity(p) {
        var custom = isCustom(p);
        var typeCell = custom
            ? '<select id="tsPlanType" class="form-select form-select-sm">' +
              PLAN_TYPES.map(function (ty) {
                  return '<option value="' + ty + '"' + (ty === p.szType ? ' selected' : '') + '>' +
                      escapeHtml(t('tariff.type.' + ty)) + '</option>';
              }).join('') + '</select>'
            : '<input type="text" class="form-control form-control-sm" value="' +
              escapeHtml(t('tariff.type.' + p.szType)) + '" disabled>';

        return '<div class="row g-3 mb-3 ts-identity">' +
            '<div class="col-md-5"><label class="form-label small mb-1 fw-semibold">' +
            escapeHtml(t('tariffsetting.label.plan_name')) + '</label>' +
            '<input type="text" id="tsPlanName" class="form-control form-control-sm" maxlength="50" value="' +
            escapeHtml(custom ? (p.szName || '') : planName(p)) + '"' + (custom ? '' : ' disabled') + '></div>' +
            '<div class="col-md-4"><label class="form-label small mb-1 fw-semibold">' +
            escapeHtml(t('tariffsetting.label.plan_type')) + '</label>' + typeCell + '</div>' +
            '</div>';
    }

    function renderSummerRange(p) {
        var start = splitMonthDay(p.szSummerStart);
        var end = splitMonthDay(p.szSummerEnd);
        return '<div class="ts-summer-bar mb-3">' +
            '<span class="fw-semibold me-2"><i class="fas fa-sun me-1 text-warning"></i>' + escapeHtml(t('tariffsetting.label.summer_range')) + '</span>' +
            monthDaySelects('tsSummerStart', start) +
            '<span class="mx-2">~</span>' +
            monthDaySelects('tsSummerEnd', end) +
            '</div>';
    }

    function splitMonthDay(s) {
        var parts = (s || '').split('-');
        return { m: parseInt(parts[0], 10) || 1, d: parseInt(parts[1], 10) || 1 };
    }

    function monthDaySelects(idPrefix, val) {
        var mOpts = '', dOpts = '';
        for (var m = 1; m <= 12; m++)
            mOpts += '<option value="' + m + '"' + (m === val.m ? ' selected' : '') + '>' + m + '</option>';
        var maxDay = daysInMonth(val.m);
        for (var d = 1; d <= maxDay; d++)
            dOpts += '<option value="' + d + '"' + (d === val.d ? ' selected' : '') + '>' + d + '</option>';
        return '<select id="' + idPrefix + 'M" class="form-select form-select-sm ts-md-select ts-summer-month" data-day-select="' + idPrefix + 'D">' + mOpts + '</select>' +
            '<span class="mx-1">' + escapeHtml(t('tariffsetting.label.month')) + '</span>' +
            '<select id="' + idPrefix + 'D" class="form-select form-select-sm ts-md-select">' + dOpts + '</select>' +
            '<span class="ms-1">' + escapeHtml(t('tariffsetting.label.day')) + '</span>';
    }

    function daysInMonth(m) {
        return new Date(2000, m, 0).getDate();   // 2000 為閏年 → 2 月 29 天
    }

    function rebuildDayOptions(monthSel) {
        var daySel = document.getElementById(monthSel.getAttribute('data-day-select'));
        var prev = parseInt(daySel.value, 10) || 1;
        var maxDay = daysInMonth(parseInt(monthSel.value, 10));
        var opts = '';
        for (var d = 1; d <= maxDay; d++)
            opts += '<option value="' + d + '"' + (d === Math.min(prev, maxDay) ? ' selected' : '') + '>' + d + '</option>';
        daySel.innerHTML = opts;
    }

    function priceInput(value, attrs) {
        return '<input type="number" class="form-control form-control-sm ts-price" step="0.01" min="0" value="' +
            (value == null ? '' : value) + '" ' + attrs + '>';
    }

    // 累進級距表（自建方案多一欄增刪；台電 seed 級距結構固定不給增刪）
    function renderTiers(p) {
        var custom = isCustom(p);
        var rows = p.tiers.map(function (tier, i) {
            var isLast = i === p.tiers.length - 1;
            var rangeCell;
            if (isLast) {
                rangeCell = '<span class="text-nowrap">' + tier.nFrom + ' ' + escapeHtml(t('tariffsetting.tier.kwh_above')) + '</span>';
            } else if (i === 0) {
                rangeCell = '<span class="text-nowrap">' +
                    '<input type="number" class="form-control form-control-sm ts-tier-to d-inline-block" step="1" min="1" value="' + tier.nTo + '" data-tier="' + i + '" data-field="to"> ' +
                    escapeHtml(t('tariffsetting.tier.kwh_below')) + '</span>';
            } else {
                rangeCell = '<span class="text-nowrap">' + tier.nFrom + ' ~ ' +
                    '<input type="number" class="form-control form-control-sm ts-tier-to d-inline-block" step="1" min="1" value="' + tier.nTo + '" data-tier="' + i + '" data-field="to"> ' +
                    escapeHtml(t('tariffsetting.tier.kwh')) + '</span>';
            }
            var actionCell = custom
                ? '<td class="text-center"><button type="button" class="btn btn-outline-danger btn-sm"' +
                  (p.tiers.length <= 2 ? ' disabled' : '') + ' title="' + escapeHtml(t('tariffsetting.button.del_tier')) +
                  '" onclick="window._ts.removeTier(' + i + ')"><i class="fas fa-times"></i></button></td>'
                : '';
            return '<tr>' +
                '<td>' + rangeCell + '</td>' +
                '<td>' + priceInput(tier.dSummer, 'data-tier="' + i + '" data-field="summer"') + '</td>' +
                '<td>' + priceInput(tier.dNonSummer, 'data-tier="' + i + '" data-field="nonsummer"') + '</td>' +
                actionCell +
                '</tr>';
        }).join('');

        var addBtn = custom
            ? '<button type="button" class="btn btn-outline-secondary btn-sm mt-2" onclick="window._ts.addTier()">' +
              '<i class="fas fa-plus me-1"></i>' + escapeHtml(t('tariffsetting.button.add_tier')) + '</button>'
            : '';

        return sectionCard('tariffsetting.section.tiers',
            '<div class="table-responsive"><table class="table table-sm table-bordered align-middle mb-0 ts-table">' +
            '<thead class="table-light"><tr>' +
            '<th>' + escapeHtml(t('tariffsetting.col.tier_range')) + '</th>' +
            '<th>' + escapeHtml(t('tariffsetting.col.summer')) + ' (' + escapeHtml(t('tariffsetting.unit.per_kwh')) + ')</th>' +
            '<th>' + escapeHtml(t('tariffsetting.col.nonsummer')) + ' (' + escapeHtml(t('tariffsetting.unit.per_kwh')) + ')</th>' +
            (custom ? '<th class="ts-col-actions">' + escapeHtml(t('tariffsetting.col.actions')) + '</th>' : '') +
            '</tr></thead><tbody>' + rows + '</tbody></table></div>' + addBtn);
    }

    // 基本電費表
    function renderBaseFees(p) {
        var rows = p.baseFees.map(function (fee, i) {
            return '<tr>' +
                '<td>' + escapeHtml(t('tariff.basefee.' + fee.szKey)) + '</td>' +
                '<td class="text-muted">' + escapeHtml(t('tariff.unit.' + fee.szUnit)) + '</td>' +
                '<td>' + (fee.dSummer == null
                    ? '<span class="text-muted">' + escapeHtml(t('tariffsetting.na')) + '</span>'
                    : priceInput(fee.dSummer, 'data-fee="' + i + '" data-field="summer"')) + '</td>' +
                '<td>' + (fee.dNonSummer == null
                    ? '<span class="text-muted">' + escapeHtml(t('tariffsetting.na')) + '</span>'
                    : priceInput(fee.dNonSummer, 'data-fee="' + i + '" data-field="nonsummer"')) + '</td>' +
                '</tr>';
        }).join('');

        return sectionCard('tariffsetting.section.base_fees',
            '<div class="table-responsive"><table class="table table-sm table-bordered align-middle mb-0 ts-table">' +
            '<thead class="table-light"><tr>' +
            '<th>' + escapeHtml(t('tariffsetting.col.base_item')) + '</th>' +
            '<th>' + escapeHtml(t('tariffsetting.col.base_unit')) + '</th>' +
            '<th>' + escapeHtml(t('tariffsetting.col.summer')) + '</th>' +
            '<th>' + escapeHtml(t('tariffsetting.col.nonsummer')) + '</th>' +
            '</tr></thead><tbody>' + rows + '</tbody></table></div>');
    }

    // 單一費率（低壓非時間電價）
    function renderFlatRate(p) {
        return sectionCard('tariffsetting.section.flat',
            '<div class="row g-3">' +
            '<div class="col-auto"><label class="form-label small mb-1">' + escapeHtml(t('tariffsetting.col.summer')) + ' (' + escapeHtml(t('tariffsetting.unit.per_kwh')) + ')</label>' +
            priceInput(p.flatRate.dSummer, 'id="tsFlatSummer"') + '</div>' +
            '<div class="col-auto"><label class="form-label small mb-1">' + escapeHtml(t('tariffsetting.col.nonsummer')) + ' (' + escapeHtml(t('tariffsetting.unit.per_kwh')) + ')</label>' +
            priceInput(p.flatRate.dNonSummer, 'id="tsFlatNonsummer"') + '</div>' +
            '</div>');
    }

    // 流動電費時段（tou）— 依日別分區塊，列 = 季節 × 時段別
    function renderFlowRates(p) {
        var html = '';
        DAY_TYPES.forEach(function (dayType) {
            var idxRows = [];
            p.flowRates.forEach(function (r, i) {
                if (r.szDayType === dayType) idxRows.push({ r: r, i: i });
            });
            if (idxRows.length === 0) return;
            idxRows.sort(function (a, b) {
                var s = SEASONS.indexOf(a.r.szSeason) - SEASONS.indexOf(b.r.szSeason);
                return s !== 0 ? s : (PERIOD_ORDER[a.r.szPeriod] || 9) - (PERIOD_ORDER[b.r.szPeriod] || 9);
            });

            var rows = idxRows.map(function (x) {
                var r = x.r, i = x.i;
                var seasonBadge = r.szSeason === 'summer'
                    ? '<span class="badge bg-warning text-dark">' + escapeHtml(t('tariffsetting.season.summer')) + '</span>'
                    : '<span class="badge bg-info text-dark">' + escapeHtml(t('tariffsetting.season.nonsummer')) + '</span>';
                var defaultName = t('tariff.period.' + r.szPeriod);
                var ranges = r.ranges.map(function (range, j) {
                    var parts = range.split('-');
                    var start = parts[0] || '00:00';
                    var end = parts[1] === '24:00' ? '00:00' : (parts[1] || '00:00');
                    return '<span class="ts-range-group">' +
                        '<input type="text" class="form-control form-control-sm ts-time" autocomplete="off" value="' + escapeHtml(start) + '" data-flow="' + i + '" data-range="' + j + '" data-part="start">' +
                        '<span class="mx-1">-</span>' +
                        '<input type="text" class="form-control form-control-sm ts-time" autocomplete="off" value="' + escapeHtml(end) + '" data-flow="' + i + '" data-range="' + j + '" data-part="end">' +
                        (r.ranges.length > 1
                            ? '<button type="button" class="btn btn-outline-danger btn-sm ts-range-del" title="' + escapeHtml(t('tariffsetting.button.del_range')) + '" onclick="window._ts.removeRange(' + i + ',' + j + ')"><i class="fas fa-times"></i></button>'
                            : '') +
                        '</span>';
                }).join('');
                ranges += '<button type="button" class="btn btn-outline-secondary btn-sm ts-range-add" onclick="window._ts.addRange(' + i + ')">' +
                    '<i class="fas fa-plus me-1"></i>' + escapeHtml(t('tariffsetting.button.add_range')) + '</button>';

                return '<tr>' +
                    '<td class="text-nowrap">' + seasonBadge + '</td>' +
                    '<td><input type="text" class="form-control form-control-sm ts-name" maxlength="20" value="' + escapeHtml(r.szName || '') + '" placeholder="' + escapeHtml(defaultName) + '" data-flow="' + i + '" data-field="name"></td>' +
                    '<td class="ts-ranges-cell">' + ranges + '</td>' +
                    '<td>' + priceInput(r.dPrice, 'data-flow="' + i + '" data-field="price"') + '</td>' +
                    '</tr>';
            }).join('');

            html += '<div class="ts-daytype-block mb-3">' +
                '<div class="ts-daytype-title"><i class="fas fa-calendar-day me-1"></i>' + escapeHtml(t('tariffsetting.daytype.' + dayType)) + '</div>' +
                '<div class="table-responsive"><table class="table table-sm table-bordered align-middle mb-0 ts-table">' +
                '<thead class="table-light"><tr>' +
                '<th class="ts-col-season">' + escapeHtml(t('tariffsetting.col.season')) + '</th>' +
                '<th class="ts-col-name">' + escapeHtml(t('tariffsetting.col.period_name')) + '</th>' +
                '<th>' + escapeHtml(t('tariffsetting.col.time_ranges')) + '</th>' +
                '<th class="ts-col-price">' + escapeHtml(t('tariffsetting.col.price')) + '</th>' +
                '</tr></thead><tbody>' + rows + '</tbody></table></div></div>';
        });

        return sectionCard('tariffsetting.section.flow',
            '<div class="text-muted small mb-2"><i class="fas fa-lightbulb me-1"></i>' + escapeHtml(t('tariffsetting.hint.midnight')) + '</div>' + html);
    }

    // 超額加價（簡易型）
    function renderSurcharge(p) {
        return sectionCard('tariffsetting.section.surcharge',
            '<div class="row g-3 align-items-end">' +
            '<div class="col-auto"><label class="form-label small mb-1">' + escapeHtml(t('tariffsetting.surcharge.over')) + '</label>' +
            '<input type="number" class="form-control form-control-sm ts-price" step="1" min="1" value="' + p.surcharge.nOverKwh + '" id="tsSurOver"></div>' +
            '<div class="col-auto"><label class="form-label small mb-1">' + escapeHtml(t('tariffsetting.surcharge.price')) + '</label>' +
            priceInput(p.surcharge.dPrice, 'id="tsSurPrice"') + '</div>' +
            '</div>');
    }

    function sectionCard(titleKey, bodyHtml) {
        return '<div class="ts-section mb-3">' +
            '<div class="ts-section-title">' + escapeHtml(t(titleKey)) + '</div>' +
            bodyHtml + '</div>';
    }

    // ── DOM → g_plan 回填 ────────────────────────────────

    function collect() {
        var p = g_plan;
        if (!p) return null;
        if (isCustom(p)) {
            p.szName = val('tsPlanName').trim();
            var typeSel = document.getElementById('tsPlanType');
            if (typeSel) p.szType = typeSel.value;
        }
        p.szSummerStart = pad2(val('tsSummerStartM')) + '-' + pad2(val('tsSummerStartD'));
        p.szSummerEnd = pad2(val('tsSummerEndM')) + '-' + pad2(val('tsSummerEndD'));

        document.querySelectorAll('input[data-tier]').forEach(function (el) {
            var tier = p.tiers[parseInt(el.getAttribute('data-tier'), 10)];
            var field = el.getAttribute('data-field');
            if (field === 'to') tier.nTo = intOrNull(el.value);
            else if (field === 'summer') tier.dSummer = numOr0(el.value);
            else tier.dNonSummer = numOr0(el.value);
        });
        // 級距下限自動接續上一級上限 +1
        for (var i = 1; i < p.tiers.length; i++) {
            if (p.tiers[i - 1].nTo != null) p.tiers[i].nFrom = p.tiers[i - 1].nTo + 1;
        }

        if (p.flatRate) {
            p.flatRate.dSummer = numOr0(val('tsFlatSummer'));
            p.flatRate.dNonSummer = numOr0(val('tsFlatNonsummer'));
        }

        document.querySelectorAll('input[data-fee]').forEach(function (el) {
            var fee = p.baseFees[parseInt(el.getAttribute('data-fee'), 10)];
            if (el.getAttribute('data-field') === 'summer') fee.dSummer = numOr0(el.value);
            else fee.dNonSummer = numOr0(el.value);
        });

        document.querySelectorAll('input[data-flow]').forEach(function (el) {
            var rate = p.flowRates[parseInt(el.getAttribute('data-flow'), 10)];
            var field = el.getAttribute('data-field');
            if (field === 'name') rate.szName = el.value.trim() || null;
            else if (field === 'price') rate.dPrice = numOr0(el.value);
            else {
                var j = parseInt(el.getAttribute('data-range'), 10);
                var parts = (rate.ranges[j] || '00:00-24:00').split('-');
                var v = el.value.trim() || '00:00';
                if (el.getAttribute('data-part') === 'start') parts[0] = v;
                else parts[1] = (v === '00:00') ? '24:00' : v;   // 訖時 00:00 = 當日結束 24:00
                rate.ranges[j] = parts[0] + '-' + parts[1];
            }
        });

        if (p.surcharge) {
            p.surcharge.nOverKwh = parseInt(val('tsSurOver'), 10) || 0;
            p.surcharge.dPrice = numOr0(val('tsSurPrice'));
        }
        return p;
    }

    function val(id) { var el = document.getElementById(id); return el ? el.value : ''; }
    function pad2(n) { n = parseInt(n, 10) || 0; return n < 10 ? '0' + n : String(n); }
    function intOrNull(v) { var n = parseInt(v, 10); return isNaN(n) ? null : n; }
    function numOr0(v) { var n = parseFloat(v); return isNaN(n) ? 0 : n; }

    // ── 前端驗證（與後端同規則） ──────────────────────────

    function validate(p) {
        if (isCustom(p) && !p.szName) return t('tariffsetting.err.name_empty');
        if (p.szType === 'progressive') return validateTiers(p.tiers);
        if (p.szType === 'tou') return validateFlow(p.flowRates);
        if (p.szType === 'flat') {
            if (!p.flatRate) return t('tariffsetting.err.flat_missing');
            if (p.flatRate.dSummer < 0 || p.flatRate.dNonSummer < 0) return t('tariffsetting.err.price_negative');
        }
        return null;
    }

    function validateTiers(tiers) {
        for (var i = 0; i < tiers.length; i++) {
            var isLast = i === tiers.length - 1;
            if (!isLast) {
                if (tiers[i].nTo == null || tiers[i].nTo <= tiers[i].nFrom)
                    return t('tariffsetting.err.tier_order');
            }
            if (tiers[i].dSummer < 0 || tiers[i].dNonSummer < 0)
                return t('tariffsetting.err.price_negative');
        }
        return null;
    }

    function validateFlow(flowRates) {
        for (var d = 0; d < DAY_TYPES.length; d++) {
            for (var s = 0; s < SEASONS.length; s++) {
                var group = flowRates.filter(function (r) {
                    return r.szDayType === DAY_TYPES[d] && r.szSeason === SEASONS[s];
                });
                if (group.length === 0) continue;   // seed 結構固定，缺組交後端擋
                var where = t('tariffsetting.daytype.' + DAY_TYPES[d]) + ' × ' + t('tariffsetting.season.' + SEASONS[s]);
                var err = checkCoverage(group, where);
                if (err) return err;
            }
        }
        return null;
    }

    function checkCoverage(group, where) {
        var intervals = [];
        for (var i = 0; i < group.length; i++) {
            for (var j = 0; j < group[i].ranges.length; j++) {
                var parts = group[i].ranges[j].split('-');
                var start = toMin(parts[0]), end = toMin(parts[1]);
                if (start == null || end == null || start === end)
                    return t('tariffsetting.err.range_format', { 0: where });
                if (start < end) intervals.push([start, end]);
                else { intervals.push([start, 1440]); if (end > 0) intervals.push([0, end]); }
            }
        }
        intervals.sort(function (a, b) { return a[0] - b[0]; });
        var cursor = 0;
        for (var k = 0; k < intervals.length; k++) {
            if (intervals[k][0] < cursor) return t('tariffsetting.err.group_overlap', { 0: where, 1: toHHmm(intervals[k][0]) });
            if (intervals[k][0] > cursor) return t('tariffsetting.err.group_gap', { 0: where, 1: toHHmm(cursor) + '~' + toHHmm(intervals[k][0]) });
            cursor = intervals[k][1];
        }
        if (cursor !== 1440) return t('tariffsetting.err.group_gap', { 0: where, 1: toHHmm(cursor) + '~24:00' });
        return null;
    }

    function toMin(s) {
        if (!s) return null;
        var m = /^(\d{1,2}):(\d{2})$/.exec(s.trim());
        if (!m) return null;
        var h = parseInt(m[1], 10), mi = parseInt(m[2], 10);
        if (h === 24 && mi === 0) return 1440;
        if (h > 23 || mi > 59) return null;
        return h * 60 + mi;
    }

    function toHHmm(min) { return pad2(Math.floor(min / 60)) + ':' + pad2(min % 60); }

    // ── 自建方案 CRUD ────────────────────────────────────

    // 型態骨架 — 從空白建立 / 換型態時填入該型態的最小合法結構（後端驗證會擋不合法者）
    function applyTypeSkeleton(p, szType) {
        p.szType = szType;
        p.szNoteKey = null;
        p.surcharge = null;
        if (szType === 'progressive') {
            p.tiers = [
                { nFrom: 1, nTo: 120, dSummer: 0, dNonSummer: 0 },
                { nFrom: 121, nTo: null, dSummer: 0, dNonSummer: 0 }
            ];
            p.flatRate = null;
            p.flowRates = [];
            p.baseFees = [];
        } else if (szType === 'flat') {
            p.tiers = [];
            p.flatRate = { dSummer: 0, dNonSummer: 0 };
            p.flowRates = [];
        } else {
            // tou：每（日別 × 季節）給一列覆蓋 24h 的單一時段；細分時段建議用「複製方案」從台電方案改
            p.tiers = [];
            p.flatRate = null;
            p.flowRates = [];
            DAY_TYPES.forEach(function (dayType) {
                SEASONS.forEach(function (season) {
                    p.flowRates.push({
                        szDayType: dayType, szSeason: season, szPeriod: 'peak',
                        szName: null, ranges: ['00:00-24:00'], dPrice: 0
                    });
                });
            });
        }
        return p;
    }

    function newPlanId() {
        return 'custom_' + Date.now();
    }

    // 切到自訂類別並選中方案（新增/複製共用）
    function adoptNewPlan(plan) {
        g_config.plans.push(plan);
        g_newPlanIds.push(plan.szPlanId);
        document.getElementById('tsCategory').value = CUSTOM;
        fillPlanSelect(CUSTOM);
        document.getElementById('tsPlan').value = plan.szPlanId;
        g_plan = JSON.parse(JSON.stringify(plan));
        g_dirty = true;
        renderPlan();
        renderAdoptions();   // 時間軸下拉要看得到新方案
    }

    function addPlan() {
        if (!confirmDiscardDirty()) return;
        var plan = applyTypeSkeleton({
            szPlanId: newPlanId(),
            szName: t('tariffsetting.plan.new_name'),
            szCategory: CUSTOM,
            szSummerStart: '06-01',
            szSummerEnd: '09-30',
            szNoteKey: null,
            tiers: [], flatRate: null, baseFees: [], flowRates: [], surcharge: null
        }, 'flat');
        adoptNewPlan(plan);
    }

    function copyPlan() {
        if (!g_plan) return;
        if (!confirmDiscardDirty()) return;
        var src = findPlan(g_plan.szPlanId) || g_plan;
        var copy = JSON.parse(JSON.stringify(src));
        copy.szPlanId = newPlanId();
        copy.szCategory = CUSTOM;
        copy.szNoteKey = null;   // 台電方案備註（如「指定 30 日尖峰」）不適用於自建方案
        copy.szName = t('tariffsetting.plan.copy_name', { 0: planName(src) });
        adoptNewPlan(copy);
    }

    async function deletePlan() {
        if (!isCustom(g_plan)) { alert(t('tariffsetting.msg.seed_undeletable')); return; }
        var szPlanId = g_plan.szPlanId;
        var used = (g_config.adoptions || []).filter(function (a) { return a.szPlanId === szPlanId; });
        if (used.length > 0) { alert(t('tariffsetting.msg.plan_in_use')); return; }
        if (!confirm(t('tariffsetting.confirm.del_plan', { 0: planName(g_plan) }))) return;

        // 尚未存到伺服器的新方案只需本機移除
        var isLocalOnly = g_newPlanIds.indexOf(szPlanId) >= 0;
        if (!isLocalOnly) {
            try {
                var res = await fetch('/TariffSetting/api/plan/' + encodeURIComponent(szPlanId), { method: 'DELETE' });
                if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || res.statusText);
            } catch (e) {
                alert(t('tariffsetting.msg.del_plan_fail', { 0: e.message }));
                return;
            }
        }
        g_config.plans = g_config.plans.filter(function (x) { return x.szPlanId !== szPlanId; });
        g_newPlanIds = g_newPlanIds.filter(function (x) { return x !== szPlanId; });
        g_dirty = false;

        fillPlanSelect(CUSTOM);
        var next = document.getElementById('tsPlan').value;
        if (next) selectPlan(next);
        else renderEmptyCategory();
        renderAdoptions();
    }

    function onTypeChange(szType) {
        collect();
        applyTypeSkeleton(g_plan, szType);
        g_dirty = true;
        renderPlan();
    }

    // 累進級距增刪（自建方案限定）
    function addTier() {
        var p = collect();
        if (!p) return;
        var last = p.tiers[p.tiers.length - 1];
        if (last) {
            last.nTo = last.nFrom + 99;
            p.tiers.push({ nFrom: last.nTo + 1, nTo: null, dSummer: last.dSummer, dNonSummer: last.dNonSummer });
        } else {
            p.tiers.push({ nFrom: 1, nTo: null, dSummer: 0, dNonSummer: 0 });
        }
        g_dirty = true;
        renderPlan();
    }

    function removeTier(idx) {
        var p = collect();
        if (!p || p.tiers.length <= 2) return;
        p.tiers.splice(idx, 1);
        rechainTiers(p.tiers);
        g_dirty = true;
        renderPlan();
    }

    // 級距鏈重算：後續 nFrom = 上一級 nTo+1、最後一級 nTo=null
    function rechainTiers(tiers) {
        for (var i = 0; i < tiers.length; i++) {
            if (i > 0) tiers[i].nFrom = (tiers[i - 1].nTo || 0) + 1;
            if (i === tiers.length - 1) tiers[i].nTo = null;
        }
    }

    // ── 採用時間軸 ───────────────────────────────────────

    function sortAdoptions() {
        g_config.adoptions.sort(function (a, b) {
            return (a.szEffectiveDate || '').localeCompare(b.szEffectiveDate || '');
        });
    }

    function renderAdoptions() {
        var list = document.getElementById('tsAdoptionList');
        if (!list) return;
        var adoptions = g_config.adoptions || [];
        if (adoptions.length === 0) {
            list.innerHTML = '<div class="text-muted small py-2">' + escapeHtml(t('tariffsetting.adoption.empty')) + '</div>';
            return;
        }

        var planOpts = g_config.plans.map(function (p) {
            return { id: p.szPlanId, label: planLabel(p) };
        });

        var rows = adoptions.map(function (a, i) {
            var opts = planOpts.map(function (o) {
                return '<option value="' + escapeHtml(o.id) + '"' + (o.id === a.szPlanId ? ' selected' : '') + '>' +
                    escapeHtml(o.label) + '</option>';
            }).join('');
            return '<tr>' +
                '<td><input type="date" class="form-control form-control-sm ts-adopt-date" value="' +
                escapeHtml(a.szEffectiveDate || '') + '" data-adopt="' + i + '" data-field="date"></td>' +
                '<td><select class="form-select form-select-sm" data-adopt="' + i + '" data-field="plan">' + opts + '</select></td>' +
                '<td class="text-center"><button type="button" class="btn btn-outline-danger btn-sm" title="' +
                escapeHtml(t('tariffsetting.adoption.del')) + '" onclick="window._ts.removeAdoption(' + i + ')">' +
                '<i class="fas fa-times"></i></button></td>' +
                '</tr>';
        }).join('');

        list.innerHTML = '<div class="table-responsive"><table class="table table-sm table-bordered align-middle mb-0 ts-table ts-adopt-table">' +
            '<thead class="table-light"><tr>' +
            '<th class="ts-col-date">' + escapeHtml(t('tariffsetting.adoption.effective_date')) + '</th>' +
            '<th>' + escapeHtml(t('tariffsetting.adoption.plan')) + '</th>' +
            '<th class="ts-col-actions">' + escapeHtml(t('tariffsetting.col.actions')) + '</th>' +
            '</tr></thead><tbody>' + rows + '</tbody></table></div>';
    }

    // DOM → g_config.adoptions 回填
    function collectAdoptions() {
        document.querySelectorAll('#tsAdoptionList [data-adopt]').forEach(function (el) {
            var a = g_config.adoptions[parseInt(el.getAttribute('data-adopt'), 10)];
            if (!a) return;
            if (el.getAttribute('data-field') === 'date') a.szEffectiveDate = el.value;
            else a.szPlanId = el.value;
        });
    }

    function addAdoption() {
        collectAdoptions();
        if (g_config.plans.length === 0) return;
        var current = g_plan ? g_plan.szPlanId : g_config.plans[0].szPlanId;
        g_config.adoptions.push({ szEffectiveDate: dateStr(new Date()), szPlanId: current });
        sortAdoptions();
        renderAdoptions();
    }

    function removeAdoption(idx) {
        collectAdoptions();
        g_config.adoptions.splice(idx, 1);
        renderAdoptions();
    }

    async function saveAdoptions() {
        collectAdoptions();
        var err = validateAdoptions();
        if (err) { alert(err); return; }
        sortAdoptions();

        try {
            var res = await fetch('/TariffSetting/api/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ adoptions: g_config.adoptions, plans: g_config.plans })
            });
            if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || res.statusText);
            g_newPlanIds = [];   // 整份存回後所有方案都已落地
            // szActivePlanId 由後端依今日重算 → 重讀整份設定同步顯示
            var cfgRes = await fetch('/TariffSetting/api/config');
            if (cfgRes.ok) {
                var fresh = await cfgRes.json();
                g_config.szActivePlanId = fresh.szActivePlanId;
            }
            renderAdoptions();
            fillPlanSelect(document.getElementById('tsCategory').value);
            if (g_plan) document.getElementById('tsPlan').value = g_plan.szPlanId;
            updateActiveCard();
            loadCostSummary();
            alert(t('tariffsetting.msg.saved'));
        } catch (e) {
            alert(t('tariffsetting.msg.save_fail', { 0: e.message }));
        }
    }

    // 前端驗證（與後端 ValidateAdoptions 同規則）
    function validateAdoptions() {
        var seen = {};
        for (var i = 0; i < g_config.adoptions.length; i++) {
            var a = g_config.adoptions[i];
            if (!/^\d{4}-\d{2}-\d{2}$/.test(a.szEffectiveDate || ''))
                return t('tariffsetting.err.adopt_date_format');
            if (!a.szPlanId || !findPlan(a.szPlanId))
                return t('tariffsetting.err.adopt_plan_missing', { 0: a.szEffectiveDate });
            if (seen[a.szEffectiveDate])
                return t('tariffsetting.err.adopt_date_dup', { 0: a.szEffectiveDate });
            seen[a.szEffectiveDate] = true;
        }
        return null;
    }

    // ── 契約容量歷史 ─────────────────────────────────────

    function sortContracts() {
        g_config.contracts.sort(function (a, b) {
            return (a.szEffectiveDate || '').localeCompare(b.szEffectiveDate || '');
        });
    }

    function renderContracts() {
        var list = document.getElementById('tsContractList');
        if (!list) return;
        var contracts = g_config.contracts || [];
        if (contracts.length === 0) {
            list.innerHTML = '<div class="text-muted small py-2">' + escapeHtml(t('tariffsetting.contract.empty')) + '</div>';
            return;
        }

        var rows = contracts.map(function (c, i) {
            var kw = c.kw || {};
            var cells = CONTRACT_KEYS.map(function (key) {
                var v = kw[key];
                return '<td><input type="number" min="0" step="1" class="form-control form-control-sm text-end ts-contract-kw" value="' +
                    (v != null && v !== 0 ? escapeHtml(v) : '') + '" data-contract="' + i + '" data-field="' + key + '"></td>';
            }).join('');
            return '<tr>' +
                '<td><input type="date" class="form-control form-control-sm ts-adopt-date" value="' +
                escapeHtml(c.szEffectiveDate || '') + '" data-contract="' + i + '" data-field="date"></td>' +
                cells +
                '<td class="text-center"><button type="button" class="btn btn-outline-danger btn-sm" title="' +
                escapeHtml(t('tariffsetting.contract.del')) + '" onclick="window._ts.removeContract(' + i + ')">' +
                '<i class="fas fa-times"></i></button></td>' +
                '</tr>';
        }).join('');

        var heads = CONTRACT_KEYS.map(function (key) {
            return '<th class="text-end">' + escapeHtml(t('tariff.basefee.' + key)) + ' (kW)</th>';
        }).join('');
        list.innerHTML = '<div class="table-responsive"><table class="table table-sm table-bordered align-middle mb-0 ts-table ts-contract-table">' +
            '<thead class="table-light"><tr>' +
            '<th class="ts-col-date">' + escapeHtml(t('tariffsetting.contract.effective_date')) + '</th>' + heads +
            '<th class="ts-col-actions">' + escapeHtml(t('tariffsetting.col.actions')) + '</th>' +
            '</tr></thead><tbody>' + rows + '</tbody></table></div>';
    }

    // DOM → g_config.contracts 回填（空白 kW 視為未訂，不送 key）
    function collectContracts() {
        document.querySelectorAll('#tsContractList [data-contract]').forEach(function (el) {
            var c = g_config.contracts[parseInt(el.getAttribute('data-contract'), 10)];
            if (!c) return;
            var field = el.getAttribute('data-field');
            if (field === 'date') { c.szEffectiveDate = el.value; return; }
            if (!c.kw) c.kw = {};
            var raw = String(el.value).trim();
            if (raw === '') delete c.kw[field];
            else c.kw[field] = Number(raw);
        });
    }

    function addContract() {
        collectContracts();
        // 預設複製最後一筆的 kW（調整契約通常只改一兩個契約別）
        var last = g_config.contracts.length ? g_config.contracts[g_config.contracts.length - 1] : null;
        g_config.contracts.push({
            szEffectiveDate: dateStr(new Date()),
            kw: last ? JSON.parse(JSON.stringify(last.kw || {})) : {}
        });
        sortContracts();
        renderContracts();
    }

    function removeContract(idx) {
        collectContracts();
        g_config.contracts.splice(idx, 1);
        renderContracts();
    }

    // 前端驗證（與後端 ValidateContracts 同規則）
    function validateContracts() {
        var seen = {};
        for (var i = 0; i < g_config.contracts.length; i++) {
            var c = g_config.contracts[i];
            if (!/^\d{4}-\d{2}-\d{2}$/.test(c.szEffectiveDate || ''))
                return t('tariffsetting.err.contract_date_format');
            if (seen[c.szEffectiveDate])
                return t('tariffsetting.err.contract_date_dup', { 0: c.szEffectiveDate });
            seen[c.szEffectiveDate] = true;
            var hasAny = false;
            for (var key in (c.kw || {})) {
                var v = c.kw[key];
                if (typeof v !== 'number' || isNaN(v) || v < 0)
                    return t('tariffsetting.err.contract_kw_invalid', { 0: c.szEffectiveDate });
                if (v > 0) hasAny = true;
            }
            if (!hasAny) return t('tariffsetting.err.contract_kw_empty', { 0: c.szEffectiveDate });
        }
        return null;
    }

    async function saveContracts() {
        collectContracts();
        var err = validateContracts();
        if (err) { alert(err); return; }
        sortContracts();

        try {
            var res = await fetch('/TariffSetting/api/contracts', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ contracts: g_config.contracts })
            });
            if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || res.statusText);
            renderContracts();
            alert(t('tariffsetting.msg.saved'));
        } catch (e) {
            alert(t('tariffsetting.msg.save_fail', { 0: e.message }));
        }
    }

    // ── 動作 ─────────────────────────────────────────────

    async function savePlan() {
        var p = collect();
        if (!p) return;
        var err = validate(p);
        if (err) { alert(err); return; }

        try {
            var res = await fetch('/TariffSetting/api/plan', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(p)
            });
            if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || res.statusText);
            // 寫回本地整份設定
            var idx = g_config.plans.findIndex(function (x) { return x.szPlanId === p.szPlanId; });
            if (idx >= 0) g_config.plans[idx] = JSON.parse(JSON.stringify(p));
            else g_config.plans.push(JSON.parse(JSON.stringify(p)));
            g_newPlanIds = g_newPlanIds.filter(function (x) { return x !== p.szPlanId; });
            g_dirty = false;
            fillPlanSelect(document.getElementById('tsCategory').value);
            document.getElementById('tsPlan').value = p.szPlanId;
            document.getElementById('tsPlanTitle').textContent = planLabel(p);
            renderAdoptions();
            alert(t('tariffsetting.msg.saved'));
        } catch (e) {
            alert(t('tariffsetting.msg.save_fail', { 0: e.message }));
        }
    }

    // 設為採用方案 = 在時間軸補一筆「今日起採用」（後端同語意）
    async function setActive() {
        if (!g_plan) return;
        if (g_newPlanIds.indexOf(g_plan.szPlanId) >= 0) { alert(t('tariffsetting.msg.save_plan_first')); return; }
        try {
            var res = await fetch('/TariffSetting/api/active', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ planId: g_plan.szPlanId })
            });
            if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || res.statusText);
            var szToday = dateStr(new Date());
            g_config.adoptions = (g_config.adoptions || []).filter(function (a) { return a.szEffectiveDate !== szToday; });
            g_config.adoptions.push({ szEffectiveDate: szToday, szPlanId: g_plan.szPlanId });
            sortAdoptions();
            g_config.szActivePlanId = g_plan.szPlanId;
            fillPlanSelect(g_plan.szCategory);
            document.getElementById('tsPlan').value = g_plan.szPlanId;
            updateActiveCard();
            renderAdoptions();
        } catch (e) {
            alert(t('tariffsetting.msg.active_fail', { 0: e.message }));
        }
    }

    async function resetPlan() {
        if (!g_plan) return;
        if (isCustom(g_plan)) { alert(t('tariffsetting.msg.custom_no_reset')); return; }
        if (!confirm(t('tariffsetting.confirm.reset', { 0: planName(g_plan) }))) return;
        try {
            var res = await fetch('/TariffSetting/api/reset', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ planId: g_plan.szPlanId })
            });
            if (!res.ok) throw new Error((await res.json().catch(function () { return {}; })).message || res.statusText);
            var restored = await res.json();
            var idx = g_config.plans.findIndex(function (x) { return x.szPlanId === restored.szPlanId; });
            if (idx >= 0) g_config.plans[idx] = restored;
            g_plan = JSON.parse(JSON.stringify(restored));
            g_dirty = false;
            renderPlan();
        } catch (e) {
            alert(t('tariffsetting.msg.reset_fail', { 0: e.message }));
        }
    }

    // ── 重新計算電費 ─────────────────────────────────────

    function pad2n(n) { return n < 10 ? '0' + n : String(n); }

    function dateStr(d) {
        return d.getFullYear() + '-' + pad2n(d.getMonth() + 1) + '-' + pad2n(d.getDate());
    }

    function hasAdoptions() {
        return !!g_config && !!g_config.adoptions && g_config.adoptions.length > 0;
    }

    /// 最早生效日（全量重算起點）
    function earliestAdoptionDate() {
        var dates = g_config.adoptions
            .map(function (a) { return a.szEffectiveDate; })
            .filter(function (d) { return /^\d{4}-\d{2}-\d{2}$/.test(d || ''); })
            .sort();
        return dates.length > 0 ? dates[0] : null;
    }

    function openRecalc() {
        if (!hasAdoptions()) {
            alert(t('tariffsetting.recalc.no_active'));
            return;
        }
        // 預設區間：近 7 天（含今日）
        var end = new Date();
        var start = new Date();
        start.setDate(start.getDate() - 6);
        document.getElementById('tsRecalcStart').value = dateStr(start);
        document.getElementById('tsRecalcEnd').value = dateStr(end);
        document.getElementById('tsRecalcFull').checked = false;
        toggleFullRecalc();
        document.getElementById('tsRecalcResult').innerHTML = '';
        new bootstrap.Modal(document.getElementById('tsRecalcModal')).show();
    }

    // 全量重算 = [最早生效日, 今天]，前端切段逐段打既有 API（後端不做狀態機）
    function toggleFullRecalc() {
        var isFull = document.getElementById('tsRecalcFull').checked;
        document.getElementById('tsRecalcStart').disabled = isFull;
        document.getElementById('tsRecalcEnd').disabled = isFull;
        document.getElementById('tsRecalcFullHint').classList.toggle('d-none', !isFull);
        if (isFull) {
            var szEarliest = earliestAdoptionDate();
            if (szEarliest) {
                document.getElementById('tsRecalcStart').value = szEarliest;
                document.getElementById('tsRecalcEnd').value = dateStr(new Date());
            }
        }
    }

    // [start, end]（含頭含尾）切為每段 <= SEGMENT_DAYS 天
    function buildSegments(szStart, szEnd) {
        var segments = [];
        var dtCursor = new Date(szStart + 'T00:00:00');
        var dtEnd = new Date(szEnd + 'T00:00:00');
        while (dtCursor <= dtEnd) {
            var dtSegEnd = new Date(dtCursor.getTime());
            dtSegEnd.setDate(dtSegEnd.getDate() + SEGMENT_DAYS - 1);
            if (dtSegEnd > dtEnd) dtSegEnd = dtEnd;
            segments.push({ start: dateStr(dtCursor), end: dateStr(dtSegEnd) });
            dtCursor = new Date(dtSegEnd.getTime());
            dtCursor.setDate(dtCursor.getDate() + 1);
        }
        return segments;
    }

    async function recalcSegment(szStart, szEnd) {
        var res = await fetch('/TariffSetting/api/recalculate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ start: szStart, end: szEnd })
        });
        var data = await res.json().catch(function () { return {}; });
        if (!res.ok) {
            var key = data.errorCode === 'no_active_plan' ? 'tariffsetting.recalc.no_active'
                : data.errorCode === 'range_too_large' ? 'tariffsetting.recalc.err_too_large'
                : data.errorCode === 'invalid_range' ? 'tariffsetting.recalc.err_range'
                : null;
            throw new Error(key ? t(key) : (data.errorCode || res.statusText));
        }
        return data;
    }

    async function runRecalc() {
        var isFull = document.getElementById('tsRecalcFull').checked;
        var start = document.getElementById('tsRecalcStart').value;
        var end = document.getElementById('tsRecalcEnd').value;
        var resultEl = document.getElementById('tsRecalcResult');

        if (isFull && !hasAdoptions()) {
            resultEl.innerHTML = '<span class="text-danger">' + escapeHtml(t('tariffsetting.recalc.no_active')) + '</span>';
            return;
        }
        if (!start || !end || start > end) {
            resultEl.innerHTML = '<span class="text-danger">' + escapeHtml(t('tariffsetting.recalc.err_range')) + '</span>';
            return;
        }
        // 單段模式同步後端 366 天上限防呆（全量模式自動切段，不受此限）
        if (!isFull && (new Date(end) - new Date(start)) / 86400000 > 366) {
            resultEl.innerHTML = '<span class="text-danger">' + escapeHtml(t('tariffsetting.recalc.err_too_large')) + '</span>';
            return;
        }

        var segments = isFull ? buildSegments(start, end) : [{ start: start, end: end }];
        var btn = document.getElementById('btnTsRecalcRun');
        btn.disabled = true;
        var nHours = 0, nRows = 0;
        try {
            for (var i = 0; i < segments.length; i++) {
                resultEl.innerHTML = '<span class="text-muted"><i class="fas fa-spinner fa-spin me-1"></i>' +
                    escapeHtml(segments.length > 1
                        ? t('tariffsetting.recalc.progress', { 0: i + 1, 1: segments.length, 2: segments[i].start, 3: segments[i].end })
                        : t('tariffsetting.recalc.running')) + '</span>';
                var data = await recalcSegment(segments[i].start, segments[i].end);
                nHours += data.hours || 0;
                nRows += data.rows || 0;
            }
            resultEl.innerHTML = '<span class="text-success"><i class="fas fa-check-circle me-1"></i>' +
                escapeHtml(t('tariffsetting.recalc.done', { 0: start, 1: end, 2: nHours, 3: nRows })) + '</span>';
            loadCostSummary();   // 重算後刷新頂部累計卡片
        } catch (e) {
            resultEl.innerHTML = '<span class="text-danger">' +
                escapeHtml(t('tariffsetting.recalc.fail', { 0: e.message })) + '</span>';
        } finally {
            btn.disabled = false;
        }
    }

    // 時段區間增刪 — 先 collect 保留使用者已輸入內容再重繪
    function addRange(flowIdx) {
        collect();
        g_plan.flowRates[flowIdx].ranges.push('08:00-12:00');
        g_dirty = true;
        renderPlan();
    }

    function removeRange(flowIdx, rangeIdx) {
        collect();
        g_plan.flowRates[flowIdx].ranges.splice(rangeIdx, 1);
        g_dirty = true;
        renderPlan();
    }

    // ── 工具 ─────────────────────────────────────────────

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    window._ts = {
        savePlan: savePlan,
        setActive: setActive,
        resetPlan: resetPlan,
        addRange: addRange,
        removeRange: removeRange,
        addPlan: addPlan,
        copyPlan: copyPlan,
        deletePlan: deletePlan,
        addTier: addTier,
        removeTier: removeTier,
        addAdoption: addAdoption,
        removeAdoption: removeAdoption,
        saveAdoptions: saveAdoptions,
        addContract: addContract,
        removeContract: removeContract,
        saveContracts: saveContracts,
        openRecalc: openRecalc,
        toggleFullRecalc: toggleFullRecalc,
        runRecalc: runRecalc
    };
})();
