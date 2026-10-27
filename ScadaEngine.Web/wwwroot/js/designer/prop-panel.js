// ============================================================
// Designer — 屬性面板
// ============================================================
// 內容：renderPropPanel（533 行，包 9 種 widget 屬性 UI）、文字屬性面板、
// DI/AI 標籤同步、setProp/setPos/setSize、表格 cell 屬性面板、
// 重選點位後建立 widget 的 createXxxWithPoint、bg-transparent 切換。
// 依賴：state.js / widget-defs.js / widget-core.js / picker.js。
// 注意：本檔的 HTML template 內含大量 oninput="setProp(...)"、
// onclick="rerouteXxxPoint()" 字面 global 函式名，picker.js 內也會呼叫
// 本檔的 createXxxWithPoint — 循環依賴靠 global hoisting 解，
// 切勿改成 const xxx = function() {} 形式。
// ============================================================

// ============================================================
// 屬性面板
// ============================================================
function renderPropPanel(el) {
    const szType = el.dataset.type;
    const props  = el.widgetProps;

    // 文字 Widget 使用獨立屬性面板
    if (szType === 'text') {
        document.getElementById('propBody').innerHTML = buildTextPropHtml(el, props);
        // 選取文字元件時直接 focus 文字內容欄，便於立即修改（與表格儲存格選取一致）
        const ta = document.getElementById('txtContentInput');
        if (ta) { ta.focus(); ta.select(); }
        return;
    }

    // 共用欄位
    const szUnboundLabel = `<span style="color:#888;font-size:11px;">${escHtml(t('designer.widget.unbound'))}</span>`;
    // table 鎖定尺寸時，寬高改為 readonly 並顯示說明（plan 決策 4）
    // 折線管路大小由節點 bounding box 決定，寬高一律 readonly（plan 2026-07-23 決策 4）
    const bSizeLocked   = (szType === 'table' && props.bTableSizeLocked === true) || szType === 'pipe';
    const szSizeRO      = bSizeLocked ? 'readonly style="background:#f1f3f5;color:#888;"' : '';
    const szSizeOninput = bSizeLocked ? '' : `oninput="setSize('width', +this.value)"`;
    const szSizeOninputH= bSizeLocked ? '' : `oninput="setSize('height', +this.value)"`;
    const szSizeLockedHint = bSizeLocked
        ? `<div style="font-size:10px;color:#888;margin-top:-4px;margin-bottom:6px;">${escHtml(t(szType === 'pipe' ? 'designer.prop.pipe.size_hint' : 'designer.prop.table_size_locked'))}</div>`
        : '';
    let szHtml = `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.title'))}</label>
            <input type="text" value="${escHtml(props.szTitle)}"
                   oninput="setProp('szTitle', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pos_x'))}</label>
            <input type="number" id="pX" value="${parseInt(el.style.left)}" step="20"
                   oninput="setPos('left', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pos_y'))}</label>
            <input type="number" id="pY" value="${parseInt(el.style.top)}" step="20"
                   oninput="setPos('top', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.width'))}</label>
            <input type="number" id="pW" value="${el.offsetWidth}" step="1" ${szSizeRO} ${szSizeOninput}>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.height'))}</label>
            <input type="number" id="pH" value="${el.offsetHeight}" step="1" ${szSizeRO} ${szSizeOninputH}>
        </div>
        ${szSizeLockedHint}
        <hr class="prop-divider">
    `;

    if (szType === 'table') {
        // 預設底色樣式色塊（plan 2026-07-04）— 迷你預覽：表頭 + 奇/偶列
        const szPresetSwatches = Object.keys(TABLE_STYLE_PRESETS).map(k => {
            const p = TABLE_STYLE_PRESETS[k];
            return `<div class="table-preset-swatch" title="${escHtml(t('designer.preset.' + k))}"
                         onclick="applyTablePreset('${k}')">
                        <div style="height:7px;background:${p.szHeaderColor};"></div>
                        <div style="height:6px;background:${p.szBodyBgOdd};"></div>
                        <div style="height:6px;background:${p.szBodyBgEven};border-top:1px solid ${p.szBorderColor};"></div>
                    </div>`;
        }).join('');
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.rows'))}</label>
            <input type="number" value="${props.nRows}" min="1" max="20"
                   oninput="setProp('nRows', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.cols'))}</label>
            <input type="number" value="${props.nCols}" min="1"
                   oninput="setProp('nCols', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.default_row_height'))}</label>
            <input type="number" value="${props.nDefaultRowH ?? 20}" min="10" max="200"
                   oninput="setProp('nDefaultRowH', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.default_col_width'))}</label>
            <input type="number" value="${props.nDefaultColW ?? 80}" min="20" max="500"
                   oninput="setProp('nDefaultColW', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.table_style'))}</label>
            <div class="table-preset-row">${szPresetSwatches}</div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.header_color'))}</label>
            <input type="color" value="${props.szHeaderColor}"
                   oninput="setProp('szHeaderColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.body_bg_odd'))}</label>
            <input type="color" value="${props.szBodyBgOdd || '#ffffff'}"
                   oninput="setProp('szBodyBgOdd', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.body_bg_even'))}</label>
            <input type="color" value="${props.szBodyBgEven || '#f8f9fa'}"
                   oninput="setProp('szBodyBgEven', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.border_color'))}</label>
            <input type="color" value="${props.szBorderColor || '#f0f0f0'}"
                   oninput="setProp('szBorderColor', this.value)">
        </div>`;
    } else if (szType === 'gauge') {
        const szSidLabel = props.szPointName
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_point'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szSidLabel}
                <button class="btn btn-outline-warning btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteGaugePoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.current_value'))}</label>
            <input type="number" value="${props.fValue}" step="0.1"
                   oninput="setProp('fValue', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.min_value'))}</label>
            <input type="number" value="${props.fMin}"
                   oninput="setProp('fMin', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.max_value'))}</label>
            <input type="number" value="${props.fMax}"
                   oninput="setProp('fMax', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.unit_from_point'))}</label>
            <input type="text" value="${escHtml(props.szUnit)}" readonly
                   style="background:#f0f0f0;color:#666;cursor:not-allowed;">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.main_color_normal'))}</label>
            <input type="color" value="${props.szColor}"
                   oninput="setProp('szColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.high_color'))}</label>
            <input type="color" value="${props.szHighColor || '#dc3545'}"
                   oninput="setProp('szHighColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.low_color'))}</label>
            <input type="color" value="${props.szLowColor || '#fd7e14'}"
                   oninput="setProp('szLowColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onGaugeBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="gaugeBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>
        `;
    } else if (szType === 'trendChart') {
        const szSidLabel = props.szPointName
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;

        // 時間窗以「分鐘」呈現（現場講「看近 30 分鐘」而非 1800 秒），props 仍存秒
        const nWinSec  = props.nWindowSec || 1800;
        const nWinMin  = Math.max(1, Math.round(nWinSec / 60));
        const nSmpSec  = props.nSampleSec || 5;

        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_sid'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szSidLabel}
                <button class="btn btn-outline-primary btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteTrendChartPoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.window_min'))}</label>
            <input type="number" value="${nWinMin}" min="1" max="1440" step="1"
                   oninput="setProp('nWindowSec', Math.max(60, (+this.value || 1) * 60))">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.sample_sec'))}</label>
            <input type="number" value="${nSmpSec}" min="1" max="600" step="1"
                   oninput="setProp('nSampleSec', Math.max(1, +this.value || 1))">
        </div>
        <div id="trendPtHint" style="font-size:10px;margin:-2px 0 6px;">${_buildTrendPointHint(props)}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.y_max'))}</label>
            <input type="number" value="${props.fYMax != null ? props.fYMax : ''}" step="any"
                   placeholder="${escHtml(t('designer.prop.trend.auto'))}"
                   oninput="setProp('fYMax', this.value === '' ? null : +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.y_min'))}</label>
            <input type="number" value="${props.fYMin != null ? props.fYMin : ''}" step="any"
                   placeholder="${escHtml(t('designer.prop.trend.auto'))}"
                   oninput="setProp('fYMin', this.value === '' ? null : +this.value)">
        </div>
        <div style="font-size:10px;color:#888;margin:-2px 0 6px;">${escHtml(t('designer.prop.trend.y_auto_hint'))}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.grid_count'))}</label>
            <input type="number" value="${props.nGridCount != null ? props.nGridCount : 5}" min="0" max="10" step="1"
                   oninput="setProp('nGridCount', Math.max(0, Math.min(10, +this.value || 0)))">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.unit'))}</label>
            <input type="text" value="${escHtml(props.szUnit || '')}"
                   oninput="setProp('szUnit', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.line_color'))}</label>
            <input type="color" value="${props.szLineColor || '#0d6efd'}"
                   oninput="setProp('szLineColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.line_width'))}</label>
            <input type="number" value="${props.nLineWidth || 2}" min="1" max="6" step="1"
                   oninput="setProp('nLineWidth', Math.max(1, Math.min(6, +this.value || 1)))">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.grid_color'))}</label>
            <input type="color" value="${props.szGridColor || '#f0f0f0'}"
                   oninput="setProp('szGridColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.trend.border_color'))}</label>
            <input type="color" value="${props.szBorderColor || '#dee2e6'}"
                   oninput="setProp('szBorderColor', this.value)">
        </div>
        <div class="prop-group">
            <label style="display:inline-flex;align-items:center;gap:4px;cursor:pointer;
                          text-transform:none;letter-spacing:0;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${props.isShowLegend ? 'checked' : ''}
                       onchange="setProp('isShowLegend', this.checked)">
                ${escHtml(t('designer.prop.trend.show_legend'))}
            </label>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onTrendBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="trendBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>
        `;
    } else if (szType === 'controlBtn') {
        const szCidLabel = props.szPointName
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_cid'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szCidLabel}
                <button class="btn btn-outline-success btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteControlBtnPoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.btn_label'))}</label>
            <input type="text" value="${escHtml(props.szBtnLabel || t('designer.default.controlBtn_label'))}"
                   oninput="setProp('szBtnLabel', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.btn_icon'))}</label>
            <select style="width:100%;padding:4px 6px;border:1px solid #555;border-radius:4px;
                           background:#2b2b2b;color:#e0e0e0;font-size:12px;"
                    onchange="setProp('szBtnIcon', this.value)">
                ${[
                    { v: 'fa-hand-pointer', tk: 'designer.prop.btn_icon.finger' },
                    { v: 'fa-play',         tk: 'designer.prop.btn_icon.play' },
                    { v: 'fa-power-off',    tk: 'designer.prop.btn_icon.power' },
                    { v: 'fa-paper-plane',  tk: 'designer.prop.btn_icon.send' },
                    { v: 'fa-bolt',         tk: 'designer.prop.btn_icon.bolt' }
                ].map(o => `<option value="${o.v}" ${(props.szBtnIcon || 'fa-hand-pointer') === o.v ? 'selected' : ''}>${escHtml(t(o.tk))}</option>`).join('')}
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.ctrl_value'))}</label>
            <input type="number" value="${props.fCtrlValue ?? 1}" step="any"
                   oninput="setProp('fCtrlValue', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.btn_color'))}</label>
            <input type="color" value="${props.szBtnColor || '#198754'}"
                   oninput="setProp('szBtnColor', this.value)">
        </div>`;
    } else if (szType === 'realtimeValue') {
        const bCircuitBound = props.nCircuitId != null;
        const szSidLabel = bCircuitBound
            ? `<span style="font-size:12px;color:#69db7c;"><i class="fas fa-sitemap me-1"></i>${escHtml(props.szCircuitName || ('#' + props.nCircuitId))}</span>`
            : (props.szPointName
                ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
                : szUnboundLabel);
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t(bCircuitBound ? 'designer.prop.bound_circuit' : 'designer.prop.bound_sid'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szSidLabel}
                <button class="btn btn-outline-danger btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteRealtimeValuePoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_size'))}</label>
            <input type="number" value="${props.nFontSize || 28}" min="12" max="120" step="2"
                   oninput="setProp('nFontSize', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_color'))}</label>
            <input type="color" value="${props.szFontColor || '#212529'}"
                   oninput="setProp('szFontColor', this.value)">
        </div>
        ${bCircuitBound ? buildRtCircuitMetricHtml(props) : `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.unit'))}</label>
            <input type="text" value="${escHtml(props.szUnit || '')}"
                   oninput="setProp('szUnit', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.decimal_places'))}</label>
            <input type="number" value="${props.nDecimalPlaces ?? 2}" min="0" max="6" step="1"
                   oninput="setProp('nDecimalPlaces', Math.max(0, Math.min(6, parseInt(this.value) || 0)))">
        </div>
        ${buildRtValueModeHtml(props)}
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.high_color'))}</label>
            <input type="color" value="${props.szHighColor || '#dc3545'}"
                   oninput="setProp('szHighColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.low_color'))}</label>
            <input type="color" value="${props.szLowColor || '#fd7e14'}"
                   oninput="setProp('szLowColor', this.value)">
        </div>`}
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onRtValBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="rtValBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>
        `;
    } else if (szType === 'diPoint') {
        const bSchedule = props.nScheduleId != null;
        let szBindRow;
        if (bSchedule) {
            szBindRow = `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.source_schedule'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                <span style="font-size:12px;color:#7eb6ff;"><i class="fas fa-calendar-alt me-1"></i>${escHtml(props.szScheduleName || '')}</span>
                <button class="btn btn-outline-success btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteDiPointPoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>`;
        } else {
            const szSidLabel = props.szPointName
                ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
                : szUnboundLabel;
            szBindRow = `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_sid'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szSidLabel}
                <button class="btn btn-outline-success btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteDiPointPoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>`;
        }
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;
        const szMode = props.szDisplayMode || 'indicator';
        szHtml += szBindRow;
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.display_mode'))}</label>
            <select onchange="setProp('szDisplayMode', this.value)"
                    style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;
                           padding:4px 6px;font-size:12px;border-radius:3px;">
                <option value="indicator" ${szMode === 'indicator' ? 'selected' : ''}>${escHtml(t('designer.prop.display_mode.indicator'))}</option>
                <option value="text"      ${szMode === 'text'      ? 'selected' : ''}>${escHtml(t('designer.prop.display_mode.text'))}</option>
            </select>
        </div>
        <hr class="prop-divider">
        ${szMode === 'indicator' ? `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.indicator_size'))}</label>
            <input type="number" value="${props.nIndicatorSize || 28}" min="12" max="80" step="2"
                   oninput="setProp('nIndicatorSize', +this.value)">
        </div>` : `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_size'))}</label>
            <input type="number" value="${props.nFontSize || 24}" min="10" max="120" step="1"
                   oninput="setProp('nFontSize', +this.value)">
        </div>`}
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.on_color'))}</label>
            <input type="color" value="${props.szOnColor || '#28a745'}"
                   oninput="setProp('szOnColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.off_color'))}</label>
            <input type="color" value="${props.szOffColor || '#6c757d'}"
                   oninput="setProp('szOffColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.on_label'))}</label>
            <input type="text" value="${escHtml(props.szOnLabel || 'ON')}"
                   oninput="setProp('szOnLabel', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.off_label'))}</label>
            <input type="text" value="${escHtml(props.szOffLabel || 'OFF')}"
                   oninput="setProp('szOffLabel', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t(szMode === 'text' ? 'designer.prop.alarm_text_color' : 'designer.prop.alarm_color'))}</label>
            <input type="color" value="${props.szAlarmColor || '#dc3545'}"
                   oninput="setProp('szAlarmColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onDiPointBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="diPointBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>
        `;
    } else if (szType === 'aoPoint') {
        const szCidLabel = props.szPointName
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_cid'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szCidLabel}
                <button class="btn btn-outline-info btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteAoPointPoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.display_name'))}</label>
            <input type="text" value="${escHtml(props.szDisplayName || props.szTitle || t('designer.default.aoPoint_title'))}"
                   oninput="setProp('szDisplayName', this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.default_write_value'))}</label>
            <input type="number" value="${props.fWriteValue ?? 0}" step="${props.fStep ?? 1}"
                   oninput="setProp('fWriteValue', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.min_value'))}</label>
            <input type="number" value="${props.fMin ?? 0}" step="any"
                   oninput="setProp('fMin', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.max_value'))}</label>
            <input type="number" value="${props.fMax ?? 100}" step="any"
                   oninput="setProp('fMax', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.step_value'))}</label>
            <input type="number" value="${props.fStep ?? 1}" min="0.001" step="any"
                   oninput="setProp('fStep', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.decimal_places'))}</label>
            <input type="number" value="${props.nDecimalPlaces ?? 1}" min="0" max="6" step="1"
                   oninput="setProp('nDecimalPlaces', Math.max(0, Math.min(6, parseInt(this.value) || 0)))">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.unit'))}</label>
            <input type="text" value="${escHtml(props.szUnit || '')}"
                   oninput="setProp('szUnit', this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.menu_manual'))}</label>
            <input type="text" value="${escHtml(props.szMenuManualLabel ?? '')}"
                   placeholder="${escHtml(t('designer.prop.menu_blank_hint'))}"
                   oninput="setProp('szMenuManualLabel', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.menu_auto'))}</label>
            <input type="text" value="${escHtml(props.szMenuAutoLabel ?? '')}"
                   placeholder="${escHtml(t('designer.prop.menu_blank_hint'))}"
                   oninput="setProp('szMenuAutoLabel', this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_size'))}</label>
            <input type="number" value="${props.nFontSize || 16}" min="10" max="60" step="1"
                   oninput="setProp('nFontSize', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_color'))}</label>
            <input type="color" value="${props.szFontColor || '#ffffff'}"
                   oninput="setProp('szFontColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.block_color'))}</label>
            <input type="color" value="${props.szBlockColor || '#0d6efd'}"
                   oninput="setProp('szBlockColor', this.value)">
        </div>`;
    } else if (szType === 'doPoint') {
        const szCidLabel = props.szPointName
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_cid'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szCidLabel}
                <button class="btn btn-outline-warning btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteDoPointPoint()">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.display_name'))}</label>
            <input type="text" value="${escHtml(props.szDisplayName || props.szTitle || t('designer.default.doPoint_title'))}"
                   oninput="setProp('szDisplayName', this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.on_write_value'))}</label>
            <input type="number" value="${props.nOnValue ?? 1}" step="any"
                   oninput="setProp('nOnValue', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.off_write_value'))}</label>
            <input type="number" value="${props.nOffValue ?? 0}" step="any"
                   oninput="setProp('nOffValue', +this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.menu_on'))}</label>
            <input type="text" value="${escHtml(props.szMenuOnLabel ?? '')}"
                   placeholder="${escHtml(t('designer.prop.menu_blank_hint'))}"
                   oninput="setProp('szMenuOnLabel', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.menu_off'))}</label>
            <input type="text" value="${escHtml(props.szMenuOffLabel ?? '')}"
                   placeholder="${escHtml(t('designer.prop.menu_blank_hint'))}"
                   oninput="setProp('szMenuOffLabel', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.menu_auto'))}</label>
            <input type="text" value="${escHtml(props.szMenuAutoLabel ?? '')}"
                   placeholder="${escHtml(t('designer.prop.menu_blank_hint'))}"
                   oninput="setProp('szMenuAutoLabel', this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_size'))}</label>
            <input type="number" value="${props.nFontSize || 16}" min="10" max="60" step="1"
                   oninput="setProp('nFontSize', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_color'))}</label>
            <input type="color" value="${props.szFontColor || '#212529'}"
                   oninput="setProp('szFontColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.block_color'))}</label>
            <input type="color" value="${props.szBlockColor || '#0d6efd'}"
                   oninput="setProp('szBlockColor', this.value)">
        </div>`;
    } else if (szType === 'pump') {
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;

        // 產生綁定欄位 HTML 的輔助函式
        function pumpBindRow(szLabel, szSidKey, szNameKey, szBtnClass) {
            const szName = props[szNameKey];
            const szDisp = szName
                ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(szName)}</span>`
                : szUnboundLabel;
            return `<div class="prop-group">
                <label>${escHtml(szLabel)}</label>
                <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                    ${szDisp}
                    <button class="btn ${szBtnClass} btn-sm py-0 px-2" style="font-size:11px;"
                            onclick="reroutePumpBinding('${szSidKey}','${szNameKey}')">
                        <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                    </button>
                </div>
            </div>`;
        }

        szHtml += `
        ${_equipReassignBtnHtml()}
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.pump.sid_monitor'))}</div>
        ${pumpBindRow(t('designer.prop.pump.run_status'),   'szSidRun',   'szRunName',   'btn-outline-info')}
        ${pumpBindRow(t('designer.prop.pump.fault_status'), 'szSidFault', 'szFaultName', 'btn-outline-danger')}
        ${pumpBindRow(t('designer.prop.pump.mode_status'),  'szSidMode',  'szModeName',  'btn-outline-warning')}
        ${pumpBindRow(t('designer.prop.pump.frequency'),    'szSidFreq',  'szFreqName',  'btn-outline-info')}
        <hr class="prop-divider">
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.pump.cid_control'))}</div>
        ${pumpBindRow(t('designer.prop.pump.start_stop'), 'szCidStartStop', 'szStartStopName', 'btn-outline-success')}
        ${pumpBindRow(t('designer.prop.pump.freq_set'),   'szCidFreqSet',   'szFreqSetName',   'btn-outline-success')}
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.outlet_dir'))}</label>
            <select style="width:100%;padding:4px 6px;border:1px solid #555;border-radius:4px;
                           background:#2b2b2b;color:#e0e0e0;font-size:12px;"
                    onchange="setProp('szOutletDir', this.value)">
                <option value="right" ${(props.szOutletDir || 'right') === 'right' ? 'selected' : ''}>${escHtml(t('designer.prop.pump.outlet_right'))}</option>
                <option value="left"  ${props.szOutletDir === 'left'  ? 'selected' : ''}>${escHtml(t('designer.prop.pump.outlet_left'))}</option>
                <option value="up"    ${props.szOutletDir === 'up'    ? 'selected' : ''}>${escHtml(t('designer.prop.pump.outlet_up'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.run_color'))}</label>
            <input type="color" value="${props.szRunColor || '#28a745'}"
                   oninput="setProp('szRunColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.stop_color'))}</label>
            <input type="color" value="${props.szStopColor || '#6c757d'}"
                   oninput="setProp('szStopColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.fault_color'))}</label>
            <input type="color" value="${props.szFaultColor || '#dc3545'}"
                   oninput="setProp('szFaultColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.manual_color'))}</label>
            <input type="color" value="${props.szManualColor || '#ffc107'}"
                   oninput="setProp('szManualColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.auto_color'))}</label>
            <input type="color" value="${props.szAutoColor || '#0d6efd'}"
                   oninput="setProp('szAutoColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onPumpBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="pumpBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>`;
    } else if (szType === 'pipe') {
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;
        const szMode = props.szBindMode || '';

        // 綁定顯示：目前生效模式該列顯示點名，另一列顯示未綁提示
        const szDiName = (szMode === 'di' && props.szPointName)
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        const szAnName = (szMode === 'analog' && props.szPointName)
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;

        szHtml += `
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.pipe.binding'))}</div>
        <div style="font-size:10px;color:#888;margin-bottom:6px;">${escHtml(t('designer.prop.pipe.binding_hint'))}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.bind_di'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szDiName}
                <button class="btn btn-outline-success btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="reroutePipeBinding('di')">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t(szMode === 'di' ? 'designer.prop.reselect' : 'designer.prop.bind'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.bind_analog'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szAnName}
                <button class="btn btn-outline-info btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="reroutePipeBinding('analog')">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t(szMode === 'analog' ? 'designer.prop.reselect' : 'designer.prop.bind'))}
                </button>
            </div>
        </div>`;
        if (szMode === 'analog') {
            szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.compare'))}</label>
            <select onchange="setProp('szCompare', this.value)"
                    style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;padding:4px 6px;font-size:12px;border-radius:3px;">
                <option value="gt"  ${(props.szCompare || 'gt') === 'gt'  ? 'selected' : ''}>${escHtml(t('designer.prop.pipe.compare_gt'))}</option>
                <option value="gte" ${props.szCompare === 'gte' ? 'selected' : ''}>${escHtml(t('designer.prop.pipe.compare_gte'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.threshold'))}</label>
            <input type="number" value="${props.fThreshold ?? 0}" step="any"
                   oninput="setProp('fThreshold', +this.value)">
        </div>`;
        }
        szHtml += `
        <hr class="prop-divider">
        <div style="font-size:10px;color:#888;margin-bottom:6px;line-height:1.6;">${escHtml(t('designer.prop.pipe.nodes_hint'))}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.direction'))}</label>
            <select onchange="setProp('szDir', this.value)"
                    style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;padding:4px 6px;font-size:12px;border-radius:3px;">
                <option value="fwd" ${(props.szDir || 'fwd') === 'fwd' ? 'selected' : ''}>${escHtml(t('designer.prop.pipe.dir_fwd'))}</option>
                <option value="rev" ${props.szDir === 'rev' ? 'selected' : ''}>${escHtml(t('designer.prop.pipe.dir_rev'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.thickness'))}</label>
            <input type="number" value="${props.nThickness || 8}" min="2" max="40" step="1"
                   oninput="setProp('nThickness', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.speed'))}</label>
            <input type="number" value="${props.nSpeed || 3}" min="1" max="5" step="1"
                   oninput="setProp('nSpeed', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.flow_color'))}</label>
            <input type="color" value="${props.szFlowColor || '#0d6efd'}"
                   oninput="setProp('szFlowColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.stop_color'))}</label>
            <input type="color" value="${props.szStopColor || '#adb5bd'}"
                   oninput="setProp('szStopColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.bad_color'))}</label>
            <input type="color" value="${props.szBadColor || '#6c757d'}"
                   oninput="setProp('szBadColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onPipeBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="pipeBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>`;
    } else if (szType === 'image') {
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;
        const szMode = props.szBindMode || '';
        const szFit  = props.szFit || 'contain';

        // 目前兩張圖預覽（無則顯示佔位框）
        function imgPreview(szSrc) {
            return szSrc
                ? `<img src="${escHtml(szSrc)}" style="width:44px;height:44px;object-fit:contain;background:#2b2b2b;border:1px solid #555;border-radius:4px;">`
                : `<div style="width:44px;height:44px;display:flex;align-items:center;justify-content:center;background:#2b2b2b;border:1px dashed #555;border-radius:4px;color:#777;font-size:9px;">—</div>`;
        }

        const szDiName = (szMode === 'di' && props.szPointName)
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;
        const szAnName = (szMode === 'analog' && props.szPointName)
            ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(props.szPointName)}</span>`
            : szUnboundLabel;

        szHtml += `
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.image.sources'))}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.image.anim'))}</label>
            <div style="display:flex;align-items:center;gap:8px;">
                ${imgPreview(props.szAnimSrc)}
                <button class="btn btn-outline-info btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="pickImageFile('anim')">
                    <i class="fas fa-upload me-1"></i>${escHtml(t('designer.prop.image.upload'))}
                </button>
                ${props.szAnimSrc ? `<button class="btn btn-outline-secondary btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="clearImageSrc('anim')"><i class="fas fa-times"></i></button>` : ''}
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.image.still'))}</label>
            <div style="display:flex;align-items:center;gap:8px;">
                ${imgPreview(props.szStillSrc)}
                <button class="btn btn-outline-info btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="pickImageFile('still')">
                    <i class="fas fa-upload me-1"></i>${escHtml(t('designer.prop.image.upload'))}
                </button>
                ${props.szStillSrc ? `<button class="btn btn-outline-secondary btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="clearImageSrc('still')"><i class="fas fa-times"></i></button>` : ''}
            </div>
        </div>
        <div style="font-size:10px;color:#888;margin:2px 0 6px;">${escHtml(t('designer.prop.image.still_hint'))}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.image.gallery'))}</label>
            <div id="imageGalleryGrid" class="image-gallery-grid">
                <div style="font-size:10px;color:#888;">${escHtml(t('designer.image.gallery_loading'))}</div>
            </div>
        </div>
        <hr class="prop-divider">
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.image.binding'))}</div>
        <div style="font-size:10px;color:#888;margin-bottom:6px;">${escHtml(t('designer.prop.image.binding_hint'))}</div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.bind_di'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szDiName}
                <button class="btn btn-outline-success btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteImageBinding('di')">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t(szMode === 'di' ? 'designer.prop.reselect' : 'designer.prop.bind'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.bind_analog'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szAnName}
                <button class="btn btn-outline-info btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="rerouteImageBinding('analog')">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t(szMode === 'analog' ? 'designer.prop.reselect' : 'designer.prop.bind'))}
                </button>
                ${szMode ? `<button class="btn btn-outline-secondary btn-sm py-0 px-2" style="font-size:11px;"
                        title="${escHtml(t('designer.ctx.clear_binding'))}"
                        onclick="clearImageBinding()"><i class="fas fa-times"></i></button>` : ''}
            </div>
        </div>`;
        if (szMode === 'analog') {
            szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.compare'))}</label>
            <select onchange="setProp('szCompare', this.value)"
                    style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;padding:4px 6px;font-size:12px;border-radius:3px;">
                <option value="gt"  ${(props.szCompare || 'gt') === 'gt'  ? 'selected' : ''}>${escHtml(t('designer.prop.pipe.compare_gt'))}</option>
                <option value="gte" ${props.szCompare === 'gte' ? 'selected' : ''}>${escHtml(t('designer.prop.pipe.compare_gte'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pipe.threshold'))}</label>
            <input type="number" value="${props.fThreshold ?? 0}" step="any"
                   oninput="setProp('fThreshold', +this.value)">
        </div>`;
        }
        szHtml += `
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.image.fit'))}</label>
            <select onchange="setProp('szFit', this.value)"
                    style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;padding:4px 6px;font-size:12px;border-radius:3px;">
                <option value="contain" ${szFit === 'contain' ? 'selected' : ''}>${escHtml(t('designer.prop.image.fit_contain'))}</option>
                <option value="cover"   ${szFit === 'cover'   ? 'selected' : ''}>${escHtml(t('designer.prop.image.fit_cover'))}</option>
                <option value="fill"    ${szFit === 'fill'    ? 'selected' : ''}>${escHtml(t('designer.prop.image.fit_fill'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onImageBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="imageBgPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>`;
        // 面板寫入 DOM 後再非同步載入內建圖庫縮圖
        setTimeout(_loadImageGallery, 0);
    } else if (szType === 'coolingTower' || szType === 'ahuFan' || szType === 'chiller') {
        // 馬達型設備（冷卻水塔 / 空調箱風扇 / 冰機）— 仿水泵，依設備差異客製綁定
        const isBgTransparent = !props.szBgColor || props.szBgColor === 'transparent';
        const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;
        const szPickerId      = szType + 'BgPicker';

        function motorBindRow(szLabel, szSidKey, szNameKey, szBtnClass) {
            const szName = props[szNameKey];
            const szDisp = szName
                ? `<span style="font-size:12px;color:#c8c8c8;">${escHtml(szName)}</span>`
                : szUnboundLabel;
            // 已綁定 → 加「取消綁定」✕ 鈕（清空 SID+名稱，重繪元件與面板）
            const szClearBtn = szName
                ? `<button class="btn btn-outline-secondary btn-sm py-0 px-2" style="font-size:11px;"
                        title="${escHtml(t('designer.ctx.clear_binding'))}"
                        onclick="clearMotorBinding('${szSidKey}','${szNameKey}')">
                    <i class="fas fa-times"></i>
                </button>`
                : '';
            return `<div class="prop-group">
                <label>${escHtml(szLabel)}</label>
                <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                    ${szDisp}
                    <button class="btn ${szBtnClass} btn-sm py-0 px-2" style="font-size:11px;"
                            onclick="rerouteMotorBinding('${szSidKey}','${szNameKey}')">
                        <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                    </button>
                    ${szClearBtn}
                </div>
            </div>`;
        }

        // ── SID 監控段（共同：運轉/故障/手自動；冰機的手自動語意=遠端/現場）──
        const szModeLabel = szType === 'chiller'
            ? t('designer.prop.motor.remote_local')
            : t('designer.prop.pump.mode_status');
        szHtml += `
        ${_equipReassignBtnHtml()}
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.pump.sid_monitor'))}</div>
        ${motorBindRow(t('designer.prop.pump.run_status'),   'szSidRun',   'szRunName',   'btn-outline-info')}
        ${motorBindRow(t('designer.prop.pump.fault_status'), 'szSidFault', 'szFaultName', 'btn-outline-danger')}
        ${motorBindRow(szModeLabel,                          'szSidMode',  'szModeName',  'btn-outline-warning')}`;

        // ── 依設備差異的監控/控制點 ──
        if (szType === 'chiller') {
            szHtml += `
        ${motorBindRow(t('designer.prop.motor.load'),    'szSidLoad',  'szLoadName',  'btn-outline-info')}
        ${motorBindRow(t('designer.prop.motor.chw_out'), 'szSidChwOut','szChwOutName','btn-outline-info')}
        <hr class="prop-divider">
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.pump.cid_control'))}</div>
        ${motorBindRow(t('designer.prop.pump.start_stop'), 'szCidStartStop', 'szStartStopName', 'btn-outline-success')}
        ${motorBindRow(t('designer.prop.motor.set_temp'),  'szCidSetTemp',   'szSetTempName',   'btn-outline-success')}
        <hr class="prop-divider">`;
        } else {
            // coolingTower / ahuFan — VFD（頻率）
            szHtml += `
        ${motorBindRow(t('designer.prop.pump.frequency'), 'szSidFreq', 'szFreqName', 'btn-outline-info')}`;
            if (szType === 'coolingTower') {
                szHtml += `
        ${motorBindRow(t('designer.prop.motor.water_temp'), 'szSidWaterTemp', 'szWaterTempName', 'btn-outline-info')}`;
            }
            szHtml += `
        <hr class="prop-divider">
        <div style="font-size:11px;color:#aaa;margin-bottom:4px;letter-spacing:1px;">${escHtml(t('designer.prop.pump.cid_control'))}</div>
        ${motorBindRow(t('designer.prop.pump.start_stop'), 'szCidStartStop', 'szStartStopName', 'btn-outline-success')}
        ${motorBindRow(t('designer.prop.pump.freq_set'),   'szCidFreqSet',   'szFreqSetName',   'btn-outline-success')}
        <hr class="prop-divider">`;
        }

        // ── 共同外觀色（沿用 pump i18n key）──
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.run_color'))}</label>
            <input type="color" value="${props.szRunColor || '#28a745'}"
                   oninput="setProp('szRunColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.stop_color'))}</label>
            <input type="color" value="${props.szStopColor || '#6c757d'}"
                   oninput="setProp('szStopColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.fault_color'))}</label>
            <input type="color" value="${props.szFaultColor || '#dc3545'}"
                   oninput="setProp('szFaultColor', this.value)">
        </div>
        `;
        if (szType === 'chiller') {
            // 冰機：模式色只剩「現場面板色」（機身專職狀態色，遠端面板固定灰，不用自動色）
            szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.motor.local_color'))}</label>
            <input type="color" value="${props.szManualColor || '#c79100'}"
                   oninput="setProp('szManualColor', this.value)">
        </div>`;
        } else {
            szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.manual_color'))}</label>
            <input type="color" value="${props.szManualColor || '#ffc107'}"
                   oninput="setProp('szManualColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pump.auto_color'))}</label>
            <input type="color" value="${props.szAutoColor || '#0d6efd'}"
                   oninput="setProp('szAutoColor', this.value)">
        </div>`;
        }
        szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onMotorBgTransparentChange('${szPickerId}', this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="${szPickerId}" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>`;
    }

    document.getElementById('propBody').innerHTML = szHtml;
}

// ============================================================
// 文字 Widget 屬性面板 HTML
// ============================================================
function buildTextPropHtml(el, props) {
    const szFontFamilyOpts = [
        { val: 'inherit',                             label: t('designer.prop.text.font.default') },
        { val: "'Microsoft JhengHei', sans-serif",    label: t('designer.prop.text.font.msjh') },
        { val: "'Noto Sans TC', sans-serif",          label: 'Noto Sans TC' },
        { val: "'Arial', sans-serif",                 label: 'Arial' },
        { val: "'Times New Roman', serif",            label: 'Times New Roman' },
        { val: "'Courier New', monospace",            label: 'Courier New' },
    ].map(o => `<option value="${escHtml(o.val)}" ${props.szFontFamily === o.val ? 'selected' : ''}>${escHtml(o.label)}</option>`).join('');

    const isBgTransparent = props.szBgColor === 'transparent';
    const szBgColorVal    = isBgTransparent ? '#ffffff' : props.szBgColor;

    return `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.text_content'))}</label>
            <textarea id="txtContentInput" rows="3" style="width:100%;background:#3c3c3c;border:1px solid #555;
                      color:#d4d4d4;padding:4px 6px;font-size:12px;border-radius:3px;
                      outline:none;resize:vertical;box-sizing:border-box;"
                      oninput="setProp('szText', this.value)">${escHtml(props.szText)}</textarea>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pos_x'))}</label>
            <input type="number" id="pX" value="${parseInt(el.style.left)}" step="20"
                   oninput="setPos('left', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.pos_y'))}</label>
            <input type="number" id="pY" value="${parseInt(el.style.top)}" step="20"
                   oninput="setPos('top', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.width'))}</label>
            <input type="number" id="pW" value="${el.offsetWidth}" step="1"
                   oninput="setSize('width', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.height'))}</label>
            <input type="number" id="pH" value="${el.offsetHeight}" step="1"
                   oninput="setSize('height', +this.value)">
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_size'))}</label>
            <input type="number" value="${props.nFontSize}" min="8" max="200" step="2"
                   oninput="setProp('nFontSize', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_color'))}</label>
            <input type="color" value="${props.szFontColor}"
                   oninput="setProp('szFontColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_family'))}</label>
            <select oninput="setProp('szFontFamily', this.value)">${szFontFamilyOpts}</select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.text.bold'))}</label>
            <select oninput="setProp('szFontWeight', this.value)">
                <option value="normal" ${props.szFontWeight === 'normal' ? 'selected' : ''}>${escHtml(t('designer.prop.text.bold.normal'))}</option>
                <option value="bold"   ${props.szFontWeight === 'bold'   ? 'selected' : ''}>${escHtml(t('designer.prop.text.bold.bold'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.text.italic'))}</label>
            <select onchange="setProp('isItalic', this.value === 'true')">
                <option value="false" ${!props.isItalic ? 'selected' : ''}>${escHtml(t('designer.prop.text.italic.no'))}</option>
                <option value="true"  ${props.isItalic  ? 'selected' : ''}>${escHtml(t('designer.prop.text.italic.yes'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.background_color'))}</label>
            <label style="display:inline-flex;align-items:center;gap:3px;font-size:11px;color:#9d9d9d;
                          font-weight:normal;text-transform:none;letter-spacing:0;cursor:pointer;margin-top:4px;white-space:nowrap;">
                <input type="checkbox" style="cursor:pointer;width:auto;"
                       ${isBgTransparent ? 'checked' : ''}
                       onchange="onTextBgTransparentChange(this.checked)">
                ${escHtml(t('designer.prop.transparent_bg'))}
            </label>
            <input type="color" id="txtBgColorPicker" value="${szBgColorVal}"
                   style="width:100%;margin-top:4px;${isBgTransparent ? 'display:none;' : ''}"
                   oninput="setProp('szBgColor', this.value)">
        </div>
    `;
}

// ============================================================
// 圖片/動畫元件（image）— 圖片來源 / 內建圖庫 / 透明底
// ============================================================
function onImageBgTransparentChange(isChecked) {
    const picker = document.getElementById('imageBgPicker');
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        if (picker) picker.style.display = 'none';
    } else {
        setProp('szBgColor', picker ? picker.value : '#ffffff');
        if (picker) picker.style.display = '';
    }
}

// 設定/清空 動畫圖或靜止圖來源
function _imageSetSrc(szWhich, szUrl) {
    if (!selectedEl || selectedEl.dataset.type !== 'image') return;
    const szKey = (szWhich === 'still') ? 'szStillSrc' : 'szAnimSrc';
    selectedEl.widgetProps[szKey] = szUrl || '';
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}
function clearImageSrc(szWhich) { _imageSetSrc(szWhich, ''); }

// 上傳自訂圖片：讀檔 → POST /Designer/asset → 回存 URL
function pickImageFile(szWhich) {
    if (!selectedEl || selectedEl.dataset.type !== 'image') return;
    const input = document.createElement('input');
    input.type   = 'file';
    input.accept = 'image/gif,image/png,image/jpeg,image/webp,image/svg+xml';
    input.onchange = async () => {
        const f = input.files && input.files[0];
        if (!f) return;
        const fd = new FormData();
        fd.append('file', f);
        try {
            const resp = await fetch('/Designer/asset', { method: 'POST', body: fd });
            const r = await resp.json();
            if (r.success && r.url) _imageSetSrc(szWhich, r.url);
            else alert(r.error || t('designer.image.upload_failed'));
        } catch (e) {
            alert(t('designer.image.upload_failed'));
        }
    };
    input.click();
}

// 選用內建圖庫：動畫圖設為 anim、靜止圖設為 still（缺一則互補）
function chooseGalleryImage(szAnim, szStill) {
    if (!selectedEl || selectedEl.dataset.type !== 'image') return;
    selectedEl.widgetProps.szAnimSrc  = szAnim || szStill || '';
    selectedEl.widgetProps.szStillSrc = szStill || szAnim || '';
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}

// 內建圖庫縮圖載入（session 內快取一次）
let _imageGalleryCache = null;
async function _loadImageGallery() {
    const grid = document.getElementById('imageGalleryGrid');
    if (!grid) return;
    try {
        if (!_imageGalleryCache) {
            const resp = await fetch('/Designer/gallery');
            _imageGalleryCache = await resp.json();
        }
        if (!_imageGalleryCache || !_imageGalleryCache.length) {
            grid.innerHTML = `<div style="font-size:10px;color:#888;">${escHtml(t('designer.image.gallery_empty'))}</div>`;
            return;
        }
        grid.innerHTML = _imageGalleryCache.map(it =>
            `<div class="image-gallery-item" title="${escHtml((it.szCategory || '') + ' / ' + (it.szName || ''))}"
                  data-anim="${escHtml(it.szAnimSrc || '')}" data-still="${escHtml(it.szStillSrc || '')}">
                <img src="${escHtml(it.szAnimSrc || it.szStillSrc || '')}" alt="">
            </div>`
        ).join('');
        // 綁 click（避免把 URL 塞進 inline onclick 造成引號逃逸問題）
        grid.querySelectorAll('.image-gallery-item').forEach(el => {
            el.addEventListener('click', () => chooseGalleryImage(el.dataset.anim, el.dataset.still));
        });
    } catch (e) {
        grid.innerHTML = `<div style="font-size:10px;color:#e06c75;">${escHtml(t('designer.image.gallery_error'))}</div>`;
    }
}

function onTextBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('txtBgColorPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('txtBgColorPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

// 從單一 widget state (props) 中檢查是否有同 SID 的 DI 標籤
function _checkDiLabelsInProps(szType, props, szSid) {
    if (szType === 'diPoint' && props.szSid === szSid) {
        if (props.szOnLabel !== 'ON' || props.szOffLabel !== 'OFF') {
            return { szOnLabel: props.szOnLabel, szOffLabel: props.szOffLabel };
        }
    }
    if (szType === 'table' && props.arrCells) {
        for (const row of props.arrCells) {
            for (const cell of row) {
                if (cell && cell.szSid === szSid && cell.szPointType === 'DI') {
                    if (cell.szOnLabel !== 'ON' || cell.szOffLabel !== 'OFF') {
                        return { szOnLabel: cell.szOnLabel, szOffLabel: cell.szOffLabel };
                    }
                }
            }
        }
    }
    return null;
}

// 在所有頁面（當前畫布 + arrPageTree 其他頁面）中，找出同 SID 的 DI ON/OFF 標籤
function _findDiLabelsForSid(szSid) {
    if (!szSid) return null;
    // 1. 搜尋當前畫布 DOM
    for (const el of canvas.querySelectorAll('.canvas-widget')) {
        if (!el.widgetProps) continue;
        const found = _checkDiLabelsInProps(el.dataset.type, el.widgetProps, szSid);
        if (found) return found;
    }
    // 2. 搜尋其他頁面的 arrWidgetState（遞迴遍歷 arrPageTree）
    function searchTree(pages) {
        for (const page of pages) {
            if (page.szId !== szCurrentPageId && page.arrWidgetState) {
                for (const ws of page.arrWidgetState) {
                    const found = _checkDiLabelsInProps(ws.szType, ws.props || {}, szSid);
                    if (found) return found;
                }
            }
            if (page.arrChildren) {
                const found = searchTree(page.arrChildren);
                if (found) return found;
            }
        }
        return null;
    }
    return searchTree(arrPageTree);
}

// 同步 ON/OFF 標籤到所有頁面中綁定同一個 SID 的 DI widget 與 table cell
function _syncDiLabelsToSid(szSid, szOnLabel, szOffLabel, elExclude) {
    if (!szSid) return;
    // 1. 同步當前畫布 DOM
    for (const el of canvas.querySelectorAll('.canvas-widget')) {
        if (!el.widgetProps) continue;
        if (el === elExclude && !el.widgetProps.arrCells) continue;
        const p = el.widgetProps;
        if (el.dataset.type === 'diPoint' && p.szSid === szSid && el !== elExclude) {
            p.szOnLabel  = szOnLabel;
            p.szOffLabel = szOffLabel;
            renderWidget(el);
        }
        if (el.dataset.type === 'table' && p.arrCells) {
            let isChanged = false;
            for (const row of p.arrCells) {
                for (const cell of row) {
                    if (cell && cell.szSid === szSid && cell.szPointType === 'DI') {
                        cell.szOnLabel  = szOnLabel;
                        cell.szOffLabel = szOffLabel;
                        isChanged = true;
                    }
                }
            }
            if (isChanged) renderWidget(el);
        }
    }
    // 2. 同步其他頁面的 arrWidgetState
    function syncTree(pages) {
        for (const page of pages) {
            if (page.szId !== szCurrentPageId && page.arrWidgetState) {
                for (const ws of page.arrWidgetState) {
                    const props = ws.props;
                    if (!props) continue;
                    if (ws.szType === 'diPoint' && props.szSid === szSid) {
                        props.szOnLabel  = szOnLabel;
                        props.szOffLabel = szOffLabel;
                    }
                    if (ws.szType === 'table' && props.arrCells) {
                        for (const row of props.arrCells) {
                            for (const cell of row) {
                                if (cell && cell.szSid === szSid && cell.szPointType === 'DI') {
                                    cell.szOnLabel  = szOnLabel;
                                    cell.szOffLabel = szOffLabel;
                                }
                            }
                        }
                    }
                }
            }
            if (page.arrChildren) syncTree(page.arrChildren);
        }
    }
    syncTree(arrPageTree);
}

// AI 點位上限/下限顏色 → 同步所有同 SID 的 AI Widget（realtimeValue + table AI cell）
function _syncAiColorsToSid(szSid, szHighColor, szLowColor, elExclude) {
    if (!szSid) return;
    const arrSyncTypes = ['realtimeValue', 'gauge'];
    // 1. 同步當前畫布 DOM
    for (const el of canvas.querySelectorAll('.canvas-widget')) {
        if (!el.widgetProps) continue;
        const p = el.widgetProps;
        if (arrSyncTypes.includes(el.dataset.type) && p.szSid === szSid && el !== elExclude) {
            p.szHighColor = szHighColor;
            p.szLowColor  = szLowColor;
            renderWidget(el);
        }
        if (el.dataset.type === 'table' && p.arrCells) {
            let isChanged = false;
            for (const row of p.arrCells) {
                for (const cell of row) {
                    if (cell && cell.szSid === szSid && (cell.szPointType || 'AI') === 'AI') {
                        cell.szHighColor = szHighColor;
                        cell.szLowColor  = szLowColor;
                        isChanged = true;
                    }
                }
            }
            if (isChanged) renderWidget(el);
        }
    }
    // 2. 同步其他頁面的 arrWidgetState
    function syncTree(pages) {
        for (const page of pages) {
            if (page.szId !== szCurrentPageId && page.arrWidgetState) {
                for (const ws of page.arrWidgetState) {
                    const props = ws.props;
                    if (!props) continue;
                    if (arrSyncTypes.includes(ws.szType) && props.szSid === szSid) {
                        props.szHighColor = szHighColor;
                        props.szLowColor  = szLowColor;
                    }
                    if (ws.szType === 'table' && props.arrCells) {
                        for (const row of props.arrCells) {
                            for (const cell of row) {
                                if (cell && cell.szSid === szSid && (cell.szPointType || 'AI') === 'AI') {
                                    cell.szHighColor = szHighColor;
                                    cell.szLowColor  = szLowColor;
                                }
                            }
                        }
                    }
                }
            }
            if (page.arrChildren) syncTree(page.arrChildren);
        }
    }
    syncTree(arrPageTree);
}

// DI 點位警報顏色 → 同步所有同 SID 的 DI Widget（diPoint + table DI cell）
function _syncDiAlarmColorToSid(szSid, szAlarmColor, elExclude) {
    if (!szSid) return;
    // 1. 同步當前畫布 DOM
    for (const el of canvas.querySelectorAll('.canvas-widget')) {
        if (!el.widgetProps) continue;
        const p = el.widgetProps;
        if (el.dataset.type === 'diPoint' && p.szSid === szSid && el !== elExclude) {
            p.szAlarmColor = szAlarmColor;
            renderWidget(el);
        }
        if (el.dataset.type === 'table' && p.arrCells) {
            let isChanged = false;
            for (const row of p.arrCells) {
                for (const cell of row) {
                    if (cell && cell.szSid === szSid && cell.szPointType === 'DI') {
                        cell.szAlarmColor = szAlarmColor;
                        isChanged = true;
                    }
                }
            }
            if (isChanged) renderWidget(el);
        }
    }
    // 2. 同步其他頁面的 arrWidgetState
    function syncTree(pages) {
        for (const page of pages) {
            if (page.szId !== szCurrentPageId && page.arrWidgetState) {
                for (const ws of page.arrWidgetState) {
                    const props = ws.props;
                    if (!props) continue;
                    if (ws.szType === 'diPoint' && props.szSid === szSid) {
                        props.szAlarmColor = szAlarmColor;
                    }
                    if (ws.szType === 'table' && props.arrCells) {
                        for (const row of props.arrCells) {
                            for (const cell of row) {
                                if (cell && cell.szSid === szSid && cell.szPointType === 'DI') {
                                    cell.szAlarmColor = szAlarmColor;
                                }
                            }
                        }
                    }
                }
            }
            if (page.arrChildren) syncTree(page.arrChildren);
        }
    }
    syncTree(arrPageTree);
}

// 更新 prop → 重新渲染 widget 內容
function setProp(szKey, val) {
    if (!selectedEl) return;
    selectedEl.widgetProps[szKey] = val;
    // 手動改小數位數 → 之後重選點位不再自動覆蓋
    if (szKey === 'nDecimalPlaces') selectedEl.widgetProps.isDecimalAuto = false;
    // 當列數/欄數改變時同步 arrCells
    if (selectedEl.dataset.type === 'table' && (szKey === 'nRows' || szKey === 'nCols')) {
        syncArrCellsSize(selectedEl.widgetProps);
        nSelectedCellRow = -1;
        nSelectedCellCol = -1;
    }
    // table 結構性屬性改變 → 翻 bTableSizeLocked，由 computeTableWidgetSize 接管尺寸（plan 決策 7）
    let bJustLockedTable = false;
    if (selectedEl.dataset.type === 'table' &&
        (szKey === 'nRows' || szKey === 'nCols' || szKey === 'nDefaultRowH' || szKey === 'nDefaultColW')) {
        const p = selectedEl.widgetProps;
        bJustLockedTable = !p.bTableSizeLocked;
        p.bTableSizeLocked = true;
        const sz = computeTableWidgetSize(p);
        selectedEl.style.width  = sz.nW + 'px';
        selectedEl.style.height = sz.nH + 'px';
        syncSizeInputs(sz.nW, sz.nH);
    }
    // DI ON/OFF 標籤修改時，同步到所有同 SID 的 DI Widget
    if (selectedEl.dataset.type === 'diPoint' && (szKey === 'szOnLabel' || szKey === 'szOffLabel')) {
        const p = selectedEl.widgetProps;
        _syncDiLabelsToSid(p.szSid, p.szOnLabel, p.szOffLabel, selectedEl);
    }
    // AI/Gauge 上限/下限顏色修改時，同步到所有同 SID 的 Widget
    if ((selectedEl.dataset.type === 'realtimeValue' || selectedEl.dataset.type === 'gauge') && (szKey === 'szHighColor' || szKey === 'szLowColor')) {
        const p = selectedEl.widgetProps;
        _syncAiColorsToSid(p.szSid, p.szHighColor, p.szLowColor, selectedEl);
    }
    // DI 警報顏色修改時，同步到所有同 SID 的 DI Widget
    if (selectedEl.dataset.type === 'diPoint' && szKey === 'szAlarmColor') {
        const p = selectedEl.widgetProps;
        _syncDiAlarmColorToSid(p.szSid, p.szAlarmColor, selectedEl);
    }
    renderWidget(selectedEl);
    // DI 顯示模式切換時重新渲染屬性面板（更新標籤文字）
    if (selectedEl.dataset.type === 'diPoint' && szKey === 'szDisplayMode') {
        renderPropPanel(selectedEl);
    }
    // 即時曲線：時間窗 / 畫點間隔改變會影響「點數」提示與是否觸及硬上限，需重繪面板。
    // ⚠️ 不可對每個 key 都重繪 —— 會在打字中途把 input 換掉、游標跳掉。
    if (selectedEl.dataset.type === 'trendChart' && (szKey === 'nWindowSec' || szKey === 'nSampleSec')) {
        _renderTrendHintOnly(selectedEl);
    }
    // table 第一次鎖定時重新渲染屬性面板（讓寬高 input 變 readonly + 出現說明）
    if (bJustLockedTable) {
        renderPropPanel(selectedEl);
    }
}

// 套用表格預設底色樣式（plan 2026-07-04）
// 一次性填色：把 preset 各色複製進 props，並整組覆蓋 header/data cell 字色
// （2026-07-04 使用者確認：連同覆蓋已自訂字色，套完可再逐格改回）
function applyTablePreset(szKey) {
    if (!selectedEl || selectedEl.dataset.type !== 'table') return;
    const preset = TABLE_STYLE_PRESETS[szKey];
    if (!preset) return;
    const props = selectedEl.widgetProps;
    initArrCells(props);
    props.szHeaderColor = preset.szHeaderColor;
    props.szBodyBgOdd   = preset.szBodyBgOdd;
    props.szBodyBgEven  = preset.szBodyBgEven;
    props.szBorderColor = preset.szBorderColor;
    props.arrCells.forEach((row, ri) => row.forEach(cell => {
        cell.szFontColor = (ri === 0) ? preset.szHeaderFont : preset.szDataFont;
    }));
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}

function setPos(szSide, nVal) {
    if (!selectedEl) return;
    selectedEl.style[szSide] = Math.max(0, nVal) + 'px';
}

function setSize(szSide, nVal) {
    if (!selectedEl) return;
    _applySizeTo(selectedEl, szSide, nVal);
}

// 對單一元件套用寬/高（含尺寸鎖定與最小值規則），供 setSize / setSizeMulti 共用
function _applySizeTo(el, szSide, nVal) {
    if (!el) return;
    // table 鎖定尺寸時禁止外部 setSize（plan 決策 4）
    if (el.dataset.type === 'table' && el.widgetProps?.bTableSizeLocked) return;
    // 折線管路大小由節點決定，禁止外部 setSize（plan 2026-07-23 決策 4）
    if (el.dataset.type === 'pipe') return;
    const def = WIDGET_DEFS[el.dataset.type];
    const nMin = szSide === 'width' ? (def?.nMinW || 40) : (def?.nMinH || 30);
    el.style[szSide] = Math.max(nMin, nVal) + 'px';
}

// 多選時同步調整所有選取元件的寬或高（各自套用鎖定/最小值規則）
function setSizeMulti(szSide, nVal) {
    if (selectedWidgetIds.size === 0) return;
    selectedWidgetIds.forEach(szId => {
        _applySizeTo(document.getElementById(szId), szSide, nVal);
    });
}

// 多選屬性面板：僅提供群組寬高調整（套用到所有選取元件）
function renderMultiSelectPropPanel() {
    const n   = selectedWidgetIds.size;
    const ref = selectedEl;   // 以最後 focus 的元件當作寬高初始參考值
    const nW  = ref ? ref.offsetWidth  : '';
    const nH  = ref ? ref.offsetHeight : '';
    document.getElementById('propBody').innerHTML = `
        <div style="font-size:12px;color:#0d6efd;margin-bottom:6px;">
            <i class="fas fa-object-group me-1"></i>${escHtml(t('designer.prop.multi_selected', { count: n }))}
        </div>
        <div style="font-size:10px;color:#888;margin-bottom:6px;line-height:1.4;">
            ${escHtml(t('designer.prop.multi_size_hint'))}
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.width'))}</label>
            <input type="number" id="pW" value="${nW}" step="1" min="1"
                   oninput="setSizeMulti('width', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.height'))}</label>
            <input type="number" id="pH" value="${nH}" step="1" min="1"
                   oninput="setSizeMulti('height', +this.value)">
        </div>
    `;
}

// ============================================================
// 表格儲存格選取與屬性面板
// ============================================================
let nSelectedCellRow = -1;
let nSelectedCellCol = -1;

function onTableCellClick(widgetEl, nRow, nCol) {
    selectWidget(widgetEl);
    nSelectedCellRow = nRow;
    nSelectedCellCol = nCol;
    // 高亮選中 cell
    widgetEl.querySelectorAll('.w-table td, .w-table th').forEach(c => c.classList.remove('selected-cell'));
    const sel = widgetEl.querySelector(`.w-table [data-row="${nRow}"][data-col="${nCol}"]`);
    if (sel) sel.classList.add('selected-cell');
    renderTableCellPropPanel(widgetEl, nRow, nCol);
    // 點選任一格（含標題列與資料格）都自動 focus 屬性面板的文字內容欄，便於直接輸入標籤
    const inp = document.getElementById('cellTextInput');
    if (inp) { inp.focus(); inp.select(); }
}

function renderTableCellPropPanel(el, nRow, nCol) {
    const props = el.widgetProps;
    initArrCells(props);
    if (nRow >= props.arrCells.length || nCol >= props.arrCells[nRow].length) return;
    const cell = props.arrCells[nRow][nCol];
    const isHeader = (nRow === 0);

    let szHtml = `
        <div style="font-size:11px;color:#0d6efd;margin-bottom:8px;cursor:pointer;"
             onclick="renderPropPanel(selectedEl)">
            <i class="fas fa-arrow-left me-1"></i>${escHtml(t('designer.prop.cell_back'))}
        </div>
        <div style="font-size:10px;color:#888;margin-bottom:6px;">
            ${escHtml(t(isHeader ? 'designer.prop.cell_header_row' : 'designer.prop.cell_data_row'))} [${nRow}, ${nCol}]
        </div>
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.text_content'))}</label>
            <input type="text" id="cellTextInput" value="${escHtml(cell.szText || '')}"
                   oninput="setCellProp('szText', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.font_size'))}</label>
            <input type="number" value="${cell.nFontSize || 12}" min="8" max="24"
                   oninput="setCellProp('nFontSize', +this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.text_color'))}</label>
            <input type="color" value="${cell.szFontColor || (isHeader ? '#ffffff' : '#444444')}"
                   oninput="setCellProp('szFontColor', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.weight'))}</label>
            <select onchange="setCellProp('szFontWeight', this.value)">
                <option value="normal" ${(cell.szFontWeight||'normal')==='normal'?'selected':''}>${escHtml(t('designer.prop.weight.normal'))}</option>
                <option value="bold" ${cell.szFontWeight==='bold'?'selected':''}>${escHtml(t('designer.prop.weight.bold'))}</option>
                <option value="500" ${cell.szFontWeight==='500'?'selected':''}>${escHtml(t('designer.prop.weight.medium'))}</option>
            </select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.alignment'))}</label>
            <div class="btn-group btn-group-sm w-100" id="cellAlignGroup">
                ${['left', 'center', 'right'].map(a => `
                <button type="button" data-align="${a}"
                        class="btn ${(cell.szAlign || 'left') === a ? 'btn-primary' : 'btn-outline-secondary'}"
                        onclick="setCellAlign('${a}')">${escHtml(t('designer.prop.align.' + a))}</button>`).join('')}
            </div>
        </div>`;

    if (!isHeader && cell.nCircuitId != null) {
        // ─ 迴路指標 cell（plan 2026-07-23）：迴路名 + 指標下拉 + 解除綁定 ─
        const szMetric = cell.szMetric || 'day_kwh';
        const szMetricOptions = ['day_kwh', 'month_kwh', 'period_kwh', 'period_cost', 'demand_kw'].map(m =>
            `<option value="${m}" ${szMetric === m ? 'selected' : ''}>${escHtml(t('designer.metric.' + m))}</option>`
        ).join('');
        szHtml += `
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_circuit'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                <span style="font-size:11px;color:#69db7c;"><i class="fas fa-sitemap me-1"></i>${escHtml(cell.szCircuitName || ('#' + cell.nCircuitId))}</span>
                <button class="btn btn-outline-warning btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="openCellPointPicker(${nRow}, ${nCol})">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t('designer.prop.reselect'))}
                </button>
                <button class="btn btn-outline-danger btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="clearCellCircuit(${nRow}, ${nCol})">
                    <i class="fas fa-unlink me-1"></i>${escHtml(t('designer.prop.clear'))}
                </button>
            </div>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.circuit_metric'))}</label>
            <select onchange="setCellProp('szMetric', this.value)">${szMetricOptions}</select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.col_decimals'))}</label>
            <input type="number" value="${props.arrColDecimals[nCol] ?? ''}" min="0" max="6"
                   placeholder="${escHtml(t('designer.prop.col_decimals_placeholder'))}"
                   oninput="setColDecimals(${nCol}, this.value)">
        </div>`;
    } else if (!isHeader) {
        // 綁定點位
        const szSidLabel = cell.szSid
            ? `<span style="font-size:11px;color:#c8c8c8;">${escHtml(cell.szPointName || cell.szSid)}</span>`
            : `<span style="color:#888;font-size:11px;">${escHtml(t('designer.widget.unbound'))}</span>`;
        szHtml += `
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.bound_point'))}</label>
            <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${szSidLabel}
                <button class="btn btn-outline-warning btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="openCellPointPicker(${nRow}, ${nCol})">
                    <i class="fas fa-exchange-alt me-1"></i>${escHtml(t(cell.szSid ? 'designer.prop.reselect' : 'designer.prop.bind'))}
                </button>
                ${cell.szSid ? `<button class="btn btn-outline-danger btn-sm py-0 px-2" style="font-size:11px;"
                        onclick="clearCellSid(${nRow}, ${nCol})">
                    <i class="fas fa-unlink me-1"></i>${escHtml(t('designer.prop.clear'))}
                </button>` : ''}
            </div>
        </div>`;
        // 首欄以外才顯示點位屬性
        if (nCol > 0) {
            const szPT = cell.szPointType || 'AI';
            szHtml += `
            <div class="prop-group">
                <label>${escHtml(t('designer.prop.point_type'))}</label>
                <select onchange="setCellProp('szPointType', this.value)">
                    <option value="AI" ${szPT === 'AI' ? 'selected' : ''}>${escHtml(t('designer.prop.point_type.ai'))}</option>
                    <option value="DI" ${szPT === 'DI' ? 'selected' : ''}>${escHtml(t('designer.prop.point_type.di'))}</option>
                </select>
            </div>`;
            if (szPT === 'AI') {
                szHtml += `
                <div class="prop-group">
                    <label>${escHtml(t('designer.prop.col_decimals'))}</label>
                    <input type="number" value="${props.arrColDecimals[nCol] ?? ''}" min="0" max="6"
                           placeholder="${escHtml(t('designer.prop.col_decimals_placeholder'))}"
                           oninput="setColDecimals(${nCol}, this.value)">
                </div>
                <div class="prop-group">
                    <label>${escHtml(t('designer.prop.high_color'))}</label>
                    <input type="color" value="${cell.szHighColor || '#dc3545'}"
                           oninput="setCellProp('szHighColor', this.value)">
                </div>
                <div class="prop-group">
                    <label>${escHtml(t('designer.prop.low_color'))}</label>
                    <input type="color" value="${cell.szLowColor || '#fd7e14'}"
                           oninput="setCellProp('szLowColor', this.value)">
                </div>
                `;
            } else if (szPT === 'DI') {
                szHtml += `
                <div class="prop-group">
                    <label>${escHtml(t('designer.prop.on_label'))}</label>
                    <input type="text" value="${escHtml(cell.szOnLabel || 'ON')}"
                           oninput="setCellProp('szOnLabel', this.value)">
                </div>
                <div class="prop-group">
                    <label>${escHtml(t('designer.prop.off_label'))}</label>
                    <input type="text" value="${escHtml(cell.szOffLabel || 'OFF')}"
                           oninput="setCellProp('szOffLabel', this.value)">
                </div>
                <div class="prop-group">
                    <label>${escHtml(t('designer.prop.alarm_text_color'))}</label>
                    <input type="color" value="${cell.szAlarmColor || '#dc3545'}"
                           oninput="setCellProp('szAlarmColor', this.value)">
                </div>
                `;
            }
        }
    }

    // ─ 該欄寬（header）/ 該列高（資料列）─ plan 決策 1
    const szSizePh = escHtml(t('designer.prop.size_placeholder_default'));
    if (isHeader) {
        const arrCW = props.arrColWidths || [];
        const vCW   = (arrCW[nCol] != null) ? arrCW[nCol] : '';
        szHtml += `
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.col_width'))}</label>
            <input type="number" value="${vCW}" min="10" max="500"
                   placeholder="${szSizePh}"
                   oninput="setColWidth(${nCol}, this.value)">
        </div>`;
    } else {
        const nDataRow = nRow - 1; // arrRowHeights 只含資料列
        const arrRH = props.arrRowHeights || [];
        const vRH   = (arrRH[nDataRow] != null) ? arrRH[nDataRow] : '';
        szHtml += `
        <hr class="prop-divider">
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.row_height'))}</label>
            <input type="number" value="${vRH}" min="10" max="500"
                   placeholder="${szSizePh}"
                   oninput="setRowHeight(${nDataRow}, this.value)">
        </div>`;
    }

    document.getElementById('propBody').innerHTML = szHtml;
}

function setCellProp(szKey, val) {
    if (!selectedEl || nSelectedCellRow < 0) return;
    const props = selectedEl.widgetProps;
    if (!props.arrCells || nSelectedCellRow >= props.arrCells.length) return;
    // 字體大小、對齊、粗細：
    //   首列（標題列）改 → 整欄同步；首欄（資料列第 0 欄）改 → 整列同步
    //   其他格自己改 → 只改該格並記入 arrOwnKeys，之後首列/首欄同步會跳過它
    //   [0,0] 同時屬首列與首欄，依首列規則同步整欄
    const arrStyleKeys = ['nFontSize', 'szAlign', 'szFontWeight'];
    if (arrStyleKeys.includes(szKey)) {
        const nRow = nSelectedCellRow, nCol = nSelectedCellCol;
        const self = props.arrCells[nRow][nCol];
        self[szKey] = val;
        const _applyUnlessOwn = (cell) => {
            if (cell && !(cell.arrOwnKeys || []).includes(szKey)) cell[szKey] = val;
        };
        if (nRow === 0) {
            for (let ri = 1; ri < props.arrCells.length; ri++) _applyUnlessOwn(props.arrCells[ri][nCol]);
        } else if (nCol === 0) {
            const row = props.arrCells[nRow];
            for (let ci = 1; ci < row.length; ci++) _applyUnlessOwn(row[ci]);
        } else {
            if (!Array.isArray(self.arrOwnKeys)) self.arrOwnKeys = [];
            if (!self.arrOwnKeys.includes(szKey)) self.arrOwnKeys.push(szKey);
        }
    } else if (szKey === 'szPointType') {
        // 點位屬性 → 整欄同步（標題列與資料列各自獨立）
        const nCol = nSelectedCellCol;
        const isHeader = nSelectedCellRow === 0;
        for (let ri = 0; ri < props.arrCells.length; ri++) {
            // 標題列只改標題列，資料列只改資料列
            if (isHeader ? ri === 0 : ri > 0) {
                if (props.arrCells[ri][nCol]) {
                    props.arrCells[ri][nCol][szKey] = val;
                }
            }
        }
    } else {
        props.arrCells[nSelectedCellRow][nSelectedCellCol][szKey] = val;
    }
    // 表格 DI Cell ON/OFF 標籤修改時，同步到所有同 SID 的 DI Widget
    if ((szKey === 'szOnLabel' || szKey === 'szOffLabel')) {
        const cell = props.arrCells[nSelectedCellRow]?.[nSelectedCellCol];
        if (cell && cell.szSid && cell.szPointType === 'DI') {
            _syncDiLabelsToSid(cell.szSid, cell.szOnLabel, cell.szOffLabel, null);
        }
    }
    // 表格 AI Cell 上限/下限顏色修改時，同步到所有同 SID 的 AI Widget
    if ((szKey === 'szHighColor' || szKey === 'szLowColor')) {
        const cell = props.arrCells[nSelectedCellRow]?.[nSelectedCellCol];
        if (cell && cell.szSid && (cell.szPointType || 'AI') === 'AI') {
            _syncAiColorsToSid(cell.szSid, cell.szHighColor, cell.szLowColor, null);
        }
    }
    // 表格 DI Cell 警報顏色修改時，同步到所有同 SID 的 DI Widget
    if (szKey === 'szAlarmColor') {
        const cell = props.arrCells[nSelectedCellRow]?.[nSelectedCellCol];
        if (cell && cell.szSid && cell.szPointType === 'DI') {
            _syncDiAlarmColorToSid(cell.szSid, cell.szAlarmColor, null);
        }
    }
    renderWidget(selectedEl);
    // 重新高亮
    const sel = selectedEl.querySelector(`.w-table [data-row="${nSelectedCellRow}"][data-col="${nSelectedCellCol}"]`);
    if (sel) sel.classList.add('selected-cell');
    // 切換點位屬性或警報開關時重新渲染屬性面板
    const arrReRenderKeys = ['szPointType'];
    if (arrReRenderKeys.includes(szKey)) {
        renderTableCellPropPanel(selectedEl, nSelectedCellRow, nSelectedCellCol);
    }
}

// 對齊用按鈕而非 select：標題列預設已置中，select 再選同值不觸發 change，首列同步整欄就不會跑
function setCellAlign(szAlign) {
    setCellProp('szAlign', szAlign);
    document.querySelectorAll('#cellAlignGroup [data-align]').forEach(btn => {
        const isActive = btn.dataset.align === szAlign;
        btn.classList.toggle('btn-primary', isActive);
        btn.classList.toggle('btn-outline-secondary', !isActive);
    });
}

function setColDecimals(nCol, val) {
    if (!selectedEl) return;
    const props = selectedEl.widgetProps;
    initArrCells(props);
    props.arrColDecimals[nCol] = val === '' ? null : Math.max(0, Math.min(6, parseInt(val)));
    // 不需重新渲染，小數位數僅影響 ScadaPage 即時顯示
}

// 該欄寬度（空值=用 nDefaultColW）— 翻 bTableSizeLocked、重算 widget 大小
function setColWidth(nCol, val) {
    if (!selectedEl) return;
    const props = selectedEl.widgetProps;
    initArrCells(props);
    if (!Array.isArray(props.arrColWidths)) props.arrColWidths = [];
    const v = (val === '' || val == null) ? null : Math.max(10, Math.min(500, parseInt(val) || 0));
    props.arrColWidths[nCol] = v;
    props.bTableSizeLocked = true;
    const sz = computeTableWidgetSize(props);
    selectedEl.style.width  = sz.nW + 'px';
    selectedEl.style.height = sz.nH + 'px';
    syncSizeInputs(sz.nW, sz.nH);
    renderWidget(selectedEl);
    // 保留選中 cell 高亮
    const sel = selectedEl.querySelector(`.w-table [data-row="${nSelectedCellRow}"][data-col="${nSelectedCellCol}"]`);
    if (sel) sel.classList.add('selected-cell');
}

// 該列高度（資料列 0..nRows-1；空值=用 nDefaultRowH）
function setRowHeight(nDataRow, val) {
    if (!selectedEl) return;
    const props = selectedEl.widgetProps;
    initArrCells(props);
    if (!Array.isArray(props.arrRowHeights)) props.arrRowHeights = [];
    const v = (val === '' || val == null) ? null : Math.max(10, Math.min(500, parseInt(val) || 0));
    props.arrRowHeights[nDataRow] = v;
    props.bTableSizeLocked = true;
    const sz = computeTableWidgetSize(props);
    selectedEl.style.width  = sz.nW + 'px';
    selectedEl.style.height = sz.nH + 'px';
    syncSizeInputs(sz.nW, sz.nH);
    renderWidget(selectedEl);
    const sel = selectedEl.querySelector(`.w-table [data-row="${nSelectedCellRow}"][data-col="${nSelectedCellCol}"]`);
    if (sel) sel.classList.add('selected-cell');
}

function clearCellSid(nRow, nCol) {
    if (!selectedEl) return;
    const cell = selectedEl.widgetProps.arrCells[nRow][nCol];
    cell.szSid = '';
    cell.szPointName = '';
    renderWidget(selectedEl);
    const sel = selectedEl.querySelector(`.w-table [data-row="${nRow}"][data-col="${nCol}"]`);
    if (sel) sel.classList.add('selected-cell');
    renderTableCellPropPanel(selectedEl, nRow, nCol);
}

// 解除表格 cell 的迴路指標綁定（新鍵全 delete，JSON 無殘留；plan 2026-07-23）
function clearCellCircuit(nRow, nCol) {
    if (!selectedEl) return;
    const cell = selectedEl.widgetProps.arrCells[nRow][nCol];
    _clearCellCircuitKeys(cell);   // picker.js
    renderWidget(selectedEl);
    const sel = selectedEl.querySelector(`.w-table [data-row="${nRow}"][data-col="${nCol}"]`);
    if (sel) sel.classList.add('selected-cell');
    renderTableCellPropPanel(selectedEl, nRow, nCol);
}

// ============================================================
// 小數位數自動帶入（plan 2026-10-23-designer-auto-decimals）
// 規則單一真相在 js/common/decimal-rule.js；只在綁點位當下判斷一次
// ============================================================
function _applyAutoDecimals(props, szName, szUnit) {
    if (!window.DecimalRule || props.isDecimalAuto === false) return;
    props.nDecimalPlaces = window.DecimalRule.guess(szName, szUnit);
    props.isDecimalAuto  = true;
}

// 表格：位數是「每欄」設定 — 該欄仍為 null 時才依綁入的 AI 點位帶入，已設定不覆蓋
function _applyAutoColDecimals(props, nCol, cell, szName, szUnit) {
    if (!window.DecimalRule || !cell || (cell.szPointType || 'AI') !== 'AI') return;
    if (!props.arrColDecimals) props.arrColDecimals = [];
    if (props.arrColDecimals[nCol] != null) return;
    props.arrColDecimals[nCol] = window.DecimalRule.guess(szName, szUnit);
}

// ============================================================
// 依點位建立 widget（picker 確認後呼叫）
// ============================================================
function createGaugeWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['gauge'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'gauge';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    const fNewMin = point.fMin ?? 0;
    const fNewMax = point.fMax ?? 100;
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szSid:       point.szSid,
        szPointName: szFullName,
        szTitle:     szFullName,
        szUnit:  point.szUnit || '',
        fMin:    fNewMin,
        fMax:    fNewMax,
        fValue:  (fNewMin + fNewMax) / 2
    };

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createTrendChartWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['trendChart'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'trendChart';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szSid:       point.szSid,
        szPointName: szFullName,
        szTitle:     szFullName,
        szUnit:      point.szUnit || ''
    };

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createControlBtnWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['controlBtn'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'controlBtn';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szCid:       point.szSid,
        szPointName: szFullName,
        szTitle:     szFullName
    };

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createRealtimeValueWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['realtimeValue'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'realtimeValue';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szSid:       point.szSid,
        szPointName: szFullName,
        szTitle:     szFullName,
        szUnit:      point.szUnit || ''
    };
    _applyAutoDecimals(el.widgetProps, szFullName, point.szUnit);

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

// 以迴路指標建立 AI 點位 widget（picker「迴路」分頁確認後呼叫；plan 2026-07-23）
function createRealtimeValueWithCircuit(circuit, x, y) {
    const def  = WIDGET_DEFS['realtimeValue'];
    const szId = 'w' + (++nWidgetCounter);

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'realtimeValue';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = { ...getWidgetDefaultProps(el.dataset.type) };
    _bindRtValueToCircuitMetric(el.widgetProps, circuit);   // picker.js

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createDiPointWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['diPoint'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'diPoint';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szSid:       point.szSid,
        szPointName: szFullName,
        szTitle:     szFullName
    };
    // 繼承同 SID 已存在的 DI ON/OFF 標籤
    const diLabels = _findDiLabelsForSid(point.szSid);
    if (diLabels) {
        el.widgetProps.szOnLabel  = diLabels.szOnLabel;
        el.widgetProps.szOffLabel = diLabels.szOffLabel;
    }

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createDiPointWithSchedule(nScheduleId, szScheduleName, x, y) {
    const def  = WIDGET_DEFS['diPoint'];
    const szId = 'w' + (++nWidgetCounter);

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'diPoint';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szSid:          '',
        szPointName:    '',
        nScheduleId:    nScheduleId,
        szScheduleName: szScheduleName || '',
        szTitle:        szScheduleName || ''
    };

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createAoPointWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['aoPoint'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'aoPoint';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szCid:         point.szSid,
        szPointName:   szFullName,
        szTitle:       szFullName,
        szDisplayName: szFullName,
        szUnit:        point.szUnit || '',
        fMin:          point.fMin ?? 0,
        fMax:          point.fMax ?? 100
    };
    _applyAutoDecimals(el.widgetProps, szFullName, point.szUnit);

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

function createDoPointWithPoint(point, x, y) {
    const def  = WIDGET_DEFS['doPoint'];
    const szId = 'w' + (++nWidgetCounter);
    const szFullName = point.szDeviceLabel ? point.szDeviceLabel + ' / ' + point.szName : point.szName;

    const el = document.createElement('div');
    el.id           = szId;
    el.className    = 'canvas-widget';
    el.dataset.type = 'doPoint';
    el.style.left   = x + 'px';
    el.style.top    = y + 'px';
    el.style.width  = def.nDefaultW + 'px';
    el.style.height = def.nDefaultH + 'px';
    el.widgetProps  = {
        ...getWidgetDefaultProps(el.dataset.type),
        szCid:         point.szSid,
        szPointName:   szFullName,
        szTitle:       szFullName,
        szDisplayName: szFullName
    };

    renderWidget(el);
    canvas.appendChild(el);
    selectWidget(el);
}

// ============================================================
// 背景透明切換（gauge / controlBtn / realtimeValue / diPoint / aoPoint / doPoint / pump）
// ============================================================
function onGaugeBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('gaugeBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('gaugeBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

// 即時曲線「點數」提示文字（時間窗 ÷ 畫點間隔；觸及硬上限時改為警示色並說明實際間隔）。
// 與執行期共用 window.TrendWindow，避免面板說 360 點、實際畫 720 點。
function _buildTrendPointHint(props) {
    const nWinSec = props.nWindowSec || 1800;
    const nSmpSec = props.nSampleSec || 5;
    if (window.TrendWindow.isCapped(nWinSec, nSmpSec)) {
        const nEffSmp = window.TrendWindow.effectiveSampleSec(nWinSec, nSmpSec);
        return `<span style="color:#ffc107;"><i class="fas fa-exclamation-triangle me-1"></i>` +
               escHtml(t('designer.prop.trend.capped_hint', { 0: window.TrendWindow.MAX_POINTS, 1: nEffSmp })) + `</span>`;
    }
    return `<span style="color:#888;">` +
           escHtml(t('designer.prop.trend.point_count_hint', { 0: window.TrendWindow.pointCount(nWinSec, nSmpSec) })) + `</span>`;
}

// 只換提示文字，不重繪整個面板 —— 面板重繪會在使用者打字中途把 input 換掉、游標跳回開頭
function _renderTrendHintOnly(el) {
    const hint = document.getElementById('trendPtHint');
    if (hint) hint.innerHTML = _buildTrendPointHint(el.widgetProps);
}

function onTrendBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('trendBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('trendBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

function onCtrlBtnBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('ctrlBtnBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('ctrlBtnBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

// ============================================================
// AI 點位 — 顯示模式（即時值 / 當日累積 / 當月累積）
// 向後相容關鍵：realtime（預設）不寫任何新鍵，切回時把累積鍵全 delete，
// 讓既有頁面與新建元件的 szWidgetStateJson 完全不變。
// ============================================================
function buildRtValueModeHtml(props) {
    const szMode = props.szValueMode || 'realtime';
    const szSelStyle = `style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;
                               padding:4px 6px;font-size:12px;border-radius:3px;"`;
    let szHtml = `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.display_mode'))}</label>
            <select onchange="onRtValueModeChange(this.value)" ${szSelStyle}>
                <option value="realtime" ${szMode === 'realtime' ? 'selected' : ''}>${escHtml(t('designer.prop.value_mode.realtime'))}</option>
                <option value="day"      ${szMode === 'day'      ? 'selected' : ''}>${escHtml(t('designer.prop.value_mode.acc_day'))}</option>
                <option value="month"    ${szMode === 'month'    ? 'selected' : ''}>${escHtml(t('designer.prop.value_mode.acc_month'))}</option>
            </select>
        </div>`;
    if (szMode === 'realtime') return szHtml;

    const szKind = props.szAccKind || 'meter';
    szHtml += `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.acc_kind'))}</label>
            <select onchange="onRtValueAccKindChange(this.value)" ${szSelStyle}>
                <option value="meter"     ${szKind === 'meter'     ? 'selected' : ''}>${escHtml(t('designer.prop.acc_kind.meter'))}</option>
                <option value="integrate" ${szKind === 'integrate' ? 'selected' : ''}>${escHtml(t('designer.prop.acc_kind.integrate'))}</option>
            </select>
        </div>
        ${szKind === 'meter' ? `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.acc_max_value'))}</label>
            <input type="number" value="${props.dMaxValue != null ? props.dMaxValue : ''}" min="0" step="any"
                   oninput="setProp('dMaxValue', this.value === '' ? null : +this.value)">
        </div>` : ''}
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.acc_unit'))}</label>
            <input type="text" value="${escHtml(props.szAccUnit || '')}" placeholder="${escHtml(props.szUnit || '')}"
                   oninput="setProp('szAccUnit', this.value)">
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.acc_decimals'))}</label>
            <input type="number" value="${props.nAccDecimals != null ? props.nAccDecimals : 1}" min="0" max="4" step="1"
                   oninput="setProp('nAccDecimals', +this.value)">
        </div>`;
    return szHtml;
}

function onRtValueModeChange(szVal) {
    if (!selectedEl) return;
    const p = selectedEl.widgetProps;
    if (szVal === 'realtime') {
        delete p.szValueMode;
        delete p.szAccKind;
        delete p.dMaxValue;
        delete p.szAccUnit;
        delete p.nAccDecimals;
    } else {
        p.szValueMode = szVal;
        if (!p.szAccKind) p.szAccKind = 'meter';
        if (p.nAccDecimals == null) p.nAccDecimals = 1;
        // 綁定 SID 為迴路 kWh 讀值且溢位上限空白 → 非同步帶入 MaxKwh（picker.js）
        const el = selectedEl;
        tryFillCircuitMaxKwhAsync(p, () => {
            if (selectedEl === el) { renderWidget(el); renderPropPanel(el); }
        });
    }
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}

// ============================================================
// AI 點位 — 迴路指標模式（plan 2026-07-23：綁 EnergyCircuit 的四指標）
// 取代 SID 型的單位/顯示模式/上下限色區塊；小數位沿用 nAccDecimals 鍵
// ============================================================
function buildRtCircuitMetricHtml(props) {
    const szMetric = props.szMetric || 'day_kwh';
    const szSelStyle = `style="width:100%;background:#3c3c3c;border:1px solid #555;color:#d4d4d4;
                               padding:4px 6px;font-size:12px;border-radius:3px;"`;
    const szOptions = ['day_kwh', 'month_kwh', 'period_kwh', 'period_cost', 'demand_kw'].map(m =>
        `<option value="${m}" ${szMetric === m ? 'selected' : ''}>${escHtml(t('designer.metric.' + m))}</option>`
    ).join('');
    return `
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.circuit_metric'))}</label>
            <select onchange="onRtCircuitMetricChange(this.value)" ${szSelStyle}>${szOptions}</select>
        </div>
        <div class="prop-group">
            <label>${escHtml(t('designer.prop.acc_decimals'))}</label>
            <input type="number" value="${props.nAccDecimals != null ? props.nAccDecimals : 1}" min="0" max="4" step="1"
                   oninput="setProp('nAccDecimals', +this.value)">
        </div>`;
}

function onRtCircuitMetricChange(szVal) {
    if (!selectedEl) return;
    selectedEl.widgetProps.szMetric = szVal;
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}

function onRtValueAccKindChange(szVal) {
    if (!selectedEl) return;
    const p = selectedEl.widgetProps;
    p.szAccKind = szVal;
    if (szVal !== 'meter') delete p.dMaxValue;
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}

function onRtValBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('rtValBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('rtValBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

function onDiPointBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('diPointBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('diPointBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

function onAoPointBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('aoPointBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('aoPointBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

function onDoPointBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('doPointBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('doPointBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

// 設備型元件（pump / coolingTower / ahuFan / chiller）屬性面板頂部「重新指定設備」按鈕
// → picker.js rerouteEquipDevice()：停在設備葉節點、預覽各槽位命中、確認整台覆蓋
function _equipReassignBtnHtml() {
    return `<div class="prop-group">
        <button class="btn btn-outline-warning btn-sm w-100" style="font-size:11px;"
                onclick="rerouteEquipDevice()">
            <i class="fas fa-magic me-1"></i>${escHtml(t('designer.equip.reassign_btn'))}
        </button>
    </div>`;
}

function onPumpBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('pumpBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('pumpBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

function onPipeBgTransparentChange(isChecked) {
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        const picker = document.getElementById('pipeBgPicker');
        if (picker) picker.style.display = 'none';
    } else {
        const picker = document.getElementById('pipeBgPicker');
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}

// 馬達型設備（冷卻水塔 / 空調箱風扇 / 冰機）— 取消單一綁定列（清 SID + 名稱）
function clearMotorBinding(szSidKey, szNameKey) {
    if (!selectedEl) return;
    const szType = selectedEl.dataset.type;
    if (szType !== 'coolingTower' && szType !== 'ahuFan' && szType !== 'chiller') return;
    selectedEl.widgetProps[szSidKey]  = '';
    selectedEl.widgetProps[szNameKey] = '';
    renderWidget(selectedEl);
    renderPropPanel(selectedEl);
}

// 馬達型設備（冷卻水塔 / 空調箱風扇 / 冰機）共用 — picker id 由呼叫端傳入
function onMotorBgTransparentChange(szPickerId, isChecked) {
    const picker = document.getElementById(szPickerId);
    if (isChecked) {
        setProp('szBgColor', 'transparent');
        if (picker) picker.style.display = 'none';
    } else {
        const szColor = picker ? picker.value : '#ffffff';
        setProp('szBgColor', szColor);
        if (picker) picker.style.display = '';
    }
}
