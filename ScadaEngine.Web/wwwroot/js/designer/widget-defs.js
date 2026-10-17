// ============================================================
// Designer — Widget 定義 + Builder HTML
// ============================================================
// 內容：WIDGET_DEFS 大物件、9 種 widget 的 buildXxxHtml、
// renderTextWidget、表格儲存格輔助函式。
// 依賴：state.js（escHtml / hasAlarmRule / isDarkColor）
// ============================================================

// ============================================================
// Widget 定義
// ============================================================
// 注意：defaultProps 內的 szTitle/szLabel/szText 等預設值會在 createWidget 時
// 透過 widget-core.js 的 getWidgetDefaultProps() 以當前 culture 解析（plan 決策 1）
const WIDGET_DEFS = {
    table: {
        szLabel: '表格',
        szIcon: 'fas fa-table',
        // table widget 大小由結構決定（computeTableWidgetSize）— nDefaultW/H 僅作為 fallback
        nDefaultW: 360,
        nDefaultH: 150,
        nMinW: 120, nMinH: 60,
        defaultProps: {
            szTitle: '__i18n__:designer.default.table_title',
            nRows: 5,
            nCols: 6,
            szHeaderColor: '#343a40',
            // ─ 底色樣式（plan 2026-07-04）：null = 沿用現行渲染（Designer CSS 斑馬紋、執行期白底）─
            szBodyBgOdd: null,           // 奇數資料列底色
            szBodyBgEven: null,          // 偶數資料列底色
            szBorderColor: null,         // 資料列分隔線色
            arrCells: null,
            arrColDecimals: null,
            // ─ 表格大小由結構決定（plan 2026-06-01）─
            nDefaultRowH: 30,            // 預設列高（含 header）
            nDefaultColW: 100,           // 預設欄寬
            arrColWidths: null,          // 長度 = nCols，null = 用 nDefaultColW
            arrRowHeights: null,         // 長度 = nRows（僅資料列；header 永遠用 nDefaultRowH）
            bTableSizeLocked: false      // false = 沿用舊 width/height（舊檔遷移）；true = 由 computeTableWidgetSize 接管
        }
    },
    gauge: {
        szLabel: '儀錶板',
        szIcon: 'fas fa-tachometer-alt',
        nDefaultW: 220,
        nDefaultH: 175,
        nMinW: 100, nMinH: 80,
        defaultProps: {
            szTitle:     '__i18n__:designer.default.gauge_title',
            szSid:       '',
            szPointName: '',
            fValue:  50.0,
            fMin:    0.0,
            fMax:    100.0,
            szUnit:  '°C',
            szColor: '#0d6efd',
            szBgColor: 'transparent',
            szHighColor: '#dc3545',
            szLowColor:  '#fd7e14'
        }
    },
    trendChart: {
        szLabel: '即時曲線',
        szIcon: 'fas fa-chart-line',
        nDefaultW: 420,
        nDefaultH: 240,
        nMinW: 200, nMinH: 120,
        defaultProps: {
            szTitle:       '__i18n__:designer.default.trendChart_title',
            szSid:         '',
            szPointName:   '',
            szUnit:        '',
            nWindowSec:    1800,        // 時間窗：近 30 分鐘
            nSampleSec:    5,           // 畫點間隔（秒）→ 360 點
            fYMin:         null,        // null = 自動（Chart.js autoscale + 10% grace）
            fYMax:         null,
            nGridCount:    5,           // Y 軸區隔線數（Chart.js ticks.count）
            szLineColor:   '#0d6efd',
            nLineWidth:    2,
            szBgColor:     '#ffffff',
            szBorderColor: '#000000',
            szGridColor:   '#cccccc',
            isShowLegend:  false
        }
    },
    text: {
        szLabel: '文字',
        szIcon: 'fas fa-font',
        nDefaultW: 160,
        nDefaultH: 60,
        nMinW: 40, nMinH: 20,
        defaultProps: {
            szTitle:      '__i18n__:designer.default.text_title',
            szText:       '__i18n__:designer.default.text_content',
            szFontFamily: 'inherit',
            szFontColor:  '#212529',
            nFontSize:    14,
            szFontWeight: 'normal',
            isItalic:     false,
            szBgColor:    'transparent'
        }
    },
    controlBtn: {
        szLabel: '控制按鈕',
        szIcon: 'fas fa-toggle-on',
        nDefaultW: 100,
        nDefaultH: 38,
        nMinW: 60, nMinH: 20,
        defaultProps: {
            szTitle:     '__i18n__:designer.default.controlBtn_title',
            szCid:       '',
            szPointName: '',
            szBtnLabel:  '__i18n__:designer.default.controlBtn_label',
            szBtnIcon:   'fa-hand-pointer',
            fCtrlValue:  1,
            szBtnColor:  '#198754',
            szBgColor:   'transparent',
            nFontSize:   14
        }
    },
    realtimeValue: {
        szLabel: 'AI 點位',
        szIcon: 'fas fa-digital-tachograph',
        nDefaultW: 100,
        nDefaultH: 20,
        nMinW: 60, nMinH: 20,
        defaultProps: {
            szTitle:     '__i18n__:designer.default.realtimeValue_title',
            szSid:       '',
            szPointName: '',
            szUnit:      '',
            nFontSize:   14,
            szFontColor: '#212529',
            szBgColor:   'transparent',
            szHighColor: '#dc3545',
            szLowColor:  '#fd7e14'
        }
    },
    diPoint: {
        szLabel: 'DI 點位',
        szIcon: 'fas fa-circle',
        nDefaultW: 100,
        nDefaultH: 20,
        nMinW: 60, nMinH: 20,
        defaultProps: {
            szTitle:        '__i18n__:designer.default.diPoint_title',
            szSid:          '',
            szPointName:    '',
            // 排程綁定（互斥於 szSid，任一時刻僅一個有值；plan 決策 2）
            nScheduleId:    null,
            szScheduleName: '',
            szDisplayMode:  'indicator',   // 'indicator' = 燈號, 'text' = 文字
            szOnColor:      '#28a745',
            szOffColor:     '#6c757d',
            szOnLabel:      'ON',
            szOffLabel:     'OFF',
            nIndicatorSize: 14,
            nFontSize:      14,
            szFontColor:    '#212529',
            szBgColor:      'transparent',
            szAlarmColor:   '#dc3545'
        }
    },
    aoPoint: {
        szLabel: 'AO 點位',
        szIcon: 'fas fa-sliders-h',
        nDefaultW: 100,
        nDefaultH: 20,
        nMinW: 60, nMinH: 20,
        defaultProps: {
            szTitle:            '__i18n__:designer.default.aoPoint_title',
            szDisplayName:      '',
            szCid:              '',
            szPointName:        '',
            szUnit:             '',
            fWriteValue:        0,
            fMin:               0,
            fMax:               100,
            fStep:              1,
            nDecimalPlaces:     2,
            nFontSize:          14,
            szFontColor:        '#ffffff',
            szMenuManualLabel:  '__i18n__:designer.default.aoPoint_menu_manual',
            szMenuAutoLabel:    '__i18n__:designer.default.aoPoint_menu_auto',
            szBgColor:          'transparent',
            szBlockColor:       '#0d6efd'
        }
    },
    doPoint: {
        szLabel: 'DO 點位',
        szIcon: 'fas fa-toggle-on',
        nDefaultW: 100,
        nDefaultH: 20,
        nMinW: 60, nMinH: 20,
        defaultProps: {
            szTitle:          '__i18n__:designer.default.doPoint_title',
            szDisplayName:    '',
            szCid:            '',
            szPointName:      '',
            szOnLabel:        'ON',
            szOffLabel:       'OFF',
            szOnColor:        '#28a745',
            szOffColor:       '#dc3545',
            nFontSize:        14,
            szFontColor:      '#ffffff',
            nOnValue:         1,
            nOffValue:        0,
            szMenuOnLabel:    '__i18n__:designer.default.doPoint_menu_on',
            szMenuOffLabel:   '__i18n__:designer.default.doPoint_menu_off',
            szMenuAutoLabel:  '__i18n__:designer.default.doPoint_menu_auto',
            szBgColor:        'transparent',
            szBlockColor:     '#0d6efd'
        }
    },
    pump: {
        szLabel: '水泵',
        szIcon: 'fas fa-fan',
        nDefaultW: 130,
        nDefaultH: 120,
        nMinW: 60, nMinH: 60,
        defaultProps: {
            szTitle:          '__i18n__:designer.default.pump_title',
            // SID 點位（唯讀監控）
            szSidRun:         '',   szRunName:       '',   // 運轉狀態
            szSidFault:       '',   szFaultName:     '',   // 故障狀態
            szSidMode:        '',   szModeName:      '',   // 手自動狀態
            szSidFreq:        '',   szFreqName:      '',   // 頻率
            // CID 點位（控制寫入）
            szCidStartStop:   '',   szStartStopName: '',   // 啟動停止
            szCidFreqSet:     '',   szFreqSetName:   '',   // 頻率設定
            nFreqSetMin:      0,
            nFreqSetMax:      60,
            // 外觀
            nFreqMax:         60,
            szManualColor:    '#ffc107',
            szAutoColor:      '#0d6efd',
            szRunColor:       '#28a745',
            szStopColor:      '#6c757d',
            szFaultColor:     '#dc3545',
            szOutletDir:      'right',
            szBgColor:        'transparent'
        }
    },
    pipe: {
        szLabel: '管路',
        szIcon: 'fas fa-grip-lines',
        nDefaultW: 160,
        nDefaultH: 24,
        nMinW: 24, nMinH: 12,
        defaultProps: {
            szTitle:      '__i18n__:designer.default.pipe_title',
            // 綁定（二擇一互斥；szBindMode 為單一真相，決定哪種綁定生效）
            szBindMode:   '',          // '' 未綁 | 'di' | 'analog'
            szSid:        '',
            szPointName:  '',
            fThreshold:   0,           // 僅 analog 模式：越過此值才流動
            szCompare:    'gt',        // 'gt'（>）| 'gte'（>=）
            // 外觀
            szOrient:     'h',         // 相容欄位（舊直管 'h'|'v'）；折線化後不再寫入
            arrPoints:    null,        // 正交折線節點 [{x,y},...]（相對 widget 左上角 px；null=舊格式待推導）
            nThickness:   8,           // 管線粗細 px
            szFlowColor:  '#0d6efd',   // 流動色
            szStopColor:  '#adb5bd',   // 停止色（管身底色）
            szBadColor:   '#6c757d',   // 斷線色
            nSpeed:       3,           // 流速檔 1..5
            szDir:        'fwd',       // 'fwd' 正向 | 'rev' 逆向
            szBgColor:    'transparent'
        }
    },
    coolingTower: {
        szLabel: '冷卻水塔',
        szIcon: 'fas fa-fan',
        nDefaultW: 130,
        nDefaultH: 130,
        nMinW: 60, nMinH: 60,
        defaultProps: {
            szTitle:          '__i18n__:designer.default.coolingTower_title',
            // SID 點位（唯讀監控）
            szSidRun:         '',   szRunName:       '',
            szSidFault:       '',   szFaultName:     '',
            szSidMode:        '',   szModeName:      '',
            szSidFreq:        '',   szFreqName:      '',   // 風扇頻率
            szSidWaterTemp:   '',   szWaterTempName: '',   // 出水溫（僅 tooltip）
            // CID 點位（控制寫入）
            szCidStartStop:   '',   szStartStopName: '',
            szCidFreqSet:     '',   szFreqSetName:   '',
            nFreqSetMin:      0,
            nFreqSetMax:      60,
            nFreqMax:         60,
            szManualColor:    '#ffc107',
            szAutoColor:      '#0d6efd',
            szRunColor:       '#28a745',
            szStopColor:      '#6c757d',
            szFaultColor:     '#dc3545',
            szBgColor:        'transparent'
        }
    },
    ahuFan: {
        szLabel: '空調箱風扇',
        szIcon: 'fas fa-wind',
        nDefaultW: 130,
        nDefaultH: 120,
        nMinW: 60, nMinH: 60,
        defaultProps: {
            szTitle:          '__i18n__:designer.default.ahuFan_title',
            szSidRun:         '',   szRunName:       '',
            szSidFault:       '',   szFaultName:     '',
            szSidMode:        '',   szModeName:      '',
            szSidFreq:        '',   szFreqName:      '',
            szCidStartStop:   '',   szStartStopName: '',
            szCidFreqSet:     '',   szFreqSetName:   '',
            nFreqSetMin:      0,
            nFreqSetMax:      60,
            nFreqMax:         60,
            szManualColor:    '#ffc107',
            szAutoColor:      '#0d6efd',
            szRunColor:       '#28a745',
            szStopColor:      '#6c757d',
            szFaultColor:     '#dc3545',
            szBgColor:        'transparent'
        }
    },
    image: {
        szLabel: '圖片/動畫',
        szIcon: 'fas fa-film',
        nDefaultW: 120,
        nDefaultH: 120,
        nMinW: 24, nMinH: 24,
        defaultProps: {
            szTitle:     '__i18n__:designer.default.image_title',
            // 兩張圖來源（URL；內建圖庫走靜態路徑、上傳走 /Designer/asset/{hash}）
            szAnimSrc:   '',           // 動畫圖（GIF）— 綁定條件成立時顯示
            szStillSrc:  '',           // 靜止圖 — 綁定條件不成立時顯示（決策 2：選項 a）
            // 綁定（沿用管路 szBindMode 模型；'' = 純裝飾固定播放）
            szBindMode:  '',           // '' 未綁 | 'di' | 'analog'
            szSid:       '',
            szPointName: '',
            fThreshold:  0,            // 僅 analog：越過此值才動
            szCompare:   'gt',         // 'gt'（>）| 'gte'（>=）
            // 外觀
            szFit:       'contain',    // object-fit：contain | cover | fill
            szBgColor:   'transparent'
        }
    },
    chiller: {
        szLabel: '冰機',
        szIcon: 'fas fa-snowflake',
        nDefaultW: 130,
        nDefaultH: 135,
        nMinW: 70, nMinH: 60,
        defaultProps: {
            szTitle:          '__i18n__:designer.default.chiller_title',
            // SID 點位（唯讀監控）
            szSidRun:         '',   szRunName:       '',
            szSidFault:       '',   szFaultName:     '',
            szSidMode:        '',   szModeName:      '',   // 冰機語意=遠端/現場（0=現場→控制箱面板深黃）
            szSidLoad:        '',   szLoadName:      '',   // 負載%（底部橫條）
            szSidChwOut:      '',   szChwOutName:    '',   // 冰水出水溫（僅 tooltip）
            // CID 點位（控制寫入）
            szCidStartStop:   '',   szStartStopName: '',
            szCidSetTemp:     '',   szSetTempName:   '',   // 冰水設定溫度（右下角雙擊編輯，無上下限）
            nLoadMax:         100,
            szManualColor:    '#c79100',   // 現場面板色（深黃）
            szAutoColor:      '#0d6efd',
            szRunColor:       '#28a745',
            szStopColor:      '#6c757d',
            szFaultColor:     '#dc3545',
            szBgColor:        'transparent'
        }
    }
};

// ============================================================
// 綁定欄位清單（頁面層級複製用）
// ============================================================
// page-clipboard.js 複製整頁時，會把 widget props 裡指向特定點位 / 迴路 / 排程的
// 欄位全部清空 —— 「複製一頁 = 版面照抄、點位重綁」。
//
// 名稱欄（szXxxName）命名不規則，必須列舉；SID / CID 欄走前綴規則兜底。
// ⚠️ 新增 widget 或新增綁定欄位時，這裡沒同步 = **靜默保留舊點位**（複製出來的頁面
// 看起來沒綁，實際上還指著來源設備）。下方 _auditBindingKeys() 會在啟動時掃描
// WIDGET_DEFS，把漏掉的 key 在 console 喊出來。
const DESIGNER_BINDING_KEYS = new Set([
    'szPointName',
    'szRunName', 'szFaultName', 'szModeName', 'szFreqName', 'szLoadName',
    'szChwOutName', 'szWaterTempName',
    'szStartStopName', 'szFreqSetName', 'szSetTempName',
    'szCircuitName', 'szScheduleName',
    // 管路的綁定模式：'' = 未綁（純裝飾固定流動）。留著 'di'/'analog' 而 szSid 被清空，
    // 執行期會卡在「永遠停止」的半狀態（scadapage/render.js updatePipe 直接 return）
    'szBindMode',
]);

// 走 delete（而非設空字串）的欄位 —— 與既有 clearCellCircuit「新鍵全 delete，
// JSON 無殘留」一致；szMetric 屬迴路綁定三元組（nCircuitId / szCircuitName / szMetric）
const DESIGNER_BINDING_DELETE_KEYS = new Set([
    'nCircuitId', 'nScheduleId', 'szMetric',
]);

// 前綴規則兜底：szSid* / szCid* 一律視為綁定
// （szSidLabel / szCidLabel / szSidAttr 等只是渲染區域變數，不是 props，不受影響）
function isDesignerBindingKey(szKey) {
    return DESIGNER_BINDING_KEYS.has(szKey) || /^szSid/.test(szKey) || /^szCid/.test(szKey);
}

// 啟動自檢：掃描所有 defaultProps，找出「看起來像綁定卻沒被任何規則涵蓋」的 key。
// 純硬編清單日後一定會漏（新增 widget 時沒人記得回來改），漏掉就是靜默保留舊點位。
// 由 index.js 於啟動時呼叫（本檔不做頂層副作用）。
function auditDesignerBindingKeys() {
    const suspects = new Set();
    for (const szType in WIDGET_DEFS) {
        const props = WIDGET_DEFS[szType].defaultProps || {};
        for (const k in props) {
            if (isDesignerBindingKey(k) || DESIGNER_BINDING_DELETE_KEYS.has(k)) continue;
            // 名稱像綁定（Sid / Cid / CircuitId / ScheduleId 結尾為 Name 的點位欄）卻沒涵蓋到
            if (/Sid|Cid|CircuitId|ScheduleId/i.test(k)) suspects.add(`${szType}.${k}`);
        }
    }
    if (suspects.size > 0) {
        console.warn('[Designer] 疑似未納入複製清空範圍的綁定欄位：', [...suspects].join(', '),
            '— 請檢查 widget-defs.js 的 DESIGNER_BINDING_KEYS');
    }
}

// Resolve `__i18n__:key` placeholders in default props using current culture.
// Called by widget-core.js when materializing widget defaults on create.
function getWidgetDefaultProps(szType) {
    const def = WIDGET_DEFS[szType];
    if (!def || !def.defaultProps) return {};
    const out = {};
    for (const k in def.defaultProps) {
        const v = def.defaultProps[k];
        if (typeof v === 'string' && v.startsWith('__i18n__:')) {
            out[k] = t(v.slice('__i18n__:'.length));
        } else {
            out[k] = v;
        }
    }
    return out;
}

// ============================================================
// 文字 Widget 渲染
// ============================================================
function renderTextWidget(el) {
    const props = el.widgetProps;
    const szFontStyle = props.isItalic ? 'italic' : 'normal';
    const szBg        = props.szBgColor || 'transparent';

    el.innerHTML = `
        <div class="text-widget-body" style="
            font-family:${props.szFontFamily};
            font-size:${props.nFontSize}px;
            color:${props.szFontColor};
            font-weight:${props.szFontWeight};
            font-style:${szFontStyle};
            background:${szBg};
        ">${escHtml(props.szText)}</div>
        <button class="text-del-btn" onclick="deleteWidget('${el.id}')" title="${escHtml(t('designer.widget.delete_text_tooltip'))}"><i class="fas fa-times" aria-hidden="true"></i></button>
        <div class="resize-knob" title="${escHtml(t('designer.widget.resize_tooltip'))}"></div>
    `;

    // 拖移（整個文字主體）
    el.querySelector('.text-widget-body').addEventListener('mousedown', e => {
        e.preventDefault();
        startMove(e, el);
    });

    // 縮放（右下角）
    el.querySelector('.resize-knob').addEventListener('mousedown', e => {
        e.preventDefault();
        e.stopPropagation();
        startResize(e, el);
    });

    // 點選選取（支援 Ctrl 多選）
    el.addEventListener('mousedown', (ev) => onWidgetMouseDown(ev, el));

    // 雙擊 → focus 屬性面板文字內容欄
    bindWidgetDblClick(el);
}

// ============================================================
// 表格 HTML
// ============================================================
// Note: TABLE_SAMPLE 為內部 sample 資料常數，未實際渲染至 UI，保留繁中即可。
const TABLE_SAMPLE = [
    ['溫度感測器', '85.3°C', '正常', '2026-03-05'],
    ['壓力感測器', '2.40 bar', '正常', '2026-03-05'],
    ['流量計',     '12.7 L/s', '警告', '2026-03-05'],
    ['液位感測器', '67.2%',   '正常', '2026-03-05'],
    ['電流感測器', '32.1 A',  '正常', '2026-03-05'],
    ['電壓感測器', '220 V',   '正常', '2026-03-05'],
];

// 表頭預設值（建立 table widget 時的初始 header cell text）— 依當前 culture 取值，
// 寫入 DB 後即為 user input
function getDefaultColHeaders() {
    return [
        t('designer.table.header.name'),
        t('designer.table.header.value'),
        t('designer.table.header.status'),
        t('designer.table.header.timestamp')
    ];
}

// ── 表格預設底色樣式（plan 2026-07-04）──
// 一次性填色：點選時把顏色複製進 props（含全部 cell 字色），不存 preset key，
// 套用後仍可個別微調；配色對齊 docs/設計規範.md 雙主題色票
const TABLE_STYLE_PRESETS = {
    classic:    { szHeaderColor: '#343a40', szHeaderFont: '#ffffff', szBodyBgOdd: '#ffffff', szBodyBgEven: '#f8f9fa', szBorderColor: '#dee2e6', szDataFont: '#444444' },
    scada_blue: { szHeaderColor: '#0d6efd', szHeaderFont: '#ffffff', szBodyBgOdd: '#ffffff', szBodyBgEven: '#e7f1ff', szBorderColor: '#b6d4fe', szDataFont: '#212529' },
    ems_green:  { szHeaderColor: '#2e7d32', szHeaderFont: '#ffffff', szBodyBgOdd: '#ffffff', szBodyBgEven: '#e8f5e9', szBorderColor: '#c8e6c9', szDataFont: '#1b5e20' },
    dark:       { szHeaderColor: '#1f2937', szHeaderFont: '#e5e7eb', szBodyBgOdd: '#111827', szBodyBgEven: '#1e293b', szBorderColor: '#374151', szDataFont: '#e5e7eb' },
    minimal:    { szHeaderColor: '#f8f9fa', szHeaderFont: '#495057', szBodyBgOdd: '#ffffff', szBodyBgEven: '#ffffff', szBorderColor: '#e9ecef', szDataFont: '#444444' }
};

// ── 儲存格預設值 ──
function _defaultHeaderCell(ci) {
    // 第一列（表頭）預設置中
    return { szText: '', szFontColor: '#fff', szFontWeight: '500', szAlign: 'center', nFontSize: 16 };
}
function _defaultDataCell(ri, ci) {
    return {
        // 第一欄預設置中，其餘資料欄靠右（數值對齊）
        szText: '', szFontColor: '#444', szFontWeight: 'normal', szAlign: (ci === 0 ? 'center' : 'right'), nFontSize: 16,
        szSid: '', szPointName: '', szPointType: 'AI',
        szOnLabel: 'ON', szOffLabel: 'OFF',
        szHighColor: '#dc3545', szLowColor: '#fd7e14',
        szAlarmColor: '#dc3545'
    };
}

// ── 初始化 arrCells（新建或舊資料遷移）──
function initArrCells(props) {
    const nC = Math.max(1, props.nCols || 3);
    const nR = Math.max(1, props.nRows || 5);
    if (!props.arrCells) {
        props.arrCells = [];
        // row 0 = 標題列
        props.arrCells.push(Array.from({ length: nC }, (_, ci) => _defaultHeaderCell(ci)));
        // row 1..nR = 資料列
        for (let ri = 0; ri < nR; ri++) {
            props.arrCells.push(Array.from({ length: nC }, (_, ci) => _defaultDataCell(ri, ci)));
        }
    }
    if (!props.arrColDecimals) {
        props.arrColDecimals = Array.from({ length: nC }, () => null);
    }
    // ─ 表格大小：舊檔可能無這些欄位，填補預設 ─
    if (props.nDefaultRowH == null) props.nDefaultRowH = 20;
    if (props.nDefaultColW == null) props.nDefaultColW = 80;
    if (!Array.isArray(props.arrColWidths))  props.arrColWidths  = Array.from({ length: nC }, () => null);
    if (!Array.isArray(props.arrRowHeights)) props.arrRowHeights = Array.from({ length: nR }, () => null);
    if (typeof props.bTableSizeLocked !== 'boolean') props.bTableSizeLocked = false;
    // ─ 底色樣式：舊檔無這些欄位，補 null（= 沿用現行渲染）─
    if (props.szBodyBgOdd === undefined)   props.szBodyBgOdd = null;
    if (props.szBodyBgEven === undefined)  props.szBodyBgEven = null;
    if (props.szBorderColor === undefined) props.szBorderColor = null;
}

// ── 當 nRows/nCols 改變時同步 arrCells 大小 ──
function syncArrCellsSize(props) {
    if (!props.arrCells) { initArrCells(props); return; }
    const nC = Math.max(1, props.nCols || 3);
    const nR = Math.max(1, props.nRows || 5);
    const nTotalRows = nR + 1; // +1 for header
    // 調整列數
    while (props.arrCells.length < nTotalRows) {
        const ri = props.arrCells.length - 1;
        props.arrCells.push(Array.from({ length: nC }, (_, ci) => _defaultDataCell(ri, ci)));
    }
    while (props.arrCells.length > nTotalRows) props.arrCells.pop();
    // 調整欄數
    props.arrCells.forEach((row, ri) => {
        while (row.length < nC) {
            const ci = row.length;
            row.push(ri === 0 ? _defaultHeaderCell(ci) : _defaultDataCell(ri - 1, ci));
        }
        while (row.length > nC) row.pop();
    });
    // 同步 arrColDecimals
    while (props.arrColDecimals.length < nC) props.arrColDecimals.push(null);
    while (props.arrColDecimals.length > nC) props.arrColDecimals.pop();
    // 同步 arrColWidths（長度 = nCols）
    if (!Array.isArray(props.arrColWidths)) props.arrColWidths = [];
    while (props.arrColWidths.length < nC) props.arrColWidths.push(null);
    while (props.arrColWidths.length > nC) props.arrColWidths.pop();
    // 同步 arrRowHeights（長度 = nRows，僅資料列）
    if (!Array.isArray(props.arrRowHeights)) props.arrRowHeights = [];
    while (props.arrRowHeights.length < nR) props.arrRowHeights.push(null);
    while (props.arrRowHeights.length > nR) props.arrRowHeights.pop();
}

// ── 計算 table widget 整體外框尺寸（由欄列尺寸決定）──
// chrome（border + widget-body padding）：水平 10px、垂直 24px（含 widget-header 預留 18px）
const TABLE_CHROME_W = 10;
const TABLE_CHROME_H = 24;
function computeTableWidgetSize(props) {
    const nC = Math.max(1, props.nCols || 1);
    const nR = Math.max(1, props.nRows || 1);
    const nDefW = Math.max(1, props.nDefaultColW || 80);
    const nDefH = Math.max(1, props.nDefaultRowH || 20);
    const arrCW = Array.isArray(props.arrColWidths) ? props.arrColWidths : [];
    const arrRH = Array.isArray(props.arrRowHeights) ? props.arrRowHeights : [];
    let nW = TABLE_CHROME_W;
    for (let i = 0; i < nC; i++) nW += (arrCW[i] != null ? +arrCW[i] : nDefW);
    let nH = TABLE_CHROME_H + nDefH; // header row 固定 = nDefaultRowH
    for (let i = 0; i < nR; i++) nH += (arrRH[i] != null ? +arrRH[i] : nDefH);
    return { nW: Math.round(nW), nH: Math.round(nH) };
}

function buildTableHtml(props) {
    initArrCells(props);
    const nC = Math.max(1, props.nCols || 3);
    const nR = Math.max(1, props.nRows || 5);
    const arrCells = props.arrCells;
    const nDefW = props.nDefaultColW || 80;
    const nDefH = props.nDefaultRowH || 20;
    const arrCW = props.arrColWidths || [];
    const arrRH = props.arrRowHeights || [];

    // colgroup — 控制各欄寬度
    const szColgroup = '<colgroup>' + Array.from({ length: nC }, (_, ci) => {
        const w = arrCW[ci] != null ? +arrCW[ci] : nDefW;
        return `<col style="width:${w}px">`;
    }).join('') + '</colgroup>';

    // 標題列 (row 0) — 高度永遠 = nDefaultRowH
    const szHeaders = arrCells[0].slice(0, nC).map((cell, ci) =>
        `<th data-row="0" data-col="${ci}" style="background:${props.szHeaderColor};
            color:${cell.szFontColor || '#fff'};font-weight:${cell.szFontWeight || '500'};
            font-size:${cell.nFontSize || 12}px;text-align:${cell.szAlign || 'left'};
            cursor:pointer;">${escHtml(cell.szText || '')}</th>`
    ).join('');

    // 底色樣式（plan 2026-07-04）：null = 不輸出 inline，沿用 designer.css 斑馬紋
    const szBgOdd   = props.szBodyBgOdd   || '';
    const szBgEven  = props.szBodyBgEven  || '';
    const szBorderC = props.szBorderColor || '';

    // 資料列 (row 1..nR) — tr 帶 inline height
    const szRows = Array.from({ length: nR }, (_, ri) => {
        const rowIdx = ri + 1;
        if (rowIdx >= arrCells.length) return '';
        const row = arrCells[rowIdx];
        const nRowH = arrRH[ri] != null ? +arrRH[ri] : nDefH;
        const szRowBg = (ri % 2 === 1) ? szBgEven : szBgOdd;
        const szBgStyle = szRowBg ? `background:${szRowBg};` : '';
        // 末列不輸出 inline border，保留 CSS tr:last-child 無框線行為
        const szBorderStyle = (szBorderC && ri < nR - 1) ? `border-bottom:1px solid ${szBorderC};` : '';
        const szCells = row.slice(0, nC).map((cell, ci) => {
            const szSidAttr = cell.szSid ? `data-sid="${escHtml(cell.szSid)}"` : '';
            let szSidLabel = '';
            if (cell.szSid) {
                szSidLabel = `<span style="font-size:9px;color:#0d6efd;opacity:.7;margin-left:4px;white-space:nowrap;"><i class="fas fa-link" style="font-size:8px;margin-right:2px;"></i>${escHtml(cell.szPointName || cell.szSid)}</span>`;
            } else if (cell.nCircuitId != null) {
                // 迴路指標 cell（plan 2026-07-23）：綠色 sitemap + 迴路名·指標縮寫
                szSidLabel = `<span style="font-size:9px;color:#2e7d32;opacity:.85;margin-left:4px;white-space:nowrap;"><i class="fas fa-sitemap" style="font-size:8px;margin-right:2px;"></i>${escHtml((cell.szCircuitName || ('#' + cell.nCircuitId)) + '·' + t('designer.metric.badge.' + (cell.szMetric || 'day_kwh')))}</span>`;
            }
            return `<td data-row="${rowIdx}" data-col="${ci}" ${szSidAttr}
                style="color:${cell.szFontColor || '#444'};font-weight:${cell.szFontWeight || 'normal'};
                       font-size:${cell.nFontSize || 12}px;text-align:${cell.szAlign || 'left'};
                       ${szBgStyle}${szBorderStyle}
                       cursor:pointer;position:relative;">${escHtml(cell.szText || '')}${szSidLabel}</td>`;
        }).join('');
        return `<tr style="height:${nRowH}px">${szCells}</tr>`;
    }).join('');

    return `<table class="w-table">
        ${szColgroup}
        <thead><tr style="height:${nDefH}px">${szHeaders}</tr></thead>
        <tbody>${szRows}</tbody>
    </table>`;
}

// ============================================================
// 儀錶板 SVG
// ============================================================
function buildGaugeHtml(props) {
    const cx = 100, cy = 110, r = 83;

    const fRaw = (props.fValue - props.fMin) / ((props.fMax - props.fMin) || 1);
    const fPct = Math.max(0.001, Math.min(0.999, fRaw));

    // 計算終點（SVG 座標，順時針 sweep=1）
    // 起始角 θ=180°（左端），終止角 = 180° + pct*180°
    const thetaEnd = (180 + fPct * 180) * Math.PI / 180;
    const ex = (cx + r * Math.cos(thetaEnd)).toFixed(2);
    const ey = (cy + r * Math.sin(thetaEnd)).toFixed(2);

    // 顏色閾值
    const fPctClamped = Math.max(0, Math.min(1, fRaw));
    const szColor = fPctClamped >= 0.85 ? '#dc3545'
                  : fPctClamped >= 0.65 ? '#fd7e14'
                  : props.szColor;

    // 背景弧（完整半圓，sweep=1）
    const szBg = `M ${cx-r} ${cy} A ${r} ${r} 0 0 1 ${cx+r} ${cy}`;
    // 數值弧
    const szVal = `M ${cx-r} ${cy} A ${r} ${r} 0 0 1 ${ex} ${ey}`;

    const szDisplayVal = Number(props.fValue).toFixed(1);
    const szGaugeBg = (props.szBgColor && props.szBgColor !== 'transparent') ? props.szBgColor : 'transparent';

    // 警報小鈴鐺
    const szAlarmBadge = hasAlarmRule(props.szSid)
        ? `<div style="position:absolute;top:2px;right:4px;font-size:9px;color:#dc3545;opacity:.7;z-index:1;">
               <i class="fas fa-bell"></i>
           </div>`
        : '';

    return `<div style="position:relative;width:100%;height:100%;background:${szGaugeBg};border-radius:4px;">
    ${szAlarmBadge}
    <svg viewBox="0 0 200 145" xmlns="http://www.w3.org/2000/svg"
                 style="width:100%;height:100%;display:block;">
        <!-- 背景弧 -->
        <path d="${szBg}" fill="none" stroke="#e9ecef" stroke-width="15" stroke-linecap="round"/>
        <!-- 數值弧 -->
        <path d="${szVal}" fill="none" stroke="${szColor}" stroke-width="15" stroke-linecap="round"/>
        <!-- 主數值 -->
        <text x="100" y="96" text-anchor="middle" font-size="26" font-weight="700"
              fill="#212529" font-family="'Segoe UI',sans-serif">${szDisplayVal}</text>
        <!-- 單位 -->
        <text x="100" y="113" text-anchor="middle" font-size="13" fill="#6c757d"
              font-family="'Segoe UI',sans-serif">${props.szUnit}</text>
        <!-- 最小值 -->
        <text x="${cx-r}" y="${cy+18}" text-anchor="middle" font-size="10" fill="#adb5bd">${props.fMin}</text>
        <!-- 最大值 -->
        <text x="${cx+r}" y="${cy+18}" text-anchor="middle" font-size="10" fill="#adb5bd">${props.fMax}</text>
        <!-- 標題 -->
        <text x="100" y="140" text-anchor="middle" font-size="11" fill="#868e96"
              font-family="'Segoe UI',sans-serif">${props.szTitle}</text>
    </svg></div>`;
}

// ============================================================
// 即時曲線 HTML（Designer 預覽 — 固定假波形，不連線、不輪詢、不查 DB）
// ============================================================
// 編輯期只需回答「這張圖多大、什麼顏色、Y 軸怎麼切」，不需要真資料（plan 決策 6）。
// 用 SVG 而非 Chart.js：免相依、resize 時靠 preserveAspectRatio="none" 自動自適應，
// 線寬與格線靠 vector-effect="non-scaling-stroke" 不隨拉伸變形。
const TREND_FAKE_WAVE_SEGMENTS = 60;

function buildTrendChartHtml(props) {
    const szBg     = (props.szBgColor && props.szBgColor !== 'transparent') ? props.szBgColor : 'transparent';
    const szBorder = props.szBorderColor || '#000000';
    const szGrid   = props.szGridColor   || '#cccccc';
    const szLine   = props.szLineColor   || '#0d6efd';
    const nLineW   = Math.max(1, props.nLineWidth || 2);
    const nGrid    = Math.max(0, Math.min(10, props.nGridCount != null ? props.nGridCount : 5));

    // 水平格線（等分，不含上下邊界）
    let szGridHtml = '';
    for (let i = 1; i <= nGrid; i++) {
        const y = (100 * i / (nGrid + 1)).toFixed(2);
        szGridHtml += `<line x1="0" y1="${y}" x2="100" y2="${y}" stroke="${szGrid}"
                             stroke-width="1" vector-effect="non-scaling-stroke"/>`;
    }

    // 假波形：兩個不同週期的正弦疊加，看起來像現場訊號而非規律測試波
    const arrPts = [];
    for (let i = 0; i <= TREND_FAKE_WAVE_SEGMENTS; i++) {
        const x = 100 * i / TREND_FAKE_WAVE_SEGMENTS;
        const f = Math.sin(i * 0.38) * 0.62 + Math.sin(i * 0.11 + 1.1) * 0.3;
        // SVG y 軸向下，故 50 - f*波幅
        arrPts.push(x.toFixed(2) + ',' + (50 - f * 34).toFixed(2));
    }

    // 綁定標籤（左上）
    const szBindBadge = props.szPointName
        ? `<div style="position:absolute;top:2px;left:5px;font-size:9px;color:${szLine};opacity:.9;
                       max-width:70%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;
                       pointer-events:none;"><i class="fas fa-link" style="font-size:8px;margin-right:2px;"></i>${escHtml(props.szPointName)}</div>`
        : `<div style="position:absolute;top:2px;left:5px;font-size:9px;color:#dc3545;pointer-events:none;">
               <i class="fas fa-unlink me-1"></i>${escHtml(t('designer.widget.unbound_sid'))}</div>`;

    // 警報小鈴鐺（與 gauge / realtimeValue 一致）
    const szAlarmBadge = hasAlarmRule(props.szSid)
        ? `<div style="position:absolute;top:2px;right:4px;font-size:9px;color:#dc3545;opacity:.7;">
               <i class="fas fa-bell"></i></div>`
        : '';

    // Y 軸上下限標籤（右側；留空顯示「自動」）+ 時間窗標籤（左下）
    const szAuto  = escHtml(t('designer.prop.trend.auto'));
    const szYMax  = (props.fYMax != null && props.fYMax !== '') ? escHtml(String(props.fYMax)) : szAuto;
    const szYMin  = (props.fYMin != null && props.fYMin !== '') ? escHtml(String(props.fYMin)) : szAuto;
    const szUnit  = props.szUnit ? ' ' + escHtml(props.szUnit) : '';
    const nWinMin = Math.max(1, Math.round((props.nWindowSec || 1800) / 60));
    const szAxisStyle = 'position:absolute;font-size:8px;color:#868e96;pointer-events:none;';

    return `<div style="position:relative;width:100%;height:100%;box-sizing:border-box;
                        background:${szBg};border:1px solid ${szBorder};border-radius:4px;overflow:hidden;">
        <svg viewBox="0 0 100 100" preserveAspectRatio="none"
             xmlns="http://www.w3.org/2000/svg"
             style="position:absolute;inset:0;width:100%;height:100%;display:block;">
            ${szGridHtml}
            <polyline points="${arrPts.join(' ')}" fill="none" stroke="${szLine}"
                      stroke-width="${nLineW}" stroke-linejoin="round" stroke-linecap="round"
                      vector-effect="non-scaling-stroke"/>
        </svg>
        ${szBindBadge}
        ${szAlarmBadge}
        <div style="${szAxisStyle}top:14px;right:5px;">${szYMax}${szUnit}</div>
        <div style="${szAxisStyle}bottom:3px;right:5px;">${szYMin}${szUnit}</div>
        <div style="${szAxisStyle}bottom:3px;left:5px;">${escHtml(t('designer.prop.trend.window_badge', { 0: nWinMin }))}</div>
    </div>`;
}

// ============================================================
// 控制按鈕 HTML
// ============================================================
function buildControlBtnHtml(props) {
    const szCidLabel = props.szPointName
        ? ''
        : `<div class="ctrl-btn-cid-label" style="color:#dc3545;"><i class="fas fa-unlink me-1"></i>${escHtml(t('designer.widget.unbound_cid'))}</div>`;
    return `
        <div class="ctrl-btn-body">
            <button class="ctrl-btn-main" style="background:${props.szBtnColor || '#198754'};" disabled>
                <i class="fas ${props.szBtnIcon || 'fa-hand-pointer'}" style="font-size:12px;"></i>
                ${escHtml(props.szBtnLabel || t('designer.default.controlBtn_label'))}
            </button>
            ${szCidLabel}
        </div>`;
}

// ============================================================
// AI 點位 HTML（Designer 預覽）
// ============================================================
// 迴路指標模式的單位顯示（電費=元、需量=kW、其餘 kWh）
function _cmetricUnitShown(szMetric) {
    if (szMetric === 'period_cost') return t('designer.metric.unit_cost');
    if (szMetric === 'demand_kw') return 'kW';
    return 'kWh';
}

function buildRealtimeValueHtml(props) {
    const bCircuitBound = props.nCircuitId != null;
    const szSidLabel = (props.szPointName || bCircuitBound)
        ? ''
        : `<div style="font-size:10px;color:#dc3545;margin-top:2px;"><i class="fas fa-unlink me-1"></i>${escHtml(t('designer.widget.unbound_sid'))}</div>`;
    const szBg = (props.szBgColor && props.szBgColor !== 'transparent') ? props.szBgColor : 'transparent';

    // 警報小鈴鐺
    const szAlarmBadge = hasAlarmRule(props.szSid)
        ? `<div style="position:absolute;top:2px;right:4px;font-size:9px;color:#dc3545;opacity:.7;">
               <i class="fas fa-bell"></i>
           </div>`
        : '';

    // badge（左上角）：迴路指標模式=指標縮寫（綠）；SID 累積模式=日累/月累（藍）；即時值無 badge
    const szValueMode = props.szValueMode || 'realtime';
    let szModeBadge = '';
    if (bCircuitBound) {
        const szMetric = props.szMetric || 'day_kwh';
        szModeBadge = `<div style="position:absolute;top:2px;left:4px;font-size:9px;color:#2e7d32;opacity:.9;font-weight:600;">
               ${escHtml(t('designer.metric.badge.' + szMetric))}
           </div>`;
    } else if (szValueMode === 'day' || szValueMode === 'month') {
        szModeBadge = `<div style="position:absolute;top:2px;left:4px;font-size:9px;color:#0d6efd;opacity:.85;font-weight:600;">
               ${escHtml(t(szValueMode === 'day' ? 'designer.widget.acc_day_badge' : 'designer.widget.acc_month_badge'))}
           </div>`;
    }
    // 單位：迴路指標依指標決定（電費=元、需量=kW、其餘 kWh）；SID 累積模式自訂累積單位優先，空則沿用即時單位
    const szUnitShown = bCircuitBound
        ? _cmetricUnitShown(props.szMetric || 'day_kwh')
        : ((szValueMode !== 'realtime' && props.szAccUnit) ? props.szAccUnit : (props.szUnit || ''));

    let szBorder = '';
    if (szBg === 'transparent') {
        const szCanvasBg = document.getElementById('designCanvas')?.style.backgroundColor || '';
        szBorder = 'border:1px solid ' + (isDarkColor(szCanvasBg) ? '#fff' : '#000') + ';';
    }

    return `
        <div style="width:100%;height:100%;display:flex;flex-direction:column;position:relative;
                    align-items:center;justify-content:center;background:${szBg};border-radius:4px;${szBorder}">
            ${szAlarmBadge}
            ${szModeBadge}
            <div style="font-size:${props.nFontSize || 28}px;font-weight:700;color:${props.szFontColor || '#212529'};
                        font-family:'Segoe UI',sans-serif;line-height:1.2;">
                --
                <span style="font-size:${Math.max(12, (props.nFontSize || 28) * 0.45)}px;font-weight:400;color:#6c757d;margin-left:4px;">${escHtml(szUnitShown)}</span>
            </div>
            ${szSidLabel}
        </div>`;
}

// ============================================================
// DI 點位 HTML（Designer 預覽）
// ============================================================
function buildDiPointHtml(props) {
    const bSchedule = props.nScheduleId != null;
    let szBindLabel;
    if (bSchedule) {
        // Designer 預覽：排程模式靜態顯示 OFF + 排程名標籤（plan 決策 4）
        szBindLabel = `<div style="font-size:10px;color:#0d6efd;margin-top:2px;"><i class="fas fa-calendar-alt me-1"></i>${escHtml(props.szScheduleName || '')}</div>`;
    } else if (props.szPointName) {
        szBindLabel = '';
    } else {
        szBindLabel = `<div style="font-size:10px;color:#dc3545;margin-top:2px;"><i class="fas fa-unlink me-1"></i>${escHtml(t('designer.widget.unbound_sid'))}</div>`;
    }
    const szBg = (props.szBgColor && props.szBgColor !== 'transparent') ? props.szBgColor : 'transparent';
    const szMode = props.szDisplayMode || 'indicator';

    // 警報標記（排程模式無 SID 警報概念，僅 SID 模式顯示）
    const szAlarmBadge = (!bSchedule && hasAlarmRule(props.szSid))
        ? `<div style="position:absolute;top:2px;right:4px;font-size:9px;color:#dc3545;opacity:.7;">
               <i class="fas fa-bell"></i>
           </div>`
        : '';

    // Designer 預覽預設顯示 OFF 狀態
    let szContentHtml = '';
    if (szMode === 'text') {
        // 文字模式：只顯示 ON/OFF 文字，套用顏色
        const nFs = props.nFontSize || 24;
        const szColor = props.szOffColor || '#6c757d';
        const szLabel = props.szOffLabel || 'OFF';
        szContentHtml = `
            <span style="font-size:${nFs}px;font-weight:700;color:${szColor};
                         font-family:'Segoe UI',sans-serif;">${escHtml(szLabel)}</span>`;
    } else {
        // 燈號模式：圓形指示燈，自動適應容器大小
        const nSize = props.nIndicatorSize || 28;
        const szColor = props.szOffColor || '#6c757d';
        szContentHtml = `
            <span class="di-indicator" style="--di-size:${nSize}px;
                         background:${szColor};box-shadow:0 0 6px ${szColor};"></span>`;
    }

    let szBorder = '';
    if (szBg === 'transparent') {
        const szCanvasBg = document.getElementById('designCanvas')?.style.backgroundColor || '';
        szBorder = 'border:1px solid ' + (isDarkColor(szCanvasBg) ? '#fff' : '#000') + ';';
    }

    return `
        <div style="width:100%;height:100%;display:flex;flex-direction:column;position:relative;
                    align-items:center;justify-content:center;background:${szBg};border-radius:4px;gap:2px;${szBorder}">
            ${szAlarmBadge}
            ${szContentHtml}
            ${szBindLabel}
        </div>`;
}

// ============================================================
// AO 點位 HTML（Designer 預覽）
// ============================================================
function buildAoPointHtml(props) {
    const szCidLabel = props.szPointName
        ? ''
        : `<div class="ao-point-cid-label" style="color:#dc3545;"><i class="fas fa-unlink me-1"></i>${escHtml(t('designer.widget.unbound_cid'))}</div>`;
    const szBlock = props.szBlockColor
                  || (props.szBgColor && props.szBgColor !== 'transparent' ? props.szBgColor : null)
                  || '#0d6efd';
    const nFs = props.nFontSize || 16;
    const szName = props.szDisplayName || props.szTitle || t('designer.default.aoPoint_title');

    return `
        <div class="ao-point-body">
            <div style="width:100%;height:100%;display:flex;align-items:center;justify-content:center;
                        font-size:${nFs}px;font-weight:600;color:${props.szFontColor || '#ffffff'};
                        text-align:center;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;
                        border-radius:6px;background:${szBlock};
                        box-shadow:0 2px 4px rgba(0,0,0,.15);">
                ${escHtml(szName)}
            </div>
            ${szCidLabel}
        </div>`;
}

// ============================================================
// DO 點位 HTML（Designer 預覽）
// ============================================================
function buildDoPointHtml(props) {
    const szCidLabel = props.szPointName
        ? ''
        : `<div class="do-point-cid-label" style="color:#dc3545;"><i class="fas fa-unlink me-1"></i>${escHtml(t('designer.widget.unbound_cid'))}</div>`;
    const szBlock = props.szBlockColor
                  || (props.szBgColor && props.szBgColor !== 'transparent' ? props.szBgColor : null)
                  || '#0d6efd';
    const nFs = props.nFontSize || 16;
    const szName = props.szDisplayName || props.szTitle || t('designer.default.doPoint_title');

    return `
        <div class="do-point-body">
            <div style="width:100%;height:100%;display:flex;align-items:center;justify-content:center;
                        font-size:${nFs}px;font-weight:600;color:${props.szFontColor || '#212529'};
                        text-align:center;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;
                        border-radius:6px;background:${szBlock};
                        box-shadow:0 2px 4px rgba(0,0,0,.15);">
                ${escHtml(szName)}
            </div>
            ${szCidLabel}
        </div>`;
}

// ============================================================
// 水泵 SVG HTML（蝸殼泵輪廓，出水口方向透過旋轉實現）
// ============================================================
function buildPumpHtml(props, szState) {
    // szState: 'stop' | 'run' | 'fault'
    const szRunColor   = props.szRunColor   || '#28a745';
    const szStopColor  = props.szStopColor  || '#6c757d';
    const szFaultColor = props.szFaultColor || '#dc3545';
    const szCircleColor = szState === 'fault' ? szFaultColor
                        : szState === 'run'   ? szRunColor
                        : szStopColor;
    // 泵體顏色：有綁定手自動SID時依模式著色（Designer預設顯示自動色），否則中性灰
    const szManualColor = props.szManualColor || '#ffc107';
    const szAutoColor   = props.szAutoColor   || '#0d6efd';
    const szBodyColor   = props.szSidMode ? szAutoColor : '#555';
    const szBg  = (props.szBgColor && props.szBgColor !== 'transparent') ? props.szBgColor : 'transparent';
    const szDir = props.szOutletDir || 'right';
    const szTransform = szDir === 'left'  ? 'translate(120,0) scale(-1,1)'
                      : szDir === 'up'    ? 'rotate(-90,60,50)'
                      : '';

    // 頻率 linear gauge（僅綁定 szSidFreq 時顯示）
    const bHasFreq = !!props.szSidFreq;
    const nFreqMax = props.nFreqMax || 60;
    const szViewBox = bHasFreq ? '0 0 170 110' : '0 0 120 110';
    const szContrastColor = (function() {
        const bg = szBg;
        if (!bg || bg === 'transparent') return '#333';
        const m = bg.match(/^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i);
        if (!m) return '#333';
        const lum = (parseInt(m[1],16)*299 + parseInt(m[2],16)*587 + parseInt(m[3],16)*114) / 1000;
        return lum > 128 ? '#333' : '#f0f0f0';
    })();
    let szGaugeHtml = '';
    if (bHasFreq) {
        const nBarTop = 22, nBarH = 68;
        const nFillH = Math.round(nBarH * 0.5);
        szGaugeHtml = `
            <rect x="115" y="${nBarTop}" width="10" height="${nBarH}" rx="3" fill="#333" stroke="#555" stroke-width="1"/>
            <rect x="115" y="${nBarTop + nBarH - nFillH}" width="10" height="${nFillH}" rx="3" fill="#17a2b8" opacity="0.6"/>
            <text x="120" y="${nBarTop - 4}" text-anchor="middle" font-size="8" fill="${szContrastColor}">${nFreqMax}</text>
            <text x="120" y="${nBarTop + nBarH + 10}" text-anchor="middle" font-size="8" fill="${szContrastColor}">0</text>
            <text x="130" y="${nBarTop + nBarH - nFillH + nFillH / 2 + 3}" text-anchor="start"
                  font-size="9" fill="${szContrastColor}" font-weight="600">-- Hz</text>`;
    }

    return `<div style="position:relative;width:100%;height:100%;background:${szBg};border-radius:4px;">
    <svg viewBox="${szViewBox}" xmlns="http://www.w3.org/2000/svg"
         style="width:100%;height:100%;display:block;">
        <!-- 底座 Base -->
        <rect x="38" y="86" width="44" height="6" rx="2" fill="#4a4a4a"/>
        <rect x="38" y="92" width="44" height="3" rx="1" fill="#333"/>
        <rect x="39" y="86" width="42" height="2" rx="1" fill="rgba(255,255,255,.12)"/>
        <!-- 支撐柱 -->
        <rect x="48" y="72" width="7" height="15" rx="1.5" fill="#4a4a4a" stroke="#3a3a3a" stroke-width=".5"/>
        <rect x="65" y="72" width="7" height="15" rx="1.5" fill="#4a4a4a" stroke="#3a3a3a" stroke-width=".5"/>
        <rect x="48" y="72" width="3.5" height="15" rx="1" fill="rgba(255,255,255,.08)"/>
        <rect x="65" y="72" width="3.5" height="15" rx="1" fill="rgba(255,255,255,.08)"/>
        <g transform="${szTransform}">
            <!-- 泵體 Volute shadow -->
            <path d="M 60,22 L 106,22 L 106,37 L 86,37 A 30,30 0 1,1 60,22 Z"
                  fill="rgba(0,0,0,.15)"/>
            <!-- 泵體 Volute body -->
            <path d="M 60,20 L 105,20 L 105,35 L 86,35 A 30,30 0 1,1 60,20 Z"
                  fill="${szBodyColor}" stroke="#3a3a3a" stroke-width="2" stroke-linejoin="round"/>
            <!-- 泵體 Top highlight -->
            <rect x="63" y="21" width="40" height="4" rx="1" fill="rgba(255,255,255,.15)"/>
            <!-- 出口法蘭 Outlet flange -->
            <rect x="103" y="18" width="4" height="19" rx="1" fill="${szBodyColor}" stroke="#3a3a3a" stroke-width="1"/>
            <line x1="104" y1="20" x2="106" y2="20" stroke="rgba(255,255,255,.2)" stroke-width=".8"/>
            <line x1="104" y1="35" x2="106" y2="35" stroke="rgba(0,0,0,.2)" stroke-width=".8"/>
            <!-- 葉片 Housing ring -->
            <circle cx="60" cy="50" r="14" fill="rgba(0,0,0,.2)" stroke="#3a3a3a" stroke-width="1"/>
            <!-- 葉片 Impeller disc -->
            <circle cx="60" cy="50" r="12" fill="${szCircleColor}" stroke="#333" stroke-width="1.5"/>
            <ellipse cx="57" cy="46" rx="6" ry="4" fill="rgba(255,255,255,.15)"/>
            <!-- 葉片 Blade 1 (up) -->
            <path d="M 58,48 C 56,43 57,39 60,38 C 63,39 64,43 62,48 Z" fill="rgba(255,255,255,.4)"/>
            <!-- 葉片 Blade 2 (lower-right) -->
            <path d="M 61.5,51 C 65,54 68.5,54.5 69,56 C 67,58 63,56 60.5,53 Z" fill="rgba(255,255,255,.4)"/>
            <!-- 葉片 Blade 3 (lower-left) -->
            <path d="M 58.5,51 C 55,54 51.5,54.5 51,56 C 53,58 57,56 59.5,53 Z" fill="rgba(255,255,255,.4)"/>
            <!-- 中心輪轂 Hub -->
            <circle cx="60" cy="50" r="3.5" fill="#3a3a3a" stroke="#2a2a2a" stroke-width="1"/>
            <circle cx="59" cy="49" r="1.2" fill="rgba(255,255,255,.25)"/>
        </g>${szGaugeHtml}
    </svg></div>`;
}

// ============================================================
// 冷卻水塔 / 空調箱風扇 / 冰機 — Designer 預覽（共用 MotorEquip 模組）
// ============================================================
// 三者執行期渲染在 scadapage/widget-motor.js，Designer 預覽與其共用同一份 SVG（plan 決策 3-A）。
// 冰機額外於右下角顯示「設定溫度」文字（Designer 為靜態預覽，執行期才可雙擊編輯）。
function buildCoolingTowerHtml(props, szState) {
    return MotorEquip.build({
        szType: 'coolingTower', props: props, szState: szState || 'stop',
        szPrimaryVal: '', szModeVal: '', bInteractive: false,
        szBadgesHtml: '', szOverlayHtml: '', szHoverHtml: ''
    });
}

function buildAhuFanHtml(props, szState) {
    return MotorEquip.build({
        szType: 'ahuFan', props: props, szState: szState || 'stop',
        szPrimaryVal: '', szModeVal: '', bInteractive: false,
        szBadgesHtml: '', szOverlayHtml: '', szHoverHtml: ''
    });
}

function buildChillerHtml(props, szState) {
    const szOverlay = props.szCidSetTemp
        ? `<div class="chiller-settemp" style="position:absolute;bottom:3px;right:4px;
              font-size:10px;color:#fff;background:rgba(33,37,41,.7);
              padding:1px 6px;border-radius:4px;white-space:nowrap;pointer-events:none;">
              ${escHtml(t('designer.motor.set_temp_prefix'))} --°C</div>`
        : '';
    return MotorEquip.build({
        szType: 'chiller', props: props, szState: szState || 'stop',
        szPrimaryVal: '', szModeVal: '', bInteractive: false,
        szBadgesHtml: '', szOverlayHtml: szOverlay, szHoverHtml: ''
    });
}

// ============================================================
// 管路流動元件（正交折線：SVG path，圖形共用 common/pipe-svg.js）
// ============================================================
// szState: 'flow'（流動）| 'stop'（靜止）| 'bad'（斷線）。Designer 預覽固定 'flow'。
// el 供讀取 widget 目前寬高（舊格式 h/v 直管推導 2 節點用）。
function buildPipeHtml(props, szState, el) {
    const nW = el ? (parseInt(el.style.width)  || el.offsetWidth  || 160) : 160;
    const nH = el ? (parseInt(el.style.height) || el.offsetHeight || 24)  : 24;
    return PipeSvg.build({ props: props, szState: szState, nW: nW, nH: nH, szHoverHtml: '' });
}

// ============================================================
// 圖片/動畫元件（自訂 GIF；圖形共用 common/image-widget.js）
// ============================================================
// szState: 'run'（播 GIF）| 'stop'（靜止圖）| 'bad'（斷線）。Designer 預覽固定 'run'（畫布顯示動畫，決策 4）。
function buildImageHtml(props, szState) {
    return ImageWidget.build(props, szState || 'run', t('designer.image.no_image'));
}
