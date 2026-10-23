// ============================================================
// scadapage/render.js — 渲染分派器（renderScadaWidget）+ 1 秒輪詢更新（fetchAndUpdateGauges / updateScadaWidgets）
// ============================================================
// 純搬移自 scadapage.js（plan 2026-08-06-scadapage-js-split）。
// 不包 IIFE、global scope，與 js/designer/ 拆分同模式；
// 共享狀態集中於 state.js。原始 4-space 縮排刻意保留，
// 以保證 template literal 內容 byte-level 不變（純搬移原則）。
// ============================================================

    function renderScadaWidget(canvas, ws) {
        var el = document.createElement('div');
        el.style.cssText =
            'position:absolute;left:' + ws.nX + 'px;top:' + ws.nY + 'px;' +
            'width:' + ws.nW + 'px;height:' + ws.nH + 'px;overflow:hidden;';
        el.dataset.type = ws.szType;

        if (ws.szType === 'gauge') {
            var p = ws.props || {};
            el.dataset.sid     = p.szSid    || '';
            el.dataset.fMin    = p.fMin     || 0;
            el.dataset.fMax    = p.fMax     || 100;
            el.dataset.szUnit  = p.szUnit   || '';
            el.dataset.szColor     = p.szColor     || '#00c0ff';
            el.dataset.szTitle     = p.szTitle     || '';
            el.dataset.szBgColor   = p.szBgColor   || 'transparent';
            el.dataset.szHighColor = p.szHighColor || '#dc3545';
            el.dataset.szLowColor  = p.szLowColor  || '#fd7e14';
            el.classList.add('scada-gauge');
            el.innerHTML = buildGaugeHtml(p);
            if (p.szSid) el.addEventListener('contextmenu', function (ev) { onTrendContextMenu(ev, el.dataset.sid); });
        } else if (ws.szType === 'table') {
            el.classList.add('scada-table');
            // 鎖定尺寸的 table 改用 props 重算 widget 外框，確保與 Designer 一致（plan 2026-06-01）
            var tProps = ws.props || {};
            if (tProps.bTableSizeLocked === true) {
                var nTC = Math.max(1, tProps.nCols || 3);
                var nTR = Math.max(1, tProps.nRows || 5);
                var nTDefW = tProps.nDefaultColW || 80;
                var nTDefH = tProps.nDefaultRowH || 20;
                var arrTCW = tProps.arrColWidths || [];
                var arrTRH = tProps.arrRowHeights || [];
                var nNetW = 0;
                for (var iC = 0; iC < nTC; iC++) nNetW += (arrTCW[iC] != null ? +arrTCW[iC] : nTDefW);
                var nNetH = nTDefH; // header row
                for (var iR = 0; iR < nTR; iR++) nNetH += (arrTRH[iR] != null ? +arrTRH[iR] : nTDefH);
                el.style.width  = nNetW + 'px';
                el.style.height = nNetH + 'px';
            }
            el.innerHTML = buildTableHtml(tProps);
            el.addEventListener('contextmenu', function (ev) {
                var td = ev.target.closest('td[data-sid]');
                if (td && td.dataset.sid) onTrendContextMenu(ev, td.dataset.sid);
            });
        } else if (ws.szType === 'text') {
            var p = ws.props || {};
            var szFontStyle = p.isItalic ? 'italic' : 'normal';
            var szBg        = p.szBgColor || 'transparent';
            el.style.overflow = 'visible';
            el.style.background = 'transparent';
            el.innerHTML = '<div style="' +
                'width:100%;height:100%;' +
                'display:flex;align-items:center;justify-content:center;' +
                'font-family:' + (p.szFontFamily || 'inherit') + ';' +
                'font-size:' + (p.nFontSize || 18) + 'px;' +
                'color:' + (p.szFontColor || '#212529') + ';' +
                'font-weight:' + (p.szFontWeight || 'normal') + ';' +
                'font-style:' + szFontStyle + ';' +
                'background:' + szBg + ';' +
                'word-break:break-word;' +
                'text-align:center;' +
                'padding:4px 8px;' +
                'box-sizing:border-box;' +
                'line-height:1.3;' +
                '">' + escViewHtml(p.szText || '') + '</div>';
        } else if (ws.szType === 'controlBtn') {
            var p = ws.props || {};
            el.dataset.cid        = p.szCid       || '';
            el.dataset.szTitle    = p.szTitle      || '\u63a7\u5236';
            el.dataset.szBtnLabel = p.szBtnLabel   || '\u57f7\u884c';
            el.dataset.szBtnIcon  = p.szBtnIcon    || 'fa-hand-pointer';
            el.dataset.fCtrlValue = p.fCtrlValue   || 1;
            el.dataset.szBtnColor = p.szBtnColor   || '#198754';
            el.classList.add('scada-ctrl-btn');
            el.style.overflow = 'visible';
            el.innerHTML = buildCtrlBtnHtml(p);
            if (_canControlPage(scadaCurrentId)) {
                el.querySelector('.ctrl-btn-exec')?.addEventListener('click', function () { onControlBtnClick(el); });
            }
        } else if (ws.szType === 'realtimeValue') {
            var p = ws.props || {};
            el.dataset.sid             = p.szSid            || '';
            el.dataset.szUnit          = p.szUnit           || '';
            // 舊頁面無 nDecimalPlaces → 沿用 2 位，外觀不變
            el.dataset.nDecimalPlaces  = p.nDecimalPlaces != null ? p.nDecimalPlaces : 2;
            el.dataset.szTitle         = p.szTitle          || '';
            el.dataset.nFontSize       = p.nFontSize        || 28;
            el.dataset.szFontColor     = p.szFontColor      || '#212529';
            el.dataset.szBgColor       = p.szBgColor        || 'transparent';
            el.dataset.szHighColor     = p.szHighColor      || '#dc3545';
            el.dataset.szLowColor      = p.szLowColor       || '#fd7e14';
            var szValueMode = p.szValueMode || 'realtime';
            if (p.nCircuitId != null) {
                // 迴路指標模式（plan 2026-07-23）：掛 scada-rt-cmetric（不掛 scada-rt-value / scada-rt-acc），
                // 1 秒即時迴圈與 30 秒累積輪詢都不觸碰，由 30 秒迴路指標輪詢負責更新
                var szMetric = p.szMetric || 'day_kwh';
                el.dataset.circuitId   = p.nCircuitId;
                el.dataset.metric      = szMetric;
                el.dataset.circuitName = p.szCircuitName || '';
                el.dataset.accDecimals = (p.nAccDecimals != null ? p.nAccDecimals : 1);
                el.classList.add('scada-rt-cmetric');
                el.innerHTML = buildRealtimeValueViewHtml({
                    nFontSize: p.nFontSize || 28, szFontColor: p.szFontColor || '#212529',
                    szUnit: _cmetricUnit(szMetric),
                    szTitle: _cmetricTooltipTitle(p.szCircuitName || '', szMetric, false),
                    szBgColor: p.szBgColor || 'transparent'
                }, '--');
            } else if (szValueMode === 'day' || szValueMode === 'month') {
                // 累積模式：掛 scada-rt-acc（不掛 scada-rt-value），
                // 既有 1 秒即時迴圈自然跳過，由 30 秒累積輪詢負責更新
                el.dataset.valueMode   = szValueMode;
                el.dataset.accKind     = p.szAccKind || 'meter';
                if (p.dMaxValue != null) el.dataset.maxValue = p.dMaxValue;
                el.dataset.accUnit     = p.szAccUnit || '';
                el.dataset.accDecimals = (p.nAccDecimals != null ? p.nAccDecimals : 1);
                el.classList.add('scada-rt-acc');
                el.innerHTML = buildRealtimeValueViewHtml({
                    nFontSize: p.nFontSize || 28, szFontColor: p.szFontColor || '#212529',
                    szUnit: p.szAccUnit || p.szUnit || '',
                    szTitle: _accTooltipTitle(p.szTitle || '', szValueMode),
                    szBgColor: p.szBgColor || 'transparent'
                }, '--');
            } else {
                el.classList.add('scada-rt-value');
                el.innerHTML = buildRealtimeValueViewHtml(p, '--');
            }
            el.style.overflow = 'visible';
            if (p.szSid) el.addEventListener('contextmenu', function (ev) { onTrendContextMenu(ev, el.dataset.sid); });
        } else if (ws.szType === 'diPoint') {
            var p = ws.props || {};
            var bSchedule = p.nScheduleId != null;
            el.dataset.szDisplayMode  = p.szDisplayMode  || 'indicator';
            el.dataset.szOnColor      = p.szOnColor      || '#28a745';
            el.dataset.szOffColor     = p.szOffColor     || '#6c757d';
            el.dataset.szOnLabel      = p.szOnLabel      || 'ON';
            el.dataset.szOffLabel     = p.szOffLabel     || 'OFF';
            el.dataset.nIndicatorSize = p.nIndicatorSize || 28;
            el.dataset.nFontSize      = p.nFontSize      || 24;
            el.dataset.szFontColor    = p.szFontColor    || '#212529';
            el.dataset.szBgColor      = p.szBgColor      || 'transparent';
            el.dataset.szTitle        = p.szTitle        || '';
            el.dataset.szAlarmColor   = p.szAlarmColor   || '#dc3545';
            if (bSchedule) {
                el.dataset.scheduleId   = String(p.nScheduleId);
                el.dataset.scheduleName = p.szScheduleName || '';
                el.classList.add('scada-di-schedule');
                el.style.overflow = 'visible';
                el.innerHTML = buildDiPointViewHtml(p, null);
            } else {
                el.dataset.sid            = p.szSid          || '';
                el.classList.add('scada-di-point');
                el.style.overflow = 'visible';
                el.innerHTML = buildDiPointViewHtml(p, null);
                if (p.szSid) el.addEventListener('contextmenu', function (ev) { onTrendContextMenu(ev, el.dataset.sid); });
            }
        } else if (ws.szType === 'aoPoint') {
            var p = ws.props || {};
            el.dataset.cid               = p.szCid              || '';
            el.dataset.sid               = p.szCid              || '';
            el.dataset.szTitle           = p.szTitle             || 'AO \u9ede\u4f4d';
            el.dataset.szDisplayName     = p.szDisplayName       || p.szTitle || 'AO \u9ede\u4f4d';
            el.dataset.szUnit            = p.szUnit              || '';
            el.dataset.fWriteValue       = p.fWriteValue         || 0;
            el.dataset.fMin              = p.fMin                || 0;
            el.dataset.fMax              = p.fMax                || 100;
            el.dataset.fStep             = p.fStep               || 1;
            el.dataset.nDecimalPlaces    = p.nDecimalPlaces != null ? p.nDecimalPlaces : 2;
            el.dataset.nFontSize         = p.nFontSize           || 16;
            el.dataset.szFontColor       = p.szFontColor         || '#ffffff';
            el.dataset.szMenuManualLabel = p.szMenuManualLabel   || '\u624b\u52d5\u63a7\u5236';
            el.dataset.szMenuAutoLabel   = p.szMenuAutoLabel     || '\u81ea\u52d5\u63a7\u5236';
            el.dataset.szBgColor         = p.szBgColor           || 'transparent';
            el.classList.add('scada-ao-point');
            el.style.overflow = 'visible';
            el.innerHTML = buildAoPointViewHtml(p);
            if (_canControlPage(scadaCurrentId)) {
                el.addEventListener('contextmenu', function (e) { onAoPointContextMenu(e, el); });
            }
        } else if (ws.szType === 'doPoint') {
            var p = ws.props || {};
            el.dataset.cid            = p.szCid            || '';
            el.dataset.szTitle        = p.szTitle           || 'DO \u9ede\u4f4d';
            el.dataset.szDisplayName  = p.szDisplayName     || p.szTitle || 'DO \u9ede\u4f4d';
            el.dataset.nOnValue       = p.nOnValue          || 1;
            el.dataset.nOffValue      = p.nOffValue         || 0;
            el.dataset.nFontSize      = p.nFontSize         || 16;
            el.dataset.szFontColor    = p.szFontColor       || '#212529';
            el.dataset.szMenuOnLabel  = p.szMenuOnLabel  || '';
            el.dataset.szMenuOffLabel = p.szMenuOffLabel || '';
            el.dataset.szMenuAutoLabel = p.szMenuAutoLabel || '';
            el.dataset.szBgColor      = p.szBgColor         || 'transparent';
            el.classList.add('scada-do-point');
            el.style.overflow = 'visible';
            el.innerHTML = buildDoPointViewHtml(p);
            if (_canControlPage(scadaCurrentId)) {
                el.addEventListener('contextmenu', function (e) { onDoPointContextMenu(e, el); });
            }
        } else if (ws.szType === 'pump') {
            var p = ws.props || {};
            el.dataset.sidRun        = p.szSidRun        || '';
            el.dataset.sidFault      = p.szSidFault      || '';
            el.dataset.sidMode       = p.szSidMode       || '';
            el.dataset.sidFreq       = p.szSidFreq       || '';
            el.dataset.cidStartStop  = p.szCidStartStop  || '';
            el.dataset.cidFreqSet    = p.szCidFreqSet    || '';
            el.dataset.nFreqSetMin   = p.nFreqSetMin     || 0;
            el.dataset.nFreqSetMax   = p.nFreqSetMax     || 60;
            el.dataset.szTitle       = p.szTitle          || '\u6c34\u6cf5';
            el.dataset.szRunColor    = p.szRunColor       || '#28a745';
            el.dataset.szStopColor   = p.szStopColor      || '#6c757d';
            el.dataset.szFaultColor  = p.szFaultColor     || '#dc3545';
            el.dataset.szManualColor = p.szManualColor    || '#ffc107';
            el.dataset.szAutoColor   = p.szAutoColor      || '#0d6efd';
            el.dataset.szOutletDir   = p.szOutletDir      || 'right';
            el.dataset.szBgColor     = p.szBgColor        || 'transparent';
            el.dataset.nFreqMax      = p.nFreqMax         || 60;
            el.classList.add('scada-pump');
            el.style.overflow = 'visible';
            el.innerHTML = buildPumpViewHtml(p, 'stop', '', '');
            el.addEventListener('contextmenu', function (e) { onPumpContextMenu(e, el); });
        } else if (ws.szType === 'pipe') {
            var p = ws.props || {};
            el.dataset.sid         = p.szSid       || '';
            el.dataset.bindMode    = p.szBindMode  || '';
            el.dataset.fThreshold  = (p.fThreshold != null ? p.fThreshold : 0);
            el.dataset.szCompare   = p.szCompare   || 'gt';
            el.dataset.szOrient    = p.szOrient    || 'h';
            el.dataset.nThickness  = p.nThickness  || 8;
            el.dataset.szFlowColor = p.szFlowColor || '#0d6efd';
            el.dataset.szStopColor = p.szStopColor || '#adb5bd';
            el.dataset.szBadColor  = p.szBadColor  || '#6c757d';
            el.dataset.nSpeed      = p.nSpeed      || 3;
            el.dataset.szDir       = p.szDir       || 'fwd';
            el.dataset.szBgColor   = p.szBgColor   || 'transparent';
            el.dataset.szTitle     = p.szTitle     || '';
            // 折線節點（新格式）；舊存檔無 arrPoints → builder 依 szOrient + 寬高推導直管
            if (p.arrPoints && p.arrPoints.length >= 2) el.dataset.arrPoints = JSON.stringify(p.arrPoints);
            el.classList.add('scada-pipe');
            el.style.overflow = 'visible';
            // 未綁定 → 純裝飾固定流動；已綁定 → 初始靜止，待 polling 更新
            el.innerHTML = buildPipeViewHtml(p, p.szBindMode ? 'stop' : 'flow', '', ws.nW, ws.nH);
            if (p.szSid) el.addEventListener('contextmenu', function (ev) { onTrendContextMenu(ev, el.dataset.sid); });
        } else if (ws.szType === 'coolingTower' || ws.szType === 'ahuFan' || ws.szType === 'chiller') {
            // 馬達型設備（冷卻水塔 / 空調箱風扇 / 冰機）— 仿水泵，共用 MotorEquip 圖形
            var p = ws.props || {};
            el.dataset.motorType    = ws.szType;
            el.dataset.sidRun       = p.szSidRun       || '';
            el.dataset.sidFault     = p.szSidFault     || '';
            el.dataset.sidMode      = p.szSidMode      || '';
            el.dataset.sidFreq      = p.szSidFreq      || '';
            el.dataset.sidLoad      = p.szSidLoad      || '';
            el.dataset.sidWaterTemp = p.szSidWaterTemp || '';
            el.dataset.sidChwOut    = p.szSidChwOut    || '';
            el.dataset.cidStartStop = p.szCidStartStop || '';
            el.dataset.cidFreqSet   = p.szCidFreqSet   || '';
            el.dataset.cidSetTemp   = p.szCidSetTemp   || '';
            el.dataset.nFreqSetMin  = p.nFreqSetMin    || 0;
            el.dataset.nFreqSetMax  = p.nFreqSetMax    || 60;
            el.dataset.nFreqMax     = p.nFreqMax       || 60;
            el.dataset.nLoadMax     = p.nLoadMax       || 100;
            el.dataset.szTitle      = p.szTitle || (ws.szType === 'chiller' ? '冰機' : ws.szType === 'coolingTower' ? '冷卻水塔' : '空調箱風扇');
            el.dataset.szRunColor   = p.szRunColor    || '#28a745';
            el.dataset.szStopColor  = p.szStopColor   || '#6c757d';
            el.dataset.szFaultColor = p.szFaultColor  || '#dc3545';
            el.dataset.szManualColor = p.szManualColor || '#ffc107';
            el.dataset.szAutoColor  = p.szAutoColor   || '#0d6efd';
            el.dataset.szBgColor    = p.szBgColor     || 'transparent';
            el.classList.add('scada-motor');
            el.style.overflow = 'visible';
            el.innerHTML = _buildMotorViewHtml(p, ws.szType, 'stop', '', '', '');
            el.addEventListener('contextmenu', function (e) { onMotorContextMenu(e, el); });
            if (ws.szType === 'chiller') {
                el.addEventListener('dblclick', function (ev) {
                    if (!ev.target.closest('.chiller-settemp')) return;
                    if (!_canControlPage(scadaCurrentId)) return;
                    _motorPromptSetTemp(el);
                });
            }
        } else if (ws.szType === 'trendChart') {
            // 即時曲線（滾動趨勢圖）— 這裡只佈好容器與 canvas，真正的 Chart 實例由
            // renderScadaCanvas 在整批 widget 都 append 完之後統一 initTrendCharts() 建立
            // （Chart.js 需要 canvas 已在 DOM 內才量得到容器尺寸）
            var p = ws.props || {};
            el.id = 'scadaTrend' + (++_nTrendSeq);
            el.dataset.sid = p.szSid || '';
            el.classList.add('scada-trend');
            el._trendProps = p;
            var szTrendBg     = (p.szBgColor && p.szBgColor !== 'transparent') ? p.szBgColor : 'transparent';
            var szTrendBorder = p.szBorderColor || '#000000';
            el.innerHTML =
                '<div class="scada-trend-box" style="background:' + szTrendBg + ';' +
                    'border:1px solid ' + szTrendBorder + ';">' +
                    '<div class="scada-trend-plot"><canvas class="scada-trend-canvas"></canvas></div>' +
                    '<div class="scada-trend-unbound" style="display:none;">' +
                        '<i class="fas fa-unlink me-1"></i>' + escViewHtml(t('scadapage.trend.unbound')) +
                    '</div>' +
                '</div>';
            if (p.szSid) el.addEventListener('contextmenu', function (ev) { onTrendContextMenu(ev, el.dataset.sid); });
        } else if (ws.szType === 'image') {
            // 圖片/動畫（自訂 GIF）— 綁定條件成立播 GIF、不成立顯示靜止圖（plan 2026-09-18）
            var p = ws.props || {};
            el.dataset.animSrc    = p.szAnimSrc  || '';
            el.dataset.stillSrc   = p.szStillSrc || '';
            el.dataset.fit        = p.szFit      || 'contain';
            el.dataset.szBgColor  = p.szBgColor  || 'transparent';
            el.dataset.bindMode   = p.szBindMode || '';
            el.dataset.sid        = p.szSid      || '';
            el.dataset.fThreshold = (p.fThreshold != null ? p.fThreshold : 0);
            el.dataset.szCompare  = p.szCompare  || 'gt';
            el.classList.add('scada-image');
            // 未綁定 → 純裝飾固定播放；已綁定 → 初始靜止，待 polling 更新
            var szImgInit = p.szBindMode ? 'stop' : 'run';
            el.dataset._imgState = szImgInit;
            el.innerHTML = ImageWidget.build(p, szImgInit, '');
            if (p.szSid) el.addEventListener('contextmenu', function (ev) { onTrendContextMenu(ev, el.dataset.sid); });
        }
        canvas.appendChild(el);
    }

    // ── 即時資料輪詢與 Widget 更新 ──
    async function fetchAndUpdateGauges() {
        try {
            var resp = await fetch('/api/realtime/latest');
            if (!resp.ok) {
                console.warn('\u5373\u6642\u8cc7\u6599 API \u56de\u61c9\u7570\u5e38\uff1a', resp.status, resp.statusText);
                return;
            }
            var result = await resp.json();
            if (result.success && result.data) {
                lastData = result.data;
                updateScadaWidgets(result.data);
            }
        } catch (err) {
            console.warn('\u5373\u6642\u8cc7\u6599\u53d6\u5f97\u5931\u6557\uff1a', err.message);
        }
    }

    // AI 點位小數位數（Designer 綁點時依 DecimalRule 寫入；舊頁面無值 → 2）
    function _rtDecimals(el) {
        var n = parseInt(el.dataset.nDecimalPlaces, 10);
        return isNaN(n) ? 2 : Math.max(0, Math.min(6, n));
    }

    function updateScadaWidgets(data) {
        var sidMap = {};
        var sidValueMap = {};
        var sidQualityMap = {};
        var sidIsAutoMap = {};
        var sidTsMap = {};          // 點位自身時間戳（即時曲線去重用，見 widget-trend.js）
        var sidNameMap = {};        // 點位名稱（hover 標籤 fallback 用）
        data.forEach(function (item) {
            if (item.sid) {
                sidValueMap[item.sid] = item.value;
                sidQualityMap[item.sid] = (item.quality || '').toUpperCase();
                sidTsMap[item.sid] = item.timestamp;
                if (item.name) sidNameMap[item.sid] = item.name;
                if (item.value !== '--' && item.value !== null && item.value !== undefined) {
                    var fParsed = parseFloat(item.value);
                    if (!isNaN(fParsed)) sidMap[item.sid] = fParsed;
                }
                // 同步手動/自動旗標：null 代表非控制點位，不收進 map
                if (item.isAuto === true || item.isAuto === false) {
                    sidIsAutoMap[item.sid] = item.isAuto;
                    // 同步 _aoManualValueMap 讓右鍵選單高亮狀態與 polling 一致
                    if (_aoManualValueMap[item.sid]) {
                        _aoManualValueMap[item.sid].isAuto = item.isAuto;
                    } else {
                        _aoManualValueMap[item.sid] = { value: 0, isAuto: item.isAuto };
                    }
                }
            }
        });

        function isBadQuality(sid) {
            return sidQualityMap[sid] === 'BAD';
        }

        // hover 標籤文字：設計時填的標題優先，未填改用點位名稱（讓每個綁點元件滑過都看得到點名）
        function hoverTitle(el, sid) {
            return el.dataset.szTitle || sidNameMap[sid] || '';
        }

        // 更新 Gauge
        document.querySelectorAll('.scada-gauge[data-sid]').forEach(function (el) {
            var sid = el.dataset.sid;
            if (!sid || !(sid in sidValueMap)) return;

            var _rule = _alarmRuleMap[sid];
            var _gHighColor = el.dataset.szHighColor || '#dc3545';
            var _gLowColor  = el.dataset.szLowColor  || '#fd7e14';
            function getGaugeAlarmColor(fVal) {
                if (!_rule) return null;
                // 有前置條件 → 看 Engine 實際 active 警報（前端不知道閘門 / 延遲狀態）
                if (_ruleUsesActiveAlarm(_rule)) {
                    if (_rule.isAlarmHigh && _isActiveAlarm(sid, 'high')) return _gHighColor;
                    if (_rule.isAlarmLow  && _isActiveAlarm(sid, 'low'))  return _gLowColor;
                    return null;
                }
                if (_rule.isAlarmHigh && fVal >= ((_rule.dAlarmHighValue || 80) - (_rule.dDeadbandHigh || 0))) return _gHighColor;
                if (_rule.isAlarmLow  && fVal <= ((_rule.dAlarmLowValue  || 20) + (_rule.dDeadbandLow  || 0))) return _gLowColor;
                return null;
            }

            if (isBadQuality(sid)) {
                el.innerHTML = buildGaugeHtml({
                    fValue: 0, fMin: parseFloat(el.dataset.fMin) || 0, fMax: parseFloat(el.dataset.fMax) || 100,
                    szUnit: el.dataset.szUnit || '', szColor: el.dataset.szColor || '#00c0ff',
                    szTitle: hoverTitle(el, sid), szBgColor: el.dataset.szBgColor || 'transparent', isOffline: true
                });
            } else if (sidMap[sid] !== undefined) {
                var fVal = sidMap[sid];
                el.innerHTML = buildGaugeHtml({
                    fValue: fVal, fMin: parseFloat(el.dataset.fMin) || 0, fMax: parseFloat(el.dataset.fMax) || 100,
                    szUnit: el.dataset.szUnit || '', szColor: el.dataset.szColor || '#00c0ff',
                    szTitle: hoverTitle(el, sid), szBgColor: el.dataset.szBgColor || 'transparent',
                    szAlarmOverride: getGaugeAlarmColor(fVal)
                });
            }
        });

        // 更新即時數值
        document.querySelectorAll('.scada-rt-value[data-sid]').forEach(function (el) {
            var sid = el.dataset.sid;
            if (!sid) return;

            var _rtRule = _alarmRuleMap[sid];
            var _szHighColor = el.dataset.szHighColor || '#dc3545';
            var _szLowColor  = el.dataset.szLowColor  || '#fd7e14';
            function getAlarmColor(fVal) {
                if (!_rtRule) return null;
                if (_ruleUsesActiveAlarm(_rtRule)) {
                    if (_rtRule.isAlarmHigh && _isActiveAlarm(sid, 'high')) return _szHighColor;
                    if (_rtRule.isAlarmLow  && _isActiveAlarm(sid, 'low'))  return _szLowColor;
                    return null;
                }
                if (_rtRule.isAlarmHigh && fVal >= ((_rtRule.dAlarmHighValue || 80) - (_rtRule.dDeadbandHigh || 0))) return _szHighColor;
                if (_rtRule.isAlarmLow  && fVal <= ((_rtRule.dAlarmLowValue  || 20) + (_rtRule.dDeadbandLow  || 0))) return _szLowColor;
                return null;
            }

            if (isBadQuality(sid)) {
                el.innerHTML = buildRealtimeValueViewHtml({
                    nFontSize: parseInt(el.dataset.nFontSize) || 28, szFontColor: '#dc3545',
                    szUnit: '', szTitle: hoverTitle(el, sid), szBgColor: el.dataset.szBgColor || 'transparent'
                }, '\u65b7\u7dda');
            } else if (sidMap[sid] !== undefined) {
                var fVal = sidMap[sid];
                var szAlmClr = getAlarmColor(fVal);
                el.innerHTML = buildRealtimeValueViewHtml({
                    nFontSize: parseInt(el.dataset.nFontSize) || 28,
                    szFontColor: szAlmClr || (el.dataset.szFontColor || '#212529'),
                    szUnit: el.dataset.szUnit || '', szTitle: hoverTitle(el, sid),
                    szBgColor: el.dataset.szBgColor || 'transparent'
                }, Number(fVal).toFixed(_rtDecimals(el)));
            } else if (sidValueMap[sid] !== undefined) {
                el.innerHTML = buildRealtimeValueViewHtml({
                    nFontSize: parseInt(el.dataset.nFontSize) || 28,
                    szFontColor: el.dataset.szFontColor || '#212529',
                    szUnit: el.dataset.szUnit || '', szTitle: hoverTitle(el, sid),
                    szBgColor: el.dataset.szBgColor || 'transparent'
                }, '--');
            }
        });

        // 更新 DI 點位 — 排程綁定
        document.querySelectorAll('.scada-di-schedule[data-schedule-id]').forEach(function (el) {
            var nSchId = parseInt(el.dataset.scheduleId, 10);
            if (isNaN(nSchId)) return;
            var sch = (_scheduleCache || []).find(function (s) { return s.nId === nSchId; });
            var szSchName = (sch && sch.szName) ? sch.szName : (el.dataset.scheduleName || '');
            // 排程已刪除或停用 → 紅字提示（依使用者回覆 + plan 驗收條件）
            if (!sch || !sch.isEnabled) {
                var szTooltipNF = szSchName
                    ? '<div class="scada-hover-label" style="display:none;position:absolute;top:100%;margin-top:4px;left:50%;' +
                            'transform:translateX(-50%);background:rgba(33,37,41,.85);color:#fff;' +
                            'font-size:11px;padding:2px 8px;border-radius:4px;white-space:nowrap;' +
                            'pointer-events:none;z-index:25;">' +
                        escViewHtml(t('scadapage.di.schedule_label')) + ': ' + escViewHtml(szSchName) + '</div>'
                    : '';
                el.innerHTML = '<div style="position:relative;width:100%;height:100%;display:flex;' +
                    'align-items:center;justify-content:center;">' +
                    '<span style="font-size:13px;color:#dc3545;font-weight:700;">' +
                    escViewHtml(t('scadapage.di.schedule_not_found')) + '</span>' +
                    szTooltipNF + '</div>';
                return;
            }
            var bIsOn = (window.ScheduleEval && window.ScheduleEval.evalScheduleNow)
                ? window.ScheduleEval.evalScheduleNow(nSchId, 'contact_no', _scheduleCache)
                : null;
            el.innerHTML = buildDiPointViewHtml({
                szDisplayMode: el.dataset.szDisplayMode || 'indicator',
                szOnColor: el.dataset.szOnColor || '#28a745', szOffColor: el.dataset.szOffColor || '#6c757d',
                szOnLabel: el.dataset.szOnLabel || 'ON', szOffLabel: el.dataset.szOffLabel || 'OFF',
                nIndicatorSize: parseInt(el.dataset.nIndicatorSize) || 28,
                nFontSize: parseInt(el.dataset.nFontSize) || 24,
                szBgColor: el.dataset.szBgColor || 'transparent', szTitle: el.dataset.szTitle || '',
                szScheduleName: szSchName,
                isAlarmEnabled: false,
                szAlarmColor: el.dataset.szAlarmColor || '#dc3545'
            }, bIsOn === true);
        });

        // 更新 DI 點位
        document.querySelectorAll('.scada-di-point[data-sid]').forEach(function (el) {
            var sid = el.dataset.sid;
            if (!sid) return;

            if (isBadQuality(sid)) {
                el.innerHTML = '<div style="position:relative;width:100%;height:100%;display:flex;' +
                    'align-items:center;justify-content:center;">' +
                    '<span style="font-size:13px;color:#dc3545;font-weight:700;">\u65b7\u7dda</span></div>';
            } else if (sidValueMap[sid] !== undefined && sidValueMap[sid] !== '--') {
                var raw = sidValueMap[sid];
                var bIsOn = (raw === 1 || raw === '1' || raw === true || raw === 'true'
                    || (typeof raw === 'string' && raw.toUpperCase() === 'ON')
                    || parseFloat(raw) >= 1);
                var _diRule = _alarmRuleMap[sid];
                // 有前置條件 → 以實際 active 警報決定是否著警報色（觸發狀態對齊目前狀態即可上色）
                var _diUseActive = _ruleUsesActiveAlarm(_diRule);
                el.innerHTML = buildDiPointViewHtml({
                    szDisplayMode: el.dataset.szDisplayMode || 'indicator',
                    szOnColor: el.dataset.szOnColor || '#28a745', szOffColor: el.dataset.szOffColor || '#6c757d',
                    szOnLabel: el.dataset.szOnLabel || 'ON', szOffLabel: el.dataset.szOffLabel || 'OFF',
                    nIndicatorSize: parseInt(el.dataset.nIndicatorSize) || 28,
                    nFontSize: parseInt(el.dataset.nFontSize) || 24,
                    szBgColor: el.dataset.szBgColor || 'transparent', szTitle: hoverTitle(el, sid),
                    isAlarmEnabled: _diUseActive ? (!!_diRule.isDiAlarm && _isActiveAlarm(sid, 'di')) : (_diRule?.isDiAlarm || false),
                    szAlarmTrigger: _diUseActive ? (bIsOn ? 'ON' : 'OFF') : (_diRule?.szDiTriggerState || 'ON'),
                    szAlarmColor: el.dataset.szAlarmColor || '#dc3545'
                }, bIsOn);
            }
        });

        // 更新水泵
        document.querySelectorAll('.scada-pump').forEach(function (el) {
            if (_pumpGaugeDrag && _pumpGaugeDrag.el === el) return;

            var sidRun   = el.dataset.sidRun   || '';
            var sidFault = el.dataset.sidFault || '';
            var sidMode  = el.dataset.sidMode  || '';
            var sidFreq  = el.dataset.sidFreq  || '';
            if (!sidRun && !sidFault && !sidMode && !sidFreq) return;

            if (sidRun && isBadQuality(sidRun)) {
                el.innerHTML = '<div style="position:relative;width:100%;height:100%;display:flex;' +
                    'align-items:center;justify-content:center;">' +
                    '<span style="font-size:13px;color:#dc3545;font-weight:700;">\u65b7\u7dda</span></div>';
                return;
            }

            var bIsRunning = false;
            if (sidRun && sidValueMap[sidRun] !== undefined && sidValueMap[sidRun] !== '--') {
                var raw = sidValueMap[sidRun];
                bIsRunning = (raw === 1 || raw === '1' || raw === true || raw === 'true'
                    || (typeof raw === 'string' && raw.toUpperCase() === 'ON')
                    || parseFloat(raw) >= 1);
            }

            var bIsFault = false;
            if (sidFault && sidValueMap[sidFault] !== undefined && sidValueMap[sidFault] !== '--') {
                var rawF = sidValueMap[sidFault];
                bIsFault = (rawF === 1 || rawF === '1' || rawF === true || rawF === 'true'
                    || parseFloat(rawF) >= 1);
            }

            var szState = bIsFault ? 'fault' : bIsRunning ? 'run' : 'stop';
            var szFreqVal = (sidFreq && sidValueMap[sidFreq] !== undefined) ? sidValueMap[sidFreq] : '';
            var szModeVal = (sidMode && sidValueMap[sidMode] !== undefined) ? sidValueMap[sidMode] : '';

            var szStateKey = szState + '|' + szModeVal;
            if (el.dataset._pumpKey !== szStateKey) {
                el.dataset._pumpKey = szStateKey;
                el.innerHTML = buildPumpViewHtml({
                    szTitle: el.dataset.szTitle || '\u6c34\u6cf5',
                    szRunColor: el.dataset.szRunColor || '#28a745', szStopColor: el.dataset.szStopColor || '#6c757d',
                    szFaultColor: el.dataset.szFaultColor || '#dc3545', szManualColor: el.dataset.szManualColor || '#ffc107',
                    szAutoColor: el.dataset.szAutoColor || '#0d6efd', szOutletDir: el.dataset.szOutletDir || 'right',
                    szBgColor: el.dataset.szBgColor || 'transparent', nFreqMax: parseFloat(el.dataset.nFreqMax) || 60,
                    szSidFreq: sidFreq, szSidMode: sidMode,
                    szCidStartStop: el.dataset.cidStartStop || '', szCidFreqSet: el.dataset.cidFreqSet || ''
                }, szState, szFreqVal, szModeVal);
            } else if (sidFreq) {
                var nFreqMax = parseFloat(el.dataset.nFreqMax) || 60;
                var fFreq = (szFreqVal !== '' && szFreqVal !== '--') ? parseFloat(szFreqVal) : 0;
                var fRatio = Math.max(0, Math.min(1, fFreq / (nFreqMax || 1)));
                var nBarTop = 22, nBarH = 68;
                var nFillH = Math.round(nBarH * fRatio);
                var nFillY = nBarTop + nBarH - nFillH;
                var szFreqText = (szFreqVal !== '' && szFreqVal !== '--')
                    ? parseFloat(szFreqVal).toFixed(1) + ' Hz' : '';
                var fillRect = el.querySelector('.pump-gauge-fill');
                if (fillRect) { fillRect.setAttribute('y', nFillY); fillRect.setAttribute('height', nFillH); }
                var textEl = el.querySelector('.pump-gauge-text');
                if (textEl) { textEl.textContent = szFreqText; textEl.setAttribute('y', nFillY + nFillH / 2 + 3); }
            }
        });

        // 更新馬達型設備（冷卻水塔 / 空調箱風扇 / 冰機）
        document.querySelectorAll('.scada-motor').forEach(function (el) {
            if (_pumpGaugeDrag && _pumpGaugeDrag.el === el) return;   // 頻率條拖曳中略過

            var szType = el.dataset.motorType;
            var bVfd   = (szType !== 'chiller');
            var sidRun     = el.dataset.sidRun   || '';
            var sidFault   = el.dataset.sidFault || '';
            var sidMode    = el.dataset.sidMode  || '';
            var sidPrimary = bVfd ? (el.dataset.sidFreq || '') : (el.dataset.sidLoad || '');
            var sidExtra   = bVfd ? (szType === 'coolingTower' ? (el.dataset.sidWaterTemp || '') : '')
                                  : (el.dataset.sidChwOut || '');
            if (!sidRun && !sidFault && !sidMode && !sidPrimary) return;

            if (sidRun && isBadQuality(sidRun)) {
                // 斷線：圖照畫（停止灰），上方紅字「斷線」；hover 仍浮出「標題 — 斷線」
                if (el.dataset._motorKey !== 'bad') {
                    el.dataset._motorKey = 'bad';
                    el.innerHTML = _buildMotorViewHtml(_motorPropsFromEl(el), szType, 'stop', '', '', '', true);
                }
                return;
            }

            var bIsRunning = false;
            if (sidRun && sidValueMap[sidRun] !== undefined && sidValueMap[sidRun] !== '--') {
                var mraw = sidValueMap[sidRun];
                bIsRunning = (mraw === 1 || mraw === '1' || mraw === true || mraw === 'true'
                    || (typeof mraw === 'string' && mraw.toUpperCase() === 'ON') || parseFloat(mraw) >= 1);
            }
            var bIsFault = false;
            if (sidFault && sidValueMap[sidFault] !== undefined && sidValueMap[sidFault] !== '--') {
                var mrawF = sidValueMap[sidFault];
                bIsFault = (mrawF === 1 || mrawF === '1' || mrawF === true || mrawF === 'true' || parseFloat(mrawF) >= 1);
            }

            var szState = bIsFault ? 'fault' : bIsRunning ? 'run' : 'stop';
            var szPrimaryVal = (sidPrimary && sidValueMap[sidPrimary] !== undefined) ? sidValueMap[sidPrimary] : '';
            var szModeVal = (sidMode && sidValueMap[sidMode] !== undefined) ? sidValueMap[sidMode] : '';

            // 額外監控溫度（僅 hover tooltip 顯示）
            var szExtraTip = '';
            if (sidExtra && sidMap[sidExtra] !== undefined) {
                var info = _findPointInfo(sidExtra);
                szExtraTip = Number(sidMap[sidExtra]).toFixed(1) + (info.unit || '°C');
            }

            var szMotorKey = szState + '|' + szModeVal;
            if (el.dataset._motorKey !== szMotorKey) {
                el.dataset._motorKey = szMotorKey;
                el.innerHTML = _buildMotorViewHtml(_motorPropsFromEl(el), szType, szState, szPrimaryVal, szModeVal, szExtraTip);
            } else if (sidPrimary) {
                // 僅更新主數值條 fill/text（VFD=右側直條調 y/height；冰機=底部橫條調 width，文字定位固定）
                var nMotMax = bVfd ? (parseFloat(el.dataset.nFreqMax) || 60) : (parseFloat(el.dataset.nLoadMax) || 100);
                var fMotVal = (szPrimaryVal !== '' && szPrimaryVal !== '--') ? parseFloat(szPrimaryVal) : 0;
                var fMotRatio = Math.max(0, Math.min(1, fMotVal / (nMotMax || 1)));
                var szMotText = (szPrimaryVal !== '' && szPrimaryVal !== '--')
                    ? (bVfd ? parseFloat(szPrimaryVal).toFixed(1) + ' Hz' : parseFloat(szPrimaryVal).toFixed(0) + ' %') : '';
                var mFill = el.querySelector('.pump-gauge-fill');
                var mText = el.querySelector('.pump-gauge-text');
                if (bVfd) {
                    var nMBarTop = 22, nMBarH = 68;
                    var nMFillH = Math.round(nMBarH * fMotRatio);
                    var nMFillY = nMBarTop + nMBarH - nMFillH;
                    if (mFill) { mFill.setAttribute('y', nMFillY); mFill.setAttribute('height', nMFillH); }
                    if (mText) { mText.textContent = szMotText; mText.setAttribute('y', nMFillY + nMFillH / 2 + 3); }
                } else {
                    var nMFillW = Math.round((MotorEquip.CHILLER_BAR_W || 66) * fMotRatio);
                    if (mFill) mFill.setAttribute('width', nMFillW);
                    if (mText) mText.textContent = szMotText;
                }
            }
        });

        // 更新管路流動元件
        document.querySelectorAll('.scada-pipe').forEach(function (el) {
            var szBindMode = el.dataset.bindMode || '';
            if (!szBindMode) return;   // 未綁定 → 純裝飾固定流動，不更新
            var sid = el.dataset.sid || '';
            if (!sid) return;

            var szState;
            var szValueText = '';
            if (isBadQuality(sid)) {
                szState = 'bad';
            } else if (szBindMode === 'di') {
                var raw = sidValueMap[sid];
                if (raw === undefined || raw === '--') return;
                var bIsOn = (raw === 1 || raw === '1' || raw === true || raw === 'true'
                    || (typeof raw === 'string' && raw.toUpperCase() === 'ON')
                    || parseFloat(raw) >= 1);
                szState = bIsOn ? 'flow' : 'stop';
            } else {   // analog：越過閾值才流動
                if (sidMap[sid] === undefined) return;
                var fVal = sidMap[sid];
                var fThr = parseFloat(el.dataset.fThreshold) || 0;
                var bFlow = (el.dataset.szCompare === 'gte') ? (fVal >= fThr) : (fVal > fThr);
                szState = bFlow ? 'flow' : 'stop';
                szValueText = Number(fVal).toFixed(2);
            }

            var szKey = szState + '|' + szValueText;
            if (el.dataset._pipeKey === szKey) return;
            el.dataset._pipeKey = szKey;
            var arrPipePts = null;
            try { arrPipePts = el.dataset.arrPoints ? JSON.parse(el.dataset.arrPoints) : null; } catch (_) { }
            el.innerHTML = buildPipeViewHtml({
                szOrient: el.dataset.szOrient, nThickness: el.dataset.nThickness,
                szFlowColor: el.dataset.szFlowColor, szStopColor: el.dataset.szStopColor,
                szBadColor: el.dataset.szBadColor, nSpeed: el.dataset.nSpeed,
                szDir: el.dataset.szDir, szBgColor: el.dataset.szBgColor,
                szTitle: hoverTitle(el, sid), arrPoints: arrPipePts
            }, szState, szValueText, parseInt(el.style.width), parseInt(el.style.height));
        });

        // 更新圖片/動畫元件（依綁定狀態切 GIF / 靜止圖）
        document.querySelectorAll('.scada-image').forEach(function (el) {
            var szBindMode = el.dataset.bindMode || '';
            if (!szBindMode) return;   // 未綁定 → 純裝飾固定播放，不更新
            var sid = el.dataset.sid || '';
            if (!sid) return;

            var szState;
            if (isBadQuality(sid)) {
                szState = 'bad';
            } else if (szBindMode === 'di') {
                var raw = sidValueMap[sid];
                if (raw === undefined || raw === '--') return;
                var bIsOn = (raw === 1 || raw === '1' || raw === true || raw === 'true'
                    || (typeof raw === 'string' && raw.toUpperCase() === 'ON')
                    || parseFloat(raw) >= 1);
                szState = bIsOn ? 'run' : 'stop';
            } else {   // analog：越過閾值才動
                if (sidMap[sid] === undefined) return;
                var fVal = sidMap[sid];
                var fThr = parseFloat(el.dataset.fThreshold) || 0;
                var bRun = (el.dataset.szCompare === 'gte') ? (fVal >= fThr) : (fVal > fThr);
                szState = bRun ? 'run' : 'stop';
            }

            // 狀態不變不重繪（避免每秒換 innerHTML 讓 GIF 重播閃爍）
            if (el.dataset._imgState === szState) return;
            el.dataset._imgState = szState;
            el.innerHTML = ImageWidget.build({
                szAnimSrc: el.dataset.animSrc, szStillSrc: el.dataset.stillSrc,
                szFit: el.dataset.fit, szBgColor: el.dataset.szBgColor
            }, szState, '');
        });

        // 更新表格 SID 儲存格
        document.querySelectorAll('.scada-table td[data-sid]').forEach(function (td) {
            var sid = td.dataset.sid;
            if (!sid) return;
            var szPT = td.dataset.pointType || 'AI';
            if (!td.title && sidNameMap[sid]) td.title = sidNameMap[sid];   // 儲存格 hover 顯示點位名稱
            var szOrigColor = td.dataset.origColor || '#444';

            if (isBadQuality(sid)) {
                td.textContent = '\u65b7\u7dda';
                td.style.color = '#dc3545';
                return;
            }

            if (szPT === 'DI') {
                if (sidValueMap[sid] === undefined || sidValueMap[sid] === '--') return;
                var raw = sidValueMap[sid];
                var bIsOn = (raw === 1 || raw === '1' || raw === true || raw === 'true'
                    || (typeof raw === 'string' && raw.toUpperCase() === 'ON')
                    || parseFloat(raw) >= 1);
                td.textContent = bIsOn ? (td.dataset.szOnLabel || 'ON') : (td.dataset.szOffLabel || 'OFF');

                var _tdDiRule = _alarmRuleMap[sid];
                var _tdDiAlarmColor = td.dataset.szAlarmColor || '#dc3545';
                if (_tdDiRule?.isDiAlarm) {
                    var isDiAlarming = _ruleUsesActiveAlarm(_tdDiRule)
                        ? _isActiveAlarm(sid, 'di')
                        : (_tdDiRule.szDiTriggerState === 'ON' && bIsOn)
                          || (_tdDiRule.szDiTriggerState === 'OFF' && !bIsOn);
                    td.style.color = isDiAlarming ? _tdDiAlarmColor : szOrigColor;
                } else {
                    td.style.color = szOrigColor;
                }
            } else {
                var nDecimals = parseInt(td.dataset.decimals);
                if (sidMap[sid] !== undefined) {
                    var fVal = sidMap[sid];
                    td.textContent = isNaN(nDecimals) ? Number(fVal).toFixed(2) : Number(fVal).toFixed(nDecimals);

                    var _tdAiRule = _alarmRuleMap[sid];
                    var _tdHighColor = td.dataset.szHighColor || '#dc3545';
                    var _tdLowColor  = td.dataset.szLowColor  || '#fd7e14';
                    var szColor = szOrigColor;
                    if (_tdAiRule && _ruleUsesActiveAlarm(_tdAiRule)) {
                        if (_tdAiRule.isAlarmHigh && _isActiveAlarm(sid, 'high')) szColor = _tdHighColor;
                        else if (_tdAiRule.isAlarmLow && _isActiveAlarm(sid, 'low')) szColor = _tdLowColor;
                    } else if (_tdAiRule) {
                        if (_tdAiRule.isAlarmHigh && fVal >= ((_tdAiRule.dAlarmHighValue || 80) - (_tdAiRule.dDeadbandHigh || 0))) {
                            szColor = _tdHighColor;
                        } else if (_tdAiRule.isAlarmLow && fVal <= ((_tdAiRule.dAlarmLowValue || 20) + (_tdAiRule.dDeadbandLow || 0))) {
                            szColor = _tdLowColor;
                        }
                    }
                    td.style.color = szColor;
                } else if (sidValueMap[sid] !== undefined) {
                    td.textContent = sidValueMap[sid];
                    td.style.color = szOrigColor;
                }
            }
        });

        // 更新即時曲線：append 新點（以點位自身 timestamp 去重）後統一推進時間窗、重繪。
        // 穩態下這是曲線唯一的資料來源 —— backfill 只在 widget 掛載時做一次，不週期查 DB。
        document.querySelectorAll('.scada-trend[data-sid]').forEach(function (el) {
            var sid = el.dataset.sid;
            if (!sid) return;
            pushTrendPoint(el.id, sidTsMap[sid], sidValueMap[sid], sidQualityMap[sid]);
        });
        tickTrendCharts();
        _quickTrendOnData(sidTsMap, sidValueMap, sidQualityMap);

        // 更新所有控制元件（AO/DO/Pump x2）右上角的 M 角標
        // 必須放在 pump rerender 之後，因 pump 換 innerHTML 會新建 badge DOM
        document.querySelectorAll('.scada-mode-badge[data-cid]').forEach(function (badge) {
            var cid = badge.dataset.cid;
            if (!cid) return;
            var ia = sidIsAutoMap[cid];
            badge.style.display = (ia === false) ? 'block' : 'none';
        });
    }
