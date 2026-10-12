(function () {
    'use strict';

    function t(key, args) { return (window.i18n && window.i18n.t) ? window.i18n.t(key, args) : key; }

    var coordinators = window._dbCoordData || [];
    var nCurrentIndex = -1;

    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function clearAllActive() {
        document.querySelectorAll('.coordinator-item')
            .forEach(function (el) { el.classList.remove('active'); });
    }

    function showDetail(index) {
        var c = coordinators[index];
        if (!c) return;
        nCurrentIndex = index;

        document.getElementById('emptyDetail').style.display = 'none';
        document.getElementById('fullDetail').style.display = '';
        document.getElementById('detailTitle').innerHTML =
            '<i class="fas fa-info-circle me-1"></i>' + t('dbcoord.card.device_detail') + ' — ' + escapeHtml(c.name || '');

        document.getElementById('fieldId').value              = c.id;
        document.getElementById('fieldName').value            = c.name || '';
        document.getElementById('fieldPollingInterval').value = c.pollingInterval;
        document.getElementById('fieldConnectTimeout').value  = c.connectTimeout;
        document.getElementById('fieldMonitorEnabled').value  = c.monitorEnabled ? t('dbcoord.value.yes') : t('dbcoord.value.no');
        document.getElementById('fieldPointCount').value      = (c.points || []).length;

        // 設定檔已移除 / 待 Engine 載入 標示 + 逐台刪除按鈕（Excel 匯入功能，共用 source-excel-import.js）
        var note = document.getElementById('configMissingNote');
        if (note) note.style.display = c.configMissing ? '' : 'none';
        var pendingNote = document.getElementById('pendingEngineNote');
        if (pendingNote) pendingNote.style.display = c.pendingEngine ? '' : 'none';
        var btnDel = document.getElementById('btnDeleteSource');
        if (btnDel) btnDel.style.display = c.configMissing ? 'none' : '';

        // 待 Engine 載入：JSON 在、DB 沒有 → 沒有 Id / 點位可顯示，只留提示與刪除按鈕
        if (c.pendingEngine) {
            document.getElementById('fieldId').value = '';
            document.getElementById('fieldPollingInterval').value = '';
            document.getElementById('fieldConnectTimeout').value = '';
            document.getElementById('fieldMonitorEnabled').value = '';
            document.getElementById('fieldPointCount').value = '';
        }

        renderPointTable(c);
    }

    var btnDeleteSource = document.getElementById('btnDeleteSource');
    if (btnDeleteSource) {
        btnDeleteSource.addEventListener('click', function () {
            var c = coordinators[nCurrentIndex];
            if (!c || !window.SourceExcelImport) return;
            window.SourceExcelImport.deleteSource(c.name);
        });
    }

    // ── 點位列表 + 行內名稱編輯 ──────────────────────────────

    function renderPointTable(c) {
        var tbody = document.getElementById('pointTableBody');
        if (!tbody) return;

        var points = c.points || [];
        if (!points.length) {
            tbody.innerHTML = '<tr><td colspan="5" class="text-center text-muted py-3">' +
                escapeHtml(t('dbcoord.point.empty')) + '</td></tr>';
            return;
        }

        tbody.innerHTML = points.map(function (p, i) {
            return '<tr data-pindex="' + i + '">' +
                '<td class="text-muted small">' + escapeHtml(p.sid) + '</td>' +
                '<td class="point-name-cell">' + nameCellHtml(p) + '</td>' +
                '<td class="point-unit-cell">' + escapeHtml(p.unit) + '</td>' +
                '<td class="text-end">' + escapeHtml(p.min) + '</td>' +
                '<td class="text-end">' + escapeHtml(p.max) + '</td>' +
            '</tr>';
        }).join('');

        tbody.querySelectorAll('.point-name-cell, .point-unit-cell').forEach(function (cell) {
            cell.addEventListener('dblclick', function () { beginEdit(this.closest('tr')); });
        });
        tbody.querySelectorAll('.btn-point-edit').forEach(function (btn) {
            btn.addEventListener('click', function () { beginEdit(this.closest('tr')); });
        });
    }

    function nameCellHtml(p) {
        return '<span class="point-name-text">' + escapeHtml(p.name) + '</span>' +
            '<button type="button" class="btn btn-link btn-sm p-0 ms-2 btn-point-edit" title="' +
                escapeHtml(t('dbcoord.point.edit')) + '">' +
                '<i class="fas fa-pencil-alt small"></i>' +
            '</button>';
    }

    function beginEdit(tr) {
        if (!tr || tr.querySelector('.point-name-input')) return;

        // 一次只編輯一列：先還原其他編輯中列
        document.querySelectorAll('#pointTableBody tr').forEach(function (row) {
            if (row !== tr && row.querySelector('.point-name-input')) cancelEdit(row);
        });

        var c = coordinators[nCurrentIndex];
        if (!c) return;
        var p = (c.points || [])[parseInt(tr.dataset.pindex)];
        if (!p) return;

        var cell = tr.querySelector('.point-name-cell');
        var unitCell = tr.querySelector('.point-unit-cell');
        cell.innerHTML =
            '<div class="d-flex align-items-center gap-1">' +
                '<input type="text" class="form-control form-control-sm point-name-input" maxlength="100" value="' + escapeHtml(p.name) + '" />' +
                '<button type="button" class="btn btn-success btn-sm btn-point-save" title="' + escapeHtml(t('dbcoord.point.save')) + '"><i class="fas fa-check"></i></button>' +
                '<button type="button" class="btn btn-outline-secondary btn-sm btn-point-cancel" title="' + escapeHtml(t('dbcoord.point.cancel')) + '"><i class="fas fa-times"></i></button>' +
            '</div>';
        unitCell.innerHTML =
            '<input type="text" class="form-control form-control-sm point-unit-input" maxlength="50" value="' + escapeHtml(p.unit) + '" />';

        var keyHandler = function (e) {
            if (e.key === 'Enter') { e.preventDefault(); savePoint(tr); }
            else if (e.key === 'Escape') { cancelEdit(tr); }
        };
        var input = cell.querySelector('.point-name-input');
        input.focus();
        input.select();
        input.addEventListener('keydown', keyHandler);
        unitCell.querySelector('.point-unit-input').addEventListener('keydown', keyHandler);
        cell.querySelector('.btn-point-save').addEventListener('click', function () { savePoint(tr); });
        cell.querySelector('.btn-point-cancel').addEventListener('click', function () { cancelEdit(tr); });
    }

    function cancelEdit(tr) {
        var c = coordinators[nCurrentIndex];
        if (!c || !tr) return;
        var p = (c.points || [])[parseInt(tr.dataset.pindex)];
        if (!p) return;

        // cell 元素未被替換，renderPointTable 掛的 dblclick listener 仍有效；只需重掛新按鈕
        var cell = tr.querySelector('.point-name-cell');
        cell.innerHTML = nameCellHtml(p);
        cell.querySelector('.btn-point-edit').addEventListener('click', function () { beginEdit(tr); });
        tr.querySelector('.point-unit-cell').innerHTML = escapeHtml(p.unit);
    }

    function savePoint(tr) {
        var c = coordinators[nCurrentIndex];
        if (!c || !tr) return;
        var p = (c.points || [])[parseInt(tr.dataset.pindex)];
        if (!p) return;

        var input = tr.querySelector('.point-name-input');
        var unitInput = tr.querySelector('.point-unit-input');
        var newName = (input.value || '').trim();
        var newUnit = (unitInput.value || '').trim();
        if (!newName) {
            alert(t('dbcoord.point.name_required'));
            input.focus();
            return;
        }
        if (newName === p.name && newUnit === p.unit) { cancelEdit(tr); return; }

        var saveBtn = tr.querySelector('.btn-point-save');
        var cancelBtn = tr.querySelector('.btn-point-cancel');
        var setBusy = function (isBusy) {
            input.disabled = isBusy;
            unitInput.disabled = isBusy;
            saveBtn.disabled = isBusy;
            cancelBtn.disabled = isBusy;
            saveBtn.innerHTML = isBusy ? '<i class="fas fa-spinner fa-spin"></i>' : '<i class="fas fa-check"></i>';
        };
        setBusy(true);

        fetch('/DbCoordinator/UpdatePoint', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: c.id, sequence: p.sequence, newName: newName, newUnit: newUnit })
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.success) {
                    p.name = newName;
                    p.unit = newUnit;
                    cancelEdit(tr);
                    flashSaved(tr);
                    if (!data.reloadSent && data.message) alert(data.message);
                } else {
                    alert(data.message || t('dbcoord.msg.failure_default'));
                    setBusy(false);
                    input.focus();
                }
            })
            .catch(function (err) {
                alert(t('dbcoord.msg.call_failed_with', { error: err.message }));
                setBusy(false);
            });
    }

    /// 儲存成功的短暫視覺回饋（列閃綠底）
    function flashSaved(tr) {
        tr.classList.add('point-row-saved');
        setTimeout(function () { tr.classList.remove('point-row-saved'); }, 1500);
    }

    // ── 側欄 ────────────────────────────────────────────────

    function renderSidebar() {
        var container = document.getElementById('dbCoordinatorList');
        if (!container) return;

        if (!coordinators.length) {
            container.innerHTML = '<div class="text-center text-muted py-4">' +
                '<i class="fas fa-inbox fa-2x mb-2 d-block"></i>' +
                escapeHtml(t('dbcoord.empty.no_source')) +
            '</div>';
            var empty = document.getElementById('emptyDetailMsg');
            if (empty) empty.textContent = t('dbcoord.empty.not_loaded');
            return;
        }

        var disabledBadge = '<span class="badge bg-secondary ms-1" style="font-size:.6rem;">' + escapeHtml(t('dbcoord.badge.disabled')) + '</span>';
        var missingBadge = '<span class="badge bg-warning text-dark ms-1 srcxl-missing-badge">' + escapeHtml(t('dbcoord.badge.config_missing')) + '</span>';
        var pendingBadge = '<span class="badge bg-info text-dark ms-1 srcxl-missing-badge">' + escapeHtml(t('dbcoord.badge.pending_engine')) + '</span>';
        var html = coordinators.map(function (c, i) {
            var badge = (c.monitorEnabled ? '' : disabledBadge) + (c.configMissing ? missingBadge : '') + (c.pendingEngine ? pendingBadge : '');
            var szIcon = c.pendingEngine ? 'fa-hourglass-half text-warning' : 'fa-database text-secondary';
            return '<a href="#" class="list-group-item list-group-item-action py-2 coordinator-item" data-index="' + i + '">' +
                '<i class="fas ' + szIcon + ' me-1"></i>' +
                '<span class="small">' + escapeHtml(c.name) + '</span>' +
                badge +
            '</a>';
        }).join('');

        container.innerHTML = html;

        container.querySelectorAll('.coordinator-item').forEach(function (item) {
            item.addEventListener('click', function (e) {
                e.preventDefault();
                clearAllActive();
                this.classList.add('active');
                showDetail(parseInt(this.dataset.index));
            });
        });

        // 預設載入第一筆
        var firstItem = container.querySelector('.coordinator-item');
        if (firstItem) {
            firstItem.classList.add('active');
            showDetail(0);
        }

        // 待 Engine 載入的來源 → 共用 JS 背景輪詢 /DbCoordinator/SourceStatus，Engine Reload 寫完 DB 後自動重新整理
        var aPending = coordinators.filter(function (c) { return c.pendingEngine; }).map(function (c) { return c.name; });
        if (aPending.length && window.SourceExcelImport && window.SourceExcelImport.watchPending) {
            window.SourceExcelImport.watchPending(aPending);
        }
    }

    function refreshPage() {
        window.location.reload();
    }

    function reloadEngine() {
        var btn = document.getElementById('btnReloadEngine');
        if (btn) {
            btn.disabled = true;
            btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>' + escapeHtml(t('dbcoord.btn.notifying'));
        }

        fetch('/DbCoordinator/Reload', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var msg = data.message || (data.success ? t('dbcoord.msg.success_default') : t('dbcoord.msg.failure_default'));
                alert(msg);
                if (data.success) {
                    setTimeout(function () { window.location.reload(); }, 1500);
                }
            })
            .catch(function (err) {
                alert(t('dbcoord.msg.call_failed_with', { error: err.message }));
            })
            .finally(function () {
                if (btn) {
                    btn.disabled = false;
                    btn.innerHTML = '<i class="fas fa-bolt me-1"></i>' + escapeHtml(t('dbcoord.btn.reload_engine'));
                }
            });
    }

    window._dbCoord = {
        refreshPage: refreshPage,
        reloadEngine: reloadEngine
    };

    // 等 i18n 字典載入完再 render，避免 sidebar 空狀態出現 key 名而非翻譯
    if (window.i18n && window.i18n.ready) {
        window.i18n.ready(function () {
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', renderSidebar);
            } else {
                renderSidebar();
            }
        });
    } else {
        document.addEventListener('DOMContentLoaded', renderSidebar);
    }
})();
