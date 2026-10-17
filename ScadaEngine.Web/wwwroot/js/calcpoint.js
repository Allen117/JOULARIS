(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var _calcPoints = window._calcPointData || [];
    var _editMode = false;
    var _deleteSID = '';
    var _calcPointModal = null;
    var _deleteModal = null;
    var _pickerModal = null;

    // ── 點位選擇器狀態 ──
    var _pickerDevices = null;   // 設備清單
    var _pickerPoints = null;    // 點位清單
    var _pickerTargetRow = null; // 目前開啟選擇器的 <tr>
    var _pickerDevId = -1;
    var _pickerModbusId = null;
    var _pickerDeviceGroup = null; // Modbus 站號內 Device 分群（null=不依 Device 篩, ''=未分群桶）
    var _pickerCalcGroup = null;
    var _pickerSourceType = null;  // 'modbus' | 'calc' | 'db' | 'opc'
    var _pickerExtGroup = null;    // DB/OPC 來源的 Coordinator 群組
    var _pickerSelectedSid = null;
    var CALC_DEV_ID = -999;

    // ── 公式範本 ──
    var TEMPLATES = {
        wetbulb: {
            nameKey: 'calcpoint.tpl.wetbulb.point_name',
            hintKey: 'calcpoint.tpl.wetbulb.hint',
            unit: '\u00b0C',
            formula: 'WetBulb(T, RH)',
            vars: ['T', 'RH']
        },
        enthalpy: {
            nameKey: 'calcpoint.tpl.enthalpy.point_name',
            hintKey: 'calcpoint.tpl.enthalpy.hint',
            unit: 'kJ/kg',
            formula: 'Enthalpy(T, RH)',
            vars: ['T', 'RH']
        },
        dewpoint: {
            nameKey: 'calcpoint.tpl.dewpoint.point_name',
            hintKey: 'calcpoint.tpl.dewpoint.hint',
            unit: '\u00b0C',
            formula: 'DewPoint(T, RH)',
            vars: ['T', 'RH']
        }
    };

    function init() {
        _calcPointModal = new bootstrap.Modal(document.getElementById('calcPointModal'));
        _deleteModal = new bootstrap.Modal(document.getElementById('deleteModal'));

        var formulaInput = document.getElementById('inputFormula');
        if (formulaInput) new bootstrap.Tooltip(formulaInput);

        if (window.i18n && window.i18n.ready) {
            window.i18n.ready(renderTable);
        } else {
            renderTable();
        }
    }

    // ══════════════════════════════════════
    //  資料表格
    // ══════════════════════════════════════

    function renderTable() {
        var tbody = document.getElementById('calcPointTableBody');
        var emptyState = document.getElementById('emptyState');
        var countLabel = document.getElementById('calcPointCount');

        if (_calcPoints.length === 0) {
            tbody.innerHTML = '';
            emptyState.style.display = '';
            countLabel.textContent = t('calcpoint.count.total', { n: 0 });
            return;
        }

        emptyState.style.display = 'none';
        countLabel.textContent = t('calcpoint.count.total', { n: _calcPoints.length });

        var szEnabled = t('calcpoint.status.enabled');
        var szDisabled = t('calcpoint.status.disabled');
        var szEdit = t('calcpoint.btn.edit');
        var szDelete = t('calcpoint.btn.delete');

        var html = '';
        for (var i = 0; i < _calcPoints.length; i++) {
            var p = _calcPoints[i];
            var statusBadge = p.isEnabled
                ? '<span class="badge bg-success">' + escapeHtml(szEnabled) + '</span>'
                : '<span class="badge bg-secondary">' + escapeHtml(szDisabled) + '</span>';

            html += '<tr>' +
                '<td>' + escapeHtml(p.szName) + '</td>' +
                '<td class="font-monospace small text-truncate" style="max-width:250px;" title="' + escapeHtml(p.szFormula) + '">' + escapeHtml(p.szFormula) + '</td>' +
                '<td>' + escapeHtml(p.szUnit || '') + '</td>' +
                '<td>' + escapeHtml(p.szGroupName || '') + '</td>' +
                '<td class="text-center">' + statusBadge + '</td>' +
                '<td class="small">' + escapeHtml(p.szCreatedAt || '') + '</td>' +
                '<td class="text-center">' +
                '<button class="btn btn-outline-primary btn-sm me-1" onclick="window._calcPoint.openEditModal(\'' + escapeHtml(p.szSID) + '\')">' +
                '<i class="fas fa-edit me-1"></i>' + escapeHtml(szEdit) + '</button>' +
                '<button class="btn btn-outline-danger btn-sm" onclick="window._calcPoint.openDeleteModal(\'' + escapeHtml(p.szSID) + '\')">' +
                '<i class="fas fa-trash-alt me-1"></i>' + escapeHtml(szDelete) + '</button>' +
                '</td></tr>';
        }
        tbody.innerHTML = html;
    }

    // ══════════════════════════════════════
    //  變數列操作
    // ══════════════════════════════════════

    function addVariableRow(varName, sid, pointName) {
        var tbody = document.getElementById('variableTableBody');
        var tr = document.createElement('tr');
        var szDisplayName = pointName || '';
        if (!szDisplayName && sid) {
            szDisplayName = _findPointName(sid);
        }

        var szUnset = t('calcpoint.pk.unset');
        var szSelect = t('calcpoint.btn.select');
        var szVarPlaceholder = t('calcpoint.placeholder.var_name');

        tr.innerHTML =
            '<td><input type="text" class="form-control form-control-sm var-name" value="' + escapeHtml(varName || '') + '" placeholder="' + escapeHtml(szVarPlaceholder) + '"></td>' +
            '<td>' +
            '  <div class="d-flex align-items-center gap-2">' +
            '    <input type="hidden" class="var-sid" value="' + escapeHtml(sid || '') + '">' +
            '    <span class="var-point-label flex-grow-1 small' + (szDisplayName ? '' : ' text-muted') + '">' +
                    (szDisplayName ? escapeHtml(szDisplayName) : escapeHtml(szUnset)) +
            '    </span>' +
            '    <button class="btn btn-outline-primary btn-sm flex-shrink-0" onclick="window._calcPoint.openVarPicker(this)">' +
            '      <i class="fas fa-search me-1"></i>' + escapeHtml(szSelect) +
            '    </button>' +
            '  </div>' +
            '</td>' +
            '<td class="text-center"><button class="btn btn-outline-danger btn-sm" onclick="this.closest(\'tr\').remove()">' +
            '<i class="fas fa-times"></i></button></td>';
        tbody.appendChild(tr);
    }

    function _findPointName(sid) {
        if (!_pickerPoints || !sid) return sid;
        for (var i = 0; i < _pickerPoints.length; i++) {
            if (_pickerPoints[i].szSid === sid) {
                var p = _pickerPoints[i];
                return p._deviceLabel ? p._deviceLabel + ' / ' + p.szName : p.szName;
            }
        }
        return sid;
    }

    function collectFormData() {
        var inputMappings = {};
        var rows = document.querySelectorAll('#variableTableBody tr');
        for (var i = 0; i < rows.length; i++) {
            var name = rows[i].querySelector('.var-name').value.trim();
            var sid = rows[i].querySelector('.var-sid').value;
            if (name && sid) {
                inputMappings[name] = sid;
            }
        }

        return {
            name: document.getElementById('inputName').value.trim(),
            unit: document.getElementById('inputUnit').value.trim(),
            groupName: document.getElementById('inputGroupName').value.trim(),
            formula: document.getElementById('inputFormula').value.trim(),
            inputMappings: JSON.stringify(inputMappings)
        };
    }

    // ══════════════════════════════════════
    //  新增 / 編輯 Modal
    // ══════════════════════════════════════

    function applyTemplate(key) {
        var hint = document.getElementById('templateHint');
        if (!key) {
            if (hint) hint.style.display = 'none';
            return;
        }
        var tpl = TEMPLATES[key];
        if (!tpl) return;

        document.getElementById('inputName').value = t(tpl.nameKey);
        document.getElementById('inputUnit').value = tpl.unit;
        document.getElementById('inputFormula').value = tpl.formula;
        document.getElementById('previewResult').textContent = '';
        document.getElementById('variableTableBody').innerHTML = '';
        for (var i = 0; i < tpl.vars.length; i++) {
            addVariableRow(tpl.vars[i], '');
        }
        if (hint) {
            hint.textContent = t(tpl.hintKey);
            hint.style.display = '';
        }
    }

    function _resetTemplateSelect() {
        var sel = document.getElementById('selTemplate');
        if (sel) sel.value = '';
        var hint = document.getElementById('templateHint');
        if (hint) hint.style.display = 'none';
    }

    function openCreateModal() {
        _editMode = false;
        _resetTemplateSelect();
        document.getElementById('calcPointModalTitle').textContent = t('calcpoint.modal.add_title');
        document.getElementById('editSID').value = '';
        document.getElementById('inputName').value = '';
        document.getElementById('inputUnit').value = '';
        document.getElementById('inputGroupName').value = '';
        document.getElementById('inputFormula').value = '';
        document.getElementById('variableTableBody').innerHTML = '';
        document.getElementById('previewResult').textContent = '';
        addVariableRow('', '');
        _calcPointModal.show();
    }

    function openEditModal(sid) {
        var point = null;
        for (var i = 0; i < _calcPoints.length; i++) {
            if (_calcPoints[i].szSID === sid) { point = _calcPoints[i]; break; }
        }
        if (!point) return;

        _ensurePickerData(function () {
            _fillEditModal(point);
        });
    }

    function _fillEditModal(point) {
        _editMode = true;
        _resetTemplateSelect();
        document.getElementById('calcPointModalTitle').textContent = t('calcpoint.modal.edit_title') + ' - ' + point.szSID;
        document.getElementById('editSID').value = point.szSID;
        document.getElementById('inputName').value = point.szName;
        document.getElementById('inputUnit').value = point.szUnit || '';
        document.getElementById('inputGroupName').value = point.szGroupName || '';
        document.getElementById('inputFormula').value = point.szFormula;
        document.getElementById('previewResult').textContent = '';

        document.getElementById('variableTableBody').innerHTML = '';
        try {
            var mappings = JSON.parse(point.szInputMappings);
            var keys = Object.keys(mappings);
            for (var j = 0; j < keys.length; j++) {
                addVariableRow(keys[j], mappings[keys[j]]);
            }
        } catch (e) {
            addVariableRow('', '');
        }

        _calcPointModal.show();
    }

    // ══════════════════════════════════════
    //  儲存 / 刪除
    // ══════════════════════════════════════

    function saveCalcPoint() {
        var data = collectFormData();

        if (!data.name) { alert(t('calcpoint.msg.enter_name')); return; }
        if (!data.formula) { alert(t('calcpoint.msg.enter_formula')); return; }

        // 允許零變數（純常數 / 只用自訂函數的公式），不再強制至少一個變數

        var url, body;
        if (_editMode) {
            url = '/CalcPoint/Update';
            body = {
                SID: document.getElementById('editSID').value,
                Name: data.name,
                Unit: data.unit,
                GroupName: data.groupName,
                Formula: data.formula,
                InputMappings: data.inputMappings,
                IsEnabled: true
            };
        } else {
            url = '/CalcPoint/Create';
            body = {
                Name: data.name,
                Unit: data.unit,
                GroupName: data.groupName,
                Formula: data.formula,
                InputMappings: data.inputMappings
            };
        }

        fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                if (result.success) {
                    _calcPointModal.hide();
                    location.reload();
                } else {
                    alert(result.message || t('calcpoint.msg.operation_failed'));
                }
            })
            .catch(function (err) {
                console.error(err);
                alert(t('calcpoint.msg.network_error'));
            });
    }

    function openDeleteModal(sid) {
        _deleteSID = sid;
        var msgEl = document.getElementById('deleteMessage');
        if (msgEl) {
            msgEl.innerHTML = t('calcpoint.modal.delete_message', { sid: escapeHtml(sid) });
        }
        _deleteModal.show();
    }

    function confirmDelete() {
        fetch('/CalcPoint/Delete', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ SID: _deleteSID })
        })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                if (result.success) {
                    _deleteModal.hide();
                    location.reload();
                } else {
                    alert(result.message || t('calcpoint.msg.delete_failed_default'));
                }
            })
            .catch(function (err) {
                console.error(err);
                alert(t('calcpoint.msg.network_error'));
            });
    }

    // ══════════════════════════════════════
    //  公式預覽
    // ══════════════════════════════════════

    function previewFormula() {
        var data = collectFormData();
        if (!data.formula) { alert(t('calcpoint.msg.enter_formula_first')); return; }

        var resultSpan = document.getElementById('previewResult');
        resultSpan.textContent = t('calcpoint.msg.calculating');
        resultSpan.className = 'fw-semibold text-muted';

        fetch('/CalcPoint/Preview', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ Formula: data.formula, InputMappings: data.inputMappings })
        })
            .then(function (res) { return res.json(); })
            .then(function (result) {
                if (result.success) {
                    var unit = document.getElementById('inputUnit').value.trim();
                    resultSpan.textContent = '→ ' + result.result.toFixed(2) + (unit ? ' ' + unit : '');
                    resultSpan.className = 'fw-semibold text-success';
                } else {
                    resultSpan.textContent = '';
                    var failIcon = document.createElement('i');
                    failIcon.className = 'fas fa-times-circle me-1';
                    failIcon.setAttribute('aria-hidden', 'true');
                    resultSpan.appendChild(failIcon);
                    resultSpan.appendChild(document.createTextNode(result.message));
                    resultSpan.className = 'fw-semibold text-danger';
                }
            })
            .catch(function () {
                resultSpan.textContent = t('calcpoint.msg.network_error');
                resultSpan.className = 'fw-semibold text-danger';
            });
    }

    // ══════════════════════════════════════
    //  點位選擇器（Picker）
    // ══════════════════════════════════════

    function openVarPicker(btnEl) {
        _pickerTargetRow = btnEl.closest('tr');
        _pickerSelectedSid = null;
        _pickerDevId = -1;
        _pickerModbusId = null;
        _pickerCalcGroup = null;
        _pickerSourceType = null;
        _pickerExtGroup = null;
        _ensurePickerData(function () {
            _pkShowStep0();
        });
    }

    function _ensurePickerData(cb) {
        if (_pickerDevices && _pickerPoints) { cb(); return; }
        Promise.all([
            fetch('/Designer/Devices').then(function (r) { return r.json(); }),
            fetch('/Designer/Points').then(function (r) { return r.json(); })
        ]).then(function (results) {
            _pickerDevices = results[0];
            _pickerPoints = results[1];
            _enrichPointLabels();
            cb();
        }).catch(function (err) {
            alert(t('calcpoint.pk.load_failed') + '\n' + err.message);
        });
    }

    function _enrichPointLabels() {
        if (!_pickerDevices || !_pickerPoints) return;
        var szCalcDefaultLabel = t('calcpoint.pk.calc_points_label');
        _pickerPoints.forEach(function (p) {
            if (_isCalcPoint(p.szSid)) {
                p._deviceLabel = p.szGroupName || szCalcDefaultLabel;
                return;
            }
            if (_isDbPoint(p.szSid) || _isOpcPoint(p.szSid) || _isDmdPoint(p.szSid)) {
                p._deviceLabel = p.szGroupName || '';
                return;
            }
            var szLabel = '';
            for (var i = 0; i < _pickerDevices.length; i++) {
                var d = _pickerDevices[i];
                if (!_isPointOfDevice(p.szSid, d.nId)) continue;
                szLabel = window.PointGrouping.pointDeviceLabel(p.szSid, p.szDeviceGroup, d);
                break;
            }
            p._deviceLabel = szLabel;
        });
    }

    // 以下 helper 委派共用層 window.PointGrouping（分群解析單一真相）
    function _isCalcPoint(sid) { return window.PointGrouping.isCalcSid(sid); }
    function _isDbPoint(sid) { return window.PointGrouping.isDbSid(sid); }
    function _isOpcPoint(sid) { return window.PointGrouping.isOpcSid(sid); }
    function _isDmdPoint(sid) { return window.PointGrouping.isDmdSid(sid); }
    function _getSidPrefix(sid) { return window.PointGrouping.getSidPrefix(sid); }
    function _isPointOfDevice(sid, nDevId) {
        if (nDevId === CALC_DEV_ID) return _isCalcPoint(sid);
        if (_isCalcPoint(sid)) return false;
        var nPfx = _getSidPrefix(sid);
        return nPfx >= nDevId * 65536 && nPfx < (nDevId + 1) * 65536;
    }

    // ── Picker 步驟導航 ──

    function _pkShowStep0() {
        document.getElementById('cpPkStep0').style.display = '';
        document.getElementById('cpPkStep1').style.display = 'none';
        document.getElementById('cpPkStep2').style.display = 'none';
        document.getElementById('cpPkTitle').textContent = t('calcpoint.pk.title_source');
        document.getElementById('cpPkBtnConfirm').disabled = true;

        if (!_pickerModal) {
            _pickerModal = new bootstrap.Modal(document.getElementById('cpPointPickerModal'));
        }
        _pickerModal.show();
    }

    function _pkShowDeviceStep() {
        _pickerSourceType = 'modbus';
        _pickerModbusId = null;
        _pickerDeviceGroup = null;
        document.getElementById('cpPkStep0').style.display = 'none';
        document.getElementById('cpPkStep1').style.display = '';
        document.getElementById('cpPkStep2').style.display = 'none';
        document.getElementById('cpPkTitle').textContent = t('calcpoint.pk.title_select_device');
        _pkRenderDeviceList();
    }

    // ── DB / OPC UA / 需量 來源（依群組兩層瀏覽；需量僅一組「需量」群）──

    function _pkIsExtMatch(sid) {
        if (_pickerSourceType === 'dmd') return _isDmdPoint(sid);
        return _pickerSourceType === 'db' ? _isDbPoint(sid) : _isOpcPoint(sid);
    }

    function pkShowExtStep(type) {
        _pickerSourceType = type;
        _pickerDevId = -1;
        _pickerModbusId = null;
        _pickerCalcGroup = null;
        _pickerExtGroup = null;
        _pickerSelectedSid = null;

        var groups = {};
        (_pickerPoints || []).forEach(function (p) {
            if (!_pkIsExtMatch(p.szSid)) return;
            var g = p.szGroupName || '';
            if (!groups[g]) groups[g] = 0;
            groups[g]++;
        });
        var keys = Object.keys(groups).sort();

        document.getElementById('cpPkStep0').style.display = 'none';
        document.getElementById('cpPkStep1').style.display = '';
        document.getElementById('cpPkStep2').style.display = 'none';
        document.getElementById('cpPkTitle').textContent = t('calcpoint.pk.title_select_device');

        var szIcon = type === 'db' ? 'fa-database' : (type === 'dmd' ? 'fa-bolt' : 'fa-network-wired');
        var szColor = type === 'db' ? '#198754' : (type === 'dmd' ? '#dc3545' : '#6f42c1');
        var container = document.getElementById('cpPkDeviceList');
        if (keys.length === 0) {
            container.innerHTML = '<div class="text-center text-muted py-4"><i class="fas fa-inbox fa-2x d-block mb-2"></i>' + escapeHtml(t('calcpoint.pk.no_points')) + '</div>';
            return;
        }
        var html = '';
        for (var i = 0; i < keys.length; i++) {
            var g = keys[i];
            html += '<div class="cpPk-list-item" onclick="window._calcPoint.pkSelectExtGroup(\'' + escapeHtml(g) + '\')">' +
                '<i class="fas ' + szIcon + '" style="color:' + szColor + ';flex-shrink:0;"></i>' +
                '<div style="flex:1;min-width:0;">' +
                '<div class="cpPk-item-name">' + escapeHtml(g || t('calcpoint.pk.ungrouped')) + '</div>' +
                '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: groups[g] })) + '</div>' +
                '</div>' +
                '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
        }
        container.innerHTML = html;
    }

    function pkSelectExtGroup(g) {
        _pickerExtGroup = g;
        _pickerSelectedSid = null;
        document.getElementById('cpPkDevName').textContent = g || t('calcpoint.pk.ungrouped');
        document.getElementById('cpPkDevIcon').className =
            'fas ' + (_pickerSourceType === 'db' ? 'fa-database' : (_pickerSourceType === 'dmd' ? 'fa-bolt' : 'fa-network-wired')) + ' me-1';
        _pkShowPointList(t('calcpoint.pk.title_select_point'));
    }

    function _pkShowCalcStep() {
        _pickerSourceType = 'calc';
        _pickerDevId = CALC_DEV_ID;
        _pickerModbusId = null;
        _pickerSelectedSid = null;
        _pickerCalcGroup = null;

        var groups = _pkGetCalcGroups();
        if (groups.length > 0) {
            document.getElementById('cpPkStep0').style.display = 'none';
            document.getElementById('cpPkStep1').style.display = '';
            document.getElementById('cpPkStep2').style.display = 'none';
            document.getElementById('cpPkTitle').textContent = t('calcpoint.pk.title_select_calc_group');
            _pkRenderCalcGroupList(groups);
        } else {
            _pkShowCalcFlat();
        }
    }

    function _pkGetCalcGroups() {
        if (!_pickerPoints) return [];
        var groups = {};
        _pickerPoints.forEach(function (p) {
            if (!_isCalcPoint(p.szSid)) return;
            var g = p.szGroupName || '';
            if (!groups[g]) groups[g] = 0;
            groups[g]++;
        });
        return Object.keys(groups).filter(function (g) { return g !== ''; }).sort();
    }

    function _pkRenderCalcGroupList(groups) {
        var container = document.getElementById('cpPkDeviceList');
        var hasUngrouped = (_pickerPoints || []).some(function (p) { return _isCalcPoint(p.szSid) && !p.szGroupName; });

        var html = '';
        for (var i = 0; i < groups.length; i++) {
            var g = groups[i];
            var nPts = (_pickerPoints || []).filter(function (p) { return _isCalcPoint(p.szSid) && p.szGroupName === g; }).length;
            html += '<div class="cpPk-list-item" onclick="window._calcPoint.pkSelectCalcGroup(\'' + escapeHtml(g) + '\')">' +
                '<i class="fas fa-layer-group" style="color:#ffc107;flex-shrink:0;"></i>' +
                '<div style="flex:1;min-width:0;">' +
                '<div class="cpPk-item-name">' + escapeHtml(g) + '</div>' +
                '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nPts })) + '</div>' +
                '</div>' +
                '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
        }
        if (hasUngrouped) {
            var nU = (_pickerPoints || []).filter(function (p) { return _isCalcPoint(p.szSid) && !p.szGroupName; }).length;
            html += '<div class="cpPk-list-item" onclick="window._calcPoint.pkSelectCalcGroup(\'\')">' +
                '<i class="fas fa-inbox" style="color:#6c757d;flex-shrink:0;"></i>' +
                '<div style="flex:1;min-width:0;">' +
                '<div class="cpPk-item-name">' + escapeHtml(t('calcpoint.pk.ungrouped')) + '</div>' +
                '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nU })) + '</div>' +
                '</div>' +
                '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
        }
        container.innerHTML = html;
    }

    function pkSelectCalcGroup(g) {
        _pickerCalcGroup = g;
        _pickerSelectedSid = null;
        document.getElementById('cpPkDevName').textContent = g || t('calcpoint.pk.ungrouped');
        document.getElementById('cpPkDevIcon').className = 'fas fa-layer-group me-1';
        _pkShowPointList(t('calcpoint.pk.title_select_calc_point'));
    }

    function _pkShowCalcFlat() {
        _pickerCalcGroup = null;
        document.getElementById('cpPkDevName').textContent = t('calcpoint.pk.calc_points_label');
        document.getElementById('cpPkDevIcon').className = 'fas fa-calculator me-1';
        _pkShowPointList(t('calcpoint.pk.title_select_calc_point'));
    }

    // ── 設備清單 ──

    function _pkRenderDeviceList() {
        var container = document.getElementById('cpPkDeviceList');
        if (!_pickerDevices || _pickerDevices.length === 0) {
            container.innerHTML = '<div class="text-center text-muted py-4"><i class="fas fa-plug fa-2x d-block mb-2"></i>' + escapeHtml(t('calcpoint.pk.no_devices')) + '</div>';
            return;
        }

        var html = '';
        for (var i = 0; i < _pickerDevices.length; i++) {
            var d = _pickerDevices[i];
            var nPts = (_pickerPoints || []).filter(function (p) { return _isPointOfDevice(p.szSid, d.nId); }).length;
            var coord = window.PointGrouping.parseCoord(d);
            var modbusIds = coord.modbusIds;
            var deviceNames = coord.deviceNames;

            if (modbusIds.length > 1) {
                var subHtml = '';
                for (var j = 0; j < modbusIds.length; j++) {
                    var mid = modbusIds[j];
                    var subName = j < deviceNames.length ? deviceNames[j] : '';
                    var label = subName || mid;
                    subHtml += '<div class="cpPk-list-item cpPk-sub-item" onclick="window._calcPoint.pkSelectDevice(' + d.nId + ',\'' + escapeHtml(label) + '\',' + mid + ')">' +
                        '<i class="fas fa-microchip" style="color:#0d6efd;flex-shrink:0;font-size:12px;"></i>' +
                        '<div style="flex:1;min-width:0;"><div class="cpPk-item-name">' + escapeHtml(label) + '</div></div>' +
                        '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
                }
                html += '<div class="cpPk-list-item" onclick="window._calcPoint.pkToggleSub(this)" style="cursor:pointer;">' +
                    '<i class="fas fa-server" style="color:#0d6efd;flex-shrink:0;"></i>' +
                    '<div style="flex:1;min-width:0;">' +
                    '<div class="cpPk-item-name">' + escapeHtml(d.szName) + '</div>' +
                    '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nPts })) + '</div>' +
                    '</div>' +
                    '<i class="fas fa-chevron-down cpPk-toggle-icon" style="color:#999;font-size:11px;transition:transform .2s;"></i></div>' +
                    '<div class="cpPk-sub-menu" style="display:none;">' + subHtml + '</div>';
            } else if (window.PointGrouping.hasDeviceGroups(d, _pickerPoints)) {
                // 單站號 + 有 Device 分群：可展開 Device 子選單（+「未分群」桶，決策 4/5）
                var dg = window.PointGrouping.coordDeviceGroups(d, _pickerPoints);
                var gSub = '';
                for (var gi = 0; gi < dg.names.length; gi++) {
                    var gName = dg.names[gi];
                    var nG = dg.countByGroup[gName] || 0;
                    gSub += '<div class="cpPk-list-item cpPk-sub-item" onclick="window._calcPoint.pkSelectDeviceGroup(' + d.nId + ',\'' + escapeHtml(gName) + '\')">' +
                        '<i class="fas fa-microchip" style="color:#0d6efd;flex-shrink:0;font-size:12px;"></i>' +
                        '<div style="flex:1;min-width:0;"><div class="cpPk-item-name">' + escapeHtml(gName) + '</div>' +
                        '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nG })) + '</div></div>' +
                        '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
                }
                if (dg.hasUngrouped) {
                    var nU = (_pickerPoints || []).filter(function (pp) { return _isPointOfDevice(pp.szSid, d.nId) && !window.PointGrouping.getDeviceGroup(pp); }).length;
                    gSub += '<div class="cpPk-list-item cpPk-sub-item" onclick="window._calcPoint.pkSelectDeviceGroup(' + d.nId + ',\'\')">' +
                        '<i class="fas fa-inbox" style="color:#6c757d;flex-shrink:0;font-size:12px;"></i>' +
                        '<div style="flex:1;min-width:0;"><div class="cpPk-item-name">' + escapeHtml(t('calcpoint.pk.ungrouped')) + '</div>' +
                        '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nU })) + '</div></div>' +
                        '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
                }
                html += '<div class="cpPk-list-item" onclick="window._calcPoint.pkToggleSub(this)" style="cursor:pointer;">' +
                    '<i class="fas fa-server" style="color:#0d6efd;flex-shrink:0;"></i>' +
                    '<div style="flex:1;min-width:0;">' +
                    '<div class="cpPk-item-name">' + escapeHtml(d.szName) + '</div>' +
                    '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nPts })) + '</div>' +
                    '</div>' +
                    '<i class="fas fa-chevron-down cpPk-toggle-icon" style="color:#999;font-size:11px;transition:transform .2s;"></i></div>' +
                    '<div class="cpPk-sub-menu" style="display:none;">' + gSub + '</div>';
            } else {
                html += '<div class="cpPk-list-item" onclick="window._calcPoint.pkSelectDevice(' + d.nId + ',\'' + escapeHtml(d.szName) + '\')">' +
                    '<i class="fas fa-server" style="color:#0d6efd;flex-shrink:0;"></i>' +
                    '<div style="flex:1;min-width:0;">' +
                    '<div class="cpPk-item-name">' + escapeHtml(d.szName) + '</div>' +
                    '<div class="cpPk-item-sub">' + escapeHtml(t('calcpoint.pk.points_count', { n: nPts })) + '</div>' +
                    '</div>' +
                    '<i class="fas fa-chevron-right" style="color:#999;font-size:11px;"></i></div>';
            }
        }
        container.innerHTML = html;
    }

    function pkToggleSub(el) {
        var sub = el.nextElementSibling;
        var icon = el.querySelector('.cpPk-toggle-icon');
        if (sub.style.display === 'none') {
            sub.style.display = '';
            icon.style.transform = 'rotate(180deg)';
        } else {
            sub.style.display = 'none';
            icon.style.transform = '';
        }
    }

    function pkSelectDevice(nDevId, szLabel, nModbusId) {
        _pickerDevId = nDevId;
        _pickerModbusId = nModbusId != null ? nModbusId : null;
        _pickerDeviceGroup = null;
        _pickerSelectedSid = null;
        document.getElementById('cpPkDevName').textContent = szLabel || String(nDevId);
        document.getElementById('cpPkDevIcon').className = 'fas fa-server me-1';
        _pkShowPointList(t('calcpoint.pk.title_select_point'));
    }

    // 選取單站號 Coordinator 內某 Device 子設備（szGroup=''＝未分群桶）
    function pkSelectDeviceGroup(nDevId, szGroup) {
        _pickerDevId = nDevId;
        _pickerModbusId = null;
        _pickerDeviceGroup = szGroup;
        _pickerSelectedSid = null;
        document.getElementById('cpPkDevName').textContent = szGroup || t('calcpoint.pk.ungrouped');
        document.getElementById('cpPkDevIcon').className = szGroup ? 'fas fa-microchip me-1' : 'fas fa-inbox me-1';
        _pkShowPointList(t('calcpoint.pk.title_select_point'));
    }

    // ── 點位清單 ──

    function _pkShowPointList(title) {
        document.getElementById('cpPkStep0').style.display = 'none';
        document.getElementById('cpPkStep1').style.display = 'none';
        document.getElementById('cpPkStep2').style.display = '';
        document.getElementById('cpPkTitle').textContent = title;
        document.getElementById('cpPkSearch').value = '';
        document.getElementById('cpPkBtnConfirm').disabled = true;
        _pkRenderPoints('');
    }

    function _pkRenderPoints(keyword) {
        var szQ = keyword.trim().toLowerCase();
        var filtered = (_pickerPoints || []).filter(function (p) {
            if (_pickerSourceType === 'db' || _pickerSourceType === 'opc' || _pickerSourceType === 'dmd') {
                if (!_pkIsExtMatch(p.szSid)) return false;
                if (_pickerExtGroup != null && (p.szGroupName || '') !== _pickerExtGroup) return false;
            } else if (_pickerDevId === CALC_DEV_ID) {
                if (!_isCalcPoint(p.szSid)) return false;
                if (_pickerCalcGroup != null) {
                    if ((p.szGroupName || '') !== _pickerCalcGroup) return false;
                }
            } else if (_pickerModbusId != null) {
                var nPfx = _getSidPrefix(p.szSid);
                var base = _pickerDevId * 65536 + _pickerModbusId * 256;
                if (nPfx < base || nPfx >= base + 256) return false;
            } else if (_pickerDeviceGroup != null) {
                if (!_isPointOfDevice(p.szSid, _pickerDevId)) return false;
                if (window.PointGrouping.getDeviceGroup(p) !== _pickerDeviceGroup) return false;
            } else {
                if (!_isPointOfDevice(p.szSid, _pickerDevId)) return false;
            }
            return !szQ || p.szName.toLowerCase().indexOf(szQ) >= 0;
        });

        var container = document.getElementById('cpPkPointList');
        if (filtered.length === 0) {
            container.innerHTML = '<div class="text-center text-muted py-4"><i class="fas fa-inbox fa-2x d-block mb-2"></i>' + escapeHtml(t('calcpoint.pk.no_points')) + '</div>';
            return;
        }

        var html = '';
        for (var i = 0; i < filtered.length; i++) {
            var p = filtered[i];
            var prefix = p._deviceLabel ? '<span style="color:#0d6efd;font-size:11px;">' + escapeHtml(p._deviceLabel) + '</span><span style="color:#aaa;margin:0 4px;">/</span>' : '';
            html += '<div class="cpPk-list-item" data-sid="' + escapeHtml(p.szSid) + '" onclick="window._calcPoint.pkSelectPoint(this,\'' + escapeHtml(p.szSid) + '\')">' +
                '<i class="fas fa-circle" style="font-size:6px;color:#198754;flex-shrink:0;"></i>' +
                '<div style="flex:1;min-width:0;">' +
                '<div class="cpPk-item-name">' + prefix + escapeHtml(p.szName) + '</div>' +
                '</div>' +
                '<span class="cpPk-item-unit">' + escapeHtml(p.szUnit || '') + '</span></div>';
        }
        container.innerHTML = html;
    }

    function pkSelectPoint(el, sid) {
        var items = document.querySelectorAll('#cpPkPointList .cpPk-list-item');
        for (var i = 0; i < items.length; i++) items[i].classList.remove('selected');
        el.classList.add('selected');
        _pickerSelectedSid = sid;
        document.getElementById('cpPkBtnConfirm').disabled = false;
    }

    function pkFilterPoints(keyword) {
        _pkRenderPoints(keyword);
        _pickerSelectedSid = null;
        document.getElementById('cpPkBtnConfirm').disabled = true;
    }

    // ── Picker 確認 / 返回 ──

    function pkConfirm() {
        if (!_pickerSelectedSid || !_pickerTargetRow) return;
        var point = null;
        for (var i = 0; i < _pickerPoints.length; i++) {
            if (_pickerPoints[i].szSid === _pickerSelectedSid) { point = _pickerPoints[i]; break; }
        }
        if (!point) return;

        var szFullName = point._deviceLabel ? point._deviceLabel + ' / ' + point.szName : point.szName;

        // 更新目標列
        var hiddenInput = _pickerTargetRow.querySelector('.var-sid');
        var labelSpan = _pickerTargetRow.querySelector('.var-point-label');
        hiddenInput.value = point.szSid;
        labelSpan.textContent = szFullName;
        labelSpan.classList.remove('text-muted');

        _pickerModal.hide();
        _pickerTargetRow = null;
    }

    function pkGoBack() {
        if (_pickerSourceType === 'db' || _pickerSourceType === 'opc' || _pickerSourceType === 'dmd') {
            pkShowExtStep(_pickerSourceType);
            return;
        }
        if (_pickerDevId === CALC_DEV_ID) {
            if (_pickerCalcGroup != null) {
                _pickerCalcGroup = null;
                _pkShowCalcStep();
            } else {
                _pkShowStep0();
            }
        } else {
            _pkShowDeviceStep();
        }
    }

    function pkBackToStep0() {
        _pkShowStep0();
    }

    // ── 工具函式 ──

    function escapeHtml(str) {
        if (!str) return '';
        return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    // ── 公開 API ──
    window._calcPoint = {
        openCreateModal: openCreateModal,
        openEditModal: openEditModal,
        openDeleteModal: openDeleteModal,
        confirmDelete: confirmDelete,
        saveCalcPoint: saveCalcPoint,
        addVariableRow: function () { addVariableRow('', ''); },
        previewFormula: previewFormula,
        applyTemplate: applyTemplate,
        openVarPicker: openVarPicker,
        pkShowDeviceStep: _pkShowDeviceStep,
        pkShowCalcStep: _pkShowCalcStep,
        pkShowExtStep: pkShowExtStep,
        pkSelectExtGroup: pkSelectExtGroup,
        pkSelectDevice: pkSelectDevice,
        pkSelectDeviceGroup: pkSelectDeviceGroup,
        pkSelectCalcGroup: pkSelectCalcGroup,
        pkSelectPoint: pkSelectPoint,
        pkFilterPoints: pkFilterPoints,
        pkConfirm: pkConfirm,
        pkGoBack: pkGoBack,
        pkBackToStep0: pkBackToStep0,
        pkToggleSub: pkToggleSub
    };

    // DOM Ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
