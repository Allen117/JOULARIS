// LogicFlow Canvas 渲染：節點、連線、自動延伸、port 拖曳
(function () {
    const S = window.__lfNS;

    // 歷史值讀取標記：「-N分」badge（fa-history 圖示由元件提供，resx 只放文字）（僅啟用歷史讀取的 input / contact 節點）
    function histBadgeHtml(n) {
        if (!n.histEnabled || n.histOffsetMinutes == null) return '';
        return `<span class="hist-badge" title="${S.escHtml(S.t('logicflow.hist.badge_tip', { n: n.histOffsetMinutes }))}"><i class="fas fa-history me-1" aria-hidden="true"></i>${S.escHtml(S.t('logicflow.hist.badge', { n: n.histOffsetMinutes }))}</span>`;
    }

    // 未綁定標記：「⃠ 未綁定」badge。樹層級複製出的邏輯點位全空，
    // 少了這個提示會看不出哪些節點還沒接上點位（低調灰字，避免開發中的空白接點一片紅）
    function unboundBadgeHtml(n) {
        const isPointNode = n.type === 'input' || n.type === 'output';
        const isContact = n.type === 'contact_no' || n.type === 'contact_nc';
        if (!isPointNode && !isContact) return '';
        if (n.sid) return '';
        if (isContact && n.scheduleId != null) return '';
        return `<span class="unbound-badge" title="${S.escHtml(S.t('logicflow.node.unbound_tip'))}">`
            + `<i class="fas fa-unlink"></i>${S.escHtml(S.t('logicflow.node.unbound'))}</span>`;
    }

    function renderCanvasNodes() {
        const canvas = document.getElementById('diagramCanvas');
        if (!canvas) return;

        // 重繪前保存目前 focus 的節點輸入框（node/欄位/游標/當前輸入值），重繪後還原。
        // 否則背景輪詢每秒重建整個節點 DOM 會搶走 focus、並用 model 舊值蓋掉使用者正在打的字
        // （例如 variadic 的「數量」框，點進去馬上就跳走、要試很多次）。
        const _ae = document.activeElement;
        let _focusSave = null;
        if (_ae && _ae.tagName === 'INPUT' && canvas.contains(_ae) && _ae.dataset.nodeId != null) {
            _focusSave = {
                nodeId: _ae.dataset.nodeId,
                field: _ae.dataset.field || null,
                cls: _ae.classList[0] || null,
                value: _ae.value,
                selStart: null, selEnd: null
            };
            try { _focusSave.selStart = _ae.selectionStart; _focusSave.selEnd = _ae.selectionEnd; } catch (_) { }
        }

        canvas.querySelectorAll('.flow-node').forEach(el => el.remove());

        // 確保 SVG 連線層存在
        if (!canvas.querySelector('svg.edge-layer')) {
            const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.classList.add('edge-layer');
            svg.setAttribute('style', 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:5;');
            canvas.insertBefore(svg, canvas.firstChild);
        }

        for (const n of S.canvasNodes) {
            const meta = S.NODE_META[n.type] || { icon: 'fas fa-question', label: n.type, inputs: [], outputs: [] };
            const el = document.createElement('div');
            el.className = `flow-node node-${n.type}${S.selectedNodeIds.has(n.id) ? ' node-selected' : ''}`;
            el.dataset.nodeId = n.id;
            el.style.left = n.x + 'px';
            el.style.top = n.y + 'px';
            if (n.height) el.style.height = n.height + 'px';
            let label = n.pointName ? n.pointName : S.t(meta.label);

            // compare 節點：a值 運算子 b值（唯讀顯示）
            if (n.type === 'compare' && n.operator && S.COMPARE_OPS[n.operator]) {
                el.innerHTML = `<span class="compare-value-display" data-node-id="${n.id}" data-field="a">--</span>`
                    + `<span class="compare-op-symbol">${S.escHtml(S.COMPARE_OPS[n.operator].symbol)}</span>`
                    + `<span class="compare-value-display" data-node-id="${n.id}" data-field="b">--</span>`;
            } else if (n.type === 'constant') {
                const val = n.constValue != null ? n.constValue : 0;
                el.innerHTML = `<i class="${meta.icon}"></i>`
                    + `<input type="text" class="const-value-input" value="${S.fmtNum(val)}" data-node-id="${n.id}" data-raw="${val}">`;
            } else if (n.type === 'math' && n.operator && S.MATH_OPS[n.operator]) {
                const mop = S.MATH_OPS[n.operator];
                let html = `<span class="math-op-badge">${S.escHtml(mop.symbol)}</span>`;
                if (mop.hasValue) {
                    html += `<span class="math-value-display" data-node-id="${n.id}">--</span>`;
                } else {
                    html += `<span style="font-size:.75rem">${S.escHtml(S.t(mop.label))}</span>`;
                }
                el.innerHTML = html;
            } else if (n.type === 'timer') {
                const delEdge = S.canvasEdges.find(e => e.target === n.id && e.targetPort === 'delay');
                const del = delEdge ? (S.getNodeOutputValue(delEdge.source, delEdge.sourcePort) ?? n.timerDelay ?? 5) : (n.timerDelay ?? 5);
                const delHtml = delEdge
                    ? `<span class="timer-value-display" data-node-id="${n.id}" data-field="delay">${S.fmtNum(del)}</span>`
                    : `<input type="text" class="timer-value-input" value="${S.fmtNum(del)}" data-node-id="${n.id}" data-field="delay" data-raw="${del}">`;
                const top = n.operator && S.TIMER_OPS[n.operator] ? S.TIMER_OPS[n.operator] : S.TIMER_OPS['tp'];
                if (n.operator === 'ton' || n.operator === 'tpr') {
                    el.innerHTML = `<i class="${meta.icon}"></i>`
                        + `<span class="timer-type-badge">${S.escHtml(top.symbol)}</span>`
                        + `<span class="timer-label">${S.escHtml(S.t('logicflow.timer.delay_label'))}</span>`
                        + delHtml
                        + `<span class="timer-unit">${S.escHtml(S.t('logicflow.timer.second'))}</span>`;
                } else {
                    const hldEdge = S.canvasEdges.find(e => e.target === n.id && e.targetPort === 'hold');
                    const hld = hldEdge ? (S.getNodeOutputValue(hldEdge.source, hldEdge.sourcePort) ?? n.timerHold ?? 2) : (n.timerHold ?? 2);
                    const hldHtml = hldEdge
                        ? `<span class="timer-value-display" data-node-id="${n.id}" data-field="hold">${S.fmtNum(hld)}</span>`
                        : `<input type="text" class="timer-value-input" value="${S.fmtNum(hld)}" data-node-id="${n.id}" data-field="hold" data-raw="${hld}">`;
                    el.innerHTML = `<i class="${meta.icon}"></i>`
                        + `<span class="timer-type-badge">${S.escHtml(top.symbol)}</span>`
                        + `<span class="timer-label">延</span>`
                        + delHtml
                        + `<span class="timer-label">${S.escHtml(S.t('logicflow.timer.hold_label'))}</span>`
                        + hldHtml
                        + `<span class="timer-unit">秒</span>`;
                }
            } else if (n.type === 'counter') {
                const presetVal = n.presetValue != null ? n.presetValue : 10;
                const intervalSec = Math.round(((n.cuMinIntervalMs != null ? n.cuMinIntervalMs : 60000)) / 1000);
                const cvText = n._counterValue != null ? String(n._counterValue) : '0';
                const qText = n._counterQ === 1 ? '1' : '0';
                el.innerHTML = `<i class="${meta.icon}"></i>`
                    + `<span class="counter-type-badge">CTU</span>`
                    + `<span class="counter-label">≥</span>`
                    + `<input type="text" class="counter-preset-input" value="${presetVal}" data-node-id="${n.id}" data-field="presetValue" data-raw="${presetVal}" title="${S.escHtml(S.t('logicflow.counter.preset_tip'))}">`
                    + `<span class="counter-label">${S.escHtml(S.t('logicflow.counter.interval_label'))}</span>`
                    + `<input type="text" class="counter-interval-input" value="${intervalSec}" data-node-id="${n.id}" data-field="cuMinIntervalSec" data-raw="${intervalSec}" title="${S.escHtml(S.t('logicflow.counter.interval_tip'))}">`
                    + `<span class="counter-unit">${S.escHtml(S.t('logicflow.timer.second'))}</span>`
                    + `<div class="node-live-value counter-live" data-node-id="${n.id}">cv:${cvText} / q:${qText}</div>`;
            } else if (n.type === 'algorithm' && n.operator && S.ALGO_OPS[n.operator]) {
                const aop = S.ALGO_OPS[n.operator];
                const langTag = aop.language === 'csharp' ? '<span class="ui-lang-tag">C#</span>' : '';
                let aHtml = `<span class="algo-op-badge" title="${S.escHtml(aop.label)}"><i class="fas fa-microchip"></i></span>`
                    + `<span style="font-size:.75rem">${S.escHtml(aop.label)}${langTag}</span>`;
                if (aop.variadic) {
                    const nVal = n.inputCount || 1;
                    aHtml += `<input type="number" class="algo-n-input" min="1" value="${nVal}" data-node-id="${n.id}" title="${S.escHtml(S.t('logicflow.algorithm.quantity'))}">`;
                }
                // 節點反灰策略：僅當「所有 perOutput 都 Error」才整顆反灰，
                // 混合狀態（部分 Error、部分 OK）下節點維持正常外觀，由邊線各自反灰 + tooltip。
                el.classList.remove('algo-status-bad', 'algo-status-warning', 'algo-status-error');
                if (n._algoStatus && n._algoStatus.perOutput) {
                    const poKeys = Object.keys(n._algoStatus.perOutput);
                    const allError = poKeys.length > 0 && poKeys.every(k =>
                        n._algoStatus.perOutput[k] && n._algoStatus.perOutput[k].severity === 'Error');
                    if (allError) {
                        el.classList.add('algo-status-bad', 'algo-status-error');
                        const nodeName = aop.label || n.operator;
                        el.title = nodeName + ' : ' + n._algoStatus.codeName + ' (' + n._algoStatus.severity + ')';
                    } else {
                        el.removeAttribute('title');
                    }
                } else {
                    el.removeAttribute('title');
                }
                el.innerHTML = aHtml;
            } else if ((n.type === 'contact_no' || n.type === 'contact_nc') && n.scheduleId != null) {
                // 排程綁定的接點
                const isOn = S.evalScheduleNow(n.scheduleId, n.type);
                const contactState = isOn != null ? (isOn ? S.t('logicflow.state.on') : S.t('logicflow.state.off')) : '--';
                const stateClass = isOn != null ? (isOn ? 'contact-on' : 'contact-off') : '';
                el.innerHTML = `<i class="${meta.icon}"></i><span>${S.escHtml(n.scheduleName || S.t('logicflow.node.schedule'))}</span>`
                    + `<span class="contact-state ${stateClass}">${contactState}</span>`
                    + `<div class="node-live-value"><i class="fas fa-calendar-alt me-1"></i>${S.escHtml(S.t('logicflow.node.schedule'))}</div>`;
            } else if ((n.type === 'contact_no' || n.type === 'contact_nc') && n.sid) {
                const lv = S.getEffectiveLv(n);
                const hasVal = !!(lv && lv.value != null);
                const valText = hasVal ? S.fmtNum(lv.value) : '--';
                const unitText = n.unit ? ` ${n.unit}` : '';
                const badClass = (lv && lv.quality === 'Bad') ? ' quality-bad' : '';
                const isOn = hasVal && (n.type === 'contact_no' ? parseFloat(lv.value) === 1 : parseFloat(lv.value) === 0);
                const contactState = hasVal ? (isOn ? S.t('logicflow.state.on') : S.t('logicflow.state.off')) : '--';
                const stateClass = hasVal ? (isOn ? 'contact-on' : 'contact-off') : '';
                el.innerHTML = `<i class="${meta.icon}"></i><span>${S.escHtml(label)}</span>` + histBadgeHtml(n)
                    + `<span class="contact-state ${stateClass}">${contactState}</span>`
                    + `<div class="node-live-value${badClass}" data-sid="${S.escHtml(n.sid)}">${S.escHtml(valText)}${S.escHtml(unitText)}</div>`;
            } else if ((n.type === 'contact_no' || n.type === 'contact_nc') && !n.sid && n.scheduleId == null) {
                // 無 SID、無排程的接點（純邏輯控制或未設定）
                el.innerHTML = `<i class="${meta.icon}"></i><span>${S.escHtml(label)}</span>` + unboundBadgeHtml(n)
                    + `<span class="contact-state">--</span>`
                    + `<div class="node-live-value"><i class="fas fa-project-diagram me-1"></i>${S.escHtml(S.t('logicflow.node.logic_control'))}</div>`;
            } else if ((n.type === 'input' || n.type === 'output') && n.sid) {
                const lv = S.getEffectiveLv(n);
                const valText = (lv && lv.value != null) ? S.fmtNum(lv.value) : '--';
                const unitText = n.unit ? ` ${n.unit}` : '';
                const badClass = (lv && lv.quality === 'Bad') ? ' quality-bad' : '';
                let modeBadgeHtml = '';
                if (n.type === 'output') {
                    const ctrl = S._controlModeCache[n.sid];
                    const isManual = ctrl && !ctrl.isAuto;
                    const modeClass = isManual ? 'mode-manual' : 'mode-auto';
                    const modeText = isManual ? S.t('logicflow.mode.manual') : S.t('logicflow.mode.auto');
                    modeBadgeHtml = `<span class="node-mode-badge ${modeClass}" data-sid="${S.escHtml(n.sid)}">${modeText}</span>`;
                    if (isManual) el.classList.add('output-manual');
                }
                el.innerHTML = `<i class="${meta.icon}"></i><span>${S.escHtml(label)}</span>` + histBadgeHtml(n)
                    + modeBadgeHtml
                    + `<div class="node-live-value${badClass}" data-sid="${S.escHtml(n.sid)}">${S.escHtml(valText)}${S.escHtml(unitText)}</div>`;
            } else {
                // 含「未綁點位的 input / output」——樹層級複製出的邏輯就長這樣
                el.innerHTML = `<i class="${meta.icon}"></i><span>${S.escHtml(label)}</span>` + unboundBadgeHtml(n);
            }

            // 輸入埠（左側圓點）— algorithm 節點使用動態展開 inputs（含 variadic N 倍展開）
            let nodeInputPorts;
            let nodeOutputPorts;
            if (n.type === 'algorithm' && n.operator && S.ALGO_OPS[n.operator]) {
                const ports = S.getAlgoPorts(S.ALGO_OPS[n.operator], n.inputCount);
                nodeInputPorts = ports.inputs;
                nodeOutputPorts = ports.outputs;
            } else {
                const portLbl = meta.portLabels || {};
                nodeInputPorts = (meta.inputs || []).map(k => ({ key: k, label: portLbl[k] ? S.t(portLbl[k]) : k }));
                nodeOutputPorts = (meta.outputs || []).map(k => ({ key: k, label: portLbl[k] ? S.t(portLbl[k]) : k }));
            }

            // variadic 演算法：每組 repeat 埠用虛線框圍起來（N≥2 才顯示，hover 出 tooltip）
            if (n.type === 'algorithm' && n.operator && S.ALGO_OPS[n.operator] && S.ALGO_OPS[n.operator].variadic) {
                const ranges = S.getAlgoGroupRanges(S.ALGO_OPS[n.operator], n.inputCount);
                ranges.forEach(r => {
                    const fr = document.createElement('div');
                    fr.className = 'algo-group-frame';
                    // 框 = 該組的帶範圍；內縮 1px 讓相鄰組框之間留一線縫、且不觸及固定帶
                    fr.style.top = `calc(${r.topPct}% + 1px)`;
                    fr.style.height = `calc(${r.bottomPct - r.topPct}% - 2px)`;
                    fr.title = S.t('logicflow.algorithm.group_index', { index: r.index });
                    el.appendChild(fr);
                });
            }

            nodeInputPorts.forEach((p, i, arr) => {
                const pe = document.createElement('div');
                pe.className = 'flow-port flow-port-in';
                pe.dataset.port = p.key; pe.dataset.nodeId = n.id; pe.dataset.dir = 'in';
                // variadic 埠帶 topPct（分帶佈局）；非 variadic 沿用等距分佈
                const pct = (p.topPct != null) ? p.topPct : (arr.length === 1 ? 50 : (20 + i * (60 / (arr.length - 1))));
                pe.style.top = `calc(${pct}% - 5px)`;
                pe.title = p.label;
                pe.addEventListener('mousedown', onPortMouseDown);
                el.appendChild(pe);
            });

            // 輸出埠（右側圓點）
            nodeOutputPorts.forEach((p, i, arr) => {
                const pe = document.createElement('div');
                pe.className = 'flow-port flow-port-out';
                pe.dataset.port = p.key; pe.dataset.nodeId = n.id; pe.dataset.dir = 'out';
                const pctOut = (p.topPct != null) ? p.topPct : (arr.length === 1 ? 50 : (20 + i * (60 / (arr.length - 1))));
                pe.style.top = `calc(${pctOut}% - 5px)`;
                pe.title = p.label;
                pe.addEventListener('mousedown', onPortMouseDown);
                el.appendChild(pe);
            });

            // 數學運算底部注入埠（val）
            if (n.type === 'math' && n.operator && S.MATH_OPS[n.operator] && S.MATH_OPS[n.operator].hasValue) {
                const bp = document.createElement('div');
                bp.className = 'flow-port flow-port-in flow-port-bottom';
                bp.dataset.port = 'val'; bp.dataset.nodeId = n.id; bp.dataset.dir = 'in';
                bp.title = 'val';
                bp.addEventListener('mousedown', onPortMouseDown);
                el.appendChild(bp);
            }

            // 計時器底部注入埠：TON/TPR 只有 delay；TP 有 delay + hold
            if (n.type === 'timer') {
                const timerPorts = (n.operator === 'ton' || n.operator === 'tpr') ? ['delay'] : ['delay', 'hold'];
                timerPorts.forEach((pName, i) => {
                    const bp = document.createElement('div');
                    bp.className = 'flow-port flow-port-in flow-port-bottom';
                    bp.dataset.port = pName; bp.dataset.nodeId = n.id; bp.dataset.dir = 'in';
                    if (timerPorts.length === 1) {
                        bp.style.left = 'calc(50% - 5px)';
                    } else {
                        bp.style.left = `calc(${33 + i * 34}% - 5px)`;
                    }
                    bp.title = pName === 'delay' ? S.t('logicflow.timer.delay_label') : S.t('logicflow.timer.hold_label');
                    bp.addEventListener('mousedown', onPortMouseDown);
                    el.appendChild(bp);
                });
            }

            // A/B 接點底部控制埠：可接邏輯閘(0/1)控制導通
            if (n.type === 'contact_no' || n.type === 'contact_nc') {
                const hasBinding = !!(n.sid || n.scheduleId != null && n.scheduleId !== undefined);
                const cp = document.createElement('div');
                cp.className = 'flow-port flow-port-in flow-port-bottom' + (hasBinding ? ' port-disabled' : '');
                cp.dataset.port = 'ctrl'; cp.dataset.nodeId = n.id; cp.dataset.dir = 'in';
                cp.style.left = 'calc(50% - 5px)';
                cp.title = hasBinding ? S.t('logicflow.port.bound_no_ctrl') : S.t('logicflow.port.control');
                cp.addEventListener('mousedown', onPortMouseDown);
                el.appendChild(cp);
            }

            // 雙擊 input/output/contact_no 節點 → 編輯點位
            if (n.type === 'input' || n.type === 'output' || n.type === 'contact_no' || n.type === 'contact_nc') {
                el.addEventListener('dblclick', (e) => {
                    e.preventDefault();
                    S.ppEditNodeId = n.id;
                    S.ppPendingType = n.type;
                    S.ppPendingPos = { x: n.x, y: n.y };
                    S.openPointPicker();
                });
            }

            // constant textbox：存值 + 阻止拖曳 + 千分位 focus/blur
            el.querySelectorAll('.const-value-input').forEach(inp => {
                inp.addEventListener('mousedown', (e) => e.stopPropagation());
                inp.addEventListener('focus', (e) => { e.target.value = e.target.dataset.raw ?? e.target.value; e.target.select(); });
                inp.addEventListener('blur', (e) => {
                    const raw = parseFloat(e.target.value) || 0;
                    e.target.dataset.raw = raw;
                    e.target.value = S.fmtNum(raw);
                    const nid = parseInt(e.target.dataset.nodeId);
                    const nd = S.canvasNodes.find(x => x.id === nid);
                    if (nd) {
                        if (e.target.classList.contains('const-value-input')) nd.constValue = raw;
                    }
                });
            });

            // counter preset / interval textbox：存值 + 阻止拖曳
            el.querySelectorAll('.counter-preset-input, .counter-interval-input').forEach(inp => {
                inp.addEventListener('mousedown', (e) => e.stopPropagation());
                inp.addEventListener('focus', (e) => { e.target.value = e.target.dataset.raw ?? e.target.value; e.target.select(); });
                inp.addEventListener('blur', (e) => {
                    const raw = parseInt(e.target.value, 10);
                    const field = e.target.dataset.field;
                    const nid = parseInt(e.target.dataset.nodeId);
                    const ndRef = S.canvasNodes.find(x => x.id === nid);
                    if (!ndRef) return;
                    if (field === 'presetValue') {
                        const val = (isNaN(raw) || raw < 1) ? 1 : raw;
                        ndRef.presetValue = val;
                        e.target.dataset.raw = val;
                        e.target.value = String(val);
                    } else if (field === 'cuMinIntervalSec') {
                        const valSec = (isNaN(raw) || raw < 0) ? 0 : raw;
                        ndRef.cuMinIntervalMs = valSec * 1000;
                        e.target.dataset.raw = valSec;
                        e.target.value = String(valSec);
                    }
                });
            });

            // timer textbox：存值 + 阻止拖曳 + 千分位 focus/blur
            el.querySelectorAll('.timer-value-input').forEach(inp => {
                inp.addEventListener('mousedown', (e) => e.stopPropagation());
                inp.addEventListener('focus', (e) => { e.target.value = e.target.dataset.raw ?? e.target.value; e.target.select(); });
                inp.addEventListener('blur', (e) => {
                    const raw = parseFloat(e.target.value);
                    const val = (isNaN(raw) || raw <= 0) ? 1 : raw;
                    e.target.dataset.raw = val;
                    e.target.value = S.fmtNum(val);
                    const nid = parseInt(e.target.dataset.nodeId);
                    const field = e.target.dataset.field;
                    const nd = S.canvasNodes.find(x => x.id === nid);
                    if (nd) {
                        if (field === 'delay') nd.timerDelay = val;
                        else if (field === 'hold') nd.timerHold = val;
                    }
                });
            });

            // variadic algorithm：底部加 resize handle（拖動調整節點高度）
            if (n.type === 'algorithm' && n.operator && S.ALGO_OPS[n.operator] && S.ALGO_OPS[n.operator].variadic) {
                const handle = document.createElement('div');
                handle.className = 'flow-node-resize-handle';
                handle.title = S.t('logicflow.port.resize_tip');
                handle.addEventListener('mousedown', (ev) => S.onResizeHandleMouseDown(ev, n.id));
                el.appendChild(handle);
            }

            // variadic algorithm 的 N 輸入框：改 N → 更新 inputCount + 清掉超出範圍的連線 + 重繪
            el.querySelectorAll('.algo-n-input').forEach(inp => {
                inp.addEventListener('mousedown', (e) => e.stopPropagation());
                inp.addEventListener('change', (e) => {
                    const nid = parseInt(e.target.dataset.nodeId);
                    const nd = S.canvasNodes.find(x => x.id === nid);
                    if (!nd) return;
                    const raw = parseInt(e.target.value, 10);
                    const newN = (isNaN(raw) || raw < 1) ? 1 : raw;
                    nd.inputCount = newN;
                    e.target.value = newN;
                    const aop = S.ALGO_OPS[nd.operator];
                    if (aop) {
                        const ports = S.getAlgoPorts(aop, newN);
                        const inputKeys = new Set(ports.inputs.map(p => p.key));
                        const outputKeys = new Set(ports.outputs.map(p => p.key));
                        S.canvasEdges = S.canvasEdges.filter(ed => {
                            if (ed.target === nid && !inputKeys.has(ed.targetPort)) return false;
                            if (ed.source === nid && !outputKeys.has(ed.sourcePort)) return false;
                            return true;
                        });
                        // 同步更新節點 algoInputs（執行階段以此為依據）
                        nd.algoInputs = ports.inputs.map(p => p.key);
                    }
                    // 清掉演算法快取，避免 N 變動後仍套用舊結果
                    nd._algoResult = null;
                    nd._algoCachedResult = null;
                    nd._algoCacheKey = null;
                    nd._algoFetching = false;
                    nd._algoStatus = null;
                    // N 變動 → 重算節點高度，避免埠位被擠在一起
                    const newH = S.computeAlgoNodeHeight(nd);
                    if (newH != null) nd.height = newH;
                    renderCanvasNodes();
                });
            });

            el.addEventListener('mousedown', S.startDrag);
            canvas.appendChild(el);
        }

        // 還原重繪前的輸入框 focus / 游標 / 當前輸入值（見上方保存邏輯）
        if (_focusSave) {
            let sel = `input[data-node-id="${_focusSave.nodeId}"]`;
            if (_focusSave.field) sel += `[data-field="${_focusSave.field}"]`;
            else if (_focusSave.cls) sel += `.${_focusSave.cls}`;
            const inp = canvas.querySelector(sel);
            if (inp) {
                if (_focusSave.value != null) inp.value = _focusSave.value;
                inp.focus();
                try {
                    if (_focusSave.selStart != null) inp.setSelectionRange(_focusSave.selStart, _focusSave.selEnd);
                } catch (_) { /* type=number 不支援 setSelectionRange，focus 已足夠 */ }
            }
        }

        renderEdges();
        updateCanvasSize();
        S.startTimerEval(); // 有 timer 節點時啟動 1 秒 interval
    }

    // =========== 畫布自動延伸 ===========
    function updateCanvasSize() {
        const canvas = document.getElementById('diagramCanvas');
        if (!canvas) return;
        const PAD = 120;
        let maxR = 0, maxB = 0;
        canvas.querySelectorAll('.flow-node').forEach(el => {
            maxR = Math.max(maxR, el.offsetLeft + el.offsetWidth);
            maxB = Math.max(maxB, el.offsetTop + el.offsetHeight);
        });
        maxR += PAD;
        maxB += PAD;

        // SVG 連線層需覆蓋整個可捲動區域
        const svg = canvas.querySelector('svg.edge-layer');
        if (svg) {
            svg.style.width = Math.max(maxR, canvas.clientWidth) + 'px';
            svg.style.height = Math.max(maxB, canvas.clientHeight) + 'px';
        }
    }

    // =========== 連線 (Edges) ===========
    function getPortPos(nodeId, portName) {
        const canvas = document.getElementById('diagramCanvas');
        if (!canvas) return null;
        const portEl = canvas.querySelector(`.flow-port[data-node-id="${nodeId}"][data-port="${portName}"]`);
        if (!portEl) return null;
        const cr = canvas.getBoundingClientRect();
        const pr = portEl.getBoundingClientRect();
        return { x: pr.left + pr.width / 2 - cr.left + canvas.scrollLeft, y: pr.top + pr.height / 2 - cr.top + canvas.scrollTop };
    }

    function bezierPath(x1, y1, x2, y2) {
        const dx = Math.max(Math.abs(x2 - x1) * 0.5, 40);
        return `M${x1},${y1} C${x1+dx},${y1} ${x2-dx},${y2} ${x2},${y2}`;
    }

    // 底部注入埠專用：從來源右側出發，彎入目標下方（箭頭朝上）
    function bezierPathToBottom(x1, y1, x2, y2) {
        const dx = Math.max(Math.abs(x2 - x1) * 0.3, 30);
        const dy = Math.max(Math.abs(y2 - y1) * 0.5, 40);
        return `M${x1},${y1} C${x1+dx},${y1} ${x2},${y2+dy} ${x2},${y2}`;
    }

    function renderEdges() {
        const canvas = document.getElementById('diagramCanvas');
        if (!canvas) return;
        const svg = canvas.querySelector('svg.edge-layer');
        if (!svg) return;

        let html = '<defs>'
            + '<marker id="ah" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0,8 3,0 6" fill="#6c757d"/></marker>'
            + '<marker id="ah-s" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0,8 3,0 6" fill="#0d6efd"/></marker>'
            + '<marker id="ah-ok" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0,8 3,0 6" fill="#198754"/></marker>'
            + '<marker id="ah-bad" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0,8 3,0 6" fill="#dc3545"/></marker>'
            + '<marker id="ah-warn" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0,8 3,0 6" fill="#fd7e14"/></marker>'
            + '</defs>';

        for (const edge of S.canvasEdges) {
            const from = getPortPos(edge.source, edge.sourcePort);
            const to = getPortPos(edge.target, edge.targetPort);
            if (!from || !to) continue;
            const isBottomPort = edge.targetPort === 'val' || edge.targetPort === 'delay' || edge.targetPort === 'hold' || edge.targetPort === 'ctrl';
            const d = isBottomPort
                ? bezierPathToBottom(from.x, from.y, to.x, to.y)
                : bezierPath(from.x, from.y, to.x, to.y);
            const sel = edge.id === S.selectedEdgeId;

            // 決定邊線顏色
            let edgeColor = '#6c757d';
            let edgeMarker = 'ah';
            let edgeLabel = null;
            let edgeTooltip = null;
            if (sel) {
                edgeColor = '#0d6efd'; edgeMarker = 'ah-s';
            } else {
                const srcNode = S.canvasNodes.find(n => n.id === edge.source);
                if (srcNode) {
                    if (srcNode.type === 'input' && srcNode._isBad) {
                        edgeColor = '#dc3545'; edgeMarker = 'ah-bad';
                    } else if (srcNode.type === 'input' && srcNode.sid) {
                        const lv = S.getEffectiveLv(srcNode);
                        if (lv != null && lv.value != null) {
                            edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            edgeLabel = S.fmtNum(parseFloat(lv.value));
                        }
                    } else if (srcNode.type === 'constant') {
                        edgeColor = '#198754'; edgeMarker = 'ah-ok';
                        edgeLabel = S.fmtNum(srcNode.constValue != null ? parseFloat(srcNode.constValue) : 0);
                    } else if (srcNode.type === 'compare') {
                        if (srcNode._compareResult === true) {
                            edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            edgeLabel = '1';
                        } else if (srcNode._compareResult === false) {
                            edgeColor = '#adb5bd';
                            edgeLabel = '0';
                        } else {
                            edgeColor = '#adb5bd';
                        }
                    } else if (srcNode.type === 'math') {
                        if (srcNode._mathResult != null) {
                            edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            edgeLabel = S.fmtNum(srcNode._mathResult);
                        } else {
                            edgeColor = '#adb5bd';
                        }
                    } else if (['and','or','not','xor'].includes(srcNode.type)) {
                        if (srcNode._gateResult != null) {
                            edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            edgeLabel = String(srcNode._gateResult);
                        } else {
                            edgeColor = '#adb5bd';
                        }
                    } else if (srcNode.type === 'contact_no' || srcNode.type === 'contact_nc') {
                        if (srcNode._contactOn === true && srcNode._contactResult != null) {
                            edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            edgeLabel = S.fmtNum(srcNode._contactResult);
                        } else if (srcNode._contactOn === false) {
                            edgeColor = '#adb5bd';
                            edgeLabel = srcNode._contactResult === 0 ? '0' : null;
                        } else {
                            edgeColor = '#adb5bd';
                        }
                    } else if (srcNode.type === 'timer') {
                        if (srcNode.operator === 'ton') {
                            if (srcNode._tonPhase === 'on' && srcNode._timerResult != null) {
                                edgeColor = '#198754'; edgeMarker = 'ah-ok';
                                edgeLabel = S.fmtNum(srcNode._timerResult);
                            } else if (srcNode._tonPhase === 'timing' && srcNode._tonPhaseEnd) {
                                edgeColor = '#adb5bd';
                                const rem = Math.ceil((srcNode._tonPhaseEnd - Date.now()) / 1000);
                                edgeLabel = Math.max(rem, 0) + 's';
                            } else if (srcNode._timerResult === 0) {
                                edgeColor = '#adb5bd';
                                edgeLabel = '0';
                            } else {
                                edgeColor = '#adb5bd';
                            }
                        } else if (srcNode.operator === 'tpr') {
                            if (srcNode._tpPhase === 'confirmed' && srcNode._timerResult != null) {
                                edgeColor = '#198754'; edgeMarker = 'ah-ok';
                                edgeLabel = S.fmtNum(srcNode._timerResult);
                            } else if (srcNode._tpPhase === 'delay' && srcNode._tpPhaseEnd) {
                                edgeColor = '#adb5bd';
                                const rem = Math.ceil((srcNode._tpPhaseEnd - Date.now()) / 1000);
                                edgeLabel = Math.max(rem, 0) + 's';
                            } else {
                                edgeColor = '#adb5bd';
                            }
                        } else {
                            if (srcNode._tpPhase === 'hold' && srcNode._timerResult != null) {
                                edgeColor = '#198754'; edgeMarker = 'ah-ok';
                                edgeLabel = S.fmtNum(srcNode._timerResult);
                            } else if (srcNode._tpPhase === 'delay' && srcNode._tpPhaseEnd) {
                                edgeColor = '#adb5bd';
                                const rem = Math.ceil((srcNode._tpPhaseEnd - Date.now()) / 1000);
                                edgeLabel = Math.max(rem, 0) + 's';
                            } else if (srcNode._timerResult === 0) {
                                edgeColor = '#adb5bd';
                                edgeLabel = '0';
                            } else {
                                edgeColor = '#adb5bd';
                            }
                        }
                    } else if (srcNode.type === 'counter') {
                        if (edge.sourcePort === 'cv') {
                            edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            edgeLabel = String(srcNode._counterValue != null ? srcNode._counterValue : 0);
                        } else {
                            const q = srcNode._counterQ != null ? srcNode._counterQ : 0;
                            if (q === 1) {
                                edgeColor = '#198754'; edgeMarker = 'ah-ok';
                                edgeLabel = '1';
                            } else {
                                edgeColor = '#adb5bd';
                                edgeLabel = '0';
                            }
                        }
                    } else if (srcNode.type === 'algorithm') {
                        // 演算法節點：per-output 反灰／顏色／tooltip。依 edge.sourcePort 取對應輸出值與 status。
                        var portKey = edge.sourcePort || 'out';

                        // variadic 部分組未備齊：對應 repeat 輸出埠（key 結尾數字）強制反灰；
                        // 結尾無數字 = fixed 輸出，只要有任何組沒備齊，整體結果不可靠，一併反灰。
                        var unreadyGroups = srcNode._algoUnreadyGroups;
                        var isUnreadyGroupPort = false;
                        if (unreadyGroups && unreadyGroups.size > 0) {
                            var mGroup = portKey.match(/(\d+)$/);
                            if (mGroup) {
                                if (unreadyGroups.has(parseInt(mGroup[1], 10))) isUnreadyGroupPort = true;
                            } else {
                                isUnreadyGroupPort = true;
                            }
                        }

                        if (isUnreadyGroupPort) {
                            edgeColor = '#adb5bd';
                        } else {
                            var algoOutVal = null;
                            var resObj = srcNode._algoResult || srcNode._algoCachedResult;
                            if (resObj && typeof resObj === 'object') {
                                algoOutVal = resObj[portKey];
                            }
                            var portStatus = (srcNode._algoStatus && srcNode._algoStatus.perOutput)
                                ? srcNode._algoStatus.perOutput[portKey] : null;
                            var portSev = portStatus ? (portStatus.severity || 'Info') : 'Info';
                            if (portSev === 'Error') {
                                // Error：線不斷、僅變淡（灰色 + 預設箭頭），值維持顯示
                                edgeColor = '#adb5bd';
                                if (algoOutVal != null) edgeLabel = S.fmtNum(algoOutVal);
                            } else if (portSev === 'Warning') {
                                edgeColor = '#fd7e14'; edgeMarker = 'ah-warn';
                                if (algoOutVal != null) edgeLabel = S.fmtNum(algoOutVal);
                            } else if (algoOutVal != null) {
                                edgeColor = '#198754'; edgeMarker = 'ah-ok';
                                edgeLabel = S.fmtNum(algoOutVal);
                            } else if (srcNode._algoReady) {
                                edgeColor = '#198754'; edgeMarker = 'ah-ok';
                            } else {
                                edgeColor = '#adb5bd';
                            }
                            if (portStatus && portStatus.statusCodeId !== 0) {
                                var algoLbl = (S.ALGO_OPS[srcNode.operator] && S.ALGO_OPS[srcNode.operator].label) || srcNode.operator || '';
                                edgeTooltip = algoLbl + ' : ' + portKey + ' : ' + portStatus.statusCodeName + ' (' + portSev + ')';
                            }
                        }
                    }
                }
            }

            // 目標為超限的 output 節點：邊線標紅 + 附加「超限」文字
            const tgtNode = S.canvasNodes.find(n => n.id === edge.target);
            if (tgtNode && tgtNode.type === 'output' && tgtNode._isOutOfRange && !sel) {
                edgeColor = '#dc3545'; edgeMarker = 'ah-bad';
                if (edgeLabel != null) edgeLabel += ' ' + S.t('logicflow.edge.out_of_range');
                else edgeLabel = S.t('logicflow.edge.out_of_range');
            }

            // 自閉合 <path/> 不能含 <title> 子元素；有 tooltip 時改開合 tag 包 <title>
            if (edgeTooltip != null) {
                html += `<path d="${d}" class="edge-line${sel ? ' selected' : ''}" data-edge-id="${edge.id}"
                          stroke="${edgeColor}" stroke-width="${sel ? 3 : 2}" fill="none"
                          marker-end="url(#${edgeMarker})" style="pointer-events:stroke;cursor:pointer;"><title>${S.escHtml(edgeTooltip)}</title></path>`;
                html += `<path d="${d}" stroke="transparent" stroke-width="14" fill="none"
                          style="pointer-events:stroke;cursor:pointer;" data-edge-id="${edge.id}"><title>${S.escHtml(edgeTooltip)}</title></path>`;
            } else {
                html += `<path d="${d}" class="edge-line${sel ? ' selected' : ''}" data-edge-id="${edge.id}"
                          stroke="${edgeColor}" stroke-width="${sel ? 3 : 2}" fill="none"
                          marker-end="url(#${edgeMarker})" style="pointer-events:stroke;cursor:pointer;"/>`;
                html += `<path d="${d}" stroke="transparent" stroke-width="14" fill="none"
                          style="pointer-events:stroke;cursor:pointer;" data-edge-id="${edge.id}"/>`;
            }

            // 邊線上顯示結果文字
            if (edgeLabel != null) {
                const mx = (from.x + to.x) / 2, my = (from.y + to.y) / 2;
                const tw = Math.max(edgeLabel.length * 7.5 + 8, 20);
                html += `<rect x="${mx - tw/2}" y="${my - 9}" width="${tw}" height="18" rx="4" fill="#fff" stroke="${edgeColor}" stroke-width="1"/>`;
                html += `<text x="${mx}" y="${my + 5}" text-anchor="middle" font-size="12" font-weight="700" fill="${edgeColor}">${edgeLabel}</text>`;
            }
        }

        // 拖曳中的臨時虛線
        if (S.draggingEdge && S.draggingEdge.tempX != null) {
            const d = bezierPath(S.draggingEdge.startX, S.draggingEdge.startY, S.draggingEdge.tempX, S.draggingEdge.tempY);
            html += `<path d="${d}" stroke="#0d6efd" stroke-width="2" fill="none" stroke-dasharray="6,3" style="pointer-events:none;"/>`;
        }

        svg.innerHTML = html;

        // 綁定連線點擊事件（切換選取）
        svg.querySelectorAll('[data-edge-id]').forEach(p => {
            p.addEventListener('click', (e) => {
                e.stopPropagation();
                const eid = parseInt(p.dataset.edgeId);
                S.selectedEdgeId = S.selectedEdgeId === eid ? null : eid;
                renderEdges();
            });
        });
    }

    function onPortMouseDown(e) {
        const dir = e.currentTarget.dataset.dir;
        if (dir !== 'out') return; // 輸入埠不攔截，讓事件繼續冒泡（可拖曳節點）
        e.stopPropagation();
        e.preventDefault();

        const nodeId = parseInt(e.currentTarget.dataset.nodeId);
        const portName = e.currentTarget.dataset.port;
        const pos = getPortPos(nodeId, portName);
        if (!pos) return;

        S.draggingEdge = { sourceId: nodeId, sourcePort: portName, startX: pos.x, startY: pos.y, tempX: pos.x, tempY: pos.y };

        function onMove(ev) {
            const cvs = document.getElementById('diagramCanvas');
            const cr = cvs.getBoundingClientRect();
            S.draggingEdge.tempX = ev.clientX - cr.left + cvs.scrollLeft;
            S.draggingEdge.tempY = ev.clientY - cr.top + cvs.scrollTop;
            renderEdges();
        }
        function onUp(ev) {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            const tgt = document.elementFromPoint(ev.clientX, ev.clientY);
            if (tgt && tgt.classList.contains('flow-port') && tgt.dataset.dir === 'in') {
                const tId = parseInt(tgt.dataset.nodeId);
                const tPort = tgt.dataset.port;
                if (tId !== S.draggingEdge.sourceId) {
                    // ctrl 埠防呆：已設定點位或排程的接點不可使用 ctrl 控制埠
                    if (tPort === 'ctrl') {
                        var tNode = S.canvasNodes.find(function(nn) { return nn.id === tId; });
                        if (tNode && (tNode.sid || tNode.scheduleId != null)) {
                            alert(S.t('logicflow.error.contact_ctrl_conflict'));
                            S.draggingEdge = null;
                            renderEdges();
                            return;
                        }
                    }
                    // 每個 input port 只允許一條連線
                    const occupied = S.canvasEdges.some(e => e.target === tId && e.targetPort === tPort);
                    if (!occupied) {
                        S.canvasEdges.push({ id: S.nextEdgeId++, source: S.draggingEdge.sourceId, sourcePort: S.draggingEdge.sourcePort, target: tId, targetPort: tPort });
                        maybeSuggestDpRetZero(S.draggingEdge.sourceId, tId, tPort);
                    }
                }
            }
            S.draggingEdge = null;
            renderEdges();
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    }

    // 壓差計直入輔助：名稱像壓差的點位接上「壓差Fuzzy水泵頻率」的供水壓 (p_sup) 時，
    // 徵詢是否自動在回水壓 (p_ret) 接常數 0（壓差直入用法，見 dp_fuzzy_pump_freq description）。
    // 關鍵字為啟發式判斷，confirm 制、按取消即無事；p_ret 已接線則不打擾。
    function maybeSuggestDpRetZero(srcId, tId, tPort) {
        if (tPort !== 'p_sup') return;
        const tNode = S.canvasNodes.find(n => n.id === tId);
        if (!tNode || tNode.type !== 'algorithm' || tNode.operator !== 'dp_fuzzy_pump_freq') return;
        const sNode = S.canvasNodes.find(n => n.id === srcId);
        if (!sNode || sNode.type !== 'input' || !/壓差|差壓|dp/i.test(sNode.pointName || '')) return;
        if (S.canvasEdges.some(e => e.target === tId && e.targetPort === 'p_ret')) return;
        if (!confirm(S.t('logicflow.confirm.dp_direct_pret_zero'))) return;
        const pos = getPortPos(tId, 'p_ret');
        const cNode = {
            id: S.nextNodeId++, type: 'constant',
            x: Math.max(0, tNode.x - 200),
            y: pos ? Math.max(0, Math.round((pos.y - 20) / 20) * 20) : tNode.y + 60,
            constValue: 0
        };
        S.canvasNodes.push(cNode);
        S.canvasEdges.push({ id: S.nextEdgeId++, source: cNode.id, sourcePort: 'out', target: tId, targetPort: 'p_ret' });
        S.renderCanvasNodes();
    }

    function deleteSelectedEdge() {
        if (S.selectedEdgeId == null) return;
        S.canvasEdges = S.canvasEdges.filter(e => e.id !== S.selectedEdgeId);
        S.selectedEdgeId = null;
        renderEdges();
    }

    // 暴露給其他模組
    S.renderCanvasNodes = renderCanvasNodes;
    S.updateCanvasSize = updateCanvasSize;
    S.getPortPos = getPortPos;
    S.renderEdges = renderEdges;
    S.deleteSelectedEdge = deleteSelectedEdge;
})();
