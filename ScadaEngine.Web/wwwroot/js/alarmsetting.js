(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var _coordinators = window._alarmInitData.coordinators || [];
    var _points = window._alarmInitData.points || [];
    var _rules = window._alarmInitData.rules || [];
    var _lineTargets = window._alarmInitData.lineTargets || [];
    var _diLabels = window._alarmInitData.diLabels || {};
    var _emailGroups = window._alarmInitData.emailGroups || [];
    var _emailSenderConfig = window._alarmInitData.emailSenderConfig || {};
    var _smsTargets = window._alarmInitData.smsTargets || [];
    var _smsSenderConfig = window._alarmInitData.smsSenderConfig || {};
    var _alarmRulesForRouting = window._alarmInitData.alarmRulesForRouting || [];
    var _calcGroups = window._alarmInitData.calcGroups || {};   // SID → 群組名（計算點兩層瀏覽）
    var _circuits = window._alarmInitData.circuits || [];       // 迴路清單（迴路用電 NRGD-/NRGM-/NRGP-）

    // 由 Designer 設定取得 SID 對應的 DI ON/OFF 標籤；找不到則回傳預設 ON/OFF
    function getDiLabelsForSid(sid) {
        var entry = sid ? _diLabels[sid] : null;
        return {
            onLabel: (entry && entry.onLabel) ? entry.onLabel : 'ON',
            offLabel: (entry && entry.offLabel) ? entry.offLabel : 'OFF'
        };
    }

    // 將指定 SID 的 Designer DI 標籤套到表單唯讀欄位，並告知共用表單本點 SID（前置點位排除自己）
    function applyDiLabelsForSid(sid) {
        var labels = getDiLabelsForSid(sid);
        window.AlarmRuleForm.setDiLabels(labels.onLabel, labels.offLabel);
        window.AlarmRuleForm.setSelfSid(sid);
    }

    function severityLabel(n) {
        var keys = ['critical', 'high', 'medium', 'low'];
        return t('alarm.severity.' + (keys[n] || 'high'));
    }

    function maxSeverityPillLabel(n) {
        return t('alarm.max_severity_pill.' + n);
    }

    var _modal = null;
    var _lineModal = null;
    var _emailGroupModal = null;
    var _emailRecipientModal = null;
    var _emailConfigModal = null;
    var _emailRulesModal = null;
    var _smsModal = null;
    var _smsConfigModal = null;
    var _smsStatusTimer = null;
    var _currentEmailGroupRecipients = [];  // 編輯群組 modal 時的暫存收件人清單

    var CALC_DEVICE_ID = -999;
    var DMD_DEVICE_ID = -998;   // 需量虛擬點位（DMD-{kWhSID}）
    var NRG_DEVICE_ID = -997;   // 迴路用電虛擬點位（NRGD-/NRGM-/NRGP-{circuitId}）
    var CALC_UNGROUPED = '__ungrouped__';   // 計算點「未分組」桶的下拉 value（避免與真實群組名撞名）

    // isCalcSid / isDmdSid / isNrgSid 委派共用層 window.PointGrouping（分群解析單一真相）
    function isCalcSid(sid) { return window.PointGrouping.isCalcSid(sid); }
    function isDmdSid(sid) { return window.PointGrouping.isDmdSid(sid); }
    function isNrgSid(sid) { return window.PointGrouping.isNrgSid(sid); }

    // ── 計算點群組（兩層瀏覽）──

    function calcGroupOfSid(sid) {
        var g = _calcGroups[sid];
        return g ? String(g).trim() : '';
    }

    // 盤點計算點群組：names = 有名群組（排序）、hasUngrouped = 是否有未分組點位
    function getCalcGroupInfo() {
        var names = {};
        var hasUngrouped = false;
        _points.forEach(function (p) {
            if (!isCalcSid(p.sid)) return;
            var g = calcGroupOfSid(p.sid);
            if (g) names[g] = true;
            else hasUngrouped = true;
        });
        return { names: Object.keys(names).sort(), hasUngrouped: hasUngrouped };
    }

    function filterCalcPointsByGroup(groupValue) {
        return _points.filter(function (p) {
            if (!isCalcSid(p.sid)) return false;
            var g = calcGroupOfSid(p.sid);
            return groupValue === CALC_UNGROUPED ? !g : g === groupValue;
        });
    }

    // 第二層下拉共用三種語意（子設備 / 計算點群組 / 迴路），依當前設備切 label
    function setSubDeviceLabel(mode) {
        var label = document.getElementById('subDeviceLabel');
        if (!label) return;
        if (mode === 'calc') label.textContent = t('alarm.field.calc_group');
        else if (mode === 'nrg') label.textContent = t('alarm.field.circuit');
        else label.textContent = t('alarm.field.sub_device');
    }

    // 填第二層下拉（value/text 成對），並套 placeholder
    function fillSubDropdown(placeholderKey, items) {
        var selSub = document.getElementById('selSubDevice');
        selSub.innerHTML = '<option value="">' + t(placeholderKey) + '</option>';
        items.forEach(function (it) {
            var opt = document.createElement('option');
            opt.value = it.value;
            opt.textContent = it.text;
            selSub.appendChild(opt);
        });
    }

    // ── 迴路用電（NRGD-/NRGM-）──

    function circuitIdOfNrgSid(sid) {
        return parseInt(String(sid).substring(5), 10);   // 'NRGD-' / 'NRGM-' / 'NRGP-' 後綴即迴路 Id
    }

    function filterNrgPointsByCircuit(nCircuitId) {
        return _points.filter(function (p) {
            return isNrgSid(p.sid) && circuitIdOfNrgSid(p.sid) === nCircuitId;
        });
    }

    // ── 多 ID 設備判斷 ──

    function isMultiIdCoord(coord) {
        return coord && coord.modbusId && coord.modbusId.includes(',');
    }

    function getSubDevices(coord) {
        var c = window.PointGrouping.parseCoord(coord);
        return c.modbusIds.map(function (mid, i) {
            return { modbusId: parseInt(mid), name: c.deviceNames[i] || mid };
        });
    }

    // ── SID 反推 Coordinator / SubDevice ──

    function findCoordForSid(sid) {
        var hyphen = sid.indexOf('-');
        if (hyphen < 0) return null;
        var num = parseInt(sid.substring(0, hyphen));
        return _coordinators.find(function (c) {
            return num >= c.id * 65536 && num < (c.id + 1) * 65536;
        }) || null;
    }

    function findSubModbusIdForSid(sid) {
        var num = parseInt(sid.substring(0, sid.indexOf('-')));
        return Math.floor(((num - 1) % 65536) / 256);
    }

    // ── 點位篩選 ──

    function filterPointsByCoord(nDbId) {
        return _points.filter(function (p) {
            var num = parseInt(p.sid.substring(0, p.sid.indexOf('-')));
            return num >= nDbId * 65536 && num < (nDbId + 1) * 65536;
        });
    }

    function filterPointsBySubDevice(nDbId, nSubModbusId) {
        var rangeBase = nDbId * 65536 + nSubModbusId * 256;
        var rangeEnd = rangeBase + 256;
        return _points.filter(function (p) {
            var num = parseInt(p.sid.substring(0, p.sid.indexOf('-')));
            return num >= rangeBase && num < rangeEnd;
        });
    }

    // ── 設備標籤 ──

    function getDeviceLabelForSid(sid) {
        if (isCalcSid(sid)) {
            var g = calcGroupOfSid(sid);
            return g ? t('alarm.option.calc_point') + ' - ' + g : t('alarm.option.calc_point');
        }
        if (isDmdSid(sid)) return t('alarm.option.demand_point');
        if (isNrgSid(sid)) return t('alarm.option.nrg_point');
        var coord = findCoordForSid(sid);
        if (!coord) return '';
        if (isMultiIdCoord(coord)) {
            var subId = findSubModbusIdForSid(sid);
            var subs = getSubDevices(coord);
            var sub = subs.find(function (s) { return s.modbusId === subId; });
            return sub ? sub.name : coord.name;
        }
        return coord.name;
    }

    function getFullPointLabel(sid, pointName) {
        var label = getDeviceLabelForSid(sid);
        return label ? label + ' / ' + (pointName || sid) : (pointName || sid);
    }

    function fillPointDropdown(pts) {
        var sel = document.getElementById('selPoint');
        sel.innerHTML = '<option value="">' + t('alarm.select.placeholder') + '</option>';
        pts.forEach(function (p) {
            var opt = document.createElement('option');
            opt.value = p.sid;
            opt.textContent = p.name;
            opt.dataset.dataType = p.dataType;
            sel.appendChild(opt);
        });
    }

    function showSubDeviceCol(show) {
        var subCol = document.getElementById('subDeviceCol');
        if (show) {
            subCol.classList.remove('d-none');
        } else {
            subCol.classList.add('d-none');
        }
    }

    // ── 初始化 ──

    function init() {
        _modal = new bootstrap.Modal(document.getElementById('ruleModal'));
        _lineModal = new bootstrap.Modal(document.getElementById('lineTargetModal'));
        _emailGroupModal = new bootstrap.Modal(document.getElementById('emailGroupModal'));
        _emailRecipientModal = new bootstrap.Modal(document.getElementById('emailRecipientModal'));
        _emailConfigModal = new bootstrap.Modal(document.getElementById('emailConfigModal'));
        _emailRulesModal = new bootstrap.Modal(document.getElementById('emailRulesModal'));
        _smsModal = new bootstrap.Modal(document.getElementById('smsTargetModal'));
        _smsConfigModal = new bootstrap.Modal(document.getElementById('smsConfigModal'));

        if (window.i18n && window.i18n.ready) {
            window.i18n.ready(function () {
                populateCoordinators();
                renderTable();
                renderLineTable();
                renderEmailTable();
                renderSmsTable();
            });
        } else {
            populateCoordinators();
            renderTable();
            renderLineTable();
            renderEmailTable();
            renderSmsTable();
        }

        // 共用規則表單：前置點位候選 = 本頁全部點位（虛擬點由 AlarmRuleForm 排除）
        window.AlarmRuleForm.setPoints(_points);
        window.AlarmRuleForm.reset();

        // Tab 切換時換按鈕
        var rulesTabBtn = document.getElementById('tab-rules-btn');
        var lineTabBtn = document.getElementById('tab-line-btn');
        var emailTabBtn = document.getElementById('tab-email-btn');
        var smsTabBtn = document.getElementById('tab-sms-btn');
        var btnAddRule = document.getElementById('btnAddRule');
        var btnAddLine = document.getElementById('btnAddLineTarget');
        var btnAddEmail = document.getElementById('btnAddEmailGroup');
        var btnEmailConfig = document.getElementById('btnEmailConfig');
        var btnAddSms = document.getElementById('btnAddSmsTarget');
        var btnSmsConfig = document.getElementById('btnSmsConfig');

        function setActiveButtons(tab) {
            btnAddRule.classList.toggle('d-none', tab !== 'rules');
            btnAddLine.classList.toggle('d-none', tab !== 'line');
            btnAddEmail.classList.toggle('d-none', tab !== 'email');
            btnEmailConfig.classList.toggle('d-none', tab !== 'email');
            btnAddSms.classList.toggle('d-none', tab !== 'sms');
            btnSmsConfig.classList.toggle('d-none', tab !== 'sms');
            // 簡訊 tab 顯示時輪詢狀態（10 秒），離開即停止
            if (tab === 'sms') startSmsStatusPolling();
            else stopSmsStatusPolling();
        }

        // 用 URL hash 記住當前 tab，重整後（saveGroup / saveConfig 等都會 location.reload）才不會跳回預設的「警報規則」
        var TAB_HASHES = { '#tab-line': lineTabBtn, '#tab-email': emailTabBtn, '#tab-rules': rulesTabBtn, '#tab-sms': smsTabBtn };
        function updateHash(hash) {
            if (window.history && window.history.replaceState) {
                window.history.replaceState(null, '', window.location.pathname + window.location.search + hash);
            } else {
                window.location.hash = hash;
            }
        }
        if (rulesTabBtn) rulesTabBtn.addEventListener('shown.bs.tab', function () { setActiveButtons('rules'); updateHash('#tab-rules'); });
        if (lineTabBtn) lineTabBtn.addEventListener('shown.bs.tab', function () { setActiveButtons('line'); updateHash('#tab-line'); });
        if (emailTabBtn) emailTabBtn.addEventListener('shown.bs.tab', function () { setActiveButtons('email'); updateHash('#tab-email'); });
        if (smsTabBtn) smsTabBtn.addEventListener('shown.bs.tab', function () { setActiveButtons('sms'); updateHash('#tab-sms'); });

        var targetBtn = TAB_HASHES[window.location.hash];
        if (targetBtn) {
            bootstrap.Tab.getOrCreateInstance(targetBtn).show();
        }
    }

    function populateCoordinators() {
        var sel = document.getElementById('selCoord');
        sel.innerHTML = '<option value="">' + t('alarm.select.placeholder') + '</option>';
        _coordinators.forEach(function (c) {
            var opt = document.createElement('option');
            opt.value = c.id;
            opt.textContent = c.name;
            sel.appendChild(opt);
        });
        var calcOpt = document.createElement('option');
        calcOpt.value = CALC_DEVICE_ID;
        calcOpt.textContent = t('alarm.option.calc_point');
        sel.appendChild(calcOpt);
        // 需量虛擬點位（有點位才顯示）
        if (_points.some(function (p) { return isDmdSid(p.sid); })) {
            var dmdOpt = document.createElement('option');
            dmdOpt.value = DMD_DEVICE_ID;
            dmdOpt.textContent = t('alarm.option.demand_point');
            sel.appendChild(dmdOpt);
        }
        // 迴路用電虛擬點位（有迴路才顯示）
        if (_circuits.length > 0) {
            var nrgOpt = document.createElement('option');
            nrgOpt.value = NRG_DEVICE_ID;
            nrgOpt.textContent = t('alarm.option.nrg_point');
            sel.appendChild(nrgOpt);
        }
    }

    function onCoordChange() {
        var nDbId = parseInt(document.getElementById('selCoord').value) || 0;
        var selSub = document.getElementById('selSubDevice');
        document.getElementById('txtSid').value = '';
        fillPointDropdown([]);

        if (!nDbId) {
            showSubDeviceCol(false);
            selSub.innerHTML = '<option value="">' + t('alarm.select.sub_device_placeholder') + '</option>';
            return;
        }

        if (nDbId === CALC_DEVICE_ID) {
            var groupInfo = getCalcGroupInfo();
            if (groupInfo.names.length > 0) {
                // 有群組 → 兩層瀏覽（沿用子設備欄，標籤改「群組」）；未分組點位歸「未分組」桶
                setSubDeviceLabel('calc');
                showSubDeviceCol(true);
                var groupItems = groupInfo.names.map(function (g) { return { value: g, text: g }; });
                if (groupInfo.hasUngrouped)
                    groupItems.push({ value: CALC_UNGROUPED, text: t('alarm.option.ungrouped') });
                fillSubDropdown('alarm.select.group_placeholder', groupItems);
            } else {
                // 全部未分組 → 維持平鋪清單（不多一層無意義的下拉）
                showSubDeviceCol(false);
                fillPointDropdown(_points.filter(function (p) { return isCalcSid(p.sid); }));
            }
            return;
        }

        if (nDbId === DMD_DEVICE_ID) {
            showSubDeviceCol(false);
            fillPointDropdown(_points.filter(function (p) { return isDmdSid(p.sid); }));
            return;
        }

        if (nDbId === NRG_DEVICE_ID) {
            setSubDeviceLabel('nrg');
            showSubDeviceCol(true);
            fillSubDropdown('alarm.select.circuit_placeholder', _circuits.map(function (c) {
                return { value: c.id, text: c.name };
            }));
            return;
        }

        var coord = _coordinators.find(function (c) { return c.id === nDbId; });

        if (coord && isMultiIdCoord(coord)) {
            setSubDeviceLabel('sub');
            showSubDeviceCol(true);
            selSub.innerHTML = '<option value="">' + t('alarm.select.sub_device_placeholder') + '</option>';
            getSubDevices(coord).forEach(function (s) {
                var opt = document.createElement('option');
                opt.value = s.modbusId;
                opt.textContent = s.name;
                selSub.appendChild(opt);
            });
        } else {
            showSubDeviceCol(false);
            fillPointDropdown(filterPointsByCoord(nDbId));
        }
    }

    function onSubDeviceChange() {
        var nDbId = parseInt(document.getElementById('selCoord').value) || 0;
        var subValue = document.getElementById('selSubDevice').value;
        document.getElementById('txtSid').value = '';

        if (nDbId === CALC_DEVICE_ID) {
            fillPointDropdown(subValue ? filterCalcPointsByGroup(subValue) : []);
            return;
        }

        if (nDbId === NRG_DEVICE_ID) {
            var nCircuitId = parseInt(subValue);
            fillPointDropdown(!isNaN(nCircuitId) ? filterNrgPointsByCircuit(nCircuitId) : []);
            return;
        }

        var nSubId = parseInt(subValue);
        if (!isNaN(nSubId)) {
            fillPointDropdown(filterPointsBySubDevice(nDbId, nSubId));
        } else {
            fillPointDropdown([]);
        }
    }

    function onPointChange() {
        var sel = document.getElementById('selPoint');
        document.getElementById('txtSid').value = sel.value;
        applyDiLabelsForSid(sel.value);
    }

    function setupSelectorsForSid(sid) {
        if (isCalcSid(sid)) {
            document.getElementById('selCoord').value = CALC_DEVICE_ID;
            var groupInfo = getCalcGroupInfo();
            if (groupInfo.names.length > 0) {
                // 兩層瀏覽：還原群組選取（無群組點位 → 未分組桶）
                onCoordChange();
                var groupValue = calcGroupOfSid(sid) || CALC_UNGROUPED;
                document.getElementById('selSubDevice').value = groupValue;
                fillPointDropdown(filterCalcPointsByGroup(groupValue));
            } else {
                showSubDeviceCol(false);
                fillPointDropdown(_points.filter(function (p) { return isCalcSid(p.sid); }));
            }
            document.getElementById('selPoint').value = sid;
            document.getElementById('txtSid').value = sid;
            applyDiLabelsForSid(sid);
            return;
        }

        if (isNrgSid(sid)) {
            document.getElementById('selCoord').value = NRG_DEVICE_ID;
            setSubDeviceLabel('nrg');
            showSubDeviceCol(true);
            fillSubDropdown('alarm.select.circuit_placeholder', _circuits.map(function (c) {
                return { value: c.id, text: c.name };
            }));
            var nCircuitId = circuitIdOfNrgSid(sid);
            document.getElementById('selSubDevice').value = nCircuitId;
            fillPointDropdown(filterNrgPointsByCircuit(nCircuitId));
            document.getElementById('selPoint').value = sid;
            document.getElementById('txtSid').value = sid;
            applyDiLabelsForSid(sid);
            return;
        }

        if (isDmdSid(sid)) {
            document.getElementById('selCoord').value = DMD_DEVICE_ID;
            showSubDeviceCol(false);
            fillPointDropdown(_points.filter(function (p) { return isDmdSid(p.sid); }));
            document.getElementById('selPoint').value = sid;
            document.getElementById('txtSid').value = sid;
            applyDiLabelsForSid(sid);
            return;
        }

        var coord = findCoordForSid(sid);
        if (!coord) {
            document.getElementById('selCoord').value = '';
            showSubDeviceCol(false);
            fillPointDropdown([]);
            return;
        }

        document.getElementById('selCoord').value = coord.id;

        if (isMultiIdCoord(coord)) {
            setSubDeviceLabel('sub');
            showSubDeviceCol(true);
            var selSub = document.getElementById('selSubDevice');
            selSub.innerHTML = '<option value="">' + t('alarm.select.sub_device_placeholder') + '</option>';
            getSubDevices(coord).forEach(function (s) {
                var opt = document.createElement('option');
                opt.value = s.modbusId;
                opt.textContent = s.name;
                selSub.appendChild(opt);
            });
            var subId = findSubModbusIdForSid(sid);
            selSub.value = subId;
            fillPointDropdown(filterPointsBySubDevice(coord.id, subId));
        } else {
            showSubDeviceCol(false);
            fillPointDropdown(filterPointsByCoord(coord.id));
        }

        document.getElementById('selPoint').value = sid;
        document.getElementById('txtSid').value = sid;
        applyDiLabelsForSid(sid);
    }

    function toggleSection(type) {
        window.AlarmRuleForm.toggleSection(type);
    }

    function severityBadge(n) {
        return '<span class="badge badge-severity-' + n + '">' + severityLabel(n) + '</span>';
    }

    function renderTable() {
        var tbody = document.getElementById('rulesBody');
        var emptyMsg = document.getElementById('emptyMsg');

        if (_rules.length === 0) {
            tbody.innerHTML = '';
            emptyMsg.classList.remove('d-none');
            return;
        }
        emptyMsg.classList.add('d-none');

        var html = '';
        _rules.forEach(function (r) {
            var highHtml = r.isAlarmHigh
                ? '<span class="alarm-tag alarm-tag-high">≥ ' + r.alarmHighValue + ' ' + severityBadge(r.alarmHighSeverity) + '</span>'
                : '<span class="alarm-tag alarm-tag-off">-</span>';

            var lowHtml = r.isAlarmLow
                ? '<span class="alarm-tag alarm-tag-low">≤ ' + r.alarmLowValue + ' ' + severityBadge(r.alarmLowSeverity) + '</span>'
                : '<span class="alarm-tag alarm-tag-off">-</span>';

            var diHtml;
            if (r.isDiAlarm) {
                var diLabels = getDiLabelsForSid(r.sid);
                var triggerLabel = r.diTriggerState === 'OFF' ? diLabels.offLabel : diLabels.onLabel;
                diHtml = '<span class="alarm-tag alarm-tag-di">' + escHtml(triggerLabel) + ' ' + severityBadge(r.diAlarmSeverity) + '</span>';
            } else {
                diHtml = '<span class="alarm-tag alarm-tag-off">-</span>';
            }

            var fullLabel = getFullPointLabel(r.sid, r.pointName);

            html += '<tr' + (r.isEnabled ? '' : ' class="table-secondary"') + '>'
                + '<td><input type="checkbox" class="form-check-input"' + (r.isEnabled ? ' checked' : '')
                + ' onchange="window._alarm.toggleEnabled(' + r.id + ', this.checked)" /></td>'
                + '<td>' + escHtml(fullLabel) + '</td>'
                + '<td>' + highHtml + '</td>'
                + '<td>' + lowHtml + '</td>'
                + '<td>' + diHtml + '</td>'
                + '<td>' + preconditionHtml(r) + '</td>'
                + '<td>' + escHtml(r.remarks) + '</td>'
                + '<td>'
                + '<button class="btn btn-sm btn-outline-primary me-1" onclick="window._alarm.editRule(' + r.id + ')"><i class="fas fa-pen"></i></button>'
                + '<button class="btn btn-sm btn-outline-danger" onclick="window._alarm.deleteRule(' + r.id + ')"><i class="fas fa-trash"></i></button>'
                + '</td></tr>';
        });
        tbody.innerHTML = html;
    }

    // 前置條件 / 警報延遲摘要欄；前置點位已不存在時標紅警示（警報會被永久遮蔽）
    function preconditionHtml(r) {
        var parts = [];
        var sum = window.AlarmRuleForm.preconditionSummary(r);
        if (sum.text) {
            parts.push(sum.isMissing
                ? '<span class="alarm-tag alarm-tag-pre-missing" title="' + escHtml(t('arf.summary.missing')) + '">'
                    + '<i class="fas fa-exclamation-triangle me-1"></i>' + escHtml(sum.text) + '</span>'
                : '<span class="alarm-tag alarm-tag-pre">' + escHtml(sum.text) + '</span>');
        }
        if (r.alarmDelaySec > 0) {
            parts.push('<span class="alarm-tag alarm-tag-off">' + escHtml(t('alarm.rule.delay_tag', { sec: r.alarmDelaySec })) + '</span>');
        }
        return parts.length ? parts.join(' ') : '<span class="alarm-tag alarm-tag-off">-</span>';
    }

    function escHtml(s) {
        if (s === null || s === undefined) return '';
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(String(s)));
        return div.innerHTML;
    }

    function showAddModal() {
        document.getElementById('ruleModalTitle').textContent = t('alarm.modal.title_add');
        document.getElementById('editId').value = '';
        document.getElementById('selCoord').value = '';
        showSubDeviceCol(false);
        document.getElementById('selSubDevice').innerHTML = '<option value="">' + t('alarm.select.sub_device_placeholder') + '</option>';
        document.getElementById('selPoint').innerHTML = '<option value="">' + t('alarm.select.point_placeholder') + '</option>';
        document.getElementById('txtSid').value = '';
        window.AlarmRuleForm.setSelfSid('');
        window.AlarmRuleForm.reset();
        _modal.show();
    }

    function editRule(id) {
        var rule = _rules.find(function (r) { return r.id === id; });
        if (!rule) return;

        document.getElementById('ruleModalTitle').textContent = t('alarm.modal.title_edit');
        document.getElementById('editId').value = rule.id;

        window.AlarmRuleForm.fill(rule);
        // 選點器還原（內含 applyDiLabelsForSid：ON/OFF 標籤一律以 Designer 設定為準，覆蓋規則中過時的快照）
        setupSelectorsForSid(rule.sid);
        _modal.show();
    }

    function save() {
        var sid = document.getElementById('txtSid').value.trim();
        var dto = window.AlarmRuleForm.read(sid, document.getElementById('editId').value);
        var szErr = window.AlarmRuleForm.validate(dto);
        if (szErr) { alert(szErr); return; }

        window.AlarmRuleForm.save(dto)
        .then(function (res) {
            if (res.success) {
                _modal.hide();
                location.reload();
            } else {
                alert(res.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    function deleteRule(id) {
        if (!confirm(t('alarm.confirm.delete_rule'))) return;

        window.AlarmRuleForm.remove(id)
        .then(function (res) {
            if (res.success) location.reload();
            else alert(res.message || t('alarm.alert.delete_failed'));
        })
        .catch(function (e) { alert(t('alarm.alert.delete_failed') + ': ' + e.message); });
    }

    function toggleEnabled(id, isEnabled) {
        var rule = _rules.find(function (r) { return r.id === id; });
        if (!rule) return;

        // 整條規則原樣帶回（含前置條件 / 延遲欄位），只改啟用旗標
        var dto = Object.assign({}, rule, { isEnabled: isEnabled });

        window.AlarmRuleForm.save(dto)
        .then(function (res) {
            if (res.success) {
                rule.isEnabled = isEnabled;
            } else {
                alert(res.message || t('alarm.alert.save_failed'));
            }
            renderTable();
        });
    }

    // ── Line 通知設定 Tab ──

    function renderLineTable() {
        var tbody = document.getElementById('lineTargetsBody');
        var emptyMsg = document.getElementById('lineEmptyMsg');

        if (_lineTargets.length === 0) {
            tbody.innerHTML = '';
            emptyMsg.classList.remove('d-none');
            return;
        }
        emptyMsg.classList.add('d-none');

        var html = '';
        _lineTargets.forEach(function (t1) {
            var pillText = maxSeverityPillLabel(t1.maxSeverity);
            html += '<tr' + (t1.isEnabled ? '' : ' class="table-secondary"') + '>'
                + '<td><input type="checkbox" class="form-check-input"'
                + (t1.isEnabled ? ' checked' : '')
                + ' onchange="window._alarmSetting.toggleLineEnabled(' + t1.id + ', this.checked)" /></td>'
                + '<td>' + escHtml(t1.label) + '</td>'
                + '<td><span class="group-id-cell">' + escHtml(t1.groupId) + '</span></td>'
                + '<td>' + escHtml(t1.language || 'zh-TW') + '</td>'
                + '<td><span class="line-severity-pill">' + escHtml(pillText) + '</span></td>'
                + '<td>'
                + '<button class="btn btn-sm btn-line-test me-1" data-line-id="' + t1.id + '" onclick="window._alarmSetting.testLineSend(' + t1.id + ', this)">'
                + '<i class="fas fa-paper-plane me-1"></i>' + t('alarm.button.test_send') + '</button>'
                + '<button class="btn btn-sm btn-outline-primary me-1" onclick="window._alarmSetting.editLineTarget(' + t1.id + ')"><i class="fas fa-pen"></i></button>'
                + '<button class="btn btn-sm btn-outline-danger" onclick="window._alarmSetting.deleteLineTarget(' + t1.id + ')"><i class="fas fa-trash"></i></button>'
                + '</td></tr>';
        });
        tbody.innerHTML = html;
    }

    function showAddLineModal() {
        document.getElementById('lineTargetModalTitle').textContent = t('alarm.line_modal.title_add');
        document.getElementById('lineTargetEditId').value = '';
        document.getElementById('txtLineLabel').value = '';
        document.getElementById('txtLineGroupId').value = '';
        document.getElementById('selLineMaxSeverity').value = '3';
        document.getElementById('selLineLanguage').value = 'zh-TW';
        document.getElementById('chkLineEnabled').checked = true;
        _lineModal.show();
    }

    function editLineTarget(id) {
        var target = _lineTargets.find(function (x) { return x.id === id; });
        if (!target) return;
        document.getElementById('lineTargetModalTitle').textContent = t('alarm.line_modal.title_edit');
        document.getElementById('lineTargetEditId').value = target.id;
        document.getElementById('txtLineLabel').value = target.label;
        document.getElementById('txtLineGroupId').value = target.groupId;
        document.getElementById('selLineMaxSeverity').value = String(target.maxSeverity);
        document.getElementById('selLineLanguage').value = target.language || 'zh-TW';
        document.getElementById('chkLineEnabled').checked = target.isEnabled;
        _lineModal.show();
    }

    function saveLineTarget() {
        var label = document.getElementById('txtLineLabel').value.trim();
        var groupId = document.getElementById('txtLineGroupId').value.trim();
        if (!label) { alert(t('alarm.alert.input_label')); return; }
        if (!groupId) { alert(t('alarm.alert.input_group_id')); return; }

        var editId = document.getElementById('lineTargetEditId').value;
        var dto = {
            id: editId ? parseInt(editId) : null,
            label: label,
            groupId: groupId,
            maxSeverity: parseInt(document.getElementById('selLineMaxSeverity').value),
            language: document.getElementById('selLineLanguage').value,
            isEnabled: document.getElementById('chkLineEnabled').checked
        };

        fetch('/api/line-targets', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(dto)
        })
        .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
        .then(function (res) {
            if (res.ok && res.body.success) {
                _lineModal.hide();
                location.reload();
            } else {
                alert(res.body.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    function deleteLineTarget(id) {
        if (!confirm(t('alarm.confirm.delete_line_group'))) return;
        fetch('/api/line-targets/' + id, { method: 'DELETE' })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) location.reload();
            else alert(res.message || t('alarm.alert.delete_failed'));
        })
        .catch(function (e) { alert(t('alarm.alert.delete_failed') + ': ' + e.message); });
    }

    function toggleLineEnabled(id, isEnabled) {
        fetch('/api/line-targets/' + id + '/toggle', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ isEnabled: isEnabled })
        })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) {
                var target = _lineTargets.find(function (x) { return x.id === id; });
                if (target) target.isEnabled = isEnabled;
                renderLineTable();
            } else {
                alert(res.message || t('alarm.alert.toggle_failed'));
                location.reload();
            }
        });
    }

    function testLineSend(id, btn) {
        if (btn) {
            btn.disabled = true;
            btn.dataset.origHtml = btn.innerHTML;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>' + t('alarm.button.sending');
        }
        fetch('/api/line-targets/' + id + '/test', { method: 'POST' })
        .then(function (r) {
            return r.json().then(function (b) { return { status: r.status, body: b }; });
        })
        .then(function (res) {
            if (res.status === 200 && res.body.success) {
                showToastSafe(t('alarm.toast.test_sent'), 'success');
            } else if (res.status === 429) {
                alert(res.body.message || t('alarm.alert.retry_later'));
            } else {
                alert(t('alarm.alert.send_failed_with_msg', { message: (res.body.message || res.status) }));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.send_failed') + ': ' + e.message); })
        .finally(function () {
            if (btn) {
                btn.disabled = false;
                if (btn.dataset.origHtml) btn.innerHTML = btn.dataset.origHtml;
            }
        });
    }

    function showToastSafe(msg, kind) {
        // 簡易 toast：success / warning 浮出提示，其餘走 alert；如未來導入 Bootstrap toast 可在此替換
        if (kind === 'success' || kind === 'warning') {
            var isWarn = kind === 'warning';
            var div = document.createElement('div');
            div.className = 'alert position-fixed ' + (isWarn ? 'alert-warning' : 'alert-success');
            div.style.top = '80px';
            div.style.right = '20px';
            div.style.zIndex = '9999';
            div.style.maxWidth = '420px';
            div.innerHTML = '<i class="fas ' + (isWarn ? 'fa-exclamation-triangle' : 'fa-check-circle') + ' me-1"></i>' + escHtml(msg);
            document.body.appendChild(div);
            setTimeout(function () { div.remove(); }, isWarn ? 7000 : 3500);
        } else {
            alert(msg);
        }
    }

    /** 存檔回應的 reload 欄位（ok / failed / timeout）→ toast 樣式：Engine 已套用 = success，其餘 = warning */
    function reloadToastKind(body) {
        return (body && body.reload && body.reload !== 'ok') ? 'warning' : 'success';
    }

    // ── Email 通知設定 Tab ──

    function renderEmailTable() {
        var tbody = document.getElementById('emailGroupsBody');
        var emptyMsg = document.getElementById('emailEmptyMsg');
        if (!_emailGroups || _emailGroups.length === 0) {
            tbody.innerHTML = '';
            emptyMsg.classList.remove('d-none');
            return;
        }
        emptyMsg.classList.add('d-none');
        var html = '';
        _emailGroups.forEach(function (g) {
            var pillText = maxSeverityPillLabel(g.maxSeverity);
            html += '<tr' + (g.isEnabled ? '' : ' class="table-secondary"') + '>'
                + '<td><input type="checkbox" class="form-check-input"'
                + (g.isEnabled ? ' checked' : '')
                + ' onchange="window._alarmEmail.toggleGroupEnabled(' + g.id + ', this.checked)" /></td>'
                + '<td><b>' + escHtml(g.label) + '</b>'
                + '<br/><small class="text-muted">' + escHtml(g.name) + '</small></td>'
                + '<td>' + (g.recipients ? g.recipients.length : 0) + '</td>'
                + '<td>' + escHtml(g.language || 'zh-TW') + '</td>'
                + '<td><span class="line-severity-pill">' + escHtml(pillText) + '</span></td>'
                + '<td>'
                + '<button class="btn btn-sm btn-outline-secondary me-1" onclick="window._alarmEmail.showRulesModal(' + g.id + ')" title="' + t('alarm.email.button.rules') + '"><i class="fas fa-link"></i></button>'
                + '<button class="btn btn-sm btn-outline-primary me-1" onclick="window._alarmEmail.editGroup(' + g.id + ')"><i class="fas fa-pen"></i></button>'
                + '<button class="btn btn-sm btn-outline-danger" onclick="window._alarmEmail.deleteGroup(' + g.id + ')"><i class="fas fa-trash"></i></button>'
                + '</td></tr>';
        });
        tbody.innerHTML = html;
    }

    function showConfigModal() {
        var c = _emailSenderConfig || {};
        document.getElementById('txtSmtpHost').value = c.smtpHost || '';
        document.getElementById('txtSmtpPort').value = c.smtpPort || 587;
        document.getElementById('chkSmtpStartTls').checked = c.useStartTls !== false;
        document.getElementById('chkSmtpSsl').checked = !!c.useSsl;
        document.getElementById('txtSmtpUsername').value = c.username || '';
        document.getElementById('txtSmtpPassword').value = '';
        document.getElementById('smtpPasswordHint').textContent = c.hasPassword
            ? t('alarm.field.smtp_password_already_set')
            : t('alarm.field.smtp_password_not_set');
        document.getElementById('txtFromAddress').value = c.fromAddress || '';
        document.getElementById('txtFromDisplayName').value = c.fromDisplayName || 'SCADA Engine';
        document.getElementById('txtEmailRatePerMinute').value = c.ratePerMinute || 10;
        document.getElementById('txtEmailTestThrottle').value = c.testSendThrottleSeconds || 10;
        document.getElementById('chkEmailEnableNotification').checked = c.enableNotification !== false;
        _emailConfigModal.show();
    }

    function saveConfig() {
        var dto = {
            enableNotification: document.getElementById('chkEmailEnableNotification').checked,
            smtpHost: document.getElementById('txtSmtpHost').value.trim(),
            smtpPort: parseInt(document.getElementById('txtSmtpPort').value) || 587,
            useSsl: document.getElementById('chkSmtpSsl').checked,
            useStartTls: document.getElementById('chkSmtpStartTls').checked,
            username: document.getElementById('txtSmtpUsername').value.trim(),
            password: document.getElementById('txtSmtpPassword').value,
            fromAddress: document.getElementById('txtFromAddress').value.trim(),
            fromDisplayName: document.getElementById('txtFromDisplayName').value.trim(),
            ratePerMinute: parseInt(document.getElementById('txtEmailRatePerMinute').value) || 10,
            testSendThrottleSeconds: parseInt(document.getElementById('txtEmailTestThrottle').value) || 10
        };
        fetch('/api/email-config', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(dto)
        })
        .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
        .then(function (res) {
            if (res.ok && res.body.success) {
                _emailConfigModal.hide();
                // 先讓使用者看到 Engine 熱重載結果（ok / Engine 未回應 / 套用失敗），再重整頁面刷新 SMTP 摘要
                var kind = reloadToastKind(res.body);
                showToastSafe(res.body.message || t('alarm.toast.saved'), kind);
                setTimeout(function () { location.reload(); }, kind === 'success' ? 1500 : 5000);
            } else {
                alert(res.body.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    function showAddGroupModal() {
        document.getElementById('emailGroupModalTitle').textContent = t('alarm.email_modal.title_add_group');
        document.getElementById('emailGroupEditId').value = '';
        document.getElementById('txtEmailGroupName').value = '';
        document.getElementById('txtEmailGroupLabel').value = '';
        document.getElementById('selEmailMaxSeverity').value = '3';
        document.getElementById('selEmailLanguage').value = 'zh-TW';
        document.getElementById('chkEmailGroupEnabled').checked = true;
        document.getElementById('txtEmailGroupRemarks').value = '';
        _currentEmailGroupRecipients = [];
        renderEmailRecipientsInModal();
        _emailGroupModal.show();
    }

    function editGroup(id) {
        var g = _emailGroups.find(function (x) { return x.id === id; });
        if (!g) return;
        document.getElementById('emailGroupModalTitle').textContent = t('alarm.email_modal.title_edit_group');
        document.getElementById('emailGroupEditId').value = g.id;
        document.getElementById('txtEmailGroupName').value = g.name;
        document.getElementById('txtEmailGroupLabel').value = g.label;
        document.getElementById('selEmailMaxSeverity').value = String(g.maxSeverity);
        document.getElementById('selEmailLanguage').value = g.language || 'zh-TW';
        document.getElementById('chkEmailGroupEnabled').checked = g.isEnabled;
        document.getElementById('txtEmailGroupRemarks').value = g.remarks || '';
        _currentEmailGroupRecipients = (g.recipients || []).slice();
        renderEmailRecipientsInModal();
        _emailGroupModal.show();
    }

    function renderEmailRecipientsInModal() {
        var section = document.getElementById('emailRecipientsSection');
        var hint = document.getElementById('emailRecipientsHint');
        if (hint) hint.style.display = _currentEmailGroupRecipients.length ? 'none' : '';

        var existing = section.querySelector('table');
        if (existing) existing.remove();
        var existingBtn = section.querySelector('.btn-add-recipient');
        if (existingBtn) existingBtn.remove();

        if (_currentEmailGroupRecipients.length > 0) {
            var html = '<table class="table table-sm"><thead><tr>'
                + '<th>' + t('alarm.col.enabled') + '</th>'
                + '<th>Email</th>'
                + '<th>' + t('alarm.field.recipient_name') + '</th>'
                + '<th style="width:160px">' + t('alarm.col.actions') + '</th>'
                + '</tr></thead><tbody>';
            _currentEmailGroupRecipients.forEach(function (r) {
                html += '<tr>'
                    + '<td>' + (r.isEnabled ? '<i class="fas fa-check text-success"></i>' : '<i class="fas fa-times text-muted"></i>') + '</td>'
                    + '<td>' + escHtml(r.emailAddress) + '</td>'
                    + '<td>' + escHtml(r.displayName || '') + '</td>'
                    + '<td>'
                    + (r.id ? '<button class="btn btn-sm btn-outline-info me-1" onclick="window._alarmEmail.testRecipient(' + r.id + ', this)" title="' + t('alarm.button.test_send') + '"><i class="fas fa-paper-plane"></i></button>' : '')
                    + '<button class="btn btn-sm btn-outline-danger" onclick="window._alarmEmail.removeRecipient(' + (r.id || 0) + ')"><i class="fas fa-trash"></i></button>'
                    + '</td></tr>';
            });
            html += '</tbody></table>';
            section.insertAdjacentHTML('beforeend', html);
        }

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-secondary btn-add-recipient';
        btn.innerHTML = '<i class="fas fa-plus me-1"></i>' + t('alarm.button.add_recipient');
        btn.onclick = function () { showAddRecipientModal(); };
        section.appendChild(btn);
    }

    function showAddRecipientModal() {
        var editId = document.getElementById('emailGroupEditId').value;
        if (!editId) {
            alert(t('alarm.alert.save_group_first'));
            return;
        }
        document.getElementById('emailRecipientModalTitle').textContent = t('alarm.email_modal.title_add_recipient');
        document.getElementById('emailRecipientEditId').value = '';
        document.getElementById('emailRecipientGroupId').value = editId;
        document.getElementById('txtRecipientEmail').value = '';
        document.getElementById('txtRecipientName').value = '';
        document.getElementById('chkRecipientEnabled').checked = true;
        _emailRecipientModal.show();
    }

    function saveRecipient() {
        var email = document.getElementById('txtRecipientEmail').value.trim();
        var groupId = parseInt(document.getElementById('emailRecipientGroupId').value);
        if (!email) { alert(t('alarm.alert.input_email')); return; }
        var dto = {
            id: parseInt(document.getElementById('emailRecipientEditId').value) || null,
            groupId: groupId,
            emailAddress: email,
            displayName: document.getElementById('txtRecipientName').value.trim() || null,
            isEnabled: document.getElementById('chkRecipientEnabled').checked
        };
        fetch('/api/email-recipients', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(dto)
        })
        .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
        .then(function (res) {
            if (res.ok && res.body.success) {
                _emailRecipientModal.hide();
                location.reload();  // 重新載入以刷新 _emailGroups
            } else {
                alert(res.body.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    function removeRecipient(recipientId) {
        if (!recipientId) return;
        if (!confirm(t('alarm.confirm.delete_recipient'))) return;
        fetch('/api/email-recipients/' + recipientId, { method: 'DELETE' })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) {
                location.reload();
            } else {
                alert(res.message || t('alarm.alert.delete_failed'));
            }
        });
    }

    function saveGroup() {
        var name = document.getElementById('txtEmailGroupName').value.trim();
        var label = document.getElementById('txtEmailGroupLabel').value.trim();
        if (!name) { alert(t('alarm.alert.input_group_name')); return; }
        if (!label) { alert(t('alarm.alert.input_label')); return; }

        var editId = document.getElementById('emailGroupEditId').value;
        var dto = {
            id: editId ? parseInt(editId) : null,
            name: name,
            label: label,
            maxSeverity: parseInt(document.getElementById('selEmailMaxSeverity').value),
            language: document.getElementById('selEmailLanguage').value,
            isEnabled: document.getElementById('chkEmailGroupEnabled').checked,
            remarks: document.getElementById('txtEmailGroupRemarks').value.trim() || null
        };

        fetch('/api/email-groups', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(dto)
        })
        .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
        .then(function (res) {
            if (res.ok && res.body.success) {
                _emailGroupModal.hide();
                location.reload();
            } else {
                alert(res.body.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    function deleteGroup(id) {
        if (!confirm(t('alarm.confirm.delete_email_group'))) return;
        fetch('/api/email-groups/' + id, { method: 'DELETE' })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) location.reload();
            else alert(res.message || t('alarm.alert.delete_failed'));
        });
    }

    function toggleGroupEnabled(id, isEnabled) {
        fetch('/api/email-groups/' + id + '/toggle', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ isEnabled: isEnabled })
        }).then(function (r) { return r.json(); })
          .then(function (res) {
            if (res.success) {
                var g = _emailGroups.find(function (x) { return x.id === id; });
                if (g) g.isEnabled = isEnabled;
                renderEmailTable();
            }
          });
    }

    function showRulesModal(groupId) {
        var g = _emailGroups.find(function (x) { return x.id === groupId; });
        if (!g) return;
        document.getElementById('emailRulesGroupId').value = groupId;
        document.getElementById('emailRulesModalTitle').textContent =
            t('alarm.email_modal.title_rules') + ' - ' + g.label;
        var sel = (g.alarmRuleIds || []).slice();
        var list = document.getElementById('emailRulesList');
        if (!_alarmRulesForRouting || _alarmRulesForRouting.length === 0) {
            list.innerHTML = '<div class="text-muted p-3">' + t('alarm.empty.no_rules') + '</div>';
        } else {
            var html = '';
            _alarmRulesForRouting.forEach(function (r) {
                var checked = sel.indexOf(r.id) >= 0 ? ' checked' : '';
                html += '<div class="form-check">'
                     + '<input class="form-check-input email-rule-cb" type="checkbox" value="' + r.id + '" id="ruleCb_' + r.id + '"' + checked + '>'
                     + '<label class="form-check-label" for="ruleCb_' + r.id + '">'
                     + escHtml(r.pointName) + ' <small class="text-muted">(' + escHtml(r.sid) + ')</small></label></div>';
            });
            list.innerHTML = html;
        }
        _emailRulesModal.show();
    }

    function saveRules() {
        var groupId = parseInt(document.getElementById('emailRulesGroupId').value);
        var ids = [];
        document.querySelectorAll('.email-rule-cb:checked').forEach(function (cb) {
            ids.push(parseInt(cb.value));
        });
        fetch('/api/email-groups/' + groupId + '/rules', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(ids)
        }).then(function (r) { return r.json(); })
          .then(function (res) {
            if (res.success) {
                _emailRulesModal.hide();
                location.reload();
            } else {
                alert(res.message || t('alarm.alert.save_failed'));
            }
          });
    }

    function testRecipient(id, btn) {
        if (btn) {
            btn.disabled = true;
            btn.dataset.origHtml = btn.innerHTML;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
        }
        fetch('/api/email-recipients/' + id + '/test', { method: 'POST' })
        .then(function (r) {
            return r.json().then(function (b) { return { status: r.status, body: b }; });
        })
        .then(function (res) {
            if (res.status === 200 && res.body.success) {
                showToastSafe(t('alarm.toast.test_sent'), 'success');
            } else if (res.status === 429) {
                alert(res.body.message || t('alarm.alert.retry_later'));
            } else {
                alert(t('alarm.alert.send_failed_with_msg', { message: (res.body.message || res.status) }));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.send_failed') + ': ' + e.message); })
        .finally(function () {
            if (btn) {
                btn.disabled = false;
                if (btn.dataset.origHtml) btn.innerHTML = btn.dataset.origHtml;
            }
        });
    }

    // ── 簡訊通知設定 Tab ──

    function renderSmsTable() {
        var tbody = document.getElementById('smsTargetsBody');
        var emptyMsg = document.getElementById('smsEmptyMsg');
        if (!tbody) return;

        if (_smsTargets.length === 0) {
            tbody.innerHTML = '';
            emptyMsg.classList.remove('d-none');
            return;
        }
        emptyMsg.classList.add('d-none');

        var html = '';
        _smsTargets.forEach(function (s) {
            var pillText = maxSeverityPillLabel(s.maxSeverity);
            html += '<tr' + (s.isEnabled ? '' : ' class="table-secondary"') + '>'
                + '<td><input type="checkbox" class="form-check-input"'
                + (s.isEnabled ? ' checked' : '')
                + ' onchange="window._alarmSms.toggleEnabled(' + s.id + ', this.checked)" /></td>'
                + '<td>' + escHtml(s.label) + '</td>'
                + '<td><span class="group-id-cell">' + escHtml(s.phoneNumber) + '</span></td>'
                + '<td>' + escHtml(s.language || 'zh-TW') + '</td>'
                + '<td><span class="line-severity-pill">' + escHtml(pillText) + '</span></td>'
                + '<td>'
                + '<button class="btn btn-sm btn-outline-success me-1" onclick="window._alarmSms.testSend(' + s.id + ', this)">'
                + '<i class="fas fa-paper-plane me-1"></i>' + t('alarm.button.test_send') + '</button>'
                + '<button class="btn btn-sm btn-outline-primary me-1" onclick="window._alarmSms.editTarget(' + s.id + ')"><i class="fas fa-pen"></i></button>'
                + '<button class="btn btn-sm btn-outline-danger" onclick="window._alarmSms.deleteTarget(' + s.id + ')"><i class="fas fa-trash"></i></button>'
                + '</td></tr>';
        });
        tbody.innerHTML = html;
    }

    function showAddSmsModal() {
        document.getElementById('smsTargetModalTitle').textContent = t('alarm.sms_modal.title_add');
        document.getElementById('smsTargetEditId').value = '';
        document.getElementById('txtSmsLabel').value = '';
        document.getElementById('txtSmsPhone').value = '';
        document.getElementById('selSmsMaxSeverity').value = '3';
        document.getElementById('selSmsLanguage').value = 'zh-TW';
        document.getElementById('chkSmsEnabled').checked = true;
        _smsModal.show();
    }

    function editSmsTarget(id) {
        var target = _smsTargets.find(function (x) { return x.id === id; });
        if (!target) return;
        document.getElementById('smsTargetModalTitle').textContent = t('alarm.sms_modal.title_edit');
        document.getElementById('smsTargetEditId').value = target.id;
        document.getElementById('txtSmsLabel').value = target.label;
        document.getElementById('txtSmsPhone').value = target.phoneNumber;
        document.getElementById('selSmsMaxSeverity').value = String(target.maxSeverity);
        document.getElementById('selSmsLanguage').value = target.language || 'zh-TW';
        document.getElementById('chkSmsEnabled').checked = target.isEnabled;
        _smsModal.show();
    }

    function saveSmsTarget() {
        var label = document.getElementById('txtSmsLabel').value.trim();
        var phone = document.getElementById('txtSmsPhone').value.trim();
        if (!label) { alert(t('alarm.alert.input_label')); return; }
        if (!phone) { alert(t('alarm.alert.input_phone')); return; }
        if (!/^\+?\d{8,15}$/.test(phone)) { alert(t('alarm.alert.phone_invalid')); return; }

        var editId = document.getElementById('smsTargetEditId').value;
        var dto = {
            id: editId ? parseInt(editId) : null,
            label: label,
            phoneNumber: phone,
            maxSeverity: parseInt(document.getElementById('selSmsMaxSeverity').value),
            language: document.getElementById('selSmsLanguage').value,
            isEnabled: document.getElementById('chkSmsEnabled').checked
        };

        fetch('/api/sms-targets', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(dto)
        })
        .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
        .then(function (res) {
            if (res.ok && res.body.success) {
                _smsModal.hide();
                location.reload();
            } else {
                alert(res.body.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    function deleteSmsTarget(id) {
        if (!confirm(t('alarm.confirm.delete_sms_target'))) return;
        fetch('/api/sms-targets/' + id, { method: 'DELETE' })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) location.reload();
            else alert(res.message || t('alarm.alert.delete_failed'));
        })
        .catch(function (e) { alert(t('alarm.alert.delete_failed') + ': ' + e.message); });
    }

    function toggleSmsEnabled(id, isEnabled) {
        fetch('/api/sms-targets/' + id + '/toggle', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ isEnabled: isEnabled })
        })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) {
                var target = _smsTargets.find(function (x) { return x.id === id; });
                if (target) target.isEnabled = isEnabled;
                renderSmsTable();
            } else {
                alert(res.message || t('alarm.alert.toggle_failed'));
                location.reload();
            }
        });
    }

    function testSmsSend(id, btn) {
        if (btn) {
            btn.disabled = true;
            btn.dataset.origHtml = btn.innerHTML;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>' + t('alarm.button.sending');
        }
        fetch('/api/sms-targets/' + id + '/test', { method: 'POST' })
        .then(function (r) {
            return r.json().then(function (b) { return { status: r.status, body: b }; });
        })
        .then(function (res) {
            if (res.status === 200 && res.body.success) {
                showToastSafe(res.body.message || t('alarm.toast.test_sent'), 'success');
            } else if (res.status === 429) {
                alert(res.body.message || t('alarm.alert.retry_later'));
            } else {
                alert(t('alarm.alert.send_failed_with_msg', { message: (res.body.message || res.status) }));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.send_failed') + ': ' + e.message); })
        .finally(function () {
            if (btn) {
                btn.disabled = false;
                if (btn.dataset.origHtml) btn.innerHTML = btn.dataset.origHtml;
            }
        });
    }

    function showSmsConfigModal() {
        var c = _smsSenderConfig || {};
        document.getElementById('txtSmsComPort').value = c.comPort || 'auto';
        document.getElementById('txtSmsBaudRate').value = c.baudRate || 0;
        document.getElementById('txtSmsSimPin').value = c.simPin || '';
        document.getElementById('txtSmsRatePerMinute').value = c.ratePerMinute || 10;
        document.getElementById('txtSmsDailyQuota').value = (c.dailyQuota != null ? c.dailyQuota : 100);
        document.getElementById('txtSmsTestThrottle').value = c.testSendThrottleSeconds || 10;
        document.getElementById('chkSmsSendRecovery').checked = c.sendRecovery !== false;
        document.getElementById('chkSmsEnableNotification').checked = !!c.enableNotification;
        _smsConfigModal.show();
    }

    function saveSmsConfig() {
        var dto = {
            enableNotification: document.getElementById('chkSmsEnableNotification').checked,
            comPort: document.getElementById('txtSmsComPort').value.trim() || 'auto',
            baudRate: parseInt(document.getElementById('txtSmsBaudRate').value) || 0,
            simPin: document.getElementById('txtSmsSimPin').value,
            ratePerMinute: parseInt(document.getElementById('txtSmsRatePerMinute').value) || 10,
            dailyQuota: parseInt(document.getElementById('txtSmsDailyQuota').value) || 0,
            sendRecovery: document.getElementById('chkSmsSendRecovery').checked,
            testSendThrottleSeconds: parseInt(document.getElementById('txtSmsTestThrottle').value) || 10
        };
        fetch('/api/sms-config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(dto)
        })
        .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
        .then(function (res) {
            if (res.ok && res.body.success) {
                _smsConfigModal.hide();
                // Engine 熱重載 + 重掃序列埠；非 ok 時以 warning 提示（Engine 未回應 / 套用失敗）
                showToastSafe(res.body.message || t('alarm.toast.saved'), reloadToastKind(res.body));
                _smsSenderConfig = dto;
                refreshSmsStatus();
            } else {
                alert(res.body.message || t('alarm.alert.save_failed'));
            }
        })
        .catch(function (e) { alert(t('alarm.alert.save_failed') + ': ' + e.message); });
    }

    // ── 簡訊盒狀態卡（輪詢 /api/sms-status，來源 = Engine MQTT retained）──

    function startSmsStatusPolling() {
        refreshSmsStatus();
        if (!_smsStatusTimer)
            _smsStatusTimer = setInterval(refreshSmsStatus, 10000);
    }

    function stopSmsStatusPolling() {
        if (_smsStatusTimer) {
            clearInterval(_smsStatusTimer);
            _smsStatusTimer = null;
        }
    }

    function refreshSmsStatus() {
        fetch('/api/sms-status')
        .then(function (r) { return r.json(); })
        .then(function (res) {
            var badge = document.getElementById('smsStatusBadge');
            var elPort = document.getElementById('smsStatusPort');
            var elSignal = document.getElementById('smsStatusSignal');
            var elSim = document.getElementById('smsStatusSim');
            var elQuota = document.getElementById('smsStatusQuota');
            var elLastTest = document.getElementById('smsStatusLastTest');
            if (!badge) return;

            if (!res.available || !res.status) {
                badge.className = 'badge bg-secondary';
                badge.textContent = t('alarm.sms.status_unknown');
                elPort.textContent = '';
                elSignal.textContent = '';
                elSim.textContent = '';
                elQuota.textContent = '';
                elLastTest.textContent = '';
                return;
            }

            var s = res.status;
            if (!s.enabled) {
                badge.className = 'badge bg-secondary';
                badge.textContent = t('alarm.sms.status_disabled');
            } else if (s.connected) {
                badge.className = 'badge bg-success';
                badge.textContent = t('alarm.sms.status_connected');
            } else {
                badge.className = 'badge bg-danger';
                badge.textContent = t('alarm.sms.status_disconnected');
            }
            elPort.textContent = s.port ? (t('alarm.sms.status_port') + ': ' + s.port + (s.baudRate ? ' @ ' + s.baudRate : '')) : '';
            elSignal.textContent = (s.csq != null && s.csq >= 0) ? (t('alarm.sms.status_signal') + ': ' + s.csq + '/31') : '';
            elSim.textContent = s.simStatus ? ('SIM: ' + s.simStatus) : '';
            elQuota.textContent = (s.dailyQuota != null)
                ? (t('alarm.sms.status_today_sent') + ': ' + s.sentToday + (s.dailyQuota > 0 ? '/' + s.dailyQuota : ''))
                : '';
            if (s.lastTest) {
                elLastTest.textContent = t('alarm.sms.status_last_test') + ': '
                    + s.lastTest.phoneNumber + ' '
                    + (s.lastTest.success ? t('alarm.sms.test_ok') : (t('alarm.sms.test_fail') + '(' + (s.lastTest.error || '') + ')'))
                    + ' @ ' + s.lastTest.time;
            } else {
                elLastTest.textContent = '';
            }
        })
        .catch(function () { /* 輪詢失敗靜默，下輪重試 */ });
    }

    function rescanSmsModem(btn) {
        if (btn) {
            btn.disabled = true;
            btn.dataset.origHtml = btn.innerHTML;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>' + t('alarm.sms.button_rescanning');
        }
        fetch('/api/sms-rescan', { method: 'POST' })
        .then(function (r) { return r.json(); })
        .then(function (res) {
            if (res.success) showToastSafe(res.message, 'success');
            else alert(res.message || t('alarm.alert.send_failed'));
        })
        .catch(function (e) { alert(e.message); })
        .finally(function () {
            if (btn) {
                setTimeout(function () {
                    btn.disabled = false;
                    if (btn.dataset.origHtml) btn.innerHTML = btn.dataset.origHtml;
                    refreshSmsStatus();
                }, 3000);
            }
        });
    }

    document.addEventListener('DOMContentLoaded', init);

    window._alarm = {
        showAddModal: showAddModal,
        editRule: editRule,
        save: save,
        deleteRule: deleteRule,
        toggleEnabled: toggleEnabled,
        onCoordChange: onCoordChange,
        onSubDeviceChange: onSubDeviceChange,
        onPointChange: onPointChange,
        toggleSection: toggleSection
    };

    window._alarmSetting = {
        showAddLineModal: showAddLineModal,
        editLineTarget: editLineTarget,
        saveLineTarget: saveLineTarget,
        deleteLineTarget: deleteLineTarget,
        toggleLineEnabled: toggleLineEnabled,
        testLineSend: testLineSend
    };

    window._alarmSms = {
        showAddModal: showAddSmsModal,
        editTarget: editSmsTarget,
        saveTarget: saveSmsTarget,
        deleteTarget: deleteSmsTarget,
        toggleEnabled: toggleSmsEnabled,
        testSend: testSmsSend,
        showConfigModal: showSmsConfigModal,
        saveConfig: saveSmsConfig,
        rescan: rescanSmsModem
    };

    window._alarmEmail = {
        showConfigModal: showConfigModal,
        saveConfig: saveConfig,
        showAddGroupModal: showAddGroupModal,
        editGroup: editGroup,
        saveGroup: saveGroup,
        deleteGroup: deleteGroup,
        toggleGroupEnabled: toggleGroupEnabled,
        showRulesModal: showRulesModal,
        saveRules: saveRules,
        saveRecipient: saveRecipient,
        removeRecipient: removeRecipient,
        testRecipient: testRecipient
    };
})();
