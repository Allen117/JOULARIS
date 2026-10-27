/*
 * 全站警報音 + 靜音（_Layout 頂端列鈴鐺）
 *
 * 觸發：/Realtime/ActiveAlarms 中「未確認 && severity <= 2（Critical/High/Medium）」的警報
 *   Critical → 急促三連嗶循環；High → 慢速雙嗶循環；Medium → 每筆新警報響一次叮咚；Low 不響
 * 靜音：
 *   消音     — 記下當下未確認警報 key（SID:type）並進入「已消音」狀態（🔕，即使當下沒有警報也切），
 *              出現不在名單內的新警報會再響並自動解除已消音；取消靜音亦解除
 *   暫時靜音 — N 分鐘內一律不響，到期自動恢復
 *   ACK      — 該筆 isAcknowledged=true 後自然不再列入
 * 狀態存 localStorage（per 工作站），storage 事件同步到同機其他分頁；多分頁以心跳選一個 leader 發聲。
 * 聲音以 Web Audio API 即時合成，不需音檔。
 */
(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var POLL_INTERVAL_MS = 5000;
    var TICK_MS = 1000;
    var API_URL = '/Realtime/ActiveAlarms';
    var STATE_KEY = 'scada.alarmSound.state.v1';
    var LEADER_KEY = 'scada.alarmSound.leader.v1';
    var LEADER_TIMEOUT_MS = 6000;
    var MAX_AUDIBLE_SEVERITY = 2; // 0=Critical 1=High 2=Medium

    var szTabId = Date.now().toString(36) + Math.random().toString(36).slice(2, 8);

    // ── 持久狀態（localStorage）──
    var state = { silencedKeys: [], snoozeUntil: 0, isSilenced: false };

    // ── 執行期狀態 ──
    var pendingAlarms = [];          // 未確認且 severity <= 2
    var playedMediumKeys = {};       // Medium 已響過的 key
    var isLeader = true;
    var isOtherLeaderUnlocked = false; // 他分頁是 leader 且能發聲 → 本分頁不需提示解鎖
    var currentLoop = null;          // 'critical' | 'high' | null
    var loopNextAt = 0;              // AudioContext 時間軸上下一輪起點
    var loopBus = null;              // 循環音所接的 GainNode，停止時直接斷開
    var audioCtx = null;
    var isFetchOk = true;

    // ── DOM ──
    var $nav, $icon, $count, $snoozeLabel, $status, $unlock, $resume, $silence;

    // ════════════════ localStorage ════════════════

    function loadState() {
        try {
            var sz = localStorage.getItem(STATE_KEY);
            if (sz) {
                var o = JSON.parse(sz);
                state.silencedKeys = Array.isArray(o.silencedKeys) ? o.silencedKeys : [];
                state.snoozeUntil = Number(o.snoozeUntil) || 0;
                state.isSilenced = !!o.isSilenced;
            }
        } catch (e) { /* 存取失敗退化為記憶體狀態 */ }
    }

    function saveState() {
        try { localStorage.setItem(STATE_KEY, JSON.stringify(state)); } catch (e) { }
    }

    // ════════════════ 多分頁 leader ════════════════

    /** 搶 leader 規則：無 leader / 心跳逾時 / 自己已解鎖音訊而現任未解鎖（確保發聲分頁真的能響） */
    function heartbeat() {
        try {
            var now = Date.now();
            var isUnlocked = !isAudioLocked();
            var cur = JSON.parse(localStorage.getItem(LEADER_KEY) || 'null');
            var isStale = !cur || now - (Number(cur.ts) || 0) > LEADER_TIMEOUT_MS;
            if (isStale || cur.id === szTabId || (isUnlocked && !cur.unlocked)) {
                localStorage.setItem(LEADER_KEY, JSON.stringify({ id: szTabId, ts: now, unlocked: isUnlocked }));
                cur = JSON.parse(localStorage.getItem(LEADER_KEY) || 'null');
            }
            isLeader = !!cur && cur.id === szTabId;
            isOtherLeaderUnlocked = !isLeader && !!cur && !!cur.unlocked;
        } catch (e) {
            isLeader = true;
            isOtherLeaderUnlocked = false;
        }
    }

    function releaseLeader() {
        try {
            var cur = JSON.parse(localStorage.getItem(LEADER_KEY) || 'null');
            if (cur && cur.id === szTabId) localStorage.removeItem(LEADER_KEY);
        } catch (e) { }
    }

    // ════════════════ 音訊 ════════════════

    function getCtx() {
        if (!audioCtx) {
            var Ctor = window.AudioContext || window.webkitAudioContext;
            if (!Ctor) return null;
            try { audioCtx = new Ctor(); } catch (e) { return null; }
        }
        return audioCtx;
    }

    function isAudioLocked() {
        var ctx = getCtx();
        return !ctx || ctx.state !== 'running';
    }

    function unlockAudio() {
        var ctx = getCtx();
        if (ctx && ctx.state === 'suspended') {
            ctx.resume().then(function () { heartbeat(); evaluate(); }, function () { });
        }
    }

    /** 在 ctx 時間軸 at 排一個帶包絡的音，避免爆音 */
    function tone(dest, at, freq, dur, type, vol) {
        var ctx = audioCtx;
        var osc = ctx.createOscillator();
        var g = ctx.createGain();
        osc.type = type;
        osc.frequency.setValueAtTime(freq, at);
        g.gain.setValueAtTime(0.0001, at);
        g.gain.exponentialRampToValueAtTime(vol, at + 0.01);
        g.gain.setValueAtTime(vol, at + dur - 0.03);
        g.gain.exponentialRampToValueAtTime(0.0001, at + dur);
        osc.connect(g);
        g.connect(dest);
        osc.start(at);
        osc.stop(at + dur + 0.02);
    }

    // 各 pattern：排一輪並回傳該輪長度（秒）
    var PATTERNS = {
        // Critical：高低交替三連嗶，1.2 秒一輪
        critical: function (dest, at) {
            tone(dest, at, 1050, 0.14, 'square', 0.18);
            tone(dest, at + 0.22, 800, 0.14, 'square', 0.18);
            tone(dest, at + 0.44, 1050, 0.14, 'square', 0.18);
            return 1.2;
        },
        // High：中音雙嗶，2.5 秒一輪
        high: function (dest, at) {
            tone(dest, at, 700, 0.3, 'triangle', 0.3);
            tone(dest, at + 0.45, 700, 0.3, 'triangle', 0.3);
            return 2.5;
        },
        // Medium：叮咚一次
        medium: function (dest, at) {
            tone(dest, at, 880, 0.35, 'sine', 0.35);
            tone(dest, at + 0.38, 660, 0.5, 'sine', 0.35);
            return 0.9;
        }
    };

    function startLoop(szName) {
        if (currentLoop === szName) return;
        stopLoop();
        var ctx = getCtx();
        if (!ctx || ctx.state !== 'running') return;
        loopBus = ctx.createGain();
        loopBus.connect(ctx.destination);
        currentLoop = szName;
        loopNextAt = ctx.currentTime + 0.05;
        scheduleLoop();
    }

    /** 預排未來 2 秒內的循環音（背景分頁 timer 被節流時仍連續） */
    function scheduleLoop() {
        if (!currentLoop || !audioCtx) return;
        var horizon = audioCtx.currentTime + 2;
        if (loopNextAt < audioCtx.currentTime) loopNextAt = audioCtx.currentTime + 0.05;
        while (loopNextAt < horizon) {
            loopNextAt += PATTERNS[currentLoop](loopBus, loopNextAt);
        }
    }

    function stopLoop() {
        if (loopBus) {
            try { loopBus.disconnect(); } catch (e) { }
        }
        loopBus = null;
        currentLoop = null;
    }

    function playOnce(szName) {
        var ctx = getCtx();
        if (!ctx || ctx.state !== 'running') return false;
        PATTERNS[szName](ctx.destination, ctx.currentTime + 0.05);
        return true;
    }

    // ════════════════ 判斷邏輯 ════════════════

    function keyOf(a) { return a.sid + ':' + a.type; }

    function isSnoozed() { return state.snoozeUntil > Date.now(); }

    /** 未被消音的待處理警報 */
    function audibleAlarms() {
        if (isSnoozed()) return [];
        return pendingAlarms.filter(function (a) { return state.silencedKeys.indexOf(keyOf(a)) < 0; });
    }

    function evaluate() {
        // 到期的暫時靜音自動清掉
        if (state.snoozeUntil && !isSnoozed()) {
            state.snoozeUntil = 0;
            saveState();
        }

        var audible = audibleAlarms();

        // 新警報突破消音名單（要響）→ 已消音狀態自動解除，鈴鐺回到響鈴
        if (state.isSilenced && audible.length > 0) {
            state.isSilenced = false;
            saveState();
        }

        var nWorst = audible.reduce(function (m, a) { return Math.min(m, a.severity); }, 99);

        if (!isLeader || isAudioLocked()) {
            stopLoop();
        } else if (nWorst === 0) {
            startLoop('critical');
        } else if (nWorst === 1) {
            startLoop('high');
        } else {
            stopLoop();
            var aNewMedium = audible.filter(function (a) { return a.severity === 2 && !playedMediumKeys[keyOf(a)]; });
            if (aNewMedium.length && playOnce('medium')) {
                aNewMedium.forEach(function (a) { playedMediumKeys[keyOf(a)] = true; });
            }
        }

        render(audible);
    }

    // ════════════════ 輪詢 ════════════════

    function fetchAlarms() {
        fetch(API_URL, { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (json) {
                if (!json || !json.success) { isFetchOk = false; evaluate(); return; }
                isFetchOk = true;
                pendingAlarms = (json.data || []).filter(function (a) {
                    return !a.isAcknowledged && a.severity <= MAX_AUDIBLE_SEVERITY;
                });

                // 已確認或已恢復者移出消音名單 / Medium 已響名單（同點位再觸發時要再響）
                var oLive = {};
                pendingAlarms.forEach(function (a) { oLive[keyOf(a)] = true; });
                var aKept = state.silencedKeys.filter(function (k) { return oLive[k]; });
                if (aKept.length !== state.silencedKeys.length) {
                    state.silencedKeys = aKept;
                    saveState();
                }
                Object.keys(playedMediumKeys).forEach(function (k) { if (!oLive[k]) delete playedMediumKeys[k]; });

                evaluate();
            })
            .catch(function () { isFetchOk = false; evaluate(); });
    }

    // ════════════════ 操作 ════════════════

    function silence() {
        unlockAudio();
        state.silencedKeys = pendingAlarms.map(keyOf);
        state.isSilenced = true;
        saveState();
        evaluate();
    }

    function snooze(nMinutes) {
        unlockAudio();
        state.snoozeUntil = Date.now() + nMinutes * 60000;
        saveState();
        evaluate();
    }

    function resume() {
        unlockAudio();
        state.snoozeUntil = 0;
        state.silencedKeys = [];
        state.isSilenced = false;
        saveState();
        evaluate();
    }

    function test() {
        var ctx = getCtx();
        if (!ctx) return;
        var go = function () { PATTERNS.critical(ctx.destination, ctx.currentTime + 0.05); };
        if (ctx.state === 'running') go();
        else ctx.resume().then(function () { go(); heartbeat(); evaluate(); }, function () { });
    }

    // ════════════════ 畫面 ════════════════

    function render(audible) {
        if (!$nav) return;
        var nPending = pendingAlarms.length;
        var isSnooze = isSnoozed();
        var isSounding = audible.length > 0;
        // 已消音：按過「消音」且尚未被新警報突破 / 取消（有未確認警報時紅色靜止；警報都確認後仍維持，直到新警報或取消）
        var isSilenced = !isSnooze && state.isSilenced && !isSounding;

        $nav.classList.toggle('as-alerting', isSounding);
        $nav.classList.toggle('as-has-pending', nPending > 0);
        $nav.classList.toggle('as-muted', isSnooze);

        // 頂端圖示與選單項目同圖：暫時靜音 = bell-slash、消音 = volume-mute、其餘 = bell
        $icon.className = isSnooze ? 'fas fa-bell-slash' : (isSilenced ? 'fas fa-volume-mute' : 'fas fa-bell');
        $count.textContent = nPending;
        $count.classList.toggle('d-none', nPending === 0);

        // 暫時靜音：只掛剩餘分鐘數字徽章，不帶文字
        $snoozeLabel.textContent = isSnooze ? Math.ceil((state.snoozeUntil - Date.now()) / 60000) : '';
        $snoozeLabel.classList.toggle('d-none', !isSnooze);

        // 狀態文字
        var szStatus;
        if (!isFetchOk) szStatus = t('layout.alarm_sound.status.offline');
        else if (isSnooze) szStatus = t('layout.alarm_sound.status.snoozed', { min: Math.ceil((state.snoozeUntil - Date.now()) / 60000) });
        else if (isSounding) szStatus = t('layout.alarm_sound.status.sounding', { count: audible.length });
        else if (nPending > 0) szStatus = t('layout.alarm_sound.status.silenced', { count: nPending });
        else if (isSilenced) szStatus = t('layout.alarm_sound.status.silenced_idle');
        else szStatus = t('layout.alarm_sound.status.normal');
        $status.textContent = szStatus;

        // 消音只針對「正在響」的警報；沒在響時（無警報 / 已消音 / 暫時靜音）點了沒作用 → 停用
        $silence.classList.toggle('disabled', !isSounding);
        $silence.setAttribute('aria-disabled', String(!isSounding));

        $resume.classList.toggle('d-none', !isSnooze && !state.isSilenced && state.silencedKeys.length === 0);

        // 有聲要發卻被瀏覽器擋 → 提示點擊啟用
        $unlock.classList.toggle('d-none', !(isSounding && isAudioLocked() && !isOtherLeaderUnlocked));
    }

    function onMenuClick(e) {
        var el = e.target.closest('[data-as-action]');
        if (!el) return;
        e.preventDefault();
        var szAction = el.getAttribute('data-as-action');
        if (szAction === 'silence') silence();
        else if (szAction === 'snooze') snooze(parseInt(el.getAttribute('data-min'), 10) || 5);
        else if (szAction === 'resume') resume();
        else if (szAction === 'test') test();
        else if (szAction === 'unlock') unlockAudio();
    }

    function init() {
        $nav = document.getElementById('alarmSoundNav');
        if (!$nav) return;
        $icon = document.getElementById('alarmSoundIcon');
        $count = document.getElementById('alarmSoundCount');
        $snoozeLabel = document.getElementById('alarmSoundSnooze');
        $status = document.getElementById('alarmSoundStatus');
        $unlock = document.getElementById('alarmSoundUnlock');
        $resume = document.getElementById('alarmSoundResume');
        $silence = document.getElementById('alarmSoundSilence');

        loadState();
        heartbeat();

        document.addEventListener('click', onMenuClick);
        // 任一使用者互動即解鎖音訊（瀏覽器自動播放政策）
        ['pointerdown', 'keydown'].forEach(function (ev) {
            document.addEventListener(ev, unlockAudio, true);
        });

        // 其他分頁改了靜音狀態 → 同步
        window.addEventListener('storage', function (e) {
            if (e.key === STATE_KEY) { loadState(); evaluate(); }
        });
        window.addEventListener('pagehide', function () { stopLoop(); releaseLeader(); });

        var nTick = 0;
        setInterval(function () {
            nTick++;
            if (nTick % 2 === 0) {
                var wasLeader = isLeader;
                heartbeat();
                if (wasLeader !== isLeader) evaluate(); // leader 交接：立即停/接手發聲
            }
            scheduleLoop();
            if (nTick % (POLL_INTERVAL_MS / TICK_MS) === 0) fetchAlarms();
            else if (state.snoozeUntil) evaluate(); // 暫時靜音倒數 / 到期
        }, TICK_MS);

        var start = function () { fetchAlarms(); };
        if (window.i18n && window.i18n.ready) window.i18n.ready(start); else start();
    }

    window._alarmSound = { silence: silence, snooze: snooze, resume: resume, test: test };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
