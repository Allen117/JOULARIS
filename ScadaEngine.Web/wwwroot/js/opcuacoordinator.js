(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var servers = window._opcuaData || [];
    var selectedIdx = -1;          // -1 = 未選擇；-2 = 新增模式
    var workingDevices = [];       // 目前編輯中的 devices 深拷貝

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function el(id) { return document.getElementById(id); }

    /* ── 左側 Server 清單 ── */

    function renderSidebar() {
        var list = el('opcuaServerList');
        if (!list) return;

        if (!servers.length) {
            list.innerHTML = '<div class="list-group-item text-muted small py-3 text-center">' +
                escapeHtml(t('opcuacoord.empty.no_server')) + '</div>';
            return;
        }

        var html = '';
        servers.forEach(function (s, i) {
            var nPoints = 0;
            (s.devices || []).forEach(function (d) { nPoints += (d.tags || []).length; });
            html += '<a href="#" class="list-group-item list-group-item-action py-2 opcua-server-item' +
                (i === selectedIdx ? ' active' : '') + '" data-index="' + i + '">' +
                '<i class="fas fa-server me-1"></i><span class="small">' + escapeHtml(s.name) + '</span>' +
                '<span class="badge bg-secondary float-end">' + nPoints + '</span>' +
                (s.monitorEnabled ? '' : '<span class="badge bg-warning text-dark float-end me-1">' +
                    escapeHtml(t('opcuacoord.value.disabled')) + '</span>') +
                '</a>';
        });
        list.innerHTML = html;

        list.querySelectorAll('.opcua-server-item').forEach(function (item) {
            item.addEventListener('click', function (e) {
                e.preventDefault();
                selectServer(parseInt(item.getAttribute('data-index'), 10));
            });
        });
    }

    /* ── 右側連線設定表單 ── */

    function selectServer(idx) {
        var s = servers[idx];
        if (!s) return;
        selectedIdx = idx;

        el('emptyDetail').style.display = 'none';
        el('fullDetail').style.display = '';
        el('pointsCard').style.display = '';
        el('btnDeleteServer').style.display = '';

        el('fieldName').value = s.name;
        el('fieldName').readOnly = true;   // 名稱 = 檔名 = UPSERT key，不可改
        el('fieldEndpointUrl').value = s.endpointUrl;
        el('fieldUsername').value = s.username || '';
        el('fieldPassword').value = '';
        el('passwordHint').textContent = s.hasPassword
            ? t('opcuacoord.placeholder.password_keep') : '';
        el('fieldPollingInterval').value = s.pollingInterval;
        el('fieldConnectTimeout').value = s.connectTimeout;
        el('fieldMonitorEnabled').checked = !!s.monitorEnabled;

        workingDevices = JSON.parse(JSON.stringify(s.devices || []));
        renderDevices();
        renderSidebar();
    }

    function newServer() {
        selectedIdx = -2;

        el('emptyDetail').style.display = 'none';
        el('fullDetail').style.display = '';
        el('pointsCard').style.display = 'none';   // 先建 Server 拿到 Id 再編點位
        el('btnDeleteServer').style.display = 'none';

        el('fieldName').value = '';
        el('fieldName').readOnly = false;
        el('fieldEndpointUrl').value = '';
        el('fieldUsername').value = '';
        el('fieldPassword').value = '';
        el('passwordHint').textContent = '';
        el('fieldPollingInterval').value = 1000;
        el('fieldConnectTimeout').value = 5000;
        el('fieldMonitorEnabled').checked = true;

        workingDevices = [];
        renderSidebar();
    }

    function saveServer() {
        var payload = {
            id: selectedIdx >= 0 ? servers[selectedIdx].id : 0,
            name: el('fieldName').value.trim(),
            endpointUrl: el('fieldEndpointUrl').value.trim(),
            username: el('fieldUsername').value.trim(),
            password: el('fieldPassword').value,
            pollingInterval: parseInt(el('fieldPollingInterval').value, 10) || 0,
            connectTimeout: parseInt(el('fieldConnectTimeout').value, 10) || 0,
            monitorEnabled: el('fieldMonitorEnabled').checked
        };

        postJson('/OpcUaCoordinator/SaveServer', payload, el('btnSaveServer'), function (data) {
            alert(successMessage(data));
            window.location.reload();
        });
    }

    function deleteServer() {
        if (selectedIdx < 0) return;
        var s = servers[selectedIdx];
        if (!confirm(t('opcuacoord.confirm.delete_server', { name: s.name }))) return;

        postJson('/OpcUaCoordinator/DeleteServer', { id: s.id }, el('btnDeleteServer'), function (data) {
            alert(successMessage(data));
            window.location.reload();
        });
    }

    /** 存檔成功訊息；來源資料夾內的 Excel 未能自動重產（Excel 開著 / 有未匯入修改）時附上提示 */
    function successMessage(data) {
        var szMsg = data.message || t('opcuacoord.msg.success_default');
        return data.workbookNotice ? szMsg + '\n\n' + data.workbookNotice : szMsg;
    }

    /* ── 點位編輯（Device 卡片 + 點位表格） ── */

    function renderDevices() {
        var container = el('deviceContainer');
        if (!container) return;

        if (!workingDevices.length) {
            container.innerHTML = '<div class="text-muted small text-center py-3">' +
                escapeHtml(t('opcuacoord.empty.no_device')) + '</div>';
            return;
        }

        var html = '';
        workingDevices.forEach(function (d, di) {
            html += '<div class="opcua-device-card" data-device="' + di + '">' +
                '<div class="device-header">' +
                '<i class="fas fa-microchip text-secondary"></i>' +
                '<label class="small text-muted mb-0">' + escapeHtml(t('opcuacoord.device.name')) + '</label>' +
                '<input type="text" class="form-control form-control-sm device-name" value="' + escapeHtml(d.name) + '" />' +
                '<button class="btn btn-outline-primary btn-sm ms-auto" onclick="window._opcua.addTag(' + di + ')">' +
                '<i class="fas fa-plus me-1"></i>' + escapeHtml(t('opcuacoord.btn.add_point')) + '</button>' +
                '<button class="btn btn-outline-danger btn-sm" onclick="window._opcua.removeDevice(' + di + ')">' +
                '<i class="fas fa-trash"></i></button>' +
                '</div>' +
                '<div class="table-responsive"><table class="table table-sm opcua-tag-table">' +
                '<thead><tr>' +
                '<th class="col-seq">SID</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.name')) + '</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.tagname')) + '</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.controltype')) + '</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.ratio')) + '</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.unit')) + '</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.min')) + '</th>' +
                '<th>' + escapeHtml(t('opcuacoord.th.max')) + '</th>' +
                '<th class="col-actions"></th>' +
                '</tr></thead><tbody>';

            (d.tags || []).forEach(function (tag, ti) {
                var szSid = tag.seq > 0 && selectedIdx >= 0
                    ? 'OPC' + servers[selectedIdx].id + '-S' + tag.seq
                    : t('opcuacoord.value.new_point');
                html += '<tr data-tag="' + ti + '" data-seq="' + (tag.seq || 0) + '">' +
                    '<td class="col-seq">' + escapeHtml(szSid) + '</td>' +
                    '<td><input type="text" class="form-control form-control-sm tag-name" value="' + escapeHtml(tag.name) + '" /></td>' +
                    '<td><input type="text" class="form-control form-control-sm tag-nodeid" placeholder="ns=2;s=D1.T" value="' + escapeHtml(tag.tagName) + '" /></td>' +
                    '<td><select class="form-select form-select-sm tag-controltype">' +
                    '<option value=""' + (!tag.controlType ? ' selected' : '') + '>' + escapeHtml(t('opcuacoord.value.readonly')) + '</option>' +
                    '<option value="AO"' + (tag.controlType === 'AO' ? ' selected' : '') + '>AO</option>' +
                    '<option value="DO"' + (tag.controlType === 'DO' ? ' selected' : '') + '>DO</option>' +
                    '</select></td>' +
                    '<td><input type="number" step="any" class="form-control form-control-sm tag-ratio" value="' + escapeHtml(tag.ratio != null ? tag.ratio : 1) + '" /></td>' +
                    '<td><input type="text" class="form-control form-control-sm tag-unit" value="' + escapeHtml(tag.unit || '') + '" /></td>' +
                    '<td><input type="number" step="any" class="form-control form-control-sm tag-min" value="' + (tag.min != null ? escapeHtml(tag.min) : '') + '" /></td>' +
                    '<td><input type="number" step="any" class="form-control form-control-sm tag-max" value="' + (tag.max != null ? escapeHtml(tag.max) : '') + '" /></td>' +
                    '<td class="col-actions">' +
                    '<button class="btn btn-outline-secondary btn-sm me-1" title="' + escapeHtml(t('opcuacoord.btn.test')) + '" onclick="window._opcua.testRead(' + di + ',' + ti + ')">' +
                    '<i class="fas fa-vial"></i></button>' +
                    '<button class="btn btn-outline-danger btn-sm" onclick="window._opcua.removeTag(' + di + ',' + ti + ')">' +
                    '<i class="fas fa-times"></i></button>' +
                    '</td></tr>';
            });

            html += '</tbody></table></div></div>';
        });
        container.innerHTML = html;
    }

    /// 從 DOM 收集目前編輯值回 workingDevices（結構操作前呼叫，避免輸入遺失）
    function collectDevicesFromDom() {
        var container = el('deviceContainer');
        if (!container) return;

        var collected = [];
        container.querySelectorAll('.opcua-device-card').forEach(function (card) {
            var device = {
                name: (card.querySelector('.device-name') || {}).value || '',
                tags: []
            };
            card.querySelectorAll('tbody tr').forEach(function (tr) {
                device.tags.push({
                    seq: parseInt(tr.getAttribute('data-seq'), 10) || 0,
                    name: tr.querySelector('.tag-name').value,
                    tagName: tr.querySelector('.tag-nodeid').value,
                    controlType: tr.querySelector('.tag-controltype').value,
                    ratio: parseFloat(tr.querySelector('.tag-ratio').value) || 0,
                    unit: tr.querySelector('.tag-unit').value,
                    min: tr.querySelector('.tag-min').value === '' ? null : parseFloat(tr.querySelector('.tag-min').value),
                    max: tr.querySelector('.tag-max').value === '' ? null : parseFloat(tr.querySelector('.tag-max').value)
                });
            });
            collected.push(device);
        });
        workingDevices = collected;
    }

    function addDevice() {
        if (selectedIdx < 0) return;
        collectDevicesFromDom();
        workingDevices.push({ name: 'D' + (workingDevices.length + 1), tags: [] });
        renderDevices();
    }

    function removeDevice(di) {
        collectDevicesFromDom();
        var d = workingDevices[di];
        if (!d) return;
        if ((d.tags || []).length &&
            !confirm(t('opcuacoord.confirm.delete_device', { name: d.name }))) return;
        workingDevices.splice(di, 1);
        renderDevices();
    }

    function addTag(di) {
        collectDevicesFromDom();
        if (!workingDevices[di]) return;
        workingDevices[di].tags.push({
            seq: 0, name: '', tagName: '', controlType: '', ratio: 1, unit: '', min: null, max: null
        });
        renderDevices();
    }

    function removeTag(di, ti) {
        collectDevicesFromDom();
        if (!workingDevices[di] || !workingDevices[di].tags[ti]) return;
        workingDevices[di].tags.splice(ti, 1);
        renderDevices();
    }

    function savePoints() {
        if (selectedIdx < 0) return;
        collectDevicesFromDom();

        var payload = {
            id: servers[selectedIdx].id,
            devices: workingDevices.map(function (d) {
                return {
                    name: d.name,
                    tags: d.tags.map(function (tag) {
                        return {
                            seq: tag.seq || 0,
                            name: tag.name,
                            tagName: tag.tagName,
                            controlType: tag.controlType,
                            ratio: tag.ratio,
                            unit: tag.unit,
                            min: tag.min,
                            max: tag.max
                        };
                    })
                };
            })
        };

        postJson('/OpcUaCoordinator/SavePoints', payload, el('btnSavePoints'), function (data) {
            alert(successMessage(data));
            window.location.reload();
        });
    }

    function testRead(di, ti) {
        collectDevicesFromDom();
        var tag = (workingDevices[di] || { tags: [] }).tags[ti];
        if (!tag) return;

        var payload = {
            serverId: selectedIdx >= 0 ? servers[selectedIdx].id : 0,
            endpointUrl: el('fieldEndpointUrl').value.trim(),
            username: el('fieldUsername').value.trim(),
            password: el('fieldPassword').value,
            nodeId: tag.tagName,
            ratio: tag.ratio || 1
        };

        postJson('/OpcUaCoordinator/TestRead', payload, null, function (data) {
            alert(data.message || '');
        });
    }

    /* ── 共用 ── */

    function postJson(url, payload, btn, onSuccess) {
        var szOriginalHtml = btn ? btn.innerHTML : '';
        if (btn) {
            btn.disabled = true;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
        }
        fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        })
            .then(function (r) { return r.json().then(function (data) { return { ok: r.ok, data: data }; }); })
            .then(function (result) {
                if (result.ok && result.data.success) {
                    onSuccess(result.data);
                } else {
                    alert(result.data.message || t('opcuacoord.msg.failure_default'));
                }
            })
            .catch(function (err) {
                alert(t('opcuacoord.msg.call_failed', { error: err.message }));
            })
            .finally(function () {
                if (btn) { btn.disabled = false; btn.innerHTML = szOriginalHtml; }
            });
    }

    window._opcua = {
        newServer: newServer,
        saveServer: saveServer,
        deleteServer: deleteServer,
        addDevice: addDevice,
        removeDevice: removeDevice,
        addTag: addTag,
        removeTag: removeTag,
        savePoints: savePoints,
        testRead: testRead
    };

    function init() {
        renderSidebar();
        if (servers.length > 0) selectServer(0);
    }

    if (window.i18n && window.i18n.ready) {
        window.i18n.ready(function () {
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
            else init();
        });
    } else {
        document.addEventListener('DOMContentLoaded', init);
    }
})();
