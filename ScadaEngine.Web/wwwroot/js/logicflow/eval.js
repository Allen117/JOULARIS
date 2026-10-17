// LogicFlow 即時值輪詢 + 節點求值 + 排程判斷
// 註：evalOneNode 約 600 行單一函式，本檔僅做檔案層搬移，未拆內部 switch（見 plan 決策 3）
(function () {
    const S = window.__lfNS;

    // 歷史值快取：key = 'sid|offsetMinutes'，依分鐘 bucket 快取查詢結果，避免每輪 eval 打 API
    S._histCache = S._histCache || {};
    S._histFetching = S._histFetching || {};

    // =========== 歷史值讀取 ===========

    // 取得啟用歷史讀取節點的顯示值；查無資料 → { value: null, quality: 'Bad' }
    function getHistLv(nd) {
        if (!nd.histEnabled || !nd.sid || nd.histOffsetMinutes == null) return null;
        var entry = S._histCache[nd.sid + '|' + nd.histOffsetMinutes];
        if (entry && entry.found) return { value: entry.value, quality: 'Good' };
        return { value: null, quality: 'Bad' };
    }

    // 節點有效值：啟用歷史讀取 → 歷史快取；否則即時快取
    function getEffectiveLv(nd) {
        if (nd.histEnabled && nd.sid && nd.histOffsetMinutes != null) return getHistLv(nd);
        return nd.sid ? S._realtimeCache[nd.sid] : null;
    }

    // 抓取所有啟用歷史讀取節點的歷史值（每 (sid, offset) 組合每分鐘最多查一次）
    async function fetchHistoryValues() {
        var histNodes = S.canvasNodes.filter(function (n) {
            return n.histEnabled && n.sid && n.histOffsetMinutes != null
                && (n.type === 'input' || n.type === 'contact_no' || n.type === 'contact_nc');
        });
        if (histNodes.length === 0) return;

        var nMinute = Math.floor(Date.now() / 60000);
        var keys = new Set();
        histNodes.forEach(function (n) { keys.add(n.sid + '|' + n.histOffsetMinutes); });

        var fetches = [];
        keys.forEach(function (key) {
            var entry = S._histCache[key];
            if (entry && entry.minute === nMinute) return;   // 本分鐘已查過
            if (S._histFetching[key]) return;                 // 查詢中
            var parts = key.split('|');
            S._histFetching[key] = true;
            fetches.push(
                fetch(S.API + '/history-value?sid=' + encodeURIComponent(parts[0]) + '&offsetMinutes=' + parts[1])
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (data) {
                        S._histCache[key] = {
                            found: !!(data && data.found),
                            value: (data && data.found) ? data.value : null,
                            minute: nMinute
                        };
                    })
                    .catch(function () { S._histCache[key] = { found: false, value: null, minute: nMinute }; })
                    .then(function () { S._histFetching[key] = false; })
            );
        });
        if (fetches.length > 0) await Promise.all(fetches);
    }

    // =========== 即時值輪詢 ===========
    function startRealtimePolling() {
        stopRealtimePolling();
        fetchRealtimeValues();
    }

    function stopRealtimePolling() {
        if (S._realtimeTimer) { clearTimeout(S._realtimeTimer); S._realtimeTimer = null; }
        stopTimerEval();
    }

    // 計時器專用 1 秒 interval（讓倒數與輸出即時更新）
    function startTimerEval() {
        stopTimerEval();
        var needInterval = S.canvasNodes.some(n => n.type === 'timer')
            || S.canvasNodes.some(n => (n.type === 'contact_no' || n.type === 'contact_nc') && n.scheduleId != null)
            || S.canvasNodes.some(n => n.type === 'counter');
        if (needInterval) {
            S._timerEvalInterval = setInterval(() => evaluateNodes(), 1000);
        }
    }

    function stopTimerEval() {
        if (S._timerEvalInterval) { clearInterval(S._timerEvalInterval); S._timerEvalInterval = null; }
    }

    async function fetchRealtimeValues() {
        const sids = S.canvasNodes.filter(n => n.sid).map(n => n.sid);
        if (sids.length === 0) {
            // 純排程畫布也需要 evaluate + render
            evaluateNodes();
            S.renderCanvasNodes();
            S._realtimeTimer = setTimeout(fetchRealtimeValues, S._pollInterval);
            return;
        }

        // ── 1. 拉即時值（獨立 try-catch，不受控制模式 API 影響） ──
        try {
            const resp = await fetch('/api/realtime/by-sids', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(sids)
            });
            const json = await resp.json();
            if (json.success && json.data) {
                const returnedSids = json.data.map(d => d.sid);
                const missingSids = sids.filter(s => !returnedSids.includes(s));
                if (missingSids.length > 0) {
                    console.warn('[LogicFlow] missing SID in API response:', missingSids);
                }
                for (const item of json.data) {
                    S._realtimeCache[item.sid] = { value: item.value, quality: item.quality };
                }
            } else {
                console.warn('[LogicFlow] API response error:', json);
            }
        } catch (e) {
            console.warn('[LogicFlow] realtime fetch error:', e);
        }

        // ── 2. 拉控制模式（獨立 try-catch） ──
        try {
            const ctrlResp = await fetch('/api/control/manual-values');
            if (ctrlResp.ok) {
                const ctrlJson = await ctrlResp.json();
                S._controlModeCache = ctrlJson || {};
            }
        } catch (_) { /* 控制模式拉取失敗不影響即時值顯示 */ }

        // ── 2.5 拉歷史值（啟用歷史讀取的節點；依分鐘 bucket 快取） ──
        try {
            await fetchHistoryValues();
        } catch (_) { /* 歷史值拉取失敗 → 快取維持舊值或 Bad */ }

        // ── 3. 局部更新 DOM（用節點唯一 ID 定位，避免重複 SID 問題） ──
        const canvas = document.getElementById('diagramCanvas');
        if (canvas) {
            for (const nd of S.canvasNodes) {
                if (!nd.sid) continue;
                const lv = getEffectiveLv(nd);
                if (!lv) continue;
                const nodeEl = canvas.querySelector(`.flow-node[data-node-id="${nd.id}"]`);
                if (!nodeEl) continue;
                const valEl = nodeEl.querySelector('.node-live-value');
                if (valEl) {
                    const unitText = nd.unit ? ` ${nd.unit}` : '';
                    valEl.textContent = (lv.value != null ? S.fmtNum(lv.value) : '--') + unitText;
                    valEl.classList.toggle('quality-bad', lv.quality === 'Bad');
                }
                if (nd.type === 'contact_no' || nd.type === 'contact_nc') {
                    const stateEl = nodeEl.querySelector('.contact-state');
                    if (stateEl) {
                        const pv = lv.value != null ? parseFloat(lv.value) : NaN;
                        const isOn = !isNaN(pv) && (nd.type === 'contact_no' ? pv === 1 : pv === 0);
                        stateEl.textContent = isOn ? S.t('logicflow.state.on') : S.t('logicflow.state.off');
                        stateEl.classList.toggle('contact-on', isOn);
                        stateEl.classList.toggle('contact-off', !isOn);
                    }
                }
                if (nd.type === 'output') {
                    const ctrl = S._controlModeCache[nd.sid];
                    const isManual = ctrl && !ctrl.isAuto;
                    const badge = nodeEl.querySelector('.node-mode-badge');
                    if (badge) {
                        badge.textContent = isManual ? S.t('logicflow.mode.manual') : S.t('logicflow.mode.auto');
                        badge.classList.toggle('mode-manual', isManual);
                        badge.classList.toggle('mode-auto', !isManual);
                    }
                    nodeEl.classList.toggle('output-manual', isManual);
                }
            }
        }
        // ── 4. 更新排程接點的 ON/OFF 狀態 ──
        if (canvas) {
            for (const nd of S.canvasNodes) {
                if ((nd.type !== 'contact_no' && nd.type !== 'contact_nc') || nd.scheduleId == null) continue;
                const nodeEl = canvas.querySelector(`.flow-node[data-node-id="${nd.id}"]`);
                if (!nodeEl) continue;
                const stateEl = nodeEl.querySelector('.contact-state');
                if (!stateEl) continue;
                const isOn = evalScheduleNow(nd.scheduleId, nd.type);
                stateEl.textContent = isOn != null ? (isOn ? S.t('logicflow.state.on') : S.t('logicflow.state.off')) : '--';
                stateEl.classList.toggle('contact-on', !!isOn);
                stateEl.classList.toggle('contact-off', isOn != null && !isOn);
            }
        }
        evaluateNodes();
        // ── 5. 更新 ctrl 埠驅動的接點 ON/OFF 狀態 ──
        if (canvas) {
            for (const nd of S.canvasNodes) {
                if (nd.type !== 'contact_no' && nd.type !== 'contact_nc') continue;
                var hasCtrl = S.canvasEdges.some(function(e) { return e.target === nd.id && e.targetPort === 'ctrl'; });
                if (!hasCtrl) continue;
                var nodeEl = canvas.querySelector('.flow-node[data-node-id="' + nd.id + '"]');
                if (!nodeEl) continue;
                var stateEl = nodeEl.querySelector('.contact-state');
                if (!stateEl) continue;
                var isOn = nd._contactOn !== undefined ? nd._contactOn : (nd._contactResult != null && nd._contactResult !== 0);
                var evaluated = nd._contactOn !== undefined || nd._contactResult != null;
                stateEl.textContent = evaluated ? (isOn ? S.t('logicflow.state.on') : S.t('logicflow.state.off')) : '--';
                stateEl.classList.toggle('contact-on', isOn);
                stateEl.classList.toggle('contact-off', evaluated && !isOn);
            }
        }
        // 永遠排下一次輪詢（不論成功或失敗）
        S._realtimeTimer = setTimeout(fetchRealtimeValues, S._pollInterval);
    }

    // ── 標記 input 節點的 Bad 品質 ──
    function markBadInputs() {
        for (const nd of S.canvasNodes) {
            if ((nd.type === 'input' || nd.type === 'contact_no' || nd.type === 'contact_nc') && nd.sid) {
                if (nd.histEnabled && nd.histOffsetMinutes != null) {
                    // 歷史值讀取：查無值即視為 Bad（規格：找不到 → 不進行下一步演算）
                    const hv = getHistLv(nd);
                    nd._isBad = !(hv && hv.value != null);
                } else {
                    const lv = S._realtimeCache[nd.sid];
                    nd._isBad = lv ? lv.quality === 'Bad' : false;
                }
            } else {
                nd._isBad = false;
            }
        }
    }

    // ── 檢查某節點的上游是否有 Bad 品質來源（遞迴） ──
    function hasUpstreamBad(nodeId, visited) {
        if (!visited) visited = new Set();
        if (visited.has(nodeId)) return false;
        visited.add(nodeId);
        const nd = S.canvasNodes.find(n => n.id === nodeId);
        if (!nd) return false;
        if (nd.type === 'input' || nd.type === 'contact_no' || nd.type === 'contact_nc') return !!nd._isBad;
        const inEdges = S.canvasEdges.filter(e => e.target === nodeId);
        for (const e of inEdges) {
            if (hasUpstreamBad(e.source, visited)) return true;
        }
        return false;
    }

    // ── 取得某節點的輸出數值（演算法節點支援多輸出，依 sourcePort 索引） ──
    function getNodeOutputValue(nodeId, sourcePort) {
        const nd = S.canvasNodes.find(n => n.id === nodeId);
        if (!nd) return null;
        const port = sourcePort || 'out';
        if (nd.type === 'input' && nd.sid) {
            if (nd._isBad) return null;
            const lv = getEffectiveLv(nd);
            return (lv != null && lv.value != null) ? parseFloat(lv.value) : null;
        }
        if (nd.type === 'constant') {
            return nd.constValue != null ? parseFloat(nd.constValue) : 0;
        }
        if (nd.type === 'math') {
            return nd._mathResult != null ? nd._mathResult : null;
        }
        if (nd.type === 'compare') {
            if (nd._compareResult === true) return 1;
            if (nd._compareResult === false) return 0;
            return null;
        }
        if (['and','or','not','xor'].includes(nd.type)) {
            return nd._gateResult != null ? nd._gateResult : null;
        }
        if (nd.type === 'timer') {
            return nd._timerResult != null ? nd._timerResult : null;
        }
        if (nd.type === 'contact_no' || nd.type === 'contact_nc') {
            return nd._contactResult != null ? nd._contactResult : null;
        }
        if (nd.type === 'algorithm') {
            const live = nd._algoResult;
            if (live && typeof live === 'object' && live[port] != null) return live[port];
            // 非同步等待期間：沿用上次快取值，避免輸出 0 導致下游閃爍
            const cached = nd._algoCachedResult;
            if (cached && typeof cached === 'object' && cached[port] != null) return cached[port];
            return null;
        }
        if (nd.type === 'counter') {
            if (port === 'cv') return nd._counterValue != null ? nd._counterValue : 0;
            // q：跨 tick 保留以支援 q→reset 自回授
            return nd._counterQ != null ? nd._counterQ : 0;
        }
        return null;
    }

    // ── 取得連到某節點某埠的來源節點輸出值 ──
    function getInputValue(nodeId, portName) {
        const edge = S.canvasEdges.find(e => e.target === nodeId && e.targetPort === portName);
        if (!edge) return null;
        return getNodeOutputValue(edge.source, edge.sourcePort);
    }

    // ── auto 注入輸入（@inputs_auto_repeat）：從指定輸出 port 往下游找第一個 output 節點 ──
    // 與 Engine 的 per-port FindDownstreamOutputSidFromPort 對應（起始邊只走 sourcePort 相符者）
    function findDownstreamOutputNodeFromPort(startNodeId, sourcePort) {
        var visited = new Set([startNodeId]);
        var queue = [];
        S.canvasEdges
            .filter(function (e) { return e.source === startNodeId && e.sourcePort === sourcePort; })
            .forEach(function (e) { queue.push(e.target); });
        while (queue.length > 0) {
            var nid = queue.shift();
            if (visited.has(nid)) continue;
            visited.add(nid);
            var target = S.canvasNodes.find(function (n) { return n.id === nid; });
            if (!target) continue;
            if (target.type === 'output' && target.sid) return target;
            S.canvasEdges.filter(function (e) { return e.source === nid; }).forEach(function (e) { queue.push(e.target); });
        }
        return null;
    }

    // ── TPR 回饋偵測：從指定節點往下游找 output 節點，讀取其即時值 ──
    function getTprFeedbackValue(startNodeId) {
        var visited = new Set([startNodeId]);
        var queue = [];
        S.canvasEdges.filter(e => e.source === startNodeId).forEach(e => queue.push(e.target));
        while (queue.length > 0) {
            var nid = queue.shift();
            if (visited.has(nid)) continue;
            visited.add(nid);
            var target = S.canvasNodes.find(n => n.id === nid);
            if (!target) continue;
            if (target.type === 'output' && target.sid) {
                var lv = S._realtimeCache[target.sid];
                return lv ? parseFloat(lv.value) : null;
            }
            S.canvasEdges.filter(e => e.source === nid).forEach(e => queue.push(e.target));
        }
        return null;
    }

    // ── 排程即時評估 — 委派給共用 window.ScheduleEval（plan 決策 3）──
    // 完整實作見 /js/common/schedule-eval.js，由 ScadaPage 與 LogicFlow 共用
    function evalScheduleNow(scheduleId, nodeType) {
        return window.ScheduleEval.evalScheduleNow(scheduleId, nodeType, S.ppAllSchedules);
    }

    // 從 Engine（經 MQTT → Web API）取得 TP 計時器的實際狀態，注入到前端節點
    async function syncTimerStateFromEngine(treeId) {
        try {
            const resp = await fetch(S.API + '/timer-state/' + treeId);
            if (!resp.ok) return;
            const states = await resp.json();
            // states 格式: { "treeId-nodeId": { phase, phaseEndMs, hasHeld } }
            for (const nd of S.canvasNodes) {
                if (nd.type !== 'timer') continue;
                const key = treeId + '-' + nd.id;
                const st = states[key];
                if (!st || !st.phase) continue;
                if (nd.operator === 'ton') {
                    nd._tonPhase = st.phase;
                    nd._tonPhaseEnd = st.phaseEndMs;
                } else {
                    nd._tpPhase = st.phase;
                    nd._tpPhaseEnd = st.phaseEndMs;
                    nd._tpHasHeld = st.hasHeld;
                }
                const remaining = st.phaseEndMs - Date.now();
                if (remaining > 0) {
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), remaining);
                }
            }
        } catch (e) {
            // 取不到就走原本的前端初始化邏輯
        }
    }

    // ── 單節點求值（回傳是否成功算出結果） ──
    function evalOneNode(nd) {
        if (nd.type === 'math' && nd.operator && S.MATH_OPS[nd.operator]) {
            nd._mathResult = null;
            if (hasUpstreamBad(nd.id)) return false;
            const v = getInputValue(nd.id, 'in');
            if (v == null) return false;
            const mop = S.MATH_OPS[nd.operator];
            let p = 0;
            if (mop.hasValue) {
                const pVal = getInputValue(nd.id, 'val');
                if (pVal == null) return false;
                p = pVal;
            }
            switch (nd.operator) {
                case 'add':   nd._mathResult = v + p;            break;
                case 'sub':   nd._mathResult = v - p;            break;
                case 'mul':   nd._mathResult = v * p;            break;
                case 'div':   nd._mathResult = p !== 0 ? v / p : null; break;
                case 'mod':   nd._mathResult = p !== 0 ? v % p : null; break;
                case 'pow':   nd._mathResult = Math.pow(v, p);   break;
                case 'abs':   nd._mathResult = Math.abs(v);      break;
                case 'sqrt':  nd._mathResult = v >= 0 ? Math.sqrt(v) : null; break;
                case 'round': nd._mathResult = Math.round(v);    break;
            }
            return nd._mathResult != null;
        }
        if (nd.type === 'compare' && nd.operator && S.COMPARE_OPS[nd.operator]) {
            nd._compareResult = null;
            if (hasUpstreamBad(nd.id)) return false;
            const a = getInputValue(nd.id, 'a');
            const b = getInputValue(nd.id, 'b');
            if (a == null || b == null) return false;
            switch (nd.operator) {
                case 'lt':  nd._compareResult = a < b;  break;
                case 'gt':  nd._compareResult = a > b;  break;
                case 'lte': nd._compareResult = a <= b; break;
                case 'gte': nd._compareResult = a >= b; break;
                case 'eq':  nd._compareResult = a === b; break;
                case 'neq': nd._compareResult = a !== b; break;
                default:    return false;
            }
            return true;
        }
        const GATE_TYPES = ['and', 'or', 'not', 'xor'];
        if (GATE_TYPES.includes(nd.type)) {
            nd._gateResult = null;
            if (hasUpstreamBad(nd.id)) return false;
            if (nd.type === 'not') {
                const v = getInputValue(nd.id, 'in');
                if (v == null || (v !== 0 && v !== 1)) return false;
                nd._gateResult = v === 0 ? 1 : 0;
            } else {
                const a = getInputValue(nd.id, 'a');
                const b = getInputValue(nd.id, 'b');
                if (a == null || b == null || (a !== 0 && a !== 1) || (b !== 0 && b !== 1)) return false;
                switch (nd.type) {
                    case 'and': nd._gateResult = (a === 1 && b === 1) ? 1 : 0; break;
                    case 'or':  nd._gateResult = (a === 1 || b === 1) ? 1 : 0; break;
                    case 'xor': nd._gateResult = (a !== b) ? 1 : 0;            break;
                }
            }
            return nd._gateResult != null;
        }
        // ── 計時器 ──
        if (nd.type === 'timer') {
            // ── TON 延時開啟 ──
            if (nd.operator === 'ton') {
                const inEdge = S.canvasEdges.find(e => e.target === nd.id && e.targetPort === 'in');
                let inputVal = null;
                if (inEdge) {
                    if (hasUpstreamBad(nd.id)) {
                        nd._tonPhase = null;
                        nd._tonPhaseEnd = null;
                        nd._timerResult = null;
                        nd._timerDone = true;
                        return true;
                    }
                    inputVal = getNodeOutputValue(inEdge.source, inEdge.sourcePort);
                    if (inputVal == null) return false;
                } else {
                    inputVal = 1;
                }
                const isInputOn = inputVal !== 0;
                const effDelay = getInputValue(nd.id, 'delay') ?? nd.timerDelay ?? 5;
                const delayMs = Math.max(effDelay * 1000, 500);
                const now = Date.now();

                if (!isInputOn) {
                    nd._tonPhase = null;
                    nd._tonPhaseEnd = null;
                    nd._timerResult = 0;
                    nd._timerDone = true;
                    return true;
                }
                if (!nd._tonPhase) {
                    nd._tonPhase = 'timing';
                    nd._tonPhaseEnd = now + delayMs;
                    clearTimeout(nd._tpTimeout);
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                }
                if (now >= nd._tonPhaseEnd) {
                    nd._tonPhase = 'on';
                    nd._timerResult = inputVal;
                } else {
                    nd._timerResult = 0;
                }
                nd._timerDone = true;
                return true;
            }
            // TPR 延遲導通 + 回饋重送：
            // delay 倒數中輸出 null（值變會 debounce 重置）；
            // 倒數結束輸出 passValue 並進入 confirmed（同時開始 settling 倒數）；
            // confirmed 內若輸入值變或下游回饋偏離 → 回 delay。
            // _tpPhaseEnd 雙語意：delay 階段=倒數結束時間；confirmed 階段=settling 結束時間。
            if (nd.operator === 'tpr') {
                const inEdge = S.canvasEdges.find(e => e.target === nd.id && e.targetPort === 'in');
                let passValue = 1;
                let currentInput = null;

                if (inEdge) {
                    if (hasUpstreamBad(nd.id)) {
                        clearTimeout(nd._tpTimeout);
                        nd._tpPhase = null; nd._tpPhaseEnd = null;
                        nd._tprPrevInput = null; nd._tprLastSentValue = null;
                        nd._timerResult = null; nd._timerDone = true;
                        return true;
                    }
                    const v = getNodeOutputValue(inEdge.source, inEdge.sourcePort);
                    if (v != null) { currentInput = v; passValue = v; }
                    else {
                        const srcNd = S.canvasNodes.find(n => n.id === inEdge.source);
                        if (srcNd && srcNd.type !== 'input' && srcNd.type !== 'constant' && !srcNd._evalDone) return false;
                    }
                } else {
                    currentInput = 1;
                }

                if (currentInput == null) {
                    clearTimeout(nd._tpTimeout);
                    nd._tpPhase = null; nd._tpPhaseEnd = null;
                    nd._tprPrevInput = null; nd._tprLastSentValue = null;
                    nd._timerResult = null; nd._timerDone = true;
                    return true;
                }

                const feedback = getTprFeedbackValue(nd.id);
                const effDelay = getInputValue(nd.id, 'delay') ?? nd.timerDelay ?? 5;
                const delayMs = Math.max(effDelay * 1000, 500);
                const now = Date.now();

                if (!nd._tpPhase) {
                    nd._tpPhase = 'delay';
                    nd._tpPhaseEnd = now + delayMs;
                    nd._tprPrevInput = currentInput;
                    clearTimeout(nd._tpTimeout);
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                    nd._timerResult = null;
                    nd._timerDone = true;
                    return true;
                }

                // confirmed：已輸出 passValue，視 settling 與回饋狀況決定是否回 delay
                if (nd._tpPhase === 'confirmed') {
                    // (a) 輸入值變化 → 回 delay 重啟倒數
                    if (nd._tprLastSentValue != null
                        && Math.abs(passValue - nd._tprLastSentValue) >= 0.001) {
                        nd._tpPhase = 'delay';
                        nd._tpPhaseEnd = now + delayMs;
                        nd._tprPrevInput = currentInput;
                        clearTimeout(nd._tpTimeout);
                        nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                        nd._timerResult = null;
                        nd._timerDone = true;
                        return true;
                    }

                    // (b) settling 中（now < _tpPhaseEnd）→ 不檢查回饋，持續輸出 passValue
                    if (nd._tpPhaseEnd != null && now < nd._tpPhaseEnd) {
                        nd._timerResult = passValue;
                        nd._timerDone = true;
                        return true;
                    }

                    // (c) settling 已過 → 檢查回饋
                    const isFeedbackMatch = feedback != null && Math.abs(feedback - passValue) < 0.001;
                    if (isFeedbackMatch) {
                        nd._timerResult = passValue;
                        nd._timerDone = true;
                        return true;
                    }
                    nd._tpPhase = 'delay';
                    nd._tpPhaseEnd = now + delayMs;
                    nd._tprPrevInput = currentInput;
                    clearTimeout(nd._tpTimeout);
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                    nd._timerResult = null;
                    nd._timerDone = true;
                    return true;
                }

                // delay：倒數中
                if (nd._tprPrevInput != null
                    && Math.abs(currentInput - nd._tprPrevInput) >= 0.001) {
                    nd._tpPhaseEnd = now + delayMs;
                    nd._tprPrevInput = currentInput;
                    clearTimeout(nd._tpTimeout);
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                    nd._timerResult = null;
                    nd._timerDone = true;
                    return true;
                }

                if (now >= nd._tpPhaseEnd) {
                    nd._tpPhase = 'confirmed';
                    nd._tprLastSentValue = passValue;
                    nd._tpPhaseEnd = now + delayMs;  // settling 結束時間
                    clearTimeout(nd._tpTimeout);
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                    nd._timerResult = passValue;
                    nd._timerDone = true;
                    return true;
                }

                nd._timerResult = null;
                nd._timerDone = true;
                return true;
            }
            // TP 脈衝：質變觸發 — 輸入值改變 → delay → hold(輸出) → 閒置等待下次質變
            const inEdge = S.canvasEdges.find(e => e.target === nd.id && e.targetPort === 'in');
            let passValue = 1;

            let currentInput = null;
            if (inEdge) {
                if (hasUpstreamBad(nd.id)) {
                    clearTimeout(nd._tpTimeout);
                    nd._tpPhase = null;
                    nd._tpPhaseEnd = null;
                    nd._tpHasHeld = false;
                    nd._tpPrevInput = undefined;
                    nd._timerResult = null;
                    nd._timerDone = true;
                    return true;
                }
                const v = getNodeOutputValue(inEdge.source, inEdge.sourcePort);
                if (v != null) {
                    currentInput = v;
                    passValue = v;
                } else {
                    // v == null → 區分「上游尚未評估」vs「上游已評估但輸出 null」
                    const srcNd = S.canvasNodes.find(n => n.id === inEdge.source);
                    if (srcNd && srcNd.type !== 'input' && srcNd.type !== 'constant' && !srcNd._evalDone) {
                        return false;
                    }
                }
            } else {
                currentInput = 1;
            }

            const effDelay = getInputValue(nd.id, 'delay') ?? nd.timerDelay ?? 5;
            const effHold  = getInputValue(nd.id, 'hold')  ?? nd.timerHold  ?? 2;
            const delayMs = Math.max(effDelay * 1000, 500);
            const holdMs  = Math.max(effHold  * 1000, 500);
            const now = Date.now();

            // ── 質變偵測：輸入值與上次不同時觸發新週期 ──
            // _tpPrevInput 三態：undefined=首次載入(不觸發), null=上游曾 null(會觸發), 數值=比較用
            if (currentInput != null) {
                if (nd._tpPrevInput === undefined) {
                    nd._tpPrevInput = currentInput;
                } else if (nd._tpPrevInput === null || nd._tpPrevInput !== currentInput) {
                    nd._tpPrevInput = currentInput;
                    clearTimeout(nd._tpTimeout);
                    nd._tpPhase = 'delay';
                    nd._tpPhaseEnd = now + delayMs;
                    nd._tpHasHeld = false;
                    nd._timerStartTime = now;
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), delayMs);
                }
            } else {
                nd._tpPrevInput = null;
                if (nd._tpPhase) {
                    clearTimeout(nd._tpTimeout);
                    nd._tpPhase = null;
                    nd._tpPhaseEnd = null;
                    nd._tpHasHeld = false;
                }
            }

            if (!nd._tpPhase) {
                nd._timerResult = null;
                nd._timerDone = true;
                return true;
            }

            if (now >= nd._tpPhaseEnd) {
                if (nd._tpPhase === 'delay') {
                    nd._tpPhase = 'hold';
                    nd._tpPhaseEnd = now + holdMs;
                    nd._tpHasHeld = true;
                    clearTimeout(nd._tpTimeout);
                    nd._tpTimeout = setTimeout(() => evaluateNodes(), holdMs);
                } else {
                    nd._tpPhase = null;
                    nd._tpPhaseEnd = null;
                    clearTimeout(nd._tpTimeout);
                    nd._timerResult = null;
                    nd._timerDone = true;
                    return true;
                }
            }

            nd._timerResult = nd._tpPhase === 'hold' ? passValue : null;
            nd._timerDone = true;
            return true;
        }
        // ── A/B 接點 — ctrl 埠模式（邏輯閘控制導通，優先於排程/點位）──
        if (nd.type === 'contact_no' || nd.type === 'contact_nc') {
            var ctrlEdge = S.canvasEdges.find(function(e) { return e.target === nd.id && e.targetPort === 'ctrl'; });
            if (ctrlEdge) {
                nd._contactResult = null;
                nd._contactOn = undefined;
                var ctrlVal = getNodeOutputValue(ctrlEdge.source, ctrlEdge.sourcePort);
                if (ctrlVal == null) return false;
                var isOnCtrl = nd.type === 'contact_no' ? (ctrlVal === 1) : (ctrlVal === 0);
                nd._contactOn = isOnCtrl;
                var inValCtrl = getInputValue(nd.id, 'in');
                var inEdgeCtrl = S.canvasEdges.find(function(e) { return e.target === nd.id && e.targetPort === 'in'; });
                if (inEdgeCtrl && inValCtrl == null) {
                    var srcCtrl = S.canvasNodes.find(function(n) { return n.id === inEdgeCtrl.source; });
                    if (srcCtrl && srcCtrl.type !== 'input' && srcCtrl.type !== 'constant' && !srcCtrl._evalDone) return false;
                    nd._contactResult = null;
                    return true;
                }
                nd._contactResult = isOnCtrl ? (inValCtrl != null ? inValCtrl : 1) : (inValCtrl != null ? null : 0);
                return true;
            }
        }
        // ── A/B 接點 — 排程模式 ──
        if ((nd.type === 'contact_no' || nd.type === 'contact_nc') && nd.scheduleId != null) {
            nd._contactResult = null;
            nd._contactOn = undefined;
            var isOn = evalScheduleNow(nd.scheduleId, nd.type);
            if (isOn == null) return false;
            nd._contactOn = isOn;
            var inVal = getInputValue(nd.id, 'in');
            var inEdgeSch = S.canvasEdges.find(function(e) { return e.target === nd.id && e.targetPort === 'in'; });
            if (inEdgeSch && inVal == null) {
                var srcSch = S.canvasNodes.find(function(n) { return n.id === inEdgeSch.source; });
                if (srcSch && srcSch.type !== 'input' && srcSch.type !== 'constant' && !srcSch._evalDone) return false;
                nd._contactResult = null;
                return true;
            }
            if (isOn) {
                nd._contactResult = inVal != null ? inVal : 1;
            } else {
                nd._contactResult = inVal != null ? null : 0;
            }
            return true;
        }
        // ── A接點（常開）/ B接點（常閉）— 點位模式 ──
        if ((nd.type === 'contact_no' || nd.type === 'contact_nc') && nd.sid) {
            nd._contactResult = null;
            nd._contactOn = undefined;
            if (hasUpstreamBad(nd.id)) return false;
            const lv = getEffectiveLv(nd);
            if (!lv || lv.value == null) return false;
            const pointVal = parseFloat(lv.value);
            if (isNaN(pointVal)) return false;
            const isOn = nd.type === 'contact_no' ? (pointVal === 1) : (pointVal === 0);
            nd._contactOn = isOn;
            const inVal = getInputValue(nd.id, 'in');
            var inEdgePt = S.canvasEdges.find(function(e) { return e.target === nd.id && e.targetPort === 'in'; });
            if (inEdgePt && inVal == null) {
                var srcPt = S.canvasNodes.find(function(n) { return n.id === inEdgePt.source; });
                if (srcPt && srcPt.type !== 'input' && srcPt.type !== 'constant' && !srcPt._evalDone) return false;
                nd._contactResult = null;
                return true;
            }
            if (isOn) {
                nd._contactResult = inVal != null ? inVal : 1;
            } else {
                nd._contactResult = inVal != null ? null : 0;
            }
            return true;
        }

        // ── 計數器節點（CTU）──
        if (nd.type === 'counter') {
            let preset = nd.presetValue != null ? nd.presetValue : 10;
            const presetEdge = S.canvasEdges.find(e => e.target === nd.id && e.targetPort === 'preset');
            if (presetEdge) {
                const pv = getNodeOutputValue(presetEdge.source, presetEdge.sourcePort);
                if (pv != null) preset = Math.max(1, Math.floor(pv));
                else {
                    const srcP = S.canvasNodes.find(n => n.id === presetEdge.source);
                    if (srcP && srcP.type !== 'input' && srcP.type !== 'constant' && !srcP._evalDone) return false;
                }
            }
            if (preset < 1) preset = 1;

            const cuEdge = S.canvasEdges.find(e => e.target === nd.id && e.targetPort === 'cu');
            let cuVal = null;
            let isCuBad = false;
            if (cuEdge) {
                const srcCu = S.canvasNodes.find(n => n.id === cuEdge.source);
                if (srcCu && hasUpstreamBad(cuEdge.source)) {
                    isCuBad = true;
                } else {
                    const v = getNodeOutputValue(cuEdge.source, cuEdge.sourcePort);
                    if (v != null) cuVal = v;
                    else if (srcCu && srcCu.type !== 'input' && srcCu.type !== 'constant' && !srcCu._evalDone) return false;
                }
            }

            // reset：自回授特例（直接讀 nd._counterQ 上一 tick 的 q），其餘走標準等待
            const resetEdge = S.canvasEdges.find(e => e.target === nd.id && e.targetPort === 'reset');
            let resetVal = null;
            if (resetEdge) {
                if (resetEdge.source === nd.id) {
                    resetVal = nd._counterQ != null ? nd._counterQ : 0;
                } else {
                    const srcR = S.canvasNodes.find(n => n.id === resetEdge.source);
                    if (srcR && hasUpstreamBad(resetEdge.source)) {
                        resetVal = null;
                    } else {
                        const v = getNodeOutputValue(resetEdge.source, resetEdge.sourcePort);
                        if (v != null) resetVal = v;
                        else if (srcR && srcR.type !== 'input' && srcR.type !== 'constant' && !srcR._evalDone) return false;
                    }
                }
            }

            if (nd._counterValue == null) nd._counterValue = 0;
            if (nd._counterLastEdgeAt == null) nd._counterLastEdgeAt = 0;

            const isReset = resetVal != null && resetVal !== 0;
            if (isReset) {
                nd._counterValue = 0;
                if (cuVal != null) nd._counterPrevCu = cuVal;
            } else if (cuVal != null && !isCuBad) {
                if (nd._counterPrevCu == null) {
                    nd._counterPrevCu = cuVal;
                } else {
                    const isEdge = nd._counterPrevCu === 0 && cuVal !== 0;
                    if (isEdge) {
                        const minMs = Math.max(0, nd.cuMinIntervalMs != null ? nd.cuMinIntervalMs : 60000);
                        const now = Date.now();
                        const isFirstEdge = nd._counterLastEdgeAt === 0;
                        if (isFirstEdge || (now - nd._counterLastEdgeAt) >= minMs) {
                            if (nd._counterValue < preset) nd._counterValue++;
                            nd._counterLastEdgeAt = now;
                        }
                    }
                    nd._counterPrevCu = cuVal;
                }
            } else if (cuVal == null && !isCuBad && cuEdge) {
                nd._counterPrevCu = 0;
            }

            nd._counterQ = nd._counterValue >= preset ? 1 : 0;
            return true;
        }

        // ── 演算法節點：呼叫後端 API 取得實際計算結果 ──
        if (nd.type === 'algorithm' && nd.operator && S.ALGO_OPS[nd.operator]) {
            nd._algoResult = null;
            nd._algoReady = false;
            nd._algoUnreadyGroups = null;
            if (hasUpstreamBad(nd.id)) return false;
            var algo = S.ALGO_OPS[nd.operator];
            var ports = S.getAlgoPorts(algo, nd.inputCount);
            var algoInputs = ports.inputs;
            var inputValues = {};

            if (algo.variadic) {
                // variadic：fixed 全要齊；repeat 逐組檢查，至少一組備齊就送 API。
                // 未備齊的組記錄在 _algoUnreadyGroups，render 時其對應輸出埠強制反灰。
                var fixedKeys = (algo.inputsFixed || []).map(function(p) { return p.key; });
                var repeatKeys = (algo.inputsRepeat || []).map(function(p) { return p.key; });
                var N = nd.inputCount || 1;
                for (var fi = 0; fi < fixedKeys.length; fi++) {
                    var fv = getInputValue(nd.id, fixedKeys[fi]);
                    if (fv == null) return false;
                    inputValues[fixedKeys[fi]] = fv;
                }
                var unreadyGroups = new Set();
                var anyGroupReady = false;
                for (var g = 1; g <= N; g++) {
                    var groupOK = true;
                    var groupVals = {};
                    for (var ri = 0; ri < repeatKeys.length; ri++) {
                        var rkey = repeatKeys[ri] + g;
                        var rv = getInputValue(nd.id, rkey);
                        if (rv == null) { groupOK = false; break; }
                        groupVals[rkey] = rv;
                    }
                    if (groupOK) {
                        // auto 注入輸入（@inputs_auto_repeat）：模擬端以下游 output 節點的
                        // fMin/fMax（點位 Min/Max 快照）帶入；找不到下游 output → 不注入，
                        // Python 框架回該組 INPUT_MISSING（與 Engine 行為一致）
                        (algo.inputsAutoRepeat || []).forEach(function (def) {
                            var outNd = findDownstreamOutputNodeFromPort(nd.id, def.port + g);
                            if (!outNd) return;
                            var v = def.field === 'Min' ? (outNd.fMin != null ? outNd.fMin : 0)
                                  : def.field === 'Max' ? (outNd.fMax != null ? outNd.fMax : 100)
                                  : null;
                            if (v != null) groupVals[def.key + g] = v;
                        });
                        Object.assign(inputValues, groupVals);
                        anyGroupReady = true;
                    } else {
                        unreadyGroups.add(g);
                    }
                }
                if (!anyGroupReady) return false;
                nd._algoUnreadyGroups = unreadyGroups;
            } else {
                for (var i = 0; i < algoInputs.length; i++) {
                    var portKey = algoInputs[i].key;
                    var v = getInputValue(nd.id, portKey);
                    if (v == null) return false;
                    inputValues[portKey] = v;
                }
            }
            nd._algoReady = true;

            var nForCache = algo.variadic ? (nd.inputCount || 1) : '_';
            var sortedInputKeys = Object.keys(inputValues).sort();
            var cacheKey = nd.operator + '|N' + nForCache + '|' + sortedInputKeys.map(function(k) { return k + '=' + inputValues[k]; }).join('|');
            if (nd._algoCacheKey === cacheKey && nd._algoCachedResult != null) {
                nd._algoResult = nd._algoCachedResult;
                return true;
            }

            // 非同步呼叫後端 API（不阻塞，結果回來後下一個 render 週期顯示）
            if (!nd._algoFetching) {
                nd._algoFetching = true;
                var payload = { inputs: inputValues };
                if (algo.variadic) payload.n = nd.inputCount || 1;
                fetch('/LogicFlow/api/algo-eval/' + encodeURIComponent(nd.operator), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload)
                })
                .then(function(r) { return r.ok ? r.json() : Promise.reject(r.status); })
                .then(function(data) {
                    var res = data.result || data;
                    var resDict = (typeof res === 'number') ? { out: res } : res;
                    nd._algoCachedResult = resDict;
                    nd._algoCacheKey = cacheKey;
                    nd._algoResult = resDict;
                    // 演算法狀態：merged（codeId/codeName/severity）+ perOutput（每個輸出 port 各自 status）
                    // 舊 response 缺 perOutput → fallback：所有 result key 套 merged，避免畫面崩
                    var mergedCodeId = (data && data.statusCodeId != null) ? data.statusCodeId : 0;
                    var mergedCodeName = (data && data.statusCodeName) ? data.statusCodeName : 'OK';
                    var mergedSeverity = (data && data.severity) ? data.severity : 'Info';
                    var perOutput = (data && data.perOutput && typeof data.perOutput === 'object') ? data.perOutput : null;
                    if (!perOutput) {
                        perOutput = {};
                        Object.keys(resDict).forEach(function(k) {
                            perOutput[k] = { statusCodeId: mergedCodeId, statusCodeName: mergedCodeName, severity: mergedSeverity };
                        });
                    }
                    nd._algoStatus = {
                        codeId: mergedCodeId,
                        codeName: mergedCodeName,
                        severity: mergedSeverity,
                        perOutput: perOutput
                    };
                    nd._algoFetching = false;
                    // 節點本身只在「所有 perOutput 都 Error」時才整顆反灰
                    var algoEl = document.querySelector('.flow-node[data-node-id="' + nd.id + '"]');
                    if (algoEl) {
                        algoEl.classList.remove('algo-status-bad', 'algo-status-warning', 'algo-status-error');
                        var poKeys = Object.keys(perOutput);
                        var allError = poKeys.length > 0 && poKeys.every(function(k) {
                            return perOutput[k] && perOutput[k].severity === 'Error';
                        });
                        if (allError) {
                            algoEl.classList.add('algo-status-bad', 'algo-status-error');
                            var algoLabel = (S.ALGO_OPS[nd.operator] && S.ALGO_OPS[nd.operator].label) || nd.operator;
                            algoEl.title = algoLabel + ' : ' + mergedCodeName + ' (' + mergedSeverity + ')';
                        } else {
                            algoEl.removeAttribute('title');
                        }
                    }
                })
                .catch(function(err) {
                    console.error('algo-eval failed:', nd.operator, err);
                    nd._algoFetching = false;
                });
            }
            return true;
        }

        return false;
    }

    // ── 全部節點求值（多輪迭代，讓任意串接順序都能正確傳播） ──
    function evaluateNodes() {
        markBadInputs();

        const EVAL_TYPES = ['math', 'compare', 'and', 'or', 'not', 'xor', 'timer', 'contact_no', 'contact_nc', 'algorithm', 'counter'];
        const evalNodes = S.canvasNodes.filter(n => EVAL_TYPES.includes(n.type));

        // 清除舊結果（_timerStartTime、_tpPhase、_counterValue 等狀態機欄位不清除）
        for (const nd of evalNodes) {
            nd._mathResult = null;
            nd._compareResult = null;
            nd._gateResult = null;
            nd._timerResult = null;
            nd._timerDone = false;
            nd._contactResult = null;
            nd._algoResult = null;
            nd._algoReady = false;
            nd._algoUnreadyGroups = null;
            // counter._counterQ 跨 tick 保留以支援 q→reset 自回授；不清掉。
            nd._evalDone = false;
        }

        // 多輪迭代：每輪嘗試求值所有未完成節點，直到沒有新結果產生
        const maxRounds = evalNodes.length + 1;
        for (let round = 0; round < maxRounds; round++) {
            let changed = false;
            for (const nd of evalNodes) {
                if (nd._evalDone) continue;
                if (evalOneNode(nd)) {
                    nd._evalDone = true;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        // 輸出節點狀態更新（display-only，控制命令由 Engine 後端執行）
        for (const nd of S.canvasNodes) {
            if (nd.type !== 'output' || !nd.sid) continue;
            const val = getInputValue(nd.id, 'in');
            const isGreen = !hasUpstreamBad(nd.id) && val != null;
            const prev = S._outputPrevState[nd.id] || { green: false, value: null };
            const ctrl = S._controlModeCache[nd.sid];
            const isManual = ctrl && !ctrl.isAuto;
            const fMin = nd.fMin ?? 0;
            const fMax = nd.fMax ?? 100;
            nd._isOutOfRange = isGreen && (val < fMin || val > fMax);
            // 前端僅記錄狀態供顯示，不送出控制命令（Engine BackgroundService 負責）
            if (S._isLogicEnabled && isGreen && (!prev.green || val !== prev.value) && !isManual && !nd._isOutOfRange) {
                console.log('[LogicFlow] Engine running, skip front-end write:', nd.sid, '=', val);
            }
            S._outputPrevState[nd.id] = { green: isGreen, value: isGreen ? val : null };
        }

        // 更新數學運算節點的注入值顯示
        const cvs = document.getElementById('diagramCanvas');
        if (cvs) {
            for (const nd of S.canvasNodes) {
                if (nd.type !== 'math' || !nd.operator || !S.MATH_OPS[nd.operator] || !S.MATH_OPS[nd.operator].hasValue) continue;
                const dispEl = cvs.querySelector(`.math-value-display[data-node-id="${nd.id}"]`);
                if (!dispEl) continue;
                const valFromPort = getInputValue(nd.id, 'val');
                dispEl.textContent = valFromPort != null ? S.fmtNum(valFromPort) : '--';
            }

            // 更新比較節點的注入值顯示
            for (const nd of S.canvasNodes) {
                if (nd.type !== 'compare') continue;
                for (const field of ['a', 'b']) {
                    const dispEl = cvs.querySelector(`.compare-value-display[data-node-id="${nd.id}"][data-field="${field}"]`);
                    if (!dispEl) continue;
                    const injVal = getInputValue(nd.id, field);
                    dispEl.textContent = injVal != null ? S.fmtNum(injVal) : '--';
                }
            }

            // 更新計數器節點的 cv/q 即時顯示
            for (const nd of S.canvasNodes) {
                if (nd.type !== 'counter') continue;
                const liveEl = cvs.querySelector(`.counter-live[data-node-id="${nd.id}"]`);
                if (!liveEl) continue;
                const cvText = nd._counterValue != null ? String(nd._counterValue) : '0';
                const qText = nd._counterQ === 1 ? '1' : '0';
                liveEl.textContent = `cv:${cvText} / q:${qText}`;
            }

            // 更新計時器節點的注入值顯示
            for (const nd of S.canvasNodes) {
                if (nd.type !== 'timer') continue;
                const timerFields = (nd.operator === 'ton' || nd.operator === 'tpr') ? ['delay'] : ['delay', 'hold'];
                for (const field of timerFields) {
                    const dispEl = cvs.querySelector(`.timer-value-display[data-node-id="${nd.id}"][data-field="${field}"]`);
                    if (!dispEl) continue;
                    const injVal = getInputValue(nd.id, field);
                    const defVal = field === 'delay' ? (nd.timerDelay ?? 5) : (nd.timerHold ?? 2);
                    dispEl.textContent = S.fmtNum(injVal != null ? injVal : defVal);
                }
            }
        }

        S.renderEdges();
    }

    // ── 送出輸出控制命令 ──
    function sendOutputControl(szCid, dValue) {
        fetch('/api/control/write', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ cid: szCid, value: dValue, mode: 'logicflow' })
        }).then(res => {
            if (!res.ok) console.warn('[LogicFlow] control write failed:', szCid, res.statusText);
            else console.log('[LogicFlow] control write:', szCid, '=', dValue);
        }).catch(err => console.warn('[LogicFlow] control write error:', szCid, err));
    }

    // 暴露給其他模組
    S.startRealtimePolling = startRealtimePolling;
    S.stopRealtimePolling = stopRealtimePolling;
    S.startTimerEval = startTimerEval;
    S.stopTimerEval = stopTimerEval;
    S.fetchRealtimeValues = fetchRealtimeValues;
    S.markBadInputs = markBadInputs;
    S.hasUpstreamBad = hasUpstreamBad;
    S.getEffectiveLv = getEffectiveLv;
    S.getNodeOutputValue = getNodeOutputValue;
    S.getInputValue = getInputValue;
    S.evalScheduleNow = evalScheduleNow;
    S.evalOneNode = evalOneNode;
    S.evaluateNodes = evaluateNodes;
    S.sendOutputControl = sendOutputControl;
    S.syncTimerStateFromEngine = syncTimerStateFromEngine;
})();
